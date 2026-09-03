using Gg2.Protocol;
using Gg2.Server.Plugins;

namespace Gg2.Tests;

/// <summary>
/// The <c>chat_v2_pr</c> server half, against a host that is not a server.
/// </summary>
/// <remarks>
/// ⚠️ <b>These are the tests that can actually be wrong in an interesting
/// way.</b> The wire plumbing either works or nothing arrives; what is easy to
/// get subtly wrong is the <em>content</em> — the colour prefix, the <c>#</c>
/// that separates the name from the message, who a team message reaches, and
/// when the spam counter resets. A stock client renders whatever it is handed,
/// so none of those has a symptom the server can see.
/// <para>
/// Every expectation below cites the <c>event_user</c> handler in
/// <c>plugin.gml</c> it comes from.
/// </para>
/// </remarks>
public sealed class ChatV2PluginTests
{
    private const byte Hello = 0;
    private const byte GlobalChat = 1;
    private const byte TeamChat = 2;

    /// <summary>A host with a roster and nothing else.</summary>
    private sealed class FakeHost : IPluginHost
    {
        private sealed record Player(string Name, Team Team, string Address, bool Client)
        {
            public string Name { get; set; } = Name;
        }

        private readonly List<Player> _players = [];

        /// <summary>Every <c>PluginPacketSendTo</c>, in order.</summary>
        public List<(int Slot, byte[] Data)> Sent { get; } = [];

        /// <summary>Every <c>PluginPacketSend</c>, in order.</summary>
        public List<byte[]> Broadcast { get; } = [];

        public List<string> Logged { get; } = [];

        public List<(int Slot, string Name)> Renames { get; } = [];

        public long CurrentTimeMilliseconds { get; set; }

        public int PlayerCount => _players.Count;

        public int Add(string name, Team team, string address = "10.0.0.1", bool client = true)
        {
            _players.Add(new Player(name, team, address, client));
            return _players.Count - 1;
        }

        public void RemoveAt(int slot) => _players.RemoveAt(slot);

        public string NameOf(int slot) => _players[slot].Name;

        public Team TeamOf(int slot) => _players[slot].Team;

        public string RemoteAddressOf(int slot) => _players[slot].Client ? _players[slot].Address : string.Empty;

        public bool IsClient(int slot) => _players[slot].Client;

        public void Rename(int slot, string name)
        {
            _players[slot].Name = name;
            Renames.Add((slot, name));
        }

        public void SendPluginPacketToAll(int packetId, ReadOnlySpan<byte> data) =>
            Broadcast.Add(data.ToArray());

        public void SendPluginPacketTo(int slot, int packetId, ReadOnlySpan<byte> data) =>
            Sent.Add((slot, data.ToArray()));

        public void Log(string message) => Logged.Add(message);

        /// <summary>The messages sent to one slot, decoded from their length prefixes.</summary>
        public List<string> MessagesTo(int slot)
        {
            var messages = new List<string>();
            foreach (var (target, data) in Sent)
                if (target == slot)
                    messages.Add(Gg2Text.Decode(data.AsSpan(1, data[0])));

            return messages;
        }
    }

    /// <summary>A host, a plugin bound to packet id 0, and slot 0 already added.</summary>
    private static (FakeHost Host, ChatV2Plugin Plugin) Fresh()
    {
        var host = new FakeHost();
        var plugin = new ChatV2Plugin();
        plugin.Attach(new PluginChannel(host, 0));

        // Slot 0 is the server's own reserved player: never a client, never in
        // chat, and it exists before anybody connects.
        host.Add("Server", Team.Spectator, address: string.Empty, client: false);
        plugin.PlayerAdded(0);

        return (host, plugin);
    }

    private static int Join(FakeHost host, ChatV2Plugin plugin, string name, Team team, string address = "10.0.0.1")
    {
        var slot = host.Add(name, team, address);
        plugin.PlayerAdded(slot);
        plugin.Tick();   // adopts the name silently, as the reference's first Begin Step does
        return slot;
    }

    private static void SayGlobal(ChatV2Plugin plugin, int slot, string message) =>
        plugin.Receive(slot, Body(GlobalChat, message));

    private static void SayTeam(ChatV2Plugin plugin, int slot, string message) =>
        plugin.Receive(slot, Body(TeamChat, message));

    /// <summary>The client's <c>event3</c>: a kind byte, a length, the bytes.</summary>
    private static byte[] Body(byte kind, string message)
    {
        var text = Gg2Text.Encode(message);
        var body = new byte[2 + text.Length];
        body[0] = kind;
        body[1] = (byte)text.Length;
        text.CopyTo(body, 2);
        return body;
    }

    // --- hello ---------------------------------------------------------------

    /// <summary>
    /// <c>event0</c> case 0: the hello marks the sender as a chatter and
    /// announces them — to themselves included, because <c>hasChat</c> is set
    /// before the announcement goes out.
    /// </summary>
    [Fact]
    public void HelloAnnouncesTheJoinerToEveryChatterIncludingThemselves()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red);
        var bob = Join(host, plugin, "bob", Team.Blue);

        plugin.Receive(alice, [Hello]);
        plugin.Receive(bob, [Hello]);

        Assert.Equal(["Oalice has joined chat!", "Obob has joined chat!"], host.MessagesTo(alice));
        Assert.Equal(["Obob has joined chat!"], host.MessagesTo(bob));
    }

    /// <summary>
    /// ⚠️ A player who never said hello never receives, which is what makes it
    /// safe to advertise the plugin as optional.
    /// </summary>
    [Fact]
    public void APlayerWithoutTheHelloReceivesNothing()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red);
        var silent = Join(host, plugin, "silent", Team.Blue);

        plugin.Receive(alice, [Hello]);
        SayGlobal(plugin, alice, "hi");

        Assert.Empty(host.MessagesTo(silent));
    }

    /// <summary>A second hello is ignored rather than announced twice.</summary>
    [Fact]
    public void HelloIsIdempotent()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red);

        plugin.Receive(alice, [Hello]);
        plugin.Receive(alice, [Hello]);

        Assert.Single(host.MessagesTo(alice));
    }

    // --- formatting ----------------------------------------------------------

    /// <summary>
    /// <c>event1</c>: <c>colour + name + ": #W" + message</c> for global chat.
    /// </summary>
    /// <remarks>
    /// ⚠️ The <c>#</c> is not decoration. The client half uses it to find where
    /// the colour changes when it wraps a long line (<c>event4</c>), so dropping
    /// it makes every wrapped message continue in the speaker's team colour.
    /// </remarks>
    [Theory]
    [InlineData(Team.Red, "R")]
    [InlineData(Team.Blue, "B")]
    [InlineData(Team.Spectator, "W")]
    public void GlobalChatIsPrefixedWithTheSpeakersTeamColour(Team team, string colour)
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", team);
        plugin.Receive(alice, [Hello]);

        SayGlobal(plugin, alice, "hello world");

        Assert.Equal($"{colour}alice: #Whello world", host.MessagesTo(alice)[^1]);
    }

    /// <summary>
    /// <c>event1</c>'s team branch has no <c>#</c> and no second colour code.
    /// </summary>
    [Fact]
    public void TeamChatIsFormattedWithoutTheColourBreak()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red);
        plugin.Receive(alice, [Hello]);

        SayTeam(plugin, alice, "on my way");

        Assert.Equal("Ralice: on my way", host.MessagesTo(alice)[^1]);
    }

    /// <summary>
    /// ⚠️ <c>#</c> in what a player typed becomes a space, because it is the
    /// client half's own colour marker.
    /// </summary>
    [Fact]
    public void HashesInAMessageAreReplacedWithSpaces()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red);
        plugin.Receive(alice, [Hello]);

        SayGlobal(plugin, alice, "a#b#c");

        Assert.Equal("Ralice: #Wa b c", host.MessagesTo(alice)[^1]);
    }

    // --- routing -------------------------------------------------------------

    /// <summary>
    /// <c>event2</c>: a team message reaches that team's chatters and nobody
    /// else.
    /// </summary>
    /// <remarks>
    /// ⛔ The one routing rule with a real consequence. A client cannot be
    /// trusted to hide what it has been sent, so team chat leaking here is team
    /// chat the other team reads.
    /// </remarks>
    [Fact]
    public void TeamChatReachesOnlyThatTeam()
    {
        var (host, plugin) = Fresh();
        var red = Join(host, plugin, "red", Team.Red);
        var alsoRed = Join(host, plugin, "alsored", Team.Red);
        var blue = Join(host, plugin, "blue", Team.Blue);
        var spectator = Join(host, plugin, "spec", Team.Spectator);

        foreach (var slot in new[] { red, alsoRed, blue, spectator })
            plugin.Receive(slot, [Hello]);

        SayTeam(plugin, red, "push left");

        Assert.Contains("Rred: push left", host.MessagesTo(red));
        Assert.Contains("Rred: push left", host.MessagesTo(alsoRed));
        Assert.DoesNotContain("Rred: push left", host.MessagesTo(blue));
        Assert.DoesNotContain("Rred: push left", host.MessagesTo(spectator));
    }

    /// <summary>
    /// <c>event0</c> case 2 refuses team chat from a spectator: there is no team
    /// to route it to.
    /// </summary>
    [Fact]
    public void ASpectatorCannotSendTeamChat()
    {
        var (host, plugin) = Fresh();
        var spectator = Join(host, plugin, "spec", Team.Spectator);
        var red = Join(host, plugin, "red", Team.Red);

        plugin.Receive(spectator, [Hello]);
        plugin.Receive(red, [Hello]);

        var before = host.Sent.Count;
        SayTeam(plugin, spectator, "anyone?");

        Assert.Equal(before, host.Sent.Count);
    }

    /// <summary>
    /// ⚠️ Player by player, never a broadcast. A broadcast would be cheaper and
    /// would send team chat to everybody.
    /// </summary>
    [Fact]
    public void NothingIsEverBroadcast()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red);

        plugin.Receive(alice, [Hello]);
        SayGlobal(plugin, alice, "hi");
        SayTeam(plugin, alice, "hi");

        Assert.Empty(host.Broadcast);
    }

    // --- spam ----------------------------------------------------------------

    /// <summary>
    /// <c>event7</c>: the sixth message inside the two-second window mutes the
    /// sender, and the message that trips it is still delivered.
    /// </summary>
    [Fact]
    public void SixMessagesInsideTheWindowMuteTheSender()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red);
        var bob = Join(host, plugin, "bob", Team.Blue);

        plugin.Receive(alice, [Hello]);
        plugin.Receive(bob, [Hello]);

        for (var i = 0; i < 6; i++)
        {
            host.CurrentTimeMilliseconds += 100;
            SayGlobal(plugin, alice, $"spam {i}");
        }

        var seen = host.MessagesTo(bob);
        Assert.Contains("Ralice: #Wspam 5", seen);
        Assert.Contains("Oalice has been muted due to spamming.", seen);

        // Muted means hasChat is false: nothing further is sent OR received.
        var after = host.Sent.Count;
        SayGlobal(plugin, alice, "still here?");
        Assert.Equal(after, host.Sent.Count);
    }

    /// <summary>
    /// ⚠️ The window restarts from the <em>last</em> message, not the first, so
    /// a steady talker never resets it. Transcribed rather than smoothed.
    /// </summary>
    [Fact]
    public void AGapLongerThanTheWindowResetsTheCount()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red);
        plugin.Receive(alice, [Hello]);

        for (var i = 0; i < 20; i++)
        {
            host.CurrentTimeMilliseconds += 2001;
            SayGlobal(plugin, alice, $"line {i}");
        }

        Assert.DoesNotContain(
            host.MessagesTo(alice),
            m => m.Contains("muted", StringComparison.Ordinal));
    }

    /// <summary>
    /// The mute is keyed on the address, so reconnecting and saying hello again
    /// is refused.
    /// </summary>
    [Fact]
    public void AMutedAddressCannotRejoinChat()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red, address: "10.0.0.7");

        plugin.Receive(alice, [Hello]);
        for (var i = 0; i < 6; i++)
        {
            host.CurrentTimeMilliseconds += 100;
            SayGlobal(plugin, alice, "spam");
        }

        // Reconnect: a new slot, the same address.
        plugin.PlayerRemoving(alice);
        host.RemoveAt(alice);

        var again = Join(host, plugin, "alice", Team.Red, address: "10.0.0.7");
        var before = host.Sent.Count;
        plugin.Receive(again, [Hello]);

        Assert.Equal(before, host.Sent.Count);
    }

    // --- names ---------------------------------------------------------------

    /// <summary>
    /// The <c>Player</c> Begin Step host branch: a second "alice" becomes
    /// "alice2", for every client whether or not it has the plugin.
    /// </summary>
    /// <remarks>
    /// ⚠️ The counter is the number of matching players, not the next free
    /// number — so the third is "alice3" and, if "alice2" leaves, the next is
    /// "alice2" again.
    /// </remarks>
    [Fact]
    public void ADuplicateNameGetsACounterAppended()
    {
        var (host, plugin) = Fresh();
        Join(host, plugin, "alice", Team.Red);

        var second = host.Add("alice", Team.Blue);
        plugin.PlayerAdded(second);
        plugin.Tick();

        Assert.Equal([(second, "alice2")], host.Renames);
        Assert.Equal("alice2", host.NameOf(second));
    }

    /// <summary>
    /// ⚠️ Compared on the first 18 bytes, case-insensitively, and truncated to
    /// those 18 before the counter goes on.
    /// </summary>
    [Fact]
    public void NamesAreComparedOnTheirFirstEighteenBytesCaseInsensitively()
    {
        var (host, plugin) = Fresh();
        Join(host, plugin, "aaaaaaaaaaaaaaaaaaXX", Team.Red);

        var second = host.Add("AAAAAAAAAAAAAAAAAAyy", Team.Blue);
        plugin.PlayerAdded(second);
        plugin.Tick();

        Assert.Equal("AAAAAAAAAAAAAAAAAA2", host.NameOf(second));
    }

    /// <summary>A unique name is left alone and nothing is broadcast.</summary>
    [Fact]
    public void AUniqueNameIsNotRenamed()
    {
        var (host, plugin) = Fresh();
        Join(host, plugin, "alice", Team.Red);
        Join(host, plugin, "bob", Team.Blue);

        Assert.Empty(host.Renames);
    }

    /// <summary>
    /// A rename by a player in chat is announced; the join that first sets
    /// <c>nameBkp</c> is not.
    /// </summary>
    [Fact]
    public void ARenameIsAnnouncedButAJoinIsNot()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red);
        plugin.Receive(alice, [Hello]);

        var afterHello = host.MessagesTo(alice).Count;

        host.Rename(alice, "alicia");
        host.Renames.Clear();
        plugin.Tick();

        Assert.Equal(
            "Oalice has changed their name to alicia.",
            host.MessagesTo(alice)[afterHello]);
    }

    // --- the compaction trap -------------------------------------------------

    /// <summary>
    /// ⛔ A departure shifts every slot above it down, and the plugin's state has
    /// to move with it.
    /// </summary>
    /// <remarks>
    /// Without <see cref="PluginPlayerTable{T}"/> this test is what fails: bob's
    /// "in chat" flag stays at slot 2 while bob becomes slot 1, so the next
    /// message is delivered to whoever is at 2 — or to nobody. The symptom
    /// arrives seconds after an unrelated disconnect, which is exactly why it is
    /// gated rather than reasoned about.
    /// </remarks>
    [Fact]
    public void PerPlayerStateFollowsTheRosterDownAfterADeparture()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red);
        var bob = Join(host, plugin, "bob", Team.Blue);

        plugin.Receive(alice, [Hello]);
        plugin.Receive(bob, [Hello]);

        // Alice leaves. Bob shifts from slot 2 to slot 1.
        plugin.PlayerRemoving(alice);
        host.RemoveAt(alice);
        Assert.Equal("bob", host.NameOf(1));

        host.Sent.Clear();
        SayGlobal(plugin, 1, "still here");

        Assert.Equal(["Bbob: #Wstill here"], host.MessagesTo(1));
    }

    // --- malformed input -----------------------------------------------------

    /// <summary>
    /// ⚠️ A truncated packet is refused, not read past. The reference gets
    /// zeroes from GM8 for an over-read; a span would throw on the tick thread.
    /// </summary>
    [Fact]
    public void ATruncatedPacketIsSurvived()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red);
        plugin.Receive(alice, [Hello]);
        host.Sent.Clear();

        plugin.Receive(alice, []);                       // no kind byte
        plugin.Receive(alice, [GlobalChat]);             // no length
        plugin.Receive(alice, [GlobalChat, 200, 0x41]);  // length lies

        Assert.Equal(2, host.Sent.Count);                // the two that carry a body
        Assert.Equal("Ralice: #W", host.MessagesTo(alice)[0]);
        Assert.Equal("Ralice: #WA", host.MessagesTo(alice)[1]);
    }

    /// <summary>An unknown kind byte is ignored, as the reference's switch is.</summary>
    [Fact]
    public void AnUnknownKindByteIsIgnored()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red);
        plugin.Receive(alice, [Hello]);
        host.Sent.Clear();

        plugin.Receive(alice, [99, 1, 0x41]);

        Assert.Empty(host.Sent);
    }

    /// <summary>
    /// The length prefix is a byte, so a line that would exceed 255 is cut.
    /// </summary>
    [Fact]
    public void ALineLongerThanTwoHundredAndFiftyFiveBytesIsTruncated()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red);
        plugin.Receive(alice, [Hello]);

        SayGlobal(plugin, alice, new string('x', 255));

        var line = host.MessagesTo(alice)[^1];
        Assert.Equal(255, line.Length);
        Assert.StartsWith("Ralice: #W", line, StringComparison.Ordinal);
    }
}
