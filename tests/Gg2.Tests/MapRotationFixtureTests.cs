using System.Linq;
using Gg2.Maps;
using Gg2.Protocol;

namespace Gg2.Tests;

/// <summary>
/// The first recording that rotates to a <b>different map, in a different
/// gamemode</b>, mid-stream.
/// </summary>
/// <remarks>
/// <c>gen_destroy</c> with <c>ctf_2dfort</c> appended to
/// <c>global.map_rotation</c>, red's generator finished off so <b>blue</b> wins,
/// and the server rotates at t2622.
/// <para>
/// <b>Blue winning is the point.</b> <c>GameServerBeginStep.gml:107</c> takes
/// a different branch when <em>red</em> wins and the map has another area — it
/// advances the area and keeps the same map — which is why
/// <c>reference-gen_destroy-closedgen.trace</c> rotated back to
/// <c>gen_destroy</c>. Only the <c>else</c> arm calls
/// <c>nextMapInRotation()</c>.
/// </para>
/// <para>
/// <b><c>MapRotation</c> in the ini is a FILE PATH, not a list</b>
/// (<c>game_init.gml:94</c>), so with no file the server's rotation holds a
/// single entry — the current map.
/// </para>
/// </remarks>
public sealed class MapRotationFixtureTests
{
    private const string Fixture = "reference-gen_destroy-rotation.trace";

    /// <summary>The tick the rotation lands on, from the recording.</summary>
    private const int RotationTick = 2622;

    private static WorldShape ShapeOf(string map) =>
        GameModes.ShapeOf(LevelMap.FromPng(map, ReferenceTree.ReadMap(map)).ForArea(1));

    private static (Trace Trace, IReadOnlyList<TraceTick> Ticks) Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", Fixture);
        Assert.True(File.Exists(path), $"missing {path}");

        var trace = Trace.Decode(File.ReadAllBytes(path));
        var ticks = TraceParser.Parse(trace, ShapeOf(trace.Header.Map), out _, out var stopped);

        Assert.True(stopped is null, $"{Fixture}: {stopped}");
        return (trace, ticks);
    }

    private static (int Tick, string Map)[] ChangeMaps(IReadOnlyList<TraceTick> ticks) =>
        ticks
            .SelectMany(t => t.Events
                .Where(m => m.Type == MessageType.ChangeMap)
                .Select(m => (t.Index, Map: System.Text.Encoding.ASCII.GetString(m.Payload, 1, m.Payload[0]))))
            .ToArray();

    /// <summary>
    /// It really does change to another map, and another gamemode.
    /// </summary>
    [Fact]
    public void ItRotatesToADifferentMapAndGamemode()
    {
        var (trace, ticks) = Load();

        Assert.Equal("gen_destroy", trace.Header.Map);

        var changes = ChangeMaps(ticks);
        Assert.Equal(2, changes.Length);
        Assert.Equal("gen_destroy", changes[0].Map);
        Assert.Equal("ctf_2dfort", changes[1].Map);
        Assert.Equal(RotationTick, changes[1].Tick);

        // Two different modes, so the wire shape genuinely changes underneath.
        Assert.Equal(GameMode.Generator, ShapeOf("gen_destroy").GameMode);
        Assert.Equal(GameMode.CaptureTheFlag, ShapeOf("ctf_2dfort").GameMode);

        // And the recording keeps going for long enough to matter.
        Assert.True(ticks.Count - RotationTick > 500, "the rotation is too close to the end");
    }

    /// <summary>
    /// The map still changes exactly 300 ticks after the round ends — on the
    /// other branch of the round-end rule.
    /// </summary>
    /// <remarks>
    /// A second, independent confirmation of the ten seconds
    /// <c>GameServerBeginStep.gml</c> waits, this time with <b>blue</b> winning
    /// and the map actually changing, where
    /// <c>GeneratorFixtureTests</c> measured it with red winning and the same
    /// map reloading.
    /// </remarks>
    [Fact]
    public void TheRotationIsThreeHundredTicksAfterTheRoundEnds()
    {
        var (_, ticks) = Load();

        var mapEnd = ticks.Single(t => t.Events.Any(m => m.Type == MessageType.MapEnd));
        Assert.Equal(300, RotationTick - mapEnd.Index);

        // Blue won, so the round-end rule took the arm that rotates.
        var payload = mapEnd.Events.First(m => m.Type == MessageType.MapEnd).Payload;
        var length = payload[0];
        Assert.Equal("ctf_2dfort", System.Text.Encoding.ASCII.GetString(payload, 1, length));
        Assert.Equal((byte)Team.Blue, payload[1 + length]);
    }

    /// <summary>
    /// <b>The reader survives the mode change only because nothing after it
    /// reads the HUD.</b>
    /// </summary>
    /// <remarks>
    /// <c>TraceParser</c> takes <b>one shape for the whole trace</b>, so a
    /// rotation to another gamemode should frame the rest of the recording
    /// wrongly. It does not, because of what carries the HUD block.
    /// <para>
    /// <b>Only <c>FULL_UPDATE</c> and <c>CAPS_UPDATE</c> carry the HUD
    /// block.</b> <c>StateUpdateReader</c> returns before the tail on every
    /// other tier, so an <c>INPUTSTATE</c> and a <c>QUICK_UPDATE</c> never read
    /// it. In this recording the caps updates land at t389, t2188 and t2189 —
    /// <b>all before the rotation</b> — and the only full update is the join at
    /// t0. The 95 quick updates that follow the change read no HUD at all.
    /// </para>
    /// <para>
    /// The shapes really are incompatible: <c>Generator</c>'s HUD is thirteen
    /// bytes (<c>ubyte + uint</c> plus two <c>ushort</c>s per generator) and
    /// <c>CaptureTheFlag</c>'s is five. Parsing this trace with the CTF shape
    /// fails at byte 432, which is the control proving they cannot be swapped.
    /// </para>
    /// <para>
    /// <b>If this test fails, the risk has finally been reached</b> — a
    /// recording with a caps update after a mode change is the thing that would
    /// force <c>TraceParser</c> to re-shape on <c>CHANGE_MAP</c>. Do the
    /// re-shaping and replace this test.
    /// </para>
    /// </remarks>
    [Fact]
    public void NothingAfterTheRotationReadsTheHudBlock()
    {
        var (trace, ticks) = Load();

        var hudBearing = ticks
            .Where(t => t.Index > RotationTick)
            .Where(t => t.Events.Any(m => m.Type is MessageType.CapsUpdate or MessageType.FullUpdate))
            .Select(t => t.Index)
            .ToArray();

        Assert.True(
            hudBearing.Length == 0,
            "a CAPS_UPDATE or FULL_UPDATE now follows the mode change at "
            + $"t{RotationTick}: {string.Join(", ", hudBearing)} — the wrong-shape risk is finally "
            + "reachable, so TraceParser must re-shape on CHANGE_MAP. Replace this test.");

        // The control: the two shapes are genuinely incompatible, so it really
        // is the absence of HUD-bearing updates that saves the reader.
        TraceParser.Parse(trace, ShapeOf("ctf_2dfort"), out _, out var stoppedWithCtf);
        Assert.NotNull(stoppedWithCtf);
    }
}
