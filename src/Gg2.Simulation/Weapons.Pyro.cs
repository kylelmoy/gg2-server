using Gg2.Protocol;

namespace Gg2.Simulation;

public static partial class Weapons
{
    /// <summary>
    /// <c>Objects/Weapons/Flamethrower.events/User Event 1.xml</c>: one flame.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>the first two lines run whether or not the weapon can
    /// fire.</b> <c>if (ammoCount &lt; 1.8) ammoCount -= 1.8;</c> — the
    /// reference's own comment is "prevent sputtering", and it drives an
    /// already-empty tank further negative on every tick the trigger is held, so
    /// that the refill in <see cref="RefillFlamethrower"/> has something to climb
    /// out of and the weapon cannot fire one frame in every few. The clamp back
    /// to zero is in that same Begin Step.
    /// <para>
    /// ⚠️ <b>no seed, and no <c>WEAPON_FIRE</c>.</b> The event calls
    /// <c>randomize()</c> — so like the Minigun and the Blade the Flamethrower is
    /// exempt from docs/SIMULATION.md's "a weapon puts its seed on the wire", and
    /// the spread is server-side RNG the port is free about. It draws from
    /// <see cref="WorldState.Random"/> rather than reseeding, because a
    /// simulation with no clock cannot <c>randomize()</c> and does not need to.
    /// </para>
    /// <para>
    /// ⚠️ <b>the muzzle carries <c>owner.equipmentOffset</c>, which is
    /// computed in the character's <em>Draw</em> event</b>
    /// (<c>Character.events/Draw.xml:170-190</c>) as the run-animation bob. It is
    /// the only place in the game where a drawing variable feeds the simulation.
    /// A headless server has no Draw event, so this port keeps it at its
    /// <c>Create</c> value of 0 — a divergence of up to two pixels on a running
    /// pyro's flame origin. Reproducing it would mean porting the run-animation
    /// state machine, which is drawing.
    /// </para>
    /// </remarks>
    private static void FireFlamethrower(CollisionWorld world, ref WorldState state, int slot)
    {
        ref var character = ref state.Characters[slot];
        ref var weapon = ref state.Weapons[slot];
        var stats = WeaponStats.For(WeaponKind.Flamethrower);

        var aim = character.AimDirection;
        var muzzleX = weapon.X + Gml.LengthDirX(FlameMuzzleOffset, aim);
        var muzzleY = weapon.Y + Gml.LengthDirY(FlameMuzzleOffset, aim);

        // Above the guard, and unconditional. See the remarks.
        if (Gml.Less(weapon.AmmoCount, FlameAmmoCost))
            weapon.AmmoCount -= FlameAmmoCost;

        if (!weapon.ReadyToShoot
            || !Gml.GreaterOrEqual(weapon.AmmoCount, FlameAmmoCost)
            || world.CollisionLineBlocksBullets(
                weapon.X, weapon.Y, muzzleX, muzzleY, state.BulletFilter))
            return;

        // sign(random(2) - 1) * power(random(3), 1.8), then a factor of
        // `1 - hspeed/owner.basemaxspeed` — where `hspeed` is the *weapon's*, and
        // nothing in the game ever gives a weapon instance a speed. So that
        // factor is exactly 1; it is not transcribed, and it is a bug in the
        // reference that was meant to read `owner.hspeed`.
        var spread = Gml.Sign(state.Random.Next(2) - 1) * Math.Pow(state.Random.Next(3), 1.8);

        var flame = CreateBurning(
            ProjectileKind.Flame,
            muzzleX,
            muzzleY,
            aim + spread,
            FlameSpeed + state.Random.Next(FlameSpeedJitter),
            slot,
            character.Team,
            DamageSource.Flamethrower,
            ref state.Random);

        // ⚠️ `motion_add(owner.direction, owner.speed)` — the owner's whole
        // velocity vector, where a pellet and a needle take only its hspeed. A
        // falling pyro adds their fall to every flame.
        flame.Motion.MotionAdd(character.Motion.Direction, character.Motion.Speed);

        state.SpawnProjectile(flame);

        weapon.ReadyToShoot = false;
        weapon.IsRefilling = false;
        weapon.AmmoCount -= FlameAmmoCost;

        // A tank that just ran dry waits fourteen ticks rather than one, which is
        // what stops an empty pyro flickering out a flame every other frame.
        weapon.RefireTimer = Gml.Greater(weapon.AmmoCount, 0)
            ? stats.RefireTime
            : stats.ReloadBuffer * 2;

        weapon.ReloadTimer = stats.ReloadBuffer;
    }

    /// <summary>
    /// <c>Objects/Weapons/Flamethrower.events/User Event 2.xml</c>: the
    /// airblast, and the flare that rides on it.
    /// </summary>
    /// <remarks>
    /// <b>The blast's reach is only a rotated box and a circle — there is no
    /// cone test.</b> <c>blastAngle = 75</c> is set in the Create event and
    /// never read here, and the per-victim <c>angle</c> the event computes is
    /// dead too. An <c>AirBlastO</c> is created 25px ahead of the weapon along
    /// the aim, rotated to the aim (aim + 180 with <c>image_xscale = -1</c>
    /// when the pyro faces left — the weapon's xscale, which End Step writes in
    /// the same block as <c>currentWeapon.x</c>, so it is
    /// <see cref="CharacterState.Facing"/>), and every victim is tested with
    /// <c>collision_circle(x, y, r, poof, false, true)</c> centred on the
    /// <em>victim's</em> position against the poof's reported box.
    /// <para>
    /// <b>The poof is not pooled.</b> Its whole simulated effect happens inside
    /// this event; afterwards it is animation (Step counts <c>image_index</c>
    /// to 5) and an <c>instance_destroy</c> on touching an Obstacle, none of
    /// which the server can observe — and a later blast tests only against its
    /// <em>own</em> poof. The <c>motion_add(owner.direction, owner.speed)</c>
    /// it receives moves only those unobservable later frames.
    /// </para>
    /// <para>
    /// ⚠️ <b>The flare leaves before the reflect loops</b>, so <c>with(Flare)</c>
    /// walks the flare just fired — and skips it on team. And its gate reads
    /// <c>owner.keyState</c>, not the down-or-pressed union — the same
    /// distinction as the medic's uber pop.
    /// </para>
    /// <para>
    /// The <c>LooseSheet</c> and <c>Gib</c> loops are client-side debris a
    /// headless server has none of, and are not transcribed.
    /// </para>
    /// </remarks>
    private static void AirBlast(CollisionWorld world, ref WorldState state, int slot)
    {
        ref var character = ref state.Characters[slot];
        ref var weapon = ref state.Weapons[slot];

        if (!weapon.ReadyToBlast || !Gml.GreaterOrEqual(weapon.AmmoCount, BlastAmmoCost))
            return;

        var aim = character.AimDirection;
        var poofX = weapon.X + Gml.LengthDirX(FlameMuzzleOffset, aim);
        var poofY = weapon.Y + Gml.LengthDirY(FlameMuzzleOffset, aim);

        // if (image_xscale == 1) { poof.image_angle = aim } else { aim + 180,
        // xscale -1 } — the weapon's xscale is the owner's facing (End Step
        // writes both from the same test).
        var facing = character.Facing;
        var poofBox = Aabb.ForRotatedSpriteReported(
            AirBlastSpriteBbox,
            AirBlastOriginX,
            AirBlastOriginY,
            poofX,
            poofY,
            facing >= 0 ? aim : aim + 180,
            facing);

        // --- the flare -------------------------------------------------------
        if ((character.KeyState & PlayerKeys.Attack) != 0
            && Gml.GreaterOrEqual(weapon.AmmoCount, FlareAmmoThreshold)
            && weapon.ReadyToFlare
            && !world.CollisionLineBlocksBullets(
                weapon.X, weapon.Y, poofX, poofY, state.BulletFilter))
        {
            // The same x+lengthdir_x(25, aim) as the poof, and unlike the flame
            // there is no equipmentOffset on the y.
            var flare = CreateBurning(
                ProjectileKind.Flare,
                poofX,
                poofY,
                aim,
                FlareSpeed,
                slot,
                character.Team,
                DamageSource.Flare,
                ref state.Random);

            state.SpawnProjectile(flare);

            weapon.ReadyToFlare = false;
            weapon.AmmoCount -= FlareAmmoCost;
            weapon.FlareTimer = FlareReloadTime;
        }

        // --- reflects: rockets, then flares, then mines ----------------------
        for (var i = 0; i < state.LiveProjectileCount; i++)
        {
            ref var rocket = ref state.Projectiles[state.LiveProjectileSlot(i)];

            if (rocket.Kind != ProjectileKind.Rocket
                || rocket.Team == character.Team
                || !Aabb.CircleHits(rocket.X, rocket.Y, BlastReflectRadius, poofBox))
                continue;

            rocket.OwnerSlot = (byte)slot;
            rocket.Team = character.Team;
            rocket.Source = DamageSource.ReflectedRocket;

            // hitDamage = 25 and explosionDamage = 30 are the Rocket's own
            // Create values written back; the first is per-instance here and
            // the second is a per-kind lookup that already answers 30.
            rocket.HitDamage = 25;

            // knockback = 8 with both alarms re-armed — twice and nearly three
            // times the fresh rocket's 20/30, so a reflected rocket shoves for
            // longer than a fired one.
            rocket.Knockback = ReflectedRocketKnockback;
            rocket.KnockbackCapTimer = ReflectedKnockbackCapDelay;
            rocket.KnockbackEndTimer = ReflectedKnockbackEndDelay;

            // motion_set(aim, speed): the direction turns, the speed stays.
            rocket.Motion.Direction = aim;
        }

        for (var i = 0; i < state.LiveProjectileCount; i++)
        {
            ref var flare = ref state.Projectiles[state.LiveProjectileSlot(i)];

            if (flare.Kind != ProjectileKind.Flare
                || flare.Team == character.Team
                || !Aabb.CircleHits(flare.X, flare.Y, BlastReflectRadius, poofBox))
                continue;

            flare.OwnerSlot = (byte)slot;
            flare.Team = character.Team;
            flare.Source = DamageSource.ReflectedFlare;

            // alarm[0] = 40: a reflected flare gets its whole life back.
            flare.Lifetime = ReflectedFlareLife;

            flare.Motion.Direction = aim;
        }

        for (var i = 0; i < state.LiveProjectileCount; i++)
        {
            ref var mine = ref state.Projectiles[state.LiveProjectileSlot(i)];

            if (mine.Kind != ProjectileKind.Mine
                || mine.Team == character.Team
                || !Aabb.CircleHits(mine.X, mine.Y, BlastReflectRadius, poofBox))
                continue;

            // motion_set(aim, max(speed, blastStrength/3)) — a resting stuck
            // mine leaves at 28/3, a faster one keeps its speed.
            var speed = Math.Max(mine.Motion.Speed, BlastStrength / 3);
            mine.Motion.Direction = aim;
            mine.Motion.Speed = speed;

            // reflector = other.ownerPlayer — the pyro's *player*, so it
            // survives the pyro's death and is renumbered on a leave.
            mine.Reflector = (byte)slot;

            if (mine.Stickied)
            {
                // 0.65 is one of the literals GM8 parses a ULP high — Gml.
                mine.Motion.Speed *= Gml.PointSixFive;

                // The surface normal, from four place_meeting(±3, Obstacle)
                // probes — the map only, like the landing that stuck it.
                var probe = mine;
                probe.Y = mine.Y - 3;
                double dy = world.PlaceFreeInMask(probe.Box) ? 0 : 1;
                probe.Y = mine.Y + 3;
                dy -= world.PlaceFreeInMask(probe.Box) ? 0 : 1;
                probe.Y = mine.Y;
                probe.X = mine.X - 3;
                double dx = world.PlaceFreeInMask(probe.Box) ? 0 : 1;
                probe.X = mine.X + 3;
                dx -= world.PlaceFreeInMask(probe.Box) ? 0 : 1;

                var length = Math.Sqrt((dx * dx) + (dy * dy));
                if (Gml.Greater(length, 0))
                {
                    dx /= length;
                    dy /= length;
                    var normalSpeed = (dx * mine.Motion.HSpeed) + (dy * mine.Motion.VSpeed);

                    // Reflect only a velocity *into* the surface — component
                    // writes, so no snap, exactly the reference's hspeed -= ...
                    if (Gml.Less(normalSpeed, 0))
                    {
                        mine.Motion.HSpeed -= 2 * normalSpeed * dx;
                        mine.Motion.VSpeed -= 2 * normalSpeed * dy;
                    }
                }

                mine.Stickied = false;
            }
        }

        // --- the characters: shoves and extinguishes -------------------------
        for (var victimSlot = 0; victimSlot < WorldState.MaxSlots; victimSlot++)
        {
            if (!state.Alive[victimSlot])
                continue;

            ref var victim = ref state.Characters[victimSlot];

            // dist is measured from the *weapon* (`other.x` inside the with),
            // while the circle is centred on the victim — the Medigun's
            // two-instances trap again.
            var dist = Gml.PointDistance(weapon.X, weapon.Y, victim.X, victim.Y);

            if (!Aabb.CircleHits(victim.X, victim.Y, BlastCharacterRadius, poofBox))
                continue;

            if (victim.Team != character.Team)
            {
                // The shove. Note what is *not* guarded: no ubered test and no
                // hp test — an ubered enemy is thrown, and so is one who is
                // already dead but not yet noticed by End Step.
                victim.Motion.MotionAdd(aim, CharacterBlastStrength * (1 - (dist / BlastDistance)));
                victim.Motion.VSpeed -= 2;
                victim.MoveStatus = 3;

                if (victim.LastDamageDealer != slot && victim.LastDamageDealer != victimSlot)
                {
                    victim.SecondToLastDamageDealer = victim.LastDamageDealer;
                    victim.AssistTimer = victim.FinishOffTimer;
                }

                victim.FinishOffTimer = GameConstants.AssistTime;
                victim.LastDamageDealer = (byte)slot;

                // lastDamageSource = -1: a shove has no weapon, so a victim
                // blasted into a pit is credited to the pyro with no source.
                victim.LastDamageSource = DamageSource.None;
            }
            else if (Gml.Greater(victim.BurnIntensity, 0) || Gml.Greater(victim.BurnDuration, 0))
            {
                // The extinguish, which includes the pyro putting themself out.
                // Each of the victim's drawn flames comes loose as a real Flame
                // owned by the *blasting* pyro, flying along the aim.
                //
                // The reference places them at the victim's position plus
                // `flameArray` offsets — per-character randoms rolled once in
                // the Create event, uniform over the bbox half-extents. Both
                // that roll and this one are off the unseeded stream, so the
                // port rolls fresh offsets here rather than carrying five
                // doubles per character for four phases between fires.
                var box = victim.Box;
                var halfWidth = (box.Right - box.Left) / 2.0;
                var halfHeight = (box.Bottom - box.Top) / 2.0;

                // for (i = 0; i < realnumflames; ...) — a fractional bound, so
                // 3.7 flames means four. Recomputed by End Step *after* this
                // tick's burn decay, so the value read here is one tick stale.
                for (double i = 0; Gml.Less(i, victim.RealNumFlames); i++)
                {
                    var offsetX = -halfWidth + state.Random.Next(2 * halfWidth);
                    var offsetY = -halfHeight + state.Random.Next(2 * halfHeight);

                    var loose = CreateBurning(
                        ProjectileKind.Flame,
                        victim.X + offsetX,
                        victim.Y + offsetY,
                        aim,
                        FlameSpeed + state.Random.Next(2.5),
                        slot,
                        character.Team,
                        DamageSource.Flamethrower,
                        ref state.Random);

                    // instance_create, not createShot — so image_angle is never
                    // written and the box stays unrotated.
                    loose.ImageAngle = 0;

                    // with(f) motion_add(direction, speed): its own vector added
                    // to itself, so a loose flame flies at twice the rolled
                    // speed. The reference says what it says.
                    loose.Motion.MotionAdd(loose.Motion.Direction, loose.Motion.Speed);

                    state.SpawnProjectile(loose);
                }

                victim.BurnIntensity = 0;
                victim.BurnDuration = 0;
                victim.BurnedBy = CharacterState.Nobody;
                victim.AfterburnSource = DamageSource.None;
            }
        }

        // --- Finish (justBlast is the recoil animation and is not modelled) --
        weapon.ReadyToBlast = false;
        weapon.ReadyToShoot = false;
        weapon.ReloadTimer = BlastReloadTime;
        weapon.IsRefilling = false;
        weapon.BlastTimer = BlastReloadTime;
        weapon.RefireTimer = BlastNoFlameTime;
        weapon.AmmoCount -= BlastAmmoCost;
    }

    /// <summary>
    /// <c>createShot</c> plus a <c>BurningProjectile</c> child's Create event
    /// and the <c>event_inherited()</c> below it — a <c>Flame</c> or a
    /// <c>Flare</c>.
    /// </summary>
    /// <remarks>
    /// The parent's Create event is where <c>alarm[0]</c> and the one-in-eight
    /// <c>perseverant</c> draw live, and the child's is where the six numbers
    /// <see cref="FlameStats"/> holds are set. Both run, child first.
    /// </remarks>
    private static ProjectileState CreateBurning(
        ProjectileKind kind,
        double x,
        double y,
        double direction,
        double speed,
        int ownerSlot,
        Team team,
        DamageSource source,
        ref GmlRandom random)
    {
        var flame = new ProjectileState
        {
            Kind = kind,
            X = x,
            Y = y,
            OwnerSlot = (byte)ownerSlot,
            Reflector = ProjectileState.NoOwner,
            Team = team,
            Source = source,
            HitDamage = FlameStats.HitDamage(kind),
            Lifetime = FlameStats.Life(kind),

            // random(1) < 1/8, off the global stream. A perseverant flame is
            // never destroyed by burning somebody, so it can burn a whole team.
            Perseverant = random.Next(1) < FlameStats.PerseverantChance,
        };

        flame.Motion.Direction = direction;
        flame.ImageAngle = direction;
        flame.Motion.Speed = speed;

        return flame;
    }
}
