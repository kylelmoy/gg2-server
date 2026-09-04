using System.Linq;
using Gg2.Maps;
using Gg2.Parity;
using Gg2.Protocol;

namespace Gg2.Tests;

/// <summary>
/// The first Arena recording in the project — and the first stream anywhere here
/// that contains an <c>ARENA_ENDROUND</c>.
/// </summary>
/// <remarks>
/// <b>Three soldiers a side on <c>arena_lumberyard</c></b>, spawn-anchored by the
/// map-reload recipe.
/// <para>
/// <b><c>ARENA_ENDROUND</c> is not a bodyless message.</b> It carries five bytes
/// and then five more <em>per MVP</em>, so the counts have to be read to know the
/// length — a reader that skipped the body would lose the stream the first time
/// a round ended. <b>This recording exercises that state.</b>
/// </para>
/// <para>
/// <b>The result is a negative, and that is the point: it frames to the last
/// byte, three round-ends and all.</b>
/// </para>
/// <para>
/// <b>What this adds over <c>ArenaTests</c> and <c>PlayerStatsLayoutTests</c>,
/// which already gate the same arithmetic:</b> those round-trip the port's own
/// writer through the port's own reader, so they agree with each other whether
/// or not either agrees with the engine. This is the reference's bytes. Both
/// kinds of test fail if the skip is removed — the difference is what a pass
/// means.
/// </para>
/// </remarks>
public sealed class ArenaFixtureTests
{
    private const string Fixture = "reference-arena_lumberyard-closedarena.trace";

    private static LevelMap MapLoader(string name) =>
        LevelMap.FromPng(name, ReferenceTree.ReadMap(name)).ForArea(1);

    private static (Trace Trace, IReadOnlyList<TraceTick> Ticks) Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", Fixture);
        Assert.True(File.Exists(path), $"missing {path}");

        var trace = Trace.Decode(File.ReadAllBytes(path));
        var map = MapLoader(trace.Header.Map);
        var ticks = TraceParser.Parse(trace, GameModes.ShapeOf(map), out _, out var stopped);

        Assert.True(stopped is null, $"{Fixture}: {stopped}");
        return (trace, ticks);
    }

    private static byte[][] EndRounds(IReadOnlyList<TraceTick> ticks) =>
        ticks
            .SelectMany(t => t.Events.Where(m => m.Type == MessageType.ArenaEndRound))
            .Select(m => m.Payload)
            .ToArray();

    /// <summary>
    /// It is an Arena recording, on an Arena map, and it frames whole.
    /// </summary>
    /// <remarks>
    /// The shape matters as much as the mode: Arena is one of the three gamemodes
    /// whose wire carries a control-point block, and that block has <b>no count
    /// prefix</b> — a reader rebuilds the points from the map in the same order or
    /// every update after the first is misframed.
    /// </remarks>
    [Fact]
    public void ItIsAnArenaRecordingAndFramesCleanly()
    {
        var (trace, ticks) = Load();
        var shape = GameModes.ShapeOf(MapLoader("arena_lumberyard"));

        Assert.Equal("arena_lumberyard", trace.Header.Map);
        Assert.Equal(GameMode.Arena, shape.GameMode);
        Assert.Equal(1, shape.ControlPointCount);

        Assert.True(ticks.Count > 4000, $"only {ticks.Count} ticks");
    }

    /// <summary>
    /// <b>Three rounds actually end inside it</b>, so the variable-length
    /// message is exercised rather than merely possible.
    /// </summary>
    /// <remarks>
    /// Arena is the only mode where nobody respawns, which is what makes wiping
    /// the enemy team a win condition and what lets three rounds fit into 150
    /// seconds. The restarts and start-rounds are asserted alongside so that a
    /// re-recording which lost the round loop fails here rather than quietly
    /// gating nothing.
    /// </remarks>
    [Fact]
    public void ThreeRoundsEndInsideTheRecording()
    {
        var (_, ticks) = Load();

        Assert.Equal(3, EndRounds(ticks).Length);

        Assert.True(
            ticks.Sum(t => t.Events.Count(m => m.Type == MessageType.ArenaStartRound)) >= 3,
            "no ARENA_STARTROUND");

        Assert.True(
            ticks.Sum(t => t.Events.Count(m => m.Type == MessageType.ArenaRestart)) >= 3,
            "no ARENA_RESTART");

        // A real fight rather than a clock running out on six idle bots: Arena
        // ends a round by elimination, so the deaths are what end these.
        Assert.True(ticks.Sum(t => t.Events.Count(m => m.Type == MessageType.PlayerDeath)) >= 6, "not enough deaths");
    }

    /// <summary>
    /// <b>The gate: every <c>ARENA_ENDROUND</c>'s length is exactly what its
    /// own MVP counts say it should be.</b>
    /// </summary>
    /// <remarks>
    /// Framing cleanly is necessary but not sufficient — a reader that consumed
    /// the right <em>number</em> of bytes for the wrong reason would pass that.
    /// This re-derives the length from the two counts inside the payload, which
    /// is the arithmetic <c>ServerStream</c> has to get right:
    /// <c>5 + 5 × (mvpsRed + mvpsBlue)</c>, with each list capped at three a
    /// side.
    /// <para>
    /// <b>Both counts are written before either list</b>
    /// (<c>serverArenaEndRound.gml</c>), so a sender cannot stream the records
    /// and count as it goes — and a reader cannot know the length until it has
    /// read both.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryEndRoundIsExactlyAsLongAsItsMvpCountsSay()
    {
        var (_, ticks) = Load();

        foreach (var payload in EndRounds(ticks))
        {
            Assert.True(payload.Length >= 5, $"a bodyless ARENA_ENDROUND: {payload.Length} bytes");

            var mvpsRed = payload[1];
            var mvpsBlue = payload[2];

            // ds_priority_delete_min is called at most three times per team.
            Assert.InRange(mvpsRed, 0, 3);
            Assert.InRange(mvpsBlue, 0, 3);

            Assert.Equal(5 + (5 * (mvpsRed + mvpsBlue)), payload.Length);
        }
    }

    /// <summary>
    /// The control that stops the test above passing for free: at least one of
    /// these really does carry MVP records, so the length really does vary.
    /// </summary>
    /// <remarks>
    /// If every round ended with both lists empty, every payload would be five
    /// bytes and a reader that treated the message as fixed-length would frame
    /// this recording perfectly — the bug would still be unreached and the
    /// fixture would be worthless. Asserted so a re-recording cannot lose that
    /// property silently.
    /// </remarks>
    [Fact]
    public void AtLeastOneEndRoundCarriesMvpRecords()
    {
        var (_, ticks) = Load();
        var payloads = EndRounds(ticks);

        Assert.Contains(payloads, p => p.Length > 5);

        // And the winning team is a real team on each of them: red, blue, or the
        // spectator value the reference spells a draw with.
        Assert.All(payloads, p => Assert.InRange(p[0], 0, 2));
    }

    /// <summary>
    /// The characters move like the reference from the last spawn wave.
    /// </summary>
    /// <remarks>
    /// <b>Arena re-spawns everybody at the start of every round</b>
    /// (<c>respawnEveryone()</c> calls <c>PlayerSpawn()</c> directly rather than
    /// arming <c>alarm[5]</c>), so this recording holds 54 <c>PLAYER_SPAWN</c>s
    /// across five round starts and its last anchor is very late. The window
    /// asserted here is therefore short by construction and is not what the
    /// fixture is for — the framing above is.
    /// </remarks>
    [Fact]
    public void TheTrajectoriesAreExactFromTheLastSpawnAnchor()
    {
        var (trace, ticks) = Load();

        var anchor = FixtureAnchors.LastExactAnchor(ticks);
        Assert.True(anchor is not null, $"{Fixture}: not closed-loop");

        var report = new Replayer(new PortedSimulation(MapLoader))
            { Compare = Fields.Trajectory }
            .Replay(Fixture, trace.Header.Map, ticks, ReplayMode.ClosedLoop, stopAtFirstBadTick: false);

        var after = report.Mismatches.Where(m => m.Tick > anchor!.Value).ToArray();
        var firstBad = after.Length == 0 ? ticks[^1].Index : after.Min(m => m.Tick);

        Assert.True(
            firstBad - anchor.Value >= 40,
            $"{Fixture}: exact for only {firstBad - anchor.Value} ticks after t{anchor}");
    }
}
