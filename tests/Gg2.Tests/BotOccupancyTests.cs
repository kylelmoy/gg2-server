using Gg2.Bots;
using Gg2.Protocol;
using Gg2.Simulation;
using Xunit.Abstractions;

namespace Gg2.Tests;

/// <summary>
/// Edge occupancy: why a team of bots does not all walk down the same corridor.
/// </summary>
/// <remarks>
/// <b>This is the only safe way to spread a team out, and a per-edge random
/// jitter is not.</b> The jitter was tried in the reference and deleted: it is
/// mean-reverting, so over a sixty-edge route every candidate inflates by the same
/// average and all eight bots got the identical route anyway. Occupancy works
/// because it is structured — what it makes expensive is precisely what is crowded
/// — shared, and coarse.
/// </remarks>
public sealed class BotOccupancyTests(ITestOutputHelper output)
{
    private const string Map = "ctf_truefort";

    /// <summary>
    /// A leg with two genuinely different lanes.
    /// </summary>
    /// <remarks>
    /// <b>Swept for, not chosen.</b> Most legs have one sensible route and a
    /// 25% penalty on a few edges does not move them, so a fixture picked by eye
    /// gates nothing at all. Swept across every seventh start and eleventh goal on
    /// two maps: node 0 → 143 goes from a 32-node route to a 14-node one with
    /// <b>one</b> node in common, which is as unambiguous as this gets.
    /// </remarks>
    private const int From = 0;

    private const int To = 143;

    private static NavGraph Graph()
    {
        ReferenceTree.RequireNavCache();
        return NavCache.Load(ReferenceTree.NavCacheDirectory, Map, 1);
    }

    /// <summary>A route, counted against a team, as a bot would hold it.</summary>
    private static BotPathState Walking(IEnumerable<int> route, BotOccupancy occupancy, Team team)
    {
        var state = new BotPathState();
        state.Path.AddRange(route);
        occupancy.Add(state, team);
        return state;
    }

    /// <summary>
    /// Four bots on one team asking for the same leg do not all get the same
    /// route.
    /// </summary>
    /// <remarks>
    /// <b>The claim the whole mechanism exists for, and the control is the half
    /// that matters:</b> the identical four searches with no occupancy return the
    /// identical route four times, which is what the server did before this.
    /// </remarks>
    [Fact]
    public void AWholeTeamDoesNotPlanTheIdenticalRoute()
    {
        var graph = Graph();
        var search = new NavSearch(graph);
        var query = new NavQuery(Team.Red);

        var shared = new BotOccupancy();
        var spread = new List<string>();

        for (var bot = 0; bot < 4; bot++)
        {
            var route = new List<int>();
            Assert.True(search.Find(From, To, query, route, null, shared.Counts(Team.Red)));

            spread.Add(string.Join(',', route));
            Walking(route, shared, Team.Red);
        }

        var plain = new List<string>();

        for (var bot = 0; bot < 4; bot++)
        {
            var route = new List<int>();
            Assert.True(search.Find(From, To, query, route));
            plain.Add(string.Join(',', route));
        }

        output.WriteLine($"{spread.Distinct().Count()} distinct routes with occupancy, {plain.Distinct().Count()} without");

        // The control: without it, four bots walk in single file.
        Assert.Single(plain.Distinct());

        Assert.True(
            spread.Distinct().Count() > 1,
            "every bot took the same route despite its team-mates already being on it");
    }

    /// <summary>
    /// The other team is not pushed around by ours.
    /// </summary>
    /// <remarks>
    /// <b>Per team rather than one shared map, because the thing being spread
    /// out is a team.</b> A blue bot has no reason to avoid the route red is
    /// walking — if anything it wants to be on it — and folding both into one map
    /// would have each team's routes push the other's around for no reason anyone
    /// could see from the outside.
    /// </remarks>
    [Fact]
    public void OneTeamsCrowdingDoesNotMoveTheOther()
    {
        var graph = Graph();
        var search = new NavSearch(graph);
        var shared = new BotOccupancy();

        var red = new List<int>();
        Assert.True(search.Find(From, To, new NavQuery(Team.Red), red));
        Walking(red, shared, Team.Red);

        // The premise: red really is crowded now, and a red bot would move.
        var redAgain = new List<int>();
        Assert.True(search.Find(From, To, new NavQuery(Team.Red), redAgain, null, shared.Counts(Team.Red)));
        Assert.False(redAgain.SequenceEqual(red), "red was not moved, so this gates nothing");

        // Blue's answer is compared with BLUE's own uncrowded answer, never
        // with red's. The two teams do not agree about this leg in the first
        // place — a gate is passable to the team that owns it — so a test that
        // expected the same route would fail on the gates and say nothing at all
        // about occupancy.
        var alone = new List<int>();
        Assert.True(search.Find(From, To, new NavQuery(Team.Blue), alone));

        var crowded = new List<int>();
        Assert.True(search.Find(From, To, new NavQuery(Team.Blue), crowded, null, shared.Counts(Team.Blue)));

        Assert.Equal(alone, crowded);
        Assert.Equal(0, shared.EdgeCount(Team.Blue));
    }

    /// <summary>
    /// A planner does not charge a bot for the route it is already walking.
    /// </summary>
    /// <remarks>
    /// <b>Not an optimisation — it is what makes a route stable across
    /// re-plans.</b> A bot that can see its own route in the counts is charged
    /// extra for the path it is on, so every re-plan tries to move it somewhere
    /// else and the whole team churns lanes every <c>BOT_REPLAN_TICKS</c>.
    /// Measured in the reference on this map: with self excluded, 2 of 16 re-plans
    /// changed a route; with a node-keyed penalty — which cannot exclude self
    /// cleanly, since a bot stands on its own nodes — it was 15 of 16.
    /// <para>
    /// The control is the premise: a <em>second</em> bot planning the same leg at
    /// the same moment is moved, so the counts are demonstrably not empty.
    /// </para>
    /// </remarks>
    [Fact]
    public void APlannerDoesNotChargeABotForItsOwnRoute()
    {
        var graph = Graph();
        var follower = new PathFollower(graph);
        var shared = new BotOccupancy();
        var query = new NavQuery(Team.Red);

        var state = new BotPathState();
        var (gx, gy) = BotSuppress.Centre(graph, To);
        var (sx, sy) = BotSuppress.Centre(graph, From);

        Assert.True(follower.SetGoal(state, gx, gy));
        Assert.True(follower.Plan(state, sx, sy, 0, in query, shared));

        var first = state.Path.ToArray();
        Assert.True(state.Occupied);
        Assert.Equal(Team.Red, state.OccupiedTeam);

        // The premise: a team-mate asking for the same leg right now is moved.
        var mate = new List<int>();
        Assert.True(new NavSearch(graph).Find(From, To, query, mate, null, shared.Counts(Team.Red)));
        Assert.False(mate.SequenceEqual(first), "the counts moved nobody, so self-exclusion gates nothing");

        // ReplanAt and PlanFailUntil are the follower's own throttles; a
        // re-plan from the same place at a later tick is what a real bot does
        // every BOT_REPLAN_TICKS.
        Assert.True(follower.Plan(state, sx, sy, BotConstants.ReplanTicks * 2, in query, shared));

        output.WriteLine($"route {first.Length} nodes, re-planned to {state.Path.Count}");
        Assert.Equal(first, state.Path.ToArray());
    }

    /// <summary>
    /// Adding twice counts once, and removing twice does not go negative.
    /// </summary>
    /// <remarks>
    /// <b>A count that goes negative makes an edge CHEAPER the more bots are on
    /// it</b>, which is the exact opposite of the point — and it is reachable,
    /// because a route is added in one place and removed in three: on every
    /// re-plan, arrival and death; when the bot leaves or the map changes; and by
    /// the planner itself before it searches.
    /// </remarks>
    [Fact]
    public void TheBookkeepingIsIdempotentInBothDirections()
    {
        var graph = Graph();
        var search = new NavSearch(graph);
        var route = new List<int>();
        Assert.True(search.Find(From, To, new NavQuery(Team.Red), route));

        var shared = new BotOccupancy();
        var state = Walking(route, shared, Team.Red);

        var once = shared.EdgeCount(Team.Red);
        Assert.True(once > 0);

        shared.Add(state, Team.Red);
        Assert.Equal(once, shared.EdgeCount(Team.Red));

        // Every edge is on exactly one bot, so every count is 1 — a second add
        // that took would show as a 2 rather than as more keys.
        foreach (var count in shared.Counts(Team.Red)!.Values)
            Assert.Equal(1, count);

        shared.Remove(state);
        Assert.Equal(0, shared.EdgeCount(Team.Red));

        shared.Remove(state);
        Assert.Equal(0, shared.EdgeCount(Team.Red));

        // And the case the flag actually protects, which the empty map above
        // does NOT reach: a second bot on the same edges. `if (was <= 0) remove`
        // means a count can never be stored negative, so a double-decrement of a
        // lone bot's route is invisible — what it costs is somebody ELSE's count.
        // One bot removed twice must leave the other's 1 standing, not 0.
        var mine = Walking(route, shared, Team.Red);
        var theirs = Walking(route, shared, Team.Red);

        foreach (var count in shared.Counts(Team.Red)!.Values)
            Assert.Equal(2, count);

        shared.Remove(mine);
        shared.Remove(mine);

        Assert.Equal(theirs.Path.Count - 1, shared.EdgeCount(Team.Red));

        foreach (var count in shared.Counts(Team.Red)!.Values)
            Assert.Equal(1, count);
    }

    /// <summary>
    /// A removal goes to the team the route was counted against.
    /// </summary>
    /// <remarks>
    /// <b>The two genuinely come apart.</b> Autobalance switches the
    /// lowest-scoring player on the larger team mid-round and can perfectly well
    /// pick a bot, and a map rotation re-teams every one of them. A removal that
    /// read the bot's team <em>now</em> would subtract from the map it has just
    /// joined and leave its old team's counts raised for ever — an edge nothing is
    /// walking that every future search on that team is charged extra for.
    /// </remarks>
    [Fact]
    public void ARemovalGoesToTheTeamTheRouteWasCountedAgainst()
    {
        var graph = Graph();
        var search = new NavSearch(graph);
        var route = new List<int>();
        Assert.True(search.Find(From, To, new NavQuery(Team.Red), route));

        var shared = new BotOccupancy();

        var switched = Walking(route, shared, Team.Red);
        var stayed = Walking(route, shared, Team.Blue);

        Assert.Equal(switched.Path.Count - 1, shared.EdgeCount(Team.Red));
        Assert.Equal(stayed.Path.Count - 1, shared.EdgeCount(Team.Blue));

        // The switched bot is now blue, and its route comes off RED.
        shared.Remove(switched);

        Assert.Equal(0, shared.EdgeCount(Team.Red));
        Assert.Equal(stayed.Path.Count - 1, shared.EdgeCount(Team.Blue));
    }

    /// <summary>
    /// A count that falls to nothing is deleted rather than left at zero.
    /// </summary>
    /// <remarks>
    /// So the map stays the size of what is actually being walked rather than
    /// growing to the size of every edge any bot has ever used on this map.
    /// </remarks>
    [Fact]
    public void ACountThatFallsToNothingIsDeleted()
    {
        var graph = Graph();
        var search = new NavSearch(graph);
        var route = new List<int>();
        Assert.True(search.Find(From, To, new NavQuery(Team.Red), route));

        var shared = new BotOccupancy();
        var state = Walking(route, shared, Team.Red);

        shared.Remove(state);

        Assert.Empty(shared.Counts(Team.Red)!);
    }

    /// <summary>
    /// A map change drops the counts and the flag together.
    /// </summary>
    /// <remarks>
    /// <b>Clearing the flag is the half that is easy to forget.</b> Dropping
    /// the maps alone leaves every bot believing its route is still counted, so
    /// the next removal declines to decrement and the route is added a
    /// <em>second</em> time on the next plan — a count of 2 for one bot, on a map
    /// where it may be the only thing walking.
    /// <para>
    /// And occupancy keys are node indices, which only mean anything to one
    /// build of the graph — counts from the previous map would sit there making
    /// arbitrary edges of the new one look busy.
    /// </para>
    /// </remarks>
    [Fact]
    public void AMapChangeDropsTheCountsAndTheFlagTogether()
    {
        var graph = Graph();
        var shared = new BotOccupancy();

        var bot = new ScriptedBot(graph, new NavQuery(Team.Red), BotSkill.ForTier(5), 0)
        {
            Occupancy = shared,
        };

        var search = new NavSearch(graph);
        var route = new List<int>();
        Assert.True(search.Find(From, To, new NavQuery(Team.Red), route));

        bot.Path.Path.AddRange(route);
        shared.Add(bot.Path, Team.Red);

        Assert.True(bot.Path.Occupied);
        Assert.True(shared.EdgeCount(Team.Red) > 0);

        shared.Reset();
        bot.SetMap(graph);

        Assert.False(bot.Path.Occupied);
        Assert.Equal(Team.Spectator, bot.Path.OccupiedTeam);
        Assert.Equal(0, shared.EdgeCount(Team.Red));

        // The state the flag protects: a fresh route counts once, not twice.
        bot.Path.Path.AddRange(route);
        shared.Add(bot.Path, Team.Red);

        foreach (var count in shared.Counts(Team.Red)!.Values)
            Assert.Equal(1, count);
    }

    /// <summary>
    /// Arriving takes the route off the counts.
    /// </summary>
    /// <remarks>
    /// <b>Off the counts BEFORE the route is freed</b>, because the edges can
    /// only be found through the path that is about to be cleared. This is the
    /// third of the three places <c>botPathFree</c> removes from — re-plan,
    /// arrival, death — and the one that is invisible to a test that never lets a
    /// bot get anywhere: an arrived bot leaves its whole route crowded for ever,
    /// so every team-mate is charged for a corridor nobody is in.
    /// </remarks>
    [Fact]
    public void ArrivingTakesTheRouteOffTheCounts()
    {
        var graph = Graph();
        var follower = new PathFollower(graph);
        var shared = new BotOccupancy();
        var query = new NavQuery(Team.Red);

        var state = new BotPathState();
        var (gx, gy) = BotSuppress.Centre(graph, To);
        var (sx, sy) = BotSuppress.Centre(graph, From);

        Assert.True(follower.SetGoal(state, gx, gy));
        Assert.True(follower.Plan(state, sx, sy, 0, in query, shared));
        Assert.True(shared.EdgeCount(Team.Red) > 0);

        // Standing on the goal, stopped. Arrival needs the bot STOPPED and not
        // merely close — GG2 keeps horizontal speed for ~10 ticks after the keys
        // come off, so the follower brakes inside the band rather than declaring
        // arrival on position.
        var self = CharacterState.Fresh(PlayerClass.Scout, Team.Red);
        self.X = state.GoalX;
        self.Y = state.GoalY;
        self.OnGround = true;
        self.Motion.HSpeed = 0;

        var weapon = default(WeaponState);

        for (var tick = 1; tick < 30 && !state.Arrived; tick++)
            follower.Step(state, in self, in weapon, tick, in query, shared);

        Assert.True(state.Arrived, "the bot never arrived, so nothing here is gated");
        Assert.Equal(0, shared.EdgeCount(Team.Red));
        Assert.False(state.Occupied);
    }

    /// <summary>
    /// A dead bot stops crowding its route.
    /// </summary>
    /// <remarks>
    /// It will come back somewhere else entirely, so the edges it was walking are
    /// not crowded any more — and a team-mate planning in the meantime should not
    /// be charged for them.
    /// </remarks>
    [Fact]
    public void ADeadBotStopsCrowdingItsRoute()
    {
        var graph = Graph();
        var shared = new BotOccupancy();

        var world = ScenarioWorld.Load(
            Map, 1, ReferenceTree.IncludedFiles, ReferenceTree.NavCacheDirectory);

        var slot = world.Place(PlayerClass.Scout, Team.Red, 3570, 325);

        var bot = new ScriptedBot(graph, new NavQuery(Team.Red), BotSkill.ForTier(5), 0)
        {
            Occupancy = shared,
        };

        var search = new NavSearch(graph);
        var route = new List<int>();
        Assert.True(search.Find(From, To, new NavQuery(Team.Red), route));

        bot.Path.Path.AddRange(route);
        shared.Add(bot.Path, Team.Red);
        Assert.True(shared.EdgeCount(Team.Red) > 0);

        world.State.Clear(slot);
        Assert.False(world.State.IsAlive(slot));

        bot.Poll(new WorldView(in world.State, world.Collision), slot, 1);

        Assert.Equal(0, shared.EdgeCount(Team.Red));
        Assert.False(bot.Path.Occupied);
    }
}
