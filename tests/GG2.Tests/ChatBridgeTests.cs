using GG2.Protocol;
using GG2.Server.Plugins;

namespace GG2.Tests;

/// <summary>
/// The chat core on <c>chat</c>'s bridged wire: stock v6 clients and chat2026
/// clients (behind <see cref="ChatPlugin.Envelope"/>) in one room, and players
/// who never say hello.
/// </summary>
/// <remarks>
/// <c>Chat2026PluginTests</c> holds the core's behaviour to the chat2026 GML's
/// strings on the plain wire. These hold each translation: what a v6 client is
/// sent for each thing the core does, and that the two formats meet in one room,
/// one tally and one mute list. The v6 byte shapes are from <c>plugin.gml</c> in
/// <c>chat@bad8081d64f0b808a2bb3a6d88978fb5.zip</c>, the client's user event 1.
/// </remarks>
public sealed class ChatBridgeTests
{
    private const byte E = ChatPlugin.Envelope;

    private static (FakePluginHost Host, ChatPlugin Plugin) Fresh(
        ChatWire wire = ChatWire.Bridged, ChatSettings? settings = null, FakePluginHost? host = null)
    {
        host ??= new FakePluginHost();
        var plugin = new ChatPlugin(wire, settings);
        plugin.Attach(new PluginChannel(host, 0));

        host.Add("Server", Team.Spectator, address: string.Empty, client: false);
        plugin.PlayerAdded(0);
        plugin.MapChanged("ctf_truefort");
        plugin.Tick();
        return (host, plugin);
    }

    private static int Arrive(FakePluginHost host, ChatPlugin plugin, string name, Team team, string address)
    {
        var slot = host.Add(name, team, address);
        plugin.PlayerAdded(slot);
        plugin.Tick();
        return slot;
    }

    private static int JoinV6(FakePluginHost host, ChatPlugin plugin, string name, Team team, string? address = null)
    {
        var slot = Arrive(host, plugin, name, team, address ?? "10.0.0." + (host.PlayerCount + 1));
        plugin.Receive(slot, [0]);
        return slot;
    }

    private static int JoinChat2026(FakePluginHost host, ChatPlugin plugin, string name, Team team, string? address = null)
    {
        var slot = Arrive(host, plugin, name, team, address ?? "10.0.0." + (host.PlayerCount + 1));
        plugin.Receive(slot, [E, 0, ChatPlugin.ProtocolVersion, 0]);
        return slot;
    }

    private static void SayV6(ChatPlugin plugin, int slot, string text, bool team = false)
    {
        var bytes = GG2Text.Encode(text);
        plugin.Receive(slot, [team ? (byte)2 : (byte)1, (byte)bytes.Length, .. bytes]);
    }

    private static void SayChat2026(ChatPlugin plugin, int slot, string text, bool team = false)
    {
        var bytes = GG2Text.Encode(text);
        plugin.Receive(slot, [E, 1, team ? (byte)1 : (byte)0, (byte)bytes.Length, .. bytes]);
    }

    private static List<string> To(FakePluginHost host, int slot) =>
        host.Sent.Where(s => s.Slot == slot).Select(s => Show(s.Data)).ToList();

    /// <summary>One frame as a readable line, either format.</summary>
    private static string Show(byte[] data)
    {
        var at = 0;
        string Str()
        {
            var length = data[at++];
            var text = GG2Text.Decode(data.AsSpan(at, length));
            at += length;
            return text;
        }

        List<string> Options()
        {
            var n = data[at++];
            var options = new List<string>();
            for (var i = 0; i < n; i++)
                options.Add(Str());

            return options;
        }

        if (data[0] == E)
        {
            at = 2;
            switch (data[1])
            {
                case 0:
                    at = 6;
                    return $"WELCOME v{data[2]} motd={Str()}";
                case 1:
                    at = 5;
                    return $"LINE #{data[2]} flags={data[3]} team={data[4]} {Str()}: {Str()}";
                case 2:
                    at = 3;
                    return $"NOTICE {data[2]} {Str()}";
                case 3:
                {
                    var id = data[at++];
                    var question = Str();
                    var options = Options();
                    at += 2;
                    return $"VOTE_START {id} {question} [{string.Join("|", options)}] may={data[at]}";
                }
                case 4:
                    return $"VOTE_TALLY {data[2]} {string.Join(",", data.Skip(4).Take(data[3]))}";
                case 5:
                    at = 4;
                    return $"VOTE_END {data[2]} winner={data[3]} {Str()}";
                default:
                    return $"chat2026 kind {data[1]}";
            }
        }

        at = 1;
        switch (data[0])
        {
            case < 200:
                at = 2;
                return $"v6 #{data[0]} audience={data[1]} {Str()}";
            case 200 or 204:
            {
                var question = Str();
                var options = Options();
                at += 2;
                var counts = data.Skip(at).Take(options.Count);
                return $"v6 {data[0]} {question} [{string.Join("|", options)}] {string.Join(",", counts)}";
            }
            case 201:
                return $"v6 201 winner={data[1]}";
            case 203:
                return $"v6 203 +{data[1]}";
            default:
                return $"v6 {data[0]} {Str()}";
        }
    }

    // --- greeting -------------------------------------------------------------

    /// <summary>
    /// gg2-client on iOS joins a server that requires <c>chat</c> and never says
    /// hello. A GM8 client without the plugin dies on any plugin packet, so the
    /// whole room's activity must reach the other two and not one byte of it this one.
    /// </summary>
    [Fact]
    public void APlayerWhoNeverGreetsIsSentNothingWhateverHappens()
    {
        var (host, plugin) = Fresh(settings: new ChatSettings { AdminPassword = "pw" });
        host.ChatJoinMessage = "Welcome";
        var alice = JoinV6(host, plugin, "alice", Team.Red);
        var bob = JoinChat2026(host, plugin, "bob", Team.Blue);
        var carol = Arrive(host, plugin, "carol", Team.Red, "10.0.0.9");
        plugin.Receive(carol, [E, 1, 0, 2, (byte)'h', (byte)'i']);
        plugin.Receive(carol, [1, 2, (byte)'h', (byte)'i']);

        SayV6(plugin, alice, "hello");
        SayChat2026(plugin, bob, "hi", team: true);
        SayV6(plugin, alice, "/help");
        plugin.SayAsServer("from the panel");
        SayChat2026(plugin, bob, "/votescramble");
        plugin.Receive(alice, [3, 1]);
        plugin.Tick();
        host.Rename(alice, "alice2");
        plugin.Tick();

        Assert.Contains(To(host, alice), line => line.Contains("Teams scrambled", StringComparison.Ordinal));
        Assert.Contains(To(host, bob), line => line.StartsWith("VOTE_END", StringComparison.Ordinal));
        Assert.Empty(To(host, carol));
    }

    [Fact]
    public void EachFormatIsGreetedInItsOwnWayAndTheMotdKeepsItsLineBreakOnV6()
    {
        var (host, plugin) = Fresh();
        host.ChatJoinMessage = "Hi#Type /help";
        var alice = JoinV6(host, plugin, "alice", Team.Red);
        var bob = JoinChat2026(host, plugin, "bob", Team.Blue);

        Assert.Equal(["v6 255 OHi#Type /help"], To(host, alice).Take(1));
        Assert.Equal(["WELCOME v1 motd=Hi#Type /help"], To(host, bob));
    }

    /// <summary>
    /// On a real server the hello can arrive before the plugin's first tick for
    /// that player, so a chat2026 client hears its own join. Once.
    /// </summary>
    [Fact]
    public void AHelloBeforeTheFirstTickHearsItsOwnJoinOnce()
    {
        var (host, plugin) = Fresh();
        var bob = host.Add("bob", Team.Spectator, "10.0.0.2");
        plugin.PlayerAdded(bob);
        plugin.Receive(bob, [E, 0, ChatPlugin.ProtocolVersion, 0]);
        plugin.Tick();
        plugin.Tick();

        Assert.Equal(["WELCOME v1 motd=", "NOTICE 1 bob has joined the game."], To(host, bob));
    }

    [Fact]
    public void AHelloRepeatedInTheSameFormatIsIgnored()
    {
        var (host, plugin) = Fresh();
        host.ChatJoinMessage = "Hi";
        var alice = JoinV6(host, plugin, "alice", Team.Red);
        var bob = JoinChat2026(host, plugin, "bob", Team.Blue);
        host.Sent.Clear();

        plugin.Receive(alice, [0]);
        plugin.Receive(bob, [E, 0, ChatPlugin.ProtocolVersion, 0]);

        Assert.Empty(host.Sent);
    }

    /// <summary>
    /// The desktop client sends an enveloped HELLO, waits two seconds, and falls
    /// back to v6. A WELCOME that was merely late must not leave the two ends
    /// disagreeing: the v6 hello switches the player, and from then on nothing
    /// enveloped reaches it.
    /// </summary>
    [Fact]
    public void AHelloInTheOtherFormatSwitchesThePlayer()
    {
        var (host, plugin) = Fresh();
        var alice = JoinV6(host, plugin, "alice", Team.Red);
        var bob = JoinChat2026(host, plugin, "bob", Team.Blue);

        plugin.Receive(bob, [0]);
        host.Sent.Clear();
        SayV6(plugin, alice, "hello");
        SayChat2026(plugin, bob, "ignored now");
        SayV6(plugin, bob, "v6 now");

        Assert.Equal(["v6 #1 audience=0 hello", "v6 #2 audience=0 v6 now"], To(host, bob));
        Assert.DoesNotContain(host.Sent, s => s.Data[0] == E);

        plugin.Receive(bob, [E, 0, ChatPlugin.ProtocolVersion, 0]);
        Assert.Equal("WELCOME v1 motd=", To(host, bob)[^1]);
    }

    [Fact]
    public void APlainWireIgnoresV6AndNeverSendsTheEnvelope()
    {
        var (host, plugin) = Fresh(ChatWire.Plain);
        var alice = Arrive(host, plugin, "alice", Team.Red, "10.0.0.2");

        plugin.Receive(alice, [0]);
        Assert.DoesNotContain(host.Sent, s => s.Slot == alice);

        plugin.Receive(alice, [0, ChatPlugin.ProtocolVersion, 0]);
        Assert.Equal([0, ChatPlugin.ProtocolVersion], host.Sent.Single(s => s.Slot == alice).Data[..2]);
        Assert.Equal("chat2026", plugin.Name);
    }

    // --- lines ------------------------------------------------------------------

    [Fact]
    public void ALineCrossesBetweenFormatsByteForByte()
    {
        var (host, plugin) = Fresh();
        var alice = JoinV6(host, plugin, "alice", Team.Red);
        var bob = JoinChat2026(host, plugin, "bob", Team.Blue);
        host.Sent.Clear();

        SayV6(plugin, alice, "hello");
        SayChat2026(plugin, bob, "hi");

        Assert.Equal(
            [
                [E, 1, 1, 0, 0, 5, .. "alice"u8, 5, .. "hello"u8],
                [E, 1, 2, 0, 1, 3, .. "bob"u8, 2, .. "hi"u8],
            ],
            host.Sent.Where(s => s.Slot == bob).Select(s => s.Data));
        Assert.Equal(
            [
                [1, 0, 5, .. "hello"u8],
                [2, 0, 2, .. "hi"u8],
            ],
            host.Sent.Where(s => s.Slot == alice).Select(s => s.Data));
        Assert.Equal([(alice, "hello", false), (bob, "hi", false)], host.Heard);
    }

    /// <summary>
    /// Team chat reaches one team in both formats, with v6's audience of
    /// <c>1 + team</c>. An action becomes <c>chr(13)</c> + text for v6, which is
    /// v6's own <c>/me</c> form and which a stock client was seen to draw as
    /// <c>*Name text</c>; v6's <c>chr(13)</c> form becomes the action flag.
    /// </summary>
    [Fact]
    public void TeamChatAndActionsTranslate()
    {
        var (host, plugin) = Fresh();
        var alice = JoinV6(host, plugin, "alice", Team.Red);
        var bob = JoinChat2026(host, plugin, "bob", Team.Blue);
        var dave = JoinChat2026(host, plugin, "dave", Team.Red);
        host.Sent.Clear();

        SayV6(plugin, alice, "\rwaves", team: true);
        SayChat2026(plugin, dave, "/me dances");

        Assert.Equal(["v6 #1 audience=1 \rwaves", "v6 #3 audience=0 \rdances"], To(host, alice));
        Assert.Equal(["LINE #1 flags=9 team=0 alice: waves", "LINE #3 flags=8 team=0 dave: dances"], To(host, dave));
        Assert.Equal(["LINE #3 flags=8 team=0 dave: dances"], To(host, bob));
    }

    /// <summary>
    /// A command typed into v6 team chat runs. v6's own host relays it as text
    /// (<c>plugin.gml:493-505</c>); the core follows chat2026.
    /// </summary>
    [Fact]
    public void ACommandInV6TeamChatRuns()
    {
        var (host, plugin) = Fresh();
        var alice = JoinV6(host, plugin, "alice", Team.Red);
        var dave = JoinV6(host, plugin, "dave", Team.Red);
        host.Sent.Clear();

        SayV6(plugin, alice, "/nope", team: true);

        Assert.Equal(["v6 255 ONo such command. /help lists them."], To(host, alice));
        Assert.Empty(To(host, dave));
    }

    [Fact]
    public void NoticesToV6AreOrangeEscapedAndSuppressibleOnlyAboutPlayers()
    {
        var (host, plugin) = Fresh();
        var alice = JoinV6(host, plugin, "alice", Team.Red);
        var bob = JoinChat2026(host, plugin, "bob", Team.Blue);
        host.Sent.Clear();

        Arrive(host, plugin, "a#b", Team.Blue, "10.0.0.7");
        SayV6(plugin, alice, "/players");

        Assert.Equal("v6 254 Oa\\#b has joined the game.", To(host, alice)[0]);
        Assert.Equal("NOTICE 1 a#b has joined the game.", To(host, bob)[0]);
        Assert.All(To(host, alice).Skip(1), line => Assert.StartsWith(@"v6 255 O\#", line));
    }

    [Fact]
    public void TheServerSpeaksAsSlotZeroInBothFormats()
    {
        var (host, plugin) = Fresh();
        var alice = JoinV6(host, plugin, "alice", Team.Red);
        var bob = JoinChat2026(host, plugin, "bob", Team.Blue);
        host.Sent.Clear();

        plugin.SayAsServer("/not a command");

        Assert.Equal(["v6 #0 audience=0 /not a command"], To(host, alice));
        Assert.Equal(["LINE #0 flags=0 team=2 Server: /not a command"], To(host, bob));
        Assert.Equal([(0, "/not a command", false)], host.Heard);
    }

    // --- votes ------------------------------------------------------------------

    /// <summary>
    /// One tally, answered from both formats. v6 gets the dialog as a 204, one
    /// 203 per answer, then a 201 naming the winner and the result as a 255.
    /// </summary>
    [Fact]
    public void AVoteIsAnsweredFromBothFormats()
    {
        var (host, plugin) = Fresh();
        var alice = JoinV6(host, plugin, "alice", Team.Red);
        var bob = JoinChat2026(host, plugin, "bob", Team.Blue);
        var carol = JoinChat2026(host, plugin, "carol", Team.Red);
        var dave = JoinV6(host, plugin, "dave", Team.Blue);
        host.Sent.Clear();

        SayChat2026(plugin, bob, "/votescramble");
        plugin.Receive(alice, [3, 1]);
        plugin.Receive(dave, [3, 2]);
        plugin.Receive(carol, [E, 2, 1, 1]);
        plugin.Tick();

        Assert.Equal(
            [
                "v6 204 Scramble the teams? [Yes|No] 0,0",
                "v6 203 +0",
                "v6 255 Obob started a vote: Scramble the teams?",
                "v6 203 +0",
                "v6 203 +1",
                "v6 203 +0",
                "v6 201 winner=1",
                "v6 255 OTeams scrambled (3 yes, 1 no).",
            ],
            To(host, alice));
        Assert.Equal("VOTE_START 1 Scramble the teams? [Yes|No] may=1", To(host, carol)[0]);
        Assert.Equal("VOTE_TALLY 1 3,1", To(host, carol)[^2]);
        Assert.Equal("VOTE_END 1 winner=1 Teams scrambled (3 yes, 1 no).", To(host, carol)[^1]);
        Assert.False(plugin.VoteActive);
    }

    /// <summary>
    /// v6 has no dialog a player may see but not answer: its kick target gets no
    /// dialog at all (<c>mayVote</c>), only the result line, and an answer it
    /// sends anyway is not counted.
    /// </summary>
    [Fact]
    public void AV6KickTargetGetsNoDialogOnlyTheResult()
    {
        var (host, plugin) = Fresh();
        var alice = JoinChat2026(host, plugin, "alice", Team.Red);
        var eve = JoinV6(host, plugin, "eve", Team.Blue);
        JoinV6(host, plugin, "dave", Team.Blue);
        host.Sent.Clear();

        SayChat2026(plugin, alice, "/votekick eve");
        plugin.Receive(eve, [3, 2]);
        Assert.True(plugin.VoteActive);

        host.CurrentTimeMilliseconds += 31000;
        plugin.Tick();

        var lines = To(host, eve);
        Assert.DoesNotContain(lines, line => line.StartsWith("v6 20", StringComparison.Ordinal));
        Assert.Equal("v6 255 Oeve stays (1 of the 2 votes needed).", lines[^1]);
    }

    /// <summary>
    /// A player who switches format mid-vote is offered the vote again, with the
    /// counts so far, but the answer already given stands: the switch is not a
    /// second vote.
    /// </summary>
    [Fact]
    public void SwitchingFormatMidVoteIsNotASecondVote()
    {
        var (host, plugin) = Fresh();
        var alice = JoinChat2026(host, plugin, "alice", Team.Red);
        var bob = JoinChat2026(host, plugin, "bob", Team.Blue);
        JoinChat2026(host, plugin, "carol", Team.Blue);

        SayChat2026(plugin, alice, "/votescramble");
        plugin.Receive(bob, [E, 2, 1, 2]);
        host.Sent.Clear();

        plugin.Receive(bob, [0]);
        plugin.Receive(bob, [3, 1]);

        Assert.Equal(["v6 204 Scramble the teams? [Yes|No] 1,1"], To(host, bob));
        Assert.True(plugin.VoteActive);
    }

    [Fact]
    public void AV6ClientGreetingMidVoteIsCaughtUp()
    {
        var (host, plugin) = Fresh();
        var alice = JoinChat2026(host, plugin, "alice", Team.Red);
        JoinChat2026(host, plugin, "bob", Team.Blue);
        SayChat2026(plugin, alice, "/votenextround");

        var dave = JoinV6(host, plugin, "dave", Team.Blue);

        Assert.Equal("v6 204 End this round now? [Yes|No] 1,0", To(host, dave)[^1]);
    }

    // --- mutes ------------------------------------------------------------------

    /// <summary>
    /// A mute is by address and survives a reconnect whichever format the player
    /// comes back in. v6 used to refuse a muted address's hello outright; under
    /// the core it is greeted and can read, and cannot talk.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AMuteSurvivesAReconnectInEitherFormat(bool backAsChat2026)
    {
        var (host, plugin) = Fresh(settings: new ChatSettings { AdminPassword = "pw" });
        var admin = JoinChat2026(host, plugin, "admin", Team.Red);
        var bob = JoinV6(host, plugin, "bob", Team.Blue, "10.0.0.50");
        SayChat2026(plugin, admin, "/login pw");
        SayChat2026(plugin, admin, "/mute bob 5");

        plugin.PlayerRemoving(bob);
        host.RemoveAt(bob);
        bob = backAsChat2026
            ? JoinChat2026(host, plugin, "bob", Team.Blue, "10.0.0.50")
            : JoinV6(host, plugin, "bob", Team.Blue, "10.0.0.50");
        host.Sent.Clear();

        if (backAsChat2026)
            SayChat2026(plugin, bob, "let me talk");
        else
            SayV6(plugin, bob, "let me talk");

        Assert.Empty(To(host, admin));
        Assert.Equal(
            backAsChat2026 ? "NOTICE 5 You are muted for another 300 seconds." : "v6 255 OYou are muted for another 300 seconds.",
            Assert.Single(To(host, bob)));
    }

    // --- /bots ------------------------------------------------------------------

    [Fact]
    public void BotsIsRefusedUntilTheServerConfiguresAWindow()
    {
        var (host, plugin) = Fresh();
        var alice = JoinV6(host, plugin, "alice", Team.Red);
        host.BotsManaged = true;
        host.BotCount = 4;
        host.Sent.Clear();

        SayV6(plugin, alice, "/bots off");

        Assert.Equal(["v6 255 OThis server does not let bots be turned off from chat."], To(host, alice));
        Assert.Empty(host.Suspensions);
    }

    [Fact]
    public void BotsOffSuspendsAndASecondOffDoesNotExtendIt()
    {
        var host = new FakePluginHost { SuspendMinutes = 20, BotsManaged = true, BotCount = 4 };
        (host, var plugin) = Fresh(host: host);
        var alice = JoinV6(host, plugin, "alice", Team.Red);
        var bob = JoinChat2026(host, plugin, "bob", Team.Blue);
        host.Sent.Clear();

        SayChat2026(plugin, bob, "/bots");
        SayChat2026(plugin, bob, "/bots off");
        SayV6(plugin, alice, "/bots off");
        SayV6(plugin, alice, "/bots on");

        Assert.Equal(
            [
                "NOTICE 5 4 bots are playing. /bots off takes them away for 20 minutes.",
                "NOTICE 0 bob turned the bots off for 20 minutes. /bots on brings them back.",
                "NOTICE 0 alice turned the bots back on.",
            ],
            To(host, bob));
        Assert.Equal(
            [
                "v6 255 Obob turned the bots off for 20 minutes. /bots on brings them back.",
                "v6 255 OBots are already off for another 20 minutes.",
                "v6 255 Oalice turned the bots back on.",
            ],
            To(host, alice));
        Assert.Equal([20], host.Suspensions);
        Assert.Equal(1, host.Resumes);
    }

    [Fact]
    public void BotsOffOnAServerWithNoPopulationManagerIsRefused()
    {
        var host = new FakePluginHost { SuspendMinutes = 20 };
        (host, var plugin) = Fresh(host: host);
        var alice = JoinV6(host, plugin, "alice", Team.Red);
        host.Sent.Clear();

        SayV6(plugin, alice, "/bots off");

        Assert.Equal(["v6 255 OThis server is not running bots."], To(host, alice));
        Assert.Empty(host.Suspensions);
    }
}
