namespace GG2.Bots;

/// <summary>
/// Decides which micro-controller a bot may spend a decision on, given what every
/// other bot is doing on the same tick.
/// </summary>
/// <remarks>
/// <b>This exists because the constraint on a hybrid bot is the TICK TAIL, not the
/// duty cycle.</b> A search decision at twelve live characters is a large fraction
/// of a 33.3 ms tick on its own, and handovers are <em>correlated</em>: several
/// bots have an enemy in range during the same firefight, so the worst tick in a
/// match is several coinciding decisions rather than one expensive bot. A
/// per-bot setting cannot see that, because no bot knows what the others are
/// doing. <b>One arbiter shared by every bot on a server does.</b>
/// <para>
/// <b>SHARED, and therefore ordered by ROSTER SLOT.</b> Bots are polled in slot
/// order, so a quota is spent by low slots first. That is deterministic — a match
/// replays identically — but it is not fair, and a bot that is always slot 11 is
/// always the one downgraded. <b>An arbiter that wants fairness has to rotate on
/// something derived from the tick, and it must derive it from the TICK rather
/// than from a clock</b>, for the reason below.
/// </para>
/// <para>
/// <b>DO NOT READ A CLOCK IN HERE.</b> An arbiter whose answer depends on
/// wall-clock time makes a match unreproducible: <c>HeadlessMatch</c> results stop
/// being a function of the seed, <c>eval</c> stops being thread-count independent
/// — a property this repo gates rather than assumes — and any parity fixture
/// recorded with bots in it stops replaying. <b>The measured resource number
/// belongs OUTSIDE the tick</b>: let the host read <c>--tick-stats</c> and set the
/// budget between ticks, and keep the decision inside the tick a pure function of
/// the budget it was given. <see cref="DecisionQuotaArbiter.UnitsPerTick"/> is
/// settable for exactly that.
/// </para>
/// <para>
/// <b>Costs are measured in microseconds</b> — a policy decision is 6.9 µs and a
/// search decision 4,795 µs at twelve live characters (<c>BENCH_POLICYCOST</c>
/// and <c>BENCH_SEARCH</c>), a 695:1 ratio; see
/// <see cref="IMicroController.DecisionCost"/>.
/// </para>
/// <para>
/// <b>So a budget names something.</b> The tick is 33,300 µs, which makes
/// <c>UnitsPerTick = 10000</c> "ten milliseconds of controller work a tick" and
/// caps concurrent search decisions at two. <b>No budget has been TUNED</b> — the
/// mechanism is measured, the policy for setting it is not.
/// </para>
/// <para>
/// <b>The search's constant is configuration-dependent and cannot not be.</b>
/// Its cost scales with population, horizon and candidate count. Re-measure with
/// <c>BENCH_SEARCH=1 BENCH_POP=&lt;n&gt;</c> before deriving a budget.
/// </para>
/// <para>
/// <b>The falsification condition:</b> if a downgrading arbiter does not move a
/// <c>BENCH_THREADS</c>-style p99.9 column, it is not doing what this file says.
/// </para>
/// </remarks>
public interface IMicroArbiter
{
    /// <summary>
    /// Which tier this bot may use for the decision it is about to take, or -1 to
    /// leave the wheel with the scripted layer.
    /// </summary>
    /// <param name="tick">The server's frame counter. The only legitimate clock in here.</param>
    /// <param name="slot">The roster slot asking. A slot index is a roster index.</param>
    /// <param name="tiers">The bot's controllers, most capable first.</param>
    /// <param name="eligible">
    /// Bit <c>i</c> set means <c>tiers[i]</c> will drive this character's class.
    /// <b>Returning a tier outside this mask throws</b> — see
    /// <c>HybridBot.Choose</c>. Never silently substituted, because a silent
    /// substitution makes an arm that is not the arm anybody configured.
    /// </param>
    /// <remarks>
    /// <b>Advisory and side-effect free.</b> The bot may still not take the
    /// decision — its controller's own cadence may not be at a boundary — and
    /// <see cref="Admitted"/> is what says one really happened. Spending a budget
    /// here would under-serve later bots for decisions that never occurred.
    /// </remarks>
    /// <param name="liveCharacters">
    /// Live characters, for <see cref="IMicroController.DecisionCostFor"/>.
    /// A search decision's cost is affine in this, so an arbiter that assumed a
    /// constant is out by roughly 2x between a duel and a full server.
    /// </param>
    int Choose(
        int tick, int slot, IReadOnlyList<IMicroController> tiers, uint eligible, int liveCharacters);

    /// <summary>
    /// Called once per decision that was actually taken, after it was taken.
    /// </summary>
    /// <remarks>
    /// <b>Per DECISION, not per tick driven.</b> A controller drives every tick
    /// of a decision and decides on one of them; charging the held ticks
    /// over-reports the cost by the decision length.
    /// </remarks>
    void Admitted(int tick, int slot, int tier, int cost);
}

/// <summary>
/// A fixed budget of controller microseconds per tick, spent most-capable-first.
/// </summary>
/// <remarks>
/// The simplest arbiter that answers the question this seam was built for: when
/// several bots want an expensive controller on the same tick, the first few get
/// it and the rest drop to a cheaper tier or to the scripted layer.
/// <para>
/// <b>Deterministic.</b> Its answer depends on the tick, the poll order and the
/// configured budget, and on nothing else — no clock, no thread count, no machine.
/// A match with one of these in it still replays from its seed.
/// </para>
/// <para>
/// <b><see cref="UnitsPerTick"/> is where a resource-driven policy plugs in, and
/// it must be written from OUTSIDE the tick.</b> A host that watches
/// <c>--tick-stats</c> and lowers the budget when the tail creeps up keeps the
/// measurement outside the simulation and the decision inside it pure. Writing it
/// from within a tick would make the budget depend on where in that tick you were.
/// </para>
/// <para>
/// <b>Not thread-safe, and does not need to be.</b> Bots are polled serially in
/// slot order; <c>RolloutOptions.Threads</c> parallelises the candidate loop
/// INSIDE one decision, never across bots — CLAUDE.md, and <c>BotOccupancy</c> has
/// the same property for the same reason.
/// </para>
/// </remarks>
public sealed class DecisionQuotaArbiter : IMicroArbiter
{
    private int _tick = int.MinValue;
    private int _spent;

    /// <param name="unitsPerTick">
    /// The budget, in <b>microseconds of controller work per tick</b>. See
    /// <see cref="IMicroController.DecisionCost"/>. The tick itself is 33,300 µs.
    /// </param>
    public DecisionQuotaArbiter(int unitsPerTick)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(unitsPerTick);

        UnitsPerTick = unitsPerTick;
    }

    /// <summary>
    /// The budget per tick. Settable from outside the tick; see this type's
    /// remarks.
    /// </summary>
    public int UnitsPerTick { get; set; }

    /// <summary>
    /// What <c>--bot-mode tiered</c> uses when <c>--bot-arbiter</c> is not given:
    /// <b>10,000 µs of controller work per tick</b>.
    /// </summary>
    /// <remarks>
    /// <b>Derived from measured COSTS, but the budget itself has never been
    /// tuned — this is a stated guess, not a result.</b> The tick is 33,300 µs; a
    /// search decision measures ~4,800 µs at twelve live characters and a policy
    /// decision ~7 µs, so 10,000 caps concurrent search decisions at two and
    /// leaves the rest of the tick to the simulation. Nothing has measured
    /// whether two is the right number of bots to let search on a tick, or what
    /// the play is like on either side of it.
    /// <para>
    /// <b>What would show this is wrong:</b> <c>--tick-stats</c> reporting a
    /// tail near 33.3 ms on a tiered server (too generous), or the arbiter
    /// starving the search tier so often that tiered plays like
    /// <see cref="BotMode.Policy"/> (too mean —
    /// <see cref="DecisionQuotaArbiter.SpentThisTick"/> against the tick count is
    /// the way to see it). Re-measure before quoting this number as a finding.
    /// </para>
    /// </remarks>
    public const int DefaultTieredBudgetMicroseconds = 10_000;

    /// <summary>Units spent on the tick most recently seen. For a test and a diagnostic.</summary>
    public int SpentThisTick => _spent;

    public int Choose(
        int tick, int slot, IReadOnlyList<IMicroController> tiers, uint eligible, int liveCharacters)
    {
        if (tick != _tick)
        {
            _tick = tick;
            _spent = 0;
        }

        var left = UnitsPerTick - _spent;

        // Most capable first, so a bot takes the best tier that still fits and a
        // cheaper one only when it does not.
        for (var i = 0; i < tiers.Count; i++)
        {
            if ((eligible & (1u << i)) == 0)
                continue;

            if (tiers[i].DecisionCostFor(liveCharacters) <= left)
                return i;
        }

        // -1, not "the cheapest tier anyway". A budget that is always exceeded
        // by one more decision is not a budget, and the scripted layer is a
        // complete bot — this is the fallback the architecture already has.
        return -1;
    }

    public void Admitted(int tick, int slot, int tier, int cost)
    {
        if (tick != _tick)
        {
            _tick = tick;
            _spent = 0;
        }

        _spent += cost;
    }
}
