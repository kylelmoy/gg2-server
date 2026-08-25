using Gg2.Protocol;

namespace Gg2.Simulation;

/// <summary>
/// One character's weapon, as state rather than as an instance.
/// </summary>
/// <remarks>
/// The reference gives every character a weapon <em>object</em>
/// (<c>Character.events/Create.xml</c>'s <c>currentWeapon</c>) and puts its
/// mutable fields on that instance. A weapon has no independent existence — it
/// is created with its owner and destroyed with it — so here it is a struct
/// living beside the character in the same roster slot. That keeps
/// <see cref="WorldState"/> a flat value type, which docs/BOTAI.md item 1 says
/// is the one thing that cannot be retrofitted.
/// <para>
/// <b>Counters, not alarms.</b> CLAUDE.md's rule puts alarms on the list of
/// Game Maker showing through, so each of the reference's <c>alarm[n]</c> that
/// survives is a named tick counter here. GM8 alarms count down by exactly one
/// per frame and rest at <c>-1</c> (docs/SIMULATION.md), so
/// <see cref="Inactive"/> is <c>-1</c> and a counter fires on reaching zero.
/// </para>
/// <para>
/// <b>Only what is observable.</b> <c>justShot</c>, <c>alarm[6]</c> and the
/// sprite fields drive the recoil and reload <em>animations</em>, which the
/// client runs for itself off nothing the server sends; they are not here.
/// Weapon-specific state that no wire field and no other character can see —
/// the blade's bubble count, the flamethrower's blast and flare timers, the
/// revolver's stab cooldown — arrives with the weapon that needs it in stage 3.
/// </para>
/// </remarks>
public struct WeaponState
{
    /// <summary>A counter that is not running. GM8's resting alarm value.</summary>
    public const int Inactive = -1;

    /// <summary>
    /// <c>uberCharge</c> at full. The wire carries <c>uberCharge*255/2000</c> in
    /// a medic's class-specific byte, so this constant is part of the format.
    /// </summary>
    public const double MaxUberCharge = 2000;

    /// <summary>The roster slot a medic is beaming, or <see cref="NoTarget"/>.</summary>
    /// <remarks>
    /// <b>255, not 0.</b> Zero is roster slot 0, which is the server's own
    /// reserved player and exists even in a headless server, so a zero here
    /// tells every client the medic is healing it.
    /// </remarks>
    public const byte NoTarget = 255;

    /// <summary>
    /// The weapon instance's own position — <b>where a projectile is created</b>.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Not the character's position, and not even this tick's.</b>
    /// <c>Character.events/End Step.xml:64-65</c> is the only thing that writes
    /// it, as <c>round(x)</c> and <c>round(y)</c> — GML's round, half to even —
    /// and a weapon fires in <em>Begin Step</em>. So a shot leaves from the
    /// character's position at the end of the <em>previous</em> tick, rounded to
    /// a whole pixel, which is up to half a pixel and one tick of travel away
    /// from where the character is when it pulls the trigger.
    /// <para>
    /// The reference gets this for free by keeping the weapon a separate
    /// instance that trails its owner. Reproducing it is not optional: the
    /// muzzle is the origin of every pellet's fifteen-pixel nudge and of every
    /// hitscan, so half a pixel here is half a pixel of aim.
    /// </para>
    /// <para>
    /// It is set to the character's unrounded position at creation
    /// (<c>Character.events/Create.xml:199</c> passes <c>x, y</c> to
    /// <c>instance_create</c>) and only becomes a rounded trailing copy after
    /// the first End Step.
    /// </para>
    /// </remarks>
    public double X;

    /// <summary>The weapon instance's own position. See <see cref="X"/>.</summary>
    public double Y;

    /// <summary>Rounds left in the magazine. On the wire in every character block.</summary>
    /// <remarks>
    /// A <c>double</c> because the minigun and the flamethrower drain and refill
    /// it fractionally, and because GML has one numeric type.
    /// </remarks>
    public double AmmoCount;

    /// <summary>
    /// <c>readyToShoot</c>. On the wire in every weapon block except a medic's.
    /// </summary>
    /// <remarks>
    /// <c>Weapon.events/Create.xml</c> starts it <b>false</b> with
    /// <see cref="RefireTimer"/> already running, so a character that has just
    /// spawned is not yet able to fire.
    /// </remarks>
    public bool ReadyToShoot;

    /// <summary>
    /// <c>alarm[0]</c>: ticks until <see cref="ReadyToShoot"/> comes back. On
    /// the wire in every weapon block except a medic's.
    /// </summary>
    public int RefireTimer;

    /// <summary>
    /// <c>alarm[5]</c>: ticks until the next round is put back in the magazine.
    /// </summary>
    public int ReloadTimer;

    /// <summary>Medic: the roster slot being healed, or <see cref="NoTarget"/>.</summary>
    public byte HealTarget;

    /// <summary>Medic: <c>uberCharge</c>, in <c>[0, <see cref="MaxUberCharge"/>]</c>.</summary>
    public double UberCharge;

    /// <summary>Medic: whether the uber is being spent right now.</summary>
    public bool Ubering;

    /// <summary>Medic: whether a full charge is banked and waiting.</summary>
    public bool UberReady;

    /// <summary>
    /// Sniper: the rifle's <c>t</c>, the charge accumulated while scoped. On the
    /// wire in a sniper's class-specific byte, unscaled.
    /// </summary>
    public double Charge;

    /// <summary>
    /// Demoman: <c>lobbed</c>, the number of mines out. On the wire, and it
    /// decides how many mine blocks follow the weapon block.
    /// </summary>
    public int MinesLobbed;

    /// <summary>
    /// The weapon a character spawns holding.
    /// </summary>
    /// <remarks>
    /// <c>Weapon.events/Create.xml</c> in full: <c>readyToShoot = false</c> and
    /// <c>alarm[0] = refireTime</c>, plus each weapon's own
    /// <c>ammoCount = maxAmmo</c>. Everything else starts at its zero, which is
    /// what <c>default</c> already gives.
    /// </remarks>
    public static WeaponState Spawn(PlayerClass playerClass, double x = 0, double y = 0)
    {
        var stats = WeaponStats.For(playerClass);

        return new WeaponState
        {
            AmmoCount = stats.MaxAmmo,
            ReadyToShoot = false,
            RefireTimer = stats.RefireTime,
            ReloadTimer = Inactive,
            HealTarget = NoTarget,

            // instance_create(x, y, weapons[0]) — the character's own
            // coordinates, unrounded. End Step replaces them with round(x) from
            // this tick onward.
            X = x,
            Y = y,
        };
    }
}
