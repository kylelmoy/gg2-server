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
            _link.Service(
                Node, null, (tick, command, _) => Node.Schedule(tick, command), Adopt);

            // A joiner simulates nothing until it has been handed the match.
            if (!_synchronised)
                return;

            // ⚠️ And then it CATCHES UP, the same as MeshSession: a node left
            // behind by the transfer schedules its commands against its own
            // clock, and they arrive in the peer's past.
            if (_link.PeerOrigin is { } theirs)
            {
                var behind = Math.Min((int)theirs - Node.Tick, new MeshOptions().MaxRollbackTicks - 1);

                for (var i = 0; i < behind; i++)
                    Node.Advance();
            }

            Node.Advance();
        }

        /// <summary>
        /// Takes on the match the server is already running — the same thing
        /// <see cref="MeshSession.Adopt"/> does, and returning the new node for
        /// the same reason: the inputs that follow a snapshot in one read belong
        /// to it, and handed to the old node they are lost for good.
        /// </summary>
        private MeshNode? Adopt(byte[] bytes)
        {
            if (_synchronised || MeshSnapshot.Decode(bytes) is not { } snapshot)
                return null;

            Node = new MeshNode(
                new CollisionWorld(_map), snapshot.State, new MeshOptions(),
                new SpawnPoints(_map), startTick: snapshot.Tick);

            Node.Adopt(snapshot.Members);
            _synchronised = true;

            return Node;
        }

        /// <summary>Schedules a command on the server as a real peer would.</summary>
        public void Send(int tick, in MeshCommand command, string name)
        {
            _link.SendCommand(tick, command, name);
            Node.Schedule(tick, command);
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

    /// <summary>
    /// Gives the far end a player who is ALIVE and never presses anything, so
    /// the server can never confirm a tick.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A peer that has a player and sends no inputs for them.</b> The server
    /// guesses that player's keys every tick and never hears the real ones, so
    /// no tick of its is ever certain — which is the state a held event has to
    /// survive without being published.
    /// </para>
    /// <para>
    /// ⛔ <b>The team change is the point, not scaffolding.</b>
    /// <c>MeshNode.IsCertain</c> waits on the input of every player who was
    /// ALIVE at the start of the tick, not everybody in the roster — a
    /// spectator presses nothing the world can see, so a node that waited for
    /// one would confirm nothing for as long as somebody was watching. Without
    /// the change this player has no character and blocks nothing.
    /// </para>
    /// </remarks>
    private static void AddSilentPeerPlayer(ServerHarness harness, Peer peer)
    {
        peer.Send(
            peer.Node.Tick + MeshSession.CommandLead,
            MeshCommand.AddPlayer(origin: 1, sequence: 1),
            "Silent");

        var slot = -1;

        // ⚠️ The slot is RESOLVED from the SERVER's roster, never assumed and
        // never read off the peer. A join takes the lowest free place, so it
        // depends on how many bots this server filled. Naming slot 1 changed a
        // BOT's team instead and left the silent player a spectator, blocking
        // nothing.
        for (var i = 0; i < MeshSession.CommandLead + 40 && slot < 0; i++)
        {
            harness.Tick();
            slot = SlotOf(harness, "Silent");
        }

        Assert.True(slot > 0, "the peer's join never reached the server's roster");

        peer.Send(
            peer.Node.Tick + MeshSession.CommandLead,
            MeshCommand.ChangeTeam(slot, Team.Red),
            "Silent");

        for (var i = 0; i < MeshSession.CommandLead + 30; i++)
            harness.Tick();
    }

    /// <summary>
    /// Where a player is in the roster right now, or -1.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Re-resolved at every use, never held across ticks.</b> The
    /// population manager removes a bot when a person takes its place, and a
    /// removal renumbers every slot above it — so an index read forty ticks ago
    /// names somebody else, or nobody. Measured: holding one here made this test
    /// fail intermittently on whichever tick the roster happened to shrink.
    /// </remarks>
    private static int SlotOf(ServerHarness harness, string name)
    {
        for (var at = 1; at < harness.Server.RosterCount; at++)
        {
            if (harness.Server.NameOf(at) == name)
                return at;
        }

        return -1;
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
    /// ⛔ A meshed bot leaves, the peer agrees, and the roster still matches the
    /// world slot for slot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A departure is the half of the roster that cannot be done locally.</b>
    /// Removing here would compact this node's roster while the node's world
    /// still held the player, and the two would then name different people at
    /// every slot above the hole — counts agreeing while every entry above it is
    /// somebody else.
    /// </para>
    /// <para>
    /// ⚠️ <b>The survivors are checked by NAME, not by count.</b> A roster that
    /// lost the wrong entry is exactly as long as one that lost the right entry,
    /// so a length assertion passes against the corruption this is about.
    /// </para>
    /// </remarks>
    [Fact]
    public void AMeshedBotLeavesAndTakesItsOwnPlaceWithIt()
    {
        using var harness = new ServerHarness(options =>
        {
            options.MeshEnabled = true;
            options.MeshListenPort = 0;
        });

        using var peer = new Peer(harness.Server.MeshPort, Map());
        harness.AfterTick = peer.Tick;

        harness.Tick(20);

        // Three, so there is somebody above the leaver to be mistaken for them.
        var slots = new int[3];
        var names = new[] { "[BOT] one", "[BOT] two", "[BOT] three" };

        for (var i = 0; i < names.Length; i++)
        {
            var at = i;

            harness.Server.AddBot(
                names[i], Team.Red, PlayerClass.Scout, new Idle(), slot => slots[at] = slot);
        }

        for (var i = 0; i < MeshSession.CommandLead + 80 && slots[2] == 0; i++)
            harness.Tick();

        Assert.All(slots, slot => Assert.True(slot > 0));

        var leaver = slots[0];
        var before = harness.Server.RosterCount;

        harness.Server.RemoveBot(leaver);

        // ⛔ Not gone yet: the place goes when the command lands, on both nodes
        // at the same tick. A server that took it here would be a tick ahead of
        // its own node.
        Assert.Equal(before, harness.Server.RosterCount);

        for (var i = 0; i < MeshSession.CommandLead + 40 && harness.Server.RosterCount == before; i++)
            harness.Tick();

        Assert.Equal(before - 1, harness.Server.RosterCount);

        // ⛔ The two who stayed are the two who did not leave, and they shifted
        // down into the hole. This is the assertion a trailing trim fails.
        Assert.Equal("[BOT] two", harness.Server.NameOf(leaver));
        Assert.Equal("[BOT] three", harness.Server.NameOf(leaver + 1));
        Assert.DoesNotContain(
            names[0],
            Enumerable.Range(0, harness.Server.RosterCount).Select(harness.Server.NameOf));

        // The peer removed the same player, so both worlds are the same length.
        Assert.False(peer.Has(harness.Server.RosterCount));
        Assert.True(peer.Has(harness.Server.RosterCount - 1));

        // And the roster still agrees with the world slot for slot — a roster
        // longer than the world asks a client for a character that does not
        // exist.
        for (var slot = 1; slot < harness.Server.RosterCount; slot++)
            Assert.True(peer.Has(slot), $"the peer has no player in slot {slot}");
    }

    /// <summary>
    /// ⛔ A node whose peer has gone quiet publishes nothing irreversible, and
    /// goes on publishing positions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The holding rule, read off the wire.</b> A death, a capture or an
    /// intel grab cannot be taken back on a stock client, so a node may only
    /// send one once the tick that produced it can no longer be re-simulated
    /// into a tick where it did not happen. A tick is certain when every live
    /// player's real input for it has been heard — so a peer with a player who
    /// never presses anything makes every tick uncertain for ever.
    /// </para>
    /// <para>
    /// ⚠️ <b>Counted as MESSAGES a client received, not as a server-side
    /// counter.</b> A counter incremented where the events are drained says the
    /// drain happened, which is true of a server that drains and then broadcasts
    /// the provisional events anyway — measured: that mutation passed against
    /// the counter and fails against this.
    /// </para>
    /// <para>
    /// ⚠️ <b>The control is the same server before the silent player arrives</b>,
    /// where every player is local and every tick confirms at once. Without it
    /// this passes against a server that had simply stopped broadcasting, which
    /// is why the position stream is asserted to keep flowing too.
    /// </para>
    /// </remarks>
    [Fact]
    public void ASilentPeerHoldsEveryEventAndStopsNothingElse()
    {
        const string map = "gg_debug";

        using var harness = new ServerHarness(options =>
        {
            options.Map = map;
            options.MeshEnabled = true;
            options.MeshListenPort = 0;

            // Without the graphs the bots never meet and never fire, and an
            // empty fingerprint reads exactly like holding.
            options.NavCacheDirectory = MapDirectory.ResolveNav();
            options.Bots = BotPopulationOptions.Default with
            {
                Enabled = true,
                FillToPlayers = 6,
                MaxBots = 6,
                MinHumans = 0,
            };
        });

        using var peer = new Peer(harness.Server.MeshPort, Map());
        harness.AfterTick = peer.Tick;

        harness.Tick(20);

        var client = harness.JoinClient("watcher");
        var reader = new BroadcastReader(LevelMap.FromPng(map, ReferenceTree.ReadMap(map)));

        // Let the bots reach each other and start shooting.
        harness.Tick(400);
        reader.Drain(client);
        harness.Tick(200);

        var fighting = Irreversible(reader.Drain(client));

        Assert.True(
            fighting > 0,
            "nothing irreversible happened while everything was confirmed, so this test "
            + "cannot say anything about holding");

        // Now give the peer a player who is alive and never presses anything.
        AddSilentPeerPlayer(harness, peer);

        var silent = SlotOf(harness, "Silent");

        Assert.True(silent > 0, "the silent player left the roster again");
        Assert.True(harness.Server.IsAlive(silent), "the peer's player never got a character");

        // Everything up to the silence, discarded.
        reader.Drain(client);

        // ⚠️ MEASURED ONLY WHILE THE SILENT PLAYER IS ALIVE, and that is the
        // claim rather than a convenience. A tick waits on the inputs of players
        // who were ALIVE at its start, so a silent player who is shot stops
        // blocking anything and confirmation correctly resumes — which is right,
        // and made a fixed window fail on whichever run the bots got to them.
        var held = new List<ServerMessage>();
        var window = 0;

        for (var i = 0; i < 200; i++)
        {
            harness.Tick();

            var at = SlotOf(harness, "Silent");

            if (at < 0 || !harness.Server.IsAlive(at))
                break;

            held.AddRange(reader.Drain(client));
            window++;
        }

        Assert.True(window >= 40, $"the silent player only survived {window} ticks, which is not "
            + "long enough to say anything about holding");

        // ⛔ Not one tick became certain, so nothing that cannot be unsaid was
        // sent.
        Assert.Equal(0, Irreversible(held));

        // ⚠️ And the server did not simply stop: the positions kept going, which
        // is the provisional stream the whole design exists to keep.
        Assert.True(
            held.Count(message => message.Type == MessageType.QuickUpdate) > 0,
            "the server stopped sending positions, which is not holding");
    }

    /// <summary>
    /// The messages a rewind could unmake and a stock client could not take
    /// back.
    /// </summary>
    private static int Irreversible(IEnumerable<ServerMessage> messages) =>
        messages.Count(message => message.Type
            is MessageType.WeaponFire
            or MessageType.PlayerDeath
            or MessageType.PlayerSpawn);

    /// <summary>Reads framed messages out of a client's byte stream.</summary>
    private sealed class BroadcastReader
    {
        private readonly ServerStream _stream = new();
        private readonly ReceiveBuffer _buffer = new();

        public BroadcastReader(LevelMap map) => _stream.SetShape(GameModes.ShapeOf(map));

        public List<ServerMessage> Drain(LoopbackClient client)
        {
            client.Poll();
            _buffer.Append(client.Take(client.Received.Count));

            var messages = new List<ServerMessage>();

            while (_stream.TryRead(_buffer, out var message))
                messages.Add(message);

            return messages;
        }
    }

    /// <summary>
    /// ⛔ A peer that connects after the match started is handed it, and agrees
    /// about every player.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Without this a mesh only works if both nodes start together.</b> A node
    /// that connects mid-match began from whatever world it was built with and
    /// hears only what is scheduled from then on — so a server that already had
    /// players has a roster its new peer does not share, and the two then
    /// disagree about which slot every later join takes.
    /// </para>
    /// <para>
    /// ⚠️ <b>The roster is checked player by player, not by count.</b> Two nodes
    /// with the same number of players and different people in them is exactly
    /// the failure this exists to stop, and it is the one a length assertion
    /// cannot see.
    /// </para>
    /// </remarks>
    [Fact]
    public void APeerConnectingMidMatchIsHandedIt()
    {
        using var harness = new ServerHarness(options =>
        {
            options.MeshEnabled = true;
            options.MeshListenPort = 0;
        });

        // A match that is already underway: three bots with places, teams and
        // characters, none of which the peer will have seen happen.
        var slots = new int[3];
        var names = new[] { "[BOT] one", "[BOT] two", "[BOT] three" };

        for (var i = 0; i < names.Length; i++)
        {
            var at = i;

            harness.Server.AddBot(
                names[i], Team.Red, PlayerClass.Scout, new Idle(), slot => slots[at] = slot);
        }

        harness.Tick(MeshSession.CommandLead + 120);

        Assert.All(slots, slot => Assert.True(slot > 0));

        // ⛔ Only NOW does the peer arrive.
        using var peer = new Peer(harness.Server.MeshPort, Map());
        harness.AfterTick = peer.Tick;

        for (var i = 0; i < 80 && !peer.Has(slots[2]); i++)
            harness.Tick();

        // Every one of them, in the place this server put them, owned by this
        // server.
        foreach (var slot in slots)
        {
            Assert.True(peer.Has(slot), $"the peer was never told about slot {slot}");
            Assert.Equal(0, peer.OriginOf(slot));
            Assert.Equal(Team.Red, peer.TeamOf(slot));
        }

        // ⛔ And nobody the server does not have. A snapshot that added players
        // without clearing what the node already had would pass everything above.
        Assert.False(
            peer.Has(harness.Server.RosterCount),
            "the peer has a player the server does not");

        // ⛔ The strongest form: the two worlds are the same world. A digest
        // covers positions, velocities, hp, the scoreboard and the RNG seed, so
        // it fails on a peer that has the right roster and the wrong match.
        //
        // ⚠️ AT A TICK, never live. A peer adopts the world as of the tick the
        // snapshot was taken and is a few ticks behind from then on, so its live
        // state differs from the server's for entirely correct reasons.
        harness.Tick(30);

        var common = peer.Node.Tick - 5;

        Assert.NotNull(peer.Node.DigestAt(common));
        Assert.Equal(harness.Server.MeshDigestAt(common), peer.Node.DigestAt(common));
    }

    /// <summary>
    /// A disconnecting client leaves a meshed match, rather than standing there
    /// for ever.
    /// </summary>
    /// <remarks>
    /// <b>The path a real server actually takes.</b> The socket is gone at once
    /// — nothing can be done about that — but the roster place waits for the
    /// tick both nodes remove it at, which is the same lead a join already waits
    /// out.
    /// </remarks>
    [Fact]
    public void ADisconnectingClientLeavesAMeshedMatch()
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

        var before = harness.Server.RosterCount;

        client.Dispose();

        for (var i = 0; i < MeshSession.CommandLead + 60 && harness.Server.RosterCount == before; i++)
            harness.Tick();

        Assert.Equal(before - 1, harness.Server.RosterCount);
        Assert.DoesNotContain(
            "Vindicator",
            Enumerable.Range(0, harness.Server.RosterCount).Select(harness.Server.NameOf));

        // The peer let them go too, at the tick the command named.
        Assert.False(peer.Has(slot));
    }
}
