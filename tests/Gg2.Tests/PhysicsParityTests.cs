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
    /// The first tick of this trace on which any character has taken damage.
    /// </summary>
    /// <remarks>
    /// Phase 4 is trajectories, and it implements no combat at all — so this is
    /// the tick from which the recording stops being a statement about the
    /// physics and starts being one about weapons. Derived from the fixture
    /// rather than hard-coded, so a re-recording moves it without a mystery.
    /// </remarks>
    private static int FirstDamagedTick(IReadOnlyList<TraceTick> ticks)
    {
        foreach (var tick in ticks)
        {
            foreach (var slot in tick.Slots)
            {
                // Zero is a dead character's block, not a damaged one.
                if (slot.Observed is { } observed && observed.Hp is not 100 and not 0)
                    return tick.Index;
            }
        }

        return int.MaxValue;
    }

    /// <summary>
    /// The ported physics reproduces the reference <b>exactly</b>, closed-loop,
    /// for every tick up to the moment combat first touches a character.
    /// </summary>
    /// <remarks>
    /// Closed-loop: anchored once, then never told the answer again, so errors
    /// compound instead of being wiped every seventh tick. Both axes are zero —
    /// not "within a wire unit", zero — across 393 ticks of two bots running,
    /// jumping, falling and landing.
    /// <para>
    /// <b>This used to be within one wire unit of <c>y</c>, and the unit was the
    /// anchor.</b> A replay used to start every character from a broadcast
    /// position, which carries a fifth of a pixel of position and <c>1/8.5</c>
    /// px/tick of velocity, so it began somewhere inside a bin rather than on the
    /// state the reference was in. Resolving <c>PLAYER_SPAWN</c> against the
    /// map's own spawn points instead (<see cref="SpawnPoints"/>) starts them on
    /// an integer coordinate with both speeds zero — a state that passed through
    /// no wire — and the residual disappeared. docs/PLAN.md phase 4 has the
    /// history.
    /// </para>
    /// <para>
    /// The divergence that remains is <b>not</b> physics. It begins on the exact
    /// tick the first character's <c>hp</c> drops below full, and it arrives as
    /// hp and hspeed together: damage and its knockback, which phase 5
    /// implements and this one does not.
    /// </para>
    /// </remarks>
    [Fact]
    public void ClosedLoopIsExactUntilCombatStarts()
    {
        var (trace, ticks) = Load();
        var damaged = FirstDamagedTick(ticks);

        Assert.True(damaged < ticks.Count, "the fixture has no combat in it at all");

        var report = Replay(ticks, trace.Header.Map, ReplayMode.ClosedLoop, stopAtFirstBadTick: false);

        Assert.True(report.Comparisons > 100, $"only {report.Comparisons} comparisons");

        // Every mismatch is at or after the first damage. Nothing before it.
        foreach (var mismatch in report.Mismatches)
        {
            Assert.True(mismatch.Tick >= damaged,
                $"a mismatch at tick {mismatch.Tick} predates the first damage at {damaged}: " +
                $"slot {mismatch.Slot} {mismatch.Field} expected {mismatch.Expected}, got {mismatch.Actual}");
        }

        // And the run really did get that far before anything went wrong.
        Assert.True(damaged > 380, $"combat started at tick {damaged}, earlier than expected");
    }

    /// <summary>
    /// Stopping at the first bad tick, the replay reaches the first damaged tick
    /// with both axes exactly zero.
    /// </summary>
    /// <remarks>
    /// The same claim as above stated as the gate rather than as a survey, and
    /// the one that would catch a regression soonest: any arithmetic error at
    /// all shows up here as a non-zero max, because a closed loop never gets
    /// corrected.
    /// </remarks>
    [Fact]
    public void ClosedLoopReachesCombatWithNoPositionErrorOnEitherAxis()
    {
        var (trace, ticks) = Load();
        var damaged = FirstDamagedTick(ticks);

        // Truncated at the first damage, so the gate is a plain "zero on both
        // axes" over everything phase 4 claims to reproduce. Replaying past it
        // would fold the knockback this phase does not implement into the same
        // number and there would be nothing to assert.
        var beforeCombat = ticks.Take(damaged).ToList();

        var report = Replay(beforeCombat, trace.Header.Map, ReplayMode.ClosedLoop, stopAtFirstBadTick: false);

        Assert.True(report.Comparisons > 100, $"only {report.Comparisons} comparisons");
        Assert.Empty(report.Mismatches);
        Assert.Equal(0, report.MaxXError);
        Assert.Equal(0, report.MaxYError);
    }

    /// <summary>
    /// The anchor is on the reference's own value, not merely inside the right
    /// quantisation bin — so moving it can only make things worse.
    /// </summary>
    /// <remarks>
    /// <b>This test asserts the inverse of what it used to.</b> While a replay
    /// anchored from the wire, the honest question was "is the residual the
    /// arithmetic or the anchor?", and the answer was found by sweeping each
    /// slot's anchor across its own bin to see the divergence move later. There
    /// is no bin left to sweep: a spawn resolves to an integer coordinate with
    /// both speeds zero.
    /// <para>
    /// So the experiment inverts. Nudging now starts a character somewhere the
    /// reference never was, and every offset must be strictly worse than none.
    /// A nudge that <em>helped</em> would mean the spawn lookup is putting
    /// characters in the wrong place and something else is absorbing the error.
    /// </para>
    /// </remarks>
    [Fact]
    public void NudgingTheAnchorOffTheSpawnPointOnlyMakesItWorse()
    {
        var (trace, ticks) = Load();
        var baseline = Replay(ticks, trace.Header.Map, ReplayMode.ClosedLoop);

        // Well inside what used to be a wire bin, so this is not a test that a
        // large displacement breaks things — a fiftieth of a pixel is far below
        // the resolution the wire could even express.
        foreach (var offset in new[] { -0.1, -0.02, 0.02, 0.1 })
        {
            var nudged = Run(ticks, trace.Header.Map, new Dictionary<int, AnchorNudge>
            {
                [2] = new AnchorNudge(0, offset, 0, 0),
            });

            Assert.True(nudged.TicksReplayed <= baseline.TicksReplayed,
                $"nudging slot 2 by {offset} reached tick {nudged.TicksReplayed}, " +
                $"further than the un-nudged {baseline.TicksReplayed} — the spawn anchor is not on the true value");
        }
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

    /// <summary>
    /// A <c>PLAYER_SPAWN</c> anchors on the map's own spawn point, exactly,
    /// rather than on the next broadcast position.
    /// </summary>
    /// <remarks>
    /// This is the change that makes a closed-loop gate of zero reachable, and
    /// the assertion is deliberately about <em>exactness</em> rather than
    /// closeness. The wire the test feeds in says the character is somewhere
    /// else entirely; the replay must ignore that and take the spawn point,
    /// because that is what the reference did and what every client does
    /// (<c>doEventSpawn.gml</c>).
    /// <para>
    /// Note the ordering it also pins: the spawn is applied before the tick's
    /// own comparison, so the character exists to be compared from its first
    /// tick instead of being resynchronised into place.
    /// </para>
    /// </remarks>
    [Fact]
    public void ASpawnAnchorsOnTheSpawnPointAndNotOnTheWire()
    {
        const int Slot = 0;

        var simulation = new PortedSimulation(MapLoader);
        simulation.LoadMap("ctf_truefort");
        simulation.SetPlayerTeam(Slot, Team.Red);
        simulation.SetPlayerClass(Slot, PlayerClass.Scout);

        // Spawn point 2 of red's group 0. Deliberately not the first, so an
        // implementation that ignores the id and takes the head of the list
        // fails here.
        simulation.SpawnCharacter(Slot, 2, 0);

        Assert.True(simulation.TryGetState(Slot, out var state));

        var map = LevelMap.FromPng("ctf_truefort", ReferenceTree.ReadMap("ctf_truefort"));
        var expected = new SpawnPoints(map);
        Assert.True(expected.TryGet(Team.Red, 0, 2, out var x, out var y));

        // Exact, not near: these came from the map, not through the wire's
        // fifths of a pixel.
        Assert.Equal(x, state.X);
        Assert.Equal(y, state.Y);
        Assert.Equal(0, state.HSpeed);
        Assert.Equal(0, state.VSpeed);

        // 1656, from the entity block — the value a wire anchor could only have
        // reached to within a fifth of a pixel.
        Assert.Equal(1656, state.X);
        Assert.Equal(456, state.Y);
    }

    /// <summary>
    /// An unresolvable spawn leaves the replay on its old behaviour rather than
    /// placing a character somewhere wrong.
    /// </summary>
    /// <remarks>
    /// Silently falling back is right here: a trace can name a spawn group a
    /// map does not have, and anchoring on the next observed position is what
    /// the harness did for all of phase 2. Placing a character at (0, 0)
    /// instead would report a wall of mismatches that say nothing.
    /// </remarks>
    [Fact]
    public void AnUnresolvableSpawnPlacesNothing()
    {
        var simulation = new PortedSimulation(MapLoader);
        simulation.LoadMap("ctf_truefort");
        simulation.SetPlayerTeam(0, Team.Red);

        Assert.False(simulation.SpawnCharacter(0, 99, 0));
        Assert.False(simulation.TryGetCharacter(0, out _));

        Assert.False(simulation.SpawnCharacter(0, 0, 4));
        Assert.False(simulation.TryGetCharacter(0, out _));
    }

    /// <summary>
    /// A spawned character starts on its class's full health, as
    /// <c>Character.events/Create.xml:37</c> does it.
    /// </summary>
    [Fact]
    public void ASpawnedCharacterStartsOnFullHealth()
    {
        var simulation = new PortedSimulation(MapLoader);
        simulation.LoadMap("ctf_truefort");
        simulation.SetPlayerTeam(0, Team.Red);
        simulation.SetPlayerClass(0, PlayerClass.Heavy);

        Assert.True(simulation.SpawnCharacter(0, 0, 0));
        Assert.True(simulation.TryGetCharacter(0, out var block));
        Assert.Equal(200, block.Hp);
    }
}
