using System.Linq;
using Gg2.Maps;
using Gg2.Parity;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// The first Team Deathmatch recordings in the project, on the two maps that
/// play it.
/// </summary>
/// <remarks>
/// <b>Recorded 2026-08-27</b>, six soldiers, both spawn-anchored by the
/// map-reload recipe. Until then every fixture here was Capture the Flag, so
/// <c>GameModes.ShapeOf</c> had never produced a Team Deathmatch shape for a
/// real reader and the mode's wire had no evidence behind it at all.
/// <para>
/// ✅ <b>Both frame cleanly to the last byte</b>, and <c>gg2-parity census</c>
/// reports <c>TeamDeathmatch</c> for each. That negative is the first thing
/// these recordings buy — the Arena <c>ARENA_ENDROUND</c> bug and the
/// engineer's <c>INPUTSTATE</c> bug were both of this shape and both survived
/// because no recording reached the state.
/// </para>
/// <para>
/// ⚠️ <b>They disagree wildly about everything else, and that is the finding.</b>
/// <c>tdm_mantic</c> replays exactly for <b>3,043</b> ticks — the longest
/// closed-loop window in the project — and <c>gg_debug</c> for <b>142</b>. That
/// second number has been 2, 23, 100, 135 and now 142 in a single day, and each
/// step was a different bug: the alarm-stage spawn seam, dropdown platforms, the
/// rotated projectile mask, Team Deathmatch's spawn invulnerability, and walking
/// characters in creation order rather than slot order. See
/// <see cref="GgDebugReplaysPastItsDropdownPlatformsAndStopsOnDamage"/>.
/// </para>
/// </remarks>
public sealed class TeamDeathmatchFixtureTests
{
    private const string Mantic = "reference-tdm_mantic-closedtdm.trace";
    private const string Debug = "reference-gg_debug-closedtdm.trace";

    private static LevelMap MapLoader(string name) =>
        LevelMap.FromPng(name, ReferenceTree.ReadMap(name));

    private static (Trace Trace, IReadOnlyList<TraceTick> Ticks) Load(string fixture)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture);
        Assert.True(File.Exists(path), $"missing {path}");

        var trace = Trace.Decode(File.ReadAllBytes(path));
        var map = MapLoader(trace.Header.Map);
        var ticks = TraceParser.Parse(trace, GameModes.ShapeOf(map), out _, out var stopped);

        Assert.True(stopped is null, $"{fixture}: {stopped}");
        return (trace, ticks);
    }

    private static int CountMessages(IReadOnlyList<TraceTick> ticks, MessageType type) =>
        ticks.Sum(t => t.Events.Count(m => m.Type == type));

    private static (int Exact, int Anchor) ExactWindow(string fixture, Fields compare)
    {
        var (trace, ticks) = Load(fixture);

        var anchor = FixtureAnchors.LastExactAnchor(ticks);
        Assert.True(anchor is not null, $"{fixture}: some slot holds a character it never spawned into");

        var report = new Replayer(new PortedSimulation(MapLoader)) { Compare = compare }
            .Replay(fixture, trace.Header.Map, ticks, ReplayMode.ClosedLoop, stopAtFirstBadTick: false);

        var after = report.Mismatches.Where(m => m.Tick > anchor!.Value).ToArray();
        var firstBad = after.Length == 0 ? ticks[^1].Index : after.Min(m => m.Tick);

        return (firstBad - anchor.Value, anchor.Value);
    }

    /// <summary>
    /// Both are Team Deathmatch, on the two maps that play it, and both frame.
    /// </summary>
    [Theory]
    [InlineData(Mantic, "tdm_mantic")]
    [InlineData(Debug, "gg_debug")]
    public void BothAreTeamDeathmatchAndFrameCleanly(string fixture, string map)
    {
        var (trace, ticks) = Load(fixture);

        Assert.Equal(map, trace.Header.Map);
        Assert.Equal(GameMode.TeamDeathmatch, GameModes.ShapeOf(MapLoader(map)).GameMode);
        Assert.True(ticks.Count > 2400, $"{fixture}: only {ticks.Count} ticks");
    }

    /// <summary>
    /// <c>gg_debug</c> is the busiest recording in the project.
    /// </summary>
    /// <remarks>
    /// Fifty-two deaths and 262 seeded weapon fires in a hundred seconds, which
    /// is more combat than any other fixture holds — six soldiers on a very
    /// small map. Every one of those deaths is a Team Deathmatch kill, so this
    /// is also the first recorded evidence that the mode scores at all.
    /// <para>
    /// ⚠️ <b>Most of it is still not assertable</b>: the window reaches t523 of
    /// 2,998, which is four of the fifty-two deaths. It is worth keeping
    /// precisely because it becomes the sharpest combat fixture in the project
    /// as that window grows, and it has grown from 2 ticks to 142 by being
    /// chased five times.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheDebugRecordingIsFullOfCombat()
    {
        var (_, ticks) = Load(Debug);

        Assert.True(CountMessages(ticks, MessageType.PlayerDeath) >= 45, "not enough deaths");
        Assert.True(CountMessages(ticks, MessageType.PlayerSpawn) >= 50, "not enough spawns");
        Assert.True(CountMessages(ticks, MessageType.WeaponFire) >= 200, "not enough fire");
    }

    /// <summary>
    /// <c>tdm_mantic</c> replays exactly for three thousand ticks — and is quiet.
    /// </summary>
    /// <remarks>
    /// <b>The longest closed-loop window in the project</b>, and by this file's
    /// own standard worth less than a short one on a loud recording: the bots
    /// never met, so there is no weapon fire and no death in it at all. What it
    /// does gate is a hundred seconds of six characters traversing a map no
    /// fixture had ever touched, on <c>Trajectory | Hp | Deaths</c> with zero
    /// mismatches.
    /// <para>
    /// ⚠️ <b>It does NOT gate the move boxes</b>, which is the reason it was
    /// recorded on this map — <c>tdm_mantic</c> is the only shipped map placing
    /// any (nine up, six down, confirmed live as fifteen). Checked by mutation:
    /// commenting out <c>MoveBoxes.CollisionStep</c> leaves the window at 3,043
    /// ticks, so no bot ever rode one. The same is true of the six pit falls —
    /// there are no deaths to have caused.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheManticRecordingReplaysExactlyForThreeThousandTicks()
    {
        var (_, ticks) = Load(Mantic);

        // Quiet, and asserted so that a re-recording which is *not* quiet cannot
        // silently inherit this window's reputation.
        Assert.Equal(0, CountMessages(ticks, MessageType.WeaponFire));
        Assert.Equal(0, CountMessages(ticks, MessageType.PlayerDeath));

        var (exact, anchor) = ExactWindow(Mantic, Fields.Trajectory | Fields.Hp | Fields.Deaths);

        // 3,043 today, t554 to the end of the recording.
        Assert.True(
            exact >= 3000,
            $"{Mantic}: exact for only {exact} ticks after the last spawn anchor (t{anchor})");
    }

    /// <summary>
    /// ⚠️ <b><c>gg_debug</c> shows the same landing divergence as
    /// <c>ctf_2dfort</c>, and that narrows the open finding a long way.</b>
    /// </summary>
    /// <remarks>
    /// Two ticks after the anchor, <b>all six slots at once</b>: the reference
    /// has them at rest at <c>y = 168</c> with <c>vspeed 0</c>, and the port has
    /// them at <c>163.2</c> still falling at about 1.18 px a tick — exactly
    /// where a character dropped from the spawn marker at <c>y = 162</c> would
    /// be after two ticks of gravity.
    /// <para>
    /// <b>What the two TDM recordings settle about
    /// <see cref="SecondMapFixtureTests"/>'s finding:</b>
    /// </para>
    /// <list type="bullet">
    /// <item><b>It is not "any map that is not <c>ctf_truefort</c>".</b>
    /// <c>tdm_mantic</c> is a third map and replays exactly for 3,043 ticks.</item>
    /// <item><b>It is not the collision mask</b>, which
    /// <c>SecondMapFixtureTests</c> established for <c>ctf_2dfort</c> by asking
    /// the engine and the port the same column.</item>
    /// <item><b>It is not dropdown platforms</b>, which the port does not
    /// implement: <c>tdm_mantic</c> has four and is exact, <c>ctf_2dfort</c> has
    /// none and diverges.</item>
    /// <item><b>The signature is the same on both failing maps</b> — the
    /// reference at rest on an integer <c>y</c> while the port is still falling
    /// from the spawn marker, six pixels above (894→900, 162→168). On
    /// <c>ctf_truefort</c>, where the port is exact, the reference instead comes
    /// to rest on a <em>fractional</em> y (474.47) after an eighteen-pixel drop,
    /// which is what a fall that really happened looks like.</item>
    /// </list>
    /// <para>
    /// ✅ <b>The landing half of that is FIXED (2026-08-27)</b> — it was the
    /// replayer pre-placing a spawning character instead of building it in the
    /// alarm stage, and <see cref="ITraceSimulation.QueueSpawn"/> closes it.
    /// <c>ctf_2dfort</c> went from 5 ticks to 362.
    /// </para>
    /// <para>
    /// ✅ <b>The DROPDOWN PLATFORMS are implemented (2026-08-27) and this window
    /// is no longer about them.</b> <c>gg_debug</c> places two, at
    /// <c>438,216</c> and <c>498,216</c>, and slots 1 and 3 used to fall past
    /// them while the reference rested on <c>y = 192</c>. Implementing the
    /// reference's three branches — the Begin Step ground test, the Step
    /// push-out and the End Step stair guard — took the window from <b>23 ticks
    /// to 100</b>.
    /// </para>
    /// <para>
    /// ⚠️ <b>And the trajectories inside it are now EXACT</b> — the closed-loop
    /// replay reports <c>|dx| max 0, |dy| max 0</c> over 132 ticks and 86
    /// comparisons. What stopped it next was <b>damage</b>, not position: slot 4
    /// survived a death the reference recorded at t476.
    /// </para>
    /// <para>
    /// ✅ <b>And that is fixed (2026-08-27), which took the window from 100 to
    /// 135 — on <em>every</em> field, not just the trajectory.</b> It was two
    /// bugs stacked: GM8's overlap primitives use the precise mask for a rotated
    /// projectile where this port used the bounding box
    /// (<see cref="ProjectileMask"/>), and behind it the parity harness had never
    /// read Team Deathmatch's spawn invulnerability off the wire, so every
    /// replayed respawn was mortal from its first frame. The whole story, and
    /// the arithmetic that found it, is in
    /// <see cref="GgDebugSplashOpenFindingTests"/>.
    /// </para>
    /// <para>
    /// ⚠️ <b>What stops it now, at t523, is a fifth of a pixel</b> on slot 4's x.
    /// Three missing kills became a rounding residue.
    /// </para>
    /// <para>
    /// ⚠️ <b>The platform's BOX is inferred rather than measured.</b>
    /// <c>DropdownPlatform.xml</c> names sprite44 and <c>sprite44.xml</c> puts
    /// its origin at (0,0); sprite44's 60x6 is itself measured, because floors
    /// and walls share it. But the collision oracle never sampled a dropdown
    /// platform — <c>sample-collision.js</c> enumerates what
    /// <c>charSetSolids</c> names, and a platform is never solid — so confirming
    /// it against the running game is owed to the next live session. This
    /// window replaying exactly is strong evidence and not proof: CLAUDE.md's
    /// "a green fixture is not confirmation of a box".
    /// </para>
    /// </remarks>
    [Fact]
    public void GgDebugReplaysPastItsDropdownPlatformsAndStopsOnDamage()
    {
        var (exact, anchor) = ExactWindow(Debug, Fields.Trajectory);

        Assert.True(
            exact is >= 130 and <= 200,
            $"{Debug}: the window is now {exact} ticks after t{anchor}, not the pinned 142 — "
                + "if it has shrunk to about 23 the dropdown platforms have regressed, to about "
                + "100 the rotated projectile mask has, and to 135 the creation-order collision "
                + "walk has; if it has grown a long way, the t523 sub-pixel residue is closed and "
                + "this recording's 52 deaths can become real assertions");
    }

    /// <summary>
    /// The control that puts the blame on the platform rather than on the map:
    /// <c>gg_debug</c> really does place two, and at the height the divergence
    /// points at.
    /// </summary>
    /// <remarks>
    /// Without this, the claim above is an unverified reading of a residual.
    /// With it, implementing dropdown platforms has a coordinate to test against
    /// and this file fails loudly if a shipped map ever moves them.
    /// </remarks>
    [Fact]
    public void GgDebugPlacesTheTwoPlatformsTheDivergencePointsAt()
    {
        var map = MapLoader("gg_debug");

        var platforms = map.Entities
            .Where(e => e.Type == EntityTypes.DropdownPlatform)
            .Select(e => (e.X, e.Y))
            .OrderBy(p => p.X)
            .ToArray();

        Assert.Equal([(438.0, 216.0), (498.0, 216.0)], platforms);
    }
}
