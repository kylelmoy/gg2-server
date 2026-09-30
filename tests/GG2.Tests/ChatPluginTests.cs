using GG2.Protocol;
using GG2.Server.Plugins;

namespace GG2.Tests;

/// <summary>
/// The <c>chat</c> server half, against a host that is not a server.
/// </summary>
/// <remarks>
/// <b>These are the tests that can be wrong in an interesting way.</b> The wire
/// plumbing either works or nothing arrives; what is easy to get subtly wrong is
/// the <em>content</em> — which byte says who spoke, whether the server or the
/// client colours a line, who a team message reaches, and what a name with a
/// <c>#</c> in it does to an announcement. A stock client renders whatever it is
/// handed, so none of those has a symptom the server can see.
/// <para>
/// Every expectation cites the GML in
/// <c>chat@bad8081d64f0b808a2bb3a6d88978fb5.zip</c> it comes from.
/// </para>
/// </remarks>
public sealed class ChatPluginTests
{
    /// <summary>
    /// <c>plugin.gml:595-618</c>'s orange. Spelled here because every assertion
    /// about a line the SERVER composed has to include it — see
    /// <see cref="TheConfiguredJoinLineGoesToTheJoinerAlone"/>.
    /// </summary>
    private const string ColourOrange = "O";

    private const byte Hello = 0;
    private const byte GlobalChat = 1;
    private const byte TeamChat = 2;

    private const byte SenderPlayerInfo = 254;

    /// <summary>A host, a plugin bound to packet id 0, and slot 0 already added.</summary>
    internal static (FakePluginHost Host, ChatPlugin Plugin) Fresh()
    {
        var host = new FakePluginHost();
        var plugin = new ChatPlugin();
        plugin.Attach(new PluginChannel(host, 0));

        // Slot 0 is the server's own reserved player: never a client, never in
        // chat, and it exists before anybody connects.
        host.Add("Server", Team.Spectator, address: string.Empty, client: false);
        plugin.PlayerAdded(0);

        return (host, plugin);
    }

    /// <summary>Joins a player and puts them in chat, which is the ordinary case.</summary>
    internal static int Join(
        FakePluginHost host,
        ChatPlugin plugin,
        string name,
        Team team,
        string address = "10.0.0.1",
        bool chat = true,
        bool client = true)
    {
        var slot = host.Add(name, team, address, client);
        plugin.PlayerAdded(slot);

        // Adopts the name silently, as the reference's first Begin Step does.
        plugin.Tick();

        if (chat)
            plugin.Receive(slot, [Hello]);

        return slot;
    }

    /// <summary>The client's send (<c>chat_api.gml:32-38</c>): a kind, a length, the bytes.</summary>
    internal static byte[] Body(byte kind, string message)
    {
        var text = GG2Text.Encode(message);
        var body = new byte[2 + text.Length];
        body[0] = kind;
        body[1] = (byte)text.Length;
        text.CopyTo(body, 2);
        return body;
    }

    private static void SayGlobal(ChatPlugin plugin, int slot, string message) =>
        plugin.Receive(slot, Body(GlobalChat, message));

    private static void SayTeam(ChatPlugin plugin, int slot, string message) =>
        plugin.Receive(slot, Body(TeamChat, message));

    /// <summary>
    /// One decoded server-to-client line, or null when the packet is a vote
    /// packet rather than a line (<c>plugin.gml:525-592</c>).
    /// </summary>
    internal static (int Sender, int Audience, string Text)? AsLine(byte[] packet)
    {
        if (packet[0] < 200)
            return (packet[0], packet[1], GG2Text.Decode(packet.AsSpan(3, packet[2])));

        if (packet[0] is 200 or 201 or 203 or 204)
            return null;

        return (packet[0], 0, GG2Text.Decode(packet.AsSpan(2, packet[1])));
    }

    /// <summary>Every line one slot was sent, in order.</summary>
    internal static List<(int Sender, int Audience, string Text)> LinesTo(FakePluginHost host, int slot)
    {
        var lines = new List<(int, int, string)>();

        foreach (var (target, data) in host.Sent)
        {
            if (target != slot)
                continue;

            if (AsLine(data) is { } line)
                lines.Add(line);
        }

        return lines;
    }

    /// <summary>Just the text of those lines, which is what most assertions want.</summary>
    internal static List<string> TextTo(FakePluginHost host, int slot) =>
        LinesTo(host, slot).ConvertAll(line => line.Text);

    // --- hello ---------------------------------------------------------------

    /// <summary>
    /// <c>plugin.gml:446-453</c>: the hello marks the sender as a chatter and
    /// announces them — to themselves included, because <c>hasChat</c> is set
    /// before the announcement goes out.
    /// </summary>
    [Fact]
    public void HelloAnnouncesTheJoinerToEveryChatterIncludingThemselves()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red);
        var bob = Join(host, plugin, "bob", Team.Blue);

        Assert.Equal(["Oalice has joined chat!", "Obob has joined chat!"], TextTo(host, alice));
        Assert.Equal(["Obob has joined chat!"], TextTo(host, bob));
    }

    /// <summary>
    /// An announcement goes out as 254, which a client with "Display player
    /// info" off drops (<c>chat_api.gml:50-52</c>).
    /// </summary>
    [Fact]
    public void AJoinAnnouncementIsSuppressiblePlayerInfo()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red);

        Assert.Equal(SenderPlayerInfo, LinesTo(host, alice)[0].Sender);
    }

    /// <summary>
    /// A player who never said hello never receives, which is what makes it safe
    /// to advertise the plugin as optional.
    /// </summary>
    [Fact]
    public void APlayerWithoutTheHelloReceivesNothing()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red);
        var silent = Join(host, plugin, "silent", Team.Blue, chat: false);

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

        Assert.Single(TextTo(host, alice));
    }

    // --- relaying ------------------------------------------------------------

    /// <summary>
    /// <b>The server sends the raw text and the speaker's roster index.</b>
    /// <c>chat_api.gml:8-14</c> writes the sender and the message and nothing
    /// else; the client's <c>doColorCode</c> (<c>chat_api.gml:96-123</c>) adds
    /// the name and picks the colour from the speaker's team. A server that
    /// formatted the line here would draw the name twice.
    /// </summary>
    [Fact]
    public void GlobalChatCarriesTheRawTextAndTheSpeakersSlot()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red);
        var bob = Join(host, plugin, "bob", Team.Blue);

        host.Sent.Clear();
        SayGlobal(plugin, alice, "hello everyone");

        Assert.Equal([(alice, 0, "hello everyone")], LinesTo(host, alice));
        Assert.Equal([(alice, 0, "hello everyone")], LinesTo(host, bob));
    }

    /// <summary>
    /// <c>plugin.gml:495</c>: team chat's audience is <c>1 + team</c>, and
    /// <c>chat_api.gml:22</c> routes it to players whose team is
    /// <c>audience - 1</c>. Red is 0, so red's team chat is audience 1.
    /// </summary>
    [Fact]
    public void TeamChatReachesOnlyThatTeam()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red);
        var carol = Join(host, plugin, "carol", Team.Red);
        var bob = Join(host, plugin, "bob", Team.Blue);

        host.Sent.Clear();
        SayTeam(plugin, alice, "flank left");

        Assert.Equal([(alice, 1, "flank left")], LinesTo(host, alice));
        Assert.Equal([(alice, 1, "flank left")], LinesTo(host, carol));
        Assert.Empty(LinesTo(host, bob));
    }

    /// <summary><c>plugin.gml:494</c>: a spectator has no team to talk to.</summary>
    [Fact]
    public void ASpectatorCannotTeamChat()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Spectator);
        var bob = Join(host, plugin, "bob", Team.Blue);

        host.Sent.Clear();
        SayTeam(plugin, alice, "anybody there");

        Assert.Empty(host.Sent);
    }

    /// <summary>
    /// <b>A command typed into team chat is relayed as text, not run.</b>
    /// <c>plugin.gml:493-505</c> calls <c>sendChatMessage</c> directly, never
    /// <c>executeChat</c>, so only global chat has a console. Transcribed: a
    /// player who sees their own <c>/votekick</c> appear as a team message has
    /// learnt something true about the server.
    /// </summary>
    [Fact]
    public void ACommandInTeamChatIsRelayedRatherThanRun()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red);

        host.Sent.Clear();
        SayTeam(plugin, alice, "/votescramble");

        Assert.Equal([(alice, 1, "/votescramble")], LinesTo(host, alice));
    }

    /// <summary>
    /// A message that exactly fills the length prefix arrives whole. The
    /// SEND side's chunking loop (<c>chat_api.gml:7-28</c>) is exercised by
    /// <see cref="ChatCommandTests.ALongMapListArrivesAsSeveralPackets"/>,
    /// because no client can put more than 255 bytes into one packet.
    /// </summary>
    [Fact]
    public void AFullLengthMessageArrivesWhole()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red);

        var message = new string('a', 255);

        host.Sent.Clear();
        SayGlobal(plugin, alice, message);

        Assert.Equal([message], TextTo(host, alice));
    }

    /// <summary>
    /// <c>chat_api.gml:7</c> is <c>while(msg != "")</c>, so an empty line is
    /// never sent at all.
    /// </summary>
    [Fact]
    public void AnEmptyMessageSendsNothing()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red);

        host.Sent.Clear();
        SayGlobal(plugin, alice, string.Empty);

        Assert.Empty(host.Sent);
    }

    // --- joining, leaving, renaming ------------------------------------------

    /// <summary>
    /// <c>plugin.gml:186-195</c>, and it runs <b>before</b> the roster removal
    /// so the name is still there to put in the line.
    /// </summary>
    [Fact]
    public void LeavingIsAnnouncedToEverybodyInChat()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red);
        var bob = Join(host, plugin, "bob", Team.Blue);

        host.Sent.Clear();
        plugin.PlayerRemoving(bob);
        host.RemoveAt(bob);

        Assert.Equal(["Obob has left the game."], TextTo(host, alice));
    }

    /// <summary>A player who never joined chat leaves without a word.</summary>
    [Fact]
    public void APlayerWhoWasNeverInChatLeavesSilently()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red);
        var silent = Join(host, plugin, "silent", Team.Blue, chat: false);

        host.Sent.Clear();
        plugin.PlayerRemoving(silent);
        host.RemoveAt(silent);

        Assert.Empty(host.Sent);
    }

    /// <summary>
    /// <c>plugin.gml:158-181</c>: a rename is announced, but the first sight of
    /// a name is not — <c>nameBkp</c> starts empty and the reference exits after
    /// adopting it.
    /// </summary>
    [Fact]
    public void ARenameIsAnnouncedAndAJoinIsNot()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red);

        host.Sent.Clear();
        plugin.Tick();
        Assert.Empty(host.Sent);

        host.Rename(alice, "alicia");
        host.Renames.Clear();
        plugin.Tick();

        Assert.Equal(["Oalice has changed their name to alicia."], TextTo(host, alice));
    }

    /// <summary>
    /// <c>plugin.gml:159-165</c>: an empty name becomes "Nameless" and goes out
    /// as a real <c>PLAYER_CHANGENAME</c>. <b>This is the one thing the plugin
    /// does that a client without it still sees.</b>
    /// </summary>
    [Fact]
    public void AnEmptyNameBecomesNamelessForEverybody()
    {
        var (host, plugin) = Fresh();
        var slot = host.Add(string.Empty, Team.Red);
        plugin.PlayerAdded(slot);

        plugin.Tick();

        Assert.Equal([(slot, "Nameless")], host.Renames);
    }

    /// <summary>
    /// <b>There is no name de-duplication in this plugin.</b> That is
    /// <see cref="ChatV2Plugin"/>'s, and a reader who goes looking for it here
    /// should find this test rather than conclude the port dropped it.
    /// </summary>
    [Fact]
    public void TwoPlayersMayShareAName()
    {
        var (host, plugin) = Fresh();
        Join(host, plugin, "alice", Team.Red);
        Join(host, plugin, "alice", Team.Blue, address: "10.0.0.2");

        plugin.Tick();

        Assert.Empty(host.Renames);
    }

    /// <summary>
    /// <c>sanitiseNewlines</c> on every name the SERVER puts in a line it
    /// composes: GM8 draws a bare <c>#</c> as a line break, so a player called
    /// <c>"a#b"</c> could otherwise forge a second line of a server
    /// announcement.
    /// </summary>
    [Fact]
    public void ANameWithAHashIsEscapedInAServerComposedLine()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "a#b", Team.Red);

        Assert.Equal(["Oa\\#b has joined chat!"], TextTo(host, alice));
    }

    /// <summary>
    /// ⛔ And <b>not</b> on what a player typed: the client sanitises that at
    /// draw time (<c>chat_api.gml:99</c>), so doing it here too would double the
    /// backslashes.
    /// </summary>
    [Fact]
    public void APlayersOwnMessageIsRelayedExactly()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red);

        host.Sent.Clear();
        SayGlobal(plugin, alice, "look at #1");

        Assert.Equal(["look at #1"], TextTo(host, alice));
    }

    /// <summary>
    /// A line whose first byte is <c>chr(13)</c> is the client's <c>/me</c>
    /// (<c>commands.gml:65-77</c>). The server relays it like any other message
    /// and the client renders it in the third person, so nothing here special
    /// cases it — this test is what says that is deliberate.
    /// </summary>
    [Fact]
    public void TheMeFormIsRelayedLikeAnyOtherMessage()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red);

        host.Sent.Clear();
        SayGlobal(plugin, alice, "\rwaves");

        Assert.Equal([(alice, 0, "\rwaves")], LinesTo(host, alice));
    }

    // --- roster compaction ---------------------------------------------------

    /// <summary>
    /// <b>A slot index is a roster index and removal compacts.</b> Break
    /// <see cref="PluginPlayerTable{T}"/>'s <c>RemoveAt</c> and this is what goes
    /// red: bob's chat state would land on carol.
    /// </summary>
    [Fact]
    public void ChatStateFollowsThePlayerWhenTheRosterCompacts()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red);
        var bob = Join(host, plugin, "bob", Team.Blue, address: "10.0.0.2");
        var carol = Join(host, plugin, "carol", Team.Red, address: "10.0.0.3", chat: false);

        plugin.PlayerRemoving(bob);
        host.RemoveAt(bob);

        // carol is now in bob's old slot and is still not in chat.
        host.Sent.Clear();
        SayGlobal(plugin, alice, "still here");

        Assert.Equal([alice], host.Sent.ConvertAll(s => s.Slot));
    }

    // --- the configured join line --------------------------------------------

    /// <summary>
    /// <c>[Server] ChatJoinMessage</c> reaches a player when they announce chat,
    /// and nobody else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>On the hello, not on the roster entry.</b> Before the hello there is no
    /// chat window on the other end to draw it into, and this port's own
    /// <c>PlayerAdded</c> runs at the join — several ticks earlier, and for a
    /// client that may have no chat plugin at all.
    /// </para>
    /// <para>
    /// <b>As a 255, not a 254.</b> A client with "Display player info" off drops
    /// every 254, and this is the line that tells a player what the server's
    /// commands are.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheConfiguredJoinLineGoesToTheJoinerAlone()
    {
        var (host, plugin) = Fresh();
        host.ChatJoinMessage = "Bots in the way? Type /bots off";

        var alice = Join(host, plugin, "alice", Team.Red);
        host.Sent.Clear();

        var bob = Join(host, plugin, "bob", Team.Blue, address: "10.0.0.2");

        // ⛔ THE LEADING COLOUR CODE IS LOAD BEARING, and asserting on the
        // message alone is what let this ship broken. plugin.gml's draw loop ends
        // `msg = string_copy(msg, 2, 255)`: character 1 of every line is eaten as
        // a colour code whether or not one is there, so a line sent without one
        // loses its first letter. Measured against a stock GM8 client, which
        // drew "Welcome!..." as "elcome!...".
        var toBob = LinesTo(host, bob);

        Assert.Contains(
            toBob,
            line => line.Sender == 255 && line.Text == ColourOrange + host.ChatJoinMessage);

        Assert.DoesNotContain(
            TextTo(host, alice),
            text => text.Contains(host.ChatJoinMessage, StringComparison.Ordinal));
    }

    /// <summary>An empty join line sends nothing at all.</summary>
    [Fact]
    public void NoJoinLineIsConfiguredAndNothingExtraIsSent()
    {
        var (host, plugin) = Fresh();

        var alice = Join(host, plugin, "alice", Team.Red);

        // The join announcement and nothing else.
        Assert.Single(LinesTo(host, alice));
    }

    // --- the operator's line, and the panel's history --------------------------

    /// <summary>
    /// The panel's chat box speaks as slot 0 exactly as a player speaks: the raw
    /// text under index 0, so the client names and colours it from its own
    /// roster entry for the reserved spectator. A leading slash is text, not a
    /// command.
    /// </summary>
    [Fact]
    public void TheServerSpeaksAsSlotZeroToEverybodyInChat()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red);
        var bob = Join(host, plugin, "bob", Team.Blue);
        var silent = Join(host, plugin, "silent", Team.Red, chat: false);

        host.Sent.Clear();
        plugin.SayAsServer("/votekick bob");

        Assert.Equal([(0, 0, "/votekick bob")], LinesTo(host, alice));
        Assert.Equal([(0, 0, "/votekick bob")], LinesTo(host, bob));
        Assert.Empty(LinesTo(host, silent));
        Assert.Equal([(0, "/votekick bob", false)], host.Heard);
    }

    /// <summary>
    /// What the panel's chat window is fed: what players said, team chat marked,
    /// and never a command, which is acted on rather than said.
    /// </summary>
    [Fact]
    public void PlayerLinesAreHeardAndCommandsAreNot()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red);

        SayGlobal(plugin, alice, "hello");
        SayTeam(plugin, alice, "flank left");
        SayGlobal(plugin, alice, "/maplist");

        Assert.Equal([(alice, "hello", false), (alice, "flank left", true)], host.Heard);
    }
}
