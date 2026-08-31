using Gg2.Bots;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Net;
using System.Net.Sockets;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Env;

/// <summary>
/// The loopback socket host: one client, request and response.
/// </summary>
/// <remarks>
/// <b><c>docs/ENV.md</c> step 5.</b> Loopback only and one client at a time,
/// like the control port — it executes against live worlds, and a surface that
/// accepted a second client would need a policy for what "the batch" then meant.
/// <para>
/// ⚠️ <b>The <see cref="EnvMessage.Hello"/> REFUSES a mismatch rather than
/// warning.</b> The <c>PROTOCOL_UUID</c> idiom this project already uses: a
/// trainer running against a different observation layout, action encoding or
/// simulation stamp is not slightly wrong, it is collecting data that cannot be
/// used, and it should find out in the first millisecond rather than after an
/// hour of rollouts.
/// </para>
/// </remarks>
public sealed class EnvServer(string mapDirectory, string? navDirectory = null)
{
    private EnvBatch? _batch;
    private float[] _observations = [];

    /// <summary>The port actually bound, which a 0 request resolves to.</summary>
    public int Port { get; private set; }

    /// <summary>
    /// Binds, accepts one client, and serves it until it closes.
    /// </summary>
    /// <param name="bound">
    /// Called with the port actually bound, after the listener starts and
    /// before it blocks accepting.
    /// </param>
    /// <remarks>
    /// ⚠️ <b><see cref="IPAddress.Loopback"/>, never <c>Any</c>.</b> This
    /// executes arbitrary batch commands against live simulation state; the
    /// control port made the same choice for the same reason.
    /// <para>
    /// ⚠️ <b><paramref name="bound"/> exists because <see cref="Port"/> is
    /// unreadable in time.</b> It is assigned here and then this method blocks
    /// in <c>AcceptTcpClient</c>, so a caller on the same thread cannot observe
    /// it until after the client has already been and gone. `--port 0` printed
    /// the literal 0 for exactly that reason, which made the OS-assigned port
    /// the tool's own `--help` promises unobtainable by any launcher.
    /// </para>
    /// </remarks>
    public void Serve(int port, CancellationToken cancellation = default, Action<int>? bound = null)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);

        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        bound?.Invoke(Port);

        try
        {
            using var client = listener.AcceptTcpClient();
            using var stream = client.GetStream();

            ServeOne(stream, cancellation);
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// Serves one already-connected stream. Exposed so a test can drive the real
    /// message loop without a socket in the way.
    /// </summary>
    public void ServeOne(Stream stream, CancellationToken cancellation = default)
    {
        while (!cancellation.IsCancellationRequested)
        {
            var frame = EnvProtocol.ReadFrame(stream);

            if (frame is null || frame.Length == 0)
                return;

            var kind = (EnvMessage)frame[0];
            var body = frame.AsSpan(1);

            byte[] reply;

            try
            {
                if (kind == EnvMessage.Close)
                {
                    EnvProtocol.WriteFrame(stream, [(byte)EnvMessage.Close]);
                    return;
                }

                reply = Handle(kind, body);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or InvalidDataException)
            {
                // ⚠️ A refusal is a message, never a dropped connection. A
                // trainer that saw the socket close would report "the env died"
                // and the reason would be in nobody's log.
                reply = Error(e.Message);
            }

            EnvProtocol.WriteFrame(stream, reply);
        }
    }

    private byte[] Handle(EnvMessage kind, ReadOnlySpan<byte> body) => kind switch
    {
        EnvMessage.Hello => Hello(body),
        EnvMessage.MakeBatch => MakeBatch(body),
        EnvMessage.Step => Step(body),
        EnvMessage.StepScored => Step(body, scored: true),
        EnvMessage.Candidates => Candidates(),
        EnvMessage.Reset => Reset(body),
        EnvMessage.Snapshot => Snapshot(body),
        EnvMessage.Restore => Restore(body),
        _ => Error($"unknown message {(byte)kind}"),
    };

    private static byte[] Error(string reason)
    {
        var text = System.Text.Encoding.UTF8.GetBytes(reason);
        var reply = new byte[1 + 4 + text.Length];

        reply[0] = (byte)EnvMessage.Error;
        BinaryPrimitives.WriteInt32LittleEndian(reply.AsSpan(1), text.Length);
        text.CopyTo(reply.AsSpan(5));

        return reply;
    }

    private static byte[] Hello(ReadOnlySpan<byte> body)
    {
        if (body.Length < 4)
            throw new InvalidDataException("Hello carries the client's protocol version");

        var theirs = BinaryPrimitives.ReadInt32LittleEndian(body);

        if (theirs != EnvProtocol.Version)
        {
            throw new InvalidDataException(
                $"protocol version {theirs} does not match this build's {EnvProtocol.Version}");
        }

        var shake = EnvProtocol.Handshake.Current;
        var uuid = System.Text.Encoding.UTF8.GetBytes(shake.SimulationUuid);

        var reply = new byte[1 + (4 * 4) + 4 + uuid.Length + 4 + (shake.HeadShape.Length * 4)];
        var at = 0;

        reply[at++] = (byte)EnvMessage.Hello;

        void Int(int value)
        {
            BinaryPrimitives.WriteInt32LittleEndian(reply.AsSpan(at), value);
            at += 4;
        }

        Int(shake.ProtocolVersion);
        Int(shake.ObservationLayoutVersion);
        Int(shake.ActionEncodingVersion);
        Int(shake.ObservationLength);
        Int(uuid.Length);

        uuid.CopyTo(reply.AsSpan(at));
        at += uuid.Length;

        Int(shake.HeadShape.Length);

        foreach (var head in shake.HeadShape)
            Int(head);

        return reply;
    }

    private byte[] MakeBatch(ReadOnlySpan<byte> body)
    {
        // count, mapNameLength, mapName, redClass, blueClass, redX, redY,
        // blueX, blueY, decisionCap, actionRepeat, seed, threads,
        // redDriver, blueDriver, redTier, blueTier,
        // redRolloutOpponent, blueRolloutOpponent, minSeparation, maxSeparation,
        // redHorizonTicks, blueHorizonTicks, redShadowSearch, blueShadowSearch.
        //
        // ⚠️ Every field is REQUIRED rather than optional-if-absent. Tolerating a
        // short frame would let an older client through the Hello check it
        // already failed, and it would then read the tail fields out of whatever
        // followed — for v3 that is the separation band, where a stray non-zero
        // f64 silently moves placement from spawn points to nav nodes.
        //
        // ⚠️ Checked as a whole up front rather than per read, because a
        // ReadOnlySpan slice past the end throws an ArgumentOutOfRangeException
        // that says nothing about the protocol.
        //
        // ⚠️ Read with an explicit cursor rather than local helper functions: a
        // `ReadOnlySpan<byte>` is a ref-like type and cannot be captured by a
        // lambda or a local function at all. The compiler says so, which is the
        // good outcome — the alternative design copies the frame to an array
        // first and allocates on every message.
        var at = 0;

        var count = BinaryPrimitives.ReadInt32LittleEndian(body[at..]);
        at += 4;

        var nameLength = BinaryPrimitives.ReadInt32LittleEndian(body[at..]);
        at += 4;

        if (nameLength is < 1 or > 64 || body.Length < at + nameLength)
            throw new InvalidDataException("MakeBatch's map name is missing or absurd");

        var map = System.Text.Encoding.UTF8.GetString(body.Slice(at, nameLength));
        at += nameLength;

        // ⚠️ The fixed tail, checked as one number rather than per read, and the
        // number lives on EnvProtocol beside the version — see
        // EnvProtocol.MakeBatchFixedBytes for why those two are kept together.
        // This check is the backstop for a mis-framed client; the VERSION is what
        // names a mismatch, and it names it at Hello.
        if (body.Length < at + EnvProtocol.MakeBatchFixedBytes)
        {
            throw new InvalidDataException(
                $"MakeBatch wants {at + EnvProtocol.MakeBatchFixedBytes} bytes and got {body.Length}; "
                + $"this build speaks protocol v{EnvProtocol.Version}");
        }

        var redClass = (PlayerClass)BinaryPrimitives.ReadInt32LittleEndian(body[at..]);
        at += 4;
        var blueClass = (PlayerClass)BinaryPrimitives.ReadInt32LittleEndian(body[at..]);
        at += 4;

        var redX = BinaryPrimitives.ReadDoubleLittleEndian(body[at..]);
        at += 8;
        var redY = BinaryPrimitives.ReadDoubleLittleEndian(body[at..]);
        at += 8;
        var blueX = BinaryPrimitives.ReadDoubleLittleEndian(body[at..]);
        at += 8;
        var blueY = BinaryPrimitives.ReadDoubleLittleEndian(body[at..]);
        at += 8;

        var decisionCap = BinaryPrimitives.ReadInt32LittleEndian(body[at..]);
        at += 4;
        var actionRepeat = BinaryPrimitives.ReadInt32LittleEndian(body[at..]);
        at += 4;
        var seed = (uint)BinaryPrimitives.ReadInt32LittleEndian(body[at..]);
        at += 4;
        var threads = BinaryPrimitives.ReadInt32LittleEndian(body[at..]);
        at += 4;

        var redDriver = (EnvDriver)BinaryPrimitives.ReadInt32LittleEndian(body[at..]);
        at += 4;
        var blueDriver = (EnvDriver)BinaryPrimitives.ReadInt32LittleEndian(body[at..]);
        at += 4;
        var redTier = BinaryPrimitives.ReadInt32LittleEndian(body[at..]);
        at += 4;
        var blueTier = BinaryPrimitives.ReadInt32LittleEndian(body[at..]);
        at += 4;

        var redRollout = (RolloutOpponent)BinaryPrimitives.ReadInt32LittleEndian(body[at..]);
        at += 4;
        var blueRollout = (RolloutOpponent)BinaryPrimitives.ReadInt32LittleEndian(body[at..]);
        at += 4;
        var minSeparation = BinaryPrimitives.ReadDoubleLittleEndian(body[at..]);
        at += 8;
        var maxSeparation = BinaryPrimitives.ReadDoubleLittleEndian(body[at..]);
        at += 8;

        var redHorizon = BinaryPrimitives.ReadInt32LittleEndian(body[at..]);
        at += 4;
        var blueHorizon = BinaryPrimitives.ReadInt32LittleEndian(body[at..]);
        at += 4;

        var redShadow = BinaryPrimitives.ReadInt32LittleEndian(body[at..]) != 0;
        at += 4;
        var blueShadow = BinaryPrimitives.ReadInt32LittleEndian(body[at..]) != 0;

        var spec = new DuelSpec
        {
            Map = map,
            RedClass = redClass,
            BlueClass = blueClass,
            RedX = redX,
            RedY = redY,
            BlueX = blueX,
            BlueY = blueY,
            DecisionCap = decisionCap,
            ActionRepeat = actionRepeat,
            RedDriver = redDriver,
            BlueDriver = blueDriver,
            RedTier = redTier,
            BlueTier = blueTier,
            RedRolloutOpponent = redRollout,
            BlueRolloutOpponent = blueRollout,
            MinSeparation = minSeparation,
            MaxSeparation = maxSeparation,
            RedHorizonTicks = redHorizon,
            BlueHorizonTicks = blueHorizon,
            RedShadowSearch = redShadow,
            BlueShadowSearch = blueShadow,
        };

        // ⚠️ Validated HERE as well as in Reset, so a malformed driver is refused
        // at the protocol edge with a reason on the wire rather than thrown from
        // inside a batch construction the client cannot see into.
        spec.Validate();

        _batch = new EnvBatch(count, spec, seed, mapDirectory, navDirectory, threads);
        _observations = new float[count * 2 * ObservationWriter.Length];

        return Observations(EnvMessage.MakeBatch);
    }

    private byte[] Step(ReadOnlySpan<byte> body, bool scored = false)
    {
        var batch = _batch ?? throw new InvalidOperationException("no batch; send MakeBatch first");

        // ⚠️ SIX head bytes per agent since v4, red then blue, per world — the
        // sixth is Zoom. A v3 client sending five would be read one agent out of
        // step from the second world onward, which is why the head count is part
        // of the version rather than something to tolerate.
        var needed = batch.Count * 2 * EnvProtocol.ActionBytes;

        if (body.Length < needed)
            throw new InvalidDataException($"Step wants {needed} action bytes, got {body.Length}");

        var red = new EnvAction[batch.Count];
        var blue = new EnvAction[batch.Count];

        for (var i = 0; i < batch.Count; i++)
        {
            var pair = i * 2 * EnvProtocol.ActionBytes;

            red[i] = ReadAction(body.Slice(pair, EnvProtocol.ActionBytes));
            blue[i] = ReadAction(body.Slice(pair + EnvProtocol.ActionBytes, EnvProtocol.ActionBytes));
        }

        var redOut = new StepResult[batch.Count];
        var blueOut = new StepResult[batch.Count];

        batch.Step(red, blue, redOut, blueOut);

        // ⚠️ The reply KIND echoes what was asked for, so a client can tell which
        // shape it is holding without tracking its own request.
        return scored
            ? Observations(EnvMessage.StepScored, redOut, blueOut)
            : Observations(EnvMessage.Step, redOut, blueOut);
    }

    private static EnvAction ReadAction(ReadOnlySpan<byte> b) => new()
    {
        Horizontal = b[0],
        Jump = b[1],
        Down = b[2],
        Fire = b[3],
        AimDelta = b[4],

        // ⚠️ v4's sixth head. A LEVEL — 0 unscoped, 1 scoped — not a toggle; the
        // edge is ActionDecoder.WantsZoomToggle's, so a client that keeps sending
        // 1 stays scoped rather than flickering.
        Zoom = b[5],
    };

    private byte[] Reset(ReadOnlySpan<byte> body)
    {
        var batch = _batch ?? throw new InvalidOperationException("no batch; send MakeBatch first");

        if (body.Length < 4)
            throw new InvalidDataException("Reset carries a world index");

        var index = BinaryPrimitives.ReadInt32LittleEndian(body);

        if ((uint)index >= (uint)batch.Count)
            throw new ArgumentOutOfRangeException(nameof(body), index, "no such world");

        // ⚠️ Through the batch, which owns the spec. Calling
        // `World(index).Reset(default, ...)` compiles and resets the world to an
        // EMPTY spec — no map, no classes — which throws somewhere else entirely.
        // The first draft of this did exactly that.
        batch.ResetWorld(index);

        return Observations(EnvMessage.Reset);
    }

    /// <summary>
    /// The candidate actions a <see cref="EnvMessage.StepScored"/> distribution is
    /// aligned with — red's set, then blue's.
    /// </summary>
    /// <remarks>
    /// ⚠️ Answered from world 0's spec, which every world in the batch shares, so
    /// this is a property of the batch rather than of a step. A non-rollout side
    /// reports a count of zero — the same explicit zero <c>WriteScores</c> uses,
    /// and for the same reason: "this side does not search" must not be spelled
    /// like anything else.
    /// </remarks>
    private byte[] Candidates()
    {
        var batch = _batch ?? throw new InvalidOperationException("no batch; send MakeBatch first");
        var world = batch.World(0);

        var red = world.CandidatesFor(world.RedSlot);
        var blue = world.CandidatesFor(world.BlueSlot);

        var reply = new byte[1 + 4 + (red.Length * 6 * 4) + 4 + (blue.Length * 6 * 4)];
        var at = 0;

        reply[at++] = (byte)EnvMessage.Candidates;

        WriteCandidates(reply, ref at, red);
        WriteCandidates(reply, ref at, blue);

        return reply;
    }

    /// <summary>One side's candidate set: a count, then six i32 per action.</summary>
    /// <remarks>
    /// ⚠️ Six i32 rather than six bytes, matching how the reward block already
    /// reports an action, so a consumer parses the same shape in both places.
    /// </remarks>
    private static void WriteCandidates(byte[] into, ref int at, ReadOnlySpan<EnvAction> candidates)
    {
        BinaryPrimitives.WriteInt32LittleEndian(into.AsSpan(at), candidates.Length);
        at += 4;

        foreach (var action in candidates)
        {
            BinaryPrimitives.WriteInt32LittleEndian(into.AsSpan(at), action.Horizontal);
            at += 4;
            BinaryPrimitives.WriteInt32LittleEndian(into.AsSpan(at), action.Jump);
            at += 4;
            BinaryPrimitives.WriteInt32LittleEndian(into.AsSpan(at), action.Down);
            at += 4;
            BinaryPrimitives.WriteInt32LittleEndian(into.AsSpan(at), action.Fire);
            at += 4;
            BinaryPrimitives.WriteInt32LittleEndian(into.AsSpan(at), action.AimDelta);
            at += 4;
            BinaryPrimitives.WriteInt32LittleEndian(into.AsSpan(at), action.Zoom);
            at += 4;
        }
    }

    private byte[] Snapshot(ReadOnlySpan<byte> body)
    {
        var batch = _batch ?? throw new InvalidOperationException("no batch; send MakeBatch first");
        var index = BinaryPrimitives.ReadInt32LittleEndian(body);

        if ((uint)index >= (uint)batch.Count)
            throw new ArgumentOutOfRangeException(nameof(body), index, "no such world");

        var bytes = batch.World(index).Snapshot();
        var reply = new byte[1 + 4 + bytes.Length];

        reply[0] = (byte)EnvMessage.Snapshot;
        BinaryPrimitives.WriteInt32LittleEndian(reply.AsSpan(1), bytes.Length);
        bytes.CopyTo(reply.AsSpan(5));

        return reply;
    }

    private byte[] Restore(ReadOnlySpan<byte> body)
    {
        var batch = _batch ?? throw new InvalidOperationException("no batch; send MakeBatch first");
        var index = BinaryPrimitives.ReadInt32LittleEndian(body);

        if ((uint)index >= (uint)batch.Count)
            throw new ArgumentOutOfRangeException(nameof(body), index, "no such world");

        var length = BinaryPrimitives.ReadInt32LittleEndian(body[4..]);

        batch.World(index).Restore(body.Slice(8, length));

        return [(byte)EnvMessage.Restore];
    }

    /// <summary>
    /// The observation block for every world, red then blue, plus the reward
    /// components when there are any.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>One contiguous block</b>, which is <c>gg2-rl</c>'s
    /// <c>CONTRACT.md</c> requirement: a trainer maps it straight into a tensor
    /// rather than walking a structure per world.
    /// </remarks>
    private byte[] Observations(
        EnvMessage kind, StepResult[]? red = null, StepResult[]? blue = null)
    {
        var batch = _batch!;
        var stride = ObservationWriter.Length;

        for (var i = 0; i < batch.Count; i++)
        {
            var world = batch.World(i);
            var view = new WorldView(in world.World.State, world.World.Collision);

            ObservationWriter.Write(in view, world.RedSlot, _observations.AsSpan(((i * 2) + 0) * stride, stride));
            ObservationWriter.Write(in view, world.BlueSlot, _observations.AsSpan(((i * 2) + 1) * stride, stride));
        }

        var componentBytes = EnvProtocol.ComponentBytes;

        // ⚠️ Sized from the real spans rather than from CandidateCount, because a
        // sniper's set is twice a soldier's and a batch can hold both. Assuming
        // one length would truncate half of every mixed batch.
        var scoreFloats = 0;

        if (kind == EnvMessage.StepScored)
        {
            for (var i = 0; i < batch.Count; i++)
            {
                var world = batch.World(i);
                scoreFloats += world.ScoresFor(world.RedSlot).Length;
                scoreFloats += world.ScoresFor(world.BlueSlot).Length;
            }
        }

        var floats = _observations.Length;
        var reply = new byte[
            1 + 4 + (floats * 4) + (batch.Count * 2 * componentBytes)
            + (kind == EnvMessage.StepScored
                ? (batch.Count * 2 * (4 + (6 * 4))) + (scoreFloats * 4)
                : 0)];
        var at = 0;

        reply[at++] = (byte)kind;
        BinaryPrimitives.WriteInt32LittleEndian(reply.AsSpan(at), batch.Count);
        at += 4;

        // ⚠️ One copy, not 26,752 four-byte writes. The observations are already
        // a contiguous float32 block in the layout the wire wants, so on a
        // little-endian host this is a memcpy — and every host this runs on is
        // one. The per-float loop is kept for the other case rather than deleted,
        // because "x86 only" is not a thing this protocol says anywhere.
        //
        // ⚠️ This was NOT the bottleneck, and docs/ENV.md said it was. Measured
        // 2026-08-30: the serialisation is ~2% of a batch-16 round trip against
        // 49% for the observation writer. See that document's measurement table.
        if (BitConverter.IsLittleEndian)
        {
            MemoryMarshal.AsBytes(_observations.AsSpan()).CopyTo(reply.AsSpan(at));
            at += _observations.Length * 4;
        }
        else
        {
            foreach (var value in _observations)
            {
                BinaryPrimitives.WriteSingleLittleEndian(reply.AsSpan(at), value);
                at += 4;
            }
        }

        for (var i = 0; i < batch.Count; i++)
        {
            WriteComponents(reply, ref at, red is null ? default : red[i]);
            WriteComponents(reply, ref at, blue is null ? default : blue[i]);
        }

        // ⚠️ The score block, and ONLY for StepScored. Every agent gets a length
        // even when it is zero, so the block is walkable without knowing which
        // slots searched — a client that had to infer that would be holding a
        // model of this repo's internals, which is the thing the whole seam
        // exists to avoid.
        if (kind == EnvMessage.StepScored)
        {
            for (var i = 0; i < batch.Count; i++)
            {
                var world = batch.World(i);

                WriteScores(
                    reply, ref at, world.ScoresFor(world.RedSlot), world.SearchActionFor(world.RedSlot));
                WriteScores(
                    reply, ref at, world.ScoresFor(world.BlueSlot), world.SearchActionFor(world.BlueSlot));
            }
        }

        return reply;
    }

    /// <summary>
    /// One agent's candidate scores: a count, then that many float32.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Length-prefixed per agent rather than a batch-wide stride.</b> A
    /// sniper searches 120 candidates and everything else 60, so a single stride
    /// would either truncate the snipers or pad every other agent — and a batch
    /// can hold both classes at once.
    /// <para>
    /// ⚠️ <b>Zero is a real length</b>, written for any slot that is not
    /// search-driven. It is not the same as a block of zero scores: zero is a
    /// meaningful score, the blind case, so the two must not be spelled the same
    /// way. See <see cref="StepResult.BestScore"/>.
    /// </para>
    /// <para>
    /// ⚠️ <b>float32</b>, matching the damage fields a score is a difference of.
    /// </para>
    /// </remarks>
    private static void WriteScores(
        byte[] into, ref int at, ReadOnlySpan<double> scores, EnvAction chosen)
    {
        BinaryPrimitives.WriteInt32LittleEndian(into.AsSpan(at), scores.Length);
        at += 4;

        foreach (var score in scores)
        {
            BinaryPrimitives.WriteSingleLittleEndian(into.AsSpan(at), (float)score);
            at += 4;
        }

        // ⚠️ The action the search CHOSE, six i32, after the distribution — and it
        // is not derivable from it. A median of four candidates share the best
        // score and the search picks among them on aim error, so "the argmax" is
        // not one action. For a shadow slot this is the label: what the expert
        // would have done in the state the policy reached.
        //
        // ⚠️ Written even when the count is zero, so the block's shape does not
        // depend on whether a slot searched. It is meaningless there and the count
        // says so.
        BinaryPrimitives.WriteInt32LittleEndian(into.AsSpan(at), chosen.Horizontal);
        at += 4;
        BinaryPrimitives.WriteInt32LittleEndian(into.AsSpan(at), chosen.Jump);
        at += 4;
        BinaryPrimitives.WriteInt32LittleEndian(into.AsSpan(at), chosen.Down);
        at += 4;
        BinaryPrimitives.WriteInt32LittleEndian(into.AsSpan(at), chosen.Fire);
        at += 4;
        BinaryPrimitives.WriteInt32LittleEndian(into.AsSpan(at), chosen.AimDelta);
        at += 4;
        BinaryPrimitives.WriteInt32LittleEndian(into.AsSpan(at), chosen.Zoom);
        at += 4;
    }

    /// <summary>
    /// One agent's reward components and the action it took.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The action is echoed for a client side too, and that is not
    /// redundancy.</b> A trainer logging transitions would otherwise have to
    /// correlate this reply with its own send buffer, and the two stop agreeing
    /// the moment a side is bot-driven — which is precisely when the action is
    /// the thing it most needs. One field that always means "what this agent did"
    /// is cheaper than two paths that usually agree.
    /// </remarks>
    private static void WriteComponents(byte[] into, ref int at, StepResult r)
    {
        BinaryPrimitives.WriteSingleLittleEndian(into.AsSpan(at), (float)r.DamageDealt);
        at += 4;
        BinaryPrimitives.WriteSingleLittleEndian(into.AsSpan(at), (float)r.DamageTaken);
        at += 4;
        BinaryPrimitives.WriteInt32LittleEndian(into.AsSpan(at), r.Kills);
        at += 4;
        BinaryPrimitives.WriteInt32LittleEndian(into.AsSpan(at), r.Deaths);
        at += 4;
        BinaryPrimitives.WriteInt32LittleEndian(into.AsSpan(at), (int)r.Termination);
        at += 4;

        BinaryPrimitives.WriteInt32LittleEndian(into.AsSpan(at), r.Action.Horizontal);
        at += 4;
        BinaryPrimitives.WriteInt32LittleEndian(into.AsSpan(at), r.Action.Jump);
        at += 4;
        BinaryPrimitives.WriteInt32LittleEndian(into.AsSpan(at), r.Action.Down);
        at += 4;
        BinaryPrimitives.WriteInt32LittleEndian(into.AsSpan(at), r.Action.Fire);
        at += 4;
        BinaryPrimitives.WriteInt32LittleEndian(into.AsSpan(at), r.Action.AimDelta);
        at += 4;
        BinaryPrimitives.WriteInt32LittleEndian(into.AsSpan(at), r.Action.Zoom);
        at += 4;

        // ⚠️ 1 or 0 rather than a bool byte, keeping the whole block 4-byte typed
        // — see EnvProtocol.ComponentBytes.
        BinaryPrimitives.WriteInt32LittleEndian(into.AsSpan(at), r.ActionExact ? 1 : 0);
        at += 4;

        // ⚠️ APPENDED, deliberately, where v4's Zoom was INSERTED. Inserting moved
        // ActionExact under every reader and my own test caught it by asserting
        // the zoom level was 1; appending means a stale reader runs off the end
        // instead, which fails loudly. The action heads had to stay contiguous;
        // this does not.
        //
        // ⚠️ float32 like the damage fields. A score is a damage difference, so it
        // carries exactly the precision damage does.
        BinaryPrimitives.WriteSingleLittleEndian(into.AsSpan(at), (float)r.BestScore);
        at += 4;
    }
}
