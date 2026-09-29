using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace GG2.Server;

/// <summary>
/// Raises Windows' timer resolution for as long as the server is running, so a
/// paced tick lands when it is asked to.
/// </summary>
/// <remarks>
/// <b>Measured, on an idle server, before this existed.</b> The tick loop paces
/// with <c>WaitHandle.WaitOne</c>, and Windows rounds a wait up to the current
/// timer quantum — about 15.6ms by default. Against a 33.333ms tick that gives a
/// <b>bimodal</b> distribution with nothing between the modes:
/// <code>
/// n=870  min=28.68  p50=31.04  p99=46.98  max=47.54
/// 25-29ms:1   30-34ms:736   45-49ms:133
/// </code>
/// So <b>15% of ticks arrived about 14ms late</b> — nearly half a tick — and the
/// mean hid it completely, because the loop catches up on the following ticks.
/// <para>
/// <b>Why that reaches a player.</b> The stock client re-simulates the physics
/// between position updates and corrects when one arrives (docs/PARITY.md). A
/// server whose ticks arrive irregularly makes those corrections irregular, and
/// the client shows it as rubberbanding.
/// </para>
/// <para>
/// <b>This changes pacing only — never simulation.</b> A tick computes the
/// same thing whenever it runs; <c>docs/SIMULATION.md</c>'s determinism contract
/// is about what a tick does, not when. It is safe by CLAUDE.md's rule
/// ("compatible on the wire and in observable simulation, free everywhere
/// else") precisely because the wire content does not depend on it.
/// </para>
/// <para>
/// <b>The obvious alternative does not work</b>, and was measured rather than
/// assumed: a <c>Thread.Sleep(1)</c> plus spin loop is bimodal in the same way,
/// because <c>Sleep</c> is rounded by the same quantum. Nothing short of
/// changing the quantum — or burning a core on a pure spin — moves it.
/// </para>
/// <para>
/// <b>Non-Windows is a no-op.</b> Linux's nanosleep already has the resolution
/// this is asking Windows for.
/// </para>
/// </remarks>
public static class TimerResolution
{
    /// <summary>
    /// One millisecond: the finest resolution <c>timeBeginPeriod</c> is
    /// documented to accept, and enough for a 33ms tick.
    /// </summary>
    private const uint Period = 1;

    [SupportedOSPlatform("windows")]
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint BeginPeriod(uint period);

    [SupportedOSPlatform("windows")]
    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint EndPeriod(uint period);

    /// <summary>
    /// Raises the resolution until the returned handle is disposed.
    /// </summary>
    /// <remarks>
    /// <b>Every <c>timeBeginPeriod</c> needs its <c>timeEndPeriod</c></b> —
    /// the setting is reference-counted per process and outlives a process that
    /// leaks it only until that process dies, but leaking it inside a long-lived
    /// server keeps the whole machine on a 1ms quantum. Hence the handle rather
    /// than a bare call.
    /// </remarks>
    public static IDisposable Raise() =>
        OperatingSystem.IsWindows() && BeginPeriod(Period) == 0
            ? new Window()
            : new Nothing();

    private sealed class Window : IDisposable
    {
        private bool _ended;

        public void Dispose()
        {
            if (_ended || !OperatingSystem.IsWindows())
                return;

            _ended = true;
            EndPeriod(Period);
        }
    }

    private sealed class Nothing : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
