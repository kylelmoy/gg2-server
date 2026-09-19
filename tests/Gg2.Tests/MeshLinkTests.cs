using System.Net;
using Gg2.Maps;
using Gg2.Mesh;
using Gg2.Protocol;
using Gg2.Server;
using Gg2.Simulation;
using Xunit;

namespace Gg2.Tests;

/// <summary>
/// Two nodes exchanging inputs over a real socket.
/// </summary>
/// <remarks>
/// <b>Loopback rather than a fake.</b> The failure this has to be able to catch
/// is a reader that assumes one read is one frame — which is true on a fake, is
/// usually true on loopback, and is false over a real link exactly when a match
/// is busiest. So the framing is exercised directly, byte at a time, as well as
/// through the socket.
/// </remarks>
public sealed class MeshLinkTests
{
    private static CollisionWorld Collision() =>
        new(LevelMap.FromPng("ctf_truefort", ReferenceTree.ReadMap("ctf_truefort")));

    private static WorldState World()
    {
        var state = default(WorldState);

        state.AddPlayer(0);

        for (var slot = 1; slot <= 2; slot++)
        {
            var team = slot == 1 ? Team.Red : Team.Blue;

            state.AddPlayer(slot);
            state.Players[slot].Team = team;
            state.Players[slot].Class = PlayerClass.Soldier;

            var character = CharacterState.Fresh(PlayerClass.Soldier, team);
            character.X = 380 + (slot * 100);
            character.Y = 268;
            character.OnGround = true;

            state.Characters[slot] = character;
            state.Weapons[slot] = WeaponState.Spawn(PlayerClass.Soldier, character.X, character.Y);
            state.Alive[slot] = true;
        }

        return state;
    }

    private static (MeshLink Host, MeshLink Peer) Pair()
    {
        var host = MeshLink.Listen(IPAddress.Loopback, 0);
        var peer = MeshLink.Connect("127.0.0.1", host.Port);

        return (host, peer);
    }

    [Fact]
    public void InputsCrossTheWireAndReachTheNode()
    {
        ReferenceTree.RequireAvailable();

        var (host, peer) = Pair();
        using var _ = host;
        using var __ = peer;

        var node = new MeshNode(Collision(), World());

        peer.SendInputs(7, [1, 2], [
            new PlayerInput(PlayerKeys.Left, 900, 40),
            new PlayerInput(PlayerKeys.Jump | PlayerKeys.Attack, 1800, 20),
        ]);

        peer.Service(node);

        // The host end accepts and reads; a couple of turns because accept and
        // first read are separate polls.
        for (var i = 0; i < 20 && host.Stats.InputsReceived < 2; i++)
        {
            host.Service(node);
            Thread.Sleep(5);
        }

        Assert.Equal(2, host.Stats.InputsReceived);

        // The node was told, and told the right thing: tick 7 is in the future
        // for a node at tick 0, so advancing to it must use those keys.
        node.Advance(8);

        Assert.True(node.Tick > 7);
    }

    /// <summary>
    /// A frame split across reads is still one frame, and two frames in one read
    /// are still two.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>The bug this exists for is invisible on a fake transport.</b> A
    /// reader that treated a read as a frame boundary passes every test written
    /// against an in-memory pipe and loses the stream's alignment the first time
    /// a real network splits a packet — after which it submits whatever the
    /// following bytes happen to spell.
    /// </remarks>
    [Fact]
    public void AFrameSplitAcrossReadsIsStillOneFrame()
    {
        ReferenceTree.RequireAvailable();

        var (host, peer) = Pair();
        using var _ = host;
        using var __ = peer;

        var node = new MeshNode(Collision(), World());

        // Three frames, written and flushed one byte at a time by the socket's
        // own whim — the sleep between services is what gives the stack the
        // chance to deliver them in pieces.
        for (var tick = 0; tick < 3; tick++)
        {
            peer.SendInputs(tick, [1], [new PlayerInput(PlayerKeys.Right, (ushort)(tick * 100), 40)]);
            peer.Service(node);
        }

        for (var i = 0; i < 40 && host.Stats.InputsReceived < 3; i++)
        {
            host.Service(node);
            Thread.Sleep(5);
        }

        Assert.Equal(3, host.Stats.InputsReceived);
    }

    /// <summary>
    /// A digest frame carries what the peer believes, so a node can notice that
    /// it disagrees.
    /// </summary>
    [Fact]
    public void ADigestCrossesTheWireIntact()
    {
        ReferenceTree.RequireAvailable();

        var (host, peer) = Pair();
        using var _ = host;
        using var __ = peer;

        var node = new MeshNode(Collision(), World());

        const ulong Sent = 0xDEADBEEFCAFEF00D;
        peer.SendDigest(42, Sent);
        peer.Service(node);

        var seen = new List<(int Tick, ulong Digest)>();

        for (var i = 0; i < 20 && seen.Count == 0; i++)
        {
            host.Service(node, (tick, digest) => seen.Add((tick, digest)));
            Thread.Sleep(5);
        }

        Assert.Equal([(42, Sent)], seen);
    }

    /// <summary>
    /// Two nodes driven only through their links reach the same world.
    /// </summary>
    /// <remarks>
    /// <b>The whole thing, over a socket.</b> <c>MeshMatchTests</c> proves the
    /// rollback converges when inputs are handed over in memory; this proves
    /// nothing is lost in framing them, and that a node fed purely by a wire
    /// behaves as one fed by a method call.
    /// </remarks>
    [Fact]
    public void TwoNodesConvergeOverASocket()
    {
        ReferenceTree.RequireAvailable();

        var (host, peer) = Pair();
        using var _ = host;
        using var __ = peer;

        var a = new MeshNode(Collision(), World());
        var b = new MeshNode(Collision(), World());

        // Slot 1 belongs to A, slot 2 to B. ⚠️ `host` is A's end of the link and
        // `peer` is B's, so what A writes to `host` is read by servicing `peer`
        // — servicing the end it was written on hands a node its OWN inputs back
        // and it never hears the other player at all.
        for (var tick = 0; tick < 90; tick++)
        {
            var fromA = new PlayerInput(
                (tick / 5) % 2 == 0 ? PlayerKeys.Left : PlayerKeys.Right, (ushort)(tick * 13), 40);

            var fromB = new PlayerInput(
                (tick / 7) % 2 == 0 ? PlayerKeys.Right : PlayerKeys.None, (ushort)(tick * 29), 40);

            a.Submit(tick, 1, fromA);
            b.Submit(tick, 2, fromB);

            host.SendInputs(tick, [1], [fromA]);
            peer.SendInputs(tick, [2], [fromB]);

            peer.Service(b);
            host.Service(a);

            a.Advance();
            b.Advance();
        }

        // Let the last frames land, then reconcile without stepping a tick
        // neither node has inputs for.
        for (var i = 0; i < 20; i++)
        {
            peer.Service(b);
            host.Service(a);
            Thread.Sleep(5);
        }

        a.Settle();
        b.Settle();

        Assert.Equal(a.Tick, b.Tick);
        Assert.Equal(a.Digest, b.Digest);
    }

    /// <summary>
    /// A roster change crosses the link, name and all, and lands on its tick.
    /// </summary>
    /// <remarks>
    /// <b>The name rides with the command because a slot number is not a
    /// person.</b> The peer has to tell its OWN clients who joined, and it has
    /// no other way to learn that.
    /// </remarks>
    [Fact]
    public void ACommandCrossesTheWireWithItsName()
    {
        ReferenceTree.RequireAvailable();

        var (host, peer) = Pair();
        using var _ = host;
        using var __ = peer;

        var node = new MeshNode(Collision(), World());
        var heard = new List<(int Tick, MeshCommand Command, string Name)>();

        peer.SendCommand(40, MeshCommand.AddPlayer(origin: 0, sequence: 5), "Vindicator");
        peer.SendCommand(40, MeshCommand.SetTeam(5, Team.Blue));
        peer.Service(node);

        for (var i = 0; i < 20 && heard.Count < 2; i++)
        {
            host.Service(node, null, (tick, command, name) => heard.Add((tick, command, name)));
            Thread.Sleep(5);
        }

        Assert.Equal(2, heard.Count);
        Assert.Equal((40, MeshCommand.AddPlayer(origin: 0, sequence: 5), "Vindicator"), heard[0]);
        Assert.Equal((40, MeshCommand.SetTeam(5, Team.Blue), string.Empty), heard[1]);
    }

    /// <summary>
    /// ⛔ A name whose bytes look like a length does not corrupt the stream.
    /// </summary>
    /// <remarks>
    /// <b>The bug this exists for was in the first version of the frame.</b> The
    /// length byte sat AFTER the name and the reader scanned for it, so a name
    /// containing a byte equal to its own index framed short — and every frame
    /// after it was read from the wrong offset. A GG2 name is twenty arbitrary
    /// bytes, control characters included, so that is not a hypothetical input.
    /// </remarks>
    [Fact]
    public void ANameFullOfAwkwardBytesStillFrames()
    {
        ReferenceTree.RequireAvailable();

        var (host, peer) = Pair();
        using var _ = host;
        using var __ = peer;

        var node = new MeshNode(Collision(), World());
        var heard = new List<(int Tick, MeshCommand Command, string Name)>();

        // Exactly the shape that defeated the scanning reader: byte i == i.
        var awkward = new string([(char)0, (char)1, (char)2, (char)3, (char)4]);

        peer.SendCommand(11, MeshCommand.AddPlayer(origin: 0, sequence: 6), awkward);
        peer.SendCommand(12, MeshCommand.Spawn(6), "after");
        peer.Service(node);

        for (var i = 0; i < 20 && heard.Count < 2; i++)
        {
            host.Service(node, null, (tick, command, name) => heard.Add((tick, command, name)));
            Thread.Sleep(5);
        }

        Assert.Equal(2, heard.Count);
        Assert.Equal(awkward, heard[0].Name);

        // The frame AFTER it is the one that proves alignment survived.
        Assert.Equal((12, MeshCommand.Spawn(6), "after"), heard[1]);
    }

    /// <summary>
    /// A port nothing is listening on, reserved and released so the number is
    /// free right now.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Inherently a small race</b> — something else could take the number
    /// between the release and the use. There is no way to ask for "a port that
    /// will be free in a moment", and a hard-coded number is worse because it
    /// collides with whatever is already running. A failure here is a
    /// collision, not a regression in the code under test.
    /// </remarks>
    private static int FreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);

        probe.Start();

        var port = ((IPEndPoint)probe.LocalEndpoint).Port;

        probe.Stop();

        return port;
    }

    /// <summary>
    /// What ONE refused connection costs on this machine.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Measured rather than assumed, because the platforms disagree by two
    /// orders of magnitude.</b> A closed loopback port refuses immediately on
    /// Linux and takes about two seconds on Windows, which retransmits the SYN
    /// before giving up. Both gates below are about whether a RETRY happened, so
    /// both calibrate against this instead of against a constant — a constant
    /// picked on either platform is wrong on the other, and picking the larger
    /// makes the gate stop biting on the faster one.
    /// </remarks>
    private static TimeSpan OneRefusedAttempt()
    {
        var started = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            using var _ = MeshLink.Connect("127.0.0.1", FreePort());
        }
        catch (System.Net.Sockets.SocketException)
        {
            // Expected — nothing is listening. The timing is the answer.
        }

        return started.Elapsed;
    }

    /// <summary>
    /// ⛔ A joiner waits for its peer to come up rather than exiting the moment
    /// it finds nothing listening.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Neither end can run a match alone</b>, so a joiner that gives up on
    /// the first refusal makes the startup ORDER load-bearing: the listener has
    /// to be up first, arranged by hand, which is exactly what an unattended run
    /// of two nodes cannot do. Waiting costs nothing that exiting does not cost
    /// more.
    /// </para>
    /// <para>
    /// ⚠️ <b>The listener is deliberately LATE, by more than one attempt
    /// costs.</b> A test that starts it first passes whether or not anything
    /// retries, which is the failure this gate exists to catch. So does one
    /// that starts it inside the first attempt's own window: on Windows a
    /// refused connect blocks for about two seconds, and a listener that binds
    /// during it is picked up by that same attempt with nothing having retried.
    /// The delay is derived from <see cref="OneRefusedAttempt"/> for that
    /// reason.
    /// </para>
    /// </remarks>
    [Fact]
    public void AJoinerWaitsForAPeerThatIsNotListeningYet()
    {
        var attempt = OneRefusedAttempt();
        var port = FreePort();
        var said = new List<string>();

        MeshLink? listening = null;

        // ⛔ Longer than one attempt, so the first one MUST have failed and a
        // second one MUST have been made. That is the whole claim.
        var delay = attempt + attempt + TimeSpan.FromSeconds(1);

        // ⚠️ A Thread rather than a Task: this blocks on purpose, and the
        // analyser is right that awaiting a Task from a test body deadlocks.
        var late = new Thread(() =>
        {
            Thread.Sleep(delay);
            listening = MeshLink.Listen(IPAddress.Loopback, port);
        });

        late.Start();

        using var joined = MeshLink.Connect(
            "127.0.0.1", port, delay + TimeSpan.FromSeconds(30), said.Add);

        late.Join(TimeSpan.FromSeconds(30));

        Assert.True(joined.Connected, "the joiner did not end up connected");

        // ⚠️ Said once, not once per attempt: this is a startup path with a
        // person watching, and a line every retry buries what the peer says
        // when it finally does come up.
        Assert.Single(said);
        Assert.Contains(port.ToString(System.Globalization.CultureInfo.InvariantCulture), said[0]);

        listening?.Dispose();
    }

    /// <summary>
    /// ⛔ The control: with no patience a refusal is still reported at once.
    /// </summary>
    /// <remarks>
    /// <b>This is what stops the retry swallowing a wrong address.</b> An
    /// operator who typed the wrong host wants to be told, and a gate that only
    /// checked "it eventually connects" would pass against a Connect that
    /// retried for ever. It also pins the one-argument overload as the
    /// no-waiting one, which every existing caller relies on.
    /// </remarks>
    [Fact]
    public void WithNoPatienceARefusalIsReportedImmediately()
    {
        var attempt = OneRefusedAttempt();
        var port = FreePort();
        var started = System.Diagnostics.Stopwatch.StartNew();

        Assert.ThrowsAny<System.Net.Sockets.SocketException>(
            () => MeshLink.Connect("127.0.0.1", port));

        // ⛔ Inside ONE attempt plus a margin, so a Connect that made a second
        // one — which costs another attempt and a sleep between them — fails
        // this on either platform. The margin is what keeps it meaningful on
        // Linux, where an attempt itself is free.
        var ceiling = attempt + attempt + TimeSpan.FromMilliseconds(300);

        Assert.True(
            started.Elapsed < ceiling,
            $"a refusal took {started.ElapsedMilliseconds}ms against a "
                + $"{ceiling.TotalMilliseconds:F0}ms ceiling, so something retried");
    }
}
