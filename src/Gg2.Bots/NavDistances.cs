namespace Gg2.Bots;

/// <summary>
/// The exact cheapest cost between every pair of nodes, and the first hop of
/// that cheapest route, precomputed — a Compressed Path Database in the sense
/// of Bono et al., IJCAI 2019, uncompressed because the graph is small.
/// </summary>
/// <remarks>
/// <b>Two answers from one reverse Dijkstra.</b> The <em>distance</em> is A\*'s
/// heuristic, replacing a straight-line guess with the exact remaining cost on
/// the graph this table was built for; the <em>next hop</em> is the route
/// itself, one node at a time. <see cref="NavSearch"/> uses the distance as a
/// lower bound and walks the next hops, priced at LIVE cost, as an upper bound
/// and an incumbent — a chain nothing has perturbed is returned without a
/// search, and a chain something has perturbed bounds the search that follows.
/// docs/TRAPS-BOTS.md, "CPD-Search: the next-hop table as the ANSWER".
/// <para>
/// <b>Admissible under occupancy, the blacklist and the gates alike, and that
/// is the whole argument.</b> Every one of those can only ever make a route
/// dearer — occupancy multiplies by <c>1 + BotConstants.OccupancyCost × crowd</c>,
/// a blacklisted edge is removed, a closed gate removes one — so the cost
/// measured here is at or below the true remaining cost for every asker whose
/// arcs are a subset of the table's. The same inequality read the other way is
/// what makes the chain's live-priced cost an upper bound: it is a real route,
/// available to the asker at that price, or it is not available and prices at
/// infinity.
/// </para>
/// <para>
/// ⚠️ <b>A table is admissible only for askers whose own edges are a SUBSET of
/// the ones it was built with.</b> <see cref="NavDistanceSet"/> builds one per
/// ability profile and picks; the permissive table
/// (<see cref="Derive(NavGraph)"/>) is valid for everybody and nearly worthless
/// — 1.06x against 1.36x — because rocket jumps are 14% of the shipped edges
/// and they are shortcuts.
/// </para>
/// <para>
/// <b>Laid out <c>[goal][from]</c>.</b> The heuristic asks
/// <see cref="Between"/> for many <c>from</c> and one <c>goal</c>, and a chain
/// walk asks <see cref="NextHop"/> for a sequence of <c>from</c> and one
/// <c>goal</c>: both read one contiguous row. The <c>[from][goal]</c> layout
/// this shipped with first made every read a stride-N cache miss.
/// </para>
/// <para>
/// <b>It also answers "no route" for free.</b> If the permissive graph cannot
/// get from A to B then no query can, so an unreachable pair costs a single
/// array read instead of a search that expands the whole reachable component
/// before giving up. On <c>cp_dirtbowl</c> that is most pairs.
/// </para>
/// </remarks>
public sealed class NavDistances
{
    /// <summary><see cref="NextHop"/>'s answer when there is no route.</summary>
    public const int None = -1;

    private const ushort NoHop = ushort.MaxValue;

    // Both [goal * _nodes + from].
    private readonly float[] _cost;
    private readonly ushort[] _next;
    private readonly int _nodes;

    private NavDistances(float[] cost, ushort[] next, int nodes)
    {
        _cost = cost;
        _next = next;
        _nodes = nodes;
    }

    /// <summary>How many nodes the table was built for.</summary>
    public int NodeCount => _nodes;

    /// <summary>Bytes the table occupies, both halves.</summary>
    public long Bytes => ((long)_cost.Length * sizeof(float)) + ((long)_next.Length * sizeof(ushort));

    /// <summary>
    /// The cheapest cost from <paramref name="from"/> to <paramref name="goal"/>,
    /// or <see cref="float.PositiveInfinity"/> if there is no route at all.
    /// </summary>
    public float Between(int from, int goal) =>
        (uint)from >= (uint)_nodes || (uint)goal >= (uint)_nodes
            ? float.PositiveInfinity
            : _cost[(goal * _nodes) + from];

    /// <summary>
    /// The node after <paramref name="from"/> on the cheapest route to
    /// <paramref name="goal"/>, or <see cref="None"/> if there is no route or
    /// <paramref name="from"/> is the goal.
    /// </summary>
    /// <remarks>
    /// Following this from any node reaches the goal in at most
    /// <see cref="NodeCount"/> hops: the hops are the parent pointers of one
    /// shortest-path tree, so the chain cannot cycle. Summing the graph's own
    /// cost of each hop reproduces <see cref="Between"/> to float rounding —
    /// <c>NavDistancesTests.TheNextHopChainPricesToTheDistance</c>.
    /// </remarks>
    public int NextHop(int from, int goal)
    {
        if ((uint)from >= (uint)_nodes || (uint)goal >= (uint)_nodes)
            return None;

        var hop = _next[(goal * _nodes) + from];
        return hop == NoHop ? None : hop;
    }

    /// <summary>
    /// Whether an edge belongs in a table built for these abilities.
    /// </summary>
    /// <remarks>
    /// <b>Gates are always OPEN here and that is deliberate.</b> A gate only ever
    /// takes edges away from an asker, so a table built with every gate open is a
    /// superset of any gated asker's graph and therefore still a lower bound for
    /// them. Splitting on gates as well would multiply the tables for a bound
    /// that is already valid.
    /// </remarks>
    private static bool InTable(in NavEdge edge, bool doubleJump, bool rocketJump, bool stickyJump) =>
        edge.Type switch
        {
            NavEdgeType.DoubleJump => doubleJump,
            NavEdgeType.RocketJump => rocketJump,
            NavEdgeType.StickyJump => stickyJump,
            _ => true,
        };

    /// <inheritdoc cref="Derive(NavGraph, bool, bool, bool)"/>
    public static NavDistances Derive(NavGraph graph) => Derive(graph, true, true, true);

    /// <summary>
    /// Builds the table — one reverse Dijkstra per node.
    /// </summary>
    /// <param name="doubleJump">Include <see cref="NavEdgeType.DoubleJump"/> arcs.</param>
    /// <param name="rocketJump">Include <see cref="NavEdgeType.RocketJump"/> arcs.</param>
    /// <param name="stickyJump">Include <see cref="NavEdgeType.StickyJump"/> arcs.</param>
    /// <remarks>
    /// <b>Reverse, so one search serves every source for a given goal</b>, which
    /// is the direction both halves are asked in — and it is what makes the next
    /// hop free: the node a reverse relaxation arrived FROM is the node the
    /// forward route goes TO.
    /// <para>
    /// ⚠️ <b>Every stored distance is rounded DOWN to the next representable
    /// float.</b> A <c>double</c> cost narrowed to <c>float</c> rounds to
    /// nearest, which can round <em>up</em> — and a heuristic a fraction above
    /// the true remaining cost is inadmissible, which costs optimality for
    /// nothing. <see cref="MathF.BitDecrement"/> is the cheapest way to be sure.
    /// The table is a <c>float</c> at all because it is N² and the precision
    /// buys nothing: it is an estimate feeding a comparison, not a cost that
    /// accumulates. The one place that rounding shows is
    /// <see cref="NavSearch"/>'s incumbent test, which carries a tolerance for
    /// exactly it.
    /// </para>
    /// <para>
    /// ⚠️ <b>A table is admissible only for askers whose own edges are a SUBSET
    /// of the ones it was built with.</b> Including every arc gives a bound valid
    /// for everybody and is what <see cref="Derive(NavGraph)"/> does; excluding
    /// the arcs a given class cannot take gives a much TIGHTER bound for that
    /// class and a wrong one for any class that can take them.
    /// </para>
    /// </remarks>
    public static NavDistances Derive(
        NavGraph graph, bool doubleJump, bool rocketJump, bool stickyJump)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var n = graph.NodeCount;

        // ushort hops: the sentinel takes one value, so the ceiling is one below
        // it. The N² float table runs out of int indexing long before this
        // matters (46,340 nodes), but the two limits are separate facts.
        if (n >= NoHop)
            throw new ArgumentException($"a {n}-node graph cannot index its hops as ushort", nameof(graph));

        var cost = new float[(long)n * n <= int.MaxValue ? n * n : 0];

        if (cost.Length == 0 && n > 0)
            throw new ArgumentException($"a {n}-node graph needs {(long)n * n} entries", nameof(graph));

        var next = new ushort[cost.Length];

        Array.Fill(cost, float.PositiveInfinity);
        Array.Fill(next, NoHop);

        // Reverse adjacency in CSR, built once and reused by every Dijkstra.
        var indegree = new int[n];
        for (var e = 0; e < graph.EdgeCount; e++)
            if (InTable(in graph.Edges[e], doubleJump, rocketJump, stickyJump))
                indegree[graph.Edges[e].To]++;

        var start = new int[n + 1];
        for (var i = 0; i < n; i++)
            start[i + 1] = start[i] + indegree[i];

        var fill = (int[])start.Clone();
        var from = new int[start[n]];
        var weight = new double[start[n]];

        for (var e = 0; e < graph.EdgeCount; e++)
        {
            ref readonly var edge = ref graph.Edges[e];

            if (!InTable(in edge, doubleJump, rocketJump, stickyJump))
                continue;

            var slot = fill[edge.To]++;
            from[slot] = edge.From;
            weight[slot] = edge.Cost;
        }

        var dist = new double[n];
        var hop = new int[n];
        var open = new PriorityQueue<int, double>();

        for (var goal = 0; goal < n; goal++)
        {
            Array.Fill(dist, double.PositiveInfinity);
            Array.Fill(hop, None);
            open.Clear();

            dist[goal] = 0;
            open.Enqueue(goal, 0);

            while (open.TryDequeue(out var u, out var du))
            {
                // Lazy deletion, like NavSearch's frontier: a node can be queued
                // more than once and only the first pop is the cheapest.
                if (du > dist[u])
                    continue;

                for (var p = start[u]; p < start[u + 1]; p++)
                {
                    var v = from[p];
                    var via = du + weight[p];

                    if (via >= dist[v])
                        continue;

                    dist[v] = via;
                    hop[v] = u;
                    open.Enqueue(v, via);
                }
            }

            var row = goal * n;

            for (var v = 0; v < n; v++)
            {
                if (double.IsPositiveInfinity(dist[v]))
                    continue;

                // The goal's own entry is exactly zero, not BitDecrement(0),
                // which is a negative denormal.
                cost[row + v] = dist[v] == 0 ? 0f : MathF.BitDecrement((float)dist[v]);

                if (hop[v] != None)
                    next[row + v] = (ushort)hop[v];
            }
        }

        return new NavDistances(cost, next, n);
    }
}
