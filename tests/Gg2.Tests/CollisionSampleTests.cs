using System.Text.Json.Serialization;
using Gg2.Maps;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// Phase 3's tier-1 check: does this port's collision agree with the engine?
/// </summary>
/// <remarks>
/// The fixture is 23,040 points sampled from the <b>running game</b> with
/// <c>collision_point</c> against <c>CollisionMapO</c> — the map's own collision
/// instance, a sprite built at runtime from the walkmask and scaled six times.
/// That primitive is what every <c>place_free</c> in the physics is built on, so
/// pinning it pins the rest.
/// <para>
/// Sampled deliberately rather than uniformly, because the answer is only ever
/// in doubt at a boundary: across cell edges at both <c>x % 6 == 0</c> and
/// <c>x % 6 == 3</c>, across the origin into negative coordinates, and across
/// the far edge where the sprite stops. Regenerate with
/// <c>node tools/sample-collision.js</c> against a running dedicated server.
/// </para>
/// </remarks>
public sealed class CollisionSampleTests
{
    private sealed record SampleWindow
    {
        [JsonPropertyName("why")] public required string Why { get; init; }

        [JsonPropertyName("x")] public required int X { get; init; }

        [JsonPropertyName("y")] public required int Y { get; init; }

        [JsonPropertyName("w")] public required int W { get; init; }

        [JsonPropertyName("h")] public required int H { get; init; }

        /// <summary>Row-major '0'/'1', one per world pixel.</summary>
        [JsonPropertyName("solid")] public required string Solid { get; init; }
    }

    private sealed record SampleMap
    {
        [JsonPropertyName("name")] public required string Name { get; init; }

        [JsonPropertyName("mapWidth")] public required int MapWidth { get; init; }

        [JsonPropertyName("mapHeight")] public required int MapHeight { get; init; }

        [JsonPropertyName("maskWidth")] public required int MaskWidth { get; init; }

        [JsonPropertyName("maskHeight")] public required int MaskHeight { get; init; }

        [JsonPropertyName("windows")] public required SampleWindow[] Windows { get; init; }

        public override string ToString() => Name;
    }

    private sealed record Document(
        [property: JsonPropertyName("maps")] SampleMap[] Maps);

    private static readonly SampleMap[] Samples = Load();

    private static SampleMap[] Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "collision-samples.json");
        if (!File.Exists(path))
            throw new FileNotFoundException($"missing {path}; run tools/sample-collision.js", path);

        return System.Text.Json.JsonSerializer.Deserialize<Document>(File.ReadAllBytes(path))!.Maps;
    }

    public static TheoryData<string> MapNames()
    {
        var data = new TheoryData<string>();
        foreach (var map in Samples)
            data.Add(map.Name);
        return data;
    }

    private static SampleMap SampleFor(string name) => Samples.Single(m => m.Name == name);

    /// <summary>
    /// Every sampled point agrees with the live game, exactly.
    /// </summary>
    [Theory]
    [MemberData(nameof(MapNames))]
    public void EverySampledPointAgreesWithTheLiveGame(string name)
    {
        var sample = SampleFor(name);
        var world = new CollisionWorld(LevelMap.FromPng(name, ReferenceTree.ReadMap(name)));

        var checkedPoints = 0;
        var solidPoints = 0;

        foreach (var window in sample.Windows)
        {
            Assert.Equal(window.W * window.H, window.Solid.Length);

            for (var row = 0; row < window.H; row++)
            {
                for (var column = 0; column < window.W; column++)
                {
                    var x = window.X + column;
                    var y = window.Y + row;

                    var expected = window.Solid[row * window.W + column] == '1';
                    var actual = world.IsSolidAt(x, y);

                    if (expected != actual)
                    {
                        Assert.Fail(
                            $"{name} {window.Why}: world ({x}, {y}) -> cell " +
                            $"({CollisionWorld.CellOf(x)}, {CollisionWorld.CellOf(y)}): " +
                            $"game says {(expected ? "solid" : "open")}, port says {(actual ? "solid" : "open")}");
                    }

                    checkedPoints++;
                    if (expected)
                        solidPoints++;
                }
            }
        }

        Assert.True(checkedPoints > 5000, $"only {checkedPoints} points sampled for {name}");

        // A window set that is all open, or all solid, would agree for the wrong
        // reason. The samples are placed on real edges, so both must appear.
        Assert.InRange(solidPoints, 1, checkedPoints - 1);
    }

    /// <summary>
    /// The samples really do straddle boundaries. Without this, the test above
    /// could pass on 23,000 points that are all comfortably inside one cell.
    /// </summary>
    [Theory]
    [MemberData(nameof(MapNames))]
    public void SamplesStraddleCellBoundariesAndTheMapEdge(string name)
    {
        var sample = SampleFor(name);

        var flips = 0;
        var negatives = 0;
        var pastEdge = 0;

        foreach (var window in sample.Windows)
        {
            for (var row = 0; row < window.H; row++)
            {
                for (var column = 1; column < window.W; column++)
                {
                    var here = window.Solid[row * window.W + column];
                    var left = window.Solid[row * window.W + column - 1];
                    if (here != left)
                        flips++;
                }
            }

            if (window.X < 0 || window.Y < 0)
                negatives++;
            if (window.X + window.W > sample.MaskWidth || window.Y + window.H > sample.MaskHeight)
                pastEdge++;
        }

        Assert.True(flips > 20, $"{name}: only {flips} solid/open transitions in the samples");
        Assert.True(negatives > 0, $"{name}: no window covers a negative coordinate");
        Assert.True(pastEdge > 0, $"{name}: no window reaches past the mask");
    }

    /// <summary>
    /// The map's bounds come from the art and the collision from the mask, and
    /// they are not always the same size.
    /// </summary>
    /// <remarks>
    /// <c>map_width()</c> is <c>background_width[7] * background_xscale[7]</c>,
    /// the background image. <c>gg_debug</c> ships 120x64 of art over a 118x69
    /// mask, so its bounds reach twelve pixels past the mask horizontally and
    /// stop thirty short of it vertically. Twenty-one of the twenty-two shipped
    /// maps have them equal, which is why this is worth pinning.
    /// </remarks>
    [Theory]
    [MemberData(nameof(MapNames))]
    public void MapBoundsComeFromTheArtAndCollisionFromTheMask(string name)
    {
        var sample = SampleFor(name);
        var world = new CollisionWorld(LevelMap.FromPng(name, ReferenceTree.ReadMap(name)));

        // These are the live game's own map_width()/map_height().
        Assert.Equal(sample.MapWidth, world.Width);
        Assert.Equal(sample.MapHeight, world.Height);

        Assert.Equal(sample.MaskWidth, world.MaskWidth);
        Assert.Equal(sample.MaskHeight, world.MaskHeight);
    }

    [Fact]
    public void GgDebugIsTheMapWhereArtAndMaskDisagree()
    {
        var debug = SampleFor("gg_debug");

        Assert.Equal(720, debug.MapWidth);
        Assert.Equal(384, debug.MapHeight);
        Assert.Equal(708, debug.MaskWidth);
        Assert.Equal(414, debug.MaskHeight);
    }

    /// <summary>
    /// World pixel to cell is a floor, not a truncation. C#'s integer division
    /// rounds toward zero, so <c>-1 / 6 == 0</c> — which would read a pixel one
    /// to the left of the map as cell 0, and cell 0 is often solid.
    /// </summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(5, 0)]
    [InlineData(6, 1)]
    [InlineData(11, 1)]
    [InlineData(3156, 526)]
    [InlineData(-1, -1)]
    [InlineData(-6, -1)]
    [InlineData(-7, -2)]
    [InlineData(-12, -2)]
    public void CellOfFloorsRatherThanTruncating(int world, int cell) =>
        Assert.Equal(cell, CollisionWorld.CellOf(world));

    /// <summary>
    /// Off the mask is open, on every side. Measured: the live game answers
    /// "open" for x in -6..-1 and for x from map_width() upward.
    /// </summary>
    [Fact]
    public void OffTheMaskIsOpen()
    {
        var world = new CollisionWorld(
            LevelMap.FromPng("ctf_truefort", ReferenceTree.ReadMap("ctf_truefort")));

        for (var x = -12; x < 0; x++)
            Assert.False(world.IsSolidAt(x, 249), $"x {x} should be off the mask");

        for (var y = -12; y < 0; y++)
            Assert.False(world.IsSolidAt(357, y), $"y {y} should be off the mask");

        Assert.False(world.IsSolidAt(world.MaskWidth, 540));
        Assert.False(world.IsSolidAt(540, world.MaskHeight));
    }
}
