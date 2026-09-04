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

        double[] euclidAll = [0, 0], looseAll = [0, 0], tableAll = [0, 0];
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

            // Pairs THIS asker can route, not merely pairs the gates-open table
            // can. A goal behind the other team's gate is finite in every table
            // and unreachable for the asker, and such a query expands the whole
            // component with or without a table — a real cost, the same in every
            // arm, and not what this measures. Counted and printed instead.
            var pairs = new List<(int From, int To)>();
            var gated = 0;
            var spins = 0;

            while (pairs.Count < 500 && spins++ < 100_000)
            {
                var from = rng.Next(n);
                var to = objectives[rng.Next(4)];

                if (from == to || float.IsPositiveInfinity(table.Between(from, to)))
                    continue;

                if (search.Find(from, to, query, route))
                    pairs.Add((from, to));
                else
                    gated++;
            }

            if (pairs.Count < 50)
                continue;

            output.WriteLine($"{map}_a{area}: {gated} of {pairs.Count + gated} finite pairs are behind a gate for this asker");

            double Bench(NavDistances? with, IReadOnlyDictionary<long, int>? crowd, out double pops)
            {
                // Warm past tier-1: promotion is at 30 calls and the rejit is
                // asynchronous, so a short warm-up measures tier-0 code.
                for (var w = 0; w < 4; w++)
                    foreach (var (from, to) in pairs)
                        search.Find(from, to, query, route, null, crowd, default, with);

                var clock = Stopwatch.StartNew();
                long popped = 0;

                for (var rep = 0; rep < 5; rep++)
                {
                    foreach (var (from, to) in pairs)
                    {
                        search.Find(from, to, query, route, null, crowd, default, with);
                        popped += search.Pops;
                    }
                }

                clock.Stop();
                pops = popped / (double)(5 * pairs.Count);
                return clock.Elapsed.TotalMicroseconds / (5.0 * pairs.Count);
            }

            // Two arms: the live server's crowd on the chains, and the crowd-free
            // graph `eval` and every rollout search — where the table's chain is
            // the answer at the first pop whenever the blacklist is empty.
            foreach (var (arm, crowd) in (( string, IReadOnlyDictionary<long, int>?)[])[("crowd", occupancy), ("clean", null)])
            {
                // Interleaved, every arm twice, alternating.
                var euclid = Bench(null, crowd, out var euclidPops);
                var loose = Bench(permissive, crowd, out var loosePops);
                var withTable = Bench(table, crowd, out var tablePops);
                euclid = (euclid + Bench(null, crowd, out _)) / 2;
                loose = (loose + Bench(permissive, crowd, out _)) / 2;
                withTable = (withTable + Bench(table, crowd, out _)) / 2;

                var a = arm == "crowd" ? 0 : 1;
                euclidAll[a] += euclid;
                looseAll[a] += loose;
                tableAll[a] += withTable;

                output.WriteLine(
                    $"{map + "_a" + area + " " + arm,-22} {euclid,7:F1} {loose,7:F1} {withTable,7:F1} | "
                    + $"{euclid / withTable,5:F2}x | {euclidPops,4:F0}/{loosePops,4:F0}/{tablePops,-4:F0} "
                    + $"{table.Bytes / 1048576.0,5:F2} {sw.ElapsedMilliseconds,6}");
            }

            maps++;
        }

        output.WriteLine(string.Empty);

        foreach (var a in (int[])[0, 1])
        {
            output.WriteLine(
                $"MEAN {(a == 0 ? "crowd" : "clean")} over {maps} graphs: euclid {euclidAll[a] / maps:F2}us, "
                + $"permissive table {looseAll[a] / maps:F2}us ({euclidAll[a] / looseAll[a]:F2}x), "
                + $"class-matched table {tableAll[a] / maps:F2}us ({euclidAll[a] / tableAll[a]:F2}x)");
        }
    }

    /// <summary>
    /// Both plans return a route of the same optimal cost, on the same perturbed
    /// legs.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Now a production gate rather than scaffolding, because BOTH plans
    /// ship.</b> <see cref="NavPlan.Auto"/> routes a crowded query through
    /// <see cref="NavPlan.VerifyThenSearch"/> and an uncrowded one through
    /// <see cref="NavPlan.Incumbent"/>, so a bot's route can come from either
    /// depending on what its team-mates are doing that tick. If the two ever
    /// disagreed, a bot's path would change with the crowd for reasons that have
    /// nothing to do with the crowd — and both would still look optimal in
    /// isolation.
    /// <para>
    /// ⚠️ Compared on COST, not on node sequence. Two distinct routes can price
    /// identically — a walk and a jump joining the same pair, ties broken by heap
    /// order — and demanding the same nodes would fail on a difference that costs
    /// the bot nothing.
    /// </para>
    /// </remarks>
    [Fact]
    public void BothPlansAgreeOnCost()
    {
        ReferenceTree.RequireNavCache();

        var rng = new Random(20260904);
        var legs = 0;
        var perturbed = 0;

        foreach (var (map, area) in NavCache.List(ReferenceTree.NavCacheDirectory))
        {
            var graph = NavCache.Load(ReferenceTree.NavCacheDirectory, map, area);
            var n = graph.NodeCount;
            var set = NavDistanceSet.Derive(graph);
            var search = new NavSearch(graph);
            var a = new List<int>();
            var b = new List<int>();

            foreach (var query in Queries())
            {
                var table = set.For(in query);

                for (var leg = 0; leg < 12; leg++)
                {
                    var start = rng.Next(n);
                    var goal = rng.Next(n);

                    if (start == goal || float.IsPositiveInfinity(table.Between(start, goal)))
                        continue;

                    var chain = Chain(table, start, goal);
                    var blocked = new HashSet<long>();
                    var occupancy = new Dictionary<long, int>();

                    if (chain.Count > 2 && leg % 3 != 0)
                    {
                        var i = rng.Next(chain.Count - 1);
                        blocked.Add(NavGraph.EdgeKey(chain[i], chain[i + 1]));
                    }

                    if (chain.Count > 2 && leg % 3 != 1)
                    {
                        for (var k = 0; k < 2; k++)
                        {
                            var i = rng.Next(chain.Count - 1);
                            occupancy[NavGraph.EdgeKey(chain[i], chain[i + 1])] = 1 + rng.Next(3);
                        }
                    }

                    if (blocked.Count > 0 || occupancy.Count > 0)
                        perturbed++;

                    var okA = search.Find(
                        start, goal, query, a, blocked, occupancy, default, table, NavPlan.Incumbent);
                    var okB = search.Find(
                        start, goal, query, b, blocked, occupancy, default, table, NavPlan.VerifyThenSearch);

                    Assert.Equal(okA, okB);

                    if (!okA)
                        continue;

                    var costA = LivePrice(graph, a, query, blocked, occupancy);
                    var costB = LivePrice(graph, b, query, blocked, occupancy);

                    Assert.True(
                        Math.Abs(costA - costB) <= costA * 2e-6,
                        $"{map}_a{area} {start}->{goal} {query}: incumbent {costA}, verify {costB}");

                    legs++;
                }
            }
        }

        output.WriteLine($"{legs} legs agreed, {perturbed} of them perturbed");
        Assert.True(legs > 1000, $"only {legs} legs; the sweep proved little");
    }

    /// <summary>
    /// <see cref="NavPlan.Auto"/> really does pick the plan that matches the
    /// crowd.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>The gate that stops the whole selection being decorative.</b> An
    /// <c>Auto</c> that always chose one plan would satisfy
    /// <see cref="BothPlansAgreeOnCost"/> perfectly — the routes would be right,
    /// the situational win would just be silently gone, and no other test in this
    /// repo could see it. Expansions are the discriminator because the two plans
    /// differ in exactly that.
    /// </remarks>
    [Fact]
    public void AutoPicksThePlanThatMatchesTheCrowd()
    {
        ReferenceTree.RequireNavCache();

        var graph = NavCache.Load(ReferenceTree.NavCacheDirectory, "ctf_truefort", 1);
        var set = NavDistanceSet.Derive(graph);
        var search = new NavSearch(graph);
        var query = new NavQuery(Team.Red);
        var table = set.For(in query);
        var route = new List<int>();
        var rng = new Random(20260904);

        var crowdedLegs = 0;
        var cleanLegs = 0;

        for (var leg = 0; leg < 400; leg++)
        {
            var start = rng.Next(graph.NodeCount);
            var goal = rng.Next(graph.NodeCount);

            if (start == goal || float.IsPositiveInfinity(table.Between(start, goal)))
                continue;

            var chain = Chain(table, start, goal);

            if (chain.Count < 3)
                continue;

            var occupancy = new Dictionary<long, int>();

            for (var k = 0; k < 2; k++)
            {
                var i = rng.Next(chain.Count - 1);
                occupancy[NavGraph.EdgeKey(chain[i], chain[i + 1])] = 1 + rng.Next(3);
            }

            // Crowded: Auto must behave as VerifyThenSearch.
            if (search.Find(start, goal, query, route, null, occupancy, default, table, NavPlan.Auto))
            {
                var auto = search.Pops;
                search.Find(start, goal, query, route, null, occupancy, default, table, NavPlan.VerifyThenSearch);
                Assert.Equal(search.Pops, auto);
                crowdedLegs++;
            }

            // Uncrowded: Auto must behave as Incumbent. An EMPTY map counts as
            // uncrowded — NavSearch hoists `Count: > 0`, not `is not null`.
            if (search.Find(start, goal, query, route, null, new Dictionary<long, int>(), default, table, NavPlan.Auto))
            {
                var auto = search.Pops;
                search.Find(start, goal, query, route, null, null, default, table, NavPlan.Incumbent);
                Assert.Equal(search.Pops, auto);
                cleanLegs++;
            }
        }

        output.WriteLine($"{crowdedLegs} crowded legs and {cleanLegs} clean legs matched their plan");
        Assert.True(crowdedLegs > 50 && cleanLegs > 50, "the sweep found too few legs to prove anything");
    }

    /// <summary>
    /// CPD-Search against MASTER — the shipped weighted straight-line A\* — in
    /// time, in expansions, and in what the routes cost.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>The cost column is not optional.</b> Master is a <em>weighted</em>
    /// search: <c>NavConstants.SearchWeight</c> inflates its heuristic by 1.3 and
    /// the closed-set skip goes with it, so it is allowed to return a worse route
    /// and does — about 8.5% dearer than optimal, stable to a tenth of a point.
    /// A speed comparison that leaves that out has master winning a race it is
    /// running downhill.
    /// <para>
    /// ⚠️ Interleaved and run twice an arm, alternating, because the machine is
    /// noisy enough that two sequential blocks would measure the machine.
    /// <c>tools/bench/README.md</c> trap 5 is the same rule.
    /// </para>
    /// <para>
    /// Both crowd arms are kept now that <c>--no-bot-occupancy</c> makes the
    /// choice real: the crowded one is a live server with occupancy on, the clean
    /// one is that server with it off, and it is also what <c>eval</c> and every
    /// training rollout have always run.
    /// </para>
    /// </remarks>
    [Fact(Skip = "Measurement, not a gate. Numbers in docs/TRAPS-BOTS.md.")]
    public void WhatTheTableCostsAgainstMaster()
    {
        ReferenceTree.RequireNavCache();

        output.WriteLine(
            $"{"graph / arm",-26} {"master",8} {"cpd",8} {"verify",8} {"auto",8} | "
            + $"{"master cost",11}");

        double[] incAll = [0, 0], mastAll = [0, 0], verAll = [0, 0], autoAll = [0, 0];
        double[] costAll = [0, 0];
        var maps = 0;

        foreach (var (map, area) in NavCache.List(ReferenceTree.NavCacheDirectory))
        {
            var graph = NavCache.Load(ReferenceTree.NavCacheDirectory, map, area);

            if (graph.NodeCount < 200)
                continue;

            var table = NavDistances.Derive(graph, false, false, false);
            var search = new NavSearch(graph);
            var query = new NavQuery(Team.Red);
            var route = new List<int>();
            var rng = new Random(20260904);
            var n = graph.NodeCount;

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

                if (from == to || float.IsPositiveInfinity(table.Between(from, to)))
                    continue;

                if (search.Find(from, to, query, route))
                    pairs.Add((from, to));
            }

            if (pairs.Count < 50)
                continue;

            // `with` null is MASTER: no table, so Find falls to the shipped
            // straight-line heuristic inflated by NavConstants.SearchWeight.
            // ⚠️ That arm answers a DIFFERENT question — it is allowed to return
            // a suboptimal route — which is why `cost` is measured beside the
            // clock and not left for the reader to assume away.
            double Bench(NavDistances? with, NavPlan plan, IReadOnlyDictionary<long, int>? crowd,
                out double pops, out double cost)
            {
                for (var w = 0; w < 4; w++)
                    foreach (var (from, to) in pairs)
                        search.Find(from, to, query, route, null, crowd, default, with, plan);

                var clock = Stopwatch.StartNew();
                long popped = 0;

                for (var rep = 0; rep < 5; rep++)
                {
                    foreach (var (from, to) in pairs)
                    {
                        search.Find(from, to, query, route, null, crowd, default, with, plan);
                        popped += search.Pops;
                    }
                }

                clock.Stop();

                // Priced once, outside the clock, on the last repetition's routes.
                var none = new HashSet<long>();
                var empty = crowd ?? new Dictionary<long, int>();
                var total = 0.0;
                var priced = 0;

                foreach (var (from, to) in pairs)
                {
                    if (!search.Find(from, to, query, route, null, crowd, default, with, plan))
                        continue;

                    var c = LivePrice(graph, route, query, none, empty);

                    if (!double.IsPositiveInfinity(c))
                    {
                        total += c;
                        priced++;
                    }
                }

                cost = priced == 0 ? 0 : total / priced;
                pops = popped / (double)(5 * pairs.Count);
                return clock.Elapsed.TotalMicroseconds / (5.0 * pairs.Count);
            }

            foreach (var (arm, crowd) in ((string, IReadOnlyDictionary<long, int>?)[])
                [("crowd", occupancy), ("clean", null)])
            {
                var mast = Bench(null, NavPlan.Auto, crowd, out _, out var mastCost);
                var inc = Bench(table, NavPlan.Incumbent, crowd, out _, out var optCost);
                var ver = Bench(table, NavPlan.VerifyThenSearch, crowd, out _, out _);
                var auto = Bench(table, NavPlan.Auto, crowd, out _, out _);
                mast = (mast + Bench(null, NavPlan.Auto, crowd, out _, out _)) / 2;
                inc = (inc + Bench(table, NavPlan.Incumbent, crowd, out _, out _)) / 2;
                ver = (ver + Bench(table, NavPlan.VerifyThenSearch, crowd, out _, out _)) / 2;
                auto = (auto + Bench(table, NavPlan.Auto, crowd, out _, out _)) / 2;

                var a = arm == "crowd" ? 0 : 1;
                mastAll[a] += mast;
                incAll[a] += inc;
                verAll[a] += ver;
                autoAll[a] += auto;
                costAll[a] += optCost == 0 ? 1 : mastCost / optCost;

                output.WriteLine(
                    $"{map + "_a" + area + " " + arm,-26} {mast,8:F2} {inc,8:F2} {ver,8:F2} {auto,8:F2} | "
                    + $"{(optCost == 0 ? 1 : mastCost / optCost),10:P1}");
            }

            maps++;
        }

        output.WriteLine(string.Empty);

        foreach (var a in (int[])[0, 1])
        {
            output.WriteLine(
                $"MEAN {(a == 0 ? "crowd" : "clean")} over {maps} graphs: "
                + $"master {mastAll[a] / maps:F2}us, CPD {incAll[a] / maps:F2}us, "
                + $"verify {verAll[a] / maps:F2}us, AUTO {autoAll[a] / maps:F2}us "
                + $"({mastAll[a] / autoAll[a]:F2}x master) "
                + $"| master's routes cost {costAll[a] / maps:P1} of optimal");
        }
    }

    /// <summary>
    /// Following the next hops reaches the goal, and pricing each hop at the
    /// graph's own cost reproduces the distance.
    /// </summary>
    /// <remarks>
    /// The two halves of the table come out of one Dijkstra and this is what
    /// ties them together: a chain that priced above its distance would be a
    /// route the table promised and does not have, and a chain that priced
    /// below it would be a distance that is not a lower bound. Both tables —
    /// permissive and walker — because the hop has to be an edge the table was
    /// built with, not merely an edge.
    /// </remarks>
    [Fact]
    public void TheNextHopChainPricesToTheDistance()
    {
        ReferenceTree.RequireNavCache();

        var chains = 0;

        foreach (var (map, area) in NavCache.List(ReferenceTree.NavCacheDirectory))
        {
            var graph = NavCache.Load(ReferenceTree.NavCacheDirectory, map, area);
            var n = graph.NodeCount;

            foreach (var walker in (bool[])[false, true])
            {
                var table = walker
                    ? NavDistances.Derive(graph, false, false, false)
                    : NavDistances.Derive(graph);

                for (var from = 0; from < n; from += 7)
                {
                    for (var goal = 0; goal < n; goal += 11)
                    {
                        var expected = table.Between(from, goal);

                        if (from == goal)
                        {
                            Assert.Equal(0f, expected);
                            Assert.Equal(NavDistances.None, table.NextHop(from, goal));
                            continue;
                        }

                        if (float.IsPositiveInfinity(expected))
                        {
                            Assert.Equal(NavDistances.None, table.NextHop(from, goal));
                            continue;
                        }

                        var at = from;
                        var total = 0.0;
                        var hops = 0;

                        while (at != goal)
                        {
                            var next = table.NextHop(at, goal);

                            Assert.True(next >= 0, $"{map}_a{area} {from}->{goal}: the chain ends at {at}");
                            Assert.True(++hops <= n, $"{map}_a{area} {from}->{goal}: the chain cycles");

                            total += CheapestStatic(graph, at, next, walker);
                            at = next;
                        }

                        Assert.True(
                            expected <= total && total <= expected * (1 + 1e-6),
                            $"{map}_a{area} {from}->{goal} walker={walker}: table {expected}, chain {total}");

                        chains++;
                    }
                }
            }
        }

        output.WriteLine($"{chains} chains walked across every shipped graph, both tables");
        Assert.True(chains > 10_000);
    }

    /// <summary>
    /// CPD-Search returns the cheapest route under LIVE costs — a ban and a
    /// crowd on the stored chain included — and never a hop the asker cannot
    /// take.
    /// </summary>
    /// <remarks>
    /// <b>The optimality claim, checked against an oracle that has nothing in
    /// common with the search.</b> A plain forward Dijkstra over the routable
    /// edges at live cost, written here, prices the true optimum; the search's
    /// route is re-priced hop by hop the same way. The perturbations are put ON
    /// the chain deliberately, so most legs are dirty and the bound has to do
    /// its work rather than the first pop; the count of first-pop answers is
    /// printed, not asserted.
    /// <para>
    /// Tolerance is the search's own: an incumbent is accepted within 1e-6 of
    /// the proven bound, so the route may price up to that above the optimum
    /// and no more.
    /// </para>
    /// </remarks>
    [Fact]
    public void CpdSearchIsOptimalUnderLiveCosts()
    {
        ReferenceTree.RequireNavCache();

        var rng = new Random(20260904);
        var legs = 0;
        var firstPop = 0;

        foreach (var (map, area) in NavCache.List(ReferenceTree.NavCacheDirectory))
        {
            var graph = NavCache.Load(ReferenceTree.NavCacheDirectory, map, area);
            var n = graph.NodeCount;
            var set = NavDistanceSet.Derive(graph);
            var search = new NavSearch(graph);
            var path = new List<int>();

            foreach (var query in Queries())
            {
                var table = set.For(in query);

                for (var leg = 0; leg < 12; leg++)
                {
                    var start = rng.Next(n);
                    var goal = rng.Next(n);

                    if (start == goal || float.IsPositiveInfinity(table.Between(start, goal)))
                        continue;

                    var chain = Chain(table, start, goal);
                    var blocked = new HashSet<long>();
                    var occupancy = new Dictionary<long, int>();

                    // Ban a hop on two legs in three, crowd two hops on two in
                    // three: one leg in three of each kind is left alone.
                    if (chain.Count > 2 && leg % 3 != 0)
                    {
                        var i = rng.Next(chain.Count - 1);
                        blocked.Add(NavGraph.EdgeKey(chain[i], chain[i + 1]));
                    }

                    if (chain.Count > 2 && leg % 3 != 1)
                    {
                        for (var k = 0; k < 2; k++)
                        {
                            var i = rng.Next(chain.Count - 1);
                            occupancy[NavGraph.EdgeKey(chain[i], chain[i + 1])] = 1 + rng.Next(3);
                        }
                    }

                    var best = ReferenceCost(graph, start, goal, query, blocked, occupancy);

                    if (!search.Find(start, goal, query, path, blocked, occupancy, default, table))
                    {
                        Assert.True(
                            double.IsPositiveInfinity(best),
                            $"{map}_a{area} {start}->{goal} {query}: no route found, but one costs {best}");
                        continue;
                    }

                    if (search.Pops == 1)
                        firstPop++;

                    var priced = LivePrice(graph, path, query, blocked, occupancy);

                    Assert.True(
                        !double.IsPositiveInfinity(priced),
                        $"{map}_a{area} {start}->{goal} {query}: the route carries a hop the asker cannot take");

                    Assert.True(
                        priced <= best * (1 + 2e-6),
                        $"{map}_a{area} {start}->{goal} {query}: route {priced} against optimum {best}");

                    legs++;
                }
            }
        }

        output.WriteLine($"{legs} legs, {firstPop} answered at the first pop");
        Assert.True(legs > 1000, $"only {legs} legs; the sweep proved little");
    }

    /// <summary>The table's chain from <paramref name="start"/> to <paramref name="goal"/>, as nodes.</summary>
    private static List<int> Chain(NavDistances table, int start, int goal)
    {
        var chain = new List<int> { start };
        var at = start;

        while (at != goal && chain.Count <= table.NodeCount)
        {
            at = table.NextHop(at, goal);
            if (at < 0)
                break;

            chain.Add(at);
        }

        return chain;
    }

    /// <summary>
    /// The cheapest edge from <paramref name="from"/> to <paramref name="to"/> a
    /// table built for a walker (or for everybody) would have used.
    /// </summary>
    private static double CheapestStatic(NavGraph graph, int from, int to, bool walker)
    {
        var best = double.PositiveInfinity;

        foreach (var edge in graph.EdgesFrom(from))
        {
            if (edge.To != to)
                continue;

            if (walker && edge.Type is NavEdgeType.DoubleJump or NavEdgeType.RocketJump or NavEdgeType.StickyJump)
                continue;

            if (edge.Cost < best)
                best = edge.Cost;
        }

        return best;
    }

    /// <summary>What this asker pays now for the cheapest routable edge between two nodes.</summary>
    private static double LiveEdge(
        NavGraph graph, int from, int to, in NavQuery query,
        IReadOnlySet<long> blocked, IReadOnlyDictionary<long, int> occupancy)
    {
        var key = NavGraph.EdgeKey(from, to);

        if (blocked.Contains(key))
            return double.PositiveInfinity;

        var crowd = occupancy.GetValueOrDefault(key);
        var best = double.PositiveInfinity;

        foreach (var edge in graph.EdgesFrom(from))
        {
            if (edge.To != to || !NavGraph.EdgeRoutable(in edge, in query))
                continue;

            var cost = edge.Cost * (1 + (BotConstants.OccupancyCost * crowd));
            if (cost < best)
                best = cost;
        }

        return best;
    }

    /// <summary>A route priced at live cost, infinite if any hop is not routable.</summary>
    private static double LivePrice(
        NavGraph graph, IReadOnlyList<int> route, in NavQuery query,
        IReadOnlySet<long> blocked, IReadOnlyDictionary<long, int> occupancy)
    {
        var total = 0.0;

        for (var i = 1; i < route.Count; i++)
            total += LiveEdge(graph, route[i - 1], route[i], in query, blocked, occupancy);

        return total;
    }

    /// <summary>
    /// The true cheapest cost under live pricing — a forward Dijkstra with no
    /// heuristic, no table and no closed-set shortcut.
    /// </summary>
    private static double ReferenceCost(
        NavGraph graph, int start, int goal, in NavQuery query,
        IReadOnlySet<long> blocked, IReadOnlyDictionary<long, int> occupancy)
    {
        var dist = new double[graph.NodeCount];
        Array.Fill(dist, double.PositiveInfinity);
        dist[start] = 0;

        var open = new PriorityQueue<int, double>();
        open.Enqueue(start, 0);

        while (open.TryDequeue(out var u, out var du))
        {
            if (du > dist[u])
                continue;

            if (u == goal)
                return du;

            foreach (var edge in graph.EdgesFrom(u))
            {
                if (!NavGraph.EdgeRoutable(in edge, in query))
                    continue;

                var key = NavGraph.EdgeKey(u, edge.To);

                if (blocked.Contains(key))
                    continue;

                var cost = edge.Cost * (1 + (BotConstants.OccupancyCost * occupancy.GetValueOrDefault(key)));
                var via = du + cost;

                if (via < dist[edge.To])
                {
                    dist[edge.To] = via;
                    open.Enqueue(edge.To, via);
                }
            }
        }

        return dist[goal];
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
