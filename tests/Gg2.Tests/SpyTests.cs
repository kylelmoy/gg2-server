using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// The spy: a cloak on the right click, and a Revolver whose attack key fires a
/// bullet out of cloak and buries a knife in it.
/// </summary>
/// <remarks>
/// <c>Objects/Weapons/Revolver.events/*</c>,
/// <c>Objects/Projectiles/{StabMask, StabAnim}.events/*</c>, and the cloak block
/// of <c>Objects/InGameElements/Character.events/Begin Step.xml</c>.
/// <para>
/// <b>No recording can gate any of it.</b> No fixture in the project contains a
/// spy — every one is scout, soldier, demoman, medic, pyro and engineer — and
/// the cloak reaches the wire only as one bit of a byte no recording has ever
/// carried a spy in. These tests are the whole gate, and each was checked by
/// breaking the line it is about.
/// </para>
/// </remarks>
public sealed class SpyTests
{
    private const string Map = "ctf_truefort";

    /// <summary>
    /// Open air with eighty clear pixels each side of it, found by scanning.
    /// </summary>
    /// <remarks>
    /// ⚠️ The knife needs the room: its box reaches 31 pixels forward and 24
    /// up, and every reach test here is really a line-of-sight test as well. A
    /// spot with only a character-sized clearance passes the box test and fails
    /// the line, which looks exactly like a wrong box.
    /// </remarks>
    private const double ClearX = 2280;

    private const double ClearY = 200;

    private static CollisionWorld? _sharedWorld;

    private static CollisionWorld World => _sharedWorld ??=
        new CollisionWorld(LevelMap.FromPng(Map, ReferenceTree.ReadMap(Map)));

    private static WeaponStats Stats => WeaponStats.For(WeaponKind.Revolver);

    /// <summary>A spy, ready to shoot and ready to stab, aiming as told.</summary>
    private static void Put(
        ref WorldState state,
        int slot,
        double x = ClearX,
        double y = ClearY,
        PlayerKeys keys = PlayerKeys.None,
        double aimDegrees = 0,
        Team team = Team.Red,
        PlayerClass playerClass = PlayerClass.Spy)
    {
        var character = CharacterState.Fresh(playerClass, team);
        character.X = x;
        character.Y = y;
        character.NetAimDirection = (ushort)Math.Round(aimDegrees * 65536.0 / 360.0);
        character.KeyState = keys;
        character.Facing = 1;

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

    private static ProjectileState Live(in WorldState state, int index) =>
        state.Projectiles[state.LiveProjectileSlot(index)];

    private static int CountOf(in WorldState state, ProjectileKind kind)
    {
        var n = 0;
        for (var i = 0; i < state.LiveProjectileCount; i++)
            if (Live(state, i).Kind == kind)
                n++;

        return n;
    }

    private static void Attack(ref WorldState state, int slot = 1)
    {
        var events = default(TickEvents);
        Weapons.FireStep(World, ref state, slot, ref events);
    }

    /// <summary>The cloak key, which is the right click, as a fresh press.</summary>
    private static void PressCloak(ref WorldState state, int slot = 1)
    {
        state.Characters[slot].PressedKeys = PlayerKeys.Special;
        CharacterPhysics.CloakStep(ref state.Characters[slot], ref state.Weapons[slot]);
        state.Characters[slot].PressedKeys = PlayerKeys.None;
    }

    // --- the cloak ------------------------------------------------------------

    /// <summary>A spy starts visible, ready to stab, with a full cylinder.</summary>
    [Fact]
    public void AFreshSpyIsVisibleAndArmed()
    {
        var state = default(WorldState);
        Put(ref state, 1);

        Assert.False(state.Characters[1].Cloak);
        Assert.Equal(1, state.Characters[1].CloakAlpha);
        Assert.True(state.Weapons[1].ReadyToStab);
        Assert.Equal(6, state.Weapons[1].AmmoCount);
    }

    /// <summary>The right click toggles the cloak, and it fades over twenty ticks.</summary>
    [Fact]
    public void TheCloakFadesOverTwentyTicks()
    {
        var state = default(WorldState);
        Put(ref state, 1);

        PressCloak(ref state);
        Assert.True(state.Characters[1].Cloak);
        Assert.Equal(1, state.Characters[1].CloakAlpha);

        for (var i = 0; i < 19; i++)
            CharacterPhysics.EndStep(World, ref state.Characters[1]);

        Assert.True(state.Characters[1].CloakAlpha > 0,
            $"already gone after 19 ticks: {state.Characters[1].CloakAlpha}");

        CharacterPhysics.EndStep(World, ref state.Characters[1]);

        // ⚠️ Exactly zero, not a residue — the reference clamps with
        // max(cloakAlpha - 0.05, 0) rather than by comparison.
        Assert.Equal(0, state.Characters[1].CloakAlpha);
    }

    /// <summary>And back in over twenty more.</summary>
    [Fact]
    public void UncloakingFadesBackIn()
    {
        var state = default(WorldState);
        Put(ref state, 1);
        state.Characters[1].Cloak = true;
        state.Characters[1].CloakAlpha = 0;

        PressCloak(ref state);
        Assert.False(state.Characters[1].Cloak);

        for (var i = 0; i < 20; i++)
            CharacterPhysics.EndStep(World, ref state.Characters[1]);

        Assert.Equal(1, state.Characters[1].CloakAlpha);
    }

    /// <summary>
    /// ⚠️ <b>A cloak can only be <em>started</em> from fully visible</b>, so a
    /// spy who drops it cannot vanish again until the twenty ticks of fade-in
    /// have run.
    /// </summary>
    [Fact]
    public void ASpyStillFadingBackInCannotCloakAgain()
    {
        var state = default(WorldState);
        Put(ref state, 1);
        state.Characters[1].CloakAlpha = 0.5;

        PressCloak(ref state);

        Assert.False(state.Characters[1].Cloak);
    }

    /// <summary>But dropping one is always allowed, however far the fade has got.</summary>
    [Fact]
    public void ACloakCanAlwaysBeDropped()
    {
        var state = default(WorldState);
        Put(ref state, 1);
        state.Characters[1].Cloak = true;
        state.Characters[1].CloakAlpha = 0.5;

        PressCloak(ref state);

        Assert.False(state.Characters[1].Cloak);
    }

    /// <summary>Only a class with <c>canCloak</c> — which is the spy alone.</summary>
    [Fact]
    public void NoOtherClassCanCloak()
    {
        var state = default(WorldState);
        Put(ref state, 1, playerClass: PlayerClass.Scout);

        PressCloak(ref state);

        Assert.False(state.Characters[1].Cloak);
    }

    /// <summary>
    /// ⚠️ <b><c>readyToStab</c> gates the toggle</b>, so the fifty ticks after a
    /// stab are fifty a spy cannot vanish or reappear.
    /// </summary>
    [Fact]
    public void ASpyOnStabCooldownCannotCloak()
    {
        var state = default(WorldState);
        Put(ref state, 1);
        state.Weapons[1].ReadyToStab = false;

        PressCloak(ref state);

        Assert.False(state.Characters[1].Cloak);
    }

    /// <summary>An intel carrier cannot cloak at all.</summary>
    [Fact]
    public void AnIntelCarrierCannotCloak()
    {
        var state = default(WorldState);
        Put(ref state, 1);
        state.Characters[1].CarryingIntel = true;

        PressCloak(ref state);

        Assert.False(state.Characters[1].Cloak);
    }

    /// <summary>
    /// ⚠️ And a spy who ends up carrying one while cloaked is uncloaked
    /// <em>outright</em>, not faded.
    /// </summary>
    [Fact]
    public void ACarryingSpyIsUncloakedOutright()
    {
        var state = default(WorldState);
        Put(ref state, 1);
        state.Characters[1].Cloak = true;
        state.Characters[1].CloakAlpha = 0;
        state.Characters[1].CarryingIntel = true;

        var events = default(TickEvents);
        Intel.BeginStep(ref state, 1, ref events);

        Assert.False(state.Characters[1].Cloak);
        Assert.Equal(1, state.Characters[1].CloakAlpha);
    }

    /// <summary>
    /// ⚠️ <b>Uncloaking costs the intel grab for 25 ticks</b> — and the alarm is
    /// a <c>max</c>, so a longer cooldown already running is not shortened.
    /// </summary>
    [Fact]
    public void UncloakingBlocksTheIntelGrab()
    {
        var state = default(WorldState);
        Put(ref state, 1);
        state.Characters[1].Cloak = true;
        state.Characters[1].CanGrabIntel = true;
        state.Characters[1].IntelGrabTimer = 40;

        PressCloak(ref state);

        Assert.False(state.Characters[1].CanGrabIntel);
        Assert.Equal(40, state.Characters[1].IntelGrabTimer);

        var shorter = default(WorldState);
        Put(ref shorter, 1);
        shorter.Characters[1].Cloak = true;
        shorter.Characters[1].IntelGrabTimer = WeaponState.Inactive;

        PressCloak(ref shorter);

        Assert.Equal(25, shorter.Characters[1].IntelGrabTimer);
    }

    /// <summary>Cloaking on does not touch the grab timer — only cloaking off does.</summary>
    [Fact]
    public void GoingInvisibleDoesNotBlockTheIntelGrab()
    {
        var state = default(WorldState);
        Put(ref state, 1);
        state.Characters[1].CanGrabIntel = true;

        PressCloak(ref state);

        Assert.True(state.Characters[1].Cloak);
        Assert.True(state.Characters[1].CanGrabIntel);
    }

    /// <summary>
    /// ⚠️ <b>The fade is frozen while a hit has the cloak flickering</b> — the
    /// two branches are an <c>else if</c>, so a flickering cloaked spy neither
    /// fades out nor back in.
    /// </summary>
    [Fact]
    public void AFlickeringCloakDoesNotFade()
    {
        var state = default(WorldState);
        Put(ref state, 1);
        state.Characters[1].Cloak = true;
        state.Characters[1].CloakAlpha = 0.5;
        state.Characters[1].CloakFlicker = true;

        CharacterPhysics.EndStep(World, ref state.Characters[1]);

        Assert.Equal(0.5, state.Characters[1].CloakAlpha);
    }

    /// <summary>The cloak is an edge, not a held key.</summary>
    [Fact]
    public void HoldingTheCloakKeyDoesNotFlicker()
    {
        var state = default(WorldState);
        Put(ref state, 1, keys: PlayerKeys.Special);

        CharacterPhysics.CloakStep(ref state.Characters[1], ref state.Weapons[1]);

        Assert.False(state.Characters[1].Cloak);
    }

    // --- the Revolver ---------------------------------------------------------

    /// <summary>One trigger pull is one bullet, and it costs one round.</summary>
    [Fact]
    public void OneShotIsOneBulletAndCostsOneRound()
    {
        var state = default(WorldState);
        Put(ref state, 1, keys: PlayerKeys.Attack);

        Attack(ref state);

        Assert.Equal(1, CountOf(state, ProjectileKind.Shot));
        Assert.Equal(5, state.Weapons[1].AmmoCount);
        Assert.Equal(28, Live(state, 0).HitDamage);
        Assert.False(state.Weapons[1].ReadyToShoot);
        Assert.Equal(Stats.RefireTime, state.Weapons[1].RefireTimer);
    }

    /// <summary>
    /// ⚠️ <b>The reload is <c>reloadBuffer + reloadTime</c> in one go</b> — 63
    /// ticks — and the whole cylinder comes back at once rather than a round at
    /// a time.
    /// </summary>
    [Fact]
    public void TheWholeCylinderComesBackAfterSixtyThreeTicks()
    {
        var state = default(WorldState);
        Put(ref state, 1, keys: PlayerKeys.Attack);

        Attack(ref state);
        Assert.Equal(Stats.ReloadBuffer + Stats.ReloadTime, state.Weapons[1].ReloadTimer);

        var events = default(TickEvents);
        for (var i = 0; i < Stats.ReloadBuffer + Stats.ReloadTime - 1; i++)
            Timers.Advance(ref state, null, ref events);

        // ⚠️ Emptied first: with a single round spent, "put one back" and
        // "put the whole cylinder back" are the same answer and the test gates
        // neither.
        state.Weapons[1].AmmoCount = 0;

        Timers.Advance(ref state, null, ref events);

        Assert.Equal(6, state.Weapons[1].AmmoCount);
    }

    /// <summary>
    /// ⚠️ <b>The muzzle is the weapon's own <c>x</c></b> — the only weapon with
    /// no <c>lengthdir</c> at all — five pixels up, and then a whole extra step
    /// forward before anything can test it.
    /// </summary>
    [Fact]
    public void TheBulletStartsAWholeStepAheadOfTheMuzzle()
    {
        var state = default(WorldState);
        Put(ref state, 1, keys: PlayerKeys.Attack);

        Attack(ref state);

        // Aimed right and standing still: 21 forward from the weapon.
        Assert.Equal(ClearX + 21, Live(state, 0).X, 9);
        Assert.Equal(ClearY - 5, Live(state, 0).Y, 9);
    }

    /// <summary>
    /// ⚠️ <b>The speed boost is a product of two speeds</b>, not the owner's
    /// alone: <c>speed += owner.hspeed * shot.hspeed / 15</c>. So it adds when
    /// the spy runs with the bullet, subtracts when they run against it, and is
    /// nothing at all straight up.
    /// </summary>
    [Fact]
    public void TheSpeedBoostIsAProductAndCanSubtract()
    {
        double SpeedOf(double runSpeed, double aimDegrees)
        {
            var state = default(WorldState);
            Put(ref state, 1, keys: PlayerKeys.Attack, aimDegrees: aimDegrees);
            state.Characters[1].Motion.SetComponents(runSpeed, 0);
            Attack(ref state);
            return Live(state, 0).Motion.Speed;
        }

        // 21 + 4 * 21 / 15.
        Assert.Equal(21 + (4 * 21 / 15.0), SpeedOf(4, 0), 9);

        // Aimed left: the shot's own hspeed is -21, so a rightward run slows it.
        Assert.Equal(21 - (4 * 21 / 15.0), SpeedOf(4, 180), 9);

        // Straight up: the shot's hspeed is 0, so the product is too.
        Assert.Equal(21, SpeedOf(4, 90), 9);
    }

    /// <summary>A cloaked spy does not shoot — the shot arm tests <c>!owner.cloak</c>.</summary>
    [Fact]
    public void ACloakedSpyDoesNotShoot()
    {
        var state = default(WorldState);
        Put(ref state, 1, keys: PlayerKeys.Attack);
        state.Characters[1].Cloak = true;
        state.Characters[1].CloakAlpha = 0;

        Attack(ref state);

        Assert.Equal(0, CountOf(state, ProjectileKind.Shot));
        Assert.Equal(6, state.Weapons[1].AmmoCount);
    }

    // --- the stab -------------------------------------------------------------

    /// <summary>
    /// ⚠️ <b>The attack key stabs when the spy is cloaked</b>, and nothing takes
    /// damage for another thirty-two ticks.
    /// </summary>
    [Fact]
    public void ACloakedSpyStabsAndNothingHappensYet()
    {
        var state = default(WorldState);
        Put(ref state, 1, keys: PlayerKeys.Attack);
        state.Characters[1].Cloak = true;

        Attack(ref state);

        Assert.Equal(0, state.LiveProjectileCount);
        Assert.False(state.Weapons[1].ReadyToStab);
        Assert.Equal(32, state.Weapons[1].StabTimer);
        Assert.True(state.Characters[1].Stabbing);
        Assert.Equal(0, state.Characters[1].RunPower);
        Assert.Equal(0, state.Characters[1].JumpStrength);
    }

    /// <summary>
    /// ⚠️ <b>A spy holding the cloak key cannot stab</b> — that is what stops the
    /// press that uncloaks them also burying a knife.
    /// </summary>
    [Fact]
    public void ASpyHoldingTheCloakKeyDoesNotStab()
    {
        var state = default(WorldState);
        Put(ref state, 1, keys: PlayerKeys.Attack | PlayerKeys.Special);
        state.Characters[1].Cloak = true;

        Attack(ref state);

        Assert.False(state.Characters[1].Stabbing);
        Assert.True(state.Weapons[1].ReadyToStab);
    }

    /// <summary>
    /// ⚠️ <b>An uncloaked spy who <em>cannot</em> shoot does not stab
    /// either.</b> The <c>else</c> arm has its own <c>owner.cloak</c> clause, so
    /// an empty cylinder is silence rather than a knife.
    /// </summary>
    /// <remarks>
    /// The control for that clause: every other test here reaches the stab arm
    /// only when the spy is cloaked, so deleting it changes nothing they see.
    /// </remarks>
    [Fact]
    public void AnUncloakedSpyOutOfAmmoDoesNothingAtAll()
    {
        var state = default(WorldState);
        Put(ref state, 1, keys: PlayerKeys.Attack);
        state.Weapons[1].AmmoCount = 0;

        Attack(ref state);

        Assert.Equal(0, state.LiveProjectileCount);
        Assert.False(state.Characters[1].Stabbing);
        Assert.True(state.Weapons[1].ReadyToStab);
    }

    /// <summary>And a cloaked spy on stab cooldown does nothing either.</summary>
    [Fact]
    public void ACloakedSpyOnCooldownDoesNotStab()
    {
        var state = default(WorldState);
        Put(ref state, 1, keys: PlayerKeys.Attack);
        state.Characters[1].Cloak = true;
        state.Weapons[1].ReadyToStab = false;

        Attack(ref state);

        Assert.False(state.Characters[1].Stabbing);
        Assert.Equal(0, state.Weapons[1].StabTimer);
    }

    /// <summary>An uncloaked spy shoots instead, off the same key.</summary>
    [Fact]
    public void AnUncloakedSpyShootsRatherThanStabbing()
    {
        var state = default(WorldState);
        Put(ref state, 1, keys: PlayerKeys.Attack);

        Attack(ref state);

        Assert.Equal(1, CountOf(state, ProjectileKind.Shot));
        Assert.False(state.Characters[1].Stabbing);
    }

    /// <summary>
    /// The knife's hitbox arrives thirty-two ticks later and the flag comes back
    /// eighteen after that — a fifty-tick cooldown in total.
    /// </summary>
    [Fact]
    public void TheKnifeArrivesLateAndTheCooldownIsFifty()
    {
        var state = default(WorldState);
        Put(ref state, 1, keys: PlayerKeys.Attack);
        state.Characters[1].Cloak = true;

        Attack(ref state);

        var events = default(TickEvents);

        // ⚠️ Literals, not Weapons.StabWindUp — a test that counts with the
        // constant it is testing cannot fail when the constant changes, which
        // is how four of these survived their first mutation run.
        for (var i = 0; i < 31; i++)
            Timers.Advance(ref state, null, ref events);

        Assert.Equal(0, CountOf(state, ProjectileKind.Stab));

        Timers.Advance(ref state, null, ref events);

        Assert.Equal(1, CountOf(state, ProjectileKind.Stab));
        Assert.False(state.Weapons[1].ReadyToStab);

        // ⚠️ The two alarms cost 49 more ticks and not 50, because GM8's
        // alarm stage is index-major: alarm[1]'s event sets alarm[2] and the
        // same stage then walks index 2 and spends one of it. Fifty ticks from
        // the stab, which is the number that matters.
        for (var i = 0; i < 16; i++)
            Timers.Advance(ref state, null, ref events);

        Assert.False(state.Weapons[1].ReadyToStab);

        Timers.Advance(ref state, null, ref events);

        Assert.True(state.Weapons[1].ReadyToStab);
    }

    /// <summary>And the hitbox itself lives only six ticks.</summary>
    [Fact]
    public void TheKnifeLivesSixTicks()
    {
        var state = default(WorldState);
        Put(ref state, 1);
        Weapons.CreateStabMask(ref state, 1);

        var handle = state.LiveProjectileHandle(0);
        var events = default(TickEvents);

        for (var i = 0; i < 5; i++)
            Timers.Advance(ref state, null, ref events);

        Assert.True(state.IsLive(handle));

        Timers.Advance(ref state, null, ref events);

        Assert.False(state.IsLive(handle));
    }

    /// <summary>
    /// ⚠️ <b>The freeze lasts fifty-two ticks</b>, long after the knife has come
    /// and gone, and it is the animation that ends it.
    /// </summary>
    [Fact]
    public void TheFreezeOutlastsTheKnifeByTwiceItsWindUp()
    {
        var state = default(WorldState);
        Put(ref state, 1, keys: PlayerKeys.Attack);
        state.Characters[1].Cloak = true;

        Attack(ref state);

        for (var i = 0; i < 51; i++)
            Weapons.StabAnimStep(ref state);

        Assert.True(state.Characters[1].Stabbing);
        Assert.Equal(0, state.Characters[1].RunPower);

        Weapons.StabAnimStep(ref state);

        Assert.False(state.Characters[1].Stabbing);
        Assert.Equal(ClassStats.For(PlayerClass.Spy).RunPower, state.Characters[1].RunPower);
        Assert.Equal(ClassStats.BaseJumpStrength, state.Characters[1].JumpStrength);
    }

    /// <summary>
    /// ⚠️ <b>A stabbing spy cannot jump</b>, and the guard is its own — a zeroed
    /// <c>jumpStrength</c> is not enough, because the test above it compares
    /// against that same zero and passes.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The spy has to be <em>moving downwards</em> for this to test
    /// anything.</b> The guard above the flag is
    /// <c>vspeed &gt; -jumpStrength</c>, and with a stabbing spy's zeroed
    /// jumpStrength that is <c>vspeed &gt; 0</c> — which a spy resting on the
    /// floor fails, so the whole block is skipped and the flag is never reached.
    /// A first version of this test landed the spy first and passed with the
    /// guard deleted.
    /// </remarks>
    [Fact]
    public void AStabbingSpyCannotJump()
    {
        double JumpFrom(bool stabbing)
        {
            // Placed on the floor rather than settled onto it: twenty ticks of
            // gravity leave the character a fraction inside it, which makes
            // BeginStepInput's `stuckInWall` true and skips the jump entirely
            // — so both arms returned the same answer and the test was vacuous.
            var state = default(WorldState);
            Put(ref state, 1, x: 2600, y: 708);

            Assert.True(World.PlaceFree(state.Characters[1].Box, 2600, 708, state.Characters[1].Filter),
                "the standing position is inside the map");
            Assert.False(World.PlaceFree(state.Characters[1].Box, 2600, 709, state.Characters[1].Filter),
                "there is no floor under the standing position");

            state.Characters[1].Stabbing = stabbing;
            state.Characters[1].JumpStrength = 0;
            state.Characters[1].VSpeed = 2;
            state.Characters[1].PressedKeys = PlayerKeys.Jump;

            CharacterPhysics.BeginStepInput(World, ref state.Characters[1]);

            return state.Characters[1].VSpeed;
        }

        // The control: without the flag the jump *is* taken, and setting vspeed
        // to -0 cancels the fall.
        Assert.Equal(0, JumpFrom(stabbing: false));

        // With it, the spy keeps falling.
        Assert.Equal(2, JumpFrom(stabbing: true));
    }

    /// <summary>A stabbing spy does not accelerate either: <c>runPower</c> is 0.</summary>
    [Fact]
    public void AStabbingSpyDoesNotAccelerate()
    {
        var state = default(WorldState);
        Put(ref state, 1, keys: PlayerKeys.Attack);
        state.Characters[1].Cloak = true;
        Attack(ref state);

        Span<PlayerInput> right = [default, new PlayerInput(PlayerKeys.Right, 0, 0)];

        for (var i = 0; i < 10; i++)
            MatchSimulation.Tick(World, ref state, right);

        Assert.Equal(0, state.Characters[1].HSpeed);
    }

    // --- what the knife does --------------------------------------------------

    /// <summary>Two hundred damage, which is more than any class has.</summary>
    [Fact]
    public void AKnifeDealsTwoHundred()
    {
        var state = default(WorldState);
        Put(ref state, 1);
        Put(ref state, 2, x: ClearX + 10, y: ClearY, team: Team.Blue, playerClass: PlayerClass.Heavy);

        var before = state.Characters[2].Hp;
        Weapons.CreateStabMask(ref state, 1);
        Projectiles.StabStep(ref state);
        Projectiles.Collide(World, ref state, ref DiscardedEvents.Events);

        Assert.Equal(before - 200, state.Characters[2].Hp);
        Assert.Equal(0, state.Characters[2].Hp);
    }

    /// <summary>
    /// ⚠️ <b>One knife takes two people.</b> <c>maxKills = 2</c>, so a stab that
    /// lands on a medic still has one left for the patient behind them.
    /// </summary>
    [Fact]
    public void OneKnifeTakesTwoAndThenGoesAway()
    {
        var state = default(WorldState);
        Put(ref state, 1);
        Put(ref state, 2, x: ClearX + 6, y: ClearY, team: Team.Blue, playerClass: PlayerClass.Heavy);
        Put(ref state, 3, x: ClearX + 12, y: ClearY, team: Team.Blue, playerClass: PlayerClass.Heavy);
        Put(ref state, 4, x: ClearX + 18, y: ClearY, team: Team.Blue, playerClass: PlayerClass.Heavy);

        Weapons.CreateStabMask(ref state, 1);
        Projectiles.StabStep(ref state);
        Projectiles.Collide(World, ref state, ref DiscardedEvents.Events);

        Assert.Equal(2, Hurt(state, 2) + Hurt(state, 3) + Hurt(state, 4));
        Assert.Equal(0, CountOf(state, ProjectileKind.Stab));

        static int Hurt(in WorldState state, int slot) =>
            state.Characters[slot].Hp < 200 ? 1 : 0;
    }

    /// <summary>
    /// ⚠️ <b>The two facings agreeing is what makes it a backstab</b> — the
    /// victim's against the <em>knife's</em>, which is the direction the spy
    /// committed to and not where they are looking now.
    /// </summary>
    [Fact]
    public void FacingTheSameWayIsABackstab()
    {
        DamageSource SourceFor(int victimFacing)
        {
            var state = default(WorldState);
            Put(ref state, 1);
            Put(ref state, 2, x: ClearX + 10, y: ClearY, team: Team.Blue, playerClass: PlayerClass.Heavy);
            state.Characters[2].Facing = victimFacing;

            // Aimed right, so the knife's own scale is +1.
            state.Weapons[1].StabDirection = 0;
            Weapons.CreateStabMask(ref state, 1);
            Projectiles.StabStep(ref state);
            Projectiles.Collide(World, ref state, ref DiscardedEvents.Events);

            return state.Characters[2].LastDamageSource;
        }

        Assert.Equal(DamageSource.Backstab, SourceFor(1));
        Assert.Equal(DamageSource.Knife, SourceFor(-1));
    }

    /// <summary>
    /// ⚠️ <b>The knife's box is mirrored by the direction it was aimed</b>, so a
    /// stab aimed left reaches thirty-one pixels left and two right.
    /// </summary>
    [Fact]
    public void TheKnifeReachesTheWayItWasAimed()
    {
        bool Reaches(double stabDegrees, double victimOffset)
        {
            var state = default(WorldState);
            Put(ref state, 1);
            Put(ref state, 2, x: ClearX + victimOffset, y: ClearY, team: Team.Blue,
                playerClass: PlayerClass.Heavy);

            state.Weapons[1].StabDirection = stabDegrees;
            Weapons.CreateStabMask(ref state, 1);
            Projectiles.StabStep(ref state);
            Projectiles.Collide(World, ref state, ref DiscardedEvents.Events);

            return state.Characters[2].Hp < 200;
        }

        Assert.True(Reaches(0, 30), "an aimed-right stab did not reach 30 right");
        Assert.True(Reaches(180, -30), "an aimed-left stab did not reach 30 left");
    }

    /// <summary>
    /// ⚠️ <b>A wall between them blocks the stab and destroys the knife</b> —
    /// the <c>else</c> is inside the guard, so it costs the whole hitbox rather
    /// than one of its two hits.
    /// </summary>
    /// <remarks>
    /// The map is solid from x = 2137 at this height, found by scanning, and
    /// asserted below so this cannot quietly stop testing the line of sight.
    /// </remarks>
    [Fact]
    public void AWallBetweenThemBlocksTheStabAndEndsIt()
    {
        var state = default(WorldState);
        Put(ref state, 1, x: 2120, y: ClearY);
        Put(ref state, 2, x: 2150, y: ClearY, team: Team.Blue, playerClass: PlayerClass.Heavy);

        Assert.True(World.CollisionLineHitsMask(2120, ClearY - 12, 2150, ClearY),
            "the two positions are not separated by the map");

        Weapons.CreateStabMask(ref state, 1);
        Projectiles.StabStep(ref state);
        Projectiles.Collide(World, ref state, ref DiscardedEvents.Events);

        Assert.Equal(200, state.Characters[2].Hp);
        Assert.Equal(0, CountOf(state, ProjectileKind.Stab));
    }

    /// <summary>
    /// ⚠️ <b>But a spy standing <em>inside</em> somebody stabs them through
    /// anything</b> — the first half of the test is the spy's own
    /// <c>place_meeting</c>, and the line only matters for the reach beyond
    /// arm's length.
    /// </summary>
    [Fact]
    public void ASpyOverlappingTheirVictimStabsThroughTheWall()
    {
        var state = default(WorldState);
        Put(ref state, 1, x: 2145, y: ClearY);
        Put(ref state, 2, x: 2145, y: ClearY, team: Team.Blue, playerClass: PlayerClass.Heavy);

        Weapons.CreateStabMask(ref state, 1);
        Projectiles.StabStep(ref state);
        Projectiles.Collide(World, ref state, ref DiscardedEvents.Events);

        Assert.Equal(0, state.Characters[2].Hp);
    }

    /// <summary>A team-mate is not touched, and does not cost the knife anything.</summary>
    [Fact]
    public void ATeamMateIsNotStabbed()
    {
        var state = default(WorldState);
        Put(ref state, 1);
        Put(ref state, 2, x: ClearX + 10, y: ClearY, team: Team.Red, playerClass: PlayerClass.Heavy);

        Weapons.CreateStabMask(ref state, 1);
        Projectiles.StabStep(ref state);
        Projectiles.Collide(World, ref state, ref DiscardedEvents.Events);

        Assert.Equal(200, state.Characters[2].Hp);
        Assert.Equal(1, CountOf(state, ProjectileKind.Stab));
    }

    /// <summary>
    /// ⚠️ <b>The knife trails its spy by one tick's velocity</b>, because its
    /// Step reads the owner's mid-Step position and it has no speed of its own
    /// to be moved by afterwards.
    /// </summary>
    [Fact]
    public void TheKnifeTrailsARunningSpy()
    {
        var state = default(WorldState);
        Put(ref state, 1);
        state.Characters[1].Motion.SetComponents(5, 0);

        Weapons.CreateStabMask(ref state, 1);
        Projectiles.StabStep(ref state);

        Assert.Equal(ClearX - 5, Live(state, 0).X, 9);
    }

    /// <summary>
    /// ⚠️ <b>A generator takes 0.36 of the knife</b> — 72 — and there is no
    /// line-of-sight test at all, which every other arm of the same event has.
    /// </summary>
    [Fact]
    public void AGeneratorTakesAThirdOfTheKnifeThroughAnything()
    {
        var level = LevelMap.FromPng("gen_destroy", ReferenceTree.ReadMap("gen_destroy")).ForArea(1);
        var world = new CollisionWorld(level);

        var state = default(WorldState);
        Generators.Place(ref state, level);

        Assert.True(state.BlueGenerator.Exists, "the map placed no blue generator");

        Put(ref state, 1, x: state.BlueGenerator.X, y: state.BlueGenerator.Y, team: Team.Red);

        var shieldBefore = state.BlueGenerator.ShieldHp;
        Weapons.CreateStabMask(ref state, 1);
        Projectiles.StabStep(ref state);
        Projectiles.Collide(world, ref state, ref DiscardedEvents.Events);

        // ⚠️ The *shield* takes the full 72 and the hit points take half of
        // it — the generator's own discount, which is why this asserts the
        // shield: it is the only place the 0.36 shows up undivided.
        Assert.Equal(shieldBefore - (200 * 0.36), state.BlueGenerator.ShieldHp, 9);
        Assert.Equal(0, CountOf(state, ProjectileKind.Stab));
    }

    /// <summary>The knife dies with its spy, like a Quote's bubbles and a mine.</summary>
    [Fact]
    public void TheKnifeDiesWithItsSpy()
    {
        var state = default(WorldState);
        Put(ref state, 1);
        Weapons.CreateStabMask(ref state, 1);

        Assert.Equal(1, CountOf(state, ProjectileKind.Stab));

        state.Clear(1);

        Assert.Equal(0, CountOf(state, ProjectileKind.Stab));
    }

    /// <summary>
    /// A backstab kill is worth a stab and a point on the scoreboard — the
    /// clause <c>Scoring</c> has carried, transcribed and unreachable, since
    /// phase 6.
    /// </summary>
    [Fact]
    public void AStabKillIsWorthAStabAndAPoint()
    {
        var state = default(WorldState);
        Put(ref state, 1);
        Put(ref state, 2, x: ClearX + 10, y: ClearY, team: Team.Blue, playerClass: PlayerClass.Heavy);

        Weapons.CreateStabMask(ref state, 1);
        Projectiles.StabStep(ref state);
        Projectiles.Collide(World, ref state, ref DiscardedEvents.Events);

        Assert.Equal(DamageSource.Backstab, state.Characters[2].LastDamageSource);

        var events = default(TickEvents);
        Deaths.Kill(ref state, 2, ref events);

        Assert.Equal(1, state.Players[1].Stats[(int)StatIndex.Stabs]);

        // Two: the stab clause awards a point and the ordinary kill awards
        // another. A backstab is worth twice what a bullet is on the
        // scoreboard.
        Assert.Equal(2, state.Players[1].Stats[(int)StatIndex.Points]);
    }

    // --- through a whole tick -------------------------------------------------

    /// <summary>
    /// The cloak toggle, the knife's Step and the freeze all run inside
    /// <see cref="MatchSimulation.Tick"/>.
    /// </summary>
    /// <remarks>
    /// The control for the three phases <c>MatchSimulation</c> gained: every
    /// other test in this file drives them directly, which gates the rule and
    /// not the wiring.
    /// </remarks>
    [Fact]
    public void ATickRunsTheCloakTheKnifeAndTheFreeze()
    {
        var state = default(WorldState);
        Put(ref state, 1);

        // The cloak, on a fresh right click delivered as a real input.
        Span<PlayerInput> cloak = [default, new PlayerInput(PlayerKeys.Special, 0, 0)];
        MatchSimulation.Tick(World, ref state, cloak);

        Assert.True(state.Characters[1].Cloak);
        Assert.True(state.Characters[1].CloakAlpha < 1, "the End Step fade did not run");

        // The stab, on the attack key.
        Span<PlayerInput> stab = [default, new PlayerInput(PlayerKeys.Attack, 0, 0)];
        MatchSimulation.Tick(World, ref state, stab);

        Assert.True(state.Characters[1].Stabbing);

        // The freeze ticks down inside the tick, and the knife appears on time.
        Span<PlayerInput> idle = [default, new PlayerInput(PlayerKeys.None, 0, 0)];
        for (var i = 0; i < Weapons.StabWindUp; i++)
            MatchSimulation.Tick(World, ref state, idle);

        Assert.Equal(1, CountOf(state, ProjectileKind.Stab));
        Assert.True(state.Characters[1].StabFreezeTimer < 52,
            "the freeze did not tick inside the tick");

        // And the knife rides its owner inside the tick, not only when a test
        // calls StabStep by hand.
        var mask = state.LiveProjectileHandle(0);
        state.Characters[1].X = ClearX + 40;

        MatchSimulation.Tick(World, ref state, idle);

        Assert.Equal(state.Characters[1].X - state.Characters[1].HSpeed,
            state.Projectiles[mask.Index].X, 9);
    }
}
