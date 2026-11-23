namespace GG2.Protocol;

/// <summary>How the payload length of a client-to-server command is determined.</summary>
public enum CommandLength
{
    /// <summary>No such command. The byte is ignored and the next one tried.</summary>
    Invalid,

    /// <summary>A fixed number of payload bytes.</summary>
    Fixed,

    /// <summary>One length byte, then that many payload bytes.</summary>
    Prefixed1,

    /// <summary>Two length bytes (LE), then that many payload bytes.</summary>
    Prefixed2,
}

/// <summary>
/// The payload-length table for client-to-server commands: one instance per
/// <see cref="WireProtocol"/>.
/// </summary>
/// <remarks>
/// <see cref="Legacy"/> is transcribed from
/// <c>Scripts/GameServer/GameServerDefineCommands.gml</c>. Every byte not listed
/// there is <c>commandBytesInvalidCommand</c>, and the reference's response to
/// one is to <b>ignore it and try the next byte</b> — it does not resynchronise
/// and it does not kick. Reproduce that: a stricter port disconnects clients
/// the reference tolerates.
/// <para>
/// <see cref="Native"/> starts as a copy of the legacy table and is where a
/// native-only command is added. ⚠️ <b>A command added only there can never
/// reach the server from a stock client</b>, because the legacy table frames
/// its id as an unrecognised byte. That is the gate that keeps a native
/// feature off the legacy wire, so do not add a native command to
/// <see cref="Legacy"/> to keep the two in step.
/// </para>
/// </remarks>
public sealed class ClientCommandTable
{
    private const sbyte InvalidCommand = -1;
    private const sbyte PrefixLength1 = -2;
    private const sbyte PrefixLength2 = -3;

    private readonly sbyte[] _lengths;

    private ClientCommandTable(WireProtocol protocol, sbyte[] lengths)
    {
        Protocol = protocol;
        _lengths = lengths;
    }

    /// <summary>The stock client's commands, and the port's bare <c>PREDICT_HELLO</c>.</summary>
    public static ClientCommandTable Legacy { get; } = new(WireProtocol.Legacy, BuildLegacy());

    /// <summary>The native client's commands. Identical to <see cref="Legacy"/> at native version 1.</summary>
    public static ClientCommandTable Native { get; } = new(WireProtocol.Native, BuildNative());

    public static ClientCommandTable For(WireProtocol protocol) =>
        protocol == WireProtocol.Native ? Native : Legacy;

    public WireProtocol Protocol { get; }

    private static sbyte[] BuildLegacy()
    {
        var table = new sbyte[256];
        Array.Fill(table, InvalidCommand);

        table[(int)MessageType.PlayerLeave] = 0;
        table[(int)MessageType.PlayerChangeClass] = 1;
        table[(int)MessageType.PlayerChangeTeam] = 1;
        table[(int)MessageType.ChatBubble] = 1;
        table[(int)MessageType.BuildSentry] = 0;
        table[(int)MessageType.DestroySentry] = 0;
        table[(int)MessageType.DropIntel] = 0;
        table[(int)MessageType.Omnomnomnom] = 0;
        table[(int)MessageType.ToggleZoom] = 0;
        table[(int)MessageType.PlayerChangeName] = PrefixLength1;
        table[(int)MessageType.InputState] = 4;
        table[(int)MessageType.RewardRequest] = PrefixLength1;
        table[(int)MessageType.RewardChallengeResponse] = 16;
        table[(int)MessageType.PluginPacket] = PrefixLength2;
        table[(int)MessageType.ClientSettings] = 1;

        // The port's own: PREDICT_HELLO. A bare byte, so a GM8 server that does
        // not know it skips exactly that byte and loses nothing after it.
        // docs/PREDICTION.md.
        table[(int)MessageType.Predict] = 0;

        return table;
    }

    private static sbyte[] BuildNative()
    {
        var table = BuildLegacy();

        // Native-only commands go here, each with a NativeProtocol.Version bump
        // and a case in GameServer.Handle. docs/PROTOCOL.md, "Two protocols".

        return table;
    }

    /// <summary>How the payload of <paramref name="command"/> is framed.</summary>
    public CommandLength KindOf(byte command) => _lengths[command] switch
    {
        InvalidCommand => CommandLength.Invalid,
        PrefixLength1 => CommandLength.Prefixed1,
        PrefixLength2 => CommandLength.Prefixed2,
        _ => CommandLength.Fixed,
    };

    /// <summary>
    /// Payload byte count for a <see cref="CommandLength.Fixed"/> command.
    /// Meaningless for the others.
    /// </summary>
    public int FixedLengthOf(byte command) => _lengths[command];

    public bool IsValid(byte command) => _lengths[command] != InvalidCommand;
}

/// <summary>
/// The legacy table's lookups, and the flood limit both protocols share.
/// </summary>
public static class ClientCommands
{
    /// <summary>How the payload of <paramref name="command"/> is framed on the legacy wire.</summary>
    public static CommandLength KindOf(byte command) => ClientCommandTable.Legacy.KindOf(command);

    /// <summary>
    /// Payload byte count for a legacy <see cref="CommandLength.Fixed"/>
    /// command. Meaningless for the others.
    /// </summary>
    public static int FixedLengthOf(byte command) => ClientCommandTable.Legacy.FixedLengthOf(command);

    public static bool IsValid(byte command) => ClientCommandTable.Legacy.IsValid(command);

    /// <summary>
    /// Commands processed per player per tick before the rest are left buffered
    /// — <c>processClientCommands.gml</c>'s <c>commandLimitRemaining</c>, flood
    /// protection.
    /// </summary>
    /// <remarks>
    /// The limit counts <em>completed</em> commands only. An invalid byte does
    /// not spend one, so a client sending garbage is not rate-limited by this;
    /// it is simply ignored.
    /// </remarks>
    public const int PerTickLimit = 10;
}
