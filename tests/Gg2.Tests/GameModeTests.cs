using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Server;

namespace Gg2.Tests;

/// <summary>
/// Gamemode inference, which decides the shape of every <c>FULL_UPDATE</c> tail.
/// </summary>
/// <remarks>
/// There is no stored mode — the reference decides from which objects the map
/// created (<c>basicRoomSetup.gml:36-70</c>). The map-name prefixes below are a
/// convention, not the rule, which is exactly what makes them a useful check:
/// they were chosen by whoever made the map, and inference has to agree with
/// them without ever looking at the name.
/// </remarks>
public sealed class GameModeTests
{
    public static TheoryData<MapFixture> Maps => MapFixtures.AsTheoryData();

    [Theory]
    [MemberData(nameof(Maps))]
    public void InferredModeAgreesWithTheMapNamePrefix(MapFixture fixture)
    {
        var map = LevelMap.FromPng(fixture.Name, ReferenceTree.ReadMap(fixture.Name));
        var mode = GameModes.Of(map);

        var expected = fixture.Name switch
        {
            // Not CTF despite the prefix. It has intel *and* setup gates, so
            // basicRoomSetup.gml builds an InvasionHUD — which is a different
            // width on the wire. Attack/defend maps are named ctf_ by
            // convention only, and this is why inference reads the entities
            // rather than the name.
            "ctf_avanti" => GameMode.Invasion,

            _ => fixture.Name.Split('_')[0] switch
        {
            "ctf" => GameMode.CaptureTheFlag,
            "arena" => GameMode.Arena,
            "koth" => GameMode.KingOfTheHill,
            "dkoth" => GameMode.DualKingOfTheHill,
            "cp" => GameMode.ControlPoints,
            "gen" => GameMode.Generator,
            "tdm" => GameMode.TeamDeathmatch,

            // gg_debug is a collision test map with no objective at all.
            "gg" => GameMode.TeamDeathmatch,

            _ => throw new InvalidOperationException($"unhandled map prefix in {fixture.Name}"),
        },
        };

        Assert.Equal(expected, mode);
    }

    /// <summary>
    /// A CTF map that also has a setup gate is Invasion, not CTF — the two
    /// modes' HUD blocks are different widths, so this branch is not cosmetic.
    /// </summary>
    [Fact]
    public void IntelPlusASetupGateIsInvasion()
    {
        var ctf = MapWith(EntityTypes.RedIntel, EntityTypes.BlueIntel);
        Assert.Equal(GameMode.CaptureTheFlag, GameModes.Of(ctf));

        var invasion = MapWith(EntityTypes.RedIntel, EntityTypes.BlueIntel, EntityTypes.SetupGate);
        Assert.Equal(GameMode.Invasion, GameModes.Of(invasion));
    }

    /// <summary>
    /// Every control point is a child of <c>ControlPoint</c>, so the specific
    /// kinds must be ruled out before the numbered-point branch. Testing DKOTH
    /// after KOTH would silently pass; testing the generic branch first would
    /// silently swallow both.
    /// </summary>
    [Fact]
    public void SpecificControlPointKindsWinOverTheGenericBranch()
    {
        Assert.Equal(GameMode.KingOfTheHill, GameModes.Of(MapWith(EntityTypes.KothControlPoint)));

        Assert.Equal(GameMode.DualKingOfTheHill,
            GameModes.Of(MapWith(EntityTypes.KothRedControlPoint, EntityTypes.KothBlueControlPoint)));

        Assert.Equal(GameMode.Arena, GameModes.Of(MapWith(EntityTypes.ArenaControlPoint)));

        Assert.Equal(GameMode.ControlPoints,
            GameModes.Of(MapWith(EntityTypes.ControlPoint1, EntityTypes.ControlPoint2)));
    }

    [Fact]
    public void NothingObjectiveShapedIsTeamDeathmatch() =>
        Assert.Equal(GameMode.TeamDeathmatch, GameModes.Of(MapWith(EntityTypes.RedSpawn, EntityTypes.BlueSpawn)));

    /// <summary>
    /// A map's area count is one plus its <c>NextAreaO</c> markers
    /// (<c>basicRoomSetup.gml:7</c>). It reaches the wire in every
    /// <c>JOIN_UPDATE</c> and <c>MAP_END</c>, and it decides whether winning
    /// advances a stage or ends the map.
    /// </summary>
    [Theory]
    [MemberData(nameof(Maps))]
    public void AreaCountIsOnePlusTheNextAreaMarkers(MapFixture fixture)
    {
        var map = LevelMap.FromPng(fixture.Name, ReferenceTree.ReadMap(fixture.Name));

        var markers = map.OfType(EntityTypes.NextArea).Count();
        Assert.Equal(1 + markers, GameModes.TotalAreas(map));
    }

    /// <summary>
    /// <c>cp_dirtbowl</c> is the one shipped map that is multi-stage: two
    /// <c>NextAreaO</c> markers, so three areas. Worth pinning, because it is
    /// the only shipped exercise of the <c>global.currentMapArea</c> path that
    /// phase 6 has to get right.
    /// </summary>
    [Fact]
    public void DirtbowlIsTheOneMultiStageMap()
    {
        foreach (var fixture in MapFixtures.All)
        {
            var map = LevelMap.FromPng(fixture.Name, ReferenceTree.ReadMap(fixture.Name));
            var areas = GameModes.TotalAreas(map);

            if (fixture.Name == "cp_dirtbowl")
                Assert.Equal(3, areas);
            else
                Assert.Equal(1, areas);
        }
    }

    /// <summary>Builds a map with a one-cell walkmask and the given entities.</summary>
    private static LevelMap MapWith(params string[] types)
    {
        var entities = string.Join(',', types.Select(t => $"{{type:{t},x:0,y:0}}"));

        // A 1x1 open mask: one cell, six zero bits, chr(0 + 32) = ' '.
        var levelData = string.Join('\n',
            "{WALKMASK}", "1", "1", " ", "{END WALKMASK}",
            "{ENTITIES}", "[" + entities + "]", "{END ENTITIES}");

        return LevelMap.FromLevelData("synthetic", levelData);
    }
}
