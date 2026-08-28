using Gg2.Bots;
using System.Buffers.Binary;
using Gg2.Simulation;

namespace Gg2.Env;

/// <summary>The message kinds, request and response sharing one byte space.</summary>
/// <remarks>
/// ⚠️ <b>Explicit values, and they are part of the protocol version.</b>
/// Reordering this enum renumbers the wire, exactly as reordering the
/// reference's damage sources does — see <c>docs/PROTOCOL.md</c>.
/// </remarks>
public enum EnvMessage : byte
{
    Hello = 1,
    MakeBatch = 2,
    Step = 3,
    Reset = 4,
    Snapshot = 5,
    Restore = 6,
    Close = 7,

    /// <summary>A refusal, carrying a reason string. Never a silent failure.</summary>
    Error = 255,
}

/// <summary>
/// The framing: <c>u32</c> little-endian length, then the body.
/// </summary>
/// <remarks>
/// <b><c>docs/ENV.md</c>'s protocol v0.</b> A local socket with
/// length-prefixed binary frames, request and response. Shared memory is an
/// optimisation to make once there is a throughput number that justifies it —
/// and this project's rule is that the number comes first.
/// <para>
/// ⚠️ <b>The length prefix does NOT include itself.</b> Stating it because the
/// other convention is equally common and the two are indistinguishable until a
/// frame happens to be exactly four bytes longer than it should be.
/// </para>
/// </remarks>
public static class EnvProtocol
{
    /// <summary>
    /// ⚠️ <b>Bumped on any change to the framing or the message set.</b> A
    /// mismatch is refused at <see cref="EnvMessage.Hello"/> — the
    /// <c>PROTOCOL_UUID</c> idiom, not a warning.
    /// </summary>
    /// <remarks>
    /// <b>v2, 2026-08-28</b>: <c>MakeBatch</c> carries a driver and a skill tier
    /// per side, and every reward block carries the action that side actually
    /// took plus whether it is reproducible. ⚠️ <b>A v1 client is refused rather
    /// than tolerated</b> — the reward block changed length, so a v1 reader would
    /// slice a v2 reply into plausible nonsense rather than failing.
    /// </remarks>
    public const int Version = 2;

    /// <summary>
    /// Bytes of reward components per agent in every reply.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Fixed, and present as zeros on <c>MakeBatch</c> and <c>Reset</c>
    /// too</b>, so the reply's layout never depends on which message produced it
    /// — a variable shape here is a shape the trainer has to branch on.
    /// <para>
    /// ⚠️ <b>Eleven 4-byte fields rather than five plus six packed bytes.</b> The
    /// action heads are 0-14 and would fit in a byte each, but <c>gg2-rl</c>'s
    /// <c>CONTRACT.md</c> maps this block straight into a tensor, and a block that
    /// is 4-byte typed throughout can be viewed as one dtype. Six bytes saved per
    /// agent against a 209-float observation is not a trade worth making.
    /// </para>
    /// </remarks>
    public const int ComponentBytes = 44;

    /// <summary>
    /// A frame larger than this is refused rather than allocated.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A length prefix read off a socket is attacker-controlled input even
    /// on loopback</b> — a garbled or mis-framed client sends a four-byte length
    /// that means nothing, and `new byte[thatLength]` is how a mis-frame becomes
    /// an out-of-memory instead of an error message. The cap is generous: a
    /// batch-64 observation block is a few hundred kilobytes.
    /// </remarks>
    public const int MaxFrameBytes = 64 * 1024 * 1024;

    /// <summary>Reads one frame's body, or null at a clean end of stream.</summary>
    public static byte[]? ReadFrame(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        Span<byte> header = stackalloc byte[4];

        if (!ReadExactly(stream, header))
            return null;

        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);

        if (length > MaxFrameBytes)
            throw new InvalidDataException($"frame of {length} bytes is past the {MaxFrameBytes} cap");

        var body = new byte[length];

        if (!ReadExactly(stream, body))
            throw new EndOfStreamException("the stream ended part way through a frame");

        return body;
    }

    /// <summary>Writes one frame.</summary>
    public static void WriteFrame(Stream stream, ReadOnlySpan<byte> body)
    {
        ArgumentNullException.ThrowIfNull(stream);

        Span<byte> header = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)body.Length);

        stream.Write(header);
        stream.Write(body);
        stream.Flush();
    }

    /// <summary>
    /// ⚠️ <b>A socket read returns what has arrived, not what was asked for.</b>
    /// Treating one <c>Read</c> as a whole frame works on loopback until the
    /// frame is bigger than a buffer somewhere, and then fails as a corrupt
    /// message rather than as a short read.
    /// </summary>
    private static bool ReadExactly(Stream stream, Span<byte> into)
    {
        var got = 0;

        while (got < into.Length)
        {
            var read = stream.Read(into[got..]);

            if (read == 0)
                return got == 0 ? false : throw new EndOfStreamException("stream ended mid-frame");

            got += read;
        }

        return true;
    }

    /// <summary>What a <see cref="EnvMessage.Hello"/> is answered with.</summary>
    /// <remarks>
    /// Everything a client must agree with before it sends anything else. ⚠️ The
    /// simulation stamp is in here because a policy trained against different
    /// physics is not loadable, and a version handshake that checked only the
    /// framing would let that through.
    /// </remarks>
    public readonly record struct Handshake
    {
        public required int ProtocolVersion { get; init; }

        public required string SimulationUuid { get; init; }

        public required int ObservationLayoutVersion { get; init; }

        public required int ActionEncodingVersion { get; init; }

        public required int ObservationLength { get; init; }

        /// <summary>The five head widths, in the order the actions are sent.</summary>
        public required int[] HeadShape { get; init; }

        public static Handshake Current => new()
        {
            ProtocolVersion = Version,
            SimulationUuid = SimulationStamp.Uuid,
            ObservationLayoutVersion = ObservationWriter.LayoutVersion,
            ActionEncodingVersion = SimulationStamp.ActionEncodingVersion,
            ObservationLength = ObservationWriter.Length,
            HeadShape = [3, 2, 2, 3, ActionDecoder.AimBins],
        };
    }
}
