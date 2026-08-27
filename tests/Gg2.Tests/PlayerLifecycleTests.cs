using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// Phase 6 groundwork: the player in the world — team, class, the respawn
/// timer — and what a class or team change does to a live character.
/// </summary>
/// <remarks>
/// Transcribed from <c>Objects/InGameElements/Player.events/{Create, Alarm
/// 5}.xml</c>, <c>Scripts/Spawning/PlayerSpawn.gml</c>,
/// <c>Character.events/Destroy.xml</c>'s respawn arming, and
/// <c>processClientCommands.gml</c>'s <c>PLAYER_CHANGECLASS</c> and
/// <c>PLAYER_CHANGETEAM</c> cases. No recording gates respawn: every checked-in
/// closed-loop fixture injects the reference's own <c>PLAYER_SPAWN</c>s, so
/// these tests are the gate.
/// </remarks>
public sealed class PlayerLifecycleTests
{
    private const string Map = "ctf_truefort";

    /// <summary>A standing position: the floor is directly under it.</summary>
    private const double OpenX = 2600;

    private const double OpenY = 708;

    private static LevelMap? _sharedLevel;

    private static LevelMap Level => _sharedLevel ??= LevelMap.FromPng(Map, ReferenceTree.ReadMap(Map));

    private static CollisionWorld? _sharedWorld;

    private static CollisionWorld World => _sharedWorld ??= new CollisionWorld(Level);

    private static SpawnPoints? _sharedSpawns;

    private static SpawnPoints Spawns => _sharedSpawns ??= new SpawnPoints(Level);

    /// <summary>A live character whose player the world knows about.</summary>
    private static void Put(
        ref WorldState state,
        int slot,
        PlayerClass playerClass,
        Team team,
        double x,
        double y,
        double hp = -1)
    {
        var character = CharacterState.Fresh(playerClass, team);
        character.X = x;
        character.Y = y;
        if (hp >= 0)
            character.Hp = hp;

        state.Characters[slot] = character;
        state.Weapons[slot] = WeaponState.Spawn(playerClass, x, y);
        state.Alive[slot] = true;

        state.AddPlayer(slot);
        state.Players[slot].Team = team;
        state.Players[slot].Class = playerClass;
    }

    private static void Tick(ref WorldState state, ref TickEvents events) =>
        MatchSimulation.Tick(World, ref state, [], ref events, Spawns);

    // --- the respawn ----------------------------------------------------------

    /// <summary>
    /// Death arms <c>player.alarm[5]</c> to <c>Server_Respawntime</c>, and the
    /// character comes back exactly that many ticks later at a spawn point of
    /// its team — announced as a point id and group, never a position.
    /// </summary>
    [Fact]
    public void ADeathArmsTheRespawnTimerAndTheSpawnComesBackOnIt()
    {
        var state = default(WorldState);
        var events = default(TickEvents);

        state.RespawnTime = 30;
        Put(ref state, 1, PlayerClass.Scout, Team.Red, OpenX, OpenY, hp: 0);

        Tick(ref state, ref events);

        Assert.True(events.Died[1]);
        Assert.False(state.Alive[1]);
        Assert.Equal(30, state.Players[1].RespawnTimer);

        // An alarm set to n fires n ticks later: 29 quiet ticks...
        for (var tick = 0; tick < 29; tick++)
        {
            Tick(ref state, ref events);
            Assert.False(state.Alive[1]);
            Assert.False(events.Spawned[1]);
        }

        // ...and the thirtieth spawns.
        Tick(ref state, ref events);

        Assert.True(events.Spawned[1]);
        Assert.True(state.Alive[1]);
        Assert.Equal(Timers.Inactive, state.Players[1].RespawnTimer);

        var spawn = events.Spawns[1];
        Assert.Equal(0, spawn.Group);
        Assert.True(spawn.SpawnPointId < Spawns.Count(Team.Red, 0));

        // The character stands exactly on the announced point, full hp, fresh.
        Assert.True(Spawns.TryGet(Team.Red, spawn.Group, spawn.SpawnPointId, out var x, out var y));
        Assert.Equal(x, state.Characters[1].X);
        Assert.Equal(y, state.Characters[1].Y);
        Assert.Equal(ClassStats.For(PlayerClass.Scout).MaxHp, state.Characters[1].Hp);
    }

    /// <summary>The respawn wears whatever class and team the player holds *now*.</summary>
    [Fact]
    public void TheRespawnWearsThePlayersCurrentClassAndTeam()
    {
        var state = default(WorldState);
        var events = default(TickEvents);

        state.RespawnTime = 5;
        Put(ref state, 1, PlayerClass.Scout, Team.Red, OpenX, OpenY, hp: 0);

        Tick(ref state, ref events);
        Assert.False(state.Alive[1]);

        // A dead player changes class while waiting; the timer keeps counting.
        PlayerCommands.ChangeClass(ref state, 1, PlayerClass.Heavy);

        for (var tick = 0; tick < 5; tick++)
            Tick(ref state, ref events);

        Assert.True(state.Alive[1]);
        Assert.Equal(PlayerClass.Heavy, state.Characters[1].Class);
        Assert.Equal(ClassStats.For(PlayerClass.Heavy).MaxHp, state.Characters[1].Hp);
    }

    /// <summary>PlayerSpawn.gml: a spectator's alarm fires and spawns nobody.</summary>
    [Fact]
    public void ASpectatorsRespawnAlarmSpawnsNobody()
    {
        var state = default(WorldState);
        var events = default(TickEvents);

        state.AddPlayer(1);
        state.Players[1].RespawnTimer = 1;

        Tick(ref state, ref events);

        Assert.False(state.Alive[1]);
        Assert.False(events.Spawned[1]);
        Assert.Equal(Timers.Inactive, state.Players[1].RespawnTimer);
    }

    /// <summary>
    /// RespawnTime 0 is "off": a death arms nothing and nobody self-spawns —
    /// the mode the parity replayer runs in, where the recording's own
    /// <c>PLAYER_SPAWN</c>s are the only way back.
    /// </summary>
    [Fact]
    public void WithRespawnTimeZeroADeathArmsNothing()
    {
        var state = default(WorldState);
        var events = default(TickEvents);

        Put(ref state, 1, PlayerClass.Scout, Team.Red, OpenX, OpenY, hp: 0);

        for (var tick = 0; tick < 40; tick++)
            Tick(ref state, ref events);

        Assert.False(state.Alive[1]);
        Assert.Equal(Timers.Inactive, state.Players[1].RespawnTimer);
    }

    // --- class and team changes ----------------------------------------------

    /// <summary>
    /// A class change with a live, undamaged character: they bid farewell —
    /// killed by their own hand, no assistant — and the respawn is armed.
    /// </summary>
    [Fact]
    public void ChangingClassAliveIsABidFarewell()
    {
        var state = default(WorldState);

        state.RespawnTime = 30;
        Put(ref state, 1, PlayerClass.Scout, Team.Red, OpenX, OpenY);

        var death = PlayerCommands.ChangeClass(ref state, 1, PlayerClass.Soldier);

        Assert.NotNull(death);
        Assert.Equal(1, death.Value.Slot);
        Assert.Equal(1, death.Value.Killer);
        Assert.Equal(CharacterState.Nobody, death.Value.Assistant);
        Assert.Equal(DamageSource.BidFarewell, death.Value.Source);

        Assert.False(state.Alive[1]);
        Assert.Equal(default, state.Weapons[1]);
        Assert.Equal(PlayerClass.Soldier, state.Players[1].Class);
        Assert.Equal(30, state.Players[1].RespawnTimer);
    }

    /// <summary>
    /// With somebody's damage still fresh, the change hands them the kill as
    /// FINISHED_OFF — with the killer's-healer assist redirect, like any death.
    /// </summary>
    [Fact]
    public void ChangingClassWhileHurtHandsTheKillToTheAttacker()
    {
        var state = default(WorldState);

        Put(ref state, 1, PlayerClass.Scout, Team.Red, OpenX, OpenY);
        Put(ref state, 2, PlayerClass.Soldier, Team.Blue, OpenX + 200, OpenY);
        Put(ref state, 3, PlayerClass.Medic, Team.Blue, OpenX + 300, OpenY);

        state.Characters[1].LastDamageDealer = 2;
        state.Characters[1].SecondToLastDamageDealer = 4;
        state.Characters[2].Healer = 3;

        var death = PlayerCommands.ChangeClass(ref state, 1, PlayerClass.Soldier);

        Assert.NotNull(death);
        Assert.Equal(2, death.Value.Killer);
        Assert.Equal(3, death.Value.Assistant);
        Assert.Equal(DamageSource.FinishedOff, death.Value.Source);
    }

    /// <summary>
    /// A characterless player's class change queues a spawn for this same
    /// tick's alarm stage — `alarm[5] = 1` — without touching one already
    /// counting down.
    /// </summary>
    [Fact]
    public void ChangingClassDeadQueuesAnImmediateSpawn()
    {
        var state = default(WorldState);
        var events = default(TickEvents);

        state.RespawnTime = 30;
        state.AddPlayer(1);
        state.Players[1].Team = Team.Red;

        var death = PlayerCommands.ChangeClass(ref state, 1, PlayerClass.Pyro);

        Assert.Null(death);
        Assert.Equal(1, state.Players[1].RespawnTimer);

        Tick(ref state, ref events);

        Assert.True(events.Spawned[1]);
        Assert.Equal(PlayerClass.Pyro, state.Characters[1].Class);

        // And the not-touching half: a running timer is left alone.
        Deaths.Kill(ref state, 1, ref events);
        Assert.Equal(30, state.Players[1].RespawnTimer);
        PlayerCommands.ChangeClass(ref state, 1, PlayerClass.Scout);
        Assert.Equal(30, state.Players[1].RespawnTimer);
    }

    /// <summary>A team change with a live character kills them and re-arms the timer.</summary>
    [Fact]
    public void ChangingTeamAliveKillsAndReArmsTheRespawn()
    {
        var state = default(WorldState);
        var events = default(TickEvents);

        state.RespawnTime = 4;
        Put(ref state, 1, PlayerClass.Scout, Team.Red, OpenX, OpenY);

        var death = PlayerCommands.ChangeTeam(ref state, 1, Team.Blue);

        Assert.NotNull(death);
        Assert.Equal(DamageSource.BidFarewell, death.Value.Source);
        Assert.False(state.Alive[1]);
        Assert.Equal(4, state.Players[1].RespawnTimer);
        Assert.Equal(Team.Blue, state.Players[1].Team);

        for (var tick = 0; tick < 4; tick++)
            Tick(ref state, ref events);

        Assert.True(state.Alive[1]);
        Assert.Equal(Team.Blue, state.Characters[1].Team);

        // On a blue spawn point, not a red one.
        var spawn = events.Spawns[1];
        Assert.True(Spawns.TryGet(Team.Blue, spawn.Group, spawn.SpawnPointId, out var x, out _));
        Assert.Equal(x, state.Characters[1].X);
    }

    // --- the team balance guard ----------------------------------------------

    [Fact]
    public void JoiningTheBiggerTeamIsRefused()
    {
        var state = default(WorldState);

        state.AddPlayer(1);
        state.AddPlayer(2);
        state.AddPlayer(3);
        state.Players[2].Team = Team.Red;
        state.Players[3].Team = Team.Red;

        // Red outnumbers blue 2-0, so red is refused and blue is granted.
        Assert.Null(PlayerCommands.ResolveRequestedTeam(ref state, 1, Team.Red));
        Assert.Equal(Team.Blue, PlayerCommands.ResolveRequestedTeam(ref state, 1, Team.Blue));

        // "Any" resolves to the smaller team outright.
        Assert.Equal(Team.Blue, PlayerCommands.ResolveRequestedTeam(ref state, 1, Team.Any));

        // An invalid byte is a request for "any" (processClientCommands.gml).
        Assert.Equal(Team.Blue, PlayerCommands.ResolveRequestedTeam(ref state, 1, (Team)7));
    }

    [Fact]
    public void TheTeamYouAreAlreadyOnIsRefusedAndSpectatorIsAlwaysOpen()
    {
        var state = default(WorldState);

        state.AddPlayer(1);
        state.Players[1].Team = Team.Red;

        Assert.Null(PlayerCommands.ResolveRequestedTeam(ref state, 1, Team.Red));
        Assert.Equal(Team.Spectator, PlayerCommands.ResolveRequestedTeam(ref state, 1, Team.Spectator));

        // Level teams: "any" is choose(RED, BLUE), and the seed-0 stream's
        // first draw picks RED — the requester's own team, which the
        // `newTeam != player.team` clause then refuses. The reference really
        // does refuse an "any" whose coin lands where you already are.
        Assert.Null(PlayerCommands.ResolveRequestedTeam(ref state, 1, Team.Any));

        // A spectator asking "any" takes whatever the coin says.
        state.Players[1].Team = Team.Spectator;
        state.Random = new GmlRandom(0);
        Assert.Equal(Team.Red, PlayerCommands.ResolveRequestedTeam(ref state, 1, Team.Any));
    }

    // --- the orphaned sentry, now on the simulation's own tick ----------------

    /// <summary>
    /// <c>Sentry.events/Begin Step.xml</c>'s opening guard: a sentry whose
    /// owner stopped being an engineer of its team dies on the tick *after*
    /// the change — not inside it, which is where the old seams fired.
    /// </summary>
    [Fact]
    public void AnOrphanedSentryDiesOnTheNextTicksBeginStep()
    {
        var state = default(WorldState);
        var events = default(TickEvents);

        Put(ref state, 1, PlayerClass.Engineer, Team.Red, OpenX, OpenY);
        state.HasSentry[1] = true;
        state.Sentries[1] = SentryState.Build(Team.Red, OpenX + 60, OpenY, 1);

        var death = PlayerCommands.ChangeClass(ref state, 1, PlayerClass.Soldier);
        Assert.NotNull(death);

        // The change itself leaves the sentry standing — the reference's guard
        // is the next Begin Step's.
        Assert.True(state.HasSentry[1]);

        Tick(ref state, ref events);

        Assert.False(state.HasSentry[1]);
        Assert.True(events.SentryDestroyed[1]);

        var destruction = events.SentryDestructions[1];
        Assert.Equal(CharacterState.Nobody, destruction.Killer);
        Assert.Equal(CharacterState.Nobody, destruction.Assistant);
        Assert.Equal(DamageSource.None, destruction.Source);
    }

    /// <summary>The same guard fires on a team change, one tick later.</summary>
    [Fact]
    public void AnEngineerChangingTeamLosesTheSentryATickLater()
    {
        var state = default(WorldState);
        var events = default(TickEvents);

        Put(ref state, 1, PlayerClass.Engineer, Team.Red, OpenX, OpenY);
        state.HasSentry[1] = true;
        state.Sentries[1] = SentryState.Build(Team.Red, OpenX + 60, OpenY, 1);

        PlayerCommands.ChangeTeam(ref state, 1, Team.Blue);
        Assert.True(state.HasSentry[1]);

        Tick(ref state, ref events);
        Assert.False(state.HasSentry[1]);
    }

    /// <summary>Build reads the owner's team off the world's player state.</summary>
    [Fact]
    public void BuildingASentryTakesTheOwnersTeamFromThePlayer()
    {
        var state = default(WorldState);

        Put(ref state, 1, PlayerClass.Engineer, Team.Blue, OpenX, OpenY);

        Sentries.Build(ref state, 1, OpenX + 60, OpenY, 1);

        Assert.Equal(Team.Blue, state.Sentries[1].Team);
    }

    // --- the roster compaction ------------------------------------------------

    [Fact]
    public void RemoveSlotCompactsThePlayers()
    {
        var state = default(WorldState);

        state.AddPlayer(1);
        state.AddPlayer(2);
        state.Players[1].Team = Team.Red;
        state.Players[2].Team = Team.Blue;
        state.Players[2].Class = PlayerClass.Heavy;
        state.Players[2].RespawnTimer = 17;

        state.RemoveSlot(1);

        Assert.True(state.HasPlayer[1]);
        Assert.False(state.HasPlayer[2]);
        Assert.Equal(Team.Blue, state.Players[1].Team);
        Assert.Equal(PlayerClass.Heavy, state.Players[1].Class);
        Assert.Equal(17, state.Players[1].RespawnTimer);
    }
}
