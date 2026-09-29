using System.Text.Json;
using System.Text.Json.Serialization;
using GG2.Maps;
using GG2.Simulation;

namespace GG2.Tests;

/// <summary>
/// The four collision boxes this project had to derive rather than measure,
/// against the boxes the running engine reports.
/// </summary>
/// <remarks>
/// Four boxes were derived rather than measured, each documented as such:
/// <see cref="ProjectileSprites.Bubble"/> from
/// <c>BubbleS.xml</c>'s <c>mode="FULL"</c>, <see cref="ProjectileSprites.Stab"/>
/// and the briefcase's box from the PNGs' alpha, and
/// <see cref="Generators.Box"/> from <c>GeneratorRedS</c>. This test asks the
/// engine directly and pins all four to its answer. Regenerate the fixture with
/// <c>node gg2-samplers/probes/probe-sprite-boxes.js</c>.
/// <para>
/// The briefcase mattered most: <c>IntelTests</c> pins only
/// <see cref="Intel.BoxLeft"/>, and docs/PARITY.md records that its other three
/// edges can each move <b>sixteen</b> pixels with every fixture still green.
/// This test is the only thing in the suite that bites on those three.
/// </para>
/// </remarks>
public sealed class SpriteBoxSampleTests
{
    private sealed record Box
    {
        [JsonPropertyName("left")] public required int Left { get; init; }

        [JsonPropertyName("top")] public required int Top { get; init; }

        [JsonPropertyName("right")] public required int Right { get; init; }

        [JsonPropertyName("bottom")] public required int Bottom { get; init; }
    }

    private sealed record SpriteSample
    {
        [JsonPropertyName("sprite")] public required string Sprite { get; init; }

        [JsonPropertyName("object")] public required string Object { get; init; }

        /// <summary>
        /// <c>object_get_mask</c>. -1 means the object collides as its own
        /// <c>sprite_index</c> — the <c>Sentry</c> trap not applying here.
        /// </summary>
        [JsonPropertyName("objectMask")] public required int ObjectMask { get; init; }

        [JsonPropertyName("frames")] public required int Frames { get; init; }

        [JsonPropertyName("width")] public required int Width { get; init; }

        [JsonPropertyName("height")] public required int Height { get; init; }

        [JsonPropertyName("xOrigin")] public required int XOrigin { get; init; }

        [JsonPropertyName("yOrigin")] public required int YOrigin { get; init; }

        [JsonPropertyName("bboxLeft")] public required int BboxLeft { get; init; }

        [JsonPropertyName("bboxTop")] public required int BboxTop { get; init; }

        [JsonPropertyName("bboxRight")] public required int BboxRight { get; init; }

        [JsonPropertyName("bboxBottom")] public required int BboxBottom { get; init; }

        /// <summary><c>bbox_*</c> minus the instance position at <c>image_xscale = 1</c>.</summary>
        [JsonPropertyName("instance")] public required Box Instance { get; init; }

        /// <summary>The same at <c>image_xscale = -1</c>.</summary>
        [JsonPropertyName("flipped")] public required Box Flipped { get; init; }
    }

    private sealed record Document
    {
        [JsonPropertyName("sprites")] public required SpriteSample[] Sprites { get; init; }
    }

    private static Document Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "sprite-box-samples.json");
        Assert.True(File.Exists(path), $"missing {path}");
        return JsonSerializer.Deserialize<Document>(File.ReadAllBytes(path))!;
    }

    private static SpriteSample Sprite(string name)
    {
        var sample = Array.Find(Load().Sprites, s => s.Sprite == name);
        Assert.NotNull(sample);
        return sample!;
    }

    /// <summary>
    /// <see cref="ProjectileSprites.Bubble"/> was the one entry in that table
    /// derived from the sprite XML rather than sampled.
    /// </summary>
    [Fact]
    public void TheBubblesBoxIsTheOneTheEngineReports()
    {
        var engine = Sprite("BubbleS");
        var port = ProjectileSprites.Bubble;

        Assert.Equal(new Aabb(engine.BboxLeft, engine.BboxTop, engine.BboxRight, engine.BboxBottom), port.Bbox);
        Assert.Equal(engine.XOrigin, port.OriginX);
        Assert.Equal(engine.YOrigin, port.OriginY);

        // mode="FULL" really does mean the whole image rather than the alpha's
        // extent — the derivation that entry leaned on, now asked directly.
        Assert.Equal(0, engine.BboxLeft);
        Assert.Equal(0, engine.BboxTop);
        Assert.Equal(11, engine.BboxRight);
        Assert.Equal(11, engine.BboxBottom);
    }

    /// <summary>
    /// <see cref="ProjectileSprites.Stab"/> — an AUTO bound, which unlike the
    /// bubble's FULL one had no measured control anywhere in that table.
    /// </summary>
    [Fact]
    public void TheKnivesBoxIsTheOneTheEngineReports()
    {
        var engine = Sprite("StabMaskS");
        var port = ProjectileSprites.Stab;

        Assert.Equal(new Aabb(engine.BboxLeft, engine.BboxTop, engine.BboxRight, engine.BboxBottom), port.Bbox);
        Assert.Equal(engine.XOrigin, port.OriginX);
        Assert.Equal(engine.YOrigin, port.OriginY);

        // Thirty-one pixels of reach in front of the spy, and two behind: the
        // claim Weapons.TryStab's remarks make about the longest melee box in
        // the game.
        Assert.Equal(-2, engine.Instance.Left);
        Assert.Equal(31, engine.Instance.Right);
    }

    /// <summary>
    /// The briefcase: the one collision box in the project that was never
    /// measured, and the one no recording can pin.
    /// </summary>
    [Fact]
    public void TheBriefcasesBoxIsTheOneTheEngineReports()
    {
        var red = Sprite("IntelligenceRedS");
        var blue = Sprite("IntelligenceBlueS");

        // Both teams' briefcases collide identically, which is what lets Intel
        // keep a single box.
        Assert.Equal(red.Instance, blue.Instance);

        Assert.Equal(Intel.BoxLeft, red.Instance.Left);
        Assert.Equal(Intel.BoxTop, red.Instance.Top);
        Assert.Equal(Intel.BoxRight, red.Instance.Right);
        Assert.Equal(Intel.BoxBottom, red.Instance.Bottom);

        // The bottom edge is +7 on a 22-tall sprite whose origin sits at 12:
        // the AUTO bound stops two rows short of the image, which is exactly
        // what reading a PNG's nominal height gets wrong.
        Assert.Equal(22, red.Height);
        Assert.Equal(19, red.BboxBottom);
    }

    /// <summary>
    /// The generator, in the same standing as the briefcase — and the box its
    /// four-hundred-pixel blast measures every distance from.
    /// </summary>
    [Fact]
    public void TheGeneratorsBoxIsTheOneTheEngineReports()
    {
        var red = Sprite("GeneratorRedS");
        var blue = Sprite("GeneratorBlueS");

        Assert.Equal(red.Instance, blue.Instance);

        Assert.Equal(Generators.Box.Left, red.Instance.Left);
        Assert.Equal(Generators.Box.Top, red.Instance.Top);
        Assert.Equal(Generators.Box.Right, red.Instance.Right);
        Assert.Equal(Generators.Box.Bottom, red.Instance.Bottom);

        // Forty-eight frames and one bounding box. A GM8 sprite has a single
        // bbox however many sub-images it holds, which is why the generator
        // needs no mask_index override where the Sentry does.
        Assert.Equal(48, red.Frames);
    }

    /// <summary>
    /// Every object here collides as its own sprite, so none of them is wearing
    /// the <c>Sentry</c>'s <c>mask_index</c> trap.
    /// </summary>
    [Fact]
    public void NoneOfTheseObjectsOverridesItsMask()
    {
        foreach (var sample in Load().Sprites)
            Assert.Equal(-1, sample.ObjectMask);
    }

    /// <summary>
    /// The control: flipping a sprite mirrors its box about the origin and
    /// <em>then</em> pushes <c>bbox_left</c> two pixels further out.
    /// </summary>
    /// <remarks>
    /// <b>The mirror is the half <see cref="Aabb.ForCharacter"/> is allowed
    /// to skip, and only because every character mask is symmetric about its
    /// origin.</b> None of the four boxes here is: the briefcase runs -9..+10
    /// and mirrors to -10..+9. Nothing ever draws a briefcase or a generator
    /// flipped, so no code reads these rows — they are here so the two-pixel
    /// inflation is checked on four sprites that are not characters, and so the
    /// next reader can see what <c>ForCharacter</c> would get wrong if it were
    /// ever handed an asymmetric box.
    /// </remarks>
    [Fact]
    public void FlippingASpriteMirrorsItAndInflatesBboxLeftByTwo()
    {
        foreach (var sample in Load().Sprites)
        {
            var left = sample.BboxLeft - sample.XOrigin;
            var right = sample.BboxRight - sample.XOrigin;

            Assert.Equal(left, sample.Instance.Left);
            Assert.Equal(right, sample.Instance.Right);

            Assert.Equal(-right - 2, sample.Flipped.Left);
            Assert.Equal(-left, sample.Flipped.Right);

            // image_xscale leaves the vertical alone.
            Assert.Equal(sample.Instance.Top, sample.Flipped.Top);
            Assert.Equal(sample.Instance.Bottom, sample.Flipped.Bottom);
        }
    }

    /// <summary>
    /// <b>The dropdown platform's box</b> — the port's inferred 60x6 bar is right.
    /// </summary>
    /// <remarks>
    /// The collision oracle samples every rectangle the port builds, but only for
    /// things that are <em>solid</em>, and <c>SolidKind.DropdownPlatform</c> is
    /// never solid to anything — so the platform falls through the net that would
    /// otherwise catch it. This test uses <c>probe-sprite-boxes.js</c>'s
    /// <c>mask_index</c> trick instead, which does not care about solidity because
    /// a bounding box is a property of the sprite.
    /// <para>
    /// <b>The sprite has NINE sub-images.</b> "One frame" is a bad assumption to
    /// carry into the next collider: the box is the same for all of them, but
    /// that has to be checked, not assumed.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheDropdownPlatformsBoxIsTheOneTheEngineReports()
    {
        var engine = Sprite("sprite44");

        Assert.Equal(60, engine.Width);
        Assert.Equal(6, engine.Height);
        Assert.Equal(0, engine.XOrigin);
        Assert.Equal(0, engine.YOrigin);

        // The port infers this one rather than reading it: ColliderSprites.Bar
        // with BarLong x BarShort.
        var rect = ColliderSprites.Of(new LevelEntity
        {
            Type = EntityTypes.DropdownPlatform,
            X = 100,
            Y = 200,
            Properties = new Dictionary<string, GgonValue>(),
        });

        Assert.NotNull(rect);
        Assert.Equal(SolidKind.DropdownPlatform, rect!.Value.Kind);

        Assert.Equal(100 + engine.Instance.Left, rect.Value.Left);
        Assert.Equal(100 + engine.Instance.Right, rect.Value.Right);
        Assert.Equal(200 + engine.Instance.Top, rect.Value.Top);
        Assert.Equal(200 + engine.Instance.Bottom, rect.Value.Bottom);
    }

    /// <summary>
    /// <b>OPEN, latent: a collider placed with a negative scale gets an
    /// inverted rectangle.</b>
    /// </summary>
    /// <remarks>
    /// <c>SolidRect.FromEntity</c> computes
    /// <c>Right = round(x + width * xScale) - 1</c>, so at <c>xScale = -1</c> it
    /// returns <b>Left = x, Right = x - 61</b> — a rectangle whose right edge is
    /// left of its left edge, which overlaps nothing. The engine's answer,
    /// measured on all seven sprites above, is <c>[-(right + 2), -left]</c>:
    /// for this sprite <b>Left = x - 61, Right = x</b>, two pixels wider than
    /// the unflipped box rather than a mirror of it.
    /// <para>
    /// <b>Unreachable with the shipped maps.</b> Not one entity in
    /// <c>maps.json</c> — every built-in map — has a negative scale, which is
    /// why no fixture and no oracle has ever shown this. A custom map can set
    /// one, and the server serves custom maps.
    /// </para>
    /// <para>
    /// <b>This test pins the defect rather than the fix</b>, like
    /// <c>SentryKillFixtureTests</c>: it fails when the rectangle is normalised,
    /// and the flip inflation above is the number to normalise it to. Left
    /// unfixed because no map exercises it and a fix without a case that needs
    /// it is a change to collision geometry made on spec.
    /// </para>
    /// </remarks>
    [Fact]
    public void AFlippedColliderStillBuildsAnInvertedRectangle()
    {
        var rect = ColliderSprites.Of(new LevelEntity
        {
            Type = EntityTypes.DropdownPlatform,
            X = 100,
            Y = 200,
            XScale = -1,
            Properties = new Dictionary<string, GgonValue>(),
        });

        Assert.NotNull(rect);

        // What the port produces today: inverted, so it overlaps nothing.
        Assert.Equal(100, rect!.Value.Left);
        Assert.Equal(100 - 61, rect.Value.Right);
        Assert.True(
            rect.Value.Right < rect.Value.Left,
            "the rectangle is no longer inverted — if this was fixed, re-pin it "
                + "against the measured flip: Left = x - 61, Right = x");

        // And what the engine says it should be.
        var engine = Sprite("sprite44");
        Assert.Equal(-61, engine.Flipped.Left);
        Assert.Equal(0, engine.Flipped.Right);
    }

    /// <summary>
    /// <b>The four dropdown platforms the game actually places, against the
    /// boxes the engine reports for them — and every one is SCALED.</b>
    /// </summary>
    /// <remarks>
    /// <see cref="TheDropdownPlatformsBoxIsTheOneTheEngineReports"/> checks the
    /// port at <b>scale 1</b>, which is the one case that never occurs: all four
    /// dropdown platforms on all 22 shipped maps are on <c>tdm_mantic</c>, and
    /// their x scales are <b>1.6, 0.5, 0.6 and 1.2</b>. So the assertion that
    /// matters — that <c>Right = round(x + 60*xscale) - 1</c> is what the engine
    /// does at a <em>fractional</em> scale — needs this test too.
    /// <para>
    /// <b>All four scales agree under rounding and truncation, so this test does
    /// NOT gate <c>FromEntity</c>'s rounding.</b> <c>60 * 0.6</c> lands on exactly
    /// 36.0 in <c>double</c>, and 1.6, 0.5 and 1.2 land on integers too.
    /// <b>The rounding is observable on the setup gates instead</b> — see
    /// <see cref="TheSevenSetupGatesThatDistinguishRoundingFromTruncationMatchTheEngine"/>.
    /// </para>
    /// <para>
    /// <b>And the mask fills the scaled box</b>, which is a separate question
    /// from the box: <c>collision_point</c> at every column reports <b>36 of
    /// 36</b> hits at scale 0.6 and <b>96 of 96</b> at scale 1.6, contiguous, with
    /// nothing outside. <c>sprite44.xml</c> declares
    /// <c>&lt;shape&gt;RECTANGLE&lt;/shape&gt;</c> with <c>mode="FULL"</c>, and it
    /// means it.
    /// </para>
    /// <para>
    /// This goes through <see cref="LevelMap"/> rather than a synthetic entity,
    /// so it gates the real path: map bytes, entity, rectangle.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheFourDropdownPlatformsTheGameActuallyPlacesMatchTheEngine()
    {
        var map = LevelMap.FromPng("tdm_mantic", ReferenceTree.ReadMap("tdm_mantic"));

        var platforms = new List<SolidRect>();

        foreach (var entity in map.Entities)
            if (entity.Type == EntityTypes.DropdownPlatform && ColliderSprites.Of(entity) is { } rect)
                platforms.Add(rect);

        // Every dropdown platform in the game is on this map. If that stops
        // being true the rows below are no longer the whole population, and the
        // scaled cases may have moved somewhere ungated.
        Assert.Equal(4, platforms.Count);

        // x, xscale, and the engine's bbox_left..bbox_right, measured by
        // creating each instance and reading its bounding box.
        var expected = new (int X, double XScale, int Left, int Right)[]
        {
            (1164, 1.6, 1164, 1259),
            (1074, 0.5, 1074, 1103),
            (1590, 0.6, 1590, 1625),
            (2622, 1.2, 2622, 2693),
        };

        foreach (var (x, xScale, left, right) in expected)
        {
            var rect = platforms.Single(p => p.Left == left);

            Assert.Equal(SolidKind.DropdownPlatform, rect.Kind);
            Assert.Equal(x, rect.Left);
            Assert.Equal(right, rect.Right);
            Assert.Equal(xScale, rect.XScale, 9);

            // yscale is 1 on all four, so the height is the unscaled six.
            Assert.Equal(rect.Top + 5, rect.Bottom);
        }
    }

    /// <summary>
    /// <b>The seven collider rectangles in the whole game whose edge depends on
    /// <c>FromEntity</c> ROUNDING rather than truncating — and the engine rounds.</b>
    /// </summary>
    /// <remarks>
    /// <c>SolidRect.FromEntity</c> computes
    /// <c>Right = RoundPosition(x + width * xScale) - 1</c>. Replacing that round
    /// with a truncation still passes the dropdown-platform test above, since its
    /// four real scales all land on exact integers — the rounding needs its own
    /// gate.
    /// <para>
    /// Scanned across all 22 shipped maps: <b>7 of 128 collider rectangles differ
    /// between rounding and truncating</b>, and every one of them is a
    /// <c>ControlPointSetupGate</c> on <c>cp_dirtbowl</c> or <c>ctf_avanti</c>.
    /// Those are the solids that hold players in spawn during setup, so a
    /// one-pixel edge here is wire-visible geometry, not decoration.
    /// </para>
    /// <para>
    /// Each row's <c>Right</c>/<c>Bottom</c> was measured by creating a
    /// <c>ControlPointSetupGate</c> at that position and scale and reading
    /// <c>bbox_right</c>/<c>bbox_bottom</c>. <b>The scales were sent as integer
    /// ratios</b> (<c>56/100</c>, not <c>0.56</c>): GM8's parser is a ULP off on
    /// some decimal literals, which would measure a neighbouring rectangle and
    /// blame the port.
    /// </para>
    /// <para>
    /// <b>Why the collision oracle misses them</b> is worth keeping: it
    /// enumerates what is <em>solid</em>, and a setup gate is only solid during
    /// setup. Same shape of hole as the dropdown platform, which is never solid
    /// at all.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheSevenSetupGatesThatDistinguishRoundingFromTruncationMatchTheEngine()
    {
        // map, x, xscale, y, yscale, engine bbox_right, engine bbox_bottom
        var measured = new (string Map, int X, double XScale, int Y, double YScale, int Right, int Bottom)[]
        {
            ("cp_dirtbowl", 660, 0.56, 756, 6.56, 677, 965),
            ("cp_dirtbowl", 738, 0.56, 2274, 2.06, 755, 2339),
            ("cp_dirtbowl", 1776, 0.38, 3756, 1.31, 1787, 3797),
            ("cp_dirtbowl", 840, 0.75, 3636, 1.31, 863, 3677),
            ("ctf_avanti", 1074, 1.13, 402, 0.56, 1109, 419),
            ("ctf_avanti", 1074, 0.56, 444, 0.38, 1091, 455),
            ("ctf_avanti", 1074, 0.19, 468, 0.56, 1079, 485),
        };

        foreach (var row in measured)
        {
            var rect = ColliderSprites.Of(new LevelEntity
            {
                Type = EntityTypes.SetupGate,
                X = row.X,
                Y = row.Y,
                XScale = row.XScale,
                YScale = row.YScale,
                Properties = new Dictionary<string, GgonValue>(),
            });

            Assert.NotNull(rect);
            Assert.Equal(row.X, rect!.Value.Left);
            Assert.Equal(row.Y, rect.Value.Top);
            Assert.Equal(row.Right, rect.Value.Right);
            Assert.Equal(row.Bottom, rect.Value.Bottom);

            // The control: this row exists precisely because truncating differs.
            // If both edges ever agree, the row has stopped doing its job and the
            // scan that chose it needs re-running.
            var truncRight = (int)(row.X + 32 * row.XScale) - 1;
            var truncBottom = (int)(row.Y + 32 * row.YScale) - 1;

            Assert.True(
                row.Right != truncRight || row.Bottom != truncBottom,
                $"{row.Map} ({row.X},{row.Y}) no longer distinguishes rounding from "
                    + "truncation; re-run the round-vs-truncate scan to pick rows that do");
        }
    }

    /// <summary>
    /// The seven rows above really are the whole population.
    /// </summary>
    /// <remarks>
    /// Without this, a map change could add an eighth rounding-sensitive collider
    /// and the table above would still pass while no longer covering the game.
    /// </remarks>
    [Fact]
    public void NoOtherColliderInTheGameDependsOnThatRounding()
    {
        var sensitive = new List<string>();

        foreach (var name in ReferenceTree.ShippedMapNames())
        {

            LevelMap map;
            try { map = LevelMap.FromPng(name, ReferenceTree.ReadMap(name)); }
            catch (Exception) { continue; }

            foreach (var entity in map.Entities)
            {
                if (ColliderSprites.Of(entity) is not { } rect)
                    continue;

                var truncRight = (int)(rect.X + rect.SpriteWidth * rect.XScale) - 1;
                var truncBottom = (int)(rect.Y + rect.SpriteHeight * rect.YScale) - 1;

                if (rect.Right != truncRight || rect.Bottom != truncBottom)
                    sensitive.Add($"{name} {entity.Type} ({rect.X},{rect.Y})");
            }
        }

        Assert.Equal(7, sensitive.Count);
        Assert.All(sensitive, s => Assert.Contains("etupGate", s));
    }
}
