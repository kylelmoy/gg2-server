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

                // Candidate 0 of the enumeration: no movement, no jump, no fire,
                // aim bin 0 — hard left — and no zoom.
                Assert.Equal(0, red.Action.Horizontal);
                Assert.Equal(0, red.Action.Jump);
                Assert.Equal(0, red.Action.Fire);
                Assert.Equal(0, red.Action.AimDelta);
                Assert.Equal(0, red.Action.Zoom);
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

        Assert.True(
            shortHorizon > 0.5,
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
