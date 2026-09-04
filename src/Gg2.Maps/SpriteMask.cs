namespace Gg2.Maps;

/// <summary>
/// Which pixels of a map-supplied sprite are solid.
/// </summary>
/// <remarks>
/// A GM8 sprite added at run time collides <b>per pixel</b>, not by its bounding
/// box — measured against the running game, <c>docs/SIMULATION.md</c>, "Runtime
/// <c>sprite_add</c> sprites are precise". This is that mask: one bit per source
/// pixel, before any scaling.
/// <para>
/// <b>Only <c>MovingPlatform</c> needs this.</b> Everything else solid in GG2
/// is the 6x6 walkmask or an axis-aligned rectangle; a platform is the one object
/// whose shape is an arbitrary image the map author embedded. Do not reach for
/// this anywhere else without checking that the reference really is precise
/// there.
/// </para>
/// </remarks>
public sealed class SpriteMask
{
    private readonly bool[] _solid;

    public SpriteMask(int width, int height, bool[] solid)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentNullException.ThrowIfNull(solid);

        if (solid.Length != width * height)
            throw new ArgumentException($"expected {width * height} pixels, got {solid.Length}", nameof(solid));

        Width = width;
        Height = height;
        _solid = solid;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>How many pixels collide. Zero means the sprite is entirely transparent.</summary>
    public int SolidCount
    {
        get
        {
            var count = 0;
            foreach (var pixel in _solid)
                if (pixel)
                    count++;

            return count;
        }
    }

    /// <summary>Whether this source pixel collides. Outside the sprite is never solid.</summary>
    public bool IsSolid(int x, int y) =>
        (uint)x < (uint)Width && (uint)y < (uint)Height && _solid[(y * Width) + x];

    /// <summary>
    /// Decodes an embedded sprite resource into a mask.
    /// </summary>
    /// <param name="file">The raw file bytes out of the map's <c>meta</c> block.</param>
    /// <param name="removeBackground">
    /// GM8's <c>removeback</c>. <c>sprite_add(name, 1, 1, 0, 0, 0)</c> in
    /// <c>stringToResource.gml</c> passes 1, so this is always true for a map
    /// resource.
    /// </param>
    /// <remarks>
    /// <b>The format is sniffed the way the reference sniffs it</b>, not from
    /// the file's real magic: <c>stringToResource.gml:10-16</c> takes characters
    /// 1-3 and tests for <c>GIF</c>, then characters 2-4 and tests for <c>PNG</c>.
    /// The second is offset by one because a PNG's signature begins with 0x89.
    /// Anything else it returns -1 for, meaning no sprite and therefore no
    /// collision at all.
    /// </remarks>
    public static SpriteMask? Decode(ReadOnlySpan<byte> file, bool removeBackground = true)
    {
        if (file.Length >= 3 && file[0] == 'G' && file[1] == 'I' && file[2] == 'F')
            return GifSprite.Decode(file, removeBackground);

        if (file.Length >= 4 && file[1] == 'P' && file[2] == 'N' && file[3] == 'G')
            return PngSprite.Decode(file, removeBackground);

        // stringToResource returns -1, the platform keeps sprite_index = -1, and
        // nothing collides with it. `animap` in ctf_2dfort-ani is a real example:
        // nine bytes of truncated GIF.
        return null;
    }

    /// <summary>
    /// The colour GM8's <c>removeback</c> makes transparent: the
    /// <b>bottom-left</b> pixel.
    /// </summary>
    /// <remarks>
    /// <b>Bottom-left, not top-left, and this is the single most likely thing
    /// in this file to be wrong.</b> It is taken from Game Maker 8's own
    /// documentation for <c>sprite_add</c> ("all pixels with the background
    /// colour, that is the colour of the leftmost bottom pixel") rather than from
    /// anything measured here, and no probe has confirmed it against the running
    /// game. A sprite whose two corners differ in colour is the case that would
    /// expose it. See <c>docs/TRAPS-SIMULATION.md</c>, the verification plan.
    /// </remarks>
    public static int BackgroundPixelIndex(int width, int height) => (height - 1) * width;
}
