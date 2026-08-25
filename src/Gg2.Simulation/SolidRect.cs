using Gg2.Protocol;

namespace Gg2.Simulation;

/// <summary>What kind of solid a rectangle is, which decides when it is solid.</summary>
/// <remarks>
/// Solidity in GG2 is not a property of the obstacle — it is a property of the
/// obstacle <em>and the character asking</em>. The reference expresses that by
/// toggling a <c>solid</c> flag on every obstacle before each check and clearing
/// it afterwards (<c>Scripts/Physics/charSetSolids.gml</c>), because GML gave it
/// no better option. Here it is a predicate over a
/// <see cref="CollisionFilter"/>.
/// </remarks>
public enum SolidKind
{
    /// <summary>Always solid. <c>Obstacle</c> and its children.</summary>
    Obstacle,

    /// <summary>Always solid to characters. <c>PlayerWall</c>, <c>PlayerWallHorizontal</c>.</summary>
    PlayerWall,

    /// <summary>
    /// Solid when the asker is not on the gate's team, <b>or</b> is carrying
    /// intel.
    /// </summary>
    /// <remarks>
    /// The "or intel" half is what stops a team walking its own flag out
    /// through its own gate.
    /// </remarks>
    TeamGate,

    /// <summary>
    /// Solid when the asker is on a different team <b>and</b> is carrying intel.
    /// </summary>
    /// <remarks>
    /// The gate's team may be <see cref="Team.Any"/>
    /// (<c>IntelGateVertical</c>, <c>IntelGateHorizontal</c>), which is never
    /// equal to a character's team and so blocks any intel carrier.
    /// </remarks>
    IntelGate,

    /// <summary>Solid only while the setup timer is running.</summary>
    SetupGate,

    /// <summary>
    /// Solid to bullets and to nothing else. <c>BulletWall</c>,
    /// <c>BulletWallHorizontal</c>.
    /// </summary>
    /// <remarks>
    /// The mirror image of <see cref="PlayerWall"/>: <c>charSetSolids</c> never
    /// mentions it and <c>gunSetSolids</c> ends with it. Maps use the pair to
    /// make a window a player cannot climb through but can shoot through, and
    /// the other way round.
    /// </remarks>
    BulletWall,
}

/// <summary>
/// Who is asking, for the solidity questions that depend on it.
/// </summary>
/// <remarks>
/// docs/ARCHITECTURE.md's <c>CollisionFilter</c>: a parameter rather than a
/// hidden mutation, which is both faster than the reference's set/clear dance
/// and makes gate semantics visible at the call site.
/// </remarks>
public readonly record struct CollisionFilter
{
    public required Team Team { get; init; }

    public required bool CarryingIntel { get; init; }

    /// <summary>
    /// The character is holding <c>KEY_DOWN</c>, so drop-through platforms do
    /// not hold it up.
    /// </summary>
    public bool IgnoreDropdown { get; init; }

    /// <summary>Whether the map's setup gates are currently closed.</summary>
    public bool SetupGatesClosed { get; init; }

    /// <summary>
    /// The asker is a bullet, not a character:
    /// <c>Scripts/Physics/gunSetSolids.gml</c> rather than
    /// <c>charSetSolids.gml</c>.
    /// </summary>
    /// <remarks>
    /// <b>Three differences, and none of them is a detail.</b> A bullet passes
    /// straight through a <see cref="SolidKind.PlayerWall"/>, is stopped by a
    /// <see cref="SolidKind.BulletWall"/> that a character walks through, and is
    /// stopped by <em>every</em> gate regardless of team or intel — where a
    /// character's gates depend on both. Getting this wrong makes bullets stop
    /// at thin air on one side of a map and fly through walls on the other.
    /// </remarks>
    public bool Bullet { get; init; }

    /// <summary>A filter that collides with everything that can ever be solid.</summary>
    public static CollisionFilter Everything => new()
    {
        Team = Team.Spectator,
        CarryingIntel = true,
        SetupGatesClosed = true,
    };

    /// <summary>
    /// What a projectile or a hitscan collides with —
    /// <c>gunSetSolids</c>'s list.
    /// </summary>
    /// <param name="setupGatesClosed">Whether the setup timer is still running.</param>
    public static CollisionFilter ForBullet(bool setupGatesClosed = false) => new()
    {
        // The team and the intel flag are read only by the gate rules, and a
        // bullet's gates ignore both — so any value here is the same answer.
        Team = Team.Spectator,
        CarryingIntel = false,
        SetupGatesClosed = setupGatesClosed,
        Bullet = true,
    };
}

/// <summary>
/// An axis-aligned solid rectangle: a gate, a player wall, an obstacle.
/// </summary>
/// <remarks>
/// Bounds are <b>inclusive world pixels</b>, matching GM8's <c>bbox_*</c>.
/// <para>
/// The extent comes from the instance's sprite and scale, and GM8 works in
/// half-open sprite space:
/// <code>
/// bbox_right = round(x + (spriteRight + 1 - xoffset) * xscale) - 1
/// </code>
/// with the round being half to <b>even</b> — measured, see
/// <see cref="FromEntity"/>. So a 6-wide sprite at scale 3 covers exactly
/// eighteen pixels, and one at scale 2.25 covers thirteen rather than fourteen.
/// </para>
/// </remarks>
public readonly record struct SolidRect
{
    public required int Left { get; init; }

    public required int Right { get; init; }

    public required int Top { get; init; }

    public required int Bottom { get; init; }

    public required SolidKind Kind { get; init; }

    /// <summary>The gate's own team, where the kind has one.</summary>
    public Team Team { get; init; }

    /// <summary>Whether this rectangle is solid to the character described by the filter.</summary>
    public bool IsSolidTo(in CollisionFilter filter) => Kind switch
    {
        SolidKind.Obstacle => true,

        // charSetSolids sets it; gunSetSolids does not mention it at all.
        SolidKind.PlayerWall => !filter.Bullet,

        // gunSetSolids: `with(TeamGate) solid = true` — unconditionally, where
        // charSetSolids asks (team != other.team or other.intel).
        SolidKind.TeamGate => filter.Bullet || Team != filter.Team || filter.CarryingIntel,

        // Likewise: solid = (team != other.team and other.intel) for a
        // character, and simply solid for a bullet.
        SolidKind.IntelGate => filter.Bullet || (Team != filter.Team && filter.CarryingIntel),

        // The one both lists agree on: closed while the setup timer runs, open
        // afterwards, for characters and bullets alike.
        SolidKind.SetupGate => filter.SetupGatesClosed,

        SolidKind.BulletWall => filter.Bullet,

        _ => false,
    };

    /// <summary>Whether an inclusive pixel rectangle overlaps this one.</summary>
    public bool Overlaps(int left, int right, int top, int bottom) =>
        left <= Right && right >= Left && top <= Bottom && bottom >= Top;

    /// <summary>
    /// Builds the rectangle a map entity becomes, given its collider sprite's
    /// size.
    /// </summary>
    /// <remarks>
    /// Every collider sprite in the reference has its origin at (0, 0) and a
    /// bounding box covering the whole image, so the extent reduces to
    /// <c>round(x + width * xscale) - 1</c>. Confirmed against three live
    /// <c>RedTeamGate</c> instances on <c>ctf_truefort</c> at scales 3×0.80,
    /// 3×1 and 13×0.30.
    /// </remarks>
    public static SolidRect FromEntity(
        double x,
        double y,
        double xScale,
        double yScale,
        int spriteWidth,
        int spriteHeight,
        SolidKind kind,
        Team team = Team.Spectator)
    {
        var left = CollisionWorld.RoundPosition(x);
        var top = CollisionWorld.RoundPosition(y);

        return new SolidRect
        {
            Left = left,
            Right = CollisionWorld.RoundPosition(x + spriteWidth * xScale) - 1,
            Top = top,
            Bottom = CollisionWorld.RoundPosition(y + spriteHeight * yScale) - 1,
            Kind = kind,
            Team = team,
        };
    }
}
