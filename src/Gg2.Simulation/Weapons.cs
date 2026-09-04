using Gg2.Protocol;

namespace Gg2.Simulation;

/// <summary>
/// Pulling the trigger: the path from a held attack key to projectiles in the
/// air.
/// </summary>
/// <remarks>
/// Four of the reference's events collapse into this file, because in GML they
/// are four objects passing control to each other and here they are one
/// function:
/// <list type="number">
/// <item>
/// <c>Character.events/Begin Step.xml:96-100</c> — the attack key, and
/// <c>with(currentWeapon) event_user(1)</c>.
/// </item>
/// <item>
/// <c>Objects/Weapons/Scattergun.events/User Event 1.xml</c> — the host checks
/// <c>readyToShoot</c> and the magazine, draws a seed, broadcasts it and then
/// fires locally with it.
/// </item>
/// <item>
/// <c>Scripts/Events/doEventFireWeapon.gml</c> — <c>random_set_seed(seed)</c>
/// around <c>event_user(3)</c>, restoring the outer seed afterwards.
/// </item>
/// <item>
/// <c>Objects/Weapons/Scattergun.events/User Event 3.xml</c> — the six pellets.
/// </item>
/// </list>
/// <para>
/// <b>Where this runs is load-bearing.</b> It is a Begin Step, and it happens
/// <em>before</em> the acceleration loop in the same event — so
/// <c>shot.hspeed += owner.hspeed</c> adds the speed the character carried out
/// of the previous tick, not the one it is about to have. It is also before
/// <see cref="Timers.Advance"/>, so the refire counter this tick tests is the
/// one last tick left behind.
/// </para>
/// <para>
/// <b>The seeded window is a contract.</b> Every draw between
/// <c>random_set_seed</c> and the end of <c>event_user(3)</c> is re-run by every
/// client from the seed on the wire, so the <em>number</em> of draws matters as
/// much as their use: a pellet costs exactly three advances — the
/// <c>choose(0, 0, 1)</c> in <c>Shot.events/Create.xml</c>, the speed jitter and
/// the direction jitter — and dropping the first because nothing reads
/// <c>perseverant</c> yet would desync every later pellet.
/// </para>
/// </remarks>
public static partial class Weapons
{
    /// <summary>Pellets one blast of a scattergun throws.</summary>
    private const int ScattergunPellets = 6;

    /// <summary>A scattergun pellet's muzzle speed, before the jitter.</summary>
    private const double ScattergunPelletSpeed = 13;

    /// <summary>
    /// How far forward a pellet is placed before it first moves.
    /// </summary>
    /// <remarks>
    /// The reference's own comment: "Move shot forward to avoid immediate
    /// collision with a wall behind the character". Applied along the pellet's
    /// <em>scattered</em> direction, with <c>lengthdir</c> — which snaps, so a
    /// pellet fired near an axis lands on an exact offset. See
    /// <see cref="Gml.LengthDirX"/>.
    /// </remarks>
    private const double ScattergunNudge = 15;

    /// <summary>
    /// How far ahead of the weapon a rocket is created.
    /// <c>Rocketlauncher.events/User Event 3.xml</c>.
    /// </summary>
    /// <remarks>
    /// Five pixels further than a pellet's nudge, and it is applied along the
    /// <em>aim</em> rather than along a scattered heading, because a rocket does
    /// not scatter. The twenty pixels between the weapon and the spawn point are
    /// what <c>collision_line_bulletblocking</c> is asked about.
    /// </remarks>
    private const double RocketMuzzleOffset = 20;

    /// <summary>A rocket's launch speed, before the first tick's drag.</summary>
    private const double RocketSpeed = 13;

    /// <summary>Damage a direct rocket hit deals. <c>Rocket.events/Create.xml</c>.</summary>
    private const double RocketHitDamage = 25;

    /// <summary>How hard a fresh rocket's blast shoves.</summary>
    private const double RocketKnockback = 8;

    /// <summary>How far from its owner a rocket may get before it fades.</summary>
    private const double RocketRange = 800;

    /// <summary><c>alarm[1]</c>: the tick the knockback is capped at 5.</summary>
    private const int RocketKnockbackCapTicks = 20;

    /// <summary><c>alarm[2]</c>: the tick the knockback becomes 0.</summary>
    private const int RocketKnockbackEndTicks = 30;

    /// <summary>
    /// How many of a demoman's mines may be in the world at once.
    /// </summary>
    /// <remarks>
    /// The guard is in <c>Minegun.events/User Event 1.xml</c>, alongside the
    /// ready-and-loaded test and so <b>before the seed is drawn</b> — a demoman
    /// at eight mines does not fire, does not broadcast, and does not advance
    /// the shared stream.
    /// </remarks>
    private const int MinegunMaxMines = 8;

    /// <summary>How far ahead of the weapon a mine is created.</summary>
    private const double MineMuzzleOffset = 10;

    /// <summary>A mine's launch speed.</summary>
    private const double MineSpeed = 12;

    /// <summary>How hard a mine's blast shoves. <c>Mine.events/Create.xml</c>.</summary>
    private const double MineKnockback = 10;

    /// <summary>Damage a scattergun pellet deals. <c>Shot.events/Create.xml</c>.</summary>
    private const double ShotHitDamage = 8;

    /// <summary>
    /// The width of a scattergun pellet's direction jitter: <c>random(15) - 7.5</c>.
    /// </summary>
    private const double ScattergunSpread = 15;

    /// <summary>Pellets one blast of a shotgun throws.</summary>
    private const int ShotgunPellets = 5;

    /// <summary>
    /// Damage one shotgun pellet deals.
    /// </summary>
    /// <remarks>
    /// Seven, where a scattergun's is eight — and note where it comes from:
    /// <c>Shot.events/Create.xml</c> sets 8 and
    /// <c>Shotgun.events/User Event 3.xml</c> overwrites it per pellet, exactly
    /// as the Medigun overwrites a needle's. The projectile's own Create event is
    /// the default, not the answer.
    /// </remarks>
    private const double ShotgunHitDamage = 7;

    /// <summary>
    /// The width of a shotgun pellet's direction jitter: <c>random(11) - 5.5</c>.
    /// </summary>
    /// <remarks>
    /// <b>Tighter than a scattergun's <c>random(15) - 7.5</c>.</b> Five
    /// pellets in an eleven-degree cone against six in a fifteen-degree one, so
    /// an engineer's shotgun is the longer-ranged of the two despite dealing less
    /// per pellet. Both are inside the seeded window and both are contract: every
    /// client re-derives the spread from the broadcast seed.
    /// </remarks>
    private const double ShotgunSpread = 11;

    /// <summary>Damage one needle deals. <c>Needle.events/Create.xml</c>.</summary>
    private const double NeedleHitDamage = 4;

    /// <summary>
    /// How far along the aim a minigun bullet starts, from the weapon.
    /// </summary>
    /// <remarks>
    /// <c>Minigun.events/User Event 1.xml</c>: <c>x + lengthdir_x(20, aim)</c>
    /// and <c>y + 12 + lengthdir_y(20, aim)</c>.
    /// </remarks>
    private const double MinigunMuzzleOffset = 20;

    /// <summary>
    /// <b>A flat twelve pixels down, outside the <c>lengthdir</c>.</b> The
    /// muzzle is not on the aim ray at all: it is twenty pixels along the aim
    /// and then twelve below that, whichever way the heavy is looking. Every
    /// other weapon in the game offsets symmetrically or not at all.
    /// </remarks>
    private const double MinigunMuzzleDrop = 12;

    /// <summary>
    /// The cone: <c>random(14) - 7</c>, so ±7 degrees.
    /// </summary>
    private const double MinigunSpread = 14;

    /// <summary>Bullet speed: <c>12 + random(1)</c>.</summary>
    private const double MinigunShotSpeed = 12;

    /// <inheritdoc cref="MinigunShotSpeed"/>
    private const double MinigunSpeedJitter = 1;

    /// <summary>
    /// What one shot costs, and the floor it needs to fire.
    /// </summary>
    /// <remarks>
    /// <b>They differ.</b> The guard is <c>ammoCount &gt;= 2</c> and the
    /// charge is <c>ammoCount -= 3</c>, so a minigun on exactly 2 fires and
    /// lands on -1 — which the weapon's own Begin Step then clamps to 0. It is
    /// the only weapon in the game whose shot costs more than it checks for.
    /// </remarks>
    private const double MinigunAmmoCost = 3;

    /// <inheritdoc cref="MinigunAmmoCost"/>
    private const double MinigunMinAmmo = 2;

    /// <summary>
    /// <c>shot.alarm[0] = 30</c>: a minigun bullet lives thirty ticks, not the
    /// <c>Shot</c>'s own forty.
    /// </summary>
    private const int MinigunShotLifetime = 30;

    /// <summary>
    /// What the reload buffer is multiplied by when the shot left the tank
    /// nearly empty: <c>if(ammoCount &lt; 3) reloadBufferFactor = 2.5</c>.
    /// </summary>
    /// <remarks>
    /// Read <em>after</em> the ammo is spent, so it is about the tank the shot
    /// left behind rather than the one it found. The reference keeps a
    /// commented-out cosine spline beside it; the shipped code is this
    /// two-valued step.
    /// </remarks>
    private const double MinigunEmptyReloadFactor = 2.5;

    /// <summary>
    /// The ceiling the weapon's Begin Step pushes <c>alarm[5]</c> up to while
    /// the trigger is held.
    /// </summary>
    /// <remarks>
    /// A bare literal in the reference, and it is exactly
    /// <c>reloadBuffer * MinigunEmptyReloadFactor</c> — 10 × 2.5. Written as the
    /// literal it is, because nothing in the reference derives it.
    /// </remarks>
    private const int MinigunReloadExtendCap = 25;

    /// <summary>What an unscoped rifle deals, whatever the charge.</summary>
    private const double RifleUnscopedDamage = 35;

    /// <summary>What a scoped rifle deals with no charge at all.</summary>
    private const double RifleBaseDamage = 45;

    /// <summary>What a fully charged scoped rifle deals.</summary>
    private const double RifleMaxDamage = 75;

    /// <summary>Ticks of scoping to reach <see cref="RifleMaxDamage"/>.</summary>
    /// <remarks>
    /// Public because the bot layer's sniper rule reads it: a scoped bot waits
    /// for a charge proportional to the range before it fires, because the
    /// Rifle's damage <em>is</em> its range. A second copy of the number would be
    /// a bot that fires at the wrong moment for ever after this one changed.
    /// </remarks>
    public const double RifleChargeTime = 105;

    /// <summary>
    /// Extra ticks a scoped shot adds to the refire, and to the shell eject.
    /// </summary>
    /// <remarks>
    /// <c>alarm[0] = reloadTime + 20*owner.zoomed</c> — GM8 multiplies by a
    /// boolean, so a scoped shot takes 60 ticks to come back and an unscoped one
    /// 40.
    /// </remarks>
    private const int RifleScopedRefirePenalty = 20;

    /// <summary>
    /// How far a rifle shot reaches at full damage:
    /// <c>len = 1500*(((hitDamage*100)/maxDamage)/100)</c>.
    /// </summary>
    /// <remarks>
    /// <b>The range is proportional to the damage</b>, so an unscoped shot
    /// carries 700 pixels and a fully charged one the whole 1,500. The
    /// ×100-then-÷100 round trip is the reference's own and is transcribed as
    /// written: it is not a no-op in floating point, and the length feeds a
    /// binary search whose halvings would drift with it.
    /// </remarks>
    private const double RifleRange = 1500;

    /// <summary>
    /// How far below the Medigun a needle is created:
    /// <c>createShot(x, y + yoffset + 1, ...)</c>.
    /// </summary>
    /// <remarks>
    /// <b>Four pixels, and three of them come from a sprite origin.</b>
    /// <c>Medigun.events/Create.xml</c> sets <c>yoffset = 0</c> and then calls
    /// <c>event_inherited()</c>, and <c>Weapon.events/Create.xml</c> adds
    /// <c>sprite_yoffset</c> — GM8's read-only origin of the current sprite,
    /// which at that moment is still the object's default <c>MedigunS</c>
    /// (origin 8, 3; <c>Sprites/Weapons/MedigunS.xml</c>). So <c>yoffset</c> is
    /// 3 and the needle leaves one pixel below that.
    /// <para>
    /// The golden-weapon sprites cannot change it: <c>sprite_index</c> is only
    /// reassigned <em>after</em> <c>event_inherited()</c> has run.
    /// </para>
    /// </remarks>
    private const double NeedleMuzzleOffsetY = 4;

    /// <summary>A needle's base speed, before the jitter.</summary>
    private const double NeedleSpeed = 7;

    /// <summary>The width of a needle's speed jitter: <c>7 + random(3)</c>.</summary>
    private const double NeedleSpeedJitter = 3;

    /// <summary>A <c>Needle</c>'s lifetime. <c>Needle.events/Create.xml</c>.</summary>
    private const int NeedleLifetime = 40;

    /// <summary>A <c>Shot</c>'s default lifetime, before a weapon overrides it.</summary>
    private const int ShotLifetime = 40;

    /// <summary>How far ahead of the Flamethrower a flame is created.</summary>
    private const double FlameMuzzleOffset = 25;

    /// <summary>A flame's base speed, before the jitter.</summary>
    private const double FlameSpeed = 6.5;

    /// <summary>The width of a flame's speed jitter: <c>6.5 + random(3.5)</c>.</summary>
    private const double FlameSpeedJitter = 3.5;

    /// <summary>
    /// What one flame costs, and the threshold it is tested against.
    /// </summary>
    /// <remarks>
    /// Both are 1.8, and the guard is <c>ammoCount &gt;= 1.8</c> — so the tank
    /// can be left holding a fraction of a flame's worth. See
    /// <see cref="FireFlamethrower"/> for the "prevent sputtering" line that
    /// deals with it.
    /// </remarks>
    private const double FlameAmmoCost = 1.8;

    // ----- the spy's Revolver and knife (Revolver.events/Create.xml) ---------

    /// <summary><c>yoffset</c>: the muzzle sits <c>yoffset + 1</c> above the weapon.</summary>
    private const double RevolverMuzzleDrop = -6 + 1;

    /// <summary>A revolver bullet's muzzle speed, from <c>createShot(..., 21)</c>.</summary>
    private const double RevolverShotSpeed = 21;

    /// <summary><c>shot.hitDamage = 28</c>, the hardest single bullet in the game.</summary>
    public const double RevolverHitDamage = 28;

    /// <summary>
    /// <c>StabreloadTime</c>: <c>alarm[1]</c>, the wind-up before the knife's
    /// hitbox exists.
    /// </summary>
    public const int StabWindUp = 32;

    /// <summary>
    /// <c>alarm[2]</c>, armed by <c>Alarm 1</c> — the rest of the cooldown
    /// before another stab, or a cloak toggle, is allowed.
    /// </summary>
    /// <remarks>
    /// <b>Set by a drag-and-drop <c>action_set_alarm(18, 2)</c></b>, which
    /// the usual grep for GML code inside an event XML does not see at all. So
    /// the whole cooldown is <see cref="StabWindUp"/> + this = <b>50</b> ticks.
    /// </remarks>
    public const int StabCooldown = 18;

    /// <summary><c>hitDamage = 200</c>, which is more than any class has.</summary>
    public const double StabHitDamage = 200;

    /// <summary>How long a <c>StabMask</c> exists: <c>alarm[0] = 6</c>.</summary>
    public const int StabMaskLifetime = 6;

    /// <summary>
    /// How long a stab holds its spy still: the <c>StabAnim</c>'s whole life.
    /// </summary>
    /// <remarks>
    /// <b>Derived from two drawing variables, and it is the longest
    /// self-imposed freeze in the game — over a second and a half.</b>
    /// <c>StabAnim.events/</c>: <c>alarm[0] = 10</c> then
    /// <c>image_speed = 1</c>, so <c>image_index</c> climbs one a tick from
    /// tick 10 and reaches the <c>&gt;= 32</c> that sets <c>done</c> at tick 42
    /// (<c>SpyRedBackstabTorsoS</c> has exactly 33 frames, so it never wraps
    /// first). From there an <c>alpha</c> of 1 falls 0.1 a tick and the
    /// instance destroys itself when it is no longer <c>&gt; 0</c> — which is
    /// the <b>tenth</b> subtraction, not the eleventh, because 1 - 10x0.1 is
    /// 1.4e-16 and GML's <c>&gt;</c> is fuzzy to 1e-13. The rocket's fade, in
    /// another costume. 42 + 10 = 52.
    /// <para>
    /// <b>The one tick that was uncertain is measured, and 52 stands.</b>
    /// <b>GM8 advances <c>image_index</c> after the whole Step stage</b>,
    /// so a Step reading it sees what the previous frame left and the
    /// <c>&gt;= 32</c> threshold lands on tick 42. See
    /// <c>docs/SIMULATION.md</c>, "Settled: GM8 advances <c>image_index</c> after
    /// the whole Step stage", and the spy section of
    /// <c>docs/TRAPS-SIMULATION.md</c>.
    /// </para>
    /// </remarks>
    public const int StabFreezeTicks = 52;

    /// <summary>
    /// What fraction of the knife's damage a generator takes: <c>0.36</c>.
    /// </summary>
    /// <remarks>
    /// 72 rather than 200, and the only place in the game a damage figure is
    /// scaled for the generator. Its event also has <b>no line-of-sight
    /// test</b>, and the reference says why: "map designers might put walls
    /// behind the generator".
    /// </remarks>
    public const double StabGeneratorFactor = 0.36;

    // ----- Quote's Blade (Blade.events/Create.xml) ---------------------------

    /// <summary><c>bubbleRefireTime</c>: <c>alarm[0]</c> after a bubble, shorter than the blade's.</summary>
    private const int BubbleRefireTime = 4;

    /// <summary><c>maxBubbles</c>: how many of one Blade's bubbles may be alive at once.</summary>
    public const int BladeMaxBubbles = 25;

    /// <summary><c>maxBlades</c>: one blade in the air at a time.</summary>
    public const int BladeMaxBlades = 1;

    /// <summary>
    /// <c>bladeLife</c>: a blade's <c>alarm[0]</c>, and its weapon's
    /// <c>alarm[5]</c>.
    /// </summary>
    /// <remarks>
    /// The reference's own comment on the second use: "this isn't for when the
    /// blade actually dies, but the longest it could live". The refill stays
    /// off for the whole window whether or not the blade came home early.
    /// </remarks>
    public const int BladeLife = 15;

    /// <summary><c>bladeDamage</c>: a blade thrown with the magazine above two thirds.</summary>
    public const double BladeDamage = 17;

    /// <summary><c>depletedBladeDamage</c>: one thrown below one third.</summary>
    public const double DepletedBladeDamage = 8;

    /// <summary><c>lowAmmoThreshold</c>: <c>maxAmmo/3</c>.</summary>
    public const double BladeLowAmmoThreshold = 90 / 3.0;

    /// <summary><c>highAmmoThreshold</c>: <c>2*maxAmmo/3</c>.</summary>
    public const double BladeHighAmmoThreshold = 2 / 3.0 * 90;

    /// <summary><c>replenishingRate</c>: two units a tick, in the weapon's own Begin Step.</summary>
    private const double BladeReplenishingRate = 2;

    /// <summary>How far ahead of the Blade a bubble is created.</summary>
    private const double BubbleMuzzleOffset = 8;

    /// <summary>A bubble's muzzle speed, from <c>createShot(..., 10)</c>.</summary>
    private const double BubbleSpeed = 10;

    /// <summary>How far ahead of the Blade a thrown blade is created.</summary>
    private const double BladeMuzzleOffset = 5;

    /// <summary>A blade's muzzle speed, from <c>createShot(..., 12)</c>.</summary>
    private const double BladeSpeed = 12;

    // ----- the airblast (Flamethrower.events/Create.xml + User Event 2.xml) --

    /// <summary><c>blastReloadTime</c>: <c>alarm[1]</c>'s and <c>alarm[5]</c>'s re-arm.</summary>
    public const int BlastReloadTime = 40;

    /// <summary><c>flareReloadTime</c>: <c>alarm[2]</c>'s re-arm.</summary>
    public const int FlareReloadTime = 55;

    /// <summary>What one blast costs, and the threshold it is tested against.</summary>
    private const double BlastAmmoCost = 40;

    /// <summary><c>blastNoFlameTime</c>: <c>alarm[0]</c> after a blast — no flames for 15 ticks.</summary>
    private const int BlastNoFlameTime = 15;

    /// <summary><c>blastDistance</c>: the shove falloff's denominator.</summary>
    private const double BlastDistance = 150;

    /// <summary><c>characterBlastStrength</c>: the shove at zero distance.</summary>
    private const double CharacterBlastStrength = 15;

    /// <summary>
    /// <c>blastStrength</c>: what shoves a loose sheet or gib — neither of
    /// which a headless server has — and, divided by three, the floor a
    /// reflected mine's speed is raised to.
    /// </summary>
    private const double BlastStrength = 28;

    /// <summary>The circle asked about every character (and sheet and gib).</summary>
    private const double BlastCharacterRadius = 25;

    /// <summary>The circle asked about every rocket, flare and mine.</summary>
    private const double BlastReflectRadius = 5;

    /// <summary>A reflected rocket's restored <c>knockback</c> and its two re-armed alarms.</summary>
    private const double ReflectedRocketKnockback = 8;

    private const int ReflectedKnockbackCapDelay = 40;

    private const int ReflectedKnockbackEndDelay = 80;

    /// <summary>A reflected flare's re-armed lifetime: <c>alarm[0] = 40</c>.</summary>
    private const int ReflectedFlareLife = 40;

    /// <summary>The flare's cost, and the tank level that permits it.</summary>
    private const double FlareAmmoCost = 35;

    private const double FlareAmmoThreshold = 75;

    /// <summary>A flare's muzzle speed, from <c>createShot(..., 15)</c>.</summary>
    private const double FlareSpeed = 15;

    /// <summary>
    /// <c>AirBlastMaskS</c>, the mask <c>AirBlastO.xml</c> declares: bbox
    /// 9,1,97,28 on a 120x30 image about origin (1, 14). Measured against the
    /// engine (<c>tools/probes/probe-airblast.js</c>) — the XML's AUTO bound
    /// happens to agree, but the engine is the oracle.
    /// </summary>
    private static readonly Aabb AirBlastSpriteBbox = new(9, 1, 97, 28);

    private const double AirBlastOriginX = 1;

    private const double AirBlastOriginY = 14;

    /// <summary>
    /// One character's fire step: the part of Begin Step that can create
    /// projectiles.
    /// </summary>
    /// <remarks>
    /// Called immediately before <see cref="CharacterPhysics.BeginStep"/> for
    /// the same slot rather than as a pass of its own, and the two orderings are
    /// not equivalent. In the reference this code sits inside Begin Step, a few
    /// lines above the acceleration loop; nothing it reads is written by the
    /// lines above it and nothing it writes is read by them, so hoisting it to
    /// the front of the same slot's Begin Step is exact — while hoisting it to
    /// the front of the <em>tick</em> would let one character's shot see another
    /// character's stale position.
    /// </remarks>
    public static void FireStep(CollisionWorld world, ref WorldState state, int slot, ref TickEvents events)
    {
        ArgumentNullException.ThrowIfNull(world);

        ref var character = ref state.Characters[slot];
        ref var weapon = ref state.Weapons[slot];

        // Character.events/Begin Step.xml:96 — `downOrPressedKeys`, the union,
        // so a click that arrives and is released inside one tick still fires.
        var downOrPressed = character.KeyState | character.PressedKeys;

        if ((downOrPressed & PlayerKeys.Attack) == 0)
            return;

        // Begin Step.xml:84: the whole attack/taunt/cloak block is wrapped in
        // `if(!taunting and !omnomnomnom)`. An eating heavy holding fire does
        // nothing at all — and note this returns BEFORE the spawn-protection
        // line below, so eating does not spend it either. The same is true of a
        // taunt, and of the losing team of an Arena round
        // (Begin Step.xml:97's `!player.humiliated`).
        if (character.Taunting || character.Omnomnomnom || character.Humiliated)
            return;

        // Character.events/Begin Step.xml:85, beside the `event_user(1)` and not
        // inside the weapon. It is the *key* that ends spawn protection, not
        // the shot — a scout out of ammo with a hot refire timer still gives it
        // up by pulling the trigger.
        character.DeathmatchInvulnerable = 0;

        // All three of `taunting`, `omnomnomnom` and `player.humiliated`
        // suppress the shot, and all three are live guards above — the last two
        // arrived with the sandvich and with Arena's humiliation round. Begin
        // Step.xml:84 wraps the attack, the taunt and the cloak in
        // `if(!taunting and !omnomnomnom)`, :97 adds `!player.humiliated`, and
        // :124 takes the right click too.

        var kind = WeaponStats.KindFor(character.Class);

        // The Medigun leaves before the guard below, not after it. Its User
        // Event 1 is a heal beam rather than a shot: it has no `ammoCount > 0`
        // clause at all, and its `readyToShoot` test guards only the *target
        // acquisition* — so a medic with an empty magazine and a hot refire
        // timer still heals. Applying the Scattergun's guard here would silence
        // the beam for two ticks out of every three.

        if (kind == WeaponKind.Medigun)
        {
            Healing.BeamStep(world, ref state, slot, ref events);
            return;
        }

        // The Flamethrower leaves early for a different reason: its User Event 1
        // does work *before* the ready-and-loaded test. The "prevent sputtering"
        // line runs on every tick the trigger is held, empty tank or not.
        if (kind == WeaponKind.Flamethrower)
        {
            FireFlamethrower(world, ref state, slot);
            return;
        }

        // The Minigun has the same sputtering line, in the same place, for the
        // same reason — and like the Flamethrower it is unseeded, so it
        // broadcasts no WEAPON_FIRE and never reaches DrawSeed below.
        if (kind == WeaponKind.Minigun)
        {
            FireMinigun(world, ref state, slot);
            return;
        }

        // The Revolver's User Event 1 is an if/else with two entirely
        // different bodies: a seeded bullet, or — for a *cloaked* spy who is
        // not holding the cloak key — the knife. The shot arm has one clause
        // the shared guard below does not (`!owner.cloak`), and the stab arm has
        // to be reachable when the shot arm is refused, so the whole decision
        // happens here and only the shot arm falls through.
        if (kind == WeaponKind.Revolver)
        {
            if (!weapon.ReadyToShoot
                || character.Cloak
                || !Gml.Greater(weapon.AmmoCount, 0))
            {
                TryStab(ref state, slot);
                return;
            }
        }

        // The Blade leaves early for a third reason: its User Event 1 has no
        // ammo clause either — a bubble is free — and it is unseeded like the
        // two above, so it must not reach DrawSeed.
        if (kind == WeaponKind.Blade)
        {
            FireBubble(ref state, slot);
            return;
        }

        // The Rifle has no magazine at all — `maxAmmo = 0` — so the shared
        // `ammoCount > 0` guard below would silence it for ever. Its own User
        // Event 1 tests only `readyToShoot`. It *is* seeded, though, so unlike
        // the two above it falls through to DrawSeed rather than firing here.

        // Scattergun.events/User Event 1.xml. `global.isHost` is always true
        // here: this *is* the host, and a client never fires for itself.
        if (!weapon.ReadyToShoot)
            return;

        // Every weapon but the Rifle also needs a round in the magazine.
        if (kind != WeaponKind.Rifle && !Gml.Greater(weapon.AmmoCount, 0))
            return;

        // Every unimplemented weapon's User Event 3 arrives with that weapon.
        // Bailing *before* the seed is drawn rather than after is deliberate: a
        // shot that is announced and not simulated is worse than one that never
        // happens, because the client would draw projectiles the server has no
        // record of and then snap them away.
        if (kind is not (WeaponKind.Scattergun or WeaponKind.Shotgun
            or WeaponKind.Rocketlauncher or WeaponKind.Minegun or WeaponKind.Rifle
            or WeaponKind.Revolver))
            return;

        // The Minegun's User Event 1 carries one more clause than everybody
        // else's: `lobbed < maxMines`. It sits with the other two, above the
        // seed, so a demoman who has run the field out of mines is silent.
        if (kind == WeaponKind.Minegun && weapon.Lobbed >= MinegunMaxMines)
            return;

        // Drawn for every weapon that fires, including the ones that do not
        // scatter. The Rocketlauncher's User Event 1 calls irandom(65535) and
        // broadcasts it exactly like the Scattergun's, and its User Event 3 then
        // reads none of it — but the draw still happens and the seed still goes
        // on the wire.
        var seed = DrawSeed(ref state, slot, ref events);

        events.Fired[slot] = true;
        events.Fires[slot] = new WeaponFireEvent(
            slot,
            seed,
            character.X,
            character.Y,
            character.HSpeed,
            character.VSpeed);

        // doEventFireWeapon.gml saves the global seed, sets the shot's, and puts
        // the global one back. A local generator is the same thing with none of
        // the bookkeeping: WorldState.Random is simply never touched below.
        var random = new GmlRandom(seed);

        switch (kind)
        {
            case WeaponKind.Scattergun:
            case WeaponKind.Shotgun:
                FireScattergun(ref state, slot, kind, ref random);
                break;

            case WeaponKind.Rocketlauncher:
                FireRocketlauncher(world, ref state, slot);
                break;

            case WeaponKind.Minegun:
                FireMinegun(ref state, slot);
                break;

            case WeaponKind.Rifle:
                // The seed is drawn and broadcast and then read by nothing —
                // the Rifle's User Event 3 uses no randomness at all, exactly
                // like the Rocketlauncher's.
                FireRifle(world, ref state, slot, ref events);
                break;

            case WeaponKind.Revolver:
                // Seeded, and the seed is spent by createShot's perseverant
                // draw and nothing else — one bullet, no spread.
                FireRevolver(ref state, slot, ref random);
                break;
        }
    }

    /// <summary>
    /// <c>Character.events/End Step.xml:64-65</c>: the weapon trails its owner.
    /// </summary>
    /// <remarks>
    /// Deliberately not folded into <see cref="CharacterPhysics.EndStep"/>'s
    /// body — it is called from there, at the point the reference does it, which
    /// is <b>before</b> the map clamp a few lines below. A character standing on
    /// the map edge would otherwise arm its weapon at the clamped position.
    /// </remarks>
    public static void FollowOwner(in CharacterState character, ref WeaponState weapon)
    {
        weapon.X = Gml.Round(character.X);
        weapon.Y = Gml.Round(character.Y);
    }

    /// <summary>
    /// The seed this shot's spread is drawn from: the recorded one if the
    /// harness supplied it, otherwise a fresh draw.
    /// </summary>
    private static ushort DrawSeed(ref WorldState state, int slot, ref TickEvents events)
    {
        if (events.HasForcedFireSeed[slot])
        {
            // Consumed, so the next shot from this slot needs its own — a
            // replay that ran out of recorded seeds must fall through to a draw
            // and diverge visibly, rather than silently reusing the last one.
            events.HasForcedFireSeed[slot] = false;
            return events.ForcedFireSeed[slot];
        }

        return (ushort)state.Random.NextInclusive(65535);
    }

    /// <summary>
    /// <c>Character.events/Begin Step.xml:120-126</c>: right-click, and what the
    /// held weapon makes of it.
    /// </summary>
    /// <remarks>
    /// <b>Where this runs is the whole point.</b> It is between the jump and the
    /// <c>switch(moveStatus)</c> that picks this tick's friction, so a mine that
    /// goes off under its own owner sets <c>moveStatus = 1</c> and that switch
    /// reads it immediately. <see cref="CharacterPhysics.BeginStepInput"/> and
    /// <see cref="CharacterPhysics.BeginStepAccelerate"/> exist to leave room for
    /// it.
    /// <para>
    /// The Minegun detonates and the Medigun shoots needles or pops its uber.
    /// The Flamethrower's airblast, the Revolver's stab and Quote's bubbles hang
    /// off the same key and arrive with their weapons; every other weapon's User
    /// Event 2 is empty, so the key does nothing at all for six of the ten
    /// classes.
    /// </para>
    /// </remarks>
    public static void SpecialStep(CollisionWorld world, ref WorldState state, int slot, ref TickEvents events)
    {
        ArgumentNullException.ThrowIfNull(world);

        ref var character = ref state.Characters[slot];

        // The union again, so a click that arrives and is released inside one
        // tick still counts. `taunting`, `omnomnomnom` and `player.humiliated`
        // all guard it, below.
        var downOrPressed = character.KeyState | character.PressedKeys;

        if ((downOrPressed & PlayerKeys.Special) == 0)
            return;

        // Begin Step.xml:124's `and !omnomnomnom`, which unlike the taunt clause
        // beside it has no demoman exemption: an eating demoman is impossible,
        // but an eating heavy right-clicking is not, and it does nothing.
        //
        // `(!taunting or player.class == CLASS_DEMOMAN)` — the exemption is
        // real and it is the only one in the block: a demoman can detonate mines
        // in the middle of a taunt, and nobody else can do anything.
        if (character.Omnomnomnom
            || character.Humiliated
            || (character.Taunting && character.Class != PlayerClass.Demoman))
        {
            return;
        }

        // Character.events/Begin Step.xml:124, *above* the `event_user(2)` this
        // time rather than below it — the order is not observable, and the rule
        // is the same one the attack key has: the key ends spawn protection
        // whether or not the weapon does anything.
        character.DeathmatchInvulnerable = 0;

        var kind = WeaponStats.KindFor(character.Class);

        if (kind == WeaponKind.Medigun)
        {
            FireNeedleOrUber(ref state, slot, ref events);
            return;
        }

        if (kind == WeaponKind.Flamethrower)
        {
            AirBlast(world, ref state, slot);
            return;
        }

        if (kind == WeaponKind.Blade)
        {
            ThrowBlade(ref state, slot);
            return;
        }

        if (kind != WeaponKind.Minegun)
            return;

        // Minegun.events/User Event 2.xml: every mine of this player's, in
        // creation order, then `lobbed = 0`.
        //
        // The count is zeroed rather than decremented to zero, and the two are
        // not the same: DetonateMine frees each mine, and WorldState's free path
        // already decrements the owner's count for it. Setting it afterwards is
        // what the reference does and is also what makes a mine the chain
        // destroyed without visiting this loop still get counted.
        for (var i = state.LiveProjectileCount - 1; i >= 0; i--)
        {
            if (i >= state.LiveProjectileCount)
                continue;

            var handle = state.LiveProjectileHandle(i);
            ref var projectile = ref state.Projectiles[handle.Index];

            if (projectile.Kind == ProjectileKind.Mine && projectile.OwnerSlot == slot)
                Explosions.DetonateMine(world, ref state, handle, ref events);
        }

        state.Weapons[slot].Lobbed = 0;
    }

    /// <summary>
    /// <c>Objects/Weapons/Flamethrower.events/Begin Step.xml</c>: the tank tops
    /// itself up.
    /// </summary>
    /// <remarks>
    /// A <em>weapon's</em> Begin Step, so it runs after the whole of its owner's
    /// — the same placement as the Medigun's uber drain, and for the same reason
    /// (a weapon carries a higher instance id than its character).
    /// <para>
    /// <b>the guard is <c>ammoCount &lt;= maxAmmo</c>, so the tank
    /// overfills to 201.8 and stops there.</b> Two hundred is not the resting
    /// value: a pyro who has fired once at any point in the match sits at 201.8
    /// for the rest of it, and the wire carries <b>202</b>. It is an
    /// <c>else if</c>, so a tick that clamped a negative tank does not also
    /// refill it.
    /// </para>
    /// </remarks>
    public static void WeaponBeginStep(ref WeaponState weapon, PlayerClass playerClass) =>
        WeaponBeginStep(ref weapon, playerClass, zoomed: false);

    /// <inheritdoc cref="WeaponBeginStep(ref WeaponState, PlayerClass)"/>
    /// <param name="zoomed">
    /// The owner's <c>zoomed</c>. Only the Rifle reads it, and it is passed
    /// rather than looked up because a weapon has no way back to its character
    /// in this state layout.
    /// </param>
    public static void WeaponBeginStep(ref WeaponState weapon, PlayerClass playerClass, bool zoomed)
    {
        var kind = WeaponStats.KindFor(playerClass);

        if (kind == WeaponKind.Rifle)
        {
            RifleBeginStep(ref weapon, zoomed);
            return;
        }

        if (kind == WeaponKind.Blade)
        {
            // Blade.events/Begin Step.xml, the whole event:
            // `if (replenishing) ammoCount = min(max(ammoCount + 2, 0), maxAmmo);`
            //
            // Clamped at *both* ends, where the Flamethrower's and the
            // Minigun's are two separate branches. So a Blade's tank rests at
            // exactly maxAmmo and never at the pyro's 201.8 — the guard is on
            // `replenishing` and not on the level, so there is no
            // `ammoCount <= maxAmmo` to overshoot.
            if (weapon.Replenishing)
            {
                weapon.AmmoCount = Math.Min(
                    Math.Max(weapon.AmmoCount + BladeReplenishingRate, 0),
                    WeaponStats.For(kind).MaxAmmo);
            }

            return;
        }

        if (kind is not (WeaponKind.Flamethrower or WeaponKind.Minigun))
            return;

        var stats = WeaponStats.For(kind);
        var refill = kind == WeaponKind.Flamethrower ? FlameAmmoCost : 1;

        if (Gml.Less(weapon.AmmoCount, 0))
        {
            weapon.AmmoCount = 0;
        }
        else if (Gml.LessOrEqual(weapon.AmmoCount, stats.MaxAmmo) && weapon.IsRefilling)
        {
            weapon.AmmoCount += refill;
        }

        if (kind != WeaponKind.Minigun)
            return;

        // The Minigun's Begin Step has a third line the Flamethrower's does
        // not: `if(!readyToShoot and alarm[5] < 25 and !isRefilling) alarm[5] += 1`.
        //
        // **It is not what stops a held trigger refilling the tank** — that
        // is the `alarm[5] = reloadBuffer*factor` on every shot, which re-arms
        // the buffer to 10 every two ticks and never lets it reach zero. This
        // line does something much smaller: the alarm stage takes one off
        // immediately after it, so during the (at most two) ticks between
        // `readyToShoot = false` and alarm[0] putting it back, the buffer holds
        // instead of decaying. The effect is that letting go of the trigger
        // starts the refill about **two ticks later** than it otherwise would.
        //
        // It reads as frame-rate compensation: the decrement is
        // `-global.delta_factor` and this is a flat `+1`, so below 30 Hz it
        // would push the buffer up rather than merely hold it. At the nominal
        // rate the port runs at, two ticks is the whole of it — measured by
        // deleting the line and watching
        // MinigunTests.LettingGoStartsTheRefillOneTickLate fail with 10.
        //
        // `!isRefilling` is unreachable and transcribed anyway. For it to
        // matter the weapon would have to be refilling *and* not ready to shoot,
        // and nothing reaches that: a shot clears `isRefilling` and the only
        // thing that clears `readyToShoot` is a shot. Proved by mutation.
        //
        // A resting -1 satisfies `< 25` too, so it is pushed to 0 — which
        // Timers.Advance decays back to -1 without firing. Harmless, and it is
        // what the reference does.
        if (!weapon.ReadyToShoot
            && weapon.ReloadTimer < MinigunReloadExtendCap
            && !weapon.IsRefilling)
        {
            weapon.ReloadTimer++;
        }
    }
}
