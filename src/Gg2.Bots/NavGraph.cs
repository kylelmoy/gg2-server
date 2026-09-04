using Gg2.Protocol;

namespace Gg2.Bots;

/// <summary>One walkable surface span, in nav cells.</summary>
/// <remarks>
/// <see cref="X0"/> and <see cref="X1"/> are <b>anchor columns</b>, not world
/// pixels and not the sprite's centre — see <see cref="NavConstants.BoxW"/>.
/// Use <see cref="NavGraph.WorldSpan"/> to get pixels.
/// </remarks>
public readonly record struct NavNode(int Row, int X0, int X1, int Flags, NavDoor Door, NavGate Gate);

/// <summary>One directed traversal between two nodes.</summary>
/// <remarks>
/// <see cref="Cost"/> is the search weight and <see cref="Ticks"/> the predicted
/// duration; they differ because a jump carries <c>NAV_JUMP_PENALTY</c> and a
/// rocket jump costs hit points. <see cref="Takeoff"/> is the anchor column the
/// arc was validated from, which a follower has to actually reach before
/// pressing — an arc proved from one column does not reach from the next.
/// <para>
/// <b><see cref="Bucket"/> is a horizontal VELOCITY in px/tick and it is
/// fractional — it is not a bucket index, whatever the cache column is called.</b>
/// <c>NAV_EDGE_BUCKET</c> is what <c>navJumpTakeoff</c> proved the arc at:
/// "leave the takeoff column, cover NAV_EDGE_BUCKET px of ground a tick, be over
/// the landing when the arc comes back down" (<c>botPathKeys.gml:436</c>).
/// Truncating it to <c>int</c> zeroes out over a third of jump edges across the
/// shipped graphs, which makes the in-flight tracker steer for the takeoff
/// column itself and the takeoff gate demand the whole gap from a reach of
/// nothing. The name is the trap; <c>NavGraphFixtureTests</c> is what stops it
/// coming back.
/// </para>
/// </remarks>
public readonly record struct NavEdge(
    int From, int To, NavEdgeType Type, double Bucket, double Ticks, double Cost,
    NavGate Gate, int Takeoff, int Rejump);

/// <summary>
/// A loaded nav graph: the nodes, the edges, and the two indices
/// <c>navCacheLoad.gml</c> derives rather than stores.
/// </summary>
/// <remarks>
/// <b>Pure and immutable once built.</b> The file reading is
/// <see cref="NavCache"/>'s; this type holds no stream, no path and no map, so a
/// rollout can carry one without reaching for the disk — the same separation
/// <c>Gg2.Simulation</c> keeps between <c>CollisionWorld</c> and the tick.
/// <para>
/// <b>Neither index is in the file, on purpose.</b> <c>navCacheLoad.gml</c>:
/// "the format carries the graph and nothing that can be recomputed from it",
/// and re-deriving both costs less than the parse that just happened.
/// </para>
/// <para>
/// <b>A graph is per map <em>and per area</em>.</b> A tall map is several
/// rounds stacked vertically and <c>basicRoomSetup</c> destroys everything
/// outside the active stage's y-band, so each stage is genuinely a different
/// graph — <c>cp_dirtbowl</c> writes six control points and plays two. Give the
/// reader the same area the simulation was given
/// (<c>Gg2.Maps.LevelMap.ForArea</c>).
/// </para>
/// </remarks>
public sealed class NavGraph
{
    /// <summary>
    /// How far below a character's origin its feet are.
    /// </summary>
    /// <remarks>
    /// <b>A literal in the GML, not a <c>NAV_*</c> constant</b> — which is why
    /// it is here and not in <see cref="NavConstants"/>. The origin sits at the
    /// chest; every stand position in the nav layer is
    /// <c>(row + BoxH) * CellSize - 23</c>, which is what
    /// <c>navSetGoalNode</c> and <c>navNodeFromWorld</c> both use.
    /// </remarks>
    public const int FeetOffset = 23;

    private readonly NavNode[] _nodes;
    private readonly NavEdge[] _edges;

    // navEdgeIndex.gml: for each node, where its outgoing edges start and how
    // many there are. The cache guarantees the edge list is already sorted by
    // from-node — gg2-nav-gen counting-sorts it as its last step — so a node's
    // neighbours are one contiguous range. Without it every expansion is a scan
    // of the whole edge list: on cp_dirtbowl, 1,298 reads per expansion against
    // about two.
    private readonly int[] _edgeStart;
    private readonly int[] _edgeCount;

    // navRowIndex.gml: the first node on each mask row, or -1. Nodes are emitted
    // sorted by row then x, so one row's nodes are contiguous and a walk ends at
    // the first node whose row differs.
    private readonly int[] _rowStart;

    internal NavGraph(
        string map, int area, int version, int maskWidth, int maskHeight,
        NavNode[] nodes, NavEdge[] edges)
    {
        Map = map;
        Area = area;
        Version = version;
        MaskWidth = maskWidth;
        MaskHeight = maskHeight;
        _nodes = nodes;
        _edges = edges;

        _edgeStart = new int[nodes.Length];
        _edgeCount = new int[nodes.Length];
        Array.Fill(_edgeStart, -1);

        for (var e = 0; e < edges.Length; e++)
        {
            var from = edges[e].From;

            // The reference skips an out-of-range from-node rather than
            // throwing: a corrupt row should cost one edge, not the graph.
            if ((uint)from >= (uint)nodes.Length)
                continue;

            if (_edgeStart[from] < 0)
                _edgeStart[from] = e;

            _edgeCount[from]++;
        }

        if (nodes.Length > KeyStride)
        {
            // navEdgeKey packs two node indices into one number. A graph big
            // enough to overflow the stride would alias two different edges onto
            // one key, and the symptom would be a bot blacklisting an edge it
            // never tried.
            throw new ArgumentException(
                $"{map} area {area} has {nodes.Length} nodes, past navEdgeKey's stride of {KeyStride}",
                nameof(nodes));
        }

        _rowStart = new int[maskHeight + 2];
        Array.Fill(_rowStart, -1);

        // Backwards, so the lowest index on each row is what survives —
        // navRowIndex.gml. Forwards would leave the *last*, and the tie-break in
        // NodeFromWorld is a strict `<`, so the two orders resolve ties to
        // different nodes.
        for (var i = nodes.Length - 1; i >= 0; i--)
        {
            var row = nodes[i].Row;
            if ((uint)row < (uint)_rowStart.Length)
                _rowStart[row] = i;
        }
    }

    /// <summary>The map this graph was built for, without the area suffix.</summary>
    public string Map { get; }

    /// <summary>Which stage of a multi-area map, 1-based.</summary>
    public int Area { get; }

    /// <summary>The <c>NAV_CACHE_VERSION</c> the file declared.</summary>
    public int Version { get; }

    /// <summary>The walkmask width the graph was built over, in cells.</summary>
    public int MaskWidth { get; }

    /// <summary>The walkmask height the graph was built over, in cells.</summary>
    public int MaskHeight { get; }

    public ReadOnlySpan<NavNode> Nodes => _nodes;

    public ReadOnlySpan<NavEdge> Edges => _edges;

    public int NodeCount => _nodes.Length;

    public int EdgeCount => _edges.Length;

    /// <summary>
    /// Where <paramref name="node"/>'s outgoing edges begin in <see cref="Edges"/>,
    /// or -1 if it has none.
    /// </summary>
    /// <remarks>
    /// <b>The index a caller needs to key a per-edge side table.</b>
    /// <see cref="EdgesFrom"/> hands back a slice with no way to say where in the
    /// flat array it came from, so anything holding one value per edge — a lane's
    /// cost field, a usage count — could not look its entry up. The i-th edge of
    /// <c>EdgesFrom(node)</c> is <c>Edges[EdgeStart(node) + i]</c>.
    /// </remarks>
    public int EdgeStart(int node) =>
        (uint)node >= (uint)_nodes.Length ? -1 : _edgeStart[node];

    /// <summary>The outgoing edges of <paramref name="node"/>, as a contiguous slice.</summary>
    public ReadOnlySpan<NavEdge> EdgesFrom(int node)
    {
        if ((uint)node >= (uint)_nodes.Length || _edgeStart[node] < 0)
            return ReadOnlySpan<NavEdge>.Empty;

        return _edges.AsSpan(_edgeStart[node], _edgeCount[node]);
    }

    // --- coordinates ---------------------------------------------------------

    /// <summary>
    /// <c>navAnchorCol.gml</c>: the anchor column a character standing at this
    /// world x occupies.
    /// </summary>
    /// <remarks>
    /// The <c>- BoxWidth/2</c> is the half-box offset between a node's span
    /// (the box's left edge) and a character's <c>x</c> (the sprite's centre).
    /// <c>div</c> in GML is an integer divide, and <see cref="NavConstants.BoxW"/>
    /// is even, so a plain <c>/ 2</c> is exact here.
    /// </remarks>
    public static int AnchorColumn(double worldX) =>
        (int)Math.Floor(worldX / NavConstants.CellSize) - (NavConstants.BoxW / 2);

    /// <summary>
    /// <c>navColWorldX.gml</c>: the world x a character stands at when its
    /// anchor is in this column — the centre of the body box.
    /// </summary>
    public static double ColumnWorldX(int column) =>
        (column * NavConstants.CellSize)
        + (NavConstants.BoxW * NavConstants.CellSize / 2.0);

    /// <summary>The world y of the surface a node stands on.</summary>
    public double FloorY(int node) =>
        (_nodes[node].Row + NavConstants.BoxH) * (double)NavConstants.CellSize;

    /// <summary>
    /// Where a character standing on this node has its origin — its chest.
    /// </summary>
    /// <remarks>
    /// The formula <c>navSetGoalNode</c> uses, and the one
    /// <see cref="NodeFromWorld"/> inverts.
    /// </remarks>
    public double StandY(int node) => FloorY(node) - FeetOffset;

    /// <summary>The node's span in world pixels, as stand positions.</summary>
    public (double Left, double Right) WorldSpan(int node) =>
        (ColumnWorldX(_nodes[node].X0), ColumnWorldX(_nodes[node].X1));

    // --- lookup --------------------------------------------------------------

    /// <summary>
    /// <c>navNodeFromWorld.gml</c>: the node a character standing at
    /// <paramref name="worldX"/>, <paramref name="worldY"/> is on, or -1.
    /// </summary>
    /// <remarks>
    /// <b>Not "the nearest node".</b> It is the best-ranked one inside a
    /// tolerance of three rows and two columns, ranked <c>dy * 4 + dx</c> — so
    /// height is weighted above horizontal distance, because two surfaces
    /// stacked a few rows apart are a much likelier confusion than two side by
    /// side. Neither axis demands an exact hit: a bot mid-step still has to
    /// resolve to the surface it is plainly on, and returning -1 there makes the
    /// path follower think it fell off the graph and re-plan, which is how a bot
    /// thrashes in place.
    /// <para>
    /// <b>The feet row is <em>rounded</em>, not floored, and that one word is
    /// the difference between a bot that walks a staircase and one that hops up
    /// it.</b> A node's feet sit exactly on a row boundary, but GG2 rests a
    /// character with its feet a pixel <em>above</em> the surface — so flooring
    /// puts a stationary bot in the row above the one it is plainly standing in.
    /// On a wide floor nothing notices; on stepped terrain the node one row up
    /// is also one column across, wins the tie-break, and the bot believes it is
    /// on the step behind the one it is on. On <c>ctf_truefort</c>, resolving
    /// y=835 at n616's own stand position gives n616, but resolving 834 — where
    /// the engine actually rests the body — gives n608, the step above.
    /// </para>
    /// <para>
    /// <c>gg2-agent/tools/navgraph.js</c> floors instead of rounding here — it
    /// documents the format but is wrong about this line. The reference is the
    /// specification (CLAUDE.md, "the reference is the specification, not this
    /// repo"), so this rounds, and <c>NavGraphTests</c> gates the difference.
    /// </para>
    /// <para>
    /// The round is <see cref="Gml.Round"/>, half to even, because that is what
    /// GM8's <c>round()</c> is. On a half-integer feet row the two spellings
    /// disagree by a whole row.
    /// </para>
    /// </remarks>
    public int NodeFromWorld(double worldX, double worldY)
    {
        var mx = AnchorColumn(worldX);
        var my = (int)Gml.Round((worldY + FeetOffset) / (double)NavConstants.CellSize);

        var best = -1;
        var bestRank = int.MaxValue;

        // Only seven mask rows can pass the dy test, so the search goes through
        // the row index rather than reading every node to reject almost all of
        // them: a linear scan over ctf_truefort's 687 nodes costs real time
        // once botNodeSnap starts calling this up to 151 times a decision.
        var rowLow = Math.Max(0, my - NavConstants.BoxH - 3);
        var rowHigh = Math.Min(_rowStart.Length - 1, my - NavConstants.BoxH + 3);

        for (var row = rowLow; row <= rowHigh; row++)
        {
            var i = _rowStart[row];
            if (i < 0)
                continue;

            // Constant across the row, so it is hoisted out of the walk.
            var dy = Math.Abs(row + NavConstants.BoxH - my);

            while (i < _nodes.Length && _nodes[i].Row == row)
            {
                ref readonly var node = ref _nodes[i];

                var dx = 0;
                if (mx < node.X0)
                    dx = node.X0 - mx;
                else if (mx > node.X1)
                    dx = mx - node.X1;

                if (dx <= 2)
                {
                    var rank = (dy * 4) + dx;

                    // Strict `<`: the first node reached at a given rank wins,
                    // and the rows are walked in ascending order, so the answer
                    // is the lowest index — which is what makes the row index
                    // and a whole-graph scan agree.
                    if (rank < bestRank)
                    {
                        bestRank = rank;
                        best = i;
                    }
                }

                i++;
            }
        }

        return best;
    }

    /// <summary>
    /// <c>botObjectiveUpdate</c>'s downward search: the first node resolvable
    /// below a map object, and where a bot would stand on it.
    /// </summary>
    /// <remarks>
    /// <b>Map objects are not anchored the way a character is</b>, and no
    /// single offset generalises across object types — <c>koth_corinth</c>'s
    /// capture-zone marker floats 48px above its floor and <c>koth_harvest</c>'s
    /// floats 66px. Taking the marker's own y gives -1. So
    /// search downward for the first node that resolves and derive the goal from
    /// <em>that</em> node, which is what makes the same call resolve the same way
    /// every time.
    /// </remarks>
    public NavSnap SnapObjective(double worldX, double worldY)
    {
        for (var drop = 0; drop <= NavConstants.MaxFall * NavConstants.CellSize; drop += NavConstants.CellSize)
        {
            var node = NodeFromWorld(worldX, worldY + drop);
            if (node < 0)
                continue;

            var column = Math.Clamp(AnchorColumn(worldX), _nodes[node].X0, _nodes[node].X1);
            return new NavSnap(node, drop, ColumnWorldX(column), StandY(node));
        }

        return new NavSnap(-1, -1, worldX, worldY);
    }

    // --- gates ---------------------------------------------------------------

    /// <summary>
    /// <c>navGatePassable.gml</c>: whether this traveller may pass a gate code.
    /// </summary>
    /// <remarks>
    /// Mirrors <c>charSetSolids.gml</c>, the only place the engine decides it,
    /// which sets solidity from the <em>gate's</em> point of view with the
    /// character as <c>other</c>:
    /// <code>
    /// TeamGate   solid = (team != other.team or other.intel)
    /// IntelGate  solid = (team != other.team and other.intel)
    /// </code>
    /// Inverted: <b>your own team gate is open unless you are carrying intel
    /// out of it</b>, and an enemy intel gate is closed only to a carrier. That
    /// first rule is why a CTF return leg is a different query from the outbound
    /// one — checking only the outbound leg lets a carrier fetch the flag on
    /// <c>ctf_conflict</c> and never carry it home.
    /// <para>
    /// An unknown code refuses passage. A graph built by a newer generator, or
    /// a corrupt row, should make a bot refuse to walk through something — worse
    /// behaviour, not a broken game.
    /// </para>
    /// </remarks>
    public static bool GatePassable(NavGate gate, Team team, bool hasIntel, bool setupGatesClosed) =>
        gate switch
        {
            NavGate.None => true,
            NavGate.TeamRed => team == Team.Red && !hasIntel,
            NavGate.TeamBlue => team == Team.Blue && !hasIntel,
            NavGate.IntelRed => team == Team.Red || !hasIntel,
            NavGate.IntelBlue => team == Team.Blue || !hasIntel,
            NavGate.Setup => !setupGatesClosed,
            _ => false,
        };

    /// <summary>
    /// Whether this traveller may take <paramref name="edge"/>.
    /// </summary>
    /// <remarks>
    /// <b>The test is on the EDGE and not on the node it arrives at</b>, which
    /// looks like an omission and is the reference's rule —
    /// <c>navFindPath.gml</c>: "an arc can cross a gate without landing on it —
    /// the generator stamps an edge that passes through a gate with that gate,
    /// so the check below covers both". Across every shipped graph, no edge
    /// lands on a gated node without carrying that gate itself — <see cref="Nodes"/>,
    /// and <c>NavGraphFixtureTests.NoEdgeLandsOnAGatedNodeUnstamped</c>, which is
    /// what notices if a future generator stops.
    /// <para>
    /// It also falls out of this that <b>a bot standing inside a gate it may
    /// not pass still gets edges out</b> — one that has just picked up the intel,
    /// or anyone the setup gates shut around. Those edges arrive somewhere
    /// ungated and are never refused. Adding the landing-node test would not
    /// break that, but adding a test on the node the bot is <em>standing</em> on
    /// would trap it.
    /// </para>
    /// </remarks>
    public bool EdgePassable(in NavEdge edge, Team team, bool hasIntel, bool setupGatesClosed) =>
        GatePassable(edge.Gate, team, hasIntel, setupGatesClosed);

    /// <summary>
    /// Whether this traveller may take <paramref name="edge"/> at all: the gate
    /// and the class arcs together.
    /// </summary>
    /// <remarks>
    /// <b>The one copy of the rule, and it has to stay the one copy.</b>
    /// <see cref="NavSearch"/> applies it per <em>edge</em> while choosing a
    /// route; <see cref="PathFollower"/> applies it again when it re-resolves the
    /// hop it is steering. Those two answering differently is not a cosmetic
    /// inconsistency — it is a bot flying an arc its own route never costed.
    /// <para>
    /// <b>Refusing is the DEFAULT for every class arc.</b> The failure mode of
    /// the opposite is a heavy routed over something it physically cannot fly,
    /// which is worse than a longer walk.
    /// </para>
    /// </remarks>
    public static bool EdgeRoutable(in NavEdge edge, in NavQuery query) =>
        GatePassable(edge.Gate, query.Team, query.HasIntel, query.SetupGatesClosed)
        && (query.CanDoubleJump || edge.Type != NavEdgeType.DoubleJump)
        && (query.CanStickyJump || edge.Type != NavEdgeType.StickyJump)
        && (query.CanRocketJump || edge.Type != NavEdgeType.RocketJump);

    /// <summary>
    /// <c>navEdgeKey.gml</c>: one number naming a directed edge by its
    /// endpoints.
    /// </summary>
    /// <remarks>
    /// Keyed on the endpoints rather than on the edge's index because an index
    /// only means anything to one build of the graph, and the thing that holds
    /// these — a bot's anti-thrash blacklist — outlives individual searches.
    /// Endpoints are no more stable across a rebuild, but a blacklist entry
    /// expires in seconds and a rebuild only happens on a map change, which
    /// clears the bots' paths anyway.
    /// <para>
    /// The multiplier is comfortably above any node count a real map produces
    /// — the largest shipped graph, <c>cp_dirtbowl</c>, comes to 833 — and is
    /// checked rather than assumed, because a collision here silently blacklists
    /// or crowds the wrong edge.
    /// </para>
    /// </remarks>
    public static long EdgeKey(int fromNode, int toNode) => ((long)fromNode * KeyStride) + toNode;

    /// <summary>The <c>navEdgeKey</c> multiplier. See <see cref="EdgeKey"/>.</summary>
    public const int KeyStride = 100_000;
}

/// <summary>What <see cref="NavGraph.SnapObjective"/> found.</summary>
/// <param name="Node">The node, or -1 if the search fell through.</param>
/// <param name="Drop">How far down it had to look, in pixels, or -1.</param>
/// <param name="GoalX">Where a bot should stand: clamped into the node's span.</param>
/// <param name="GoalY">The stand y — the chest, not the floor.</param>
public readonly record struct NavSnap(int Node, int Drop, double GoalX, double GoalY);
