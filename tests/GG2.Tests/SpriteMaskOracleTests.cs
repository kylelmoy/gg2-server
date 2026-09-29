using System.Text.Json;
using GG2.Maps;
using GG2.Simulation;

namespace GG2.Tests;

/// <summary>
/// The decoded masks against <b>the engine's own answer</b>.
/// </summary>
/// <remarks>
/// <c>sprite-mask-samples.json</c> is what GM8 said, rasterised by
/// <c>gg2-samplers/probes/probe-platform-mask.js</c> with
/// <c>collision_point(prec = 1)</c> over a sprite loaded exactly the way
/// <c>stringToResource.gml</c> loads one. Where this port and that file disagree,
/// <b>the file is right.</b>
/// <para>
/// This test turns three claims into measurements:
/// </para>
/// <list type="bullet">
/// <item><c>removeback</c> really does sample the <b>bottom-left</b> pixel —
/// both PNGs match to the pixel.</item>
/// <item>Scaling is exact replication: a 3x sprite has exactly 9x the solid
/// pixels, so the room→source mapping is plain floor division.</item>
/// <item>A GIF's mask is the <b>union of every subimage</b>, each with
/// <c>removeback</c> applied against <b>its own</b> bottom-left pixel — not
/// frame 1 alone, and not one global background pixel shared across
/// subimages.</item>
/// </list>
/// <para>
/// <b>Regenerating this needs the running game</b> — see the probe's header
/// and <c>docs/PARALLEL.md</c> on holding the bridge. A failure here is a real
/// disagreement with GM8, not a stale fixture: check the change was one you
/// meant before re-recording.
/// </para>
/// </remarks>
public sealed class SpriteMaskOracleTests
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    public static TheoryData<string, int> Samples()
    {
        var data = new TheoryData<string, int>();

        foreach (var sample in Load().RootElement.GetProperty("samples").EnumerateArray())
            data.Add(sample.GetProperty("file").GetString()!, sample.GetProperty("scale").GetInt32());

        return data;
    }

    /// <summary>Every sprite, at every scale the probe recorded, pixel for pixel.</summary>
    [Theory]
    [MemberData(nameof(Samples))]
    public void TheMaskMatchesTheEngine(string file, int scale)
    {
        var sample = Find(file, scale);
        var rows = sample.GetProperty("rows").EnumerateArray().Select(r => r.GetString()!).ToArray();

        var mask = SpriteMask.Decode(File.ReadAllBytes(Path.Combine(Fixtures, file)));
        Assert.NotNull(mask);

        // The engine reports the sprite's own size; the mask must agree before
        // any pixel comparison means anything.
        Assert.Equal(sample.GetProperty("spriteWidth").GetInt32(), mask.Width);
        Assert.Equal(sample.GetProperty("spriteHeight").GetInt32(), mask.Height);

        Assert.Equal(mask.Height * scale, rows.Length);

        var mismatches = new List<string>();

        for (var y = 0; y < rows.Length; y++)
        for (var x = 0; x < rows[y].Length; x++)
        {
            var engine = rows[y][x] == '#';

            // The probe rasterised the sprite at the origin, so a room pixel maps
            // straight back through the scale.
            var port = mask.IsSolid(x / scale, y / scale);

            if (engine != port && mismatches.Count < 12)
                mismatches.Add($"({x},{y}) engine={engine} port={port}");
        }

        Assert.Empty(mismatches);
    }

    /// <summary>
    /// The bubble's exact solid-pixel count, pinned.
    /// </summary>
    /// <remarks>
    /// 586, not 576. The ten-pixel gap was every frame after the first, and the
    /// last three of those were <c>removeback</c> sampling each patch's own
    /// corner. A regression to either wrong model changes this number.
    /// </remarks>
    [Fact]
    public void TheAnimatedBubbleUnionsEveryFrame()
    {
        var mask = SpriteMask.Decode(File.ReadAllBytes(Path.Combine(Fixtures, "sprite-bubble1.gif")))!;

        Assert.Equal(586, mask.SolidCount);
        Assert.Equal(586, Find("sprite-bubble1.gif", 1).GetProperty("solid").GetInt32());
    }

    /// <summary>
    /// GM8 expanded the animated GIF into four subimages despite being asked for
    /// one.
    /// </summary>
    /// <remarks>
    /// The fact that makes the union necessary, recorded from the engine rather
    /// than assumed. It also means <c>animationspeed</c> is decoration as far
    /// as collision goes: one mask serves every frame, so the platform's shape
    /// does not change as it animates.
    /// </remarks>
    [Fact]
    public void TheEngineReportsFourSubimagesForTheBubble() =>
        Assert.Equal(4, Find("sprite-bubble1.gif", 1).GetProperty("subimages").GetInt32());

    /// <summary>
    /// Scaling replicates exactly: 3x has nine times the solid pixels.
    /// </summary>
    /// <remarks>
    /// Confirms the room→source mapping is floor division with no rounding of its
    /// own, which is what <see cref="PreciseCollision"/> assumes. Real maps scale
    /// platforms by 1, 2, 3 and 6.
    /// </remarks>
    [Theory]
    [InlineData("sprite-eleplat.png")]
    [InlineData("sprite-ji.png")]
    [InlineData("sprite-bubble1.gif")]
    public void ThreeTimesScaleIsNineTimesTheArea(string file) =>
        Assert.Equal(
            Find(file, 1).GetProperty("solid").GetInt32() * 9,
            Find(file, 3).GetProperty("solid").GetInt32());

    /// <summary>
    /// <see cref="PreciseCollision"/> agrees with the engine at every room pixel
    /// of a scaled sprite.
    /// </summary>
    /// <remarks>
    /// The mask test above checks the decode; this checks the thing the
    /// simulation actually calls, including its rounding and its inclusive box.
    /// A 1x1 box makes it a pure per-pixel question.
    /// </remarks>
    [Fact]
    public void TheCollisionQueryAgreesWithTheEngineAtThreeTimesScale()
    {
        var sample = Find("sprite-bubble1.gif", 3);
        var rows = sample.GetProperty("rows").EnumerateArray().Select(r => r.GetString()!).ToArray();

        var mask = SpriteMask.Decode(File.ReadAllBytes(Path.Combine(Fixtures, "sprite-bubble1.gif")))!;
        var point = new CharacterBox(0, 0, 0, 0);

        var mismatches = new List<string>();

        for (var y = 0; y < rows.Length; y++)
        for (var x = 0; x < rows[y].Length; x++)
        {
            var engine = rows[y][x] == '#';
            var port = PreciseCollision.Meets(mask, 0, 0, 3, 3, point, x, y);

            if (engine != port && mismatches.Count < 12)
                mismatches.Add($"({x},{y}) engine={engine} port={port}");
        }

        Assert.Empty(mismatches);
    }

    private static JsonDocument Load() =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Fixtures, "sprite-mask-samples.json")));

    private static JsonElement Find(string file, int scale)
    {
        foreach (var sample in Load().RootElement.GetProperty("samples").EnumerateArray())
        {
            if (sample.GetProperty("file").GetString() == file
                && sample.GetProperty("scale").GetInt32() == scale)
            {
                return sample.Clone();
            }
        }

        throw new InvalidOperationException($"no engine sample for {file} at {scale}x");
    }
}
