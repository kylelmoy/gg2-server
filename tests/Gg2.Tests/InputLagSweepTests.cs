using Gg2.Maps;
using Gg2.Parity;
using Gg2.Protocol;

namespace Gg2.Tests;

/// <summary>
/// docs/PARITY.md's <b>instrument 3</b>: which tick's keys best explain the
/// motion that followed.
/// </summary>
/// <remarks>
/// ⛔ <b>This is the half of the one-tick bug class that nothing else can
/// see.</b> The flush-boundary instruments — the stage table, <c>WireStageTests</c>,
/// <c>FlushShapeTests</c> — all ask WHEN a message leaves. <c>486ef3e</c>, the
/// bug that started the whole hunt, was a message that left at exactly the right
/// moment carrying the PREVIOUS tick's keys. No framing test can distinguish
/// that from a correct one, and no closed-loop replay can either: the port
/// simulated with the right keys and only published the wrong ones.
/// <para>
/// <b>The instrument.</b> Replay open-loop with the recorded inputs shifted by
/// <c>L</c> ticks and measure how far the prediction drifts before the next
/// correction lands. A stream whose keys are in phase predicts best at
/// <c>L = 0</c>. A stream whose server published each tick's keys a tick late
/// predicts best at <c>L = +1</c>, because the keys filed under tick <c>N</c>
/// are really tick <c>N-1</c>'s and the motion is explained by the next row down.
/// </para>
/// <para>
/// ⚠️ <b>Open loop is not a preference here, it is the only mode that works.</b>
/// Closed loop stops at the first mismatch, and a deliberately mis-phased replay
/// mismatches almost immediately — the run would end before the prediction error
/// meant anything. Open loop re-anchors from the trace at every position-bearing
/// tick, so every window is an independent measurement.
/// </para>
/// <para>
/// ⚠️ <b>What this gates is the RECORDING, not the port.</b> Run against a
/// reference trace it says the reference's stream is in phase and the port's
/// physics agrees with it — which is what validates the instrument. Pointing it
/// at a recording made by this port's own <c>--record</c> is what would have
/// caught <c>486ef3e</c>, and that is not wired up here: no port-made recording
/// is checked in. docs/PARITY.md says so in the same words.
/// </para>
/// </remarks>
public class InputLagSweepTests
{
    /// <summary>How far either side of zero to sweep.</summary>
    private const int MaxLag = 2;

    private static LevelMap MapLoader(string name) =>
        LevelMap.FromPng(name, ReferenceTree.ReadMap(name));

    /// <summary>
    /// The prediction error at one lag, as the 99th percentile of the open-loop
    /// drift in x and y together.
    /// </summary>
    /// <remarks>
    /// ⚠️ p99 rather than max, for the reason <see cref="ClientPredictionGateTests"/>
    /// gives: a respawn teleports a character across the map and shows up as an
    /// "error" of hundreds. Those are real events, not drift, and a max would be
    /// dominated by them at every lag equally — which would flatten the very
    /// curve this reads.
    /// </remarks>
    private static long ErrorAtLag(string fixture, int lag)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture + ".trace");
        Assert.True(File.Exists(path), $"missing {path}");

        var trace = Trace.Decode(File.ReadAllBytes(path));
        var map = MapLoader(trace.Header.Map);
        var ticks = TraceParser.Parse(trace, GameModes.ShapeOf(map), out _, out _);

        var report = new Replayer(new PortedSimulation(MapLoader))
        {
            Compare = Fields.Trajectory,
            InputLag = lag,
        }.Replay(fixture, trace.Header.Map, ticks, ReplayMode.OpenLoop, stopAtFirstBadTick: false);

        return report.PercentileXError(99) + report.PercentileYError(99);
    }

    private static IReadOnlyDictionary<int, long> Sweep(string fixture)
    {
        var curve = new Dictionary<int, long>();

        for (var lag = -MaxLag; lag <= MaxLag; lag++)
            curve[lag] = ErrorAtLag(fixture, lag);

        return curve;
    }

    private static string Describe(IReadOnlyDictionary<int, long> curve) =>
        string.Join(", ", curve.OrderBy(e => e.Key).Select(e => $"{e.Key:+0;-0;0}={e.Value}"));

    /// <summary>
    /// ⛔ The reference's own stream is in phase: lag 0 predicts strictly better
    /// than being a tick out in either direction.
    /// </summary>
    /// <remarks>
    /// <b>STRICTLY, in both directions, and that is the assertion that matters.</b>
    /// "argmin is 0" alone is satisfied by a flat curve, and a flat curve means
    /// the instrument is measuring nothing — see
    /// <see cref="TheSweepIsNotFlatSoAMisPhasedStreamWouldShow"/>, which is the
    /// same fact stated so it cannot be read as a formality.
    /// </remarks>
    [Theory]
    [InlineData("reference-ctf_truefort-closedcombat")]
    [InlineData("reference-gg_debug-closedtdm")]
    public void TheReferencesKeysAreInPhaseWithItsPositions(string fixture)
    {
        var curve = Sweep(fixture);
        var best = curve.OrderBy(e => e.Value).ThenBy(e => Math.Abs(e.Key)).First().Key;

        Assert.True(
            best == 0,
            $"the recorded keys explain the motion best at lag {best}, not 0 — "
            + $"the stream is mis-phased, or the port's physics is. [{Describe(curve)}]");
    }

    /// <summary>
    /// ⛔ The vacuity guard: being a tick out has to COST something, or the test
    /// above proves nothing.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>This is the instrument's own calibration, and it is what a
    /// mutation check would otherwise have to supply.</b> Shifting the inputs by
    /// one tick is exactly the defect <c>486ef3e</c> put on the wire, so a
    /// recording where <c>+1</c> is no worse than <c>0</c> is a recording this
    /// instrument could not have caught it in. Asserting the gap is real turns
    /// "argmin is 0" from a coincidence into a measurement.
    /// </remarks>
    [Theory]
    [InlineData("reference-ctf_truefort-closedcombat")]
    [InlineData("reference-gg_debug-closedtdm")]
    public void TheSweepIsNotFlatSoAMisPhasedStreamWouldShow(string fixture)
    {
        var curve = Sweep(fixture);

        Assert.True(
            curve[1] > curve[0],
            $"a +1 tick lag cost nothing, so this fixture cannot detect a late keystate [{Describe(curve)}]");

        Assert.True(
            curve[-1] > curve[0],
            $"a -1 tick lag cost nothing, so this fixture cannot detect an early keystate [{Describe(curve)}]");
    }

    /// <summary>
    /// ⚠️ The curve is monotone away from zero, which is what makes the argmin
    /// meaningful rather than a local dip.
    /// </summary>
    /// <remarks>
    /// A two-tick shift must cost at least as much as a one-tick shift. If it did
    /// not, the minimum at zero would be an artefact of the window length rather
    /// than evidence about phase, and reading the argmin would be reading noise.
    /// </remarks>
    [Theory]
    [InlineData("reference-ctf_truefort-closedcombat")]
    [InlineData("reference-gg_debug-closedtdm")]
    public void ErrorGrowsWithTheSizeOfTheShift(string fixture)
    {
        var curve = Sweep(fixture);

        Assert.True(curve[2] >= curve[1], $"lag +2 beat +1 [{Describe(curve)}]");
        Assert.True(curve[-2] >= curve[-1], $"lag -2 beat -1 [{Describe(curve)}]");
    }
}
