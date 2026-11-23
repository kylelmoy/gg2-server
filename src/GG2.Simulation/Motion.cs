using GG2.Protocol;

namespace GG2.Simulation;

/// <summary>
/// Game Maker 8's built-in motion variables — <c>hspeed</c>, <c>vspeed</c>,
/// <c>direction</c> and <c>speed</c> — and the way writing one rewrites the
/// others.
/// </summary>
/// <remarks>
/// <b>All four are stored, and none is derived on read.</b>
/// A shot is created with <c>direction</c> and <c>speed</c>, then has
/// <c>hspeed</c> added to it, then <c>speed</c> and <c>direction</c> adjusted
/// again (<c>Objects/Weapons/Scattergun.events/User Event 3.xml</c>), so it
/// round-trips between the two representations three times before it moves.
/// <para>
/// Three details of that round trip change results rather than last digits:
/// </para>
/// <list type="bullet">
/// <item>
/// A <b>recomputed</b> value snaps to a whole number when it lands within 1e-4
/// of one — see <see cref="Gml.SnapToNearInteger"/>. A shot fired at exactly 90
/// degrees gets <c>hspeed = 0</c>, not <c>7.9e-16</c>. Note the size of it:
/// <see cref="Gml.Epsilon"/> is 1e-13, a billion times tighter, and the two do
/// completely different jobs.
/// </item>
/// <item>
/// <b>Both</b> <c>direction</c> and <c>speed</c> are snapped when the components
/// are written, and both components are snapped when the polar pair is. A value
/// written <em>directly</em> into any of the four is stored as given.
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
/// <c>update_hvspeed</c>, <c>update_speed_direction</c>) and measured against
/// the running game — 399 write sequences and 27 angles, regenerate with
/// a live-game sampler.
/// </para>
/// <para>
/// <b>This is the one primitive in the project that is not bit-exact, and it
/// cannot be made so.</b> GM8 is a Delphi program and its motion conversions run
/// on the x87 stack at 80-bit extended precision with Intel's <c>FSIN</c> and
/// <c>FCOS</c>; the port runs on IEEE doubles. Eight spellings of
/// degrees-to-radians were measured against 486 components and none reproduced
/// the engine — the best of them, the one used here, agrees on 87% of sampled
/// values and is within <b>1.5e-15 per unit of speed</b> on the rest. That is an
/// order of magnitude inside GML's own comparison fuzz, a projectile accumulates
/// under 1e-12 of a pixel of it over its whole 40-tick life, and positions reach
/// the wire quantised to a fifth of a pixel — so nothing in the reference can
/// observe it. The discrete part, the snap, <em>is</em> exact.
/// docs/SIMULATION.md has the measurements.
/// </para>
/// </remarks>
public struct Motion
{
    /// <summary>
    /// How close a recomputed value must land to a whole number before GM8
    /// snaps it there.
    /// </summary>
    /// <remarks>
    /// The rule itself lives in <see cref="Gml.SnapToNearInteger"/>, because
    /// <c>lengthdir_x</c> and <c>lengthdir_y</c> apply the same one and the
    /// Scattergun uses both in the same event. Measured at exactly 1e-4, strict
    /// — see tests/GG2.Tests/MotionSampleTests.cs.
    /// </remarks>
    public const double RoundThreshold = Gml.MotionSnapThreshold;

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
        _hspeed = Gml.SnapToNearInteger(Math.Cos(radians) * _speed);
        _vspeed = Gml.SnapToNearInteger(-Math.Sin(radians) * _speed);
    }

    /// <summary>
    /// <c>update_speed_direction</c>: polar pair from the components. <b>Both</b>
    /// are snapped.
    /// </summary>
    /// <remarks>
    /// Not a last-bit correction: writing <c>hspeed = 3.00005</c> with
    /// <c>vspeed</c> at zero leaves <c>hspeed</c> alone and reports
    /// <c>speed</c> as exactly <b>3</b>, and two components of 1e-15 report a
    /// speed of exactly <b>0</b>. Measured — see
    /// tests/GG2.Tests/MotionSampleTests.cs.
    /// </remarks>
    private void UpdateSpeedDirection()
    {
        var degrees = RemEuclid(ToDegrees(Math.Atan2(-_vspeed, _hspeed)), 360);
        _direction = Gml.SnapToNearInteger(degrees);

        // sqrt(h*h + v*v), never Math.Hypot: Hypot rescales its arguments and
        // loses the last bit — measured, 636 of 722 samples, see
        // GG2.Simulation.Aabb.
        _speed = Gml.SnapToNearInteger(Math.Sqrt((_hspeed * _hspeed) + (_vspeed * _vspeed)));
    }

    /// <summary>Rust's <c>f64::rem_euclid</c>: the remainder, never negative.</summary>
    private static double RemEuclid(double value, double modulus)
    {
        var remainder = value % modulus;
        return remainder < 0 ? remainder + Math.Abs(modulus) : remainder;
    }

    /// <summary>
    /// One multiply by a precomputed constant. Shared with the GML built-in of
    /// the same name, which is what <c>lengthdir</c> uses.
    /// </summary>
    private static double ToRadians(double degrees) => Gml.DegToRad(degrees);

    private static double ToDegrees(double radians) => Gml.RadToDeg(radians);
}
