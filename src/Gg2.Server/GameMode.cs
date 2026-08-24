using Gg2.Maps;

namespace Gg2.Server;

/// <summary>
/// Which gamemode a map is, and therefore which HUD block its state updates
/// carry.
/// </summary>
/// <remarks>
/// There is no stored mode: the reference decides from which objects the map
/// created (<c>Scripts/Maps/basicRoomSetup.gml:36-70</c>), and so does this.
/// <b>The order of the tests is load-bearing</b> — every control point is a
/// child of <c>ControlPoint</c>, so the specific kinds have to be ruled out
/// before the generic branch, and a CTF map with a setup gate is Invasion
/// rather than CTF.
/// </remarks>
public enum GameMode
{
    /// <summary>Intel present, no setup gate. <c>CTFHUD</c>.</summary>
    CaptureTheFlag,

    /// <summary>Intel present <em>and</em> a setup gate. <c>InvasionHUD</c>.</summary>
    Invasion,

    /// <summary><c>GeneratorHUD</c>.</summary>
    Generator,

    /// <summary><c>ArenaHUD</c>.</summary>
    Arena,

    /// <summary><c>KothHUD</c>.</summary>
    KingOfTheHill,

    /// <summary><c>DKothHUD</c>.</summary>
    DualKingOfTheHill,

    /// <summary>Numbered control points. <c>ControlPointHUD</c>.</summary>
    ControlPoints,

    /// <summary>Nothing else matched. <c>TeamDeathmatchHUD</c>.</summary>
    TeamDeathmatch,
}

public static class GameModes
{
    private static readonly string[] NumberedControlPoints =
    [
        EntityTypes.ControlPoint1,
        EntityTypes.ControlPoint2,
        EntityTypes.ControlPoint3,
        EntityTypes.ControlPoint4,
        EntityTypes.ControlPoint5,
    ];

    /// <summary>
    /// Infers the gamemode the way <c>basicRoomSetup.gml</c> does, in the same
    /// order.
    /// </summary>
    public static GameMode Of(LevelMap map)
    {
        // "instance_exists(IntelligenceBase) or instance_exists(Intelligence)" —
        // the intel object is created by its base, so the base entity is enough.
        if (Has(map, EntityTypes.RedIntel) || Has(map, EntityTypes.BlueIntel))
            return Has(map, EntityTypes.SetupGate) ? GameMode.Invasion : GameMode.CaptureTheFlag;

        if (Has(map, EntityTypes.GeneratorRed) || Has(map, EntityTypes.GeneratorBlue))
            return GameMode.Generator;

        if (Has(map, EntityTypes.ArenaControlPoint))
            return GameMode.Arena;

        if (Has(map, EntityTypes.KothControlPoint))
            return GameMode.KingOfTheHill;

        if (Has(map, EntityTypes.KothRedControlPoint) && Has(map, EntityTypes.KothBlueControlPoint))
            return GameMode.DualKingOfTheHill;

        foreach (var type in NumberedControlPoints)
            if (Has(map, type))
                return GameMode.ControlPoints;

        return GameMode.TeamDeathmatch;
    }

    /// <summary>
    /// How many map areas a map has: one, plus one per <c>NextAreaO</c>
    /// (<c>basicRoomSetup.gml:7</c>).
    /// </summary>
    public static int TotalAreas(LevelMap map) => 1 + Count(map, EntityTypes.NextArea);

    private static bool Has(LevelMap map, string type)
    {
        foreach (var entity in map.Entities)
            if (string.Equals(entity.Type, type, StringComparison.Ordinal))
                return true;
        return false;
    }

    private static int Count(LevelMap map, string type)
    {
        var count = 0;
        foreach (var entity in map.Entities)
            if (string.Equals(entity.Type, type, StringComparison.Ordinal))
                count++;
        return count;
    }
}
