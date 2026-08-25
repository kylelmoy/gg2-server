using Gg2.Protocol;

namespace Gg2.Simulation;

/// <summary>
/// An engineer's sentry, from <c>Objects/InGameElements/Sentry.events</c>.
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
/// sentry block carries no weapon block after it. Its <c>readyToShoot</c> and
/// refire counter are fields here.
/// </para>
/// </remarks>
public struct SentryState
{
    /// <summary>Maximum hp once built. A sentry starts at a quarter of it.</summary>
    public const int MaxHp = 100;

    /// <summary>Hp a freshly placed sentry has, before any building.</summary>
    public const int InitialHp = 25;

    /// <summary>Hp added per tick while an engineer hits it with a wrench.</summary>
    public const double BuildRate = 1.2;

    /// <summary>Blast radius of its own destruction, used for the self-knockback.</summary>
    public const int BlastRadius = 65;

    public const int Knockback = 4;

    /// <summary>Ticks between the turret's shots.</summary>
    public const int RefireTime = 5;

    public Team Team;

    public double X;

    public double Y;

    /// <summary>
    /// <c>startDirection</c>: +1 or -1, the facing it was placed with. Signed on
    /// the wire, and also the sentry's <c>image_xscale</c>.
    /// </summary>
    public int StartDirection;

    public double Hp;

    /// <summary>
    /// Whether construction has finished. On the wire as the top bit of the hp
    /// byte, and it decides whether a <c>vspeed</c> byte follows.
    /// </summary>
    public bool Built;

    /// <summary>Whether it has stopped falling. An unbuilt sentry drops to the floor.</summary>
    public bool Landed;

    /// <summary>
    /// Fall speed. On the wire only while unbuilt, which is the only time it is
    /// non-trivial.
    /// </summary>
    /// <remarks>
    /// <c>Sentry.events/Create.xml</c> starts it at <c>0.001</c> rather than
    /// zero, together with an <c>oldVspeed</c> holding the same. That is not
    /// noise: the landing test compares the two, and a pair of exact zeros would
    /// read as "already landed" on the first tick.
    /// </remarks>
    public double VSpeed;

    /// <summary>The previous tick's <see cref="VSpeed"/>, for the landing test.</summary>
    public double OldVSpeed;

    public int Kills;

    /// <summary>Whether the turret can fire this tick.</summary>
    public bool ReadyToShoot;

    /// <summary>Ticks until <see cref="ReadyToShoot"/> comes back.</summary>
    public int RefireTimer;

    /// <summary>Roster slot of the last player to damage it, or 255.</summary>
    public byte LastDamageDealer;

    public DamageSource LastDamageSource;

    /// <summary>
    /// A sentry as <c>buildSentry</c> leaves it: placed, unbuilt, at a quarter
    /// health, still falling.
    /// </summary>
    public static SentryState Build(Team team, double x, double y, int startDirection) => new()
    {
        Team = team,
        X = x,
        Y = y,
        StartDirection = startDirection,
        Hp = InitialHp,
        VSpeed = 0.001,
        OldVSpeed = 0.001,
        ReadyToShoot = false,
        RefireTimer = RefireTime,
        LastDamageDealer = 255,
    };
}
