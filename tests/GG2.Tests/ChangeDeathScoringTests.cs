using GG2.Maps;
using GG2.Protocol;
using GG2.Simulation;
using Xunit;

namespace GG2.Tests;

/// <summary>
/// A class or team change that kills is scored like any death:
/// <c>processClientCommands.gml:89/99/162/172</c> follow the send with
/// <c>doEventPlayerDeath</c>, which counts the victim's death, the finisher's
/// kill and points, and Team Deathmatch's kill (<c>doEventPlayerDeath.gml:30,
/// :308</c>).
/// </summary>
/// <remarks>
/// No parity fixture covered this: when the scoring was added no fixture's result
/// moved, so a missing score here would go unnoticed without these two tests.
/// </remarks>
public sealed class ChangeDeathScoringTests
{
    private const int Red = 1;
    private const int Blue = 2;

    /// <summary>
    /// A duel map: Team Deathmatch with no spawn rooms at all, so the change is
    /// the death arm rather than the silent swap every shipped spawn sits in.
    /// </summary>
    private static (CollisionWorld World, WorldState State) TwoPlayers()
    {
        var path = Path.Combine(ReferenceTree.MapsDirectory, "duel", "duel_01.png");
        var level = LevelMap.FromPng("duel_01", File.ReadAllBytes(path)).ForArea(1);
        var spawns = new SpawnPoints(level);
        var state = default(WorldState);

        state.GameMode = GameModes.Of(level);
        Assert.Equal(GameMode.TeamDeathmatch, state.GameMode);
        state.TimeLimitMinutes = 15;
        state.CapLimit = 100;

        for (var i = 0; i < TeamBalance.ClassCount; i++)
            state.ClassLimits[i] = 255;

        Rounds.Start(ref state);
        state.AddPlayer(0);
        state.AddPlayer(Red);
        state.AddPlayer(Blue);
        Assert.True(state.Spawn(spawns, Red, PlayerClass.Soldier, Team.Red, 0, 0));
        Assert.True(state.Spawn(spawns, Blue, PlayerClass.Soldier, Team.Blue, 0, 0));

        var world = new CollisionWorld(level);
        Assert.False(world.PointInSpawnRoom(state.Characters[Red].X, state.Characters[Red].Y));
        return (world, state);
    }

    [Fact]
    public void AFinishedOffClassChangeScoresTheDeathTheKillAndTheTeamPoint()
    {
        var (world, state) = TwoPlayers();
        state.Characters[Red].LastDamageDealer = Blue;

        var death = PlayerCommands.ChangeClass(ref state, Red, PlayerClass.Heavy, world);

        Assert.Equal(DamageSource.FinishedOff, death?.Source);
        Assert.Equal(1, Scoring.Get(state, Red, StatIndex.Deaths));
        Assert.Equal(1, Scoring.Get(state, Blue, StatIndex.Kills));
        Assert.Equal(1, state.BlueCaps);
        Assert.Equal(0, state.RedCaps);
    }

    [Fact]
    public void ABidFarewellClassChangeScoresTheDeathAndNothingElse()
    {
        var (world, state) = TwoPlayers();

        var death = PlayerCommands.ChangeClass(ref state, Red, PlayerClass.Heavy, world);

        Assert.Equal(DamageSource.BidFarewell, death?.Source);
        Assert.Equal(1, Scoring.Get(state, Red, StatIndex.Deaths));
        Assert.Equal(0, Scoring.Get(state, Red, StatIndex.Kills));
        Assert.Equal(0, state.BlueCaps);
        Assert.Equal(0, state.RedCaps);
    }
}
