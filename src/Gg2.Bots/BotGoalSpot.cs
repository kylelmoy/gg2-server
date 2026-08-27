using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Bots;

/// <summary>What sightline a spot must have to the thing it is chosen for.</summary>
public enum BotLos
{
    /// <summary>Do not ask. Proximity is the whole requirement.</summary>
    Any = BotConstants.LosAny,

    /// <summary>A clear line — the ordinary case, and what a firing position means.</summary>
    Need = BotConstants.LosNeed,

    /// <summary>A blocked line. The spy's flank: near enough to walk in from, out of sight.</summary>
    Avoid = BotConstants.LosAvoid,
}

/// <summary>Where <see cref="BotGoalSpot"/> decided to stand.</summary>
/// <param name="Node">The nav node, or -1 if nothing in the graph qualified.</param>
/// <param name="X">
/// The chosen node's own stand position. ⚠️ On a miss this is the anchor itself
/// rather than stale, so a caller that ignores <paramref name="Node"/> gets a
/// usable point instead of whatever the last call left behind.
/// </param>
/// <param name="Y"><inheritdoc cref="X"/></param>
public readonly record struct BotSpot(int Node, double X, double Y);

/// <summary>
/// <c>botGoalSpot</c>: the node to stand on in order to <em>do something to</em> a
/// point, rather than to be on it.
/// </summary>
/// <remarks>
/// Four separate behaviours want the same thing and none of them wants the point
/// itself: a firing position on the enemy generator, which is shot from range and
/// standing on was never the goal; ground overlooking a control point we already
/// hold; a push past a captured objective toward the enemy; and a medic's follow
/// position beside its ally. Expressed as a goal <em>predicate</em> those are one
/// function with different arguments, and building it once is the whole point —
/// the alternative is three near-copies that drift.
/// <para>
/// The search is <c>botFindTarget</c>'s shape rather than a scan: score every
/// candidate cheaply, then pay for the expensive test (the sightline) only on the
/// best ones, best-first, and stop at the first that passes.
/// <c>BOT_SPOT_TRIES</c> bounds the collision-line calls whatever the map looks
/// like, so the worst case is a fixed cost rather than one per node. It runs on
/// the objective cadence, staggered per bot, so at most a couple of bots pay the
/// O(nodes) scoring pass on any one frame.
/// </para>
/// <para>
/// ⚠️ <b>Reachability is deliberately NOT checked.</b> Answering it costs an A*
/// per candidate and the caller is about to run exactly one A* anyway — so
/// <see cref="BotObjective"/> watches for a spot goal that never produces a path
/// and falls back to the plain objective for a while instead. <b>A goal that
/// resolves is not a route that exists</b>, and this only promises the first half.
/// </para>
/// </remarks>
public sealed class BotGoalSpot
{
    private readonly NavGraph _graph;

    // ⚠️ Reused across calls, like NavSearch's scratch, and not thread-safe for
    // the same reason: one search at a time per instance.
    private readonly List<(int Node, double Rank)> _candidates = [];

    public BotGoalSpot(NavGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        _graph = graph;
    }

    /// <summary>How many sightline tests the last <see cref="Find"/> paid for.</summary>
    /// <remarks>
    /// Exposed so a test can show the <c>BOT_SPOT_TRIES</c> bound actually binds,
    /// which is otherwise invisible: a bounded search and an unbounded one return
    /// the same node on every map that has one.
    /// </remarks>
    public int Tries { get; private set; }

    /// <summary>
    /// Ranks every node in the band and returns the best that has the sightline
    /// asked for.
    /// </summary>
    /// <param name="fromX">Where the bot is standing — it prefers to walk less.</param>
    /// <param name="fromY"><inheritdoc cref="fromX"/></param>
    /// <param name="targetX">The anchor: the thing this spot is <em>for</em>.</param>
    /// <param name="targetY"><inheritdoc cref="targetX"/></param>
    /// <param name="minDistance">The near edge of the band.</param>
    /// <param name="maxDistance">The far edge.</param>
    /// <param name="los">Which sightline to the anchor the spot must have.</param>
    /// <param name="highBonus">
    /// How much a pixel of height above the anchor is worth, in the same world
    /// pixels every other term is in. ⚠️ Height is the whole model of "a good
    /// position" here: it is what makes a position advantageous and it is the one
    /// part of that which is cheap to ask for. No sightline graph, no cover map.
    /// </param>
    /// <param name="filter">What stops a bullet, for the sightline test.</param>
    public BotSpot Find(
        double fromX, double fromY,
        double targetX, double targetY,
        double minDistance, double maxDistance,
        BotLos los, double highBonus,
        CollisionWorld collision, in CollisionFilter filter)
    {
        ArgumentNullException.ThrowIfNull(collision);

        Tries = 0;

        // ⚠️ The anchor is the fallback, published even on a miss — see BotSpot.X.
        var miss = new BotSpot(-1, targetX, targetY);

        if (maxDistance <= minDistance)
            return miss;

        var middle = (minDistance + maxDistance) / 2;
        var targetColumn = NavGraph.AnchorColumn(targetX);

        _candidates.Clear();

        for (var node = 0; node < _graph.NodeCount; node++)
        {
            var (x, y) = Stand(node, targetColumn);
            var toTarget = Gml.PointDistance(x, y, targetX, targetY);

            if (toTarget < minDistance || toTarget > maxDistance)
                continue;

            // In world pixels throughout, so the three terms are comparable:
            // walk less, stand in the middle of the band rather than on either
            // edge of it, and prefer height when asked for.
            //
            // ⚠️ A negative rank is fine and is not clamped. Clamping at 1 the way
            // botFindTarget does would collapse every high-ground candidate onto
            // one priority and throw away the ordering this exists for.
            // (botFindTarget clamps only so a printed threat reads sensibly.)
            var rank = Gml.PointDistance(fromX, fromY, x, y)
                + Math.Abs(toTarget - middle)
                - (highBonus * Math.Max(0, targetY - y));

            _candidates.Add((node, rank));
        }

        // ⚠️ Ties broken by node index rather than left to the sort. List.Sort is
        // unstable, and a run of this suite is asserted to be reproducible — an
        // arbitrary tie order would make a bot pick a different spot on a rerun
        // for no reason anybody could see. The reference pops a ds_priority, whose
        // own tie rule is unspecified, so nothing is owed to it here.
        _candidates.Sort(static (a, b) =>
        {
            var byRank = a.Rank.CompareTo(b.Rank);
            return byRank != 0 ? byRank : a.Node.CompareTo(b.Node);
        });

        foreach (var (node, _) in _candidates)
        {
            var (x, y) = Stand(node, targetColumn);

            if (los == BotLos.Any)
                return new BotSpot(node, x, y);

            Tries++;

            // "Wrong" is the sightline the caller did NOT ask for: a blocked line
            // for Need, a clear one for Avoid.
            var wrong = collision.CollisionLineBlocksBullets(x, y, targetX, targetY, filter);

            if (los == BotLos.Avoid)
                wrong = !wrong;

            if (!wrong)
                return new BotSpot(node, x, y);

            if (Tries >= BotConstants.SpotTries)
                break;
        }

        return miss;
    }

    /// <summary>
    /// Where a bot standing on this node would have its origin, at the column
    /// facing the target.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Whichever part of the run faces the target, not its middle.</b> A long
    /// floor's midpoint can be well out of range while one of its ends is not, and
    /// rejecting the whole run for that would lose most of the usable ground on a
    /// map.
    /// <para>
    /// The position is the character <em>origin</em> — chest height, feet 23px
    /// lower — which is both what the sightline test wants and what
    /// <c>NodeFromWorld</c> resolves back to this same node later.
    /// </para>
    /// </remarks>
    private (double X, double Y) Stand(int node, int targetColumn)
    {
        var n = _graph.Nodes[node];
        var column = Math.Clamp(targetColumn, n.X0, n.X1);

        return (NavGraph.ColumnWorldX(column), _graph.StandY(node));
    }
}
