using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Bots;

/// <summary>
/// A <see cref="RolloutSearch"/> as a micro-controller: forward-simulate every
/// candidate action and keep the best one.
/// </summary>
/// <remarks>
/// `gg2-rl`'s arm 3, and the better BOT of the two shipped controllers — it beat
/// the policy hybrid on death share pooled over ~34,000 deaths (44.55% against
/// 46.17%, z = −4.26) and over 840 matches an arm (−3.59 points, t = −5.60).
/// <para>
/// <b>Cost is not the mean.</b> A search decision is ~7.6 µs·10³ at twelve live
/// characters and handovers are CORRELATED, because several bots have an enemy in
/// range during the same firefight. <b>The constraint is the tick tail, not the
/// duty cycle</b> — which is what <see cref="IMicroArbiter"/> exists to bound.
/// `tools/bench/README.md`, trap 6.
/// </para>
/// <para>
/// <b>Every line here came out of <c>SearchBot</c> unchanged</b>, for the
/// reason <see cref="PolicyController"/> states.
/// </para>
/// </remarks>
public sealed class SearchController : IMicroController
{
    private readonly RolloutSearch _search;

    public SearchController(RolloutSearch search)
    {
        ArgumentNullException.ThrowIfNull(search);

        _search = search;
    }

    public SearchController(RolloutOptions options)
        : this(new RolloutSearch(options))
    {
    }

    /// <summary>The search this drives. For a diagnostic and for a test.</summary>
    public RolloutSearch Search => _search;

    public string Name => "search";

    /// <summary>
    /// The reference measurement: <b>4,795 µs</b> at
    /// <see cref="ReferencePopulation"/> live characters, horizon 20, 60
    /// candidates, on <c>ctf_truefort</c>.
    /// </summary>
    /// <remarks>
    /// Measured in Release, <c>BENCH_SEARCH</c> at <c>BENCH_POP=12</c>.
    /// </remarks>
    public const int ReferenceCost = 4795;

    /// <summary>The population <see cref="ReferenceCost"/> was measured at.</summary>
    public const int ReferencePopulation = 12;

    /// <summary>
    /// Permille of <see cref="ReferenceCost"/> that scales with the live
    /// character count — <b>593</b> — with the remaining 407 flat.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Measured, and it is not proportionality.</b>
    /// <c>BENCH_SEARCH</c> at horizon 20 and 60 candidates on <c>gg_debug</c>,
    /// Release, swept over four populations:
    /// </para>
    /// <code>
    ///   live characters   2      6     12     20
    ///   µs a decision  1831   2624   3617   5116
    /// </code>
    /// <para>
    /// Least squares gives <c>1490 + 181·N</c>, within 2% at every point — so the
    /// cost is <b>affine in the population, not proportional to it</b>, and
    /// 181×12/3662 = 59% of the reference cost is the part that moves. <b>A
    /// proportional model would be roughly 2× low at duel populations</b>, which
    /// is exactly where a scenario or a two-character bench would use it.
    /// </para>
    /// <para>
    /// <b>The SHAPE is measured here and the SCALE is not.</b> The sweep is on
    /// <c>gg_debug</c> and reads 3,617 µs at twelve where <c>ctf_truefort</c> read
    /// 4,795, so the map is worth a third of the cost. Only the shape is taken
    /// from the sweep; the absolute anchor stays the earlier
    /// <c>ctf_truefort</c> measurement so this change recalibrates nothing.
    /// </para>
    /// <para>
    /// <b>And the scale does not need to be right.</b> Only the ratio between
    /// controllers reaches the arbiter, and <c>MicroBudgetController</c>
    /// calibrates the budget to whatever these numbers mean on the machine it is
    /// running on. What has to be right is that this tracks true cost <em>up to a
    /// constant factor</em> — which is what the sweep establishes and what
    /// re-running it on another map or machine would re-establish.
    /// </para>
    /// <para>
    /// <b>What would show this wrong:</b> a <c>BENCH_SEARCH</c> sweep whose
    /// points do not sit on a line, or whose intercept share is far from 59%.
    /// Re-run it after any change to the candidate set, the horizon, or what a
    /// rollout tick simulates.
    /// </para>
    /// </remarks>
    public const int PopulationPermille = 593;

    /// <summary>
    /// <see cref="ReferenceCost"/> adjusted to this population, affine.
    /// </summary>
    /// <remarks>
    /// <b>Integer arithmetic throughout.</b> The result feeds
    /// <c>DecisionQuotaArbiter.Choose</c>, so it decides which controller drives a
    /// bot — keeping it exact keeps that decision reproducible without relying on
    /// floating-point being identical everywhere.
    /// </remarks>
    public int DecisionCostFor(int liveCharacters)
    {
        var population = Math.Max(0, liveCharacters);

        var flat = (long)(1000 - PopulationPermille) * ReferencePopulation;
        var scaled = (long)PopulationPermille * population;
        var cost = ReferenceCost * (flat + scaled) / (1000L * ReferencePopulation);

        // At least 1: a zero cost is a controller the arbiter would admit
        // without limit, and an empty server should not be the one configuration
        // with no budget at all.
        return (int)Math.Max(1, cost);
    }

    public bool DecidedThisPoll { get; private set; }

    /// <summary>
    /// Whether to engage only on the classes the policy was trained for.
    /// </summary>
    /// <remarks>
    /// <b>Defaults to TRUE and that is a measurement decision, not a
    /// limitation.</b> The search drives any class; the policy was trained on
    /// some. Letting the search engage on classes arm 2 cannot would move two
    /// things between the arms at once, and the extra classes are a separate — and
    /// favourable — axis that the experiment is not measuring. Turn it off to
    /// measure that instead, and say which you did.
    /// </remarks>
    public bool RestrictToTrainedClasses { get; init; } = true;

    /// <summary>
    /// Which set to restrict to, when <see cref="RestrictToTrainedClasses"/> is on.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Defaults to <see cref="PolicyNetwork.DefaultTrainedClasses"/> and
    /// that is now an ASSUMPTION about the other arm rather than a fact.</b> This
    /// controller holds no checkpoint — it restricts to the policy's classes only
    /// so the two arms of a comparison engage on the same ones. Since 2026-09-06
    /// the set is a property of the checkpoint, so a comparison against one whose
    /// set is not the default has to say so here, or the arms differ on the axis
    /// the restriction exists to hold still.
    /// </remarks>
    public ReadOnlyMemory<PlayerClass> TrainedClasses { get; init; } =
        PolicyNetwork.DefaultTrainedClasses.ToArray();

    public bool Drives(PlayerClass playerClass) =>
        !RestrictToTrainedClasses || TrainedClasses.Span.Contains(playerClass);

    public bool AtDecisionBoundary(int slot) => _search.AtDecisionBoundary(slot);

    public void OnHandoverIn(in WorldView world, int slot) => _search.ResyncAim(in world, slot);

    /// <summary>
    /// Nothing. <c>RolloutSearch</c> keeps its own cadence across a gap, so a
    /// re-entry waits for the search's next boundary rather than starting one.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately unlike <see cref="PolicyController.OnHandoverOut"/></b>,
    /// and inherited verbatim from <c>SearchBot</c>, which zeroed nothing here.
    /// The arms were measured with this asymmetry.
    /// </remarks>
    public void OnHandoverOut()
    {
    }

    public PlayerInput Poll(in WorldView world, int slot, int tick)
    {
        var input = _search.Poll(in world, slot, tick);

        // Read AFTER the poll: LastDecisionTicksSimulated is zeroed at the top
        // of Poll and set only when it really searched.
        DecidedThisPoll = _search.LastDecisionTicksSimulated > 0;

        return input;
    }
}
