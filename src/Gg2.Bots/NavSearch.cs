using Gg2.Protocol;

namespace Gg2.Bots;

/// <summary>
/// Who is asking for a route, and what they can do.
/// </summary>
/// <remarks>
/// Everything in the graph is static — one edge set serves every bot — so the
/// per-asker facts live here and are applied per query. <b>That is the shape
/// the whole nav layer is built on</b>: gate nodes and class-specific arcs are
/// carried in the graph with full connectivity and refused at search time, so a
/// blue bot's search simply cannot expand through a red spawn gate and the same
/// graph still serves the red bots going through it.
/// </remarks>
/// <param name="CanDoubleJump">
/// May take a <see cref="NavEdgeType.DoubleJump"/> arc. In the reference this
/// is read off the <em>character</em>'s <c>canDoublejump</c> rather than tested
/// against the scout class, because that is the field the game itself gates the
/// second jump on — a class list would be a second copy of the fact, and the two
/// would disagree the first time a class gained or lost the ability.
/// </param>
/// <param name="CanRocketJump">
/// May take a <see cref="NavEdgeType.RocketJump"/> arc. <b>Not a property of
/// the class</b>, and not even of the life: it is read off the <em>weapon</em>
/// (firing a rocket at your own feet is a thing a Rocketlauncher does) <b>and</b>
/// carries an hp test, because the arc costs <c>NAV_RJ_DAMAGE</c> to traverse.
/// So a soldier's routes shed their rocket jumps on their own as it takes
/// damage. Strictly greater than the damage, not equal: the damage is dealt
/// before the bot lands, so equal health is a bot that dies in mid-air on an
/// edge it was told to fly.
/// <para>
/// Health is a <em>plan-time</em> gate and cannot be the only one. This
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
    /// <c>&gt;</c>, never <c>&gt;=</c>: "a rocket jump taken at 31hp is a real
    /// thing a player does, and the point of these edges is a bot that reads
    /// like a player" — but at exactly 30 the bot dies in the air.
    /// </remarks>
    public static bool CanAffordRocketJump(double hp) => hp > NavConstants.RjDamage;

    /// <summary>
    /// Whether a character of this hp may be routed over a sticky jump.
    /// </summary>
    /// <remarks>
    /// <b>The game cannot make this refusal and this port can.</b>
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
/// <b>Not thread-safe, on purpose.</b> One search at a time per instance;
/// give each worker its own. Sharing one across a batched policy evaluation
/// would interleave two frontiers into one <c>gScore</c> and produce routes that
/// are wrong rather than slow.
/// </para>
/// </remarks>
/// <summary>
/// How a search uses a <see cref="NavDistances"/> table, when it has one.
/// </summary>
/// <remarks>
/// <b>Both plans return the SAME route</b> — the optimal one under live costs —
/// so the only thing that separates them is what they spend proving it. That is
/// deliberate: it makes the two directly comparable in search cost and in
/// nothing else. <c>docs/TRAPS-BOTS.md</c>, "Two ways to spend a next-hop
/// table".
/// </remarks>
public enum NavPlan
{
    /// <summary>
    /// CPD-Search. The chain priced at live cost is an incumbent and an upper
    /// bound; the search stops as soon as the frontier's lower bound reaches it,
    /// so a perturbed chain still bounds the work instead of being thrown away.
    /// </summary>
    Incumbent,

    /// <summary>
    /// The two-stage design `docs/PROPOSAL-ROUTE-TABLE.md` proposed before the
    /// prior-art search: verify the chain, and on any perturbation discard it and
    /// search from scratch.
    /// </summary>
    /// <remarks>
    /// <b>Kept so the incumbent's contribution can be measured rather than
    /// asserted.</b> It is the same algorithm as <see cref="Incumbent"/> minus
    /// exactly one idea, which is what makes the difference between them
    /// attributable.
    /// </remarks>
    VerifyThenSearch,
}

public sealed class NavSearch
{
    /// <summary>
    /// The pop guard. The graph is a few hundred nodes; a runaway here means a
    /// corrupt edge list, and a hung server is far worse than a missing path.
    /// </summary>
    private const int PopGuard = 100_000;

    /// <summary>
    /// How far below the incumbent's cost a frontier entry may sit and still
    /// prove it, relative.
    /// </summary>
    /// <remarks>
    /// The table's distances are floats rounded DOWN, so on a chain nothing has
    /// perturbed the lower bound sits one float ULP (2^-23) under the live price
    /// of the identical edges, and an exact test would expand the whole chain to
    /// close a gap that is rounding. 1e-6 is an order above that ULP and three
    /// orders below the smallest perturbation this layer makes — one ×1.25 hop
    /// on a sixty-hop route is ~4e-3 — so it never accepts an incumbent a real
    /// cost change should have displaced.
    /// </remarks>
    private const double IncumbentTolerance = 1e-6;

    private readonly NavGraph _graph;
    private readonly double[] _gScore;
    private readonly int[] _cameFrom;
    private readonly bool[] _closed;
    private readonly PriorityQueue<int, double> _open = new();

    // CPD-Search scratch: the live-priced cost of the table's chain from each
    // node (-1 = not walked this search), and the walk's own stack.
    private readonly double[] _liveCost;
    private readonly int[] _walk;

    public NavSearch(NavGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        _graph = graph;
        _gScore = new double[graph.NodeCount];
        _cameFrom = new int[graph.NodeCount];
        _closed = new bool[graph.NodeCount];
        _liveCost = new double[graph.NodeCount];
        _walk = new int[graph.NodeCount];
    }

    /// <summary>How many nodes the last search popped. Diagnostic only.</summary>
    public int Pops { get; private set; }

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
    /// <b>The anti-thrash half of the follower</b>: without it a bot that
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
    /// <b>A per-edge random jitter is not a substitute.</b> It is mean-reverting
    /// — over a sixty-edge route every candidate inflates by about the same
    /// average — so on ctf_truefort all eight bots got the <em>identical</em>
    /// 68-node route: the jitter did not fan the team out, it moved the whole
    /// team onto one slightly worse route. Occupancy works because it is
    /// structured (what it makes expensive is exactly what is crowded), the
    /// same for every bot asking at the same moment, and coarse.
    /// </para>
    /// </param>
    /// <param name="laneCost">
    /// One cost per edge, indexed as <see cref="NavGraph.Edges"/> is, or null to
    /// use the graph's own. <b><see cref="BotLanes"/>' half of route variety</b>:
    /// where <paramref name="occupancy"/> disperses a team by reacting to what
    /// its members are walking this tick, a lane disperses it with a field fixed
    /// for the graph's lifetime — the same dispersion, computable in advance.
    /// <para>
    /// A lane never removes an edge, so it cannot change what is reachable; the
    /// route it returns is the cheapest one <em>under that lane's costs</em>.
    /// </para>
    /// </param>
    /// <param name="distances">
    /// A table matched to this asker's abilities, or null for the straight-line
    /// heuristic. With one, this is CPD-Search: the table's distance is the
    /// heuristic, unweighted, and its next-hop chain priced live is an incumbent
    /// the search proves against — a chain nothing has perturbed comes back at
    /// the first pop. ⚠️ The table must have been built for a SUPERSET of the
    /// asker's arcs (<see cref="NavDistanceSet.For"/> picks one), or the
    /// heuristic overestimates and the route is wrong rather than slow.
    /// </param>
    /// <returns>Whether a route was found.</returns>
    public bool Find(
        int start,
        int goal,
        in NavQuery query,
        List<int> path,
        IReadOnlySet<long>? blocked = null,
        IReadOnlyDictionary<long, int>? occupancy = null,
        ReadOnlySpan<double> laneCost = default,
        NavDistances? distances = null,
        NavPlan plan = NavPlan.Incumbent)
    {
        ArgumentNullException.ThrowIfNull(path);

        path.Clear();
        Pops = 0;

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
        // The node's ROW, not its floor row — the same quantity for every
        // node, so the difference cancels and the distance is the same. Kept as
        // the reference writes it so a reader comparing the two does not have to
        // prove that.
        var goalX = (_graph.Nodes[goal].X0 + _graph.Nodes[goal].X1) / 2.0;
        var goalY = (double)_graph.Nodes[goal].Row;

        // The table's answer for the whole leg. Infinity means the FULLY
        // PERMISSIVE graph cannot get there, so no query can — a single array
        // read instead of expanding the entire reachable component to discover
        // it. On cp_dirtbowl that is most pairs.
        if (distances is not null && float.IsPositiveInfinity(distances.Between(start, goal)))
            return false;

        // Hoisted out of the edge loop for real cost, not tidiness. Both
        // collections are usually EMPTY — a bot's blacklist averages about 0.15
        // entries a search over a match, and the occupancy map is null whenever
        // the caller fields one team — but even an empty one costs the key
        // arithmetic plus an interface-dispatched lookup on every edge the
        // frontier touches. Neither can become non-empty mid-search: nothing in
        // this loop writes to either.
        var hasBlocked = blocked is { Count: > 0 };
        var hasOccupancy = occupancy is { Count: > 0 };

        // Hoisted for the same reason as the two above: an empty lane field is
        // the common case (a bot with no lane, every scenario, every rollout) and
        // the test would otherwise run on every edge the frontier touches.
        var hasLanes = !laneCost.IsEmpty;

        _gScore[start] = 0;

        // CPD-Search (Bono et al., IJCAI 2019) whenever a table is present: the
        // incumbent is the cheapest LIVE-priced next-hop chain seen so far and
        // `bound` is its cost. Seeded from the start node, so a chain nothing
        // has perturbed is returned at the first pop with no expansion at all.
        // docs/TRAPS-BOTS.md, "CPD-Search: the next-hop table as the ANSWER".
        var bound = double.PositiveInfinity;
        var incumbent = -1;

        if (distances is not null)
        {
            Array.Fill(_liveCost, -1);

            var chain = LiveChainCost(start, goal, in query, blocked, occupancy, laneCost, distances);

            if (plan == NavPlan.VerifyThenSearch)
            {
                // ⛔ The whole verification, in one comparison. Each hop is priced
                // at or above the static edge the table's Dijkstra used, so the
                // chain's live cost can only EQUAL its static cost when no hop was
                // gated, blacklisted, out of class, laned or crowded. And when it
                // does, the chain is optimal under live costs: any rival is a path
                // in the permissive graph, so costs at least the static optimum,
                // which is what this chain costs. Walking the hops to check them
                // one at a time would be the same test done longhand.
                if (chain <= distances.Between(start, goal) * (1 + IncumbentTolerance))
                    return ReconstructIncumbent(start, start, goal, path, distances);

                // Perturbed: this plan throws the chain away. `bound` stays
                // infinite and `incumbent` stays -1, so the pop test below can
                // never fire and the expansion loop is a plain unweighted A* on
                // the exact heuristic — which is precisely the arm the incumbent
                // has to beat.
            }
            else
            {
                bound = chain;
                incumbent = start;
            }

            // Priced at g + h so the incumbent test can fire on the first pop.
            // Without a table the sole first entry's priority is irrelevant and
            // stays 0, as the reference has it.
            _open.Enqueue(start, distances.Between(start, goal));
        }
        else
        {
            _open.Enqueue(start, 0);
        }

        var current = -1;
        var guard = 0;

        while (_open.Count > 0)
        {
            if (++guard > PopGuard)
                break;

            _open.TryDequeue(out current, out var f);
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

            // The incumbent is provably as cheap as anything still to come:
            // every remaining entry's f is at least this one's, every f is a
            // lower bound on a route through that node, and the incumbent is a
            // real route at `bound`. Tested before the closed check on purpose —
            // a stale entry's f bounds the rest of the frontier just as well.
            if (f * (1 + IncumbentTolerance) >= bound)
                return ReconstructIncumbent(start, incumbent, goal, path, distances!);

            // A stale copy of a node that has already been expanded.
            if (_closed[current])
                continue;

            _closed[current] = true;

            // Indexed rather than a foreach because a lane's cost field is keyed
            // by the edge's position in the flat array, which a slice cannot say.
            var edgeStart = _graph.EdgeStart(current);
            var edges = _graph.EdgesFrom(current);

            for (var i = 0; i < edges.Length; i++)
            {
                ref readonly var edge = ref edges[i];

                var next = edge.To;

                if ((uint)next >= (uint)_graph.NodeCount)
                    continue;

                // A closed node is NOT revisited, and this is a deliberate
                // departure from the reference — navFindPath.gml re-opens one
                // when a cheaper route arrives, because the straight-line
                // heuristic is admissible but not *consistent* and skipping is
                // only sound when it is. So this gives up optimality on purpose:
                // routes cost a few percent more on average (worst case ~1.65x),
                // nothing that existed becomes unreachable, and the bots' search
                // cost drops substantially in return.
                //
                // The two halves belong together. An inflated heuristic WITHOUT
                // this makes the search SLOWER, not faster, because an
                // inconsistent heuristic re-opens far more and every re-open is
                // a re-expansion — the opposite of the textbook result, and why
                // NavConstants.SearchWeight cannot be added on its own.
                if (_closed[next])
                    continue;

                // The gate test is on the EDGE and not on the node it arrives
                // at. An arc can cross a gate without landing on it, and the
                // generator stamps such an edge with that gate — so this covers
                // both; across the shipped graphs no edge lands on a gated node
                // without carrying the gate itself
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
                // BOTH halves live in NavGraph.EdgeRoutable and are applied a
                // SECOND time by PathFollower.TryFindEdge, which re-resolves the
                // hop it is steering. Inlining either of them back here would let
                // the two answers drift, and a follower steering an edge this
                // search refused is a bot flying an arc its route never costed.
                if (!NavGraph.EdgeRoutable(in edge, in query))
                    continue;

                var cost = hasLanes ? laneCost[edgeStart + i] : edge.Cost;

                // The key is only built when somebody is going to look it up.
                if (hasBlocked || hasOccupancy)
                {
                    var key = NavGraph.EdgeKey(current, next);

                    if (hasBlocked && blocked!.Contains(key))
                        continue;

                    if (hasOccupancy && occupancy!.TryGetValue(key, out var crowd))
                        cost *= 1 + (BotConstants.OccupancyCost * crowd);
                }

                var tentative = _gScore[current] + cost;
                var existing = _gScore[next];

                // `e >= 0 and e <= tentative`: -1 is "not reached yet", so a
                // first visit always passes and an equal cost never does.
                if (existing >= 0 && existing <= tentative)
                    continue;

                _gScore[next] = tentative;
                _cameFrom[next] = current;

                if (distances is null)
                {
                    var nextX = (_graph.Nodes[next].X0 + _graph.Nodes[next].X1) / 2.0;
                    var nextY = (double)_graph.Nodes[next].Row;

                    var estimate = Gml.PointDistance(nextX, nextY, goalX, goalY);

                    // WEIGHTED. NavConstants.SearchWeight inflates the estimate,
                    // which makes the frontier commit to the goal's direction
                    // sooner and expand fewer nodes for a route that may be
                    // longer. The trade, and why it is 1.3 rather than more, is
                    // recorded there.
                    _open.Enqueue(next, tentative + (NavConstants.SearchWeight * estimate));
                }
                else
                {
                    // The exact remaining cost on the table's graph — at or
                    // below the true remainder for this asker, whatever the gates,
                    // the blacklist and the crowd have done to it.
                    var exact = distances.Between(next, goal);

                    // Nothing reaches the goal from there, so nothing routed
                    // through it ever can. The straight-line heuristic has no way
                    // to know this and expands the dead end in full.
                    if (float.IsPositiveInfinity(exact))
                        continue;

                    // UNWEIGHTED, and that is a decision. An exact relaxed
                    // distance is consistent, so plain A* on it expands only what
                    // lies within the perturbation of the cheapest route and the
                    // closed-set skip above is sound rather than a licensed
                    // shortcut. Carrying SearchWeight across would also let the
                    // incumbent test below accept an UNPERTURBED chain the moment
                    // it is within 30% — which every crowded chain is — and erase
                    // the dispersion occupancy exists to buy. TRAPS-BOTS,
                    // "CPD-Search", has the measurements.
                    _open.Enqueue(next, tentative + exact);

                    // The table's chain from here, priced at what this asker
                    // would pay now, is a real route: the cheapest such is the
                    // incumbent, and its cost is what the pop test proves
                    // against. ⚠️ Skipped entirely under VerifyThenSearch — the
                    // chain walk is most of what the incumbent COSTS, so leaving
                    // it in would charge that arm for a bound it never uses and
                    // flatter the comparison.
                    if (plan == NavPlan.Incumbent)
                    {
                        var live = tentative
                            + LiveChainCost(next, goal, in query, blocked, occupancy, laneCost, distances);

                        if (live < bound)
                        {
                            bound = live;
                            incumbent = next;
                        }
                    }
                }
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
    /// <b>A chain that does not reach the start is reported as no path at
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
    private bool Reconstruct(int start, int end, List<int> path)
    {
        path.Add(end);

        var at = end;
        var guard = 0;

        while (at != start && guard < _graph.NodeCount + 2)
        {
            at = _cameFrom[at];
            if (at < 0)
                break;

            path.Add(at);
            guard++;
        }

        // Walked goal-first and turned round at the end, rather than
        // front-inserted the way the reference writes it. Insert(0, …) shifts
        // the whole list on every step, so a sixty-node route moved about two
        // thousand elements to produce sixty — quadratic in a route length the
        // search already has in hand. The order this leaves behind is the same
        // one, including the path[0] guard below.
        path.Reverse();

        if (path[0] != start)
        {
            path.Clear();
            return false;
        }

        return true;
    }

    /// <summary>
    /// The incumbent: the search's own route to <paramref name="node"/>, then
    /// the table's chain from there to the goal.
    /// </summary>
    /// <remarks>
    /// The chain priced finite when it became the incumbent, which means every
    /// hop had a routable, unblocked edge at that moment — and nothing in a
    /// search mutates the graph, the blacklist or the crowd — so the route
    /// handed back is one the follower can take, hop for hop. A chain that does
    /// not reach the goal is reported as no route at all, for the same reason
    /// <see cref="Reconstruct"/> gives.
    /// </remarks>
    private bool ReconstructIncumbent(int start, int node, int goal, List<int> path, NavDistances distances)
    {
        if (!Reconstruct(start, node, path))
            return false;

        var at = node;
        var guard = 0;

        while (at != goal)
        {
            at = distances.NextHop(at, goal);

            if (at < 0 || ++guard > _graph.NodeCount)
            {
                path.Clear();
                return false;
            }

            path.Add(at);
        }

        return true;
    }

    /// <summary>
    /// The table's chain from <paramref name="node"/> to the goal, priced at
    /// what THIS asker would pay for it now — or infinity if a hop is gated,
    /// blacklisted or beyond the asker's class.
    /// </summary>
    /// <remarks>
    /// Memoised per search in <c>_liveCost</c>, so a chain walked from one node
    /// is not walked again from any node on it: the walking a search does is
    /// bounded by the nodes its chains touch, not by hops × expansions.
    /// Iterative where the paper recurses — a chain can be the whole graph long
    /// and the stack is not the place for it.
    /// <para>
    /// <b>At or above <see cref="NavDistances.Between"/> by construction.</b>
    /// Each hop is priced at the cheapest ROUTABLE edge between its endpoints
    /// under live cost, which is at or above the cheapest in-table static edge
    /// the Dijkstra used: routable ⊆ in-table, and live ≥ static. "Cheapest"
    /// has to be said because two edges can join one pair of nodes — a walk and
    /// a jump — and pricing the wrong one would hand back a route dearer than
    /// the one the follower then flies.
    /// </para>
    /// </remarks>
    private double LiveChainCost(
        int node,
        int goal,
        in NavQuery query,
        IReadOnlySet<long>? blocked,
        IReadOnlyDictionary<long, int>? occupancy,
        ReadOnlySpan<double> laneCost,
        NavDistances distances)
    {
        if (node == goal)
            return 0;

        if (_liveCost[node] >= 0)
            return _liveCost[node];

        var depth = 0;
        var at = node;

        while (at != goal && at >= 0 && _liveCost[at] < 0 && depth < _walk.Length)
        {
            _walk[depth++] = at;
            at = distances.NextHop(at, goal);
        }

        // What the rest costs from where the walk stopped: nothing at the goal,
        // the memo at a node already walked, and infinity off the end of a
        // broken chain — no hop, or a cycle. Neither can come out of Derive,
        // and both are refused rather than trusted.
        var tail = at == goal ? 0.0
            : at >= 0 && _liveCost[at] >= 0 ? _liveCost[at]
            : double.PositiveInfinity;

        for (var i = depth - 1; i >= 0; i--)
        {
            var from = _walk[i];
            var to = i + 1 < depth ? _walk[i + 1] : at;

            tail += LiveEdgeCost(from, to, in query, blocked, occupancy, laneCost);
            _liveCost[from] = tail;
        }

        return _liveCost[node];
    }

    /// <summary>
    /// What this asker pays now for the cheapest routable edge from
    /// <paramref name="from"/> to <paramref name="to"/>, or infinity.
    /// </summary>
    /// <remarks>
    /// The same pricing the frontier loop in <see cref="Find"/> applies — gate
    /// and class via <see cref="NavGraph.EdgeRoutable"/>, then the lane's cost
    /// or the graph's, then the blacklist and the crowd by edge key. The two
    /// drifting apart would make the incumbent's price a lie, which
    /// <c>NavSearchTests</c> checks by re-pricing what comes back.
    /// </remarks>
    private double LiveEdgeCost(
        int from,
        int to,
        in NavQuery query,
        IReadOnlySet<long>? blocked,
        IReadOnlyDictionary<long, int>? occupancy,
        ReadOnlySpan<double> laneCost)
    {
        var crowd = 0;

        if (blocked is { Count: > 0 } || occupancy is { Count: > 0 })
        {
            // One key covers every edge between the pair, so a blacklisted pair
            // is refused whole and a crowded pair is priced whole.
            var key = NavGraph.EdgeKey(from, to);

            if (blocked is { Count: > 0 } && blocked.Contains(key))
                return double.PositiveInfinity;

            if (occupancy is { Count: > 0 })
                occupancy.TryGetValue(key, out crowd);
        }

        var best = double.PositiveInfinity;
        var edgeStart = _graph.EdgeStart(from);
        var edges = _graph.EdgesFrom(from);

        for (var i = 0; i < edges.Length; i++)
        {
            ref readonly var edge = ref edges[i];

            if (edge.To != to || !NavGraph.EdgeRoutable(in edge, in query))
                continue;

            var cost = laneCost.IsEmpty ? edge.Cost : laneCost[edgeStart + i];

            if (crowd > 0)
                cost *= 1 + (BotConstants.OccupancyCost * crowd);

            if (cost < best)
                best = cost;
        }

        return best;
    }
}
