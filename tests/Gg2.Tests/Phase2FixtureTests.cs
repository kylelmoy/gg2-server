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
/// <b>Recorded 2026-08-27.</b> Three scripted takes on <c>gg_debug</c>, a scout
/// driven from the agent bridge, and <b>one take of a human playing
/// <c>tdm_mantic</c></b>. <c>docs/PLAN.md</c> has asked for these since phase 2
/// and listed them as the one deliverable still open:
/// "a double-jump, a wall step-up, a corner fall and a drop-through platform
/// need scripted input rather than a bot's own judgement". Every other fixture
/// in this project is bots playing, which covers walking and jumping only
/// incidentally.
/// <para>
/// ✅ <b>All four replay closed-loop with zero mismatches</b> on
/// <see cref="Fields.All"/> — 723, 625, 474 and <b>1,161</b> ticks. Nobody fires
/// and nobody dies in any of them, so unlike the combat fixtures there is
/// nothing to cap a window but the recording's own length, or a real divergence.
/// </para>
/// <para>
/// ⚠️ <b>THE HUMAN TAKE IS THE ONE THAT WORKED, and that is the finding.</b>
/// Three scripted recordings reached <see cref="CharacterHitObstacle"/>'s step
/// arms <b>zero</b> times between them, despite 505 wall collisions — every one
/// taking the arm that stops the character dead. A hundred seconds of a person
/// playing reached them <b>50 times</b>, plus 3 sloping ceilings, and turned up
/// a drop-through bug on the way
/// (<see cref="ThePortPutsAPlayerBackOnAPlatformTheyDroppedThrough"/>).
/// </para>
/// <para>
/// <b>Why, and it is worth knowing before writing another driver:</b> a script
/// walks in straight lines between points worked out in advance, so it only ever
/// meets the geometry somebody predicted. A person wanders, changes direction
/// mid-stride, and taps keys for five ticks and releases them. Two of the three
/// scripted routes were also planned against a walkmask profile taken with the
/// <em>wrong collision filter</em>, which is its own lesson — but the deeper one
/// is that <b>a person is a better fuzzer than a plan</b>.
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

    /// <summary>
    /// ✅ <b>A HUMAN playing <c>tdm_mantic</c> for a hundred seconds</b>, and the
    /// only recording in the project that is neither a bot nor a script.
    /// </summary>
    /// <remarks>
    /// <b>It reaches both branches three scripted attempts could not</b> — 50
    /// step-ups and 3 sloping ceilings against zero — and it found a bug on the
    /// way. See <see cref="TheMutationTableThatSaysWhatTheseFixturesGate"/> and
    /// <see cref="ThePortPutsAPlayerBackOnAPlatformTheyDroppedThrough"/>.
    /// <para>
    /// ⚠️ <b>The lesson is about the recording method, not the map.</b> A script
    /// walks in straight lines between the points somebody worked out in
    /// advance; a person wanders, changes their mind, taps a key and releases
    /// it. Every branch this fixture reaches that the others do not is of that
    /// second kind.
    /// </para>
    /// </remarks>
    private const string Human = "reference-tdm_mantic-humanmoves.trace";

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
    [InlineData(Walk, "gg_debug")]
    [InlineData(Jumps, "gg_debug")]
    [InlineData(Stairs, "gg_debug")]
    [InlineData(Human, "tdm_mantic")]
    public void EachIsASinglePlayerRecordingWithNoCombatInIt(string fixture, string map)
    {
        var (trace, ticks) = Load(fixture);

        Assert.Equal(map, trace.Header.Map);
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
    [InlineData(Human, 1100)]
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
    /// <description>scripted: unchanged ❌ — <b>human: 50 executions</b> ✅</description></item>
    /// <item><term>sloping ceiling (<c>CharacterHitObstacle</c>, <c>Y += StepHeight</c>)</term>
    /// <description>scripted: unchanged ❌ — <b>human: 3 executions</b> ✅</description></item>
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
    /// <b>So <c>gg_debug</c>'s six-pixel staircase is not a step-up.</b> The
    /// scout demonstrably climbs it — 192 back up to 168, verified from the
    /// server mid-recording — by some path that is not these arms. Reaching them
    /// needs geometry where <c>PlaceFree(x + sign, y - 6)</c> is true while
    /// <c>PlaceFree(x + sign, y)</c> is false: a wall at most six pixels tall
    /// with headroom above it.
    /// </para>
    /// <para>
    /// ✅ <b>And a human found that geometry in a hundred seconds without being
    /// told where it was.</b> 50 step-ups and 3 sloping ceilings on
    /// <c>tdm_mantic</c>, closing both branches that three planned routes missed.
    /// ⚠️ Note the counters are of <em>executions</em>, not window movement:
    /// disabling an arm that fires 50 times still may not move a window, which
    /// is why the count is the measurement here and the mutation is not.
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
        Assert.Equal(2525, ExactWindow(Human).Exact);
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

    /// <summary>
    /// ⚠️ <b>OPEN FINDING: the port puts a player back on a dropdown platform
    /// they had already dropped through.</b>
    /// </summary>
    /// <remarks>
    /// <b>Found by a human tapping a key, 2026-08-27.</b> At t1621–1625 of
    /// <see cref="Human"/> the player holds <c>KEY_DOWN</c> on the platform at
    /// <c>2622,1014</c>, and lets go. The reference drops them through and they
    /// keep falling — y 990.4 → 1001.2 → 1041.2 → 1086.4. The port starts
    /// falling with them and then, once <c>Down</c> is released, <b>lifts them
    /// back onto the platform</b> and holds them at 990.4 for the rest of the
    /// recording.
    /// <para>
    /// ⚠️ <b>All three dropdown paths DO honour <c>KEY_DOWN</c></b> — the Begin
    /// Step ground test, the Step push-out and
    /// <c>CharacterPhysics.StandingOnDropdown</c> each check it, so this was
    /// never a missing feature. ✅ <b>The suspect named here was the right one</b>
    /// — the Step push-out's "already inside it before this tick's move" guard —
    /// and the defect was its <b>sign</b>.
    /// <para>
    /// ✅ <b>FIXED 2026-08-28.</b> The reference is
    /// <c>place_meeting(x, y + (other.y - other.y_previous), other)</c> inside
    /// <c>with(DropdownPlatform)</c>, so <c>x</c>/<c>y</c> are the <b>platform's</b>
    /// and it moves the platform DOWN by the character's dy — which relative to
    /// the character asks "was I already inside it at my previous y". The port
    /// moved the <em>character</em> down by dy instead, which asks whether it
    /// will be inside the platform <em>next</em> tick. Opposite direction, and
    /// the comment above it already described the correct behaviour.
    /// </para>
    /// <para>
    /// <b>What one sign was worth:</b> this fixture's exact window went
    /// <b>1,161 → 2,525</b>, which is the whole recording past its anchor with
    /// zero mismatches; the client-prediction gate's row for it went
    /// <b>270 / 44 → 24 / 2</b> in y; and <c>gg_debug-closedtdm</c>'s p99 in y
    /// went <b>77 → 2</b> — a fixture nothing else in the suite was watching for
    /// this.
    /// </para>
    /// <para>
    /// ⚠️ <b>The shift is applied to the platform, not the character, and that
    /// is not equivalent.</b> GM8 rounds the <em>calling instance's</em> position
    /// to build the box it tests, so the reference rounds <c>platformY + dy</c>;
    /// rounding the character's <c>y - dy</c> is a different number with an
    /// unrelated fractional part. See the comment in <c>CharacterPhysics</c>.
    /// </para>
    /// <para>
    /// Note the earlier dropdown coverage is NOT this: the walk fixture gates the
    /// platform <em>holding a character up</em>, which is the opposite direction.
    /// Both directions now have a gate.
    /// </para>
    /// </remarks>
    [Fact]
    public void APlayerWhoDropsThroughAPlatformKeepsFalling()
    {
        var (trace, ticks) = Load(Human);

        // The tap that starts it, on the wire, on every tick.
        var down = ticks
            .Where(t => t.Index is >= 1618 and <= 1628)
            .Where(t => (t.Slots[1].Input.Keys & PlayerKeys.Down) != 0)
            .Select(t => t.Index)
            .ToArray();

        Assert.Equal([1621, 1622, 1623, 1624, 1625], down);

        var anchor = FixtureAnchors.LastExactAnchor(ticks)!.Value;

        var report = new Replayer(new PortedSimulation(MapLoader)) { Compare = Fields.All }
            .Replay(Human, trace.Header.Map, ticks, ReplayMode.ClosedLoop, stopAtFirstBadTick: false);

        // ⚠️ Was tick 1634, six ticks after the key came up, with the reference
        // 254 wire units — nearly 51 px — BELOW the port, which is the port
        // holding the player up on a platform they had left. Now there is
        // nothing at all after the anchor.
        Assert.DoesNotContain(report.Mismatches, m => m.Tick > anchor);
    }
}
