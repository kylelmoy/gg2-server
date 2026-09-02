using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Bots;

/// <summary>
/// The thing behind the handover gate: whatever owns a bot's keys and aim while
/// the scripted layer keeps routing, objectives and class behaviour.
/// </summary>
/// <remarks>
/// ⛔ <b>This is the seam a new bot implementation drops into.</b> Before it
/// existed, "another way to fight" meant a near-copy of <c>PolicyBot</c> — the
/// gate, the intel carve-out, the "scripted thinks every tick" rule, the aim
/// resync and five counters, all restated — and <c>SearchBot</c> was exactly that
/// copy. <b>Everything on that list is <see cref="HybridBot"/>'s now</b>, and a
/// new controller implements the six members below.
/// <para>
/// ⚠️ <b>The controller owns its own decision CADENCE, and that is not a
/// convenience.</b> <c>ActionDecoder</c> emits the jump bit only on the first tick
/// of a decision, so the held ticks are a <em>different decode of the same
/// action</em> rather than a repeat of one <c>PlayerInput</c>. A gate that counted
/// ticks itself would either starve a controller of its held ticks or
/// re-implement the pressed-edge logic a second time — CLAUDE.md's "GG2 jumps on
/// the PRESSED edge" scar. So the gate asks
/// <see cref="AtDecisionBoundary"/> and never counts.
/// </para>
/// <para>
/// ⛔ <b>An implementation must not draw from <c>WorldState.Random</c></b> — the
/// match's stream is observable simulation, and a bot drawing from it makes the
/// stream's position depend on how many bots were thinking. CLAUDE.md's first bot
/// rule, and it applies here exactly as it does to <c>ScriptedBot</c>.
/// </para>
/// <para>
/// ⚠️ <b>No allocation per poll.</b> Interface dispatch is free; a
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
    /// ⚠️ <b>An arm nobody can name in a result table is an arm nobody can
    /// report.</b> The bench printed "hybrid" for two different controllers until
    /// this existed.
    /// </remarks>
    string Name { get; }

    /// <summary>
    /// A relative cost per DECISION, in whatever units an arbiter is configured
    /// in. 1 is the unit.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>These are not measurements and no expression may treat them as
    /// one.</b> They order the tiers for <see cref="IMicroArbiter"/> and nothing
    /// else. `tools/bench/README.md` trap 7 is the standing example of what
    /// happens when a bare constant in an expression is read as a measured cost:
    /// a <c>4.7</c> that was an estimate, was 1.4x low, and drove an
    /// affordable/OVER column in two repos for a day. <b>Measure with
    /// <c>BENCH_SEARCH</c> or <c>BENCH_POLICYCOST</c> before quoting a ratio.</b>
    /// </remarks>
    int DecisionCost => 1;

    /// <summary>
    /// Whether this controller may drive a character of this class at all.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Asked every tick, not once at construction</b>, because a class can
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
    /// ⚠️ <b>Control changes hands only here.</b> Two controllers with independent
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
    /// ⛔ <b>This is where absolute aim is resynchronised, and skipping it is
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
    /// ⚠️ <b>The two shipped controllers answer this differently and both are
    /// right.</b> <see cref="PolicyController"/> abandons its part-finished
    /// decision, so a re-entry starts a fresh one; <see cref="SearchController"/>
    /// leaves <c>RolloutSearch</c>'s counter alone, so a re-entry waits for the
    /// search's own boundary. That asymmetry is inherited verbatim from
    /// <c>PolicyBot</c> and <c>SearchBot</c> and is preserved on purpose — the
    /// arms were measured with it.
    /// </remarks>
    void OnHandoverOut();

    /// <summary>This slot's keys and aim for this tick.</summary>
    PlayerInput Poll(in WorldView world, int slot, int tick);

    /// <summary>
    /// Whether the poll just made actually DECIDED, as opposed to replaying a
    /// decision already taken.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>This is the expensive event and it is not "the controller drove".</b>
    /// A controller drives every tick of a decision and decides on one of them, so
    /// counting ticks it drove over-reports the cost by the decision length. A
    /// tick-budget question needs the count of DECISIONS that coincided.
    /// </remarks>
    bool DecidedThisPoll { get; }
}
