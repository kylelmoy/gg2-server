using GG2.Bots;
using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Tests;

/// <summary>
/// <see cref="HumanizedBot"/>'s legs (D6): on ticks the policy drives, a dance
/// becomes a stand and jump presses are thinned; everything else passes.
/// </summary>
/// <remarks>
/// A stub controller stands in for the policy, so its keys are the only
/// variable, and a real <see cref="HybridBot"/> hands it the wheel in a duel.
/// </remarks>
public sealed class HumanizedLegsTests
{
    private class Dancer : IMicroController
    {
        public virtual string Name => "dancer";

        public int DecisionCost => 1;


        public bool DecidedThisPoll => true;

        public bool Drives(PlayerClass playerClass) => true;

        public bool AtDecisionBoundary(int slot) => true;

        public void OnHandoverIn(in WorldView world, int slot)
        {
        }

        public void OnHandoverOut()
        {
        }

        public virtual PlayerInput Poll(in WorldView world, int slot, int tick) => new(Keys(tick), 0, 0);

        // Two ticks each way, and a one-tick jump press every six ticks.
        internal static PlayerKeys Keys(int tick) =>
            ((tick / 2) % 2 == 0 ? PlayerKeys.Left : PlayerKeys.Right)
            | (tick % 6 == 0 ? PlayerKeys.Jump : PlayerKeys.None);
    }

    /// <summary>Right until <see cref="SwitchAt"/>, then left: one clean reversal.</summary>
    private sealed class Switcher : Dancer
    {
        public const int SwitchAt = 60;

        public override string Name => "switcher";

        public override PlayerInput Poll(in WorldView world, int slot, int tick) =>
            new(tick < SwitchAt ? PlayerKeys.Right : PlayerKeys.Left, 0, 0);
    }

    private static HumanizeOptions Legs { get; } = HumanizeOptions.Tracking2 with
    {
        LegMemory = 0.9,
        LegStartIntent = 0.5,
        LegStopIntent = 0.2,
        JumpKeep = 0.3,
        JumpRefractoryTicks = 15,
    };

    private static HumanizeOptions Running { get; } = Legs with { LegReverseIntent = 0.5, LegIdleActivity = 0.2 };

    private static (int Changes, int Presses, int PolicyTicks, int Moving) Run(HumanizeOptions options, int ticks) =>
        Run(options, ticks, new Dancer(), 4, null);

    /// <summary>Runs the filter over <paramref name="controller"/>; <paramref name="leftAt"/> gets the first tick the output pressed left.</summary>
    private static (int Changes, int Presses, int PolicyTicks, int Moving) Run(
        HumanizeOptions options, int ticks, IMicroController controller, int tier, int[]? leftAt)
    {
        var world = ScenarioWorld.Load("ctf_truefort", 1, ReferenceTree.MapsDirectory, ReferenceTree.NavCacheDirectory);
        var red = world.Place(PlayerClass.Soldier, Team.Red, 600, 850);
        world.Place(PlayerClass.Soldier, Team.Blue, 680, 850);
        world.Step(20);

        var scripted = new ScriptedBot(world.Nav, new NavQuery(Team.Red), BotSkill.ForTier(tier), stagger: 0);
        var hybrid = new HybridBot(controller, scripted) { Rule = HandoverRule.Distance };
        var bot = new HumanizedBot(hybrid, options, 3);
        var changes = 0;
        var presses = 0;
        var policyTicks = 0;
        var moving = 0;
        var last = PlayerKeys.None;

        for (var tick = 0; tick < ticks; tick++)
        {
            var view = new WorldView(in world.State, world.Collision);
            var keys = bot.Poll(in view, red, tick).Keys;

            if (leftAt is { } first && first[0] < 0 && hybrid.MicroDrove && (keys & PlayerKeys.Left) != 0)
                first[0] = tick;

            if (hybrid.MicroDrove)
            {
                policyTicks++;

                if ((keys & (PlayerKeys.Left | PlayerKeys.Right)) != 0)
                    moving++;

                if ((keys & (PlayerKeys.Left | PlayerKeys.Right)) != (last & (PlayerKeys.Left | PlayerKeys.Right)))
                    changes++;

                if ((keys & PlayerKeys.Jump) != 0 && (last & PlayerKeys.Jump) == 0)
                    presses++;
            }

            last = keys;
            world.Step();
        }

        return (changes, presses, policyTicks, moving);
    }

    /// <summary>
    /// The control is <see cref="HumanizeOptions.Tracking2"/>, without legs: the
    /// dance passes, a change every two ticks and a press every six.
    /// </summary>
    [Fact]
    public void ADanceStandsAndJumpsThin()
    {
        var off = Run(HumanizeOptions.Tracking2, 300);
        var on = Run(Legs, 300);

        Assert.True(off.PolicyTicks > 200, $"the dancer drove {off.PolicyTicks} ticks, too few to read");
        Assert.True(off.Changes > off.PolicyTicks / 3, $"without legs, {off.Changes} changes: the control did not dance");
        Assert.True(on.Changes < off.Changes / 5, $"legs: {on.Changes} changes against {off.Changes}");
        Assert.True(on.Presses < off.Presses / 2, $"legs: {on.Presses} presses against {off.Presses}");
    }

    /// <summary>D7: the same dance keeps the legs running instead of standing.</summary>
    [Fact]
    public void ADanceKeepsRunning()
    {
        var off = Run(HumanizeOptions.Tracking2, 300);
        var stand = Run(Legs, 300);
        var run = Run(Running, 300);

        Assert.True(run.Changes < off.Changes / 5, $"running: {run.Changes} changes against {off.Changes}");
        Assert.True(run.Moving > run.PolicyTicks * 9 / 10, $"running: moving {run.Moving} of {run.PolicyTicks} policy ticks");
        Assert.True(stand.Moving < stand.PolicyTicks / 2, $"standing: moving {stand.Moving} of {stand.PolicyTicks}, so the contrast proves little");
    }

    /// <summary>
    /// D12: a held reversal passes within the cooldown of the switch, at the
    /// lowest tier too. D10's averaged legs (<see cref="HumanizeOptions.Tracking7"/>)
    /// take at least 41 ticks, which is the duel server's joust.
    /// </summary>
    [Fact]
    public void AHeldReversalPassesAtOnce()
    {
        var cooldown = BotSkill.ForTier(1).ReversalCooldownTicks;
        var nine = new[] { -1 };
        var seven = new[] { -1 };

        Run(HumanizeOptions.Tracking9, 150, new Switcher(), 1, nine);
        Run(HumanizeOptions.Tracking7, 150, new Switcher(), 1, seven);

        Assert.InRange(nine[0], Switcher.SwitchAt, Switcher.SwitchAt + cooldown);
        Assert.True(seven[0] < 0 || seven[0] >= Switcher.SwitchAt + 40, $"tracking7 reversed at {seven[0]}, so the contrast proves little");
    }

    /// <summary>
    /// D12: the cooldown lengthens as the skill falls, so a two-tick dance passes
    /// near intact at tier 4 and is slowed at tier 1, while the legs keep moving.
    /// </summary>
    [Fact]
    public void TheCooldownSlowsADanceMoreAtLowerTiers()
    {
        Assert.True(BotSkill.ForTier(1).ReversalCooldownTicks > BotSkill.ForTier(3).ReversalCooldownTicks);
        Assert.True(BotSkill.ForTier(3).ReversalCooldownTicks > BotSkill.ForTier(4).ReversalCooldownTicks);

        var off = Run(HumanizeOptions.Tracking2, 300);
        var top = Run(HumanizeOptions.Tracking9, 300, new Dancer(), 4, null);
        var bottom = Run(HumanizeOptions.Tracking9, 300, new Dancer(), 1, null);

        Assert.True(top.Changes > off.Changes * 2 / 3, $"tier 4: {top.Changes} changes against {off.Changes}");
        Assert.True(bottom.Changes < off.Changes / 3, $"tier 1: {bottom.Changes} changes against {off.Changes}");
        Assert.True(bottom.Moving > bottom.PolicyTicks * 9 / 10, $"tier 1: moving {bottom.Moving} of {bottom.PolicyTicks}");
    }

    /// <summary>Tier 5 passes through exactly, legs and all.</summary>
    [Fact]
    public void TheTopTierKeepsItsLegs()
    {
        var world = ScenarioWorld.Load("ctf_truefort", 1, ReferenceTree.MapsDirectory, ReferenceTree.NavCacheDirectory);
        var red = world.Place(PlayerClass.Soldier, Team.Red, 600, 850);
        world.Place(PlayerClass.Soldier, Team.Blue, 680, 850);
        world.Step(20);

        var scripted = new ScriptedBot(world.Nav, new NavQuery(Team.Red), BotSkill.ForTier(5), stagger: 0);
        var hybrid = new HybridBot(new Dancer(), scripted) { Rule = HandoverRule.Distance };
        var bot = HumanizedBot.ByTier(hybrid, 3, Legs);
        var drove = 0;

        for (var tick = 0; tick < 120; tick++)
        {
            var view = new WorldView(in world.State, world.Collision);
            var keys = bot.Poll(in view, red, tick).Keys;

            if (hybrid.MicroDrove)
            {
                drove++;
                Assert.Equal(Dancer.Keys(tick), keys);
            }

            world.Step();
        }

        Assert.True(drove > 60, $"the dancer drove {drove} ticks, so the equality above proves little");
    }
}
