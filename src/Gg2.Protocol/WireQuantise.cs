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
}
