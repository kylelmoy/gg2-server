namespace Gg2.Protocol;

/// <summary>One framed client-to-server command: the command byte and its payload.</summary>
/// <remarks>
/// <see cref="Payload"/> points into the <see cref="ReceiveBuffer"/> it came
/// from and is valid only until that buffer is next touched. Copy anything that
/// must outlive the handler.
/// </remarks>
public readonly ref struct ClientCommand(byte command, ReadOnlySpan<byte> payload)
{
    public byte Command { get; } = command;

    public MessageType Type => (MessageType)Command;

    public ReadOnlySpan<byte> Payload { get; } = payload;
}

/// <summary>
/// Reassembles a client's byte stream into framed commands.
/// </summary>
/// <remarks>
/// The reference does this with a four-state machine driven by
/// <c>tcp_receive(socket, n)</c> (<c>Scripts/GameServer/processClientCommands.gml</c>).
/// The shape is reproduced here — including the two behaviours a from-scratch
/// framer would get wrong:
/// <list type="bullet">
/// <item>An unrecognised command byte is <b>skipped</b>, not treated as a
/// framing error. The stream is not resynchronised and the client is not
/// kicked, and it does not spend one of the ten commands a tick allows.</item>
/// <item>A zero-length command completes immediately, with no wait for payload
/// bytes that will never come.</item>
/// </list>
/// <para>
/// Holds framing state only; the bytes live in the caller's
/// <see cref="ReceiveBuffer"/>. Pure — it never touches a socket.
/// </para>
/// </remarks>
public sealed class CommandStream
{
    private enum State
    {
        /// <summary>Waiting for a command byte.</summary>
        Command,

        /// <summary>Waiting for a one-byte length prefix.</summary>
        Length1,

        /// <summary>Waiting for a two-byte length prefix.</summary>
        Length2,

        /// <summary>Waiting for payload.</summary>
        Payload,
    }

    private State _state = State.Command;
    private int _expected = 1;
    private byte _command;

    /// <summary>How many bytes must arrive before the framer can make progress.</summary>
    public int ExpectedBytes => _expected;

    /// <summary>
    /// Unrecognised command bytes skipped over the life of this stream.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Counted rather than acted on here, because the framer is not the
    /// layer that gets to decide.</b> Skipping is the reference's behaviour and
    /// this class reproduces it (see the type's remarks): a stray byte is
    /// ignored, the stream is not resynchronised, the client is not kicked, and
    /// it does not spend one of the ten commands a tick allows.
    /// <para>
    /// That last clause is the problem this counter exists for. Skipping is the
    /// one path through the framer that consumes buffered bytes without
    /// producing a command, so the reference's own flood control does not
    /// rate-limit it at all — a client sending nothing but garbage makes the
    /// server scan every byte of it, every tick, for free. A stock client sends
    /// exactly zero of these in a session, so a total this high is a statement
    /// about the client rather than about the network.
    /// <c>ConnectionLimits.MaxInvalidCommandBytes</c> is where it is enforced.
    /// </para>
    /// </remarks>
    public long InvalidBytesSkipped { get; private set; }

    /// <summary>
    /// Pulls the next complete command out of <paramref name="source"/>,
    /// skipping any invalid command bytes in the way. Returns false when more
    /// bytes are needed.
    /// </summary>
    public bool TryRead(ReceiveBuffer source, out ClientCommand command)
    {
        while (source.TryTake(_expected, out var bytes))
        {
            switch (_state)
            {
                case State.Command:
                    _command = bytes[0];

                    switch (ClientCommands.KindOf(_command))
                    {
                        case CommandLength.Invalid:
                            // Ignore it and wait for another command byte.
                            InvalidBytesSkipped++;
                            continue;
                        case CommandLength.Prefixed1:
                            _state = State.Length1;
                            _expected = 1;
                            continue;
                        case CommandLength.Prefixed2:
                            _state = State.Length2;
                            _expected = 2;
                            continue;
                        default:
                            _state = State.Payload;
                            _expected = ClientCommands.FixedLengthOf(_command);
                            continue;
                    }

                case State.Length1:
                    _expected = bytes[0];
                    _state = State.Payload;
                    continue;

                case State.Length2:
                    _expected = bytes[0] | (bytes[1] << 8);
                    _state = State.Payload;
                    continue;

                case State.Payload:
                    command = new ClientCommand(_command, bytes);
                    _state = State.Command;
                    _expected = 1;
                    return true;
            }
        }

        command = default;
        return false;
    }
}
