using GG2.Maps;
using GG2.Parity;
using GG2.Protocol;

namespace GG2.Tests;

/// <summary>
/// A replay's <c>CHANGE_MAP</c> keeps the roster's classes and teams.
/// </summary>
/// <remarks>
/// <b>Every live recording depends on this.</b> <c>MatchRecorder</c> opens a
/// segment with the join burst — which names every player's class — and the
/// rotation's own <c>CHANGE_MAP</c> is teed in right behind it. A replay that
/// rebuilt the world wholesale on that message spawned everyone who did not
/// re-pick as a scout: measured on the fleet's recordings, 71 of 149 traces
/// showed a -20/-60/-100 hp mismatch against the wire
/// (a 120, 160 or 200 hp class spawned at 100), and <c>gg2-env dataset</c>
/// wrote those scouts into its observation rows. Nothing was red, because no
/// checked-in fixture crosses a map change after naming a class.
/// </remarks>
public sealed class ReplayMapChangeRosterTests
{
    private static LevelMap MapLoader(string name) =>
        LevelMap.FromPng(name, ReferenceTree.ReadMap(name));

    [Fact]
    public void AClassNamedBeforeAMapChangeSpawnsAfterIt()
    {
        var simulation = new PortedSimulation(MapLoader);
        simulation.LoadMap("ctf_truefort");
        simulation.SetPlayerTeam(1, Team.Blue);
        simulation.SetPlayerClass(1, PlayerClass.Heavy);

        simulation.LoadMap("ctf_2dfort");

        Assert.True(simulation.SpawnCharacter(1, 0, 0));
        Assert.True(simulation.TryGetState(1, out var character));
        Assert.Equal(PlayerClass.Heavy, character.Class);
        Assert.Equal(Team.Blue, character.Team);
        Assert.Equal(200, character.Hp);
    }

    [Fact]
    public void AMapChangeDoesNotInventPlayers()
    {
        var simulation = new PortedSimulation(MapLoader);
        simulation.LoadMap("ctf_truefort");
        simulation.LoadMap("ctf_2dfort");

        Assert.False(simulation.TryGetState(1, out _));
    }
}
