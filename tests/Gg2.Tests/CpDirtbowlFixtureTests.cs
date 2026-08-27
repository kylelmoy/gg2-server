using System.Linq;
using Gg2.Maps;
using Gg2.Parity;
using Gg2.Protocol;

namespace Gg2.Tests;

/// <summary>
/// The first fixture on a <b>multi-area</b> map, and the first attack/defend
/// one.
/// </summary>
/// <remarks>
/// <b>Recorded 2026-08-27: three soldiers a side on <c>cp_dirtbowl</c></b>,
/// spawn-anchored.
/// <para>
/// ⚠️ <b>It did not frame at all on its first outing</b>, and that is what it
/// was recorded to find. A tall map is several rounds stacked vertically and
/// only one area's entities exist: <c>cp_dirtbowl</c> writes <b>six</b> control
/// points and plays <b>two</b>. The HUD block carries <b>no count prefix</b>, so
/// a reader framing by the wrong number desynchronises on the very first
/// update — measured, byte 458 of 138,406, with 137,948 bytes silently thrown
/// away.
/// </para>
/// <para>
/// <c>PortedSimulation.LoadMap</c> had narrowed with <c>ForArea(1)</c> since
/// phase 6 and carried a comment saying it "would need the recording's own
/// currentMapArea if one were ever made on cp_dirtbowl". One has been now — and
/// the gap was not there but in every <em>reader</em>: <c>gg2-parity</c>'s
/// three <c>ShapeOf</c> call sites and <c>FixtureFramingTests</c> all passed the
/// un-narrowed map.
/// </para>
/// </remarks>
public sealed class CpDirtbowlFixtureTests
{
    private const string Fixture = "reference-cp_dirtbowl-closedcp.trace";

    /// <summary>
    /// ⚠️ Narrowed, exactly as every reader of a recording must.
    /// </summary>
    private static LevelMap MapLoader(string name) =>
        LevelMap.FromPng(name, ReferenceTree.ReadMap(name)).ForArea(1);

    private static (Trace Trace, IReadOnlyList<TraceTick> Ticks) Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", Fixture);
        Assert.True(File.Exists(path), $"missing {path}");

        var trace = Trace.Decode(File.ReadAllBytes(path));
        var ticks = TraceParser.Parse(
            trace, GameModes.ShapeOf(MapLoader(trace.Header.Map)), out _, out var stopped);

        Assert.True(stopped is null, $"{Fixture}: {stopped}");
        return (trace, ticks);
    }

    /// <summary>
    /// ⚠️ <b>The control: narrowing is load-bearing, not tidiness.</b>
    /// </summary>
    /// <remarks>
    /// Without this the fix above could be reverted and every test in this file
    /// would still pass, because they all narrow. This is the one that asserts
    /// the <em>un</em>-narrowed reader really does fall over — and by how much,
    /// so a future change that merely moves the failure later is visible.
    /// </remarks>
    [Fact]
    public void TheUnNarrowedShapeCannotFrameThisRecording()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", Fixture);
        var trace = Trace.Decode(File.ReadAllBytes(path));

        var raw = LevelMap.FromPng(trace.Header.Map, ReferenceTree.ReadMap(trace.Header.Map));

        // Six written, two played.
        Assert.Equal(6, GameModes.ShapeOf(raw).ControlPointCount);
        Assert.Equal(2, GameModes.ShapeOf(raw.ForArea(1)).ControlPointCount);

        TraceParser.Parse(trace, GameModes.ShapeOf(raw), out _, out var stoppedWide);
        Assert.NotNull(stoppedWide);

        TraceParser.Parse(trace, GameModes.ShapeOf(raw.ForArea(1)), out _, out var stoppedNarrow);
        Assert.Null(stoppedNarrow);
    }

    /// <summary>
    /// It is the mode and shape it should be, and it frames to the last byte.
    /// </summary>
    [Fact]
    public void ItIsAnAttackDefendRecordingAndFramesCleanly()
    {
        var (trace, ticks) = Load();

        Assert.Equal("cp_dirtbowl", trace.Header.Map);

        var shape = GameModes.ShapeOf(MapLoader("cp_dirtbowl"));
        Assert.Equal(GameMode.ControlPoints, shape.GameMode);
        Assert.Equal(2, shape.ControlPointCount);

        // ⚠️ Attack/defend is the arrangement with a setup gate, which is how
        // the port tells it from a symmetrical control-points map.
        Assert.True(GameModes.HasSetupGate(MapLoader("cp_dirtbowl")), "no setup gate");

        Assert.True(ticks.Count > 3000, $"only {ticks.Count} ticks");
    }

    /// <summary>It is a real match, not six bots standing still.</summary>
    [Fact]
    public void TheRecordingHoldsAFight()
    {
        var (_, ticks) = Load();

        Assert.True(ticks.Sum(t => t.Events.Count(m => m.Type == MessageType.WeaponFire)) >= 70, "not enough fire");
        Assert.True(ticks.Sum(t => t.Events.Count(m => m.Type == MessageType.PlayerDeath)) >= 8, "not enough deaths");
    }

    /// <summary>
    /// Soldiers on a multi-area map move exactly like the reference from their
    /// spawn anchors.
    /// </summary>
    /// <remarks>
    /// The first evidence that the port's <c>ForArea</c> narrowing produces the
    /// right <em>world</em> and not merely the right block count: a character
    /// standing on area 1's terrain would be somewhere else entirely if the
    /// wrong area's entities were placed.
    /// </remarks>
    [Fact]
    public void TheTrajectoriesAreExactFromTheSpawnAnchor()
    {
        var (trace, ticks) = Load();

        var anchor = FixtureAnchors.LastExactAnchor(ticks);
        Assert.True(anchor is not null, $"{Fixture}: not closed-loop");

        var report = new Replayer(new PortedSimulation(MapLoader))
            { Compare = Fields.Trajectory | Fields.Hp | Fields.Deaths }
            .Replay(Fixture, trace.Header.Map, ticks, ReplayMode.ClosedLoop, stopAtFirstBadTick: false);

        var after = report.Mismatches.Where(m => m.Tick > anchor!.Value).ToArray();
        var firstBad = after.Length == 0 ? ticks[^1].Index : after.Min(m => m.Tick);
        var exact = firstBad - anchor.Value;

        // 905 today, t417 to t1322 — a strong window for a fixture with 85
        // weapon fires and ten deaths in it.
        Assert.True(
            exact >= 850,
            $"{Fixture}: exact for only {exact} ticks after the last spawn anchor (t{anchor})");
    }
}
