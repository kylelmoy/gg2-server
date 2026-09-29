using GG2.Protocol;

namespace GG2.Simulation;

public static partial class Weapons
{
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
}
