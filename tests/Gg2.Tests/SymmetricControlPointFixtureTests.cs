using System.Linq;
using Gg2.Maps;
using Gg2.Parity;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// The first recording of the <b>symmetrical</b> control-point arrangement — the
/// other half of a gamemode that had only ever been recorded attack/defend.
/// </summary>
/// <remarks>
/// <b>Recorded 2026-08-27: three soldiers a side on <c>cp_egypt</c></b>,
/// spawn-anchored by the map-reload recipe, teams forced after the reload.
/// <para>
/// ⚠️ <b><c>ControlPoints</c> is two rule sets, not one</b>, and
/// <c>GameModes.HasSetupGate</c> is the whole of what separates them
/// (<c>ControlPointHUD.events/Create.xml</c>). <c>cp_dirtbowl</c> places a setup
/// gate and plays attack/defend; <c>cp_egypt</c> places none and plays the
/// symmetrical arrangement. Only the first had a fixture, so the symmetrical
/// half's starting ownership and capture-time ramp had never been read back from
/// a real stream.
/// </para>
/// <para>
/// It is also the <b>largest control-point block anywhere</b>: five blocks, and
/// the block carries <b>no count prefix</b>, so a reader that rebuilt the points
/// from the map in the wrong number desynchronises. <c>koth_valley</c> tested one
/// and <c>dkoth_atalia</c> two.
/// </para>
/// </remarks>
public sealed class SymmetricControlPointFixtureTests
{
    private const string Fixture = "reference-cp_egypt-closedsymmetric.trace";

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

    /// <summary>Five points, the symmetrical arrangement, framed whole.</summary>
    [Fact]
    public void ItIsTheSymmetricalArrangementWithFivePointsAndFramesCleanly()
    {
        var (trace, ticks) = Load();
        var map = MapLoader("cp_egypt");
        var shape = GameModes.ShapeOf(map);

        Assert.Equal("cp_egypt", trace.Header.Map);
        Assert.Equal(GameMode.ControlPoints, shape.GameMode);
        Assert.Equal(5, shape.ControlPointCount);

        // ⚠️ The one thing that decides which of the two CP rule sets runs.
        Assert.False(GameModes.HasSetupGate(map));

        // And the control: the other CP map is the attack/defend one.
        Assert.True(GameModes.HasSetupGate(MapLoader("cp_dirtbowl")));

        Assert.True(ticks.Count > 4000, $"only {ticks.Count} ticks");
    }

    /// <summary>
    /// The control on the framing: five is the only point count that reads this
    /// recording.
    /// </summary>
    /// <remarks>
    /// Without it, "it framed" is equally consistent with a reader that never
    /// looked at the block — the same argument as
    /// <see cref="DualKothFixtureTests.NoOtherPointCountCanFrameThisRecording"/>,
    /// and worth repeating at five because that is where a per-point loop is most
    /// likely to be off by one.
    /// </remarks>
    [Fact]
    public void NoOtherPointCountCanFrameThisRecording()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", Fixture);
        var trace = Trace.Decode(File.ReadAllBytes(path));
        var shape = GameModes.ShapeOf(MapLoader("cp_egypt"));

        TraceParser.Parse(trace, shape, out _, out var atFive);
        Assert.Null(atFive);

        TraceParser.Parse(trace, shape with { ControlPointCount = 4 }, out _, out var atFour);
        Assert.NotNull(atFour);

        TraceParser.Parse(trace, shape with { ControlPointCount = 6 }, out _, out var atSix);
        Assert.NotNull(atSix);
    }

    /// <summary>
    /// ✅ <b>The five points open red, red, neutral, blue, blue</b> — read off the
    /// wire.
    /// </summary>
    /// <remarks>
    /// <c>ControlPoints.AssignStartingOwners</c>: a symmetrical map gives each
    /// team the points on its own side and leaves the middle one neutral. Nothing
    /// had ever observed that from a real stream, because the only CP recording
    /// was attack/defend, where every point starts blue.
    /// <para>
    /// ⚠️ <b>The wire cannot show <c>locked</c></b> — the block is team, capping
    /// team and meter — so the neighbouring rule that keeps four of these five
    /// locked stays unobservable. What this asserts is the ownership, which is
    /// what the lock rule is derived from.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheFivePointsOpenRedRedNeutralBlueBlue()
    {
        var (_, ticks) = Load();

        var first = ticks.First(t => t.ControlPoints.Length == 5);
        var teams = first.ControlPoints.Select(p => p.Team).ToArray();

        Assert.Equal(
            [(sbyte)Team.Red, (sbyte)Team.Red, (sbyte)-1, (sbyte)Team.Blue, (sbyte)Team.Blue],
            teams);
    }

    /// <summary>
    /// ✅ <b>And the PORT opens them the same way</b> — the assertion above is
    /// about the recording, this one is about
    /// <c>ControlPoints.AssignStartingOwners</c>.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Reading a fact off a fixture is not a gate on the port.</b>
    /// <see cref="TheFivePointsOpenRedRedNeutralBlueBlue"/> would go on passing
    /// against a port that opened every point neutral, because it never asks the
    /// port anything. This loads the same map into the same code the replayer
    /// uses and asks it, which is the half that bites: breaking
    /// <c>AssignStartingOwners</c> fails it.
    /// <para>
    /// ⚠️ <b>Asked directly rather than through a replay, and that is not
    /// laziness</b> — see
    /// <see cref="ThePortsMeterOnTheMiddlePointIsTwoAheadInsideTheWindow"/> for
    /// why a replay cannot answer it here.
    /// </para>
    /// </remarks>
    [Fact]
    public void ThePortOpensThePointsTheSameWay()
    {
        var simulation = new PortedSimulation(MapLoader);
        simulation.LoadMap("cp_egypt");

        var teams = new List<sbyte>();

        for (var i = 0; i < 5; i++)
        {
            Assert.True(simulation.TryGetControlPoint(i, out var point), $"no point {i}");
            teams.Add(point.Team);
        }

        // Exactly what the engine had on 2026-08-27, and what the recording's
        // own first observation shows.
        Assert.Equal(
            [(sbyte)Team.Red, (sbyte)Team.Red, (sbyte)-1, (sbyte)Team.Blue, (sbyte)Team.Blue],
            teams);

        // And no sixth, which is the shape the wire framing assumes.
        Assert.False(simulation.TryGetControlPoint(5, out _));
    }

    /// <summary>
    /// ✅ <b>The port's capture meter agrees with the reference</b>, on the one
    /// observation that falls inside this fixture's window.
    /// </summary>
    /// <remarks>
    /// <b>This fixture is where the disagreement was first seen</b>, and the
    /// window is what made it credible: 892 ticks (t709–t1601) with a
    /// control-point observation at <b>t1599</b> inside it — where
    /// <see cref="KothFixtureTests"/>'s earliest is three hundred ticks past the
    /// end. With every character exactly where the reference put it, drift was
    /// not available as an explanation, and the port read <b>65</b> where the
    /// reference read <b>63</b>.
    /// <para>
    /// ⚠️ <b>It was the harness, not the simulation.</b> <c>Replayer</c> compared
    /// the control-point block after running the tick, and the block describes
    /// the end of the <em>previous</em> tick. See
    /// <see cref="UncontestedControlPointFixtureTests.ThePortCapturesThePointExactlyAsTheReferenceDoes"/>,
    /// which carries the diagnosis and the second measurement that pinned its
    /// shape.
    /// </para>
    /// <para>
    /// Kept as a gate because it is the cheaper of the two — one observation, on
    /// a recording with an ordinary two-sided roster — and because it fails on
    /// the same ordering regression.
    /// </para>
    /// </remarks>
    [Fact]
    public void ThePortsMeterAgreesOnTheObservationInsideTheWindow()
    {
        var (trace, ticks) = Load();
        var anchor = FixtureAnchors.LastExactAnchor(ticks)!.Value;

        var trajectory = new Replayer(new PortedSimulation(MapLoader))
            { Compare = Fields.Trajectory }
            .Replay(Fixture, trace.Header.Map, ticks, ReplayMode.ClosedLoop, stopAtFirstBadTick: false);

        var bad = trajectory.Mismatches.Where(m => m.Tick > anchor).ToArray();
        var window = (bad.Length == 0 ? ticks[^1].Index : bad.Min(m => m.Tick)) - anchor;

        // The property that makes the comparison mean anything: the observation
        // is inside the window, so the characters are not the explanation.
        var inside = ticks
            .Where(t => t.ControlPoints.Length == 5)
            .Select(t => t.Index)
            .Where(i => i > anchor && i <= anchor + window)
            .ToArray();

        Assert.Equal([1599], inside);

        var points = new Replayer(new PortedSimulation(MapLoader))
            { Compare = Fields.ControlPoints }
            .Replay(Fixture, trace.Header.Map, ticks, ReplayMode.ClosedLoop, stopAtFirstBadTick: false);

        var atTheObservation = points.Mismatches.Where(m => m.Tick == 1599).ToArray();

        Assert.True(
            atTheObservation.Length == 0,
            "the port disagrees about the meter at t1599, inside the trajectory window: "
                + string.Join(
                    "; ",
                    atTheObservation.Select(m => $"{m.Field} exp {m.Expected} got {m.Actual}")));
    }
}
