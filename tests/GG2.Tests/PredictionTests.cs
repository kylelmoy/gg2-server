using System.Runtime.InteropServices;
using GG2.Maps;
using GG2.Protocol;
using GG2.Server;
using GG2.Simulation;

namespace GG2.Tests;

/// <summary>
/// Server support for client-side prediction: the welcome, the per-client input
/// queue and the per-tick acknowledgement. docs/PREDICTION.md, whose "Gates"
/// list these are.
/// </summary>
/// <remarks>
/// <b>Every server test reads the WIRE</b>, decoded with <see cref="ServerStream"/>
/// the way a client would, never the server's own counters: the claim is about
/// what a client is told, and which update it is told it for.
/// <para>
/// Each input a test sends carries a distinct aim direction, which is how an
/// update's keys and aim name the input the server applied on that tick
/// without depending on movement.
/// </para>
/// </remarks>
public sealed class PredictionTests
{
    private const string Map = "ctf_2dfort";
    private const ushort AimBase = 1000;

    private static PlayerInput Numbered(uint n, PlayerKeys keys = PlayerKeys.None) =>
        new(keys, (ushort)(AimBase + n), 40);

    /// <summary>A joined, spawned client, its stream framed from the join burst on.</summary>
    private sealed class Player(LoopbackClient client, ServerStream stream, ReceiveBuffer buffer)
    {
        public LoopbackClient Client { get; } = client;

        private uint _sent;

        public uint Sent => _sent;

        public void SendNext(PlayerKeys keys = PlayerKeys.None) =>
            Client.SendInputState(Numbered(++_sent, keys));

        public void SendHello() => Client.SendCommand(MessageType.Predict);

        public List<ServerMessage> Read()
        {
            buffer.Append(Client.Take(Client.Received.Count));
            var messages = new List<ServerMessage>();
            while (stream.TryRead(buffer, out var message))
                messages.Add(message);
            return messages;
        }
    }

    private static Player Join(ServerHarness harness, string name, Team team = Team.Red)
    {
        var client = harness.JoinClient(name);
        var stream = new ServerStream();
        stream.SetShape(GameModes.ShapeOf(LevelMap.FromPng(Map, ReferenceTree.ReadMap(Map))));
        var player = new Player(client, stream, new ReceiveBuffer());

        client.SendChangeTeam(team);
        client.SendChangeClass(PlayerClass.Soldier);
        return player;
    }

    private static uint AckOf(ServerMessage message) =>
        BitConverter.ToUInt32(message.Payload);

    private static bool IsUpdate(ServerMessage message) =>
        message.Type is MessageType.InputState or MessageType.QuickUpdate;

    /// <summary>The input number the update's aim for <paramref name="slot"/> names.</summary>
    private static uint AppliedInput(ServerMessage update, int slot) =>
        (uint)(update.State!.Players[slot].Character!.Value.NetAimDirection - AimBase);

    /// <summary>
    /// After N inputs and a hello, the welcome says N, at version 1, and the
    /// first ack agrees with it.
    /// </summary>
    [Fact]
    public void TheWelcomeCountsTheInputsSentBeforeTheHello()
    {
        using var harness = new ServerHarness(options => options.Map = Map);
        var player = Join(harness, "predictor");
        harness.TickUntil(() => harness.Server.IsAlive(1), "the client spawned", 400);
        player.Read();

        for (var i = 0; i < 3; i++)
            player.SendNext();
        player.SendHello();
        harness.Tick(1);

        var messages = player.Read();
        var welcome = Assert.Single(messages, m => m.Type == MessageType.Predict);
        Assert.Equal(ServerMessages.PredictVersion, welcome.Payload[0]);
        Assert.Equal(3u, BitConverter.ToUInt32(welcome.Payload, 1));

        var ack = Assert.Single(messages, m => m.Type == MessageType.PredictAck);
        Assert.Equal(3u, AckOf(ack));

        // A second hello is not a second welcome.
        player.SendHello();
        harness.Tick(1);
        Assert.DoesNotContain(player.Read(), m => m.Type == MessageType.Predict);
    }

    /// <summary>
    /// Every state update a welcomed client receives is preceded by exactly one
    /// ack, and the update's keys are the input that ack names — the input that
    /// actually reached <c>ApplyInput</c>.
    /// </summary>
    /// <remarks>
    /// Two inputs in one tick move the ack by one on that tick and one on the
    /// next, and the broadcast follows them in turn. No input holds both. This
    /// goes red if the consume moves below <c>WriteStateUpdate</c> (the update
    /// would carry the input before the one the ack names) or if the queue is
    /// bypassed (two inputs would land on one tick).
    /// </remarks>
    [Fact]
    public void EachUpdateIsPrecededByOneAckNamingTheInputItCarries()
    {
        using var harness = new ServerHarness(options => options.Map = Map);
        var player = Join(harness, "predictor");
        harness.TickUntil(() => harness.Server.IsAlive(1), "the client spawned", 400);

        player.SendNext();
        player.SendHello();
        harness.Tick(2);
        player.Read();

        // Per tick: how many inputs to send before it.
        int[] plan = [1, 2, 0, 1, 0, 0, 2, 1, 1, 0];
        var acks = new List<uint>();

        foreach (var count in plan)
        {
            for (var i = 0; i < count; i++)
                player.SendNext();
            harness.Tick(1);

            uint? pending = null;
            var updates = 0;

            foreach (var message in player.Read())
            {
                if (message.Type == MessageType.PredictAck)
                {
                    Assert.Null(pending); // two acks with no update between them
                    pending = AckOf(message);
                }
                else if (IsUpdate(message))
                {
                    Assert.True(pending.HasValue, "a state update with no ack ahead of it");
                    Assert.Equal(pending!.Value, AppliedInput(message, 1));
                    acks.Add(pending.Value);
                    pending = null;
                    updates++;
                }
            }

            Assert.Equal(1, updates);
            Assert.Null(pending);
        }

        // 1 sent at hello time, then the plan: one consumed per tick while any wait.
        Assert.Equal([2u, 3u, 4u, 5u, 5u, 5u, 6u, 7u, 8u, 9u], acks);
        Assert.Equal(player.Sent, acks[^1]);
    }

    /// <summary>
    /// A socket client that never says hello is sent no prediction message and
    /// otherwise the same messages as one that did — the stock-client claim.
    /// </summary>
    [Fact]
    public void AClientThatNeverSaysHelloSeesNoDifference()
    {
        using var harness = new ServerHarness(options => options.Map = Map);
        var stock = Join(harness, "stock");
        var predictor = Join(harness, "predictor", Team.Blue);
        harness.TickUntil(
            () => harness.Server.IsAlive(1) && harness.Server.IsAlive(2), "both spawned", 400);

        predictor.SendHello();
        harness.Tick(2);
        stock.Read();
        predictor.Read();

        var stockSeen = new List<ServerMessage>();
        var predictorSeen = new List<ServerMessage>();

        for (var tick = 0; tick < 40; tick++)
        {
            stock.SendNext(tick % 10 < 5 ? PlayerKeys.Right : PlayerKeys.Left);
            predictor.SendNext(tick % 6 < 3 ? PlayerKeys.Left : PlayerKeys.Right);
            harness.Tick(1);
            stockSeen.AddRange(stock.Read());
            predictorSeen.AddRange(predictor.Read());
        }

        Assert.DoesNotContain(stockSeen, m => m.Type is MessageType.Predict or MessageType.PredictAck);
        Assert.Equal(40, predictorSeen.Count(m => m.Type == MessageType.PredictAck));

        var stripped = predictorSeen
            .Where(m => m.Type is not (MessageType.Predict or MessageType.PredictAck))
            .ToList();

        Assert.Equal(stockSeen.Count, stripped.Count);
        for (var i = 0; i < stripped.Count; i++)
        {
            Assert.Equal(stockSeen[i].Type, stripped[i].Type);
            Assert.True(
                stockSeen[i].Payload.AsSpan().SequenceEqual(stripped[i].Payload),
                $"message {i} ({stockSeen[i].Type}) differs between the two clients");
        }
    }

    /// <summary>Off, the hello is ignored and nothing is ever sent for it.</summary>
    [Fact]
    public void WithPredictionOffTheHelloIsIgnored()
    {
        using var harness = new ServerHarness(options =>
        {
            options.Map = Map;
            options.ClientPrediction = false;
        });
        var player = Join(harness, "predictor");
        harness.TickUntil(() => harness.Server.IsAlive(1), "the client spawned", 400);

        player.SendNext();
        player.SendHello();
        player.SendNext();
        harness.Tick(3);

        Assert.DoesNotContain(player.Read(), m => m.Type is MessageType.Predict or MessageType.PredictAck);
        Assert.DoesNotContain(harness.Log.Warnings, line => line.Contains("192") || line.Contains("0xC0") || line.Contains("unrecognised", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A burst deeper than the cap folds into one tick, the ack counting every
    /// input folded in, and a tap folded away still reaches the character.
    /// </summary>
    [Fact]
    public void ABurstMergesDownToTheCapWithoutLosingATap()
    {
        using var harness = new ServerHarness(options => options.Map = Map);
        var player = Join(harness, "predictor");
        harness.TickUntil(() => harness.Server.IsAlive(1), "the client spawned", 400);

        player.SendNext();
        player.SendHello();
        harness.Tick(3);
        player.Read();
        Assert.False(harness.Server.IsTaunting(1), "the baseline was already taunting");

        // Five at once: a taunt tap and four quiet inputs. Cap 2 leaves two
        // waiting, so the first three share this tick.
        player.SendNext(PlayerKeys.Taunt);
        for (var i = 0; i < 4; i++)
            player.SendNext();
        harness.Tick(1);

        var ack = player.Read().Last(m => m.Type == MessageType.PredictAck);
        Assert.Equal(4u, AckOf(ack)); // input 1 before, then 2, 3, 4 folded together
        Assert.True(
            harness.Server.IsTaunting(1),
            "a tap merged into a later input's tick was lost; the merge must OR the edges");
    }

    // --- The queue itself -----------------------------------------------------

    [Fact]
    public void AnEmptyQueueHoldsTheLastInput()
    {
        var source = new NetworkInputSource();
        source.StartQueueing(0);
        source.Enqueue(Numbered(1, PlayerKeys.Right));

        source.ConsumeTick(2, 30);
        Assert.Equal(1u, source.InputsApplied);
        Assert.Equal(PlayerKeys.Right, source.Latest.Keys);

        source.ConsumeTick(2, 30);
        Assert.Equal(1u, source.InputsApplied);
        Assert.Equal(PlayerKeys.Right, source.Latest.Keys);
        Assert.Equal((PlayerKeys.Right, PlayerKeys.None), source.TakeEdges());
    }

    [Fact]
    public void AStandingInputDrainsAfterTheDrainTicks()
    {
        var source = new NetworkInputSource();
        source.StartQueueing(0);

        // A gap then a pair: one input left standing behind every consume.
        source.Enqueue(Numbered(1));
        source.Enqueue(Numbered(2));
        uint sent = 2;

        var applied = new List<uint>();
        for (var tick = 0; tick < 6; tick++)
        {
            source.ConsumeTick(2, 3);
            applied.Add(source.InputsApplied);
            source.Enqueue(Numbered(++sent));
        }

        // Three ticks standing, then two inputs share a tick, then the queue
        // runs at depth zero after each consume.
        Assert.Equal([1u, 2u, 3u, 5u, 6u, 7u], applied);
        Assert.Equal(1, source.QueuedCount);
    }

    [Fact]
    public void AFloodCannotGrowTheQueuePastItsCapacity()
    {
        var source = new NetworkInputSource();
        source.StartQueueing(0);

        // A frozen server reads without consuming.
        for (uint n = 1; n <= 100; n++)
            source.Enqueue(Numbered(n));

        Assert.Equal(NetworkInputSource.QueueCapacity, source.QueuedCount);

        source.ConsumeTick(2, 30);
        Assert.Equal(2, source.QueuedCount);
        Assert.Equal(98u, source.InputsApplied);
        Assert.Equal(AimBase + 98, source.Latest.NetAimDirection);
    }

    // --- The fork ------------------------------------------------------------

    /// <summary>
    /// A forked tick has no hidden state: ticking copies of the match, with
    /// different inputs, leaves the original and everything the tick reads
    /// untouched.
    /// </summary>
    /// <remarks>
    /// The client's prediction is exactly this — copy the replica, tick the copy
    /// a few times, read the local character, throw the copy away — so it rests
    /// on <see cref="MatchSimulation.Tick"/> depending on nothing outside its
    /// arguments and writing nothing but its state. A static, a cache or a
    /// <see cref="CollisionWorld"/> write that a tick changes would make the
    /// forked run below diverge from the reference run, and nothing else in the
    /// suite would notice. docs/PREDICTION.md, "What the server adds for this".
    /// </remarks>
    [Fact]
    public void ForkedTicksLeaveTheMatchAndTheWorldUntouched()
    {
        const string map = "ctf_truefort";
        var level = LevelMap.FromPng(map, ReferenceTree.ReadMap(map));
        var spawns = new SpawnPoints(level);

        var reference = Match(level, spawns);
        var forked = reference;
        var world = new CollisionWorld(level);

        var inputs = new PlayerInput[WorldState.MaxSlots];
        var forkInputs = new PlayerInput[WorldState.MaxSlots];
        var events = default(TickEvents);
        var forkEvents = default(TickEvents);
        var rng = new Random(7);
        var projectilesSeen = false;

        for (var tick = 0; tick < 900; tick++)
        {
            for (var slot = 1; slot <= 4; slot++)
            {
                if (tick % 15 == 0)
                {
                    inputs[slot] = new PlayerInput(
                        (PlayerKeys)rng.Next(0, 256) & ~PlayerKeys.Taunt,
                        (ushort)rng.Next(0, 65536),
                        (byte)rng.Next(20, 120));
                }
            }

            // The rollout: a copy ticked ten times, slot 1 on a different
            // input, everybody else held, then discarded.
            var before = Bytes(in forked).ToArray();
            var fork = forked;
            for (var slot = 0; slot < WorldState.MaxSlots; slot++)
                forkInputs[slot] = fork.Characters[slot].HeldInput;

            for (var step = 0; step < 10; step++)
            {
                forkInputs[1] = new PlayerInput(PlayerKeys.Left | PlayerKeys.Jump | PlayerKeys.Attack, 20000, 60);
                MatchSimulation.Tick(world, ref fork, forkInputs, ref forkEvents, spawnPoints: null);
            }

            Assert.True(Bytes(in forked).SequenceEqual(before), $"ticking a copy changed the original at tick {tick}");

            MatchSimulation.Tick(world, ref reference, inputs, ref events, spawns);
            MatchSimulation.Tick(world, ref forked, inputs, ref events, spawns);
            projectilesSeen |= reference.LiveProjectileCount > 0;

            if (!Bytes(in forked).SequenceEqual(Bytes(in reference)))
                Assert.Fail($"the match diverged from its unforked twin at tick {tick}: something a tick reads lives outside its arguments");
        }

        Assert.True(projectilesSeen, "no projectile was ever in flight, so the pool went unexercised");
    }

    /// <summary>A held input reapplied creates no edge.</summary>
    [Fact]
    public void AHeldInputCreatesNoEdge()
    {
        var character = CharacterState.Fresh(PlayerClass.Soldier, Team.Red);
        character.ApplyInput(new PlayerInput(PlayerKeys.Jump | PlayerKeys.Attack, 123, 45));
        character.PressedKeys = PlayerKeys.None;
        character.ReleasedKeys = PlayerKeys.None;

        var held = character.HeldInput;
        Assert.Equal(new PlayerInput(PlayerKeys.Jump | PlayerKeys.Attack, 123, 45), held);

        character.ApplyInput(held);
        Assert.Equal(PlayerKeys.None, character.PressedKeys);
        Assert.Equal(PlayerKeys.None, character.ReleasedKeys);
    }

    private static ReadOnlySpan<byte> Bytes(in WorldState state) =>
        MemoryMarshal.AsBytes(new ReadOnlySpan<WorldState>(in state));

    /// <summary>Two soldiers a side in a running CTF match.</summary>
    private static WorldState Match(LevelMap level, SpawnPoints spawns)
    {
        var state = default(WorldState);
        state.GameMode = GameModes.Of(level);
        state.TimeLimitMinutes = 15;
        state.RespawnTime = 0;
        state.TdmInvulnerabilityTicks = 0;

        for (var i = 0; i < TeamBalance.ClassCount; i++)
            state.ClassLimits[i] = 255;

        Intel.Place(ref state, level);
        Rounds.Start(ref state);
        state.AddPlayer(0);

        foreach (var (slot, team) in new[] { (1, Team.Red), (2, Team.Blue), (3, Team.Red), (4, Team.Blue) })
        {
            state.AddPlayer(slot);
            state.Spawn(spawns, slot, PlayerClass.Soldier, team, spawnpointId: 0, group: 0);
        }

        return state;
    }
}
