namespace GG2.Bots;

/// <summary>
/// Which of the shipped bot brains a server runs — the four arms
/// <c>docs/BOTS.md</c> compares, named once so a flag and a result table can
/// mean the same thing.
/// </summary>
/// <remarks>
/// <para>
/// The numbers are part of the contract: <c>--bot-mode</c> accepts them as well
/// as the names, so a script can say <c>--bot-mode 3</c> and a person can say
/// <c>--bot-mode tiered</c>. <b>Do not renumber them</b> — a benchmark harness
/// that sweeps 0..3 would silently start measuring different arms.
/// </para>
/// <para>
/// <b>This replaced <c>--search-bots</c>, a boolean.</b> With one flag per
/// controller the four combinations were reachable only by knowing which pairs of
/// flags to give together, and the two-controller case additionally needed
/// <c>--bot-arbiter</c> or it was refused. One enum makes the reachable set
/// obvious and lets <see cref="Tiered"/> default its own budget.
/// </para>
/// </remarks>
public enum BotMode
{
    /// <summary>
    /// The scripted layer alone — arm 1, and the control every comparison is
    /// made against.
    /// </summary>
    /// <remarks>
    /// <b>A complete bot, not a degraded one.</b> It walks, fights and plays
    /// the objective; the other three only replace its keys and aim inside the
    /// engagement window.
    /// </remarks>
    Scripted = 0,

    /// <summary>
    /// The scripted layer plus a rollout-search micro-controller — arm 3.
    /// </summary>
    /// <remarks>
    /// <b>The better bot and the expensive one.</b> Better on death share than
    /// both the all-scripted control and the policy hybrid, pooled over ~34,000
    /// deaths and 840+ matches an arm. <b>Never measured with humans in the
    /// server</b> — a rollout re-simulates every live character for every tick of
    /// every candidate, so cost rises with the population and every number quoted
    /// comes from 8-bot headless matches. Watch <c>--tick-stats</c>.
    /// </remarks>
    Search = 1,

    /// <summary>
    /// The scripted layer plus a trained policy checkpoint — arm 2. Needs
    /// <c>--policy</c>.
    /// </summary>
    Policy = 2,

    /// <summary>
    /// Both, most expensive first, with an arbiter deciding which may spend a
    /// decision when several bots engage on the same tick. Needs <c>--policy</c>.
    /// </summary>
    /// <remarks>
    /// <b>Ordering is the arbiter's whole input</b> — search then policy, so a
    /// server under pressure drops to the cheaper controller before it drops to
    /// the scripted layer. See <c>HybridBot</c>.
    /// <para>
    /// Unlike the other three this one <em>defaults</em> a budget
    /// (<see cref="DecisionQuotaArbiter.DefaultTieredBudgetMicroseconds"/>)
    /// rather than refusing without <c>--bot-arbiter</c>: refusing made the mode
    /// unreachable without a number nobody has tuned, which is a worse default
    /// than a stated guess.
    /// </para>
    /// </remarks>
    Tiered = 3,
}

/// <summary>What each <see cref="BotMode"/> needs and what it builds.</summary>
public static class BotModes
{
    /// <summary>
    /// Whether this mode drives bots with a trained checkpoint, and therefore
    /// needs one loaded.
    /// </summary>
    /// <remarks>
    /// <b>Used in both directions by the host</b>: a mode that needs a policy
    /// and was given none is refused, and a policy given to a mode that cannot
    /// use it is refused too. The second is the one worth having — a checkpoint
    /// loaded, reported and never consulted is exactly the shape of claim this
    /// repo keeps catching.
    /// </remarks>
    public static bool UsesPolicy(this BotMode mode) =>
        mode is BotMode.Policy or BotMode.Tiered;

    /// <summary>Whether this mode runs the rollout search.</summary>
    public static bool UsesSearch(this BotMode mode) =>
        mode is BotMode.Search or BotMode.Tiered;

    /// <summary>
    /// Whether this mode runs any micro-controller, and so needs a budget.
    /// </summary>
    /// <remarks>
    /// <b>ANY controller, not just more than one.</b> An arbiter has two jobs
    /// and only the first needs a choice: it picks between tiers, and it GATES —
    /// returning -1 once the budget is spent, which drops that bot to the scripted
    /// layer. A single-tier mode still wants the second. Reading it as "choose
    /// between tiers" left <see cref="BotMode.Search"/> with no throttle at all,
    /// and twelve bots searching on one tick is over the tick budget before the
    /// simulation runs.
    /// </remarks>
    public static bool NeedsArbiter(this BotMode mode) => mode != BotMode.Scripted;
}
