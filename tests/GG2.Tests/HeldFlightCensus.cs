using GG2.Bots;
using GG2.Simulation;

namespace GG2.Tests;

/// <summary>
/// Which edges a held flight (N1) changes the outcome of, against the tracker
/// alone: the corpus swept twice, diffed edge by edge.
/// </summary>
/// <remarks>
/// A measurement, not a gate; skipped in the suite. <c>gg2-rl</c>
/// <c>docs/PLAN.md</c>, navigation, N1.
/// </remarks>
public sealed class HeldFlightCensus(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact(Skip = "a census: run by hand")]
    public void Census()
    {
        ReferenceTree.RequireNavCache();

        var threads = Math.Max(1, Environment.ProcessorCount - 2);
        var off = new Dictionary<(string, int), IReadOnlyList<EdgeFlight>>();
        var on = new Dictionary<(string, int), IReadOnlyList<EdgeFlight>>();

        EdgeFlights.SweepAll(ReferenceTree.MapsDirectory, ReferenceTree.NavCacheDirectory, distances: true, threads: threads,
            onGraph: (map, area, flights) => off[(map, area)] = flights.ToArray(), heldFlight: false);
        EdgeFlights.SweepAll(ReferenceTree.MapsDirectory, ReferenceTree.NavCacheDirectory, distances: true, threads: threads,
            onGraph: (map, area, flights) => on[(map, area)] = flights.ToArray(), heldFlight: true);

        var lost = new List<string>();
        var won = 0;
        var byType = new Dictionary<NavEdgeType, (int Lost, int Won)>();
        var ticksOff = 0L;
        var ticksOn = 0L;

        foreach (var (key, a) in off)
        {
            var b = on[key];

            for (var i = 0; i < a.Count; i++)
            {
                var x = a[i];
                var y = b[i];

                if (x.Measured && y.Measured)
                {
                    ticksOff += x.Ticks;
                    ticksOn += y.Ticks;
                }

                if (x.Outcome == EdgeFlightOutcome.Arrived && y.Outcome == EdgeFlightOutcome.Stranded)
                {
                    lost.Add($"{key.Item1}/{key.Item2} e{x.Edge} {x.Type} {x.From}->{x.To} ticks {x.Ticks} vs {y.Ticks} miss {y.MissX:0.#}");
                    var t = byType.GetValueOrDefault(x.Type);
                    byType[x.Type] = (t.Lost + 1, t.Won);
                }
                else if (x.Outcome == EdgeFlightOutcome.Stranded && y.Outcome == EdgeFlightOutcome.Arrived)
                {
                    won++;
                    var t = byType.GetValueOrDefault(x.Type);
                    byType[x.Type] = (t.Lost, t.Won + 1);
                }
            }
        }

        output.WriteLine($"lost {lost.Count}, won {won}; ticks on both-measured edges {ticksOff} -> {ticksOn}");
        foreach (var (type, (l, w)) in byType.OrderBy(kv => kv.Key))
            output.WriteLine($"  {type}: lost {l}, won {w}");
        foreach (var line in lost.Take(60))
            output.WriteLine(line);
    }
}
