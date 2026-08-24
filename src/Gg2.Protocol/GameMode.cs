namespace Gg2.Protocol;

/// <summary>
/// Which gamemode a map is, and therefore which HUD block its state updates
/// carry.
/// </summary>
/// <remarks>
/// This lives in the protocol rather than with the map because it is a
/// <b>wire-layout selector</b>: the <c>FULL_UPDATE</c> and <c>CAPS_UPDATE</c>
/// tails are a different width and shape per mode, so nothing can frame a state
/// update without it. Deciding which mode a given map <em>is</em> needs the
/// entity list, and that stays in <c>Gg2.Maps</c>.
/// <para>
/// The reference has no stored mode: <c>Scripts/Maps/basicRoomSetup.gml:36-70</c>
/// decides from which objects the map created, and the order of its tests is
/// load-bearing — every control point is a child of <c>ControlPoint</c>, so the
/// specific kinds have to be ruled out before the generic branch, and a CTF map
/// with a setup gate is Invasion rather than CTF.
/// </para>
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

/// <summary>
/// What a state update's shape depends on, beyond the roster.
/// </summary>
/// <remarks>
/// All of it comes from the map, and none of it changes while a map is loaded —
/// which is why a reader can be told once and then frame every update until the
/// next <c>CHANGE_MAP</c>.
/// </remarks>
public readonly record struct WorldShape
{
    public required GameMode GameMode { get; init; }

    /// <summary>
    /// Control points the HUD block serialises, in its own order. Zero for the
    /// modes whose HUD carries none.
    /// </summary>
    public required int ControlPointCount { get; init; }

    /// <summary>
    /// <c>MovingPlatform</c> instances. Their blocks carry no count prefix —
    /// the client has the same map loaded and knows how many there are — so a
    /// reader has to be told.
    /// </summary>
    public required int MovingPlatformCount { get; init; }
}
