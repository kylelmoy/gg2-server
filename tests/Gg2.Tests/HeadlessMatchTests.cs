using Gg2.Bots;
using Gg2.Maps;
using Gg2.Protocol;

namespace Gg2.Tests;

/// <summary>
/// <c>docs/BOTAI.md</c> item 8 and phase 8's match runner: N independent matches
/// in one process, no sockets, no sleeping, a fixed seed, <b>and identical
/// results regardless of how many threads it used</b>.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>That last clause is the one with teeth, and it is a constraint on what
/// a match may share.</b> <c>NavSearch</c> is deliberately not thread-safe — its
/// scratch is reused across queries — so two matches sharing one would
/// interleave two frontiers into a single <c>gScore</c> and produce routes that
/// are <em>wrong rather than slow</em>. That failure is invisible by
/// construction: a slightly worse bot, in a subset of runs, for no discoverable
/// reason. Which is why it is gated by comparing whole result arrays rather than
/// by a summary statistic.
/// </para>
/// <para>
/// Matches here are deliberately short. A full 15-minute round is ~27,000 ticks
/// and about 0.75s; these run a couple of thousand, which still has every bot
/// planning routes, fighting and dying, and keeps the suite fast.
/// </para>
/// </remarks>
public sealed class HeadlessMatchTests
{
    private static HeadlessMatchOptions Short => HeadlessMatchOptions.Default with
    {
        Bots = 6,
        MaxTicks = 900,
    };

    private static LevelMap Map(string name = "ctf_truefort") =>
        LevelMap.FromPng(name, ReferenceTree.ReadMap(name)).ForArea(1);

    /// <summary>
    /// The map and its nav graph.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A match without a nav graph is not a weaker test, it is a
    /// different one — and for these it is a vacuous one.</b> Bots with no graph
    /// do not walk anywhere, so on a map the size of <c>ctf_truefort</c> the two
    /// teams never meet: every match comes out
    /// <c>Exhausted, 0-0, MaxTicks</c> whatever the seed, and "reproducible" and
    /// "different seeds differ" become trivially true and trivially false
    /// respectively. Measured — all four seed-sensitive tests here failed that
    /// way before this existed.
    /// </remarks>
    private static (LevelMap Map, NavGraph Nav) Navigable(string name = "ctf_truefort")
    {
        ReferenceTree.RequireNavCache();
        return (Map(name), NavCache.Load(ReferenceTree.NavCacheDirectory, name, 1));
    }

    /// <summary>
    /// ⚠️ The load-bearing test: the same batch on one thread and on eight gives
    /// the same array, element for element.
    /// </summary>
    /// <remarks>
    /// Whole results, not a summary: a runner that appended instead of writing
    /// to <c>results[i]</c> would give the right win counts in the wrong order,
    /// and a test that only compared win rates would pass on it.
    /// </remarks>
    [Fact]
    public void ABatchGivesTheSameAnswersOnOneThreadAndOnEight()
    {
        ReferenceTree.RequireAvailable();

        var (map, nav) = Navigable();

        var sequential = MatchRunner.Run(Short, count: 6, map, nav, threads: 1);
        var parallel = MatchRunner.Run(Short, count: 6, map, nav, threads: 8);

        Assert.Equal(sequential, parallel);
    }

    /// <summary>
    /// And the matches in a batch actually differ, so the test above is not
    /// satisfied by six identical matches.
    /// </summary>
    /// <remarks>
    /// ⚠️ Without this, a runner that ignored the index and played the same
    /// match six times would pass every determinism assertion here perfectly.
    /// </remarks>
    [Fact]
    public void MatchesInABatchAreNotAllTheSameMatch()
    {
        ReferenceTree.RequireAvailable();

        var (map, nav) = Navigable();
        var results = MatchRunner.Run(Short, count: 6, map, nav, threads: 1);

        Assert.True(
            results.Distinct().Count() > 1,
            "every match in the batch came out identical, so the seed is not reaching them");
    }

    /// <summary>One seed, one match, however many times it is played.</summary>
    [Fact]
    public void AMatchIsReproducibleFromItsSeed()
    {
        ReferenceTree.RequireAvailable();

        var (map, nav) = Navigable();

        var first = HeadlessMatch.On(Short, map, nav).Run();
        var second = HeadlessMatch.On(Short, map, nav).Run();

        Assert.Equal(first, second);
    }

    /// <summary>
    /// ⚠️ And <see cref="HeadlessMatch.Reset"/> really resets: a match played,
    /// reset and played again gives what it gave the first time.
    /// </summary>
    /// <remarks>
    /// This is the environment contract phase 9 needs — a trainer resets and
    /// replays constantly — and it is a stronger claim than "a fresh match is
    /// reproducible", because it says nothing survives a reset. The RNG, every
    /// bot's own stream, the roster and the world all have to go back.
    /// </remarks>
    [Fact]
    public void ResetPutsAMatchBackToItsFirstTick()
    {
        ReferenceTree.RequireAvailable();

        var (map, nav) = Navigable();
        var match = HeadlessMatch.On(Short, map, nav);

        var first = match.Run();

        match.Reset();
        Assert.Equal(0, match.Tick);
        Assert.Equal(MatchOutcome.Running, match.Outcome);

        var second = match.Run();

        Assert.Equal(first, second);
    }

    /// <summary>A different seed is a different match.</summary>
    /// <remarks>
    /// The control for reproducibility: without it, a match that ignored its
    /// seed entirely would satisfy every equality above.
    /// </remarks>
    [Fact]
    public void ADifferentSeedIsADifferentMatch()
    {
        ReferenceTree.RequireAvailable();

        var (map, nav) = Navigable();

        var first = HeadlessMatch.On(Short with { Seed = 1 }, map, nav).Run();
        var second = HeadlessMatch.On(Short with { Seed = 999 }, map, nav).Run();

        Assert.NotEqual(first, second);
    }

    /// <summary>
    /// A match fills its roster, spawns everybody and plays: bots are alive,
    /// moving and dying.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Without this the determinism tests are satisfied by a match that
    /// does nothing at all</b> — reproducibly and on any number of threads. It
    /// asserts deaths rather than captures because a capture needs a carrier to
    /// survive the whole way home, which over 900 ticks it usually does not:
    /// measured over 9,000 ticks with twelve bots, both teams reach the enemy
    /// flag (red to x=4966 against a base at 4962) and grab it five times
    /// between them, and still cap zero times. Fighting is the thing that
    /// happens on every tick.
    /// </remarks>
    [Fact]
    public void AMatchActuallyPlays()
    {
        ReferenceTree.RequireAvailable();

        var (map, nav) = Navigable();
        var match = HeadlessMatch.On(Short, map, nav);

        var deaths = 0;
        var alive = 0;

        for (var tick = 0; tick < 900; tick++)
        {
            match.Step();

            for (var slot = 1; slot <= Short.Bots; slot++)
            {
                if (match.Events.Died[slot])
                    deaths++;
            }
        }

        for (var slot = 1; slot <= Short.Bots; slot++)
        {
            if (match.State.IsAlive(slot))
                alive++;
        }

        Assert.True(alive > 0, "nobody was alive at the end of the match");
        Assert.True(deaths > 0, "nobody died in 900 ticks, so the bots are not fighting");
    }

    /// <summary>
    /// A match with no nav cache still runs — the bots simply do not walk
    /// anywhere.
    /// </summary>
    /// <remarks>
    /// ⚠️ <c>navCacheLoad.gml</c>'s own contract, and the same answer
    /// <c>ScenarioWorld</c> and <c>GameServer</c> give: a missing cache is "this
    /// map does not navigate", never an error. The nav caches are gitignored in
    /// the reference and are not shipped, so a fresh checkout has none — which
    /// makes this the case CI actually runs.
    /// </remarks>
    [Fact]
    public void AMatchWithoutANavCacheStillRuns()
    {
        ReferenceTree.RequireAvailable();

        var result = HeadlessMatch.On(Short, Map(), nav: null).Run();

        Assert.Equal(MatchOutcome.Exhausted, result.Outcome);
        Assert.Equal(900, result.Ticks);
    }

    /// <summary>
    /// ⚠️ A defaulted <see cref="MatchResult"/> must not read as "red won".
    /// </summary>
    /// <remarks>
    /// <c>Team.Red</c> is 0, so a zeroed result names red the winner — the same
    /// family of trap as <c>CharacterState.Nobody</c> being 255 rather than 0.
    /// <see cref="MatchOutcome.Running"/> is likewise 0, which is what makes the
    /// pair safe: the winner is only meaningful once the outcome says
    /// <see cref="MatchOutcome.Decided"/>.
    /// </remarks>
    [Fact]
    public void ADefaultedResultIsNotARedWin()
    {
        var defaulted = default(MatchResult);

        Assert.Equal(MatchOutcome.Running, defaulted.Outcome);
        Assert.NotEqual(MatchOutcome.Decided, defaulted.Outcome);
    }

    /// <summary>
    /// ⚠️ <see cref="HeadlessMatchOptions.Default"/> is not <c>new()</c>, and the
    /// difference is a match that silently does nothing.
    /// </summary>
    /// <remarks>
    /// A record struct's primary-constructor defaults do not apply to the
    /// zeroing constructor. The same trap as <c>BotPopulationOptions.Default</c>
    /// and <c>CharacterState.Fresh</c>; it is asserted because the failure is
    /// silent every time.
    /// </remarks>
    [Fact]
    public void DefaultOptionsAreNotAZeroedStruct()
    {
        var zeroed = default(HeadlessMatchOptions);

        Assert.Null(zeroed.Map);
        Assert.Equal(0, zeroed.Bots);
        Assert.Equal(0, zeroed.MaxTicks);

        var real = HeadlessMatchOptions.Default;

        Assert.NotNull(real.Map);
        Assert.True(real.Bots > 0);
        Assert.True(real.MaxTicks > 0);
    }

    /// <summary>
    /// Explicit teams put exactly that many bots on each side.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>This is what makes a bot change measurable against another</b> —
    /// "red runs the new one, blue runs the old" is the comparison
    /// <c>docs/BOTAI.md</c> item 8 exists for, and an evenly-filled match cannot
    /// express it. It is also what settled whether the bots could return a flag
    /// at all: <c>RedBots: 6, BlueBots: 0</c> is an uncontested attack.
    /// </remarks>
    [Theory]
    [InlineData(6, 0)]
    [InlineData(1, 5)]
    [InlineData(3, 3)]
    public void ExplicitTeamsArePlacedExactly(int red, int blue)
    {
        ReferenceTree.RequireAvailable();

        var match = HeadlessMatch.On(
            Short with { RedBots = red, BlueBots = blue },
            Map(),
            nav: null);

        var placedRed = 0;
        var placedBlue = 0;

        // Slot 0 is the server's own reserved player and is always a spectator.
        for (var slot = 1; slot < red + blue + 1; slot++)
        {
            if (match.State.Players[slot].Team == Team.Red)
                placedRed++;
            else if (match.State.Players[slot].Team == Team.Blue)
                placedBlue++;
        }

        Assert.Equal(red, placedRed);
        Assert.Equal(blue, placedBlue);
    }

    /// <summary>
    /// ⚠️ Zero on a side is a value, not "unspecified" — which is why the
    /// fields are nullable.
    /// </summary>
    /// <remarks>
    /// An uncontested attack is <c>RedBots: 6, BlueBots: 0</c>, and if 0 meant
    /// "fill this side evenly" that experiment could not be written at all. This
    /// codebase's own rule is that a zeroed field meaning "off" is a trap; here
    /// the nullable says "unset" and 0 says 0.
    /// </remarks>
    [Fact]
    public void AnEmptyTeamIsDifferentFromAnUnspecifiedOne()
    {
        ReferenceTree.RequireAvailable();

        Assert.False(HeadlessMatchOptions.Default.HasExplicitTeams);
        Assert.True((HeadlessMatchOptions.Default with { BlueBots = 0 }).HasExplicitTeams);

        var uncontested = HeadlessMatch.On(
            Short with { RedBots = 4, BlueBots = 0 },
            Map(),
            nav: null);

        for (var slot = 1; slot <= 4; slot++)
            Assert.Equal(Team.Red, uncontested.State.Players[slot].Team);
    }

    /// <summary>The summary reports draws and unfinished matches rather than folding them away.</summary>
    /// <remarks>
    /// ⚠️ A bot change that makes capturing worse shows up FIRST as more
    /// unfinished matches. A summary that only counted red against blue would
    /// hide it, and the win rate would drift for a reason nobody could find.
    /// </remarks>
    [Fact]
    public void TheSummaryKeepsDrawsAndUnfinishedMatchesSeparate()
    {
        MatchResult[] results =
        [
            new(MatchOutcome.Decided, Team.Red, 5, 0, 100, 0),
            new(MatchOutcome.Decided, Team.Blue, 0, 5, 200, 0),
            new(MatchOutcome.Decided, Team.Spectator, 1, 1, 300, 0),
            new(MatchOutcome.Exhausted, Team.Spectator, 0, 0, 400, 0),
        ];

        var summary = MatchRunner.Summarise(results);

        Assert.Equal(4, summary.Matches);
        Assert.Equal(1, summary.RedWins);
        Assert.Equal(1, summary.BlueWins);
        Assert.Equal(1, summary.Draws);
        Assert.Equal(1, summary.Exhausted);
        Assert.Equal(1000, summary.TotalTicks);
        Assert.Equal(250, summary.MeanTicks);
    }

    /// <summary>
    /// ⚠️ Two batches with adjacent seeds are two different experiments.
    /// </summary>
    /// <remarks>
    /// <c>seed + index</c> would make <c>{seed: 1, count: 100}</c> and
    /// <c>{seed: 2, count: 100}</c> share 99 of their 100 matches, so a
    /// "repeat with a different seed" would be almost the same measurement —
    /// and the second batch would look like confirmation of the first.
    /// </remarks>
    [Fact]
    public void AdjacentBatchSeedsDoNotOverlap()
    {
        ReferenceTree.RequireAvailable();

        var (map, nav) = Navigable();

        var first = MatchRunner.Run(Short with { Seed = 1 }, count: 4, map, nav);
        var second = MatchRunner.Run(Short with { Seed = 2 }, count: 4, map, nav);

        Assert.Empty(first.Intersect(second));
    }
}
