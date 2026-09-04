using Gg2.Protocol;

namespace Gg2.Simulation;

public static partial class Weapons
{
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
}
