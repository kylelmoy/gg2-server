using System.Diagnostics;

namespace Gg2.Protocol;

/// <summary>
/// How a GML <c>real</c> becomes an integer on the wire.
/// </summary>
/// <remarks>
/// GML has one numeric type — an IEEE-754 double — so <c>write_ushort(b, x*5)</c>
/// hands the networking extension a fractional value and the extension decides
/// what byte pattern that is. Two rules, both read from the Faucet Networking
/// source (<c>faucet/ReadWritable.hpp</c>, <c>writeIntValue</c>, and
/// <c>faucet/clipped_cast.hpp</c>):
/// <code>
/// IntType converted = clipped_cast&lt;IntType&gt;(round(value));
/// </code>
/// <list type="number">
/// <item><b>Round half away from zero.</b> That is C++ <c>std::round</c>, not
/// banker's rounding — so 2.5 becomes 3 and -2.5 becomes -3. .NET's
/// <c>Math.Round</c> defaults to banker's and is the wrong answer here.</item>
/// <item><b>Clamp, do not wrap.</b> <c>clipped_cast</c> saturates at the target
/// type's bounds, so an out-of-range value pins to the edge instead of wrapping
/// round to a small one.</item>
/// </list>
/// <para>
/// <b>This is not the same as GML's own <c>round()</c>,</b> which is
/// round-half-to-even, and the reference applies that one to some fields and
/// not others — <c>Intelligence</c>'s position is written as
/// <c>round(x*5)</c> while <c>Character</c>'s is written as plain <c>x*5</c>.
/// Two objects, two quantisations, one wire. Use
/// <see cref="Gml.Round"/> first only where the reference does.
/// </para>
/// <para>
/// Repository: <see href="https://github.com/Medo42/Faucet-Networking-Extension"/>.
/// </para>
/// </remarks>
public static class WireQuantise
{
    public static byte ToUByte(double value) => (byte)Clip(value, byte.MinValue, byte.MaxValue);

    public static sbyte ToByte(double value) => (sbyte)Clip(value, sbyte.MinValue, sbyte.MaxValue);

    public static ushort ToUShort(double value) => (ushort)Clip(value, ushort.MinValue, ushort.MaxValue);

    public static short ToShort(double value) => (short)Clip(value, short.MinValue, short.MaxValue);

    public static uint ToUInt(double value) => (uint)Clip(value, uint.MinValue, uint.MaxValue);

    public static int ToInt(double value) => (int)Clip(value, int.MinValue, int.MaxValue);

    /// <summary>
    /// <c>clipped_cast&lt;T&gt;(round(value))</c>, with the bounds passed in
    /// because <c>clipped_cast</c> compares against them as doubles.
    /// </summary>
    /// <remarks>
    /// The comparisons are <c>&lt;=</c> lowest and <c>&gt;=</c> max, which is
    /// why a value exactly on the boundary still takes the clamped branch. That
    /// is only observable for the unsigned 32-bit case, where
    /// <c>uint.MaxValue</c> is not exactly representable as the double the
    /// comparison uses — and the answer is the same either way.
    /// </remarks>
    private static double Clip(double value, double lowest, double highest)
    {
        if (double.IsNaN(value))
        {
            // static_cast<int>(NaN) is undefined behaviour in C++, so the
            // reference has no defined answer here either. A NaN reaching the
            // wire means the physics already went wrong; fail loudly in debug
            // and pick something deterministic in release rather than inventing
            // a "compatible" bit pattern.
            Debug.Fail("NaN reached wire quantisation");
            return 0;
        }

        // std::round: half away from zero.
        var rounded = Math.Round(value, MidpointRounding.AwayFromZero);

        if (rounded <= lowest)
            return lowest;

        return rounded >= highest ? highest : rounded;
    }
}

/// <summary>
/// GML's own numeric functions, where they differ from the C# ones that look
/// like them.
/// </summary>
/// <remarks>
/// Collected here so a call site can say which language's rounding it means.
/// Getting these confused produces a one-unit difference that looks exactly
/// like ordinary GG2 netcode jitter (docs/SIMULATION.md, "Float rules").
/// </remarks>
public static class Gml
{
    /// <summary>
    /// GML <c>round()</c>: round half to <b>even</b>, which .NET's
    /// <c>Math.Round</c> also does by default. <c>round(0.5) == 0</c>,
    /// <c>round(1.5) == 2</c>, <c>round(2.5) == 2</c>.
    /// </summary>
    /// <remarks>
    /// So <c>round(x)</c> is <b>not</b> <c>floor(x + 0.5)</c>, and it is not
    /// what the networking extension does on its way to the wire either.
    /// </remarks>
    public static double Round(double value) => Math.Round(value, MidpointRounding.ToEven);

    /// <summary>GML <c>floor()</c>, which matches <c>Math.Floor</c> on negatives too.</summary>
    public static double Floor(double value) => Math.Floor(value);

    public static double Ceil(double value) => Math.Ceiling(value);

    // -- Decimal literals -----------------------------------------------------
    //
    // Game Maker 8 does not parse every decimal literal to the IEEE-754 double
    // nearest it. For some values it lands one ULP high, and the physics
    // constants are full of them. Measured against the running game by pulling
    // the mantissa out a chunk at a time (docs/SIMULATION.md, "GM8's decimal
    // literals"):
    //
    //     GML text   GM8 holds             C# "0.6" holds
    //     0.6        0.6000000000000001    0.6                (one ULP lower)
    //     0.85       0.8500000000000001    0.85
    //     1.15       1.1500000000000001    1.15
    //     0.65       0.6500000000000001    0.65
    //     0.35       0.35000000000000003   0.35
    //
    // while 0.1, 0.2, 0.45, 0.54, 0.9, 1.05, 1.4 and 8.3 come out the same in
    // both. Writing the C# decimal that rounds to GM8's double is the whole fix:
    // each constant below parses to exactly the bits the game has.
    //
    // This is not pedantry. `baseFriction - 1` subtracts almost all of the
    // value away, so one ULP in 1.15 becomes fourteen in `basemaxspeed`; and a
    // landing crawl backs off in eighths until a position is free, so an
    // arbitrarily small difference either side of a boundary moves a character
    // by a sixteenth of a pixel and keeps it there.

    /// <summary>The double GM8 holds for the literal <c>0.6</c>.</summary>
    public const double PointSix = 0.6000000000000001;

    /// <summary>The double GM8 holds for the literal <c>0.85</c>.</summary>
    public const double PointEightFive = 0.8500000000000001;

    /// <summary>The double GM8 holds for the literal <c>1.15</c>.</summary>
    public const double OnePointOneFive = 1.1500000000000001;

    /// <summary>The double GM8 holds for the literal <c>0.65</c>.</summary>
    public const double PointSixFive = 0.6500000000000001;

    /// <summary>The double GM8 holds for the literal <c>0.35</c>.</summary>
    public const double PointThreeFive = 0.35000000000000003;

    // -- Comparisons ----------------------------------------------------------
    //
    // GML's comparison operators are FUZZY. Two reals compare equal when they
    // are within 1e-13 of each other, and every other operator is built on that
    // — so `a > b` is false for a difference smaller than the epsilon, and
    // `sign(x)` is 0 for any x below it. Measured against the running game:
    // 1e-13 is non-zero, 1e-14 is zero, and 1e6 differs from 1e6 + 1e-10, so the
    // epsilon is absolute rather than relative.
    //
    // C# has none of this, and the difference is not academic. A crawl that
    // consumes all of its allowance leaves `hleft` at about -2.7e-14 through
    // ordinary cancellation; GML calls that zero and skips the wall check, while
    // a literal `!= 0` takes it and slides the character six pixels down a
    // sloping ceiling. That is one tick of input turning into a visible
    // teleport, from a difference of 3e-14.
    //
    // Use these wherever the reference compares two reals. Integers, and
    // anything already rounded to a collision box, do not need them.

    /// <summary>How close two reals have to be before GML calls them equal.</summary>
    public const double Epsilon = 1e-13;

    /// <summary>GML <c>a == b</c>.</summary>
    public static bool Equal(double a, double b) => Math.Abs(a - b) < Epsilon;

    /// <summary>GML <c>a != b</c>.</summary>
    public static bool NotEqual(double a, double b) => !Equal(a, b);

    /// <summary>GML <c>a &gt; b</c>: greater, and by more than the epsilon.</summary>
    public static bool Greater(double a, double b) => a > b && !Equal(a, b);

    /// <summary>GML <c>a &lt; b</c>.</summary>
    public static bool Less(double a, double b) => a < b && !Equal(a, b);

    /// <summary>GML <c>a &gt;= b</c>: greater, or equal within the epsilon.</summary>
    public static bool GreaterOrEqual(double a, double b) => a > b || Equal(a, b);

    /// <summary>GML <c>a &lt;= b</c>.</summary>
    public static bool LessOrEqual(double a, double b) => a < b || Equal(a, b);

    /// <summary>
    /// GML <c>sign()</c>, which is <b>0</b> for anything inside the epsilon.
    /// </summary>
    /// <remarks>
    /// <c>Math.Sign</c> is only 0 for a true zero, and the movement solver feeds
    /// <c>sign()</c> straight into a <c>place_free</c> probe offset — so the
    /// difference is whether the character checks the cell beside it or the one
    /// it is standing in.
    /// </remarks>
    public static int Sign(double value) => Equal(value, 0) ? 0 : Math.Sign(value);
}
