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
    /// A fired counter <b>rests at 0 for the remainder of the frame its event
    /// ran on</b>, and reaches -1 only on the <em>next</em> frame's alarm stage.
    /// It is never wiped before the body runs, which is what makes a handler
    /// that re-arms its own alarm work: <c>Medic.events/Alarm 11.xml</c> ends
    /// with <c>alarm[11] = 30</c> and <c>Scattergun.events/Alarm 5.xml</c> with
    /// <c>alarm[5] = reloadTime</c>, and every caller here re-arms after
    /// <see cref="Advance(ref int)"/> returns, overwriting the 0.
    /// </item>
    /// </list>
    /// <para>
    /// ⚠️ <b>MEASURED 2026-08-27 against the running game, after this file spent
    /// a stage asserting the opposite.</b> The code wrote <c>Inactive</c> the
    /// instant a counter expired, on the reasoning that GM8 sets -1 before the
    /// event body. It does not: a fired alarm reads <b>0</b> for the rest of
    /// that frame. <c>docs/SIMULATION.md</c>'s own pseudocode had it right and
    /// the code was changed away from it.
    /// </para>
    /// <para>
    /// ⚠️ <b>One site makes it observable, and it is a whole tick of behaviour.</b>
    /// <c>Character.events/Begin Step.xml:67</c> gates the afterburn intensity
    /// decay on <c>alarm[0] == -1</c> <em>exactly</em>, and Begin Step runs
    /// <em>before</em> the alarm stage. So with the eager write the frame after
    /// a burn's alarm fired already read -1 and faded; with the engine's timing
    /// it reads 0, and the fade starts a frame later. <b>The port was starting
    /// every burn's decay a tick early.</b>
    /// </para>
    /// <para>
    /// Firing itself is unaffected either way — a counter resting at 0
    /// decrements to -1 and returns false, because the fire test is
    /// <em>exactly</em> zero and -1 is not it.
    /// </para>
    /// </remarks>
    public static bool Advance(ref int timer)
    {
        if (timer < 0)
            return false;

        timer--;

        // ⚠️ The counter is deliberately LEFT AT ZERO here. GM8 does not write
        // -1 until the next frame's alarm stage, and the difference is one tick
        // of afterburn decay — see the remarks above. A `timer = Inactive;` on
        // this line reads like tidiness and is a measured behaviour change.
        return timer == 0;
    }

    /// <summary>GM8's resting alarm value. The same -1 as <see cref="WeaponState.Inactive"/>.</summary>
    public const int Inactive = -1;

    /// <summary>
    /// What a counter reads for the remainder of the frame its event ran on,
    /// before the next frame's alarm stage takes it to <see cref="Inactive"/>.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A spent counter is not immediately an inactive one</b>, and the
    /// difference is a whole tick of afterburn decay — see
    /// <see cref="Advance(ref int)"/>. It exists as a name because seven test
    /// sites assert "this alarm has been spent" on the tick it fired, and a
    /// literal <c>0</c> there reads like a magic number rather than like the
    /// measured rule it is.
    /// </remarks>
    public const int JustFired = 0;

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
    /// (docs/TRAPS-SIMULATION.md's alarm table), so it is what the transcription assumes. A pellet
    /// hits within a handful of ticks and a whole tick of lifetime is only
    /// visible on a shot that reaches its maximum range, so the cost of being
    /// wrong is small — but it <em>is</em> observable, and it is on
    /// docs/SIMULATION.md's list of things still to measure.
    /// </para>
    /// </remarks>
    public static int SetAlarm(double ticks) => (int)Gml.Round(ticks);

    /// <summary>
    /// The overload for a caller with no spawn points and nothing to do with
    /// the tick's events — most tests. A respawn alarm that fires with no spawn
    /// points to resolve against spawns nobody, so only use this where no
    /// player's <see cref="PlayerState.RespawnTimer"/> is running.
    /// </summary>
    public static void Advance(ref WorldState state)
    {
        var events = default(TickEvents);
        Advance(ref state, null, ref events);
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
    /// <param name="spawnPoints">
    /// The map's spawn points, for the respawn stage — the one alarm whose
    /// event creates an instance. Null spawns nobody.
    /// </param>
    /// <param name="events">Where a respawn is reported, for the broadcast.</param>
    public static void Advance(ref WorldState state, Maps.SpawnPoints? spawnPoints, ref TickEvents events)
    {
        // --- alarm 0: a dropped briefcase returning itself --------------------
        //
        // Intelligence.events/Alarm 0.xml. First in the whole stage because both
        // briefcases are created by their bases at room start, before any
        // character exists — and GM8 runs one alarm index across every instance
        // in id order. See Intel.ReturnStep.
        Intel.ReturnStep(ref state, ref events);

        // Generator.events/Alarm 0.xml: `isShieldRegenerating = true`. Beside
        // the briefcase because a generator is a map object too, created at room
        // start and so older than anything a match creates. Which of the two
        // runs first is unobservable — no map places both.
        if (state.RedGenerator.Exists && Advance(ref state.RedGenerator.RegenerationAlarm))
            Generators.ShieldBufferElapsed(ref state.RedGenerator);

        if (state.BlueGenerator.Exists && Advance(ref state.BlueGenerator.RegenerationAlarm))
            Generators.ShieldBufferElapsed(ref state.BlueGenerator);

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

        // SentryTurret.events/Alarm 0.xml: `readyToShoot = true`. The turret's
        // alarm exists only while the turret does. Its id sits among the
        // projectiles' — both are mid-match creations — but neither reads the
        // other, so the order inside the alarm-0 stage is not observable.
        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
        {
            if (!state.HasSentry[slot] || !state.Sentries[slot].HasTurret)
                continue;

            if (Advance(ref state.Sentries[slot].RefireTimer))
                state.Sentries[slot].ReadyToShoot = true;
        }

        // Shot.events/Alarm 0.xml is a bare instance_destroy, and every other
        // projectile's alarm 0 is a lifetime too — except the Mine's, whose
        // Alarm 0 is `reflector = noone;`: the 30-tick window in which a
        // reflected mine credits the pyro closes, and the mine lives on. The
        // projectiles come after the characters because their instances are
        // younger — a pellet is created by a character that already existed —
        // and GM8 runs one alarm index across instances in id order.
        //
        // The index is advanced only when nothing was freed: FreeProjectile
        // compacts the creation-order array, so the entry that slid into i is
        // the next one to look at and i++ would step over it.
        for (var i = 0; i < state.LiveProjectileCount;)
        {
            var handle = state.LiveProjectileHandle(i);
            ref var projectile = ref state.Projectiles[handle.Index];

            if (!Advance(ref projectile.Lifetime))
            {
                i++;
            }
            else if (projectile.Kind == ProjectileKind.Mine)
            {
                projectile.Reflector = ProjectileState.NoOwner;
                i++;
            }
            else
            {
                state.FreeProjectile(handle);
            }
        }

        // --- alarm 1: intel grabability, and the airblast cooldown ------------
        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
        {
            if (!state.Alive[slot])
                continue;

            if (Advance(ref state.Characters[slot].IntelGrabTimer))
                state.Characters[slot].CanGrabIntel = true;

            // Flamethrower.events/Alarm 1.xml: `readyToBlast = true;`. The
            // weapon is its own instance, younger than its owner, so its alarm
            // fires after the character's within the same index — invisible, as
            // neither reads the other.
            if (Advance(ref state.Weapons[slot].BlastTimer))
                state.Weapons[slot].ReadyToBlast = true;

            // Revolver.events/Alarm 1.xml, the *same* alarm index on a
            // different weapon: thirty-two ticks after a spy commits to a stab,
            // the knife's hitbox finally exists.
            //
            // ⚠️ And the event ends with a drag-and-drop
            // `action_set_alarm(18, 2)` that no grep for GML code inside the
            // event finds — see Weapons.StabCooldown.
            if (Advance(ref state.Weapons[slot].StabTimer))
            {
                Weapons.CreateStabMask(ref state, slot);
                state.Weapons[slot].StabReadyTimer = Weapons.StabCooldown;
            }
        }

        // Rocket.events/Alarm 1.xml: `if knockback > 5 knockback = 5;`. Twenty
        // ticks after launch a rocket stops being able to throw anybody very
        // far, which is what keeps a long-range rocket from acting as a shove.
        for (var i = 0; i < state.LiveProjectileCount; i++)
        {
            ref var projectile = ref state.Projectiles[state.LiveProjectileSlot(i)];

            if (Advance(ref projectile.KnockbackCapTimer) && Gml.Greater(projectile.Knockback, 5))
                projectile.Knockback = 5;
        }

        // --- alarm 2: end of uber, and the flare cooldown ---------------------
        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
        {
            if (!state.Alive[slot])
                continue;

            if (Advance(ref state.Characters[slot].UberTimer))
                state.Characters[slot].Ubered = false;

            // Flamethrower.events/Alarm 2.xml: `readyToFlare = true;`.
            if (Advance(ref state.Weapons[slot].FlareTimer))
                state.Weapons[slot].ReadyToFlare = true;

            // Revolver.events/Alarm 2.xml, one line: `readyToStab = true`. It
            // re-opens the *cloak toggle* as well as the next stab —
            // Character.events/Begin Step.xml:135 gates the toggle on the same
            // flag — so a spy who has just stabbed cannot vanish for fifty
            // ticks.
            if (Advance(ref state.Weapons[slot].StabReadyTimer))
                state.Weapons[slot].ReadyToStab = true;
        }

        // Rocket.events/Alarm 2.xml: `knockback = 0;`. Ten ticks after the cap,
        // a rocket is pure damage.
        for (var i = 0; i < state.LiveProjectileCount; i++)
        {
            ref var projectile = ref state.Projectiles[state.LiveProjectileSlot(i)];

            if (Advance(ref projectile.KnockbackEndTimer))
                projectile.Knockback = 0;
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

        // Rocket.events/Alarm 3.xml is a bare instance_destroy: the end of a
        // rocket that ran out of leash and then had the leash catch up with it.
        // A rocket still outside its range re-arms this every movement pass, so
        // the usual over-range death is the alpha countdown in Projectiles
        // instead, two ticks later.
        //
        // Frees, so the cursor only advances when nothing was removed — the same
        // rule as alarm 0 above.
        for (var i = 0; i < state.LiveProjectileCount;)
        {
            var handle = state.LiveProjectileHandle(i);

            if (Advance(ref state.Projectiles[handle.Index].FadeTimer))
                state.FreeProjectile(handle);
            else
                i++;
        }

        // --- alarm 4: kill assist expiration ----------------------------------
        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
        {
            if (!state.Alive[slot])
                continue;

            if (Advance(ref state.Characters[slot].AssistTimer))
                state.Characters[slot].SecondToLastDamageDealer = CharacterState.Nobody;
        }

        // --- alarm 5: the players' respawns, then the weapons' reloads --------
        //
        // Player.events/Alarm 5.xml: `if (spawningAllowed()) PlayerSpawn();`.
        // spawningAllowed() is arena-round gating and is true in every mode the
        // port runs. Player instances are created at join, before any character
        // or weapon exists, so within the index every player's alarm fires
        // before any weapon's — and a character spawned here has its own alarms
        // visited by the *later* indices of this same stage, exactly as a GM8
        // instance created mid-stage is.
        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
        {
            if (!state.HasPlayer[slot])
                continue;

            if (Advance(ref state.Players[slot].RespawnTimer))
                PlayerSpawn(spawnPoints, ref state, slot, ref events);
        }

        // --- alarm 5 still: a weapon's reload, and its body differs per weapon
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

                case WeaponKind.Flamethrower:
                case WeaponKind.Minigun:
                    // Flamethrower.events/Alarm 5.xml and Minigun.events/Alarm
                    // 5.xml are the same single line, and neither is a reload at
                    // all: `isRefilling = true`. The magazine comes back from
                    // the weapon's own Begin Step instead — 1.8 at a time for
                    // the pyro and 1 at a time for the heavy.
                    //
                    // ⚠️ The Minigun's buffer is longer than it looks: its Begin
                    // Step pushes alarm[5] back up by one on every tick the
                    // trigger is held, so this fires only once the heavy stops
                    // shooting. See Weapons.WeaponBeginStep.
                    weapon.IsRefilling = true;
                    break;

                case WeaponKind.Blade:
                    // Blade.events/Alarm 5.xml, also a single line and also not
                    // a reload: `replenishing = 1`. Armed by throwing a blade
                    // rather than by firing, and for the blade's whole possible
                    // lifetime — so a Quote whose blade came home in three ticks
                    // still waits fifteen for the tank to start climbing.
                    weapon.Replenishing = true;
                    break;

                case WeaponKind.Medigun:
                    // Medigun.events/Alarm 5.xml is one line and has no guard on
                    // it at all: `ammoCount = maxAmmo;`. Not the Revolver's
                    // `if ammoCount < maxAmmo`, and not the Scattergun's
                    // one-round-at-a-time — the whole forty come back at once,
                    // 55 ticks after the last needle.
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

        // --- alarm 11: a medic mends itself ------------------------------------
        //
        // Objects/Characters/Medic.events/Alarm 11.xml, the only alarm in the
        // game that belongs to a single class. It re-arms itself, so it runs
        // once a second for as long as the medic lives.
        //
        // ⚠️ It is last because alarms are index-major (see this method's
        // remarks) and eleven is the highest index in the game — so a medic
        // regenerates *after* every refire, every lifetime and every reload has
        // been resolved this tick.
        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
        {
            if (!state.Alive[slot])
                continue;

            if (!Advance(ref state.Characters[slot].RegenTimer))
                continue;

            // Guarded on the class the same way alarm 7 is: the alarm is armed
            // only by Medic.events/Create.xml, so a non-medic can never reach
            // this, and saying so keeps a stray timer from healing a scout.
            if (state.Characters[slot].Class != PlayerClass.Medic)
                continue;

            Healing.Regenerate(ref state.Characters[slot]);
        }
    }

    /// <summary>
    /// <c>Scripts/Spawning/PlayerSpawn.gml</c>: a fresh character at a random
    /// spawn point of the player's team.
    /// </summary>
    /// <remarks>
    /// The guards are the script's own: team not spectator, no character
    /// (<c>object == -1</c>); <c>global.isHost</c> and <c>!global.mapchanging</c>
    /// are always true here. <c>selectSpawnGroup</c> answers 0 for every mode
    /// without control points, which is every mode the port runs; the point id
    /// is <c>floor(random(n))</c> off the global stream — free, because
    /// <c>sendEventSpawn</c> broadcasts it and every client follows the message
    /// rather than re-deriving the draw.
    /// </remarks>
    /// <summary>
    /// <c>PlayerSpawn()</c> called directly rather than through the alarm.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Arena's <c>respawnEveryone()</c> does exactly this</b>, and the
    /// difference is a whole tick: arming <c>alarm[5] = 1</c> instead would put
    /// every character in a new round one tick later than the reference does,
    /// and the round starts on the same tick. The guards are the script's own,
    /// so a spectator still spawns nothing.
    /// </remarks>
    public static void SpawnNow(
        Maps.SpawnPoints? spawnPoints,
        ref WorldState state,
        int slot,
        ref TickEvents events) =>
        PlayerSpawn(spawnPoints, ref state, slot, ref events);

    private static void PlayerSpawn(
        Maps.SpawnPoints? spawnPoints,
        ref WorldState state,
        int slot,
        ref TickEvents events)
    {
        ref readonly var player = ref state.Players[slot];

        // `if(global.isHost and !global.mapchanging and team != TEAM_SPECTATOR
        // and object == -1)`. ⚠️ The alarm still fires while the map is
        // changing — the guard is inside the script, not on the alarm — so the
        // timer is spent and the player simply does not appear. That is what
        // makes the reset's blanket `alarm[5] = 1` safe.
        if (spawnPoints is null
            || state.MapChanging
            || !Arena.SpawningAllowed(state)
            || state.Alive[slot]
            || player.Team is not (Team.Red or Team.Blue))
        {
            return;
        }

        const byte Group = 0;

        var available = spawnPoints.Count(player.Team, Group);
        if (available == 0)
            return;

        var point = (int)state.Random.Next(available);

        if (!state.Spawn(spawnPoints, slot, player.Class, player.Team, point, Group))
            return;

        events.Spawned[slot] = true;
        events.Spawns[slot] = new PlayerSpawnEvent(slot, (byte)point, Group);
    }
}
