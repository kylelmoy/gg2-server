using GG2.Maps;
using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Tests;

/// <summary>
/// The Minegun, the mine and its blast: phase 5 stage 6.
/// </summary>
/// <remarks>
/// <b>No recording gates any of this.</b>
/// <c>reference-ctf_truefort-combat.trace</c> contains a demoman, but its first
/// <c>WEAPON_FIRE</c> of any kind is on <b>tick 647 of 748</b> — so for 86% of
/// its length it is a physics recording rather than a combat one, and its
/// residual has nothing to do with weapons. These tests are the gate, and each
/// was checked by deleting the line it is about.
/// <para>
/// Transcribed from <c>Objects/Weapons/Minegun.events/{Create, User Event 1,
/// User Event 2, User Event 3, Destroy}.xml</c>,
/// <c>Objects/Projectiles/Mine.events/{Create, Destroy, User Event 2}.xml</c>
/// and the <c>with(Mine)</c> block of
/// <c>Scripts/Physics/System/move_all_bullets.gml</c>.
/// </para>
/// </remarks>
public sealed class MinegunTests
{
    private const string Map = "ctf_truefort";

    /// <summary>A standing position: the floor is directly under it.</summary>
    private const double OpenX = 2600;

    private const double OpenY = 708;

    /// <summary>Sixty pixels up, which is genuinely airborne.</summary>
    private const double AirY = OpenY - 60;

    /// <summary><c>Mine.events/Create.xml</c>.</summary>
    private const double BlastRadius = 40;

    private const double ExplosionDamage = 45;

    private static CollisionWorld? _sharedWorld;

    private static CollisionWorld World => _sharedWorld ??=
        new CollisionWorld(LevelMap.FromPng(Map, ReferenceTree.ReadMap(Map)));

    private static void Put(
        ref WorldState state,
        int slot,
        PlayerClass playerClass,
        Team team,
        double x,
        double y,
        PlayerKeys keys = PlayerKeys.None,
        double aimDegrees = 0)
    {
        state.Characters[slot] = new CharacterState
        {
            Class = playerClass,
            Team = team,
            X = x,
            Y = y,
            Hp = ClassStats.For(playerClass).MaxHp,
            Facing = 1,
            LastFacing = 1,
            RunPower = ClassStats.For(playerClass).RunPower,
            JumpStrength = ClassStats.BaseJumpStrength,
            CloakAlpha = 1,
            CanGrabIntel = true,
            LastDamageDealer = CharacterState.Nobody,
            SecondToLastDamageDealer = CharacterState.Nobody,
            BurnedBy = CharacterState.Nobody,
            Healer = CharacterState.Nobody,
            NetAimDirection = (ushort)Math.Round(aimDegrees * 65536.0 / 360.0),
            KeyState = keys,
        };

        state.Weapons[slot] = WeaponState.Spawn(playerClass, x, y);
        state.Weapons[slot].ReadyToShoot = true;
        state.Weapons[slot].RefireTimer = WeaponState.Inactive;
        state.Alive[slot] = true;
    }

    /// <summary>A mine as the launcher makes one, placed by hand.</summary>
    private static ProjectileState Mine(
        double x,
        double y,
        int ownerSlot,
        Team team,
        double direction = 0,
        double speed = 12,
        bool stuck = false)
    {
        var mine = new ProjectileState
        {
            Kind = ProjectileKind.Mine,
            Reflector = ProjectileState.NoOwner,
            X = x,
            Y = y,
            OwnerSlot = (byte)ownerSlot,
            Team = team,
            Source = DamageSource.Minegun,
            Knockback = 10,
            SplashThreshold = !stuck,
            Stickied = stuck,
        };

        mine.Motion.Direction = direction;
        mine.Motion.Speed = stuck ? 0 : speed;
        mine.ImageAngle = 0;

        return mine;
    }

    private static ProjectileState Live(in WorldState state, int index) =>
        state.Projectiles[state.LiveProjectileSlot(index)];

    private static int FloorBelow(int x, int y)
    {
        for (var probe = y; probe < y + 600; probe++)
        {
            if (!World.PlaceFree(new Aabb(x, probe, x, probe), CollisionFilter.ForBullet()))
                return probe;
        }

        Assert.Fail($"no floor under ({x}, {y}) on {Map}");
        return 0;
    }

    // --- the launcher ---------------------------------------------------------

    /// <summary>
    /// One mine, ten pixels ahead of the weapon, at speed 12 — and with its box
    /// angle set back to zero.
    /// </summary>
    /// <remarks>
    /// <c>createShot</c> sets <c>image_angle</c> to the aim and
    /// <c>Minegun.events/User Event 3.xml</c> immediately writes
    /// <c>oid.image_angle = 0</c>. A mine is the one projectile with an
    /// axis-aligned box however it is thrown, and unlike the Shot and the Rocket
    /// its movement block never writes the angle again.
    /// </remarks>
    [Fact]
    public void TheLauncherMakesOneAxisAlignedMine()
    {
        var state = default(WorldState);
        var events = default(TickEvents);
        Put(ref state, 1, PlayerClass.Demoman, Team.Red, OpenX, OpenY, PlayerKeys.Attack, aimDegrees: 45);

        Weapons.FireStep(World, ref state, 1, ref events);

        Assert.Equal(1, state.LiveProjectileCount);

        var mine = Live(state, 0);
        Assert.Equal(ProjectileKind.Mine, mine.Kind);
        Assert.Equal(OpenX + Gml.LengthDirX(10, 45), mine.X);
        Assert.Equal(OpenY + Gml.LengthDirY(10, 45), mine.Y);
        Assert.Equal(12, mine.Motion.Speed);
        Assert.Equal(45, mine.Motion.Direction);
        Assert.Equal(DamageSource.Minegun, mine.Source);

        // The aim was 45 degrees and the box is square to the world anyway.
        Assert.Equal(0, mine.ImageAngle);

        Assert.Equal(1, state.Weapons[1].Lobbed);
        Assert.Equal(7, state.Weapons[1].AmmoCount);
        Assert.Equal(26, state.Weapons[1].RefireTimer);
    }

    /// <summary>
    /// A demoman already holding eight mines does not fire, and does not draw a
    /// seed.
    /// </summary>
    /// <remarks>
    /// The <c>lobbed &lt; maxMines</c> clause is in User Event 1 alongside the
    /// ready and ammo tests, so it bails <em>before</em> <c>irandom(65535)</c>.
    /// A port that checked it inside User Event 3 instead would broadcast a
    /// <c>WEAPON_FIRE</c> for a mine that does not exist and would advance the
    /// shared stream, desynchronising every later shot.
    /// </remarks>
    [Fact]
    public void ADemomanAtEightMinesIsSilent()
    {
        var state = default(WorldState);
        var events = default(TickEvents);
        Put(ref state, 1, PlayerClass.Demoman, Team.Red, OpenX, OpenY, PlayerKeys.Attack);
        state.Weapons[1].Lobbed = 8;

        var before = state.Random;
        Weapons.FireStep(World, ref state, 1, ref events);

        Assert.Equal(0, state.LiveProjectileCount);
        Assert.False(events.Fired[1]);
        Assert.Equal(before, state.Random);
        Assert.True(state.Weapons[1].ReadyToShoot);
    }

    /// <summary>The control: seven mines is fine.</summary>
    [Fact]
    public void ADemomanAtSevenMinesStillFires()
    {
        var state = default(WorldState);
        var events = default(TickEvents);
        Put(ref state, 1, PlayerClass.Demoman, Team.Red, OpenX, OpenY, PlayerKeys.Attack);
        state.Weapons[1].Lobbed = 7;

        Weapons.FireStep(World, ref state, 1, ref events);

        Assert.Equal(1, state.LiveProjectileCount);
        Assert.Equal(8, state.Weapons[1].Lobbed);
        Assert.True(events.Fired[1]);
    }

    // --- flight ---------------------------------------------------------------

    /// <summary>A mine falls at 0.2 a tick.</summary>
    [Fact]
    public void AMineFallsUnderGravity()
    {
        var state = default(WorldState);

        // Sideways and barely moving, so vspeed starts at exactly zero: the
        // `speed != 0` guard skips the whole block for a mine at rest, so it
        // cannot simply be given no velocity at all.
        state.SpawnProjectile(Mine(OpenX, AirY - 400, ownerSlot: 1, Team.Red, direction: 0, speed: 0.0001));

        for (var tick = 1; tick <= 3; tick++)
        {
            Projectiles.Move(World, ref state, ref DiscardedEvents.Events);
            Assert.Equal(0.2 * tick, Live(state, 0).Motion.VSpeed, 9);
        }
    }

    /// <summary>
    /// And never faster than 8, which is the only terminal velocity on any
    /// projectile in the game.
    /// </summary>
    /// <remarks>
    /// <c>vspeed = min(vspeed, 8)</c>. Asserted one tick either side of the cap
    /// rather than by dropping a mine a long way — a falling mine finds the
    /// floor, sticks, and stops answering, which makes a "did it exceed 8"
    /// assertion pass for the wrong reason.
    /// </remarks>
    [Fact]
    public void AMineNeverFallsFasterThanEight()
    {
        var state = default(WorldState);
        var below = Mine(OpenX, AirY - 400, ownerSlot: 1, Team.Red);
        below.Motion.SetComponents(0, 7.7);
        state.SpawnProjectile(below);

        // 7.7 + 0.2 is under the cap and lands on it exactly.
        Projectiles.Move(World, ref state, ref DiscardedEvents.Events);
        Assert.Equal(7.9, Live(state, 0).Motion.VSpeed, 9);

        // 7.9 + 0.2 would be 8.1. The cap takes it.
        Projectiles.Move(World, ref state, ref DiscardedEvents.Events);
        Assert.Equal(8, Live(state, 0).Motion.VSpeed, 9);

        // And it stays there rather than creeping.
        Projectiles.Move(World, ref state, ref DiscardedEvents.Events);
        Assert.Equal(8, Live(state, 0).Motion.VSpeed, 9);
    }

    /// <summary>
    /// A mine that reaches the floor sticks to it instead of dying, and stops
    /// moving for good.
    /// </summary>
    [Fact]
    public void AMineLandingOnTheMapSticks()
    {
        var state = default(WorldState);
        var floor = FloorBelow((int)OpenX, (int)OpenY);
        state.SpawnProjectile(Mine(OpenX, floor - 40, ownerSlot: 1, Team.Red, direction: 270, speed: 12));

        for (var tick = 0; tick < 10 && state.LiveProjectileCount > 0 && !Live(state, 0).Stickied; tick++)
            Projectiles.Move(World, ref state, ref DiscardedEvents.Events);

        Assert.Equal(1, state.LiveProjectileCount);

        var mine = Live(state, 0);
        Assert.True(mine.Stickied, "the mine did not stick to the floor");
        Assert.Equal(0, mine.Motion.Speed);
        Assert.True(World.PlaceFree(mine.Box, CollisionFilter.ForBullet()), "it stuck inside the wall");

        // Still true on the pass that landed it: `splashThreshhold` is set
        // from `!stickied` at the *top* of the block and the stick happens at
        // the bottom, so the cutoff a blast sees is always a tick behind. That
        // is the reason ProjectileState stores it instead of deriving it.
        Assert.True(mine.SplashThreshold);

        // And it stays exactly where it landed, losing the cutoff as it does.
        var (x, y) = (mine.X, mine.Y);
        Projectiles.Move(World, ref state, ref DiscardedEvents.Events);

        Assert.Equal(x, Live(state, 0).X);
        Assert.Equal(y, Live(state, 0).Y);
        Assert.False(Live(state, 0).SplashThreshold);
    }

    /// <summary>
    /// A mine sticks only to the map. A gate kills it.
    /// </summary>
    /// <remarks>
    /// The stick is guarded by <c>place_meeting(x, y, Obstacle)</c>, bracketed
    /// by <c>wallSetSolid()</c> — which is <c>with(Obstacle) solid = true</c> and
    /// nothing else. Everything else a bullet collides with falls through to
    /// <c>if (!place_free(x, y)) instance_destroy()</c>.
    /// </remarks>
    [Fact]
    public void AMineThrownIntoAGateDies()
    {
        SolidRect gate = default;
        var found = false;

        foreach (var rect in World.Rects)
        {
            if (rect.Kind != SolidKind.TeamGate)
                continue;

            gate = rect;
            found = true;
            break;
        }

        Assert.True(found, $"{Map} has no team gate");

        var state = default(WorldState);
        var y = (gate.Top + gate.Bottom) / 2;

        // Started inside the gate, which is where a mine that reached one ends
        // up, and moving so the substep loop cannot leave it.
        state.SpawnProjectile(Mine(gate.Left + 2, y, ownerSlot: 1, Team.Red, direction: 0, speed: 2));

        Projectiles.Move(World, ref state, ref DiscardedEvents.Events);

        Assert.Equal(0, state.LiveProjectileCount);
    }

    /// <summary>
    /// A mine flies straight through people: it has no character collision event
    /// at all.
    /// </summary>
    /// <remarks>
    /// <c>Objects/Projectiles/Mine.events/</c> holds a Sentry collision and a
    /// Generator collision and nothing for characters — unlike the Shot and the
    /// Rocket, both of which stop on one. The only thing a mine does to a
    /// character is explode near them.
    /// </remarks>
    [Fact]
    public void AMineFliesThroughAnEnemy()
    {
        var state = default(WorldState);
        Put(ref state, 2, PlayerClass.Soldier, Team.Blue, OpenX, AirY);

        var mine = Mine(OpenX, AirY, ownerSlot: 1, Team.Red, direction: 0, speed: 2);
        state.SpawnProjectile(mine);

        // Overlapping, so a collision event would certainly have fired.
        Assert.True(mine.Box.Overlaps(Aabb.ForCharacter(
            state.Characters[2].Box, state.Characters[2].X, state.Characters[2].Y)));

        var before = state.Characters[2].Hp;
        Projectiles.Collide(World, ref state, ref DiscardedEvents.Events);

        Assert.Equal(1, state.LiveProjectileCount);
        Assert.Equal(before, state.Characters[2].Hp);
    }

    // --- the blast ------------------------------------------------------------

    /// <summary>
    /// Right-click detonates every mine this demoman owns and resets the count.
    /// </summary>
    [Fact]
    public void RightClickDetonatesEveryMineTheDemomanOwns()
    {
        var state = default(WorldState);
        Put(ref state, 1, PlayerClass.Demoman, Team.Red, OpenX, AirY, PlayerKeys.Special);
        state.Weapons[1].Lobbed = 2;

        // Far apart, so neither is in the other's radius and only the explicit
        // detonation can account for both going off.
        state.SpawnProjectile(Mine(OpenX + 400, AirY, ownerSlot: 1, Team.Red, stuck: true));
        state.SpawnProjectile(Mine(OpenX + 800, AirY, ownerSlot: 1, Team.Red, stuck: true));

        Weapons.SpecialStep(World, ref state, 1, ref TestEvents.Discarded);

        Assert.Equal(0, state.LiveProjectileCount);
        Assert.Equal(0, state.Weapons[1].Lobbed);
    }

    /// <summary>The control: a mine belonging to somebody else is left alone.</summary>
    [Fact]
    public void RightClickLeavesAnotherPlayersMines()
    {
        var state = default(WorldState);
        Put(ref state, 1, PlayerClass.Demoman, Team.Red, OpenX, AirY, PlayerKeys.Special);
        Put(ref state, 2, PlayerClass.Demoman, Team.Red, OpenX + 400, AirY);
        state.Weapons[2].Lobbed = 1;

        state.SpawnProjectile(Mine(OpenX + 400, AirY, ownerSlot: 2, Team.Red, stuck: true));

        Weapons.SpecialStep(World, ref state, 1, ref TestEvents.Discarded);

        Assert.Equal(1, state.LiveProjectileCount);
        Assert.Equal(1, state.Weapons[2].Lobbed);
    }

    /// <summary>
    /// A mine's blast sets off its owner's other mines and any enemy's.
    /// </summary>
    [Fact]
    public void ABlastChainsThroughItsOwnAndEnemyMines()
    {
        var state = default(WorldState);

        var first = state.SpawnProjectile(Mine(OpenX, AirY, ownerSlot: 1, Team.Red, stuck: true));

        // Mine, ten pixels away: chained because it is mine.
        state.SpawnProjectile(Mine(OpenX + 10, AirY, ownerSlot: 1, Team.Red, stuck: true));

        // An enemy's, also ten pixels away: chained because it is an enemy's.
        state.SpawnProjectile(Mine(OpenX - 10, AirY, ownerSlot: 3, Team.Blue, stuck: true));

        Assert.Equal(3, state.LiveProjectileCount);
        Explosions.DetonateMine(World, ref state, first, ref DiscardedEvents.Events);

        Assert.Equal(0, state.LiveProjectileCount);
    }

    /// <summary>
    /// A team-mate's mine is left where it is.
    /// </summary>
    /// <remarks>
    /// <c>(other.team != team or other.ownerPlayer == ownerPlayer)</c> — an
    /// enemy's, or your own, and a team-mate's is neither.
    /// <para>
    /// No enemy mine in this scenario on purpose. A blast <em>reaches</em> a
    /// team-mate's mine through an enemy's — the enemy's chain sees a Red mine
    /// as an enemy mine and detonates it — so a test with all three present
    /// would clear the board and prove nothing about this clause.
    /// </para>
    /// </remarks>
    [Fact]
    public void ABlastLeavesATeamMatesMine()
    {
        var state = default(WorldState);

        var first = state.SpawnProjectile(Mine(OpenX, AirY, ownerSlot: 1, Team.Red, stuck: true));
        state.SpawnProjectile(Mine(OpenX + 10, AirY, ownerSlot: 2, Team.Red, stuck: true));

        Explosions.DetonateMine(World, ref state, first, ref DiscardedEvents.Events);

        Assert.Equal(1, state.LiveProjectileCount);
        Assert.Equal(2, Live(state, 0).OwnerSlot);
    }

    /// <summary>
    /// The blast hurts an enemy, and hurts its own owner at five ninths.
    /// </summary>
    [Fact]
    public void AMineHurtsItsOwnerLessThanAnEnemy()
    {
        var enemy = default(WorldState);
        Put(ref enemy, 2, PlayerClass.Soldier, Team.Blue, OpenX, AirY);
        var enemyHandle = enemy.SpawnProjectile(Mine(OpenX, AirY, ownerSlot: 1, Team.Red, stuck: true));

        var own = default(WorldState);
        Put(ref own, 1, PlayerClass.Soldier, Team.Red, OpenX, AirY);
        var ownHandle = own.SpawnProjectile(Mine(OpenX, AirY, ownerSlot: 1, Team.Red, stuck: true));

        var start = ClassStats.For(PlayerClass.Soldier).MaxHp;

        Explosions.DetonateMine(World, ref enemy, enemyHandle, ref DiscardedEvents.Events);
        Explosions.DetonateMine(World, ref own, ownHandle, ref DiscardedEvents.Events);

        var toEnemy = start - enemy.Characters[2].Hp;
        var toOwner = start - own.Characters[1].Hp;

        Assert.True(toEnemy > 0, "the enemy took nothing");
        Assert.Equal(toEnemy * 5.0 / 9.0, toOwner, 9);

        // 1 for your own mine, 4 for anybody else's — never 2.
        Assert.Equal(1, own.Characters[1].MoveStatus);
        Assert.Equal(4, enemy.Characters[2].MoveStatus);
    }

    /// <summary>
    /// A stuck mine has no quarter-radius cutoff and a flying one does.
    /// </summary>
    /// <remarks>
    /// <c>if (1 - d/blastRadius &lt;= 0.25 <b>and other.splashThreshhold</b>)
    /// continue;</c> — the extra clause is the whole difference, and it means a
    /// landed mine damages everything inside its forty pixels however faintly.
    /// The rocket's blast has no such exemption.
    /// </remarks>
    [Fact]
    public void AStuckMineHasNoCutoffAndAFlyingOneDoes()
    {
        // Far enough out that 1 - d/40 <= 0.25, i.e. d >= 30.
        var flying = default(WorldState);
        Put(ref flying, 2, PlayerClass.Soldier, Team.Blue, OpenX, AirY);
        var flyingMine = MineAtGap(in flying, 2, 32, stuck: false);
        var flyingHandle = flying.SpawnProjectile(flyingMine);

        var stuck = default(WorldState);
        Put(ref stuck, 2, PlayerClass.Soldier, Team.Blue, OpenX, AirY);
        var stuckMine = MineAtGap(in stuck, 2, 32, stuck: true);
        var stuckHandle = stuck.SpawnProjectile(stuckMine);

        var gap = Gap(in flying, 2, in flyingMine);
        Assert.True(gap >= 30 && gap < BlastRadius, $"the setup put the mine {gap} away");

        var start = ClassStats.For(PlayerClass.Soldier).MaxHp;
        Explosions.DetonateMine(World, ref flying, flyingHandle, ref DiscardedEvents.Events);
        Explosions.DetonateMine(World, ref stuck, stuckHandle, ref DiscardedEvents.Events);

        Assert.Equal(start, flying.Characters[2].Hp);
        Assert.True(stuck.Characters[2].Hp < start, "the stuck mine's cutoff still applied");
        Assert.Equal(
            start - (ExplosionDamage * (1 - (gap / BlastRadius))),
            stuck.Characters[2].Hp,
            9);
    }

    /// <summary>
    /// A demoman's mines die with them — silently, without exploding.
    /// </summary>
    /// <remarks>
    /// <c>Minegun.events/Destroy.xml</c>, and the one exception to "a projectile
    /// outlives its owner". A rocket already in the air does not care that its
    /// shooter died; a mine belongs to the weapon and goes with it.
    /// </remarks>
    [Fact]
    public void AMineDiesWithItsOwnerWithoutExploding()
    {
        var state = default(WorldState);
        Put(ref state, 1, PlayerClass.Demoman, Team.Red, OpenX, AirY);
        Put(ref state, 2, PlayerClass.Soldier, Team.Blue, OpenX, AirY);
        state.Weapons[1].Lobbed = 1;
        state.SpawnProjectile(Mine(OpenX, AirY, ownerSlot: 1, Team.Red, stuck: true));

        var before = state.Characters[2].Hp;
        state.Clear(1);

        Assert.Equal(0, state.LiveProjectileCount);
        Assert.Equal(before, state.Characters[2].Hp);
    }

    /// <summary>
    /// Destroying a mine gives the demoman their slot back, whatever killed it.
    /// </summary>
    /// <remarks>
    /// <c>Mine.events/Destroy.xml</c> is one line and runs on every path.
    /// Without it a demoman who threw eight mines into a gate would never fire
    /// again.
    /// </remarks>
    [Fact]
    public void DestroyingAMineGivesBackItsSlot()
    {
        var state = default(WorldState);
        Put(ref state, 1, PlayerClass.Demoman, Team.Red, OpenX, AirY);
        state.Weapons[1].Lobbed = 3;

        var handle = state.SpawnProjectile(Mine(OpenX, AirY, ownerSlot: 1, Team.Red, stuck: true));
        state.FreeProjectile(handle);

        Assert.Equal(2, state.Weapons[1].Lobbed);
    }

    // --- the ordering ---------------------------------------------------------

    /// <summary>
    /// Detonating in the middle of Begin Step is not the same tick's physics
    /// as detonating after it.
    /// </summary>
    /// <remarks>
    /// <b>This is the mine jump.</b> The reference runs the right-click at
    /// <c>Character.events/Begin Step.xml:120-126</c>, between the jump and the
    /// <c>switch(moveStatus)</c> at 143 that picks this tick's control and
    /// friction factors — and a mine going off under its owner sets
    /// <c>moveStatus = 1</c>. So the blast changes the same tick it happens on.
    /// <para>
    /// The two orderings are run side by side here rather than asserting a
    /// number, because the claim <em>is</em> that they differ:
    /// <see cref="MatchSimulation"/> splits
    /// <see cref="CharacterPhysics.BeginStepInput"/> from
    /// <see cref="CharacterPhysics.BeginStepAccelerate"/> for no other reason,
    /// and if this ever stops failing the split has become pointless.
    /// </para>
    /// </remarks>
    [Fact]
    public void DetonatingBeforeTheFrictionSwitchChangesTheSameTick()
    {
        static WorldState Setup()
        {
            var state = default(WorldState);
            Put(ref state, 1, PlayerClass.Demoman, Team.Red, OpenX, AirY, PlayerKeys.Special | PlayerKeys.Left);
            state.Weapons[1].Lobbed = 1;

            // Directly under the demoman, close enough to throw them.
            state.SpawnProjectile(Mine(OpenX, AirY + 12, ownerSlot: 1, Team.Red, stuck: true));
            return state;
        }

        // The reference's order.
        var correct = Setup();
        Weapons.FireStep(World, ref correct, 1, ref Unsafe());
        CharacterPhysics.BeginStepInput(World, ref correct.Characters[1]);
        Weapons.SpecialStep(World, ref correct, 1, ref TestEvents.Discarded);
        CharacterPhysics.BeginStepAccelerate(World, ref correct.Characters[1]);

        // Detonating after the whole of Begin Step instead.
        var late = Setup();
        Weapons.FireStep(World, ref late, 1, ref Unsafe());
        CharacterPhysics.BeginStepInput(World, ref late.Characters[1]);
        CharacterPhysics.BeginStepAccelerate(World, ref late.Characters[1]);
        Weapons.SpecialStep(World, ref late, 1, ref TestEvents.Discarded);

        // Both were thrown identically — the blast itself does not depend on the
        // order.
        Assert.Equal(1, correct.Characters[1].MoveStatus);
        Assert.Equal(1, late.Characters[1].MoveStatus);

        // But only one of them was accelerated under a mine-jump's friction.
        Assert.NotEqual(late.Characters[1].HSpeed, correct.Characters[1].HSpeed);
    }

    /// <summary>
    /// ...and <see cref="MatchSimulation"/> really does run it in that order.
    /// </summary>
    /// <remarks>
    /// The test above proves the two orderings differ; this one proves the tick
    /// picks the right one. Both are needed — moving the detonation to after
    /// <see cref="CharacterPhysics.BeginStepAccelerate"/> in
    /// <c>MatchSimulation</c> leaves the test above passing, because it calls
    /// the halves itself.
    /// <para>
    /// <c>HSpeed</c> is the witness because <see cref="CharacterPhysics.Step"/>
    /// leaves it alone for a character in open air — it clamps, halves the
    /// gravity and moves, and only a wall changes the horizontal.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheTickDetonatesInTheMiddleOfBeginStep()
    {
        var early = MineJumpScenario();
        CharacterPhysics.BeginStepInput(World, ref early.Characters[1]);
        Weapons.SpecialStep(World, ref early, 1, ref TestEvents.Discarded);
        CharacterPhysics.BeginStepAccelerate(World, ref early.Characters[1]);

        var late = MineJumpScenario();
        CharacterPhysics.BeginStepInput(World, ref late.Characters[1]);
        CharacterPhysics.BeginStepAccelerate(World, ref late.Characters[1]);
        Weapons.SpecialStep(World, ref late, 1, ref TestEvents.Discarded);

        // The premise: the orderings are distinguishable at all.
        Assert.NotEqual(late.Characters[1].HSpeed, early.Characters[1].HSpeed);

        var ticked = MineJumpScenario();
        MatchSimulation.Tick(World, ref ticked, []);

        Assert.Equal(early.Characters[1].HSpeed, ticked.Characters[1].HSpeed);
    }

    /// <summary>A demoman in the air, holding left, above one of their own mines.</summary>
    private static WorldState MineJumpScenario()
    {
        var state = default(WorldState);
        Put(ref state, 1, PlayerClass.Demoman, Team.Red, OpenX, AirY, PlayerKeys.Special | PlayerKeys.Left);
        state.Weapons[1].Lobbed = 1;
        state.SpawnProjectile(Mine(OpenX, AirY + 12, ownerSlot: 1, Team.Red, stuck: true));

        return state;
    }

    private static TickEvents _events;

    private static ref TickEvents Unsafe()
    {
        _events = default;
        return ref _events;
    }

    // --- helpers --------------------------------------------------------------

    private static double Gap(in WorldState state, int slot, in ProjectileState mine)
    {
        ref readonly var victim = ref state.Characters[slot];
        return Aabb.ForCharacter(victim.Box, victim.X, victim.Y, victim.Facing).Distance(mine.Box);
    }

    /// <summary>A mine to the victim's left, at the first offset opening a gap.</summary>
    private static ProjectileState MineAtGap(in WorldState state, int victimSlot, double targetGap, bool stuck)
    {
        ref readonly var victim = ref state.Characters[victimSlot];

        for (var offset = 0; offset < 200; offset++)
        {
            var candidate = Mine(victim.X - offset, victim.Y, ownerSlot: 1, Team.Red, stuck: stuck);

            if (Gap(in state, victimSlot, in candidate) >= targetGap)
                return candidate;
        }

        Assert.Fail($"no offset opens a gap of {targetGap}");
        return default;
    }
}
