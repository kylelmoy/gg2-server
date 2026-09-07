using Gg2.Bots;
using Gg2.Simulation;

namespace Gg2.Server;

/// <summary>
/// The one place a <see cref="BotBrainSpec"/> is built out of the five settings
/// that describe one.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ <b>ONE PLACE BUILDS THE BRAIN, at the end, from inputs collected first.</b>
/// The flag handlers used to write the brain directly, which made
/// <c>--search-threads</c> order-dependent: given before <c>--search-bots</c> it
/// was picked up, given after it had to re-write the option it had just found.
/// Collecting first and building once removes the ordering entirely, and it is
/// what lets a config file and the command line reach the same reconcile instead
/// of growing two copies of it — CLAUDE.md's "three places" rule one level up,
/// the same argument <see cref="ControlCommands"/> makes for the operator verbs.
/// </para>
/// <para>
/// ⚠️ <b>This is why <see cref="ServerOptions.BotMode"/> and the four settings
/// beside it exist as options at all.</b> A brain holds a loaded
/// <c>PolicyNetwork</c> — megabytes of weights with no textual form — so it
/// cannot be written back to a config file and cannot round-trip. Its inputs
/// can, so the inputs are the configuration and the brain is derived.
/// </para>
/// </remarks>
public static class BotBrain
{
    /// <summary>
    /// <c>--search-threads</c>'s default: a third of the machine's cores, 1..8.
    /// </summary>
    /// <remarks>
    /// A property rather than a constant because it reads
    /// <see cref="Environment.ProcessorCount"/>, and a <c>const</c> would bake
    /// the build machine's core count into every deployment.
    /// </remarks>
    public static int DefaultSearchThreads => Math.Clamp(Environment.ProcessorCount / 3, 1, 8);

    /// <summary>
    /// Reconciles the brain settings and writes the result into
    /// <see cref="BotPopulationOptions.Brain"/>.
    /// </summary>
    /// <param name="options">The options to reconcile, changed in place.</param>
    /// <param name="report">
    /// Where to print the checkpoint's stamp line, or null to print nothing.
    /// <b>Passed in rather than assumed to be the console</b>: the host prints it
    /// before any log sink exists, and a server restarted from the panel prints
    /// it into the log an operator is actually reading.
    /// </param>
    /// <exception cref="ArgumentException">
    /// A mode that needs a checkpoint was given none, or a checkpoint was given
    /// to a mode that never consults one. ⛔ <b>Refused rather than ignored</b>:
    /// a checkpoint loaded, printed and never used is a server saying it is doing
    /// something it is not.
    /// </exception>
    public static void Build(ServerOptions options, Action<string>? report = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var mode = options.BotMode;
        var policyPath = options.PolicyPath.Length == 0 ? null : options.PolicyPath;

        if (mode.UsesPolicy() && policyPath is null)
        {
            throw new ArgumentException(
                $"bot mode '{mode.ToString().ToLowerInvariant()}' drives bots with a trained "
                + "checkpoint and none was given; add --policy <path> (or a [Bots] Policy key), "
                + "or use bot mode scripted or search.");
        }

        if (!mode.UsesPolicy() && policyPath is not null)
        {
            throw new ArgumentException(
                $"a policy checkpoint was given but bot mode '{mode.ToString().ToLowerInvariant()}' "
                + "never consults one; use bot mode policy or tiered, or drop the checkpoint.");
        }

        var arbiterUnits = options.BotArbiterMicroseconds;

        // ⚠️ Tiered DEFAULTS its budget rather than refusing without one. Refusing
        // made the mode unreachable without a number nobody has tuned, which is a
        // worse default than a stated guess — see the constant, which says what
        // would show it is wrong.
        // ⚠️ EVERY mode with a controller gets a budget, not just tiered. The
        // reasoning that "one tier has nothing to choose between, so an arbiter is
        // pure cost" was wrong: an arbiter's second job is to GATE, and with one
        // tier it still returns -1 and drops the bot to the scripted layer once the
        // budget is spent. Without one, search mode let all twelve bots search on
        // the same tick — 12 x ~4,800 us against a 33,300 us tick, which is over
        // budget before the simulation runs at all.
        if (mode.NeedsArbiter())
            arbiterUnits ??= DecisionQuotaArbiter.DefaultTieredBudgetMicroseconds;

        var tiers = new List<MicroControllerSpec>();

        if (mode.UsesSearch())
            tiers.Add(new SearchMicroSpec(new RolloutOptions(Threads: options.SearchThreads)));

        if (mode.UsesPolicy())
        {
            var policy = PolicyNetwork.Load(policyPath!, options.PolicyIgnoreStamp);

            // ⛔ ALWAYS reported, not only when overridden. A run whose stamps
            // agree says so; a run that bypassed the guard says THAT, loudly, on
            // the same line. A bypassed guard that leaves no trace in the output is
            // worse than no guard, because the next reader cannot tell it happened.
            report?.Invoke(
                $"policy {Path.GetFileName(policyPath!)}: trained against {policy.SimulationUuid}, "
                + $"this build is {SimulationStamp.Uuid}"

                // ⚠️ Reported, never compared. NavConstants left the gating surface
                // on 2026-09-03 so a nav edit stops stranding checkpoints — but
                // removing it from the GATE must not remove it from the RECORD, or
                // "the bots play differently and the stamp matches" becomes
                // unattributable.
                + $", routing {SimulationStamp.RoutingUuid}"
                + (policy.SimulationUuid == SimulationStamp.Uuid
                    ? " (match)"
                    : "  <-- STAMP MISMATCH, BYPASSED. Everything this policy decides was "
                      + "learned on a different simulation."));

            // ⚠️ AFTER the search, so tier 0 is the more expensive controller and an
            // arbiter under pressure drops to the policy before it drops to the
            // scripted layer. Ordering is the arbiter's whole input — HybridBot.
            tiers.Add(new PolicyMicroSpec(policy));
        }

        // ⛔ Unreachable from the settings now — NeedsArbiter defaults the budget
        // for every mode with a controller — and kept as the invariant it always
        // was. If a fifth mode arrives with two tiers and no default, this is what
        // stops it shipping a controller that is configured, reported and never
        // consulted.
        if (tiers.Count > 1 && arbiterUnits is null)
        {
            throw new ArgumentException(
                $"{tiers.Count} bot controllers were configured but no arbiter budget; "
                + "without one the first tier always drives and the rest are dead weight");
        }

        // ⚠️ CLEARED when there is no controller, not left alone. Reconciling twice
        // — which a restart does — must be idempotent, and a brain left over from a
        // previous mode is a server running a controller its own settings say it is
        // not.
        options.Bots = options.Bots with
        {
            Brain = tiers.Count == 0
                ? null
                : new BotBrainSpec
                {
                    Tiers = tiers,

                    // ⛔ The server's default is the OPPOSITE of BotBrainSpec's,
                    // deliberately — see ServerOptions.BotEngagesBotsToo.
                    Engage = options.BotEngagesBotsToo
                        ? EngageOpponents.Anyone
                        : EngageOpponents.HumansOnly,

                    // ⛔ ONE arbiter for the whole server, shared by every bot. A
                    // per-bot one cannot see the coinciding decisions that make the
                    // tick tail, which is the only reason it exists.
                    Arbiter = arbiterUnits is { } units ? new DecisionQuotaArbiter(units) : null,
                },
        };
    }
}
