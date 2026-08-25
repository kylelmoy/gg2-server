using Gg2.Protocol;

namespace Gg2.Simulation;

/// <summary>
/// The two tick phases that belong to projectiles rather than to characters:
/// GM8's automatic movement, and the collision events that follow it.
/// </summary>
/// <remarks>
/// <b>These are separate phases and not one loop, because the engine's are.</b>
/// Game Maker moves <em>every</em> instance and only then runs collision events,
/// so a pellet that would pass through a character mid-step still collides with
/// wherever it lands, and two pellets that reach the same victim on the same
/// tick both do so from their post-move positions. Interleaving move-and-check
/// per projectile is a different simulation.
/// <para>
/// <c>Shot</c> has neither a Step nor an End Step event
/// (<c>Objects/Projectiles/Shot.events/</c> holds a Create, an Alarm 0 and four
/// collision events, and nothing else), so movement is the whole of its
/// per-tick behaviour: <c>x += hspeed</c>, <c>y += vspeed</c>, with no gravity,
/// no drag and no wall test.
/// </para>
/// <para>
/// ⚠️ <b>A pellet is not stopped by walls, and that is not an omission.</b>
/// Nothing checks the map while a shot is in flight. The only
/// <c>place_free</c> is at the top of the collision events, so a pellet that
/// reaches a character <em>through</em> a wall dies at that moment, having
/// dealt no damage — and one that flies into a wall with nobody behind it sails
/// on until its alarm expires. Transcribed from
/// <c>Shot.events/Collision with Character.xml</c>; do not "fix" it.
/// </para>
/// </remarks>
public static class Projectiles
{
    /// <summary>
    /// The automatic-movement phase: every projectile advances by its own
    /// velocity.
    /// </summary>
    /// <remarks>
    /// Written straight onto the position rather than through
    /// <see cref="Motion"/>, because moving an instance does not touch its
    /// motion variables — this is GM8 adding <c>hspeed</c> to <c>x</c>, not a
    /// velocity being recomputed.
    /// <para>
    /// Characters are not here. Their movement is folded into
    /// <see cref="CharacterPhysics.Step"/>, because
    /// <c>Character.events/Step.xml</c> ends by subtracting the same two values
    /// so that the engine's move puts them back — see that file's remarks. The
    /// net effect is identical and this phase has nothing left to do for them.
    /// </para>
    /// <para>
    /// <b>A projectile created earlier in the same tick moves in it, and that is
    /// measured rather than assumed.</b> A weapon fires in Begin Step and this
    /// pass comes later in the same frame, so the question is whether GM8's
    /// movement pass walks instances that did not exist when the frame started.
    /// It does: delaying a pellet's first move by one tick and replaying
    /// <c>reference-ctf_truefort-bots.trace</c> closed-loop diverges at tick 393
    /// — the same six pellets still land, but a tick late, and both scouts'
    /// <c>hspeed</c> at the next broadcast is wrong by the knockback. With the
    /// move here the whole 898-tick recording is exact. See
    /// <c>PhysicsParityTests.ClosedLoopIsExactAcrossTheWholeTrace</c>.
    /// </para>
    /// </remarks>
    public static void Move(ref WorldState state)
    {
        for (var i = 0; i < state.LiveProjectileCount; i++)
        {
            ref var projectile = ref state.Projectiles[state.LiveProjectileSlot(i)];

            projectile.X += projectile.Motion.HSpeed;
            projectile.Y += projectile.Motion.VSpeed;
        }
    }

    /// <summary>
    /// The collision phase: every projectile against every character.
    /// </summary>
    /// <remarks>
    /// Projectiles are walked in <b>creation order</b>
    /// (<see cref="WorldState.LiveProjectileSlot"/>) and characters in slot
    /// order, which is the reference's instance order in both cases — GM8 runs a
    /// collision event over the instances of one object and then the instances
    /// of the other, both by id, and an id is issued at creation.
    /// </remarks>
    public static void Collide(CollisionWorld world, ref WorldState state)
    {
        ArgumentNullException.ThrowIfNull(world);

        // The index only advances when the projectile survived: a hit frees the
        // pool slot and compacts the creation-order array, so whatever slid into
        // i is the next projectile and i++ would skip it.
        for (var i = 0; i < state.LiveProjectileCount;)
        {
            if (CollideOne(world, ref state, state.LiveProjectileHandle(i)))
                continue;

            i++;
        }
    }

    /// <summary>
    /// One projectile against every character. True if it was destroyed.
    /// </summary>
    private static bool CollideOne(CollisionWorld world, ref WorldState state, ProjectileHandle handle)
    {
        // Hoisted out of the character loop: the box depends only on the
        // projectile, and nothing inside the loop moves it.
        var projectileBox = state.Projectiles[handle.Index].Box;

        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
        {
            if (!state.Alive[slot])
                continue;

            ref var victim = ref state.Characters[slot];

            // place_meeting's box, which is not distance_to_object's: the
            // facing inflation GM8 reports on bbox_left is a broad-phase bound
            // and the overlap test goes to the precise mask, which is symmetric
            // about the origin. Passing the facing here would widen every
            // left-facing character by two pixels for free (see Aabb).
            var victimBox = Aabb.ForCharacter(victim.Box, victim.X, victim.Y);

            if (!projectileBox.Overlaps(victimBox))
                continue;

            if (HitCharacter(world, ref state, handle, slot))
                return true;
        }

        return false;
    }

    /// <summary>
    /// <c>Shot.events/Collision with Character.xml</c>, in full. True if the
    /// projectile was destroyed.
    /// </summary>
    private static bool HitCharacter(
        CollisionWorld world,
        ref WorldState state,
        ProjectileHandle handle,
        int slot)
    {
        ref var projectile = ref state.Projectiles[handle.Index];

        // gunSetSolids(); if (!place_free(x, y)) { instance_destroy(); exit; }
        //
        // The event's first act, and it is about the *projectile's* own
        // position, not the character's: a pellet that has reached somebody
        // through a wall is inside that wall, and dies there without dealing
        // damage. This is the only wall test a Shot ever gets, which is why it
        // is here rather than in Move.
        if (!world.PlaceFree(projectile.Box, CollisionFilter.ForBullet()))
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

        // damageCharacter -> dealDamage. The deathmatch invulnerability guard in
        // dealDamage is inert outside deathmatch (Character.events/Create.xml
        // sets deathmatch_invulnerable = 0 for every other mode), and
        // global.dealDamageFunction is the empty string unless a mod sets it
        // (Scripts/Game/game_init.gml:356).
        victim.Hp -= projectile.HitDamage;
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
        // doing. Applied to every class, as the reference does — a non-spy
        // simply never has a cloakAlpha below 1 for it to raise.
        victim.CloakAlpha = Math.Max(victim.CloakAlpha, 0.3);
        victim.CloakFlicker = true;
        victim.CloakFlickerTimer = 3;

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
