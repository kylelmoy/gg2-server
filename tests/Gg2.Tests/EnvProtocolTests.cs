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
        int blueTier = 0)
    {
        var name = System.Text.Encoding.UTF8.GetBytes("gg_debug");

        return Concat(
            Int(count),
            Int(name.Length),
            name,
            Int((int)PlayerClass.Soldier),
            Int((int)PlayerClass.Soldier),
            Real(600), Real(850),
            Real(700), Real(850),
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
            Int(blueTier));
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

        Assert.Equal(5, Next());
        Assert.Equal(3, Next());
        Assert.Equal(2, Next());
        Assert.Equal(2, Next());
        Assert.Equal(3, Next());
        Assert.Equal(ActionDecoder.AimBins, Next());
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
        var actions = new byte[count * 10];

        for (var i = 0; i < count; i++)
        {
            actions[(i * 10) + 0] = 2;                              // red: right
            actions[(i * 10) + 4] = ActionDecoder.AimCentreBin;
            actions[(i * 10) + 5] = 1;                              // blue: left
            actions[(i * 10) + 9] = ActionDecoder.AimCentreBin;
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
            var actions = new byte[2 * 2 * 5];

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
                var exact = BinaryPrimitives.ReadInt32LittleEndian(reply.AsSpan(blueAt + 40));

                // The bot's action, not the client's — an idle driver presses
                // nothing and does not turn.
                Assert.Equal(0, horizontal);
                Assert.Equal(0, jump);
                Assert.Equal(ActionDecoder.AimCentreBin, aim);
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
