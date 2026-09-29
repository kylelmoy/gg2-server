using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Server;

public sealed partial class GameServer
{
    // --- the roster and its clients -----------------------------------------

    private sealed class ClientPlayer
    {
        public required ClientConnection Connection { get; init; }

        public required RosterEntry Entry { get; init; }

        public ReceiveBuffer Received { get; } = new();

        public CommandStream Commands { get; } = new();

        /// <summary>
        /// Whether this connection's unrecognised bytes have been reported.
        /// </summary>
        /// <remarks>
        /// One line per connection, not per run: the line carries the first
        /// runs and the running totals, so a second one for the same connection
        /// would repeat the evidence while adding a rate a peer chooses.
        /// </remarks>
        public bool ReportedInvalidCommands { get; set; }

        /// <summary>
        /// Bytes already standing in the broadcast buffer when this client
        /// joined, which its next flush must skip.
        /// </summary>
        /// <remarks>
        /// ⚠️ <b>Everything appended between a tick's flush and its join burst
        /// is ALREADY IN that burst's <c>FULL_UPDATE</c>, so sending it as well
        /// tells the joiner the same thing twice.</b> The burst is written at
        /// the very bottom of the tick (<c>ServiceJoiningConnections</c>), while
        /// the buffer it would otherwise inherit was filled above it by
        /// <c>BroadcastEndStepEvents</c> and by the control port and web admin
        /// executing against the live game. Those messages describe a world the
        /// burst has already serialised.
        /// <para>
        /// The two costs of not skipping, both measured against a real client:
        /// a <c>PLAYER_DEATH</c> for a character the joiner never had makes
        /// <c>doEventPlayerDeath</c> dereference <c>victim.object.currentWeapon</c>
        /// on <c>-1</c> (<c>Scripts/Events/doEventPlayerDeath.gml:41</c>), and a
        /// <c>PLAYER_JOIN</c> or <c>PLAYER_LEAVE</c> from a kick moves a roster
        /// the burst already replayed — which is the roster-count disagreement
        /// <c>deserializeState.gml:11-13</c> only warns about before
        /// deserialising misaligned. docs/PARITY.md, "A joiner was sent the tick
        /// it had already been given".
        /// </para>
        /// <para>
        /// ⚠️ <b>The reference does not need this because its burst predates
        /// those events</b>, not because it sends them safely:
        /// <c>GameServerEndStep.gml:18-20</c> services a joining player in the
        /// GameServer's End Step, which is the FIRST End Step of the frame, so
        /// <c>Character.events/End Step.xml:43</c>'s <c>doEventPlayerDeath</c>
        /// has not run yet and the dying character is still in the
        /// <c>serializeState</c> the joiner is handed. The port fuses Step and
        /// End Step into <c>AdvanceSimulation</c>, so it cannot serialise
        /// between them and closes the gap from the other end instead: one
        /// consistent story rather than the state from after the events and the
        /// events as well.
        /// </para>
        /// </remarks>
        public int SkipBroadcastBytes { get; set; }

        /// <summary>
        /// This connection's analytics session, or null when analytics are off.
        /// </summary>
        /// <remarks>
        /// ⚠️ <b>Held here rather than looked up on the way out.</b> The store
        /// keys on a hash of the address, and by the time
        /// <c>RemoveClient</c> runs the socket may already be faulted — so
        /// re-deriving the key from <c>Connection.RemoteAddress</c> at that
        /// point would silently lose the playtime of exactly the disconnections
        /// that were not clean.
        /// </remarks>
        public AnalyticsSession? Analytics { get; set; }
    }

    private void ServiceClients()
    {
        // Reverse order so a removal does not skip the next entry, and so the
        // roster indices of everything still to be serviced stay put.
        for (var i = _clients.Count - 1; i >= 0; i--)
        {
            var client = _clients[i];

            client.Connection.Receive(client.Received);

            if (client.Connection.IsClosed)
            {
                RemoveClient(i, client.Connection.CloseReason ?? "connection lost");
                continue;
            }

            // ⚠️ THE CONTAINMENT THAT MATTERS, and it is here rather than around
            // the tick because here is where the blast radius can be made
            // one connection wide. Everything below this call is driven by bytes
            // one client chose: a payload the framer sized from a length the
            // client sent, a plugin's own parser, an index derived from a value
            // in the packet. A throw from any of it used to take the whole tick,
            // and Run had no guard at all — so one client could end the process
            // for everybody on the server.
            //
            // Dropping the client is the least-bad answer, not a good one. The
            // command was applied part-way, so WorldState may have a half-made
            // change in it; the alternative is losing every player rather than
            // one. Run's TickFaultBudget is what catches the case where this
            // keeps happening.
            //
            // ⛔ Logged at error, never swallowed. A fault here is a bug in the
            // port or a client doing something nobody has seen, and both are
            // things to go and read the trace of.
            try
            {
                ProcessCommands(client);
            }
            catch (Exception e)
            {
                _log.Error(
                    $"dropping {LogText.Safe(client.Entry.Name)}: their commands faulted the server", e);

                client.Connection.Close("their commands faulted the server");
            }

            if (client.Connection.IsClosed)
                RemoveClient(i, client.Connection.CloseReason ?? "disconnected");
        }
    }

    /// <summary>
    /// The bots' half of the roster service loop: the commands a key cannot
    /// express, asked for once per bot per tick.
    /// </summary>
    /// <remarks>
    /// <c>GameServerBeginStep.gml:79-85</c> puts <c>botInputUpdate(player)</c>
    /// in the same loop and the same position as
    /// <c>processClientCommands(player, i)</c> — a bot is serviced exactly where
    /// a socket is. The <em>keys</em> half of that is
    /// <see cref="GatherInputs"/>, which runs for every slot including this one;
    /// what is left here is <see cref="ICommandSource"/>.
    /// <para>
    /// A separate ascending pass rather than one interleaved loop, because
    /// <see cref="ServiceClients"/> walks its clients <em>backwards</em> so a
    /// disconnect cannot skip the next one — and a departure compacts the
    /// roster, so running after it is what keeps every slot index below stable.
    /// The cost is that a bot's broadcasts follow the humans' within a tick
    /// where the reference interleaves them by roster index. Nothing observes
    /// it: commands from different players produce independent messages, and no
    /// recording in this project contains a bot at all.
    /// </para>
    /// </remarks>
    private void ServiceBots()
    {
        for (var slot = 0; slot < _roster.Count; slot++)
        {
            var entry = _roster[slot];

            if (entry.Source is not ICommandSource source)
                continue;

            _commands.Clear();

            // Same containment as ServiceClients, one layer in. A bot is this
            // project's own code rather than an attacker's, so a fault here is a
            // bug rather than an attack — but the consequence of not catching it
            // is identical: every human on the server loses their game because
            // one bot's decision code threw. The bot is removed rather than
            // retried, because a bot that faulted once on this world state will
            // fault again on the next tick's, and a log line every 33 ms is the
            // second outage.
            //
            // ⛔ Logged at error and never swallowed: an unhandled exception out
            // of the bot layer is a bug to go and read the trace of.
            try
            {
                source.PollCommands(new WorldView(in _world, _collision), slot, (int)_frame, ref _commands);
            }
            catch (Exception e)
            {
                _log.Error($"removing bot {LogText.Safe(entry.Name)} in slot {slot}: it faulted", e);
                _faultedBots.Add(entry);
                continue;
            }

            for (var i = 0; i < _commands.Count; i++)
            {
                Execute(entry, _commands[i]);

                // ⚠️ A command can move the roster under this loop: a class or
                // team change can kill, and a kill can autobalance somebody
                // across. It cannot *remove* an entry — only a disconnect does
                // that, and a bot has no socket — so the index stays this
                // player's. Re-reading the entry is not enough on its own,
                // which is why every Execute* below takes the entry and asks
                // IdOf for the slot rather than trusting one captured here.
            }
        }

        // ⚠️ AFTER the loop, not inside it. RemoveBot compacts the roster and
        // renumbers every slot above the hole, so removing mid-pass would hand
        // the next iteration a different player than the index it holds — the
        // same trap WorldState.RemoveSlot carries, one layer out. The entries
        // are resolved back to slots here because they have not moved yet.
        if (_faultedBots.Count == 0)
            return;

        foreach (var entry in _faultedBots)
        {
            var slot = _roster.IndexOf(entry);

            if (slot > 0)
                RemoveBot(slot);
        }

        _faultedBots.Clear();
    }

    /// <summary>
    /// Adds a bot to the roster and joins it to the game the way a client is
    /// joined — <c>Scripts/Bots/botAdd.gml</c>.
    /// </summary>
    /// <remarks>
    /// The reference's order exactly: roster insertion first, so the id is the
    /// index it lands at; <c>PLAYER_JOIN</c> broadcast <em>before</em> the team
    /// and class are known; <c>checkClasslimits</c>; then <c>CHANGETEAM</c> and
    /// <c>CHANGECLASS</c> — ⚠️ <b>team first</b>, which is the opposite of the
    /// order <see cref="CompleteJoin"/> replays an existing roster in, and is
    /// what the reference does. Finally <c>alarm[5] = 1</c> for anything but a
    /// spectator: a spawn in this same tick's alarm stage.
    /// <para>
    /// ⚠️ <b>There is no <c>FULL_UPDATE</c> burst and no
    /// <see cref="ClientPlayer"/>.</b> Those exist to catch a joining socket up;
    /// a bot has no socket, and the state it needs is the one it is already
    /// standing in.
    /// </para>
    /// <para>
    /// Bots do not count against <see cref="ServerOptions.PlayerLimit"/> —
    /// <c>getNumberOfOccupiedSlots</c> subtracts them — but they do occupy
    /// roster slots, and <see cref="WorldState.MaxSlots"/> is the hard ceiling
    /// on those.
    /// </para>
    /// </remarks>
    /// <param name="source">
    /// What this bot presses. May also implement <see cref="ICommandSource"/>
    /// and <see cref="IBatchInputSource"/>.
    /// </param>
    /// <returns>The roster slot the bot landed in.</returns>
    public int AddBot(string name, Team team, PlayerClass playerClass, IInputSource source)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(source);

        if (_roster.Count >= WorldState.MaxSlots)
            throw new InvalidOperationException($"the roster is full at {WorldState.MaxSlots} slots");

        var entry = new RosterEntry { Name = name, IsBot = true, Source = source };
        var slot = _roster.Add(entry);
        _plugins.PlayerAdded(slot);
        _world.AddPlayer(slot);

        // ⚠️ Mirrored into the world so BOT CODE can tell a person from a bot —
        // Handover.ShouldEngage under EngageOpponents.HumansOnly. ⛔ Nothing in
        // MatchSimulation may read it; BotIsNotVisibleToTheSimulationTests is the
        // gate. It goes here rather than being read off the roster so a bot's
        // decisions stay a pure function of WorldState and remain replayable.
        //
        // ⚠️ AFTER AddPlayer, which writes PlayerState.Fresh over the slot.
        _world.Players[slot].IsBot = true;

        ServerMessages.PlayerJoin(_broadcast, entry.Name);

        // botAdd.gml:26 — the class it counts with is the one checkClasslimits
        // handed back, not the one asked for.
        var allowed = Classlimits.Pick(ref _world, slot, team, playerClass);

        entry.Team = team;
        entry.Class = allowed;
        _world.Players[slot].Team = team;
        _world.Players[slot].Class = allowed;

        ServerMessages.PlayerChangeTeam(_broadcast, (byte)slot, team);
        ServerMessages.PlayerChangeClass(_broadcast, (byte)slot, allowed);

        if (team != Team.Spectator)
            _world.Players[slot].RespawnTimer = 1;

        _log.Info($"bot {LogText.Safe(name)} added as player {slot} ({team}, {allowed})");
        return slot;
    }

    /// <summary>
    /// Removes a bot, mirroring the disconnect path —
    /// <c>Scripts/Bots/botRemove.gml</c>.
    /// </summary>
    /// <remarks>
    /// The same <see cref="WorldState.RemoveSlot"/> a departure runs, and for
    /// the same reason: a slot index <em>is</em> a roster index, so everything
    /// above the hole shifts down on both sides of the wire at once. Everything
    /// the bot owned goes with it — its character, its sentry and every
    /// projectile of its still in the air (docs/SIMULATION.md, "what a
    /// departure destroys").
    /// </remarks>
    public void RemoveBot(int slot)
    {
        if (slot <= 0 || slot >= _roster.Count || !_roster[slot].IsBot)
            throw new ArgumentOutOfRangeException(nameof(slot), slot, "not a bot slot");

        var name = _roster[slot].Name;

        // ⛔ BEFORE the removal, so a plugin's per-player table compacts with the
        // roster rather than after it. PluginPlayerTable says what goes wrong
        // otherwise, and it is the same shape as the WorldState.RemoveSlot trap.
        _plugins.PlayerRemoving(slot);

        _roster.RemoveAt(slot);
        _world.RemoveSlot(slot);

        ServerMessages.PlayerLeave(_broadcast, (byte)slot);

        // GameServerBeginStep.gml:70's ServerBalanceTeams() after a departure,
        // which botRemove reaches through removePlayer for the same reason.
        TeamBalance.Check(ref _world, ref _events);
        BroadcastTeamBalance();

        _log.Info($"bot {LogText.Safe(name)} removed from player {slot}");
    }

    private void ProcessCommands(ClientPlayer client)
    {
        // At most ten completed commands per player per tick; the rest stay
        // buffered. Flood protection, from processClientCommands.gml.
        var remaining = ClientCommands.PerTickLimit;

        while (remaining > 0 && client.Commands.TryRead(client.Received, out var command))
        {
            remaining--;
            Handle(client, command);

            if (client.Connection.IsClosed)
                return;
        }

        // ⚠️ The gap the ten-command budget leaves open. An UNRECOGNISED command
        // byte is skipped rather than kicked — the reference's behaviour, kept
        // deliberately (ClientCommands) — and skipping does not spend one of the
        // ten, so garbage is the one thing a client can send that the
        // reference's own flood control does not rate-limit at all. It makes the
        // server scan every byte, every tick, for nothing.
        //
        // A stray byte is free, and a client that sends nothing but garbage is
        // not. What sits between those is not settled: a stock client has hit
        // the threshold once (ConnectionLimits.MaxInvalidCommandBytes), which is
        // why the line below carries the evidence and not just the count.
        //
        // The line goes out the FIRST time this connection skips anything,
        // while it is still up, because that is the only moment the claim above
        // is checkable: waiting for the threshold means learning nothing until
        // 4096 bytes have gone past, and learning it as the evidence is being
        // disconnected. InvalidCommandReport says what a run's shape means.
        if (client.Commands.InvalidBytesSkipped > 0 && !client.ReportedInvalidCommands)
        {
            client.ReportedInvalidCommands = true;

            if (_invalidCommandLog.ShouldLog(_nowMs, out var suppressed))
            {
                _log.Warn(
                    $"{LogText.Safe(client.Entry.Name)}: "
                    + InvalidCommandReport.Describe(client.Commands)
                    + Suffix(suppressed));
            }
        }

        if (client.Commands.InvalidBytesSkipped > _limits.MaxInvalidCommandBytes)
        {
            // ⚠️ The report goes in a line of its own, not in the close reason.
            // The reason reaches the log through LogText.Safe, which caps a
            // client-controlled string at 64 characters — so a report handed to
            // it is clipped mid-run, in the one line an operator is most likely
            // to read. The reason stays the short count it has always been.
            // (Nothing here reaches the wire: Close only records the reason.)
            _log.Warn(
                $"{LogText.Safe(client.Entry.Name)}: dropping - "
                + InvalidCommandReport.Describe(client.Commands));

            client.Connection.Close(
                $"{client.Commands.InvalidBytesSkipped} unrecognised command bytes");
        }
    }

    private void Handle(ClientPlayer client, in ClientCommand command)
    {
        var entry = client.Entry;
        var reader = new NetReader(command.Payload);

        switch (command.Type)
        {
            case MessageType.PlayerLeave:
                client.Connection.Close("client sent PLAYER_LEAVE");
                break;

            case MessageType.InputState:
                // The one command that is not a PlayerCommand: it *is* the
                // per-tick input, and it reaches the simulation through the
                // seam like everything else. A client always has a
                // NetworkInputSource behind it, so the cast never fails.
                ((NetworkInputSource)entry.Source).Latest = PlayerInput.Read(ref reader);
                break;

            case MessageType.PlayerChangeTeam:
                ExecuteChangeTeam(entry, (Team)reader.ReadUByte());
                break;

            case MessageType.PlayerChangeClass:
            {
                var requested = (PlayerClass)reader.ReadUByte();
                if (!Enum.IsDefined(requested))
                    break; // getCharacterObject() == -1: the reference ignores it.

                ExecuteChangeClass(entry, requested);
                break;
            }

            case MessageType.ChatBubble:
                ExecuteChatBubble(entry, reader.ReadUByte());
                break;

            case MessageType.ClientSettings:
                ExecuteClientSettings(entry, reader.ReadUByte() != 0);
                break;

            case MessageType.PlayerChangeName:
                ExecuteChangeName(client, entry, command.Payload);
                break;

            case MessageType.BuildSentry:
                ExecuteBuildSentry(IdOf(entry));
                break;

            case MessageType.DestroySentry:
                ExecuteDestroySentry(IdOf(entry));
                break;

            case MessageType.DropIntel:
                ExecuteDropIntel(IdOf(entry));
                break;

            case MessageType.ToggleZoom:
                ExecuteToggleZoom(IdOf(entry));
                break;

            case MessageType.Omnomnomnom:
                ExecuteEatSandvich(IdOf(entry));
                break;

            // ⚠️ An UNRECOGNISED plugin packet is KICKED, not ignored, and that
            // IS the reference's behaviour rather than a policy of ours.
            // processClientCommands.gml:338-362 hands the packet to
            // _PluginPacketPush, which returns false when the id is not in
            // global.pluginPacketBuffers — a map populated only by
            // loadserverplugins, and keyed by each plugin's position in the
            // advertised list. So a server with no plugins kicks EVERY sender,
            // and a server with two plugins kicks any id above 1.
            //
            // ⚠️ Unreachable from a stock client whose plugin list matches the
            // greeting's, which is the whole point: the client derives the id
            // from the same list in the same order. Reaching the kick means a
            // client that made an id up.
            case MessageType.PluginPacket:
            {
                // The container's own layout: one id byte, then the plugin's
                // data. The two-byte length prefix has already been consumed by
                // the command stream, so Payload starts at the id.
                if (command.Payload.Length >= 1
                    && _plugins.Dispatch(command.Payload[0], IdOf(entry), command.Payload[1..]))
                {
                    break;
                }

                var kick = new NetWriter(2);
                ServerMessages.Kick(kick, KickReason.BadPluginPacket);
                client.Connection.Send(kick.WrittenSpan);

                // ⚠️ Flush BEFORE closing, or the kick is buffered and thrown
                // away and the client just sees a dropped connection with no
                // reason. The reference writes to the socket and then destroys
                // it, which has the same requirement; the multi-client refusal
                // above is the other place in this file that gets it right.
                client.Connection.Flush();
                client.Connection.Close("unrecognised plugin packet id");

                _log.Warn(
                    $"kicked {LogText.Safe(entry.Name)}: plugin packet id "
                    + (command.Payload.Length >= 1 ? command.Payload[0].ToString() : "(empty)")
                    + $", but this server advertises {_plugins.Count} plugin(s)");
                break;
            }

            // ⚠️ The client is not told that rewards are off, because the
            // reference has no way to say so — there is no "rewards disabled"
            // message and no flag in the greeting. A client whose REWARD_REQUEST
            // goes unanswered simply never gets its hats, and it neither retries
            // nor blocks (ClientBeginStep.gml:163-170 sends it once, on join).
            case MessageType.RewardRequest:
                ExecuteRewardRequest(client, entry, command.Payload);
                break;

            case MessageType.RewardChallengeResponse:
                ExecuteRewardChallengeResponse(entry, command.Payload);
                break;

            default:
                // Unreachable: CommandStream only yields commands in the table.
                break;
        }
    }
}
