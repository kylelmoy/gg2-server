using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// The engineer's sentry: phase 5's last weapon system.
/// </summary>
/// <remarks>
/// <b>No recording can gate most of this precisely.</b> A turret's fire
/// broadcasts nothing — its damage reaches the wire only as the victim's hp
/// byte — and no checked-in fixture holds a sentry that survives to shoot in a
/// closed loop. So these tests are the gate. Geometry that needs the running
/// engine was measured on 2026-08-26 (docs/SIMULATION.md: the sentry's constant
/// mask box, the turret's rotated one, collision_circle's Euclidean rule) and
/// the boxes here are those measurements, not readings of the sprite XML.
/// <para>
/// Transcribed from <c>Objects/InGameElements/Sentry.events/{Create, Begin
/// Step, End Step, User Event 2, Destroy}.xml</c>,
/// <c>Objects/Weapons/SentryTurret.events/{Create, User Event 0, End Step,
/// Alarm 0}.xml</c>, <c>Scripts/GameServer/serverBuildSentry.gml</c>,
/// <c>Scripts/Object Creation/buildSentry.gml</c>,
/// <c>Scripts/Damage/damageSentry.gml</c> and the <c>Collision with
/// Sentry</c> events of <c>Shot</c>, <c>Rocket</c>, <c>Mine</c> and
/// <c>BurningProjectile</c>.
/// </para>
/// </remarks>
public sealed class SentryTests
{
    private const string Map = "ctf_truefort";

    /// <summary>A standing position on open floor; the floor mask starts at y = 732.</summary>
    private const double OpenX = 2600;

    private const double OpenY = 708;

    /// <summary>Where a sentry's origin sits when resting on that floor: bottom = 719 + 12 = 731.</summary>
    private const double FloorY = 719;

    private static CollisionWorld? _sharedWorld;

    private static CollisionWorld World => _sharedWorld ??=
        new CollisionWorld(LevelMap.FromPng(Map, ReferenceTree.ReadMap(Map)));

    private static void Put(
        ref WorldState state,
        int slot,
        PlayerClass playerClass,
        Team team,
        double x,
        double y)
    {
        var character = CharacterState.Fresh(playerClass, team);
        character.X = x;
        character.Y = y;

        state.Characters[slot] = character;
        state.Weapons[slot] = WeaponState.Spawn(playerClass, x, y);
        state.Weapons[slot].ReadyToShoot = false;
        state.Weapons[slot].RefireTimer = WeaponState.Inactive;
        state.Alive[slot] = true;

        // Phase 6: the player behind the slot, which the sentry Begin Step
        // guard and Build read.
        state.AddPlayer(slot);
        state.Players[slot].Team = team;
        state.Players[slot].Class = playerClass;
    }

    /// <summary>
    /// A finished sentry with a warm turret, as one looks a while after
    /// construction: full hp, refire alarm long since fired, no target seen.
    /// </summary>
    private static void PutBuiltSentry(
        ref WorldState state,
        int slot,
        Team team,
        double x,
        double y,
        int facing = 1)
    {
        var sentry = SentryState.Build(team, x, y, facing);
        sentry.Built = true;
        sentry.HasTurret = true;
        sentry.Hp = SentryState.MaxHp;
        sentry.Motion.VSpeed = 0;
        sentry.OldVSpeed = 0;
        sentry.ReadyToShoot = true;
        sentry.RefireTimer = Timers.Inactive;
        sentry.NoticedTarget = true;
        sentry.TurretDirection = facing > 0 ? 0 : 180;
        sentry.TurretXScale = facing;
        sentry.RotateIndex = sentry.RotateStart;

        state.Sentries[slot] = sentry;
        state.HasSentry[slot] = true;

        // The owner player, or the Begin Step class/team guard - the whole of
        // the reference's, in the simulation since phase 6 - eats the sentry
        // on the first tick.
        if (!state.HasPlayer[slot])
        {
            state.AddPlayer(slot);
            state.Players[slot].Team = team;
            state.Players[slot].Class = PlayerClass.Engineer;
        }
    }

    private static void Tick(CollisionWorld world, ref WorldState state, ref TickEvents events) =>
        MatchSimulation.Tick(world, ref state, [], ref events);

    // =========================================================================
    // Building
    // =========================================================================

    /// <summary>
    /// <c>buildSentry.gml</c> takes the whole hundred, and
    /// <c>Character.events/Begin Step.xml:387</c> crawls it back a tenth per
    /// tick.
    /// </summary>
    [Fact]
    public void BuildingCostsTheWholeStockAndRegenerationCrawlsItBack()
    {
        var world = World;
        var state = default(WorldState);
        var events = default(TickEvents);

        Put(ref state, 1, PlayerClass.Engineer, Team.Red, OpenX, OpenY);
        Assert.True(Sentries.CanBuild(state, 1));

        Sentries.Build(ref state, 1, OpenX, OpenY, 1);

        Assert.True(state.HasSentry[1]);
        Assert.Equal(0, state.Characters[1].NutsNBolts);
        Assert.False(Sentries.CanBuild(state, 1));

        Tick(world, ref state, ref events);

        // min(0 + 0.1, 100). Ten ticks a unit, a thousand ticks to full.
        Assert.Equal(0.1, state.Characters[1].NutsNBolts);
    }

    /// <summary>
    /// <c>serverBuildSentry.gml</c>: <c>nutsNBolts == 100</c> is an equality,
    /// and <c>collision_circle(x, y, 50, Sentry, ...)</c> refuses a build near
    /// any sentry — the distance being to the sentry's <em>inclusive box</em>,
    /// boundary included (docs/SIMULATION.md, collision_circle).
    /// </summary>
    [Fact]
    public void AnEngineerShortOfStockOrTooCloseToASentryCannotBuild()
    {
        var state = default(WorldState);
        Put(ref state, 1, PlayerClass.Engineer, Team.Red, OpenX, OpenY);

        state.Characters[1].NutsNBolts = 99.9;
        Assert.False(Sentries.CanBuild(state, 1));

        state.Characters[1].NutsNBolts = 100;
        Assert.True(Sentries.CanBuild(state, 1));

        // The circle is centred on the builder at (2600, 708) and the sentry's
        // box top starts at 712, so the gap is Euclidean over both axes: a box
        // whose left edge is 49 away is sqrt(49² + 4²) = 49.16 in — refused —
        // and one pixel further is 50.16, past the radius.
        PutBuiltSentry(ref state, 2, Team.Red, OpenX + 57, FloorY);
        Assert.False(Sentries.CanBuild(state, 1));

        state.Sentries[2].X = OpenX + 58;
        Assert.True(Sentries.CanBuild(state, 1));

        // And never a second sentry for the same player.
        state.HasSentry[1] = true;
        state.Sentries[1] = SentryState.Build(Team.Red, OpenX - 300, OpenY, 1);
        Assert.False(Sentries.CanBuild(state, 1));
    }

    /// <summary>
    /// A placed sentry falls under the same 0.6 gravity as everything else and
    /// reports <c>SENTRY_POSITION</c> once, on the tick it stops.
    /// </summary>
    [Fact]
    public void AFreshSentryFallsLandsOnTheFloorAndAnnouncesItOnce()
    {
        var world = World;
        var state = default(WorldState);
        var events = default(TickEvents);

        Put(ref state, 1, PlayerClass.Engineer, Team.Red, OpenX, OpenY);
        Sentries.Build(ref state, 1, OpenX, OpenY, 1);

        var landings = 0;
        var landedTick = -1;

        for (var tick = 0; tick < 60; tick++)
        {
            Tick(world, ref state, ref events);

            if (events.SentryLanded[1])
            {
                landings++;
                landedTick = tick;
            }
        }

        Assert.Equal(1, landings);
        Assert.True(landedTick >= 0);

        // Resting: origin rounding to 719 puts the mask's bottom row on 731,
        // the last free row above the floor at 732. The fraction survives —
        // move_outside_solid keeps the fractional part of its start — and the
        // exact value pins the whole falling arithmetic: five ticks of 0.6
        // gravity from vspeed 0.001 leave y at 717.006, and the contact crawl's
        // (i/8)² back-off lands on this. ⚠️ The fixture landing gate cannot see
        // a wrong gravity constant on an eleven-pixel fall (0.6 and 0.7 land on
        // the same tick in the same wire bin — checked by mutation), so this
        // digit-exact value is the control that bites.
        Assert.Equal(0, state.Sentries[1].Motion.VSpeed);
        Assert.Equal(719.395625, state.Sentries[1].Y);
        Assert.Equal(OpenX, state.Sentries[1].X);
    }

    /// <summary>
    /// Construction: 1.2 hp per tick from 25, finishing <em>built</em> with hp
    /// set to exactly 100 — 64 ticks after the landing tick (63 increments
    /// overshoot to 100.6, and the 64th pass takes the else-branch).
    /// </summary>
    [Fact]
    public void ConstructionClimbsAtTheFullRateAndFinishesAtExactlyFullHp()
    {
        var world = World;
        var state = default(WorldState);
        var events = default(TickEvents);

        Put(ref state, 1, PlayerClass.Engineer, Team.Red, OpenX, OpenY);
        Sentries.Build(ref state, 1, OpenX, OpenY, 1);

        var landedTick = -1;
        var builtTick = -1;

        for (var tick = 0; tick < 150 && builtTick < 0; tick++)
        {
            Tick(world, ref state, ref events);

            if (events.SentryLanded[1])
                landedTick = tick;

            if (state.Sentries[1].Built && builtTick < 0)
                builtTick = tick;
        }

        Assert.True(landedTick >= 0);
        Assert.Equal(landedTick + 64, builtTick);
        Assert.Equal(SentryState.MaxHp, state.Sentries[1].Hp);
        Assert.True(state.Sentries[1].HasTurret);
    }

    /// <summary>
    /// <c>damageSentry.gml</c>: any damage drops the build rate to 0.6 — for
    /// good. The reference's comment about the rate ramping is beside a
    /// <c>min()</c> that can never move it.
    /// </summary>
    [Fact]
    public void DamageDuringConstructionSlowsTheBuildPermanently()
    {
        var world = World;
        var state = default(WorldState);
        var events = default(TickEvents);

        Put(ref state, 1, PlayerClass.Engineer, Team.Red, OpenX, OpenY);
        state.HasSentry[1] = true;
        state.Sentries[1] = SentryState.Build(Team.Red, OpenX, FloorY, 1);
        state.Sentries[1].Motion.VSpeed = 0;
        state.Sentries[1].OldVSpeed = 0;

        Sentries.Damage(ref state, 1, 10, dealer: 2, DamageSource.Scattergun);
        var hp = state.Sentries[1].Hp;
        Assert.Equal(SentryState.InitialHp - 10, hp);

        Tick(world, ref state, ref events);

        Assert.Equal(hp + SentryState.DamagedBuildRate, state.Sentries[1].Hp);
    }

    // =========================================================================
    // The turret
    // =========================================================================

    /// <summary>
    /// Acquiring a target out of idle costs <c>refireTime*2</c>: the first
    /// shot lands ten ticks after the turret first sees somebody, and 8 hp
    /// come off with the turret's own damage source.
    /// </summary>
    [Fact]
    public void TheTurretWaitsTenTicksAfterNoticingBeforeItsFirstShot()
    {
        var world = World;
        var state = default(WorldState);
        var events = default(TickEvents);

        PutBuiltSentry(ref state, 1, Team.Red, OpenX, FloorY);
        Put(ref state, 2, PlayerClass.Scout, Team.Blue, OpenX + 100, OpenY);

        var firstHit = -1;

        for (var tick = 1; tick <= 20 && firstHit < 0; tick++)
        {
            Tick(world, ref state, ref events);

            if (Gml.Less(state.Characters[2].Hp, 100))
                firstHit = tick;
        }

        Assert.Equal(11, firstHit);
        Assert.Equal(92.0, state.Characters[2].Hp);
        Assert.Equal(1, state.Characters[2].LastDamageDealer);
        Assert.Equal(DamageSource.SentryTurret, state.Characters[2].LastDamageSource);
    }

    /// <summary>Once warm, one shot every five ticks — the refire alarm.</summary>
    [Fact]
    public void TheTurretFiresEveryFiveTicksOnceWarm()
    {
        var world = World;
        var state = default(WorldState);
        var events = default(TickEvents);

        PutBuiltSentry(ref state, 1, Team.Red, OpenX, FloorY);
        Put(ref state, 2, PlayerClass.Heavy, Team.Blue, OpenX + 100, OpenY);

        var hitTicks = new List<int>();
        var hp = state.Characters[2].Hp;

        for (var tick = 1; tick <= 26; tick++)
        {
            Tick(world, ref state, ref events);

            if (Gml.Less(state.Characters[2].Hp, hp))
            {
                hitTicks.Add(tick);
                hp = state.Characters[2].Hp;
            }
        }

        Assert.Equal([11, 16, 21, 26], hitTicks);
    }

    /// <summary>
    /// The angle restriction: a target more than 45 degrees above the
    /// horizontal is invisible unless it stands on the gun.
    /// </summary>
    [Fact]
    public void TheTurretIgnoresATargetOutsideTheFortyFiveDegreeArc()
    {
        var world = World;
        var state = default(WorldState);
        var events = default(TickEvents);

        PutBuiltSentry(ref state, 1, Team.Red, OpenX, FloorY);

        // Straight up: 90 degrees, in range, in plain sight — and ignored.
        Put(ref state, 2, PlayerClass.Scout, Team.Blue, OpenX, FloorY - 200);
        state.Characters[2].Motion.VSpeed = 0;

        for (var tick = 0; tick < 15; tick++)
        {
            // Re-pin the falling scout so the geometry stays put.
            state.Characters[2].X = OpenX;
            state.Characters[2].Y = FloorY - 200;
            state.Characters[2].Motion.VSpeed = 0;
            state.Characters[2].Hp = 100;

            Tick(world, ref state, ref events);

            Assert.Equal(100.0, state.Characters[2].Hp);
        }
    }

    /// <summary>
    /// <c>collision_line_bulletblocking</c>: a target with the map between it
    /// and the turret is not a target. The control run, with the same angles
    /// and range but clear sight, fires — so a failure here is the wall test
    /// and not the arc.
    /// </summary>
    [Fact]
    public void TheTurretCannotShootThroughTheFloor()
    {
        var world = World;

        // Blocked: the line from (2600, 719) to (2900, 750) dips under the
        // floor mask at y = 732 long before it arrives.
        var state = default(WorldState);
        PutBuiltSentry(ref state, 1, Team.Red, OpenX, FloorY);
        Put(ref state, 2, PlayerClass.Scout, Team.Blue, OpenX + 300, 750);

        // Already warm on this target: skip the ten-tick notice delay so a
        // missing shot means the wall test and not the alert pause.
        state.Sentries[1].NoticedTarget = false;

        Sentries.TurretEndStep(world, ref state, 1, ref DiscardedEvents.Events);
        Assert.Equal(100.0, state.Characters[2].Hp);

        // Clear: the same target up on the floor is hit at once.
        var control = default(WorldState);
        PutBuiltSentry(ref control, 1, Team.Red, OpenX, FloorY);
        Put(ref control, 2, PlayerClass.Scout, Team.Blue, OpenX + 300, OpenY);
        control.Sentries[1].NoticedTarget = false;

        Sentries.TurretEndStep(world, ref control, 1, ref DiscardedEvents.Events);
        Assert.Equal(92.0, control.Characters[2].Hp);
    }

    /// <summary>
    /// The individual sentry occlusion test: any sentry that is neither the
    /// turret's own nor the target blocks the shot — a friendly one included.
    /// </summary>
    [Fact]
    public void AnotherSentryOnTheLineObscuresTheTarget()
    {
        var world = World;

        var state = default(WorldState);
        PutBuiltSentry(ref state, 1, Team.Red, OpenX, FloorY);
        PutBuiltSentry(ref state, 2, Team.Red, OpenX + 50, FloorY);
        state.Sentries[2].ReadyToShoot = false;
        Put(ref state, 3, PlayerClass.Scout, Team.Blue, OpenX + 100, OpenY);
        state.Sentries[1].NoticedTarget = false;

        Sentries.TurretEndStep(world, ref state, 1, ref DiscardedEvents.Events);
        Assert.Equal(100.0, state.Characters[3].Hp);

        // Without the blocker the same shot lands.
        var control = default(WorldState);
        PutBuiltSentry(ref control, 1, Team.Red, OpenX, FloorY);
        Put(ref control, 3, PlayerClass.Scout, Team.Blue, OpenX + 100, OpenY);
        control.Sentries[1].NoticedTarget = false;

        Sentries.TurretEndStep(world, ref control, 1, ref DiscardedEvents.Events);
        Assert.Equal(92.0, control.Characters[3].Hp);
    }

    /// <summary>
    /// A target behind the turret starts the flip: 0.3 of a frame per tick
    /// across four frames means the turn completes on the fourteenth turret
    /// step, and the held shot lands on the fifteenth.
    /// </summary>
    [Fact]
    public void TheTurretTurnsForFourteenTicksBeforeShootingBehindItself()
    {
        var world = World;
        var state = default(WorldState);
        var events = default(TickEvents);

        PutBuiltSentry(ref state, 1, Team.Red, OpenX, FloorY, facing: 1);
        Put(ref state, 2, PlayerClass.Scout, Team.Blue, OpenX - 100, OpenY);

        var firstHit = -1;

        for (var tick = 1; tick <= 25 && firstHit < 0; tick++)
        {
            // There is no floor at OpenX - 100, so the scout is re-pinned each
            // tick — the test is about the turret's flip, not the scout's fall.
            state.Characters[2].X = OpenX - 100;
            state.Characters[2].Y = OpenY;
            state.Characters[2].Motion.VSpeed = 0;

            Tick(world, ref state, ref events);

            if (Gml.Less(state.Characters[2].Hp, 100))
                firstHit = tick;
        }

        // Refire would have allowed tick 11; the flip (ceil(4/0.3) = 14 ticks)
        // finishes on tick 14 and the fire check runs before the flip block, so
        // the shot lands on 15.
        Assert.Equal(15, firstHit);
        Assert.Equal(-1, state.Sentries[1].TurretXScale);
    }

    /// <summary>
    /// An enemy <em>sentry</em> is a target too, and turret damage slows its
    /// repair like any other damage.
    /// </summary>
    [Fact]
    public void TheTurretShootsAnEnemySentry()
    {
        var world = World;
        var state = default(WorldState);

        PutBuiltSentry(ref state, 1, Team.Red, OpenX, FloorY);
        PutBuiltSentry(ref state, 2, Team.Blue, OpenX + 100, FloorY);
        state.Sentries[1].NoticedTarget = false;
        state.Sentries[2].ReadyToShoot = false;

        Sentries.TurretEndStep(world, ref state, 1, ref DiscardedEvents.Events);

        Assert.Equal(92.0, state.Sentries[2].Hp);
        Assert.Equal(SentryState.DamagedBuildRate, state.Sentries[2].BuildRate);
        Assert.Equal(1, state.Sentries[2].LastDamageDealer);
        Assert.Equal(DamageSource.SentryTurret, state.Sentries[2].LastDamageSource);
    }

    // =========================================================================
    // Projectiles against a sentry
    // =========================================================================

    /// <summary>
    /// <c>Shot.events/Collision with Sentry.xml</c>: an enemy pellet damages
    /// and dies; a friendly one passes straight through.
    /// </summary>
    [Fact]
    public void APelletDamagesAnEnemySentryAndPassesThroughAFriendlyOne()
    {
        var world = World;
        var events = default(TickEvents);

        var state = default(WorldState);
        PutBuiltSentry(ref state, 1, Team.Red, OpenX, FloorY);

        var enemyPellet = new ProjectileState
        {
            Kind = ProjectileKind.Shot,
            X = OpenX,
            Y = FloorY,
            OwnerSlot = 2,
            Team = Team.Blue,
            Source = DamageSource.Scattergun,
            HitDamage = 8,
            Lifetime = 30,
        };

        var handle = state.SpawnProjectile(enemyPellet);
        Tick(world, ref state, ref events);

        Assert.Equal(92.0, state.Sentries[1].Hp);
        Assert.Equal(2, state.Sentries[1].LastDamageDealer);
        Assert.Equal(DamageSource.Scattergun, state.Sentries[1].LastDamageSource);
        Assert.False(state.TryGetProjectile(handle, out _));

        var friendly = default(WorldState);
        PutBuiltSentry(ref friendly, 1, Team.Red, OpenX, FloorY);

        var ownPellet = enemyPellet with { Team = Team.Red };
        var ownHandle = friendly.SpawnProjectile(ownPellet);
        Tick(world, ref friendly, ref events);

        Assert.Equal(100.0, friendly.Sentries[1].Hp);
        Assert.True(friendly.TryGetProjectile(ownHandle, out _));
    }

    /// <summary>
    /// <c>Rocket.events/Collision with Sentry.xml</c>: the sentry becomes the
    /// direct victim and then takes the zero-distance splash on top —
    /// 25 + 30 = 55.
    /// </summary>
    [Fact]
    public void ARocketOnASentryDealsTheDirectHitPlusTheFullSplash()
    {
        var world = World;
        var events = default(TickEvents);
        var state = default(WorldState);

        PutBuiltSentry(ref state, 1, Team.Red, OpenX, FloorY);

        var rocket = new ProjectileState
        {
            Kind = ProjectileKind.Rocket,
            X = OpenX,
            Y = FloorY,
            OwnerSlot = 2,
            Team = Team.Blue,
            Source = DamageSource.Rocketlauncher,
            HitDamage = 25,
            Knockback = 8,
            DistanceToTravel = 800,
            LastKnownX = OpenX,
            LastKnownY = FloorY,
        };

        var handle = state.SpawnProjectile(rocket);
        Tick(world, ref state, ref events);

        Assert.Equal(45.0, state.Sentries[1].Hp);
        Assert.False(state.TryGetProjectile(handle, out _));
    }

    /// <summary>
    /// A mine sticks to an enemy sentry rather than exploding on it, and a
    /// mine's blast hits sentries half again as hard as characters —
    /// <c>explosionDamage*3/2</c>, the reference's own two-stickies-kill rule.
    /// </summary>
    [Fact]
    public void AMineSticksToAnEnemySentryAndItsBlastHitsItHalfAgainAsHard()
    {
        var world = World;
        var events = default(TickEvents);
        var state = default(WorldState);

        PutBuiltSentry(ref state, 1, Team.Red, OpenX, FloorY);

        var mine = new ProjectileState
        {
            Kind = ProjectileKind.Mine,
            Reflector = ProjectileState.NoOwner,
            X = OpenX,
            Y = FloorY - 2,
            OwnerSlot = 2,
            Team = Team.Blue,
            Source = DamageSource.Minegun,
            Knockback = 10,
            SplashThreshold = true,
        };

        var handle = state.SpawnProjectile(mine);
        Tick(world, ref state, ref events);

        Assert.True(state.TryGetProjectile(handle, out var stuck));
        Assert.True(stuck.Stickied);
        Assert.Equal(0, stuck.Motion.Speed);
        Assert.Equal(100.0, state.Sentries[1].Hp);

        Explosions.DetonateMine(world, ref state, handle, ref DiscardedEvents.Events);

        // Zero distance, zero falloff: 45 * 3/2.
        Assert.Equal(100.0 - 67.5, state.Sentries[1].Hp);
        Assert.Equal(DamageSource.Minegun, state.Sentries[1].LastDamageSource);
    }

    /// <summary>
    /// <c>Sentry.events/Destroy.xml</c> lets every mine stuck to the dying
    /// sentry drop to the floor.
    /// </summary>
    [Fact]
    public void AStuckMineDropsWhenItsSentryDies()
    {
        var world = World;
        var state = default(WorldState);

        PutBuiltSentry(ref state, 1, Team.Red, OpenX, FloorY);

        var mine = new ProjectileState
        {
            Kind = ProjectileKind.Mine,
            Reflector = ProjectileState.NoOwner,
            X = OpenX,
            Y = FloorY - 2,
            OwnerSlot = 2,
            Team = Team.Blue,
            Source = DamageSource.Minegun,
            Stickied = true,
        };

        var handle = state.SpawnProjectile(mine);
        Sentries.Destroy(world, ref state, 1);

        Assert.False(state.HasSentry[1]);
        Assert.True(state.TryGetProjectile(handle, out var dropped));
        Assert.False(dropped.Stickied);
    }

    /// <summary>
    /// <c>BurningProjectile.events/Collision with Sentry.xml</c> destroys the
    /// flame unconditionally on an enemy sentry — there is no penetration
    /// count and no <c>perseverant</c> exemption, unlike the character event.
    /// </summary>
    [Fact]
    public void AFlameBurnsASentryAndDiesOnItEvenWhenPerseverant()
    {
        var world = World;
        var events = default(TickEvents);
        var state = default(WorldState);

        PutBuiltSentry(ref state, 1, Team.Red, OpenX, FloorY);

        var flame = new ProjectileState
        {
            Kind = ProjectileKind.Flame,
            X = OpenX,
            Y = FloorY,
            OwnerSlot = 2,
            Team = Team.Blue,
            Source = DamageSource.Flamethrower,
            HitDamage = FlameStats.HitDamage(ProjectileKind.Flame),
            Lifetime = 10,
            Perseverant = true,
        };

        var handle = state.SpawnProjectile(flame);
        Tick(world, ref state, ref events);

        Assert.Equal(100.0 - FlameStats.HitDamage(ProjectileKind.Flame), state.Sentries[1].Hp);
        Assert.False(state.TryGetProjectile(handle, out _));
    }

    // =========================================================================
    // Destruction
    // =========================================================================

    /// <summary>
    /// A sentry out of hp dies in its End Step, and the <c>DESTROY_SENTRY</c>
    /// event names the killer and — like a character death — redirects the
    /// assist to the <em>killer's</em> medic.
    /// </summary>
    [Fact]
    public void ADestroyedSentryNamesItsKillerAndTheKillersMedic()
    {
        var world = World;
        var events = default(TickEvents);
        var state = default(WorldState);

        PutBuiltSentry(ref state, 1, Team.Red, OpenX, FloorY);
        state.Sentries[1].Hp = 5;

        Put(ref state, 2, PlayerClass.Scout, Team.Blue, OpenX + 200, OpenY);
        Put(ref state, 3, PlayerClass.Medic, Team.Blue, OpenX + 240, OpenY);
        state.Characters[2].Healer = 3;

        state.SpawnProjectile(new ProjectileState
        {
            Kind = ProjectileKind.Shot,
            X = OpenX,
            Y = FloorY,
            OwnerSlot = 2,
            Team = Team.Blue,
            Source = DamageSource.Scattergun,
            HitDamage = 8,
            Lifetime = 30,
        });

        Tick(world, ref state, ref events);

        Assert.False(state.HasSentry[1]);
        Assert.True(events.SentryDestroyed[1]);

        var destruction = events.SentryDestructions[1];
        Assert.Equal(2, destruction.Killer);
        Assert.Equal(3, destruction.Assistant);
        Assert.Equal(DamageSource.Scattergun, destruction.Source);
    }

    /// <summary>
    /// The Destroy event's self-knockback reaches only an <em>airborne</em>
    /// owner — <c>!place_meeting(x, y+1, Obstacle)</c> exempts anyone on the
    /// ground entirely.
    /// </summary>
    [Fact]
    public void TheDyingSentryShovesItsOwnerOnlyWhenAirborne()
    {
        var world = World;

        var grounded = default(WorldState);
        Put(ref grounded, 1, PlayerClass.Engineer, Team.Red, OpenX, OpenY);
        PutBuiltSentry(ref grounded, 1, Team.Red, OpenX, FloorY);

        Sentries.Destroy(world, ref grounded, 1);
        Assert.Equal(0, grounded.Characters[1].Motion.Speed);

        var airborne = default(WorldState);
        Put(ref airborne, 1, PlayerClass.Engineer, Team.Red, OpenX, OpenY - 60);
        PutBuiltSentry(ref airborne, 1, Team.Red, OpenX, FloorY);

        Sentries.Destroy(world, ref airborne, 1);
        Assert.True(Gml.Greater(airborne.Characters[1].Motion.Speed, 0));
    }

    /// <summary>
    /// ✅ <b>A sentry built inside a team gate dies on the same tick</b> —
    /// <c>Sentry.events/Collision with TeamGate.xml</c>, <c>hp -= 999</c>, and
    /// the End Step below it does the rest.
    /// </summary>
    /// <remarks>
    /// The gate used is <c>ctf_truefort</c>'s red one at
    /// [1578,444]-[1595,497], which is where the recording's t280 build died.
    /// Unlike the Begin Step guard this is a <em>same-tick</em> death: the
    /// collision stage and the End Step are both after Begin Step, so a sentry
    /// created during a tick never survives to the next one.
    /// </remarks>
    [Fact]
    public void ASentryStandingInATeamGateIsDestroyedByItsCollisionEvent()
    {
        var world = World;
        var events = default(TickEvents);
        var state = default(WorldState);

        PutBuiltSentry(ref state, 1, Team.Red, 1574.2, 474.4);

        Tick(world, ref state, ref events);

        Assert.False(state.HasSentry[1]);
        Assert.True(events.SentryDestroyed[1]);

        // ⚠️ Byte-identical to the Begin Step guard's destruction, and that is
        // the whole reason the recorded ones were misattributed for five
        // sessions: lastDamageSource stays at Create's -1, which write_ubyte
        // puts on the wire as 0.
        var destruction = events.SentryDestructions[1];
        Assert.Equal(CharacterState.Nobody, destruction.Killer);
        Assert.Equal(CharacterState.Nobody, destruction.Assistant);
        Assert.Equal(DamageSource.None, destruction.Source);
    }

    /// <summary>
    /// ⚠️ <b>Its own team's gate kills it too.</b> Neither collision event
    /// looks at <c>team</c>.
    /// </summary>
    /// <remarks>
    /// Three of the five in <c>reference-ctf_truefort-closedsentryteam.trace</c>
    /// are an engineer's sentry in his own doorway, so this is the common case
    /// rather than the corner one. The control is the same gate two hundred
    /// pixels to the left, where the sentry lives.
    /// </remarks>
    [Fact]
    public void AGateKillsASentryOfItsOwnTeamAndSpareOneStandingClear()
    {
        var world = World;

        var inGate = default(WorldState);
        var gateEvents = default(TickEvents);
        PutBuiltSentry(ref inGate, 1, Team.Red, 1586, 470);
        Tick(world, ref inGate, ref gateEvents);
        Assert.False(inGate.HasSentry[1]);

        var clear = default(WorldState);
        var clearEvents = default(TickEvents);
        PutBuiltSentry(ref clear, 1, Team.Red, 1386, 470);
        Tick(world, ref clear, ref clearEvents);
        Assert.True(clear.HasSentry[1]);
        Assert.False(clearEvents.SentryDestroyed[1]);
    }

    /// <summary>
    /// The Begin Step guard: two sentries too close together — one fell onto
    /// the other — and the first to step destroys itself, killer nobody,
    /// source 0.
    /// </summary>
    [Fact]
    public void OverlappingSentriesResolveByTheFirstDestroyingItself()
    {
        var world = World;
        var events = default(TickEvents);
        var state = default(WorldState);

        PutBuiltSentry(ref state, 1, Team.Red, OpenX, FloorY);
        PutBuiltSentry(ref state, 2, Team.Blue, OpenX + 10, FloorY);

        Tick(world, ref state, ref events);

        Assert.False(state.HasSentry[1]);
        Assert.True(state.HasSentry[2]);

        Assert.True(events.SentryDestroyed[1]);
        var destruction = events.SentryDestructions[1];
        Assert.Equal(CharacterState.Nobody, destruction.Killer);
        Assert.Equal(CharacterState.Nobody, destruction.Assistant);
        Assert.Equal(DamageSource.None, destruction.Source);
    }

    /// <summary>
    /// ✅ <b>A sentry whose owner is dropped to SPECTATOR scraps itself</b> —
    /// the second clause, reached by a roster reset rather than a side switch.
    /// </summary>
    /// <remarks>
    /// <c>Sentry.events/Begin Step.xml:16</c>, <c>team != ownerPlayer.team</c>.
    /// <c>PlayerLifecycleTests.AnEngineerChangingTeamLosesTheSentryATickLater</c>
    /// already covers red-to-blue; this is the other way into the same clause,
    /// and it is the one that happens without anybody choosing it.
    /// <para>
    /// ⚠️ <b>Reproduced live 2026-08-28, by accident.</b> A sentry built by a lone
    /// player on a dedicated server died one tick after the server reset that
    /// player's roster entry to <c>TEAM_SPECTATOR</c> — the same reset
    /// <c>CLAUDE.md</c> records the server doing on a map change. A sentry's own
    /// team is copied once at build (<c>buildSentry.gml</c>) and never updated,
    /// so <b>the clause is an inequality, not a red-versus-blue test</b>:
    /// spectator orphans a sentry exactly as switching sides does.
    /// </para>
    /// <para>
    /// ⚠️ <b>This is also why a live "reproduction" of the recorded self-destructs
    /// was wrong.</b> Building at the trace's exact coordinates killed the sentry
    /// and looked like the answer; the control at a position the trace says works
    /// killed it identically, because both deaths were this roster reset.
    /// <b>Position is eliminated</b> — see <c>SentryKillFixtureTests</c>' open
    /// finding.
    /// </para>
    /// </remarks>
    [Fact]
    public void ASentryWhoseOwnerIsDroppedToSpectatorScrapsItself()
    {
        var world = World;
        var state = default(WorldState);
        var events = default(TickEvents);

        Put(ref state, 1, PlayerClass.Engineer, Team.Red, OpenX, OpenY);
        PutBuiltSentry(ref state, 1, Team.Red, OpenX, FloorY);

        // The control: an engineer of the sentry's own team keeps it.
        Tick(world, ref state, ref events);
        Assert.True(state.HasSentry[1]);
        Assert.False(events.SentryDestroyed[1]);

        state.Players[1].Team = Team.Spectator;
        Tick(world, ref state, ref events);

        Assert.False(state.HasSentry[1]);
        Assert.True(events.SentryDestroyed[1]);

        // The signature the wire carries: DESTROY_SENTRY [slot, 255, 255, 0].
        var death = events.SentryDestructions[1];
        Assert.Equal(CharacterState.Nobody, death.Killer);
        Assert.Equal(CharacterState.Nobody, death.Assistant);
        Assert.Equal(DamageSource.None, death.Source);
    }
}
