using Gg2.Protocol;

namespace Gg2.Simulation;

public static partial class Weapons
{
    /// <summary>
    /// <c>Objects/Weapons/Scattergun.events/User Event 3.xml</c> — six pellets
    /// and eighteen random draws — and <c>Shotgun.events/User Event 3.xml</c>,
    /// which is the same event with four numbers changed.
    /// </summary>
    /// <remarks>
    /// <b>The two events are otherwise line for line identical</b>, down to the
    /// order of the three writes per pellet and the fifteen-pixel nudge, so they
    /// share a body rather than being copied. What differs:
    /// <list type="bullet">
    /// <item>five pellets rather than six, and so fifteen draws rather than
    /// eighteen;</item>
    /// <item>7 damage rather than 8;</item>
    /// <item>a tighter cone — <c>random(11) - 5.5</c> against
    /// <c>random(15) - 7.5</c>;</item>
    /// <item>⚠️ <b>no lifetime override at all.</b> The Scattergun ends every
    /// pellet with <c>alarm[0] = 35 * (...)</c>, the range compensation in
    /// <see cref="ScattergunPelletLifetime"/>; the Shotgun writes nothing, so its
    /// pellets keep the <c>Shot</c>'s own 40 and a running engineer's shots
    /// out-range a standing one's. Deleting the compensation would make the two
    /// weapons agree, which is exactly why it is worth saying.</item>
    /// </list>
    /// <para>
    /// The draw count is contract for both: every client re-derives the blast
    /// from the broadcast seed, so five pellets must cost exactly fifteen
    /// advances.
    /// </para>
    /// </remarks>
    private static void FireScattergun(
        ref WorldState state,
        int slot,
        WeaponKind kind,
        ref GmlRandom random)
    {
        ref var character = ref state.Characters[slot];
        ref var weapon = ref state.Weapons[slot];
        var stats = WeaponStats.For(kind);

        var scattergun = kind == WeaponKind.Scattergun;
        var pellets = scattergun ? ScattergunPellets : ShotgunPellets;
        var spread = scattergun ? ScattergunSpread : ShotgunSpread;
        var damage = scattergun ? ShotHitDamage : ShotgunHitDamage;
        var source = scattergun ? DamageSource.Scattergun : DamageSource.Shotgun;

        weapon.AmmoCount = Math.Max(0, weapon.AmmoCount - 1);

        var aim = character.AimDirection;
        var muzzleX = weapon.X;
        var muzzleY = weapon.Y;
        // Hoisted out of the repeat(), where the reference recomputes it. It
        // reads only the aim and the owner's hspeed, neither of which the loop
        // touches, and it draws nothing from the seeded stream — so this is the
        // same value every pellet and the same number of advances.
        //
        // The Shotgun has none, so its pellets keep the Shot's own 40.
        var lifetime = scattergun ? ScattergunPelletLifetime(aim, character.HSpeed) : ShotLifetime;

        for (var pellet = 0; pellet < pellets; pellet++)
        {
            var shot = CreateShot(
                muzzleX,
                muzzleY,
                ProjectileKind.Shot,
                source,
                aim,
                ScattergunPelletSpeed,
                slot,
                character.Team,
                ref random);

            shot.HitDamage = damage;

            // Order matters and so does the representation each step writes
            // through: a component write, then a speed write, then a direction
            // write. Each round trip re-derives the other pair and snaps it
            // (see Motion), so collapsing these into one polar assignment gives
            // different last bits and, near an axis, a different answer.
            shot.Motion.HSpeed += character.HSpeed;
            shot.Motion.Speed += random.Next(4) - 2;
            shot.Motion.Direction += random.Next(spread) - (spread / 2);

            // lengthdir, not cos/sin: the engine's snaps and a bare cosine does
            // not, and a pellet fired at 90 degrees must land exactly 15 pixels
            // above the muzzle.
            shot.X += Gml.LengthDirX(ScattergunNudge, shot.Motion.Direction);
            shot.Y += Gml.LengthDirY(ScattergunNudge, shot.Motion.Direction);

            shot.Lifetime = lifetime;

            state.SpawnProjectile(shot);
        }

        weapon.ReadyToShoot = false;
        weapon.RefireTimer = stats.RefireTime;
        weapon.ReloadTimer = stats.ReloadBuffer + stats.ReloadTime;
    }

    /// <summary>
    /// <c>Objects/Weapons/Rocketlauncher.events/User Event 3.xml</c>: one
    /// rocket, no random draws.
    /// </summary>
    /// <remarks>
    /// <b>The whole event is deterministic.</b> A rocket has no spread, so
    /// nothing between <c>random_set_seed</c> and the end of the event touches
    /// the stream — the seed on the wire is drawn, sent and then ignored by both
    /// sides. That is not an argument for skipping the draw: it comes out of the
    /// same <c>irandom(65535)</c> in User Event 1 that every other weapon uses.
    /// </remarks>
    private static void FireRocketlauncher(CollisionWorld world, ref WorldState state, int slot)
    {
        ref var character = ref state.Characters[slot];
        ref var weapon = ref state.Weapons[slot];
        var stats = WeaponStats.For(WeaponKind.Rocketlauncher);

        weapon.AmmoCount = Math.Max(0, weapon.AmmoCount - 1);

        var aim = character.AimDirection;

        // lengthdir, which snaps: a rocket fired straight up starts exactly
        // twenty pixels above the weapon rather than 3.5e-5 to one side.
        var muzzleX = weapon.X + Gml.LengthDirX(RocketMuzzleOffset, aim);
        var muzzleY = weapon.Y + Gml.LengthDirY(RocketMuzzleOffset, aim);

        var rocket = CreateRocket(muzzleX, muzzleY, aim, slot, character.Team);

        // collision_line_bulletblocking(other.x, other.y, newx, newy) — from the
        // *weapon* to the spawn point, evaluated inside a `with(oid)` where
        // `other` is the launcher. A rocket born the far side of something solid
        // goes off where it is instead of being deleted, so that a rocket jump
        // taken against a wall still happens on this tick.
        rocket.ExplodeImmediately = world.CollisionLineBlocksBullets(
            weapon.X,
            weapon.Y,
            muzzleX,
            muzzleY,
            state.BulletFilter);

        // The leash starts on the character rather than on the muzzle, so the
        // twenty pixels of offset are already spent out of the 800.
        rocket.LastKnownX = character.X;
        rocket.LastKnownY = character.Y;

        state.SpawnProjectile(rocket);

        weapon.ReadyToShoot = false;
        weapon.RefireTimer = stats.RefireTime;
        weapon.ReloadTimer = stats.ReloadBuffer + stats.ReloadTime;
    }

    /// <summary>
    /// <c>createShot</c> plus <c>Objects/Projectiles/Rocket.events/Create.xml</c>.
    /// </summary>
    private static ProjectileState CreateRocket(
        double x,
        double y,
        double direction,
        int ownerSlot,
        Team team)
    {
        var rocket = new ProjectileState
        {
            Kind = ProjectileKind.Rocket,
            X = x,
            Y = y,
            OwnerSlot = (byte)ownerSlot,
            Reflector = ProjectileState.NoOwner,
            Team = team,
            Source = DamageSource.Rocketlauncher,

            HitDamage = RocketHitDamage,
            Knockback = RocketKnockback,
            DistanceToTravel = RocketRange,
            KnockbackCapTimer = RocketKnockbackCapTicks,
            KnockbackEndTimer = RocketKnockbackEndTicks,
        };

        // No alarm[0]: a Rocket has no lifetime. It dies to a wall, to a
        // character, or to its own fade — never to a timer running out.
        //
        // direction first, then speed, as createShot writes them: with the speed
        // still zero the direction write leaves both components at zero, and it
        // is the speed write that turns the pair into a velocity.
        rocket.Motion.Direction = direction;
        rocket.ImageAngle = direction;
        rocket.Motion.Speed = RocketSpeed;

        return rocket;
    }

    /// <summary>
    /// <c>Objects/Weapons/Minegun.events/User Event 3.xml</c>: one mine, no
    /// random draws, and no collision test on the way out.
    /// </summary>
    /// <remarks>
    /// Deterministic like the Rocketlauncher's, and simpler still: there is no
    /// <c>collision_line_bulletblocking</c>, so a demoman with their back to a
    /// wall lobs a mine that starts inside it and is destroyed by the movement
    /// pass's muzzle test.
    /// <para>
    /// ⚠️ <b><c>oid.image_angle = 0</c>, written straight after
    /// <c>createShot</c> set it to the aim.</b> A mine is the one projectile
    /// whose bounding box is axis-aligned however it is thrown, and nothing ever
    /// writes the angle again — <c>move_all_bullets</c>'s mine block has no
    /// <c>image_angle = direction</c> where the Shot's and the Rocket's both do.
    /// </para>
    /// </remarks>
    private static void FireMinegun(ref WorldState state, int slot)
    {
        ref var character = ref state.Characters[slot];
        ref var weapon = ref state.Weapons[slot];
        var stats = WeaponStats.For(WeaponKind.Minegun);

        weapon.AmmoCount = Math.Max(0, weapon.AmmoCount - 1);

        var aim = character.AimDirection;
        var mine = new ProjectileState
        {
            Kind = ProjectileKind.Mine,
            X = weapon.X + Gml.LengthDirX(MineMuzzleOffset, aim),
            Y = weapon.Y + Gml.LengthDirY(MineMuzzleOffset, aim),
            OwnerSlot = (byte)slot,
            Reflector = ProjectileState.NoOwner,
            Team = character.Team,
            Source = DamageSource.Minegun,
            Knockback = MineKnockback,

            // Mine.events/Create.xml. A flying mine skips the outer quarter of
            // its own blast; landing clears this.
            SplashThreshold = true,

            // No alarm[0] is set, so it rests at GM8's -1 — and the reflected
            // mine's `alarm[0] < 0` re-arm in Projectiles.MoveMine reads the
            // resting value on the very tick it lands. A defaulted 0 would
            // skip that re-arm for one tick.
            Lifetime = Timers.Inactive,
        };

        mine.Motion.Direction = aim;
        mine.Motion.Speed = MineSpeed;

        // ...and then the launcher overwrites the angle with zero.
        mine.ImageAngle = 0;

        // No alarm[0]: a mine has no lifetime. It waits.
        state.SpawnProjectile(mine);

        weapon.Lobbed++;
        weapon.ReadyToShoot = false;
        weapon.RefireTimer = stats.RefireTime;
        weapon.ReloadTimer = stats.ReloadBuffer + stats.ReloadTime;
    }

    /// <summary>
    /// How long a scattergun pellet lives, given the shooter's aim and
    /// horizontal speed.
    /// </summary>
    /// <remarks>
    /// <b>A range compensation, and it looks like a mistake until you see what
    /// it is for.</b> Every pellet inherits the shooter's <c>hspeed</c>, so a
    /// scout running forwards throws pellets that travel further per tick and
    /// would otherwise out-range a standing one. The factor
    /// <c>(min(1, forward/actual) - 1)/2 + 1</c> shortens the fuse by up to
    /// half when the inherited speed helps, and is exactly 1 when it does not.
    /// <para>
    /// The reference spells it out at
    /// <c>Objects/Weapons/Scattergun.events/User Event 3.xml</c>, and it is
    /// transcribed rather than simplified: <c>abs</c> appears on the numerator
    /// and on the whole denominator but not on <c>cos</c> inside the
    /// denominator, so the two are not the same quantity and cancelling them
    /// changes the answer whenever <c>hspeed</c> opposes the aim.
    /// </para>
    /// <para>
    /// The apparent division by zero at a vertical aim does not happen: GM8's
    /// <c>cos(degtorad(90))</c> is 6.1e-17 rather than 0, so numerator and
    /// denominator are both tiny and the ratio is 1. The port's is the same
    /// 6.1e-17, for the same reason — <see cref="Gml.DegToRad"/> is one multiply.
    /// </para>
    /// </remarks>
    public static int ScattergunPelletLifetime(double aimDirection, double hspeed)
    {
        var cos = Math.Cos(Gml.DegToRad(aimDirection));
        var forward = Math.Abs(cos) * ScattergunPelletSpeed;
        var actual = Math.Abs((cos * ScattergunPelletSpeed) + hspeed);

        return Timers.SetAlarm(35 * (((Math.Min(1, forward / actual) - 1) / 2) + 1));
    }

    private static ProjectileState CreateShot(
        double x,
        double y,
        ProjectileKind kind,
        DamageSource source,
        double direction,
        double speed,
        int ownerSlot,
        Team team,
        ref GmlRandom random)
    {
        var shot = new ProjectileState
        {
            Kind = kind,
            X = x,
            Y = y,
            OwnerSlot = (byte)ownerSlot,
            Reflector = ProjectileState.NoOwner,
            Team = team,
            Source = source,
        };

        // instance_create runs the Create event before createShot's own `with`
        // block, so the choose() draw comes first in the seeded stream. Its
        // position in the order is what the client re-derives against.
        shot.Perseverant = random.ChooseIndex(3) == 2;
        shot.HitDamage = ShotHitDamage;
        shot.Lifetime = ShotLifetime;

        // direction first, then speed: with speed still zero the direction write
        // leaves both components at zero, and it is the speed write that turns
        // the pair into a velocity. Writing speed first would point the pellet
        // along the previous direction, which for a fresh instance is 0 degrees.
        shot.Motion.Direction = direction;
        shot.ImageAngle = direction;
        shot.Motion.Speed = speed;

        return shot;
    }
}
