using System.Linq;
using Gg2.Maps;
using Gg2.Parity;
using Gg2.Protocol;

namespace Gg2.Tests;

/// <summary>
/// The uncontested <c>cp_egypt</c> recording, which turns a two-tick glimpse of
/// the capture meter into a <b>4,617-tick</b> window — and settles that the
/// port's meter really does run ahead.
/// </summary>
/// <remarks>
/// <b>Recorded 2026-08-27: six red scouts and no blue team</b>, spawn-anchored,
/// roster forced after the reload, and the bots steered onto the neutral middle
/// point and then onto the next one.
/// <para>
/// ⚠️ <b>The one-sided roster is the design, for the third time.</b> What ends a
/// closed-loop window is weapon knockback, so removing the enemy team is how you
/// buy one long enough to watch a slow objective — it did this for
/// <c>koth_valley</c>'s hill and <c>ctf_2dfort</c>'s capture, and it is what was
/// needed here. <see cref="SymmetricControlPointFixtureTests"/> could see the
/// meter for exactly <b>two ticks</b>; this one watches the whole capture.
/// </para>
/// <para>
/// <b>Scouts on purpose</b>: <c>capStrength</c> 2 is the only class override in
/// the capture rules, so the meter moves twice as fast — and that doubling is
/// what made the error legible as <em>one tick of comparison offset</em> rather
/// than a wrong rate. See
/// <see cref="ThePortCapturesThePointExactlyAsTheReferenceDoes"/>.
/// </para>
/// </remarks>
public sealed class UncontestedControlPointFixtureTests
{
    private const string Fixture = "reference-cp_egypt-uncontestedcp.trace";

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

    private static int Anchor(IReadOnlyList<TraceTick> ticks)
    {
        var anchor = FixtureAnchors.LastExactAnchor(ticks);
        Assert.True(anchor is not null, $"{Fixture}: not closed-loop");
        return anchor!.Value;
    }

    private static DriftReport Replay(Trace trace, IReadOnlyList<TraceTick> ticks, Fields compare) =>
        new Replayer(new PortedSimulation(MapLoader)) { Compare = compare }
            .Replay(Fixture, trace.Header.Map, ticks, ReplayMode.ClosedLoop, stopAtFirstBadTick: false);

    /// <summary>
    /// It is the quiet recording it was meant to be, and it captures.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Not silent, and the difference matters.</b> With no enemy team there
    /// is still a handful of weapon fire and a few deaths — a scout shooting a
    /// wall, and falls. What buys the window is that <em>nothing shoves anybody
    /// else</em>, not that nothing happens. Asserted loosely for that reason:
    /// a re-recording that turned into a real fight would blow the window and
    /// should fail here first.
    /// </remarks>
    [Fact]
    public void ItIsOneSidedAndTheMiddlePointChangesHands()
    {
        var (_, ticks) = Load();

        Assert.True(
            ticks.Sum(t => t.Events.Count(m => m.Type == MessageType.WeaponFire)) < 40,
            "this is supposed to be an uncontested recording");

        var states = ticks
            .Where(t => t.ControlPoints.Length == 5)
            .Select(t => t.ControlPoints[2].Team)
            .ToHashSet();

        // Neutral at the start and red by the end: the middle point really is
        // captured inside the recording.
        Assert.Contains((sbyte)-1, states);
        Assert.Contains((sbyte)Team.Red, states);
    }

    /// <summary>
    /// ✅ <b>The window is 4,617 ticks and every control-point observation after
    /// the anchor is inside it.</b>
    /// </summary>
    /// <remarks>
    /// That is the whole point of the recording: it is the first time the
    /// symmetrical capture rules can be compared against the engine with the
    /// characters guaranteed to be where the reference put them, so a
    /// disagreement cannot be explained away as drift.
    /// </remarks>
    [Fact]
    public void EveryObservationAfterTheAnchorIsInsideTheTrajectoryWindow()
    {
        var (trace, ticks) = Load();
        var anchor = Anchor(ticks);

        var bad = Replay(trace, ticks, Fields.Trajectory).Mismatches.Where(m => m.Tick > anchor).ToArray();
        var window = (bad.Length == 0 ? ticks[^1].Index : bad.Min(m => m.Tick)) - anchor;

        Assert.True(window >= 4000, $"the window is only {window} ticks after t{anchor}");

        var outside = ticks
            .Where(t => t.ControlPoints.Length == 5)
            .Select(t => t.Index)
            .Where(i => i > anchor && i > anchor + window)
            .ToArray();

        Assert.True(outside.Length == 0, $"observations outside the window: {string.Join(", ", outside)}");

        // And there really are some to compare — three, at t1479, t1577 and
        // t2723. Without this the test passes on a recording that observed
        // nothing at all.
        var inside = ticks.Count(t => t.ControlPoints.Length == 5 && t.Index > anchor);
        Assert.True(inside >= 3, $"only {inside} observation(s) after the anchor");
    }

    /// <summary>
    /// ✅ <b>The port captures the point exactly as the reference does</b> — the
    /// first time the symmetrical capture rules have been held to the engine.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>This was an open finding for a few hours, and it was the HARNESS.</b>
    /// The port looked like it started every capture two ticks early: +2 against
    /// three soldiers on <c>reference-cp_egypt-closedsymmetric.trace</c> and +4
    /// against six scouts here, which is what a fixed head start does to a meter
    /// that advances by <c>capStrength</c> a tick. It was not a head start.
    /// <c>Replayer</c> compared the control-point block <em>after</em> running
    /// the tick, where the block — serialised in the server's Begin Step, like
    /// the positions beside it — describes the <b>end of the previous tick</b>.
    /// One tick of capture, read as a rule error.
    /// <para>
    /// ⚠️ <b>The lesson is the one this project keeps paying for</b>: the KotH
    /// capture looked exactly like this and turned out to be the replay starting
    /// the round early. <b>Eliminate the harness before believing a divergence
    /// is the simulation</b> — and note what nearly hid it, which is that the
    /// wrong order made <c>koth_valley</c> exact and only a second CP recording
    /// with a different <c>capStrength</c> made the shape of the error legible.
    /// </para>
    /// <para>
    /// This test is now the gate on that ordering: moving the comparison back
    /// after <c>Advance</c> fails it, and so does moving the unlock injection
    /// after the comparison.
    /// </para>
    /// </remarks>
    [Fact]
    public void ThePortCapturesThePointExactlyAsTheReferenceDoes()
    {
        var (trace, ticks) = Load();
        var anchor = Anchor(ticks);

        var mismatches = Replay(trace, ticks, Fields.ControlPoints)
            .Mismatches
            .Where(m => m.Tick > anchor)
            .ToArray();

        // ⚠️ The control, and it is the one that was missing across three of the
        // five comparison families. "No mismatches" is satisfied just as well by
        // a comparison that never runs — measured: routing
        // ReconcileControlPoints' output to a discarded list failed nothing until
        // this existed. Replay with every observed point's owner flipped to
        // spectator: the port still owns them properly, so the comparison must
        // report a difference. If it reports nothing, it is dead.
        var doctored = ticks
            .Select(t => new TraceTick
            {
                Index = t.Index,
                Tier = t.Tier,
                Slots = t.Slots,
                ControlPointUnlock = t.ControlPointUnlock,
                Events = t.Events,
                ControlPoints = t.ControlPoints
                    .Select(p => p with { Team = (sbyte)Team.Spectator })
                    .ToArray(),
            })
            .ToList();

        var control = new Replayer(new PortedSimulation(MapLoader)) { Compare = Fields.ControlPoints }
            .Replay(Fixture, trace.Header.Map, doctored, ReplayMode.ClosedLoop, stopAtFirstBadTick: false);

        Assert.True(
            control.Mismatches.Any(m => m.Tick > anchor),
            "the control-point comparison reported nothing against doctored blocks — it is not "
                + "running, so the assertion below proves nothing");

        Assert.True(
            mismatches.Length == 0,
            "the port disagrees about cp_egypt's points: "
                + string.Join(
                    "; ",
                    mismatches.Take(5).Select(m => $"t{m.Tick} pt{m.Slot} {m.Field} exp {m.Expected} got {m.Actual}")));
    }
}
