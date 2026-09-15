using Gg2.Bots;
using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Simulation;
using Xunit.Abstractions;

namespace Gg2.Tests;

/// <summary>
/// What a bot does about being shot at: from outside its class's attention
/// radius, and by a sentry.
/// </summary>
/// <remarks>
/// <b>Reported from play, on a control map</b>: a defending pyro stood on the
/// point and took fire from across the room without reacting at all — no aim, no
/// keys, no step — until it died. The cause was one line: a class radius is what
/// a bot SCANS with, and it was also deciding whether a bot could be TOLD.
/// Everything past it was dropped in <see cref="BotTargeting.Find"/> before the
/// <c>shot me recently</c> term was applied, so that term was unreachable at
/// exactly the distance it exists for.
/// <para>
/// The two halves of the answer are asserted separately because they fail
/// separately: <see cref="BotTargeting.AttentionRange"/> is what lets the bot see
/// its attacker, and <c>ScriptedBot</c>'s short re-post timer is what moves it
/// when seeing is all it can do.
/// </para>
/// <para>
/// <b>The heavy is the control and is not decoration.</b> Its 500px radius
/// already covered the shooter, so an assertion that only says "a bot under fire
/// reacts" is satisfied by it on the unfixed code too — a suite without it cannot
/// tell a fix that works from a scenario that always worked.
/// </para>
/// <para>
/// The second subject shares the harness rather than the question: a sentry's
/// damage is credited to its OWNER, so "who is shooting me" named the engineer
/// and not the gun. <c>docs/TRAPS-BOTS.md</c>, "a sentry is credited to its
/// engineer".
/// </para>
/// </remarks>
public sealed class BotUnderFireTests(ITestOutputHelper output)
{
    private const string Map = "koth_valley";

    /// <summary>
    /// Far enough out that a pyro (200px) and a scout (220px) cannot see it and a
    /// heavy (500px) can. Also past <c>BOT_BLIND_RANGE</c>, so the blind fallback
    /// cannot supply the target either.
    /// </summary>
    private const double ShotFrom = 418;

    private sealed record Watch(
        int TargetTicks, int AttackTicks, double Walked, double FurthestFromPost, bool Alive, double Hp);

    /// <summary>
    /// A red bot holding a control point its team owns, and a blue sniper standing
    /// off it with a clear line.
    /// </summary>
    /// <remarks>
    /// <b>The damage is written rather than fired.</b> A real rifle would make the
    /// measurement depend on the shooter's aim and on whether the bot's own
    /// reaction happened to break the line — which is the thing under test. Every
    /// field a damage path writes is written here (<c>LastDamageDealer</c>, the
    /// source, the finish-off timer and <c>TimeUnscathed</c>), so what the bot
    /// reads is what a rifle would have left.
    /// </remarks>
    private Watch Post(PlayerClass playerClass)
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

        Intel.Place(ref state, level);
        ControlPoints.Place(ref state, level, out var zones);
        world.SetCaptureZones(zones);
        Rounds.Start(ref state);

        var spawns = new SpawnPoints(level);
        ReferenceTree.RequireNavCache();
        var graph = NavCache.Load(ReferenceTree.NavCacheDirectory, Map, 1);

        // Slot 0 is the server's own reserved player and always exists.
        state.AddPlayer(0);

        const int Bot = 1;
        const int Shooter = 2;

        state.AddPlayer(Bot);
        state.Spawn(spawns, Bot, playerClass, Team.Red, spawnpointId: 0, group: 0);
        state.AddPlayer(Shooter);
        state.Spawn(spawns, Shooter, PlayerClass.Sniper, Team.Blue, spawnpointId: 0, group: 0);

        var bot = new ScriptedBot(graph, new NavQuery(Team.Red), BotSkill.ForTier(5), stagger: 1)
        {
            Objective = new BotObjective(graph, spawns),
            Role = BotRole.Defend,
            SpreadX = 0,
            AliveLast = true,
        };

        var zone = world.CaptureZones[0];
        var postX = (zone.Left + zone.Right) / 2.0;
        var postY = (zone.Top + zone.Bottom) / 2.0;

        // Red holds the point and the bot is standing on it: the reported state,
        // set rather than played out, so the test measures the reaction and not the
        // walk to the objective.
        state.ControlPoints[0].Owned = true;
        state.ControlPoints[0].Team = Team.Red;
        state.ControlPoints[0].Locked = false;

        state.Characters[Bot].X = postX;
        state.Characters[Bot].Y = postY;

        var (shooterX, shooterY) = FiringPosition(world, in state, postX, postY);

        var events = default(TickEvents);
        Span<PlayerInput> inputs = stackalloc PlayerInput[WorldState.MaxSlots];

        var targetTicks = 0;
        var attackTicks = 0;
        var walked = 0.0;
        var furthest = 0.0;

        for (var tick = 0; tick < 300; tick++)
        {
            // Pinned every tick: the shooter is a firing position rather than a
            // player, and letting it fall down the hill would move the range
            // mid-test.
            state.Characters[Shooter].X = shooterX;
            state.Characters[Shooter].Y = shooterY;
            state.Characters[Shooter].HSpeed = 0;
            state.Characters[Shooter].VSpeed = 0;

            inputs.Clear();
            inputs[Bot] = bot.Poll(new WorldView(in state, world), Bot, tick);

            if (bot.Combat.Target.Exists)
                targetTicks++;

            if ((inputs[Bot].Keys & PlayerKeys.Attack) != 0)
                attackTicks++;

            var before = state.Characters[Bot].X;

            MatchSimulation.Tick(world, ref state, inputs[..3], ref events, spawns);

            if (!state.IsAlive(Bot))
                break;

            walked += Math.Abs(state.Characters[Bot].X - before);
            furthest = Math.Max(furthest, Math.Abs(state.Characters[Bot].X - postX));

            // A rifle hit every second.
            if (tick % 30 == 0)
            {
                ref var victim = ref state.Characters[Bot];
                victim.Hp -= 10;
                victim.LastDamageDealer = Shooter;
                victim.LastDamageSource = DamageSource.RifleCharged;
                victim.FinishOffTimer = GameConstants.AssistTime;
                victim.TimeUnscathed = 0;
            }
        }

        var watch = new Watch(
            targetTicks, attackTicks, walked, furthest,
            state.IsAlive(Bot), state.Characters[Bot].Hp);

        output.WriteLine(
            $"{playerClass} (attention {BotTargeting.ClassRange(playerClass)}px, shot from {ShotFrom:0}px): "
            + $"target on {watch.TargetTicks}/300 ticks, attack on {watch.AttackTicks}, "
            + $"walked {watch.Walked:0}px, furthest from the post {watch.FurthestFromPost:0}px");

        return watch;
    }

    /// <summary>
    /// Somewhere about <see cref="ShotFrom"/> off the point with a clear bullet
    /// line to it.
    /// </summary>
    /// <remarks>
    /// <b>Swept rather than written down.</b> koth_valley's point sits in a hollow
    /// and there is no clear horizontal line at that range at all, so a hard-coded
    /// pair would have put the shooter behind a hillside and left the bot with
    /// nothing to react to — a test that passes on the fixed code for the wrong
    /// reason.
    /// </remarks>
    private static (double X, double Y) FiringPosition(
        CollisionWorld world, in WorldState state, double postX, double postY)
    {
        for (var dy = -260.0; dy <= 120; dy += 20)
        {
            foreach (var sign in new[] { -1.0, 1.0 })
            {
                var x = postX + sign * ShotFrom;
                var y = postY + dy;

                if (!world.CollisionLineBlocksBullets(postX, postY, x, y, state.BulletFilter))
                    return (x, y);
            }
        }

        throw new InvalidOperationException($"no clear line {ShotFrom}px off koth_valley's point");
    }

    /// <summary>
    /// The reported bug: a pyro whose attacker is 200px past what it scans.
    /// </summary>
    /// <remarks>
    /// It sees the sniper — that is <see cref="BotTargeting.AttentionRange"/> — and
    /// it does not flame at it, because <c>BotClassKeys</c> gates the flame on
    /// <c>BOT_FLAME_REACH</c> and nothing here widened that. A pyro burning air at
    /// 418px would be this fix overshooting rather than working.
    /// </remarks>
    [Fact]
    public void APyroShotFromOutsideItsRadiusLooksAtWhoIsShootingIt()
    {
        var watch = Post(PlayerClass.Pyro);

        Assert.True(watch.TargetTicks > 150, $"the pyro held a target on only {watch.TargetTicks} of 300 ticks");
        Assert.Equal(0, watch.AttackTicks);
    }

    /// <summary>
    /// And it moves: the half a bot with no answer needs.
    /// </summary>
    /// <remarks>
    /// The scout rather than the pyro, because a pyro's spot search wanders about
    /// 100px around a held point of its own accord — enough to swamp a displacement
    /// test either way. A scout on its post stands genuinely still (3px over 300
    /// ticks on the unfixed code), so any real displacement here is the re-post and
    /// nothing else.
    /// </remarks>
    [Fact]
    public void AScoutShotFromOutsideItsRadiusStopsStandingThere()
    {
        var watch = Post(PlayerClass.Scout);

        Assert.True(watch.TargetTicks > 100, $"the scout held a target on only {watch.TargetTicks} of 300 ticks");

        Assert.True(
            watch.FurthestFromPost > BotConstants.RepostMinMove,
            $"the scout moved {watch.FurthestFromPost:0}px off its post, which is not a re-post");
    }

    /// <summary>
    /// A clear bullet line to the point at exactly <paramref name="radius"/>
    /// pixels, swept round a ring.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Separate from <see cref="FiringPosition"/> on purpose, and not a
    /// duplicate to be merged away.</b> That one sweeps
    /// <c>x = post ± ShotFrom</c> over a range of vertical offsets, which makes
    /// <c>ShotFrom</c> a horizontal component rather than a distance — the first
    /// clear line on this map is 260px above the point, so its shooter actually
    /// stands about 493px off. The three tests above are calibrated against that
    /// position and <b>replacing it with a true-distance ring moves them</b>: the
    /// heavy's control assertion goes from holding its post to leaving it by
    /// 369px, which is the fixture moving rather than the claim failing.
    /// <para>
    /// The sentry scenario needs a real distance, because its whole point is a
    /// sentry and an engineer at two known ranges from the same spot. So it gets
    /// its own helper and the old fixture is left exactly where it was.
    /// </para>
    /// </remarks>
    private static (double X, double Y) RingPosition(
        CollisionWorld world, in WorldState state, double postX, double postY, double radius)
    {
        for (var degrees = 0.0; degrees < 360; degrees += 5)
        {
            var x = postX + radius * Math.Cos(degrees * Math.PI / 180);
            var y = postY - radius * Math.Sin(degrees * Math.PI / 180);

            if (world.CollisionLineBlocksBullets(postX, postY, x, y, state.BulletFilter))
                continue;

            Assert.Equal(radius, Gml.PointDistance(postX, postY, x, y), 6);

            return (x, y);
        }

        throw new InvalidOperationException($"no clear line {radius}px off koth_valley's point");
    }

    private sealed record SentryWatch(
        int SentryTicks, int EngineerTicks, int NoTargetTicks, int AttackTicks, double Hp);

    /// <summary>
    /// A bot holding the point with an enemy sentry shooting it, and the engineer
    /// who built the sentry standing nearer than the sentry is.
    /// </summary>
    /// <remarks>
    /// <b>The engineer is deliberately the nearer of the two.</b> The whole claim
    /// is that the bot answers the thing that is shooting it rather than the
    /// nearest enemy body, and a fixture where the sentry is also the nearest is
    /// satisfied by the plain distance term and gates nothing.
    /// <para>
    /// The sentry's damage is written rather than fired, for the reason the sniper
    /// harness above writes its own: a real turret's damage depends on its facing
    /// and its refire, and the bot's reaction changes both. Every field the
    /// turret's own fire writes is written here — dealer, source, finish-off timer
    /// and <c>TimeUnscathed</c> — so what the bot reads is what a turret would have
    /// left. <c>DamageSource.SentryTurret</c> with the ENGINEER as the dealer is
    /// not a quirk of the fixture: it is exactly what the turret writes, and it is
    /// the reason the bonus needs two fields to land in the right place.
    /// </para>
    /// </remarks>
    private SentryWatch PostAgainstASentry(PlayerClass playerClass)
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

        Intel.Place(ref state, level);
        ControlPoints.Place(ref state, level, out var zones);
        world.SetCaptureZones(zones);
        Rounds.Start(ref state);

        var spawns = new SpawnPoints(level);
        ReferenceTree.RequireNavCache();
        var graph = NavCache.Load(ReferenceTree.NavCacheDirectory, Map, 1);

        state.AddPlayer(0);

        const int Bot = 1;
        const int Engineer = 2;

        state.AddPlayer(Bot);
        state.Spawn(spawns, Bot, playerClass, Team.Red, spawnpointId: 0, group: 0);
        state.AddPlayer(Engineer);
        state.Spawn(spawns, Engineer, PlayerClass.Engineer, Team.Blue, spawnpointId: 0, group: 0);

        var bot = new ScriptedBot(graph, new NavQuery(Team.Red), BotSkill.ForTier(5), stagger: 1)
        {
            Objective = new BotObjective(graph, spawns),
            Role = BotRole.Defend,
            SpreadX = 0,
            AliveLast = true,
        };

        var zone = world.CaptureZones[0];
        var postX = (zone.Left + zone.Right) / 2.0;
        var postY = (zone.Top + zone.Bottom) / 2.0;

        state.ControlPoints[0].Owned = true;
        state.ControlPoints[0].Team = Team.Red;
        state.ControlPoints[0].Locked = false;

        state.Characters[Bot].X = postX;
        state.Characters[Bot].Y = postY;

        var (sentryX, sentryY) = RingPosition(world, in state, postX, postY, 260);
        var (engineerX, engineerY) = RingPosition(world, in state, postX, postY, 150);

        state.Sentries[Engineer] = SentryState.Build(Team.Blue, sentryX, sentryY, 1);
        state.HasSentry[Engineer] = true;

        var events = default(TickEvents);
        Span<PlayerInput> inputs = stackalloc PlayerInput[WorldState.MaxSlots];

        var sentryTicks = 0;
        var engineerTicks = 0;
        var noTargetTicks = 0;
        var attackTicks = 0;

        for (var tick = 0; tick < 300; tick++)
        {
            state.Characters[Engineer].X = engineerX;
            state.Characters[Engineer].Y = engineerY;
            state.Characters[Engineer].HSpeed = 0;
            state.Characters[Engineer].VSpeed = 0;

            // The sentry is a fixture too: it is the thing being shot at, and
            // letting the simulation knock it down would end the measurement
            // early with the bot's reaction unmeasured.
            state.Sentries[Engineer].X = sentryX;
            state.Sentries[Engineer].Y = sentryY;
            state.Sentries[Engineer].Hp = SentryState.MaxHp;

            inputs.Clear();
            inputs[Bot] = bot.Poll(new WorldView(in state, world), Bot, tick);

            switch (bot.Combat.Target.Kind)
            {
                case BotTargetKind.Sentry: sentryTicks++; break;
                case BotTargetKind.Character when bot.Combat.Target.Slot == Engineer: engineerTicks++; break;
                case BotTargetKind.None: noTargetTicks++; break;
            }

            if ((inputs[Bot].Keys & PlayerKeys.Attack) != 0)
                attackTicks++;

            MatchSimulation.Tick(world, ref state, inputs[..3], ref events, spawns);

            if (!state.IsAlive(Bot))
                break;

            // The turret lands a hit every half second, credited the way the
            // turret credits it: to the ENGINEER's slot, tagged as the gun.
            if (tick % 15 == 0)
            {
                ref var victim = ref state.Characters[Bot];
                victim.Hp -= 8;
                victim.LastDamageDealer = Engineer;
                victim.LastDamageSource = DamageSource.SentryTurret;
                victim.FinishOffTimer = GameConstants.AssistTime;
                victim.TimeUnscathed = 0;
            }
        }

        var watch = new SentryWatch(
            sentryTicks, engineerTicks, noTargetTicks, attackTicks, state.Characters[Bot].Hp);

        output.WriteLine(
            $"{playerClass} shot by a sentry 260px off the point, engineer 150px: "
            + $"aimed at the sentry {watch.SentryTicks}/300, at the engineer {watch.EngineerTicks}, "
            + $"nothing {watch.NoTargetTicks}, attack on {watch.AttackTicks}, hp {watch.Hp}");

        return watch;
    }

    /// <summary>
    /// The reported case: a bot on the objective under sentry fire answers the
    /// sentry rather than its builder.
    /// </summary>
    /// <remarks>
    /// A turret's damage is credited to its OWNER, so before the source test the
    /// whole attacker bonus went to the engineer — and the bot chose between the
    /// gun killing it and the player who built it on the distance term alone.
    /// <para>
    /// ⚠️ <b>"The sentry won more ticks than the engineer" gates nothing here</b>,
    /// and the first version of this test asserted exactly that. On the unfixed
    /// code the heavy already splits 166 to 106 in the sentry's favour, because the
    /// sentry bonus alone beats the engineer's own bonus about half the time — so
    /// that assertion passes on both arms. What moves is the engineer's count, and
    /// it moves to <b>zero</b>: with the bonus on the right candidate the sentry
    /// scores 1 against the engineer's 90, and there is no tick on which the
    /// builder is the better answer.
    /// </para>
    /// <para>
    /// The second assertion is not redundant with the first. A bot that acquired
    /// <em>nothing</em> also scores zero engineer ticks, so without it the whole
    /// test is satisfied by a targeting layer that stopped working.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(PlayerClass.Heavy)]
    [InlineData(PlayerClass.Soldier)]
    public void ABotOnThePointAnswersTheSentryRatherThanItsBuilder(PlayerClass playerClass)
    {
        var watch = PostAgainstASentry(playerClass);

        Assert.Equal(0, watch.EngineerTicks);

        Assert.True(
            watch.SentryTicks > 250,
            $"held the sentry on only {watch.SentryTicks} of 300 ticks, so the zero above says nothing");
    }

    /// <summary>
    /// The control: a class whose radius already reached the shooter, which must
    /// still stand and fight rather than be moved by the new timer.
    /// </summary>
    /// <remarks>
    /// <b>Both halves matter.</b> It fires, as it did before; and it holds its
    /// post, which is what says the short re-post timer cannot pull a bot off
    /// ground it is successfully defending — the over-trigger that cost
    /// <c>arena_montane</c> 12 of 128 matches when the timer was keyed on being shot
    /// alone. docs/TRAPS-BOTS.md, "standing still under fire".
    /// </remarks>
    [Fact]
    public void AHeavyThatCanAlreadySeeTheShooterStandsAndFights()
    {
        var watch = Post(PlayerClass.Heavy);

        Assert.True(watch.AttackTicks > 100, $"the heavy fired on only {watch.AttackTicks} of 300 ticks");

        Assert.True(
            watch.FurthestFromPost < BotConstants.RepostMinMove,
            $"the heavy left its post by {watch.FurthestFromPost:0}px while it was winning the fight");
    }
}
