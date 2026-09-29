using GG2.Maps;
using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Tests;

/// <summary>
/// The generator: the only objective in the game with hit points.
/// </summary>
/// <remarks>
/// <c>Objects/Map elements/Gamemode-specific/Generator/</c>,
/// <c>Scripts/Damage API/damageGenerator.gml</c>,
/// <c>Scripts/Events/doEventGeneratorDestroy.gml</c> and
/// <c>Objects/Overlays/Gamemodes/GeneratorHUD.events/Step.xml</c>.
/// <para>
/// <b>No fixture reaches any of it, and none can.</b> Every recording in the
/// project is on <c>ctf_truefort</c>; <c>gen_destroy</c> is the only shipped
/// generator map and nothing has ever been recorded on it. These tests are the
/// whole gate.
/// </para>
/// </remarks>
public sealed class GeneratorTests
{
    private const string Map = "gen_destroy";

    private static LevelMap Level() =>
        LevelMap.FromPng(Map, ReferenceTree.ReadMap(Map)).ForArea(1);

    /// <summary>A loaded generator map, exactly as <c>GameServer.OpenMap</c> does it.</summary>
    private static (WorldState State, CollisionWorld World) Started(int timeLimitMinutes = 15)
    {
        var level = Level();
        var world = new CollisionWorld(level);
        var state = default(WorldState);

        state.GameMode = GameModes.Of(level);
        state.TimeLimitMinutes = timeLimitMinutes;
        state.CapLimit = 5;

        Generators.Place(ref state, level);
        ControlPoints.Place(ref state, level, out var zones);
        world.SetCaptureZones(zones);
        Rounds.Start(ref state);

        return (state, world);
    }

    private static void Put(ref WorldState state, int slot, Team team, double x, double y,
        PlayerClass playerClass = PlayerClass.Soldier)
    {
        state.AddPlayer(slot);
        state.Players[slot].Team = team;
        state.Players[slot].Class = playerClass;

        var character = CharacterState.Fresh(playerClass, team);
        character.X = x;
        character.Y = y;

        state.Characters[slot] = character;
        state.Weapons[slot] = WeaponState.Spawn(playerClass, x, y);
        state.Alive[slot] = true;
    }

    // --- the map ------------------------------------------------------------

    /// <summary>
    /// The one shipped generator map places one of each, and the mode is
    /// decided by their presence.
    /// </summary>
    [Fact]
    public void TheMapPlacesBothGeneratorsAtFullHealth()
    {
        var (state, _) = Started();

        Assert.Equal(GameMode.Generator, state.GameMode);

        foreach (var team in new[] { Team.Red, Team.Blue })
        {
            ref var generator = ref Generators.Of(ref state, team);

            Assert.True(generator.Exists);
            Assert.Equal(Generators.MaxHp, generator.Hp);
            Assert.Equal(Generators.MaxShieldHp, generator.ShieldHp);
            Assert.False(generator.IsShieldRegenerating);

            // At rest, not at zero: the round's tie-break reads this alarm as
            // "hit in the last three seconds", and a resting 0 would say yes for
            // ever.
            Assert.Equal(Timers.Inactive, generator.RegenerationAlarm);
        }
    }

    /// <summary>
    /// The map's shape promises one HUD block per generator, and
    /// <see cref="Generators.Place"/> takes one of each.
    /// </summary>
    [Fact]
    public void TheShapePromisesOneBlockPerGeneratorThePortRepresents()
    {
        var level = Level();

        Assert.Equal(2, GameModes.ShapeOf(level).GeneratorCount);
        Assert.Equal(0, GameModes.ShapeOf(
            LevelMap.FromPng("ctf_truefort", ReferenceTree.ReadMap("ctf_truefort"))).GeneratorCount);
    }

    // --- damage and the shield ----------------------------------------------

    /// <summary>
    /// The shield is a discount, not a barrier: a covered hit still costs
    /// half its damage in real hit points, and costs the shield all of it.
    /// </summary>
    [Fact]
    public void AShieldedHitStillTakesHalfOfItOffTheGenerator()
    {
        var (state, _) = Started();

        Generators.Damage(ref state, Team.Red, 40);

        Assert.Equal(Generators.MaxHp - 20, state.RedGenerator.Hp);
        Assert.Equal(Generators.MaxShieldHp - 40, state.RedGenerator.ShieldHp);
    }

    /// <summary>
    /// A hit bigger than the shield pays half on the covered part and the full
    /// rate on the rest — the reference's "allow overkill to be applied
    /// directly to the target".
    /// </summary>
    [Fact]
    public void AHitThatOverrunsTheShieldPaysFullRateOnTheRemainder()
    {
        var (state, _) = Started();

        Generators.Damage(ref state, Team.Red, 400);

        // (400 - 300) + 300*0.5 = 250.
        Assert.Equal(Generators.MaxHp - 250, state.RedGenerator.Hp);
        Assert.Equal(0, state.RedGenerator.ShieldHp);
    }

    /// <summary>
    /// The whole shield is worth 150 points of generator and nothing else — the
    /// control that would catch a shield modelled as absorption.
    /// </summary>
    [Fact]
    public void TheWholeShieldIsWorthHalfItsValueInHitPoints()
    {
        var (a, _) = Started();
        var (b, _) = Started();

        // 300 in one hit, versus 300 in ten.
        Generators.Damage(ref a, Team.Red, 300);

        for (var i = 0; i < 10; i++)
            Generators.Damage(ref b, Team.Red, 30);

        Assert.Equal(Generators.MaxHp - 150, a.RedGenerator.Hp);
        Assert.Equal(a.RedGenerator.Hp, b.RedGenerator.Hp);
        Assert.Equal(0, b.RedGenerator.ShieldHp);
    }

    /// <summary>
    /// Every hit re-arms the three-second buffer, so a generator under
    /// sustained fire never begins regenerating.
    /// </summary>
    [Fact]
    public void DamageReArmsTheRegenerationBufferAndStopsAnyRegenerationUnderWay()
    {
        var (state, _) = Started();

        state.RedGenerator.IsShieldRegenerating = true;
        state.RedGenerator.RegenerationAlarm = 3;

        Generators.Damage(ref state, Team.Red, 1);

        Assert.False(state.RedGenerator.IsShieldRegenerating);
        Assert.Equal(Generators.RegenerationBuffer, state.RedGenerator.RegenerationAlarm);
    }

    /// <summary>
    /// The buffer runs, then the shield comes back at one point a tick and
    /// stops at full.
    /// </summary>
    /// <remarks>
    /// One a tick because <c>isShieldRegenerating</c> is used as the
    /// <em>rate</em> and GM8's <c>true</c> is 1. Ten seconds for a full shield.
    /// </remarks>
    [Fact]
    public void TheShieldComesBackAtOnePointATickAfterThreeQuietSeconds()
    {
        var (state, world) = Started();

        Generators.Damage(ref state, Team.Red, 100);
        var shieldAfterHit = state.RedGenerator.ShieldHp;

        // An alarm set to n fires n ticks later, so 89 ticks of nothing.
        for (var i = 0; i < Generators.RegenerationBuffer - 1; i++)
            MatchSimulation.Tick(world, ref state, []);

        Assert.Equal(shieldAfterHit, state.RedGenerator.ShieldHp);
        Assert.False(state.RedGenerator.IsShieldRegenerating);

        // The tick the buffer expires is also the tick the shield first
        // moves: the alarm stage runs between Begin Step and Step, and the
        // regeneration is in the Step below it.
        MatchSimulation.Tick(world, ref state, []);
        Assert.True(state.RedGenerator.IsShieldRegenerating);
        Assert.Equal(shieldAfterHit + 1, state.RedGenerator.ShieldHp);

        for (var i = 0; i < 200; i++)
            MatchSimulation.Tick(world, ref state, []);

        Assert.Equal(Generators.MaxShieldHp, state.RedGenerator.ShieldHp);
        Assert.False(state.RedGenerator.IsShieldRegenerating);
    }

    // --- the destruction ----------------------------------------------------

    /// <summary>
    /// A generator on no hit points takes the match with it, on the tick after
    /// the damage — and the win goes to the <em>other</em> team.
    /// </summary>
    [Fact]
    public void AGeneratorOnNoHitPointsEndsTheMatchForTheOtherTeam()
    {
        var (state, world) = Started();

        Generators.Damage(ref state, Team.Red, 100000);
        Assert.True(state.RedGenerator.Exists);
        Assert.False(state.RoundDecided);

        var events = default(TickEvents);
        MatchSimulation.Tick(world, ref state, [], ref events);

        Assert.True(events.RedGeneratorDestroyed);
        Assert.False(events.BlueGeneratorDestroyed);
        Assert.False(state.RedGenerator.Exists);
        Assert.True(state.RoundDecided);
        Assert.Equal(Team.Blue, state.Winner);
    }

    /// <summary>
    /// The first generator to go decides the match; a second one going on
    /// the same tick cannot overturn it.
    /// </summary>
    /// <remarks>
    /// <c>doEventGeneratorDestroy.gml:14</c> is <c>if(global.winners == -1)</c>.
    /// Both messages still go out.
    /// </remarks>
    [Fact]
    public void TwoGeneratorsDyingAtOnceStillLeaveTheFirstOnesWinnerStanding()
    {
        var (state, world) = Started();

        state.RedGenerator.Hp = 0;
        state.BlueGenerator.Hp = -50;

        var events = default(TickEvents);
        MatchSimulation.Tick(world, ref state, [], ref events);

        Assert.True(events.RedGeneratorDestroyed);
        Assert.True(events.BlueGeneratorDestroyed);
        Assert.Equal(Team.Blue, state.Winner);
    }

    /// <summary>
    /// A generator on a fraction of a hit point is still standing — the reason
    /// the wire carries <c>ceil(hp)</c>.
    /// </summary>
    [Fact]
    public void AGeneratorOnAFractionOfAHitPointIsStillStanding()
    {
        var (state, world) = Started();

        state.RedGenerator.Hp = 0.1;
        MatchSimulation.Tick(world, ref state, []);

        Assert.True(state.RedGenerator.Exists);
        Assert.False(state.RoundDecided);
        Assert.Equal(1, Generators.ToBlock(state.RedGenerator)!.Value.Hp);
    }

    // --- the explosion ------------------------------------------------------

    /// <summary>
    /// The explosion has no team test: the team that just won is gibbed too.
    /// </summary>
    [Fact]
    public void TheExplosionKillsBothTeamsAlike()
    {
        var (state, world) = Started();
        var generator = state.RedGenerator;

        Put(ref state, 1, Team.Red, generator.X + 60, generator.Y);
        Put(ref state, 2, Team.Blue, generator.X - 60, generator.Y);
        Put(ref state, 3, Team.Blue, generator.X + 900, generator.Y);

        var farHp = state.Characters[3].Hp;

        Generators.Destroy(world, ref state, Team.Red, ref DiscardedEvents.Events);

        Assert.True(state.Characters[1].Hp < 0);
        Assert.True(state.Characters[2].Hp < 0);
        Assert.Equal(farHp, state.Characters[3].Hp);
    }

    /// <summary>
    /// It also throws them, away from the generator and harder the closer they
    /// were.
    /// </summary>
    [Fact]
    public void TheExplosionThrowsWhatItReachesAwayFromItself()
    {
        var (state, world) = Started();
        var generator = state.RedGenerator;

        Put(ref state, 1, Team.Blue, generator.X + 60, generator.Y);
        Put(ref state, 2, Team.Blue, generator.X + 300, generator.Y);

        Generators.Destroy(world, ref state, Team.Red, ref DiscardedEvents.Events);

        // Both pushed to the right, which is away from the generator.
        Assert.True(state.Characters[1].Motion.HSpeed > 0);
        Assert.True(state.Characters[2].Motion.HSpeed > 0);

        // And the near one harder: the falloff is linear in distance.
        Assert.True(state.Characters[1].Motion.HSpeed > state.Characters[2].Motion.HSpeed);
    }

    /// <summary>
    /// Nobody hurt them recently, so the generator takes the kill and the
    /// dealer is cleared.
    /// </summary>
    [Fact]
    public void AVictimWithNoRecentDealerIsCreditedToTheGenerator()
    {
        var (state, world) = Started();
        var generator = state.RedGenerator;

        Put(ref state, 1, Team.Blue, generator.X + 60, generator.Y);

        Generators.Destroy(world, ref state, Team.Red, ref DiscardedEvents.Events);

        Assert.Equal(CharacterState.Nobody, state.Characters[1].LastDamageDealer);
        Assert.Equal(DamageSource.GeneratorExplosion, state.Characters[1].LastDamageSource);
    }

    /// <summary>
    /// Somebody else hurt them last, so they are <em>finished off</em> and
    /// that somebody keeps the kill — the dealer is left in place.
    /// </summary>
    [Fact]
    public void AVictimSomebodyElseHurtIsFinishedOffAndGibbed()
    {
        var (state, world) = Started();
        var generator = state.RedGenerator;

        Put(ref state, 1, Team.Blue, generator.X + 60, generator.Y);
        Put(ref state, 2, Team.Red, generator.X + 200, generator.Y);

        state.Characters[1].LastDamageDealer = 2;
        state.Characters[1].LastDamageSource = DamageSource.Rocketlauncher;

        Generators.Destroy(world, ref state, Team.Red, ref DiscardedEvents.Events);

        Assert.Equal(2, state.Characters[1].LastDamageDealer);
        Assert.Equal(DamageSource.FinishedOffGib, state.Characters[1].LastDamageSource);
    }

    /// <summary>
    /// A victim whose own last dealer was themselves takes the generator's
    /// credit, not a finish-off: <c>lastDamageDealer != player</c>.
    /// </summary>
    [Fact]
    public void AVictimWhoLastHurtThemselvesIsStillTheGeneratorsKill()
    {
        var (state, world) = Started();
        var generator = state.RedGenerator;

        Put(ref state, 1, Team.Blue, generator.X + 60, generator.Y);
        state.Characters[1].LastDamageDealer = 1;
        state.Characters[1].LastDamageSource = DamageSource.Rocketlauncher;

        Generators.Destroy(world, ref state, Team.Red, ref DiscardedEvents.Events);

        Assert.Equal(CharacterState.Nobody, state.Characters[1].LastDamageDealer);
        Assert.Equal(DamageSource.GeneratorExplosion, state.Characters[1].LastDamageSource);
    }

    /// <summary>
    /// A sentry in the blast is destroyed — and does <b>not</b> take the
    /// permanent build-rate penalty, because the event writes <c>hp</c> straight
    /// rather than calling <c>damageSentry</c>.
    /// </summary>
    [Fact]
    public void ASentryInTheBlastLosesItsHitPointsButNotItsBuildRate()
    {
        var (state, world) = Started();
        var generator = state.RedGenerator;

        var sentry = SentryState.Build(Team.Blue, generator.X + 80, generator.Y, 1);
        sentry.Built = true;
        sentry.Hp = SentryState.MaxHp;
        state.Sentries[1] = sentry;
        state.HasSentry[1] = true;
        state.AddPlayer(1);
        state.Players[1].Team = Team.Blue;
        state.Players[1].Class = PlayerClass.Engineer;

        var rateBefore = state.Sentries[1].BuildRate;

        Generators.Destroy(world, ref state, Team.Red, ref DiscardedEvents.Events);

        Assert.True(state.Sentries[1].Hp < 0);
        Assert.Equal(rateBefore, state.Sentries[1].BuildRate);
        Assert.Equal(CharacterState.Nobody, state.Sentries[1].LastDamageDealer);
        Assert.Equal(DamageSource.GeneratorExplosion, state.Sentries[1].LastDamageSource);
    }

    /// <summary>
    /// Rockets in the blast go off and mines detonate, so nothing is left in the
    /// air.
    /// </summary>
    [Fact]
    public void TheExplosionSetsOffEverythingInTheAirThatCanGoOff()
    {
        var (state, world) = Started();
        var generator = state.RedGenerator;

        Put(ref state, 1, Team.Blue, generator.X + 700, generator.Y, PlayerClass.Demoman);

        state.SpawnProjectile(new ProjectileState
        {
            Kind = ProjectileKind.Rocket,
            Reflector = ProjectileState.NoOwner,
            X = generator.X + 100,
            Y = generator.Y,
            OwnerSlot = 1,
            Team = Team.Blue,
            Source = DamageSource.Rocketlauncher,
            HitDamage = 25,
        });

        state.SpawnProjectile(new ProjectileState
        {
            Kind = ProjectileKind.Mine,
            Reflector = ProjectileState.NoOwner,
            X = generator.X + 150,
            Y = generator.Y,
            OwnerSlot = 1,
            Team = Team.Blue,
            Source = DamageSource.Minegun,
        });
        state.Weapons[1].Lobbed++;

        Assert.Equal(2, state.LiveProjectileCount);

        Generators.Destroy(world, ref state, Team.Red, ref DiscardedEvents.Events);

        Assert.Equal(0, state.LiveProjectileCount);
    }

    // --- what can damage one -------------------------------------------------

    /// <summary>A pellet damages the enemy generator and dies on it.</summary>
    [Fact]
    public void APelletDamagesTheEnemyGeneratorAndDiesOnIt()
    {
        var (state, world) = Started();
        var generator = state.RedGenerator;

        Fire(ref state, ProjectileKind.Shot, Team.Blue, generator.X, generator.Y, hitDamage: 8);
        Projectiles.Collide(world, ref state, ref DiscardedEvents.Events);

        Assert.Equal(0, state.LiveProjectileCount);
        Assert.Equal(Generators.MaxHp - 4, state.RedGenerator.Hp);
        Assert.Equal(Generators.MaxShieldHp - 8, state.RedGenerator.ShieldHp);
    }

    /// <summary>And leaves its own team's alone, flying straight through it.</summary>
    [Fact]
    public void APelletIgnoresItsOwnTeamsGenerator()
    {
        var (state, world) = Started();
        var generator = state.RedGenerator;

        Fire(ref state, ProjectileKind.Shot, Team.Red, generator.X, generator.Y, hitDamage: 8);
        Projectiles.Collide(world, ref state, ref DiscardedEvents.Events);

        Assert.Equal(1, state.LiveProjectileCount);
        Assert.Equal(Generators.MaxHp, state.RedGenerator.Hp);
    }

    /// <summary>
    /// The <c>Shot</c>'s generator event has <b>no wall test</b>, where its
    /// character and sentry events both open with one.
    /// </summary>
    /// <remarks>
    /// The control that would catch the sentry event being reused: a pellet
    /// standing in solid map still damages a generator it overlaps.
    /// </remarks>
    [Fact]
    public void APelletInsideAWallStillDamagesAGenerator()
    {
        var level = Level();
        var (state, world) = Started();

        // gen_destroy hangs both generators in open air, so the generator is
        // moved onto solid map rather than the pellet being moved into a wall
        // near it. The position is one where a whole pellet is buried — the
        // test asks the same question the wall test would — and it is searched
        // for rather than assumed, so this cannot quietly stop testing what it
        // says it does.
        var found = false;
        double px = 0, py = 0;

        for (var y = 0; y < level.Height && !found; y += 8)
        {
            for (var x = 0; x < level.Width && !found; x += 8)
            {
                var probe = new ProjectileState { Kind = ProjectileKind.Shot, X = x, Y = y };

                if (world.PlaceFree(probe.Box, state.BulletFilter))
                    continue;

                found = true;
                px = x;
                py = y;
            }
        }

        Assert.True(found, "no buried pellet position anywhere on gen_destroy");

        state.RedGenerator.X = px;
        state.RedGenerator.Y = py;

        Fire(ref state, ProjectileKind.Shot, Team.Blue, px, py, hitDamage: 8);

        // The muzzle really is inside solid map: a Collision with Character or
        // a Collision with Sentry would destroy the pellet here and deal
        // nothing, which is the whole point of the case.
        Assert.False(world.PlaceFree(state.Projectiles[state.LiveProjectileSlot(0)].Box, state.BulletFilter));

        Projectiles.Collide(world, ref state, ref DiscardedEvents.Events);

        Assert.Equal(0, state.LiveProjectileCount);
        Assert.Equal(Generators.MaxHp - 4, state.RedGenerator.Hp);
    }

    /// <summary>
    /// A mine sticks to an enemy generator rather than exploding on it, exactly
    /// as it does to a sentry.
    /// </summary>
    [Fact]
    public void AMineSticksToAnEnemyGeneratorRatherThanExploding()
    {
        var (state, world) = Started();
        var generator = state.RedGenerator;

        Put(ref state, 1, Team.Blue, generator.X + 400, generator.Y, PlayerClass.Demoman);

        var handle = Fire(ref state, ProjectileKind.Mine, Team.Blue, generator.X, generator.Y, hitDamage: 0);
        state.Projectiles[handle.Index].Motion.SetComponents(4, -2);
        state.Weapons[1].Lobbed++;

        Projectiles.Collide(world, ref state, ref DiscardedEvents.Events);

        Assert.Equal(1, state.LiveProjectileCount);
        Assert.True(state.Projectiles[handle.Index].Stickied);
        Assert.Equal(0, state.Projectiles[handle.Index].Motion.Speed);
        Assert.Equal(Generators.MaxHp, state.RedGenerator.Hp);
    }

    /// <summary>
    /// A direct rocket deals its hit <em>and</em> its splash at distance
    /// zero, unlike a sentry hit where the direct half is <c>damageAuto</c>.
    /// </summary>
    [Fact]
    public void ARocketDealsItsHitAndItsSplashToAGenerator()
    {
        var (state, world) = Started();
        var generator = state.RedGenerator;

        Fire(ref state, ProjectileKind.Rocket, Team.Blue, generator.X, generator.Y, hitDamage: 25);
        Projectiles.Collide(world, ref state, ref DiscardedEvents.Events);

        Assert.Equal(0, state.LiveProjectileCount);

        // 25 through the shield: hp -= 12.5, shield 275. Then 30 of splash at
        // distance zero: hp -= 15, shield 245.
        Assert.Equal(Generators.MaxHp - 27.5, state.RedGenerator.Hp);
        Assert.Equal(Generators.MaxShieldHp - 55, state.RedGenerator.ShieldHp);
    }

    /// <summary>
    /// A rocket that goes off nearby scratches the generator with the same
    /// linear falloff a sentry takes — and has no quarter-radius cutoff, so
    /// even the outer edge of the blast counts.
    /// </summary>
    [Fact]
    public void ARocketBlastReachesAGeneratorRightToTheEdge()
    {
        var (state, world) = Started();
        var generator = state.RedGenerator;

        // Just inside the 65-pixel blast radius, measured from the generator's
        // box rather than its origin.
        var box = Generators.BodyBox(generator);
        var handle = Fire(ref state, ProjectileKind.Rocket, Team.Blue, box.Right + 64, generator.Y, hitDamage: 25);

        Explosions.Explode(world, ref state, handle, CharacterState.Nobody, ref DiscardedEvents.Events);

        Assert.True(state.RedGenerator.Hp < Generators.MaxHp);
        Assert.True(state.RedGenerator.Hp > Generators.MaxHp - 1);
    }

    /// <summary>
    /// A <em>flying</em> mine's blast skips its outer quarter, and a landed
    /// one does not — <c>and other.splashThreshhold</c>, which the movement
    /// pass clears on landing.
    /// </summary>
    /// <remarks>
    /// The control that <c>SplashThreshold</c> is really read on this path.
    /// It cannot be tested on a rocket, because nothing in the game ever sets
    /// the flag on one — <c>Mine.events/Create.xml</c> is its only writer — so
    /// giving the rocket's generator arm the cutoff would be an
    /// <em>equivalent</em> change rather than a wrong one. This is the mine's
    /// side, where the flag really varies.
    /// </remarks>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void OnlyALandedMineScratchesAGeneratorFromTheEdgeOfItsBlast(bool flying, bool expectDamage)
    {
        var (state, world) = Started();
        var generator = state.RedGenerator;

        Put(ref state, 1, Team.Blue, generator.X + 500, generator.Y, PlayerClass.Demoman);

        // Just inside the 40-pixel radius, so `1 - gap/radius` is under a
        // quarter — the band a flying mine skips.
        var box = Generators.BodyBox(generator);
        var handle = Fire(ref state, ProjectileKind.Mine, Team.Blue, box.Right + 39, generator.Y, hitDamage: 0);

        state.Projectiles[handle.Index].SplashThreshold = flying;
        state.Projectiles[handle.Index].Stickied = !flying;
        state.Weapons[1].Lobbed++;

        Explosions.DetonateMine(world, ref state, handle, ref DiscardedEvents.Events);

        Assert.Equal(expectDamage, state.RedGenerator.Hp < Generators.MaxHp);
    }

    private static ProjectileHandle Fire(
        ref WorldState state,
        ProjectileKind kind,
        Team team,
        double x,
        double y,
        double hitDamage)
    {
        var projectile = new ProjectileState
        {
            Kind = kind,
            Reflector = ProjectileState.NoOwner,
            X = x,
            Y = y,
            OwnerSlot = 1,
            Team = team,
            Source = DamageSource.Scattergun,
            HitDamage = hitDamage,
        };

        return state.SpawnProjectile(projectile);
    }

    // --- the round -----------------------------------------------------------

    /// <summary>The clock counts down, and nothing happens while it is running.</summary>
    [Fact]
    public void TheClockRunsAndDecidesNothingWhileItDoes()
    {
        var (state, world) = Started(timeLimitMinutes: 1);

        MatchSimulation.Tick(world, ref state, []);

        Assert.Equal(Rounds.TimerFor(1) - 1, state.RoundTimer);
        Assert.False(state.RoundDecided);
    }

    /// <summary>
    /// At 0:00 the healthier generator wins — by <em>damage stage</em>, not
    /// by hit points, so the two are tied anywhere inside one band.
    /// </summary>
    [Theory]
    // Both untouched: a draw, which the wire spells TEAM_SPECTATOR.
    [InlineData(2100, 2100, (int)Team.Spectator)]
    // Blue is one band down; red wins by being in better shape.
    [InlineData(2100, 1000, (int)Team.Red)]
    [InlineData(500, 2100, (int)Team.Blue)]
    // Both hurt, and both in the same band: still a draw, though red has taken
    // 700 more damage. This is the case that separates stages from hit points.
    [InlineData(700, 1385, (int)Team.Spectator)]
    public void TheClockRunningOutGoesToTheHealthierDamageStage(double redHp, double blueHp, int winner)
    {
        var (state, _) = Started(timeLimitMinutes: 1);

        state.RedGenerator.Hp = redHp;
        state.BlueGenerator.Hp = blueHp;
        state.RoundTimer = 0;

        Rounds.Step(ref state);

        Assert.True(state.RoundDecided);
        Assert.Equal((Team)winner, state.Winner);
    }

    /// <summary>
    /// It is the generator that is about to <em>win</em> whose alarm is
    /// read: a push against the leader holds the match open.
    /// </summary>
    /// <remarks>
    /// <c>if (redStage &gt; blueStage &amp;&amp; GeneratorRed.alarm[0] &gt; -1)</c>
    /// — red is ahead and <b>red's</b> generator is the one being hit. Reading
    /// it as "the trailing generator is under fire" gets every row of this table
    /// backwards, which is what the first draft of it did.
    /// </remarks>
    [Theory]
    // The leading generator is under fire, so the match stays open.
    [InlineData(2100, 1000, true, false)]
    [InlineData(500, 2100, false, true)]
    // Level stages: either one being hit is enough.
    [InlineData(2100, 2100, true, false)]
    [InlineData(2100, 2100, false, true)]
    public void AGeneratorUnderFireAtTheWhistleForcesOvertime(
        double redHp, double blueHp, bool redUnderFire, bool blueUnderFire)
    {
        var (state, _) = Started(timeLimitMinutes: 1);

        state.RedGenerator.Hp = redHp;
        state.BlueGenerator.Hp = blueHp;
        state.RedGenerator.RegenerationAlarm = redUnderFire ? 30 : Timers.Inactive;
        state.BlueGenerator.RegenerationAlarm = blueUnderFire ? 30 : Timers.Inactive;
        state.RoundTimer = 0;

        Rounds.Step(ref state);

        Assert.False(state.RoundDecided);
        Assert.True(state.Overtime);
    }

    /// <summary>
    /// The control for the row above: the <em>trailing</em> generator being
    /// under fire changes nothing, because nobody is contesting the result.
    /// </summary>
    [Fact]
    public void TheLosingGeneratorBeingUnderFireDoesNotHoldTheMatchOpen()
    {
        var (state, _) = Started(timeLimitMinutes: 1);

        state.RedGenerator.Hp = 2100;
        state.BlueGenerator.Hp = 1000;
        state.BlueGenerator.RegenerationAlarm = 30;
        state.RoundTimer = 0;

        Rounds.Step(ref state);

        Assert.True(state.RoundDecided);
        Assert.Equal(Team.Red, state.Winner);
        Assert.False(state.Overtime);
    }

    /// <summary>
    /// The tie-break cannot run on the tick the clock reaches zero — the
    /// reference's <c>else if</c> means the decrement and the decision are
    /// never the same tick.
    /// </summary>
    [Fact]
    public void TheTieBreakWaitsOneTickAfterTheClockReachesZero()
    {
        var (state, _) = Started(timeLimitMinutes: 1);

        state.RedGenerator.Hp = 2100;
        state.BlueGenerator.Hp = 100;
        state.RoundTimer = 1;

        Rounds.Step(ref state);

        Assert.Equal(0, state.RoundTimer);
        Assert.False(state.RoundDecided);

        Rounds.Step(ref state);

        Assert.True(state.RoundDecided);
        Assert.Equal(Team.Red, state.Winner);
    }

    /// <summary>
    /// A destroyed generator ends the tie-break's reach: the round is already
    /// decided and nothing here can change it.
    /// </summary>
    [Fact]
    public void TheTieBreakDoesNothingOnceAGeneratorHasGone()
    {
        var (state, _) = Started(timeLimitMinutes: 1);

        state.RedGenerator.Exists = false;
        state.BlueGenerator.Hp = 100;
        state.RoundTimer = 0;

        Rounds.Step(ref state);

        Assert.False(state.RoundDecided);
        Assert.False(state.Overtime);
    }

    /// <summary>
    /// A world with no time limit — the default, and every unit test and bot
    /// rollout — never reaches the tie-break at all.
    /// </summary>
    [Fact]
    public void NoTimeLimitMeansTheTieBreakNeverFires()
    {
        var (state, _) = Started(timeLimitMinutes: 0);

        Rounds.Step(ref state);

        Assert.False(state.RoundDecided);
    }

    /// <summary>
    /// A map change puts both generators back, so the next round starts on full
    /// health rather than on the last round's wreckage.
    /// </summary>
    [Fact]
    public void AMapChangeClearsTheGenerators()
    {
        var (state, _) = Started();

        state.RedGenerator.Hp = 5;
        Rounds.ResetForNewMap(ref state, firstArea: true);

        Assert.False(state.RedGenerator.Exists);

        Generators.Place(ref state, Level());
        Assert.Equal(Generators.MaxHp, state.RedGenerator.Hp);
    }
}
