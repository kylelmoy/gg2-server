using GG2.Bots;
using GG2.Maps;
using GG2.Parity;
using GG2.Protocol;
using GG2.Server;
using Xunit.Abstractions;

namespace GG2.Tests;

/// <summary>
/// The two tools that caught, localised and finally SOLVED the outlier in
/// <c>docs/PARITY.md</c>, "the phase gate's outlier was a STUCK SLOT".
/// </summary>
/// <remarks>
/// ⛔ <b>Both are SKIPPED, because neither is a gate.</b> <see cref="Hunt"/> runs
/// dozens of full bot matches and keeps the ones that misbehave; <see cref="Analyse"/>
/// grades a kept one. Kept in the suite rather than thrown away because the
/// outlier is rare, cost a session to catch once, and is only PARTLY fixed —
/// the stale-yprevious cause is closed and gated (<c>AnchorYPreviousTests</c>),
/// but two of the eleven traces caught on 2026-09-06 did not move at all
/// (p99 y 48 and 74), so at least one more cause is live and these are how to
/// go after it.
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

        // ⛔ The dump, when a slot is named. Reconcile compares and Anchor snaps
        // the slot back to the trace on the SAME tick, in that order — so a
        // constant error is not drift, it is the replay re-diverging by the same
        // amount in the seven ticks between two anchors, every time. Only a
        // per-tick dump can show which of those seven ticks does it.
        var dump = Environment.GetEnvironmentVariable("ANALYSE_DUMP_SLOT");

        // ⚠️ Console.Out is redirected around the replay because Replayer.Dump
        // writes there and xunit does not capture raw Console — it surfaces only
        // ITestOutputHelper. Without this the dump goes to the test host's stdout
        // and `dotnet test` swallows it, which reads as "the dump did not fire".
        var captured = new StringWriter();
        var previous = Console.Out;
        DriftReport report;

        Console.SetOut(captured);

        try
        {
            report = new Replayer(new PortedSimulation(MapLoader))
            {
                Compare = Fields.Trajectory,
                InputLag = 0,
                DumpSlot = dump is null ? null : int.Parse(dump),
                DumpFrom = int.Parse(Environment.GetEnvironmentVariable("ANALYSE_DUMP_FROM") ?? "0"),
                DumpTo = int.Parse(Environment.GetEnvironmentVariable("ANALYSE_DUMP_TO") ?? "2147483647"),
            }.Replay(
                Path.GetFileName(path), trace.Header.Map, ticks, ReplayMode.OpenLoop,
                stopAtFirstBadTick: false);
        }
        finally
        {
            Console.SetOut(previous);
        }

        if (captured.ToString() is { Length: > 0 } dumped)
            output.WriteLine(dumped);

        // ⛔ What the TRACE says about the slot, beside what the replay did. The
        // replay's copy being stationary is only half the story: Anchor snaps a
        // slot to `Observed` on every position-bearing tick, so either the trace
        // is not offering one (and Anchor clears instead) or the anchor is being
        // skipped. These two columns separate those without guessing.
        if (dump is not null)
        {
            var slotIndex = int.Parse(dump);
            var from = int.Parse(Environment.GetEnvironmentVariable("ANALYSE_DUMP_FROM") ?? "0");
            var to = int.Parse(Environment.GetEnvironmentVariable("ANALYSE_DUMP_TO") ?? "2147483647");

            output.WriteLine($"what the TRACE holds for slot {slotIndex}:");

            foreach (var t in ticks.Where(t => t.Index >= from && t.Index <= to))
            {
                var recorded = slotIndex < t.Slots.Length ? t.Slots[slotIndex].Observed : null;

                output.WriteLine(
                    $"  tick {t.Index,5} {(t.HasPositions ? "*" : " ")} "
                    + (recorded is { } o ? $"x {o.X,6} y {o.Y,6} hp {o.Hp,4}" : "no character in the trace"));
            }
        }

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
