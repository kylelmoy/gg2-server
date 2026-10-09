using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Bots;

/// <summary>
/// Reshapes a bot's movement keys and aim, inside a fight, into the shape a
/// person's take. It wraps whatever brain the server built (scripted or hybrid)
/// and changes only what leaves it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists.</b> A classifier given one second of in-range play tells the
/// fleet's bots from its people at AUC 0.915 (<c>gg2-rl</c> <c>docs/STATUS.md</c>,
/// phase 5, 2026-10-09; a human-vs-human control reads 0.514). Its three biggest
/// signals are the ones this targets, and all of them are the same for the
/// scripted and policy brains:
/// </para>
/// <list type="table">
/// <listheader><term>enemy within 300px</term><description>people / <c>Policy</c> bots, fleet traces 2026-09-15 to 2026-10-09</description></listheader>
/// <item><term>horizontal key held</term><description>median 10 ticks, p25 4 / median 3, p25 1</description></item>
/// <item><term>|turn| p95, p99, p99.9</term><description>6.2, 25.9, 135 deg / 24, 24, 30: a plateau at the tier-5 <see cref="BotSkill.TurnRate"/></description></item>
/// <item><term>aim miss, p10</term><description>6.3 px / 1.7 px</description></item>
/// </list>
/// <para>
/// <b>Movement: a minimum hold.</b> Once the horizontal key changes, the new
/// value is held for <see cref="HumanizeOptions.MinHoldTicks"/> to
/// <see cref="HumanizeOptions.MaxHoldTicks"/> ticks before another change is
/// let through. That removes the one- and two-tick flickers that a person never
/// produces. Jump, down and every other key pass through untouched.
/// </para>
/// <para>
/// <b>Aim: saccade and pursuit, not a constant slew.</b> A person moves the
/// crosshair in two ways: a fast flick to a new target, after a reaction delay,
/// that lands a little off; and small corrections in between, with a still mouse
/// much of the time and corrections noisy enough to overshoot. The brain's aim is treated as
/// where the bot <em>wants</em> to look, and the output follows it that way.
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
/// passes through unchanged and the filter re-syncs to it, so routing, edge
/// flights and objective play are exactly what they were and a fight starts from
/// the brain's own aim rather than a stale one.
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

    private PlayerKeys _horizontal;
    private int _holdLeft;

    private double _aim;
    private int _reactLeft = -1;

    public HumanizedBot(IInputSource inner, HumanizeOptions options, uint seed)
    {
        _inner = inner;
        _scripted = ScriptedBot.Of(inner)
            ?? throw new ArgumentException("the filter needs a scripted layer to read the fight from", nameof(inner));
        Options = options;

        // Offset from the stagger the scripted layer seeds itself with, so the
        // two streams of one bot are not the same sequence.
        _random = new GmlRandom(seed ^ 0x9E3779B9u);
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

    public PlayerInput Poll(in WorldView world, int slot, int tick)
    {
        var input = _inner.Poll(in world, slot, tick);

        if (slot != _slot)
        {
            _slot = slot;
            _engaged = false;
        }

        var engaged = world.IsAlive(slot) && InFight(in world, slot);

        if (!engaged)
        {
            _engaged = false;
            return input;
        }

        if (!_engaged)
        {
            // Entering a fight: start from what the brain is doing now.
            _engaged = true;
            _horizontal = input.Keys & Horizontal;
            _holdLeft = 0;
            _aim = Degrees(input.NetAimDirection);
            _reactLeft = -1;
        }

        var keys = HoldMovement(input.Keys);
        var aim = FollowAim(Degrees(input.NetAimDirection));

        return new PlayerInput(keys, Wire(aim), input.AimDistance);
    }

    private const PlayerKeys Horizontal = PlayerKeys.Left | PlayerKeys.Right;

    private bool InFight(in WorldView world, int slot)
    {
        var target = _scripted.Combat.Target;

        if (!target.Exists || !_scripted.Combat.Visible)
            return false;

        ref readonly var self = ref world.Character(slot);
        var dx = target.X - self.X;
        var dy = target.Y - self.Y;

        return (dx * dx) + (dy * dy) <= Options.Range * Options.Range;
    }

    private PlayerKeys HoldMovement(PlayerKeys keys)
    {
        var wanted = keys & Horizontal;

        if (_holdLeft > 0)
            _holdLeft--;

        if (wanted != _horizontal)
        {
            if (_holdLeft > 0)
                return (keys & ~Horizontal) | _horizontal;

            _horizontal = wanted;
            _holdLeft = Options.MinHoldTicks + _random.NextInclusive(Options.MaxHoldTicks - Options.MinHoldTicks);
        }

        return keys;
    }

    private double FollowAim(double wanted)
    {
        var error = Wrap(wanted - _aim);
        var size = Math.Abs(error);

        if (size >= Options.SaccadeDegrees)
        {
            // A flick waits out a reaction time from the moment the error grew
            // past the threshold, then lands with an error that scales with how
            // far it travelled.
            if (_reactLeft < 0)
                _reactLeft = Options.MinReactionTicks + _random.NextInclusive(Options.MaxReactionTicks - Options.MinReactionTicks);

            // Not reacted yet, so not moving towards it at all. A pursuit here
            // would close the gap before the flick was due and turn every flick
            // back into a slew.
            if (_reactLeft > 0)
            {
                _reactLeft--;
                return _aim;
            }

            _reactLeft = -1;
            _aim = Wrap(_aim + (error * (1 + (Options.SaccadeEndpointError * Gaussian()))));
            return _aim;
        }

        _reactLeft = -1;

        // Close enough: the mouse is still. Otherwise a partial correction whose
        // noise scales with the step, so a still mouse stays still and a large
        // correction can overshoot and reverse. A constant per-tick tremor
        // reversed five to seven times a second against the people's one.
        if (size < Options.DeadZoneDegrees)
            return _aim;

        _aim = Wrap(_aim + (Options.PursuitGain * error * (1 + (Options.PursuitNoise * Gaussian()))));
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
/// reversal rate and key-hold quantiles (<c>gg2-rl</c> <c>docs/PLAN.md</c>, phase 5).
/// Open loop means the brain did not see the filtered output, which it does live.
/// </remarks>
/// <param name="Range">Pixels within which a visible target makes it a fight. The classifier's wider band.</param>
/// <param name="MinHoldTicks">Shortest hold of a new horizontal key. The people's p25 is 5 ticks.</param>
/// <param name="MaxHoldTicks">Longest forced hold. People hold for a median of 10 ticks; a floor drawn from 5-12 puts the filtered median near it.</param>
/// <param name="SaccadeDegrees">An aim error at least this large is closed by a flick rather than a correction.</param>
/// <param name="MinReactionTicks">Fewest ticks before a flick fires.</param>
/// <param name="MaxReactionTicks">Most ticks before a flick fires. 2-4 ticks is 67-133 ms, on top of the scripted acquire delay.</param>
/// <param name="SaccadeEndpointError">A flick lands off by this fraction of its size, one standard deviation.</param>
/// <param name="PursuitGain">Fraction of a smaller error closed per tick.</param>
/// <param name="PursuitNoise">A correction's noise, as a fraction of its own size, one standard deviation.</param>
/// <param name="DeadZoneDegrees">Below this error the mouse is still. The people's share of ticks with no turn is 0.55.</param>
public sealed record HumanizeOptions(
    double Range = 300,
    int MinHoldTicks = 5,
    int MaxHoldTicks = 12,
    double SaccadeDegrees = 20,
    int MinReactionTicks = 2,
    int MaxReactionTicks = 4,
    double SaccadeEndpointError = 0.08,
    double PursuitGain = 0.4,
    double PursuitNoise = 0.3,
    double DeadZoneDegrees = 1.0)
{
    public static HumanizeOptions Default { get; } = new();
}
