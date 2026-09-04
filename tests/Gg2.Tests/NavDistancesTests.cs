using System.Diagnostics;
using Gg2.Bots;
using Gg2.Protocol;
using Xunit.Abstractions;

namespace Gg2.Tests;

/// <summary>
/// The precomputed distance table: that it is a valid heuristic, and what it
/// buys.
/// </summary>
/// <remarks>
/// <b>The correctness claim is admissibility and it is checked directly.</b>
/// The table is built on the fully permissive graph, so its answer is at or
/// below the true remaining cost for every asker whatever the gates, the
/// blacklist and the crowd have done — and if that is ever false the search
/// silently returns worse routes rather than failing, which is the kind of bug
/// this repo keeps finding in other clothes.
/// </remarks>
public sealed class NavDistancesTests(ITestOutputHelper output)
{
    private static NavGraph Graph(string map = "ctf_truefort")
    {
        ReferenceTree.RequireNavCache();
        return NavCache.Load(ReferenceTree.NavCacheDirectory, map, 1);
    }

    /// <summary>
    /// The table never overestimates — on every shipped graph, against the true
    /// cost of the route the search actually returns.
    /// </summary>
    /// <remarks>
    /// <b>The gate the whole design rests on.</b> A heuristic above the true
    /// remaining cost is inadmissible, and an inadmissible heuristic combined
    /// with the closed-set skip gives routes that are wrong rather than merely
    /// suboptimal. Checked against a route priced with the graph's own costs,
    /// which is an upper bound on the cheapest — so <c>table &lt;= priced</c>
    /// must hold with no tolerance beyond float rounding, and the rounding is
    /// already handled by storing each value rounded DOWN.
    /// </remarks>
    [Fact]
    public void TheTableNeverOverestimates()
    {
        ReferenceTree.RequireNavCache();

        var checkedPairs = 0;

        foreach (var (map, area) in NavCache.List(ReferenceTree.NavCacheDirectory))
        {
            var graph = NavCache.Load(ReferenceTree.NavCacheDirectory, map, area);
            var table = NavDistances.Derive(graph);
            var search = new NavSearch(graph);
            var route = new List<int>();

            // Every query class the shipped bots produce, because the claim is
            // that ONE table serves all of them.
            foreach (var query in Queries())
            {
                var legs = 0;

                for (var from = 0; from < graph.NodeCount && legs < 60; from += 13)
                {
                    for (var to = 0; to < graph.NodeCount && legs < 60; to += 17)
                    {
                        if (from == to || !search.Find(from, to, query, route))
                            continue;

                        legs++;
                        checkedPairs++;

                        var actual = Priced(graph, route);
                        var estimate = table.Between(from, to);

                        Assert.True(
                            estimate <= actual,
                            $"{map} {from}->{to} {query.Team}: table {estimate} > actual {actual}");
                    }
                }
            }
        }

        output.WriteLine($"{checkedPairs} pairs checked across every shipped graph");
        Assert.True(checkedPairs > 1000);
    }

    /// <summary>
    /// An unreachable pair is answered without searching, and agrees with the
    /// search.
    /// </summary>
    /// <remarks>
    /// The early-out is sound only because the table is built on the permissive
    /// graph: if THAT cannot reach the goal, no restricted query can. The
    /// converse does not hold and is not claimed — a finite entry only means the
    /// search has to run.
    /// </remarks>
    [Fact]
    public void AnUnreachablePairIsRefusedWithoutASearch()
    {
        var graph = Graph("cp_dirtbowl");
        var table = NavDistances.Derive(graph);
        var search = new NavSearch(graph);
        var query = new NavQuery(Team.Red);
        var route = new List<int>();

        var refused = 0;

        for (var from = 0; from < graph.NodeCount; from += 11)
        {
            for (var to = 0; to < graph.NodeCount; to += 13)
            {
                if (from == to || !float.IsPositiveInfinity(table.Between(from, to)))
                    continue;

                refused++;

                // Without the table the search must agree there is no route.
                Assert.False(search.Find(from, to, query, route));

                // With it, the same answer and no pops at all.
                Assert.False(search.Find(from, to, query, route, null, null, default, table));
                Assert.Equal(0, search.Pops);
            }
        }

        output.WriteLine($"{refused} unreachable pairs answered from the table");
        Assert.True(refused > 0);
    }

    /// <summary>
    /// What the table buys, on the workload the bots actually produce.
    /// </summary>
    /// <remarks>
    /// <b>Goals clustered on a few objectives and a team's routes occupying</b>,
    /// because that is what a match looks like and it is the workload the
    /// straight-line heuristic does worst on. ⚠️ <b>Interleaved</b>: the arms
    /// alternate inside one process after a shared warm-up, because this repo's
    /// own record is that a cold A\* reads 57-70 µs for a search that settles at
    /// 15, and that the sampling profiler is unusable here.
    /// </remarks>
    [Fact(Skip = "Measurement, not a gate. Numbers in docs/TRAPS-BOTS.md.")]
    public void WhatTheTableBuys()
    {
        ReferenceTree.RequireNavCache();

        output.WriteLine(
            $"{"graph",-22} {"euclid",7} {"loose",7} {"matched",7} | {"gain",6} | "
            + $"{"pops e/l/m",14} {"MB",5} {"ms",6}");

        double euclidAll = 0, looseAll = 0, tableAll = 0;
        var maps = 0;

        foreach (var (map, area) in NavCache.List(ReferenceTree.NavCacheDirectory))
        {
            var graph = NavCache.Load(ReferenceTree.NavCacheDirectory, map, area);

            if (graph.NodeCount < 200)
                continue;

            // Matched to the asker below — a plain walker, no jump arcs. The
            // permissive table is admissible for it too but far looser, and the
            // difference is what this measures.
            var sw = Stopwatch.StartNew();
            var table = NavDistances.Derive(graph, false, false, false);
            sw.Stop();

            var permissive = NavDistances.Derive(graph);

            var search = new NavSearch(graph);
            var query = new NavQuery(Team.Red);
            var route = new List<int>();
            var rng = new Random(20260904);
            var n = graph.NodeCount;

            // Four objectives, and a team's worth of routes crowding.
            var objectives = new int[4];
            for (var i = 0; i < 4; i++)
                objectives[i] = rng.Next(n);

            var occupancy = new Dictionary<long, int>();

            for (var bot = 0; bot < 6; bot++)
            {
                if (!search.Find(rng.Next(n), objectives[rng.Next(4)], query, route))
                    continue;

                for (var i = 1; i < route.Count; i++)
                {
                    var key = NavGraph.EdgeKey(route[i - 1], route[i]);
                    occupancy[key] = occupancy.GetValueOrDefault(key) + 1;
                }
            }

            var pairs = new List<(int From, int To)>();
            var spins = 0;

            while (pairs.Count < 500 && spins++ < 100_000)
            {
                var from = rng.Next(n);
                var to = objectives[rng.Next(4)];

                if (from != to && !float.IsPositiveInfinity(table.Between(from, to)))
                    pairs.Add((from, to));
            }

            if (pairs.Count < 50)
                continue;

            double Bench(NavDistances? with, out double pops)
            {
                // Warm past tier-1: promotion is at 30 calls and the rejit is
                // asynchronous, so a short warm-up measures tier-0 code.
                for (var w = 0; w < 4; w++)
                    foreach (var (from, to) in pairs)
                        search.Find(from, to, query, route, null, occupancy, default, with);

                var clock = Stopwatch.StartNew();
                long popped = 0;

                for (var rep = 0; rep < 5; rep++)
                {
                    foreach (var (from, to) in pairs)
                    {
                        search.Find(from, to, query, route, null, occupancy, default, with);
                        popped += search.Pops;
                    }
                }

                clock.Stop();
                pops = popped / (double)(5 * pairs.Count);
                return clock.Elapsed.TotalMicroseconds / (5.0 * pairs.Count);
            }

            // Interleaved, every arm twice, alternating.
            var euclid = Bench(null, out var euclidPops);
            var loose = Bench(permissive, out var loosePops);
            var withTable = Bench(table, out var tablePops);
            euclid = (euclid + Bench(null, out _)) / 2;
            loose = (loose + Bench(permissive, out _)) / 2;
            withTable = (withTable + Bench(table, out _)) / 2;

            euclidAll += euclid;
            looseAll += loose;
            tableAll += withTable;
            maps++;

            output.WriteLine(
                $"{map + "_a" + area,-22} {euclid,7:F1} {loose,7:F1} {withTable,7:F1} | "
                + $"{euclid / withTable,5:F2}x | {euclidPops,4:F0}/{loosePops,4:F0}/{tablePops,-4:F0} "
                + $"{table.Bytes / 1048576.0,5:F2} {sw.ElapsedMilliseconds,6}");
        }

        output.WriteLine(string.Empty);
        output.WriteLine(
            $"MEAN over {maps} graphs: euclid {euclidAll / maps:F2}us, permissive table "
            + $"{looseAll / maps:F2}us ({euclidAll / looseAll:F2}x), class-matched table "
            + $"{tableAll / maps:F2}us ({euclidAll / tableAll:F2}x)");
    }

    /// <summary>Every query class the shipped bots produce.</summary>
    private static IEnumerable<NavQuery> Queries()
    {
        foreach (var team in (Team[])[Team.Red, Team.Blue])
        {
            foreach (var intel in (bool[])[false, true])
            {
                yield return new NavQuery(team, intel);
                yield return new NavQuery(team, intel, SetupGatesClosed: true);
                yield return new NavQuery(team, intel, CanDoubleJump: true);
                yield return new NavQuery(team, intel, CanRocketJump: true);
            }
        }
    }

    /// <summary>A route priced with the graph's own edge costs.</summary>
    private static double Priced(NavGraph graph, IReadOnlyList<int> route)
    {
        var total = 0.0;

        for (var i = 1; i < route.Count; i++)
        {
            var best = double.PositiveInfinity;

            foreach (var edge in graph.EdgesFrom(route[i - 1]))
                if (edge.To == route[i] && edge.Cost < best)
                    best = edge.Cost;

            if (!double.IsPositiveInfinity(best))
                total += best;
        }

        return total;
    }
}
