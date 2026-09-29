using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using GG2.Bots;
using GG2.Maps;
using GG2.Protocol;
using GG2.Simulation;
using Xunit.Abstractions;

namespace GG2.Tests;

/// <summary>
/// The structural constraints <c>docs/BOTAI.md</c> asks for, asserted rather than
/// described.
/// </summary>
/// <remarks>
/// <b>That document was written "while it is still specification" and its
/// claims had never been checked against the shipped code.</b> Most of them are
/// prose about design intent, which no test can hold — but four are structural
/// facts that a single careless change silently destroys, and three of those are
/// the ones the document says cannot be retrofitted at all.
/// <para>
/// <b>The failure mode this exists for is not a broken test elsewhere.</b>
/// Putting a <c>List&lt;&gt;</c> or a logger in <see cref="WorldState"/> breaks
/// nothing that runs today: the server still serves, every parity window holds,
/// and the whole suite stays green. What it destroys is rollout search, months
/// later, for a reason nobody can find.
/// </para>
/// </remarks>
public sealed class BotAiConstraintsTests(ITestOutputHelper output)
{
    private const string Map = "ctf_truefort";

    /// <summary>
    /// Item 1: <see cref="WorldState"/> holds no reference, anywhere in it.
    /// </summary>
    /// <remarks>
    /// <b>The one item <c>docs/BOTAI.md</c> marks "retrofit cost: total".</b>
    /// A clone has to be a <c>memcpy</c>, so no field may point outside the state
    /// — no map object, no config, no logger, no event sink that owns memory.
    /// Read-only inputs are passed <em>to</em> <c>Tick</c> alongside the state,
    /// never held by it, which is why <see cref="CollisionWorld"/> is a parameter.
    /// <para>
    /// This is <b>recursive</b>: it fails on a reference nested any depth down,
    /// including one inside a struct inside an <c>[InlineArray]</c> slot buffer,
    /// which is exactly where one would be added without anybody noticing.
    /// </para>
    /// </remarks>
    [Fact]
    public void WorldStateHoldsNoReferenceAnywhereInIt()
    {
        Assert.False(
            RuntimeHelpers.IsReferenceOrContainsReferences<WorldState>(),
            "WorldState now holds a reference: a clone is a graph walk and rollout search is dead on cost");

        // The control, because a test that cannot fail is not a gate: the same
        // question about a type that DOES hold one. Without it, an assertion that
        // silently answered false for everything would read as a pass.
        Assert.True(RuntimeHelpers.IsReferenceOrContainsReferences<WithAReference>());

        // And the two states this rule protects, named so a failure points at the
        // line rather than at the whole type.
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<CharacterState>());
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<ProjectileState>());
    }

    /// <summary>
    /// Item 2: a state round-trips through raw bytes and goes on ticking
    /// identically.
    /// </summary>
    /// <remarks>
    /// <c>docs/BOTAI.md</c>: "if item 1 is done properly this is nearly free — a
    /// copyable flat state is a serialisable one". This is what makes that
    /// concrete, and it needs no production code at all: the bytes ARE the state.
    /// <para>
    /// <b>Restoring is not enough; continuing is the claim.</b> A state that
    /// compares equal and then diverges on the next tick is the failure this is
    /// really about — a hidden counter, a cursor into a pool, an RNG stream left
    /// outside the struct. So the restored copy is stepped fifty ticks against a
    /// control that was never serialised, and the two must agree byte for byte at
    /// the end rather than at the start.
    /// </para>
    /// </remarks>
    [Fact]
    public void AStateRoundTripsThroughBytesAndGoesOnTickingIdentically()
    {
        var (world, spawns) = Level();
        var state = Match(spawns);

        // Somewhere interesting: a few seconds in, with shots in the air.
        var inputs = new PlayerInput[WorldState.MaxSlots];
        inputs[1] = new PlayerInput(PlayerKeys.Right | PlayerKeys.Attack, 8192, 40);
        inputs[2] = new PlayerInput(PlayerKeys.Left, 40000, 40);

        // Stop on the CONDITION, not on a tick count. A rocket's whole life is
        // shorter than its refire, so at most instants after firing there is
        // nothing in the air at all — and a snapshot with an empty pool does not
        // exercise the handles and the creation order, which is the part of the
        // state most likely to hide something outside itself.
        var armed = false;

        for (var tick = 0; tick < 600 && !armed; tick++)
        {
            MatchSimulation.Tick(world, ref state, inputs, spawns);
            armed = state.LiveProjectileCount > 0 && tick > 60;
        }

        Assert.True(armed, "nothing ever reached the air, so the pool is not exercised");

        var bytes = Bytes(in state).ToArray();
        output.WriteLine($"a whole match is {bytes.Length} bytes");

        var restored = MemoryMarshal.Read<WorldState>(bytes);
        Assert.True(Bytes(in restored).SequenceEqual(bytes), "the round trip is not bit-identical");

        // And it CONTINUES identically, which is the part that matters.
        var control = state;

        for (var tick = 0; tick < 50; tick++)
        {
            MatchSimulation.Tick(world, ref control, inputs, spawns);
            MatchSimulation.Tick(world, ref restored, inputs, spawns);
        }

        Assert.True(
            Bytes(in restored).SequenceEqual(Bytes(in control).ToArray()),
            "the restored state diverged, so something the tick reads lives outside WorldState");
    }

    /// <summary>
    /// Item 1's other half: a steady-state tick allocates nothing.
    /// </summary>
    /// <remarks>
    /// <b>"Cheap to copy" is only half of it</b> — if each simulated tick
    /// produces garbage, a search that plays thousands of candidate futures pays
    /// for a collection it never asked for, and no amount of C# speed rescues it.
    /// <para>
    /// The measurement is per <em>thread</em> and includes the test's own
    /// allocations, so everything it needs is allocated before the window opens
    /// and the loop below allocates nothing of its own. The budget is zero rather
    /// than "small": a tick that allocates at all does so every tick.
    /// </para>
    /// </remarks>
    [Fact]
    public void ASteadyStateTickAllocatesNothing()
    {
        var (world, spawns) = Level();
        var state = Match(spawns);

        var inputs = new PlayerInput[WorldState.MaxSlots];
        inputs[1] = new PlayerInput(PlayerKeys.Right | PlayerKeys.Attack, 8192, 40);
        inputs[2] = new PlayerInput(PlayerKeys.Left | PlayerKeys.Jump, 40000, 40);

        // Out of the spawn wave and into a running fight, so the ticks measured
        // below are the steady-state ones rather than the first of a match.
        for (var tick = 0; tick < 200; tick++)
            MatchSimulation.Tick(world, ref state, inputs, spawns);

        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var tick = 0; tick < 300; tick++)
            MatchSimulation.Tick(world, ref state, inputs, spawns);

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;



        output.WriteLine($"300 ticks allocated {allocated} bytes");
        Assert.Equal(0, allocated);
    }

    /// <summary>
    /// Item 6: <see cref="WorldView"/> is a <c>ref struct</c>, so the read-only
    /// guarantee is structural.
    /// </summary>
    /// <remarks>
    /// Constructing one is free and the language <b>refuses</b> to let a source
    /// stash it in a field or keep it past the call. A by-value projection would
    /// have been a kilobyte copy per bot per decision — item 1 in another costume
    /// — and a class would have been an allocation on the same path.
    /// </remarks>
    [Fact]
    public void WorldViewIsARefStruct()
    {
        Assert.True(typeof(WorldView).IsByRefLike);
        Assert.True(typeof(WorldView).IsValueType);

        // The control: an ordinary struct is not by-ref-like, so the property
        // being asserted is a real one rather than something every type has.
        Assert.False(typeof(PlayerInput).IsByRefLike);
    }

    /// <summary>
    /// Item 4: a bot's action is exactly the wire action, with no privileged aim.
    /// </summary>
    /// <remarks>
    /// <b>The honesty property: a bot cannot aim more precisely than a player
    /// can.</b> One action space for humans, scripted bots, search and learned
    /// policies means a logged human action is a valid bot action and vice versa
    /// — no conversion layer, no train/deploy mismatch, and behaviour cloning
    /// targets the exact quantisation the game runs on.
    /// <para>
    /// A <c>double</c> anywhere in <see cref="PlayerInput"/> is how that gets
    /// lost, and it would be lost quietly: the server would work, and only the
    /// train/deploy match would be gone.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheActionSpaceIsExactlyTheWireAction()
    {
        var fields = typeof(PlayerInput).GetFields(
            System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.NonPublic);

        foreach (var field in fields)
        {
            output.WriteLine($"{field.Name}: {field.FieldType.Name}");

            Assert.True(
                field.FieldType == typeof(byte)
                || field.FieldType == typeof(ushort)
                || field.FieldType.IsEnum,
                $"PlayerInput.{field.Name} is a {field.FieldType.Name}: a bot can now aim more precisely than a player");
        }

        // The aim is the wire's 16 bits and the degrees are derived from it, so a
        // bot that wrote degrees would be quantised on the way in like everyone
        // else. 65536/360 is about 182 steps a degree.
        var input = new PlayerInput(PlayerKeys.None, 8192, 40);
        Assert.Equal(45, input.AimDirectionDegrees);

        // And the way IN is quantised too: PlayerInput.FromAim rounds a real
        // aim onto the same 65,536 steps, so a bot that computed degrees is on the
        // grid a client is on. About 182 steps a degree.
        Assert.Equal(
            input.NetAimDirection,
            PlayerInput.FromAim(PlayerKeys.None, 45, 80).NetAimDirection);
    }

    // --- helpers -------------------------------------------------------------

    /// <summary>A struct that does hold a reference — the control for item 1.</summary>
#pragma warning disable CS0649 // never assigned: the type exists to be asked about, not used
    private struct WithAReference
    {
        public int Slot;
        public string? Name;
    }
#pragma warning restore CS0649

    private static ReadOnlySpan<byte> Bytes(in WorldState state) =>
        MemoryMarshal.AsBytes(new ReadOnlySpan<WorldState>(in state));

    private static (CollisionWorld World, SpawnPoints Spawns) Level()
    {
        var level = LevelMap.FromPng(Map, ReferenceTree.ReadMap(Map));
        return (new CollisionWorld(level), new SpawnPoints(level));
    }

    /// <summary>A running CTF match with two soldiers in it.</summary>
    private static WorldState Match(SpawnPoints spawns)
    {
        var level = LevelMap.FromPng(Map, ReferenceTree.ReadMap(Map));
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

        foreach (var (slot, team) in new[] { (1, Team.Red), (2, Team.Blue) })
        {
            state.AddPlayer(slot);
            state.Spawn(spawns, slot, PlayerClass.Soldier, team, spawnpointId: 0, group: 0);
        }

        return state;
    }
}
