namespace GG2.Protocol;

/// <summary>
/// Writers for the fixed-shape server-to-client messages.
/// </summary>
/// <remarks>
/// One method per message, each transcribed from the pair that defines it: the
/// server-side <c>write_*</c> calls (mostly <c>Scripts/Message Builders/</c>)
/// and the matching <c>case</c> in <c>Scripts/Client/ClientBeginStep.gml</c>.
/// That pair is authoritative — do not infer a layout from a message's name.
/// <para>
/// The state updates are not here. They need world state, so they live with
/// whatever owns it.
/// </para>
/// </remarks>
public static class ServerMessages
{
    /// <summary>
    /// The server's greeting, sent once the client is past the protocol check
    /// and any password (<c>serviceJoiningPlayer.gml</c>,
    /// <c>STATE_CLIENT_AUTHENTICATED</c>).
    /// </summary>
    /// <remarks>
    /// An <b>empty</b> <paramref name="mapMd5"/> means the map is built into the
    /// client and cannot be downloaded, which is what removes the whole
    /// map-download path from a built-in-maps-only server's scope.
    /// </remarks>
    public static void Hello(
        NetWriter writer,
        string serverName,
        string currentMap,
        string mapMd5,
        bool pluginsRequired,
        string pluginList)
    {
        writer.WriteMessageType(MessageType.Hello);
        writer.WriteString1(serverName);
        writer.WriteString1(currentMap);
        writer.WriteString1(mapMd5);
        writer.WriteUByte(pluginsRequired ? (byte)1 : (byte)0);
        writer.WriteString2(pluginList);
    }

    /// <summary>
    /// Sent on the joining socket alone, immediately before the
    /// <c>FULL_UPDATE</c> that completes a join
    /// (<c>Scripts/Message Builders/ServerJoinUpdate.gml</c>).
    /// </summary>
    public static void JoinUpdate(NetWriter writer, byte playerId, byte currentMapArea)
    {
        writer.WriteMessageType(MessageType.JoinUpdate);
        writer.WriteUByte(playerId);
        writer.WriteUByte(currentMapArea);
    }

    /// <summary>
    /// Broadcast to everyone <b>including the joiner</b>. Carries only a name:
    /// the new player's roster index is implicitly "one past the end".
    /// </summary>
    public static void PlayerJoin(NetWriter writer, string name)
    {
        writer.WriteMessageType(MessageType.PlayerJoin);
        writer.WriteString1(name);
    }

    /// <summary>
    /// Removes a roster entry. Both sides then <b>compact the list</b>, so every
    /// index above <paramref name="playerId"/> shifts down by one — see
    /// docs/PROTOCOL.md, "Roster indexing".
    /// </summary>
    public static void PlayerLeave(NetWriter writer, byte playerId)
    {
        writer.WriteMessageType(MessageType.PlayerLeave);
        writer.WriteUByte(playerId);
    }

    public static void PlayerChangeTeam(NetWriter writer, byte playerId, Team team)
    {
        writer.WriteMessageType(MessageType.PlayerChangeTeam);
        writer.WriteUByte(playerId);
        writer.WriteUByte((byte)team);
    }

    public static void PlayerChangeClass(NetWriter writer, byte playerId, PlayerClass playerClass)
    {
        writer.WriteMessageType(MessageType.PlayerChangeClass);
        writer.WriteUByte(playerId);
        writer.WriteUByte((byte)playerClass);
    }

    /// <summary>
    /// <c>PLAYER_SPAWN</c>: which spawn point, not where.
    /// </summary>
    /// <remarks>
    /// <c>sendEventSpawn.gml</c> sends the point id and the group and lets every
    /// client resolve them against its own copy of the map
    /// (<c>doEventSpawn.gml</c>). That is why a spawn is the one moment a
    /// character's position crosses the wire exactly rather than quantised to a
    /// fifth of a pixel — see docs/PARITY.md.
    /// </remarks>
    public static void PlayerSpawn(NetWriter writer, byte playerId, byte spawnPointId, byte group)
    {
        writer.WriteMessageType(MessageType.PlayerSpawn);
        writer.WriteUByte(playerId);
        writer.WriteUByte(spawnPointId);
        writer.WriteUByte(group);
    }

    /// <summary>
    /// <c>WEAPON_FIRE</c>: somebody pulled the trigger, and here is the seed.
    /// </summary>
    /// <remarks>
    /// <b>The one message whose payload is an instruction to run a random
    /// generator.</b> The server does not send where the pellets went; it sends
    /// the seed it drew, and every client runs <c>random_set_seed</c> and
    /// derives the spread itself (<c>Scripts/Events/doEventFireWeapon.gml</c>).
    /// So the position and speeds here are not decoration either — a client
    /// snaps the shooter to them before firing, so that its pellets start where
    /// the server's did.
    /// <para>
    /// Sent from <c>sendEventFireWeapon.gml</c>, which the weapon's
    /// <c>User Event 1</c> calls in the <em>character's</em> Begin Step — after
    /// the tick's state update, which the <c>GameServer</c> instance wrote in
    /// its own. That ordering is what puts a fire message after the update of
    /// the tick it happened on, and docs/PARITY.md's replayer relies on it.
    /// </para>
    /// <para>
    /// Note the speed scale: <c>8.5</c>, the character's, not the <c>5</c> a
    /// mine's block uses.
    /// </para>
    /// </remarks>
    public static void WeaponFire(
        NetWriter writer,
        byte playerId,
        double x,
        double y,
        double hspeed,
        double vspeed,
        ushort seed)
    {
        writer.WriteMessageType(MessageType.WeaponFire);
        writer.WriteUByte(playerId);
        writer.WriteUShortFromReal(x * 5);
        writer.WriteUShortFromReal(y * 5);
        writer.WriteByteFromReal(hspeed * 8.5);
        writer.WriteByteFromReal(vspeed * 8.5);
        writer.WriteUShort(seed);
    }

    /// <summary>
    /// <c>PLAYER_DEATH</c>: somebody's character died, and who gets credit.
    /// </summary>
    /// <remarks>
    /// <c>Scripts/Events/sendEventPlayerDeath.gml</c>, four bytes: victim,
    /// killer, assistant, cause.
    /// <para>
    /// <b><paramref name="killer"/> and <paramref name="assistant"/> are 255 for
    /// nobody, never 0.</b> The reference writes 255 where
    /// <c>instance_exists</c> is false, and roster slot 0 is the server's own
    /// reserved player and always exists — so a defaulted zero would credit it
    /// with every unattributed kill. <see cref="GG2.Simulation"/> spells the
    /// same sentinel <c>CharacterState.Nobody</c>.
    /// </para>
    /// <para>
    /// Sent from <c>Character.events/End Step.xml:34-47</c>, which is
    /// <em>after</em> the tick's state update (the <c>GameServer</c> instance
    /// writes that in its own Begin Step) and after any <c>WEAPON_FIRE</c> the
    /// same tick produced in a character's Begin Step. So one tick's stream is
    /// update, then fires, then deaths — and docs/PARITY.md's replayer reads a
    /// death out of the <em>following</em> tick's message block, exactly as it
    /// does a fire seed.
    /// </para>
    /// </remarks>
    public static void PlayerDeath(
        NetWriter writer,
        byte victim,
        byte killer,
        byte assistant,
        DamageSource cause)
    {
        writer.WriteMessageType(MessageType.PlayerDeath);
        writer.WriteUByte(victim);
        writer.WriteUByte(killer);
        writer.WriteUByte(assistant);
        writer.WriteUByte((byte)cause);
    }

    /// <summary>
    /// <c>Scripts/Message Builders/sendEventUberReady.gml</c>: a medic's charge
    /// is full.
    /// </summary>
    /// <remarks>
    /// One byte of body. The <em>amount</em> of charge is already in every state
    /// update (a medic's class-specific byte is <c>uberCharge*255/2000</c>), so
    /// this message exists for the sound and the chat bubble the client plays —
    /// but it is also the only unambiguous statement of <em>which tick</em> the
    /// meter filled on, which is what makes it worth comparing in a replay.
    /// </remarks>
    public static void UberCharged(NetWriter writer, byte playerId)
    {
        writer.WriteMessageType(MessageType.UberCharged);
        writer.WriteUByte(playerId);
    }

    /// <summary>
    /// <c>Scripts/Message Builders/sendEventUber.gml</c>: a medic has started
    /// spending it.
    /// </summary>
    public static void Uber(NetWriter writer, byte playerId)
    {
        writer.WriteMessageType(MessageType.Uber);
        writer.WriteUByte(playerId);
    }

    /// <summary>
    /// <c>Scripts/Message Builders/sendEventGrabIntel.gml</c>: this player has
    /// picked the enemy briefcase up.
    /// </summary>
    /// <remarks>
    /// <b>One byte of body, and it carries no position.</b> Every client runs
    /// <c>doEventGrabIntel</c> itself against the flag it already has, so the
    /// message only has to name who — which also means a client that disagrees
    /// about where the flag was gets no correction until the next
    /// <c>FULL_UPDATE</c>.
    /// </remarks>
    public static void GrabIntel(NetWriter writer, byte playerId)
    {
        writer.WriteMessageType(MessageType.GrabIntel);
        writer.WriteUByte(playerId);
    }

    /// <summary>
    /// <c>Scripts/Message Builders/sendEventDropIntel.gml</c>: this player has
    /// put it down.
    /// </summary>
    /// <remarks>
    /// <b>Never sent for a carrier who died.</b> The script's own comment:
    /// the drop would be a nested event inside the death and the client would
    /// error out dropping a flag for a character it has already destroyed. A
    /// client infers that drop from the <c>PLAYER_DEATH</c> instead.
    /// </remarks>
    public static void DropIntel(NetWriter writer, byte playerId)
    {
        writer.WriteMessageType(MessageType.DropIntel);
        writer.WriteUByte(playerId);
    }

    /// <summary>
    /// <c>Scripts/Message Builders/sendEventScoreIntel.gml</c>: this player has
    /// captured.
    /// </summary>
    public static void ScoreIntel(NetWriter writer, byte playerId)
    {
        writer.WriteMessageType(MessageType.ScoreIntel);
        writer.WriteUByte(playerId);
    }

    /// <summary>
    /// <c>Scripts/Message Builders/sendEventReturnIntel.gml</c>: a dropped
    /// briefcase has gone home on its own.
    /// </summary>
    /// <remarks>
    /// The body is a <em>team</em>, not a player — nobody returns a flag in GG2,
    /// its own <c>alarm[0]</c> does. The one message in the objective that names
    /// no roster index.
    /// </remarks>
    public static void ReturnIntel(NetWriter writer, Team team)
    {
        writer.WriteMessageType(MessageType.ReturnIntel);
        writer.WriteUByte((byte)team);
    }

    /// <summary>
    /// <c>Scripts/Message Builders/sendEventGeneratorDestroy.gml</c>: a
    /// generator has been destroyed.
    /// </summary>
    /// <remarks>
    /// <b>The body is the team that <em>lost</em> its generator</b>, and the
    /// script's own docstring says "the team who destroyed the bomb". Its one
    /// caller — <c>Generator.events/Begin Step.xml:19</c> — passes the
    /// generator's own <c>team</c>, and <c>doEventGeneratorDestroy</c> awards
    /// the win to the other one. The comment is the wrong half.
    /// <para>
    /// Naming a team rather than a player, like <see cref="ReturnIntel"/>:
    /// nothing in the reference records who fired the last shot at a generator.
    /// </para>
    /// </remarks>
    public static void GeneratorDestroy(NetWriter writer, Team team)
    {
        writer.WriteMessageType(MessageType.GeneratorDestroy);
        writer.WriteUByte((byte)team);
    }

    /// <summary>
    /// <c>TOGGLE_ZOOM</c>: a sniper raised or lowered the scope.
    /// </summary>
    /// <remarks>
    /// One byte of body, and <b>it says nothing about which way</b> — every
    /// client flips that player's own `zoomed` (`ClientBeginStep.gml:404-410`),
    /// so both sides must already agree. A client that missed one is inverted
    /// until the next one, and nothing in a state update corrects it: `zoomed`
    /// is never serialised. See `GG2.Simulation.CharacterState.Zoomed`.
    /// </remarks>
    public static void ToggleZoom(NetWriter writer, byte playerId)
    {
        writer.WriteMessageType(MessageType.ToggleZoom);
        writer.WriteUByte(playerId);
    }

    /// <summary>
    /// <c>OMNOMNOMNOM</c>: this heavy has started eating.
    /// </summary>
    /// <remarks>
    /// One byte of body, the player id, and <b>the client does more with it
    /// than start an animation</b>: <c>ClientBeginStep.gml:386-400</c> also puts
    /// the sandvich on cooldown itself, <c>if(hp &lt; 200)</c> — a hard 200
    /// rather than the character's <c>maxHp</c>, which happens to be the heavy's
    /// and is the only class that can receive this. The server re-arms the same
    /// cooldown every tick of the eat, so the two agree without the server ever
    /// sending the timer.
    /// </remarks>
    public static void Omnomnomnom(NetWriter writer, byte playerId)
    {
        writer.WriteMessageType(MessageType.Omnomnomnom);
        writer.WriteUByte(playerId);
    }

    /// <summary>
    /// <c>BALANCE</c>: the teams are uneven and everybody should know.
    /// </summary>
    /// <remarks>
    /// <b>One body byte, not two.</b> The notice is
    /// <c>BALANCE, 255</c> and stops there; a real switch
    /// (<see cref="BalanceSwitch"/>) adds the class. A reader that frames this
    /// at a fixed width loses the stream — see <see cref="ServerStream"/>.
    /// </remarks>
    public static void BalanceNotice(NetWriter writer)
    {
        writer.WriteMessageType(MessageType.Balance);
        writer.WriteUByte(ServerStream.BalanceNotice);
    }

    /// <summary>
    /// <c>BALANCE</c>: this player has been moved to the other team.
    /// </summary>
    /// <remarks>
    /// <b>The message does not say which team.</b> The client flips the
    /// player's own <c>team</c> field (<c>ClientBeginStep.gml:260-264</c>), so
    /// the server and the client must already agree on where they were — and
    /// the class byte is sent because <c>checkClasslimits</c> may have changed
    /// it on the way across.
    /// <para>
    /// It also destroys the player's character on both sides, and no
    /// <c>PLAYER_DEATH</c> goes out for it.
    /// </para>
    /// </remarks>
    public static void BalanceSwitch(NetWriter writer, byte playerId, PlayerClass playerClass)
    {
        writer.WriteMessageType(MessageType.Balance);
        writer.WriteUByte(playerId);
        writer.WriteUByte((byte)playerClass);
    }

    /// <summary>
    /// <c>ARENA_RESTART</c>: the next round's setup period has begun.
    /// </summary>
    /// <remarks>
    /// Bodyless. A client runs the same <c>doEventArenaRestart</c> the server
    /// does, so the message is the whole instruction — its own countdowns never
    /// drive a transition.
    /// </remarks>
    public static void ArenaRestart(NetWriter writer) =>
        writer.WriteMessageType(MessageType.ArenaRestart);

    /// <summary><c>ARENA_STARTROUND</c>: the round proper has begun. Bodyless.</summary>
    public static void ArenaStartRound(NetWriter writer) =>
        writer.WriteMessageType(MessageType.ArenaStartRound);

    /// <summary>
    /// <c>ARENA_WAITFORPLAYERS</c>: a team has emptied out. Bodyless.
    /// </summary>
    public static void ArenaWaitForPlayers(NetWriter writer) =>
        writer.WriteMessageType(MessageType.ArenaWaitForPlayers);

    /// <summary>
    /// <c>ARENA_ENDROUND</c>: somebody has won the round.
    /// </summary>
    /// <remarks>
    /// <b>The one Arena message with a body, and it is variable-length.</b>
    /// <c>serverArenaEndRound.gml</c> writes the winner, the two MVP counts and
    /// both round scores, then five bytes for each MVP — up to three a side. The
    /// client reads exactly that (<c>clientArenaEndRound.gml</c>), so the counts
    /// have to match the records that follow or the stream is lost from here on.
    /// <para>
    /// <b>The two counts are written before either list</b>, so a caller
    /// cannot stream the records and count as it goes — both lists have to be
    /// decided first. That is what makes a wrong count a lost stream rather than
    /// a wrong number.
    /// </para>
    /// </remarks>
    public static void ArenaEndRound(
        NetWriter writer,
        Team winner,
        byte redWins,
        byte blueWins,
        ReadOnlySpan<ArenaMvp> redMvps = default,
        ReadOnlySpan<ArenaMvp> blueMvps = default)
    {
        writer.WriteMessageType(MessageType.ArenaEndRound);
        writer.WriteUByte((byte)winner);
        writer.WriteUByte(checked((byte)redMvps.Length));
        writer.WriteUByte(checked((byte)blueMvps.Length));
        writer.WriteUByte(redWins);
        writer.WriteUByte(blueWins);

        // Red's whole list, then blue's — two loops in the reference, not one
        // interleaved walk.
        foreach (var mvp in redMvps)
            WriteMvp(writer, mvp);

        foreach (var mvp in blueMvps)
            WriteMvp(writer, mvp);
    }

    private static void WriteMvp(NetWriter writer, in ArenaMvp mvp)
    {
        writer.WriteUByte(mvp.PlayerId);
        writer.WriteUByte(mvp.Kills);
        writer.WriteUShort(mvp.Healing);
        writer.WriteUByte(mvp.Points);
    }

    /// <summary>
    /// <c>Scripts/Message Builders/sendEventUnlockCP.gml</c>: the pregame lock
    /// has come off every control point.
    /// </summary>
    /// <remarks>
    /// <b>A body of nothing at all</b> — one message byte. The client's
    /// <c>doEventUnlockCP</c> is <c>with ControlPoint locked = 0</c>, and a
    /// client that misses the message recovers anyway: the KotH HUD unlocks the
    /// point itself when a <c>CAPS_UPDATE</c> brings it a <c>cpUnlock</c> of
    /// zero.
    /// </remarks>
    public static void UnlockControlPoints(NetWriter writer) =>
        writer.WriteMessageType(MessageType.UnlockCp);

    /// <summary>
    /// <c>Scripts/GameServer/serverBuildSentry.gml</c>'s broadcast: an accepted
    /// build, echoed to every client with the exact placement.
    /// </summary>
    /// <remarks>
    /// The reference writes the first two bytes to <c>global.sendBuffer</c> and
    /// the last five to <c>global.serializeBuffer</c> — which
    /// <c>serializeState</c> has already aliased to the same buffer, so the
    /// seven bytes are contiguous on the wire and this writes them plainly. The
    /// position is <c>round(x*5)</c>, GML's half-to-even.
    /// </remarks>
    public static void BuildSentry(NetWriter writer, byte playerId, ushort x5, ushort y5, sbyte direction)
    {
        writer.WriteMessageType(MessageType.BuildSentry);
        writer.WriteUByte(playerId);
        writer.WriteUShort(x5);
        writer.WriteUShort(y5);
        writer.WriteByte(direction);
    }

    /// <summary>
    /// <c>Sentry.events/Begin Step.xml</c>'s landing broadcast: the sentry has
    /// stopped falling, and this is exactly where.
    /// </summary>
    public static void SentryPosition(NetWriter writer, byte playerId, ushort x5, ushort y5)
    {
        writer.WriteMessageType(MessageType.SentryPosition);
        writer.WriteUByte(playerId);
        writer.WriteUShort(x5);
        writer.WriteUShort(y5);
    }

    /// <summary>
    /// <c>Scripts/Message Builders/sendEventDestruction.gml</c>: a sentry died,
    /// and who gets the credit.
    /// </summary>
    /// <remarks>
    /// The same slot-or-255 convention as <see cref="PlayerDeath"/>. An owner
    /// detonating their own sentry sends nothing at all —
    /// <c>processClientCommands.gml</c>'s <c>DESTROY_SENTRY</c> case is a bare
    /// <c>instance_destroy</c> — so this goes out only for the simulated
    /// destructions.
    /// </remarks>
    public static void DestroySentry(NetWriter writer, byte playerId, byte killer, byte assistant, DamageSource cause)
    {
        writer.WriteMessageType(MessageType.DestroySentry);
        writer.WriteUByte(playerId);
        writer.WriteUByte(killer);
        writer.WriteUByte(assistant);
        writer.WriteUByte((byte)cause);
    }

    public static void PlayerChangeName(NetWriter writer, byte playerId, string name)
    {
        writer.WriteMessageType(MessageType.PlayerChangeName);
        writer.WriteUByte(playerId);
        writer.WriteString1(name);
    }

    public static void ChatBubble(NetWriter writer, byte playerId, byte image)
    {
        writer.WriteMessageType(MessageType.ChatBubble);
        writer.WriteUByte(playerId);
        writer.WriteUByte(image);
    }

    /// <summary>
    /// The 16-byte nonce a client signs to prove who it is.
    /// </summary>
    /// <remarks>
    /// <c>processClientCommands.gml:320-325</c>. <b>Raw bytes with no length
    /// prefix</b> — <c>write_binstring</c>, and the client reads exactly 16
    /// (<c>ClientBeginStep.gml:553-556</c>). Prefixing it would desynchronise
    /// that socket's stream permanently rather than fail visibly.
    /// <para>
    /// <b>To the one socket, never the broadcast buffer.</b> A challenge is
    /// addressed to one client, and putting it on the shared stream would both
    /// leak it and hand every other client a message it cannot parse.
    /// </para>
    /// </remarks>
    public static void RewardChallengeCode(NetWriter writer, ReadOnlySpan<byte> challenge)
    {
        writer.WriteMessageType(MessageType.RewardChallengeCode);
        writer.WriteBytes(challenge);
    }

    /// <summary>
    /// Tells everybody which rewards a player has authenticated for.
    /// </summary>
    /// <remarks>
    /// <c>sendEventUpdateRewards.gml</c>: the player id, then a
    /// <c>ushort</c>-prefixed string of <c>:</c>-separated reward names. Clients
    /// parse it into a set and a badge list and draw from those; the server
    /// keeps nothing.
    /// </remarks>
    public static void RewardUpdate(NetWriter writer, byte playerId, string rewards)
    {
        writer.WriteMessageType(MessageType.RewardUpdate);
        writer.WriteUByte(playerId);
        writer.WriteString2(rewards);
    }

    /// <summary>
    /// Echoes one player's <c>CLIENT_SETTINGS</c> to everybody.
    /// </summary>
    /// <remarks>
    /// <c>processClientCommands.gml:364-371</c>. <b>This has to be a
    /// broadcast rather than an acknowledgement to the one socket</b>: queued
    /// jumping changes when that character jumps, and every client re-simulates
    /// every character between position updates. A client that never hears
    /// about it dead-reckons the wrong jump on every landing until the next
    /// <c>FULL_UPDATE</c> — which only a joiner ever gets.
    /// </remarks>
    public static void ClientSettings(NetWriter writer, byte playerId, bool queueJump)
    {
        writer.WriteMessageType(MessageType.ClientSettings);
        writer.WriteUByte(playerId);
        writer.WriteUByte(queueJump ? (byte)1 : (byte)0);
    }

    /// <summary>A server notice shown in the client's chat area.</summary>
    public static void MessageString(NetWriter writer, string text)
    {
        writer.WriteMessageType(MessageType.MessageString);
        writer.WriteString1(text);
    }

    public static void ChangeMap(NetWriter writer, string map, string md5)
    {
        writer.WriteMessageType(MessageType.ChangeMap);
        writer.WriteString1(map);
        writer.WriteString1(md5);
    }

    public static void MapEnd(NetWriter writer, string nextMap, Team winners, byte currentMapArea)
    {
        writer.WriteMessageType(MessageType.MapEnd);
        writer.WriteString1(nextMap);
        writer.WriteUByte((byte)winners);
        writer.WriteUByte(currentMapArea);
    }

    /// <summary>
    /// A server-sent plugin's packet: the container
    /// <c>Scripts/Plugins/API/PluginPacketSend.gml:33-45</c> builds, and the one
    /// <c>PluginPacketSendTo</c> builds identically.
    /// </summary>
    /// <remarks>
    /// <b>The length covers the id byte as well as the data</b> —
    /// <c>write_ushort(buffer_size(dataBuffer) + 1)</c> — and the client reads
    /// exactly that many bytes before parsing anything else
    /// (<c>ClientBeginStep.gml:614-626</c>). One off here desynchronises the
    /// whole stream from that point on, and the client's own report of it is
    /// "The Server sent unexpected data" some messages later.
    /// <para>
    /// Same shape in both directions: the client-to-server command table gives
    /// <c>PLUGIN_PACKET</c> a two-byte length prefix for exactly this field.
    /// </para>
    /// </remarks>
    public static void PluginPacket(NetWriter writer, byte packetId, ReadOnlySpan<byte> data)
    {
        // The reference's own ceiling, from the same ushort:
        // PluginPacketSend.gml:21-22 refuses anything larger rather than
        // truncating it.
        if (data.Length > 65534)
            throw new ProtocolException(
                $"plugin packet of {data.Length} bytes does not fit a 2-byte length prefix");

        writer.WriteMessageType(MessageType.PluginPacket);
        writer.WriteUShort((ushort)(data.Length + 1));
        writer.WriteUByte(packetId);
        writer.WriteBytes(data);
    }

    public static void Kick(NetWriter writer, KickReason reason)
    {
        writer.WriteMessageType(MessageType.Kick);
        writer.WriteUByte((byte)reason);
    }

    /// <summary>Sole reply to a client whose <c>PROTOCOL_UUID</c> did not match.</summary>
    /// <remarks>
    /// The reference writes this and then drops the connection without setting a
    /// next state (<c>serviceJoiningPlayer.gml</c>, <c>STATE_EXPECT_HELLO</c>).
    /// </remarks>
    public static void IncompatibleProtocol(NetWriter writer) =>
        writer.WriteMessageType(MessageType.IncompatibleProtocol);

    public static void PasswordRequest(NetWriter writer) =>
        writer.WriteMessageType(MessageType.PasswordRequest);

    public static void PasswordWrong(NetWriter writer) =>
        writer.WriteMessageType(MessageType.PasswordWrong);

    public static void ServerFull(NetWriter writer) =>
        writer.WriteMessageType(MessageType.ServerFull);

    public static void ReserveSlot(NetWriter writer) =>
        writer.WriteMessageType(MessageType.ReserveSlot);

    /// <summary>The version of the prediction messages this server speaks.</summary>
    /// <remarks>A later version is a different hello byte, not a new value here. docs/PREDICTION.md.</remarks>
    public const byte PredictVersion = 1;

    /// <summary>
    /// <c>PREDICT_WELCOME</c>: the one reply to a client's <c>PREDICT_HELLO</c>.
    /// </summary>
    /// <param name="inputsRead">
    /// <c>INPUTSTATE</c>s this connection had sent when the hello was read. The
    /// client checks it against its own count and does not predict on a mismatch.
    /// </param>
    public static void PredictWelcome(NetWriter writer, uint inputsRead)
    {
        writer.WriteMessageType(MessageType.Predict);
        writer.WriteUByte(PredictVersion);
        writer.WriteUInt(inputsRead);
    }

    /// <summary>
    /// <c>PREDICT_ACK</c>: this connection's <c>INPUTSTATE</c>s consumed into
    /// ticks so far, for the next state update in the stream.
    /// </summary>
    public static void PredictAck(NetWriter writer, uint inputsApplied)
    {
        writer.WriteMessageType(MessageType.PredictAck);
        writer.WriteUInt(inputsApplied);
    }
}
