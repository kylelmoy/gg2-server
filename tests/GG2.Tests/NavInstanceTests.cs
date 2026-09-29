using System.Buffers.Binary;
using System.Text.RegularExpressions;
using GG2.Maps;
using GG2.Nav;
using GG2.Simulation;

namespace GG2.Tests;

/// <summary>
/// The collider sprites the nav generator stamps with, against the two things
/// that could make them wrong.
/// </summary>
/// <remarks>
/// ⚠️ <b><c>GG2.Nav</c> restates four sprite sizes that <c>ColliderSprites</c>
/// already carries</b>, because a generator has no business referencing the
/// simulation — so there are two copies of a number that decides where every wall
/// and every gate is in a graph. <c>gg2-nav-gen</c> did not have this problem: it
/// read the sizes out of the checkout's own PNG headers at run time, and asserted
/// the origin and mask shape while it was there. This port ships without a
/// checkout and cannot, so this test is what replaces that check — one arm against
/// the other copy, which always runs, and one against the reference, which runs
/// when a checkout is present.
/// <para>
/// ⚠️ The failure mode is not a crash. A resized <c>sprite45</c> would move every
/// gate boundary in every graph while both files still looked right.
/// </para>
/// </remarks>
public sealed partial class NavInstanceTests
{
    /// <summary>
    /// The generator and the simulation agree on every collider rectangle.
    /// </summary>
    /// <remarks>
    /// Compared through the public seam on both sides — an entity in, a box out —
    /// rather than by exposing two sets of constants to each other, so it stays a
    /// check on what the two actually stamp.
    /// <para>
    /// ⚠️ <b>Scales are in the comparison</b>, and a non-integer one especially:
    /// the whole difference between the two box derivations is what they do with a
    /// fractional edge, and at scale 1 they cannot disagree.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(EntityTypes.PlayerWall)]
    [InlineData(EntityTypes.PlayerWallHorizontal)]
    [InlineData(EntityTypes.DropdownPlatform)]
    [InlineData(EntityTypes.RedTeamGate)]
    [InlineData(EntityTypes.BlueTeamGate)]
    [InlineData(EntityTypes.RedTeamGate2)]
    [InlineData(EntityTypes.BlueTeamGate2)]
    [InlineData(EntityTypes.RedIntelGate)]
    [InlineData(EntityTypes.BlueIntelGate)]
    [InlineData(EntityTypes.RedIntelGate2)]
    [InlineData(EntityTypes.BlueIntelGate2)]
    [InlineData(EntityTypes.IntelGateHorizontal)]
    [InlineData(EntityTypes.IntelGateVertical)]
    [InlineData(EntityTypes.SetupGate)]
    public void TheGeneratorAndTheSimulationStampTheSameRectangle(string type)
    {
        foreach (var (xScale, yScale) in new[] { (1.0, 1.0), (3.0, 1.0), (1.0, 2.25), (13.0, 0.3) })
        {
            var entity = new LevelEntity
            {
                Type = type,
                X = 300,
                Y = 606,
                XScale = xScale,
                YScale = yScale,
                Properties = new Dictionary<string, GgonValue>(StringComparer.Ordinal),
            };

            var simulation = ColliderSprites.Of(entity);
            Assert.NotNull(simulation);

            var generated = Assert.Single(NavInstances.Instantiate([entity]));
            Assert.True(generated.HasBox, $"{type} has no bounding box in the generator");

            Assert.Equal(simulation!.Value.Left, generated.Left);
            Assert.Equal(simulation.Value.Right, generated.Right);
            Assert.Equal(simulation.Value.Top, generated.Top);
            Assert.Equal(simulation.Value.Bottom, generated.Bottom);
        }
    }

    /// <summary>
    /// ⚠️ The other three, which <c>ColliderSprites</c> deliberately does not
    /// carry: they are not solid to a character, so the simulation has no
    /// rectangle for them and only the generator does.
    /// </summary>
    /// <remarks>
    /// A killbox does not stop a character; it kills one. The nav layer still has
    /// to know where it is, because a node standing in one is a place the graph
    /// must never offer — so the size is checked against the reference below and
    /// against nothing here.
    /// </remarks>
    [Theory]
    [InlineData(EntityTypes.KillBox)]
    [InlineData(EntityTypes.PitFall)]
    [InlineData(EntityTypes.FragBox)]
    [InlineData(EntityTypes.MoveBoxUp)]
    public void TheHazardsAreTheSpawnRoomCollider(string type)
    {
        var entity = new LevelEntity
        {
            Type = type,
            X = 0,
            Y = 0,
            Properties = new Dictionary<string, GgonValue>(StringComparer.Ordinal),
        };

        var generated = Assert.Single(NavInstances.Instantiate([entity]));

        // sprite64, 42x42, inclusive at both ends.
        Assert.Equal(0, generated.Left);
        Assert.Equal(41, generated.Right);
        Assert.Equal(0, generated.Top);
        Assert.Equal(41, generated.Bottom);
    }

    /// <summary>
    /// ⛔ And the sprites in the reference are still the size, origin and mask
    /// shape both copies assume.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The origin and the mask shape are half of this, not decoration.</b> A
    /// sprite that grew a nonzero origin would shift every stamp by the origin, and
    /// one that stopped being a full-image RECTANGLE would make the box a lie about
    /// the mask — and both would leave a size check green. <c>gg2-nav-gen</c>
    /// asserted them on every run for that reason; this is the once-per-suite
    /// version.
    /// </remarks>
    [Fact]
    public void TheReferenceSpritesAreStillWhatBothCopiesAssume()
    {
        ReferenceTree.RequireAvailable();

        Check("sprite44", 60, 6);
        Check("sprite45", 6, 60);
        Check("sprite64", 42, 42);
        Check("SetupGateS", 32, 32);

        static void Check(string sprite, int width, int height)
        {
            var images = Directory
                .EnumerateDirectories(
                    Path.Combine(ReferenceTree.Root, "Source", "gg2", "Sprites"), sprite + ".images",
                    SearchOption.AllDirectories)
                .FirstOrDefault();

            Assert.True(images is not null, $"sprite {sprite} is no longer in the reference");

            var meta = File.ReadAllText(images![..^".images".Length] + ".xml");

            Assert.True(
                OriginPattern().IsMatch(meta),
                $"sprite {sprite} no longer has origin (0, 0) — stamping needs updating");

            Assert.Contains("<shape>RECTANGLE</shape>", meta, StringComparison.Ordinal);
            Assert.Matches(BoundsPattern(), meta);

            // The PNG's IHDR: width and height as big-endian uint32 at byte 16.
            var first = Directory.EnumerateFiles(images, "*.png").Order(StringComparer.Ordinal).First();
            var header = new byte[24];

            using (var file = File.OpenRead(first))
                file.ReadExactly(header);

            Assert.Equal((uint)width, BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(16, 4)));
            Assert.Equal((uint)height, BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20, 4)));
        }
    }

    /// <summary>
    /// ⚠️ A <c>MovingPlatform</c> is skipped rather than guessed at.
    /// </summary>
    /// <remarks>
    /// In the game it is stamped wherever it happens to be sitting when the graph
    /// is built, which is not a fact about the map at all — a moving platform reads
    /// as a static ledge at whichever end of its travel the build caught it.
    /// Offline there is no "happens to be", so inventing one would make the graph
    /// disagree with the game for a reason nothing could reproduce. No shipped map
    /// uses one, so nothing else in the suite would notice this changing.
    /// </remarks>
    [Fact]
    public void AMovingPlatformIsNotStamped()
    {
        var entity = new LevelEntity
        {
            Type = EntityTypes.MovingPlatform,
            X = 120,
            Y = 240,
            Properties = new Dictionary<string, GgonValue>(StringComparer.Ordinal),
        };

        Assert.False(Assert.Single(NavInstances.Instantiate([entity])).HasBox);
    }

    [GeneratedRegex("""<origin\s+x="0"\s+y="0"\s*/>""")]
    private static partial Regex OriginPattern();

    [GeneratedRegex("<bounds[^>]*mode=\"FULL\"")]
    private static partial Regex BoundsPattern();
}
