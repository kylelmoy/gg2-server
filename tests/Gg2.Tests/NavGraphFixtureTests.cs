using Gg2.Bots;
using Gg2.Maps;
using Gg2.Protocol;

namespace Gg2.Tests;

/// <summary>
/// The reader against every nav cache <c>gg2-nav-gen</c> has actually written.
/// </summary>
/// <remarks>
/// <see cref="NavCacheTests"/> pins the format by writing bytes and reading them
/// back; this reads the real ones, which is the only thing that can catch a
/// layout that is self-consistently wrong. Between them the two answer different
/// questions: "does this reader match the documented format" and "does the
/// documented format match what the generator emits".
/// <para>
/// ⚠️ <b>These need <c>Source/build/botnav/</c>, which is gitignored in the
/// reference and is not shipped.</b> A checkout that has never run the generator
/// has none, which is why they say so with
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
    /// ⚠️ <b>Both indices are correctness bets on the generator's output, not on
    /// this reader's code.</b> <c>navEdgeIndex.gml</c> takes it on trust that
    /// "edges must already be sorted by <c>NAV_EDGE_FROM</c>, which the cache
    /// file guarantees: gg2-nav-gen counting-sorts the edge list by from-node as
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
    /// ⚠️ <b>The cross-check that catches an area mix-up</b>, which is the one
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
    /// Data, asserted so it stops being true loudly. ⚠️ The claim worth having is
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
}
