using Gg2.Maps;
using Gg2.Parity;
using Gg2.Protocol;

namespace Gg2.Tests;

/// <summary>
/// The ported physics against a recording of the real GM8 server.
/// </summary>
/// <remarks>
/// docs/PARITY.md tier 2, with the real simulation behind the seam. The fixture
/// is thirty seconds of two bots playing <c>ctf_truefort</c> — real spawns,
/// real jumps, real falls — recorded from the reference and replayed here with
/// no game running.
/// </remarks>
public sealed class PhysicsParityTests
{
    private const string BotTrace = "reference-ctf_truefort-bots.trace";

    private static (Trace Trace, IReadOnlyList<TraceTick> Ticks) Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", BotTrace);
        Assert.True(File.Exists(path), $"missing {path}");

        var trace = Trace.Decode(File.ReadAllBytes(path));
        var map = LevelMap.FromPng(trace.Header.Map, ReferenceTree.ReadMap(trace.Header.Map));
        var ticks = TraceParser.Parse(trace, GameModes.ShapeOf(map), out _);

        return (trace, ticks);
    }

    private static LevelMap MapLoader(string name) =>
        LevelMap.FromPng(name, ReferenceTree.ReadMap(name));

    private static DriftReport Replay(
        IReadOnlyList<TraceTick> ticks,
        string map,
        ReplayMode mode,
        double nudgeX = 0,
        double nudgeY = 0,
        bool stopAtFirstBadTick = true)
    {
        var simulation = new PortedSimulation(MapLoader) { AnchorNudgeX = nudgeX, AnchorNudgeY = nudgeY };

        return new Replayer(simulation) { Compare = Fields.Trajectory | Fields.Hp }
            .Replay(BotTrace, map, ticks, mode, stopAtFirstBadTick);
    }

    /// <summary>
    /// The ported physics tracks the reference exactly for hundreds of ticks
    /// with no correction at all.
    /// </summary>
    /// <remarks>
    /// Closed-loop: anchored once, then never told the answer again. This is the
    /// honest measure of "is the physics the same", and it is a far stronger
    /// statement than the open-loop one — errors compound rather than being
    /// wiped every seventh tick.
    /// </remarks>
    [Fact]
    public void ClosedLoopTracksTheReferenceForHundredsOfTicks()
    {
        var (trace, ticks) = Load();
        var report = Replay(ticks, trace.Header.Map, ReplayMode.ClosedLoop);

        Assert.True(report.Comparisons > 40, $"only {report.Comparisons} comparisons");

        // Horizontal is exact over the whole run: friction is contractive, so a
        // sub-quantisation error in the anchor shrinks rather than compounds.
        Assert.Equal(0, report.MaxXError);

        // Vertical is within one wire unit — a fifth of a world pixel, which is
        // the resolution the anchor itself was quantised to.
        Assert.True(report.MaxYError <= 1, $"max |dy| was {report.MaxYError}: {report.Render()}");
        Assert.True(report.TicksReplayed > 200, $"only reached tick {report.TicksReplayed}");
    }

    /// <summary>
    /// The residual mismatch is <b>not</b> explained by the anchor's position
    /// quantisation — which is worth knowing, because it was the obvious
    /// explanation and it is wrong.
    /// </summary>
    /// <remarks>
    /// A trace anchors from the wire, which carries position to a fifth of a
    /// pixel, so a replay necessarily starts somewhere inside that bin rather
    /// than on the reference's own value. If that were the whole story, some
    /// offset inside the bin would replay exactly. None does.
    /// <para>
    /// So this test pins the negative result rather than pretending it is a
    /// positive one: sweeping ±0.1px on the affected slot never reaches zero,
    /// which means there is a genuine sub-pixel difference in the arithmetic
    /// still to find. docs/PLAN.md phase 4 is explicit that a persistent
    /// one-unit difference is a real ordering bug rather than noise, and the
    /// gate stays un-loosened until it is found.
    /// </para>
    /// <para>
    /// Still to rule out: the anchor's <em>velocity</em>, which the wire
    /// quantises far more coarsely than position — a whole 1/8.5 px/tick — and
    /// which decides where a landing crawl stops.
    /// </para>
    /// </remarks>
    [Fact]
    public void ResidualMismatchIsNotTheAnchorsPositionQuantisation()
    {
        var (trace, ticks) = Load();

        // Which slot the residual mismatch is on. Nudging every character at
        // once is the wrong experiment: an offset that fixes one can break
        // another, and the total never reaches zero for reasons that have
        // nothing to do with either.
        var baseline = Replay(ticks, trace.Header.Map, ReplayMode.ClosedLoop);
        Assert.NotEmpty(baseline.Mismatches);
        var slot = baseline.Mismatches[0].Slot;

        var best = baseline.Mismatches.Count;
        double bestOffset = 0;

        // The bin is one fifth of a pixel wide, so ±0.1 covers it.
        for (var nudge = -0.1; nudge <= 0.1001; nudge += 0.002)
        {
            var simulation = new PortedSimulation(MapLoader) { AnchorNudgeY = nudge, NudgeSlot = slot };

            var report = new Replayer(simulation) { Compare = Fields.Trajectory | Fields.Hp }
                .Replay(BotTrace, trace.Header.Map, ticks, ReplayMode.ClosedLoop);

            if (report.Mismatches.Count >= best)
                continue;

            best = report.Mismatches.Count;
            bestOffset = nudge;

            if (best == 0)
                break;
        }

        // The measured result, pinned so it is noticed if it changes in either
        // direction. If a later fix makes this reach zero, this test should be
        // turned back into the positive assertion it was written as.
        Assert.Equal(1, best);
        Assert.True(Math.Abs(bestOffset) <= 0.1, $"best offset {bestOffset} left the bin");
        Assert.True(slot >= 0);
    }

    /// <summary>
    /// Open-loop: the prediction error a player would actually see, measured at
    /// every correction over the whole recording.
    /// </summary>
    /// <remarks>
    /// Reported as a distribution rather than pass/fail, per docs/PARITY.md.
    /// The gate is loose on the maximum and tight on the 99th percentile,
    /// because the interesting number is what happens on a typical tick — one
    /// bad window is a discrete event to go and find, not a reason to fail the
    /// suite while the fixture set is this small.
    /// </remarks>
    [Fact]
    public void OpenLoopPredictionErrorIsSubPixelOnATypicalTick()
    {
        var (trace, ticks) = Load();
        var report = Replay(ticks, trace.Header.Map, ReplayMode.OpenLoop, stopAtFirstBadTick: false);

        Assert.True(report.Comparisons > 150, $"only {report.Comparisons} comparisons");

        // Five wire units is one world pixel.
        Assert.True(report.PercentileXError(99) <= 25,
            $"p99 |dx| was {report.PercentileXError(99)}: {report.Render()}");
        Assert.True(report.PercentileYError(99) <= 5,
            $"p99 |dy| was {report.PercentileYError(99)}: {report.Render()}");
    }

    /// <summary>
    /// One update carries this tick's input and last tick's position. Replaying
    /// as though it were the other way round makes a correct simulation look
    /// systematically slow, which is how the pairing was settled.
    /// </summary>
    [Fact]
    public void PairingInputsWithTheWrongTickIsMeasurablyWorse()
    {
        var (trace, ticks) = Load();

        var right = Replay(ticks, trace.Header.Map, ReplayMode.OpenLoop, stopAtFirstBadTick: false);

        var wrong = new Replayer(new PortedSimulation(MapLoader))
        {
            Compare = Fields.Trajectory | Fields.Hp,
            UpdatesPrecedeTheirTick = false,
        }.Replay(BotTrace, trace.Header.Map, ticks, ReplayMode.OpenLoop, stopAtFirstBadTick: false);

        Assert.True(wrong.PercentileXError(99) > right.PercentileXError(99),
            $"off-by-one p99 |dx| {wrong.PercentileXError(99)} was not worse than {right.PercentileXError(99)}");
    }

    /// <summary>
    /// The harness can still fail. Without this, a bug that made every
    /// comparison vacuous would show up as a clean pass.
    /// </summary>
    [Fact]
    public void TheEmptySimulationStillFailsTheSameTrace()
    {
        var (trace, ticks) = Load();

        var report = new Replayer(new EmptySimulation()) { Compare = Fields.Trajectory | Fields.Hp }
            .Replay(BotTrace, trace.Header.Map, ticks, ReplayMode.ClosedLoop);

        Assert.False(report.Passed);
        Assert.True(report.MaxXError > 50, $"a still simulation only drifted {report.MaxXError}");
    }
}
