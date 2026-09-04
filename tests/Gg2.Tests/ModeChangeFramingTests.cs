using System.Linq;
using Gg2.Maps;
using Gg2.Protocol;

namespace Gg2.Tests;

/// <summary>
/// The recording that finally reaches the wrong-shape framing risk, and the
/// re-shaping that answers it.
/// </summary>
/// <remarks>
/// <c>gen_destroy</c> rotates to <c>ctf_2dfort</c> exactly as
/// <c>reference-gen_destroy-rotation.trace</c> does — but this time a
/// <c>CAPS_UPDATE</c> is forced <em>after</em> the change, with
/// <c>GameServer.syncTimer = 1</c>, which is precisely what a capture does
/// (<c>doEventScoreIntel</c>).
/// <para>
/// <b>That one message is the whole difference between a trace that frames
/// and one that does not.</b> Only <c>FULL_UPDATE</c> and <c>CAPS_UPDATE</c>
/// carry the HUD block — <c>StateUpdateReader</c> returns before the tail on
/// every other tier — so a reader holding the wrong gamemode's shape is
/// harmless until one of those arrives. The sibling recording rotates gamemode
/// and frames perfectly for that reason alone.
/// </para>
/// </remarks>
public sealed class ModeChangeFramingTests
{
    private const string Fixture = "reference-gen_destroy-rotationcaps.trace";

    private static WorldShape ShapeOf(string map) =>
        GameModes.ShapeOf(LevelMap.FromPng(map, ReferenceTree.ReadMap(map)).ForArea(1));

    private static Trace Read()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", Fixture);
        Assert.True(File.Exists(path), $"missing {path}");
        return Trace.Decode(File.ReadAllBytes(path));
    }

    /// <summary>
    /// <b>A single shape cannot frame this recording</b> — the control that
    /// makes the fix below mean something.
    /// </summary>
    /// <remarks>
    /// Without this, re-shaping could be reverted and every other test here
    /// would still pass. Measured: framing stops at byte 79,467 of 124,340 —
    /// where the forced <c>CAPS_UPDATE</c> lands on the new map and the
    /// thirteen-byte Generator HUD is read out of a five-byte Capture the Flag
    /// one.
    /// </remarks>
    [Fact]
    public void OneShapeFromEndToEndCannotFrameIt()
    {
        var trace = Read();

        TraceParser.Parse(trace, ShapeOf("gen_destroy"), out _, out var stopped);
        Assert.NotNull(stopped);

        // And not merely a short capture: most of the file is thrown away.
        Assert.Contains("79467", stopped);
    }

    /// <summary>
    /// <b>Re-shaping on <c>CHANGE_MAP</c> frames it to the last byte.</b>
    /// </summary>
    [Fact]
    public void ReShapingOnChangeMapFramesItWhole()
    {
        var trace = Read();

        var ticks = TraceParser.Parse(
            trace, ShapeOf(trace.Header.Map), out var start, out var stopped, ShapeOf);

        Assert.True(stopped is null, $"{Fixture}: {stopped}");
        Assert.NotNull(start);
        Assert.True(ticks.Count > 3000, $"only {ticks.Count} ticks");
    }

    /// <summary>
    /// The recording really does contain the shape change and the HUD-bearing
    /// update after it.
    /// </summary>
    /// <remarks>
    /// A re-recording that lost either would pass the two tests above by having
    /// nothing to get wrong — the first by never desynchronising, the second by
    /// never needing to re-shape.
    /// </remarks>
    [Fact]
    public void ItRotatesGamemodeAndThenSendsACapsUpdate()
    {
        var trace = Read();
        var ticks = TraceParser.Parse(
            trace, ShapeOf(trace.Header.Map), out _, out _, ShapeOf);

        var rotation = ticks
            .SelectMany(t => t.Events
                .Where(m => m.Type == MessageType.ChangeMap)
                .Select(m => (t.Index, Map: System.Text.Encoding.ASCII.GetString(m.Payload, 1, m.Payload[0]))))
            .Last();

        Assert.Equal("ctf_2dfort", rotation.Map);
        Assert.NotEqual(ShapeOf("gen_destroy").GameMode, ShapeOf(rotation.Map).GameMode);

        // The message that makes the wrong shape bite.
        var capsAfter = ticks
            .Where(t => t.Index > rotation.Index)
            .Count(t => t.Events.Any(m => m.Type is MessageType.CapsUpdate or MessageType.FullUpdate));

        Assert.True(capsAfter >= 1, "no HUD-bearing update after the rotation");
    }

    /// <summary>
    /// The sibling recording, which rotates gamemode and frames anyway.
    /// </summary>
    /// <remarks>
    /// Kept as a pair so the distinction stays visible: rotating the gamemode is
    /// <em>not</em> what breaks a reader — rotating it and then sending a
    /// HUD-bearing update is. Re-shaping is correct for both.
    /// </remarks>
    [Fact]
    public void TheSiblingRecordingFramesWithOrWithoutReShaping()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "reference-gen_destroy-rotation.trace");
        var trace = Trace.Decode(File.ReadAllBytes(path));

        TraceParser.Parse(trace, ShapeOf(trace.Header.Map), out _, out var withoutReshape);
        Assert.Null(withoutReshape);

        TraceParser.Parse(trace, ShapeOf(trace.Header.Map), out _, out var withReshape, ShapeOf);
        Assert.Null(withReshape);
    }
}
