using System.Linq;
using Gg2.Maps;
using Gg2.Mesh;
using Gg2.Protocol;
using Gg2.Server;
using Gg2.Simulation;
using Xunit;

namespace Gg2.Tests;

/// <summary>
/// A roster change scheduled on one node and heard by the other.
/// </summary>
/// <remarks>
/// <b>The piece that stands between a mesh and a server people can join.</b>
/// Until a command crosses the link, a join applied on one node leaves the other
/// simulating a match with one fewer player in it — and unlike a mispredicted
/// key, that never converges.
/// </remarks>
public sealed class MeshSessionTests
{
    private static CollisionWorld Collision() =>
        new(LevelMap.FromPng("ctf_truefort", ReferenceTree.ReadMap("ctf_truefort")));

    private static SpawnPoints Spawns() =>
        new(LevelMap.FromPng("ctf_truefort", ReferenceTree.ReadMap("ctf_truefort")).ForArea(1));

    private static WorldState World()
    {
        var state = default(WorldState);
        state.AddPlayer(0);

        return state;
    }

    /// <summary>A listening node and a joining one, on loopback.</summary>
    private static (MeshSession Host, MeshSession Peer) Pair()
    {
        var listen = new ServerOptions { MeshEnabled = true, MeshListenPort = 0 };
        var host = MeshSession.Start(listen, Collision(), World(), Spawns(), new RecordingLog());

        var join = new ServerOptions
        {
            MeshEnabled = true,
            MeshConnect = $"127.0.0.1:{host.Port}",
        };

        return (host, MeshSession.Start(join, Collision(), World(), Spawns(), new RecordingLog()));
    }

    private static void Pump(MeshSession a, MeshSession b, int ticks)
    {
        var none = new PlayerInput[WorldState.MaxSlots];

        for (var i = 0; i < ticks; i++)
        {
            a.Tick(none, WorldState.MaxSlots);
            b.Tick(none, WorldState.MaxSlots);
            Thread.Sleep(2);
        }
    }

    [Fact]
    public void AJoinScheduledOnOneNodeReachesTheOther()
    {
        ReferenceTree.RequireAvailable();

        var (host, peer) = Pair();
        using var hostLease = host;
        using var peerLease = peer;

        // Let the link come up before anything is said over it.
        Pump(host, peer, 5);

        // ⚠️ The slot is not asked for — it falls out of applying the join, so
        // the issuer only learns it afterwards. That is the real sequence a
        // server follows: schedule the join, wait for it to land, then say
        // anything about the player.
        var at = host.Schedule(MeshCommand.AddPlayer(origin: 0, sequence: 1), "Vindicator");

        Pump(host, peer, MeshSession.CommandLead + 10);

        Assert.True(
            host.Node.TryGetAssignedSlot(0, 1, out var slot),
            "the node that scheduled the join never applied it");

        Assert.True(
            peer.Node.TryGetAssignedSlot(0, 1, out var theirs),
            "the peer never heard about the join");

        Assert.Equal(slot, theirs);
        Assert.True(host.Node.State.HasPlayer[slot]);
        Assert.True(peer.Node.State.HasPlayer[slot]);
        Assert.Equal("Vindicator", peer.NameOf(slot));

        // ...and the peer can mirror it into its own roster, which is what the
        // server above will do with it.
        Assert.Contains(peer.FromPeer, entry => entry.Command.Kind == MeshCommandKind.AddPlayer);
        Assert.True(at > 0);
    }

    /// <summary>
    /// Both nodes apply it at the same tick, so neither simulates a player the
    /// other does not have.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>The assertion that makes this worth having.</b> Both ending up with
    /// the player is not enough: applied at different ticks, the two nodes
    /// simulate different worlds from there on, and the digest check would start
    /// reporting a divergence that nothing can repair.
    /// </remarks>
    [Fact]
    public void BothNodesApplyItOnTheSameTick()
    {
        ReferenceTree.RequireAvailable();

        var (host, peer) = Pair();
        using var hostLease = host;
        using var peerLease = peer;

        Pump(host, peer, 5);
        host.Schedule(MeshCommand.AddPlayer(origin: 0, sequence: 1), "Vindicator");

        int? hostAt = null;
        int? peerAt = null;
        var none = new PlayerInput[WorldState.MaxSlots];

        for (var i = 0; i < MeshSession.CommandLead + 12; i++)
        {
            host.Tick(none, WorldState.MaxSlots);
            peer.Tick(none, WorldState.MaxSlots);

            hostAt ??= host.Node.TryGetAssignedSlot(0, 1, out _) ? host.Node.Tick : null;
            peerAt ??= peer.Node.TryGetAssignedSlot(0, 1, out _) ? peer.Node.Tick : null;

            Thread.Sleep(2);
        }

        Assert.NotNull(hostAt);
        Assert.NotNull(peerAt);
        Assert.Equal(hostAt, peerAt);
    }

    /// <summary>
    /// The two nodes still agree about the world after a roster change.
    /// </summary>
    [Fact]
    public void TheNodesStillAgreeAfterAJoin()
    {
        ReferenceTree.RequireAvailable();

        var (host, peer) = Pair();
        using var hostLease = host;
        using var peerLease = peer;

        Pump(host, peer, 5);

        host.Schedule(MeshCommand.AddPlayer(origin: 0, sequence: 1), "Vindicator");
        Pump(host, peer, MeshSession.CommandLead + 5);

        Assert.True(host.Node.TryGetAssignedSlot(0, 1, out var slot));

        host.Schedule(MeshCommand.SetTeam(slot, Team.Blue));
        host.Schedule(MeshCommand.SetClass(slot, PlayerClass.Scout));
        host.Schedule(MeshCommand.Spawn(slot));

        Pump(host, peer, MeshSession.CommandLead + 25);

        host.Node.Settle();
        peer.Node.Settle();

        var common = Math.Min(host.Node.Tick, peer.Node.Tick) - 1;

        Assert.True(common > MeshSession.CommandLead, "the run was too short to have proved anything");
        Assert.Equal(host.Node.DigestAt(common), peer.Node.DigestAt(common));
    }
}
