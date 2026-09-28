using Gg2.Protocol;
using Gg2.Server;

namespace Gg2.Tests;

/// <summary>
/// Phase 8's first item: the tick loop can stop sleeping.
/// </summary>
/// <remarks>
/// <para>
/// <c>docs/PLAN.md</c> calls faster-than-real-time "nearly free", and it is —
/// but only because nothing about the simulation is rate-dependent.
/// <c>MatchSimulation.Tick</c> takes no clock and every duration in the game is
/// counted in ticks rather than seconds, so pacing is presentation and removing
/// it changes throughput and nothing else. These tests gate the pacing
/// arithmetic and the one branch that had to be kept away from it.
/// </para>
/// </remarks>
public sealed class ServerPacingTests
{
    private static ServerOptions Options(double speed) =>
        new()
        {
            Port = 0,
            Map = "ctf_truefort",
            MapDirectory = ReferenceTree.MapsDirectory,
            SpeedMultiplier = speed,
        };

    /// <summary>
    /// The default is the reference's own rate, and a server that says nothing
    /// about speed is a server that paces exactly as it did before this landed.
    /// </summary>
    [Fact]
    public void ADefaultServerPacesAtThirtyHertz()
    {
        using var harness = new ServerHarness();

        Assert.Equal(1.0, harness.Options.SpeedMultiplier);
        Assert.Equal(
            TimeSpan.FromSeconds(1.0 / GameConstants.TickRate),
            harness.Server.TickInterval);
    }

    /// <summary>
    /// A multiplier shortens the interval proportionally: four times real time
    /// is a quarter of the wait.
    /// </summary>
    [Fact]
    public void AMultiplierShortensTheIntervalProportionally()
    {
        using var server = new GameServer(Options(4.0), new RecordingLog());
        server.Start();

        Assert.Equal(
            TimeSpan.FromSeconds(1.0 / (GameConstants.TickRate * 4.0)),
            server.TickInterval);
    }

    /// <summary>
    /// Infinity is the unpaced signal, and it reaches the loop as an interval of
    /// exactly zero.
    /// </summary>
    /// <remarks>
    /// Infinity rather than 0 for "as fast as possible" is deliberate. Three
    /// fields in this codebase already use 0 for "no limit" and every one is a
    /// documented trap; here the arithmetic says it instead, because
    /// <c>tickLength / speed</c> really is zero at infinity. A 0 would read as
    /// "stopped", which is a mode the pacer does not have.
    /// </remarks>
    [Fact]
    public void InfinityIsTheUnpacedSignalAndReachesTheLoopAsZero()
    {
        using var server = new GameServer(Options(double.PositiveInfinity), new RecordingLog());
        server.Start();

        Assert.Equal(TimeSpan.Zero, server.TickInterval);
    }

    /// <summary>
    /// An unpaced server ticks, and stops when it is asked to.
    /// </summary>
    /// <remarks>
    /// The wall clock only bounds the test; the assertion is on the frame
    /// counter, which is what the loop actually advances.
    /// </remarks>
    [Fact]
    public void AnUnpacedServerRunsFarFasterThanRealTime()
    {
        var (server, runLines, frames, elapsed) = RunUnpaced(untilFrame: 2000);

        using (server)
        {
            Assert.True(frames >= 2000, $"only reached frame {frames}");

            // 2,000 ticks is 66 seconds of match. Anything close to that means
            // the loop is still sleeping; the margin is enormous on purpose, so
            // this cannot fail on a loaded CI machine.
            Assert.True(
                elapsed < TimeSpan.FromSeconds(20),
                $"2,000 unpaced ticks took {elapsed.TotalSeconds:0.0}s, which is 66s of match — "
                    + "the loop is still pacing");

            Assert.DoesNotContain(runLines, line => line.Contains("backlog", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// The load-bearing one: an unpaced server must not report tick backlog.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The backlog branch sheds tick debt when <c>wait &lt; -tickLength * 10</c>.
    /// At a zero <c>tickLength</c> that reads <c>wait &lt; 0</c>, which is true
    /// on every tick after the first — so an unpaced loop sharing the paced
    /// one's body would log "dropped 0ms of tick backlog" forever, once per
    /// tick, at unpaced speed. <b>There is no debt to shed when there is no
    /// clock to be behind</b>, which is why <c>Run</c> branches to a separate
    /// loop rather than running the same one with a zero interval.
    /// </para>
    /// <para>
    /// The mutation this catches: delete the early-return loop in
    /// <see cref="GameServer.Run"/> and let the paced body run at
    /// <see cref="TimeSpan.Zero"/>.
    /// </para>
    /// </remarks>
    [Fact]
    public void AnUnpacedServerLogsNothingAtAll()
    {
        var (server, runLines, _, _) = RunUnpaced(untilFrame: 2000);

        using (server)
        {
            // Not merely "no backlog line" — an unpaced server nobody is
            // connected to has nothing whatever to say once it is running, so
            // any output at all is a regression worth looking at.
            Assert.True(
                runLines.Count == 0,
                "an unpaced run loop logged: " + string.Join(" | ", runLines.Take(5)));
        }
    }

    /// <summary>
    /// Zero, negative and NaN are refused at startup rather than deep inside the
    /// loop.
    /// </summary>
    /// <remarks>
    /// Each of the three produces a different flavour of wrong wait —
    /// infinite, negative and NaN — and <c>WaitHandle.WaitOne</c> throws on the
    /// last two from inside a running server rather than at configuration time.
    /// Zero is refused because "stopped" is not a mode the pacer has; freezing
    /// belongs to the control port.
    /// </remarks>
    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.NegativeInfinity)]
    public void AnImpossibleSpeedIsRefusedAtStartup(double speed)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Options(speed).Validate());
    }

    /// <summary>
    /// Runs an unpaced server on its own thread until it passes
    /// <paramref name="untilFrame"/>, then cancels and joins.
    /// </summary>
    /// <remarks>
    /// The join is what makes reading the log safe. <see cref="RecordingLog"/>
    /// is a plain list and the server writes to it from the run thread; joining
    /// first is the memory barrier, and reading while it ran would be a race
    /// that fails once a fortnight.
    /// </remarks>
    private static (GameServer Server, IReadOnlyList<string> RunLines, long Frames, TimeSpan Elapsed)
        RunUnpaced(long untilFrame)
    {
        var log = new RecordingLog();
        var server = new GameServer(Options(double.PositiveInfinity), log);
        server.Start();

        // Start() announces the listener and the map it loaded. That is not the
        // run loop talking, so it is not what these tests are about.
        var beforeRun = log.Lines.Count;

        using var cancellation = new CancellationTokenSource();

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var thread = new Thread(() => server.Run(cancellation.Token)) { IsBackground = true };
        thread.Start();

        var deadline = TimeSpan.FromSeconds(30);
        while (server.Frame < untilFrame && clock.Elapsed < deadline)
        {
            Thread.Sleep(1);
        }

        var elapsed = clock.Elapsed;
        var frames = server.Frame;

        cancellation.Cancel();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "the run loop did not stop when cancelled");

        return (server, log.Lines.Skip(beforeRun).ToArray(), frames, elapsed);
    }
}
