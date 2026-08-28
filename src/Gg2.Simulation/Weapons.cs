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
public static class Weapons
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
    /// ⚠️ The guard is in <c>Minegun.events/User Event 1.xml</c>, alongside the
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
    /// ⚠️ <b>Tighter than a scattergun's <c>random(15) - 7.5</c>.</b> Five
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
    /// ⚠️ <b>A flat twelve pixels down, outside the <c>lengthdir</c>.</b> The
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
    /// ⚠️ <b>They differ.</b> The guard is <c>ammoCount &gt;= 2</c> and the
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
    /// ⚠️ Public because the bot layer's sniper rule reads it: a scoped bot waits
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
    /// ⚠️ <b>The range is proportional to the damage</b>, so an unscoped shot
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
    /// ⚠️ <b>Set by a drag-and-drop <c>action_set_alarm(18, 2)</c></b>, which
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
    /// ⚠️ <b>Derived from two drawing variables, and it is the longest
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
    /// ✅ <b>The one tick that was uncertain is MEASURED</b> (2026-08-27), and 52
    /// stands. <b>GM8 advances <c>image_index</c> after the whole Step stage</b>,
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
    /// scaled for the generator. Its event also has ⚠️ <b>no line-of-sight
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
    /// 9,1,97,28 on a 120x30 image about origin (1, 14). Measured 2026-08-26
    /// against the engine (<c>tools/probes/probe-airblast.js</c>) — the XML's
    /// AUTO bound happens to agree, but the engine is the oracle.
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
        // inside the weapon. ⚠️ It is the *key* that ends spawn protection, not
        // the shot — a scout out of ammo with a hot refire timer still gives it
        // up by pulling the trigger.
        character.DeathmatchInvulnerable = 0;

        // ⚠️ All three of `taunting`, `omnomnomnom` and `player.humiliated`
        // suppress the shot, and all three are live guards above — the last two
        // arrived with the sandvich and with Arena's humiliation round. Begin
        // Step.xml:84 wraps the attack, the taunt and the cloak in
        // `if(!taunting and !omnomnomnom)`, :97 adds `!player.humiliated`, and
        // :124 takes the right click too.

        var kind = WeaponStats.KindFor(character.Class);

        // ⚠️ The Medigun leaves before the guard below, not after it. Its User
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

        // ⚠️ The Revolver's User Event 1 is an if/else with two entirely
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

        // ⚠️ The Rifle has no magazine at all — `maxAmmo = 0` — so the shared
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
    /// <c>Rifle.events/Begin Step.xml</c>: the charge, and the damage it buys.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The charge only runs while scoped <em>and</em> ready to
    /// shoot</b>, and anything else resets it to zero outright — there is no
    /// decay. Unscoping throws away a full charge instantly, and so does firing:
    /// the shot clears <c>readyToShoot</c>, so <c>t</c> is back at 0 on the very
    /// next tick.
    /// </para>
    /// <para>
    /// ⚠️ <b>The ramp is a square root, not a line.</b>
    /// <c>baseDamage + floor(sqrt(t/chargeTime)*(maxDamage-baseDamage))</c> — so
    /// a quarter of the charge time is already half the bonus. The
    /// <c>floor</c> makes the damage an integer, which is why a rifle's hp
    /// arithmetic stays exact where a flame's does not.
    /// </para>
    /// <para>
    /// ⚠️ <b><c>hitDamage</c> is recomputed every tick</b>, not at the moment
    /// of the shot — which is what makes <c>toggleZoom</c>'s own write to it
    /// redundant, and why this port does not transcribe that write.
    /// </para>
    /// </remarks>
    private static void RifleBeginStep(ref WeaponState weapon, bool zoomed)
    {
        if (zoomed && weapon.ReadyToShoot)
        {
            weapon.Charge += 1;

            if (Gml.Greater(weapon.Charge, RifleChargeTime))
                weapon.Charge = RifleChargeTime;
        }
        else
        {
            weapon.Charge = 0;
        }

        weapon.HitDamage = zoomed
            ? RifleBaseDamage + Gml.Floor(
                Math.Sqrt(weapon.Charge / RifleChargeTime) * (RifleMaxDamage - RifleBaseDamage))
            : RifleUnscopedDamage;
    }

    /// <summary>
    /// <c>Rifle.events/User Event 3.xml</c>: the hitscan, and what it hits.
    /// </summary>
    /// <remarks>
    /// <b>The only hitscan weapon in the game</b>, and the only one that reaches
    /// its target the same tick it is fired.
    /// <para>
    /// ⚠️ <b>It is a binary search, not a ray cast.</b> The reference halves
    /// the segment sixteen-ish times, testing only the <em>near</em> half each
    /// round and keeping whatever it hit, until the remaining length is under a
    /// pixel. Its own comment explains why: <c>collision_line</c> can test one
    /// object or all of them, never "some instances of some objects", so the
    /// search tests each stopping <em>object</em> in turn and blanks the masks
    /// of the instances that should not stop the bullet.
    /// </para>
    /// <para>
    /// ⚠️ <b>The object order decides ties, and it is not "nearest wins".</b>
    /// <c>Obstacle, Generator, Character, Sentry, [TeamGate], IntelGate,
    /// [ControlPointSetupGate], BulletWall</c> — the inner loop stops at the
    /// first object type that reports a hit anywhere in the tested half, so a
    /// wall behind a player still beats the player if both are in the same
    /// sub-segment. Reproducing the order is not optional.
    /// </para>
    /// <para>
    /// ⚠️ <b>Three kinds of instance are made invisible to it</b>, by having
    /// their masks blanked for the duration: friendly generators, friendly
    /// sentries and <em>the shooter</em>. Everything else is solid to the shot —
    /// including <b>team-mates</b>, who stop the bullet and take no damage from
    /// it. Body-blocking a sniper works.
    /// </para>
    /// <para>
    /// ⚠️ <b>The range is the damage.</b> <c>len = 1500*(hitDamage/maxDamage)</c>,
    /// so an unscoped shot carries 700 pixels and a fully charged one 1,500 —
    /// and a rifle that is still charging reaches proportionally less far.
    /// </para>
    /// </remarks>
    private static void FireRifle(
        CollisionWorld world, ref WorldState state, int slot, ref TickEvents events)
    {
        ref var character = ref state.Characters[slot];
        ref var weapon = ref state.Weapons[slot];
        var stats = WeaponStats.For(WeaponKind.Rifle);

        var zoomed = character.Zoomed;
        var damage = weapon.HitDamage;

        weapon.ReadyToShoot = false;
        weapon.RefireTimer = stats.ReloadTime + (zoomed ? RifleScopedRefirePenalty : 0);

        // ⚠️ The ×100-then-÷100 round trip is the reference's own arithmetic,
        // and it is transcribed rather than simplified: the result seeds a
        // sequence of halvings, so a last-bit difference here moves where the
        // search stops.
        var length = RifleRange * (((damage * 100) / RifleMaxDamage) / 100);

        var x1 = weapon.X;
        var y1 = weapon.Y;
        var x2 = x1 + Gml.LengthDirX(length, character.AimDirection);
        var y2 = y1 + Gml.LengthDirY(length, character.AimDirection);

        var hit = Hitscan(world, in state, slot, ref x1, ref y1, ref x2, ref y2, length);

        switch (hit.Kind)
        {
            case HitscanTarget.Character:
                DealRifleHit(ref state, slot, hit.Slot, damage, zoomed, ref events);
                break;

            case HitscanTarget.Sentry:
                // No ubered test and no team test — the friendly ones were made
                // invisible instead, so anything reached here is fair game.
                Sentries.Damage(ref state, hit.Slot, damage, (byte)slot,
                    zoomed ? DamageSource.RifleCharged : DamageSource.Rifle);
                break;

            case HitscanTarget.Generator:
                Generators.Damage(ref state, (Team)hit.Slot, damage);
                break;
        }
    }

    /// <summary>What a rifle's binary search came to rest on.</summary>
    private enum HitscanTarget
    {
        None,
        Map,
        Character,
        Sentry,
        Generator,
    }

    /// <summary>
    /// The instance the search stopped at: a roster slot for a character or a
    /// sentry, a <see cref="Team"/> for a generator.
    /// </summary>
    private readonly record struct HitscanHit(HitscanTarget Kind, int Slot);

    /// <summary>
    /// The <c>while(len&gt;1)</c> loop, in the reference's object order.
    /// </summary>
    /// <remarks>
    /// The halving is on the segment and the test is always on the <b>near</b>
    /// half, so the search converges on the first thing along the ray to within
    /// a pixel — it does not find the exact contact point, and it can return an
    /// instance a true ray cast would have passed behind. That is the
    /// reference's answer and it is the one clients see.
    /// </remarks>
    private static HitscanHit Hitscan(
        CollisionWorld world,
        in WorldState state,
        int shooter,
        ref double x1,
        ref double y1,
        ref double x2,
        ref double y2,
        double length)
    {
        var found = new HitscanHit(HitscanTarget.None, 0);

        while (Gml.Greater(length, 1))
        {
            var xm = (x1 + x2) / 2;
            var ym = (y1 + y2) / 2;

            var hit = FirstStoppingObject(world, in state, shooter, x1, y1, xm, ym);

            if (hit.Kind != HitscanTarget.None)
            {
                x2 = xm;
                y2 = ym;
                found = hit;
            }
            else
            {
                x1 = xm;
                y1 = ym;
            }

            length /= 2;
        }

        return found;
    }

    /// <summary>
    /// One round of the search: the first <em>object</em> in the reference's
    /// list that anything of reports a hit on this segment.
    /// </summary>
    private static HitscanHit FirstStoppingObject(
        CollisionWorld world,
        in WorldState state,
        int shooter,
        double x1,
        double y1,
        double x2,
        double y2)
    {
        // 1. Obstacle — the map's own mask, and the player walls a bullet
        //    ignores are not Obstacles. It is first, so a wall beats everything
        //    behind it in the same half.
        if (world.CollisionLineHitsMask(x1, y1, x2, y2))
            return new HitscanHit(HitscanTarget.Map, 0);

        // 2. Generator — friendly ones had their masks blanked, so only the
        //    enemy's is here.
        var shooterTeam = state.Characters[shooter].Team;

        foreach (var team in new[] { Team.Red, Team.Blue })
        {
            ref readonly var generator = ref team == Team.Red
                ? ref state.RedGenerator
                : ref state.BlueGenerator;

            if (!generator.Exists || team == shooterTeam)
                continue;

            if (CollisionLine.Hits(
                    LineCollider.ForCharacter(Generators.Box, generator.X, generator.Y),
                    default(CollisionLine.FilledMask), x1, y1, x2, y2))
            {
                return new HitscanHit(HitscanTarget.Generator, (int)team);
            }
        }

        // 3. Character — everybody but the shooter, team-mates included. A
        //    team-mate stops the bullet and takes nothing.
        for (var i = 0; i < WorldState.MaxSlots; i++)
        {
            if (!state.Alive[i] || i == shooter)
                continue;

            ref readonly var other = ref state.Characters[i];

            if (CollisionLine.Hits(
                    LineCollider.ForCharacter(other.Box, other.X, other.Y, other.Facing),
                    default(CollisionLine.FilledMask), x1, y1, x2, y2))
            {
                return new HitscanHit(HitscanTarget.Character, i);
            }
        }

        // 4. Sentry — enemy ones only, for the same reason as the generator.
        for (var i = 0; i < WorldState.MaxSlots; i++)
        {
            if (!state.HasSentry[i] || state.Sentries[i].Team == shooterTeam)
                continue;

            ref readonly var sentry = ref state.Sentries[i];

            if (CollisionLine.Hits(
                    LineCollider.ForCharacter(Sentries.Box, sentry.X, sentry.Y, sentry.StartDirection),
                    default(CollisionLine.FilledMask), x1, y1, x2, y2))
            {
                return new HitscanHit(HitscanTarget.Sentry, i);
            }
        }

        // 5-8. The gates and the bullet walls, in the reference's order:
        //      TeamGate (unless mapchanging), IntelGate, ControlPointSetupGate
        //      (only while the setup timer runs), BulletWall. CollisionWorld
        //      walks them together off the same filter gunSetSolids builds,
        //      minus the Obstacle it already tested above.
        return world.CollisionLineBlocksBullets(x1, y1, x2, y2, state.BulletFilter)
            ? new HitscanHit(HitscanTarget.Map, 0)
            : new HitscanHit(HitscanTarget.None, 0);
    }

    /// <summary>
    /// The <c>with(hitInstance)</c> character arm: damage, the assist
    /// bookkeeping and the flicker.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The team and uber tests are here rather than in the search</b>, so
    /// a team-mate or an ubered enemy still <em>stops</em> the bullet and simply
    /// takes nothing from it.
    /// </remarks>
    private static void DealRifleHit(
        ref WorldState state,
        int shooter,
        int victimSlot,
        double damage,
        bool zoomed,
        ref TickEvents events)
    {
        ref var victim = ref state.Characters[victimSlot];

        if (victim.Ubered || victim.Team == state.Characters[shooter].Team)
            return;

        DamageApi.Deal(ref events, victimSlot, shooter, ref victim, damage);
        victim.TimeUnscathed = 0;

        // The same demote-and-inherit the other weapons do.
        if (victim.LastDamageDealer != shooter && victim.LastDamageDealer != victimSlot)
        {
            victim.SecondToLastDamageDealer = victim.LastDamageDealer;
            victim.AssistTimer = victim.FinishOffTimer;
        }

        victim.FinishOffTimer = GameConstants.AssistTime;
        victim.LastDamageDealer = (byte)shooter;
        victim.DealFlicker();
        victim.LastDamageSource = zoomed ? DamageSource.RifleCharged : DamageSource.Rifle;
    }

    /// <summary>
    /// <c>Minigun.events/User Event 1.xml</c>: the heavy's chaingun.
    /// </summary>
    /// <remarks>
    /// <b>A single bullet every two ticks, and it is unseeded.</b> Like the
    /// Flamethrower and the Blade the Minigun calls <c>randomize()</c> and puts
    /// no seed on the wire, so no client re-derives its spread and it
    /// broadcasts no <c>WEAPON_FIRE</c>. docs/PARITY.md: a recording containing
    /// a heavy has an hp window that ends at its first connecting shot whatever
    /// this port does, which is why the gate here is unit tests and not a
    /// fixture.
    /// <para>
    /// ⚠️ <b><c>randomize()</c> is deliberately not transcribed.</b> It
    /// reseeds GM8's global stream from the clock; doing that to
    /// <see cref="WorldState.Random"/> would make a forked rollout diverge from
    /// its parent for a reason that has nothing to do with the actions being
    /// compared, and would break docs/BOTAI.md item 2's "restore to a
    /// bit-identical state". Drawing from the existing stream is just as
    /// unpredictable to a client — which sees no seed either way — and stays
    /// reproducible. docs/SIMULATION.md, "server-side RNG is free".
    /// </para>
    /// <para>
    /// ⚠️ <b>The "prevent sputtering" line is above the ready-and-loaded
    /// guard</b>, exactly as the Flamethrower's is, so a heavy holding the
    /// trigger on an empty tank drives <c>ammoCount</c> further negative every
    /// tick. The weapon's own Begin Step clamps it back to 0 the same tick.
    /// </para>
    /// <para>
    /// ⚠️ <b>A blocked muzzle costs the shot and nothing else.</b>
    /// <c>collision_line_bulletblocking(x, y, shotx, shoty)</c> is tested
    /// <em>before</em> the bullet is created, and its <c>else</c> makes only a
    /// cosmetic <c>Impact</c> — but the ammo, the refire and the reload buffer
    /// below it all run regardless. Firing into a wall you are standing against
    /// still empties the tank.
    /// </para>
    /// </remarks>
    private static void FireMinigun(CollisionWorld world, ref WorldState state, int slot)
    {
        ref var character = ref state.Characters[slot];
        ref var weapon = ref state.Weapons[slot];
        var stats = WeaponStats.For(WeaponKind.Minigun);

        // Above the guard, and unconditional — see the remarks.
        if (Gml.Less(weapon.AmmoCount, MinigunMinAmmo))
            weapon.AmmoCount -= MinigunMinAmmo;

        if (!weapon.ReadyToShoot || !Gml.GreaterOrEqual(weapon.AmmoCount, MinigunMinAmmo))
            return;

        var aim = character.AimDirection;

        // ⚠️ The drop is outside the lengthdir. See MinigunMuzzleDrop.
        var muzzleX = weapon.X + Gml.LengthDirX(MinigunMuzzleOffset, aim);
        var muzzleY = weapon.Y + MinigunMuzzleDrop + Gml.LengthDirY(MinigunMuzzleOffset, aim);

        // ⚠️ Both draws happen while evaluating createShot's arguments, so
        // they come *before* the Shot's own Create event and its perseverant
        // choose() — the opposite order from the Scattergun, which passes the
        // unjittered aim and adjusts the pellet afterwards. Left to right: the
        // direction jitter, then the speed jitter.
        var direction = aim + (state.Random.Next(MinigunSpread) - (MinigunSpread / 2));
        var speed = MinigunShotSpeed + state.Random.Next(MinigunSpeedJitter);

        if (!world.CollisionLineBlocksBullets(
                weapon.X, weapon.Y, muzzleX, muzzleY, state.BulletFilter))
        {
            var shot = CreateShot(
                muzzleX,
                muzzleY,
                ProjectileKind.Shot,
                DamageSource.Minigun,
                direction,
                speed,
                slot,
                character.Team,
                ref state.Random);

            // The Minigun does not touch hitDamage, so a bullet keeps the
            // Shot's own 8 — the same as a scattergun pellet and one more than
            // the engineer's.

            // A component write after two polar ones, which re-derives the pair
            // and snaps it. A pellet does the same; see Motion.
            shot.Motion.HSpeed += character.HSpeed;

            // Thirty ticks, not the Shot's forty.
            shot.Lifetime = MinigunShotLifetime;

            state.SpawnProjectile(shot);
        }

        weapon.ReadyToShoot = false;
        weapon.IsRefilling = false;
        weapon.AmmoCount -= MinigunAmmoCost;

        // ⚠️ Read after the ammo is spent, so it describes the tank the shot
        // left behind.
        var factor = Gml.Less(weapon.AmmoCount, MinigunAmmoCost) ? MinigunEmptyReloadFactor : 1;

        weapon.RefireTimer = stats.RefireTime;
        weapon.ReloadTimer = Timers.SetAlarm(stats.ReloadBuffer * factor);
    }

    /// <summary>
    /// <c>Objects/Weapons/Revolver.events/User Event 3.xml</c>: one bullet.
    /// </summary>
    /// <remarks>
    /// The hardest single projectile in the game — 28, against a scattergun
    /// pellet's 8 — and the fastest, at 21.
    /// <list type="bullet">
    /// <item>⚠️ <b>The speed boost is a <em>product</em> of two speeds</b>:
    /// <c>with(shot) speed += owner.hspeed * hspeed / 15</c>, where inside the
    /// <c>with</c> the bare <c>hspeed</c> is the <em>shot's</em>. So a spy
    /// running right at 4 adds <c>4 x 21 / 15</c> to a bullet aimed right and
    /// <em>subtracts</em> the same from one aimed left, and a bullet fired
    /// straight up gets nothing at all.</item>
    /// <item>⚠️ <b>The bullet is moved a whole step forward the moment it is
    /// made</b>, with the boosted speed and before anything can test it. The
    /// reference's comment: "to avoid immediate collision with a wall behind
    /// the character", and "delta_factor is left out intentionally". That is a
    /// second nudge on top of the movement pass's own, so a revolver bullet
    /// covers two steps on its first tick.</item>
    /// <item>⚠️ <b>The reload is <c>reloadBuffer + reloadTime</c> in one
    /// go</b> — 63 ticks — where the Scattergun family arms the buffer and then
    /// re-arms per round. The Revolver's Alarm 5 refills the whole cylinder at
    /// once and does not re-arm, so there is only ever the one wait.</item>
    /// </list>
    /// The muzzle is the weapon's own <c>x</c> — no <c>lengthdir</c> at all,
    /// the only weapon without one — and <c>y + yoffset + 1</c>, five pixels
    /// up.
    /// </remarks>
    private static void FireRevolver(ref WorldState state, int slot, ref GmlRandom random)
    {
        ref var character = ref state.Characters[slot];
        ref var weapon = ref state.Weapons[slot];
        var stats = WeaponStats.For(WeaponKind.Revolver);

        weapon.AmmoCount = Math.Max(0, weapon.AmmoCount - 1);

        var shot = CreateShot(
            weapon.X,
            weapon.Y + RevolverMuzzleDrop,
            ProjectileKind.Shot,
            DamageSource.Revolver,
            character.AimDirection,
            RevolverShotSpeed,
            slot,
            character.Team,
            ref random);

        shot.HitDamage = RevolverHitDamage;

        // A polar write after the polar pair createShot left, so it re-derives
        // and snaps the components.
        shot.Motion.Speed += character.HSpeed * shot.Motion.HSpeed / 15;

        // The extra step, at the boosted speed.
        shot.X += Gml.LengthDirX(shot.Motion.Speed, shot.Motion.Direction);
        shot.Y += Gml.LengthDirY(shot.Motion.Speed, shot.Motion.Direction);

        state.SpawnProjectile(shot);

        weapon.ReadyToShoot = false;
        weapon.RefireTimer = stats.RefireTime;
        weapon.ReloadTimer = stats.ReloadBuffer + stats.ReloadTime;
    }

    /// <summary>
    /// The <c>else</c> arm of <c>Revolver.events/User Event 1.xml</c>: the
    /// knife.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>It is the attack key that stabs, and only while cloaked.</b> A spy
    /// out of cloak shoots and a spy in cloak stabs, off the same button — so
    /// there is no separate melee input, and the arm is reached only when the
    /// shot arm was refused.
    /// <para>
    /// ⚠️ <b><c>!(owner.keyState &amp; $08)</c> is the <em>cloak</em> key</b>,
    /// which is the right click. A spy holding it down cannot stab, which is
    /// what stops the press that uncloaks them also burying a knife.
    /// </para>
    /// <para>
    /// ⚠️ <b>Nothing takes damage here.</b> This creates the animation and arms
    /// <see cref="WeaponState.StabTimer"/>; the hitbox arrives thirty-two ticks
    /// later, in the alarm stage. And the spy is frozen for
    /// <see cref="StabFreezeTicks"/> — <c>runPower = 0</c> and
    /// <c>jumpStrength = 0</c>, both of which the reference restores from
    /// <c>baseRunPower</c>/<c>baseJumpStrength</c> rather than from whatever
    /// they were.
    /// </para>
    /// </remarks>
    private static void TryStab(ref WorldState state, int slot)
    {
        ref var character = ref state.Characters[slot];
        ref var weapon = ref state.Weapons[slot];

        if (!weapon.ReadyToStab
            || !character.Cloak
            || (character.KeyState & PlayerKeys.Special) != 0)
        {
            return;
        }

        character.RunPower = 0;
        character.JumpStrength = 0;
        character.Stabbing = true;
        character.StabFreezeTimer = StabFreezeTicks;

        weapon.StabDirection = character.AimDirection;
        weapon.ReadyToStab = false;
        weapon.StabTimer = StabWindUp;
    }

    /// <summary>
    /// <c>Revolver.events/Alarm 1.xml</c>: the knife's hitbox, thirty-two ticks
    /// after the spy committed to it.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>It is not a shot.</b> No <c>createShot</c>, so no
    /// <c>perseverant</c> draw and no <c>image_angle</c> — a <c>StabMask</c>
    /// stands still at its owner's feet for six ticks with speed zero, and its
    /// <c>direction</c> exists only to decide which way its box is mirrored.
    /// <para>
    /// The position here is the weapon's, and it is overwritten by the mask's
    /// own Step in the same tick (<see cref="Projectiles.StabStep"/>) — so it
    /// is transcribed rather than load-bearing.
    /// </para>
    /// </remarks>
    public static void CreateStabMask(ref WorldState state, int slot)
    {
        ref var character = ref state.Characters[slot];
        ref var weapon = ref state.Weapons[slot];

        var mask = new ProjectileState
        {
            Kind = ProjectileKind.Stab,
            X = weapon.X,
            Y = weapon.Y,
            OwnerSlot = (byte)slot,
            Reflector = ProjectileState.NoOwner,
            Team = character.Team,
            Source = DamageSource.Knife,

            // StabMask.events/Create.xml writes 8 and Alarm 1 overwrites it two
            // lines later; the 8 is never observable.
            HitDamage = StabHitDamage,
            Lifetime = StabMaskLifetime,
        };

        mask.Motion.Direction = weapon.StabDirection;
        mask.Motion.Speed = 0;

        state.SpawnProjectile(mask);
    }

    /// <summary>
    /// <c>StabAnim.events/Step.xml</c> and its Destroy: the freeze running out.
    /// </summary>
    /// <remarks>
    /// A <c>StabAnim</c> is younger than every character, so its Step runs
    /// after all of theirs — which is why this is its own phase beside the
    /// bubbles' rather than a line inside the character loop. See
    /// <see cref="CharacterState.StabFreezeTimer"/> for why it is a counter and
    /// not an instance.
    /// </remarks>
    public static void StabAnimStep(ref WorldState state)
    {
        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
        {
            if (!state.Alive[slot])
                continue;

            ref var character = ref state.Characters[slot];

            if (character.StabFreezeTimer <= 0)
                continue;

            character.StabFreezeTimer -= 1;

            if (character.StabFreezeTimer > 0)
                continue;

            // The Destroy event, in full: back to the *base* values, not to
            // whatever the character had before the stab.
            character.RunPower = ClassStats.For(character.Class).RunPower;
            character.JumpStrength = ClassStats.BaseJumpStrength;
            character.Stabbing = false;
        }
    }

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
    /// <c>Objects/Weapons/Blade.events/User Event 2.xml</c>: the thrown blade.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>This click costs nothing up front either — the blade bills its
    /// owner while it flies.</b> The weapon only stops replenishing; the
    /// magazine is drained one unit a tick by
    /// <c>BladeB.events/End Step.xml</c>, and its Destroy event charges the
    /// difference up to <c>refireTime</c> if it came home early. So a blade that
    /// hits something immediately costs 5 and one that runs its course costs 15.
    /// <para>
    /// ⚠️ <b>The blade's <c>image_angle</c> is forced to 0</b>, which is the one
    /// projectile in the game whose box is not turned to face its flight. The
    /// reference cites its own issue #230: a rotated 17x17 box gets wider than
    /// the character who threw it and sticks in the wall behind them.
    /// <c>move_all_bullets.gml</c>'s <c>with(BladeB)</c> block has no
    /// <c>image_angle = direction</c> line either, so nothing puts it back.
    /// </para>
    /// <para>
    /// ⚠️ <b>The damage ramp is read from the magazine at the moment of the
    /// throw</b> and frozen onto the blade — above two thirds it is 17, below
    /// one third it is 8, and in between it is linear. The drain the blade then
    /// causes does not change the damage it already carries.
    /// </para>
    /// </remarks>
    private static void ThrowBlade(ref WorldState state, int slot)
    {
        ref var character = ref state.Characters[slot];
        ref var weapon = ref state.Weapons[slot];
        var stats = WeaponStats.For(WeaponKind.Blade);

        if (!weapon.ReadyToShoot || weapon.BladesOut >= BladeMaxBlades)
            return;

        weapon.Replenishing = false;

        var aim = character.AimDirection;

        var blade = CreateBlade(
            weapon.X + Gml.LengthDirX(BladeMuzzleOffset, aim),
            weapon.Y + Gml.LengthDirY(BladeMuzzleOffset, aim),
            aim,
            slot,
            character.Team);

        blade.HitDamage = BladeThrowDamage(weapon.AmmoCount);

        weapon.RefireTimer = stats.RefireTime;
        blade.Lifetime = BladeLife;
        weapon.ReloadTimer = BladeLife;
        weapon.BladesOut += 1;

        // `with(shot) hspeed += owner.hspeed` — a component write after the two
        // polar ones, which re-derives the pair and snaps it. Same shape as the
        // Minigun's, and note it is the *horizontal* alone: a falling Quote does
        // not throw the blade downwards.
        blade.Motion.HSpeed += character.HSpeed;

        state.SpawnProjectile(blade);

        weapon.ReadyToShoot = false;
    }

    /// <summary>What a blade thrown with this much in the magazine deals.</summary>
    public static double BladeThrowDamage(double ammoCount)
    {
        if (Gml.Greater(ammoCount, BladeHighAmmoThreshold))
            return BladeDamage;

        if (Gml.Less(ammoCount, BladeLowAmmoThreshold))
            return DepletedBladeDamage;

        return ((ammoCount - BladeLowAmmoThreshold)
                / (BladeHighAmmoThreshold - BladeLowAmmoThreshold)
                * (BladeDamage - DepletedBladeDamage))
               + DepletedBladeDamage;
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
    /// <c>Objects/Weapons/Scattergun.events/User Event 3.xml</c> — six pellets
    /// and eighteen random draws — and <c>Shotgun.events/User Event 3.xml</c>,
    /// which is the same event with four numbers changed.
    /// </summary>
    /// <remarks>
    /// <b>The two events are otherwise line for line identical</b>, down to the
    /// order of the three writes per pellet and the fifteen-pixel nudge, so they
    /// share a body rather than being copied. What differs:
    /// <list type="bullet">
    /// <item>five pellets rather than six, and so fifteen draws rather than
    /// eighteen;</item>
    /// <item>7 damage rather than 8;</item>
    /// <item>a tighter cone — <c>random(11) - 5.5</c> against
    /// <c>random(15) - 7.5</c>;</item>
    /// <item>⚠️ <b>no lifetime override at all.</b> The Scattergun ends every
    /// pellet with <c>alarm[0] = 35 * (...)</c>, the range compensation in
    /// <see cref="ScattergunPelletLifetime"/>; the Shotgun writes nothing, so its
    /// pellets keep the <c>Shot</c>'s own 40 and a running engineer's shots
    /// out-range a standing one's. Deleting the compensation would make the two
    /// weapons agree, which is exactly why it is worth saying.</item>
    /// </list>
    /// <para>
    /// The draw count is contract for both: every client re-derives the blast
    /// from the broadcast seed, so five pellets must cost exactly fifteen
    /// advances.
    /// </para>
    /// </remarks>
    private static void FireScattergun(
        ref WorldState state,
        int slot,
        WeaponKind kind,
        ref GmlRandom random)
    {
        ref var character = ref state.Characters[slot];
        ref var weapon = ref state.Weapons[slot];
        var stats = WeaponStats.For(kind);

        var scattergun = kind == WeaponKind.Scattergun;
        var pellets = scattergun ? ScattergunPellets : ShotgunPellets;
        var spread = scattergun ? ScattergunSpread : ShotgunSpread;
        var damage = scattergun ? ShotHitDamage : ShotgunHitDamage;
        var source = scattergun ? DamageSource.Scattergun : DamageSource.Shotgun;

        weapon.AmmoCount = Math.Max(0, weapon.AmmoCount - 1);

        var aim = character.AimDirection;
        var muzzleX = weapon.X;
        var muzzleY = weapon.Y;
        // Hoisted out of the repeat(), where the reference recomputes it. It
        // reads only the aim and the owner's hspeed, neither of which the loop
        // touches, and it draws nothing from the seeded stream — so this is the
        // same value every pellet and the same number of advances.
        //
        // The Shotgun has none, so its pellets keep the Shot's own 40.
        var lifetime = scattergun ? ScattergunPelletLifetime(aim, character.HSpeed) : ShotLifetime;

        for (var pellet = 0; pellet < pellets; pellet++)
        {
            var shot = CreateShot(
                muzzleX,
                muzzleY,
                ProjectileKind.Shot,
                source,
                aim,
                ScattergunPelletSpeed,
                slot,
                character.Team,
                ref random);

            shot.HitDamage = damage;

            // Order matters and so does the representation each step writes
            // through: a component write, then a speed write, then a direction
            // write. Each round trip re-derives the other pair and snaps it
            // (see Motion), so collapsing these into one polar assignment gives
            // different last bits and, near an axis, a different answer.
            shot.Motion.HSpeed += character.HSpeed;
            shot.Motion.Speed += random.Next(4) - 2;
            shot.Motion.Direction += random.Next(spread) - (spread / 2);

            // lengthdir, not cos/sin: the engine's snaps and a bare cosine does
            // not, and a pellet fired at 90 degrees must land exactly 15 pixels
            // above the muzzle.
            shot.X += Gml.LengthDirX(ScattergunNudge, shot.Motion.Direction);
            shot.Y += Gml.LengthDirY(ScattergunNudge, shot.Motion.Direction);

            shot.Lifetime = lifetime;

            state.SpawnProjectile(shot);
        }

        weapon.ReadyToShoot = false;
        weapon.RefireTimer = stats.RefireTime;
        weapon.ReloadTimer = stats.ReloadBuffer + stats.ReloadTime;
    }

    /// <summary>
    /// <c>Objects/Weapons/Rocketlauncher.events/User Event 3.xml</c>: one
    /// rocket, no random draws.
    /// </summary>
    /// <remarks>
    /// <b>The whole event is deterministic.</b> A rocket has no spread, so
    /// nothing between <c>random_set_seed</c> and the end of the event touches
    /// the stream — the seed on the wire is drawn, sent and then ignored by both
    /// sides. That is not an argument for skipping the draw: it comes out of the
    /// same <c>irandom(65535)</c> in User Event 1 that every other weapon uses.
    /// </remarks>
    private static void FireRocketlauncher(CollisionWorld world, ref WorldState state, int slot)
    {
        ref var character = ref state.Characters[slot];
        ref var weapon = ref state.Weapons[slot];
        var stats = WeaponStats.For(WeaponKind.Rocketlauncher);

        weapon.AmmoCount = Math.Max(0, weapon.AmmoCount - 1);

        var aim = character.AimDirection;

        // lengthdir, which snaps: a rocket fired straight up starts exactly
        // twenty pixels above the weapon rather than 3.5e-5 to one side.
        var muzzleX = weapon.X + Gml.LengthDirX(RocketMuzzleOffset, aim);
        var muzzleY = weapon.Y + Gml.LengthDirY(RocketMuzzleOffset, aim);

        var rocket = CreateRocket(muzzleX, muzzleY, aim, slot, character.Team);

        // collision_line_bulletblocking(other.x, other.y, newx, newy) — from the
        // *weapon* to the spawn point, evaluated inside a `with(oid)` where
        // `other` is the launcher. A rocket born the far side of something solid
        // goes off where it is instead of being deleted, so that a rocket jump
        // taken against a wall still happens on this tick.
        rocket.ExplodeImmediately = world.CollisionLineBlocksBullets(
            weapon.X,
            weapon.Y,
            muzzleX,
            muzzleY,
            state.BulletFilter);

        // The leash starts on the character rather than on the muzzle, so the
        // twenty pixels of offset are already spent out of the 800.
        rocket.LastKnownX = character.X;
        rocket.LastKnownY = character.Y;

        state.SpawnProjectile(rocket);

        weapon.ReadyToShoot = false;
        weapon.RefireTimer = stats.RefireTime;
        weapon.ReloadTimer = stats.ReloadBuffer + stats.ReloadTime;
    }

    /// <summary>
    /// <c>createShot</c> plus <c>Objects/Projectiles/Rocket.events/Create.xml</c>.
    /// </summary>
    private static ProjectileState CreateRocket(
        double x,
        double y,
        double direction,
        int ownerSlot,
        Team team)
    {
        var rocket = new ProjectileState
        {
            Kind = ProjectileKind.Rocket,
            X = x,
            Y = y,
            OwnerSlot = (byte)ownerSlot,
            Reflector = ProjectileState.NoOwner,
            Team = team,
            Source = DamageSource.Rocketlauncher,

            HitDamage = RocketHitDamage,
            Knockback = RocketKnockback,
            DistanceToTravel = RocketRange,
            KnockbackCapTimer = RocketKnockbackCapTicks,
            KnockbackEndTimer = RocketKnockbackEndTicks,
        };

        // No alarm[0]: a Rocket has no lifetime. It dies to a wall, to a
        // character, or to its own fade — never to a timer running out.
        //
        // direction first, then speed, as createShot writes them: with the speed
        // still zero the direction write leaves both components at zero, and it
        // is the speed write that turns the pair into a velocity.
        rocket.Motion.Direction = direction;
        rocket.ImageAngle = direction;
        rocket.Motion.Speed = RocketSpeed;

        return rocket;
    }

    /// <summary>
    /// <c>Objects/Weapons/Minegun.events/User Event 3.xml</c>: one mine, no
    /// random draws, and no collision test on the way out.
    /// </summary>
    /// <remarks>
    /// Deterministic like the Rocketlauncher's, and simpler still: there is no
    /// <c>collision_line_bulletblocking</c>, so a demoman with their back to a
    /// wall lobs a mine that starts inside it and is destroyed by the movement
    /// pass's muzzle test.
    /// <para>
    /// ⚠️ <b><c>oid.image_angle = 0</c>, written straight after
    /// <c>createShot</c> set it to the aim.</b> A mine is the one projectile
    /// whose bounding box is axis-aligned however it is thrown, and nothing ever
    /// writes the angle again — <c>move_all_bullets</c>'s mine block has no
    /// <c>image_angle = direction</c> where the Shot's and the Rocket's both do.
    /// </para>
    /// </remarks>
    private static void FireMinegun(ref WorldState state, int slot)
    {
        ref var character = ref state.Characters[slot];
        ref var weapon = ref state.Weapons[slot];
        var stats = WeaponStats.For(WeaponKind.Minegun);

        weapon.AmmoCount = Math.Max(0, weapon.AmmoCount - 1);

        var aim = character.AimDirection;
        var mine = new ProjectileState
        {
            Kind = ProjectileKind.Mine,
            X = weapon.X + Gml.LengthDirX(MineMuzzleOffset, aim),
            Y = weapon.Y + Gml.LengthDirY(MineMuzzleOffset, aim),
            OwnerSlot = (byte)slot,
            Reflector = ProjectileState.NoOwner,
            Team = character.Team,
            Source = DamageSource.Minegun,
            Knockback = MineKnockback,

            // Mine.events/Create.xml. A flying mine skips the outer quarter of
            // its own blast; landing clears this.
            SplashThreshold = true,

            // No alarm[0] is set, so it rests at GM8's -1 — and the reflected
            // mine's `alarm[0] < 0` re-arm in Projectiles.MoveMine reads the
            // resting value on the very tick it lands. A defaulted 0 would
            // skip that re-arm for one tick.
            Lifetime = Timers.Inactive,
        };

        mine.Motion.Direction = aim;
        mine.Motion.Speed = MineSpeed;

        // ...and then the launcher overwrites the angle with zero.
        mine.ImageAngle = 0;

        // No alarm[0]: a mine has no lifetime. It waits.
        state.SpawnProjectile(mine);

        weapon.Lobbed++;
        weapon.ReadyToShoot = false;
        weapon.RefireTimer = stats.RefireTime;
        weapon.ReloadTimer = stats.ReloadBuffer + stats.ReloadTime;
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
        // ⚠️ `(!taunting or player.class == CLASS_DEMOMAN)` — the exemption is
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
    /// <c>Objects/Weapons/Flamethrower.events/User Event 1.xml</c>: one flame.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>the first two lines run whether or not the weapon can
    /// fire.</b> <c>if (ammoCount &lt; 1.8) ammoCount -= 1.8;</c> — the
    /// reference's own comment is "prevent sputtering", and it drives an
    /// already-empty tank further negative on every tick the trigger is held, so
    /// that the refill in <see cref="RefillFlamethrower"/> has something to climb
    /// out of and the weapon cannot fire one frame in every few. The clamp back
    /// to zero is in that same Begin Step.
    /// <para>
    /// ⚠️ <b>no seed, and no <c>WEAPON_FIRE</c>.</b> The event calls
    /// <c>randomize()</c> — so like the Minigun and the Blade the Flamethrower is
    /// exempt from docs/SIMULATION.md's "a weapon puts its seed on the wire", and
    /// the spread is server-side RNG the port is free about. It draws from
    /// <see cref="WorldState.Random"/> rather than reseeding, because a
    /// simulation with no clock cannot <c>randomize()</c> and does not need to.
    /// </para>
    /// <para>
    /// ⚠️ <b>the muzzle carries <c>owner.equipmentOffset</c>, which is
    /// computed in the character's <em>Draw</em> event</b>
    /// (<c>Character.events/Draw.xml:170-190</c>) as the run-animation bob. It is
    /// the only place in the game where a drawing variable feeds the simulation.
    /// A headless server has no Draw event, so this port keeps it at its
    /// <c>Create</c> value of 0 — a divergence of up to two pixels on a running
    /// pyro's flame origin. Reproducing it would mean porting the run-animation
    /// state machine, which is drawing.
    /// </para>
    /// </remarks>
    private static void FireFlamethrower(CollisionWorld world, ref WorldState state, int slot)
    {
        ref var character = ref state.Characters[slot];
        ref var weapon = ref state.Weapons[slot];
        var stats = WeaponStats.For(WeaponKind.Flamethrower);

        var aim = character.AimDirection;
        var muzzleX = weapon.X + Gml.LengthDirX(FlameMuzzleOffset, aim);
        var muzzleY = weapon.Y + Gml.LengthDirY(FlameMuzzleOffset, aim);

        // Above the guard, and unconditional. See the remarks.
        if (Gml.Less(weapon.AmmoCount, FlameAmmoCost))
            weapon.AmmoCount -= FlameAmmoCost;

        if (!weapon.ReadyToShoot
            || !Gml.GreaterOrEqual(weapon.AmmoCount, FlameAmmoCost)
            || world.CollisionLineBlocksBullets(
                weapon.X, weapon.Y, muzzleX, muzzleY, state.BulletFilter))
            return;

        // sign(random(2) - 1) * power(random(3), 1.8), then a factor of
        // `1 - hspeed/owner.basemaxspeed` — where `hspeed` is the *weapon's*, and
        // nothing in the game ever gives a weapon instance a speed. So that
        // factor is exactly 1; it is not transcribed, and it is a bug in the
        // reference that was meant to read `owner.hspeed`.
        var spread = Gml.Sign(state.Random.Next(2) - 1) * Math.Pow(state.Random.Next(3), 1.8);

        var flame = CreateBurning(
            ProjectileKind.Flame,
            muzzleX,
            muzzleY,
            aim + spread,
            FlameSpeed + state.Random.Next(FlameSpeedJitter),
            slot,
            character.Team,
            DamageSource.Flamethrower,
            ref state.Random);

        // ⚠️ `motion_add(owner.direction, owner.speed)` — the owner's whole
        // velocity vector, where a pellet and a needle take only its hspeed. A
        // falling pyro adds their fall to every flame.
        flame.Motion.MotionAdd(character.Motion.Direction, character.Motion.Speed);

        state.SpawnProjectile(flame);

        weapon.ReadyToShoot = false;
        weapon.IsRefilling = false;
        weapon.AmmoCount -= FlameAmmoCost;

        // A tank that just ran dry waits fourteen ticks rather than one, which is
        // what stops an empty pyro flickering out a flame every other frame.
        weapon.RefireTimer = Gml.Greater(weapon.AmmoCount, 0)
            ? stats.RefireTime
            : stats.ReloadBuffer * 2;

        weapon.ReloadTimer = stats.ReloadBuffer;
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
    /// ⚠️ <b>the guard is <c>ammoCount &lt;= maxAmmo</c>, so the tank
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
            // ⚠️ Clamped at *both* ends, where the Flamethrower's and the
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

        // ⚠️ The Minigun's Begin Step has a third line the Flamethrower's does
        // not: `if(!readyToShoot and alarm[5] < 25 and !isRefilling) alarm[5] += 1`.
        //
        // ⚠️ **It is not what stops a held trigger refilling the tank** — that
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
        // ⚠️ `!isRefilling` is unreachable and transcribed anyway. For it to
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

    /// <summary>
    /// <c>Objects/Weapons/Flamethrower.events/User Event 2.xml</c>: the
    /// airblast, and the flare that rides on it.
    /// </summary>
    /// <remarks>
    /// <b>The blast's reach is only a rotated box and a circle — there is no
    /// cone test.</b> <c>blastAngle = 75</c> is set in the Create event and
    /// never read here, and the per-victim <c>angle</c> the event computes is
    /// dead too. An <c>AirBlastO</c> is created 25px ahead of the weapon along
    /// the aim, rotated to the aim (aim + 180 with <c>image_xscale = -1</c>
    /// when the pyro faces left — the weapon's xscale, which End Step writes in
    /// the same block as <c>currentWeapon.x</c>, so it is
    /// <see cref="CharacterState.Facing"/>), and every victim is tested with
    /// <c>collision_circle(x, y, r, poof, false, true)</c> centred on the
    /// <em>victim's</em> position against the poof's reported box.
    /// <para>
    /// <b>The poof is not pooled.</b> Its whole simulated effect happens inside
    /// this event; afterwards it is animation (Step counts <c>image_index</c>
    /// to 5) and an <c>instance_destroy</c> on touching an Obstacle, none of
    /// which the server can observe — and a later blast tests only against its
    /// <em>own</em> poof. The <c>motion_add(owner.direction, owner.speed)</c>
    /// it receives moves only those unobservable later frames.
    /// </para>
    /// <para>
    /// ⚠️ <b>The flare leaves before the reflect loops</b>, so <c>with(Flare)</c>
    /// walks the flare just fired — and skips it on team. And its gate reads
    /// <c>owner.keyState</c>, not the down-or-pressed union — the same
    /// distinction as the medic's uber pop.
    /// </para>
    /// <para>
    /// The <c>LooseSheet</c> and <c>Gib</c> loops are client-side debris a
    /// headless server has none of, and are not transcribed.
    /// </para>
    /// </remarks>
    private static void AirBlast(CollisionWorld world, ref WorldState state, int slot)
    {
        ref var character = ref state.Characters[slot];
        ref var weapon = ref state.Weapons[slot];

        if (!weapon.ReadyToBlast || !Gml.GreaterOrEqual(weapon.AmmoCount, BlastAmmoCost))
            return;

        var aim = character.AimDirection;
        var poofX = weapon.X + Gml.LengthDirX(FlameMuzzleOffset, aim);
        var poofY = weapon.Y + Gml.LengthDirY(FlameMuzzleOffset, aim);

        // if (image_xscale == 1) { poof.image_angle = aim } else { aim + 180,
        // xscale -1 } — the weapon's xscale is the owner's facing (End Step
        // writes both from the same test).
        var facing = character.Facing;
        var poofBox = Aabb.ForRotatedSpriteReported(
            AirBlastSpriteBbox,
            AirBlastOriginX,
            AirBlastOriginY,
            poofX,
            poofY,
            facing >= 0 ? aim : aim + 180,
            facing);

        // --- the flare -------------------------------------------------------
        if ((character.KeyState & PlayerKeys.Attack) != 0
            && Gml.GreaterOrEqual(weapon.AmmoCount, FlareAmmoThreshold)
            && weapon.ReadyToFlare
            && !world.CollisionLineBlocksBullets(
                weapon.X, weapon.Y, poofX, poofY, state.BulletFilter))
        {
            // The same x+lengthdir_x(25, aim) as the poof, and unlike the flame
            // there is no equipmentOffset on the y.
            var flare = CreateBurning(
                ProjectileKind.Flare,
                poofX,
                poofY,
                aim,
                FlareSpeed,
                slot,
                character.Team,
                DamageSource.Flare,
                ref state.Random);

            state.SpawnProjectile(flare);

            weapon.ReadyToFlare = false;
            weapon.AmmoCount -= FlareAmmoCost;
            weapon.FlareTimer = FlareReloadTime;
        }

        // --- reflects: rockets, then flares, then mines ----------------------
        for (var i = 0; i < state.LiveProjectileCount; i++)
        {
            ref var rocket = ref state.Projectiles[state.LiveProjectileSlot(i)];

            if (rocket.Kind != ProjectileKind.Rocket
                || rocket.Team == character.Team
                || !Aabb.CircleHits(rocket.X, rocket.Y, BlastReflectRadius, poofBox))
                continue;

            rocket.OwnerSlot = (byte)slot;
            rocket.Team = character.Team;
            rocket.Source = DamageSource.ReflectedRocket;

            // hitDamage = 25 and explosionDamage = 30 are the Rocket's own
            // Create values written back; the first is per-instance here and
            // the second is a per-kind lookup that already answers 30.
            rocket.HitDamage = 25;

            // knockback = 8 with both alarms re-armed — twice and nearly three
            // times the fresh rocket's 20/30, so a reflected rocket shoves for
            // longer than a fired one.
            rocket.Knockback = ReflectedRocketKnockback;
            rocket.KnockbackCapTimer = ReflectedKnockbackCapDelay;
            rocket.KnockbackEndTimer = ReflectedKnockbackEndDelay;

            // motion_set(aim, speed): the direction turns, the speed stays.
            rocket.Motion.Direction = aim;
        }

        for (var i = 0; i < state.LiveProjectileCount; i++)
        {
            ref var flare = ref state.Projectiles[state.LiveProjectileSlot(i)];

            if (flare.Kind != ProjectileKind.Flare
                || flare.Team == character.Team
                || !Aabb.CircleHits(flare.X, flare.Y, BlastReflectRadius, poofBox))
                continue;

            flare.OwnerSlot = (byte)slot;
            flare.Team = character.Team;
            flare.Source = DamageSource.ReflectedFlare;

            // alarm[0] = 40: a reflected flare gets its whole life back.
            flare.Lifetime = ReflectedFlareLife;

            flare.Motion.Direction = aim;
        }

        for (var i = 0; i < state.LiveProjectileCount; i++)
        {
            ref var mine = ref state.Projectiles[state.LiveProjectileSlot(i)];

            if (mine.Kind != ProjectileKind.Mine
                || mine.Team == character.Team
                || !Aabb.CircleHits(mine.X, mine.Y, BlastReflectRadius, poofBox))
                continue;

            // motion_set(aim, max(speed, blastStrength/3)) — a resting stuck
            // mine leaves at 28/3, a faster one keeps its speed.
            var speed = Math.Max(mine.Motion.Speed, BlastStrength / 3);
            mine.Motion.Direction = aim;
            mine.Motion.Speed = speed;

            // reflector = other.ownerPlayer — the pyro's *player*, so it
            // survives the pyro's death and is renumbered on a leave.
            mine.Reflector = (byte)slot;

            if (mine.Stickied)
            {
                // 0.65 is one of the literals GM8 parses a ULP high — Gml.
                mine.Motion.Speed *= Gml.PointSixFive;

                // The surface normal, from four place_meeting(±3, Obstacle)
                // probes — the map only, like the landing that stuck it.
                var probe = mine;
                probe.Y = mine.Y - 3;
                double dy = world.PlaceFreeInMask(probe.Box) ? 0 : 1;
                probe.Y = mine.Y + 3;
                dy -= world.PlaceFreeInMask(probe.Box) ? 0 : 1;
                probe.Y = mine.Y;
                probe.X = mine.X - 3;
                double dx = world.PlaceFreeInMask(probe.Box) ? 0 : 1;
                probe.X = mine.X + 3;
                dx -= world.PlaceFreeInMask(probe.Box) ? 0 : 1;

                var length = Math.Sqrt((dx * dx) + (dy * dy));
                if (Gml.Greater(length, 0))
                {
                    dx /= length;
                    dy /= length;
                    var normalSpeed = (dx * mine.Motion.HSpeed) + (dy * mine.Motion.VSpeed);

                    // Reflect only a velocity *into* the surface — component
                    // writes, so no snap, exactly the reference's hspeed -= ...
                    if (Gml.Less(normalSpeed, 0))
                    {
                        mine.Motion.HSpeed -= 2 * normalSpeed * dx;
                        mine.Motion.VSpeed -= 2 * normalSpeed * dy;
                    }
                }

                mine.Stickied = false;
            }
        }

        // --- the characters: shoves and extinguishes -------------------------
        for (var victimSlot = 0; victimSlot < WorldState.MaxSlots; victimSlot++)
        {
            if (!state.Alive[victimSlot])
                continue;

            ref var victim = ref state.Characters[victimSlot];

            // dist is measured from the *weapon* (`other.x` inside the with),
            // while the circle is centred on the victim — the Medigun's
            // two-instances trap again.
            var dist = Gml.PointDistance(weapon.X, weapon.Y, victim.X, victim.Y);

            if (!Aabb.CircleHits(victim.X, victim.Y, BlastCharacterRadius, poofBox))
                continue;

            if (victim.Team != character.Team)
            {
                // The shove. Note what is *not* guarded: no ubered test and no
                // hp test — an ubered enemy is thrown, and so is one who is
                // already dead but not yet noticed by End Step.
                victim.Motion.MotionAdd(aim, CharacterBlastStrength * (1 - (dist / BlastDistance)));
                victim.Motion.VSpeed -= 2;
                victim.MoveStatus = 3;

                if (victim.LastDamageDealer != slot && victim.LastDamageDealer != victimSlot)
                {
                    victim.SecondToLastDamageDealer = victim.LastDamageDealer;
                    victim.AssistTimer = victim.FinishOffTimer;
                }

                victim.FinishOffTimer = GameConstants.AssistTime;
                victim.LastDamageDealer = (byte)slot;

                // lastDamageSource = -1: a shove has no weapon, so a victim
                // blasted into a pit is credited to the pyro with no source.
                victim.LastDamageSource = DamageSource.None;
            }
            else if (Gml.Greater(victim.BurnIntensity, 0) || Gml.Greater(victim.BurnDuration, 0))
            {
                // The extinguish, which includes the pyro putting themself out.
                // Each of the victim's drawn flames comes loose as a real Flame
                // owned by the *blasting* pyro, flying along the aim.
                //
                // The reference places them at the victim's position plus
                // `flameArray` offsets — per-character randoms rolled once in
                // the Create event, uniform over the bbox half-extents. Both
                // that roll and this one are off the unseeded stream, so the
                // port rolls fresh offsets here rather than carrying five
                // doubles per character for four phases between fires.
                var box = victim.Box;
                var halfWidth = (box.Right - box.Left) / 2.0;
                var halfHeight = (box.Bottom - box.Top) / 2.0;

                // for (i = 0; i < realnumflames; ...) — a fractional bound, so
                // 3.7 flames means four. Recomputed by End Step *after* this
                // tick's burn decay, so the value read here is one tick stale.
                for (double i = 0; Gml.Less(i, victim.RealNumFlames); i++)
                {
                    var offsetX = -halfWidth + state.Random.Next(2 * halfWidth);
                    var offsetY = -halfHeight + state.Random.Next(2 * halfHeight);

                    var loose = CreateBurning(
                        ProjectileKind.Flame,
                        victim.X + offsetX,
                        victim.Y + offsetY,
                        aim,
                        FlameSpeed + state.Random.Next(2.5),
                        slot,
                        character.Team,
                        DamageSource.Flamethrower,
                        ref state.Random);

                    // instance_create, not createShot — so image_angle is never
                    // written and the box stays unrotated.
                    loose.ImageAngle = 0;

                    // with(f) motion_add(direction, speed): its own vector added
                    // to itself, so a loose flame flies at twice the rolled
                    // speed. The reference says what it says.
                    loose.Motion.MotionAdd(loose.Motion.Direction, loose.Motion.Speed);

                    state.SpawnProjectile(loose);
                }

                victim.BurnIntensity = 0;
                victim.BurnDuration = 0;
                victim.BurnedBy = CharacterState.Nobody;
                victim.AfterburnSource = DamageSource.None;
            }
        }

        // --- Finish (justBlast is the recoil animation and is not modelled) --
        weapon.ReadyToBlast = false;
        weapon.ReadyToShoot = false;
        weapon.ReloadTimer = BlastReloadTime;
        weapon.IsRefilling = false;
        weapon.BlastTimer = BlastReloadTime;
        weapon.RefireTimer = BlastNoFlameTime;
        weapon.AmmoCount -= BlastAmmoCost;
    }

    /// <summary>
    /// <c>createShot</c> plus a <c>BurningProjectile</c> child's Create event
    /// and the <c>event_inherited()</c> below it — a <c>Flame</c> or a
    /// <c>Flare</c>.
    /// </summary>
    /// <remarks>
    /// The parent's Create event is where <c>alarm[0]</c> and the one-in-eight
    /// <c>perseverant</c> draw live, and the child's is where the six numbers
    /// <see cref="FlameStats"/> holds are set. Both run, child first.
    /// </remarks>
    private static ProjectileState CreateBurning(
        ProjectileKind kind,
        double x,
        double y,
        double direction,
        double speed,
        int ownerSlot,
        Team team,
        DamageSource source,
        ref GmlRandom random)
    {
        var flame = new ProjectileState
        {
            Kind = kind,
            X = x,
            Y = y,
            OwnerSlot = (byte)ownerSlot,
            Reflector = ProjectileState.NoOwner,
            Team = team,
            Source = source,
            HitDamage = FlameStats.HitDamage(kind),
            Lifetime = FlameStats.Life(kind),

            // random(1) < 1/8, off the global stream. A perseverant flame is
            // never destroyed by burning somebody, so it can burn a whole team.
            Perseverant = random.Next(1) < FlameStats.PerseverantChance,
        };

        flame.Motion.Direction = direction;
        flame.ImageAngle = direction;
        flame.Motion.Speed = speed;

        return flame;
    }

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

    /// <summary>
    /// How long a scattergun pellet lives, given the shooter's aim and
    /// horizontal speed.
    /// </summary>
    /// <remarks>
    /// <b>A range compensation, and it looks like a mistake until you see what
    /// it is for.</b> Every pellet inherits the shooter's <c>hspeed</c>, so a
    /// scout running forwards throws pellets that travel further per tick and
    /// would otherwise out-range a standing one. The factor
    /// <c>(min(1, forward/actual) - 1)/2 + 1</c> shortens the fuse by up to
    /// half when the inherited speed helps, and is exactly 1 when it does not.
    /// <para>
    /// The reference spells it out at
    /// <c>Objects/Weapons/Scattergun.events/User Event 3.xml</c>, and it is
    /// transcribed rather than simplified: <c>abs</c> appears on the numerator
    /// and on the whole denominator but not on <c>cos</c> inside the
    /// denominator, so the two are not the same quantity and cancelling them
    /// changes the answer whenever <c>hspeed</c> opposes the aim.
    /// </para>
    /// <para>
    /// The apparent division by zero at a vertical aim does not happen: GM8's
    /// <c>cos(degtorad(90))</c> is 6.1e-17 rather than 0, so numerator and
    /// denominator are both tiny and the ratio is 1. The port's is the same
    /// 6.1e-17, for the same reason — <see cref="Gml.DegToRad"/> is one multiply.
    /// </para>
    /// </remarks>
    public static int ScattergunPelletLifetime(double aimDirection, double hspeed)
    {
        var cos = Math.Cos(Gml.DegToRad(aimDirection));
        var forward = Math.Abs(cos) * ScattergunPelletSpeed;
        var actual = Math.Abs((cos * ScattergunPelletSpeed) + hspeed);

        return Timers.SetAlarm(35 * (((Math.Min(1, forward / actual) - 1) / 2) + 1));
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

    /// <summary>
    /// <c>createShot(..., BladeB, ...)</c> plus
    /// <c>BladeB.events/Create.xml</c>, with the <c>image_angle = 0</c> the
    /// weapon writes immediately afterwards.
    /// </summary>
    private static ProjectileState CreateBlade(
        double x,
        double y,
        double direction,
        int ownerSlot,
        Team team)
    {
        var blade = new ProjectileState
        {
            Kind = ProjectileKind.Blade,
            X = x,
            Y = y,
            OwnerSlot = (byte)ownerSlot,
            Reflector = ProjectileState.NoOwner,
            Team = team,
            Source = DamageSource.Blade,

            // BladeB.events/Create.xml:2. Always overwritten by the weapon two
            // lines later, and transcribed because a blade created any other
            // way would keep it.
            HitDamage = 5,
            Lifetime = BladeLife,
        };

        blade.Motion.Direction = direction;
        blade.Motion.Speed = BladeSpeed;

        // ⚠️ Not `direction`. createShot writes the aim here and User Event 2
        // overwrites it with 0 on the very next line — see ThrowBlade.
        blade.ImageAngle = 0;

        return blade;
    }

    private static ProjectileState CreateShot(
        double x,
        double y,
        ProjectileKind kind,
        DamageSource source,
        double direction,
        double speed,
        int ownerSlot,
        Team team,
        ref GmlRandom random)
    {
        var shot = new ProjectileState
        {
            Kind = kind,
            X = x,
            Y = y,
            OwnerSlot = (byte)ownerSlot,
            Reflector = ProjectileState.NoOwner,
            Team = team,
            Source = source,
        };

        // instance_create runs the Create event before createShot's own `with`
        // block, so the choose() draw comes first in the seeded stream. Its
        // position in the order is what the client re-derives against.
        shot.Perseverant = random.ChooseIndex(3) == 2;
        shot.HitDamage = ShotHitDamage;
        shot.Lifetime = ShotLifetime;

        // direction first, then speed: with speed still zero the direction write
        // leaves both components at zero, and it is the speed write that turns
        // the pair into a velocity. Writing speed first would point the pellet
        // along the previous direction, which for a fresh instance is 0 degrees.
        shot.Motion.Direction = direction;
        shot.ImageAngle = direction;
        shot.Motion.Speed = speed;

        return shot;
    }
}
