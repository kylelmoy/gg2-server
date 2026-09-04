using System.Linq;
using Gg2.Maps;
using Gg2.Parity;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// The first fixture in the project recorded on a map other than
/// <c>ctf_truefort</c> — it found a divergence in its first five ticks, and
/// fixing that divergence is what this file now gates.
/// </summary>
/// <remarks>
/// <b>Three scouts against three soldiers on
/// <c>ctf_2dfort</c></b>, the narrowest CTF map (572px of art against
/// truefort's 891), chosen so the bots meet early enough for a death and its
/// 150-tick respawn to fit inside a closed-loop window.
/// <para>
/// The window was originally <b>five ticks</b>, because every character
/// landed 0.4 world pixels low on its very first fall — a harness bug, not a
/// simulation one. Fixed by spawning through the alarm stage
/// (<see cref="ITraceSimulation.QueueSpawn"/>); the window is <b>362 ticks</b>.
/// <see cref="TheWindowSurvivesTheSpawnStageFix"/> is the gate.
/// </para>
/// <para>
/// The recording holds eight deaths, sixteen spawns, a <c>GRAB_INTEL</c> and the
/// <c>SCORE_INTEL</c> that follows it — the first capture-with-its-grab in any
/// fixture here. <b>The capture is still out of reach</b>: the grab is at
/// t1404 and the window ends at t877, so what blocks it now is ordinary drift
/// rather than a landing bug.
/// </para>
/// </remarks>
public sealed class SecondMapFixtureTests
{
    private const string Fixture = "reference-ctf_2dfort-closedrespawn.trace";

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

    /// <summary>How many <em>ticks</em> carry at least one message of this type.</summary>
    private static int Count(IReadOnlyList<TraceTick> ticks, MessageType type) =>
        ticks.Count(t => t.Events.Any(m => m.Type == type));

    /// <summary>
    /// How many <em>messages</em> of this type the recording holds.
    /// </summary>
    /// <remarks>
    /// Not the same as <see cref="Count"/>, and the spawn wave is exactly
    /// where the difference bites: six characters spawn on one tick, so the
    /// tick count says 1 where the message count says 6. <c>gg2-parity census</c>
    /// prints the message count, so use this one when comparing against it.
    /// </remarks>
    private static int CountMessages(IReadOnlyList<TraceTick> ticks, MessageType type) =>
        ticks.Sum(t => t.Events.Count(m => m.Type == type));

    private static (int Exact, int Anchor) ExactWindow(Fields compare)
    {
        var (trace, ticks) = Load();

        var anchor = FixtureAnchors.LastExactAnchor(ticks);
        Assert.True(anchor is not null, $"{Fixture}: some slot holds a character it never spawned into");

        var report = new Replayer(new PortedSimulation(MapLoader)) { Compare = compare }
            .Replay(Fixture, trace.Header.Map, ticks, ReplayMode.ClosedLoop, stopAtFirstBadTick: false);

        var after = report.Mismatches.Where(m => m.Tick > anchor!.Value).ToArray();
        var firstBad = after.Length == 0 ? ticks[^1].Index : after.Min(m => m.Tick);

        return (firstBad - anchor.Value, anchor.Value);
    }

    /// <summary>
    /// It really is on a different map, and it frames.
    /// </summary>
    /// <remarks>
    /// <b>Every other fixture in this project is <c>ctf_truefort</c></b>, so
    /// nothing had ever loaded a second map's terrain through the replay path —
    /// the trace header, <c>GameModes.ShapeOf</c> and <c>PortedSimulation.LoadMap</c>
    /// all had exactly one map's worth of evidence behind them.
    /// </remarks>
    [Fact]
    public void ItIsRecordedOnASecondMapAndFramesCleanly()
    {
        var (trace, ticks) = Load();

        Assert.Equal("ctf_2dfort", trace.Header.Map);
        Assert.True(ticks.Count > 2400, $"only {ticks.Count} ticks");
    }

    /// <summary>
    /// It holds the things it was recorded for, including the first capture in
    /// any fixture here.
    /// </summary>
    [Fact]
    public void TheRecordingHoldsDeathsRespawnsAndACaptureWithItsGrab()
    {
        var (_, ticks) = Load();

        Assert.True(CountMessages(ticks, MessageType.PlayerDeath) >= 8, "not enough deaths");
        Assert.True(CountMessages(ticks, MessageType.PlayerSpawn) >= 14, "not enough spawns");
        Assert.True(Count(ticks, MessageType.WeaponFire) >= 50, "not enough seeded fire");

        // The grab and the capture that follows it, in one recording. Nothing
        // else in the project has a SCORE_INTEL at all, so `doEventScoreIntel`
        // has never been gated end to end — and still is not, because the window
        // below ends 900 ticks before the grab.
        Assert.True(Count(ticks, MessageType.GrabIntel) >= 1, "no grab");
        Assert.True(Count(ticks, MessageType.ScoreIntel) >= 1, "no capture");
    }

    /// <summary>It is closed-loop, so the window below means something.</summary>
    [Fact]
    public void EveryCharacterIsSpawnAnchored()
    {
        var (_, ticks) = Load();
        Assert.True(FixtureAnchors.LastExactAnchor(ticks) is not null, $"{Fixture}: not closed-loop");
    }

    /// <summary>
    /// <b>The gate on the landing fix: this map's window is hundreds of ticks,
    /// not five.</b>
    /// </summary>
    /// <remarks>
    /// <c>ctf_2dfort</c>'s spawn points are at <c>y = 894</c> and the floor
    /// beneath them stops a scout at <c>y = 900</c>, so every character falls
    /// six pixels the moment it spawns. The reference lands on exactly 900. The
    /// port lands on <b>900.4</b> — all six slots, on the same tick, five ticks
    /// after the spawn wave, and it stays there.
    /// <para>
    /// <b>The map layer is not at fault</b>, which
    /// <see cref="ThePortsCollisionAgreesWithTheEngineOnThisMap"/> establishes:
    /// the port's <c>PlaceFree</c> gives free at 900 and blocked at 901 for a
    /// scout box at x = 402, which is exactly what the engine answered when
    /// asked the same column. <c>maps.json</c> already gates this map's mask
    /// cell-for-cell, and the map places no gates, no player walls and no
    /// dropdown platforms — one <c>Obstacle</c> and nothing else.
    /// </para>
    /// <para>
    /// It is the landing: on <c>ctf_truefort</c> the spawn wave produces no such
    /// offset, because its spawn markers sit eighteen pixels above the floor,
    /// outside the snap described below.
    /// </para>
    /// <para>
    /// <b>Root cause: the harness, not the simulation.</b> The reference creates
    /// a character in the <b>alarm stage</b> (<c>player.alarm[5]</c> →
    /// <c>PlayerSpawn.gml</c>), which runs between Begin Step and Step — so on
    /// its first tick a character gets a Step and an End Step but <b>no Begin
    /// Step</b>, and Begin Step is where gravity is applied. Its first End Step
    /// therefore still sees <c>vspeed == 0</c>, the guard on the stair descent,
    /// and it <b>snaps</b> the six pixels onto the floor. The replayer had
    /// placed the character before running the tick, so it took that tick's
    /// gravity and fell instead.
    /// </para>
    /// <para>
    /// Fixed by <see cref="ITraceSimulation.QueueSpawn"/>, which arms the alarm
    /// so the tick itself builds the character — <b>a tick early</b>, because a
    /// <c>PLAYER_SPAWN</c> in tick T's block describes tick T-1, exactly as a
    /// fire seed and a death do.
    /// </para>
    /// <para>
    /// This test is the gate: <b>5 ticks before the fix, 362 after</b>. Removing
    /// the queueing puts it straight back to 5, which is a difference no
    /// tolerance can hide.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheWindowSurvivesTheSpawnStageFix()
    {
        var (exact, anchor) = ExactWindow(Fields.Trajectory);

        // 362 today, t515 to t877. The floor is well above the five ticks this
        // fixture managed while the replayer pre-placed its characters.
        Assert.True(
            exact >= 300,
            $"{Fixture}: exact for only {exact} ticks after t{anchor} — if this has collapsed "
                + "towards five, the alarm-stage spawn seam has stopped working");
    }

    /// <summary>
    /// <b>The capture is still out of reach, but not because of the landing
    /// bug.</b>
    /// </summary>
    /// <remarks>
    /// The recording's <c>GRAB_INTEL</c> is at t1404 and its <c>SCORE_INTEL</c>
    /// at t2299, both far past the window's end. What separates them from it now
    /// is ordinary accumulated drift, not the landing — so a recording that
    /// reaches a capture inside a window needs the objective to happen
    /// <em>early</em>, not a physics fix. Asserted so that a future improvement
    /// says so loudly.
    /// </remarks>
    [Fact]
    public void TheGrabAndCaptureStillFallOutsideTheWindow()
    {
        var (_, ticks) = Load();
        var (exact, anchor) = ExactWindow(Fields.Trajectory);

        var grab = ticks.First(t => t.Events.Any(m => m.Type == MessageType.GrabIntel)).Index;

        Assert.True(
            grab > anchor + exact,
            $"the grab at t{grab} is now inside the window (t{anchor}..t{anchor + exact}) — "
                + "this fixture can gate doEventGrabIntel and doEventScoreIntel; replace this test");
    }

    /// <summary>
    /// The control that puts the blame on the physics rather than the map.
    /// </summary>
    /// <remarks>
    /// Measured against the running engine with a live scout's own
    /// box at the red spawn column: <c>place_free</c> is true for y ≤ 900 and
    /// false for y ≥ 901. This is the same question asked of the port, and it is
    /// a fifth map for the collision layer — <c>collision-samples.json</c>
    /// covers <c>ctf_truefort</c>, <c>koth_valley</c>, <c>gg_debug</c> and
    /// <c>arena_lumberyard</c>, and never this one.
    /// </remarks>
    [Fact]
    public void ThePortsCollisionAgreesWithTheEngineOnThisMap()
    {
        var world = new CollisionWorld(MapLoader("ctf_2dfort"));
        var box = CharacterBox.For(PlayerClass.Scout);
        var filter = CollisionFilter.Everything;

        for (var y = 894; y <= 900; y++)
            Assert.True(world.PlaceFree(box, 402, y, filter), $"y={y} should be free");

        for (var y = 901; y <= 906; y++)
            Assert.False(world.PlaceFree(box, 402, y, filter), $"y={y} should be blocked");
    }
}
