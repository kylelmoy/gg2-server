using System.Reflection;
using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Server;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// The five bytes a moving platform puts on a <c>QUICK_UPDATE</c> and a
/// <c>FULL_UPDATE</c>, written and read straight back.
/// </summary>
/// <remarks>
/// <c>MovingPlatform.events/User Event 10.xml</c> writes them and
/// <c>User Event 11.xml</c> reads them, and there is no count prefix on either
/// side: the client sizes the read from its own copy of the map. So the framing
/// depends on the writer and the reader agreeing about how many platforms exist,
/// and a disagreement desynchronises the stream permanently rather than showing
/// up as a glitch — the same failure the generator block has.
/// <para>
/// <b>Nothing in the parity corpus reaches this</b>, because no shipped map
/// places a platform. These round trips are the only check there is.
/// </para>
/// </remarks>
public sealed class MovingPlatformUpdateLayoutTests
{
    private const string Map = "ctf_truefort";

    private const double X = 300;
    private const double Y = 500;

    /// <summary>
    /// The duplicated ceiling in <see cref="StateUpdateWriter"/> against the
    /// real one.
    /// </summary>
    /// <remarks>
    /// <c>Gg2.Server</c> deliberately does not <c>using Gg2.Simulation</c> in the
    /// writer — it reaches the world through delegates precisely so it cannot
    /// copy a <c>WorldState</c> — so the stackalloc's size is written out by
    /// hand. That is a duplicated constant, and the writer's own comment promises
    /// this test holds the two together. Without it the copy could shrink and
    /// silently truncate a custom map's platforms.
    /// </remarks>
    [Fact]
    public void TheWritersStackallocIsBigEnoughForEveryPlatformTheStateCanHold()
    {
        var field = typeof(StateUpdateWriter)
            .GetField("MaxMovingPlatformBlocks", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(field);

        var blocks = (int)field.GetRawConstantValue()!;

        Assert.True(
            blocks >= WorldState.MaxMovingPlatforms,
            $"the writer can frame {blocks} platforms but a world can hold {WorldState.MaxMovingPlatforms}");
    }

    [Theory]
    [InlineData(MessageType.QuickUpdate)]
    [InlineData(MessageType.FullUpdate)]
    public void APlatformsPositionAndStateSurviveTheRoundTrip(MessageType tier)
    {
        // 0.2 is the smallest step the wire can carry: x_ * 5 as a ushort, so a
        // fifth of a pixel. Chosen to prove the multiply is there rather than a
        // whole number that would survive without it.
        var update = RoundTrip(tier, platforms: 1, x: X + 0.2, y: Y, movementState: MovingPlatforms.Down);

        var platform = Assert.Single(update.MovingPlatforms);

        Assert.Equal((ushort)((X + 0.2) * 5), platform.X);
        Assert.Equal((ushort)(Y * 5), platform.Y);
        Assert.Equal(MovingPlatforms.Down, platform.MovementState);
    }

    /// <summary>An <c>INPUTSTATE</c> carries no platform block at all.</summary>
    /// <remarks>
    /// User Event 10 writes nothing outside the two update tiers, which is why
    /// the writer's guard is on the tier rather than on the count.
    /// </remarks>
    [Fact]
    public void AnInputstateCarriesNoPlatformBlock()
    {
        var withPlatform = Bytes(MessageType.InputState, platforms: 1);
        var without = Bytes(MessageType.InputState, platforms: 0);

        Assert.Equal(without.Length, withPlatform.Length);
    }

    /// <summary>Every platform gets a block, in order, with no count prefix.</summary>
    [Fact]
    public void EveryPlatformCostsExactlyFiveBytes()
    {
        var none = Bytes(MessageType.QuickUpdate, platforms: 0);
        var three = Bytes(MessageType.QuickUpdate, platforms: 3);

        Assert.Equal(none.Length + 15, three.Length);
    }

    /// <summary>
    /// The blocks come out in level-data order, so the client can match them to
    /// its own instances positionally.
    /// </summary>
    [Fact]
    public void TheBlocksKeepTheMapsOrder()
    {
        var update = RoundTrip(MessageType.QuickUpdate, platforms: 3, x: X, y: Y, movementState: MovingPlatforms.Up);

        Assert.Equal(3, update.MovingPlatforms.Length);

        // Placed 20px apart by the helper below, so order is readable off x.
        for (var i = 0; i < 3; i++)
            Assert.Equal((ushort)((X + (20 * i)) * 5), update.MovingPlatforms[i].X);
    }

    private static byte[] Bytes(MessageType tier, int platforms) =>
        Written(tier, platforms, X, Y, MovingPlatforms.Up).WrittenSpan.ToArray();

    private static StateUpdate RoundTrip(
        MessageType tier, int platforms, double x, double y, byte movementState)
    {
        var writer = Written(tier, platforms, x, y, movementState);

        var shape = GameModes.ShapeOf(Level()) with { MovingPlatformCount = platforms };

        // The writer emits the message-type byte and the reader starts at the
        // payload, so the first byte is skipped.
        var reader = new NetReader(writer.WrittenSpan[1..]);
        return StateUpdateReader.Read(ref reader, tier, shape, []);
    }

    private static NetWriter Written(
        MessageType tier, int platforms, double x, double y, byte movementState)
    {
        var map = Level();
        var roster = new Roster("server");
        var match = new MatchState(map, roster, new ServerOptions());

        static bool NoCharacters(int slot, out CharacterBlock character, out WeaponBlock weapon)
        {
            character = default;
            weapon = default;
            return false;
        }

        int Platforms(Span<MovingPlatformBlock> blocks)
        {
            for (var i = 0; i < platforms; i++)
            {
                blocks[i] = new MovingPlatformBlock(
                    WireQuantise.ToUShort((x + (20 * i)) * 5),
                    WireQuantise.ToUShort(y * 5),
                    movementState);
            }

            return platforms;
        }

        var writer = new NetWriter();
        new StateUpdateWriter(
() => match, NoCharacters, movingPlatforms: Platforms).Write(writer, tier);
        return writer;
    }

    private static LevelMap Level() =>
        LevelMap.FromPng(Map, ReferenceTree.ReadMap(Map)).ForArea(1);
}
