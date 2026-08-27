using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// The scattergun and the pellets it throws: phase 5 stage 3.
/// </summary>
/// <remarks>
/// <c>PhysicsParityTests</c> proves the whole chain against a recording of the
/// real server, which is the claim that matters — but it proves it as one
/// number. These pin the individual rules, so that when that gate goes red it
/// says <em>which</em> of them moved.
/// <para>
/// Everything here is transcribed from four places:
/// <c>Objects/Weapons/Scattergun.events/{User Event 1, User Event 3, Alarm
/// 5}.xml</c>, <c>Objects/Projectiles/Shot.events/{Create, Collision with
/// Character}.xml</c>, <c>Scripts/Object Creation/createShot.gml</c> and
/// <c>Character.events/End Step.xml</c>.
/// </para>
/// </remarks>
public sealed class ScattergunTests
{
    private const string Map = "ctf_truefort";

    /// <summary>
    /// Open ground on <c>ctf_truefort</c> — where the fixture's two bots met.
    /// </summary>
    /// <remarks>
    /// Every test that uses it asserts the space really is free rather than
    /// trusting the number, so a map change breaks the test loudly instead of
    /// making a collision test pass for the wrong reason.
    /// </remarks>
    private const double OpenX = 2600;

    private const double OpenY = 708;

    private static CollisionWorld World() =>
        new(LevelMap.FromPng(Map, ReferenceTree.ReadMap(Map)));

    /// <summary>
    /// One world for the tests that only need <c>FireStep</c> to have something
    /// to hand the Rocketlauncher.
    /// </summary>
    /// <remarks>
    /// A scattergun blast reads no geometry at all, so these tests pass the map
    /// only because the signature now requires it — the rocket's
    /// <c>explodeImmediately</c> test is what put it there. Shared and lazy so
    /// twelve tests do not decode the same PNG twelve times.
    /// </remarks>
    private static CollisionWorld SharedWorld => _sharedWorld ??= World();

    private static CollisionWorld? _sharedWorld;

    /// <summary>
    /// A scout standing at (<paramref name="x"/>, <paramref name="y"/>) with a
    /// loaded, ready scattergun and the attack key held.
    /// </summary>
    private static void PutScout(
        ref WorldState state,
        int slot,
        Team team,
        double x,
        double y,
        double aimDegrees = 0,
        bool firing = true)
    {
        state.Characters[slot] = new CharacterState
        {
            Class = PlayerClass.Scout,
            Team = team,
            X = x,
            Y = y,
            Hp = ClassStats.For(PlayerClass.Scout).MaxHp,
            Facing = 1,
            LastFacing = 1,
            RunPower = ClassStats.For(PlayerClass.Scout).RunPower,
            JumpStrength = ClassStats.BaseJumpStrength,
            CloakAlpha = 1,
            CanGrabIntel = true,
            LastDamageDealer = CharacterState.Nobody,
            SecondToLastDamageDealer = CharacterState.Nobody,
            BurnedBy = CharacterState.Nobody,
            Healer = CharacterState.Nobody,

            // 65536 units to the circle, so this is the wire's own encoding.
            NetAimDirection = (ushort)Math.Round(aimDegrees * 65536.0 / 360.0),
            KeyState = firing ? PlayerKeys.Attack : PlayerKeys.None,
        };

        state.Weapons[slot] = WeaponState.Spawn(PlayerClass.Scout, x, y);
        state.Weapons[slot].ReadyToShoot = true;
        state.Weapons[slot].RefireTimer = WeaponState.Inactive;
        state.Alive[slot] = true;
    }

    private static ProjectileState[] LivePellets(in WorldState state)
    {
        var pellets = new ProjectileState[state.LiveProjectileCount];
        for (var i = 0; i < pellets.Length; i++)
            pellets[i] = state.Projectiles[state.LiveProjectileSlot(i)];

        return pellets;
    }

    // --- the blast ------------------------------------------------------------

    /// <summary>
    /// <c>repeat(6)</c>, one round out of the magazine, and both counters
    /// started.
    /// </summary>
    /// <remarks>
    /// <c>alarm[5]</c> is <c>reloadBuffer + reloadTime</c> and not
    /// <c>reloadTime</c>: the first round takes 35 ticks to come back and every
    /// one after it takes 15, which is what makes emptying the magazine cost
    /// more than firing it dry in bursts.
    /// </remarks>
    [Fact]
    public void ABlastIsSixPelletsAndOneRound()
    {
        var state = default(WorldState);
        var events = default(TickEvents);
        PutScout(ref state, 1, Team.Red, OpenX, OpenY);

        Weapons.FireStep(SharedWorld, ref state, 1, ref events);

        Assert.Equal(6, state.LiveProjectileCount);
        Assert.Equal(5, state.Weapons[1].AmmoCount);
        Assert.False(state.Weapons[1].ReadyToShoot);
        Assert.Equal(20, state.Weapons[1].RefireTimer);
        Assert.Equal(35, state.Weapons[1].ReloadTimer);

        Assert.True(events.Fired[1]);
        Assert.Equal(1, events.Fires[1].Slot);
    }

    /// <summary>Nothing happens without the attack key, a loaded weapon and a ready one.</summary>
    [Theory]
    [InlineData(false, true, 6)]
    [InlineData(true, false, 6)]
    [InlineData(true, true, 0)]
    public void ABlastNeedsTheKeyTheWeaponAndTheAmmo(bool ready, bool firing, double ammo)
    {
        var state = default(WorldState);
        var events = default(TickEvents);
        PutScout(ref state, 1, Team.Red, OpenX, OpenY, firing: firing);

        state.Weapons[1].ReadyToShoot = ready;
        state.Weapons[1].AmmoCount = ammo;

        Weapons.FireStep(SharedWorld, ref state, 1, ref events);

        Assert.Equal(0, state.LiveProjectileCount);
        Assert.False(events.Fired[1]);
    }

    /// <summary>
    /// A key pressed and released inside one tick still fires.
    /// </summary>
    /// <remarks>
    /// <c>downOrPressedKeys = keyState | pressedKeys</c>, the same union the
    /// movement uses. A client that clicks and releases between two of the
    /// server's ticks sends a <c>keyState</c> with the bit already clear, and
    /// the shot must still happen.
    /// </remarks>
    [Fact]
    public void APressReleasedInsideTheSameTickStillFires()
    {
        var state = default(WorldState);
        var events = default(TickEvents);
        PutScout(ref state, 1, Team.Red, OpenX, OpenY, firing: false);

        state.Characters[1].PressedKeys = PlayerKeys.Attack;

        Weapons.FireStep(SharedWorld, ref state, 1, ref events);

        Assert.Equal(6, state.LiveProjectileCount);
    }

    // --- where a pellet comes from and which way it points ---------------------

    /// <summary>
    /// A pellet leaves the <b>weapon</b>, which is the character's position from
    /// the previous tick, rounded.
    /// </summary>
    /// <remarks>
    /// The trap this whole file exists to pin. A weapon fires in Begin Step and
    /// only catches up to its owner in End Step
    /// (<c>Character.events/End Step.xml:64-65</c>), so the muzzle is a tick and
    /// a rounding behind the character. Taking the character's own coordinates
    /// instead would move every pellet by up to half a pixel plus a tick of
    /// travel.
    /// </remarks>
    [Fact]
    public void APelletLeavesFromTheWeaponAndNotFromTheCharacter()
    {
        var state = default(WorldState);
        var events = default(TickEvents);
        PutScout(ref state, 1, Team.Red, OpenX, OpenY);

        // The character has moved on since End Step armed the weapon.
        state.Characters[1].X = OpenX + 4.37;
        state.Weapons[1].X = OpenX;
        state.Weapons[1].Y = OpenY;

        Weapons.FireStep(SharedWorld, ref state, 1, ref events);

        // A pellet is placed at the muzzle and then nudged fifteen pixels along
        // its own heading, so what is asserted is the distance from the weapon
        // rather than the coordinate. From the character it would be more.
        foreach (var pellet in LivePellets(state))
        {
            var dx = pellet.X - OpenX;
            var dy = pellet.Y - OpenY;

            Assert.Equal(15, Math.Sqrt((dx * dx) + (dy * dy)), 9);
        }
    }

    /// <summary>
    /// The weapon follows its owner at <c>round(x)</c>, half to even.
    /// </summary>
    [Fact]
    public void TheWeaponFollowsItsOwnerAtARoundedPosition()
    {
        var character = new CharacterState { X = 100.5, Y = 200.5 };
        var weapon = default(WeaponState);

        Weapons.FollowOwner(character, ref weapon);

        // Half to even: 100.5 down to 100, 200.5 up to 200 — both to the even
        // neighbour, which is what distinguishes this from half-away-from-zero.
        Assert.Equal(100, weapon.X);
        Assert.Equal(200, weapon.Y);
    }

    /// <summary>
    /// ⚠️ All six pellets carry the <b>aim</b> as their <c>image_angle</c> while
    /// flying along six scattered headings.
    /// </summary>
    /// <remarks>
    /// <c>createShot</c> sets <c>image_angle = dir</c> and the Scattergun then
    /// only scatters <c>direction</c>. Since the box is the rotated sprite's,
    /// this means a blast's pellets all have the same hit box shape however
    /// widely they spread — and a port that pointed the box along each pellet's
    /// own heading would give the outer pellets a different reach.
    /// </remarks>
    [Fact]
    public void EveryPelletKeepsTheAimAsItsBoxAngleAndScattersOnlyItsHeading()
    {
        const double Aim = 30;

        var state = default(WorldState);
        var events = default(TickEvents);
        PutScout(ref state, 1, Team.Red, OpenX, OpenY, aimDegrees: Aim);

        Weapons.FireStep(SharedWorld, ref state, 1, ref events);

        var pellets = LivePellets(state);
        var aim = state.Characters[1].AimDirection;

        Assert.All(pellets, p => Assert.Equal(aim, p.ImageAngle));

        // random(15) - 7.5, so the spread is inside +/- 7.5 degrees and is not
        // all the same value.
        Assert.All(pellets, p => Assert.True(Math.Abs(p.Motion.Direction - aim) <= 7.5,
            $"a pellet went to {p.Motion.Direction} from an aim of {aim}"));

        Assert.True(pellets.Select(p => p.Motion.Direction).Distinct().Count() > 1,
            "every pellet took the same heading, so the spread is not being drawn");
    }

    /// <summary>
    /// A pellet starts fifteen pixels along its own heading, placed with
    /// <c>lengthdir</c>.
    /// </summary>
    /// <remarks>
    /// Checked on a straight-up shot, where the snap in
    /// <see cref="Gml.LengthDirY"/> is the whole point: the engine answers
    /// <c>lengthdir_y(15, 90)</c> with exactly -15 rather than with
    /// -14.999999999999998, and the pellet lands on a whole pixel. The spread
    /// still moves it off 90, so this asserts the distance rather than the
    /// coordinate.
    /// </remarks>
    [Fact]
    public void APelletIsNudgedFifteenPixelsForwardBeforeItMoves()
    {
        var state = default(WorldState);
        var events = default(TickEvents);
        PutScout(ref state, 1, Team.Red, OpenX, OpenY, aimDegrees: 90);

        Weapons.FireStep(SharedWorld, ref state, 1, ref events);

        foreach (var pellet in LivePellets(state))
        {
            var dx = pellet.X - state.Weapons[1].X;
            var dy = pellet.Y - state.Weapons[1].Y;

            Assert.Equal(15, Math.Sqrt((dx * dx) + (dy * dy)), 9);

            // Up the screen, because GM8's y axis points down and its angles do
            // not.
            Assert.True(dy < 0, $"a pellet aimed at 90 degrees went down by {dy}");
        }
    }

    // --- the seeded stream ----------------------------------------------------

    /// <summary>
    /// A pellet costs exactly three draws, in the order choose, speed,
    /// direction.
    /// </summary>
    /// <remarks>
    /// <b>The most brittle thing in the file and the most important.</b> Every
    /// client re-derives the whole blast from the seed on the wire, so the
    /// number of advances matters as much as their use — dropping the
    /// <c>choose(0, 0, 1)</c> in <c>Shot.events/Create.xml</c> because nothing
    /// reads <c>perseverant</c> yet would leave pellet 1 correct and every
    /// pellet after it wrong.
    /// <para>
    /// Rebuilt here from a bare <see cref="GmlRandom"/> rather than compared
    /// against a recorded fixture, so the assertion is about the <em>order</em>
    /// and not about a magic list of angles.
    /// </para>
    /// </remarks>
    [Fact]
    public void EachPelletCostsThreeDrawsInCreateSpeedDirectionOrder()
    {
        const ushort Seed = 4321;
        const double Aim = 0;

        var state = default(WorldState);
        var events = default(TickEvents);
        PutScout(ref state, 1, Team.Red, OpenX, OpenY, aimDegrees: Aim);
        events.ForceFireSeed(1, Seed);

        Weapons.FireStep(SharedWorld, ref state, 1, ref events);

        var random = new GmlRandom(Seed);
        var pellets = LivePellets(state);

        for (var i = 0; i < pellets.Length; i++)
        {
            var perseverant = random.ChooseIndex(3) == 2;
            var speedJitter = random.Next(4) - 2;
            var directionJitter = random.Next(15) - 7.5;

            var expected = default(Motion);
            expected.Direction = Aim;
            expected.Speed = 13;
            expected.HSpeed += state.Characters[1].HSpeed;
            expected.Speed += speedJitter;
            expected.Direction += directionJitter;

            Assert.Equal(perseverant, pellets[i].Perseverant);
            Assert.Equal(expected.Direction, pellets[i].Motion.Direction);
            Assert.Equal(expected.Speed, pellets[i].Motion.Speed);
        }
    }

    /// <summary>A forced seed is used once and not reused by the next blast.</summary>
    /// <remarks>
    /// The property the parity harness leans on: an unused seed has to mean the
    /// port did not fire when the reference did, which is only true if a seed is
    /// consumed exactly once.
    /// </remarks>
    [Fact]
    public void AForcedSeedIsConsumedByTheShotThatUsesIt()
    {
        var state = default(WorldState);
        var events = default(TickEvents);
        PutScout(ref state, 1, Team.Red, OpenX, OpenY);
        events.ForceFireSeed(1, 1234);

        Weapons.FireStep(SharedWorld, ref state, 1, ref events);

        Assert.False(events.HasForcedFireSeed[1]);
    }

    /// <summary>
    /// The same state and the same seed give the same blast; a different seed
    /// does not.
    /// </summary>
    [Fact]
    public void TheSeedDecidesTheBlast()
    {
        double[] Blast(ushort seed)
        {
            var state = default(WorldState);
            var events = default(TickEvents);
            PutScout(ref state, 1, Team.Red, OpenX, OpenY);
            events.ForceFireSeed(1, seed);

            Weapons.FireStep(SharedWorld, ref state, 1, ref events);
            return [.. LivePellets(state).Select(p => p.Motion.Direction)];
        }

        Assert.Equal(Blast(7), Blast(7));
        Assert.NotEqual(Blast(7), Blast(8));
    }

    // --- the lifetime ---------------------------------------------------------

    /// <summary>
    /// A standing shooter's pellets live the full 35 ticks; running forwards
    /// shortens them.
    /// </summary>
    /// <remarks>
    /// The range compensation in <c>User Event 3</c>. A pellet inherits the
    /// shooter's <c>hspeed</c>, so a scout running at its target would otherwise
    /// out-range one standing still; the fuse is shortened by up to half to
    /// cancel it. Running <em>away</em> does not lengthen it — the
    /// <c>min(1, ...)</c> caps the factor at 1.
    /// </remarks>
    [Fact]
    public void PelletLifetimeCompensatesForTheShootersOwnSpeed()
    {
        var standing = Weapons.ScattergunPelletLifetime(0, 0);
        var running = Weapons.ScattergunPelletLifetime(0, 6);
        var retreating = Weapons.ScattergunPelletLifetime(0, -6);

        Assert.Equal(35, standing);
        Assert.True(running < standing, $"running forward gave {running}, not less than {standing}");
        Assert.Equal(35, retreating);

        // Half is the floor: (min(1, ...) - 1)/2 + 1 cannot go below 0.5.
        Assert.True(running >= 17, $"the factor fell below a half: {running}");
    }

    /// <summary>
    /// A vertical aim does not divide by zero, because GM8's cosine of 90
    /// degrees is not zero.
    /// </summary>
    /// <remarks>
    /// The expression divides by <c>abs(cos(aim)*13 + hspeed)</c>, which looks
    /// like a hole at 90 degrees with the shooter standing still. It is not:
    /// <c>cos(degtorad(90))</c> is 6.1e-17 in the engine and in the port alike,
    /// so numerator and denominator are the same tiny number and the ratio is 1.
    /// </remarks>
    [Fact]
    public void AVerticalShotFromAStandingShooterStillGetsAFullLifetime()
    {
        Assert.Equal(35, Weapons.ScattergunPelletLifetime(90, 0));
        Assert.Equal(35, Weapons.ScattergunPelletLifetime(270, 0));
    }

    /// <summary>A pellet is destroyed when its lifetime counter reaches zero.</summary>
    [Fact]
    public void APelletExpires()
    {
        var state = default(WorldState);
        var events = default(TickEvents);
        PutScout(ref state, 1, Team.Red, OpenX, OpenY);

        Weapons.FireStep(SharedWorld, ref state, 1, ref events);
        Assert.Equal(6, state.LiveProjectileCount);

        var lifetime = LivePellets(state)[0].Lifetime;

        for (var i = 0; i < lifetime - 1; i++)
            Timers.Advance(ref state);

        Assert.Equal(6, state.LiveProjectileCount);

        Timers.Advance(ref state);
        Assert.Equal(0, state.LiveProjectileCount);
    }

    // --- the reload -----------------------------------------------------------

    /// <summary>
    /// The magazine refills one round at a time, 35 ticks for the first and 15
    /// for each after it.
    /// </summary>
    [Fact]
    public void TheMagazineRefillsOneRoundAtATime()
    {
        var state = default(WorldState);
        var events = default(TickEvents);
        PutScout(ref state, 1, Team.Red, OpenX, OpenY);

        Weapons.FireStep(SharedWorld, ref state, 1, ref events);
        Assert.Equal(5, state.Weapons[1].AmmoCount);

        for (var i = 0; i < 35; i++)
            Timers.Advance(ref state);

        Assert.Equal(6, state.Weapons[1].AmmoCount);

        // Full again, so it does not re-arm: the second `if` asks about the
        // *new* count. Asserted by running on rather than by reading the
        // counter, because a GM8 alarm rests at 0 for one tick after it fires
        // and only then falls to -1 (see Timers.Advance) — so the counter says
        // nothing useful on the tick it went off.
        for (var i = 0; i < 60; i++)
            Timers.Advance(ref state);

        Assert.Equal(6, state.Weapons[1].AmmoCount);
    }

    /// <summary>The refire counter re-arms the weapon exactly twenty ticks later.</summary>
    [Fact]
    public void TheWeaponIsReadyAgainAfterTheRefireTime()
    {
        var state = default(WorldState);
        var events = default(TickEvents);
        PutScout(ref state, 1, Team.Red, OpenX, OpenY);

        Weapons.FireStep(SharedWorld, ref state, 1, ref events);
        Assert.False(state.Weapons[1].ReadyToShoot);

        for (var i = 0; i < 19; i++)
            Timers.Advance(ref state);

        Assert.False(state.Weapons[1].ReadyToShoot);

        Timers.Advance(ref state);
        Assert.True(state.Weapons[1].ReadyToShoot);
    }

    // --- hitting somebody -----------------------------------------------------

    /// <summary>A pellet placed on an enemy damages them, pushes them, and dies.</summary>
    /// <remarks>
    /// The knockback arguments come from the <em>shot</em> and not from the
    /// victim: <c>with(other) motion_add(other.direction, other.speed*0.03)</c>
    /// swaps what <c>other</c> means, so a standing target is pushed along the
    /// pellet's heading rather than accelerated along its own.
    /// </remarks>
    [Fact]
    public void APelletDamagesAnEnemyAndPushesThemAlongItsOwnHeading()
    {
        var world = World();
        var state = default(WorldState);
        PutScout(ref state, 1, Team.Blue, OpenX, OpenY, firing: false);

        Assert.True(
            world.PlaceFree(CharacterBox.For(PlayerClass.Scout), OpenX, OpenY, state.Characters[1].Filter),
            "the test's chosen coordinates are not open ground any more");

        var pellet = new ProjectileState
        {
            Kind = ProjectileKind.Shot,
            Team = Team.Red,
            OwnerSlot = 2,
            Source = DamageSource.Scattergun,
            X = OpenX,
            Y = OpenY,
            HitDamage = 8,
            Lifetime = 35,
        };
        pellet.Motion.SetPolar(13, 0);
        state.SpawnProjectile(pellet);

        Projectiles.Collide(world, ref state);

        Assert.Equal(92, state.Characters[1].Hp);
        Assert.Equal(0, state.LiveProjectileCount);

        Assert.Equal(2, state.Characters[1].LastDamageDealer);
        Assert.Equal(DamageSource.Scattergun, state.Characters[1].LastDamageSource);
        Assert.Equal(GameConstants.AssistTime, state.Characters[1].FinishOffTimer);

        // 13 * 0.03 along zero degrees: to the right, and nothing vertical.
        Assert.Equal(13 * 0.03, state.Characters[1].HSpeed, 12);
        Assert.Equal(0, state.Characters[1].VSpeed);

        // dealFlicker, which every class gets and only a spy can see.
        Assert.True(state.Characters[1].CloakFlicker);
        Assert.Equal(3, state.Characters[1].CloakFlickerTimer);
    }

    /// <summary>
    /// A pellet passes through a team-mate, through its own owner, and through
    /// somebody already dead — and survives all three.
    /// </summary>
    [Theory]
    [InlineData("teammate")]
    [InlineData("owner")]
    [InlineData("dead")]
    [InlineData("ubered")]
    public void APelletPassesThroughWhoeverItMayNotHit(string who)
    {
        var world = World();
        var state = default(WorldState);

        var victimTeam = who == "teammate" ? Team.Red : Team.Blue;
        PutScout(ref state, 1, victimTeam, OpenX, OpenY, firing: false);

        if (who == "dead")
            state.Characters[1].Hp = 0;
        if (who == "ubered")
            state.Characters[1].Ubered = true;

        var pellet = new ProjectileState
        {
            Kind = ProjectileKind.Shot,
            Team = Team.Red,
            OwnerSlot = who == "owner" ? (byte)1 : (byte)2,
            Source = DamageSource.Scattergun,
            X = OpenX,
            Y = OpenY,
            HitDamage = 8,
            Lifetime = 35,
        };
        pellet.Motion.SetPolar(13, 0);
        state.SpawnProjectile(pellet);

        Projectiles.Collide(world, ref state);

        // Not destroyed: the collision event falls off its end rather than
        // calling instance_destroy, so the pellet carries on to whoever is
        // behind them.
        Assert.Equal(1, state.LiveProjectileCount);
        Assert.Equal(who == "dead" ? 0 : 100, state.Characters[1].Hp);
    }

    /// <summary>
    /// ⚠️ A pellet that reaches somebody <em>through</em> a wall dies there and
    /// deals no damage.
    /// </summary>
    /// <remarks>
    /// The whole of a <c>Shot</c>'s interaction with the map. Nothing tests the
    /// walls while it flies — only the first line of the collision event does,
    /// and only when there is a character to collide with. So a pellet can pass
    /// through a wall and be stopped by it retroactively, and a pellet fired
    /// into a wall with nobody behind it sails on until its alarm expires. This
    /// is the reference's behaviour and not a simplification.
    /// </remarks>
    [Fact]
    public void APelletInsideAWallDiesWithoutDealingDamage()
    {
        var world = World();
        var state = default(WorldState);

        var (wallX, wallY) = FirstSolidPixelNear(world, (int)OpenX, (int)OpenY);
        PutScout(ref state, 1, Team.Blue, wallX, wallY, firing: false);

        var pellet = new ProjectileState
        {
            Kind = ProjectileKind.Shot,
            Team = Team.Red,
            OwnerSlot = 2,
            Source = DamageSource.Scattergun,
            X = wallX,

            // Two below the first solid row, because a Shot's box sits *above*
            // its position: the sprite origin is the tip of an 18x2 streak whose
            // rows are -2 and -1. Placing it on the solid pixel itself would put
            // the box in the open air over the wall.
            Y = wallY + 2,
            HitDamage = 8,
            Lifetime = 35,
        };
        pellet.Motion.SetPolar(13, 0);
        state.SpawnProjectile(pellet);

        Assert.False(world.PlaceFree(pellet.Box, CollisionFilter.ForBullet()),
            "the pellet is not inside the map, so this test is not testing anything");

        Projectiles.Collide(world, ref state);

        Assert.Equal(0, state.LiveProjectileCount);
        Assert.Equal(100, state.Characters[1].Hp);
    }

    /// <summary>The first solid map pixel below a given point.</summary>
    /// <remarks>
    /// Searched rather than hard-coded so the test says what it needs — "inside
    /// the map's geometry" — instead of naming a coordinate that a re-drawn map
    /// could quietly turn into open air.
    /// </remarks>
    private static (int X, int Y) FirstSolidPixelNear(CollisionWorld world, int x, int y)
    {
        for (var probe = y; probe < y + 200; probe++)
        {
            if (world.IsSolidAt(x, probe))
                return (x, probe);
        }

        throw new InvalidOperationException($"no solid pixel below ({x}, {y}) on {Map}");
    }

    // --- the whole tick -------------------------------------------------------

    /// <summary>
    /// Through <see cref="MatchSimulation.Tick"/>: a shot fired on one tick has
    /// already moved by the end of it.
    /// </summary>
    /// <remarks>
    /// The phase ordering the rest of stage 3 hangs off. A weapon fires in Begin
    /// Step, and GM8's automatic-movement pass comes later in the same frame and
    /// walks every instance then present — so a pellet created this tick moves
    /// this tick, on top of the fifteen-pixel nudge it was placed with.
    /// </remarks>
    [Fact]
    public void APelletMovesOnTheTickItIsFiredOn()
    {
        var world = World();
        var state = default(WorldState);
        var events = default(TickEvents);
        PutScout(ref state, 1, Team.Red, OpenX, OpenY);

        MatchSimulation.Tick(world, ref state, [], ref events);

        Assert.Equal(6, state.LiveProjectileCount);

        foreach (var pellet in LivePellets(state))
        {
            var travelled = Math.Abs(pellet.X - OpenX);

            // 15 of nudge plus one tick of an 11-to-15 px/tick pellet.
            Assert.True(travelled > 15 + 10, $"a pellet only reached {travelled} from its muzzle");
        }
    }
}
