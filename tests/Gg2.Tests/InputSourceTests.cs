using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Server;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// The bot seam: an <see cref="IInputSource"/> driving a real server, and a
/// client unable to tell it from a socket.
/// </summary>
/// <remarks>
/// docs/ARCHITECTURE.md, "Bots". The claim is not "a bot can be written" — it is
/// that a bot and a network client go through <em>one</em> path: one input
/// gather, one set of command handlers, one roster. So every assertion here is
/// made from the <b>broadcast stream</b>, decoded by the same
/// <see cref="ServerStream"/> the parity recorder uses, rather than by reaching
/// into the server. If a bot's sentry did not reach the wire, a client would not
/// draw it, and a test that read the world directly would not have noticed.
/// <para>
/// ⚠️ <b>None of this is observable simulation and no fixture can gate it.</b>
/// No recording in this project contains a bot; the reference's own bots write
/// <c>keyState</c> in the same event and the same roster loop that reads a
/// socket (<c>GameServerBeginStep.gml:79-85</c>), so the seam changes nothing a
/// trace could see. These tests are the whole gate.
/// </para>
/// </remarks>
public sealed class InputSourceTests
{
    private const string Map = "ctf_truefort";

    private static LevelMap LoadMap() => LevelMap.FromPng(Map, ReferenceTree.ReadMap(Map));

    /// <summary>Decodes the broadcast the way a real client frames it.</summary>
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

    /// <summary>Presses one fixed input for ever, and counts how often it was asked.</summary>
    private sealed class HeldInput(PlayerInput input) : IInputSource
    {
        public int Polls { get; private set; }

        public PlayerInput Poll(in WorldView world, int slot, int tick)
        {
            Polls++;
            return input;
        }
    }

    /// <summary>An input source that also issues commands, on the tick it is told to.</summary>
    private sealed class Scripted : IInputSource, ICommandSource
    {
        private readonly Queue<PlayerCommand> _pending = new();

        public PlayerInput Input { get; set; }

        public int CommandPolls { get; private set; }

        public void Enqueue(PlayerCommand command) => _pending.Enqueue(command);

        public PlayerInput Poll(in WorldView world, int slot, int tick) => Input;

        public void PollCommands(in WorldView world, int slot, int tick, ref PlayerCommandBuffer commands)
        {
            CommandPolls++;

            while (_pending.Count > 0 && commands.Push(_pending.Peek()))
                _pending.Dequeue();
        }
    }

    /// <summary>One policy for several bots — docs/BOTAI.md item 5.</summary>
    private sealed class Batched : IBatchInputSource
    {
        public List<int[]> Calls { get; } = [];

        public int SingleSlotPolls { get; private set; }

        public PlayerInput Poll(in WorldView world, int slot, int tick)
        {
            SingleSlotPolls++;
            return PlayerInput.None;
        }

        public void PollMany(
            in WorldView world, ReadOnlySpan<int> slots, Span<PlayerInput> outputs, int tick)
        {
            Calls.Add(slots.ToArray());

            // A different aim per slot, so a batch written back in the wrong
            // order fails rather than looking right.
            for (var i = 0; i < slots.Length; i++)
                outputs[i] = PlayerInput.FromAim(PlayerKeys.Right, slots[i] * 10, 0);
        }
    }

    /// <summary>
    /// A bot's keys reach the simulation, and a client sees the result as an
    /// ordinary player moving.
    /// </summary>
    /// <remarks>
    /// The gate for <c>GameServer.GatherInputs</c>. Deleting the poll leaves the
    /// bot standing on its spawn point, which is what a zeroed
    /// <see cref="PlayerInput"/> means — so the assertion is on the character
    /// having <em>moved</em>, not on it existing.
    /// </remarks>
    [Fact]
    public void ABotsKeysReachTheSimulationAndTheWire()
    {
        var map = LoadMap();
        using var harness = new ServerHarness(o => o.Map = Map);
        var observer = harness.JoinClient("observer");
        var reader = new BroadcastReader(map);

        // ⚠️ Drain first. A joining client's FULL_UPDATE is sent to its own
        // socket and sits in its buffer; it was serialised before the bot
        // existed, so reading it later gives an update with no slot for the bot
        // in it at all.
        harness.Tick();
        reader.Drain(observer);

        var source = new HeldInput(PlayerInput.FromAim(PlayerKeys.Right, 0, 0));
        var slot = harness.Server.AddBot("walker", Team.Red, PlayerClass.Scout, source);

        harness.Tick(2);
        var start = NextCharacter(harness, reader, observer, slot);

        harness.Tick(30);
        var later = NextCharacter(harness, reader, observer, slot);

        Assert.True(source.Polls > 0, "the source was never polled");
        Assert.True(
            later.X > start.X,
            $"a bot holding RIGHT did not move: {start.X} then {later.X} wire units");
    }

    /// <summary>
    /// A bot joins the way a client does: <c>PLAYER_JOIN</c>, then the team,
    /// then the class, then a spawn.
    /// </summary>
    /// <remarks>
    /// <c>botAdd.gml</c>'s order, and ⚠️ <b>team before class</b> — the opposite
    /// of the order <c>CompleteJoin</c> replays an existing roster in. Getting
    /// it the other way round is invisible to this port and visible to a client
    /// that applies a class limit as it reads.
    /// </remarks>
    [Fact]
    public void AddingABotAnnouncesItLikeAJoiningClient()
    {
        var map = LoadMap();
        using var harness = new ServerHarness(o => o.Map = Map);
        var observer = harness.JoinClient("observer");
        var reader = new BroadcastReader(map);

        harness.Tick();
        reader.Drain(observer);

        var slot = harness.Server.AddBot("bot", Team.Blue, PlayerClass.Soldier, new HeldInput(PlayerInput.None));
        harness.Tick(2);

        var messages = reader.Drain(observer);
        var kinds = messages
            .Where(m => m.Type is MessageType.PlayerJoin
                or MessageType.PlayerChangeTeam
                or MessageType.PlayerChangeClass
                or MessageType.PlayerSpawn)
            .Select(m => m.Type)
            .ToArray();

        Assert.Equal(
            [
                MessageType.PlayerJoin,
                MessageType.PlayerChangeTeam,
                MessageType.PlayerChangeClass,
                MessageType.PlayerSpawn,
            ],
            kinds);

        var spawn = messages.Single(m => m.Type == MessageType.PlayerSpawn);
        Assert.Equal(slot, spawn.Payload[0]);
    }

    /// <summary>
    /// A bot's command goes through the same handler a client's packet reaches,
    /// broadcast included.
    /// </summary>
    /// <remarks>
    /// ⚠️ The point of asserting on the <em>message</em> is that a second copy
    /// of the handler would pass a test that only checked the world.
    /// <c>botServerActions.gml</c>: a bot "performs them by calling the same
    /// server-side scripts the network path calls".
    /// <para>
    /// The zoom rather than the sentry because its preconditions are a live
    /// sniper and nothing else — <c>serverToggleZoom.gml</c> — so the assertion
    /// is about the seam and not about where on the map the bot is standing.
    /// The sentry, whose preconditions are the awkward ones, is
    /// <see cref="ABotBuildsASentryOnceItIsOutOfItsSpawnRoom"/>.
    /// </para>
    /// </remarks>
    [Fact]
    public void ABotsCommandGoesThroughTheSocketPathsHandler()
    {
        var map = LoadMap();
        using var harness = new ServerHarness(o => o.Map = Map);
        var observer = harness.JoinClient("observer");
        var reader = new BroadcastReader(map);

        var source = new Scripted();
        var slot = harness.Server.AddBot("sniper", Team.Red, PlayerClass.Sniper, source);

        harness.Tick(10);
        reader.Drain(observer);

        source.Enqueue(PlayerCommand.ToggleZoom());
        harness.Tick(2);

        var zoomed = reader.Drain(observer).SingleOrDefault(m => m.Type == MessageType.ToggleZoom);

        Assert.True(source.CommandPolls > 0, "the command source was never polled");
        Assert.NotNull(zoomed);
        Assert.Equal(slot, zoomed.Payload[0]);
    }

    /// <summary>
    /// A bot builds a sentry — and cannot until it has walked out of its own
    /// spawn room.
    /// </summary>
    /// <remarks>
    /// The command the phase's handoff names, and the one whose preconditions
    /// are worth demonstrating from the bot side. ⚠️ <b>Every spawn point on
    /// every map is inside a spawn room</b> and
    /// <c>serverBuildSentry.gml</c>'s guard is
    /// <c>collision_point(x, y, SpawnRoom, 0, 0) &lt; 0</c> — so a freshly
    /// spawned engineer asking to build is refused, every tick, until it has
    /// left. The bot here does what a real one would: walks, and keeps asking.
    /// <para>
    /// That is also the sharper claim. The build only ever succeeds because the
    /// bot's <em>keys</em> moved it out and its <em>command</em> was re-checked
    /// against where the keys put it — both halves of the seam, in one test.
    /// </para>
    /// </remarks>
    [Fact]
    public void ABotBuildsASentryOnceItIsOutOfItsSpawnRoom()
    {
        var map = LoadMap();
        using var harness = new ServerHarness(o => o.Map = Map);
        var observer = harness.JoinClient("observer");
        var reader = new BroadcastReader(map);

        // ⚠️ LEFT, and it is not arbitrary: red spawns on the *right* of
        // ctf_truefort (x ~1608), and an engineer holding RIGHT walks into the
        // back of its own base and stands there inside the spawn room for ever.
        // Measured, by trying it.
        var source = new Scripted { Input = PlayerInput.FromAim(PlayerKeys.Left, 180, 0) };
        var slot = harness.Server.AddBot("engie", Team.Red, PlayerClass.Engineer, source);

        harness.Tick(5);
        reader.Drain(observer);

        ServerMessage? built = null;
        var refusals = 0;

        for (var i = 0; i < 300 && built is null; i++)
        {
            source.Enqueue(PlayerCommand.BuildSentry());
            harness.Tick();

            built = reader.Drain(observer).FirstOrDefault(m => m.Type == MessageType.BuildSentry);

            if (built is null)
                refusals++;
        }

        Assert.True(refusals > 0, "the build was accepted on the spawn point, inside the spawn room");
        Assert.NotNull(built);
        Assert.Equal(slot, built.Payload[0]);
    }

    /// <summary>
    /// A command whose preconditions fail is silence, exactly as it is for a
    /// client that sends the same packet.
    /// </summary>
    /// <remarks>
    /// A scout has no sentry to build. <c>serverBuildSentry.gml</c> guards and
    /// returns; nothing is broadcast and nothing throws. This is the control for
    /// the test above: the seam must not be a way round a precondition.
    /// </remarks>
    [Fact]
    public void ACommandWithFailedPreconditionsIsSilent()
    {
        var map = LoadMap();
        using var harness = new ServerHarness(o => o.Map = Map);
        var observer = harness.JoinClient("observer");
        var reader = new BroadcastReader(map);

        var source = new Scripted();
        harness.Server.AddBot("scout", Team.Red, PlayerClass.Scout, source);

        harness.Tick(40);
        reader.Drain(observer);

        source.Enqueue(PlayerCommand.BuildSentry());
        source.Enqueue(PlayerCommand.ToggleZoom());
        harness.Tick(2);

        var messages = reader.Drain(observer);

        Assert.DoesNotContain(messages, m => m.Type == MessageType.BuildSentry);
        Assert.DoesNotContain(messages, m => m.Type == MessageType.ToggleZoom);
    }

    /// <summary>
    /// A bot changes class through the command channel, and it is announced.
    /// </summary>
    /// <remarks>
    /// The class change is the command with the most behind it — it can kill,
    /// and the death precedes the change message. Here the bot is a spectator
    /// with no character, so what is being gated is that the command reaches
    /// <c>ExecuteChangeClass</c> at all and that the roster moved with it: the
    /// next <c>PLAYER_CHANGECLASS</c> a client sees names the new class.
    /// </remarks>
    [Fact]
    public void ABotChangesClassThroughTheCommandChannel()
    {
        var map = LoadMap();
        using var harness = new ServerHarness(o => o.Map = Map);
        var observer = harness.JoinClient("observer");
        var reader = new BroadcastReader(map);

        var source = new Scripted();
        var slot = harness.Server.AddBot("swap", Team.Spectator, PlayerClass.Scout, source);

        harness.Tick(2);
        reader.Drain(observer);

        source.Enqueue(PlayerCommand.ChangeClass(PlayerClass.Demoman));
        harness.Tick(2);

        var changed = reader.Drain(observer).SingleOrDefault(m => m.Type == MessageType.PlayerChangeClass);

        Assert.NotNull(changed);
        Assert.Equal(slot, changed.Payload[0]);
        Assert.Equal((byte)PlayerClass.Demoman, changed.Payload[1]);
    }

    /// <summary>
    /// A batching source is asked once, with every slot it drives, in roster
    /// order — and never per slot.
    /// </summary>
    /// <remarks>
    /// docs/BOTAI.md item 5. The reason this is gated now, with nothing
    /// implementing the batched form for real, is that item's own warning:
    /// "adding it after every call site assumes per-slot polling is a wide
    /// change". <see cref="Batched.SingleSlotPolls"/> is the control — if the
    /// grouping in <c>GatherInputs</c> is deleted, the per-slot fallback still
    /// works and every other assertion here still passes.
    /// </remarks>
    [Fact]
    public void OnePolicyDrivingSeveralBotsIsAskedOnce()
    {
        using var harness = new ServerHarness(o => o.Map = Map);

        var shared = new Batched();
        var first = harness.Server.AddBot("a", Team.Red, PlayerClass.Scout, shared);
        var second = harness.Server.AddBot("b", Team.Blue, PlayerClass.Scout, shared);

        // A source of its own in between, to prove the grouping picks its slots
        // out rather than taking a contiguous run.
        var alone = new HeldInput(PlayerInput.None);
        var third = harness.Server.AddBot("c", Team.Red, PlayerClass.Scout, alone);
        var fourth = harness.Server.AddBot("d", Team.Blue, PlayerClass.Scout, shared);

        shared.Calls.Clear();
        harness.Tick();

        var call = Assert.Single(shared.Calls);
        Assert.Equal([first, second, fourth], call);
        Assert.Equal(0, shared.SingleSlotPolls);
        Assert.NotEqual(third, call[0]);
    }

    /// <summary>
    /// The batched inputs land in the slots they were decided for.
    /// </summary>
    /// <remarks>
    /// The aim each slot is given in <see cref="Batched.PollMany"/> is a
    /// function of the slot, so an off-by-one write-back is a different aim on
    /// the wire rather than an invisible mix-up.
    /// </remarks>
    [Fact]
    public void ABatchedDecisionLandsInTheRightSlot()
    {
        var map = LoadMap();
        using var harness = new ServerHarness(o => o.Map = Map);
        var observer = harness.JoinClient("observer");
        var reader = new BroadcastReader(map);

        harness.Tick();
        reader.Drain(observer);

        var shared = new Batched();
        var first = harness.Server.AddBot("a", Team.Red, PlayerClass.Scout, shared);
        var second = harness.Server.AddBot("b", Team.Red, PlayerClass.Scout, shared);

        harness.Tick(20);

        var update = NextUpdateWithPositions(harness, reader, observer);

        var one = update.Players[first].Character;
        var two = update.Players[second].Character;

        Assert.NotNull(one);
        Assert.NotNull(two);
        Assert.Equal(PlayerInput.FromAim(PlayerKeys.Right, first * 10, 0).NetAimDirection, one.Value.NetAimDirection);
        Assert.Equal(PlayerInput.FromAim(PlayerKeys.Right, second * 10, 0).NetAimDirection, two.Value.NetAimDirection);
    }

    /// <summary>
    /// A bot gets exactly the socket's flood budget, and no more.
    /// </summary>
    /// <remarks>
    /// <c>processClientCommands.gml</c> completes at most
    /// <see cref="ClientCommands.PerTickLimit"/> commands per player per tick.
    /// Here the limit is the buffer's capacity, so there is nowhere to put an
    /// eleventh and no separate counter to forget.
    /// </remarks>
    [Fact]
    public void ACommandBufferHoldsExactlyTheSocketsPerTickBudget()
    {
        var buffer = default(PlayerCommandBuffer);

        for (var i = 0; i < ClientCommands.PerTickLimit; i++)
            Assert.True(buffer.Push(PlayerCommand.BuildSentry()), $"push {i} was refused");

        Assert.Equal(ClientCommands.PerTickLimit, buffer.Count);
        Assert.False(buffer.Push(PlayerCommand.BuildSentry()));
        Assert.Equal(ClientCommands.PerTickLimit, buffer.Count);

        buffer.Clear();
        Assert.Equal(0, buffer.Count);
        Assert.True(buffer.Push(PlayerCommand.DropIntel()));
        Assert.Equal(PlayerCommandKind.DropIntel, buffer[0].Kind);
    }

    /// <summary>
    /// A socket's input persists between packets rather than being cleared each
    /// tick.
    /// </summary>
    /// <remarks>
    /// ⚠️ <c>processClientCommands.gml:267</c> writes <c>keyState</c> when a
    /// packet arrives and nothing clears it, and the client dead-reckons on the
    /// same assumption — so a lost or late packet must leave the player
    /// pressing what they last pressed. A <see cref="NetworkInputSource"/> that
    /// returned <see cref="PlayerInput.None"/> when nothing new had arrived
    /// would turn every dropped packet into a stutter, and it is the kind of
    /// thing a seam refactor makes easy to get wrong.
    /// </remarks>
    [Fact]
    public void ASocketsLastInputIsHeldUntilTheNextPacket()
    {
        var source = new NetworkInputSource();
        var world = default(WorldState);
        var collision = new CollisionWorld(LoadMap());

        var held = PlayerInput.FromAim(PlayerKeys.Left | PlayerKeys.Jump, 45, 100);
        source.Latest = held;

        for (var tick = 0; tick < 5; tick++)
            Assert.Equal(held, source.Poll(new WorldView(in world, collision), 1, tick));
    }

    /// <summary>Ticks until a position-bearing update arrives, and returns it.</summary>
    private static StateUpdate NextUpdateWithPositions(
        ServerHarness harness, BroadcastReader reader, LoopbackClient client)
    {
        for (var i = 0; i < 20; i++)
        {
            harness.Tick();

            foreach (var message in reader.Drain(client))
            {
                if (message.State is { Tier: MessageType.QuickUpdate or MessageType.FullUpdate } state)
                    return state;
            }
        }

        Assert.Fail("no position-bearing update in 20 ticks");
        return null!;
    }

    private static CharacterBlock NextCharacter(
        ServerHarness harness, BroadcastReader reader, LoopbackClient client, int slot)
    {
        var update = NextUpdateWithPositions(harness, reader, client);
        var character = update.Players[slot].Character;

        Assert.True(character.HasValue, $"slot {slot} has no character");
        return character.Value;
    }
}
