using GG2.Protocol;
using GG2.Server.Plugins;

namespace GG2.Tests;

/// <summary>
/// The slash commands and votes baked into <see cref="ChatV2Plugin"/>.
/// </summary>
/// <remarks>
/// Ported from Lorgan3's server-side chat extensions — <c>party.gml</c>,
/// <c>votekick.gml</c>, <c>votemap.gml</c>, <c>votemute.gml</c>,
/// <c>votescramble.gml</c>.
/// <para>
/// <b>The vote UI is not the reference's, because it cannot be.</b> Those
/// plugins extend a newer chat plugin with a vote dialog; the clients here run
/// <c>chat_v2_pr</c>, which has none. So a vote is announced as text and answered
/// by typing. What IS the reference's, and is what these assert, is every
/// <em>rule</em>: the thresholds (90 / 70 / 50), the quorum, the thirty-second
/// clock, one vote at a time, and the target of a kick not voting on it.
/// </para>
/// <para>
/// <b>Thresholds are the part with no symptom.</b> A kick that needed 50%
/// instead of 90% would work perfectly in every manual test and be a grief tool
/// on a live server. Each one below cites the GML line it comes from.
/// </para>
/// </remarks>
public sealed class ChatV2CommandTests
{
    private const byte Hello = 0;
    private const byte GlobalChat = 1;

    /// <summary><c>sendVote(30 * 30, ...)</c>.</summary>
    private const int VoteTicks = 900;

    private static (FakePluginHost Host, ChatV2Plugin Plugin) Fresh()
    {
        var host = new FakePluginHost();
        var plugin = new ChatV2Plugin();
        plugin.Attach(new PluginChannel(host, 0));

        host.Add("Server", Team.Spectator, address: string.Empty, client: false);
        plugin.PlayerAdded(0);

        return (host, plugin);
    }

    /// <summary>Joins a player and puts them in chat, which is what a command needs.</summary>
    private static int Join(
        FakePluginHost host,
        ChatV2Plugin plugin,
        string name,
        Team team = Team.Red,
        string address = "10.0.0.1",
        bool client = true)
    {
        var slot = host.Add(name, team, address, client);
        plugin.PlayerAdded(slot);
        plugin.Tick();

        if (client)
            plugin.Receive(slot, [Hello]);

        return slot;
    }

    /// <summary>Sends one line of global chat, which is how a command arrives.</summary>
    private static void Say(ChatV2Plugin plugin, int slot, string message)
    {
        var text = GG2Text.Encode(message);
        var body = new byte[2 + text.Length];
        body[0] = GlobalChat;
        body[1] = (byte)text.Length;
        text.CopyTo(body, 2);
        plugin.Receive(slot, body);
    }

    /// <summary>Runs the vote clock out.</summary>
    private static void RunVoteToEnd(ChatV2Plugin plugin)
    {
        for (var i = 0; i < VoteTicks; i++)
            plugin.Tick();
    }

    // --- the plain commands --------------------------------------------------

    /// <summary>
    /// A command is never echoed as chat, even one that was refused.
    /// </summary>
    /// <remarks>
    /// The load-bearing half of the whole design. The client sends "/votekick
    /// bob" as ordinary text and has no idea it was special; if the server
    /// echoed it, every command would appear as a message and the vote would be
    /// unreadable underneath it.
    /// </remarks>
    [Fact]
    public void ACommandIsNeverEchoedAsChat()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice");
        host.Sent.Clear();

        Say(plugin, alice, "/party");
        Say(plugin, alice, "/nosuchcommand");
        Say(plugin, alice, "/votekick nobody");

        Assert.DoesNotContain(host.MessagesTo(alice), m => m.Contains("alice: ", StringComparison.Ordinal));
    }

    [Fact]
    public void PartyAnnouncesToEverybodyInChat()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice");
        var bob = Join(host, plugin, "bob", Team.Blue, "10.0.0.2");
        host.Sent.Clear();

        Say(plugin, alice, "/party");

        Assert.Contains("OParty hard!", host.MessagesTo(alice));
        Assert.Contains("OParty hard!", host.MessagesTo(bob));
    }

    [Fact]
    public void HelpListsEveryCommandAndExplainsOne()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice");
        host.Sent.Clear();

        Say(plugin, alice, "/help");
        var listing = string.Join(' ', host.MessagesTo(alice));

        foreach (var command in new[] { "/votekick", "/votemute", "/votescramble", "/votenextround", "/votemap", "/party" })
            Assert.Contains(command, listing, StringComparison.Ordinal);

        host.Sent.Clear();
        Say(plugin, alice, "/help votekick");
        Assert.Contains("Starts a vote to kick", host.MessagesTo(alice)[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>/help</c> is split across lines rather than sent as one. A chat
    /// message's length prefix is a byte, so one long line would be cut on the
    /// wire — silently, and only once enough commands existed.
    /// </summary>
    [Fact]
    public void HelpFitsInsideTheLengthPrefix()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice");
        host.Sent.Clear();

        Say(plugin, alice, "/help");

        Assert.All(host.MessagesTo(alice), line => Assert.True(line.Length <= 255));
        Assert.True(host.MessagesTo(alice).Count > 1, "the listing should wrap onto more than one line");
    }

    /// <summary>
    /// <c>votekick.gml:107-110</c>: a client asking for a host command is told
    /// so. A headless server has no host player, so this is the only reachable
    /// branch — and it is the whole of the client-visible behaviour.
    /// </summary>
    [Theory]
    [InlineData("/kick alice")]
    [InlineData("/nextround")]
    public void HostOnlyCommandsAreRefused(string command)
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice");
        host.Sent.Clear();

        Say(plugin, alice, command);

        Assert.Equal("OYou can only use this command as host.", host.MessagesTo(alice)[0]);
        Assert.Empty(host.Kicked);
        Assert.Equal(0, host.RoundEnds);
    }

    // --- votekick ------------------------------------------------------------

    /// <summary>
    /// <c>votekick.gml:89</c>: <b>90%</b>, which is near-unanimity on purpose.
    /// </summary>
    [Fact]
    public void AUnanimousKickVotePasses()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice");
        var bob = Join(host, plugin, "bob", Team.Blue, "10.0.0.2");
        var victim = Join(host, plugin, "victim", Team.Blue, "10.0.0.3");

        Say(plugin, alice, "/votekick victim");
        Say(plugin, alice, "/yes");
        Say(plugin, bob, "/yes");
        RunVoteToEnd(plugin);

        Assert.Equal([("victim", "vote kicked")], host.Kicked);
        Assert.Contains(host.AllMessages(), m => m.Contains("has been vote kicked (100% yes)", StringComparison.Ordinal));
        Assert.Equal(3, victim);
    }

    /// <summary>
    /// <b>Two thirds is not enough to kick.</b> 66% clears every other
    /// threshold in this file and must not clear this one.
    /// </summary>
    [Fact]
    public void AKickVoteBelowNinetyPercentFails()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice");
        var bob = Join(host, plugin, "bob", Team.Blue, "10.0.0.2");
        var carol = Join(host, plugin, "carol", Team.Blue, "10.0.0.3");
        Join(host, plugin, "victim", Team.Blue, "10.0.0.4");

        Say(plugin, alice, "/votekick victim");
        Say(plugin, alice, "/yes");
        Say(plugin, bob, "/yes");
        Say(plugin, carol, "/no");
        RunVoteToEnd(plugin);

        Assert.Empty(host.Kicked);
        Assert.Contains(host.AllMessages(), m => m.Contains("Voting failed (34% no)", StringComparison.Ordinal));
    }

    /// <summary>
    /// <c>votekick.gml:40</c>, <c>target.mayVote = false</c>: the player a vote
    /// is about does not get a say in it.
    /// </summary>
    [Fact]
    public void TheTargetOfAVoteCannotVoteOnIt()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice");
        var victim = Join(host, plugin, "victim", Team.Blue, "10.0.0.3");

        Say(plugin, alice, "/votekick victim");
        host.Sent.Clear();
        Say(plugin, victim, "/no");

        Assert.Equal("OYou cannot vote on this.", host.MessagesTo(victim)[0]);

        Say(plugin, alice, "/yes");
        RunVoteToEnd(plugin);

        Assert.Equal([("victim", "vote kicked")], host.Kicked);
    }

    /// <summary>
    /// <c>votekick.gml:23-29</c>: the host is not kickable. Slot 0 is this
    /// server's own reserved player, which is the same thing.
    /// </summary>
    [Fact]
    public void TheHostSlotCannotBeKicked()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice");
        host.Sent.Clear();

        Say(plugin, alice, "/votekick Server");

        Assert.Equal("OYou cannot kick the host.", host.MessagesTo(alice)[0]);
    }

    /// <summary>
    /// Not in the reference, which had no bots. Kicking one would succeed and
    /// mean nothing — the population manager puts it straight back.
    /// </summary>
    [Fact]
    public void ABotCannotBeVoteKicked()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice");
        Join(host, plugin, "[BOT] 1", Team.Blue, address: string.Empty, client: false);
        host.Sent.Clear();

        Say(plugin, alice, "/votekick [BOT] 1");

        Assert.Equal("OYou cannot kick a bot.", host.MessagesTo(alice)[0]);
    }

    /// <summary>
    /// <b>A vote outlives a disconnection, and a slot index does not.</b>
    /// </summary>
    /// <remarks>
    /// Thirty seconds is long enough for somebody unrelated to leave. If the
    /// vote held a slot index, this test kicks "victim2" — the player who
    /// inherited the number — rather than the one it was called about. That is
    /// the whole reason <see cref="IPluginHost.StableIdOf"/> exists.
    /// </remarks>
    [Fact]
    public void AVoteFollowsItsTargetAcrossADeparture()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice");
        var bystander = Join(host, plugin, "bystander", Team.Blue, "10.0.0.2");
        Join(host, plugin, "victim", Team.Blue, "10.0.0.3");

        Say(plugin, alice, "/votekick victim");
        Say(plugin, alice, "/yes");

        // The bystander leaves mid-vote. "victim" was slot 3 and is now slot 2.
        plugin.PlayerRemoving(bystander);
        host.RemoveAt(bystander);
        Assert.Equal("victim", host.NameOf(2));

        RunVoteToEnd(plugin);

        Assert.Equal([("victim", "vote kicked")], host.Kicked);
    }

    /// <summary>The target leaving during the vote is the commonest outcome of one.</summary>
    [Fact]
    public void AVoteWhoseTargetLeftKicksNobody()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice");
        var victim = Join(host, plugin, "victim", Team.Blue, "10.0.0.3");

        Say(plugin, alice, "/votekick victim");
        Say(plugin, alice, "/yes");

        plugin.PlayerRemoving(victim);
        host.RemoveAt(victim);

        RunVoteToEnd(plugin);

        Assert.Empty(host.Kicked);
        Assert.Contains(host.AllMessages(), m => m.Contains("has already left", StringComparison.Ordinal));
    }

    /// <summary>
    /// A vote-kicked player comes back muted, because the kick is one
    /// disconnection rather than a ban.
    /// </summary>
    [Fact]
    public void AVoteKickedAddressCannotRejoinChat()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice");
        var victim = Join(host, plugin, "victim", Team.Blue, "10.0.0.9");

        Say(plugin, alice, "/votekick victim");
        Say(plugin, alice, "/yes");
        RunVoteToEnd(plugin);

        plugin.PlayerRemoving(victim);
        host.RemoveAt(victim);

        var again = Join(host, plugin, "victim", Team.Blue, "10.0.0.9");
        host.Sent.Clear();
        Say(plugin, again, "/party");

        Assert.Empty(host.Sent);
    }

    // --- votemute ------------------------------------------------------------

    /// <summary>
    /// The two bars, sandwiched: <c>votemute.gml:92</c> is <b>70%</b> and
    /// <c>votekick.gml:89</c> is <b>90%</b>.
    /// </summary>
    /// <remarks>
    /// <b>Every row votes NEAR a threshold, and that is the whole point.</b> A
    /// vote of 3-0 passes at every bar and so cannot catch a threshold dropped to
    /// a simple majority; 75% is between 70 and 90 but still above 50, so it
    /// alone cannot catch that mutation either. A threshold needs a row on each
    /// side of it:
    /// <list type="bullet">
    /// <item>60% fails a mute (and would pass a simple majority)</item>
    /// <item>75% passes a mute and fails a kick</item>
    /// <item>90% passes a kick</item>
    /// </list>
    /// </remarks>
    [Theory]
    [InlineData("votemute", 3, 2, false)]   // 60%: under 70
    [InlineData("votemute", 3, 1, true)]    // 75%: over 70, under 90
    [InlineData("votekick", 3, 1, false)]   // 75%: under 90
    [InlineData("votekick", 9, 1, true)]    // 90%: exactly the bar, which passes
    public void TheKickAndMuteBarsAreWhereTheReferencePutThem(
        string command, int yes, int no, bool expectedToPass)
    {
        var (host, plugin) = Fresh();

        var voters = new List<int>();
        for (var i = 0; i < yes + no; i++)
            voters.Add(Join(host, plugin, $"voter{i}", Team.Blue, $"10.0.0.{i + 10}"));

        Join(host, plugin, "victim", Team.Blue, "10.0.1.1");

        Say(plugin, voters[0], $"/{command} victim");
        for (var i = 0; i < voters.Count; i++)
            Say(plugin, voters[i], i < yes ? "/yes" : "/no");

        RunVoteToEnd(plugin);

        var percent = (int)Math.Floor(yes / (double)(yes + no) * 100);
        var outcome = string.Join(" | ", host.AllMessages());

        // The quorum must not be what decided it, or the row proves nothing
        // about the threshold.
        Assert.DoesNotContain("not enough people voted", outcome, StringComparison.Ordinal);

        if (expectedToPass)
        {
            Assert.Contains($"({percent}% yes)", outcome, StringComparison.Ordinal);
            Assert.DoesNotContain("Voting failed", outcome, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains($"Voting failed ({100 - percent}% no)", outcome, StringComparison.Ordinal);
            Assert.Empty(host.Kicked);
        }
    }

    /// <summary>A muted player is out of chat, so their commands stop working too.</summary>
    [Fact]
    public void AMutedPlayerCannotUseCommandsEither()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice");
        var bob = Join(host, plugin, "bob", Team.Blue, "10.0.0.2");
        var victim = Join(host, plugin, "victim", Team.Blue, "10.0.0.3");

        Say(plugin, alice, "/votemute victim");
        Say(plugin, alice, "/yes");
        Say(plugin, bob, "/yes");
        RunVoteToEnd(plugin);

        host.Sent.Clear();
        Say(plugin, victim, "/party");
        Assert.Empty(host.Sent);
    }

    // --- votescramble --------------------------------------------------------

    /// <summary>
    /// <c>votescramble.gml:76-113</c>: everyone on a team is reshuffled into two
    /// halves. Spectators are left where they are.
    /// </summary>
    [Fact]
    public void AScrambleVoteSplitsTheTeamsAndLeavesSpectatorsAlone()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice");
        var b = Join(host, plugin, "b", Team.Red, "10.0.0.2");
        Join(host, plugin, "c", Team.Blue, "10.0.0.3");
        Join(host, plugin, "d", Team.Blue, "10.0.0.4");
        var watcher = Join(host, plugin, "watcher", Team.Spectator, "10.0.0.5");

        Say(plugin, alice, "/votescramble");

        // Five chatters, so the quorum is two answers. One yes is 100% and
        // still not enough — see AVoteBelowQuorumFailsHoweverItSplit.
        Say(plugin, alice, "/yes");
        Say(plugin, b, "/yes");
        RunVoteToEnd(plugin);

        Assert.Contains(host.AllMessages(), m => m.Contains("Scrambling teams", StringComparison.Ordinal));

        var red = 0;
        var blue = 0;
        for (var slot = 1; slot <= 4; slot++)
        {
            if (host.TeamOf(slot) == Team.Red)
                red++;
            else if (host.TeamOf(slot) == Team.Blue)
                blue++;
        }

        Assert.Equal(2, red);
        Assert.Equal(2, blue);
        Assert.Equal(Team.Spectator, host.TeamOf(watcher));
    }

    /// <summary>
    /// <c>votescramble.gml:68</c>: strictly <b>more</b> than half, so an exact
    /// tie fails.
    /// </summary>
    [Fact]
    public void AnEvenlySplitScrambleVoteFails()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice");
        var bob = Join(host, plugin, "bob", Team.Blue, "10.0.0.2");

        Say(plugin, alice, "/votescramble");
        Say(plugin, alice, "/yes");
        Say(plugin, bob, "/no");
        RunVoteToEnd(plugin);

        Assert.Empty(host.TeamChanges);
        Assert.Contains(host.AllMessages(), m => m.Contains("Voting failed (50% no)", StringComparison.Ordinal));
    }

    // --- votenextround -------------------------------------------------------

    [Fact]
    public void ANextRoundVoteEndsTheRound()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice");

        Say(plugin, alice, "/votenextround");
        Say(plugin, alice, "/yes");
        RunVoteToEnd(plugin);

        Assert.Equal(1, host.RoundEnds);
        Assert.Contains(host.AllMessages(), m => m.Contains("Ending round", StringComparison.Ordinal));
    }

    // --- votemap -------------------------------------------------------------

    /// <summary>
    /// <c>votemap.gml:75-79</c>: exactly one matching map is a yes/no vote.
    /// </summary>
    [Fact]
    public void AMapVoteMatchingOneMapIsAYesNoVote()
    {
        var (host, plugin) = Fresh();
        host.VotableMaps = ["ctf_truefort", "koth_valley", "arena_montane"];
        var alice = Join(host, plugin, "alice");
        host.Sent.Clear();

        Say(plugin, alice, "/votemap koth");
        Assert.Contains(host.MessagesTo(alice), m => m.Contains("Set next map to koth_valley?", StringComparison.Ordinal));

        Say(plugin, alice, "/yes");
        RunVoteToEnd(plugin);

        Assert.Equal(["koth_valley"], host.NextMaps);
    }

    /// <summary>
    /// <c>votemap.gml:204</c>: a single-map vote needs <b>50%</b>, so an exact
    /// tie passes it — unlike every other yes/no vote here.
    /// </summary>
    /// <remarks>
    /// The row that matters is the 50-50 one: it passes here and fails under
    /// any higher bar,
    /// and it is also the row that separates this vote from
    /// <c>votescramble</c>'s <c>&gt; 50</c> a few tests up.
    /// </remarks>
    [Theory]
    [InlineData(1, 1, true)]    // 50%: exactly the bar
    [InlineData(1, 2, false)]   // 33%
    public void ASingleMapVotePassesOnATie(int yes, int no, bool expectedToPass)
    {
        var (host, plugin) = Fresh();
        host.VotableMaps = ["ctf_truefort", "koth_valley"];

        var voters = new List<int>();
        for (var i = 0; i < yes + no; i++)
            voters.Add(Join(host, plugin, $"voter{i}", Team.Blue, $"10.0.0.{i + 10}"));

        Say(plugin, voters[0], "/votemap koth");
        for (var i = 0; i < voters.Count; i++)
            Say(plugin, voters[i], i < yes ? "/yes" : "/no");

        RunVoteToEnd(plugin);

        var outcome = string.Join(" | ", host.AllMessages());
        Assert.DoesNotContain("not enough people voted", outcome, StringComparison.Ordinal);

        if (expectedToPass)
            Assert.Equal(["koth_valley"], host.NextMaps);
        else
            Assert.Empty(host.NextMaps);
    }

    /// <summary>
    /// <c>votemap.gml:80-86</c>: several matches become a numbered vote, with
    /// "keep default" appended.
    /// </summary>
    [Fact]
    public void AMapVoteMatchingSeveralMapsIsANumberedVote()
    {
        var (host, plugin) = Fresh();
        host.VotableMaps = ["ctf_truefort", "ctf_orange", "koth_valley"];
        var alice = Join(host, plugin, "alice");
        var bob = Join(host, plugin, "bob", Team.Blue, "10.0.0.2");
        host.Sent.Clear();

        Say(plugin, alice, "/votemap ctf");

        var announcement = string.Join(' ', host.MessagesTo(alice));
        Assert.Contains("1. ctf_", announcement, StringComparison.Ordinal);
        Assert.Contains("2. ctf_", announcement, StringComparison.Ordinal);
        Assert.Contains("keep default", announcement, StringComparison.Ordinal);

        Say(plugin, alice, "/vote 1");
        Say(plugin, bob, "/vote 1");
        RunVoteToEnd(plugin);

        Assert.Single(host.NextMaps);
        Assert.StartsWith("ctf_", host.NextMaps[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>votemap.gml:159</c>: nobody voting, or "keep default" winning, changes
    /// nothing.
    /// </summary>
    [Fact]
    public void AMapVoteNobodyAnswersChangesNothing()
    {
        var (host, plugin) = Fresh();
        host.VotableMaps = ["ctf_truefort", "ctf_orange", "koth_valley"];
        var alice = Join(host, plugin, "alice");

        Say(plugin, alice, "/votemap ctf");
        RunVoteToEnd(plugin);

        Assert.Empty(host.NextMaps);
        Assert.Contains(host.AllMessages(), m => m.Contains("maprotation has not been changed", StringComparison.Ordinal));
    }

    /// <summary>
    /// <c>selectedmap</c> (<c>votemap.gml:42-45</c>): once per round, and the
    /// round change is what clears it.
    /// </summary>
    [Fact]
    public void AMapVoteCanOnlySucceedOncePerRound()
    {
        var (host, plugin) = Fresh();
        host.VotableMaps = ["ctf_truefort", "koth_valley"];
        var alice = Join(host, plugin, "alice");

        Say(plugin, alice, "/votemap koth");
        Say(plugin, alice, "/yes");
        RunVoteToEnd(plugin);
        Assert.Single(host.NextMaps);

        host.Sent.Clear();
        Say(plugin, alice, "/votemap truefort");
        Assert.Contains(host.MessagesTo(alice), m => m.Contains("only vote for the next map once", StringComparison.Ordinal));

        plugin.MapChanged("koth_valley");
        host.Sent.Clear();
        Say(plugin, alice, "/votemap truefort");
        Assert.Contains(host.MessagesTo(alice), m => m.Contains("Set next map to ctf_truefort?", StringComparison.Ordinal));
    }

    [Fact]
    public void AMapVoteWithNoVotableMapsSaysSo()
    {
        var (host, plugin) = Fresh();
        host.VotableMaps = [];
        var alice = Join(host, plugin, "alice");
        host.Sent.Clear();

        Say(plugin, alice, "/votemap");

        Assert.Equal("ONo maps were found!", host.MessagesTo(alice)[0]);
    }

    // --- the shared vote rules ----------------------------------------------

    /// <summary>
    /// The guard every one of the five plugins opens with: one vote at a time.
    /// </summary>
    /// <remarks>
    /// The reference writes this as <c>global.chatWindow.alarm[0] &gt; 0</c>,
    /// which in <c>chat_v2_pr</c> would be the chat window's four-second fade. In
    /// the plugin these extend it is the <b>vote timer</b> — the message divides
    /// it by 30 and calls the result seconds, and <c>sendVote</c> is given
    /// <c>30 * 30</c>. Reading it the other way would have produced a cooldown
    /// nobody asked for.
    /// </remarks>
    [Fact]
    public void OnlyOneVoteRunsAtATime()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice");

        Say(plugin, alice, "/votescramble");
        host.Sent.Clear();

        Say(plugin, alice, "/votenextround");

        Assert.Contains(
            host.MessagesTo(alice),
            m => m.Contains("You may not call a vote at the moment", StringComparison.Ordinal));

        RunVoteToEnd(plugin);
        host.Sent.Clear();

        Say(plugin, alice, "/votenextround");
        Assert.Contains(host.MessagesTo(alice), m => m.Contains("End the round?", StringComparison.Ordinal));
    }

    /// <summary>
    /// Re-voting replaces rather than adding.
    /// </summary>
    /// <remarks>
    /// The reference collects one answer per client because its dialog can only
    /// be answered once; here the same message can arrive as many times as a
    /// player types it, and counting each one would let one person carry any
    /// vote alone.
    /// <para>
    /// <b>Asserted on the round end, not on a scramble.</b> A two-player
    /// scramble can deal players the teams they already had, so "no team moved"
    /// is not proof that ballots were not double-counted. The effect under test
    /// has to be one that cannot happen by luck.
    /// </para>
    /// </remarks>
    [Fact]
    public void VotingTwiceReplacesTheFirstAnswer()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice");
        var bob = Join(host, plugin, "bob", Team.Blue, "10.0.0.2");

        Say(plugin, alice, "/votenextround");
        Say(plugin, alice, "/yes");
        Say(plugin, alice, "/yes");
        Say(plugin, alice, "/yes");
        Say(plugin, bob, "/no");
        RunVoteToEnd(plugin);

        // One yes and one no is 50%, and votenextround needs strictly more.
        Assert.Equal(0, host.RoundEnds);
        Assert.Contains(host.AllMessages(), m => m.Contains("Voting failed (50% no)", StringComparison.Ordinal));
    }

    /// <summary>
    /// The quorum: <c>floor(voters / 2)</c> must answer, or the vote fails
    /// whatever the split was.
    /// </summary>
    /// <remarks>
    /// <b>This port divides the players who could vote; the reference divides
    /// <c>ds_list_size(global.players)</c>.</b> A deliberate difference — this
    /// server fills with bots by default and bots cannot type, so the
    /// reference's sum makes the quorum unreachable on every bot server. The
    /// feature would look implemented and never once pass.
    /// </remarks>
    [Fact]
    public void AVoteBelowQuorumFailsHoweverItSplit()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice");
        Join(host, plugin, "b", Team.Blue, "10.0.0.2");
        Join(host, plugin, "c", Team.Blue, "10.0.0.3");
        Join(host, plugin, "d", Team.Blue, "10.0.0.4");
        Join(host, plugin, "e", Team.Blue, "10.0.0.5");
        Join(host, plugin, "f", Team.Blue, "10.0.0.6");

        // Six chatters, so three answers are needed. One yes is 100% and still
        // not enough.
        Say(plugin, alice, "/votenextround");
        Say(plugin, alice, "/yes");
        RunVoteToEnd(plugin);

        Assert.Equal(0, host.RoundEnds);
        Assert.Contains(host.AllMessages(), m => m.Contains("not enough people voted", StringComparison.Ordinal));
    }

    /// <summary>
    /// Bots do not count towards the quorum, which is the whole reason it is
    /// computed over chatters rather than over the roster.
    /// </summary>
    [Fact]
    public void BotsDoNotMakeTheQuorumUnreachable()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice");
        var bob = Join(host, plugin, "bob", Team.Blue, "10.0.0.2");

        for (var i = 0; i < 12; i++)
            Join(host, plugin, $"[BOT] {i}", Team.Red, address: string.Empty, client: false);

        Say(plugin, alice, "/votenextround");
        Say(plugin, alice, "/yes");
        Say(plugin, bob, "/yes");
        RunVoteToEnd(plugin);

        Assert.Equal(1, host.RoundEnds);
    }

    /// <summary>A vote that is still running when the map changes is dropped.</summary>
    /// <remarks>
    /// Dropped rather than resolved: a scramble or a round-end vote landing in
    /// the round <em>after</em> the one it was called in would act on a world
    /// nobody voted about.
    /// </remarks>
    [Fact]
    public void AMapChangeCancelsAVoteInFlight()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice");

        Say(plugin, alice, "/votenextround");
        Say(plugin, alice, "/yes");

        plugin.MapChanged("koth_valley");
        RunVoteToEnd(plugin);

        Assert.Equal(0, host.RoundEnds);
        Assert.Contains(host.AllMessages(), m => m.Contains("cancelled by the map change", StringComparison.Ordinal));
    }

    [Fact]
    public void VotingWithNoVoteRunningSaysSo()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice");
        host.Sent.Clear();

        Say(plugin, alice, "/yes");

        Assert.Equal("OThere is no vote running.", host.MessagesTo(alice)[0]);
    }

    /// <summary>
    /// Ambiguity is refused, not resolved. Picking the first of two matches is
    /// how a vote kicks the wrong person.
    /// </summary>
    [Fact]
    public void AnAmbiguousNameIsRefused()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice");
        Join(host, plugin, "bobby", Team.Blue, "10.0.0.2");
        Join(host, plugin, "bobbo", Team.Blue, "10.0.0.3");
        host.Sent.Clear();

        Say(plugin, alice, "/votekick bob");

        Assert.Contains(host.MessagesTo(alice), m => m.Contains("More than one player matches", StringComparison.Ordinal));
    }

    /// <summary>An exact name beats a substring, so "bob" among "bob" and "bobby" works.</summary>
    [Fact]
    public void AnExactNameWinsOverASubstring()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice");
        Join(host, plugin, "bob", Team.Blue, "10.0.0.2");
        Join(host, plugin, "bobby", Team.Blue, "10.0.0.3");

        Say(plugin, alice, "/votekick bob");
        Say(plugin, alice, "/yes");
        RunVoteToEnd(plugin);

        Assert.Equal([("bob", "vote kicked")], host.Kicked);
    }

    // --- /bots ---------------------------------------------------------------
    //
    // ⚠️ This port's own command, so there is no GML to compare against. The same
    // four outcomes as ChatBridgeTests' copy, against this plugin's own wire.

    /// <summary>Off until the server sets <c>[Bots] SuspendMinutes</c>.</summary>
    [Fact]
    public void BotsIsRefusedUntilTheServerConfiguresAWindow()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice");
        host.Sent.Clear();

        Say(plugin, alice, "/bots off");

        Assert.Contains(
            "OThis server does not let bots be turned off from chat.",
            host.MessagesTo(alice));

        Assert.Empty(host.Suspensions);
    }

    /// <summary>
    /// <c>/bots off</c> suspends and announces it; <c>/bots on</c> ends it.
    /// </summary>
    /// <remarks>
    /// <b>The suspension is asked of the HOST, and the announcement goes to
    /// everybody.</b> A version that composed a reply without calling
    /// <c>SuspendBots</c> passes any test that reads only the text, and a version
    /// that whispered would leave everyone else watching the bots vanish with no
    /// explanation.
    /// </remarks>
    [Fact]
    public void BotsOffSuspendsAndBotsOnEndsIt()
    {
        var (host, plugin) = Fresh();
        host.SuspendMinutes = 15;
        host.BotsManaged = true;
        host.BotCount = 6;

        var alice = Join(host, plugin, "alice");
        var bob = Join(host, plugin, "bob", Team.Blue, "10.0.0.2");
        host.Sent.Clear();

        Say(plugin, alice, "/bots off");

        Assert.Equal([15], host.Suspensions);
        Assert.Equal(0, host.BotCount);
        Assert.Contains(
            host.MessagesTo(bob),
            m => m.Contains("alice turned the bots off for 15 minutes", StringComparison.Ordinal));

        host.Sent.Clear();
        Say(plugin, bob, "/bots on");

        Assert.Equal(1, host.Resumes);
        Assert.Contains(
            host.MessagesTo(alice),
            m => m.Contains("bob turned the bots back on", StringComparison.Ordinal));
    }

    /// <summary>A second <c>/bots off</c> inside the window does not re-arm it.</summary>
    [Fact]
    public void ASecondBotsOffDoesNotExtendTheWindow()
    {
        var (host, plugin) = Fresh();
        host.SuspendMinutes = 15;
        host.BotsManaged = true;
        host.BotCount = 6;

        var alice = Join(host, plugin, "alice");

        Say(plugin, alice, "/bots off");
        host.BotSuspendSecondsLeft = 300;

        host.Sent.Clear();
        Say(plugin, alice, "/bots off");

        Assert.Equal([15], host.Suspensions);
        Assert.Contains("OBots are already off for another 5 minutes.", host.MessagesTo(alice));
    }

    /// <summary>A server not running bots is told so rather than told it worked.</summary>
    [Fact]
    public void BotsOffOnAServerWithNoPopulationManagerIsRefused()
    {
        var (host, plugin) = Fresh();
        host.SuspendMinutes = 15;
        host.BotsManaged = false;

        var alice = Join(host, plugin, "alice");
        host.Sent.Clear();

        Say(plugin, alice, "/bots off");

        Assert.Contains("OThis server is not running bots.", host.MessagesTo(alice));
        Assert.Empty(host.Suspensions);
    }

    /// <summary>Bare <c>/bots</c> reports, including the running suspension.</summary>
    [Fact]
    public void BareBotsReportsThePopulation()
    {
        var (host, plugin) = Fresh();
        host.SuspendMinutes = 15;
        host.BotsManaged = true;
        host.BotCount = 1;

        var alice = Join(host, plugin, "alice");
        host.Sent.Clear();

        Say(plugin, alice, "/bots");

        Assert.Contains(
            "O1 bot is playing. /bots off takes the bots away for 15 minutes.",
            host.MessagesTo(alice));
    }

    /// <summary>
    /// It is in <c>/help</c>, which is the only way a player finds out it exists.
    /// </summary>
    [Fact]
    public void BotsIsListedInHelp()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice");
        host.Sent.Clear();

        Say(plugin, alice, "/help");

        Assert.Contains(host.MessagesTo(alice), m => m.Contains("bots", StringComparison.Ordinal));
    }

    // --- the configured join line --------------------------------------------

    /// <summary>
    /// <c>[Server] ChatJoinMessage</c> reaches a player when they announce chat,
    /// and nobody else.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b><c>#</c> becomes a space here, unlike on <c>chat</c>.</b> This
    /// plugin's client half reads the last <c>#</c> in a line to decide which
    /// colour a wrapped continuation starts in, so a <c>#</c> the server sent
    /// would recolour the rest of everyone's window.
    /// </remarks>
    [Fact]
    public void TheConfiguredJoinLineGoesToTheJoinerAloneWithNoColourMarker()
    {
        var (host, plugin) = Fresh();
        host.ChatJoinMessage = "Bots in the way?#Type /bots off";

        var alice = Join(host, plugin, "alice");
        host.Sent.Clear();

        var bob = Join(host, plugin, "bob", Team.Blue, "10.0.0.2");

        Assert.Contains("OBots in the way? Type /bots off", host.MessagesTo(bob));
        Assert.DoesNotContain(
            host.MessagesTo(alice),
            m => m.Contains("Bots in the way", StringComparison.Ordinal));
    }
}
