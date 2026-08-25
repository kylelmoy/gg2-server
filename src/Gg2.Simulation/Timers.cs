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

        // --- alarm 5 is a weapon's reload, and its body differs per weapon -----
        //
        // Scattergun.events/Alarm 5.xml puts a round back and re-arms itself;
        // Minigun's and Flamethrower's refill instead; Blade's replenishes
        // bubbles. There is no shared body to run, so WeaponState.ReloadTimer is
        // advanced by the weapon that owns it, in stage 3. Nothing sets it yet,
        // so nothing is being dropped here.

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
