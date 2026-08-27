using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Bots;

/// <summary>
/// One tick of path following: a route in, movement keys out.
/// </summary>
/// <remarks>
/// <c>Scripts/Bots/botPathKeys.gml</c> and <c>botPathPlan.gml</c>. The shape is
/// Surfacer's surface navigator plus Quake III's think loop:
/// <list type="number">
/// <item>note whether the bot moved at all since last tick — the stuck
/// detector's only input;</item>
/// <item>plan, if there is no route or the re-plan timer is up;</item>
/// <item>find which node the bot is standing on and advance along the route to
/// it; failing to find itself on the route at all is an interruption;</item>
/// <item>steer along the current edge according to what kind of move it is;</item>
/// <item>if keys were pressed and nothing moved for <c>BOT_STUCK_TICKS</c>, give
/// up on this edge: blacklist it and re-plan.</item>
/// </list>
/// <para>
/// ⚠️ <b>This runs every tick, unlike target selection, which re-decides every
/// fifteen.</b> Aiming may lag and look human; steering may not — a jump missed
/// by one tick is a bot standing at the edge of a gap for ever.
/// </para>
/// <para>
/// ⚠️ <b>Each edge kind is steered by the geometry its generator used, never by
/// a general rule.</b> A fall leaves past the end of a surface, a drop-through
/// goes down through its middle, a jump leaves from a searched column and then
/// flies a specific arc. Taking those from the same numbers the build used is
/// what keeps the follower honest about what it planned.
/// </para>
/// <para>
/// ⚠️ <b>The jump edges are not implemented yet</b> — <see cref="NavEdgeType.Jump"/>,
/// <see cref="NavEdgeType.DoubleJump"/> and <see cref="NavEdgeType.RocketJump"/>
/// are 209 of <c>botPathKeys.gml</c>'s 394 code lines and four helper scripts,
/// and they are the next piece. Until then such an edge is steered by nothing and
/// counted in <see cref="BotPathState.UnsupportedEdgeTicks"/>; the stuck detector
/// takes it away and the search routes round it, which is the machinery that
/// already exists for an edge that turns out to be untraversable. <b>Do not
/// paper over it with an approximate jump</b> — an arc the generator did not
/// validate is exactly what the whole takeoff apparatus exists to prevent.
/// </para>
/// </remarks>
public sealed class PathFollower
{
    private readonly NavGraph _graph;
    private readonly NavSearch _search;
    private readonly List<int> _scratch = [];

    public PathFollower(NavGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        _graph = graph;
        _search = new NavSearch(graph);
    }

    /// <summary>
    /// Advances one bot's route by a tick and returns the movement keys.
    /// </summary>
    /// <param name="state">The bot's navigation state, updated in place.</param>
    /// <param name="x">The character's origin.</param>
    /// <param name="y">The character's origin.</param>
    /// <param name="hSpeed">The character's horizontal speed, for the brake band.</param>
    /// <param name="onGround">
    /// ⚠️ Load-bearing far beyond the obvious. It gates the route advance, the
    /// off-route branch and arrival, all for the same reason: an airborne bot is
    /// flying a trajectory with its own end, and the tick it lands is the tick
    /// either branch has anything true to say.
    /// </param>
    /// <param name="tick">The server frame, for every timer here.</param>
    /// <param name="query">Who is asking, for the search.</param>
    /// <param name="occupancy">The team's edge crowding, or null.</param>
    /// <returns>LEFT / RIGHT / JUMP / DOWN. Never an attack or a special.</returns>
    public PlayerKeys Step(
        BotPathState state,
        double x,
        double y,
        double hSpeed,
        bool onGround,
        int tick,
        in NavQuery query,
        IReadOnlyDictionary<long, int>? occupancy = null)
    {
        ArgumentNullException.ThrowIfNull(state);

        // Cleared up front and set only by the in-flight branch, so every early
        // return leaves it false.
        state.FlyingEdge = false;

        if (!state.HasGoal)
            return PlayerKeys.None;

        // ⚠️ Half a pixel, not zero: good_move_contact_solid works at 1/8 pixel
        // precision, so a bot grinding against a wall jitters by a fraction
        // without going anywhere.
        var moved = Math.Abs(x - state.LastX) > 0.5 || Math.Abs(y - state.LastY) > 0.5;
        state.LastX = x;
        state.LastY = y;

        // ⚠️ Planning is on the timer even when it FAILS, which is the whole
        // reason this is one condition and not "plan whenever we have no route".
        // A goal that is genuinely unreachable makes every search fail, and
        // retrying it every tick is a full A* per bot per frame that can never
        // succeed. Event-driven re-plans set ReplanAt to 0 rather than calling
        // in here.
        if (tick >= state.ReplanAt)
            Plan(state, x, y, tick, query, occupancy);

        if (!state.HasPath)
            return PlayerKeys.None;

        var size = state.Path.Count;

        // ⚠️ Only while standing on something. NodeFromWorld tolerates being a
        // couple of cells off a surface, so an arc passing over its own landing
        // resolves to that node ~15px before the feet reach it — and advancing
        // there hands the follower the *next* edge while the bot is still in the
        // air. Measured in the reference on ctf_truefort's two stacked jumps:
        // the bot took off, was switched onto the return leg at the apex, landed
        // back where it started and did it again, a whole team pacing at the
        // enemy door for the rest of the round.
        var here = onGround ? _graph.NodeFromWorld(x, y) : -1;
        var found = false;

        if (here >= 0)
        {
            // Forward only. A bot only advances along its own route, and looking
            // backwards would let a long surface the route touches twice pull it
            // into a loop.
            for (var i = state.PathAt; i < size; i++)
            {
                if (state.Path[i] != here)
                    continue;

                state.PathAt = i;
                found = true;
                break;
            }

            // The run-up suppression the reference has here is part of the jump
            // branch — a run-up deliberately walks BACKWARDS along the route and
            // the forward-only search cannot see it. It lands with the jumps.
        }

        // ⚠️ Standing on a surface the route does not mention is not a delay to
        // ride out, it is a finished move that went somewhere else, and waiting
        // BOT_OFFPATH_TICKS to admit it is how a bot walks somewhere it cannot
        // get back from. So that case re-plans at once.
        //
        // Not knowing where we are at all is the different case and does get the
        // timer: standing in a gap between two runs is a bot that has to
        // re-plan, and a 28-tick arc is not — which is why the timer lives
        // inside the on-ground branch and an airborne bot touches neither
        // counter.
        if (found)
        {
            state.OffPathTicks = 0;
        }
        else if (onGround)
        {
            if (here >= 0)
                return OffRoute(state, x, y, tick, query, occupancy);

            state.OffPathTicks++;
        }

        if (state.OffPathTicks > BotConstants.OffpathTicks)
        {
            Plan(state, x, y, tick, query, occupancy);
            return PlayerKeys.None;
        }

        if (state.PathAt >= size - 1)
            return WalkToGoal(state, x, hSpeed, onGround, moved, tick, query, occupancy);

        return FollowEdge(state, x, hSpeed, onGround, moved, tick, query, occupancy);
    }

    /// <summary>
    /// The bot has landed somewhere its route does not mention.
    /// </summary>
    /// <remarks>
    /// ⚠️ The edge in progress is the one that put it there, so it is taken away
    /// the way the stuck detector does — otherwise the search hands back the
    /// identical route from the node the bot drifted onto, it walks back, and the
    /// pair of surfaces becomes a loop it never leaves. Unlike the wedged case,
    /// the anti-thrash distance check never fires here, because the bot is
    /// genuinely covering ground.
    /// <para>
    /// ⚠️ <b>But not on the first miss</b> — see
    /// <see cref="BotPathState.FailCount"/>.
    /// </para>
    /// </remarks>
    private PlayerKeys OffRoute(
        BotPathState state, double x, double y, int tick,
        in NavQuery query, IReadOnlyDictionary<long, int>? occupancy)
    {
        state.OffRouteFires++;
        state.OffPathTicks = 0;

        if (state.EdgeFrom == state.FailFrom && state.EdgeTo == state.FailTo)
        {
            state.FailCount++;
        }
        else
        {
            state.FailFrom = state.EdgeFrom;
            state.FailTo = state.EdgeTo;
            state.FailCount = 1;
        }

        if (state.FailCount >= 2)
        {
            state.BlacklistEdge(state.EdgeFrom, state.EdgeTo, tick);
            state.FailCount = 0;
        }

        Plan(state, x, y, tick, query, occupancy);
        return PlayerKeys.None;
    }

    /// <summary>
    /// On the last node of the route: walk the rest of the way to the goal along
    /// the surface, and stop on it.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Being within tolerance of the goal is not the same as stopping
    /// there.</b> A bot arrives at a run — often still falling onto the last
    /// surface — and GG2 keeps a character's horizontal speed for about ten ticks
    /// after the keys come off, so declaring arrival on position alone and then
    /// pressing nothing coasts it thirty to forty pixels past the thing it was
    /// sent to. Measured in the reference on all three legs of the gg_debug walk;
    /// the last one slid into the left wall.
    /// <para>
    /// So inside the tolerance band the bot <em>brakes</em> rather than coasts,
    /// and only counts as arrived once it is on the ground and actually stopped.
    /// Braking is a press against the motion, which is also how a player stops;
    /// the speed floor is what keeps that from becoming a press back the other
    /// way.
    /// </para>
    /// </remarks>
    private PlayerKeys WalkToGoal(
        BotPathState state, double x, double hSpeed, bool onGround, bool moved, int tick,
        in NavQuery query, IReadOnlyDictionary<long, int>? occupancy)
    {
        state.EdgeFrom = -1;
        state.EdgeTo = -1;

        var last = state.Path[^1];
        var node = _graph.Nodes[last];

        // The goal clamped into the node's own span, since NodeFromWorld resolves
        // a goal that is *near* a surface rather than exactly on it.
        var goalX = Math.Max(
            NavGraph.ColumnWorldX(node.X0),
            Math.Min(NavGraph.ColumnWorldX(node.X1), state.GoalX));

        // ⚠️ PathStale disables the whole band. The route being followed is then
        // one to a goal that has already been replaced, and its last node is
        // somewhere the bot was sent before — reaching the end of it is not an
        // arrival at anything.
        if (Math.Abs(x - goalX) <= BotConstants.ArriveTol && !state.PathStale)
        {
            if (onGround && Math.Abs(hSpeed) < 1)
            {
                state.Arrived = true;
                state.ArrivedAt = tick;
                state.HasGoal = false;
                state.FreePath();
                return PlayerKeys.None;
            }

            var brake = hSpeed > 1 ? PlayerKeys.Left
                : hSpeed < -1 ? PlayerKeys.Right
                : PlayerKeys.None;

            // Not stuck: standing still inside the band with nothing to brake is
            // the whole point, and the airborne case is waiting on a landing
            // rather than wedged.
            state.StuckTicks = 0;
            return brake;
        }

        var keys = x < goalX ? PlayerKeys.Right : PlayerKeys.Left;

        // ⚠️ The stuck test here is NOT gated on having pressed something, unlike
        // the one at the end of an edge: this branch always presses a direction,
        // so `moved` alone is the whole question.
        if (!moved)
            state.StuckTicks++;
        else
            state.StuckTicks = 0;

        if (state.StuckTicks >= BotConstants.StuckTicks)
        {
            state.StuckFires++;
            Plan(state, x, state.GoalY, tick, query, occupancy);
            return PlayerKeys.None;
        }

        return keys;
    }

    /// <summary>Steers along the edge between the current node and the next.</summary>
    private PlayerKeys FollowEdge(
        BotPathState state, double x, double hSpeed, bool onGround, bool moved, int tick,
        in NavQuery query, IReadOnlyDictionary<long, int>? occupancy)
    {
        var current = state.Path[state.PathAt];
        var next = state.Path[state.PathAt + 1];

        state.EdgeFrom = current;
        state.EdgeTo = next;

        // ⚠️ The graph is rebuilt on every map change, and a route planned
        // against the old one names nodes that no longer connect — or no longer
        // exist.
        if (!TryFindEdge(current, next, out var edge))
        {
            Plan(state, x, state.GoalY, tick, query, occupancy);
            return PlayerKeys.None;
        }

        var from = _graph.Nodes[current];
        var to = _graph.Nodes[next];
        var column = NavGraph.AnchorColumn(x);

        var dirToNext = (to.X0 + to.X1) > (from.X0 + from.X1) ? 1
            : (to.X0 + to.X1) < (from.X0 + from.X1) ? -1
            : 0;

        // Default: head for the near end of the next surface, a couple of cells
        // in, so the bot commits to standing on it rather than stopping on the
        // boundary.
        int targetColumn;
        if (column < to.X0)
        {
            targetColumn = Math.Min(to.X1, to.X0 + BotConstants.EntryLead);
        }
        else if (column > to.X1)
        {
            targetColumn = Math.Max(to.X0, to.X1 - BotConstants.EntryLead);
        }
        else
        {
            // ⚠️ Already over the next surface's columns without being on it. Two
            // runs a row apart genuinely overlap here — a node's span is in
            // anchor columns and the body box is NAV_BOX_W wide — so the answer
            // is to keep going its way until NodeFromWorld agrees, not to stand
            // still.
            targetColumn = Math.Clamp(column + (dirToNext * BotConstants.EntryLead), to.X0, to.X1);
        }

        var keys = PlayerKeys.None;
        var tracking = false;
        var trackX = 0.0;

        switch (edge.Type)
        {
            case NavEdgeType.DropThrough:
                // The generator takes the drop from the middle of the run, and
                // DOWN disables the platform entirely — both the standing test
                // and the push-out.
                targetColumn = (int)Math.Floor((from.X0 + from.X1) / 2.0);

                if (Math.Abs(column - targetColumn) <= BotConstants.EntryLead)
                    keys |= PlayerKeys.Down;

                break;

            case NavEdgeType.Fall:
            {
                // ⚠️ The generator drops straight down from the cell just past
                // ONE end of the run and carries that column on the edge. Which
                // end cannot be re-derived here: "the column just past the run is
                // inside the landing node" is true of BOTH ends wherever the
                // landing surface reaches under the whole run, and guessing
                // picked the right-hand end whenever it was ambiguous. In the
                // reference that walked a bot on ctf_avanti into the solid block
                // against its right end and wedged it there — stuck, edge
                // blacklisted, ~1,100 ticks for a leg the graph priced at 545 —
                // when the fall it wanted was off the left.
                var takeoff = edge.Takeoff;

                if (takeoff < 0)
                {
                    // A graph cached before falls carried a takeoff. The same
                    // guess as before, which is right whenever only one end
                    // works.
                    takeoff = from.X1 + 1 >= to.X0 && from.X1 + 1 <= to.X1
                        ? from.X1 + 1
                        : from.X0 - 1;
                }

                targetColumn = takeoff > from.X1
                    ? from.X1 + BotConstants.EntryLead
                    : from.X0 - BotConstants.EntryLead;

                // ⚠️ Off the edge the modelled trajectory is a STRAIGHT VERTICAL
                // DROP from the takeoff column — the generator swept straight
                // down from it and credited whatever surface it met — so the
                // bot's job in the air is to hold that column, exactly the way a
                // jump holds its arc. Without this a fall is the one edge kind
                // flown with no plan at all: the bot walks off at whatever speed
                // it had, keeps it, and sails past any landing narrower than the
                // drift.
                //
                // ⚠️ The column is the one the EDGE carries, not the walk-off
                // hint above: that is deliberately past the end of the run, and
                // holding it would aim the drop a cell wide of the sweep that
                // proved it.
                if (!onGround)
                {
                    trackX = NavGraph.ColumnWorldX(takeoff);
                    tracking = true;
                    state.FlyingEdge = true;
                }

                break;
            }

            case NavEdgeType.Walk:
            case NavEdgeType.MoveBox:
                // Nothing beyond the default steering: a walk edge is walked, and
                // a move box carries the bot once it is standing on one.
                break;

            default:
                // ⚠️ Jump, DoubleJump, RocketJump — not implemented. Counted
                // rather than approximated, and left to the stuck detector below.
                // See this type's remarks.
                state.UnsupportedEdgeTicks++;
                break;
        }

        // ⚠️ The steering target is a column to walk to, and once the bot is on
        // it the nudge continues the way the next node lies rather than stopping:
        // a node's span is in anchor columns and two runs a row apart overlap, so
        // standing exactly on the target could otherwise mean standing still for
        // ever.
        var targetX = NavGraph.ColumnWorldX(targetColumn);

        if (x < targetX - BotConstants.SteerTol)
            keys |= PlayerKeys.Right;
        else if (x > targetX + BotConstants.SteerTol)
            keys |= PlayerKeys.Left;
        else if (dirToNext > 0)
            keys |= PlayerKeys.Right;
        else if (dirToNext < 0)
            keys |= PlayerKeys.Left;

        // In the air on a validated trajectory the plan REPLACES the steering
        // entirely — steering aims at a column to walk to and there is no walking
        // to be done. Behind the plan is a press forward, ahead of it a press
        // back, which is a brake and is how a player stops drifting too. The
        // tolerance is a pixel because that is roughly what one tick of a slow
        // arc covers, and chattering the key either side of the line is exactly
        // what holds the average where it belongs.
        if (tracking)
        {
            keys &= ~(PlayerKeys.Left | PlayerKeys.Right);

            if (x < trackX - 1)
                keys |= PlayerKeys.Right;
            else if (x > trackX + 1)
                keys |= PlayerKeys.Left;
        }

        // ⚠️ Gated on actually pressing something, unlike the arrival branch's:
        // a bot standing on its target waiting for a jump window would otherwise
        // report itself stuck.
        if (!moved && keys != PlayerKeys.None)
            state.StuckTicks++;
        else
            state.StuckTicks = 0;

        if (state.StuckTicks >= BotConstants.StuckTicks)
        {
            state.StuckFires++;
            state.BlacklistEdge(current, next, tick);
            Plan(state, x, state.GoalY, tick, query, occupancy);
            return PlayerKeys.None;
        }

        return keys;
    }

    /// <summary>The edge between two nodes, if the graph still has one.</summary>
    /// <remarks>
    /// <c>navEdgeFind</c>. The first match wins; a graph can carry more than one
    /// edge between a pair — a walk and a parallel fall, for instance — and the
    /// search costed whichever it took, so the follower steers the first the
    /// index yields, exactly as the reference does.
    /// </remarks>
    private bool TryFindEdge(int from, int to, out NavEdge edge)
    {
        foreach (var candidate in _graph.EdgesFrom(from))
        {
            if (candidate.To != to)
                continue;

            edge = candidate;
            return true;
        }

        edge = default;
        return false;
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
        in NavQuery query, IReadOnlyDictionary<long, int>? occupancy = null)
    {
        ArgumentNullException.ThrowIfNull(state);

        // The timer is re-armed whatever happens below, staggered nowhere: the
        // reference staggers by player id so a full server does not search on one
        // frame, and a caller here can do the same by offsetting `tick`.
        state.ReplanAt = tick + BotConstants.ReplanTicks;

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

        if (!_search.Find(start, goal, query, _scratch, state.Blacklist, occupancy))
        {
            state.PlanFailUntil = tick + BotConstants.ReplanTicks;
            return false;
        }

        // ⚠️ Only NOW is the old route given up. FreePath is what zeroes the
        // follower's per-route state, and all of that belongs to the route being
        // replaced.
        state.FreePath();
        state.Path.AddRange(_scratch);
        state.PathAt = 0;
        state.PathStale = false;
        state.PlanFailUntil = 0;
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
        // the timer.
        state.ReplanAt = 0;
        state.PlanFailUntil = 0;
        return true;
    }
}
