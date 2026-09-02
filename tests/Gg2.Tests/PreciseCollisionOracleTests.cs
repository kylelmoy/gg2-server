using System.Text.Json;
using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// <see cref="PreciseCollision"/> against the engine, with a REAL character box.
/// </summary>
/// <remarks>
/// Phase C of the probe plan, run 2026-09-01 by
/// <c>tools/probes/probe-platform-carry.js</c>. Everything verified before it
/// used a 1x1 probe point, so the character side of the query — the inclusive
/// bounds, the rounding, and whether GM8 even treats a character as a rectangle —
/// was still only self-consistent.
/// <para>
/// Three things the sweep settled, all of which <see cref="PreciseCollision"/>
/// already did:
/// </para>
/// <list type="number">
/// <item>✅ <b>A character is a RECTANGLE to GM8, not a silhouette.</b> For the
/// same character at the same position, <c>place_meeting</c> against the platform
/// and <c>collision_rectangle</c> over that character's own bbox differ
/// <em>only</em> in the transition band and never in shape. Had the character's
/// own precise mask been used, they would disagree wherever the person outline is
/// inside the box, which is most places.</item>
/// <item>✅ <b>The bounds are inclusive.</b> First contact is where the right
/// edge lands exactly on the sprite's first solid column, not one pixel
/// past.</item>
/// <item>✅ <b>The position rounds HALF TO EVEN</b>, and the Heavy discriminates
/// it: its first contact is at x = 2990.6, not 2990.5. At exactly 2990.5 half to
/// even gives 2990 and misses, half away from zero gives 2991 and hits. The
/// engine misses.</item>
/// </list>
/// <para>
/// ⚠️ <b>The comparison is against <c>place_meeting</c>, not
/// <c>collision_rectangle</c>.</b> The two differ by up to half a pixel at the
/// boundary because only the first rounds the instance position — and
/// <c>place_meeting</c> is what the reference's carry calls, so it is the one
/// this port has to match.
/// </para>
/// <para>
/// ⚠️ <b>The first run of the probe was VACUOUS and looked like a pass.</b> Its
/// sweep never reached the platform, so every sample was "no collision" on both
/// sides, the two agreed perfectly, and it reported zero disagreements. The probe
/// now refuses to write a fixture unless the sweep actually crosses the edge.
/// </para>
/// </remarks>
public sealed class PreciseCollisionOracleTests
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    public static TheoryData<string> Classes()
    {
        var data = new TheoryData<string>();

        foreach (var sample in Load().RootElement.GetProperty("samples").EnumerateArray())
            data.Add(sample.GetProperty("class").GetString()!);

        return data;
    }

    /// <summary>Every position of the engine's sweep, against this port's query.</summary>
    [Theory]
    [MemberData(nameof(Classes))]
    public void TheSweepMatchesTheEngine(string className)
    {
        var sample = Find(className);

        var box = sample.GetProperty("box");
        var characterBox = new CharacterBox(
            box.GetProperty("left").GetInt32(),
            box.GetProperty("right").GetInt32(),
            box.GetProperty("top").GetInt32(),
            box.GetProperty("bottom").GetInt32());

        var mask = SpriteMask.Decode(
            File.ReadAllBytes(Path.Combine(Fixtures, sample.GetProperty("sprite").GetString()!)))!;

        var spriteX = sample.GetProperty("originX").GetDouble();
        var spriteY = sample.GetProperty("originY").GetDouble();
        var startX = sample.GetProperty("startX").GetDouble();
        var denominator = sample.GetProperty("stepDenominator").GetDouble();
        var probeY = sample.GetProperty("probeY").GetDouble();

        var engine = sample.GetProperty("placeMeeting").EnumerateArray().Select(e => e.GetBoolean()).ToArray();
        var mismatches = new List<string>();

        for (var i = 0; i < engine.Length; i++)
        {
            // The probe sent tenths as a ratio so GM8's parser could not shift
            // one by a ULP; the same division is done here rather than a literal.
            var x = startX + (i / denominator);

            var port = PreciseCollision.Meets(mask, spriteX, spriteY, 1, 1, characterBox, x, probeY);

            if (port != engine[i] && mismatches.Count < 12)
                mismatches.Add($"x={x} engine={engine[i]} port={port}");
        }

        Assert.Empty(mismatches);
    }

    /// <summary>
    /// ⚠️ The sweep is not vacuous: it really does cross the edge.
    /// </summary>
    /// <remarks>
    /// The probe's own guard, asserted again here so a re-recorded fixture that
    /// never touched the platform cannot pass this file either. A uniform sweep
    /// agrees with anything.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Classes))]
    public void TheSweepActuallyCrossesTheEdge(string className)
    {
        var engine = Find(className).GetProperty("placeMeeting")
            .EnumerateArray().Select(e => e.GetBoolean()).ToArray();

        Assert.Contains(true, engine);
        Assert.Contains(false, engine);
    }

    /// <summary>
    /// A character is a rectangle: the two engine methods differ only where the
    /// rounding does.
    /// </summary>
    [Theory]
    [MemberData(nameof(Classes))]
    public void TheEngineTreatsACharacterAsARectangle(string className)
    {
        var sample = Find(className);

        var meeting = sample.GetProperty("placeMeeting").EnumerateArray().Select(e => e.GetBoolean()).ToArray();
        var rectangle = sample.GetProperty("collisionRectangle").EnumerateArray().Select(e => e.GetBoolean()).ToArray();

        var firstMeeting = Array.IndexOf(meeting, true);
        var firstRectangle = Array.IndexOf(rectangle, true);

        // place_meeting rounds and so triggers no later than the exact rectangle.
        Assert.True(firstMeeting <= firstRectangle);

        // And outside that band the two are identical — which is what says the
        // character's mask is its box rather than its outline.
        for (var i = 0; i < meeting.Length; i++)
        {
            if (i >= firstMeeting && i < firstRectangle)
                continue;

            Assert.Equal(rectangle[i], meeting[i]);
        }
    }

    /// <summary>
    /// ⚠️ Half to even, discriminated by the Heavy.
    /// </summary>
    /// <remarks>
    /// Its first contact is at 2990.6. At 2990.5 the two rounding rules part
    /// company — half to even gives 2990 and misses, half away from zero gives
    /// 2991 and hits — and the engine misses. That single sample is the whole
    /// evidence for the rule at a real boundary, so it is asserted rather than
    /// left implicit in the sweep.
    /// </remarks>
    [Fact]
    public void AnExactHalfRoundsDownToEven()
    {
        var sample = Find("Heavy");

        var startX = sample.GetProperty("startX").GetDouble();
        var denominator = sample.GetProperty("stepDenominator").GetDouble();
        var engine = sample.GetProperty("placeMeeting").EnumerateArray().Select(e => e.GetBoolean()).ToArray();

        var half = -1;
        for (var i = 0; i < engine.Length; i++)
        {
            if (Math.Abs((startX + (i / denominator)) - 2990.5) < 1e-9)
            {
                half = i;
                break;
            }
        }

        Assert.True(half >= 0, "the sweep does not contain x = 2990.5");

        // 2990.5 -> 2990 (even) -> right edge 2999, one short of the sprite.
        Assert.False(engine[half]);

        // And the very next tenth does hit, so this is the boundary rather than
        // somewhere the character was simply far away.
        Assert.True(engine[half + 1]);
    }

    private static JsonDocument Load() =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Fixtures, "platform-carry-samples.json")));

    private static JsonElement Find(string className)
    {
        foreach (var sample in Load().RootElement.GetProperty("samples").EnumerateArray())
        {
            if (sample.GetProperty("class").GetString() == className)
                return sample.Clone();
        }

        throw new InvalidOperationException($"no engine sample for {className}");
    }
}
