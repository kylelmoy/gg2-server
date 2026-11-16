using GG2.Bots;
using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Tests;

/// <summary>
/// Arm 3: the scripted bot with a <see cref="RolloutSearch"/> micro-controller.
/// </summary>
/// <remarks>
/// <b>What these gate is that ONE thing differs from the policy arm: which
/// controller <see cref="HybridBot"/> hands to.</b>
/// An arm-2/arm-3 comparison is a comparison of controllers only if the gate, the
/// range, the rule, the intel carve-out and the every-tick scripted poll are the
/// same on both sides. A difference in any of those makes the experiment measure
/// two bots rather than two controllers — and it would not look like an error, it
/// would look like a result.
/// </remarks>
public sealed class SearchBotTests
{
    private static ScenarioWorld World() =>
        ScenarioWorld.Load("ctf_truefort", 1, ReferenceTree.MapsDirectory, ReferenceTree.NavCacheDirectory);

    private static ScriptedBot Scripted(ScenarioWorld world) =>
        new(world.Nav, new NavQuery(Team.Red), BotSkill.ForTier(5), stagger: 0);

    private static HybridBot Bot(ScenarioWorld world, out ScriptedBot scripted)
    {
        scripted = Scripted(world);

        return new HybridBot(new SearchController(new RolloutOptions(HorizonTicks: 8)), scripted);
    }

    /// <summary>Places red and a blue enemy <paramref name="gap"/> px away.</summary>
    private static int Duel(ScenarioWorld world, int gap)
    {
        var red = world.Place(PlayerClass.Soldier, Team.Red, 600, 850);
        world.Place(PlayerClass.Soldier, Team.Blue, 600 + gap, 850);
        world.Step(20);

        return red;
    }

    private static void Run(HybridBot bot, ScenarioWorld world, int red, int ticks)
    {
        for (var tick = 0; tick < ticks; tick++)
        {
            var view = new WorldView(in world.State, world.Collision);
            bot.Poll(in view, red, tick);
            world.Step();
        }
    }

    /// <summary>
    /// Far apart, the scripted layer drives and the search never runs.
    /// </summary>
    /// <remarks>
    /// The cheap half of the gate, and what makes the cost bearable at all: a
    /// search decision is 23% of a tick at twelve characters, so a bot that
    /// engaged everywhere would be unshippable however well it fought.
    /// </remarks>
    [Fact]
    public void OutsideTheWindowTheScriptedLayerDrives()
    {
        var world = World();
        var bot = Bot(world, out _);
        var red = Duel(world, gap: 1000);

        Run(bot, world, red, 12);

        Assert.False(bot.MicroDrove);
        Assert.Equal(0, bot.MicroTicks);
        Assert.Equal(12, bot.ScriptedTicks);
    }

    /// <summary>Inside the window the search takes the wheel.</summary>
    [Fact]
    public void InsideTheWindowTheSearchDrives()
    {
        var world = World();
        var bot = Bot(world, out _);
        var red = Duel(world, gap: 80);

        Run(bot, world, red, 12);

        Assert.True(
            bot.MicroTicks > 0,
            "the search never took the wheel with an enemy 80px away, so the handover is not "
            + "reaching it — check Handover.ShouldEngage and RolloutSearch.AtDecisionBoundary");
    }

    /// <summary>
    /// The intel carrier is the macro layer's, exactly as in arm 2.
    /// </summary>
    /// <remarks>
    /// Carrying the intel is an objective task and objectives are the scripted
    /// layer's. If this differed between the arms, arm 3 would be playing a
    /// different game on every CTF map rather than running a different controller.
    /// </remarks>
    [Fact]
    public void TheIntelCarrierIsAlwaysTheScriptedLayers()
    {
        // HandoverRule.Range, NOT the shipped MacroTask conjunction, and that is
        // what makes this test mean anything. Under MacroTask the scripted layer does
        // not want to engage while carrying the intel anyway, so the search stays out
        // whether or not the carve-out exists — deleting it left this green. Dropping
        // the WantsToEngage conjunct leaves the carve-out as the only thing that can
        // keep the search away, which is the property being asserted.
        var carrying = World();
        var scriptedA = Scripted(carrying);
        var withIntel = new HybridBot(new SearchController(new RolloutOptions(HorizonTicks: 8)), scriptedA)
        {
            Rule = HandoverRule.Distance,
        };

        var redA = Duel(carrying, gap: 80);
        carrying.State.Characters[redA].CarryingIntel = true;
        Run(withIntel, carrying, redA, 12);

        // The contrast: identical but for the intel. Without it the search MUST
        // drive, or the zero above says nothing.
        var empty = World();
        var withoutIntel = new HybridBot(new SearchController(new RolloutOptions(HorizonTicks: 8)), Scripted(empty))
        {
            Rule = HandoverRule.Distance,
        };

        var redB = Duel(empty, gap: 80);
        Run(withoutIntel, empty, redB, 12);

        Assert.True(
            withoutIntel.MicroTicks > 0,
            "the search did not drive even without the intel, so the zero below proves nothing");

        Assert.Equal(0, withIntel.MicroTicks);
    }

    /// <summary>
    /// The scripted layer thinks on EVERY tick, including ones it does not drive.
    /// </summary>
    /// <remarks>
    /// <b>The measured lesson from arm 2, asserted rather than assumed.</b>
    /// Polling the scripted bot only on the ticks it drove left the macro layer
    /// doing no objective planning during fights — no re-target, no re-path, no
    /// spot ban — and captures fell 20 to 7 over a hundred matches. A search
    /// hybrid that reintroduced it would lose the objective for a reason that has
    /// nothing to do with the search.
    /// <para>
    /// <c>WantsToEngage</c> is written by <c>ScriptedBot.Poll</c>, so its being
    /// true after ticks the search drove is evidence the scripted layer ran on
    /// them. The tick accounting is the second half: every poll is charged to
    /// exactly one of the two controllers.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheScriptedLayerThinksOnEveryTickEvenWhenTheSearchDrives()
    {
        var world = World();
        var bot = Bot(world, out var scripted);
        var red = Duel(world, gap: 80);

        Run(bot, world, red, 16);

        Assert.True(bot.MicroTicks > 0, "the search never drove, so this proves nothing");
        Assert.Equal(16, bot.MicroTicks + bot.ScriptedTicks);

        // The load-bearing assertion, and WantsToEngage alone was NOT it: with
        // the scripted poll moved behind `if it drives`, this class still passed,
        // because the hold stays set from the ticks it did drive. ScriptedPolls
        // counts THINKING rather than DRIVING, and only it separates the two.
        Assert.Equal(16, bot.ScriptedPolls);

        Assert.True(
            scripted.WantsToEngage,
            "the scripted engage hold is not set after sixteen ticks beside an enemy");
    }

    /// <summary>
    /// Both arms engage at the same distances, because they share one gate.
    /// </summary>
    /// <remarks>
    /// <b>Behavioural rather than structural.</b> Asserting that both call
    /// <c>Handover</c> would pass on two classes calling it with different
    /// arguments. This runs a real <see cref="SimPolicyController"/> and a real
    /// <see cref="SearchController"/> over the same world and asserts they agree
    /// about who drives — which is the property the experiment actually needs.
    /// <para>
    /// Both arms are the same <see cref="HybridBot"/>, so the gate is shared by
    /// construction and this cannot catch two gates drifting apart. What it
    /// catches is a CONTROLLER that changes when the gate fires — an
    /// <c>AtDecisionBoundary</c> or a <c>Drives</c> that disagrees between them.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(80)]
    [InlineData(1000)]
    public void TheTwoArmsHandOverAtTheSameDistances(int gap)
    {
        var policyWorld = World();
        var policy = new HybridBot(new SimPolicyController(PolicyFixture.Load(), [PlayerClass.Soldier]), Scripted(policyWorld));

        var policyRed = Duel(policyWorld, gap);

        for (var tick = 0; tick < 12; tick++)
        {
            var view = new WorldView(in policyWorld.State, policyWorld.Collision);
            policy.Poll(in view, policyRed, tick);
            policyWorld.Step();
        }

        var searchWorld = World();
        var search = Bot(searchWorld, out _);
        var searchRed = Duel(searchWorld, gap);

        Run(search, searchWorld, searchRed, 12);

        Assert.Equal(policy.MicroTicks > 0, search.MicroTicks > 0);
    }

    /// <summary>
    /// The search is resynced when control comes BACK to it.
    /// </summary>
    /// <remarks>
    /// <b>The tests above never handed control back, so deleting the resync call
    /// left them all green.</b> Found by mutation, which is the only thing that could
    /// have found it: a missing resync does not throw, it makes the first decision
    /// after each handover turn from a bearing nobody is pointing at.
    /// <para>
    /// The enemy starts out of range so the scripted layer drives and writes the
    /// bearing, then is moved into range. What the search asks for on the tick it
    /// takes over must be within one decision’s turn of where the character is
    /// actually pointing.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheSearchIsResyncedWhenControlComesBack()
    {
        var world = World();
        var bot = Bot(world, out _);
        var red = Duel(world, gap: 80);

        // In range: the search takes the wheel.
        Run(bot, world, red, 8);
        Assert.True(bot.MicroTicks > 0, "the search never drove, so this proves nothing");

        // Force control OUT through a real gate path rather than by moving the
        // enemy: picking up the intel is the carve-out, is deterministic, and does
        // not depend on the scripted layer re-acquiring a target.
        world.State.Characters[red].CarryingIntel = true;
        Run(bot, world, red, 4);

        // While the macro layer had it, the bearing moved - which is exactly what
        // the scripted layer does, writing it directly.
        world.State.Characters[red].NetAimDirection = 49152;

        // And now control comes BACK.
        world.State.Characters[red].CarryingIntel = false;

        var before = bot.MicroTicks;
        var view = new WorldView(in world.State, world.Collision);
        var taken = bot.Poll(in view, red, 12);

        Assert.True(
            bot.MicroTicks > before,
            "control did not come back to the search, so this proves nothing");

        var wanted = taken.NetAimDirection * 360.0 / 65536.0;
        var actual = world.State.Characters[red].NetAimDirection * 360.0 / 65536.0;
        var apart = Math.Abs(((wanted - actual + 540.0) % 360.0) - 180.0);

        Assert.True(
            apart <= ActionDecoder.MaxTurnDegreesPerDecision + 1e-6,
            $"the search asked for an aim {apart:0.0} degrees from where the character " +
            "points, which is more than one decision can turn - it was not resynced on " +
            "the handover and is turning from a bearing nobody is pointing at");
    }

    /// <summary>
    /// Control changes hands only on the search’s decision boundary.
    /// </summary>
    /// <remarks>
    /// <b>Two controllers with independent cadences flap against each other at
    /// the threshold</b>, and each holds state the other does not maintain — the
    /// macro layer’s follower, the search’s absolute aim. The policy arm refuses
    /// to swap mid-decision for that reason and this must too.
    /// <para>
    /// Checked deterministically rather than by jittering the range: engage, then
    /// teleport the enemy away MID-decision. Respecting the boundary, the search
    /// keeps the wheel until its decision ends; ignoring it, control returns on the
    /// very next tick. Found by mutation — replacing the boundary test left every
    /// other test in this class green.
    /// </para>
    /// </remarks>
    [Fact]
    public void ControlDoesNotChangeHandsMidDecision()
    {
        var world = World();
        var bot = Bot(world, out _);
        var red = Duel(world, gap: 80);
        var enemy = red + 1;

        // One poll: the search takes over and starts a decision.
        var view = new WorldView(in world.State, world.Collision);
        bot.Poll(in view, red, 0);
        world.Step();

        Assert.True(bot.MicroDrove, "the search did not take over, so this proves nothing");

        // Now make the gate want the scripted layer back, mid-decision.
        world.State.Characters[enemy].X = world.State.Characters[red].X + 4000;

        var next = new WorldView(in world.State, world.Collision);
        bot.Poll(in next, red, 1);

        Assert.True(
            bot.MicroDrove,
            "control went back to the scripted layer on the tick after the search took a " +
            "decision — the gate is re-evaluating every tick instead of on the search’s " +
            "decision boundary, which is the two-cadence flap this design refuses");
    }
}
