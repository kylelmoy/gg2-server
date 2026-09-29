using GG2.Bots;
using GG2.Maps;
using GG2.Protocol;
using GG2.Simulation;
using Xunit.Abstractions;

namespace GG2.Tests;

/// <summary>
/// <c>botArcClear</c>: whether the shot a bot is lined up on has a path along the
/// <b>parabola</b> it will actually fly.
/// </summary>
/// <remarks>
/// <b>Nothing else in the bot layer checks an arc.</b> Target selection, the
/// keep-target test and the spot search are all a straight chest-to-chest
/// sightline, while the aim solver deliberately aims <em>above</em> the target to
/// compensate for drop — about 178px of it at 500px for a Minegun round. So a
/// demoman with a clear straight line to a target across a room is aiming a whole
/// storey above it, and indoors that is the reported "sometimes shooting at the
/// ceiling".
/// <para>
/// <b>The triangular drop itself is gated by
/// <c>BotAimTests.TheProjectileDropIsTheTriangularNumber</c></b> and is not
/// re-tested here — <see cref="BotArc"/> flies the same law the solver aims with,
/// and testing it twice would gate the copy rather than the rule.
/// </para>
/// </remarks>
public sealed class BotArcTests(ITestOutputHelper output)
{
    private const string Map = "ctf_truefort";

    // A demoman at node 27 and a target 368px away at node 136: the straight line is
    // CLEAR, and the arc leaving at the solved 355° is blocked BY THE SAG — the same
    // aim fired flat gets through. Both are inside the demoman's 500px attention
    // radius and the heavy's.
    //
    // The fixture must be blocked by SAG rather than by the aim simply pointing into
    // geometry: it has to separate `Clear(aim, speed, grav)` from
    // `Clear(aim, speed, 0)`, not merely from the chest-to-chest line, and only a
    // handful of pairs on this map do.
    private const int ShooterNode = 27;
    private const int SagTarget = 136;

    // The same shooter and a target 271px away whose arc IS clear — so "blocked" is
    // not this file's only answer.
    private const int OpenNode = 2;

    // A vertical shot where the parabola's OWN endpoint (4290, 576) lands past the
    // target at (4290, 547) and inside the floor under it. Ending the walk there
    // instead of on the aim point refuses this shot, which is the commonest shot in
    // the game — a grounded enemy.
    private const int EndpointShooter = 52;
    private const int EndpointTarget = 280;

    /// <summary>
    /// A pair where a rocket's LEAD alone decides it — see the soldier test.
    /// </summary>
    /// <remarks>
    /// Swept for across every node pair on the map: the chest-to-chest line is
    /// clear, the still-aim arc is clear, and the led arc is blocked. Twelve pairs
    /// qualify and this is the one with the widest angular separation, so the
    /// fixture is not a knife edge.
    /// </remarks>
    private const int LeadShooter = 0;

    private const int LeadTarget = 214;

    /// <summary>A target running left at a scout's sort of speed.</summary>
    private const double LeadSpeed = -6;

    private static NavGraph Graph()
    {
        ReferenceTree.RequireNavCache();
        return NavCache.Load(ReferenceTree.NavCacheDirectory, Map, 1);
    }

    private static CollisionWorld World() =>
        new(LevelMap.FromPng(Map, ReferenceTree.ReadMap(Map)));

    private static CharacterState At(NavGraph graph, PlayerClass playerClass, int node)
    {
        var self = CharacterState.Fresh(playerClass, Team.Red);
        (self.X, self.Y) = BotSuppress.Centre(graph, node);
        return self;
    }

    /// <summary>The class's own solved aim at a point, which is what it would fire.</summary>
    /// <remarks>
    /// <b>WITH the floor under the target, never <c>BotAim.NoFloor</c>.</b> The
    /// solver applies gravity whatever the target's vertical speed is, so with no
    /// floor a target standing perfectly still is predicted to keep falling and the
    /// aim comes back pointing BELOW its feet, into the ground — which blocks every
    /// arc solved that way and reads as this whole feature refusing everything.
    /// docs/TRAPS-BOTS.md, "Phase 7 item 4d".
    /// </remarks>
    private static double Aim(NavGraph graph, in CharacterState self, double x, double y) =>
        BotAim.Solve(in self, x, y, 0, 0, BotLeadMode.Full, BotAim.FloorUnder(graph, x, y));

    // ------------------------------------------------------------------
    // The arc
    // ------------------------------------------------------------------

    /// <summary>
    /// A clear straight line is not a clear arc.
    /// </summary>
    /// <remarks>
    /// <b>The whole reason this exists.</b> The two are different questions and the
    /// bot layer had only ever asked the first one.
    /// </remarks>
    [Fact]
    public void AClearStraightLineIsNotAClearArc()
    {
        var graph = Graph();
        var world = World();
        var demo = At(graph, PlayerClass.Demoman, ShooterNode);
        var (tx, ty) = BotSuppress.Centre(graph, SagTarget);

        // The premise: the straight line passes, so every other sight test in the
        // bot layer says this shot is fine.
        Assert.False(
            world.CollisionLineBlocksBullets(demo.X, demo.Y, tx, ty, CollisionFilter.ForBullet()),
            "the fixture's straight line is blocked, so it cannot show the difference");

        var (speed, gravity, _) = BotAim.Ballistics(in demo);
        var aim = Aim(graph, in demo, tx, ty);

        output.WriteLine($"demo ({demo.X:0},{demo.Y:0}) -> ({tx:0},{ty:0}), aim {aim:0.#}, grav {gravity}");

        Assert.False(
            BotArc.Clear(world, in demo, aim, speed, gravity, tx, ty),
            "the arc was called clear where the sag clips the geometry");

        // The control that makes it the SAG and not the aim direction: the same
        // shooter, the same target, THE SAME AIM, fired with no gravity. A rocket
        // flies that path, and it gets through.
        //
        // Without this the fixture only says "the aimed ray is blocked", which a
        // straight-line sampler answers identically — three mutations survived
        // against a fixture that said only that.
        Assert.True(
            BotArc.Clear(world, in demo, aim, speed, gravity: 0, tx, ty),
            "the same aim fired flat was also refused, so this is not about gravity at all");
    }

    /// <summary>
    /// A shot with a clear arc is allowed.
    /// </summary>
    /// <remarks>
    /// The control the whole file rests on: without it every assertion here is
    /// satisfied by a method that returns false.
    /// </remarks>
    [Fact]
    public void AShotWithAClearArcIsAllowed()
    {
        var graph = Graph();
        var world = World();
        var demo = At(graph, PlayerClass.Demoman, ShooterNode);
        var (tx, ty) = BotSuppress.Centre(graph, OpenNode);

        var (speed, gravity, _) = BotAim.Ballistics(in demo);

        Assert.True(
            BotArc.Clear(world, in demo, Aim(graph, in demo, tx, ty), speed, gravity, tx, ty),
            "a clear arc was refused");
    }

    /// <summary>
    /// A hitscan weapon has no arc, and a zero speed says so rather than dividing.
    /// </summary>
    /// <remarks>
    /// A speed of zero is how the hitscan and melee weapons — the Rifle, the
    /// knife, the Medigun — fall out of the ballistics model without a special case.
    /// Their caller should not be here, and if it is, the answer is "no arc to
    /// block" rather than a division by zero.
    /// </remarks>
    [Fact]
    public void AHitscanWeaponHasNoArcToBlock()
    {
        var graph = Graph();
        var world = World();

        // A pair whose STRAIGHT LINE is blocked, which is what makes the guard
        // observable at all. With a clear line the method answers true either way —
        // a zero speed makes every sample the shooter's own position and the last one
        // the target, which is exactly the straight-line test.
        var sniper = At(graph, PlayerClass.Sniper, 0);
        var (tx, ty) = BotSuppress.Centre(graph, 10);

        Assert.True(
            world.CollisionLineBlocksBullets(sniper.X, sniper.Y, tx, ty, CollisionFilter.ForBullet()),
            "the fixture's line is clear, so the guard is unobservable");

        Assert.Equal(0, BotAim.Ballistics(in sniper).Speed);
        Assert.True(BotArc.Clear(world, in sniper, 0, 0, 0, tx, ty));
    }

    /// <summary>
    /// The last sample is the target, not the parabola's own endpoint.
    /// </summary>
    /// <remarks>
    /// <b>The flight time is recovered from the horizontal offset and carries a
    /// tick or so of error.</b> Letting that error put the final sample a few pixels
    /// INTO the floor the target is standing on would refuse every shot at a grounded
    /// enemy — so the last sample is the aim point itself, which is also exactly what
    /// the straight sight test does, and a flat weapon therefore gets the answer it
    /// always did.
    /// <para>
    /// The fixture is a vertical shot at a target 198px below, where the parabola's
    /// own endpoint lands 29px PAST it and inside the floor. Asserted as a premise
    /// below, because a pair where the two coincide gates nothing.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheLastSampleIsTheTargetAndNotTheParabolasEnd()
    {
        var graph = Graph();
        var world = World();
        var demo = At(graph, PlayerClass.Demoman, EndpointShooter);
        var (tx, ty) = BotSuppress.Centre(graph, EndpointTarget);

        var (speed, gravity, _) = BotAim.Ballistics(in demo);
        var aim = Aim(graph, in demo, tx, ty);

        // The premise: where the parabola actually ends is behind geometry.
        var vx = Gml.LengthDirX(speed, aim);
        var vy = Gml.LengthDirY(speed, aim);

        var flight = Math.Clamp(
            Math.Abs(vx) > 0.01 ? (tx - demo.X) / vx : Gml.PointDistance(demo.X, demo.Y, tx, ty) / speed,
            1,
            BotConstants.AimMaxTicks);

        var endX = demo.X + (vx * flight);
        var endY = demo.Y + (vy * flight) + (gravity * flight * (flight + 1) / 2);

        output.WriteLine($"target ({tx:0},{ty:0}), parabola ends ({endX:0},{endY:0})");

        Assert.True(
            world.CollisionLineBlocksBullets(tx, ty, endX, endY, CollisionFilter.ForBullet()),
            "the parabola's endpoint is not behind anything, so this gates nothing");

        Assert.True(
            BotArc.Clear(world, in demo, aim, speed, gravity, tx, ty),
            "a shot at a grounded enemy was refused by the flight time's own error");
    }

    /// <summary>
    /// A near-vertical shot falls back to the straight-line flight time.
    /// </summary>
    /// <remarks>
    /// The horizontal offset says nothing about the flight time when the shot is
    /// nearly straight up or down — dividing by it gives an enormous or infinite t,
    /// and the samples then walk a parabola a thousand ticks long. The fallback is
    /// distance over speed, clamped the same way the lead solver clamps its own.
    /// </remarks>
    [Fact]
    public void ANearVerticalShotDoesNotDivideByItsOwnHorizontalOffset()
    {
        var graph = Graph();
        var world = World();
        var demo = At(graph, PlayerClass.Demoman, ShooterNode);

        var (speed, gravity, _) = BotAim.Ballistics(in demo);

        // Straight up, at a target directly overhead. No exception, and a definite
        // answer either way.
        var up = BotArc.Clear(world, in demo, 90, speed, gravity, demo.X, demo.Y - 200);
        var down = BotArc.Clear(world, in demo, 270, speed, gravity, demo.X, demo.Y + 200);

        output.WriteLine($"straight up {up}, straight down {down}");

        // Straight down from a node's stand position is into the floor the bot is
        // standing on, which is the one answer that is certain here.
        Assert.False(down);
    }

    // ------------------------------------------------------------------
    // The caller
    // ------------------------------------------------------------------

    private static ScenarioWorld Scenario() =>
        ScenarioWorld.Load(Map, 1, ReferenceTree.MapsDirectory, ReferenceTree.NavCacheDirectory);

    /// <summary>
    /// What a class presses with a target at <paramref name="targetNode"/>, aimed
    /// the way its own solver would aim.
    /// </summary>
    /// <remarks>
    /// <c>BotClassKeys.For</c> directly rather than the whole combat step,
    /// and that is not a shortcut. The trigger this file is about sits behind three
    /// other gates in <c>BotCombat</c> — the fire delay, the aim settle and the
    /// hold-fire roll — so a test that drove the pipeline would be measuring those
    /// as well, and the hold-fire roll in particular makes it intermittent. The arc
    /// gate lives in the class policy, and this asks the class policy.
    /// </remarks>
    private static PlayerKeys Keys(PlayerClass playerClass, int targetNode) =>
        Keys(playerClass, ShooterNode, targetNode, aimDegrees: null);

    private static PlayerKeys Keys(
        PlayerClass playerClass, int shooterNode, int targetNode, double? aimDegrees)
    {
        var graph = Graph();
        var world = Scenario();

        var (sx, sy) = BotSuppress.Centre(graph, shooterNode);
        var (tx, ty) = BotSuppress.Centre(graph, targetNode);

        var slot = world.Place(playerClass, Team.Red, sx, sy);
        var enemy = world.Place(PlayerClass.Scout, Team.Blue, tx, ty);

        // The aim has to be SET, because the arc is sampled from the
        // character's live aimDirection rather than from the target. A defaulted aim
        // points right and the arc then has nothing to do with the target at all.
        // It is stored as the wire's ushort; the degrees property is derived.
        var solved = aimDegrees ?? Aim(graph, in world.State.Characters[slot], tx, ty);
        world.State.Characters[slot].NetAimDirection = (ushort)(solved * 65536.0 / 360.0);

        var state = new BotCombatState
        {
            Target = new BotTarget(BotTargetKind.Character, enemy, tx, ty, Visible: true),
        };

        var distance = Gml.PointDistance(sx, sy, tx, ty);

        return BotClassKeys.For(state, new WorldView(in world.State, world.Collision), slot, distance, 0);
    }

    /// <summary>
    /// A soldier withholds a rocket whose LEAD takes it into geometry.
    /// </summary>
    /// <remarks>
    /// A rocket flies flat, so a soldier's arc is very nearly the straight line the
    /// target search already cleared, and separating the two needs a <b>moving</b>
    /// target, so the aim solver leads and the shot no longer goes down the line the
    /// target was cleared on.
    /// <para>
    /// <b>Swept for.</b> The pair has to satisfy three things at once: the
    /// chest-to-chest line clear (or the target search would never offer it), the
    /// still-aim arc clear (or it is the demoman's case and gravity is doing the
    /// work), and the led arc blocked. Node 0 → 214 on <c>ctf_truefort</c> at
    /// 417px: the still aim is 319.7° and a target running at 6px/tick pulls it to
    /// 300.8°, nineteen degrees into a wall.
    /// </para>
    /// <para>
    /// The class policy reads the character's live <c>aimDirection</c> and does
    /// not solve, so the solve happens here and the angle is what is handed over —
    /// which is also what makes the control exact: the <em>same</em> soldier at
    /// the <em>same</em> pair, aimed the way it would aim at a stationary target,
    /// fires.
    /// </para>
    /// </remarks>
    [Fact]
    public void ASoldierWithholdsARocketWhoseLeadTakesItIntoGeometry()
    {
        var graph = Graph();
        var world = World();
        var soldier = At(graph, PlayerClass.Soldier, LeadShooter);
        var (tx, ty) = BotSuppress.Centre(graph, LeadTarget);

        var (speed, gravity, _) = BotAim.Ballistics(in soldier);
        Assert.Equal(0, gravity);

        var floor = BotAim.FloorUnder(graph, tx, ty);
        var still = BotAim.Solve(in soldier, tx, ty, 0, 0, BotLeadMode.Full, floor);
        var led = BotAim.Solve(in soldier, tx, ty, LeadSpeed, 0, BotLeadMode.Full, floor);

        output.WriteLine($"still {still:0.0}°, led {led:0.0}°, {BotTargeting.AngleDelta(led, still):0.0}° apart");

        // The three premises, in the order the sweep applied them.
        Assert.False(
            world.CollisionLineBlocksBullets(soldier.X, soldier.Y, tx, ty, CollisionFilter.ForBullet()),
            "the straight line is blocked, so the target search would never offer this pair");

        Assert.True(
            BotArc.Clear(world, in soldier, still, speed, gravity, tx, ty),
            "the still shot is blocked too, so gravity or the geometry is doing the work");

        Assert.False(
            BotArc.Clear(world, in soldier, led, speed, gravity, tx, ty),
            "the led shot is clear, so there is nothing here to withhold");

        // And the class policy acts on it.
        Assert.Equal(
            PlayerKeys.None,
            Keys(PlayerClass.Soldier, LeadShooter, LeadTarget, led) & PlayerKeys.Attack);

        Assert.True(
            (Keys(PlayerClass.Soldier, LeadShooter, LeadTarget, still) & PlayerKeys.Attack) != 0,
            "the soldier withheld a shot it had a clear path for");
    }

    /// <summary>
    /// A demoman holds fire when its arc is blocked; a soldier's flat rocket does not.
    /// </summary>
    /// <remarks>
    /// <b>Only the two classes whose shot kills them if it goes off next to
    /// them</b> pay for this test at all — but the two answer differently on the same
    /// geometry, because a rocket has no gravity. Its arc IS the straight line the
    /// target search already cleared, so the gate is very nearly free for a soldier
    /// and decisive for a demoman.
    /// </remarks>
    [Fact]
    public void ADemomanHoldsFireOnABlockedArcAndASoldiersFlatRocketDoesNot()
    {
        var graph = Graph();
        var world = World();
        var demo = At(graph, PlayerClass.Demoman, ShooterNode);
        var (tx, ty) = BotSuppress.Centre(graph, SagTarget);
        var (speed, gravity, _) = BotAim.Ballistics(in demo);

        // The premise, from the unit level: this arc really is blocked and the
        // straight line really is not.
        Assert.False(BotArc.Clear(world, in demo, Aim(graph, in demo, tx, ty), speed, gravity, tx, ty));
        Assert.False(
            world.CollisionLineBlocksBullets(demo.X, demo.Y, tx, ty, CollisionFilter.ForBullet()));

        Assert.Equal(PlayerKeys.None, Keys(PlayerClass.Demoman, SagTarget) & PlayerKeys.Attack);

        // The control — a SOLDIER at the same place, whose rocket has no
        // gravity. Without it this passes on a gate that refuses everything.
        var soldier = At(graph, PlayerClass.Soldier, ShooterNode);
        Assert.Equal(0, BotAim.Ballistics(in soldier).Gravity);

        Assert.True((Keys(PlayerClass.Soldier, SagTarget) & PlayerKeys.Attack) != 0);
    }

    /// <summary>
    /// A demoman with a clear arc fires.
    /// </summary>
    /// <remarks>
    /// The other control, and the one that stops the gate becoming "a
    /// demoman never shoots". Only 8 of the 58 sampled pairs on this map are blocked,
    /// so the common case has to keep working.
    /// </remarks>
    [Fact]
    public void ADemomanWithAClearArcFires()
    {
        var graph = Graph();
        var world = World();
        var demo = At(graph, PlayerClass.Demoman, ShooterNode);
        var (tx, ty) = BotSuppress.Centre(graph, OpenNode);
        var (speed, gravity, _) = BotAim.Ballistics(in demo);

        Assert.True(BotArc.Clear(world, in demo, Aim(graph, in demo, tx, ty), speed, gravity, tx, ty));
        Assert.True((Keys(PlayerClass.Demoman, OpenNode) & PlayerKeys.Attack) != 0);
    }

    /// <summary>
    /// A class with no arc gate fires regardless.
    /// </summary>
    /// <remarks>
    /// The gate is deliberately NOT universal. A heavy's Minigun arcs as
    /// much as a scattergun and it still fires: the cost of a wasted round is ammo it
    /// has plenty of, where a demoman's is a mine on its own side of a wall — and
    /// withholding would make a scout stop firing in exactly the corridors it wants
    /// to be firing in.
    /// </remarks>
    [Fact]
    public void AClassWithNoArcGateFiresRegardless()
    {
        var graph = Graph();
        var world = World();
        var heavy = At(graph, PlayerClass.Heavy, ShooterNode);
        var (tx, ty) = BotSuppress.Centre(graph, SagTarget);
        var (speed, gravity, _) = BotAim.Ballistics(in heavy);

        // The heavy's own arc to the same place is blocked...
        Assert.False(BotArc.Clear(world, in heavy, Aim(graph, in heavy, tx, ty), speed, gravity, tx, ty));

        // ...and it shoots anyway.
        Assert.True((Keys(PlayerClass.Heavy, SagTarget) & PlayerKeys.Attack) != 0);
    }
}
