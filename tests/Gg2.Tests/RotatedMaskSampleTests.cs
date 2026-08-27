using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// ⚠️ <b>What GM8's overlap primitives actually test when a projectile is
/// rotated — and it is not the bounding box this port uses.</b>
/// </summary>
/// <remarks>
/// <b>Measured 2026-08-27</b> with <c>collision_point(..., precise)</c> over a
/// grid, nine sprites by eighteen angles; regenerate with
/// <c>node tools/sample-rotated-mask.js</c>. It needs a running game but no bot,
/// because every probe is a projectile it creates itself.
/// <para>
/// <b>The split, and it runs straight through the middle of one GML line.</b>
/// <c>distance_to_object</c> is computed from the bounding boxes — rotated or
/// not, and with the character's <c>image_xscale</c> inflation — which is why
/// splash damage has always come out exact and why
/// <see cref="PrimitiveSampleTests"/> passes. <c>place_free</c> and
/// <c>place_meeting</c> are computed from the <b>precise masks</b>. For an
/// unrotated sprite the two coincide, and
/// <see cref="EveryUnrotatedMaskExactlyFillsItsBoundingBox"/> is what says so —
/// so nothing in six phases ever noticed.
/// </para>
/// <para>
/// ⚠️ <b>Rotated, the box is not a small over-estimate.</b> A <c>Shot</c> at 45°
/// has 25 opaque pixels inside a 169-pixel box. The port treats all 169 as
/// solid, so a pellet fired diagonally collides with things it is six pixels
/// clear of — and the Scattergun spreads every pellet by
/// <c>random(15) - 7.5</c>, so diagonal pellets are the normal case rather than
/// the exotic one.
/// </para>
/// <para>
/// <b>What it has actually been caught costing</b> is one blast:
/// <see cref="GgDebugSplashOpenFindingTests"/>. A rocket backing out of the
/// character it hit stops as soon as it is free, and stopping on the box rather
/// than on the mask carries it six pixels too far, which is the whole of the
/// <c>gg_debug</c> damage divergence. <see cref="TheRocketsTopRowIsOnePixelAtTheDivergenceAngle"/>
/// is that six pixels, isolated.
/// </para>
/// <para>
/// ⚠️ <b>Not yet fixed, and the fix is its own piece of work</b> — it changes
/// which character every projectile in the game hits, which is observable
/// simulation, so it needs the exactness bar rather than a good approximation.
/// The obvious model gets close and not close enough: inverse-rotating each
/// world pixel into sprite space and flooring, then testing against the
/// sprite's own bbox rectangle, reproduces 1,759 of 1,764 cells over four
/// rocket angles. Five wrong cells is not a rounding preference to guess at
/// from four angles; this fixture is here so the next attempt can be checked
/// against 162 masks without the game.
/// </para>
/// </remarks>
public sealed class RotatedMaskSampleTests
{
    private sealed record Box
    {
        [JsonPropertyName("left")] public required int Left { get; init; }

        [JsonPropertyName("top")] public required int Top { get; init; }

        [JsonPropertyName("right")] public required int Right { get; init; }

        [JsonPropertyName("bottom")] public required int Bottom { get; init; }
    }

    private sealed record Sample
    {
        [JsonPropertyName("angle")] public required double Angle { get; init; }

        /// <summary>The box GM8 reported, relative to the instance's position.</summary>
        [JsonPropertyName("box")] public required Box Box { get; init; }

        /// <summary>Half-width of the sampled grid, in pixels.</summary>
        [JsonPropertyName("reach")] public required int Reach { get; init; }

        /// <summary>One row per grid line, <c>#</c> where the precise mask covers.</summary>
        [JsonPropertyName("mask")] public required string[] Mask { get; init; }

        /// <summary>Whether the mask covers the offset, which may be outside the grid.</summary>
        public bool Covers(int dx, int dy)
        {
            if (dx < -Reach || dx > Reach || dy < -Reach || dy > Reach)
                return false;

            return Mask[dy + Reach][dx + Reach] == '#';
        }

        public int OpaquePixels => Mask.Sum(row => row.Count(c => c == '#'));

        public int BoxArea => (Box.Right - Box.Left + 1) * (Box.Bottom - Box.Top + 1);
    }

    private sealed record Document
    {
        [JsonPropertyName("origin")] public required JsonElement Origin { get; init; }

        [JsonPropertyName("sprites")] public required Dictionary<string, Sample[]> Sprites { get; init; }
    }

    private static Document Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "rotated-mask-samples.json");
        Assert.True(File.Exists(path), $"missing {path}");
        return JsonSerializer.Deserialize<Document>(File.ReadAllBytes(path))!;
    }

    private static ProjectileKind KindOf(string name) => Enum.Parse<ProjectileKind>(name);

    /// <summary>
    /// ✅ <b>Unrotated, the mask and the box are the same rectangle — for all
    /// nine sprites.</b>
    /// </summary>
    /// <remarks>
    /// This is the control that says the port's model is right everywhere it has
    /// ever been exercised, and it is the reason the rotated case went unseen:
    /// <c>primitive-samples.json</c> holds 361 rocket offsets and every one of
    /// them is an unrotated rocket, so <c>place_meeting</c> was only ever asked
    /// the question where the two answers agree.
    /// <para>
    /// It also gates <see cref="ProjectileSprites"/>'s bbox table from a second
    /// direction: a sprite whose mask did <em>not</em> fill its box would mean
    /// the port is wrong at angle zero too, which would be a much larger
    /// finding.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryUnrotatedMaskExactlyFillsItsBoundingBox()
    {
        var document = Load();
        Assert.Equal(9, document.Sprites.Count);

        foreach (var (name, samples) in document.Sprites)
        {
            var flat = samples.Single(s => s.Angle == 0);

            for (var dy = flat.Box.Top; dy <= flat.Box.Bottom; dy++)
            {
                for (var dx = flat.Box.Left; dx <= flat.Box.Right; dx++)
                {
                    Assert.True(
                        flat.Covers(dx, dy),
                        $"{name} at angle 0: the box covers ({dx},{dy}) and the mask does not");
                }
            }

            Assert.Equal(flat.BoxArea, flat.OpaquePixels);
        }
    }

    /// <summary>
    /// The box GM8 reports for a rotated sprite is the one
    /// <see cref="Aabb.ForRotatedSprite"/> computes — exactly on 156 of these
    /// 162, and within one pixel on a rounding tie for the other six.
    /// </summary>
    /// <remarks>
    /// A cross-check rather than a new claim: <c>rotated-box-samples.json</c>
    /// already holds 1,008 of these and <see cref="RotatedBoxSampleTests"/>
    /// gates them. It is repeated because this file's subject is the gap
    /// between the box and the mask, and that comparison only means anything if
    /// the box half is known to be right.
    /// <para>
    /// ⚠️ <b>The tie exemption is the same principled one</b>, and deliberately
    /// not a list of names: a difference is allowed only where the model itself
    /// says a rotated corner is sitting within <c>1e-9</c> of a half pixel, and
    /// only by one pixel. <b>Do not fudge the rounding</b> —
    /// <see cref="Aabb.ForRotatedSprite"/>'s remarks are explicit that a fudge
    /// would be wrong at every angle instead of at a few, and a tie is
    /// unreachable in play because every pellet's direction carries
    /// <c>random(15) - 7.5</c>.
    /// </para>
    /// <para>
    /// ⚠️ <b>And it corrects that note in one respect.</b> It says the ties are
    /// "210 and 330 degrees, where <c>sin</c> is analytically -0.5". This
    /// fixture puts three of the six at <b>300</b>, where it is <c>cos</c> that
    /// is analytically 0.5 — same mechanism, an axis the old sampler's angle
    /// grid did not happen to expose. The predicate below already covered it;
    /// only the prose was narrow.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheReportedBoxIsTheOneThePortComputesExceptOnARoundingTie()
    {
        var document = Load();
        var total = 0;
        var exact = 0;
        var ties = 0;

        foreach (var (name, samples) in document.Sprites)
        {
            var sprite = ProjectileSprites.For(KindOf(name));

            foreach (var sample in samples)
            {
                total++;

                var box = Aabb.ForRotatedSprite(
                    sprite.Bbox, sprite.OriginX, sprite.OriginY, 3000, 600, sample.Angle);

                var expected = new Aabb(
                    3000 + sample.Box.Left,
                    600 + sample.Box.Top,
                    3000 + sample.Box.Right,
                    600 + sample.Box.Bottom);

                if (box == expected)
                {
                    exact++;
                    continue;
                }

                var where = $"{name} at {sample.Angle} degrees: {box} != engine {expected}";
                Assert.True(IsRoundingTie(sprite, sample.Angle), where);

                Assert.True(
                    Math.Abs(box.Left - expected.Left) <= 1
                        && Math.Abs(box.Top - expected.Top) <= 1
                        && Math.Abs(box.Right - expected.Right) <= 1
                        && Math.Abs(box.Bottom - expected.Bottom) <= 1,
                    $"{where} — a tie may move an edge by one pixel, not more");

                ties++;
            }
        }

        Assert.Equal(162, total);
        Assert.True(exact >= 156, $"only {exact} of {total} boxes are exact; that was 156 when measured");
        Assert.True(ties <= 6, $"{ties} boxes needed the tie exemption; that was 6 when measured");
    }

    /// <summary>
    /// Is any rotated corner sitting on a half pixel, where the engine and the
    /// port can round apart?
    /// </summary>
    /// <remarks>
    /// The same test <see cref="RotatedBoxSampleTests"/> applies, against the
    /// same <c>1e-9</c> window and for the same reason: rounding is the one
    /// discontinuity where docs/SIMULATION.md's x87 gap — 1e-15 and invisible
    /// everywhere else — can move an integer.
    /// </remarks>
    private static bool IsRoundingTie(in ProjectileSprites.Sprite sprite, double degrees)
    {
        var radians = Gg2.Protocol.Gml.DegToRad(degrees);
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);

        var left = sprite.Bbox.Left - sprite.OriginX;
        var right = sprite.Bbox.Right - sprite.OriginX;
        var top = sprite.Bbox.Top - sprite.OriginY;
        var bottom = sprite.Bbox.Bottom - sprite.OriginY;

        foreach (var (dx, dy) in new[] { (left, top), (right, top), (left, bottom), (right, bottom) })
        {
            foreach (var v in new[] { (dx * cos) + (dy * sin), (-dx * sin) + (dy * cos) })
            {
                if (Math.Abs(Math.Abs(v - Math.Floor(v)) - 0.5) < 1e-9)
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// ⚠️ <b>The open finding, stated as a number: every sprite's box badly
    /// over-states its mask once it is turned.</b>
    /// </summary>
    /// <remarks>
    /// The port has no mask model at all — <c>Aabb.Overlaps</c> is the whole of
    /// its collision test — so today it treats every one of these boxes as
    /// solid. The worst case per sprite is pinned so that a fix has to come
    /// through here and say what it changed.
    /// <para>
    /// <b>This does not fail when the finding is fixed</b>, unlike the two in
    /// <see cref="GgDebugSplashOpenFindingTests"/>: it is a statement about the
    /// engine, not about the port, and it stays true afterwards. What it would
    /// catch is a re-recording that quietly disagreed with the measurement this
    /// file is built on.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("Rocket", 45, 16, 42)]
    [InlineData("Shot", 45, 25, 169)]
    [InlineData("Needle", 45, 75, 225)]
    [InlineData("Flare", 30, 7, 12)]
    [InlineData("Mine", 30, 35, 64)]
    [InlineData("Flame", 9, 7, 9)]
    [InlineData("Blade", 45, 286, 552)]
    [InlineData("Bubble", 45, 144, 272)]
    [InlineData("Stab", 45, 1440, 2916)]
    public void ARotatedBoxOverStatesItsMask(string sprite, double angle, int opaque, int area)
    {
        var sample = Load().Sprites[sprite].Single(s => s.Angle == angle);

        Assert.Equal(area, sample.BoxArea);
        Assert.Equal(opaque, sample.OpaquePixels);

        // The mask never reaches outside the box, which is what makes the box a
        // sound broad-phase and the port's answer an over-estimate rather than
        // simply a different one.
        for (var dy = -sample.Reach; dy <= sample.Reach; dy++)
        {
            for (var dx = -sample.Reach; dx <= sample.Reach; dx++)
            {
                if (!sample.Covers(dx, dy))
                    continue;

                Assert.True(
                    dx >= sample.Box.Left && dx <= sample.Box.Right
                        && dy >= sample.Box.Top && dy <= sample.Box.Bottom,
                    $"{sprite} at {angle}: mask covers ({dx},{dy}), outside the reported box");
            }
        }
    }

    /// <summary>
    /// ⚠️ <b>The six pixels that <c>gg_debug</c> costs, isolated: at the
    /// divergence angle a rocket's top row is ONE pixel wide, and the port
    /// treats it as eight.</b>
    /// </summary>
    /// <remarks>
    /// 351.002197265625° is the exact heading of the rocket that struck slot 2
    /// on tick 475 — a wire value, since <c>netAimDirection</c> is 16 bits, so it
    /// is reproducible rather than approximate. The rocket overlapped that
    /// character on its top row alone, and backing out along <c>direction + 180</c>
    /// it clears the character as soon as that row does.
    /// <para>
    /// One pixel at offset 0 versus a row running from -1 to +6 is six whole
    /// pixels of back-out, which is exactly the gap between the port's
    /// <c>distance_to_object</c> of 36 and the 30 the reference's damage
    /// demands. See <see cref="GgDebugSplashOpenFindingTests"/> for the
    /// arithmetic that pins 30 from two independent directions.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheRocketsTopRowIsOnePixelAtTheDivergenceAngle()
    {
        var sample = Load().Sprites["Rocket"].Single(s => s.Angle == 351.002197265625);

        Assert.Equal(-1, sample.Box.Top);
        Assert.Equal(-1, sample.Box.Left);
        Assert.Equal(6, sample.Box.Right);

        var topRow = Enumerable
            .Range(sample.Box.Left, sample.Box.Right - sample.Box.Left + 1)
            .Where(dx => sample.Covers(dx, sample.Box.Top))
            .ToArray();

        Assert.Equal([0], topRow);

        // And the row below it is the full eight, so the narrowing is the mask's
        // shape rather than the whole sprite having moved.
        var nextRow = Enumerable
            .Range(sample.Box.Left, sample.Box.Right - sample.Box.Left + 1)
            .Where(dx => sample.Covers(dx, sample.Box.Top + 1))
            .ToArray();

        Assert.Equal([-1, 0, 1, 2, 3, 4, 5, 6], nextRow);
    }
}
