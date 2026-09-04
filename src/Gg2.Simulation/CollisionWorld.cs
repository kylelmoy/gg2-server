using Gg2.Maps;
using Gg2.Protocol;

namespace Gg2.Simulation;

/// <summary>
/// What is solid, and where.
/// </summary>
/// <remarks>
/// Read-only, built once per map, and passed <em>to</em> the tick rather than
/// held by the world state (docs/BOTAI.md, item 1). Everything solid in GG2 is
/// one of two things: the map's walkmask, and a handful of axis-aligned
/// rectangles.
/// <para>
/// <b>The boundary rule is measured, not assumed.</b> It was one of the two
/// open questions in docs/SIMULATION.md and was settled against the running
/// game — see that document's "Settled: the collision boundary rule". A cell
/// covers <c>[6c, 6c+6)</c> on both axes, and anything off the mask is open, on
/// the negative side and past the far edge alike.
/// </para>
/// </remarks>
public sealed class CollisionWorld
{
    private readonly Walkmask _mask;

    private readonly SolidRect[] _rects;

    public CollisionWorld(LevelMap map)
    {
        ArgumentNullException.ThrowIfNull(map);

        Map = map;
        _mask = map.Walkmask;
        _rects = [.. ColliderSprites.Build(map)];

        // Neither of these is solid to anything, so they are not in _rects: a
        // spawn room and a cabinet are regions to be asked about, not walls.
        // Unlike the capture zones they need nothing but the map, so they are
        // built here rather than handed in afterwards.
        // Fully qualified: the properties below shadow the two static classes
        // inside this type, and the properties are the better name at every
        // call site (world.Cabinets, world.SpawnRooms).
        _spawnRooms = Gg2.Simulation.SpawnRooms.Build(map);
        _cabinets = Gg2.Simulation.Cabinets.Build(map);
        _killBoxes = Gg2.Simulation.KillBoxes.Build(map);
        _moveBoxes = Gg2.Simulation.MoveBoxes.Build(map);
        _movingPlatforms = Gg2.Simulation.MovingPlatforms.Build(map);
    }

    public LevelMap Map { get; }

    /// <summary>
    /// The map's solid rectangles — gates, player walls — in level-data order.
    /// </summary>
    /// <remarks>
    /// Whether any given one is solid depends on who is asking; see
    /// <see cref="SolidRect.IsSolidTo"/>.
    /// </remarks>
    public IReadOnlyList<SolidRect> Rects => _rects;

    /// <summary>
    /// The same rectangles, as a span.
    /// </summary>
    /// <remarks>
    /// <b>The tick path must use this and not <see cref="Rects"/>.</b> A
    /// <c>foreach</c> over <c>IReadOnlyList&lt;T&gt;</c> boxes the enumerator,
    /// which is an allocation per character per tick — docs/BOTAI.md item 1's
    /// whole subject, and `BotAiConstraintsTests.ASteadyStateTickAllocatesNothing`
    /// caught it the moment the dropdown-platform scan landed.
    /// </remarks>
    public ReadOnlySpan<SolidRect> RectSpan => _rects;

    /// <summary>
    /// The map's <c>CaptureZone</c> rectangles, each tagged with the control
    /// point it belongs to.
    /// </summary>
    /// <remarks>
    /// Here rather than in <see cref="WorldState"/> because a zone is map
    /// geometry: it never moves, never opens and never changes hands, and the
    /// state has to stay a <c>memcpy</c>. Empty until
    /// <see cref="SetCaptureZones"/> is called — the zones need the control
    /// points to have been placed first, since each one is tagged with the
    /// nearest, and that ordering lives with the caller that loads the map.
    /// </remarks>
    public IReadOnlyList<CaptureZone> CaptureZones => _captureZones;

    private CaptureZone[] _captureZones = [];

    private readonly SpawnRoomRect[] _spawnRooms;

    private readonly HealingCabinet[] _cabinets;

    /// <summary>The map's <c>SpawnRoom</c> rectangles.</summary>
    public IReadOnlyList<SpawnRoomRect> SpawnRooms => _spawnRooms;

    /// <summary>The map's <c>HealingCabinet</c> rectangles and their switches.</summary>
    public IReadOnlyList<HealingCabinet> Cabinets => _cabinets;

    private readonly KillBoxRect[] _killBoxes;

    /// <summary>The map's <c>KillBox</c>, <c>PitFall</c> and <c>FragBox</c> rectangles.</summary>
    /// <remarks>
    /// Scanned across all 22 shipped maps: only <c>tdm_mantic</c> places any,
    /// and its six are all pit falls. See <see cref="KillBoxes"/>.
    /// </remarks>
    public IReadOnlyList<KillBoxRect> KillBoxes => _killBoxes;

    private readonly MoveBoxRect[] _moveBoxes;

    /// <summary>The map's <c>MoveBox</c> rectangles and which way each pushes.</summary>
    /// <remarks>
    /// Scanned across all 22 shipped maps: <c>tdm_mantic</c> alone, nine up and
    /// six down. See <see cref="MoveBoxes"/>.
    /// </remarks>
    public IReadOnlyList<MoveBoxRect> MoveBoxes => _moveBoxes;

    private readonly MovingPlatformDefinition[] _movingPlatforms;

    /// <summary>
    /// The map's <c>MovingPlatform</c> configuration, in level-data order.
    /// </summary>
    /// <remarks>
    /// <b>A span, and the tick path must use it rather than a list</b> — the
    /// same boxing-in-the-tick rule <see cref="RectSpan"/> carries.
    /// <para>
    /// <b>Zero on all 22 shipped maps</b>, so everything downstream of this is
    /// reachable only from a custom map or a hand-built one. That is why
    /// <c>MovingPlatformTests</c> builds its entities rather than loading one.
    /// </para>
    /// </remarks>
    public ReadOnlySpan<MovingPlatformDefinition> MovingPlatforms => _movingPlatforms;

    /// <summary>
    /// <c>collision_point(x, y, SpawnRoom, 0, 0) &gt;= 0</c>: is this
    /// character's <em>origin</em> inside a spawn room?
    /// </summary>
    /// <remarks>
    /// <b>The point, not the box</b>, and the reference asks both ways about
    /// the same object — see <see cref="MeetsSpawnRoom"/>. This is the form a
    /// class change and a sentry build use, and it is the same
    /// <c>collision_point</c> a capture zone uses: the origin is at the
    /// character's feet, so a character whose head is in the room and whose feet
    /// are not is <em>outside</em> it.
    /// </remarks>
    public bool PointInSpawnRoom(double x, double y)
    {
        var px = RoundPosition(x);
        var py = RoundPosition(y);

        foreach (var room in _spawnRooms)
            if (px >= room.Left && px <= room.Right && py >= room.Top && py <= room.Bottom)
                return true;

        return false;
    }

    /// <summary>
    /// <c>place_meeting(x, y, SpawnRoom)</c>: does this character's <em>box</em>
    /// overlap a spawn room?
    /// </summary>
    /// <remarks>
    /// The other half of the disagreement above. This is the form
    /// <c>Character.events/Destroy.xml:37</c> uses for the fast respawn, so the
    /// respawn is more generous than the class-change swap by exactly the height
    /// of a character.
    /// </remarks>
    public bool MeetsSpawnRoom(in CharacterBox box, double x, double y)
    {
        var character = Aabb.ForCharacter(box, x, y);

        foreach (var room in _spawnRooms)
            if (character.Overlaps(new Aabb(room.Left, room.Top, room.Right, room.Bottom)))
                return true;

        return false;
    }

    /// <summary>
    /// <c>place_meeting(x, y, HealingCabinet)</c>, which is
    /// <c>Character.events/End Step.xml:104</c>'s <c>onCabinet</c>.
    /// </summary>
    public bool MeetsCabinet(in CharacterBox box, double x, double y)
    {
        var character = Aabb.ForCharacter(box, x, y);

        foreach (var cabinet in _cabinets)
            if (character.Overlaps(new Aabb(cabinet.Left, cabinet.Top, cabinet.Right, cabinet.Bottom)))
                return true;

        return false;
    }

    /// <summary>
    /// Supplies the capture zones once the control points they name have been
    /// placed. See <see cref="ControlPoints.Place"/>.
    /// </summary>
    public void SetCaptureZones(CaptureZone[] zones) => _captureZones = zones ?? [];

    /// <summary>
    /// <c>map_width()</c> — the art's width, which is what the physics clamps
    /// positions into. Not the mask's extent; see <see cref="LevelMap.Width"/>.
    /// </summary>
    public int Width => Map.Width;

    public int Height => Map.Height;

    /// <summary>
    /// Whether the walkmask is solid at a world pixel.
    /// </summary>
    /// <remarks>
    /// <b>The floor here must be a floor, not a truncation.</b> C#'s <c>/</c> on
    /// integers rounds toward zero, so <c>-1 / 6 == 0</c> and a pixel one to the
    /// left of the map would read cell 0 — which is often solid. The live game
    /// answers "open" for every negative coordinate, so the conversion is an
    /// arithmetic shift, and the sign has to survive it.
    /// </remarks>
    public bool IsSolidAt(int worldX, int worldY) =>
        _mask.IsSolid(CellOf(worldX), CellOf(worldY));

    /// <summary>
    /// World pixel to mask cell: <c>floor(v / 6)</c>, correct for negatives.
    /// </summary>
    /// <remarks>
    /// The shift is the point. <c>v / 6</c> truncates toward zero in C# and in
    /// GML's <c>div</c>; GM8's own rasterisation floors, and the difference only
    /// shows up left of and above the map — which is exactly where a character
    /// gets pushed when something goes wrong.
    /// </remarks>
    public static int CellOf(int worldCoordinate)
    {
        // Six is not a power of two, so this is a real floored division rather
        // than a shift. Written out because `worldCoordinate / 6` is wrong and
        // looks right.
        var cell = worldCoordinate / GameConstants.CellSize;
        return worldCoordinate < 0 && cell * GameConstants.CellSize != worldCoordinate ? cell - 1 : cell;
    }

    /// <summary>
    /// The walkmask itself, for a caller that needs to sweep it rather than ask
    /// one question at a time.
    /// </summary>
    /// <remarks>
    /// <b>Exposed for observation extraction, not for the simulation.</b>
    /// Nothing in the tick reads this — the tick asks <see cref="IsSolidAt"/> and
    /// <see cref="PlaceFree"/>, which are the questions GM8 asks. It exists so
    /// <c>ObservationWriter</c>'s ray fan can hoist the mask and use
    /// <see cref="Gg2.Maps.Walkmask.Clearance"/>, which is a policy concern and
    /// deliberately not a collision one: a ray fan wants "how far", and this
    /// world's primitives answer "is anything in the way".
    /// </remarks>
    public Walkmask Mask => _mask;

    /// <summary>The world-pixel extent of the walkmask, which may differ from the map's.</summary>
    public int MaskWidth => _mask.WorldWidth;

    public int MaskHeight => _mask.WorldHeight;

    /// <summary>
    /// Whether <paramref name="box"/> placed at (<paramref name="x"/>,
    /// <paramref name="y"/>) touches nothing solid — GM8's <c>place_free</c>.
    /// </summary>
    /// <remarks>
    /// <b>The position is rounded to an integer first, half to even.</b> That is
    /// the measured rule, and it is the one thing about this function that
    /// cannot be guessed: a character is almost never on an integer coordinate,
    /// so getting it wrong is wrong nearly always, by a pixel, in a way that
    /// looks like ordinary netcode jitter.
    /// <para>
    /// Measured on the running game by sweeping a character box toward a wall a
    /// tenth of a pixel at a time. With a Heavy box (right offset +9) and a wall
    /// whose first solid pixel is 1572, the flip is between x = 1562.5 (free)
    /// and x = 1562.6 (blocked): the threshold is 1563, and 1562.5 rounding
    /// *down* to the even 1562 is what distinguishes half-to-even from half-up.
    /// The y axis behaves identically. Both are pinned by fixtures.
    /// </para>
    /// <para>
    /// Both ends of the box are inclusive: at x = 1562.6 the box's right edge
    /// lands exactly on 1572, the wall's first solid pixel, and that is already
    /// blocked.
    /// </para>
    /// <para>
    /// <b>Facing does not matter.</b> A character flips <c>image_xscale</c> to
    /// -1 when facing left (<c>Character.events/End Step.xml:55-60</c>), and
    /// GM8 then reports a <em>wider</em> <c>bbox_left</c> for it — a scout reads
    /// -8 instead of -6. That inflated box is a broad-phase bound and
    /// <c>place_free</c> does not use it: every class's mask rectangle is
    /// symmetric about its origin, so mirroring it changes nothing. Measured
    /// both ways against the same wall; the answers are identical. Do not
    /// "fix" this by reading <c>bbox_left</c>.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// <b>Map geometry only</b> — the walkmask, ignoring gates and player
    /// walls. Separate from <see cref="PlaceFree(in CharacterBox, double, double, in CollisionFilter)"/>
    /// rather than a default filter, because "which solids were enabled" is
    /// exactly the question a caller must not answer by accident: the reference
    /// brackets every check with <c>charSetSolids</c>/<c>charUnsetSolids</c>,
    /// and outside that bracket even the map is not solid.
    /// </remarks>
    public bool PlaceFreeInMask(in CharacterBox box, double x, double y)
    {
        var originX = RoundPosition(x);
        var originY = RoundPosition(y);

        var firstColumn = CellOf(originX + box.Left);
        var lastColumn = CellOf(originX + box.Right);
        var firstRow = CellOf(originY + box.Top);
        var lastRow = CellOf(originY + box.Bottom);

        // One masked word read per row, not one test per cell — see
        // Walkmask.AnySolidInBox, which is what the bitset layout was built for.
        return !_mask.AnySolidInBox(firstColumn, lastColumn, firstRow, lastRow);
    }

    /// <summary>
    /// <c>place_free</c> for a character described by
    /// <paramref name="filter"/> — the walkmask plus whichever solid rectangles
    /// are solid to it.
    /// </summary>
    public bool PlaceFree(in CharacterBox box, double x, double y, in CollisionFilter filter) =>
        PlaceFree(box, x, y, filter, default);

    /// <summary>
    /// <c>place_free</c> with one moving platform temporarily solid as well.
    /// </summary>
    /// <remarks>
    /// <b>Only the carry passes a non-empty collider</b>, and only around the
    /// one <c>good_move_contact_solid</c> the reference wraps in
    /// <c>other.solid = true</c>. A platform is not part of the world's solid set
    /// — it holds people up through the dropdown path instead — so it is passed
    /// in for a single call rather than held here.
    /// </remarks>
    public bool PlaceFree(
        in CharacterBox box, double x, double y, in CollisionFilter filter,
        in PreciseCollision.PlatformCollider platform)
    {
        if (platform.Meets(box, x, y))
            return false;

        var originX = RoundPosition(x);
        var originY = RoundPosition(y);

        var left = originX + box.Left;
        var right = originX + box.Right;
        var top = originY + box.Top;
        var bottom = originY + box.Bottom;

        // Inclusive on both ends, so the last cell is CellOf(right), not
        // CellOf(right) - 1.
        var firstColumn = CellOf(left);
        var lastColumn = CellOf(right);
        var firstRow = CellOf(top);
        var lastRow = CellOf(bottom);

        // One masked word read per row — Walkmask.AnySolidInBox. This is the
        // hottest of the four copies of this loop: the movement crawl asks it
        // once per pixel of travel.
        if (_mask.AnySolidInBox(firstColumn, lastColumn, firstRow, lastRow))
            return false;

        // Small and rarely more than a dozen, so a linear scan beats any index
        // that would have to be rebuilt when a gate opens.
        foreach (var rect in _rects)
            if (rect.Overlaps(left, right, top, bottom) && rect.IsSolidTo(filter))
                return false;

        return true;
    }

    /// <summary>
    /// <c>place_meeting(x, y, &lt;object&gt;)</c> for one family of map solid.
    /// </summary>
    /// <remarks>
    /// <b>Not <c>place_free</c>, and the difference is the whole point of the
    /// one caller.</b> <c>place_free</c> asks whether anything <em>solid to
    /// me</em> is in the way; this asks whether a named object's mask overlaps,
    /// solidity and team irrelevant. <c>Character.events/Collision with
    /// IntelligenceRed.xml</c> is the case that needs it —
    /// <c>!place_meeting(x, y, TeamGate)</c> refuses the grab to a player
    /// standing inside <em>any</em> team gate, including their own, which is
    /// non-solid to them and which <c>place_free</c> would therefore say nothing
    /// about.
    /// <para>
    /// The map's walkmask is not consulted: a <c>place_meeting</c> against an
    /// object is only ever about that object's instances. And
    /// <see cref="SolidKind"/> is the port's stand-in for the object family, so
    /// <see cref="SolidKind.TeamGate"/> here means exactly
    /// <c>RedTeamGate</c>, <c>BlueTeamGate</c> and their two horizontal
    /// variants — <c>IntelGate</c> is a <em>sibling</em> under <c>Gate</c> and
    /// is not one of them.
    /// </para>
    /// </remarks>
    public bool PlaceMeetingKind(in CharacterBox box, double x, double y, SolidKind kind)
    {
        var originX = RoundPosition(x);
        var originY = RoundPosition(y);

        var left = originX + box.Left;
        var right = originX + box.Right;
        var top = originY + box.Top;
        var bottom = originY + box.Bottom;

        foreach (var rect in _rects)
            if (rect.Kind == kind && rect.Overlaps(left, right, top, bottom))
                return true;

        return false;
    }

    /// <summary>
    /// <c>place_free</c> for an instance whose bounding box is already known.
    /// </summary>
    /// <remarks>
    /// <b>The box, not a position and an offset.</b> A character's box is a
    /// fixed rectangle around its position, so
    /// <see cref="PlaceFree(in CharacterBox, double, double, in CollisionFilter)"/>
    /// can take the position and build it; a projectile's is the rotated bound
    /// of its sprite (<see cref="Aabb.ForRotatedSprite"/>) and there is no
    /// offset that describes it. The position rounding has already happened by
    /// the time a box exists, which is why none is applied here.
    /// <para>
    /// This is what <c>Shot.events/Collision with Character.xml</c> opens with,
    /// bracketed by <c>gunSetSolids</c> — so pass
    /// <see cref="CollisionFilter.ForBullet"/>, not a character's filter.
    /// </para>
    /// </remarks>
    public bool PlaceFree(in Aabb box, in CollisionFilter filter)
    {
        var firstColumn = CellOf(box.Left);
        var lastColumn = CellOf(box.Right);
        var firstRow = CellOf(box.Top);
        var lastRow = CellOf(box.Bottom);

        // One masked word read per row — Walkmask.AnySolidInBox. This is the
        // hottest of the four copies of this loop: the movement crawl asks it
        // once per pixel of travel.
        if (_mask.AnySolidInBox(firstColumn, lastColumn, firstRow, lastRow))
            return false;

        foreach (var rect in _rects)
            if (rect.Overlaps(box.Left, box.Right, box.Top, box.Bottom) && rect.IsSolidTo(filter))
                return false;

        return true;
    }

    /// <summary>
    /// Whether any solid that is <em>not</em> an <see cref="SolidKind.Obstacle"/>
    /// blocks this box.
    /// </summary>
    /// <remarks>
    /// <b><c>gunSetSolids</c> minus the walls</b>, which is exactly the set of
    /// map objects a <c>Bubble</c> has a destroying collision event for:
    /// <c>TeamGate</c>, <c>BulletWall</c>, <c>IntelGate</c> and
    /// <c>ControlPointSetupGate</c>. Its <c>Collision with Obstacle</c> is a
    /// slide rather than a death, so the two halves have to be asked
    /// separately — see <see cref="Projectiles"/>.
    /// <para>
    /// The map's own mask is not consulted for the same reason: it is a
    /// <c>CustomMapO</c>, and <c>CustomMapO</c> descends from <c>Obstacle</c>.
    /// Solidity still comes from <see cref="SolidRect.IsSolidTo"/> rather than
    /// from the object family, because the reference's four events ask exactly
    /// the questions that predicate already answers for a bullet — including
    /// that an intel gate stays shut while the map is changing.
    /// </para>
    /// </remarks>
    public bool MeetsNonObstacleSolid(in Aabb box, in CollisionFilter filter)
    {
        foreach (var rect in _rects)
        {
            if (rect.Kind == SolidKind.Obstacle)
                continue;

            if (rect.Overlaps(box.Left, box.Right, box.Top, box.Bottom) && rect.IsSolidTo(filter))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The map's walkmask as a <see cref="CollisionLine"/> mask lookup.
    /// </summary>
    /// <remarks>
    /// A struct so <see cref="CollisionLine.Hits{TMask}"/> stays allocation-free
    /// and the lookup inlines. It holds a reference, which is fine here and
    /// would not be in <see cref="WorldState"/> — this is a read-only input
    /// passed <em>to</em> a query, never state (docs/BOTAI.md, item 1).
    /// </remarks>
    public readonly struct WalkmaskLookup(Walkmask mask) : CollisionLine.IMask
    {
        public bool IsSolid(int x, int y) => mask.IsSolid(x, y);

        /// <summary>
        /// The map's clearance field, which is what makes a long line test cheap.
        /// See <see cref="CollisionLine.IMask.SafeSkipPixels"/> and
        /// <see cref="Walkmask.SafeSkipPixels"/>.
        /// </summary>
        /// <remarks>
        /// <b>The bounds test cannot fire through <see cref="MapCollider"/>
        /// today and is kept anyway</b>, which was checked rather than assumed:
        /// that collider's mask box is <c>[0, Width - 1] × [0, Height - 1]</c>,
        /// exactly the walkmask's extent, and <c>CollisionLine.Walk</c> tests
        /// against it before asking. It stays because <c>IMask</c> makes no
        /// promise that its caller bounds-checked — the same reason
        /// <see cref="Walkmask.IsSolid"/> carries its own off-mask rule — and
        /// because an out-of-range read here would be a silently wrong skip
        /// rather than a crash.
        /// </remarks>
        public int SafeSkipPixels(int x, int y)
        {
            if ((uint)x >= (uint)mask.Width || (uint)y >= (uint)mask.Height)
                return 0;

            return Walkmask.SafeSkipPixels(mask.Clearance[(y * mask.Width) + x]);
        }
    }

    /// <summary>
    /// The map's collision sprite, as GM8 sees it.
    /// </summary>
    /// <remarks>
    /// One instance of <c>CustomMapO</c> at the origin with
    /// <c>image_xscale = image_yscale = 6</c>, whose sprite is built at runtime
    /// from the map's walkmask
    /// (<c>Scripts/Maps/CustomMaps/CustomMapProcessLevelData.gml:22</c>). Read
    /// out of the running game: position (0, 0), origin (0, 0), and a bounding
    /// box of <c>[0, 6·width) × [0, 6·height)</c> — which is the mask's extent,
    /// not <see cref="Width"/>.
    /// <para>
    /// <c>CustomMapO</c> descends from <c>CollisionMapO</c> and so from
    /// <c>Obstacle</c>, which is why <c>collision_line_bulletblocking</c>'s
    /// first line — <c>collision_line(..., Obstacle, 1, 0)</c> — is what stops
    /// a bullet at a wall even though the map is never named.
    /// </para>
    /// </remarks>
    public LineCollider MapCollider => new(
        0,
        0,
        GameConstants.CellSize,
        GameConstants.CellSize,
        new Aabb(0, 0, MaskWidth - 1, MaskHeight - 1),
        0,
        0,
        new Aabb(0, 0, _mask.Width - 1, _mask.Height - 1));

    /// <summary>
    /// <c>collision_line</c> against the map's walkmask.
    /// </summary>
    /// <remarks>
    /// <b>Map geometry only</b>, the same split as
    /// <see cref="PlaceFreeInMask"/>: gates and bullet walls are separate
    /// instances and the reference asks about them separately, in the order
    /// <c>collision_line_bulletblocking</c> lists them.
    /// </remarks>
    public bool CollisionLineHitsMask(double x1, double y1, double x2, double y2) =>
        CollisionLine.Hits(MapCollider, new WalkmaskLookup(_mask), x1, y1, x2, y2);

    /// <summary>
    /// <c>place_meeting(x, y, Obstacle)</c> for a box: the map, and nothing
    /// else.
    /// </summary>
    /// <remarks>
    /// The <see cref="Aabb"/> counterpart of
    /// <see cref="PlaceFreeInMask(in CharacterBox, double, double)"/>, and it
    /// exists for one caller: a mine landing on a wall.
    /// <c>move_all_bullets.gml</c> brackets that with
    /// <c>gunUnsetSolids(); wallSetSolid();</c> — and
    /// <c>Scripts/Physics/wallSetSolid.gml</c> is one line,
    /// <c>with(Obstacle) solid = true</c>. So a mine backs out of the map and of
    /// nothing else: it does not stick to a gate or to a bullet wall, it dies
    /// against them.
    /// </remarks>
    public bool PlaceFreeInMask(in Aabb box)
    {
        var firstColumn = CellOf(box.Left);
        var lastColumn = CellOf(box.Right);
        var firstRow = CellOf(box.Top);
        var lastRow = CellOf(box.Bottom);

        // One masked word read per row, not one test per cell — see
        // Walkmask.AnySolidInBox, which is what the bitset layout was built for.
        return !_mask.AnySolidInBox(firstColumn, lastColumn, firstRow, lastRow);
    }

    /// <summary>
    /// <c>Scripts/Physics/collision_line_bulletblocking.gml</c>: is there
    /// anything between these two points that a bullet cannot pass?
    /// </summary>
    /// <remarks>
    /// The reference asks five questions and returns on the first yes —
    /// <c>Obstacle</c>, <c>BulletWall</c>, <c>IntelGate</c>, <c>TeamGate</c>
    /// (unless the map is changing) and <c>ControlPointSetupGate</c> (while the
    /// setup gates are shut). The order is not observable, because the result is
    /// a boolean, so this asks the map and then every solid rectangle the
    /// <em>bullet's</em> filter says is solid — which is exactly those four
    /// objects and, correctly, not <c>PlayerWall</c>.
    /// <para>
    /// <b>The rectangles were the part that had to be measured before it could
    /// be written.</b> A rectangle is an instance rather than map geometry, so
    /// it needs its own <see cref="LineCollider"/>, and building one means
    /// claiming its sprite mask fills its bounding box. Measured against the
    /// running game: every point inside the reported box of all
    /// eight solid level-entity types is solid to a precise
    /// <c>collision_point</c> — twenty instances at scales from 0.3 to 13,
    /// 100% filled in every case. See <see cref="LineCollider.ForSolidRect"/>.
    /// </para>
    /// <para>
    /// <c>global.mapchanging</c> is not modelled. It is true only between a
    /// <c>CHANGE_MAP</c> and the new map finishing its load, during which no
    /// weapon can fire because no character exists.
    /// </para>
    /// </remarks>
    public bool CollisionLineBlocksBullets(double x1, double y1, double x2, double y2, in CollisionFilter filter)
    {
        if (CollisionLineHitsMask(x1, y1, x2, y2))
            return true;

        for (var i = 0; i < _rects.Length; i++)
        {
            if (RectBlocks(i, x1, y1, x2, y2, in filter))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The same question against a <em>pre-filtered</em> set of rect indices, for
    /// a caller asking about many segments that share a bounding box.
    /// </summary>
    /// <remarks>
    /// <b>The body is the same one</b> — <see cref="RectBlocks"/> — so this
    /// cannot answer differently from the overload above; it only asks fewer
    /// rects. Fill <paramref name="candidates"/> with
    /// <see cref="BulletRectsIn"/> over a box that contains every segment, and
    /// the answer is identical: a rect that can meet a segment must overlap any
    /// box the segment sits inside.
    /// <para>
    /// <b>Worth it only for many segments.</b> <c>BotArc.Clear</c> asks about
    /// ten segments of one parabola, so the rect set would otherwise be walked
    /// ten times over — and a <see cref="LineCollider"/> built per rect per
    /// segment only to be thrown away. Measured: the rect half of a twelve-bot
    /// tick, 0.60 µs, is almost all of it that.
    /// </para>
    /// </remarks>
    public bool CollisionLineBlocksBullets(
        double x1, double y1, double x2, double y2, in CollisionFilter filter, ReadOnlySpan<int> candidates)
    {
        if (CollisionLineHitsMask(x1, y1, x2, y2))
            return true;

        foreach (var i in candidates)
        {
            if (RectBlocks(i, x1, y1, x2, y2, in filter))
                return true;
        }

        return false;
    }

    /// <summary>One solid rect against one segment. The one body.</summary>
    /// <remarks>
    /// The <see cref="CollisionLine.MightMeet"/> call is not redundant with
    /// the one inside <see cref="CollisionLine.Hits"/>: it is the same test, made
    /// before a <see cref="LineCollider"/> is built for a rect on the far side of
    /// the map rather than after.
    /// </remarks>
    private bool RectBlocks(int index, double x1, double y1, double x2, double y2, in CollisionFilter filter)
    {
        ref readonly var rect = ref _rects[index];

        if (!rect.IsSolidTo(filter))
            return false;

        if (!CollisionLine.MightMeet(new Aabb(rect.Left, rect.Top, rect.Right, rect.Bottom), x1, y1, x2, y2))
            return false;

        return CollisionLine.Hits(
            LineCollider.ForSolidRect(rect), default(CollisionLine.FilledMask), x1, y1, x2, y2);
    }

    /// <summary>How many solid rects the map has, for sizing a candidate span.</summary>
    public int RectCount => _rects.Length;

    /// <summary>
    /// The indices of every rect that could block a bullet anywhere inside the
    /// given bounds.
    /// </summary>
    /// <remarks>
    /// <b>Conservative by construction and therefore exact</b>: a rect that can
    /// meet a segment must overlap any box that segment lies inside, so filtering
    /// on the enclosing box cannot drop a rect that would have blocked. The same
    /// <see cref="CollisionLine.MightMeet"/> body decides both.
    /// </remarks>
    public int BulletRectsIn(
        double left, double top, double right, double bottom, in CollisionFilter filter, Span<int> into)
    {
        var count = 0;

        for (var i = 0; i < _rects.Length && count < into.Length; i++)
        {
            ref readonly var rect = ref _rects[i];

            if (!rect.IsSolidTo(filter))
                continue;

            if (!CollisionLine.MightMeet(
                    new Aabb(rect.Left, rect.Top, rect.Right, rect.Bottom),
                    left, top, right, bottom))
            {
                continue;
            }

            into[count++] = i;
        }

        return count;
    }

    /// <summary>
    /// The integer position GM8 collides at, for a real one.
    /// </summary>
    /// <remarks>
    /// Round half to even — the same rule as GML's <c>round()</c> and .NET's
    /// default, and <em>not</em> the same as the networking extension's
    /// half-away-from-zero (see <c>Gg2.Protocol.WireQuantise</c>). Three
    /// roundings in one codebase, and they are not all the same one.
    /// </remarks>
    public static int RoundPosition(double value) => (int)Math.Round(value, MidpointRounding.ToEven);
}
