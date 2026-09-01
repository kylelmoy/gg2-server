using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Bots;

/// <summary>
/// The hybrid bot: the scripted layer plays the game, the learned policy fights.
/// </summary>
/// <remarks>
/// <b><c>gg2-rl</c>'s phase 4 architecture, and every number in it is theirs.</b>
/// The scripted layer owns movement, routing, objectives and class behaviour
/// outside engagement range; inside <see cref="EngagementRange"/> the policy owns
/// keys and aim. The split costs about **1.7 points** against a policy driving
/// throughout, and that is a lower bound twice over — their stand-in walker could
/// not route, and a straight-line gate hands wall-separated bots to a policy that
/// cannot route either.
/// <para>
/// ⚠️ <b>Do NOT hand movement over entirely.</b> The policy's own movement is
/// worth 16-23 points over random and about 19.5 over walking straight at the
/// enemy. In range it is not merely approaching; it is spacing and dodging.
/// </para>
/// <para>
/// ⛔ <b>And do NOT widen <see cref="EngagementRange"/> past about 400px.</b> The
/// observation saturates there: goal distance clamps at <c>min(d / 400, 1)</c>,
/// the geometry rays reach 320px and the walkmask patch covers 128px. Beyond that
/// the policy is being handed the wheel at a range its own input cannot describe.
/// </para>
/// </remarks>
/// <summary>What decides when the policy takes the wheel.</summary>
public enum HandoverRule
{
    /// <summary>An enemy within <see cref="PolicyBot.EngagementRange"/>.</summary>
    /// <remarks>
    /// ⛔ <b>Measured to cost the objective.</b> The 300px came from a DUEL
    /// measurement, where every tick is a combat tick and there is nothing else
    /// the bot could be doing. In CTF "an enemy is near" means "stop pursuing the
    /// objective", because that is all the policy knows to do with the wheel.
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
    /// intent alone hands the policy the wheel far past where its observation
    /// saturates. See <c>PolicyBot.ShouldEngage</c> for what that measured.
    /// </para>
    /// </remarks>
    MacroTask,

    /// <summary>
    /// No handover at all: the scripted layer always drives MOVEMENT, the policy
    /// always drives AIM and FIRE.
    /// </summary>
    /// <remarks>
    /// <b><c>gg2-rl</c>'s arm A, and the argument for it is that a parameter we
    /// keep getting wrong is a parameter worth deleting.</b> Proximity was wrong;
    /// intent was better; intent alone was catastrophic until it was conjoined
    /// with range. A head-level split has no trigger to get wrong, and the two
    /// concerns separate cleanly — the scripted layer never loses its path state
    /// or its objective, and the policy contributes only what it is measurably
    /// good at.
    /// <para>
    /// ⚠️ <b>`gg2-rl` argued its own user OUT of this shape and has withdrawn the
    /// objection.</b> The evidence was that the policy's movement is worth 16-23
    /// points — but measured against RANDOM movement and against walking straight
    /// at the enemy, never against competent nav-graph movement, because no such
    /// baseline existed on that side. In a match the alternative is not flailing,
    /// it is this repo's pathfinder.
    /// </para>
    /// <para>
    /// ⚠️ <b>What would falsify it</b>: the death share sliding back toward the
    /// all-scripted 50.5% would mean the policy's movement was carrying the combat
    /// gain after all.
    /// </para>
    /// </remarks>
    HeadSplit,
}

public sealed class PolicyBot : IInputSource, ICommandSource
{
    /// <summary>
    /// Straight-line pixels within which the policy drives.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>150, and it was 300 until it was measured in matches.</b> The
    /// ceiling is still an observation property rather than a preference — see the
    /// type's remarks — but the useful value turned out to be far below it.
    /// <b>The policy is most valuable used SPARINGLY</b>: at 150px it holds 7% of
    /// ticks and beats the all-scripted bot on both combat and captures, where at
    /// 300px it holds 23% and loses the objective. See
    /// <see cref="DuelEngagementRange"/> for the sweep.
    /// <para>
    /// Straight line, not path distance: a wall-separated pair inside the range is
    /// still handed to the policy.
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

    /// <summary>
    /// The range this bot actually uses. <see cref="EngagementRange"/> by default.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Settable so the frontier can be looked for, not so it can be tuned
    /// by taste.</b> `gg2-rl` measured 300px in duels and warned against widening
    /// it past ~400 (the observation saturates); narrowing it is the untested
    /// direction, and the reason to look is that the macro-task gate got MORE
    /// captures on LESS control at no combat cost, which is a Pareto move rather
    /// than a slide along a trade.
    /// </remarks>
    public double Range { get; init; } = EngagementRange;

    /// <summary>
    /// The classes the policy was trained on, and therefore the only ones it may
    /// drive.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Five of the nine class columns were constant zero for every decision
    /// of every training run</b>, self block and enemy block, so their first-layer
    /// weights are still the random draw — byte-identical between iteration 400
    /// and 2400. Driving a demoman, medic, engineer, spy or pyro does not tell the
    /// policy which class it is; it injects untrained noise.
    /// <para>
    /// ⚠️ <b>The trained set is not a prefix.</b> Heavy is <c>PlayerClass</c> index
    /// 6, so the untrained columns are 3, 4, 5, 7 and 8 — interleaved. Anything
    /// that assumes "the first four" is wrong.
    /// </para>
    /// <para>
    /// ⚠️ <b>An untrained class falls back to the scripted bot ENTIRELY</b> rather
    /// than being refused. A hybrid bot that would not play is worse than what
    /// ships today, and the scripted layer is a complete bot on its own.
    /// </para>
    /// </remarks>
    public static ReadOnlySpan<PlayerClass> TrainedClasses =>
        [PlayerClass.Scout, PlayerClass.Soldier, PlayerClass.Sniper, PlayerClass.Heavy];

    private readonly PolicyNetwork _policy;
    private readonly ScriptedBot _scripted;
    private readonly ActionDecoder _decoder = new();
    private readonly float[] _observation;
    private readonly int[] _heads;

    private EnvAction _action = EnvAction.Idle;
    private int _ticksLeftInDecision;
    private int _slot = -1;
    private bool _policyHadControl;

    /// <summary>
    /// Whether the scripted layer keeps control while this character is carrying
    /// the intel.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>On by default, and the measurement is the reason.</b> Over 108
    /// matches a side, wiring the policy in took red from <b>20 captures to 2</b>
    /// while blue's stayed at 12 in both arms — the policy fights instead of
    /// running the flag home, which is exactly what a reward of damage dealt
    /// minus taken with no objective term predicts. `gg2-rl`'s architecture says
    /// the macro layer owns objectives entirely, and <b>a character carrying the
    /// intel is on an objective task</b>, so this is that rule applied rather
    /// than an exception to it.
    /// <para>
    /// ⚠️ <b>Settable so the measurement can be repeated.</b> A default that
    /// could not be turned off would make the 20-versus-2 number unreproducible
    /// by anyone who came later.
    /// </para>
    /// </remarks>
    public bool ScriptedCarriesIntel { get; init; } = true;

    /// <summary>Which predicate decides when the policy takes the wheel.</summary>
    /// <remarks>
    /// ⚠️ <b><see cref="HandoverRule.MacroTask"/> by default, from the 2026-09-01
    /// sweep.</b> It gets more captures on LESS control at a small combat cost
    /// than <see cref="HandoverRule.Distance"/>, and it is time-bounded, which
    /// proximity is not.
    /// </remarks>
    public HandoverRule Rule { get; init; } = HandoverRule.MacroTask;

    public PolicyBot(PolicyNetwork policy, ScriptedBot scripted)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(scripted);

        _policy = policy;
        _scripted = scripted;
        _observation = new float[policy.ObservationLength];
        _heads = new int[ActionDecoder.HeadShape.Length];
    }

    /// <summary>Whether the policy drove the most recent tick. For a test and a diagnostic.</summary>
    public bool PolicyDrove { get; private set; }

    /// <summary>Ticks the policy has driven since this bot was made.</summary>
    public long PolicyTicks { get; private set; }

    /// <summary>Ticks the scripted layer has driven.</summary>
    public long ScriptedTicks { get; private set; }

    /// <summary>Whether this class is one the policy was trained on.</summary>
    public static bool IsTrained(PlayerClass playerClass)
    {
        foreach (var trained in TrainedClasses)
        {
            if (trained == playerClass)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Commands — team and class choice, drops, taunts — always come from the
    /// scripted layer.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The policy has no command head and must never acquire one.</b>
    /// <c>gg2-rl</c>'s reward is damage dealt minus taken with no objective term,
    /// by its user's decision — a bot that runs past you to tap the intel is
    /// optimal and unpleasant. The macro layer owns objectives entirely.
    /// </remarks>
    public void PollCommands(in WorldView world, int slot, int tick, ref PlayerCommandBuffer commands) =>
        _scripted.PollCommands(in world, slot, tick, ref commands);

    public PlayerInput Poll(in WorldView world, int slot, int tick)
    {
        if (slot != _slot)
        {
            _slot = slot;
            _ticksLeftInDecision = 0;
            _policyHadControl = false;
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
        // ⚠️ It costs a scripted poll every tick, which is the cheap half of this
        // bot, and it is what makes "the macro layer owns objectives" true rather
        // than aspirational.
        var scripted = _scripted.Poll(in world, slot, tick);

        // A dead or untrained-class character is the scripted layer's entirely —
        // and so is one carrying the intel, which is an objective task and
        // therefore the macro layer's by the architecture's own rule.
        if (!world.IsAlive(slot)
            || !IsTrained(self.Class)
            || (ScriptedCarriesIntel && self.CarryingIntel))
        {
            return Scripted(scripted);
        }

        var firstTickOfDecision = _ticksLeftInDecision <= 0;

        // ⚠️ Control changes hands only on a DECISION boundary, never mid-decision.
        // Testing the range every tick would flap the two controllers against each
        // other at the threshold, and each has state the other does not maintain —
        // the scripted layer's follower, this decoder's absolute aim. It also
        // keeps the 300px gate exact rather than needing a hysteresis band nobody
        // measured.
        if (Rule == HandoverRule.HeadSplit)
        {
            if (firstTickOfDecision)
            {
                _ticksLeftInDecision = Math.Max(1, _policy.ActionRepeat);
                Decide(in world, slot);
            }

            return Merge(scripted, PolicyInput(slot, firstTickOfDecision));
        }

        if (!firstTickOfDecision)
        {
            return _policyHadControl
                ? PolicyInput(slot, firstTick: false)
                : Scripted(scripted);
        }

        if (!ShouldEngage(in world, slot))
            return Scripted(scripted);

        // ⚠️ Resync the decoder's absolute aim on every handover INTO the policy.
        // The scripted layer writes the character's bearing directly, so a decoder
        // carrying the bearing from the last time the policy had control would
        // turn from somewhere nobody is pointing. RolloutSearch.Poll documents the
        // same trap for the same reason.
        if (!_policyHadControl)
            _decoder.SetAim(slot, self.NetAimDirection * 360.0 / 65536.0);

        _policyHadControl = true;

        // ⚠️ The network runs once per ActionRepeat ticks, which is the cadence it
        // was trained at and is carried by the checkpoint. Running it every tick
        // would be a policy acting on a rhythm it never saw.
        _ticksLeftInDecision = Math.Max(1, _policy.ActionRepeat);

        Decide(in world, slot);

        return PolicyInput(slot, firstTick: true);
    }

    /// <summary>Whether the policy should take the wheel this decision.</summary>
    private bool ShouldEngage(in WorldView world, int slot)
    {
        var inRange = DistanceToNearestEnemy(in world, slot) <= Range;

        // ⚠️ MacroTask is a CONJUNCTION, and the range half is not optional.
        // Gating on the scripted engage hold ALONE was tried and is wrong: that
        // hold fires out to BotTargeting.ClassRange, which is 800px for a soldier
        // and 900 for a sniper — three times this range. It took the policy's
        // share of control from 27% to 71% and handed it the wheel at distances
        // its observation cannot describe, which is the thing the 400px ceiling
        // exists to forbid. Captures fell to 3 a hundred matches.
        //
        // `gg2-rl` predicted this failure exactly: "your control-share number is
        // the mechanism dial. A goal-gated handover should cut it sharply. If it
        // does not move, the gate is not doing what we think." It did not move
        // down; it more than doubled.
        return Rule switch
        {
            HandoverRule.MacroTask => inRange && _scripted.WantsToEngage,
            _ => inRange,
        };
    }

    private void Decide(in WorldView world, int slot)
    {
        ObservationWriter.Write(in world, slot, _observation);
        _policy.Act(_observation, _heads);

        _action = new EnvAction
        {
            Horizontal = (byte)_heads[0],
            Jump = (byte)_heads[1],
            Down = (byte)_heads[2],
            Fire = (byte)_heads[3],
            AimDelta = (byte)_heads[4],
            Zoom = (byte)_heads[5],
        };
    }

    /// <summary>
    /// Scripted movement, policy aim and fire.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b><c>Special</c> goes with FIRE, not with movement.</b> It is the
    /// policy's third fire value — a medic's uber, a sniper's zoom, a soldier's
    /// nothing — and leaving it on the scripted side would let the two halves
    /// press two different attacks in one tick.
    /// <para>
    /// ⚠️ <b><c>Taunt</c> stays with the scripted layer</b>, because the policy
    /// has no taunt head and a taunt freezes the character where it stands.
    /// </para>
    /// </remarks>
    private static PlayerInput Merge(PlayerInput scripted, PlayerInput policy)
    {
        const PlayerKeys Movement = PlayerKeys.Left | PlayerKeys.Right | PlayerKeys.Jump
            | PlayerKeys.Down | PlayerKeys.Taunt;
        const PlayerKeys Combat = PlayerKeys.Attack | PlayerKeys.Special;

        return new PlayerInput(
            (scripted.Keys & Movement) | (policy.Keys & Combat),
            policy.NetAimDirection,
            policy.AimDistance);
    }

    private PlayerInput PolicyInput(int slot, bool firstTick)
    {
        _ticksLeftInDecision--;
        PolicyDrove = true;
        PolicyTicks++;

        return _decoder.Decode(slot, _action, firstTick);
    }

    private PlayerInput Scripted(PlayerInput scripted)
    {
        _ticksLeftInDecision = 0;
        _policyHadControl = false;
        PolicyDrove = false;
        ScriptedTicks++;

        return scripted;
    }

    private static double DistanceToNearestEnemy(in WorldView world, int slot)
    {
        Span<int> found = stackalloc int[1];

        if (world.NearestEnemies(slot, found) == 0)
            return double.PositiveInfinity;

        ref readonly var self = ref world.Character(slot);
        ref readonly var enemy = ref world.Character(found[0]);

        var dx = enemy.X - self.X;
        var dy = enemy.Y - self.Y;

        return Math.Sqrt((dx * dx) + (dy * dy));
    }
}
