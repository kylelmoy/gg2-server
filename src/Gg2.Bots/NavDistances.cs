namespace Gg2.Bots;

/// <summary>
/// The exact cheapest cost between every pair of nodes, precomputed — A\*'s
/// heuristic, replacing a straight-line guess with the answer.
/// </summary>
/// <remarks>
/// <b>This is the useful half of "precompute Dijkstra for all pairs".</b> The
/// other half — precomputing the ROUTES and looking them up instead of
/// searching — is far faster and does not work here: <see cref="BotOccupancy"/>
/// makes an edge's cost depend on what the team is walking this tick, so a
/// stored route is only still optimal if it happens to touch nothing contended,
/// which on realistic traffic is under 20% of queries. A stored <em>distance</em>
/// has no such problem, because it is only ever used as a lower bound.
/// <para>
/// <b>Admissible under occupancy, the blacklist and the gates alike, and that
/// is the whole argument.</b> Every one of those can only ever make a route
/// dearer — occupancy multiplies by <c>1 + BotConstants.OccupancyCost × crowd</c>,
/// a blacklisted edge is removed, a closed gate removes one — so the cost
/// measured here on the fully permissive graph is at or below the true remaining
/// cost for every asker. <see cref="NavSearch"/> still reads the live occupancy
/// map on every edge exactly as before; this only tells it which way to look.
/// </para>
/// <para>
/// <b>One table for every <see cref="NavQuery"/>, built with every edge
/// routable.</b> A per-class table would be tighter — a walker's true distances
/// are well above these wherever the graph leans on rocket jumps, which are 14%
/// of the shipped edges — but only the most permissive graph gives a bound valid
/// for all askers, and a table per ability profile is an optimisation to make
/// once the looseness is measured rather than assumed.
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
    private readonly float[] _cost;
    private readonly int _nodes;

    private NavDistances(float[] cost, int nodes)
    {
        _cost = cost;
        _nodes = nodes;
    }

    /// <summary>How many nodes the table was built for.</summary>
    public int NodeCount => _nodes;

    /// <summary>Bytes the table occupies.</summary>
    public long Bytes => (long)_cost.Length * sizeof(float);

    /// <summary>
    /// The cheapest cost from <paramref name="from"/> to <paramref name="goal"/>,
    /// or <see cref="float.PositiveInfinity"/> if there is no route at all.
    /// </summary>
    public float Between(int from, int goal) =>
        (uint)from >= (uint)_nodes || (uint)goal >= (uint)_nodes
            ? float.PositiveInfinity
            : _cost[(from * _nodes) + goal];

    /// <summary>
    /// Builds the table — one reverse Dijkstra per node.
    /// </summary>
    /// <remarks>
    /// <b>Reverse, so one search serves every source for a given goal</b>, which
    /// is the direction the heuristic is asked in: <c>Between(n, goal)</c> for
    /// many <c>n</c> and one <c>goal</c>.
    /// <para>
    /// ⚠️ <b>Every stored value is rounded DOWN to the next representable
    /// float.</b> A <c>double</c> cost narrowed to <c>float</c> rounds to
    /// nearest, which can round <em>up</em> — and a heuristic a fraction above
    /// the true remaining cost is inadmissible, which costs optimality for
    /// nothing. <see cref="MathF.BitDecrement"/> is the cheapest way to be sure.
    /// The table is a <c>float</c> at all because it is N² and the precision
    /// buys nothing: it is an estimate feeding a comparison, not a cost that
    /// accumulates.
    /// </para>
    /// </remarks>
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

    /// <param name="doubleJump">Include <see cref="NavEdgeType.DoubleJump"/> arcs.</param>
    /// <param name="rocketJump">Include <see cref="NavEdgeType.RocketJump"/> arcs.</param>
    /// <param name="stickyJump">Include <see cref="NavEdgeType.StickyJump"/> arcs.</param>
    /// <remarks>
    /// ⚠️ <b>A table is admissible only for askers whose own edges are a SUBSET
    /// of the ones it was built with.</b> Including every arc gives a bound valid
    /// for everybody and is what <see cref="Derive(NavGraph)"/> does; excluding
    /// the arcs a given class cannot take gives a much TIGHTER bound for that
    /// class and a wrong one for any class that can take them. Rocket jumps
    /// alone are 14% of the shipped edges and they are shortcuts, so the
    /// permissive table badly understates a walker's real distances.
    /// </remarks>
    public static NavDistances Derive(
        NavGraph graph, bool doubleJump, bool rocketJump, bool stickyJump)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var n = graph.NodeCount;
        var cost = new float[(long)n * n <= int.MaxValue ? n * n : 0];

        if (cost.Length == 0 && n > 0)
            throw new ArgumentException($"a {n}-node graph needs {(long)n * n} entries", nameof(graph));

        Array.Fill(cost, float.PositiveInfinity);

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
        var open = new PriorityQueue<int, double>();

        for (var goal = 0; goal < n; goal++)
        {
            Array.Fill(dist, double.PositiveInfinity);
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
                    var next = du + weight[p];

                    if (next >= dist[v])
                        continue;

                    dist[v] = next;
                    open.Enqueue(v, next);
                }
            }

            for (var v = 0; v < n; v++)
            {
                cost[(v * n) + goal] = double.IsPositiveInfinity(dist[v])
                    ? float.PositiveInfinity
                    : MathF.BitDecrement((float)dist[v]);
            }
        }

        return new NavDistances(cost, n);
    }
}
