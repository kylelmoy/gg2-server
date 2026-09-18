using System.Net;
using Gg2.Bots;
using Gg2.Maps;
using Gg2.Mesh;
using Gg2.Protocol;
using Gg2.Server;
using Gg2.Simulation;
using Xunit;

namespace Gg2.Tests;

/// <summary>
/// A real client joining a server that is one node of a mesh.
/// </summary>
/// <remarks>
/// <para>
/// <b>The point where the rollback engine meets a stock GG2 client.</b> A roster
/// place is no longer this server's to give: it comes from a command both nodes
/// apply at an agreed tick, so the joiner waits while the peer is told, and only
/// then does anything about them reach the wire.
/// </para>
/// <para>
/// ⛔ The roster and the world must agree slot for slot. A state update's player
/// count comes from the ROSTER and each block from the world, so a roster longer
/// than the world asks for a character that does not exist — and a client aborts
/// on a count it did not expect.
/// </para>
/// </remarks>
public sealed class MeshJoinTests
{
    /// <summary>
    /// A peer for the server under test: a bare node on the other end of a real
    /// socket, which is all the server needs to have somebody to agree with.
    /// </summary>
    private sealed class Peer : IDisposable
    {
        private readonly MeshLink _link;
        private readonly LevelMap _map;

        /// <summary>
        /// Starts at tick 0 and is REBUILT at the server's tick once it says
        /// what that is — the same thing MeshSession does, because a node that
        /// counted its own ticks would schedule for moments the other had
        /// already passed.
        /// </summary>
        public MeshNode Node { get; private set; }

        private bool _synchronised;

        public Peer(int port, LevelMap map)
        {
            _link = MeshLink.Connect("127.0.0.1", port);
            _map = map;

            Node = new MeshNode(
                new CollisionWorld(map), Start(), new MeshOptions(), new SpawnPoints(map));
        }

        /// <summary>Slot 0 only — the same world the server starts from.</summary>
        private static WorldState Start()
        {
            var state = default(WorldState);
            state.AddPlayer(0);

            return state;
        }

        /// <summary>
        /// One tick, driven by the harness so the two nodes stay on the same
        /// tick.
        /// </summary>
        /// <remarks>
        /// ⚠️ <b>Lockstep rather than a timer.</b> A harness tick runs as fast as
        /// the machine allows; a peer on its own 5ms loop falls thousands of
        /// ticks behind within a second, and every command then arrives for a
        /// moment far outside its window. Two real servers both pace at 30 Hz,
        /// which is what stepping here reproduces.
        /// </remarks>
        public void Tick()
        {
            _link.Service(Node, null, (tick, command, _) => Node.Schedule(tick, command));

            if (!_synchronised)
            {
                if (_link.PeerOrigin is not { } theirs)
                    return;

                Node = new MeshNode(
                    new CollisionWorld(_map), Start(), new MeshOptions(),
                    new SpawnPoints(_map), startTick: (int)theirs);

                _synchronised = true;
            }

            Node.Advance();
        }

        public bool Has(int slot) => _synchronised && Node.State.HasPlayer[slot];

        public bool IsBot(int slot) => _synchronised && Node.State.Players[slot].IsBot;

        public int OriginOf(int slot) => Node.OriginOf(slot);

        public Team TeamOf(int slot) => Node.State.Players[slot].Team;

        public void Dispose() => _link.Dispose();
    }

    /// <summary>A bot that never presses anything — this is about the roster.</summary>
    private sealed class Idle : IInputSource
    {
        public PlayerInput Poll(in WorldView world, int slot, int tick) => default;
    }

    private static LevelMap Map() =>
        LevelMap.FromPng("ctf_truefort", ReferenceTree.ReadMap("ctf_truefort")).ForArea(1);

    /// <summary>
    /// ⛔ The gate: a client joins, and both the server and its peer end up with
    /// that player in the same roster place.
    /// </summary>
    [Fact]
    public void AClientCanJoinAMeshedServerAndThePeerLearnsAboutThem()
    {
        using var harness = new ServerHarness(options =>
        {
            options.MeshEnabled = true;
            options.MeshListenPort = 0;
        });

        using var peer = new Peer(harness.Server.MeshPort, Map());
        harness.AfterTick = peer.Tick;

        // The link has to be up, and the two nodes on the same tick, before a
        // join can be scheduled over it.
        harness.Tick(20);

        // The whole handshake, and the join that follows it — which now waits
        // for a tick both nodes agreed on rather than landing at once.
        harness.JoinClient("Vindicator");

        var slot = -1;

        for (var i = 1; i < harness.Server.RosterCount; i++)
        {
            if (harness.Server.NameOf(i) == "Vindicator")
                slot = i;
        }

        Assert.True(slot > 0, "the joiner is not in the roster under their own name");

        // Run on until the peer reaches the tick the join was agreed for.
        for (var i = 0; i < 60 && !peer.Has(slot); i++)
            harness.Tick();

        // The peer has the same player in the same place, which is the whole
        // claim: a roster place agreed across a link.
        Assert.True(peer.Has(slot), "the peer never heard about the join");
        Assert.Equal(0, peer.OriginOf(slot));
    }

    /// <summary>
    /// A standalone server is unchanged: the join is immediate and nothing waits
    /// for anybody.
    /// </summary>
    /// <remarks>
    /// <b>The control.</b> Every assertion above would also pass against a
    /// server that ignored the mesh entirely and admitted the client at once —
    /// this is what says the waiting is real, and it is the same harness so the
    /// difference is the option and nothing else.
    /// </remarks>
    [Fact]
    public void AStandaloneServerAdmitsAClientImmediately()
    {
        using var harness = new ServerHarness();

        harness.JoinClient("Vindicator");

        Assert.Equal("Vindicator", harness.Server.NameOf(harness.Server.RosterCount - 1));
    }

    /// <summary>
    /// ⛔ A meshed client can pick a team, and the peer applies the same change.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Joining is not playing.</b> A client that can reach the roster but
    /// whose team change is silently reverted on the next copy from the node is
    /// worse than one that was refused: it looks like it works. A team change
    /// mutates the world outside the tick, so in a mesh it has to be a scheduled
    /// command like a join.
    /// </para>
    /// <para>
    /// ⚠️ What this does NOT assert is autobalance: a meshed change runs
    /// <c>PlayerCommands.ChangeTeam</c>, not the balancing variant, so a meshed
    /// server does not rebalance on a team change the way a standalone one does.
    /// Both nodes agree, which is what matters for the mesh; the difference from
    /// standalone is real and written down rather than discovered.
    /// </para>
    /// </remarks>
    [Fact]
    public void AMeshedClientCanPickATeamAndThePeerAgrees()
    {
        using var harness = new ServerHarness(options =>
        {
            options.MeshEnabled = true;
            options.MeshListenPort = 0;
        });

        using var peer = new Peer(harness.Server.MeshPort, Map());
        harness.AfterTick = peer.Tick;

        harness.Tick(20);

        var client = harness.JoinClient("Vindicator");

        var slot = -1;

        for (var i = 1; i < harness.Server.RosterCount; i++)
        {
            if (harness.Server.NameOf(i) == "Vindicator")
                slot = i;
        }

        Assert.True(slot > 0);

        client.SendChangeTeam(Team.Red);

        // The change waits for its agreed tick, like the join did.
        for (var i = 0; i < MeshSession.CommandLead + 40; i++)
            harness.Tick();

        Assert.Equal(Team.Red, harness.Server.TeamOf(slot));
        Assert.Equal(Team.Red, peer.TeamOf(slot));
    }

    /// <summary>
    /// ⛔ A meshed server's own bot is agreed with the peer, plays, and is still
    /// there a hundred ticks later.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The tail of this test is the part that bites.</b> A bot added the
    /// standalone way writes the roster and the world directly — and in a mesh
    /// the world is COPIED FROM THE NODE at every tick, so such a bot exists for
    /// one tick and is then erased by a world that never heard of it. Asserting
    /// only that the bot appears would pass against exactly that: the ticks
    /// afterwards are what say it is real.
    /// </para>
    /// <para>
    /// ⚠️ <b>And that it is ALIVE.</b> A slot in the roster is not a bot playing;
    /// a character in the world is, and it only gets one because
    /// <c>CompleteMeshBot</c> schedules the real <c>ChangeTeam</c>, which arms
    /// the respawn.
    /// </para>
    /// </remarks>
    [Fact]
    public void AMeshedServersBotIsAgreedWithThePeerAndKeepsPlaying()
    {
        using var harness = new ServerHarness(options =>
        {
            options.MeshEnabled = true;
            options.MeshListenPort = 0;
        });

        using var peer = new Peer(harness.Server.MeshPort, Map());
        harness.AfterTick = peer.Tick;

        harness.Tick(20);

        var landed = -1;

        var returned = harness.Server.AddBot(
            "[BOT] scout", Team.Red, PlayerClass.Scout, new Idle(),
            slot => landed = slot);

        // ⛔ There is no slot to give: a roster place is the mesh's to agree, and
        // a caller that used this return value would be naming slot -1.
        Assert.Equal(GameServer.MeshPending, returned);
        Assert.Equal(-1, landed);

        for (var i = 0; i < MeshSession.CommandLead + 80; i++)
        {
            harness.Tick();

            if (landed > 0 && peer.IsBot(landed) && harness.Server.IsAlive(landed))
                break;
        }

        Assert.True(landed > 0, "the mesh never gave the bot a slot");
        Assert.True(harness.Server.EntryOf(landed).IsBot);
        Assert.Equal(Team.Red, harness.Server.TeamOf(landed));

        Assert.True(peer.Has(landed), "the peer never heard about the bot");
        Assert.True(peer.IsBot(landed), "the peer reads this node's bot as a person");
        Assert.Equal(Team.Red, peer.TeamOf(landed));
        Assert.True(harness.Server.IsAlive(landed), "the bot never got a character");

        harness.Tick(100);

        Assert.True(peer.Has(landed), "the bot vanished from the peer");
        Assert.True(peer.IsBot(landed), "the bot flag did not survive");
        Assert.Equal(Team.Red, harness.Server.TeamOf(landed));
        Assert.True(harness.Server.IsAlive(landed), "the bot lost its character");
    }

    /// <summary>
    /// ⛔ A meshed bot cannot be removed, and the server SAYS so instead of
    /// corrupting the roster.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A departure is the half of the roster a mesh cannot yet agree.</b>
    /// Removing locally would compact this node's roster while the node's world
    /// still held the player, and the two would then name different people at
    /// every slot above the hole. Scheduling a <c>RemovePlayer</c> is not enough
    /// either: a command names a SLOT, a removal renumbers every slot above it,
    /// and two removals in flight at once means the second was numbered against
    /// a roster the first has already changed.
    /// </para>
    /// <para>
    /// ⚠️ <b>The gate is that the refusal is STATED.</b> A silent refusal and a
    /// silent corruption look identical from outside the process, and this
    /// became reachable the moment a meshed server could have bots at all — the
    /// population manager removes one whenever a person takes its place.
    /// </para>
    /// </remarks>
    [Fact]
    public void AMeshedBotCannotLeaveAndTheServerSaysSo()
    {
        using var harness = new ServerHarness(options =>
        {
            options.MeshEnabled = true;
            options.MeshListenPort = 0;
        });

        using var peer = new Peer(harness.Server.MeshPort, Map());
        harness.AfterTick = peer.Tick;

        harness.Tick(20);

        var landed = -1;

        harness.Server.AddBot(
            "[BOT] scout", Team.Red, PlayerClass.Scout, new Idle(), slot => landed = slot);

        for (var i = 0; i < MeshSession.CommandLead + 80 && landed < 0; i++)
            harness.Tick();

        Assert.True(landed > 0);

        var before = harness.Server.RosterCount;

        harness.Server.RemoveBot(landed);

        // Still there, on both sides, and the roster did not shrink under the
        // world — which is the corruption being refused.
        Assert.Equal(before, harness.Server.RosterCount);
        Assert.True(harness.Server.EntryOf(landed).IsBot);

        harness.Tick(20);

        Assert.True(peer.Has(landed));
        Assert.Equal(before, harness.Server.RosterCount);

        // ⛔ And it was said out loud. A gap recorded only in a code comment is a
        // gap nobody is tracking.
        Assert.Contains(
            harness.Log.Warnings,
            line => line.Contains("cannot be removed while meshed", StringComparison.Ordinal));
    }
}
