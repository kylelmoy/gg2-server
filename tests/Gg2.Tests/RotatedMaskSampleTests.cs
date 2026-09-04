using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// <b>What GM8's overlap primitives actually test when a projectile is
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
/// <b>Rotated, the box is not a small over-estimate.</b> A <c>Shot</c> at 45°
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
/// <b>Not yet fixed, and the fix is its own piece of work</b> — it changes
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
    /// <b>Unrotated, the mask and the box are the same rectangle — for all
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
    /// <b>The tie exemption is the same principled one</b>, and deliberately
    /// not a list of names: a difference is allowed only where the model itself
    /// says a rotated corner is sitting within <c>1e-9</c> of a half pixel, and
    /// only by one pixel. <b>Do not fudge the rounding</b> —
    /// <see cref="Aabb.ForRotatedSprite"/>'s remarks are explicit that a fudge
    /// would be wrong at every angle instead of at a few, and a tie is
    /// unreachable in play because every pellet's direction carries
    /// <c>random(15) - 7.5</c>.
    /// </para>
    /// <para>
    /// <b>And it corrects that note in one respect.</b> It says the ties are
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
    /// <b>The open finding, stated as a number: every sprite's box badly
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
    /// <b>The six pixels that <c>gg_debug</c> costs, isolated: at the
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

    /// <summary>
    /// <b><see cref="ProjectileMask"/> against every sampled pixel: 99.9736%,
    /// and every single mismatch is a rounding tie.</b>
    /// </summary>
    /// <remarks>
    /// <b>The number that justifies the model is the one it replaced.</b>
    /// Treating the reported bounding box as solid — what this port did until
    /// 2026-08-27, and what <see cref="BoundingBoxAloneIsWrongOnOneSampledPixelInTwelve"/>
    /// measures — is right on 91.5% of these pixels. The mask is right on
    /// 99.9736%: 82 wrong where the box was 26,342.
    /// <para>
    /// <b>And the 82 are not a residue traded away — every one is a rounding
    /// tie</b>, asserted below rather than counted. A transformed coordinate
    /// within <c>1e-9</c> of an integer is where docs/SIMULATION.md's x87 gap
    /// decides a floor the other way; it is the class
    /// <see cref="Aabb.ForRotatedSprite"/> documents and the same instruction
    /// applies — <b>do not fudge it</b>. A structural mismatch would mean the
    /// model itself is wrong, and there are none.
    /// </para>
    /// <para>
    /// <b>An earlier version of this test scored 431 and blamed 171 of them
    /// on the model.</b> It scored <c>Covers</c> across the whole sampled grid
    /// while the shipped path only ever asks inside the reported box, so it
    /// counted disagreements about pixels no caller can reach. <b>Score what the
    /// code does, not what a helper would say if asked a question it is never
    /// asked.</b>
    /// </para>
    /// </remarks>
    [Fact]
    public void TheMaskModelMatchesTheEngineOnAlmostEverySampledPixel()
    {
        var (wrong, total, structural) = ScoreEveryPixel(mask: true);

        Assert.Equal(310194, total);

        // The assertion with the content. A tie is arithmetic this project
        // has already decided it cannot win; a structural mismatch is the model
        // being wrong, and there are none.
        Assert.True(
            structural == 0,
            $"{structural} of the {wrong} mismatches are not rounding ties — the model is wrong, "
                + "not merely out-rounded. docs/TRAPS-SIMULATION.md lists the families already "
                + "ruled out; do not start from scratch.");

        Assert.True(
            wrong <= 100,
            $"the mask is wrong on {wrong} of {total} sampled pixels; that was 82 when measured");
    }

    /// <summary>
    /// The control that says the model earns its keep: the bounding box on its
    /// own is wrong on one sampled pixel in twelve.
    /// </summary>
    /// <remarks>
    /// Without this the accuracy above is a number with nothing to beat. With
    /// it, deleting <see cref="ProjectileMask"/> and going back to
    /// <c>Aabb.Overlaps</c> moves 26,342 pixels, which is what the two
    /// assertions between them refuse to let happen quietly.
    /// </remarks>
    [Fact]
    public void BoundingBoxAloneIsWrongOnOneSampledPixelInTwelve()
    {
        var (wrong, total, _) = ScoreEveryPixel(mask: false);

        Assert.Equal(310194, total);
        Assert.True(
            wrong is > 20000 and < 30000,
            $"the box alone is wrong on {wrong} of {total}; that was 26,342 when measured");
    }

    /// <summary>
    /// Every sampled pixel of every mask, against either the model or the box.
    /// </summary>
    /// <returns>
    /// How many pixels disagreed, how many were compared, and how many of the
    /// disagreements are <em>not</em> explained by a rounding tie.
    /// </returns>
    private static (int Wrong, int Total, int Structural) ScoreEveryPixel(bool mask)
    {
        var document = Load();
        var wrong = 0;
        var total = 0;
        var structural = 0;

        foreach (var (name, samples) in document.Sprites)
        {
            foreach (var sample in samples)
            {
                // The probe sat on an exact integer position, which is what
                // makes these answers integers rather than a rounding.
                var projectile = new ProjectileState
                {
                    Kind = KindOf(name),
                    X = 3000,
                    Y = 600,
                    ImageAngle = sample.Angle,
                };

                // The port's own box, which on six of these 162 samples is one
                // pixel from the engine's. Those are the rounding ties
                // RotatedBoxSampleTests already exempts, and a mask disagreement
                // at an edge only one of the two boxes covers is that same tie
                // showing through rather than a second fault.
                var portBox = projectile.Box;
                var boxAgrees = portBox.Left - 3000 == sample.Box.Left
                    && portBox.Right - 3000 == sample.Box.Right
                    && portBox.Top - 600 == sample.Box.Top
                    && portBox.Bottom - 600 == sample.Box.Bottom;

                for (var dy = -sample.Reach; dy <= sample.Reach; dy++)
                {
                    for (var dx = -sample.Reach; dx <= sample.Reach; dx++)
                    {
                        var expected = sample.Covers(dx, dy);

                        var actual = mask
                            ? ProjectileMask.Covers(projectile, 3000 + dx, 600 + dy)
                            : dx >= sample.Box.Left && dx <= sample.Box.Right
                                && dy >= sample.Box.Top && dy <= sample.Box.Bottom;

                        total++;

                        if (actual == expected)
                            continue;

                        wrong++;

                        // The same 1e-9 window RotatedBoxSampleTests uses, on the
                        // coordinate this model floors rather than on a rotated
                        // corner.
                        var radians = Gg2.Protocol.Gml.DegToRad(sample.Angle);
                        var u = (dx * Math.Cos(radians)) - (dy * Math.Sin(radians));
                        var v = (dx * Math.Sin(radians)) + (dy * Math.Cos(radians));

                        var maskTie = Math.Abs(u - Math.Round(u)) < 1e-9
                            || Math.Abs(v - Math.Round(v)) < 1e-9;

                        // A pixel the two boxes disagree about, on a sample whose
                        // boxes disagree at all.
                        var boxTie = !boxAgrees
                            && (dx >= sample.Box.Left && dx <= sample.Box.Right
                                && dy >= sample.Box.Top && dy <= sample.Box.Bottom)
                                != (3000 + dx >= portBox.Left && 3000 + dx <= portBox.Right
                                    && 600 + dy >= portBox.Top && 600 + dy <= portBox.Bottom);

                        if (!maskTie && !boxTie)
                            structural++;
                    }
                }
            }
        }

        return (wrong, total, structural);
    }

    /// <summary>
    /// <b>Two projectiles whose boxes overlap and whose masks do not — the
    /// case the bubble sites were getting wrong.</b>
    /// </summary>
    /// <remarks>
    /// <b>This is the only gate the two-projectile overload has, and it needs
    /// one.</b> Wiring it into <c>HitBubble</c> and <c>BubbleHitProjectiles</c>
    /// moved <b>no</b> fixture window — Quote's blade is the only source of
    /// bubbles and no recording has a pellet grazing one — so unlike the
    /// character path there is no recording to notice a regression here.
    /// <para>
    /// The geometry is chosen so the two boxes touch at exactly one pixel, and
    /// that pixel is the corner of the rocket's box furthest from its mask: a
    /// rocket at 45° is a diagonal bar, and its box's top-left corner is empty.
    /// The control below asserts the boxes really do overlap, because without it
    /// this test would pass just as well against geometry that never touched at
    /// all.
    /// </para>
    /// </remarks>
    [Fact]
    public void TwoProjectilesCanShareABoxCornerWithoutTheirMasksMeeting()
    {
        var rocket = new ProjectileState
        {
            Kind = ProjectileKind.Rocket,
            X = 1000,
            Y = 1000,
            ImageAngle = 45,
        };

        // Placed so its box's bottom-right corner is the rocket box's top-left.
        var bubble = new ProjectileState
        {
            Kind = ProjectileKind.Bubble,
            X = 994,
            Y = 990,
        };

        var rocketBox = rocket.Box;
        var bubbleBox = bubble.Box;

        // The control: one shared pixel, so the box test says they meet.
        Assert.Equal(999, rocketBox.Left);
        Assert.Equal(995, rocketBox.Top);
        Assert.Equal(999, bubbleBox.Right);
        Assert.Equal(995, bubbleBox.Bottom);
        Assert.True(rocketBox.Overlaps(bubbleBox), "the boxes must overlap or this proves nothing");

        // The bubble is unrotated, so its mask fills its box and it covers the
        // shared pixel. The rocket does not: at 45° that corner is empty.
        Assert.True(ProjectileMask.Covers(bubble, 999, 995));
        Assert.False(ProjectileMask.Covers(rocket, 999, 995));

        Assert.False(
            ProjectileMask.Overlaps(in rocket, in rocketBox, in bubble, in bubbleBox),
            "the masks do not share a pixel, so the projectiles are not touching");
    }

    /// <summary>
    /// <b>MEASURED 2026-08-28: a precise mask hangs off the ROUNDED position,
    /// not the exact one</b> — so <c>ProjectileMask</c>'s anchor is right.
    /// </summary>
    /// <remarks>
    /// <b>This was the open half of the mask model.</b> Every sample in the
    /// fixture above is taken at an exact integer position, deliberately, "so
    /// the bbox rounding is not part of what is measured" — which left the
    /// anchor a choice rather than a measurement. It mattered because a
    /// projectile is essentially never at an integer position: a rocket's x
    /// moves by <c>hspeed</c> every tick.
    /// <para>
    /// <c>tools/probes/probe-fractional-mask.js</c>, rotated Rocket at 30°,
    /// rasterised at x + 0, +0.25, +0.5, +0.75:
    /// <b>+0.25 is pixel-identical to +0, and +0.75 is +0 shifted exactly one
    /// pixel right.</b> That is rounding, and it rules out the mask following
    /// the real position — which would have moved a boundary between +0 and
    /// +0.25.
    /// </para>
    /// <para>
    /// This test asserts the port has the same property rather than checking in
    /// another grid: the fixture already pins <em>what</em> the mask is, and
    /// what was unknown was only <em>where it hangs</em>.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(0.25, 0.0)]
    [InlineData(0.75, 1.0)]
    [InlineData(-0.25, 0.0)]
    public void AFractionalPositionCollidesAsItsRoundedOne(double offset, double equivalent)
    {
        var rocket = new ProjectileState
        {
            Kind = ProjectileKind.Rocket,
            X = 3000 + offset,
            Y = 3000,
            OwnerSlot = CharacterState.Nobody,
            Team = Team.Red,
            Source = DamageSource.Rocketlauncher,
            ImageAngle = 30,
        };

        var control = rocket with { X = 3000 + equivalent };

        var covered = 0;

        for (var py = 3000 - 9; py <= 3000 + 9; py++)
        for (var px = 3000 - 9; px <= 3000 + 9; px++)
        {
            var a = ProjectileMask.Covers(in rocket, px, py);
            Assert.Equal(a, ProjectileMask.Covers(in control, px, py));

            if (a)
                covered++;
        }

        // A mask that covered nothing would satisfy the equality above without
        // saying anything, which is the failure this project keeps meeting.
        Assert.True(covered > 0, "the mask covered no pixel at all");
    }
}
