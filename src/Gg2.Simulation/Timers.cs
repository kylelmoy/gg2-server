using Gg2.Protocol;

namespace Gg2.Simulation;

/// <summary>
/// The alarm phase: every countdown in the world, advanced once, firing what it
/// expires.
/// </summary>
/// <remarks>
/// Game Maker runs alarms in their own event stage between Begin Step and Step,
/// so this sits between them in <see cref="MatchSimulation.Tick"/>. CLAUDE.md
/// puts alarms on the list of Game Maker showing through, so the reference's
/// <c>alarm[n]</c> are named counters here — but the <em>phase</em> is
/// observable and stays.
/// </remarks>
public static class Timers
{
    /// <summary>
    /// Advances one counter and says whether it just expired.
    /// </summary>
    /// <remarks>
    /// GM8's exact rule, from OpenGMK's <c>run_alarms</c>: an alarm at or above
    /// zero is decremented, and the event runs only when the decrement lands on
    /// <em>exactly</em> zero. Three consequences that a plainer
    /// "count down and fire at zero" gets wrong:
    /// <list type="bullet">
    /// <item>
    /// A counter set to <c>n</c> fires <c>n</c> ticks later, not <c>n+1</c>.
    /// </item>
    /// <item>
    /// A counter set to <b>0</b> never fires at all — it goes straight to -1.
    /// <c>Character.events/Create.xml</c> writes <c>alarm[1] = 0</c>, and that
    /// is a way of saying "off", not "fire immediately".
    /// </item>
    /// <item>
    /// After firing, the counter rests at 0 rather than -1 and is consumed on
    /// the following tick. Leaving it at 0 here is what makes it fire once.
    /// </item>
    /// </list>
    /// </remarks>
    public static bool Advance(ref int timer)
    {
        if (timer < 0)
            return false;

        timer--;
        return timer == 0;
    }

    /// <summary>
    /// The whole number of ticks GM8 stores when an alarm is assigned a real.
    /// </summary>
    /// <remarks>
    /// <b>Most alarms in GG2 are set to integers and this changes nothing for
    /// them. One is not.</b>
    /// <c>Objects/Weapons/Scattergun.events/User Event 3.xml</c> ends every
    /// pellet with
    /// <c>alarm[0] = 35 * ((min(1, ...) - 1)/2 + 1)</c>, which is 35 times a
    /// factor in <c>[0.5, 1]</c> — a whole number only when the shooter's
    /// <c>hspeed</c> is zero. A counter that stepped down from 30.45 would pass
    /// 0.45 and then -0.55 without ever landing on zero, and
    /// <see cref="Advance(ref int)"/>'s "fires only on exactly zero" rule would
    /// mean the pellet never expired.
    /// <para>
    /// ⚠️ <b>Half to even, and this specific choice is not measured.</b> GM8's
    /// alarms are a Delphi array of integers and an assignment converts; the
    /// rounding it uses is the one thing about
    /// <see cref="Timers"/> still taken from the OpenGMK transcription rather
    /// than from the running game, along with the phase itself. Half to even is
    /// this codebase's default for every GML-side rounding except the wire's
    /// (CLAUDE.md's table), so it is what the transcription assumes. A pellet
    /// hits within a handful of ticks and a whole tick of lifetime is only
    /// visible on a shot that reaches its maximum range, so the cost of being
    /// wrong is small — but it <em>is</em> observable, and it is on
    /// docs/SIMULATION.md's list of things still to measure.
    /// </para>
    /// </remarks>
    public static int SetAlarm(double ticks) => (int)Gml.Round(ticks);

    /// <summary>
    /// Advances every counter in the world, in GM8's own order.
    /// </summary>
    /// <remarks>
    /// <b>Alarm index first, instance second.</b> <c>run_alarms</c> loops
    /// <c>for alarm_id in 0..=11</c> on the outside and over instances on the
    /// inside, so every instance's alarm 0 fires before any instance's alarm 1 —
    /// not one instance's alarms in order and then the next's. Nothing here
    /// crosses instances yet, so the difference is invisible today; it will stop
    /// being invisible the moment an expiry damages somebody.
    /// </remarks>
    public static void Advance(ref WorldState state)
    {
        // --- alarm 0: afterburn intensity decay, and the weapon refire --------
        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
        {
            if (!state.Alive[slot])
                continue;

            ref var character = ref state.Characters[slot];

            // Character.events/Alarm 0.xml, "Afterburn intensity stuff".
            if (Advance(ref character.AfterburnDecayTimer))
                character.IntensityDecay = character.BurnIntensity / CharacterState.BurnDecayDuration;

            // Weapon.events/Alarm 0.xml. Every weapon inherits it, including the
            // ones that override the event — GM8 runs the parent's actions
            // through the action_inherited their event carries.
            if (Advance(ref state.Weapons[slot].RefireTimer))
                state.Weapons[slot].ReadyToShoot = true;
        }

        // Shot.events/Alarm 0.xml is a bare instance_destroy, and every other
        // projectile's alarm 0 is a lifetime too. The projectiles come after
        // the characters because their instances are younger — a pellet is
        // created by a character that already existed — and GM8 runs one alarm
        // index across instances in id order.
        //
        // The index is advanced only when nothing was freed: FreeProjectile
        // compacts the creation-order array, so the entry that slid into i is
        // the next one to look at and i++ would step over it.
        for (var i = 0; i < state.LiveProjectileCount;)
        {
            var handle = state.LiveProjectileHandle(i);

            if (Advance(ref state.Projectiles[handle.Index].Lifetime))
                state.FreeProjectile(handle);
            else
                i++;
        }

        // --- alarm 1: intel grabability --------------------------------------
        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
        {
            if (!state.Alive[slot])
                continue;

            if (Advance(ref state.Characters[slot].IntelGrabTimer))
                state.Characters[slot].CanGrabIntel = true;
        }

        // --- alarm 2: end of uber --------------------------------------------
        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
        {
            if (!state.Alive[slot])
                continue;

            if (Advance(ref state.Characters[slot].UberTimer))
                state.Characters[slot].Ubered = false;
        }

        // --- alarm 3: finish-off expiration -----------------------------------
        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
        {
            if (!state.Alive[slot])
                continue;

            ref var character = ref state.Characters[slot];

            // Note it clears the *second* dealer as well as the last one: once
            // the finish-off window has closed there is no assist to award
            // either, and Alarm 4 may never have run.
            if (Advance(ref character.FinishOffTimer))
            {
                character.LastDamageDealer = CharacterState.Nobody;
                character.LastDamageSource = DamageSource.None;
                character.SecondToLastDamageDealer = CharacterState.Nobody;
            }
        }

        // --- alarm 4: kill assist expiration ----------------------------------
        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
        {
            if (!state.Alive[slot])
                continue;

            if (Advance(ref state.Characters[slot].AssistTimer))
                state.Characters[slot].SecondToLastDamageDealer = CharacterState.Nobody;
        }

        // --- alarm 5: a weapon's reload, and its body differs per weapon ------
        //
        // Four weapons share one body to the line — Scattergun, Shotgun,
        // Rocketlauncher and Minegun each put a single round back and re-arm the
        // alarm if the magazine is still short. The Revolver refills the whole
        // magazine at once. Minigun and Flamethrower have no reload cycle at all
        // (reloadTime 0); Rifle, Medigun and Blade have their own bodies and
        // arrive with their weapons.
        //
        // Nothing sets ReloadTimer for a weapon not listed below, so the switch
        // cannot silently drop one: an unimplemented weapon's counter is never
        // started.
        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
        {
            if (!state.Alive[slot])
                continue;

            ref var weapon = ref state.Weapons[slot];

            if (!Advance(ref weapon.ReloadTimer))
                continue;

            var playerClass = state.Characters[slot].Class;
            var stats = WeaponStats.For(playerClass);

            switch (WeaponStats.KindFor(playerClass))
            {
                case WeaponKind.Scattergun:
                case WeaponKind.Shotgun:
                case WeaponKind.Rocketlauncher:
                case WeaponKind.Minegun:
                    // Two separate `if`s in the reference, not an else: the
                    // round goes in first and the re-arm then asks about the
                    // *new* count, so the last round loaded does not schedule a
                    // pointless reload.
                    if (Gml.Less(weapon.AmmoCount, stats.MaxAmmo))
                        weapon.AmmoCount += 1;

                    if (Gml.Less(weapon.AmmoCount, stats.MaxAmmo))
                        weapon.ReloadTimer = stats.ReloadTime;

                    break;

                case WeaponKind.Revolver:
                    // The whole cylinder at once, and it does not re-arm.
                    if (Gml.Less(weapon.AmmoCount, stats.MaxAmmo))
                        weapon.AmmoCount = stats.MaxAmmo;

                    break;
            }
        }

        // --- alarm 7: cloak flicker -------------------------------------------
        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
        {
            if (!state.Alive[slot])
                continue;

            ref var character = ref state.Characters[slot];

            if (!Advance(ref character.CloakFlickerTimer))
                continue;

            // Guarded on the class in the reference too, so a non-spy that
            // somehow had the timer running keeps its alpha.
            if (character.Class != PlayerClass.Spy)
                continue;

            character.CloakFlicker = false;
            if (character.Cloak)
                character.CloakAlpha = 0;
        }
    }
}
