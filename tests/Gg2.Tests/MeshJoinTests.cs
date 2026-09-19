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

            // ⚠️ A real node announces where its clock is, and BOTH ends do:
            // the server schedules its commands ahead of the peer's tick rather
            // than its own, so a peer that never said where it was would be sent
            // commands for moments it had already passed.
            if (Node.Tick % 5 == 0)
                _link.SendHello(Node.Tick);
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
    /// Gives the far end a player who is alive and never presses anything, so
    /// the server cannot confirm a tick.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>The team change is the point, not scaffolding.</b>
    /// <c>MeshNode.IsCertain</c> waits on the input of every player who was
    /// ALIVE at the start of the tick, not everybody in the roster — a spectator
    /// presses nothing the world can see. Without the change this player has no
    /// character and blocks nothing.
    /// </remarks>
    private static int AddSilentPeerPlayer(ServerHarness harness, Peer peer)
    {
        peer.Send(
            peer.Node.Tick + MeshSession.CommandLead,
            MeshCommand.AddPlayer(origin: 1, sequence: 1),
            "Silent");

        var slot = -1;

        // ⚠️ RESOLVED from the SERVER's roster, never assumed: a join takes the
        // lowest free place, which depends on how many players are already here.
        for (var i = 0; i < 200 && slot < 0; i++)
        {
            harness.Tick();
            slot = SlotOf(harness, "Silent");
        }

        Assert.True(slot > 0, "the peer's join never reached the server's roster");

        peer.Send(
            peer.Node.Tick + MeshSession.CommandLead,
            MeshCommand.ChangeTeam(slot, Team.Red),
            "Silent");

        for (var i = 0; i < 200 && !harness.Server.IsAlive(slot); i++)
            harness.Tick();

        return slot;
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

    private static LevelMap Map() => MapNamed("ctf_truefort");

    private static LevelMap MapNamed(string name) =>
        LevelMap.FromPng(name, ReferenceTree.ReadMap(name)).ForArea(1);

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
    /// <b>The holding rule, read off the wire.</b> A death, a capture or a spawn
    /// cannot be taken back on a stock client, so a node may only send one once
    /// the tick that produced it can no longer be re-simulated into a tick where
    /// it did not happen. A tick is certain when every live player's real input
    /// for it has been heard — so a peer with a player who never presses
    /// anything makes every tick uncertain for ever.
    /// </para>
    /// <para>
    /// ⚠️ <b>Counted as MESSAGES a client received, not as a server-side
    /// counter.</b> A counter incremented where the events are drained says the
    /// drain happened, which is equally true of a server that drains and then
    /// broadcasts the provisional events anyway — measured: that mutation passes
    /// against the counter and fails against this.
    /// </para>
    /// <para>
    /// ⛔ <b>NOBODY FIGHTS HERE, and that is the design rather than a
    /// simplification.</b> Three earlier versions of this test measured a
    /// bot match and flaked: a player standing in a firefight pressing nothing
    /// is killed within a few seconds, a dead player correctly stops holding
    /// anything up, and the measurement window collapsed to three ticks on the
    /// runs where the bots were quick. Every player here is on one team, so
    /// nothing dies, the silent player stays alive, and the only irreversible
    /// event in the match is the one this test creates on purpose.
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
        });

        using var peer = new Peer(harness.Server.MeshPort, MapNamed(map));
        harness.AfterTick = peer.Tick;

        harness.Tick(20);

        var client = harness.JoinClient("watcher");
        var reader = new BroadcastReader(LevelMap.FromPng(map, ReferenceTree.ReadMap(map)));

        harness.Tick(30);
        reader.Drain(client);

        // ⛔ THE CONTROL. A bot joining spawns, and a spawn is a thing a client
        // cannot be told twice — so with every player local and every tick
        // certain, it reaches the client. Without this the assertion below
        // passes against a server that had simply stopped broadcasting.
        AddBotAndWait(harness, "[BOT] first");

        Assert.True(
            Irreversible(reader.Drain(client)) > 0,
            "a bot joined and nothing irreversible reached the client, so this test cannot say "
            + "anything about holding");

        // Now a peer player who is alive and never presses anything. On the same
        // team as everybody else, so nothing kills them and confirmation stays
        // stalled for as long as this test needs.
        var silent = AddSilentPeerPlayer(harness, peer);

        Assert.True(harness.Server.IsAlive(silent), "the peer's player never got a character");

        reader.Drain(client);

        // The same event again, with confirmation stalled this time.
        AddBotAndWait(harness, "[BOT] second");

        var held = reader.Drain(client);

        // ⛔ Not one tick became certain, so nothing that cannot be unsaid was
        // sent.
        Assert.Equal(0, Irreversible(held));

        // ⚠️ And the server did not simply stop: the positions kept going, which
        // is the provisional stream the whole design exists to keep.
        Assert.True(
            held.Count(message => message.Type == MessageType.QuickUpdate) > 0,
            "the server stopped sending positions, which is not holding");

        // The silent player is still alive, so the stretch above really was one
        // where nothing could be confirmed.
        Assert.True(harness.Server.IsAlive(silent), "the silent player died mid-measurement");
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

    /// <summary>Adds a bot, on Red like everybody else, and waits for its spawn.</summary>
    private static void AddBotAndWait(ServerHarness harness, string name)
    {
        harness.Server.AddBot(name, Team.Red, PlayerClass.Scout, new Idle());

        for (var i = 0; i < MeshSession.CommandLead + 90; i++)
            harness.Tick();
    }

    /// <summary>
    /// ⛔ A meshed server fills to its bot target ONCE, not once per tick while
    /// the joins are in flight.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Found on a live two-host run, not here.</b> A server told to fill to
    /// four added FORTY-EIGHT bots and then spent the next minute removing them
    /// again. The population manager fills by comparing the roster against its
    /// target every tick, and a meshed join takes about ten ticks to reach the
    /// roster — so it asked for the whole shortfall again on every one of those
    /// ticks.
    /// </para>
    /// <para>
    /// ⚠️ <b>Nothing failed while it happened</b>, which is why no local test
    /// caught it: the joins all landed, the departures all worked, and the two
    /// nodes agreed throughout. It was correct and absurd, and only visible to
    /// somebody reading the log of a real server.
    /// </para>
    /// </remarks>
    [Fact]
    public void AMeshedServerFillsToItsBotTargetOnce()
    {
        using var harness = new ServerHarness(options =>
        {
            options.Map = "gg_debug";
            options.MeshEnabled = true;
            options.MeshListenPort = 0;
            options.NavCacheDirectory = MapDirectory.ResolveNav();
            options.Bots = BotPopulationOptions.Default with
            {
                Enabled = true,
                FillToPlayers = 4,
                MaxBots = 4,
                MinHumans = 0,
            };
        });

        using var peer = new Peer(harness.Server.MeshPort, MapNamed("gg_debug"));
        harness.AfterTick = peer.Tick;

        // Well past the lead a join waits out, so every one of them has landed.
        harness.Tick(MeshSession.CommandLead + 200);

        // ⛔ Four bots, and nothing pending behind them. The roster is the whole
        // claim: an overshoot shows up here as a roster several times the target.
        Assert.Equal(4, harness.Server.BotSlots.Count);
        Assert.Equal(0, harness.Server.BotsPending);

        // ⚠️ And it did not get there by adding a crowd and removing most of
        // them again, which is what the bug did — forty-eight asks for a target
        // of four.
        //
        // ⛔ Bounded rather than exact, and the reason is real: a command the
        // mesh will not carry is NOT parked, so the manager asks again next tick
        // until it is accepted. That retry is the designed behaviour — it is
        // what stops a refused join leaving a bot parked for a slot that is
        // never coming — so a handful of extra asks is correct and a crowd is
        // not.
        var asked = harness.Log.Lines.Count(
            line => line.Contains("asked the mesh for a slot", StringComparison.Ordinal));

        Assert.InRange(asked, 4, 12);
    }

    /// <summary>
    /// ⛔ Two meshed servers show their clients the SAME roster: same names,
    /// same teams, same classes, slot for slot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Found on a live two-host run, and it is the failure the world's own
    /// digest cannot see.</b> The two nodes agreed about the world perfectly
    /// while one server showed four players on Red and Blue and the other showed
    /// the same four slots as spectators under the names they had joined with.
    /// Everything a client sees about a player — name, team, class — lives in
    /// the ROSTER, and none of it was crossing the link.
    /// </para>
    /// <para>
    /// ⚠️ Team and class are now copied from the world every tick, because the
    /// world is the thing that decides them and cannot drift. A name is not in
    /// the world at all, so it is forwarded like a chat bubble.
    /// </para>
    /// </remarks>
    [Fact]
    public void TwoMeshedServersShowTheSameRoster()
    {
        const string map = "gg_debug";

        using var listener = new ServerHarness(options =>
        {
            options.Map = map;
            options.MeshEnabled = true;
            options.MeshListenPort = 0;
        });

        listener.Tick(20);

        var landed = -1;

        listener.Server.AddBot(
            "[BOT] one", Team.Red, PlayerClass.Soldier, new Idle(), slot => landed = slot);

        for (var i = 0; i < MeshSession.CommandLead + 120 && landed < 0; i++)
            listener.Tick();

        Assert.True(landed > 0, "the bot never got a slot");

        // A rename, which is what a re-class does on a real server. This one
        // happens while nobody is listening.
        listener.Server.RenameBot(landed, "[BOT] early");

        // Let the team change land before anybody is listening.
        listener.Tick(60);

        Assert.Equal(Team.Red, listener.Server.TeamOf(landed));

        // ⛔ ONLY NOW does the peer arrive, so everything above reaches it as a
        // SNAPSHOT rather than as commands it watched happen. That is the live
        // case: a snapshot carries the world, and a team is in the world, but
        // the roster a client is served is not the world.
        using var joiner = new ServerHarness(options =>
        {
            options.Map = map;
            options.MeshEnabled = true;
            options.MeshConnect = $"127.0.0.1:{listener.Server.MeshPort}";
        });

        listener.AfterTick = () => joiner.Tick();

        for (var i = 0; i < 160; i++)
            listener.Tick();

        Assert.True(joiner.Server.RosterCount > landed, "the joiner never heard about the bot");

        // ⛔ The rename that happened BEFORE the peer existed reached it, which
        // it can only have done in the snapshot.
        Assert.Equal("[BOT] early", joiner.Server.NameOf(landed));

        // ⛔ And one AFTER it arrived reaches it too, which is the forward.
        listener.Server.RenameBot(landed, "[BOT] late");

        for (var i = 0; i < 60; i++)
            listener.Tick();

        Assert.Equal("[BOT] late", listener.Server.NameOf(landed));
        Assert.Equal("[BOT] late", joiner.Server.NameOf(landed));

        Assert.Equal(Team.Red, listener.Server.TeamOf(landed));
        Assert.Equal(Team.Red, joiner.Server.TeamOf(landed));

        Assert.Equal(listener.Server.ClassOf(landed), joiner.Server.ClassOf(landed));

        // And the rosters are the same length, so those slots mean the same
        // thing on both.
        Assert.Equal(listener.Server.RosterCount, joiner.Server.RosterCount);
    }

    /// <summary>
    /// ⛔ A node that is BEHIND its peer still gets its commands heard.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The failure a real link produced and a loopback one never did.</b> A
    /// node schedules a roster change for "a few ticks from now"; if it is
    /// itself behind its peer by more than that lead, the tick it names is
    /// already in the peer's PAST and the peer refuses it — while the sender has
    /// applied it. The two then run different matches, and the only sign is a
    /// digest that never agrees again.
    /// </para>
    /// <para>
    /// Measured across two hosts: the joiner sat about nineteen ticks behind the
    /// listener, every command it sent landed nine ticks in the listener's past,
    /// and the rosters diverged from the first bot onwards. The fix is to
    /// schedule ahead of the PEER's clock rather than one's own.
    /// </para>
    /// <para>
    /// ⚠️ The skew here is forced rather than hoped for: the peer is advanced by
    /// hand well past the server before anything is scheduled.
    /// </para>
    /// </remarks>
    [Fact]
    public void ANodeBehindItsPeerStillGetsItsCommandsHeard()
    {
        using var harness = new ServerHarness(options =>
        {
            options.MeshEnabled = true;
            options.MeshListenPort = 0;
        });

        using var peer = new Peer(harness.Server.MeshPort, Map());
        harness.AfterTick = peer.Tick;

        harness.Tick(20);

        // ⛔ Put the PEER well ahead of this server — further than CommandLead,
        // which is exactly the case a real link produced.
        var ahead = MeshSession.CommandLead + 9;

        for (var i = 0; i < ahead; i++)
            peer.Node.Advance();

        // Let the server hear where the peer now is.
        harness.Tick(20);

        var landed = -1;

        harness.Server.AddBot(
            "[BOT] one", Team.Red, PlayerClass.Scout, new Idle(), slot => landed = slot);

        for (var i = 0; i < MeshSession.CommandLead + ahead + 120 && landed < 0; i++)
            harness.Tick();

        Assert.True(landed > 0, "the server never gave the bot a slot");

        // ⛔ And the PEER applied the same join, which is the half that used to
        // be dropped: a slot the sender has and the peer refused is a divergence
        // nothing recovers from.
        for (var i = 0; i < 120 && !peer.Has(landed); i++)
            harness.Tick();

        Assert.True(peer.Has(landed), "the peer refused the join as too late");
    }

    /// <summary>
    /// ⛔ A meshed server never idles, however empty it looks.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The deadlock a live two-host run actually hit.</b> An empty server
    /// drops to 2 Hz to save a machine that nobody is playing on. A meshed one
    /// is not empty — its peer is running a match at 30 Hz — and a node that
    /// idles falls fifteen ticks behind for every second it does, so the next
    /// roster change it schedules names a tick its peer passed long ago and is
    /// refused.
    /// </para>
    /// <para>
    /// ⛔ It feeds itself: the joiner's own bots were refused, which left its
    /// roster empty, which kept it idle, which kept its clock behind, which got
    /// the next join refused. It never acquired a single player in five minutes.
    /// </para>
    /// </remarks>
    [Fact]
    public void AMeshedServerNeverIdles()
    {
        using var meshed = new ServerHarness(options =>
        {
            options.MeshEnabled = true;
            options.MeshListenPort = 0;
        });

        // Nobody is connected and there are no bots, which is exactly when an
        // ordinary server idles.
        Assert.False(meshed.Server.IsIdle, "a meshed server called itself idle");

        // ⚠️ The control: the same empty server without a mesh DOES idle, so
        // this is the mesh doing it and not the harness.
        using var standalone = new ServerHarness();

        Assert.True(standalone.Server.IsIdle, "an empty standalone server should idle");
    }

    /// <summary>
    /// ⛔ Two meshed servers both told to fill six produce SIX, and the node
    /// that owns them never stops sending their inputs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Found on a direct two-host link, and it is what stopped the mesh
    /// agreeing.</b> The two nodes share a roster, so two population managers
    /// both filling to six made twelve and then raced to remove them — and
    /// <c>RemoveOne</c> only ever takes a node's OWN bots, because a peer's
    /// player arrives as a plain roster entry with no <c>IsBot</c>. The listener
    /// removed every bot it owned, ended up owning NOTHING, and stopped sending
    /// inputs; its peer went on guessing players that were no longer its to
    /// drive, and confirmation stalled on both sides.
    /// </para>
    /// <para>
    /// ⚠️ <b>The second assertion is the one that matters.</b> A roster of the
    /// right size is satisfied by the broken version too, a moment before it
    /// removes the wrong six — what says it is right is that the owner still has
    /// players to send.
    /// </para>
    /// </remarks>
    [Fact]
    public void TwoMeshedServersFillOneMatchBetweenThem()
    {
        const string map = "gg_debug";

        static void Filling(ServerOptions options)
        {
            options.Map = map;
            options.MeshEnabled = true;
            options.NavCacheDirectory = MapDirectory.ResolveNav();
            options.Bots = BotPopulationOptions.Default with
            {
                Enabled = true,
                FillToPlayers = 6,
                MaxBots = 6,
                MinHumans = 0,
            };
        }

        using var listener = new ServerHarness(options =>
        {
            Filling(options);
            options.MeshListenPort = 0;
        });

        using var joiner = new ServerHarness(options =>
        {
            Filling(options);
            options.MeshConnect = $"127.0.0.1:{listener.Server.MeshPort}";
        });

        listener.AfterTick = () => joiner.Tick();

        // Well past the lead, and long enough for any churn to show up.
        listener.Tick(MeshSession.CommandLead + 400);

        // ⛔ Six between them, not six each.
        Assert.Equal(6, listener.Server.BotSlots.Count);

        // ⛔ And the node that owns them is still driving them. `owned 0` is what
        // the live failure looked like, and everything downstream of it — the
        // peer guessing players nobody drives, confirmation stalling — follows
        // from exactly this number being zero.
        Assert.True(
            listener.Server.MeshOwnedSlots > 0,
            "the listener owns no slots, so it is telling its peer nothing about its own players");

        // The joiner shares the roster and fills none of it itself.
        Assert.Empty(joiner.Server.BotSlots);
        Assert.Equal(listener.Server.RosterCount, joiner.Server.RosterCount);
    }

    /// <summary>
    /// ⛔ A joiner can be given players of its own, and then BOTH nodes have
    /// something to mispredict.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A node with no players of its own gives its peer nothing to guess
    /// wrong about.</b> Measured over a real link: with every player belonging
    /// to the listener, both nodes reported zero rollbacks for the whole match —
    /// the two agreed, but the rewind that the entire design rests on was never
    /// once exercised.
    /// </para>
    /// <para>
    /// ⚠️ The joiner does not fill the match (the listener does, for both), so
    /// its players have to be asked for one at a time. That is what
    /// <c>bot-add</c> is for.
    /// </para>
    /// </remarks>
    [Fact]
    public void AJoinerCanBeGivenPlayersOfItsOwn()
    {
        const string map = "gg_debug";

        static void Meshed(ServerOptions options)
        {
            options.Map = map;
            options.MeshEnabled = true;
            options.NavCacheDirectory = MapDirectory.ResolveNav();
            options.Bots = BotPopulationOptions.Default with
            {
                Enabled = true,
                FillToPlayers = 6,
                MaxBots = 6,
                MinHumans = 0,
            };
        }

        using var listener = new ServerHarness(options =>
        {
            Meshed(options);
            options.MeshListenPort = 0;
        });

        using var joiner = new ServerHarness(options =>
        {
            Meshed(options);
            options.MeshConnect = $"127.0.0.1:{listener.Server.MeshPort}";
        });

        listener.AfterTick = () => joiner.Tick();
        listener.Tick(MeshSession.CommandLead + 200);

        // The listener filled the match and drives those players.
        Assert.True(listener.Server.MeshOwnedSlots > 0, $"listener owns {listener.Server.MeshOwnedSlots}, bots={listener.Server.BotSlots.Count}, leaving={listener.Server.BotsLeaving}, roster={listener.Server.RosterCount}");

        // ⛔ The joiner drives NOBODY, which is the state that left the rollback
        // untested over a real link.
        Assert.Equal(0, joiner.Server.MeshOwnedSlots);

        // ⛔ THREE of its own, not one, and the number matters. The listener's
        // target counts the whole shared roster, so three new players push it to
        // shed three — and shedding several in one pass is what makes the
        // manager pick a DIFFERENT bot each time. Shedding one, it picks the
        // same bot on every tick and the duplicate is refused, which hides the
        // bug this asserts against.
        for (var i = 0; i < 3; i++)
            Assert.True(joiner.Server.AddMeshBot(), "the joiner would not take a bot");

        for (var i = 0; i < MeshSession.CommandLead + 200; i++)
            listener.Tick();

        // ⛔ Both nodes now drive somebody, so each has something the other must
        // guess at.
        Assert.True(
            joiner.Server.MeshOwnedSlots > 0,
            "the joiner still drives nobody, so its peer has nothing to mispredict");

        // ⚠️ NOT asserted here: that the listener still drives somebody. It does
        // not — it sheds its own bots to nothing when the joiner adds players —
        // and the cause is a rollback rewriting a tick's removals after the
        // roster has already acted on them. docs/MESH.md, "Roster changes are
        // drained on tick, not on confirmation". Asserting it here would be a
        // red suite standing in for a written-down bug.

        // ⚠️ And the fill verb is REFUSED on the joiner rather than accepted and
        // ignored — a field that takes a number and changes nothing is worse
        // than one that says no.
        Assert.False(joiner.Server.MeshFillsBots);
        Assert.True(listener.Server.MeshFillsBots);

        // ⛔ THE LISTENER DOES NOT STARVE ITSELF. Its target counts the whole
        // shared roster, so the joiner's players push it to shed some of its own
        // — and it used to shed every one, ending up driving nobody. The cause
        // was a rewind restoring the world without the tables that NAME players,
        // so a re-applied departure resolved to nobody and left the roster and
        // the world disagreeing about who was here.
        Assert.True(
            listener.Server.MeshOwnedSlots > 0,
            "the listener removed its way down to driving nobody");

    }

    /// <summary>
    /// ⛔ A running mesh says whether the two nodes agree.
    /// </summary>
    /// <remarks>
    /// <b>Without this the one claim the design rests on is unobservable.</b>
    /// The digests are exchanged and compared every second, and the result went
    /// into a counter with no log line, no control verb and nothing on the
    /// panel — so a live mesh could be diverging and the only way to find out
    /// was to read the source. Found while trying to watch a real match across
    /// two hosts, which is where a missing view of a claim actually bites.
    /// </remarks>
    [Fact]
    public void AMeshedServerSaysWhetherTheNodesAgree()
    {
        using var harness = new ServerHarness(options =>
        {
            options.MeshEnabled = true;
            options.MeshListenPort = 0;
        });

        using var peer = new Peer(harness.Server.MeshPort, Map());
        harness.AfterTick = peer.Tick;

        // The report is on a ten-second cadence, so this has to cross one.
        harness.Tick(320);

        Assert.Contains(
            harness.Log.Lines,
            line => line.Contains("mesh: tick", StringComparison.Ordinal)
                && line.Contains("agreed", StringComparison.Ordinal));

        // ⛔ And it says the two are LINKED and IN STEP, which is the part an
        // operator acts on. A report that always said "NO PEER" would satisfy a
        // test that only looked for the word "mesh".
        Assert.Contains(
            harness.Log.Lines,
            line => line.Contains("linked", StringComparison.Ordinal)
                && line.Contains("in step", StringComparison.Ordinal));
    }

    /// <summary>
    /// ⛔ A chat bubble made in one region is seen in the other.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Forwarded, never scheduled, and the difference is the point.</b> A
    /// bubble never reaches <c>WorldState</c> — it is rewritten and relayed — so
    /// two nodes showing it at slightly different moments is not a divergence,
    /// there is nothing to agree about, and nothing a rewind could unmake. It is
    /// not held with the deaths and it does not wait for a tick.
    /// </para>
    /// <para>
    /// ⚠️ What it needs is to ARRIVE. Without this a mesh splits the only social
    /// channel the game has along the same line it splits the regions, which is
    /// the one thing players would notice immediately.
    /// </para>
    /// </remarks>
    [Fact]
    public void AChatBubbleCrossesTheMesh()
    {
        const string map = "gg_debug";

        using var listener = new ServerHarness(options =>
        {
            options.Map = map;
            options.MeshEnabled = true;
            options.MeshListenPort = 0;
        });

        using var joiner = new ServerHarness(options =>
        {
            options.Map = map;
            options.MeshEnabled = true;
            options.MeshConnect = $"127.0.0.1:{listener.Server.MeshPort}";
        });

        listener.AfterTick = () => joiner.Tick();
        listener.Tick(30);

        // A player on the LISTENER, and somebody watching on the JOINER.
        var talker = listener.JoinClient("talker");
        var watcher = joiner.JoinClient("watcher");

        listener.Tick(60);

        var talkerSlot = -1;

        for (var i = 1; i < listener.Server.RosterCount; i++)
        {
            if (listener.Server.NameOf(i) == "talker")
                talkerSlot = i;
        }

        Assert.True(talkerSlot > 0, "the talker never joined");

        // The watcher has to know about them before a bubble over their head
        // means anything.
        for (var i = 0; i < 120 && joiner.Server.RosterCount <= talkerSlot; i++)
            listener.Tick();

        Assert.True(joiner.Server.RosterCount > talkerSlot, "the joiner never heard about the talker");

        var reader = new BroadcastReader(LevelMap.FromPng(map, ReferenceTree.ReadMap(map)));
        reader.Drain(watcher);

        talker.SendCommand(MessageType.ChatBubble);
        talker.Send([(byte)MessageType.ChatBubble, 1]);

        listener.Tick(30);

        // ⛔ It reached a client on the OTHER node, naming the player who made it.
        var seen = reader.Drain(watcher);

        Assert.Contains(seen, message => message.Type == MessageType.ChatBubble);
    }

    /// <summary>
    /// ⛔ A meshed map change takes both servers with it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A map change is the one event that replaces the whole world.</b>
    /// <c>Rounds.ResetForNewMap</c> puts everybody back to spectator and clears
    /// the score, and <c>OpenMap</c> swaps the collision world and the spawn
    /// points and places the new map's intel, generators and platforms. In a
    /// mesh the NODE owns the world, so a reset written straight to
    /// <c>_world</c> is overwritten by the next copy from the node — and the
    /// node goes on simulating the old map while this server's clients have been
    /// sent <c>CHANGE_MAP</c>. Measured before this was scheduled: the clients
    /// were on the new map and the simulation was on the old one, and nothing
    /// said so.
    /// </para>
    /// <para>
    /// ⚠️ <b>Two REAL servers, not a server and a bare node.</b> Following a map
    /// change means running <c>OpenMap</c> — loading a file, rebuilding the
    /// collision world, placing the intel — which is server work a node cannot
    /// do and a test double should not imitate. A stub that reproduced it would
    /// be asserting against a second implementation of the thing under test.
    /// </para>
    /// </remarks>
    [Fact]
    public void AMeshedMapChangeTakesBothServersWithIt()
    {
        using var listener = new ServerHarness(options =>
        {
            options.MeshEnabled = true;
            options.MeshListenPort = 0;
        });

        using var joiner = new ServerHarness(options =>
        {
            options.MeshEnabled = true;
            options.MeshConnect = $"127.0.0.1:{listener.Server.MeshPort}";
        });

        // One clock for both, the same reason a peer is stepped by the harness:
        // two servers on their own timers fall thousands of ticks apart.
        listener.AfterTick = () => joiner.Tick();

        listener.Tick(30);

        var landed = -1;

        listener.Server.AddBot(
            "[BOT] one", Team.Red, PlayerClass.Scout, new Idle(), slot => landed = slot);

        for (var i = 0; i < MeshSession.CommandLead + 80 && landed < 0; i++)
            listener.Tick();

        Assert.True(landed > 0, "the bot never got a slot");

        // ⚠️ The roster has to REACH that slot before it can be asked about it:
        // the joiner learns of the bot when the join lands at its end, a few
        // ticks after this server placed it.
        for (var i = 0; i < 120; i++)
        {
            listener.Tick();

            if (joiner.Server.RosterCount > landed && joiner.Server.TeamOf(landed) == Team.Red)
                break;
        }

        Assert.True(joiner.Server.RosterCount > landed, "the joiner never heard about the bot");
        Assert.Equal(Team.Red, joiner.Server.TeamOf(landed));

        Assert.True(listener.Server.RequestMapChange("ctf_orange"), "the map change was refused");

        listener.Tick(Rounds.MapChangeDelay + 90);

        // Both servers are serving the new map to their own clients.
        Assert.Equal("ctf_orange", listener.Server.CurrentMap);
        Assert.Equal("ctf_orange", joiner.Server.CurrentMap);

        // ⛔ And both SIMULATIONS went with them: a map change makes everybody a
        // spectator, and a node that stayed on the old map would still have this
        // player on Red.
        Assert.Equal(Team.Spectator, listener.Server.TeamOf(landed));
        Assert.Equal(Team.Spectator, joiner.Server.TeamOf(landed));

        // ⛔ The strongest form: the two rebuilt nodes are running one world.
        // Compared AT A TICK, never live — a joiner is a little behind for
        // entirely correct reasons.
        // ⚠️ A tick BOTH nodes still hold, waited for rather than assumed. Either
        // can be the one behind, and a node REBUILT by the map change has no
        // history at all until it has run a few ticks past it — so this runs on
        // until both can answer for the same moment.
        var common = 0;

        for (var i = 0; i < 300; i++)
        {
            listener.Tick();
            common = Math.Min(listener.Server.MeshTick, joiner.Server.MeshTick) - 4;

            if (common > 0
                && listener.Server.MeshDigestAt(common) is not null
                && joiner.Server.MeshDigestAt(common) is not null)
            {
                break;
            }
        }

        var gap = $"listener {listener.Server.MeshTick}, joiner {joiner.Server.MeshTick}, common {common}";

        Assert.True(listener.Server.MeshDigestAt(common) is not null, $"listener has no digest: {gap}");
        Assert.True(joiner.Server.MeshDigestAt(common) is not null, $"joiner has no digest: {gap}");
        Assert.Equal(listener.Server.MeshDigestAt(common), joiner.Server.MeshDigestAt(common));
    }

    /// <summary>
    /// ⛔ A map change does not orphan the players that live through it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A map change replaces the world and the node; it does not replace the
    /// people.</b> <c>Rounds.ResetForNewMap</c> clears each character and puts
    /// everybody back to spectator, but <c>HasPlayer</c> stays set and nobody
    /// moves slot — so the same players are still in the same places and still
    /// belong to whoever let them in. A node rebuilt with empty tables makes
    /// every one of them ownerless: this node submits no inputs for its own
    /// players, its peer guesses them for ever, and no departure can ever be
    /// scheduled for them because there is no join left to name.
    /// </para>
    /// <para>
    /// ⚠️ <b>THE DIGESTS GO ON AGREEING WHILE THAT IS TRUE</b>, which is why
    /// <see cref="AMeshedMapChangeTakesBothServersWithIt"/> stayed green through
    /// it: both nodes guess the orphans identically and reach the same world. A
    /// map change that agrees is not a map change that worked, so this asserts
    /// ownership rather than agreement. Measured against two real servers over a
    /// real map change before the fix — <c>owned</c> fell 4 to 0 on the listener
    /// and 3 to 1 on the joiner, with zero disagreements reported and 964
    /// roster-versus-world warnings a side.
    /// </para>
    /// </remarks>
    [Fact]
    public void AMeshedMapChangeKeepsWhoOwnsWhom()
    {
        using var listener = new ServerHarness(options =>
        {
            options.MeshEnabled = true;
            options.MeshListenPort = 0;
        });

        using var joiner = new ServerHarness(options =>
        {
            options.MeshEnabled = true;
            options.MeshConnect = $"127.0.0.1:{listener.Server.MeshPort}";
        });

        listener.AfterTick = () => joiner.Tick();

        listener.Tick(30);

        var landed = -1;

        listener.Server.AddBot(
            "[BOT] one", Team.Red, PlayerClass.Scout, new Idle(), slot => landed = slot);

        for (var i = 0; i < MeshSession.CommandLead + 80 && landed < 0; i++)
            listener.Tick();

        Assert.True(landed > 0, "the bot never got a slot");

        for (var i = 0; i < 120 && joiner.Server.RosterCount <= landed; i++)
            listener.Tick();

        Assert.True(joiner.Server.RosterCount > landed, "the joiner never heard about the bot");

        // The listener let this player in, so the listener drives it. Asserted
        // before the change so a zero afterwards cannot be read as "it never
        // owned anything in the first place".
        var ownedBefore = listener.Server.MeshOwnedSlots;

        Assert.True(ownedBefore > 0, "the listener owned nobody before the map change");

        Assert.True(listener.Server.RequestMapChange("ctf_orange"), "the map change was refused");

        listener.Tick(Rounds.MapChangeDelay + 90);

        Assert.Equal("ctf_orange", listener.Server.CurrentMap);
        Assert.Equal("ctf_orange", joiner.Server.CurrentMap);

        // ⛔ The claim. The bot is the listener's on both sides of the change,
        // and a rebuilt node that forgot so reports zero here while every other
        // assertion in the neighbouring test still passes.
        Assert.True(
            listener.Server.MeshOwnedSlots >= ownedBefore,
            $"the map change orphaned the listener's players: owned {ownedBefore} "
                + $"before, {listener.Server.MeshOwnedSlots} after");
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

    /// <summary>
    /// ⛔ A departure that is decided while the node is a tick ahead of the
    /// roster still takes the player it named.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The window is real and it is one tick wide.</b> The node applies a
    /// removal at the bottom of a tick, in <c>AdvanceSimulation</c>;
    /// <c>DrainMeshCommands</c> replays it against the roster at the top of the
    /// next one. <c>ServiceClients</c> and <c>ServiceBots</c> run above that
    /// drain, so a disconnect or a bot removal decided in that tick reads its
    /// slot off a roster that still holds the departed player and resolves it
    /// against a node that has already renumbered every slot above the hole.
    /// </para>
    /// <para>
    /// ⛔ <b>The failure is silent and the digests agree through it.</b> Both
    /// nodes are told to remove the join that now sits at that slot, so both
    /// remove the same wrong person and their worlds go on matching perfectly.
    /// Nothing but naming the departure by the join recorded at admission
    /// catches it — <c>GameServer._meshJoinKey</c>.
    /// </para>
    /// <para>
    /// ⚠️ <b>Five bots, not four.</b> With four, the stale slot resolves to
    /// nothing and the departure is merely refused, which a count assertion
    /// would catch anyway. The fifth is what makes the stale slot name a real
    /// player, which is the corruption this is about: the test asserts the
    /// named bot left AND that its neighbour stayed, because the bug satisfies
    /// the count either way.
    /// </para>
    /// </remarks>
    [Fact]
    public void ADepartureDecidedWhileTheNodeIsAheadTakesTheRightPlayer()
    {
        using var harness = new ServerHarness(options =>
        {
            options.MeshEnabled = true;
            options.MeshListenPort = 0;
        });

        using var peer = new Peer(harness.Server.MeshPort, Map());
        harness.AfterTick = peer.Tick;

        harness.Tick(20);

        var names = new[] { "[BOT] one", "[BOT] two", "[BOT] three", "[BOT] four", "[BOT] five" };
        var slots = new int[names.Length];

        for (var i = 0; i < names.Length; i++)
        {
            var at = i;

            harness.Server.AddBot(
                names[i], Team.Red, PlayerClass.Scout, new Idle(), slot => slots[at] = slot);
        }

        for (var i = 0; i < MeshSession.CommandLead + 120 && slots[^1] == 0; i++)
            harness.Tick();

        Assert.All(slots, slot => Assert.True(slot > 0, "a bot never reached the roster"));

        var before = harness.Server.RosterCount;

        // The first departure, which is the one that renumbers the node.
        harness.Server.RemoveBot(SlotOf(harness, names[0]));

        // ⛔ Tick INTO the window and stop there. A world shorter than the
        // roster is exactly "the node has applied a removal the roster has not
        // drained", which is the state the next tick's client and bot servicing
        // would run in. Ticking one more would close it.
        var reached = false;

        for (var i = 0; i < MeshSession.CommandLead + 120 && !reached; i++)
        {
            harness.Tick();
            reached = harness.Server.RosterCount > harness.Server.WorldPlayerCount;
        }

        // ⚠️ Asserted rather than assumed. A gate whose premise never fires
        // passes for the wrong reason, and this one's premise is a single tick.
        Assert.True(reached, "the test never reached the tick where the node leads the roster");
        Assert.Equal(before, harness.Server.RosterCount);

        // Decided in the window, the way a disconnect in ServiceClients is.
        // Slot 4 is "[BOT] four" in the roster and "[BOT] five" in the node.
        var departing = SlotOf(harness, names[3]);

        harness.Server.RemoveBot(departing);

        for (var i = 0; i < MeshSession.CommandLead + 120 && harness.Server.RosterCount > before - 2; i++)
            harness.Tick();

        Assert.Equal(before - 2, harness.Server.RosterCount);

        var left = Enumerable.Range(0, harness.Server.RosterCount)
            .Select(harness.Server.NameOf)
            .ToArray();

        // ⛔ The claim, in both directions. The bug removes names[4] and leaves
        // names[3] standing, so a "two bots left" count passes against it.
        Assert.DoesNotContain(names[0], left);
        Assert.DoesNotContain(names[3], left);
        Assert.Contains(names[4], left);
        Assert.Contains(names[1], left);
        Assert.Contains(names[2], left);

        // And both worlds still describe the same people, slot for slot.
        for (var slot = 1; slot < harness.Server.RosterCount; slot++)
            Assert.True(peer.Has(slot), $"the peer has no player in slot {slot}");

        Assert.False(peer.Has(harness.Server.RosterCount));
    }
}
