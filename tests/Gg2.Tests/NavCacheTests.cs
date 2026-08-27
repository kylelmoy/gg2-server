using System.Buffers.Binary;
using System.Globalization;
using Gg2.Bots;
using Gg2.Protocol;

namespace Gg2.Tests;

/// <summary>
/// The nav cache reader: the format, and the arithmetic that reads it.
/// </summary>
/// <remarks>
/// ⚠️ <b>The format is not the obvious one</b>, and the obvious reading costs an
/// afternoon. A <c>ds_grid_write</c> string is a 12-byte header (uint32 601,
/// uint32 width, uint32 height) followed by <b>column-major</b> cells of
/// <b>sixteen</b> bytes each, with the double sitting <b>four</b> bytes into the
/// cell — not eight-byte doubles packed end to end, which is exactly what it
/// looks like.
/// <para>
/// The grids below are <em>built here</em> rather than read from the reference's
/// build directory. Two reasons, and the second is the one that matters:
/// <c>Source/build/</c> is gitignored so a fresh checkout has no caches at all;
/// and a test that only ever reads files somebody else wrote cannot tell a
/// reader that is wrong from a writer that is wrong the same way. Writing the
/// bytes to the documented layout and reading them back is what pins the layout
/// itself. <see cref="NavGraphFixtureTests"/> then reads the real ones.
/// </para>
/// </remarks>
public sealed class NavCacheTests
{
    /// <summary>
    /// Serialises a grid exactly as GM8's <c>ds_grid_write</c> does.
    /// </summary>
    /// <remarks>
    /// <paramref name="cells"/> is indexed <c>[x][y]</c> — column-major, like
    /// the format. Deliberately written from the layout description rather than
    /// by inverting the reader, so a reader with the stride wrong cannot agree
    /// with a writer that is wrong the same way.
    /// </remarks>
    internal static string WriteGrid(double[][] cells)
    {
        var width = cells.Length;
        var height = width == 0 ? 0 : cells[0].Length;
        var bytes = new byte[12 + (width * height * 16)];

        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), 601);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), (uint)width);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), (uint)height);

        for (var x = 0; x < width; x++)
        {
            for (var y = 0; y < height; y++)
            {
                var at = 12 + (16 * ((x * height) + y)) + 4;
                BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(at, 8), cells[x][y]);
            }
        }

        return Convert.ToHexString(bytes);
    }

    /// <summary>Builds a cache file's text from node and edge rows.</summary>
    internal static string WriteCache(
        int maskWidth, int maskHeight, double[][] nodeRows, double[][] edgeRows, int? version = null)
    {
        // The file is field-major: one column per field, one row per record. The
        // callers below write records, so transpose.
        static double[][] Columns(double[][] rows, int fields)
        {
            var columns = new double[fields][];
            for (var f = 0; f < fields; f++)
            {
                columns[f] = new double[Math.Max(rows.Length, 1)];
                for (var r = 0; r < rows.Length; r++)
                    columns[f][r] = rows[r][f];
            }

            return columns;
        }

        var v = (version ?? NavConstants.CacheVersion).ToString(CultureInfo.InvariantCulture);

        return $"navgraph {v}\n"
            + $"{maskWidth} {maskHeight} {nodeRows.Length} {edgeRows.Length}\n"
            + WriteGrid(Columns(nodeRows, NavConstants.NodeFields)) + "\n"
            + WriteGrid(Columns(edgeRows, NavConstants.EdgeFields)) + "\n";
    }

    /// <summary>row, x0, x1, flags, door, gate.</summary>
    private static double[] Node(int row, int x0, int x1, NavGate gate = NavGate.None) =>
        [row, x0, x1, 0, 0, (double)gate];

    /// <summary>from, to, type, bucket, ticks, cost, gate, takeoff, rejump.</summary>
    private static double[] Edge(int from, int to, NavEdgeType type, NavGate gate = NavGate.None) =>
        [from, to, (double)type, 0, 10, 12, (double)gate, 3, 0];

    /// <summary>
    /// Every field of every record survives the round trip, in the right column.
    /// </summary>
    /// <remarks>
    /// The gate for the cell stride. ⚠️ Eight-byte packing — the obvious
    /// reading — puts every field in the wrong place, and because they are all
    /// plausible small numbers the result is a graph that loads without
    /// complaint and describes geometry that does not exist.
    /// </remarks>
    [Fact]
    public void EveryFieldSurvivesTheRoundTrip()
    {
        var text = WriteCache(
            240, 180,
            [Node(10, 4, 9), Node(10, 20, 26, NavGate.TeamRed), Node(17, 4, 30, NavGate.Setup)],
            [
                [0, 1, (double)NavEdgeType.Walk, 2, 14.5, 16.25, (double)NavGate.None, 7, 0],
                [1, 2, (double)NavEdgeType.RocketJump, 5, 41, 51, (double)NavGate.IntelBlue, 22, 1],
            ]);

        var graph = NavCache.Parse(text, "fixture", 1);

        Assert.Equal(240, graph.MaskWidth);
        Assert.Equal(180, graph.MaskHeight);
        Assert.Equal(3, graph.NodeCount);
        Assert.Equal(2, graph.EdgeCount);

        Assert.Equal(new NavNode(10, 20, 26, 0, NavDoor.None, NavGate.TeamRed), graph.Nodes[1]);
        Assert.Equal(new NavNode(17, 4, 30, 0, NavDoor.None, NavGate.Setup), graph.Nodes[2]);

        // ⚠️ Rejump is field 8, and gg2-agent/tools/navgraph.js — the file the
        // format is documented in — does not read it. A reader written from that
        // alone drops it silently.
        Assert.Equal(
            new NavEdge(1, 2, NavEdgeType.RocketJump, 5, 41, 51, NavGate.IntelBlue, 22, 1),
            graph.Edges[1]);

        // Ticks and cost are reals and are different numbers: the search weight
        // carries a jump penalty the predicted duration does not.
        Assert.Equal(14.5, graph.Edges[0].Ticks);
        Assert.Equal(16.25, graph.Edges[0].Cost);
    }

    /// <summary>
    /// A cache written by a different generator is refused, not adapted.
    /// </summary>
    /// <remarks>
    /// <c>navCacheLoad.gml</c> returns false having allocated nothing. The field
    /// layout is what matters and it rarely moves — which is exactly why a
    /// reader that silently accepts a foreign version is how you end up trusting
    /// numbers about geometry that no longer exists.
    /// </remarks>
    [Fact]
    public void AForeignVersionIsRefused()
    {
        var text = WriteCache(240, 180, [Node(10, 4, 9)], [], version: NavConstants.CacheVersion + 1);

        var error = Assert.Throws<FormatException>(() => NavCache.Parse(text, "fixture", 1));
        Assert.Contains("navgraph", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A header that promises more records than the payload holds is refused.
    /// </summary>
    /// <remarks>
    /// <c>navCacheLoad.gml</c>'s own check: "the header and the payload have to
    /// agree, or the columns do not mean what the field constants say they
    /// mean."
    /// </remarks>
    [Fact]
    public void AHeaderThatDisagreesWithThePayloadIsRefused()
    {
        var honest = WriteCache(240, 180, [Node(10, 4, 9)], [Edge(0, 0, NavEdgeType.Walk)]);
        var lines = honest.Split('\n');
        lines[1] = "240 180 9 1"; // nine nodes promised, one written

        Assert.Throws<FormatException>(() => NavCache.Parse(string.Join('\n', lines), "fixture", 1));
    }

    /// <summary>
    /// A line that is not a grid is refused on its type tag rather than read as
    /// a pile of plausible doubles.
    /// </summary>
    [Fact]
    public void ALineThatIsNotAGridIsRefused()
    {
        var honest = WriteCache(240, 180, [Node(10, 4, 9)], []);
        var lines = honest.Split('\n');

        // Same length, valid hex, wrong type tag.
        var bytes = Convert.FromHexString(lines[2]);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), 602);
        lines[2] = Convert.ToHexString(bytes);

        var error = Assert.Throws<FormatException>(() => NavCache.Parse(string.Join('\n', lines), "fixture", 1));
        Assert.Contains("601", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A missing file is false, not an exception.</summary>
    /// <remarks>
    /// The reference's contract: a miss is "no bots path on this map", not an
    /// error, because a server with an ungenerated map in its rotation should
    /// keep serving that map to humans.
    /// </remarks>
    [Fact]
    public void AMissingCacheIsAQuietFalse()
    {
        Assert.False(NavCache.TryLoad(Path.GetTempPath(), "no_such_map_at_all", 1, out var graph));
        Assert.Null(graph);
    }

    /// <summary>
    /// The cache key is the map name and the area, and unsafe characters become
    /// underscores.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Not the MD5.</b> <c>serverGotoMap.gml</c> leaves
    /// <c>currentMapMD5</c> empty for every internal map, so keying on it alone
    /// would give all 22 shipped maps one cache entry and serve the first graph
    /// built for every one of them.
    /// </remarks>
    [Theory]
    [InlineData("ctf_truefort", 1, "ctf_truefort_a1")]
    [InlineData("cp_dirtbowl", 3, "cp_dirtbowl_a3")]
    [InlineData("my map!", 1, "my_map__a1")]
    public void TheKeyIsTheNameAndTheArea(string map, int area, string expected) =>
        Assert.Equal(expected, NavCache.Key(map, area));

    /// <summary>
    /// The edge index groups a node's outgoing edges into one contiguous slice.
    /// </summary>
    /// <remarks>
    /// The cache guarantees the edge list is sorted by from-node — gg2-nav-gen
    /// counting-sorts it as its last step — which is what lets the index be two
    /// numbers instead of a list. A node with no outgoing edges is an empty
    /// span, not a slice starting at edge 0: ⚠️ <c>navEdgeIndex.gml</c> defaults
    /// the start to -1 for exactly that ambiguity.
    /// </remarks>
    [Fact]
    public void TheEdgeIndexGroupsEachNodesOutgoingEdges()
    {
        var graph = NavCache.Parse(
            WriteCache(
                240, 180,
                [Node(10, 4, 9), Node(10, 20, 26), Node(17, 4, 30)],
                [
                    Edge(0, 1, NavEdgeType.Walk),
                    Edge(0, 2, NavEdgeType.Fall),
                    Edge(2, 0, NavEdgeType.Jump),
                ]),
            "fixture", 1);

        Assert.Equal([1, 2], graph.EdgesFrom(0).ToArray().Select(e => e.To));
        Assert.True(graph.EdgesFrom(1).IsEmpty);
        Assert.Equal([0], graph.EdgesFrom(2).ToArray().Select(e => e.To));

        // Out of range is empty rather than a throw: the graph is read from a
        // file and a caller asking about a node that is not there wants an
        // answer, not an exception.
        Assert.True(graph.EdgesFrom(-1).IsEmpty);
        Assert.True(graph.EdgesFrom(99).IsEmpty);
    }

    /// <summary>
    /// A node lookup rounds the feet row rather than flooring it.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The one word that is the difference between a bot that walks a
    /// staircase and one that hops up it</b>, and the one place this reader
    /// deliberately disagrees with <c>gg2-agent/tools/navgraph.js</c>, which
    /// still floors. <c>navNodeFromWorld.gml</c>'s own header explains it: a
    /// node's feet sit exactly on a row boundary, but GG2 rests a character with
    /// its feet a pixel <em>above</em> the surface, so flooring puts a
    /// stationary bot in the row above the one it is standing in.
    /// <para>
    /// The fixture is a staircase, because that is the only shape where it
    /// shows: on a wide floor the node below wins on <c>dx</c> anyway. Two
    /// one-column nodes a row apart, and a body resting one pixel high.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheFeetRowIsRoundedAndNotFloored()
    {
        // Two steps of a staircase: one column each, one row apart.
        var graph = NavCache.Parse(
            WriteCache(240, 180, [Node(20, 8, 8), Node(19, 9, 9)], []), "fixture", 1);

        // Where a body standing on node 0 has its origin.
        var stand = graph.StandY(0);
        Assert.Equal(0, graph.NodeFromWorld(NavGraph.ColumnWorldX(8), stand));

        // ⚠️ And a pixel higher, which is where the engine actually rests it.
        // (20 + 6) * 6 - 23 = 133; (133 - 1 + 23) / 6 = 25.833, which rounds to
        // 26 = 20 + BoxHeight and floors to 25 — the step above.
        var resting = stand - 1;
        Assert.Equal(0, graph.NodeFromWorld(NavGraph.ColumnWorldX(8), resting));

        // The control: flooring gives node 1 for the same position, so the
        // assertion above is about the rounding and not about the fixture.
        var flooredRow = (int)Math.Floor((resting + NavGraph.FeetOffset) / (double)NavConstants.CellSize);
        Assert.Equal(graph.Nodes[1].Row + NavConstants.BoxH, flooredRow);
    }

    /// <summary>
    /// The row index and a whole-graph scan resolve to the same node, ties
    /// included.
    /// </summary>
    /// <remarks>
    /// <c>navNodeFromWorld.gml</c> keeps both, and asserts their equivalence in
    /// prose: "walking the rows in ascending order visits exactly the nodes the
    /// old scan would have accepted, in the same index order, so the strict
    /// <c>&lt;</c> still resolves a tie to the same node it always did." This
    /// port only has the indexed form, so the scan is written here as the
    /// control — and it is what catches a row index built forwards, which leaves
    /// the *last* node on each row and breaks exactly the ties this is about.
    /// </remarks>
    [Fact]
    public void TheRowIndexAgreesWithAWholeGraphScan()
    {
        var nodes = new List<double[]>();

        // A shape with plenty of ties: several nodes per row, rows close enough
        // together to be inside the tolerance of one another.
        for (var row = 8; row <= 14; row++)
            for (var x = 0; x < 40; x += 7)
                nodes.Add(Node(row, x, x + 3));

        var graph = NavCache.Parse(WriteCache(240, 180, [.. nodes], []), "fixture", 1);

        var checkedAny = false;

        for (var wx = 0; wx < 260; wx += 3)
        {
            for (var wy = 40; wy < 140; wy += 2)
            {
                var indexed = graph.NodeFromWorld(wx, wy);
                var scanned = Scan(graph, wx, wy);

                Assert.Equal(scanned, indexed);
                checkedAny |= indexed >= 0;
            }
        }

        Assert.True(checkedAny, "the sweep never resolved a node, so it proved nothing");
    }

    /// <summary>
    /// <c>navNodeFromWorld.gml</c>'s linear fallback, transcribed as the control
    /// for the indexed form.
    /// </summary>
    private static int Scan(NavGraph graph, double worldX, double worldY)
    {
        var mx = NavGraph.AnchorColumn(worldX);
        var my = (int)Gml.Round((worldY + NavGraph.FeetOffset) / (double)NavConstants.CellSize);

        var best = -1;
        var bestRank = int.MaxValue;

        for (var i = 0; i < graph.NodeCount; i++)
        {
            var node = graph.Nodes[i];

            var dy = Math.Abs(node.Row + NavConstants.BoxH - my);
            if (dy > 3)
                continue;

            var dx = 0;
            if (mx < node.X0)
                dx = node.X0 - mx;
            else if (mx > node.X1)
                dx = mx - node.X1;

            if (dx > 2)
                continue;

            var rank = (dy * 4) + dx;
            if (rank < bestRank)
            {
                bestRank = rank;
                best = i;
            }
        }

        return best;
    }

    /// <summary>
    /// The anchor-column conversion is a half-box offset, and it round-trips.
    /// </summary>
    /// <remarks>
    /// ⚠️ A node's x-span is the <em>left edge</em> of the body box; a
    /// character's <c>x</c> is the centre of its sprite. Dropping the offset is
    /// invisible on a wide surface and quietly wrong on a narrow one — a
    /// two-cell ledge is missed entirely.
    /// </remarks>
    [Fact]
    public void TheAnchorColumnIsHalfABoxLeftOfTheBodyCentre()
    {
        for (var column = -3; column < 40; column++)
        {
            var world = NavGraph.ColumnWorldX(column);
            Assert.Equal(column, NavGraph.AnchorColumn(world));
        }

        // The offset is real: the centre of the box is two cells right of its
        // anchor, so the two numbers are not the same.
        Assert.Equal(12.0, NavGraph.ColumnWorldX(0));
        Assert.NotEqual(0, NavGraph.AnchorColumn(0));
    }

    /// <summary>
    /// A gate's answer depends on the traveller, which is why gates are not
    /// baked into the graph.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Your own team gate is open <em>unless</em> you are carrying intel
    /// out of it</b>, and an enemy intel gate is closed only to a carrier. That
    /// first rule is why a CTF return leg is a different query from the outbound
    /// one — the reference records a broken <c>ctf_conflict</c> hiding behind a
    /// passing audit for two revisions because only the outbound leg was
    /// checked.
    /// </remarks>
    [Theory]
    // No gate: everyone, always.
    [InlineData(NavGate.None, Team.Red, false, false, true)]
    [InlineData(NavGate.None, Team.Blue, true, true, true)]
    // Red's own team gate: open to red, closed to red carrying, closed to blue.
    [InlineData(NavGate.TeamRed, Team.Red, false, false, true)]
    [InlineData(NavGate.TeamRed, Team.Red, true, false, false)]
    [InlineData(NavGate.TeamRed, Team.Blue, false, false, false)]
    // Red's intel gate: open to red either way, and to blue only empty-handed.
    [InlineData(NavGate.IntelRed, Team.Red, true, false, true)]
    [InlineData(NavGate.IntelRed, Team.Blue, false, false, true)]
    [InlineData(NavGate.IntelRed, Team.Blue, true, false, false)]
    // The setup gate is closed to everyone, and only while setup runs.
    [InlineData(NavGate.Setup, Team.Red, false, true, false)]
    [InlineData(NavGate.Setup, Team.Red, false, false, true)]
    public void AGateAsksWhoIsWalkingThroughIt(
        NavGate gate, Team team, bool hasIntel, bool setupClosed, bool expected) =>
        Assert.Equal(expected, NavGraph.GatePassable(gate, team, hasIntel, setupClosed));

    /// <summary>
    /// The typed views agree with the constant table they restate.
    /// </summary>
    /// <remarks>
    /// <c>Gg2.Protocol.NavConstants</c> is the transcription of
    /// <c>Constants.xml</c> and <c>ConstantsTests</c> already pins it to the
    /// file — so the only way these enums can be wrong is by drifting from
    /// <em>it</em>, which is what this catches. ⚠️ The numbers were nearly
    /// re-derived from the reference a second time when this reader was written;
    /// the table had been there since phase 0.
    /// </remarks>
    [Fact]
    public void TheTypedViewsMatchTheConstantTable()
    {
        Assert.Equal(NavConstants.EdgeWalk, (int)NavEdgeType.Walk);
        Assert.Equal(NavConstants.EdgeFall, (int)NavEdgeType.Fall);
        Assert.Equal(NavConstants.EdgeJump, (int)NavEdgeType.Jump);
        Assert.Equal(NavConstants.EdgeDropthrough, (int)NavEdgeType.DropThrough);
        Assert.Equal(NavConstants.EdgeMovebox, (int)NavEdgeType.MoveBox);
        Assert.Equal(NavConstants.EdgeDoublejump, (int)NavEdgeType.DoubleJump);
        Assert.Equal(NavConstants.EdgeRocketjump, (int)NavEdgeType.RocketJump);
        Assert.Equal(NavConstants.EdgeStickyjump, (int)NavEdgeType.StickyJump);

        Assert.Equal(NavConstants.GateNone, (int)NavGate.None);
        Assert.Equal(NavConstants.GateTeamRed, (int)NavGate.TeamRed);
        Assert.Equal(NavConstants.GateTeamBlue, (int)NavGate.TeamBlue);
        Assert.Equal(NavConstants.GateIntelRed, (int)NavGate.IntelRed);
        Assert.Equal(NavConstants.GateIntelBlue, (int)NavGate.IntelBlue);
        Assert.Equal(NavConstants.GateSetup, (int)NavGate.Setup);

        Assert.Equal(NavConstants.DoorNone, (int)NavDoor.None);
        Assert.Equal(NavConstants.DoorLeft, (int)NavDoor.Left);
        Assert.Equal(NavConstants.DoorRight, (int)NavDoor.Right);
    }

    /// <summary>An unknown gate code refuses passage rather than allowing it.</summary>
    /// <remarks>
    /// A graph built by a newer generator, or a corrupt row. A bot that will not
    /// walk through something is worse behaviour, not a broken game.
    /// </remarks>
    [Fact]
    public void AnUnknownGateCodeRefusesPassage() =>
        Assert.False(NavGraph.GatePassable((NavGate)97, Team.Red, hasIntel: false, setupGatesClosed: false));

    /// <summary>
    /// An edge is judged by its own gate, and not by the gate on the node it
    /// lands in.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>This looks like an omission and is the reference's rule.</b>
    /// <c>navFindPath.gml</c> tests <c>NAV_EDGE_GATE</c> and nothing else,
    /// because "an arc can cross a gate without landing on it — the generator
    /// stamps an edge that passes through a gate with that gate, so the check
    /// below covers both".
    /// <para>
    /// ⚠️ <b>This test asserted the opposite until 2026-08-27</b>, because it was
    /// written from <c>gg2-agent/tools/navgraph.js</c>'s <c>reach</c>, which
    /// checks both. That tool is an audit answering "can a body get there" and is
    /// deliberately conservative; the game is the specification. The two agree on
    /// every shipped graph —
    /// <c>NavSearchFixtureTests.NoEdgeLandsOnAGatedNodeUnstamped</c> is the
    /// measurement, and is what notices if a generator ever stops stamping.
    /// </para>
    /// <para>
    /// The rule this preserves is worth stating: <b>a bot standing inside a gate
    /// it may not pass still gets edges out.</b> One that has just picked up the
    /// intel, or anyone the setup gates shut around, would otherwise be trapped
    /// by its own footing.
    /// </para>
    /// </remarks>
    [Fact]
    public void AnEdgeIsJudgedByItsOwnGateAndNotItsLandingNode()
    {
        var graph = NavCache.Parse(
            WriteCache(
                240, 180,
                [Node(10, 4, 9, NavGate.TeamRed), Node(10, 20, 26, NavGate.TeamBlue)],
                [Edge(0, 1, NavEdgeType.Walk), Edge(1, 0, NavEdgeType.Walk, NavGate.TeamBlue)]),
            "fixture", 1);

        var unstamped = graph.Edges[0];
        var stamped = graph.Edges[1];

        // The unstamped edge lands on a blue gate and is open to everyone: the
        // generator would have stamped it if it were not.
        Assert.Equal(NavGate.None, unstamped.Gate);
        Assert.True(graph.EdgePassable(unstamped, Team.Red, hasIntel: false, setupGatesClosed: false));
        Assert.True(graph.EdgePassable(unstamped, Team.Blue, hasIntel: false, setupGatesClosed: false));

        // The stamped one is refused, and its landing node's own red gate has
        // nothing to do with it.
        Assert.True(graph.EdgePassable(stamped, Team.Blue, hasIntel: false, setupGatesClosed: false));
        Assert.False(graph.EdgePassable(stamped, Team.Red, hasIntel: false, setupGatesClosed: false));

        // ⚠️ And the edge OUT of a gated node is not refused for standing there:
        // node 0 carries a red gate, and a blue bot on it can still leave.
        Assert.True(graph.EdgePassable(unstamped, Team.Blue, hasIntel: true, setupGatesClosed: true));
    }

    /// <summary>
    /// A node count past <c>navEdgeKey</c>'s stride is refused rather than
    /// silently aliasing two edges onto one key.
    /// </summary>
    /// <remarks>
    /// The multiplier is 100,000 and the largest shipped graph is 833 nodes, so
    /// this is unreachable today. ⚠️ It is checked because the symptom of a
    /// collision is a bot blacklisting an edge it never tried, which is
    /// indistinguishable from the thrash the blacklist exists to stop.
    /// </remarks>
    [Fact]
    public void TheEdgeKeyPacksTwoNodeIndicesWithoutColliding()
    {
        Assert.NotEqual(NavGraph.EdgeKey(0, 1), NavGraph.EdgeKey(1, 0));
        Assert.NotEqual(NavGraph.EdgeKey(1, NavGraph.KeyStride - 1), NavGraph.EdgeKey(2, 0));
        Assert.Equal((833L * NavGraph.KeyStride) + 12, NavGraph.EdgeKey(833, 12));
    }
}
