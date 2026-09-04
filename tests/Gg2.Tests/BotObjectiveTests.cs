using Gg2.Bots;
using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Simulation;
using Xunit.Abstractions;

namespace Gg2.Tests;

/// <summary>
/// <c>botObjectiveUpdate</c> and the two helpers it composes — the role split and
/// the spot search.
/// </summary>
/// <remarks>
/// <b>These build a world with a real objective in it, which
/// <see cref="ScenarioWorld"/> deliberately does not.</b> That harness's whole
/// contract is "no gamemode", so it is the wrong instrument here and the mode
/// tests' pattern — a <c>CollisionWorld</c> and a <c>WorldState</c> built directly
/// — is the right one.
/// <para>
/// <b>The end-to-end test is the one that matters and the unit ones exist
/// because it is slow to bisect.</b> Every rule below is invisible from outside:
/// a bot with the wrong role still walks somewhere, a bot with no spread still
/// arrives, and a cascade that picks the wrong flag looks exactly like a routing
/// problem two thousand ticks later.
/// </para>
/// </remarks>
public sealed class BotObjectiveTests(ITestOutputHelper output)
{
    private const string CtfMap = "ctf_truefort";

    private static LevelMap Level(string name) =>
        LevelMap.FromPng(name, ReferenceTree.ReadMap(name));

    /// <summary>A CTF world with both briefcases on their bases.</summary>
    private static (WorldState State, CollisionWorld World, SpawnPoints Spawns) Ctf()
    {
        var level = Level(CtfMap);
        var world = new CollisionWorld(level);
        var state = default(WorldState);

        state.GameMode = GameModes.Of(level);
        state.TimeLimitMinutes = 15;
        state.RespawnTime = 0;
        state.TdmInvulnerabilityTicks = 0;

        for (var i = 0; i < TeamBalance.ClassCount; i++)
            state.ClassLimits[i] = 255;

        Intel.Place(ref state, level);
        Rounds.Start(ref state);

        state.AddPlayer(0);

        return (state, world, new SpawnPoints(level));
    }

    private static int Put(
        ref WorldState state, SpawnPoints spawns, int slot,
        PlayerClass playerClass, Team team, double x, double y)
    {
        state.AddPlayer(slot);
        state.Spawn(spawns, slot, playerClass, team, spawnpointId: 0, group: 0);

        state.Characters[slot].X = x;
        state.Characters[slot].Y = y;
        state.Weapons[slot].X = x;
        state.Weapons[slot].Y = y;

        return slot;
    }

    private static NavGraph Graph(string map = CtfMap)
    {
        ReferenceTree.RequireNavCache();
        return NavCache.Load(ReferenceTree.NavCacheDirectory, map, 1);
    }

    private static BotGoal Decide(
        in WorldState state, CollisionWorld world, SpawnPoints spawns, NavGraph graph,
        int slot, BotRole role = BotRole.Attack, double spread = 0)
    {
        var objective = new BotObjective(graph, spawns);
        return objective.Decide(new WorldView(in state, world), slot, role, spread, allowSpot: true);
    }

    // ------------------------------------------------------------------
    // The role split
    // ------------------------------------------------------------------

    /// <summary>
    /// One bot in every N defends, and N comes from the class.
    /// </summary>
    /// <remarks>
    /// <b>The class term is a PERIOD, not a veto.</b> "Engineers defend, scouts
    /// attack" breaks at both ends: a team of nine engineers would have nobody
    /// attacking and a team of nine scouts nobody defending, and the population
    /// picker rolls a class with <c>irandom(8)</c>, so it can produce either.
    /// <para>
    /// <b>The first bot on a team is an attacker every time</b>, which is the
    /// right default when there is only one — and it is what the <c>+1</c> in the
    /// modulo buys. A rule keyed on <c>place % every == 0</c> instead makes the
    /// lone bot on a team a defender with nothing to defend against.
    /// </remarks>
    [Fact]
    public void OneBotInEveryNDefendsAndNComesFromTheClass()
    {
        // Soldier: the default period of 3.
        Assert.Equal(BotRole.Attack, BotRoles.Assign(PlayerClass.Soldier, 0));
        Assert.Equal(BotRole.Attack, BotRoles.Assign(PlayerClass.Soldier, 1));
        Assert.Equal(BotRole.Defend, BotRoles.Assign(PlayerClass.Soldier, 2));
        Assert.Equal(BotRole.Attack, BotRoles.Assign(PlayerClass.Soldier, 3));

        // Keen: an engineer is a fixed emplacement, so every second one defends.
        Assert.Equal(BotRole.Attack, BotRoles.Assign(PlayerClass.Engineer, 0));
        Assert.Equal(BotRole.Defend, BotRoles.Assign(PlayerClass.Engineer, 1));

        // Averse: a scout at capStrength 2 is the fastest capper in the game.
        for (var place = 0; place < 5; place++)
            Assert.Equal(BotRole.Attack, BotRoles.Assign(PlayerClass.Scout, place));

        Assert.Equal(BotRole.Defend, BotRoles.Assign(PlayerClass.Scout, 5));

        // The control that makes the whole design bite: an all-one-class team
        // still splits. A veto rule leaves one of these two with nobody.
        var engineers = 0;
        var scouts = 0;

        for (var place = 0; place < 12; place++)
        {
            if (BotRoles.Assign(PlayerClass.Engineer, place) == BotRole.Defend)
                engineers++;

            if (BotRoles.Assign(PlayerClass.Scout, place) == BotRole.Defend)
                scouts++;
        }

        Assert.Equal(6, engineers);
        Assert.Equal(2, scouts);
    }

    /// <summary>
    /// The count is over the bot's own lean group, not over the whole team.
    /// </summary>
    /// <remarks>
    /// <b>Counting the whole team makes the period meaningless in a mixed
    /// one.</b> Two engineers separated by four scouts sit at whole-team positions
    /// 0 and 5, and neither lands on a multiple of 2 — so a team with two engineers
    /// in it has no defenders at all. Sharing a period is what makes a lean group:
    /// engineer and heavy count together as one defensive pool, scout and spy as
    /// one offensive pool, everyone else as the third.
    /// </remarks>
    [Fact]
    public void ThePlaceIsCountedWithinTheLeanGroup()
    {
        // Engineer, then four scouts, then a second engineer. The second engineer
        // is at whole-team position 5 and lean-group position 1.
        ReadOnlySpan<PlayerClass> earlier =
        [
            PlayerClass.Engineer,
            PlayerClass.Scout, PlayerClass.Scout, PlayerClass.Scout, PlayerClass.Scout,
        ];

        Assert.Equal(BotRole.Defend, BotRoles.Assign(PlayerClass.Engineer, earlier));

        // The control: at whole-team position 5, (5 + 1) % 2 == 0 would also say
        // Defend, so the fixture has to separate the two readings. A heavy shares
        // the engineer's period and a spy shares the scout's, so moving one scout
        // to a heavy changes the lean count and not the team count.
        ReadOnlySpan<PlayerClass> withHeavy =
        [
            PlayerClass.Engineer,
            PlayerClass.Heavy, PlayerClass.Scout, PlayerClass.Scout, PlayerClass.Scout,
        ];

        Assert.Equal(BotRole.Attack, BotRoles.Assign(PlayerClass.Engineer, withHeavy));

        // And the other direction: a spy counts with the scouts, so it is invisible
        // to an engineer's lean group.
        ReadOnlySpan<PlayerClass> withSpy =
        [
            PlayerClass.Engineer,
            PlayerClass.Spy, PlayerClass.Spy, PlayerClass.Spy, PlayerClass.Spy,
        ];

        Assert.Equal(BotRole.Defend, BotRoles.Assign(PlayerClass.Engineer, withSpy));
    }

    /// <summary>
    /// Bots get different spreads, and they stay inside the maximum.
    /// </summary>
    /// <remarks>
    /// <b>"Inside the maximum" alone is what let this silently do nothing in the
    /// reference for a whole build.</b> The assignment was deleted along with an
    /// unrelated route seed and its comment left behind, so every bot's spread sat
    /// at its Create-event 0 — and the test asserted only
    /// <c>abs(spread) &lt;= BOT_SPREAD_MAX</c>, which 0 satisfies. So this asserts
    /// that the offsets actually <em>differ</em> and that both signs occur.
    /// </remarks>
    [Fact]
    public void EveryBotGetsItsOwnSpreadAndTheyAreNotAllZero()
    {
        var seen = new HashSet<double>();
        var negative = 0;
        var positive = 0;

        for (var slot = 1; slot <= 12; slot++)
        {
            var spread = BotRoles.SpreadX(slot);

            Assert.True(Math.Abs(spread) <= BotConstants.SpreadMax, $"slot {slot} spread {spread}");
            seen.Add(spread);

            if (spread < 0)
                negative++;
            else if (spread > 0)
                positive++;
        }

        Assert.True(seen.Count >= 8, $"only {seen.Count} distinct spreads across 12 bots");
        Assert.True(negative > 0 && positive > 0, "the spread never changes side");
    }

    // ------------------------------------------------------------------
    // The mode cascade
    // ------------------------------------------------------------------

    /// <summary>
    /// An attacker heads for the enemy flag and a defender for its own.
    /// </summary>
    /// <remarks>
    /// The whole reason the role split exists: before it, every bot in every mode
    /// ran at the enemy objective in single file and nothing was ever behind them.
    /// </remarks>
    [Fact]
    public void AnAttackerFetchesTheEnemyFlagAndADefenderGuardsItsOwn()
    {
        var (state, world, spawns) = Ctf();
        var graph = Graph();
        var slot = Put(ref state, spawns, 1, PlayerClass.Soldier, Team.Red, 1000, 800);

        var attack = Decide(in state, world, spawns, graph, slot, BotRole.Attack);
        var defend = Decide(in state, world, spawns, graph, slot, BotRole.Defend);

        Assert.True(attack.Found);
        Assert.True(defend.Found);

        // The enemy flag has to be TOUCHED — a capture is a collision — so the
        // spread is off entirely and the goal is the flag itself.
        Assert.Equal(state.BlueIntel.X, attack.X);
        Assert.True(attack.MustTouch);

        // A defender does NOT stand on what it guards, and BOT_DEFEND_RADIUS is
        // added even at a spread of zero: `if(spreadX >= 0) spreadX += ...`. So the
        // claim here is "beside its own flag", not "on it" — the first draft of this
        // test asserted equality and failed by exactly 220px.
        Assert.False(defend.MustTouch);
        Assert.Equal(state.RedIntel.X + BotConstants.DefendRadius, defend.X);

        // ...and the two really are different flags, which equality would have
        // proved for free and a radius test does not.
        Assert.True(
            Math.Abs(defend.X - state.RedIntel.X) < Math.Abs(defend.X - state.BlueIntel.X),
            "the defender was sent nearer the enemy flag than its own");
    }

    /// <summary>
    /// A carrier goes home, whatever its role says.
    /// </summary>
    /// <remarks>
    /// Roles do not apply here: there is exactly one thing to do with an enemy
    /// intel and it is not "guard something else while holding it". A capture is
    /// a collision with the base, exactly like the grab at the other end of the
    /// run, so it is the second goal that has to be walked into rather than covered.
    /// </remarks>
    [Fact]
    public void ACarrierRunsForItsOwnBaseWhicheverRoleItHas()
    {
        var (state, world, spawns) = Ctf();
        var graph = Graph();
        var slot = Put(ref state, spawns, 1, PlayerClass.Soldier, Team.Red, 1000, 800);

        state.Characters[slot].CarryingIntel = true;
        state.BlueIntel.OnField = false;

        foreach (var role in new[] { BotRole.Attack, BotRole.Defend })
        {
            var goal = Decide(in state, world, spawns, graph, slot, role);

            Assert.True(goal.Found);
            Assert.Equal(state.RedIntel.HomeX, goal.X);
            Assert.True(goal.MustTouch);
        }
    }

    /// <summary>
    /// A stolen flag is chased by every role, not only by defenders.
    /// </summary>
    /// <remarks>
    /// <b>Our own flag being carried outranks fetching theirs, and it is tested
    /// first for that reason.</b> Before this only a defender chased a thief — so
    /// on a team that happened to roll none (the role count runs down the roster,
    /// and a short team can land none) an enemy could stand in the open holding the
    /// flag with nobody on the map interested in them. Reported from play as "bots
    /// do not pursue the enemy holding the flag — no effort to kill me while I
    /// stand at 2676,582".
    /// <para>
    /// An attacker halfway to the enemy base is still the closest thing our flag has
    /// to a defender, and a capture ends the round whatever else that attacker was
    /// about to achieve.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryRoleChasesTheEnemyCarryingOurFlag()
    {
        var (state, world, spawns) = Ctf();
        var graph = Graph();

        var mine = Put(ref state, spawns, 1, PlayerClass.Soldier, Team.Red, 1000, 800);
        var thief = Put(ref state, spawns, 2, PlayerClass.Scout, Team.Blue, 2600, 580);

        state.Characters[thief].CarryingIntel = true;
        state.RedIntel.OnField = false;

        foreach (var role in new[] { BotRole.Attack, BotRole.Defend })
        {
            var goal = Decide(in state, world, spawns, graph, mine, role);

            Assert.True(goal.Found, $"{role} had no goal while its own flag was stolen");
            Assert.Equal(state.Characters[thief].X, goal.X);
            Assert.Equal(state.Characters[thief].Y, goal.Y);

            // A moving target takes neither a standoff band nor a spread: the
            // first is a bot orbiting a running player and the second is a
            // deliberate miss of the one thing worth intercepting.
            Assert.True(goal.MustTouch);
            Assert.False(goal.IsSpot);
        }

        // The control: with the flag back on its base the same attacker goes for
        // the enemy's instead. Without it this passes on any implementation that
        // always chases whoever is nearest.
        state.RedIntel.OnField = true;

        var normal = Decide(in state, world, spawns, graph, mine, BotRole.Attack);
        Assert.Equal(state.BlueIntel.X, normal.X);
    }

    /// <summary>
    /// An attacker whose team-mate got the grab escorts them home.
    /// </summary>
    /// <remarks>
    /// The flag this bot was going to fetch is already in a team-mate's hands, so
    /// there is nothing left at the far end to walk to. Measured in the reference
    /// on ctf_truefort: three of four blue bots stood at the empty red stand for the
    /// whole time their team-mate was running the flag back.
    /// </remarks>
    [Fact]
    public void AnAttackerEscortsTheTeamMateWhoGotTheGrab()
    {
        var (state, world, spawns) = Ctf();
        var graph = Graph();

        var mine = Put(ref state, spawns, 1, PlayerClass.Soldier, Team.Red, 1000, 800);
        var mate = Put(ref state, spawns, 2, PlayerClass.Scout, Team.Red, 3200, 600);

        state.Characters[mate].CarryingIntel = true;
        state.BlueIntel.OnField = false;

        var goal = Decide(in state, world, spawns, graph, mine, BotRole.Attack);

        Assert.True(goal.Found);
        Assert.Equal(state.Characters[mate].X, goal.X);
    }

    /// <summary>
    /// A control point's goal is the middle of its capture zone.
    /// </summary>
    /// <remarks>
    /// <b>The middle, not the origin.</b> A capture zone's origin is its top-left
    /// corner, so aiming there asks the follower to stop on the exact boundary of
    /// the thing it is trying to stand in — and arrival is only accurate to
    /// <c>BOT_ARRIVE_TOL</c>. Measured in the reference on koth_valley: the bot
    /// climbed the whole four-jump chain, stopped 3px short of the zone's left edge,
    /// and captured nothing. The zone is 125px wide; its centre is not a near miss.
    /// </remarks>
    [Fact]
    public void AControlPointsGoalIsTheMiddleOfItsZone()
    {
        var level = Level("koth_valley");
        var world = new CollisionWorld(level);
        var state = default(WorldState);

        state.GameMode = GameModes.Of(level);
        state.TimeLimitMinutes = 15;

        ControlPoints.Place(ref state, level, out var zones);
        world.SetCaptureZones(zones);
        Rounds.Start(ref state);

        state.AddPlayer(0);
        var spawns = new SpawnPoints(level);
        var slot = Put(ref state, spawns, 1, PlayerClass.Soldier, Team.Red, 800, 580);

        var graph = Graph("koth_valley");
        var goal = Decide(in state, world, spawns, graph, slot);

        Assert.True(goal.Found);

        var zone = zones.Single(z => z.Point == 0);
        Assert.Equal((zone.Left + zone.Right) / 2.0, goal.X);
        Assert.Equal((zone.Top + zone.Bottom) / 2.0, goal.Y);

        // And it is not the origin. Without this the test passes on a port that
        // aims at the corner, since the corner is also "in the zone" by any loose
        // reading.
        Assert.NotEqual(zone.Left, goal.X);
        Assert.True(goal.X - zone.Left > 40, "the goal is within 40px of the zone's left edge");
    }

    /// <summary>
    /// A locked point is still walked to.
    /// </summary>
    /// <remarks>
    /// Every point is locked for the first 900 ticks of a KotH round and 1,800 of
    /// an Arena one, during which the nearest-unlocked-point loop finds nothing and
    /// would leave the bot with <b>no goal at all</b> — standing frozen at spawn
    /// while a human is already walking out to be on the point the moment it opens.
    /// </remarks>
    [Fact]
    public void ALockedPointIsStillWalkedTo()
    {
        var level = Level("koth_valley");
        var world = new CollisionWorld(level);
        var state = default(WorldState);

        state.GameMode = GameModes.Of(level);
        state.TimeLimitMinutes = 15;

        ControlPoints.Place(ref state, level, out var zones);
        world.SetCaptureZones(zones);
        Rounds.Start(ref state);

        for (var i = 0; i < state.ControlPointCount; i++)
            state.ControlPoints[i].Locked = true;

        state.AddPlayer(0);
        var spawns = new SpawnPoints(level);
        var slot = Put(ref state, spawns, 1, PlayerClass.Soldier, Team.Red, 800, 580);

        var goal = Decide(in state, world, spawns, Graph("koth_valley"), slot);

        Assert.True(goal.Found, "a bot had no goal at all while the hill was locked");
    }

    /// <summary>
    /// An enemy on our point cancels every "stand somewhere better" behaviour.
    /// </summary>
    /// <remarks>
    /// A control point is captured by <b>bodies inside the zone</b> and by nothing
    /// else, so the moment an enemy is standing in it, an overlook 250px up the hill
    /// and a push 400px out toward their spawn are the same mistake: the bot is
    /// looking at the point it is losing. Reported from play as "if the enemy is
    /// capturing the point, bots should return to it to fight / contest".
    /// <para>
    /// <b>Presence, not the capture meter.</b> Presence is recomputed every step
    /// from the characters in the zone, so it is true on the frame an enemy steps
    /// inside; the meter takes a second to become non-zero, and by the time it reads
    /// as a capture the point is a third gone.
    /// </para>
    /// </remarks>
    [Fact]
    public void AnEnemyOnOurPointPullsADefenderBackOntoIt()
    {
        var level = Level("koth_valley");
        var world = new CollisionWorld(level);
        var state = default(WorldState);

        state.GameMode = GameModes.Of(level);
        state.TimeLimitMinutes = 15;

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
        var centreX = (zone.Left + zone.Right) / 2.0;
        var centreY = (zone.Top + zone.Bottom) / 2.0;

        // A defending SNIPER standing on its own held point, so both the hold
        // overlook and the class standoff want to move it away.
        var slot = Put(ref state, spawns, 1, PlayerClass.Sniper, Team.Red, centreX, centreY);
        var graph = Graph("koth_valley");

        var quiet = Decide(in state, world, spawns, graph, slot, BotRole.Defend);

        Assert.True(quiet.Found);
        Assert.True(quiet.IsSpot, "a defender on its own quiet point did not take a position off it");

        state.ControlPoints[0].BluePresence = 2;

        var underAttack = Decide(in state, world, spawns, graph, slot, BotRole.Defend);

        Assert.True(underAttack.Found);
        Assert.False(underAttack.IsSpot, "a defender kept its perch while the point was being taken");
        Assert.Equal(centreX, underAttack.X);

        // And the spread comes off too. A capture zone is 125px wide here, the
        // spread is up to 96 either way and a defender adds another 220 on top —
        // measured live in the reference, a defending sniper sent to contest its own
        // point stood at 2544 while the zone ended at 2393, contesting nothing.
        var spread = Decide(in state, world, spawns, graph, slot, BotRole.Defend, spread: 90);
        Assert.Equal(centreX, spread.X);
    }

    /// <summary>
    /// An attacker's generator goal is a firing position, not the generator.
    /// </summary>
    /// <remarks>
    /// The objective here is to <em>destroy</em> a generator, not to touch one, and a
    /// generator is shot from wherever a bot has line of sight to it. At 2,100 hp
    /// that is a sustained-fire job from range, and standing on it was never the goal.
    /// A defender wants to be <em>near</em> its own rather than lined up on it, so
    /// only the attacker's goal is a spot.
    /// </remarks>
    [Fact]
    public void AnAttackersGeneratorGoalIsAFiringPosition()
    {
        var level = Level("gen_destroy");
        var world = new CollisionWorld(level);
        var state = default(WorldState);

        state.GameMode = GameModes.Of(level);
        state.TimeLimitMinutes = 15;

        Generators.Place(ref state, level);
        Rounds.Start(ref state);

        state.AddPlayer(0);
        var spawns = new SpawnPoints(level);
        var graph = Graph("gen_destroy");
        var slot = Put(ref state, spawns, 1, PlayerClass.Soldier, Team.Red, 2358, 235);

        var attack = Decide(in state, world, spawns, graph, slot, BotRole.Attack);
        var defend = Decide(in state, world, spawns, graph, slot, BotRole.Defend);

        Assert.True(attack.Found);
        Assert.True(attack.IsSpot, "an attacker was sent at the generator rather than to a firing position");

        var box = Generators.BodyBox(in state.BlueGenerator);
        var toTarget = Gml.PointDistance(
            attack.X, attack.Y, (box.Left + box.Right) / 2.0, (box.Top + box.Bottom) / 2.0);

        var band = BotTargeting.ClassRange(PlayerClass.Soldier) * BotConstants.SpotBand;
        var floor = Math.Max(BotTargeting.ClassMinBand(PlayerClass.Soldier), 1);

        Assert.InRange(toTarget, floor, band);
        output.WriteLine($"firing position {attack.X:0},{attack.Y:0} at {toTarget:0}px of a {floor}-{band:0} band");

        // The defender's own generator, and no firing position — it wants to be
        // near it, not lined up on it.
        Assert.True(defend.Found);
        Assert.False(defend.IsSpot);

        // Beside its own generator, not on it — BOT_DEFEND_RADIUS again, added
        // at a spread of zero.
        var red = Generators.BodyBox(in state.RedGenerator);
        Assert.Equal(((red.Left + red.Right) / 2.0) + BotConstants.DefendRadius, defend.X);
    }

    /// <summary>
    /// Team Deathmatch gives no goal at all.
    /// </summary>
    /// <remarks>
    /// It has no landmark: <c>basicRoomSetup</c>'s cascade ends there precisely
    /// because there is nothing on the map to head for, so bots keep fighting
    /// whatever wanders into range. This is also the property that keeps
    /// <see cref="ScenarioWorld"/> inert — a scenario places no objective, and a
    /// cascade that invented one would retask every scenario's bot mid-run.
    /// </remarks>
    [Fact]
    public void TeamDeathmatchGivesNoGoal()
    {
        var level = Level("tdm_mantic");
        var world = new CollisionWorld(level);
        var state = default(WorldState);

        state.GameMode = GameModes.Of(level);
        state.TimeLimitMinutes = 15;

        Intel.Place(ref state, level);
        Generators.Place(ref state, level);
        ControlPoints.Place(ref state, level, out var zones);
        world.SetCaptureZones(zones);
        Rounds.Start(ref state);

        state.AddPlayer(0);
        var spawns = new SpawnPoints(level);
        var slot = Put(ref state, spawns, 1, PlayerClass.Soldier, Team.Red, 700, 500);

        Assert.Equal(GameMode.TeamDeathmatch, state.GameMode);

        var goal = Decide(in state, world, spawns, Graph("tdm_mantic"), slot);

        Assert.False(goal.Found, "a Team Deathmatch map produced an objective out of nothing");
    }

    // ------------------------------------------------------------------
    // The spot search
    // ------------------------------------------------------------------

    /// <summary>
    /// A spot lands inside the band, prefers height when asked, and honours the
    /// sightline sense.
    /// </summary>
    /// <remarks>
    /// <b><c>Avoid</c> is the same test with the answer inverted</b>, and it is
    /// the spy's flank: near enough to the objective to walk in from, out of sight
    /// of whoever is guarding it. A port that treated it as "do not care" would give
    /// the spy the same position as everybody else and nothing would look wrong.
    /// </remarks>
    [Fact]
    public void ASpotIsInTheBandAndHonoursTheSightlineItWasAskedFor()
    {
        var level = Level(CtfMap);
        var world = new CollisionWorld(level);
        var graph = Graph();
        var spot = new BotGoalSpot(graph);
        var filter = CollisionFilter.ForBullet();

        // The measured open stretch on ctf_truefort — 1,000px of clear line.
        const double TargetX = 380;
        const double TargetY = 268;

        var need = spot.Find(3570, 325, TargetX, TargetY, 200, 600, BotLos.Need, 0, world, filter);
        var avoid = spot.Find(3570, 325, TargetX, TargetY, 200, 600, BotLos.Avoid, 0, world, filter);

        Assert.True(need.Node >= 0, "no node with a clear line in a 200-600px band");
        Assert.True(avoid.Node >= 0, "no node with a blocked line in a 200-600px band");

        Assert.InRange(Gml.PointDistance(need.X, need.Y, TargetX, TargetY), 200, 600);
        Assert.InRange(Gml.PointDistance(avoid.X, avoid.Y, TargetX, TargetY), 200, 600);

        // The two senses really do answer differently about their own node.
        Assert.False(world.CollisionLineBlocksBullets(need.X, need.Y, TargetX, TargetY, filter));
        Assert.True(world.CollisionLineBlocksBullets(avoid.X, avoid.Y, TargetX, TargetY, filter));

        // Height, when asked for. It is the whole model of "a good position" —
        // no sightline graph, no cover map — so a bonus that did nothing would leave
        // a defending sniper standing at the same level as the thing it is watching.
        //
        // The anchor is a DIFFERENT point from the sightline one above and was
        // found by sweeping every node rather than chosen: (380, 268) is already
        // near the top of ctf_truefort, so no node in a 200-600px band with a clear
        // line to it is any higher and the bonus correctly changes nothing. Only 10
        // of the map's 400-odd nodes make the bonus move the answer at all. The one
        // used here is node 214's stand position, where it buys 102px of rise.
        const double LowX = 1194;
        const double LowY = 493;

        var flat = spot.Find(LowX, LowY, LowX, LowY, 200, 600, BotLos.Need, 0, world, filter);
        var high = spot.Find(
            LowX, LowY, LowX, LowY, 200, 600, BotLos.Need, BotConstants.SpotHighSniper, world, filter);

        Assert.True(flat.Node >= 0 && high.Node >= 0);
        Assert.True(high.Y < flat.Y, $"the height bonus chose no higher ground: {high.Y} against {flat.Y}");

        // And the miss publishes the anchor rather than something stale, so a
        // caller that ignores the -1 gets a usable point.
        var impossible = spot.Find(3570, 325, TargetX, TargetY, 90000, 90001, BotLos.Need, 0, world, filter);

        Assert.Equal(-1, impossible.Node);
        Assert.Equal(TargetX, impossible.X);
        Assert.Equal(TargetY, impossible.Y);
    }

    /// <summary>
    /// The sightline budget stops a hopeless search.
    /// </summary>
    /// <remarks>
    /// <c>BOT_SPOT_TRIES</c> is what makes the worst case a fixed cost rather than
    /// one collision-line per node, and <b>it is invisible on any search that
    /// succeeds</b> — a bounded search and an unbounded one return the same node and
    /// pay the same number of tests, because both stop at the first that passes. A
    /// first draft asserted <c>Tries &lt;= BOT_SPOT_TRIES</c> on a search that found
    /// something, and deleting the bound left it green.
    /// <para>
    /// So the fixture is a search that <b>cannot</b> succeed: node 35's stand
    /// position on ctf_truefort is under an overhang, and no node in a 150-4000px
    /// band has a clear line to it. Found by sweeping every node — 12 of them do
    /// this, and none of the obvious open positions does.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheSightlineBudgetStopsAHopelessSearch()
    {
        var level = Level(CtfMap);
        var world = new CollisionWorld(level);
        var spot = new BotGoalSpot(Graph());

        // Node 35's own stand position, not a round number near it: the anchor
        // has to be somewhere nothing can see, and moving it a few pixels lands it
        // back in the open.
        var result = spot.Find(2202, 343, 2202, 343, 150, 4000, BotLos.Need, 0, world, CollisionFilter.ForBullet());

        Assert.Equal(-1, result.Node);
        Assert.Equal(BotConstants.SpotTries, spot.Tries);

        // The control: from a position that CAN see things, the same band answers on
        // far fewer tests — so the number above is the bound biting and not the map
        // happening to have twelve candidates.
        var open = spot.Find(3570, 325, 380, 268, 200, 600, BotLos.Need, 0, world, CollisionFilter.ForBullet());

        Assert.True(open.Node >= 0);
        Assert.True(spot.Tries < BotConstants.SpotTries, $"the open search also paid {spot.Tries} tests");
    }

    // ------------------------------------------------------------------
    // The cadence
    // ------------------------------------------------------------------

    /// <summary>
    /// An unchanged objective is not reissued.
    /// </summary>
    /// <remarks>
    /// <b>Reissuing a goal forces an immediate re-plan</b> — it clears the
    /// follower's re-plan deadline outright — so an objective layer that set the
    /// same destination every 30 ticks would fight the very timer the follower was
    /// built around. Hence the <c>BOT_GOAL_RETARGET_DIST</c> test rather than an
    /// unconditional set.
    /// <para>
    /// <b>The observable is the deadline, not the arrival.</b> Measured on the
    /// end-to-end leg below, deleting the throttle costs 41 re-plans → 58 and 10
    /// off-route events → 19, and the bot <em>still arrives</em> — slightly sooner,
    /// in fact (1,780 ticks → 1,693). So a test that only watches whether the bot
    /// gets there passes with the throttle gone, which is exactly what the first
    /// draft of this file did.
    /// </para>
    /// <para>
    /// A moving goal — a flag carrier — trips the 48px test naturally, which is the
    /// case it was written for and the second half of this test.
    /// </para>
    /// </remarks>
    [Fact]
    public void AnUnchangedObjectiveDoesNotForceAReplan()
    {
        var (state, world, spawns) = Ctf();
        var graph = Graph();

        // A HEAVY, not a soldier. This test measures the objective cadence's
        // contribution to the re-plan count, and a soldier's routes now contain
        // rocket-jump arcs — which are blacklisted and re-planned around whenever
        // the launcher is not loaded, adding churn that has nothing to do with the
        // throttle under test. Measured: with a soldier the STILL window re-planned
        // seven times against the moving window's five, which inverts the very
        // comparison this makes. A heavy has neither special arc.
        var slot = Put(ref state, spawns, 1, PlayerClass.Heavy, Team.Red, 384, 864);

        var bot = new ScriptedBot(graph, new NavQuery(Team.Red), BotSkill.ForTier(5), stagger: 1)
        {
            Objective = new BotObjective(graph, spawns),
            Role = BotRole.Attack,
            AliveLast = true,
        };

        var events = default(TickEvents);
        Span<PlayerInput> inputs = stackalloc PlayerInput[WorldState.MaxSlots];

        // Long enough to have crossed several objective ticks and planned a route.
        for (var tick = 0; tick < 200; tick++)
        {
            inputs.Clear();
            inputs[slot] = bot.Poll(new WorldView(in state, world), slot, tick);
            MatchSimulation.Tick(world, ref state, inputs[..(slot + 1)], ref events, spawns);
        }

        Assert.True(bot.Path.HasPath, "the bot never planned a route at all");
        Assert.True(bot.Path.ReplanAt > 200, "the follower has no re-plan deadline to protect");

        var goalX = bot.Path.GoalX;

        // Keep STEPPING rather than polling in isolation. A Poll with no tick
        // behind it leaves the character where it was, and the follower's own
        // plan/fail bookkeeping then reads a world that never moved — which is a
        // different experiment from the one this is about. A first draft did that
        // and the re-plan it was looking for never happened.
        var replansBefore = bot.Path.Replans;

        for (var tick = 200; tick < 200 + (BotConstants.ObjectivePeriod * 4); tick++)
        {
            inputs.Clear();
            inputs[slot] = bot.Poll(new WorldView(in state, world), slot, tick);
            MatchSimulation.Tick(world, ref state, inputs[..(slot + 1)], ref events, spawns);
        }

        var quiet = bot.Path.Replans - replansBefore;

        // The flag has not moved, so the goal has not been reissued at all — the
        // objective layer contributed nothing to that re-plan count.
        Assert.Equal(goalX, bot.Path.GoalX);

        // Now move it past BOT_GOAL_RETARGET_DIST and run the same window. Each
        // of the four objective ticks now reissues, and each reissue forces a plan.
        replansBefore = bot.Path.Replans;
        var moved = 0;

        for (var tick = 200 + (BotConstants.ObjectivePeriod * 4);
             tick < 200 + (BotConstants.ObjectivePeriod * 8);
             tick++)
        {
            // Walk the flag away from the bot every tick, so every objective tick
            // sees a destination that has moved further than the throttle allows.
            state.BlueIntel.X -= 8;

            inputs.Clear();
            inputs[slot] = bot.Poll(new WorldView(in state, world), slot, tick);
            MatchSimulation.Tick(world, ref state, inputs[..(slot + 1)], ref events, spawns);

            if (!Gml.Equal(bot.Path.GoalX, goalX))
                moved++;
        }

        var chasing = bot.Path.Replans - replansBefore;

        output.WriteLine($"re-plans over four objective periods: {quiet} still, {chasing} chasing");

        Assert.True(moved > 0, "the goal never followed the moving flag");
        Assert.True(
            chasing > quiet,
            $"a moving objective re-planned no more than a still one: {chasing} against {quiet}");
    }

    /// <summary>
    /// A spread that steps off a ledge is refused.
    /// </summary>
    /// <remarks>
    /// <b>The objective's own node is resolved first and the spread only earns its
    /// way in.</b> Getting that the other way round is a defect the reference shipped
    /// for one build: the snap searches <em>downward</em>, so snapping an
    /// already-offset position can land on a floor several storeys below the
    /// objective — the offset steps off the edge of the platform the intel is on and
    /// the search falls all the way to the ground. A bot then walks confidently to a
    /// place with nothing to do with its objective, which is far worse than not
    /// spreading at all.
    /// <para>
    /// <b>The fixtures were swept for, not chosen.</b> ctf_orange's node 1 is the
    /// extreme case — a 96px spread there drops <b>636px</b>, most of the map — and
    /// ctf_truefort's node 1 is the ordinary one at 102px, which is still more than
    /// a body's height and so still wrong. The guard is "within a body's height",
    /// which makes it "the same level" rather than "the same surface", so a spread
    /// can still cross between two runs that meet at a step.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("ctf_orange", 4488.0, 487.0)]
    [InlineData("ctf_truefort", 1560.0, 223.0)]
    public void ASpreadThatStepsOffALedgeIsRefused(string map, double x, double y)
    {
        var graph = Graph(map);
        var objective = new BotObjective(graph, new SpawnPoints(Level(map)));

        const double Spread = 96;

        // The premise: the offset position really does resolve, and really does
        // resolve far below. Without this the test could pass because the snap found
        // nothing at all, which is a different reason.
        var cliff = graph.SnapObjective(x + Spread, y).Node;
        var home = graph.SnapObjective(x, y).Node;

        Assert.True(cliff >= 0 && home >= 0);
        Assert.True(
            graph.StandY(cliff) - graph.StandY(home) > NavConstants.BoxH * NavConstants.CellSize,
            $"{map} ({x},{y}) is not a ledge: the offset lands {graph.StandY(cliff) - graph.StandY(home):0}px below");

        var goal = new BotGoal(Found: true, x + Spread, y, IsSpot: false, MustTouch: false);

        Assert.True(objective.TryResolve(in goal, Spread, out var gx, out var gy));

        Assert.Equal(graph.StandY(home), gy);
        Assert.True(
            Math.Abs(gx - x) < Spread,
            $"the spread survived a {graph.StandY(cliff) - graph.StandY(home):0}px drop: {gx:0} against {x:0}");

        // The control: a spread that stays on the same level is KEPT. Without it
        // this passes on an implementation that ignores the spread entirely, which
        // would silently undo the whole reason it exists.
        var flatGoal = new BotGoal(Found: true, x - Spread, y, IsSpot: false, MustTouch: false);
        var flat = graph.SnapObjective(x - Spread, y).Node;

        if (flat >= 0
            && Math.Abs(graph.StandY(flat) - graph.StandY(home)) <= NavConstants.BoxH * NavConstants.CellSize)
        {
            Assert.True(objective.TryResolve(in flatGoal, -Spread, out var fx, out _));
            Assert.True(fx < x, $"a level spread was dropped too: {fx:0} against {x:0}");
        }
    }

    // ------------------------------------------------------------------
    // End to end
    // ------------------------------------------------------------------

    /// <summary>
    /// A bot given nothing but a map walks across it, takes the enemy intel, and
    /// turns round.
    /// </summary>
    /// <remarks>
    /// <b>This is the gate the rest of this file exists to bisect.</b> Nothing is
    /// told to the bot: it is placed at its own spawn with a
    /// <see cref="BotObjective"/> and stepped, and everything after that — reading
    /// the mode off the entities, picking the enemy flag, resolving it to a node,
    /// planning, flying two stacked jumps, arriving, grabbing, and re-deciding for
    /// the run home — is the layer under test.
    /// <para>
    /// <b>The turn-round half is not a bonus assertion.</b> A cascade that finds
    /// the enemy flag and then keeps finding it after the grab sends the carrier
    /// back to the base it just left; the carry branch is what stops that, and it is
    /// invisible until somebody is actually carrying. Reaching the flag proves the
    /// route; leaving with it proves the cascade.
    /// </para>
    /// <para>
    /// <b>The control is a bot with no objective at all</b>, which must go
    /// nowhere. Without it every assertion here is also satisfied by a bot that
    /// happens to spawn on the flag, and the spawn points are the map's.
    /// </para>
    /// </remarks>
    [Fact]
    public void ABotWithNothingButAMapTakesTheEnemyIntel()
    {
        var (state, world, spawns) = Ctf();
        var graph = Graph();

        var slot = Put(ref state, spawns, 1, PlayerClass.Soldier, Team.Red, 384, 864);

        var bot = new ScriptedBot(graph, new NavQuery(Team.Red), BotSkill.ForTier(5), stagger: 1)
        {
            Objective = new BotObjective(graph, spawns),
            Role = BotRole.Attack,
            SpreadX = BotRoles.SpreadX(1),
            AliveLast = true,
        };

        var events = default(TickEvents);
        Span<PlayerInput> inputs = stackalloc PlayerInput[WorldState.MaxSlots];

        var grabbedAt = -1;

        // The scenario suite does this leg (`truefort-spawn-to-intel`) in 1,750
        // ticks with the goal handed over; the same budget it uses is the budget
        // here, so a regression that doubles the walk fails rather than passing
        // slowly.
        const int Budget = 6000;

        for (var tick = 0; tick < Budget && grabbedAt < 0; tick++)
        {
            inputs.Clear();
            inputs[slot] = bot.Poll(new WorldView(in state, world), slot, tick);

            MatchSimulation.Tick(world, ref state, inputs[..(slot + 1)], ref events, spawns);

            if (state.IsAlive(slot) && state.Characters[slot].CarryingIntel)
                grabbedAt = tick;
        }

        Assert.True(
            grabbedAt >= 0,
            $"never took the intel in {Budget} ticks: ended at x={state.Characters[slot].X:0} against a "
            + $"flag at {state.BlueIntel.HomeX:0}, replans {bot.Path.Replans}, stuck {bot.Path.StuckFires}, "
            + $"off-route {bot.Path.OffRouteFires}, blacklisted {bot.Path.BlacklistFires}");

        output.WriteLine(
            $"grabbed at tick {grabbedAt}: replans {bot.Path.Replans}, stuck {bot.Path.StuckFires}, "
            + $"off-route {bot.Path.OffRouteFires}, blacklisted {bot.Path.BlacklistFires}, "
            + $"rocket jumps {bot.Path.RocketJumps}");

        // PINNED, exactly, and a failure here is the RE-RECORD rather than a
        // broken test: check the change is one you meant, then that it went the way
        // you meant, then update these and say so in the commit. The same
        // discipline `ScenarioSuiteTests.Baseline` and the pinned `SIM_UUID` ask
        // for, and it is exact for the same reason — every draw is seeded, the
        // graph comes off disk and the physics is IEEE doubles, so a number that
        // moves at all is a behaviour change and not noise.
        //
        // A budget set before the arcs were flown is not itself a gate on the cost
        // — that is why this pins the exact tick rather than an upper bound.
        //
        // ctf_truefort carries 833 node pairs (across the shipped caches) where a
        // soldier's route is costed as a rocket jump while the edge index yields a
        // double jump first; a bot that flies the ordinary hop instead lands
        // somewhere its route did not mention and can ban a perfectly good edge.
        // `UnsupportedEdgeTicks` does not see this: the ban is one of the counters
        // above, and it stays at zero because the dispatch switch saw a type it
        // handles.
        Assert.Equal(1598, grabbedAt);
        Assert.Equal(32, bot.Path.Replans);
        Assert.Equal(0, bot.Path.StuckFires);
        Assert.Equal(10, bot.Path.OffRouteFires);
        Assert.Equal(0, bot.Path.BlacklistFires);

        // And it turns round. The goal has to be re-decided on the objective
        // cadence, so step past one before asking.
        var beforeX = bot.Path.GoalX;

        for (var tick = grabbedAt + 1; tick <= grabbedAt + BotConstants.ObjectivePeriod + 1; tick++)
        {
            inputs.Clear();
            inputs[slot] = bot.Poll(new WorldView(in state, world), slot, tick);
            MatchSimulation.Tick(world, ref state, inputs[..(slot + 1)], ref events, spawns);
        }

        Assert.True(
            bot.Path.GoalX < beforeX,
            $"the carrier did not turn round: goal {beforeX:0} to {bot.Path.GoalX:0}");

        Assert.True(
            Math.Abs(bot.Path.GoalX - state.RedIntel.HomeX) < 200,
            $"the carrier is heading for {bot.Path.GoalX:0}, not its own base at {state.RedIntel.HomeX:0}");

        // The control: the same bot with no objective layer never moves, so the
        // walk above is the cascade's doing and not the map's spawn placement.
        var (idleState, idleWorld, idleSpawns) = Ctf();
        var idleSlot = Put(ref idleState, idleSpawns, 1, PlayerClass.Soldier, Team.Red, 384, 864);

        var idle = new ScriptedBot(graph, new NavQuery(Team.Red), BotSkill.ForTier(5), stagger: 1)
        {
            AliveLast = true,
        };

        var startX = idleState.Characters[idleSlot].X;

        for (var tick = 0; tick < 300; tick++)
        {
            inputs.Clear();
            inputs[idleSlot] = idle.Poll(new WorldView(in idleState, idleWorld), idleSlot, tick);
            MatchSimulation.Tick(idleWorld, ref idleState, inputs[..(idleSlot + 1)], ref events, idleSpawns);
        }

        Assert.Equal(startX, idleState.Characters[idleSlot].X);
        Assert.False(idleState.Characters[idleSlot].CarryingIntel);
    }
}
