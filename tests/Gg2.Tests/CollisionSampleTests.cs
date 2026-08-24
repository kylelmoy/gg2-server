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

    /// <summary>
    /// A character box swept across real geometry an eighth of a pixel at a
    /// time, with the live game's <c>place_free</c> answer at each step.
    /// </summary>
    private sealed record SampleSweep
    {
        [JsonPropertyName("class")] public required string Class { get; init; }

        /// <summary>"x" or "y" — which coordinate moves.</summary>
        [JsonPropertyName("axis")] public required string Axis { get; init; }

        [JsonPropertyName("x")] public required double X { get; init; }

        [JsonPropertyName("y")] public required double Y { get; init; }

        [JsonPropertyName("step")] public required double Step { get; init; }

        [JsonPropertyName("steps")] public required int Steps { get; init; }

        /// <summary>'1' where the game said free.</summary>
        [JsonPropertyName("free")] public required string Free { get; init; }
    }

    private sealed record SampleMap
    {
        [JsonPropertyName("name")] public required string Name { get; init; }

        [JsonPropertyName("mapWidth")] public required int MapWidth { get; init; }

        [JsonPropertyName("mapHeight")] public required int MapHeight { get; init; }

        [JsonPropertyName("maskWidth")] public required int MaskWidth { get; init; }

        [JsonPropertyName("maskHeight")] public required int MaskHeight { get; init; }

        [JsonPropertyName("windows")] public required SampleWindow[] Windows { get; init; }

        [JsonPropertyName("sweeps")] public SampleSweep[] Sweeps { get; init; } = [];

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
    /// <c>PlaceFree</c> agrees with the live game's, at fractional positions.
    /// </summary>
    /// <remarks>
    /// Each sweep moves a real character box an eighth of a pixel at a time
    /// across real geometry, anchored so the flip from free to blocked falls in
    /// the middle. Three classes with three different box widths, because a rule
    /// that happens to work for one box size is not a rule.
    /// <para>
    /// The eighth-pixel step is chosen so every sweep lands on the halves, which
    /// is the only place round-half-to-even and round-half-up disagree.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(MapNames))]
    public void PlaceFreeAgreesWithTheLiveGameAtFractionalPositions(string name)
    {
        var sample = SampleFor(name);
        var world = new CollisionWorld(LevelMap.FromPng(name, ReferenceTree.ReadMap(name)));

        Assert.NotEmpty(sample.Sweeps);
        var queries = 0;

        foreach (var sweep in sample.Sweeps)
        {
            var box = CharacterBox.For(Enum.Parse<Gg2.Protocol.PlayerClass>(sweep.Class));
            Assert.Equal(sweep.Steps, sweep.Free.Length);

            // Every sweep must actually cross the boundary, or it agrees with
            // anything.
            Assert.Contains('1', sweep.Free);
            Assert.Contains('0', sweep.Free);

            for (var step = 0; step < sweep.Steps; step++)
            {
                var offset = step * sweep.Step;
                var x = sweep.Axis == "x" ? sweep.X + offset : sweep.X;
                var y = sweep.Axis == "x" ? sweep.Y : sweep.Y + offset;

                var expected = sweep.Free[step] == '1';
                var actual = world.PlaceFree(box, x, y);

                if (expected != actual)
                {
                    Assert.Fail(
                        $"{name} {sweep.Class} sweep along {sweep.Axis}: at ({x}, {y}) " +
                        $"(rounded to {CollisionWorld.RoundPosition(x)}, {CollisionWorld.RoundPosition(y)}) " +
                        $"game says {(expected ? "free" : "blocked")}, port says {(actual ? "free" : "blocked")}");
                }

                queries++;
            }
        }

        Assert.True(queries > 1000, $"only {queries} place_free queries for {name}");
    }

    /// <summary>
    /// The rounding is half to <b>even</b>, and the sweeps prove it rather than
    /// merely being consistent with it.
    /// </summary>
    /// <remarks>
    /// Round-half-up and round-half-to-even differ only at exact halves, so a
    /// sweep only discriminates where a half sits on the boundary. Each of the
    /// three classes has a different box width, which puts the threshold at a
    /// different parity — so between them the sweeps cover both the case where
    /// the even neighbour is below and where it is above.
    /// </remarks>
    [Theory]
    [MemberData(nameof(MapNames))]
    public void HalvesRoundToEvenAndTheSamplesProveIt(string name)
    {
        var sample = SampleFor(name);
        var world = new CollisionWorld(LevelMap.FromPng(name, ReferenceTree.ReadMap(name)));

        var discriminating = 0;

        foreach (var sweep in sample.Sweeps)
        {
            var box = CharacterBox.For(Enum.Parse<Gg2.Protocol.PlayerClass>(sweep.Class));

            for (var step = 0; step < sweep.Steps; step++)
            {
                var offset = step * sweep.Step;
                var x = sweep.Axis == "x" ? sweep.X + offset : sweep.X;
                var y = sweep.Axis == "x" ? sweep.Y : sweep.Y + offset;

                var moving = sweep.Axis == "x" ? x : y;
                if (Math.Abs(moving - Math.Truncate(moving) - 0.5) > 1e-9)
                    continue; // not an exact half

                // What half-up would have decided.
                var halfUp = (int)Math.Round(moving, MidpointRounding.AwayFromZero);
                var toEven = CollisionWorld.RoundPosition(moving);
                if (halfUp == toEven)
                    continue; // the two rules agree here

                var underHalfUp = sweep.Axis == "x"
                    ? world.PlaceFree(box, halfUp, y)
                    : world.PlaceFree(box, x, halfUp);

                var actual = world.PlaceFree(box, x, y);
                var expected = sweep.Free[step] == '1';

                Assert.Equal(expected, actual);

                if (underHalfUp != expected)
                    discriminating++;
            }
        }

        Assert.True(discriminating > 0,
            $"{name}: no sampled half distinguishes round-half-to-even from round-half-up");
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
