using Gg2.Bots;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// The parts of <see cref="HybridBot"/> that are the SEAM rather than the arm:
/// tier selection, the arbiter, the head split, and the counter invariant.
/// </summary>
/// <remarks>
/// ⛔ <b>None of this was reachable before the wrapper split was merged.</b> The
/// two old classes each hard-coded one controller, so "which controller drives"
/// was not a decision anything could make and not a decision anything could test.
/// <para>
/// ⚠️ <b>These use a STUB controller on purpose.</b> A test that drove a real
/// <c>RolloutSearch</c> to establish that the arbiter refused it would take
/// seconds and would fail for a dozen reasons that are not the arbiter. The stub
/// makes "who was asked, and what did they answer" the only variable — and the
/// real controllers are covered by <c>SearchBotTests</c> and
/// <c>PolicyMatchTests</c>, which is the right split.
/// </para>
/// <para>
/// ⚠️ <b>Every assertion of an ABSENCE here states its own control</b>, because an
/// absence passes on a deleted feature. The pattern this repo arrived at after
/// four of eight moving-platform tests turned out to be vacuous — CLAUDE.md.
/// </para>
/// </remarks>
public sealed class HybridBotSeamTests
{
    /// <summary>
    /// A controller that always decides, and records what it was asked.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Decides on EVERY poll</b> — <c>AtDecisionBoundary</c> is always true —
    /// so the gate never has to wait for a cadence and a test can count decisions
    /// by counting ticks. The two real controllers have cadences; that is their
    /// tests' subject, not this file's.
    /// </remarks>
    private sealed class Stub : IMicroController
    {
        private readonly PlayerKeys _keys;

        internal Stub(string name, int cost, PlayerKeys keys = PlayerKeys.Attack)
        {
            Name = name;
            DecisionCost = cost;
            _keys = keys;
        }

        public string Name { get; }

        public int DecisionCost { get; }

        public bool DecidedThisPoll => true;

        /// <summary>Classes this refuses, to exercise the eligibility mask.</summary>
        internal PlayerClass? Refuses { get; init; }

        internal int Polls { get; private set; }

        internal int HandoversIn { get; private set; }

        internal int HandoversOut { get; private set; }

        public bool Drives(PlayerClass playerClass) => Refuses != playerClass;

        public bool AtDecisionBoundary(int slot) => true;

        public void OnHandoverIn(in WorldView world, int slot) => HandoversIn++;

        public void OnHandoverOut() => HandoversOut++;

        public PlayerInput Poll(in WorldView world, int slot, int tick)
        {
            Polls++;

            return new PlayerInput(_keys, 0, 0);
        }
    }

    private static ScenarioWorld World() =>
        ScenarioWorld.Load("ctf_truefort", 1, ReferenceTree.MapsDirectory, ReferenceTree.NavCacheDirectory);

    private static ScriptedBot Scripted(ScenarioWorld world) =>
        new(world.Nav, new NavQuery(Team.Red), BotSkill.ForTier(5), stagger: 0);

    /// <summary>Places red with a blue enemy 80px away — inside the 150px window.</summary>
    private static int Duel(ScenarioWorld world, int gap = 80)
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
    /// With no arbiter, tier 0 drives and the cheaper tiers are never asked.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The control is the second assertion.</b> "Tier 1 was never polled"
    /// passes on a bot that engaged nobody, so tier 0's poll count has to be
    /// positive for the zero to mean anything.
    /// </remarks>
    [Fact]
    public void WithNoArbiterTheMostCapableTierAlwaysDrives()
    {
        var world = World();
        var expensive = new Stub("expensive", cost: 4);
        var cheap = new Stub("cheap", cost: 1);
        var bot = new HybridBot([expensive, cheap], Scripted(world)) { Rule = HandoverRule.Distance };

        Run(bot, world, Duel(world), 12);

        Assert.True(expensive.Polls > 0, "nobody engaged, so the zero below proves nothing");
        Assert.Equal(0, cheap.Polls);
        Assert.Same(expensive, bot.Driving);
    }

    /// <summary>
    /// A budget too small for tier 0 drops the bot to the cheaper tier, not to the
    /// scripted layer.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>This is the behaviour the seam was built for</b>: cheaper logic when
    /// the expensive one cannot be afforded, rather than an all-or-nothing switch.
    /// The control is the previous test — the same bot with no arbiter runs the
    /// expensive tier — so a zero here is the arbiter's doing and not the gate's.
    /// </remarks>
    [Fact]
    public void ABudgetTooSmallForTierZeroFallsToTheCheaperTier()
    {
        var world = World();
        var expensive = new Stub("expensive", cost: 4);
        var cheap = new Stub("cheap", cost: 1);
        var bot = new HybridBot(
            [expensive, cheap], Scripted(world), new DecisionQuotaArbiter(unitsPerTick: 1))
        {
            Rule = HandoverRule.Distance,
        };

        Run(bot, world, Duel(world), 12);

        Assert.True(cheap.Polls > 0, "nobody engaged, so the zero below proves nothing");
        Assert.Equal(0, expensive.Polls);
    }

    /// <summary>
    /// A budget too small for ANY tier leaves the wheel with the scripted layer.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>-1, not "the cheapest tier anyway".</b> A budget that one more
    /// decision always exceeds is not a budget. The scripted layer is a complete
    /// bot, which is why refusing is a safe answer at all.
    /// </remarks>
    [Fact]
    public void AnExhaustedBudgetLeavesTheScriptedLayerDriving()
    {
        var world = World();
        var micro = new Stub("micro", cost: 1);
        var bot = new HybridBot(
            micro, Scripted(world), new DecisionQuotaArbiter(unitsPerTick: 0))
        {
            Rule = HandoverRule.Distance,
        };

        var red = Duel(world);

        Run(bot, world, red, 12);

        Assert.Equal(0, micro.Polls);
        Assert.Equal(12, bot.ScriptedTicks);

        // The control: the identical bot with the budget raised engages. Without
        // this, a gate that never opened would produce the same two numbers.
        var open = World();
        var micro2 = new Stub("micro", cost: 1);
        var bot2 = new HybridBot(
            micro2, Scripted(open), new DecisionQuotaArbiter(unitsPerTick: 1))
        {
            Rule = HandoverRule.Distance,
        };

        Run(bot2, open, Duel(open), 12);

        Assert.True(micro2.Polls > 0, "the budget was not what kept the controller out");
    }

    /// <summary>
    /// ⛔ ONE arbiter shared by several bots is what makes coinciding decisions
    /// visible, and it is the only reason this seam exists.
    /// </summary>
    /// <remarks>
    /// <b>The tick tail is several bots deciding on the SAME tick</b>, not one
    /// expensive bot — handovers are correlated, because several bots have an enemy
    /// in range during the same firefight. A per-bot budget cannot see that at all.
    /// <para>
    /// ⚠️ <b>The spend is ordered by roster slot</b>, so the first bot polled takes
    /// the budget. Deterministic, and stated in <see cref="IMicroArbiter"/> as the
    /// fairness cost.
    /// </para>
    /// </remarks>
    [Fact]
    public void OneArbiterBoundsTheTotalAcrossBotsDecidingOnTheSameTick()
    {
        var world = World();
        var arbiter = new DecisionQuotaArbiter(unitsPerTick: 1);

        var firstMicro = new Stub("first", cost: 1);
        var secondMicro = new Stub("second", cost: 1);

        var first = new HybridBot(firstMicro, Scripted(world), arbiter) { Rule = HandoverRule.Distance };
        var second = new HybridBot(secondMicro, Scripted(world), arbiter) { Rule = HandoverRule.Distance };

        var redA = world.Place(PlayerClass.Soldier, Team.Red, 600, 850);
        var redB = world.Place(PlayerClass.Soldier, Team.Red, 640, 850);
        world.Place(PlayerClass.Soldier, Team.Blue, 680, 850);
        world.Step(20);

        for (var tick = 0; tick < 12; tick++)
        {
            var view = new WorldView(in world.State, world.Collision);
            first.Poll(in view, redA, tick);
            second.Poll(in view, redB, tick);
            world.Step();
        }

        Assert.True(firstMicro.Polls > 0, "neither bot engaged, so the zero below proves nothing");
        Assert.Equal(0, secondMicro.Polls);

        // The control: two arbiters with the same budget, so the SHARING is what is
        // under test rather than the budget. Both bots must engage.
        var apart = World();
        var thirdMicro = new Stub("third", cost: 1);
        var fourthMicro = new Stub("fourth", cost: 1);

        var third = new HybridBot(
            thirdMicro, Scripted(apart), new DecisionQuotaArbiter(1)) { Rule = HandoverRule.Distance };
        var fourth = new HybridBot(
            fourthMicro, Scripted(apart), new DecisionQuotaArbiter(1)) { Rule = HandoverRule.Distance };

        var redC = apart.Place(PlayerClass.Soldier, Team.Red, 600, 850);
        var redD = apart.Place(PlayerClass.Soldier, Team.Red, 640, 850);
        apart.Place(PlayerClass.Soldier, Team.Blue, 680, 850);
        apart.Step(20);

        for (var tick = 0; tick < 12; tick++)
        {
            var view = new WorldView(in apart.State, apart.Collision);
            third.Poll(in view, redC, tick);
            fourth.Poll(in view, redD, tick);
            apart.Step();
        }

        Assert.True(
            thirdMicro.Polls > 0 && fourthMicro.Polls > 0,
            "with a budget each, both bots must engage — otherwise the zero above is "
            + "the budget's doing rather than the sharing's");
    }

    /// <summary>
    /// ⛔ The budget resets every TICK, from the tick number and nothing else.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The one thing an arbiter may read.</b> A wall clock in here makes a
    /// match unreproducible — <c>HeadlessMatch</c> results stop being a function of
    /// the seed and <c>eval</c> stops being thread-count independent, which this
    /// repo gates rather than assumes. <see cref="IMicroArbiter"/> says so; this
    /// makes the reset itself mechanical.
    /// </remarks>
    [Fact]
    public void TheBudgetIsPerTickAndResetsOnTheTickNumber()
    {
        var arbiter = new DecisionQuotaArbiter(unitsPerTick: 2);
        var tiers = new IMicroController[] { new Stub("only", cost: 2) };

        Assert.Equal(0, arbiter.Choose(tick: 10, slot: 1, tiers, eligible: 1));
        arbiter.Admitted(tick: 10, slot: 1, tier: 0, cost: 2);

        Assert.Equal(-1, arbiter.Choose(tick: 10, slot: 2, tiers, eligible: 1));

        // Same budget, next tick.
        Assert.Equal(0, arbiter.Choose(tick: 11, slot: 2, tiers, eligible: 1));
    }

    /// <summary>
    /// A tier that refuses this class is not offered to the arbiter, and a bot
    /// whose every tier refuses stays scripted.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Asked per tick, not at construction</b> — a class can change mid-match
    /// and the wrapper cannot be rebuilt when it does.
    /// </remarks>
    [Fact]
    public void ATierThatRefusesTheClassIsSkippedAndTheLastOneFallsBackToScripted()
    {
        var world = World();
        var refuses = new Stub("refuses", cost: 1) { Refuses = PlayerClass.Soldier };
        var takes = new Stub("takes", cost: 1);
        var bot = new HybridBot([refuses, takes], Scripted(world)) { Rule = HandoverRule.Distance };

        Run(bot, world, Duel(world), 12);

        Assert.True(takes.Polls > 0, "nobody engaged, so the zero below proves nothing");
        Assert.Equal(0, refuses.Polls);

        var none = World();
        var refusesToo = new Stub("refuses", cost: 1) { Refuses = PlayerClass.Soldier };
        var scriptedOnly = new HybridBot(refusesToo, Scripted(none)) { Rule = HandoverRule.Distance };

        Run(scriptedOnly, none, Duel(none), 12);

        Assert.Equal(0, refusesToo.Polls);
        Assert.Equal(12, scriptedOnly.ScriptedTicks);
    }

    /// <summary>
    /// ⛔ An arbiter naming an ineligible tier throws rather than being clamped.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A silent substitution makes an arm that is not the arm anybody
    /// configured</b>, and it would be invisible in a result table — the class of
    /// failure this repo keeps catching late.
    /// </remarks>
    [Fact]
    public void AnArbiterNamingAnIneligibleTierThrows()
    {
        var world = World();
        var refuses = new Stub("refuses", cost: 1) { Refuses = PlayerClass.Soldier };
        var takes = new Stub("takes", cost: 1);
        var bot = new HybridBot([refuses, takes], Scripted(world), new AlwaysTierZero())
        {
            Rule = HandoverRule.Distance,
        };

        var red = Duel(world);

        var thrown = Assert.Throws<InvalidOperationException>(() => Run(bot, world, red, 12));

        Assert.Contains("not eligible", thrown.Message, StringComparison.Ordinal);
    }

    private sealed class AlwaysTierZero : IMicroArbiter
    {
        public int Choose(int tick, int slot, IReadOnlyList<IMicroController> tiers, uint eligible) => 0;

        public void Admitted(int tick, int slot, int tier, int cost)
        {
        }
    }

    /// <summary>
    /// ⛔ <c>ScriptedPolls == MicroTicks + ScriptedTicks</c>: the macro layer thinks
    /// on every tick, including the ones it does not drive.
    /// </summary>
    /// <remarks>
    /// <b>Arm 2's measured bug.</b> Polling the scripted layer only on the ticks it
    /// drove left it doing no objective planning during fights — no re-target, no
    /// re-path, no spot ban — and captures fell 20 to 7 over a hundred matches.
    /// <para>
    /// ⚠️ <b>This invariant was testable on ONE arm before the merge.</b>
    /// <c>SearchBot</c> counted <c>ScriptedPolls</c> and <c>PolicyBot</c> did not,
    /// so the policy arm carried the same property with nothing able to check it.
    /// Every arm gets every counter now, and this asserts it across a run where
    /// control changes hands in both directions.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheScriptedLayerIsPolledOnEveryTickWhicheverControllerDrove()
    {
        var world = World();
        var micro = new Stub("micro", cost: 1);
        var bot = new HybridBot(micro, Scripted(world)) { Rule = HandoverRule.Distance };

        var red = world.Place(PlayerClass.Soldier, Team.Red, 600, 850);
        var enemy = world.Place(PlayerClass.Soldier, Team.Blue, 680, 850);
        world.Step(20);

        for (var tick = 0; tick < 20; tick++)
        {
            // Push the enemy out of range halfway, so control changes hands both
            // ways inside one run rather than the counters agreeing trivially.
            if (tick == 10)
                world.State.Characters[enemy].X = world.State.Characters[red].X + 4000;

            var view = new WorldView(in world.State, world.Collision);
            bot.Poll(in view, red, tick);
            world.Step();
        }

        Assert.True(bot.MicroTicks > 0, "the controller never drove");
        Assert.True(bot.ScriptedTicks > 0, "the scripted layer never drove");
        Assert.Equal(20, bot.ScriptedPolls);
        Assert.Equal(bot.ScriptedPolls, bot.MicroTicks + bot.ScriptedTicks);
    }

    /// <summary>
    /// <see cref="HandoverRule.HeadSplit"/>: scripted movement, controller aim and
    /// fire, every tick, with no gate.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>This rule shipped with ZERO tests until the wrapper merge.</b> It was a
    /// branch inside <c>PolicyBot.Poll</c> that nothing in the suite, the bench or
    /// the host ever selected — so restructuring it was a change nothing could have
    /// caught, which is what made writing this a precondition rather than a bonus.
    /// <para>
    /// ⚠️ <b>The stub presses <c>Attack</c> and never a movement key</b>, so
    /// "movement came from the scripted layer" is checked by the attack bit
    /// surviving and by the controller being polled on every tick — no gate, no
    /// scripted ticks at all.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheHeadSplitTakesCombatFromTheControllerAndMovementFromTheScriptedLayer()
    {
        // ⚠️ TWO worlds built and stepped identically, one driven by a bare
        // ScriptedBot. Polling does not feed the world in this harness, so the two
        // evolve in lockstep and the bare bot's keys ARE the scripted half of the
        // merge — which is the only way to check the split without asserting
        // against a movement pattern somebody guessed.
        var split = World();
        var control = World();

        var micro = new Stub("micro", cost: 1, keys: PlayerKeys.Attack | PlayerKeys.Left);
        var bot = new HybridBot(micro, Scripted(split)) { Rule = HandoverRule.HeadSplit };
        var bare = Scripted(control);

        // No enemy anywhere: under any gated rule the controller would never drive,
        // which is what makes this a test of the head split rather than of range.
        var red = split.Place(PlayerClass.Soldier, Team.Red, 600, 850);
        var controlRed = control.Place(PlayerClass.Soldier, Team.Red, 600, 850);
        split.Step(20);
        control.Step(20);

        const PlayerKeys Movement = PlayerKeys.Left | PlayerKeys.Right | PlayerKeys.Jump
            | PlayerKeys.Down | PlayerKeys.Taunt;

        var sawAttack = false;

        for (var tick = 0; tick < 12; tick++)
        {
            var view = new WorldView(in split.State, split.Collision);
            var merged = bot.Poll(in view, red, tick);

            var controlView = new WorldView(in control.State, control.Collision);
            var scripted = bare.Poll(in controlView, controlRed, tick);

            sawAttack |= (merged.Keys & PlayerKeys.Attack) != 0;

            Assert.Equal(scripted.Keys & Movement, merged.Keys & Movement);

            split.Step();
            control.Step();
        }

        Assert.Equal(12, micro.Polls);
        Assert.Equal(0, bot.ScriptedTicks);
        Assert.True(sawAttack, "the controller's fire bit never reached the output");

        // ⚠️ The control for the equality above, which would otherwise pass on a
        // merge that took EVERYTHING from the scripted layer. The stub holds Left
        // on every tick, so the two halves must disagree somewhere or the assertion
        // is checking nothing.
        var last = new WorldView(in control.State, control.Collision);

        Assert.True(
            (bare.Poll(in last, controlRed, 12).Keys & PlayerKeys.Left) == 0,
            "the scripted layer was pressing Left too, so the movement equality above "
            + "cannot tell the two halves apart");
    }

    /// <summary>
    /// Handing the wheel over calls <c>OnHandoverIn</c>, and giving it back calls
    /// <c>OnHandoverOut</c>, once each per change.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>The aim resync rides on <c>OnHandoverIn</c>.</b> Skipping it makes a
    /// controller turn from a bearing nobody is pointing at, silently, in exactly
    /// the decisions a handover exists to hand it — <c>SearchBotTests</c> asserts
    /// the consequence on a real search; this asserts the call happens at all, which
    /// that test cannot separate from the search's own aim logic.
    /// </remarks>
    [Fact]
    public void TheWheelChangingHandsIsAnnouncedToTheController()
    {
        var world = World();
        var micro = new Stub("micro", cost: 1);
        var bot = new HybridBot(micro, Scripted(world)) { Rule = HandoverRule.Distance };

        var red = world.Place(PlayerClass.Soldier, Team.Red, 600, 850);
        var enemy = world.Place(PlayerClass.Soldier, Team.Blue, 680, 850);
        world.Step(20);

        for (var tick = 0; tick < 20; tick++)
        {
            if (tick == 10)
                world.State.Characters[enemy].X = world.State.Characters[red].X + 4000;

            var view = new WorldView(in world.State, world.Collision);
            bot.Poll(in view, red, tick);
            world.Step();
        }

        Assert.Equal(1, micro.HandoversIn);
        Assert.Equal(1, micro.HandoversOut);
    }
}
