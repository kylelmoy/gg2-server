using GG2.Protocol;

namespace GG2.Simulation;

public static partial class Projectiles
{
    /// <summary>
    /// <c>move_all_bullets.gml</c>'s <c>with(Rocket)</c> block. True if the
    /// rocket was destroyed.
    /// </summary>
    /// <remarks>
    /// <b>Four things a pellet does not do.</b> It accelerates and then drags;
    /// it measures its distance from a leash anchored on its owner and starts
    /// fading when the leash runs out; it dies to an alpha countdown rather than
    /// to a lifetime; and — the one that changes where an explosion lands —
    /// <b>it does not move at all on a tick it is already colliding</b>, so that
    /// the blast is centred on what it hit rather than a step past it.
    /// </remarks>
    private static bool MoveRocket(
        CollisionWorld world, ref WorldState state, ProjectileHandle handle, ref TickEvents events)
    {
        ref var rocket = ref state.Projectiles[handle.Index];

        // if(instance_exists(owner)) { lastknownx = owner.x; ... }
        //
        // `owner` is the character, not the player — so the anchor follows a
        // living shooter around and freezes where a dead one fell. A rocket
        // whose shooter dies keeps whatever range it had left from that spot.
        if (rocket.OwnerSlot != ProjectileState.NoOwner && state.Alive[rocket.OwnerSlot])
        {
            rocket.LastKnownX = state.Characters[rocket.OwnerSlot].X;
            rocket.LastKnownY = state.Characters[rocket.OwnerSlot].Y;
        }

        var travelled = Gml.PointDistance(rocket.X, rocket.Y, rocket.LastKnownX, rocket.LastKnownY);
        if (Gml.GreaterOrEqual(travelled, rocket.DistanceToTravel))
        {
            rocket.Fade = true;
            rocket.FadeTimer = RocketFadeTicks;
        }

        // Two separate polar writes, in this order. Each rewrites the components
        // and snaps them (see Motion), so `speed = (speed + 1) * 0.92` is a
        // different number in the last bits.
        rocket.Motion.Speed += RocketAcceleration;
        rocket.Motion.Speed *= RocketDrag;

        rocket.ImageAngle = rocket.Motion.Direction;

        if (rocket.Fade)
            rocket.FadeTicks++;

        // The alpha countdown, which is a real destroy and not a draw call. See
        // ProjectileState.FadeTicks for why the tenth fading pass is the last
        // one and not the eleventh.
        if (rocket.FadeTicks >= RocketFadeLifetime)
        {
            state.FreeProjectile(handle);
            return true;
        }

        // colliding = explodeImmediately — a rocket fired with a wall already
        // between the weapon and the muzzle goes off where it was made.
        var colliding = rocket.ExplodeImmediately;

        if (!rocket.HasMoved)
            colliding |= !ProjectileMask.PlaceFree(world, in rocket, state.BulletFilter);

        // ⚠️ `if(!colliding)`, where the Shot moves unconditionally. The
        // reference's own comment: "Only move forward if we don't collide, to
        // ensure that the rocket will explode at the place of its collision."
        // Moving first would shift every wall blast by a tick of travel and
        // change who is inside the radius.
        if (!colliding)
        {
            rocket.X += rocket.Motion.HSpeed;
            rocket.Y += rocket.Motion.VSpeed;
            colliding |= !ProjectileMask.PlaceFree(world, in rocket, state.BulletFilter);
        }

        rocket.HasMoved = true;

        if (!colliding)
            return false;

        // if (!fade) event_user(5) else instance_destroy().
        //
        // ⚠️ A rocket past its range does no damage at all — not to the wall, and
        // not to somebody standing in front of it. It is deleted where it is.
        if (rocket.Fade)
        {
            state.FreeProjectile(handle);
            return true;
        }

        return Explosions.Explode(world, ref state, handle, CharacterState.Nobody, ref events);
    }

    /// <summary>
    /// <c>move_all_bullets.gml</c>'s <c>with(Mine)</c> block. True if the mine
    /// was destroyed.
    /// </summary>
    /// <remarks>
    /// <b>The only projectile that stops without dying.</b> A mine that reaches
    /// an <c>Obstacle</c> is backed out of it, has its speed zeroed and sticks;
    /// one that reaches anything else a bullet collides with — a gate, a bullet
    /// wall — is destroyed where it is. The difference is one
    /// <c>place_meeting(x, y, Obstacle)</c>, and it is why
    /// <see cref="CollisionWorld.PlaceFreeInMask(in Aabb)"/> exists.
    /// </remarks>
    private static bool MoveMine(CollisionWorld world, ref WorldState state, ProjectileHandle handle)
    {
        ref var mine = ref state.Projectiles[handle.Index];

        if (mine.Stickied)
        {
            // if (reflector != noone and alarm[0] < 0) alarm[0] = 30: a
            // reflected mine that lands starts the thirty-tick window in which
            // its blast still credits the pyro. The mine's Alarm 0 is
            // `reflector = noone`, not a destroy — Timers.Advance special-cases
            // it.
            if (mine.Reflector != ProjectileState.NoOwner && mine.Lifetime < 0)
                mine.Lifetime = MineReflectorWindow;

            mine.SplashThreshold = false;
        }
        else
        {
            // A component write, so it rewrites direction and speed — which the
            // substep loop below then reads.
            mine.Motion.VSpeed = Math.Min(mine.Motion.VSpeed + MineGravity, MineTerminalVSpeed);
            mine.SplashThreshold = true;
        }

        // A stuck mine has speed 0 and skips the whole of the rest, which is why
        // it never falls again and never re-tests its position.
        if (!Gml.NotEqual(mine.Motion.Speed, 0))
            return false;

        // ⚠️ `colliding = !place_free`, not `|=`: unlike the Shot's block this
        // one *assigns*, so the muzzle test is the whole of the initial value.
        var colliding = !mine.HasMoved && !ProjectileMask.PlaceFree(world, in mine, state.BulletFilter);

        SubstepWalk(world, ref mine, MineSubstep, state.BulletFilter, ref colliding);
        mine.HasMoved = true;

        if (!colliding)
            return false;

        // place_meeting(x, y, Obstacle) — the map alone, because the reference
        // swaps gunSetSolids for wallSetSolid around this and wallSetSolid is
        // `with(Obstacle) solid = true` and nothing more.
        if (!ProjectileMask.PlaceFreeInMask(world, in mine))
        {
            BackOutOfWalls(world, ref mine, mine.Motion.Direction + 180, mine.Motion.Speed);

            // A polar write of zero. It leaves `direction` alone — which is what
            // the back-out above just used — and takes both components to zero,
            // so the mine stops and stays stopped.
            mine.Motion.Speed = 0;
            mine.Stickied = true;
        }

        // Re-tested after the stick: a mine that backed out successfully is now
        // free and lives. One that hit a gate never entered the branch at all,
        // and one that could not escape the wall dies in it.
        if (ProjectileMask.PlaceFree(world, in mine, state.BulletFilter))
            return false;

        state.FreeProjectile(handle);
        return true;
    }

    /// <summary>
    /// <c>Rocket.events/Collision with Character.xml</c>. True if the rocket was
    /// destroyed.
    /// </summary>
    /// <remarks>
    /// <b>Almost nothing the pellet's event guards on is guarded on here.</b>
    /// There is no <c>place_free</c>, no <c>hp &gt; 0</c>, no <c>ubered</c> and
    /// no owner check — a rocket that reaches an enemy through a wall still
    /// explodes on them, and the uber test happens later, inside the blast. The
    /// only branch is team, and a team-mate takes the second arm rather than
    /// nothing.
    /// </remarks>
    private static bool RocketHitCharacter(
        CollisionWorld world,
        ref WorldState state,
        ProjectileHandle handle,
        int slot,
        ref TickEvents events)
    {
        ref var rocket = ref state.Projectiles[handle.Index];
        ref var victim = ref state.Characters[slot];

        if (victim.Team == rocket.Team)
        {
            // else if (ds_list_find_index(playersPassed, other.player) == -1
            //          and other.player != ownerPlayer)
            //
            // A rocket loses a hundred pixels of range for each *different*
            // team-mate it passes through, and its own shooter is free. This is
            // the only thing that ever changes distanceToTravel.
            var bit = 1UL << slot;

            if ((rocket.PlayersPassed & bit) == 0 && slot != rocket.OwnerSlot)
            {
                rocket.PlayersPassed |= bit;
                rocket.DistanceToTravel -= RocketPassThroughCost;
            }

            return false;
        }

        victim.DealFlicker();

        // really_move_contact_solid(direction, speed), with the victim made
        // solid for the length of the call and *everything else* not — the
        // movement pass has already run gunUnsetSolids by the time a collision
        // event fires. So the rocket backs out of the character it is inside and
        // nothing else can stop it.
        BackOutOfCharacter(ref rocket, in victim, rocket.Motion.Direction + 180, rocket.Motion.Speed);

        // with(other) motion_add(other.direction, 2).
        //
        // ⚠️ Inside the `with`, `other` is the rocket — the same trap as the
        // pellet's knockback. A standing target is shoved two units along the
        // rocket's heading, not along its own.
        victim.Motion.MotionAdd(rocket.Motion.Direction, 2);

        return Explosions.Explode(world, ref state, handle, (byte)slot, ref events);
    }
}
