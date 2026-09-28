using System.Text.Json;
using System.Text.Json.Serialization;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// <see cref="Aabb.ForRotatedSprite"/> against the bounding box the engine
/// gives a rotated instance.
/// </summary>
/// <remarks>
/// Phase 4 never needed this and phase 5 cannot start without it. A character's
/// <c>image_angle</c> is always 0, so its box is a fixed rectangle offset by its
/// position; but <c>Scripts/Object Creation/createShot.gml</c> sets
/// <c>image_angle = dir</c> on every projectile it creates, and GM8 reports the
/// axis-aligned bound of the rotated sprite. A scattergun pellet fired flat has
/// an 18x2 box; the same pellet at 45 degrees has a 13x13 one. That box is what
/// every hit is decided against.
/// <para>
/// Measured over all seven projectile sprites at 144 angles each. Regenerate
/// with <c>node gg2-samplers/sample-rotated-box.js</c>.
/// </para>
/// </remarks>
public sealed class RotatedBoxSampleTests
{
    /// <summary>
    /// How close a rotated corner has to sit to a half pixel before its
    /// rounding is a coin toss.
    /// </summary>
    /// <remarks>
    /// Rounding is a discontinuity, so it is the one place the x87 gap in
    /// docs/SIMULATION.md's "four motion variables" — everywhere else 1e-15 and
    /// invisible — can change an integer. A corner this close to a half is a tie
    /// and the engine may break it the other way.
    /// </remarks>
    private const double TieWindow = 1e-9;

    private sealed record Sprite
    {
        [JsonPropertyName("bboxLeft")] public required int BboxLeft { get; init; }

        [JsonPropertyName("bboxTop")] public required int BboxTop { get; init; }

        [JsonPropertyName("bboxRight")] public required int BboxRight { get; init; }

        [JsonPropertyName("bboxBottom")] public required int BboxBottom { get; init; }

        [JsonPropertyName("xOrigin")] public required double XOrigin { get; init; }

        [JsonPropertyName("yOrigin")] public required double YOrigin { get; init; }

        [JsonPropertyName("xScale")] public required double XScale { get; init; }

        [JsonPropertyName("yScale")] public required double YScale { get; init; }
    }

    private sealed record Row
    {
        /// <summary>Degrees, as the numerator and denominator that were sent.</summary>
        [JsonPropertyName("angle")] public required double[] Angle { get; init; }

        /// <summary>The engine's <c>bbox_*</c>, relative to the instance position.</summary>
        [JsonPropertyName("left")] public required int Left { get; init; }

        [JsonPropertyName("top")] public required int Top { get; init; }

        [JsonPropertyName("right")] public required int Right { get; init; }

        [JsonPropertyName("bottom")] public required int Bottom { get; init; }
    }

    private sealed record ObjectSamples
    {
        [JsonPropertyName("object")] public required string Object { get; init; }

        [JsonPropertyName("sprite")] public required Sprite Sprite { get; init; }

        [JsonPropertyName("rows")] public required Row[] Rows { get; init; }
    }

    private sealed record Document
    {
        [JsonPropertyName("objects")] public required ObjectSamples[] Objects { get; init; }
    }

    private static Document Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "rotated-box-samples.json");
        Assert.True(File.Exists(path), $"missing {path}");
        return JsonSerializer.Deserialize<Document>(File.ReadAllBytes(path))!;
    }

    /// <summary>
    /// Is any rotated corner sitting on a half pixel, where the engine and the
    /// port can round apart?
    /// </summary>
    private static bool IsRoundingTie(in Sprite sprite, double degrees)
    {
        var radians = Gg2.Protocol.Gml.DegToRad(degrees);
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);

        var left = (sprite.BboxLeft - sprite.XOrigin) * sprite.XScale;
        var right = (sprite.BboxRight - sprite.XOrigin) * sprite.XScale;
        var top = (sprite.BboxTop - sprite.YOrigin) * sprite.YScale;
        var bottom = (sprite.BboxBottom - sprite.YOrigin) * sprite.YScale;

        foreach (var (dx, dy) in new[] { (left, top), (right, top), (left, bottom), (right, bottom) })
        {
            foreach (var v in new[] { (dx * cos) + (dy * sin), (-dx * sin) + (dy * cos) })
            {
                if (Math.Abs(Math.Abs(v - Math.Floor(v)) - 0.5) < TieWindow)
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Every sampled box matches the engine exactly, or differs by one pixel on
    /// an edge whose rotated corner is a rounding tie.
    /// </summary>
    /// <remarks>
    /// 1,002 of 1,008 are exact; the six that are not are 210 and 330 degrees on
    /// the three sprites with an asymmetric box, where <c>sin</c> is analytically
    /// exactly -0.5. Neither the count of exact matches nor the tie exemption is
    /// left implicit: the exemption is granted only where the model itself says
    /// a corner is on a half, so it cannot quietly cover an ordinary mistake.
    /// <para>
    /// Those two angles are this fixture's grid, not the rule.
    /// <c>rotated-mask-samples.json</c> finds ties at 300 as well, where
    /// it is <c>cos</c> that is analytically 0.5 (<see cref="RotatedMaskSampleTests"/>).
    /// <see cref="IsRoundingTie"/> already answers for both because it asks
    /// about the corner rather than about the angle — which is exactly why the
    /// exemption is written as a predicate and not as a list of angles.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryRotatedBoxMatchesTheEngine()
    {
        var objects = Load().Objects;
        Assert.NotEmpty(objects);

        var total = 0;
        var exact = 0;
        var tiesAllowed = 0;

        foreach (var samples in objects)
        {
            var sprite = new Aabb(
                samples.Sprite.BboxLeft,
                samples.Sprite.BboxTop,
                samples.Sprite.BboxRight,
                samples.Sprite.BboxBottom);

            foreach (var row in samples.Rows)
            {
                total++;
                var degrees = row.Angle[0] / row.Angle[1];

                // Position 0, because the fixture stores the box relative to the
                // instance. An integer position adds nothing to round.
                var box = Aabb.ForRotatedSprite(
                    sprite,
                    samples.Sprite.XOrigin,
                    samples.Sprite.YOrigin,
                    0,
                    0,
                    degrees,
                    samples.Sprite.XScale,
                    samples.Sprite.YScale);

                var expected = new Aabb(row.Left, row.Top, row.Right, row.Bottom);

                if (box == expected)
                {
                    exact++;
                    continue;
                }

                var where = $"{samples.Object} at {degrees} degrees: {box} != engine {expected}";
                Assert.True(IsRoundingTie(samples.Sprite, degrees), where);

                Assert.True(
                    Math.Abs(box.Left - expected.Left) <= 1
                    && Math.Abs(box.Top - expected.Top) <= 1
                    && Math.Abs(box.Right - expected.Right) <= 1
                    && Math.Abs(box.Bottom - expected.Bottom) <= 1,
                    $"{where} — a tie may move an edge by one pixel, not more");

                tiesAllowed++;
            }
        }

        Assert.True(total > 1000, $"only {total} boxes in the fixture");

        // A floor rather than an equality, so a genuine improvement is not a
        // failure — but tight enough that losing the rule entirely cannot pass.
        Assert.True(
            exact * 1000 >= total * 990,
            $"only {exact} of {total} rotated boxes are exact; that was 1002/1008 when this was measured");

        Assert.True(tiesAllowed <= 8, $"{tiesAllowed} boxes needed the tie exemption; that was 6 when measured");
    }

    /// <summary>
    /// The unrotated box is the sprite's own, and the rotation is not a no-op.
    /// </summary>
    /// <remarks>
    /// Named separately because it is the case a reader will assume and the one
    /// a regression would most plausibly collapse to. A <c>Shot</c> at 0 degrees
    /// is 18 pixels wide and 2 tall with its origin at the tip; at 90 degrees it
    /// is 2 by 18. If <c>image_angle</c> were ever dropped on the way into
    /// <see cref="Aabb.ForRotatedSprite"/>, every pellet would keep the flat box
    /// and vertical shots would stop hitting anything.
    /// </remarks>
    [Fact]
    public void AShotIsFlatAtZeroAndUprightAtNinety()
    {
        var shot = Load().Objects.Single(o => o.Object == "Shot");

        var flat = shot.Rows.Single(r => r.Angle[0] == 0);
        Assert.Equal(18, flat.Right - flat.Left + 1);
        Assert.Equal(2, flat.Bottom - flat.Top + 1);

        // The origin is the tip of the streak: the box runs back from it.
        Assert.Equal(0, flat.Right);

        var upright = shot.Rows.Single(r => r.Angle[0] == 900);
        Assert.Equal(2, upright.Right - upright.Left + 1);
        Assert.Equal(18, upright.Bottom - upright.Top + 1);
    }

    /// <summary>
    /// <see cref="ProjectileSprites"/> holds the same numbers this fixture was
    /// measured with.
    /// </summary>
    /// <remarks>
    /// The simulation cannot read the fixture — it has no I/O — so the sprite
    /// metadata is a small hand-written table beside the code that uses it. That
    /// makes it the one thing here that could silently drift away from the
    /// oracle, which is exactly what this closes: the table and the measurement
    /// are checked against each other rather than merely descended from the same
    /// afternoon.
    /// <para>
    /// Every sampled sprite is at scale 1, so a non-unit scale in the fixture
    /// would mean the table is describing something else and is asserted away
    /// rather than quietly folded in.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheSpriteTableMatchesWhatWasMeasured()
    {
        var kinds = new Dictionary<string, ProjectileKind>(StringComparer.Ordinal)
        {
            ["Shot"] = ProjectileKind.Shot,
            ["Rocket"] = ProjectileKind.Rocket,
            ["Needle"] = ProjectileKind.Needle,
            ["Flare"] = ProjectileKind.Flare,
            ["Mine"] = ProjectileKind.Mine,
            ["Flame"] = ProjectileKind.Flame,
            ["BladeB"] = ProjectileKind.Blade,
        };

        var checkedSprites = 0;

        foreach (var samples in Load().Objects)
        {
            Assert.True(kinds.TryGetValue(samples.Object, out var kind),
                $"{samples.Object} is in the fixture and not in the table");

            Assert.Equal(1, samples.Sprite.XScale);
            Assert.Equal(1, samples.Sprite.YScale);

            var sprite = ProjectileSprites.For(kind);

            Assert.Equal(
                new Aabb(
                    samples.Sprite.BboxLeft,
                    samples.Sprite.BboxTop,
                    samples.Sprite.BboxRight,
                    samples.Sprite.BboxBottom),
                sprite.Bbox);

            Assert.Equal(samples.Sprite.XOrigin, sprite.OriginX);
            Assert.Equal(samples.Sprite.YOrigin, sprite.OriginY);

            checkedSprites++;
        }

        Assert.Equal(kinds.Count, checkedSprites);
    }
}
