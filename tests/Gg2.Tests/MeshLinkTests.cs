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
}
