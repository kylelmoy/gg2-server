using System.Collections.Concurrent;
using System.Diagnostics;
using GG2.Bots;
using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Tests;

/// <summary>
/// How far the follower is from the fastest inputs over an edge: the shipping
/// sweep's flights against <see cref="EdgeSearch"/> from the same start to the
/// same arrival test.
/// </summary>
/// <remarks>
/// A measurement, not a gate; skipped in the suite. <c>gg2-rl</c>
/// <c>docs/PLAN.md</c>, "Navigation by search", NS.
/// </remarks>
public sealed class EdgeSearchCensus(Xunit.Abstractions.ITestOutputHelper output)
{
    private static readonly NavEdgeType[] Types = [NavEdgeType.Jump, NavEdgeType.DoubleJump, NavEdgeType.Fall, NavEdgeType.Walk];

    private sealed class Playback(PlayerKeys[] plan) : IInputSource
    {
        private int _at;

        public PlayerInput Poll(in WorldView world, int slot, int tick)
        {
            ref readonly var c = ref world.Character(slot);
            var keys = _at < plan.Length ? plan[_at++] : PlayerKeys.None;
            return new PlayerInput(keys, c.NetAimDirection, c.AimDistance);
        }
    }

    [Fact(Skip = "a census: run by hand")]
    public void Census()
    {
        ReferenceTree.RequireNavCache();

        var flights = new List<(string Map, int Area, EdgeFlight Flight)>();
        EdgeFlights.SweepAll(
            ReferenceTree.MapsDirectory, ReferenceTree.NavCacheDirectory, distances: true,
            threads: Math.Max(1, Environment.ProcessorCount - 2),
            onGraph: (map, area, list) =>
            {
                foreach (var f in list)
                {
                    if (f.Measured && Types.Contains(f.Type))
                        flights.Add((map, area, f));
                }
            });

        var rng = new Random(7);
        var stranded = flights.Where(f => f.Flight.Outcome == EdgeFlightOutcome.Stranded).ToList();
        var arrived = flights.Where(f => f.Flight.Outcome == EdgeFlightOutcome.Arrived)
            .OrderBy(_ => rng.Next()).Take(1000).ToList();
        var work = stranded.Concat(arrived).ToList();

        // EDGE_SEARCH_LIMIT caps the work for a timing run.
        if (int.TryParse(Environment.GetEnvironmentVariable("EDGE_SEARCH_LIMIT"), out var limit))
            work = stranded.Take(limit).Concat(arrived.Take(limit)).ToList();

        var results = new ConcurrentBag<(EdgeFlight Flight, EdgeSearch.Result Search, bool Replayed)>();
        var clock = Stopwatch.StartNew();

        Parallel.ForEach(work, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 2) }, item =>
        {
            var (map, area, flight) = item;
            var world = ScenarioWorld.Load(map, area, ReferenceTree.MapsDirectory, ReferenceTree.NavCacheDirectory);
            var graph = world.Nav!;
            var cls = EdgeFlights.ClassFor(flight.Type);
            var slot = world.Place(cls, flight.Team, Mid(graph, flight.From), EdgeFlights.StandingY(graph, flight.From));
            var start = world.State.Characters[slot];
            var search = EdgeSearch.Fastest(world.Collision, graph, in start, flight.To, Mid(graph, flight.To));

            // The plan replayed in the full world, to check the search's physics
            // is the world's: the same placement, the keys, then the arrival test.
            var replayed = false;
            if (search.Found)
            {
                var check = ScenarioWorld.Load(map, area, ReferenceTree.MapsDirectory, ReferenceTree.NavCacheDirectory);
                var at = check.Place(cls, flight.Team, Mid(graph, flight.From), EdgeFlights.StandingY(graph, flight.From));
                check.Drive(at, new Playback(search.Plan));

                for (var t = 0; t < search.Ticks; t++)
                    check.Step();

                ref readonly var c = ref check.State.Characters[at];
                replayed = c.OnGround && Math.Abs(c.HSpeed) < 1 && graph.NodeFromWorld(c.X, c.Y) == flight.To;
            }

            results.Add((flight, search, replayed));
        });

        var all = results.ToList();
        output.WriteLine($"{all.Count} edges searched in {clock.Elapsed.TotalSeconds:0}s, {all.Sum(r => r.Search.Expanded):N0} states stepped");

        var found = all.Where(r => r.Search.Found).ToList();
        output.WriteLine($"replayed in the full world: {found.Count(r => r.Replayed)} of {found.Count} found plans arrive");

        foreach (var type in Types.Select(t => (NavEdgeType?)t).Append(null))
        {
            bool Of(EdgeFlight f) => type is null || f.Type == type;
            var s = all.Where(r => r.Flight.Outcome == EdgeFlightOutcome.Stranded && Of(r.Flight)).ToList();
            var a = all.Where(r => r.Flight.Outcome == EdgeFlightOutcome.Arrived && Of(r.Flight) && r.Search.Found).ToList();
            var aAll = all.Count(r => r.Flight.Outcome == EdgeFlightOutcome.Arrived && Of(r.Flight));
            var name = type?.ToString() ?? "ALL";

            var line = $"{name,-10} stranded {s.Count,4}: search completes {s.Count(r => r.Search.Found),4}";
            if (a.Count > 0)
            {
                var ratios = a.Select(r => (double)r.Flight.Ticks / r.Search.Ticks).OrderBy(x => x).ToList();
                var follower = a.Sum(r => (long)r.Flight.Ticks);
                var fastest = a.Sum(r => (long)r.Search.Ticks);
                line += $" | arrived {aAll,4} (search found {a.Count}): follower/search median {ratios[ratios.Count / 2]:0.00}"
                    + $" p90 {ratios[ratios.Count * 9 / 10]:0.00}; ticks {follower:N0} vs {fastest:N0}, saved {100.0 * (follower - fastest) / follower:0.0}%";
            }

            output.WriteLine(line);
        }
    }

    private static double Mid(NavGraph graph, int node) =>
        NavGraph.ColumnWorldX((graph.Nodes[node].X0 + graph.Nodes[node].X1) / 2);
}
