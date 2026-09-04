using Gg2.Bots;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// The follower driving a real character across a real map.
/// </summary>
/// <remarks>
/// <see cref="PathFollowerTests"/> asserts the decisions with the physics taken
/// out; this puts them back. It is the only place that answers the question the
/// whole layer exists for — <b>does a bot get there</b> — and it answers it
/// through <see cref="ScenarioWorld"/>, so the character is moved by the same
/// <c>MatchSimulation.Tick</c> the parity fixtures gate.
/// <para>
/// The leg here avoids the arcs that need a weapon: <see cref="Steerable"/>
/// excludes only the two edge kinds whose takeoff gate asks about a weapon this
/// harness does not model. The leg is chosen by asking the graph for one every
/// edge of which this follower can steer, so these tests widen on their own as
/// more arms land, and <see cref="ABotWalksALegItCanActuallySteer"/> says so if
/// the leg it finds changes shape.
/// </para>
/// </remarks>
public sealed class PathFollowerFixtureTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const string Map = "ctf_truefort";

    /// <summary>Edge kinds this fixture asks a leg to be made of.</summary>
    /// <remarks>
    /// Every kind the generator emits is steered; the two missing here are the
    /// two whose takeoff gate asks about a weapon, and both are refused by the
    /// search on this query anyway (<see cref="NavQuery.CanRocketJump"/> and
    /// <see cref="NavQuery.CanStickyJump"/> default false), so neither can reach a
    /// route here. Keeping them out makes the chosen leg deterministic rather than
    /// making a claim about the follower.
    /// </remarks>
    private static bool Steerable(NavEdgeType type) =>
        type is NavEdgeType.Walk or NavEdgeType.Fall or NavEdgeType.DropThrough
            or NavEdgeType.MoveBox or NavEdgeType.Jump or NavEdgeType.DoubleJump;

    private static ScenarioWorld Load()
    {
        ReferenceTree.RequireAvailable();
        ReferenceTree.RequireNavCache();

        return ScenarioWorld.Load(Map, 1, ReferenceTree.IncludedFiles, ReferenceTree.NavCacheDirectory);
    }

    /// <summary>
    /// Finds a leg the <em>search</em> routes entirely over edges this follower
    /// can steer.
    /// </summary>
    /// <remarks>
    /// "There exists a walk-only way there" is the wrong question: the follower
    /// is handed <see cref="NavSearch"/>'s route, not any route — and the search
    /// costs a jump the same as anything else, so it happily prefers one across a
    /// leg a bot could have walked. On ctf_truefort the first walk-only chain
    /// found had a jump in the middle of the route the search actually returned.
    /// <para>
    /// So: propose candidates with a breadth-first walk over steerable edges,
    /// longest first, and accept the first one whose <em>planned</em> route is
    /// steerable end to end. That is the leg a bot would really be given.
    /// </para>
    /// </remarks>
    private static (int Start, int Goal, List<int> Route) FindWalkableLeg(NavGraph graph)
    {
        var search = new NavSearch(graph);
        var query = new NavQuery(Team.Red);
        var candidates = new List<(int Start, int Goal, int Hops)>();

        var depth = new int[graph.NodeCount];
        var queue = new Queue<int>();

        for (var start = 0; start < graph.NodeCount; start++)
        {
            Array.Fill(depth, -1);
            depth[start] = 0;
            queue.Clear();
            queue.Enqueue(start);

            var best = (Goal: -1, Hops: 0);

            while (queue.Count > 0)
            {
                var at = queue.Dequeue();

                foreach (var edge in graph.EdgesFrom(at))
                {
                    if (!Steerable(edge.Type) || depth[edge.To] >= 0 || edge.Gate != NavGate.None)
                        continue;

                    depth[edge.To] = depth[at] + 1;
                    queue.Enqueue(edge.To);

                    if (depth[edge.To] > best.Hops)
                        best = (edge.To, depth[edge.To]);
                }
            }

            if (best.Hops >= 3)
                candidates.Add((start, best.Goal, best.Hops));
        }

        // Longest first, then by node index so the answer does not depend on the
        // order the graph happened to be walked in.
        candidates.Sort((a, b) => b.Hops != a.Hops ? b.Hops - a.Hops : a.Start - b.Start);

        foreach (var (start, goal, _) in candidates)
        {
            var route = new List<int>();

            if (!search.Find(start, goal, query, route) || route.Count < 4)
                continue;

            if (RouteIsSteerable(graph, route))
                return (start, goal, route);
        }

        return (-1, -1, []);
    }

    /// <summary>Whether every hop of a route is an edge the follower can steer.</summary>
    private static bool RouteIsSteerable(NavGraph graph, List<int> route)
    {
        for (var i = 0; i + 1 < route.Count; i++)
        {
            var steerable = false;

            foreach (var edge in graph.EdgesFrom(route[i]))
            {
                if (edge.To == route[i + 1] && Steerable(edge.Type))
                    steerable = true;
            }

            if (!steerable)
                return false;
        }

        return true;
    }

    /// <summary>
    /// A bot walks a real leg on a real map and stops on its goal.
    /// </summary>
    /// <remarks>
    /// The claim the whole follower exists to support. Every part of the spine is
    /// exercised on the way — the plan, the forward-only route advance, the
    /// steering, the brake band and the arrival — against a character moved by
    /// the real simulation rather than by a test's arithmetic.
    /// <para>
    /// The controls are what make it a gate rather than a demonstration: a bot
    /// with no goal must not move, and the arrival must be an arrival rather than
    /// the budget running out. Without those, a follower that pressed RIGHT
    /// unconditionally would pass.
    /// </para>
    /// </remarks>
    [Fact]
    public void ABotWalksALegItCanActuallySteer()
    {
        var world = Load();
        var graph = world.Nav;

        Assert.NotNull(graph);

        var (start, goal, route) = FindWalkableLeg(graph);

        Assert.True(
            start >= 0,
            $"no leg on {Map} is planned entirely over edges this follower can steer; there is nothing "
            + "to walk until the jump branch lands");

        Assert.True(route.Count >= 4, $"the leg found is only {route.Count} nodes; too short to prove anything");

        var startX = NavGraph.ColumnWorldX((graph.Nodes[start].X0 + graph.Nodes[start].X1) / 2);
        var slot = world.Place(PlayerClass.Soldier, Team.Red, startX, graph.StandY(start));

        var goalX = NavGraph.ColumnWorldX((graph.Nodes[goal].X0 + graph.Nodes[goal].X1) / 2);
        var goalY = graph.StandY(goal);

        var follower = new PathFollower(graph);
        var state = new BotPathState();
        Assert.True(follower.SetGoal(state, goalX, goalY));

        world.Drive(slot, new Followed(follower, state, new NavQuery(Team.Red)));

        // Six thousand is the reference's own budget for a leg across this map
        // (`truefort-spawn-to-intel`), and it is not slack: the leg chosen here
        // is the longest steerable one on ctf_truefort, not spawn-to-intel.
        //
        // Pinned figures: 1,786 ticks, 39 re-plans, 0 stuck fires, 10 off-route
        // events, 0 blacklists. This is one of the project's exactly-pinned
        // end-to-end bot measurements (see CLAUDE.md); a failure here is a
        // re-record, not a broken test — confirm the change was intended before
        // updating the numbers.
        //
        // The reference quotes 2,200-2,500 ticks for its own shorter leg, so this
        // port is still ahead of it on a longer route.
        const int Budget = 6000;

        var arrivedAt = -1;

        for (var tick = 0; tick < Budget && !state.Arrived; tick++)
        {
            world.Step();
            arrivedAt = tick;
        }

        Assert.True(
            state.Arrived,
            $"did not arrive in {Budget} ticks: {Math.Abs(world.State.Characters[slot].X - state.GoalX):0.#}px "
            + $"short, replans {state.Replans}, stuck {state.StuckFires}, off-route {state.OffRouteFires}, "
            + $"blacklisted {state.BlacklistFires}");

        // Pinned exactly, the same discipline `BotObjectiveTests`' intel run and
        // `ScenarioSuiteTests.Baseline` ask for: every draw is seeded, the graph
        // comes off disk and the physics is IEEE doubles, so a number that moves
        // at all is a behaviour change, not noise. Equally, a pin that does not
        // move when you changed something real says the change never reached what
        // it measures — quote the diff with the number, never the number alone.
        Assert.Equal(1786, arrivedAt);
        Assert.Equal(39, state.Replans);
        Assert.Equal(0, state.StuckFires);
        Assert.Equal(10, state.OffRouteFires);
        Assert.Equal(0, state.BlacklistFires);

        output.WriteLine(
            $"{route.Count} nodes, arrived t={state.ArrivedAt}, replans {state.Replans}, "
            + $"stuck {state.StuckFires}, off-route {state.OffRouteFires}, "
            + $"blacklisted {state.BlacklistFires}");

        // Arrived means stopped on it, not merely near it.
        Assert.True(Math.Abs(world.State.Characters[slot].X - goalX) <= BotConstants.ArriveTol);

        // Nothing on the route was an edge kind the follower cannot steer, so
        // this is a real crossing rather than a lucky one.
        Assert.Equal(0, state.UnsupportedEdgeTicks);

        // Pinned exactly, the discipline `ScenarioSuiteTests.Baseline`,
        // `BotObjectiveTests`' intel run and the pinned `SIM_UUID` all ask for:
        // every draw is seeded, the graph comes off disk and the physics is IEEE
        // doubles, so a number that moves at all is a behaviour change, not
        // noise. A loose bound here can hide a large regression while looking
        // like diligence, so these are exact rather than thresholds.
        //
        // Zero blacklists is the load-bearing one: a bot that starts banning
        // edges again still arrives — just slowly, inside any budget, with the
        // route quality gone and nothing red.
        Assert.Equal(121, route.Count);
        Assert.Equal(1786, state.ArrivedAt);
        Assert.Equal(39, state.Replans);
        Assert.Equal(0, state.StuckFires);
        Assert.Equal(10, state.OffRouteFires);
        Assert.Equal(0, state.BlacklistFires);

        // The control: the same bot with no goal presses nothing and goes
        // nowhere, so the walk above is the follower's doing.
        var idle = Load();
        var idleSlot = idle.Place(PlayerClass.Soldier, Team.Red, startX, graph.StandY(start));
        idle.Drive(idleSlot, new Followed(new PathFollower(graph), new BotPathState(), new NavQuery(Team.Red)));

        var before = idle.State.Characters[idleSlot].X;
        idle.Step(120);

        Assert.Equal(before, idle.State.Characters[idleSlot].X);
    }

    /// <summary>
    /// A goal the graph cannot reach leaves the bot standing, and does not
    /// search every tick.
    /// </summary>
    /// <remarks>
    /// The throttle that matters at twelve bots. A goal that is genuinely
    /// unreachable makes every search fail, and retrying it every tick is a full
    /// A* per bot per frame that can never succeed — so planning is on the timer
    /// even when it fails.
    /// </remarks>
    [Fact]
    public void AnUnreachableGoalIsSearchedForOnTheTimerAndNotEveryTick()
    {
        var world = Load();
        var graph = world.Nav!;

        var follower = new PathFollower(graph);
        var state = new BotPathState();

        // Somewhere off the map entirely: the snap finds nothing, so the goal
        // cannot even be set.
        Assert.False(follower.SetGoal(state, -10_000, -10_000));
        Assert.False(state.HasGoal);

        // A goal that snaps but whose node no route reaches: a blue-gated pocket
        // asked for by a red bot. Rather than hunt for one, ask for a real node
        // and then ban every way in.
        var start = 0;
        var goal = graph.NodeCount - 1;

        var startX = NavGraph.ColumnWorldX((graph.Nodes[start].X0 + graph.Nodes[start].X1) / 2);
        Assert.True(follower.SetGoal(
            state,
            NavGraph.ColumnWorldX((graph.Nodes[goal].X0 + graph.Nodes[goal].X1) / 2),
            graph.StandY(goal)));

        for (var i = 0; i < graph.NodeCount; i++)
            state.BlacklistEdge(i, goal, 0);

        var ticks = 0;
        var slot = world.Place(PlayerClass.Soldier, Team.Red, startX, graph.StandY(start));
        world.Drive(slot, new Followed(follower, state, new NavQuery(Team.Red), () => ticks));

        for (; ticks < 200; ticks++)
            world.Step();

        Assert.False(state.Arrived);

        // 200 ticks at BOT_REPLAN_TICKS apart is five searches, not two hundred.
        Assert.True(
            state.Replans <= (200 / BotConstants.ReplanTicks) + 2,
            $"an unreachable goal was searched for {state.Replans} times in 200 ticks");
    }

    /// <summary>An input source that hands the follower's keys to the simulation.</summary>
    /// <remarks>
    /// The bot seam doing exactly what docs/ARCHITECTURE.md says it is for: the
    /// follower produces the same <see cref="PlayerInput"/> a socket does, and
    /// the world cannot tell.
    /// </remarks>
    private sealed class Followed(
        PathFollower follower, BotPathState state, NavQuery query, Func<int>? clock = null) : IInputSource
    {
        public PlayerInput Poll(in WorldView world, int slot, int tick)
        {
            if (!world.IsAlive(slot))
                return PlayerInput.None;

            ref readonly var character = ref world.Character(slot);

            var keys = follower.Step(state, in character, clock?.Invoke() ?? tick, query);

            return new PlayerInput(keys, 0, 0);
        }
    }
}
