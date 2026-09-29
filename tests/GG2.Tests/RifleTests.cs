using GG2.Maps;
using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Tests;

/// <summary>
/// The sniper's Rifle: the scope, the charge, and the only hitscan in the game.
/// </summary>
/// <remarks>
/// <c>Objects/Weapons/Rifle.events/{Create, Begin Step, User Event 1, User
/// Event 3}.xml</c>, <c>Scripts/Misc/toggleZoom.gml</c> and
/// <c>Scripts/GameServer/serverToggleZoom.gml</c>.
/// <para>
/// <b>No recording gates any of it</b> — every fixture was recorded with
/// <c>[Classlimits]</c> holding the bots to the classes the port had, so no
/// sniper has ever appeared in one. These tests are the whole gate.
/// </para>
/// </remarks>
public sealed class RifleTests
{
    private const string Map = "ctf_truefort";

    /// <summary>A standing position with the floor under it.</summary>
    private const double OpenX = 2600;

    private const double OpenY = 708;

    /// <summary>
    /// A position with <b>1,500 pixels of clear air</b> to its right — the whole
    /// of a fully charged rifle's range.
    /// </summary>
    /// <remarks>
    /// Found by scanning the map rather than guessed: the range tests need a
    /// corridor no wall interrupts, because <c>Obstacle</c> is first in the
    /// hitscan's object list and would win over the victim.
    /// </remarks>
    private const double ClearX = 356;

    private const double ClearY = 268;

    /// <summary>A position with clear air for 100 pixels and a wall by 400.</summary>
    private const double WallX = 1188;

    private const double WallY = 100;

    private static CollisionWorld? _sharedWorld;

    private static CollisionWorld World => _sharedWorld ??=
        new CollisionWorld(LevelMap.FromPng(Map, ReferenceTree.ReadMap(Map)));

    private static void Put(
        ref WorldState state,
        int slot,
        double x,
        double y,
        Team team = Team.Red,
        PlayerClass playerClass = PlayerClass.Sniper,
        double aimDegrees = 0)
    {
        var character = CharacterState.Fresh(playerClass, team);
        character.X = x;
        character.Y = y;
        character.NetAimDirection = (ushort)Math.Round(aimDegrees * 65536.0 / 360.0);

        state.Characters[slot] = character;
        state.Weapons[slot] = WeaponState.Spawn(playerClass, x, y);
        state.Weapons[slot].X = x;
        state.Weapons[slot].Y = y;
        state.Weapons[slot].ReadyToShoot = true;
        state.Weapons[slot].RefireTimer = WeaponState.Inactive;
        state.Alive[slot] = true;

        state.AddPlayer(slot);
        state.Players[slot].Team = team;
        state.Players[slot].Class = playerClass;
    }

    /// <summary>Pulls the trigger once, through the real fire path.</summary>
    private static void Fire(ref WorldState state, int slot = 1)
    {
        state.Characters[slot].KeyState = PlayerKeys.Attack;

        var events = default(TickEvents);
        Weapons.FireStep(World, ref state, slot, ref events);
    }

    /// <summary>Runs the weapon's own Begin Step, which is where the charge is.</summary>
    private static void WeaponTick(ref WorldState state, int slot = 1) =>
        Weapons.WeaponBeginStep(
            ref state.Weapons[slot],
            state.Characters[slot].Class,
            state.Characters[slot].Zoomed);

    // --- the scope ------------------------------------------------------------

    /// <summary>
    /// Zoom changes the <b>physics</b>: a scoped sniper accelerates at 0.6
    /// rather than their class's 0.9 and jumps two units lower.
    /// </summary>
    [Fact]
    public void ScopingSlowsTheSniperDownAndLowersTheirJump()
    {
        var state = default(WorldState);
        Put(ref state, 1, OpenX, OpenY);

        Assert.Equal(ClassStats.For(PlayerClass.Sniper).RunPower, state.Characters[1].RunPower);
        Assert.Equal(ClassStats.BaseJumpStrength, state.Characters[1].JumpStrength);

        Assert.True(PlayerCommands.ToggleZoom(ref state, 1));

        Assert.True(state.Characters[1].Zoomed);
        Assert.Equal(Gml.PointSix, state.Characters[1].RunPower);
        Assert.Equal(6 + (Gml.PointSix / 2), state.Characters[1].JumpStrength);

        // And back again.
        Assert.True(PlayerCommands.ToggleZoom(ref state, 1));

        Assert.False(state.Characters[1].Zoomed);
        Assert.Equal(ClassStats.For(PlayerClass.Sniper).RunPower, state.Characters[1].RunPower);
        Assert.Equal(ClassStats.BaseJumpStrength, state.Characters[1].JumpStrength);
    }

    /// <summary>
    /// <b><c>basemaxspeed</c> is not recomputed when the run power drops</b>,
    /// so the acceleration <em>guard</em> stays at the unscoped value.
    /// </summary>
    /// <remarks>
    /// <b>It is a guard, not a speed cap</b>, which is easy to get backwards.
    /// A character's terminal speed
    /// comes from the friction balance and really is lower while scoped
    /// (0.6/0.9 of it, measured at 3.4 against 5.1); what <c>basemaxspeed</c>
    /// does is decide whether the acceleration is applied at all
    /// (<c>if hspeed &lt;= basemaxspeed</c>).
    /// <para>
    /// So the observable is a scoped sniper travelling <em>faster</em> than their
    /// own equilibrium — pushed there by a blast, say — who still accelerates,
    /// because the guard is the unscoped 5.1 and not the scoped 3.4. A port that
    /// derived the guard from the current run power would refuse.
    /// </para>
    /// </remarks>
    [Fact]
    public void ScopingDoesNotLowerTheAccelerationGuard()
    {
        var stats = ClassStats.For(PlayerClass.Sniper);

        // Between the scoped equilibrium (about 3.4) and the unscoped guard
        // (about 5.1), so the two readings of basemaxspeed disagree here.
        var between = stats.BaseMaxSpeed * 0.8;

        var pushing = default(WorldState);
        Put(ref pushing, 1, ClearX, ClearY);
        PlayerCommands.ToggleZoom(ref pushing, 1);
        pushing.Characters[1].HSpeed = between;

        var coasting = default(WorldState);
        Put(ref coasting, 1, ClearX, ClearY);
        PlayerCommands.ToggleZoom(ref coasting, 1);
        coasting.Characters[1].HSpeed = between;

        Span<PlayerInput> right = [default, new PlayerInput(PlayerKeys.Right, 0, 0)];
        Span<PlayerInput> idle = [default, new PlayerInput(PlayerKeys.None, 0, 0)];

        MatchSimulation.Tick(World, ref pushing, right);
        MatchSimulation.Tick(World, ref coasting, idle);

        Assert.True(pushing.Characters[1].HSpeed > coasting.Characters[1].HSpeed,
            "a scoped sniper above their own equilibrium did not accelerate, "
            + "so the guard was taken from the scoped run power");
    }

    /// <summary>
    /// And the equilibrium itself <em>is</em> lower while scoped — two thirds of
    /// it, which is the ratio of the run powers.
    /// </summary>
    [Fact]
    public void ScopingLowersTheSpeedASniperSettlesAt()
    {
        var walking = default(WorldState);
        Put(ref walking, 1, ClearX, ClearY);

        var scoped = default(WorldState);
        Put(ref scoped, 1, ClearX, ClearY);
        PlayerCommands.ToggleZoom(ref scoped, 1);

        Span<PlayerInput> right = [default, new PlayerInput(PlayerKeys.Right, 0, 0)];

        // Held in place horizontally so the map cannot interrupt the run; only
        // the speed is under test.
        for (var i = 0; i < 400; i++)
        {
            walking.Characters[1].X = ClearX;
            scoped.Characters[1].X = ClearX;

            MatchSimulation.Tick(World, ref walking, right);
            MatchSimulation.Tick(World, ref scoped, right);
        }

        var ratio = scoped.Characters[1].HSpeed / walking.Characters[1].HSpeed;

        Assert.Equal(Gml.PointSix / ClassStats.For(PlayerClass.Sniper).RunPower, ratio, 6);
    }

    /// <summary>Only a live sniper may zoom.</summary>
    [Theory]
    [InlineData(PlayerClass.Sniper, true, true)]
    [InlineData(PlayerClass.Soldier, true, false)]
    [InlineData(PlayerClass.Sniper, false, false)]
    public void OnlyALiveSniperMayZoom(PlayerClass playerClass, bool alive, bool accepted)
    {
        var state = default(WorldState);
        Put(ref state, 1, OpenX, OpenY, playerClass: playerClass);

        if (!alive)
            state.Clear(1);

        Assert.Equal(accepted, PlayerCommands.ToggleZoom(ref state, 1));
    }

    // --- the charge -----------------------------------------------------------

    /// <summary>An unscoped rifle deals 35 whatever it has been doing.</summary>
    [Fact]
    public void AnUnscopedRifleAlwaysDealsThirtyFive()
    {
        var state = default(WorldState);
        Put(ref state, 1, OpenX, OpenY);

        for (var i = 0; i < 200; i++)
            WeaponTick(ref state);

        Assert.Equal(0, state.Weapons[1].Charge);
        Assert.Equal(35, state.Weapons[1].HitDamage);
    }

    /// <summary>
    /// The ramp is a <b>square root</b>, so a quarter of the charge time is
    /// already half the bonus — and the <c>floor</c> keeps the damage an
    /// integer.
    /// </summary>
    /// <remarks>
    /// The counts are <em>ticks of Begin Step</em>, and each one both charges
    /// and recomputes — so after n of them the charge is n and the damage is
    /// <c>45 + floor(sqrt(n/105)*30)</c>. A quarter of the way in is already
    /// half the bonus.
    /// </remarks>
    [Theory]
    [InlineData(1, 47)]      // one tick already buys two damage: sqrt is steep here
    [InlineData(26, 59)]     // a quarter of the time: sqrt(0.2476)*30 = 14.9
    [InlineData(105, 75)]    // full
    [InlineData(200, 75)]    // clamped
    public void AScopedRifleChargesAlongASquareRoot(int ticks, double damage)
    {
        var state = default(WorldState);
        Put(ref state, 1, OpenX, OpenY);
        PlayerCommands.ToggleZoom(ref state, 1);

        for (var i = 0; i < ticks; i++)
            WeaponTick(ref state);

        Assert.Equal(damage, state.Weapons[1].HitDamage);
    }

    /// <summary>
    /// <b>Unscoping throws a full charge away instantly</b> — there is no
    /// decay, the <c>else</c> is <c>t = 0</c>.
    /// </summary>
    [Fact]
    public void UnscopingThrowsTheChargeAway()
    {
        var state = default(WorldState);
        Put(ref state, 1, OpenX, OpenY);
        PlayerCommands.ToggleZoom(ref state, 1);

        for (var i = 0; i < 105; i++)
            WeaponTick(ref state);

        Assert.Equal(105, state.Weapons[1].Charge);

        PlayerCommands.ToggleZoom(ref state, 1);
        WeaponTick(ref state);

        Assert.Equal(0, state.Weapons[1].Charge);
        Assert.Equal(35, state.Weapons[1].HitDamage);
    }

    /// <summary>
    /// <b>Firing throws it away too</b>, because the shot clears
    /// <c>readyToShoot</c> and the charge needs it.
    /// </summary>
    [Fact]
    public void FiringThrowsTheChargeAway()
    {
        var state = default(WorldState);
        Put(ref state, 1, OpenX, OpenY, aimDegrees: 90);
        PlayerCommands.ToggleZoom(ref state, 1);

        for (var i = 0; i < 105; i++)
            WeaponTick(ref state);

        Assert.Equal(105, state.Weapons[1].Charge);

        Fire(ref state);
        WeaponTick(ref state);

        Assert.Equal(0, state.Weapons[1].Charge);
    }

    // --- the shot -------------------------------------------------------------

    /// <summary>
    /// The Rifle has <b>no magazine</b> — <c>maxAmmo = 0</c> — so the shared
    /// ready-and-loaded guard would silence it for ever.
    /// </summary>
    [Fact]
    public void ARifleFiresWithNoAmmoAtAll()
    {
        var state = default(WorldState);
        Put(ref state, 1, OpenX, OpenY, aimDegrees: 90);

        Assert.Equal(0, state.Weapons[1].AmmoCount);

        Fire(ref state);

        Assert.False(state.Weapons[1].ReadyToShoot);
    }

    /// <summary>
    /// A scoped shot takes <b>60</b> ticks to come back and an unscoped one
    /// 40 — <c>alarm[0] = reloadTime + 20*owner.zoomed</c>.
    /// </summary>
    [Theory]
    [InlineData(false, 40)]
    [InlineData(true, 60)]
    public void AScopedShotTakesTwentyTicksLongerToComeBack(bool zoomed, int refire)
    {
        var state = default(WorldState);
        Put(ref state, 1, OpenX, OpenY, aimDegrees: 90);

        if (zoomed)
            PlayerCommands.ToggleZoom(ref state, 1);

        WeaponTick(ref state);
        Fire(ref state);

        Assert.Equal(refire, state.Weapons[1].RefireTimer);
    }

    /// <summary>It is seeded, so it broadcasts a <c>WEAPON_FIRE</c>.</summary>
    /// <remarks>
    /// The seed is drawn, sent, and then read by nothing — the Rifle's User
    /// Event 3 uses no randomness at all, exactly like the Rocketlauncher's.
    /// </remarks>
    [Fact]
    public void ARifleBroadcastsASeedItNeverUses()
    {
        var state = default(WorldState);
        Put(ref state, 1, OpenX, OpenY, aimDegrees: 90);

        state.Characters[1].KeyState = PlayerKeys.Attack;
        var events = default(TickEvents);
        Weapons.FireStep(World, ref state, 1, ref events);

        Assert.True(events.Fired[1]);
    }

    /// <summary>A rifle shot hits an enemy in front of it, and hurts them.</summary>
    [Fact]
    public void AShotHitsAnEnemyInItsPath()
    {
        var state = default(WorldState);
        Put(ref state, 1, OpenX, OpenY, Team.Red, aimDegrees: 0);
        Put(ref state, 2, OpenX + 200, OpenY, Team.Blue);

        var before = state.Characters[2].Hp;

        WeaponTick(ref state);
        Fire(ref state);

        Assert.Equal(before - 35, state.Characters[2].Hp);
        Assert.Equal(1, state.Characters[2].LastDamageDealer);
        Assert.Equal(DamageSource.Rifle, state.Characters[2].LastDamageSource);
    }

    /// <summary>A charged shot names the other damage source and deals more.</summary>
    [Fact]
    public void AChargedShotNamesItsOwnDamageSource()
    {
        var state = default(WorldState);
        Put(ref state, 1, OpenX, OpenY, Team.Red, aimDegrees: 0);
        Put(ref state, 2, OpenX + 200, OpenY, Team.Blue);

        PlayerCommands.ToggleZoom(ref state, 1);

        for (var i = 0; i < 110; i++)
            WeaponTick(ref state);

        var before = state.Characters[2].Hp;
        Fire(ref state);

        Assert.Equal(before - 75, state.Characters[2].Hp);
        Assert.Equal(DamageSource.RifleCharged, state.Characters[2].LastDamageSource);
    }

    /// <summary>
    /// <b>A team-mate stops the bullet and takes nothing.</b> Only the
    /// shooter's own mask is blanked, so body-blocking a sniper works.
    /// </summary>
    [Fact]
    public void ATeamMateBlocksTheShotAndTakesNothing()
    {
        var state = default(WorldState);
        Put(ref state, 1, ClearX, ClearY, Team.Red, aimDegrees: 0);
        Put(ref state, 2, ClearX + 60, ClearY, Team.Red);
        Put(ref state, 3, ClearX + 200, ClearY, Team.Blue);

        var mate = state.Characters[2].Hp;
        var enemy = state.Characters[3].Hp;

        WeaponTick(ref state);
        Fire(ref state);

        Assert.Equal(mate, state.Characters[2].Hp);
        Assert.Equal(enemy, state.Characters[3].Hp);
    }

    /// <summary>An ubered enemy stops it and takes nothing either.</summary>
    [Fact]
    public void AnUberedEnemyStopsTheShotAndTakesNothing()
    {
        var state = default(WorldState);
        Put(ref state, 1, ClearX, ClearY, Team.Red, aimDegrees: 0);
        Put(ref state, 2, ClearX + 60, ClearY, Team.Blue);
        Put(ref state, 3, ClearX + 200, ClearY, Team.Blue);

        state.Characters[2].Ubered = true;

        var ubered = state.Characters[2].Hp;
        var behind = state.Characters[3].Hp;

        WeaponTick(ref state);
        Fire(ref state);

        Assert.Equal(ubered, state.Characters[2].Hp);
        Assert.Equal(behind, state.Characters[3].Hp);
    }

    /// <summary>The shooter's own body never stops the shot.</summary>
    [Fact]
    public void TheShooterIsInvisibleToTheirOwnShot()
    {
        var state = default(WorldState);
        Put(ref state, 1, OpenX, OpenY, Team.Red, aimDegrees: 0);
        Put(ref state, 2, OpenX + 200, OpenY, Team.Blue);

        WeaponTick(ref state);
        Fire(ref state);

        // If the shooter blocked it, the enemy would be untouched.
        Assert.True(state.Characters[2].Hp < ClassStats.For(PlayerClass.Sniper).MaxHp);
    }

    /// <summary>
    /// <b>The range is the damage.</b> An unscoped shot carries 700 pixels;
    /// an enemy past that is out of reach even in clear air.
    /// </summary>
    [Fact]
    public void AnUnscopedShotRunsOutAtSevenHundredPixels()
    {
        var near = default(WorldState);
        Put(ref near, 1, ClearX, ClearY, Team.Red, aimDegrees: 0);
        Put(ref near, 2, ClearX + 600, ClearY, Team.Blue);

        WeaponTick(ref near);
        Fire(ref near);

        Assert.True(near.Characters[2].Hp < ClassStats.For(PlayerClass.Sniper).MaxHp,
            "an enemy inside the unscoped range was not hit");

        // The control: the same shot cannot reach past 700.
        var far = default(WorldState);
        Put(ref far, 1, ClearX, ClearY, Team.Red, aimDegrees: 0);
        Put(ref far, 2, ClearX + 900, ClearY, Team.Blue);

        var untouched = far.Characters[2].Hp;

        WeaponTick(ref far);
        Fire(ref far);

        Assert.Equal(untouched, far.Characters[2].Hp);
    }

    /// <summary>
    /// And a fully charged shot reaches the whole 1,500 — the same enemy the
    /// unscoped shot could not touch.
    /// </summary>
    [Fact]
    public void AChargedShotReachesTwiceAsFar()
    {
        var state = default(WorldState);
        Put(ref state, 1, ClearX, ClearY, Team.Red, aimDegrees: 0);
        Put(ref state, 2, ClearX + 900, ClearY, Team.Blue);

        PlayerCommands.ToggleZoom(ref state, 1);

        for (var i = 0; i < 110; i++)
            WeaponTick(ref state);

        var before = state.Characters[2].Hp;
        Fire(ref state);

        Assert.Equal(before - 75, state.Characters[2].Hp);
    }

    /// <summary>
    /// <b>The map beats everything behind it.</b> <c>Obstacle</c> is first in
    /// the reference's object list, so a wall in the same half of the search
    /// wins over a player.
    /// </summary>
    [Fact]
    public void AWallStopsTheShot()
    {
        var state = default(WorldState);
        Put(ref state, 1, WallX, WallY, Team.Red, aimDegrees: 0);
        Put(ref state, 2, WallX + 400, WallY, Team.Blue);

        // The premise, asserted rather than assumed: clear air for the first
        // hundred pixels and solid map by four hundred.
        Assert.False(World.CollisionLineHitsMask(WallX, WallY, WallX + 100, WallY));
        Assert.True(World.CollisionLineHitsMask(WallX, WallY, WallX + 400, WallY));

        var before = state.Characters[2].Hp;

        WeaponTick(ref state);
        Fire(ref state);

        Assert.Equal(before, state.Characters[2].Hp);
    }

    /// <summary>
    /// A jump taken while scoped is two units weaker — the physics reading the
    /// character's own <c>jumpStrength</c> rather than the class constant.
    /// </summary>
    [Fact]
    public void AScopedSniperJumpsLower()
    {
        var walking = default(WorldState);
        Put(ref walking, 1, OpenX, OpenY);

        var scoped = default(WorldState);
        Put(ref scoped, 1, OpenX, OpenY);
        PlayerCommands.ToggleZoom(ref scoped, 1);

        // Land first, so the jump is a ground jump.
        for (var i = 0; i < 10; i++)
        {
            MatchSimulation.Tick(World, ref walking, []);
            MatchSimulation.Tick(World, ref scoped, []);
        }

        Assert.True(walking.Characters[1].OnGround, "the unscoped sniper never landed");
        Assert.True(scoped.Characters[1].OnGround, "the scoped sniper never landed");

        Span<PlayerInput> jump = [default, new PlayerInput(PlayerKeys.Jump, 0, 0)];
        MatchSimulation.Tick(World, ref walking, jump);
        MatchSimulation.Tick(World, ref scoped, jump);

        // Up is negative, so the scoped one is the *less* negative.
        Assert.True(scoped.Characters[1].VSpeed > walking.Characters[1].VSpeed,
            $"scoped {scoped.Characters[1].VSpeed} against {walking.Characters[1].VSpeed}");
    }

    /// <summary>
    /// The zoom reaches the weapon through a real tick, not only when a test
    /// hands it over.
    /// </summary>
    /// <remarks>
    /// The control that <c>MatchSimulation</c> passes the owner's
    /// <c>zoomed</c> into the weapon's own Begin Step — a weapon has no way back
    /// to its character in this state layout, so it has to be handed across.
    /// </remarks>
    [Fact]
    public void TheChargeAccumulatesThroughARealTick()
    {
        var state = default(WorldState);
        Put(ref state, 1, OpenX, OpenY);
        PlayerCommands.ToggleZoom(ref state, 1);

        for (var i = 0; i < 20; i++)
            MatchSimulation.Tick(World, ref state, []);

        Assert.Equal(20, state.Weapons[1].Charge);
        Assert.True(state.Weapons[1].HitDamage > 45,
            $"the damage never rose above the base: {state.Weapons[1].HitDamage}");
    }

    /// <summary>
    /// A <b>gate</b> stops the shot too, not only the map's own mask — the
    /// hitscan's object list ends with the same <c>gunSetSolids</c> set every
    /// bullet is tested against.
    /// </summary>
    /// <remarks>
    /// The position is one where the map mask is clear for 300 pixels and a
    /// gate is not, found by scanning; it is asserted rather than assumed so
    /// this cannot quietly stop testing what it says.
    /// </remarks>
    [Fact]
    public void AGateStopsTheShotEvenWhereTheMapIsClear()
    {
        const double GateX = 1380;
        const double GateY = 444;

        var state = default(WorldState);
        Put(ref state, 1, GateX, GateY, Team.Red, aimDegrees: 0);
        Put(ref state, 2, GateX + 600, GateY, Team.Blue);

        // The victim has to be well clear of the gate, not merely behind it.
        // A character is tested *before* a gate in the reference's object list,
        // so one sharing a sub-segment with the gate is hit through it.
        Assert.False(World.CollisionLineHitsMask(GateX, GateY, GateX + 600, GateY));
        Assert.True(World.CollisionLineBlocksBullets(
            GateX, GateY, GateX + 200, GateY, state.BulletFilter));

        var before = state.Characters[2].Hp;

        WeaponTick(ref state);
        Fire(ref state);

        Assert.Equal(before, state.Characters[2].Hp);
    }

    /// <summary>A rifle shot damages an enemy sentry it reaches.</summary>
    [Fact]
    public void AShotDamagesAnEnemySentry()
    {
        var state = default(WorldState);
        Put(ref state, 1, OpenX, OpenY, Team.Red, aimDegrees: 0);

        var sentry = SentryState.Build(Team.Blue, OpenX + 150, OpenY, 1);
        sentry.Built = true;
        sentry.Hp = SentryState.MaxHp;
        state.Sentries[2] = sentry;
        state.HasSentry[2] = true;
        state.AddPlayer(2);
        state.Players[2].Team = Team.Blue;
        state.Players[2].Class = PlayerClass.Engineer;

        WeaponTick(ref state);
        Fire(ref state);

        Assert.Equal(SentryState.MaxHp - 35, state.Sentries[2].Hp);
        Assert.Equal(DamageSource.Rifle, state.Sentries[2].LastDamageSource);
    }

    /// <summary>
    /// A <b>friendly</b> sentry is invisible to the shot — its mask is
    /// blanked — so it neither blocks nor takes damage.
    /// </summary>
    [Fact]
    public void AFriendlySentryIsInvisibleToTheShot()
    {
        var state = default(WorldState);
        Put(ref state, 1, OpenX, OpenY, Team.Red, aimDegrees: 0);
        Put(ref state, 2, OpenX + 300, OpenY, Team.Blue);

        var sentry = SentryState.Build(Team.Red, OpenX + 150, OpenY, 1);
        sentry.Built = true;
        sentry.Hp = SentryState.MaxHp;
        state.Sentries[3] = sentry;
        state.HasSentry[3] = true;
        state.AddPlayer(3);
        state.Players[3].Team = Team.Red;
        state.Players[3].Class = PlayerClass.Engineer;

        var before = state.Characters[2].Hp;

        WeaponTick(ref state);
        Fire(ref state);

        Assert.Equal(SentryState.MaxHp, state.Sentries[3].Hp);
        Assert.Equal(before - 35, state.Characters[2].Hp);
    }
}
