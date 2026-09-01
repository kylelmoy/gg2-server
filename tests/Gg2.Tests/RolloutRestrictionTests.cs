using Gg2.Bots;
using Gg2.Env;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// <see cref="RolloutSearch.RestrictNextDecision"/> — `gg2-rl`'s DESIGN.md step 3.
/// </summary>
/// <remarks>
/// A distilled policy prunes the search's candidates so it is stronger at equal
/// cost. Measured there: the policy's top 8 of 60 contains this search's best
/// action 78.2% of the time against 55.9% for a random 8.
/// <para>
/// ⚠️ <b>Two of the properties here are load-bearing and would be invisible to
/// any test that only checked the winner.</b> The restriction is a SET, so the
/// decision must not depend on the order the indices arrive in; and a class swap
/// changes the candidate list between 60 and 120 entries, where index i names a
/// different action — so a stale mask must be dropped rather than reused.
/// </para>
/// </remarks>
public sealed class RolloutRestrictionTests
{
    private const string Map = "gg_debug";

    private static ScenarioWorld World(out int red)
    {
        var world = ScenarioWorld.Load(Map, 1, ReferenceTree.IncludedFiles, null);

        red = world.Place(PlayerClass.Soldier, Team.Red, 600, 850);
        world.Place(PlayerClass.Soldier, Team.Blue, 760, 850);

        for (var i = 0; i < 20; i++)
            world.Step();

        return world;
    }

    private static RolloutSearch Search() => new(new RolloutOptions(DecisionTicks: 1));

    /// <summary>
    /// Every way of asking "how many candidates" gives the same answer.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>The gate on the CLASS of bug, not on the instance of it.</b> Shipping
    /// candidate pruning shipped a third source for this number — the validator
    /// asked the live <c>RolloutSearch.CandidateCount</c>, which is the set
    /// currently built and reads 60 for a sniper until its first decision, while
    /// <c>Candidates</c> and the score block both said 120. `gg2-rl` found it in
    /// an hour; every test here passed over it.
    /// <para>
    /// ⚠️ <b>An arithmetic count that drifted from the loop that builds the list
    /// would be the same bug wearing a different hat</b>, which is why this
    /// compares the two rather than asserting either against a literal.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(PlayerClass.Scout)]
    [InlineData(PlayerClass.Soldier)]
    [InlineData(PlayerClass.Sniper)]
    [InlineData(PlayerClass.Medic)]
    [InlineData(PlayerClass.Heavy)]
    [InlineData(PlayerClass.Spy)]
    [InlineData(PlayerClass.Engineer)]
    [InlineData(PlayerClass.Pyro)]
    [InlineData(PlayerClass.Demoman)]
    public void EveryCountSourceAgrees(PlayerClass playerClass)
    {
        foreach (var options in new[]
        {
            new RolloutOptions(),
            new RolloutOptions(ConsiderSpecial: true),
            new RolloutOptions(HorizonTicks: 45, DecisionTicks: 1),
        })
        {
            Assert.Equal(
                RolloutSearch.CandidatesFor(options, playerClass).Length,
                RolloutSearch.CandidateCountFor(options, playerClass));
        }
    }

    /// <summary>
    /// And a duel answers the same number for a slot BEFORE its first step.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Before the first step is the whole point</b> — a restriction arrives
    /// WITH step 0, so a count that only becomes right after a search has run is
    /// right too late. That is exactly how the sniper bug presented.
    /// </remarks>
    [Theory]
    [InlineData(PlayerClass.Sniper, 120)]
    [InlineData(PlayerClass.Soldier, 60)]
    public void ADuelKnowsItsCandidateCountBeforeSteppingOnce(PlayerClass playerClass, int expected)
    {
        var env = new DuelEnv(ReferenceTree.IncludedFiles);

        env.Reset(
            new DuelSpec
            {
                Map = Map,
                RedClass = playerClass,
                BlueClass = PlayerClass.Soldier,
                RedX = 600,
                RedY = 850,
                BlueX = 760,
                BlueY = 850,
                DecisionCap = 40,
                ActionRepeat = 2,
                RedDriver = EnvDriver.Rollout,
                RedHorizonTicks = 6,
            },
            seed: 3);

        Assert.Equal(expected, env.CandidateCountFor(env.RedSlot));
        Assert.Equal(env.CandidatesFor(env.RedSlot).Length, env.CandidateCountFor(env.RedSlot));

        // A non-searching slot has no candidates and no count.
        Assert.Equal(0, env.CandidateCountFor(env.BlueSlot));
    }

    [Fact]
    public void AnEmptyRestrictionSearchesEverything()
    {
        var world = World(out var red);
        var search = Search();
        var view = new WorldView(in world.State, world.Collision);

        search.RestrictNextDecision([], PlayerClass.Soldier);
        search.Poll(in view, red, 0);

        Assert.Equal(search.CandidateCount, search.LastScores.Length);
        Assert.Equal(search.CandidateCount, search.LastSearchedIndices.Length);

        // Identity, so a caller that ignores the indices is right until somebody prunes.
        for (var i = 0; i < search.CandidateCount; i++)
            Assert.Equal(i, search.LastSearchedIndices[i]);
    }

    [Fact]
    public void ARestrictedSearchScoresOnlyThoseCandidates()
    {
        var world = World(out var red);
        var search = Search();
        var view = new WorldView(in world.State, world.Collision);
        int[] keep = [7, 2, 40, 2, 19];

        search.RestrictNextDecision(keep, PlayerClass.Soldier);
        search.Poll(in view, red, 0);

        // Duplicates collapse; ascending order regardless of how they arrived.
        Assert.Equal([2, 7, 19, 40], search.LastSearchedIndices.ToArray());
        Assert.Equal(4, search.LastScores.Length);
        Assert.Equal(4, search.LastCandidates.Length);

        // The candidates handed back are the ones named, not a prefix.
        var all = RolloutSearch.CandidatesFor(new RolloutOptions(DecisionTicks: 1), PlayerClass.Soldier);

        Assert.Equal(all[2], search.LastCandidates[0]);
        Assert.Equal(all[40], search.LastCandidates[3]);
    }

    /// <summary>
    /// The cost accounting follows what was searched, not what could have been.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The whole point of a restriction is that it costs less.</b> A figure
    /// counted from the candidate list would report a budget nobody spent, and
    /// <c>docs/BOTAI.md</c> item 9 names this as the number to watch.
    /// </remarks>
    [Fact]
    public void TheTicksSimulatedFollowTheRestriction()
    {
        var world = World(out var red);
        var search = new RolloutSearch(new RolloutOptions(HorizonTicks: 10, DecisionTicks: 1));
        var view = new WorldView(in world.State, world.Collision);

        search.Poll(in view, red, 0);

        Assert.Equal(search.CandidateCount * 10L, search.LastDecisionTicksSimulated);

        search.RestrictNextDecision([1, 2, 3], PlayerClass.Soldier);
        search.Poll(in view, red, 1);

        Assert.Equal(30L, search.LastDecisionTicksSimulated);
    }

    /// <summary>
    /// The decision is a pure function of the SET, not of the order it arrived in.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>This is the property that would rot silently.</b> The tie-break is
    /// lexicographic — damage, then aim error — and `gg2-rl` measured <b>85.2%</b>
    /// of non-blind decisions with their top two candidates tied, so iteration
    /// order settles a great many decisions. A search whose answer depended on how
    /// a client happened to sort its indices would be irreproducible, and every
    /// other test here would still pass.
    /// </remarks>
    [Fact]
    public void TheOrderTheIndicesArriveInDoesNotMatter()
    {
        var random = new Random(4);

        for (var trial = 0; trial < 40; trial++)
        {
            var world = World(out var red);
            var view = new WorldView(in world.State, world.Collision);

            var keep = new int[8];

            for (var i = 0; i < keep.Length; i++)
                keep[i] = random.Next(60);

            var forward = Search();
            forward.RestrictNextDecision(keep, PlayerClass.Soldier);
            var a = forward.Poll(in view, red, 0);

            var shuffled = (int[])keep.Clone();

            for (var i = shuffled.Length - 1; i > 0; i--)
            {
                var j = random.Next(i + 1);
                (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
            }

            var reversed = Search();
            reversed.RestrictNextDecision(shuffled, PlayerClass.Soldier);
            var b = reversed.Poll(in view, red, 0);

            Assert.Equal(a, b);
            Assert.Equal(forward.LastSearchedIndices.ToArray(), reversed.LastSearchedIndices.ToArray());
        }
    }

    /// <summary>
    /// A restricted search picks the best of the subset, and it is the same score
    /// the unrestricted search gave that candidate.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Restricting must not change what a candidate is WORTH</b>, only which
    /// ones are looked at. A rollout that behaved differently under a restriction
    /// would make every pruned distribution incomparable with an unpruned one, and
    /// the distillation corpus would silently mix two scales.
    /// </remarks>
    [Fact]
    public void ARestrictedScoreEqualsTheUnrestrictedOne()
    {
        var world = World(out var red);
        var view = new WorldView(in world.State, world.Collision);

        var full = Search();
        full.Poll(in view, red, 0);

        var reference = full.LastScores.ToArray();

        int[] keep = [3, 11, 27, 44];

        var pruned = Search();
        pruned.RestrictNextDecision(keep, PlayerClass.Soldier);
        pruned.Poll(in view, red, 0);

        for (var i = 0; i < keep.Length; i++)
        {
            var index = pruned.LastSearchedIndices[i];

            Assert.Equal(reference[index], pruned.LastScores[i]);
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(60)]
    [InlineData(int.MaxValue)]
    public void AnOutOfRangeIndexIsRefused(int index)
    {
        var search = Search();

        Assert.Throws<ArgumentOutOfRangeException>(() => search.RestrictNextDecision([0, index], PlayerClass.Soldier));
    }

    /// <summary>
    /// A refused restriction leaves the search unrestricted rather than half-set.
    /// </summary>
    [Fact]
    public void ARefusedRestrictionSearchesEverything()
    {
        var world = World(out var red);
        var search = Search();
        var view = new WorldView(in world.State, world.Collision);

        Assert.Throws<ArgumentOutOfRangeException>(() => search.RestrictNextDecision([1, 2, 999], PlayerClass.Soldier));

        search.Poll(in view, red, 0);

        Assert.Equal(search.CandidateCount, search.LastScores.Length);
    }

    /// <summary>
    /// A restriction is dropped when the candidate set changes size under it.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>The silent failure this whole feature is most exposed to.</b> Only a
    /// sniper's set carries the zoom dimension, so it holds 120 candidates against
    /// everyone else's 60 — and <c>BuildCandidates</c> interleaves zoom inside
    /// each (aim, fire) pair, so index <c>i</c> in the 120-list is a DIFFERENT
    /// action from index <c>i</c> in the 60-list. A mask that merely fit would
    /// search six wrong actions and report plausible numbers for them.
    /// <para>
    /// ⚠️ Going 120 → 60 is the dangerous direction, because the stale mask is
    /// large enough to index. That is the direction tested here.
    /// </para>
    /// </remarks>
    [Fact]
    public void AClassChangeDropsAStaleRestriction()
    {
        var world = ScenarioWorld.Load(Map, 1, ReferenceTree.IncludedFiles, null);
        var sniper = world.Place(PlayerClass.Sniper, Team.Red, 600, 850);
        world.Place(PlayerClass.Soldier, Team.Blue, 760, 850);

        for (var i = 0; i < 20; i++)
            world.Step();

        var search = Search();
        var view = new WorldView(in world.State, world.Collision);

        // A sniper first, so the set is 120 and a restriction can name index 100.
        search.Poll(in view, sniper, 0);
        Assert.Equal(120, search.CandidateCount);

        search.RestrictNextDecision([100, 101, 102], PlayerClass.Sniper);

        // Now the same slot is a soldier: 60 candidates, and the mask is stale.
        world.State.Characters[sniper].Class = PlayerClass.Soldier;

        search.Poll(in view, sniper, 1);

        Assert.Equal(60, search.CandidateCount);
        Assert.Equal(60, search.LastScores.Length);
    }

    /// <summary>
    /// A restriction survives until a decision actually searches.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Only the first tick of a decision searches</b> — a decision is held
    /// for <c>DecisionTicks</c> — so a restriction set mid-decision must apply to
    /// the next search rather than be dropped on a tick that was never going to
    /// look at it.
    /// </remarks>
    [Fact]
    public void ARestrictionWaitsForTheNextSearch()
    {
        var world = World(out var red);
        var search = new RolloutSearch(new RolloutOptions(DecisionTicks: 4));
        var view = new WorldView(in world.State, world.Collision);

        search.Poll(in view, red, 0);
        Assert.Equal(search.CandidateCount, search.LastScores.Length);

        search.RestrictNextDecision([5, 6], PlayerClass.Soldier);

        // Ticks 1-3 are held, not searched, and must not consume the restriction.
        search.Poll(in view, red, 1);
        search.Poll(in view, red, 2);
        search.Poll(in view, red, 3);

        Assert.Equal(search.CandidateCount, search.LastScores.Length);

        search.Poll(in view, red, 4);

        Assert.Equal(2, search.LastScores.Length);
        Assert.Equal([5, 6], search.LastSearchedIndices.ToArray());
    }

    /// <summary>And it is consumed, not sticky.</summary>
    [Fact]
    public void ARestrictionAppliesToOneDecisionOnly()
    {
        var world = World(out var red);
        var search = Search();
        var view = new WorldView(in world.State, world.Collision);

        search.RestrictNextDecision([5, 6], PlayerClass.Soldier);
        search.Poll(in view, red, 0);
        Assert.Equal(2, search.LastScores.Length);

        search.Poll(in view, red, 1);
        Assert.Equal(search.CandidateCount, search.LastScores.Length);
    }
}
