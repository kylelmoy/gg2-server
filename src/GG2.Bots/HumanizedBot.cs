using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Bots;

/// <summary>
/// Reshapes a bot's aim, inside a fight, into the shape a person's takes. It
/// wraps whatever brain the server built (scripted or hybrid) and changes only
/// the aim that leaves it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists.</b> A classifier given one second of in-range play tells the
/// fleet's bots from its people at AUC 0.915 (<c>gg2-rl</c> <c>docs/STATUS.md</c>,
/// phase 5, 2026-10-09; a human-vs-human control reads 0.514). Its three biggest
/// signals are below, and all of them are the same for the scripted and policy
/// brains. This targets the two an observer can see, the aim; the first is a key
/// state (see the movement paragraph):
/// </para>
/// <list type="table">
/// <listheader><term>enemy within 300px</term><description>people / <c>Policy</c> bots, fleet traces 2026-09-15 to 2026-10-09</description></listheader>
/// <item><term>horizontal key held</term><description>median 10 ticks, p25 4 / median 3, p25 1</description></item>
/// <item><term>|turn| p95, p99, p99.9</term><description>6.2, 25.9, 135 deg / 24, 24, 30: a plateau at the tier-5 <see cref="BotSkill.TurnRate"/></description></item>
/// <item><term>aim miss, p10</term><description>6.3 px / 1.7 px</description></item>
/// </list>
/// <para>
/// <b>No key is touched, movement included.</b> v2 held a new horizontal key for
/// 5-12 ticks, which matched the people's key-hold lengths and made the bots
/// <em>less</em> human to watch. A player sees motion, never keys, and the bots'
/// velocity already reverses at the people's rate (0.41 a second against 0.45). A
/// key held past where the brain let go overshoots and comes back: 0.54 a second.
/// The hold bought no strength either. Most of the key churn is the path
/// follower's arc tracker trimming on and off in the air, which is what makes its
/// jumps land (<c>gg2-rl</c> <c>docs/PLAN.md</c>, phase 5, "the churn is in a key").
/// </para>
/// <para>
/// <b>Aim: a flick and corrections, not a constant slew.</b> A person moves
/// the crosshair in two ways: a flick to a new target, after a reaction delay,
/// that lands a little off; and small corrections in between, with a still mouse
/// much of the time. Both are <em>smooth</em>: a person's turn on one tick predicts
/// the next (lag-1 autocorrelation 0.43 within 300px, the bots' 0.27) and one
/// movement lasts about 3.5 ticks. That smoothness is what a classifier reads
/// best (<c>gg2-rl</c> <c>docs/PLAN.md</c>, phase 5, A2). With
/// <see cref="HumanizeOptions.Strokes"/> a flick is a minimum-jerk stroke over several
/// ticks and a correction low-passes the turn rate; the defaults are v3, one-tick
/// flicks and unsmoothed corrections (see <see cref="HumanizeOptions.Default"/>).
/// The brain's aim is treated as where the bot <em>wants</em> to look, and
/// the output follows it that way.
/// The output never feeds back: the brain keeps its own aim, as
/// <see cref="HybridBot"/>'s handicap already relies on.
/// </para>
/// <para>
/// <b>The trigger is not touched.</b> A held key and a tapped one are the same
/// shots on a refire-limited weapon, and no observer sees keys. The classifier's
/// fire features partly read that unobservable difference.
/// </para>
/// <para>
/// <b>Only inside a fight.</b> The gate is the scripted layer's own: a visible
/// combat target within <see cref="HumanizeOptions.Range"/>. Outside it the input
/// passes through unchanged and the filter re-syncs to it, so rocket-jump aim and
/// everything else outside a fight are exactly what they were, and a fight
/// starts from the brain's own aim rather than a stale one.
/// </para>
/// <para>
/// <b>Its own random stream.</b> Never <c>WorldState.Random</c>, which is
/// observable simulation, and never <see cref="ScriptedBot.Random"/>, whose
/// position the scripted layer's behaviour depends on: drawing from it would make
/// this filter change the brain's decisions as well as its output.
/// </para>
/// </remarks>
public sealed class HumanizedBot : IInputSource, ICommandSource, IWrapsScriptedBot
{
    private readonly IInputSource _inner;
    private readonly ScriptedBot _scripted;
    private GmlRandom _random;

    private int _slot = -1;
    private bool _engaged;

    private double _aim;
    private int _reactLeft = -1;
    private double _velocity;
    private double _shown = double.NaN;

    private double _strokeFrom;
    private double _strokeAmplitude;
    private int _strokeTicks;
    private int _strokeAt;

    // D6, the legs: their own stream, so a preset without them draws the aim's
    // sequence unchanged.
    private readonly HybridBot? _hybrid;
    private GmlRandom _legsRandom;
    private bool _legsOn;
    private double _intent;
    private double _activity;
    private int _heading;
    private bool? _jumpDecision;
    private bool _jumpOut;
    private int _jumpAt = int.MinValue / 2;

    public HumanizedBot(IInputSource inner, HumanizeOptions options, uint seed)
    {
        _inner = inner;
        _scripted = ScriptedBot.Of(inner)
            ?? throw new ArgumentException("the filter needs a scripted layer to read the fight from", nameof(inner));
        Options = options;

        // Offset from the stagger the scripted layer seeds itself with, so the
        // two streams of one bot are not the same sequence.
        _random = new GmlRandom(seed ^ 0x9E3779B9u);
        _legsRandom = new GmlRandom(seed ^ 0x85EBCA6Bu);
        _hybrid = HybridBot.Of(inner);
    }

    public HumanizeOptions Options { get; }

    /// <summary>The brain this filter wraps.</summary>
    public IInputSource Inner => _inner;

    /// <summary>Whether the last poll was inside a fight, so the filter acted.</summary>
    public bool Engaged => _engaged;

    ScriptedBot IWrapsScriptedBot.Scripted => _scripted;

    public void PollCommands(in WorldView world, int slot, int tick, ref PlayerCommandBuffer commands)
    {
        if (_inner is ICommandSource source)
            source.PollCommands(in world, slot, tick, ref commands);
    }

    /// <summary>
    /// The difficulty knob: every bot is wrapped, and the filter runs only while the
    /// bot's current <see cref="BotSkill.HumanAim"/> asks for it, with
    /// <see cref="HumanizeOptions.Tracking2"/> (D4; <see cref="HumanizeOptions.Tracking"/> was D3). Otherwise it is an exact pass-through
    /// that draws nothing, so tier 5 plays exactly as it would unwrapped.
    /// </summary>
    /// <param name="inner">The brain.</param>
    /// <param name="seed">The filter's own stream.</param>
    /// <param name="options">The preset, or null for <see cref="HumanizeOptions.Tracking2"/>; a bench arm sets it.</param>
    public static HumanizedBot ByTier(IInputSource inner, uint seed, HumanizeOptions? options = null) =>
        new(inner, options ?? HumanizeOptions.Tracking2, seed) { OnlyWhenSkillAsks = true };

    /// <summary>Run only while the scripted layer's skill has <see cref="BotSkill.HumanAim"/>.</summary>
    public bool OnlyWhenSkillAsks { get; init; }

    public PlayerInput Poll(in WorldView world, int slot, int tick)
    {
        var input = PollAim(in world, slot, tick);

        return Options.FiltersLegs ? Legs(input, in world, slot, tick) : input;
    }

    private PlayerInput PollAim(in WorldView world, int slot, int tick)
    {
        var input = _inner.Poll(in world, slot, tick);

        if (OnlyWhenSkillAsks && !_scripted.Skill.HumanAim)
        {
            _engaged = false;
            _shown = double.NaN;
            return input;
        }

        if (slot != _slot)
        {
            _slot = slot;
            _engaged = false;
        }

        var alive = world.IsAlive(slot);
        var engaged = alive && InFight(in world, slot);

        if (!engaged)
        {
            _engaged = false;

            // D2: with no target and an enemy close by, the reference brain points
            // the aim along the walk, so a bot that reverses snaps half a turn.
            // Holding it instead is what a person does between targets.
            if (Options.HoldWithoutTarget && alive && !double.IsNaN(_shown)
                && !_scripted.Combat.Target.Exists && EnemyWithin(in world, slot))
            {
                return new PlayerInput(input.Keys, Wire(_shown), input.AimDistance);
            }

            _shown = alive ? Degrees(input.NetAimDirection) : double.NaN;
            return input;
        }

        if (!_engaged)
        {
            // Entering a fight: start from what the brain is doing now, or, when
            // the aim was being held, from where it is actually pointing.
            _engaged = true;
            _aim = Options.HoldWithoutTarget && !double.IsNaN(_shown) ? _shown : Degrees(input.NetAimDirection);
            _reactLeft = -1;
            _velocity = 0;
            _strokeTicks = 0;
        }

        var aim = FollowAim(Degrees(input.NetAimDirection), firing: (input.Keys & (PlayerKeys.Attack | PlayerKeys.Special)) != 0);
        _shown = aim;

        return new PlayerInput(input.Keys, Wire(aim), input.AimDistance);
    }

    /// <summary>
    /// D6: on ticks the policy drives, commit or stand. The brain's left/right is
    /// averaged, a direction starts only once the average is clearly one way and
    /// stops once it is not, and a reversal passes through a stop. A fast dance
    /// averages out, so the legs stand. Jump presses are thinned. The scripted
    /// layer's ticks pass untouched, since its jump arcs need every key.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The state runs on every tick, not only the policy's.</b> The wheel
    /// changes hands in short bursts, and a filter that restarted on each one let
    /// the dance and its jumps straight through.
    /// </remarks>
    private PlayerInput Legs(PlayerInput input, in WorldView world, int slot, int tick)
    {
        if ((OnlyWhenSkillAsks && !_scripted.Skill.HumanAim) || !world.IsAlive(slot))
        {
            _legsOn = false;
            _jumpDecision = null;
            _jumpOut = false;
            return input;
        }

        var keys = input.Keys;
        var wanted = ((keys & PlayerKeys.Right) != 0 ? 1 : 0) - ((keys & PlayerKeys.Left) != 0 ? 1 : 0);
        var jump = (keys & PlayerKeys.Jump) != 0;
        var policy = _hybrid is { MicroDrove: true };

        if (!_legsOn)
        {
            _legsOn = true;
            _intent = wanted;
            _activity = Math.Abs(wanted);
            _heading = wanted;
        }
        else
        {
            _intent = (Options.LegMemory * _intent) + ((1 - Options.LegMemory) * wanted);
            _activity = (Options.LegMemory * _activity) + ((1 - Options.LegMemory) * Math.Abs(wanted));

            if (Options.KeepsRunning)
            {
                if (_heading == 0)
                {
                    if (Math.Abs(_intent) >= Options.LegStartIntent || _activity >= Options.LegStartIntent)
                        _heading = _intent != 0 ? Math.Sign(_intent) : wanted;
                }
                else if (_intent * _heading <= -Options.LegReverseIntent)
                {
                    _heading = -_heading;
                }
                else if (_activity < Options.LegIdleActivity)
                {
                    _heading = 0;
                }
            }
            else if (_heading == 0)
            {
                if (Math.Abs(_intent) >= Options.LegStartIntent)
                    _heading = Math.Sign(_intent);
            }
            else if (_intent * _heading < Options.LegStopIntent)
            {
                _heading = 0;
            }
        }

        // One decision a hold of the brain's key, made when passing it would be a
        // press: stripping only a press's first tick would make its second one.
        if (!jump)
        {
            _jumpDecision = null;
        }
        else if (_jumpDecision is null)
        {
            if (!policy || _jumpOut)
            {
                _jumpDecision = true;
            }
            else
            {
                _jumpDecision = tick - _jumpAt >= Options.JumpRefractoryTicks && _legsRandom.Next(1) < Options.JumpKeep;

                if (_jumpDecision == true)
                    _jumpAt = tick;
            }
        }

        if (!policy)
        {
            _jumpOut = jump;
            return input;
        }

        keys &= ~(PlayerKeys.Left | PlayerKeys.Right);

        if (_heading > 0)
            keys |= PlayerKeys.Right;
        else if (_heading < 0)
            keys |= PlayerKeys.Left;

        if (jump && _jumpDecision != true)
            keys &= ~PlayerKeys.Jump;

        _jumpOut = (keys & PlayerKeys.Jump) != 0;

        return new PlayerInput(keys, input.NetAimDirection, input.AimDistance);
    }

    private bool InFight(in WorldView world, int slot)
    {
        var target = _scripted.Combat.Target;

        if (!target.Exists || !_scripted.Combat.Visible)
        {
            // D5: with no target the reference brain points the aim along the
            // walk, and a bot reversing near an enemy snaps half a turn. Counting
            // a close enemy as a fight sends that snap through the filter too.
            return Options.EngageWithoutTarget && EnemyWithin(in world, slot);
        }

        ref readonly var self = ref world.Character(slot);
        var dx = target.X - self.X;
        var dy = target.Y - self.Y;

        return (dx * dx) + (dy * dy) <= Options.Range * Options.Range;
    }

    private bool EnemyWithin(in WorldView world, int slot)
    {
        Span<int> nearest = stackalloc int[1];

        if (world.NearestEnemies(slot, nearest) == 0)
            return false;

        ref readonly var self = ref world.Character(slot);
        ref readonly var enemy = ref world.Character(nearest[0]);
        var dx = enemy.X - self.X;
        var dy = enemy.Y - self.Y;

        return (dx * dx) + (dy * dy) <= Options.Range * Options.Range;
    }

    private double FollowAim(double wanted, bool firing)
    {
        // A stroke in flight runs to its end: a person does not re-aim halfway
        // through a flick, and corrections resume once it has landed.
        if (_strokeTicks > 0)
            return AdvanceStroke();

        var error = Wrap(wanted - _aim);
        var size = Math.Abs(error);

        // Between shots a person lets the aim drift, and a bot that keeps
        // re-acquiring is turning twice as much as one. v5's idle values apply
        // while the brain presses neither attack nor special; NaN is "as firing".
        var saccade = firing || double.IsNaN(Options.IdleSaccadeDegrees) ? Options.SaccadeDegrees : Options.IdleSaccadeDegrees;
        var deadZone = firing || double.IsNaN(Options.IdleDeadZoneDegrees) ? Options.DeadZoneDegrees : Options.IdleDeadZoneDegrees;
        var gain = firing || double.IsNaN(Options.IdlePursuitGain) ? Options.PursuitGain : Options.IdlePursuitGain;

        if (size >= saccade)
        {
            // A flick waits out a reaction time from the moment the error grew
            // past the threshold, and lands with an error that scales with how
            // far it travelled.
            if (_reactLeft < 0)
                _reactLeft = Options.MinReactionTicks + _random.NextInclusive(Options.MaxReactionTicks - Options.MinReactionTicks);

            // Not reacted yet, so not moving towards it at all. A correction here
            // would close the gap before the flick was due and turn every flick
            // back into a slew.
            if (_reactLeft > 0)
            {
                _reactLeft--;
                return _aim;
            }

            _reactLeft = -1;
            _velocity = 0;
            _strokeFrom = _aim;
            _strokeAmplitude = Options.SaccadeGain * error * (1 + (Options.SaccadeEndpointError * Gaussian()));
            _strokeTicks = Math.Clamp(
                (int)Math.Round(Options.StrokeBaseTicks + (Math.Abs(_strokeAmplitude) / Options.StrokeDegreesPerTick)),
                Options.MinStrokeTicks,
                Options.MaxStrokeTicks);
            _strokeAt = 0;

            return AdvanceStroke();
        }

        _reactLeft = -1;

        // A correction moves the turn RATE toward a fraction of the error, so one
        // tick's turn carries into the next the way a hand's does. Its noise
        // scales with the velocity, so a still mouse stays still. Inside the dead
        // zone the hand coasts to a stop rather than freezing.
        var aimFor = size >= deadZone ? gain * error : 0.0;
        _velocity += Options.PursuitSmoothing * (aimFor - _velocity);

        if (aimFor == 0.0 && Math.Abs(_velocity) < Options.StopDegrees)
            _velocity = 0;
        else if (_velocity != 0)
        {
            _velocity *= 1 + (Options.PursuitNoise * Gaussian());

            // D4: a moving hand trembles, so the aim sometimes turns back for a
            // tick. Drawn only when set, so every preset without it is unchanged.
            if (Options.PursuitTremorDegrees > 0)
                _velocity += Options.PursuitTremorDegrees * Gaussian();
        }

        _aim = Wrap(_aim + _velocity);
        return _aim;
    }

    /// <summary>One tick along the current flick's minimum-jerk profile.</summary>
    /// <remarks>
    /// <c>10τ³ − 15τ⁴ + 6τ⁵</c>: the standard model of a human pointing movement.
    /// It starts and ends at rest with a bell-shaped speed, so consecutive ticks'
    /// turns are correlated and the movement lasts several ticks, which is what
    /// people's aim does and a one-tick jump does not.
    /// </remarks>
    private double AdvanceStroke()
    {
        _strokeAt++;
        var tau = _strokeAt / (double)_strokeTicks;
        _aim = Wrap(_strokeFrom + (_strokeAmplitude * ((10 * tau * tau * tau) - (15 * tau * tau * tau * tau) + (6 * tau * tau * tau * tau * tau))));

        if (_strokeAt >= _strokeTicks)
            _strokeTicks = 0;

        return _aim;
    }

    /// <summary>A standard normal draw, Box-Muller over this filter's own stream.</summary>
    private double Gaussian()
    {
        var u1 = 1.0 - _random.Next(1);
        var u2 = _random.Next(1);
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    private static double Degrees(ushort wire) => wire * 360.0 / 65536.0;

    private static ushort Wire(double degrees)
    {
        var d = degrees % 360.0;
        if (d < 0)
            d += 360.0;

        return (ushort)((int)Math.Round(d * 65536.0 / 360.0) & 0xFFFF);
    }

    private static double Wrap(double degrees)
    {
        var d = (degrees + 180.0) % 360.0;
        if (d < 0)
            d += 360.0;

        return d - 180.0;
    }
}

/// <summary>
/// <see cref="HumanizedBot"/>'s parameters, fitted to the fleet's people.
/// </summary>
/// <remarks>
/// <b>Fitted to distributions, never to the classifier.</b> The classifier is the
/// test; tuning against its twelve numbers would beat the instrument and leave a
/// bot a person still spots. <see cref="Default"/> was fitted by replaying a copy
/// of this filter, open loop, over a recorded bots-only server's brain output and
/// matching the people's in-range turn quantiles (p75-p99), still-mouse share,
/// reversal rate, lag-1 autocorrelation and movement length (<c>gg2-rl</c>
/// <c>docs/PLAN.md</c>, phase 5, v4).
/// Open loop means the brain did not see the filtered output, which it does live.
/// </remarks>
/// <param name="Range">Pixels within which a visible target makes it a fight. The classifier's wider band.</param>
/// <param name="SaccadeDegrees">An aim error at least this large is closed by a flick rather than a correction.</param>
/// <param name="MinReactionTicks">Fewest ticks before a flick fires.</param>
/// <param name="MaxReactionTicks">Most ticks before a flick fires. 2-4 ticks is 67-133 ms, on top of the scripted acquire delay.</param>
/// <param name="SaccadeEndpointError">A flick lands off by this fraction of its size, one standard deviation.</param>
/// <param name="StrokeBaseTicks">A flick's duration is this plus its size over <paramref name="StrokeDegreesPerTick"/>, rounded.</param>
/// <param name="StrokeDegreesPerTick">How much longer a bigger flick takes.</param>
/// <param name="MinStrokeTicks">Shortest flick.</param>
/// <param name="MaxStrokeTicks">Longest flick.</param>
/// <param name="PursuitGain">The turn rate a correction moves toward, as a fraction of the error.</param>
/// <param name="PursuitSmoothing">How far the turn rate moves toward that each tick: 1 is no smoothing.</param>
/// <param name="PursuitNoise">A correction's noise, as a fraction of its own velocity, one standard deviation.</param>
/// <param name="DeadZoneDegrees">Below this error the hand coasts to a stop.</param>
/// <param name="StopDegrees">A coasting turn below this stops.</param>
/// <param name="IdleSaccadeDegrees">
/// <paramref name="SaccadeDegrees"/> while the brain presses neither attack nor
/// special, or NaN for the same value. v5.
/// </param>
/// <param name="IdleDeadZoneDegrees"><paramref name="DeadZoneDegrees"/> between shots, or NaN. v5.</param>
/// <param name="IdlePursuitGain"><paramref name="PursuitGain"/> between shots, or NaN. v5.</param>
/// <param name="SaccadeGain">
/// The fraction of the error a flick covers, before its endpoint error. 1 closes
/// it; below 1 a flick is a partial correction that the next one finishes. D4.
/// </param>
/// <param name="PursuitTremorDegrees">
/// An additive turn, one standard deviation, on every tick a correction is
/// moving: the reversals a hand makes. 0 for none. D4.
/// </param>
/// <param name="EngageWithoutTarget">
/// An enemy within <paramref name="Range"/> is a fight even while the scripted layer
/// has no target, so the brain's walk-facing aim is filtered rather than passed
/// through. D5.
/// </param>
/// <param name="LegMemory">
/// D6. On ticks the policy drives, the brain's left/right is averaged with this
/// weight on the past (an exponential average). NaN, the default, leaves the legs
/// alone.
/// </param>
/// <param name="LegStartIntent">Standing, the legs start once the average reaches this, either way.</param>
/// <param name="LegStopIntent">Moving, they stop once the average toward the heading falls below this.</param>
/// <param name="LegReverseIntent">
/// D7. Set, the legs keep running through a dance: moving, they reverse straight
/// away once the average against the heading reaches this, and stop only once the
/// brain's activity (the average of \|left/right\|) falls below
/// <paramref name="LegIdleActivity"/>. Standing, a dance starts them too.
/// <paramref name="LegStopIntent"/> is then unused. NaN, the default, is D6.
/// </param>
/// <param name="LegIdleActivity">D7. Moving, the legs stop once the brain's activity falls below this.</param>
/// <param name="JumpKeep">The chance a policy jump press passes.</param>
/// <param name="JumpRefractoryTicks">No policy jump passes within this many ticks of the last one that did.</param>
/// <param name="HoldWithoutTarget">
/// While the scripted layer has no target and an enemy is within <paramref name="Range"/>,
/// hold the aim rather than following the walk. D2.
/// </param>
public sealed record HumanizeOptions(
    double Range = 300,
    double SaccadeDegrees = 20,
    int MinReactionTicks = 2,
    int MaxReactionTicks = 4,
    double SaccadeEndpointError = 0.08,
    double StrokeBaseTicks = 0,
    double StrokeDegreesPerTick = 40,
    int MinStrokeTicks = 1,
    int MaxStrokeTicks = 1,
    double PursuitGain = 0.4,
    double PursuitSmoothing = 1.0,
    double PursuitNoise = 0.3,
    double DeadZoneDegrees = 1.0,
    double StopDegrees = 0.3,
    double IdleSaccadeDegrees = double.NaN,
    double IdleDeadZoneDegrees = double.NaN,
    double IdlePursuitGain = double.NaN,
    bool HoldWithoutTarget = false,
    double SaccadeGain = 1.0,
    double PursuitTremorDegrees = 0,
    bool EngageWithoutTarget = false,
    double LegMemory = double.NaN,
    double LegStartIntent = 0.5,
    double LegStopIntent = 0.2,
    double JumpKeep = 1.0,
    int JumpRefractoryTicks = 0,
    double LegReverseIntent = double.NaN,
    double LegIdleActivity = 0.2)
{
    /// <summary>Whether D6's leg filter is on.</summary>
    public bool FiltersLegs => !double.IsNaN(LegMemory);

    /// <summary>Whether the legs keep running through a dance (D7) rather than stand (D6).</summary>
    public bool KeepsRunning => !double.IsNaN(LegReverseIntent);

    /// <summary>
    /// v3, the shipped shape: one-tick flicks and unsmoothed corrections. These
    /// defaults reproduce v3's output exactly.
    /// </summary>
    public static HumanizeOptions Default { get; } = new();

    /// <summary>
    /// v4: flicks as minimum-jerk strokes and smoothed corrections, fitted to the
    /// people's aim smoothness. <b>Measured and not the default</b>: it removes the
    /// smoothness tell (an independent learned discriminator ranks it most human,
    /// 0.816 against v3's 0.868 at 300px), but the pre-registered instrument moves
    /// by only 0.006 and it costs more strength (−0.042 death share against v3's
    /// −0.025). <c>gg2-rl</c> <c>docs/PLAN.md</c> phase 5, v4.
    /// </summary>
    public static HumanizeOptions Strokes { get; } = new(
        SaccadeDegrees: 12,
        StrokeBaseTicks: 2,
        StrokeDegreesPerTick: 40,
        MinStrokeTicks: 3,
        MaxStrokeTicks: 10,
        PursuitGain: 0.45,
        PursuitSmoothing: 0.6,
        PursuitNoise: 0.5,
        DeadZoneDegrees: 2.0);

    /// <summary>
    /// v5: v3 while firing, and lazy between shots. With the attack key up the aim
    /// drifts until the error passes 45°, then corrects at a fifth of it or flicks
    /// past 50°. Fitted open loop to the people's turn and aim error within 150px,
    /// split by fire: between shots people turn about 2°/tick and sit 35° off,
    /// where v3 turns about 4°/tick. <c>gg2-rl</c> <c>docs/PLAN.md</c> phase 5, v5.
    /// <b>Measured and not the default</b>: live, the drift is paid back by a flick
    /// when the brain fires again, so overall turning does not move. O2 reads 0.879
    /// against v3's 0.866, at −0.036 death share against v3's −0.024.
    /// </summary>
    public static HumanizeOptions Lazy { get; } = new(
        IdleSaccadeDegrees: 50,
        IdleDeadZoneDegrees: 45,
        IdlePursuitGain: 0.2);

    /// <summary>
    /// D2: v3, holding the aim while the scripted layer has no target and an enemy
    /// is close, instead of pointing it along the walk. <c>gg2-rl</c>
    /// <c>docs/PLAN.md</c> phase 5, D2. <b>Measured and not the default</b>: free in
    /// strength, but O2 reads 0.890 against v3's 0.864, because a held aim is dead
    /// still and then snaps when a target is acquired.
    /// </summary>
    public static HumanizeOptions Hold { get; } = new(HoldWithoutTarget: true);

    /// <summary>
    /// D3, tiers 1-4's aim: it lags the target the way people do. Corrections move
    /// at 8% of the error a tick and stop inside 16°, and only an error past 120°
    /// is flicked. Fitted open loop to the people's in-range turn size, p90, still
    /// share, aim error and smoothness (<c>gg2-rl</c> <c>scripts/fit_humanize_tracking.py</c>,
    /// <c>docs/PLAN.md</c> phase 5, D3). Tiers 1-4 used it until <see cref="Tracking2"/>
    /// replaced it the same day. Measured at tier 4: O2 0.901 to 0.870, and the fleet discriminator 0.747 to
    /// 0.618 within 150px.
    /// </summary>
    public static HumanizeOptions Tracking { get; } = new(
        SaccadeDegrees: 120,
        PursuitGain: 0.08,
        DeadZoneDegrees: 16);

    /// <summary>
    /// D4, <see cref="Tracking"/> with the reversals and flicks it lacked. Errors
    /// past 32° are closed by half, in 2-4 tick strokes that land within ±60%, so
    /// they sometimes overshoot; corrections inside that start at 8° and tremble
    /// by 0.75° a tick. Fitted open loop to the people's in-range turn size, still
    /// share, aim error, smoothness, reversals, flicks over 30° and movement
    /// length (<c>gg2-rl</c> <c>scripts/fit_humanize_tracking.py --d4</c>,
    /// <c>docs/PLAN.md</c> phase 5, D4). <b>What tiers 1-4 use</b>, through
    /// <see cref="HumanizedBot.ByTier"/>: at tier 4 against <see cref="Tracking"/>, O2
    /// 0.883 to 0.809 with the ladder unchanged; the fleet discriminator read 0.617
    /// to 0.629, inside its noise, and the user adopted it on 2026-10-10.
    /// <para>
    /// <b>D5 added <see cref="EngageWithoutTarget"/></b>, so the brain's walk snaps
    /// near an enemy are filtered too: O2 0.803 to 0.767, the fleet discriminator
    /// 0.635 to 0.607 (0.771 to 0.652 at 300px), and the ladder still even.
    /// </para>
    /// </summary>
    public static HumanizeOptions Tracking2 { get; } = new(
        SaccadeDegrees: 32,
        SaccadeEndpointError: 0.6,
        SaccadeGain: 0.5,
        StrokeBaseTicks: 1,
        StrokeDegreesPerTick: 10,
        MinStrokeTicks: 2,
        MaxStrokeTicks: 4,
        PursuitGain: 0.08,
        DeadZoneDegrees: 8,
        PursuitTremorDegrees: 0.75,
        EngageWithoutTarget: true);

    /// <summary>
    /// D6's candidate: <see cref="Tracking2"/> with legs. On ticks the policy drives,
    /// its left/right is averaged at 0.9 a tick; the legs start at 0.75 and stop
    /// when the average crosses zero, and one jump press in ten passes, at most one
    /// every 90 ticks. Fitted with <c>BENCH_TURNS</c> at tier 4 to the people's
    /// still share, \|Δhspeed\|, reversals and take-offs within 150px
    /// (<c>gg2-rl</c> <c>docs/PLAN.md</c> phase 5, D6).
    /// </summary>
    public static HumanizeOptions Tracking3 { get; } = Tracking2 with
    {
        LegMemory = 0.9,
        LegStartIntent = 0.75,
        LegStopIntent = 0.0,
        JumpKeep = 0.1,
        JumpRefractoryTicks = 90,
    };

    /// <summary>
    /// D7's candidate: <see cref="Tracking3"/>'s jumps, with legs that keep running
    /// through the policy's dance instead of standing. Averaged at 0.95 a tick; a
    /// reversal needs the average at 0.75 against the heading, and the legs stop
    /// once the brain's activity falls below 0.3. Fitted with <c>BENCH_TURNS</c> at
    /// tier 4 to the people's reversals, \|Δhspeed\| and take-offs and a speed
    /// target (<c>gg2-rl</c> <c>docs/PLAN.md</c> phase 5, D7).
    /// </summary>
    public static HumanizeOptions Tracking4 { get; } = Tracking3 with
    {
        LegMemory = 0.95,
        LegStartIntent = 0.75,
        LegReverseIntent = 0.75,
        LegIdleActivity = 0.3,
    };

    /// <summary>
    /// D8's candidate: <see cref="Tracking2"/> with its timing refitted. Only errors
    /// past 48° are stroked, the pursuit closes 12% a tick and trembles by 1°, so
    /// more of the closing is persistent motion rather than short bursts. Fitted open
    /// loop with the people's window p90, lag-3 autocorrelation and jerk added to D4's
    /// targets (<c>gg2-rl</c> <c>scripts/fit_humanize_tracking.py --d8 --d8b</c>,
    /// <c>docs/PLAN.md</c> phase 5, D8).
    /// </summary>
    public static HumanizeOptions Tracking5 { get; } = Tracking2 with
    {
        SaccadeDegrees = 48,
        PursuitGain = 0.12,
        PursuitTremorDegrees = 1.0,
    };

    /// <summary>
    /// D9's candidate: <see cref="Tracking5"/> with pursuit gain 0.10, between D4's
    /// and D8's, to give back the aim error the fleet discriminator read in D8
    /// (<c>gg2-rl</c> <c>docs/PLAN.md</c> phase 5, D9).
    /// </summary>
    public static HumanizeOptions Tracking6 { get; } = Tracking5 with { PursuitGain = 0.10 };

    /// <summary>The preset names a setting can carry.</summary>
    public static IReadOnlyList<string> PresetNames { get; } = ["v3", "strokes", "lazy", "hold", "tracking", "tracking2", "tracking3", "tracking4", "tracking5", "tracking6"];

    /// <summary>A preset's canonical name in <see cref="PresetNames"/>, refusing anything else.</summary>
    /// <exception cref="FormatException">The name is not a preset.</exception>
    public static string PresetName(string name)
    {
        var options = ForPreset(name);
        return options == Strokes ? "strokes" : options == Lazy ? "lazy" : options == Hold ? "hold" : options == Tracking ? "tracking" : options == Tracking2 ? "tracking2" : options == Tracking3 ? "tracking3" : options == Tracking4 ? "tracking4" : options == Tracking5 ? "tracking5" : options == Tracking6 ? "tracking6" : "v3";
    }

    /// <summary>A preset by name, case-insensitive.</summary>
    /// <exception cref="FormatException">The name is not one of <see cref="PresetNames"/>.</exception>
    public static HumanizeOptions ForPreset(string name) => name.Trim().ToLowerInvariant() switch
    {
        "v3" => Default,
        "strokes" or "v4" => Strokes,
        "lazy" or "v5" => Lazy,
        "hold" or "d2" => Hold,
        "tracking" or "d3" => Tracking,
        // tracking2e was D5's candidate, folded into Tracking2 when it passed.
        "tracking2" or "d4" or "tracking2e" or "d5" => Tracking2,
        "tracking3" or "d6" => Tracking3,
        "tracking4" or "d7" => Tracking4,
        "tracking5" or "d8" => Tracking5,
        "tracking6" or "d9" => Tracking6,
        _ => throw new FormatException($"humanize preset '{name}': v3, strokes, lazy, hold, tracking, tracking2, tracking3, tracking4, tracking5 or tracking6"),
    };
}
