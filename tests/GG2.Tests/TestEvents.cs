using GG2.Simulation;

namespace GG2.Tests;

/// <summary>
/// A <see cref="TickEvents"/> for a test that does not care what the tick
/// reported.
/// </summary>
/// <remarks>
/// Several simulation entry points take one by <c>ref</c> because the live
/// server and the parity replayer both need what they write. A test exercising
/// one rule of one weapon usually does not, and a local <c>default</c> per call
/// site is several kilobytes of inline-array zeroing for nothing.
/// <para>
/// <c>[ThreadStatic]</c> because xunit runs test collections in parallel, and
/// two of them writing one buffer would be a race — a harmless one, since
/// nothing reads it back, but the kind of thing that is much cheaper to rule out
/// than to debug.
/// </para>
/// </remarks>
internal static class TestEvents
{
    [ThreadStatic]
    private static TickEvents _scratch;

    /// <summary>A scratch buffer whose contents are never examined.</summary>
    public static ref TickEvents Discarded => ref _scratch;
}
