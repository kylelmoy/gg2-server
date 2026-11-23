using GG2.Bots;
using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Tests;

/// <summary>
/// The parts of <see cref="HybridBot"/> that are the SEAM rather than the arm:
/// tier selection, the head split, and the counter invariant.
/// </summary>
/// <remarks>
/// <b>None of this was reachable before the wrapper split was merged.</b> The
/// two old classes each hard-coded one controller, so "which controller drives"
/// was not a decision anything could make and not a decision anything could test.
/// <para>
/// <b>These use a STUB controller on purpose.</b> The stub makes "who was asked,
/// and what did they answer" the only variable — and the real controller is
/// covered by <c>PolicyMatchTests</c>, which is the right split.
/// </para>
/// <para>
/// <b>Every assertion of an ABSENCE here states its own control</b>, because an
/// absence passes on a deleted feature. The pattern this repo arrived at after
/// four of eight moving-platform tests turned out to be vacuous — CONTRIBUTING.md.
/// </para>
/// </remarks>
public sealed class HybridBotSeamTests
{
    /// <summary>
    /// A controller that always decides, and records what it was asked.
    /// </summary>
    /// <remarks>
    /// <b>Decides on EVERY poll</b> — <c>AtDecisionBoundary</c> is always true —
    /// so the gate never has to wait for a cadence and a test can count decisions
    /// by counting ticks. The two real controllers have cadences; that is their
    /// tests' subject, not this file's.
    /// </remarks>
    private sealed class Stub : IMicroController, IBeamController
    {
        private readonly PlayerKeys _keys;

        internal Stub(string name, PlayerKeys keys = PlayerKeys.Attack)
        {
            Name = name;
            _keys = keys;
        }

        public string Name { get; }

        public bool DecidedThisPoll => true;

        /// <summary>Classes this refuses, to exercise the eligibility mask.</summary>
        internal PlayerClass? Refuses { get; init; }

        internal int Polls { get; private set; }

        internal int HandoversIn { get; private set; }

        internal int HandoversOut { get; private set; }

        public bool Drives(PlayerClass playerClass) => Refuses != playerClass;

        /// <summary>Whether this stub claims to heal, so a beaming medic may be handed to it.</summary>
        public bool OwnsBeam { get; init; }

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

    private static ScriptedBot Scripted(ScenarioWorld world, int tier = 5) =>
        new(world.Nav, new NavQuery(Team.Red), BotSkill.ForTier(tier), stagger: 0);

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
    /// The first eligible tier drives and the later tiers are never asked.
    /// </summary>
    /// <remarks>
    /// <b>The control is the second assertion.</b> "Tier 1 was never polled"
    /// passes on a bot that engaged nobody, so tier 0's poll count has to be
    /// positive for the zero to mean anything.
    /// </remarks>
    [Fact]
    public void TheFirstEligibleTierAlwaysDrives()
    {
        var world = World();
        var first = new Stub("first");
        var second = new Stub("second");
        var bot = new HybridBot([first, second], Scripted(world)) { Rule = HandoverRule.Distance };

        Run(bot, world, Duel(world), 12);

        Assert.True(first.Polls > 0, "nobody engaged, so the zero below proves nothing");
        Assert.Equal(0, second.Polls);
        Assert.Same(first, bot.Driving);
    }

    /// <summary>
    /// A tier that refuses this class is skipped, and a bot whose every tier
    /// refuses stays scripted.
    /// </summary>
    /// <remarks>
    /// <b>Asked per tick, not at construction</b> — a class can change mid-match
    /// and the wrapper cannot be rebuilt when it does.
    /// </remarks>
    [Fact]
    public void ATierThatRefusesTheClassIsSkippedAndTheLastOneFallsBackToScripted()
    {
        var world = World();
        var refuses = new Stub("refuses") { Refuses = PlayerClass.Soldier };
        var takes = new Stub("takes");
        var bot = new HybridBot([refuses, takes], Scripted(world)) { Rule = HandoverRule.Distance };

        Run(bot, world, Duel(world), 12);

        Assert.True(takes.Polls > 0, "nobody engaged, so the zero below proves nothing");
        Assert.Equal(0, refuses.Polls);

        var none = World();
        var refusesToo = new Stub("refuses") { Refuses = PlayerClass.Soldier };
        var scriptedOnly = new HybridBot(refusesToo, Scripted(none)) { Rule = HandoverRule.Distance };

        Run(scriptedOnly, none, Duel(none), 12);

        Assert.Equal(0, refusesToo.Polls);
        Assert.Equal(12, scriptedOnly.ScriptedTicks);
    }

    /// <summary>
    /// <c>ScriptedPolls == MicroTicks + ScriptedTicks</c>: the macro layer thinks
    /// on every tick, including the ones it does not drive.
    /// </summary>
    /// <remarks>
    /// <b>Arm 2's measured bug.</b> Polling the scripted layer only on the ticks it
    /// drove left it doing no objective planning during fights — no re-target, no
    /// re-path, no spot ban — and captures fell 20 to 7 over a hundred matches.
    /// <para>
    /// <b>This invariant was testable on ONE arm before the merge.</b>
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
        var micro = new Stub("micro");
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
    /// <b>This rule shipped with ZERO tests until the wrapper merge.</b> It was a
    /// branch inside <c>PolicyBot.Poll</c> that nothing in the suite, the bench or
    /// the host ever selected — so restructuring it was a change nothing could have
    /// caught, which is what made writing this a precondition rather than a bonus.
    /// <para>
    /// <b>The stub presses <c>Attack</c> and never a movement key</b>, so
    /// "movement came from the scripted layer" is checked by the attack bit
    /// surviving and by the controller being polled on every tick — no gate, no
    /// scripted ticks at all.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheHeadSplitTakesCombatFromTheControllerAndMovementFromTheScriptedLayer()
    {
        // TWO worlds built and stepped identically, one driven by a bare
        // ScriptedBot. Polling does not feed the world in this harness, so the two
        // evolve in lockstep and the bare bot's keys ARE the scripted half of the
        // merge — which is the only way to check the split without asserting
        // against a movement pattern somebody guessed.
        var split = World();
        var control = World();

        var micro = new Stub("micro", keys: PlayerKeys.Attack | PlayerKeys.Left);
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

        // The control for the equality above, which would otherwise pass on a
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
    /// <b>The aim resync rides on <c>OnHandoverIn</c>.</b> Skipping it makes a
    /// controller turn from a bearing nobody is pointing at, silently, in exactly
    /// the decisions a handover exists to hand it — <c>SearchBotTests</c> asserts
    /// the consequence on a real search; this asserts the call happens at all, which
    /// that test cannot separate from the search's own aim logic.
    /// </remarks>
    [Fact]
    public void TheWheelChangingHandsIsAnnouncedToTheController()
    {
        var world = World();
        var micro = new Stub("micro");
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

    /// <summary>
    /// At tier 5 the controller's output reaches the wire untouched, and the
    /// scripted layer's stream does not move.
    /// </summary>
    /// <remarks>
    /// <b>This is what keeps every policy measurement and pin valid</b>: they all
    /// run at tier 5. The control is the next test, where the same duel at tier 1
    /// does change the output.
    /// </remarks>
    [Fact]
    public void AtTheTopTierTheControllerIsNotHandicapped()
    {
        // A bare ScriptedBot on a world stepped in lockstep is the stream's
        // control: the scripted layer draws at tier 5 itself, so only a
        // difference from the bare bot's stream is the handicap's.
        var world = World();
        var control = World();
        var micro = new Stub("micro");
        var scripted = Scripted(world);
        var bare = Scripted(control);
        var bot = new HybridBot(micro, scripted) { Rule = HandoverRule.Distance };
        var red = Duel(world);
        var controlRed = Duel(control);
        var driven = 0;

        for (var tick = 0; tick < 60; tick++)
        {
            var view = new WorldView(in world.State, world.Collision);
            var input = bot.Poll(in view, red, tick);

            var controlView = new WorldView(in control.State, control.Collision);
            bare.Poll(in controlView, controlRed, tick);

            if (bot.MicroDrove)
            {
                driven++;
                Assert.Equal(new PlayerInput(PlayerKeys.Attack, 0, 0), input);
            }

            world.Step();
            control.Step();
        }

        Assert.True(driven > 0, "the controller never drove");

        // Equal states draw equal numbers.
        var mine = scripted.Random;
        var theirs = bare.Random;
        Assert.Equal(theirs.Next(1), mine.Next(1));
    }

    /// <summary>
    /// At tier 1 the controller's trigger waits out the fire delay, is sometimes
    /// held, and its aim is off by up to the tier's error.
    /// </summary>
    /// <remarks>
    /// <b>The control is that Attack DOES get through</b>: a handicap that withheld
    /// the trigger for ever would pass every other assertion here.
    /// </remarks>
    [Fact]
    public void BelowTheTopTierTheControllerCarriesTheDifficulty()
    {
        var world = World();
        var micro = new Stub("micro");
        var scripted = Scripted(world, tier: 1);
        var bot = new HybridBot(micro, scripted) { Rule = HandoverRule.Distance };
        var red = Duel(world);
        var skill = BotSkill.ForTier(1);
        var maxError = (int)Math.Ceiling(skill.PolicyAimErrorDeg * 65536 / 360);
        int driven = 0, fired = 0, withheldAfterDelay = 0, offAim = 0;

        for (var tick = 0; tick < 300; tick++)
        {
            var view = new WorldView(in world.State, world.Collision);
            var input = bot.Poll(in view, red, tick);

            if (bot.MicroDrove)
            {
                driven++;

                var attack = input.IsDown(PlayerKeys.Attack);
                var sinceAcquired = tick - scripted.Combat.TargetAt;

                if (sinceAcquired < skill.FireDelayTicks)
                    Assert.False(attack, $"fired {sinceAcquired} ticks after acquisition");
                else if (attack)
                    fired++;
                else
                    withheldAfterDelay++;

                var error = (short)input.NetAimDirection;
                Assert.InRange(error, -maxError, maxError);

                if (error != 0)
                    offAim++;
            }

            world.Step();
        }

        Assert.True(driven > skill.FireDelayTicks, "the controller barely drove");
        Assert.True(fired > 0, "the trigger never got through");
        Assert.True(withheldAfterDelay > 0, "the hold-fire roll never came up");
        Assert.True(offAim > 0, "the aim was never off");
    }

    /// <summary>
    /// A release band keeps a controller already driving out to
    /// <see cref="HybridBot.ReleaseRange"/>, and never widens where it takes the
    /// wheel.
    /// </summary>
    /// <remarks>
    /// Under <see cref="HandoverRule.Distance"/>, so the range is the only term.
    /// The enemy is moved, not the bot, so nothing about the bot's own state
    /// changes between the steps. The control is the same sequence with no band,
    /// which gives the wheel back at 200px.
    /// </remarks>
    [Theory]
    [InlineData(double.NaN, false)]
    [InlineData(250.0, true)]
    public void AReleaseBandKeepsTheWheelInAFightAlreadyTaken(double release, bool keptAt200)
    {
        var world = World();
        var micro = new Stub("policy", PlayerKeys.None);
        var bot = new HybridBot(micro, Scripted(world)) { Rule = HandoverRule.Distance, ReleaseRange = release };
        var red = Duel(world);
        var blue = red + 1;
        var x = world.State.Characters[red].X;

        Run(bot, world, red, 5);
        Assert.Same(micro, bot.Driving);

        world.State.Characters[blue].X = x + 200;
        Run(bot, world, red, 3);
        Assert.Equal(keptAt200, bot.Driving is not null);

        // Past the band, it goes back whatever the band is.
        world.State.Characters[blue].X = x + 280;
        Run(bot, world, red, 3);
        Assert.Null(bot.Driving);

        // And at 200px with no controller driving, the band does not take it.
        world.State.Characters[blue].X = x + 200;
        Run(bot, world, red, 3);
        Assert.Null(bot.Driving);
    }

    /// <summary>
    /// <see cref="HybridBot.EngageNearOwnGoal"/> hands over a fight the engage
    /// hold leaves to the scripted layer only because the bot stands on its goal.
    /// </summary>
    /// <remarks>
    /// The goal is pinned 100px from the bot, every tick, so the hold's
    /// near-goal term is false throughout and nothing else is. Not ON the bot:
    /// standing on a goal is arrival, which clears it. The control is
    /// the shipped gate, which leaves the wheel with the scripted layer, and
    /// the premise check is that the hold's other terms said fight.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NearItsOwnGoalTheFightIsHandedOverOnlyWhenAsked(bool nearGoal)
    {
        var world = World();
        var micro = new Stub("policy", PlayerKeys.None);
        var scripted = Scripted(world);
        var bot = new HybridBot(micro, scripted) { EngageNearOwnGoal = nearGoal };
        var red = Duel(world);
        var otherTermsSaidFight = false;

        for (var tick = 0; tick < 40; tick++)
        {
            ref readonly var self = ref world.State.Characters[red];
            scripted.Path.HasGoal = true;
            scripted.Path.GoalX = self.X - 100;
            scripted.Path.GoalY = self.Y;

            var view = new WorldView(in world.State, world.Collision);
            bot.Poll(in view, red, tick);
            world.Step();

            Assert.False(scripted.WantsToEngage, "the bot is on its goal, so the hold should be off");
            otherTermsSaidFight |= scripted.WantsToEngageNearGoal;
        }

        Assert.True(otherTermsSaidFight, "the hold's other terms never said fight, so this gates nothing");
        Assert.Equal(nearGoal, micro.Polls > 0);
    }

    /// <summary>
    /// A beaming medic is handed only to a controller that owns the beam
    /// (<see cref="IBeamController"/>, gg2-sim's medic layout); any other
    /// controller leaves it with the scripted layer.
    /// </summary>
    /// <remarks>
    /// The premise is that the scripted medic really had a patient on the ticks
    /// it was asked, or the refusal would be the range's and not the beam's.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ABeamingMedicIsHandedOnlyToAControllerThatOwnsTheBeam(bool ownsBeam)
    {
        var world = World();
        var micro = new Stub("policy", PlayerKeys.None) { OwnsBeam = ownsBeam };
        var scripted = Scripted(world);
        var bot = new HybridBot(micro, scripted) { Rule = HandoverRule.Distance };
        var medic = world.Place(PlayerClass.Medic, Team.Red, 600, 850);
        var patient = world.Place(PlayerClass.Soldier, Team.Red, 620, 850);
        world.Place(PlayerClass.Soldier, Team.Blue, 680, 850);
        world.Step(20);
        var beaming = 0;

        for (var tick = 0; tick < 40; tick++)
        {
            world.State.Characters[patient].Hp = 40;
            var view = new WorldView(in world.State, world.Collision);
            bot.Poll(in view, medic, tick);

            if (scripted.Combat.Ally == patient)
                beaming++;

            world.Step();
        }

        Assert.True(beaming > 30, $"the scripted medic had a patient on only {beaming} ticks, so this gates nothing");
        Assert.Equal(ownsBeam, micro.Polls > 0);
    }

    /// <summary>A <see cref="Stub"/> that also counts sightings and wants to build on every tick.</summary>
    private sealed class SightingBuilder : IMicroController, ISightingController, IBuildController
    {
        private readonly Stub _inner = new("sighting");

        internal int Sightings { get; private set; }

        internal int SightingsWhileDriving { get; private set; }

        internal bool IsDriving { get; set; }

        public string Name => _inner.Name;

        public bool DecidedThisPoll => true;

        public bool Drives(PlayerClass playerClass) => true;

        public bool AtDecisionBoundary(int slot) => true;

        public void OnHandoverIn(in WorldView world, int slot) => IsDriving = true;

        public void OnHandoverOut() => IsDriving = false;

        public PlayerInput Poll(in WorldView world, int slot, int tick) => _inner.Poll(in world, slot, tick);

        public void Sight(in WorldView world, int slot, int tick)
        {
            Sightings++;

            if (IsDriving)
                SightingsWhileDriving++;
        }

        public bool OwnsBuild { get; init; } = true;

        public bool WantsBuild(in WorldView world, int slot, int tick) => true;
    }

    /// <summary>
    /// An <see cref="ISightingController"/> tier is told what its body sees on
    /// every tick the scripted layer drives, and never while it drives itself.
    /// </summary>
    /// <remarks>
    /// gg2-sim's base layout v2 net keeps a sighting memory its environment fills
    /// on every step, scripted or not; <c>ref/Matches.cs</c> sights an idle hybrid
    /// before each step. The control is the out-of-range half: with nobody near,
    /// the tier never drives, so every tick is a sighting.
    /// </remarks>
    [Fact]
    public void ASightingTierLearnsWhileTheScriptedLayerDrives()
    {
        var world = World();
        var far = world.Place(PlayerClass.Soldier, Team.Red, 600, 850);
        world.Place(PlayerClass.Soldier, Team.Blue, 1600, 850);
        world.Step(20);
        var idle = new SightingBuilder();
        Run(new HybridBot(idle, Scripted(world)) { Rule = HandoverRule.Distance }, world, far, 12);
        Assert.Equal(12, idle.Sightings);

        var near = World();
        var driver = new SightingBuilder();
        var bot = new HybridBot(driver, Scripted(near)) { Rule = HandoverRule.Distance };
        Run(bot, near, Duel(near), 12);

        Assert.Same(driver, bot.Driving);
        Assert.Equal(0, driver.SightingsWhileDriving);
        Assert.True(driver.Sightings < 12, "it drove from the first tick on, so it should not have been sighted on all twelve");
    }

    /// <summary>
    /// While an <see cref="IBuildController"/> that owns the build drives, its
    /// <c>BuildSentry</c> goes out; when it does not own it, or does not drive,
    /// none does (the scripted layer never builds).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ABuildControllerSendsTheBuildCommandWhileItDrives(bool ownsBuild)
    {
        var world = World();
        var red = world.Place(PlayerClass.Engineer, Team.Red, 600, 850);
        world.Place(PlayerClass.Soldier, Team.Blue, 680, 850);
        world.Step(20);
        var tier = new SightingBuilder { OwnsBuild = ownsBuild };
        var bot = new HybridBot(tier, Scripted(world)) { Rule = HandoverRule.Distance };
        int driven = 0, built = 0, idleBuilt = 0;

        for (var tick = 0; tick < 12; tick++)
        {
            var view = new WorldView(in world.State, world.Collision);
            var commands = default(PlayerCommandBuffer);
            bot.PollCommands(in view, red, tick, ref commands);
            var sent = false;

            for (var i = 0; i < commands.Count; i++)
                sent |= commands[i].Kind == PlayerCommandKind.BuildSentry;

            if (bot.Driving is not null)
                driven++;

            if (sent && bot.Driving is not null)
                built++;
            else if (sent)
                idleBuilt++;

            bot.Poll(in view, red, tick);
            world.Step();
        }

        // Driving is counted at command time, so it is the wheel the previous
        // tick's Poll settled: every such tick is one the tier answers for.
        Assert.True(driven > 0, "the tier never drove, so this gates nothing");
        Assert.Equal(0, idleBuilt);
        Assert.Equal(ownsBuild ? driven : 0, built);
    }
}
