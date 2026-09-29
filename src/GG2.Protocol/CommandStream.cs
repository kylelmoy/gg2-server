namespace GG2.Protocol;

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
/// One run of consecutive unrecognised command bytes, with the context that
/// says what the sender had just done.
/// </summary>
/// <remarks>
/// ⚠️ <b>The run, not the byte, is the unit that identifies the cause.</b> A
/// framer that has lost alignment produces one long run; a sender emitting a
/// command this server does not know produces a short run of the same shape
/// over and over. Counting bytes alone cannot tell those apart, and
/// <see cref="CommandStream.InvalidBytesSkipped"/> is what the connection guard
/// acts on — so when it fires, this is the evidence that says whether the
/// number or the sender is the thing at fault.
/// </remarks>
public struct InvalidRun
{
    /// <summary>Byte values kept from the start of a run.</summary>
    public const int SampleBytes = 8;

    /// <summary>Bytes the stream had consumed before this run's first byte.</summary>
    public long Offset;

    /// <summary>
    /// The last command completed before this run, or -1 when the run precedes
    /// every completed command — which is itself the answer when a connection
    /// never gets framed at all.
    /// </summary>
    public int AfterCommand;

    /// <summary>Payload length of <see cref="AfterCommand"/>.</summary>
    public int AfterPayloadLength;

    /// <summary>Commands completed on this stream before the run.</summary>
    public long AfterCommandCount;

    /// <summary>Consecutive unrecognised bytes in the run.</summary>
    public int Length;

    /// <summary>Values from the start of the run; <see cref="SampleCount"/> are set.</summary>
    public InvalidRunSample Sample;

    /// <summary>How many of <see cref="Sample"/> are set.</summary>
    public int SampleCount;
}

/// <summary>The first <see cref="InvalidRun.SampleBytes"/> bytes of a run.</summary>
[System.Runtime.CompilerServices.InlineArray(InvalidRun.SampleBytes)]
public struct InvalidRunSample
{
    private byte _first;
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

    /// <summary>How many runs are kept. Enough to see a pattern, small enough to allocate once.</summary>
    private const int MaxRecordedRuns = 8;

    private long _consumed;
    private long _completed;
    private int _lastCommand = -1;
    private int _lastPayloadLength;

    // Allocated on the first unrecognised byte and never after, so a
    // connection that frames cleanly never pays for this at all.
    private InvalidRun[]? _runs;
    private int _recordedRuns;
    private long _runsSeen;

    // -1 while the previous byte was not an unrecognised one; otherwise the
    // index of the run being extended, or int.MinValue once MaxRecordedRuns
    // runs have been recorded and the current one is being counted only.
    private int _openRun = -1;

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
    /// server scan every byte of it, every tick, for free.
    /// <c>ConnectionLimits.MaxInvalidCommandBytes</c> is where it is enforced.
    /// </para>
    /// <para>
    /// ⚠️ <b>A stock client sends none while it is healthy, and one per command
    /// once it is not.</b> A client in a runaway state — about 293 commands a
    /// second against a 30 Hz tick — emitted exactly one stray <c>0x00</c> per
    /// framed command; each run is a single byte and the next command frames
    /// correctly, so nothing is desynchronised. The controlled re-run at
    /// <c>room_speed</c> 30 over the same link produced zero in 15 minutes.
    /// <see cref="InvalidRuns"/> is what made the difference legible.
    /// docs/HARDENING.md, "The one guard that has been seen to fire on a stock
    /// client".
    /// </para>
    /// </remarks>
    public long InvalidBytesSkipped { get; private set; }

    /// <summary>
    /// Runs of unrecognised bytes recorded from this stream, oldest first, at
    /// most <see cref="MaxRecordedRuns"/> of them.
    /// </summary>
    /// <remarks>
    /// The cap is on what is <em>kept</em>, not on what is counted:
    /// <see cref="InvalidRunsSeen"/> and <see cref="InvalidBytesSkipped"/> keep
    /// rising after it. Bounded because this is a per-connection buffer filled
    /// by whatever the peer sends, which is the one thing a connection must not
    /// be able to make the server hold without limit.
    /// </remarks>
    public ReadOnlySpan<InvalidRun> InvalidRuns =>
        _runs is null ? default : _runs.AsSpan(0, _recordedRuns);

    /// <summary>Runs of unrecognised bytes seen, including those past the cap.</summary>
    public long InvalidRunsSeen => _runsSeen;

    /// <summary>Commands this stream has framed.</summary>
    public long CommandsFramed => _completed;


    /// <summary>
    /// Pulls the next complete command out of <paramref name="source"/>,
    /// skipping any invalid command bytes in the way. Returns false when more
    /// bytes are needed.
    /// </summary>
    public bool TryRead(ReceiveBuffer source, out ClientCommand command)
    {
        while (source.TryTake(_expected, out var bytes))
        {
            _consumed += bytes.Length;

            switch (_state)
            {
                case State.Command:
                    _command = bytes[0];

                    switch (ClientCommands.KindOf(_command))
                    {
                        case CommandLength.Invalid:
                            // Ignore it and wait for another command byte.
                            InvalidBytesSkipped++;
                            NoteInvalid(_command);
                            continue;
                        case CommandLength.Prefixed1:
                            _openRun = -1;
                            _state = State.Length1;
                            _expected = 1;
                            continue;
                        case CommandLength.Prefixed2:
                            _openRun = -1;
                            _state = State.Length2;
                            _expected = 2;
                            continue;
                        default:
                            _openRun = -1;
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
                    _completed++;
                    _lastCommand = _command;
                    _lastPayloadLength = bytes.Length;
                    return true;
            }
        }

        command = default;
        return false;
    }

    /// <summary>
    /// Records one unrecognised byte against the run it belongs to.
    /// </summary>
    /// <remarks>
    /// Off the path a stock client takes: the first call is the only one that
    /// allocates, and a session that frames cleanly never makes it.
    /// </remarks>
    private void NoteInvalid(byte value)
    {
        if (_openRun == -1)
        {
            _runsSeen++;

            if (_recordedRuns < MaxRecordedRuns)
            {
                _runs ??= new InvalidRun[MaxRecordedRuns];
                _openRun = _recordedRuns++;
                _runs[_openRun] = new InvalidRun
                {
                    // The byte has already been consumed, so the run starts one back.
                    Offset = _consumed - 1,
                    AfterCommand = _lastCommand,
                    AfterPayloadLength = _lastPayloadLength,
                    AfterCommandCount = _completed,
                };
            }
            else
            {
                // Still a run in progress, just not one being written down.
                _openRun = int.MinValue;
            }
        }

        if (_openRun < 0)
            return;

        ref var run = ref _runs![_openRun];
        run.Length++;

        if (run.SampleCount < InvalidRun.SampleBytes)
            run.Sample[run.SampleCount++] = value;
    }
}
