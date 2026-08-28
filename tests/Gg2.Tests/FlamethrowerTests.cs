using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// The Flamethrower, the flame and afterburn: phase 5 stage 8.
/// </summary>
/// <remarks>
/// <b>No recording gates any of this, and one of the fixtures explains why it
/// matters.</b> `reference-ctf_truefort-closedsupport.trace` disagrees with the
/// port about hp from tick 66, hundreds of ticks before its anchor, because a
/// pyro is burning people the port could not burn. These tests are the gate, and
/// each was checked by breaking the line it is about.
/// <para>
/// Transcribed from <c>Objects/Weapons/Flamethrower.events/{Create, Begin Step,
/// Alarm 5, User Event 1}.xml</c>,
/// <c>Objects/Projectiles/{Flame, BurningProjectile}.events/*.xml</c>, the
/// <c>with(BurningProjectile)</c> block of
/// <c>Scripts/Physics/System/move_all_bullets.gml</c> and
/// <c>Character.events/{Create, Alarm 0}.xml</c> plus lines 41-70 of its
/// <c>Begin Step</c>.
/// </para>
/// </remarks>
public sealed class FlamethrowerTests
{
    private const string Map = "ctf_truefort";

    /// <summary>A standing position: the floor is directly under it.</summary>
    private const double OpenX = 2600;

    private const double OpenY = 708;

    /// <summary><c>Flame.events/Create.xml</c>.</summary>
    private const double FlameDamage = 3.14;

    /// <summary>What one flame costs the tank, and the threshold to fire.</summary>
    private const double AmmoCost = 1.8;

    private static CollisionWorld? _sharedWorld;

    private static CollisionWorld World => _sharedWorld ??=
        new CollisionWorld(LevelMap.FromPng(Map, ReferenceTree.ReadMap(Map)));

    private static void Put(
        ref WorldState state,
        int slot,
        PlayerClass playerClass,
        Team team,
        double x,
        double y,
        PlayerKeys keys = PlayerKeys.None,
        double aimDegrees = 0)
    {
        var character = CharacterState.Fresh(playerClass, team);
        character.X = x;
        character.Y = y;
        character.NetAimDirection = (ushort)Math.Round(aimDegrees * 65536.0 / 360.0);
        character.KeyState = keys;

        state.Characters[slot] = character;
        state.Weapons[slot] = WeaponState.Spawn(playerClass, x, y);
        state.Weapons[slot].ReadyToShoot = true;
        state.Weapons[slot].RefireTimer = WeaponState.Inactive;
        state.Alive[slot] = true;
    }

    /// <summary>A flame as the weapon makes one, placed by hand.</summary>
    private static ProjectileState Flame(
        double x,
        double y,
        int ownerSlot,
        Team team,
        double direction = 0,
        double speed = 8,
        int lifetime = 15,
        bool perseverant = false)
    {
        var flame = new ProjectileState
        {
            Kind = ProjectileKind.Flame,
            X = x,
            Y = y,
            OwnerSlot = (byte)ownerSlot,
            Team = team,
            Source = DamageSource.Flamethrower,
            HitDamage = FlameDamage,
            Lifetime = lifetime,
            Perseverant = perseverant,
        };

        flame.Motion.Direction = direction;
        flame.ImageAngle = direction;
        flame.Motion.Speed = speed;

        return flame;
    }

    private static ProjectileState Live(in WorldState state, int index) =>
        state.Projectiles[state.LiveProjectileSlot(index)];

    // --- the weapon -----------------------------------------------------------

    /// <summary>One flame, twenty-five pixels ahead of the weapon.</summary>
    [Fact]
    public void TheFlamethrowerMakesOneFlameAheadOfTheWeapon()
    {
        var state = default(WorldState);
        var events = default(TickEvents);

        Put(ref state, 1, PlayerClass.Pyro, Team.Red, OpenX, OpenY, PlayerKeys.Attack);

        Weapons.FireStep(World, ref state, 1, ref events);

        Assert.Equal(1, state.LiveProjectileCount);

        var flame = Live(state, 0);
        Assert.Equal(ProjectileKind.Flame, flame.Kind);
        Assert.Equal(OpenX + 25, flame.X);
        Assert.Equal(OpenY, flame.Y);
        Assert.Equal(DamageSource.Flamethrower, flame.Source);
        Assert.Equal(FlameDamage, flame.HitDamage);
        Assert.Equal(15, flame.Lifetime);

        // 6.5 + random(3.5), and the spread is sign(random(2)-1)*power(random(3), 1.8),
        // so the heading is the aim plus at most 3^1.8 either way.
        Assert.InRange(flame.Motion.Speed, 6.5, 10);

        // The heading wraps, so measure the spread as a signed offset from the
        // aim rather than as an interval.
        var spread = ((flame.Motion.Direction + 180) % 360) - 180;
        Assert.InRange(spread, -Math.Pow(3, 1.8), Math.Pow(3, 1.8));

        Assert.Equal(200 - AmmoCost, state.Weapons[1].AmmoCount);
        Assert.False(state.Weapons[1].ReadyToShoot);
        Assert.False(state.Weapons[1].IsRefilling);
        Assert.Equal(1, state.Weapons[1].RefireTimer);
        Assert.Equal(7, state.Weapons[1].ReloadTimer);
    }

    /// <summary>
    /// A flame inherits the pyro's whole velocity, not just its hspeed.
    /// </summary>
    /// <remarks>
    /// <c>with(shot) motion_add(owner.direction, owner.speed)</c> — where a
    /// pellet and a needle both take <c>hspeed</c> alone. A falling pyro adds
    /// their fall to every flame, which a horizontal-only inheritance would miss
    /// entirely.
    /// </remarks>
    [Fact]
    public void AFlameInheritsTheWholeOwnerVelocity()
    {
        // Differential, and it has to be: the flame's own random spread tilts it
        // off horizontal by a fraction of a degree, which puts a few thousandths
        // of vspeed on it whatever the owner is doing. Two fires from the same
        // random stream draw the same spread and the same speed, so the
        // difference between them is exactly what the owner contributed.
        Assert.Equal(5, Fired(vspeed: 5) - Fired(vspeed: 0), 9);

        static double Fired(double vspeed)
        {
            var state = default(WorldState);
            var events = default(TickEvents);

            Put(ref state, 1, PlayerClass.Pyro, Team.Red, OpenX, OpenY - 200, PlayerKeys.Attack);
            state.Characters[1].VSpeed = vspeed;

            Weapons.FireStep(World, ref state, 1, ref events);
            return Live(state, 0).Motion.VSpeed;
        }
    }

    /// <summary>A flame is not fired through a wall.</summary>
    [Fact]
    public void AFlameIsNotFiredThroughAWall()
    {
        var state = default(WorldState);
        var events = default(TickEvents);

        // Straight down, into the floor the pyro is standing on.
        Put(ref state, 1, PlayerClass.Pyro, Team.Red, OpenX, OpenY, PlayerKeys.Attack, aimDegrees: 270);

        Assert.True(World.CollisionLineHitsMask(OpenX, OpenY, OpenX, OpenY + 25));

        Weapons.FireStep(World, ref state, 1, ref events);

        Assert.Equal(0, state.LiveProjectileCount);

        // And the shot was not charged for: readyToShoot and the tank are
        // untouched, because the guard is above every one of those writes.
        Assert.True(state.Weapons[1].ReadyToShoot);
        Assert.Equal(200, state.Weapons[1].AmmoCount);
    }

    /// <summary>
    /// An empty tank is driven further negative on every tick the trigger is
    /// held.
    /// </summary>
    /// <remarks>
    /// ⚠️ The reference's comment is "prevent sputtering", and the line sits
    /// <b>above</b> the ready-and-loaded guard so it runs whether or not anything
    /// is fired. Without it a tank hovering just under 1.8 would refill to just
    /// over it and fire one frame in every few; with it the tank has to climb out
    /// of a hole first.
    /// </remarks>
    [Fact]
    public void AnEmptyTankSputtersFurtherNegativeEveryTick()
    {
        var state = default(WorldState);
        var events = default(TickEvents);

        Put(ref state, 1, PlayerClass.Pyro, Team.Red, OpenX, OpenY, PlayerKeys.Attack);
        state.Weapons[1].AmmoCount = 1;

        Weapons.FireStep(World, ref state, 1, ref events);
        Assert.Equal(0, state.LiveProjectileCount);
        Assert.Equal(1 - AmmoCost, state.Weapons[1].AmmoCount);

        Weapons.FireStep(World, ref state, 1, ref events);
        Assert.Equal(1 - AmmoCost - AmmoCost, state.Weapons[1].AmmoCount);
    }

    /// <summary>
    /// The tank refills to <b>201.8</b> and stops there — not to 200.
    /// </summary>
    /// <remarks>
    /// ⚠️ The guard is <c>ammoCount &lt;= maxAmmo</c>, so a full tank is topped
    /// up once more and then fails the test. A pyro who has fired once at any
    /// point in the match rests at 201.8 for the rest of it, and the wire carries
    /// <b>202</b> rather than 200.
    /// </remarks>
    [Fact]
    public void TheTankOverfillsToTwoHundredAndOnePointEight()
    {
        var state = default(WorldState);
        Put(ref state, 1, PlayerClass.Pyro, Team.Red, OpenX, OpenY);
        // Five flames down, which is the only kind of tank the game produces:
        // it starts full and is only ever drained 1.8 at a time.
        state.Weapons[1].AmmoCount = 200 - (5 * AmmoCost);
        state.Weapons[1].IsRefilling = true;

        for (var tick = 0; tick < 40; tick++)
            Weapons.WeaponBeginStep(ref state.Weapons[1], PlayerClass.Pyro);

        Assert.Equal(201.8, state.Weapons[1].AmmoCount, 9);
        Assert.Equal(202, CharacterWire.ToBlock(state.Characters[1], state.Weapons[1]).Ammo);
    }

    /// <summary>
    /// A negative tank is clamped rather than refilled, and the two are an
    /// <c>else if</c>.
    /// </summary>
    [Fact]
    public void AClampedTankIsNotAlsoRefilledOnTheSameTick()
    {
        var state = default(WorldState);
        Put(ref state, 1, PlayerClass.Pyro, Team.Red, OpenX, OpenY);
        state.Weapons[1].AmmoCount = -3;
        state.Weapons[1].IsRefilling = true;

        Weapons.WeaponBeginStep(ref state.Weapons[1], PlayerClass.Pyro);
        Assert.Equal(0, state.Weapons[1].AmmoCount);

        Weapons.WeaponBeginStep(ref state.Weapons[1], PlayerClass.Pyro);
        Assert.Equal(AmmoCost, state.Weapons[1].AmmoCount);
    }

    /// <summary>
    /// Firing stops the refill, and <c>alarm[5]</c> restarts it seven ticks
    /// later.
    /// </summary>
    /// <remarks>
    /// So a pyro holding the trigger never refills at all — the alarm is pushed
    /// back to 7 by every flame.
    /// </remarks>
    [Fact]
    public void TheTankOnlyRefillsSevenTicksAfterTheLastFlame()
    {
        var state = default(WorldState);
        Put(ref state, 1, PlayerClass.Pyro, Team.Red, OpenX, OpenY);
        state.Weapons[1].IsRefilling = true;
        state.Weapons[1].ReloadTimer = 7;

        for (var tick = 0; tick < 6; tick++)
        {
            Timers.Advance(ref state);
            Assert.True(state.Weapons[1].IsRefilling);
        }

        state.Weapons[1].IsRefilling = false;
        Timers.Advance(ref state);
        Assert.True(state.Weapons[1].IsRefilling);
        Assert.Equal(Timers.JustFired, state.Weapons[1].ReloadTimer);
    }

    /// <summary>A tank that just ran dry waits fourteen ticks, not one.</summary>
    [Fact]
    public void AnEmptyTankWaitsFourteenTicksNotOne()
    {
        var state = default(WorldState);
        var events = default(TickEvents);

        Put(ref state, 1, PlayerClass.Pyro, Team.Red, OpenX, OpenY, PlayerKeys.Attack);
        state.Weapons[1].AmmoCount = AmmoCost;

        Weapons.FireStep(World, ref state, 1, ref events);

        Assert.Equal(1, state.LiveProjectileCount);
        Assert.Equal(0, state.Weapons[1].AmmoCount);
        Assert.Equal(14, state.Weapons[1].RefireTimer);
    }

    // --- the flame ------------------------------------------------------------

    /// <summary>A flame falls at a pellet's 0.15, not a needle's 0.2.</summary>
    [Fact]
    public void AFlameFallsAtAPelletsGravity()
    {
        var state = default(WorldState);
        Put(ref state, 1, PlayerClass.Pyro, Team.Red, OpenX, OpenY - 200);
        state.SpawnProjectile(Flame(OpenX, OpenY - 200, 1, Team.Red, speed: 0));

        Projectiles.Move(World, ref state, ref DiscardedEvents.Events);

        Assert.Equal(0.15, Live(state, 0).Motion.VSpeed);
    }

    /// <summary>
    /// A flame substeps six pixels at a time, so it cannot tunnel a thin wall.
    /// </summary>
    /// <remarks>
    /// The distinguishing case: fire one straight down from just above the
    /// floor at a speed that would clear it in a single jump. A single-jump
    /// block would land past the floor and find open air; the substep walk stops
    /// inside it.
    /// </remarks>
    [Fact]
    public void AFlameSubstepsAndStopsInTheFirstSolidHop()
    {
        var floor = FloorBelow((int)OpenX, (int)OpenY);

        var state = default(WorldState);
        Put(ref state, 1, PlayerClass.Pyro, Team.Red, OpenX, OpenY);

        // Twelve pixels above the floor, moving down at 30 — two substeps get
        // there and a single jump would sail past.
        state.SpawnProjectile(Flame(OpenX, floor - 12, 1, Team.Red, direction: 270, speed: 30));

        Projectiles.Move(World, ref state, ref DiscardedEvents.Events);

        Assert.Equal(0, state.LiveProjectileCount);
    }

    /// <summary>A flame that lands in a wall dies there.</summary>
    [Fact]
    public void AFlameDiesInAWall()
    {
        var floor = FloorBelow((int)OpenX, (int)OpenY);

        var state = default(WorldState);
        Put(ref state, 1, PlayerClass.Pyro, Team.Red, OpenX, OpenY);
        state.SpawnProjectile(Flame(OpenX, floor + 4, 1, Team.Red, speed: 0));

        Projectiles.Move(World, ref state, ref DiscardedEvents.Events);

        Assert.Equal(0, state.LiveProjectileCount);
    }

    // --- burning somebody -----------------------------------------------------

    /// <summary>
    /// A flame deals 3.14 and sets the victim alight.
    /// </summary>
    [Fact]
    public void AFlameDealsItsDamageAndSetsThemAlight()
    {
        var state = default(WorldState);
        Put(ref state, 1, PlayerClass.Pyro, Team.Red, OpenX, OpenY);
        Put(ref state, 2, PlayerClass.Soldier, Team.Blue, OpenX + 40, OpenY);

        // Lifetime 15 is a flame that has not moved yet: the freshest there is.
        state.SpawnProjectile(Flame(OpenX + 40, OpenY, 1, Team.Red));

        Projectiles.Collide(World, ref state, ref DiscardedEvents.Events);

        ref var victim = ref state.Characters[2];
        Assert.Equal(160 - FlameDamage, victim.Hp);
        Assert.Equal(30, victim.BurnDuration);
        Assert.Equal(1, victim.BurnedBy);
        Assert.Equal(DamageSource.Flamethrower, victim.AfterburnSource);
        Assert.Equal(90, victim.AfterburnDecayTimer);
        Assert.Equal(1, victim.LastDamageDealer);
        Assert.Equal(DamageSource.Flamethrower, victim.LastDamageSource);
        Assert.Equal(0, victim.TimeUnscathed);

        // 1.35 * (0*0.65 + 0.35) — the falloff at maximum freshness.
        Assert.Equal(1.35 * 0.35, victim.BurnIntensity, 12);
    }

    /// <summary>
    /// ⚠️ A <em>fresh</em> flame adds less heat than an old one, which is the
    /// opposite of what the reference's comment beside it says.
    /// </summary>
    /// <remarks>
    /// <c>falloffAmount = min(1, (flameLife - alarm[0]) / flameLife)</c> is small
    /// when <c>alarm[0]</c> is large, i.e. when the flame is young. The comment
    /// claims the reverse ("value higher when life is fresh"). The code is the
    /// specification.
    /// </remarks>
    [Fact]
    public void AnOldFlameAddsMoreHeatThanAFreshOne()
    {
        var fresh = Burn(lifetime: 15);
        var spent = Burn(lifetime: 0);

        Assert.Equal(1.35 * 0.35, fresh, 12);
        Assert.Equal(1.35 * 1.0, spent, 12);
        Assert.True(spent > fresh);

        static double Burn(int lifetime)
        {
            var state = default(WorldState);
            Put(ref state, 1, PlayerClass.Pyro, Team.Red, OpenX, OpenY);
            Put(ref state, 2, PlayerClass.Soldier, Team.Blue, OpenX + 40, OpenY);
            state.SpawnProjectile(Flame(OpenX + 40, OpenY, 1, Team.Red, lifetime: lifetime));

            Projectiles.Collide(World, ref state, ref DiscardedEvents.Events);
            return state.Characters[2].BurnIntensity;
        }
    }

    /// <summary>Above the "leg" of 7, heat additions are halved.</summary>
    [Fact]
    public void AboveTheLegTheAdditionsAreHalved()
    {
        var state = default(WorldState);
        Put(ref state, 1, PlayerClass.Pyro, Team.Red, OpenX, OpenY);
        Put(ref state, 2, PlayerClass.Soldier, Team.Blue, OpenX + 40, OpenY);
        state.Characters[2].BurnIntensity = 7.5;

        state.SpawnProjectile(Flame(OpenX + 40, OpenY, 1, Team.Red, lifetime: 0));

        Projectiles.Collide(World, ref state, ref DiscardedEvents.Events);

        Assert.Equal(7.5 + (1.35 / 2), state.Characters[2].BurnIntensity, 12);
    }

    /// <summary>Both ceilings, applied after both additions.</summary>
    [Fact]
    public void HeatAndFuelAreBothCapped()
    {
        var state = default(WorldState);
        Put(ref state, 1, PlayerClass.Pyro, Team.Red, OpenX, OpenY);
        Put(ref state, 2, PlayerClass.Soldier, Team.Blue, OpenX + 40, OpenY);
        state.Characters[2].BurnIntensity = 12.9;
        state.Characters[2].BurnDuration = 205;

        state.SpawnProjectile(Flame(OpenX + 40, OpenY, 1, Team.Red, lifetime: 0));

        Projectiles.Collide(World, ref state, ref DiscardedEvents.Events);

        Assert.Equal(CharacterState.MaxBurnIntensity, state.Characters[2].BurnIntensity);
        Assert.Equal(CharacterState.MaxBurnDuration, state.Characters[2].BurnDuration);
    }

    /// <summary>
    /// A flame burns one person and dies on the <em>second</em>.
    /// </summary>
    /// <remarks>
    /// ⚠️ The cap is compared before the victim is recorded, so an empty list
    /// passes <c>0 &gt;= 1</c> as false and the first victim is survived.
    /// </remarks>
    [Fact]
    public void AFlameBurnsOnePersonAndDiesOnTheSecond()
    {
        var state = default(WorldState);
        Put(ref state, 1, PlayerClass.Pyro, Team.Red, OpenX, OpenY);
        Put(ref state, 2, PlayerClass.Soldier, Team.Blue, OpenX + 40, OpenY);
        Put(ref state, 3, PlayerClass.Soldier, Team.Blue, OpenX + 40, OpenY);

        state.SpawnProjectile(Flame(OpenX + 40, OpenY, 1, Team.Red));

        Projectiles.Collide(World, ref state, ref DiscardedEvents.Events);

        // Both were reached in the same pass: slot 2 burned and survived the
        // flame, slot 3 burned and killed it.
        Assert.Equal(160 - FlameDamage, state.Characters[2].Hp);
        Assert.Equal(160 - FlameDamage, state.Characters[3].Hp);
        Assert.Equal(0, state.LiveProjectileCount);
    }

    /// <summary>A perseverant flame is never destroyed by burning anybody.</summary>
    [Fact]
    public void APerseverantFlameBurnsEverybodyAndLives()
    {
        var state = default(WorldState);
        Put(ref state, 1, PlayerClass.Pyro, Team.Red, OpenX, OpenY);
        Put(ref state, 2, PlayerClass.Soldier, Team.Blue, OpenX + 40, OpenY);
        Put(ref state, 3, PlayerClass.Soldier, Team.Blue, OpenX + 40, OpenY);

        state.SpawnProjectile(Flame(OpenX + 40, OpenY, 1, Team.Red, perseverant: true));

        Projectiles.Collide(World, ref state, ref DiscardedEvents.Events);

        Assert.Equal(160 - FlameDamage, state.Characters[2].Hp);
        Assert.Equal(160 - FlameDamage, state.Characters[3].Hp);
        Assert.Equal(1, state.LiveProjectileCount);
    }

    /// <summary>The same flame does not burn the same person twice.</summary>
    [Fact]
    public void AFlameBurnsTheSamePersonOnlyOnce()
    {
        var state = default(WorldState);
        Put(ref state, 1, PlayerClass.Pyro, Team.Red, OpenX, OpenY);
        Put(ref state, 2, PlayerClass.Soldier, Team.Blue, OpenX + 40, OpenY);

        state.SpawnProjectile(Flame(OpenX + 40, OpenY, 1, Team.Red, perseverant: true, speed: 0));

        Projectiles.Collide(World, ref state, ref DiscardedEvents.Events);
        var afterFirst = state.Characters[2].Hp;

        Projectiles.Collide(World, ref state, ref DiscardedEvents.Events);

        Assert.Equal(afterFirst, state.Characters[2].Hp);
        Assert.Equal(1, state.LiveProjectileCount);
    }

    /// <summary>A team-mate, an ubered enemy and a corpse are all skipped.</summary>
    [Theory]
    [InlineData("teammate")]
    [InlineData("ubered")]
    [InlineData("dead")]
    public void AFlameSkipsWhoItShould(string which)
    {
        var state = default(WorldState);
        Put(ref state, 1, PlayerClass.Pyro, Team.Red, OpenX, OpenY);
        Put(ref state, 2, PlayerClass.Soldier, which == "teammate" ? Team.Red : Team.Blue, OpenX + 40, OpenY);

        if (which == "ubered")
            state.Characters[2].Ubered = true;
        if (which == "dead")
            state.Characters[2].Hp = 0;

        var before = state.Characters[2].Hp;
        state.SpawnProjectile(Flame(OpenX + 40, OpenY, 1, Team.Red));

        Projectiles.Collide(World, ref state, ref DiscardedEvents.Events);

        Assert.Equal(before, state.Characters[2].Hp);
        Assert.Equal(0, state.Characters[2].BurnDuration);

        // And not a destroy: the flame falls off the end of the event.
        Assert.Equal(1, state.LiveProjectileCount);
    }

    // --- afterburn ------------------------------------------------------------

    /// <summary>Afterburn deals the intensity over thirty, once a tick.</summary>
    [Fact]
    public void AfterburnDealsTheIntensityOverThirtyEachTick()
    {
        var state = default(WorldState);
        Put(ref state, 1, PlayerClass.Soldier, Team.Blue, OpenX, OpenY);
        state.Characters[1].BurnIntensity = 6;
        state.Characters[1].BurnDuration = 60;
        state.Characters[1].BurnedBy = 2;
        state.Characters[1].AfterburnSource = DamageSource.Flamethrower;
        state.Characters[1].AfterburnDecayTimer = 90;

        Afterburn.BeginStep(ref state, 1, ref DiscardedEvents.Events);

        Assert.Equal(160 - (6.0 / 30), state.Characters[1].Hp, 12);
        Assert.Equal(59, state.Characters[1].BurnDuration);
        Assert.Equal(0, state.Characters[1].TimeUnscathed);
        Assert.Equal(2, state.Characters[1].LastDamageDealer);
        Assert.Equal(DamageSource.Flamethrower, state.Characters[1].LastDamageSource);

        // The decay delay is still running, so no heat has been lost.
        Assert.Equal(6, state.Characters[1].BurnIntensity);
    }

    /// <summary>
    /// A fire on somebody already at zero hp deals no damage and takes no
    /// credit.
    /// </summary>
    /// <remarks>
    /// The reference cites its own bug number for this guard: "Don't count if
    /// someone else already finished him off, to prevent afterburn from
    /// 'stealing' sticky kills (Bug #1021989)." The fuel still burns.
    /// </remarks>
    [Fact]
    public void AfterburnDoesNotStealAKillFromSomebodyElse()
    {
        var state = default(WorldState);
        Put(ref state, 1, PlayerClass.Soldier, Team.Blue, OpenX, OpenY);
        state.Characters[1].Hp = 0;
        state.Characters[1].BurnIntensity = 6;
        state.Characters[1].BurnDuration = 60;
        state.Characters[1].BurnedBy = 2;
        state.Characters[1].LastDamageDealer = 3;
        state.Characters[1].LastDamageSource = DamageSource.Minegun;

        Afterburn.BeginStep(ref state, 1, ref DiscardedEvents.Events);

        Assert.Equal(0, state.Characters[1].Hp);
        Assert.Equal(3, state.Characters[1].LastDamageDealer);
        Assert.Equal(DamageSource.Minegun, state.Characters[1].LastDamageSource);

        // The fuel is still consumed — the guard is only around the damage.
        Assert.Equal(59, state.Characters[1].BurnDuration);
    }

    /// <summary>
    /// The heat only starts fading ninety ticks after the last flame lands.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ The gate is <c>alarm[0] == -1</c> <b>exactly</b>, not <c>&lt;= 0</c>,
    /// and <c>Character.events/Begin Step.xml:67</c> runs <em>before</em> the
    /// alarm stage. So the fade does not start on the tick after the alarm
    /// fires: that tick's Begin Step still reads the 0 the alarm was left at,
    /// and only the tick after <em>that</em> sees -1.
    /// </para>
    /// <para>
    /// ⚠️ <b>This test used to assert the opposite, and its remark used to give
    /// the reason.</b> It read "that is what forced <c>Timers.Advance</c> to
    /// leave a fired counter at -1 rather than 0 — at 0 the decay would start a
    /// tick late, every time." A tick late is exactly what the running game
    /// does: measured 2026-08-27, a fired GM8 alarm reads 0 for the rest of its
    /// own frame. The port was starting every burn's decay a tick early, and
    /// this is the one test that could see it.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheHeatOnlyFadesNinetyTicksAfterTheLastFlame()
    {
        var state = default(WorldState);
        Put(ref state, 1, PlayerClass.Soldier, Team.Blue, OpenX, OpenY);
        state.Characters[1].BurnIntensity = 9;
        state.Characters[1].BurnDuration = 210;
        state.Characters[1].BurnedBy = 2;
        state.Characters[1].AfterburnSource = DamageSource.Flamethrower;
        state.Characters[1].AfterburnDecayTimer = CharacterState.BurnDecayDelay;

        var idle = new PlayerInput[8];

        // Ninety ticks of alarm 0. It fires on the ninetieth and the Alarm 0
        // body computes intensityDecay from whatever heat is left.
        for (var tick = 0; tick < 90; tick++)
        {
            MatchSimulation.Tick(World, ref state, idle);
            Assert.Equal(9, state.Characters[1].BurnIntensity);
        }

        Assert.Equal(Timers.JustFired, state.Characters[1].AfterburnDecayTimer);
        Assert.Equal(9.0 / CharacterState.BurnDecayDuration, state.Characters[1].IntensityDecay, 12);

        // ⚠️ The next tick's Begin Step reads the 0 the alarm was left at, so it
        // does NOT fade — and that tick's own alarm stage is what takes the
        // counter to -1.
        MatchSimulation.Tick(World, ref state, idle);
        Assert.Equal(9, state.Characters[1].BurnIntensity);
        Assert.Equal(Timers.Inactive, state.Characters[1].AfterburnDecayTimer);

        // The tick after that sees -1 and starts the fade.
        MatchSimulation.Tick(World, ref state, idle);
        Assert.Equal(9 - (9.0 / 90), state.Characters[1].BurnIntensity, 12);
    }

    /// <summary>
    /// A flame landing pushes the delay back, so a sprayed victim never fades.
    /// </summary>
    [Fact]
    public void EveryFlameRestartsTheDecayDelay()
    {
        var state = default(WorldState);
        Put(ref state, 1, PlayerClass.Pyro, Team.Red, OpenX, OpenY);
        Put(ref state, 2, PlayerClass.Soldier, Team.Blue, OpenX + 40, OpenY);
        state.Characters[2].AfterburnDecayTimer = 3;

        state.SpawnProjectile(Flame(OpenX + 40, OpenY, 1, Team.Red));
        Projectiles.Collide(World, ref state, ref DiscardedEvents.Events);

        Assert.Equal(CharacterState.BurnDecayDelay, state.Characters[2].AfterburnDecayTimer);
    }

    /// <summary>
    /// Either quantity running out ends the fire, and it forgets who lit it.
    /// </summary>
    /// <remarks>
    /// That last part is load-bearing: <c>burnedBy</c> is the only record of the
    /// pyro's claim, so a fire that expires cannot finish anybody off afterwards.
    /// </remarks>
    [Theory]
    [InlineData(1, 6)]
    [InlineData(60, 0.02)]
    public void EitherQuantityRunningOutEndsTheFire(double duration, double intensity)
    {
        var state = default(WorldState);
        Put(ref state, 1, PlayerClass.Soldier, Team.Blue, OpenX, OpenY);
        state.Characters[1].BurnDuration = duration;
        state.Characters[1].BurnIntensity = intensity;
        state.Characters[1].BurnedBy = 2;
        state.Characters[1].AfterburnSource = DamageSource.Flamethrower;
        state.Characters[1].IntensityDecay = 1;
        state.Characters[1].AfterburnDecayTimer = WeaponState.Inactive;

        Afterburn.BeginStep(ref state, 1, ref DiscardedEvents.Events);

        Assert.Equal(0, state.Characters[1].BurnDuration);
        Assert.Equal(0, state.Characters[1].BurnIntensity);
        Assert.Equal(CharacterState.Nobody, state.Characters[1].BurnedBy);
        Assert.Equal(DamageSource.None, state.Characters[1].AfterburnSource);
    }

    /// <summary>Afterburn can kill, and the kill is credited to the pyro.</summary>
    /// <remarks>
    /// The death is not noticed in Begin Step — <see cref="Deaths"/> runs at End
    /// Step, as it does for every other damage source — so the whole tick
    /// completes first.
    /// </remarks>
    [Fact]
    public void AfterburnCanFinishSomebodyOff()
    {
        var state = default(WorldState);
        var events = default(TickEvents);

        Put(ref state, 1, PlayerClass.Pyro, Team.Red, OpenX, OpenY);
        Put(ref state, 2, PlayerClass.Soldier, Team.Blue, OpenX + 60, OpenY);
        state.Characters[2].Hp = 0.1;
        state.Characters[2].BurnIntensity = 13;
        state.Characters[2].BurnDuration = 60;
        state.Characters[2].BurnedBy = 1;
        state.Characters[2].AfterburnSource = DamageSource.Flamethrower;
        state.Characters[2].LastDamageDealer = 1;
        state.Characters[2].AfterburnDecayTimer = 90;

        MatchSimulation.Tick(World, ref state, new PlayerInput[8], ref events);

        Assert.False(state.Alive[2]);
        Assert.True(events.Died[2]);
        Assert.Equal(1, events.Deaths[2].Killer);
        Assert.Equal(DamageSource.Flamethrower, events.Deaths[2].Source);
    }

    /// <summary>An uber puts a fire out and forgets who lit it.</summary>
    [Fact]
    public void AnUberExtinguishesAFire()
    {
        var state = default(WorldState);
        Put(ref state, 1, PlayerClass.Soldier, Team.Blue, OpenX, OpenY);
        state.Characters[1].BurnIntensity = 9;
        state.Characters[1].BurnDuration = 100;
        state.Characters[1].BurnedBy = 2;
        state.Characters[1].Ubered = true;
        state.Characters[1].UberTimer = 3;

        MatchSimulation.Tick(World, ref state, new PlayerInput[8]);

        Assert.Equal(0, state.Characters[1].BurnIntensity);
        Assert.Equal(0, state.Characters[1].BurnDuration);
        Assert.Equal(CharacterState.Nobody, state.Characters[1].BurnedBy);
    }

    private static int FloorBelow(int x, int y)
    {
        for (var probe = y; probe < y + 600; probe++)
        {
            if (!World.PlaceFree(new Aabb(x, probe, x, probe), CollisionFilter.ForBullet()))
                return probe;
        }

        Assert.Fail($"no floor under ({x}, {y}) on {Map}");
        return 0;
    }
}
