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
/// ⚠️ <b>Cost is not the mean.</b> A search decision is ~7.6 µs·10³ at twelve live
/// characters and handovers are CORRELATED, because several bots have an enemy in
/// range during the same firefight. <b>The constraint is the tick tail, not the
/// duty cycle</b> — which is what <see cref="IMicroArbiter"/> exists to bound.
/// `tools/bench/README.md`, trap 6.
/// </para>
/// <para>
/// ⚠️ <b>Every line here came out of <c>SearchBot</c> unchanged</b>, for the
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
    /// <b>4,800 µs</b> at twelve live characters, horizon 20, 60 candidates.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Measured 2026-09-02, Release, <c>BENCH_SEARCH</c> at
    /// <c>BENCH_POP=12</c> on <c>ctf_truefort</c>: 4,795 µs a decision.</b> It was
    /// <b>4</b> in the invented units this replaced, against a policy's 1 — a 4:1
    /// ratio where the measured one is <b>695:1</b>.
    /// <para>
    /// ⛔ <b>This constant is wrong the moment the configuration changes, and it
    /// cannot be otherwise.</b> Cost is candidates × (per-candidate + horizon ×
    /// per-tick), and the per-tick term scales with the number of live characters
    /// because a rollout re-simulates all of them. Halve the population and this
    /// roughly halves. <b>It is here so an arbiter's arithmetic is out by tens of
    /// percent rather than by two orders of magnitude</b>, which is what it was.
    /// </para>
    /// <para>
    /// ⚠️ <b>Re-measure before deriving a budget from it</b> —
    /// <c>BENCH_SEARCH=1 BENCH_POP=&lt;n&gt;</c>.
    /// </para>
    /// </remarks>
    public int DecisionCost => 4800;

    public bool DecidedThisPoll { get; private set; }

    /// <summary>
    /// Whether to engage only on the classes the policy was trained for.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Defaults to TRUE and that is a measurement decision, not a
    /// limitation.</b> The search drives any class; the policy was trained on
    /// some. Letting the search engage on classes arm 2 cannot would move two
    /// things between the arms at once, and the extra classes are a separate — and
    /// favourable — axis that the experiment is not measuring. Turn it off to
    /// measure that instead, and say which you did.
    /// </remarks>
    public bool RestrictToTrainedClasses { get; init; } = true;

    public bool Drives(PlayerClass playerClass) =>
        !RestrictToTrainedClasses || PolicyController.IsTrained(playerClass);

    public bool AtDecisionBoundary(int slot) => _search.AtDecisionBoundary(slot);

    public void OnHandoverIn(in WorldView world, int slot) => _search.ResyncAim(in world, slot);

    /// <summary>
    /// Nothing. <c>RolloutSearch</c> keeps its own cadence across a gap, so a
    /// re-entry waits for the search's next boundary rather than starting one.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Deliberately unlike <see cref="PolicyController.OnHandoverOut"/></b>,
    /// and inherited verbatim from <c>SearchBot</c>, which zeroed nothing here.
    /// The arms were measured with this asymmetry.
    /// </remarks>
    public void OnHandoverOut()
    {
    }

    public PlayerInput Poll(in WorldView world, int slot, int tick)
    {
        var input = _search.Poll(in world, slot, tick);

        // ⚠️ Read AFTER the poll: LastDecisionTicksSimulated is zeroed at the top
        // of Poll and set only when it really searched.
        DecidedThisPoll = _search.LastDecisionTicksSimulated > 0;

        return input;
    }
}
