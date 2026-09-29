namespace GG2.Bots;

/// <summary>
/// A handful of structurally different corridors through one nav graph, derived
/// from the graph itself and fixed for its lifetime.
/// </summary>
/// <remarks>
/// <b>This exists to take route variety out of the search's live cost function
/// and put it in the scripted layer.</b> <see cref="BotOccupancy"/> spreads a
/// team out by charging more for edges team-mates are currently walking, which
/// works — but it makes an edge's cost depend on what five other bots are doing
/// this tick, so no route can ever be precomputed. A lane is the same idea moved
/// off the clock: the cost field is a property of the <em>graph</em>, so every
/// route through it can be computed once and looked up.
/// <para>
/// <b>⚠️ This is NOT the per-edge jitter that was tried and deleted, and the
/// distinction is the whole design.</b> That jitter failed because it was
/// mean-reverting — over a sixty-edge route every candidate inflated by about
/// the same average, so the ordering barely moved and on <c>ctf_truefort</c> all
/// eight bots got the identical route anyway. What a lane penalises is a whole
/// <em>corridor</em> that the previous lane's routes actually used, found by
/// sweeping the graph rather than by rolling dice. That is structured (what it
/// makes expensive is a real route, not noise), shared (every bot on a lane sees
/// the same field), coarse, and — unlike occupancy — <b>static</b>.
/// </para>
/// <para>
/// <b>Reachability is preserved by construction.</b> Every lane multiplies costs
/// and never removes an edge, so a leg that has a route on lane 0 has one on
/// every lane. That is what makes a lane safe to assign to a bot for its whole
/// life: it can be sent down a worse corridor, never into a dead end.
/// </para>
/// <para>
/// <b>Derived under one query rather than per class.</b> Gates and class arcs
/// change which edges a given bot may use, but a lane is a dispersion device and
/// not a correctness one — a lane that discourages an edge the asker could not
/// take anyway costs nothing. One field per lane serves every
/// <see cref="NavQuery"/>.
/// </para>
/// </remarks>
public sealed class BotLanes
{
    /// <summary>
    /// How much a lane charges for a corridor an earlier lane already claimed.
    /// </summary>
    /// <remarks>
    /// Compounding, so an edge two earlier lanes both used costs
    /// <c>Penalty²</c>. Chosen to sit above <see cref="BotConstants.OccupancyCost"/>'s
    /// effective 1.25–1.5 — a lane has to divert a route on its own, where
    /// occupancy had a whole team's worth of counts stacking up to do it.
    /// </remarks>
    public const double Penalty = 1.75;

    /// <summary>How many legs the derivation sweep costs a lane.</summary>
    /// <remarks>
    /// The same every-seventh-start, every-eleventh-goal stride the occupancy
    /// fixture was swept with, so "a representative leg" means the same thing
    /// here as it does there. Capped because this runs at a map change.
    /// </remarks>
    private const int SweepLegs = 300;

    private readonly double[][] _cost;

    private BotLanes(double[][] cost) => _cost = cost;

    /// <summary>How many lanes there are. Lane 0 is always the honest costs.</summary>
    public int Count => _cost.Length;

    /// <summary>
    /// One cost per edge for <paramref name="lane"/>, indexed as
    /// <see cref="NavGraph.Edges"/> is.
    /// </summary>
    /// <remarks>
    /// <b>Wraps rather than throws.</b> The caller is a roster position and the
    /// roster is longer than the lane count by design — see
    /// <see cref="BotRoles.Lane"/>.
    /// </remarks>
    public ReadOnlyMemory<double> Costs(int lane) =>
        _cost[((lane % _cost.Length) + _cost.Length) % _cost.Length];

    /// <summary>
    /// Derives <paramref name="lanes"/> cost fields for <paramref name="graph"/>.
    /// </summary>
    /// <remarks>
    /// <b>Iterative penalisation.</b> Lane 0 is the graph's own costs. Lane
    /// <c>L</c> starts from those and multiplies by <see cref="Penalty"/> once
    /// for every earlier lane whose swept routes used that edge, so each lane is
    /// pushed off the corridors all its predecessors chose rather than only the
    /// one before it.
    /// <para>
    /// <b>Dijkstra rather than the A\* the bots run.</b> The lanes describe the
    /// graph's structure, and <c>NavConstants.SearchWeight</c> plus the
    /// closed-set skip mean A\* deliberately does not return cheapest routes —
    /// deriving off that would bake this build's search tuning into the map data
    /// and make every lane move the next time the weight is tuned.
    /// </para>
    /// </remarks>
    public static BotLanes Derive(NavGraph graph, int lanes, double penalty = Penalty)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentOutOfRangeException.ThrowIfLessThan(lanes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(penalty, 1.0);

        var cost = new double[lanes][];
        var used = new bool[graph.EdgeCount];

        cost[0] = new double[graph.EdgeCount];
        for (var e = 0; e < graph.EdgeCount; e++)
            cost[0][e] = graph.Edges[e].Cost;

        for (var lane = 1; lane < lanes; lane++)
        {
            Array.Clear(used);
            Sweep(graph, cost[lane - 1], used);

            cost[lane] = new double[graph.EdgeCount];
            for (var e = 0; e < graph.EdgeCount; e++)
                cost[lane][e] = used[e] ? cost[lane - 1][e] * penalty : cost[lane - 1][e];
        }

        return new BotLanes(cost);
    }

    /// <summary>
    /// Marks every edge the cheapest routes over a fixed stride of legs use.
    /// </summary>
    private static void Sweep(NavGraph graph, double[] cost, bool[] used)
    {
        var n = graph.NodeCount;
        var dist = new double[n];
        var from = new int[n];
        var open = new PriorityQueue<int, double>();
        var legs = 0;

        for (var start = 0; start < n && legs < SweepLegs; start += 7)
        {
            // One Dijkstra serves every goal from this start, so the stride over
            // goals costs nothing beyond the walk back.
            Array.Fill(dist, double.PositiveInfinity);
            Array.Fill(from, -1);
            open.Clear();

            dist[start] = 0;
            open.Enqueue(start, 0);

            while (open.TryDequeue(out var u, out var du))
            {
                if (du > dist[u])
                    continue;

                var edgeStart = graph.EdgeStart(u);
                var edges = graph.EdgesFrom(u);

                for (var i = 0; i < edges.Length; i++)
                {
                    var v = edges[i].To;
                    if ((uint)v >= (uint)n)
                        continue;

                    var nd = du + cost[edgeStart + i];
                    if (nd >= dist[v])
                        continue;

                    dist[v] = nd;
                    from[v] = edgeStart + i;
                    open.Enqueue(v, nd);
                }
            }

            for (var goal = 0; goal < n && legs < SweepLegs; goal += 11)
            {
                if (goal == start || double.IsPositiveInfinity(dist[goal]))
                    continue;

                legs++;

                var at = goal;
                var guard = 0;

                while (at != start && guard++ < n + 2)
                {
                    var edge = from[at];
                    if (edge < 0)
                        break;

                    used[edge] = true;
                    at = graph.Edges[edge].From;
                }
            }
        }
    }
}
