using Gg2.Bots;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Gg2.Env;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// The env's socket protocol, exercised over a real socket.
/// </summary>
/// <remarks>
/// <c>docs/ENV.md</c>: "Protocol round-trip against a real socket, including the
/// `Hello` refuse." ⚠️ <b>A real socket rather than a memory stream, and that
/// is the point of the gate</b> — framing bugs hide behind a stream that always
/// returns everything asked for, and reappear the first time a frame is split
/// across two reads.
/// </remarks>
public sealed class EnvProtocolTests : IDisposable
{
    private readonly TcpListener _listener;
    private readonly Task _server;
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;

    public EnvProtocolTests()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();

        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _server = Task.Run(() =>
        {
            using var accepted = _listener.AcceptTcpClient();
            using var stream = accepted.GetStream();

            new EnvServer(ReferenceTree.IncludedFiles).ServeOne(stream);
        });

        _client = new TcpClient();
        _client.Connect(IPAddress.Loopback, port);
        _stream = _client.GetStream();
    }

    public void Dispose()
    {
        try
        {
            Send(EnvMessage.Close, []);
            Receive();
        }
        catch (IOException)
        {
            // The server may already have gone; this is teardown.
        }

        _stream.Dispose();
        _client.Dispose();
        _server.Wait(TimeSpan.FromSeconds(5));
        _listener.Stop();
    }

    private void Send(EnvMessage kind, ReadOnlySpan<byte> body)
    {
        var frame = new byte[1 + body.Length];
        frame[0] = (byte)kind;
        body.CopyTo(frame.AsSpan(1));

        EnvProtocol.WriteFrame(_stream, frame);
    }

    private byte[] Receive() =>
        EnvProtocol.ReadFrame(_stream) ?? throw new IOException("the server closed the connection");

    private static byte[] Int(int value)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, value);
        return b;
    }

    private static byte[] Real(double value)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteDoubleLittleEndian(b, value);
        return b;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var total = parts.Sum(p => p.Length);
        var all = new byte[total];
        var at = 0;

        foreach (var p in parts)
        {
            p.CopyTo(all, at);
            at += p.Length;
        }

        return all;
    }

    private static byte[] MakeBatchBody(
        int count,
        int cap = 20,
        int repeat = 2,
        int threads = 1,
        EnvDriver red = EnvDriver.Client,
        EnvDriver blue = EnvDriver.Client,
        int redTier = 0,
        int blueTier = 0,
        RolloutOpponent redRollout = RolloutOpponent.HoldLastInput,
        RolloutOpponent blueRollout = RolloutOpponent.HoldLastInput,
        double minSeparation = 0,
        double maxSeparation = 0,
        int redHorizon = 0,
        int blueHorizon = 0,
        double redX = 600,
        double blueX = 700,
        string map = "gg_debug")
    {
        var name = System.Text.Encoding.UTF8.GetBytes(map);

        return Concat(
            Int(count),
            Int(name.Length),
            name,
            Int((int)PlayerClass.Soldier),
            Int((int)PlayerClass.Soldier),
            Real(redX), Real(850),
            Real(blueX), Real(850),
            Int(cap),
            Int(repeat),
            Int(1234),
            Int(threads),

            // ⚠️ v2's four driver fields. They are REQUIRED, not
            // optional-if-absent: the reward block changed length in v2, so a
            // client that omitted them would be reading a reply it cannot slice.
            Int((int)red),
            Int((int)blue),
            Int(redTier),
            Int(blueTier),

            // ⚠️ v3's four. Required for the same reason plus a sharper one: a
            // short frame would read the separation band out of whatever
            // followed, and any non-zero maximum moves placement off the spawn
            // points entirely.
            Int((int)redRollout),
            Int((int)blueRollout),
            Real(minSeparation),
            Real(maxSeparation),

            // v6's horizons. 0 is "the search's own default" rather than a horizon
            // of zero, which RolloutOptions.WithDefaults resolves.
            Int(redHorizon),
            Int(blueHorizon));
    }

    /// <summary>
    /// ⚠️ <b>The version and the frame layout are pinned to each other</b>, so
    /// changing one without the other is a failing test rather than a silent lie.
    /// </summary>
    /// <remarks>
    /// <b>This is the gate for a bug that shipped.</b> <c>dbe8b15</c> added
    /// protocol v3's four <c>MakeBatch</c> fields to the body, to
    /// <c>EnvServer.MakeBatch</c>, to both clients and to <c>docs/ENV.md</c> —
    /// and left <see cref="EnvProtocol.Version"/> at 2. Every one of those edits
    /// was consistent with every other; the constant was simply never written,
    /// and the commit message, the document and the message to <c>gg2-rl</c> all
    /// asserted that it had been.
    /// <para>
    /// ⚠️ <b>What it cost was the handshake's whole purpose.</b> A client
    /// speaking the real layout sent <c>Hello(3)</c> and was <em>refused</em>;
    /// a client speaking the stale one handshook <em>successfully</em> and then
    /// failed its first <c>MakeBatch</c> on the frame-length check — with an
    /// error naming v2 as the thing it should be speaking, which it already was.
    /// The version exists so a mismatch is named at
    /// <see cref="EnvMessage.Hello"/>; a length check is a backstop for a
    /// mis-framed client, not a substitute for it.
    /// </para>
    /// <para>
    /// ⚠️ <b>Nothing in this repo could have caught it, and that is the point of
    /// the pin.</b> Every test here builds its own frames from the same constants
    /// the server reads, so client and server agreed with each other about a
    /// layout the version misdescribed. Only a caller with an independent notion
    /// of the version — <c>gg2-rl</c>, whose <c>test_protocol.py</c> pins these
    /// against its own — could see it. This test is the local half of that:
    /// the two numbers are asserted <em>together</em>, so the body cannot grow
    /// without somebody editing a line with the version on it.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheVersionAndTheFrameLayoutMoveTogether()
    {
        // ⚠️ Change BOTH or neither. If you are here because you added a field to
        // MakeBatch, the version below is the other half of that change — and
        // gg2-rl has to be told, because its suite pins these from outside.
        Assert.Equal(6, EnvProtocol.Version);
        Assert.Equal(104, EnvProtocol.MakeBatchFixedBytes);

        // ⚠️ v4's sixth action head, on both sides of the wire. A client a version
        // behind sends five bytes an agent and every action from the second world
        // on is read one byte out of step — plausible values, wrong agent.
        Assert.Equal(6, EnvProtocol.ActionBytes);
        Assert.Equal(SimulationStamp.ActionEncodingVersion, 2);

        // The reward block is the other half of the wire the trainer slices, and
        // v3 deliberately did NOT move it — stated here so a change to it is a
        // conscious edit to this same test rather than a surprise downstream.
        Assert.Equal(52, EnvProtocol.ComponentBytes);

        // And the handshake reports the same number this file pins, rather than
        // one computed somewhere else.
        Assert.Equal(EnvProtocol.Version, EnvProtocol.Handshake.Current.ProtocolVersion);
    }

    /// <summary>
    /// A body one field short of the layout is refused, and the message names the
    /// version this build actually speaks.
    /// </summary>
    /// <remarks>
    /// The backstop, gated so that it stays a backstop: it exists for a
    /// mis-framed client, and a client that is merely a version behind should
    /// have been stopped at <see cref="EnvMessage.Hello"/> long before here.
    /// </remarks>
    [Fact]
    public void AShortMakeBatchIsRefusedWithTheVersionInIt()
    {
        Send(EnvMessage.Hello, Int(EnvProtocol.Version));
        Receive();

        // A v2-shaped body: everything except the four fields v3 appended.
        var full = MakeBatchBody(1);
        Send(EnvMessage.MakeBatch, full.AsSpan(0, full.Length - 24));

        var reply = Receive();
        Assert.Equal((byte)EnvMessage.Error, reply[0]);

        var length = BinaryPrimitives.ReadInt32LittleEndian(reply.AsSpan(1));
        var reason = System.Text.Encoding.UTF8.GetString(reply, 5, length);

        Assert.Contains($"protocol v{EnvProtocol.Version}", reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠️ <b>The advertised action space matches the one the wire actually
    /// carries</b> — one head per action byte, and every width real.
    /// </summary>
    /// <remarks>
    /// <b>The gate for a blocker that shipped.</b> v4 added the <c>Zoom</c> head
    /// to the request, the reader, the reward block and
    /// <see cref="EnvProtocol.ActionBytes"/>, and left
    /// <c>Handshake.HeadShape</c> at five entries. The server read six bytes and
    /// wrote six heads back while telling every client the space had five.
    /// <para>
    /// ⚠️ <b>Nothing here could have caught it and the failure is silent.</b>
    /// Every test in this file builds its frames from the constants directly, so
    /// none of them ever asks the handshake what the action space is. A trainer
    /// that believed the advertisement would size a five-head policy against a
    /// six-head space — not a crash, just a sniper that can never zoom, which is
    /// the exact bug v4 removed. Found by <c>gg2-rl</c> on its first connection,
    /// because it asserts the advertised shape rather than assuming it.
    /// </para>
    /// <para>
    /// ⚠️ <b>Derived from <see cref="EnvProtocol.ActionBytes"/>, never restated.</b>
    /// A literal 6 here would drift exactly as the literal 5 did — the whole
    /// lesson of this bug and of the version constant before it is that a
    /// self-description checked against another copy of itself is not checked.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheAdvertisedHeadShapeMatchesTheWire()
    {
        var shape = EnvProtocol.Handshake.Current.HeadShape;

        // One head per byte the Step request carries per agent.
        Assert.Equal(EnvProtocol.ActionBytes, shape.Length);

        // ⚠️ And the reward block echoes every head plus ActionExact, at four
        // bytes each after the five reward fields — so the sizes are tied too.
        // Getting this wrong is how ActionExact silently moved under a reader.
        // five reward fields, one per action head, ActionExact, BestScore.
        Assert.Equal(EnvProtocol.ComponentBytes, (5 + shape.Length + 2) * 4);

        foreach (var width in shape)
            Assert.True(width >= 2, $"a head of width {width} cannot encode a choice");

        // The widths themselves, against the decoder that consumes them.
        Assert.Equal([3, 2, 2, 3, ActionDecoder.AimBins, 2], shape);
    }

    /// <summary>
    /// ✅ <b><see cref="EnvMessage.StepScored"/> carries the search's whole
    /// candidate distribution, and plain <see cref="EnvMessage.Step"/> carries
    /// none of it.</b>
    /// </summary>
    /// <remarks>
    /// <c>gg2-rl/docs/DESIGN.md</c>'s reason a search is a better expert than a
    /// reward signal is that its output is a distribution rather than a scalar. A
    /// one-hot on the argmax throws away the search's own uncertainty — that two
    /// candidates scored 70 and 69 is exactly the information saying "either is
    /// fine".
    /// <para>
    /// ⚠️ <b>Opt-in per call, and the control is the point.</b> 60 floats an agent
    /// (120 for a sniper) against a 52-byte reward block would tax the random-head
    /// smoke run and move phase 0's throughput criterion — the one number quoted
    /// for the socket boundary. So this asserts that a plain Step reply is exactly
    /// the length it was before, byte for byte, and not merely that StepScored is
    /// longer.
    /// </para>
    /// <para>
    /// ⚠️ <b>Length-prefixed per agent</b>, because a batch can hold a sniper
    /// (120 candidates) beside a soldier (60) and a single stride would truncate
    /// one or pad the other. A non-searching slot gets a length of <b>zero</b>,
    /// which is deliberately not the same as a block of zero scores: zero is a
    /// meaningful score — the blind case.
    /// </para>
    /// </remarks>
    [Fact]
    public void OnlyStepScoredCarriesTheCandidateDistribution()
    {
        Send(EnvMessage.Hello, Int(EnvProtocol.Version));
        Receive();

        // Red searches, blue does not — so one agent has a distribution and the
        // other has an explicit zero.
        Send(EnvMessage.MakeBatch, MakeBatchBody(count: 1, red: EnvDriver.Rollout, blue: EnvDriver.Idle));
        Assert.Equal((byte)EnvMessage.MakeBatch, Receive()[0]);

        var actions = new byte[2 * EnvProtocol.ActionBytes];
        actions[4] = ActionDecoder.AimCentreBin;
        actions[EnvProtocol.ActionBytes + 4] = ActionDecoder.AimCentreBin;

        Send(EnvMessage.Step, actions);
        var plain = Receive();
        Assert.Equal((byte)EnvMessage.Step, plain[0]);

        Send(EnvMessage.StepScored, actions);
        var scored = Receive();

        // ⚠️ The reply KIND echoes the request, so a client never has to track
        // which shape it is holding.
        Assert.Equal((byte)EnvMessage.StepScored, scored[0]);

        var expectedPlain = 1 + 4
            + (ObservationWriter.Length * 2 * 4)
            + (2 * EnvProtocol.ComponentBytes);

        // The control: a plain Step is untouched by this feature existing.
        Assert.Equal(expectedPlain, plain.Length);

        // Two length prefixes plus red's candidates. Blue is Idle: zero of them.
        var at = expectedPlain;

        var redCount = BinaryPrimitives.ReadInt32LittleEndian(scored.AsSpan(at));
        at += 4;

        Assert.Equal(60, redCount);

        var scores = new float[redCount];
        for (var i = 0; i < redCount; i++)
        {
            scores[i] = BinaryPrimitives.ReadSingleLittleEndian(scored.AsSpan(at));
            at += 4;
        }

        var blueCount = BinaryPrimitives.ReadInt32LittleEndian(scored.AsSpan(at));
        at += 4;

        // ⚠️ Zero, written explicitly. An Idle slot did not search; that is not
        // the same as having searched and scored nothing.
        Assert.Equal(0, blueCount);

        Assert.Equal(at, scored.Length);

        // And the distribution agrees with the scalar the same reply carries:
        // BestScore is the last field of red's component block.
        var redComponents = 1 + 4 + (ObservationWriter.Length * 2 * 4);
        var bestScore = BinaryPrimitives.ReadSingleLittleEndian(
            scored.AsSpan(redComponents + EnvProtocol.ComponentBytes - 4));

        var max = scores[0];
        foreach (var v in scores)
        {
            if (v > max)
                max = v;
        }

        Assert.Equal(max, bestScore);
    }

    /// <summary>
    /// ✅ <b>The candidate ACTIONS come back, and the action at the distribution's
    /// argmax is the action the search took.</b>
    /// </summary>
    /// <remarks>
    /// <c>StepScored</c> sent scores aligned with a candidate list whose actions
    /// the env never sent, so a consumer turning an index into an action had to
    /// reconstruct <c>RolloutSearch</c>'s enumeration in its own repo — the second
    /// source of truth the seam exists to prevent. ⚠️ <b>This repo made exactly
    /// that argument about the shadow slot's argmax and did not notice it applied
    /// to the whole distribution</b>; <c>gg2-rl</c> did, and without the actions it
    /// could build only margin weighting rather than true soft targets.
    /// <para>
    /// ⚠️ <b>ALIGNMENT is the assertion, not presence.</b> A candidate list that
    /// came back in a different order from the scores would train a policy towards
    /// the wrong actions and nothing about it would look wrong. The one relation
    /// that cannot hold by accident is that the action at the argmax of the
    /// distribution equals the action the reward block says was taken — so that is
    /// what is checked, across the socket, on a real search.
    /// </para>
    /// <para>
    /// ⚠️ <b>No version bump</b>: a new kind leaves every existing reply
    /// byte-identical, so a client that never sends it is unaffected. Asserted
    /// here by checking the plain <c>Step</c> reply's length is untouched.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheCandidateActionsComeBackAlignedWithTheScores()
    {
        Send(EnvMessage.Hello, Int(EnvProtocol.Version));
        Receive();

        // ⚠️ A cap of 120 rather than the default 20: a soldier's refire starts at
        // 29 ticks against a 20-tick horizon, so the first decisions are all the
        // blind tie-break and a short episode never reaches one that scores.
        Send(EnvMessage.MakeBatch, MakeBatchBody(
            count: 1, cap: 120, red: EnvDriver.Rollout, blue: EnvDriver.Idle));
        Assert.Equal((byte)EnvMessage.MakeBatch, Receive()[0]);

        Send(EnvMessage.Candidates, []);
        var table = Receive();
        Assert.Equal((byte)EnvMessage.Candidates, table[0]);

        var at = 1;
        var redCount = BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(at));
        at += 4;

        Assert.Equal(60, redCount);

        var candidates = new (int H, int J, int D, int F, int A, int Z)[redCount];
        for (var i = 0; i < redCount; i++)
        {
            candidates[i] = (
                BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(at)),
                BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(at + 4)),
                BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(at + 8)),
                BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(at + 12)),
                BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(at + 16)),
                BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(at + 20)));
            at += 24;
        }

        // ⚠️ Blue is Idle: an explicit zero, not an absent field.
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(table.AsSpan(at)));
        Assert.Equal(at + 4, table.Length);

        var actions = new byte[2 * EnvProtocol.ActionBytes];
        actions[4] = ActionDecoder.AimCentreBin;
        actions[EnvProtocol.ActionBytes + 4] = ActionDecoder.AimCentreBin;

        // Step until the search finds something, so the argmax is meaningful
        // rather than the blind tie-break.
        var componentsAt = 1 + 4 + (ObservationWriter.Length * 2 * 4);
        byte[] scored;
        float best;
        var scores = Array.Empty<float>();

        var guard = 0;
        do
        {
            Send(EnvMessage.StepScored, actions);
            scored = Receive();

            best = BinaryPrimitives.ReadSingleLittleEndian(
                scored.AsSpan(componentsAt + EnvProtocol.ComponentBytes - 4));

            var sat = componentsAt + (2 * EnvProtocol.ComponentBytes);
            var n = BinaryPrimitives.ReadInt32LittleEndian(scored.AsSpan(sat));
            sat += 4;

            scores = new float[n];
            for (var i = 0; i < n; i++)
                scores[i] = BinaryPrimitives.ReadSingleLittleEndian(scored.AsSpan(sat + (i * 4)));
        }
        while (best == 0 && ++guard < 100);

        Assert.True(best != 0, "the search never scored anything; the argmax would be the blind tie-break");
        Assert.Equal(redCount, scores.Length);

        var max = scores[0];
        foreach (var v in scores)
        {
            if (v > max)
                max = v;
        }

        var taken = (
            BinaryPrimitives.ReadInt32LittleEndian(scored.AsSpan(componentsAt + 20)),
            BinaryPrimitives.ReadInt32LittleEndian(scored.AsSpan(componentsAt + 24)),
            BinaryPrimitives.ReadInt32LittleEndian(scored.AsSpan(componentsAt + 28)),
            BinaryPrimitives.ReadInt32LittleEndian(scored.AsSpan(componentsAt + 32)),
            BinaryPrimitives.ReadInt32LittleEndian(scored.AsSpan(componentsAt + 36)),
            BinaryPrimitives.ReadInt32LittleEndian(scored.AsSpan(componentsAt + 40)));

        // ⚠️ RE-RECORDED 2026-08-29. This used to assert the taken action was the
        // candidate at the FIRST argmax index, and that stopped being true when
        // RolloutSearch gained its aim tie-break: the search now picks among tied
        // candidates on aim error rather than on enumeration order, so the action
        // it takes is IN the argmax set without being the earliest member of it.
        // gg2-rl measured a median of SIX actions sharing the best score, so
        // "the argmax" was never a single thing to begin with.
        //
        // ⚠️ THE ALIGNMENT CLAIM SURVIVES AND IS THE SAME ONE: look the taken
        // action up in the candidate table and its score must be maximal. A table
        // returned in a different order from the scores still fails — the action
        // is found at an index whose score is not the max — which is what the
        // reversal mutation checks.
        var takenAt = Array.FindIndex(
            candidates, c => c == taken);

        Assert.True(takenAt >= 0, "the action taken is not in the candidate table at all");
        Assert.Equal(max, scores[takenAt]);
    }

    /// <summary>A handshake agrees on everything a checkpoint depends on.</summary>
    [Fact]
    public void HelloAgreesOnTheVersionsAndTheShape()
    {
        Send(EnvMessage.Hello, Int(EnvProtocol.Version));

        var reply = Receive();

        Assert.Equal((byte)EnvMessage.Hello, reply[0]);

        var at = 1;
        int Next()
        {
            var v = BinaryPrimitives.ReadInt32LittleEndian(reply.AsSpan(at));
            at += 4;
            return v;
        }

        Assert.Equal(EnvProtocol.Version, Next());
        Assert.Equal(ObservationWriter.LayoutVersion, Next());
        Assert.Equal(SimulationStamp.ActionEncodingVersion, Next());
        Assert.Equal(ObservationWriter.Length, Next());

        var uuidLength = Next();
        var uuid = System.Text.Encoding.UTF8.GetString(reply, at, uuidLength);
        at += uuidLength;

        // ⚠️ The simulation stamp is in the handshake because a policy trained
        // against different physics is not loadable, and a version check that
        // covered only the framing would let that through.
        Assert.Equal(SimulationStamp.Uuid, uuid);

        // ⚠️ DERIVED from ActionBytes, not restated. This line read
        // `Assert.Equal(5, Next())` while the wire carried six heads, and it
        // passed — because it checked the advertisement against a literal copy of
        // the advertisement rather than against the thing advertised. That is the
        // whole failure: a self-description checked against another copy of
        // itself is not checked. TheAdvertisedHeadShapeMatchesTheWire is the
        // other half, tying the shape to the frame and the reward block.
        Assert.Equal(EnvProtocol.ActionBytes, Next());

        foreach (var width in EnvProtocol.Handshake.Current.HeadShape)
            Assert.Equal(width, Next());
    }

    /// <summary>
    /// ⚠️ <b>A version mismatch is REFUSED, not warned about.</b>
    /// </summary>
    /// <remarks>
    /// The <c>PROTOCOL_UUID</c> idiom. A trainer running against a different
    /// protocol is not slightly wrong — it is collecting data that cannot be
    /// used — and it should find out in the first millisecond rather than after
    /// an hour of rollouts.
    /// </remarks>
    [Fact]
    public void HelloRefusesAVersionMismatch()
    {
        Send(EnvMessage.Hello, Int(EnvProtocol.Version + 1));

        var reply = Receive();

        Assert.Equal((byte)EnvMessage.Error, reply[0]);

        var length = BinaryPrimitives.ReadInt32LittleEndian(reply.AsSpan(1));
        var reason = System.Text.Encoding.UTF8.GetString(reply, 5, length);

        Assert.Contains("protocol version", reason);
    }

    /// <summary>A batch is made, stepped, and the reply is the right size.</summary>
    [Fact]
    public void ABatchCanBeMadeAndStepped()
    {
        const int count = 4;

        Send(EnvMessage.MakeBatch, MakeBatchBody(count));

        var made = Receive();

        Assert.Equal((byte)EnvMessage.MakeBatch, made[0]);
        Assert.Equal(count, BinaryPrimitives.ReadInt32LittleEndian(made.AsSpan(1)));

        // 1 kind + 4 count + observations + components.
        var expected = 1 + 4 + (count * 2 * ObservationWriter.Length * 4)
            + (count * 2 * EnvProtocol.ComponentBytes);
        Assert.Equal(expected, made.Length);

        // Five head bytes per agent, red then blue, per world.
        var actions = new byte[count * 2 * EnvProtocol.ActionBytes];

        for (var i = 0; i < count; i++)
        {
            var pair = i * 2 * EnvProtocol.ActionBytes;

            actions[pair + 0] = 2;                                          // red: right
            actions[pair + 4] = ActionDecoder.AimCentreBin;
            actions[pair + EnvProtocol.ActionBytes + 0] = 1;                // blue: left
            actions[pair + EnvProtocol.ActionBytes + 4] = ActionDecoder.AimCentreBin;
        }

        Send(EnvMessage.Step, actions);

        var stepped = Receive();

        Assert.Equal((byte)EnvMessage.Step, stepped[0]);
        Assert.Equal(expected, stepped.Length);

        // The observations moved: the agents walked.
        Assert.NotEqual(made.AsSpan(5, 64).ToArray(), stepped.AsSpan(5, 64).ToArray());
    }

    /// <summary>A snapshot round-trips over the wire.</summary>
    [Fact]
    public void ASnapshotRoundTripsOverTheSocket()
    {
        Send(EnvMessage.MakeBatch, MakeBatchBody(2));
        Receive();

        Send(EnvMessage.Snapshot, Int(0));

        var snap = Receive();
        Assert.Equal((byte)EnvMessage.Snapshot, snap[0]);

        var length = BinaryPrimitives.ReadInt32LittleEndian(snap.AsSpan(1));
        Assert.True(length > 1000, $"a whole world should be more than {length} bytes");

        Send(EnvMessage.Restore, Concat(Int(0), Int(length), snap.AsSpan(5, length).ToArray()));

        Assert.Equal((byte)EnvMessage.Restore, Receive()[0]);
    }

    /// <summary>
    /// ⚠️ <b>A malformed request is answered with an Error, never a dropped
    /// connection.</b>
    /// </summary>
    /// <remarks>
    /// A trainer that saw the socket close would report "the env died" and the
    /// reason would be in nobody's log. This also checks the connection is still
    /// usable afterwards, which is the half that makes it a refusal rather than
    /// a crash that happened to be caught.
    /// </remarks>
    [Fact]
    public void AMalformedRequestIsRefusedAndTheConnectionSurvives()
    {
        Send(EnvMessage.Step, []);

        var reply = Receive();
        Assert.Equal((byte)EnvMessage.Error, reply[0]);

        // Still alive.
        Send(EnvMessage.Hello, Int(EnvProtocol.Version));
        Assert.Equal((byte)EnvMessage.Hello, Receive()[0]);
    }

    /// <summary>An unknown message kind is refused rather than ignored.</summary>
    [Fact]
    public void AnUnknownMessageIsRefused()
    {
        Send((EnvMessage)200, []);

        Assert.Equal((byte)EnvMessage.Error, Receive()[0]);
    }

    /// <summary>
    /// ⚠️ <b>A frame is read to its length, however the socket splits it.</b>
    /// </summary>
    /// <remarks>
    /// A socket read returns what has arrived, not what was asked for. Treating
    /// one <c>Read</c> as a whole frame works on loopback until a frame is
    /// larger than some buffer, and then fails as a corrupt message rather than
    /// as a short read. This sends a header and a body in two writes with a
    /// pause between them.
    /// </remarks>
    [Fact]
    public void AFrameSplitAcrossTwoWritesIsStillOneMessage()
    {
        var body = new byte[1 + 4];
        body[0] = (byte)EnvMessage.Hello;
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(1), EnvProtocol.Version);

        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)body.Length);

        _stream.Write(header);
        _stream.Flush();
        Thread.Sleep(50);
        _stream.Write(body);
        _stream.Flush();

        Assert.Equal((byte)EnvMessage.Hello, Receive()[0]);
    }
    /// <summary>
    /// A bot-driven slot can be asked for over the socket, and what it did comes
    /// back in the reply.
    /// </summary>
    /// <remarks>
    /// <b>The item, end to end.</b> Until protocol v2 <c>MakeBatch</c> could only
    /// drive both slots from the client, so a duel between <c>RolloutSearch</c>
    /// and <c>ScriptedBot</c> — two things that both live in this repo — could not
    /// be asked for at all. That blocked <c>gg2-rl</c>'s phase 1 exit criterion,
    /// its phase 2 collection and its phase 3 opponent pool.
    /// <para>
    /// ⚠️ <b>The two properties it asked for are the two assertions here</b>: a
    /// bot-driven slot still returns its own reward components, and the action the
    /// bot chose comes back. Neither is visible from outside the process any other
    /// way.
    /// </para>
    /// <para>
    /// ⚠️ <b>Idle rather than Scripted, on purpose.</b> A scripted opponent needs
    /// a nav cache, which is generated out of band and gitignored — a protocol
    /// test that skipped without one would be a protocol test nobody runs. Idle is
    /// the driver whose behaviour is certain, so a reply that carries its action
    /// and its damage proves the wire without depending on a bot.
    /// </para>
    /// </remarks>
    [Fact]
    public void ABotDrivenSlotIsAskedForOverTheSocketAndItsActionComesBack()
    {
        Send(EnvMessage.Hello, Int(EnvProtocol.Version));
        Receive();

        Send(EnvMessage.MakeBatch, MakeBatchBody(count: 2, cap: 40, repeat: 2, blue: EnvDriver.Idle));

        var made = Receive();
        Assert.Equal((byte)EnvMessage.MakeBatch, made[0]);

        var componentsAt = 1 + 4 + (2 * 2 * ObservationWriter.Length * 4);
        var sawDamage = false;

        for (var step = 0; step < 40; step++)
        {
            // Red fires straight at its neighbour every step; blue is the idle
            // driver and is sent an action that must be IGNORED.
            var actions = new byte[2 * 2 * EnvProtocol.ActionBytes];

            for (var w = 0; w < 2; w++)
            {
                // red: fire, no turn
                actions[(w * 10) + 3] = 1;
                actions[(w * 10) + 4] = ActionDecoder.AimCentreBin;

                // ⚠️ blue is sent a hard left turn and a jump, which the env must
                // throw away because a bot drives that slot. If the driver were
                // ignored and the client's action used instead, this is what would
                // come back.
                actions[(w * 10) + 5 + 0] = 1;
                actions[(w * 10) + 5 + 1] = 1;
                actions[(w * 10) + 5 + 4] = 0;
            }

            Send(EnvMessage.Step, actions);

            var reply = Receive();
            Assert.Equal((byte)EnvMessage.Step, reply[0]);

            for (var w = 0; w < 2; w++)
            {
                var blueAt = componentsAt + (((w * 2) + 1) * EnvProtocol.ComponentBytes);

                var damageTaken = BinaryPrimitives.ReadSingleLittleEndian(reply.AsSpan(blueAt + 4));

                var horizontal = BinaryPrimitives.ReadInt32LittleEndian(reply.AsSpan(blueAt + 20));
                var jump = BinaryPrimitives.ReadInt32LittleEndian(reply.AsSpan(blueAt + 24));
                var aim = BinaryPrimitives.ReadInt32LittleEndian(reply.AsSpan(blueAt + 36));

                // ⚠️ v4 inserted Zoom at 40, so `exact` moved to 44. Reading it at
                // the old offset returned the zoom level and asserted it was 1 —
                // which is the failure a tail-appended field would NOT have caused
                // and an inserted one does. It is why the head went at the end of
                // the ACTION block rather than the end of the record: the action
                // heads stay contiguous, and the one field that moved is caught
                // here rather than by a trainer slicing plausible nonsense.
                var zoom = BinaryPrimitives.ReadInt32LittleEndian(reply.AsSpan(blueAt + 40));
                var exact = BinaryPrimitives.ReadInt32LittleEndian(reply.AsSpan(blueAt + 44));

                // The bot's action, not the client's — an idle driver presses
                // nothing and does not turn.
                Assert.Equal(0, horizontal);
                Assert.Equal(0, jump);
                Assert.Equal(ActionDecoder.AimCentreBin, aim);

                // An idle soldier is not a sniper and never scopes.
                Assert.Equal(0, zoom);

                Assert.Equal(1, exact);

                if (damageTaken > 0)
                    sawDamage = true;
            }
        }

        // ⚠️ The other half: a bot-driven slot still earns its reward components.
        // Without this the test passes on a server that returned a zeroed block
        // for every bot slot.
        Assert.True(sawDamage, "a bot-driven slot was shot at for 40 steps and reported no damage taken");
    }

    /// <summary>An unknown driver is refused on the wire, with a reason.</summary>
    /// <remarks>
    /// ⚠️ <b>Refused at the protocol edge rather than thrown from inside the batch
    /// construction</b>, so the client gets a sentence it can print instead of a
    /// dropped connection.
    /// </remarks>
    [Fact]
    public void AnUnknownDriverIsRefusedWithAReason()
    {
        Send(EnvMessage.Hello, Int(EnvProtocol.Version));
        Receive();

        Send(EnvMessage.MakeBatch, MakeBatchBody(count: 1, red: (EnvDriver)200));

        var reply = Receive();

        Assert.Equal((byte)EnvMessage.Error, reply[0]);

        var length = BinaryPrimitives.ReadInt32LittleEndian(reply.AsSpan(1));
        var reason = System.Text.Encoding.UTF8.GetString(reply, 5, length);

        Assert.Contains("driver", reason, StringComparison.OrdinalIgnoreCase);
    }
}
