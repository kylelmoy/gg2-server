namespace GG2.Maps;

/// <summary>
/// The entity type names a map may use, transcribed from the registration calls
/// in <c>Scripts/Maps/Builder/builder_init.gml</c> (the <c>addEntity</c> block).
/// </summary>
/// <remarks>
/// The names are the wire-level identity of an entity — they are what is written
/// into a map PNG — so they are reproduced verbatim, casing and all. Note that
/// the reference is inconsistent about it (<c>medCabinet</c>, <c>redspawn</c>,
/// <c>CapturePoint</c>, <c>playerwall_horizontal</c>); that is not a
/// transcription slip.
/// <para>
/// <c>meta</c> is not in this list. It is a pseudo-entity carrying map metadata
/// and is split out by <see cref="LevelMap"/> before entities are built.
/// </para>
/// </remarks>
public static class EntityTypes
{
    public const string Meta = "meta";

    // Spawning
    public const string SpawnRoom = "spawnroom";
    public const string RedSpawn = "redspawn";
    public const string RedSpawn1 = "redspawn1";
    public const string RedSpawn2 = "redspawn2";
    public const string RedSpawn3 = "redspawn3";
    public const string RedSpawn4 = "redspawn4";
    public const string BlueSpawn = "bluespawn";
    public const string BlueSpawn1 = "bluespawn1";
    public const string BlueSpawn2 = "bluespawn2";
    public const string BlueSpawn3 = "bluespawn3";
    public const string BlueSpawn4 = "bluespawn4";

    // Capture the flag
    public const string RedIntel = "redintel";
    public const string BlueIntel = "blueintel";

    // Gates. The "2" and "horizontal" variants are floors rather than walls.
    public const string RedTeamGate = "redteamgate";
    public const string BlueTeamGate = "blueteamgate";
    public const string RedTeamGate2 = "redteamgate2";
    public const string BlueTeamGate2 = "blueteamgate2";
    public const string RedIntelGate = "redintelgate";
    public const string BlueIntelGate = "blueintelgate";
    public const string RedIntelGate2 = "redintelgate2";
    public const string BlueIntelGate2 = "blueintelgate2";
    public const string IntelGateHorizontal = "intelgatehorizontal";
    public const string IntelGateVertical = "intelgatevertical";
    public const string SetupGate = "SetupGate";

    // Walls and hazards
    public const string PlayerWall = "playerwall";
    public const string PlayerWallHorizontal = "playerwall_horizontal";
    public const string BulletWall = "bulletwall";
    public const string BulletWallHorizontal = "bulletwall_horizontal";
    public const string LeftDoor = "leftdoor";
    public const string RightDoor = "rightdoor";
    public const string KillBox = "killbox";
    public const string PitFall = "pitfall";
    public const string FragBox = "fragbox";

    // Pickups
    public const string MedCabinet = "medCabinet";

    // Control points and objectives
    public const string ControlPoint1 = "controlPoint1";
    public const string ControlPoint2 = "controlPoint2";
    public const string ControlPoint3 = "controlPoint3";
    public const string ControlPoint4 = "controlPoint4";
    public const string ControlPoint5 = "controlPoint5";
    public const string CapturePoint = "CapturePoint";
    public const string ArenaControlPoint = "ArenaControlPoint";
    public const string KothControlPoint = "KothControlPoint";
    public const string KothRedControlPoint = "KothRedControlPoint";
    public const string KothBlueControlPoint = "KothBlueControlPoint";
    public const string GeneratorRed = "GeneratorRed";
    public const string GeneratorBlue = "GeneratorBlue";
    public const string NextArea = "NextAreaO";

    // Platforms and movers
    public const string DropdownPlatform = "dropdownPlatform";
    public const string MovingPlatform = "moving_platform";
    public const string MoveBoxUp = "MoveBoxUp";
    public const string MoveBoxDown = "MoveBoxDown";
    public const string MoveBoxLeft = "MoveBoxLeft";
    public const string MoveBoxRight = "MoveBoxRight";

    // Decoration
    public const string Foreground = "foreground";
    public const string ForegroundScale = "foreground_scale";

    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        SpawnRoom, RedSpawn, RedSpawn1, RedSpawn2, RedSpawn3, RedSpawn4,
        BlueSpawn, BlueSpawn1, BlueSpawn2, BlueSpawn3, BlueSpawn4,
        RedIntel, BlueIntel,
        RedTeamGate, BlueTeamGate, RedTeamGate2, BlueTeamGate2,
        RedIntelGate, BlueIntelGate, RedIntelGate2, BlueIntelGate2,
        IntelGateHorizontal, IntelGateVertical, SetupGate,
        PlayerWall, PlayerWallHorizontal, BulletWall, BulletWallHorizontal,
        LeftDoor, RightDoor, KillBox, PitFall, FragBox,
        MedCabinet,
        ControlPoint1, ControlPoint2, ControlPoint3, ControlPoint4, ControlPoint5,
        CapturePoint, ArenaControlPoint,
        KothControlPoint, KothRedControlPoint, KothBlueControlPoint,
        GeneratorRed, GeneratorBlue, NextArea,
        DropdownPlatform, MovingPlatform,
        MoveBoxUp, MoveBoxDown, MoveBoxLeft, MoveBoxRight,
        Foreground, ForegroundScale,
    };

    /// <summary>Whether the reference's builder registers this type name.</summary>
    public static bool IsKnown(string type) => Known.Contains(type);

    /// <summary>Every registered type name. Ordinal comparison; casing matters.</summary>
    public static IReadOnlyCollection<string> All => Known;
}
