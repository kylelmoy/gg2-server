using GG2.Protocol;

namespace GG2.Simulation;

/// <summary>
/// One tick of the match, as a pure function.
/// </summary>
/// <remarks>
/// docs/ARCHITECTURE.md's <c>Tick(WorldState, ReadOnlySpan&lt;PlayerInput&gt;)</c>,
/// and the rule the rest of the project hangs off: no sockets, no clock, no
/// <c>async</c>, no allocation on the steady-state path. Everything the tick
/// needs is in the state, the inputs and the read-only world handed to it;
/// everything it produces is in the state.
/// <para>
/// This is deliberately the <em>only</em> place the phase ordering below is
/// written down. The parity replayer and the live server both call it, so a
/// recording proves the same code the server runs — which is the whole point of
/// docs/PARITY.md and stops being true the moment there are two copies.
/// </para>
/// </remarks>
public static class MatchSimulation
{
    /// <summary>
    /// Advances every living character by one tick at a fixed 30 Hz.
    /// </summary>
    /// <remarks>
    /// <b>The phase split across characters is observable and is not an
    /// implementation detail.</b> Game Maker runs an event for every instance
    /// before moving to the next event, so every character finishes Begin Step
    /// before any starts Step. Running one character all the way through and
    /// then the next changes what each sees of the others and is a different
    /// simulation — see <see cref="CharacterPhysics"/>.
    /// <para>
    /// Slots are walked in ascending order within each phase, which is the
    /// reference's instance order: a character's instance is created when its
    /// player spawns, and the roster is positional.
    /// </para>
    /// </remarks>
    /// <param name="world">Collision geometry. Read-only, and not part of the state.</param>
    /// <param name="state">The match state, advanced in place.</param>
    /// <param name="inputs">
    /// One entry per slot. Slots beyond its length keep the input they had,
    /// which is what a player who sent nothing this tick should get.
    /// </param>
    /// <remarks>
    /// The overload for a caller with nothing to do with the tick's events. It
    /// zeroes a <see cref="TickEvents"/> on the stack per call, which is a few
    /// kilobytes — fine for a test and wrong inside a rollout loop, where the
    /// buffer should be hoisted and the four-argument form used.
    /// </remarks>
    public static void Tick(
        CollisionWorld world,
        ref WorldState state,
        ReadOnlySpan<PlayerInput> inputs,
        Maps.SpawnPoints? spawnPoints = null)
    {
        var events = default(TickEvents);
        Tick(world, ref state, inputs, ref events, spawnPoints);
    }

    /// <summary>
    /// Advances the match by one tick, reporting what it did that the state does
    /// not record.
    /// </summary>
    /// <param name="world">Collision geometry. Read-only, and not part of the state.</param>
    /// <param name="state">The match state, advanced in place.</param>
    /// <param name="inputs">
    /// One entry per slot. Slots beyond its length keep the input they had,
    /// which is what a player who sent nothing this tick should get.
    /// </param>
    /// <param name="events">
    /// Cleared of last tick's outputs and filled with this tick's. Also carries
    /// the one input a tick can be <em>told</em> rather than deciding — a forced
    /// weapon-fire seed, which is how a parity replay reproduces a recorded
    /// shot. See <see cref="TickEvents"/>.
    /// </param>
    /// <param name="spawnPoints">
    /// The map's spawn points, which the alarm stage's respawns resolve
    /// against. Null — the default, and what the parity replayer wants —
    /// spawns nobody: a replay injects the recording's own
    /// <c>PLAYER_SPAWN</c>s instead.
    /// </param>
    public static void Tick(
        CollisionWorld world,
        ref WorldState state,
        ReadOnlySpan<PlayerInput> inputs,
        ref TickEvents events,
        Maps.SpawnPoints? spawnPoints = null)
    {
        ArgumentNullException.ThrowIfNull(world);

        events.ClearOutputs();

        // GameServerBeginStep.gml:1-2, the first two lines of the server's own
        // Begin Step and so the first thing in a frame. It only counts while a
        // balance notice is outstanding.
        TeamBalance.Advance(ref state);

        // The generators' Begin Step, before every character's: a generator is
        // a map object and so older than anything a match creates. It is the
        // only Begin Step in the game that can kill a roomful of characters, and
        // running it here is what makes the explosion read each of them at its
        // end-of-previous-tick position — the same rule the projectile pass
        // follows for the same reason.
        Generators.BeginStep(world, ref state, ref events);

        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
        {
            if (!state.Alive[slot])
                continue;

            if (slot < inputs.Length)
                state.Characters[slot].ApplyInput(inputs[slot]);

            // The tick's one writer of the two mirrored globals — see
            // CharacterState.MapChanging. Before any physics reads a filter.
            state.Characters[slot].MapChanging = state.MapChanging;
            state.Characters[slot].SetupGatesClosed = state.SetupTimer > 0;
            state.Characters[slot].ArenaRoundEnded = Arena.RoundEnded(state);

            // The third and fourth mirrored flags, and the only ones that come
            // off the PLAYER rather than off a global: Arena's humiliation
            // round, and the client's queued-jumping preference.
            state.Characters[slot].Humiliated = state.Players[slot].Humiliated;
            state.Characters[slot].QueueJump = state.Players[slot].QueueJump;
        }

        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
        {
            if (!state.Alive[slot])
                continue;

            // Firing is part of this character's Begin Step and sits a few lines
            // above the acceleration loop, so it must run inside this pass and
            // immediately before the physics for the same slot — not as a
            // sweep of its own. Weapons.FireStep says why that is exact.
            // Character.events/Begin Step.xml, in its own order: the attack
            // (line 83), then the jump (93-116), then the right-click (120-126),
            // then the friction switch and the acceleration loop (143 onwards).
            //
            // The detonation genuinely belongs in the middle. A demoman's
            // mine sets moveStatus on whoever it throws, and the switch below it
            // reads moveStatus on the same tick — that is what a mine jump is.
            // Near the top of Begin Step, above the attack key — so a burning
            // character takes its tick of fire damage before it gets to shoot,
            // and a medic beaming it sees the duration this already spent. It
            // sits just below the ground checks, which are inside
            // BeginStepInput below and touch nothing it reads or writes.
            Afterburn.BeginStep(ref state, slot, ref events);

            Weapons.FireStep(world, ref state, slot, ref events);
            CharacterPhysics.BeginStepInput(world, ref state.Characters[slot]);
            Weapons.SpecialStep(world, ref state, slot, ref events);

            // Begin Step:129-142, between the right-click block above and the
            // moveStatus switch below — and on the *same key*, so one press of
            // the right button runs both.
            CharacterPhysics.CloakStep(ref state.Characters[slot], ref state.Weapons[slot]);

            CharacterPhysics.BeginStepAccelerate(world, ref state.Characters[slot]);

            // The tail of the same event, after the acceleration loop:
            // Begin Step:303-337 charges a carrier's return timer and :366
            // makes an ubered carrier drop the flag, both between the gravity
            // above and the `if ubered` block below. The nuts-and-bolts
            // regeneration and the animation counter beside them arrive with
            // the phases that own them.
            Intel.BeginStep(ref state, slot, ref events);

            // Begin Step:429-439, below the intel block and the humiliation
            // one: which capture zone the character is standing in, which its
            // control point reads in the Step stage below.
            ControlPoints.BeginStep(world, ref state.Characters[slot]);

            CharacterPhysics.BeginStepUberEffects(
                ref state.Characters[slot], ref state.Weapons[slot]);

            // A weapon is a *separate instance* with a *higher id*, so its
            // own Begin Step runs after the whole of its owner's and before the
            // next character's. Three weapons have one: the Medigun spends an
            // uber, and the Flamethrower and the Minigun top up their tanks —
            // the Minigun also holding its own reload buffer open while the
            // trigger is down.
            Healing.UberDrainStep(ref state, slot);
            Weapons.WeaponBeginStep(
                ref state.Weapons[slot],
                state.Characters[slot].Class,
                state.Characters[slot].Zoomed);
        }

        // Every bubble's Begin Step: the leash to its owner. A Bubble is
        // younger than the character that made it, so this follows the whole
        // character loop — and it is not observable where among the younger
        // instances it sits, because a Begin Step moves nobody and the only
        // thing it reads is its owner's position.
        Projectiles.BubbleBeginStep(ref state);

        // Every sentry's Begin Step: build progress, the turret's birth, and
        // the fall. After the whole character loop because a sentry is created
        // later than every character alive when it was placed, so its Begin
        // Step runs after theirs. (A character spawning *after* the sentry
        // would run after it in the reference; the port has no respawn yet, so
        // the case cannot arise inside a tick.)
        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
        {
            if (state.HasSentry[slot])
                Sentries.BeginStep(world, ref state, slot, ref events);
        }

        // Game Maker's alarm stage sits between Begin Step and Step. Note the
        // consequence for a weapon: Begin Step ran *first*, so the shot above
        // was tested against the refire counter last tick left behind, and the
        // decrement that may arm the weapon happens here, after the chance to
        // use it has passed. Respawns happen inside it too — Player.events/
        // Alarm 5.xml — so a character born here runs its Step below in the
        // same tick, exactly as a GM8 instance created between the stages does.
        Timers.Advance(ref state, spawnPoints, ref events);

        // The projectile pass runs BEFORE the characters move, and that is
        // measured rather than tidy.
        //
        // `move_all_bullets` is called from Objects/Networking/GameServer.events
        // /Step.xml:16 — it is a *Step* event, the same stage the characters'
        // Step events run in. Nothing has moved yet when it runs: GM8's
        // automatic-movement pass comes after every Step event, and
        // Character.events/Step.xml deliberately subtracts its own hspeed and
        // vspeed back off so that pass can re-add them. So a blast that goes off
        // inside move_all_bullets sees every character at its *end-of-previous-
        // tick* position, whichever order the two Step events run in.
        //
        // This port commits a character's move inside CharacterPhysics.Step
        // rather than subtracting it back, so the only way to show the
        // projectiles the same world is to run them first.
        //
        // Measured on reference-ctf_truefort-closedcombat.trace, where a soldier
        // rocket-jumps on the same tick as their jump: with the pass after the
        // characters the blast landed four pixels outside its owner's box and
        // dealt 28.15385 instead of 30, and the replay diverged there. Moving it
        // here makes the damage exactly 30 and pushes the exact window on.
        Projectiles.Move(world, ref state, ref events);

        // The gamemode HUD's Step, which is where the round is decided.
        // Between move_all_bullets and the characters because CTFHUD is younger
        // than the GameServer and older than every Character — and the order is
        // observable: deciding the round sets MapChanging, and that opens every
        // gate for the characters that move immediately below.
        // The control points' own Step, before the round's: a ControlPoint is a
        // map object and so older than the HUD that reads it, and the KotH HUD
        // reads this tick's `capping` and `team` to decide the round.
        ControlPoints.Step(ref state);

        // The generators' own Step — the shield coming back — beside the
        // control points' and before the HUD's, for the same reason: both are
        // map objects and the HUD that reads them is younger.
        Generators.Step(ref state);

        Rounds.Step(ref state);

        // ArenaHUD's own Step, after the point it reads. It decides the round
        // and therefore the map, so it stands beside Rounds.Step rather than
        // inside it — and unlike the other HUDs it broadcasts transitions.
        Arena.Step(ref state, spawnPoints, ref events);

        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
        {
            if (!state.Alive[slot])
                continue;

            CharacterPhysics.Step(world, ref state.Characters[slot]);

            // Objects/Characters/Heavy.events/Step.xml, after the inherited
            // Step it wraps — the only class-specific Step event in the game.
            // The rate it sets is what *next* tick accelerates with.
            CharacterPhysics.HeavyStep(ref state.Characters[slot]);
        }

        // Every bubble's own Step, which is the one projectile movement that
        // does NOT happen in move_all_bullets — a Bubble has no `with` block
        // there and steers itself instead. Its instance is younger than its
        // owner, so it runs after that character's Step, and the difference is
        // observable: the owner's `hspeed` it picks up a fifth of is this
        // tick's, already through the friction and the wall tests.
        Projectiles.BubbleStep(ref state);

        // StabMask.events/Step.xml, in the same phase and for the same reason:
        // the knife is younger than every character, so it pins itself to its
        // owner's *mid-Step* position — which trails them by a tick's velocity.
        Projectiles.StabStep(ref state);

        // StabAnim's Step, which is the only thing that ever ends a spy's stab
        // freeze. Also younger than every character.
        Weapons.StabAnimStep(ref state);

        // The automatic movement pass for sentries. A sentry has no Step event,
        // so GM8 simply adds its speeds here — after the projectile pass read
        // last tick's position, like everything else that moves.
        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
        {
            if (state.HasSentry[slot])
                Sentries.Move(ref state, slot);
        }

        // Medigun.events/Step.xml, a weapon's own Step event and so after every
        // character's. It drops a beam that did not connect this tick, which is
        // the only thing that ever ends one — releasing the attack key stops
        // Healing.BeamStep being reached at all, so nothing inside it can
        // notice. Nothing else in this stage reads healTarget or healer, so its
        // position among the Step events is not observable.
        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
        {
            if (state.Alive[slot] && state.Characters[slot].Class == PlayerClass.Medic)
                Healing.DecayStep(ref state, slot);
        }

        // Collision events. GM8 runs these after the automatic-movement pass, so
        // unlike the blast above they see *both* sides at their new positions —
        // a hit is decided on where things ended up. Before End Step, so a
        // knockback lands in the state End Step then reads.
        Projectiles.Collide(world, ref state, ref events);

        // Character.events/Collision with HealingCabinet.xml, in the same
        // stage and — by GM8's collision-event ordering, which keys on the
        // other object's id — between the capture (IntelligenceBase*, 104/105)
        // and the grab (Intelligence*, 133/134). Not observable either way; see
        // Cabinets.CollisionStep.
        Cabinets.CollisionStep(world, ref state, ref events);

        // The intel's four collision events, in the same stage. Their order
        // relative to a projectile's is not observable: a grab or a capture
        // moves no character, changes no hp and cannot destroy anything a
        // projectile is about to hit, and the flag itself has no collision event
        // with a projectile at all.
        Intel.CollisionStep(world, ref state, ref events);

        // Character.events/Collision with {KillBox, FragBox, PitFall}.xml and
        // the sentry's two, after everything above them: GM8 keys a character's
        // collision events by the other object's id, and the boxes are 506, 509
        // and 794 against the objective's 104/105 and 133/134. So a character
        // standing in a pit on top of a briefcase grabs it and *then* dies,
        // which is what makes Deaths' return arm reachable at all.
        //
        // Placed after Intel.CollisionStep for that reason and not for tidiness.
        // Sentry.events/Collision with {TeamGate, IntelGate}.xml — the "built
        // at T, self-destructed at T+1" finding: a sentry built in a doorway
        // takes 999 and dies in its own End Step below. TeamGate is object 110
        // and so strictly runs before KillBox's 506; IntelGate's 516 falls
        // after FragBox's 509. Unobservable either way — see
        // Sentries.GateCollisionStep.
        Sentries.GateCollisionStep(world, ref state);

        KillBoxes.CollisionStep(world, ref state);

        // Character.events/Collision with MoveBox{Up,Down,Left,Right}.xml. Their
        // ids (756-761) put them between FragBox and PitFall, so strictly they
        // interleave with the call above — and it does not matter: a killing box
        // writes hp and a damage source, a move box writes the velocity pair and
        // moveStatus, and neither reads the other. See MoveBoxes.CollisionStep.
        MoveBoxes.CollisionStep(world, ref state);

        // The moving platforms' End Step, before every character's. A platform
        // is a map object and so older than anything a match creates, and GM8
        // runs an event in instance-id order — the same rule that puts
        // Generators.BeginStep above the character loop at the top of this tick.
        //
        // In the reference this event ALSO carries its riders, inside a
        // `with(Character)` that runs before those characters' own End Steps.
        // That half is not ported and cannot be until a platform has a collision
        // box; MovingPlatforms' remarks say why, and the position of this call is
        // what the carry will need when it lands.
        MovingPlatforms.EndStep(world, ref state);

        // End Step, which is also where anything that ran out of hp dies. GM8's
        // instance_destroy takes effect at once, so a character killed here is
        // gone before the next slot's End Step runs — which is why the death is
        // resolved inside this loop rather than swept up after it. The order
        // matters as soon as a medic is involved: the assist redirect reads the
        // killer's healer, and a killer who dies earlier in the same pass has no
        // character left to read it from.
        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
        {
            if (!state.Alive[slot])
                continue;

            // A client's replica never decides a death: WorldState.ClientReplica.
            if (!CharacterPhysics.EndStep(world, ref state.Characters[slot], ref state.Weapons[slot], host: !state.ClientReplica))
                continue;

            Deaths.Kill(ref state, slot, ref events, world);

            // Character.events/End Step.xml:29-31, `with(GameServer)
            // ServerBalanceTeams()`, immediately after the death event and
            // inside the block that exits. The player who just died is
            // therefore a candidate: their character is already gone, so the
            // "only dead players" guard passes for them.
            TeamBalance.Check(ref state, ref events);
        }

        // BladeB.events/End Step.xml: the blade in flight bills its owner a
        // unit of magazine. After every character's End Step because a blade is
        // younger than all of them, and it is the only projectile with an End
        // Step at all.
        Projectiles.BladeEndStep(ref state);

        // The sentries' End Steps, and then their turrets'. Each turret is
        // created after its own sentry, so its End Step follows the sentry's —
        // a sentry that dies here takes its turret with it before the turret
        // can fire. Between *different* players' sentries the reference's order
        // is instance creation, which this slot walk only approximates; it is
        // observable only when two turrets kill each other's sentry on the
        // same tick.
        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
        {
            if (state.HasSentry[slot])
                Sentries.EndStep(world, ref state, slot, ref events);
        }

        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
        {
            if (state.HasSentry[slot] && state.Sentries[slot].HasTurret)
                Sentries.TurretEndStep(world, ref state, slot, ref events);
        }
    }
}
