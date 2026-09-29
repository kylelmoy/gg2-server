using GG2.Maps;
using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Tests;

/// <summary>
/// The heavy's Minigun: one bullet every other tick, and a tank that only
/// refills when the trigger is released.
/// </summary>
/// <remarks>
/// <c>Objects/Weapons/Minigun.events/{Create, Begin Step, User Event 1}.xml</c>.
/// <para>
/// <b>No recording can gate any of it, and that is a property of the weapon
/// rather than of the fixtures.</b> The Minigun calls <c>randomize()</c> and
/// broadcasts no seed, so no client re-derives its spread and no replay can
/// reproduce it — docs/PARITY.md, "an unseeded weapon caps a fixture's hp
/// window". These tests are the whole gate, and each was checked by breaking
/// the line it is about.
/// </para>
/// </remarks>
public sealed class MinigunTests
{
    private const string Map = "ctf_truefort";

    /// <summary>A standing position with the floor directly under it.</summary>
    private const double OpenX = 2600;

    private const double OpenY = 708;

    private static CollisionWorld? _sharedWorld;

    private static CollisionWorld World => _sharedWorld ??=
        new CollisionWorld(LevelMap.FromPng(Map, ReferenceTree.ReadMap(Map)));

    private static WeaponStats Stats => WeaponStats.For(WeaponKind.Minigun);

    /// <summary>A heavy, ready to shoot, aiming as told.</summary>
    private static void Put(
        ref WorldState state,
        int slot,
        double x = OpenX,
        double y = OpenY,
        PlayerKeys keys = PlayerKeys.None,
        double aimDegrees = 0,
        Team team = Team.Red)
    {
        var character = CharacterState.Fresh(PlayerClass.Heavy, team);
        character.X = x;
        character.Y = y;
        character.NetAimDirection = (ushort)Math.Round(aimDegrees * 65536.0 / 360.0);
        character.KeyState = keys;

        state.Characters[slot] = character;
        state.Weapons[slot] = WeaponState.Spawn(PlayerClass.Heavy, x, y);
        state.Weapons[slot].X = x;
        state.Weapons[slot].Y = y;
        state.Weapons[slot].ReadyToShoot = true;
        state.Weapons[slot].RefireTimer = WeaponState.Inactive;
        state.Alive[slot] = true;
        state.AddPlayer(slot);
        state.Players[slot].Team = team;
        state.Players[slot].Class = PlayerClass.Heavy;
    }

    private static ProjectileState Live(in WorldState state, int index) =>
        state.Projectiles[state.LiveProjectileSlot(index)];

    /// <summary>
    /// An input for slot 1, aimed straight up.
    /// </summary>
    /// <remarks>
    /// <b>The aim has to be in the input, not on the character.</b>
    /// <c>ApplyInput</c> overwrites <c>NetAimDirection</c> every tick a slot is
    /// covered by the span, so a test that sets the aim on the character and
    /// then ticks with a bare <c>PlayerKeys</c> input is really aiming at 0.
    /// <para>
    /// And a slot <em>beyond</em> the span keeps the input it had, so
    /// "release the trigger" is an explicit <c>PlayerKeys.None</c> input and
    /// never an empty span.
    /// </para>
    /// </remarks>
    private static PlayerInput Aiming(PlayerKeys keys, double degrees = 90) =>
        new(keys, (ushort)Math.Round(degrees * 65536.0 / 360.0), 0);

    private static TickEvents Fire(ref WorldState state, int slot = 1)
    {
        var events = default(TickEvents);
        Weapons.FireStep(World, ref state, slot, ref events);
        return events;
    }

    // --- the weapon itself ----------------------------------------------------

    /// <summary>A heavy starts with a full 200 and a hot refire counter.</summary>
    [Fact]
    public void AFreshMinigunIsFullAndNotYetReady()
    {
        var weapon = WeaponState.Spawn(PlayerClass.Heavy, 0, 0);

        Assert.Equal(200, weapon.AmmoCount);
        Assert.Equal(Stats.RefireTime, weapon.RefireTimer);
        Assert.False(weapon.ReadyToShoot);
    }

    /// <summary>One trigger pull is one bullet, and it costs three.</summary>
    [Fact]
    public void OneShotIsOneBulletAndCostsThree()
    {
        var state = default(WorldState);
        Put(ref state, 1, keys: PlayerKeys.Attack);

        Fire(ref state);

        Assert.Equal(1, state.LiveProjectileCount);
        Assert.Equal(197, state.Weapons[1].AmmoCount);
        Assert.False(state.Weapons[1].ReadyToShoot);
        Assert.Equal(Stats.RefireTime, state.Weapons[1].RefireTimer);
    }

    /// <summary>
    /// <b>It is unseeded</b>: no <c>WEAPON_FIRE</c> and no seed, like the
    /// Flamethrower and the Blade.
    /// </summary>
    [Fact]
    public void AMinigunBroadcastsNoWeaponFire()
    {
        var state = default(WorldState);
        Put(ref state, 1, keys: PlayerKeys.Attack);

        var events = Fire(ref state);

        Assert.Equal(1, state.LiveProjectileCount);
        Assert.False(events.Fired[1]);
    }

    /// <summary>A bullet is a <c>Shot</c> and keeps the <c>Shot</c>'s own 8 damage.</summary>
    /// <remarks>
    /// The Minigun's User Event 1 never touches <c>hitDamage</c>, unlike the
    /// engineer's Shotgun which drops it to 7.
    /// </remarks>
    [Fact]
    public void ABulletIsAShotDealingEight()
    {
        var state = default(WorldState);
        Put(ref state, 1, keys: PlayerKeys.Attack);

        Fire(ref state);
        var shot = Live(state, 0);

        Assert.Equal(ProjectileKind.Shot, shot.Kind);
        Assert.Equal(8, shot.HitDamage);
        Assert.Equal(DamageSource.Minigun, shot.Source);
    }

    /// <summary>
    /// <b>A minigun bullet lives thirty ticks, not the <c>Shot</c>'s
    /// forty</b> — <c>shot.alarm[0] = 30</c>.
    /// </summary>
    [Fact]
    public void ABulletLivesThirtyTicksAndNotForty()
    {
        var state = default(WorldState);
        Put(ref state, 1, keys: PlayerKeys.Attack);

        Fire(ref state);

        Assert.Equal(30, Live(state, 0).Lifetime);
    }

    /// <summary>
    /// <b>The muzzle is twenty pixels along the aim and then twelve
    /// <em>below</em> that</b> — the drop is outside the <c>lengthdir</c>, so it
    /// is down whichever way the heavy is looking.
    /// </summary>
    /// <remarks>
    /// Two aims that mirror each other: at 0 and at 180 the <c>lengthdir</c>
    /// contributes nothing vertical, so both bullets sit exactly twelve below
    /// the weapon and their x differs by forty.
    /// </remarks>
    [Fact]
    public void TheMuzzleDropsTwelveWhicheverWayItAims()
    {
        var right = default(WorldState);
        Put(ref right, 1, keys: PlayerKeys.Attack, aimDegrees: 0);
        Fire(ref right);

        var left = default(WorldState);
        Put(ref left, 1, keys: PlayerKeys.Attack, aimDegrees: 180);
        Fire(ref left);

        Assert.Equal(OpenY + 12, Live(right, 0).Y, 9);
        Assert.Equal(OpenY + 12, Live(left, 0).Y, 9);

        Assert.Equal(OpenX + 20, Live(right, 0).X, 9);
        Assert.Equal(OpenX - 20, Live(left, 0).X, 9);
    }

    /// <summary>
    /// The spread is ±7 degrees and the speed is 12 or a shade over — the two
    /// draws the shot makes.
    /// </summary>
    [Fact]
    public void EveryBulletIsWithinSevenDegreesAndTwelveish()
    {
        var state = default(WorldState);
        Put(ref state, 1, aimDegrees: 90);

        for (var i = 0; i < 200; i++)
        {
            state.Characters[1].KeyState = PlayerKeys.Attack;
            state.Weapons[1].ReadyToShoot = true;
            state.Weapons[1].AmmoCount = 200;

            Fire(ref state);
        }

        Assert.Equal(200, state.LiveProjectileCount);

        var sawLow = false;
        var sawHigh = false;
        var sawJitter = false;

        for (var i = 0; i < state.LiveProjectileCount; i++)
        {
            var shot = Live(state, i);

            // The owner is standing still, so hspeed adds nothing and the
            // heading is the drawn one.
            var offset = shot.Motion.Direction - 90;
            Assert.InRange(offset, -7, 7);

            // The speed carries the 12 + random(1).
            Assert.InRange(shot.Motion.Speed, 12, 13);

            sawLow |= offset < -3;
            sawHigh |= offset > 3;

            // The jitter needs its own control: every speed is *within*
            // [12, 13) whether or not `random(1)` is drawn at all, so only
            // seeing one that is not exactly 12 proves the draw happens. Note
            // Motion snaps a recomputed speed to a whole number within 1e-4, so
            // a handful genuinely are 12.
            sawJitter |= shot.Motion.Speed != 12;
        }

        Assert.True(sawLow && sawHigh, "the cone never reached its edges");
        Assert.True(sawJitter, "every bullet came out at exactly speed 12");
    }

    /// <summary>
    /// A bullet inherits its shooter's horizontal velocity, like a pellet does.
    /// </summary>
    [Fact]
    public void ABulletInheritsTheShootersHorizontalVelocity()
    {
        var still = default(WorldState);
        Put(ref still, 1, keys: PlayerKeys.Attack, aimDegrees: 90);
        Fire(ref still);

        var running = default(WorldState);
        Put(ref running, 1, keys: PlayerKeys.Attack, aimDegrees: 90);
        running.Characters[1].HSpeed = 5;
        Fire(ref running);

        Assert.Equal(Live(still, 0).Motion.HSpeed + 5, Live(running, 0).Motion.HSpeed, 9);
    }

    // --- the wall test --------------------------------------------------------

    /// <summary>
    /// <b>A blocked muzzle costs the shot and nothing else.</b> The ammo, the
    /// refire and the reload buffer all run regardless — firing into a wall you
    /// are standing against still empties the tank.
    /// </summary>
    [Fact]
    public void AMuzzleInsideAWallCostsTheAmmoAndMakesNoBullet()
    {
        var level = LevelMap.FromPng(Map, ReferenceTree.ReadMap(Map));
        var world = new CollisionWorld(level);
        var state = default(WorldState);

        // A position whose muzzle twenty pixels to the right is inside solid
        // map. Searched for rather than assumed.
        var found = false;
        double px = 0, py = 0;

        for (var y = 0; y < level.Height && !found; y += 4)
        {
            for (var x = 0; x < level.Width - 40 && !found; x += 4)
            {
                if (!world.CollisionLineBlocksBullets(x, y, x + 20, y + 12, state.BulletFilter))
                    continue;

                found = true;
                px = x;
                py = y;
            }
        }

        Assert.True(found, "no blocked muzzle line anywhere on the map");

        Put(ref state, 1, px, py, keys: PlayerKeys.Attack, aimDegrees: 0);

        var events = default(TickEvents);
        Weapons.FireStep(world, ref state, 1, ref events);

        Assert.Equal(0, state.LiveProjectileCount);

        // And it still cost the shot.
        Assert.Equal(197, state.Weapons[1].AmmoCount);
        Assert.False(state.Weapons[1].ReadyToShoot);
    }

    // --- the tank -------------------------------------------------------------

    /// <summary>
    /// <b>The guard is two and the charge is three</b>, so a minigun on
    /// exactly 2 fires and lands on -1 — which the weapon's own Begin Step then
    /// clamps to 0.
    /// </summary>
    [Fact]
    public void ATankOnTwoFiresAndGoesNegativeThenClamps()
    {
        var state = default(WorldState);
        Put(ref state, 1, keys: PlayerKeys.Attack);
        state.Weapons[1].AmmoCount = 2;

        Fire(ref state);

        Assert.Equal(1, state.LiveProjectileCount);
        Assert.Equal(-1, state.Weapons[1].AmmoCount);

        Weapons.WeaponBeginStep(ref state.Weapons[1], PlayerClass.Heavy);
        Assert.Equal(0, state.Weapons[1].AmmoCount);
    }

    /// <summary>
    /// <b>The "prevent sputtering" line is above the guard</b>, so holding
    /// the trigger on an empty tank drives it further negative every tick — the
    /// same shape as the Flamethrower's.
    /// </summary>
    [Fact]
    public void AnEmptyTankIsDrivenNegativeByHoldingTheTrigger()
    {
        var state = default(WorldState);
        Put(ref state, 1, keys: PlayerKeys.Attack);
        state.Weapons[1].AmmoCount = 0;

        Fire(ref state);

        Assert.Equal(0, state.LiveProjectileCount);
        Assert.Equal(-2, state.Weapons[1].AmmoCount);

        // Still ready, because nothing was spent on a refire.
        Assert.True(state.Weapons[1].ReadyToShoot);
    }

    /// <summary>
    /// <b>The tank rests at 201, not 200</b>, because the refill guard is
    /// <c>ammoCount &lt;= maxAmmo</c> — the same off-by-one the pyro's 201.8 has.
    /// </summary>
    [Fact]
    public void TheTankRestsOneAboveItsMaximum()
    {
        var state = default(WorldState);
        Put(ref state, 1);
        state.Weapons[1].AmmoCount = 198;
        state.Weapons[1].IsRefilling = true;

        for (var i = 0; i < 10; i++)
            Weapons.WeaponBeginStep(ref state.Weapons[1], PlayerClass.Heavy);

        Assert.Equal(201, state.Weapons[1].AmmoCount);
    }

    /// <summary>
    /// Holding the trigger stops the tank refilling — and it is the
    /// <c>alarm[5] = reloadBuffer</c> on <em>every shot</em> that does it, not
    /// the Begin Step's increment.
    /// </summary>
    /// <remarks>
    /// The buffer is re-armed to 10 every two ticks and never reaches zero. The
    /// increment is a much smaller effect; see
    /// <see cref="LettingGoStartsTheRefillOneTickLate"/>.
    /// </remarks>
    [Fact]
    public void HoldingTheTriggerStopsTheTankRefilling()
    {
        var state = default(WorldState);
        Put(ref state, 1, keys: PlayerKeys.Attack);

        // Sixty ticks of held trigger, aimed up so the muzzle clears the floor.
        Span<PlayerInput> firing = [default, Aiming(PlayerKeys.Attack)];

        // Checked on *every* tick, not just at the end. The refill flag is
        // cleared again by the next shot, so a burst that started refilling
        // half way through can look untouched by the time the loop ends — which
        // is exactly what a test that only looked at the last tick missed.
        for (var i = 0; i < 60; i++)
        {
            MatchSimulation.Tick(World, ref state, firing);

            Assert.False(state.Weapons[1].IsRefilling,
                $"the tank started refilling on tick {i} with the trigger down");
        }

        // The buffer never got away from its ceiling.
        Assert.InRange(state.Weapons[1].ReloadTimer, 0, 25);

        // And the tank went down, not up.
        Assert.True(state.Weapons[1].AmmoCount < 200,
            $"the tank was at {state.Weapons[1].AmmoCount}");
    }

    /// <summary>
    /// <b>What the Begin Step's <c>alarm[5] += 1</c> is actually worth: one
    /// tick.</b>
    /// </summary>
    /// <remarks>
    /// It holds the buffer through the ticks between <c>readyToShoot = false</c>
    /// and alarm[0] putting it back, so letting go of the trigger starts the
    /// refill later than the bare buffer would. Measured: <b>11 ticks against a
    /// <c>reloadBuffer</c> of 10</b>, and deleting the line makes it 10.
    /// <para>
    /// One rather than two, because the weapon's Begin Step runs after its
    /// owner's in the <em>same</em> tick as the shot — so the shot tick is
    /// already one of the two, and only the tick after it is bought.
    /// </para>
    /// <para>
    /// This is the only thing in the weapon that line changes at 30 Hz — it
    /// reads as frame-rate compensation. Without this test it is unobservable
    /// and a mutation deleting it survives, which is exactly what happened.
    /// </para>
    /// </remarks>
    [Fact]
    public void LettingGoStartsTheRefillOneTickLate()
    {
        var state = default(WorldState);
        Put(ref state, 1);

        Span<PlayerInput> firing = [default, Aiming(PlayerKeys.Attack)];
        Span<PlayerInput> idle = [default, Aiming(PlayerKeys.None)];

        // Exactly one shot, then the trigger is up for the rest.
        MatchSimulation.Tick(World, ref state, firing);
        Assert.Equal(197, state.Weapons[1].AmmoCount);

        var ticks = 0;

        while (!state.Weapons[1].IsRefilling && ticks < 60)
        {
            MatchSimulation.Tick(World, ref state, idle);
            ticks++;
        }

        // reloadBuffer is 10, and the held tick makes it 11.
        Assert.Equal(11, ticks);
    }

    /// <summary>
    /// The control: release the trigger and the buffer runs out, the refill
    /// starts, and the tank comes back.
    /// </summary>
    [Fact]
    public void ReleasingTheTriggerLetsTheTankRefill()
    {
        var state = default(WorldState);
        Put(ref state, 1, keys: PlayerKeys.Attack);

        Span<PlayerInput> firing = [default, Aiming(PlayerKeys.Attack)];
        Span<PlayerInput> idle = [default, Aiming(PlayerKeys.None)];

        for (var i = 0; i < 60; i++)
            MatchSimulation.Tick(World, ref state, firing);

        var spent = state.Weapons[1].AmmoCount;

        // An explicit trigger-up input, not an empty span: a slot the span
        // does not cover keeps the keys it had, so an empty span would hold the
        // trigger down for ever.
        for (var i = 0; i < 60; i++)
            MatchSimulation.Tick(World, ref state, idle);

        Assert.True(state.Weapons[1].IsRefilling, "the refill never started");
        Assert.True(state.Weapons[1].AmmoCount > spent,
            $"the tank stayed at {state.Weapons[1].AmmoCount}");
    }

    /// <summary>
    /// <b>A shot that leaves the tank nearly empty buys a reload buffer two
    /// and a half times as long</b>, and the test is on the tank the shot
    /// <em>left behind</em> rather than the one it found.
    /// </summary>
    [Theory]
    [InlineData(200, 10)]   // plenty left: the plain buffer
    [InlineData(5, 25)]     // 5 - 3 = 2, which is under 3: the long one
    public void ANearlyEmptyTankBuysALongerBuffer(double ammo, int buffer)
    {
        var state = default(WorldState);
        Put(ref state, 1, keys: PlayerKeys.Attack);
        state.Weapons[1].AmmoCount = ammo;

        Fire(ref state);

        Assert.Equal(buffer, state.Weapons[1].ReloadTimer);
    }

    // --- the heavy's own Step event -------------------------------------------

    /// <summary>
    /// <b>A heavy holding the trigger walks at 0.3 instead of 0.8.</b>
    /// <c>Objects/Characters/Heavy.events/Step.xml</c> — the only
    /// class-specific Step event in the game.
    /// </summary>
    [Fact]
    public void AFiringHeavySlowsDown()
    {
        var state = default(WorldState);
        Put(ref state, 1);

        Assert.Equal(ClassStats.For(PlayerClass.Heavy).RunPower, state.Characters[1].RunPower);

        Span<PlayerInput> firing = [default, Aiming(PlayerKeys.Attack)];
        MatchSimulation.Tick(World, ref state, firing);

        Assert.Equal(0.3, state.Characters[1].RunPower);
    }

    /// <summary>And speeds back up the moment the trigger is released.</summary>
    [Fact]
    public void AHeavyWhoStopsFiringSpeedsBackUp()
    {
        var state = default(WorldState);
        Put(ref state, 1);

        Span<PlayerInput> firing = [default, Aiming(PlayerKeys.Attack)];
        Span<PlayerInput> idle = [default, Aiming(PlayerKeys.None)];

        MatchSimulation.Tick(World, ref state, firing);
        Assert.Equal(0.3, state.Characters[1].RunPower);

        MatchSimulation.Tick(World, ref state, idle);
        Assert.Equal(ClassStats.For(PlayerClass.Heavy).RunPower, state.Characters[1].RunPower);
    }

    /// <summary>
    /// <b>It reads <c>keyState</c> alone, not the down-or-pressed
    /// union</b> — so a click that arrives and releases inside one tick fires a
    /// bullet and does <em>not</em> slow the heavy.
    /// </summary>
    [Fact]
    public void ATapFiresButDoesNotSlowTheHeavy()
    {
        var state = default(WorldState);
        Put(ref state, 1);

        // A press with the key already up: PressedKeys set, KeyState clear.
        state.Characters[1].KeyState = PlayerKeys.None;
        state.Characters[1].PressedKeys = PlayerKeys.Attack;

        var events = default(TickEvents);
        Weapons.FireStep(World, ref state, 1, ref events);

        // The tap did fire.
        Assert.Equal(197, state.Weapons[1].AmmoCount);

        CharacterPhysics.HeavyStep(ref state.Characters[1]);

        // And did not slow them.
        Assert.Equal(ClassStats.For(PlayerClass.Heavy).RunPower, state.Characters[1].RunPower);
    }

    /// <summary>It is the heavy's alone: no other class has a Step event.</summary>
    [Fact]
    public void NoOtherClassSlowsDownWhileFiring()
    {
        var state = default(WorldState);

        var character = CharacterState.Fresh(PlayerClass.Soldier, Team.Red);
        character.KeyState = PlayerKeys.Attack;
        state.Characters[1] = character;
        state.Alive[1] = true;

        CharacterPhysics.HeavyStep(ref state.Characters[1]);

        Assert.Equal(ClassStats.For(PlayerClass.Soldier).RunPower, state.Characters[1].RunPower);
    }

    // --- the whole thing through a tick ---------------------------------------

    /// <summary>
    /// A heavy holding the trigger fires every other tick, which is what
    /// <c>refireTime = 2</c> means once the alarm stage is in the loop.
    /// </summary>
    /// <remarks>
    /// Counted in <b>ammo spent</b> rather than in live bullets: a bullet can
    /// die on the map, and the point here is the cadence and not the flight.
    /// </remarks>
    [Fact]
    public void AHeldTriggerFiresEveryOtherTick()
    {
        var state = default(WorldState);
        Put(ref state, 1);

        Span<PlayerInput> firing = [default, Aiming(PlayerKeys.Attack)];

        for (var i = 0; i < 20; i++)
            MatchSimulation.Tick(World, ref state, firing);

        // Three ammo a shot, and nothing refills while the trigger is held.
        var shots = (200 - state.Weapons[1].AmmoCount) / 3;

        // Twenty ticks at one shot per two, give or take the first tick's
        // already-hot counter.
        Assert.InRange(shots, 9, 11);
    }
}
