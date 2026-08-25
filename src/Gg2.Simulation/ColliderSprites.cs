using Gg2.Maps;
using Gg2.Protocol;

namespace Gg2.Simulation;

/// <summary>
/// Which map entity becomes which solid rectangle, and how big.
/// </summary>
/// <remarks>
/// The sizes are the collider sprites', read out of the running game. There are
/// only four of them and they are plain filled rectangles with their origin at
/// (0, 0):
/// <code>
/// sprite44   60 x 6    horizontal bar — floors, dropdown platforms
/// sprite45    6 x 60   vertical bar   — walls, gates
/// sprite3    16 x 16   Obstacle
/// SetupGateS 32 x 32   ControlPointSetupGate
/// </code>
/// A map entity's <c>xscale</c> and <c>yscale</c> stretch one of those.
/// </remarks>
public static class ColliderSprites
{
    private const int BarLong = 60;
    private const int BarShort = 6;
    private const int SetupGate = 32;

    /// <summary>The rectangle an entity becomes, or null if it is not solid to characters.</summary>
    /// <remarks>
    /// The drop-through platforms are deliberately absent: they are not in
    /// <c>charSetSolids</c>, and the physics handles them separately in
    /// <c>Character.events/Begin Step.xml</c> because a character stands on one
    /// only when falling onto it and not holding <c>KEY_DOWN</c>.
    /// <para>
    /// <c>BulletWall</c> <em>is</em> here, and used to not be. It is solid to
    /// nothing a character does — which is why phase 3 could leave it out — and
    /// solid to every projectile, because <c>gunSetSolids</c> ends with it.
    /// Whether a given rectangle is solid to a given asker is
    /// <see cref="SolidRect.IsSolidTo"/>'s business, not this function's.
    /// </para>
    /// <para>
    /// ⚠️ <b>A bullet wall can open, and that is not modelled.</b>
    /// <c>BulletWall.events/Begin Step.xml</c> clears its mask while any
    /// character is within a <c>distance</c> property the map may set, so a map
    /// can build a window that closes when nobody is near it. The property
    /// defaults to -1, meaning never — and no built-in map sets it (checked
    /// across all 22 in <c>maps.json</c>; the two that place bullet walls,
    /// <c>arena_lumberyard</c> and <c>koth_harvest</c>, carry no properties at
    /// all). Every rectangle here is therefore permanently closed, which is
    /// exact for the shipped maps and wrong for a custom map that uses the
    /// trigger.
    /// </para>
    /// </remarks>
    public static SolidRect? Of(LevelEntity entity) => entity.Type switch
    {
        // Walls. Vertical bar unless the name says horizontal.
        EntityTypes.PlayerWall =>
            Bar(entity, vertical: true, SolidKind.PlayerWall),
        EntityTypes.PlayerWallHorizontal =>
            Bar(entity, vertical: false, SolidKind.PlayerWall),

        EntityTypes.BulletWall =>
            Bar(entity, vertical: true, SolidKind.BulletWall),
        EntityTypes.BulletWallHorizontal =>
            Bar(entity, vertical: false, SolidKind.BulletWall),

        // Team gates. The "2" variants are the horizontal ones.
        EntityTypes.RedTeamGate =>
            Bar(entity, vertical: true, SolidKind.TeamGate, Team.Red),
        EntityTypes.BlueTeamGate =>
            Bar(entity, vertical: true, SolidKind.TeamGate, Team.Blue),
        EntityTypes.RedTeamGate2 =>
            Bar(entity, vertical: false, SolidKind.TeamGate, Team.Red),
        EntityTypes.BlueTeamGate2 =>
            Bar(entity, vertical: false, SolidKind.TeamGate, Team.Blue),

        // Intel gates. The un-teamed pair carries TEAM_ANY, which never equals
        // a character's team, so they block any intel carrier.
        EntityTypes.RedIntelGate =>
            Bar(entity, vertical: true, SolidKind.IntelGate, Team.Red),
        EntityTypes.BlueIntelGate =>
            Bar(entity, vertical: true, SolidKind.IntelGate, Team.Blue),
        EntityTypes.RedIntelGate2 =>
            Bar(entity, vertical: false, SolidKind.IntelGate, Team.Red),
        EntityTypes.BlueIntelGate2 =>
            Bar(entity, vertical: false, SolidKind.IntelGate, Team.Blue),
        EntityTypes.IntelGateVertical =>
            Bar(entity, vertical: true, SolidKind.IntelGate, Team.Any),
        EntityTypes.IntelGateHorizontal =>
            Bar(entity, vertical: false, SolidKind.IntelGate, Team.Any),

        EntityTypes.SetupGate => SolidRect.FromEntity(
            entity.X, entity.Y, entity.XScale, entity.YScale, SetupGate, SetupGate, SolidKind.SetupGate),

        _ => null,
    };

    private static SolidRect Bar(LevelEntity entity, bool vertical, SolidKind kind, Team team = Team.Spectator) =>
        SolidRect.FromEntity(
            entity.X,
            entity.Y,
            entity.XScale,
            entity.YScale,
            vertical ? BarShort : BarLong,
            vertical ? BarLong : BarShort,
            kind,
            team);

    /// <summary>Every solid rectangle on a map, in level-data order.</summary>
    public static List<SolidRect> Build(LevelMap map)
    {
        var rects = new List<SolidRect>();

        foreach (var entity in map.Entities)
            if (Of(entity) is { } rect)
                rects.Add(rect);

        return rects;
    }
}
