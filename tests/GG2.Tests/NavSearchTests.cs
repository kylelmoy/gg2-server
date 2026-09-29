using GG2.Bots;
using GG2.Protocol;

namespace GG2.Tests;

/// <summary>
/// A* over the nav graph — <c>Scripts/BotNav/navFindPath.gml</c>.
/// </summary>
/// <remarks>
/// The graph fixtures here are hand-built, because every property worth
/// asserting about a search is about a shape you have to <em>construct</em>: a
/// cheap route that only appears after a node is closed, a gate that refuses one
/// team and not the other, an arc only a scout can fly. Real maps have all of
/// those somewhere and none of them at a coordinate a test can name.
/// <see cref="NavSearchFixtureTests"/> runs the same search over the shipped
/// graphs, which is where "it does not fall over on real geometry" is answered.
/// </remarks>
public sealed class NavSearchTests
{
    /// <summary>row, x0, x1, flags, door, gate.</summary>
    private static double[] Node(int row, int x0, int x1, NavGate gate = NavGate.None) =>
        [row, x0, x1, 0, 0, (double)gate];

    /// <summary>from, to, type, bucket, ticks, cost, gate, takeoff, rejump.</summary>
    private static double[] Edge(
        int from, int to, double cost,
        NavEdgeType type = NavEdgeType.Walk, NavGate gate = NavGate.None) =>
        [from, to, (double)type, 0, cost, cost, (double)gate, 0, 0];

    /// <summary>
    /// Builds a graph from node and edge rows.
    /// </summary>
    /// <remarks>
    /// The edges are sorted by from-node first, because the edge index assumes
    /// it and the cache guarantees it — a fixture that forgot would silently
    /// give a node a short neighbour list and every assertion here would be
    /// about the wrong graph.
    /// </remarks>
    private static NavGraph Build(double[][] nodes, double[][] edges, bool admissible = true)
    {
        var sorted = edges.OrderBy(e => e[NavConstants.EdgeFrom]).ToArray();
        var graph = NavCache.Parse(NavCacheTests.WriteCache(400, 200, nodes, sorted), "fixture", 1);

        if (admissible)
            AssertAdmissible(graph);

        return graph;
    }

    /// <summary>
    /// Every edge costs at least the straight line it covers.
    /// </summary>
    /// <remarks>
    /// <b>The property the heuristic's admissibility rests on, asserted about
    /// the fixtures rather than assumed.</b> <c>navFindPath.gml</c>: "the
    /// heuristic is straight-line distance between surface midpoints in mask
    /// cells; walk cost is also measured in cells, so the heuristic never
    /// overestimates a walk and A* stays admissible. Fall edges are charged their
    /// drop height, which is at least the straight-line distance they cover
    /// vertically, so they do not break it either."
    /// <para>
    /// It is checked here because a fixture that violates it is not a small
    /// fixture — it is a <em>different search</em>. A cheap edge to a distant
    /// node makes the heuristic overestimate, A* stops being optimal, and a test
    /// asserting "the cheapest route wins" then passes or fails for reasons that
    /// have nothing to do with the code. That is exactly what the occupancy
    /// fixture did on its first draft.
    /// </para>
    /// </remarks>
    private static void AssertAdmissible(NavGraph graph)
    {
        foreach (var edge in graph.Edges)
        {
            var from = graph.Nodes[edge.From];
            var to = graph.Nodes[edge.To];

            var length = Gml.PointDistance(
                (from.X0 + from.X1) / 2.0, from.Row,
                (to.X0 + to.X1) / 2.0, to.Row);

            Assert.True(
                edge.Cost >= length,
                $"edge {edge.From}->{edge.To} costs {edge.Cost} to cover {length:0.##} cells, "
                + "so the heuristic overestimates and this fixture is not the search under test");
        }
    }

    private static List<int> Route(
        NavGraph graph, int start, int goal, NavQuery query,
        IReadOnlySet<long>? blocked = null,
        IReadOnlyDictionary<long, int>? occupancy = null,
        NavDistances? distances = null)
    {
        var path = new List<int>();
        new NavSearch(graph).Find(start, goal, query, path, blocked, occupancy, default, distances);
        return path;
    }

    /// <summary>A route runs start to goal inclusive, along real edges.</summary>
    [Fact]
    public void ARouteRunsStartToGoalAlongRealEdges()
    {
        var graph = Build(
            [Node(10, 0, 4), Node(10, 6, 10), Node(10, 12, 16)],
            [Edge(0, 1, 6), Edge(1, 2, 6)]);

        Assert.Equal([0, 1, 2], Route(graph, 0, 2, new NavQuery(Team.Red)));

        // The degenerate case is a route of one, not an empty one: a bot already
        // standing on its goal has arrived, and an empty list would read as "no
        // route" to every caller.
        Assert.Equal([1], Route(graph, 1, 1, new NavQuery(Team.Red)));

        // No route back: the edges are directed.
        Assert.Empty(Route(graph, 2, 0, new NavQuery(Team.Red)));
    }

    /// <summary>
    /// The search returns the <em>cheapest</em> route, not the shortest one in
    /// hops.
    /// </summary>
    /// <remarks>
    /// The fixture makes the two disagree on purpose: one hop at 50 against
    /// three at 10. A greedy or breadth-first search answers [0, 3] and passes
    /// nothing here.
    /// </remarks>
    [Fact]
    public void TheCheapestRouteWinsOverTheFewestHops()
    {
        var graph = Build(
            [Node(10, 0, 4), Node(10, 6, 10), Node(10, 12, 16), Node(10, 18, 22)],
            [Edge(0, 3, 50), Edge(0, 1, 10), Edge(1, 2, 10), Edge(2, 3, 10)]);

        Assert.Equal([0, 1, 2, 3], Route(graph, 0, 3, new NavQuery(Team.Red)));
    }

    /// <summary>
    /// A closed node is re-opened when a cheaper route to it turns up.
    /// </summary>
    /// <remarks>
    /// <b>This is what makes the search optimal rather than merely fast.</b>
    /// Skipping an already-closed neighbour is only sound when the heuristic is
    /// <em>consistent</em>, which is strictly stronger than the admissibility
    /// straight-line distance gives: on honest costs the re-open shortens the
    /// ctf_truefort blue leg from 68 nodes to 60.
    /// <para>
    /// <b>The fixture has to force the re-open to actually fire, and the first
    /// draft of this test did not.</b> It is not enough that a cheaper route
    /// exists — it has to arrive at a node that is <em>already closed</em>, which
    /// needs three things at once: N reached expensively but popped early
    /// (because it is near the goal), M reached cheaply but popped late (because
    /// it is far from the goal), and the goal expensive enough from N that M pops
    /// before it. Get any one wrong and the search happens to take the right
    /// route anyway, and the test passes with the re-open deleted. Checked by
    /// mutation, which is the only way to know.
    /// </para>
    /// <para>
    /// The map, with the goal at x = 0 and every node on one row:
    /// <code>
    ///   start --40--> N (x=2)  --100--> goal (x=0)
    ///     |                ^
    ///      --1--> M (x=100) --1--
    /// </code>
    /// N pops at f = 40 + 2 = 42 and closes; the goal is pushed at 140; M pops at
    /// f = 1 + 100 = 101, which is still under 140, and re-prices N at 2. With
    /// the re-open the answer is start-M-N-goal at 102; without it, start-N-goal
    /// at 140.
    /// </para>
    /// </remarks>
    [Fact]
    public void AClosedNodeIsNotReopenedAndTheRouteIsTheWorseOne()
    {
        // `admissible: false` on purpose, and it is the point of the fixture.
        // A heuristic that never overestimates is not enough to make skipping a
        // closed node unsound — that needs *consistency*, which is strictly
        // stronger — and the cheapest way to construct an inconsistent one is a
        // cheap edge to a distant node. On a real graph the same effect arrives
        // through `occupancy`, which perturbs costs deliberately and makes the
        // two properties come apart for real.
        var graph = Build(
            admissible: false,
            nodes:
            [
                Node(10, 200, 200), // 0: start, out of the way
                Node(10, 2, 2),     // 1: N, beside the goal
                Node(10, 100, 100), // 2: M, far from the goal
                Node(10, 0, 0),     // 3: goal
            ],
            edges:
            [
                Edge(0, 1, 40),  // N the expensive way, but it pops first
                Edge(0, 2, 1),   // M cheaply, but it pops late
                Edge(2, 1, 1),   // ...and re-prices N at 2, after N has closed
                Edge(1, 3, 100), // the only way home
            ]);

        var search = new NavSearch(graph);
        var path = new List<int>();

        Assert.True(search.Find(0, 3, new NavQuery(Team.Red), path));

        // [0, 2, 1, 3] costs 102 and is the OPTIMAL route. This search returns
        // [0, 1, 3], which costs 140 — 37% worse — and that is the deliberate
        // trade, not a bug. N is closed at cost 40 before M re-prices it at 2,
        // and a closed node is never revisited.
        //
        // The cost of the trade is worth having written down somewhere it
        // cannot rot: an adversarial graph is where the loss is largest, and on
        // the 25 shipped graphs the same change costs 0.6% of route cost on
        // average. NavConstants.SearchWeight carries the whole measurement and
        // the reason.
        Assert.Equal([0, 1, 3], path);
    }

    /// <summary>
    /// A gate refuses one team's search and not the other's, on the same graph.
    /// </summary>
    /// <remarks>
    /// The property the whole nav layer is built on: one edge set serves every
    /// bot, and who is asking is applied per query. <b>And carrying the intel
    /// closes your own team's gate</b>, which is why a CTF return leg is a
    /// different query from the outbound one.
    /// </remarks>
    [Fact]
    public void AGateAnswersDifferentlyPerAskerOnOneGraph()
    {
        // Three nodes in a line and one below them: the direct pair costs 5 a
        // hop, the detour 40, and every edge costs at least the distance it
        // covers.
        var graph = Build(
            [Node(10, 0, 0), Node(10, 5, 5), Node(10, 10, 10), Node(40, 5, 5)],
            [
                Edge(0, 1, 5, gate: NavGate.TeamRed),  // through red's own gate
                Edge(1, 2, 5),
                Edge(0, 3, 40),                        // the long way round
                Edge(3, 2, 40),
            ]);

        Assert.Equal([0, 1, 2], Route(graph, 0, 2, new NavQuery(Team.Red)));
        Assert.Equal([0, 3, 2], Route(graph, 0, 2, new NavQuery(Team.Blue)));

        // Red carrying the intel is refused its own gate and takes blue's
        // route.
        Assert.Equal([0, 3, 2], Route(graph, 0, 2, new NavQuery(Team.Red, HasIntel: true)));
    }

    /// <summary>
    /// A class-specific arc is refused unless the asker says it can fly it, and
    /// refusing is the default.
    /// </summary>
    /// <remarks>
    /// <b>The default matters more than the flag.</b> "The failure mode of the
    /// opposite default is a Heavy routed over an arc it physically cannot fly",
    /// and a caller that has not been taught about these — a hand-issued query, a
    /// test — gets the conservative answer.
    /// </remarks>
    [Theory]
    [InlineData(NavEdgeType.DoubleJump)]
    [InlineData(NavEdgeType.RocketJump)]
    public void AClassArcIsRefusedByDefault(NavEdgeType type)
    {
        var graph = Build(
            [Node(10, 0, 0), Node(10, 5, 5), Node(40, 0, 0)],
            [Edge(0, 1, 5, type), Edge(0, 2, 30), Edge(2, 1, 40)]);

        // The default query cannot fly it.
        Assert.Equal([0, 2, 1], Route(graph, 0, 1, new NavQuery(Team.Red)));

        var able = type == NavEdgeType.DoubleJump
            ? new NavQuery(Team.Red, CanDoubleJump: true)
            : new NavQuery(Team.Red, CanRocketJump: true);

        Assert.Equal([0, 1], Route(graph, 0, 1, able));

        // And the flags are not interchangeable: a scout's second jump does not
        // buy it a rocket jump.
        var wrongFlag = type == NavEdgeType.DoubleJump
            ? new NavQuery(Team.Red, CanRocketJump: true)
            : new NavQuery(Team.Red, CanDoubleJump: true);

        Assert.Equal([0, 2, 1], Route(graph, 0, 1, wrongFlag));
    }

    /// <summary>
    /// A rocket jump is refused to a bot that cannot survive one, and the test
    /// is strictly greater.
    /// </summary>
    /// <remarks>
    /// Not <c>&gt;=</c>: the damage is dealt before the bot lands, so equal
    /// health is a bot that dies in mid-air on an edge it was told to fly. There
    /// is no margin beyond that on purpose — "a rocket jump taken at 31hp is a
    /// real thing a player does, and the point of these edges is a bot that
    /// reads like a player".
    /// </remarks>
    [Fact]
    public void ARocketJumpNeedsMoreHealthThanItCosts()
    {
        Assert.False(NavQuery.CanAffordRocketJump(NavConstants.RjDamage - 1));
        Assert.False(NavQuery.CanAffordRocketJump(NavConstants.RjDamage));
        Assert.True(NavQuery.CanAffordRocketJump(NavConstants.RjDamage + 1));
    }

    /// <summary>
    /// A blacklisted edge is refused, and the route goes round it.
    /// </summary>
    /// <remarks>
    /// <b>The anti-thrash half of the follower.</b> Without it a bot that
    /// wedges in a doorway re-plans, is handed the same route it just failed, and
    /// wedges again for ever. It is a <em>set</em> rather than a cost multiplier
    /// because a multiplier would break the heuristic's admissibility.
    /// </remarks>
    [Fact]
    public void ABlacklistedEdgeIsRefused()
    {
        var graph = Build(
            [Node(10, 0, 0), Node(10, 5, 5), Node(40, 0, 0)],
            [Edge(0, 1, 5), Edge(0, 2, 30), Edge(2, 1, 40)]);

        Assert.Equal([0, 1], Route(graph, 0, 1, new NavQuery(Team.Red)));

        var blocked = new HashSet<long> { NavGraph.EdgeKey(0, 1) };
        Assert.Equal([0, 2, 1], Route(graph, 0, 1, new NavQuery(Team.Red), blocked));

        // Directed: blacklisting 0->1 says nothing about 1->0. The key packs
        // the endpoints in order, and a follower that failed one direction has
        // learnt nothing about the other.
        Assert.NotEqual(NavGraph.EdgeKey(0, 1), NavGraph.EdgeKey(1, 0));
    }

    /// <summary>
    /// An edge team-mates are already on costs more, and enough of them move the
    /// route.
    /// </summary>
    /// <remarks>
    /// The rate is <c>1 + BOT_OCCUPANCY_COST × count</c>. <b>A per-edge random
    /// jitter is not a substitute</b>: it is
    /// mean-reverting, so over a long route every candidate inflates by about the
    /// same average and the ordering barely moves — on ctf_truefort all eight
    /// bots got the identical 68-node route. Occupancy works because it is
    /// structured, shared, and coarse.
    /// </remarks>
    [Fact]
    public void ACrowdedEdgeCostsMoreAndEnoughOfACrowdMovesTheRoute()
    {
        // A ten-cell hop against a two-leg detour through the node halfway
        // between: 6.5 + 6.5 = 13, and each leg covers five cells, so the
        // heuristic still never overestimates.
        var graph = Build(
            [Node(10, 0, 0), Node(10, 10, 10), Node(10, 5, 5)],
            [Edge(0, 1, 10), Edge(0, 2, 6.5), Edge(2, 1, 6.5)]);

        var query = new NavQuery(Team.Red);
        Assert.Equal([0, 1], Route(graph, 0, 1, query));

        // The direct edge is 10 and the detour 13. One team-mate takes the
        // direct edge to 12.5 — still cheaper, so the route holds.
        var one = new Dictionary<long, int> { [NavGraph.EdgeKey(0, 1)] = 1 };
        Assert.Equal([0, 1], Route(graph, 0, 1, query, occupancy: one));

        // Two takes it to 15, and the crowd finally moves the route.
        var two = new Dictionary<long, int> { [NavGraph.EdgeKey(0, 1)] = 2 };
        Assert.Equal([0, 2, 1], Route(graph, 0, 1, query, occupancy: two));
    }

    // --- CPD-Search: the table's chain as the answer, verified ---------------
    //
    // The same fixtures as above, asked with a table. Each one perturbs the
    // stored chain a different way — nothing, a ban, a crowd, a gate, a class
    // arc — and asserts the search still answers what it answered without the
    // table, which is the claim: the table changes the cost of the search, not
    // what comes back. docs/TRAPS-BOTS.md, "CPD-Search".

    /// <summary>
    /// A chain nothing has perturbed comes back at the first pop.
    /// </summary>
    /// <remarks>
    /// <c>Pops == 1</c> is the whole point: the start node is popped, its
    /// lower bound meets the incumbent's live price, and the chain is returned
    /// with no node expanded. A search that expanded the chain to "verify" it
    /// would answer the same route at four pops and would have missed the idea.
    /// </remarks>
    [Fact]
    public void CpdACleanChainIsReturnedWithoutExpandingANode()
    {
        var graph = Build(
            [Node(10, 0, 4), Node(10, 6, 10), Node(10, 12, 16), Node(10, 18, 22)],
            [Edge(0, 3, 50), Edge(0, 1, 10), Edge(1, 2, 10), Edge(2, 3, 10)]);

        var table = NavDistances.Derive(graph);
        var search = new NavSearch(graph);
        var path = new List<int>();

        Assert.True(search.Find(0, 3, new NavQuery(Team.Red), path, distances: table));
        Assert.Equal([0, 1, 2, 3], path);
        Assert.Equal(1, search.Pops);
    }

    /// <summary>
    /// A blacklisted hop on the chain prices it at infinity, the search runs,
    /// and it routes round.
    /// </summary>
    [Fact]
    public void CpdABlacklistedHopOnTheChainForcesTheSearchRoundIt()
    {
        var graph = Build(
            [Node(10, 0, 0), Node(10, 5, 5), Node(40, 0, 0)],
            [Edge(0, 1, 5), Edge(0, 2, 30), Edge(2, 1, 40)]);

        var table = NavDistances.Derive(graph);
        var search = new NavSearch(graph);
        var path = new List<int>();
        var blocked = new HashSet<long> { NavGraph.EdgeKey(0, 1) };

        Assert.True(search.Find(0, 1, new NavQuery(Team.Red), path, blocked, distances: table));
        Assert.Equal([0, 2, 1], path);
        Assert.True(search.Pops > 1, "a dirty chain has to be searched past");
    }

    /// <summary>
    /// A crowded hop on the chain is priced, not waved through on the
    /// incumbent — the answer is the same one the table-less search gives.
    /// </summary>
    /// <remarks>
    /// <b>This is the test a WEIGHTED incumbent test fails.</b> With three on
    /// the direct hop the chain prices at 17.5 against a 13 detour, and the
    /// lower bound at the start is 10: a search that accepted the incumbent once
    /// it was within <c>SearchWeight</c> (1.3 × 10 = 13 &lt; 17.5 — not quite;
    /// at 1.75 it would) of the bound would hand back the crowded route, and
    /// occupancy would stop dispersing anybody at whatever weight was chosen.
    /// One search instance for every call, so the memo's reset is exercised.
    /// </remarks>
    [Fact]
    public void CpdACrowdedHopOnTheChainStillMovesTheRoute()
    {
        var graph = Build(
            [Node(10, 0, 0), Node(10, 10, 10), Node(10, 5, 5)],
            [Edge(0, 1, 10), Edge(0, 2, 6.5), Edge(2, 1, 6.5)]);

        var table = NavDistances.Derive(graph);
        var search = new NavSearch(graph);
        var query = new NavQuery(Team.Red);
        var path = new List<int>();

        var one = new Dictionary<long, int> { [NavGraph.EdgeKey(0, 1)] = 1 };
        Assert.True(search.Find(0, 1, query, path, null, one, default, table));
        Assert.Equal([0, 1], path);

        var three = new Dictionary<long, int> { [NavGraph.EdgeKey(0, 1)] = 3 };
        Assert.True(search.Find(0, 1, query, path, null, three, default, table));
        Assert.Equal([0, 2, 1], path);

        // And back again: the memo from the crowded search must not linger.
        Assert.True(search.Find(0, 1, query, path, null, one, default, table));
        Assert.Equal([0, 1], path);
    }

    /// <summary>
    /// One table built with every gate open serves both teams: the asker the
    /// gate refuses gets the detour, not the chain.
    /// </summary>
    [Fact]
    public void CpdAGateOnTheChainIsRefusedPerAskerFromOneTable()
    {
        var graph = Build(
            [Node(10, 0, 0), Node(10, 5, 5), Node(10, 10, 10), Node(40, 5, 5)],
            [
                Edge(0, 1, 5, gate: NavGate.TeamRed),
                Edge(1, 2, 5),
                Edge(0, 3, 40),
                Edge(3, 2, 40),
            ]);

        var table = NavDistances.Derive(graph);

        Assert.Equal([0, 1, 2], Route(graph, 0, 2, new NavQuery(Team.Red), distances: table));
        Assert.Equal([0, 3, 2], Route(graph, 0, 2, new NavQuery(Team.Blue), distances: table));
        Assert.Equal([0, 3, 2], Route(graph, 0, 2, new NavQuery(Team.Red, HasIntel: true), distances: table));
    }

    /// <summary>
    /// A class arc on the chain is refused to an asker that cannot fly it, from
    /// the permissive table and from the matched one alike.
    /// </summary>
    /// <remarks>
    /// The permissive table is SAFE for a walker — the chain prices at infinity
    /// and the search goes round — it is only loose. The matched table's chain
    /// is the detour itself, so the walker gets it at the first pop. Both
    /// answers are the same route; what differs is the work.
    /// </remarks>
    [Theory]
    [InlineData(NavEdgeType.DoubleJump)]
    [InlineData(NavEdgeType.RocketJump)]
    public void CpdAClassArcOnTheChainIsRefusedToAWalkerFromEitherTable(NavEdgeType type)
    {
        var graph = Build(
            [Node(10, 0, 0), Node(10, 5, 5), Node(40, 0, 0)],
            [Edge(0, 1, 5, type), Edge(0, 2, 30), Edge(2, 1, 40)]);

        var permissive = NavDistances.Derive(graph);
        var walker = NavDistances.Derive(graph, false, false, false);
        var search = new NavSearch(graph);
        var path = new List<int>();

        Assert.True(search.Find(0, 1, new NavQuery(Team.Red), path, distances: permissive));
        Assert.Equal([0, 2, 1], path);
        Assert.True(search.Pops > 1);

        Assert.True(search.Find(0, 1, new NavQuery(Team.Red), path, distances: walker));
        Assert.Equal([0, 2, 1], path);
        Assert.Equal(1, search.Pops);

        var able = type == NavEdgeType.DoubleJump
            ? new NavQuery(Team.Red, CanDoubleJump: true)
            : new NavQuery(Team.Red, CanRocketJump: true);

        Assert.True(search.Find(0, 1, able, path, distances: permissive));
        Assert.Equal([0, 1], path);
        Assert.Equal(1, search.Pops);
    }

    /// <summary>
    /// An unreachable goal is no route, not a partial one.
    /// </summary>
    /// <remarks>
    /// A follower handed a route that does not reach the goal walks confidently
    /// to the wrong place, which is worse than standing still — and the
    /// reconstruct's own guard says the same thing about a route that does not
    /// start where the caller is.
    /// </remarks>
    [Fact]
    public void AnUnreachableGoalIsNoRoute()
    {
        var graph = Build(
            [Node(10, 0, 4), Node(10, 6, 10), Node(10, 200, 204)],
            [Edge(0, 1, 6)]);

        var path = new List<int> { 99, 98 }; // pre-dirtied: it must be cleared
        Assert.False(new NavSearch(graph).Find(0, 2, new NavQuery(Team.Red), path));
        Assert.Empty(path);

        // Out of range is refused rather than throwing: start and goal come from
        // a snap, which answers -1 when a position is in the void.
        Assert.False(new NavSearch(graph).Find(-1, 1, new NavQuery(Team.Red), path));
        Assert.False(new NavSearch(graph).Find(0, 99, new NavQuery(Team.Red), path));
    }

    /// <summary>
    /// One search instance reused gives the same answers as fresh ones.
    /// </summary>
    /// <remarks>
    /// The scratch arrays are sized to the graph and cleared per search rather
    /// than reallocated, which is the whole reason this is an instance. A
    /// missing clear does not throw — it leaks the previous frontier into the
    /// next answer, and the symptom is a route that was correct the first time a
    /// bot asked and wrong afterwards.
    /// </remarks>
    [Fact]
    public void AReusedSearchAgreesWithAFreshOne()
    {
        var graph = Build(
            [Node(10, 0, 0), Node(10, 5, 5), Node(10, 10, 10), Node(40, 5, 5)],
            [Edge(0, 1, 5), Edge(1, 2, 5), Edge(0, 3, 40), Edge(3, 2, 40), Edge(2, 3, 40)]);

        var shared = new NavSearch(graph);
        var reused = new List<int>();
        var query = new NavQuery(Team.Red);

        foreach (var (start, goal) in new[] { (0, 2), (0, 3), (1, 3), (0, 2), (3, 2) })
        {
            shared.Find(start, goal, query, reused);

            var fresh = new List<int>();
            new NavSearch(graph).Find(start, goal, query, fresh);

            Assert.Equal(fresh, reused);
        }
    }
}
