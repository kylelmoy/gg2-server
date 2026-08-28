using Gg2.Maps;
using Gg2.Parity;
using Gg2.Protocol;

namespace Gg2.Tests;

/// <summary>
/// <b>The gate on what a player actually sees: how far the stock client's
/// prediction has drifted by the moment a correction lands.</b>
/// </summary>
/// <remarks>
/// ⚠️ <b>This is the failure mode CLAUDE.md calls invisible</b> — "there is no
/// test that fails when trajectories drift — the client just snaps". It is not
/// quite invisible, and the correction matters: <c>ReplayMode.OpenLoop</c> has
/// existed since phase 2 and <c>docs/PARITY.md</c> chose to report it "as a
/// distribution rather than pass/fail". What was missing was not the instrument
/// but the pin.
/// <para>
/// ✅ <b>Open-loop IS the client's view, exactly.</b> <c>Replayer.Anchor</c>
/// resets each slot from the trace's <em>observed</em> block — the quantised
/// wire values, position to 1/5 px and velocity to 1/8.5 px/tick — and then
/// simulates forward to the next position-bearing tick with the recorded
/// inputs. That is precisely what <c>Character.events/User Event 13.xml</c> does
/// to a client: hard-assign <c>x</c>, <c>y</c>, <c>hspeed</c>, <c>vspeed</c>,
/// then dead-reckon for seven ticks. So the number here is the size of the snap.
/// </para>
/// <para>
/// ⚠️ <b>Why this is not redundant with the closed-loop windows.</b> Closed-loop
/// asks "does the port match the reference tick for tick", and a fixture's exact
/// window answers with a single number that says nothing about how bad things
/// are just past its edge. This asks "how wrong is the client allowed to be",
/// which is the quantity a player perceives, and it keeps measuring for the whole
/// recording rather than stopping at the first disagreement.
/// </para>
/// <para>
/// ⚠️ <b>p99, not max, is the statistic — and that is forced, not preferred.</b>
/// A death teleports a character to a spawn point, so the window containing a
/// respawn shows an "error" of most of the map. Those are real events, not
/// drift, and they are why the combat fixtures below have maxima in the hundreds
/// while their p99 sits at 2. On the single-player fixtures nobody dies and the
/// maximum is meaningful, so those are pinned on both.
/// </para>
/// </remarks>
public class ClientPredictionGateTests
{
    private static LevelMap MapLoader(string name) =>
        LevelMap.FromPng(name, ReferenceTree.ReadMap(name));

    private static DriftReport OpenLoop(string fixture)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture + ".trace");
        Assert.True(File.Exists(path), $"missing {path}");

        var trace = Trace.Decode(File.ReadAllBytes(path));
        var ticks = Parse(trace);

        return new Replayer(new PortedSimulation(MapLoader))
        {
            Compare = Fields.Trajectory,
        }.Replay(fixture, trace.Header.Map, ticks, ReplayMode.OpenLoop, stopAtFirstBadTick: false);
    }

    /// <summary>
    /// Frames a trace, and refuses one that does not frame to its last byte.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The three-argument <c>Parse</c> discards the "stopped" flag</b>, and
    /// a pin measured on a half-framed recording is a pin on an arbitrary prefix:
    /// it would hold, and it would mean nothing. Every number in this file is
    /// only worth having if the whole recording was read.
    /// </remarks>
    private static IReadOnlyList<TraceTick> Parse(Trace trace)
    {
        var map = MapLoader(trace.Header.Map);

        var ticks = TraceParser.Parse(trace, GameModes.ShapeOf(map), out _, out var stopped);

        if (stopped is null)
            return ticks;

        // ⚠️ A multi-area map writes every area's control points into its file
        // and plays one area's worth — cp_dirtbowl writes six and plays two — so
        // the un-narrowed shape expects a wider HUD block than the wire carries
        // and loses the stream almost immediately. `CpDirtbowlFixtureTests`
        // pins that as its own control; the narrowed shape is what its loader,
        // and `MapRotationFixtureTests`, both use.
        //
        // The fallback is safe rather than papering over a regression: if the
        // narrowed shape does not frame either, this still fails — and if a
        // change moved a fixture from one branch to the other, its pinned
        // numbers below would move with it and fail there.
        ticks = TraceParser.Parse(trace, GameModes.ShapeOf(map.ForArea(1)), out _, out var narrowed);

        Assert.True(
            narrowed is null,
            $"{trace.Header.Map}: framing stopped early on both the whole-map shape ({stopped}) "
                + $"and the area-1 shape ({narrowed})");

        return ticks;
    }

    /// <summary>
    /// ✅ <b>The recordings with one character in them, pinned exactly on both
    /// statistics.</b>
    /// </summary>
    /// <remarks>
    /// <b>These are the sharp instrument.</b> Nobody fires, nobody dies and
    /// nobody else is on the map, so every wire unit of error is prediction
    /// error and nothing else — no respawn teleport, no combat knockback, no
    /// second character to be wrong about. <b>Four of the five hold the client to
    /// two wire units, which is 0.4 px.</b>
    /// <para>
    /// ⚠️ <b>The exception is the human take's 270 / 44 in y, and it is a known
    /// bug rather than noise</b> — the drop-through platform of
    /// <c>Phase2FixtureTests.ThePortPutsAPlayerBackOnAPlatformTheyDroppedThrough</c>.
    /// 270 wire units is 54 px: the port holds the player up on a platform the
    /// reference drops them through, so the client is corrected by most of a
    /// screen height. <b>Fixing that item will move this row</b>, and it should
    /// be re-pinned then, downwards.
    /// </para>
    /// <para>
    /// ⚠️ <b>Exact equality, not an upper bound, and deliberately so.</b> This
    /// project has been bitten by bounds that decayed into formalities — see
    /// <c>docs/TRAPS-SIMULATION.md</c>, "a bound nobody re-measures decays into a
    /// formality", where a 25-unit gate outlived a port five times more accurate
    /// than when it was written. An exact pin fails in <b>both</b> directions, so
    /// an improvement has to be noticed and recorded rather than quietly banked.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("reference-gg_debug-scriptedwalk", 1, 1, 0, 0)]
    [InlineData("reference-gg_debug-scriptedjumps", 0, 0, 2, 2)]
    [InlineData("reference-gg_debug-scriptedstairs", 2, 2, 0, 0)]
    [InlineData("reference-tdm_mantic-closedtdm", 0, 0, 0, 0)]
    [InlineData("reference-tdm_mantic-humanmoves", 2, 2, 270, 44)]
    public void ASinglePlayerRecordingPinsTheSnapExactly(
        string fixture,
        long maxX,
        long p99X,
        long maxY,
        long p99Y)
    {
        var report = OpenLoop(fixture);

        Assert.True(report.Comparisons > 50, $"{fixture}: only {report.Comparisons} comparisons");

        Assert.Equal(maxX, report.MaxXError);
        Assert.Equal(p99X, report.PercentileXError(99));
        Assert.Equal(maxY, report.MaxYError);
        Assert.Equal(p99Y, report.PercentileYError(99));
    }

    /// <summary>
    /// Every other fixture, pinned on the 99th percentile — the statistic a
    /// respawn cannot forge.
    /// </summary>
    /// <remarks>
    /// <b>Measured 2026-08-28 across the whole fixture set.</b> Most sit at 2
    /// wire units, under half a pixel, with two classes and both generator maps
    /// noticeably worse. <b>The rows worth looking at some day</b>, recorded here
    /// because nothing else in the suite would notice them: the heavy at 49 in x,
    /// the sniper at 25/20, <c>gg_debug-closedtdm</c> at 77 in y, and
    /// <c>gen_destroy-closedgen</c> at 18/30.
    /// <para>
    /// ⚠️ <b>A high row here is not automatically a bug.</b> These fixtures have
    /// several characters in them and open-loop compares every slot: a knockback,
    /// an airblast or a sentry shove all move somebody in a way seven ticks of
    /// dead reckoning cannot predict, and the reference's own client would be
    /// corrected too. What the pin gives is a <b>baseline that moves when the
    /// physics moves</b>, which is the thing that did not exist.
    /// </para>
    /// <para>
    /// ⚠️ <b>One fixture of the 36 is deliberately absent:
    /// <c>gen_destroy-rotationcaps</c>.</b> It exists to be the recording a
    /// single shape <em>cannot</em> frame — it rotates to a different gamemode
    /// and then forces a <c>CAPS_UPDATE</c>, the one tier that carries the HUD
    /// block, so a reader holding the old shape loses the stream 64% of the way
    /// in (<c>ModeChangeFramingTests</c>). Framing it needs re-shaping mid-trace,
    /// which this gate's loader does not do, and a pin measured on 64% of a
    /// recording is a pin on an arbitrary prefix.
    /// </para>
    /// <para>
    /// ⚠️ <b>Both of those were found by the framing assertion, not by
    /// inspection</b> — and the first draft of this file pinned numbers taken
    /// from <b>458 bytes of cp_dirtbowl's 138,406</b>, which would have passed
    /// forever and meant nothing. A distribution is only worth pinning if the
    /// whole recording was read.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("reference-arena_lumberyard-closedarena", 2, 2)]
    [InlineData("reference-arena_lumberyard-waitforplayers", 9, 2)]
    [InlineData("reference-cp_dirtbowl-closedcp", 2, 2)]
    [InlineData("reference-cp_egypt-closedsymmetric", 6, 2)]
    [InlineData("reference-cp_egypt-uncontestedcp", 5, 1)]
    [InlineData("reference-ctf_2dfort-autobalance", 10, 3)]
    [InlineData("reference-ctf_2dfort-closedrespawn", 2, 2)]
    [InlineData("reference-ctf_2dfort-dropintel", 2, 2)]
    [InlineData("reference-ctf_2dfort-uncontestedcap", 2, 2)]
    [InlineData("reference-ctf_avanti-closedinvasion", 2, 2)]
    [InlineData("reference-ctf_truefort-bots", 2, 3)]
    [InlineData("reference-ctf_truefort-closedairblast", 9, 2)]
    [InlineData("reference-ctf_truefort-closedcombat", 2, 2)]
    [InlineData("reference-ctf_truefort-closedheavy", 49, 3)]
    [InlineData("reference-ctf_truefort-closedquote", 8, 2)]
    [InlineData("reference-ctf_truefort-closedsentrykill", 2, 2)]
    [InlineData("reference-ctf_truefort-closedsniper", 25, 20)]
    [InlineData("reference-ctf_truefort-closedspy", 2, 2)]
    [InlineData("reference-ctf_truefort-closedsupport", 9, 2)]
    [InlineData("reference-ctf_truefort-combat", 2, 2)]
    [InlineData("reference-ctf_truefort-hotcombat", 2, 2)]
    [InlineData("reference-ctf_truefort-hotsupport", 9, 9)]
    [InlineData("reference-ctf_truefort-spawnroomdeath", 2, 2)]
    [InlineData("reference-dkoth_atalia-closeddkoth", 2, 2)]
    [InlineData("reference-gen_destroy-closedgen", 18, 30)]
    [InlineData("reference-gen_destroy-rotation", 7, 10)]
    [InlineData("reference-gg_debug-closedtdm", 2, 77)]
    [InlineData("reference-koth_valley-closedkoth", 5, 2)]
    [InlineData("reference-koth_valley-uncontested", 2, 2)]
    public void EveryOtherFixturePinsItsNinetyNinthPercentile(string fixture, long p99X, long p99Y)
    {
        var report = OpenLoop(fixture);

        Assert.True(report.Comparisons > 50, $"{fixture}: only {report.Comparisons} comparisons");

        Assert.Equal(p99X, report.PercentileXError(99));
        Assert.Equal(p99Y, report.PercentileYError(99));
    }

    /// <summary>
    /// ⚠️ <b>The gate has to be able to fail, and this is the proof it can.</b>
    /// </summary>
    /// <remarks>
    /// A pin nobody has watched move is indistinguishable from a pin on a
    /// quantity the harness is not really measuring — the same reason
    /// <c>TraceTests.TheEmptySimulationStillFailsTheSameTrace</c> exists. A
    /// simulation that does nothing at all must score far worse than the pinned
    /// 1 wire unit, and it does: the character walks the length of
    /// <c>gg_debug</c> and a still simulation is left behind by all of it.
    /// </remarks>
    [Fact]
    public void AStillSimulationScoresFarWorseThanThePin()
    {
        const string fixture = "reference-gg_debug-scriptedwalk";

        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture + ".trace");
        var trace = Trace.Decode(File.ReadAllBytes(path));
        var ticks = Parse(trace);

        var still = new Replayer(new EmptySimulation()) { Compare = Fields.Trajectory }
            .Replay(fixture, trace.Header.Map, ticks, ReplayMode.OpenLoop, stopAtFirstBadTick: false);

        Assert.Equal(1, OpenLoop(fixture).PercentileXError(99));
        Assert.True(
            still.PercentileXError(99) > 10,
            $"a simulation that does nothing scored p99 |dx| {still.PercentileXError(99)}, "
                + "which is close enough to the real one that this gate is measuring nothing");
    }

    /// <summary>
    /// ⚠️ <b>Measured, and worth knowing before choosing a fixture to test a
    /// physics change against: a steady walk cannot see an input-phase error.</b>
    /// </summary>
    /// <remarks>
    /// Pairing each update with the wrong tick is the mutation
    /// <c>PhysicsParityTests.PairingInputsWithTheWrongTickIsMeasurablyWorse</c>
    /// detects on the bot trace. On <c>scriptedwalk</c> it changes the p99
    /// <b>not at all</b> — both score 1 — because the character holds one key at
    /// a constant speed, and a constant input is the same input a tick early.
    /// <para>
    /// <b>So the single-player fixtures are the sharpest instrument for
    /// trajectory error and the bluntest for input timing</b>, and that is not a
    /// defect in them. It is the reason the bot and combat traces stay in the
    /// suite, and the reason this is asserted rather than left as a surprise for
    /// whoever next reaches for the cleanest fixture to test a change against.
    /// </para>
    /// </remarks>
    [Fact]
    public void ASteadyWalkIsBlindToInputPhaseAndThatIsWhyTheBotTraceStays()
    {
        const string fixture = "reference-gg_debug-scriptedwalk";

        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture + ".trace");
        var trace = Trace.Decode(File.ReadAllBytes(path));
        var ticks = Parse(trace);

        var wrong = new Replayer(new PortedSimulation(MapLoader))
        {
            Compare = Fields.Trajectory,
            UpdatesPrecedeTheirTick = false,
        }.Replay(fixture, trace.Header.Map, ticks, ReplayMode.OpenLoop, stopAtFirstBadTick: false);

        Assert.Equal(1, wrong.PercentileXError(99));
        Assert.Equal(1, OpenLoop(fixture).PercentileXError(99));
    }
}
