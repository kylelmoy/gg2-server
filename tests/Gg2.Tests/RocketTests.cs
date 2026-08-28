using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// The Rocketlauncher, the rocket and the blast: phase 5 stage 5.
/// </summary>
/// <remarks>
/// <b>Nothing in a recording gates any of this yet.</b>
/// <c>reference-ctf_truefort-combat.trace</c> is four soldiers, a medic and a
/// demoman, and it diverges at tick 9 for reasons that have nothing to do with
/// weapons — measured, by running it with the rocket switched off and getting
/// the identical report. So until that is chased down, these tests <em>are</em>
/// the gate, and each one was checked by deleting the line it is about.
/// <para>
/// Transcribed from <c>Objects/Weapons/Rocketlauncher.events/User Event 3.xml</c>,
/// <c>Objects/Projectiles/Rocket.events/{Create, Alarm 1, Alarm 2,
/// Collision with Character, User Event 5}.xml</c> and the <c>with(Rocket)</c>
/// block of <c>Scripts/Physics/System/move_all_bullets.gml</c>.
/// </para>
/// </remarks>
public sealed class RocketTests
{
    private const string Map = "ctf_truefort";

    /// <summary>Open ground on <c>ctf_truefort</c>, clear for a long way either side.</summary>
    private const double OpenX = 2600;

    private const double OpenY = 708;

    /// <summary><c>Rocket.events/Create.xml:19</c>.</summary>
    private const double BlastRadius = 65;

    /// <summary><c>Rocket.events/Create.xml:18</c>.</summary>
    private const double ExplosionDamage = 30;

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
        double aimDegrees = 0,
        bool firing = false,
        int facing = 1)
    {
        state.Characters[slot] = new CharacterState
        {
            Class = playerClass,
            Team = team,
            X = x,
            Y = y,
            Hp = ClassStats.For(playerClass).MaxHp,
            Facing = facing,
            LastFacing = facing,
            CloakAlpha = 1,
            CanGrabIntel = true,
            LastDamageDealer = CharacterState.Nobody,
            SecondToLastDamageDealer = CharacterState.Nobody,
            BurnedBy = CharacterState.Nobody,
            Healer = CharacterState.Nobody,
            NetAimDirection = (ushort)Math.Round(aimDegrees * 65536.0 / 360.0),
            KeyState = firing ? PlayerKeys.Attack : PlayerKeys.None,
        };

        state.Weapons[slot] = WeaponState.Spawn(playerClass, x, y);
        state.Weapons[slot].ReadyToShoot = true;
        state.Weapons[slot].RefireTimer = WeaponState.Inactive;
        state.Alive[slot] = true;
    }

    /// <summary>A rocket as the launcher makes one, placed by hand.</summary>
    private static ProjectileState Rocket(double x, double y, double direction, int ownerSlot, Team team)
    {
        var rocket = new ProjectileState
        {
            Kind = ProjectileKind.Rocket,
            X = x,
            Y = y,
            OwnerSlot = (byte)ownerSlot,
            Team = team,
            Source = DamageSource.Rocketlauncher,
            HitDamage = 25,
            Knockback = 8,
            DistanceToTravel = 800,
            KnockbackCapTimer = 20,
            KnockbackEndTimer = 30,
            LastKnownX = x,
            LastKnownY = y,
        };

        rocket.Motion.Direction = direction;
        rocket.ImageAngle = direction;
        rocket.Motion.Speed = 13;

        return rocket;
    }

    /// <summary>
    /// Sixty pixels above <see cref="OpenY"/>, which is genuinely airborne.
    /// </summary>
    /// <remarks>
    /// <c>OpenY</c> is a <em>standing</em> position — it is where the bot trace's
    /// two scouts met, with the floor directly under it — so it is the wrong
    /// place to test anything that asks whether somebody is off the ground.
    /// Every test that uses either one asserts which it has.
    /// </remarks>
    private const double AirY = OpenY - 60;

    /// <summary>
    /// A rocket placed to the victim's left at the first offset that opens at
    /// least <paramref name="targetGap"/> pixels of box-to-box distance.
    /// </summary>
    /// <remarks>
    /// Searched rather than computed, so the tests state the distance they mean
    /// and stay true if either bounding box is ever remeasured. A rocket's box is
    /// its rotated sprite's and a character's is class-specific, so the offset
    /// that produces a given gap is not a number worth writing down.
    /// </remarks>
    /// <param name="side">
    /// -1 puts the rocket to the victim's left, so it is flying towards them; +1
    /// puts it to their right, flying away.
    /// </param>
    private static ProjectileState RocketAtGap(
        in WorldState state,
        int victimSlot,
        double targetGap,
        Team team,
        int side = -1)
    {
        ref readonly var victim = ref state.Characters[victimSlot];

        for (var offset = 0; offset < 400; offset++)
        {
            var candidate = Rocket(victim.X + (side * offset), victim.Y, direction: 0, ownerSlot: 1, team);

            if (BlastGap(in state, victimSlot, in candidate) >= targetGap)
                return candidate;
        }

        Assert.Fail($"no offset within 400px opens a gap of {targetGap}");
        return default;
    }

    private static ProjectileState Live(in WorldState state, int index) =>
        state.Projectiles[state.LiveProjectileSlot(index)];

    /// <summary>The gap the blast measures, from the victim's facing-inflated box.</summary>
    private static double BlastGap(in WorldState state, int slot, in ProjectileState rocket)
    {
        ref readonly var victim = ref state.Characters[slot];
        var box = Aabb.ForCharacter(victim.Box, victim.X, victim.Y, victim.Facing);

        return box.Distance(rocket.Box);
    }

    // --- the launcher ---------------------------------------------------------

    /// <summary>
    /// One rocket, twenty pixels ahead of the weapon, at speed 13 along the aim.
    /// </summary>
    /// <remarks>
    /// ⚠️ The muzzle is the <em>weapon's</em> position plus the offset, and the
    /// weapon is where the character was at the end of the previous tick, rounded
    /// half to even — <c>Character.events/End Step.xml:64-65</c>. Anchoring the
    /// offset on the character instead moves every rocket.
    /// </remarks>
    [Fact]
    public void TheLauncherMakesOneRocketAheadOfTheWeapon()
    {
        var state = default(WorldState);
        var events = default(TickEvents);
        Put(ref state, 1, PlayerClass.Soldier, Team.Red, OpenX, OpenY, aimDegrees: 0, firing: true);

        Weapons.FireStep(World, ref state, 1, ref events);

        Assert.Equal(1, state.LiveProjectileCount);

        var rocket = Live(state, 0);
        Assert.Equal(ProjectileKind.Rocket, rocket.Kind);
        Assert.Equal(OpenX + 20, rocket.X);
        Assert.Equal(OpenY, rocket.Y);
        Assert.Equal(13, rocket.Motion.Speed);
        Assert.Equal(0, rocket.Motion.Direction);
        Assert.Equal(DamageSource.Rocketlauncher, rocket.Source);
        Assert.Equal(1, rocket.OwnerSlot);

        // The leash is anchored on the *character*, so the twenty pixels of
        // muzzle offset come out of the 800 straight away.
        Assert.Equal(OpenX, rocket.LastKnownX);
        Assert.Equal(OpenY, rocket.LastKnownY);

        Assert.Equal(3, state.Weapons[1].AmmoCount);
        Assert.False(state.Weapons[1].ReadyToShoot);
        Assert.Equal(30, state.Weapons[1].RefireTimer);
        Assert.Equal(52, state.Weapons[1].ReloadTimer);
    }

    /// <summary>
    /// A soldier's shot draws a seed and broadcasts it even though the rocket
    /// never reads one.
    /// </summary>
    /// <remarks>
    /// <c>Rocketlauncher.events/User Event 1.xml</c> is the Scattergun's, line
    /// for line: <c>irandom(65535)</c>, <c>sendEventFireWeapon</c>,
    /// <c>doEventFireWeapon</c>. Dropping the draw because User Event 3 is
    /// deterministic would leave the client without a <c>WEAPON_FIRE</c> to
    /// animate.
    /// </remarks>
    [Fact]
    public void FiringARocketStillReportsAFireEvent()
    {
        var state = default(WorldState);
        var events = default(TickEvents);
        Put(ref state, 1, PlayerClass.Soldier, Team.Red, OpenX, OpenY, firing: true);

        Weapons.FireStep(World, ref state, 1, ref events);

        Assert.True(events.Fired[1]);
        Assert.Equal(1, events.Fires[1].Slot);
    }

    /// <summary>
    /// A rocket whose muzzle is the far side of a team gate is born already
    /// exploding.
    /// </summary>
    /// <remarks>
    /// The end of <c>collision_line_bulletblocking</c>'s path, and the only
    /// caller of it. A gate is an <em>instance</em> rather than map geometry, so
    /// this only works once <see cref="CollisionWorld.CollisionLineBlocksBullets"/>
    /// walks the solid rectangles as well as the walkmask — and it only walks
    /// them correctly because a level entity's mask fills its box, which was
    /// measured against the running game rather than assumed.
    /// <para>
    /// ⚠️ Team does not enter into it. <c>gunSetSolids</c> makes <em>every</em>
    /// gate solid to a bullet whichever team it belongs to, so a red soldier's
    /// rocket goes off against a red gate. That is why the soldier below is on
    /// the same team as the gate it is shooting.
    /// </para>
    /// </remarks>
    [Fact]
    public void ARocketFiredIntoAGateExplodesAtTheMuzzle()
    {
        SolidRect gate = default;
        var found = false;

        foreach (var rect in World.Rects)
        {
            if (rect.Kind != SolidKind.TeamGate)
                continue;

            gate = rect;
            found = true;
            break;
        }

        Assert.True(found, $"{Map} has no team gate to shoot at");

        // Ten pixels short of the gate's left edge, so the twenty-pixel muzzle
        // offset lands the rocket inside it and the segment between crosses it.
        var x = gate.Left - 10;
        var y = (gate.Top + gate.Bottom) / 2;

        var state = default(WorldState);
        var events = default(TickEvents);
        Put(ref state, 1, PlayerClass.Soldier, Team.Red, x, y, aimDegrees: 0, firing: true);

        Weapons.FireStep(World, ref state, 1, ref events);

        Assert.Equal(1, state.LiveProjectileCount);
        Assert.True(Live(state, 0).ExplodeImmediately, "the gate did not block the muzzle line");
    }

    /// <summary>
    /// The control: the same soldier facing away from the gate fires an ordinary
    /// rocket.
    /// </summary>
    /// <remarks>
    /// Without it the test above would pass just as well if every rocket were
    /// born exploding.
    /// </remarks>
    [Fact]
    public void ARocketFiredAwayFromAGateIsOrdinary()
    {
        SolidRect gate = default;
        var found = false;

        foreach (var rect in World.Rects)
        {
            if (rect.Kind != SolidKind.TeamGate)
                continue;

            gate = rect;
            found = true;
            break;
        }

        Assert.True(found, $"{Map} has no team gate to shoot at");

        var x = gate.Left - 10;
        var y = (gate.Top + gate.Bottom) / 2;

        var state = default(WorldState);
        var events = default(TickEvents);
        Put(ref state, 1, PlayerClass.Soldier, Team.Red, x, y, aimDegrees: 180, firing: true);

        Weapons.FireStep(World, ref state, 1, ref events);

        Assert.Equal(1, state.LiveProjectileCount);

        // The twenty pixels behind the soldier really are clear, so a false here
        // means the muzzle line said so rather than that nothing was tested.
        Assert.True(
            World.PlaceFree(Live(state, 0).Box, CollisionFilter.ForBullet()),
            "the setup put the away-facing muzzle inside something");
        Assert.False(Live(state, 0).ExplodeImmediately);
    }

    // --- flight ---------------------------------------------------------------

    /// <summary>
    /// A rocket gains a unit of speed and then loses eight per cent of the
    /// result, every tick.
    /// </summary>
    /// <remarks>
    /// Two separate polar writes and not <c>(speed + 1) * 0.92</c>: each one
    /// re-derives the components and snaps them, so the collapsed form differs
    /// in the last bits. The reference keeps the acceleration because the
    /// airblast needs it, not because a rocket is meant to speed up — the pair
    /// converges downwards on 11.5 from a launch speed of 13.
    /// </remarks>
    [Fact]
    public void ARocketAcceleratesAndThenDrags()
    {
        var state = default(WorldState);
        state.SpawnProjectile(Rocket(OpenX, OpenY, direction: 0, ownerSlot: 1, Team.Red));

        Projectiles.Move(World, ref state, ref DiscardedEvents.Events);

        var rocket = Live(state, 0);
        Assert.Equal((13 + 1) * 0.92, rocket.Motion.Speed, 12);
        Assert.Equal(OpenX + ((13 + 1) * 0.92), rocket.X, 9);
    }

    /// <summary>
    /// ⚠️ A rocket does not move on a tick it is already colliding, so the blast
    /// lands where it hit rather than a step past it.
    /// </summary>
    /// <remarks>
    /// <c>if(!colliding) { x += hspeed; ... }</c>, which is the one structural
    /// difference between the rocket's movement block and the pellet's. The
    /// reference's own comment says why.
    /// </remarks>
    [Fact]
    public void ARocketFiredWithABlockedMuzzleExplodesWhereItWasMade()
    {
        var state = default(WorldState);
        Put(ref state, 2, PlayerClass.Soldier, Team.Blue, OpenX, AirY);

        // The victim is *behind* the rocket, forty pixels back, and the rocket is
        // pointed away from them. Forty is inside the blast; forty plus one tick
        // of travel is past the quarter-radius cutoff — so if the rocket takes
        // its step before going off, this character takes nothing at all. A
        // victim in front would be hurt either way and the test would be blind.
        var rocket = RocketAtGap(in state, 2, 40, Team.Red, side: +1);
        rocket.ExplodeImmediately = true;
        var handle = state.SpawnProjectile(rocket);

        var gap = BlastGap(in state, 2, in rocket);
        Assert.True(gap >= 40 && gap < 48.75, $"the setup put the rocket {gap} away");
        Assert.True(gap + ((13 + 1) * 0.92) > 48.75, "one tick of travel does not clear the cutoff");

        var before = state.Characters[2].Hp;
        Projectiles.Move(World, ref state, ref DiscardedEvents.Events);

        Assert.Equal(0, state.LiveProjectileCount);
        Assert.True(state.Characters[2].Hp < before, "the rocket stepped before it exploded");
        Assert.False(state.TryGetProjectile(handle, out _));
    }

    /// <summary>
    /// The travel budget is measured from the owner, and a rocket that outruns
    /// it fades out and stops being able to hurt anybody.
    /// </summary>
    /// <remarks>
    /// ⚠️ <c>if (!fade) event_user(5) else instance_destroy()</c> — an
    /// over-range rocket that reaches a wall is deleted in silence, and somebody
    /// standing in front of that wall takes nothing at all.
    /// </remarks>
    [Fact]
    public void AFadedRocketDoesNoDamage()
    {
        var state = default(WorldState);
        Put(ref state, 2, PlayerClass.Soldier, Team.Blue, OpenX, AirY);

        var rocket = RocketAtGap(in state, 2, 10, Team.Red, side: +1);
        rocket.Fade = true;
        rocket.ExplodeImmediately = true;
        state.SpawnProjectile(rocket);
        var before = state.Characters[2].Hp;

        Projectiles.Move(World, ref state, ref DiscardedEvents.Events);

        Assert.Equal(0, state.LiveProjectileCount);
        Assert.Equal(before, state.Characters[2].Hp);
    }

    /// <summary>
    /// A fading rocket lasts exactly ten more passes.
    /// </summary>
    /// <remarks>
    /// Because <c>image_alpha</c> reaches 1.4e-16 on the tenth subtraction of
    /// 0.1, and GML's <c>&gt; 0</c> is fuzzy to 1e-13. Eleven would be the answer
    /// with an exact comparison.
    /// </remarks>
    [Fact]
    public void AFadingRocketLastsTenPasses()
    {
        var state = default(WorldState);

        // Off the ground and flying level: OpenY is a standing position, so a
        // rocket born on it has the floor inside its box, and ten passes of
        // upward flight would find the ceiling.
        var rocket = Rocket(OpenX, AirY, direction: 0, ownerSlot: 1, Team.Red);
        rocket.Fade = true;
        rocket.DistanceToTravel = 100000;
        state.SpawnProjectile(rocket);

        // The corridor it is about to fly down has to be empty, or this measures
        // the map instead of the fade.
        Assert.True(
            World.PlaceFree(
                Rocket(OpenX + 150, AirY, direction: 0, ownerSlot: 1, Team.Red).Box,
                CollisionFilter.ForBullet()),
            "the setup put a wall inside the fading rocket's flight path");

        for (var pass = 1; pass < 10; pass++)
        {
            Projectiles.Move(World, ref state, ref DiscardedEvents.Events);
            Assert.Equal(1, state.LiveProjectileCount);
        }

        Projectiles.Move(World, ref state, ref DiscardedEvents.Events);
        Assert.Equal(0, state.LiveProjectileCount);
    }

    // --- the blast ------------------------------------------------------------

    /// <summary>
    /// Splash damage falls off linearly with the gap between the boxes.
    /// </summary>
    [Fact]
    public void SplashDamageFallsOffLinearly()
    {
        var state = default(WorldState);
        Put(ref state, 2, PlayerClass.Soldier, Team.Blue, OpenX, OpenY);

        var rocket = Rocket(OpenX - 40, OpenY, direction: 0, ownerSlot: 1, Team.Red);
        var handle = state.SpawnProjectile(rocket);

        var gap = BlastGap(in state, 2, in rocket);
        Assert.True(gap > 0 && gap < BlastRadius, $"the setup put the rocket {gap} away");

        var before = state.Characters[2].Hp;
        Assert.True(Explosions.Explode(World, ref state, handle, CharacterState.Nobody, ref DiscardedEvents.Events));

        Assert.Equal(before - (ExplosionDamage * (1 - (gap / BlastRadius))), state.Characters[2].Hp, 9);
        Assert.Equal(1, state.Characters[2].LastDamageDealer);
        Assert.Equal(DamageSource.Rocketlauncher, state.Characters[2].LastDamageSource);
    }

    /// <summary>
    /// Past three quarters of the radius a character gets nothing at all — not
    /// even the shove.
    /// </summary>
    /// <remarks>
    /// <c>if (1 - fake_distance/other.blastRadius &lt;= 0.25) continue;</c>, and
    /// <c>continue</c> inside a <c>with</c> moves to the next instance. Written
    /// as an early <c>return</c> from the per-character body, so the knockback
    /// below it is skipped too — reading it as "no damage" would leave a
    /// long-range rocket able to push people around.
    /// </remarks>
    [Fact]
    public void TheOuterQuarterOfTheRadiusGetsNothing()
    {
        var state = default(WorldState);
        Put(ref state, 2, PlayerClass.Soldier, Team.Blue, OpenX, OpenY);

        // 1 - d/65 <= 0.25 at d >= 48.75, so a gap of 49 is out and 48 is in.
        var rocket = RocketAtGap(in state, 2, 49, Team.Red);
        var handle = state.SpawnProjectile(rocket);

        var gap = BlastGap(in state, 2, in rocket);
        Assert.True(gap >= 48.75 && gap < BlastRadius, $"the setup put the rocket {gap} away");

        var before = state.Characters[2].Hp;
        Explosions.Explode(World, ref state, handle, CharacterState.Nobody, ref DiscardedEvents.Events);

        Assert.Equal(before, state.Characters[2].Hp);
        Assert.Equal(0, state.Characters[2].Motion.Speed);
        Assert.Equal(0, state.Characters[2].MoveStatus);
    }

    /// <summary>
    /// The control for the cutoff: two pixels closer and the same character is
    /// damaged and thrown.
    /// </summary>
    [Fact]
    public void JustInsideTheCutoffGetsBothDamageAndKnockback()
    {
        var state = default(WorldState);
        Put(ref state, 2, PlayerClass.Soldier, Team.Blue, OpenX, OpenY);

        var rocket = RocketAtGap(in state, 2, 48, Team.Red);
        var handle = state.SpawnProjectile(rocket);

        var gap = BlastGap(in state, 2, in rocket);
        Assert.True(gap < 48.75, $"the setup put the rocket {gap} away");

        var before = state.Characters[2].Hp;
        Explosions.Explode(World, ref state, handle, CharacterState.Nobody, ref DiscardedEvents.Events);

        Assert.True(state.Characters[2].Hp < before);
        Assert.True(state.Characters[2].Motion.Speed > 0);
        Assert.Equal(2, state.Characters[2].MoveStatus);
    }

    /// <summary>
    /// ⚠️ Splash damage depends on which way the victim is facing.
    /// </summary>
    /// <remarks>
    /// <c>distance_to_object</c> takes GM8's reported bounding box, and a
    /// character with <c>image_xscale = -1</c> has one two pixels wider on the
    /// left. <c>place_meeting</c> — the test three lines away that decides
    /// whether a rocket <em>hit</em> somebody — does not. So the same rocket at
    /// the same coordinates can be inside the cutoff for a left-facing character
    /// and outside it for a right-facing one, and this is the pair that proves
    /// it.
    /// </remarks>
    [Fact]
    public void ALeftFacingVictimIsTwoPixelsWiderToTheBlast()
    {
        // A gap of exactly 49 facing right, which the cutoff excludes, becomes 47
        // facing left, which it does not.
        var facingRight = default(WorldState);
        Put(ref facingRight, 2, PlayerClass.Soldier, Team.Blue, OpenX, OpenY, facing: 1);

        var placed = RocketAtGap(in facingRight, 2, 49, Team.Red);
        var rightHandle = facingRight.SpawnProjectile(placed);

        var facingLeft = default(WorldState);
        Put(ref facingLeft, 2, PlayerClass.Soldier, Team.Blue, OpenX, OpenY, facing: -1);
        var leftHandle = facingLeft.SpawnProjectile(placed);

        var rightGap = BlastGap(in facingRight, 2, in facingRight.Projectiles[rightHandle.Index]);
        var leftGap = BlastGap(in facingLeft, 2, in facingLeft.Projectiles[leftHandle.Index]);
        Assert.Equal(rightGap - 2, leftGap);

        var before = facingRight.Characters[2].Hp;
        Explosions.Explode(World, ref facingRight, rightHandle, CharacterState.Nobody, ref DiscardedEvents.Events);
        Explosions.Explode(World, ref facingLeft, leftHandle, CharacterState.Nobody, ref DiscardedEvents.Events);

        Assert.Equal(before, facingRight.Characters[2].Hp);
        Assert.True(facingLeft.Characters[2].Hp < before, "the left-facing victim took nothing either");
    }

    /// <summary>
    /// A team-mate standing on the ground is exempt from the whole blast.
    /// </summary>
    /// <remarks>
    /// <c>!(team == other.team and id != other.ownerPlayer.object and
    /// place_meeting(x, y+1, Obstacle))</c>. All three have to hold, so the
    /// shooter is never exempt from their own rocket and an airborne team-mate is
    /// still boosted.
    /// </remarks>
    [Fact]
    public void AGroundedTeamMateIsExemptFromTheBlast()
    {
        var state = default(WorldState);
        var floor = FloorBelow((int)OpenX, (int)OpenY);

        // Standing on the floor: one pixel down is solid.
        var standing = floor - 23 - 1;
        Put(ref state, 2, PlayerClass.Soldier, Team.Red, OpenX, standing);
        Assert.False(
            World.PlaceFreeInMask(state.Characters[2].Box, OpenX, standing + 1),
            "the setup did not put the team-mate on the ground");

        var handle = state.SpawnProjectile(
            Rocket(OpenX - 30, standing, direction: 0, ownerSlot: 1, Team.Red));

        var before = state.Characters[2].Hp;
        Explosions.Explode(World, ref state, handle, CharacterState.Nobody, ref DiscardedEvents.Events);

        Assert.Equal(before, state.Characters[2].Hp);
        Assert.Equal(0, state.Characters[2].Motion.Speed);
    }

    /// <summary>
    /// The control: the same team-mate in the air is thrown, and still takes no
    /// damage.
    /// </summary>
    /// <remarks>
    /// The exemption is about the ground, not about the team. Friendly fire is
    /// separately excluded by <c>if (other.team != team) or
    /// (id==other.ownerPlayer.object)</c> around the damage alone, which is what
    /// makes a rocket a way to launch a team-mate rather than to kill one.
    /// </remarks>
    [Fact]
    public void AnAirborneTeamMateIsThrownButNotHurt()
    {
        var state = default(WorldState);
        Put(ref state, 2, PlayerClass.Soldier, Team.Red, OpenX, AirY);
        Assert.True(
            World.PlaceFreeInMask(state.Characters[2].Box, OpenX, AirY + 1),
            "the setup did not get the team-mate off the ground");

        var handle = state.SpawnProjectile(
            Rocket(OpenX - 30, AirY, direction: 0, ownerSlot: 1, Team.Red));

        var before = state.Characters[2].Hp;
        Explosions.Explode(World, ref state, handle, CharacterState.Nobody, ref DiscardedEvents.Events);

        Assert.Equal(before, state.Characters[2].Hp);
        Assert.True(state.Characters[2].Motion.Speed > 0);
        Assert.Equal(4, state.Characters[2].MoveStatus);
    }

    /// <summary>
    /// The shooter is hurt by their own rocket and gets the rocket jump.
    /// </summary>
    /// <remarks>
    /// <c>moveStatus = 1</c> and <c>speed *= 1.06</c>, and no upward boost — the
    /// blast is doing the lifting. The damage arm is reached because
    /// <c>id == other.ownerPlayer.object</c> is the second half of its
    /// condition, so self-damage survives the friendly-fire exclusion.
    /// </remarks>
    [Fact]
    public void ARocketJumpHurtsAndLaunchesItsOwnShooter()
    {
        var state = default(WorldState);
        Put(ref state, 1, PlayerClass.Soldier, Team.Red, OpenX, OpenY);

        // Below the shooter, which is where a rocket jump's rocket is.
        var handle = state.SpawnProjectile(
            Rocket(OpenX, OpenY + 40, direction: 270, ownerSlot: 1, Team.Red));

        var before = state.Characters[1].Hp;
        Explosions.Explode(World, ref state, handle, CharacterState.Nobody, ref DiscardedEvents.Events);

        Assert.True(state.Characters[1].Hp < before, "the shooter took no self-damage");
        Assert.Equal(1, state.Characters[1].MoveStatus);
        Assert.True(state.Characters[1].Motion.VSpeed < 0, "the shooter was not thrown upwards");
    }

    /// <summary>
    /// ✅ <b>A rocket jump costs its shooter exactly what the same blast costs an
    /// enemy — GG2 has no self-damage reduction at all.</b>
    /// </summary>
    /// <remarks>
    /// <c>Rocket.events/User Event 5.xml</c> deals
    /// <c>other.explosionDamage*(1-(fake_distance/other.blastRadius))</c> from a
    /// single line reached by both arms of
    /// <c>if (other.team != team) or (id==other.ownerPlayer.object)</c>, and
    /// <c>damageCharacter</c> → <c>dealDamage</c> has no owner test anywhere
    /// below it. So the shooter pays the full linear falloff.
    /// <para>
    /// ⚠️ <b>The gate is the EQUALITY, not the number.</b>
    /// <c>ARocketJumpHurtsAndLaunchesItsOwnShooter</c> asserts only
    /// <c>Hp &lt; before</c>, which is what a halved or floored self-damage rule
    /// would still satisfy — and adding one is the single most natural "fix"
    /// somebody reading a self-kill report would reach for. Running the identical
    /// geometry twice, once as the shooter and once as an enemy, is what makes
    /// any such factor fail rather than pass quietly.
    /// </para>
    /// <para>
    /// A mid-radius gap rather than point-blank, so the falloff term is a real
    /// fraction: a multiplicative fudge and an additive one both move it.
    /// </para>
    /// </remarks>
    [Fact]
    public void ARocketJumpCostsItsShooterExactlyWhatItWouldCostAnEnemy()
    {
        // The shooter, hit by their own rocket.
        var self = default(WorldState);
        Put(ref self, 1, PlayerClass.Soldier, Team.Red, OpenX, OpenY);

        var selfRocket = RocketAtGap(in self, 1, 20, Team.Red);
        var gap = BlastGap(in self, 1, in selfRocket);
        Assert.True(gap > 0 && gap < BlastRadius, $"the setup put the rocket {gap} away");

        var selfHandle = self.SpawnProjectile(selfRocket);
        Assert.True(Explosions.Explode(World, ref self, selfHandle, CharacterState.Nobody, ref DiscardedEvents.Events));

        var selfLoss = ClassStats.For(PlayerClass.Soldier).MaxHp - self.Characters[1].Hp;

        // The same soldier at the same coordinates and the same facing, so the
        // box the blast measures against is the same one — but on the other team,
        // with the shooter parked well outside the radius.
        var enemy = default(WorldState);
        Put(ref enemy, 1, PlayerClass.Soldier, Team.Red, OpenX + 400, OpenY);
        Put(ref enemy, 2, PlayerClass.Soldier, Team.Blue, OpenX, OpenY);

        var enemyRocket = selfRocket;
        Assert.Equal(gap, BlastGap(in enemy, 2, in enemyRocket), 9);

        var enemyHandle = enemy.SpawnProjectile(enemyRocket);
        Assert.True(Explosions.Explode(World, ref enemy, enemyHandle, CharacterState.Nobody, ref DiscardedEvents.Events));

        var enemyLoss = ClassStats.For(PlayerClass.Soldier).MaxHp - enemy.Characters[2].Hp;

        Assert.Equal(ExplosionDamage * (1 - (gap / BlastRadius)), selfLoss, 9);
        Assert.Equal(selfLoss, enemyLoss, 9);

        // The control that says the two runs really were different situations
        // rather than the same one twice: the shooter is rocketing themselves and
        // the enemy is merely being shoved.
        Assert.Equal(1, self.Characters[1].MoveStatus);
        Assert.Equal(2, enemy.Characters[2].MoveStatus);
    }

    /// <summary>
    /// ✅ <b>A rocket that reports its own shooter as the thing it struck does not
    /// go off at all</b> — which is why a rocket jump can never cost 25 + 30.
    /// </summary>
    /// <remarks>
    /// The whole of <c>User Event 5</c> is inside
    /// <c>if(characterHit.id != ownerPlayer.object)</c>, <c>instance_destroy()</c>
    /// included. Deleting that guard is invisible to every other test in this
    /// file — they all pass <c>CharacterState.Nobody</c> as the direct victim —
    /// and it would turn the worst rocket jump in the game from 30 into 55.
    /// </remarks>
    [Fact]
    public void ARocketThatStruckItsOwnShooterNeitherExplodesNorDies()
    {
        var state = default(WorldState);
        Put(ref state, 1, PlayerClass.Soldier, Team.Red, OpenX, OpenY);

        var handle = state.SpawnProjectile(Rocket(OpenX, OpenY, direction: 270, ownerSlot: 1, Team.Red));

        Assert.False(Explosions.Explode(World, ref state, handle, directVictim: 1, ref DiscardedEvents.Events));

        Assert.Equal(ClassStats.For(PlayerClass.Soldier).MaxHp, state.Characters[1].Hp);
        Assert.Equal(0, state.Characters[1].MoveStatus);
        Assert.Equal(1, state.LiveProjectileCount);
    }

    /// <summary>
    /// ✅ <b>The worst a rocket jump can cost a soldier is 30 of 160</b>, so it
    /// takes six of them from full health to kill you.
    /// </summary>
    /// <remarks>
    /// The number this pins is the one a player report needs to be read against:
    /// three self-kills in a session is a soldier who was already hurt, not a
    /// self-damage bug. The direct hit's 25 is unreachable
    /// (<see cref="ARocketThatStruckItsOwnShooterNeitherExplodesNorDies"/>), and
    /// the blast's own falloff is capped by <c>fake_distance</c> being pulled to
    /// zero at contact, so <c>explosionDamage</c> is the whole ceiling.
    /// </remarks>
    [Fact]
    public void TheWorstARocketJumpCanCostIsTheBlastDamageAlone()
    {
        var state = default(WorldState);
        Put(ref state, 1, PlayerClass.Soldier, Team.Red, OpenX, OpenY);

        // Point-blank: the boxes overlap, so `fake_distance` is zero and the
        // falloff term is 1.
        var rocket = RocketAtGap(in state, 1, 0, Team.Red);
        Assert.Equal(0, BlastGap(in state, 1, in rocket));

        var handle = state.SpawnProjectile(rocket);
        Assert.True(Explosions.Explode(World, ref state, handle, CharacterState.Nobody, ref DiscardedEvents.Events));

        var maxHp = ClassStats.For(PlayerClass.Soldier).MaxHp;
        Assert.Equal(maxHp - ExplosionDamage, state.Characters[1].Hp, 9);

        // Five survivable, the sixth fatal — stated as arithmetic on the pinned
        // ceiling rather than by rocket-jumping five more times, which would
        // measure the falloff of five different geometries.
        //
        // ⚠️ 160/30 is 5.33, not 5: a soldier who has rocket-jumped five times
        // from full is on 10hp and still standing. The first draft of this test
        // asserted five and was wrong, which is the whole reason the count is
        // pinned rather than left in a comment.
        Assert.True(maxHp - (5 * ExplosionDamage) > 0);
        Assert.True(maxHp - (6 * ExplosionDamage) <= 0);
    }

    /// <summary>
    /// ✅ <b>A rocket that hits an enemy is GONE, and does not fly on.</b>
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Reported by a player from the first two-human game</b> — "the
    /// rocket appeared to continue moving past the scout it killed and exploded
    /// against the stairs past the scout". This is the server half of that
    /// report, and it holds: the rocket is destroyed on the tick it hits.
    /// <para>
    /// <b>Everything else in this file asserts the destruction indirectly</b>,
    /// through <c>Explosions.Explode</c>'s return value. This drives the whole
    /// collision path — <c>Projectiles.Move</c>, which is what a tick actually
    /// calls — and then asks the pool, which is the question the report asks.
    /// </para>
    /// <para>
    /// ⚠️ <b>The report's cause is not here, and this test cannot find it.</b>
    /// A rocket is <em>never on the wire</em>: <c>WEAPON_FIRE</c> carries the
    /// shooter's quantised position, velocity and an RNG seed, and the client
    /// creates and simulates the whole flight itself with nothing ever
    /// correcting it. What the client corrects every seventh tick is the
    /// <em>target</em>. So a rocket drawn flying through somebody is the
    /// character's prediction error, not the projectile's — see
    /// <c>ClientPredictionGateTests</c>, and <c>docs/TRAPS-SIMULATION.md</c>.
    /// </para>
    /// </remarks>
    [Fact]
    public void ARocketThatHitsAnEnemyIsDestroyedRatherThanFlyingOn()
    {
        var state = default(WorldState);

        // Far enough ahead that the rocket has to travel to reach them, so the
        // collision happens inside Move rather than at the muzzle.
        Put(ref state, 2, PlayerClass.Scout, Team.Blue, OpenX + 60, OpenY);
        state.SpawnProjectile(Rocket(OpenX, OpenY, direction: 0, ownerSlot: 1, Team.Red));

        Assert.Equal(1, state.LiveProjectileCount);

        var before = state.Characters[2].Hp;

        // ⚠️ Both stages. A tick is Move and then, well after the characters
        // have moved, Collide — MatchSimulation.cs:234 and :313. Calling only
        // Move flies the rocket straight through everybody, which is what the
        // first draft of this test did and why it looked like it had reproduced
        // the report.
        for (var tick = 0; tick < 10 && state.LiveProjectileCount > 0; tick++)
        {
            Projectiles.Move(World, ref state, ref DiscardedEvents.Events);
            Projectiles.Collide(World, ref state, ref DiscardedEvents.Events);
        }

        Assert.Equal(0, state.LiveProjectileCount);
        Assert.True(
            state.Characters[2].Hp < before,
            "the rocket vanished without hurting anybody, so it hit something else");
    }

    /// <summary>
    /// A direct hit deals its own 25 on top of whatever the blast does.
    /// </summary>
    [Fact]
    public void ADirectHitDealsItsOwnDamageAsWellAsTheBlast()
    {
        var state = default(WorldState);
        Put(ref state, 2, PlayerClass.Soldier, Team.Blue, OpenX, OpenY);

        var rocket = Rocket(OpenX, OpenY, direction: 0, ownerSlot: 1, Team.Red);
        var handle = state.SpawnProjectile(rocket);

        var gap = BlastGap(in state, 2, in rocket);
        Assert.Equal(0, gap);

        var before = state.Characters[2].Hp;
        Assert.True(Explosions.Explode(World, ref state, handle, 2, ref DiscardedEvents.Events));

        // 25 for the hit, and the full 30 for a blast at zero distance.
        Assert.Equal(before - 25 - ExplosionDamage, state.Characters[2].Hp, 9);
    }

    /// <summary>
    /// An ubered character takes neither the direct hit nor the splash, and is
    /// still thrown.
    /// </summary>
    /// <remarks>
    /// The <c>motion_add</c> sits above the <c>if(!ubered and hp &gt; 0)</c>, so
    /// invulnerability stops the damage and not the physics.
    /// </remarks>
    [Fact]
    public void AnUberedVictimIsThrownButNotHurt()
    {
        var state = default(WorldState);
        Put(ref state, 2, PlayerClass.Soldier, Team.Blue, OpenX, OpenY);
        state.Characters[2].Ubered = true;

        var handle = state.SpawnProjectile(Rocket(OpenX, OpenY, direction: 0, ownerSlot: 1, Team.Red));

        var before = state.Characters[2].Hp;
        Explosions.Explode(World, ref state, handle, 2, ref DiscardedEvents.Events);

        Assert.Equal(before, state.Characters[2].Hp);
        Assert.True(state.Characters[2].Motion.Speed > 0);
    }

    // --- passing through team-mates -------------------------------------------

    /// <summary>
    /// A rocket loses a hundred pixels of range per team-mate it flies through,
    /// and only once each.
    /// </summary>
    [Fact]
    public void FlyingThroughATeamMateCostsRangeOnce()
    {
        var state = default(WorldState);
        Put(ref state, 1, PlayerClass.Soldier, Team.Red, OpenX - 400, OpenY);
        Put(ref state, 2, PlayerClass.Soldier, Team.Red, OpenX, OpenY);
        state.SpawnProjectile(Rocket(OpenX, OpenY, direction: 0, ownerSlot: 1, Team.Red));

        Projectiles.Collide(World, ref state, ref DiscardedEvents.Events);
        Assert.Equal(700, Live(state, 0).DistanceToTravel);

        // Still overlapping on the next tick, and it must not be charged again.
        Projectiles.Collide(World, ref state, ref DiscardedEvents.Events);
        Assert.Equal(700, Live(state, 0).DistanceToTravel);
    }

    /// <summary>The shooter is free: a rocket does not pay to leave its own barrel.</summary>
    [Fact]
    public void FlyingThroughItsOwnShooterIsFree()
    {
        var state = default(WorldState);
        Put(ref state, 1, PlayerClass.Soldier, Team.Red, OpenX, OpenY);
        state.SpawnProjectile(Rocket(OpenX, OpenY, direction: 0, ownerSlot: 1, Team.Red));

        Projectiles.Collide(World, ref state, ref DiscardedEvents.Events);

        Assert.Equal(800, Live(state, 0).DistanceToTravel);
    }

    /// <summary>
    /// The set of already-passed team-mates is renumbered when somebody leaves.
    /// </summary>
    /// <remarks>
    /// CLAUDE.md's rule: anything that names a roster slot has to move with the
    /// roster. Without it a rocket that had already flown through slot 3 would
    /// start charging slot 3's replacement — or stop charging the stranger who
    /// slid into the index.
    /// </remarks>
    [Fact]
    public void ThePassedThroughSetFollowsTheRoster()
    {
        var state = default(WorldState);
        Put(ref state, 1, PlayerClass.Soldier, Team.Red, OpenX - 400, OpenY);
        Put(ref state, 3, PlayerClass.Soldier, Team.Red, OpenX, OpenY);
        state.SpawnProjectile(Rocket(OpenX, OpenY, direction: 0, ownerSlot: 1, Team.Red));

        Projectiles.Collide(World, ref state, ref DiscardedEvents.Events);
        Assert.Equal(1UL << 3, Live(state, 0).PlayersPassed);

        state.RemoveSlot(2);

        Assert.Equal(1UL << 2, Live(state, 0).PlayersPassed);
        Assert.Equal(1, Live(state, 0).OwnerSlot);
    }

    // --- the knockback alarms -------------------------------------------------

    /// <summary>
    /// A rocket's shove is capped after twenty ticks and gone after thirty.
    /// </summary>
    [Fact]
    public void TheKnockbackIsCappedAndThenLost()
    {
        var state = default(WorldState);
        state.SpawnProjectile(Rocket(OpenX, OpenY, direction: 0, ownerSlot: 1, Team.Red));

        for (var tick = 0; tick < 20; tick++)
        {
            Assert.Equal(8, Live(state, 0).Knockback);
            Timers.Advance(ref state);
        }

        Assert.Equal(5, Live(state, 0).Knockback);

        for (var tick = 0; tick < 10; tick++)
            Timers.Advance(ref state);

        Assert.Equal(0, Live(state, 0).Knockback);
    }

    /// <summary>The first solid row under a point.</summary>
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
