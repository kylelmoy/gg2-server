using Gg2.Protocol;

namespace Gg2.Bots;

/// <summary>
/// Who is asking for a route, and what they can do.
/// </summary>
/// <remarks>
/// Everything in the graph is static — one edge set serves every bot — so the
/// per-asker facts live here and are applied per query. ⚠️ <b>That is the shape
/// the whole nav layer is built on</b>: gate nodes and class-specific arcs are
/// carried in the graph with full connectivity and refused at search time, so a
/// blue bot's search simply cannot expand through a red spawn gate and the same
/// graph still serves the red bots going through it.
/// </remarks>
/// <param name="CanDoubleJump">
/// May take a <see cref="NavEdgeType.DoubleJump"/> arc. ⚠️ In the reference this
/// is read off the <em>character</em>'s <c>canDoublejump</c> rather than tested
/// against the scout class, because that is the field the game itself gates the
/// second jump on — a class list would be a second copy of the fact, and the two
/// would disagree the first time a class gained or lost the ability.
/// </param>
/// <param name="CanRocketJump">
/// May take a <see cref="NavEdgeType.RocketJump"/> arc. ⚠️ <b>Not a property of
/// the class</b>, and not even of the life: it is read off the <em>weapon</em>
/// (firing a rocket at your own feet is a thing a Rocketlauncher does) <b>and</b>
/// carries an hp test, because the arc costs <c>NAV_RJ_DAMAGE</c> to traverse.
/// So a soldier's routes shed their rocket jumps on their own as it takes
/// damage. ⚠️ Strictly greater than the damage, not equal: the damage is dealt
/// before the bot lands, so equal health is a bot that dies in mid-air on an
/// edge it was told to fly.
/// <para>
/// ⚠️ Health is a <em>plan-time</em> gate and cannot be the only one. This
/// refuses every rocket-jump edge to a bot that cannot afford one; it has no way
/// to say "you can afford two of the three on this route". The follower has to
/// re-ask at each takeoff.
/// </para>
/// </param>
public readonly record struct NavQuery(
    Team Team,
    bool HasIntel = false,
    bool SetupGatesClosed = false,
    bool CanDoubleJump = false,
    bool CanRocketJump = false,
    bool CanStickyJump = false)
{
    /// <summary>
    /// Whether a character of this hp may be routed over a rocket jump.
    /// </summary>
    /// <remarks>
    /// <c>botPathPlan.gml</c>'s test, extracted so the strictness cannot drift.
    /// ⚠️ <c>&gt;</c>, never <c>&gt;=</c>: "a rocket jump taken at 31hp is a real
    /// thing a player does, and the point of these edges is a bot that reads
    /// like a player" — but at exactly 30 the bot dies in the air.
    /// </remarks>
    public static bool CanAffordRocketJump(double hp) => hp > NavConstants.RjDamage;

    /// <summary>
    /// Whether a character of this hp may be routed over a sticky jump.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The game cannot make this refusal and this port can.</b>
    /// <c>navFindPath</c> takes <c>canDouble</c> and <c>canRocket</c> and has no
    /// <c>canSticky</c>, so a cache containing type-7 edges routes <em>every
    /// class</em> over them in the stock game — a medic walking to a takeoff to
    /// wait for a mine boost no medic can produce. That asymmetry is the whole
    /// argument for `gg2-nav-gen` emitting them only behind a flag, into a cache
    /// the game never reads. See its NOTES-STICKYJUMP.md.
    /// </remarks>
    public static bool CanAffordStickyJump(double hp) => hp > NavConstants.SjDamage;
}

/// <summary>
/// A\* over a <see cref="NavGraph"/> — <c>Scripts/BotNav/navFindPath.gml</c>.
/// </summary>
/// <remarks>
/// <b>An instance rather than a static method, and the scratch is reused.</b>
/// The reference allocates four <c>ds_grid</c>s and a <c>ds_priority</c> per
/// query and frees them again; a bot re-plans on a timer and twelve of them
/// share a server, so here the arrays are sized to the graph once and cleared
/// per search. docs/BOTAI.md item 1's no-garbage rule is about the tick path and
/// a search is not on it, but a rollout that re-plans is, and the shape costs
/// nothing.
/// <para>
/// ⚠️ <b>Not thread-safe, on purpose.</b> One search at a time per instance;
/// give each worker its own. Sharing one across a batched policy evaluation
/// would interleave two frontiers into one <c>gScore</c> and produce routes that
/// are wrong rather than slow.
/// </para>
/// </remarks>
public sealed class NavSearch
{
    /// <summary>
    /// The pop guard. ⚠️ The graph is a few hundred nodes; a runaway here means a
    /// corrupt edge list, and a hung server is far worse than a missing path.
    /// </summary>
    private const int PopGuard = 100_000;

    private readonly NavGraph _graph;
    private readonly double[] _gScore;
    private readonly int[] _cameFrom;
    private readonly bool[] _closed;
    private readonly PriorityQueue<int, double> _open = new();

    public NavSearch(NavGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        _graph = graph;
        _gScore = new double[graph.NodeCount];
        _cameFrom = new int[graph.NodeCount];
        _closed = new bool[graph.NodeCount];
    }

    /// <summary>How many nodes the last search popped. Diagnostic only.</summary>
    public int Pops { get; private set; }

    /// <summary>
    /// How many closed nodes the last search re-opened. Diagnostic only.
    /// </summary>
    /// <remarks>
    /// Worth reporting because it is the measure of how inconsistent the
    /// heuristic turned out to be, which <see cref="Find"/>'s occupancy argument
    /// makes worse on purpose. The reference measured 113 → 50 on one leg when
    /// occupancy went in: the extra work is a wider frontier, not thrashing.
    /// </remarks>
    public int Reopens { get; private set; }

    /// <summary>
    /// Finds the cheapest route from <paramref name="start"/> to
    /// <paramref name="goal"/> for this asker.
    /// </summary>
    /// <param name="path">
    /// Cleared and filled with node indices, start to goal inclusive. Left empty
    /// when there is no route.
    /// </param>
    /// <param name="blocked">
    /// Edge keys (<see cref="NavGraph.EdgeKey"/>) to refuse, or null.
    /// ⚠️ <b>The anti-thrash half of the follower</b>: without it a bot that
    /// wedges in a doorway re-plans, is handed the same route it just failed, and
    /// wedges again for ever. It is per bot, which is why it is a parameter and
    /// not part of the graph — and a set rather than a cost multiplier, which is
    /// what keeps the heuristic admissible.
    /// </param>
    /// <param name="occupancy">
    /// Edge key → how many team-mates are already walking it, or null. An edge in
    /// it costs <c>(1 + BOT_OCCUPANCY_COST × count)</c> times its honest cost, so
    /// a team fans out across whatever alternatives the map offers.
    /// <para>
    /// ⚠️ <b>A per-edge random jitter is not a substitute and was deleted from the
    /// reference.</b> It is mean-reverting — over a sixty-edge route every
    /// candidate inflates by about the same average — so on ctf_truefort all
    /// eight bots got the <em>identical</em> 68-node route: the jitter did not
    /// fan the team out, it moved the whole team onto one slightly worse route.
    /// Occupancy works because it is structured (what it makes expensive is
    /// exactly what is crowded), the same for every bot asking at the same
    /// moment, and coarse. Do not re-add the jitter.
    /// </para>
    /// </param>
    /// <returns>Whether a route was found.</returns>
    public bool Find(
        int start,
        int goal,
        in NavQuery query,
        List<int> path,
        IReadOnlySet<long>? blocked = null,
        IReadOnlyDictionary<long, int>? occupancy = null)
    {
        ArgumentNullException.ThrowIfNull(path);

        path.Clear();
        Pops = 0;
        Reopens = 0;

        if ((uint)start >= (uint)_graph.NodeCount || (uint)goal >= (uint)_graph.NodeCount)
            return false;

        if (start == goal)
        {
            path.Add(start);
            return true;
        }

        Array.Fill(_gScore, -1);
        Array.Fill(_cameFrom, -1);
        Array.Clear(_closed);
        _open.Clear();

        // The heuristic's target: the goal's surface midpoint, in mask cells.
        // ⚠️ The node's ROW, not its floor row — the same quantity for every
        // node, so the difference cancels and the distance is the same. Kept as
        // the reference writes it so a reader comparing the two does not have to
        // prove that.
        var goalX = (_graph.Nodes[goal].X0 + _graph.Nodes[goal].X1) / 2.0;
        var goalY = (double)_graph.Nodes[goal].Row;

        _gScore[start] = 0;
        _open.Enqueue(start, 0);

        var current = -1;
        var guard = 0;

        while (_open.Count > 0)
        {
            if (++guard > PopGuard)
                break;

            current = _open.Dequeue();
            Pops++;

            // The goal test is on the POP. A node can be queued more than once
            // — this is lazy deletion, not a decrease-key — so the first pop of
            // the goal is the cheapest route to it and there is nothing left to
            // improve.
            //
            // It sits above the closed test because the reference puts it there,
            // and the two orders are *equivalent*: the goal is only ever closed
            // by being expanded, and it is never expanded because this break
            // fires first, so `_closed[goal]` is false at every pop of it.
            // Checked by mutation — swapping them fails nothing — and written
            // down so nobody spends the same ten minutes proving it again.
            if (current == goal)
                break;

            // A stale copy of a node that has already been expanded.
            if (_closed[current])
                continue;

            _closed[current] = true;

            foreach (var edge in _graph.EdgesFrom(current))
            {
                var next = edge.To;

                if ((uint)next >= (uint)_graph.NodeCount)
                    continue;

                // ⚠️ NO closed test on the neighbour. A node that is already
                // closed is still allowed to be improved — see the re-open
                // below. Skipping it is only sound when the heuristic is
                // *consistent*, which is strictly stronger than the
                // admissibility the straight-line distance gives, and the
                // reference measured the difference on honest costs: the
                // ctf_truefort blue leg went from 68 nodes to 60 when the
                // re-open went in.

                // ⚠️ The gate test is on the EDGE and not on the node it arrives
                // at. An arc can cross a gate without landing on it, and the
                // generator stamps such an edge with that gate — so this covers
                // both. Measured on all 24 shipped graphs: of 115,757 edges,
                // none lands on a gated node without carrying the gate itself
                // (NavGraphFixtureTests.NoEdgeLandsOnAGatedNodeUnstamped).
                //
                // It also falls out of this that a bot standing inside a gate it
                // may not pass — one that has just picked up the intel, or
                // anyone the setup gates shut around — still gets edges *out*:
                // those arrive somewhere ungated and are never refused.
                //
                // The class half of the graph has the same shape: those arcs are
                // carried with full connectivity and refused per query — the
                // sticky one being the refusal the reference's own A* cannot make
                // at all, see NavQuery.CanAffordStickyJump.
                //
                // ⚠️ BOTH halves live in NavGraph.EdgeRoutable and are applied a
                // SECOND time by PathFollower.TryFindEdge, which re-resolves the
                // hop it is steering. Inlining either of them back here would let
                // the two answers drift, and a follower steering an edge this
                // search refused is a bot flying an arc its route never costed —
                // measured at 833 node pairs on the shipped graphs.
                if (!NavGraph.EdgeRoutable(in edge, in query))
                    continue;

                var key = NavGraph.EdgeKey(current, next);

                if (blocked is not null && blocked.Contains(key))
                    continue;

                var cost = edge.Cost;

                if (occupancy is not null && occupancy.TryGetValue(key, out var crowd))
                    cost *= 1 + (BotConstants.OccupancyCost * crowd);

                var tentative = _gScore[current] + cost;
                var existing = _gScore[next];

                // `e >= 0 and e <= tentative`: -1 is "not reached yet", so a
                // first visit always passes and an equal cost never does.
                if (existing >= 0 && existing <= tentative)
                    continue;

                _gScore[next] = tentative;
                _cameFrom[next] = current;

                // ⚠️ Re-open it. The push below is ignored by the pop loop while
                // the node is still marked closed, so without this the
                // improvement would be recorded in gScore and never expanded
                // from — the node would keep its old, worse successors.
                if (_closed[next])
                {
                    _closed[next] = false;
                    Reopens++;
                }

                var nextX = (_graph.Nodes[next].X0 + _graph.Nodes[next].X1) / 2.0;
                var nextY = (double)_graph.Nodes[next].Row;

                _open.Enqueue(
                    next,
                    tentative + Gml.PointDistance(nextX, nextY, goalX, goalY));
            }
        }

        if (current != goal)
            return false;

        return Reconstruct(start, goal, path);
    }

    /// <summary>
    /// Walks <c>cameFrom</c> back from the goal, front-inserting as it goes.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A chain that does not reach the start is reported as no path at
    /// all</b>, not as a partial one: an inconsistent <c>cameFrom</c> would
    /// otherwise hand a follower a route that does not start where it is
    /// standing, which is a bot walking confidently in the wrong direction.
    /// <para>
    /// That guard is the reference's and is <b>unreachable by construction
    /// here</b> — every <c>cameFrom</c> write is paired with a <c>gScore</c>
    /// write and the search only reconstructs when the goal was popped. Deleting
    /// it fails no test, which was checked rather than assumed. It is kept
    /// because the cost of being wrong about that is a bot confidently walking
    /// somewhere else, and the cost of the guard is one comparison.
    /// </para>
    /// </remarks>
    private bool Reconstruct(int start, int goal, List<int> path)
    {
        path.Add(goal);

        var at = goal;
        var guard = 0;

        while (at != start && guard < _graph.NodeCount + 2)
        {
            at = _cameFrom[at];
            if (at < 0)
                break;

            path.Insert(0, at);
            guard++;
        }

        if (path[0] != start)
        {
            path.Clear();
            return false;
        }

        return true;
    }
}
