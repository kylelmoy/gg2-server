using GG2.Protocol;

namespace GG2.Simulation;

public static partial class Projectiles
{
    /// <summary>
    /// <c>move_all_bullets.gml</c>'s <c>with(BladeB)</c> block. True if the
    /// blade was destroyed.
    /// </summary>
    /// <remarks>
    /// <b>The <c>with(Shot)</c> block with three lines removed, and each
    /// absence is a rule.</b> There is <em>no gravity</em>, so a thrown blade
    /// flies dead straight for its fifteen ticks; there is no
    /// <c>image_angle = direction</c>, so the 0 that
    /// <c>Blade.events/User Event 2.xml</c> forced survives the whole flight
    /// (see that event's own comment, and GG2 issue #230); and there is no
    /// <c>image_alpha</c> fade, which is drawing either way.
    /// <para>
    /// What it keeps is the muzzle test on the first pass and the destination
    /// test on every pass — so a blade thrown into a wall dies in it, exactly
    /// like a pellet.
    /// </para>
    /// </remarks>
    private static bool MoveBlade(CollisionWorld world, ref WorldState state, ProjectileHandle handle)
    {
        ref var blade = ref state.Projectiles[handle.Index];

        var colliding = !blade.HasMoved && !ProjectileMask.PlaceFree(world, in blade, state.BulletFilter);

        blade.X += blade.Motion.HSpeed;
        blade.Y += blade.Motion.VSpeed;
        blade.HasMoved = true;

        colliding |= !ProjectileMask.PlaceFree(world, in blade, state.BulletFilter);

        if (!colliding)
            return false;

        state.FreeProjectile(handle);
        return true;
    }

    /// <summary>
    /// <c>StabMask.events/Step.xml</c>: the knife rides its owner.
    /// </summary>
    /// <remarks>
    /// The whole event is <c>x = owner.x; y = owner.y;</c> plus the mirror,
    /// which <see cref="ProjectileState.XScale"/> derives instead of storing.
    /// <para>
    /// ⚠️ <b>So the knife trails its spy by exactly one tick's velocity.</b> A
    /// <c>StabMask</c> is younger than every character, so this runs after
    /// their Steps — where the reference has each character back at
    /// <c>x - hspeed</c>, before GM8's movement pass — and then GM8 adds the
    /// mask's <em>own</em> speed, which is zero. A spy running at 5 stabs from
    /// five pixels behind where they are standing.
    /// </para>
    /// <para>
    /// A knife whose owner has stopped existing cannot arise: the Revolver's
    /// Destroy event takes it, like the Blade's bubbles and the Minegun's
    /// mines.
    /// </para>
    /// </remarks>
    public static void StabStep(ref WorldState state)
    {
        for (var i = 0; i < state.LiveProjectileCount; i++)
        {
            ref var mask = ref state.Projectiles[state.LiveProjectileSlot(i)];

            if (mask.Kind != ProjectileKind.Stab)
                continue;

            if (mask.OwnerSlot == ProjectileState.NoOwner || !state.Alive[mask.OwnerSlot])
                continue;

            ref var owner = ref state.Characters[mask.OwnerSlot];

            mask.X = owner.X - owner.HSpeed;
            mask.Y = owner.Y - owner.VSpeed;
        }
    }

    /// <summary>
    /// Both halves of the knife's reachability test:
    /// <c>place_meeting</c> from the <em>spy</em>, or a clear line from the
    /// knife.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The overlap test is the spy's box against the victim's, not the
    /// knife's.</b> <c>with(ownerPlayer.object) connecting |= place_meeting(x,
    /// y, otherId)</c> — so a spy standing inside somebody stabs them through
    /// any wall, and the line test below is only for the reach the knife has
    /// beyond arm's length.
    /// <para>
    /// ⚠️ <b>And the line starts twelve pixels above the knife</b>, at chest
    /// height, ending at the victim's <em>origin</em> — which is at their feet.
    /// <c>collision_line_bulletblocking</c> is <c>gunSetSolids</c>' list
    /// exactly: obstacles and the map, bullet walls, intel gates always, team
    /// gates unless the map is changing, setup gates while the timer runs. No
    /// player walls.
    /// </para>
    /// </remarks>
    private static bool StabConnects(
        CollisionWorld world,
        in WorldState state,
        in ProjectileState mask,
        in Aabb targetBox,
        double targetX,
        double targetY)
    {
        if (mask.OwnerSlot != ProjectileState.NoOwner && state.Alive[mask.OwnerSlot])
        {
            ref readonly var spy = ref state.Characters[mask.OwnerSlot];

            // place_meeting's box on both sides: no facing inflation.
            if (Aabb.ForCharacter(spy.Box, spy.X, spy.Y).Overlaps(targetBox))
                return true;
        }

        return !world.CollisionLineHitsMask(mask.X, mask.Y - StabLineHeight, targetX, targetY)
               && !world.CollisionLineBlocksBullets(
                   mask.X, mask.Y - StabLineHeight, targetX, targetY, state.BulletFilter);
    }

    /// <summary>
    /// <c>StabMask.events/Collision with Character.xml</c>. True if the knife
    /// was destroyed.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A stab hits two things and only then goes away</b> —
    /// <c>maxKills = 2</c> — so a knife buried in a medic still has one left for
    /// the patient behind them.
    /// <para>
    /// ⚠️ <b>A stab that cannot reach destroys itself.</b> The <c>else</c> is
    /// inside the four-clause guard, so touching a team-mate, a corpse or an
    /// ubered enemy costs nothing, and touching a reachable-looking enemy
    /// through a wall ends the knife outright.
    /// </para>
    /// <para>
    /// ⚠️ <b>Whether it is a backstab is decided by the two facings agreeing</b>
    /// — <c>sign(other.image_xscale) == sign(image_xscale)</c>, the victim's
    /// against the <em>knife's</em>, which is the direction the spy committed to
    /// thirty-two ticks earlier and not where they are looking now. Both are
    /// 200 damage; only the kill log and the scoreboard tell them apart.
    /// </para>
    /// </remarks>
    private static bool StabHitCharacter(
        CollisionWorld world,
        ref WorldState state,
        ProjectileHandle handle,
        int slot,
        ref TickEvents events)
    {
        ref var mask = ref state.Projectiles[handle.Index];
        ref var victim = ref state.Characters[slot];

        if (mask.OwnerSlot == slot
            || victim.Team == mask.Team
            || !Gml.Greater(victim.Hp, 0)
            || victim.Ubered)
        {
            return false;
        }

        if (!StabConnects(
                world, in state, in mask,
                Aabb.ForCharacter(victim.Box, victim.X, victim.Y),
                victim.X, victim.Y))
        {
            state.FreeProjectile(handle);
            return true;
        }

        DamageApi.DamageCharacter(ref events, slot, mask.OwnerSlot, ref victim, mask.HitDamage);

        if (victim.LastDamageDealer != mask.OwnerSlot && victim.LastDamageDealer != slot)
        {
            victim.SecondToLastDamageDealer = victim.LastDamageDealer;
            victim.AssistTimer = victim.FinishOffTimer;
        }

        victim.FinishOffTimer = GameConstants.AssistTime;
        victim.LastDamageDealer = mask.OwnerSlot;
        victim.LastDamageSource = victim.Facing == mask.XScale
            ? DamageSource.Backstab
            : DamageSource.Knife;

        // ⚠️ No `timeUnscathed = 0` and no dealFlicker — a knife is the one
        // character damage path with neither. At 200 damage the omission is
        // rarely observable, but a stab that a spawn bubble blocks leaves the
        // healing ramp climbing.
        mask.NumKills += 1;

        if (mask.NumKills < StabMaxKills)
            return false;

        state.FreeProjectile(handle);
        return true;
    }

    /// <summary>
    /// <c>StabMask.events/Collision with Sentry.xml</c>: the character event
    /// with the hp and uber clauses gone.
    /// </summary>
    private static bool StabHitSentry(
        CollisionWorld world,
        ref WorldState state,
        ProjectileHandle handle,
        int slot)
    {
        ref var mask = ref state.Projectiles[handle.Index];
        ref var sentry = ref state.Sentries[slot];

        if (sentry.Team == mask.Team)
            return false;

        if (!StabConnects(
                world, in state, in mask,
                Sentries.BodyBox(sentry), sentry.X, sentry.Y))
        {
            state.FreeProjectile(handle);
            return true;
        }

        Sentries.Damage(ref state, slot, mask.HitDamage, mask.OwnerSlot, mask.Source);

        mask.NumKills += 1;

        if (mask.NumKills < StabMaxKills)
            return false;

        state.FreeProjectile(handle);
        return true;
    }

    // ===== Quote's Blade and its Bubble =====================================

    /// <summary>
    /// <c>BladeB.events/Collision with Character.xml</c>. True if the blade was
    /// destroyed.
    /// </summary>
    /// <remarks>
    /// <b>Not the pellet's event with a bigger number in it.</b> Four things
    /// differ, and three of them favour the blade:
    /// <list type="bullet">
    /// <item>⚠️ <b>there is no <c>place_free</c> muzzle test</b>, so a blade
    /// that reached somebody through a wall still sticks in them;</item>
    /// <item>⚠️ <b>the guard is two clauses, not four</b> — not yourself and
    /// not a team-mate. <c>ubered</c> is checked <em>inside</em>, around the
    /// damage alone, and <c>hp &gt; 0</c> is not checked at all;</item>
    /// <item>⚠️ <b>so an ubered enemy still stops the blade and is still
    /// shoved</b>, taking no damage — the <c>motion_add</c>, the
    /// <c>dealFlicker</c> and the <c>instance_destroy</c> are all outside the
    /// uber guard;</item>
    /// <item>the shove is <b>0.4</b> of the blade's speed rather than a
    /// pellet's 0.03, which at speed 12 is nearly five units of velocity.</item>
    /// </list>
    /// </remarks>
    private static bool BladeHitCharacter(
        ref WorldState state, ProjectileHandle handle, int slot, ref TickEvents events)
    {
        ref var blade = ref state.Projectiles[handle.Index];
        ref var victim = ref state.Characters[slot];

        // if(other.id != ownerPlayer.object and other.team != team)
        if (blade.OwnerSlot == slot || victim.Team == blade.Team)
            return false;

        if (!victim.Ubered)
        {
            DamageApi.DamageCharacter(ref events, slot, blade.OwnerSlot, ref victim, blade.HitDamage);

            if (victim.LastDamageDealer != blade.OwnerSlot && victim.LastDamageDealer != slot)
            {
                victim.SecondToLastDamageDealer = victim.LastDamageDealer;
                victim.AssistTimer = victim.FinishOffTimer;
            }

            victim.FinishOffTimer = GameConstants.AssistTime;
            victim.LastDamageDealer = blade.OwnerSlot;
            victim.LastDamageSource = blade.Source;
        }

        // with(other) motion_add(other.direction, other.speed*0.4) — `other`
        // inside the `with` is the blade, so both arguments are the blade's.
        victim.Motion.MotionAdd(blade.Motion.Direction, blade.Motion.Speed * BladeKnockback);
        victim.DealFlicker();

        state.FreeProjectile(handle);
        return true;
    }

    /// <summary>
    /// <c>BladeB.events/End Step.xml</c>: every blade in flight drinks a unit
    /// of its owner's magazine.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>This is the Blade's entire ammo cost.</b> Neither click spends
    /// anything at the moment it is made; the magazine drains while a blade is
    /// out and is charged the difference up to <c>refireTime</c> when it lands
    /// (<see cref="WorldState.FreeProjectile"/>). A blade is younger than every
    /// character, so its End Step runs after all of theirs — which is why this
    /// is its own phase rather than a line inside the character loop.
    /// <para>
    /// The reference guards it on <c>instance_exists(owner.currentWeapon)</c>,
    /// and a blade with no owner cannot exist here: the Blade's Destroy event
    /// takes it along.
    /// </para>
    /// </remarks>
    public static void BladeEndStep(ref WorldState state)
    {
        for (var i = 0; i < state.LiveProjectileCount; i++)
        {
            ref var blade = ref state.Projectiles[state.LiveProjectileSlot(i)];

            if (blade.Kind != ProjectileKind.Blade)
                continue;

            if (blade.OwnerSlot == ProjectileState.NoOwner || !state.Alive[blade.OwnerSlot])
                continue;

            state.Weapons[blade.OwnerSlot].AmmoCount -= 1;
            blade.AmmoDrained += 1;
        }
    }
}
