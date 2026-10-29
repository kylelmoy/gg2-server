using GG2.Maps;
using GG2.Protocol;
using GG2.Simulation;
using Xunit;

namespace GG2.Tests;

/// <summary>
/// <see cref="PlayerState.SpawnPin"/>: a pinned player respawns at its pin and
/// the random draw is skipped; an unpinned one draws exactly as before.
/// <c>docs/DUEL.md</c>.
/// </summary>
public sealed class SpawnPinTests
{
    private const string Map = "ctf_truefort";
    private const int Slot = 1;

    private static (CollisionWorld World, SpawnPoints Spawns, WorldState State) Setup()
    {
        var level = LevelMap.FromPng(Map, ReferenceTree.ReadMap(Map)).ForArea(1);
        var state = default(WorldState);

        state.GameMode = GameModes.Of(level);
        state.TimeLimitMinutes = 15;
        state.RespawnTime = 0;

        for (var i = 0; i < TeamBalance.ClassCount; i++)
            state.ClassLimits[i] = 255;

        Intel.Place(ref state, level);
        Rounds.Start(ref state);
        state.AddPlayer(0);
        state.AddPlayer(Slot);
        state.Players[Slot].Team = Team.Red;
        state.Players[Slot].Class = PlayerClass.Soldier;

        return (new CollisionWorld(level), new SpawnPoints(level), state);
    }

    /// <summary>Arms the respawn and runs the one tick it fires in.</summary>
    private static PlayerSpawnEvent RespawnOnce(CollisionWorld world, SpawnPoints spawns, ref WorldState state)
    {
        if (state.IsAlive(Slot))
            state.Clear(Slot);

        state.Players[Slot].RespawnTimer = 1;

        var events = default(TickEvents);
        var inputs = new PlayerInput[Slot + 1];
        MatchSimulation.Tick(world, ref state, inputs, ref events, spawns);

        Assert.True(events.Spawned[Slot], "the armed respawn did not fire");
        return events.Spawns[Slot];
    }

    /// <summary>
    /// Both ways a player gets made: <see cref="PlayerState.Fresh"/>, and the
    /// object initialiser the parity replayer and many tests use.
    /// </summary>
    [Fact]
    public void ANewPlayerIsUnpinnedHoweverItIsMade()
    {
        Assert.Null(PlayerState.Fresh.SpawnPin);
        Assert.Null(new PlayerState { Team = Team.Red }.SpawnPin);
        Assert.Null(default(PlayerState).SpawnPin);
    }

    [Fact]
    public void APinnedPlayerAlwaysTakesItsPin()
    {
        var (world, spawns, state) = Setup();
        Assert.True(spawns.Count(Team.Red, 0) >= 4, "the test needs a map with several red spawns");

        foreach (var pin in new[] { 3, 0, 2, 3, 1 })
        {
            state.Players[Slot].SpawnPin = pin;
            var spawn = RespawnOnce(world, spawns, ref state);

            Assert.Equal(pin, spawn.SpawnPointId);
            Assert.True(spawns.TryGet(Team.Red, 0, pin, out var x, out _));
            Assert.Equal(x, state.Characters[Slot].X);
        }
    }

    /// <summary>
    /// The pin replaces the draw rather than following it, so the stream does
    /// not move — and, as the control, an unpinned spawn on the same state does
    /// move it, so the comparison can fail.
    /// </summary>
    [Fact]
    public void APinSkipsTheDrawAndNoPinMakesIt()
    {
        var (world, spawns, state) = Setup();

        state.Players[Slot].SpawnPin = 2;
        var before = state.Random;
        RespawnOnce(world, spawns, ref state);
        Assert.Equal(before, state.Random);

        state.Players[Slot].SpawnPin = null;
        before = state.Random;
        RespawnOnce(world, spawns, ref state);
        Assert.NotEqual(before, state.Random);
    }

    [Fact]
    public void APinPastTheListFallsBackToTheDraw()
    {
        var (world, spawns, state) = Setup();
        var available = spawns.Count(Team.Red, 0);

        state.Players[Slot].SpawnPin = available;
        var before = state.Random;
        var spawn = RespawnOnce(world, spawns, ref state);

        Assert.True(spawn.SpawnPointId < available);
        Assert.NotEqual(before, state.Random);
    }

    /// <summary>A respawn updates the player in place, and a departure moves it whole.</summary>
    [Fact]
    public void ThePinSurvivesRespawnAndRosterCompaction()
    {
        var (world, spawns, state) = Setup();
        state.Players[Slot].SpawnPin = 4;
        RespawnOnce(world, spawns, ref state);
        Assert.Equal(4, state.Players[Slot].SpawnPin);

        state.AddPlayer(Slot + 1);
        state.Players[Slot + 1].SpawnPin = 1;
        state.RemoveSlot(Slot);

        Assert.Equal(1, state.Players[Slot].SpawnPin);
    }
}
