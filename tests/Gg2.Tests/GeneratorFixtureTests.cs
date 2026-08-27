using System.Linq;
using Gg2.Maps;
using Gg2.Parity;
using Gg2.Protocol;

namespace Gg2.Tests;

/// <summary>
/// The first Generator recording — and the first <c>MAP_END</c> and mid-stream
/// <c>CHANGE_MAP</c> anywhere in the project.
/// </summary>
/// <remarks>
/// <b>Recorded 2026-08-27: three soldiers a side on <c>gen_destroy</c></b>, the
/// only shipped map that plays the mode, spawn-anchored. Both generators took
/// real bot damage (down to 1846 and 1755 of 2100) before blue's was finished
/// off over the bridge at t2027, so the round would end inside the recording.
/// <para>
/// Four things reach a fixture here for the first time: a
/// <c>GENERATOR_DESTROY</c>, a <c>MAP_END</c>, a <c>CHANGE_MAP</c> that is not
/// the recorder's own join, and the generator HUD block.
/// </para>
/// </remarks>
public sealed class GeneratorFixtureTests
{
    private const string Fixture = "reference-gen_destroy-closedgen.trace";

    private static LevelMap MapLoader(string name) =>
        LevelMap.FromPng(name, ReferenceTree.ReadMap(name)).ForArea(1);

    private static WorldShape Shape() => GameModes.ShapeOf(MapLoader("gen_destroy"));

    private static (Trace Trace, IReadOnlyList<TraceTick> Ticks) Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", Fixture);
        Assert.True(File.Exists(path), $"missing {path}");

        var trace = Trace.Decode(File.ReadAllBytes(path));
        var ticks = TraceParser.Parse(trace, Shape(), out _, out var stopped);

        Assert.True(stopped is null, $"{Fixture}: {stopped}");
        return (trace, ticks);
    }

    private static (int Tick, byte[] Payload) Single(IReadOnlyList<TraceTick> ticks, MessageType type)
    {
        var hits = ticks
            .SelectMany(t => t.Events.Where(m => m.Type == type).Select(m => (t.Index, m.Payload)))
            .ToArray();

        Assert.Single(hits);
        return hits[0];
    }

    /// <summary>It is the Generator mode, with both generators in the shape.</summary>
    [Fact]
    public void ItIsAGeneratorRecording()
    {
        var (trace, ticks) = Load();

        Assert.Equal("gen_destroy", trace.Header.Map);
        Assert.Equal(GameMode.Generator, Shape().GameMode);
        Assert.Equal(2, Shape().GeneratorCount);
        Assert.True(ticks.Count > 3000, $"only {ticks.Count} ticks");
    }

    /// <summary>
    /// ⚠️ <b>The generator HUD block does NOT shorten when a generator dies</b>,
    /// and that is the finding.
    /// </summary>
    /// <remarks>
    /// docs/PROTOCOL.md records that the block carries one record per
    /// <em>surviving</em> generator, so a destruction shortens every later
    /// update by four bytes — making <c>WorldShape.GeneratorCount</c> the one
    /// shape field a map cannot settle for good, and requiring a reader to
    /// decrement it on <c>GENERATOR_DESTROY</c>. <b>Nothing in
    /// <c>ServerStream</c> does that</b>, and this recording shows it does not
    /// need to.
    /// <para>
    /// <b>Measured:</b> the wire carries <b>two</b> records for the whole
    /// recording, including the 300 ticks between the destruction at t2027 and
    /// the map change at t2328. Parsing at a fixed count of 2 is clean to the
    /// last byte; forcing 1 or 3 desynchronises within 450 bytes. That is the
    /// control — without it "it framed" would be equally consistent with a
    /// reader that never looked at the block.
    /// </para>
    /// <para>
    /// ⚠️ <b>Why, and why it may be unreachable in practice:</b> destroying a
    /// generator ends the round immediately (t2027, <c>MAP_END</c> at t2028) and
    /// <b>a round ending does not sweep the map</b> — the dead generator's
    /// instance survives the scoreboard, so the HUD still writes it. On
    /// <c>gen_destroy</c> each team has exactly one generator, so destroying one
    /// <em>always</em> ends the round. A custom map with two per team could
    /// still reach the shortened block; no shipped one can.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheGeneratorBlockDoesNotShortenWhenAGeneratorDies()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", Fixture);
        var trace = Trace.Decode(File.ReadAllBytes(path));

        TraceParser.Parse(trace, Shape(), out _, out var atTwo);
        Assert.Null(atTwo);

        // The controls: a reader that had decremented, and one that over-counts.
        TraceParser.Parse(trace, Shape() with { GeneratorCount = 1 }, out _, out var atOne);
        Assert.NotNull(atOne);

        TraceParser.Parse(trace, Shape() with { GeneratorCount = 3 }, out _, out var atThree);
        Assert.NotNull(atThree);
    }

    /// <summary>
    /// The round ends inside the recording, and the messages say what happened.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b><c>GENERATOR_DESTROY</c> names the team that LOST one</b>, which
    /// <c>sendEventGeneratorDestroy.gml</c>'s own docstring gets backwards.
    /// Blue's was the one destroyed and the payload is 1 — and the
    /// <c>MAP_END</c> a tick later names red as the winner, which is the
    /// consistency check that makes the reading unambiguous.
    /// </remarks>
    [Fact]
    public void TheRoundEndsAndTheMessagesNameTheLoserAndTheWinner()
    {
        var (_, ticks) = Load();

        var destroy = Single(ticks, MessageType.GeneratorDestroy);
        Assert.Equal((byte)Team.Blue, destroy.Payload[0]);

        var mapEnd = Single(ticks, MessageType.MapEnd);

        // string1 nextMap, ubyte winners, ubyte currentMapArea.
        var length = mapEnd.Payload[0];
        Assert.Equal("gen_destroy", System.Text.Encoding.ASCII.GetString(mapEnd.Payload, 1, length));
        Assert.Equal((byte)Team.Red, mapEnd.Payload[1 + length]);
        Assert.Equal(1, mapEnd.Payload[2 + length]);

        // ⚠️ The destruction and the round end are one tick apart: the winner is
        // decided in Step and broadcast in the NEXT tick's Begin Step.
        Assert.Equal(destroy.Tick + 1, mapEnd.Tick);
    }

    /// <summary>
    /// ✅ <b>The map changes ten seconds after the round ends</b> — measured,
    /// and the first mid-stream <c>CHANGE_MAP</c> in any fixture.
    /// </summary>
    /// <remarks>
    /// <c>GameServerBeginStep.gml</c>: tell everybody, then change the map ten
    /// seconds later. Exactly 300 ticks, t2028 to t2328.
    /// <para>
    /// ⚠️ <b>It rotates back to the same map, so the wire shape never changes.</b>
    /// A rotation to a map of a different mode would leave <c>TraceParser</c>
    /// framing the rest of the recording with the wrong shape — it takes one
    /// shape for the whole trace — so that case is still unreached.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheMapChangesThreeHundredTicksAfterTheRoundEnds()
    {
        var (_, ticks) = Load();

        var mapEnd = Single(ticks, MessageType.MapEnd);

        var changes = ticks
            .SelectMany(t => t.Events.Where(m => m.Type == MessageType.ChangeMap).Select(m => (t.Index, m.Payload)))
            .ToArray();

        // The recorder's own join, and the one the round end caused.
        Assert.Equal(2, changes.Length);
        Assert.Equal(0, changes[0].Index);
        Assert.Equal(300, changes[1].Index - mapEnd.Tick);

        var length = changes[1].Payload[0];
        Assert.Equal("gen_destroy", System.Text.Encoding.ASCII.GetString(changes[1].Payload, 1, length));
    }

    /// <summary>Soldiers on the generator map move like the reference.</summary>
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

        // 408 today, t415 to t823 — with 107 weapon fires and eleven deaths in
        // the recording, and a generator soaking rockets throughout.
        Assert.True(
            exact >= 380,
            $"{Fixture}: exact for only {exact} ticks after the last spawn anchor (t{anchor})");
    }
}
