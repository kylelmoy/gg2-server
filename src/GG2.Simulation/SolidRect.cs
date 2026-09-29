using GG2.Protocol;

namespace GG2.Simulation;

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

    /// <summary>
    /// <c>DropdownPlatform</c> — <b>never solid, to anything.</b>
    /// </summary>
    /// <remarks>
    /// It is in the rectangle list purely so <c>PlaceMeetingKind</c> can find
    /// it, and <see cref="IsSolidFor"/> deliberately has no case for it: the
    /// object's own <c>solid</c> is false and it is not in <c>charSetSolids</c>,
    /// so <c>place_free</c> passes straight through one in every direction. A
    /// character stands on it only through <c>CharacterPhysics</c>'s two
    /// explicit dropdown branches — the Begin Step ground test and the Step
    /// push-out — which is why adding a case here would break jumping up through
    /// one.
    /// </remarks>
    DropdownPlatform,

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
    /// <c>global.mapchanging</c>: the round is over and the win is on screen.
    /// </summary>
    /// <remarks>
    /// <b>Every gate opens, and the two lists disagree about which.</b>
    /// <c>charSetSolids.gml:4</c> wraps <em>both</em> its <c>IntelGate</c> and
    /// <c>TeamGate</c> blocks in <c>if(!global.mapchanging)</c>, so to a
    /// character neither kind is solid; <c>gunSetSolids.gml:5</c> wraps only its
    /// <c>TeamGate</c> block and leaves <c>with(IntelGate) solid = true</c>
    /// outside it, so to a <b>bullet an intel gate stays solid</b> through the
    /// whole victory screen. Obstacles, player walls and setup gates are
    /// untouched by either — the world does not open, only its doors.
    /// </remarks>
    public bool MapChanging { get; init; }

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

    /// <summary>
    /// The asker sees only <see cref="SolidKind.Obstacle"/> —
    /// <c>Scripts/Physics/wallSetSolid.gml</c>, which is <c>with(Obstacle)
    /// solid = true</c> and nothing else.
    /// </summary>
    /// <remarks>
    /// The sentry's list. A falling sentry passes through player walls, bullet
    /// walls and every gate, and lands only on the map and its obstacles —
    /// neither <c>charSetSolids</c> nor <c>gunSetSolids</c> describes it, which
    /// is why this is a third flag rather than a combination of the others.
    /// </remarks>
    public bool WallsOnly { get; init; }

    /// <summary>A filter that collides with everything that can ever be solid.</summary>
    public static CollisionFilter Everything => new()
    {
        Team = Team.Spectator,
        CarryingIntel = true,
        SetupGatesClosed = true,
    };

    /// <summary>
    /// What a sentry collides with while placed or falling —
    /// <c>wallSetSolid</c>'s list, which is <c>Obstacle</c> alone.
    /// </summary>
    public static CollisionFilter ForSentry() => new()
    {
        // Only the Obstacle rule ever fires under WallsOnly, and it reads
        // neither the team nor the intel flag.
        Team = Team.Spectator,
        CarryingIntel = false,
        WallsOnly = true,
    };

    /// <summary>
    /// What a projectile or a hitscan collides with —
    /// <c>gunSetSolids</c>'s list.
    /// </summary>
    /// <param name="setupGatesClosed">Whether the setup timer is still running.</param>
    /// <param name="mapChanging">
    /// Whether the round is over and the win is on screen, which opens a team
    /// gate to bullets and — unlike a character's list — leaves an intel gate
    /// shut. See <see cref="MapChanging"/>.
    /// </param>
    public static CollisionFilter ForBullet(bool setupGatesClosed = false, bool mapChanging = false) => new()
    {
        // The team and the intel flag are read only by the gate rules, and a
        // bullet's gates ignore both — so any value here is the same answer.
        Team = Team.Spectator,
        CarryingIntel = false,
        SetupGatesClosed = setupGatesClosed,
        MapChanging = mapChanging,
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

    // --- what a hitscan needs, and a place_free does not ---------------------
    //
    // place_free only ever asks about the box, which the four bounds above
    // already are. collision_line walks pixels and looks each one up in the
    // *mask*, mapping world to sprite through the instance's position and
    // scale — so it needs the instance rather than its bound. See
    // LineCollider.ForSolidRect.
    //
    // Free, unlike everything on ProjectileState: a SolidRect lives in
    // CollisionWorld, which is a read-only input passed *to* the tick and never
    // held by WorldState, so none of this is cloned by a rollout.

    /// <summary>The instance's unrounded <c>x</c>. GM8 rounds it itself.</summary>
    public required double X { get; init; }

    /// <summary>The instance's unrounded <c>y</c>.</summary>
    public required double Y { get; init; }

    /// <summary>
    /// <c>image_xscale</c>. Fractional on real maps — 0.3, 0.8, 1.7, 13.
    /// </summary>
    /// <remarks>
    /// <b>Required, not defaulted, because a zero would divide.</b>
    /// <see cref="CollisionLine"/> maps a world pixel to a mask pixel by
    /// dividing by this, so a construction site that forgot it would not fail
    /// loudly — it would report an infinite mask index and quietly answer "no
    /// wall" for every hitscan.
    /// </remarks>
    public required double XScale { get; init; }

    /// <inheritdoc cref="XScale"/>
    public required double YScale { get; init; }

    /// <summary>The mask sprite's width in sprite pixels, before scaling.</summary>
    /// <remarks>
    /// Every solid a map places wears one of two sprites the game builds at
    /// runtime: a 6x60 vertical bar and a 60x6 horizontal one. Both have their
    /// origin at (0, 0) and a mask that fills the sprite exactly — measured
    /// across eight object types and twenty instances, at scales from 0.3 to 13,
    /// with every point inside the reported bounding box solid to a precise
    /// <c>collision_point</c>. That is what lets this be two integers
    /// instead of sprite metadata.
    /// </remarks>
    public required int SpriteWidth { get; init; }

    /// <inheritdoc cref="SpriteWidth"/>
    public required int SpriteHeight { get; init; }

    /// <summary>Whether this rectangle is solid to the character described by the filter.</summary>
    public bool IsSolidTo(in CollisionFilter filter) => filter.WallsOnly
        ? Kind == SolidKind.Obstacle
        : Kind switch
    {
        SolidKind.Obstacle => true,

        // charSetSolids sets it; gunSetSolids does not mention it at all.
        SolidKind.PlayerWall => !filter.Bullet,

        // gunSetSolids: `with(TeamGate) solid = true` — unconditionally, where
        // charSetSolids asks (team != other.team or other.intel). Both lists put
        // this one inside `if(!global.mapchanging)`, so it opens to everybody
        // while the win is on screen.
        SolidKind.TeamGate => !filter.MapChanging
                              && (filter.Bullet || Team != filter.Team || filter.CarryingIntel),

        // Likewise: solid = (team != other.team and other.intel) for a
        // character, and simply solid for a bullet.
        //
        // But only charSetSolids' half is inside the mapchanging guard.
        // gunSetSolids opens its TeamGate block and leaves `with(IntelGate)
        // solid = true` outside it, so a bullet is still stopped by an intel
        // gate during the victory screen and a character walks through it.
        SolidKind.IntelGate => filter.Bullet
                               || (!filter.MapChanging && Team != filter.Team && filter.CarryingIntel),

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
            X = x,
            Y = y,
            XScale = xScale,
            YScale = yScale,
            SpriteWidth = spriteWidth,
            SpriteHeight = spriteHeight,
        };
    }
}
