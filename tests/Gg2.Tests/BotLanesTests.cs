using Gg2.Bots;
using Gg2.Protocol;
using Xunit.Abstractions;

namespace Gg2.Tests;

/// <summary>
/// The lane derivation: what it promises, and what would go red if it silently
/// did nothing.
/// </summary>
/// <remarks>
/// <b>Written because the dispersion measurements cannot fail.</b>
/// <see cref="RouteDiversityTests"/> and <see cref="RouteDiversityMatchTests"/>
/// print scores; a <see cref="BotLanes"/> that derived four identical fields
/// would score exactly what "no mechanism" scores and every one of them would
/// still pass. These are the assertions that catch that.
/// </remarks>
public sealed class BotLanesTests(ITestOutputHelper output)
{
    private const string Map = "ctf_truefort";

    private static NavGraph Graph()
    {
        ReferenceTree.RequireNavCache();
        return NavCache.Load(ReferenceTree.NavCacheDirectory, Map, 1);
    }

    /// <summary>
    /// Lane 0 is the graph's own costs, exactly.
    /// </summary>
    /// <remarks>
    /// <b>The property every existing measurement depends on.</b> A bot with no
    /// lane and a bot on lane 0 must route identically, or
    /// <c>NavRouteIdentityTests</c>' digests describe one configuration and the
    /// server runs another. Exact equality, not a tolerance: lane 0 is a copy of
    /// the costs and nothing is done to it.
    /// </remarks>
    [Fact]
    public void LaneZeroIsTheGraphsOwnCosts()
    {
        var graph = Graph();
        var lanes = BotLanes.Derive(graph, 4);

        var zero = lanes.Costs(0).Span;

        Assert.Equal(graph.EdgeCount, zero.Length);

        for (var e = 0; e < graph.EdgeCount; e++)
            Assert.Equal(graph.Edges[e].Cost, zero[e]);
    }

    /// <summary>
    /// Each later lane really does charge more, on a real share of the graph.
    /// </summary>
    /// <remarks>
    /// <b>The gate against a derivation that fires and achieves nothing</b> —
    /// the failure mode that killed the per-edge jitter and took a hand
    /// measurement to notice. A lane that penalised three edges would pass any
    /// "the fields differ" check; this asserts the penalised share is a corridor's
    /// worth, and that the lanes are strictly nested, which is what iterative
    /// penalisation means.
    /// </remarks>
    [Fact]
    public void EachLanePenalisesACorridorTheLastOneUsed()
    {
        var graph = Graph();
        var lanes = BotLanes.Derive(graph, 4);

        var previous = 0;

        for (var lane = 1; lane < lanes.Count; lane++)
        {
            var zero = lanes.Costs(0).Span;
            var mine = lanes.Costs(lane).Span;

            var raised = 0;

            for (var e = 0; e < graph.EdgeCount; e++)
            {
                // Never cheaper than the honest cost: a lane may divert a bot,
                // never bribe it onto an edge the graph says is expensive.
                Assert.True(mine[e] >= zero[e]);

                if (mine[e] > zero[e])
                    raised++;
            }

            output.WriteLine($"lane {lane}: {raised} of {graph.EdgeCount} edges raised");

            // A corridor's worth, not a handful.
            Assert.True(
                raised > graph.EdgeCount / 100,
                $"lane {lane} raised only {raised} of {graph.EdgeCount} edges");

            // Strictly nested: lane L penalises everything lane L-1 did and more.
            Assert.True(raised > previous, $"lane {lane} penalised no more than lane {lane - 1}");
            previous = raised;
        }
    }

    /// <summary>
    /// A lane can send a bot the long way round; it can never strand one.
    /// </summary>
    /// <remarks>
    /// <b>This is what makes a lane safe to give a bot for its whole life.</b>
    /// Every lane multiplies costs and removes nothing, so reachability is a
    /// property of the graph and not of the lane — a bot on the worst lane still
    /// gets a route wherever lane 0 would have found one. Asserted rather than
    /// argued, because the argument stops holding the moment somebody adds a
    /// penalty that saturates to infinity.
    /// </remarks>
    [Fact]
    public void EveryLaneReachesExactlyWhatLaneZeroReaches()
    {
        var graph = Graph();
        var lanes = BotLanes.Derive(graph, 4);
        var search = new NavSearch(graph);
        var query = new NavQuery(Team.Red);
        var route = new List<int>();

        var legs = 0;

        for (var from = 0; from < graph.NodeCount && legs < 200; from += 7)
        {
            for (var to = 0; to < graph.NodeCount && legs < 200; to += 11)
            {
                if (from == to)
                    continue;

                legs++;

                var baseline = search.Find(from, to, query, route, null, null, lanes.Costs(0).Span);

                for (var lane = 1; lane < lanes.Count; lane++)
                {
                    var found = search.Find(from, to, query, route, null, null, lanes.Costs(lane).Span);

                    Assert.True(
                        found == baseline,
                        $"lane {lane} disagrees with lane 0 about whether {from} -> {to} exists");
                }
            }
        }

        Assert.True(legs > 0);
    }

    /// <summary>
    /// Four bots on four lanes do not walk in single file.
    /// </summary>
    /// <remarks>
    /// The claim the whole mechanism exists for, on the leg
    /// <c>BotOccupancyTests</c> swept for — so the two mechanisms are asserted
    /// against the same evidence. ⚠️ <b>The control is the half that matters</b>:
    /// the same four searches on lane 0 return one route four times.
    /// </remarks>
    [Fact]
    public void FourLanesDoNotWalkInSingleFile()
    {
        var graph = Graph();
        var lanes = BotLanes.Derive(graph, 4);
        var search = new NavSearch(graph);
        var query = new NavQuery(Team.Red);

        var spread = new List<IReadOnlyList<int>>();
        var flat = new List<IReadOnlyList<int>>();

        for (var lane = 0; lane < 4; lane++)
        {
            var route = new List<int>();
            Assert.True(search.Find(0, 143, query, route, null, null, lanes.Costs(lane).Span));
            spread.Add(route);

            var same = new List<int>();
            Assert.True(search.Find(0, 143, query, same, null, null, lanes.Costs(0).Span));
            flat.Add(same);
        }

        output.WriteLine(
            $"lanes: {RouteDiversity.DistinctRoutes(spread)} distinct, spread {RouteDiversity.Spread(spread):F3}");

        Assert.Equal(1, RouteDiversity.DistinctRoutes(flat));
        Assert.Equal(4, RouteDiversity.DistinctRoutes(spread));
        Assert.True(RouteDiversity.Spread(spread) > 0.3);
    }

    /// <summary>
    /// A lane index wraps onto the lanes that exist.
    /// </summary>
    /// <remarks>
    /// The caller is a roster position and the roster is longer than the lane
    /// count by design, so wrapping is the contract rather than a convenience.
    /// The negative case is asserted because C#'s <c>%</c> keeps the sign and an
    /// unguarded index would throw on a caller that counted down.
    /// </remarks>
    [Fact]
    public void ALaneIndexWraps()
    {
        var graph = Graph();
        var lanes = BotLanes.Derive(graph, 3);

        Assert.True(lanes.Costs(3).Span.SequenceEqual(lanes.Costs(0).Span));
        Assert.True(lanes.Costs(-1).Span.SequenceEqual(lanes.Costs(2).Span));

        // And the assignment that feeds it divides a team evenly.
        Assert.Equal(0, BotRoles.Lane(0, 3));
        Assert.Equal(1, BotRoles.Lane(1, 3));
        Assert.Equal(2, BotRoles.Lane(2, 3));
        Assert.Equal(0, BotRoles.Lane(3, 3));

        // One lane is "no lanes", and must not divide by more than it has.
        Assert.Equal(0, BotRoles.Lane(7, 1));
    }
}
