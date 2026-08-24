using Gg2.Protocol;

namespace Gg2.Server;

/// <summary>What a connection that has not finished joining is waiting for.</summary>
/// <remarks>
/// The values are the reference's, from
/// <c>Objects/InGameElements/JoiningPlayer.events/Create.xml</c>. They are not
/// on the wire, so they need not be reproduced — they are kept so the two state
/// machines can be read side by side.
/// </remarks>
public enum JoinState
{
    /// <summary>The 17-byte HELLO + UUID.</summary>
    ExpectHello = 1,

    /// <summary>A one-byte length prefix, after which <c>MessageState</c> resumes.</summary>
    ExpectMessageLength = 2,

    ExpectName = 3,
    ExpectPassword = 4,

    /// <summary>Nothing to read; the greeting is written on the next service call.</summary>
    ClientAuthenticated = 5,

    ExpectCommand = 6,

    /// <summary>Streaming a custom map. Never entered by a built-in-maps-only server.</summary>
    ClientDownloading = 7,

    /// <summary>
    /// The connection is finished — either it joined, or it fell through a case
    /// that set no next state.
    /// </summary>
    /// <remarks>
    /// The reference expresses this as <c>newState == -1</c> reaching
    /// <c>state = newState</c> at <c>serviceJoiningPlayer.gml:186</c>, which
    /// destroys the <c>JoiningPlayer</c> and its socket. That "fall through and
    /// die" is the default for every unhandled case, and it is load-bearing: a
    /// wrong password, a full server, an unrecognised command byte and a
    /// <c>PLAYER_JOIN</c> without a prior <c>RESERVE_SLOT</c> all end the
    /// connection.
    /// </remarks>
    Done = -1,
}

/// <summary>What servicing a joining connection produced.</summary>
public enum JoinOutcome
{
    /// <summary>Still handshaking. Call again next tick.</summary>
    Pending,

    /// <summary>The client completed the join and should become a roster entry.</summary>
    Joined,

    /// <summary>The connection is finished and should be dropped.</summary>
    Finished,
}

/// <summary>
/// The join handshake, transcribed from
/// <c>Scripts/GameServer/serviceJoiningPlayer.gml</c>.
/// </summary>
/// <remarks>
/// Pull-driven in the same way the reference is: each call reads at most one
/// step's worth of bytes and returns. Everything it writes goes into the
/// connection's own send queue, because the join path is the one place the
/// server sends per-client bytes rather than the shared broadcast stream.
/// </remarks>
public sealed class JoiningConnection(ClientConnection connection, DateTime now)
{
    private readonly ReceiveBuffer _received = new();
    private readonly NetWriter _out = new(256);

    private int _expectedBytes = 17;
    private JoinState _messageState;

    public ClientConnection Connection { get; } = connection;

    public JoinState State { get; private set; } = JoinState.ExpectHello;

    /// <summary>The name claimed by <c>RESERVE_SLOT</c>, truncated to 20 bytes.</summary>
    public string Name { get; private set; } = string.Empty;

    /// <summary>
    /// Whether this connection has reserved a slot. <c>PLAYER_JOIN</c> requires
    /// it; <c>PING</c> and <c>DOWNLOAD_MAP</c> do not.
    /// </summary>
    public bool OccupiesSlot { get; private set; }

    public DateTime LastContact { get; private set; } = now;

    /// <summary>
    /// Connections idle this long are dropped
    /// (<c>serviceJoiningPlayer.gml:1</c>, <c>current_time - lastContact &gt; 30000</c>).
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(30000);

    /// <summary>
    /// Advances the handshake as far as the bytes available allow.
    /// </summary>
    /// <param name="context">What the server needs to answer with.</param>
    /// <param name="now">Wall clock, for the idle timeout only.</param>
    public JoinOutcome Service(in JoinContext context, DateTime now)
    {
        if (Connection.IsClosed)
            return JoinOutcome.Finished;

        Connection.Receive(_received);

        if (Connection.IsClosed)
            return JoinOutcome.Finished;

        if (now - LastContact > Timeout)
        {
            Connection.Close("handshake timed out");
            return JoinOutcome.Finished;
        }

        // One step per call, exactly as the reference services one JoiningPlayer
        // per End Step. Looping here would let a client with a full buffer run
        // several steps in one tick, which is a behaviour difference for no gain.
        if (!_received.TryTake(_expectedBytes, out var payload))
        {
            FlushOut();
            return JoinOutcome.Pending;
        }

        LastContact = now;

        var next = JoinState.Done;
        var joined = false;

        switch (State)
        {
            case JoinState.ExpectHello:
                next = HandleHello(payload, context);
                break;

            case JoinState.ExpectMessageLength:
                _expectedBytes = payload[0];
                next = _messageState;
                break;

            case JoinState.ExpectPassword:
                next = HandlePassword(payload, context);
                break;

            case JoinState.ClientAuthenticated:
                WriteGreeting(context);
                next = JoinState.ExpectCommand;
                _expectedBytes = 1;
                break;

            case JoinState.ExpectCommand:
                next = HandleCommand(payload[0], out joined);
                break;

            case JoinState.ExpectName:
                next = HandleName(payload, context);
                break;
        }

        FlushOut();
        State = next;

        if (joined)
            return JoinOutcome.Joined;

        return State == JoinState.Done ? JoinOutcome.Finished : JoinOutcome.Pending;
    }

    private JoinState HandleHello(ReadOnlySpan<byte> payload, in JoinContext context)
    {
        // 17 bytes: the HELLO byte and all 16 of PROTOCOL_UUID. The reference
        // compares every one of them and answers a single byte on any mismatch.
        var sameProtocol = payload[0] == (byte)MessageType.Hello
                           && payload[1..17].SequenceEqual(GameConstants.ProtocolUuidBytes);

        if (!sameProtocol)
        {
            ServerMessages.IncompatibleProtocol(_out);
            Connection.Close("protocol uuid mismatch");
            return JoinState.Done;
        }

        if (context.Password.Length != 0)
        {
            ServerMessages.PasswordRequest(_out);
            _messageState = JoinState.ExpectPassword;
            _expectedBytes = 1;
            return JoinState.ExpectMessageLength;
        }

        // No password: straight to the greeting, with nothing left to read.
        // expectedBytes = 0 means the next service call runs immediately.
        _expectedBytes = 0;
        return JoinState.ClientAuthenticated;
    }

    private JoinState HandlePassword(ReadOnlySpan<byte> payload, in JoinContext context)
    {
        if (Gg2Text.Decode(payload) == context.Password)
        {
            _expectedBytes = 0;
            return JoinState.ClientAuthenticated;
        }

        // Not retryable: the reference writes PASSWORD_WRONG and falls through
        // with no next state, and the client destroys itself on receiving it.
        ServerMessages.PasswordWrong(_out);
        Connection.Close("wrong password");
        return JoinState.Done;
    }

    private JoinState HandleCommand(byte command, out bool joined)
    {
        joined = false;

        switch ((MessageType)command)
        {
            case MessageType.Ping:
                // Keeps the connection alive while the client loads plugins.
                _expectedBytes = 1;
                return JoinState.ExpectCommand;

            case MessageType.ReserveSlot:
                _messageState = JoinState.ExpectName;
                _expectedBytes = 1;
                return JoinState.ExpectMessageLength;

            case MessageType.PlayerJoin:
                if (!OccupiesSlot)
                {
                    // RESERVE_SLOT is required first, and the reference's answer
                    // to skipping it is to drop the connection.
                    Connection.Close("PLAYER_JOIN without RESERVE_SLOT");
                    return JoinState.Done;
                }

                joined = true;
                return JoinState.Done;

            case MessageType.DownloadMap:
                // Only reachable when a non-empty MD5 was advertised. A
                // built-in-maps-only server always advertises "", so the
                // reference's own guard fails and the connection ends.
                Connection.Close("map download requested for a built-in map");
                return JoinState.Done;

            default:
                Connection.Close($"unexpected command {command} during handshake");
                return JoinState.Done;
        }
    }

    private JoinState HandleName(ReadOnlySpan<byte> payload, in JoinContext context)
    {
        if (context.OccupiedSlots >= context.PlayerLimit)
        {
            ServerMessages.ServerFull(_out);
            Connection.Close("server full");
            return JoinState.Done;
        }

        OccupiesSlot = true;

        var name = Gg2Text.Decode(payload);
        Name = name.Length > GameConstants.MaxPlayerNameLength
            ? name[..GameConstants.MaxPlayerNameLength]
            : name;

        ServerMessages.ReserveSlot(_out);

        _expectedBytes = 1;
        return JoinState.ExpectCommand;
    }

    private void WriteGreeting(in JoinContext context)
    {
        ServerMessages.Hello(
            _out,
            context.ServerName,
            context.CurrentMap,
            // Empty MD5 means "built into the client", which removes the whole
            // map-download path from scope.
            mapMd5: string.Empty,
            pluginsRequired: false,
            pluginList: string.Empty);
    }

    private void FlushOut()
    {
        if (_out.IsEmpty)
            return;

        Connection.Send(_out.WrittenSpan);
        _out.Reset();
    }
}

/// <summary>What the handshake needs to know about the server it is joining.</summary>
public readonly struct JoinContext
{
    public required string ServerName { get; init; }

    public required string CurrentMap { get; init; }

    public required string Password { get; init; }

    public required int PlayerLimit { get; init; }

    /// <summary>
    /// Seats already taken: roster entries that count, plus every other joining
    /// connection that has already reserved.
    /// </summary>
    public required int OccupiedSlots { get; init; }
}
