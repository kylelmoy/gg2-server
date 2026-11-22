using System.Text;
using GG2.Protocol;

namespace GG2.Bots;

/// <summary>
/// A recipe for one <see cref="IMicroController"/>: enough to build a fresh
/// instance per bot, and nothing that is per-bot state.
/// </summary>
/// <remarks>
/// <b>A controller is built PER BOT and a spec is shared.</b> A controller holds
/// per-slot state — its decision cadence, a decoder's absolute aim — so one
/// instance driving several bots would have them stepping on each other. What may be shared is the immutable half:
/// <see cref="SimPolicyMicroSpec"/> hands every bot the SAME
/// <see cref="SimPolicyNetwork"/>, which is safe only because bots are polled
/// serially.
/// </remarks>
public abstract record MicroControllerSpec
{
    /// <summary>What this arm is called in a result table.</summary>
    public abstract string Name { get; }

    /// <summary>A fresh controller for one bot.</summary>
    public abstract IMicroController Create();
}

/// <summary>
/// Everything that makes a bot something other than the all-scripted arm: which
/// controllers it may use, in what order, behind which gate.
/// </summary>
/// <remarks>
/// <b>This is the ONE field a caller sets to change what a bot is.</b> Before it
/// existed, <c>HeadlessMatchOptions</c> and <c>BotPopulationOptions</c> each
/// carried a nullable field per controller, a ternary chain to pick between them
/// and a hand-written "both are set" throw — <b>duplicated in two places, and a
/// third controller meant editing both.</b> A list has no such arity.
/// <para>
/// <b>Null means the all-scripted arm</b>, not a broken configuration. That is
/// arm 1, it is a complete bot, and it is the control every comparison is made
/// against.
/// </para>
/// <para>
/// <b>The first tier that drives a bot's class is the one that drives it.</b>
/// Several tiers are how several checkpoints drive disjoint class sets; on a
/// server <c>BotBrain</c> guarantees the sets are disjoint, so the order only
/// decides anything for a hand-built spec that overlaps.
/// </para>
/// </remarks>
public sealed record BotBrainSpec
{
    /// <summary>The controllers, most capable first. At least one.</summary>
    public required IReadOnlyList<MicroControllerSpec> Tiers { get; init; }

    /// <summary>The handover rule. <c>MacroTask</c> is the shipped conjunction.</summary>
    public HandoverRule Rule { get; init; } = HandoverRule.MacroTask;

    /// <summary>
    /// Whether a bot opponent is worth engaging the controller for.
    /// </summary>
    /// <remarks>
    /// <b><see cref="EngageOpponents.Anyone"/> here, and <c>HumansOnly</c> on
    /// the live server.</b> A headless bot-vs-bot match has no humans, so
    /// defaulting this to <c>HumansOnly</c> would make every arm measurement
    /// silently measure the all-scripted control. See the enum.
    /// </remarks>
    public EngageOpponents Engage { get; init; } = EngageOpponents.Anyone;

    /// <summary>The engagement window. The measured 150px by default.</summary>
    public double Range { get; init; } = HybridBot.EngagementRange;

    /// <summary>Whether the scripted layer keeps the wheel while carrying the intel.</summary>
    public bool ScriptedCarriesIntel { get; init; } = true;

    /// <summary>
    /// <see cref="HybridBot.ReleaseRange"/>: a controller that is driving keeps the
    /// wheel out to 300px. Shipped.
    /// </summary>
    /// <remarks>
    /// Measured with <c>BENCH_HANDOVER</c> (eight fleet maps, 1,600 paired matches
    /// an arm, red hybrid against blue scripted). Against no band, the policy's
    /// control share rises from 4.44% to 5.97%, death share improves by 0.60 ± 0.18
    /// points, and win share moves −0.87 ± 2.12 points. That is inside the
    /// pre-registered 1-point bar, but the interval cannot exclude a cost of about 3.
    /// <c>gg2-rl</c> <c>docs/PLAN.md</c> phase 5, B2. NaN turns the band off.
    /// </remarks>
    public double ReleaseRange { get; init; } = DefaultReleaseRange;

    /// <summary>The shipped release band, in pixels.</summary>
    public const double DefaultReleaseRange = 300;

    /// <summary><see cref="HybridBot.EngageNearOwnGoal"/>: off by default.</summary>
    public bool EngageNearOwnGoal { get; init; }

    /// <summary>
    /// Whether every bot puts up a chat bubble when the wheel changes hands.
    /// </summary>
    /// <remarks>
    /// <b>A debugging aid, off by default in every arm.</b> See
    /// <see cref="HandoverBubble"/>. It is on the spec rather than passed to
    /// <see cref="Wrap"/> so that one server-wide setting reaches every bot
    /// through the one field a caller already sets to change what a bot is.
    /// </remarks>
    public bool Bubbles { get; init; }

    /// <summary>One tier, default gate. The shape almost every caller wants.</summary>
    public static BotBrainSpec Of(params MicroControllerSpec[] tiers) =>
        new() { Tiers = tiers };

    /// <summary>
    /// What to print in a result table: the arm this describes, tiers joined by
    /// <c>+</c>.
    /// </summary>
    /// <remarks>
    /// <b>An arm nobody can name in a result table is an arm nobody can
    /// report.</b> The bench printed "hybrid" for two different controllers.
    /// </remarks>
    public string Name
    {
        get
        {
            var text = new StringBuilder();

            foreach (var tier in Tiers)
            {
                if (text.Length > 0)
                    text.Append('+');

                text.Append(tier.Name);
            }

            return text.ToString();
        }
    }

    /// <summary>Builds this bot's controllers and wraps a macro layer in them.</summary>
    /// <remarks>
    /// Called once per bot at construction, never in the tick loop — every
    /// allocation a controller needs happens here.
    /// </remarks>
    public HybridBot Wrap(ScriptedBot scripted)
    {
        ArgumentNullException.ThrowIfNull(scripted);

        if (Tiers.Count == 0)
            throw new InvalidOperationException("a BotBrainSpec needs at least one tier");

        var built = new IMicroController[Tiers.Count];

        for (var i = 0; i < built.Length; i++)
            built[i] = Tiers[i].Create();

        return new HybridBot(built, scripted)
        {
            Rule = Rule,
            Range = Range,
            ReleaseRange = ReleaseRange,
            EngageNearOwnGoal = EngageNearOwnGoal,
            Engage = Engage,
            ScriptedCarriesIntel = ScriptedCarriesIntel,
            Bubbles = Bubbles,
        };
    }
}
