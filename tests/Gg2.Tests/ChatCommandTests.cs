using Gg2.Protocol;
using Gg2.Server.Plugins;

namespace Gg2.Tests;

/// <summary>
/// The <c>chat</c> plugin's console and its vote dialog.
/// </summary>
/// <remarks>
/// <b>A threshold needs a row on each side of it, and an effect under test has
/// to be one that cannot happen by luck.</b> A test that votes unanimously on a
/// threshold passes at every threshold; a scramble test with two players is
/// satisfied by "moved nobody". So the kick and mute bars are each driven from
/// both sides, and the scramble runs with enough players that the result is not
/// a coin flip.
/// <para>
/// The other thing worth its own tests is that a vote outlives the roster: it
/// runs for thirty seconds, which is long enough for two people to disconnect,
/// and every slot index means something different after each one.
/// </para>
/// </remarks>
public sealed class ChatCommandTests
{
    private const byte Hello = 0;
    private const byte GlobalChat = 1;
    private const byte VoteAnswer = 3;

    private const int VoteTicks = 30 * 30;

    /// <summary>
    /// Past <c>voteTimeout</c>, so a vote may be called at all — see
    /// <c>chat_api.gml:175</c> and the first-minute quirk it produces.
    /// </summary>
    private const long AfterCooldown = 60_001;

    private static (FakePluginHost Host, ChatPlugin Plugin) Fresh()
    {
        var (host, plugin) = ChatPluginTests.Fresh();
        host.CurrentTimeMilliseconds = AfterCooldown;
        return (host, plugin);
    }

    private static int Join(FakePluginHost host, ChatPlugin plugin, string name, Team team, string address) =>
        ChatPluginTests.Join(host, plugin, name, team, address);

    private static void Say(ChatPlugin plugin, int slot, string message) =>
        plugin.Receive(slot, ChatPluginTests.Body(GlobalChat, message));

    /// <summary>The client's dialog reporting a 1-based option (<c>vote_api.gml:82-83</c>).</summary>
    private static void Answer(ChatPlugin plugin, int slot, int option) =>
        plugin.Receive(slot, [VoteAnswer, (byte)option]);

    private static void RunTheClockOut(ChatPlugin plugin)
    {
        for (var i = 0; i < VoteTicks; i++)
            plugin.Tick();
    }

    // --- decoding the vote packets -------------------------------------------

    /// <summary>Packet 200 or 204 (<c>vote_api.gml:240-250</c>, <c>plugin.gml:458-472</c>).</summary>
    private static (string Question, string[] Options, int Ticks, int[] Tally)? AsPoll(byte[] p)
    {
        if (p[0] is not (200 or 204))
            return null;

        var at = 1;
        var question = Gg2Text.Decode(p.AsSpan(at + 1, p[at]));
        at += 1 + p[at];

        var count = p[at++];
        var options = new string[count];

        for (var i = 0; i < count; i++)
        {
            options[i] = Gg2Text.Decode(p.AsSpan(at + 1, p[at]));
            at += 1 + p[at];
        }

        var ticks = p[at] | (p[at + 1] << 8);
        at += 2;

        var tally = new int[p[0] == 204 ? count : 0];
        for (var i = 0; i < tally.Length; i++)
            tally[i] = p[at++];

        return (question, options, ticks, tally);
    }

    private static List<(string Question, string[] Options, int Ticks, int[] Tally)> PollsTo(
        FakePluginHost host, int slot)
    {
        var polls = new List<(string, string[], int, int[])>();

        foreach (var (target, data) in host.Sent)
            if (target == slot && AsPoll(data) is { } poll)
                polls.Add(poll);

        return polls;
    }

    /// <summary>Packet 201: the 1-based winner, or 0 to just close the dialog.</summary>
    private static List<int> ResultsTo(FakePluginHost host, int slot)
    {
        var results = new List<int>();

        foreach (var (target, data) in host.Sent)
            if (target == slot && data[0] == 201)
                results.Add(data[1]);

        return results;
    }

    /// <summary>Packet 203: one more vote for a 0-based option.</summary>
    private static List<int> TalliesTo(FakePluginHost host, int slot)
    {
        var tallies = new List<int>();

        foreach (var (target, data) in host.Sent)
            if (target == slot && data[0] == 203)
                tallies.Add(data[1]);

        return tallies;
    }

    private static List<string> TextTo(FakePluginHost host, int slot) =>
        ChatPluginTests.TextTo(host, slot);

    // --- help ----------------------------------------------------------------

    /// <summary><c>commands.gml:25-39</c>: the list, joined with a final "and".</summary>
    [Fact]
    public void HelpListsEveryCommand()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red, "10.0.0.1");

        host.Sent.Clear();
        Say(plugin, alice, "/help");

        var line = Assert.Single(TextTo(host, alice));
        Assert.StartsWith("OAvailable server commands: help, maplist", line, StringComparison.Ordinal);
        Assert.Contains(", votemap", line, StringComparison.Ordinal);

        // The LAST entry, which is what the "a, b and c" join has to get right --
        // and it is this port's own /bots rather than the reference's /votemap.
        Assert.Contains(" and bots", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The client re-sends <c>/help x</c> as <c>/help "x"</c></b>
    /// (<c>commands.gml:16</c>), so a parser that kept the quotes would answer
    /// "does not exist" for every help request a real client makes.
    /// </summary>
    [Fact]
    public void HelpForOneCommandStripsTheQuotesTheClientAdds()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red, "10.0.0.1");

        host.Sent.Clear();
        Say(plugin, alice, "/help \"votekick\"");

        Assert.Equal(
            ["OStarts a vote to kick the selected player. Use /votekick [Name]"],
            TextTo(host, alice));
    }

    /// <summary><c>chat_api.gml:193-200</c>, to the caller and nobody else.</summary>
    [Fact]
    public void AnUnknownCommandIsRefusedToItsCallerAlone()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red, "10.0.0.1");
        var bob = Join(host, plugin, "bob", Team.Blue, "10.0.0.2");

        host.Sent.Clear();
        Say(plugin, alice, "/nonsense");

        Assert.Equal(
            ["OSorry this command does not exist. Type /help for console commands."],
            TextTo(host, alice));

        Assert.Empty(TextTo(host, bob));
    }

    /// <summary>A command is never echoed as chat, even when it is refused.</summary>
    [Fact]
    public void ACommandIsNeverRelayedAsChat()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red, "10.0.0.1");
        var bob = Join(host, plugin, "bob", Team.Blue, "10.0.0.2");

        host.Sent.Clear();
        Say(plugin, alice, "/votekick nobody-by-that-name");

        Assert.Empty(TextTo(host, bob));
    }

    /// <summary>party.gml. "S" is the random colour this plugin's renderer knows.</summary>
    [Fact]
    public void PartyReachesEverybody()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red, "10.0.0.1");
        var bob = Join(host, plugin, "bob", Team.Blue, "10.0.0.2");

        host.Sent.Clear();
        Say(plugin, alice, "/party");

        Assert.Equal(["SParty hard!"], TextTo(host, alice));
        Assert.Equal(["SParty hard!"], TextTo(host, bob));
    }

    // --- the vote-call cooldown ----------------------------------------------

    /// <summary>
    /// <c>chat_api.gml:175</c> tests <c>current_time - voteTimeout &gt;
    /// lastVoteCall</c>, and both start at zero — so no vote can be called until
    /// the server has been up a minute. Transcribed, quirk and all.
    /// </summary>
    [Fact]
    public void NoVoteCanBeCalledInTheServersFirstMinute()
    {
        var (host, plugin) = ChatPluginTests.Fresh();
        var alice = ChatPluginTests.Join(host, plugin, "alice", Team.Red);

        host.Sent.Clear();
        Say(plugin, alice, "/votescramble");

        Assert.Equal(["OPlease wait 60 seconds before starting a vote."], TextTo(host, alice));
        Assert.Empty(PollsTo(host, alice));
    }

    /// <summary>One vote per player per minute (<c>plugin.gml:224</c>).</summary>
    [Fact]
    public void ASecondVoteFromTheSamePlayerInsideTheMinuteIsRefused()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red, "10.0.0.1");

        Say(plugin, alice, "/votescramble");
        RunTheClockOut(plugin);

        host.CurrentTimeMilliseconds += 1000;
        host.Sent.Clear();
        Say(plugin, alice, "/votescramble");

        Assert.Equal(["OPlease wait 59 seconds before starting a vote."], TextTo(host, alice));
    }

    /// <summary>
    /// <c>vote_api.gml:377-385</c>: one vote at a time, whoever calls the second.
    /// </summary>
    [Fact]
    public void ASecondVoteFromAnotherPlayerWhileOneIsRunningIsRefused()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red, "10.0.0.1");
        var bob = Join(host, plugin, "bob", Team.Blue, "10.0.0.2");

        Say(plugin, alice, "/votescramble");

        host.Sent.Clear();
        Say(plugin, bob, "/votenextround");

        var line = Assert.Single(TextTo(host, bob));
        Assert.StartsWith("OYou may not call vote at the moment.", line, StringComparison.Ordinal);
    }

    // --- the dialog ----------------------------------------------------------

    /// <summary>
    /// <c>vote_api.gml:239-259</c>: a poll to everybody in chat who may vote,
    /// carrying the question, the options and the clock in steps.
    /// </summary>
    [Fact]
    public void AVoteOpensTheDialogOnEveryClientInChat()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red, "10.0.0.1");
        var bob = Join(host, plugin, "bob", Team.Blue, "10.0.0.2");

        host.Sent.Clear();
        Say(plugin, alice, "/votescramble");

        foreach (var slot in new[] { alice, bob })
        {
            var poll = Assert.Single(PollsTo(host, slot));
            Assert.Equal("Scramble teams?", poll.Question);
            Assert.Equal(["Yes", "No"], poll.Options);
            Assert.Equal(VoteTicks, poll.Ticks);
        }
    }

    /// <summary>
    /// <c>votekick.gml:40</c>, <c>target.mayVote = false</c>: the player a vote is
    /// about is not shown it and cannot answer it.
    /// </summary>
    [Fact]
    public void AKickVotesTargetIsNotShownItAndCannotAnswer()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red, "10.0.0.1");
        var bob = Join(host, plugin, "bob", Team.Blue, "10.0.0.2");

        host.Sent.Clear();
        Say(plugin, alice, "/votekick bob");

        Assert.Single(PollsTo(host, alice));
        Assert.Empty(PollsTo(host, bob));

        // And an answer sent anyway is dropped: canVote was never set.
        host.Sent.Clear();
        Answer(plugin, bob, 1);
        Assert.Empty(TalliesTo(host, alice));
    }

    /// <summary>
    /// <c>vote_api.gml:182-190</c>: every dialog's bar chart grows as the votes
    /// land, the voter's own included, and the option is 0-based on the wire.
    /// </summary>
    [Fact]
    public void EachAnswerIsBroadcastToEveryOpenDialog()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red, "10.0.0.1");
        var bob = Join(host, plugin, "bob", Team.Blue, "10.0.0.2");
        var carol = Join(host, plugin, "carol", Team.Red, "10.0.0.3");

        Say(plugin, alice, "/votescramble");

        host.Sent.Clear();
        Answer(plugin, alice, 1);
        Answer(plugin, bob, 2);

        Assert.Equal([0, 1], TalliesTo(host, carol));
        Assert.Equal([0, 1], TalliesTo(host, alice));
    }

    /// <summary>One answer per player: <c>canVote</c> is cleared by the first.</summary>
    [Fact]
    public void APlayerMayOnlyAnswerOnce()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red, "10.0.0.1");
        var bob = Join(host, plugin, "bob", Team.Blue, "10.0.0.2");

        Say(plugin, alice, "/votescramble");

        host.Sent.Clear();
        Answer(plugin, alice, 1);
        Answer(plugin, alice, 1);

        Assert.Equal([0], TalliesTo(host, bob));
    }

    /// <summary>
    /// <c>vote_api.gml:200-202</c>: once everybody who could answer has, the
    /// vote does not sit out the rest of its thirty seconds.
    /// </summary>
    [Fact]
    public void AVoteEndsEarlyOnceEverybodyHasAnswered()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red, "10.0.0.1");
        var bob = Join(host, plugin, "bob", Team.Blue, "10.0.0.2");

        Say(plugin, alice, "/votescramble");
        Answer(plugin, alice, 1);
        Answer(plugin, bob, 1);

        host.Sent.Clear();
        plugin.Tick();

        Assert.Equal([1], ResultsTo(host, alice));
    }

    /// <summary>
    /// <c>plugin.gml:455-482</c>: a client arriving mid-vote gets the question,
    /// the clock as it stands and the votes so far.
    /// </summary>
    [Fact]
    public void AClientJoiningMidVoteIsCaughtUp()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red, "10.0.0.1");

        // Two voters, because one answering is everybody answering and would end
        // the vote before anybody could join it.
        var carol = Join(host, plugin, "carol", Team.Red, "10.0.0.3");

        Say(plugin, alice, "/votescramble");
        Answer(plugin, alice, 1);
        plugin.Tick();

        // Join ticks once more before saying hello, hence two ticks off the clock.
        var bob = ChatPluginTests.Join(host, plugin, "bob", Team.Blue, "10.0.0.2");

        var poll = Assert.Single(PollsTo(host, bob));
        Assert.Equal("Scramble teams?", poll.Question);
        Assert.Equal([1, 0], poll.Tally);
        Assert.Equal(VoteTicks - 2, poll.Ticks);

        // And they can answer it, which is the point of being caught up.
        host.Sent.Clear();
        Answer(plugin, bob, 1);
        Assert.Equal([0], TalliesTo(host, carol));
    }

    // --- thresholds ----------------------------------------------------------

    /// <summary>Ten clients in chat, plus the one a vote will be about.</summary>
    private static (FakePluginHost Host, ChatPlugin Plugin, int Caller, int Target, int[] Voters) Crowd()
    {
        var (host, plugin) = Fresh();

        var voters = new int[10];
        for (var i = 0; i < voters.Length; i++)
            voters[i] = Join(host, plugin, $"voter{i}", i % 2 == 0 ? Team.Red : Team.Blue, $"10.0.1.{i}");

        var target = Join(host, plugin, "target", Team.Blue, "10.0.2.1");

        return (host, plugin, voters[0], target, voters);
    }

    private static void AnswerYesThenNo(ChatPlugin plugin, int[] voters, int yes)
    {
        for (var i = 0; i < voters.Length; i++)
            Answer(plugin, voters[i], i < yes ? 1 : 2);
    }

    /// <summary><c>votekick.gml:89</c>: 90%, and not a point less.</summary>
    [Theory]
    [InlineData(9, true)]    // 90%
    [InlineData(8, false)]   // 80%
    public void AKickNeedsNinetyPercent(int yes, bool kicked)
    {
        var (host, plugin, caller, _, voters) = Crowd();

        Say(plugin, caller, "/votekick target");
        AnswerYesThenNo(plugin, voters, yes);
        RunTheClockOut(plugin);

        Assert.Equal(kicked, host.Kicked.Count == 1);

        if (kicked)
            Assert.Equal("target", host.Kicked[0].Name);
    }

    /// <summary>
    /// <c>votemute.gml:92</c>: 70%. <b>A different bar from the kick's, on
    /// purpose</b> — the two rows here and the two above are what say so.
    /// </summary>
    [Theory]
    [InlineData(7, true)]    // 70%
    [InlineData(6, false)]   // 60%
    public void AMuteNeedsSeventyPercent(int yes, bool muted)
    {
        var (host, plugin, caller, target, voters) = Crowd();

        Say(plugin, caller, "/votemute target");
        AnswerYesThenNo(plugin, voters, yes);
        RunTheClockOut(plugin);

        // ⚠️ After the result line, which reaches the target either way: the
        // reference announces the mute before applying it.
        host.Sent.Clear();
        Say(plugin, voters[1], "anybody there");
        Assert.Equal(muted, ChatPluginTests.LinesTo(host, target).Count == 0);
    }

    /// <summary>
    /// <c>votekick.gml:76-82</c>: below the quorum a vote fails whatever the
    /// split, and the dialog is closed rather than shown a winner.
    /// </summary>
    [Fact]
    public void AVoteTooFewPeopleAnsweredFails()
    {
        var (host, plugin, caller, _, voters) = Crowd();

        Say(plugin, caller, "/votekick target");

        // Quorum is half of the ten who could answer; four unanimous yeses are
        // not enough, which a percentage-only check would have passed.
        for (var i = 0; i < 4; i++)
            Answer(plugin, voters[i], 1);

        host.Sent.Clear();
        RunTheClockOut(plugin);

        Assert.Equal(["OVoting failed (not enough people voted)"], TextTo(host, caller));
        Assert.Equal([0], ResultsTo(host, caller));
        Assert.Empty(host.Kicked);
    }

    /// <summary>
    /// <b>The quorum counts players who could vote, not the roster.</b> This
    /// server fills with bots by default and a bot cannot press a key, so
    /// counting them would make every vote on a bot server unreachable — the
    /// feature would look implemented and never once pass.
    /// </summary>
    [Fact]
    public void BotsAreNotCountedTowardsTheQuorum()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red, "10.0.0.1");
        var bob = Join(host, plugin, "bob", Team.Blue, "10.0.0.2");

        for (var i = 0; i < 10; i++)
            ChatPluginTests.Join(host, plugin, $"bot{i}", Team.Red, string.Empty, chat: false, client: false);

        Say(plugin, alice, "/votenextround");
        Answer(plugin, alice, 1);
        Answer(plugin, bob, 1);
        RunTheClockOut(plugin);

        Assert.Equal(1, host.RoundEnds);
    }

    /// <summary>
    /// <c>vote_api.gml:307-310</c>: a decided vote highlights what won, and the
    /// result reaches the player it was about — <c>mayVote</c> is reset before
    /// the result goes out (<c>plugin.gml:240-242</c>).
    /// </summary>
    [Fact]
    public void TheResultPacketNamesTheWinnerAndReachesTheTarget()
    {
        var (host, plugin, caller, target, voters) = Crowd();

        Say(plugin, caller, "/votekick target");
        AnswerYesThenNo(plugin, voters, 3);

        host.Sent.Clear();
        RunTheClockOut(plugin);

        Assert.Equal([2], ResultsTo(host, caller));    // "No" won
        Assert.Equal([2], ResultsTo(host, target));
    }

    // --- what a vote does ----------------------------------------------------

    /// <summary>
    /// <c>votescramble.gml:76-113</c>. <b>With enough players that "moved
    /// nobody" is not also a pass</b>: everybody starts red, so a correct
    /// scramble has to produce blues.
    /// </summary>
    [Fact]
    public void AScrambleSplitsTheTeams()
    {
        var (host, plugin) = Fresh();

        var players = new int[8];
        for (var i = 0; i < players.Length; i++)
            players[i] = Join(host, plugin, $"p{i}", Team.Red, $"10.0.3.{i}");

        var spectator = Join(host, plugin, "watcher", Team.Spectator, "10.0.4.1");

        Say(plugin, players[0], "/votescramble");
        foreach (var slot in players)
            Answer(plugin, slot, 1);

        RunTheClockOut(plugin);

        var blue = 0;
        for (var slot = 0; slot < host.PlayerCount; slot++)
            if (host.TeamOf(slot) == Team.Blue)
                blue++;

        Assert.Equal(4, blue);

        // ⚠️ A spectator is left watching rather than dragged into the game.
        Assert.Equal(Team.Spectator, host.TeamOf(spectator));
    }

    /// <summary>
    /// <c>votemap.gml:204</c>: one map that matched what was typed becomes a
    /// yes/no question.
    /// </summary>
    [Fact]
    public void VoteMapWithOneMatchAsksYesOrNo()
    {
        var (host, plugin) = Fresh();
        host.VotableMaps = ["ctf_truefort", "koth_valley", "arena_montane"];

        var alice = Join(host, plugin, "alice", Team.Red, "10.0.0.1");
        var bob = Join(host, plugin, "bob", Team.Blue, "10.0.0.2");

        host.Sent.Clear();
        Say(plugin, alice, "/votemap valley");

        var poll = Assert.Single(PollsTo(host, alice));
        Assert.Equal("Set next map to koth_valley?", poll.Question);
        Assert.Equal(["Yes", "No"], poll.Options);

        Answer(plugin, alice, 1);
        Answer(plugin, bob, 1);
        RunTheClockOut(plugin);

        Assert.Equal(["koth_valley"], host.NextMaps);
    }

    /// <summary>
    /// <c>votemap.gml:83-131</c>: no match falls back to a shuffled selection,
    /// eight at most, with "keep default" always last.
    /// </summary>
    [Fact]
    public void VoteMapWithNoMatchOffersAShuffledChoiceEndingInKeepDefault()
    {
        var (host, plugin) = Fresh();

        var maps = new string[12];
        for (var i = 0; i < maps.Length; i++)
            maps[i] = $"ctf_map{i}";

        host.VotableMaps = maps;
        var alice = Join(host, plugin, "alice", Team.Red, "10.0.0.1");

        host.Sent.Clear();
        Say(plugin, alice, "/votemap nothing_like_this");

        var poll = Assert.Single(PollsTo(host, alice));
        Assert.Equal(9, poll.Options.Length);
        Assert.Equal("keep default", poll.Options[^1]);
        Assert.Equal(
            "OSelected map not found, using random selection.",
            TextTo(host, alice)[0]);
    }

    /// <summary>
    /// <c>votemap.gml:141-183</c>: "keep default" winning leaves the rotation
    /// alone, and so does nobody voting.
    /// </summary>
    [Fact]
    public void AMapChoiceWonByKeepDefaultChangesNothing()
    {
        var (host, plugin) = Fresh();
        host.VotableMaps = ["ctf_a", "ctf_b"];

        var alice = Join(host, plugin, "alice", Team.Red, "10.0.0.1");
        var bob = Join(host, plugin, "bob", Team.Blue, "10.0.0.2");

        Say(plugin, alice, "/votemap");

        var poll = Assert.Single(PollsTo(host, alice));
        var keepDefault = Array.IndexOf(poll.Options, "keep default") + 1;

        Answer(plugin, alice, keepDefault);
        Answer(plugin, bob, keepDefault);
        RunTheClockOut(plugin);

        Assert.Empty(host.NextMaps);
    }

    /// <summary><c>votemap.gml:42-45</c>: once per round.</summary>
    [Fact]
    public void OnlyOneMapVoteMaySucceedPerRound()
    {
        var (host, plugin) = Fresh();
        host.VotableMaps = ["ctf_a", "ctf_b"];

        var alice = Join(host, plugin, "alice", Team.Red, "10.0.0.1");
        var bob = Join(host, plugin, "bob", Team.Blue, "10.0.0.2");

        Say(plugin, alice, "/votemap ctf_b");
        Answer(plugin, alice, 1);
        Answer(plugin, bob, 1);
        RunTheClockOut(plugin);

        Assert.Equal(["ctf_b"], host.NextMaps);

        host.CurrentTimeMilliseconds += 120_000;
        host.Sent.Clear();
        Say(plugin, bob, "/votemap ctf_a");

        Assert.Equal(["OYou can only vote for the next map once."], TextTo(host, bob));

        // And a new round clears it.
        plugin.MapChanged("ctf_b");
        host.CurrentTimeMilliseconds += 120_000;
        host.Sent.Clear();
        Say(plugin, bob, "/votemap ctf_a");

        Assert.Single(PollsTo(host, bob));
    }

    /// <summary>
    /// A vote still running when the map changes is dropped rather than
    /// resolved: it would otherwise act on a world nobody voted about.
    /// </summary>
    [Fact]
    public void AMapChangeCancelsARunningVote()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red, "10.0.0.1");

        Say(plugin, alice, "/votenextround");
        Answer(plugin, alice, 1);

        host.Sent.Clear();
        plugin.MapChanged("ctf_truefort");
        RunTheClockOut(plugin);

        Assert.Equal([0], ResultsTo(host, alice));
        Assert.Equal(["OThe vote was cancelled by the map change."], TextTo(host, alice));
        Assert.Equal(0, host.RoundEnds);
    }

    /// <summary>
    /// A vote-muted player can reconnect, and comes back unable to talk:
    /// the ban list is keyed on the address (<c>plugin.gml:447</c>).
    /// </summary>
    [Fact]
    public void AVoteMutedPlayerComesBackMuted()
    {
        var (host, plugin, caller, target, voters) = Crowd();

        Say(plugin, caller, "/votemute target");
        AnswerYesThenNo(plugin, voters, 10);
        RunTheClockOut(plugin);

        plugin.PlayerRemoving(target);
        host.RemoveAt(target);

        host.Sent.Clear();
        var again = Join(host, plugin, "target", Team.Blue, "10.0.2.1");

        // No "has joined chat!", and nothing afterwards either.
        Assert.Empty(ChatPluginTests.LinesTo(host, again));

        Say(plugin, voters[1], "anybody there");
        Assert.Empty(ChatPluginTests.LinesTo(host, again));
    }

    // --- the player picker ---------------------------------------------------

    /// <summary>
    /// <c>vote_api.gml:370-406</c>: a vote command with no name opens a numbered
    /// list, as a vote only its caller sees.
    /// </summary>
    [Fact]
    public void AVoteCommandWithNoNameOpensThePickerForItsCallerAlone()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red, "10.0.0.1");
        var bob = Join(host, plugin, "bob", Team.Blue, "10.0.0.2");

        host.SetPoints(bob, 7);

        host.Sent.Clear();
        Say(plugin, alice, "/votekick");

        var poll = Assert.Single(PollsTo(host, alice));
        Assert.Equal("Select a player.", poll.Question);
        Assert.Equal(["Server (0)", "alice (0)", "bob (7)"], poll.Options);
        Assert.Empty(PollsTo(host, bob));
    }

    /// <summary>
    /// ⚠️ <b>Nine options is a hard ceiling</b>, and a full picker page is one of
    /// the two places that reaches it. The client reads at most nine option
    /// strings whatever the count byte says (<c>plugin.gml:545</c>), so a tenth
    /// would leave bytes in the buffer and the clock would be read from the wrong
    /// offset.
    /// </summary>
    [Fact]
    public void AFullPickerPageIsStillNineOptions()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red, "10.0.0.1");

        for (var i = 0; i < 20; i++)
            Join(host, plugin, $"p{i}", Team.Blue, $"10.0.5.{i}");

        host.Sent.Clear();
        Say(plugin, alice, "/votekick");

        var poll = Assert.Single(PollsTo(host, alice));
        Assert.Equal(9, poll.Options.Length);
        Assert.Equal("Next page", poll.Options[^1]);
    }

    /// <summary>
    /// <b>The picker remembers stable ids, not slots.</b> A vote runs for thirty
    /// seconds; if somebody disconnects in between, every index above them has
    /// moved. Store slots instead of stable ids and this targets the wrong
    /// player — which is the whole reason <see cref="IPluginHost.StableIdOf"/>
    /// exists.
    /// </summary>
    [Fact]
    public void ThePickerTargetsTheRightPlayerAfterTheRosterCompacts()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red, "10.0.0.1");
        var bob = Join(host, plugin, "bob", Team.Blue, "10.0.0.2");
        var carol = Join(host, plugin, "carol", Team.Red, "10.0.0.3");
        Join(host, plugin, "dave", Team.Blue, "10.0.0.4");

        Say(plugin, alice, "/votekick");

        // Slots 0 Server, 1 alice, 2 bob, 3 carol, 4 dave: the picker's fourth
        // option is carol.
        plugin.PlayerRemoving(bob);
        host.RemoveAt(bob);

        host.Sent.Clear();
        Answer(plugin, alice, 4);

        // The picker's dialog is cleared first, then the real vote opens.
        Assert.Equal([0], ResultsTo(host, alice));

        var poll = Assert.Single(PollsTo(host, alice));
        Assert.Equal("Kick player carol?", poll.Question);
        Assert.Equal(["Yes", "No"], poll.Options);
    }

    /// <summary>
    /// <c>votekick.gml:23-29</c>: the host is not kickable, and slot 0 is this
    /// server's own reserved player. Picking it from the list is refused rather
    /// than acted on, which is what the reference does too.
    /// </summary>
    [Fact]
    public void PickingTheServersOwnSlotIsRefused()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red, "10.0.0.1");

        Say(plugin, alice, "/votekick");

        host.Sent.Clear();
        Answer(plugin, alice, 1);

        Assert.Equal(["OYou cannot kick the host."], TextTo(host, alice));
        Assert.Empty(PollsTo(host, alice));
    }

    /// <summary>A bot is on the list and is refused for the same reason.</summary>
    [Fact]
    public void PickingABotIsRefused()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red, "10.0.0.1");
        ChatPluginTests.Join(host, plugin, "bot", Team.Blue, string.Empty, chat: false, client: false);

        host.Sent.Clear();
        Say(plugin, alice, "/votekick bot");

        Assert.Equal(["OYou cannot kick a bot."], TextTo(host, alice));
    }

    // --- finding a player ----------------------------------------------------

    /// <summary><c>vote_api.gml:345-357</c>: a substring that matches two is refused.</summary>
    [Fact]
    public void AnAmbiguousNameIsRefusedRatherThanGuessed()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red, "10.0.0.1");
        Join(host, plugin, "bobby", Team.Blue, "10.0.0.2");
        Join(host, plugin, "bobbin", Team.Blue, "10.0.0.3");

        host.Sent.Clear();
        Say(plugin, alice, "/votekick bob");

        Assert.Equal(
            ["OThere are multiple players with this name, please be more specific or use this "
             + "command with no arguments."],
            TextTo(host, alice));
    }

    /// <summary>
    /// <c>vote_api.gml:339-343</c>: an exact name is tried before any substring,
    /// so a "bob" sitting next to a "bobby" is still reachable.
    /// </summary>
    [Fact]
    public void AnExactNameBeatsASubstring()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red, "10.0.0.1");
        Join(host, plugin, "bob", Team.Blue, "10.0.0.2");
        Join(host, plugin, "bobby", Team.Blue, "10.0.0.3");

        host.Sent.Clear();
        Say(plugin, alice, "/votekick bob");

        var poll = Assert.Single(PollsTo(host, alice));
        Assert.Equal("Kick player bob?", poll.Question);
    }

    // --- maplist -------------------------------------------------------------

    /// <summary>
    /// <c>commands.gml:128-158</c>, starting from the current map when no offset
    /// is given. The header's range is the reference's and is off by one — it
    /// claims eleven and lists ten.
    /// </summary>
    [Fact]
    public void MapListStartsAtTheCurrentMap()
    {
        var (host, plugin) = Fresh();
        host.MapRotation = ["a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k", "l"];
        host.CurrentMapIndex = 1;

        var alice = Join(host, plugin, "alice", Team.Red, "10.0.0.1");

        host.Sent.Clear();
        Say(plugin, alice, "/maplist");

        Assert.Equal(
            ["OMaplist (2-12/12): b - c - d - e - f - g - h - i - j - k"],
            TextTo(host, alice));
    }

    /// <summary>A server with no rotation says so rather than indexing past the end.</summary>
    [Fact]
    public void MapListOnASingleMapServerSaysSo()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red, "10.0.0.1");

        host.Sent.Clear();
        Say(plugin, alice, "/maplist");

        Assert.Equal(["OThis server is not rotating maps."], TextTo(host, alice));
    }

    /// <summary>
    /// <c>chat_api.gml:7-28</c>: a line the SERVER composes that is longer than
    /// the length prefix becomes several packets rather than being truncated.
    /// Nothing a client can send exercises this — 255 bytes is all one client
    /// packet holds — so a long map list is the test for it.
    /// </summary>
    [Fact]
    public void ALongMapListArrivesAsSeveralPackets()
    {
        var (host, plugin) = Fresh();

        var rotation = new string[10];
        for (var i = 0; i < rotation.Length; i++)
            rotation[i] = $"ctf_a_map_with_a_very_long_name_{i}";

        host.MapRotation = rotation;
        var alice = Join(host, plugin, "alice", Team.Red, "10.0.0.1");

        host.Sent.Clear();
        Say(plugin, alice, "/maplist");

        var lines = TextTo(host, alice);
        Assert.Equal(2, lines.Count);
        Assert.Equal(255, lines[0].Length);
        Assert.Contains(rotation[9], string.Concat(lines), StringComparison.Ordinal);
    }

    // --- /bots ---------------------------------------------------------------
    //
    // ⚠️ This port's own command, so there is no GML to compare against and no
    // fixture that could gate it. What the tests hold instead is the set of
    // replies: "no such command here", "nothing to suspend", "already off" and
    // "done" are four different outcomes, and every one of them is a state a
    // player can reach by typing three words.

    /// <summary>
    /// Off by default: a server that has not set <c>[Bots] SuspendMinutes</c>
    /// says the command does not exist here.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>The refusal is the default and it has to be legible.</b> Silence, or
    /// "this command does not exist", would be a player concluding the server is
    /// broken; nothing in the repo would go red either way.
    /// </remarks>
    [Fact]
    public void BotsIsRefusedUntilTheServerConfiguresAWindow()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red, "10.0.0.1");

        host.Sent.Clear();
        Say(plugin, alice, "/bots off");

        Assert.Equal(
            ["OThis server does not let bots be turned off from chat."],
            TextTo(host, alice));

        Assert.Empty(host.Suspensions);
    }

    /// <summary>
    /// <c>/bots off</c> suspends, announces it to everybody, and <c>/bots on</c>
    /// ends it.
    /// </summary>
    /// <remarks>
    /// <b>The announcement goes to everybody and the suspension is asked of the
    /// HOST.</b> A version of this that only replied to the caller would leave the
    /// other players watching the bots vanish with no explanation, and a version
    /// that composed a reply without calling <c>SuspendBots</c> would pass any
    /// test that read only the text.
    /// </remarks>
    [Fact]
    public void BotsOffSuspendsAndBotsOnEndsIt()
    {
        var (host, plugin) = Fresh();
        host.SuspendMinutes = 15;
        host.BotsManaged = true;
        host.BotCount = 6;

        var alice = Join(host, plugin, "alice", Team.Red, "10.0.0.1");
        var bob = Join(host, plugin, "bob", Team.Blue, "10.0.0.2");

        host.Sent.Clear();
        Say(plugin, alice, "/bots off");

        Assert.Equal([15], host.Suspensions);
        Assert.Equal(0, host.BotCount);

        var announcement = Assert.Single(TextTo(host, bob));
        Assert.Contains("alice turned the bots off for 15 minutes", announcement, StringComparison.Ordinal);

        host.Sent.Clear();
        Say(plugin, bob, "/bots on");

        Assert.Equal(1, host.Resumes);
        Assert.Contains(
            "bob turned the bots back on",
            Assert.Single(TextTo(host, alice)),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A second <c>/bots off</c> inside a running window does NOT re-arm it.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>The whole bound on the feature is that the window ends.</b> Re-arming
    /// would make one player typing three words every few minutes an indefinite
    /// switch, and the suspension would then be exactly the thing an operator
    /// cannot see a reason for.
    /// </remarks>
    [Fact]
    public void ASecondBotsOffDoesNotExtendTheWindow()
    {
        var (host, plugin) = Fresh();
        host.SuspendMinutes = 15;
        host.BotsManaged = true;
        host.BotCount = 6;

        var alice = Join(host, plugin, "alice", Team.Red, "10.0.0.1");

        Say(plugin, alice, "/bots off");
        host.BotSuspendSecondsLeft = 300;

        host.Sent.Clear();
        Say(plugin, alice, "/bots off");

        Assert.Equal([15], host.Suspensions);
        Assert.Equal(
            ["OBots are already off for another 5 minutes."],
            TextTo(host, alice));
    }

    /// <summary>
    /// A server not running bots is told so, rather than told the suspension
    /// worked.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>"There are no bots right now" and "this server does not run bots"
    /// are different.</b> A suspension on a server whose population manager is off
    /// changes nothing a player can see — including a bot an operator added by
    /// hand, which no suspension removes — so reporting success would be the
    /// command lying about what it did.
    /// </remarks>
    [Fact]
    public void BotsOffOnAServerWithNoPopulationManagerIsRefused()
    {
        var (host, plugin) = Fresh();
        host.SuspendMinutes = 15;
        host.BotsManaged = false;

        var alice = Join(host, plugin, "alice", Team.Red, "10.0.0.1");

        host.Sent.Clear();
        Say(plugin, alice, "/bots off");

        Assert.Equal(["OThis server is not running bots."], TextTo(host, alice));
        Assert.Empty(host.Suspensions);
    }

    /// <summary>Bare <c>/bots</c> reports, and reports the suspension while one runs.</summary>
    [Fact]
    public void BareBotsReportsThePopulation()
    {
        var (host, plugin) = Fresh();
        host.SuspendMinutes = 15;
        host.BotsManaged = true;
        host.BotCount = 4;

        var alice = Join(host, plugin, "alice", Team.Red, "10.0.0.1");

        host.Sent.Clear();
        Say(plugin, alice, "/bots");

        Assert.Equal(
            ["O4 bots are playing. /bots off takes them away for 15 minutes."],
            TextTo(host, alice));

        host.BotSuspendSecondsLeft = 61;
        host.Sent.Clear();
        Say(plugin, alice, "/bots");

        // ⚠️ Rounded UP. 61 seconds left reported as "1 minute" would count down
        // to "0 minutes" with the bots still away, which reads as a broken clock.
        Assert.Equal(
            ["OBots are off for another 2 minutes. /bots on brings them back."],
            TextTo(host, alice));
    }

    /// <summary><c>/bots on</c> with nothing suspended says so rather than nothing.</summary>
    [Fact]
    public void BotsOnWithNothingSuspendedSaysSo()
    {
        var (host, plugin) = Fresh();
        host.SuspendMinutes = 15;
        host.BotsManaged = true;

        var alice = Join(host, plugin, "alice", Team.Red, "10.0.0.1");

        host.Sent.Clear();
        Say(plugin, alice, "/bots on");

        Assert.Equal(["OThe bots are not turned off."], TextTo(host, alice));
        Assert.Equal(0, host.Resumes);
    }

    /// <summary>
    /// It is in <c>/help</c>, which is the only way a player finds out it exists.
    /// </summary>
    /// <remarks>
    /// <b>The command table and the dispatch are two lists that can disagree.</b>
    /// A command that runs and is not listed is one nobody discovers; one that is
    /// listed and does not run answers "this command does not exist" to a player
    /// who read the help.
    /// </remarks>
    [Fact]
    public void BotsIsListedInHelp()
    {
        var (host, plugin) = Fresh();
        var alice = Join(host, plugin, "alice", Team.Red, "10.0.0.1");

        host.Sent.Clear();
        Say(plugin, alice, "/help");

        Assert.Contains("bots", string.Concat(TextTo(host, alice)), StringComparison.Ordinal);

        host.Sent.Clear();
        Say(plugin, alice, "/help \"bots\"");

        Assert.Contains(
            "/bots off",
            string.Concat(TextTo(host, alice)),
            StringComparison.Ordinal);
    }
}
