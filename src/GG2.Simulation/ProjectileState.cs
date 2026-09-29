using GG2.Protocol;

namespace GG2.Simulation;

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
    Stab,
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

    /// <summary>
    /// Mine only: the pyro whose airblast last reflected it, or
    /// <see cref="NoOwner"/>.
    /// </summary>
    /// <remarks>
    /// The reference's <c>reflector</c> is a <em>player</em> instance
    /// (<c>Mine.events/Create.xml</c> starts it <c>noone</c>), so it is a roster
    /// slot here and <see cref="WorldState.RemoveSlot"/> renumbers it. It is
    /// cleared by the mine's own Alarm 0 — the one projectile whose alarm 0 is
    /// not a destroy — thirty ticks after a reflected mine lands, and it
    /// redirects the blame when the mine's blast reaches its own owner.
    /// <b>Every creation site must write <see cref="NoOwner"/></b>: a
    /// defaulted 0 silently names slot 0, the server's own player.
    /// </remarks>
    public byte Reflector;

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
    /// <b>It is written twice, and only the second write is observable.</b>
    /// <c>createShot</c> sets it to the aim direction and the Scattergun then
    /// scatters <c>direction</c> alone — but
    /// <c>Scripts/Physics/System/move_all_bullets.gml</c> assigns
    /// <c>image_angle = direction</c> on every pass, including the pellet's
    /// first, and that pass runs in the Step stage while a pellet is created in
    /// Begin Step. So by the time any collision event can see the box, the aim
    /// angle has already been overwritten by the flight heading.
    /// <para>
    /// This repo asserted the opposite for a phase — that a blast's six pellets
    /// share one box angle — because the claim was read off
    /// <c>Objects/Projectiles/Shot.events/</c>, which has no Step event. The
    /// behaviour is not in the object; it is in the script the networking
    /// controller calls (<c>GameServer.events/Step.xml:16</c>).
    /// </para>
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

    /// <summary>
    /// <c>BladeB</c> only: how much of its owner's magazine this blade has
    /// already drunk.
    /// </summary>
    /// <remarks>
    /// <c>BladeB.events/End Step.xml</c> takes one unit a tick off
    /// <c>owner.currentWeapon.ammoCount</c> and counts it here;
    /// <c>BladeB.events/Destroy.xml</c> then charges the difference up to the
    /// weapon's <c>refireTime</c> in one go —
    /// <c>if (ammoDrained &lt; refireTime) ammoCount -= refireTime -
    /// ammoDrained</c>. So a blade that comes home early still costs five, and
    /// one that lives its full fifteen ticks costs fifteen.
    /// <para>
    /// <b>It cannot be derived from the remaining lifetime.</b> Both count
    /// the same ticks, but the alarm stage runs before the End Step, so a blade
    /// destroyed by a collision in between has spent an alarm it never drank —
    /// and the refund at its Destroy would be a unit short.
    /// </para>
    /// </remarks>
    public double AmmoDrained;

    /// <summary>
    /// <c>stickied</c>: this mine has hit a wall and stopped.
    /// </summary>
    /// <remarks>
    /// Set by the movement pass when a mine lands on an <c>Obstacle</c>, and by
    /// its Sentry and Generator collision events. It reaches the wire — the last
    /// byte of each mine's record in a demoman's weapon block
    /// (<c>Mine.events/User Event 12.xml</c>) — so it is not an internal detail.
    /// </remarks>
    public bool Stickied;

    /// <summary>
    /// <c>bubbled</c>: this mine has already been turned around by a bubble.
    /// </summary>
    /// <remarks>
    /// <c>Bubble.events/Collision with Mine.xml</c> sets it, and the same event
    /// refuses to reflect a mine that already has it — so one bubble screen
    /// bounces an incoming mine once and then lets it through, rather than
    /// pinning it in mid-air. Nothing ever clears it.
    /// <para>
    /// <b>Not the same as an airblast's reflection.</b> A pyro's blast reads
    /// <see cref="Reflector"/> and re-arms the mine's alarm; a bubble writes
    /// both this and the reflector and touches no timer.
    /// </para>
    /// </remarks>
    public bool Bubbled;

    /// <summary>
    /// <c>StabMask</c> only: how many things this knife has already connected
    /// with.
    /// </summary>
    /// <remarks>
    /// <b>A stab hits <em>two</em>.</b> <c>maxKills = 2</c>, and the counter
    /// is bumped by the character and sentry events alike — so one knife can
    /// take a medic and their patient, or a sentry and its engineer. It reaches
    /// 2 and no further, which is why a byte is exact where the reference keeps
    /// a real.
    /// </remarks>
    public byte NumKills;

    /// <summary>
    /// <c>splashThreshhold</c>: whether this blast skips its outer quarter.
    /// </summary>
    /// <remarks>
    /// <b>A stuck mine has no cutoff and a flying one does.</b>
    /// <c>move_all_bullets.gml</c> sets it from <c>!stickied</c> on every pass,
    /// and <c>Mine.events/User Event 2.xml</c> guards the quarter-radius
    /// <c>continue</c> with it — so a mine that has landed damages everything
    /// inside its 40-pixel radius, however faintly, while one still in the air
    /// behaves like a rocket.
    /// <para>
    /// Stored rather than read off <see cref="Stickied"/> at the moment of the
    /// blast, because the two are a tick apart: detonation is a Begin Step and
    /// the movement pass that refreshes this is in Step, so a blast sees what
    /// <em>last</em> tick's movement left behind.
    /// </para>
    /// </remarks>
    public bool SplashThreshold;

    /// <summary>
    /// <c>exploded</c>: this projectile's blast has already begun.
    /// </summary>
    /// <remarks>
    /// <b>Only a mine needs it, and it needs it because mines chain.</b> A
    /// mine's blast detonates every other mine in its radius, and those blasts
    /// scan back over a list that still contains the first one — GM8's
    /// <c>with</c> walks the instances that existed when the block was entered,
    /// and the <c>instance_destroy</c> is the last line of the event rather than
    /// the first. Without the flag a pair of adjacent mines would detonate each
    /// other forever.
    /// <para>
    /// A rocket sets the same flag in the reference and cannot reach it: every
    /// path that explodes one frees it in the same call.
    /// </para>
    /// </remarks>
    public bool Exploded;

    /// <summary>
    /// Whether <see cref="Projectiles.Move"/> has already run for this
    /// projectile.
    /// </summary>
    /// <remarks>
    /// <c>move_all_bullets.gml</c>'s <c>firststep</c>, inverted so that a zeroed
    /// pool slot is a projectile that has not moved yet — the reference spells
    /// it <c>if(!variable_local_exists("firststep")) firststep = true;</c>,
    /// which is the same default reached the GML way.
    /// <para>
    /// It exists because the <em>first</em> pass tests <c>place_free</c> at the
    /// muzzle as well as at the destination: a shot that is born inside a wall
    /// dies without ever dealing damage. That is what the fifteen-pixel forward
    /// nudge in the Scattergun's own comment ("to avoid immediate collision with
    /// a wall behind the character") is defending against.
    /// </para>
    /// </remarks>
    public bool HasMoved;

    // No originx/originy, and that is a deliberate omission rather than an
    // oversight. Shot.events/Create.xml sets both and nothing in the entire
    // reference ever reads them — checked across Objects/ and Scripts/ — so
    // they are not observable, and CLAUDE.md's rule ("compatible in observable
    // simulation, free everywhere else") says not to carry them. The cost of
    // carrying them would not be nominal: two doubles times a 512-slot pool is
    // eight kilobytes on every clone of the world, and docs/BOTAI.md item 1
    // makes clone size the one thing that cannot be retrofitted. Add them back
    // if a weapon turns out to need a projectile's launch point.

    // --- what only an exploding projectile carries ---------------------------
    //
    // A Rocket and a Mine have these; a Shot does not. They sit on the shared
    // struct rather than in a per-kind union because the pool is one flat array
    // and docs/BOTAI.md item 1 wants it cheap to memcpy, not clever. Everything
    // below is here because something in the reference *writes* it after
    // creation — a constant that nothing reassigns is a lookup instead
    // (Explosions.BlastRadiusFor).

    /// <summary>
    /// How hard the blast shoves. <c>Rocket.events/Create.xml</c> starts it at
    /// 8, alarm 1 caps it at 5 twenty ticks later and alarm 2 zeroes it ten
    /// ticks after that.
    /// </summary>
    /// <remarks>
    /// Per-instance rather than per-kind because the pyro's airblast rewrites
    /// it: <c>Flamethrower.events/User Event 2.xml</c> reflects a rocket by
    /// setting <c>knockback = 8</c> and re-arming both alarms to 40 and 80 — so
    /// a reflected rocket is not merely a rocket of a certain age, and deriving
    /// this from a tick counter would be wrong the moment the pyro lands.
    /// </remarks>
    public double Knockback;

    /// <summary>
    /// <c>distanceToTravel</c>: how far from its owner a rocket may get before
    /// it starts to fade. 800, less 100 for each team-mate it has flown through.
    /// </summary>
    public double DistanceToTravel;

    /// <summary>
    /// The owner's position, as of the last pass on which the owner still
    /// existed. The rocket's travel budget is measured from here, not from the
    /// muzzle.
    /// </summary>
    /// <remarks>
    /// <c>move_all_bullets.gml</c>'s <c>with(Rocket)</c> refreshes it every pass
    /// from <c>owner.x</c>/<c>owner.y</c> while <c>instance_exists(owner)</c> —
    /// so the budget is a leash that follows a live shooter around and freezes
    /// where a dead one fell.
    /// </remarks>
    public double LastKnownX;

    /// <inheritdoc cref="LastKnownX"/>
    public double LastKnownY;

    /// <summary>
    /// <c>playersPassed</c>: the roster slots this rocket has already flown
    /// through, one bit each.
    /// </summary>
    /// <remarks>
    /// The reference keeps a <c>ds_list</c> and asks
    /// <c>ds_list_find_index(...) == -1</c>; a bitmask is the same set membership
    /// with no allocation, and <see cref="WorldState.MaxSlots"/> is 64, which is
    /// exactly a <c>ulong</c>.
    /// <para>
    /// It names roster slots, so <see cref="WorldState.RemoveSlot"/> has to
    /// renumber it like every other stored index — CLAUDE.md's rule. A stale bit
    /// would let a rocket fly through a stranger for free some seconds after an
    /// unrelated disconnect.
    /// </para>
    /// </remarks>
    public ulong PlayersPassed;

    /// <summary>
    /// A burning projectile's <c>enemiesHit</c>: the roster slots it has already
    /// burnt, one bit each.
    /// </summary>
    /// <remarks>
    /// <c>BurningProjectile.events/Create.xml</c> makes it a <c>ds_list</c> and
    /// the collision event both tests and appends to it, so a flame burns each
    /// character at most once however many ticks it spends inside them.
    /// <para>
    /// <b>The reference stores character ids and this stores roster slots</b>,
    /// which are not the same thing across a death and a respawn — the same
    /// collapse <see cref="OwnerSlot"/> makes. A flame lives fifteen ticks, so
    /// the window in which it could matter is far shorter than a respawn.
    /// </para>
    /// <para>
    /// Renumbered by <see cref="WorldState.RemoveSlot"/> along with
    /// <see cref="PlayersPassed"/>. A separate field rather than sharing that
    /// one: they are different sets with different rules, and a rocket that
    /// reused a flame's bits would charge itself the pass-through cost twice.
    /// </para>
    /// </remarks>
    public ulong EnemiesHit;

    /// <summary><c>alarm[1]</c>: caps <see cref="Knockback"/> at 5.</summary>
    public int KnockbackCapTimer;

    /// <summary><c>alarm[2]</c>: zeroes <see cref="Knockback"/>.</summary>
    public int KnockbackEndTimer;

    /// <summary>
    /// <c>alarm[3]</c>: destroys the projectile without exploding it, eight
    /// ticks after the travel budget ran out.
    /// </summary>
    public int FadeTimer;

    /// <summary>
    /// How many passes this projectile has spent fading. At ten it is gone.
    /// </summary>
    /// <remarks>
    /// <b>The reference spells this as a drawing field, and it is not
    /// decoration.</b> <c>move_all_bullets.gml</c>'s rocket block subtracts 0.1
    /// from <c>image_alpha</c> on every fading pass and then ends its particle
    /// section with <c>if (image_alpha &gt; 0) ... else instance_destroy()</c>.
    /// The guard around it is <c>global.run_virtual_ticks</c>, which
    /// docs/SIMULATION.md settles as always true at 30 Hz — so this is how a
    /// rocket that outruns its leash actually dies. <c>alarm[3]</c> is re-armed
    /// to 8 on every pass the leash is still over-extended, so it only fires for
    /// a rocket whose owner has come back towards it.
    /// <para>
    /// Counted rather than stored as the alpha itself, which is exact and not an
    /// approximation. The value only ever walks 1, 0.9, 0.8 … so the sequence is
    /// fixed, and <b>the tenth subtraction leaves 1.4e-16</b> — under GML's own
    /// 1e-13 comparison fuzz, so <c>&gt; 0</c> is already false there and the
    /// rocket dies on the tenth fading pass rather than the eleventh. A counter
    /// says that in four bytes instead of eight and cannot be left at a
    /// defaulted zero that means "already invisible".
    /// </para>
    /// <para>
    /// A <c>Shot</c> writes <c>image_alpha</c> too, from its remaining lifetime,
    /// and nothing reads it back: pellets have no alpha-triggered destroy. That
    /// half is not transcribed.
    /// </para>
    /// </remarks>
    public int FadeTicks;

    /// <summary>
    /// Set by the Rocketlauncher when the muzzle is already the far side of
    /// something solid, so the rocket explodes where it was made.
    /// </summary>
    /// <remarks>
    /// The reference's comment: "Delay explosion to make same-frame
    /// rocketjumping work reliably". A rocket fired at a wall you are standing
    /// against must still explode on <em>this</em> tick's movement pass rather
    /// than being deleted, or the jump it was fired for does not happen.
    /// </remarks>
    public bool ExplodeImmediately;

    /// <summary>
    /// The travel budget has run out: the rocket is on its way to disappearing
    /// and will no longer explode.
    /// </summary>
    /// <remarks>
    /// <b>A faded rocket that reaches a wall is destroyed silently.</b>
    /// <c>move_all_bullets.gml</c> branches on it — <c>if (!fade) event_user(5)
    /// else instance_destroy()</c> — so a rocket at the end of its range does no
    /// damage at all, even to somebody standing in front of the wall it hits.
    /// </remarks>
    public bool Fade;

    // No `exploded`. The reference's flag guards User Event 5 against being
    // entered twice before the instance_destroy at its end takes effect, which
    // GM8 makes possible because a collision event and the movement pass can
    // both fire it. Here the two are separate phases and every path that
    // explodes frees the pool slot in the same call, so a second entry cannot
    // be constructed — carrying the flag would cost 512 bytes of clone to
    // encode "unreachable".

    /// <summary>
    /// <c>image_xscale</c>: 1 for everything except a <c>StabMask</c>, which
    /// mirrors itself to face the way it was aimed.
    /// </summary>
    /// <remarks>
    /// <c>StabMask.events/Step.xml</c>:
    /// <c>if (direction &lt; 90 or direction &gt; 270) image_xscale = 1; else -1;</c>
    /// — recomputed every Step, but a stab's <c>direction</c> is written once
    /// and its speed is zero, so deriving it is exact and costs no field.
    /// <para>
    /// <b>It is not only the box.</b> The knife compares
    /// <c>sign(other.image_xscale) == sign(image_xscale)</c> to decide whether
    /// a hit is a backstab, so this is what makes the difference between 200
    /// damage credited as a stab and 200 credited as a backstab.
    /// </para>
    /// </remarks>
    public readonly int XScale =>
        Kind != ProjectileKind.Stab || Motion.Direction < 90 || Motion.Direction > 270 ? 1 : -1;

    /// <summary>The box GM8 reports for this projectile, rotated by its angle.</summary>
    public readonly Aabb Box
    {
        get
        {
            var sprite = ProjectileSprites.For(Kind);
            return Aabb.ForRotatedSprite(
                sprite.Bbox, sprite.OriginX, sprite.OriginY, X, Y, ImageAngle, XScale);
        }
    }
}
