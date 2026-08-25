using Gg2.Protocol;

namespace Gg2.Simulation;

/// <summary>
/// Game Maker 8's built-in motion variables — <c>hspeed</c>, <c>vspeed</c>,
/// <c>direction</c> and <c>speed</c> — and the way writing one rewrites the
/// others.
/// </summary>
/// <remarks>
/// <b>All four are stored, and none is derived on read.</b> docs/SIMULATION.md
/// used to say <c>speed</c> and <c>direction</c> were "derived from
/// <c>hspeed</c>/<c>vspeed</c>", which is true only for an instance that never
/// writes them — which is every character in phase 4 and no projectile at all.
/// A shot is created with <c>direction</c> and <c>speed</c>, then has
/// <c>hspeed</c> added to it, then <c>speed</c> and <c>direction</c> adjusted
/// again (<c>Objects/Weapons/Scattergun.events/User Event 3.xml</c>), so it
/// round-trips between the two representations three times before it moves.
/// <para>
/// Two details of that round trip change results rather than last digits, and
/// both are transcribed below:
/// </para>
/// <list type="bullet">
/// <item>
/// Recomputing <c>hspeed</c>/<c>vspeed</c> <b>snaps each to the nearest integer
/// when it lands within 1e-4 of one</b>. A shot fired at exactly 90 degrees gets
/// <c>hspeed = 0</c>, not <c>7.9e-16</c> — which matters because
/// <see cref="Gml.Sign"/>'s fuzz is 1e-13, a thousand times tighter than this
/// snap.
/// </item>
/// <item>
/// Writing <c>hspeed</c> or <c>vspeed</c> a value it already holds is a
/// <b>no-op</b>: <c>direction</c> is not recomputed, so an instance stopped dead
/// keeps pointing where it was aimed instead of collapsing to zero degrees.
/// </item>
/// </list>
/// <para>
/// <b>Source and status.</b> Transcribed from OpenGMK's
/// <c>gm8emulator/src/instance.rs</c> (<c>set_hspeed</c>, <c>set_direction</c>,
/// <c>update_hvspeed</c>, <c>update_speed_direction</c>), a faithful GM8
/// reimplementation — the same route that settled <c>collision_line</c>. It is
/// <b>not yet measured against the running game</b>: nothing in phase 4 writes
/// <c>direction</c> or <c>speed</c>, so no existing fixture can see it. Phase
/// 5's first projectile oracle must confirm the snap threshold and the
/// degree/radian conversions before a trajectory is trusted.
/// </para>
/// </remarks>
public struct Motion
{
    /// <summary>
    /// How close a recomputed component must land to an integer before GM8
    /// snaps it there — <c>round_threshold</c> in <c>update_hvspeed</c>.
    /// </summary>
    public const double RoundThreshold = 0.0001;

    private double _hspeed;
    private double _vspeed;
    private double _direction;
    private double _speed;

    /// <summary>GM8's <c>hspeed</c>. Writing it recomputes direction and speed.</summary>
    public double HSpeed
    {
        readonly get => _hspeed;

        // The unchanged case really is a no-op in GM8, not merely a redundant
        // recompute: it is what keeps `hspeed = hspeed` from throwing away a
        // direction that a zero speed can no longer encode.
        set
        {
            if (_hspeed == value)
                return;

            _hspeed = value;
            UpdateSpeedDirection();
        }
    }

    /// <summary>GM8's <c>vspeed</c>. Writing it recomputes direction and speed.</summary>
    public double VSpeed
    {
        readonly get => _vspeed;

        set
        {
            if (_vspeed == value)
                return;

            _vspeed = value;
            UpdateSpeedDirection();
        }
    }

    /// <summary>
    /// GM8's <c>direction</c>, in degrees, always in <c>[0, 360)</c>. Writing it
    /// recomputes both components from the current speed.
    /// </summary>
    public double Direction
    {
        readonly get => _direction;

        set
        {
            _direction = RemEuclid(value, 360);
            UpdateHVSpeed();
        }
    }

    /// <summary>
    /// GM8's <c>speed</c>. Writing it recomputes both components along the
    /// current direction.
    /// </summary>
    /// <remarks>
    /// Not <c>sqrt(hspeed^2 + vspeed^2)</c> in general. It is that after any
    /// component write, but a direct write leaves it holding exactly what was
    /// assigned while the components pick up the snap above — so the two can
    /// disagree in the last bits, and a projectile that reads <c>speed</c> reads
    /// the assigned one.
    /// </remarks>
    public double Speed
    {
        readonly get => _speed;

        set
        {
            _speed = value;
            UpdateHVSpeed();
        }
    }

    /// <summary>Sets both components at once, recomputing direction and speed once.</summary>
    public void SetComponents(double hspeed, double vspeed)
    {
        if (_hspeed == hspeed && _vspeed == vspeed)
            return;

        _hspeed = hspeed;
        _vspeed = vspeed;
        UpdateSpeedDirection();
    }

    /// <summary>Sets speed and direction at once, recomputing the components once.</summary>
    public void SetPolar(double speed, double direction)
    {
        _speed = speed;
        _direction = RemEuclid(direction, 360);
        UpdateHVSpeed();
    }

    /// <summary>
    /// GML's <c>motion_add(direction, speed)</c>: add a vector to the current
    /// motion.
    /// </summary>
    /// <remarks>
    /// Adds to the <em>components</em>, which is why it goes through the
    /// component path and picks up its snap rather than the polar one.
    /// </remarks>
    public void MotionAdd(double direction, double speed)
    {
        var radians = ToRadians(direction);
        SetComponents(_hspeed + (Math.Cos(radians) * speed), _vspeed - (Math.Sin(radians) * speed));
    }

    /// <summary>
    /// <c>update_hvspeed</c>: components from the polar pair, each snapped to a
    /// nearby integer.
    /// </summary>
    private void UpdateHVSpeed()
    {
        var radians = ToRadians(_direction);
        _hspeed = Snap(Math.Cos(radians) * _speed);
        _vspeed = Snap(-Math.Sin(radians) * _speed);
    }

    /// <summary>
    /// <c>update_speed_direction</c>: polar pair from the components. Direction
    /// is snapped the same way; <b>speed is not</b>.
    /// </summary>
    private void UpdateSpeedDirection()
    {
        var degrees = RemEuclid(ToDegrees(Math.Atan2(-_vspeed, _hspeed)), 360);
        _direction = Snap(degrees);

        // sqrt(h*h + v*v), never Math.Hypot: Hypot rescales its arguments and
        // loses the last bit — measured, 636 of 722 samples, see
        // Gg2.Simulation.Aabb.
        _speed = Math.Sqrt((_hspeed * _hspeed) + (_vspeed * _vspeed));
    }

    /// <summary>Nearest integer if it is within <see cref="RoundThreshold"/>, else the value.</summary>
    private static double Snap(double value)
    {
        // GM8's own round, which is half to even — but the tie can never be
        // reached through a 1e-4 window, so the rule is here for provenance
        // rather than for effect.
        var rounded = Gml.Round(value);
        return Math.Abs(rounded - value) < RoundThreshold ? rounded : value;
    }

    /// <summary>Rust's <c>f64::rem_euclid</c>: the remainder, never negative.</summary>
    private static double RemEuclid(double value, double modulus)
    {
        var remainder = value % modulus;
        return remainder < 0 ? remainder + Math.Abs(modulus) : remainder;
    }

    /// <summary>
    /// One multiply by a precomputed constant, matching <c>f64::to_radians</c>
    /// rather than <c>degrees * PI / 180</c> — the two differ in the last bit.
    /// </summary>
    private static double ToRadians(double degrees) => degrees * (Math.PI / 180.0);

    private static double ToDegrees(double radians) => radians * (180.0 / Math.PI);
}
