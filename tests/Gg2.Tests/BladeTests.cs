using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// Quote's Blade: a stream of free bubbles on the left click, and one thrown
/// blade on the right that bills the magazine while it flies.
/// </summary>
/// <remarks>
/// <c>Objects/Weapons/Blade.events/*</c>,
/// <c>Objects/Projectiles/{BladeB, Bubble}.events/*</c>, and the
/// <c>with(BladeB)</c> block of
/// <c>Scripts/Physics/System/move_all_bullets.gml</c>.
/// <para>
/// <b>No recording can gate any of it.</b> Both clicks call <c>randomize()</c>
/// and broadcast no seed, so the Blade joins the Minigun and the Flamethrower on
/// docs/PARITY.md's unseeded list — and a bubble is worse than that: it is never
/// serialised in any tier, so even its existence is invisible to a state-update
/// comparison. These tests are the whole gate, and each was checked by breaking
/// the line it is about.
/// </para>
/// </remarks>
public sealed class BladeTests
{
    private const string Map = "ctf_truefort";

    /// <summary>A standing position with the floor directly under it.</summary>
    private const double OpenX = 2600;

    private const double OpenY = 708;

    /// <summary>Open air with three hundred clear pixels to the right of it.</summary>
    private const double ClearX = 2200;

    private const double ClearY = 300;

    private static CollisionWorld? _sharedWorld;

    private static CollisionWorld World => _sharedWorld ??=
        new CollisionWorld(LevelMap.FromPng(Map, ReferenceTree.ReadMap(Map)));

    private static WeaponStats Stats => WeaponStats.For(WeaponKind.Blade);

    /// <summary>A Quote, ready to shoot, aiming as told.</summary>
    private static void Put(
        ref WorldState state,
        int slot,
        double x = ClearX,
        double y = ClearY,
        PlayerKeys keys = PlayerKeys.None,
        double aimDegrees = 0,
        Team team = Team.Red,
        PlayerClass playerClass = PlayerClass.Quote)
    {
        var character = CharacterState.Fresh(playerClass, team);
        character.X = x;
        character.Y = y;
        character.NetAimDirection = (ushort)Math.Round(aimDegrees * 65536.0 / 360.0);
        character.KeyState = keys;

        state.Characters[slot] = character;
        state.Weapons[slot] = WeaponState.Spawn(playerClass, x, y);
        state.Weapons[slot].X = x;
        state.Weapons[slot].Y = y;
        state.Weapons[slot].ReadyToShoot = true;
        state.Weapons[slot].RefireTimer = WeaponState.Inactive;
        state.Alive[slot] = true;
        state.AddPlayer(slot);
        state.Players[slot].Team = team;
        state.Players[slot].Class = playerClass;
    }

    private static ProjectileState Live(in WorldState state, int index) =>
        state.Projectiles[state.LiveProjectileSlot(index)];

    private static int CountOf(in WorldState state, ProjectileKind kind)
    {
        var n = 0;
        for (var i = 0; i < state.LiveProjectileCount; i++)
            if (Live(state, i).Kind == kind)
                n++;

        return n;
    }

    private static void Attack(ref WorldState state, int slot = 1)
    {
        var events = default(TickEvents);
        Weapons.FireStep(World, ref state, slot, ref events);
    }

    private static void Special(ref WorldState state, int slot = 1)
    {
        var events = default(TickEvents);
        Weapons.SpecialStep(World, ref state, slot, ref events);
    }

    // --- the weapon -----------------------------------------------------------

    /// <summary>A Quote starts with a full 90, replenishing, and no blade out.</summary>
    [Fact]
    public void AFreshBladeIsFullAndReplenishing()
    {
        var weapon = WeaponState.Spawn(PlayerClass.Quote, 0, 0);

        Assert.Equal(90, weapon.AmmoCount);
        Assert.True(weapon.Replenishing);
        Assert.Equal(0, weapon.BubbleCount);
        Assert.Equal(0, weapon.BladesOut);
        Assert.Equal(Stats.RefireTime, weapon.RefireTimer);
        Assert.False(weapon.ReadyToShoot);
    }

    /// <summary>
    /// ⚠️ <b>The magazine is clamped at both ends every tick it replenishes</b>,
    /// so a Blade's tank rests at exactly 90 — where a pyro's rests at 201.8,
    /// because that guard is on the level and this one is on a flag.
    /// </summary>
    [Fact]
    public void AFullTankDoesNotOvershoot()
    {
        var weapon = WeaponState.Spawn(PlayerClass.Quote, 0, 0);

        for (var i = 0; i < 10; i++)
            Weapons.WeaponBeginStep(ref weapon, PlayerClass.Quote);

        Assert.Equal(90, weapon.AmmoCount);
    }

    /// <summary>And it climbs two a tick from below.</summary>
    [Fact]
    public void AnEmptyTankClimbsTwoATick()
    {
        var weapon = WeaponState.Spawn(PlayerClass.Quote, 0, 0);
        weapon.AmmoCount = 40;

        Weapons.WeaponBeginStep(ref weapon, PlayerClass.Quote);
        Assert.Equal(42, weapon.AmmoCount);

        Weapons.WeaponBeginStep(ref weapon, PlayerClass.Quote);
        Assert.Equal(44, weapon.AmmoCount);
    }

    /// <summary>
    /// ⚠️ <b>A Blade that is not replenishing does not climb at all</b> — the
    /// whole Begin Step is inside <c>if (replenishing)</c>.
    /// </summary>
    [Fact]
    public void ABladeThatIsNotReplenishingHoldsItsTank()
    {
        var weapon = WeaponState.Spawn(PlayerClass.Quote, 0, 0);
        weapon.AmmoCount = 40;
        weapon.Replenishing = false;

        Weapons.WeaponBeginStep(ref weapon, PlayerClass.Quote);

        Assert.Equal(40, weapon.AmmoCount);
    }

    // --- the bubble, on the left click ---------------------------------------

    /// <summary>
    /// ⚠️ <b>A bubble costs nothing.</b> No ammo clause and no ammo spend — the
    /// magazine is entirely the right click's.
    /// </summary>
    [Fact]
    public void ABubbleIsFree()
    {
        var state = default(WorldState);
        Put(ref state, 1, keys: PlayerKeys.Attack);

        Attack(ref state);

        Assert.Equal(1, CountOf(state, ProjectileKind.Bubble));
        Assert.Equal(90, state.Weapons[1].AmmoCount);
        Assert.Equal(1, state.Weapons[1].BubbleCount);
        Assert.False(state.Weapons[1].ReadyToShoot);
    }

    /// <summary>
    /// ⚠️ <b>Its refire is four ticks, not the weapon's five</b> — the left
    /// click writes <c>bubbleRefireTime</c> and the right click writes
    /// <c>refireTime</c>.
    /// </summary>
    [Fact]
    public void ABubbleUsesItsOwnShorterRefire()
    {
        var state = default(WorldState);
        Put(ref state, 1, keys: PlayerKeys.Attack);

        Attack(ref state);

        Assert.Equal(4, state.Weapons[1].RefireTimer);
        Assert.NotEqual(Stats.RefireTime, state.Weapons[1].RefireTimer);
    }

    /// <summary>
    /// A bubble leaves the weapon eight pixels along the aim, at speed 10, and
    /// picks up its owner's whole velocity on top.
    /// </summary>
    [Fact]
    public void ABubbleInheritsItsOwnersVelocity()
    {
        var still = default(WorldState);
        Put(ref still, 1, keys: PlayerKeys.Attack);
        Attack(ref still);

        var running = default(WorldState);
        Put(ref running, 1, keys: PlayerKeys.Attack);
        running.Characters[1].Motion.SetComponents(4, 0);
        Attack(ref running);

        Assert.Equal(ClearX + 8, Live(still, 0).X);
        Assert.Equal(10, Live(still, 0).Motion.Speed);

        // motion_add(owner.direction, owner.speed) with the owner running right
        // at 4: the whole of it, not a fraction.
        Assert.Equal(14, Live(running, 0).Motion.HSpeed, 9);
    }

    /// <summary>The twenty-sixth bubble is refused.</summary>
    [Fact]
    public void TwentyFiveBubblesIsTheLimit()
    {
        var state = default(WorldState);
        Put(ref state, 1, keys: PlayerKeys.Attack);

        for (var i = 0; i < 30; i++)
        {
            state.Weapons[1].ReadyToShoot = true;
            Attack(ref state);
        }

        Assert.Equal(Weapons.BladeMaxBubbles, CountOf(state, ProjectileKind.Bubble));
        Assert.Equal(Weapons.BladeMaxBubbles, state.Weapons[1].BubbleCount);
    }

    /// <summary>And popping one gives the slot back.</summary>
    [Fact]
    public void APoppedBubbleGivesItsSlotBack()
    {
        var state = default(WorldState);
        Put(ref state, 1, keys: PlayerKeys.Attack);
        Attack(ref state);

        Assert.Equal(1, state.Weapons[1].BubbleCount);

        state.FreeProjectile(state.LiveProjectileHandle(0));

        Assert.Equal(0, state.Weapons[1].BubbleCount);
    }

    /// <summary>
    /// ⚠️ <b>A bubble's box is <em>rotated</em> by the direction it was fired
    /// in</b>, because nothing ever overwrites the <c>image_angle</c>
    /// <c>createShot</c> gave it — a <c>Bubble</c> has no <c>with</c> block in
    /// <c>move_all_bullets</c> at all, and that is where every other projectile's
    /// <c>image_angle = direction</c> lives.
    /// </summary>
    /// <remarks>
    /// A 12x12 square turned 45 degrees reports a box about 17 wide, so this is
    /// not a rounding difference — it decides who a bubble blocks.
    /// </remarks>
    [Fact]
    public void ABubbleFiredDiagonallyHasAWiderBox()
    {
        var flat = default(WorldState);
        Put(ref flat, 1, keys: PlayerKeys.Attack);
        Attack(ref flat);

        var diagonal = default(WorldState);
        Put(ref diagonal, 1, keys: PlayerKeys.Attack, aimDegrees: 45);
        Attack(ref diagonal);

        Assert.Equal(12, Live(flat, 0).Box.Width);
        Assert.True(Live(diagonal, 0).Box.Width > 15,
            $"a diagonal bubble reported {Live(diagonal, 0).Box.Width}");
    }

    // --- the bubble's Step ----------------------------------------------------

    /// <summary>
    /// One Step from a state chosen so that three of the four terms are exactly
    /// zero, which pins the fourth: <c>speed = (speed + 2.2) * 0.6</c>.
    /// </summary>
    /// <remarks>
    /// The owner is stationary (so the 0.2 term is zero) and the bubble sits at
    /// exactly the ring radius (so the arctangent is zero and the spring adds
    /// nothing). What is left is the aim pull and the drag: 0 + 2 = 2, then
    /// (2 + 2.2) × 0.6 = 2.52.
    /// <para>
    /// ⚠️ The reference's own comment says the speed "should stay around 3.3",
    /// which is the fixed point of the drag line <em>alone</em>: s = (s+2.2)×0.6
    /// solves at 3.3. It is not what a bubble actually settles at, because the
    /// steering above the line keeps adding to the speed the line then shrinks.
    /// A test that asserted 3.3 after forty ticks measured 1.75 — do not read
    /// the comment as a prediction.
    /// </para>
    /// </remarks>
    [Fact]
    public void OneBubbleStepIsFourTermsAndADrag()
    {
        var state = default(WorldState);
        Put(ref state, 1, keys: PlayerKeys.Attack);
        Attack(ref state);

        var handle = state.LiveProjectileHandle(0);
        state.Projectiles[handle.Index].X = ClearX + 90;
        state.Projectiles[handle.Index].Y = ClearY;
        state.Projectiles[handle.Index].Motion.Speed = 0;

        Projectiles.BubbleStep(ref state);

        Assert.Equal((0 + 2 + 2.2) * Gml.PointSix, state.Projectiles[handle.Index].Motion.Speed, 12);
        Assert.Equal(0, state.Projectiles[handle.Index].Motion.Direction, 9);
        Assert.Equal(ClearX + 90 + ((0 + 2 + 2.2) * Gml.PointSix),
            state.Projectiles[handle.Index].X, 9);
    }

    /// <summary>
    /// ⚠️ <b>The spring runs both ways.</b> Inside the ring it pushes out and
    /// outside it pulls in — the strength is <em>negated</em> and the direction
    /// is measured owner-to-bubble, which is easy to read backwards.
    /// </summary>
    [Fact]
    public void TheOrbitSpringPushesOutInsideTheRingAndPullsInOutside()
    {
        double Drift(double distance)
        {
            var state = default(WorldState);
            Put(ref state, 1, keys: PlayerKeys.Attack);
            Attack(ref state);

            var handle = state.LiveProjectileHandle(0);

            // Directly *above* the owner, so the horizontal aim pull cannot be
            // mistaken for the spring.
            state.Projectiles[handle.Index].X = ClearX;
            state.Projectiles[handle.Index].Y = ClearY - distance;
            state.Projectiles[handle.Index].Motion.Speed = 0;

            Projectiles.BubbleStep(ref state);

            return state.Projectiles[handle.Index].Motion.VSpeed;
        }

        // Up is negative: inside the ring the bubble is pushed further up.
        Assert.True(Drift(40) < 0, "a bubble inside the ring was not pushed out");
        Assert.True(Drift(140) > 0, "a bubble outside the ring was not pulled in");
    }

    /// <summary>And a fifth of the owner's own velocity, as components.</summary>
    /// <remarks>
    /// ⚠️ <b>The owner's position this reads is <c>X - HSpeed</c>, not
    /// <c>X</c></b> — during the Step stage the reference has every character
    /// back where its own Step left it, before GM8's movement pass. So the
    /// character below is displaced by exactly its velocity, which puts the
    /// point the spring measures from back at the ring centre and leaves this
    /// test measuring one term instead of two.
    /// </remarks>
    [Fact]
    public void ABubblePicksUpAFifthOfItsOwnersVelocityEveryTick()
    {
        var state = default(WorldState);
        Put(ref state, 1, keys: PlayerKeys.Attack);
        Attack(ref state);

        var handle = state.LiveProjectileHandle(0);
        state.Projectiles[handle.Index].X = ClearX + 90;
        state.Projectiles[handle.Index].Y = ClearY;
        state.Projectiles[handle.Index].Motion.Speed = 0;

        // Straight down at 10, so the pick-up cannot be confused with the aim
        // pull — and displaced so the mid-Step position is still (ClearX, ClearY).
        state.Characters[1].Y = ClearY + 10;
        state.Characters[1].Motion.SetComponents(0, 10);

        Projectiles.BubbleStep(ref state);

        ref var bubble = ref state.Projectiles[handle.Index];

        // 10 * 0.2 = 2 down, the aim pull of 2 right, no spring at the ring
        // radius — then the drag on the *speed*, which is the length of the
        // pair rather than either component.
        Assert.Equal((Math.Sqrt(8) + 2.2) * Gml.PointSix, bubble.Motion.Speed, 9);
        Assert.True(bubble.Motion.VSpeed > 0, "the owner's descent was not picked up");
    }

    /// <summary>
    /// ⚠️ <b>The leash is the whole reason a bubble ever leaves early</b>, and it
    /// is 160 pixels of <c>distance_to_object</c> from its owner.
    /// </summary>
    [Fact]
    public void ABubbleTooFarFromItsOwnerPops()
    {
        var state = default(WorldState);
        Put(ref state, 1, keys: PlayerKeys.Attack);
        Attack(ref state);

        var handle = state.LiveProjectileHandle(0);

        state.Projectiles[handle.Index].X = ClearX + 150;
        Projectiles.BubbleBeginStep(ref state);
        Assert.True(state.IsLive(handle), "popped inside the leash");

        state.Projectiles[handle.Index].X = ClearX + 400;
        Projectiles.BubbleBeginStep(ref state);
        Assert.False(state.IsLive(handle), "survived far outside the leash");
    }

    /// <summary>
    /// ⚠️ <b>A bubble is NOT moved by <c>Projectiles.Move</c></b> — it has no
    /// block in <c>move_all_bullets</c>, so the pellet pass must leave it exactly
    /// where it was.
    /// </summary>
    /// <remarks>
    /// The control for the phase split: if the bubble were moved here it would
    /// see its owner's position <em>before</em> the characters' Steps rather than
    /// after, which is a different simulation.
    /// </remarks>
    [Fact]
    public void TheBulletPassDoesNotMoveABubble()
    {
        var state = default(WorldState);
        Put(ref state, 1, keys: PlayerKeys.Attack);
        Attack(ref state);

        var before = Live(state, 0);
        Projectiles.Move(World, ref state, ref DiscardedEvents.Events);
        var after = Live(state, 0);

        Assert.Equal(before.X, after.X);
        Assert.Equal(before.Y, after.Y);
        Assert.Equal(before.Motion.VSpeed, after.Motion.VSpeed);
    }

    /// <summary>Two same-team bubbles push each other apart rather than popping.</summary>
    [Fact]
    public void TwoFriendlyBubblesRepelEachOther()
    {
        var state = default(WorldState);
        Put(ref state, 1, keys: PlayerKeys.Attack);

        Attack(ref state);
        state.Weapons[1].ReadyToShoot = true;
        Attack(ref state);

        // Stacked exactly one pixel apart, well inside each other's box.
        var first = state.LiveProjectileHandle(0);
        var second = state.LiveProjectileHandle(1);
        state.Projectiles[second.Index].X = state.Projectiles[first.Index].X + 1;

        var before = state.Projectiles[first.Index].Motion.HSpeed;

        Projectiles.Collide(World, ref state, ref DiscardedEvents.Events);

        Assert.Equal(2, CountOf(state, ProjectileKind.Bubble));
        Assert.True(state.Projectiles[first.Index].Motion.HSpeed < before,
            "the first bubble was not pushed away from its neighbour");
    }

    /// <summary>An enemy's bubble pops it instead.</summary>
    [Fact]
    public void AnEnemyBubblePopsIt()
    {
        var state = default(WorldState);
        Put(ref state, 1, keys: PlayerKeys.Attack);
        Put(ref state, 2, x: ClearX, y: ClearY, keys: PlayerKeys.Attack, team: Team.Blue);

        Attack(ref state, 1);
        Attack(ref state, 2);

        Assert.Equal(2, CountOf(state, ProjectileKind.Bubble));

        // ⚠️ Stacked well clear of both characters. Left at the muzzle they
        // overlap the *other* Quote and pop on them instead, which made this
        // pass with the bubble-versus-bubble rule deleted.
        for (var i = 0; i < 2; i++)
        {
            var handle = state.LiveProjectileHandle(i);
            state.Projectiles[handle.Index].X = ClearX - 100;
            state.Projectiles[handle.Index].Y = ClearY;
        }

        Projectiles.Collide(World, ref state, ref DiscardedEvents.Events);

        Assert.Equal(0, CountOf(state, ProjectileKind.Bubble));
    }

    // --- the bubble against other projectiles ---------------------------------

    /// <summary>
    /// An enemy pellet and the bubble it meets destroy each other — which is what
    /// a Quote's screen is <em>for</em>.
    /// </summary>
    [Fact]
    public void AnEnemyPelletAndABubbleTradeThemselves()
    {
        var state = default(WorldState);
        Put(ref state, 1, keys: PlayerKeys.Attack);
        Attack(ref state);

        var bubble = Live(state, 0);
        SpawnShot(ref state, bubble.X, bubble.Y, Team.Blue, perseverant: false);

        Projectiles.Collide(World, ref state, ref DiscardedEvents.Events);

        Assert.Equal(0, state.LiveProjectileCount);
    }

    /// <summary>
    /// ⚠️ <b>A perseverant pellet survives</b> — the one-in-three
    /// <c>choose(0,0,1)</c> draw every pellet has made since phase 5 finally
    /// means something — <b>and the bubble still pops.</b>
    /// </summary>
    [Fact]
    public void APerseverantPelletPassesThroughAndStillPopsTheBubble()
    {
        var state = default(WorldState);
        Put(ref state, 1, keys: PlayerKeys.Attack);
        Attack(ref state);

        var bubble = Live(state, 0);
        SpawnShot(ref state, bubble.X, bubble.Y, Team.Blue, perseverant: true);

        Projectiles.Collide(World, ref state, ref DiscardedEvents.Events);

        Assert.Equal(0, CountOf(state, ProjectileKind.Bubble));
        Assert.Equal(1, CountOf(state, ProjectileKind.Shot));
    }

    /// <summary>
    /// ⚠️ <b>And a <em>friendly</em> perseverant pellet pops it too.</b> The
    /// bubble's condition is <c>team != other.team or other.perseverant</c>,
    /// which is not the pellet's own — so a Quote's own team-mate can clear
    /// their screen by luck.
    /// </summary>
    [Fact]
    public void AFriendlyPerseverantPelletPopsTheBubbleAnyway()
    {
        var state = default(WorldState);
        Put(ref state, 1, keys: PlayerKeys.Attack);
        Attack(ref state);

        var bubble = Live(state, 0);
        SpawnShot(ref state, bubble.X, bubble.Y, Team.Red, perseverant: true);

        Projectiles.Collide(World, ref state, ref DiscardedEvents.Events);

        Assert.Equal(0, CountOf(state, ProjectileKind.Bubble));
        Assert.Equal(1, CountOf(state, ProjectileKind.Shot));
    }

    /// <summary>A friendly ordinary pellet flies through and leaves it alone.</summary>
    [Fact]
    public void AFriendlyOrdinaryPelletLeavesTheBubbleAlone()
    {
        var state = default(WorldState);
        Put(ref state, 1, keys: PlayerKeys.Attack);
        Attack(ref state);

        var bubble = Live(state, 0);
        SpawnShot(ref state, bubble.X, bubble.Y, Team.Red, perseverant: false);

        Projectiles.Collide(World, ref state, ref DiscardedEvents.Events);

        Assert.Equal(1, CountOf(state, ProjectileKind.Bubble));
        Assert.Equal(1, CountOf(state, ProjectileKind.Shot));
    }

    /// <summary>Where <see cref="Attack"/> puts a Quote's first bubble.</summary>
    /// <remarks>
    /// Read off a scratch world so a test can put something at that spot
    /// <em>before</em> the bubble exists. A Blade's bubble draws nothing from
    /// the shared RNG stream, so this is deterministic.
    /// </remarks>
    private static (double X, double Y) BubbleSpot()
    {
        var scratch = default(WorldState);
        Put(ref scratch, 1, keys: PlayerKeys.Attack);
        Attack(ref scratch);

        var bubble = Live(scratch, 0);
        return (bubble.X, bubble.Y);
    }

    /// <summary>
    /// The whole pellet-versus-bubble truth table, from both creation orders.
    /// </summary>
    /// <remarks>
    /// ✅ <b>Measured against the running game on 2026-08-27</b>, by putting a
    /// real <c>Bubble</c> and a real <c>Shot</c> on the same point in open space
    /// and reading back which survived one frame later
    /// (<c>tools/probes/probe-bubble-pair.js</c>). All four rows agreed with
    /// the port. That settles the question docs/SIMULATION.md had open: <b>GM8
    /// runs both instances' collision events</b>, and a projectile that
    /// destroyed itself in its own event is still seen by the other side's, so
    /// an enemy ordinary pellet and a bubble really do annihilate each other.
    /// The alternative — the instance the stage reaches first wins and the other
    /// survives — is refuted by the enemy/ordinary row, where <em>both</em> die.
    /// <para>
    /// ⚠️ <b>The two orders take different entry points.</b>
    /// <c>Projectiles.Collide</c> walks in creation order, so a bubble fired
    /// first is reached through <c>BubbleHitProjectiles</c> and a pellet fired
    /// first through <c>HitBubble</c>. Every other test in this file creates the
    /// bubble first, so the pellet-side entry had no coverage of this table at
    /// all — and a Quote throwing a screen up in front of pellets already in the
    /// air is the ordinary case, not the exotic one.
    /// </para>
    /// </remarks>
    [Theory]
    // enemy + ordinary: the pair annihilates. This is the row that proves both
    // events run — one side alone cannot destroy both instances.
    [InlineData(Team.Blue, false, false, 0, 0)]
    [InlineData(Team.Blue, false, true, 0, 0)]
    // enemy + perseverant: the pellet flies on and the bubble pops anyway.
    [InlineData(Team.Blue, true, false, 0, 1)]
    [InlineData(Team.Blue, true, true, 0, 1)]
    // friendly + perseverant: the bubble's condition is an `or`, so a team-mate
    // clears the screen by luck.
    [InlineData(Team.Red, true, false, 0, 1)]
    [InlineData(Team.Red, true, true, 0, 1)]
    // friendly + ordinary: neither side's condition is true and nothing happens.
    [InlineData(Team.Red, false, false, 1, 1)]
    [InlineData(Team.Red, false, true, 1, 1)]
    public void ThePelletAndBubbleTableHoldsWhicheverWasFiredFirst(
        Team shotTeam, bool perseverant, bool pelletFirst, int bubbles, int shots)
    {
        var state = default(WorldState);

        if (pelletFirst)
        {
            var spot = BubbleSpot();
            SpawnShot(ref state, spot.X, spot.Y, shotTeam, perseverant);
            Put(ref state, 1, keys: PlayerKeys.Attack);
            Attack(ref state);
        }
        else
        {
            Put(ref state, 1, keys: PlayerKeys.Attack);
            Attack(ref state);

            var bubble = Live(state, 0);
            SpawnShot(ref state, bubble.X, bubble.Y, shotTeam, perseverant);
        }

        // The pair really is overlapping, whichever way round it was built —
        // otherwise a row asserting "both survive" would pass for the wrong
        // reason.
        Assert.Equal(1, CountOf(state, ProjectileKind.Bubble));
        Assert.Equal(1, CountOf(state, ProjectileKind.Shot));

        Projectiles.Collide(World, ref state, ref DiscardedEvents.Events);

        Assert.Equal(bubbles, CountOf(state, ProjectileKind.Bubble));
        Assert.Equal(shots, CountOf(state, ProjectileKind.Shot));
    }

    /// <summary>
    /// ⚠️ <b>A mine is turned around rather than popped</b>, and the bubble
    /// spends half its life doing it.
    /// </summary>
    [Fact]
    public void ABubbleReflectsAMineOnceAndHalvesItsOwnLife()
    {
        var state = default(WorldState);
        Put(ref state, 1, keys: PlayerKeys.Attack);
        Attack(ref state);

        var bubbleHandle = state.LiveProjectileHandle(0);
        var bubble = state.Projectiles[bubbleHandle.Index];

        var mine = new ProjectileState
        {
            Kind = ProjectileKind.Mine,
            X = bubble.X,
            Y = bubble.Y,
            Team = Team.Blue,
            OwnerSlot = 2,
            Reflector = ProjectileState.NoOwner,
            Source = DamageSource.Minegun,
        };
        mine.Motion.SetComponents(-6, 0);
        var mineHandle = state.SpawnProjectile(mine);

        var life = state.Projectiles[bubbleHandle.Index].Lifetime;

        Projectiles.Collide(World, ref state, ref DiscardedEvents.Events);

        Assert.True(state.IsLive(mineHandle), "the mine was destroyed rather than reflected");
        Assert.Equal(6, state.Projectiles[mineHandle.Index].Motion.HSpeed, 9);
        Assert.True(state.Projectiles[mineHandle.Index].Bubbled);
        Assert.Equal(1, state.Projectiles[mineHandle.Index].Reflector);
        Assert.Equal(life / 2, state.Projectiles[bubbleHandle.Index].Lifetime);
    }

    /// <summary>
    /// ⚠️ <b>Only once.</b> The second bubble sees <c>bubbled</c> and lets the
    /// mine through, so a screen cannot pin one in mid-air.
    /// </summary>
    [Fact]
    public void ABubbledMineIsNotReflectedTwice()
    {
        var state = default(WorldState);
        Put(ref state, 1, keys: PlayerKeys.Attack);
        Attack(ref state);

        var bubble = Live(state, 0);

        var mine = new ProjectileState
        {
            Kind = ProjectileKind.Mine,
            X = bubble.X,
            Y = bubble.Y,
            Team = Team.Blue,
            OwnerSlot = 2,
            Reflector = ProjectileState.NoOwner,
            Source = DamageSource.Minegun,
            Bubbled = true,
        };
        mine.Motion.SetComponents(-6, 0);
        var mineHandle = state.SpawnProjectile(mine);

        Projectiles.Collide(World, ref state, ref DiscardedEvents.Events);

        Assert.Equal(-6, state.Projectiles[mineHandle.Index].Motion.HSpeed, 9);
    }

    // --- the bubble against people --------------------------------------------

    /// <summary>A bubble deals three quarters of a hit point and pops.</summary>
    [Fact]
    public void ABubbleDealsThreeQuartersAndPops()
    {
        var state = default(WorldState);
        Put(ref state, 1, keys: PlayerKeys.Attack);
        Put(ref state, 2, x: ClearX + 8, y: ClearY, team: Team.Blue, playerClass: PlayerClass.Soldier);

        Attack(ref state, 1);
        var before = state.Characters[2].Hp;

        Projectiles.Collide(World, ref state, ref DiscardedEvents.Events);

        Assert.Equal(before - 0.75, state.Characters[2].Hp, 9);
        Assert.Equal(0, CountOf(state, ProjectileKind.Bubble));
    }

    /// <summary>
    /// ⚠️ <b>And it does not reset <c>timeUnscathed</c></b>, which every other
    /// damaging collision event in the game does — so bubbles cannot hold a
    /// medic's healing ramp down.
    /// </summary>
    [Fact]
    public void ABubbleDoesNotResetTimeUnscathed()
    {
        var state = default(WorldState);
        Put(ref state, 1, keys: PlayerKeys.Attack);
        Put(ref state, 2, x: ClearX + 8, y: ClearY, team: Team.Blue, playerClass: PlayerClass.Soldier);
        state.Characters[2].TimeUnscathed = 200;

        Attack(ref state, 1);
        Projectiles.Collide(World, ref state, ref DiscardedEvents.Events);

        Assert.Equal(200, state.Characters[2].TimeUnscathed);
    }

    /// <summary>
    /// ⚠️ <b>A cloaked spy who could stab is invisible to bubbles</b> — the
    /// event exits before the team test, so the bubble neither damages them nor
    /// pops on them.
    /// </summary>
    [Fact]
    public void ACloakedSpyIsInvisibleToBubbles()
    {
        var state = default(WorldState);
        Put(ref state, 1, keys: PlayerKeys.Attack);
        Put(ref state, 2, x: ClearX + 8, y: ClearY, team: Team.Blue, playerClass: PlayerClass.Spy);
        state.Characters[2].Cloak = true;

        Attack(ref state, 1);
        var before = state.Characters[2].Hp;

        Projectiles.Collide(World, ref state, ref DiscardedEvents.Events);

        Assert.Equal(before, state.Characters[2].Hp);
        Assert.Equal(1, CountOf(state, ProjectileKind.Bubble));
    }

    /// <summary>
    /// But a spy whose knife is on cooldown is an ordinary target again — which
    /// is what makes the guard two clauses rather than one.
    /// </summary>
    [Fact]
    public void ACloakedSpyWhoCannotStabIsHitNormally()
    {
        var state = default(WorldState);
        Put(ref state, 1, keys: PlayerKeys.Attack);
        Put(ref state, 2, x: ClearX + 8, y: ClearY, team: Team.Blue, playerClass: PlayerClass.Spy);
        state.Characters[2].Cloak = true;
        state.Weapons[2].ReadyToStab = false;

        Attack(ref state, 1);
        var before = state.Characters[2].Hp;

        Projectiles.Collide(World, ref state, ref DiscardedEvents.Events);

        Assert.Equal(before - 0.75, state.Characters[2].Hp, 9);
        Assert.Equal(0, CountOf(state, ProjectileKind.Bubble));
    }

    // --- the thrown blade -----------------------------------------------------

    /// <summary>
    /// ⚠️ <b>Throwing a blade costs nothing at the moment of the throw</b> — it
    /// only stops the tank replenishing.
    /// </summary>
    [Fact]
    public void ThrowingABladeOnlyStopsTheRefill()
    {
        var state = default(WorldState);
        Put(ref state, 1, keys: PlayerKeys.Special);

        Special(ref state);

        Assert.Equal(1, CountOf(state, ProjectileKind.Blade));
        Assert.Equal(90, state.Weapons[1].AmmoCount);
        Assert.False(state.Weapons[1].Replenishing);
        Assert.Equal(1, state.Weapons[1].BladesOut);
        Assert.Equal(Stats.RefireTime, state.Weapons[1].RefireTimer);
        Assert.Equal(Weapons.BladeLife, state.Weapons[1].ReloadTimer);
    }

    /// <summary>One blade at a time.</summary>
    [Fact]
    public void OnlyOneBladeMayBeInTheAir()
    {
        var state = default(WorldState);
        Put(ref state, 1, keys: PlayerKeys.Special);

        Special(ref state);
        state.Weapons[1].ReadyToShoot = true;
        Special(ref state);

        Assert.Equal(1, CountOf(state, ProjectileKind.Blade));
    }

    /// <summary>
    /// ⚠️ <b>A blade's <c>image_angle</c> is forced to 0</b>, whichever way it
    /// was thrown — the one projectile whose box does not follow its flight, for
    /// the reason the reference's own comment gives (GG2 issue #230).
    /// </summary>
    [Fact]
    public void ABladeIsNeverRotated()
    {
        var state = default(WorldState);
        Put(ref state, 1, keys: PlayerKeys.Special, aimDegrees: 45);

        Special(ref state);
        Assert.Equal(0, Live(state, 0).ImageAngle);

        // And the movement pass does not put it back, unlike a pellet's.
        Projectiles.Move(World, ref state, ref DiscardedEvents.Events);
        Assert.Equal(0, Live(state, 0).ImageAngle);
    }

    /// <summary>
    /// ⚠️ <b>A blade does not fall.</b> Its <c>with</c> block in
    /// <c>move_all_bullets</c> has no gravity line, where a pellet's has 0.15
    /// and a needle's 0.2.
    /// </summary>
    [Fact]
    public void ABladeFliesFlat()
    {
        var state = default(WorldState);
        Put(ref state, 1, keys: PlayerKeys.Special);

        Special(ref state);

        for (var i = 0; i < 5; i++)
            Projectiles.Move(World, ref state, ref DiscardedEvents.Events);

        Assert.Equal(1, CountOf(state, ProjectileKind.Blade));
        Assert.Equal(0, Live(state, 0).Motion.VSpeed);
        Assert.Equal(ClearY, Live(state, 0).Y);
    }

    /// <summary>
    /// The damage ramp: 17 above two thirds, 8 below one third, and linear in
    /// between.
    /// </summary>
    [Fact]
    public void TheBladeDamageRampIsReadAtTheThrow()
    {
        Assert.Equal(17, Weapons.BladeThrowDamage(90));
        Assert.Equal(8, Weapons.BladeThrowDamage(10));

        // Halfway between the thresholds is halfway between the damages.
        Assert.Equal(12.5, Weapons.BladeThrowDamage(45), 9);

        // ⚠️ The boundaries are `>` and `<`, so exactly on a threshold takes the
        // interpolation arm rather than the flat one — which agrees with it at
        // both ends and is worth pinning anyway.
        Assert.Equal(17, Weapons.BladeThrowDamage(60), 9);
        Assert.Equal(8, Weapons.BladeThrowDamage(30), 9);
    }

    /// <summary>And it is frozen onto the blade, not re-read as the tank drains.</summary>
    [Fact]
    public void ADepletedQuoteThrowsAWeakerBlade()
    {
        var strong = default(WorldState);
        Put(ref strong, 1, keys: PlayerKeys.Special);
        Special(ref strong);

        var weak = default(WorldState);
        Put(ref weak, 1, keys: PlayerKeys.Special);
        weak.Weapons[1].AmmoCount = 10;
        Special(ref weak);

        Assert.Equal(17, Live(strong, 0).HitDamage);
        Assert.Equal(8, Live(weak, 0).HitDamage);
    }

    // --- what the blade costs -------------------------------------------------

    /// <summary>
    /// ⚠️ <b>The blade bills its owner one unit a tick while it flies</b>, and
    /// its owner's tank is frozen meanwhile — so the two do not cancel out.
    /// </summary>
    [Fact]
    public void ABladeInFlightDrainsTheMagazine()
    {
        var state = default(WorldState);
        Put(ref state, 1, keys: PlayerKeys.Special);
        Special(ref state);

        Projectiles.BladeEndStep(ref state);
        Assert.Equal(89, state.Weapons[1].AmmoCount);
        Assert.Equal(1, Live(state, 0).AmmoDrained);

        Projectiles.BladeEndStep(ref state);
        Assert.Equal(88, state.Weapons[1].AmmoCount);
        Assert.Equal(2, Live(state, 0).AmmoDrained);
    }

    /// <summary>
    /// ⚠️ <b>A blade that comes home early is still charged the refire time.</b>
    /// One that hit something after two ticks has drained 2 and its Destroy
    /// event takes the other 3.
    /// </summary>
    [Fact]
    public void AShortLivedBladeIsChargedTheDifference()
    {
        var state = default(WorldState);
        Put(ref state, 1, keys: PlayerKeys.Special);
        Special(ref state);

        Projectiles.BladeEndStep(ref state);
        Projectiles.BladeEndStep(ref state);
        Assert.Equal(88, state.Weapons[1].AmmoCount);

        state.FreeProjectile(state.LiveProjectileHandle(0));

        Assert.Equal(85, state.Weapons[1].AmmoCount);
        Assert.Equal(0, state.Weapons[1].BladesOut);
    }

    /// <summary>
    /// And one that outlived the refire time is charged nothing extra — the
    /// refund is <c>if (ammoDrained &lt; refireTime)</c> and not a flat top-up.
    /// </summary>
    [Fact]
    public void ALongLivedBladeIsChargedNothingExtra()
    {
        var state = default(WorldState);
        Put(ref state, 1, keys: PlayerKeys.Special);
        Special(ref state);

        for (var i = 0; i < 10; i++)
            Projectiles.BladeEndStep(ref state);

        Assert.Equal(80, state.Weapons[1].AmmoCount);

        state.FreeProjectile(state.LiveProjectileHandle(0));

        Assert.Equal(80, state.Weapons[1].AmmoCount);
    }

    /// <summary>
    /// The refill comes back on the weapon's alarm 5, fifteen ticks after the
    /// throw — <em>not</em> when the blade lands.
    /// </summary>
    [Fact]
    public void TheRefillComesBackOnTheWeaponsOwnTimer()
    {
        var state = default(WorldState);
        Put(ref state, 1, keys: PlayerKeys.Special);
        Special(ref state);

        // The blade lands immediately; the tank stays frozen anyway.
        state.FreeProjectile(state.LiveProjectileHandle(0));
        Assert.False(state.Weapons[1].Replenishing);

        var events = default(TickEvents);
        for (var i = 0; i < Weapons.BladeLife; i++)
            Timers.Advance(ref state, null, ref events);

        Assert.True(state.Weapons[1].Replenishing);
    }

    // --- the blade against people ---------------------------------------------

    /// <summary>
    /// A blade damages the first enemy it reaches, shoves them hard, and dies
    /// in them.
    /// </summary>
    [Fact]
    public void ABladeStrikesOnceAndDies()
    {
        var state = default(WorldState);
        Put(ref state, 1, keys: PlayerKeys.Special);
        Put(ref state, 2, x: ClearX + 5, y: ClearY, team: Team.Blue, playerClass: PlayerClass.Soldier);

        Special(ref state, 1);
        var before = state.Characters[2].Hp;

        Projectiles.Collide(World, ref state, ref DiscardedEvents.Events);

        Assert.Equal(before - 17, state.Characters[2].Hp, 9);
        Assert.Equal(0, CountOf(state, ProjectileKind.Blade));

        // motion_add(blade.direction, blade.speed * 0.4) — 12 * 0.4.
        Assert.Equal(4.8, state.Characters[2].HSpeed, 9);
    }

    /// <summary>
    /// ⚠️ <b>An ubered enemy stops the blade and is still shoved</b>, taking no
    /// damage — the shove and the destroy are outside the uber guard.
    /// </summary>
    [Fact]
    public void AnUberedEnemyStopsTheBladeAndIsStillShoved()
    {
        var state = default(WorldState);
        Put(ref state, 1, keys: PlayerKeys.Special);
        Put(ref state, 2, x: ClearX + 5, y: ClearY, team: Team.Blue, playerClass: PlayerClass.Soldier);
        state.Characters[2].Ubered = true;

        Special(ref state, 1);
        var before = state.Characters[2].Hp;

        Projectiles.Collide(World, ref state, ref DiscardedEvents.Events);

        Assert.Equal(before, state.Characters[2].Hp);
        Assert.Equal(0, CountOf(state, ProjectileKind.Blade));
        Assert.Equal(4.8, state.Characters[2].HSpeed, 9);
    }

    /// <summary>A team-mate is untouched and the blade flies on.</summary>
    [Fact]
    public void ATeamMateDoesNotStopTheBlade()
    {
        var state = default(WorldState);
        Put(ref state, 1, keys: PlayerKeys.Special);
        Put(ref state, 2, x: ClearX + 5, y: ClearY, team: Team.Red, playerClass: PlayerClass.Soldier);

        Special(ref state, 1);
        var before = state.Characters[2].Hp;

        Projectiles.Collide(World, ref state, ref DiscardedEvents.Events);

        Assert.Equal(before, state.Characters[2].Hp);
        Assert.Equal(1, CountOf(state, ProjectileKind.Blade));
    }

    /// <summary>
    /// ⚠️ <b>A Quote's own blade pops their own bubbles</b> — the condition is
    /// "an enemy's, or the same player's", so a team-mate Quote's screen is
    /// safe and their own is not.
    /// </summary>
    [Fact]
    public void AQuotesOwnBladeCutsThroughTheirOwnScreen()
    {
        var state = default(WorldState);
        Put(ref state, 1, keys: PlayerKeys.Attack | PlayerKeys.Special);
        Attack(ref state, 1);

        var bubble = Live(state, 0);
        state.Weapons[1].ReadyToShoot = true;
        Special(ref state, 1);

        // Sit the blade on the bubble.
        var bladeHandle = state.LiveProjectileHandle(1);
        state.Projectiles[bladeHandle.Index].X = bubble.X;
        state.Projectiles[bladeHandle.Index].Y = bubble.Y;

        Projectiles.Collide(World, ref state, ref DiscardedEvents.Events);

        Assert.Equal(0, CountOf(state, ProjectileKind.Bubble));
        Assert.Equal(1, CountOf(state, ProjectileKind.Blade));
    }

    /// <summary>A team-mate Quote's blade leaves the screen standing.</summary>
    [Fact]
    public void ATeamMatesBladeLeavesTheScreenStanding()
    {
        var state = default(WorldState);
        Put(ref state, 1, keys: PlayerKeys.Attack);
        Put(ref state, 2, x: ClearX, y: ClearY, keys: PlayerKeys.Special, team: Team.Red);

        Attack(ref state, 1);
        var bubble = Live(state, 0);
        Special(ref state, 2);

        var bladeHandle = state.LiveProjectileHandle(1);
        state.Projectiles[bladeHandle.Index].X = bubble.X;
        state.Projectiles[bladeHandle.Index].Y = bubble.Y;

        Projectiles.Collide(World, ref state, ref DiscardedEvents.Events);

        Assert.Equal(1, CountOf(state, ProjectileKind.Bubble));
    }

    // --- what dies with the weapon --------------------------------------------

    /// <summary>
    /// ⚠️ <b>Bubbles and the blade die with their owner</b>, silently — the
    /// Blade's Destroy event takes both, the way the Minegun takes its mines.
    /// </summary>
    [Fact]
    public void QuotesProjectilesDieWithThem()
    {
        var state = default(WorldState);
        Put(ref state, 1, keys: PlayerKeys.Attack | PlayerKeys.Special);

        Attack(ref state, 1);
        state.Weapons[1].ReadyToShoot = true;
        Special(ref state, 1);

        // A rocket somebody else fired stays up, which is the control: this is
        // the weapon taking its own projectiles, not a blanket sweep.
        var rocket = new ProjectileState
        {
            Kind = ProjectileKind.Rocket,
            X = ClearX,
            Y = ClearY,
            Team = Team.Blue,
            OwnerSlot = 2,
            Reflector = ProjectileState.NoOwner,
            Source = DamageSource.Rocketlauncher,
        };
        var rocketHandle = state.SpawnProjectile(rocket);

        Assert.Equal(3, state.LiveProjectileCount);

        state.Clear(1);

        Assert.Equal(1, state.LiveProjectileCount);
        Assert.True(state.IsLive(rocketHandle));
    }

    // --- what pops a bubble from outside --------------------------------------

    /// <summary>
    /// ⚠️ <b>The three explosions that pop bubbles use three different
    /// formulas.</b> A generator's reaches its whole 400-pixel radius where a
    /// rocket's reaches 0.66 of its own — so a bubble 200 pixels from a
    /// generator pops and one 200 pixels from a rocket does not.
    /// </summary>
    [Fact]
    public void AGeneratorReachesFurtherThanARocket()
    {
        var far = default(WorldState);
        Put(ref far, 1, keys: PlayerKeys.Attack);
        Attack(ref far, 1);

        var handle = far.LiveProjectileHandle(0);
        far.Projectiles[handle.Index].X = ClearX + 200;

        var box = Aabb.ForCharacter(
            new CharacterBox(left: -1, right: 1, top: -1, bottom: 1), ClearX, ClearY);

        Explosions.PopBubblesByBox(ref far, box, Explosions.BlastRadiusFor(ProjectileKind.Rocket) * 0.66);
        Assert.True(far.IsLive(handle), "a rocket's sweep reached 200 pixels");

        Explosions.PopBubblesByBox(ref far, box, Generators.BlastRadius);
        Assert.False(far.IsLive(handle), "a generator's sweep did not reach 200 pixels");
    }

    /// <summary>
    /// And a mine's is the odd one: centre to centre, and <em>widened</em> by the
    /// bubble's own four-pixel self-pop radius rather than scaled down.
    /// </summary>
    [Fact]
    public void AMineSweepsCentreToCentreAndWiderThanItsBlast()
    {
        var radius = Explosions.BlastRadiusFor(ProjectileKind.Mine);

        var inside = default(WorldState);
        Put(ref inside, 1, keys: PlayerKeys.Attack);
        Attack(ref inside, 1);
        var handle = inside.LiveProjectileHandle(0);

        // Just past the blast radius, but inside it plus the self-pop radius.
        inside.Projectiles[handle.Index].X = ClearX + radius + 2;
        inside.Projectiles[handle.Index].Y = ClearY;

        Explosions.PopBubblesByPoint(ref inside, ClearX, ClearY, radius + Projectiles.BubbleSelfPopRadius);

        Assert.False(inside.IsLive(handle),
            "the self-pop radius did not widen the sweep past the blast radius");
    }

    // --- through a whole tick -------------------------------------------------

    /// <summary>
    /// The bubble's Step and its leash both run inside
    /// <see cref="MatchSimulation.Tick"/>, and the blade's End Step with them.
    /// </summary>
    /// <remarks>
    /// The control for the three phases <c>MatchSimulation</c> gained: every
    /// other test in this file drives them directly, which gates the rule and
    /// not the wiring. Deleting any of the three calls leaves those tests green.
    /// </remarks>
    [Fact]
    public void ATickRunsAllThreeOfQuotesPhases()
    {
        var state = default(WorldState);
        Put(ref state, 1, keys: PlayerKeys.Attack | PlayerKeys.Special);

        Attack(ref state, 1);
        state.Weapons[1].ReadyToShoot = true;
        Special(ref state, 1);

        var bubble = state.LiveProjectileHandle(0);
        var blade = state.LiveProjectileHandle(1);

        // Behind the Quote, out of the blade's way: a Quote's own blade pops
        // their own bubbles, which is a different rule and has its own test.
        // ⚠️ And clear of the wall at x = 2137: parked inside it, the obstacle
        // response moved the bubble on its own and this test passed with
        // BubbleStep deleted.
        state.Projectiles[bubble.Index].X = ClearX - 100;
        var startX = state.Projectiles[bubble.Index].X;

        MatchSimulation.Tick(World, ref state, []);

        // BubbleStep moved it.
        Assert.True(state.IsLive(bubble), "the bubble was destroyed by an ordinary tick");
        Assert.NotEqual(startX, state.Projectiles[bubble.Index].X);

        // BladeEndStep billed the magazine.
        Assert.True(state.IsLive(blade), "the blade was destroyed by an ordinary tick");
        Assert.Equal(1, state.Projectiles[blade.Index].AmmoDrained);
        Assert.Equal(89, state.Weapons[1].AmmoCount);

        // And the leash is armed: dragged out of range, the next tick pops it.
        state.Projectiles[bubble.Index].X = state.Characters[1].X + 250;
        MatchSimulation.Tick(World, ref state, []);

        Assert.False(state.IsLive(bubble), "the leash did not run inside the tick");
    }

    /// <summary>
    /// ⚠️ <b>The drag's 0.6 is one of the five literals GM8 parses a ULP above
    /// the IEEE nearest</b>, so it has to come from <see cref="Gml"/> — asserted
    /// to the last bit, because the difference is 5e-16 and any tolerance at all
    /// hides it.
    /// </summary>
    [Fact]
    public void TheBubblesDragUsesGmlsOwnPointSix()
    {
        var state = default(WorldState);
        Put(ref state, 1, keys: PlayerKeys.Attack);
        Attack(ref state);

        var handle = state.LiveProjectileHandle(0);
        state.Projectiles[handle.Index].X = ClearX + 90;
        state.Projectiles[handle.Index].Y = ClearY;
        state.Projectiles[handle.Index].Motion.Speed = 0;

        Projectiles.BubbleStep(ref state);

        Assert.Equal((0 + 2 + 2.2) * Gml.PointSix, state.Projectiles[handle.Index].Motion.Speed);
        Assert.NotEqual((0 + 2 + 2.2) * 0.6, state.Projectiles[handle.Index].Motion.Speed);
    }

    /// <summary>The leash is 160, not 320 — a bubble a quarter-screen out pops.</summary>
    [Fact]
    public void TheLeashIsOneHundredAndSixty()
    {
        var state = default(WorldState);
        Put(ref state, 1, keys: PlayerKeys.Attack);
        Attack(ref state);

        var handle = state.LiveProjectileHandle(0);
        state.Projectiles[handle.Index].X = ClearX + 250;

        Projectiles.BubbleBeginStep(ref state);

        Assert.False(state.IsLive(handle));
    }

    /// <summary>A blade takes only its owner's <em>horizontal</em> speed.</summary>
    /// <remarks>
    /// <c>with(shot) hspeed += owner.hspeed</c> — a falling Quote does not throw
    /// the blade downwards, which a <c>motion_add</c> of the owner's vector
    /// (what a bubble gets) would do.
    /// </remarks>
    [Fact]
    public void ABladeTakesOnlyItsOwnersHorizontalSpeed()
    {
        var state = default(WorldState);
        Put(ref state, 1, keys: PlayerKeys.Special);
        state.Characters[1].Motion.SetComponents(4, 9);

        Special(ref state);

        Assert.Equal(16, Live(state, 0).Motion.HSpeed, 9);
        Assert.Equal(0, Live(state, 0).Motion.VSpeed, 9);
    }

    // --- the walls ------------------------------------------------------------

    /// <summary>
    /// A team gate destroys a bubble even where the map's own mask is clear —
    /// the four wall events between them are <c>gunSetSolids</c> without
    /// <c>Obstacle</c>.
    /// </summary>
    [Fact]
    public void AGateDestroysABubble()
    {
        const double GateX = 1586;
        const double GateY = 470;

        var state = default(WorldState);
        Put(ref state, 1, x: GateX, y: GateY, keys: PlayerKeys.Attack);
        Attack(ref state);

        var handle = state.LiveProjectileHandle(0);
        state.Projectiles[handle.Index].X = GateX;
        state.Projectiles[handle.Index].Y = GateY;

        // Asserted rather than assumed: this has to be the gate and not the map.
        Assert.True(World.PlaceFreeInMask(state.Projectiles[handle.Index].Box));

        Projectiles.Collide(World, ref state, ref DiscardedEvents.Events);

        Assert.False(state.IsLive(handle));
    }

    /// <summary>
    /// ⚠️ And <c>global.mapchanging</c> opens it again — for a bullet a team
    /// gate opens and an intel gate does not, which is the trap
    /// <see cref="SolidRect.IsSolidTo"/> records and which this reuses rather
    /// than restating.
    /// </summary>
    [Fact]
    public void AnOpenGateDoesNotDestroyABubble()
    {
        const double GateX = 1586;
        const double GateY = 470;

        var state = default(WorldState);
        state.MapChanging = true;
        Put(ref state, 1, x: GateX, y: GateY, keys: PlayerKeys.Attack);
        Attack(ref state);

        var handle = state.LiveProjectileHandle(0);
        state.Projectiles[handle.Index].X = GateX;
        state.Projectiles[handle.Index].Y = GateY;

        Projectiles.Collide(World, ref state, ref DiscardedEvents.Events);

        Assert.True(state.IsLive(handle));
    }

    /// <summary>
    /// ⚠️ <b>A wall stops a bubble rather than killing it</b> — the only
    /// projectile in the game with a wall response instead of a wall death. It
    /// backs out along its own heading and loses the axis that is blocked.
    /// </summary>
    /// <remarks>
    /// The map is solid from x = 2137 at this height and clear below it, found
    /// by scanning; the bubble starts three pixels inside.
    /// </remarks>
    [Fact]
    public void AWallStopsABubbleWithoutKillingIt()
    {
        const double WallY = 300;
        const double Inside = 2140;

        var state = default(WorldState);
        Put(ref state, 1, x: Inside - 60, y: WallY, keys: PlayerKeys.Attack);
        Attack(ref state);

        var handle = state.LiveProjectileHandle(0);
        state.Projectiles[handle.Index].X = Inside;
        state.Projectiles[handle.Index].Y = WallY;

        Assert.False(World.PlaceFreeInMask(state.Projectiles[handle.Index].Box));

        Projectiles.Collide(World, ref state, ref DiscardedEvents.Events);

        Assert.True(state.IsLive(handle), "the bubble died on a wall");
        Assert.True(World.PlaceFreeInMask(state.Projectiles[handle.Index].Box),
            "the bubble was left inside the wall");
        Assert.Equal(0, state.Projectiles[handle.Index].Motion.HSpeed);
    }

    /// <summary>
    /// ⚠️ <b>The six-pixel nudge</b>: a bubble whose next step is blocked but
    /// whose next step six pixels lower is not drops under the corner and keeps
    /// its speed.
    /// </summary>
    /// <remarks>
    /// (520, 216) on <c>ctf_truefort</c> is a spot where the box ten pixels
    /// ahead is solid and the same box six pixels lower is clear — found by
    /// scanning, and asserted below so this cannot quietly stop testing the
    /// nudge.
    /// </remarks>
    [Fact]
    public void ABubbleSlidesSixPixelsUnderACorner()
    {
        const double LedgeX = 520;
        const double LedgeY = 216;

        var state = default(WorldState);
        Put(ref state, 1, x: LedgeX, y: LedgeY, keys: PlayerKeys.Attack);
        Attack(ref state);

        var handle = state.LiveProjectileHandle(0);

        // Ten pixels into the ledge, moving right at its muzzle speed.
        state.Projectiles[handle.Index].X = LedgeX + 10;
        state.Projectiles[handle.Index].Y = LedgeY;

        Assert.False(World.PlaceFreeInMask(state.Projectiles[handle.Index].Box));

        Projectiles.Collide(World, ref state, ref DiscardedEvents.Events);

        Assert.True(state.IsLive(handle));
        Assert.Equal(LedgeY + 6, state.Projectiles[handle.Index].Y);

        // And, having got under it, the bubble keeps going.
        Assert.Equal(10, state.Projectiles[handle.Index].Motion.HSpeed, 9);
    }

    /// <summary>
    /// ⚠️ <b>Only the axis that is blocked is stopped</b>, and the three tests
    /// are three statements rather than one decision — each reads what the ones
    /// above it left behind.
    /// </summary>
    /// <remarks>
    /// The control for <c>hspeed = 0</c> specifically. With a purely horizontal
    /// bubble the line is redundant: the <c>speed = 0</c> below it would zero
    /// the pair anyway, and deleting it changes nothing. Giving the bubble a
    /// downward component into clear air separates them — the horizontal stops
    /// and the vertical is kept, where a port with only the <c>speed = 0</c>
    /// line would lose both.
    /// </remarks>
    [Fact]
    public void AWallStopsOnlyTheAxisThatIsBlocked()
    {
        const double WallY = 300;
        const double Inside = 2140;

        var state = default(WorldState);
        Put(ref state, 1, x: Inside - 60, y: WallY, keys: PlayerKeys.Attack);
        Attack(ref state);

        var handle = state.LiveProjectileHandle(0);
        state.Projectiles[handle.Index].X = Inside;
        state.Projectiles[handle.Index].Y = WallY;
        state.Projectiles[handle.Index].Motion.SetComponents(10, 6);

        Projectiles.Collide(World, ref state, ref DiscardedEvents.Events);

        Assert.True(state.IsLive(handle));
        Assert.Equal(0, state.Projectiles[handle.Index].Motion.HSpeed);
        Assert.Equal(6, state.Projectiles[handle.Index].Motion.VSpeed, 9);
    }

    /// <summary>A blade, unlike a bubble, simply dies in the wall it reaches.</summary>
    [Fact]
    public void ABladeDiesInAWall()
    {
        const double WallY = 300;

        var state = default(WorldState);
        Put(ref state, 1, x: 2100, y: WallY, keys: PlayerKeys.Special);
        Special(ref state);

        var handle = state.LiveProjectileHandle(0);

        for (var i = 0; i < 6 && state.IsLive(handle); i++)
            Projectiles.Move(World, ref state, ref DiscardedEvents.Events);

        Assert.False(state.IsLive(handle), "a blade flew through the wall");
    }

    // --- what an explosion does to a screen -----------------------------------

    /// <summary>
    /// ⚠️ <b>A rocket flies straight through a bubble that dies on it</b> — a
    /// <c>Rocket</c> has no <c>Collision with Bubble</c> event of its own, so
    /// only one half of the pair exists.
    /// </summary>
    [Fact]
    public void ARocketPassesThroughABubbleThatDiesOnIt()
    {
        var state = default(WorldState);
        Put(ref state, 1, keys: PlayerKeys.Attack);
        Attack(ref state);

        // Well clear of the Quote: a blue rocket that overlapped them would
        // explode on the character rather than reaching the bubble.
        var handle = state.LiveProjectileHandle(0);
        state.Projectiles[handle.Index].X = ClearX + 100;

        var bubble = state.Projectiles[handle.Index];
        var rocket = SpawnRocket(ref state, bubble.X, bubble.Y, Team.Blue);

        Projectiles.Collide(World, ref state, ref DiscardedEvents.Events);

        Assert.Equal(0, CountOf(state, ProjectileKind.Bubble));
        Assert.True(state.IsLive(rocket), "the rocket died on a bubble");
    }

    /// <summary>
    /// A real rocket blast pops a bubble inside 0.66 of its radius and leaves
    /// one outside it standing.
    /// </summary>
    /// <remarks>
    /// The rocket's radius is 65, so the bubble sweep reaches about 43 — this
    /// drives a genuine explosion rather than calling
    /// <see cref="Explosions.PopBubblesByBox"/> with a hand-picked number, which
    /// would gate the arithmetic and not the call site.
    /// </remarks>
    [Fact]
    public void ARocketBlastPopsTheBubblesInsideTwoThirdsOfItsRadius()
    {
        var state = default(WorldState);
        Put(ref state, 1, keys: PlayerKeys.Attack);

        // Two bubbles: one 30 pixels from where the rocket will go off, one 400.
        Attack(ref state);
        state.Weapons[1].ReadyToShoot = true;
        Attack(ref state);

        var near = state.LiveProjectileHandle(0);
        var far = state.LiveProjectileHandle(1);

        // A victim for the rocket to hit, out of both bubbles' way.
        Put(ref state, 2, x: ClearX + 120, y: ClearY, team: Team.Blue, playerClass: PlayerClass.Soldier);

        state.Projectiles[near.Index].X = ClearX + 150;
        state.Projectiles[near.Index].Y = ClearY - 30;

        // ⚠️ Sixty pixels straight up: a box gap of about 53, which is outside
        // the 0.66 sweep and *inside* the 65-pixel blast. A bubble parked far
        // away instead would survive whatever factor the sweep used.
        state.Projectiles[far.Index].X = ClearX + 120;
        state.Projectiles[far.Index].Y = ClearY - 60;

        SpawnRocket(ref state, ClearX + 120, ClearY, Team.Red, owner: 1);

        Projectiles.Collide(World, ref state, ref DiscardedEvents.Events);

        Assert.False(state.IsLive(near), "a bubble beside the blast survived");
        Assert.True(state.IsLive(far),
            "a bubble inside the blast but outside two thirds of it was popped");
    }

    /// <summary>
    /// ⚠️ <b>A mine's sweep is centre-to-centre and <em>wider</em> than its own
    /// blast</b>, by the bubble's four-pixel self-pop radius — so a bubble 42
    /// pixels from a mine pops where a character 42 pixels away takes nothing.
    /// </summary>
    [Fact]
    public void AMineBlastPopsABubbleJustOutsideItsRadius()
    {
        var state = default(WorldState);
        Put(ref state, 1, keys: PlayerKeys.Attack);
        Attack(ref state);

        var handle = state.LiveProjectileHandle(0);
        state.Projectiles[handle.Index].X = ClearX + 42;
        state.Projectiles[handle.Index].Y = ClearY;

        var mine = new ProjectileState
        {
            Kind = ProjectileKind.Mine,
            X = ClearX,
            Y = ClearY,
            Team = Team.Blue,
            OwnerSlot = 2,
            Reflector = ProjectileState.NoOwner,
            Source = DamageSource.Minegun,
            Stickied = true,
        };
        var mineHandle = state.SpawnProjectile(mine);

        Explosions.DetonateMine(World, ref state, mineHandle, ref DiscardedEvents.Events);

        Assert.False(state.IsLive(handle),
            "the mine's sweep was not widened past its 40-pixel blast radius");
    }

    /// <summary>
    /// And a generator's reaches its whole 400 — the largest sweep in the game,
    /// where a rocket's is the smallest.
    /// </summary>
    [Fact]
    public void AGeneratorGoingUpClearsBubblesThreeHundredPixelsAway()
    {
        var level = LevelMap.FromPng("gen_destroy", ReferenceTree.ReadMap("gen_destroy")).ForArea(1);
        var world = new CollisionWorld(level);

        var state = default(WorldState);
        Generators.Place(ref state, level);

        Assert.True(state.RedGenerator.Exists, "the map placed no red generator");

        Put(ref state, 1, x: state.RedGenerator.X + 330, y: state.RedGenerator.Y,
            keys: PlayerKeys.Attack);

        var events = default(TickEvents);
        Weapons.FireStep(world, ref state, 1, ref events);

        var handle = state.LiveProjectileHandle(0);
        // ⚠️ 330 rather than 200: two thirds of 400 is 264, so a bubble any
        // closer would pop under a rocket-shaped sweep as well and the number
        // would not be gated at all.
        state.Projectiles[handle.Index].X = state.RedGenerator.X + 330;
        state.Projectiles[handle.Index].Y = state.RedGenerator.Y;

        Generators.Destroy(world, ref state, Team.Red, ref DiscardedEvents.Events);

        Assert.False(state.IsLive(handle),
            "a generator's sweep did not reach 330 pixels");
    }

    /// <summary>
    /// ⚠️ <b>The collision stage walks the projectiles that existed when it
    /// began</b>, not a cursor over the live array — and an explosion that frees
    /// an <em>older</em> projectile is what tells the two apart.
    /// </summary>
    /// <remarks>
    /// A cursor compacts under itself: the rocket below pops a bubble created
    /// before it and then destroys itself, which moves two entries down and
    /// steps the cursor clean past the pellet behind them. The pellet's victim
    /// then takes no damage at all — silently, and only when a bubble happens to
    /// be older than a rocket.
    /// </remarks>
    [Fact]
    public void AnExplosionDoesNotMakeTheStageSkipTheProjectileBehindIt()
    {
        var state = default(WorldState);
        Put(ref state, 1, keys: PlayerKeys.Attack);

        // 0: a bubble beside where the rocket will go off.
        Attack(ref state);
        var bubble = state.LiveProjectileHandle(0);
        state.Projectiles[bubble.Index].X = ClearX + 150;
        state.Projectiles[bubble.Index].Y = ClearY - 30;

        // The rocket's victim, and the pellet's, 600 pixels apart so neither
        // blast can reach the other.
        Put(ref state, 2, x: ClearX + 120, y: ClearY, team: Team.Blue, playerClass: PlayerClass.Soldier);
        Put(ref state, 3, x: ClearX + 800, y: ClearY, team: Team.Blue, playerClass: PlayerClass.Soldier);

        // 1: the rocket. 2: the pellet, behind it in creation order.
        SpawnRocket(ref state, ClearX + 120, ClearY, Team.Red, owner: 1);
        SpawnShot(ref state, ClearX + 800, ClearY, Team.Red, perseverant: false, owner: 1);

        var before = state.Characters[3].Hp;

        Projectiles.Collide(World, ref state, ref DiscardedEvents.Events);

        Assert.False(state.IsLive(bubble), "the rocket's blast did not pop the bubble");
        Assert.Equal(before - 8, state.Characters[3].Hp, 9);
    }

    private static ProjectileHandle SpawnRocket(
        ref WorldState state, double x, double y, Team team, int owner = 4)
    {
        var rocket = new ProjectileState
        {
            Kind = ProjectileKind.Rocket,
            X = x,
            Y = y,
            Team = team,
            OwnerSlot = (byte)owner,
            Reflector = ProjectileState.NoOwner,
            Source = DamageSource.Rocketlauncher,
            HitDamage = 25,
            Lifetime = 100,
            Knockback = 8,
            DistanceToTravel = 800,
            LastKnownX = x,
            LastKnownY = y,
            HasMoved = true,
        };
        rocket.Motion.Direction = 0;
        rocket.ImageAngle = 0;
        rocket.Motion.Speed = 13;

        return state.SpawnProjectile(rocket);
    }

    private static void SpawnShot(
        ref WorldState state, double x, double y, Team team, bool perseverant, int owner = 3)
    {
        var shot = new ProjectileState
        {
            Kind = ProjectileKind.Shot,
            X = x,
            Y = y,
            Team = team,
            OwnerSlot = (byte)owner,
            Reflector = ProjectileState.NoOwner,
            Source = DamageSource.Scattergun,
            HitDamage = 8,
            Lifetime = 40,
            Perseverant = perseverant,

            // Already past its muzzle test, so the collision phase does not kill
            // it for being newly born.
            HasMoved = true,
        };
        shot.Motion.Direction = 180;
        shot.ImageAngle = 180;
        shot.Motion.Speed = 1;

        state.SpawnProjectile(shot);
    }
}
