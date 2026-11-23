using GG2.Bots;
using GG2.Maps;
using GG2.Protocol;

namespace GG2.Tests;

/// <summary>
/// The reader against every nav cache the original nav generator has actually written.
/// </summary>
/// <remarks>
/// <see cref="NavCacheTests"/> pins the format by writing bytes and reading them
/// back; this reads the real ones, which is the only thing that can catch a
/// layout that is self-consistently wrong. Between them the two answer different
/// questions: "does this reader match the documented format" and "does the
/// documented format match what the generator emits".
/// <para>
/// <b>These need the nav caches in <c>nav/</c>, which are gitignored here and
/// not shipped.</b> A clone whose generator has never been run has none, which is
/// why they say so with
/// <see cref="ReferenceTree.RequireNavCache"/> rather than failing as if the
/// tree were missing.
/// </para>
/// </remarks>
public sealed class NavGraphFixtureTests
{
    private static IReadOnlyList<(string Map, int Area)> Caches()
    {
        ReferenceTree.RequireNavCache();
        return NavCache.List(ReferenceTree.NavCacheDirectory);
    }

    /// <summary>Every cache on disk parses, and its header agrees with its payload.</summary>
    [Fact]
    public void EveryShippedCacheLoads()
    {
        var caches = Caches();
        Assert.NotEmpty(caches);

        foreach (var (map, area) in caches)
        {
            Assert.True(
                NavCache.TryLoad(ReferenceTree.NavCacheDirectory, map, area, out var graph),
                $"{map} area {area} did not load");

            Assert.NotNull(graph);
            Assert.Equal(NavConstants.CacheVersion, graph.Version);
            Assert.True(graph.NodeCount > 0, $"{map} area {area} has no nodes");
            Assert.Equal(map, graph.Map);
            Assert.Equal(area, graph.Area);
        }
    }

    /// <summary>
    /// Every edge names nodes that exist, and every gate code is one this reader
    /// knows.
    /// </summary>
    /// <remarks>
    /// A code outside the enum would be silently refused passage by
    /// <see cref="NavGraph.GatePassable"/>'s default arm — which is the right
    /// behaviour for a corrupt row and the wrong behaviour for a real one this
    /// reader has not been told about. Asserting it here is what turns "bots
    /// won't walk through that" into a test failure.
    /// </remarks>
    [Fact]
    public void EveryEdgeAndGateIsWellFormed()
    {
        foreach (var (map, area) in Caches())
        {
            var graph = NavCache.Load(ReferenceTree.NavCacheDirectory, map, area);

            foreach (var node in graph.Nodes)
            {
                Assert.True(Enum.IsDefined(node.Gate), $"{map}: node gate {(int)node.Gate}");
                Assert.True(Enum.IsDefined(node.Door), $"{map}: node door {(int)node.Door}");
                Assert.True(node.X0 <= node.X1, $"{map}: node span {node.X0}..{node.X1} is backwards");
            }

            foreach (var edge in graph.Edges)
            {
                Assert.InRange(edge.From, 0, graph.NodeCount - 1);
                Assert.InRange(edge.To, 0, graph.NodeCount - 1);
                Assert.True(Enum.IsDefined(edge.Type), $"{map}: edge type {(int)edge.Type}");
                Assert.True(Enum.IsDefined(edge.Gate), $"{map}: edge gate {(int)edge.Gate}");
            }
        }
    }

    /// <summary>
    /// The two orderings the derived indices depend on actually hold in the
    /// files.
    /// </summary>
    /// <remarks>
    /// <b>Both indices are correctness bets on the generator's output, not on
    /// this reader's code.</b> <c>navEdgeIndex.gml</c> takes it on trust that
    /// "edges must already be sorted by <c>NAV_EDGE_FROM</c>, which the cache
    /// file guarantees: the original nav generator counting-sorts the edge list by from-node as
    /// its last step", and <c>navNodeFromWorld.gml</c> the same for "nodes are
    /// emitted sorted by y then x, so the nodes of one row are contiguous".
    /// <para>
    /// If either stops being true the index does not throw — it returns a
    /// shorter neighbour list, or stops a row walk early, and every answer
    /// downstream is quietly incomplete. This is the test that notices, and it
    /// is the reason to have it rather than trusting the prose.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheFileIsSortedTheWayBothIndicesAssume()
    {
        foreach (var (map, area) in Caches())
        {
            var graph = NavCache.Load(ReferenceTree.NavCacheDirectory, map, area);

            for (var e = 1; e < graph.EdgeCount; e++)
            {
                Assert.True(
                    graph.Edges[e].From >= graph.Edges[e - 1].From,
                    $"{map} area {area}: edge {e} is from node {graph.Edges[e].From} after "
                    + $"{graph.Edges[e - 1].From} — the edge index assumes this cannot happen");
            }

            for (var n = 1; n < graph.NodeCount; n++)
            {
                var previous = graph.Nodes[n - 1];
                var current = graph.Nodes[n];

                Assert.True(
                    current.Row > previous.Row || (current.Row == previous.Row && current.X0 >= previous.X0),
                    $"{map} area {area}: node {n} (row {current.Row}, x0 {current.X0}) follows "
                    + $"(row {previous.Row}, x0 {previous.X0}) — the row index assumes y then x");
            }
        }
    }

    /// <summary>
    /// The graph's mask dimensions are the map's own, for the area it names.
    /// </summary>
    /// <remarks>
    /// <b>The cross-check that catches an area mix-up</b>, which is the one
    /// mistake in this layer that produces a completely plausible wrong answer:
    /// a tall map is several rounds stacked vertically and only one area's
    /// entities exist, so <c>cp_dirtbowl</c>'s three graphs describe three
    /// different worlds over one terrain. The terrain is <em>not</em> narrowed by
    /// <see cref="LevelMap.ForArea"/> — the mask is one instance and survives the
    /// <c>with all</c> — so every area of a map shares these numbers, and a
    /// graph whose mask does not match the map is a graph for a different map.
    /// </remarks>
    [Fact]
    public void EveryGraphsMaskMatchesItsMap()
    {
        var checkedAny = false;

        foreach (var (map, area) in Caches())
        {
            if (!File.Exists(ReferenceTree.MapPath(map)))
                continue; // a custom map somebody generated; not this port's problem

            var level = LevelMap.FromPng(map, ReferenceTree.ReadMap(map));
            var graph = NavCache.Load(ReferenceTree.NavCacheDirectory, map, area);

            Assert.Equal(level.Walkmask.Width, graph.MaskWidth);
            Assert.Equal(level.Walkmask.Height, graph.MaskHeight);
            checkedAny = true;
        }

        Assert.True(checkedAny, "no cache matched a built-in map, so nothing was cross-checked");
    }

    /// <summary>
    /// A node's own stand position resolves back to a node on its own row.
    /// </summary>
    /// <remarks>
    /// The round trip through the arithmetic that matters most: <c>StandY</c>
    /// out, <c>NodeFromWorld</c> back. It is not required to return the
    /// <em>same</em> node — a wide floor and a ledge beside it can tie, and the
    /// rank prefers the lower index — but it must land on the same row, because
    /// a stand position is on that surface by construction.
    /// <para>
    /// This is what fails if the anchor-column offset, the feet offset or the
    /// feet-row rounding is wrong, and it fails on real geometry rather than on
    /// a fixture chosen to make the point.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryNodesStandPositionResolvesToItsOwnRow()
    {
        var resolved = 0;

        foreach (var (map, area) in Caches())
        {
            var graph = NavCache.Load(ReferenceTree.NavCacheDirectory, map, area);

            for (var n = 0; n < graph.NodeCount; n++)
            {
                // The middle of the span, so the answer is about the row and not
                // about a neighbour two columns away winning on dx.
                var column = (graph.Nodes[n].X0 + graph.Nodes[n].X1) / 2;
                var found = graph.NodeFromWorld(NavGraph.ColumnWorldX(column), graph.StandY(n));

                Assert.True(found >= 0, $"{map} area {area}: node {n}'s own stand position resolves to nothing");
                Assert.Equal(graph.Nodes[n].Row, graph.Nodes[found].Row);
                resolved++;
            }
        }

        Assert.True(resolved > 1000, $"only {resolved} nodes were checked; the sweep proved little");
    }

    /// <summary>
    /// A multi-area map really does have one graph per area, and they differ.
    /// </summary>
    /// <remarks>
    /// <c>cp_dirtbowl</c> writes six control points and plays two, and each stage
    /// is a genuinely different graph. If the reader ever collapsed the area into
    /// the key this would still find three files and hand back the same graph
    /// three times.
    /// </remarks>
    [Fact]
    public void AMultiAreaMapHasADifferentGraphPerArea()
    {
        var caches = Caches();
        var areas = caches.Where(c => c.Map == "cp_dirtbowl").Select(c => c.Area).Order().ToArray();

        Assert.Equal([1, 2, 3], areas);

        var graphs = areas
            .Select(a => NavCache.Load(ReferenceTree.NavCacheDirectory, "cp_dirtbowl", a))
            .ToArray();

        Assert.Distinct(graphs.Select(g => (g.NodeCount, g.EdgeCount)).ToArray());
    }

    /// <summary>
    /// The gates the shipped maps actually place, and what they mean for a
    /// carrier.
    /// </summary>
    /// <remarks>
    /// Data, asserted so it stops being true loudly. The claim worth having is
    /// the second one: <b>a CTF map's own team gates are the reason the return
    /// leg is a different query from the outbound one</b> — red walks out of its
    /// own gate freely and cannot carry the enemy flag back through it — and a
    /// gate-blind traversal cannot see that at all.
    /// </remarks>
    [Fact]
    public void ACtfMapPlacesTeamGatesThatRefuseACarrier()
    {
        ReferenceTree.RequireNavCache();

        var graph = NavCache.Load(ReferenceTree.NavCacheDirectory, "ctf_truefort", 1);

        var gates = graph.Nodes.ToArray()
            .Select(n => n.Gate)
            .Concat(graph.Edges.ToArray().Select(e => e.Gate))
            .Where(g => g != NavGate.None)
            .Distinct()
            .ToArray();

        Assert.NotEmpty(gates);

        foreach (var gate in gates)
        {
            var owner = gate is NavGate.TeamRed or NavGate.IntelRed ? Team.Red : Team.Blue;

            if (gate is NavGate.TeamRed or NavGate.TeamBlue)
            {
                Assert.True(NavGraph.GatePassable(gate, owner, hasIntel: false, setupGatesClosed: false));
                Assert.False(NavGraph.GatePassable(gate, owner, hasIntel: true, setupGatesClosed: false));
            }
        }
    }

    /// <summary>
    /// A jump edge's velocity survives the load with its fraction intact.
    /// </summary>
    /// <remarks>
    /// <c>NAV_EDGE_BUCKET</c> is the horizontal velocity <c>navJumpTakeoff</c>
    /// proved the arc at — "cover NAV_EDGE_BUCKET px of ground a tick"
    /// (<c>botPathKeys.gml:436</c>) — and the name invites an integer it has
    /// never held. Truncating it to an <c>int</c> costs five of the fourteen
    /// behaviour scenarios: <c>truefort-blue-spawn-exit-west</c> ends 834px from
    /// a goal 150px away where the reference walks in 58 ticks.
    /// <para>
    /// Two assertions, because they fail at different distances from the cause.
    /// <b>No jump edge may read zero</b> — that is the 34.6% the truncation
    /// zeroed outright, and a zero velocity makes the in-flight tracker steer for
    /// the takeoff column itself, so the bot is told to hang still in the air.
    /// And <b>the values must actually be fractional</b>, which is what notices a
    /// rounding rather than a truncation, and what notices a regenerated cache
    /// that quietly started storing indices.
    /// </para>
    /// <para>
    /// The scenario suite catches this too, and catching it there is not
    /// enough: it is a whole map load and 6,000 ticks away from the line that is
    /// wrong, and it reads as a follower defect. Measured over the 24 shipped
    /// caches — 67,013 jump edges, every one fractional.
    /// </para>
    /// </remarks>
    [Fact]
    public void AJumpEdgesVelocityIsARealAndSurvivesTheLoad()
    {
        var total = 0;
        var fractional = 0;

        foreach (var (map, area) in Caches())
        {
            var graph = NavCache.Load(ReferenceTree.NavCacheDirectory, map, area);

            foreach (var edge in graph.Edges)
            {
                if (edge.Type != NavEdgeType.Jump)
                    continue;

                total++;

                Assert.True(
                    edge.Bucket > 0,
                    $"{map} a{area}: jump edge {edge.From}->{edge.To} has a horizontal velocity "
                    + "of 0, which cannot fly anywhere — NAV_EDGE_BUCKET has been truncated");

                if (edge.Bucket != Math.Floor(edge.Bucket))
                    fractional++;
            }
        }

        Assert.True(total > 60000, $"only {total} jump edges across every cache, expected ~67,000");

        // Measured: 67,005 of 67,013. Eight really do land on a whole number,
        // which is what a velocity does occasionally and what an index does
        // always — so the claim is a proportion, and the sharp gate is the two
        // named edges below.
        Assert.True(
            fractional > total * 0.999,
            $"only {fractional} of {total} jump velocities are fractional; a field that is "
            + "mostly integral is an index, and the follower's trajectory model is built on "
            + "this being a speed");

        // The gate that actually bites, and it bites on a rounding as well as a
        // truncation. Both values are the cache's own bits; the second is the arc
        // the reference's takeoff-gate comment quotes as "0.70 px/tick over 17.2
        // ticks against a 12px gap: exactly 12 for exactly 12", which is
        // corroboration that this port is reading the same field the reference is.
        var truefort = NavCache.Load(ReferenceTree.NavCacheDirectory, "ctf_truefort", 1);
        var gallery = NavCache.Load(ReferenceTree.NavCacheDirectory, "koth_gallery", 1);

        Assert.Equal(1.9518072289156623, Edge(truefort, 178, 177).Bucket);
        Assert.Equal(0.6975139064871476, Edge(gallery, 86, 58).Bucket);
    }

    private static NavEdge Edge(NavGraph graph, int from, int to)
    {
        foreach (var edge in graph.Edges)
        {
            if (edge.From == from && edge.To == to)
                return edge;
        }

        throw new InvalidOperationException($"no edge {from}->{to} in {graph.Map}");
    }
    /// <summary>
    /// Parallel edges are common, they carry different types, and the type the
    /// edge index yields first is routinely NOT the one a search would choose.
    /// </summary>
    /// <remarks>
    /// <b>This is the measurement that made a whole class of bug visible, and
    /// it is pinned so the synthetic tests for it cannot go quietly vacuous.</b>
    /// A route is a list of nodes, so <c>PathFollower.TryFindEdge</c> re-derives
    /// which edge each hop meant — and it used to take the first match. Nothing
    /// in the repo said how often a pair carries more than one.
    /// <para>
    /// It carries more than one <b>11,662 times</b>, and the two that matter are
    /// class-refusable: <b>837 node pairs where a soldier's route is costed as a
    /// rocket jump while the index yields a double jump first</b>. That soldier
    /// pressed for a second jump its class does not have, flew an ordinary 57px
    /// hop where the arc wanted 276, and eventually blacklisted a good edge.
    /// <see cref="PathFollowerTests.ASoldierOnAPairThatAlsoCarriesADoubleJumpStillTakesTheRocketJump"/>
    /// is the gate; this is the evidence that the gate describes real graphs.
    /// </para>
    /// <para>
    /// <b>A generator that stopped emitting parallel edges would make every one
    /// of those tests pass while testing nothing</b>, and nothing else here would
    /// notice — the caches are built out of band by the original nav generator. So the counts
    /// are exact, and a move is the re-record: check the generator changed the way
    /// you meant, update the numbers, and say so in the commit.
    /// </para>
    /// <para>
    /// <b>The scout column is deliberately zero and is not padding.</b> The
    /// double jump is what the index yields first on every one of those 833 pairs,
    /// so a scout was accidentally right the whole time — which is exactly why the
    /// bug survived: half the population never showed it.
    /// </para>
    /// </remarks>
    [Fact]
    public void ParallelEdgesAreCommonAndCarryMoreThanOneType()
    {
        var caches = Caches();

        var pairs = 0;
        var multi = 0;
        var mixed = 0;
        var soldierMisresolved = 0;
        var soldierUnflyable = 0;
        var scoutMisresolved = 0;
        var scoutUnflyable = 0;

        var soldier = new NavQuery(Team.Red, CanRocketJump: true);
        var scout = new NavQuery(Team.Red, CanDoubleJump: true);

        var byTo = new Dictionary<int, List<NavEdge>>();

        foreach (var (map, area) in caches)
        {
            var graph = NavCache.Load(ReferenceTree.NavCacheDirectory, map, area);

            for (var node = 0; node < graph.NodeCount; node++)
            {
                byTo.Clear();

                foreach (var edge in graph.EdgesFrom(node))
                {
                    if (!byTo.TryGetValue(edge.To, out var list))
                        byTo[edge.To] = list = [];

                    list.Add(edge);
                }

                foreach (var (_, list) in byTo)
                {
                    pairs++;

                    if (list.Count == 1)
                        continue;

                    multi++;

                    var first = list[0].Type;
                    foreach (var edge in list)
                    {
                        if (edge.Type == first)
                            continue;

                        mixed++;
                        break;
                    }

                    if (Misresolved(list, in soldier, out var soldierRefused))
                    {
                        soldierMisresolved++;

                        if (soldierRefused)
                            soldierUnflyable++;
                    }

                    if (Misresolved(list, in scout, out var scoutRefused))
                    {
                        scoutMisresolved++;

                        if (scoutRefused)
                            scoutUnflyable++;
                    }
                }
            }
        }

        Assert.Equal(104_588, pairs);
        Assert.Equal(11_728, multi);
        Assert.Equal(11_662, mixed);

        // TWO different faults, and keeping them apart is the point.
        //
        // The larger number is a COST mismatch: 3,821 walk/fall pairs where both
        // edges are flyable by anybody and the cheaper one is simply not the one
        // the index yields first. The bot got somewhere; it got there along an arc
        // its route had not costed.
        //
        // The smaller is a CAPABILITY mismatch and is the live bug: 833 pairs
        // where the edge the index yields first is one this traveller may not use
        // at all. A soldier crossing any of them pressed for a second jump its
        // class does not have.
        Assert.Equal(4_692, soldierMisresolved);
        Assert.Equal(837, soldierUnflyable);

        Assert.Equal(3_855, scoutMisresolved);
        Assert.Equal(0, scoutUnflyable);
    }

    /// <summary>
    /// Whether taking the first edge of a parallel group gives a different
    /// <em>type</em> from the one <see cref="NavSearch"/> would have costed, and
    /// whether that first edge is one this traveller may not use at all.
    /// </summary>
    /// <remarks>
    /// The search's choice is the strictly cheapest routable edge, first-in-order
    /// on ties — see <c>PathFollower.TryFindEdge</c>, which reproduces exactly
    /// that. A group no traveller can use is not counted: there is no route over
    /// it to mis-steer in the first place.
    /// </remarks>
    private static bool Misresolved(List<NavEdge> group, in NavQuery query, out bool firstIsRefused)
    {
        var chosen = -1;

        for (var i = 0; i < group.Count; i++)
        {
            var edge = group[i];

            if (!NavGraph.EdgeRoutable(in edge, in query))
                continue;

            if (chosen < 0 || edge.Cost < group[chosen].Cost)
                chosen = i;
        }

        var first = group[0];

        firstIsRefused = chosen >= 0 && !NavGraph.EdgeRoutable(in first, in query);

        return chosen >= 0 && group[chosen].Type != first.Type;
    }
}
