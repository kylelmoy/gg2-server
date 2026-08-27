using System.Linq;
using Gg2.Maps;
using Gg2.Parity;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// The first King of the Hill recording in the project.
/// </summary>
/// <remarks>
/// <b>Recorded 2026-08-27: three scouts against three soldiers on
/// <c>koth_valley</c></b>, spawn-anchored by the map-reload recipe. Every
/// fixture before the 2026-08-27 batch was Capture the Flag, and until this one
/// nothing had ever put a <b>control point</b> on the wire here.
/// <para>
/// ✅ <b>The headline is that it frames cleanly to the last byte</b>, and that
/// is a sharper negative than it looks. <b>The control-point block carries no
/// count prefix</b> (docs/PROTOCOL.md, "The KotH HUD block") — a reader has to
/// build the same points from the same map, in the same order, or every update
/// after the first is misframed. Nothing had ever tested that against a real
/// stream. <c>gg2-parity census</c> reports <c>KingOfTheHill, 1 control
/// point(s)</c>, and <see cref="FixtureFramingTests"/> covers the file.
/// </para>
/// <para>
/// The roster is scouts against soldiers on purpose: <b>a scout's
/// <c>capStrength</c> is 2 and every other class's is 1</b>, the only class
/// override in the capture rules.
/// </para>
/// </remarks>
public sealed class KothFixtureTests
{
    private const string Fixture = "reference-koth_valley-closedkoth.trace";

    private static LevelMap MapLoader(string name) =>
        LevelMap.FromPng(name, ReferenceTree.ReadMap(name));

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

    private static int CountMessages(IReadOnlyList<TraceTick> ticks, MessageType type) =>
        ticks.Sum(t => t.Events.Count(m => m.Type == type));

    /// <summary>
    /// It is King of the Hill, on a map with a control point, and it frames.
    /// </summary>
    [Fact]
    public void ItIsKingOfTheHillWithAControlPointAndFramesCleanly()
    {
        var (trace, ticks) = Load();
        var shape = GameModes.ShapeOf(MapLoader("koth_valley"));

        Assert.Equal("koth_valley", trace.Header.Map);
        Assert.Equal(GameMode.KingOfTheHill, shape.GameMode);

        // ⚠️ The number the framing depends on. One point on this map, and the
        // reader gets it from the map rather than from the wire.
        Assert.Equal(1, shape.ControlPointCount);

        Assert.True(ticks.Count > 3000, $"only {ticks.Count} ticks");
    }

    /// <summary>
    /// It holds the mode's own traffic, including the project's first
    /// <c>UNLOCKCP</c>.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b><c>UNLOCKCP</c> is contract, not decoration</b>: KotH always sends
    /// it and Dual King of the Hill never does (docs/SIMULATION.md, "Settled:
    /// Dual King of the Hill"), so it is the one message that tells the two
    /// modes apart on the wire.
    /// <para>
    /// The <c>CAPS_UPDATE</c> count is the other thing that separates this from
    /// a CTF recording: a capture-the-flag fixture holds one or two, and this
    /// holds thirty-nine, because the hill's meter moves.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheRecordingHoldsTheHillsOwnTraffic()
    {
        var (_, ticks) = Load();

        Assert.True(CountMessages(ticks, MessageType.UnlockCp) >= 1, "no UNLOCKCP");
        Assert.True(CountMessages(ticks, MessageType.CapsUpdate) >= 20, "not enough caps updates");

        // And it is a real match rather than six bots standing still.
        Assert.True(CountMessages(ticks, MessageType.WeaponFire) >= 100, "not enough fire");
        Assert.True(CountMessages(ticks, MessageType.PlayerDeath) >= 10, "not enough deaths");
    }

    /// <summary>
    /// Scouts and soldiers replay exactly for 330 ticks on a third gamemode's
    /// map.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Note what this window is <em>not</em>.</b> It is trajectory, hp and
    /// deaths — it says nothing whatever about the hill, because
    /// <b>no recording can</b>: see
    /// <see cref="TheHarnessCannotCompareAControlPointAtAll"/>.
    /// <para>
    /// It is also a useful negative for the open landing finding
    /// (<see cref="SecondMapFixtureTests"/>): <c>koth_valley</c> is a fifth map
    /// and behaves normally — the first divergence is ordinary single-slot drift
    /// of two wire units at t739, not six characters falling at once.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheTrajectoriesAndDamageAreExactFromTheSpawnAnchor()
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

        // 330 today, t409 to t739.
        Assert.True(
            exact >= 300,
            $"{Fixture}: exact for only {exact} ticks after the last spawn anchor (t{anchor})");
    }

    /// <summary>
    /// The recording observes the hill being captured, and the harness can now
    /// see it.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A control point reaches the wire only on a <c>CAPS_UPDATE</c> or
    /// the join <c>FULL_UPDATE</c></b> — <c>StateUpdateReader</c> returns before
    /// the HUD tail on any other tier — so a recording observes one a few dozen
    /// times in several thousand ticks, on no fixed cadence, and never on the
    /// positional one. That is why <c>Replayer</c> compares these on any tick
    /// carrying a block rather than on <c>HasPositions</c>: gating it on the
    /// positional cadence silently skipped every observation, which is exactly
    /// the bug this test was written after hitting.
    /// <para>
    /// The nine post-anchor observations cover the whole lifecycle — neutral,
    /// being captured, captured, contested from the other side, and released —
    /// including <c>0/0/0</c> at t1302, which is the documented quirk that
    /// <c>pointCapture</c> leaves <c>cappingTeam</c> set on the tick the point
    /// changes hands.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheRecordingObservesTheHillChangingHands()
    {
        var (_, ticks) = Load();

        var observed = ticks.Where(t => t.ControlPoints.Length > 0).ToArray();
        Assert.True(observed.Length >= 30, $"only {observed.Length} control-point observations");

        var states = observed
            .Select(t => (t.ControlPoints[0].Team, t.ControlPoints[0].CappingTeam, t.ControlPoints[0].Capping))
            .ToHashSet();

        // Owned by red at some point, and contested by blue at another.
        Assert.Contains(states, s => s.Team == 0);
        Assert.Contains(states, s => s.CappingTeam == 1);

        // ⚠️ The tick the point changed hands: owner set AND cappingTeam still
        // set, which only the next tick's `capping <= 0` clears.
        Assert.Contains(states, s => s.Team == 0 && s.CappingTeam == 0);
    }

    /// <summary>
    /// ⚠️ <b>A replay had no control point at all until 2026-08-27</b>, and
    /// nothing could have noticed.
    /// </summary>
    /// <remarks>
    /// Two bugs in <c>PortedSimulation.LoadMap</c>, both found the moment
    /// <see cref="Fields.ControlPoints"/> existed to report them:
    /// <list type="number">
    /// <item>It placed the briefcases and <b>never called
    /// <c>ControlPoints.Place</c></b>, so a King of the Hill, Control Points or
    /// Arena replay ran with no points and no capture zones — a character
    /// standing on the hill was standing on nothing.</item>
    /// <item>⚠️ <b>And fixing that alone was not enough</b>:
    /// <c>ControlPoints.Place</c> switches on <c>state.GameMode</c> to decide
    /// which entity types are points, and a defaulted <c>WorldState</c> is
    /// Capture the Flag — which places <em>none</em> of a KotH map's. The mode
    /// has to be set from the map first, exactly as <c>GameServer</c> does.</item>
    /// </list>
    /// This test is the regression gate on both.
    /// </remarks>
    [Fact]
    public void AReplayNowHasTheHillItIsSupposedToHave()
    {
        var simulation = new PortedSimulation(MapLoader);
        simulation.LoadMap("koth_valley");

        Assert.True(
            simulation.TryGetControlPoint(0, out _),
            "the replay placed no control point — check PortedSimulation.LoadMap sets GameMode "
            + "before calling ControlPoints.Place");

        // And exactly one, matching what the wire framing assumes.
        Assert.False(simulation.TryGetControlPoint(1, out _));
    }

    /// <summary>
    /// ⚠️ <b>What this fixture still cannot gate, and why.</b>
    /// </summary>
    /// <remarks>
    /// <b>Every one of its nine post-anchor control-point observations falls
    /// outside its 330-tick trajectory window</b> — the earliest is t1010 and
    /// the window ends at t739. By then the port's characters have drifted, so
    /// which of them is standing on the point can differ for reasons that have
    /// nothing to do with the capture rules, and the eleven differences the
    /// comparison reports cannot distinguish a wrong rule from ordinary
    /// positional drift.
    /// <para>
    /// So the plumbing is proven and the <em>rules</em> are not.
    /// <b>What is wanted is a recording in which the hill changes hands inside
    /// the trajectory window</b> — bots pushed onto the point early, or a
    /// shorter <c>cpUnlock</c>. Until then <c>ControlPointTests</c> and
    /// <c>DualKothTests</c> remain the real gate for the capture arithmetic.
    /// </para>
    /// <para>
    /// Asserted rather than commented so it stops being true loudly: if a
    /// future recording puts an observation inside the window, this fails and
    /// that recording is the first thing able to gate the capture rules against
    /// the engine.
    /// </para>
    /// </remarks>
    [Fact]
    public void NoControlPointObservationFallsInsideTheTrajectoryWindow()
    {
        var (_, ticks) = Load();
        var anchor = FixtureAnchors.LastExactAnchor(ticks)!.Value;

        // The measured trajectory window, from the test above.
        const int TrajectoryWindow = 330;

        var inside = ticks
            .Where(t => t.ControlPoints.Length > 0)
            .Select(t => t.Index)
            .Where(i => i > anchor && i <= anchor + TrajectoryWindow)
            .ToArray();

        Assert.True(
            inside.Length == 0,
            "a control-point observation now falls inside the trajectory window "
            + $"(t{anchor}..t{anchor + TrajectoryWindow}): {string.Join(", ", inside)} — that is good "
            + "news, and this fixture can now gate the capture rules. Replace this test.");
    }
}
