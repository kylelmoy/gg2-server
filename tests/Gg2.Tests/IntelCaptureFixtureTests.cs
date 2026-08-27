using System.Linq;
using Gg2.Maps;
using Gg2.Parity;
using Gg2.Protocol;

namespace Gg2.Tests;

/// <summary>
/// The first recording whose <b>grab and capture both happen inside a
/// closed-loop window</b> — so the first that can gate the objective against the
/// engine at all.
/// </summary>
/// <remarks>
/// <b>Recorded 2026-08-27: six red scouts and no blue team on
/// <c>ctf_2dfort</c></b>, spawn-anchored by the map-reload recipe, with the bots
/// steered to blue's briefcase and then home again
/// (<c>botGoalLocked</c> + <c>botSetGoal</c>).
/// <para>
/// ⚠️ <b>The one-sided roster is the design, exactly as it is for
/// <see cref="KothCaptureFixtureTests"/>.</b>
/// <c>reference-ctf_2dfort-closedrespawn.trace</c> already held a
/// <c>GRAB_INTEL</c> and the <c>SCORE_INTEL</c> that followed it — the first
/// capture-with-its-grab in any fixture here — and could gate neither, because
/// its window ends at t877 and the grab is at t1404. <b>What ends a closed-loop
/// window is weapon knockback</b>, so removing the enemy team is how you buy one
/// long enough to reach a slow objective. This recording replays with <b>no
/// mismatches at all, over its whole 4,934 ticks</b>.
/// </para>
/// <para>
/// ⚠️ <b>Steering bots cannot invalidate a closed-loop fixture</b> — a replay
/// consumes the recorded <c>INPUTSTATE</c>s and simulates no bot AI, so
/// <c>botSetGoal</c> changes what gets recorded and nothing about how it
/// replays. A <em>teleport</em> would break the anchor; a goal does not.
/// docs/PARITY.md.
/// </para>
/// </remarks>
public sealed class IntelCaptureFixtureTests
{
    private const string Fixture = "reference-ctf_2dfort-uncontestedcap.trace";

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

    private static int TickOf(IReadOnlyList<TraceTick> ticks, MessageType type) =>
        ticks.First(t => t.Events.Any(m => m.Type == type)).Index;

    /// <summary>
    /// It is the quiet recording it was meant to be, and it holds both halves of
    /// a capture.
    /// </summary>
    /// <remarks>
    /// Asserted because the fixture's whole value rests on it: a re-recording
    /// with a blue team in it would have a short window again and would go on
    /// passing the window test below while quietly gating nothing.
    /// </remarks>
    [Fact]
    public void ItIsUncontestedAndHoldsAGrabAndACapture()
    {
        var (_, ticks) = Load();

        Assert.Equal(0, ticks.Sum(t => t.Events.Count(m => m.Type == MessageType.WeaponFire)));
        Assert.Equal(0, ticks.Sum(t => t.Events.Count(m => m.Type == MessageType.PlayerDeath)));

        Assert.Equal(1, ticks.Sum(t => t.Events.Count(m => m.Type == MessageType.GrabIntel)));
        Assert.Equal(1, ticks.Sum(t => t.Events.Count(m => m.Type == MessageType.ScoreIntel)));

        // The grab comes first, and the run home is long enough to be a real
        // journey rather than a briefcase dropped on its own base.
        var grab = TickOf(ticks, MessageType.GrabIntel);
        var score = TickOf(ticks, MessageType.ScoreIntel);

        // Measured: anchor t618, grab t1355, capture t2867, last tick t4933 —
        // so both events sit more than seven hundred ticks inside the window at
        // either end, rather than clinging to its edge.
        Assert.True(score > grab + 500, $"grab t{grab} and capture t{score} are too close together");
    }

    /// <summary>
    /// ✅ <b>The whole recording replays exactly</b> — trajectory, hp and deaths,
    /// every tick of it.
    /// </summary>
    /// <remarks>
    /// The longest exact window in the project, and it is long for a reason
    /// rather than by luck: no weapon is ever fired, so nothing is ever shoved.
    /// It is the same trade the uncontested KotH fixture makes — a long window on
    /// a quiet recording is worth less per tick than a short one on a loud one,
    /// and is the only way to reach an objective that takes a minute to happen.
    /// </remarks>
    [Fact]
    public void TheWholeRecordingIsExact()
    {
        var (report, anchor, ticks) = Replay(Fields.Trajectory | Fields.Hp | Fields.Deaths);

        var after = report.Mismatches.Where(m => m.Tick > anchor).ToArray();

        Assert.True(
            after.Length == 0,
            $"{Fixture}: {after.Length} mismatch(es) after t{anchor}, first at t{(after.Length == 0 ? -1 : after[0].Tick)}");

        Assert.True(ticks[^1].Index - anchor > 4000, "the window is shorter than it was recorded to be");
    }

    /// <summary>
    /// ✅ <b>The gate this fixture exists for: the port grabs and captures on the
    /// reference's own ticks.</b>
    /// </summary>
    /// <remarks>
    /// ⚠️ <c>Fields.Intel</c> compares the objective's <em>messages</em>, not any
    /// state on the wire — a briefcase and a carrier's <c>intel</c> byte are
    /// <c>FULL_UPDATE</c>-only and a recording holds exactly one of those, on the
    /// join path before anything has happened. So this asserts that the port
    /// emits a <c>GRAB_INTEL</c> and a <c>SCORE_INTEL</c> on the same ticks the
    /// reference did, which is what <c>doEventGrabIntel</c> and
    /// <c>doEventScoreIntel</c> amount to from outside.
    /// <para>
    /// Every earlier fixture that observed the objective did so outside its
    /// window, so adding the whole of the objective in phase 6 moved none of the
    /// five closed-loop windows by a tick (docs/PARITY.md). <b>This is the first
    /// fixture for which deleting the objective would fail a test.</b>
    /// </para>
    /// </remarks>
    [Fact]
    public void ThePortGrabsAndCapturesOnTheReferencesTicks()
    {
        var (report, anchor, _) = Replay(Fields.Intel);

        var after = report.Mismatches.Where(m => m.Tick > anchor).ToArray();

        Assert.True(
            after.Length == 0,
            $"{Fixture}: the objective diverges — "
                + string.Join(
                    "; ",
                    after.Take(4).Select(m => $"t{m.Tick} {m.Field} exp {m.Expected} got {m.Actual}")));
    }

    /// <summary>
    /// The control that proves the test above bites: both events really do fall
    /// inside the window it is asserted over.
    /// </summary>
    /// <remarks>
    /// Without this, <see cref="ThePortGrabsAndCapturesOnTheReferencesTicks"/>
    /// would pass just as happily if the objective happened before the anchor and
    /// the comparison saw nothing at all — which is exactly the bug the
    /// control-point comparison shipped with on its first run (docs/PARITY.md,
    /// "the comparison must not be gated on <c>HasPositions</c>").
    /// </remarks>
    [Fact]
    public void BothEventsFallInsideTheWindowTheGateAssertsOver()
    {
        var (_, ticks) = Load();
        var anchor = FixtureAnchors.LastExactAnchor(ticks)!.Value;

        var grab = TickOf(ticks, MessageType.GrabIntel);
        var score = TickOf(ticks, MessageType.ScoreIntel);

        Assert.True(grab > anchor, $"the grab at t{grab} is not inside the window (anchor t{anchor})");
        Assert.True(score > anchor, $"the capture at t{score} is not inside the window (anchor t{anchor})");

        // ⚠️ And the comparison is alive, which the two assertions above do NOT
        // establish. "No mismatches" is satisfied just as well by a comparison
        // that never runs — measured: routing ReconcileIntel's output to a
        // discarded list failed nothing until this control existed, the same
        // hole that left Fields.Ubers unexercised for a phase. Replay the trace
        // with the intel messages stripped out: the port still grabs and
        // captures, so the comparison must report them as unexpected.
        var stripped = ticks
            .Select(t => new TraceTick
            {
                Index = t.Index,
                Tier = t.Tier,
                Slots = t.Slots,
                ControlPoints = t.ControlPoints,
                ControlPointUnlock = t.ControlPointUnlock,
                Events = t.Events
                    .Where(m => m.Type is not (MessageType.GrabIntel or MessageType.ScoreIntel
                        or MessageType.DropIntel or MessageType.ReturnIntel))
                    .ToArray(),
            })
            .ToList();

        var trace = Trace.Decode(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", Fixture)));

        var control = new Replayer(new PortedSimulation(MapLoader)) { Compare = Fields.Intel }
            .Replay(Fixture, trace.Header.Map, stripped, ReplayMode.ClosedLoop, stopAtFirstBadTick: false);

        Assert.True(
            control.Mismatches.Any(m => m.Tick > anchor && m.Expected == 0),
            "the intel comparison reported nothing against a trace with the intel messages removed — "
                + "it is not running, so the gate above proves nothing");
    }
}
