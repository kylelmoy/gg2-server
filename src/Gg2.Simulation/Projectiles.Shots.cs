using Gg2.Protocol;

namespace Gg2.Simulation;

public static partial class Projectiles
{
    /// <summary>
    /// <c>move_all_bullets.gml</c>'s <c>with(Shot)</c> block, which is also its
    /// <c>with(Needle)</c> block. True if the projectile was destroyed.
    /// </summary>
    /// <param name="gravity">
    /// <see cref="ShotGravity"/> or <see cref="NeedleGravity"/> — the one line
    /// the two blocks disagree about.
    /// </param>
    private static bool MoveShot(
        CollisionWorld world,
        ref WorldState state,
        ProjectileHandle handle,
        double gravity)
    {
        ref var shot = ref state.Projectiles[handle.Index];

        // vspeed += 0.15. A pellet falls, at a quarter of a character's gravity.
        // ⚠ This repo left it out for a phase and the bot trace stayed
        // exact anyway: a Shot is never serialised, so the drop is observable
        // only where it changes whether something is hit, and at the ranges in
        // that recording it does not. Absence of a red test was not evidence.
        shot.Motion.VSpeed += gravity;

        // image_angle = direction, every pass. The pellet's box turns to follow
        // its own heading, so the aim angle createShot wrote survives only until
        // this line first runs — which is before any collision event can
        // look at the box.
        shot.ImageAngle = shot.Motion.Direction;

        // A pellet born inside a wall dies without getting anywhere. Only on the
        // first pass: afterwards the previous pass has already established that
        // the position is free, because a pellet that reached a solid one was
        // destroyed there.
        var colliding = !shot.HasMoved && !ProjectileMask.PlaceFree(world, in shot, state.BulletFilter);

        shot.X += shot.Motion.HSpeed;
        shot.Y += shot.Motion.VSpeed;
        shot.HasMoved = true;

        // `colliding |=`, not `else`: the destination is tested whether or not
        // the muzzle was. Both are the *bullet's* solids — a pellet passes
        // through a PlayerWall and is stopped by a BulletWall and by every gate,
        // whichever team's. state.BulletFilter.
        colliding |= !ProjectileMask.PlaceFree(world, in shot, state.BulletFilter);

        if (!colliding)
            return false;

        // The reference creates an Impact here, which is a particle: it has no
        // collision events and nothing on the server can observe it.
        state.FreeProjectile(handle);
        return true;
    }

    /// <summary>
    /// <c>Shot.events/Collision with Character.xml</c>, in full. True if the
    /// pellet was destroyed.
    /// </summary>
    private static bool ShotHitCharacter(
        CollisionWorld world,
        ref WorldState state,
        ProjectileHandle handle,
        int slot,
        ref TickEvents events)
    {
        ref var projectile = ref state.Projectiles[handle.Index];

        // gunSetSolids(); if (!place_free(x, y)) { instance_destroy(); exit; }
        //
        // The event's first act, and it is about the *projectile's* own
        // position, not the character's: a pellet that has reached somebody
        // through a wall is inside that wall, and dies there without dealing
        // damage. This is the only wall test a Shot ever gets, which is why it
        // is here rather than in Move.
        if (!ProjectileMask.PlaceFree(world, in projectile, state.BulletFilter))
        {
            state.FreeProjectile(handle);
            return true;
        }

        ref var victim = ref state.Characters[slot];

        // if(other.id != ownerPlayer.object and other.team != team
        //    && other.hp > 0 && other.ubered == 0)
        //
        // `ownerPlayer.object` is the owner's *current* character, so a pellet
        // cannot hit the player who fired it even after they have died and
        // respawned. A roster slot is both halves of that comparison here.
        if (projectile.OwnerSlot == slot
            || victim.Team == projectile.Team
            || !Gml.Greater(victim.Hp, 0)
            || victim.Ubered)
        {
            // Not a hit, and *not* a destroy: the pellet carries on through a
            // team-mate. The event simply falls off its end.
            return false;
        }

        // damageCharacter -> dealDamage. ⚠️ The return value is deliberately
        // ignored: a Team Deathmatch spawn bubble stops the hit points moving
        // and nothing else, so the timeUnscathed reset below and the assist
        // bookkeeping under it both still run — which is exactly what
        // damageCharacter.gml:3-7 does.
        DamageApi.Deal(ref events, slot, projectile.OwnerSlot, ref victim, projectile.HitDamage);
        victim.TimeUnscathed = 0;

        // Assist bookkeeping. The demoted dealer inherits what is *left* of the
        // finish-off window rather than getting a fresh one — `alarm[4] =
        // alarm[3]`, not ASSIST_TIME.
        if (victim.LastDamageDealer != projectile.OwnerSlot && victim.LastDamageDealer != slot)
        {
            victim.SecondToLastDamageDealer = victim.LastDamageDealer;
            victim.AssistTimer = victim.FinishOffTimer;
        }

        victim.FinishOffTimer = GameConstants.AssistTime;
        victim.LastDamageDealer = projectile.OwnerSlot;
        victim.LastDamageSource = projectile.Source;

        // dealFlicker: a hit spy is briefly visible whatever their cloak is
        // doing.
        victim.DealFlicker();

        // with(other) { motion_add(other.direction, other.speed*0.03); }
        //
        // ⚠️ Inside a `with`, `other` is the *caller* — so both arguments come
        // from the shot, not from the character being pushed. Reading them as
        // the victim's own direction and speed would make a hit accelerate
        // whoever was already moving and do nothing to somebody standing still.
        victim.Motion.MotionAdd(projectile.Motion.Direction, projectile.Motion.Speed * 0.03);

        state.FreeProjectile(handle);
        return true;
    }
}
