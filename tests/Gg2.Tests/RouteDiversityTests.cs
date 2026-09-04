using Gg2.Bots;
using Gg2.Protocol;
using Xunit.Abstractions;

namespace Gg2.Tests;

/// <summary>
/// What edge occupancy actually achieves, scored rather than asserted.
/// </summary>
/// <remarks>
/// <b>Written before the numbers existed, and the predictions below are part of
/// the test.</b> This repo's own rule: only a pre-registered test is defensible
/// unqualified, and a p-value on a post-hoc quantity prints identically to one
/// on a real prediction. The point of scoring occupancy is that a replacement is
/// being considered — moving route variety out of the search's cost function and
/// into the scripted layer, so that a precomputed route table becomes usable —
/// and there has never been an instrument that could say whether a replacement
/// matched.
/// <para>
/// <b>The three outcomes this was written to distinguish, in advance:</b>
/// </para>
/// <list type="number">
/// <item>
/// <b>The control must be exactly 0.</b> With no occupancy map, four bots
/// asking for the same leg must return the identical route four times, on every
/// leg of every graph. Anything else means the search is not a pure function of
/// (graph, query, leg) and every measurement in this file — and every route
/// digest in <see cref="NavRouteIdentityTests"/> — is measuring noise. This is
/// the assertion, and it is the one that could genuinely fire.
/// </item>
/// <item>
/// <b>Predicted: occupancy's mean spread over swept legs is LOW — under 0.2.</b>
/// <c>BotOccupancyTests</c> had to <em>sweep</em> for its fixture because "most
/// legs have one sensible route and a 25% penalty on a few edges doesn't move
/// them". If that is right, occupancy moves a minority of legs and does nothing
/// on the rest, and the bar a lane assignment has to clear is correspondingly
/// low. <b>This is the outcome that makes the replacement attractive.</b>
/// </item>
/// <item>
/// <b>The alternative: mean spread is HIGH — over 0.5.</b> Then occupancy is
/// dispersing nearly every leg, a static lane assignment would have to reproduce
/// that across the whole graph rather than at a few branch points, and the
/// replacement needs real lane derivation rather than a handful of via-nodes.
/// </item>
/// </list>
/// <para>
/// ⚠️ <b>A high score is not a good score and this file cannot tell the
/// difference.</b> <see cref="RouteDiversity"/> measures dispersion only; four
/// bots dispersed down four bad corridors score 1.0. Route quality lives in
/// <c>ScenarioSuiteTests</c>' walking time and the two single-leg pins, and a
/// replacement has to clear those too.
/// </para>
/// </remarks>
public sealed class RouteDiversityTests(ITestOutputHelper output)
{
    /// <summary>Bots asking in turn for the same leg, as a team's planners do.</summary>
    private const int Askers = 4;

    /// <summary>
    /// How many legs are scored per graph.
    /// </summary>
    /// <remarks>
    /// A deterministic stride over the node index rather than a sample, so the
    /// set of legs is a property of the graph and not of a seed — the same reason
    /// <see cref="NavRouteIdentityTests"/> walks pairs rather than drawing them.
    /// Capped because the full cross product is millions of searches and this is
    /// a checked-in test, not a bench run.
    /// </remarks>
    private const int LegsPerGraph = 400;

    /// <summary>
    /// Four bots asking in turn, with a shared occupancy map and without.
    /// </summary>
    private static (double Spread, int Distinct) Ask(
        NavGraph graph, NavSearch search, in NavQuery query, int from, int to, bool crowded)
    {
        var shared = crowded ? new BotOccupancy() : null;
        var routes = new List<IReadOnlyList<int>>(Askers);

        for (var bot = 0; bot < Askers; bot++)
        {
            var route = new List<int>();

            if (!search.Find(from, to, query, route, null, shared?.Counts(query.Team)))
                return (0, 0);

            routes.Add(route);

            if (shared is not null)
            {
                var state = new BotPathState();
                state.Path.AddRange(route);
                shared.Add(state, query.Team);
            }
        }

        return (RouteDiversity.Spread(routes), RouteDiversity.DistinctRoutes(routes));
    }

    /// <summary>
    /// The recorded measurement, reproduced by the new statistic.
    /// </summary>
    /// <remarks>
    /// <b>A new instrument that cannot reproduce the number the old one recorded
    /// is a new instrument nobody should trust.</b> <c>docs/TRAPS-BOTS.md</c>,
    /// phase 7 item 4q: on <c>ctf_truefort</c> node 0 → 143, four bots asking in
    /// turn get <b>four distinct routes with occupancy and one without</b>.
    /// <para>
    /// This pins the four that <c>BotOccupancyTests</c> only asserts is "more
    /// than one" — a gate that passes at two of four, which is most of the
    /// mechanism failing.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheStatisticReproducesTheRecordedMeasurement()
    {
        ReferenceTree.RequireNavCache();

        var graph = NavCache.Load(ReferenceTree.NavCacheDirectory, "ctf_truefort", 1);
        var search = new NavSearch(graph);
        var query = new NavQuery(Team.Red);

        var crowded = Ask(graph, search, in query, 0, 143, crowded: true);
        var plain = Ask(graph, search, in query, 0, 143, crowded: false);

        output.WriteLine($"crowded: spread {crowded.Spread:F3}, {crowded.Distinct} distinct");
        output.WriteLine($"plain:   spread {plain.Spread:F3}, {plain.Distinct} distinct");

        Assert.Equal(4, crowded.Distinct);
        Assert.Equal(1, plain.Distinct);
        Assert.Equal(0, plain.Spread);
        Assert.True(crowded.Spread > 0, "the recorded leg dispersed nothing");
    }

    /// <summary>
    /// Occupancy's scorecard across every shipped graph, and the control.
    /// </summary>
    /// <remarks>
    /// The control — outcome 1 above — is what is asserted: without an occupancy
    /// map every leg scores exactly 0, because the search is a pure function of
    /// the leg. The scorecard itself is printed rather than pinned <b>for
    /// now</b>: pinning it before the replacement exists would only record the
    /// incumbent, and the number worth pinning is the comparison.
    /// </remarks>
    [Fact]
    public void OccupancyScorecardAcrossTheShippedGraphs()
    {
        ReferenceTree.RequireNavCache();

        var graphs = NavCache.List(ReferenceTree.NavCacheDirectory);
        Assert.NotEmpty(graphs);

        output.WriteLine($"{"graph",-24} {"legs",5} {"moved%",7} {"mean",6} {"mean|moved",10} {"max",6}");

        var allLegs = 0;
        var allMoved = 0;
        var allSpread = 0.0;

        foreach (var (map, area) in graphs)
        {
            var graph = NavCache.Load(ReferenceTree.NavCacheDirectory, map, area);
            var search = new NavSearch(graph);
            var query = new NavQuery(Team.Red);

            var legs = 0;
            var moved = 0;
            var sum = 0.0;
            var movedSum = 0.0;
            var max = 0.0;

            // Every seventh start against every eleventh goal — the stride the
            // occupancy fixture was swept with, so the two agree about what a
            // representative leg is.
            for (var from = 0; from < graph.NodeCount && legs < LegsPerGraph; from += 7)
            {
                for (var to = 0; to < graph.NodeCount && legs < LegsPerGraph; to += 11)
                {
                    if (from == to)
                        continue;

                    var crowded = Ask(graph, search, in query, from, to, crowded: true);
                    if (crowded.Distinct == 0)
                        continue;

                    var plain = Ask(graph, search, in query, from, to, crowded: false);

                    // The control, on every leg rather than on one.
                    Assert.Equal(0, plain.Spread);
                    Assert.Equal(1, plain.Distinct);

                    legs++;
                    sum += crowded.Spread;
                    max = Math.Max(max, crowded.Spread);

                    if (crowded.Spread > 0)
                    {
                        moved++;
                        movedSum += crowded.Spread;
                    }
                }
            }

            if (legs == 0)
                continue;

            allLegs += legs;
            allMoved += moved;
            allSpread += sum;

            output.WriteLine(
                $"{map + "_a" + area,-24} {legs,5} {100.0 * moved / legs,7:F1} {sum / legs,6:F3} "
                + $"{(moved == 0 ? 0 : movedSum / moved),10:F3} {max,6:F3}");
        }

        output.WriteLine(string.Empty);
        output.WriteLine(
            $"ALL {allLegs} legs: {100.0 * allMoved / allLegs:F1}% moved, mean spread {allSpread / allLegs:F3}");
    }

    /// <summary>
    /// Four bots on four <see cref="BotLanes"/>, against the same four under
    /// occupancy.
    /// </summary>
    /// <remarks>
    /// <b>The head-to-head the replacement has to win.</b> Same legs, same
    /// stride, same statistic: occupancy disperses by reacting to what the team
    /// is walking now, a lane by a field fixed when the graph loaded. The lane
    /// arm also reports the route-cost inflation it pays, because dispersion
    /// bought by sending bots down bad corridors is not the same purchase —
    /// <see cref="RouteDiversity"/> cannot tell the two apart and this column
    /// can.
    /// </remarks>
    [Fact]
    public void LanesAgainstOccupancyOnTheSameLegs()
    {
        ReferenceTree.RequireNavCache();

        output.WriteLine(
            $"{"graph",-24} {"legs",5} {"occupancy",10} {"lanes",8} {"lane cost",10}");

        var occAll = 0.0;
        var laneAll = 0.0;
        var inflationAll = 0.0;
        var maps = 0;

        foreach (var (map, area) in NavCache.List(ReferenceTree.NavCacheDirectory))
        {
            var graph = NavCache.Load(ReferenceTree.NavCacheDirectory, map, area);
            var search = new NavSearch(graph);
            var query = new NavQuery(Team.Red);
            var lanes = BotLanes.Derive(graph, Askers);

            var legs = 0;
            var occSum = 0.0;
            var laneSum = 0.0;
            var baseCost = 0.0;
            var laneCost = 0.0;

            for (var from = 0; from < graph.NodeCount && legs < LegsPerGraph; from += 7)
            {
                for (var to = 0; to < graph.NodeCount && legs < LegsPerGraph; to += 11)
                {
                    if (from == to)
                        continue;

                    var crowded = Ask(graph, search, in query, from, to, crowded: true);
                    if (crowded.Distinct == 0)
                        continue;

                    var routes = new List<IReadOnlyList<int>>(Askers);
                    var ok = true;

                    for (var lane = 0; lane < Askers && ok; lane++)
                    {
                        var route = new List<int>();
                        ok = search.Find(from, to, query, route, null, null, lanes.Costs(lane).Span);

                        if (ok)
                        {
                            routes.Add(route);

                            // Both priced with the graph's OWN costs: what a lane
                            // charges itself is bookkeeping, what it costs the bot
                            // is walking time.
                            var cost = TrueCost(graph, route);
                            laneCost += cost;

                            if (lane == 0)
                                baseCost += cost * Askers;
                        }
                    }

                    if (!ok)
                        continue;

                    legs++;
                    occSum += crowded.Spread;
                    laneSum += RouteDiversity.Spread(routes);
                }
            }

            if (legs == 0)
                continue;

            maps++;
            occAll += occSum / legs;
            laneAll += laneSum / legs;

            var inflation = baseCost == 0 ? 1 : laneCost / baseCost;
            inflationAll += inflation;

            output.WriteLine(
                $"{map + "_a" + area,-24} {legs,5} {occSum / legs,10:F3} {laneSum / legs,8:F3} {inflation,9:P1}");
        }

        output.WriteLine(string.Empty);
        output.WriteLine(
            $"MEAN over {maps} graphs: occupancy {occAll / maps:F3}, lanes {laneAll / maps:F3}, "
            + $"lane route cost {inflationAll / maps:P1} of lane 0's");
    }

    /// <summary>A route priced with the graph's own edge costs.</summary>
    private static double TrueCost(NavGraph graph, IReadOnlyList<int> route)
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
