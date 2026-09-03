using System.Numerics;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Bots;

/// <summary>What decides when a micro-controller takes the wheel.</summary>
/// <summary>
/// Which opponents are worth handing the wheel to a micro-controller for.
/// </summary>
/// <remarks>
/// ⛔ <b>The default here is <see cref="Anyone"/> and the SERVER's default is
/// <see cref="HumansOnly"/>, and that difference is deliberate.</b> A headless
/// bot-vs-bot match has no humans in it at all, so a library default of
/// <c>HumansOnly</c> would make every arm measurement silently measure the
/// all-scripted control — <c>HeadlessMatch</c>, the scenarios and
/// <c>tools/bench</c> would all report the controller doing nothing, and the
/// number would look like a result. The live server has the opposite need, so it
/// opts in; see <c>ServerOptions.BotEngagesBotsToo</c>.
/// </remarks>
public enum EngageOpponents
{
    /// <summary>
    /// Any enemy in range engages the controller. Every recorded arm measurement
    /// was taken this way, because it is the only setting under which a
    /// bot-vs-bot match exercises the controller at all.
    /// </summary>
    Anyone = 0,

    /// <summary>
    /// Only a human enemy engages the controller; a bot-vs-bot encounter stays on
    /// the scripted layer.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>What this buys is cost, not skill.</b> The expensive controllers
    /// exist to be good against people; spending a 4,800 µs search decision on
    /// two bots shooting at each other in an empty corner of the map is the bulk
    /// of what a populated server pays for. ⛔ <b>It does not make the bots
    /// better</b>, and a measurement taken under it against bots is measuring the
    /// scripted layer whatever the mode says.
    /// </remarks>
    HumansOnly = 1,
}

public enum HandoverRule
{
    /// <summary>An enemy within <see cref="HybridBot.EngagementRange"/>.</summary>
    /// <remarks>
    /// ⛔ <b>Measured to cost the objective.</b> The 300px came from a DUEL
    /// measurement, where every tick is a combat tick and there is nothing else
    /// the bot could be doing. In CTF "an enemy is near" means "stop pursuing the
    /// objective", because that is all a combat controller knows to do with the
    /// wheel.
    /// </remarks>
    Distance,

    /// <summary>
    /// The scripted layer would itself stop and fight — its own engage hold.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Intent AND range</b>, reusing the predicate the macro layer already
    /// had rather than inventing a second one. Crucially the hold is
    /// <b>time-bounded</b>: it lapses <c>BotConstants.EngageTicks</c> after the
    /// target was acquired, so control returns and the route resumes. The distance
    /// rule has no timer at all.
    /// <para>
    /// ⚠️ <b>The range half is not redundant.</b> The scripted hold fires out to
    /// <c>BotTargeting.ClassRange</c> — 800px for a soldier, 900 for a sniper — so
    /// intent alone hands the wheel far past where a policy's observation
    /// saturates. See <see cref="Handover.ShouldEngage"/> for what that measured.
    /// </para>
    /// </remarks>
    MacroTask,

    /// <summary>
    /// No handover at all: the scripted layer always drives MOVEMENT, the
    /// controller always drives AIM and FIRE.
    /// </summary>
    /// <remarks>
    /// <b><c>gg2-rl</c>'s arm A, and the argument for it is that a parameter we
    /// keep getting wrong is a parameter worth deleting.</b> Proximity was wrong;
    /// intent was better; intent alone was catastrophic until it was conjoined
    /// with range. A head-level split has no trigger to get wrong, and the two
    /// concerns separate cleanly — the scripted layer never loses its path state
    /// or its objective, and the controller contributes only what it is measurably
    /// good at.
    /// <para>
    /// ⚠️ <b>What would falsify it</b>: the death share sliding back toward the
    /// all-scripted 50.5% would mean the policy's movement was carrying the combat
    /// gain after all.
    /// </para>
    /// </remarks>
    HeadSplit,
}

/// <summary>
/// The hybrid bot: the scripted layer plays the game, a
/// <see cref="IMicroController"/> fights.
/// </summary>
/// <remarks>
/// <b>The single implementation of the handover architecture.</b> The scripted
/// layer owns movement, routing, objectives and class behaviour outside
/// engagement range; inside <see cref="EngagementRange"/> the controller owns keys
/// and aim.
/// <para>
/// ⛔ <b>This class REPLACED <c>PolicyBot</c> and <c>SearchBot</c>, which were the
/// same 200 lines twice.</b> Every difference between them lived in five methods
/// and is now behind <see cref="IMicroController"/>. That mattered for more than
/// tidiness: <c>SearchBot</c> had a <c>ScriptedPolls</c> counter and
/// <c>PolicyBot</c> did not, so the invariant that says the macro layer never
/// stopped planning during a fight — arm 2's measured bug, 20 captures down to 7 —
/// was testable on one arm and not the other. <b>Every arm gets every counter
/// now.</b>
/// </para>
/// <para>
/// ⚠️ <b>TIERS ARE ORDERED MOST CAPABLE FIRST.</b> With one tier and no
/// <see cref="IMicroArbiter"/> this is exactly what the two old classes did. With
/// several, an arbiter picks which one may spend a decision this tick — see
/// <see cref="IMicroArbiter"/> for why that is the shape the tick tail needs and
/// what it costs in reproducibility.
/// </para>
/// <para>
/// ⛔ <b>This does NOT reach the wire or observable simulation.</b> A bot may press
/// "drop intel"; <c>MatchSimulation</c> may not decide to drop one. The simulation
/// never special-cases a bot — CLAUDE.md.
/// </para>
/// </remarks>
public sealed class HybridBot : IInputSource, ICommandSource, IWrapsScriptedBot
{
    /// <summary>
    /// Straight-line pixels within which a controller drives.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>150, and it was 300 until it was measured in matches.</b> <b>The
    /// policy was most valuable used SPARINGLY</b>: at 150px it held 7% of ticks
    /// and beat the all-scripted bot on both combat and captures, where at 300px it
    /// held 23% and lost the objective.
    /// <para>
    /// ⛔ <b>Do NOT widen past about 400px for a policy.</b> The observation
    /// saturates there: goal distance clamps at <c>min(d / 400, 1)</c>, the
    /// geometry rays reach 320px and the walkmask patch covers 128px. Beyond that
    /// the policy is handed the wheel at a range its own input cannot describe. A
    /// controller that does not use <c>ObservationWriter</c> is not bound by that
    /// ceiling and would have to establish its own.
    /// </para>
    /// <para>
    /// Straight line, not path distance: a wall-separated pair inside the range is
    /// still handed over.
    /// </para>
    /// <para>
    /// ⛔ <b>Every behavioural number quoted here describes
    /// <c>SIM_UUID 7ff5940d97aa7bed</c> and this build is not that simulation.</b>
    /// Treat them as history and re-measure before acting on one — <c>docs/BOTS.md</c>,
    /// "a policy is DATED by the physics".
    /// </para>
    /// </remarks>
    public const double EngagementRange = 150.0;

    /// <summary>
    /// The 300px `gg2-rl` measured in DUELS, kept for reference and no longer the
    /// default.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Superseded for match play by the sweep of 2026-09-01</b>, 420 matches
    /// a point with an all-scripted control, confirmed at 1,200. Red's capture
    /// share against the same opponent: 47.0% at 300px, 59.7% at 220, <b>68.3% at
    /// 150</b>, 67.2% at 80, against 51.4% for the all-scripted control. The duel
    /// figure was right for duels — where every tick is a combat tick and there is
    /// nothing else the bot could be doing — and too wide for a game with an
    /// objective in it.
    /// </remarks>
    public const double DuelEngagementRange = 300.0;

    private readonly IMicroController[] _tiers;
    private readonly ScriptedBot _scripted;
    private readonly IMicroArbiter? _arbiter;

    private int _slot = -1;

    /// <summary>Index into <c>_tiers</c> of whoever holds the wheel, or -1.</summary>
    private int _driving = -1;

    public HybridBot(IMicroController micro, ScriptedBot scripted, IMicroArbiter? arbiter = null)
        : this([micro], scripted, arbiter)
    {
    }

    public HybridBot(
        IReadOnlyList<IMicroController> tiers, ScriptedBot scripted, IMicroArbiter? arbiter = null)
    {
        ArgumentNullException.ThrowIfNull(tiers);
        ArgumentNullException.ThrowIfNull(scripted);

        if (tiers.Count == 0)
            throw new ArgumentException("a hybrid bot needs at least one controller", nameof(tiers));

        // ⚠️ 32 because eligibility is carried in a uint bitmask, which is what
        // keeps the per-decision path allocation-free. Nothing wants 32 tiers; the
        // limit is stated so a 33rd fails loudly rather than silently dropping out
        // of every arbiter's view.
        if (tiers.Count > 32)
            throw new ArgumentException("at most 32 controller tiers", nameof(tiers));

        foreach (var tier in tiers)
            ArgumentNullException.ThrowIfNull(tier);

        _tiers = [.. tiers];
        _scripted = scripted;
        _arbiter = arbiter;
    }

    /// <summary>The macro layer this wraps. See <see cref="IWrapsScriptedBot"/>.</summary>
    public ScriptedBot ScriptedBot => _scripted;

    ScriptedBot IWrapsScriptedBot.Scripted => _scripted;

    /// <summary>The controllers, most capable first.</summary>
    public IReadOnlyList<IMicroController> Tiers => _tiers;

    /// <summary>Whoever holds the wheel right now, or null when the macro layer does.</summary>
    public IMicroController? Driving => _driving < 0 ? null : _tiers[_driving];

    /// <summary>The engagement window. The measured 150px by default.</summary>
    /// <remarks>
    /// ⚠️ <b>Settable so the frontier can be looked for, not so it can be tuned by
    /// taste.</b> See <see cref="EngagementRange"/>.
    /// </remarks>
    public double Range { get; init; } = EngagementRange;

    /// <summary>The handover rule. <c>MacroTask</c> is the shipped conjunction.</summary>
    public HandoverRule Rule { get; init; } = HandoverRule.MacroTask;

    /// <summary>
    /// Whether a bot opponent is worth engaging a controller for.
    /// </summary>
    /// <remarks>
    /// ⛔ <see cref="EngageOpponents.Anyone"/> here, <c>HumansOnly</c> on the live
    /// server — see the enum for why the two differ.
    /// </remarks>
    public EngageOpponents Engage { get; init; } = EngageOpponents.Anyone;

    /// <summary>
    /// Whether the scripted layer keeps control while this character carries the
    /// intel.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>On by default, and the measurement is the reason.</b> Over 108 matches
    /// a side, wiring the policy in took red from <b>20 captures to 2</b> while
    /// blue's stayed at 12 in both arms — the policy fights instead of running the
    /// flag home, which is what a reward of damage dealt minus taken with no
    /// objective term predicts. <b>A character carrying the intel is on an
    /// objective task</b>, so this is the architecture's own rule applied rather
    /// than an exception to it.
    /// <para>
    /// ⚠️ <b>Settable so the measurement can be repeated.</b> A default that could
    /// not be turned off would make the 20-versus-2 number unreproducible by anyone
    /// who came later.
    /// </para>
    /// </remarks>
    public bool ScriptedCarriesIntel { get; init; } = true;

    /// <summary>Whether a controller drove the tick just polled.</summary>
    public bool MicroDrove { get; private set; }

    /// <summary>
    /// Whether the controller that drove actually DECIDED on the tick just polled.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The expensive event, and not the same as
    /// <see cref="MicroDrove"/>.</b> See
    /// <see cref="IMicroController.DecidedThisPoll"/>.
    /// </remarks>
    public bool DecidedThisPoll { get; private set; }

    /// <summary>Ticks a controller has held the wheel.</summary>
    public long MicroTicks { get; private set; }

    /// <summary>Decisions controllers have taken.</summary>
    public long MicroDecisions { get; private set; }

    /// <summary>Ticks the scripted layer has held it.</summary>
    public long ScriptedTicks { get; private set; }

    /// <summary>
    /// Times the scripted layer has been POLLED, which is every tick.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Not the same as <see cref="ScriptedTicks"/>, and the difference is the
    /// point.</b> That counts ticks it DROVE; this counts ticks it THOUGHT. They
    /// differ by exactly <see cref="MicroTicks"/>, and the invariant
    /// <c>ScriptedPolls == MicroTicks + ScriptedTicks</c> is what says the macro
    /// layer never stopped planning during a fight — arm 2's measured bug, where
    /// polling it only on the ticks it drove cost 20 captures a hundred matches
    /// down to 7. ⛔ <b>Without this counter that property is untestable at unit
    /// level</b>, and a test asserting it from <c>WantsToEngage</c> alone passes on
    /// the broken version.
    /// </remarks>
    public long ScriptedPolls { get; private set; }

    /// <summary>
    /// Commands — team and class choice, drops, taunts — always come from the
    /// scripted layer.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A combat controller has no command head and must never acquire
    /// one.</b> <c>gg2-rl</c>'s reward is damage dealt minus taken with no
    /// objective term, by its user's decision — a bot that runs past you to tap the
    /// intel is optimal and unpleasant. The macro layer owns objectives entirely.
    /// </remarks>
    public void PollCommands(in WorldView world, int slot, int tick, ref PlayerCommandBuffer commands) =>
        _scripted.PollCommands(in world, slot, tick, ref commands);

    public PlayerInput Poll(in WorldView world, int slot, int tick)
    {
        if (slot != _slot)
        {
            _slot = slot;
            ReleaseWheel();
        }

        ref readonly var self = ref world.Character(slot);

        // ⛔ THE SCRIPTED LAYER THINKS EVERY TICK, WHETHER OR NOT IT DRIVES.
        //
        // The first version polled it only on the ticks it drove — so for the 27%
        // of ticks the policy held the wheel, the macro layer did no objective
        // planning at all: no re-target, no re-path, no spot ban, and its path
        // state was stale the moment control came back. A bot that meets enemies
        // often therefore stopped pursuing the objective even between fights.
        // Captures fell 20 to 7 over a hundred matches; docs/ENV.md has the arms.
        //
        // ⚠️ Counted beside the poll it counts, in one place, so no mutation can
        // leave a counter reporting work that never happened — a counter separable
        // from the thing it counts is the same defect as a qualifier separable from
        // its claim.
        ScriptedPolls++;
        var scripted = _scripted.Poll(in world, slot, tick);

        // A dead character is the scripted layer's entirely — and so is one
        // carrying the intel, which is an objective task and therefore the macro
        // layer's by the architecture's own rule.
        if (!world.IsAlive(slot) || (ScriptedCarriesIntel && self.CarryingIntel))
            return Scripted(scripted);

        var eligible = EligibleTiers(self.Class);

        // Every controller refuses this class. It falls back to the scripted layer
        // ENTIRELY rather than being refused a body.
        if (eligible == 0)
            return Scripted(scripted);

        if (Rule == HandoverRule.HeadSplit)
            return HeadSplit(in world, slot, tick, scripted, eligible);

        // ⚠️ Control changes hands only on a DECISION boundary, never mid-decision.
        // Testing the range every tick would flap two controllers against each
        // other at the threshold, and each holds state the other does not maintain
        // — the scripted layer's follower, a decoder's absolute aim. It also keeps
        // the gate exact rather than needing a hysteresis band nobody measured.
        if (_driving >= 0 && !_tiers[_driving].AtDecisionBoundary(slot))
            return MicroInput(_driving, in world, slot, tick);

        if (!Handover.ShouldEngage(in world, slot, Range, Rule, _scripted.WantsToEngage, Engage))
            return Scripted(scripted);

        var chosen = Choose(in world, tick, slot, eligible);

        if (chosen < 0)
            return Scripted(scripted);

        // The chosen controller keeps its own cadence across a gap, so it may not
        // be ready to start a decision even though the gate is open.
        if (!_tiers[chosen].AtDecisionBoundary(slot))
            return Scripted(scripted);

        TakeWheel(chosen, in world, slot);

        return MicroInput(chosen, in world, slot, tick);
    }

    /// <summary>
    /// Scripted movement, controller aim and fire, every tick, with no gate.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b><c>Special</c> goes with FIRE, not with movement.</b> It is the
    /// controller's third fire value — a medic's uber, a sniper's zoom, a soldier's
    /// nothing — and leaving it on the scripted side would let the two halves press
    /// two different attacks in one tick.
    /// <para>
    /// ⚠️ <b><c>Taunt</c> stays with the scripted layer</b>, because a combat
    /// controller has no taunt head and a taunt freezes the character where it
    /// stands.
    /// </para>
    /// <para>
    /// ⚠️ <b>No aim resync here and that is correct</b> — the controller owns the
    /// bearing continuously, so there is no handover for it to be stale across.
    /// Inherited verbatim from <c>PolicyBot</c>.
    /// </para>
    /// </remarks>
    private PlayerInput HeadSplit(
        in WorldView world, int slot, int tick, PlayerInput scripted, uint eligible)
    {
        var chosen = Choose(in world, tick, slot, eligible);

        if (chosen < 0)
            return Scripted(scripted);

        if (chosen != _driving)
        {
            if (_driving >= 0)
                _tiers[_driving].OnHandoverOut();

            _driving = chosen;
        }

        const PlayerKeys Movement = PlayerKeys.Left | PlayerKeys.Right | PlayerKeys.Jump
            | PlayerKeys.Down | PlayerKeys.Taunt;
        const PlayerKeys Combat = PlayerKeys.Attack | PlayerKeys.Special;

        var micro = MicroInput(chosen, in world, slot, tick);

        return new PlayerInput(
            (scripted.Keys & Movement) | (micro.Keys & Combat),
            micro.NetAimDirection,
            micro.AimDistance);
    }

    /// <summary>Which tiers will drive this class, as a bitmask.</summary>
    private uint EligibleTiers(PlayerClass playerClass)
    {
        var mask = 0u;

        for (var i = 0; i < _tiers.Length; i++)
        {
            if (_tiers[i].Drives(playerClass))
                mask |= 1u << i;
        }

        return mask;
    }

    /// <summary>
    /// Which tier may spend a decision here: the arbiter's answer, or the most
    /// capable eligible tier when there is no arbiter.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Advisory, and separate from <see cref="IMicroArbiter.Admitted"/> on
    /// purpose.</b> A tier chosen here may still not take the wheel — its own
    /// cadence may not be at a boundary — and a budget spent on a decision that
    /// never happened is a budget that under-serves the next bot for no reason.
    /// </remarks>
    private int Choose(in WorldView world, int tick, int slot, uint eligible)
    {
        if (_arbiter is null)
            return BitOperations.TrailingZeroCount(eligible);

        // ⚠️ Read here rather than passed down from Poll: it is a 64-entry scan
        // against a decision that costs thousands of microseconds, and threading
        // it through two more signatures buys nothing measurable.
        var chosen = _arbiter.Choose(tick, slot, _tiers, eligible, world.LiveCharacters);

        // ⛔ An arbiter naming an ineligible tier is a bug in the arbiter, and
        // honouring it would drive a class a controller refuses. Refused here
        // rather than clamped, because a silent substitution makes an arm that is
        // not the arm anybody configured.
        if (chosen >= 0 && (chosen >= _tiers.Length || (eligible & (1u << chosen)) == 0))
        {
            throw new InvalidOperationException(
                $"{_arbiter.GetType().Name} chose tier {chosen}, which is not eligible "
                + $"(eligible mask 0x{eligible:x}, {_tiers.Length} tiers)");
        }

        return chosen;
    }

    private void TakeWheel(int chosen, in WorldView world, int slot)
    {
        if (chosen == _driving)
            return;

        if (_driving >= 0)
            _tiers[_driving].OnHandoverOut();

        _driving = chosen;

        // ⛔ Resync absolute aim on every handover IN. The scripted layer writes
        // the character's bearing directly, so a controller carrying the bearing
        // from the last engagement turns from somewhere nobody is pointing —
        // silently, and in exactly the decisions a handover exists to hand it.
        _tiers[chosen].OnHandoverIn(in world, slot);
    }

    private void ReleaseWheel()
    {
        if (_driving < 0)
            return;

        _tiers[_driving].OnHandoverOut();
        _driving = -1;
    }

    private PlayerInput MicroInput(int tier, in WorldView world, int slot, int tick)
    {
        MicroDrove = true;
        MicroTicks++;

        var input = _tiers[tier].Poll(in world, slot, tick);

        DecidedThisPoll = _tiers[tier].DecidedThisPoll;

        if (DecidedThisPoll)
        {
            MicroDecisions++;

            // ⚠️ Charged on the DECISION, not on the tick, and only once it really
            // happened. See Choose.
            _arbiter?.Admitted(tick, slot, tier, _tiers[tier].DecisionCostFor(world.LiveCharacters));
        }

        return input;
    }

    private PlayerInput Scripted(PlayerInput scripted)
    {
        ReleaseWheel();

        MicroDrove = false;
        DecidedThisPoll = false;
        ScriptedTicks++;

        return scripted;
    }
}
