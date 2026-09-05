using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Server;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// The generator HUD block's bytes, through the real writer and the real
/// reader.
/// </summary>
/// <remarks>
/// <b>This exists for a framing hazard, not for a field count.</b> The block
/// carries one two-field record per <em>surviving</em> generator, because the
/// HUD reaches each through a <c>with</c>
/// (<c>GeneratorHUD.events/User Event 12.xml</c>) — so destroying one shortens
/// every later update by four bytes. A reader that always takes two would lose
/// the stream the first time a generator went down and surface hundreds of bytes
/// later as "no framing for server message N", which is exactly how the sentry
/// <c>INPUTSTATE</c> bug and the <c>ARENA_ENDROUND</c> one both presented.
/// <para>
/// <b>No recording can catch it.</b> Every fixture in the project is on
/// <c>ctf_truefort</c>; <c>gen_destroy</c> is the only shipped generator map and
/// nothing has ever been recorded on it.
/// </para>
/// </remarks>
public sealed class GeneratorUpdateLayoutTests
{
    private const string Map = "gen_destroy";

    private static LevelMap Level() =>
        LevelMap.FromPng(Map, ReferenceTree.ReadMap(Map)).ForArea(1);

    /// <summary>
    /// Writes an update for a world and reads it straight back, with the
    /// objective plumbed the way <c>GameServer</c> plumbs it.
    /// </summary>
    /// <param name="generatorCount">
    /// What the reader is told to expect. A consumer keeps this in step by
    /// decrementing on <c>GENERATOR_DESTROY</c>; passing the wrong number here
    /// is how a test proves the framing depends on it.
    /// </param>
    private static StateUpdate RoundTrip(in WorldState state, MessageType tier, int generatorCount)
    {
        var map = Level();
        var roster = new Roster("server");
        var match = new MatchState(map, roster, new ServerOptions(), GameModes.TotalAreas(map));

        var captured = state;

        ObjectiveBlock Objective() => new(
            null,
            null,
            0,
            0,
            captured.TimeLimitMinutes,
            captured.RoundTimer,
            RedGenerator: Generators.ToBlock(captured.RedGenerator),
            BlueGenerator: Generators.ToBlock(captured.BlueGenerator));

        static bool NoCharacters(int slot, out CharacterBlock character, out WeaponBlock weapon)
        {
            character = default;
            weapon = default;
            return false;
        }

        var writer = new NetWriter();
        new StateUpdateWriter(
() => match, NoCharacters, null, Objective).Write(writer, tier);

        var shape = GameModes.ShapeOf(map) with { GeneratorCount = generatorCount };

        // The writer emits the message-type byte and the reader starts at the
        // payload, so the first byte is skipped — see MineUpdateLayoutTests.
        var reader = new NetReader(writer.WrittenSpan[1..]);
        return StateUpdateReader.Read(ref reader, tier, shape, []);
    }

    private static WorldState Placed()
    {
        var state = default(WorldState);
        state.GameMode = GameMode.Generator;
        state.TimeLimitMinutes = 10;
        state.RoundTimer = Rounds.TimerFor(10);
        Generators.Place(ref state, Level());
        return state;
    }

    /// <summary>
    /// Blue's generator is written first, then red's — the reverse of every
    /// other paired field on the wire.
    /// </summary>
    /// <remarks>
    /// <c>with GeneratorBlue event_user(12); with GeneratorRed event_user(12);</c>,
    /// in that order. The two are given different health so that swapping them
    /// fails rather than passing by symmetry.
    /// </remarks>
    [Fact]
    public void TheBlockIsBlueThenRed()
    {
        var state = Placed();

        Generators.Damage(ref state, Team.Red, 800);
        Generators.Damage(ref state, Team.Blue, 100);

        var update = RoundTrip(state, MessageType.FullUpdate, generatorCount: 2);

        // timeLimitMins, timer, then two per generator.
        Assert.Equal(6, update.HudScalars.Length);

        Assert.Equal((long)Gml.Ceil(state.BlueGenerator.Hp), update.HudScalars[2]);
        Assert.Equal((long)Gml.Ceil(state.BlueGenerator.ShieldHp), update.HudScalars[3]);
        Assert.Equal((long)Gml.Ceil(state.RedGenerator.Hp), update.HudScalars[4]);
        Assert.Equal((long)Gml.Ceil(state.RedGenerator.ShieldHp), update.HudScalars[5]);

        // And the two really are different, so the assertion above is not
        // satisfied by both being full.
        Assert.NotEqual(update.HudScalars[2], update.HudScalars[4]);
    }

    /// <summary>
    /// A destroyed generator writes <b>nothing</b> — not a zeroed block — so
    /// the message is four bytes shorter and a following message still frames.
    /// </summary>
    /// <remarks>
    /// The control that would have caught the latent bug: appending a second
    /// message and reading it back proves the update's length was right, which
    /// is the same trick <c>ArenaTests.ArenaEndRoundIsFramedByItsMvpCounts</c>
    /// uses.
    /// </remarks>
    [Fact]
    public void ADestroyedGeneratorLeavesTheBlockFourBytesShorter()
    {
        var state = Placed();
        state.RedGenerator.Exists = false;

        var map = Level();
        var roster = new Roster("server");
        var match = new MatchState(map, roster, new ServerOptions(), GameModes.TotalAreas(map));
        var captured = state;

        ObjectiveBlock Objective() => new(
            null,
            null,
            0,
            0,
            captured.TimeLimitMinutes,
            captured.RoundTimer,
            RedGenerator: Generators.ToBlock(captured.RedGenerator),
            BlueGenerator: Generators.ToBlock(captured.BlueGenerator));

        static bool NoCharacters(int slot, out CharacterBlock character, out WeaponBlock weapon)
        {
            character = default;
            weapon = default;
            return false;
        }

        var writer = new NetWriter();
        var stateWriter = new StateUpdateWriter(
() => match, NoCharacters, null, Objective);
        stateWriter.Write(writer, MessageType.CapsUpdate);

        // A message whose own framing is unambiguous, so that reading it back
        // proves the update above consumed exactly its own bytes.
        ServerMessages.GeneratorDestroy(writer, Team.Red);

        var stream = new ServerStream();
        stream.SetShape(GameModes.ShapeOf(map) with { GeneratorCount = 1 });

        var buffer = new ReceiveBuffer();
        buffer.Append(writer.WrittenSpan.ToArray());

        Assert.True(stream.TryRead(buffer, out var update));
        Assert.Equal(MessageType.CapsUpdate, update.Type);
        Assert.Equal(4, update.State!.HudScalars.Length);

        Assert.True(stream.TryRead(buffer, out var destroy));
        Assert.Equal(MessageType.GeneratorDestroy, destroy.Type);
    }

    /// <summary>
    /// A live server on the one shipped generator map frames a
    /// <c>FULL_UPDATE</c> through the real <see cref="ServerStream"/>, with both
    /// generators at full health.
    /// </summary>
    [Fact]
    public void ALiveGeneratorServerFramesBothGenerators()
    {
        var level = Level();

        using var harness = new ServerHarness(o => o.Map = Map);
        var client = harness.JoinClient("watcher");

        var stream = new ServerStream();
        stream.SetShape(GameModes.ShapeOf(level));
        var buffer = new ReceiveBuffer();

        harness.Tick();
        client.Poll();
        buffer.Append(client.Take(client.Received.Count));

        var messages = new List<ServerMessage>();
        while (stream.TryRead(buffer, out var message))
            messages.Add(message);

        var full = messages.Last(m => m.State is { Tier: MessageType.FullUpdate }).State!;

        Assert.Equal(6, full.HudScalars.Length);
        Assert.Equal((long)Generators.MaxHp, full.HudScalars[2]);
        Assert.Equal((long)Generators.MaxShieldHp, full.HudScalars[3]);
        Assert.Equal((long)Generators.MaxHp, full.HudScalars[4]);
        Assert.Equal((long)Generators.MaxShieldHp, full.HudScalars[5]);
    }
}
