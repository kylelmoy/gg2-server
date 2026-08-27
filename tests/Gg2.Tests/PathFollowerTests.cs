using Gg2.Bots;
using Gg2.Protocol;

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
    private static double[] Edge(int from, int to, double cost, NavEdgeType type = NavEdgeType.Walk, int takeoff = -1) =>
        [from, to, (double)type, 0, cost, cost, 0, takeoff, 0];

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

        var keys = follower.Step(state, start.X, start.Y, 0, onGround: true, 0, new NavQuery(Team.Red));

        Assert.Equal([0, 1, 2], state.Path);
        Assert.Equal(1, state.Replans);
        Assert.Equal(PlayerKeys.Right, keys);

        // ...and the other way round, from the far end.
        var back = new BotPathState();
        Assert.True(follower.SetGoal(back, Stand(graph, 0, 4).X, Stand(graph, 0, 4).Y));

        var far = Stand(graph, 2, 45);
        Assert.Equal(
            PlayerKeys.Left,
            follower.Step(back, far.X, far.Y, 0, onGround: true, 0, new NavQuery(Team.Red)));
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
        Assert.Equal(PlayerKeys.None, follower.Step(state, at.X, at.Y, 0, true, 0, new NavQuery(Team.Red)));
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
        follower.Step(state, start.X, start.Y, 0, onGround: true, 0, new NavQuery(Team.Red));

        Assert.Equal(0, state.PathAt);

        var overNodeOne = Stand(graph, 1, 24);

        follower.Step(state, overNodeOne.X, overNodeOne.Y, 0, onGround: false, 1, new NavQuery(Team.Red));
        Assert.Equal(0, state.PathAt);

        follower.Step(state, overNodeOne.X, overNodeOne.Y, 0, onGround: true, 2, new NavQuery(Team.Red));
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
        var braking = follower.Step(state, goal.X, goal.Y, 5, onGround: true, 0, new NavQuery(Team.Red));

        Assert.Equal(PlayerKeys.Left, braking);
        Assert.False(state.Arrived);
        Assert.True(state.HasGoal);

        // Running left: brake right.
        Assert.Equal(
            PlayerKeys.Right,
            follower.Step(state, goal.X, goal.Y, -5, onGround: true, 1, new NavQuery(Team.Red)));

        // Stopped on the ground inside the band: arrived, and the goal is
        // cleared so nothing keeps steering to it.
        var arrived = follower.Step(state, goal.X, goal.Y, 0, onGround: true, 2, new NavQuery(Team.Red));

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
        follower.Step(state, goal.X, goal.Y, 0, onGround: false, 0, new NavQuery(Team.Red));

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
        follower.Step(state, goal.X, goal.Y, 0, onGround: true, 0, new NavQuery(Team.Red));
        Assert.True(state.Arrived);

        // Now the same position and route, but marked stale.
        var stale = new BotPathState { HasGoal = true, GoalX = goal.X, GoalY = goal.Y, PathStale = true };
        stale.Path.Add(0);
        stale.ReplanAt = int.MaxValue; // do not let the timer re-plan it away

        var keys = follower.Step(stale, goal.X, goal.Y, 0, onGround: true, 0, new NavQuery(Team.Red));

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
        follower.Step(state, NavGraph.ColumnWorldX(4), graph.StandY(0), 0, true, 0, new NavQuery(Team.Red));

        Assert.Equal([0, 1, 2], state.Path);
        state.EdgeFrom = 0;
        state.EdgeTo = 1;

        // Land on node 3, which the route does not mention.
        var off = (X: NavGraph.ColumnWorldX(4), Y: graph.StandY(3));

        follower.Step(state, off.X, off.Y, 0, onGround: true, 1, new NavQuery(Team.Red));

        Assert.Equal(1, state.OffRouteFires);
        Assert.Equal(0, state.BlacklistFires);
        Assert.Equal(2, state.Replans); // the re-plan is immediate

        // The search from a dead end fails, so the old route is kept and the bot
        // is still standing somewhere it does not mention.
        Assert.Equal([0, 1, 2], state.Path);

        // Set the same edge up again and miss it a second time.
        state.EdgeFrom = 0;
        state.EdgeTo = 1;
        follower.Step(state, off.X, off.Y, 0, onGround: true, 2, new NavQuery(Team.Red));

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
            follower.Step(state, at.X, at.Y, 0, onGround: true, tick, new NavQuery(Team.Red));

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
            follower.Step(wobbling, at.X + jitter, at.Y, 0, true, tick, new NavQuery(Team.Red));
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
        var here = follower.Step(state, middle, graph.StandY(0), 0, true, 0, new NavQuery(Team.Red));
        Assert.True(here.HasFlag(PlayerKeys.Down), $"at the middle the bot pressed {here}");

        // At the end: walk to the middle first, no DOWN.
        var end = NavGraph.ColumnWorldX(0);
        var away = follower.Step(state, end, graph.StandY(0), 0, true, 1, new NavQuery(Team.Red));
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
            follower.Step(state, middle, graph.StandY(0), 0, true, 0, new NavQuery(Team.Red)));

        // In the air and drifting right of the takeoff column: press back left to
        // hold it, and report the bot as flying a validated edge.
        var takeoffX = NavGraph.ColumnWorldX(9);
        var drifted = follower.Step(state, takeoffX + 10, graph.StandY(0) + 20, 4, false, 1, new NavQuery(Team.Red));

        Assert.Equal(PlayerKeys.Left, drifted);
        Assert.True(state.FlyingEdge, "a bot part-way through a validated fall must say so");

        // Drifting left of it: press right.
        Assert.Equal(
            PlayerKeys.Right,
            follower.Step(state, takeoffX - 10, graph.StandY(0) + 20, -4, false, 2, new NavQuery(Team.Red)));
    }

    /// <summary>
    /// An unimplemented jump edge is counted and abandoned rather than faked.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The contract until phase 7 item 4b-ii lands.</b> Steering a jump with
    /// an approximation would fly an arc the generator never validated, which is
    /// exactly what the whole takeoff apparatus exists to prevent — so the
    /// follower does nothing, the stuck detector takes the edge away, and the
    /// search routes round it. <see cref="BotPathState.UnsupportedEdgeTicks"/> is
    /// what stops that being silent.
    /// <para>
    /// This test is <b>expected to be deleted</b> when the jump branch lands. It
    /// is here so that "the bot cannot jump yet" is a stated, checked property
    /// rather than something a reader has to infer from a missing case.
    /// </para>
    /// </remarks>
    [Fact]
    public void AnUnimplementedJumpEdgeIsCountedAndAbandoned()
    {
        var graph = Build(
            [Node(30, 0, 9), Node(10, 20, 29)],
            [Edge(0, 1, 200, NavEdgeType.Jump, takeoff: 8)]);

        var follower = new PathFollower(graph);
        var state = new BotPathState();

        follower.SetGoal(state, NavGraph.ColumnWorldX(25), graph.StandY(1));

        var at = Stand(graph, 0, 4);

        for (var tick = 0; tick < BotConstants.StuckTicks + 1; tick++)
            follower.Step(state, at.X, at.Y, 0, onGround: true, tick, new NavQuery(Team.Red));

        Assert.True(state.UnsupportedEdgeTicks > 0, "the jump edge was steered by something");
        Assert.Equal(1, state.StuckFires);
        Assert.Contains(NavGraph.EdgeKey(0, 1), state.Blacklist);

        // ⚠️ And it never presses JUMP: a press the generator did not plan is
        // worse than no press, because it launches an arc nothing validated.
        Assert.Equal(
            PlayerKeys.None,
            follower.Step(state, at.X, at.Y, 0, true, 99, new NavQuery(Team.Red)) & PlayerKeys.Jump);
    }
}
