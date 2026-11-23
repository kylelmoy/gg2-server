using GG2.Protocol;

namespace GG2.Server.Wire;

/// <summary>
/// The tick's broadcast: one byte stream per <see cref="WireProtocol"/>, each
/// written once and sent to every client that speaks it.
/// </summary>
/// <remarks>
/// <para>
/// Every message method here writes the same news into each stream through that
/// protocol's <see cref="ServerEncoder"/>, so the two streams carry the same
/// events in the same order and differ only in how they are encoded. The fan-out
/// is still one serialisation per protocol and N sends, not N serialisations.
/// </para>
/// <para>
/// <b>The legacy stream is always written</b>, client or no client: it is what
/// <c>MatchRecorder</c> tees, and the recorder's traces are legacy-format by
/// definition (<c>TraceParser</c>). <b>The native stream is written only while
/// a native client is listening</b> (<see cref="Listen"/>), so a server with no
/// native client pays nothing for the second protocol.
/// </para>
/// <para>
/// ⚠️ <b>A join mark is an offset into ONE of these streams</b> — the joiner's
/// own (<see cref="Length"/>). It is safe for a native joiner to take a mark on
/// a stream that was not being written: the stream is empty, the mark is 0, and
/// the mark's meaning ("skip what was standing before the burst") is satisfied
/// because nothing was.
/// </para>
/// </remarks>
public sealed class ServerBroadcast
{
    private readonly NetWriter _legacy = new(8192);
    private readonly NetWriter _native = new(8192);
    private readonly ServerEncoder _legacyEncoder = ServerEncoder.Legacy;
    private readonly ServerEncoder _nativeEncoder = ServerEncoder.Native;
    private int _nativeListeners;

    /// <summary>Whether neither stream holds anything.</summary>
    public bool IsEmpty => _legacy.IsEmpty && _native.IsEmpty;

    /// <summary>Whether the native stream is being written.</summary>
    public bool NativeActive => _nativeListeners > 0;

    /// <summary>Native clients listening. Diagnostics and tests.</summary>
    public int NativeListeners => _nativeListeners;

    /// <summary>Counts a client that now follows <paramref name="protocol"/>'s stream.</summary>
    public void Listen(WireProtocol protocol)
    {
        if (protocol == WireProtocol.Native)
            _nativeListeners++;
    }

    /// <summary>Uncounts a client that has left.</summary>
    public void Unlisten(WireProtocol protocol)
    {
        if (protocol == WireProtocol.Native && _nativeListeners > 0)
            _nativeListeners--;
    }

    /// <summary>Forgets every listener, for a server that has dropped all its clients.</summary>
    public void ClearListeners() => _nativeListeners = 0;

    /// <summary>The bytes standing in <paramref name="protocol"/>'s stream.</summary>
    public ReadOnlySpan<byte> Written(WireProtocol protocol) =>
        protocol == WireProtocol.Native ? _native.WrittenSpan : _legacy.WrittenSpan;

    /// <summary>The length of <paramref name="protocol"/>'s stream: a join mark.</summary>
    public int Length(WireProtocol protocol) => Written(protocol).Length;

    /// <summary>The legacy stream, which is what the recorder keeps.</summary>
    public ReadOnlySpan<byte> LegacyWritten => _legacy.WrittenSpan;

    public void Reset()
    {
        _legacy.Reset();
        _native.Reset();
    }

    // --- state updates ---------------------------------------------------------

    public void StateUpdate(StateUpdateWriter state, MessageType tier)
    {
        _legacyEncoder.StateUpdate(_legacy, state, tier);
        if (NativeActive)
            _nativeEncoder.StateUpdate(_native, state, tier);
    }

    // --- the roster ------------------------------------------------------------

    public void PlayerJoin(string name)
    {
        _legacyEncoder.PlayerJoin(_legacy, name);
        if (NativeActive)
            _nativeEncoder.PlayerJoin(_native, name);
    }

    public void PlayerLeave(byte playerId)
    {
        _legacyEncoder.PlayerLeave(_legacy, playerId);
        if (NativeActive)
            _nativeEncoder.PlayerLeave(_native, playerId);
    }

    public void PlayerChangeTeam(byte playerId, Team team)
    {
        _legacyEncoder.PlayerChangeTeam(_legacy, playerId, team);
        if (NativeActive)
            _nativeEncoder.PlayerChangeTeam(_native, playerId, team);
    }

    public void PlayerChangeClass(byte playerId, PlayerClass playerClass)
    {
        _legacyEncoder.PlayerChangeClass(_legacy, playerId, playerClass);
        if (NativeActive)
            _nativeEncoder.PlayerChangeClass(_native, playerId, playerClass);
    }

    public void PlayerChangeName(byte playerId, string name)
    {
        _legacyEncoder.PlayerChangeName(_legacy, playerId, name);
        if (NativeActive)
            _nativeEncoder.PlayerChangeName(_native, playerId, name);
    }

    public void ClientSettings(byte playerId, bool queueJump)
    {
        _legacyEncoder.ClientSettings(_legacy, playerId, queueJump);
        if (NativeActive)
            _nativeEncoder.ClientSettings(_native, playerId, queueJump);
    }

    public void BalanceNotice()
    {
        _legacyEncoder.BalanceNotice(_legacy);
        if (NativeActive)
            _nativeEncoder.BalanceNotice(_native);
    }

    public void BalanceSwitch(byte playerId, PlayerClass playerClass)
    {
        _legacyEncoder.BalanceSwitch(_legacy, playerId, playerClass);
        if (NativeActive)
            _nativeEncoder.BalanceSwitch(_native, playerId, playerClass);
    }

    // --- characters ------------------------------------------------------------

    public void PlayerSpawn(byte playerId, byte spawnPointId, byte group)
    {
        _legacyEncoder.PlayerSpawn(_legacy, playerId, spawnPointId, group);
        if (NativeActive)
            _nativeEncoder.PlayerSpawn(_native, playerId, spawnPointId, group);
    }

    public void PlayerDeath(byte victim, byte killer, byte assistant, DamageSource cause)
    {
        _legacyEncoder.PlayerDeath(_legacy, victim, killer, assistant, cause);
        if (NativeActive)
            _nativeEncoder.PlayerDeath(_native, victim, killer, assistant, cause);
    }

    public void WeaponFire(byte playerId, double x, double y, double hspeed, double vspeed, ushort seed)
    {
        _legacyEncoder.WeaponFire(_legacy, playerId, x, y, hspeed, vspeed, seed);
        if (NativeActive)
            _nativeEncoder.WeaponFire(_native, playerId, x, y, hspeed, vspeed, seed);
    }

    public void UberCharged(byte playerId)
    {
        _legacyEncoder.UberCharged(_legacy, playerId);
        if (NativeActive)
            _nativeEncoder.UberCharged(_native, playerId);
    }

    public void Uber(byte playerId)
    {
        _legacyEncoder.Uber(_legacy, playerId);
        if (NativeActive)
            _nativeEncoder.Uber(_native, playerId);
    }

    public void ToggleZoom(byte playerId)
    {
        _legacyEncoder.ToggleZoom(_legacy, playerId);
        if (NativeActive)
            _nativeEncoder.ToggleZoom(_native, playerId);
    }

    public void Omnomnomnom(byte playerId)
    {
        _legacyEncoder.Omnomnomnom(_legacy, playerId);
        if (NativeActive)
            _nativeEncoder.Omnomnomnom(_native, playerId);
    }

    public void ChatBubble(byte playerId, byte image)
    {
        _legacyEncoder.ChatBubble(_legacy, playerId, image);
        if (NativeActive)
            _nativeEncoder.ChatBubble(_native, playerId, image);
    }

    // --- sentries --------------------------------------------------------------

    public void BuildSentry(byte playerId, ushort x5, ushort y5, sbyte direction)
    {
        _legacyEncoder.BuildSentry(_legacy, playerId, x5, y5, direction);
        if (NativeActive)
            _nativeEncoder.BuildSentry(_native, playerId, x5, y5, direction);
    }

    public void SentryPosition(byte playerId, ushort x5, ushort y5)
    {
        _legacyEncoder.SentryPosition(_legacy, playerId, x5, y5);
        if (NativeActive)
            _nativeEncoder.SentryPosition(_native, playerId, x5, y5);
    }

    public void DestroySentry(byte playerId, byte killer, byte assistant, DamageSource cause)
    {
        _legacyEncoder.DestroySentry(_legacy, playerId, killer, assistant, cause);
        if (NativeActive)
            _nativeEncoder.DestroySentry(_native, playerId, killer, assistant, cause);
    }

    // --- objectives ------------------------------------------------------------

    public void GrabIntel(byte playerId)
    {
        _legacyEncoder.GrabIntel(_legacy, playerId);
        if (NativeActive)
            _nativeEncoder.GrabIntel(_native, playerId);
    }

    public void DropIntel(byte playerId)
    {
        _legacyEncoder.DropIntel(_legacy, playerId);
        if (NativeActive)
            _nativeEncoder.DropIntel(_native, playerId);
    }

    public void ScoreIntel(byte playerId)
    {
        _legacyEncoder.ScoreIntel(_legacy, playerId);
        if (NativeActive)
            _nativeEncoder.ScoreIntel(_native, playerId);
    }

    public void ReturnIntel(Team team)
    {
        _legacyEncoder.ReturnIntel(_legacy, team);
        if (NativeActive)
            _nativeEncoder.ReturnIntel(_native, team);
    }

    public void GeneratorDestroy(Team team)
    {
        _legacyEncoder.GeneratorDestroy(_legacy, team);
        if (NativeActive)
            _nativeEncoder.GeneratorDestroy(_native, team);
    }

    public void UnlockControlPoints()
    {
        _legacyEncoder.UnlockControlPoints(_legacy);
        if (NativeActive)
            _nativeEncoder.UnlockControlPoints(_native);
    }

    public void ArenaRestart()
    {
        _legacyEncoder.ArenaRestart(_legacy);
        if (NativeActive)
            _nativeEncoder.ArenaRestart(_native);
    }

    public void ArenaStartRound()
    {
        _legacyEncoder.ArenaStartRound(_legacy);
        if (NativeActive)
            _nativeEncoder.ArenaStartRound(_native);
    }

    public void ArenaWaitForPlayers()
    {
        _legacyEncoder.ArenaWaitForPlayers(_legacy);
        if (NativeActive)
            _nativeEncoder.ArenaWaitForPlayers(_native);
    }

    public void ArenaEndRound(
        Team winner, byte redWins, byte blueWins, ReadOnlySpan<ArenaMvp> redMvps, ReadOnlySpan<ArenaMvp> blueMvps)
    {
        _legacyEncoder.ArenaEndRound(_legacy, winner, redWins, blueWins, redMvps, blueMvps);
        if (NativeActive)
            _nativeEncoder.ArenaEndRound(_native, winner, redWins, blueWins, redMvps, blueMvps);
    }

    // --- the round and the map -------------------------------------------------

    public void ChangeMap(string map, string md5)
    {
        _legacyEncoder.ChangeMap(_legacy, map, md5);
        if (NativeActive)
            _nativeEncoder.ChangeMap(_native, map, md5);
    }

    public void MapEnd(string nextMap, Team winners, byte currentMapArea)
    {
        _legacyEncoder.MapEnd(_legacy, nextMap, winners, currentMapArea);
        if (NativeActive)
            _nativeEncoder.MapEnd(_native, nextMap, winners, currentMapArea);
    }

    // --- text, rewards, plugins ------------------------------------------------

    public void MessageString(string text)
    {
        _legacyEncoder.MessageString(_legacy, text);
        if (NativeActive)
            _nativeEncoder.MessageString(_native, text);
    }

    public void RewardUpdate(byte playerId, string rewards)
    {
        _legacyEncoder.RewardUpdate(_legacy, playerId, rewards);
        if (NativeActive)
            _nativeEncoder.RewardUpdate(_native, playerId, rewards);
    }

    public void PluginPacket(byte packetId, ReadOnlySpan<byte> data)
    {
        _legacyEncoder.PluginPacket(_legacy, packetId, data);
        if (NativeActive)
            _nativeEncoder.PluginPacket(_native, packetId, data);
    }
}
