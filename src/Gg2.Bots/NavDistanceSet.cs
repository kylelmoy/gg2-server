namespace Gg2.Bots;

/// <summary>
/// One <see cref="NavDistances"/> table per ability profile, and the pick of the
/// right one for an asker.
/// </summary>
/// <remarks>
/// <b>This type exists because the permissive table is nearly worthless and a
/// single table is the shape somebody would reach for first.</b> A table built
/// with every arc routable is admissible for everybody — that is why
/// <see cref="NavDistances.Derive(NavGraph)"/> offers it — but it measured
/// <b>1.06x</b> against the straight-line heuristic where a table matched to the
/// asker's own class measured <b>1.36x</b>. Rocket jumps are about 14% of the
/// shipped edges and they are <em>shortcuts</em>, so a table that includes them
/// understates a walker's real remaining cost and the bound goes slack exactly
/// where it was supposed to bite. Wiring in one permissive table is doing the
/// whole job and collecting none of the gain.
/// <para>
/// <b>Only the three class arcs split the set.</b> A <see cref="NavQuery"/> also
/// carries a team, an intel flag and the setup-gate state, and none of them
/// needs a table of its own: every one of those can only ever <em>remove</em>
/// edges from an asker, so a table built with all gates open is a superset of
/// any gated asker's graph and still a lower bound for them. Splitting on gates
/// as well would multiply the tables for a bound that is already valid.
/// </para>
/// <para>
/// <b>Tables are shared when the graph cannot tell the profiles apart.</b> A
/// graph with no <see cref="NavEdgeType.StickyJump"/> arcs in it gives the same
/// answer whether or not the asker may take one, so the profile key is first
/// masked down to the arc types the graph actually contains and one table is
/// built per <em>distinct</em> masked key. On the shipped graphs that is
/// typically two — a walker's and a rocket-jumper's — rather than eight.
/// </para>
/// <para>
/// ⚠️ <b>Read-only once built, and deliberately so.</b> Every bot owns its own
/// <see cref="NavSearch"/> (one per <see cref="PathFollower"/>) but one set
/// serves the whole server, including a threaded <c>eval</c>: nothing here is
/// written after <see cref="Derive"/> returns, so sharing it across workers
/// needs no lock. Do not add a lazily-built table without revisiting that.
/// </para>
/// <para>
/// ⚠️ <b>Indexed by node, so it belongs to one build of one graph.</b>
/// <see cref="ScriptedBot.SetMap"/> drops it exactly as it drops the route, the
/// lane and the occupancy flag.
/// </para>
/// </remarks>
public sealed class NavDistanceSet
{
    private const int DoubleJumpBit = 1;
    private const int RocketJumpBit = 2;
    private const int StickyJumpBit = 4;

    private const int Profiles = 8;

    // Indexed by the raw three-bit profile key; entries repeat where the graph
    // cannot tell two profiles apart.
    private readonly NavDistances[] _byProfile;

    private NavDistanceSet(NavDistances[] byProfile, int tableCount, long bytes)
    {
        _byProfile = byProfile;
        TableCount = tableCount;
        Bytes = bytes;
    }

    /// <summary>How many DISTINCT tables were built.</summary>
    /// <remarks>
    /// Not eight: see the sharing rule above. This is the number worth logging,
    /// because it and <see cref="Bytes"/> are what a map change actually paid.
    /// </remarks>
    public int TableCount { get; }

    /// <summary>Bytes the distinct tables occupy in total.</summary>
    public long Bytes { get; }

    /// <summary>
    /// The tightest table that is still a lower bound for this asker.
    /// </summary>
    /// <remarks>
    /// <b>An exact profile match, not merely a superset.</b> A table whose arc
    /// set equals the asker's own is admissible (equality is a superset) and is
    /// the tightest such table there is, so there is nothing to choose between:
    /// the key indexes straight in.
    /// </remarks>
    public NavDistances For(in NavQuery query) => _byProfile[KeyOf(in query)];

    /// <summary>
    /// Builds every profile the graph can distinguish.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>This is the expensive call and it belongs at a map change.</b> One
    /// reverse Dijkstra per node per distinct table: 3-94 ms and 0.2-2.7 MB for
    /// a single table on the shipped graphs, times <see cref="TableCount"/>. It
    /// is why the feature is opt-in rather than on.
    /// </remarks>
    public static NavDistanceSet Derive(NavGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        // Which arc types the graph actually contains. A profile bit for an arc
        // type that is not present cannot change a single distance, so masking
        // it away is what collapses eight profiles into the two or three a real
        // graph needs.
        var present = 0;

        for (var e = 0; e < graph.EdgeCount; e++)
        {
            present |= graph.Edges[e].Type switch
            {
                NavEdgeType.DoubleJump => DoubleJumpBit,
                NavEdgeType.RocketJump => RocketJumpBit,
                NavEdgeType.StickyJump => StickyJumpBit,
                _ => 0,
            };
        }

        var byProfile = new NavDistances[Profiles];
        var built = new NavDistances?[Profiles];
        var tables = 0;
        var bytes = 0L;

        for (var key = 0; key < Profiles; key++)
        {
            var masked = key & present;

            if (built[masked] is null)
            {
                built[masked] = NavDistances.Derive(
                    graph,
                    (masked & DoubleJumpBit) != 0,
                    (masked & RocketJumpBit) != 0,
                    (masked & StickyJumpBit) != 0);

                tables++;
                bytes += built[masked]!.Bytes;
            }

            byProfile[key] = built[masked]!;
        }

        return new NavDistanceSet(byProfile, tables, bytes);
    }

    private static int KeyOf(in NavQuery query) =>
        (query.CanDoubleJump ? DoubleJumpBit : 0)
        | (query.CanRocketJump ? RocketJumpBit : 0)
        | (query.CanStickyJump ? StickyJumpBit : 0);
}
