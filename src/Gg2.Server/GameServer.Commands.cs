using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Server;

public sealed partial class GameServer
{
    // --- the commands that are not keys ---------------------------------------
    //
    // Every one of these is reached from two places: a socket packet, and a bot
    // that asked for it through ICommandSource. ⚠️ They must stay ONE copy.
    // Scripts/Bots/botServerActions.gml is explicit about why — "a bot performs
    // them by calling the same server-side scripts the network path calls …
    // each of which re-checks its own preconditions" — and the reference
    // extracted serverBuildSentry.gml and serverToggleZoom.gml out of
    // processClientCommands.gml for exactly that reason. A second copy here
    // would let a bot build a sentry a client could not.

    /// <summary>
    /// Runs one <see cref="PlayerCommand"/> for a slot, whoever asked for it.
    /// </summary>
    /// <remarks>
    /// A command whose preconditions fail is silence, never an error: that is
    /// the reference's guard style throughout <c>processClientCommands.gml</c>,
    /// and <c>botServerActions.gml</c> leans on it — "a wrong guess here is
    /// ignored rather than harmful".
    /// </remarks>
    private void Execute(RosterEntry entry, in PlayerCommand command)
    {
        switch (command.Kind)
        {
            case PlayerCommandKind.BuildSentry:
                ExecuteBuildSentry(IdOf(entry));
                break;

            case PlayerCommandKind.DestroySentry:
                ExecuteDestroySentry(IdOf(entry));
                break;

            case PlayerCommandKind.ToggleZoom:
                ExecuteToggleZoom(IdOf(entry));
                break;

            case PlayerCommandKind.DropIntel:
                ExecuteDropIntel(IdOf(entry));
                break;

            case PlayerCommandKind.EatSandvich:
                ExecuteEatSandvich(IdOf(entry));
                break;

            case PlayerCommandKind.ChangeClass:
                // The socket path's Enum.IsDefined guard, for the same reason:
                // getCharacterObject() == -1 is ignored by the reference.
                if (Enum.IsDefined(command.AsClass))
                    ExecuteChangeClass(entry, command.AsClass);
                break;

            case PlayerCommandKind.ChangeTeam:
                ExecuteChangeTeam(entry, command.AsTeam);
                break;

            // The same body the socket switch reaches, so the April Fools
            // rewrite and the per-player throttle apply to a bot's bubble
            // exactly as they do to a person's.
            case PlayerCommandKind.ChatBubble:
                ExecuteChatBubble(entry, command.Argument);
                break;

            case PlayerCommandKind.None:
            default:
                break;
        }
    }

    private void ExecuteChangeTeam(RosterEntry entry, Team requested)
    {
        var slot = IdOf(entry);

        // The balance block: "any" resolves to the smaller team, a
        // request for the bigger team — or the one they are already on
        // — is refused with silence.
        var resolved = PlayerCommands.ResolveRequestedTeam(ref _world, slot, requested);

        if (resolved is not Team team)
            return;

        ApplyChangeTeam(entry, team);
    }

    /// <summary>
    /// The half of a team change after the request has been resolved: the
    /// death, the class re-check, the two broadcasts and the rebalance.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Split out so a FORCED move reuses this and nothing else.</b> A
    /// scramble has to put people on the team it picked, including the bigger
    /// one, so it cannot go through <see cref="PlayerCommands.ResolveRequestedTeam"/>
    /// — and the reference's own scramble plugin skips exactly that check and
    /// nothing else, writing <c>player.team</c> and calling
    /// <c>ServerPlayerChangeteam</c> directly.
    /// <para>
    /// ⚠️ The rule this keeps is CLAUDE.md's for bots, one layer out: an
    /// operator or a plugin uses the <em>same</em> method a client command uses,
    /// so nothing can do something a client cannot. Reaching into
    /// <see cref="WorldState"/> from either would be the bug this shape
    /// prevents.
    /// </para>
    /// </remarks>
    private void ApplyChangeTeam(RosterEntry entry, Team team)
    {
        var slot = IdOf(entry);

        // getCharacterObject(player.class) != -1 or TEAM_SPECTATOR:
        // every class the port accepts has a character object, so the
        // clause is always true here.

        // A change with a live character is a death, announced before
        // the change itself — the reference's own stream order. The
        // simulation half also arms the respawn and remembers the team;
        // the sentry notices on its own next Begin Step, exactly one
        // tick later like the reference's guard.
        var death = PlayerCommands.ChangeTeamWithBalance(
            ref _world, slot, team, ref _events, out var reclassed);

        if (death is PlayerDeathEvent died)
            ServerMessages.PlayerDeath(
                _broadcast, (byte)slot, died.Killer, died.Assistant, died.Source);

        // ⚠️ checkClasslimits runs *before* the team message and is
        // announced separately, and only when it actually moved the
        // class — processClientCommands.gml:181-186.
        if (reclassed is PlayerClass forced)
        {
            entry.Class = forced;
            ServerMessages.PlayerChangeClass(_broadcast, (byte)slot, forced);
        }

        entry.Team = team;
        ServerMessages.PlayerChangeTeam(_broadcast, (byte)slot, team);

        // And ServerBalanceTeams(), which ChangeTeamWithBalance has
        // already run into _events — the messages it produced go out
        // after this one, which is the reference's order.
        BroadcastTeamBalance();
    }

    private void ExecuteChangeClass(RosterEntry entry, PlayerClass requested)
    {
        var slot = IdOf(entry);

        // A live character dies for the swap — BID_FAREWELL by their
        // own hand, or FINISHED_OFF for whoever had hurt them — and the
        // death precedes the class message, the reference's order. A
        // characterless player is queued to spawn in this same tick's
        // alarm stage.
        if (PlayerCommands.ChangeClass(ref _world, slot, requested, _collision) is PlayerDeathEvent death)
            ServerMessages.PlayerDeath(
                _broadcast, (byte)slot, death.Killer, death.Assistant, death.Source);

        entry.Class = requested;
        ServerMessages.PlayerChangeClass(_broadcast, (byte)slot, requested);
    }

    /// <summary>
    /// <c>CLIENT_SETTINGS</c>: one client's queued-jumping preference.
    /// </summary>
    /// <remarks>
    /// <c>processClientCommands.gml:364-371</c>. Three writes, and none of them
    /// is redundant: the roster copy is what a joiner's <c>FULL_UPDATE</c>
    /// carries, the <see cref="WorldState"/> copy is what the physics reads, and
    /// the broadcast is what already-connected clients dead-reckon with. Missing
    /// any one of them puts somebody's jump on a different rule from everybody
    /// else's.
    /// </remarks>
    private void ExecuteClientSettings(RosterEntry entry, bool queueJump)
    {
        var slot = IdOf(entry);

        entry.QueueJump = queueJump;
        _world.Players[slot].QueueJump = queueJump;

        ServerMessages.ClientSettings(_broadcast, (byte)slot, queueJump);
    }

    /// <summary>
    /// <c>PLAYER_CHANGENAME</c>: rename, throttle, or kick.
    /// </summary>
    /// <remarks>
    /// <c>processClientCommands.gml:279-317</c>, and ⚠️ <b>an over-long name is
    /// a KICK here, not a truncation.</b> The join path truncates
    /// (<c>serviceJoiningPlayer.gml:176</c>) and this one does not, which looks
    /// like an inconsistency in the reference and is not ours to tidy: a client
    /// that renames past the limit is one that did not write the message its own
    /// UI can produce.
    /// <para>
    /// ⚠️ <b>The throttle is one change per second, and it is the flood
    /// control.</b> Every accepted rename is an unbounded broadcast to every
    /// connected client; without it one socket can make the server send as fast
    /// as it can read. The reference keys it on <c>current_time</c> per player.
    /// </para>
    /// </remarks>
    private void ExecuteChangeName(ClientPlayer client, RosterEntry entry, ReadOnlySpan<byte> payload)
    {
        // ⚠️ The reference tests the PAYLOAD length in bytes, before decoding —
        // `socket_receivebuffer_size(socket)`, which is the count the length
        // prefix declared. Testing the decoded string instead would let a name
        // of 30 bytes that decodes to 20 characters through.
        if (payload.Length > GameConstants.MaxPlayerNameLength)
        {
            var kick = new NetWriter(2);
            ServerMessages.Kick(kick, KickReason.Name);
            client.Connection.Send(kick.WrittenSpan);

            // Flush before closing, or the reason is buffered and discarded and
            // the client sees only a dropped connection — the same requirement
            // the plugin-packet and multi-client kicks have.
            client.Connection.Flush();
            client.Connection.Close("name longer than the protocol allows");

            _log.Warn($"kicked {LogText.Safe(entry.Name)}: PLAYER_CHANGENAME of {payload.Length} bytes");
            return;
        }

        // ⚠️ The throttled command is DROPPED, not deferred. The reference's
        // `break` leaves the socket's other commands to carry on, so a client
        // spamming renames simply has most of them ignored.
        if (entry.LastNameChangeMs is { } last && _nowMs - last < NameChangeIntervalMs)
            return;

        entry.LastNameChangeMs = _nowMs;
        entry.Name = Gg2Text.Decode(payload);

        ServerMessages.PlayerChangeName(_broadcast, IdOf(entry), entry.Name);
    }

    /// <summary>
    /// <c>CHAT_BUBBLE</c>: rewrite it if the calendar says so, then relay — at
    /// most one per player per <see cref="ConnectionLimits.ChatBubbleIntervalMs"/>.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The throttle is a DELIBERATE DEPARTURE.</b>
    /// <c>processClientCommands.gml:194-206</c> relays every bubble it is sent,
    /// and this drops some of them. The reasoning is the reference's own, applied
    /// one case further: it throttles <c>PLAYER_CHANGENAME</c> because "every
    /// accepted rename is an unbounded broadcast to every connected client" —
    /// which is equally true of the bubble in the case beside it, and equally
    /// exploitable, since <c>ClientCommands.PerTickLimit</c> lets one socket send
    /// ten a tick. On a LAN with friends it never came up.
    /// <para>
    /// ⚠️ <b>The throttled bubble is DROPPED, not deferred</b> — the same shape
    /// as the rename above, and for the same reason: queueing would move the
    /// flood rather than end it, and a bubble that appears two seconds after the
    /// keypress is worse than one that never appears.
    /// </para>
    /// <para>
    /// The April Fools rewrite still runs first, so the two behaviours compose
    /// the way they read: what is relayed is what the reference would have
    /// relayed, only less often. <c>ChatBubbleIntervalMs = 0</c> restores the
    /// reference exactly.
    /// </para>
    /// </remarks>
    private void ExecuteChatBubble(RosterEntry entry, byte requested)
    {
        if (!ChatBubbles.Rewrite(requested, _aprilFools, out var image))
            return;

        var interval = _limits.ChatBubbleIntervalMs;

        if (interval > 0
            && entry.LastChatBubbleMs is { } last
            && _nowMs - last < interval)
        {
            return;
        }

        entry.LastChatBubbleMs = _nowMs;
        ServerMessages.ChatBubble(_broadcast, IdOf(entry), image);
    }

    /// <summary>
    /// <c>REWARD_REQUEST</c>: mint a nonce and challenge the client.
    /// </summary>
    /// <remarks>
    /// <c>processClientCommands.gml:320-326</c>, which answers <b>every</b>
    /// request unconditionally — there is no server-side auth configuration in
    /// the reference at all, and the <c>RewardKey</c>/<c>RewardId</c> pair a host
    /// can set controls only its <em>own</em> self-auth. This port gates the
    /// reply on <c>--rewards</c> instead, because answering commits it to an
    /// outbound connection to a third party later in the handshake.
    /// <para>
    /// ⚠️ <b>A second request overwrites the first.</b> The reference stores the
    /// id and challenge in plain player variables and does the same. It costs
    /// nothing, because <see cref="RosterEntry.RewardAuthStarted"/> is what
    /// actually bounds the work a client can cause.
    /// </para>
    /// </remarks>
    private void ExecuteRewardRequest(ClientPlayer client, RosterEntry entry, ReadOnlySpan<byte> payload)
    {
        if (_rewards is null)
            return;

        entry.RewardId = Gg2Text.Decode(payload);
        entry.RewardChallenge = RewardAuth.NewChallenge();

        // ⚠️ To this socket, not to _broadcast. A challenge addressed to
        // everybody would leak it and hand every other client a message its
        // stream cannot parse.
        var writer = new NetWriter(1 + RewardAuth.ChallengeBytes);
        ServerMessages.RewardChallengeCode(writer, entry.RewardChallenge);
        client.Connection.Send(writer.WrittenSpan);
    }

    /// <summary>
    /// <c>REWARD_CHALLENGE_RESPONSE</c>: forward the client's answer to the auth
    /// server.
    /// </summary>
    /// <remarks>
    /// <c>processClientCommands.gml:328-336</c>. ⛔ <b>This server verifies
    /// nothing</b> — it does not have the client's key and is not meant to. It
    /// relays, and the auth server is the only party that can tell a real answer
    /// from a made-up one.
    /// <para>
    /// The reference's two guards are both here: <c>variable_local_exists</c> on
    /// the id and challenge, which drops a response to a challenge that was
    /// never issued, and <c>rewardAuthAlreadyStarted</c>, which bounds a client
    /// to one outbound connection for the life of its session.
    /// </para>
    /// </remarks>
    private void ExecuteRewardChallengeResponse(RosterEntry entry, ReadOnlySpan<byte> payload)
    {
        if (_rewards is null)
            return;

        if (entry.RewardId is null || entry.RewardChallenge is null)
            return;

        if (entry.RewardAuthStarted)
            return;

        entry.RewardAuthStarted = true;

        // ipCheck: true. The auth server folds the address this request arrives
        // from into the HMAC it recomputes, which is what stops this server
        // replaying the client's answer at a different one. Only the reference's
        // self-auth path passes false, and this port has no local player to
        // decorate — docs/PROTOCOL.md, "What a headless server can skip".
        _rewards.Enqueue(
            entry,
            RewardAuth.BuildAuthRequest(payload, entry.RewardChallenge, ipCheck: true, entry.RewardId));
    }

    /// <summary>
    /// Applies whatever auth checks finished since the last tick.
    /// </summary>
    /// <remarks>
    /// <c>rewardAuthSuccess.gml</c> → <c>sendEventUpdateRewards</c>. ⛔ <b>The
    /// roster index is resolved HERE, not when the check was queued.</b> Several
    /// seconds pass over an auth round trip, and any <c>PLAYER_LEAVE</c> in that
    /// window compacts every index above the hole — so the slot that asked is
    /// not necessarily the slot that answers to that number now.
    /// <see cref="Roster.IndexOf"/> on the entry object is the reference's
    /// <c>instance_exists(currentPlayer)</c>: -1 means they left, and the result
    /// is dropped.
    /// </remarks>
    private void ServiceRewardAuth()
    {
        if (_rewards is null)
            return;

        while (_rewards.TryTakeResult(out var result))
        {
            if (result.Rewards is null)
                continue;

            var slot = _roster.IndexOf(result.Player);
            if (slot < 0)
                continue;

            // Remembered as well as broadcast: the broadcast reaches whoever is
            // connected now, and this is what a later joiner's FULL_UPDATE
            // carries.
            result.Player.Rewards = result.Rewards;

            ServerMessages.RewardUpdate(_broadcast, (byte)slot, result.Rewards);
            _log.Info($"rewards for {LogText.Safe(result.Player.Name)} authenticated");
        }
    }

    private void ExecuteBuildSentry(int slot)
    {
        // serverBuildSentry.gml: check, broadcast, build. A failed
        // precondition is silence, exactly as the reference's guard
        if (!Sentries.CanBuild(_world, slot, _collision))
            return;

        ref readonly var builder = ref _world.Characters[slot];

        ServerMessages.BuildSentry(
            _broadcast,
            (byte)slot,
            WireQuantise.ToUShort(Gml.Round(builder.X * 5)),
            WireQuantise.ToUShort(Gml.Round(builder.Y * 5)),
            (sbyte)builder.Facing);

        // The build itself uses the *exact* position; only the
        // broadcast is quantised, like a character's own block. The
        // team comes off the world's own player state.
        Sentries.Build(ref _world, slot, builder.X, builder.Y, builder.Facing);
    }

    private void ExecuteDestroySentry(int slot)
    {
        // processClientCommands.gml: a bare instance_destroy, and no
        // broadcast at all — clients notice through the subobject bit
        // of the next update. The Destroy event still runs, so the
        // owner is shoved if airborne and stuck mines drop.
        if (slot < WorldState.MaxSlots && _world.HasSentry[slot])
            Sentries.Destroy(_collision, ref _world, slot);
    }

    private void ExecuteDropIntel(int slot)
    {
        // processClientCommands.gml:221 — the player asked to put it
        // down. Unlike the death drop this one *is* broadcast, and the
        // reference sends it before doing it (sendEventDropIntel then
        // doEventDropIntel); the order is unobservable because the
        // message carries nothing but the player.
        //
        // ⚠️ It is a command and not a keypress, so it never reaches
        // MatchSimulation.Tick — the same shape as BUILD_SENTRY, and
        // the reason Intel.Drop is public.
        if (Intel.Drop(ref _world, slot))
            ServerMessages.DropIntel(_broadcast, (byte)slot);
    }

    private void ExecuteEatSandvich(int slot)
    {
        // serverEatSandvich.gml, and the same shape as the zoom beside it: the
        // preconditions live in the simulation, and a refusal is silence.
        //
        // ⚠️ The broadcast carries no timer and no hp. The client re-derives
        // the cooldown itself from the same OMNOMNOMNOM byte
        // (ClientBeginStep.gml:386-400), and the healing arrives through the
        // ordinary position updates a tick at a time — so a client that missed
        // this message sees a heavy healing for no reason, not a desynchronised
        // one.
        if (PlayerCommands.EatSandvich(ref _world, slot))
            ServerMessages.Omnomnomnom(_broadcast, (byte)slot);
    }

    private void ExecuteToggleZoom(int slot)
    {
        // serverToggleZoom.gml: the preconditions, the broadcast, then
        // the apply. ⚠️ Zoom is its own client command rather than a key
        // in the input byte, which is the whole reason the reference
        // extracted it into a script a bot can call.
        if (PlayerCommands.ToggleZoom(ref _world, slot))
            ServerMessages.ToggleZoom(_broadcast, (byte)slot);
    }

    private byte IdOf(RosterEntry entry) => checked((byte)_roster.IndexOf(entry));

    private void RemoveClient(int clientIndex, string reason)
    {
        var client = _clients[clientIndex];
        var rosterIndex = _roster.IndexOf(client.Entry);

        // ⚠️ Read BEFORE the removal. GameServerBeginStep.gml:46-49 re-announces
        // only when the server WAS full, so that a list showing "10/10" stops
        // showing it the moment a slot opens — the case where a stale entry
        // actually costs somebody a game. Sampling after the remove would make
        // the test `occupied == limit` never true and the branch dead.
        var wasFull = OccupiedSlots() == _options.PlayerLimit;

        // ⚠️ Before the removal, or this connection's traffic vanishes from the
        // running total. See _bytesRetired.
        _bytesRetired += client.Connection.BytesSent;

        // Every departure, whatever caused it — a clean quit, a lost
        // connection, a kick, a ban. This is the ONLY place a session is
        // closed, which is what makes "playtime" the time the socket was open
        // rather than the time somebody chose to leave.
        _analytics.End(client.Analytics, DateTime.UtcNow);
        client.Analytics = null;

        _clients.RemoveAt(clientIndex);

        if (rosterIndex > 0)
        {
            // ⛔ BEFORE the removal — see RemoveBot.
            _plugins.PlayerRemoving(rosterIndex);

            _roster.RemoveAt(rosterIndex);

            // Both sides compact the list, so every index above this one shifts
            // down by one. The client does it in its PLAYER_LEAVE case, and the
            // world has to shift with it — a slot index IS a roster index, so a
            // character left in place would belong to the wrong player from here
            // on.
            _world.RemoveSlot(rosterIndex);

            ServerMessages.PlayerLeave(_broadcast, (byte)rosterIndex);

            // GameServerBeginStep.gml:70: ServerBalanceTeams() immediately after
            // the PLAYER_LEAVE, because a departure is the commonest way for the
            // teams to come apart — and, since the leaver is gone, the commonest
            // way for them to come back together.
            TeamBalance.Check(ref _world, ref _events);
            BroadcastTeamBalance();
        }

        if (_churnLog.ShouldLog(_nowMs, out var suppressed))
        {
            _log.Info(
                $"{LogText.Safe(client.Entry.Name)} left ({LogText.Safe(reason)}); "
                + $"{_roster.Count - 1} of {_options.PlayerLimit} slots used"
                + Suffix(suppressed));
        }
        client.Connection.Dispose();

        if (wasFull)
            AnnounceToLobby();
    }
}
