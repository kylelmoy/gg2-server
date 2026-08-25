namespace Gg2.Protocol;

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
}
