using System.Linq;
using Gg2.Maps;
using Gg2.Parity;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// <b>The targeted phase-2 fixture set: recordings of a <em>scripted</em>
/// character rather than a bot's own judgement.</b>
/// </summary>
/// <remarks>
/// <b>Recorded 2026-08-27</b> on <c>gg_debug</c>, one scout driven from the
/// agent bridge against the reference server. <c>docs/PLAN.md</c> has asked for
/// these since phase 2 and listed them as the one deliverable still open:
/// "a double-jump, a wall step-up, a corner fall and a drop-through platform
/// need scripted input rather than a bot's own judgement". Every other fixture
/// in this project is bots playing, which covers walking and jumping only
/// incidentally.
/// <para>
/// ✅ <b>All three replay closed-loop to the last tick of their recording</b>, on
/// <see cref="Fields.All"/> — 723, 625 and 474 ticks with zero mismatches. A
/// scripted trace has no unseeded weapon in it and nobody dies, so unlike the
/// combat fixtures there is nothing to cap the window but the recording's own
/// length.
/// </para>
/// <para>
/// ⚠️ <b>Two of the four manoeuvres are covered, not four</b>, and which two was
/// measured rather than intended — see
/// <see cref="TheMutationTableThatSaysWhatTheseFixturesGate"/>. The double jump
/// and the drop-through platform are gated; a <b>wall step-up</b> and a
/// <b>corner fall</b> are not, and the third recording was made specifically to
/// reach them and did not.
/// </para>
/// <para>
/// ⚠️ <b>The reason is worth more than the fixtures.</b> The step arms of
/// <see cref="CharacterHitObstacle"/> execute <b>zero</b> times across all
/// three, while the two walking recordings enter that solver's wall branch 505
/// times between them — every one taking the arm that stops the character dead.
/// <c>gg_debug</c>'s six-pixel staircase is climbed by some other path
/// entirely, so it is not the geometry those arms want.
/// </para>
/// </remarks>
public sealed class Phase2FixtureTests
{
    /// <summary>A scout walking left off the spawn ledge and across the map.</summary>
    private const string Walk = "reference-gg_debug-scriptedwalk.trace";

    /// <summary>A scout jumping on the spot, about thirty times.</summary>
    private const string Jumps = "reference-gg_debug-scriptedjumps.trace";

    /// <summary>
    /// A scout walking down <c>gg_debug</c>'s six-pixel staircase and back up
    /// it, twice.
    /// </summary>
    /// <remarks>
    /// Recorded specifically to reach <see cref="CharacterHitObstacle"/>'s step
    /// arms, and it does not — see
    /// <see cref="TheMutationTableThatSaysWhatTheseFixturesGate"/>. Kept anyway:
    /// it is 474 more ticks of exact terrain traversal, and it is the evidence
    /// that the staircase is not what those arms are for.
    /// </remarks>
    private const string Stairs = "reference-gg_debug-scriptedstairs.trace";

    private static LevelMap MapLoader(string name) =>
        LevelMap.FromPng(name, ReferenceTree.ReadMap(name));

    private static (Trace Trace, IReadOnlyList<TraceTick> Ticks) Load(string fixture)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture);
        Assert.True(File.Exists(path), $"missing {path}");

        var trace = Trace.Decode(File.ReadAllBytes(path));
        var ticks = TraceParser.Parse(trace, GameModes.ShapeOf(MapLoader(trace.Header.Map)), out _, out var stopped);

        Assert.True(stopped is null, $"{fixture}: {stopped}");
        return (trace, ticks);
    }

    private static (int Exact, int Anchor) ExactWindow(string fixture)
    {
        var (trace, ticks) = Load(fixture);

        var anchor = FixtureAnchors.LastExactAnchor(ticks);
        Assert.True(anchor is not null, $"{fixture}: some slot holds a character it never spawned into");

        var report = new Replayer(new PortedSimulation(MapLoader)) { Compare = Fields.All }
            .Replay(fixture, trace.Header.Map, ticks, ReplayMode.ClosedLoop, stopAtFirstBadTick: false);

        var after = report.Mismatches.Where(m => m.Tick > anchor!.Value).ToArray();
        var firstBad = after.Length == 0 ? ticks[^1].Index : after.Min(m => m.Tick);

        return (firstBad - anchor.Value, anchor.Value);
    }

    /// <summary>
    /// Both recordings frame to the last byte and hold exactly one scripted
    /// character.
    /// </summary>
    /// <remarks>
    /// The negative worth having: a recording of one player is easy to get
    /// wrong, and a trace with the scout still spectating would replay
    /// beautifully while gating nothing.
    /// </remarks>
    [Theory]
    [InlineData(Walk)]
    [InlineData(Jumps)]
    [InlineData(Stairs)]
    public void BothAreScriptedSinglePlayerRecordingsOnGgDebug(string fixture)
    {
        var (trace, ticks) = Load(fixture);

        Assert.Equal("gg_debug", trace.Header.Map);
        Assert.True(ticks.Count > 700, $"{fixture}: only {ticks.Count} ticks");

        // Exactly one spawn, and it is what the window anchors on.
        var spawns = ticks.SelectMany(t => t.Events).Count(m => m.Type == MessageType.PlayerSpawn);
        Assert.Equal(1, spawns);

        // Nobody fires and nobody dies, which is what makes these windows say
        // something about the physics alone.
        Assert.Equal(0, ticks.SelectMany(t => t.Events).Count(m => m.Type == MessageType.WeaponFire));
        Assert.Equal(0, ticks.SelectMany(t => t.Events).Count(m => m.Type == MessageType.PlayerDeath));
    }

    /// <summary>
    /// ✅ <b>Both replay to the last tick of their recording, on every field.</b>
    /// </summary>
    /// <remarks>
    /// 723 and 625 ticks today, which is the whole of each trace after its spawn
    /// anchor. Floors rather than equalities because they cannot grow — the
    /// recording ends — so the only news a change here can bring is bad news.
    /// </remarks>
    [Theory]
    [InlineData(Walk, 700)]
    [InlineData(Jumps, 600)]
    [InlineData(Stairs, 450)]
    public void BothReplayToTheEndOfTheirRecording(string fixture, int floor)
    {
        var (exact, anchor) = ExactWindow(fixture);

        Assert.True(
            exact >= floor,
            $"{fixture}: exact for only {exact} ticks after the spawn anchor (t{anchor})");
    }

    /// <summary>
    /// ⚠️ <b>What these fixtures actually gate, measured by deleting each branch
    /// and watching the window — not by what the route was meant to do.</b>
    /// </summary>
    /// <remarks>
    /// <b>The table, run 2026-08-27.</b> Each row disables one branch and reports
    /// the three windows:
    /// <list type="table">
    /// <item><term>baseline</term><description>walk 723, jumps 625, stairs 474</description></item>
    /// <item><term>double jump (<c>CharacterPhysics</c>, <c>CanDoubleJump</c>)</term>
    /// <description>jumps <b>162</b> ✅, others unchanged</description></item>
    /// <item><term>dropdown platform (<c>CharacterPhysics</c>, the Step push-out)</term>
    /// <description>walk <b>116</b> ✅, others unchanged</description></item>
    /// <item><term>stair descent (<c>CharacterPhysics</c> End Step)</term>
    /// <description>walk <b>4</b>, jumps <b>1</b> — gated hard, but see below</description></item>
    /// <item><term>wall step-up (<c>CharacterHitObstacle</c>, <c>Y -= StepHeight</c>)</term>
    /// <description>all three unchanged — <b>none</b> ❌</description></item>
    /// <item><term>sloping ceiling (<c>CharacterHitObstacle</c>, <c>Y += StepHeight</c>)</term>
    /// <description>all three unchanged — <b>none</b> ❌</description></item>
    /// </list>
    /// <para>
    /// ✅ <b>So phase 2 gains a double-jump gate and a second, much sharper
    /// dropdown-platform gate.</b> The platform branch was previously covered
    /// only by <c>gg_debug-closedtdm</c>, whose window moves 135 → about 23 when
    /// it regresses; this one moves 723 → 116.
    /// </para>
    /// <para>
    /// ⚠️ <b>The stair descent collapses both walking fixtures, but that is not
    /// evidence these recordings are special</b> — it runs on every grounded
    /// tick, so almost any fixture would show it. It is in the table for
    /// completeness, not as a claim of new coverage.
    /// </para>
    /// <para>
    /// ⚠️ <b>The step arms are executed ZERO times by all three recordings, and
    /// that is measured rather than inferred.</b> A counter on each arm during
    /// the replays: the walk and stairs fixtures enter
    /// <c>characterHitObstacle</c>'s wall branch <b>340 and 165 times</b> — 505
    /// real wall collisions — and <em>every one</em> takes the third arm, the
    /// one that stops the character dead. Neither step is ever free.
    /// </para>
    /// <para>
    /// <b>So <c>gg_debug</c>'s six-pixel staircase is not a step-up, and that is
    /// the useful finding.</b> The scout demonstrably climbs it — 192 back up to
    /// 168, verified from the server mid-recording — by some path that is not
    /// these arms. Reaching them needs geometry where
    /// <c>PlaceFree(x + sign, y - 6)</c> is true while <c>PlaceFree(x + sign, y)</c>
    /// is false: a wall at most six pixels tall with headroom above it. A
    /// staircase approached at a scout's five pixels a tick is not that.
    /// </para>
    /// <para>
    /// This test asserts only the positives, because a test cannot delete a
    /// branch. It is here so the table has a home beside the fixtures it
    /// describes, and so the windows it quotes are checked.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheMutationTableThatSaysWhatTheseFixturesGate()
    {
        Assert.Equal(723, ExactWindow(Walk).Exact);
        Assert.Equal(625, ExactWindow(Jumps).Exact);
        Assert.Equal(474, ExactWindow(Stairs).Exact);
    }

    /// <summary>
    /// The jumps recording really is full of jumps, and the walk recording is
    /// not — asserted from the wire so the mutation table above has a control
    /// that does not depend on the port.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Without this, "disabling the double jump shrinks the window" could
    /// be true of any recording that happens to leave the ground.</b> A scout
    /// jumping thirty times in place is a specific claim about what was
    /// recorded, and it is checkable: the jump key is on the wire on every tick,
    /// on every tier.
    /// </remarks>
    [Fact]
    public void TheJumpsRecordingPressesJumpAndTheWalkOneNeverDoes()
    {
        static int JumpPresses(IReadOnlyList<TraceTick> ticks)
        {
            var presses = 0;
            var held = false;

            foreach (var tick in ticks)
            {
                foreach (var slot in tick.Slots)
                {
                    if (slot.Observed is not { } observed)
                        continue;

                    var now = (observed.Keys & PlayerKeys.Jump) != 0;

                    if (now && !held)
                        presses++;

                    held = now;
                }
            }

            return presses;
        }

        Assert.True(JumpPresses(Load(Jumps).Ticks) > 20, "the jumps recording should be full of jumps");
        Assert.Equal(0, JumpPresses(Load(Walk).Ticks));
        Assert.Equal(0, JumpPresses(Load(Stairs).Ticks));
    }
}
