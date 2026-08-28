using Gg2.Bots;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// The path follower's spine — <c>botPathKeys.gml</c> and <c>botPathPlan.gml</c>.
/// </summary>
/// <remarks>
/// Every test here drives <see cref="PathFollower.Step"/> directly with a
/// position and an on-ground flag rather than through a simulation, because what
/// is under test is the decision and not the physics.
/// <see cref="PathFollowerFixtureTests"/> is where a leg is actually walked.
/// <para>
/// ⚠️ <b>The jump edges are not implemented</b> (phase 7 item 4b-ii), so no
/// fixture here has one on a route it expects to be followed. The one that does
/// asserts it is <em>counted and abandoned</em>, which is the behaviour that has
/// to hold until they land.
/// </para>
/// </remarks>
public sealed class PathFollowerTests
{
    /// <summary>row, x0, x1, flags, door, gate.</summary>
    private static double[] Node(int row, int x0, int x1) => [row, x0, x1, 0, 0, 0];

    /// <summary>from, to, type, bucket, ticks, cost, gate, takeoff, rejump.</summary>
    /// <remarks>
    /// bucket is the arc's horizontal speed and ticks its duration, and on a jump
    /// edge they are the whole trajectory: the follower flies bucket * ticks
    /// pixels of ground from the takeoff column. On a walk edge they are ignored,
    /// which is why they default to the cost.
    /// </remarks>
    private static double[] Edge(
        int from, int to, double cost, NavEdgeType type = NavEdgeType.Walk,
        int takeoff = -1, double bucket = 0, double ticks = 0, int rejump = -1) =>
        [from, to, (double)type, bucket, ticks == 0 ? cost : ticks, cost, 0, takeoff, rejump];

    private static NavGraph Build(double[][] nodes, double[][] edges)
    {
        var sorted = edges.OrderBy(e => e[NavConstants.EdgeFrom]).ToArray();
        return NavCache.Parse(NavCacheTests.WriteCache(400, 200, nodes, sorted), "fixture", 1);
    }

    /// <summary>
    /// One long floor, split into three surfaces a bot can walk along.
    /// </summary>
    /// <remarks>
    /// Every node is on the same row, so a body standing on any of them resolves
    /// there, and the columns are far enough apart that no two tie.
    /// </remarks>
    private static NavGraph Floor() =>
        Build(
            [Node(10, 0, 9), Node(10, 20, 29), Node(10, 40, 49)],
            [
                Edge(0, 1, 66), Edge(1, 0, 66),
                Edge(1, 2, 66), Edge(2, 1, 66),
            ]);

    /// <summary>Where a body stands to be on this node, at this column.</summary>
    private static (double X, double Y) Stand(NavGraph graph, int node, int column) =>
        (NavGraph.ColumnWorldX(column), graph.StandY(node));

    /// <summary>
    /// A character standing (or flying) at a chosen place.
    /// </summary>
    /// <remarks>
    /// ⚠️ <see cref="CharacterState.Fresh"/>, never <c>new CharacterState()</c>:
    /// six of its fields name a roster slot and a zeroed one claims slot 0 last
    /// hurt it — and <c>RunPower</c> starts at zero, so a hand-built character
    /// that skipped the factory would stand still and the takeoff gate would ask
    /// <see cref="JumpModel.Reach"/> about a class that cannot accelerate.
    /// </remarks>
    /// <summary>A Rocketlauncher with rounds in it and off cooldown.</summary>
    /// <remarks>
    /// ⚠️ The rest of <see cref="WeaponState"/> is defaulted deliberately —
    /// the rocket gate reads only these two — but a zeroed one is NOT loaded and NOT
    /// ready, which is the conservative answer and what every other test here wants.
    /// </remarks>
    private static WeaponState Loaded(double ammo = 4) =>
        new() { ReadyToShoot = ammo > 0, AmmoCount = ammo };

    private static CharacterState At(
        double x, double y, double hSpeed = 0, bool onGround = true,
        PlayerClass playerClass = PlayerClass.Soldier)
    {
        var character = CharacterState.Fresh(playerClass, Team.Red);
        character.X = x;
        character.Y = y;
        character.HSpeed = hSpeed;
        character.OnGround = onGround;
        return character;
    }

    /// <summary>
    /// A bot with a goal plans a route and walks toward it.
    /// </summary>
    /// <remarks>
    /// The spine end to end at its simplest: <see cref="PathFollower.SetGoal"/>
    /// snaps, the first <see cref="PathFollower.Step"/> plans, and the keys point
    /// the right way.
    /// </remarks>
    [Fact]
    public void ABotWithAGoalPlansAndWalksTowardIt()
    {
        var graph = Floor();
        var follower = new PathFollower(graph);
        var state = new BotPathState();

        var start = Stand(graph, 0, 4);
        var goal = Stand(graph, 2, 45);

        Assert.True(follower.SetGoal(state, goal.X, goal.Y));

        var keys = follower.Step(state, At(start.X, start.Y, 0, true), 0, new NavQuery(Team.Red));

        Assert.Equal([0, 1, 2], state.Path);
        Assert.Equal(1, state.Replans);
        Assert.Equal(PlayerKeys.Right, keys);

        // ...and the other way round, from the far end.
        var back = new BotPathState();
        Assert.True(follower.SetGoal(back, Stand(graph, 0, 4).X, Stand(graph, 0, 4).Y));

        var far = Stand(graph, 2, 45);
        Assert.Equal(
            PlayerKeys.Left,
            follower.Step(back, At(far.X, far.Y, 0, true), 0, new NavQuery(Team.Red)));
    }

    /// <summary>
    /// No goal is no keys, and no search either.
    /// </summary>
    /// <remarks>
    /// The reference returns 0 before anything else happens. It matters because a
    /// bot without a goal is the ordinary state between objectives, and planning
    /// there would be a full A* per bot per frame for nothing.
    /// </remarks>
    [Fact]
    public void NoGoalIsNoKeysAndNoSearch()
    {
        var graph = Floor();
        var follower = new PathFollower(graph);
        var state = new BotPathState();

        var at = Stand(graph, 0, 4);
        Assert.Equal(PlayerKeys.None, follower.Step(state, At(at.X, at.Y, 0, true), 0, new NavQuery(Team.Red)));
        Assert.Equal(0, state.Replans);
        Assert.False(state.HasPath);
    }

    /// <summary>
    /// The route index advances only while the bot is on the ground.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The single most expensive bug in the reference's follower.</b>
    /// <c>NodeFromWorld</c> tolerates being a couple of cells off a surface, so an
    /// arc passing over its own landing resolves to that node about fifteen pixels
    /// before the feet reach it — and advancing there hands the follower the
    /// <em>next</em> edge while the bot is still in the air. Measured on
    /// ctf_truefort's two stacked jumps: the bot took off, was switched onto the
    /// return leg at the apex, landed back where it started, and did it again.
    /// <para>
    /// The fixture puts the bot squarely on node 1 — which it would advance to if
    /// asked — and shows the index does not move while airborne and does the tick
    /// it lands.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheRouteAdvancesOnlyOnTheGround()
    {
        var graph = Floor();
        var follower = new PathFollower(graph);
        var state = new BotPathState();

        var start = Stand(graph, 0, 4);
        var goal = Stand(graph, 2, 45);
        follower.SetGoal(state, goal.X, goal.Y);
        follower.Step(state, At(start.X, start.Y, 0, true), 0, new NavQuery(Team.Red));

        Assert.Equal(0, state.PathAt);

        var overNodeOne = Stand(graph, 1, 24);

        follower.Step(state, At(overNodeOne.X, overNodeOne.Y, 0, false), 1, new NavQuery(Team.Red));
        Assert.Equal(0, state.PathAt);

        follower.Step(state, At(overNodeOne.X, overNodeOne.Y, 0, true), 2, new NavQuery(Team.Red));
        Assert.Equal(1, state.PathAt);
    }

    /// <summary>
    /// Arrival needs the bot stopped, not merely close.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Being within tolerance of the goal is not the same as stopping
    /// there.</b> GG2 keeps a character's horizontal speed for about ten ticks
    /// after the keys come off, so declaring arrival on position alone and then
    /// pressing nothing coasts the bot thirty to forty pixels past the thing it
    /// was sent to — measured in the reference on all three legs of the gg_debug
    /// walk, the last of which slid into the left wall.
    /// <para>
    /// So inside the band the bot <em>brakes</em>: a press against its own
    /// motion, which is how a player stops. The speed floor of 1 is what keeps
    /// that from becoming a press back the other way.
    /// </para>
    /// </remarks>
    [Fact]
    public void ArrivalNeedsTheBotStoppedAndNotMerelyClose()
    {
        var graph = Floor();
        var follower = new PathFollower(graph);
        var state = new BotPathState();

        var goal = Stand(graph, 0, 4);
        follower.SetGoal(state, goal.X, goal.Y);

        // Inside the band and still running to the right: brake left, do not
        // arrive.
        var braking = follower.Step(state, At(goal.X, goal.Y, 5, true), 0, new NavQuery(Team.Red));

        Assert.Equal(PlayerKeys.Left, braking);
        Assert.False(state.Arrived);
        Assert.True(state.HasGoal);

        // Running left: brake right.
        Assert.Equal(
            PlayerKeys.Right,
            follower.Step(state, At(goal.X, goal.Y, -5, true), 1, new NavQuery(Team.Red)));

        // Stopped on the ground inside the band: arrived, and the goal is
        // cleared so nothing keeps steering to it.
        var arrived = follower.Step(state, At(goal.X, goal.Y, 0, true), 2, new NavQuery(Team.Red));

        Assert.Equal(PlayerKeys.None, arrived);
        Assert.True(state.Arrived);
        Assert.False(state.HasGoal);
        Assert.False(state.HasPath);
        Assert.Equal(2, state.ArrivedAt);
    }

    /// <summary>
    /// Stopped in the air inside the band is not an arrival.
    /// </summary>
    /// <remarks>
    /// A bot often reaches its last surface still falling onto it. The on-ground
    /// half of the test is what keeps "at the apex of a hop over the goal" from
    /// counting.
    /// </remarks>
    [Fact]
    public void StoppedInTheAirIsNotAnArrival()
    {
        var graph = Floor();
        var follower = new PathFollower(graph);
        var state = new BotPathState();

        var goal = Stand(graph, 0, 4);
        follower.SetGoal(state, goal.X, goal.Y);
        follower.Step(state, At(goal.X, goal.Y, 0, false), 0, new NavQuery(Team.Red));

        Assert.False(state.Arrived);
        Assert.True(state.HasGoal);
    }

    /// <summary>
    /// A stale route never declares an arrival.
    /// </summary>
    /// <remarks>
    /// ⚠️ A route kept only so the bot keeps moving until a plan for the new goal
    /// exists ends somewhere the bot was sent <em>before</em>. Reaching the end of
    /// it is not an arrival at anything, and declaring one would clear
    /// <see cref="BotPathState.HasGoal"/> and throw away the objective the bot was
    /// just given.
    /// </remarks>
    [Fact]
    public void AStaleRouteNeverArrives()
    {
        var graph = Floor();
        var follower = new PathFollower(graph);
        var state = new BotPathState();

        var goal = Stand(graph, 0, 4);
        follower.SetGoal(state, goal.X, goal.Y);
        follower.Step(state, At(goal.X, goal.Y, 0, true), 0, new NavQuery(Team.Red));
        Assert.True(state.Arrived);

        // Now the same position and route, but marked stale.
        var stale = new BotPathState { HasGoal = true, GoalX = goal.X, GoalY = goal.Y, PathStale = true };
        stale.Path.Add(0);
        stale.ReplanAt = int.MaxValue; // do not let the timer re-plan it away

        var keys = follower.Step(stale, At(goal.X, goal.Y, 0, true), 0, new NavQuery(Team.Red));

        Assert.False(stale.Arrived);
        Assert.True(stale.HasGoal);

        // It keeps walking rather than standing: the band is off entirely, so the
        // ordinary "head for the goal" press applies.
        Assert.NotEqual(PlayerKeys.None, keys);
    }

    /// <summary>
    /// Landing off the route re-plans at once, and the second consecutive miss
    /// takes the edge away.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Not on the first miss.</b> A lot of real edges are marginal — a
    /// three-column ledge reached by a slow steep arc — and those are flown
    /// stochastically. Banning one the first time it is missed cost the reference
    /// ~250 frames to complete a hop that takes about 30: fifteen off-route events
    /// produced fifteen blacklists, and the blacklist was the whole delay.
    /// <para>
    /// ⚠️ And it re-plans <em>immediately</em> rather than riding out
    /// <c>BOT_OFFPATH_TICKS</c>: standing on a surface the route does not mention
    /// is a finished move that went somewhere else, and waiting to admit it is how
    /// a bot walks somewhere it cannot get back from.
    /// </para>
    /// </remarks>
    [Fact]
    public void LandingOffTheRouteRePlansAndTheSecondMissBlacklists()
    {
        // ⚠️ Node 3 is a DEAD END, and that is what makes a *second* consecutive
        // miss reachable: with a way back the re-plan simply installs a route
        // that starts where the bot actually landed, so it is on its route again
        // and the strike counter never reaches two. Which is the right
        // behaviour — this fixture is about the case where it cannot.
        var graph = Build(
            [Node(10, 0, 9), Node(10, 20, 29), Node(10, 40, 49), Node(30, 0, 9)],
            [
                Edge(0, 1, 66), Edge(1, 2, 66),
                Edge(0, 3, 120),
            ]);

        var follower = new PathFollower(graph);
        var state = new BotPathState();

        follower.SetGoal(state, NavGraph.ColumnWorldX(45), graph.StandY(2));
        follower.Step(state, At(NavGraph.ColumnWorldX(4), graph.StandY(0)), 0, new NavQuery(Team.Red));

        Assert.Equal([0, 1, 2], state.Path);
        state.EdgeFrom = 0;
        state.EdgeTo = 1;

        // Land on node 3, which the route does not mention.
        var off = (X: NavGraph.ColumnWorldX(4), Y: graph.StandY(3));

        follower.Step(state, At(off.X, off.Y, 0, true), 1, new NavQuery(Team.Red));

        Assert.Equal(1, state.OffRouteFires);
        Assert.Equal(0, state.BlacklistFires);
        Assert.Equal(2, state.Replans); // the re-plan is immediate

        // The search from a dead end fails, so the old route is kept and the bot
        // is still standing somewhere it does not mention.
        Assert.Equal([0, 1, 2], state.Path);

        // Set the same edge up again and miss it a second time.
        state.EdgeFrom = 0;
        state.EdgeTo = 1;
        follower.Step(state, At(off.X, off.Y, 0, true), 2, new NavQuery(Team.Red));

        Assert.Equal(2, state.OffRouteFires);
        Assert.Equal(1, state.BlacklistFires);
        Assert.Contains(NavGraph.EdgeKey(0, 1), state.Blacklist);
    }

    /// <summary>
    /// Pressing something and going nowhere gives up on the edge.
    /// </summary>
    /// <remarks>
    /// Surfacer's stuck detector, widened to <c>BOT_STUCK_TICKS</c>. ⚠️ Gated on
    /// actually pressing something, or a bot standing on its target waiting for a
    /// jump window would report itself stuck — and the moved test is half a pixel
    /// rather than zero, because <c>good_move_contact_solid</c> works at eighth-pixel
    /// precision and a bot grinding against a wall jitters without going anywhere.
    /// </remarks>
    [Fact]
    public void PressingAndGoingNowhereBlacklistsTheEdge()
    {
        var graph = Floor();
        var follower = new PathFollower(graph);
        var state = new BotPathState();

        var at = Stand(graph, 0, 4);
        follower.SetGoal(state, Stand(graph, 2, 45).X, Stand(graph, 2, 45).Y);

        for (var tick = 0; tick < BotConstants.StuckTicks + 1; tick++)
            follower.Step(state, At(at.X, at.Y, 0, true), tick, new NavQuery(Team.Red));

        Assert.Equal(1, state.StuckFires);
        Assert.Equal(1, state.BlacklistFires);
        Assert.Contains(NavGraph.EdgeKey(0, 1), state.Blacklist);

        // ⚠️ A quarter-pixel wobble is still "went nowhere": the threshold is half
        // a pixel and not zero.
        var wobbling = new BotPathState();
        follower.SetGoal(wobbling, Stand(graph, 2, 45).X, Stand(graph, 2, 45).Y);

        for (var tick = 0; tick < BotConstants.StuckTicks + 1; tick++)
        {
            var jitter = (tick % 2) * 0.25;
            follower.Step(wobbling, At(at.X + jitter, at.Y, 0, true), tick, new NavQuery(Team.Red));
        }

        Assert.Equal(1, wobbling.StuckFires);
    }

    /// <summary>
    /// A blacklisted edge is refused by the next plan, and comes back when it
    /// expires.
    /// </summary>
    /// <remarks>
    /// The two halves that make the blacklist a delay rather than a permanent
    /// verdict. ⚠️ Expiry is swept at plan time and nowhere else — that is the
    /// only moment a stale entry could affect anything, so it needs no timer of
    /// its own.
    /// </remarks>
    [Fact]
    public void ABlacklistedEdgeIsRefusedUntilItExpires()
    {
        // Two routes to node 2: the direct pair, and a detour through 3.
        var graph = Build(
            [Node(10, 0, 9), Node(10, 20, 29), Node(10, 40, 49), Node(40, 20, 29)],
            [
                Edge(0, 1, 66), Edge(1, 2, 66),
                Edge(0, 3, 200), Edge(3, 2, 200),
            ]);

        var follower = new PathFollower(graph);
        var state = new BotPathState { HasGoal = true };

        var at = Stand(graph, 0, 4);
        follower.SetGoal(state, Stand(graph, 2, 45).X, Stand(graph, 2, 45).Y);

        follower.Plan(state, at.X, at.Y, 0, new NavQuery(Team.Red));
        Assert.Equal([0, 1, 2], state.Path);

        state.BlacklistEdge(0, 1, 0);
        state.ReplanAt = 0;
        follower.Plan(state, at.X, at.Y, 1, new NavQuery(Team.Red));
        Assert.Equal([0, 3, 2], state.Path);

        // The ban runs from when it was imposed, so it is gone a moment after.
        follower.Plan(state, at.X, at.Y, BotConstants.BlacklistTicks + 1, new NavQuery(Team.Red));
        Assert.Equal([0, 1, 2], state.Path);
        Assert.Empty(state.Blacklist);
    }

    /// <summary>
    /// A ban already imposed is not re-armed by another failure.
    /// </summary>
    /// <remarks>
    /// ⚠️ The reference exits on the existence test, so a ban runs from when it
    /// was first imposed. Without that a bot that keeps failing the same edge
    /// extends it indefinitely, and the blacklist stops being a delay.
    /// </remarks>
    [Fact]
    public void ABanIsNotReArmedByAnotherFailure()
    {
        var state = new BotPathState();

        state.BlacklistEdge(0, 1, 0);
        state.BlacklistEdge(0, 1, 100);

        Assert.Equal(1, state.BlacklistFires);
        Assert.Single(state.BlacklistOrder);
        Assert.Equal(BotConstants.BlacklistTicks, state.BlacklistOrder[0].Expiry);

        // Negative nodes are ignored, which is what makes it safe to call with
        // EdgeFrom when no edge was running.
        state.BlacklistEdge(-1, 4, 0);
        state.BlacklistEdge(4, -1, 0);
        Assert.Equal(1, state.BlacklistFires);
    }

    /// <summary>
    /// A failed search keeps the old route and parks the next attempt.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The search runs before the old route is given up.</b> This was the
    /// reference's "bots momentarily stop moving" report, and the stall was
    /// structural: with free-then-search, a bot between a goal changing and a
    /// route existing had nothing to follow at all. Keeping the old route means a
    /// bot whose new goal cannot be planned for keeps walking the last route that
    /// worked, which is what a person does.
    /// <para>
    /// ⚠️ And the failure parks the next search for a re-plan window, or every
    /// event-driven caller would fire again on the very next tick — a full A* per
    /// bot per frame for a goal that cannot be reached from a pixel away either.
    /// </para>
    /// </remarks>
    [Fact]
    public void AFailedSearchKeepsTheOldRouteAndParksTheNextOne()
    {
        // Node 3 is reachable from nowhere.
        var graph = Build(
            [Node(10, 0, 9), Node(10, 20, 29), Node(10, 40, 49), Node(80, 0, 9)],
            [Edge(0, 1, 66), Edge(1, 2, 66)]);

        var follower = new PathFollower(graph);
        var state = new BotPathState();

        var at = Stand(graph, 0, 4);
        follower.SetGoal(state, Stand(graph, 2, 45).X, Stand(graph, 2, 45).Y);
        follower.Plan(state, at.X, at.Y, 0, new NavQuery(Team.Red));

        var installed = state.Path.ToArray();
        Assert.NotEmpty(installed);

        // Send it somewhere unreachable.
        Assert.True(follower.SetGoal(state, NavGraph.ColumnWorldX(4), graph.StandY(3)));
        Assert.False(follower.Plan(state, at.X, at.Y, 1, new NavQuery(Team.Red)));

        Assert.Equal(installed, state.Path);
        Assert.True(state.PathStale);
        Assert.Equal(1 + BotConstants.ReplanTicks, state.PlanFailUntil);

        // The next attempt inside the window does not even search.
        var before = state.Replans;
        Assert.False(follower.Plan(state, at.X, at.Y, 2, new NavQuery(Team.Red)));
        Assert.Equal(before, state.Replans);
    }

    /// <summary>
    /// Two re-plans in the same place take the edge away.
    /// </summary>
    /// <remarks>
    /// ⚠️ The anti-thrash check catches the case the stuck detector cannot: a bot
    /// that is technically moving — shuffling back and forth across a ledge — and
    /// getting nowhere. It lives with the planner rather than the detector
    /// because it is about re-planning, not about failing outright.
    /// </remarks>
    [Fact]
    public void TwoRePlansInTheSamePlaceBlacklistTheEdge()
    {
        var graph = Floor();
        var follower = new PathFollower(graph);
        var state = new BotPathState();

        var at = Stand(graph, 0, 4);
        follower.SetGoal(state, Stand(graph, 2, 45).X, Stand(graph, 2, 45).Y);

        // ⚠️ The edge in progress is re-established before every plan, because
        // that is what the follower does: FollowEdge writes it each tick and a
        // successful plan clears it again. A test that set it once would find
        // nothing to blacklist by the third call and would pass with the whole
        // thrash check deleted — which is exactly what the first draft did.
        // `botPathPlan.gml:48` reads it at the top of the plan for the same
        // reason.
        follower.Plan(state, at.X, at.Y, 0, new NavQuery(Team.Red));
        state.EdgeFrom = 0;
        state.EdgeTo = 1;

        // Two more from the same spot: the first counts, the second fires.
        follower.Plan(state, at.X, at.Y, 1, new NavQuery(Team.Red));
        Assert.Equal(0, state.BlacklistFires);

        state.EdgeFrom = 0;
        state.EdgeTo = 1;
        follower.Plan(state, at.X, at.Y, 2, new NavQuery(Team.Red));
        Assert.Equal(1, state.BlacklistFires);

        // ⚠️ And moving further than BOT_THRASH_DIST between re-plans resets it:
        // a bot covering ground is not thrashing.
        var moved = new BotPathState();
        follower.SetGoal(moved, Stand(graph, 2, 45).X, Stand(graph, 2, 45).Y);

        for (var i = 0; i < 5; i++)
        {
            moved.EdgeFrom = 0;
            moved.EdgeTo = 1;
            follower.Plan(moved, at.X + (i * BotConstants.ThrashDist * 2), at.Y, i, new NavQuery(Team.Red));
        }

        Assert.Equal(0, moved.BlacklistFires);
    }

    /// <summary>
    /// A drop-through is taken from the middle of the run, with DOWN held.
    /// </summary>
    /// <remarks>
    /// The generator takes the drop from the middle of the surface, and ⚠️ DOWN
    /// disables the platform entirely — both the standing test and the push-out —
    /// so the key is what makes the edge work rather than merely an intention.
    /// </remarks>
    [Fact]
    public void ADropThroughGoesDownThroughTheMiddle()
    {
        var graph = Build(
            [Node(10, 0, 20), Node(30, 0, 20)],
            [Edge(0, 1, 120, NavEdgeType.DropThrough)]);

        var follower = new PathFollower(graph);
        var state = new BotPathState();

        follower.SetGoal(state, NavGraph.ColumnWorldX(10), graph.StandY(1));

        // At the middle: DOWN.
        var middle = NavGraph.ColumnWorldX(10);
        var here = follower.Step(state, At(middle, graph.StandY(0), 0, true), 0, new NavQuery(Team.Red));
        Assert.True(here.HasFlag(PlayerKeys.Down), $"at the middle the bot pressed {here}");

        // At the end: walk to the middle first, no DOWN.
        var end = NavGraph.ColumnWorldX(0);
        var away = follower.Step(state, At(end, graph.StandY(0), 0, true), 1, new NavQuery(Team.Red));
        Assert.False(away.HasFlag(PlayerKeys.Down), $"at the end the bot pressed {away}");
        Assert.True(away.HasFlag(PlayerKeys.Right), "it did not walk toward the middle");
    }

    /// <summary>
    /// A fall walks off the end the edge names, and then holds that column.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Which end cannot be re-derived, which is why the edge carries it.</b>
    /// "The column just past the run is inside the landing node" is true of BOTH
    /// ends wherever the landing surface reaches under the whole run — what a
    /// platform standing on a wider ledge looks like — and guessing picked the
    /// right-hand end whenever it was ambiguous. In the reference that walked a
    /// bot on ctf_avanti into the solid block against its right end and wedged it
    /// there, when the fall it wanted was off the left.
    /// <para>
    /// ⚠️ And in the air the plan is a STRAIGHT VERTICAL DROP from that column, so
    /// the bot's job is to hold it. Without that a fall is the one edge kind
    /// flown with no plan at all: the bot walks off at whatever speed it had, keeps
    /// it, and sails past any landing narrower than the drift.
    /// </para>
    /// </remarks>
    [Fact]
    public void AFallLeavesFromTheEndTheEdgeNamesAndHoldsThatColumn()
    {
        // The landing reaches under the whole run, so both ends are "just past
        // the run and inside the landing" and the guess is ambiguous.
        var graph = Build(
            [Node(10, 10, 20), Node(40, 0, 30)],
            [Edge(0, 1, 180, NavEdgeType.Fall, takeoff: 9)]); // off the LEFT

        var follower = new PathFollower(graph);
        var state = new BotPathState();

        follower.SetGoal(state, NavGraph.ColumnWorldX(15), graph.StandY(1));

        // On the ground in the middle of the run: walk left, toward the takeoff.
        var middle = NavGraph.ColumnWorldX(15);
        Assert.Equal(
            PlayerKeys.Left,
            follower.Step(state, At(middle, graph.StandY(0), 0, true), 0, new NavQuery(Team.Red)));

        // In the air and drifting right of the takeoff column: press back left to
        // hold it, and report the bot as flying a validated edge.
        var takeoffX = NavGraph.ColumnWorldX(9);
        var drifted = follower.Step(state, At(takeoffX + 10, graph.StandY(0) + 20, 4, false), 1, new NavQuery(Team.Red));

        Assert.Equal(PlayerKeys.Left, drifted);
        Assert.True(state.FlyingEdge, "a bot part-way through a validated fall must say so");

        // Drifting left of it: press right.
        Assert.Equal(
            PlayerKeys.Right,
            follower.Step(state, At(takeoffX - 10, graph.StandY(0) + 20, -4, false), 2, new NavQuery(Team.Red)));
    }

    /// <summary>
    /// The rocket law is not the ordinary law, in both directions at once.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>They differ in BOTH terms of the motion law, not one.</b>
    /// Character's Begin Step sets the control and friction factors from
    /// <c>moveStatus</c>, and 1 — "rocketing/mining myself" — gives 0.65 and
    /// <b>1</b> where the default gives 0.85 and 1.15. A friction of 1 means
    /// horizontal speed does not bleed at all for the whole flight.
    /// <para>
    /// So using <see cref="JumpModel.Reach"/> for a rocket arc is wrong in both
    /// directions at once, which is worse than wrong in one — and the two fixtures
    /// below are one of each, measured rather than assumed:
    /// </para>
    /// <para>
    /// <b>A cold start on a fast arc</b> (needVx 6, v0 0, 30 ticks): the rocket law
    /// reaches <b>136.9px</b> against the ordinary law's <b>119.5</b>. Nothing takes
    /// the speed back off between presses, so it climbs linearly instead of
    /// approaching a ceiling geometrically. Using the ordinary model here REFUSES
    /// arcs the bot flies easily — which is the whole reason the takeoff gate stopped
    /// demanding the arc's full speed in the first place.
    /// </para>
    /// <para>
    /// <b>A hot start on a slow arc</b> (needVx 2, v0 8): the rocket law overshoots
    /// its own 60px schedule to <b>85.6px</b> where the ordinary law lands at
    /// <b>59</b>. A rocket jump carries its takeoff momentum the entire flight; the
    /// only way down is to press against the motion. Using the ordinary model here
    /// ACCEPTS arcs the bot sails straight past.
    /// </para>
    /// <para>
    /// ⚠️ This is the control that makes the two models' existence
    /// load-bearing. Point the rocket branch of the takeoff gate at
    /// <see cref="JumpModel"/> and every other follower test still passes — the arcs
    /// in them are flyable under either law.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheRocketLawIsNotTheOrdinaryLaw()
    {
        var soldier = At(0, 0);

        // Accelerating: a cold start on an arc asking for most of the ceiling.
        var rocketCold = RocketModel.Reach(in soldier, 6, 30, 0);
        var jumpCold = JumpModel.Reach(in soldier, 6, 30, 0);

        Assert.True(
            rocketCold > jumpCold + 10,
            $"a cold rocket takeoff did not out-travel a cold ordinary one: "
            + $"{rocketCold:0.#} against {jumpCold:0.#}");

        // Coasting hot: a rocket jump cannot shed what it left with.
        var rocketHot = RocketModel.Reach(in soldier, 2, 30, 8);
        var jumpHot = JumpModel.Reach(in soldier, 2, 30, 8);

        Assert.True(
            rocketHot > jumpHot + 20,
            $"a hot rocket takeoff shed speed like an ordinary one: "
            + $"{rocketHot:0.#} against {jumpHot:0.#}");

        // ⚠️ and it really does sail past its own schedule, which is the
        // half a caller has to care about — 2 px/tick over 30 ticks is 60.
        Assert.True(rocketHot > 2 * 30, $"the hot arc landed on schedule at {rocketHot:0.#}");
        Assert.True(jumpHot <= 2 * 30, $"the ordinary law also overshot, at {jumpHot:0.#}");
    }

    /// <summary>
    /// The takeoff gate asks the ROCKET model about a rocket arc.
    /// </summary>
    /// <remarks>
    /// ⚠️ <c>TheRocketLawIsNotTheOrdinaryLaw</c> gates that the two models
    /// differ; this gates that the takeoff gate picks the right one, which is a
    /// separate claim and was briefly ungated. Point the gate at
    /// <see cref="JumpModel"/> and every other follower test still passes, because
    /// their arcs are flyable under either law.
    /// <para>
    /// The fixture is an arc flyable under one law and not the other, taken from the
    /// measured table: needVx 6 over 30 ticks from a standstill reaches
    /// <b>136.9px</b> under the rocket law and <b>119.5px</b> under the ordinary
    /// one. The gap is <b>132px</b>, so with the tolerance the rocket law clears it
    /// and the ordinary law does not — and the observable difference is whether the
    /// bot launches from where it stands or starts a run-up.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheTakeoffGateAsksTheRocketModelAboutARocketArc()
    {
        // Landing 22 anchor columns past the takeoff: a 132px gap.
        var graph = Build(
            [Node(30, 0, 9), Node(10, 30, 39)],
            [Edge(0, 1, 200, NavEdgeType.RocketJump, takeoff: 8, bucket: 6, ticks: 30)]);

        var follower = new PathFollower(graph);
        var state = new BotPathState();

        follower.SetGoal(state, NavGraph.ColumnWorldX(35), graph.StandY(1));

        var at = Stand(graph, 0, 8);
        var able = new NavQuery(Team.Red, CanRocketJump: true);
        var loaded = Loaded();

        // One tick, standing still on the takeoff column. Under the rocket law this
        // is a launch; under the ordinary one it is a refusal and a run-up.
        var keys = follower.Step(state, At(at.X, at.Y), in loaded, 0, able);

        Assert.True(
            state.RocketFire,
            "the gate refused an arc the rocket law clears — it is asking the ordinary model");

        Assert.True((keys & PlayerKeys.Jump) != 0);
        Assert.Equal(-1, state.RunupAt);

        // ⚠️ The control — the SAME arc as an ordinary jump edge is refused
        // and does start a run-up. Without it this passes on a gate that accepts
        // everything.
        var ordinary = Build(
            [Node(30, 0, 9), Node(10, 30, 39)],
            [Edge(0, 1, 200, NavEdgeType.Jump, takeoff: 8, bucket: 6, ticks: 30)]);

        var second = new PathFollower(ordinary);
        var plain = new BotPathState();

        second.SetGoal(plain, NavGraph.ColumnWorldX(35), ordinary.StandY(1));

        var plainKeys = second.Step(plain, At(at.X, at.Y), 0, new NavQuery(Team.Red));

        Assert.False(plain.RocketFire);
        Assert.Equal(PlayerKeys.None, plainKeys & PlayerKeys.Jump);
        Assert.True(plain.RunupAt >= 0, "the ordinary law accepted the same arc from a standstill");
    }

    /// <summary>
    /// A rocket jump needs hp above the damage it does, strictly.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Strictly greater, because the damage lands before the bot
    /// does</b> — a soldier on exactly <c>NAV_RJ_DAMAGE</c> arrives dead. And the
    /// health test is re-asked at the gate because the search only ever answered it
    /// at plan time: a route planned at 160hp is still installed at 35.
    /// </remarks>
    [Fact]
    public void ARocketJumpNeedsHealthAboveItsOwnDamageStrictly()
    {
        var loaded = Loaded();

        var healthy = At(0, 0);
        healthy.Hp = NavConstants.RjDamage + 1;
        Assert.True(RocketModel.Ready(in healthy, in loaded));

        var exactly = At(0, 0);
        exactly.Hp = NavConstants.RjDamage;
        Assert.False(RocketModel.Ready(in exactly, in loaded), "a soldier on exactly 30hp would arrive dead");

        // The other three clauses, each on its own.
        Assert.False(RocketModel.Ready(in healthy, Loaded(ammo: 0)), "an empty launcher was ready");

        var cooling = healthy;
        Assert.False(
            RocketModel.Ready(in cooling, new WeaponState { ReadyToShoot = false, AmmoCount = 4 }),
            "a launcher on cooldown was ready");

        // ⚠️ the class, not the weapon slot — every class has a WeaponState,
        // and only the soldier's is a Rocketlauncher.
        var scout = At(0, 0, playerClass: PlayerClass.Scout);
        scout.Hp = 200;
        Assert.False(RocketModel.Ready(in scout, in loaded), "a scout rocket-jumped");
    }

    /// <summary>
    /// A loaded soldier takes off on a rocket-jump edge and asks for the rocket.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The follower cannot fire.</b> It owns LEFT / RIGHT / JUMP / DOWN;
    /// the aim and the trigger come from the combat half. So a rocket jump is a JUMP
    /// press plus a <em>request</em> — <see cref="BotPathState.RocketFire"/> — that
    /// <c>ScriptedBot</c> turns into an aim of 270 and an ATTACK bit after combat
    /// has run, which is the only place that can see both halves.
    /// </remarks>
    [Fact]
    public void ALoadedSoldierTakesOffOnARocketJumpAndAsksForTheRocket()
    {
        var graph = Build(
            [Node(30, 0, 9), Node(10, 20, 29)],
            [Edge(0, 1, 200, NavEdgeType.RocketJump, takeoff: 8, bucket: 5, ticks: 20)]);

        var follower = new PathFollower(graph);
        var state = new BotPathState();

        follower.SetGoal(state, NavGraph.ColumnWorldX(25), graph.StandY(1));

        var at = Stand(graph, 0, 8);
        var able = new NavQuery(Team.Red, CanRocketJump: true);
        var loaded = Loaded();

        var keys = PlayerKeys.None;

        for (var tick = 0; tick < 40 && !state.RocketFire; tick++)
            keys = follower.Step(state, At(at.X, at.Y), in loaded, tick, able);

        Assert.True(state.RocketFire, "a loaded soldier never asked for the rocket");
        Assert.True((keys & PlayerKeys.Jump) != 0, "it asked for the rocket without jumping");
        Assert.Equal(1, state.RocketJumps);
        Assert.Equal(0, state.UnsupportedEdgeTicks);

        // ⚠️ Cleared at the top of the next Step, so a tick that does not
        // reach the takeoff gate cannot leave the request standing. Without that a
        // bot fires a rocket at its own feet every tick of the flight.
        follower.Step(state, At(at.X, at.Y - 40, 3, onGround: false), in loaded, 41, able);
        Assert.False(state.RocketFire, "the rocket request survived the takeoff tick");
    }

    /// <summary>
    /// ✅ <b>A demoman on a sticky-jump edge places a mine, waits, and then jumps
    /// and detonates on ONE tick.</b>
    /// </summary>
    /// <remarks>
    /// <b>The only edge kind that does not leave on the tick its gate opens.</b>
    /// A rocket jump is one press — <c>Character.events/Begin Step.xml</c> runs
    /// the attack branch before the jump branch and the rocket goes off in the
    /// same step. A mine has to be placed and has to settle, so the gate opening
    /// starts a <see cref="NavConstants.SjSetupTicks"/> countdown and the launch
    /// is five ticks later.
    /// <para>
    /// ⚠️ <b>The jump and the detonation are the same tick, and that ordering is
    /// the manoeuvre.</b> Begin Step's jump branch (line 108) runs before its
    /// right-click branch (line 137), so <c>vspeed = -jumpStrength</c> is already
    /// in place when the blast's <c>motion_add</c> lands on top of it —
    /// <c>MineJumpTests</c> measures the result at -14.460 px/tick. Detonating a
    /// tick later throws the jump away.
    /// </para>
    /// </remarks>
    [Fact]
    public void ADemomanPlacesAMineWaitsAndThenJumpsAndDetonatesTogether()
    {
        var graph = Build(
            [Node(30, 0, 9), Node(10, 20, 29)],
            [Edge(0, 1, 200, NavEdgeType.StickyJump, takeoff: 8, bucket: 5, ticks: 20)]);

        var follower = new PathFollower(graph);
        var state = new BotPathState();

        follower.SetGoal(state, NavGraph.ColumnWorldX(25), graph.StandY(1));

        var at = Stand(graph, 0, 8);
        var able = new NavQuery(Team.Red, CanStickyJump: true);
        var loaded = Loaded();

        // Phase one: the mine goes down and the bot does NOT jump.
        var keys = follower.Step(
            state, At(at.X, at.Y, playerClass: PlayerClass.Demoman), in loaded, 0, able);

        Assert.True(state.MineFire, "the demoman never placed a mine");
        Assert.False(state.MineDetonate, "it detonated on the placing tick");
        Assert.True((keys & PlayerKeys.Jump) == 0, "it jumped before the mine had settled");
        Assert.Equal(NavConstants.SjSetupTicks, state.StickySetup);

        // The setup ticks: still no jump, and no second mine.
        for (var tick = 1; tick <= NavConstants.SjSetupTicks; tick++)
        {
            keys = follower.Step(
                state, At(at.X, at.Y, playerClass: PlayerClass.Demoman), in loaded, tick, able);

            if (state.MineDetonate)
                break;

            Assert.False(state.MineFire, $"tick {tick} placed a second mine");
            Assert.True((keys & PlayerKeys.Jump) == 0, $"tick {tick} jumped early");
        }

        // Phase two: jump and detonate together.
        Assert.True(state.MineDetonate, "the demoman never detonated");
        Assert.True((keys & PlayerKeys.Jump) != 0, "it detonated without jumping");
        Assert.Equal(1, state.StickyJumps);
        Assert.Equal(0, state.UnsupportedEdgeTicks);
        Assert.Equal(-1, state.StickySetup);
    }

    /// <summary>
    /// ⚠️ <b>Placing the mine puts the launcher on cooldown, and the countdown
    /// must survive that.</b>
    /// </summary>
    /// <remarks>
    /// <b>The bug this pins was invisible to every synthetic test and cost every
    /// real flight.</b> <see cref="StickyModel.Ready"/> requires
    /// <c>ReadyToShoot</c>, and firing the mine clears it for the refire — so a
    /// gate that demands readiness on every tick closes on the tick after the
    /// placement, the abandon branch throws the setup away, and the bot never
    /// jumps. Measured across 48 real edges on four maps from
    /// <c>gg2-nav-gen</c>'s sticky cache: <b>zero sticky jumps taken</b>.
    /// <para>
    /// ⚠️ <b>The synthetic tests could not see it because their fixture weapon is
    /// permanently ready</b> — <c>Loaded()</c> sets <c>ReadyToShoot</c> once and
    /// nothing ever spends it. This test hands the follower a weapon that goes
    /// cold the moment the mine is placed, which is what the simulation does.
    /// </para>
    /// <para>
    /// Readiness is required to <em>start</em> the manoeuvre and irrelevant once
    /// the mine is on the ground, which is the fix.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheCountdownSurvivesTheLauncherGoingColdAfterThePlacement()
    {
        var graph = Build(
            [Node(30, 0, 9), Node(10, 20, 29)],
            [Edge(0, 1, 200, NavEdgeType.StickyJump, takeoff: 8, bucket: 5, ticks: 20)]);

        var follower = new PathFollower(graph);
        var state = new BotPathState();

        follower.SetGoal(state, NavGraph.ColumnWorldX(25), graph.StandY(1));

        var at = Stand(graph, 0, 8);
        var able = new NavQuery(Team.Red, CanStickyJump: true);

        // Ready until the mine goes down, cold for ever after — the shape a
        // refire has, and the shape Loaded() does not.
        var weapon = Loaded();
        var keys = PlayerKeys.None;

        for (var tick = 0; tick < 20 && !state.MineDetonate; tick++)
        {
            keys = follower.Step(
                state, At(at.X, at.Y, playerClass: PlayerClass.Demoman), in weapon, tick, able);

            if (state.MineFire)
                weapon = new WeaponState { ReadyToShoot = false, AmmoCount = 3 };
        }

        Assert.True(state.MineDetonate, "the countdown died with the launcher's cooldown");
        Assert.True((keys & PlayerKeys.Jump) != 0, "it detonated without jumping");
        Assert.Equal(1, state.StickyJumps);
    }

    /// <summary>
    /// ⚠️ <b>A cold launcher makes a sticky jump WAIT on the takeoff column</b>,
    /// exactly as it does a rocket jump.
    /// </summary>
    /// <remarks>
    /// The waiting branch was <c>isRocket</c>-only, which is a policy the sticky
    /// jump needs just as badly: a demoman arriving at a takeoff just after
    /// firing — or freshly spawned, where the refire starts cold — falls through
    /// to the run-up instead, walks off the column and back, and burns the
    /// takeoff patience budget until the edge is blacklisted. That is a refusal
    /// with no bad arc anywhere in it.
    /// <para>
    /// ⚠️ <b>Fixing it did not change any real flight</b>, which is recorded
    /// rather than hidden: it is a correct policy and it was not the thing
    /// refusing `gg2-nav-gen`'s edges. See docs/TRAPS-BOTS.md.
    /// </para>
    /// </remarks>
    [Fact]
    public void AColdLauncherMakesAStickyJumpWaitRatherThanRunUp()
    {
        var graph = Build(
            [Node(30, 0, 9), Node(10, 20, 29)],
            [Edge(0, 1, 200, NavEdgeType.StickyJump, takeoff: 8, bucket: 5, ticks: 20)]);

        var follower = new PathFollower(graph);
        var state = new BotPathState();

        follower.SetGoal(state, NavGraph.ColumnWorldX(25), graph.StandY(1));

        var at = Stand(graph, 0, 8);
        var able = new NavQuery(Team.Red, CanStickyJump: true);
        var cold = new WeaponState { ReadyToShoot = false, AmmoCount = 3 };

        for (var tick = 0; tick < 10; tick++)
        {
            var keys = follower.Step(
                state, At(at.X, at.Y, playerClass: PlayerClass.Demoman), in cold, tick, able);

            Assert.False(state.MineFire, "a cold launcher placed a mine");
            Assert.True((keys & PlayerKeys.Jump) == 0, "it jumped with a cold launcher");

            // The point of the branch: it stands still rather than running up.
            Assert.True(
                (keys & (PlayerKeys.Left | PlayerKeys.Right)) == 0,
                $"tick {tick} ran up instead of waiting for the launcher");
        }
    }

    /// <summary>
    /// ⚠️ <b>A sticky-jump edge is refused to anybody who cannot fly one</b>, and
    /// the follower counts the ticks rather than steering it.
    /// </summary>
    /// <remarks>
    /// The reference's A* <em>cannot</em> make this refusal —
    /// <c>navFindPath</c> takes <c>canDouble</c> and <c>canRocket</c> and has no
    /// <c>canSticky</c> — so a cache carrying type-7 edges routes every class
    /// over them in the stock game. This port filters them in
    /// <see cref="NavSearch"/>, which is the whole argument for the generator
    /// emitting them only into a cache the game never reads.
    /// </remarks>
    [Fact]
    public void AScoutOnAStickyEdgeNeitherMinesNorJumps()
    {
        var graph = Build(
            [Node(30, 0, 9), Node(10, 20, 29)],
            [Edge(0, 1, 200, NavEdgeType.StickyJump, takeoff: 8, bucket: 5, ticks: 20)]);

        var follower = new PathFollower(graph);
        var state = new BotPathState();

        follower.SetGoal(state, NavGraph.ColumnWorldX(25), graph.StandY(1));

        var at = Stand(graph, 0, 8);
        var able = new NavQuery(Team.Red, CanStickyJump: true);
        var loaded = Loaded();

        for (var tick = 0; tick < 20; tick++)
        {
            var keys = follower.Step(
                state, At(at.X, at.Y, playerClass: PlayerClass.Scout), in loaded, tick, able);

            Assert.False(state.MineFire, "a scout placed a mine");
            Assert.False(state.MineDetonate, "a scout detonated something");
            Assert.True((keys & PlayerKeys.Jump) == 0, "a scout took off on a sticky edge");
        }
    }

    /// <summary>
    /// With no rocket the bot waits on the takeoff column, then gives the edge up.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Jumping without the rocket is the worst available outcome</b>:
    /// the arc rises 276px where an ordinary jump rises 57, so the bot would leave
    /// the ground, track a trajectory it cannot be on, and land somewhere the route
    /// does not mention — costing an off-route fire and, on the second, a blacklist
    /// on a perfectly good edge.
    /// <para>
    /// ⚠️ <b>And it WAITS rather than running up.</b> The launcher's refire is
    /// 30 ticks against <c>BOT_TAKEOFF_PATIENCE</c>'s 60, so the overwhelmingly
    /// common reason to be here is a rocket a second away — and walking away from a
    /// jump the bot is about to be able to make and back again is the shuffle that
    /// budget exists to end, not a thing to spend it on.
    /// </para>
    /// <para>
    /// ⚠️ <b>The escape hatch takes the EDGE away rather than shoving the bot
    /// off it</b>, which is what it does for every other arc. Two seconds at the
    /// gate with no rocket is an honest "untraversable right now"; the search
    /// re-plans without it and the ban expires long after the launcher has reloaded.
    /// </para>
    /// </remarks>
    [Fact]
    public void WithNoRocketTheBotWaitsAndThenGivesTheEdgeUp()
    {
        var graph = Build(
            [Node(30, 0, 9), Node(10, 20, 29)],
            [Edge(0, 1, 200, NavEdgeType.RocketJump, takeoff: 8, bucket: 5, ticks: 20)]);

        var follower = new PathFollower(graph);

        // ⚠️ The stagger is not decoration. A re-plan clears the edge in progress
        // and the takeoff budget with it, so a bot's re-plan interval is a hard
        // ceiling on that budget — at the default stagger of 0 the interval is
        // BOT_REPLAN_TICKS (45), BOT_TAKEOFF_PATIENCE is 60, and the escape hatch is
        // unreachable. Item 4b-ii found that by a test failing; it bites here too.
        //
        // ⚠️ Which means a bot with an EMPTY launcher waits at the gate rather than
        // giving the edge up, at every stagger below 15 — and that is very nearly
        // always the right answer, because the launcher's refire is 30 ticks and its
        // ammo comes back. The hatch is for the case where it does not.
        var state = new BotPathState { ReplanStagger = BotConstants.ReplanTicks - 1 };

        follower.SetGoal(state, NavGraph.ColumnWorldX(25), graph.StandY(1));

        var at = Stand(graph, 0, 8);
        var able = new NavQuery(Team.Red, CanRocketJump: true);
        var empty = Loaded(ammo: 0);

        // Well inside the patience budget: it waits, and it does not jump.
        for (var tick = 0; tick < BotConstants.TakeoffPatience - 2; tick++)
        {
            var keys = follower.Step(state, At(at.X, at.Y), in empty, tick, able);

            Assert.Equal(PlayerKeys.None, keys & PlayerKeys.Jump);
            Assert.False(state.RocketFire);
        }

        Assert.Empty(state.Blacklist);

        // Past it: the edge is given up.
        for (var tick = BotConstants.TakeoffPatience - 2; tick < BotConstants.TakeoffPatience + 4; tick++)
            follower.Step(state, At(at.X, at.Y), in empty, tick, able);

        Assert.Contains(NavGraph.EdgeKey(0, 1), state.Blacklist);

        // ⚠️ The control — the same bot with a loaded launcher jumps instead
        // of waiting. Without it this passes on a follower that refuses every rocket
        // edge, which is where the port was before this landed.
        var loadedState = new BotPathState();
        follower.SetGoal(loadedState, NavGraph.ColumnWorldX(25), graph.StandY(1));

        var loaded = Loaded();
        var jumped = false;

        for (var tick = 0; tick < 40 && !jumped; tick++)
            jumped = (follower.Step(loadedState, At(at.X, at.Y), in loaded, tick, able) & PlayerKeys.Jump) != 0;

        Assert.True(jumped);
        Assert.Empty(loadedState.Blacklist);
    }

    /// <summary>
    /// A rocket that did not go off is not the arc that was planned.
    /// </summary>
    /// <remarks>
    /// ⚠️ <c>moveStatus</c> 1 is "rocketing/mining myself" and is set by the
    /// blast itself, so a rocket-jump edge in the air <em>without</em> it is an
    /// ORDINARY jump — 57px of rise against the arc's 276 — and steering it toward a
    /// trajectory it cannot be on only carries the bot further from where an
    /// ordinary jump was always going to put it. The follower stops steering and
    /// lets the off-route machinery adjudicate the landing, which already gives the
    /// edge one retry before banning it — right, because the common causes are
    /// transient (an enemy walked into the rocket) and the uncommon one is not.
    /// <para>
    /// ⚠️ <c>AirTicks &gt; 1</c> rather than <c>&gt;= 1</c>: the blast lands
    /// in the same tick's simulation as the takeoff press, so tick one is always
    /// <c>moveStatus</c> 0 and a stricter test would abandon every rocket jump on
    /// its first airborne tick.
    /// </para>
    /// </remarks>
    [Fact]
    public void ARocketThatDidNotGoOffStopsBeingSteered()
    {
        var graph = Build(
            [Node(30, 0, 9), Node(10, 20, 29)],
            [Edge(0, 1, 200, NavEdgeType.RocketJump, takeoff: 8, bucket: 5, ticks: 20)]);

        var follower = new PathFollower(graph);
        var state = new BotPathState();

        follower.SetGoal(state, NavGraph.ColumnWorldX(25), graph.StandY(1));

        var at = Stand(graph, 0, 8);
        var able = new NavQuery(Team.Red, CanRocketJump: true);
        var loaded = Loaded();

        for (var tick = 0; tick < 40 && !state.RocketFire; tick++)
            follower.Step(state, At(at.X, at.Y), in loaded, tick, able);

        Assert.True(state.RocketFire);

        // Airborne and NOT rocketing: two ticks, so AirTicks passes 1.
        var drifting = At(at.X, at.Y - 30, hSpeed: 0, onGround: false);

        follower.Step(state, in drifting, in loaded, 50, able);
        var keys = follower.Step(state, in drifting, in loaded, 51, able);

        Assert.False(state.FlyingEdge, "the follower kept flying an arc the rocket never made");
        Assert.Equal(PlayerKeys.None, keys & (PlayerKeys.Left | PlayerKeys.Right));

        // ⚠️ The control — the same two ticks WITH moveStatus 1 are steered,
        // so the bail is the moveStatus test and not simply "airborne".
        var rocketing = drifting;
        rocketing.MoveStatus = 1;

        var flying = new BotPathState();
        follower.SetGoal(flying, NavGraph.ColumnWorldX(25), graph.StandY(1));

        for (var tick = 0; tick < 40 && !flying.RocketFire; tick++)
            follower.Step(flying, At(at.X, at.Y), in loaded, tick, able);

        follower.Step(flying, in rocketing, in loaded, 50, able);
        follower.Step(flying, in rocketing, in loaded, 51, able);

        Assert.True(flying.FlyingEdge, "a real rocket jump was not steered");
    }

    /// <summary>
    /// A jump edge leaves from the column the EDGE names, not from the end of
    /// the run.
    /// </summary>
    /// <remarks>
    /// ⚠️ The generator SEARCHED for the takeoff column rather than assuming it.
    /// It is usually the end of the run facing the landing — but where the
    /// landing surface is also what ends the run (climbing onto a crate) the body
    /// is flush against it there and the only flyable arcs start a few cells
    /// back. Re-deriving it from the node's own columns would jump from a column
    /// the build already rejected and quietly turn those edges back into
    /// fictions.
    /// </remarks>
    [Fact]
    public void AJumpLeavesFromTheColumnTheEdgeNames()
    {
        // The run ends at column 9, but the arc was proved from column 4.
        var graph = Build(
            [Node(30, 0, 9), Node(10, 20, 29)],
            [Edge(0, 1, 60, NavEdgeType.Jump, takeoff: 4, bucket: 3, ticks: 30)]);

        var follower = new PathFollower(graph);
        var state = new BotPathState();

        follower.SetGoal(state, NavGraph.ColumnWorldX(25), graph.StandY(1));

        // Standing at the end of the run, right of the takeoff: walk back left to
        // it rather than jumping from here.
        var atEnd = Stand(graph, 0, 9);
        Assert.Equal(PlayerKeys.Left, follower.Step(state, At(atEnd.X, atEnd.Y), 0, new NavQuery(Team.Red)));
    }

    /// <summary>
    /// A jump is a press on alternate ticks, never a hold.
    /// </summary>
    /// <remarks>
    /// ⚠️ <c>Character.events/Begin Step.xml</c> jumps on <c>pressedKeys and $80</c>
    /// — a rising edge — so a bot holding JUMP down jumps exactly once and then
    /// never again. Alternating is what gives a bot that still wants to jump a
    /// fresh press every other tick until it leaves the ground.
    /// </remarks>
    [Fact]
    public void AJumpIsAPressOnAlternateTicksAndNeverAHold()
    {
        // ⚠️ The arc has to cover its own gap, or this is not a fixture a
        // generator could ever have written: takeoff column 9 to landing column
        // 12 is 18 world pixels, and 1.2 px/tick over 20 ticks is 24. A first
        // draft asked 0.5 over 20 — a 10px arc across an 18px gap — and the gate
        // correctly refused it, which looks exactly like the press being broken.
        var graph = Build(
            [Node(30, 0, 9), Node(10, 12, 20)],
            [Edge(0, 1, 30, NavEdgeType.Jump, takeoff: 9, bucket: 1.2, ticks: 20)]);

        var follower = new PathFollower(graph);
        var state = new BotPathState();

        follower.SetGoal(state, NavGraph.ColumnWorldX(16), graph.StandY(1));

        var at = Stand(graph, 0, 9);
        var pressed = new List<bool>();

        for (var tick = 0; tick < 6; tick++)
        {
            pressed.Add(
                follower.Step(state, At(at.X, at.Y), tick, new NavQuery(Team.Red)).HasFlag(PlayerKeys.Jump));
        }

        Assert.Equal([true, false, true, false, true, false], pressed);
    }

    /// <summary>
    /// The gate accepts a standstill when the arc reaches, and starts a run-up
    /// when it does not.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The measurement that decided the gate's whole shape.</b>
    /// "Not moving away" accepts a standstill, which is right for nine arcs in
    /// ten — <b>91.9% of jump edges need no takeoff speed at all</b> — and
    /// catastrophic for the rest: an arc asking for most of the class's ceiling
    /// has no surplus for the tracker to make the acceleration ramp back out of,
    /// and <b>3,310 of 62,069 jump edges are unflyable by a heavy that way</b>.
    /// So the speed test is <see cref="JumpModel.Reach"/>.
    /// <para>
    /// Demanding the arc's <em>full</em> speed instead was tried and lost — 647
    /// to 868 and 674 to 997 ticks on the valley scenarios with no more arrivals
    /// — because it forces a run-up on every jump in the game. The first
    /// assertion here is what that regression would break.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheGateAcceptsAStandstillOnlyWhenTheArcReaches()
    {
        // A gentle arc over a short gap: 18px of ground and twenty ticks to
        // cover it in, so a soldier flies it from a standstill.
        var easy = Build(
            [Node(30, 0, 9), Node(10, 12, 20)],
            [Edge(0, 1, 30, NavEdgeType.Jump, takeoff: 9, bucket: 1.2, ticks: 20)]);

        var easyFollower = new PathFollower(easy);
        var easyState = new BotPathState();
        easyFollower.SetGoal(easyState, NavGraph.ColumnWorldX(16), easy.StandY(1));

        var easyAt = Stand(easy, 0, 9);
        Assert.True(
            easyFollower.Step(easyState, At(easyAt.X, easyAt.Y), 0, new NavQuery(Team.Red))
                .HasFlag(PlayerKeys.Jump),
            "a gentle arc was refused from a standstill; 91.9% of arcs are this one");

        // ⚠️ The same shape, but SHORT and FAST: 54px of ground in twelve ticks
        // wants 4.5 px/tick from tick zero, which is essentially a heavy's
        // ceiling — so there is no surplus for the tracker to make the
        // acceleration ramp back out of and the bot would land short. This is
        // the 3,310-of-62,069 case in miniature.
        var hard = Build(
            [Node(30, 0, 9), Node(10, 18, 26)],
            [Edge(0, 1, 60, NavEdgeType.Jump, takeoff: 9, bucket: 4.5, ticks: 12)]);

        var hardFollower = new PathFollower(hard);
        var hardState = new BotPathState();
        hardFollower.SetGoal(hardState, NavGraph.ColumnWorldX(22), hard.StandY(1));

        var hardAt = Stand(hard, 0, 9);
        var keys = hardFollower.Step(
            hardState, At(hardAt.X, hardAt.Y, playerClass: PlayerClass.Heavy), 0, new NavQuery(Team.Red));

        Assert.False(keys.HasFlag(PlayerKeys.Jump), "an arc the bot cannot reach was taken from a standstill");
        Assert.True(hardState.RunupAt >= 0, "the gate refused without starting a run-up");
        Assert.True(hardState.RunupAt < 9, $"the run-up mark {hardState.RunupAt} is not behind the takeoff");
    }

    /// <summary>
    /// A bot faster than the arc allows brakes rather than leaving hot.
    /// </summary>
    /// <remarks>
    /// Worth bleeding off even though the tracker can correct it: with no key
    /// held GG2 sheds horizontal speed by only about 13% a tick, so a bot
    /// arriving at 9 is still over 3 some eight ticks later.
    /// </remarks>
    [Fact]
    public void ABotFasterThanTheArcBrakes()
    {
        var graph = Build(
            [Node(30, 0, 9), Node(10, 12, 20)],
            [Edge(0, 1, 30, NavEdgeType.Jump, takeoff: 9, bucket: 1.2, ticks: 20)]);

        var follower = new PathFollower(graph);
        var state = new BotPathState();
        follower.SetGoal(state, NavGraph.ColumnWorldX(16), graph.StandY(1));

        var at = Stand(graph, 0, 9);

        // Running right at 9 px/tick into an arc that wants 1.2: brake left.
        var keys = follower.Step(state, At(at.X, at.Y, hSpeed: 9), 0, new NavQuery(Team.Red));

        Assert.False(keys.HasFlag(PlayerKeys.Jump));
        Assert.Equal(PlayerKeys.Left, keys);
    }

    /// <summary>
    /// The escape hatch jumps anyway once the gate has refused for two seconds.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A gate that can refuse is a gate that can refuse for ever.</b> A bot
    /// on a run too short to build the speed, or wedged against a wall it cannot
    /// back away from, would otherwise work the gate for the rest of the round
    /// with every other detector reading it as healthy: it is not stuck (it is
    /// moving) and it is not off route (it is standing exactly where the route
    /// says). Jump on whatever it has instead — that either works, or it lands
    /// back where it started and the off-route machinery already knows what to do.
    /// <para>
    /// ⚠️ In the reference the budget was set in four places and incremented in
    /// none, so this was dead code for its whole life.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheEscapeHatchJumpsAnywayAfterTakeoffPatience()
    {
        // The short fast arc again — no standstill reaches it — on a node with no
        // floor behind it, so the run-up mark is the takeoff column itself and
        // the gate refuses for ever.
        var graph = Build(
            [Node(30, 0, 9), Node(10, 18, 26)],
            [Edge(0, 1, 60, NavEdgeType.Jump, takeoff: 9, bucket: 4.5, ticks: 12)]);

        var follower = new PathFollower(graph);

        // ⚠️ The stagger is not decoration here. A re-plan clears the edge in
        // progress and the budget with it, so a bot's re-plan interval is a hard
        // ceiling on its takeoff budget — at the default stagger of 0 the
        // interval is BOT_REPLAN_TICKS (45), BOT_TAKEOFF_PATIENCE is 60, and the
        // hatch is unreachable. Nothing in the reference remarks on that; it was
        // found by this test failing.
        var state = new BotPathState { ReplanStagger = BotConstants.ReplanTicks - 1 };

        follower.SetGoal(state, NavGraph.ColumnWorldX(22), graph.StandY(1));

        var at = Stand(graph, 0, 9);
        var jumpedAt = -1;

        // ⚠️ The bot is jittered so the stuck detector never fires: what is under
        // test is the takeoff budget, and a stuck bot would be taken away by the
        // other mechanism first.
        for (var tick = 0; tick < BotConstants.TakeoffPatience + 5 && jumpedAt < 0; tick++)
        {
            var jitter = (tick % 2) * 2.0;

            if (follower.Step(state, At(at.X + jitter, at.Y, playerClass: PlayerClass.Heavy), tick,
                    new NavQuery(Team.Red))
                .HasFlag(PlayerKeys.Jump))
            {
                jumpedAt = tick;
            }
        }

        Assert.True(jumpedAt >= 0, "the gate refused for ever");
        Assert.Equal(BotConstants.TakeoffPatience - 1, jumpedAt);
    }

    /// <summary>
    /// In the air the plan replaces the steering, and the target moves with the
    /// clock.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>An arc is flown as a trajectory, not as a speed.</b> The target is
    /// <c>takeoffX + dir * min(distance, needVx * airTicks)</c> — an absolute
    /// world x — so being behind is a reason to press forward and being ahead is
    /// a reason to brake. Holding a speed instead loses most of a cell of ground
    /// over the acceleration ramp with nothing to make it back, and any nudge in
    /// the air is permanent. <b>Measured: 12-19% of jumps landed on the node they
    /// were aimed at before, 100% after.</b>
    /// </remarks>
    [Fact]
    public void InTheAirThePlanReplacesTheSteering()
    {
        var graph = Build(
            [Node(30, 0, 9), Node(10, 20, 29)],
            [Edge(0, 1, 60, NavEdgeType.Jump, takeoff: 9, bucket: 3, ticks: 20)]);

        var follower = new PathFollower(graph);
        var state = new BotPathState();
        follower.SetGoal(state, NavGraph.ColumnWorldX(25), graph.StandY(1));

        var takeoffX = NavGraph.ColumnWorldX(9);

        // One airborne tick: the plan wants takeoffX + 3.
        var behind = follower.Step(
            state, At(takeoffX, graph.StandY(0) - 20, onGround: false), 1, new NavQuery(Team.Red));

        Assert.Equal(1, state.AirTicks);
        Assert.True(state.FlyingEdge, "a bot on a validated arc must say so");
        Assert.Equal(PlayerKeys.Right, behind);

        // Ahead of the plan on the same tick count: brake back.
        var ahead = follower.Step(
            state, At(takeoffX + 40, graph.StandY(0) - 20, onGround: false), 2, new NavQuery(Team.Red));

        Assert.Equal(PlayerKeys.Left, ahead);

        // ⚠️ And the target is CLAMPED at the arc's own distance, so a late bot
        // keeps pressing toward the landing rather than aiming past it. 3 * 20 is
        // 60 pixels: a hundred ticks in, the plan is still 60 pixels out and not
        // three hundred.
        state.AirTicks = 100;

        Assert.Equal(
            PlayerKeys.Right,
            follower.Step(
                state, At(takeoffX + 50, graph.StandY(0) - 20, onGround: false), 3, new NavQuery(Team.Red)));

        state.AirTicks = 100;

        Assert.Equal(
            PlayerKeys.Left,
            follower.Step(
                state, At(takeoffX + 70, graph.StandY(0) - 20, onGround: false), 4, new NavQuery(Team.Red)));
    }

    /// <summary>
    /// A double-jump arc spends its second impulse once, on the tick the
    /// generator proved it at, and only for a class that has one.
    /// </summary>
    /// <remarks>
    /// ⚠️ <c>Rejump</c> is -1 on every other edge kind, so the branch is inert
    /// for them without testing the type. It is a <b>press</b>, not a hold: Begin
    /// Step takes the second jump on the rising edge and refuses it while
    /// <c>vspeed</c> is still above the jump strength, so holding JUMP through the
    /// arc would consume the edge on the takeoff tick and leave nothing to press.
    /// <para>
    /// ⚠️ <c>CanDoubleJump</c> is checked even though the search will not hand
    /// these edges to anything else: a route outlives the plan that made it, and
    /// a bot can change class mid-life while a path is still installed.
    /// </para>
    /// </remarks>
    [Fact]
    public void ADoubleJumpFiresOnceAtTheTickTheGeneratorProved()
    {
        var graph = Build(
            [Node(30, 0, 9), Node(10, 20, 29)],
            [Edge(0, 1, 60, NavEdgeType.DoubleJump, takeoff: 9, bucket: 3, ticks: 20, rejump: 5)]);

        var follower = new PathFollower(graph);
        var scout = new NavQuery(Team.Red, CanDoubleJump: true);
        var state = new BotPathState();

        follower.SetGoal(state, NavGraph.ColumnWorldX(25), graph.StandY(1));

        var takeoffX = NavGraph.ColumnWorldX(9);
        var presses = new List<int>();

        for (var air = 1; air <= 8; air++)
        {
            var keys = follower.Step(
                state, At(takeoffX, graph.StandY(0) - 20, onGround: false), air, scout);

            if (keys.HasFlag(PlayerKeys.Jump))
                presses.Add(air);
        }

        Assert.Equal([5], presses);
        Assert.Equal(1, state.DoubleJumps);

        // ⚠️ A class with no second jump never presses it, whatever the edge says
        // — and the situation is the reference's own: <b>a route outlives the
        // plan that made it</b>, so the route is planned as a scout and then
        // flown by something that cannot double jump.
        //
        // ⚠️ The plan has to be the SCOUT's. A first draft planned and flew with
        // the same no-double-jump query, and the search simply refused the edge:
        // the bot had no route, the branch was never reached, and the control
        // passed with the CanDoubleJump test deleted.
        var reclassed = new BotPathState();
        follower.SetGoal(reclassed, NavGraph.ColumnWorldX(25), graph.StandY(1));
        follower.Plan(reclassed, takeoffX, graph.StandY(0), 0, scout);

        Assert.Equal([0, 1], reclassed.Path);

        var heavy = new NavQuery(Team.Red);

        for (var air = 1; air <= 8; air++)
        {
            // ⚠️ And the timer must not re-plan the route away mid-flight, or the
            // bot loses the edge for a reason that has nothing to do with the
            // test.
            reclassed.ReplanAt = int.MaxValue;

            Assert.False(
                follower.Step(reclassed, At(takeoffX, graph.StandY(0) - 20, onGround: false), air, heavy)
                    .HasFlag(PlayerKeys.Jump),
                $"a class with no second jump pressed one on air tick {air}");
        }

        Assert.Equal(0, reclassed.DoubleJumps);
    }

    /// <summary>
    /// A run-up follows walk edges through the graph, not the node's own columns.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The clamp into the node's own columns was the bug the whole change
    /// is about.</b> A node one anchor column wide — which is what the top step of
    /// a staircase is — resolves that clamp back to the column the bot is already
    /// standing on, so the run-up was a no-op and the jump happened from a
    /// standstill anyway. Walk edges are one continuous floor by construction.
    /// <b>Modelled offline: 3,310 unflyable jump edges to 253, and koth_gallery's
    /// 105 to 0.</b>
    /// </remarks>
    [Fact]
    public void ARunUpFollowsWalkEdgesThroughTheGraph()
    {
        // Node 1 is ONE column wide — the top of a staircase — with a wide floor
        // behind it at node 0.
        var graph = Build(
            [Node(30, 0, 19), Node(30, 20, 20), Node(10, 29, 37)],
            [
                Edge(1, 0, 66), Edge(0, 1, 66),
                Edge(1, 2, 60, NavEdgeType.Jump, takeoff: 20, bucket: 4.5, ticks: 12),
            ]);

        // Backing up six cells from column 20, heading right.
        var mark = JumpModel.RunupColumn(
            graph, node: 1, takeoffColumn: 20, jumpDirection: 1,
            maxCells: BotConstants.RunupCells, new NavQuery(Team.Red));

        Assert.Equal(20 - BotConstants.RunupCells, mark);

        // ⚠️ The control: clamped into node 1's own span the answer is 20 — the
        // column the bot is already on — and the run-up is a no-op.
        Assert.NotEqual(20, mark);

        // With nowhere to back up to, it gives up rather than walking off the
        // node: the run-up is the map's to give.
        var perch = Build(
            [Node(30, 0, 19), Node(30, 20, 20), Node(10, 29, 37)],
            [Edge(1, 2, 60, NavEdgeType.Jump, takeoff: 20, bucket: 4.5, ticks: 12)]);

        Assert.Equal(
            20,
            JumpModel.RunupColumn(perch, 1, 20, 1, BotConstants.RunupCells, new NavQuery(Team.Red)));
    }

    /// <summary>
    /// A bot part-way through a run-up is not treated as off route.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A run-up deliberately walks BACKWARDS along the route</b>, and the
    /// forward-only route advance cannot see that. Without the suppression a bot
    /// backing up to build speed lands on a node the route does not mention
    /// <em>ahead</em> of it, which reads as a finished move that went somewhere
    /// else — so it fires an off-route event and, on the second consecutive miss,
    /// blacklists the very edge it was preparing for. That is the single reason
    /// the obvious "just let it back up" fix is not a two-line change.
    /// <para>
    /// ⚠️ And <see cref="BotPathState.PathAt"/> must not move: the edge being
    /// prepared for has to stay the current one for the whole run-up, or the
    /// follower is handed a different edge half way through and steers to the
    /// wrong takeoff column.
    /// </para>
    /// </remarks>
    [Fact]
    public void ABotBackingUpForARunUpIsNotOffRoute()
    {
        var graph = Build(
            [Node(30, 0, 19), Node(30, 20, 20), Node(10, 29, 37)],
            [
                Edge(1, 0, 66), Edge(0, 1, 66),
                Edge(1, 2, 60, NavEdgeType.Jump, takeoff: 20, bucket: 4.5, ticks: 12),
            ]);

        var follower = new PathFollower(graph);
        var state = new BotPathState();

        follower.SetGoal(state, NavGraph.ColumnWorldX(33), graph.StandY(2));

        // Standing on the one-column perch as a heavy: the arc is at its ceiling,
        // so the gate refuses and starts a run-up.
        var perch = Stand(graph, 1, 20);
        follower.Step(state, At(perch.X, perch.Y, playerClass: PlayerClass.Heavy), 0, new NavQuery(Team.Red));

        Assert.Equal([1, 2], state.Path);
        Assert.True(state.RunupAt >= 0);
        Assert.Equal(0, state.PathAt);

        // Now back on node 0, which the route does not mention at all.
        var behind = Stand(graph, 0, 14);
        follower.Step(state, At(behind.X, behind.Y, playerClass: PlayerClass.Heavy), 1, new NavQuery(Team.Red));

        Assert.Equal(0, state.OffRouteFires);
        Assert.Equal(0, state.BlacklistFires);
        Assert.Equal(0, state.PathAt);
    }

    /// <summary>
    /// The reach model flies the arc it was given, and a standstill is not the
    /// same as the arc's own speed.
    /// </summary>
    /// <remarks>
    /// The tracker replayed inside <see cref="JumpModel.Reach"/> is
    /// <see cref="PathFollower"/>'s own, so a bot leaving at exactly the arc's
    /// speed covers the arc's own distance. ⚠️ Leaving from a standstill does
    /// not: the acceleration ramp is a deficit the tracker only repays out of the
    /// surplus between the arc's speed and the class's ceiling — and
    /// <c>NAV_JUMP_VX</c> (4.53) is literally a heavy's ceiling, which is why the
    /// fast arcs bite.
    /// </remarks>
    [Fact]
    public void TheReachModelFliesTheArcItWasGiven()
    {
        var soldier = CharacterState.Fresh(PlayerClass.Soldier, Team.Red);

        // Leaving at the arc's own speed: the tracker has nothing to correct.
        var onPlan = JumpModel.Reach(soldier, needVx: 3, flightTicks: 20, v0: 3);
        Assert.InRange(onPlan, 3 * 20 * 0.9, 3 * 20 * 1.1);

        // From a standstill on the same arc: short, but the tracker claws most of
        // it back.
        var fromRest = JumpModel.Reach(soldier, needVx: 3, flightTicks: 20, v0: 0);
        Assert.True(fromRest < onPlan, $"a standstill reached {fromRest} against {onPlan} on plan");

        // ⚠️ A heavy on an arc at its own ceiling has no surplus at all, so the
        // deficit is never repaid — this is the 3,310-of-62,069 case.
        var heavy = CharacterState.Fresh(PlayerClass.Heavy, Team.Red);
        var ceiling = JumpModel.Reach(heavy, needVx: 4.53, flightTicks: 40, v0: 0);

        Assert.True(
            ceiling < 4.53 * 40 * 0.9,
            $"a heavy reached {ceiling} of {4.53 * 40} from a standstill at its own ceiling");
    }

    // --- which of a pair's parallel edges gets steered -------------------------

    /// <summary>
    /// A node pair carrying a double jump AND a rocket jump: the traveller that
    /// can only fly one of them gets that one.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>This was a live bug on the SHIPPED graphs, and it hid an entire
    /// feature.</b> A route is a list of nodes, so the follower re-derives which
    /// edge each hop meant — and it used to take the first the edge index yielded.
    /// <see cref="NavSearch"/> refuses per <em>edge</em>, so the two disagreed
    /// wherever a pair carried more than one type, which is <b>11,615 of the
    /// 104,087 node pairs across the 24 shipped caches</b>
    /// (<c>NavGraphFixtureTests.ParallelEdgesAreCommonAndCarryMoreThanOneType</c>).
    /// <para>
    /// The double-jump/rocket-jump pair is the one that bites: <b>833 pairs where
    /// a soldier's route is costed as a rocket jump</b> and the double jump is
    /// what the index yields first. The bot then flew an ordinary 57px hop where
    /// the arc wanted 276, landed somewhere the route does not mention, and
    /// eventually blacklisted a perfectly good edge — all with
    /// <see cref="BotPathState.UnsupportedEdgeTicks"/> at zero, because the
    /// dispatch switch saw a type it handles.
    /// </para>
    /// <para>
    /// The declared order here is the one the shipped graphs have: the double
    /// jump first. Reverting <c>TryFindEdge</c> to "first match wins" makes this
    /// fail on <see cref="BotPathState.RocketFire"/> — checked, not assumed.
    /// </para>
    /// </remarks>
    [Fact]
    public void ASoldierOnAPairThatAlsoCarriesADoubleJumpStillTakesTheRocketJump()
    {
        // ⚠️ The double jump is declared FIRST and is what the old resolution
        // yielded. Its cost is deliberately the CHEAPER of the two, so this
        // cannot pass by accident on a cost comparison that ignored the query.
        var graph = Build(
            [Node(30, 0, 9), Node(10, 20, 29)],
            [
                Edge(0, 1, 100, NavEdgeType.DoubleJump, takeoff: 8, bucket: 5, ticks: 20, rejump: 6),
                Edge(0, 1, 200, NavEdgeType.RocketJump, takeoff: 8, bucket: 5, ticks: 20),
            ]);

        var follower = new PathFollower(graph);
        var state = new BotPathState();

        follower.SetGoal(state, NavGraph.ColumnWorldX(25), graph.StandY(1));

        var at = Stand(graph, 0, 8);
        var soldier = new NavQuery(Team.Red, CanRocketJump: true);
        var loaded = Loaded();

        var keys = follower.Step(state, At(at.X, at.Y), in loaded, 0, soldier);

        Assert.True(
            state.RocketFire,
            "the soldier was routed over a rocket jump and steered the parallel double jump instead");
        Assert.True((keys & PlayerKeys.Jump) != 0, "it asked for the rocket without jumping");
        Assert.Equal(1, state.RocketJumps);
        Assert.Equal(0, state.DoubleJumps);
        Assert.Equal(0, state.UnsupportedEdgeTicks);
    }

    /// <summary>
    /// The control: the same pair, and a scout gets the double jump.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Without this the test above is satisfied by "always prefer the rocket
    /// jump".</b> The claim is that the follower resolves whatever the search
    /// resolved, which means the answer has to change with the traveller — so the
    /// discriminating fact is the pair of results, not either one.
    /// <para>
    /// A scout may not rocket-jump, so the search costs this hop as the double
    /// jump; the follower must steer that one and must not ask for a rocket it has
    /// no launcher for. <b>Firing one at its own feet is a real outcome</b> —
    /// <c>ScriptedBot</c> ORs <see cref="BotPathState.RocketFire"/> straight into
    /// ATTACK and overrides the combat aim to straight down.
    /// </para>
    /// </remarks>
    [Fact]
    public void AScoutOnTheSamePairTakesTheDoubleJumpInstead()
    {
        var graph = Build(
            [Node(30, 0, 9), Node(10, 20, 29)],
            [
                Edge(0, 1, 100, NavEdgeType.DoubleJump, takeoff: 8, bucket: 5, ticks: 20, rejump: 6),
                Edge(0, 1, 200, NavEdgeType.RocketJump, takeoff: 8, bucket: 5, ticks: 20),
            ]);

        var follower = new PathFollower(graph);
        var state = new BotPathState();

        follower.SetGoal(state, NavGraph.ColumnWorldX(25), graph.StandY(1));

        var at = Stand(graph, 0, 8);
        var scout = new NavQuery(Team.Red, CanDoubleJump: true);
        var none = default(WeaponState);

        var keys = follower.Step(
            state, At(at.X, at.Y, playerClass: PlayerClass.Scout), in none, 0, scout);

        Assert.False(state.RocketFire, "a scout asked for a rocket it does not carry");
        Assert.True((keys & PlayerKeys.Jump) != 0, "the scout never left the ground");
        Assert.Equal(0, state.RocketJumps);
        Assert.Equal(0, state.UnsupportedEdgeTicks);
    }

    /// <summary>
    /// ⚠️ <b>The same fault hid the whole sticky-jump arm.</b>
    /// </summary>
    /// <remarks>
    /// A cache carrying type-7 edges puts a rocket jump beside a sticky jump on
    /// <b>2,460 node pairs</b> and a double jump beside one on 1,124 more, and the
    /// rocket jump is what the index yields first. A demoman carries a Minegun, so
    /// <see cref="RocketModel.Ready"/> was false, the takeoff gate never opened,
    /// and the bot stood on the takeoff column waiting for a launcher it does not
    /// have — <b>zero sticky jumps across 48 real edges on four maps, with the arm
    /// itself entirely correct</b>. <see cref="StickyModel.Ready"/> was never
    /// consulted once.
    /// <para>
    /// ⚠️ <b>Six hypotheses had been eliminated by measurement before this</b> —
    /// the reach, the need, the gate arithmetic, the goal snap, the harness start
    /// and the dispatch switch — and every one of them was correct. They were all
    /// asking whether the arm refused, and the arm was never asked.
    /// </para>
    /// </remarks>
    [Fact]
    public void ADemomanOnAPairThatAlsoCarriesARocketJumpStillPlacesItsMine()
    {
        // The order a sticky cache really has: the rocket jump first.
        var graph = Build(
            [Node(30, 0, 9), Node(10, 20, 29)],
            [
                Edge(0, 1, 100, NavEdgeType.RocketJump, takeoff: 8, bucket: 5, ticks: 20),
                Edge(0, 1, 200, NavEdgeType.StickyJump, takeoff: 8, bucket: 5, ticks: 20),
            ]);

        var follower = new PathFollower(graph);
        var state = new BotPathState();

        follower.SetGoal(state, NavGraph.ColumnWorldX(25), graph.StandY(1));

        var at = Stand(graph, 0, 8);
        var demo = new NavQuery(Team.Red, CanStickyJump: true);
        var loaded = Loaded();

        var keys = follower.Step(
            state, At(at.X, at.Y, playerClass: PlayerClass.Demoman), in loaded, 0, demo);

        Assert.True(state.MineFire, "the demoman was routed over a sticky jump and never placed a mine");
        Assert.False(state.RocketFire, "a demoman asked for a rocket");
        Assert.True((keys & PlayerKeys.Jump) == 0, "it jumped before the mine had settled");
        Assert.Equal(NavConstants.SjSetupTicks, state.StickySetup);
        Assert.Equal(0, state.UnsupportedEdgeTicks);
    }

    /// <summary>
    /// Two edges a traveller can fly BOTH of: the cheaper wins, and an exact tie
    /// leaves the earlier one standing.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>This is A*'s tie-break, not a choice.</b> <see cref="NavSearch"/>
    /// relaxes with <c>existing &gt;= 0 &amp;&amp; existing &lt;= tentative →
    /// continue</c>, so among parallel edges it keeps the strictly cheapest and an
    /// equal cost never displaces what is already there. The follower has to
    /// reproduce that exactly or it steers an arc the route did not cost, which is
    /// the whole bug this group of tests exists for.
    /// <para>
    /// ⚠️ The occupancy multiplier deliberately does not enter it: it is keyed on
    /// the node pair, so it scales every parallel edge by the same factor and
    /// cannot reorder them.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheCheaperParallelEdgeWinsAndAnExactTieGoesToTheFirst()
    {
        // A traveller that can fly either arc, so the class filter cannot be what
        // decides it.
        var both = new NavQuery(Team.Red, CanDoubleJump: true, CanRocketJump: true);
        var loaded = Loaded();

        // Strictly cheaper rocket jump: it wins even though the double jump is
        // declared first.
        var cheaper = Build(
            [Node(30, 0, 9), Node(10, 20, 29)],
            [
                Edge(0, 1, 200, NavEdgeType.DoubleJump, takeoff: 8, bucket: 5, ticks: 20, rejump: 6),
                Edge(0, 1, 100, NavEdgeType.RocketJump, takeoff: 8, bucket: 5, ticks: 20),
            ]);

        var cheaperState = new BotPathState();
        var cheaperFollower = new PathFollower(cheaper);
        cheaperFollower.SetGoal(cheaperState, NavGraph.ColumnWorldX(25), cheaper.StandY(1));

        var at = Stand(cheaper, 0, 8);
        cheaperFollower.Step(cheaperState, At(at.X, at.Y), in loaded, 0, both);

        Assert.True(cheaperState.RocketFire, "the strictly cheaper parallel edge did not win");

        // The same pair at an identical cost: the first declared stands.
        var tied = Build(
            [Node(30, 0, 9), Node(10, 20, 29)],
            [
                Edge(0, 1, 150, NavEdgeType.DoubleJump, takeoff: 8, bucket: 5, ticks: 20, rejump: 6),
                Edge(0, 1, 150, NavEdgeType.RocketJump, takeoff: 8, bucket: 5, ticks: 20),
            ]);

        var tiedState = new BotPathState();
        var tiedFollower = new PathFollower(tied);
        tiedFollower.SetGoal(tiedState, NavGraph.ColumnWorldX(25), tied.StandY(1));

        tiedFollower.Step(tiedState, At(at.X, at.Y), in loaded, 0, both);

        Assert.False(tiedState.RocketFire, "an exact tie displaced the edge declared first");
    }

    /// <summary>
    /// ⚠️ <b>A route is steered against the query that PLANNED it, and losing the
    /// capability mid-arc must not change which edge is being flown.</b>
    /// </summary>
    /// <remarks>
    /// <b>Measured, and it is a loop.</b> <c>ScriptedBot</c> rebuilds
    /// <see cref="NavQuery.CanRocketJump"/> from the live weapon every tick, and
    /// firing the rocket that starts a rocket jump puts the launcher straight onto
    /// its refire cooldown — so the capability is false on the tick <em>after</em>
    /// takeoff, with the bot already in the air on the arc. Re-resolving the hop
    /// against the live query there abandons an arc that has already been paid for
    /// at 30 hp.
    /// <para>
    /// On <c>avanti-ramp-pocket-to-intel</c>, with the live query: launch, re-plan
    /// mid-flight, land, walk back, reload, launch again — <b>five times, 150 hp,
    /// and a leg that used to arrive in 175 ticks did not arrive at all in 600</b>.
    /// With the plan-time query it arrives in 217 with one rocket jump, and
    /// <see cref="ScenarioSuiteTests"/> pins that end to end.
    /// </para>
    /// <para>
    /// The live query still decides everything it should: the next <em>plan</em>
    /// uses it, and the takeoff gate re-checks <see cref="RocketModel.Ready"/>
    /// before leaving the ground. What it must not do is change the arc halfway
    /// along.
    /// </para>
    /// </remarks>
    [Fact]
    public void LosingTheRocketMidArcDoesNotReplanOutOfTheArcItIsFlying()
    {
        var graph = Build(
            [Node(30, 0, 9), Node(10, 20, 29)],
            [
                Edge(0, 1, 100, NavEdgeType.DoubleJump, takeoff: 8, bucket: 5, ticks: 20, rejump: 6),
                Edge(0, 1, 200, NavEdgeType.RocketJump, takeoff: 8, bucket: 5, ticks: 20),
            ]);

        var follower = new PathFollower(graph);
        var state = new BotPathState();

        follower.SetGoal(state, NavGraph.ColumnWorldX(25), graph.StandY(1));

        var at = Stand(graph, 0, 8);
        var loaded = Loaded();

        // Plan and take off with the launcher ready.
        follower.Step(state, At(at.X, at.Y), in loaded, 0, new NavQuery(Team.Red, CanRocketJump: true));
        Assert.True(state.RocketFire);

        var replansAtTakeoff = state.Replans;

        // ⚠️ The next ticks are the real test: the rocket has gone, so the launcher
        // is cold and the live query no longer offers a rocket jump — while the bot
        // is airborne under moveStatus 1, on the arc it just bought.
        var flying = At(at.X, at.Y - 40, hSpeed: 3, onGround: false);
        flying.MoveStatus = 1;

        var cold = default(WeaponState);

        for (var tick = 1; tick <= 8; tick++)
            follower.Step(state, in flying, in cold, tick, new NavQuery(Team.Red));

        Assert.Equal(0, state.EdgeUnroutableFires);
        Assert.Equal(replansAtTakeoff, state.Replans);
        Assert.True(state.FlyingEdge, "the follower stopped flying the arc it had taken off on");
    }
}
