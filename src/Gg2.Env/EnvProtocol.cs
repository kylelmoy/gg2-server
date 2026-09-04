using Gg2.Bots;
using System.Buffers;
using System.Buffers.Binary;
using Gg2.Simulation;

namespace Gg2.Env;

/// <summary>The message kinds, request and response sharing one byte space.</summary>
/// <remarks>
/// <b>Explicit values, and they are part of the protocol version.</b>
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
    /// <b>Opt-in per call, and that is the whole design.</b> The distribution
    /// is 60 doubles an agent — 120 for a sniper — against a 52-byte reward block,
    /// so carrying it in every reply would tax the random-head smoke run and move
    /// the throughput criterion that is the one number quoted for the socket
    /// boundary. A separate kind costs nothing when unused.
    /// <para>
    /// The request body is byte-identical to <see cref="Step"/>'s. The reply is
    /// <see cref="Step"/>'s followed by the score block — see
    /// <c>EnvServer.StepScored</c>.
    /// </para>
    /// <para>
    /// <b>float32, not double.</b> A score is a sum of damage differences and
    /// carries exactly the precision the damage fields already do; doubling the
    /// payload to preserve digits the source does not have is not a trade.
    /// </para>
    /// </remarks>
    StepScored = 8,

    /// <summary>
    /// The candidate ACTIONS a <see cref="StepScored"/> distribution is over.
    /// </summary>
    /// <remarks>
    /// <b>A distribution is useless without the actions it ranges over</b>, and
    /// <see cref="StepScored"/> sends only the scores. A consumer turning an
    /// index into an action would otherwise have to reconstruct
    /// <c>RolloutSearch</c>'s enumeration inside its own repo — a second source
    /// of truth about this search, which is exactly what the input seam exists
    /// to prevent.
    /// <para>
    /// <b>Asked once, not per step.</b> The set is fixed by the batch's spec —
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

    /// <summary>
    /// Sets the macro goal a slot's observation reports. <c>gg2-rl</c> phase 4.
    /// </summary>
    /// <remarks>
    /// Body: <c>i32</c> world, <c>i32</c> side (0 red, 1 blue), <c>i32</c> kind,
    /// <c>i32</c> hasTarget, <c>f64</c> x, <c>f64</c> y. A world of <c>-1</c>
    /// means every world in the batch, which is what a trainer conditioning a
    /// whole batch on one goal actually wants and saves it 128 round trips.
    /// Reply: the ordinary observation block, so the caller sees the goal it just
    /// set reflected in the vector rather than having to step to find out.
    /// <para>
    /// <b>No version bump, on <see cref="Candidates"/>'s precedent and for its
    /// reason</b>: no existing message's body or reply changes by one byte, and a
    /// client that never sends this is unaffected.
    /// </para>
    /// <para>
    /// <b>A goal survives the auto-reset at an episode boundary</b> — see
    /// <c>DuelEnv.SetGoal</c> for why, and it is the half of this feature most
    /// likely to surprise.
    /// </para>
    /// <para>
    /// <b>An out-of-range kind is refused, not clamped.</b> The goal one-hot
    /// writes no column for one, which a policy reads as "no goal" rather than as
    /// an error.
    /// </para>
    /// </remarks>
    SetGoal = 10,

    /// <summary>
    /// <see cref="Step"/>, with the searching slots restricted to a subset of
    /// their candidates. <c>gg2-rl</c>'s <c>docs/DESIGN.md</c> step 3.
    /// </summary>
    /// <remarks>
    /// Body: <see cref="Step"/>'s actions, then for each world red-then-blue an
    /// <c>i32</c> count followed by that many <c>i32</c> candidate indices. A
    /// count of zero means "search everything", which is what every slot without
    /// a search must send.
    /// <para>
    /// <b>A separate kind rather than a tail on <see cref="Step"/>.</b>
    /// <c>EnvServer.Step</c> checks <c>body.Length &lt; needed</c>, so it already
    /// accepts and ignores trailing bytes — a new client sending restrictions to
    /// an old server would be silently unrestricted and would measure "pruning
    /// changes nothing". A kind the old server does not know is refused loudly
    /// instead.
    /// </para>
    /// <para>
    /// <b>Rides on the step rather than being set beforehand</b>, because the
    /// restriction changes every decision and a separate message would double the
    /// round trips — and round trips are what a trainer actually waits on. See
    /// <c>docs/ENV.md</c>, "the boundary is latency, not throughput".
    /// </para>
    /// <para>
    /// <b>The indices are a set.</b> Nothing here may assume they are ordered
    /// or that the first is anybody's favourite: a client may send top-(k−m) plus
    /// m sampled from the pruned remainder, because a search that only evaluates
    /// what the policy already likes cannot correct the policy where it is wrong.
    /// <see cref="Gg2.Bots.RolloutSearch.RestrictNextDecision"/>.
    /// </para>
    /// </remarks>
    StepRestricted = 11,

    /// <summary>
    /// <see cref="StepRestricted"/> plus the score block, which for a restricted
    /// search carries <c>(index, score)</c> PAIRS.
    /// </summary>
    /// <remarks>
    /// <b>Pairs, not a full-length block with a sentinel for the unsearched.</b>
    /// Zero is already a real score — the blind case — and a sentinel is a number
    /// sitting in a numeric array that somebody will eventually sum, average or
    /// softmax. The pairs are self-describing about which actions were looked at,
    /// which is exactly the argument <see cref="Candidates"/> makes one level up.
    /// <para>
    /// Reply: <see cref="Step"/>'s, then per agent an <c>i32</c> count, that many
    /// <c>(i32 index, f32 score)</c> pairs, then the chosen action as six
    /// <c>i32</c>. An unrestricted slot reports indices 0..n−1, so the shape does
    /// not depend on whether anybody pruned.
    /// </para>
    /// </remarks>
    StepScoredRestricted = 12,

    /// <summary>
    /// Loads a policy checkpoint into the server and returns a handle for it.
    /// </summary>
    /// <remarks>
    /// Body: <c>u32</c> path length, then the path. Reply: <c>i32</c> handle,
    /// then <c>u32</c> length and the <c>SIM_UUID</c> the checkpoint was checked
    /// against. <b>A stamp mismatch is an <see cref="Error"/>, not a warning</b>
    /// — <c>SimulationStamp.Require</c> throws, which is the whole point of that
    /// type: a policy trained against physics that has since changed plays
    /// slightly worse for ever with the cause invisible.
    /// <para>
    /// <b>Handles start at 1, because 0 means "no continuation"</b> in
    /// <c>MakeBatch</c>'s per-side field. A handle table that started at 0 would
    /// make the first policy loaded indistinguishable from none — the same
    /// "nobody is 255, never 0" trap <c>WorldState</c> records, one layer out.
    /// </para>
    /// <para>
    /// <b>This is the first thing in this protocol that needs weights in the
    /// server.</b> <c>EnvDriver</c> deliberately does not have a policy member,
    /// because a past checkpoint is <c>Client</c>: the trainer holds the weights
    /// and drives that slot itself. A rollout continuation cannot work that way —
    /// it runs inside the search's fork, k times per candidate per decision, in
    /// worlds the socket never sees. That is why this exists and why it is not a
    /// general plug-in seam.
    /// </para>
    /// </remarks>
    LoadPolicy = 13,

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
/// <b>The length prefix does not include itself.</b> Stating it because the
/// other convention is equally common and the two are indistinguishable until a
/// frame happens to be exactly four bytes longer than it should be.
/// </para>
/// </remarks>
public static class EnvProtocol
{
    /// <summary>
    /// <b>Bumped on any change to the framing or the message set.</b> A
    /// mismatch is refused at <see cref="EnvMessage.Hello"/> — the
    /// <c>PROTOCOL_UUID</c> idiom, not a warning.
    /// </summary>
    /// <remarks>
    /// <b>v8</b>: <c>MakeBatch</c> gains a <b>commitment length</b> and a
    /// <b>continuation policy handle</b> per side, four i32 appended
    /// (<see cref="MakeBatchFixedBytes"/> 112 → 128), and there is a new message
    /// <see cref="EnvMessage.LoadPolicy"/>. Together they let the rollout search
    /// keep acting past its commitment under a trained policy instead of going
    /// neutral: a long horizon sees damage land and picks an implausible plan, a
    /// neutral continuation picks a plausible plan and sees nothing land.
    /// <b>Per side</b>, because the criterion is the new search against the old
    /// one head-to-head in one world, rather than each against a scripted tier.
    /// Reply shapes for every other message and the action encoding are untouched,
    /// and <c>SIM_UUID</c> does not move: it is deliberately blind to the bot
    /// layer, and a continuation is a bot decision.
    /// <para>
    /// <b>v7</b>: <c>MakeBatch</c> gains a <b>shadow search</b> flag per side,
    /// two i32 appended (<see cref="MakeBatchFixedBytes"/> 104 → 112), and every
    /// <see cref="EnvMessage.StepScored"/> per-agent block gains the action the
    /// search chose — six i32 after the distribution. That second part changes a
    /// message already in use, which is why this is a bump rather than a new
    /// kind. The chosen action is not derivable from the scores: a median of
    /// four candidates share the best, and the search picks among them on aim
    /// error. Reply shapes for every other message, and the action encoding, are
    /// untouched — <c>SIM_UUID</c> does not move.
    /// <para>
    /// <b>v6</b>: <c>MakeBatch</c> gains a rollout <b>horizon</b> per side, two
    /// i32 appended, so <see cref="MakeBatchFixedBytes"/> goes 96 to 104. 0
    /// means the search's own default, which is unchanged. Reply shapes and the
    /// action encoding are untouched, so <c>SIM_UUID</c> and
    /// <see cref="ComponentBytes"/> do not move.
    /// <para>
    /// <b>v5</b>: the reward block gains <c>BestScore</c> — <b>appended</b>, so
    /// <see cref="ComponentBytes"/> goes 48 → 52 and nothing already in the
    /// block moves. It is the winning candidate's score for a <c>Rollout</c>
    /// slot and NaN otherwise, and <b>zero labels a blind decision</b>: when
    /// nothing can score inside the search's horizon every candidate ties at 0
    /// and the tie goes to candidate 0, which encodes "turn hard left, do
    /// nothing". A consumer cannot separate that from a genuine hold by value
    /// alone; this field is what makes them labellable.
    /// <b>The action encoding does not move</b>, so <c>SIM_UUID</c> is
    /// unchanged — this is a reply-shape change only.
    /// <para>
    /// <b>v4</b>: the action gains a sixth head, <c>Zoom</c>. That changes the
    /// wire in both directions — <c>Step</c> carries <b>six</b> bytes per agent
    /// instead of five, and <see cref="ComponentBytes"/> goes 44 → 48 because
    /// the reward block echoes the action taken. It also moves
    /// <c>SimulationStamp.ActionEncodingVersion</c> 1 → 2, so a checkpoint
    /// trained against v3 is not poolable with one trained after it.
    /// <para>
    /// <b>v3</b>: <c>MakeBatch</c> gains four fields at the end of its body — a
    /// <c>RolloutOpponent</c> per side as i32, then a minimum and a maximum
    /// separation as f64. <b><c>ComponentBytes</c> does not move</b>, so unlike
    /// the v2 bump a client only has to lengthen what it sends; every reply it
    /// already slices is byte-identical. Both fields answer: reporting the
    /// search baseline under two opponent models, and sampled placements that
    /// are actually within engagement range of each other.
    /// <para>
    /// <b>v2</b>: <c>MakeBatch</c> carries a driver and a skill tier per side,
    /// and every reward block carries the action that side actually took plus
    /// whether it is reproducible. <b>A v1 client is refused rather than
    /// tolerated</b> — the reward block changed length, so a v1 reader would
    /// slice a v2 reply into plausible nonsense rather than failing.
    /// </para>
    /// <para>
    /// Adding a field to the tail of one message is not a reason to skip the
    /// version bump: a short frame would read the new fields out of whatever
    /// followed, and the separation band's f64 would be the difference between
    /// spawn-point sampling and nav-node sampling read off uninitialised bytes.
    /// </para>
    /// </remarks>
    public const int Version = 8;

    /// <summary>
    /// Bytes of <c>MakeBatch</c> body after the map name — the fixed tail.
    /// </summary>
    /// <remarks>
    /// Two classes, two drivers, two tiers and two rollout opponents at four
    /// bytes (32); the decision cap, the action repeat, the seed and the thread
    /// count at four (16); four coordinates and two separations at eight (48);
    /// two rollout horizons and two shadow flags at four (16); two commitment
    /// lengths and two continuation handles at four (16).
    /// <para>
    /// <b>It lives beside <see cref="Version"/> so that the two cannot move
    /// apart, and that is not tidiness — it is the gate for a real bug class.</b>
    /// If a body field is added to <c>EnvServer.MakeBatch</c>, to both clients
    /// and to <c>docs/ENV.md</c> without bumping <see cref="Version"/>, the
    /// handshake refuses the client that is correct and admits the one that is
    /// stale, which fails one layer later with an error naming the version it
    /// should already be speaking.
    /// </para>
    /// <para>
    /// <b>The version exists to name a mismatch at <see cref="EnvMessage.Hello"/>.</b>
    /// A frame-length check is a backstop for a mis-framed client, not a
    /// substitute for it: it fires after the handshake has already said the two
    /// sides agree. <c>EnvProtocolTests.TheVersionAndTheFrameLayoutMoveTogether</c>
    /// pins this pair, so changing the body without changing the version is a
    /// failing test rather than a silent lie.
    /// </para>
    /// </remarks>
    public const int MakeBatchFixedBytes = 32 + 16 + 48 + 8 + 8 + 16;

    /// <summary>
    /// Bytes of reward components per agent in every reply.
    /// </summary>
    /// <remarks>
    /// <b>Fixed, and present as zeros on <c>MakeBatch</c> and <c>Reset</c>
    /// too</b>, so the reply's layout never depends on which message produced it
    /// — a variable shape here is a shape the trainer has to branch on.
    /// <para>
    /// <b>v4 made it twelve fields from eleven</b>, the extra one being the
    /// <c>Zoom</c> head the action gained; <b>v5 makes it thirteen</b> with
    /// <c>BestScore</c>. v4's head was <b>inserted</b> and moved
    /// <c>ActionExact</c> under every reader; v5's is <b>appended</b>, so a stale
    /// reader runs off the end and fails loudly instead of misreading a field.
    /// The action heads had to stay contiguous; a score does not.
    /// <para>
    /// <b>Twelve 4-byte fields rather than six plus six packed bytes.</b> The
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
    /// <b>One byte per head, and the count is part of <see cref="Version"/>.</b>
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
    /// <b>A length prefix read off a socket is attacker-controlled input even
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
    /// <remarks>
    /// <b>One write, not a header write followed by a body write</b>, and the
    /// difference is measured rather than tidiness. Two writes put a 4-byte
    /// segment on the wire ahead of the body, which is the textbook shape Nagle
    /// holds back waiting for an ACK: at batch 1, <b>12 round trips in 20,000
    /// stalled past 10 ms and the worst was 25.3 ms</b>. A trainer stepping
    /// workers in lockstep pays the worst of them on every step.
    /// <para>
    /// Coalescing removed the stalls outright and took the mean round trip from
    /// <b>76.9 to 54.4 µs</b> at batch 1 — the extra copy is cheaper than the
    /// syscall it saves. <c>docs/ENV.md</c>, "the boundary is latency, not
    /// throughput". <see cref="EnvServer"/> also sets <c>NoDelay</c>, which is
    /// the belt to this braces; each helps on its own and neither replaces the
    /// other.
    /// </para>
    /// <para>
    /// <b>Rented, not allocated.</b> A reply is 107 KB at batch 64 and this is
    /// on the per-step path, so a <c>new byte[]</c> here would trade a syscall
    /// for GC pressure and land the big ones on the large-object heap.
    /// </para>
    /// </remarks>
    public static void WriteFrame(Stream stream, ReadOnlySpan<byte> body)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var frame = ArrayPool<byte>.Shared.Rent(4 + body.Length);

        try
        {
            BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)body.Length);
            body.CopyTo(frame.AsSpan(4));

            stream.Write(frame.AsSpan(0, 4 + body.Length));
            stream.Flush();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(frame);
        }
    }

    /// <summary>
    /// <b>A socket read returns what has arrived, not what was asked for.</b>
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
    /// Everything a client must agree with before it sends anything else. The
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
        /// <b>This is the env's runtime self-description, and a trainer sizes
        /// its policy's output layer from it.</b> If the wire gains a head (as
        /// v4 did for <c>Zoom</c>, in the request, the reader, the reward block
        /// and <see cref="ActionBytes"/>) but this is left unchanged, the code
        /// stays entirely self-consistent — the server reads and writes the new
        /// head count while telling every client the action space is one head
        /// short.
        /// <para>
        /// <b>Nothing on this side can catch that on its own, and the failure it
        /// causes is silent.</b> No test here asks the handshake what the action
        /// space is; every one of them builds frames from the constants
        /// directly. A trainer that believed the advertisement would build a
        /// policy one head short of the real space — not a crash, just a class
        /// that can never use its extra head.
        /// </para>
        /// <para>
        /// The gate is
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
            // From ActionDecoder, not restated. A policy in Gg2.Bots has to
            // agree with this shape and cannot reference this assembly, so the
            // definition lives there and this reads it.
            HeadShape = ActionDecoder.HeadShape.ToArray(),
        };
    }
}
