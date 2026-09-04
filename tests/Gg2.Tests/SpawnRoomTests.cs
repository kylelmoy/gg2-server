using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// The spawn room and the healing cabinet — the two map objects phase 6 left
/// unmodelled.
/// </summary>
/// <remarks>
/// <c>Objects/Map elements/Spawnroom objects/</c>, and the three call sites that
/// ask about them: <c>Character.events/Destroy.xml:37</c> (the fast respawn),
/// <c>processClientCommands.gml:84</c> (the silent class swap) and
/// <c>serverBuildSentry.gml</c> (the build refusal). Plus
/// <c>Character.events/Collision with HealingCabinet.xml</c>.
/// <para>
/// <b>No recording can gate any of it.</b> A spawn room and a cabinet are
/// invisible on the wire — there is no message for either, and the effects they
/// have (a respawn timer, an absent death, a full hp bar) are indistinguishable
/// in a state update from the same thing happening for another reason.
/// </para>
/// </remarks>
public sealed class SpawnRoomTests
{
    private const string Map = "ctf_truefort";

    private static LevelMap Level() =>
        LevelMap.FromPng(Map, ReferenceTree.ReadMap(Map)).ForArea(1);

    private static CollisionWorld? _sharedWorld;

    private static CollisionWorld World => _sharedWorld ??= new CollisionWorld(Level());

    /// <summary>A red character standing where the map says red spawns.</summary>
    private static (double X, double Y) RedSpawn()
    {
        var spawns = new SpawnPoints(Level());
        Assert.True(spawns.TryGet(Team.Red, 0, 0, out var x, out var y));
        return (x, y);
    }

    private static void Put(
        ref WorldState state, int slot, double x, double y,
        PlayerClass playerClass = PlayerClass.Scout, Team team = Team.Red)
    {
        var character = CharacterState.Fresh(playerClass, team);
        character.X = x;
        character.Y = y;
        character.Facing = 1;

        state.Characters[slot] = character;
        state.Weapons[slot] = WeaponState.Spawn(playerClass, x, y);
        state.Alive[slot] = true;
        state.AddPlayer(slot);
        state.Players[slot].Team = team;
        state.Players[slot].Class = playerClass;
    }

    // --- the map reads the objects at all -------------------------------------

    /// <summary>
    /// <c>ctf_truefort</c> places four spawn rooms and four cabinets, and the
    /// map parser has always known the entity names.
    /// </summary>
    [Fact]
    public void TheMapPlacesSpawnRoomsAndCabinets()
    {
        Assert.Equal(4, World.SpawnRooms.Count);
        Assert.Equal(4, World.Cabinets.Count);

        // The defaults from HealingCabinet.events/Create.xml, since no shipped
        // map overrides them.
        foreach (var cabinet in World.Cabinets)
        {
            Assert.True(cabinet.Heal);
            Assert.True(cabinet.Refill);
            Assert.False(cabinet.Uber);
        }
    }

    /// <summary>
    /// <b>Every spawn point on every map is inside a spawn room</b>, which is
    /// what makes the two rules below the common case rather than a corner.
    /// </summary>
    [Fact]
    public void EverySpawnPointIsInsideASpawnRoom()
    {
        foreach (var name in new[] { "ctf_truefort", "koth_valley", "gg_debug", "cp_dirtbowl" })
        {
            var map = LevelMap.FromPng(name, ReferenceTree.ReadMap(name)).ForArea(1);
            var world = new CollisionWorld(map);
            var spawns = new SpawnPoints(map);

            Assert.True(world.SpawnRooms.Count > 0, $"{name} places no spawn rooms");

            foreach (var team in new[] { Team.Red, Team.Blue })
            {
                Assert.True(spawns.TryGet(team, 0, 0, out var x, out var y),
                    $"{name} has no {team} spawn point");
                Assert.True(world.PointInSpawnRoom(x, y),
                    $"{name}'s {team} spawn ({x},{y}) is not inside a spawn room");
            }
        }
    }

    /// <summary>
    /// The two collider sprites are the sizes the reference declares: 42x42 for
    /// the room and 32x48 for the cabinet.
    /// </summary>
    /// <remarks>
    /// Asserted on the rectangle rather than through a character, because a
    /// character standing in the middle of either is inside whatever size the
    /// port used — which is how a wrong height survived its first mutation run.
    /// Inclusive bounds, so a 48-tall sprite spans 47.
    /// </remarks>
    [Fact]
    public void TheColliderSpritesAreTheDeclaredSizes()
    {
        var cabinet = Cabinets.Of(new LevelEntity
        {
            Type = EntityTypes.MedCabinet,
            X = 100,
            Y = 100,
            Properties = new Dictionary<string, GgonValue>(),
        });

        Assert.Equal(31, cabinet.Right - cabinet.Left);
        Assert.Equal(47, cabinet.Bottom - cabinet.Top);

        // Built rather than taken from the map: every shipped spawn room is
        // scaled, so none of them pins the sprite's own size.
        var room = SpawnRooms.Of(new LevelEntity
        {
            Type = EntityTypes.SpawnRoom,
            X = 100,
            Y = 100,
            Properties = new Dictionary<string, GgonValue>(),
        });

        Assert.Equal(41, room.Right - room.Left);
        Assert.Equal(41, room.Bottom - room.Top);
    }

    // --- the two guards that disagree -----------------------------------------

    /// <summary>
    /// <b>The reference asks about the same object in two ways.</b> A class
    /// change and a sentry build use <c>collision_point</c> — the origin, at the
    /// character's feet — and the fast respawn uses <c>place_meeting</c>, the
    /// whole box. So there is a band above every room where the two disagree.
    /// </summary>
    /// <remarks>
    /// The control that the port did not quietly use one predicate for both. A
    /// character's box reaches about thirty pixels above its origin, so a
    /// position that far above a room's top edge is outside it by the point and
    /// inside it by the box.
    /// </remarks>
    [Fact]
    public void ThePointAndTheBoxDisagreeAboveARoom()
    {
        var room = World.SpawnRooms[0];
        var box = CharacterState.Fresh(PlayerClass.Scout, Team.Red).Box;

        var x = (room.Left + room.Right) / 2.0;

        // Feet a few pixels above the room's top edge: the origin is outside and
        // the box still hangs into it.
        var y = room.Top - 3.0;

        Assert.False(World.PointInSpawnRoom(x, y), "the origin should be above the room");
        Assert.True(World.MeetsSpawnRoom(box, x, y), "the box should still overlap the room");

        // And well above it, neither.
        Assert.False(World.MeetsSpawnRoom(box, x, room.Top - 200));
    }

    // --- the fast respawn -----------------------------------------------------

    /// <summary>
    /// <b>Dying inside a spawn room arms <c>alarm[5] = 1</c></b>: the
    /// replacement is out on the next tick rather than after the respawn time.
    /// </summary>
    [Fact]
    public void DyingInASpawnRoomRespawnsNextTick()
    {
        var (x, y) = RedSpawn();

        var state = default(WorldState);
        state.RespawnTime = 150;
        Put(ref state, 1, x, y);
        state.Characters[1].Hp = 0;

        var events = default(TickEvents);
        Deaths.Kill(ref state, 1, ref events, World);

        Assert.Equal(1, state.Players[1].RespawnTimer);
    }

    /// <summary>And dying outside one serves the whole respawn time.</summary>
    /// <remarks>
    /// The control: without it the test above passes for a port that always
    /// arms 1.
    /// </remarks>
    [Fact]
    public void DyingOutsideASpawnRoomServesTheRespawnTime()
    {
        var state = default(WorldState);
        state.RespawnTime = 150;
        Put(ref state, 1, 2280, 200);
        state.Characters[1].Hp = 0;

        Assert.False(World.MeetsSpawnRoom(state.Characters[1].Box, 2280, 200),
            "the control position is inside a spawn room");

        var events = default(TickEvents);
        Deaths.Kill(ref state, 1, ref events, World);

        Assert.Equal(150, state.Players[1].RespawnTimer);
    }

    /// <summary>
    /// A death still <em>counts</em> in a spawn room — it is only the timer that
    /// changes, not the message or the scoring.
    /// </summary>
    [Fact]
    public void ASpawnRoomDeathIsStillADeath()
    {
        var (x, y) = RedSpawn();

        var state = default(WorldState);
        state.RespawnTime = 150;
        Put(ref state, 1, x, y);
        state.Characters[1].Hp = 0;

        var events = default(TickEvents);
        Deaths.Kill(ref state, 1, ref events, World);

        Assert.True(events.Died[1]);
        Assert.Equal(1, state.Players[1].Stats[(int)StatIndex.Deaths]);
    }

    // --- the silent class swap ------------------------------------------------

    /// <summary>
    /// <b>A class change inside a spawn room broadcasts nothing at all</b> —
    /// <c>processClientCommands.gml:84</c>'s <c>else</c> arm is a bare
    /// <c>instance_destroy()</c>.
    /// </summary>
    [Fact]
    public void AClassChangeInASpawnRoomIsSilent()
    {
        var (x, y) = RedSpawn();

        var state = default(WorldState);
        state.RespawnTime = 150;
        Put(ref state, 1, x, y);

        var death = PlayerCommands.ChangeClass(ref state, 1, PlayerClass.Pyro, World);

        Assert.Null(death);
        Assert.False(state.IsAlive(1));
        Assert.Equal(PlayerClass.Pyro, state.Players[1].Class);

        // And the Destroy event ran, so the replacement is one tick away.
        Assert.Equal(1, state.Players[1].RespawnTimer);
    }

    /// <summary>
    /// <b>In the band where the two guards disagree, a class change kills
    /// you <em>and</em> brings you straight back.</b>
    /// </summary>
    /// <remarks>
    /// The control that the swap really uses <c>collision_point</c> and the
    /// respawn really uses <c>place_meeting</c>: with one predicate for both,
    /// this position gives either a silent swap with a fast respawn or a death
    /// with a slow one, and never the pair below.
    /// </remarks>
    [Fact]
    public void JustAboveARoomAClassChangeKillsAndStillRespawnsAtOnce()
    {
        var room = World.SpawnRooms[0];
        var x = (room.Left + room.Right) / 2.0;
        var y = room.Top - 3.0;

        var state = default(WorldState);
        state.RespawnTime = 150;
        Put(ref state, 1, x, y);

        // The premise, both halves.
        Assert.False(World.PointInSpawnRoom(x, y));
        Assert.True(World.MeetsSpawnRoom(state.Characters[1].Box, x, y));

        var death = PlayerCommands.ChangeClass(ref state, 1, PlayerClass.Pyro, World);

        Assert.NotNull(death);
        Assert.Equal(1, state.Players[1].RespawnTimer);
    }

    /// <summary>Outside one it is a <c>BID_FAREWELL</c> death, as before.</summary>
    [Fact]
    public void AClassChangeOutsideASpawnRoomStillKills()
    {
        var state = default(WorldState);
        state.RespawnTime = 150;
        Put(ref state, 1, 2280, 200);

        var death = PlayerCommands.ChangeClass(ref state, 1, PlayerClass.Pyro, World);

        Assert.NotNull(death);
        Assert.Equal(DamageSource.BidFarewell, death.Value.Source);
        Assert.Equal(150, state.Players[1].RespawnTimer);
    }

    // --- the sentry build guard -----------------------------------------------

    /// <summary>An engineer cannot build inside a spawn room.</summary>
    [Fact]
    public void AnEngineerCannotBuildInASpawnRoom()
    {
        var (x, y) = RedSpawn();

        var state = default(WorldState);
        Put(ref state, 1, x, y, PlayerClass.Engineer);
        state.Characters[1].NutsNBolts = 100;

        Assert.False(Sentries.CanBuild(state, 1, World));

        // The control: the same engineer outside one can.
        var outside = default(WorldState);
        Put(ref outside, 1, 2280, 200, PlayerClass.Engineer);
        outside.Characters[1].NutsNBolts = 100;

        Assert.True(Sentries.CanBuild(outside, 1, World));
    }

    /// <summary>Nor on a cabinet.</summary>
    [Fact]
    public void AnEngineerCannotBuildOnACabinet()
    {
        var state = default(WorldState);
        Put(ref state, 1, 2280, 200, PlayerClass.Engineer);
        state.Characters[1].NutsNBolts = 100;

        Assert.True(Sentries.CanBuild(state, 1, World));

        state.Characters[1].OnCabinet = true;

        Assert.False(Sentries.CanBuild(state, 1, World));
    }

    // --- the cabinet ----------------------------------------------------------

    /// <summary>Standing on a cabinet fills the bar, and the nuts and bolts.</summary>
    [Fact]
    public void ACabinetFillsHealthAndNutsAndBolts()
    {
        var cabinet = World.Cabinets[0];

        var state = default(WorldState);
        Put(ref state, 1, (cabinet.Left + cabinet.Right) / 2.0, (cabinet.Top + cabinet.Bottom) / 2.0,
            PlayerClass.Engineer);
        state.Characters[1].Hp = 10;
        state.Characters[1].NutsNBolts = 0;
        state.Weapons[1].AmmoCount = 1;

        var events = default(TickEvents);
        Cabinets.CollisionStep(World, ref state, ref events);

        Assert.Equal(ClassStats.For(PlayerClass.Engineer).MaxHp, state.Characters[1].Hp);
        Assert.Equal(CharacterState.MaxNutsNBolts, state.Characters[1].NutsNBolts);
        Assert.Equal(WeaponStats.For(WeaponKind.Shotgun).MaxAmmo, state.Weapons[1].AmmoCount);
    }

    /// <summary>
    /// A cabinet with <c>refill:false</c> heals and does not touch the
    /// magazine.
    /// </summary>
    [Fact]
    public void ARefillSwitchCanBeTurnedOff()
    {
        var cabinet = World.Cabinets[0];
        var x = (cabinet.Left + cabinet.Right) / 2.0;
        var y = (cabinet.Top + cabinet.Bottom) / 2.0;

        var state = default(WorldState);
        Put(ref state, 1, x, y);
        state.Characters[1].Hp = 10;
        state.Weapons[1].AmmoCount = 1;

        var noRefill = Cabinets.Of(new LevelEntity
        {
            Type = EntityTypes.MedCabinet,
            X = cabinet.Left,
            Y = cabinet.Top,
            Properties = new Dictionary<string, GgonValue>
            {
                ["refill"] = new GgonString("false"),
            },
        });

        var events = default(TickEvents);
        Cabinets.CollisionStep([noRefill], ref state, ref events);

        Assert.Equal(1, state.Weapons[1].AmmoCount);
        Assert.Equal(ClassStats.For(PlayerClass.Scout).MaxHp, state.Characters[1].Hp);
    }

    /// <summary>
    /// <b>A refill cancels the reload timer</b>, so a cabinet does not leave a
    /// round arriving a moment later. That cancel is the only observable half of
    /// the reference's <c>event_perform(ev_alarm, 5)</c>; the body it runs is a
    /// no-op once the magazine is already full.
    /// </summary>
    [Fact]
    public void ARefillCancelsTheReloadTimer()
    {
        var cabinet = World.Cabinets[0];

        var state = default(WorldState);
        Put(ref state, 1, (cabinet.Left + cabinet.Right) / 2.0, (cabinet.Top + cabinet.Bottom) / 2.0);
        state.Weapons[1].AmmoCount = 1;
        state.Weapons[1].ReloadTimer = 20;

        var events = default(TickEvents);
        Cabinets.CollisionStep(World, ref state, ref events);

        Assert.Equal(WeaponState.Inactive, state.Weapons[1].ReloadTimer);
    }

    /// <summary>
    /// <b>The extinguish is outside the <c>heal</c> guard</b>, so a cabinet a
    /// map has switched healing off on still puts a burning character out.
    /// </summary>
    [Fact]
    public void ACabinetExtinguishesEvenWithHealingOff()
    {
        var cabinet = World.Cabinets[0];

        var state = default(WorldState);
        Put(ref state, 1, (cabinet.Left + cabinet.Right) / 2.0, (cabinet.Top + cabinet.Bottom) / 2.0);
        state.Characters[1].Hp = 10;
        state.Characters[1].BurnDuration = 100;
        state.Characters[1].BurnIntensity = 5;
        state.Characters[1].BurnedBy = 2;

        var events = default(TickEvents);
        Cabinets.CollisionStep(
            At(cabinet.Left, cabinet.Top, heal: false), ref state, ref events);

        Assert.Equal(0, state.Characters[1].BurnDuration);
        Assert.Equal(0, state.Characters[1].BurnIntensity);
        Assert.Equal(CharacterState.Nobody, state.Characters[1].BurnedBy);

        // And the healing really was off, or this proves nothing.
        Assert.Equal(10, state.Characters[1].Hp);
    }

    /// <summary>
    /// <b>The whole event is inside <c>if (!global.mapchanging)</c></b>: a
    /// cabinet does nothing during the victory screen.
    /// </summary>
    [Fact]
    public void ACabinetDoesNothingWhileTheMapIsChanging()
    {
        var cabinet = World.Cabinets[0];

        var state = default(WorldState);
        state.MapChanging = true;
        Put(ref state, 1, (cabinet.Left + cabinet.Right) / 2.0, (cabinet.Top + cabinet.Bottom) / 2.0);
        state.Characters[1].Hp = 10;

        var events = default(TickEvents);
        Cabinets.CollisionStep(World, ref state, ref events);

        Assert.Equal(10, state.Characters[1].Hp);
    }

    /// <summary>
    /// <b>Team Deathmatch trickles ten hit points a second instead</b>, and
    /// refills no nuts and bolts at all.
    /// </summary>
    [Fact]
    public void TeamDeathmatchGetsATrickleAndNoNutsAndBolts()
    {
        var cabinet = World.Cabinets[0];

        var state = default(WorldState);
        state.GameMode = GameMode.TeamDeathmatch;
        Put(ref state, 1, (cabinet.Left + cabinet.Right) / 2.0, (cabinet.Top + cabinet.Bottom) / 2.0,
            PlayerClass.Engineer);
        state.Characters[1].Hp = 10;
        state.Characters[1].NutsNBolts = 0;

        var events = default(TickEvents);
        Cabinets.CollisionStep(World, ref state, ref events);

        Assert.Equal(10 + (10.0 / 30.0), state.Characters[1].Hp, 9);
        Assert.Equal(0, state.Characters[1].NutsNBolts);
    }

    /// <summary>
    /// A cabinet with <c>uber:true</c> hands a medic a finished charge — and one
    /// without it does not.
    /// </summary>
    [Fact]
    public void AnUberCabinetFinishesAMedicsCharge()
    {
        var cabinet = World.Cabinets[0];
        var x = (cabinet.Left + cabinet.Right) / 2.0;
        var y = (cabinet.Top + cabinet.Bottom) / 2.0;

        var plain = default(WorldState);
        Put(ref plain, 1, x, y, PlayerClass.Medic);

        var events = default(TickEvents);
        Cabinets.CollisionStep(World, ref plain, ref events);

        Assert.False(plain.Weapons[1].UberReady);

        var charged = default(WorldState);
        Put(ref charged, 1, x, y, PlayerClass.Medic);

        Cabinets.CollisionStep(At(cabinet.Left, cabinet.Top, uber: true), ref charged, ref events);

        Assert.True(charged.Weapons[1].UberReady);
        Assert.Equal(WeaponState.MaxUberCharge, charged.Weapons[1].UberCharge);
        Assert.True(events.UberCharged[1]);
    }

    /// <summary>
    /// <c>onCabinet</c> is written by End Step, which is what a sentry build
    /// reads a tick later.
    /// </summary>
    [Fact]
    public void EndStepRecordsStandingOnACabinet()
    {
        var cabinet = World.Cabinets[0];

        var state = default(WorldState);
        Put(ref state, 1, (cabinet.Left + cabinet.Right) / 2.0, (cabinet.Top + cabinet.Bottom) / 2.0);

        Assert.False(state.Characters[1].OnCabinet);

        CharacterPhysics.EndStep(World, ref state.Characters[1]);

        Assert.True(state.Characters[1].OnCabinet);

        // The control: off it, the flag clears again.
        state.Characters[1].X = 2280;
        state.Characters[1].Y = 200;

        CharacterPhysics.EndStep(World, ref state.Characters[1]);

        Assert.False(state.Characters[1].OnCabinet);
    }

    /// <summary>The cabinet runs inside a real tick, not only when a test calls it.</summary>
    [Fact]
    public void ATickRunsTheCabinet()
    {
        var cabinet = World.Cabinets[0];

        var state = default(WorldState);
        Put(ref state, 1, (cabinet.Left + cabinet.Right) / 2.0, (cabinet.Top + cabinet.Bottom) / 2.0);
        state.Characters[1].Hp = 10;

        MatchSimulation.Tick(World, ref state, []);

        Assert.True(state.Characters[1].Hp > 10, "the cabinet did not run inside the tick");
        Assert.True(state.Characters[1].OnCabinet, "End Step did not record the cabinet");
    }

    /// <summary>
    /// <b>A BOOL property is only <c>"true"</c> or <c>"false"</c>.</b>
    /// <c>readProperty.gml</c>'s BOOL arm returns the default for anything else,
    /// so "TRUE", "1" and "yes" all mean "whatever the Create event said".
    /// </summary>
    /// <remarks>
    /// The only way to reach the non-default arms at all: no shipped map
    /// overrides any of the three switches, which is why <c>Cabinets.Of</c>
    /// exists as a seam.
    /// </remarks>
    [Theory]
    [InlineData("false", false)]
    [InlineData("true", true)]
    [InlineData("TRUE", false)]
    [InlineData("1", false)]
    [InlineData(null, false)]
    public void AnUberSwitchIsOnlyTheExactString(string? written, bool expected)
    {
        Assert.Equal(expected, Cabinet(uber: written).Uber);
    }

    /// <summary>And the two that default to true default the same way.</summary>
    [Theory]
    [InlineData("false", false)]
    [InlineData("true", true)]
    [InlineData("nope", true)]
    [InlineData(null, true)]
    public void AHealSwitchDefaultsToOn(string? written, bool expected)
    {
        Assert.Equal(expected, Cabinet(heal: written).Heal);
    }

    /// <summary>One cabinet with the switches spelled as given.</summary>
    private static HealingCabinet Cabinet(string? heal = null, string? uber = null)
    {
        var properties = new Dictionary<string, GgonValue>();

        if (heal is not null)
            properties["heal"] = new GgonString(heal);

        if (uber is not null)
            properties["uber"] = new GgonString(uber);

        return Cabinets.Of(new LevelEntity
        {
            Type = EntityTypes.MedCabinet,
            X = 2280,
            Y = 200,
            Properties = properties,
        });
    }

    /// <summary>One cabinet, at the position a test has put its character.</summary>
    private static HealingCabinet[] At(double x, double y, bool heal = true, bool uber = false)
    {
        var c = Cabinets.Of(new LevelEntity
        {
            Type = EntityTypes.MedCabinet,
            X = x,
            Y = y,
            Properties = new Dictionary<string, GgonValue>
            {
                ["heal"] = new GgonString(heal ? "true" : "false"),
                ["uber"] = new GgonString(uber ? "true" : "false"),
            },
        });

        return [c];
    }
}
