using GG2.Simulation;

namespace GG2.Tests;

/// <summary>
/// A <see cref="TickEvents"/> for tests that call a simulation entry point
/// directly and do not care what it announced.
/// </summary>
/// <remarks>
/// <b>The parameter is required rather than optional on purpose</b>, so that
/// a new damage site cannot quietly accumulate nothing — see
/// <see cref="DamageApi"/>. That decision costs the unit tests an argument they
/// have no use for, and this is the cheapest honest way to pay it: a convenience
/// overload in the simulation would put the hole straight back, because
/// production code could reach it too.
/// <para>
/// <c>[ThreadStatic]</c> because xunit runs collections in parallel. Nothing
/// reads the value, so races would not corrupt an assertion — but a shared
/// mutable buffer that is written from several threads is undefined behaviour
/// rather than merely useless, and one per thread costs nothing.
/// </para>
/// <para>
/// <b>Do not use this in a test that is about what a tick announced.</b>
/// Those declare their own and assert on it; this one is deliberately never
/// cleared, so its contents are meaningless.
/// </para>
/// </remarks>
internal static class DiscardedEvents
{
    [ThreadStatic]
    private static TickEvents _events;

    internal static ref TickEvents Events => ref _events;
}
