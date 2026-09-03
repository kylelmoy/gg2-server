using Gg2.Server;

namespace Gg2.Tests;

/// <summary>
/// <see cref="LateTickMonitor"/>: warn on an overrunning tick, without flooding.
/// </summary>
/// <remarks>
/// ⚠️ <b>No clock and no sleeping anywhere in this file.</b> The monitor is
/// handed lateness that has already been measured and counts its window in
/// ticks, which is what makes every assertion here exact rather than a
/// tolerance. A test that slept to produce a late tick would be measuring the
/// build agent.
/// </remarks>
public sealed class LateTickMonitorTests
{
    private const double Interval = 1000.0 / 30.0;

    private static LateTickContext Context => ContextWithWork(0.2);

    /// <summary>
    /// A context whose tick did <paramref name="workMs"/> of work.
    /// </summary>
    /// <remarks>
    /// The default is deliberately a fraction of a millisecond, because that is
    /// the case the warning has to get right: a tick that did almost nothing and
    /// was still late is the machine's fault, not the server's.
    /// </remarks>
    private static LateTickContext ContextWithWork(double workMs) =>
        new(
            Clients: 2,
            Bots: 4,
            Characters: 6,
            Map: "ctf_truefort",
            WorkMilliseconds: workMs,
            ArbiterSpentMicroseconds: null);

    // ------------------------------------------------------------------
    // Whose fault the late tick was
    // ------------------------------------------------------------------

    /// <summary>
    /// A late tick that did almost no work blames the machine, not the server.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>The line has to answer this or it is not actionable.</b> Measured
    /// 2026-09-03: a twelve-bot server used <b>0.104 ms of CPU per tick</b>, 0.3%
    /// of one core, and still showed p99 100 ms and a 262 ms worst case — because
    /// a neighbouring lane was running seventy training processes on the same
    /// machine. Without the work figure on the line, that reads exactly like a bot
    /// layer that has become too expensive, and the response to the two is
    /// opposite.
    /// </remarks>
    [Fact]
    public void ALateTickThatDidNoWorkBlamesTheMachine()
    {
        var monitor = new LateTickMonitor();

        var line = monitor.Record(tick: 1, overrunMs: 60, intervalMs: Interval, ContextWithWork(0.10));

        Assert.NotNull(line);
        Assert.Contains("work 0.10ms", line);
        Assert.Contains("NOT this tick", line);
    }

    /// <summary>
    /// A late tick whose own work blew the budget blames the tick.
    /// </summary>
    /// <remarks>
    /// The other side, and it must exist: a verdict that always said "the machine"
    /// would be worse than no verdict, because it would talk an operator out of
    /// looking at a server that really had got slow.
    /// </remarks>
    [Fact]
    public void ALateTickThatOverranOnItsOwnBlamesTheTick()
    {
        var monitor = new LateTickMonitor();

        // Work well past the interval: this tick could not have met its deadline
        // on an idle machine either.
        var line = monitor.Record(tick: 1, overrunMs: 60, intervalMs: Interval, ContextWithWork(90));

        Assert.NotNull(line);
        Assert.Contains("work 90.00ms", line);
        Assert.Contains("the tick itself", line);
        Assert.DoesNotContain("NOT this tick", line);
    }

    /// <summary>
    /// The verdict uses the same threshold lateness itself does.
    /// </summary>
    /// <remarks>
    /// ⚠️ Otherwise the two can disagree — a tick could be reported late while the
    /// verdict said its work was fine, at a work figure that would itself have
    /// been reported late. One constant, both answers.
    /// </remarks>
    [Fact]
    public void TheVerdictAndLatenessShareAThreshold()
    {
        var monitor = new LateTickMonitor();

        // Just past 1.2x the interval, which is exactly where IsLate flips.
        var justOver = Interval * (1 + LateTickMonitor.LateFraction) + 0.5;
        var justUnder = Interval * (1 + LateTickMonitor.LateFraction) - 0.5;

        Assert.Contains(
            "the tick itself",
            monitor.Record(1, overrunMs: 60, intervalMs: Interval, ContextWithWork(justOver))!);

        Assert.Contains(
            "NOT this tick",
            new LateTickMonitor()
                .Record(1, overrunMs: 60, intervalMs: Interval, ContextWithWork(justUnder))!);
    }

    // ------------------------------------------------------------------
    // What counts as late
    // ------------------------------------------------------------------

    /// <summary>
    /// The threshold is 20% of the interval — 40 ms at the default 30 Hz.
    /// </summary>
    /// <remarks>
    /// ⚠️ The same 40 ms <c>TickPacing</c> counts, and for the same measured
    /// reason: one and a quarter ticks is where the stock client has
    /// dead-reckoned far enough that the correction is visible.
    /// </remarks>
    [Fact]
    public void TheThresholdIsAFifthOfTheIntervalWhichIsFortyMsAtThirtyHertz()
    {
        Assert.False(LateTickMonitor.IsLate(6.0, Interval));   // 39.3ms total
        Assert.True(LateTickMonitor.IsLate(7.0, Interval));    // 40.3ms total

        // Exactly on the line is not late — a strict comparison, so a tick that
        // hits the budget precisely never warns.
        Assert.False(LateTickMonitor.IsLate(Interval * LateTickMonitor.LateFraction, Interval));
    }

    /// <summary>
    /// ⛔ An unpaced server can never be late.
    /// </summary>
    /// <remarks>
    /// <c>--speed max</c> never waits, so every tick overruns by construction.
    /// A monitor that did not special-case this would warn continuously on
    /// exactly the configuration that wants no output — and <c>Run</c>'s own
    /// early <c>continue</c> is the other half of the guard, so this must hold
    /// independently of the loop's ordering.
    /// </remarks>
    [Fact]
    public void AnUnpacedServerIsNeverLate()
    {
        Assert.False(LateTickMonitor.IsLate(1_000_000, 0));
        Assert.False(LateTickMonitor.IsLate(1.0, 0));
    }

    /// <summary>
    /// ⚠️ A faster <c>--speed</c> lowers the bar with the interval.
    /// </summary>
    /// <remarks>
    /// A fixed 40 ms would mean a <c>--speed 4</c> server (8.3 ms interval)
    /// never warned however far behind it fell — and that is the configuration
    /// most likely to be asked for more than the machine can deliver.
    /// </remarks>
    [Fact]
    public void AShorterIntervalLowersTheThreshold()
    {
        var quarter = Interval / 4;

        // 5ms over is nothing at 30Hz and is well past the budget at 120Hz.
        Assert.False(LateTickMonitor.IsLate(5.0, Interval));
        Assert.True(LateTickMonitor.IsLate(5.0, quarter));
    }

    // ------------------------------------------------------------------
    // The rate limit
    // ------------------------------------------------------------------

    /// <summary>The first late tick in a window prints immediately.</summary>
    /// <remarks>
    /// ⚠️ Immediately, not at the end of the window: an operator watching a
    /// server hitch needs to see it while they are still watching, and a
    /// summary-only design would delay every first warning by up to 30 seconds.
    /// </remarks>
    [Fact]
    public void TheFirstLateTickWarnsImmediatelyAndNamesTheDamage()
    {
        var monitor = new LateTickMonitor(windowTicks: 90);

        var warning = monitor.Record(tick: 10, overrunMs: 12.5, Interval, Context);

        Assert.NotNull(warning);
        Assert.Contains("late tick 10", warning, StringComparison.Ordinal);
        Assert.Contains("12.5ms over", warning, StringComparison.Ordinal);

        // The diagnostic half: what the server was doing.
        Assert.Contains("ctf_truefort", warning, StringComparison.Ordinal);
        Assert.Contains("2 client(s)", warning, StringComparison.Ordinal);
        Assert.Contains("4 bot(s)", warning, StringComparison.Ordinal);
        Assert.Contains("6 character(s)", warning, StringComparison.Ordinal);
    }

    /// <summary>
    /// ⛔ The flood control: a thousand consecutive late ticks produce THREE
    /// lines, not a thousand.
    /// </summary>
    /// <remarks>
    /// This is the assertion the class exists for. A server 2 ms past its budget
    /// on every tick would otherwise print thirty lines a second, which is not a
    /// warning but a denial of service on the operator's terminal.
    /// <para>
    /// ⚠️ <b>Three and not two, because 1,000 ticks spans two 900-tick
    /// windows</b>: the immediate warning, the summary when the first window
    /// closes, and the immediate warning that opens the second. That is the
    /// intended shape — the limit is a RATE, not a mute, and a monitor that went
    /// quiet for the life of the server would read as "the hitch stopped".
    /// </para>
    /// </remarks>
    [Fact]
    public void AThousandLateTicksProduceThreeLinesNotAThousand()
    {
        var monitor = new LateTickMonitor(windowTicks: 900);
        var lines = new List<string>();

        for (var tick = 0; tick < 1000; tick++)
        {
            if (monitor.Poll(tick) is { } summary)
                lines.Add(summary);

            if (monitor.Record(tick, overrunMs: 10, Interval, Context) is { } warning)
                lines.Add(warning);
        }

        Assert.Equal(3, lines.Count);
        Assert.Contains("late tick 0", lines[0], StringComparison.Ordinal);
        Assert.Contains("more late tick(s)", lines[1], StringComparison.Ordinal);
        Assert.Contains("late tick 900", lines[2], StringComparison.Ordinal);

        // ⚠️ Every late tick is still COUNTED, even when it is not printed —
        // "one late tick" and "a thousand" must not be indistinguishable.
        Assert.Equal(1000, monitor.LateTicks);
    }

    /// <summary>The summary names the WORST tick in the window, not the last.</summary>
    /// <remarks>
    /// ⚠️ The worst is the one worth chasing; the last is an accident of where
    /// the window boundary fell. A test that only checked "a summary appeared"
    /// would pass with either.
    /// </remarks>
    [Fact]
    public void TheSummaryNamesTheWorstTickNotTheLast()
    {
        var monitor = new LateTickMonitor(windowTicks: 10);

        monitor.Record(tick: 0, overrunMs: 8, Interval, Context);
        monitor.Record(tick: 1, overrunMs: 40, Interval, Context);   // the worst
        monitor.Record(tick: 2, overrunMs: 9, Interval, Context);    // the last

        var summary = monitor.Poll(10);

        Assert.NotNull(summary);
        Assert.Contains("tick 1", summary, StringComparison.Ordinal);
        Assert.Contains("40.0ms over", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("tick 2", summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// A single late tick in a window produces no summary — one line, not two.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The control on the flood test.</b> Without this, a monitor that
    /// always emitted a summary would pass "a thousand ticks produce two lines"
    /// and still double every isolated hitch.
    /// </remarks>
    [Fact]
    public void AnIsolatedLateTickProducesNoSummary()
    {
        var monitor = new LateTickMonitor(windowTicks: 10);

        Assert.NotNull(monitor.Record(tick: 0, overrunMs: 10, Interval, Context));

        for (var tick = 1; tick <= 20; tick++)
            Assert.Null(monitor.Poll(tick));
    }

    /// <summary>
    /// A new window warns immediately again — the limit is a rate, not a mute.
    /// </summary>
    /// <remarks>
    /// ⛔ The failure this rules out is a monitor that warns once and then never
    /// again for the life of the server, which is strictly worse than no warning
    /// at all: it would read as "the hitch stopped".
    /// </remarks>
    [Fact]
    public void ANewWindowWarnsAgain()
    {
        var monitor = new LateTickMonitor(windowTicks: 10);

        Assert.NotNull(monitor.Record(tick: 0, overrunMs: 10, Interval, Context));
        Assert.Null(monitor.Record(tick: 1, overrunMs: 10, Interval, Context));

        // Close the window.
        Assert.NotNull(monitor.Poll(10));

        Assert.NotNull(monitor.Record(tick: 11, overrunMs: 10, Interval, Context));
    }

    /// <summary>
    /// A server that stops mid-window still reports what it suppressed.
    /// </summary>
    [Fact]
    public void ShutdownFlushesAPendingSummary()
    {
        var monitor = new LateTickMonitor(windowTicks: 900);

        monitor.Record(tick: 0, overrunMs: 10, Interval, Context);
        monitor.Record(tick: 1, overrunMs: 30, Interval, Context);

        var flushed = monitor.Flush();

        Assert.NotNull(flushed);
        Assert.Contains("before shutdown", flushed, StringComparison.Ordinal);
        Assert.Contains("30.0ms over", flushed, StringComparison.Ordinal);

        // Idempotent: a second flush has nothing left to say.
        Assert.Null(monitor.Flush());
    }

    /// <summary>A healthy server prints nothing at all.</summary>
    /// <remarks>
    /// ⚠️ Which is why this is always on rather than behind a flag — there is no
    /// cost to leaving it armed on a server that never hitches.
    /// </remarks>
    [Fact]
    public void AHealthyServerIsSilent()
    {
        var monitor = new LateTickMonitor(windowTicks: 10);

        for (var tick = 0; tick < 100; tick++)
        {
            Assert.False(LateTickMonitor.IsLate(1.0, Interval));
            Assert.Null(monitor.Poll(tick));
        }

        Assert.Null(monitor.Flush());
        Assert.Equal(0, monitor.LateTicks);
    }

    /// <summary>
    /// The arbiter spend appears only when there is an arbiter.
    /// </summary>
    /// <remarks>
    /// ⚠️ Null means "no arbiter", never "spent zero" — a tiered server that
    /// granted nothing this tick is a real and different observation from a
    /// scripted server that has no arbiter to ask.
    /// </remarks>
    [Fact]
    public void TheArbiterSpendIsReportedOnlyWhenThereIsOne()
    {
        var without = new LateTickMonitor(windowTicks: 10)
            .Record(0, 10, Interval, Context);

        Assert.NotNull(without);
        Assert.DoesNotContain("controllers", without, StringComparison.Ordinal);

        var withArbiter = new LateTickMonitor(windowTicks: 10).Record(
            0, 10, Interval, Context with { ArbiterSpentMicroseconds = 9600 });

        Assert.NotNull(withArbiter);
        Assert.Contains("controllers 9600us", withArbiter, StringComparison.Ordinal);

        // Zero is reported, and is not the same as absent.
        var spentNothing = new LateTickMonitor(windowTicks: 10).Record(
            0, 10, Interval, Context with { ArbiterSpentMicroseconds = 0 });

        Assert.NotNull(spentNothing);
        Assert.Contains("controllers 0us", spentNothing, StringComparison.Ordinal);
    }
}
