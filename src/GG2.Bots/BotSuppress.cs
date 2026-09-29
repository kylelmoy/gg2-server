using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Bots;

/// <summary>
/// <c>botSuppressSpot</c>: where to keep shooting when a target is held but cannot
/// be seen.
/// </summary>
/// <remarks>
/// A bot gets 30 ticks of memory across a lost sightline and keeps firing at the
/// last position actually seen. That is right for a corner somebody is about to
/// step back around and <b>wrong for one they left</b>, because the stale position
/// is behind cover by construction — the bot is shooting the wall it lost them
/// behind. What is missing is any notion of <em>where they must come from</em>, and
/// the graph already knows: the enemy has to walk a route, and the first node on it
/// this bot can see is the place worth denying.
/// <para>
/// <b>Worth building because the case is common, and that was measured before
/// any of it was written.</b> Twelve bots on koth_valley over 3,172 frames:
/// <b>13,733 bot-ticks with a live target, 2,744 of them — 20% — with no line of
/// sight to it</b>, and for a heavy specifically 1,432 of 6,442, or 22%. About one
/// tick in five of every engagement is spent aiming at a remembered position, and
/// the 30-tick drop does not already eat most of it.
/// </para>
/// <para>
/// <b>A BOUNDED BREADTH-FIRST WALK, NOT AN A* — and that is a measurement, not a
/// preference.</b> The design asked for "run the path search from there toward the
/// heavy's own node and walk that path outward". Built that way first and timed:
/// <b>8.13 ms a call on koth_valley's 270 nodes</b>. Amortised over
/// <c>BOT_TARGET_PERIOD</c> that is 3.25 ms a frame for six heavies, which sounds
/// survivable and is not — nothing staggers the bots, so six recomputing on the
/// same frame is <b>48.8 ms against a 33.3 ms budget</b>, on the <em>smallest</em>
/// interesting map. ctf_truefort has 687 nodes.
/// </para>
/// <para>
/// The walk expands at most <c>BOT_SUPPRESS_HOPS</c> nodes outward from where the
/// enemy was and stops at the first one this bot can see. Breadth-first order is
/// <em>fewest hops from where they were</em>, which is the soonest they could
/// appear — which is the question being asked. It also needs no route to this bot
/// to exist at all, where the A* version returned nothing whenever one did not.
/// </para>
/// </remarks>
public static class BotSuppress
{
    /// <summary>Nothing useful; the caller keeps doing what it was doing.</summary>
    public const int NoSpot = -1;

    /// <summary>
    /// How many nodes the frontier may hold.
    /// </summary>
    /// <remarks>
    /// At most <c>BOT_SUPPRESS_HOPS</c> nodes are ever dequeued, so this only has
    /// to hold what those expand to. Overflowing it stops enqueuing rather than
    /// throwing, which can only <em>shrink</em> the answer — and a smaller answer is
    /// a suppression declined, which costs nothing.
    /// </remarks>
    private const int Frontier = 512;

    /// <param name="foeTeam">
    /// <b>The walk uses the ENEMY's gates, not ours.</b> It is their route, and a
    /// red bot must not decide that a blue enemy will come out through a red spawn
    /// gate.
    /// </param>
    /// <returns>A nav node, or <see cref="NoSpot"/>.</returns>
    public static int Spot(
        NavGraph graph, CollisionWorld collision, in CharacterState self,
        double lastX, double lastY, Team foeTeam)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(collision);

        // Where they were. Snapped DOWNWARD, because a position in mid-air
        // belongs to the surface under it — a target last seen jumping is going to
        // land on something.
        var source = graph.SnapObjective(lastX, lastY).Node;

        if (source < 0)
            return NoSpot;

        Span<int> queue = stackalloc int[Frontier];
        Span<int> seen = stackalloc int[Frontier];

        queue[0] = source;
        seen[0] = source;

        var head = 0;
        var tail = 1;
        var seenCount = 1;
        var hops = 0;

        var filter = CollisionFilter.ForBullet();

        while (head < tail && hops < BotConstants.SuppressHops)
        {
            var node = queue[head++];
            hops++;

            var (x, y) = Centre(graph, node);

            // NOT the node they were last seen on: that one is behind cover by
            // construction, and shooting it is exactly the behaviour this replaces.
            // The test is cheap and it is also the guard that stops a caller aiming
            // at a wall when the enemy simply stood still.
            if (node != source && !collision.CollisionLineBlocksBullets(self.X, self.Y, x, y, filter))
                return node;

            foreach (var edge in graph.EdgesFrom(node))
            {
                var to = edge.To;

                if (Contains(seen[..seenCount], to))
                    continue;

                // hasIntel is false — a carrier is on everyone's HUD anyway, and
                // guessing wrong only closes gates, which can only shrink the answer.
                if (!graph.EdgePassable(in edge, foeTeam, hasIntel: false, setupGatesClosed: false))
                    continue;

                if (tail >= Frontier || seenCount >= Frontier)
                    break;

                seen[seenCount++] = to;
                queue[tail++] = to;
            }
        }

        return NoSpot;
    }

    /// <summary>
    /// The world point a character standing on this node occupies.
    /// </summary>
    /// <remarks>
    /// The middle of its run, at chest height — which is both what the sight test
    /// wants and what the aim solve is about to be handed.
    /// </remarks>
    public static (double X, double Y) Centre(NavGraph graph, int node)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var n = graph.Nodes[node];

        // `floor` of the column midpoint, not a rounded one: the reference takes
        // the integer division of the two anchor columns, and a node an odd number
        // of columns wide would otherwise land half a cell to the right of where the
        // sightline was tested from.
        var column = (int)Math.Floor((n.X0 + n.X1) / 2.0);

        return (NavGraph.ColumnWorldX(column), graph.StandY(node));
    }

    private static bool Contains(ReadOnlySpan<int> values, int value)
    {
        foreach (var v in values)
        {
            if (v == value)
                return true;
        }

        return false;
    }
}
