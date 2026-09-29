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
/// The payload-length table for client-to-server commands, transcribed from
/// <c>Scripts/GameServer/GameServerDefineCommands.gml</c>.
/// </summary>
/// <remarks>
/// Every byte not listed there is <c>commandBytesInvalidCommand</c>, and the
/// reference's response to one is to <b>ignore it and try the next byte</b> —
/// it does not resynchronise and it does not kick. Reproduce that: a stricter
/// port disconnects clients the reference tolerates.
/// </remarks>
public static class ClientCommands
{
    private static readonly sbyte[] Lengths = BuildTable();

    private const sbyte InvalidCommand = -1;
    private const sbyte PrefixLength1 = -2;
    private const sbyte PrefixLength2 = -3;

    private static sbyte[] BuildTable()
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

        return table;
    }

    /// <summary>How the payload of <paramref name="command"/> is framed.</summary>
    public static CommandLength KindOf(byte command) => Lengths[command] switch
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
    public static int FixedLengthOf(byte command) => Lengths[command];

    public static bool IsValid(byte command) => Lengths[command] != InvalidCommand;

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
