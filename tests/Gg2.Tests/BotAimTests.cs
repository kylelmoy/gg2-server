using Gg2.Bots;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// The aim solver — <c>botAimSolve.gml</c>, <c>botAimLead.gml</c>,
/// <c>botWeaponBallistics.gml</c> and <c>botFallPredict.gml</c>.
/// </summary>
/// <remarks>
/// <b>This is an exact ballistic intercept solver against an opponent model
/// of "constant velocity plus gravity"</b>, and docs/BOTAI.md opens by saying
/// that is the wrong model against a human who reacts to the shot being fired.
/// It is ported as the <em>baseline</em> the document asks for, and these tests
/// gate it as such: what they assert is that the solver does what the reference's
/// does, not that its answers are good.
/// </remarks>
public sealed class BotAimTests
{
    private static CharacterState Shooter(
        PlayerClass playerClass = PlayerClass.Soldier,
        double x = 0, double y = 0, double hSpeed = 0)
    {
        var character = CharacterState.Fresh(playerClass, Team.Red);
        character.X = x;
        character.Y = y;
        character.HSpeed = hSpeed;
        return character;
    }

    /// <summary>
    /// Every class's ballistics are the bot's model of its weapon, not the
    /// weapon.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately simpler than the simulation's.</b> A scattergun blast is
    /// six pellets with a spread and a per-pellet lifetime; the aim model calls
    /// it one projectile at 13 px/tick falling at 0.15. Do not "fix" one to match
    /// <c>Gg2.Simulation.Projectiles</c> — a bot that aims with the real numbers
    /// is a different bot, and every one of these was tuned against play.
    /// <para>
    /// <b>Speed zero means "do not lead"</b>, which is how the hitscan and
    /// stored-target weapons fall out of the model without a special case: the
    /// sniper's Rifle and the medic's Medigun have no projectile to lead.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(PlayerClass.Scout, 13.0, 0.15)]
    [InlineData(PlayerClass.Engineer, 13.0, 0.15)]
    [InlineData(PlayerClass.Heavy, 12.5, 0.15)]
    [InlineData(PlayerClass.Spy, 21.0, 0.15)]
    [InlineData(PlayerClass.Soldier, 12.0, 0.0)]
    [InlineData(PlayerClass.Demoman, 12.0, 0.2)]
    [InlineData(PlayerClass.Pyro, 8.25, 0.15)]
    [InlineData(PlayerClass.Quote, 10.0, 0.0)]
    [InlineData(PlayerClass.Sniper, 0.0, 0.0)]
    [InlineData(PlayerClass.Medic, 0.0, 0.0)]
    public void EachClassHasItsOwnBallistics(PlayerClass playerClass, double speed, double gravity)
    {
        var (actualSpeed, actualGravity, _) = BotAim.Ballistics(Shooter(playerClass));

        Assert.Equal(speed, actualSpeed);
        Assert.Equal(gravity, actualGravity);
    }

    /// <summary>
    /// Drift is the shooter's own speed, and only for the three weapons whose
    /// shots inherit it.
    /// </summary>
    /// <remarks>
    /// The Scattergun, the Shotgun and the Minigun. It is <em>subtracted</em>
    /// from the target's velocity in the solver, because what matters is the
    /// relative motion of the muzzle and the target — a running scout's pellets
    /// carry its momentum, so it has to aim <em>behind</em> where it otherwise
    /// would.
    /// </remarks>
    [Fact]
    public void DriftIsTheShootersOwnSpeedAndOnlyForThreeWeapons()
    {
        Assert.Equal(7, BotAim.Ballistics(Shooter(PlayerClass.Scout, hSpeed: 7)).Drift);
        Assert.Equal(7, BotAim.Ballistics(Shooter(PlayerClass.Engineer, hSpeed: 7)).Drift);
        Assert.Equal(7, BotAim.Ballistics(Shooter(PlayerClass.Heavy, hSpeed: 7)).Drift);

        // A rocket does not inherit the shooter's momentum.
        Assert.Equal(0, BotAim.Ballistics(Shooter(PlayerClass.Soldier, hSpeed: 7)).Drift);
        Assert.Equal(0, BotAim.Ballistics(Shooter(PlayerClass.Demoman, hSpeed: 7)).Drift);
        Assert.Equal(0, BotAim.Ballistics(Shooter(PlayerClass.Pyro, hSpeed: 7)).Drift);
    }

    /// <summary>
    /// A hitscan weapon points straight at the target and leads nothing.
    /// </summary>
    /// <remarks>
    /// Speed zero short-circuits the whole solver, so no lead mode makes any
    /// difference for a sniper — which is right: a Rifle shot arrives on the tick
    /// it is fired.
    /// </remarks>
    [Fact]
    public void AHitscanWeaponPointsStraightAtTheTarget()
    {
        var sniper = Shooter(PlayerClass.Sniper);

        foreach (var mode in Enum.GetValues<BotLeadMode>())
        {
            // A target 100 right and moving fast: still aimed at exactly 0
            // degrees, because there is nothing to lead.
            Assert.Equal(
                0,
                BotAim.Solve(sniper, 100, 0, targetVx: 9, targetVy: 0, mode, BotAim.NoFloor),
                9);
        }
    }

    /// <summary>
    /// Gravity is corrected for even when the target's motion is not.
    /// </summary>
    /// <remarks>
    /// <see cref="BotLeadMode.None"/> still goes through the solver rather
    /// than answering a straight line: a rocket at 400px drops several body
    /// heights, and a bot that pointed straight at a stationary target would shoot
    /// its feet. What "no lead" turns off is the <em>target's</em> velocity, not
    /// the projectile's own drop.
    /// </remarks>
    [Fact]
    public void GravityIsCorrectedEvenWithNoLead()
    {
        // A demoman's mine falls at 0.2. In GM8's convention y grows downward and
        // 90 degrees is up, so aiming above the horizontal is a positive angle.
        var demo = Shooter(PlayerClass.Demoman);

        var aim = BotAim.Solve(demo, 400, 0, 0, 0, BotLeadMode.None, BotAim.NoFloor);

        Assert.True(aim > 0, $"a falling projectile at 400px was aimed at {aim} degrees, not above level");

        // The control: a soldier's rocket does not fall, so the same shot is
        // dead level. Without this the test passes for a solver that always aims
        // high.
        var soldier = Shooter(PlayerClass.Soldier);
        Assert.Equal(0, BotAim.Solve(soldier, 400, 0, 0, 0, BotLeadMode.None, BotAim.NoFloor), 9);
    }

    /// <summary>
    /// Leading a moving target aims ahead of it, and further ahead the faster it
    /// goes.
    /// </summary>
    /// <remarks>
    /// The intercept, at its simplest: a target moving right is shot at
    /// somewhere to its right. Asserted as a <em>monotonic</em> relationship
    /// rather than a number, because the number is the fixed point of an
    /// eight-pass iteration and pinning it would be pinning the arithmetic rather
    /// than the behaviour.
    /// </remarks>
    [Fact]
    public void LeadingAimsAheadOfAMovingTarget()
    {
        var soldier = Shooter(PlayerClass.Soldier);

        // A target level and to the right, standing on a floor: aim is dead
        // level, because there is nowhere ahead to aim that is not on the line.
        //
        // The floor is not decoration here. With BotAim.NoFloor even a
        // STATIONARY target is modelled as falling for ever, so the same shot
        // aims 43 degrees below the horizontal — which is correct, and is why
        // the clamp exists.
        Assert.Equal(0, BotAim.Solve(soldier, 400, 0, 0, 0, BotLeadMode.Full, floorY: 0), 9);

        // The floor has to be well BELOW the target for a rise to be visible
        // at all. Over a thirty-tick flight even a target thrown upward at 4
        // px/tick is back below where it started, so with the floor at its own
        // feet the prediction clamps there and every rise reads as level. That is
        // correct behaviour and it makes the obvious fixture gate nothing.
        //
        // Compared as SIGNED angles, because a shot aimed below the horizontal
        // comes back as 340 rather than -20.
        var stationary = BotAim.Solve(soldier, 400, 0, 0, 0, BotLeadMode.Full, floorY: 400);
        var rising = BotAim.Solve(soldier, 400, 0, 0, -4, BotLeadMode.Full, floorY: 400);
        var faster = BotAim.Solve(soldier, 400, 0, 0, -8, BotLeadMode.Full, floorY: 400);

        Assert.True(
            BotTargeting.AngleDelta(rising, 0) > BotTargeting.AngleDelta(stationary, 0),
            $"a rising target was aimed at {rising} against {stationary} for a still one");

        Assert.True(
            BotTargeting.AngleDelta(faster, 0) > BotTargeting.AngleDelta(rising, 0),
            $"a faster riser was aimed lower: {faster} against {rising}");

        // The control: with lead off, the same target is aimed at dead level.
        Assert.Equal(0, BotAim.Solve(soldier, 400, 0, 0, -8, BotLeadMode.None, floorY: 0), 9);
    }

    /// <summary>
    /// The linear mode is one pass and disagrees with the full solve on a
    /// falling target.
    /// </summary>
    /// <remarks>
    /// The three modes are a <b>difficulty</b> knob, not an optimisation. A bot
    /// that always solves the full intercept never misses a moving target, which
    /// is neither fun nor human — so the difference between them has to be real,
    /// and this is the test that says it is.
    /// </remarks>
    [Fact]
    public void TheThreeLeadModesGiveThreeDifferentAnswers()
    {
        var soldier = Shooter(PlayerClass.Soldier);

        var none = BotAim.Solve(soldier, 400, 0, 6, -6, BotLeadMode.None, BotAim.NoFloor);
        var linear = BotAim.Solve(soldier, 400, 0, 6, -6, BotLeadMode.Linear, BotAim.NoFloor);
        var full = BotAim.Solve(soldier, 400, 0, 6, -6, BotLeadMode.Full, BotAim.NoFloor);

        Assert.NotEqual(none, linear, 6);
        Assert.NotEqual(linear, full, 6);
    }

    /// <summary>
    /// A falling target's prediction has two phases, because GG2 has a terminal
    /// velocity.
    /// </summary>
    /// <remarks>
    /// A single quadratic over-predicts a long fall badly, and long falls are
    /// the aim solver's whole job. A target accelerates at
    /// <c>NAV_JUMP_GRAVITY</c> until it reaches <c>NAV_JUMP_TERM_VY</c> and then
    /// falls at a constant rate.
    /// </remarks>
    [Fact]
    public void AFallHasTwoPhasesBecauseThereIsATerminalVelocity()
    {
        // From rest it takes 10 / 0.6 ticks to reach terminal velocity.
        var clampTicks = NavConstants.JumpTermVy / NavConstants.JumpGravity;

        // Inside the ramp: the quadratic.
        var early = BotAim.FallPredict(0, 0, 5, BotAim.NoFloor);
        Assert.Equal(NavConstants.JumpGravity * 25 / 2, early, 9);

        // Well past it: linear at terminal velocity, so a pure quadratic would be
        // far larger.
        var late = BotAim.FallPredict(0, 0, 100, BotAim.NoFloor);
        var quadratic = NavConstants.JumpGravity * 100 * 100 / 2;

        Assert.True(late < quadratic / 2, $"a 100-tick fall predicted {late}; a quadratic gives {quadratic}");

        // And the join is continuous. The step has to be tiny relative to the
        // tolerance: past the join the target moves at NAV_JUMP_TERM_VY, so a
        // 1e-6 step is a 1e-5 difference and reads as a discontinuity.
        Assert.Equal(
            BotAim.FallPredict(0, 0, clampTicks, BotAim.NoFloor),
            BotAim.FallPredict(0, 0, clampTicks + 1e-9, BotAim.NoFloor),
            6);
    }

    /// <summary>
    /// A target's predicted fall is clamped at the floor under it.
    /// </summary>
    /// <remarks>
    /// <b>Without this a target standing still on the ground is predicted to
    /// keep falling through it, and the bot aims below its feet.</b> The floor is
    /// the nav graph's answer, and <see cref="BotAim.NoFloor"/> — "no map is this
    /// tall" — disables the clamp, which is the right answer over a pit.
    /// </remarks>
    [Fact]
    public void AFallIsClampedAtTheFloor()
    {
        Assert.Equal(50, BotAim.FallPredict(0, 0, 100, floorY: 50), 9);

        // Above the floor it is untouched.
        Assert.Equal(
            BotAim.FallPredict(0, 0, 5, BotAim.NoFloor),
            BotAim.FallPredict(0, 0, 5, floorY: 1000),
            9);

        // And the whole solve changes with it: a stationary target on a floor
        // is aimed at level, and the same target with the clamp switched off is
        // aimed below.
        var soldier = Shooter(PlayerClass.Demoman);

        var onFloor = BotAim.Solve(soldier, 400, 0, 0, 0, BotLeadMode.Full, floorY: 0);
        var falling = BotAim.Solve(soldier, 400, 0, 0, 0, BotLeadMode.Full, BotAim.NoFloor);

        // Compared as SIGNED angles. GM8 reports a direction in [0, 360), so
        // aiming below the horizontal comes back as 340 rather than -20 and a
        // naive `>` says the clamp lowered the aim. Measured: 17.4 degrees on a
        // floor against 340.5 — which is -19.5 — without one.
        Assert.True(
            BotTargeting.AngleDelta(onFloor, 0) > BotTargeting.AngleDelta(falling, 0),
            $"the clamp did not raise the aim: {onFloor} on a floor against {falling} without one");
    }

    /// <summary>
    /// The flight time is clamped to at least one tick.
    /// </summary>
    /// <remarks>
    /// It matters at point blank: a distance of zero gives a time of zero, and
    /// the whole prediction collapses to "aim at your own feet". The clamp is
    /// what makes a bot standing on top of somebody still aim at them.
    /// </remarks>
    [Fact]
    public void TheFlightTimeIsAtLeastOneTick()
    {
        var soldier = Shooter(PlayerClass.Soldier);

        // A target one pixel to the right, moving up fast. With a zero flight
        // time the aim would be dead level; with the one-tick floor it leads.
        var aim = BotAim.Solve(soldier, 1, 0, 0, -9, BotLeadMode.Full, BotAim.NoFloor);

        Assert.True(aim > 45, $"a point-blank riser was aimed at {aim} degrees; the time floor is missing");
    }

    /// <summary>
    /// The projectile's own drop is the triangular number, not the integral.
    /// </summary>
    /// <remarks>
    /// <c>grav × t × (t + 1) / 2</c>, <b>not</b> <c>grav × t² / 2</c>. A
    /// projectile falls in discrete ticks, so after <c>t</c> of them it has
    /// fallen the triangular number — and the difference, <c>grav × t / 2</c>, is
    /// two and a half pixels on a thirty-tick scattergun shot. Small, and exactly
    /// the kind of thing that gets "simplified" into the closed form by somebody
    /// who reads it as calculus.
    /// <para>
    /// Pinned as a number because the shape of the formula is the claim and
    /// there is no behavioural proxy for it: both versions aim slightly high, and
    /// only the arithmetic says which.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheProjectileDropIsTheTriangularNumber()
    {
        // One pass, done by hand: a target 120px right, stationary, on its own
        // floor, and a projectile at 12 px/tick falling at 1 per tick. The
        // iteration converges, so the assertion is on the converged answer.
        var aim = BotAim.Lead(
            0, 0, 120, 0, 0, 0,
            speed: 12, gravity: 1, drift: 0, floorY: 0);

        // The closed form would give a different angle; this is the triangular
        // one. Recomputing it by hand is the point of writing it out.
        var t = Math.Clamp(120.0 / 12, 1, BotConstants.AimMaxTicks);

        for (var i = 0; i < BotConstants.AimIterations; i++)
        {
            var dropY = -(1 * t * (t + 1) / 2);
            t = Math.Clamp(Gml.PointDistance(0, 0, 120, dropY) / 12, 1, BotConstants.AimMaxTicks);
        }

        var expected = Gml.PointDirection(0, 0, 120, -(1 * t * (t + 1) / 2));

        Assert.Equal(expected, aim, 9);

        // And the closed form is a different answer, which is what makes the
        // assertion above a gate rather than a tautology.
        var closedForm = Gml.PointDirection(0, 0, 120, -(1 * t * t / 2));
        Assert.NotEqual(closedForm, aim, 3);
    }

    /// <summary>
    /// A shot from inside the target still leads.
    /// </summary>
    /// <remarks>
    /// The flight time is clamped to at least one tick, and it matters at
    /// point blank: a distance of zero gives a time of zero, and the whole
    /// prediction collapses to "aim at your own feet" — <c>point_direction</c> of
    /// a point to itself is 0, so the bot aims dead right whatever the target is
    /// doing.
    /// </remarks>
    [Fact]
    public void AShotFromInsideTheTargetStillLeads()
    {
        // Source and target on the same spot, target rising fast. One tick of
        // lead puts the aim point straight up.
        var aim = BotAim.Lead(
            0, 0, 0, 0, 0, -9,
            speed: 12, gravity: 0, drift: 0, floorY: BotAim.NoFloor);

        Assert.Equal(90, aim, 9);
    }

    /// <summary>
    /// The floor lookup uses the nav graph, and answers <see cref="BotAim.NoFloor"/>
    /// when nothing is under the point.
    /// </summary>
    /// <remarks>
    /// The sentinel rather than a nullable, because the reference's is a
    /// sentinel and because "no floor" and "the floor is very far down" are the
    /// same answer to the clamp.
    /// </remarks>
    [Fact]
    public void TheFloorLookupFallsBackToTheSentinel()
    {
        ReferenceTree.RequireNavCache();

        var graph = NavCache.Load(ReferenceTree.NavCacheDirectory, "ctf_truefort", 1);

        // A node's own stand position resolves to its own floor.
        var node = 0;
        var x = NavGraph.ColumnWorldX((graph.Nodes[node].X0 + graph.Nodes[node].X1) / 2);

        Assert.Equal(graph.StandY(node), BotAim.FloorUnder(graph, x, graph.StandY(node)), 9);

        // Off the map: nothing under it.
        Assert.Equal(BotAim.NoFloor, BotAim.FloorUnder(graph, -10_000, -10_000));

        // No graph at all is the same answer, which is what lets a scenario with
        // no nav cache still aim.
        Assert.Equal(BotAim.NoFloor, BotAim.FloorUnder(null, x, graph.StandY(node)));
    }
}
