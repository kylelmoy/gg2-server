using Gg2.Protocol;

namespace Gg2.Bots;

public sealed partial class PathFollower
{
    /// <summary>
    /// The edge between two nodes that <see cref="NavSearch"/> would have taken,
    /// if the graph still has one this traveller may use.
    /// </summary>
    /// <remarks>
    /// <c>navEdgeFind</c>, and ⚠️ <b>NOT the first match.</b> A route is a list of
    /// nodes, so the follower has to re-derive which of a pair's parallel edges
    /// the search actually costed — and a graph carries plenty of them: across the
    /// 24 shipped caches, <b>11,615 node pairs carry edges of more than one
    /// type</b> (9,251 walk+fall, 1,529 fall+jump, <b>835 double-jump+rocket-jump</b>).
    /// <para>
    /// ⚠️ <b>Taking the first was a live bug, and not only a sticky-jump one.</b>
    /// The search refuses per <em>edge</em> — a scout gets no rocket jump, a
    /// soldier no double jump — while this took whichever the edge index happened
    /// to yield. Measured on the shipped graphs: <b>833 pairs where a soldier's
    /// route is costed as a rocket jump and was steered as a double jump</b>, so
    /// the bot pressed for a second jump its class does not have, flew an ordinary
    /// 57px hop where the arc wanted 276, landed off route and eventually
    /// blacklisted a perfectly good edge. On a cache carrying type-7 edges the
    /// same fault hid the sticky-jump arm completely: 3,584 pairs handed a demoman
    /// a rocket-jump or double-jump edge, <see cref="StickyModel.Ready"/> was
    /// never consulted, and the bot stood on the takeoff column waiting for a
    /// rocket launcher it does not carry. <b>Zero sticky jumps across 48 real
    /// edges on four maps, with the arm itself entirely correct.</b>
    /// </para>
    /// <para>
    /// <b>Reproducing the search exactly is cheap</b>: A* relaxes with
    /// <c>existing &gt;= 0 &amp;&amp; existing &lt;= tentative → continue</c>, so among
    /// parallel edges it keeps the strictly cheapest and the first one wins a tie.
    /// Lowest <see cref="NavEdge.Cost"/>, first-in-order on ties, is that rule.
    /// ⚠️ The occupancy multiplier does not enter it: it is keyed on the node
    /// pair, so it scales every parallel edge by the same factor and cannot
    /// reorder them.
    /// </para>
    /// <para>
    /// ⚠️ <b>No usable edge is a re-plan, not a fallback.</b> The query is rebuilt
    /// from live state every tick, so a route outlives the traveller that planned
    /// it — a soldier's rocket jumps go as its hp drops, and picking up the intel
    /// shuts a team gate on the way home. Steering a hop the search would now
    /// refuse is the failure this method exists to stop, so returning false and
    /// re-planning is the answer rather than steering it anyway.
    /// </para>
    /// </remarks>
    private static bool TryFindEdge(NavGraph graph, BotPathState state, int from, int to, out NavEdge edge)
    {
        var found = false;
        edge = default;

        // A local because PlanQuery is a property and cannot be passed by `in`.
        var planned = state.PlanQuery;

        foreach (var candidate in graph.EdgesFrom(from))
        {
            if (candidate.To != to)
                continue;

            if (!NavGraph.EdgeRoutable(in candidate, in planned))
                continue;

            // Strictly cheaper, so an equal cost leaves the earlier one standing
            // — the same tie-break A*'s `existing <= tentative` gives.
            if (found && candidate.Cost >= edge.Cost)
                continue;

            edge = candidate;
            found = true;
        }

        return found;
    }

    /// <summary>
    /// Which edge this follower would steer for a hop of the route it is
    /// carrying — <see cref="TryFindEdge"/>, in public.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>It exists so that a harness cannot answer the question a second
    /// way.</b> A route is a list of nodes and a node pair can carry several
    /// edges; picking the wrong one of them was a live bug on the shipped graphs
    /// for eight phases (docs/TRAPS-BOTS.md, "A route is a list of NODES"), and
    /// the tie-break that fixes it — strictly cheapest, first-in-order on ties,
    /// resolved against <see cref="BotPathState.PlanQuery"/> rather than the live
    /// one — is exactly the kind of rule a re-implementation drifts away from.
    /// <see cref="EdgeFlights"/> asks this before it flies anything, so a flight
    /// that reports a refusal is a refusal of the edge it names.
    /// </remarks>
    public bool TryResolveHop(BotPathState state, int from, int to, out NavEdge edge)
    {
        ArgumentNullException.ThrowIfNull(state);
        return TryFindEdge(_graph, state, from, to, out edge);
    }

    /// <summary>
    /// Works out a fresh route from where the bot is now, and installs it —
    /// <c>botPathPlan.gml</c>.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The search runs BEFORE the old route is given up, and a failed search
    /// keeps it.</b> This is the reference's "bots momentarily stop moving"
    /// report, and the stall was structural: with free-then-search, a bot between
    /// a goal changing and a route existing had nothing to follow and the
    /// follower returned no keys at all. A search that succeeds hides that in one
    /// tick; one that fails left the bot standing still until the timer came
    /// round, and then again. Keeping the old route means a bot whose new goal
    /// cannot be planned for keeps walking the last route that worked, which is
    /// what a person does.
    /// <para>
    /// ⚠️ <b>The anti-thrash check lives here rather than in the stuck detector
    /// because it is about re-planning, not about failing outright.</b> Two
    /// re-plans within <c>BOT_THRASH_DIST</c> of each other means the bot is going
    /// round in a circle at one spot, whatever the immediate cause. The stuck
    /// detector catches the blunt case where nothing moves at all; this catches
    /// the bot that is technically moving — shuffling back and forth across a
    /// ledge — and getting nowhere.
    /// </para>
    /// </remarks>
    /// <returns>Whether a route was found.</returns>
    public bool Plan(
        BotPathState state, double x, double y, int tick,
        in NavQuery query, BotOccupancy? occupancy = null)
    {
        ArgumentNullException.ThrowIfNull(state);

        // ⚠️ The timer is re-armed whatever happens below, and the stagger is
        // ADDED rather than used as a phase — `botPathPlan.gml:53`. So a bot's
        // re-plan interval is BOT_REPLAN_TICKS plus its own offset, somewhere in
        // [45, 89].
        //
        // ⚠️ <b>That interval is a hard ceiling on the takeoff budget</b>, which
        // nothing in the reference remarks on: a successful re-plan calls
        // FreePath, which sets EdgeFrom to -1, and FollowEdge's change-of-edge
        // test then zeroes TakeoffTicks on the very next tick. At a stagger of 0
        // the interval is 45 and BOT_TAKEOFF_PATIENCE (60) can never be reached —
        // the escape hatch is dead for that bot, exactly as it was dead for every
        // bot before the reference started incrementing the counter at all. A bot
        // that needs the hatch needs a stagger of at least 15.
        state.ReplanAt = tick + BotConstants.ReplanTicks
            + (state.ReplanStagger % BotConstants.ReplanTicks);

        if (!state.HasGoal)
            return false;

        // ⚠️ Only while a route is installed. A bot with nothing to follow has to
        // keep trying.
        if (state.HasPath && tick < state.PlanFailUntil)
            return false;

        var thrash = Gml.PointDistance(x, y, state.LastReplanX, state.LastReplanY);

        if (thrash < BotConstants.ThrashDist)
            state.ReplanNear++;
        else
            state.ReplanNear = 0;

        state.LastReplanX = x;
        state.LastReplanY = y;

        if (state.ReplanNear >= 2)
        {
            state.BlacklistEdge(state.EdgeFrom, state.EdgeTo, tick);
            state.ReplanNear = 0;
        }

        // ⚠️ Swept here and nowhere else: a re-plan is the only moment a stale
        // ban could affect anything, so it needs no timer of its own.
        state.SweepBlacklist(tick);

        // ⚠️ This bot's OWN route comes out of the counts before it searches, and
        // that is not an optimisation — it is what makes a route stable across
        // re-plans. A bot that can see its own route in the occupancy counts is
        // charged extra for the path it is already walking, so every re-plan tries
        // to move it somewhere else and the whole team churns lanes every
        // BOT_REPLAN_TICKS. Measured in the reference on ctf_truefort: with self
        // excluded, 2 of 16 re-plans changed a route; with a node-keyed penalty —
        // which cannot exclude self cleanly, since a bot stands on its own nodes —
        // it was 15 of 16.
        occupancy?.Remove(state);

        var start = _graph.NodeFromWorld(x, y);
        if (start < 0)
        {
            state.PlanFailUntil = tick + BotConstants.ReplanTicks;
            return false;
        }

        var goal = _graph.NodeFromWorld(state.GoalX, state.GoalY);
        if (goal < 0)
        {
            state.PlanFailUntil = tick + BotConstants.ReplanTicks;
            return false;
        }

        state.Replans++;

        if (!_search.Find(start, goal, query, _scratch, state.Blacklist, occupancy?.Counts(query.Team)))
        {
            state.PlanFailUntil = tick + BotConstants.ReplanTicks;
            return false;
        }

        // ⚠️ Only NOW is the old route given up. FreePath is what zeroes the
        // follower's per-route state, and all of that belongs to the route being
        // replaced.
        state.FreePath();
        state.Path.AddRange(_scratch);

        // ⚠️ Recorded with the route, because the route is a list of NODES and
        // the edge each hop costed can only be re-derived by asking the search's
        // own question again. Asking the LIVE one instead loops — see
        // BotPathState.PlanQuery.
        state.PlanQuery = query;
        state.PathAt = 0;
        state.PathStale = false;
        state.PlanFailUntil = 0;

        // The one place a route is ever added. ⚠️ Against the team asking, which
        // is what the removal above will NOT read back — see BotOccupancy.Remove.
        //
        // The route behind the bot is never trimmed as it advances, and does not
        // need to be: a re-plan re-installs it from wherever the bot is standing
        // now, so the tail drops out of the counts on its own every
        // BOT_REPLAN_TICKS.
        occupancy?.Add(state, query.Team);
        return true;
    }

    /// <summary>Sends the bot somewhere, snapping the point onto the graph.</summary>
    /// <remarks>
    /// <c>botSetGoal</c>'s shape: the goal is snapped downward and the snapped
    /// position is what is kept, because a raw objective coordinate usually does
    /// not resolve to a node on its own. ⚠️ The old route is marked
    /// <see cref="BotPathState.PathStale"/> rather than freed, so the bot keeps
    /// moving until a plan for the new goal exists — and its arrival band is
    /// switched off, because reaching the end of it is not an arrival at
    /// anything.
    /// </remarks>
    public bool SetGoal(BotPathState state, double worldX, double worldY)
    {
        ArgumentNullException.ThrowIfNull(state);

        var snap = _graph.SnapObjective(worldX, worldY);
        if (snap.Node < 0)
            return false;

        state.GoalX = snap.GoalX;
        state.GoalY = snap.GoalY;
        state.HasGoal = true;
        state.Arrived = false;
        state.ArrivedAt = -1;
        state.PathStale = state.HasPath;

        // Event-driven: plan on the next follower tick rather than waiting out
        // the timer. ⚠️ That is why this is counted: a goal reissue is a search,
        // and it is the majority of them — see BotPathState.GoalsSet.
        state.ReplanAt = 0;
        state.PlanFailUntil = 0;
        state.GoalsSet++;
        return true;
    }
}
