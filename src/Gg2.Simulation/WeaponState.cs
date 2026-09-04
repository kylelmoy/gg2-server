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
    /// <b>Not the character's position, and not even this tick's.</b>
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

    /// <summary>
    /// Pyro: whether the tank is topping itself up.
    /// </summary>
    /// <remarks>
    /// <c>isRefilling</c>. The Flamethrower does not reload a magazine — it
    /// refills continuously from its own Begin Step — and this is the gate.
    /// Firing clears it and <c>alarm[5]</c> puts it back seven ticks later, so a
    /// pyro who keeps the trigger down never refills at all.
    /// </remarks>
    public bool IsRefilling;

    /// <summary>
    /// Pyro: <c>readyToBlast</c> — whether the airblast is off cooldown.
    /// </summary>
    /// <remarks>
    /// <c>Flamethrower.events/Create.xml</c> starts it <b>false</b> with
    /// <see cref="BlastTimer"/> already running, like <see cref="ReadyToShoot"/>:
    /// a freshly spawned pyro cannot blast for 40 ticks.
    /// </remarks>
    public bool ReadyToBlast;

    /// <summary>
    /// Pyro: <c>alarm[1]</c>, ticks until <see cref="ReadyToBlast"/> comes back.
    /// </summary>
    public int BlastTimer;

    /// <summary>Pyro: <c>readyToFlare</c> — whether the flare is off cooldown.</summary>
    public bool ReadyToFlare;

    /// <summary>
    /// Pyro: <c>alarm[2]</c>, ticks until <see cref="ReadyToFlare"/> comes back.
    /// </summary>
    public int FlareTimer;

    /// <summary>Medic: the roster slot being healed, or <see cref="NoTarget"/>.</summary>
    public byte HealTarget;

    /// <summary>Medic: <c>uberCharge</c>, in <c>[0, <see cref="MaxUberCharge"/>]</c>.</summary>
    public double UberCharge;

    /// <summary>Medic: whether the uber is being spent right now.</summary>
    public bool Ubering;

    /// <summary>Medic: whether a full charge is banked and waiting.</summary>
    public bool UberReady;

    /// <summary>
    /// Medic: whether the beam reached its patient during the tick now running.
    /// </summary>
    /// <remarks>
    /// <b>Intra-tick only, and it has to be state rather than a return value.</b>
    /// <c>Medigun.events/User Event 1.xml</c> sets it in Begin Step and
    /// <c>Medigun.events/Step.xml</c> reads and clears it two stages later, so
    /// the two ends are in different phases of <see cref="MatchSimulation"/>.
    /// It is always false at a tick boundary, which is why nothing serialises it
    /// and why a state copied between ticks does not carry it.
    /// </remarks>
    public bool HealedThisStep;

    /// <summary>
    /// Medic: <c>hphealed</c>, hit points banked towards the next score point.
    /// </summary>
    /// <remarks>
    /// One point per <see cref="Gg2.Protocol.GameConstants.HpPerPoint"/>, and
    /// the overshoot carries — <c>Medigun.events/User Event 1.xml:107</c> is
    /// <c>hphealed -= HP_PER_POINT</c> and not <c>= 0</c>.
    /// <para>
    /// <b>It belongs to the weapon, so it dies with the medic's character.</b>
    /// The stat it feeds belongs to the player and survives; the progress
    /// towards the next point does not. Beaming 799 hp and then dying throws
    /// those 799 away.
    /// </para>
    /// </remarks>
    public double HpHealed;

    /// <summary>
    /// Sniper: the rifle's <c>t</c>, the charge accumulated while scoped. On the
    /// wire in a sniper's class-specific byte, unscaled.
    /// </summary>
    public double Charge;

    /// <summary>
    /// Sniper: the rifle's <c>hitDamage</c>, recomputed every tick from
    /// <see cref="Charge"/> and the owner's scope.
    /// </summary>
    /// <remarks>
    /// <b>The only weapon whose damage is state rather than a constant.</b>
    /// Every other weapon's <c>hitDamage</c> is set once in its Create event and
    /// read straight into the projectile it makes; the Rifle's
    /// <c>Begin Step</c> rewrites it on every tick, which is what lets the same
    /// weapon deal 35, 45 or 75 without the shot knowing why.
    /// </remarks>
    public double HitDamage;

    /// <summary>
    /// The weapon a character spawns holding.
    /// </summary>
    /// <remarks>
    /// <c>Weapon.events/Create.xml</c> in full: <c>readyToShoot = false</c> and
    /// <c>alarm[0] = refireTime</c>, plus each weapon's own
    /// <c>ammoCount = maxAmmo</c>. Everything else starts at its zero, which is
    /// what <c>default</c> already gives.
    /// </remarks>
    /// <summary>
    /// <c>lobbed</c>: how many of this demoman's mines are in the world.
    /// </summary>
    /// <remarks>
    /// A count rather than a derived number, because the reference keeps it as
    /// one and puts it <b>on the wire</b> — <c>Minegun.events/User Event
    /// 12.xml</c> writes it as the first byte of a demoman's weapon block, ahead
    /// of one seven-byte record per mine. So it is not free to recompute it from
    /// the projectile pool and hope the two agree.
    /// <para>
    /// It gates firing (<c>lobbed &lt; maxMines</c>, in User Event 1 and so
    /// <em>before</em> the seed is drawn), and it is decremented by
    /// <c>Mine.events/Destroy.xml</c> — on <em>any</em> destruction, whether the
    /// mine exploded, hit a gate or died with its owner.
    /// </para>
    /// </remarks>
    public int Lobbed;

    /// <summary>
    /// Quote: <c>replenishing</c>, whether the magazine is climbing back.
    /// </summary>
    /// <remarks>
    /// The Blade is the only weapon whose refill can be switched <em>off</em>.
    /// Throwing a blade clears it (<c>Blade.events/User Event 2.xml</c>) and
    /// <c>Blade.events/Alarm 5.xml</c> puts it back <c>bladeLife</c> ticks
    /// later — the longest a blade could possibly live, so the tank is frozen
    /// for as long as one might still be out. The refill itself is in the
    /// weapon's own Begin Step, two units a tick, clamped to
    /// <c>[0, maxAmmo]</c> at both ends.
    /// </remarks>
    public bool Replenishing;

    /// <summary>Quote: <c>bubbleCount</c>, how many of this Blade's bubbles are alive.</summary>
    /// <remarks>
    /// Gates the left click at <c>maxBubbles = 25</c> and is decremented by
    /// <c>Bubble.events/Destroy.xml</c> — the same shape as
    /// <see cref="Lobbed"/>, except that it never reaches the wire: a Blade's
    /// weapon block is two bytes and carries only the ammo.
    /// </remarks>
    public int BubbleCount;

    /// <summary>Quote: <c>bladesOut</c>, which is 0 or 1.</summary>
    /// <remarks>
    /// <c>maxBlades = 1</c>, so this gates the right click to one blade in
    /// flight at a time. <c>BladeB.events/Destroy.xml</c> decrements it.
    /// </remarks>
    public int BladesOut;

    /// <summary>
    /// Spy: <c>readyToStab</c>, whether the knife is off cooldown.
    /// </summary>
    /// <remarks>
    /// Here ahead of the Revolver because <b>a bubble reads it</b>:
    /// <c>Bubble.events/Collision with Character.xml:16-18</c> exits outright
    /// for a cloaked spy who could stab, so bubbles pass through one and never
    /// give them away. <c>Revolver.events/Create.xml:23</c> starts it true,
    /// which is why <see cref="Spawn"/> does — every other class has no such
    /// variable at all, and no other class can set <c>cloak</c>, so the guard
    /// is unreachable for them.
    /// </remarks>
    public bool ReadyToStab;

    /// <summary>
    /// Spy: <c>alarm[1]</c>, the wind-up. When it fires the knife's hitbox is
    /// created.
    /// </summary>
    /// <remarks>
    /// <b>The knife does not land when the button is pressed.</b>
    /// <c>Revolver.events/User Event 1.xml</c> starts the animation and arms
    /// this for <c>StabreloadTime = 32</c> ticks; <c>Alarm 1</c> is what
    /// actually makes the <c>StabMask</c>. So there is a full second between a
    /// spy committing to a stab and anybody taking damage from it.
    /// <para>
    /// This is GM8's <c>alarm[1]</c>, the same alarm the Flamethrower uses for
    /// <see cref="BlastTimer"/> — a separate field because the two weapons do
    /// different things with it and the alarm stage has to know which.
    /// </para>
    /// </remarks>
    public int StabTimer;

    /// <summary>Spy: <c>alarm[2]</c>, which puts <see cref="ReadyToStab"/> back.</summary>
    /// <remarks>
    /// <b>Armed by <c>Alarm 1</c>, not by the stab</b> — and by a
    /// drag-and-drop <c>action_set_alarm(18, 2)</c> that a grep for GML code
    /// does not see. So the whole cooldown is <b>32 + 18 = 50</b> ticks, and it
    /// gates the <em>cloak toggle</em> as well as the next stab.
    /// </remarks>
    public int StabReadyTimer;

    /// <summary>Spy: <c>stabdirection</c>, the aim frozen at the moment of the stab.</summary>
    /// <remarks>
    /// Read fifty ticks later by <c>Alarm 1</c> to point the hitbox, so a spy
    /// who turns around mid-animation still stabs where they committed.
    /// </remarks>
    public double StabDirection;

    public static WeaponState Spawn(PlayerClass playerClass, double x = 0, double y = 0)
    {
        var stats = WeaponStats.For(playerClass);
        var isFlamethrower = WeaponStats.KindFor(playerClass) == WeaponKind.Flamethrower;

        return new WeaponState
        {
            AmmoCount = stats.MaxAmmo,
            ReadyToShoot = false,
            RefireTimer = stats.RefireTime,
            ReloadTimer = Inactive,
            HealTarget = NoTarget,

            // Flamethrower.events/Create.xml arms both cooldowns at birth —
            // alarm[1] = blastReloadTime, alarm[2] = flareReloadTime — with the
            // ready flags false, so the right-click does nothing for the first
            // 40 ticks of a pyro's life. Everybody else's counters rest.
            ReadyToBlast = false,
            BlastTimer = isFlamethrower ? Weapons.BlastReloadTime : Inactive,
            ReadyToFlare = false,
            FlareTimer = isFlamethrower ? Weapons.FlareReloadTime : Inactive,

            // Blade.events/Create.xml:6 and Revolver.events/Create.xml:23. Set
            // for every class rather than keyed on one, because each is a
            // variable only its own weapon has and only its own weapon reads —
            // and both are `true` at birth, so a blanket write is the same
            // simulation with no branch.
            Replenishing = true,
            ReadyToStab = true,

            // instance_create(x, y, weapons[0]) — the character's own
            // coordinates, unrounded. End Step replaces them with round(x) from
            // this tick onward.
            X = x,
            Y = y,
        };
    }
}
