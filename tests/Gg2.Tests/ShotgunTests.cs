using System.Linq;
using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// The engineer's shotgun: phase 5 stage 9.
/// </summary>
/// <remarks>
/// <c>Objects/Weapons/Shotgun.events/{Create, User Event 1, User Event 3}.xml</c>.
/// The event is the Scattergun's with four numbers changed, and these tests
/// exist to pin those four — a shared body is the right transcription only for
/// as long as nobody quietly makes the two weapons agree.
/// <para>
/// Unlike the Flamethrower, the Shotgun <b>broadcasts its seed</b>, so its blast
/// is contract: every client re-derives the same five pellets, and the count of
/// random draws matters as much as their use.
/// </para>
/// </remarks>
public sealed class ShotgunTests
{
    private const string Map = "ctf_truefort";

    private const double OpenX = 2600;

    private const double OpenY = 708;

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
        PlayerKeys keys = PlayerKeys.Attack,
        double aimDegrees = 0)
    {
        var character = CharacterState.Fresh(playerClass, team);
        character.X = x;
        character.Y = y;
        character.NetAimDirection = (ushort)Math.Round(aimDegrees * 65536.0 / 360.0);
        character.KeyState = keys;

        state.Characters[slot] = character;
        state.Weapons[slot] = WeaponState.Spawn(playerClass, x, y);
        state.Weapons[slot].ReadyToShoot = true;
        state.Weapons[slot].RefireTimer = WeaponState.Inactive;
        state.Alive[slot] = true;
    }

    private static ProjectileState[] Pellets(in WorldState state)
    {
        var pellets = new ProjectileState[state.LiveProjectileCount];
        for (var i = 0; i < pellets.Length; i++)
            pellets[i] = state.Projectiles[state.LiveProjectileSlot(i)];
        return pellets;
    }

    /// <summary>Five pellets at seven damage, and one round gone.</summary>
    [Fact]
    public void ABlastIsFivePelletsOfSevenAndOneRound()
    {
        var state = default(WorldState);
        var events = default(TickEvents);
        Put(ref state, 1, PlayerClass.Engineer, Team.Red, OpenX, OpenY);

        Weapons.FireStep(World, ref state, 1, ref events);

        var pellets = Pellets(state);
        Assert.Equal(5, pellets.Length);

        foreach (var pellet in pellets)
        {
            Assert.Equal(ProjectileKind.Shot, pellet.Kind);
            Assert.Equal(DamageSource.Shotgun, pellet.Source);
            Assert.Equal(7, pellet.HitDamage);
        }

        Assert.Equal(5, state.Weapons[1].AmmoCount);
        Assert.False(state.Weapons[1].ReadyToShoot);
        Assert.Equal(20, state.Weapons[1].RefireTimer);
        Assert.Equal(35, state.Weapons[1].ReloadTimer);
        Assert.True(events.Fired[1]);
    }

    /// <summary>
    /// A shotgun pellet keeps the <c>Shot</c>'s own lifetime of 40 — there is no
    /// range compensation.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The single most deletable difference between the two weapons.</b>
    /// The Scattergun ends every pellet with
    /// <c>alarm[0] = 35 * ((min(1, forward/actual) - 1)/2 + 1)</c>, which
    /// shortens the fuse when the shooter's own speed is helping the pellet
    /// along. The Shotgun writes nothing, so a running engineer's pellets
    /// genuinely out-range a standing one's and both out-range a scout's.
    /// </remarks>
    [Fact]
    public void AShotgunPelletHasNoRangeCompensation()
    {
        var running = default(WorldState);
        var events = default(TickEvents);
        Put(ref running, 1, PlayerClass.Engineer, Team.Red, OpenX, OpenY);
        running.Characters[1].HSpeed = 6;

        Weapons.FireStep(World, ref running, 1, ref events);

        foreach (var pellet in Pellets(running))
            Assert.Equal(40, pellet.Lifetime);

        // The scout firing from the same state does shorten its fuse, so the
        // assertion above is about the Shotgun and not about the default.
        var scout = default(WorldState);
        Put(ref scout, 1, PlayerClass.Scout, Team.Red, OpenX, OpenY);
        scout.Characters[1].HSpeed = 6;

        Weapons.FireStep(World, ref scout, 1, ref events);

        foreach (var pellet in Pellets(scout))
            Assert.True(pellet.Lifetime < 40, $"scattergun pellet lived {pellet.Lifetime}");
    }

    /// <summary>
    /// Five pellets cost fifteen draws, in create-speed-direction order, over an
    /// eleven-degree cone.
    /// </summary>
    /// <remarks>
    /// Rebuilt from a bare <see cref="GmlRandom"/> rather than compared against a
    /// recorded list, so the assertion is about the order and the widths and not
    /// about magic angles. The count is contract: every client re-derives the
    /// blast from the seed on the wire, so a sixth pellet or a skipped
    /// <c>choose</c> would desync all of them.
    /// </remarks>
    [Fact]
    public void EachPelletCostsThreeDrawsAndTheConeIsElevenDegrees()
    {
        const ushort Seed = 4321;

        var state = default(WorldState);
        var events = default(TickEvents);
        Put(ref state, 1, PlayerClass.Engineer, Team.Red, OpenX, OpenY);
        events.ForceFireSeed(1, Seed);

        Weapons.FireStep(World, ref state, 1, ref events);

        var random = new GmlRandom(Seed);
        var pellets = Pellets(state);
        Assert.Equal(5, pellets.Length);

        foreach (var pellet in pellets)
        {
            var perseverant = random.ChooseIndex(3) == 2;
            var speedJitter = random.Next(4) - 2;
            var directionJitter = random.Next(11) - 5.5;

            var expected = default(Motion);
            expected.Direction = 0;
            expected.Speed = 13;
            expected.HSpeed += state.Characters[1].HSpeed;
            expected.Speed += speedJitter;
            expected.Direction += directionJitter;

            Assert.Equal(perseverant, pellet.Perseverant);
            Assert.Equal(expected.Direction, pellet.Motion.Direction);
            Assert.Equal(expected.Speed, pellet.Motion.Speed);
        }

        // The loop above *is* the draw-count assertion: it reconstructs the
        // stream position by position, so a fourth draw per pellet or a skipped
        // `choose` would shift every later pellet and fail here. There is
        // deliberately no separate count — advancing a second copy of the stream
        // by the number of draws the test itself made and finding they agree is
        // a tautology, not a check.
    }

    /// <summary>
    /// The shotgun's cone is tighter than the scattergun's, from the same seed.
    /// </summary>
    /// <remarks>
    /// The direct control on the one shared constant: <c>random(11) - 5.5</c>
    /// against <c>random(15) - 7.5</c>. Driving both from the same seed makes the
    /// comparison exact rather than statistical — every shotgun pellet's
    /// deviation is 11/15 of the scattergun pellet drawn at the same position in
    /// the stream.
    /// </remarks>
    [Fact]
    public void TheShotgunsConeIsTighterThanTheScattergunsOnTheSameSeed()
    {
        const ushort Seed = 909;

        var engineer = Deviations(PlayerClass.Engineer, Seed);
        var scout = Deviations(PlayerClass.Scout, Seed);

        // Five against six, so compare the pellets that share a stream position.
        for (var i = 0; i < engineer.Length; i++)
            Assert.Equal(scout[i] * 11.0 / 15.0, engineer[i], 12);

        static double[] Deviations(PlayerClass playerClass, ushort seed)
        {
            var state = default(WorldState);
            var events = default(TickEvents);
            Put(ref state, 1, playerClass, Team.Red, OpenX, OpenY);
            events.ForceFireSeed(1, seed);

            Weapons.FireStep(World, ref state, 1, ref events);

            return [.. Pellets(state).Select(p => ((p.Motion.Direction + 180) % 360) - 180)];
        }
    }

    /// <summary>A shotgun pellet deals seven and dies on its victim.</summary>
    [Fact]
    public void AShotgunPelletDealsSeven()
    {
        var state = default(WorldState);
        Put(ref state, 1, PlayerClass.Engineer, Team.Red, OpenX, OpenY, keys: PlayerKeys.None);
        Put(ref state, 2, PlayerClass.Soldier, Team.Blue, OpenX + 40, OpenY, keys: PlayerKeys.None);

        var pellet = new ProjectileState
        {
            Kind = ProjectileKind.Shot,
            X = OpenX + 40,
            Y = OpenY,
            OwnerSlot = 1,
            Team = Team.Red,
            Source = DamageSource.Shotgun,
            HitDamage = 7,
            Lifetime = 40,
        };
        pellet.Motion.Direction = 0;
        pellet.ImageAngle = 0;
        pellet.Motion.Speed = 13;

        state.SpawnProjectile(pellet);

        Projectiles.Collide(World, ref state, ref DiscardedEvents.Events);

        Assert.Equal(0, state.LiveProjectileCount);
        Assert.Equal(153, state.Characters[2].Hp);
        Assert.Equal(DamageSource.Shotgun, state.Characters[2].LastDamageSource);
    }
}
