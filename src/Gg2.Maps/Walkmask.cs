using System.Globalization;
using Gg2.Protocol;

namespace Gg2.Maps;

/// <summary>
/// A map's collision mask: one bit per cell, one cell per
/// <see cref="GameConstants.CellSize"/>-square block of world pixels.
/// </summary>
/// <remarks>
/// Stored as a bitset with a word stride per row, because
/// <c>PlaceFree</c> is the hottest call in the server and a character rectangle
/// spans only a handful of cells — the test wants to be a few masked word reads,
/// not a byte loop (docs/ARCHITECTURE.md, "Collision").
/// <para>
/// <b>Solidity only.</b> Gates, player walls and drop-through platforms are
/// entities, not mask cells: a cell this calls open can still be closed to a
/// character. See <see cref="LevelMap.Entities"/>.
/// </para>
/// </remarks>
public sealed class Walkmask
{
    private readonly ulong[] _words;

    /// <summary>Words per row. Rows are word-aligned so a row never straddles.</summary>
    private readonly int _stride;

    private Walkmask(int width, int height, ulong[] words, int stride)
    {
        Width = width;
        Height = height;
        _words = words;
        _stride = stride;
    }

    /// <summary>Mask width in cells.</summary>
    public int Width { get; }

    /// <summary>Mask height in cells.</summary>
    public int Height { get; }

    /// <summary>Map width in world pixels.</summary>
    public int WorldWidth => Width * GameConstants.CellSize;

    /// <summary>Map height in world pixels.</summary>
    public int WorldHeight => Height * GameConstants.CellSize;

    /// <summary>
    /// Whether the cell at (<paramref name="x"/>, <paramref name="y"/>) is
    /// solid. Off-mask is open, matching <c>walkmask.js</c>'s <c>solid()</c>.
    /// </summary>
    /// <remarks>
    /// Whether off-mask should be open is a <em>collision</em> question, not a
    /// decoding one — the reference clamps characters into the map in
    /// <c>Character.events/End Step.xml</c> rather than walling the edges. Phase
    /// 3 settles it; this accessor only reports what the mask holds.
    /// </remarks>
    public bool IsSolid(int x, int y)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
            return false;

        return (_words[y * _stride + (x >> 6)] & (1UL << (x & 63))) != 0;
    }

    /// <summary>
    /// Decodes the <c>{WALKMASK}</c> block of a map's level data.
    /// </summary>
    /// <remarks>
    /// Encoding, from <c>Scripts/Maps/CustomMaps/compressWalkmask.gml</c>:
    /// <code>
    /// {WALKMASK}\n&lt;width&gt;\n&lt;height&gt;\n&lt;data&gt;\n{END WALKMASK}
    /// </code>
    /// <c>data</c> is one continuous row-major bitstream, six bits per
    /// character, most significant bit first, each character stored as
    /// <c>chr(value + 32)</c>. Rows are <b>not</b> padded — the stream runs on
    /// from one row into the next, and only the final character is zero-padded.
    /// </remarks>
    public static Walkmask Decode(string levelData)
    {
        var block = LevelData.GetBlock(levelData, "WALKMASK");

        // width\nheight\ndata — the data itself never contains a newline, since
        // every byte of it is chr(0..63 + 32), i.e. printable ASCII.
        var firstBreak = block.IndexOf('\n');
        var secondBreak = firstBreak < 0 ? -1 : block.IndexOf('\n', firstBreak + 1);
        if (secondBreak < 0)
            throw new MapFormatException("{WALKMASK} block has no width/height header");

        var width = ParseDimension(block.AsSpan(0, firstBreak), "width");
        var height = ParseDimension(block.AsSpan(firstBreak + 1, secondBreak - firstBreak - 1), "height");
        var packed = block.AsSpan(secondBreak + 1);

        return FromBitstream(width, height, packed);
    }

    private static int ParseDimension(ReadOnlySpan<char> text, string name)
    {
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value <= 0)
            throw new MapFormatException($"{{WALKMASK}} {name} '{text}' is not a positive integer");
        return value;
    }

    private static Walkmask FromBitstream(int width, int height, ReadOnlySpan<char> packed)
    {
        var stride = (width + 63) / 64;
        var wordCount = (long)stride * height;
        if (wordCount > int.MaxValue)
            throw new MapFormatException($"{width}x{height} mask is too large");

        var words = new ulong[(int)wordCount];
        var total = (long)width * height;

        long at = 0;
        int x = 0, y = 0;

        foreach (var c in packed)
        {
            if (at >= total)
                break;

            var six = c - 32;
            if ((uint)six > 63)
                throw new MapFormatException(
                    $"{{WALKMASK}} data contains U+{(int)c:X4}, which is not chr(value + 32) for a 6-bit value");

            for (var b = 5; b >= 0 && at < total; b--, at++)
            {
                if (((six >> b) & 1) != 0)
                    words[y * stride + (x >> 6)] |= 1UL << (x & 63);

                if (++x == width)
                {
                    x = 0;
                    y++;
                }
            }
        }

        if (at < total)
            throw new MapFormatException($"walkmask is short: {at} of {total} cells decoded");

        return new Walkmask(width, height, words, stride);
    }

    /// <summary>
    /// One byte per cell, row-major, 1 where solid — the layout
    /// <c>gg2-agent/tools/walkmask.js</c> produces.
    /// </summary>
    /// <remarks>
    /// Exists so the parity fixture can compare this decoder against that one
    /// cell-for-cell (docs/PLAN.md, phase 0 exit criterion). Not a hot path.
    /// </remarks>
    public byte[] ToCellBytes()
    {
        var bytes = new byte[Width * Height];
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
            bytes[y * Width + x] = IsSolid(x, y) ? (byte)1 : (byte)0;
        return bytes;
    }

    /// <summary>Number of solid cells. Used by fixtures and by map summaries.</summary>
    public int SolidCount()
    {
        var count = 0;
        foreach (var word in _words)
            count += System.Numerics.BitOperations.PopCount(word);
        return count;
    }
}
