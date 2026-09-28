using Gg2.Bots;
using Gg2.Protocol;
using Gg2.Simulation;
using Xunit.Abstractions;

namespace Gg2.Tests;

/// <summary>
/// Whether a bot can take one step sideways and still be standing — and the one
/// press in the bot layer that has to ask.
/// </summary>
/// <remarks>
/// <b>Every fixture here was swept for rather than chosen</b>, because the
/// three cases this distinguishes look identical from a map screenshot: ground
/// that continues, ground that stops, and a solid that is not ground at all.
/// A sweep of every floor row on <c>ctf_truefort</c> found 4,528 pit-edged spots,
/// 4,157 wall-edged and 1,110 where the choice of solidity list changes the
/// answer — the last of those is the only way to gate the filter, and no fixture
/// picked by eye would have been one.
/// </remarks>
public sealed class BotStepTests(ITestOutputHelper output)
{
    private const string Map = "ctf_truefort";

    /// <summary>
    /// A ledge: the ground continues to the right and stops to the left.
    /// </summary>
    /// <remarks>
    /// The pit floor is <b>216px</b> below — asserted, so the refusal is known
    /// to be a real gap rather than a near miss of the 28px tolerance. A mutation
    /// that widened <see cref="BotConstants.EngageDrop"/> would have to widen it
    /// nearly eightfold before this stopped biting.
    /// </remarks>
    private const double LedgeX = 1064;

    private const double LedgeY = 325;

    /// <summary>A wall 16px to the right, with sound floor under both.</summary>
    private const double WallX = 684;

    /// <summary>
    /// A spot whose left-hand step lands on a <c>TeamGate</c>.
    /// </summary>
    /// <remarks>
    /// The floor continues under it, so the step is safe against <c>Obstacle</c>
    /// and refused against everything a character collides with. That difference
    /// is the whole reason the filter is named.
    /// </remarks>
    private const double GateX = 682;

    private static ScenarioWorld Load() =>
        ScenarioWorld.Load(Map, 1, ReferenceTree.MapsDirectory, ReferenceTree.NavCacheDirectory);

    /// <summary>A character of a class, put exactly where asked.</summary>
    /// <remarks>
    /// <c>ScenarioWorld.Place</c> snaps downward to a nav node, so the
    /// coordinates are overwritten afterwards: these fixtures are geometry the
    /// sweep found, and none of them is a node's own stand position.
    /// </remarks>
    private static int At(ScenarioWorld world, PlayerClass playerClass, Team team, double x, double y)
    {
        var slot = world.Place(playerClass, team, x, y);

        world.State.Characters[slot].X = x;
        world.State.Characters[slot].Y = y;
        world.State.Characters[slot].OnGround = true;

        return slot;
    }

    /// <summary>
    /// The ground continues one way and stops the other.
    /// </summary>
    [Fact]
    public void AStepOntoGroundIsSafeAndAStepOverAPitIsNot()
    {
        var world = Load();
        var slot = At(world, PlayerClass.Pyro, Team.Red, LedgeX, LedgeY);
        ref readonly var self = ref world.State.Characters[slot];

        var obstacles = CollisionFilter.ForSentry();

        // The premise: nothing is in the way either side, so this is about the
        // FLOOR and not about a wall.
        Assert.True(world.Collision.PlaceFree(self.Box, LedgeX - BotConstants.EngageStep, LedgeY, in obstacles));
        Assert.True(world.Collision.PlaceFree(self.Box, LedgeX + BotConstants.EngageStep, LedgeY, in obstacles));

        // And the gap is a real one.
        var depth = 1;
        while (depth < 900
            && world.Collision.PlaceFree(self.Box, LedgeX - BotConstants.EngageStep, LedgeY + depth, in obstacles))
        {
            depth++;
        }

        output.WriteLine($"the pit beside ({LedgeX}, {LedgeY}) bottoms out {depth}px down");
        Assert.True(depth > 200, $"the pit is only {depth}px deep, so the tolerance is barely gated");

        Assert.True(BotStep.Safe(world.Collision, in self, 1), "the step onto solid ground was refused");
        Assert.False(BotStep.Safe(world.Collision, in self, -1), "the step over a 216px pit was allowed");
    }

    /// <summary>
    /// A step DOWN to a lower ledge still reads as safe.
    /// </summary>
    /// <remarks>
    /// <b>The drop tolerance is what makes this useful rather than merely
    /// safe.</b> The question is not "can the bot get there" — that is
    /// <c>navFindPath</c>'s, answered against the graph — but only "does the
    /// ground continue", so a bot must be willing to step off a kerb. Without the
    /// tolerance every step down reads as a pit and a pyro pinned on a raised
    /// slab can never close on anything.
    /// <para>
    /// The fixture is a step whose landing has <em>nothing</em> directly under it
    /// at the bot's own level, asserted below, so a port that tested the floor at
    /// <c>y</c> rather than at <c>y + BOT_ENGAGE_DROP</c> answers false.
    /// </para>
    /// </remarks>
    [Fact]
    public void AStepDownToALowerLedgeIsSafe()
    {
        var world = Load();
        var slot = At(world, PlayerClass.Pyro, Team.Red, LedgeX, LedgeY);
        ref readonly var self = ref world.State.Characters[slot];

        var obstacles = CollisionFilter.ForSentry();
        var ahead = LedgeX + BotConstants.EngageStep;

        Assert.True(
            world.Collision.PlaceFree(self.Box, ahead, LedgeY + 6, in obstacles),
            "the landing has ground at the bot's own level, so this is not a step down");

        Assert.True(BotStep.Safe(world.Collision, in self, 1));
    }

    /// <summary>
    /// A wall ahead is not worth pressing into.
    /// </summary>
    /// <remarks>
    /// Not unsafe — there is sound floor under it, asserted below — but the press
    /// achieves nothing and the follower's stuck detector counts it. So the wall
    /// test is a separate refusal from the floor test and a port with only the
    /// floor test answers true here.
    /// </remarks>
    [Fact]
    public void AWallAheadIsNotWorthPressingInto()
    {
        var world = Load();
        var slot = At(world, PlayerClass.Pyro, Team.Red, WallX, LedgeY);
        ref readonly var self = ref world.State.Characters[slot];

        var obstacles = CollisionFilter.ForSentry();
        var ahead = WallX + BotConstants.EngageStep;

        Assert.False(
            world.Collision.PlaceFree(self.Box, ahead, LedgeY, in obstacles),
            "there is no wall here, so this gates nothing");

        Assert.False(
            world.Collision.PlaceFree(self.Box, ahead, LedgeY + BotConstants.EngageDrop, in obstacles),
            "the floor test alone would refuse this, so the wall test is unobservable");

        Assert.False(BotStep.Safe(world.Collision, in self, 1));

        // The control: the other way, over the same floor, is fine.
        Assert.True(BotStep.Safe(world.Collision, in self, -1));
    }

    /// <summary>
    /// Only <c>Obstacle</c> counts — a team gate is neither wall nor floor.
    /// </summary>
    /// <remarks>
    /// <b>The reference asks <c>place_meeting(x, y, Obstacle)</c> and not
    /// <c>place_free</c>, and names the object for a reason its own comment
    /// gives: <c>place_free</c> reads GM8's <c>solid</c> flag and the game toggles
    /// that flag underneath itself, so what it answers depends on where in the
    /// frame it is asked.</b> This port has no global flag to toggle, but the
    /// object list still has to be the right one.
    /// <para>
    /// The fixture is a spot whose left-hand step lands on a <c>TeamGate</c> with
    /// sound floor under it. Against <c>Obstacle</c> the step is safe; against
    /// everything a character collides with it is refused — so a port that used
    /// the character's own solidity list would have a pyro refuse to walk through
    /// its own team's gate.
    /// </para>
    /// </remarks>
    [Fact]
    public void OnlyObstaclesCountAndATeamGateIsNeither()
    {
        var world = Load();
        var slot = At(world, PlayerClass.Pyro, Team.Red, GateX, LedgeY);
        ref readonly var self = ref world.State.Characters[slot];

        var ahead = GateX - BotConstants.EngageStep;

        // The premise: there really is a non-Obstacle solid in the way.
        Assert.True(
            world.Collision.PlaceMeetingKind(self.Box, ahead, LedgeY, SolidKind.TeamGate),
            "no gate here, so the two solidity lists agree and this gates nothing");

        var everything = CollisionFilter.Everything;
        Assert.False(
            world.Collision.PlaceFree(self.Box, ahead, LedgeY, in everything),
            "the gate is not solid to anything, so the lists agree");

        Assert.True(BotStep.Safe(world.Collision, in self, -1), "a team gate was treated as a wall");
    }

    /// <summary>
    /// A pyro walks at its target when the ground holds, and does not walk off a
    /// ledge to reach one.
    /// </summary>
    /// <remarks>
    /// <b>This is the only press in the bot layer that no route validated.</b>
    /// Everything else that moves a bot sideways is either an edge the nav
    /// generator proved or a single step of back-off inside its own blast radius.
    /// <para>
    /// The window is narrow by construction and the fixture has to sit inside
    /// it: the hold applies within <c>ClassRange</c> (200 for a pyro) and the
    /// close-in step fires beyond <c>BOT_FLAME_REACH</c> (165), so the enemy has
    /// to be in a 35px band. 180px, either side of the ledge.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(1, true)]
    [InlineData(-1, false)]
    public void APyroClosesOnItsTargetButNotAcrossAPit(int side, bool expectPress)
    {
        var world = Load();
        var graph = NavCache.Load(ReferenceTree.NavCacheDirectory, Map, 1);

        var slot = At(world, PlayerClass.Pyro, Team.Red, LedgeX, LedgeY);
        At(world, PlayerClass.Scout, Team.Blue, LedgeX + (side * 180), LedgeY);

        var bot = new ScriptedBot(graph, new NavQuery(Team.Red), BotSkill.ForTier(5), 0);

        var keys = PlayerKeys.None;
        var acquired = false;

        for (var tick = 0; tick < 90; tick++)
        {
            keys = bot.Poll(new WorldView(in world.State, world.Collision), slot, tick).Keys;

            if (bot.HasTarget && tick > 20)
                acquired = true;
        }

        Assert.True(acquired, "the pyro never saw the enemy, so the hold was never reached");

        var pressed = keys & (PlayerKeys.Left | PlayerKeys.Right);
        output.WriteLine($"enemy {(side > 0 ? "right" : "left")}: pressed {pressed}");

        if (expectPress)
            Assert.Equal(side > 0 ? PlayerKeys.Right : PlayerKeys.Left, pressed);
        else
            Assert.Equal(PlayerKeys.None, pressed);
    }

    /// <summary>
    /// A class with no close-in distance stands where the hold caught it.
    /// </summary>
    /// <remarks>
    /// The control that keeps this a <em>pyro</em> behaviour: only the pyro's
    /// weapon reaches a fraction of the distance it can see, so only the pyro has
    /// a reason to walk into a fight the engage hold just stopped it walking past.
    /// </remarks>
    [Fact]
    public void AClassWithNoCloseInDistanceHoldsWhereItIs()
    {
        var world = Load();
        var graph = NavCache.Load(ReferenceTree.NavCacheDirectory, Map, 1);

        var slot = At(world, PlayerClass.Heavy, Team.Red, LedgeX, LedgeY);
        At(world, PlayerClass.Scout, Team.Blue, LedgeX + 180, LedgeY);

        Assert.Equal(0, BotClassProfile.CloseIn(PlayerClass.Heavy));

        var bot = new ScriptedBot(graph, new NavQuery(Team.Red), BotSkill.ForTier(5), 0);

        var keys = PlayerKeys.None;

        for (var tick = 0; tick < 90; tick++)
            keys = bot.Poll(new WorldView(in world.State, world.Collision), slot, tick).Keys;

        Assert.True(bot.HasTarget, "the heavy never saw the enemy, so the hold was never reached");
        Assert.Equal(PlayerKeys.None, keys & (PlayerKeys.Left | PlayerKeys.Right));
    }
}
