using System.Buffers.Binary;
using System.Text;
using Gg2.Protocol;

namespace Gg2.Server;

/// <summary>
/// What the server announces about itself to the public lobby at
/// <see cref="GameConstants.LobbyServerHost"/>.
/// </summary>
/// <remarks>
/// Everything the registration packet carries, gathered once so the builder is a
/// pure function of it and can be tested without a socket or a running match.
/// </remarks>
public readonly record struct LobbyServerInfo
{
    /// <summary>The 16 random bytes identifying this server for the lobby's lifetime.</summary>
    /// <remarks>
    /// <c>GameServerCreate.gml:26-28</c> — sixteen <c>irandom(255)</c> at create,
    /// and the same bytes for every later registration and the unregister. It is
    /// the lobby's primary key: a fresh one each time would leave a stale entry
    /// behind on every restart.
    /// </remarks>
    public required ReadOnlyMemory<byte> ServerId { get; init; }

    public required string ServerName { get; init; }

    public required string Map { get; init; }

    public required ushort Port { get; init; }

    public required ushort PlayerLimit { get; init; }

    public required ushort OccupiedSlots { get; init; }

    public required bool Passworded { get; init; }
}

/// <summary>
/// The two UDP datagrams the reference sends to the lobby server.
/// </summary>
/// <remarks>
/// <c>Scripts/GameServer/sendLobbyRegistration.gml</c> and
/// <c>sendLobbyUnreg.gml</c>.
/// <para>
/// <b>BIG-ENDIAN, and it is the only big-endian thing in this project.</b>
/// <c>sendLobbyRegistration.gml:9</c> is an explicit
/// <c>set_little_endian(lobbyBuffer, false)</c>, so every <c>ushort</c> here is
/// the other way round from the game protocol that <see cref="NetWriter"/>
/// writes. That is why this builds its own bytes rather than reusing the writer:
/// a <c>NetWriter</c> here would produce a packet the lobby silently ignores,
/// which is indistinguishable from the server simply not being listed.
/// </para>
/// <para>
/// <b>This is the one thing in the port that talks to a third party</b>, and
/// it is off unless <c>--lobby</c> is passed. <c>docs/PROTOCOL.md</c>: "Skip it,
/// or gate it behind a flag that defaults off." Announcing a private server to a
/// public list without being asked is not a default anyone should get.
/// </para>
/// <para>
/// <b>The live service accepts this packet</b> — a <c>--lobby</c> server on a
/// public address was read back out of the real list on 2026-09-07,
/// <c>docs/PROTOCOL.md</c>, "What a headless server can skip". No test contacts
/// it: the layout is asserted byte-for-byte against the GML, which is a
/// different claim, and the service is a third party. If a <c>--lobby</c> server
/// never appears in the list, check the service before re-deriving the layout
/// </para>
/// </remarks>
public static class LobbyRegistration
{
    /// <summary>How many bytes a server id is, and it is not negotiable.</summary>
    public const int ServerIdLength = 16;

    /// <summary>
    /// Message type "register", as a UUID.
    /// </summary>
    /// <remarks><c>sendLobbyRegistration.gml:12</c>, verbatim.</remarks>
    public const string RegisterMessageUuid = "b5dae2e8-424f-9ed0-0fcb-8c21c7ca1352";

    /// <summary>Message type "unregister".</summary>
    /// <remarks><c>sendLobbyUnreg.gml:18</c>, verbatim.</remarks>
    public const string UnregisterMessageUuid = "488984ac-45dc-86e1-9901-98dd1c01c064";

    /// <summary>
    /// How often the reference re-announces: every 900 frames, so 30 seconds.
    /// </summary>
    /// <remarks>
    /// <c>GameServerBeginStep.gml:5-8</c>, <c>frame mod 900</c>. It also
    /// re-announces out of cadence on a map change, on a join, and on a leave
    /// that un-fills a full server — all four are wired in
    /// <see cref="GameServer"/>.
    /// </remarks>
    public const int ReRegisterIntervalTicks = 900;

    /// <summary>
    /// How often the lobby has to hear from this server in REAL time, in
    /// milliseconds.
    /// </summary>
    /// <remarks>
    /// The same 30 seconds as <see cref="ReRegisterIntervalTicks"/>, counted the
    /// other way, and it is the one that governs.
    /// <para>
    /// <b>A tick count cannot express this cadence, because a tick is not a
    /// fixed length here.</b> <c>ServerOptions.IdleWhenEmpty</c> drops an empty
    /// server to <c>ServerOptions.IdleTickRate</c>, at which 900 ticks is seven
    /// and a half minutes. The lobby drops an entry it has not heard from within
    /// something under a minute — measured against the live service, 2026-09-07 —
    /// so a purely frame-counted cadence lists an empty server, loses it, and
    /// never gets it back, because the player who would have un-idled it cannot
    /// see it to join.
    /// </para>
    /// <para>
    /// The reference has no idle rate, so its <c>frame mod 900</c> is 30 seconds
    /// and this distinction never arises there.
    /// </para>
    /// </remarks>
    public const int ReRegisterIntervalMs = 30_000;

    /// <summary>
    /// Whether the 30-second block is due, on either of its two clocks.
    /// </summary>
    /// <param name="frame">Ticks since start.</param>
    /// <param name="nowMs">A monotonic millisecond reading, <c>Environment.TickCount64</c>.</param>
    /// <param name="lastMs">The same reading from when the block last ran.</param>
    /// <remarks>
    /// <b>Two arms, and neither is redundant.</b> The wall clock is what the
    /// lobby actually contracts on, per <see cref="ReRegisterIntervalMs"/>. The
    /// frame count stays because every test and the control port drive
    /// <c>GameServer.Tick</c> as fast as the machine allows, and against a real
    /// clock those runs would never reach this block at all.
    /// </remarks>
    public static bool IsReRegisterDue(long frame, long nowMs, long lastMs) =>
        frame % ReRegisterIntervalTicks == 0 || nowMs - lastMs >= ReRegisterIntervalMs;

    /// <summary>A fresh random server id.</summary>
    /// <remarks>
    /// <b>Not drawn from <c>WorldState.Random</c>.</b> That stream is
    /// observable simulation — every seeded weapon puts its draw on the wire —
    /// and taking sixteen bytes out of it at startup would shift every later
    /// spread. The reference's <c>irandom</c> here is the menu's RNG, not the
    /// match's.
    /// </remarks>
    public static byte[] NewServerId()
    {
        var id = new byte[ServerIdLength];
        System.Security.Cryptography.RandomNumberGenerator.Fill(id);
        return id;
    }

    /// <summary>The register datagram.</summary>
    public static byte[] BuildRegister(in LobbyServerInfo info)
    {
        if (info.ServerId.Length != ServerIdLength)
            throw new ArgumentException($"a server id is {ServerIdLength} bytes", nameof(info));

        var packet = new List<byte>(256);

        WriteUuid(packet, RegisterMessageUuid);
        packet.AddRange(info.ServerId.Span);
        WriteUuid(packet, GameConstants.Gg2LobbyUuid);

        // 0 is TCP. The reference never sends anything else.
        packet.Add(0);

        WriteUShort(packet, info.Port);
        WriteUShort(packet, info.PlayerLimit);
        WriteUShort(packet, info.OccupiedSlots);

        // Bots are announced as ZERO, and this port fills itself with them.
        // Transcribed rather than corrected: the field's meaning is the lobby's,
        // not ours, and a number the reference never sends is a number no client
        // of that list has ever had to render. Worth revisiting WITH the lobby's
        // owners rather than unilaterally — see docs/TRAPS-LIVEGAME.md.
        WriteUShort(packet, 0);

        WriteUShort(packet, info.Passworded ? (ushort)1 : (ushort)0);

        // Seven key/value pairs follow. Six go through writeKeyValue; the
        // seventh is written by hand because its value is 16 raw bytes rather
        // than text.
        WriteUShort(packet, 7);

        WriteKeyValue(packet, "name", info.ServerName);
        WriteKeyValue(packet, "game", GameConstants.GameNameString);
        WriteKeyValue(packet, "game_short", "gg2");
        WriteKeyValue(packet, "game_ver", GameConstants.GameVersionString);
        WriteKeyValue(packet, "game_url", GameConstants.GameUrlString);
        WriteKeyValue(packet, "map", info.Map);

        // sendLobbyRegistration.gml:33-36 — the same shape as writeKeyValue, but
        // the value is the protocol UUID's bytes, so it cannot go through it.
        var key = Encoding.UTF8.GetBytes("protocol_id");
        packet.Add(checked((byte)key.Length));
        packet.AddRange(key);
        WriteUShort(packet, 16);
        packet.AddRange(GameConstants.ProtocolUuidBytes);

        return [.. packet];
    }

    /// <summary>The unregister datagram: the message type and the server id, and nothing else.</summary>
    public static byte[] BuildUnregister(ReadOnlySpan<byte> serverId)
    {
        if (serverId.Length != ServerIdLength)
            throw new ArgumentException($"a server id is {ServerIdLength} bytes", nameof(serverId));

        var packet = new List<byte>(32);

        WriteUuid(packet, UnregisterMessageUuid);
        packet.AddRange(serverId);

        return [.. packet];
    }

    /// <summary>
    /// <c>parseUuid.gml</c>: strip the hyphens, write the sixteen bytes in
    /// textual order.
    /// </summary>
    /// <remarks>
    /// <b>Textual order, not .NET's <c>Guid.ToByteArray()</c> order.</b> A
    /// .NET <see cref="Guid"/> byte-reverses its first three groups, so parsing
    /// these through <c>Guid</c> would put four bytes in the wrong places and
    /// the lobby would never recognise the message type. The same trap
    /// <c>GameConstants.ProtocolUuidBytes</c> exists to avoid.
    /// </remarks>
    private static void WriteUuid(List<byte> packet, string uuid) =>
        packet.AddRange(Convert.FromHexString(uuid.Replace("-", string.Empty, StringComparison.Ordinal)));

    /// <summary>
    /// <c>writeKeyValue.gml</c>: a byte-counted key, then a short-counted value.
    /// </summary>
    /// <remarks>
    /// The reference truncates a value at 65535 and raises an error on a key over
    /// 255. A key here is always a literal, so only the value can overrun — and
    /// the server name is already capped far below that.
    /// </remarks>
    private static void WriteKeyValue(List<byte> packet, string key, string value)
    {
        var keyBytes = Encoding.UTF8.GetBytes(key);
        var valueBytes = Encoding.UTF8.GetBytes(value);

        if (valueBytes.Length > ushort.MaxValue)
            valueBytes = valueBytes[..ushort.MaxValue];

        packet.Add(checked((byte)keyBytes.Length));
        packet.AddRange(keyBytes);
        WriteUShort(packet, checked((ushort)valueBytes.Length));
        packet.AddRange(valueBytes);
    }

    /// <summary>A big-endian ushort. See this type's remarks.</summary>
    private static void WriteUShort(List<byte> packet, ushort value)
    {
        Span<byte> pair = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(pair, value);
        packet.Add(pair[0]);
        packet.Add(pair[1]);
    }
}
