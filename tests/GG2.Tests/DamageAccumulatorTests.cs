using GG2.Maps;
using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Tests;

/// <summary>
/// <see cref="TickEvents.DamageDealt"/> and <see cref="TickEvents.DamageTaken"/>
/// — the dense signal a training reward needs first.
/// </summary>
/// <remarks>
/// <b>The rest of <see cref="TickEvents"/> carries the sparse events</b> — kills,
/// deaths, spawns, captures — with attribution. A learned policy wants damage per
/// tick, and <c>docs/BOTAI.md</c> item 7 forbids reconstructing it by diffing hp:
/// healing, spawn invulnerability, a cabinet and a respawn all move hp without
/// anybody dealing damage.
/// <para>
/// <b>The accumulation is on <c>DamageApi.Deal</c> and its parameters are
/// required.</b> An overload without them would let a new damage site accumulate
/// nothing and nobody notice — a reward signal that is quietly incomplete is far
/// harder to see than one that is obviously broken. Making the compiler ask "who
/// dealt this" at all eleven sites is the design.
/// </para>
/// </remarks>
public sealed class DamageAccumulatorTests
{
    private const string Map = "gg_debug";
    private const double OpenX = 2000;
    private const double OpenY = 1000;

    private static CollisionWorld? _shared;

    private static CollisionWorld World => _shared ??=
        new CollisionWorld(LevelMap.FromPng(Map, ReferenceTree.ReadMap(Map)));

    private static void Put(ref WorldState state, int slot, Team team, double x, double y)
    {
        var character = CharacterState.Fresh(PlayerClass.Soldier, team);
        character.X = x;
        character.Y = y;

        state.Characters[slot] = character;
        state.Weapons[slot] = WeaponState.Spawn(PlayerClass.Soldier, x, y);
        state.Alive[slot] = true;

        state.AddPlayer(slot);
        state.Players[slot].Team = team;
        state.Players[slot].Class = PlayerClass.Soldier;
    }

    private static ProjectileHandle Rocket(
        ref WorldState state, double x, double y, double direction, int ownerSlot, Team team)
    {
        var rocket = new ProjectileState
        {
            Kind = ProjectileKind.Rocket,
            X = x,
            Y = y,
            OwnerSlot = (byte)ownerSlot,
            Team = team,
            Source = DamageSource.Rocketlauncher,
            HitDamage = 25,
            Knockback = 8,
            DistanceToTravel = 800,
            KnockbackCapTimer = 20,
            KnockbackEndTimer = 30,
            LastKnownX = x,
            LastKnownY = y,
        };

        rocket.Motion.Direction = direction;
        rocket.ImageAngle = direction;
        rocket.Motion.Speed = 13;

        return state.SpawnProjectile(rocket);
    }

    /// <summary>
    /// A rocket hitting an enemy charges the victim and credits the shooter, and
    /// the amount is the hp that actually left.
    /// </summary>
    [Fact]
    public void AHitCreditsTheShooterAndChargesTheVictim()
    {
        var state = default(WorldState);
        var events = default(TickEvents);

        Put(ref state, 1, Team.Red, OpenX, OpenY);
        Put(ref state, 2, Team.Blue, OpenX + 60, OpenY);

        Rocket(ref state, OpenX, OpenY, direction: 0, ownerSlot: 1, Team.Red);

        var before = state.Characters[2].Hp;
        var shooterHp = state.Characters[1].Hp;

        for (var tick = 0; tick < 10 && state.LiveProjectileCount > 0; tick++)
        {
            Projectiles.Move(World, ref state, ref events);
            Projectiles.Collide(World, ref state, ref events);
        }

        Assert.Equal(0, state.LiveProjectileCount);

        // The direct hit and the blast both land, and both are counted.
        Assert.True(events.DamageDealt[1] > 0, "the shooter was credited nothing");
        Assert.Equal(events.DamageDealt[1], events.DamageTaken[2], 9);
        Assert.Equal(before - state.Characters[2].Hp, events.DamageTaken[2], 9);

        // The shooter is 60 px away, so their own blast catches them too —
        // and this is the sharpest form of the self-damage rule: ONE explosion
        // deals to the enemy and self-damages the shooter, and only the first
        // half reaches DamageDealt. A reward that summed the blast would pay a
        // soldier for hurting himself.
        Assert.True(events.DamageTaken[1] > 0, "the shooter escaped their own blast");
        Assert.Equal(
            shooterHp - state.Characters[1].Hp, events.DamageTaken[1], 9);

        Assert.Equal(0, events.DamageDealt[2]);
    }

    /// <summary>
    /// <b>Self-damage is taken and never dealt.</b>
    /// </summary>
    /// <remarks>
    /// A rocket jump must not score positive reward. The
    /// blast reaches its own shooter — that is the whole mechanic — so the
    /// victim's counter has to move while the dealer's does not, even though the
    /// two are the same slot.
    /// </remarks>
    [Fact]
    public void ARocketJumpIsTakenAndNeverDealt()
    {
        var state = default(WorldState);
        var events = default(TickEvents);

        Put(ref state, 1, Team.Red, OpenX, OpenY);

        // Detonated on the spot, so the only character in range is the shooter.
        var handle = Rocket(ref state, OpenX, OpenY, direction: 90, ownerSlot: 1, Team.Red);

        var before = state.Characters[1].Hp;
        Explosions.Explode(World, ref state, handle, CharacterState.Nobody, ref events);

        Assert.True(
            state.Characters[1].Hp < before,
            "the shooter was not hurt, so this test is not measuring what it claims");

        Assert.Equal(before - state.Characters[1].Hp, events.DamageTaken[1], 9);
        Assert.Equal(0, events.DamageDealt[1]);
    }

    /// <summary>
    /// Afterburn credits the pyro who lit the fire, not whoever last hit the
    /// victim — and credits nobody once that pyro has left.
    /// </summary>
    /// <remarks>
    /// <b>The dealer slot follows <c>LastDamageDealer</c> semantics</b>, so
    /// this is the same rule that lets a rocket already in the air credit a
    /// shooter who has since died. <see cref="CharacterState.Nobody"/> is 255
    /// and never 0 — a defaulted zero would quietly credit the server's own
    /// reserved player.
    /// </remarks>
    [Fact]
    public void AfterburnCreditsTheLighterAndNobodyWhenThereIsNone()
    {
        var state = default(WorldState);
        var events = default(TickEvents);

        Put(ref state, 1, Team.Red, OpenX, OpenY);
        Put(ref state, 2, Team.Blue, OpenX + 200, OpenY);

        state.Characters[2].BurnDuration = 90;
        state.Characters[2].BurnIntensity = 30;
        state.Characters[2].BurnedBy = 1;

        Afterburn.BeginStep(ref state, 2, ref events);

        Assert.True(events.DamageTaken[2] > 0, "the burn dealt nothing");
        Assert.Equal(events.DamageTaken[2], events.DamageDealt[1], 9);

        // The lighter leaves: the burn still hurts, and credits no one.
        var orphan = default(TickEvents);
        state.Characters[2].BurnedBy = CharacterState.Nobody;

        Afterburn.BeginStep(ref state, 2, ref orphan);

        Assert.True(orphan.DamageTaken[2] > 0, "the orphaned burn dealt nothing");

        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
            Assert.Equal(0, orphan.DamageDealt[slot]);
    }

    /// <summary>
    /// Both buffers are outputs, so <see cref="TickEvents.ClearOutputs"/> zeroes
    /// them — otherwise every tick's reward would include every previous tick's.
    /// </summary>
    [Fact]
    public void ClearOutputsZeroesBothBuffers()
    {
        var events = default(TickEvents);

        events.DamageDealt[3] = 12.5;
        events.DamageTaken[4] = 7.25;

        events.ClearOutputs();

        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
        {
            Assert.Equal(0, events.DamageDealt[slot]);
            Assert.Equal(0, events.DamageTaken[slot]);
        }
    }
}
