using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Bots;

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
/// <b>This runs every tick, unlike target selection, which re-decides every
/// fifteen.</b> Aiming may lag and look human; steering may not — a jump missed
/// by one tick is a bot standing at the edge of a gap for ever.
/// </para>
/// <para>
/// <b>Each edge kind is steered by the geometry its generator used, never by
/// a general rule.</b> A fall leaves past the end of a surface, a drop-through
/// goes down through its middle, a jump leaves from a searched column and then
/// flies a specific arc. Taking those from the same numbers the build used is
/// what keeps the follower honest about what it planned.
/// </para>
/// <para>
/// <see cref="NavEdgeType.Jump"/>, <see cref="NavEdgeType.DoubleJump"/> and
/// <see cref="NavEdgeType.RocketJump"/> are steered by <see cref="SteerJump"/>
/// and gated by twelve named tests in <c>PathFollowerTests</c>.
/// </para>
/// <para>
/// <see cref="NavEdgeType.StickyJump"/> is the one edge kind genuinely
/// unhandled, and it falls to the <c>default</c> arm: steered by nothing and
/// counted in <see cref="BotPathState.UnsupportedEdgeTicks"/>, so the stuck
/// detector takes it away and the search routes round it — the machinery that
/// already exists for an edge that turns out to be untraversable. <b>Do not paper
/// over it with an approximate jump</b>; an arc the generator did not validate is
/// exactly what the whole takeoff apparatus exists to prevent.
/// </para>
/// <para>
/// <b>Unreachable on every shipped map</b>: walking all 24 cached graphs in
/// <c>nav/</c> and counting edge kinds gives Walk 18,785, Fall 11,558,
/// Jump 67,013, DropThrough 5, DoubleJump 2,382, RocketJump 16,014, and
/// <b>StickyJump 0</b>. (<see cref="NavEdgeType.MoveBox"/> is 0 too; its arm is a
/// deliberate no-op rather than a gap.) <b>Nothing filters a sticky-jump edge out
/// of the search</b>, so a graph from a generator that starts emitting them would
/// be routed over one and steered by nothing — which is why the <c>default</c>
/// arm counts rather than throws.
/// </para>
/// </remarks>
public sealed partial class PathFollower
{
    /// <summary>
    /// Where a rocket jump aims: straight down.
    /// </summary>
    /// <remarks>
    /// <b>Not a simplification of a richer choice — it is the only direction
    /// worth firing.</b> <c>vectorfactor</c> in the rocket's blast is
    /// sqrt(sin⁴ + cos⁴), which is 1 only when the impulse is axis-aligned and
    /// falls to 0.707 at 45°, so an angled rocket jump throws away up to 30% of its
    /// impulse. Down is also the one direction with guaranteed geometry to detonate
    /// against: there is a floor under the takeoff column by construction, and
    /// there may be nothing at all down-and-left of it.
    /// <para>
    /// <b>The aim SNAPS rather than slewing.</b> The turn rate is 8 degrees a
    /// tick at tier 3 and level-to-down is 90 degrees, so honouring the slew would
    /// mean eleven ticks of a bot standing still staring at the floor before every
    /// rocket jump — both a long time to be helpless and nothing like what a player
    /// looks like doing this. A player flicks down and fires.
    /// </para>
    /// </remarks>
    public const double RocketAimDegrees = 270;

    /// <summary>
    /// How far off vertical to tilt a rocket whose muzzle overhangs the lip of
    /// the run it is taking off from.
    /// </summary>
    /// <remarks>
    /// The muzzle is 20px out, so this puts it about 8px back along the run and
    /// 18px down — past the lip that the straight-down shot falls off, while
    /// keeping <c>vectorfactor</c> at about 0.85 rather than the 0.707 a 45°
    /// blast would cost. See <see cref="BotPathState.RocketAim"/>.
    /// </remarks>
    public const double LipAimDegrees = 24;

    /// <summary>
    /// Applies the follower's launch requests to a bot's outgoing keys and aim —
    /// the one place a route reaches the trigger.
    /// </summary>
    /// <remarks>
    /// <b>The follower owns LEFT / RIGHT / JUMP / DOWN and cannot fire.</b>
    /// A rocket jump and a mine jump each need one key from the combat half, so
    /// the two are married after both have run — see <see cref="ScriptedBot"/>,
    /// the caller this was extracted from.
    /// <para>
    /// <b>The aim is OVERWRITTEN rather than blended.</b> Whatever the bot was
    /// shooting at, the arc it is about to fly is worth more than the shot, and
    /// half an aim is a rocket into a wall beside its own feet.
    /// <c>Character.events/Begin Step.xml</c> reads <c>aimDirection</c> when it
    /// fires the weapon and reads the trigger bit in the same event on the same
    /// tick, so the aim must be down <em>before</em> the trigger is seen —
    /// writing both here is exactly that.
    /// </para>
    /// <para>
    /// <b>SPECIAL, not ATTACK, for the detonation</b>: Minegun's User Event 2
    /// detonates and its User Event 1 would place a second mine. And it rides on
    /// the SAME tick as the follower's JUMP press, because Begin Step runs its
    /// jump branch before its right-click branch — so the blast stacks on
    /// <c>vspeed = -jumpStrength</c> rather than replacing it.
    /// </para>
    /// <para>
    /// <b>It lives here rather than in <see cref="ScriptedBot"/> because a
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
            // ⚠️ Straight down for a MINE, whatever the rocket is doing: a mine
            // that lands at the feet is the whole geometry StickyModel.Launch's
            // measured -14.46 assumes, and it is dropped rather than fired so the
            // lip problem below does not apply to it.
            aimDegrees = state.MineFire ? RocketAimDegrees : state.RocketAim;
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
    /// This bot's lane — one cost per graph edge, or empty for the graph's own
    /// costs.
    /// </summary>
    /// <remarks>
    /// <b>A property rather than a parameter on <see cref="Step"/>, because a
    /// lane belongs to the bot and not to the tick.</b> Each bot owns its own
    /// follower (<c>MatchRunner</c> says so explicitly), a lane is assigned once
    /// when the bot joins — <see cref="BotRoles.Lane"/> — and it must not change
    /// while the bot is walking: a bot that re-lanes mid-route is
    /// indistinguishable from one that cannot make up its mind, which is the same
    /// argument <see cref="BotRoles.Assign"/> makes about roles.
    /// <para>
    /// ⚠️ <b>Cleared by whoever changes the graph.</b> The costs are indexed by
    /// edge position and an edge index only means anything to one build of a
    /// graph, so a lane held across a map change prices the new map's edges with
    /// the old map's field. <see cref="ScriptedBot.SetMap"/> is where that is
    /// handled, the same place the route and the occupancy flag are dropped.
    /// </para>
    /// <para>
    /// <see cref="ReadOnlyMemory{T}"/> rather than a span because it is a field;
    /// the search takes the span.
    /// </para>
    /// </remarks>
    public ReadOnlyMemory<double> Lane { get; set; }

    /// <summary>
    /// The precomputed distance tables feeding A\*'s heuristic, or null for the
    /// straight-line estimate the search has always used.
    /// </summary>
    /// <remarks>
    /// <b>A property for the same reason <see cref="Lane"/> is one</b>: it
    /// belongs to the graph the bot is on and not to the tick. Unlike a lane it
    /// is not per bot at all — one set serves every bot on the server, and it is
    /// read-only once derived, so sharing it across threaded workers is safe.
    /// <para>
    /// ⚠️ <b>Turning this on CHANGES ROUTES, and that is not a bug.</b>
    /// <see cref="NavConstants.SearchWeight"/> makes this a <em>weighted</em>
    /// A\*, so the heuristic decides which route comes back and not merely how
    /// fast it comes back. See <c>docs/TRAPS-BOTS.md</c>, "Shipping the
    /// precomputed distances", for what that costs and why the flag is off by
    /// default.
    /// </para>
    /// <para>
    /// ⚠️ <b>Cleared by whoever changes the graph</b>, exactly as the lane is:
    /// the tables are indexed by node and a node index only means anything to
    /// one build of a graph.
    /// </para>
    /// </remarks>
    /// <summary>
    /// The terrain, for the one decision that cannot be made from the graph.
    /// </summary>
    /// <remarks>
    /// <b>Optional, and null behaves exactly as before.</b> A rocket jump is the
    /// only thing here that needs to know where the floor actually is: the blast is
    /// created 20px along the aim, so aimed down it spawns at the body's centre,
    /// and at a takeoff column the body usually overhangs the lip of its run. The
    /// nav graph cannot answer this — a node's span ends for gates and walls as
    /// well as for edges, so "the takeoff column is the node's last" is true of
    /// most arcs and was measured to be far too broad a test (it made the corpus
    /// sweep 38% worse when used as one).
    /// <para>
    /// ⚠️ <b>Read-only, and it does not make the follower stateful.</b> It is the
    /// same <see cref="CollisionWorld"/> the tick already passes around; the
    /// follower asks it a question and stores nothing.
    /// </para>
    /// </remarks>
    public CollisionWorld? Collision { get; set; }

    public NavDistanceSet? Distances { get; set; }

    /// <summary>
    /// Plain jumps are taken where a held jump is predicted to land, and flown
    /// holding the key (N1), with the gate and the tracker as the fallback. On by
    /// default; needs <see cref="Collision"/>, and does nothing without it.
    /// </summary>
    /// <remarks>
    /// <c>gg2-rl</c> <c>docs/PLAN.md</c>, navigation: bots used to brake to the
    /// arc's planned speed, jump at about half the people's speed and chatter the
    /// keys in the air. People run at the obstacle and hold the key.
    /// </remarks>
    public bool HeldFlight { get; set; } = true;

    /// <summary>
    /// Whether a held flight may plan a scout's second jump (N3): on
    /// double-jump edges and plain ones, at people's timing, and as a rescue in
    /// the air. Off, a scout's held flights are single jumps, as before N3.
    /// </summary>
    public bool HeldDoubles { get; set; } = true;

    /// <summary>
    /// Whether a held flight may plan two phases (N4): let go or pressed away
    /// until a switch tick, then toward, where no one key lands.
    /// </summary>
    public bool HeldSwitchPlans { get; set; } = true;

    /// <summary>
    /// Whether some held step jumps are face hops (N5): run into the step, stand
    /// a moment, and hop from standing, as people take a third of theirs. Off by
    /// default; <see cref="ScriptedBot"/> sets it each tick for tiers 1-4 with no
    /// combat target, so tier 5 and a fight never stand still for it.
    /// </summary>
    public bool FaceHops { get; set; }

    /// <summary>
    /// Whether the bot hops now and then while running along a walk edge (N8),
    /// as people do about once every 13 seconds of running, half of them in
    /// pairs or runs. Off by default; set with <see cref="FaceHops"/>.
    /// </summary>
    public bool RunHops { get; set; }

    /// <summary>
    /// Whether a fall is flown held (N12): in the air, a key the predictor lands
    /// on the destination, kept while it still does, with the tracker only where
    /// none does.
    /// </summary>
    public bool HeldFalls { get; set; } = true;

    /// <summary>
    /// Advances one bot's route by a tick and returns the movement keys.
    /// </summary>
    /// <param name="state">The bot's navigation state, updated in place.</param>
    /// <param name="character">
    /// The whole character, because the takeoff gate needs more of it than a
    /// position: <see cref="JumpModel.Reach"/> replays the motion law off its
    /// class, its run power and its <c>moveStatus</c>.
    /// <see cref="CharacterState.OnGround"/> in particular is load-bearing far
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
    /// The weaponless overload. It means <b>"this bot has no rocket"</b>, not
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

        // Cleared up front like FlyingEdge, so a tick that does not reach the
        // takeoff gate cannot leave last tick's rocket request standing. The
        // reference clears both at the top of botPathKeys for the same reason.
        state.RocketFire = false;

        // The sticky jump's two presses, cleared for the same reason. The
        // COUNTDOWN is not cleared here — it has to survive the ticks between
        // placing the mine and jumping on it, which is the whole difference
        // between this manoeuvre and a rocket jump.
        state.MineFire = false;
        state.MineDetonate = false;

        if (!state.HasGoal)
            return PlayerKeys.None;

        // Half a pixel, not zero: good_move_contact_solid works at 1/8 pixel
        // precision, so a bot grinding against a wall jitters by a fraction
        // without going anywhere.
        var moved = Math.Abs(x - state.LastX) > 0.5 || Math.Abs(y - state.LastY) > 0.5;
        state.LastX = x;
        state.LastY = y;

        // A rescue owns the keys while it runs (NS3).
        if (state.Rescuing && Rescue(state, in character, tick, query, occupancy, out var rescued))
            return rescued;

        // Planning is on the timer even when it FAILS, which is the whole
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

        // Only while standing on something. NodeFromWorld tolerates being a
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
            Stood(state, here, tick);

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

            // A run-up deliberately walks BACKWARDS along the route, and the
            // forward-only search above cannot see that. Without this a bot that
            // backs up to build speed lands on a node the route does not mention
            // *ahead* of it, which the block below reads as a finished move that
            // went somewhere else — so it fires an off-route event and, on the
            // second consecutive miss, blacklists the very edge it was preparing
            // for. That is the single reason the obvious "just let it back up"
            // fix is not a two-line change.
            //
            // PathAt is deliberately NOT moved. The edge being prepared for has
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

        // Standing on a surface the route does not mention is not a delay to
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
    /// Landed somewhere the route does not go: take the edge away if it has
    /// failed twice, and re-plan from where the bot actually is.
    /// </summary>
    /// <remarks>
    /// The edge in progress is taken away the way the stuck detector does —
    /// otherwise the search hands back the identical route from the node the bot
    /// drifted onto, it walks back, and the pair of surfaces becomes a loop it
    /// never leaves. Unlike the wedged case, the anti-thrash distance check never
    /// fires here, because the bot is genuinely covering ground. But not on the
    /// first miss — see <see cref="BotPathState.FailCount"/>.
    /// <para>
    /// <b>A deliberate departure from the reference.</b>
    /// <c>botPathKeys.gml:178-192</c> counts a miss whether or not an edge is
    /// running. The reference's own comment says what the blacklist is for — "two
    /// surfaces still cannot become a loop" — and then that is exactly what
    /// happens, because the <c>(-1,-1)</c> "no edge running" sentinel resets the
    /// count for the real edge and the ban is spent on a pair
    /// <c>BlacklistEdge</c> discards. Ignoring the sentinel makes the rule do what
    /// the reference says it is for.
    /// </para>
    /// <para>
    /// Measured over ten seeds of full rounds on <c>ctf_truefort</c>, on top of
    /// the carrier ceiling: uncontested captures a match 7.3 → 7.4 with the
    /// carrier loop gone from every seed that still had one, and contested play
    /// improves too, because the loop was costing attacks there as well. 6v6 went
    /// from 0.4 captures a match to <b>1.0</b> and 5% conversion to 8%, on 84
    /// grabs → 118; 6v2 5.8 → 6.0; 6v4 3.4 → 3.6.
    /// </para>
    /// <para>
    /// <b>The bot layer is not held to the reference — see CLAUDE.md.</b> The
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

        // ONLY a real edge counts, and this is a DELIBERATE DEPARTURE from the
        // reference — see the remark on this method.
        //
        // EdgeFrom/EdgeTo are -1 whenever no edge is running: WalkToGoal zeroes
        // them, and so does FreePath. The reference counts those sentinel misses
        // like any other, which resets FailCount for the real edge that is
        // actually failing — and then spends the ban on (-1,-1), which
        // BlacklistEdge discards because the nodes are negative. So the escape
        // never reaches the edge it is for.
        //
        // On ctf_truefort a carrier looped a pair of surfaces for 26,000 ticks,
        // firing off-route in an exact 35-tick cycle of one real edge (449->395)
        // followed by two sentinels, with the ban count stuck at zero throughout.
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

        // Banned by the REMEMBERED pair, not the current one. The two are the
        // same in the reference because it only gets here with a live edge; here
        // the count can survive a sentinel miss, so the current pair may be
        // (-1,-1) at the moment the second real miss lands.
        if (state.FailCount >= 2 && state.FailFrom >= 0 && state.FailTo >= 0)
        {
            state.FailCount = 0;

            // Searched before it is banned (NS3).
            if (TryRescue(state, state.FailFrom, state.FailTo, tick))
                return PlayerKeys.None;

            state.BlacklistEdge(state.FailFrom, state.FailTo, tick);
        }
        else if (state.EdgeFrom >= 0 && state.EdgeTo >= 0 && CycleRescue(state, tick))
        {
            // A cycle of misses no ban sees (NS3b).
            return PlayerKeys.None;
        }

        Plan(state, x, y, tick, query, occupancy);
        return PlayerKeys.None;
    }

    /// <summary>
    /// On the last node of the route: walk the rest of the way to the goal along
    /// the surface, and stop on it.
    /// </summary>
    /// <remarks>
    /// <b>Being within tolerance of the goal is not the same as stopping
    /// there.</b> A bot arrives at a run — often still falling onto the last
    /// surface — and GG2 keeps a character's horizontal speed for about ten ticks
    /// after the keys come off, so declaring arrival on position alone and then
    /// pressing nothing coasts it thirty to forty pixels past the thing it was
    /// sent to (measured in the reference on all three legs of the gg_debug walk;
    /// the last one slid into the left wall).
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

        // PathStale disables the whole band. The route being followed is then
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

                // Off the counts BEFORE the route is freed — the edges can only
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

        // The stuck test here is NOT gated on having pressed something, unlike
        // the one at the end of an edge: this branch always presses a direction,
        // so `moved` alone is the whole question.
        if (!moved)
            state.StuckTicks++;
        else
            state.StuckTicks = 0;

        if (state.StuckTicks >= BotConstants.StuckTicks)
        {
            state.StuckFires++;

            // y, not state.GoalY. See the remark on Plan.
            Plan(state, x, y, tick, query, occupancy);
            return PlayerKeys.None;
        }

        return keys;
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

        // A new edge gets a fresh takeoff budget: BOT_TAKEOFF_PATIENCE is about
        // one jump refusing to happen, not about how long the bot has been
        // walking. A run-up belongs to the edge it was started for and goes with
        // it.
        if (current != state.EdgeFrom || next != state.EdgeTo)
        {
            state.TakeoffTicks = 0;
            state.RunupAt = -1;
            state.HeldDir = 0;
        }

        state.EdgeFrom = current;
        state.EdgeTo = next;

        // The graph is rebuilt on every map change, and a route planned against
        // the old one names nodes that no longer connect — or no longer exist.
        // The traveller's own arcs can go while the route stands too: see the
        // remark on TryFindEdge.
        if (!TryFindEdge(_graph, state, current, next, out var edge))
        {
            state.EdgeUnroutableFires++;

            // character.Y, not state.GoalY. See the remark on Plan.
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
            // Already over the next surface's columns without being on it. Two
            // runs a row apart genuinely overlap here — a node's span is in
            // anchor columns and the body box is NAV_BOX_W wide — so the answer
            // is to keep going its way until NodeFromWorld agrees, not to stand
            // still.
            steer.TargetColumn = Math.Clamp(column + (dirToNext * BotConstants.EntryLead), to.X0, to.X1);
        }

        if (state.RunHopAir && onGround)
            RunHopLanded(state, tick);

        // N8b: a running hop in the air holds its direction. It is not the edge's
        // jump, and a jump edge's air steering would fly it as one.
        if (state.RunHopAir)
        {
            steer.Hold = true;
            steer.HoldDir = state.RunHopDir;
        }
        else
        switch (edge.Type)
        {
            case NavEdgeType.DropThrough:
                // The generator takes the drop from the middle of the run, and
                // DOWN disables the platform entirely — both the standing test
                // and the push-out.
                steer.TargetColumn = (int)Math.Floor((from.X0 + from.X1) / 2.0);
                break;

            case NavEdgeType.Fall:
                SteerFall(state, in character, onGround, in edge, in from, in to, next, ref steer);
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

        // N8: now and then a hop, running along a walk edge or toward a jump's
        // take-off, unless the edge's own steering has the keys.
        if (RunHops && Collision is { } ground && onGround && !state.RunHopAir && state.RunupAt < 0
            && !steer.WantJump && !steer.Hold && !steer.Braking && !steer.Tracking && !steer.Abandon)
        {
            var hopDir = 0;

            if (edge.Type == NavEdgeType.Walk)
            {
                hopDir = dirToNext;
            }
            else if (edge.Type is NavEdgeType.Jump or NavEdgeType.DoubleJump or NavEdgeType.RocketJump or NavEdgeType.StickyJump)
            {
                var takeoffX = NavGraph.ColumnWorldX(edge.Takeoff >= 0 ? edge.Takeoff : (dirToNext >= 0 ? from.X1 : from.X0));
                hopDir = Math.Sign(takeoffX - x);
            }

            if (hopDir != 0)
                RunHop(state, ground, in character, hopDir, tick, ref steer);
        }

        var keys = edge.Type == NavEdgeType.DropThrough
                   && Math.Abs(column - steer.TargetColumn) <= BotConstants.EntryLead
            ? PlayerKeys.Down
            : PlayerKeys.None;

        // The steering target is a direction hint, not a place to stop.
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
        // Neither caps the approach. Braking on the way to the takeoff column
        // — so the bot arrives already travelling at the arc's speed rather than
        // accelerating into it from a stop — sounds better, and measured no
        // better on koth_valley (three trials at <90, 273 and 277 frames against
        // 220 and 239 without it). It stays out.
        if (steer.Hold)
        {
            // A held flight: one key, or none, chosen by where it lands.
            keys &= ~(PlayerKeys.Left | PlayerKeys.Right);

            if (steer.HoldDir > 0)
                keys |= PlayerKeys.Right;
            else if (steer.HoldDir < 0)
                keys |= PlayerKeys.Left;
        }
        else if (steer.Braking)
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
            // On a ROCKET arc each press is gated on speed as well as position
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

        // Before the jump press and before the stuck counter, because this is the
        // reference's `return 0` and it returns no keys at all.
        if (steer.Abandon)
        {
            state.RocketFire = false;

            // And the setup, or a bot that abandoned an edge mid-countdown
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

        // Gated on actually pressing something, unlike the arrival branch's:
        // a bot standing on its target waiting for a jump window would otherwise
        // report itself stuck.
        if (!moved && keys != PlayerKeys.None)
            state.StuckTicks++;
        else
            state.StuckTicks = 0;

        if (state.StuckTicks >= BotConstants.StuckTicks)
        {
            state.StuckFires++;

            // Searched before it is banned (NS3).
            if (TryRescue(state, current, next, tick))
            {
                state.StuckTicks = 0;
                return PlayerKeys.None;
            }

            state.BlacklistEdge(current, next, tick);

            // character.Y, not state.GoalY. See the remark on Plan.
            Plan(state, x, character.Y, tick, query, occupancy);
            return PlayerKeys.None;
        }

        return keys;
    }

    /// <summary>
    /// Jump is edge-triggered, not held.
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
}
