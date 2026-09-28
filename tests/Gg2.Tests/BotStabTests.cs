using Gg2.Bots;
using Gg2.Protocol;
using Gg2.Simulation;
using Xunit.Abstractions;

namespace Gg2.Tests;

/// <summary>
/// When a spy presses ATTACK — <c>botStabWindow</c>.
/// </summary>
/// <remarks>
/// <b>Every test here is about the gap between where the enemy is and where it
/// will be.</b> The hitbox does not exist until 32 ticks after the press, lives 6
/// more, and the spy is frozen in place for all of it — so the obvious test, "the
/// enemy is within <c>BOT_STAB_RANGE</c> right now", is aimed a full second early
/// and is close to a guaranteed miss against anything moving.
/// </remarks>
public sealed class BotStabTests(ITestOutputHelper output)
{
    private const string Map = "ctf_truefort";

    /// <summary>The measured wide floor — see <c>ScriptedBotTests</c>.</summary>
    private const double StandX = 3570;

    private const double StandY = 325;

    private static NavGraph Graph()
    {
        ReferenceTree.RequireNavCache();
        return NavCache.Load(ReferenceTree.NavCacheDirectory, Map, 1);
    }

    /// <summary>A spy at the origin of the fixture, aimed a way.</summary>
    private static CharacterState Spy(double degrees = 0)
    {
        var self = CharacterState.Fresh(PlayerClass.Spy, Team.Red);
        self.X = StandX;
        self.Y = StandY;
        self.Cloak = true;
        self.NetAimDirection = (ushort)(degrees * 65536.0 / 360.0);
        return self;
    }

    /// <summary>An enemy at an offset from the spy, moving at a velocity.</summary>
    private static CharacterState Enemy(double dx, double dy = 0, double vx = 0, double vy = 0)
    {
        var them = CharacterState.Fresh(PlayerClass.Scout, Team.Blue);
        them.X = StandX + dx;
        them.Y = StandY + dy;
        them.Motion.HSpeed = vx;
        them.Motion.VSpeed = vy;
        return them;
    }

    /// <summary>
    /// The window is where the target WILL be, not where it is.
    /// </summary>
    /// <remarks>
    /// A spy that stabbed at whoever was inside <c>BOT_STAB_RANGE</c> now would
    /// take the first case — freezing itself for 38 ticks to knife a patch of air
    /// the target left long before — and refuse the second, which is the one that
    /// lands.
    /// <para>
    /// Both fixtures use the same speed and the same distance, mirrored, so the
    /// only thing separating them is which way the target is going.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheWindowIsWhereTheTargetWillBeAndNotWhereItIs()
    {
        var graph = Graph();
        var spy = Spy();

        // Standing on the knife now, running away at 4px/tick: 128px away by t+32.
        var leaving = Enemy(dx: 10, vx: 4);

        // A long way off now, running in: 118px away, arriving at t+32.
        var arriving = Enemy(dx: 138, vx: -4);

        // The premise, which is what the old test would have measured.
        Assert.True(Gml.PointDistance(spy.X, spy.Y, leaving.X, leaving.Y) <= BotConstants.StabRange);
        Assert.True(Gml.PointDistance(spy.X, spy.Y, arriving.X, arriving.Y) > BotConstants.StabRange);

        Assert.False(
            BotStab.Window(graph, in spy, in leaving),
            "the spy froze itself for 38 ticks to knife a patch of air");

        Assert.True(
            BotStab.Window(graph, in spy, in arriving),
            "the spy refused the one stab that was going to land");
    }

    /// <summary>
    /// The box is the sprite, not a radius: 37 forward and 26 behind.
    /// </summary>
    /// <remarks>
    /// <c>StabMaskS</c> is 64×64 with origin (26, 37), and the mask's Step sets
    /// <c>image_xscale</c> from <c>direction</c> without ever touching
    /// <c>image_angle</c> — so it is axis-aligned and merely flipped. A radius
    /// would answer the same for both of these.
    /// <para>
    /// The offsets are measured from the target's own <b>edge</b>, because the
    /// mask collides mask-to-mask: the fixture puts the near edge of the enemy's
    /// box one pixel outside the reach either way, so the pair straddles the
    /// boundary rather than sitting comfortably on both sides of it.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheBoxReachesFurtherForwardThanBehind()
    {
        var graph = Graph();
        var spy = Spy();
        var box = CharacterState.Fresh(PlayerClass.Scout, Team.Blue).Box;

        // Just inside the forward reach, and the same gap behind — which is
        // outside, because behind is 26 rather than 37.
        var ahead = Enemy(dx: BotConstants.StabReach - box.Left - 1);
        var behind = Enemy(dx: -(BotConstants.StabReach - box.Left - 1));

        output.WriteLine($"scout box {box.Left}..{box.Right}, ahead dx {ahead.X - spy.X:0}, behind dx {behind.X - spy.X:0}");

        Assert.True(BotStab.Window(graph, in spy, in ahead));
        Assert.False(BotStab.Window(graph, in spy, in behind));

        // The control: the same enemy, with the spy turned round.
        var turned = Spy(180);

        Assert.False(BotStab.Window(graph, in turned, in ahead));
        Assert.True(BotStab.Window(graph, in turned, in behind));
    }

    /// <summary>
    /// Facing comes from the aim, and 90 and 270 both face left.
    /// </summary>
    /// <remarks>
    /// <c>aimDirection &gt;= 90 and aimDirection &lt;= 270</c> — <b>inclusive at
    /// both ends</b>, which is what decides which way the box is 37 long on a spy
    /// looking straight up or straight down. Written with either bound exclusive,
    /// a spy aiming dead vertical reaches the wrong way.
    /// </remarks>
    [Theory]
    [InlineData(0.0, true)]
    [InlineData(89.0, true)]
    [InlineData(90.0, false)]
    [InlineData(180.0, false)]
    [InlineData(270.0, false)]
    [InlineData(271.0, true)]
    public void FacingIsInclusiveAtNinetyAndTwoSeventy(double aim, bool reachesRight)
    {
        var graph = Graph();
        var spy = Spy(aim);
        var box = CharacterState.Fresh(PlayerClass.Scout, Team.Blue).Box;

        var right = Enemy(dx: BotConstants.StabReach - box.Left - 1);

        Assert.Equal(reachesRight, BotStab.Window(graph, in spy, in right));
    }

    /// <summary>
    /// The target is grown by its own bounds rather than treated as a point.
    /// </summary>
    /// <remarks>
    /// <b>The mask collides mask-to-mask</b>, so a character whose centre is
    /// outside the box still overlaps it by a shoulder. The fixture puts the
    /// centre past the reach and the near edge inside it, so a port that compared
    /// points answers false.
    /// </remarks>
    [Fact]
    public void ATargetIsGrownByItsOwnBounds()
    {
        var graph = Graph();
        var spy = Spy();
        var box = CharacterState.Fresh(PlayerClass.Scout, Team.Blue).Box;

        // Centre past the reach, near edge inside it.
        var dx = BotConstants.StabReach + 2;
        var shoulder = Enemy(dx);

        Assert.True(dx > BotConstants.StabReach, "the centre is inside, so the bounds do no work");
        Assert.True(dx + box.Left < BotConstants.StabReach, "the shoulder is outside too, so nothing can connect");

        Assert.True(BotStab.Window(graph, in spy, in shoulder));

        // The control: far enough that even the shoulder misses.
        Assert.False(BotStab.Window(graph, in spy, Enemy(BotConstants.StabReach - box.Left + 2)));
    }

    /// <summary>
    /// A falling target is predicted onto the floor, not through it.
    /// </summary>
    /// <remarks>
    /// <b>A target running off a ledge is exactly the case a straight-line
    /// prediction gets wrong, and at knife range it is common.</b> Over 32 ticks a
    /// falling body covers hundreds of pixels; with the floor under it, it lands
    /// and stays in the knife.
    /// <para>
    /// The control is the same fixture with <b>no graph</b>, which is what a
    /// target over a pit gets: no floor, so it is predicted to keep falling and
    /// the stab is refused.
    /// </para>
    /// </remarks>
    [Fact]
    public void AFallingTargetIsPredictedOntoTheFloor()
    {
        var graph = Graph();
        var spy = Spy();

        // Standing where the spy is, falling.
        var falling = Enemy(dx: 8, vy: 4);

        Assert.True(
            BotStab.Window(graph, in spy, in falling),
            "the target was predicted through the floor it is about to land on");

        Assert.False(
            BotStab.Window(null, in spy, in falling),
            "with no floor the target should be predicted to keep falling out of the knife");
    }

    /// <summary>
    /// A cloaked spy with the knife ready stabs; without it, it does not.
    /// </summary>
    /// <remarks>
    /// <c>readyToStab</c> is tested in the class policy rather than inside the
    /// window because it is much the cheaper of the two and gates the press
    /// regardless of geometry.
    /// <para>
    /// <c>BotClassKeys.For</c> rather than the whole combat step: the trigger
    /// sits behind the fire delay, the aim settle and the hold-fire roll, and the
    /// last of those makes a pipeline test intermittent.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void ACloakedSpyStabsOnlyWhenTheKnifeIsReadyAndWouldLand(
        bool ready, bool willLand, bool expectAttack)
    {
        var graph = Graph();
        var world = ScenarioWorld.Load(Map, 1, ReferenceTree.MapsDirectory, ReferenceTree.NavCacheDirectory);

        var spy = world.Place(PlayerClass.Spy, Team.Red, StandX, StandY);
        var enemy = world.Place(PlayerClass.Scout, Team.Blue, StandX + 100, StandY);

        var (sx, sy) = (world.State.Characters[spy].X, world.State.Characters[spy].Y);

        world.State.Characters[spy].Cloak = true;
        world.State.Characters[spy].NetAimDirection = 0;
        world.State.Weapons[spy].ReadyToStab = ready;

        // In the knife when the knife exists, or nowhere near it.
        world.State.Characters[enemy].X = willLand ? sx + 20 : sx + 400;
        world.State.Characters[enemy].Y = sy;
        world.State.Characters[enemy].Motion.HSpeed = 0;

        var state = new BotCombatState
        {
            Target = new BotTarget(
                BotTargetKind.Character, enemy,
                world.State.Characters[enemy].X, world.State.Characters[enemy].Y, Visible: true),
        };

        var view = new WorldView(in world.State, world.Collision);
        var distance = Gml.PointDistance(sx, sy, world.State.Characters[enemy].X, sy);

        var keys = BotClassKeys.For(state, view, spy, distance, 0, graph: graph);

        output.WriteLine($"ready {ready}, would land {willLand}: {keys}");
        Assert.Equal(expectAttack, (keys & PlayerKeys.Attack) != 0);
    }

    /// <summary>
    /// An uncloaked spy shoots rather than stabbing.
    /// </summary>
    /// <remarks>
    /// The stab arm is reachable only while cloaked — the Revolver's
    /// <c>User Event 1</c> is an if/else, and there is no separate melee input.
    /// This is the control that keeps the new branch out of the shooting one.
    /// </remarks>
    [Fact]
    public void AnUncloakedSpyShootsRatherThanStabbing()
    {
        var graph = Graph();
        var world = ScenarioWorld.Load(Map, 1, ReferenceTree.MapsDirectory, ReferenceTree.NavCacheDirectory);

        var spy = world.Place(PlayerClass.Spy, Team.Red, StandX, StandY);
        var enemy = world.Place(PlayerClass.Scout, Team.Blue, StandX + 100, StandY);

        var (sx, sy) = (world.State.Characters[spy].X, world.State.Characters[spy].Y);

        world.State.Characters[spy].Cloak = false;
        world.State.Weapons[spy].ReadyToStab = true;
        world.State.Characters[enemy].X = sx + 20;
        world.State.Characters[enemy].Y = sy;

        var state = new BotCombatState
        {
            Target = new BotTarget(BotTargetKind.Character, enemy, sx + 20, sy, Visible: true),
            CloakAt = 0,
        };

        // Inside BOT_CLOAK_PERIOD of the last toggle, or the spy re-cloaks
        // instead of shooting — `cloak == enemyNear` is false here, so the toggle
        // branch is not taken anyway, but pinning it keeps the test about the stab.
        var keys = BotClassKeys.For(
            state, new WorldView(in world.State, world.Collision), spy, 20, 1, graph: graph);

        Assert.Equal(PlayerKeys.Attack, keys & (PlayerKeys.Attack | PlayerKeys.Special));
    }
}
