using System.Linq;
using Gg2.Maps;
using Gg2.Parity;
using Gg2.Protocol;

namespace Gg2.Tests;

/// <summary>
/// The King of the Hill recording whose capture happens inside its trajectory
/// window — the first fixture able to gate the capture rules at all.
/// </summary>
/// <remarks>
/// <b>Recorded 2026-08-27: six red scouts and no blue team on
/// <c>koth_valley</c></b>, spawn-anchored.
/// <para>
/// ⚠️ <b>The one-sided roster is the whole design, and it is not laziness.</b>
/// <c>reference-koth_valley-closedkoth.trace</c> has a real match in it and is
/// useless for this: its trajectory window is 330 ticks and its earliest
/// control-point observation is at t1010, so every disagreement about the hill
/// could equally be explained by characters having drifted somewhere else. The
/// hill cannot be captured before its unlock timer fires (~900 ticks), and the
/// unlock cannot be forced — the port simulates its own, so forcing the
/// reference's early would manufacture a mismatch rather than expose one.
/// <b>So the window had to grow instead</b>, and what ends these windows is
/// weapon knockback: with nobody to fight, this one runs <b>3,111</b> ticks and
/// swallows the capture whole.
/// </para>
/// </remarks>
public sealed class KothCaptureFixtureTests
{
    private const string Fixture = "reference-koth_valley-uncontested.trace";

    /// <summary>The measured trajectory window, anchor t485.</summary>
    private const int TrajectoryWindow = 3111;

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

    private static (DriftReport Report, int Anchor, IReadOnlyList<TraceTick> Ticks) Replay(Fields compare)
    {
        var (trace, ticks) = Load();
        var anchor = FixtureAnchors.LastExactAnchor(ticks);
        Assert.True(anchor is not null, $"{Fixture}: not closed-loop");

        var report = new Replayer(new PortedSimulation(MapLoader)) { Compare = compare }
            .Replay(Fixture, trace.Header.Map, ticks, ReplayMode.ClosedLoop, stopAtFirstBadTick: false);

        return (report, anchor!.Value, ticks);
    }

    /// <summary>
    /// It is the quiet recording it was meant to be, and it holds a capture.
    /// </summary>
    [Fact]
    public void ItIsUncontestedAndTheHillChangesHands()
    {
        var (_, ticks) = Load();

        // ⚠️ Asserted, because the whole value of this fixture rests on it: a
        // re-recording with a blue team in it would have a short window again
        // and quietly stop gating the capture.
        Assert.Equal(0, ticks.Sum(t => t.Events.Count(m => m.Type == MessageType.WeaponFire)));
        Assert.Equal(0, ticks.Sum(t => t.Events.Count(m => m.Type == MessageType.PlayerDeath)));

        var states = ticks.Where(t => t.ControlPoints.Length > 0)
            .Select(t => (t.ControlPoints[0].Team, t.ControlPoints[0].Capping))
            .ToArray();

        Assert.Contains(states, s => s.Team == -1 && s.Capping > 0);  // being captured
        Assert.Contains(states, s => s.Team == 0);                     // captured
    }

    /// <summary>
    /// ✅ <b>Every control-point observation falls inside the trajectory
    /// window</b> — the property that makes this fixture a gate.
    /// </summary>
    /// <remarks>
    /// While the trajectories agree, a disagreement about the hill cannot be
    /// blamed on characters being somewhere else, so it is a statement about the
    /// capture rules and nothing else. That is exactly what
    /// <c>reference-koth_valley-closedkoth.trace</c> could not say.
    /// </remarks>
    [Fact]
    public void EveryControlPointObservationIsInsideTheTrajectoryWindow()
    {
        var (report, anchor, ticks) = Replay(Fields.Trajectory);

        var after = report.Mismatches.Where(m => m.Tick > anchor).ToArray();
        var firstBad = after.Length == 0 ? ticks[^1].Index : after.Min(m => m.Tick);
        var window = firstBad - anchor;

        Assert.True(window >= 3000, $"trajectory window is only {window} ticks (anchor t{anchor})");

        var observations = ticks
            .Where(t => t.Index > anchor && t.ControlPoints.Length > 0)
            .Select(t => t.Index)
            .ToArray();

        Assert.True(observations.Length >= 5, $"only {observations.Length} observations after the anchor");
        Assert.All(observations, i => Assert.True(i <= anchor + window, $"t{i} is outside the window"));
    }

    /// <summary>
    /// ✅ <b>The port reproduces this recording's hill exactly</b> — ownership,
    /// capping team and meter, on every observation.
    /// </summary>
    /// <remarks>
    /// <b>This replaces <c>ThePortStillCapturesTheHillTooEarly</c></b>, which
    /// pinned an eleven-mismatch divergence and was written to fail the day it
    /// was fixed. It failed on 2026-08-27.
    /// <para>
    /// The divergence was never in the capture arithmetic. It was that a replay
    /// started the King of the Hill pregame timer at the <c>CHANGE_MAP</c>,
    /// which is <b>not</b> the round start — the server keeps serialising the
    /// old round's HUD for a few hundred ticks after announcing the change
    /// (335 in this recording, 254 in <c>closedkoth</c>, so not a constant to
    /// subtract). <c>Replayer</c> now anchors the counter from the wire.
    /// </para>
    /// <para>
    /// ⚠️ <b>Only a positive <c>cpUnlock</c> is taken, and that is the whole
    /// trick.</b> A zero is ambiguous — it means "already unlocked", which is
    /// true of the round the recorder joined <em>and</em> of the one after the
    /// reload — and the early readings here are stale zeroes at t0 and t62. A
    /// positive value is unambiguous, because
    /// <c>KothHUD.events/Step.xml</c> fires the unlock on the tick the counter
    /// reads 0: it says both "this many ticks left" and "still locked".
    /// </para>
    /// <para>
    /// What is conceded is the countdown itself, which is <c>-1</c> a tick and
    /// unit-tested, and whose arithmetic <c>KothUnlockTimingTests</c> gates
    /// against the recordings directly. What is gated here is everything
    /// downstream: who is standing on the point, the capture rate, and
    /// ownership — with the trajectories exact throughout, so none of it can be
    /// explained by characters being somewhere else.
    /// </para>
    /// </remarks>
    [Fact]
    public void ThePortReproducesTheHillExactly()
    {
        var (report, anchor, _) = Replay(Fields.ControlPoints);

        var bad = report.Mismatches.Where(m => m.Tick > anchor).ToArray();

        Assert.True(
            bad.Length == 0,
            $"{bad.Length} control-point mismatches after t{anchor}: "
            + string.Join(", ", bad.Take(6).Select(m => $"t{m.Tick} {m.Field} exp {m.Expected} got {m.Actual}")));
    }
}
