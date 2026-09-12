using Gg2.Bots;
using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Simulation;
using Xunit.Abstractions;

namespace Gg2.Tests;

/// <summary>
/// What a bot does about being shot at from outside its class's attention
/// radius.
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
