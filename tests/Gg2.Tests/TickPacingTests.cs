using Gg2.Server;

namespace Gg2.Tests;

/// <summary>
/// <see cref="TickPacing"/> — the histogram that answers "the server hitches".
/// </summary>
/// <remarks>
/// ⚠️ <b>The test worth having here is the one that shows the mean and the
/// median lying.</b> The real distribution that provoked
/// <see cref="TimerResolution"/> had a median <em>below</em> its own target
/// while 15% of its ticks were half a tick late, so a report that led with an
/// average would have said the server was early. These tests replay that shape
/// and check the tail statistics see it.
/// </remarks>
public class TickPacingTests
{
    /// <summary>
    /// ✅ <b>The measured pre-fix distribution, and what each statistic says
    /// about it.</b>
    /// </summary>
    /// <remarks>
    /// 736 ticks at 30–34ms and 133 at 45–49ms, from an idle server pacing with
    /// <c>WaitHandle.WaitOne</c> before the timer quantum was raised
    /// (<c>docs/TRAPS-SIMULATION.md</c>). <b>The mean is 33.4ms — a bullseye on
    /// the 33.33ms target — and the median is 31ms, which is early.</b> Only the
    /// tail is honest about it.
    /// </remarks>
    [Fact]
    public void TheMeanAndTheMedianBothMissAHitchTheTailCatches()
    {
        var pacing = new TickPacing(33.333);

        for (var i = 0; i < 736; i++)
            pacing.Record(31.0);

        for (var i = 0; i < 133; i++)
            pacing.Record(47.0);

        var report = pacing.Report();

        // The two that would have cleared this server of any wrongdoing.
        Assert.Contains("p50 31", report);
        Assert.Contains("mean 33.45", report);

        // The three that convict it.
        Assert.Contains("p99 47ms", report);
        Assert.Equal(133, pacing.Over(40));
        Assert.Contains("133 over 40ms", report);

        // And the shape, because a bimodal distribution and a merely noisy one
        // have the same percentiles and entirely different causes.
        Assert.Contains("31:736 47:133", report);
    }

    /// <summary>The post-fix distribution, for contrast: nothing in the tail.</summary>
    [Fact]
    public void AHealthyDistributionHasAnEmptyTail()
    {
        var pacing = new TickPacing(33.333);

        for (var i = 0; i < 868; i++)
            pacing.Record(33.3);

        pacing.Record(36.1);
        pacing.Record(28.9);

        Assert.Equal(0, pacing.Over(40));
        Assert.Contains("0 over 40ms", pacing.Report());
        Assert.Equal(870, pacing.Count);
    }

    /// <summary>
    /// A tick slower than the histogram's last bucket still lands in it, and is
    /// still counted as late.
    /// </summary>
    /// <remarks>
    /// ⚠️ The overflow bucket matters more than its rarity suggests: a server
    /// that stalled for a second is exactly the event worth seeing, and a
    /// histogram that dropped it — or that indexed off the end — would lose the
    /// one tick anybody cares about.
    /// </remarks>
    [Fact]
    public void AStallLandsInTheOverflowBucketRatherThanFallingOffTheEnd()
    {
        var pacing = new TickPacing(33.333);

        pacing.Record(33.3);
        pacing.Record(4000.0);

        Assert.Equal(1, pacing.Over(40));
        Assert.Contains("100+:1", pacing.Report());
        Assert.Contains("max 4000.00ms", pacing.Report());
    }

    /// <summary>A server that never ticked says so rather than dividing by zero.</summary>
    [Fact]
    public void NothingRecordedIsNotAnError()
    {
        Assert.Equal("tick pacing: nothing recorded", new TickPacing(33.333).Report());
        Assert.Equal(0, new TickPacing(33.333).Count);
    }
}
