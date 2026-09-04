using Gg2.Bots;

namespace Gg2.Tests;

/// <summary>
/// How differently a group of bots routed the same leg — the instrument the bot
/// layer has never had.
/// </summary>
/// <remarks>
/// <b>Every existing check on route variety tests the MECHANISM, not the
/// outcome.</b> <c>BotOccupancyTests</c> asserts that a crowded edge costs more
/// and that four bots on one swept leg do not all agree; <c>NavSearchTests</c>
/// asserts the same on a three-node toy graph. Nothing scores how far apart a
/// team's routes actually end up, so nothing could tell a mechanism that works
/// from one that fires and achieves nothing — which is exactly how the deleted
/// per-edge jitter survived long enough to be measured by hand.
/// <para>
/// <b>The statistic is the mean pairwise Jaccard distance over routes' EDGE
/// sets.</b> 0 is single file — every bot on the identical route — and 1 is
/// fully disjoint corridors. Edges rather than nodes because an edge is what a
/// corridor is made of and what <see cref="BotOccupancy"/> keys on; normalised
/// by the union so a long route and a short one are comparable, which a raw
/// count of shared nodes is not.
/// </para>
/// <para>
/// ⚠️ <b>It is deliberately blind to whether the spread is any GOOD.</b> Four
/// bots sent down four terrible corridors score 1.0. This measures dispersion
/// and nothing else; route quality is <c>ScenarioSuiteTests</c>' walking time
/// and the two single-leg pins. A replacement for occupancy has to clear both,
/// and clearing only this one is the failure mode to expect.
/// </para>
/// </remarks>
public static class RouteDiversity
{
    /// <summary>
    /// Mean pairwise Jaccard distance over the routes' edge sets — 0 single
    /// file, 1 disjoint.
    /// </summary>
    /// <remarks>
    /// <b>Fewer than two routes has no answer and returns 0</b>, the same value
    /// as total agreement, because both mean "nothing dispersed here". A caller
    /// separating the two cases wants <see cref="DistinctRoutes"/> as well.
    /// <para>
    /// Two routes that are both too short to have an edge count as identical
    /// rather than as disjoint: an empty union is agreement about nothing, and
    /// scoring it 1 would let a map whose legs all fail report perfect spread.
    /// </para>
    /// </remarks>
    public static double Spread(IReadOnlyList<IReadOnlyList<int>> routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        if (routes.Count < 2)
            return 0;

        var sets = new HashSet<long>[routes.Count];
        for (var i = 0; i < routes.Count; i++)
            sets[i] = EdgeSet(routes[i]);

        var total = 0.0;
        var pairs = 0;

        for (var i = 0; i < sets.Length; i++)
        {
            for (var j = i + 1; j < sets.Length; j++)
            {
                var shared = 0;
                foreach (var key in sets[i])
                    if (sets[j].Contains(key))
                        shared++;

                var union = sets[i].Count + sets[j].Count - shared;

                // An empty union is two routes that agree about nothing at all.
                total += union == 0 ? 0 : 1.0 - ((double)shared / union);
                pairs++;
            }
        }

        return pairs == 0 ? 0 : total / pairs;
    }

    /// <summary>How many of the routes are distinct, as node sequences.</summary>
    /// <remarks>
    /// Kept beside <see cref="Spread"/> because it is the number the reference
    /// measurement was recorded in — "four bots asking in turn get four distinct
    /// routes with occupancy and one without" — and a new statistic that cannot
    /// reproduce the recorded one is a new statistic nobody should trust yet.
    /// It is the weaker of the two: two routes differing in a single node score
    /// 2 here and near 0 on <see cref="Spread"/>.
    /// </remarks>
    public static int DistinctRoutes(IReadOnlyList<IReadOnlyList<int>> routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var route in routes)
            seen.Add(string.Join(',', route));

        return seen.Count;
    }

    private static HashSet<long> EdgeSet(IReadOnlyList<int> route)
    {
        var set = new HashSet<long>();
        for (var i = 1; i < route.Count; i++)
            set.Add(NavGraph.EdgeKey(route[i - 1], route[i]));

        return set;
    }
}
