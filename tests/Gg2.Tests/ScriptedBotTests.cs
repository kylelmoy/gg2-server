using Gg2.Bots;
using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Server;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// A whole bot: navigation and combat ORed into one input, driving a real
/// character.
/// </summary>
/// <remarks>
/// <c>botInputUpdate.gml</c>, and the point at which everything phase 7 built
/// actually runs. ⚠️ The end-to-end test goes through a real
/// <c>GameServer</c> and reads the result off the <b>broadcast stream</b>,
/// because "a client cannot tell a bot from a socket" is the claim the whole seam
/// rests on and a test that reached into the server would not check it.
/// </remarks>
public sealed class ScriptedBotTests
{
    private const string Map = "ctf_truefort";

    /// <summary>
    /// A wide floor on ctf_truefort with a clear sightline along it.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Measured by sweeping every node</b>, and it is NOT the open stretch
    /// <see cref="BotTargetingTests"/> uses. That one — (380, 268) — was found by
    /// looking for the widest unblocked horizontal line, which is a point in the
    /// <em>air</em>: <c>NodeFromWorld</c> answers -1 there, so a follower cannot
    /// plan from it and a bot placed there never moves. And the floor below it is
    /// worse than useless for combat — the horizontal line at chest height on
    /// that floor is <em>blocked</em>, so two bots 60px apart cannot see each
    /// other.
    /// <para>
    /// What a bot test needs is both at once: somewhere a character can stand,
    /// wide enough to walk along, with a clear line to shoot down. Node 14 is 97
    /// cells wide, spans world x 3,282-3,864, and has 900px of clear sightline
    /// from its middle. Three tests failed on the wrong one before this was
    /// measured.
    /// </para>
    /// </remarks>
    private const double StandX = 3570;

    private const double StandY = 325;

    /// <summary>How far either way a placement can go and stay on node 14.</summary>
    private const double OnFloor = 280;

    private static NavGraph Graph()
    {
        ReferenceTree.RequireNavCache();
        return NavCache.Load(ReferenceTree.NavCacheDirectory, Map, 1);
    }

    private static ScenarioWorld Load()
    {
        ReferenceTree.RequireAvailable();
        ReferenceTree.RequireNavCache();

        return ScenarioWorld.Load(Map, 1, ReferenceTree.IncludedFiles, ReferenceTree.NavCacheDirectory);
    }

    private static ScriptedBot Bot(NavGraph? graph, Team team = Team.Red, int tier = 5, int stagger = 0) =>
        new(graph, new NavQuery(team), BotSkill.ForTier(tier), stagger);

    /// <summary>
    /// A place a character can actually STAND, under the open stretch.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>(LeftX, FloorY) is a clear SIGHTLINE, not a standing position.</b> It
    /// was measured by sweeping for the widest unblocked horizontal line, which
    /// is exactly what a targeting test wants and exactly what a movement test
    /// must not use: <c>NodeFromWorld</c> answers -1 eighty pixels above a floor,
    /// so the follower cannot plan from it and the bot never moves — which looks
    /// like every behaviour under test being broken at once. Three tests failed
    /// that way before this existed.
    /// </remarks>
    private static (double X, double Y) Stand(NavGraph graph, double offset = 0)
    {
        var snap = graph.SnapObjective(StandX + offset, StandY);
        Assert.True(snap.Node >= 0, $"nothing to stand on below ({StandX + offset}, {StandY})");

        return (snap.GoalX, snap.GoalY);
    }

    /// <summary>Decodes the broadcast the way a real client frames it.</summary>
    private sealed class BroadcastReader
    {
        private readonly ServerStream _stream = new();
        private readonly ReceiveBuffer _buffer = new();

        public BroadcastReader(LevelMap map) => _stream.SetShape(GameModes.ShapeOf(map));

        public List<ServerMessage> Drain(LoopbackClient client)
        {
            client.Poll();
            _buffer.Append(client.Take(client.Received.Count));

            var messages = new List<ServerMessage>();
            while (_stream.TryRead(_buffer, out var message))
                messages.Add(message);

            return messages;
        }
    }

    /// <summary>
    /// A bot walks to a goal and stops on it.
    /// </summary>
    /// <remarks>
    /// The whole phase in one test: the seam takes it, the combat half aims it,
    /// the follower routes it, and the simulation moves it. ⚠️ The control is a
    /// bot with <b>no goal</b> — it must not move — because a bot that walked
    /// regardless would pass every other assertion here.
    /// <para>
    /// That a bot's input reaches a real server and the wire is
    /// <c>InputSourceTests</c>'s claim and is not repeated here; what is new is
    /// that <see cref="ScriptedBot"/> produces an input worth carrying.
    /// </para>
    /// </remarks>
    [Fact]
    public void ABotWalksToItsGoalAndStops()
    {
        var world = Load();
        var graph = world.Nav!;

        var here = Stand(graph, -OnFloor);
        var slot = world.Place(PlayerClass.Soldier, Team.Red, here.X, here.Y);

        var bot = Bot(graph, stagger: 1);
        world.Drive(slot, bot);

        var goal = Stand(graph, OnFloor);
        Assert.True(bot.SetGoal(goal.X, goal.Y));

        // ⚠️ The regroup hold is randomised per life and is not what this test is
        // about; a bot that already existed last tick does not start one.
        bot.AliveLast = true;

        for (var i = 0; i < 600 && !bot.Path.Arrived; i++)
            world.Step();

        Assert.True(
            bot.Path.Arrived,
            $"did not arrive: {Math.Abs(world.State.Characters[slot].X - goal.X):0.#}px short, "
            + $"replans {bot.Path.Replans}, stuck {bot.Path.StuckFires}, "
            + $"off-route {bot.Path.OffRouteFires}");

        Assert.True(Math.Abs(world.State.Characters[slot].X - goal.X) <= BotConstants.ArriveTol);

        // ⚠️ The control: the same bot with no goal goes nowhere at all.
        var idle = Load();
        var idleSlot = idle.Place(PlayerClass.Soldier, Team.Red, here.X, here.Y);
        var still = Bot(idle.Nav, stagger: 1);

        idle.Drive(idleSlot, still);
        still.AliveLast = true;

        var before = idle.State.Characters[idleSlot].X;
        idle.Step(200);

        Assert.Equal(before, idle.State.Characters[idleSlot].X);
    }

    /// <summary>
    /// A bot with an enemy in front of it acquires, fires, and does damage.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>In a scenario world rather than on the server, and that is not a
    /// shortcut.</b> A server spawns its bots at the map's own spawn points —
    /// opposite ends of ctf_truefort, well past a soldier's 800px attention
    /// radius — so two bots added to a server never see each other at all until
    /// something walks them together. Placing them 300px apart is the only way to
    /// ask this question without also asking a routing one.
    /// <para>
    /// The wire half of the claim is already gated: a bot's <c>WEAPON_FIRE</c>
    /// goes out through the same <c>GameServer</c> path a client's does, which
    /// <c>InputSourceTests</c> covers for the command channel and
    /// <see cref="ABotOnARealServerWalksToItsGoal"/> covers for the input one.
    /// </para>
    /// </remarks>
    [Fact]
    public void ABotFiresAtAnEnemyAndHurtsIt()
    {
        var world = Load();

        // ⚠️ The graph is given but no goal is, which is what keeps this a combat
        // test: a bot with no goal never plans and never walks.
        //
        // ⚠️ It must NOT be null. The aim solver clamps a target's predicted fall
        // at the floor under it, and without a graph there is no floor — so a
        // target standing perfectly still is predicted to keep falling and the
        // bot aims below its feet, into the ground. A first draft passed null
        // here and the soldier acquired, fired, and never damaged anything.
        var bot = Bot(world.Nav, tier: 5, stagger: 1);

        var here = Stand(world.Nav!);
        var shooter = world.Place(PlayerClass.Soldier, Team.Red, here.X - OnFloor, here.Y);
        var dummy = world.Place(PlayerClass.Heavy, Team.Blue, here.X - OnFloor + 250, here.Y);

        world.Drive(shooter, bot);

        var startHp = world.State.Characters[dummy].Hp;
        var fired = false;

        for (var i = 0; i < 400 && !fired; i++)
        {
            world.Step();

            for (var slot = 0; slot < WorldState.MaxSlots; slot++)
            {
                if (world.Events.Fired[slot])
                    fired = true;
            }
        }

        Assert.True(bot.Combat.Target.Exists, "the bot never acquired the dummy");
        Assert.True(fired, "the bot acquired a target and never fired");

        world.Step(120);

        Assert.True(
            world.State.Characters[dummy].Hp < startHp,
            $"the dummy is still on {world.State.Characters[dummy].Hp} of {startHp}");
    }

    /// <summary>
    /// A rocket jump snaps the aim down and pulls the trigger, overriding combat.
    /// </summary>
    /// <remarks>
    /// <b>The hand-off, and the only place that can make it.</b> The follower owns
    /// LEFT / RIGHT / JUMP / DOWN and cannot fire; combat owns ATTACK and the aim
    /// and knows nothing about the route. A rocket jump needs one key from each, so
    /// it is applied here after both halves have run.
    /// <para>
    /// ⚠️ It OVERRIDES the combat aim rather than being ORed with it. Whatever
    /// the bot was shooting at, the arc it is about to fly is worth more than the
    /// shot, and half an aim is a rocket into a wall beside its own feet.
    /// Character's Begin Step reads <c>aimDirection</c> when it fires the weapon and
    /// reads the ATTACK bit in the same event on the same tick, so the aim must be
    /// down BEFORE the trigger is seen.
    /// </para>
    /// <para>
    /// ⚠️ 270 is straight down and is not a simplification of a richer choice.
    /// <c>vectorfactor</c> in the rocket's blast is sqrt(sin⁴ + cos⁴), which is 1
    /// only when the impulse is axis-aligned and falls to 0.707 at 45°, so an angled
    /// rocket jump throws away up to 30% of its impulse. It also SNAPS rather than
    /// slewing: the turn rate is 8 degrees a tick and level-to-down is 90, so
    /// honouring the slew would mean eleven ticks of a bot staring at the floor
    /// before every rocket jump.
    /// </para>
    /// </remarks>
    [Fact]
    public void ARocketJumpSnapsTheAimDownAndFires()
    {
        var world = Load();

        // ⚠️ NO graph, and that is what isolates the hand-off. The follower CLEARS
        // Path.RocketFire at the top of every Step and sets it only when its own
        // takeoff gate fires — which is right, and which means a test that plants
        // the request and then polls a routed bot has it wiped before Poll ever
        // reads it. Measured: the aim came back at 359.76, the enemy's bearing.
        // With no graph the follower does not run, the request survives, and what
        // is left under test is exactly the thing this file owns.
        // PathFollowerTests gates the follower's half.
        var bot = Bot(graph: null, stagger: 1);

        var here = Stand(world.Nav!, -OnFloor);
        var slot = world.Place(PlayerClass.Soldier, Team.Red, here.X, here.Y);

        world.Drive(slot, bot);
        bot.AliveLast = true;

        // An enemy well to the RIGHT, so combat's own aim is nowhere near straight
        // down and the override is visible.
        world.Place(PlayerClass.Heavy, Team.Blue, here.X + 300, here.Y);

        world.Step(60);

        var beforeAim = bot.Combat.AimDir;
        Assert.True(
            Math.Abs(BotTargeting.AngleDelta(beforeAim, PathFollower.RocketAimDegrees)) > 45,
            $"combat was already aiming near straight down at {beforeAim:0.#}, so the override is invisible");

        // Ask for the rocket the way the follower does, and poll one tick.
        bot.Path.RocketFire = true;

        var input = bot.Poll(new WorldView(in world.State, world.Collision), slot, 61);

        Assert.Equal(PathFollower.RocketAimDegrees, input.AimDirectionDegrees, 3);
        Assert.True((input.Keys & PlayerKeys.Attack) != 0, "the trigger was not pulled");

        // ⚠️ The control — without the request the same tick aims at the enemy
        // and not at the floor. Without it this passes on a bot that always aims
        // down.
        bot.Path.RocketFire = false;

        var plain = bot.Poll(new WorldView(in world.State, world.Collision), slot, 62);

        Assert.True(
            Math.Abs(BotTargeting.AngleDelta(plain.AimDirectionDegrees, PathFollower.RocketAimDegrees)) > 45,
            $"the bot aimed at {plain.AimDirectionDegrees:0.#} with no rocket asked for");
    }

    /// <summary>Presses nothing, for ever.</summary>
    private sealed class Idle : IInputSource
    {
        public PlayerInput Poll(in WorldView world, int slot, int tick) => PlayerInput.None;
    }

    /// <summary>
    /// The two halves are ORed, and neither can take the other's keys away.
    /// </summary>
    /// <remarks>
    /// ⚠️ Combat owns ATTACK and SPECIAL, the follower owns LEFT / RIGHT / JUMP /
    /// DOWN — which is what makes the OR safe. A bot walking <em>and</em> shooting
    /// is the ordinary case, and a port that let either half clear the other's
    /// bits would have a bot that stops to fire or fires only when standing
    /// still.
    /// </remarks>
    [Fact]
    public void TheTwoHalvesOrWithoutTakingEachOthersKeys()
    {
        var world = Load();
        var graph = world.Nav!;

        // ⚠️ The goal is deliberately CLOSE — inside BOT_ENGAGE_GOAL_NEAR — and
        // the enemy deliberately far. The engage hold exists precisely to stop a
        // bot walking past a fight, so with a distant goal the correct behaviour
        // is to STOP walking and this test would be asserting the absence of a
        // feature. Near its own goal a bot keeps walking, because taking the
        // point beats winning the fight in front of it.
        var here = Stand(graph, -OnFloor);
        var shooter = world.Place(PlayerClass.Soldier, Team.Red, here.X, here.Y);
        world.Place(PlayerClass.Heavy, Team.Blue, here.X + 300, here.Y);

        var bot = Bot(graph, stagger: 1);
        world.Drive(shooter, bot);

        var goal = Stand(graph, -OnFloor + 150);
        Assert.True(bot.SetGoal(goal.X, goal.Y));

        // ⚠️ The regroup hold would keep it standing for up to ninety ticks, and
        // this test is not about that.
        bot.AliveLast = true;

        var sawBoth = false;

        for (var i = 0; i < 400 && !sawBoth; i++)
        {
            world.Step();

            // ⚠️ Read off the CHARACTER's own key state, which is what the
            // simulation applied — not off the bot's two halves, which would be
            // asserting that the OR in ScriptedBot does what the OR in
            // ScriptedBot does.
            var input = world.State.Characters[shooter].KeyState;

            if ((input & PlayerKeys.Attack) != 0
                && (input & (PlayerKeys.Left | PlayerKeys.Right)) != 0)
            {
                sawBoth = true;
            }
        }

        Assert.True(
            sawBoth,
            "the bot never walked and fired on the same tick; the two halves are clobbering each other");
    }

    /// <summary>
    /// A bot inside its class's minimum band walks away from the enemy.
    /// </summary>
    /// <remarks>
    /// ⚠️ Two bots that walk into each other otherwise stand nose to nose with a
    /// degenerate aim solve and neither can shoot — and a soldier pinned at
    /// point-blank range cannot fire at all without killing itself. The band
    /// <b>replaces</b> the follower's horizontal keys rather than being ORed with
    /// them, because pressing both directions at once is pressing neither.
    /// </remarks>
    [Fact]
    public void ABotBacksOutOfItsOwnMinimumBand()
    {
        var world = Load();

        // An enemy 60px to the right, well inside a soldier's 110px band.
        var here = Stand(world.Nav!);
        var shooter = world.Place(PlayerClass.Soldier, Team.Red, here.X, here.Y);
        world.Place(PlayerClass.Heavy, Team.Blue, here.X + 60, here.Y);

        var bot = Bot(world.Nav, stagger: 1);
        world.Drive(shooter, bot);

        var startX = world.State.Characters[shooter].X;
        world.Step(120);

        Assert.True(bot.Combat.Target.Exists, "the bot never saw the enemy standing on top of it");
        Assert.True(
            world.State.Characters[shooter].X < startX - 10,
            $"the bot did not back away: {startX} to {world.State.Characters[shooter].X}");

        // ⚠️ The control: a PYRO has no minimum band, because point blank is
        // where it works. The same geometry must not push it back.
        var pyroWorld = Load();
        var pyroHere = Stand(pyroWorld.Nav!);
        var pyro = pyroWorld.Place(PlayerClass.Pyro, Team.Red, pyroHere.X, pyroHere.Y);
        pyroWorld.Place(PlayerClass.Heavy, Team.Blue, pyroHere.X + 60, pyroHere.Y);

        var pyroBot = Bot(pyroWorld.Nav, stagger: 1);
        pyroWorld.Drive(pyro, pyroBot);

        var pyroStart = pyroWorld.State.Characters[pyro].X;
        pyroWorld.Step(120);

        Assert.True(
            pyroWorld.State.Characters[pyro].X > pyroStart - 10,
            "a pyro backed out of a band it does not have");
    }

    /// <summary>
    /// A bot alone holds for company; one with a team-mate beside it goes.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Only movement is held</b> — the bot still aims and still shoots, so
    /// it never reads as frozen. And the hold ends at <c>BOT_REGROUP_MATES</c>,
    /// which is <b>1</b> rather than 2 on purpose: with 2 every bot on a freshly
    /// spawned team satisfied the test on the same frame and the whole team left
    /// as one body, which is the clumping the hold exists to stop.
    /// <para>
    /// ⚠️ <b>The hold is SET here rather than rolled</b>, because its length is
    /// <c>random(BOT_REGROUP_TICKS)</c> — uniform on 0-90 — and this bot's seed
    /// happens to draw about two. A test that let the roll stand would assert
    /// nothing most of the time and fail occasionally, which is the worst of both.
    /// The roll itself is gated by
    /// <see cref="ABotDrawsFromItsOwnStreamAndThatStreamAdvances"/>.
    /// </para>
    /// </remarks>
    [Fact]
    public void ABotAloneHoldsForCompany()
    {
        var world = Load();
        var graph = world.Nav!;

        var here = Stand(graph, -OnFloor);
        var alone = world.Place(PlayerClass.Soldier, Team.Red, here.X, here.Y);
        var bot = Bot(graph, stagger: 1);
        world.Drive(alone, bot);

        var goal = Stand(graph, OnFloor);
        Assert.True(bot.SetGoal(goal.X, goal.Y));

        bot.AliveLast = true;
        bot.RegroupUntil = 200;

        var startX = world.State.Characters[alone].X;
        world.Step(60);

        Assert.True(
            Math.Abs(world.State.Characters[alone].X - startX) < 30,
            $"a bot alone left without waiting: {startX} to {world.State.Characters[alone].X}");

        // ⚠️ The control: with a team-mate inside BOT_REGROUP_RADIUS the hold
        // ends at once and the bot goes.
        var withMate = Load();
        var mateHere = Stand(withMate.Nav!, -OnFloor);
        var led = withMate.Place(PlayerClass.Soldier, Team.Red, mateHere.X, mateHere.Y);
        withMate.Place(PlayerClass.Scout, Team.Red, mateHere.X + 50, mateHere.Y);

        var second = Bot(withMate.Nav, stagger: 1);
        withMate.Drive(led, second);

        var mateGoal = Stand(withMate.Nav!, OnFloor);
        Assert.True(second.SetGoal(mateGoal.X, mateGoal.Y));

        second.AliveLast = true;
        second.RegroupUntil = 200;

        var ledStart = withMate.State.Characters[led].X;
        withMate.Step(60);

        Assert.True(
            Math.Abs(withMate.State.Characters[led].X - ledStart) > 30,
            $"a bot with a team-mate beside it still waited: {ledStart} to "
            + $"{withMate.State.Characters[led].X}");

        Assert.Equal(0, second.RegroupUntil);
    }

    /// <summary>
    /// A bot draws from its own stream, not the match's.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A rule rather than a convenience.</b> The match's stream is
    /// observable simulation — every seeded weapon puts its draw on the wire —
    /// so a bot drawing from it would make the stream's position depend on how
    /// many bots were thinking.
    /// <para>
    /// ⚠️ And the first draft copied <c>world.State.Random</c> into a local: the
    /// copy advanced, the world's did not, and every tick drew the <b>same</b>
    /// numbers. That is what the second assertion here is for — a stream that
    /// never advances passes any determinism test.
    /// </para>
    /// </remarks>
    [Fact]
    public void ABotDrawsFromItsOwnStreamAndThatStreamAdvances()
    {
        var world = Load();

        var here = Stand(world.Nav!, -OnFloor);
        var shooter = world.Place(PlayerClass.Soldier, Team.Red, here.X, here.Y);
        world.Place(PlayerClass.Heavy, Team.Blue, here.X + 300, here.Y);

        var bot = Bot(world.Nav, stagger: 3);
        world.Drive(shooter, bot);

        // ⚠️ AliveLast, so the regroup roll does NOT happen. That roll advances
        // the stream once on the first tick, and a test that let it run passes
        // even when every combat draw is made from a throwaway copy — which is
        // precisely the silent failure this test exists for. Checked by
        // mutation: without this line the "make a fresh copy every tick"
        // mutation survives.
        bot.AliveLast = true;

        var matchStream = world.State.Random;
        var botStream = bot.Random;

        world.Step(200);

        // ⚠️ The bot's own stream moved, and it moved because of the COMBAT
        // draws — the aim error and the hold-fire roll.
        Assert.NotEqual(botStream, bot.Random);

        // ...and the match's did not move because of it. (The simulation may
        // advance it for its own reasons — a weapon fire draws a seed — so this
        // asserts the bot is not the cause by running with nothing that fires.)
        var quiet = Load();
        var lone = quiet.Place(PlayerClass.Soldier, Team.Red, Stand(quiet.Nav!).X, Stand(quiet.Nav!).Y);
        var loneBot = Bot(quiet.Nav, stagger: 3);
        quiet.Drive(lone, loneBot);
        loneBot.AliveLast = true;

        var before = quiet.State.Random;
        quiet.Step(200);

        Assert.Equal(before, quiet.State.Random);
        Assert.NotEqual(matchStream, bot.Random);

        // Two bots with different staggers roll differently, which is what stops
        // a whole team leaving spawn on one frame.
        var first = new ScriptedBot(null, new NavQuery(Team.Red), BotSkill.ForTier(3), stagger: 1);
        var other = new ScriptedBot(null, new NavQuery(Team.Red), BotSkill.ForTier(3), stagger: 2);

        Assert.NotEqual(first.Random.Next(1000), other.Random.Next(1000));
    }

    /// <summary>A dead bot gives up its route.</summary>
    /// <remarks>
    /// It will come back somewhere else entirely, so resuming a route from where
    /// it died is a bot walking confidently from the wrong place.
    /// </remarks>
    [Fact]
    public void ADeadBotGivesUpItsRoute()
    {
        var world = Load();
        var graph = world.Nav!;

        var here = Stand(graph, -OnFloor);
        var slot = world.Place(PlayerClass.Soldier, Team.Red, here.X, here.Y);
        var bot = Bot(graph, stagger: 1);
        world.Drive(slot, bot);

        var goal = Stand(graph, OnFloor);
        Assert.True(bot.SetGoal(goal.X, goal.Y));
        world.Step(10);

        Assert.True(bot.Path.HasPath, "the bot never planned a route to give up");

        world.State.Characters[slot].Hp = 0;
        world.Step(3);

        Assert.False(world.State.Alive[slot]);
        Assert.False(bot.Path.HasPath);
        Assert.False(bot.AliveLast);
    }
}
