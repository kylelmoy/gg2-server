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
/// ⚠️ <b>This remark used to say the jump edges were not implemented, and it was
/// stale from <c>c34549b</c> ("the jump branch") onwards</b> — corrected
/// 2026-08-28, after a session picked the queue item it had spawned and found the
/// work already done. <see cref="NavEdgeType.Jump"/>,
/// <see cref="NavEdgeType.DoubleJump"/> and <see cref="NavEdgeType.RocketJump"/>
/// are steered by <see cref="SteerJump"/> and gated by twelve named tests in
/// <c>PathFollowerTests</c>. <b>A stale "not done" costs a session exactly what a
/// stale "done" does</b>, which is the same rule <c>CLAUDE.md</c> states for the
/// sandvich in the other direction.
/// </para>
/// <para>
/// ⚠️ <see cref="NavEdgeType.StickyJump"/> is the one edge kind genuinely
/// unhandled, and it falls to the <c>default</c> arm: steered by nothing and
/// counted in <see cref="BotPathState.UnsupportedEdgeTicks"/>, so the stuck
/// detector takes it away and the search routes round it — the machinery that
/// already exists for an edge that turns out to be untraversable. <b>Do not paper
/// over it with an approximate jump</b>; an arc the generator did not validate is
/// exactly what the whole takeoff apparatus exists to prevent.
/// </para>
/// <para>
/// ✅ <b>Unreachable on every shipped map, measured 2026-08-28</b> by walking all
/// 24 cached graphs in <c>nav/</c> and counting edge kinds:
/// Walk 18,785, Fall 11,558, Jump 67,013, DropThrough 5, DoubleJump 2,382,
/// RocketJump 16,014 — and <b>StickyJump 0</b>. (<see cref="NavEdgeType.MoveBox"/>
/// is 0 too; its arm is a deliberate no-op rather than a gap.) ⚠️ <b>Nothing
/// filters a sticky-jump edge out of the search</b>, so a graph from a generator
/// that starts emitting them would be routed over one and steered by nothing —
/// which is why the <c>default</c> arm counts rather than throws.
/// </para>
/// </remarks>
public sealed class PathFollower
{
    /// <summary>
    /// Where a rocket jump aims: straight down.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Not a simplification of a richer choice — it is the only direction
    /// worth firing.</b> <c>vectorfactor</c> in the rocket's blast is
    /// sqrt(sin⁴ + cos⁴), which is 1 only when the impulse is axis-aligned and
    /// falls to 0.707 at 45°, so an angled rocket jump throws away up to 30% of its
    /// impulse. Down is also the one direction with guaranteed geometry to detonate
    /// against: there is a floor under the takeoff column by construction, and
    /// there may be nothing at all down-and-left of it.
    /// <para>
    /// ⚠️ <b>The aim SNAPS rather than slewing.</b> The turn rate is 8 degrees a
    /// tick at tier 3 and level-to-down is 90 degrees, so honouring the slew would
    /// mean eleven ticks of a bot standing still staring at the floor before every
    /// rocket jump — both a long time to be helpless and nothing like what a player
    /// looks like doing this. A player flicks down and fires.
    /// </para>
    /// </remarks>
    public const double RocketAimDegrees = 270;

    /// <summary>
    /// Applies the follower's launch requests to a bot's outgoing keys and aim —
    /// the one place a route reaches the trigger.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The follower owns LEFT / RIGHT / JUMP / DOWN and cannot fire.</b>
    /// A rocket jump and a mine jump each need one key from the combat half, so
    /// the two are married after both have run — see <see cref="ScriptedBot"/>,
    /// which is the caller this was extracted from.
    /// <para>
    /// ⚠️ <b>The aim is OVERWRITTEN rather than blended.</b> Whatever the bot was
    /// shooting at, the arc it is about to fly is worth more than the shot, and
    /// half an aim is a rocket into a wall beside its own feet.
    /// <c>Character.events/Begin Step.xml</c> reads <c>aimDirection</c> when it
    /// fires the weapon and reads the trigger bit in the same event on the same
    /// tick, so the aim must be down <em>before</em> the trigger is seen —
    /// writing both here is exactly that.
    /// </para>
    /// <para>
    /// ⚠️ <b>SPECIAL, not ATTACK, for the detonation</b>: Minegun's User Event 2
    /// detonates and its User Event 1 would place a second mine. And it rides on
    /// the SAME tick as the follower's JUMP press, because Begin Step runs its
    /// jump branch before its right-click branch — so the blast stacks on
    /// <c>vspeed = -jumpStrength</c> rather than replacing it.
    /// </para>
    /// <para>
    /// ⚠️ <b>It lives here rather than in <see cref="ScriptedBot"/> because a
    /// second copy is a harness that measures a different bot.</b> Anything that
    /// drives a <see cref="PathFollower"/> without this — a fixture, a flight
    /// harness, an evaluator — gets a follower whose rocket and mine jumps are
    /// requested and never fired, and the symptom is an arm that looks refused.
    /// <see cref="EdgeFlights"/> is the harness that needs it.
    /// </para>
    /// </remarks>
    public static void ApplyLaunch(BotPathState state, ref PlayerKeys keys, ref double aimDegrees)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (state.RocketFire || state.MineFire)
        {
            // Straight down for both: a mine that lands at the feet is the whole
            // geometry StickyModel.Launch's measured -14.46 assumes.
            aimDegrees = RocketAimDegrees;
            keys |= PlayerKeys.Attack;
        }

        if (state.MineDetonate)
        {
            aimDegrees = RocketAimDegrees;
            keys |= PlayerKeys.Special;
        }
    }

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
    /// <param name="character">
    /// The whole character, because the takeoff gate needs more of it than a
    /// position: <see cref="JumpModel.Reach"/> replays the motion law off its
    /// class, its run power and its <c>moveStatus</c>.
    /// ⚠️ <see cref="CharacterState.OnGround"/> in particular is load-bearing far
    /// beyond the obvious — it gates the route advance, the off-route branch and
    /// arrival, all for the same reason: an airborne bot is flying a trajectory
    /// with its own end, and the tick it lands is the tick either branch has
    /// anything true to say.
    /// </param>
    /// <param name="tick">The server frame, for every timer here.</param>
    /// <param name="query">Who is asking, for the search.</param>
    /// <param name="occupancy">The team's edge crowding, or null.</param>
    /// <returns>LEFT / RIGHT / JUMP / DOWN. Never an attack or a special.</returns>
    /// <remarks>
    /// The weaponless overload. ⚠️ It means <b>"this bot has no rocket"</b>, not
    /// "do not care": a rocket-jump edge is then never taken off from, which is the
    /// conservative answer and the one every caller that does not model a weapon
    /// wants. A zeroed <see cref="WeaponState"/> answers the same way — it is not
    /// loaded and it is not ready — but saying so in an overload is better than
    /// relying on a defaulted value type, which in this codebase is routinely not a
    /// valid one.
    /// </remarks>
    public PlayerKeys Step(
        BotPathState state,
        in CharacterState character,
        int tick,
        in NavQuery query,
        BotOccupancy? occupancy = null)
    {
        var none = default(WeaponState);
        return Step(state, in character, in none, tick, in query, occupancy);
    }

    public PlayerKeys Step(
        BotPathState state,
        in CharacterState character,
        in WeaponState weapon,
        int tick,
        in NavQuery query,
        BotOccupancy? occupancy = null)
    {
        ArgumentNullException.ThrowIfNull(state);

        var x = character.X;
        var y = character.Y;
        var hSpeed = character.HSpeed;
        var onGround = character.OnGround;

        // Cleared up front and set only by the in-flight branch, so every early
        // return leaves it false.
        state.FlyingEdge = false;

        // ⚠️ Cleared up front like FlyingEdge, so a tick that does not reach the
        // takeoff gate cannot leave last tick's rocket request standing. The
        // reference clears both at the top of botPathKeys for the same reason.
        state.RocketFire = false;

        // The sticky jump's two presses, cleared for the same reason. ⚠️ The
        // COUNTDOWN is not cleared here — it has to survive the ticks between
        // placing the mine and jumping on it, which is the whole difference
        // between this manoeuvre and a rocket jump.
        state.MineFire = false;
        state.MineDetonate = false;

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

            // ⚠️ A run-up deliberately walks BACKWARDS along the route, and the
            // forward-only search above cannot see that. Without this a bot that
            // backs up to build speed lands on a node the route does not mention
            // *ahead* of it, which the block below reads as a finished move that
            // went somewhere else — so it fires an off-route event and, on the
            // second consecutive miss, blacklists the very edge it was preparing
            // for. That is the single reason the obvious "just let it back up"
            // fix is not a two-line change.
            //
            // ⚠️ PathAt is deliberately NOT moved. The edge being prepared for has
            // to stay the current one for the whole run-up, or the follower is
            // handed a different edge half way through and steers to the wrong
            // takeoff column.
            //
            // The suppression is unconditional rather than "found somewhere
            // earlier on the route", because the run-up follows walk edges
            // through the GRAPH and not along the route: the floor a bot needs to
            // build speed on is often a surface the route never mentions, and
            // that case is exactly the one worth allowing. What bounds it is not
            // this test but the takeoff budget — a run-up is only ever entered
            // from the gate, and BOT_TAKEOFF_PATIENCE ends it whether or not it
            // worked.
            if (!found && state.RunupAt >= 0)
                found = true;
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
            return WalkToGoal(state, x, y, hSpeed, onGround, moved, tick, query, occupancy);

        return FollowEdge(state, in character, in weapon, moved, tick, query, occupancy);
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
    /// <summary>
    /// Landed somewhere the route does not go: take the edge away if it has now
    /// failed twice, and re-plan from where the bot actually is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>A DELIBERATE DEPARTURE FROM THE REFERENCE, and the first one in the
    /// bot layer.</b> <c>botPathKeys.gml:178-192</c> counts a miss whether or not
    /// an edge is running. The reference's own comment says what the blacklist is
    /// for — "two surfaces still cannot become a loop" — and then that is exactly
    /// what happens, because the <c>(-1,-1)</c> "no edge running" sentinel resets
    /// the count for the real edge and the ban is spent on a pair
    /// <c>BlacklistEdge</c> discards. Ignoring the sentinel makes the rule do what
    /// the reference says it is for.
    /// </para>
    /// <para>
    /// <b>Measured</b>, ten seeds a row, full rounds on <c>ctf_truefort</c>, on
    /// top of the carrier ceiling: uncontested captures a match 7.3 → 7.4 with
    /// the carrier loop gone from every seed that still had one, and — the part
    /// worth having — <b>contested play improves too</b>, because the loop was
    /// costing attacks there as well. 6v6 went from 0.4 captures a match to
    /// <b>1.0</b> and 5% conversion to 8%, on 84 grabs → 118; 6v2 5.8 → 6.0;
    /// 6v4 3.4 → 3.6.
    /// </para>
    /// <para>
    /// ⚠️ <b>The bot layer is not held to the reference — see CLAUDE.md.</b> The
    /// wire and observable simulation are; a bot's decisions are this project's
    /// own, the reference's bot layer is treated as incomplete, and much of it is
    /// expected to be replaced by a learned policy later.
    /// </para>
    /// </remarks>
    private PlayerKeys OffRoute(
        BotPathState state, double x, double y, int tick,
        in NavQuery query, BotOccupancy? occupancy)
    {
        state.OffRouteFires++;
        state.OffPathTicks = 0;

        // ⚠️ ONLY a real edge counts, and this is a DELIBERATE DEPARTURE from the
        // reference — see the remark on this method.
        //
        // EdgeFrom/EdgeTo are -1 whenever no edge is running: WalkToGoal zeroes
        // them, and so does FreePath. The reference counts those sentinel misses
        // like any other, which resets FailCount for the real edge that is
        // actually failing — and then spends the ban on (-1,-1), which
        // BlacklistEdge discards because the nodes are negative. So the escape
        // never reaches the edge it is for.
        //
        // Measured on ctf_truefort: a carrier looped a pair of surfaces for
        // 26,000 ticks, firing off-route in an exact 35-tick cycle of one real
        // edge (449->395) followed by two sentinels, with the ban count stuck at
        // zero the whole time.
        if (state.EdgeFrom >= 0 && state.EdgeTo >= 0)
        {
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
        }

        // ⚠️ Banned by the REMEMBERED pair, not the current one. The two are the
        // same in the reference because it only gets here with a live edge; here
        // the count can survive a sentinel miss, so the current pair may be
        // (-1,-1) at the moment the second real miss lands.
        if (state.FailCount >= 2 && state.FailFrom >= 0 && state.FailTo >= 0)
        {
            state.BlacklistEdge(state.FailFrom, state.FailTo, tick);
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
        BotPathState state, double x, double y, double hSpeed, bool onGround, bool moved, int tick,
        in NavQuery query, BotOccupancy? occupancy)
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

                // ⚠️ Off the counts BEFORE the route is freed — the edges can only
                // be found through the path that is about to be cleared.
                // `botPathFree` does this on arrival, on every re-plan and on
                // death; the re-plan case is already covered above, and the
                // idempotence flag is what makes the overlap harmless.
                occupancy?.Remove(state);
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

            // ⚠️ y, not state.GoalY. See the remark on Plan.
            Plan(state, x, y, tick, query, occupancy);
            return PlayerKeys.None;
        }

        return keys;
    }

    /// <summary>What the per-edge steering decided this tick.</summary>
    /// <remarks>
    /// One struct rather than six locals threaded through arguments, because the
    /// takeoff gate writes four of them and the reference's own version is a
    /// block of <c>var</c>s shared by every branch.
    /// </remarks>
    private struct Steering
    {
        /// <summary>The anchor column to walk toward.</summary>
        public int TargetColumn;

        /// <summary>Press against your own motion instead of steering.</summary>
        public bool Braking;

        /// <summary>Ask for a jump this tick. Edge-triggered — see <see cref="Press"/>.</summary>
        public bool WantJump;

        /// <summary>Fly the plan instead of steering: <see cref="TrackX"/> replaces the keys.</summary>
        public bool Tracking;

        /// <summary>Where the validated trajectory says the bot should be by now.</summary>
        public double TrackX;

        /// <summary>
        /// Press nothing at all this tick.
        /// </summary>
        /// <remarks>
        /// ⚠️ The reference's mid-air bail is `return 0` from the whole of
        /// botPathKeys — no keys, not "no steering". Returning to the default
        /// steering instead leaves the bot walking toward the takeoff column while
        /// it falls, which is a press on an arc this branch exists to stop steering.
        /// </remarks>
        public bool Abandon;

        /// <summary>Apply the rocket arc's speed gate to the tracker's presses.</summary>
        public bool TrackGate;

        /// <summary>The arc's own horizontal velocity, for that gate.</summary>
        public double TrackNeedVx;

        /// <summary>The bot's speed along the jump, for that gate.</summary>
        public double TrackAlong;

        /// <summary>Which way the arc travels, for that gate.</summary>
        public int TrackDir;

        /// <summary>Ask the combat half to aim here and fire, for a rocket jump.</summary>
        /// <remarks>
        /// ⚠️ <b>The follower cannot fire.</b> It owns LEFT / RIGHT / JUMP / DOWN
        /// and the aim comes from combat alone, so a rocket jump has to be handed
        /// over rather than pressed — <c>botInputUpdate</c> applies both AFTER
        /// <c>botCombatUpdate</c> has run, which is the only place that can see
        /// both halves.
        /// </remarks>
        public bool RocketFire;

        /// <summary>Place a mine at the feet this tick — a sticky jump's phase one.</summary>
        public bool MineFire;

        /// <summary>
        /// Detonate this tick — phase two, and the same tick as the jump.
        /// </summary>
        public bool MineDetonate;
    }

    /// <summary>Steers along the edge between the current node and the next.</summary>
    private PlayerKeys FollowEdge(
        BotPathState state, in CharacterState character, in WeaponState weapon, bool moved, int tick,
        in NavQuery query, BotOccupancy? occupancy)
    {
        var x = character.X;
        var hSpeed = character.HSpeed;
        var onGround = character.OnGround;

        var current = state.Path[state.PathAt];
        var next = state.Path[state.PathAt + 1];

        // ⚠️ A new edge gets a fresh takeoff budget: BOT_TAKEOFF_PATIENCE is about
        // one jump refusing to happen, not about how long the bot has been
        // walking. A run-up belongs to the edge it was started for and goes with
        // it.
        if (current != state.EdgeFrom || next != state.EdgeTo)
        {
            state.TakeoffTicks = 0;
            state.RunupAt = -1;
        }

        state.EdgeFrom = current;
        state.EdgeTo = next;

        // ⚠️ The graph is rebuilt on every map change, and a route planned
        // against the old one names nodes that no longer connect — or no longer
        // exist. ⚠️ AND the traveller's own arcs can go while the route stands:
        // see the remark on TryFindEdge.
        if (!TryFindEdge(_graph, state, current, next, out var edge))
        {
            state.EdgeUnroutableFires++;

            // ⚠️ character.Y, not state.GoalY. See the remark on Plan.
            Plan(state, x, character.Y, tick, query, occupancy);
            return PlayerKeys.None;
        }

        var from = _graph.Nodes[current];
        var to = _graph.Nodes[next];
        var column = NavGraph.AnchorColumn(x);

        var dirToNext = (to.X0 + to.X1) > (from.X0 + from.X1) ? 1
            : (to.X0 + to.X1) < (from.X0 + from.X1) ? -1
            : 0;

        var steer = default(Steering);

        // Default: head for the near end of the next surface, a couple of cells
        // in, so the bot commits to standing on it rather than stopping on the
        // boundary.
        if (column < to.X0)
        {
            steer.TargetColumn = Math.Min(to.X1, to.X0 + BotConstants.EntryLead);
        }
        else if (column > to.X1)
        {
            steer.TargetColumn = Math.Max(to.X0, to.X1 - BotConstants.EntryLead);
        }
        else
        {
            // ⚠️ Already over the next surface's columns without being on it. Two
            // runs a row apart genuinely overlap here — a node's span is in
            // anchor columns and the body box is NAV_BOX_W wide — so the answer
            // is to keep going its way until NodeFromWorld agrees, not to stand
            // still.
            steer.TargetColumn = Math.Clamp(column + (dirToNext * BotConstants.EntryLead), to.X0, to.X1);
        }

        switch (edge.Type)
        {
            case NavEdgeType.DropThrough:
                // The generator takes the drop from the middle of the run, and
                // DOWN disables the platform entirely — both the standing test
                // and the push-out.
                steer.TargetColumn = (int)Math.Floor((from.X0 + from.X1) / 2.0);
                break;

            case NavEdgeType.Fall:
                SteerFall(state, onGround, in edge, in from, in to, ref steer);
                break;

            case NavEdgeType.Jump:
            case NavEdgeType.DoubleJump:
            case NavEdgeType.RocketJump:
            case NavEdgeType.StickyJump:
                SteerJump(
                    state, in character, in weapon, in edge, in from, in to,
                    current, next, column, dirToNext, tick, in query, occupancy, ref steer);
                break;

            case NavEdgeType.Walk:
            case NavEdgeType.MoveBox:
                // Nothing beyond the default steering: a walk edge is walked, and
                // a move box carries the bot once it is standing on one.
                break;

            default:
                // Every edge kind the generator emits is handled above. This is
                // here so a graph from a newer generator degrades into a counted
                // number rather than into silently flying an arc nobody modelled.
                state.UnsupportedEdgeTicks++;
                break;
        }

        var keys = edge.Type == NavEdgeType.DropThrough
                   && Math.Abs(column - steer.TargetColumn) <= BotConstants.EntryLead
            ? PlayerKeys.Down
            : PlayerKeys.None;

        // ⚠️ The steering target is a direction hint, not a place to stop.
        // Standing still on top of it is the one thing that must not happen: a
        // cell is six world pixels, so a bot can sit within any sane tolerance of
        // the column it is steering for and still not be on the node that column
        // belongs to — which is exactly what the reference did the first time
        // this ran, parking 4px short of a one-cell step and re-planning the
        // identical route every 45 ticks for ever. So once it is on the target,
        // keep nudging the way the next node lies and let NodeFromWorld be the
        // thing that decides it has arrived.
        var targetX = NavGraph.ColumnWorldX(steer.TargetColumn);

        if (x < targetX - BotConstants.SteerTol)
            keys |= PlayerKeys.Right;
        else if (x > targetX + BotConstants.SteerTol)
            keys |= PlayerKeys.Left;
        else if (dirToNext > 0)
            keys |= PlayerKeys.Right;
        else if (dirToNext < 0)
            keys |= PlayerKeys.Left;

        // Braking on the ground before the arc starts, and the in-flight tracker
        // once it has: both REPLACE the steering keys rather than adding to them,
        // so the bot cannot brake and steer in one tick.
        //
        // ⚠️ Neither caps the approach. Braking on the way to the takeoff column
        // — so the bot arrives already travelling at the arc's speed rather than
        // accelerating into it from a stop — sounds better, and measured no
        // better on koth_valley (three trials at <90, 273 and 277 frames against
        // 220 and 239 without it). It stays out.
        if (steer.Braking)
        {
            keys &= ~(PlayerKeys.Left | PlayerKeys.Right);

            if (hSpeed > 0)
                keys |= PlayerKeys.Left;
            else if (hSpeed < 0)
                keys |= PlayerKeys.Right;
        }
        else if (steer.Tracking)
        {
            keys &= ~(PlayerKeys.Left | PlayerKeys.Right);

            // Behind the plan is a press forward, ahead of it a press back, which
            // is a brake and is how a player stops drifting too. The tolerance is
            // a pixel because that is roughly what one tick of a slow arc covers,
            // and chattering the key either side of the line is exactly what
            // holds the average where it belongs.
            //
            // ⚠️ On a ROCKET arc each press is gated on speed as well as position
            // — see Steering.TrackGate. The world-space test is not "behind the
            // schedule" on its own: an arc travelling left is behind when the bot
            // is to the RIGHT of the plan, so which branch is catching up depends
            // on the direction, and `TrackAlong` already carries that sign. The
            // non-rocket path keeps its unconditional presses exactly as they were.
            if (x < steer.TrackX - 1)
            {
                if (!steer.TrackGate
                    || (steer.TrackDir > 0 && steer.TrackAlong < steer.TrackNeedVx)
                    || (steer.TrackDir < 0 && steer.TrackAlong > steer.TrackNeedVx))
                {
                    keys |= PlayerKeys.Right;
                }
            }
            else if (x > steer.TrackX + 1)
            {
                if (!steer.TrackGate
                    || (steer.TrackDir > 0 && steer.TrackAlong > steer.TrackNeedVx)
                    || (steer.TrackDir < 0 && steer.TrackAlong < steer.TrackNeedVx))
                {
                    keys |= PlayerKeys.Left;
                }
            }
        }

        // ⚠️ Before the jump press and before the stuck counter, because
        // this is the reference's `return 0` and it returns no keys at all.
        if (steer.Abandon)
        {
            state.RocketFire = false;

            // ⚠️ And the setup, or a bot that abandoned an edge mid-countdown
            // detonates under a route it is no longer following.
            state.MineFire = false;
            state.MineDetonate = false;
            state.StickySetup = -1;
            state.StuckTicks = 0;
            return PlayerKeys.None;
        }

        keys |= Press(state, steer.WantJump);

        // Out of the per-edge steering and onto the state, which is what the caller
        // reads — see BotPathState.RocketFire.
        state.RocketFire = steer.RocketFire;
        state.MineFire = steer.MineFire;
        state.MineDetonate = steer.MineDetonate;

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

            // ⚠️ character.Y, not state.GoalY. See the remark on Plan.
            Plan(state, x, character.Y, tick, query, occupancy);
            return PlayerKeys.None;
        }

        return keys;
    }

    /// <summary>
    /// ⚠️ Jump is edge-triggered, not held.
    /// </summary>
    /// <remarks>
    /// <c>Character.events/Begin Step.xml</c> jumps on <c>pressedKeys &amp; $80</c>
    /// — a rising edge — so a bot holding JUMP down jumps exactly once and then
    /// never again. Alternating gives a bot that still wants to jump a fresh
    /// press every other tick until it leaves the ground.
    /// </remarks>
    private static PlayerKeys Press(BotPathState state, bool wantJump)
    {
        if (!wantJump)
        {
            state.JumpHeld = false;
            return PlayerKeys.None;
        }

        if (state.JumpHeld)
        {
            state.JumpHeld = false;
            return PlayerKeys.None;
        }

        state.JumpHeld = true;
        return PlayerKeys.Jump;
    }

    /// <summary>Walks off the end the edge names, then holds that column.</summary>
    /// <remarks>
    /// ⚠️ <b>Which end cannot be re-derived here</b>, which is why the edge
    /// carries it: "the column just past the run is inside the landing node" is
    /// true of BOTH ends wherever the landing surface reaches under the whole
    /// run — what a platform standing on a wider ledge looks like — and the old
    /// guess picked the right-hand end whenever it was ambiguous. On ctf_avanti
    /// that walked a bot into the solid block against its right end and wedged it
    /// there (stuck, edge blacklisted, ~1,100 ticks for a leg priced at 545) when
    /// the fall it wanted was off the left.
    /// <para>
    /// ⚠️ Off the edge the modelled trajectory is a STRAIGHT VERTICAL DROP from
    /// the takeoff column — the generator swept straight down from it and
    /// credited whatever surface it met — so the bot's job in the air is to hold
    /// that column, exactly the way a jump holds its arc. Without this a fall is
    /// the one edge kind flown with no plan at all: the bot walks off at whatever
    /// speed it had, keeps it, and sails past any landing narrower than the
    /// drift.
    /// </para>
    /// <para>
    /// ⚠️ The column held is the one the EDGE carries, not the walk-off hint:
    /// that is deliberately past the end of the run, and holding it would aim the
    /// drop a cell wide of the sweep that proved it.
    /// </para>
    /// </remarks>
    private static void SteerFall(
        BotPathState state, bool onGround, in NavEdge edge,
        in NavNode from, in NavNode to, ref Steering steer)
    {
        var takeoff = edge.Takeoff;

        if (takeoff < 0)
        {
            // A graph cached before falls carried a takeoff. The same guess as
            // before, which is right whenever only one end works: the column just
            // past the run, if that column is inside the landing.
            takeoff = from.X1 + 1 >= to.X0 && from.X1 + 1 <= to.X1
                ? from.X1 + 1
                : from.X0 - 1;
        }

        steer.TargetColumn = takeoff > from.X1
            ? from.X1 + BotConstants.EntryLead
            : from.X0 - BotConstants.EntryLead;

        if (onGround)
            return;

        steer.TrackX = NavGraph.ColumnWorldX(takeoff);
        steer.Tracking = true;
        state.FlyingEdge = true;
    }

    /// <summary>
    /// A jump or double-jump arc: the takeoff gate on the ground, the
    /// position tracker in the air.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>An arc is flown as a TRAJECTORY, not as a speed.</b> The generator
    /// validated one specific arc — leave the takeoff column, cover
    /// <see cref="NavEdge.Bucket"/> pixels of ground a tick, be over the landing
    /// when it comes back down — and every clearance cell it checked and every
    /// surface it ruled out is about that arc and no other. Holding a speed
    /// instead is what the reference used to do and is not the same thing: the
    /// bot starts from a standstill, takes four or five ticks to reach a slow
    /// arc's speed, and any nudge in the air is permanent. Tracking a
    /// <em>position</em> corrects both, because being behind is a reason to press
    /// and being ahead is a reason to brake. <b>Measured over every class, rise,
    /// ledge width and run-up: 12-19% of jumps landed on the node they were aimed
    /// at before, 100% after.</b>
    /// </remarks>
    private void SteerJump(
        BotPathState state, in CharacterState character, in WeaponState weapon, in NavEdge edge,
        in NavNode from, in NavNode to, int current, int next,
        int column, int dirToNext, int tick,
        in NavQuery query, BotOccupancy? occupancy, ref Steering steer)
    {
        // ⚠️ The takeoff column is carried ON THE EDGE because the generator
        // SEARCHED for it rather than assuming it. It is usually the end of the
        // run facing the landing — but where the landing surface is also what
        // ends the run (climbing onto a crate) the body is flush against it there
        // and the only flyable arcs start a few cells back. Re-deriving it from
        // the node's own columns would jump from a column the build already
        // rejected and quietly turn those edges back into fictions.
        var takeoff = edge.Takeoff >= 0 ? edge.Takeoff : (dirToNext >= 0 ? from.X1 : from.X0);

        // ⚠️ Ticks are counted rather than read off vspeed, which would otherwise
        // give the phase of the arc for free: vspeed pins at 10 once the fall
        // reaches terminal velocity, and every phase after that reads as the same
        // tick.
        var needVx = edge.Bucket;
        var flightTicks = edge.Ticks;
        var jumpDistance = needVx * flightTicks;

        // ⚠️ The generator's own direction, not the one inferred from node
        // midpoints: it is the side of the takeoff column the landing is on, and
        // a wide surface can straddle the midpoint test.
        var jumpDir = dirToNext;

        if (to.X0 > takeoff)
            jumpDir = 1;
        else if (to.X1 < takeoff)
            jumpDir = -1;

        // ⚠️ Speed ALONG THE JUMP, not speed. This is the whole of the run-up
        // problem: the old gate was `abs(hspeed) <= needVx + 1`, which is
        // satisfied by a bot drifting the wrong way at 2px/tick — so it left the
        // ground with backwards momentum and the tracker spent the arc undoing
        // it, and GG2 gives almost no authority in the air. Reported from play at
        // ctf_truefort 4470,840: "the bot has momentum going left, and can't gain
        // enough speed going right to make the jump to the right stairs."
        var along = character.HSpeed * jumpDir;
        var isRocket = edge.Type == NavEdgeType.RocketJump;

        if (!character.OnGround)
        {
            state.AirTicks++;

            // ⚠️ The rocket did not go off, so this is not the arc that was
            // planned. `moveStatus` 1 is "rocketing/mining myself" and is set by
            // the blast itself, so a rocket-jump edge in the air without it is an
            // ORDINARY jump — 57px of rise against the arc's 276 — and steering it
            // toward a trajectory it cannot be on only carries the bot further
            // from where an ordinary jump was always going to put it.
            //
            // The landing is then adjudicated by machinery that already exists and
            // already has the right policy: it comes down on a surface the route
            // does not mention, the off-route branch fires, and that branch gives
            // the edge ONE retry before blacklisting it — which is exactly right
            // here, because the common causes are transient (an enemy walked into
            // the rocket, an evasive hop got there first) and the uncommon one is
            // not.
            //
            // ⚠️ `AirTicks > 1` rather than `>= 1`: the blast lands in the same
            // tick's simulation as the takeoff press, so tick one is always
            // moveStatus 0.
            if ((isRocket || edge.Type == NavEdgeType.StickyJump)
                && state.AirTicks > 1
                && character.MoveStatus != 1)
            {
                steer.Abandon = true;
                return;
            }

            // ⚠️ The second impulse, on a double-jump arc. Rejump is the tick the
            // generator proved the arc at, counted from takeoff the same way
            // AirTicks is, and -1 on every other edge kind — so this is inert for
            // the others without testing the type.
            //
            // ⚠️ It is a PRESS, not a hold. Begin Step takes the second jump on
            // the rising edge and refuses it while `vspeed <= -jumpStrength`, so
            // holding JUMP through the arc would consume the edge on the takeoff
            // tick and leave nothing to press. The bit was released the moment
            // WantJump went false at takeoff, which is what makes setting it again
            // here an edge.
            //
            // ⚠️ CanDoubleJump is checked even though the search will not hand
            // these edges to anything else: a route outlives the plan that made
            // it, and a bot can change class mid-life while a path is still
            // installed. Pressing JUMP on a class with no second jump is a wasted
            // bit rather than a bug, but the arc it is flying is then a fiction
            // and the sooner it fails the off-route test the better.
            if (edge.Rejump >= 0 && !state.Rejumped && query.CanDoubleJump
                && state.AirTicks >= edge.Rejump)
            {
                steer.WantJump = true;
                state.Rejumped = true;
                state.DoubleJumps++;
            }

            // Where the plan says to be now, clamped at the landing so a bot that
            // is late keeps pressing toward it rather than aiming past it.
            steer.TrackX = NavGraph.ColumnWorldX(takeoff)
                + (jumpDir * Math.Min(jumpDistance, needVx * state.AirTicks));

            steer.Tracking = true;

            // ⚠️ The rocket arc's tracker is SPEED-GATED where every other arc's
            // is not, and it is the same gate RocketModel.Reach models. With no
            // friction under it, a press that has already reached the arc's own vx
            // gains nothing — an unfrictioned character just holds it — and
            // pressing on from there is what made the real tracker limit-cycle.
            //
            // ⚠️ The test is on speed ALONG THE JUMP, so which of the tracker's two
            // branches is catching up and which is braking depends on jumpDir: an
            // arc travelling left is behind when the bot is to the RIGHT of the
            // plan. `along` already carries that sign.
            steer.TrackGate = isRocket;
            steer.TrackNeedVx = needVx;
            steer.TrackAlong = along;
            steer.TrackDir = jumpDir;

            state.FlyingEdge = true;
            return;
        }

        if (Math.Abs(column - takeoff) > BotConstants.JumpLead)
        {
            Approach(state, character, takeoff, needVx, along, column, in query, ref steer);
            return;
        }

        Gate(
            state, in character, in weapon, in edge, from: current, to: next, in from,
            takeoff, needVx, flightTicks, jumpDir, along, tick, in query, occupancy, ref steer);
    }

    /// <summary>Walking toward the takeoff column, or backing up to a run-up mark.</summary>
    private void Approach(
        BotPathState state, in CharacterState character, int takeoff, double needVx, double along,
        int column, in NavQuery query, ref Steering steer)
    {
        if (state.RunupAt >= 0)
        {
            // Backing up. Two halves, and the switch between them is what makes
            // it a run-up rather than a retreat: walk to the mark, then turn round
            // and accelerate at the takeoff column over the whole distance just
            // walked.
            //
            // Arrival is BOT_ENTRY_LEAD rather than exact, for the same reason
            // every other target column is: a cell is six world pixels and a bot
            // can sit inside any sane tolerance of a column without being on it.
            if (Math.Abs(column - state.RunupAt) <= BotConstants.EntryLead)
            {
                state.RunupAt = -1;
                steer.TargetColumn = takeoff;
            }
            else
            {
                steer.TargetColumn = state.RunupAt;
            }

            return;
        }

        // Approaching. Arrive at about the arc's speed rather than sprinting at
        // the gate and braking on top of it: a bot that arrives at 9 and only then
        // starts shedding speed drifts past the one-cell window while it does,
        // backs up, and comes at it again.
        //
        // ⚠️ Only over the last stretch, and that distance is load-bearing.
        // Bleeding speed for the whole leg makes every walk between two jumps
        // happen at 2-4 px/tick instead of 7-9 — measured, it cost 30-50% on all
        // three valley scenarios while changing nothing about whether they
        // arrived. BOT_RUNUP_CELLS does double duty: the distance a bot needs to
        // build the arc's speed is also the distance it needs to shed it.
        if (along > needVx + BotConstants.JumpVtol
            && Math.Abs(column - takeoff) <= BotConstants.RunupCells)
        {
            steer.Braking = true;
        }
        else
        {
            steer.TargetColumn = takeoff;
        }
    }

    /// <summary>
    /// At the takeoff column. Three ways this can go, and only one is a jump.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The rule is "not moving away from the jump", not "moving at exactly
    /// the arc's speed", and the difference is measured.</b> GG2 applies the same
    /// control factor in the air as on the ground, so a bot that leaves slow can
    /// be pressed back up to the arc's speed within two or three ticks by the
    /// tracker — which is why slow takeoffs were already landing. A bot that
    /// leaves with speed <em>against</em> the jump cannot: it spends those ticks
    /// undoing the drift and covers a good ten pixels the wrong way first, on an
    /// arc whose height is already fixed. Demanding the full speed at the gate was
    /// tried and costs a lot for nothing — 647 → 868 and 674 → 997 ticks on the
    /// valley scenarios, with no more arrivals.
    /// <para>
    /// ⚠️ <c>along &gt;= 0</c> accepts a STANDSTILL, which is right for nine arcs
    /// in ten and catastrophic for the rest — an arc asking for most of the
    /// class's ceiling has no surplus for the tracker to make the acceleration
    /// ramp back out of. Measured over the cached graphs, <b>3,310 of 62,069 jump
    /// edges are unflyable by a heavy that way, and on koth_corinth 96% of a
    /// heavy's routes to the point cross one</b>. So the speed test is
    /// <see cref="JumpModel.Reach"/>, which replays the tracker against the speed
    /// the bot has right now — and <c>along &gt;= 0</c> is kept on top of it,
    /// because the reach model is honest about distance but knows nothing about
    /// the clearance cells the generator sampled along the arc, and a bot that
    /// leaves backwards and catches up flies through none of them.
    /// </para>
    /// </remarks>
    private void Gate(
        BotPathState state, in CharacterState character, in WeaponState weapon, in NavEdge edge,
        int from, int to,
        in NavNode fromNode, int takeoff, double needVx, double flightTicks, int jumpDir,
        double along, int tick, in NavQuery query,
        BotOccupancy? occupancy, ref Steering steer)
    {
        // ⚠️ Counted here and nowhere else. The budget is ticks spent AT the gate
        // refusing to leave; counting the approach as well would spend it on a
        // long walk and fire a jump from the middle of the run, which is what
        // broke valley-spawn-to-point the first time the reference wrote this.
        state.TakeoffTicks++;

        // ⚠️ How much ground this jump has to cover, measured from the takeoff
        // COLUMN and not from the bot's x — the difference is not cosmetic. The
        // tracker's target is clamped at needVx * flightTicks from the anchor, so
        // that is a hard ceiling on how far the bot can be steered. Plenty of arcs
        // are costed with no margin at all (koth_gallery n86 → n58 is 0.70 px/tick
        // over 17.2 ticks against a 12px gap: exactly 12 for exactly 12), so
        // asking from x — up to BOT_JUMP_LEAD cells further out at the gate —
        // asks for 22px of an arc that can never deliver more than 12. That
        // refuses the jump on every approach, runs up, comes back and refuses
        // again, silently: nothing blacklisted and nothing off route, because the
        // bot is doing exactly what it was told.
        var landingColumn = jumpDir > 0 ? _graph.Nodes[to].X0 : _graph.Nodes[to].X1;
        var need = Math.Abs(NavGraph.ColumnWorldX(landingColumn) - NavGraph.ColumnWorldX(takeoff));

        // ...minus whatever ground the bot has already covered INSIDE the takeoff
        // column.
        //
        // ⚠️ A column is not a point. Every x across one whole cell — six world
        // pixels — resolves to the same anchor column, and ColumnWorldX hands back
        // the NEAR end of that run rather than its middle, so the line above
        // assumes the bot is at the near end of a 6px band. Crediting the
        // difference is sound because the tracker aims at an ABSOLUTE world x, not
        // at a displacement from wherever the bot left the ground: leaving from
        // further along does not move the landing point, it means there is less
        // ground between the bot and it.
        //
        // Worth having rather than a rounding tidy-up: a one-column perch that
        // overhangs the way it is jumping gives no run-up at all, so every arc off
        // one is gated at a standstill, and six pixels of floor is the difference
        // between 0 and about 2.3 px/tick for a heavy. Modelled over the
        // twenty-four cached graphs, 209 of the 293 arcs otherwise refused become
        // flyable once the gate stops asking for six pixels the bot has already
        // walked.
        //
        // ⚠️ Only ever a credit, never a debit, and clamped to one cell. Behind
        // the anchor the honest number is bigger, and asking for it is the mistake
        // above: an arc costed with no margin can never deliver more, and a gate
        // that asks for it refuses on every approach for ever.
        var lead = jumpDir * (character.X - NavGraph.ColumnWorldX(takeoff));
        lead = Math.Clamp(lead, 0, NavConstants.CellSize);
        need = Math.Max(0, need - lead);

        var isRocket = edge.Type == NavEdgeType.RocketJump;

        // ⚠️ WHICH reach model, and it is not a detail. A rocket-jumping character
        // is under moveStatus 1, where Begin Step's switch gives controlFactor 0.65
        // and frictionFactor 1 — and a friction of 1 means horizontal speed does not
        // bleed at all for the whole flight. JumpModel models the ordinary
        // 0.85/1.15 and would be wrong in BOTH directions here: it under-rates how
        // well a slow takeoff catches up (nothing takes the speed back off) and
        // over-rates how easily a hot one sheds. RocketModel's own remarks have the
        // numbers.
        var isSticky = edge.Type == NavEdgeType.StickyJump;

        // ⚠️ A mine jump flies under moveStatus 1 exactly as a rocket jump does —
        // controlFactor 0.65, friction 1 — so it takes the SAME reach model. The
        // one thing a rocket has that a mine does not is NAV_RJ_SPEEDUP, which
        // RocketModel.Reach does not use. StickyModel.Reach delegates rather
        // than copying, so there is one integration to keep correct.
        var reach = isRocket
            ? RocketModel.Reach(in character, needVx, flightTicks, along)
            : isSticky
                ? StickyModel.Reach(in character, needVx, flightTicks, along)
                : JumpModel.Reach(in character, needVx, flightTicks, along);

        // ⚠️ Jumping without the rocket is the WORST available outcome. The arc
        // rises 276px where an ordinary jump rises 57, so the bot would leave the
        // ground, track a trajectory it cannot be on, and land somewhere the route
        // does not mention — which costs an off-route fire and, on the second one,
        // a blacklist on a perfectly good edge. So the gate refuses to leave rather
        // than leaving badly.
        var rocketReady = !isRocket || RocketModel.Ready(in character, in weapon);

        // ⚠️ The same refusal for a sticky jump, and it matters MORE here: the
        // arc needs a mine that has already been placed and settled, so leaving
        // the ground without one is not merely a short jump, it is a short jump
        // that also wasted the setup.
        //
        // ⚠️⚠️ BUT ONLY TO *START* ONE. Once StickySetup is running the mine is
        // already on the ground, and the launcher's readiness has nothing to do
        // with the arc — which matters because PLACING the mine puts the weapon
        // straight onto its refire cooldown. Requiring readiness throughout made
        // the gate close on the tick after the placement, every single time, and
        // the abandon branch below then threw the setup away. Zero sticky jumps
        // across 48 real edges on four maps, and the synthetic unit test could
        // not see it because its fixture weapon is
        // permanently ready.
        var stickyReady = !isSticky
            || state.StickySetup >= 0
            || StickyModel.Ready(in character, in weapon);

        if (rocketReady
            && stickyReady
            && along >= 0
            && along <= needVx + BotConstants.JumpVtol
            && reach + BotConstants.JumpReachTol >= need)
        {
            // ⚠️ A STICKY JUMP DOES NOT LEAVE ON THE TICK ITS GATE OPENS, and it
            // is the only edge kind that does not. The mine has to be placed and
            // has to settle first, so the gate opening starts a countdown and the
            // launch happens NavConstants.SjSetupTicks later. Everything else
            // here — the reach model, the run-up, the patience timer — is shared,
            // which is why this is a branch inside the gate rather than a second
            // gate beside it.
            if (isSticky)
            {
                if (state.StickySetup < 0)
                {
                    // Phase one: put the mine down and stand still. ⚠️ -1 rather
                    // than 0 is "not set up", so a zeroed state cannot read as a
                    // mine that has already settled.
                    steer.MineFire = true;
                    state.StickySetup = NavConstants.SjSetupTicks;
                }
                else if (--state.StickySetup > 0)
                {
                    // ⚠️ Decrement THEN test, so the launch lands exactly
                    // SjSetupTicks after the placing tick. Testing first and
                    // decrementing after waits one tick longer than the cost the
                    // generator charged for the mine's travel, and an arc costed
                    // for five ticks flown after six is a different arc.
                }
                else
                {
                    // Phase two, and both presses are THIS tick: Begin Step runs
                    // its jump branch before its right-click branch, so the
                    // blast's motion_add lands on top of `vspeed = -jumpStrength`.
                    // A tick later would throw the jump away.
                    Launch(state, ref steer);
                    steer.MineDetonate = true;
                    state.StickySetup = -1;
                    state.StickyJumps++;
                }

                return;
            }

            Launch(state, ref steer);

            // The hand-off: ScriptedBot applies this after the combat half has run,
            // which is the only place that can see both. ⚠️ Straight down is not a
            // simplification of a richer choice — `vectorfactor` in the rocket's
            // blast is sqrt(sin⁴ + cos⁴), which is 1 only when the impulse is
            // axis-aligned and falls to 0.707 at 45°, so an angled rocket jump
            // throws away up to 30% of its impulse. Down is also the one direction
            // with guaranteed geometry to detonate against: there is a floor under
            // the takeoff column by construction, and there may be nothing at all
            // down-and-left of it.
            if (isRocket)
            {
                steer.RocketFire = true;
                state.RocketJumps++;
            }
        }
        else if (isSticky && state.StickySetup >= 0)
        {
            // ⚠️ The gate closed with a mine already down. Abandon rather than
            // stand on a live mine waiting for a window that has moved: the
            // countdown is only meaningful for the approach that started it, and
            // the off-route machinery gives the edge one retry before blacklisting
            // it, which is the right policy for a transient.
            state.StickySetup = -1;
            steer.Abandon = true;
        }
        // ⚠️ Waiting for the launcher, and NOT falling through to the run-up below.
        // Refire is 30 ticks against BOT_TAKEOFF_PATIENCE's 60, so the overwhelmingly
        // common reason to be here is a rocket that is a second away — and the answer
        // is to stand on the takeoff column and wait for it. Letting this case reach
        // the run-up would walk the bot away from a jump it is about to be able to
        // make and back again, which is the shuffle the patience budget exists to end
        // rather than a thing to spend it on.
        //
        // Standing still is safe here where it is not elsewhere: TakeoffTicks is
        // counting, so this cannot become a bot waiting for ever, and the stuck
        // detector is not fooled — it fires only when keys are pressed and nothing
        // moves, and braking to a stop presses nothing once the bot is already still.
        // ⚠️ AND a sticky jump whose launcher is cold, which this branch was
        // rocket-only for and should never have been. A demoman arriving at a
        // takeoff just after firing — or freshly spawned, where the refire starts
        // cold — has exactly the rocket's problem: the manoeuvre is a second away
        // and the right answer is to stand on the takeoff column and wait for it.
        // Falling through to the run-up instead walks the bot off the column and
        // back, burning the takeoff patience budget until the edge is blacklisted,
        // which is a refusal with no bad arc anywhere in it.
        else if ((isRocket && !rocketReady) || (isSticky && !stickyReady))
        {
            // ⚠️ BRAKING, where the reference writes `targetCol = takeoffCol`, and
            // the two are the same behaviour in two codebases whose default
            // steering differs. The reference's default is a pure column
            // comparison, so aiming at the column the bot is already on presses
            // nothing — which is what its own comment relies on ("braking to a stop
            // presses nothing once the bot is already still", and "the stuck
            // detector is not fooled"). This port's default is a DIRECTION HINT
            // rather than a place to stop: it presses on along the route even when
            // the bot is exactly on its target column, on purpose, because a cell is
            // six world pixels and standing still on top of one is how a bot never
            // quite reaches the node. Measured: aiming at the column here pressed
            // RIGHT for twelve consecutive ticks and the stuck detector banned a
            // perfectly good edge at tick 12, forty-eight ticks before the patience
            // budget it was supposed to be spending.
            //
            // Braking is the behaviour the reference describes: press against your
            // own motion, and press nothing once still. A bot that has drifted off
            // the column stops here and is walked back by Approach on the next tick,
            // once it is more than BOT_JUMP_LEAD cells away.
            steer.Braking = true;
        }
        else if (along > needVx + BotConstants.JumpVtol)
        {
            // Faster than the arc allows. Worth bleeding off even though the
            // tracker can correct it: with no key held GG2 sheds horizontal speed
            // by only about 13% a tick, so a bot arriving at 9 is still over 3
            // some eight ticks later.
            steer.Braking = true;
        }
        else
        {
            // Moving away from the jump, or too slow for this arc. ⚠️ Backing up
            // is the only answer that works: pressing forward from here would walk
            // the bot off the end of the run it is standing on, which is usually a
            // ledge, and jumping anyway is what produced the short arcs in the
            // first place — the bot arcs out with its old momentum still on it and
            // lands back where it started.
            //
            // ⚠️ This used to clamp the run-up into the node's OWN columns, and
            // that clamp was the bug the whole change is about: a node one anchor
            // column wide, which is what the top step of a staircase is, resolves
            // the clamp back to the column the bot is already standing on, so the
            // run-up was a no-op and the jump happened from a standstill anyway.
            // JumpModel.RunupColumn follows WALK edges instead.
            state.RunupAt = state.RunupAt >= 0
                ? state.RunupAt
                : JumpModel.RunupColumn(
                    _graph, from, takeoff, jumpDir, BotConstants.RunupCells, in query);

            steer.TargetColumn = state.RunupAt;
        }

        // ⚠️ The escape hatch, because a gate that can refuse is a gate that can
        // refuse FOR EVER — a bot on a run too short to build the speed, or wedged
        // against a wall it cannot back away from, would otherwise work the gate
        // for the rest of the round with every other detector reading it as
        // healthy: it is not stuck (it is moving) and it is not off route (it is
        // standing exactly where the route says). Jump on whatever it has instead.
        // That either works, or it lands back where it started and the off-route
        // machinery already knows what to do with an edge that keeps doing that.
        if (state.TakeoffTicks >= BotConstants.TakeoffPatience)
        {
            // ⚠️ Two seconds at the gate with no rocket is not a bot to shove off
            // the edge — it would fly an ordinary jump on a 276px arc. The edge is
            // taken away instead, which is the honest description of it:
            // untraversable right now, by a bot that has been trying for two
            // seconds. The search re-plans without it, the ban expires in
            // BOT_BLACKLIST_TICKS, and by then the launcher has reloaded several
            // times over.
            if (isRocket && !rocketReady)
            {
                state.BlacklistEdge(from, to, tick);
                state.BlacklistFires++;
                Plan(state, character.X, character.Y, tick, query, occupancy);
                return;
            }

            steer.Braking = false;
            Launch(state, ref steer);
        }
    }

    /// <summary>Commits to the arc: ask for the press and reset the per-arc state.</summary>
    private static void Launch(BotPathState state, ref Steering steer)
    {
        steer.WantJump = true;
        state.AirTicks = 0;
        state.TakeoffTicks = 0;
        state.RunupAt = -1;
        state.Rejumped = false;
    }

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
