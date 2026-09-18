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
        var host = MeshSession.Start(listen, Collision(), World(), Spawns());

        var join = new ServerOptions
        {
            MeshEnabled = true,
            MeshConnect = $"127.0.0.1:{host.Port}",
        };

        return (host, MeshSession.Start(join, Collision(), World(), Spawns()));
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
        using var _ = host;
        using var __ = peer;

        // Let the link come up before anything is said over it.
        Pump(host, peer, 5);

        var at = host.Schedule(MeshCommand.AddPlayer(2), "Vindicator");
        host.Schedule(MeshCommand.SetTeam(2, Team.Blue));

        Pump(host, peer, MeshSession.CommandLead + 10);

        Assert.True(host.Node.State.HasPlayer[2], "the node that scheduled it never applied it");
        Assert.True(peer.Node.State.HasPlayer[2], "the peer never heard about the join");

        Assert.Equal(Team.Blue, peer.Node.State.Players[2].Team);
        Assert.Equal("Vindicator", peer.PeerNames[2]);

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
        using var _ = host;
        using var __ = peer;

        Pump(host, peer, 5);
        host.Schedule(MeshCommand.AddPlayer(2), "Vindicator");

        int? hostAt = null;
        int? peerAt = null;
        var none = new PlayerInput[WorldState.MaxSlots];

        for (var i = 0; i < MeshSession.CommandLead + 12; i++)
        {
            host.Tick(none, WorldState.MaxSlots);
            peer.Tick(none, WorldState.MaxSlots);

            hostAt ??= host.Node.State.HasPlayer[2] ? host.Node.Tick : null;
            peerAt ??= peer.Node.State.HasPlayer[2] ? peer.Node.Tick : null;

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
        using var _ = host;
        using var __ = peer;

        Pump(host, peer, 5);

        host.Schedule(MeshCommand.AddPlayer(2), "Vindicator");
        host.Schedule(MeshCommand.SetTeam(2, Team.Blue));
        host.Schedule(MeshCommand.SetClass(2, PlayerClass.Scout));
        host.Schedule(MeshCommand.Spawn(2));

        Pump(host, peer, MeshSession.CommandLead + 30);

        host.Node.Settle();
        peer.Node.Settle();

        var common = Math.Min(host.Node.Tick, peer.Node.Tick) - 1;

        Assert.True(common > MeshSession.CommandLead, "the run was too short to have proved anything");
        Assert.Equal(host.Node.DigestAt(common), peer.Node.DigestAt(common));
    }
}
