using Gg2.Protocol;

namespace Gg2.Simulation;

/// <summary>
/// Which <c>Objects/Projectiles/*</c> a pooled projectile is.
/// </summary>
/// <remarks>
/// <see cref="None"/> means the pool slot is empty, so it must stay the default
/// — a zeroed <see cref="WorldState"/> is a world with no projectiles in it.
/// </remarks>
public enum ProjectileKind : byte
{
    None = 0,
    Shot,
    Rocket,
    Mine,
    Flame,
    Needle,
    Flare,
    Blade,
    Bubble,
}

/// <summary>
/// A stable reference to a pooled projectile.
/// </summary>
/// <remarks>
/// <b>Index plus generation, because an index alone is a use-after-free
/// waiting to happen.</b> Pool slots are reused, so a raw index that outlives
/// the projectile it named silently starts naming a different one — the same
/// class of bug as CLAUDE.md's roster-index warning, and just as intermittent.
/// Every read goes through <see cref="WorldState.TryGetProjectile"/>, which
/// compares the generation and refuses a stale handle.
/// <para>
/// A handle is two integers and no reference, so it can be stored inside the
/// state (a mine remembers what reflected it) without breaking docs/BOTAI.md
/// item 1.
/// </para>
/// </remarks>
public readonly record struct ProjectileHandle(int Index, uint Generation)
{
    /// <summary>The reference's <c>noone</c>: names nothing, and never will.</summary>
    /// <remarks>
    /// Generation 0 is never issued — <see cref="WorldState"/> starts every slot
    /// at generation 1 — so the default of a field of this type is already the
    /// null handle, and a zeroed world has no dangling references in it.
    /// </remarks>
    public static ProjectileHandle None => default;

    public bool IsNone => Generation == 0;
}

/// <summary>
/// One projectile in flight.
/// </summary>
/// <remarks>
/// <b>The fields every projectile has, and no more.</b> These are what
/// <c>Scripts/Object Creation/createShot.gml</c> sets on whatever it is handed —
/// direction, speed, owner, team and damage source — plus the position and
/// GM8's motion pair. Per-kind state (a rocket's <c>knockback</c> and
/// <c>playersPassed</c>, a mine's <c>stickied</c>, a flame's afterburn
/// contribution) lands with the weapon that fires it, in stage 3.
/// <para>
/// <b>Owners are roster slots, not object references.</b> The reference carries
/// <c>owner</c> (the character instance) and <c>ownerPlayer</c> (the player
/// instance) as two separate pointers; a roster slot is both, because
/// <see cref="WorldState"/> indexes characters by roster slot. <c>ownerPlayer</c>
/// is the one that survives its character dying, and that is what a kill
/// attribution needs, so a projectile outliving its owner still names the right
/// player.
/// </para>
/// </remarks>
public struct ProjectileState
{
    /// <summary>Nobody: the reference's <c>noone</c> owner, and 255 on the wire.</summary>
    public const byte NoOwner = 255;

    public ProjectileKind Kind;

    /// <summary>The owner's team at the moment of firing, as <c>createShot</c> copies it.</summary>
    /// <remarks>
    /// Copied rather than looked up, because the owner can change team — or
    /// leave — while the projectile is still in the air, and the reference's
    /// <c>team = owner.team</c> froze the value at creation.
    /// </remarks>
    public Team Team;

    /// <summary>The roster slot that fired it, or <see cref="NoOwner"/>.</summary>
    public byte OwnerSlot;

    /// <summary>What the kill log calls this, from <c>createShot</c>'s <c>damageSource</c>.</summary>
    public DamageSource Source;

    public double X;

    public double Y;

    /// <summary>
    /// GM8's <c>hspeed</c>/<c>vspeed</c>/<c>direction</c>/<c>speed</c>, all four
    /// of them.
    /// </summary>
    /// <remarks>
    /// A projectile writes <c>direction</c> and <c>speed</c> directly, unlike a
    /// character, so it needs the full model rather than the two components —
    /// see <see cref="Motion"/> for why that is not the same thing.
    /// </remarks>
    public Motion Motion;
}
