using Gg2.Bots;
using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Simulation;
using Xunit.Abstractions;

namespace Gg2.Tests;

/// <summary>
/// <c>botSuppressSpot</c>: where a heavy keeps shooting when it can no longer see
/// what it is shooting at.
/// </summary>
/// <remarks>
/// A bot gets 30 ticks of memory across a lost sightline and keeps firing at the
/// last position actually seen. <b>That is right for a corner somebody is about
/// to step back around and wrong for one they left</b>, because the stale position
/// is behind cover by construction — the bot is shooting the wall it lost them
/// behind.
/// <para>
/// Measured on koth_valley with twelve bots over 3,172 frames: of 13,733
/// bot-ticks with a live target, <b>2,744 — 20% — had no line of sight to
/// it</b>, and for a heavy specifically 1,432 of 6,442. About one tick in five of
/// every engagement.
/// </para>
/// </remarks>
public sealed class BotSuppressTests(ITestOutputHelper output)
{
    private const string Map = "ctf_truefort";

    // Swept for, not chosen by eye. A heavy on the high shelf at node 0 and an
    // enemy at node 10, 115px away with the geometry between them blocking the line
    // — and node 19 beyond, which the heavy CAN see.
    //
    // The 115px matters as much as the blocked line: BOT_BLIND_RANGE is 260, so a
    // blind target further out than that is never held at all and the whole feature
    // is unreachable.
    private const int HeavyNode = 0;

    /// <summary>Where the enemy is first seen — the heavy has a clear line to it.</summary>
    private const int SeenNode = 19;

    /// <summary>Where it goes: 115px away, behind the geometry.</summary>
    private const int HiddenNode = 10;

    /// <summary>
    /// The node the walk starts from in the unit tests — where it was last SEEN.
    /// </summary>
    private const int EnemyNode = 10;

    private static NavGraph Graph()
    {
        ReferenceTree.RequireNavCache();
        return NavCache.Load(ReferenceTree.NavCacheDirectory, Map, 1);
    }

    private static CollisionWorld World() =>
        new(LevelMap.FromPng(Map, ReferenceTree.ReadMap(Map)));

    private static ScenarioWorld Scenario() =>
        ScenarioWorld.Load(Map, 1, ReferenceTree.IncludedFiles, ReferenceTree.NavCacheDirectory);

    /// <summary>The tick <see cref="Engage"/> stopped on.</summary>
    private static int LastTick;

    private static CharacterState At(NavGraph graph, int node)
    {
        var self = CharacterState.Fresh(PlayerClass.Heavy, Team.Red);
        (self.X, self.Y) = BotSuppress.Centre(graph, node);
        return self;
    }

    // ------------------------------------------------------------------
    // The walk
    // ------------------------------------------------------------------

    /// <summary>
    /// The spot is somewhere the bot can see, and never where the enemy vanished.
    /// </summary>
    /// <remarks>
    /// <b>Not the node they were last seen on</b>: that one is behind cover by
    /// construction, and shooting it is exactly the behaviour this replaces. The test
    /// is cheap and it is also the guard that stops a caller aiming at a wall when
    /// the enemy simply stood still.
    /// </remarks>
    [Fact]
    public void TheSpotIsVisibleAndIsNotWhereTheEnemyVanished()
    {
        var graph = Graph();
        var world = World();
        var self = At(graph, HeavyNode);

        var (enemyX, enemyY) = BotSuppress.Centre(graph, EnemyNode);
        var filter = CollisionFilter.ForBullet();

        // The premise: the heavy genuinely cannot see where the enemy was.
        Assert.True(
            world.CollisionLineBlocksBullets(self.X, self.Y, enemyX, enemyY, filter),
            "the fixture's blocked line is not blocked, so there is nothing to suppress");

        var spot = BotSuppress.Spot(graph, world, in self, enemyX, enemyY, Team.Blue);

        Assert.True(spot >= 0, "no suppression spot at all");
        Assert.NotEqual(EnemyNode, spot);

        var (spotX, spotY) = BotSuppress.Centre(graph, spot);

        output.WriteLine(
            $"heavy ({self.X:0},{self.Y:0}) enemy ({enemyX:0},{enemyY:0}) spot n{spot} ({spotX:0},{spotY:0})");

        Assert.False(
            world.CollisionLineBlocksBullets(self.X, self.Y, spotX, spotY, filter),
            "the suppression spot is behind cover too");
    }

    /// <summary>
    /// The spot is somewhere the enemy could actually walk to.
    /// </summary>
    /// <remarks>
    /// <b>The correctness half of the walk, and the one a test can hold.</b> The
    /// hop budget is a COST bound — a bounded breadth-first walk rather than an A*,
    /// because the A* version timed at <b>8.13 ms a call on koth_valley's 270
    /// nodes</b> and six heavies recomputing on one frame is 48.8 ms against a 33.3 ms
    /// budget. Nothing here gates that number; what is gated is that the answer is a
    /// node the enemy can reach from where it was, which a broken edge index or a
    /// mis-read gate would break.
    /// </remarks>
    [Fact]
    public void TheSpotIsReachableFromWhereTheEnemyWas()
    {
        var graph = Graph();
        var world = World();
        var self = At(graph, HeavyNode);
        var (x, y) = BotSuppress.Centre(graph, EnemyNode);

        var spot = BotSuppress.Spot(graph, world, in self, x, y, Team.Blue);
        Assert.True(spot >= 0);

        // An independent breadth-first reachability check over the same gates.
        var seen = new HashSet<int> { EnemyNode };
        var queue = new Queue<int>();
        queue.Enqueue(EnemyNode);

        while (queue.Count > 0)
        {
            foreach (var edge in graph.EdgesFrom(queue.Dequeue()))
            {
                if (!graph.EdgePassable(in edge, Team.Blue, hasIntel: false, setupGatesClosed: false))
                    continue;

                if (seen.Add(edge.To))
                    queue.Enqueue(edge.To);
            }
        }

        Assert.Contains(spot, seen);

        // And "reachable" is not vacuous: the walk does NOT reach the whole graph
        // from here, so containment is a real constraint rather than a tautology.
        // (That the gates in particular narrow it is TheWalkUsesTheEnemysGates'
        // claim, on a source where red and blue genuinely differ — from this one
        // they do not, which is why that test uses a different pair.)
        Assert.True(
            seen.Count < graph.NodeCount,
            $"every one of {graph.NodeCount} nodes is reachable from the source, so this asserts nothing");
    }

    /// <summary>
    /// The walk uses the ENEMY's gates, not ours.
    /// </summary>
    /// <remarks>
    /// <b>It is their route.</b> A red bot must not decide that a blue enemy will
    /// come out through a red spawn gate — the answer would be a doorway that enemy
    /// can never use.
    /// <para>
    /// Swept for on ctf_truefort: from node 8 a red traveller is walled in and a
    /// blue one is not, and from node 74 it is the other way round. Both directions
    /// matter, or the test passes on an implementation that simply favours one team.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheWalkUsesTheEnemysGates()
    {
        var graph = Graph();
        var world = World();
        var self = At(graph, HeavyNode);

        var (ax, ay) = BotSuppress.Centre(graph, 8);

        Assert.Equal(BotSuppress.NoSpot, BotSuppress.Spot(graph, world, in self, ax, ay, Team.Red));
        Assert.True(BotSuppress.Spot(graph, world, in self, ax, ay, Team.Blue) >= 0);

        // The mirror, so this is not "blue always finds more".
        var (bx, by) = BotSuppress.Centre(graph, 74);

        Assert.True(BotSuppress.Spot(graph, world, in self, bx, by, Team.Red) >= 0);
        Assert.Equal(BotSuppress.NoSpot, BotSuppress.Spot(graph, world, in self, bx, by, Team.Blue));
    }

    /// <summary>
    /// The snap is downward, and nothing under a position means no spot.
    /// </summary>
    /// <remarks>
    /// A position in mid-air belongs to the surface under it — a target last seen
    /// jumping is going to land on something.
    /// </remarks>
    [Fact]
    public void TheSnapIsDownwardAndNothingUnderItMeansNoSpot()
    {
        var graph = Graph();
        var world = World();
        var self = At(graph, HeavyNode);

        Assert.Equal(BotSuppress.NoSpot, BotSuppress.Spot(graph, world, in self, -5000, -5000, Team.Blue));

        // The control: a target in MID-AIR above a real floor does resolve,
        // because the snap searches downward. Without it this passes on an
        // implementation that only ever answers for a target standing still.
        var (x, y) = BotSuppress.Centre(graph, EnemyNode);

        Assert.True(BotSuppress.Spot(graph, world, in self, x, y - 40, Team.Blue) >= 0);
    }

    // ------------------------------------------------------------------
    // The caller
    // ------------------------------------------------------------------

    /// <summary>
    /// Places a heavy, lets it SEE an enemy, then takes the enemy behind cover.
    /// </summary>
    /// <remarks>
    /// <b>Seen and then lost, never acquired blind — and that is not a
    /// convenience.</b> The snapshot the aim is solved from is only refreshed while
    /// the target is visible, and the acquire hold returns before the refresh — so a
    /// target that was NEVER seen leaves the snapshot at its initial (0, 0) and the
    /// bot aims at the map origin. <b>The reference does the same thing</b>
    /// (`if(!canAim) return 0;` sits above the perceive block in botCombatUpdate), so
    /// this is faithful rather than a defect — but it means a fixture that acquires
    /// blind measures the origin instead of the feature.
    /// <para>
    /// The real case is the one the feature is for: somebody in the open who steps
    /// behind a corner. The snapshot then holds the last position actually seen,
    /// which is where the walk starts.
    /// </para>
    /// </remarks>
    private static BotCombatState Engage(
        PlayerClass playerClass, NavGraph? graph, out ScenarioWorld world, out int slot,
        out double lastSeenX, out double lastSeenY)
    {
        var g = Graph();

        world = Scenario();

        var (hx, hy) = BotSuppress.Centre(g, HeavyNode);
        var (seenX, seenY) = BotSuppress.Centre(g, SeenNode);
        var (hiddenX, hiddenY) = BotSuppress.Centre(g, HiddenNode);

        slot = world.Place(playerClass, Team.Red, hx, hy);
        var enemy = world.Place(PlayerClass.Scout, Team.Blue, seenX, seenY);

        // MOVING when it is last seen, and that is what makes the velocity
        // zeroing observable at all. An enemy that stands still is snapshotted at
        // zero and a port that forgot to clear the velocity looks identical.
        world.State.Characters[enemy].HSpeed = 7;
        world.State.Characters[enemy].VSpeed = -3;

        var state = new BotCombatState();
        var random = new GmlRandom(1);
        var view = new WorldView(in world.State, world.Collision);
        var tick = 0;

        // In the open: acquired, seen, and the snapshot taken.
        for (; tick < 60 && (!state.Target.Exists || !state.Visible || state.AimX == 0); tick++)
            BotCombat.Step(state, view, slot, tick, BotSkill.ForTier(5), ref random, graph, out _, out _);

        Assert.True(state.Target.Exists && state.Visible, "the heavy never saw the enemy in the open");

        Assert.NotEqual(0, state.SeeVx);

        lastSeenX = state.SeeX;
        lastSeenY = state.SeeY;

        // ...and behind the corner.
        world.State.Characters[enemy].X = hiddenX;
        world.State.Characters[enemy].Y = hiddenY;

        for (var stop = tick + 40; tick < stop && (state.Visible || state.AimX == lastSeenX); tick++)
            BotCombat.Step(state, view, slot, tick, BotSkill.ForTier(5), ref random, graph, out _, out _);

        LastTick = tick;
        return state;
    }

    /// <summary>
    /// A blind heavy aims at the doorway; a blind soldier keeps aiming at the wall.
    /// </summary>
    /// <remarks>
    /// <b>Opt-in per class</b>, because the walk is a graph search — the reference
    /// gates it on <c>BOT_CP_SUPPRESS</c>, which is the heavy alone. A soldier keeps
    /// the frozen snapshot, which is the pre-existing behaviour and is what makes the
    /// heavy's difference visible at all.
    /// <para>
    /// <b>And the velocity is zeroed.</b> The velocity that came with the snapshot
    /// belongs to a body that was moving when it was last seen; a doorway is not
    /// moving. Leading a fixed point by a dead man's velocity is how a suppression
    /// shot ends up in the far wall.
    /// </para>
    /// </remarks>
    [Fact]
    public void ABlindHeavyAimsAtTheDoorwayAndABlindSoldierDoesNot()
    {
        var graph = Graph();

        var heavy = Engage(PlayerClass.Heavy, graph, out _, out _, out var seenX, out var seenY);

        Assert.True(heavy.Target.Exists, "the heavy never acquired the enemy");
        Assert.False(heavy.Visible, "the heavy can see it, so there is nothing to suppress");
        Assert.True(heavy.SuppressNode >= 0, "the heavy found no doorway to deny");

        var (spotX, spotY) = BotSuppress.Centre(graph, heavy.SuppressNode);

        Assert.Equal(spotX, heavy.AimX, 3);
        Assert.Equal(spotY, heavy.AimY, 3);
        Assert.Equal(0, heavy.SeeVx);
        Assert.Equal(0, heavy.SeeVy);

        output.WriteLine(
            $"last seen ({seenX:0},{seenY:0}); heavy now aims at n{heavy.SuppressNode} ({spotX:0},{spotY:0})");

        // And it really moved off the stale position, which is the whole point.
        Assert.NotEqual(seenX, heavy.AimX);

        // The control: a soldier in the identical position keeps the stale
        // snapshot. Without it this passes on a change that moved every class's aim.
        var soldier = Engage(PlayerClass.Soldier, graph, out _, out _, out _, out _);

        Assert.True(soldier.Target.Exists);
        Assert.False(soldier.Visible);
        Assert.Equal(BotSuppress.NoSpot, soldier.SuppressNode);
        Assert.Equal(soldier.SeeX, soldier.AimX, 3);

        // And they really are aiming at different places, which the two
        // assertions above do not say on their own.
        Assert.NotEqual(heavy.AimX, soldier.AimX);
    }

    /// <summary>
    /// With no graph a heavy keeps the stale snapshot rather than failing.
    /// </summary>
    /// <remarks>
    /// A server whose map has no nav cache still runs bots — <c>NavCache.TryLoad</c>
    /// answers a quiet false — so every consumer of the graph has to degrade rather
    /// than throw. Here that means the pre-suppression behaviour.
    /// </remarks>
    [Fact]
    public void WithNoGraphAHeavyKeepsTheStaleSnapshot()
    {
        var state = Engage(PlayerClass.Heavy, graph: null, out _, out _, out _, out _);

        Assert.True(state.Target.Exists);
        Assert.Equal(BotSuppress.NoSpot, state.SuppressNode);
        Assert.Equal(state.SeeX, state.AimX, 3);
    }

    /// <summary>
    /// A heavy that can see its target does not suppress.
    /// </summary>
    /// <remarks>
    /// The whole feature is "what to do while blind". A heavy that suppressed with
    /// a clear line would be aiming at a doorway while the target stood in the open
    /// beside it.
    /// </remarks>
    [Fact]
    public void AHeavyThatCanSeeItsTargetDoesNotSuppress()
    {
        var graph = Graph();
        var world = Scenario();

        // Both on the same wide floor with 900px of clear line — the measured
        // open stretch ScriptedBotTests uses, and the opposite of this file's other
        // fixture.
        var slot = world.Place(PlayerClass.Heavy, Team.Red, 3570, 325);
        world.Place(PlayerClass.Scout, Team.Blue, 3570 + 250, 325);

        var state = new BotCombatState();
        var random = new GmlRandom(1);

        for (var tick = 0; tick < 90; tick++)
        {
            BotCombat.Step(
                state, new WorldView(in world.State, world.Collision), slot, tick,
                BotSkill.ForTier(5), ref random, graph, out _, out _);
        }

        Assert.True(state.Target.Exists);
        Assert.True(state.Visible, "the fixture's clear line is not clear");
        Assert.Equal(BotSuppress.NoSpot, state.SuppressNode);
    }

    /// <summary>
    /// The spot is recomputed on its own clock, not on the aim interval.
    /// </summary>
    /// <remarks>
    /// <b>The walk is a graph search and the aim interval is 2 ticks at the top
    /// tier</b>, so asking it there would be a search per bot per two frames on a
    /// server already near its budget with twelve bots. The answer is re-used between
    /// refreshes, which is also honest about what it is: a place, not a moving target.
    /// </remarks>
    [Fact]
    public void TheSpotIsRecomputedOnItsOwnClock()
    {
        var graph = Graph();
        var state = Engage(PlayerClass.Heavy, graph, out var world, out var slot, out _, out _);

        Assert.True(state.SuppressNode >= 0);

        var view = new WorldView(in world.State, world.Collision);
        var random = new GmlRandom(1);
        var deadline = state.SuppressAt;

        // A whole target period apart, so the aim interval (2 ticks at this tier)
        // has come round many times in between.
        Assert.True(deadline > LastTick, $"the deadline is {deadline}, already behind tick {LastTick}");

        // The claim is that the walk does NOT run on the aim interval, which is 2
        // ticks at this tier — so the window below crosses it many times over.
        Assert.True(deadline - LastTick > BotSkill.ForTier(5).AimInterval * 4);

        var held = 0;

        for (var tick = LastTick; tick < deadline; tick++)
        {
            BotCombat.Step(state, view, slot, tick, BotSkill.ForTier(5), ref random, graph, out _, out _);

            // Only while the target is still held and still blind. A permanently
            // broken sightline drops the target after the 30-tick memory window and
            // re-acquires it on the next target period, and a re-acquisition legitimately
            // recomputes — asserting through that measures the memory window instead.
            if (!state.Target.Exists || state.Visible)
                break;

            Assert.Equal(deadline, state.SuppressAt);
            held++;
        }

        Assert.True(held > BotSkill.ForTier(5).AimInterval * 2, $"only held for {held} ticks");

        output.WriteLine($"deadline {deadline}, unchanged across {held} ticks at an aim interval of {BotSkill.ForTier(5).AimInterval}");
    }
}
