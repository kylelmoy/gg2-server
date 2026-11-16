using GG2.Bots;
using GG2.Host;
using GG2.Server;

namespace GG2.Tests;

/// <summary>
/// The two halves of "how much controller work can this tick afford": a cost
/// model that tracks population, and a budget that calibrates itself to the
/// machine.
/// </summary>
/// <remarks>
/// <b>Nothing here sleeps or reads a clock.</b> <see cref="MicroBudgetController"/>
/// is handed pressure that has already been measured and counts its window in
/// ticks, so every assertion is exact. A test that produced real load to trigger
/// a cut would be asserting things about the build agent.
/// </remarks>
public sealed class MicroBudgetTests
{
    private const double Interval = 1000.0 / 30.0;

    // ------------------------------------------------------------------
    // (a) the cost model
    // ------------------------------------------------------------------

    private static SearchController Search() => new(new RolloutOptions(Threads: 1));

    /// <summary>
    /// The model reproduces the measured sweep to within 5%.
    /// </summary>
    /// <remarks>
    /// <c>BENCH_SEARCH</c>, horizon 20, 60 candidates, <c>gg_debug</c>, Release,
    /// 2026-09-03: <b>1831 / 2624 / 3617 / 5116 µs</b> at 2 / 6 / 12 / 20 live
    /// characters. Least squares gives <c>1490 + 181·N</c>.
    /// <para>
    /// <b>Compared as a SHAPE, not as absolute microseconds.</b> The sweep is on
    /// <c>gg_debug</c> and the model is anchored to <c>ctf_truefort</c>'s 4,795 at
    /// twelve, so the two differ by a constant factor of about a third — which is
    /// exactly the machine-and-map factor the model is not trying to capture and
    /// <see cref="MicroBudgetController"/> exists to absorb. Normalising at the
    /// reference population is what makes this a test of the model rather than of
    /// which map the bench ran on.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(2, 1831)]
    [InlineData(6, 2624)]
    [InlineData(12, 3617)]
    [InlineData(20, 5116)]
    public void TheCostModelReproducesTheMeasuredSweep(int population, double measuredMicroseconds)
    {
        const double MeasuredAtReference = 3617.0;

        var controller = Search();

        var modelled = controller.DecisionCostFor(population)
            / (double)controller.DecisionCostFor(SearchController.ReferencePopulation);

        var measured = measuredMicroseconds / MeasuredAtReference;

        Assert.True(
            Math.Abs(modelled - measured) / measured < 0.05,
            $"at {population} live characters the model says {modelled:0.000} of the reference "
            + $"cost and the bench measured {measured:0.000}");
    }

    /// <summary>
    /// Cost is AFFINE in the population, not proportional to it.
    /// </summary>
    /// <remarks>
    /// The distinction is the whole reason the sweep was run. A proportional
    /// model fitted through the one existing point would read
    /// <c>4795 × N / 12</c>, which at two live characters is 799 µs against a
    /// measured shape of about 2,400 — <b>3× low exactly where a duel or a
    /// two-character scenario lives</b>. This pins that the intercept survives.
    /// </remarks>
    [Fact]
    public void HalvingThePopulationDoesNotHalveTheCost()
    {
        var controller = Search();

        var atTwelve = controller.DecisionCostFor(12);
        var atSix = controller.DecisionCostFor(6);

        Assert.True(atSix > atTwelve * 0.5, $"{atSix} is proportional to {atTwelve}, not affine");
        Assert.True(atSix < atTwelve, $"{atSix} did not fall at all from {atTwelve}");

        // The flat part is 41% of the reference cost, so an empty world still
        // costs something — a rollout forks candidates whether or not anyone is
        // alive to simulate.
        Assert.True(controller.DecisionCostFor(0) > 0);
    }

    /// <summary>The reference point is preserved exactly, so nothing recalibrates.</summary>
    [Fact]
    public void TheReferencePopulationStillCostsTheReferenceCost()
    {
        Assert.Equal(
            SearchController.ReferenceCost,
            Search().DecisionCostFor(SearchController.ReferencePopulation));
    }

    /// <summary>A policy forward pass does not care how many characters are alive.</summary>
    /// <remarks>
    /// The control on the whole model: if BOTH controllers scaled, the ratio
    /// the arbiter actually uses would be unchanged and the change would be
    /// inert.
    /// </remarks>
    [Fact]
    public void ThePolicyCostIsFlatInThePopulation()
    {
        var policy = new SimPolicyController(PolicyFixture.Load(), [GG2.Protocol.PlayerClass.Soldier]);

        Assert.Equal(policy.DecisionCostFor(2), policy.DecisionCostFor(20));
    }

    /// <summary>
    /// The arbiter admits fewer searches on a fuller server, off one budget.
    /// </summary>
    /// <remarks>
    /// <b>This is what the model is FOR.</b> Before it, one constant meant a
    /// 20-character server was handed the same number of decisions as a
    /// 2-character one while each cost 2.8× more — the budget said the same thing
    /// and meant something different.
    /// </remarks>
    [Fact]
    public void AFullerServerGetsFewerSearchDecisionsFromTheSameBudget()
    {
        var arbiter = new DecisionQuotaArbiter(DecisionQuotaArbiter.DefaultTieredBudgetMicroseconds);
        IReadOnlyList<IMicroController> tiers = [Search()];

        Assert.True(Admitted(arbiter, tiers, liveCharacters: 2)
            > Admitted(arbiter, tiers, liveCharacters: 20));
    }

    private static int Admitted(
        DecisionQuotaArbiter arbiter, IReadOnlyList<IMicroController> tiers, int liveCharacters)
    {
        var granted = 0;

        for (var slot = 1; slot <= 16; slot++)
        {
            // One tick, many bots asking: the case the arbiter exists for.
            if (arbiter.Choose(tick: liveCharacters, slot, tiers, eligible: 1, liveCharacters) < 0)
                continue;

            granted++;
            arbiter.Admitted(liveCharacters, slot, 0, tiers[0].DecisionCostFor(liveCharacters));
        }

        return granted;
    }

    // ------------------------------------------------------------------
    // (b) the self-calibrating budget
    // ------------------------------------------------------------------

    private static MicroBudgetController Budget(int ceiling = 10_000, int window = 90) =>
        new(ceiling, MicroBudgetController.PolicyFloorUnits, MicroBudgetController.DefaultStepUnits, window);

    [Fact]
    public void ItStartsAtTheCeilingAndSaysSo()
    {
        var budget = Budget();

        Assert.Equal(10_000, budget.Units);
        Assert.False(budget.IsThrottled);
    }

    /// <summary>A tick that crowds its deadline halves the budget, immediately.</summary>
    /// <remarks>
    /// <b>Immediately, not at the window close.</b> A server in trouble should
    /// stop being in trouble on the next tick rather than in thirty seconds.
    /// </remarks>
    [Fact]
    public void PressureCutsTheBudgetAtOnce()
    {
        var budget = Budget();

        var moved = budget.Observe(1, Interval * 0.95, Interval);

        Assert.NotNull(moved);
        Assert.Equal(5_000, budget.Units);
        Assert.True(budget.IsThrottled);
    }

    /// <summary>
    /// One bad burst does not collapse the budget to the floor.
    /// </summary>
    /// <remarks>
    /// The cut is once per window. Without that guard a single stall — a GC
    /// pause, a map load — would take a healthy server from 10,000 to the floor
    /// in eleven ticks, and it would then spend ten minutes climbing back.
    /// </remarks>
    [Fact]
    public void ABurstCutsOnceNotOnEveryTick()
    {
        var budget = Budget(window: 90);

        for (var tick = 1; tick <= 50; tick++)
            budget.Observe(tick, Interval * 0.99, Interval);

        Assert.Equal(5_000, budget.Units);
    }

    /// <summary>Sustained pressure walks it down, one cut a window.</summary>
    [Fact]
    public void SustainedPressureWalksItDownAWindowAtATime()
    {
        var budget = Budget(window: 10);
        var seen = new List<int>();

        // Long enough to REACH the floor. Halving from 10,000 to 7 is eleven
        // cuts and the guard allows one a window, so sixty ticks stops at 78 —
        // which is the controller working, not failing. Asserting the floor needs
        // the ticks the floor actually takes.
        for (var tick = 1; tick <= 200; tick++)
        {
            budget.Observe(tick, Interval * 0.99, Interval);
            seen.Add(budget.Units);
        }

        // 10000 -> 5000 -> 2500 -> ... and it must stop at the floor rather than
        // reaching zero, which would starve the cheap tier too.
        Assert.Equal(MicroBudgetController.PolicyFloorUnits, budget.Units);
        Assert.True(seen.Distinct().Count() > 3, "the budget did not step down repeatedly");
    }

    /// <summary>A quiet window creeps the budget back up, and stops at the ceiling.</summary>
    [Fact]
    public void HeadroomCreepsItBackUpAndNeverPastTheCeiling()
    {
        var budget = Budget(ceiling: 10_000, window: 10);

        budget.Observe(1, Interval * 0.99, Interval);
        Assert.Equal(5_000, budget.Units);

        for (var tick = 2; tick <= 400; tick++)
            budget.Observe(tick, Interval * 0.10, Interval);

        // AT the ceiling, not above it: --bot-arbiter stays an upper bound the
        // operator set, and a controller that could exceed it would make the flag
        // a suggestion.
        Assert.Equal(10_000, budget.Units);
        Assert.False(budget.IsThrottled);
    }

    /// <summary>
    /// It does not raise the budget in a window it had to cut in.
    /// </summary>
    /// <remarks>
    /// The peak that caused the cut is still in that window, so a controller that
    /// only compared the peak against <c>LowWater</c> would be correct and a
    /// controller that raised anyway would oscillate. This pins which one it is.
    /// </remarks>
    [Fact]
    public void AWindowThatCutDoesNotAlsoRaise()
    {
        var budget = Budget(window: 10);

        budget.Observe(1, Interval * 0.99, Interval);
        Assert.Equal(5_000, budget.Units);

        // Quiet for the rest of that window, then cross its boundary.
        for (var tick = 2; tick <= 11; tick++)
            budget.Observe(tick, Interval * 0.05, Interval);

        Assert.Equal(5_000, budget.Units);
    }

    /// <summary>
    /// An unpaced server has no deadline, so nothing moves.
    /// </summary>
    /// <remarks>
    /// <c>--speed max</c> never waits, so every tick "overruns". A controller that
    /// read pressure there would drive the budget to the floor on the one
    /// configuration that wants the bots at full strength.
    /// </remarks>
    [Fact]
    public void AnUnpacedServerNeverMovesTheBudget()
    {
        var budget = Budget();

        Assert.Null(budget.Observe(1, 100.0, 0));
        Assert.Equal(10_000, budget.Units);
    }

    /// <summary>A comfortable server never moves off its ceiling at all.</summary>
    [Fact]
    public void AHealthyServerIsNeverThrottled()
    {
        var budget = Budget(window: 10);

        for (var tick = 1; tick <= 500; tick++)
            Assert.Null(budget.Observe(tick, Interval * 0.2, Interval));

        Assert.Equal(10_000, budget.Units);
        Assert.False(budget.IsThrottled);
    }

    // ------------------------------------------------------------------
    // Wiring
    // ------------------------------------------------------------------

    [Fact]
    public void TheAdaptiveBudgetIsOnByDefaultAndCanBePinned()
    {
        Assert.True(CommandLine.Parse([]).AdaptiveBotBudget);
        Assert.False(CommandLine.Parse(["--fixed-bot-budget"]).AdaptiveBotBudget);
    }

    /// <summary>
    /// Every mode with a controller gets a budget, including single-tier ones.
    /// </summary>
    /// <remarks>
    /// <b>A single tier still needs a budget.</b> <c>--bot-mode search</c> shipped
    /// with <c>Arbiter == null</c> on the argument that one tier has nothing to
    /// choose between. An arbiter's second job is to GATE — with one tier it still
    /// returns -1 once the budget is spent and drops the bot to the scripted
    /// layer — so search mode had no throttle at all, and twelve bots searching on
    /// one tick is 12 × ~4,800 µs against a 33,300 µs tick.
    /// </remarks>
    [Fact]
    public void SingleTierModesAlsoGetABudget()
    {
        var search = CommandLine.Parse(["--bot-mode", "search"]).Bots.Brain;

        Assert.IsType<DecisionQuotaArbiter>(Assert.IsType<BotBrainSpec>(search).Arbiter);

        // Scripted has no controller and so nothing to budget.
        Assert.Null(CommandLine.Parse(["--bot-mode", "scripted"]).Bots.Brain);
    }
}
