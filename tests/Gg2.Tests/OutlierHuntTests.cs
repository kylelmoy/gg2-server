using Gg2.Bots;
using Gg2.Maps;
using Gg2.Parity;
using Gg2.Protocol;
using Gg2.Server;
using Xunit.Abstractions;

namespace Gg2.Tests;

/// <summary>
/// The two tools that caught and localised the outlier in <c>docs/PARITY.md</c>,
/// "the phase gate's own recording has an unexplained outlier".
/// </summary>
/// <remarks>
/// ⛔ <b>Both are SKIPPED, because neither is a gate.</b> <see cref="Hunt"/> runs
/// dozens of full bot matches and keeps the ones that misbehave; <see cref="Analyse"/>
/// grades a kept one. Kept in the suite rather than thrown away because the
/// outlier is not fixed, is rare, and cost a session to catch once — the next
/// person to look at it should not have to write this again.
/// <para>
/// ⚠️ <b>The hunt needs LOAD.</b> Sixty matches on an idle machine caught nothing;
/// the same sixty alongside a full suite run caught eight in four runs. Whatever
/// the outlier is, an idle machine does not produce it, so run this while
/// something else is busy.
/// </para>
/// </remarks>
public sealed class OutlierHuntTests(ITestOutputHelper output)
{
    private const int MaxLag = 2;

    private const string Tool =
        "Tool, not a gate. Remove the Skip to hunt the docs/PARITY.md outlier; needs a busy machine.";

    private static LevelMap MapLoader(string name) =>
        LevelMap.FromPng(name, ReferenceTree.ReadMap(name));

    /// <summary>Records one clock-seeded match and hands back its trace path.</summary>
    private static string Record(string directory)
    {
        using (var harness = new ServerHarness(options =>
        {
            options.Map = "gg_debug";
            options.RecordingDirectory = directory;
            options.NavCacheDirectory = MapDirectory.ResolveNav();
            options.Bots = BotPopulationOptions.Default with
            {
                Enabled = true,
                FillToPlayers = 6,
                MaxBots = 6,
                MinHumans = 0,
            };
        }))
        {
            harness.TickUntil(() => harness.Server.RosterCount > 1, "the bots joined");
            harness.Tick(900);
        }

        return Directory.GetFiles(directory, "*.trace")[0];
    }

    private static long ErrorAt(string path, int lag)
    {
        var trace = Trace.Decode(File.ReadAllBytes(path));
        var map = MapLoader(trace.Header.Map);
        var ticks = TraceParser.Parse(trace, GameModes.ShapeOf(map), out _, out _);

        var report = new Replayer(new PortedSimulation(MapLoader))
        {
            Compare = Fields.Trajectory,
            InputLag = lag,
        }.Replay(Path.GetFileName(path), trace.Header.Map, ticks, ReplayMode.OpenLoop, stopAtFirstBadTick: false);

        return report.PercentileXError(99) + report.PercentileYError(99);
    }

    /// <summary>Where the error lands in one kept outlier trace.</summary>
    /// <remarks>
    /// <c>ANALYSE_TRACE</c> names a trace <see cref="Hunt"/> kept. It fails on
    /// purpose at the end: xunit shows a passing test's output nowhere.
    /// </remarks>
    [Fact(Skip = Tool)]
    public void Analyse()
    {
        var path = Environment.GetEnvironmentVariable("ANALYSE_TRACE")!;

        var trace = Trace.Decode(File.ReadAllBytes(path));
        var map = MapLoader(trace.Header.Map);
        var ticks = TraceParser.Parse(trace, GameModes.ShapeOf(map), out _, out _);

        var report = new Replayer(new PortedSimulation(MapLoader))
        {
            Compare = Fields.Trajectory,
            InputLag = 0,
        }.Replay(Path.GetFileName(path), trace.Header.Map, ticks, ReplayMode.OpenLoop, stopAtFirstBadTick: false);

        output.WriteLine(
            $"{Path.GetFileName(path)}: {report.TicksReplayed} ticks, {report.Comparisons} comparisons, "
            + $"{report.Mismatches.Count} mismatches, p99 x={report.PercentileXError(99)} y={report.PercentileYError(99)}");

        var worst = report.Mismatches
            .OrderByDescending(m => Math.Abs(m.Delta))
            .Take(25)
            .ToList();

        output.WriteLine("worst 25 mismatches:");
        foreach (var m in worst)
            output.WriteLine($"  t={m.Tick,-5} slot={m.Slot} {m.Field,-6} expected={m.Expected,-8} actual={m.Actual,-8} delta={m.Delta}");

        output.WriteLine("mismatches per slot:");
        foreach (var g in report.Mismatches.GroupBy(m => m.Slot).OrderByDescending(g => g.Count()))
            output.WriteLine($"  slot {g.Key}: {g.Count()}  worst |delta| {g.Max(m => Math.Abs(m.Delta))}");

        output.WriteLine("tick clusters (mismatches with |delta| > 40):");
        foreach (var g in report.Mismatches.Where(m => Math.Abs(m.Delta) > 40)
                     .GroupBy(m => m.Tick / 25).OrderBy(g => g.Key))
        {
            output.WriteLine($"  ticks {g.Key * 25}-{(g.Key * 25) + 24}: {g.Count()} (slots {string.Join(',', g.Select(m => m.Slot).Distinct().Order())})");
        }

        var slot = report.Mismatches
            .GroupBy(m => m.Slot)
            .OrderByDescending(g => g.Max(m => Math.Abs(m.Delta)))
            .First().Key;

        output.WriteLine($"slot {slot} in tick order, first 20:");
        foreach (var m in report.Mismatches.Where(m => m.Slot == slot).OrderBy(m => m.Tick).Take(20))
            output.WriteLine($"  t={m.Tick,-5} {m.Field,-6} expected={m.Expected,-8} actual={m.Actual,-8} delta={m.Delta}");

        output.WriteLine($"resyncs={report.Resyncs}");

        Assert.Fail("analysis output above");
    }

    /// <remarks>
    /// <c>HUNT_ROUNDS</c> matches, keeping any whose lag-0 error exceeds
    /// <c>HUNT_THRESHOLD</c> into <c>HUNT_KEEP</c>.
    /// </remarks>
    [Fact(Skip = Tool)]
    public void Hunt()
    {
        var keep = Environment.GetEnvironmentVariable("HUNT_KEEP")
            ?? Path.Combine(Path.GetTempPath(), "gg2-outliers");
        Directory.CreateDirectory(keep);

        var rounds = int.Parse(Environment.GetEnvironmentVariable("HUNT_ROUNDS") ?? "60");
        var threshold = long.Parse(Environment.GetEnvironmentVariable("HUNT_THRESHOLD") ?? "50");
        var caught = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        for (var i = 0; i < rounds; i++)
        {
            var directory = Path.Combine(
                Path.GetTempPath(), "gg2-tests", $"hunt-{Guid.NewGuid():N}");

            try
            {
                var path = Record(directory);
                var traces = Directory.GetFiles(directory, "*.trace").Length;
                var zero = ErrorAt(path, 0);

                if (zero <= threshold)
                    continue;

                var curve = new Dictionary<int, long>();
                for (var lag = -MaxLag; lag <= MaxLag; lag++)
                    curve[lag] = ErrorAt(path, lag);

                var kept = Path.Combine(keep, $"outlier-{zero}-{Guid.NewGuid():N}.trace");
                File.Copy(path, kept);
                caught++;

                output.WriteLine(
                    $"CAUGHT round {i}: segments={traces} 0={zero} "
                    + string.Join(", ", curve.OrderBy(e => e.Key).Select(e => $"{e.Key:+0;-0;0}={e.Value}"))
                    + $" -> {kept}");
            }
            finally
            {
                try { Directory.Delete(directory, recursive: true); } catch { }
            }
        }

        output.WriteLine($"{rounds} matches in {clock.Elapsed.TotalSeconds:0}s, {caught} over {threshold}");
    }
}
