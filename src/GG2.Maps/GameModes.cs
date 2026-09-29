using GG2.Protocol;

namespace GG2.Maps;

/// <summary>
/// Which gamemode a map is, decided the way the reference decides it.
/// </summary>
/// <remarks>
/// Lives with the maps because it is a fact about a map's entity list, and is
/// needed by anything that has to frame a state update — the server, and the
/// parity harness. <see cref="GG2.Protocol.GameMode"/> is the selector itself.
/// </remarks>
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

    /// <summary>
    /// Whether the map places a <c>ControlPointSetupGate</c>.
    /// </summary>
    /// <remarks>
    /// The one thing that separates the two control-point arrangements —
    /// <c>ControlPointHUD.events/Create.xml</c> is <c>if
    /// instance_exists(ControlPointSetupGate)</c> — and it is also what gives
    /// a CTF map Invasion's rules instead. Both read the same entity, which is
    /// why this is one question rather than two.
    /// </remarks>
    public static bool HasSetupGate(LevelMap map) => Has(map, EntityTypes.SetupGate);

    /// <summary>
    /// Everything a state update's shape depends on, beyond the roster.
    /// </summary>
    /// <remarks>
    /// <b>Give it the map the server is actually serving</b> — that is, one
    /// already narrowed with <see cref="LevelMap.ForArea"/>. On a multi-area
    /// map the unnarrowed entity list holds every area's control points at
    /// once, and a shape built from it makes a reader expect more blocks than
    /// an update carries. The framing then fails hundreds of bytes later with
    /// no hint of where. <c>cp_dirtbowl</c> is the only shipped map where the
    /// two differ: six points in the file, two in any given round.
    /// </remarks>
    public static WorldShape ShapeOf(LevelMap map)
    {
        var mode = Of(map);

        return new WorldShape
        {
            GameMode = mode,
            ControlPointCount = ControlPointsFor(map, mode),
            MovingPlatformCount = Count(map, EntityTypes.MovingPlatform),
            GeneratorCount = GeneratorsFor(map, mode),
        };
    }

    /// <summary>
    /// How many generator blocks the HUD writes at the <em>start</em> of a
    /// round.
    /// </summary>
    /// <remarks>
    /// Unlike every other count in a <see cref="WorldShape"/>, this one
    /// falls during a match: a destroyed generator stops being serialised. See
    /// <see cref="WorldShape.GeneratorCount"/>.
    /// </remarks>
    public static int GeneratorsFor(LevelMap map, GameMode mode)
    {
        if (mode != GameMode.Generator)
            return 0;

        // One per team at most, which is what GG2.Simulation.Generators.Place
        // represents — a map with two red generators would give the reference
        // two instances that every `GeneratorRed.hp` in the game reads as one.
        // Counting the entities instead would promise blocks the writer never
        // writes.
        return (Has(map, EntityTypes.GeneratorRed) ? 1 : 0)
               + (Has(map, EntityTypes.GeneratorBlue) ? 1 : 0);
    }

    /// <summary>
    /// How many control-point blocks the mode's HUD writes. Not "how many
    /// control points the map has": <c>CTFHUD</c> and <c>TeamDeathmatchHUD</c>
    /// write none whatever the map contains.
    /// </summary>
    public static int ControlPointsFor(LevelMap map, GameMode mode) => mode switch
    {
        GameMode.Arena => Count(map, EntityTypes.ArenaControlPoint),
        GameMode.KingOfTheHill => Count(map, EntityTypes.KothControlPoint),
        GameMode.DualKingOfTheHill =>
            Count(map, EntityTypes.KothRedControlPoint) + Count(map, EntityTypes.KothBlueControlPoint),
        GameMode.ControlPoints => NumberedControlPointCount(map),
        _ => 0,
    };

    private static int NumberedControlPointCount(LevelMap map)
    {
        var count = 0;
        foreach (var type in NumberedControlPoints)
            count += Count(map, type);
        return count;
    }

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
