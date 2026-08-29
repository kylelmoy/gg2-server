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

    /// <summary>
    /// <see cref="Step"/>, plus every candidate's score for each searching agent.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Opt-in per call, and that is the whole design.</b> The distribution
    /// is 60 doubles an agent — 120 for a sniper — against a 52-byte reward block,
    /// so carrying it in every reply would tax the random-head smoke run and move
    /// phase 0's throughput criterion, which is the one number <c>gg2-rl</c>
    /// quotes for the socket boundary. A separate kind costs nothing when unused.
    /// <para>
    /// The request body is byte-identical to <see cref="Step"/>'s. The reply is
    /// <see cref="Step"/>'s followed by the score block — see
    /// <c>EnvServer.StepScored</c>.
    /// </para>
    /// <para>
    /// ⚠️ <b>float32, not double.</b> A score is a sum of damage differences and
    /// carries exactly the precision the damage fields already do; doubling the
    /// payload to preserve digits the source does not have is not a trade.
    /// </para>
    /// </remarks>
    StepScored = 8,

    /// <summary>
    /// The candidate ACTIONS a <see cref="StepScored"/> distribution is over.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A distribution is useless without the actions it ranges over</b>, and
    /// <see cref="StepScored"/> sent only the scores. A consumer turning an index
    /// into an action had to reconstruct <c>RolloutSearch</c>'s enumeration inside
    /// its own repo — a second source of truth about this search, which is exactly
    /// what the input seam exists to prevent. ⚠️ <b>This repo made that argument
    /// about the shadow slot's argmax and did not notice it applied with equal
    /// force to the whole distribution</b>; <c>gg2-rl</c> did.
    /// <para>
    /// ⚠️ <b>Asked once, not per step.</b> The set is fixed by the batch's spec —
    /// a duel's classes do not change — so it rides its own message rather than
    /// 360 bytes an agent on every reply. That also keeps every existing reply
    /// byte-identical, which is why this needs no version bump: a client that
    /// never sends it is unaffected.
    /// </para>
    /// <para>
    /// Reply: red's count then that many actions, then blue's. Each action is six
    /// i32 — horizontal, jump, down, fire, aim, zoom — in the order
    /// <c>EnvServer.ReadAction</c> consumes them. A non-rollout side has a count
    /// of zero.
    /// </para>
    /// </remarks>
    Candidates = 9,
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
    /// <b>v6, 2026-08-29</b>: <c>MakeBatch</c> gains a rollout <b>horizon</b> per
    /// side, two i32 appended, so <see cref="MakeBatchFixedBytes"/> goes 96 to
    /// 104. 0 means the search's own default, which is <b>unchanged</b>: the knob
    /// exists so <c>gg2-rl</c> can sweep blind-fraction against horizon, and which
    /// value ships is a decision that should follow those curves rather than
    /// precede them. Reply shapes and the action encoding are untouched, so
    /// <c>SIM_UUID</c> and <see cref="ComponentBytes"/> do not move.
    /// <para>
    /// <b>v5, 2026-08-29</b>: the reward block gains <c>BestScore</c> —
    /// <b>appended</b>, so <see cref="ComponentBytes"/> goes 48 → 52 and nothing
    /// already in the block moves. It is the winning candidate's score for a
    /// <c>Rollout</c> slot and NaN otherwise, and <b>zero labels a blind
    /// decision</b>: when nothing can score inside the search's horizon every
    /// candidate ties at 0 and the tie goes to candidate 0, which encodes "turn
    /// hard left, do nothing". <c>gg2-rl</c> measured <b>35.2% of a 1,960-row
    /// distillation corpus</b> as exactly that action, rising to <b>46.2% for a
    /// sniper</b>, with every row valid and nothing about the dataset looking
    /// wrong. A consumer cannot separate those from genuine holds by value; this
    /// field is what makes them labellable.
    /// ⚠️ <b>The action encoding does NOT move</b>, so <c>SIM_UUID</c> is
    /// unchanged — this is a reply-shape change only.
    /// <para>
    /// <b>v4, 2026-08-29</b>: the action gains a sixth head, <c>Zoom</c>. That
    /// changes the wire in BOTH directions — <c>Step</c> carries <b>six</b> bytes
    /// per agent instead of five, and <see cref="ComponentBytes"/> goes 44 → 48
    /// because the reward block echoes the action taken. ⚠️ It also moves
    /// <c>SimulationStamp.ActionEncodingVersion</c> 1 → 2, so a checkpoint
    /// trained against v3 is not poolable with one trained after it; that is the
    /// whole reason it was done before <c>gg2-rl</c>'s first training run rather
    /// than after.
    /// <para>
    /// <b>v3, 2026-08-28</b>: <c>MakeBatch</c> gains four fields at the END of
    /// its body — a <c>RolloutOpponent</c> per side as i32, then a minimum and a
    /// maximum separation as f64. ⚠️ <b><c>ComponentBytes</c> does NOT move</b>,
    /// so unlike the v2 bump a client only has to lengthen what it sends; every
    /// reply it already slices is byte-identical. Both fields answer requests
    /// <c>gg2-rl</c>'s phase 1 was blocked on: reporting the search baseline
    /// under two opponent models, and sampled placements that are actually within
    /// engagement range of each other.
    /// <para>
    /// <b>v2, 2026-08-28</b>: <c>MakeBatch</c> carries a driver and a skill tier
    /// per side, and every reward block carries the action that side actually
    /// took plus whether it is reproducible. ⚠️ <b>A v1 client is refused rather
    /// than tolerated</b> — the reward block changed length, so a v1 reader would
    /// slice a v2 reply into plausible nonsense rather than failing.
    /// </para>
    /// <para>
    /// ⚠️ <b>Two bumps in a day is a cost paid by somebody else</b>, so v3
    /// carries both open asks at once rather than one now and one later. Adding
    /// a field to the tail of one message is not a reason to skip the bump: a
    /// short frame would read the new fields out of whatever followed, and the
    /// separation band's f64 would be the difference between spawn-point
    /// sampling and nav-node sampling read off uninitialised bytes.
    /// </para>
    /// </remarks>
    public const int Version = 6;

    /// <summary>
    /// Bytes of <c>MakeBatch</c> body after the map name — the fixed tail.
    /// </summary>
    /// <remarks>
    /// Two classes, two drivers, two tiers and two rollout opponents at four
    /// bytes (32); the decision cap, the action repeat, the seed and the thread
    /// count at four (16); four coordinates and two separations at eight (48);
    /// two rollout horizons at four (8).
    /// <para>
    /// ⚠️ <b>It lives beside <see cref="Version"/> so that the two cannot move
    /// apart, and that is not tidiness — it is the gate for a bug this file
    /// already shipped.</b> <c>dbe8b15</c> added v3's four fields to the body,
    /// to <c>EnvServer.MakeBatch</c>, to both clients and to
    /// <c>docs/ENV.md</c>, and left <see cref="Version"/> at 2. The handshake
    /// then refused the client that was correct and admitted the one that was
    /// stale, which failed one layer later with an error naming v2 as the thing
    /// it should be speaking — which it already was. Found by <c>gg2-rl</c>,
    /// from outside, by sending <c>Hello(3)</c>.
    /// </para>
    /// <para>
    /// ⚠️ <b>The version exists to name a mismatch at <see cref="EnvMessage.Hello"/>.</b>
    /// A frame-length check is a backstop for a mis-framed client, not a
    /// substitute for it: it fires after the handshake has already said the two
    /// sides agree. <c>EnvProtocolTests.TheVersionAndTheFrameLayoutMoveTogether</c>
    /// pins this pair, so changing the body without changing the version is a
    /// failing test rather than a silent lie.
    /// </para>
    /// </remarks>
    public const int MakeBatchFixedBytes = 32 + 16 + 48 + 8;

    /// <summary>
    /// Bytes of reward components per agent in every reply.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Fixed, and present as zeros on <c>MakeBatch</c> and <c>Reset</c>
    /// too</b>, so the reply's layout never depends on which message produced it
    /// — a variable shape here is a shape the trainer has to branch on.
    /// <para>
    /// ⚠️ <b>v4 made it twelve fields from eleven</b>, the extra one being the
    /// <c>Zoom</c> head the action gained; <b>v5 makes it thirteen</b> with
    /// <c>BestScore</c>. ⚠️ v4's head was <b>inserted</b> and moved
    /// <c>ActionExact</c> under every reader; v5's is <b>appended</b>, so a stale
    /// reader runs off the end and fails loudly instead of misreading a field.
    /// The action heads had to stay contiguous; a score does not.
    /// <para>
    /// ⚠️ <b>Twelve 4-byte fields rather than six plus six packed bytes.</b> The
    /// action heads are 0-14 and would fit in a byte each, but <c>gg2-rl</c>'s
    /// <c>CONTRACT.md</c> maps this block straight into a tensor, and a block that
    /// is 4-byte typed throughout can be viewed as one dtype. Six bytes saved per
    /// agent against a 209-float observation is not a trade worth making.
    /// </para>
    /// </para>
    /// </remarks>
    public const int ComponentBytes = 52;

    /// <summary>
    /// Bytes of action heads per agent in a <c>Step</c> request.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>One byte per head, and the count is part of <see cref="Version"/>.</b>
    /// It went 5 → 6 in v4 when the action gained <c>Zoom</c>. A client a version
    /// behind sends five, and from the second world onward every agent's action is
    /// read one byte out of step — plausible values, wrong agent. That is exactly
    /// why the handshake refuses rather than tolerates, and why this constant is
    /// asserted beside the version in <c>EnvProtocolTests</c>.
    /// </remarks>
    public const int ActionBytes = 6;

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

        /// <summary>
        /// The head widths, in the order the actions are sent.
        /// </summary>
        /// <remarks>
        /// ⚠️ <b>This is the env's RUNTIME SELF-DESCRIPTION, and a trainer sizes
        /// its policy's output layer from it.</b> v4 added the <c>Zoom</c> head to
        /// the request, to the reader, to the reward block and to
        /// <see cref="ActionBytes"/> — and left this at five. The code was
        /// entirely self-consistent: the server read six bytes and wrote six heads
        /// back while telling every client the action space had five.
        /// <para>
        /// ⚠️ <b>Nothing on this side could catch it, and the failure it causes is
        /// silent.</b> No test here asks the handshake what the action space is;
        /// every one of them builds frames from the constants directly. A trainer
        /// that believed the advertisement would build a five-head policy against
        /// a six-head space — not a crash, just <b>a sniper that can never
        /// zoom</b>, which is the exact bug v4 existed to remove. Found by
        /// <c>gg2-rl</c> on its first connection, because it asserts the
        /// advertised shape rather than assuming it.
        /// </para>
        /// <para>
        /// ⚠️ <b>Second time in two days that a self-description drifted from the
        /// thing it describes</b>, after <see cref="Version"/> itself. The gate is
        /// <c>EnvProtocolTests.TheAdvertisedHeadShapeMatchesTheWire</c>, which
        /// derives the count from <see cref="ActionBytes"/> rather than restating
        /// it — a literal here would drift the same way again.
        /// </para>
        /// </remarks>
        public required int[] HeadShape { get; init; }

        public static Handshake Current => new()
        {
            ProtocolVersion = Version,
            SimulationUuid = SimulationStamp.Uuid,
            ObservationLayoutVersion = ObservationWriter.LayoutVersion,
            ActionEncodingVersion = SimulationStamp.ActionEncodingVersion,
            ObservationLength = ObservationWriter.Length,
            // horizontal, jump, down, fire, aim, zoom — one entry per byte of
            // ActionBytes, in the order EnvServer.ReadAction consumes them.
            HeadShape = [3, 2, 2, 3, ActionDecoder.AimBins, 2],
        };
    }
}
