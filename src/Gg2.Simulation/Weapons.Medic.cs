using Gg2.Protocol;

namespace Gg2.Simulation;

public static partial class Weapons
{
    /// <summary>
    /// <c>Objects/Weapons/Medigun.events/User Event 2.xml</c>: a needle, or the
    /// uber.
    /// </summary>
    /// <remarks>
    /// <b>One key, two actions, and the needle wins.</b> The uber is the
    /// <c>else</c> arm — a medic who is ready to shoot, has no patient, is not
    /// already ubering and has a round left fires that round <em>instead</em> of
    /// popping their charge, so the way to activate an uber is to be beaming
    /// somebody (or out of ammo).
    /// <para>
    /// ⚠️ <b>The uber arm reads <c>keyState</c> and not the
    /// down-or-pressed union</b> that got us into this method:
    /// <c>uberReady &amp;&amp; (owner.keyState &amp; $10)</c>. So a left-click
    /// that arrives and is released inside a single tick opens the heal beam but
    /// will <em>not</em> pop an uber; the button has to still be down.
    /// </para>
    /// <para>
    /// ⚠️ <b>The needle's spread comes off the global stream, not a broadcast
    /// seed.</b> There is no <c>sendEventFireWeapon</c> anywhere in this event —
    /// like the Minigun and the Blade, the Medigun is exempt from
    /// docs/SIMULATION.md's "a weapon puts the seed on the wire" contract, and
    /// every client draws its own needle speeds. Which value comes out is
    /// therefore free; that a draw <em>happens</em> is not, because it advances
    /// the same stream the next broadcast seed is taken from.
    /// </para>
    /// </remarks>
    private static void FireNeedleOrUber(ref WorldState state, int slot, ref TickEvents events)
    {
        ref var character = ref state.Characters[slot];
        ref var weapon = ref state.Weapons[slot];
        var stats = WeaponStats.For(WeaponKind.Medigun);

        if (weapon.ReadyToShoot
            && weapon.HealTarget == WeaponState.NoTarget
            && !weapon.Ubering
            && Gml.Greater(weapon.AmmoCount, 0))
        {
            weapon.AmmoCount -= 1;

            var needle = CreateNeedle(
                weapon.X,
                weapon.Y + NeedleMuzzleOffsetY,
                character.AimDirection,
                NeedleSpeed + state.Random.Next(NeedleSpeedJitter),
                slot,
                character.Team);

            // `with(shot) hspeed += owner.hspeed;` — a component write, which
            // re-derives direction and speed and snaps them (see Motion). The
            // needle is not nudged forward the way a pellet is, so a medic with
            // their back to a wall shoots into it.
            needle.Motion.HSpeed += character.HSpeed;

            state.SpawnProjectile(needle);

            weapon.ReadyToShoot = false;
            weapon.RefireTimer = stats.RefireTime;
            weapon.ReloadTimer = stats.ReloadBuffer + stats.ReloadTime;
        }
        else if (weapon.UberReady && (character.KeyState & PlayerKeys.Attack) != 0)
        {
            // sendEventUber, then doEventUber — which sets both of these on the
            // player's Medigun. The drain itself is the weapon's own Begin Step,
            // so nothing is spent on the tick the uber starts.
            weapon.Ubering = true;
            weapon.UberReady = false;
            events.Ubered[slot] = true;

            // doEventUber.gml:15-18, inside the same `with(currentWeapon)`.
            Scoring.Award(ref state, slot, StatIndex.Invulns, 1);
            Scoring.Award(ref state, slot, StatIndex.Points, 1);
        }
    }

    /// <summary>
    /// <c>createShot</c> plus <c>Objects/Projectiles/Needle.events/Create.xml</c>.
    /// </summary>
    /// <remarks>
    /// A <c>Shot</c> with two differences that matter and one that does not: it
    /// falls faster (see <see cref="Projectiles"/>), it deals half the damage,
    /// and its Create event draws <b>nothing</b> — there is no
    /// <c>perseverant</c> on a needle, so this cannot go through
    /// <see cref="CreateShot"/> without inventing a random draw the reference
    /// does not make.
    /// </remarks>
    private static ProjectileState CreateNeedle(
        double x,
        double y,
        double direction,
        double speed,
        int ownerSlot,
        Team team)
    {
        var needle = new ProjectileState
        {
            Kind = ProjectileKind.Needle,
            X = x,
            Y = y,
            OwnerSlot = (byte)ownerSlot,
            Reflector = ProjectileState.NoOwner,
            Team = team,
            Source = DamageSource.Needlegun,
            HitDamage = NeedleHitDamage,
            Lifetime = NeedleLifetime,
        };

        // direction, then image_angle, then speed — createShot's own order, and
        // the reason a fresh instance's velocity is not left pointing along zero
        // degrees. See CreateShot.
        needle.Motion.Direction = direction;
        needle.ImageAngle = direction;
        needle.Motion.Speed = speed;

        return needle;
    }
}
