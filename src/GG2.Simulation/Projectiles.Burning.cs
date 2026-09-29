using System.Numerics;
using GG2.Protocol;

namespace GG2.Simulation;

public static partial class Projectiles
{
    /// <summary>
    /// <c>move_all_bullets.gml</c>'s <c>with(BurningProjectile)</c> block. True
    /// if the flame was destroyed.
    /// </summary>
    /// <remarks>
    /// <b>A flame substeps like a mine and dies like a pellet.</b> It walks the
    /// same six-pixel hops (<see cref="SubstepWalk"/>) and the first one that
    /// lands in something solid ends it — there is no sticking and no explosion,
    /// it simply stops existing.
    /// <para>
    /// ⚠️ <b>there is no <c>speed != 0</c> guard</b>, where the mine block
    /// has one. A flame with no speed still enters the loop; the first iteration
    /// finds a remaining distance of zero, takes a zero-length hop and finishes.
    /// </para>
    /// <para>
    /// And no <c>image_angle = direction</c>, unlike the Shot and Rocket blocks:
    /// a burning projectile keeps the angle <c>createShot</c> gave it, so its box
    /// stays aligned with the aim rather than with its own heading. It is a 3x3
    /// box centred on its origin, so this is nearly unobservable — but it is what
    /// the script says.
    /// </para>
    /// </remarks>
    private static bool MoveBurning(CollisionWorld world, ref WorldState state, ProjectileHandle handle)
    {
        ref var flame = ref state.Projectiles[handle.Index];

        // if(object_index == Flame) vspeed += 0.15 — the Flare, the other child
        // of BurningProjectile, flies flat.
        if (flame.Kind == ProjectileKind.Flame)
            flame.Motion.VSpeed += FlameGravity;

        var colliding = !flame.HasMoved && !ProjectileMask.PlaceFree(world, in flame, state.BulletFilter);

        SubstepWalk(world, ref flame, BurningSubstep, state.BulletFilter, ref colliding);
        flame.HasMoved = true;

        if (!colliding)
            return false;

        state.FreeProjectile(handle);
        return true;
    }

    /// <summary>
    /// <c>BurningProjectile.events/Collision with Character.xml</c>. True if the
    /// flame was destroyed.
    /// </summary>
    /// <remarks>
    /// <b>Not the pellet's event with different numbers.</b> Three things are its
    /// own: it has no <c>place_free</c> muzzle test, so a flame that reached
    /// somebody through a wall still burns them; it remembers who it has already
    /// burnt and refuses to burn them twice; and it sets them alight, which is
    /// <see cref="Afterburn.Ignite"/>.
    /// <para>
    /// ⚠️ <b>the penetration count is tested before the victim is
    /// recorded.</b> <c>if (ds_list_size(enemiesHit) &gt;= penetrateCap and
    /// !perseverant) instance_destroy(); else ds_list_add(...)</c> with a cap of
    /// 1 — so the list is empty on the first hit and the flame survives it, and
    /// it is the <em>second</em> victim that kills the flame. A flame burns one
    /// person and dies on the next.
    /// </para>
    /// </remarks>
    private static bool BurningHitCharacter(
        ref WorldState state, ProjectileHandle handle, int slot, ref TickEvents events)
    {
        ref var flame = ref state.Projectiles[handle.Index];
        ref var victim = ref state.Characters[slot];

        // The same four-clause guard the pellet has, and note what is missing
        // above it: no gunSetSolids/place_free, so unlike a Shot a flame does not
        // die for having arrived inside a wall.
        if (flame.OwnerSlot == slot
            || victim.Team == flame.Team
            || victim.Ubered
            || !Gml.Greater(victim.Hp, 0))
            return false;

        // `if (ds_list_find_index(enemiesHit, other.id) != -1) exit;` — already
        // burnt by this flame, so nothing happens at all. Not a destroy: the
        // flame carries on through them.
        var bit = 1UL << slot;
        if ((flame.EnemiesHit & bit) != 0)
            return false;

        DamageApi.Deal(ref events, slot, flame.OwnerSlot, ref victim, flame.HitDamage);
        victim.TimeUnscathed = 0;

        if (victim.LastDamageDealer != flame.OwnerSlot && victim.LastDamageDealer != slot)
        {
            victim.SecondToLastDamageDealer = victim.LastDamageDealer;
            victim.AssistTimer = victim.FinishOffTimer;
        }

        victim.FinishOffTimer = GameConstants.AssistTime;
        victim.LastDamageDealer = flame.OwnerSlot;
        victim.LastDamageSource = flame.Source;

        Afterburn.Ignite(ref victim, flame, flame.OwnerSlot);

        victim.DealFlicker();

        // The count, then the record. See the remarks: with a cap of 1 this
        // destroys on the second victim, never the first.
        if (BitOperations.PopCount(flame.EnemiesHit) >= FlameStats.PenetrateCap(flame.Kind) && !flame.Perseverant)
        {
            state.FreeProjectile(handle);
            return true;
        }

        flame.EnemiesHit |= bit;
        return false;
    }
}
