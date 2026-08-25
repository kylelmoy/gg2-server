using System.Text.Json;
using System.Text.Json.Serialization;
using Gg2.Maps;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// <see cref="CollisionLine"/> against the running engine.
/// </summary>
/// <remarks>
/// The last of phase 5's three oracles and by far the least guessable: the
/// other two turned out to be one-line formulas, and this one is a clip, a
/// rounding and a pixel walk whose order decides the answer. It is the Rifle's
/// entire hitscan and it gates every rocket fired, so a wrong answer here is a
/// shot that hits a wall the reference shot through.
/// <para>
/// The fixture is 9,014 segments asked of the running game
/// (<c>node tools/sample-collision-line.js</c>): seven probe boxes at both
/// coordinate parities, at positive, fractional and negative image scales and
/// with an off-centre sprite origin, plus the map's own precise 6× mask. Each
/// probe records the bounding box the engine actually gave it, so a row is a
/// self-contained statement about a rectangle the engine named.
/// </para>
/// <para>
/// <b>Both parities of every edge are in here on purpose.</b> The walk rounds
/// half to even, so whether a coordinate ending in .5 reaches a box depends on
/// whether the pixel it would round to is even — and the two 1×1 probes, one at
/// an even coordinate and one at an odd one, are what make that visible. A
/// fixture with only the six-pixel bars would pass against three different
/// wrong rounding rules.
/// </para>
/// </remarks>
public sealed class CollisionLineSampleTests
{
    private sealed record Sprite
    {
        [JsonPropertyName("originX")] public required int OriginX { get; init; }

        [JsonPropertyName("originY")] public required int OriginY { get; init; }

        /// <summary>The sprite's mask bounds, in sprite pixels: left, top, right, bottom.</summary>
        [JsonPropertyName("mask")] public required int[] Mask { get; init; }
    }

    private sealed record Probe
    {
        [JsonPropertyName("label")] public required string Label { get; init; }

        [JsonPropertyName("x")] public required double X { get; init; }

        [JsonPropertyName("y")] public required double Y { get; init; }

        [JsonPropertyName("xscale")] public required double XScale { get; init; }

        [JsonPropertyName("yscale")] public required double YScale { get; init; }

        /// <summary>Which entry of <see cref="Document.Sprites"/> is the mask.</summary>
        [JsonPropertyName("sprite")] public required string Sprite { get; init; }

        /// <summary>The instance bounding box the engine reported: left, top, right, bottom.</summary>
        [JsonPropertyName("box")] public required int[] Box { get; init; }

        /// <summary>One row per query: x1, y1, x2, y2, hit.</summary>
        [JsonPropertyName("rows")] public required double[][] Rows { get; init; }
    }

    private sealed record Walkmask
    {
        [JsonPropertyName("box")] public required int[] Box { get; init; }

        [JsonPropertyName("rows")] public required double[][] Rows { get; init; }
    }

    private sealed record Document
    {
        [JsonPropertyName("map")] public required string Map { get; init; }

        [JsonPropertyName("sprites")] public required Dictionary<string, Sprite> Sprites { get; init; }

        [JsonPropertyName("probes")] public required Probe[] Probes { get; init; }

        [JsonPropertyName("walkmask")] public required Walkmask Walkmask { get; init; }
    }

    private static Document Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "collision-line-samples.json");
        Assert.True(File.Exists(path), $"missing {path}");
        return JsonSerializer.Deserialize<Document>(File.ReadAllBytes(path))!;
    }

    private static Aabb ToAabb(int[] box) => new(box[0], box[1], box[2], box[3]);

    /// <summary>
    /// Every rectangle-masked probe agrees with the engine on every segment.
    /// </summary>
    /// <remarks>
    /// Rectangle masks are every collider in GG2 except the map — characters,
    /// gates, obstacles and bullet walls all use sprites whose mask shape is
    /// <c>RECTANGLE</c>, so the mask lookup is the bounds test and nothing else.
    /// </remarks>
    [Fact]
    public void RectangleMaskedProbesMatchTheEngine()
    {
        var document = Load();
        var checkedSamples = 0;

        foreach (var probe in document.Probes)
        {
            var sprite = document.Sprites[probe.Sprite];

            var collider = new LineCollider(
                probe.X,
                probe.Y,
                probe.XScale,
                probe.YScale,
                ToAabb(probe.Box),
                sprite.OriginX,
                sprite.OriginY,
                ToAabb(sprite.Mask));

            foreach (var row in probe.Rows)
            {
                var actual = CollisionLine.Hits(
                    collider, default(CollisionLine.FilledMask), row[0], row[1], row[2], row[3]);

                Assert.True(
                    actual == (row[4] != 0),
                    $"{probe.Label} ({row[0]}, {row[1]}) -> ({row[2]}, {row[3]}): " +
                    $"engine {row[4] != 0}, port {actual}");

                checkedSamples++;
            }
        }

        Assert.True(checkedSamples > 7000, $"only {checkedSamples} samples");
    }

    /// <summary>
    /// The map's precise mask agrees with the engine, including off every edge.
    /// </summary>
    /// <remarks>
    /// The only probe whose mask is not a filled rectangle, so the only one that
    /// exercises the per-pixel lookup — and the one the Rifle actually asks
    /// about most often, because a wall is what usually stops a shot. The
    /// off-the-edge rows matter more than they look: the walk indexes the
    /// sprite directly, and a negative index that wrapped would report a wall in
    /// mid-air.
    /// </remarks>
    [Fact]
    public void TheWalkmaskMatchesTheEngine()
    {
        var document = Load();
        var world = new CollisionWorld(
            LevelMap.FromPng(document.Map, ReferenceTree.ReadMap(document.Map)));

        Assert.Equal(ToAabb(document.Walkmask.Box), world.MapCollider.Bbox);

        var checkedSamples = 0;

        foreach (var row in document.Walkmask.Rows)
        {
            var actual = world.CollisionLineHitsMask(row[0], row[1], row[2], row[3]);

            Assert.True(
                actual == (row[4] != 0),
                $"walkmask ({row[0]}, {row[1]}) -> ({row[2]}, {row[3]}): " +
                $"engine {row[4] != 0}, port {actual}");

            checkedSamples++;
        }

        Assert.True(checkedSamples > 1800, $"only {checkedSamples} samples");
    }

    /// <summary>
    /// The fixture contains both answers on every probe, and by a wide margin.
    /// </summary>
    /// <remarks>
    /// A guard against the failure that made the <c>FULL_UPDATE</c> weapon block
    /// wrong for four phases: a fixture that only ever recorded one answer would
    /// pass against a function that always returned it. Asserted per probe, not
    /// in total, because the map rows alone would carry the total.
    /// </remarks>
    [Fact]
    public void EveryProbeRecordedBothAnswers()
    {
        var document = Load();

        foreach (var probe in document.Probes)
        {
            var hits = probe.Rows.Count(row => row[4] != 0);

            Assert.True(hits > probe.Rows.Length / 8, $"{probe.Label}: only {hits} hits");
            Assert.True(
                probe.Rows.Length - hits > probe.Rows.Length / 8,
                $"{probe.Label}: only {probe.Rows.Length - hits} misses");
        }
    }
}
