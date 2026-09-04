using Gg2.Protocol;

namespace Gg2.Simulation;

public static partial class Weapons
{
    /// <summary>
    /// <c>Objects/Weapons/Blade.events/User Event 1.xml</c>: one bubble.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The left click of the Blade costs nothing at all.</b> No ammo
    /// clause, no ammo spend — the magazine is entirely the <em>right</em>
    /// click's, and a Quote can hold the trigger down for the whole round. The
    /// only limit is <c>bubbleCount &lt; maxBubbles</c>, which is 25.
    /// <para>
    /// ⚠️ <b>Unseeded.</b> The event calls <c>randomize()</c> and broadcasts no
    /// <c>WEAPON_FIRE</c>, like the Minigun's and the Flamethrower's — so this
    /// is the third weapon exempt from docs/SIMULATION.md's "a weapon puts its
    /// seed on the wire", and the third whose fixture window nothing can raise.
    /// It draws nothing at all, in fact: a <c>Bubble</c>'s Create event has no
    /// <c>choose()</c>, so unlike a <c>Shot</c> it does not even cost the shared
    /// stream one advance.
    /// </para>
    /// <para>
    /// The bubble inherits its owner's <em>whole</em> velocity — <c>with(shot)
    /// motion_add(owner.direction, owner.speed)</c>, where <c>owner</c> inside
    /// the <c>with</c> is the shot's own owner rather than the weapon's — the
    /// same shape as a flame's.
    /// </para>
    /// </remarks>
    private static void FireBubble(ref WorldState state, int slot)
    {
        ref var character = ref state.Characters[slot];
        ref var weapon = ref state.Weapons[slot];

        if (!weapon.ReadyToShoot || weapon.BubbleCount >= BladeMaxBubbles)
            return;

        var aim = character.AimDirection;

        var bubble = CreateBubble(
            weapon.X + Gml.LengthDirX(BubbleMuzzleOffset, aim),
            weapon.Y + Gml.LengthDirY(BubbleMuzzleOffset, aim),
            aim,
            slot,
            character.Team);

        bubble.Motion.MotionAdd(character.Motion.Direction, character.Motion.Speed);

        state.SpawnProjectile(bubble);

        weapon.ReadyToShoot = false;
        weapon.RefireTimer = BubbleRefireTime;
        weapon.BubbleCount += 1;
    }

    /// <summary>
    /// <c>Scripts/Object Creation/createShot.gml</c>, and the projectile's own
    /// Create event with it.
    /// </summary>
    /// <remarks>
    /// Returned by value rather than added to the pool here, because the
    /// reference's caller keeps editing the instance after
    /// <c>createShot</c> returns — the Scattergun writes <c>hspeed</c>,
    /// <c>speed</c>, <c>direction</c>, <c>x</c>, <c>y</c> and <c>alarm[0]</c>
    /// before the pellet ever moves. Putting it in the pool first and mutating
    /// through a handle would work equally well and read worse.
    /// <para>
    /// ⚠️ <b><c>image_angle</c> is set here and never again.</b> The Scattergun
    /// then scatters <c>direction</c> and leaves the angle alone, so a pellet's
    /// bounding box is aligned with the <em>aim</em> rather than with its own
    /// flight. See <see cref="ProjectileState.ImageAngle"/>.
    /// </para>
    /// </remarks>
    /// <summary>
    /// <c>createShot(..., Bubble, ...)</c> plus
    /// <c>Bubble.events/Create.xml</c>.
    /// </summary>
    /// <remarks>
    /// Its own builder rather than <see cref="CreateShot"/>'s because a
    /// <c>Bubble</c> is <b>not</b> a <c>Shot</c> — <c>Bubble.xml</c> declares no
    /// parent at all — and, crucially, its Create event has no
    /// <c>choose(0,0,1)</c>. Reusing the pellet's builder would cost the shared
    /// RNG stream one advance per bubble that the reference never spends.
    /// </remarks>
    private static ProjectileState CreateBubble(
        double x,
        double y,
        double direction,
        int ownerSlot,
        Team team)
    {
        var bubble = new ProjectileState
        {
            Kind = ProjectileKind.Bubble,
            X = x,
            Y = y,
            OwnerSlot = (byte)ownerSlot,
            Reflector = ProjectileState.NoOwner,
            Team = team,
            Source = DamageSource.Bubble,
            HitDamage = Projectiles.BubbleHitDamage,
            Lifetime = Projectiles.BubbleLifetime,
        };

        bubble.Motion.Direction = direction;
        bubble.ImageAngle = direction;
        bubble.Motion.Speed = BubbleSpeed;

        return bubble;
    }
}
