using GG2.Maps;
using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Tests;

/// <summary>
/// The airblast and the flare: phase 5 stage 12, the last weapon behaviour.
/// </summary>
/// <remarks>
/// Transcribed from <c>Objects/Weapons/Flamethrower.events/User Event 2.xml</c>
/// (dispatched by <c>Character.events/Begin Step.xml:120-126</c>),
/// <c>Objects/Projectiles/{AirBlastO, Flare, BurningProjectile}.events/*</c>,
/// <c>Mine.events/{Alarm 0, User Event 2}.xml</c> and the mine's stickied
/// branch in <c>move_all_bullets.gml</c>. The two oracles it stands on are
/// pinned in <see cref="AirBlastBoxTests"/>; nothing here is gated by a
/// recording — the Flamethrower is unseeded, so like the flame these tests are
/// the gate.
/// </remarks>
public sealed class AirBlastTests
{
    private const string Map = "ctf_truefort";

    /// <summary>A standing position: the floor is directly under it.</summary>
    private const double OpenX = 2600;

    private const double OpenY = 708;

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

    /// <summary>A pyro with the right-click down and the blast off cooldown.</summary>
    private static void PutBlastingPyro(
        ref WorldState state,
        int slot,
        Team team,
        double x,
        double y,
        PlayerKeys extraKeys = PlayerKeys.None,
        double aimDegrees = 0)
    {
        Put(ref state, slot, PlayerClass.Pyro, team, x, y,
            PlayerKeys.Special | extraKeys, aimDegrees);

        state.Weapons[slot].ReadyToBlast = true;
        state.Weapons[slot].BlastTimer = WeaponState.Inactive;
    }

    private static void Blast(ref WorldState state, int slot)
    {
        var events = default(TickEvents);
        Weapons.SpecialStep(World, ref state, slot, ref events);
    }

    private static ProjectileState Live(in WorldState state, int index) =>
        state.Projectiles[state.LiveProjectileSlot(index)];

    // --- the guard and the finish block ----------------------------------------

    /// <summary>`if (readyToBlast && ammoCount >= 40)` — and nothing outside it.</summary>
    [Fact]
    public void TheBlastNeedsReadinessAndFortyAmmo()
    {
        var state = default(WorldState);

        Put(ref state, 1, PlayerClass.Pyro, Team.Red, OpenX, OpenY, PlayerKeys.Special);
        Put(ref state, 2, PlayerClass.Scout, Team.Blue, OpenX + 100, OpenY);

        // Fresh pyro: readyToBlast is false with the cooldown running.
        Blast(ref state, 1);
        Assert.Equal(200, state.Weapons[1].AmmoCount);
        Assert.Equal(0.0, state.Characters[2].Motion.HSpeed);

        // Ready, but the tank is below the cost.
        state.Weapons[1].ReadyToBlast = true;
        state.Weapons[1].AmmoCount = 39;
        Blast(ref state, 1);
        Assert.Equal(39, state.Weapons[1].AmmoCount);
        Assert.Equal(0.0, state.Characters[2].Motion.HSpeed);
    }

    /// <summary>The finish block, field for field.</summary>
    [Fact]
    public void TheBlastSpendsFortyAmmoAndSilencesTheFlame()
    {
        var state = default(WorldState);

        PutBlastingPyro(ref state, 1, Team.Red, OpenX, OpenY);
        state.Weapons[1].IsRefilling = true;

        Blast(ref state, 1);

        var weapon = state.Weapons[1];
        Assert.Equal(160, weapon.AmmoCount);
        Assert.False(weapon.ReadyToBlast);
        Assert.False(weapon.ReadyToShoot);
        Assert.False(weapon.IsRefilling);

        // alarm[0] = blastNoFlameTime, alarm[1] = alarm[5] = blastReloadTime.
        Assert.Equal(15, weapon.RefireTimer);
        Assert.Equal(40, weapon.BlastTimer);
        Assert.Equal(40, weapon.ReloadTimer);
    }

    /// <summary>Flamethrower.events/Alarm 1.xml, through the alarm stage.</summary>
    [Fact]
    public void AFreshPyroCanBlastFortyTicksAfterSpawning()
    {
        var state = default(WorldState);

        Put(ref state, 1, PlayerClass.Pyro, Team.Red, OpenX, OpenY);

        Assert.False(state.Weapons[1].ReadyToBlast);
        Assert.False(state.Weapons[1].ReadyToFlare);

        for (var tick = 0; tick < 40; tick++)
            Timers.Advance(ref state);

        Assert.True(state.Weapons[1].ReadyToBlast);
        Assert.False(state.Weapons[1].ReadyToFlare);

        for (var tick = 0; tick < 15; tick++)
            Timers.Advance(ref state);

        Assert.True(state.Weapons[1].ReadyToFlare);
    }

    // --- the shove -------------------------------------------------------------

    /// <summary>
    /// The shove: <c>motion_add(aim, 15*(1 - dist/150))</c> from the weapon,
    /// then <c>vspeed -= 2</c>, <c>moveStatus = 3</c>, and the pyro claims the
    /// kill with no damage source.
    /// </summary>
    [Fact]
    public void TheBlastShovesAnEnemyInFrontOfIt()
    {
        var state = default(WorldState);

        PutBlastingPyro(ref state, 1, Team.Red, OpenX, OpenY);
        Put(ref state, 2, PlayerClass.Scout, Team.Blue, OpenX + 100, OpenY);

        Blast(ref state, 1);

        ref var victim = ref state.Characters[2];

        // dist is exactly 100, so the shove is 15 * (1 - 100/150) = 5 along
        // aim 0: hspeed 5, vspeed 0 - 2.
        Assert.Equal(5.0, victim.Motion.HSpeed, 12);
        Assert.Equal(-2.0, victim.Motion.VSpeed);
        Assert.Equal(3, victim.MoveStatus);

        Assert.Equal(1, victim.LastDamageDealer);
        Assert.Equal(DamageSource.None, victim.LastDamageSource);
        Assert.Equal(GameConstants.AssistTime, victim.FinishOffTimer);
    }

    /// <summary>An enemy the poof's circle cannot reach is untouched.</summary>
    [Fact]
    public void TheBlastDoesNotReachBehindThePyro()
    {
        var state = default(WorldState);

        PutBlastingPyro(ref state, 1, Team.Red, OpenX, OpenY);
        Put(ref state, 2, PlayerClass.Scout, Team.Blue, OpenX - 80, OpenY);

        Blast(ref state, 1);

        Assert.Equal(0.0, state.Characters[2].Motion.HSpeed);
        Assert.Equal(0, state.Characters[2].MoveStatus);
        Assert.Equal(160, state.Weapons[1].AmmoCount);
    }

    /// <summary>
    /// A left-facing pyro's poof is rotated by aim + 180 at xscale -1, and the
    /// blast lands behind it — where its aim points.
    /// </summary>
    [Fact]
    public void ALeftFacingPyroBlastsAlongItsAim()
    {
        var state = default(WorldState);

        PutBlastingPyro(ref state, 1, Team.Red, OpenX, OpenY, aimDegrees: 180);
        state.Characters[1].Facing = -1;
        Put(ref state, 2, PlayerClass.Scout, Team.Blue, OpenX - 100, OpenY);

        Blast(ref state, 1);

        Assert.Equal(-5.0, state.Characters[2].Motion.HSpeed, 12);
        Assert.Equal(3, state.Characters[2].MoveStatus);
    }

    /// <summary>There is no ubered test anywhere in the shove.</summary>
    [Fact]
    public void AnUberedEnemyIsStillShoved()
    {
        var state = default(WorldState);

        PutBlastingPyro(ref state, 1, Team.Red, OpenX, OpenY);
        Put(ref state, 2, PlayerClass.Scout, Team.Blue, OpenX + 100, OpenY);
        state.Characters[2].Ubered = true;

        Blast(ref state, 1);

        Assert.Equal(5.0, state.Characters[2].Motion.HSpeed, 12);
        Assert.Equal(3, state.Characters[2].MoveStatus);
    }

    // --- the extinguish --------------------------------------------------------

    /// <summary>
    /// A burning team-mate is put out, and their drawn flames come loose as
    /// real <c>Flame</c>s owned by the blasting pyro.
    /// </summary>
    [Fact]
    public void ABurningTeamMateIsExtinguishedIntoLooseFlames()
    {
        var state = default(WorldState);

        PutBlastingPyro(ref state, 1, Team.Red, OpenX, OpenY);
        Put(ref state, 2, PlayerClass.Scout, Team.Red, OpenX + 100, OpenY);

        ref var mate = ref state.Characters[2];
        mate.BurnIntensity = 6;
        mate.BurnDuration = 100;
        mate.BurnedBy = 3;
        mate.AfterburnSource = DamageSource.Flamethrower;
        mate.RealNumFlames = 3;

        Blast(ref state, 1);

        Assert.Equal(0.0, mate.BurnIntensity);
        Assert.Equal(0.0, mate.BurnDuration);
        Assert.Equal(CharacterState.Nobody, mate.BurnedBy);
        Assert.Equal(DamageSource.None, mate.AfterburnSource);

        Assert.Equal(3, state.LiveProjectileCount);
        for (var i = 0; i < 3; i++)
        {
            var flame = Live(state, i);
            Assert.Equal(ProjectileKind.Flame, flame.Kind);
            Assert.Equal(1, flame.OwnerSlot);
            Assert.Equal(Team.Red, flame.Team);
            Assert.Equal(0.0, flame.Motion.Direction);
            Assert.Equal(0.0, flame.ImageAngle);

            // f.speed = 6.5 + random(2.5) doubled by motion_add(direction,
            // speed) on itself.
            Assert.InRange(flame.Motion.Speed, 13.0, 18.0);
        }
    }

    /// <summary>
    /// <c>for (i = 0; i &lt; realnumflames; ...)</c> loops a fractional bound:
    /// 3.7 flames means four.
    /// </summary>
    [Fact]
    public void TheLooseFlameCountFollowsTheFractionalRealNumFlames()
    {
        var state = default(WorldState);

        PutBlastingPyro(ref state, 1, Team.Red, OpenX, OpenY);
        Put(ref state, 2, PlayerClass.Scout, Team.Red, OpenX + 100, OpenY);
        state.Characters[2].BurnDuration = 100;
        state.Characters[2].BurnIntensity = 6;
        state.Characters[2].RealNumFlames = 3.7;

        Blast(ref state, 1);

        Assert.Equal(4, state.LiveProjectileCount);
    }

    /// <summary>A team-mate who is not burning is simply not touched.</summary>
    [Fact]
    public void AHealthyTeamMateIsUnaffected()
    {
        var state = default(WorldState);

        PutBlastingPyro(ref state, 1, Team.Red, OpenX, OpenY);
        Put(ref state, 2, PlayerClass.Scout, Team.Red, OpenX + 100, OpenY);

        Blast(ref state, 1);

        Assert.Equal(0, state.LiveProjectileCount);
        Assert.Equal(0.0, state.Characters[2].Motion.HSpeed);
        Assert.Equal(0, state.Characters[2].MoveStatus);
    }

    // --- the reflects ----------------------------------------------------------

    /// <summary>An enemy rocket in the poof becomes the pyro's, aimed along the aim.</summary>
    [Fact]
    public void AnEnemyRocketIsReflected()
    {
        var state = default(WorldState);

        PutBlastingPyro(ref state, 1, Team.Red, OpenX, OpenY);

        var rocket = new ProjectileState
        {
            Kind = ProjectileKind.Rocket,
            X = OpenX + 60,
            Y = OpenY,
            OwnerSlot = 2,
            Reflector = ProjectileState.NoOwner,
            Team = Team.Blue,
            Source = DamageSource.Rocketlauncher,
            HitDamage = 25,
            Knockback = 5,
        };
        rocket.Motion.Direction = 180;
        rocket.Motion.Speed = 13;
        var handle = state.SpawnProjectile(rocket);

        Blast(ref state, 1);

        ref readonly var reflected = ref state.Projectiles[handle.Index];
        Assert.Equal(1, reflected.OwnerSlot);
        Assert.Equal(Team.Red, reflected.Team);
        Assert.Equal(DamageSource.ReflectedRocket, reflected.Source);
        Assert.Equal(25.0, reflected.HitDamage);
        Assert.Equal(8.0, reflected.Knockback);
        Assert.Equal(40, reflected.KnockbackCapTimer);
        Assert.Equal(80, reflected.KnockbackEndTimer);

        // motion_set(aim, speed): turned, not slowed.
        Assert.Equal(0.0, reflected.Motion.Direction);
        Assert.Equal(13.0, reflected.Motion.Speed);
    }

    /// <summary>A friendly rocket flies on unreflected.</summary>
    [Fact]
    public void AFriendlyRocketIsNotReflected()
    {
        var state = default(WorldState);

        PutBlastingPyro(ref state, 1, Team.Red, OpenX, OpenY);

        var rocket = new ProjectileState
        {
            Kind = ProjectileKind.Rocket,
            X = OpenX + 60,
            Y = OpenY,
            OwnerSlot = 1,
            Reflector = ProjectileState.NoOwner,
            Team = Team.Red,
            Source = DamageSource.Rocketlauncher,
        };
        rocket.Motion.Direction = 180;
        rocket.Motion.Speed = 13;
        var handle = state.SpawnProjectile(rocket);

        Blast(ref state, 1);

        Assert.Equal(180.0, state.Projectiles[handle.Index].Motion.Direction);
    }

    /// <summary>A reflected enemy flare changes sides and gets its whole life back.</summary>
    [Fact]
    public void AnEnemyFlareIsReflectedWithAFreshLifetime()
    {
        var state = default(WorldState);

        PutBlastingPyro(ref state, 1, Team.Red, OpenX, OpenY);

        var flare = new ProjectileState
        {
            Kind = ProjectileKind.Flare,
            X = OpenX + 60,
            Y = OpenY,
            OwnerSlot = 2,
            Reflector = ProjectileState.NoOwner,
            Team = Team.Blue,
            Source = DamageSource.Flare,
            HitDamage = 30,
            Lifetime = 7,
        };
        flare.Motion.Direction = 180;
        flare.Motion.Speed = 15;
        var handle = state.SpawnProjectile(flare);

        Blast(ref state, 1);

        ref readonly var reflected = ref state.Projectiles[handle.Index];
        Assert.Equal(1, reflected.OwnerSlot);
        Assert.Equal(Team.Red, reflected.Team);
        Assert.Equal(DamageSource.ReflectedFlare, reflected.Source);
        Assert.Equal(40, reflected.Lifetime);
        Assert.Equal(0.0, reflected.Motion.Direction);
    }

    /// <summary>
    /// A stuck enemy mine is unstuck and thrown at
    /// <c>max(speed, 28/3) * 0.65</c> along the aim, and remembers who
    /// reflected it.
    /// </summary>
    [Fact]
    public void AStuckEnemyMineIsUnstuckAndThrown()
    {
        var state = default(WorldState);

        PutBlastingPyro(ref state, 1, Team.Red, OpenX, OpenY);

        // In mid-air, so the four place_meeting probes find no surface and the
        // bounce is skipped: pure motion_set + 0.65.
        var mine = StuckMine(OpenX + 60, OpenY, ownerSlot: 2, Team.Blue);
        var handle = state.SpawnProjectile(mine);

        Blast(ref state, 1);

        ref readonly var reflected = ref state.Projectiles[handle.Index];
        Assert.False(reflected.Stickied);
        Assert.Equal(1, reflected.Reflector);
        Assert.Equal(0.0, reflected.Motion.Direction);
        Assert.Equal(28.0 / 3 * Gml.PointSixFive, reflected.Motion.Speed, 12);
    }

    /// <summary>
    /// A stuck mine on the floor bounces: a velocity component into the
    /// surface is mirrored off its normal.
    /// </summary>
    [Fact]
    public void AReflectedMineOnTheFloorBouncesOffIt()
    {
        var state = default(WorldState);

        // Aiming down-right at 315, so the reflected mine leaves with a
        // downward vspeed the floor's upward normal must flip.
        PutBlastingPyro(ref state, 1, Team.Red, OpenX, OpenY, aimDegrees: 315);

        var floorY = FindFloorForMine(OpenX + 60);
        var mine = StuckMine(OpenX + 60, floorY, ownerSlot: 2, Team.Blue);
        var handle = state.SpawnProjectile(mine);

        Blast(ref state, 1);

        ref readonly var reflected = ref state.Projectiles[handle.Index];
        Assert.False(reflected.Stickied);

        // The horizontal component survives; the vertical one is flipped to
        // upward (negative), exactly mirrored.
        Assert.True(reflected.Motion.HSpeed > 0);
        Assert.True(reflected.Motion.VSpeed < 0);
        Assert.Equal(reflected.Motion.HSpeed, -reflected.Motion.VSpeed, 12);
    }

    /// <summary>
    /// The reflect window: thirty ticks after a reflected mine lands its
    /// alarm 0 fires `reflector = noone` — and the mine survives it, because a
    /// mine's alarm 0 is the one that is not a destroy.
    /// </summary>
    [Fact]
    public void AReflectedMineForgetsItsReflectorThirtyTicksAfterLanding()
    {
        var state = default(WorldState);

        var mine = StuckMine(OpenX + 60, OpenY, ownerSlot: 2, Team.Blue);
        mine.Reflector = 1;
        var handle = state.SpawnProjectile(mine);

        // The stickied branch of the movement pass arms alarm[0] = 30.
        Projectiles.Move(World, ref state, ref DiscardedEvents.Events);
        Assert.Equal(30, state.Projectiles[handle.Index].Lifetime);

        for (var tick = 0; tick < 30; tick++)
            Timers.Advance(ref state);

        Assert.Equal(1, state.LiveProjectileCount);
        Assert.Equal(ProjectileState.NoOwner, state.Projectiles[handle.Index].Reflector);
    }

    /// <summary>
    /// <c>Mine.events/User Event 2.xml</c>'s reflector arms: a reflected mine
    /// that reaches its own owner credits the pyro as
    /// <c>DAMAGE_SOURCE_REFLECTED_STICKY</c> — never the finish-off-gib case.
    /// </summary>
    [Fact]
    public void AReflectedMineBlastCreditsThePyroAgainstItsOwner()
    {
        var state = default(WorldState);

        Put(ref state, 1, PlayerClass.Pyro, Team.Red, OpenX + 300, OpenY);
        Put(ref state, 2, PlayerClass.Demoman, Team.Blue, OpenX, OpenY);

        var mine = StuckMine(OpenX + 10, OpenY, ownerSlot: 2, Team.Blue);
        mine.Reflector = 1;
        var handle = state.SpawnProjectile(mine);

        Explosions.DetonateMine(World, ref state, handle, ref DiscardedEvents.Events);

        ref readonly var owner = ref state.Characters[2];
        Assert.True(owner.Hp < ClassStats.For(PlayerClass.Demoman).MaxHp);
        Assert.Equal(1, owner.LastDamageDealer);
        Assert.Equal(DamageSource.ReflectedSticky, owner.LastDamageSource);
    }

    // --- the flare -------------------------------------------------------------

    /// <summary>
    /// The flare gate reads <c>keyState</c> — attack held, not merely pressed —
    /// on top of 75 ammo and its own cooldown.
    /// </summary>
    [Fact]
    public void TheFlareFiresOnlyWithTheAttackKeyHeld()
    {
        var state = default(WorldState);

        // Right-click alone: a blast, no flare.
        PutBlastingPyro(ref state, 1, Team.Red, OpenX, OpenY);
        state.Weapons[1].ReadyToFlare = true;
        Blast(ref state, 1);
        Assert.Equal(0, state.LiveProjectileCount);
        Assert.Equal(160, state.Weapons[1].AmmoCount);

        // Attack held as well: the flare leaves with the blast.
        var second = default(WorldState);
        PutBlastingPyro(ref second, 1, Team.Red, OpenX, OpenY, PlayerKeys.Attack);
        second.Weapons[1].ReadyToFlare = true;
        Blast(ref second, 1);

        Assert.Equal(1, second.LiveProjectileCount);
        var flare = Live(second, 0);
        Assert.Equal(ProjectileKind.Flare, flare.Kind);
        Assert.Equal(DamageSource.Flare, flare.Source);
        Assert.Equal(OpenX + 25, flare.X);
        Assert.Equal(15.0, flare.Motion.Speed);
        Assert.Equal(30.0, flare.HitDamage);
        Assert.Equal(40, flare.Lifetime);

        // ammoCount - 40 - 35, readyToFlare gone, alarm[2] re-armed.
        Assert.Equal(125, second.Weapons[1].AmmoCount);
        Assert.False(second.Weapons[1].ReadyToFlare);
        Assert.Equal(55, second.Weapons[1].FlareTimer);
    }

    /// <summary>
    /// `keyState & $10`, not the down-or-pressed union: a click that only
    /// arrived this tick does not launch the flare.
    /// </summary>
    [Fact]
    public void APressedButNotHeldAttackDoesNotLaunchTheFlare()
    {
        var state = default(WorldState);

        PutBlastingPyro(ref state, 1, Team.Red, OpenX, OpenY);
        state.Weapons[1].ReadyToFlare = true;
        state.Characters[1].PressedKeys |= PlayerKeys.Attack;

        Blast(ref state, 1);

        Assert.Equal(0, state.LiveProjectileCount);
        Assert.Equal(160, state.Weapons[1].AmmoCount);
    }

    /// <summary>The flare flies flat: no gravity line in its movement pass.</summary>
    [Fact]
    public void TheFlareFliesFlat()
    {
        var state = default(WorldState);

        PutBlastingPyro(ref state, 1, Team.Red, OpenX, OpenY, PlayerKeys.Attack);
        state.Weapons[1].ReadyToFlare = true;
        Blast(ref state, 1);

        Projectiles.Move(World, ref state, ref DiscardedEvents.Events);
        Projectiles.Move(World, ref state, ref DiscardedEvents.Events);

        var flare = Live(state, 0);
        Assert.Equal(0.0, flare.Motion.VSpeed);
        Assert.Equal(OpenY, flare.Y);
    }

    /// <summary>
    /// The flare is a burning projectile with its own six numbers: 30 damage,
    /// no age falloff (the full 8 heat, 35 fuel) — and a penetrate cap of 0,
    /// so it dies on its <em>first</em> victim.
    /// </summary>
    [Fact]
    public void TheFlareBurnsItsFirstVictimAndDiesOnThem()
    {
        var state = default(WorldState);

        Put(ref state, 1, PlayerClass.Pyro, Team.Red, OpenX, OpenY);
        Put(ref state, 2, PlayerClass.Heavy, Team.Blue, OpenX + 100, OpenY);

        var flare = new ProjectileState
        {
            Kind = ProjectileKind.Flare,
            X = OpenX + 100,
            Y = OpenY,
            OwnerSlot = 1,
            Reflector = ProjectileState.NoOwner,
            Team = Team.Red,
            Source = DamageSource.Flare,
            HitDamage = 30,
            Lifetime = 33,
            HasMoved = true,
        };
        flare.Motion.Direction = 0;
        flare.Motion.Speed = 15;
        state.SpawnProjectile(flare);

        Projectiles.Collide(World, ref state, ref DiscardedEvents.Events);

        ref readonly var victim = ref state.Characters[2];
        Assert.Equal(ClassStats.For(PlayerClass.Heavy).MaxHp - 30, victim.Hp);
        Assert.Equal(DamageSource.Flare, victim.LastDamageSource);

        // afterburnFalloff = false: the full burnIncrease of 8, whatever the
        // flare's age, and the full 35 ticks of fuel.
        Assert.Equal(8.0, victim.BurnIntensity);
        Assert.Equal(35.0, victim.BurnDuration);

        // penetrateCap = 0 and not perseverant: dead on the first victim.
        Assert.Equal(0, state.LiveProjectileCount);
    }

    // --- helpers ---------------------------------------------------------------

    private static ProjectileState StuckMine(double x, double y, int ownerSlot, Team team)
    {
        var mine = new ProjectileState
        {
            Kind = ProjectileKind.Mine,
            X = x,
            Y = y,
            OwnerSlot = (byte)ownerSlot,
            Reflector = ProjectileState.NoOwner,
            Team = team,
            Source = DamageSource.Minegun,
            Knockback = 4,
            Stickied = true,
            Lifetime = Timers.Inactive,
            HasMoved = true,
        };

        mine.Motion.Speed = 0;
        return mine;
    }

    /// <summary>
    /// The y at which a mine's box is free but three pixels lower is inside
    /// the map — a resting spot whose only probe-normal is the floor below.
    /// </summary>
    private static double FindFloorForMine(double x)
    {
        for (double y = OpenY; y < OpenY + 80; y++)
        {
            var probe = new ProjectileState { Kind = ProjectileKind.Mine, X = x, Y = y };
            var above = probe;
            above.Y = y - 3;
            var below = probe;
            below.Y = y + 3;

            if (World.PlaceFreeInMask(probe.Box)
                && World.PlaceFreeInMask(above.Box)
                && !World.PlaceFreeInMask(below.Box))
                return y;
        }

        throw new InvalidOperationException("no floor under the test column");
    }
}
