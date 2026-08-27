using System.Linq;
using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// The two control-point arrangements — symmetrical and attack/defend — and
/// Invasion, which shares their setup gates.
/// </summary>
/// <remarks>
/// <c>Objects/Overlays/Gamemodes/ControlPointHUD.events/{Create, Step}.xml</c>,
/// <c>InvasionHUD.events/{Create, Step}.xml</c>, and the neighbour-locking
/// branch of <c>ControlPoint.events/Step.xml:20-35</c>.
/// <para>
/// <b>One HUD object serves two quite different games.</b> Symmetrical is a
/// tug-of-war won by holding both ends; attack/defend is a one-way push where
/// blue starts with everything and only has to survive the clock. They differ in
/// who owns what, how long each point takes, which points are lockable, how long
/// the round is, and who wins — so almost every test here is a pair.
/// </para>
/// <para>
/// <b>No fixture reaches any of it.</b> Every recording in the project is on one
/// Capture the Flag map.
/// </para>
/// </remarks>
public sealed class ControlPointModeTests
{
    /// <summary>Five points, no setup gates: the symmetrical arrangement.</summary>
    private const string SymmetricalMap = "cp_egypt";

    /// <summary>Three areas of two points each, with setup gates: attack/defend.</summary>
    private const string AttackDefendMap = "cp_dirtbowl";

    /// <summary>Capture the Flag plus setup gates, which makes it Invasion.</summary>
    private const string InvasionMap = "ctf_avanti";

    private static LevelMap Level(string name, int area = 1) =>
        LevelMap.FromPng(name, ReferenceTree.ReadMap(name)).ForArea(area);

    private static (WorldState State, CollisionWorld World) Started(string map, int area = 1)
    {
        var level = Level(map, area);
        var world = new CollisionWorld(level);
        var state = default(WorldState);

        state.GameMode = GameModes.Of(level);
        state.AttackDefend = GameModes.HasSetupGate(level);
        state.TimeLimitMinutes = 15;
        state.CapLimit = 5;

        Intel.Place(ref state, level);
        ControlPoints.Place(ref state, level, out var zones);
        world.SetCaptureZones(zones);
        Rounds.Start(ref state);

        return (state, world);
    }

    private static void Tick(CollisionWorld world, ref WorldState state, int count = 1)
    {
        for (var i = 0; i < count; i++)
        {
            var events = default(TickEvents);
            MatchSimulation.Tick(world, ref state, [], ref events);
        }
    }

    // --- map areas ------------------------------------------------------------

    /// <summary>
    /// A multi-area map yields one area's entities at a time.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The port ignored map areas entirely until this landed.</b>
    /// <c>cp_dirtbowl</c> writes six control points into its file and plays two
    /// at a time; <c>basicRoomSetup.gml</c> destroys everything outside the
    /// vertical slice being played. Everything downstream — the collision
    /// rectangles, the spawn points, the objective, and the count of blocks a
    /// state update carries — was reading all three areas at once.
    /// </remarks>
    [Fact]
    public void AMultiAreaMapYieldsOneAreaAtATime()
    {
        var whole = LevelMap.FromPng(AttackDefendMap, ReferenceTree.ReadMap(AttackDefendMap));

        var numbered = whole.Entities.Count(e => e.Type.StartsWith("controlPoint", StringComparison.Ordinal));
        Assert.Equal(6, numbered);

        foreach (var area in new[] { 1, 2, 3 })
        {
            var slice = whole.ForArea(area);
            var points = slice.Entities.Count(e => e.Type.StartsWith("controlPoint", StringComparison.Ordinal));

            Assert.True(points == 2, $"area {area} has {points} control points, expected 2");
        }
    }

    /// <summary>The three areas are disjoint, and between them they are the map.</summary>
    /// <remarks>
    /// The boundary markers themselves sit in two areas at once — the
    /// reference's tests are <c>y &gt; bottom</c> and <c>y &lt; top</c>, so a
    /// entity exactly on a boundary survives both. No shipped map puts one
    /// there, which is why this counts only the control points.
    /// </remarks>
    [Fact]
    public void TheAreasPartitionTheMapsControlPoints()
    {
        var whole = LevelMap.FromPng(AttackDefendMap, ReferenceTree.ReadMap(AttackDefendMap));

        var seen = new List<double>();

        foreach (var area in new[] { 1, 2, 3 })
        {
            foreach (var e in whole.ForArea(area).Entities)
            {
                if (e.Type.StartsWith("controlPoint", StringComparison.Ordinal))
                    seen.Add(e.Y);
            }
        }

        Assert.Equal(6, seen.Count);
        Assert.Equal(6, seen.Distinct().Count());
    }

    /// <summary>A single-area map is returned unchanged.</summary>
    [Fact]
    public void ASingleAreaMapIsUnchanged()
    {
        var map = LevelMap.FromPng(SymmetricalMap, ReferenceTree.ReadMap(SymmetricalMap));

        Assert.Same(map, map.ForArea(1));
        Assert.Same(map, map.ForArea(7));
    }

    // --- which arrangement ----------------------------------------------------

    /// <summary>A setup gate is what makes a control-point map attack/defend.</summary>
    /// <remarks>
    /// <c>if instance_exists(ControlPointSetupGate)</c> — and the same entity is
    /// what turns a Capture the Flag map into Invasion, which is why one
    /// question answers both.
    /// </remarks>
    [Theory]
    [InlineData(SymmetricalMap, false)]
    [InlineData(AttackDefendMap, true)]
    [InlineData(InvasionMap, true)]
    [InlineData("ctf_truefort", false)]
    public void ASetupGateDecidesTheArrangement(string map, bool gated)
    {
        Assert.Equal(gated, GameModes.HasSetupGate(Level(map)));
    }

    // --- starting positions ---------------------------------------------------

    /// <summary>
    /// Symmetrical splits the points down the middle and leaves the centre one
    /// neutral.
    /// </summary>
    /// <remarks>
    /// Five points on <c>cp_egypt</c>: red, red, neutral, blue, blue. The middle
    /// point exists only with an odd count — <c>middlePoint = (total+1)/2</c> is
    /// a half with an even one and nothing is ever equal to it.
    /// </remarks>
    [Fact]
    public void SymmetricalSplitsThePointsDownTheMiddle()
    {
        var (state, _) = Started(SymmetricalMap);

        Assert.Equal(5, state.ControlPointCount);

        Assert.Equal([Team.Red, Team.Red, Team.Spectator, Team.Blue, Team.Blue],
            Enumerable.Range(0, 5)
                .Select(i => state.ControlPoints[i].Owned ? state.ControlPoints[i].Team : Team.Spectator)
                .ToArray());

        Assert.False(state.ControlPoints[2].Owned);
    }

    /// <summary>Attack/defend starts with blue owning everything.</summary>
    [Fact]
    public void AttackDefendStartsWithBlueOwningEverything()
    {
        var (state, _) = Started(AttackDefendMap);

        Assert.Equal(2, state.ControlPointCount);

        for (var i = 0; i < state.ControlPointCount; i++)
        {
            Assert.True(state.ControlPoints[i].Owned);
            Assert.Equal(Team.Blue, state.ControlPoints[i].Team);
        }
    }

    /// <summary>
    /// Each point gets its own capture time, and the two arrangements shape them
    /// differently.
    /// </summary>
    /// <remarks>
    /// Symmetrical is a hill — cheap at the ends, dear in the middle, mirrored.
    /// Attack/defend is a ramp — cheapest nearest the attackers and dearer with
    /// every point behind it, so pushing gets harder. Straight from
    /// <c>ControlPointHUD.events/Create.xml</c>'s table.
    /// </remarks>
    [Theory]
    [InlineData(false, 5, new double[] { 210, 420, 630, 420, 210 })]
    [InlineData(false, 3, new double[] { 472.5, 945, 472.5 })]
    [InlineData(false, 2, new double[] { 945, 945 })]
    [InlineData(true, 5, new double[] { 900, 720, 540, 360, 180 })]
    [InlineData(true, 4, new double[] { 1080, 810, 540, 270 })]
    [InlineData(true, 2, new double[] { 1800, 450 })]
    public void EachPointHasItsOwnCaptureTime(bool attackDefend, int count, double[] expected)
    {
        var actual = Enumerable.Range(0, count)
            .Select(i => ControlPoints.CapTimeFor(attackDefend, count, i))
            .ToArray();

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// A map with more points than the table covers keeps the default.
    /// </summary>
    /// <remarks>
    /// The reference's chain of <c>else if</c>s stops at five and writes nothing
    /// for a sixth point, so it keeps <c>User Event 0</c>'s 300. No shipped map
    /// reaches it, and silently falling through is what the reference does.
    /// </remarks>
    [Fact]
    public void MorePointsThanTheTableCoversKeepsTheDefault()
    {
        Assert.Equal(ControlPoints.CapTime, ControlPoints.CapTimeFor(false, 6, 0));
        Assert.Equal(ControlPoints.CapTime, ControlPoints.CapTimeFor(true, 9, 4));
    }

    /// <summary>The capture time a point is given is the one it uses.</summary>
    [Fact]
    public void ThePointUsesItsOwnCaptureTime()
    {
        var (state, world) = Started(SymmetricalMap);

        // The middle point is neutral, unlocked from the start, and the dearest
        // on the map at 630 ticks.
        Assert.Equal(630, state.ControlPoints[2].CapTime);

        var zone = world.CaptureZones.First(z => z.Point == 2);
        var character = CharacterState.Fresh(PlayerClass.Soldier, Team.Red);
        character.X = (zone.Left + zone.Right) / 2.0;
        character.Y = (zone.Top + zone.Bottom) / 2.0;

        state.Characters[1] = character;
        state.Weapons[1] = WeaponState.Spawn(PlayerClass.Soldier, character.X, character.Y);
        state.Alive[1] = true;
        state.Players[1] = new PlayerState { Team = Team.Red, Class = PlayerClass.Soldier, RespawnTimer = Timers.Inactive };
        state.HasPlayer[1] = true;

        Tick(world, ref state, 629);
        Assert.False(state.ControlPoints[2].Owned);

        Tick(world, ref state);
        Assert.True(state.ControlPoints[2].Owned);
        Assert.Equal(Team.Red, state.ControlPoints[2].Team);
    }

    // --- neighbour locking ----------------------------------------------------

    /// <summary>
    /// On a symmetrical map only the neutral middle is in play at the start.
    /// </summary>
    /// <remarks>
    /// A blue point is locked unless the point to its <em>left</em> is red's,
    /// and a red point unless the point to its <em>right</em> is blue's. At
    /// kick-off nobody has taken anything, so every owned point fails its test
    /// and the only one in play is the neutral centre — which neither branch
    /// touches, because it belongs to nobody.
    /// <para>
    /// ⚠️ <b>That is the whole reason a control point starts unlocked in this
    /// mode.</b> Nothing ever writes the middle point's lock, so it keeps
    /// <c>User Event 0</c>'s value; starting it locked would make the opening
    /// fight of every symmetrical map impossible, and no other mode would
    /// notice.
    /// </para>
    /// <para>
    /// The two <c>if (cp &gt; 1)</c> / <c>if (cp &lt; total)</c> guards are
    /// <em>not</em> exercised here: they only fire for a blue point at the far
    /// left or a red point at the far right, which is a team that has already
    /// pushed the whole way — see
    /// <see cref="SymmetricalIsWonByHoldingBothEnds"/>.
    /// </para>
    /// </remarks>
    [Fact]
    public void OnlyTheNeutralMiddleStartsUnlocked()
    {
        var (state, world) = Started(SymmetricalMap);
        Tick(world, ref state);

        Assert.True(state.ControlPoints[0].Locked);  // red, and point 2 is not blue's
        Assert.True(state.ControlPoints[1].Locked);  // red, and point 3 is neutral
        Assert.False(state.ControlPoints[2].Locked); // neutral: untouched by both branches
        Assert.True(state.ControlPoints[3].Locked);  // blue, and point 3 is neutral
        Assert.True(state.ControlPoints[4].Locked);  // blue, and point 4 is not red's
    }

    /// <summary>
    /// The end points are exempt from the rules once a team has pushed to them.
    /// </summary>
    /// <remarks>
    /// The blue rule is guarded by <c>if (cp &gt; 1)</c> and the red one by
    /// <c>if (cp &lt; totalControlPoints)</c>, so a blue point at the far left
    /// and a red point at the far right fall through <b>without their lock
    /// being written at all</b>. Those are the two points that end the match,
    /// and they stay takeable however the rest of the map looks.
    /// </remarks>
    [Fact]
    public void TheFinalPointOfEachSideIsNeverLockedByTheRules()
    {
        var (state, world) = Started(SymmetricalMap);

        // Red has pushed to the far end: point 5 is blue's and everything to its
        // left is red's, so blue's last point must still be takeable.
        for (var i = 0; i < 4; i++)
        {
            state.ControlPoints[i].Owned = true;
            state.ControlPoints[i].Team = Team.Red;
        }

        Tick(world, ref state);

        Assert.False(state.ControlPoints[4].Locked);

        // The mirror: point 1 is red's with everything to its right blue's.
        var (blue, blueWorld) = Started(SymmetricalMap);

        for (var i = 1; i < 5; i++)
        {
            blue.ControlPoints[i].Owned = true;
            blue.ControlPoints[i].Team = Team.Blue;
        }

        Tick(blueWorld, ref blue);

        Assert.False(blue.ControlPoints[0].Locked);
    }

    /// <summary>Taking the middle opens the next point along.</summary>
    [Fact]
    public void TakingAPointOpensTheNextOne()
    {
        var (state, world) = Started(SymmetricalMap);

        state.ControlPoints[2].Owned = true;
        state.ControlPoints[2].Team = Team.Red;

        Tick(world, ref state);

        // Point 4 (index 3) is blue, and the point to its left is now red's.
        Assert.False(state.ControlPoints[3].Locked);

        // And point 2 (index 1) is still shut to blue: the point to its right
        // is red's, not blue's.
        Assert.True(state.ControlPoints[1].Locked);
    }

    /// <summary>
    /// In attack/defend a point red has taken is locked for ever.
    /// </summary>
    /// <remarks>
    /// <c>if(mode==1 &amp;&amp; team == TEAM_RED) locked = true</c>, applied
    /// after the neighbour rules. Blue is defending and never takes anything
    /// back — the round only moves one way.
    /// </remarks>
    [Fact]
    public void AttackDefendNeverGivesAPointBack()
    {
        var (state, world) = Started(AttackDefendMap);

        state.ControlPoints[0].Team = Team.Red;
        Tick(world, ref state);

        Assert.True(state.ControlPoints[0].Locked);
    }

    // --- the setup gates ------------------------------------------------------

    /// <summary>
    /// The setup timer runs down and closes the map's setup gates while it does.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>This had been broken since phase 3.</b>
    /// <c>CollisionFilter.SetupGatesClosed</c> existed and was never set, so
    /// every setup gate on an Invasion or attack/defend map was open from the
    /// first tick — attackers could simply walk out during the setup period.
    /// No fixture noticed, because no fixture is on either kind of map.
    /// </remarks>
    [Theory]
    [InlineData(AttackDefendMap, 900)]
    [InlineData(InvasionMap, 1800)]
    public void TheSetupTimerClosesTheSetupGates(string map, int setup)
    {
        var (state, world) = Started(map);

        Assert.Equal(setup, state.SetupTimer);

        var gate = world.Rects.First(r => r.Kind == SolidKind.SetupGate);
        var closed = new CollisionFilter { Team = Team.Red, CarryingIntel = false, SetupGatesClosed = true };
        var open = new CollisionFilter { Team = Team.Red, CarryingIntel = false, SetupGatesClosed = false };

        Assert.True(gate.IsSolidTo(closed));
        Assert.False(gate.IsSolidTo(open));

        // A bullet sees exactly the same rule — the one solidity both lists
        // agree on.
        Assert.True(gate.IsSolidTo(CollisionFilter.ForBullet(setupGatesClosed: true)));
        Assert.False(gate.IsSolidTo(CollisionFilter.ForBullet(setupGatesClosed: false)));

        Tick(world, ref state, 10);
        Assert.Equal(setup - 10, state.SetupTimer);
    }

    /// <summary>
    /// A character really is stopped by a setup gate, and walks through it once
    /// the setup ends.
    /// </summary>
    /// <remarks>
    /// The behaviour rather than the flag. <c>CollisionFilter.SetupGatesClosed</c>
    /// is fed from a copy of <c>global.setupTimer</c> mirrored onto every
    /// character once a tick (<see cref="CharacterState.SetupGatesClosed"/>), and
    /// a test that builds the filter by hand would never notice the mirror going
    /// missing — a mutation run proved it did not.
    /// </remarks>
    [Fact]
    public void ACharacterCannotWalkThroughAClosedSetupGate()
    {
        var (state, world) = Started(AttackDefendMap);

        var gate = world.Rects.First(r => r.Kind == SolidKind.SetupGate);
        var x = (gate.Left + gate.Right) / 2.0;
        var y = (gate.Top + gate.Bottom) / 2.0;

        var character = CharacterState.Fresh(PlayerClass.Soldier, Team.Red);
        character.X = x;
        character.Y = y;

        state.Characters[1] = character;
        state.Weapons[1] = WeaponState.Spawn(PlayerClass.Soldier, x, y);
        state.Alive[1] = true;
        state.Players[1] = new PlayerState { Team = Team.Red, Class = PlayerClass.Soldier, RespawnTimer = Timers.Inactive };
        state.HasPlayer[1] = true;

        // The mirror is written at the top of the tick, so ask after one.
        Tick(world, ref state);
        var closed = state.Characters[1].Filter;

        state.SetupTimer = 0;
        Tick(world, ref state);
        var open = state.Characters[1].Filter;

        Assert.True(closed.SetupGatesClosed);
        Assert.False(open.SetupGatesClosed);

        // And the flag reaches a real solidity decision through the character's
        // own filter, not a hand-built one.
        Assert.True(gate.IsSolidTo(closed));
        Assert.False(gate.IsSolidTo(open));
    }

    /// <summary>
    /// The round clock is restarted one second before the gates open, not when
    /// they do.
    /// </summary>
    /// <remarks>
    /// <c>else if (global.setupTimer == 30) { timer = timeLimit; sound_play(SirenSnd) }</c>
    /// — the siren. Everything above 30 in the reference is a countdown beep.
    /// </remarks>
    [Fact]
    public void TheRoundClockRestartsAtTheSiren()
    {
        var (state, world) = Started(AttackDefendMap);

        // Burn most of the setup, and some of the clock with it.
        Tick(world, ref state, Rounds.AttackDefendSetupTicks - 31);
        Assert.Equal(31, state.SetupTimer);
        Assert.True(state.RoundTimer < Rounds.AttackDefendTimeLimit);

        Tick(world, ref state);

        Assert.Equal(30, state.SetupTimer);
        Assert.Equal(Rounds.AttackDefendTimeLimit, state.RoundTimer);
    }

    // --- winning --------------------------------------------------------------

    /// <summary>
    /// Symmetrical is won by holding both ends — whatever the middle says.
    /// </summary>
    /// <remarks>
    /// The reference tests <c>global.cp[1].team == global.cp[total].team</c> and
    /// nothing else, so a team that owns both ends wins even with the centre in
    /// enemy hands. Unreachable in play, because the ends are the last points
    /// each side can reach; written down because the condition really is that
    /// narrow.
    /// </remarks>
    [Fact]
    public void SymmetricalIsWonByHoldingBothEnds()
    {
        var (state, world) = Started(SymmetricalMap);

        Tick(world, ref state);
        Assert.False(state.RoundDecided);

        state.ControlPoints[4].Owned = true;
        state.ControlPoints[4].Team = Team.Red;

        Tick(world, ref state);

        Assert.True(state.RoundDecided);
        Assert.Equal(Team.Red, state.Winner);
    }

    /// <summary>Symmetrical is drawn if the clock runs out with nobody capturing.</summary>
    [Fact]
    public void SymmetricalIsDrawnOnTheClock()
    {
        var (state, world) = Started(SymmetricalMap);
        state.RoundTimer = 1;

        Tick(world, ref state);

        Assert.True(state.RoundDecided);
        Assert.Equal(Team.Spectator, state.Winner);
    }

    /// <summary>
    /// A capture in progress holds the match open past the clock, and lets go
    /// again the moment it is reverted.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Overtime here is not CTF's latch.</b> It is recomputed from scratch
    /// every tick — <c>overtime = 0</c> and then re-derived from the points — so
    /// it switches off as soon as the meter empties, and the round ends on that
    /// tick.
    /// </remarks>
    [Fact]
    public void ACaptureInProgressHoldsTheClockOpen()
    {
        var (state, world) = Started(SymmetricalMap);
        state.RoundTimer = 1;
        state.ControlPoints[2].Capping = 40;
        state.ControlPoints[2].BeingCaptured = true;
        state.ControlPoints[2].CappingTeam = Team.Red;

        Tick(world, ref state);

        Assert.True(state.Overtime);
        Assert.False(state.RoundDecided);
        Assert.Equal(39, state.ControlPoints[2].Capping);

        // Nobody is standing on it, so it reverts at one a tick — and the meter
        // is read *after* the point's own Step, so the first tick above already
        // took one off it. Thirty-eight more leaves exactly one.
        Tick(world, ref state, 38);
        Assert.Equal(1, state.ControlPoints[2].Capping);
        Assert.True(state.Overtime);
        Assert.False(state.RoundDecided);

        // And the round ends on the tick the meter empties.
        Tick(world, ref state);

        Assert.False(state.Overtime);
        Assert.True(state.RoundDecided);
        Assert.Equal(Team.Spectator, state.Winner);
    }

    /// <summary>Attack/defend is won by red taking the last point.</summary>
    [Fact]
    public void AttackDefendIsWonByTakingTheLastPoint()
    {
        var (state, world) = Started(AttackDefendMap);

        var last = state.ControlPointCount - 1;
        state.ControlPoints[last].Team = Team.Red;

        Tick(world, ref state);

        Assert.True(state.RoundDecided);
        Assert.Equal(Team.Red, state.Winner);
    }

    /// <summary>Blue wins attack/defend simply by surviving the clock.</summary>
    [Fact]
    public void AttackDefendIsWonByBlueOnTheClock()
    {
        var (state, world) = Started(AttackDefendMap);
        state.SetupTimer = 0;
        state.RoundTimer = 1;

        Tick(world, ref state);

        Assert.True(state.RoundDecided);
        Assert.Equal(Team.Blue, state.Winner);
    }

    /// <summary>
    /// Attack/defend ignores the configured time limit and plays three minutes.
    /// </summary>
    [Fact]
    public void AttackDefendAlwaysPlaysThreeMinutes()
    {
        var (state, _) = Started(AttackDefendMap);

        Assert.Equal(15, state.TimeLimitMinutes);
        Assert.Equal(Rounds.AttackDefendTimeLimit, state.RoundTimer);
    }

    /// <summary>
    /// Every capture buys the attackers three more minutes, capped at five.
    /// </summary>
    /// <remarks>
    /// <c>ControlPointHUD.timer += 5400</c> on each capture, and
    /// <c>if (timer &gt; 9000) timer = 9000</c> in the HUD to stop the
    /// <c>uint</c> it is sent in from overflowing. The reference credits the two
    /// players who found that.
    /// </remarks>
    [Fact]
    public void ACaptureBuysTimeAndTheClockIsCapped()
    {
        var (state, world) = Started(AttackDefendMap);
        state.SetupTimer = 0;
        state.RoundTimer = 1000;

        // Drive a capture by hand: the meter crossing capTime is what pays out.
        // CapTime + 1, not CapTime: nobody is standing on the point, so the
        // empty-point revert takes one off it before the capture test runs.
        state.ControlPoints[0].BeingCaptured = true;
        state.ControlPoints[0].CappingTeam = Team.Red;
        state.ControlPoints[0].Capping = state.ControlPoints[0].CapTime + 1;

        Tick(world, ref state);

        Assert.Equal(Team.Red, state.ControlPoints[0].Team);
        Assert.Equal(1000 - 1 + Rounds.AttackDefendCaptureBonus, state.RoundTimer);

        // A second one would take it past the cap.
        state.ControlPoints[1].BeingCaptured = true;
        state.ControlPoints[1].CappingTeam = Team.Red;
        state.ControlPoints[1].Capping = state.ControlPoints[1].CapTime + 1;

        Tick(world, ref state);

        Assert.Equal(9000, state.RoundTimer);
        Assert.True(state.ScoreboardSyncRequested);
    }

    // --- Invasion -------------------------------------------------------------

    /// <summary>
    /// Invasion is a Capture the Flag map with setup gates, and its own rules.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The port used to run it through Capture the Flag's round rules,
    /// which are the wrong ones.</b> Invasion is one-sided: red attacks blue's
    /// briefcase, only that briefcase can be taken, red wins on the cap limit,
    /// and blue wins by surviving the clock rather than by matching it.
    /// </remarks>
    [Fact]
    public void InvasionIsCtfWithSetupGatesAndOneWayRules()
    {
        var (state, _) = Started(InvasionMap);

        Assert.Equal(GameMode.Invasion, state.GameMode);
        Assert.Equal(Rounds.InvasionSetupTicks, state.SetupTimer);

        // The red briefcase is scenery in this mode; blue's is the objective.
        Assert.False(state.RedIntel.Grabbable);
        Assert.True(state.BlueIntel.Grabbable);
    }

    /// <summary>Red wins Invasion by reaching the cap limit.</summary>
    [Fact]
    public void RedWinsInvasionOnTheCapLimit()
    {
        var (state, world) = Started(InvasionMap);
        state.RedCaps = 5;

        Tick(world, ref state);

        Assert.True(state.RoundDecided);
        Assert.Equal(Team.Red, state.Winner);
    }

    /// <summary>
    /// Blue wins on the clock — unless red is one capture from winning and has
    /// the briefcase out.
    /// </summary>
    /// <remarks>
    /// ⚠️ <c>timer &lt;= 0 and (!redIsCapping or global.redCaps+1 &lt; caplimit)</c>.
    /// The let-off is narrow: a capture in progress only holds the match open
    /// when it would be the <em>winning</em> capture. At one of five with the
    /// flag out, blue still wins on the whistle.
    /// </remarks>
    [Theory]
    [InlineData(0, false, true)]   // nothing happening: blue wins
    [InlineData(1, true, true)]    // flag out, but 2 of 5 would not win it
    [InlineData(4, false, true)]   // one off, flag safely home
    [InlineData(4, true, false)]   // one off, flag out: the match stays open
    public void BlueWinsInvasionOnTheClockUnlessRedIsOneCaptureAway(
        int redCaps, bool flagOut, bool blueWins)
    {
        var (state, world) = Started(InvasionMap);
        state.SetupTimer = 0;
        state.RoundTimer = 1;
        state.RedCaps = redCaps;

        if (flagOut)
            state.BlueIntel.OnField = false;

        Tick(world, ref state);

        Assert.Equal(blueWins, state.RoundDecided);

        if (blueWins)
            Assert.Equal(Team.Blue, state.Winner);
        else
            Assert.True(state.Overtime);
    }

    /// <summary>
    /// A briefcase lying still with a dead return timer does not hold the match
    /// open.
    /// </summary>
    /// <remarks>
    /// ⚠️ Invasion's <c>redIsCapping</c> is <b>not</b> Capture the Flag's "away
    /// from base": it is <c>!instance_exists(IntelligenceBlue) or
    /// alarm[0] &gt; 0</c> — carried, or dropped with its return timer still
    /// running. A flag dropped straight off its base has an alarm of zero (see
    /// <c>IntelState.RecoverTimer</c>) and counts as safe.
    /// </remarks>
    [Theory]
    [InlineData(0, true)]
    [InlineData(450, false)]
    public void ADroppedBriefcaseCountsOnlyWhileItsTimerRuns(int recoverTimer, bool blueWins)
    {
        var (state, world) = Started(InvasionMap);
        state.SetupTimer = 0;
        state.RoundTimer = 1;
        state.RedCaps = 4;

        state.BlueIntel.RecoverTimer = recoverTimer;

        Tick(world, ref state);

        Assert.Equal(blueWins, state.RoundDecided);
    }

    // --- the wire -------------------------------------------------------------

    /// <summary>
    /// A live server frames the control-point block for both arrangements.
    /// </summary>
    /// <remarks>
    /// The block is the time limit, the round clock, the setup timer, and then
    /// one block per point with no count — so a wrong point count on
    /// <c>cp_dirtbowl</c> would desynchronise the stream rather than show a
    /// wrong number. That is exactly what the map-area filter above prevents.
    /// </remarks>
    [Theory]
    [InlineData(SymmetricalMap, 5)]
    [InlineData(AttackDefendMap, 2)]
    public void ALiveServerFramesTheControlPointBlock(string map, int points)
    {
        var level = Level(map);

        using var harness = new ServerHarness(o => o.Map = map);
        var client = harness.JoinClient("watcher");

        var stream = new ServerStream();
        stream.SetShape(GameModes.ShapeOf(level));
        var buffer = new ReceiveBuffer();

        harness.Tick();
        client.Poll();
        buffer.Append(client.Take(client.Received.Count));

        var messages = new List<ServerMessage>();
        while (stream.TryRead(buffer, out var message))
            messages.Add(message);

        var full = messages.Last(m => m.State is { Tier: MessageType.FullUpdate }).State!;

        // [timeLimitMins, timer, setupTimer]
        Assert.Equal(3, full.HudScalars.Length);
        Assert.Equal(15, full.HudScalars[0]);
        Assert.Equal(points, full.ControlPoints.Length);

        var setup = map == AttackDefendMap ? Rounds.AttackDefendSetupTicks : 0;
        Assert.InRange(full.HudScalars[2], setup - 30, setup);
    }
}
