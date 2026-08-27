using System.Text.Json;
using System.Text.Json.Serialization;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// The four collision boxes this project had to derive rather than measure,
/// against the boxes the running engine reports.
/// </summary>
/// <remarks>
/// Phases 5 and 6 landed four boxes without a live session, each with a comment
/// saying so: <see cref="ProjectileSprites.Bubble"/> was derived from
/// <c>BubbleS.xml</c>'s <c>mode="FULL"</c>, <see cref="ProjectileSprites.Stab"/>
/// and the briefcase's box were read off the PNGs' alpha, and
/// <see cref="Generators.Box"/> off <c>GeneratorRedS</c>. All four were asked of
/// the engine on 2026-08-27 and all four were right; this is the gate that keeps
/// them that way. Regenerate the fixture with
/// <c>node tools/probes/probe-sprite-boxes.js</c>.
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

        // ⚠️ The bottom edge is +7 on a 22-tall sprite whose origin sits at 12:
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

        // ⚠️ Forty-eight frames and one bounding box. A GM8 sprite has a single
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
    /// ⚠️ <b>The mirror is the half <see cref="Aabb.ForCharacter"/> is allowed
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
}
