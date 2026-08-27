using Gg2.Protocol;

namespace Gg2.Simulation;

/// <summary>
/// An engineer's sentry, from <c>Objects/InGameElements/Sentry.events</c>, and
/// the turret that grows out of it, from
/// <c>Objects/Weapons/SentryTurret.events</c>.
/// </summary>
/// <remarks>
/// <b>One per roster slot, like a character.</b> The reference hangs it off the
/// player (<c>player.sentry</c>, set by
/// <c>Scripts/Object Creation/buildSentry.gml</c>) and never lets a player have
/// two, so the roster slot is its identity — which is also how the wire names
/// it, as bit <c>0x02</c> of the player's subobject byte.
/// <para>
/// A sentry outlives its owner's character: it stays built while the engineer is
/// dead, which is why it sits beside <see cref="WorldState.Alive"/> rather than
/// inside <see cref="CharacterState"/>.
/// </para>
/// <para>
/// <b>The turret is not a <see cref="WeaponState"/>.</b> <c>SentryTurret</c> is
/// the one weapon object with no parent, so it inherits none of
/// <c>Weapon</c>'s events — including <c>User Event 12</c>, which is why a
/// sentry block carries no weapon block after it. Its fields live here, valid
/// only while <see cref="Built"/>: the reference creates the turret instance on
/// the tick construction finishes (<c>Sentry.events/Begin Step.xml</c>) and
/// destroys it with the sentry, so "the turret exists" and "the sentry is
/// built" are the same fact.
/// </para>
/// </remarks>
public struct SentryState
{
    /// <summary>Maximum hp once built. A sentry starts at a quarter of it.</summary>
    public const int MaxHp = 100;

    /// <summary>Hp a freshly placed sentry has, before any building.</summary>
    public const int InitialHp = 25;

    /// <summary>
    /// Hp added per tick while building.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The reference's ramp is a no-op and this constant is the whole
    /// story.</b> <c>Sentry.events/Begin Step.xml</c> follows the increment with
    /// <c>buildrate = min(buildrate, buildrate + 0.6/45)</c> under a comment
    /// claiming the rate climbs from 0.5 to 1 — but <c>min</c> of a value and a
    /// larger value is the value, so the rate never moves on its own. The only
    /// thing that ever changes it is damage: <c>damageSentry.gml</c> writes
    /// <see cref="DamagedBuildRate"/>, permanently.
    /// </remarks>
    public const double InitialBuildRate = 1.2;

    /// <summary>
    /// The build rate after the sentry has been damaged even once —
    /// <c>Scripts/Damage/damageSentry.gml</c>. It never recovers.
    /// </summary>
    public const double DamagedBuildRate = 0.6;

    /// <summary>Blast radius of its own destruction, used for the self-knockback.</summary>
    public const int BlastRadius = 65;

    public const int Knockback = 4;

    /// <summary>Ticks between the turret's shots. <c>SentryTurret.events/Create.xml</c>.</summary>
    public const int RefireTime = 5;

    /// <summary>Damage per turret shot. <c>SentryTurret.events/Create.xml</c>.</summary>
    public const double TurretHitDamage = 8;

    /// <summary>How far the turret can see, in <c>distance_to_object</c> terms.</summary>
    public const double TurretRange = 375;

    public Team Team;

    public double X;

    public double Y;

    /// <summary>
    /// <c>startDirection</c>: +1 or -1, the facing it was placed with. Signed on
    /// the wire, and also the sentry's <c>image_xscale</c> — so a left-facing
    /// sentry's reported bounding box is two pixels wider (docs/SIMULATION.md,
    /// "the sentry's collision box does not animate").
    /// </summary>
    public int StartDirection;

    public double Hp;

    /// <summary>
    /// Hp added per building tick. A field, not a constant: damage drops it to
    /// <see cref="DamagedBuildRate"/> forever.
    /// </summary>
    public double BuildRate;

    /// <summary>
    /// Whether construction has finished. On the wire as the top bit of the hp
    /// byte, and it decides whether a <c>vspeed</c> byte follows.
    /// </summary>
    public bool Built;

    /// <summary>
    /// The sentry's motion variables. Only <c>vspeed</c> is ever written — a
    /// sentry falls and nothing moves it sideways — but the falling code calls
    /// <c>really_move_contact_solid(direction, speed)</c>, which reads the two
    /// derived variables, so all four are kept.
    /// </summary>
    /// <remarks>
    /// <c>Sentry.events/Create.xml</c> starts <c>vspeed</c> at <c>0.001</c>
    /// rather than zero, together with an <c>oldVspeed</c> holding the same.
    /// That is not noise: the landing broadcast compares the two, and a pair of
    /// exact zeros would read as "already landed" on the first tick.
    /// </remarks>
    public Motion Motion;

    /// <summary>The previous tick's <c>vspeed</c>, for the landing broadcast.</summary>
    public double OldVSpeed;

    public int Kills;

    /// <summary>
    /// Whether the turret instance exists — the reference's
    /// <c>currentWeapon != -1</c>. It is created on the tick <see cref="Built"/>
    /// flips and dies with the sentry, so this trails <see cref="Built"/> by
    /// nothing; it is a separate flag only so the creation guard in
    /// <c>Sentries.BeginStep</c> runs exactly once.
    /// </summary>
    public bool HasTurret;

    /// <summary>Whether the turret can fire this tick. <c>SentryTurret</c>'s, not a weapon's.</summary>
    public bool ReadyToShoot;

    /// <summary>
    /// The turret's <c>alarm[0]</c>. Rests at <see cref="Timers.Inactive"/>;
    /// armed only while <see cref="Built"/>, because the alarm belongs to the
    /// turret instance and there is no turret before then.
    /// </summary>
    public int RefireTimer;

    /// <summary>
    /// The turret's aim, in GM8 degrees. <c>SentryTurret.events/User Event 0.xml</c>
    /// starts it along <see cref="StartDirection"/>.
    /// </summary>
    public double TurretDirection;

    /// <summary>
    /// The turret's <c>image_angle</c>, rewritten from
    /// <see cref="TurretDirection"/> at the top of every End Step. It is a
    /// collision fact, not a drawing one: the turret collides as its own
    /// <em>rotated</em> sprite, so this angle decides its bounding box.
    /// </summary>
    public double TurretImageAngle;

    /// <summary>The turret's <c>image_xscale</c>: +1 or -1, derived from the direction.</summary>
    public int TurretXScale;

    /// <summary>Whether the turret is mid-flip. It cannot fire while this is set.</summary>
    public bool Rotating;

    /// <summary>
    /// The flip animation's frame counter, advanced 0.3 per tick from
    /// <c>RotateStart</c> until it reaches <c>RotateStart + 4</c>.
    /// </summary>
    /// <remarks>
    /// Kept at the reference's own values — the red turret counts 0 to 4 and the
    /// blue one 5 to 9 (<c>User Event 0</c>'s <c>rotateoffset</c>) — rather than
    /// normalised to zero, because the flip fires on a fuzzy
    /// <c>rotateindex &gt;= rotateend</c> over an accumulated 0.3 and the two
    /// starting points accumulate different low bits.
    /// </remarks>
    public double RotateIndex;

    /// <summary>
    /// Whether the turret currently has no target. True at creation, and what
    /// makes the first shot after an idle spell late: acquiring a target while
    /// this is set costs a double-length refire delay
    /// (<c>SentryTurret.events/End Step.xml</c>, "if (noticedTarget == true)").
    /// </summary>
    public bool NoticedTarget;

    /// <summary>Roster slot of the last player to damage it, or 255.</summary>
    public byte LastDamageDealer;

    public DamageSource LastDamageSource;

    /// <summary>The flip counter's floor for this team: 0 red, 5 blue.</summary>
    public readonly double RotateStart => Team == Team.Blue ? 5 : 0;

    /// <summary>
    /// A sentry as <c>buildSentry</c> leaves it: placed, unbuilt, at a quarter
    /// health, still falling. No turret yet — <see cref="Built"/> is what says
    /// the turret fields mean anything.
    /// </summary>
    public static SentryState Build(Team team, double x, double y, int startDirection)
    {
        var sentry = new SentryState
        {
            Team = team,
            X = x,
            Y = y,
            StartDirection = startDirection,
            Hp = InitialHp,
            BuildRate = InitialBuildRate,
            OldVSpeed = 0.001,
            ReadyToShoot = false,
            RefireTimer = Timers.Inactive,
            LastDamageDealer = 255,
        };

        // A component write, like the reference's `vspeed = .001` — it re-derives
        // direction (270, straight down) and speed for really_move_contact_solid.
        sentry.Motion.VSpeed = 0.001;

        return sentry;
    }
}
