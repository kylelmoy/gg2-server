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

        for (var row = firstRow; row <= lastRow; row++)
        for (var column = firstColumn; column <= lastColumn; column++)
            if (_mask.IsSolid(column, row))
                return false;

        return true;
    }

    /// <summary>
    /// <c>place_free</c> for a character described by
    /// <paramref name="filter"/> — the walkmask plus whichever solid rectangles
    /// are solid to it.
    /// </summary>
    public bool PlaceFree(in CharacterBox box, double x, double y, in CollisionFilter filter)
    {
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

        for (var row = firstRow; row <= lastRow; row++)
        for (var column = firstColumn; column <= lastColumn; column++)
            if (_mask.IsSolid(column, row))
                return false;

        // Small and rarely more than a dozen, so a linear scan beats any index
        // that would have to be rebuilt when a gate opens.
        foreach (var rect in _rects)
            if (rect.Overlaps(left, right, top, bottom) && rect.IsSolidTo(filter))
                return false;

        return true;
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
