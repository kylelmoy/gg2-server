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

    /// <summary>
    /// <c>image_angle</c>, which decides the bounding box and therefore who this
    /// can hit.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Not the same as <see cref="Motion"/>'s direction, and the gap is
    /// deliberate on the reference's part.</b> <c>createShot</c> sets both to
    /// the aim direction, and then
    /// <c>Objects/Weapons/Scattergun.events/User Event 3.xml</c> scatters
    /// <c>direction</c> by <c>random(15)-7.5</c> and leaves <c>image_angle</c>
    /// alone. Nothing writes it again — <c>Shot</c> has no Step event — so all
    /// six pellets of one blast carry the <em>same</em> box angle while flying
    /// along six different headings.
    /// </remarks>
    public double ImageAngle;

    /// <summary>Damage one hit deals, from the projectile's own Create event.</summary>
    /// <remarks>
    /// On the projectile rather than looked up from its kind because the
    /// reference puts it there and weapons overwrite it: a sentry's shot and a
    /// scattergun's are both <c>Shot</c> with different <c>hitDamage</c>.
    /// </remarks>
    public double HitDamage;

    /// <summary>
    /// <c>alarm[0]</c>: ticks until the projectile expires and is destroyed.
    /// </summary>
    /// <remarks>
    /// <c>Shot.events/Alarm 0.xml</c> is a bare <c>instance_destroy</c>, and
    /// <c>Shot.events/Create.xml</c> starts it at <c>lifetime = 40</c> — which
    /// the Scattergun then overwrites with a shorter, movement-compensated one.
    /// <para>
    /// <b>An integer, because GM8's alarms are.</b> The Scattergun's replacement
    /// is a real (35 times a factor in <c>[0.5, 1]</c>) and a counter that
    /// stepped down from 30.45 would never land on zero and the pellet would
    /// never expire. See <see cref="Timers.SetAlarm"/> for the rounding and for
    /// what about it is still unmeasured.
    /// </para>
    /// </remarks>
    public int Lifetime;

    /// <summary>
    /// <c>perseverant</c>: whether this pellet passes through bubbles.
    /// </summary>
    /// <remarks>
    /// <c>choose(0, 0, 1)</c> in <c>Shot.events/Create.xml</c> — a one in three
    /// chance, drawn <em>inside</em> the seeded window a <c>WEAPON_FIRE</c>
    /// opens. It is stored even though nothing reads it until Quote's bubbles
    /// land, because the draw itself is not optional: it costs the shared RNG
    /// stream exactly one advance per pellet, and skipping it would desync every
    /// later pellet's spread from the client's.
    /// </remarks>
    public bool Perseverant;

    // No originx/originy, and that is a deliberate omission rather than an
    // oversight. Shot.events/Create.xml sets both and nothing in the entire
    // reference ever reads them — checked across Objects/ and Scripts/ — so
    // they are not observable, and CLAUDE.md's rule ("compatible in observable
    // simulation, free everywhere else") says not to carry them. The cost of
    // carrying them would not be nominal: two doubles times a 512-slot pool is
    // eight kilobytes on every clone of the world, and docs/BOTAI.md item 1
    // makes clone size the one thing that cannot be retrofitted. Add them back
    // if a weapon turns out to need a projectile's launch point.

    /// <summary>The box GM8 reports for this projectile, rotated by its angle.</summary>
    public readonly Aabb Box
    {
        get
        {
            var sprite = ProjectileSprites.For(Kind);
            return Aabb.ForRotatedSprite(sprite.Bbox, sprite.OriginX, sprite.OriginY, X, Y, ImageAngle);
        }
    }
}
