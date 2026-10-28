using System.Numerics;
using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Bots;

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
/// <b>This class replaces separate per-arm bot classes.</b> Every difference
/// between arms lives behind <see cref="IMicroController"/>, and every counter
/// is shared, so an invariant like "the macro layer never stops planning during
/// a fight" is testable on every arm rather than only some of them.
/// </para>
/// <para>
/// <b>TIERS ARE ORDERED MOST CAPABLE FIRST.</b> With one tier and no
/// <see cref="IMicroArbiter"/> this is a single fixed controller. With
/// several, an arbiter picks which one may spend a decision this tick — see
/// <see cref="IMicroArbiter"/> for why that is the shape the tick tail needs and
/// what it costs in reproducibility.
/// </para>
/// <para>
/// <b>This does NOT reach the wire or observable simulation.</b> A bot may press
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
    /// <b>The policy is most valuable used SPARINGLY</b>: at 150px it held 7% of
    /// ticks and beat the all-scripted bot on both combat and captures, where at
    /// 300px it held 23% and lost the objective.
    /// <para>
    /// <b>Do NOT widen past about 400px for a policy.</b> The observation
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
    /// <b>Every behavioural number quoted here describes
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
    /// <b>Superseded for match play</b> by a sweep of 420 matches a point with an
    /// all-scripted control, confirmed at 1,200. Red's capture share against the
    /// same opponent: 47.0% at 300px, 59.7% at 220, <b>68.3% at 150</b>, 67.2% at
    /// 80, against 51.4% for the all-scripted control. The duel figure is right
    /// for duels — where every tick is a combat tick and there is nothing else the
    /// bot could be doing — and too wide for a game with an objective in it.
    /// </remarks>
    public const double DuelEngagementRange = 300.0;

    /// <summary>The hybrid driving a slot, through any output filter around it.</summary>
    /// <remarks>
    /// <b>Use this instead of <c>source is HybridBot</c></b>, for the reason
    /// <see cref="ScriptedBot.Of"/> gives: a <see cref="HumanizedBot"/> wraps the
    /// hybrid, and the direct type test would say the slot has no controller.
    /// </remarks>
    public static HybridBot? Of(IInputSource? source) => source switch
    {
        HybridBot hybrid => hybrid,
        HumanizedBot humanized => Of(humanized.Inner),
        TeamGatedBot gated => gated.Hybrid,
        _ => null,
    };

    private readonly IMicroController[] _tiers;
    private readonly ScriptedBot _scripted;
    private readonly IMicroArbiter? _arbiter;

    private int _slot = -1;

    /// <summary>Index into <c>_tiers</c> of whoever holds the wheel, or -1.</summary>
    private int _driving = -1;

    /// <summary>
    /// The bubble the last handover asked for, waiting for
    /// <see cref="PollCommands"/>. Null when there is nothing to send.
    /// </summary>
    /// <remarks>
    /// <b>A field rather than a command pushed where the handover happens,
    /// because the two are polled separately and in the wrong order.</b>
    /// <c>GameServer.Tick</c> runs <c>ServiceBots</c> — which is
    /// <see cref="PollCommands"/> — before <c>GatherInputs</c>, which is
    /// <see cref="Poll"/>, so a handover decided in a poll reaches the wire on
    /// the NEXT tick. One tick of lag on a debugging aid, said out loud so
    /// nobody reads a 33 ms offset as a bug.
    /// </remarks>
    private byte? _bubble;

    /// <summary>
    /// <see cref="PollCommands"/> calls between two bubbles this bot sends.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Hard-coded against the server's per-player bubble throttle</b>,
    /// <c>ConnectionLimits.ChatBubbleIntervalMs</c> (250 ms), which drops a bubble
    /// in silence. A search bot releases and re-takes the wheel a couple of ticks
    /// apart at the edge of the range, so without this the throttle ate the take
    /// bubble after nearly every release, and a client was left showing <c>z2</c>
    /// over a bot the search was driving — 6 of 9 <c>z3</c> lost in a measured
    /// 60 s search match. Nine ticks is 300 ms at 30 Hz: 8 would be 267 ms, too
    /// close to 250 with the server reading <c>Environment.TickCount64</c> at its
    /// ~15.6 ms resolution. A diagnostic, so a constant rather than the live
    /// setting: an operator who raises the throttle gets dropped bubbles again.
    /// </remarks>
    internal const int BubbleSpacingPolls = 9;

    /// <summary><see cref="PollCommands"/> calls so far — this bot's own clock.</summary>
    /// <remarks>
    /// Counted rather than read from the <c>tick</c> argument, which a caller may
    /// restart from zero (a test's drain loop does) and which would then read as
    /// time running backwards.
    /// </remarks>
    private long _commandPolls;

    /// <summary>The poll the last bubble went out on.</summary>
    private long _bubbleSentAt = long.MinValue / 2;

    /// <summary>The last bubble sent, which is what clients are showing.</summary>
    private byte? _bubbleShown;

    /// <summary>
    /// The difficulty's aim error on the controller's output, in wire units
    /// (<c>65536</c> to the circle), held until the next draw.
    /// </summary>
    private int _handicapAim;

    /// <summary>Whether the difficulty's hold-fire roll came up for this draw.</summary>
    private bool _handicapHoldFire;

    /// <summary>Controller-driven ticks until the next handicap draw.</summary>
    /// <remarks>
    /// Counted down rather than compared with <c>tick</c>, for the same reason as
    /// <see cref="_commandPolls"/>: a caller may restart the tick from zero.
    /// </remarks>
    private int _handicapTicksLeft;

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

        // 32 because eligibility is carried in a uint bitmask, which is what
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

    /// <summary>
    /// The index of the most capable tier that will drive this class, or -1 when
    /// every tier refuses it and the scripted layer owns the slot outright.
    /// </summary>
    /// <remarks>
    /// <b>For a diagnostic — the control port's roster, the panel — never for a
    /// decision.</b> It is <see cref="Choose"/>'s answer with no arbiter, so under
    /// one the tier it names can still be refused a decision on any given tick and
    /// the bot fall to the scripted layer for it. <see cref="Driving"/> is what
    /// actually held the wheel; this is what could.
    /// <para>
    /// <b>An INDEX rather than the controller, and that is what makes it
    /// reportable.</b> <see cref="IMicroController.Name"/> is the controller's
    /// own — every <see cref="PolicyController"/> answers <c>policy</c>, whatever
    /// checkpoint it holds — so a caller naming a tier from it prints
    /// <c>policy</c> for both halves of a two-tier bundle. The LABEL lives on
    /// <see cref="MicroControllerSpec"/>, index-aligned with
    /// <see cref="Tiers"/> by <see cref="BotBrainSpec.Wrap"/>, and an index is
    /// what reaches it.
    /// </para>
    /// <para>
    /// <b>Here rather than re-derived by the caller.</b> Eligibility is
    /// <see cref="IMicroController.Drives"/> asked of the tiers in order, and a
    /// second copy of that walk is a second place for the order to be got wrong —
    /// the ordering IS the arbiter's whole input.
    /// </para>
    /// </remarks>
    public int TierIndexFor(PlayerClass playerClass)
    {
        var eligible = EligibleTiers(playerClass);

        return eligible == 0 ? -1 : BitOperations.TrailingZeroCount(eligible);
    }

    /// <summary>The engagement window. The measured 150px by default.</summary>
    /// <remarks>
    /// <b>Settable so the frontier can be looked for, not so it can be tuned by
    /// taste.</b> See <see cref="EngagementRange"/>.
    /// </remarks>
    public double Range { get; init; } = EngagementRange;

    /// <summary>The handover rule. <c>MacroTask</c> is the shipped conjunction.</summary>
    public HandoverRule Rule { get; init; } = HandoverRule.MacroTask;

    /// <summary>
    /// The range a controller already driving keeps the wheel within, or NaN for
    /// <see cref="Range"/> (no band).
    /// </summary>
    /// <remarks>
    /// <b>Hysteresis: take the wheel at <see cref="Range"/>, give it back past
    /// this.</b> It keeps the controller in fights it is already in without
    /// widening when it takes the wheel, which is what Q22 measured to cost the
    /// objective. It is still read only at a decision boundary, so there is no
    /// flapping to damp. The rest of the gate (the engage hold under
    /// <c>MacroTask</c>) applies unchanged. <c>gg2-rl</c> <c>docs/PLAN.md</c>
    /// phase 5, B2, has the measurement.
    /// </remarks>
    public double ReleaseRange { get; init; } = double.NaN;

    /// <summary>
    /// Whether a fight near the bot's own goal is handed over too.
    /// </summary>
    /// <remarks>
    /// Off, the gate reads <see cref="ScriptedBot.WantsToEngage"/>, which is false
    /// near the bot's own goal. On, it reads
    /// <see cref="ScriptedBot.WantsToEngageNearGoal"/>, so a defender fighting on
    /// its own point fights with the controller. 13% of the in-range ticks a
    /// policy class spends scripted are this term (<c>BENCH_RELEASE</c>).
    /// </remarks>
    public bool EngageNearOwnGoal { get; init; }

    /// <summary>
    /// Whether a bot opponent is worth engaging a controller for.
    /// </summary>
    /// <remarks>
    /// <see cref="EngageOpponents.Anyone"/> here, <c>HumansOnly</c> on the live
    /// server — see the enum for why the two differ.
    /// </remarks>
    public EngageOpponents Engage { get; init; } = EngageOpponents.Anyone;

    /// <summary>
    /// Whether the scripted layer keeps control while this character carries the
    /// intel.
    /// </summary>
    /// <remarks>
    /// <b>On by default, and the measurement is the reason.</b> Over 108 matches
    /// a side, wiring the policy in took red from <b>20 captures to 2</b> while
    /// blue's stayed at 12 in both arms — the policy fights instead of running the
    /// flag home, which is what a reward of damage dealt minus taken with no
    /// objective term predicts. <b>A character carrying the intel is on an
    /// objective task</b>, so this is the architecture's own rule applied rather
    /// than an exception to it.
    /// <para>
    /// <b>Settable so the measurement can be repeated.</b> A default that could
    /// not be turned off would make the 20-versus-2 number unreproducible by anyone
    /// who came later.
    /// </para>
    /// </remarks>
    public bool ScriptedCarriesIntel { get; init; } = true;

    /// <summary>
    /// Whether this bot puts up a chat bubble every time the wheel changes hands.
    /// </summary>
    /// <remarks>
    /// <b>A debugging aid, off by default</b> — see <see cref="HandoverBubble"/>
    /// for the three bubbles, why it is off, and why turning it on cannot change
    /// what a bot does.
    /// </remarks>
    public bool Bubbles { get; init; }

    /// <summary>Whether a controller drove the tick just polled.</summary>
    public bool MicroDrove { get; private set; }

    /// <summary>
    /// Whether the controller that drove actually DECIDED on the tick just polled.
    /// </summary>
    /// <remarks>
    /// <b>The expensive event, and not the same as
    /// <see cref="MicroDrove"/>.</b> See
    /// <see cref="IMicroController.DecidedThisPoll"/>.
    /// </remarks>
    public bool DecidedThisPoll { get; private set; }

    /// <summary>Ticks a controller has held the wheel.</summary>
    public long MicroTicks { get; private set; }

    /// <summary>Decisions controllers have taken.</summary>
    public long MicroDecisions { get; private set; }

    /// <summary>
    /// The skill <see cref="Handicap"/> reads in place of the scripted layer's,
    /// or null to read the scripted layer's.
    /// </summary>
    /// <remarks>
    /// A measurement knob: it lets a bench weaken the controller alone while the
    /// macro layer plays at full strength, which is the only way to tell how much
    /// of a tier is the handicap. Nothing on a live server sets it.
    /// </remarks>
    public BotSkill? HandicapSkill { get; set; }

    /// <summary>Ticks the scripted layer has held it.</summary>
    public long ScriptedTicks { get; private set; }

    /// <summary>
    /// Times the scripted layer has been POLLED, which is every tick.
    /// </summary>
    /// <remarks>
    /// <b>Not the same as <see cref="ScriptedTicks"/>, and the difference is the
    /// point.</b> That counts ticks it DROVE; this counts ticks it THOUGHT. They
    /// differ by exactly <see cref="MicroTicks"/>, and the invariant
    /// <c>ScriptedPolls == MicroTicks + ScriptedTicks</c> is what says the macro
    /// layer never stopped planning during a fight — polling it only on the ticks
    /// it drove cost 20 captures a hundred matches down to 7. <b>Without this
    /// counter that property is untestable at unit level</b>, and a test asserting
    /// it from <c>WantsToEngage</c> alone passes on the broken version.
    /// </remarks>
    public long ScriptedPolls { get; private set; }

    /// <summary>
    /// Commands — team and class choice, drops, taunts — always come from the
    /// scripted layer.
    /// </summary>
    /// <remarks>
    /// <b>A combat controller has no command head and must never acquire
    /// one.</b> <c>gg2-rl</c>'s reward is damage dealt minus taken with no
    /// objective term, by its user's decision — a bot that runs past you to tap the
    /// intel is optimal and unpleasant. The macro layer owns objectives entirely.
    /// </remarks>
    public void PollCommands(in WorldView world, int slot, int tick, ref PlayerCommandBuffer commands)
    {
        _scripted.PollCommands(in world, slot, tick, ref commands);
        _commandPolls++;

        if (_bubble is not { } image)
            return;

        var spaced = _commandPolls - _bubbleSentAt >= BubbleSpacingPolls;

        // Back to what clients already show before the last one could go out —
        // a release and re-take inside the window. Nothing to correct.
        if (!spaced)
        {
            if (image == _bubbleShown)
                _bubble = null;

            return;
        }

        // ⛔ AFTER the macro layer, never before. The buffer has a ceiling —
        // ClientCommands.PerTickLimit, the same one a socket gets — and Push
        // silently fails at it, so a debugging aid pushed first could displace a
        // real objective command. Dropping the bubble instead is the right way
        // round: nothing that matters is lost, and the aid is only ever an aid.
        if (commands.Push(PlayerCommand.ChatBubble(image)))
        {
            _bubble = null;
            _bubbleShown = image;
            _bubbleSentAt = _commandPolls;
        }
    }

    public PlayerInput Poll(in WorldView world, int slot, int tick)
    {
        if (slot != _slot)
        {
            _slot = slot;
            ReleaseWheel();

            // ⛔ After ReleaseWheel, which sets one. This wrapper is now driving
            // a different roster slot, and a bubble decided for the previous one
            // would be put up over whoever is in this one.
            _bubble = null;

            _handicapAim = 0;
            _handicapHoldFire = false;
            _handicapTicksLeft = 0;
        }

        ref readonly var self = ref world.Character(slot);

        // THE SCRIPTED LAYER THINKS EVERY TICK, WHETHER OR NOT IT DRIVES.
        //
        // Polling it only on the ticks it drove means that for the ticks the
        // policy holds the wheel, the macro layer does no objective planning at
        // all: no re-target, no re-path, no spot ban, and its path state goes
        // stale the moment control comes back. A bot that meets enemies often
        // then stops pursuing the objective even between fights. Captures fell
        // 20 to 7 over a hundred matches; gg2-rl/docs/ENV.md has the arms.
        //
        // Counted beside the poll it counts, in one place, so no mutation can
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

        // Control changes hands only on a DECISION boundary, never mid-decision.
        // Testing the range every tick would flap two controllers against each
        // other at the threshold, and each holds state the other does not maintain
        // — the scripted layer's follower, a decoder's absolute aim. It also keeps
        // the gate exact rather than needing a hysteresis band nobody measured.
        if (_driving >= 0 && !_tiers[_driving].AtDecisionBoundary(slot))
            return MicroInput(_driving, in world, slot, tick);

        // Combat.Ally, not a bool: the predicate is Handover's and the fact is
        // the scripted layer's, the same split WantsToEngage already uses.
        var range = _driving >= 0 && !double.IsNaN(ReleaseRange) ? Math.Max(Range, ReleaseRange) : Range;
        var wants = EngageNearOwnGoal ? _scripted.WantsToEngageNearGoal : _scripted.WantsToEngage;

        if (!Handover.ShouldEngage(
                in world, slot, range, Rule, wants, Engage, _scripted.Combat.Ally))
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
    /// <b><c>Special</c> goes with FIRE, not with movement.</b> It is the
    /// controller's third fire value — a medic's uber, a sniper's zoom, a soldier's
    /// nothing — and leaving it on the scripted side would let the two halves press
    /// two different attacks in one tick.
    /// <para>
    /// <b><c>Taunt</c> stays with the scripted layer</b>, because a combat
    /// controller has no taunt head and a taunt freezes the character where it
    /// stands.
    /// </para>
    /// <para>
    /// <b>No aim resync here and that is correct</b> — the controller owns the
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

            NoteHandover(chosen);
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
    /// <b>Advisory, and separate from <see cref="IMicroArbiter.Admitted"/> on
    /// purpose.</b> A tier chosen here may still not take the wheel — its own
    /// cadence may not be at a boundary — and a budget spent on a decision that
    /// never happened is a budget that under-serves the next bot for no reason.
    /// </remarks>
    private int Choose(in WorldView world, int tick, int slot, uint eligible)
    {
        if (_arbiter is null)
            return BitOperations.TrailingZeroCount(eligible);

        // Read here rather than passed down from Poll: it is a 64-entry scan
        // against a decision that costs thousands of microseconds, and threading
        // it through two more signatures buys nothing measurable.
        var chosen = _arbiter.Choose(tick, slot, _tiers, eligible, world.LiveCharacters);

        // An arbiter naming an ineligible tier is a bug in the arbiter, and
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

        NoteHandover(chosen);
        _driving = chosen;

        // Resync absolute aim on every handover IN. The scripted layer writes
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
        NoteHandover(-1);
        _driving = -1;
    }

    /// <summary>
    /// Remembers the bubble a handover asks for, for the next
    /// <see cref="PollCommands"/> to send.
    /// </summary>
    /// <remarks>
    /// <b>Called at every site that moves <c>_driving</c>, before it moves</b> —
    /// the three are <see cref="TakeWheel"/>, <see cref="ReleaseWheel"/> and
    /// <see cref="HeadSplit"/>'s own switch, and a site that grew without a call
    /// here would be a handover the aid cannot see.
    /// <para>
    /// ⚠️ <b>The pending bubble is overwritten, not queued.</b> Two handovers
    /// before it can go out — inside one tick under
    /// <see cref="HandoverRule.HeadSplit"/>, or inside
    /// <see cref="BubbleSpacingPolls"/> under any rule — send the last, which is
    /// the one describing the state the bot is actually in. A queue would put the
    /// aid permanently behind the thing it is describing.
    /// </para>
    /// </remarks>
    private void NoteHandover(int next)
    {
        if (!Bubbles)
            return;

        if (HandoverBubble.For(next < 0 ? null : _tiers[next]) is { } image)
            _bubble = image;
    }

    private PlayerInput MicroInput(int tier, in WorldView world, int slot, int tick)
    {
        MicroDrove = true;
        MicroTicks++;

        var input = Handicap(_tiers[tier].Poll(in world, slot, tick), tick);

        DecidedThisPoll = _tiers[tier].DecidedThisPoll;

        if (DecidedThisPoll)
        {
            MicroDecisions++;

            // Charged on the DECISION, not on the tick, and only once it really
            // happened. See Choose.
            _arbiter?.Admitted(tick, slot, tier, _tiers[tier].DecisionCostFor(world.LiveCharacters));
        }

        return input;
    }

    /// <summary>
    /// Applies the scripted layer's difficulty to whatever the controller pressed.
    /// </summary>
    /// <remarks>
    /// <b>Difficulty inside the window.</b> Every <see cref="BotSkill"/> gate lives
    /// in <see cref="BotCombat"/>, which a controller never runs, so without this a
    /// tier-1 bot fought at full strength the moment an enemy came within
    /// <see cref="Range"/>. Three of the scripted chain's knobs are carried over,
    /// each chosen because it is ZERO at the top tier — so tier 5 is the checkpoint
    /// exactly as trained, draws nothing from <see cref="ScriptedBot.Random"/>, and
    /// moves no pin:
    /// <list type="bullet">
    /// <item><b>The fire delay</b>, <see cref="BotSkill.FireDelayTicks"/> after the
    /// scripted layer acquired its target. Not the acquire hold too: that is 6 ticks
    /// at tier 5, and the controller already aims while it would be waiting.</item>
    /// <item><b>An aim error</b>, uniform within <see cref="BotSkill.PolicyAimErrorDeg"/>
    /// and held for <see cref="BotSkill.AimInterval"/> ticks. Held rather than
    /// redrawn every tick, because a per-tick jitter averages out across a burst and
    /// handicaps nobody. Added to the output only: the decoder keeps its own aim, so
    /// the error never accumulates.</item>
    /// <item><b>The hold-fire roll</b>, <see cref="BotSkill.HoldFireChance"/>, on the
    /// same draw.</item>
    /// </list>
    /// <b>Only <c>Attack</c> is withheld</b>, never <c>Special</c>, for the reason
    /// the scripted chain gives: a pyro's airblast or a demoman's detonation cannot
    /// be a second and a half late. Movement is left alone — a policy's dodge is a
    /// learnt behaviour with no knob to turn down.
    /// </remarks>
    private PlayerInput Handicap(PlayerInput input, int tick)
    {
        var skill = HandicapSkill ?? _scripted.Skill;
        var keys = input.Keys;

        if (--_handicapTicksLeft <= 0)
        {
            _handicapTicksLeft = skill.AimInterval;

            // Each draw only when its knob is live, so the top tier never moves
            // the scripted layer's stream.
            _handicapAim = skill.PolicyAimErrorDeg > 0
                ? (int)Math.Round((_scripted.Random.Next(2) - 1) * skill.PolicyAimErrorDeg * 65536 / 360)
                : 0;

            _handicapHoldFire = skill.HoldFireChance > 0 && _scripted.Random.Next(1) < skill.HoldFireChance;
        }

        var combat = _scripted.Combat;
        var reacting = combat.Target.Exists && tick - combat.TargetAt < skill.FireDelayTicks;

        if (reacting || _handicapHoldFire)
            keys &= ~PlayerKeys.Attack;

        return new PlayerInput(keys, (ushort)(input.NetAimDirection + _handicapAim), input.AimDistance);
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
