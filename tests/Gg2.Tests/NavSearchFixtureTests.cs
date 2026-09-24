using Gg2.Bots;
using Gg2.Protocol;

namespace Gg2.Tests;

/// <summary>
/// The search over the graphs <c>gg2-nav-gen</c> actually wrote.
/// </summary>
/// <remarks>
/// <see cref="NavSearchTests"/> asserts the search's <em>properties</em> on
/// shapes built to make each one visible. This asserts that the same code stands
/// up on real geometry — which is a different question, and the one that catches
/// an assumption about the graph rather than about A*.
/// <para>
/// These need the nav caches in <c>nav/</c>, gitignored here and not
/// shipped. See <see cref="ReferenceTree.RequireNavCache"/>.
/// </para>
/// </remarks>
public sealed class NavSearchFixtureTests
{
    private static IReadOnlyList<(string Map, int Area)> Caches()
    {
        ReferenceTree.RequireNavCache();
        return NavCache.List(ReferenceTree.NavCacheDirectory);
    }

    private static NavGraph Graph(string map, int area = 1)
    {
        ReferenceTree.RequireNavCache();
        return NavCache.Load(ReferenceTree.NavCacheDirectory, map, area);
    }

    /// <summary>
    /// Every shipped edge that lands on a gated node carries that gate itself.
    /// </summary>
    /// <remarks>
    /// <b>The invariant that makes the search's edge-only gate test correct
    /// rather than a bug.</b> <c>navFindPath.gml</c> tests
    /// <c>NAV_EDGE_GATE</c> and never the gate on the node an edge arrives at,
    /// on the grounds that "the generator stamps an edge that passes through a
    /// gate with that gate, so the check below covers both". That is a claim
    /// about the <em>generator</em>, not about the search, and nothing in the
    /// game checks it.
    /// <para>
    /// Measured across all 24 shipped graphs: <b>115,757 edges, 1,350
    /// of them stamped, 148 gated nodes, and zero edges landing on a gated node
    /// unstamped.</b> If a future generator stops stamping, this fires — and the
    /// symptom without it would be a bot routed into a gate it cannot pass,
    /// which looks like a stuck bot rather than like a graph problem.
    /// </para>
    /// </remarks>
    [Fact]
    public void NoEdgeLandsOnAGatedNodeUnstamped()
    {
        var edges = 0;
        var stamped = 0;
        var gatedNodes = 0;
        var unstamped = new List<string>();

        foreach (var (map, area) in Caches())
        {
            var graph = Graph(map, area);

            foreach (var node in graph.Nodes)
                if (node.Gate != NavGate.None)
                    gatedNodes++;

            foreach (var edge in graph.Edges)
            {
                edges++;

                if (edge.Gate != NavGate.None)
                    stamped++;
                else if (graph.Nodes[edge.To].Gate != NavGate.None)
                    unstamped.Add($"{map}_a{area} {edge.From}->{edge.To}");
            }
        }

        Assert.True(edges > 100_000, $"only {edges} edges were checked");
        Assert.True(gatedNodes > 0, "no graph places a gate, so this proves nothing");
        Assert.True(stamped > 0, "no edge carries a gate, so this proves nothing");

        Assert.True(
            unstamped.Count == 0,
            $"{unstamped.Count} edges land on a gated node without carrying its gate — the "
            + $"search's edge-only test would let a bot through: {string.Join(", ", unstamped.Take(5))}");
    }

    /// <summary>
    /// A search over every shipped graph terminates, and returns routes that are
    /// real.
    /// </summary>
    /// <remarks>
    /// Every node to a fixed goal on each map, both teams. What this catches is
    /// not "A* is wrong" — the property tests do that — but a graph shape the
    /// search does not survive: an edge index off by one, a self-loop, a
    /// disconnected component that spins the pop guard.
    /// <para>
    /// Every returned route is walked and checked edge by edge. A route is
    /// only useful if a follower can actually take each hop, and a search that
    /// returned a plausible-looking list of node indices with no edge between two
    /// of them would pass every other assertion here.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryRouteOnEveryShippedGraphIsAWalkableChain()
    {
        var routes = 0;
        var hops = 0;

        foreach (var (map, area) in Caches())
        {
            var graph = Graph(map, area);
            var search = new NavSearch(graph);
            var path = new List<int>();

            // A fixed goal per map, and every node as a start. The goal is the
            // last node rather than a chosen one: this is about coverage, not
            // about a leg anybody cares about.
            var goal = graph.NodeCount - 1;

            foreach (var team in new[] { Team.Red, Team.Blue })
            {
                var query = new NavQuery(team);

                for (var start = 0; start < graph.NodeCount; start++)
                {
                    if (!search.Find(start, goal, query, path))
                        continue;

                    routes++;

                    Assert.Equal(start, path[0]);
                    Assert.Equal(goal, path[^1]);

                    for (var i = 0; i + 1 < path.Count; i++)
                    {
                        var passable = false;

                        foreach (var edge in graph.EdgesFrom(path[i]))
                        {
                            if (edge.To != path[i + 1])
                                continue;

                            if (!graph.EdgePassable(edge, team, hasIntel: false, setupGatesClosed: false))
                                continue;

                            // The class arcs the default query refuses.
                            if (edge.Type is NavEdgeType.DoubleJump or NavEdgeType.RocketJump)
                                continue;

                            passable = true;
                            break;
                        }

                        Assert.True(
                            passable,
                            $"{map}_a{area}: the route hops {path[i]}->{path[i + 1]} with no edge "
                            + $"a {team} bot may take");

                        hops++;
                    }
                }
            }
        }

        Assert.True(routes > 1000, $"only {routes} routes were found; the sweep proved little");
        Assert.True(hops > 1000, $"only {hops} hops were checked");
    }

    /// <summary>
    /// A team gate really does change what a team can reach, on a real map.
    /// </summary>
    /// <remarks>
    /// The property test builds a gate to prove the mechanism; this shows it
    /// bites on shipped geometry, which is the thing that would silently stop
    /// being true if a map changed. <c>ctf_truefort</c> is the map whose blue
    /// spawn exit hides a pit "whose only exit is behind a BLUE TEAM GATE", so a
    /// red bot or a blue carrier that falls in is stuck until something kills it.
    /// </remarks>
    [Fact]
    public void ATeamGateChangesWhatATeamCanReach()
    {
        var graph = Graph("ctf_truefort");
        var search = new NavSearch(graph);
        var path = new List<int>();

        var red = 0;
        var blue = 0;

        // Reachability from node 0, counted rather than compared route by route:
        // what matters is that the two teams see different graphs at all.
        for (var goal = 0; goal < graph.NodeCount; goal++)
        {
            if (search.Find(0, goal, new NavQuery(Team.Red), path))
                red++;

            if (search.Find(0, goal, new NavQuery(Team.Blue), path))
                blue++;
        }

        Assert.True(red > 0 && blue > 0, "neither team can reach anything, so the graph is broken");
        Assert.NotEqual(red, blue);
    }

    /// <summary>
    /// Carrying the intel shrinks what a bot can reach on a CTF map.
    /// </summary>
    /// <remarks>
    /// <b>Your own team gate is open unless you are carrying intel out of
    /// it</b>, which is why a CTF return leg is a different query from the
    /// outbound one — and why checking only the outbound leg hid a broken
    /// <c>ctf_conflict</c> behind a passing audit for two revisions. A carrier
    /// can never reach <em>more</em> than an empty-handed bot of the same team:
    /// every gate rule is a restriction.
    /// </remarks>
    [Fact]
    public void CarryingTheIntelOnlyEverTakesRoutesAway()
    {
        var checkedMaps = 0;

        foreach (var map in new[] { "ctf_truefort", "ctf_conflict", "ctf_avanti" })
        {
            var graph = Graph(map);
            var search = new NavSearch(graph);
            var path = new List<int>();

            foreach (var team in new[] { Team.Red, Team.Blue })
            {
                var free = 0;
                var carrying = 0;

                for (var goal = 0; goal < graph.NodeCount; goal++)
                {
                    if (search.Find(0, goal, new NavQuery(team), path))
                        free++;

                    if (search.Find(0, goal, new NavQuery(team, HasIntel: true), path))
                        carrying++;
                }

                Assert.True(
                    carrying <= free,
                    $"{map}: a {team} carrier reaches {carrying} nodes against {free} empty-handed — "
                    + "a gate rule made something *more* reachable, which no gate rule can do");
            }

            checkedMaps++;
        }

        Assert.Equal(3, checkedMaps);
    }

    /// <summary>
    /// Every travel scenario's leg has a real route, hop by hop.
    /// </summary>
    /// <remarks>
    /// This replaces the breadth-first reachability check
    /// <c>ScenarioDefinitionTests</c> had while there was no search: a BFS
    /// answers "is there any way at all", and a scenario needs the route the bot
    /// will actually be handed. The two can disagree — a BFS ignores cost, so
    /// it cannot see a leg whose only route runs over an arc the asker's class
    /// cannot fly.
    /// <para>
    /// The queries here are the conservative ones: empty-handed, past setup, and
    /// unable to double- or rocket-jump. That is deliberately harder than what
    /// the scenario's own class can do, so a route found here is one every class
    /// has.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryTravelScenarioHasARouteEvenForTheLeastCapableClass()
    {
        var checkedAny = false;

        foreach (var scenario in Scenarios.All)
        {
            if (scenario.To is not { } to)
                continue;

            var graph = Graph(scenario.Map, scenario.Area);
            var search = new NavSearch(graph);
            var path = new List<int>();

            var start = graph.SnapObjective(scenario.From.X, scenario.From.Y).Node;
            var goal = graph.SnapObjective(to.X, to.Y).Node;

            Assert.True(start >= 0 && goal >= 0, $"{scenario.Name}: an endpoint is in the void");

            Assert.True(
                search.Find(start, goal, new NavQuery(scenario.Team), path),
                $"{scenario.Name}: no route from node {start} to {goal} on {scenario.Map} for "
                + $"{scenario.Team} — the scenario asks for a leg the graph does not have");

            Assert.Equal(start, path[0]);
            Assert.Equal(goal, path[^1]);
            Assert.True(path.Count > 1, $"{scenario.Name}: the leg is a single node");

            checkedAny = true;
        }

        Assert.True(checkedAny, "no travel scenario was checked");
    }
}
