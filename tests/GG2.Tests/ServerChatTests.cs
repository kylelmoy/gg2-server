using System.Text;
using GG2.Protocol;
using GG2.Server;
using GG2.Server.Plugins;

namespace GG2.Tests;

/// <summary>
/// The server's chat, as a native client sees it: every frame behind
/// <see cref="ServerChat.Envelope"/>, against a host that is not a server.
/// </summary>
/// <remarks>
/// The native wire's reference host is the GML host in gg2-chat2026, verified live
/// against a GM8 server with its <c>tests/live.js</c>; every expected string below
/// is one that run read back out of a real client or the reply text the GML sends,
/// so a native client cannot tell the two hosts apart. <c>ServerChatV6Tests</c>
/// covers what a stock v6 client is sent.
/// </remarks>
public sealed class ServerChatTests
{
    private const byte Hello = 0;
    private const byte Say = 1;
    private const byte VoteKind = 2;

    private static (FakePluginHost Host, ServerChat Plugin) Fresh(ChatSettings? settings = null)
    {
        var host = new FakePluginHost();
        var plugin = new ServerChat(settings);
        plugin.Attach(new PluginChannel(host, 0));

        host.Add("Server", Team.Spectator, address: string.Empty, client: false);
        plugin.PlayerAdded(0);
        plugin.MapChanged("ctf_truefort");
        plugin.Tick();
        return (host, plugin);
    }

    /// <summary>A roster entry, its first Begin Step, then its HELLO — the order a join takes.</summary>
    private static int Join(FakePluginHost host, ServerChat plugin, string name, Team team, string address = "10.0.0.1", bool greet = true)
    {
        var slot = host.Add(name, team, address);
        plugin.PlayerAdded(slot);
        plugin.Tick();
        if (greet)
            Send(plugin, slot, [Hello, ServerChat.ProtocolVersion, 0]);

        return slot;
    }

    /// <summary>One native message from <paramref name="slot"/>, behind the envelope.</summary>
    private static void Send(ServerChat plugin, int slot, ReadOnlySpan<byte> message) =>
        plugin.Receive(slot, [ServerChat.Envelope, .. message]);

    private static void Leave(FakePluginHost host, ServerChat plugin, int slot)
    {
        plugin.PlayerRemoving(slot);
        host.RemoveAt(slot);
    }

    private static void Speak(ServerChat plugin, int slot, string text, int audience = 0)
    {
        var bytes = GG2Text.Encode(text);
        var body = new byte[3 + bytes.Length];
        body[0] = Say;
        body[1] = (byte)audience;
        body[2] = (byte)bytes.Length;
        bytes.CopyTo(body, 3);
        Send(plugin, slot, body);
    }

    private static void Answer(ServerChat plugin, int slot, int voteId, int option) =>
        Send(plugin, slot, [VoteKind, (byte)voteId, (byte)option]);

    /// <summary>Every packet sent to <paramref name="slot"/>, decoded to one readable line each.</summary>
    private static List<string> To(FakePluginHost host, int slot) =>
        host.Sent.Where(s => s.Slot == slot).Select(s => Decode(Unwrap(s.Data))).ToList();

    /// <summary>Just the text a client would log: LINE as "tags name: text", NOTICE and VOTE_END text.</summary>
    private static List<string> Log(FakePluginHost host, int slot) =>
        host.Sent.Where(s => s.Slot == slot).Select(s => Plain(Unwrap(s.Data))).Where(t => t is not null).Select(t => t!).ToList();

    /// <summary>A sent frame without its envelope; a frame without one is a bug, and fails here.</summary>
    private static byte[] Unwrap(byte[] data)
    {
        Assert.Equal(ServerChat.Envelope, data[0]);
        return data[1..];
    }

    private static string Decode(byte[] data)
    {
        var at = 1;
        string Str()
        {
            var length = data[at++];
            var text = GG2Text.Decode(data.AsSpan(at, length));
            at += length;
            return text;
        }

        switch (data[0])
        {
            case 0:
                at = 5;
                return $"WELCOME v{data[1]} flags={data[2]} burst={data[3]} seconds={data[4]} motd={Str()}";
            case 1:
                at = 4;
                return $"LINE #{data[1]} flags={data[2]} team={data[3]} {Str()}: {Str()}";
            case 2:
                at = 2;
                return $"NOTICE {data[1]} {Str()}";
            case 3:
            {
                var id = data[at++];
                var question = Str();
                var n = data[at++];
                var options = new List<string>();
                for (var i = 0; i < n; i++)
                    options.Add(Str());

                var ticks = BitConverter.ToUInt16(data, at);
                at += 2;
                return $"VOTE_START {id} {question} [{string.Join("|", options)}] ticks={ticks} may={data[at]}";
            }
            case 4:
                return $"VOTE_TALLY {data[1]} {string.Join(",", data.Skip(3).Take(data[2]))}";
            case 5:
                at = 3;
                return $"VOTE_END {data[1]} winner={data[2]} {Str()}";
            default:
                return $"kind {data[0]}";
        }
    }

    private static string? Plain(byte[] data)
    {
        var decoded = Decode(data);
        if (decoded.StartsWith("NOTICE ", StringComparison.Ordinal))
            return decoded[(decoded.IndexOf(' ', 7) + 1)..];

        if (decoded.StartsWith("VOTE_END ", StringComparison.Ordinal))
        {
            var text = decoded[(decoded.IndexOf(' ', decoded.IndexOf("winner=", StringComparison.Ordinal)) + 1)..];
            return text.Length == 0 ? null : text;
        }

        if (!decoded.StartsWith("LINE ", StringComparison.Ordinal))
            return null;

        var flags = data[2];
        var body = decoded[(decoded.IndexOf(' ', decoded.IndexOf("team=", StringComparison.Ordinal)) + 1)..];
        var tags = new StringBuilder();
        if ((flags & 4) != 0)
            tags.Append("*SPEC* ");
        else if ((flags & 2) != 0)
            tags.Append("*DEAD* ");
        if ((flags & 1) != 0)
            tags.Append("(TEAM) ");

        if ((flags & 8) != 0)
        {
            var colon = body.IndexOf(": ", StringComparison.Ordinal);
            return tags + "* " + body[..colon] + " " + body[(colon + 2)..];
        }

        return tags + body;
    }

    // --- the handshake -------------------------------------------------------

    [Fact]
    public void HelloIsAnsweredWithAWelcomeCarryingTheVersionFlagsAndJoinLine()
    {
        var (host, plugin) = Fresh();
        host.ChatJoinMessage = "Welcome#to" + (char)7 + "the server";
        var alice = Join(host, plugin, "alice", Team.Red);

        Assert.Equal(["WELCOME v1 flags=3 burst=3 seconds=2 motd=Welcome#to the server"], To(host, alice));
    }

    [Fact]
    public void ARepeatedHelloIsIgnored()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red);
        host.Sent.Clear();

        Send(plugin, alice, [Hello, ServerChat.ProtocolVersion, 0]);

        Assert.Empty(host.Sent);
    }

    [Fact]
    public void NothingIsEverSentToAPlayerThatHasNotGreeted()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red);
        var quiet = Join(host, plugin, "quiet", Team.Red, greet: false);
        var bob = Join(host, plugin, "bob", Team.Blue);

        Speak(plugin, alice, "hello");
        Speak(plugin, alice, "/players");
        Leave(host, plugin, bob);

        Assert.Empty(To(host, quiet));
        Assert.Empty(To(host, 0));
    }

    [Fact]
    public void SayFromAnotherProtocolVersionIsIgnored()
    {
        var (host, plugin) = Fresh();
        var alice = host.Add("alice", Team.Red);
        plugin.PlayerAdded(alice);
        Send(plugin, alice, [Hello, 2, 0]);
        host.Sent.Clear();

        Speak(plugin, alice, "hello");

        Assert.Empty(host.Sent);
    }

    [Fact]
    public void EveryTruncationOfEveryPacketIsDroppedWithoutThrowing()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red);
        byte[][] packets =
        [
            [Hello, 1, 0],
            [Say, 0, 5, (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o'],
            [VoteKind, 1, 1],
        ];

        foreach (var packet in packets)
            for (var length = 0; length < packet.Length; length++)
                Send(plugin, alice, packet.AsSpan(0, length));

        Send(plugin, alice, [99, 1, 2, 3]);
    }

    // --- routing -------------------------------------------------------------

    [Fact]
    public void PublicChatReachesEveryGreetedPlayer()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red);
        var bob = Join(host, plugin, "bob", Team.Blue);
        host.Sent.Clear();

        Speak(plugin, alice, "hello all");

        Assert.Equal(["LINE #1 flags=0 team=0 alice: hello all"], To(host, alice));
        Assert.Equal(["LINE #1 flags=0 team=0 alice: hello all"], To(host, bob));
    }

    [Fact]
    public void TeamChatReachesOnlyTheTeamAndSpectatorsReachSpectators()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red);
        var carol = Join(host, plugin, "carol", Team.Red);
        var bob = Join(host, plugin, "bob", Team.Blue);
        var spec = Join(host, plugin, "spec", Team.Spectator);
        host.Sent.Clear();

        Speak(plugin, alice, "reds only", audience: 1);
        Speak(plugin, spec, "spec team", audience: 1);

        Assert.Equal(["(TEAM) alice: reds only"], Log(host, carol));
        Assert.Empty(Log(host, bob));
        Assert.Equal(["*SPEC* (TEAM) spec: spec team"], Log(host, spec));
    }

    [Fact]
    public void DeadPlayersAreHeardByEveryoneWithGravetalkAndOnlyByTheDeadWithout()
    {
        foreach (var gravetalk in new[] { true, false })
        {
            var (host, plugin) = Fresh(new ChatSettings { Gravetalk = gravetalk });
            var alice = Join(host, plugin, "alice", Team.Red);
            var bob = Join(host, plugin, "bob", Team.Blue);
            var spec = Join(host, plugin, "spec", Team.Spectator);
            host.SetAlive(bob, false);
            host.Sent.Clear();

            Speak(plugin, bob, "boo");
            Speak(plugin, alice, "still here");

            Assert.Equal(gravetalk ? ["*DEAD* bob: boo", "alice: still here"] : ["alice: still here"], Log(host, alice));
            Assert.Equal(["*DEAD* bob: boo", "alice: still here"], Log(host, spec));
        }
    }

    [Fact]
    public void ACommandTypedIntoTeamChatRunsAndIsNotRelayed()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red);
        var carol = Join(host, plugin, "carol", Team.Red);
        host.Sent.Clear();

        Speak(plugin, alice, "/nosuch thing", audience: 1);

        Assert.Equal(["No such command. /help lists them."], Log(host, alice));
        Assert.DoesNotContain(host.Sent, s => s.Slot == carol);
    }

    [Fact]
    public void ControlBytesBecomeSpacesHashSurvivesAndTextIsCappedAt127()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red);
        host.Sent.Clear();

        Speak(plugin, alice, "a#b" + (char)7 + "c");
        Speak(plugin, alice, new string('z', 200));

        var log = Log(host, alice);
        Assert.Equal("alice: a#b c", log[0]);
        Assert.Equal("alice: " + new string('z', 127), log[1]);
    }

    [Fact]
    public void MeIsAnActionLine()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red);
        host.Sent.Clear();

        Speak(plugin, alice, "/me waves");

        Assert.Equal(["* alice waves"], Log(host, alice));
    }

    // --- rate limiting -------------------------------------------------------

    [Fact]
    public void ABurstOfThreeThenOneEveryTwoSecondsThenATimedMute()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red);
        var bob = Join(host, plugin, "bob", Team.Blue);
        host.Sent.Clear();

        for (var i = 1; i <= 4; i++)
            Speak(plugin, alice, $"burst {i}");

        Assert.Equal(["alice: burst 1", "alice: burst 2", "alice: burst 3", "Please wait 2 seconds before chatting again."], Log(host, alice));

        host.CurrentTimeMilliseconds += 2100;
        host.Sent.Clear();
        Speak(plugin, alice, "patient");
        Assert.Equal(["alice: patient"], Log(host, alice));

        // One refusal already counted above: nine more make ten inside 30 s.
        host.Sent.Clear();
        for (var i = 0; i < 9; i++)
            Speak(plugin, alice, $"spam {i}");

        var muteLine = "alice has been muted for 1 minute for spamming.";
        Assert.Equal([.. Enumerable.Repeat("Please wait 2 seconds before chatting again.", 8), muteLine], Log(host, alice));
        Assert.Equal([muteLine], Log(host, bob));

        host.Sent.Clear();
        Speak(plugin, alice, "let me talk");
        Assert.Equal(["You are muted for another 60 seconds."], Log(host, alice));

        host.Sent.Clear();
        Speak(plugin, alice, "/help me");
        Assert.Equal(["/me <action>: say it as an action, * Name waves."], Log(host, alice));
    }

    // --- announcements and names ---------------------------------------------

    [Fact]
    public void JoinsLeavesRenamesAndTeamChangesAreAnnounced()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red);
        host.Sent.Clear();

        var bob = Join(host, plugin, "bob", Team.Spectator);
        host.Rename(bob, "Bob");
        plugin.Tick();
        host.SetTeam(bob, Team.Blue);
        plugin.Tick();
        Leave(host, plugin, bob);

        Assert.Equal(
            ["bob has joined the game.", "bob changed name to Bob.", "Bob joined team BLU.", "Bob has left the game."],
            Log(host, alice).Where(l => !l.StartsWith("WELCOME", StringComparison.Ordinal)).ToList());
    }

    [Fact]
    public void AnEmptyNameBecomesNameless()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red);
        host.Rename(alice, string.Empty);
        host.Renames.Clear();

        plugin.Tick();

        Assert.Equal([(alice, "Nameless")], host.Renames);
    }

    [Fact]
    public void UniqueNamesSuffixTheNewcomerWithTheLowestFreeNumber()
    {
        var (host, plugin) = Fresh(new ChatSettings { UniqueNames = true });
        Join(host, plugin, "alice", Team.Red);
        Join(host, plugin, "alice3", Team.Red);
        host.Renames.Clear();

        var second = Join(host, plugin, "ALICE", Team.Blue);

        Assert.Equal([(second, "ALICE2")], host.Renames);
        Assert.Equal("alice", host.NameOf(1));
    }

    [Fact]
    public void AMuteStaysWithItsPlayerWhenTheRosterCompacts()
    {
        var (host, plugin) = Fresh(new ChatSettings { AdminPassword = "pw" });
        var alice = Join(host, plugin, "alice", Team.Red);
        var bob = Join(host, plugin, "bob", Team.Blue, address: "10.0.0.2");
        var carol = Join(host, plugin, "carol", Team.Red, address: "10.0.0.3");
        Speak(plugin, bob, "/login pw");
        Speak(plugin, bob, "/mute carol 5");

        // alice leaves: bob moves to slot 1 and carol to slot 2.
        Leave(host, plugin, alice);
        host.Sent.Clear();
        Speak(plugin, 1, "bob still talks");
        Speak(plugin, 2, "carol does not");

        Assert.Equal(["bob: bob still talks"], Log(host, 1));
        Assert.Equal(["bob: bob still talks", "You are muted for another 300 seconds."], Log(host, 2));
        _ = (bob, carol);
    }

    [Fact]
    public void AMuteFollowsTheAddressAcrossAReconnect()
    {
        var (host, plugin) = Fresh(new ChatSettings { AdminPassword = "pw" });
        var admin = Join(host, plugin, "admin", Team.Red);
        var bob = Join(host, plugin, "bob", Team.Blue, address: "10.0.0.2");
        Speak(plugin, admin, "/login pw");
        Speak(plugin, admin, "/mute bob 5");
        Leave(host, plugin, bob);

        host.CurrentTimeMilliseconds += 60000;
        bob = Join(host, plugin, "bob", Team.Blue, address: "10.0.0.2");
        host.Sent.Clear();
        Speak(plugin, bob, "am I muted");

        Assert.Equal(["You are muted for another 240 seconds."], Log(host, bob));
    }

    // --- commands ------------------------------------------------------------

    [Fact]
    public void HelpListsCommandsAndAdminCommandsOnlyForAdmins()
    {
        var (host, plugin) = Fresh(new ChatSettings { AdminPassword = "pw" });
        var alice = Join(host, plugin, "alice", Team.Red);
        host.Sent.Clear();

        Speak(plugin, alice, "/help");
        Speak(plugin, alice, "/login pw");
        Speak(plugin, alice, "/HELP");

        Assert.Equal(
            [
                "Commands: /bots /help /ignore /login /maplist /me /players /unignore /votekick /votemap /votemute /votenextround /votescramble. /help <command> for more.",
                "You are now an admin for this session.",
                "Commands: /bots /help /ignore /login /maplist /me /players /unignore /votekick /votemap /votemute /votenextround /votescramble. /help <command> for more.",
                "Admin: /kick /map /mute /nextround /say /unmute.",
            ],
            Log(host, alice));
    }

    [Fact]
    public void AdminCommandsAreRefusedAndLoginNeedsTheRightPassword()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red);
        host.Sent.Clear();

        Speak(plugin, alice, "/say hi");
        Speak(plugin, alice, "/login anything");

        (host, plugin) = Fresh(new ChatSettings { AdminPassword = "hunter2" });
        var bob = Join(host, plugin, "bob", Team.Red);
        host.Sent.Clear();
        Speak(plugin, bob, "/login nope");
        Speak(plugin, bob, "/login hunter2");
        Speak(plugin, bob, "/say server message #1");

        Assert.Equal(["Wrong password.", "You are now an admin for this session.", "server message #1"], Log(host, bob));
    }

    [Fact]
    public void PlayersListsIndicesAndTeamsAndNamesResolveByNumberOrUniqueSubstring()
    {
        var (host, plugin) = Fresh(new ChatSettings { AdminPassword = "pw" });
        var alice = Join(host, plugin, "Player", Team.Red);
        Join(host, plugin, "Player", Team.Blue);
        Speak(plugin, alice, "/login pw");
        host.Sent.Clear();

        Speak(plugin, alice, "/players");
        Speak(plugin, alice, "/mute player");
        Speak(plugin, alice, "/mute zzzz");
        Speak(plugin, alice, "/mute #9");
        Speak(plugin, alice, "/unmute #2");

        Assert.Equal(
            [
                "#0 Server (spectating)",
                "#1 Player (RED)",
                "#2 Player (BLU)",
                "\"player\" matches Player (#1), Player (#2). Use #number.",
                "No player matches \"zzzz\". /players lists them.",
                "No player #9. /players lists them.",
                "Player was unmuted by Player.",
            ],
            Log(host, alice));
    }

    [Fact]
    public void MaplistPagesTheRotationAndMarksTheCurrentMap()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red);
        host.Sent.Clear();

        Speak(plugin, alice, "/maplist");
        host.MapRotation = Enumerable.Range(0, 12).Select(i => "map" + i).ToArray();
        plugin.MapChanged("map3");
        Speak(plugin, alice, "/maplist");
        Speak(plugin, alice, "/maplist 10");

        Assert.Equal(
            [
                "This server is not rotating maps.",
                "Maps 1-10 of 12: map0, map1, map2, map3 (now), map4, map5, map6, map7, map8, map9. /maplist 10 for more.",
                "Maps 11-12 of 12: map10, map11",
            ],
            Log(host, alice));
    }

    [Fact]
    public void KickNextRoundAndMapGoThroughTheHost()
    {
        var (host, plugin) = Fresh(new ChatSettings { AdminPassword = "pw" });
        host.VotableMaps = ["koth_valley"];
        var alice = Join(host, plugin, "alice", Team.Red);
        Join(host, plugin, "bob", Team.Blue);
        Speak(plugin, alice, "/login pw");

        Speak(plugin, alice, "/kick bob");
        Speak(plugin, alice, "/kick #0");
        Speak(plugin, alice, "/map nowhere");
        Speak(plugin, alice, "/map koth_valley");

        Assert.Equal("bob", Assert.Single(host.Kicked).Name);
        Assert.Equal(["koth_valley"], host.NextMaps);
        Assert.Equal(1, host.RoundEnds);
        Assert.Contains("The host cannot be kicked.", Log(host, alice));
        Assert.Contains("This server has no map called nowhere.", Log(host, alice));
    }

    // --- votes ---------------------------------------------------------------

    private static int VoteIdIn(FakePluginHost host, int slot)
    {
        var start = host.Sent.Last(s => s.Slot == slot && s.Data[1] == 3);
        return start.Data[2];
    }

    [Fact]
    public void AVoteKickPassesKicksAndBansTheAddressAndARejoinIsDropped()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red, address: "10.0.0.1");
        var bob = Join(host, plugin, "bob", Team.Blue, address: "10.0.0.2");
        var carol = Join(host, plugin, "carol", Team.Red, address: "10.0.0.3");
        host.Sent.Clear();

        Speak(plugin, alice, "/votekick carol");
        var id = VoteIdIn(host, carol);

        Assert.Contains("VOTE_START " + id + " Kick carol? [Yes|No] ticks=900 may=0", To(host, carol));
        Assert.Contains("VOTE_START " + id + " Kick carol? [Yes|No] ticks=900 may=1", To(host, bob));
        Assert.Contains("VOTE_TALLY " + id + " 1,0", To(host, bob));

        Answer(plugin, carol, id, 2);   // the target may not answer
        Answer(plugin, bob, id, 1);
        plugin.Tick();

        Assert.Contains("carol was kicked (2 yes, 0 no).", Log(host, alice));
        Assert.Equal("carol", Assert.Single(host.Kicked).Name);
        Assert.False(plugin.VoteActive);

        Leave(host, plugin, carol);
        host.Kicked.Clear();
        host.Sent.Clear();
        var again = host.Add("carol", Team.Spectator, "10.0.0.3");
        plugin.PlayerAdded(again);
        plugin.Tick();

        Assert.Equal(1, plugin.KickBanDrops);
        Assert.Equal("carol", Assert.Single(host.Kicked).Name);
        Assert.DoesNotContain(Log(host, alice), l => l.Contains("has joined", StringComparison.Ordinal));
    }

    [Fact]
    public void ThresholdsQuorumAndTheCooldown()
    {
        var (host, plugin) = Fresh(new ChatSettings { VoteMutePercent = 51 });
        var alice = Join(host, plugin, "alice", Team.Red);
        var bob = Join(host, plugin, "bob", Team.Blue);
        var carol = Join(host, plugin, "carol", Team.Red);

        // 1 yes 1 no is 50%: short of 51.
        Speak(plugin, alice, "/votemute carol");
        Answer(plugin, bob, VoteIdIn(host, bob), 2);
        plugin.Tick();
        Assert.Contains("carol is not muted (1 yes, 1 no).", Log(host, alice));

        host.Sent.Clear();
        Speak(plugin, alice, "/votescramble");
        Assert.Equal(["Please wait 60 seconds before starting another vote."], Log(host, alice));

        // bob calls; nobody else answers and time runs out: 1 of the 2 needed.
        host.Sent.Clear();
        Speak(plugin, bob, "/votenextround");
        host.CurrentTimeMilliseconds += 30000;
        plugin.Tick();
        Assert.Contains("The round goes on (1 of the 2 votes needed).", Log(host, alice));
        Assert.Equal(0, host.RoundEnds);
    }

    [Fact]
    public void AStaleOrSecondAnswerIsNotCounted()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red);
        var bob = Join(host, plugin, "bob", Team.Blue);
        Join(host, plugin, "carol", Team.Red);
        Speak(plugin, alice, "/votescramble");
        var id = VoteIdIn(host, bob);
        host.Sent.Clear();

        Answer(plugin, bob, (id + 255) % 256, 1);
        Answer(plugin, bob, id, 7);
        Answer(plugin, alice, id, 2);
        Assert.Empty(host.Sent);

        Answer(plugin, bob, id, 2);
        Answer(plugin, bob, id, 1);
        Assert.Equal(["VOTE_TALLY " + id + " 1,1"], To(host, bob));
    }

    [Fact]
    public void ScrambleAndNextRoundPassWithAStrictMajority()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red);
        var bob = Join(host, plugin, "bob", Team.Red);
        var carol = Join(host, plugin, "carol", Team.Red);
        var dave = Join(host, plugin, "dave", Team.Red);

        Speak(plugin, alice, "/votescramble");
        var id = VoteIdIn(host, bob);
        Answer(plugin, bob, id, 1);
        Answer(plugin, carol, id, 1);
        Answer(plugin, dave, id, 2);
        plugin.Tick();

        Assert.Contains("Teams scrambled (3 yes, 1 no).", Log(host, alice));
        Assert.Equal(2, Enumerable.Range(1, 4).Count(s => host.TeamOf(s) == Team.Red));
        Assert.Equal(2, Enumerable.Range(1, 4).Count(s => host.TeamOf(s) == Team.Blue));

        Speak(plugin, bob, "/votenextround");
        id = VoteIdIn(host, alice);
        Answer(plugin, alice, id, 1);
        Answer(plugin, carol, id, 2);
        Answer(plugin, dave, id, 2);
        plugin.Tick();
        Assert.Contains("The round goes on (2 yes, 2 no).", Log(host, alice));
        Assert.Equal(0, host.RoundEnds);
    }

    [Fact]
    public void AMapVoteSetsTheNextMapOncePerMap()
    {
        var (host, plugin) = Fresh();
        host.VotableMaps = ["ctf_truefort", "koth_valley"];
        var alice = Join(host, plugin, "alice", Team.Red);
        var bob = Join(host, plugin, "bob", Team.Blue);

        Speak(plugin, alice, "/votemap KOTH_VALLEY");
        Answer(plugin, bob, VoteIdIn(host, bob), 1);
        plugin.Tick();
        Assert.Equal(["koth_valley"], host.NextMaps);
        Assert.Contains("The next map will be koth_valley (2 yes, 0 no).", Log(host, alice));

        host.Sent.Clear();
        Speak(plugin, bob, "/votemap ctf_truefort");
        Assert.Equal(["The next map has already been voted on."], Log(host, bob));

        plugin.MapChanged("koth_valley");
        host.Sent.Clear();
        Speak(plugin, bob, "/votemap nowhere");
        Assert.Equal(["nowhere is not in the rotation. /maplist shows it."], Log(host, bob));
    }

    [Fact]
    public void ThePickerIsPrivateAndPickingStartsThePublicVote()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red);
        var bob = Join(host, plugin, "bob", Team.Blue);
        host.Sent.Clear();

        Speak(plugin, alice, "/votekick");

        Assert.Contains(To(host, alice), l => l.StartsWith("VOTE_START", StringComparison.Ordinal) && l.Contains("Who? [bob]", StringComparison.Ordinal));
        Assert.Empty(To(host, bob));

        Answer(plugin, alice, VoteIdIn(host, alice), 1);
        host.Sent.Clear();
        plugin.Tick();

        Assert.StartsWith("VOTE_END", To(host, alice)[0], StringComparison.Ordinal);
        Assert.Equal("alice started a vote: Kick bob?", Log(host, bob)[0]);
        Assert.True(plugin.VoteActive);
    }

    [Fact]
    public void ThePickerOffersANextPageOnlyWhenThereIsOne()
    {
        var (host, plugin) = Fresh();
        var caller = Join(host, plugin, "caller", Team.Red);
        for (var i = 0; i < 9; i++)
            Join(host, plugin, "p" + i, Team.Blue);

        host.Sent.Clear();
        Speak(plugin, caller, "/votemute");
        Assert.Contains(To(host, caller), l => l.Contains("Who? [p0|p1|p2|p3|p4|p5|p6|p7|Next page]", StringComparison.Ordinal));

        Answer(plugin, caller, VoteIdIn(host, caller), 9);
        host.Sent.Clear();
        plugin.Tick();
        Assert.Contains(To(host, caller), l => l.Contains("Who? [p8] ", StringComparison.Ordinal));
    }

    [Fact]
    public void TheEndOfTheRoundCancelsARunningVote()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red);
        var bob = Join(host, plugin, "bob", Team.Blue);
        Speak(plugin, alice, "/votekick bob");

        host.RoundOver = true;
        plugin.Tick();

        Assert.Contains("Vote cancelled: the round is over.", Log(host, alice));
        Assert.Empty(host.Kicked);

        host.Sent.Clear();
        Speak(plugin, bob, "/votescramble");
        Assert.Equal(["The round is over; vote on the next one."], Log(host, bob));
    }

    [Fact]
    public void APlayerGreetingMidVoteIsShownItAndMayVote()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red);
        Join(host, plugin, "bob", Team.Blue);
        Speak(plugin, alice, "/votescramble");
        host.CurrentTimeMilliseconds += 10000;

        var late = Join(host, plugin, "late", Team.Red);
        var sent = To(host, late);

        Assert.Contains(sent, l => l.EndsWith("Scramble the teams? [Yes|No] ticks=600 may=1", StringComparison.Ordinal));
        Assert.Contains(sent, l => l.EndsWith(" 1,0", StringComparison.Ordinal) && l.StartsWith("VOTE_TALLY", StringComparison.Ordinal));
    }

    [Fact]
    public void ATargetWhoLeavesMidVoteIsReportedAndTheRosterShiftDoesNotRetarget()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red);
        var bob = Join(host, plugin, "bob", Team.Blue);
        var carol = Join(host, plugin, "carol", Team.Red);
        var dave = Join(host, plugin, "dave", Team.Blue);

        Speak(plugin, alice, "/votekick carol");
        var id = VoteIdIn(host, bob);
        Leave(host, plugin, carol);   // dave moves into carol's slot
        Answer(plugin, 2, id, 1);   // bob
        Answer(plugin, 3, id, 1);   // dave, now in carol's old slot, votes and is not the target
        plugin.Tick();

        Assert.Contains("The player left before the vote ended.", Log(host, alice));
        Assert.Empty(host.Kicked);
        Assert.Equal("dave", host.NameOf(3));
        _ = dave;
    }

    [Fact]
    public void WithNoSettingsOfItsOwnTheChatTakesTheServers()
    {
        var host = new FakePluginHost { ChatSettings = new ChatSettings { AdminPassword = "from-config", VotesEnabled = false } };
        var plugin = new ServerChat();
        plugin.Attach(new PluginChannel(host, 0));
        host.Add("Server", Team.Spectator, address: string.Empty, client: false);
        plugin.PlayerAdded(0);
        var alice = Join(host, plugin, "alice", Team.Red);

        Speak(plugin, alice, "/login from-config");
        Speak(plugin, alice, "/votescramble");

        Assert.Equal("WELCOME v1 flags=1 burst=3 seconds=2 motd=", To(host, alice)[0]);
        Assert.Equal(["You are now an admin for this session.", "Voting is disabled on this server."], Log(host, alice));
    }

    [Fact]
    public void TheChatSettingsAreReadFromTheChatSectionOfTheConfigFile()
    {
        var options = new GG2.Server.ServerOptions();
        foreach (var setting in GG2.Server.ServerSettings.All.Where(s => s.Section == "Chat"))
        {
            switch (setting.Key)
            {
                case "Enabled": setting.Set(options, "0"); break;
                case "Gravetalk": setting.Set(options, "0"); break;
                case "UniqueNames": setting.Set(options, "1"); break;
                case "MuteMinutes": setting.Set(options, "5"); break;
                case "Votes": setting.Set(options, "0"); break;
                case "VoteKickPercent": setting.Set(options, "75"); break;
                case "VoteMutePercent": setting.Set(options, "70"); break;
                case "KickBanMinutes": setting.Set(options, "30"); break;
                case "AdminPassword": setting.Set(options, "pw"); break;
                default: Assert.Fail("[Chat] " + setting.Key + " is not covered here"); break;
            }
        }

        Assert.Equal(
            new ChatSettings
            {
                Enabled = false, Gravetalk = false, UniqueNames = true, MuteMinutes = 5, VotesEnabled = false,
                VoteKickPercent = 75, VoteMutePercent = 70, KickBanMinutes = 30, AdminPassword = "pw",
            },
            options.Chat);
    }

    /// <summary>
    /// The chat is the server's: the catalogue knows <c>chat</c> only as the zip a
    /// stock client downloads, and builds nothing for it.
    /// </summary>
    [Fact]
    public void TheCatalogueOnlyPinsTheChatZip()
    {
        Assert.Null(ServerPluginCatalogue.Create(ServerChat.PluginName));
        Assert.Equal("bad8081d64f0b808a2bb3a6d88978fb5", ServerPluginCatalogue.PinnedMd5(ServerChat.PluginName));
        Assert.Null(ServerPluginCatalogue.PinnedMd5("chat2026"));
    }
}
