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
        var simulation = new PortedSimulation(MapLoader);
        if (nudgeX != 0 || nudgeY != 0)
            simulation.Nudges[0] = new AnchorNudge(nudgeX, nudgeY, 0, 0);

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
    /// The residual mismatch <b>is</b> the anchor's quantisation, not the
    /// arithmetic.
    /// </summary>
    /// <remarks>
    /// A trace anchors from the wire, which carries position to a fifth of a
    /// pixel and velocity to <c>1/8.5</c> px/tick, so a replay necessarily
    /// starts somewhere inside those bins rather than on the state the reference
    /// was actually in. That makes a closed-loop replay of a <em>mid-game</em>
    /// recording unable to reach zero by construction, and it is why
    /// <see cref="PhysicsSampleTests"/> exists: there the start state is chosen
    /// exactly and the gate is bit equality.
    /// <para>
    /// <b>This test used to assert the opposite</b>, on a sweep that nudged only
    /// the slot that failed first. That sweep never reached zero — but not
    /// because the arithmetic was wrong: it bottomed out on the <em>next</em>
    /// slot's residual, which no offset applied to this slot can touch. Every
    /// anchored character has its own independent bin, so the experiment has to
    /// be per-slot.
    /// </para>
    /// <para>
    /// Settled live, and this is the measurement that matters: driving the
    /// reference engine's own <c>Character</c> events through this trace's
    /// recorded inputs, from this same wire-quantised anchor, the engine
    /// produces <c>y = 516.6899190010863</c> entering tick 218 — wire 2583, the
    /// port's answer, and one unit away from what its own recording said. The
    /// engine disagrees with itself by exactly the amount this replay does. See
    /// docs/SIMULATION.md.
    /// </para>
    /// </remarks>
    [Fact]
    public void NudgingEachSlotInsideItsOwnBinPushesTheDivergenceLater()
    {
        var (trace, ticks) = Load();

        var baseline = Replay(ticks, trace.Header.Map, ReplayMode.ClosedLoop);
        Assert.NotEmpty(baseline.Mismatches);

        // Greedy, one slot at a time: fix whichever slot fails first by sweeping
        // its own y across its own bin, then look at what fails next.
        var nudges = new Dictionary<int, AnchorNudge>();
        var reached = baseline.TicksReplayed;

        for (var round = 0; round < 4; round++)
        {
            var report = Run(ticks, trace.Header.Map, nudges);
            if (report.Mismatches.Count == 0)
                break;

            var slot = report.Mismatches[0].Slot;
            var best = report.TicksReplayed;
            AnchorNudge? bestNudge = null;

            // The bin is a fifth of a pixel wide, so ±0.1 covers it.
            for (var nudge = -0.1; nudge <= 0.1001; nudge += 0.002)
            {
                var candidate = new AnchorNudge(0, Math.Round(nudge, 4), 0, 0);
                nudges[slot] = candidate;

                var swept = Run(ticks, trace.Header.Map, nudges);
                if (swept.TicksReplayed <= best)
                    continue;

                best = swept.TicksReplayed;
                bestNudge = candidate;
            }

            nudges.Remove(slot);
            if (bestNudge is null)
                break;

            nudges[slot] = bestNudge.Value;
            reached = best;
        }

        // The measured result. Each slot's first divergence is inside its own
        // anchor bin and clears when the anchor moves within it; what stops the
        // greedy walk at the end is a later divergence on a slot it has already
        // spent its one offset on, not a residual it cannot explain.
        Assert.True(reached > 380,
            $"nudging inside the bins only reached tick {reached}, was expecting past 380");
        Assert.True(reached > baseline.TicksReplayed,
            $"nudging did not help at all: {reached} vs {baseline.TicksReplayed}");
    }

    private static DriftReport Run(
        IReadOnlyList<TraceTick> ticks,
        string map,
        IReadOnlyDictionary<int, AnchorNudge> nudges)
    {
        var simulation = new PortedSimulation(MapLoader);
        foreach (var (slot, nudge) in nudges)
            simulation.Nudges[slot] = nudge;

        return new Replayer(simulation) { Compare = Fields.Trajectory | Fields.Hp }
            .Replay(BotTrace, map, ticks, ReplayMode.ClosedLoop);
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
