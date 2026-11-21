using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Bots;

/// <summary>
/// The thing behind the handover gate: whatever owns a bot's keys and aim while
/// the scripted layer keeps routing, objectives and class behaviour.
/// </summary>
/// <remarks>
/// <b>This is the seam a new bot implementation drops into.</b> Everything a
/// combat controller needs beyond an <c>IInputSource</c> — the handover gate, the
/// intel carve-out, the "scripted thinks every tick" rule, the aim resync and the
/// counters — lives on <see cref="HybridBot"/>, and a new controller implements
/// the six members below.
/// <para>
/// <b>The controller owns its own decision CADENCE, and that is not a
/// convenience.</b> <c>ActionDecoder</c> emits the jump bit only on the first tick
/// of a decision, so the held ticks are a <em>different decode of the same
/// action</em> rather than a repeat of one <c>PlayerInput</c>. A gate that counted
/// ticks itself would either starve a controller of its held ticks or
/// re-implement the pressed-edge logic a second time — CLAUDE.md's "GG2 jumps on
/// the PRESSED edge" scar. So the gate asks
/// <see cref="AtDecisionBoundary"/> and never counts.
/// </para>
/// <para>
/// <b>An implementation must not draw from <c>WorldState.Random</c></b> — the
/// match's stream is observable simulation, and a bot drawing from it makes the
/// stream's position depend on how many bots were thinking. CLAUDE.md's first bot
/// rule, and it applies here exactly as it does to <c>ScriptedBot</c>.
/// </para>
/// <para>
/// <b>No allocation per poll.</b> Interface dispatch is free; a
/// <c>float[]</c> per decision is not. Allocate in the constructor.
/// </para>
/// </remarks>
public interface IMicroController
{
    /// <summary>
    /// What this controller is called, for a diagnostic, a control-port roster or
    /// a bench column.
    /// </summary>
    /// <remarks>
    /// <b>An arm nobody can name in a result table is an arm nobody can
    /// report.</b> Without it, a bench prints "hybrid" for two different
    /// controllers.
    /// </remarks>
    string Name { get; }

    /// <summary>
    /// Told the skill the bot is playing at, before each <see cref="Poll"/> it drives.
    /// </summary>
    /// <remarks>
    /// For the part of difficulty that lives in what a controller <em>sees</em>
    /// rather than in what <see cref="HybridBot"/> does to its output — a policy's
    /// rocket perception (<see cref="RocketPerception"/>). A controller with no such
    /// part ignores it.
    /// </remarks>
    void ApplySkill(in BotSkill skill)
    {
    }

    /// <summary>
    /// What one DECISION costs, in <b>microseconds</b>.
    /// </summary>
    /// <remarks>
    /// <b>Microseconds, not an arbitrary relative unit.</b> A policy decision is
    /// 6.9 µs (2.2 extracting the observation, 4.7 in the forward pass); a search
    /// decision at twelve live characters is 4,795 µs — a 695:1 ratio. An arbiter
    /// budgeting in coarse relative units (say 4 and 1) would admit four policy
    /// decisions as the price of one search and be wrong by more than two orders
    /// of magnitude.
    /// <para>
    /// <b>A microsecond unit makes a budget mean something.</b> The tick is
    /// 33,300 µs, so <c>--bot-arbiter 10000</c> is "ten milliseconds of controller
    /// work a tick" and caps concurrent search decisions at two.
    /// </para>
    /// <para>
    /// <b>A constant cannot be right for the search, and this is the honest
    /// approximation rather than a claim.</b> A rollout re-simulates every live
    /// character for every tick of every candidate, so its cost scales with
    /// POPULATION, horizon and candidate count — <c>BENCH_SEARCH</c> shows
    /// 2.47 ms at two characters against 7.64 ms at twelve. The value below names
    /// the configuration it was measured at. <b>Re-measure with
    /// <c>BENCH_SEARCH</c> and <c>BENCH_POLICYCOST</c> before trusting a budget
    /// derived from it.</b>
    /// </para>
    /// <para>
    /// The default of 1 belongs to a controller that has not measured itself —
    /// a stub or a test double. A real one overrides it.
    /// </para>
    /// <para>
    /// <b>It takes the live character count because cost is not a constant.</b>
    /// A rollout re-simulates every live character for every tick of every
    /// candidate, so its cost is affine in the population — measured, see
    /// <c>SearchController</c>. A controller whose cost really is flat (a policy
    /// forward pass is a fixed-size network) simply ignores the argument.
    /// </para>
    /// <para>
    /// <b>Only the RATIO between controllers is ever used</b>, by
    /// <c>DecisionQuotaArbiter</c>: <c>cost &lt;= budget - spent</c>. Nothing reads
    /// the absolute scale, which is why a number measured on one machine is still
    /// useful on another — see <c>MicroBudgetController</c>, which calibrates the
    /// budget to whatever these numbers happen to mean on the machine it is on.
    /// </para>
    /// </remarks>
    int DecisionCostFor(int liveCharacters) => 1;

    /// <summary>
    /// Whether this controller may drive a character of this class at all.
    /// </summary>
    /// <remarks>
    /// <b>Asked every tick, not once at construction</b>, because a class can
    /// change mid-match and the wrapper cannot be rebuilt when it does. A class
    /// this refuses falls back to the scripted layer ENTIRELY rather than being
    /// refused a body — the scripted layer is a complete bot on its own.
    /// </remarks>
    bool Drives(PlayerClass playerClass);

    /// <summary>
    /// Whether the next poll would start a NEW decision, as opposed to continuing
    /// one already taken.
    /// </summary>
    /// <remarks>
    /// <b>Control changes hands only here.</b> Two controllers with independent
    /// cadences flap against each other at the gate's threshold, and each holds
    /// state the other does not maintain — the scripted layer's follower, a
    /// decoder's absolute aim.
    /// </remarks>
    bool AtDecisionBoundary(int slot);

    /// <summary>
    /// Called on every handover IN, before the first <see cref="Poll"/> of the
    /// engagement.
    /// </summary>
    /// <remarks>
    /// <b>This is where absolute aim is resynchronised, and skipping it is
    /// silent.</b> The scripted layer writes the character's bearing directly, so
    /// a controller carrying the bearing from the last engagement turns from
    /// somewhere nobody is pointing — in exactly the decisions a handover exists
    /// to hand it.
    /// </remarks>
    void OnHandoverIn(in WorldView world, int slot);

    /// <summary>
    /// Called on every handover OUT, when the scripted layer takes the wheel back.
    /// </summary>
    /// <remarks>
    /// <b>The two shipped controllers answer this differently and both are
    /// right.</b> <see cref="SimPolicyController"/> abandons its part-finished
    /// decision, so a re-entry starts a fresh one; <see cref="SearchController"/>
    /// leaves <c>RolloutSearch</c>'s counter alone, so a re-entry waits for the
    /// search's own boundary. The search's half is inherited verbatim from
    /// <c>SearchBot</c> and preserved on purpose — the arms were measured with it.
    /// </remarks>
    void OnHandoverOut();

    /// <summary>This slot's keys and aim for this tick.</summary>
    PlayerInput Poll(in WorldView world, int slot, int tick);

    /// <summary>
    /// Whether the poll just made actually DECIDED, as opposed to replaying a
    /// decision already taken.
    /// </summary>
    /// <remarks>
    /// <b>This is the expensive event and it is not "the controller drove".</b>
    /// A controller drives every tick of a decision and decides on one of them, so
    /// counting ticks it drove over-reports the cost by the decision length. A
    /// tick-budget question needs the count of DECISIONS that coincided.
    /// </remarks>
    bool DecidedThisPoll { get; }
}
