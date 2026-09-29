using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Server;

public sealed partial class GameServer
{
    // --- the simulation ------------------------------------------------------

    /// <summary>Runs one tick of physics for every character in the world.</summary>
    /// <remarks>
    /// Inputs are gathered per roster slot into a scratch buffer that lives for
    /// the life of the server: <c>Tick</c> allocating on the steady-state path
    /// is what docs/BOTAI.md item 1 rules out, and a per-tick array here would
    /// be exactly that.
    /// </remarks>
    /// <param name="count">
    /// Roster slots whose input was gathered and applied before the broadcast.
    /// ⚠️ Gathered THERE and not here, so every source is polled exactly once a
    /// tick — a bot polled twice would think twice and could answer differently.
    /// </param>
    private void AdvanceSimulation(int count)
    {
        // Before the tick, because it reads the round the *previous* tick
        // decided — the reference checks it in the server's Begin Step, which is
        // the first thing in a frame. See ServeRoundEnd.
        ServeRoundEnd();

        // ⚠️ Before the tick, and the position is measured rather than tidy —
        // see the method.
        RequestGeneratorScoreboardSync();

        MatchSimulation.Tick(_collision, ref _world, _inputs.AsSpan(0, count), ref _events, _spawnPoints);

        // ⚠️ And this one after it, equally deliberately. Same method-level
        // reasoning, opposite answer.
        RequestSetupScoreboardSync();

        // Fires before deaths, because a weapon fires in a character's Begin
        // Step and a character dies in its End Step — so within one tick the
        // reference appends the WEAPON_FIRE messages first. A sentry's landing
        // is also a Begin Step (the sentry's, after every character's) and its
        // destruction an End Step, so each pair straddles the deaths the same
        // way. A spawn is the alarm stage, between the Begin Steps and the End
        // Steps, so it sits between the landings and the deaths.
        // First of all of them: a generator's Begin Step runs before every
        // character's, so a destruction is the earliest thing a tick can
        // report — and the explosion's victims die in their own End Steps, so
        // this message must precede the PLAYER_DEATHs it caused.
        BroadcastGeneratorDestructions();

        BroadcastWeaponFire(count);
        BroadcastUbers(count);
        BroadcastControlPointUnlock();
        BroadcastArena();
        BroadcastIntelDrops(count);
        BroadcastIntelReturns();
        BroadcastSentryLandings(count);

        // ⚠️ The BEGIN STEP destructions belong here, above the flush, beside
        // the landings that share their event. Only the hp<=0 ones are late.
        BroadcastSentryDestructions(count);

        BroadcastSpawns(count);
        BroadcastIntelGrabsAndCaptures(count);
    }

    /// <summary>
    /// The three messages the reference appends in an End Step — <b>after</b>
    /// its own flush, and so delivered on the following tick.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>MEASURED against the running game on 2026-09-02, not inferred.</b>
    /// <c>GameServerEndStep.gml:7-10</c> flushes and clears <c>global.sendBuffer</c>
    /// in the GameServer's End Step, and the GameServer is the OLDEST instance in
    /// the game (<c>start_server.gml:7</c>, <c>persistent</c>) — so under GM8's
    /// instance-id event order its End Step runs before every other object's, and
    /// anything a Character or a Sentry appends in <em>its</em> End Step misses
    /// that flush.
    /// <para>
    /// The probe: two bots killed on one frame, watching
    /// <c>buffer_size(global.sendBuffer)</c> once a frame from the agent bridge's
    /// Step — which is after every Begin Step and before every End Step. The frame
    /// after the deaths read <b>17 bytes against a 5-byte steady state</b>, and the
    /// 12 left over close exactly: 5 + 5 for the two <c>PLAYER_DEATH</c>s
    /// (<c>sendEventPlayerDeath.gml</c>) and 2 for the <c>BALANCE</c> notice
    /// (<c>ServerBalanceTeams.gml:29-30</c>) that a death's own
    /// <c>ServerBalanceTeams</c> writes. Those bytes could only have been appended
    /// during the PREVIOUS frame's End Steps and survived its flush.
    /// </para>
    /// <para>
    /// ⚠️ <b>This changes WHEN the bytes leave and never their order.</b> The
    /// buffer is FIFO, so these still follow the same messages they followed
    /// before and still precede the next tick's update — which is exactly why no
    /// trace can see it, and why <c>cdaa62a</c> got the boundary half right.
    /// </para>
    /// <para>
    /// ⚠️ Relative order among the three is left EXACTLY as it was, so the flush
    /// point is the only thing this changed. (The reference interleaves each
    /// character's <c>BALANCE</c> with its own death and puts every sentry's
    /// destruction after all of them; nothing observes the difference, and
    /// straightening it is not this change's business.)
    /// </para>
    /// </remarks>
    private void BroadcastEndStepEvents(int count)
    {
        BroadcastPlayerDeaths(count);

        // ⚠️ Immediately after the deaths, because that is where the reference
        // puts it: Character.events/End Step.xml sends the death and then calls
        // doEventPlayerDeath, which destroys the body, and Destroy.xml's return
        // is appended from inside that. A carrier killed by a pit returns the
        // flag AFTER its own PLAYER_DEATH, never before.
        if (_events.ReturnedRedIntelInEndStep)
            ServerMessages.ReturnIntel(_broadcast, Team.Red);

        if (_events.ReturnedBlueIntelInEndStep)
            ServerMessages.ReturnIntel(_broadcast, Team.Blue);

        BroadcastEndStepSentryDestructions(count);

        // The only in-tick BALANCE comes from a death's own ServerBalanceTeams,
        // which runs after doEventPlayerDeath.
        BroadcastTeamBalance();
    }

    /// <summary>
    /// Asks every slot's <see cref="IInputSource"/> what it is pressing.
    /// </summary>
    /// <remarks>
    /// One path for sockets and bots alike — docs/ARCHITECTURE.md's seam. The
    /// world handed to a source is the state as the previous tick left it,
    /// which is the same thing an <c>INPUTSTATE</c> that arrived before
    /// <see cref="ServiceClients"/> was decided against.
    /// <para>
    /// <b>The batched form is used where a source offers it</b>
    /// (docs/BOTAI.md item 5). Slots are grouped by source <em>identity</em>,
    /// because that is what a batching source is: one policy driving several
    /// bots, which wants one call with all of their slots rather than one call
    /// each. The grouping is here rather than deferred precisely because item 5
    /// says adding it afterwards is a wide change — a call site that assumed
    /// per-slot polling has to be rewritten, and this is that call site.
    /// </para>
    /// <para>
    /// Everything below is stack or long-lived buffers: this runs every tick.
    /// </para>
    /// </remarks>
    private void GatherInputs(int count)
    {
        Span<bool> polled = stackalloc bool[WorldState.MaxSlots];
        Span<int> group = stackalloc int[WorldState.MaxSlots];

        // Hoisted, not per group: a stackalloc inside the loop would grow the
        // frame once per batching source and never give it back.
        Span<PlayerInput> outputs = stackalloc PlayerInput[WorldState.MaxSlots];

        for (var i = 0; i < count; i++)
        {
            if (polled[i])
                continue;

            var source = _roster[i].Source;

            if (source is not IBatchInputSource batch)
            {
                _inputs[i] = source.Poll(new WorldView(in _world, _collision), i, (int)_frame);
                polled[i] = true;
                continue;
            }

            // Every remaining slot driven by this same instance, in ascending
            // order — the roster order every other per-slot loop in the project
            // walks.
            var size = 0;
            for (var j = i; j < count; j++)
            {
                if (!polled[j] && ReferenceEquals(_roster[j].Source, source))
                {
                    group[size++] = j;
                    polled[j] = true;
                }
            }

            batch.PollMany(
                new WorldView(in _world, _collision), group[..size], outputs[..size], (int)_frame);

            for (var k = 0; k < size; k++)
                _inputs[group[k]] = outputs[k];
        }
    }

    /// <summary>
    /// <c>UNLOCKCP</c>: the pregame lock has come off.
    /// </summary>
    /// <remarks>
    /// From the HUD's own Step, so it sits with the Begin Step messages rather
    /// than with the collision ones. A capture also owes a <c>CAPS_UPDATE</c>,
    /// which the point's own Step asks for the same way a cap does — see
    /// <see cref="WriteStateUpdate"/>.
    /// </remarks>
    /// <summary>
    /// The Arena round transitions — <c>ARENA_RESTART</c>,
    /// <c>ARENA_STARTROUND</c>, <c>ARENA_ENDROUND</c> and
    /// <c>ARENA_WAITFORPLAYERS</c>.
    /// </summary>
    /// <remarks>
    /// From the HUD's own Step, so they sit with the other Begin/Step-stage
    /// messages. At most one can fire in a tick: each transition leaves the
    /// state machine in a phase whose own exit condition cannot already be met.
    /// A round ending also owes a scoreboard update, because the round scores
    /// it carries are in the <c>FULL_UPDATE</c> tail rather than in the
    /// message a client can miss.
    /// </remarks>
    private void BroadcastArena()
    {
        if (_events.ArenaRestarted)
            ServerMessages.ArenaRestart(_broadcast);

        if (_events.ArenaRoundStarted)
            ServerMessages.ArenaStartRound(_broadcast);

        if (_events.ArenaWaitingForPlayers)
            ServerMessages.ArenaWaitForPlayers(_broadcast);

        if (!_events.ArenaRoundEnded)
            return;

        // ⚠️ Both lists are chosen before either is written, because the two
        // counts lead the message — see ServerMessages.ArenaEndRound. And
        // SelectMvps writes back a rounded healing total, which is the
        // reference's own assignment, so it takes the world by reference.
        Span<ArenaMvp> red = stackalloc ArenaMvp[Scoring.MaxMvps];
        Span<ArenaMvp> blue = stackalloc ArenaMvp[Scoring.MaxMvps];

        var redCount = Scoring.SelectMvps(ref _world, Team.Red, red);
        var blueCount = Scoring.SelectMvps(ref _world, Team.Blue, blue);

        ServerMessages.ArenaEndRound(
            _broadcast,
            _events.ArenaRoundWinner,
            WireQuantise.ToUByte(_world.ArenaRedWins),
            WireQuantise.ToUByte(_world.ArenaBlueWins),
            red[..redCount],
            blue[..blueCount]);

        _capsSyncPending = true;
    }

    private void BroadcastControlPointUnlock()
    {
        // syncTimer, wherever the simulation asked for one: a captured point, a
        // capture meter crossing a threshold, or the five-second warning before
        // the pregame lock lifts.
        if (_world.ScoreboardSyncRequested)
        {
            _capsSyncPending = true;
            _world.ScoreboardSyncRequested = false;
        }

        if (!_world.UnlockedControlPointsThisTick)
            return;

        ServerMessages.UnlockControlPoints(_broadcast);
        _world.UnlockedControlPointsThisTick = false;
    }

    /// <summary>
    /// <c>DROP_INTEL</c> for a carrier who was ubered into putting it down.
    /// </summary>
    /// <remarks>
    /// A Begin Step, like a <c>WEAPON_FIRE</c>, and above the uber messages'
    /// own line in the same event — so it goes out in this part of the stream.
    /// A drop caused by a death is never broadcast; see
    /// <see cref="TickEvents.DroppedIntel"/>.
    /// </remarks>
    private void BroadcastIntelDrops(int count)
    {
        for (var slot = 0; slot < count; slot++)
            if (_events.DroppedIntel[slot])
                ServerMessages.DropIntel(_broadcast, (byte)slot);
    }

    /// <summary>
    /// <c>RETURN_INTEL</c>: a dropped briefcase's own alarm fired.
    /// </summary>
    /// <remarks>
    /// The alarm stage, so between the Begin Steps and the collision events —
    /// beside the spawns rather than beside the grabs.
    /// </remarks>
    private void BroadcastIntelReturns()
    {
        // ⚠️ The alarm-stage returns ONLY. A return that came out of a dying
        // carrier's Destroy is appended in the Character's End Step, after the
        // reference has already flushed — see TickEvents.ReturnedRedIntelInEndStep
        // and BroadcastEndStepEvents.
        if (_events.ReturnedRedIntel && !_events.ReturnedRedIntelInEndStep)
            ServerMessages.ReturnIntel(_broadcast, Team.Red);

        if (_events.ReturnedBlueIntel && !_events.ReturnedBlueIntelInEndStep)
            ServerMessages.ReturnIntel(_broadcast, Team.Blue);
    }

    /// <summary>
    /// <c>BALANCE</c>, both shapes: the notice, and each player actually moved.
    /// </summary>
    /// <remarks>
    /// Drained rather than swept with the rest of the tick's events, because two
    /// of the three call sites are <em>outside</em> the tick — a departure and a
    /// team change are both handled where the command arrives. The third, a
    /// death, is inside it and is drained by
    /// <see cref="BroadcastTickEvents"/>'s call to this.
    /// <para>
    /// ⚠️ Each flag is cleared as it is sent, so calling this twice in a tick
    /// cannot double a message.
    /// </para>
    /// </remarks>
    private void BroadcastTeamBalance()
    {
        if (_events.BalanceNoticed)
        {
            ServerMessages.BalanceNotice(_broadcast);
            _events.BalanceNoticed = false;
        }

        for (var slot = 0; slot < _roster.Count && slot < WorldState.MaxSlots; slot++)
        {
            if (!_events.Balanced[slot])
                continue;

            _roster[slot].Team = _world.Players[slot].Team;
            _roster[slot].Class = _events.BalancedClass[slot];

            ServerMessages.BalanceSwitch(_broadcast, (byte)slot, _events.BalancedClass[slot]);
            _events.Balanced[slot] = false;
        }
    }

    /// <summary>
    /// <c>GENERATOR_DESTROY</c>: a generator has run out of hit points.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The message names the team that lost one</b>, not the team that
    /// won — see <see cref="ServerMessages.GeneratorDestroy"/>. Both can go out
    /// on one tick, and the reference sends both.
    /// <para>
    /// ⚠️ <b>It also shortens every later HUD block by four bytes</b>, because
    /// the destroyed generator stops being serialised. A client stays in step
    /// because it destroys its own instance on this message; anything else
    /// reading the stream has to track it too
    /// (<see cref="WorldShape.GeneratorCount"/>).
    /// </para>
    /// </remarks>
    private void BroadcastGeneratorDestructions()
    {
        if (_events.RedGeneratorDestroyed)
            ServerMessages.GeneratorDestroy(_broadcast, Team.Red);

        if (_events.BlueGeneratorDestroyed)
            ServerMessages.GeneratorDestroy(_broadcast, Team.Blue);
    }

    /// <summary>
    /// <c>GRAB_INTEL</c> and <c>SCORE_INTEL</c>, from the collision stage.
    /// </summary>
    /// <remarks>
    /// After the spawns and before the deaths, because collision events sit
    /// between the alarm stage and End Step. A capture also forces an extra
    /// <c>CAPS_UPDATE</c> out on the next tick, which is the reference's
    /// <c>GameServer.syncTimer = 1</c> — see <see cref="WriteStateUpdate"/>.
    /// </remarks>
    private void BroadcastIntelGrabsAndCaptures(int count)
    {
        for (var slot = 0; slot < count; slot++)
        {
            if (_events.GrabbedIntel[slot])
                ServerMessages.GrabIntel(_broadcast, (byte)slot);

            if (!_events.ScoredIntel[slot])
                continue;

            ServerMessages.ScoreIntel(_broadcast, (byte)slot);
            _capsSyncPending = true;
        }
    }

    /// <summary>One cell of the domination matrix. See <see cref="DominationLookup"/>.</summary>
    private byte Dominating(int killer, int victim) =>
        Dominations.Get(_world, killer, victim);

    /// <summary>One roster entry's scoreboard. See <see cref="StatsLookup"/>.</summary>
    /// <remarks>
    /// A slot index is a roster index, so this is a straight read — no lookup
    /// table, and a slot with no player answers with zeroes rather than
    /// throwing, which is what a roster entry mid-join needs.
    /// </remarks>
    private PlayerStatsBlock Statistics(int slot) => new(
        Scoring.Get(_world, slot, StatIndex.Kills),
        Scoring.Get(_world, slot, StatIndex.Deaths),
        Scoring.Get(_world, slot, StatIndex.Caps),
        Scoring.Get(_world, slot, StatIndex.Assists),
        Scoring.Get(_world, slot, StatIndex.Destruction),
        Scoring.Get(_world, slot, StatIndex.Stabs),
        Scoring.Get(_world, slot, StatIndex.Healing),
        Scoring.Get(_world, slot, StatIndex.Defenses),
        Scoring.Get(_world, slot, StatIndex.Invulns),
        Scoring.Get(_world, slot, StatIndex.Bonus),
        Scoring.Get(_world, slot, StatIndex.Points));

    /// <summary>The objective as the state writer wants it. See <see cref="ObjectiveLookup"/>.</summary>
    private ObjectiveBlock Objective()
    {
        // Reused across ticks: the block is written every update and a fresh
        // array per tick is exactly the steady-state allocation docs/BOTAI.md
        // rules out. Its length is fixed for as long as a map is loaded.
        if (_pointBlocks.Length != _world.ControlPointCount)
            _pointBlocks = new ControlPointBlock[_world.ControlPointCount];

        for (var i = 0; i < _pointBlocks.Length; i++)
            _pointBlocks[i] = ControlPoints.ToBlock(_world.ControlPoints[i]);

        return new ObjectiveBlock(
            Intel.ToBlock(_world.RedIntel),
            Intel.ToBlock(_world.BlueIntel),
            _world.RedCaps,
            _world.BlueCaps,
            _world.TimeLimitMinutes,
            _world.RoundTimer,
            _pointBlocks,
            _world.ControlPointUnlock,
            _world.RedTimer,
            _world.BlueTimer,
            _world.SetupTimer,
            (byte)_world.ArenaState,
            _world.ArenaRedWins,
            _world.ArenaBlueWins,
            (sbyte)_world.ArenaWinner,
            _world.ArenaEndCount,
            _world.ArenaRoundStart,
            Generators.ToBlock(_world.RedGenerator),
            Generators.ToBlock(_world.BlueGenerator),
            _world.KillLimit,
            _world.TdmInvulnerabilityTicks);
    }

    private ControlPointBlock[] _pointBlocks = [];

    /// <summary>
    /// Tells every client who respawned this tick and at which spawn point —
    /// <c>PLAYER_SPAWN</c>, from <c>sendEventSpawn.gml</c>.
    /// </summary>
    /// <remarks>
    /// The message carries the point id and group rather than coordinates;
    /// every client resolves the pair against its own copy of the map, which is
    /// the property the parity anchors lean on.
    /// </remarks>
    private void BroadcastSpawns(int count)
    {
        for (var slot = 0; slot < count; slot++)
        {
            if (!_events.Spawned[slot])
                continue;

            var spawn = _events.Spawns[slot];
            ServerMessages.PlayerSpawn(_broadcast, (byte)slot, spawn.SpawnPointId, spawn.Group);
        }
    }

    /// <summary>
    /// Tells every client where a sentry that just finished falling came to
    /// rest — <c>SENTRY_POSITION</c>, from <c>Sentry.events/Begin Step.xml</c>.
    /// </summary>
    /// <remarks>
    /// The position is read back off the world: a landed sentry does not move
    /// again between the event and this broadcast, and the message quantises
    /// with <c>round(x*5)</c> exactly as the sentry's own update block does.
    /// </remarks>
    private void BroadcastSentryLandings(int count)
    {
        for (var slot = 0; slot < count; slot++)
        {
            if (!_events.SentryLanded[slot] || !_world.HasSentry[slot])
                continue;

            ref readonly var sentry = ref _world.Sentries[slot];
            ServerMessages.SentryPosition(
                _broadcast,
                (byte)slot,
                WireQuantise.ToUShort(Gml.Round(sentry.X * 5)),
                WireQuantise.ToUShort(Gml.Round(sentry.Y * 5)));
        }
    }

    /// <summary>
    /// The sentries destroyed in a <b>Begin Step</b> — an owner who stopped
    /// being an engineer of that team, or two sentries too close together.
    /// </summary>
    /// <remarks>
    /// <c>Sentry.events/Begin Step.xml:1-3</c>, which makes its own tick's
    /// flush. Its <c>hp &lt;= 0</c> sibling does not; see
    /// <see cref="BroadcastEndStepSentryDestructions"/>.
    /// </remarks>
    private void BroadcastSentryDestructions(int count) =>
        BroadcastSentryDestructions(count, inEndStep: false);

    /// <summary>
    /// The sentries destroyed in their own <b>End Step</b> by running out of
    /// hit points, which ship on the following tick.
    /// </summary>
    private void BroadcastEndStepSentryDestructions(int count) =>
        BroadcastSentryDestructions(count, inEndStep: true);

    private void BroadcastSentryDestructions(int count, bool inEndStep)
    {
        for (var slot = 0; slot < count; slot++)
        {
            if (!_events.SentryDestroyed[slot]
                || _events.SentryDestroyedInEndStep[slot] != inEndStep)
            {
                continue;
            }

            var destruction = _events.SentryDestructions[slot];
            ServerMessages.DestroySentry(
                _broadcast,
                (byte)slot,
                destruction.Killer,
                destruction.Assistant,
                destruction.Source);
        }
    }

    /// <summary>
    /// Tells every client about the shots this tick took, and with what seed.
    /// </summary>
    /// <remarks>
    /// <b>After the tick, not before, and that is the reference's order.</b> The
    /// <c>GameServer</c> instance is older than every <c>Character</c>, so GM8
    /// runs its Begin Step first: the state update goes into the send buffer,
    /// and then each character's Begin Step appends any <c>WEAPON_FIRE</c> it
    /// produced. A client therefore reads the update for tick <c>n</c> and only
    /// then learns that a shot was fired during it. Moving this above
    /// <see cref="WriteStateUpdate"/> would change the byte order of the stream
    /// and shift every client's pellets by a tick.
    /// <para>
    /// It is a broadcast rather than per-client because the seed is the same for
    /// everyone — that is the entire point of sending a seed instead of six
    /// pellet vectors.
    /// </para>
    /// <para>
    /// Note where these bytes actually leave: <see cref="FlushBroadcast"/> has
    /// already run for this tick, so a fire message sits in the buffer until the
    /// <em>next</em> tick's flush and goes out ahead of that tick's update. The
    /// stream a client reads is therefore update <c>n</c>, fire <c>n</c>, update
    /// <c>n+1</c> — the reference's own byte order, and the one
    /// <c>GG2.Protocol.TraceParser</c> assumes when it files a fire message
    /// under the following tick.
    /// </para>
    /// </remarks>
    private void BroadcastWeaponFire(int count)
    {
        for (var slot = 0; slot < count; slot++)
        {
            if (!_events.Fired[slot])
                continue;

            var fire = _events.Fires[slot];
            ServerMessages.WeaponFire(
                _broadcast, (byte)slot, fire.X, fire.Y, fire.HSpeed, fire.VSpeed, fire.Seed);
        }
    }

    /// <summary>
    /// Tells every client who died this tick, and who gets the credit.
    /// </summary>
    /// <remarks>
    /// The same placement argument as <see cref="BroadcastWeaponFire"/>, one
    /// event stage later: <c>Character.events/End Step.xml:42</c> calls
    /// <c>sendEventPlayerDeath</c>, so these bytes follow the tick's update and
    /// its fires and are read by a client under the next tick's flush.
    /// <para>
    /// <b>The character is already gone from the world by the time this runs</b>
    /// — <see cref="Deaths.Kill"/> cleared the slot during the tick — so
    /// everything the message needs is in the event and none of it can be read
    /// back out of <see cref="_world"/>. That is the whole reason
    /// <see cref="TickEvents"/> carries the resolved killer and assistant rather
    /// than just a flag.
    /// </para>
    /// <para>
    /// <b>No respawn is scheduled here.</b> The reference arms
    /// <c>player.alarm[5]</c> in <c>Character.events/Destroy.xml</c> and a
    /// dead player stays dead until it fires; this server has no player-side
    /// timers yet, so a killed character simply stays absent. docs/PLAN.md
    /// phase 6.
    /// </para>
    /// </remarks>
    private void BroadcastPlayerDeaths(int count)
    {
        for (var slot = 0; slot < count; slot++)
        {
            if (!_events.Died[slot])
                continue;

            var death = _events.Deaths[slot];
            ServerMessages.PlayerDeath(
                _broadcast, (byte)slot, death.Killer, death.Assistant, death.Source);
        }
    }

    /// <summary>
    /// Tells every client which medics filled a charge this tick and which
    /// spent one.
    /// </summary>
    /// <remarks>
    /// Both come out of the medic's Begin Step — <c>UBER_CHARGED</c> from
    /// <c>Medigun.events/User Event 1.xml</c> and <c>UBER</c> from
    /// <c>User Event 2.xml</c> — so they belong in the same place in the stream
    /// as a <c>WEAPON_FIRE</c>, between the tick's state update and its deaths.
    /// <para>
    /// Per slot rather than two sweeps, because the reference emits them
    /// per instance: one medic's charge message precedes the next medic's, and
    /// a single medic's <c>UBER_CHARGED</c> precedes its own <c>UBER</c> because
    /// the attack key is handled a few lines above the right-click. The two can
    /// only coincide on the tick a medic tops up and pops in the same frame,
    /// which needs the beam and the right-click held together.
    /// </para>
    /// <para>
    /// <b>Body is one byte, the player.</b> <c>sendEventUberReady.gml</c> and
    /// <c>sendEventUber.gml</c> are two lines each and neither says anything
    /// else — the charge itself rides the medic's class-specific byte in the
    /// state update.
    /// </para>
    /// </remarks>
    private void BroadcastUbers(int count)
    {
        for (var slot = 0; slot < count; slot++)
        {
            if (_events.UberCharged[slot])
                ServerMessages.UberCharged(_broadcast, (byte)slot);

            if (_events.Ubered[slot])
                ServerMessages.Uber(_broadcast, (byte)slot);
        }
    }

    /// <summary>The wire view of a slot's character, for the state writer.</summary>
    /// <remarks>
    /// ⚠️ <b>The relayed keys are not simply <c>keyState</c>.</b> A key pressed
    /// and released again inside one tick — two <c>INPUTSTATE</c>s coalescing,
    /// which is ordinary over the internet — is in no <c>keyState</c> the tick
    /// ever saw, but the server acted on it through <c>pressedKeys</c>
    /// (<see cref="NetworkInputSource.TakeEdges"/>). Relaying <c>keyState</c>
    /// alone would make the server stab, jump or airblast while every client,
    /// including the player's own, saw nothing — so the tapped bits ride out as
    /// held for exactly this one update, which is the only way the wire can say
    /// "this key was down" at all.
    /// <para>
    /// ⛔ <b><see cref="PlayerKeys.Down"/> is left out, and that is not
    /// symmetry-breaking for its own sake.</b> Every other key reaches the
    /// simulation through <c>downOrPressedKeys</c> — the union of <c>keyState</c>
    /// and <c>pressedKeys</c> — or through a pressed-edge test, so the server DID
    /// act on the tap and a client relaying it as held reproduces that. Down is
    /// read as held only, so the server did nothing with the tap and a client told
    /// it was held would drop the character through a platform the server kept it
    /// on.
    /// </para>
    /// <para>
    /// The byte's width and meaning are unchanged, so a stock client needs no
    /// knowledge of any of this: it reads one tick of a held key, which is what
    /// happened. docs/TRAPS-SIMULATION.md, "Upstream bug fixes carried into the
    /// port".
    /// </para>
    /// </remarks>
    private bool TryGetCharacter(int slot, out CharacterBlock character, out WeaponBlock weapon)
    {
        if (!_world.IsAlive(slot))
        {
            character = default;
            weapon = default;
            return false;
        }

        character = CharacterWire.ToBlock(_world.Characters[slot], _world.Weapons[slot]);

        var tapped = _relayedPresses[slot] & ~PlayerKeys.Down;
        if (tapped != PlayerKeys.None)
            character = character with { Keys = character.Keys | tapped };

        weapon = CharacterWire.ToWeaponBlock(_world, slot);
        return true;
    }

    /// <summary>The wire view of a slot's sentry, for the state writer.</summary>
    private bool TryGetSentry(int slot, out SentryBlock sentry)
    {
        if (slot >= WorldState.MaxSlots || !_world.HasSentry[slot])
        {
            sentry = default;
            return false;
        }

        sentry = CharacterWire.ToSentryBlock(_world.Sentries[slot]);
        return true;
    }

    /// <summary>
    /// Every moving platform's block, straight off the live world.
    /// </summary>
    /// <remarks>
    /// <c>MovingPlatform.events/User Event 10.xml</c>: <c>x_ * 5</c> and
    /// <c>y_ * 5</c> as ushorts, then the movement state. ⚠️ The underscored
    /// pair, so the quantisation is the extension's <c>write_ushort</c> of a real
    /// and nothing else — no GML <c>round()</c> on the way.
    /// </remarks>
    private int MovingPlatformBlocks(Span<MovingPlatformBlock> platforms)
    {
        var count = Math.Min(_world.MovingPlatformCount, platforms.Length);

        for (var i = 0; i < count; i++)
        {
            ref readonly var platform = ref _world.MovingPlatforms[i];

            platforms[i] = new MovingPlatformBlock(
                WireQuantise.ToUShort(platform.X * 5),
                WireQuantise.ToUShort(platform.Y * 5),
                platform.MovementState);
        }

        return count;
    }

    // --- broadcast -----------------------------------------------------------

    /// <summary>
    /// <c>Generator.events/Begin Step.xml:24-25</c>: a live generator asks the
    /// scoreboard to resync every minute, on top of the two-minute heartbeat.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Below <see cref="WriteStateUpdate"/>, so the flag it sets is read
    /// on the NEXT tick.</b> That is the reference's timing, not a delay this
    /// port adds: a <c>Generator</c> is a room object and the GameServer is
    /// persistent and older, so the generator's Begin Step runs after
    /// <c>GameServerBeginStep.gml:55</c> has already tested <c>syncTimer</c>
    /// that frame. Keyed on <c>GameServer.frame</c> rather than living in
    /// <c>GG2.Simulation</c> because that counter is the server's own and not
    /// part of the world — the same reason the map-change countdown is here.
    /// <para>
    /// ⚠️ <b>And ABOVE <see cref="MatchSimulation.Tick"/>, which is the half
    /// that is easy to get wrong.</b> The reference's guard is an
    /// <c>else if</c> under <c>if hp &lt;= 0</c> — a generator about to be
    /// destroyed does not ask — and it reads that <c>hp</c> in the generator's
    /// own Begin Step, before any character has acted. Reading it after the tick
    /// would swallow the request whenever a generator died later in the same
    /// tick. Reachable only on a multiple of 1800, which is precisely the kind
    /// of tick nothing would ever notice going wrong.
    /// </para>
    /// </remarks>
    private void RequestGeneratorScoreboardSync()
    {
        if (_frame % 1800 != 0)
            return;

        if ((_world.RedGenerator.Exists && !Gml.LessOrEqual(_world.RedGenerator.Hp, 0))
            || (_world.BlueGenerator.Exists && !Gml.LessOrEqual(_world.BlueGenerator.Hp, 0)))
        {
            _capsSyncPending = true;
        }
    }

    /// <summary>
    /// <c>GameServerBeginStep.gml:55</c>'s third trigger — the setup timer
    /// reaching 180, six seconds before the gates open.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>After <see cref="MatchSimulation.Tick"/>, unlike
    /// <see cref="RequestGeneratorScoreboardSync"/>.</b> The reference reads a
    /// value the HUD's Step decrements — <c>ControlPointHUD.events/Step.xml</c>
    /// :21-22 and <c>InvasionHUD.events/Step.xml</c>:18-19, both younger than
    /// the GameServer — so the frame that decrements it to 180 is not the frame
    /// that sees 180. <see cref="Rounds.StepSetupTimer"/> is this port's copy of
    /// those five lines, so the value after the tick is the one the reference's
    /// next Begin Step reads.
    /// <para>
    /// ⚠️ <b>This trigger did not exist until 2026-09-02</b>, and the comment
    /// standing where it belonged said it "waits on the setup timer being
    /// simulated" — which had stopped being true at some point nobody recorded:
    /// <c>WorldState.SetupTimer</c> is simulated and goes out on the wire.
    /// CLAUDE.md's "a claim the repo makes about itself", found by reading
    /// rather than by any test, because no test could.
    /// </para>
    /// </remarks>
    private void RequestSetupScoreboardSync()
    {
        if (_world.SetupTimer == SetupSyncTick)
            _capsSyncPending = true;
    }

    private void WriteStateUpdate()
    {
        // Cadence is part of the contract: the client dead-reckons between the
        // position-bearing updates. Every tick but the seventh is an INPUTSTATE.
        // GameServerBeginStep.gml:55-67 — an extra scoreboard message when
        // something asked for one, and a two-minute heartbeat so a client that
        // missed one is corrected eventually.
        //
        // ⛔ NOTHING MAY SET _capsSyncPending FROM IN HERE. `syncTimer` has no
        // writer inside GameServerBeginStep either: all nine of its setters are
        // objects YOUNGER than the GameServer, so every one of them runs after
        // line 55 has already tested it, and the extra message lands on the
        // FOLLOWING tick. A set-and-consume in this method is a CAPS_UPDATE a
        // tick early — which is what the generator's minute timer did from the
        // day it landed until 2026-09-02. The two triggers keyed on the
        // last two that were still asked for up here — the generator's minute
        // timer and the setup timer's six-second mark — are in
        // RequestGeneratorScoreboardSync and RequestSetupScoreboardSync now,
        // below the broadcast with every other request.
        // CapsSyncPhaseTests is the gate.
        if (_capsSyncPending || _frame % 3600 == 0)
        {
            _stateWriter.Write(_broadcast, MessageType.CapsUpdate);
            _capsSyncPending = false;
        }

        var tier = _frame % 7 == 0 ? MessageType.QuickUpdate : MessageType.InputState;
        _stateWriter.Write(_broadcast, tier);
    }

    private void FlushBroadcast()
    {
        if (_broadcast.IsEmpty)
        {
            // ⚠️ THE MARKS DIE WITH THE BUFFER THEY INDEX. A mark is a length in
            // the buffer that was standing when a joiner's burst was written; if
            // this tick had nothing to send, carrying it to the next tick would
            // skip that many bytes of genuine news instead. Nothing observed
            // reaches here with a mark set — WriteStateUpdate appends every tick,
            // frozen or not — which is exactly why it would rot unnoticed.
            foreach (var client in _clients)
                client.SkipBroadcastBytes = 0;

            return;
        }

        _flushes++;

        // One identical byte stream to every client: one serialisation, N sends.
        // Keep it that way — it is why the send path is nearly free.
        var bytes = _broadcast.WrittenSpan;

        foreach (var client in _clients)
        {
            // A client that joined during the PREVIOUS tick skips whatever was
            // already in this buffer when its join burst was written, because
            // the burst's FULL_UPDATE already carries the world those messages
            // describe. Zero for every client on every other tick, so the common
            // path is still the whole span with no slicing.
            // ClientPlayer.SkipBroadcastBytes has the why and the reference's
            // line numbers.
            if (client.SkipBroadcastBytes == 0)
            {
                client.Connection.Send(bytes);
                continue;
            }

            // Clamped rather than trusted. The mark cannot exceed this buffer
            // while the invariant above holds, and a live server must degrade to
            // "send the rest" rather than throw if it ever does.
            var skip = Math.Min(client.SkipBroadcastBytes, bytes.Length);
            client.SkipBroadcastBytes = 0;
            client.Connection.Send(bytes[skip..]);
        }

        // ⚠️ The recording tees HERE and nowhere else, which is what makes it
        // exactly what the clients saw rather than a reconstruction of it.
        // docs/BOTAI.md item 3: one recorder, not a second logging path.
        _recorder?.Append(bytes);

        _broadcast.Reset();
    }
}
