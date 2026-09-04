using Gg2.Bots;
using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Simulation;
using Xunit.Abstractions;

namespace Gg2.Tests;

/// <summary>
/// The spot ban: what a bot does when it wants to stand somewhere it cannot walk
/// to.
/// </summary>
/// <remarks>
/// <b>A spot is chosen without checking reachability.</b>
/// <see cref="BotGoalSpot"/> ranks nodes by distance, height and sightline and
/// asks the graph nothing about whether a route exists — deliberately, because a
/// path search per candidate is a search per bot per objective tick. So a spot in
/// another component of the map presents as a goal that never plans, and
/// <see cref="ScriptedBot.SpotBanUntil"/> is the only place that can notice.
/// <para>
/// <b>This state is not reachable from any shipped map</b>, which is why the
/// branch stood ungated and said so in its own comment for four items: every one
/// of the 24 cached graphs is connected for the team that owns the ground.
/// The fixture here is a real graph with one node's edges removed — a genuine
/// two-component graph over real geometry, so the spot search sees exactly the
/// candidates it always saw and the route to them is the only thing that changed.
/// </para>
/// </remarks>
public sealed class SpotBanTests(ITestOutputHelper output)
{
    private const string Map = "koth_valley";

    /// <summary>
    /// How long a spot that never planned is banned for — <c>ScriptedBot</c>'s
    /// own literal 300, ten seconds.
    /// </summary>
    private const int SpotBan = 300;

    /// <summary>
    /// The same graph with every edge touching one node removed.
    /// </summary>
    /// <remarks>
    /// <b>Rebuilt through the real cache format and the real reader</b> rather
    /// than by constructing a <see cref="NavGraph"/> directly: the two derived
    /// indices are bets on the generator's output — edges sorted by from-node,
    /// nodes sorted by row then x — and a graph assembled behind the parser could
    /// satisfy neither while still looking right. Going through
    /// <see cref="NavCache.Parse"/> means this fixture is a graph the game could
    /// have written.
    /// <para>
    /// Isolating one node rather than cutting the map in half is the smallest
    /// two-component graph that exists, and the component that matters is the
    /// singleton: every candidate the spot search can rank is on the far side of
    /// it.
    /// </para>
    /// </remarks>
    private static NavGraph Isolate(NavGraph real, int node)
    {
        var nodes = new double[real.NodeCount][];

        for (var i = 0; i < real.NodeCount; i++)
        {
            var n = real.Nodes[i];
            nodes[i] = [n.Row, n.X0, n.X1, n.Flags, (double)n.Door, (double)n.Gate];
        }

        var edges = new List<double[]>(real.EdgeCount);

        foreach (var e in real.Edges)
        {
            if (e.From == node || e.To == node)
                continue;

            edges.Add(
                [e.From, e.To, (double)e.Type, e.Bucket, e.Ticks, e.Cost, (double)e.Gate, e.Takeoff, e.Rejump]);
        }

        var text = NavCacheTests.WriteCache(
            real.MaskWidth, real.MaskHeight, nodes, [.. edges], real.Version);

        return NavCache.Parse(text, Map, real.Area);
    }

    /// <summary>
    /// A koth_valley world with red holding its own point, and a defender on it.
    /// </summary>
    /// <remarks>
    /// The recipe <see cref="BotObjectiveTests"/> measured: a defending sniper
    /// standing on its own quiet point is the case where both the hold overlook
    /// and the class standoff want to move it off, so the goal comes back a spot.
    /// </remarks>
    private static (WorldState State, CollisionWorld World, SpawnPoints Spawns, double X, double Y) Held()
    {
        var level = LevelMap.FromPng(Map, ReferenceTree.ReadMap(Map));
        var world = new CollisionWorld(level);
        var state = default(WorldState);

        state.GameMode = GameModes.Of(level);
        state.TimeLimitMinutes = 15;
        state.RespawnTime = 0;
        state.TdmInvulnerabilityTicks = 0;

        for (var i = 0; i < TeamBalance.ClassCount; i++)
            state.ClassLimits[i] = 255;

        ControlPoints.Place(ref state, level, out var zones);
        world.SetCaptureZones(zones);
        Rounds.Start(ref state);

        state.ControlPointUnlock = 0;
        for (var i = 0; i < state.ControlPointCount; i++)
        {
            state.ControlPoints[i].Locked = false;
            state.ControlPoints[i].Owned = true;
            state.ControlPoints[i].Team = Team.Red;
        }

        state.AddPlayer(0);

        var spawns = new SpawnPoints(level);
        var zone = zones.Single(z => z.Point == 0);

        return (state, world, spawns, (zone.Left + zone.Right) / 2.0, (zone.Top + zone.Bottom) / 2.0);
    }

    private static int Put(
        ref WorldState state, SpawnPoints spawns, int slot, PlayerClass playerClass, double x, double y)
    {
        state.AddPlayer(slot);
        state.Spawn(spawns, slot, playerClass, Team.Red, spawnpointId: 0, group: 0);

        state.Characters[slot].X = x;
        state.Characters[slot].Y = y;
        state.Characters[slot].OnGround = true;
        state.Weapons[slot].X = x;
        state.Weapons[slot].Y = y;

        return slot;
    }

    /// <summary>
    /// The isolating rebuild really does produce two components, and changes
    /// nothing else.
    /// </summary>
    /// <remarks>
    /// <b>The premise every other test here rests on, and it is not free.</b>
    /// A rebuild that silently dropped or reordered nodes would give the spot
    /// search a different set of candidates, and "the spot moved" would then look
    /// exactly like "the ban fired". So the node list is asserted identical and
    /// only the edges touching the isolated node are asserted gone.
    /// </remarks>
    [Fact]
    public void TheRebuiltGraphIsTheRealOneMinusOneNodesEdges()
    {
        ReferenceTree.RequireNavCache();

        var real = NavCache.Load(ReferenceTree.NavCacheDirectory, Map, 1);
        var (state, world, spawns, px, py) = Held();
        var slot = Put(ref state, spawns, 1, PlayerClass.Sniper, px, py);

        var stood = real.NodeFromWorld(state.Characters[slot].X, state.Characters[slot].Y);
        Assert.True(stood >= 0, "the defender is not standing on a node, so nothing can be isolated");

        var cut = Isolate(real, stood);

        Assert.Equal(real.NodeCount, cut.NodeCount);
        Assert.Equal(real.MaskWidth, cut.MaskWidth);
        Assert.Equal(real.MaskHeight, cut.MaskHeight);

        for (var i = 0; i < real.NodeCount; i++)
            Assert.Equal(real.Nodes[i], cut.Nodes[i]);

        var touching = 0;
        foreach (var e in real.Edges)
        {
            if (e.From == stood || e.To == stood)
                touching++;
        }

        output.WriteLine($"node {stood} had {touching} edges of {real.EdgeCount}; {cut.EdgeCount} remain");

        Assert.True(touching > 0, "the node was already isolated, so this fixture gates nothing");
        Assert.Equal(real.EdgeCount - touching, cut.EdgeCount);
        Assert.Empty(cut.EdgesFrom(stood).ToArray());

        // And the node really is cut off both ways: nothing else reaches it either.
        foreach (var e in cut.Edges)
            Assert.True(e.To != stood, "an edge still lands on the isolated node");
    }

    /// <summary>
    /// A spot that never plans bans spots, and the bot falls back to the
    /// objective itself.
    /// </summary>
    /// <remarks>
    /// <b>The observable is WHERE the bot is sent, not the flag.</b> A spot is
    /// off the point by the class's standoff band; the fallback is the point
    /// itself. So deleting the ban leaves the bot asking for an unreachable perch
    /// for ever, and that shows up as a goal that never comes back to the thing it
    /// was supposed to be defending.
    /// <para>
    /// <c>HasGoal</c> is what separates "planning failed" from "arrived" —
    /// arrival clears the goal and frees the path together, so a bot that got
    /// there has neither. A test that watched <c>HasPath</c> alone would call an
    /// arrival a failure.
    /// </para>
    /// </remarks>
    [Fact]
    public void ASpotThatNeverPlansIsBannedAndTheBotFallsBackToTheObjective()
    {
        ReferenceTree.RequireNavCache();

        var real = NavCache.Load(ReferenceTree.NavCacheDirectory, Map, 1);
        var (state, world, spawns, px, py) = Held();
        var slot = Put(ref state, spawns, 1, PlayerClass.Sniper, px, py);

        var stood = real.NodeFromWorld(state.Characters[slot].X, state.Characters[slot].Y);
        var cut = Isolate(real, stood);

        var bot = new ScriptedBot(cut, new NavQuery(Team.Red), BotSkill.ForTier(5), 0)
        {
            Objective = new BotObjective(cut, spawns),
            Role = BotRole.Defend,
        };

        // The premise, from the objective layer itself: with spots allowed this
        // defender is sent to a perch, with them banned it is sent somewhere else,
        // and the two are far enough apart to tell from the outside.
        var objective = new BotObjective(cut, spawns);
        var view = new WorldView(in state, world);

        var perch = objective.Decide(view, slot, BotRole.Defend, spreadX: 0, allowSpot: true);
        var plain = objective.Decide(view, slot, BotRole.Defend, spreadX: 0, allowSpot: false);

        Assert.True(perch.IsSpot, "the defender was not offered a perch, so no spot can be banned");
        Assert.False(plain.IsSpot);

        Assert.True(objective.TryResolve(in perch, 0, out var sx, out var sy));
        Assert.True(objective.TryResolve(in plain, 0, out var fx, out var fy));

        var apart = Gml.PointDistance(sx, sy, fx, fy);
        output.WriteLine(
            $"perch resolves to ({sx:0},{sy:0}), the banned answer to ({fx:0},{fy:0}); {apart:0}px apart");

        Assert.True(
            apart > BotConstants.GoalRetargetDist,
            "the two goals are inside the retarget throttle, so the bot would never be re-issued one");

        var banned = 0;

        // Stop INSIDE the first ban window, and past the objective tick that
        // issues the replacement. The ban is a delay rather than a permanent stop —
        // see the lapse test below — so a loop that ran past it would watch the bot
        // pick a SECOND unreachable perch and measure that instead.
        for (var tick = 0; tick < SpotBan; tick++)
        {
            bot.Poll(view, slot, tick);

            if (bot.SpotBanUntil > 0 && banned == 0)
                banned = bot.SpotBanUntil;
        }

        output.WriteLine($"banned until {banned}, final goal ({bot.Path.GoalX:0},{bot.Path.GoalY:0})");

        // On the tick AFTER the goal was issued, not a whole objective period
        // later. The check sits above the period gate on purpose — "notice a spot
        // that never planned BEFORE deciding again, or the ban is always one
        // objective tick behind the goal that earned it" — and moving it below is
        // invisible to anything that only asserts a ban happened.
        Assert.Equal(1 + SpotBan, banned);

        // The behaviour the ban buys: the bot walks to the objective's own answer
        // rather than to a perch it can never reach.
        Assert.Equal(fx, bot.Path.GoalX);
        Assert.Equal(fy, bot.Path.GoalY);
    }

    /// <summary>
    /// The ban lapses and the bot tries again.
    /// </summary>
    /// <remarks>
    /// <b>It is a delay, not a permanent stop</b>, and the difference matters
    /// on a map where the graph is right and the bot is merely somewhere awkward:
    /// a bot that gave up on positions for good would spend the rest of the round
    /// standing on the thing it was supposed to be covering. Here the second perch
    /// is unreachable too — the node is still isolated — so the second ban is
    /// evidence that a second spot was asked for at all.
    /// </remarks>
    [Fact]
    public void TheBanLapsesAndTheBotAsksForAnotherSpot()
    {
        ReferenceTree.RequireNavCache();

        var real = NavCache.Load(ReferenceTree.NavCacheDirectory, Map, 1);
        var (state, world, spawns, px, py) = Held();
        var slot = Put(ref state, spawns, 1, PlayerClass.Sniper, px, py);

        var cut = Isolate(real, real.NodeFromWorld(state.Characters[slot].X, state.Characters[slot].Y));

        var bot = new ScriptedBot(cut, new NavQuery(Team.Red), BotSkill.ForTier(5), 0)
        {
            Objective = new BotObjective(cut, spawns),
            Role = BotRole.Defend,
        };

        var first = 0;

        for (var tick = 0; tick < SpotBan * 3; tick++)
        {
            bot.Poll(new WorldView(in state, world), slot, tick);

            if (first == 0 && bot.SpotBanUntil > 0)
                first = bot.SpotBanUntil;
        }

        output.WriteLine($"first ban until {first}, latest until {bot.SpotBanUntil}");

        Assert.True(first > 0);
        Assert.True(
            bot.SpotBanUntil > first,
            "the bot never asked for another spot, so the ban is permanent rather than a delay");
    }

    /// <summary>
    /// The ban is not re-armed once the replacement goal is in.
    /// </summary>
    /// <remarks>
    /// It fires on the goal's own <see cref="BotGoal.IsSpot"/>, so the
    /// objective's own point — which on this graph does not plan either, because
    /// the bot's node is isolated — must not extend it. A ban that renewed itself
    /// off any unplannable goal would never lapse, and a bot that lost its route
    /// for a moment would stop taking positions for the rest of the round.
    /// <para>
    /// <b>Measure past the objective tick that REPLACES the spot goal</b>, and
    /// that is the whole subtlety: between the perch being set and the next
    /// objective tick the goal is still a spot and still unplannable, so the ban
    /// legitimately advances every tick in that gap. What must not advance is the
    /// window after the replacement lands.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheBanIsNotReArmedOnceTheReplacementGoalIsIn()
    {
        ReferenceTree.RequireNavCache();

        var real = NavCache.Load(ReferenceTree.NavCacheDirectory, Map, 1);
        var (state, world, spawns, px, py) = Held();
        var slot = Put(ref state, spawns, 1, PlayerClass.Sniper, px, py);

        var cut = Isolate(real, real.NodeFromWorld(state.Characters[slot].X, state.Characters[slot].Y));

        var bot = new ScriptedBot(cut, new NavQuery(Team.Red), BotSkill.ForTier(5), 0)
        {
            Objective = new BotObjective(cut, spawns),
            Role = BotRole.Defend,
        };

        var view = new WorldView(in state, world);
        var settle = BotConstants.ObjectivePeriod * 2;

        for (var tick = 0; tick <= settle; tick++)
            bot.Poll(view, slot, tick);

        var settled = bot.SpotBanUntil;
        Assert.True(settled > 0, "no ban was ever armed");

        for (var tick = settle + 1; tick < SpotBan; tick++)
            bot.Poll(view, slot, tick);

        // The premise: the replacement does not plan either, so the only thing
        // keeping the ban from renewing is that it is not a spot.
        Assert.True(bot.Path.HasGoal, "the bot arrived, so there is no unplannable goal left to renew off");
        Assert.False(bot.Path.HasPath, "the replacement planned, so this cannot renew and the test is empty");

        output.WriteLine($"settled at {settled}, still {bot.SpotBanUntil} at tick {SpotBan - 1}");
        Assert.Equal(settled, bot.SpotBanUntil);
    }

    /// <summary>
    /// Arriving at a spot is not a spot that never planned.
    /// </summary>
    /// <remarks>
    /// <b><c>HasGoal</c> is what separates "planning failed" from "arrived"</b>
    /// — arrival clears the goal and frees the path together, so a bot that got
    /// there has neither, and a ban keyed on <c>!HasPath</c> alone would punish it
    /// for succeeding. Every later spot would then be refused for ten seconds
    /// after every arrival, which is most of a bot's life.
    /// <para>
    /// <b>The arrival is constructed rather than walked to.</b> <c>Poll</c>
    /// hands back keys and moves nothing — reaching this state honestly would mean
    /// running the simulation for as long as it takes a sniper to climb to its
    /// perch, and what is being gated is one clause, not the follower.
    /// </para>
    /// </remarks>
    [Fact]
    public void ArrivingAtASpotIsNotAFailureToPlan()
    {
        ReferenceTree.RequireNavCache();

        var real = NavCache.Load(ReferenceTree.NavCacheDirectory, Map, 1);
        var (state, world, spawns, px, py) = Held();
        var slot = Put(ref state, spawns, 1, PlayerClass.Sniper, px, py);

        var bot = new ScriptedBot(real, new NavQuery(Team.Red), BotSkill.ForTier(5), 0)
        {
            Objective = new BotObjective(real, spawns),
            Role = BotRole.Defend,
        };

        var view = new WorldView(in state, world);

        // A spot goal, planned to.
        for (var tick = 0; tick <= BotConstants.ObjectivePeriod; tick++)
            bot.Poll(view, slot, tick);

        Assert.True(bot.Path.HasGoal);
        Assert.True(bot.Path.HasPath, "the perch never planned, so this is the banned case rather than arrival");
        Assert.Equal(0, bot.SpotBanUntil);

        // What arrival leaves behind: no goal and no path.
        bot.Path.FreePath();
        bot.Path.HasGoal = false;

        bot.Poll(view, slot, BotConstants.ObjectivePeriod + 1);

        Assert.Equal(0, bot.SpotBanUntil);
    }

    /// <summary>
    /// A reachable spot is not banned.
    /// </summary>
    /// <remarks>
    /// The control that keeps the ban from being "never take a position". The
    /// identical world on the identical map with the graph left whole: the perch
    /// plans, so nothing is ever banned.
    /// </remarks>
    [Fact]
    public void AReachableSpotIsNeverBanned()
    {
        ReferenceTree.RequireNavCache();

        var real = NavCache.Load(ReferenceTree.NavCacheDirectory, Map, 1);
        var (state, world, spawns, px, py) = Held();
        var slot = Put(ref state, spawns, 1, PlayerClass.Sniper, px, py);

        var bot = new ScriptedBot(real, new NavQuery(Team.Red), BotSkill.ForTier(5), 0)
        {
            Objective = new BotObjective(real, spawns),
            Role = BotRole.Defend,
        };

        for (var tick = 0; tick < 400; tick++)
            bot.Poll(new WorldView(in state, world), slot, tick);

        output.WriteLine($"whole graph: goal ({bot.Path.GoalX:0},{bot.Path.GoalY:0}), path {bot.Path.HasPath}");

        Assert.Equal(0, bot.SpotBanUntil);
        Assert.True(
            Gml.PointDistance(bot.Path.GoalX, bot.Path.GoalY, px, py) > BotConstants.ArriveTol,
            "the defender never left the point, so the perch was never planned to");
    }
}
