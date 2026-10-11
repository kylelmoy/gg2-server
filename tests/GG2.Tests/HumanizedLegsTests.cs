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
    private sealed class Dancer : IMicroController
    {
        public string Name => "dancer";

        public int DecisionCost => 1;

        public int DecisionCostFor(int liveCharacters) => 1;

        public bool DecidedThisPoll => true;

        public bool Drives(PlayerClass playerClass) => true;

        public bool AtDecisionBoundary(int slot) => true;

        public void OnHandoverIn(in WorldView world, int slot)
        {
        }

        public void OnHandoverOut()
        {
        }

        public PlayerInput Poll(in WorldView world, int slot, int tick) => new(Keys(tick), 0, 0);

        // Two ticks each way, and a one-tick jump press every six ticks.
        internal static PlayerKeys Keys(int tick) =>
            ((tick / 2) % 2 == 0 ? PlayerKeys.Left : PlayerKeys.Right)
            | (tick % 6 == 0 ? PlayerKeys.Jump : PlayerKeys.None);
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

    private static (int Changes, int Presses, int PolicyTicks, int Moving) Run(HumanizeOptions options, int ticks)
    {
        var world = ScenarioWorld.Load("ctf_truefort", 1, ReferenceTree.MapsDirectory, ReferenceTree.NavCacheDirectory);
        var red = world.Place(PlayerClass.Soldier, Team.Red, 600, 850);
        world.Place(PlayerClass.Soldier, Team.Blue, 680, 850);
        world.Step(20);

        var scripted = new ScriptedBot(world.Nav, new NavQuery(Team.Red), BotSkill.ForTier(4), stagger: 0);
        var hybrid = new HybridBot(new Dancer(), scripted) { Rule = HandoverRule.Distance };
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
