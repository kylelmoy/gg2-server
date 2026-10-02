using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Bots;

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
/// The chosen node's own stand position. On a miss this is the anchor itself
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
/// <b>Reachability is deliberately NOT checked.</b> Answering it costs an A*
/// per candidate and the caller is about to run exactly one A* anyway — so
/// <see cref="BotObjective"/> watches for a spot goal that never produces a path
/// and falls back to the plain objective for a while instead. <b>A goal that
/// resolves is not a route that exists</b>, and this only promises the first half.
/// </para>
/// </remarks>
public sealed class BotGoalSpot
{
    private readonly NavGraph _graph;

    // Reused across calls, like NavSearch's scratch, and not thread-safe for
    // the same reason: one search at a time per instance.
    private readonly List<(int Node, double Rank)> _candidates = [];

    // The nodes reachable from _reachFrom under _reachQuery, and the BFS queue
    // that fills it. Sized once, so a recompute allocates nothing.
    private readonly bool[] _reachable;
    private readonly int[] _queue;
    private int _reachFrom = -1;
    private NavQuery _reachQuery;

    public BotGoalSpot(NavGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        _graph = graph;
        _reachable = new bool[graph.NodeCount];
        _queue = new int[graph.NodeCount];
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
    /// pixels every other term is in. Height is the whole model of "a good
    /// position" here: it is what makes a position advantageous and it is the one
    /// part of that which is cheap to ask for. No sightline graph, no cover map.
    /// </param>
    /// <param name="filter">What stops a bullet, for the sightline test.</param>
    /// <param name="avoidRadius">
    /// Refuse any candidate within this distance of <paramref name="fromX"/>,
    /// <paramref name="fromY"/> — "somewhere other than where I am standing".
    /// <para>
    /// <b>Measured from the BOT, not from the anchor.</b> The band is what the
    /// spot is <em>for</em>; this is what makes the answer a different one, so the
    /// two cannot be one test. A bot parked on the best node in its band wants the
    /// next best, not a wider band. Zero — the default — leaves the
    /// search exactly as it was.
    /// </para>
    /// </param>
    /// <param name="avoidNode">
    /// Refuse this node outright — the one the bot is standing on.
    /// <para>
    /// <b>A distance is not enough, because a node is not a point.</b> A nav node
    /// is a RUN of columns: <c>dkoth_atalia</c>'s node 126 spans 3,522 to 3,642,
    /// so a spot 96px from a bot standing in the middle of it is still that same
    /// node, <see cref="BotObjective.TryResolve"/> clamps the goal back into the
    /// node's own columns, and start and goal are then one node with an empty
    /// route between them.
    /// </para>
    /// <para>
    /// ⚠️ <b>Kept on the mechanism, NOT on a measurement.</b> Adding it moved the
    /// frozen-bot count by exactly zero (31 of 528 either way) — the bots it was
    /// reached for were held by something else, and it is <c>_repostSkip</c> that
    /// freed them. It stays because the degenerate case above is real and cheap to
    /// exclude, not because it was shown to fix anything.
    /// </para>
    /// </param>
    /// <param name="skip">
    /// Pass over this many otherwise-qualifying candidates before answering —
    /// "give me the next-best one instead".
    /// <para>
    /// <b>For a caller that has learned the best one is no good, and reachability
    /// is the case that needs it.</b> This search deliberately does not check
    /// whether a route exists (see the type's remarks), so its first answer can be
    /// a node in a part of the map the bot cannot get to. Without this the caller's
    /// only recourse is to stop asking for spots at all, and it then re-asks for
    /// the same unreachable node when the ban expires.
    /// </para>
    /// </param>
    public BotSpot Find(
        double fromX, double fromY,
        double targetX, double targetY,
        double minDistance, double maxDistance,
        BotLos los, double highBonus,
        CollisionWorld collision, in CollisionFilter filter,
        double avoidRadius = 0, int avoidNode = -1, int skip = 0, int stickyNode = -1,
        int fromNode = -1, NavQuery? reachableFor = null)
    {
        ArgumentNullException.ThrowIfNull(collision);

        Tries = 0;

        // ⚠️ A spot the bot's own class cannot walk to is not a spot. Without this
        // the band is ranked over every node on the map, and a bot whose class
        // cannot leave the pocket it stands in — a demoman spawning on tdm_mantic
        // node 209, 45 of 261 nodes reachable without a rocket jump — is handed
        // one unreachable spot after another, every plan fails, and it stands
        // still until an enemy happens to walk within reach. The filter is the
        // search's own: NavGraph.EdgeRoutable under the same live query, so a node
        // this admits is one A* can route to and one it refuses is not.
        var filtered = fromNode >= 0 && fromNode < _graph.NodeCount && reachableFor is { } query;

        if (filtered)
            Reach(fromNode, reachableFor!.Value);

        // The anchor is the fallback, published even on a miss — see BotSpot.X.
        var miss = new BotSpot(-1, targetX, targetY);

        if (maxDistance <= minDistance)
            return miss;

        var middle = (minDistance + maxDistance) / 2;
        var targetColumn = NavGraph.AnchorColumn(targetX);

        // --- keep the node already chosen for this anchor ---------------------
        //
        // The rank's first term is the straight-line distance from the BOT, so the
        // winner is a function of where the bot currently is — and the bot is
        // walking a graph route, not a straight line. Where the two disagree the
        // search feeds back on itself: a route whose first legs lead away from the
        // chosen spot makes that spot dearer every tick and a rival cheaper, the
        // goal flips, the new route leads away from THAT one, and the bot walks a
        // few pixels back and forth between two candidates for the rest of the
        // round. It presses a direction and it moves, so neither
        // BotPathState.StuckTicks (which wants a bot pressing and not moving) nor
        // ScriptedBot.ParkedTicks (which wants one not pressing at all) can see it.
        // Measured across all 22 maps at 12 bots and 8 seeds: 2.6-21.5% of all
        // alive bot time went nowhere this way, with single bots pinned for up to
        // 17,074 consecutive ticks — a whole match.
        //
        // So the choice is made ONCE per anchor and held while it still qualifies.
        // The band test below is what lets go of it: when the objective moves, the
        // held node leaves the band and the search runs normally. A re-post passes
        // no sticky node at all — breaking a perch that is not paying for itself is
        // the one thing that mechanism exists to do.
        if (stickyNode >= 0 && stickyNode != avoidNode && stickyNode < _graph.NodeCount
            && (!filtered || _reachable[stickyNode]))
        {
            var (hx, hy) = Stand(stickyNode, targetColumn);
            var held = Gml.PointDistance(hx, hy, targetX, targetY);

            if (held >= minDistance
                && held <= maxDistance
                && (avoidRadius <= 0 || Gml.PointDistance(fromX, fromY, hx, hy) >= avoidRadius)
                && Sightline(los, hx, hy, targetX, targetY, collision, in filter))
            {
                return new BotSpot(stickyNode, hx, hy);
            }
        }

        _candidates.Clear();

        for (var node = 0; node < _graph.NodeCount; node++)
        {
            var (x, y) = Stand(node, targetColumn);
            var toTarget = Gml.PointDistance(x, y, targetX, targetY);

            if (toTarget < minDistance || toTarget > maxDistance)
                continue;

            // Refused BEFORE ranking rather than after, because the rank's first
            // term is "walk less" — a node the bot is already standing on
            // scores zero there and wins by construction, which is exactly the
            // lock-in this parameter exists to break.
            if (node == avoidNode)
                continue;

            if (filtered && !_reachable[node])
                continue;

            if (avoidRadius > 0 && Gml.PointDistance(fromX, fromY, x, y) < avoidRadius)
                continue;

            // In world pixels throughout, so the three terms are comparable:
            // walk less, stand in the middle of the band rather than on either
            // edge of it, and prefer height when asked for.
            //
            // A negative rank is fine and is not clamped. Clamping at 1 the way
            // botFindTarget does would collapse every high-ground candidate onto
            // one priority and throw away the ordering this exists for.
            // (botFindTarget clamps only so a printed threat reads sensibly.)
            var rank = Gml.PointDistance(fromX, fromY, x, y)
                + Math.Abs(toTarget - middle)
                - (highBonus * Math.Max(0, targetY - y));

            _candidates.Add((node, rank));
        }

        // Ties broken by node index rather than left to the sort. List.Sort is
        // unstable, and a run of this suite is asserted to be reproducible — an
        // arbitrary tie order would make a bot pick a different spot on a rerun
        // for no reason anybody could see. The reference pops a ds_priority, whose
        // own tie rule is unspecified, so nothing is owed to it here.
        _candidates.Sort(static (a, b) =>
        {
            var byRank = a.Rank.CompareTo(b.Rank);
            return byRank != 0 ? byRank : a.Node.CompareTo(b.Node);
        });

        var passed = 0;
        var firstNode = -1;
        double firstX = 0, firstY = 0;

        foreach (var (node, _) in _candidates)
        {
            var (x, y) = Stand(node, targetColumn);

            if (Sightline(los, x, y, targetX, targetY, collision, in filter))
            {
                if (firstNode < 0)
                {
                    firstNode = node;
                    firstX = x;
                    firstY = y;
                }

                if (passed++ < skip)
                    continue;

                return new BotSpot(node, x, y);
            }

            if (Tries >= BotConstants.SpotTries)
                break;
        }

        // ⚠️ The skip WRAPS rather than walking off the end of the list. The
        // caller cycles a fixed BOT_SPOT_TRIES candidates (ScriptedBot's
        // _repostSkip) and this list is only as long as the band happens to be, so
        // a band offering four candidates to a caller on its sixth try would
        // otherwise answer "nothing anywhere" — and the caller reads that as no
        // spot existing and falls back to the point the bot is already standing
        // on. Measured on ctf_oldfort seed 4242: a defending Heavy sat on skip 6
        // against a band that never offered six, motionless for 17,688 ticks.
        //
        // Returning the FIRST qualifying candidate rather than re-running the loop
        // modulo its length: the ranking is already sorted, so the first is the one
        // a fresh caller would have been given, which is what wrapping means here.
        if (firstNode >= 0)
            return new BotSpot(firstNode, firstX, firstY);

        // Nothing reachable in the band: widen it outward and take the reachable
        // node nearest its middle that still has the sightline the caller asked
        // for and still keeps the minimum distance. The sightline is not optional
        // here — dropping it as well was measured: a red demoman on tdm_mantic
        // was walked to the reachable floor nearest the enemy, beneath it with no
        // line, and red's TDM wins fell 34 to 19 of 64 against standing at spawn.
        // Nothing qualifying at all is a miss, exactly as unfiltered.
        if (filtered)
        {
            var best = -1;
            var bestRank = double.MaxValue;
            double bestX = 0, bestY = 0;
            var budget = BotConstants.SpotTries;

            _candidates.Clear();

            for (var node = 0; node < _graph.NodeCount; node++)
            {
                if (!_reachable[node] || node == avoidNode)
                    continue;

                var (x, y) = Stand(node, targetColumn);
                var toTarget = Gml.PointDistance(x, y, targetX, targetY);

                if (toTarget < minDistance)
                    continue;

                _candidates.Add((node, Math.Abs(toTarget - middle)));
            }

            _candidates.Sort(static (a, b) =>
            {
                var byRank = a.Rank.CompareTo(b.Rank);
                return byRank != 0 ? byRank : a.Node.CompareTo(b.Node);
            });

            foreach (var (node, rank) in _candidates)
            {
                var (x, y) = Stand(node, targetColumn);

                if (Sightline(los, x, y, targetX, targetY, collision, in filter))
                {
                    best = node;
                    bestRank = rank;
                    bestX = x;
                    bestY = y;
                    break;
                }

                if (--budget <= 0)
                    break;
            }

            if (best >= 0)
                return new BotSpot(best, bestX, bestY);
        }

        return miss;
    }

    /// <summary>
    /// Fills <c>_reachable</c> with every node routable from <paramref name="from"/>
    /// under <paramref name="query"/>, unless it already holds that answer.
    /// </summary>
    /// <remarks>
    /// Cached on the pair, so a bot that has not changed node or ability pays for
    /// one BFS however many times it asks. The query changes with live state
    /// (a carrier's gates, a soldier's launcher going cold), and a stale answer
    /// would admit a spot the planner then refuses.
    /// </remarks>
    private void Reach(int from, in NavQuery query)
    {
        if (from == _reachFrom && query == _reachQuery)
            return;

        _reachFrom = from;
        _reachQuery = query;
        Array.Clear(_reachable);

        var head = 0;
        var tail = 0;
        _reachable[from] = true;
        _queue[tail++] = from;

        while (head < tail)
        {
            foreach (ref readonly var edge in _graph.EdgesFrom(_queue[head++]))
            {
                if (_reachable[edge.To] || !NavGraph.EdgeRoutable(in edge, in query))
                    continue;

                _reachable[edge.To] = true;
                _queue[tail++] = edge.To;
            }
        }
    }

    /// <summary>
    /// Where a bot standing on this node would have its origin, at the column
    /// facing the target.
    /// </summary>
    /// <remarks>
    /// <b>Whichever part of the run faces the target, not its middle.</b> A long
    /// floor's midpoint can be well out of range while one of its ends is not, and
    /// rejecting the whole run for that would lose most of the usable ground on a
    /// map.
    /// <para>
    /// The position is the character <em>origin</em> — chest height, feet 23px
    /// lower — which is both what the sightline test wants and what
    /// <c>NodeFromWorld</c> resolves back to this same node later.
    /// </para>
    /// </remarks>
    /// <summary>Whether a candidate has the sightline the caller asked for.</summary>
    /// <remarks>
    /// "Wrong" is the sightline the caller did NOT ask for: a blocked line for
    /// <see cref="BotLos.Need"/>, a clear one for <see cref="BotLos.Avoid"/>.
    /// <see cref="BotLos.Any"/> pays for no line test at all, which is what lets a
    /// proximity-only search answer without spending its try budget.
    /// </remarks>
    private bool Sightline(
        BotLos los, double x, double y, double targetX, double targetY,
        CollisionWorld collision, in CollisionFilter filter)
    {
        if (los == BotLos.Any)
            return true;

        Tries++;

        var wrong = collision.CollisionLineBlocksBullets(x, y, targetX, targetY, filter);

        return los == BotLos.Avoid ? wrong : !wrong;
    }

    private (double X, double Y) Stand(int node, int targetColumn)
    {
        var n = _graph.Nodes[node];
        var column = Math.Clamp(targetColumn, n.X0, n.X1);

        return (NavGraph.ColumnWorldX(column), _graph.StandY(node));
    }
}
