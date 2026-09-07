using System.Text;
using Gg2.Protocol;

namespace Gg2.Bots;

/// <summary>
/// A recipe for one <see cref="IMicroController"/>: enough to build a fresh
/// instance per bot, and nothing that is per-bot state.
/// </summary>
/// <remarks>
/// <b>A controller is built PER BOT and a spec is shared.</b> Both shipped
/// controllers hold per-slot state — <c>RolloutSearch</c>'s decision counter, a
/// decoder's absolute aim — so one instance driving several bots would have them
/// stepping on each other's cadence. What may be shared is the immutable half:
/// <see cref="PolicyMicroSpec"/> hands every bot the SAME
/// <see cref="PolicyNetwork"/>, which is how it has always worked and is safe only
/// because bots are polled serially.
/// </remarks>
public abstract record MicroControllerSpec
{
    /// <summary>What this arm is called in a result table.</summary>
    public abstract string Name { get; }

    /// <summary>A fresh controller for one bot.</summary>
    public abstract IMicroController Create();
}

/// <summary>A trained checkpoint driving keys and aim. <c>docs/BOTS.md</c>'s arm 2.</summary>
/// <param name="Policy">The loaded weights, SHARED by every bot — see the base type.</param>
/// <param name="Label">
/// What to call this tier in a result table, or null for the bare word
/// <c>policy</c>. ⚠️ <b>Set it whenever more than one of these is in the same
/// brain</b>, which has been possible since 2026-09-07: <see cref="BotBrainSpec.Name"/>
/// exists so an arm can be named in a table, and two unlabelled policy tiers
/// print <c>policy+policy</c> — the exact defect that type's remarks describe.
/// The host passes the checkpoint's file name.
/// </param>
/// <remarks>
/// ⚠️ <b>Trained checkpoints for this build EXIST</b> — this sentence asserted the
/// opposite until 2026-09-07, and by then it had been false for a day.
/// <c>docs/BOTS.md</c>, "the shipped bundle", has what ships and what each file's
/// numbers do and do not cover; <c>PolicyNetwork.Load</c> still refuses a foreign
/// stamp by design.
/// </remarks>
public sealed record PolicyMicroSpec(PolicyNetwork Policy, string? Label = null)
    : MicroControllerSpec
{
    public override string Name => Label is null ? "policy" : "policy:" + Label;

    public override IMicroController Create() => new PolicyController(Policy);
}

/// <summary>A rollout search driving keys and aim. <c>docs/BOTS.md</c>'s arm 3.</summary>
/// <param name="TrainedClasses">
/// Which set to restrict to. ⛔ <b>Null takes
/// <see cref="PolicyNetwork.DefaultTrainedClasses"/>, which is only right when
/// there is no checkpoint in the comparison.</b> A bench arming this search
/// against a policy arm must pass THAT checkpoint's set: since 2026-09-06 the set
/// is a property of the file, and the two shipping families outside the default
/// differ from it by two members and — for <c>rest3</c> — by ALL of them. Two arms
/// restricted to disjoint class sets are not a comparison; the restriction exists
/// to hold class coverage still and would be the thing varying it.
/// </param>
public sealed record SearchMicroSpec(
    RolloutOptions Options,
    bool RestrictToTrainedClasses = true,
    PlayerClass[]? TrainedClasses = null) : MicroControllerSpec
{
    public override string Name => "search";

    public override IMicroController Create() =>
        new SearchController(Options)
        {
            RestrictToTrainedClasses = RestrictToTrainedClasses,
            TrainedClasses = TrainedClasses ?? PolicyNetwork.DefaultTrainedClasses.ToArray(),
        };
}

/// <summary>
/// Everything that makes a bot something other than the all-scripted arm: which
/// controllers it may use, in what order, behind which gate, under which arbiter.
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
/// <b>Tiers are ordered MOST CAPABLE FIRST</b> and only matter when an
/// <see cref="Arbiter"/> is set — without one, tier 0 always drives. See
/// <see cref="IMicroArbiter"/> before configuring several, including what it costs
/// in reproducibility to decide between them badly.
/// </para>
/// </remarks>
public sealed record BotBrainSpec
{
    /// <summary>The controllers, most capable first. At least one.</summary>
    public required IReadOnlyList<MicroControllerSpec> Tiers { get; init; }

    /// <summary>
    /// Who decides which tier a bot may spend a decision on, or null to always use
    /// tier 0.
    /// </summary>
    /// <remarks>
    /// <b>ONE instance shared by every bot on a server</b>, which is the whole
    /// point — a per-bot arbiter cannot see the coinciding decisions that make the
    /// tick tail. It is held here rather than created per bot for that reason.
    /// </remarks>
    public IMicroArbiter? Arbiter { get; init; }

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

        return new HybridBot(built, scripted, Arbiter)
        {
            Rule = Rule,
            Range = Range,
            Engage = Engage,
            ScriptedCarriesIntel = ScriptedCarriesIntel,
        };
    }
}
