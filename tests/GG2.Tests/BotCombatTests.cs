using GG2.Bots;
using GG2.Maps;
using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Tests;

/// <summary>
/// The combat controller — <c>botCombatUpdate.gml</c>, <c>botClassKeys.gml</c>,
/// <c>botSkillApply.gml</c> and <c>botAimSpread.gml</c>.
/// </summary>
/// <remarks>
/// The see → acquire → aim-settled → fire chain, which is the whole difficulty
/// model. Every gate here is asserted by <em>counting ticks</em> rather than by
/// checking a final state, because each one is about <b>when</b> something is
/// allowed and a test that only looks at the end cannot tell a gate that fires
/// late from one that is missing.
/// <para>
/// The open stretch is the measured one — (380, 268) on ctf_truefort, clear
/// for 1,000px — for the reason <see cref="BotTargetingTests"/> records: picking
/// coordinates off a screenshot is how a combat test ends up measuring a wall.
/// </para>
/// </remarks>
public sealed class BotCombatTests
{
    private const double FloorY = 268;

    private const double LeftX = 380;

    private static CollisionWorld Collision()
    {
        return new CollisionWorld(LevelMap.FromPng("ctf_truefort", ReferenceTree.ReadMap("ctf_truefort")));
    }

    private static WorldState World(params (Team Team, PlayerClass Class, double X, double Y)[] people)
    {
        var state = default(WorldState);

        for (var slot = 0; slot < people.Length; slot++)
        {
            state.AddPlayer(slot);
            state.Players[slot].Team = people[slot].Team;
            state.Players[slot].Class = people[slot].Class;

            var character = CharacterState.Fresh(people[slot].Class, people[slot].Team);
            character.X = people[slot].X;
            character.Y = people[slot].Y;
            character.OnGround = true;

            state.Characters[slot] = character;
            state.Weapons[slot] = WeaponState.Spawn(people[slot].Class, people[slot].X, people[slot].Y);
            state.Alive[slot] = true;
        }

        return state;
    }

    /// <summary>A soldier and an enemy scout 300px apart on the open stretch.</summary>
    private static WorldState Duel(PlayerClass playerClass = PlayerClass.Soldier) =>
        World(
            (Team.Red, playerClass, LeftX, FloorY),
            (Team.Blue, PlayerClass.Scout, LeftX + 300, FloorY));

    /// <summary>
    /// Runs the controller for a number of ticks and reports the first tick each
    /// gate opened on.
    /// </summary>
    /// <remarks>
    /// ⛔ <b><c>in WorldState</c>, never by value, and this is not a
    /// micro-optimisation.</b> A method that takes a <c>WorldState</c> BY VALUE
    /// and then builds a <see cref="WorldView"/> over it inside a loop that
    /// calls <see cref="BotCombat.Step"/> is rejected by the .NET 10 runtime on
    /// linux-x64 with <c>InvalidProgramException: Common Language Runtime
    /// detected an invalid program</c> — the same assembly runs on Windows.
    /// Reproduced on three Linux hosts across two CPU vendors and both 10.0.11
    /// and 10.0.12, with IL compiled on Windows, so it is the runtime and
    /// neither the compiler nor the machine. Passing the state by <c>in</c>
    /// makes the identical loop run.
    /// <para>
    /// Nothing shipped hits it: the server and the simulation pass a
    /// <c>WorldState</c> by <c>ref</c> or <c>in</c> everywhere, which is why the
    /// fleet runs bots on Linux perfectly while these three tests could not
    /// even start. docs/SIMULATION.md, "The same build on two machines".
    /// </para>
    /// </remarks>
    private static (int Acquired, int Aimed, int Fired) Chain(
        in WorldState state, BotSkill skill, int ticks = 400, int stagger = 0)
    {
        var collision = Collision();
        var combat = new BotCombatState { Stagger = stagger };
        var random = state.Random;

        var acquired = -1;
        var aimed = -1;
        var fired = -1;

        for (var tick = 0; tick < ticks; tick++)
        {
            var keys = BotCombat.Step(
                combat, new WorldView(in state, collision), 0, tick, skill, ref random, null,
                out var aim, out _);

            if (acquired < 0 && combat.Target.Exists)
                acquired = tick;

            if (aimed < 0 && aim != 0)
                aimed = tick;

            if (fired < 0 && keys.HasFlag(PlayerKeys.Attack))
                fired = tick;
        }

        return (acquired, aimed, fired);
    }

    /// <summary>
    /// The four gates open in order, and each is worth its own ticks.
    /// </summary>
    /// <remarks>
    /// <b>The acquire gate holds the AIM, not just the trigger</b>, and that is
    /// the difference between a bot that looks slow and one that looks blind. A
    /// port that gated only the trigger would turn instantly and then wait, which
    /// is exactly what a weak bot must not look like.
    /// </remarks>
    [Fact]
    public void TheFourGatesOpenInOrder()
    {
        var weak = BotSkill.ForTier(1);
        var (acquired, aimed, fired) = Chain(Duel(), weak);

        Assert.True(acquired >= 0, "no target was ever picked");
        Assert.True(aimed >= 0, "the aim never moved");
        Assert.True(fired >= 0, "the trigger was never pulled");

        // The aim does not begin to move until the acquire delay has passed.
        Assert.True(
            aimed - acquired >= weak.AcquireTicks - 1,
            $"the aim moved {aimed - acquired} ticks after acquiring, inside the "
            + $"{weak.AcquireTicks}-tick acquire delay");

        // ...and the trigger waits the fire delay on top of that.
        Assert.True(
            fired - acquired >= weak.AcquireTicks + weak.FireDelayTicks,
            $"fired {fired - acquired} ticks after acquiring, inside "
            + $"{weak.AcquireTicks} + {weak.FireDelayTicks}");
    }

    /// <summary>
    /// A stronger bot reacts sooner, on every gate.
    /// </summary>
    /// <remarks>
    /// The claim the whole difficulty model rests on. Asserted as a
    /// <em>relationship between tiers</em> rather than as numbers, because the
    /// numbers are the tuning table and pinning them would pin
    /// <see cref="BotSkill"/> to itself.
    /// </remarks>
    [Fact]
    public void AStrongerBotReactsSooner()
    {
        var weak = Chain(Duel(), BotSkill.ForTier(1));
        var strong = Chain(Duel(), BotSkill.ForTier(5));

        Assert.True(
            strong.Fired < weak.Fired,
            $"tier 5 fired at tick {strong.Fired}, no sooner than tier 1's {weak.Fired}");

        Assert.True(
            strong.Aimed <= weak.Aimed,
            $"tier 5 began aiming at {strong.Aimed}, later than tier 1's {weak.Aimed}");
    }

    /// <summary>
    /// The skill table is flat at both ends and ramps in three unequal segments.
    /// </summary>
    /// <remarks>
    /// The last segment is 0.75-0.95 — two thirds the width of the two below
    /// it — so the top tier moves further per point of skill. That is deliberate
    /// and is why it is a table rather than a formula.
    /// </remarks>
    [Fact]
    public void TheSkillRampIsFlatAtBothEndsAndUnevenBetween()
    {
        Assert.Equal(10, BotSkill.Lerp(0.0, 10, 20, 30, 40));
        Assert.Equal(10, BotSkill.Lerp(0.15, 10, 20, 30, 40));
        Assert.Equal(40, BotSkill.Lerp(0.95, 10, 20, 30, 40));
        Assert.Equal(40, BotSkill.Lerp(1.0, 10, 20, 30, 40));

        // The anchors land exactly on the segment joins.
        Assert.Equal(20, BotSkill.Lerp(0.45, 10, 20, 30, 40), 9);
        Assert.Equal(30, BotSkill.Lerp(0.75, 10, 20, 30, 40), 9);

        // The last segment is steeper: 10 units over 0.2 against 10 over 0.3.
        var lastSlope = (BotSkill.Lerp(0.95, 10, 20, 30, 40) - BotSkill.Lerp(0.75, 10, 20, 30, 40)) / 0.2;
        var middleSlope = (BotSkill.Lerp(0.75, 10, 20, 30, 40) - BotSkill.Lerp(0.45, 10, 20, 30, 40)) / 0.3;

        Assert.True(lastSlope > middleSlope);

        // Tier 5 is the top of the ramp. Since D3 tier 4 plays the same skill and
        // differs only in aiming like a person; since D10 tiers 1-3 are spread
        // further apart, because tier 4's legs widened the top step.
        Assert.Equal(BotSkill.For(0.20) with { HumanAim = true }, BotSkill.ForTier(1));
        Assert.Equal(BotSkill.For(0.95) with { HumanAim = true }, BotSkill.ForTier(4));
        Assert.Equal(BotSkill.For(0.95), BotSkill.ForTier(5));
    }

    /// <summary>
    /// A bot with nothing to shoot at faces the way it is walking.
    /// </summary>
    /// <remarks>
    /// The threshold is <c>BOT_REST_SPEED</c> — GG2's own "is it really
    /// moving" floor — so a bot standing still keeps whatever it was last
    /// pointing at rather than snapping to the right. A port that used
    /// <c>&gt; 0</c> would have every idle bot facing east.
    /// </remarks>
    [Fact]
    public void WithNothingToShootTheAimFollowsTheWalk()
    {
        var state = World((Team.Red, PlayerClass.Soldier, LeftX, FloorY));
        var collision = Collision();
        // Both, not just AimDir. The slew always runs towards AimWant, so a
        // state whose two halves disagree is a bot already mid-turn — which is
        // exactly what a "standing still leaves the aim alone" fixture must not
        // be. The reference starts both at 0 in Player's Create for the same
        // reason.
        var combat = new BotCombatState { AimDir = 90, AimWant = 90 };
        var random = state.Random;
        var skill = BotSkill.ForTier(5);

        // Standing still: the aim is left alone.
        for (var tick = 0; tick < 60; tick++)
        {
            BotCombat.Step(
                combat, new WorldView(in state, collision), 0, tick, skill, ref random, null, out _, out _);
        }

        Assert.Equal(90, combat.AimDir, 9);

        // And drifting BELOW the rest speed is still standing still. A port
        // that used `> 0` would turn a bot that is merely coasting to a stop, and
        // the difference is invisible at exactly zero — which is why this is
        // asserted at 0.1 rather than at 0.
        state.Characters[0].HSpeed = BotConstants.RestSpeed / 2;

        for (var tick = 400; tick < 460; tick++)
        {
            BotCombat.Step(
                combat, new WorldView(in state, collision), 0, tick, skill, ref random, null, out _, out _);
        }

        Assert.Equal(90, combat.AimDir, 9);

        // Walking right: it slews round to 0.
        state.Characters[0].HSpeed = 5;

        for (var tick = 60; tick < 200; tick++)
        {
            BotCombat.Step(
                combat, new WorldView(in state, collision), 0, tick, skill, ref random, null, out _, out _);
        }

        Assert.Equal(0, combat.AimDir, 9);

        // And left.
        state.Characters[0].HSpeed = -5;

        for (var tick = 200; tick < 340; tick++)
        {
            BotCombat.Step(
                combat, new WorldView(in state, collision), 0, tick, skill, ref random, null, out _, out _);
        }

        Assert.Equal(180, combat.AimDir, 9);
    }

    /// <summary>
    /// The trigger is withheld until the slew has settled, even when every clock
    /// says fire.
    /// </summary>
    /// <remarks>
    /// <b>The fourth gate, and the only one that is not a clock.</b> A bot
    /// whose acquire and fire delays have both expired still must not shoot while
    /// it is turning — otherwise a weak bot with a long turn rate sprays at the
    /// wall it was facing when it acquired.
    /// <para>
    /// It has to be tested by <em>constructing</em> an unsettled aim. On a
    /// stationary target the slew settles long before the fire delay expires, so
    /// a test that just runs the chain passes with the whole gate deleted — which
    /// is what the first draft did.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheTriggerWaitsForTheAimToSettle()
    {
        var state = Duel();
        var collision = Collision();
        var skill = BotSkill.ForTier(5);
        var random = state.Random;

        // Every clock long expired, the target in hand, and the aim pointing the
        // opposite way with the solve suppressed so it stays there.
        BotCombatState Unsettled(double aimDir) => new()
        {
            Target = new BotTarget(BotTargetKind.Character, 1, LeftX + 300, FloorY, true),
            Visible = true,
            TargetAt = -1000,
            LastSeenAt = 0,
            AimWant = 0,
            AimDir = aimDir,
            AimAt = int.MaxValue,
            PerceiveAt = int.MaxValue,
            AimX = LeftX + 300,
            AimY = FloorY,
        };

        // Pointing 180 degrees away: still turning, so no trigger.
        var turning = Unsettled(180);
        var keys = BotCombat.Step(
            turning, new WorldView(in state, collision), 0, 0, skill, ref random, null, out _, out _);

        Assert.False(
            keys.HasFlag(PlayerKeys.Attack),
            "the trigger was pulled while the aim was 180 degrees off");

        // Already on target: it fires.
        var settled = Unsettled(0);
        var onTarget = BotCombat.Step(
            settled, new WorldView(in state, collision), 0, 0, skill, ref random, null, out _, out _);

        Assert.True(
            onTarget.HasFlag(PlayerKeys.Attack),
            "the trigger was withheld from a bot already on target with every clock expired");
    }

    /// <summary>
    /// The aim slews at the skill's turn rate rather than snapping.
    /// </summary>
    /// <remarks>
    /// There is no filter and no smoothing: tracking lag, overshoot and
    /// settling are all consequences of the perceive interval, the aim interval
    /// and the turn rate, and of nothing else. A port that snapped would have a
    /// tier-1 bot as accurate as a tier-5 one on a stationary target.
    /// </remarks>
    [Fact]
    public void TheAimSlewsAtTheTurnRate()
    {
        var state = World((Team.Red, PlayerClass.Soldier, LeftX, FloorY));
        var collision = Collision();
        var skill = BotSkill.ForTier(1);
        var combat = new BotCombatState { AimDir = 180, AimWant = 180 };
        var random = state.Random;

        state.Characters[0].HSpeed = 5; // wants 0

        var before = combat.AimDir;
        BotCombat.Step(combat, new WorldView(in state, collision), 0, 0, skill, ref random, null, out _, out _);

        var moved = Math.Abs(BotTargeting.AngleDelta(combat.AimDir, before));

        Assert.Equal(skill.TurnRate, moved, 6);

        // And it snaps once the remainder is inside one tick's turn, or the
        // aim oscillates either side of the target for ever and the settle gate
        // never opens.
        var nearly = new BotCombatState { AimDir = skill.TurnRate / 2, AimWant = skill.TurnRate / 2 };
        BotCombat.Step(nearly, new WorldView(in state, collision), 0, 0, skill, ref random, null, out _, out _);

        Assert.Equal(0, nearly.AimDir, 9);
    }

    /// <summary>
    /// A target behind a wall is kept for a memory window and then dropped.
    /// </summary>
    /// <remarks>
    /// <b>The bot holds its aim on the last known position and does not refresh
    /// it through the wall</b>, so it engages the moment the sightline opens
    /// instead of re-paying the whole acquire chain then. The snapshot is only
    /// refreshed while the target is visible, which is what makes that true.
    /// </remarks>
    [Fact]
    public void ATargetBehindAWallIsKeptThenDropped()
    {
        // 120px straight down from the open stretch is inside the map's geometry
        // — measured, see BotTargetingTests.
        var state = World(
            (Team.Red, PlayerClass.Soldier, LeftX, FloorY),
            (Team.Blue, PlayerClass.Scout, LeftX, FloorY + 120));

        var collision = Collision();
        var combat = new BotCombatState();
        var random = state.Random;
        var skill = BotSkill.ForTier(5);

        // It is acquired blind, so it is a target with Visible false.
        BotCombat.Step(combat, new WorldView(in state, collision), 0, 0, skill, ref random, null, out _, out _);

        Assert.True(combat.Target.Exists, "a blocked target inside the blind radius was never picked");

        var seenAt = combat.LastSeenAt;
        var heldFor = -1;

        for (var tick = 1; tick < 200 && heldFor < 0; tick++)
        {
            BotCombat.Step(
                combat, new WorldView(in state, collision), 0, tick, skill, ref random, null, out _, out _);

            if (!combat.Target.Exists)
                heldFor = tick - seenAt;
        }

        Assert.True(heldFor > 0, "a target behind a wall was never dropped");
        Assert.True(
            heldFor > BotConstants.TargetPeriod,
            $"it was dropped after only {heldFor} ticks; the memory window is meant to outlast a search period");
    }

    /// <summary>
    /// What one class wants to press at a target this far away.
    /// </summary>
    /// <remarks>
    /// One world per call, built inside. A <c>WorldState</c> is about 160KB —
    /// the clone budget <c>WorldStateTests</c> audits — so four of them as locals
    /// in one test method is 640KB of stack and a genuine
    /// <c>StackOverflowException</c>. Found the hard way: the first draft held
    /// four and the test host crashed rather than failed.
    /// </remarks>
    private static PlayerKeys Wanted(PlayerClass playerClass, double distance, bool zoomed = false, double charge = 0)
    {
        var state = World(
            (Team.Red, playerClass, LeftX, FloorY),
            (Team.Blue, PlayerClass.Scout, LeftX + distance, FloorY));

        state.Characters[0].Zoomed = zoomed;
        state.Weapons[0].Charge = charge;

        var combat = new BotCombatState
        {
            Target = new BotTarget(BotTargetKind.Character, 1, LeftX + distance, FloorY, true),
        };

        return BotClassKeys.For(combat, new WorldView(in state, Collision()), 0, distance, 0);
    }

    /// <summary>
    /// Each class presses what its own weapon wants, not a generic trigger.
    /// </summary>
    /// <remarks>
    /// <b>A medic's needle gun is on the RIGHT click</b> — the Medigun's left
    /// is the heal beam — which is also why the medic is the only class whose
    /// SPECIAL is gated by the fire delay. And a pyro's flame reaches
    /// <c>BOT_FLAME_REACH</c>, so it holds fire beyond that rather than emptying
    /// its tank into the air.
    /// </remarks>
    [Fact]
    public void EachClassPressesWhatItsWeaponWants()
    {
        // A medic shoots on SPECIAL.
        Assert.Equal(PlayerKeys.Special, Wanted(PlayerClass.Medic, 200));

        // A pyro fires inside its flame's reach and not beyond it.
        Assert.Equal(PlayerKeys.Attack, Wanted(PlayerClass.Pyro, BotConstants.FlameReach - 10));
        Assert.Equal(PlayerKeys.None, Wanted(PlayerClass.Pyro, BotConstants.FlameReach + 10));

        // A soldier refuses a shot inside its own splash-safe band, because a
        // rocket at that range kills the shooter.
        Assert.Equal(PlayerKeys.Attack, Wanted(PlayerClass.Soldier, BotConstants.SplashSafe + 10));
        Assert.Equal(PlayerKeys.None, Wanted(PlayerClass.Soldier, BotConstants.SplashSafe - 10));

        // The scout, the heavy, the engineer and Quote just shoot.
        Assert.Equal(PlayerKeys.Attack, Wanted(PlayerClass.Scout, 100));
        Assert.Equal(PlayerKeys.Attack, Wanted(PlayerClass.Heavy, 100));
    }

    /// <summary>
    /// A scoped sniper waits for a charge proportional to the range.
    /// </summary>
    /// <remarks>
    /// <b>The Rifle's damage IS its range</b>, so a scoped sniper firing early
    /// simply cannot reach — and one that waited for a full charge at every
    /// distance would never shoot at anything close. Unscoped it fires
    /// immediately, because there is no charge to wait for.
    /// </remarks>
    [Fact]
    public void AScopedSniperWaitsForACharge()
    {
        // Unscoped: fire at once, at any range.
        Assert.Equal(PlayerKeys.Attack, Wanted(PlayerClass.Sniper, 800));

        // Scoped with no charge: nothing.
        Assert.Equal(PlayerKeys.None, Wanted(PlayerClass.Sniper, 800, zoomed: true));

        // Half charged: enough for less than half the class's radius, not for the
        // far edge.
        var half = Weapons.RifleChargeTime * 0.5;
        var radius = BotTargeting.ClassRange(PlayerClass.Sniper);

        Assert.Equal(PlayerKeys.Attack, Wanted(PlayerClass.Sniper, radius * 0.4, zoomed: true, charge: half));
        Assert.Equal(PlayerKeys.None, Wanted(PlayerClass.Sniper, radius * 0.9, zoomed: true, charge: half));
    }

    /// <summary>
    /// The aim spread decays the longer a target is watched, and widens at close
    /// range.
    /// </summary>
    /// <remarks>
    /// <b>The close-range penalty makes EVERY tier worse in a brawl</b>, and it
    /// is an anti-frustration measure rather than a difficulty knob: without it
    /// bots are unbeatable at point blank. The factor <em>divides</em> and is
    /// below 1 near the target, so reading it as a multiplier inverts the whole
    /// intent.
    /// </remarks>
    [Fact]
    public void TheSpreadDecaysWithWatchingAndWidensUpClose()
    {
        const double Error = 1;
        const double Cone = 20;

        var fresh = BotCombat.Spread(Error, Cone, 1, moving: false, sinceSeen: 0, distance: 500);
        var watched = BotCombat.Spread(Error, Cone, 1, moving: false, sinceSeen: 300, distance: 500);

        Assert.True(watched < fresh, $"watching for 300 ticks did not tighten the aim: {watched} against {fresh}");

        // It decays towards the floor and never below it.
        var forever = BotCombat.Spread(Error, Cone, 1, moving: false, sinceSeen: 100_000, distance: 500);
        Assert.True(forever >= Error, $"the spread decayed below its floor to {forever}");

        // A moving target is scaled by the move multiplier.
        var still = BotCombat.Spread(Error, Cone, 0.5, moving: false, sinceSeen: 0, distance: 500);
        var moving = BotCombat.Spread(Error, Cone, 0.5, moving: true, sinceSeen: 0, distance: 500);

        Assert.Equal(still * 0.5, moving, 9);

        // And the close penalty WIDENS: at point blank the same shot is
        // 1/BOT_CLOSE_PENALTY times as loose.
        var far = BotCombat.Spread(Error, Cone, 1, moving: false, sinceSeen: 0, distance: BotConstants.CloseRange);
        var near = BotCombat.Spread(Error, Cone, 1, moving: false, sinceSeen: 0, distance: 0);

        Assert.True(near > far, $"point blank was tighter than range: {near} against {far}");
        Assert.Equal(far / BotConstants.ClosePenalty, near, 9);
    }

    /// <summary>
    /// The whole chain is deterministic: the same world and stream twice give the
    /// same ticks.
    /// </summary>
    /// <remarks>
    /// docs/BOTAI.md item 2 from the combat side. The aim error and the
    /// hold-fire roll both draw from <see cref="WorldState.Random"/> rather than
    /// a private stream, which is what makes a forked world fork the bot's
    /// behaviour with it — and is why <c>randomize()</c> is deliberately not
    /// transcribed anywhere in this port.
    /// </remarks>
    [Fact]
    public void TheChainIsDeterministic()
    {
        var first = Chain(Duel(), BotSkill.ForTier(3));
        var second = Chain(Duel(), BotSkill.ForTier(3));

        Assert.Equal(first, second);

        // And the stagger genuinely shifts the cadence, or a server full of
        // bots would run every target search on one frame.
        var staggered = Chain(Duel(), BotSkill.ForTier(3), stagger: 7);
        Assert.NotEqual(first.Acquired, staggered.Acquired);
    }
}
