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

    public CollisionWorld(LevelMap map)
    {
        ArgumentNullException.ThrowIfNull(map);

        Map = map;
        _mask = map.Walkmask;
    }

    public LevelMap Map { get; }

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
}
