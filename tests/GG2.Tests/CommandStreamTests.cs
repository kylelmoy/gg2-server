using GG2.Protocol;

namespace GG2.Tests;

/// <summary>
/// Client-to-server command framing.
/// </summary>
public sealed class CommandStreamTests
{
    private readonly ReceiveBuffer _buffer = new();
    private readonly CommandStream _stream = new();

    [Fact]
    public void ReadsAFixedLengthCommand()
    {
        Feed((byte)MessageType.InputState, 0x80, 0x00, 0x40, 0x7F);

        Assert.True(_stream.TryRead(_buffer, out var command));
        Assert.Equal(MessageType.InputState, command.Type);

        var reader = new NetReader(command.Payload);
        var input = PlayerInput.Read(ref reader);

        Assert.Equal(PlayerKeys.Jump, input.Keys);
        Assert.Equal(0x4000, input.NetAimDirection);
        Assert.Equal(0x7F, input.AimDistance);
    }

    /// <summary>
    /// A zero-payload command completes as soon as its byte arrives — there is
    /// nothing to wait for.
    /// </summary>
    [Fact]
    public void ReadsAZeroLengthCommand()
    {
        Feed((byte)MessageType.DropIntel);

        Assert.True(_stream.TryRead(_buffer, out var command));
        Assert.Equal(MessageType.DropIntel, command.Type);
        Assert.True(command.Payload.IsEmpty);
    }

    [Fact]
    public void ReadsAOneBytePrefixedCommand()
    {
        Feed((byte)MessageType.PlayerChangeName, 3, (byte)'a', (byte)'b', (byte)'c');

        Assert.True(_stream.TryRead(_buffer, out var command));
        Assert.Equal(MessageType.PlayerChangeName, command.Type);
        Assert.Equal("abc", GG2Text.Decode(command.Payload));
    }

    [Fact]
    public void ReadsATwoBytePrefixedCommand()
    {
        // PLUGIN_PACKET's length is a little-endian ushort.
        Feed((byte)MessageType.PluginPacket, 0x02, 0x00, 0xAA, 0xBB);

        Assert.True(_stream.TryRead(_buffer, out var command));
        Assert.Equal(MessageType.PluginPacket, command.Type);
        Assert.Equal(new byte[] { 0xAA, 0xBB }, command.Payload.ToArray());
    }

    /// <summary>
    /// The reference ignores an unrecognised byte and waits for the next one. It
    /// does not resynchronise and it does not kick — a stricter port would
    /// disconnect clients the reference tolerates.
    /// </summary>
    [Fact]
    public void SkipsInvalidCommandBytesWithoutLosingTheNextCommand()
    {
        // 26, 29, 48 and 49 are unallocated in Constants.xml.
        Feed(26, 29, 48, 49, (byte)MessageType.ToggleZoom);

        Assert.True(_stream.TryRead(_buffer, out var command));
        Assert.Equal(MessageType.ToggleZoom, command.Type);
        Assert.False(_stream.TryRead(_buffer, out _));
    }

    /// <summary>
    /// ⚠️ <b>The guard acts on a byte count, and a byte count cannot be acted
    /// on.</b> A run is the unit that says whether the sender is emitting
    /// something unknown or this framer has lost alignment, so the run — where
    /// it started, what command it followed, how long it was and what was in it
    /// — is what the stream records for the log line that reports a kick.
    /// </summary>
    [Fact]
    public void RecordsWhatWasSkippedAndWhatItFollowed()
    {
        Feed((byte)MessageType.InputState, 0x20, 0x00, 0x40, 0x10);
        Assert.True(_stream.TryRead(_buffer, out _));

        // 26, 29 and 48 are unallocated in Constants.xml.
        Feed(26, 29, 48, (byte)MessageType.ToggleZoom);
        Assert.True(_stream.TryRead(_buffer, out var recovered));
        Assert.Equal(MessageType.ToggleZoom, recovered.Type);

        Assert.Equal(3, _stream.InvalidBytesSkipped);
        Assert.Equal(1, _stream.InvalidRunsSeen);
        Assert.Equal(2, _stream.CommandsFramed);

        var run = Assert.Single(_stream.InvalidRuns.ToArray());
        Assert.Equal(3, run.Length);

        // The five bytes of the INPUTSTATE came first.
        Assert.Equal(5, run.Offset);
        Assert.Equal((int)MessageType.InputState, run.AfterCommand);
        Assert.Equal(4, run.AfterPayloadLength);
        Assert.Equal(1, run.AfterCommandCount);

        Assert.Equal(3, run.SampleCount);
        Assert.Equal(new byte[] { 26, 29, 48 }, Sample(run));
    }

    /// <summary>
    /// A run ends at the next byte the table knows, so garbage split by real
    /// commands reads as several short runs rather than one long one. That is
    /// the distinction the report is for.
    /// </summary>
    [Fact]
    public void SeparatesRunsBrokenByACommandTheTableKnows()
    {
        Feed(26, (byte)MessageType.BuildSentry, 29, 48, (byte)MessageType.DestroySentry);

        Assert.True(_stream.TryRead(_buffer, out _));
        Assert.True(_stream.TryRead(_buffer, out _));

        Assert.Equal(3, _stream.InvalidBytesSkipped);
        Assert.Equal(2, _stream.InvalidRunsSeen);

        var runs = _stream.InvalidRuns.ToArray();
        Assert.Equal(2, runs.Length);

        Assert.Equal(1, runs[0].Length);
        Assert.Equal(-1, runs[0].AfterCommand);
        Assert.Equal(0, runs[0].AfterCommandCount);

        Assert.Equal(2, runs[1].Length);
        Assert.Equal((int)MessageType.BuildSentry, runs[1].AfterCommand);
        Assert.Equal(0, runs[1].AfterPayloadLength);
        Assert.Equal(1, runs[1].AfterCommandCount);
    }

    /// <summary>
    /// ⛔ <b>What is kept is bounded; what is counted is not.</b> The record is
    /// a per-connection buffer filled by whatever the peer sends, which is the
    /// one thing a connection must not be able to make the server hold without
    /// limit — but the totals the guard acts on have to keep rising past the
    /// cap or the guard would stop seeing the flood.
    /// </summary>
    [Fact]
    public void StopsKeepingRunsLongBeforeItStopsCountingThem()
    {
        for (var i = 0; i < 64; i++)
            Feed(26, (byte)MessageType.BuildSentry);

        while (_stream.TryRead(_buffer, out _))
        {
        }

        Assert.Equal(64, _stream.InvalidBytesSkipped);
        Assert.Equal(64, _stream.InvalidRunsSeen);
        Assert.Equal(8, _stream.InvalidRuns.Length);
    }

    /// <summary>
    /// A long run keeps its length and its first bytes, and says that the rest
    /// were dropped by leaving the sample short of it.
    /// </summary>
    [Fact]
    public void KeepsTheStartOfARunLongerThanTheSample()
    {
        var garbage = new byte[64];
        Array.Fill(garbage, (byte)26);
        garbage[0] = 29;
        garbage[1] = 48;
        Feed(garbage);

        Assert.False(_stream.TryRead(_buffer, out _));

        var run = Assert.Single(_stream.InvalidRuns.ToArray());
        Assert.Equal(64, run.Length);
        Assert.Equal(InvalidRun.SampleBytes, run.SampleCount);
        Assert.Equal(new byte[] { 29, 48, 26, 26, 26, 26, 26, 26 }, Sample(run));
    }

    /// <summary>A stock client sends none of these, and pays nothing for the record.</summary>
    [Fact]
    public void RecordsNothingForAStreamThatFramesCleanly()
    {
        Feed((byte)MessageType.InputState, 0x20, 0x00, 0x40, 0x10);
        Assert.True(_stream.TryRead(_buffer, out _));

        Assert.Equal(0, _stream.InvalidBytesSkipped);
        Assert.Equal(0, _stream.InvalidRunsSeen);
        Assert.True(_stream.InvalidRuns.IsEmpty);
    }

    private static byte[] Sample(InvalidRun run)
    {
        var bytes = new byte[run.SampleCount];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = run.Sample[i];
        return bytes;
    }

    [Fact]
    public void ReassemblesACommandSplitAcrossReceives()
    {
        Feed((byte)MessageType.InputState, 0x20);
        Assert.False(_stream.TryRead(_buffer, out _));

        Feed(0x00);
        Assert.False(_stream.TryRead(_buffer, out _));

        Feed(0x40, 0x10);
        Assert.True(_stream.TryRead(_buffer, out var command));
        Assert.Equal(MessageType.InputState, command.Type);
        Assert.Equal(4, command.Payload.Length);
    }

    [Fact]
    public void ReadsSeveralCommandsFromOneReceive()
    {
        Feed(
            (byte)MessageType.BuildSentry,
            (byte)MessageType.PlayerChangeTeam, (byte)Team.Blue,
            (byte)MessageType.DestroySentry);

        Assert.True(_stream.TryRead(_buffer, out var first));
        Assert.Equal(MessageType.BuildSentry, first.Type);

        Assert.True(_stream.TryRead(_buffer, out var second));
        Assert.Equal(MessageType.PlayerChangeTeam, second.Type);
        Assert.Equal((byte)Team.Blue, second.Payload[0]);

        Assert.True(_stream.TryRead(_buffer, out var third));
        Assert.Equal(MessageType.DestroySentry, third.Type);

        Assert.False(_stream.TryRead(_buffer, out _));
    }

    [Fact]
    public void ZeroLengthPrefixedCommandCompletesImmediately()
    {
        Feed((byte)MessageType.RewardRequest, 0);

        Assert.True(_stream.TryRead(_buffer, out var command));
        Assert.Equal(MessageType.RewardRequest, command.Type);
        Assert.True(command.Payload.IsEmpty);
    }

    [Fact]
    public void SurvivesALargePrefixedPayload()
    {
        var payload = new byte[4096];
        Random.Shared.NextBytes(payload);

        var message = new byte[3 + payload.Length];
        message[0] = (byte)MessageType.PluginPacket;
        message[1] = (byte)(payload.Length & 0xFF);
        message[2] = (byte)(payload.Length >> 8);
        payload.CopyTo(message.AsSpan(3));

        // One byte at a time, the worst case for the reassembly buffer.
        foreach (var b in message)
            _buffer.Append([b]);

        Assert.True(_stream.TryRead(_buffer, out var command));
        Assert.Equal(payload, command.Payload.ToArray());
    }

    [Fact]
    public void TableMatchesTheReference()
    {
        Assert.Equal(CommandLength.Fixed, ClientCommands.KindOf((byte)MessageType.InputState));
        Assert.Equal(4, ClientCommands.FixedLengthOf((byte)MessageType.InputState));

        Assert.Equal(CommandLength.Fixed, ClientCommands.KindOf((byte)MessageType.PlayerLeave));
        Assert.Equal(0, ClientCommands.FixedLengthOf((byte)MessageType.PlayerLeave));

        Assert.Equal(CommandLength.Fixed, ClientCommands.KindOf((byte)MessageType.RewardChallengeResponse));
        Assert.Equal(16, ClientCommands.FixedLengthOf((byte)MessageType.RewardChallengeResponse));

        Assert.Equal(CommandLength.Prefixed1, ClientCommands.KindOf((byte)MessageType.PlayerChangeName));
        Assert.Equal(CommandLength.Prefixed1, ClientCommands.KindOf((byte)MessageType.RewardRequest));
        Assert.Equal(CommandLength.Prefixed2, ClientCommands.KindOf((byte)MessageType.PluginPacket));

        // Server-to-client-only messages are not client commands.
        Assert.Equal(CommandLength.Invalid, ClientCommands.KindOf((byte)MessageType.QuickUpdate));
        Assert.Equal(CommandLength.Invalid, ClientCommands.KindOf((byte)MessageType.FullUpdate));
        Assert.Equal(CommandLength.Invalid, ClientCommands.KindOf((byte)MessageType.Kick));

        // PING, RESERVE_SLOT and PLAYER_JOIN belong to the handshake only: the
        // in-game command table does not carry them.
        Assert.Equal(CommandLength.Invalid, ClientCommands.KindOf((byte)MessageType.Ping));
        Assert.Equal(CommandLength.Invalid, ClientCommands.KindOf((byte)MessageType.ReserveSlot));
        Assert.Equal(CommandLength.Invalid, ClientCommands.KindOf((byte)MessageType.PlayerJoin));

        var valid = 0;
        for (var i = 0; i < 256; i++)
            if (ClientCommands.IsValid((byte)i))
                valid++;

        Assert.Equal(15, valid); // the fifteen assignments in GameServerDefineCommands.gml
    }

    private void Feed(params byte[] bytes) => _buffer.Append(bytes);
}
