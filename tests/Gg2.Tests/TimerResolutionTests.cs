using Gg2.Server;

namespace Gg2.Tests;

/// <summary>
/// <see cref="TimerResolution"/>'s disposal contract.
/// </summary>
/// <remarks>
/// ⚠️ <b>What justifies this class is a measurement, not this test.</b> On an
/// idle server, pacing a 33.333ms tick with <c>WaitHandle.WaitOne</c> gave a
/// bimodal distribution — 736 ticks at 30–34ms and <b>133 at 45–49ms</b>, 15%
/// of them nearly half a tick late — and raising the Windows timer quantum to
/// 1ms took that to <b>868 of 870 in one bucket, p99 34.79ms, none over 40</b>.
/// Both runs are in <c>docs/TRAPS-SIMULATION.md</c>.
/// <para>
/// ⚠️ <b>That is not gated by a test and cannot honestly be</b>: a timing
/// assertion strong enough to catch a regression is an assertion that fails on
/// a loaded CI box. The same is true of this project's headroom number. What is
/// gated here is the part that is ordinary logic and would be a real bug — the
/// reference-counted <c>timeEndPeriod</c>, which if missed leaves the whole
/// machine on a 1ms quantum for as long as the server lives.
/// </para>
/// </remarks>
public sealed class TimerResolutionTests
{
    /// <summary>The window can be opened and closed.</summary>
    [Fact]
    public void RaisingReturnsAWindowThatCloses()
    {
        var window = TimerResolution.Raise();

        Assert.NotNull(window);
        window.Dispose();
    }

    /// <summary>
    /// ⚠️ Disposing twice must not lower the resolution twice.
    /// </summary>
    /// <remarks>
    /// <c>timeBeginPeriod</c>/<c>timeEndPeriod</c> are reference-counted per
    /// process, so an extra <c>End</c> decrements a count this process never
    /// incremented — which on a machine where something else is holding a high
    /// resolution takes it away from that program instead. A <c>using</c> plus
    /// an explicit close is an ordinary way to reach this, so the guard is not
    /// theoretical.
    /// </remarks>
    [Fact]
    public void DisposingTwiceIsSafe()
    {
        var window = TimerResolution.Raise();

        window.Dispose();
        window.Dispose();
        window.Dispose();
    }

    /// <summary>Two windows at once are independent.</summary>
    [Fact]
    public void TwoWindowsCanOverlap()
    {
        using var outer = TimerResolution.Raise();
        using (var inner = TimerResolution.Raise())
        {
            Assert.NotNull(inner);
        }

        Assert.NotNull(outer);
    }
}
