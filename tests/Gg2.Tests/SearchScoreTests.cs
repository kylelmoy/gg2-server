using Gg2.Bots;
using Gg2.Env;
using Gg2.Protocol;

namespace Gg2.Tests;

/// <summary>
/// <see cref="StepResult.BestScore"/> — the field that labels a blind decision.
/// </summary>
/// <remarks>
/// ⚠️ <b>A third of a distillation corpus was the search's tie-break rather than
/// its judgment, and nothing about it looked wrong.</b> <c>RolloutSearch</c>'s own
/// remark has always said that when nothing can score inside the horizon every
/// candidate ties at exactly 0, the tie goes to the first, and the first is
/// <c>[0,0,0,0,0,0]</c> — "turn hard left, do nothing". <c>gg2-rl</c> measured
/// what that does to training data. ⚠️ <b>Its first estimate, by matching the row
/// value, was 35.2% — and the truth measured with THIS FIELD is 70.9%.</b> The
/// heuristic ran at 93.5% precision and <b>46.4% recall</b>: right about what it
/// flagged, missing more than half. Filtering on it would have dropped 35% of the
/// corpus, left another 35% of exactly the same poison in place, and produced a
/// dataset that then LOOKED clean.
/// <para>
/// It missed them because the tie-break has <b>two faces</b>: with the env asking
/// for an action every 2 ticks and <c>RolloutSearch</c> re-planning every 4, half
/// the blind rows are candidate 0 and half are "no turn" — the in-between
/// decisions, where the measured action is whatever the character was already
/// holding. Matching the cadence collapses 1,960 rows to 989 with the blind
/// fraction unchanged, which is how the duplicates were identified at all.
/// </para>
/// <para>
/// By class, with the real signal: scout (refire 8) <b>32.7%</b>, heavy (12)
/// 46.8%, soldier (30) 80.5%, <b>sniper (40) 92.8%</b>. A sniper corpus at
/// horizon 20 is not noisy, it is noise — one row in fourteen carries a decision.
/// </para>
/// <para>
/// Every one of those rows is <b>valid</b>: correct action, correctly recorded,
/// 99.8% exact. Cloning them teaches a policy to turn hard left and freeze
/// whenever it is unsure, and it would have shown up as an inexplicable leftward
/// bias <em>after</em> a training run. ⚠️ <b>A consumer cannot filter them by
/// value</b> — a genuinely chosen "hold still" is sensible for a sniper at range
/// and is indistinguishable from the fallback. The score is what makes them
/// separable.
/// </para>
/// </remarks>
public sealed class SearchScoreTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static DuelSpec Spec(EnvDriver red) => new()
    {
        Map = "gg_debug",
        RedClass = PlayerClass.Soldier,
        BlueClass = PlayerClass.Soldier,
        RedX = 600,
        RedY = 850,
        BlueX = 760,
        BlueY = 850,
        DecisionCap = 40,
        ActionRepeat = 4,
        RedDriver = red,
        BlueDriver = EnvDriver.Idle,
    };

    private static DuelEnv Env() =>
        new(ReferenceTree.IncludedFiles, ReferenceTree.NavCacheDirectory);

    /// <summary>
    /// ✅ <b>Zero means blind, and it really happens</b> — a fresh soldier's
    /// refire is 30 ticks against a 20-tick horizon, so its first decisions score
    /// nothing and take candidate 0.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Asserted together with the ACTION</b>, because the claim is not "a
    /// score of zero occurs" but "a score of zero is exactly the state in which
    /// the action is the enumeration's first candidate". Checking the score alone
    /// would pass on a search that scored zero and still chose sensibly.
    /// </remarks>
    [Fact]
    public void AZeroScoreIsExactlyTheTieBreakAction()
    {
        ReferenceTree.RequireAvailable();
        ReferenceTree.RequireNavCache();

        var env = Env();
        env.Reset(Spec(EnvDriver.Rollout), seed: 11);

        var blind = 0;
        var scoring = 0;

        for (var i = 0; i < 40; i++)
        {
            var (red, _) = env.Step(EnvAction.Idle, EnvAction.Idle);

            Assert.False(double.IsNaN(red.BestScore), "a rollout slot reported no score");

            if (red.BestScore == 0)
            {
                blind++;

                // ⚠️ RE-RECORDED 2026-08-29 and the change is the point. This
                // used to assert aim bin 0 — hard LEFT — because ties went to the
                // enumeration's first candidate. RolloutSearch now breaks ties on
                // aim error, so a blind decision points at the enemy instead:
                // bin 7, no turn, from a duel that starts facing.
                //
                // ⚠️ So gg2-rl's [0,0,0,0,0,0] corpus signature is GONE, and a
                // blind row is no longer identifiable by its action at all — only
                // by this score being zero. That makes BestScore load-bearing
                // rather than convenient.
                Assert.Equal(0, red.Action.Horizontal);
                Assert.Equal(0, red.Action.Jump);
                Assert.Equal(0, red.Action.Fire);
                Assert.Equal(ActionDecoder.AimCentreBin, red.Action.AimDelta);
            }
            else
            {
                scoring++;
            }

            if (red.Done)
                break;
        }

        output.WriteLine($"{blind} blind decisions, {scoring} scoring");

        // Both halves have to occur or this proves nothing: all-blind would mean
        // the duel never engaged, all-scoring that the trap does not arise here.
        Assert.True(blind > 0, "no decision was blind; this scenario cannot demonstrate the trap");
        Assert.True(scoring > 0, "every decision was blind; the search never engaged at all");
    }

    /// <summary>
    /// ⚠️ NaN for every driver that is not a search, because zero is the value
    /// that carries meaning.
    /// </summary>
    /// <remarks>
    /// The same reasoning as <see cref="DuelSpec.Sampled"/>: a sentinel is safe
    /// only where no real value can collide with it. Reporting 0 for a client or a
    /// scripted slot would make "this slot did not search" indistinguishable from
    /// "this search found nothing", which is the one distinction the field exists
    /// to draw.
    /// </remarks>
    [Theory]
    [InlineData(EnvDriver.Client)]
    [InlineData(EnvDriver.Idle)]
    [InlineData(EnvDriver.Scripted)]
    public void ANonSearchSlotReportsNaNRatherThanZero(EnvDriver driver)
    {
        ReferenceTree.RequireAvailable();
        ReferenceTree.RequireNavCache();

        var env = Env();
        env.Reset(Spec(driver) with { RedTier = 3 }, seed: 11);

        var (red, _) = env.Step(EnvAction.Idle, EnvAction.Idle);

        Assert.True(
            double.IsNaN(red.BestScore),
            $"a {driver} slot reported {red.BestScore}; only a search has a score");
    }

    /// <summary>
    /// ⚠️ <b>The horizon reaches the search, and it is the blind fraction that
    /// moves</b> — which is the number the knob exists for.
    /// </summary>
    /// <remarks>
    /// <c>gg2-rl</c> needs to sweep blind-fraction against horizon and could not:
    /// <c>DuelEnv</c> answered <see cref="EnvDriver.Rollout"/> with
    /// <c>new RolloutSearch()</c> and its defaults. With
    /// <see cref="DuelSpec.RedHorizonTicks"/> the curve is measurable.
    /// <para>
    /// ⚠️ <b>Asserted through the BLIND FRACTION rather than through a getter.</b>
    /// A test that read the horizon back would gate the assignment, which is the
    /// shape that passed while <c>--nav</c> was being dropped a layer up. A
    /// soldier's refire is 30 ticks: at a horizon of 20 nothing can fire inside it
    /// and almost every decision scores zero; at 60 most can.
    /// </para>
    /// <para>
    /// ⚠️ <b>The default is UNCHANGED and this test does not argue for changing
    /// it.</b> Which value ships is a decision that should follow
    /// <c>gg2-rl</c>'s curves — blind fraction and win rate — rather than precede
    /// them.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheHorizonReachesTheSearchAndMovesTheBlindFraction()
    {
        ReferenceTree.RequireAvailable();
        ReferenceTree.RequireNavCache();

        var shortHorizon = BlindFraction(20);
        var longHorizon = BlindFraction(60);

        output.WriteLine($"horizon 20: {shortHorizon:0.00} blind, horizon 60: {longHorizon:0.00}");

        // ⚠️ RE-RECORDED 2026-08-29: this bound was 0.5 and the measurement was
        // 0.84. The aim tie-break dropped it to 0.29 — better aim lands more shots
        // inside the horizon, so fewer decisions score nothing. That is a second-
        // order benefit of the tie-break and it is the direction phase 2 wants.
        Assert.True(
            shortHorizon > 0.2,
            $"a 20-tick horizon left only {shortHorizon:0.00} of a soldier's decisions blind; "
            + "the scenario does not demonstrate the trap");

        Assert.True(
            longHorizon < shortHorizon / 2,
            $"the horizon did not move the blind fraction ({shortHorizon:0.00} -> {longHorizon:0.00}); "
            + "it is not reaching RolloutSearch");
    }

    /// <summary>What share of a rollout side's decisions scored exactly zero.</summary>
    private static double BlindFraction(int horizon)
    {
        var env = Env();
        env.Reset(Spec(EnvDriver.Rollout) with { RedHorizonTicks = horizon }, seed: 11);

        var blind = 0;
        var total = 0;

        for (var i = 0; i < 40; i++)
        {
            var (red, _) = env.Step(EnvAction.Idle, EnvAction.Idle);

            total++;
            if (red.BestScore == 0)
                blind++;

            if (red.Done)
                break;
        }

        return blind / (double)total;
    }

    /// <summary>
    /// ✅ <b>A shadow search grades a client-driven slot without driving it</b> —
    /// DAgger's aggregation step.
    /// </summary>
    /// <remarks>
    /// The policy acts, the search grades, and the next training round sees the
    /// states the policy actually reaches. Built because three other levers each
    /// bought single digits against a 56-point cloning gap — more data, better
    /// labels, a better expert — leaving <b>which states the labels are for</b> as
    /// the only untested one.
    /// <para>
    /// ⚠️ <b>Both halves are asserted and the second is the one that can silently
    /// fail.</b> A shadow that drove would be a Rollout slot wearing a different
    /// name, and the symptom would be a corpus labelled for states the policy
    /// never chose — the exact defect it exists to remove. So this checks the
    /// client's action is what actually reached the character.
    /// </para>
    /// </remarks>
    [Fact]
    public void AShadowSearchGradesWithoutDriving()
    {
        ReferenceTree.RequireAvailable();
        ReferenceTree.RequireNavCache();

        var env = Env();
        env.Reset(Spec(EnvDriver.Client) with { RedShadowSearch = true }, seed: 11);

        // A deliberate, unmistakable client action: hold RIGHT and do not turn.
        var action = EnvAction.Idle with { Horizontal = 2 };

        var graded = 0;
        var startX = env.World.State.Characters[env.RedSlot].X;

        for (var i = 0; i < 12; i++)
        {
            var (red, _) = env.Step(action, EnvAction.Idle);

            // ⚠️ THE CLIENT STILL DRIVES, asserted on the CHARACTER rather than on
            // the reported action. `ActionTaken` echoes what a client SENT without
            // consulting the world, so `red.Action` would read "right" even if the
            // shadow had taken the wheel — the first draft of this test asserted
            // exactly that and a mutation making the shadow drive sailed through
            // it. The key mask the simulation actually saw is the claim.
            Assert.True(
                (env.World.State.Characters[env.RedSlot].KeyState & PlayerKeys.Right) != 0,
                "the character is not holding RIGHT; something other than the client is driving it");

            // ⚠️ AND THE SEARCH GRADED. A client slot reports NaN without a
            // shadow; with one it reports a real score.
            Assert.False(double.IsNaN(red.BestScore), "the shadow did not grade this step");

            if (red.BestScore != 0)
                graded++;

            if (red.Done)
                break;
        }

        Assert.True(graded > 0, "every shadow grade was blind; this scenario cannot show one");

        // And it went somewhere, so "holding RIGHT" was not a mask nothing acted on.
        Assert.True(
            env.World.State.Characters[env.RedSlot].X > startX + 20,
            "the character never moved right; the client's action did not reach the world");

        // The distribution is available for the client slot, which is the label a
        // soft-target trainer needs.
        Assert.Equal(60, env.ScoresFor(env.RedSlot).Length);
        Assert.Equal(60, env.CandidatesFor(env.RedSlot).Length);

        // ⚠️ And the graded action is the SEARCH's, not the client's — that is the
        // whole product. If these were equal by construction the shadow would be
        // reporting the policy back to itself.
        Assert.NotEqual(action, env.SearchActionFor(env.RedSlot));
    }

    /// <summary>⚠️ A shadow on a driving search is refused, not doubled.</summary>
    /// <remarks>
    /// It would run a second search whose answer the first already reports, at
    /// double the cost, silently. A caller asking for that has a bug.
    /// </remarks>
    [Fact]
    public void AShadowOnANonClientSideIsRefused()
    {
        var spec = Spec(EnvDriver.Rollout) with { RedShadowSearch = true };

        Assert.Throws<ArgumentException>(spec.Validate);
    }

    /// <summary>
    /// The score reported is the search's own, not a recomputation.
    /// </summary>
    /// <remarks>
    /// ⚠️ It is read off <c>RolloutSearch.LastBestScore</c>, which is the argmax
    /// of <c>LastScores</c> — asserted in <c>RolloutSearchTests</c>. Tying the
    /// wire field to the same number here is what stops the two drifting into
    /// separate answers to the same question, which is this session's recurring
    /// failure.
    /// </remarks>
    [Fact]
    public void TheReportedScoreIsTheSearchsOwn()
    {
        ReferenceTree.RequireAvailable();
        ReferenceTree.RequireNavCache();

        var env = Env();
        env.Reset(Spec(EnvDriver.Rollout), seed: 11);

        for (var i = 0; i < 12; i++)
        {
            var (red, _) = env.Step(EnvAction.Idle, EnvAction.Idle);

            var search = (RolloutSearch)env.BotFor(env.RedSlot)!;
            Assert.Equal(search.LastBestScore, red.BestScore);

            if (red.Done)
                break;
        }
    }
}
