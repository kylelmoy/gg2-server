using GG2.Maps;
using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Tests;

/// <summary>
/// <c>Scripts/Physics/System/move_all_bullets.gml</c>: what happens to a
/// projectile between being fired and hitting something.
/// </summary>
/// <remarks>
/// <b>These exist because the 898-tick closed-loop replay could not see any of
/// it.</b> A <c>Shot</c> is never serialised, so its trajectory is observable
/// only where it changes whether somebody is hit — and for three phases this
/// repo gave a pellet no gravity, no wall test and a frozen <c>image_angle</c>,
/// and <c>reference-ctf_truefort-bots.trace</c> replayed with zero error
/// regardless. Every test below fails if its rule is removed, which is the
/// property the trace does not have.
/// <para>
/// The rules come from the <c>with(Shot)</c> block of that script, which is
/// reached from <c>Objects/Networking/GameServer.events/Step.xml:16</c> — not
/// from <c>Objects/Projectiles/Shot.events/</c>, which is where this repo
/// looked and is why it concluded there was nothing to transcribe.
/// </para>
/// </remarks>
public sealed class ProjectileMoveTests
{
    private const string Map = "ctf_truefort";

    /// <summary>Open ground on <c>ctf_truefort</c>, with the floor some way below.</summary>
    private const double OpenX = 2600;

    private const double OpenY = 708;

    private static CollisionWorld World() =>
        new(LevelMap.FromPng(Map, ReferenceTree.ReadMap(Map)));

    /// <summary>One world pixel, asked of the bullet's own solids.</summary>
    private static bool SolidAt(CollisionWorld world, int x, int y) =>
        !world.PlaceFree(new Aabb(x, y, x, y), CollisionFilter.ForBullet());

    /// <summary>The first solid row under a point. Fails the test if there is none.</summary>
    private static int FloorBelow(CollisionWorld world, int x, int y)
    {
        for (var probe = y; probe < y + 600; probe++)
        {
            if (SolidAt(world, x, probe))
                return probe;
        }

        Assert.Fail($"no floor under ({x}, {y}) on {Map}; the map or the open point has moved");
        return 0;
    }

    /// <summary>
    /// A <c>Shot</c> as <c>createShot</c> leaves one: direction, then angle,
    /// then speed.
    /// </summary>
    private static ProjectileState Shot(double x, double y, double direction, double speed)
    {
        var shot = new ProjectileState
        {
            Kind = ProjectileKind.Shot,
            X = x,
            Y = y,
            OwnerSlot = ProjectileState.NoOwner,
            Team = Team.Red,
            Source = DamageSource.Scattergun,
            HitDamage = 8,
            Lifetime = 40,
        };

        shot.Motion.Direction = direction;
        shot.ImageAngle = direction;
        shot.Motion.Speed = speed;

        return shot;
    }

    private static ProjectileState Live(in WorldState state, int index) =>
        state.Projectiles[state.LiveProjectileSlot(index)];

    /// <summary>A scout with a loaded scattergun and the trigger held.</summary>
    private static void PutScout(ref WorldState state, int slot, double aimDegrees)
    {
        state.Characters[slot] = new CharacterState
        {
            Class = PlayerClass.Scout,
            Team = Team.Red,
            X = OpenX,
            Y = OpenY,
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
            NetAimDirection = (ushort)Math.Round(aimDegrees * 65536.0 / 360.0),
            KeyState = PlayerKeys.Attack,
        };

        state.Weapons[slot] = WeaponState.Spawn(PlayerClass.Scout, OpenX, OpenY);
        state.Weapons[slot].ReadyToShoot = true;
        state.Weapons[slot].RefireTimer = WeaponState.Inactive;
        state.Alive[slot] = true;
    }

    // --- gravity --------------------------------------------------------------

    /// <summary>
    /// A pellet gains <c>0.15</c> of downward speed a tick, and gains it
    /// <em>before</em> it moves.
    /// </summary>
    /// <remarks>
    /// <c>vspeed += 0.15 * global.delta_factor;</c> sits above the position
    /// look-ahead in <c>move_all_bullets.gml</c>, so the first tick of flight
    /// already carries a tick of drop. Asserting the position rather than only
    /// the speed is what pins the ordering: applied after the move, the pellet
    /// would leave the muzzle level and this <c>Y</c> would be <c>OpenY</c>.
    /// </remarks>
    [Fact]
    public void APelletFallsFromTheTickItIsFired()
    {
        var world = World();
        var state = default(WorldState);
        state.SpawnProjectile(Shot(OpenX, OpenY, direction: 0, speed: 13));

        Projectiles.Move(world, ref state, ref DiscardedEvents.Events);

        var shot = Live(state, 0);
        Assert.Equal(0.15, shot.Motion.VSpeed);
        Assert.Equal(OpenY + 0.15, shot.Y);
        Assert.Equal(OpenX + 13, shot.X);
    }

    /// <summary>The drop accumulates, so a pellet's path is a parabola.</summary>
    [Fact]
    public void TheDropAccumulatesAcrossTicks()
    {
        var world = World();
        var state = default(WorldState);
        state.SpawnProjectile(Shot(OpenX, OpenY, direction: 0, speed: 13));

        var y = OpenY;
        for (var tick = 1; tick <= 4; tick++)
        {
            Projectiles.Move(world, ref state, ref DiscardedEvents.Events);

            var shot = Live(state, 0);
            Assert.Equal(0.15 * tick, shot.Motion.VSpeed);

            // Each tick's step is one tick longer than the last, which is the
            // whole difference between a parabola and the straight line this
            // repo drew for three phases.
            Assert.Equal(y + (0.15 * tick), shot.Y);
            y = shot.Y;
        }
    }

    /// <summary>
    /// Gravity goes through the component setter, so it rewrites the pellet's
    /// heading and speed too.
    /// </summary>
    /// <remarks>
    /// <c>vspeed</c> is one of GM8's four linked motion variables (see
    /// <see cref="Motion"/>), and the knockback a hit deals is
    /// <c>motion_add(shot.direction, shot.speed * 0.03)</c> — so a falling
    /// pellet pushes its victim slightly downwards as well as along.
    /// </remarks>
    [Fact]
    public void GravityRewritesTheHeadingAndTheSpeed()
    {
        var world = World();
        var state = default(WorldState);
        state.SpawnProjectile(Shot(OpenX, OpenY, direction: 0, speed: 13));

        Projectiles.Move(world, ref state, ref DiscardedEvents.Events);

        var shot = Live(state, 0);

        // Downwards on the screen is a *negative* GM8 direction: y grows down.
        Assert.True(shot.Motion.Direction > 359 && shot.Motion.Direction < 360);
        Assert.True(shot.Motion.Speed > 13);
        Assert.Equal(Math.Sqrt((13 * 13) + (0.15 * 0.15)), shot.Motion.Speed, 12);
    }

    // --- image_angle ----------------------------------------------------------

    /// <summary>
    /// A pellet's box turns to follow its own heading, overwriting the aim angle
    /// <c>createShot</c> gave it.
    /// </summary>
    /// <remarks>
    /// <c>image_angle = direction;</c>, every pass. The Scattergun scatters
    /// <c>direction</c> alone; the scattered value is then copied onto the angle
    /// before anything can look at the box, so a blast's six pellets each end up
    /// with their own box angle rather than sharing the aim's.
    /// </remarks>
    [Fact]
    public void EveryPelletOfABlastEndsUpWithItsOwnBoxAngle()
    {
        var world = World();
        var state = default(WorldState);
        PutScout(ref state, 1, aimDegrees: 30);

        MatchSimulation.Tick(world, ref state, []);

        Assert.Equal(6, state.LiveProjectileCount);

        var aim = state.Characters[1].AimDirection;
        var angles = new HashSet<double>();

        for (var i = 0; i < state.LiveProjectileCount; i++)
        {
            var pellet = Live(state, i);

            Assert.Equal(pellet.Motion.Direction, pellet.ImageAngle);
            angles.Add(pellet.ImageAngle);
        }

        // Six independent draws of random(15)-7.5: they are not all the aim, and
        // they are not all each other. Either failure means the assignment above
        // stopped happening.
        Assert.DoesNotContain(aim, angles);
        Assert.True(angles.Count > 1, "a blast's pellets all share one box angle");
    }

    // --- walls ----------------------------------------------------------------

    /// <summary>
    /// A pellet that flies into a wall is destroyed there, having dealt no
    /// damage.
    /// </summary>
    /// <remarks>
    /// The look-ahead <c>place_free</c> at the destination.
    /// </remarks>
    [Fact]
    public void APelletFlownIntoTheFloorDies()
    {
        var world = World();
        var floor = FloorBelow(world, (int)OpenX, (int)OpenY);
        var state = default(WorldState);

        // Five pixels of clear air above the floor, aimed straight down at 13 a
        // tick: one move puts it well inside.
        state.SpawnProjectile(Shot(OpenX, floor - 5, direction: 270, speed: 13));
        Assert.True(world.PlaceFree(Live(state, 0).Box, CollisionFilter.ForBullet()));

        Projectiles.Move(world, ref state, ref DiscardedEvents.Events);

        Assert.Equal(0, state.LiveProjectileCount);
    }

    /// <summary>
    /// The control: the same pellet fired the other way is untouched.
    /// </summary>
    /// <remarks>
    /// Without this the test above would pass just as well if <c>Move</c>
    /// destroyed everything it was handed.
    /// </remarks>
    [Fact]
    public void APelletFlownAwayFromTheFloorSurvives()
    {
        var world = World();
        var floor = FloorBelow(world, (int)OpenX, (int)OpenY);
        var state = default(WorldState);

        // Thirty pixels up rather than five: a pellet's box runs *backwards*
        // from its position (the origin of ShotS is at the tip), so one pointing
        // up trails seventeen pixels down and would clip the floor from where
        // the test above starts.
        var rising = Shot(OpenX, floor - 30, direction: 90, speed: 13);
        Assert.True(world.PlaceFree(rising.Box, CollisionFilter.ForBullet()));

        state.SpawnProjectile(rising);
        Projectiles.Move(world, ref state, ref DiscardedEvents.Events);

        Assert.Equal(1, state.LiveProjectileCount);
    }

    /// <summary>
    /// A pellet born inside a wall dies on its first pass even when the place it
    /// was heading for is clear.
    /// </summary>
    /// <remarks>
    /// <c>if(firststep) colliding |= !place_free(x, y);</c> — the muzzle is
    /// tested once, and only once. It is why the Scattergun nudges each pellet
    /// fifteen pixels forward with its own comment "to avoid immediate collision
    /// with a wall behind the character": without the nudge a scout with their
    /// back to a wall would fire six pellets that died where they were made.
    /// <para>
    /// The control below is what makes this an assertion about the muzzle test
    /// rather than about the destination one: an identical pellet that has
    /// already had a pass survives, from the same position, ending up in the
    /// same clear air.
    /// </para>
    /// </remarks>
    [Fact]
    public void APelletBornInsideAWallDies()
    {
        var world = World();
        var floor = FloorBelow(world, (int)OpenX, (int)OpenY);
        var state = default(WorldState);

        // Buried, and travelling fast enough upwards that a whole box-length of
        // pellet clears the floor in one pass.
        var buried = Shot(OpenX, floor + 5, direction: 90, speed: 40);
        Assert.False(world.PlaceFree(buried.Box, CollisionFilter.ForBullet()));

        state.SpawnProjectile(buried);
        Projectiles.Move(world, ref state, ref DiscardedEvents.Events);

        Assert.Equal(0, state.LiveProjectileCount);
    }

    /// <summary>
    /// The control for the muzzle test: the same buried pellet, one pass older,
    /// lives.
    /// </summary>
    [Fact]
    public void APelletThatHasAlreadyMovedIsNotTestedAtTheMuzzle()
    {
        var world = World();
        var floor = FloorBelow(world, (int)OpenX, (int)OpenY);
        var state = default(WorldState);

        var buried = Shot(OpenX, floor + 5, direction: 90, speed: 40);
        buried.HasMoved = true;
        state.SpawnProjectile(buried);

        Projectiles.Move(world, ref state, ref DiscardedEvents.Events);

        Assert.Equal(1, state.LiveProjectileCount);

        // And it really did land somewhere clear, which is what makes the
        // previous test a statement about the muzzle rather than about here.
        var moved = Live(state, 0);
        Assert.True(moved.Y < floor);
        Assert.True(world.PlaceFree(moved.Box, CollisionFilter.ForBullet()));
    }

    /// <summary>
    /// <c>firststep</c> is set by the pass itself, so the muzzle test happens
    /// exactly once.
    /// </summary>
    [Fact]
    public void TheMuzzleTestHappensOnlyOnTheFirstPass()
    {
        var world = World();
        var state = default(WorldState);
        state.SpawnProjectile(Shot(OpenX, OpenY, direction: 0, speed: 13));

        Assert.False(Live(state, 0).HasMoved);

        Projectiles.Move(world, ref state, ref DiscardedEvents.Events);

        Assert.True(Live(state, 0).HasMoved);
    }
}
