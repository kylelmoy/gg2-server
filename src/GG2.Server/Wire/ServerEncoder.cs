using GG2.Protocol;

namespace GG2.Server.Wire;

/// <summary>
/// Writes every server-to-client message, for one <see cref="WireProtocol"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every message the server sends after it has read a HELLO goes through one
/// of these</b>, chosen by the protocol that HELLO asked for: per client via
/// <see cref="For"/>, and for the broadcast via <see cref="ServerBroadcast"/>,
/// which runs one encoder per protocol that has a listener.
/// </para>
/// <para>
/// The base class is the legacy wire and every method is
/// <see cref="ServerMessages"/>, unchanged. <see cref="NativeServerEncoder"/>
/// overrides what the native protocol does differently. <b>To change a native
/// message, override it there</b>; to add a native-only message, add a virtual
/// here whose legacy body writes nothing (a stock client has no way to be told)
/// or whatever existing message carries the same news, and override it there.
/// Either way, bump <see cref="NativeProtocol.Version"/>.
/// </para>
/// <para>
/// ⛔ <b>The legacy bodies are the stock client's wire and are not this class's
/// to change.</b> They delegate to <see cref="ServerMessages"/>, which the
/// parity and framing tests pin; a native change made here instead of in the
/// override reaches every GM8 client.
/// </para>
/// <para>
/// ⚠️ <b>What is NOT in here, deliberately:</b> the replies that can go out
/// before a HELLO has been classified — <c>INCOMPATIBLE_PROTOCOL</c>, and the
/// <c>SERVER_FULL</c> or <c>KICK</c> refusing a connection at accept time.
/// Neither end knows the protocol yet, so those bytes are shared by both and
/// frozen with the legacy wire. A native client relies on reading them the
/// stock way.
/// </para>
/// </remarks>
public class ServerEncoder
{
    /// <summary>The stock GM8 client's encoder.</summary>
    public static ServerEncoder Legacy { get; } = new();

    /// <summary>The native client's encoder.</summary>
    public static ServerEncoder Native { get; } = new NativeServerEncoder();

    private protected ServerEncoder()
    {
    }

    public static ServerEncoder For(WireProtocol protocol) =>
        protocol == WireProtocol.Native ? Native : Legacy;

    public virtual WireProtocol Protocol => WireProtocol.Legacy;

    // --- the handshake, after the HELLO ---------------------------------------

    public virtual void PasswordRequest(NetWriter writer) => ServerMessages.PasswordRequest(writer);

    public virtual void PasswordWrong(NetWriter writer) => ServerMessages.PasswordWrong(writer);

    public virtual void ServerFull(NetWriter writer) => ServerMessages.ServerFull(writer);

    public virtual void ReserveSlot(NetWriter writer) => ServerMessages.ReserveSlot(writer);

    public virtual void Greeting(
        NetWriter writer,
        string serverName,
        string currentMap,
        string mapMd5,
        bool pluginsRequired,
        string pluginList) =>
        ServerMessages.Hello(writer, serverName, currentMap, mapMd5, pluginsRequired, pluginList);

    /// <summary>The <c>uint</c> length that precedes a map download's raw bytes.</summary>
    public virtual void MapDownloadLength(NetWriter writer, uint length) => writer.WriteUInt(length);

    // --- the join burst --------------------------------------------------------

    public virtual void JoinUpdate(NetWriter writer, byte playerId, byte currentMapArea) =>
        ServerMessages.JoinUpdate(writer, playerId, currentMapArea);

    // --- state updates ---------------------------------------------------------

    /// <summary>
    /// One state update of <paramref name="tier"/>: <c>INPUTSTATE</c>,
    /// <c>QUICK_UPDATE</c>, <c>CAPS_UPDATE</c> or <c>FULL_UPDATE</c>.
    /// </summary>
    public virtual void StateUpdate(NetWriter writer, StateUpdateWriter state, MessageType tier) =>
        state.Write(writer, tier);

    // --- the roster ------------------------------------------------------------

    public virtual void PlayerJoin(NetWriter writer, string name) => ServerMessages.PlayerJoin(writer, name);

    public virtual void PlayerLeave(NetWriter writer, byte playerId) => ServerMessages.PlayerLeave(writer, playerId);

    public virtual void PlayerChangeTeam(NetWriter writer, byte playerId, Team team) =>
        ServerMessages.PlayerChangeTeam(writer, playerId, team);

    public virtual void PlayerChangeClass(NetWriter writer, byte playerId, PlayerClass playerClass) =>
        ServerMessages.PlayerChangeClass(writer, playerId, playerClass);

    public virtual void PlayerChangeName(NetWriter writer, byte playerId, string name) =>
        ServerMessages.PlayerChangeName(writer, playerId, name);

    public virtual void ClientSettings(NetWriter writer, byte playerId, bool queueJump) =>
        ServerMessages.ClientSettings(writer, playerId, queueJump);

    public virtual void BalanceNotice(NetWriter writer) => ServerMessages.BalanceNotice(writer);

    public virtual void BalanceSwitch(NetWriter writer, byte playerId, PlayerClass playerClass) =>
        ServerMessages.BalanceSwitch(writer, playerId, playerClass);

    // --- characters ------------------------------------------------------------

    public virtual void PlayerSpawn(NetWriter writer, byte playerId, byte spawnPointId, byte group) =>
        ServerMessages.PlayerSpawn(writer, playerId, spawnPointId, group);

    public virtual void PlayerDeath(NetWriter writer, byte victim, byte killer, byte assistant, DamageSource cause) =>
        ServerMessages.PlayerDeath(writer, victim, killer, assistant, cause);

    public virtual void WeaponFire(
        NetWriter writer, byte playerId, double x, double y, double hspeed, double vspeed, ushort seed) =>
        ServerMessages.WeaponFire(writer, playerId, x, y, hspeed, vspeed, seed);

    public virtual void UberCharged(NetWriter writer, byte playerId) => ServerMessages.UberCharged(writer, playerId);

    public virtual void Uber(NetWriter writer, byte playerId) => ServerMessages.Uber(writer, playerId);

    public virtual void ToggleZoom(NetWriter writer, byte playerId) => ServerMessages.ToggleZoom(writer, playerId);

    public virtual void Omnomnomnom(NetWriter writer, byte playerId) => ServerMessages.Omnomnomnom(writer, playerId);

    public virtual void ChatBubble(NetWriter writer, byte playerId, byte image) =>
        ServerMessages.ChatBubble(writer, playerId, image);

    // --- sentries --------------------------------------------------------------

    public virtual void BuildSentry(NetWriter writer, byte playerId, ushort x5, ushort y5, sbyte direction) =>
        ServerMessages.BuildSentry(writer, playerId, x5, y5, direction);

    public virtual void SentryPosition(NetWriter writer, byte playerId, ushort x5, ushort y5) =>
        ServerMessages.SentryPosition(writer, playerId, x5, y5);

    public virtual void DestroySentry(NetWriter writer, byte playerId, byte killer, byte assistant, DamageSource cause) =>
        ServerMessages.DestroySentry(writer, playerId, killer, assistant, cause);

    // --- objectives ------------------------------------------------------------

    public virtual void GrabIntel(NetWriter writer, byte playerId) => ServerMessages.GrabIntel(writer, playerId);

    public virtual void DropIntel(NetWriter writer, byte playerId) => ServerMessages.DropIntel(writer, playerId);

    public virtual void ScoreIntel(NetWriter writer, byte playerId) => ServerMessages.ScoreIntel(writer, playerId);

    public virtual void ReturnIntel(NetWriter writer, Team team) => ServerMessages.ReturnIntel(writer, team);

    public virtual void GeneratorDestroy(NetWriter writer, Team team) => ServerMessages.GeneratorDestroy(writer, team);

    public virtual void UnlockControlPoints(NetWriter writer) => ServerMessages.UnlockControlPoints(writer);

    public virtual void ArenaRestart(NetWriter writer) => ServerMessages.ArenaRestart(writer);

    public virtual void ArenaStartRound(NetWriter writer) => ServerMessages.ArenaStartRound(writer);

    public virtual void ArenaWaitForPlayers(NetWriter writer) => ServerMessages.ArenaWaitForPlayers(writer);

    public virtual void ArenaEndRound(
        NetWriter writer,
        Team winner,
        byte redWins,
        byte blueWins,
        ReadOnlySpan<ArenaMvp> redMvps,
        ReadOnlySpan<ArenaMvp> blueMvps) =>
        ServerMessages.ArenaEndRound(writer, winner, redWins, blueWins, redMvps, blueMvps);

    // --- the round and the map -------------------------------------------------

    public virtual void ChangeMap(NetWriter writer, string map, string md5) => ServerMessages.ChangeMap(writer, map, md5);

    public virtual void MapEnd(NetWriter writer, string nextMap, Team winners, byte currentMapArea) =>
        ServerMessages.MapEnd(writer, nextMap, winners, currentMapArea);

    // --- text, rewards, plugins ------------------------------------------------

    public virtual void MessageString(NetWriter writer, string text) => ServerMessages.MessageString(writer, text);

    public virtual void RewardChallengeCode(NetWriter writer, ReadOnlySpan<byte> challenge) =>
        ServerMessages.RewardChallengeCode(writer, challenge);

    public virtual void RewardUpdate(NetWriter writer, byte playerId, string rewards) =>
        ServerMessages.RewardUpdate(writer, playerId, rewards);

    public virtual void PluginPacket(NetWriter writer, byte packetId, ReadOnlySpan<byte> data) =>
        ServerMessages.PluginPacket(writer, packetId, data);

    public virtual void Kick(NetWriter writer, KickReason reason) => ServerMessages.Kick(writer, reason);

    // --- prediction (docs/PREDICTION.md) ---------------------------------------

    public virtual void PredictWelcome(NetWriter writer, uint inputsRead) =>
        ServerMessages.PredictWelcome(writer, inputsRead);

    public virtual void PredictAck(NetWriter writer, uint inputsApplied) =>
        ServerMessages.PredictAck(writer, inputsApplied);
}

/// <summary>
/// The native protocol's encoder: <see cref="ServerEncoder"/> with whatever the
/// native wire does differently overridden.
/// </summary>
/// <remarks>
/// <b>Overrides nothing at native version 1</b>, so a native client receives
/// exactly the legacy bytes; <c>WireProtocolTests</c> pins that, and is the test
/// to update with the first override. An override changes the native wire
/// only: say what changed under "Native protocol versions" in
/// docs/PROTOCOL.md and bump <see cref="NativeProtocol.Version"/>.
/// </remarks>
public sealed class NativeServerEncoder : ServerEncoder
{
    internal NativeServerEncoder()
    {
    }

    public override WireProtocol Protocol => WireProtocol.Native;
}
