using Gg2.Bots;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// The policy continuation: the search keeps ACTING past its commitment.
/// </summary>
/// <remarks>
/// <c>gg2-rl</c>'s step 3, and protocol v8's reason for existing. Both of its
/// neighbours are measured and fail for opposite reasons — a long horizon sees
/// damage land and picks an implausible plan; a neutral continuation picks a
/// plausible plan and sees nothing land. Only a continuation that keeps acting
/// separates lookahead from commitment.
/// <para>
/// ⚠️ <b>These tests gate the MECHANISM, not the result.</b> Whether a policy
/// continuation is better is a 1,008-duel measurement in another repo, and no
/// test here can or should answer it. What is checked is that the knob is wired:
/// that it changes the rollout, that it runs on the cadence rather than every
/// tick, that it is per side, and that a misphased commitment is refused.
/// </para>
/// </remarks>
public sealed class ContinuationTests
{
    private static PolicyNetwork Policy() => PolicyFixture.Load();

    /// <summary>
    /// Two soldiers 80px apart on <c>ctf_truefort</c> — close enough that a rocket
    /// lands inside a 20-tick horizon.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The distance is load-bearing and the first version of these tests got
    /// it wrong.</b> At 160px on <c>gg_debug</c> every candidate scores exactly
    /// zero in BOTH arms, so a difference test cannot fail and a
    /// no-difference test passes vacuously. A rocket travels ~13px a tick, so a
    /// gap a rocket cannot cross inside the horizon makes the whole fixture
    /// blind — the same "what is the check's population?" question this repo has
    /// asked twice today, arriving in a scenario rather than in a corpus.
    /// </remarks>
    private static ScenarioWorld World(out int red)
    {
        var world = ScenarioWorld.Load("ctf_truefort", 1, ReferenceTree.MapsDirectory, null);

        red = world.Place(PlayerClass.Soldier, Team.Red, 600, 850);
        world.Place(PlayerClass.Soldier, Team.Blue, 680, 850);
        world.Step(20);

        return world;
    }

    /// <summary>
    /// A policy continuation changes what the search decides.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The load-bearing test, and it is a difference rather than a value.</b>
    /// Asserting a particular chosen action would pin this build's checkpoint and
    /// go red on any retrain; asserting that the scores MOVE says the policy is
    /// actually driving the rollout. ⚠️ <b>Mutation-checked by construction</b>:
    /// if <c>ContinuationPolicy</c> were ignored the two score arrays would be
    /// identical, which is exactly what this fails on.
    /// </remarks>
    [Fact]
    public void APolicyContinuationChangesTheScores()
    {
        var world = World(out var red);
        var view = new WorldView(in world.State, world.Collision);

        var neutral = new RolloutSearch(new RolloutOptions(
            HorizonTicks: 20, DecisionTicks: 4, CommitTicks: 4));

        var policy = new RolloutSearch(new RolloutOptions(
            HorizonTicks: 20, DecisionTicks: 4, CommitTicks: 4, ContinuationPolicy: Policy()));

        neutral.Poll(in view, red, 0);
        policy.Poll(in view, red, 0);

        Assert.Equal(neutral.LastScores.Length, policy.LastScores.Length);
        Assert.NotEmpty(neutral.LastScores.ToArray());

        var same = true;
        var policyTotal = 0.0;

        for (var i = 0; i < neutral.LastScores.Length; i++)
        {
            policyTotal += policy.LastScores[i];

            if (neutral.LastScores[i] != policy.LastScores[i])
                same = false;
        }

        Assert.False(
            same,
            "every candidate scored identically with and without a policy continuation, "
            + "so the policy is not driving the rollout at all");

        // ⚠️ Guards the direction as well as the difference. A fixture where every
        // candidate scores zero in both arms would fail the assertion above for
        // the right reason and pass it for the wrong one if the arms ever differed; this
        // says the policy arm actually made something happen.
        Assert.True(
            policyTotal != 0,
            "the policy arm scored zero across every candidate, so nothing landed and "
            + "this fixture cannot tell a working continuation from a broken one");

        // ⚠️ AND the neutral arm scores exactly nothing here, which is gg2-rl's
        // measured diagnosis reproduced mechanically: a neutral continuation picks
        // a plausible plan and then sees nothing land, because a candidate scores
        // from damage inside the window where somebody is still acting.
        var neutralTotal = 0.0;

        foreach (var score in neutral.LastScores)
            neutralTotal += score;

        Assert.Equal(0, neutralTotal);
    }

    /// <summary>
    /// The commitment is still played exactly, whatever continues after it.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The continuation must not leak backwards.</b> A rollout is scored
    /// over the whole horizon, but only the first <c>DecisionTicks</c> of the
    /// winning candidate are ever played — so if a continuation changed what
    /// happens INSIDE the commitment, the search would be choosing an action it
    /// then does not take. Same shape as the receding-horizon split itself.
    /// </remarks>
    [Fact]
    public void TheCommitmentIsUnaffectedByWhatContinuesAfterIt()
    {
        var world = World(out var red);
        var view = new WorldView(in world.State, world.Collision);

        // CommitTicks == HorizonTicks: the continuation branch is unreachable, so
        // a policy must make no difference whatsoever.
        var neutral = new RolloutSearch(new RolloutOptions(
            HorizonTicks: 20, DecisionTicks: 4, CommitTicks: 20));

        var policy = new RolloutSearch(new RolloutOptions(
            HorizonTicks: 20, DecisionTicks: 4, CommitTicks: 20, ContinuationPolicy: Policy()));

        neutral.Poll(in view, red, 0);
        policy.Poll(in view, red, 0);

        for (var i = 0; i < neutral.LastScores.Length; i++)
        {
            Assert.True(
                neutral.LastScores[i] == policy.LastScores[i],
                $"candidate {i} scored differently with a policy attached, but the commitment "
                + "covers the whole horizon so the continuation branch is never reached");
        }
    }

    /// <summary>
    /// ⚠️ A commitment that is not a whole number of decisions is REFUSED.
    /// </summary>
    /// <remarks>
    /// At the point the value is SET, which is <c>RolloutOptions.WithDefaults</c>
    /// — not asserted where it is read. <c>gg2-rl</c> asked for the guard and then
    /// sharpened its own first proposal (a read-site assert) to get it here: a fix
    /// belongs where it makes the mistake unavailable.
    /// </remarks>
    [Theory]
    [InlineData(6, 4)]
    [InlineData(10, 4)]
    [InlineData(3, 2)]
    public void AMisphasedCommitmentIsRefused(int commit, int decision)
    {
        var thrown = Assert.Throws<ArgumentException>(() =>
            new RolloutSearch(new RolloutOptions(
                HorizonTicks: 20, DecisionTicks: decision, CommitTicks: commit)));

        Assert.Contains("not a multiple", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠️ <b>But NOT when the commitment covers the whole horizon.</b>
    /// </summary>
    /// <remarks>
    /// The first version of the guard got this wrong and refused eight existing
    /// configurations. <c>HorizonTicks: 10, DecisionTicks: 4</c> defaults
    /// <c>CommitTicks</c> to 10, which is not a multiple of 4 — and it does not
    /// matter, because no continuation runs. A guard that fires where the thing it
    /// protects does not exist is the mirror image of a gate that can never fire,
    /// and both are the same mistake about what a check is for.
    /// </remarks>
    [Theory]
    [InlineData(10, 4)]
    [InlineData(6, 4)]
    [InlineData(25, 10)]
    public void AWholeHorizonCommitmentIsNeverMisphased(int horizon, int decision)
    {
        // CommitTicks left at 0, so it defaults to the horizon.
        var search = new RolloutSearch(new RolloutOptions(
            HorizonTicks: horizon, DecisionTicks: decision));

        Assert.NotNull(search);
    }

    /// <summary>
    /// A continuation is a decision on the cadence, not a decision every tick.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>This is a cost constraint, not a preference, and it is the reason the
    /// experiment is affordable at all.</b> Measured 2026-09-01: one policy
    /// decision is 6.58 µs, so at k=20/horizon 20 a per-tick continuation costs
    /// 3.48 ms against a 2.45 ms unpruned baseline — more than the search it
    /// replaces — while cadence 4 costs 1.47 ms.
    /// <para>
    /// Checked by contrast rather than by counting calls: at
    /// <c>DecisionTicks: 1</c> the continuation re-decides every tick and at
    /// <c>DecisionTicks: 4</c> it does not, so the two must score differently.
    /// A cadence that was ignored would make them identical.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheContinuationRunsOnTheDecisionCadence()
    {
        var world = World(out var red);
        var view = new WorldView(in world.State, world.Collision);

        var everyTick = new RolloutSearch(new RolloutOptions(
            HorizonTicks: 20, DecisionTicks: 1, CommitTicks: 4, ContinuationPolicy: Policy()));

        var everyFourth = new RolloutSearch(new RolloutOptions(
            HorizonTicks: 20, DecisionTicks: 4, CommitTicks: 4, ContinuationPolicy: Policy()));

        everyTick.Poll(in view, red, 0);
        everyFourth.Poll(in view, red, 0);

        var same = true;

        for (var i = 0; i < Math.Min(everyTick.LastScores.Length, everyFourth.LastScores.Length); i++)
            if (everyTick.LastScores[i] != everyFourth.LastScores[i])
                same = false;

        Assert.False(
            same,
            "cadence 1 and cadence 4 produced identical scores, so the continuation is not "
            + "running on DecisionTicks — it is deciding on the same ticks either way");
    }

    /// <summary>
    /// The continuation does not draw from the match's RNG stream.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>CLAUDE.md's first rule for the bot layer.</b> The match's stream is
    /// observable simulation — every seeded weapon puts its draw on the wire and
    /// every client re-derives the spread from it — so a search whose continuation
    /// consumed draws would make the stream's position depend on how many
    /// candidates were scored, and every client would desynchronise.
    /// <para>
    /// The fork is a copy, so a draw inside it cannot move the real stream by
    /// construction; what this checks is the live world after a decision, which is
    /// the thing a client can see.
    /// </para>
    /// </remarks>
    [Fact]
    public void AContinuationDoesNotDisturbTheMatchsRandomStream()
    {
        var world = World(out var red);
        var view = new WorldView(in world.State, world.Collision);
        var before = world.State.Random;

        var search = new RolloutSearch(new RolloutOptions(
            HorizonTicks: 20, DecisionTicks: 4, CommitTicks: 4, ContinuationPolicy: Policy()));

        search.Poll(in view, red, 0);

        Assert.Equal(before, world.State.Random);
    }
}
