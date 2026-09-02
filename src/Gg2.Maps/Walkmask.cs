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

    /// <summary><see cref="Clearance"/>, built with the mask. See that property.</summary>
    private readonly byte[] _clearance;

    /// <summary>Words per row. Rows are word-aligned so a row never straddles.</summary>
    private readonly int _stride;

    private Walkmask(int width, int height, ulong[] words, int stride)
    {
        Width = width;
        Height = height;
        _words = words;
        _stride = stride;
        _clearance = BuildClearance();
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
    /// Whether any cell in the inclusive rectangle is solid — the same question
    /// as <see cref="IsSolid"/> over every cell in it, one masked word read per
    /// row instead of one test per cell.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>This is what the bitset was built for and for six phases nothing
    /// called it.</b> This class's own summary has said since phase 3 that the
    /// storage is a word-strided bitset "because <c>PlaceFree</c> is the hottest
    /// call in the server and a character rectangle spans only a handful of cells
    /// — the test wants to be a few masked word reads, not a byte loop". Both
    /// <c>CollisionWorld.PlaceFreeInMask</c> overloads were the byte loop. The
    /// description was true of the layout and false of every consumer, which is
    /// CLAUDE.md's "a claim the repo makes about itself" wearing a performance
    /// hat: nothing could fail, because the loop was correct.
    /// <para>
    /// ⚠️ <b>Measured, 2026-09-02, Release, a soldier on <c>ctf_truefort</c>:</b>
    /// a box test visits <b>18 cells over 6 rows</b> and its three columns land
    /// in ONE word per row, so this reads 6 words where the loop ran 18 tests.
    /// <c>PlaceFreeInMask</c> 17.1 ns → 6.8 ns, and a 10px crawl — which calls it
    /// about thirteen times — 310 ns → 179 ns.
    /// </para>
    /// <para>
    /// ⚠️ <b>Exactly the same predicate, including off-mask.</b> Out-of-range is
    /// OPEN, as <see cref="IsSolid"/> has it, so the rectangle is clamped rather
    /// than rejected and a box hanging off the edge of the map reports only what
    /// the cells inside say. <c>WalkmaskBoxTests</c> asserts the equivalence
    /// against the naive loop over every shipped map, edges included, rather than
    /// leaving it to reading.
    /// </para>
    /// <para>
    /// ⚠️ <b>Bits above <see cref="Width"/> in a row's last word are excluded by
    /// the clamp</b>, so this does not depend on the decoder leaving them zero —
    /// a dependency that would be invisible until a map whose width is not a
    /// multiple of 64 had junk there.
    /// </para>
    /// </remarks>
    public bool AnySolidInBox(int firstColumn, int lastColumn, int firstRow, int lastRow)
    {
        // Clamp rather than reject: off-mask is open, so a box overhanging the
        // map asks only about the part that exists.
        var c0 = firstColumn < 0 ? 0 : firstColumn;
        var c1 = lastColumn >= Width ? Width - 1 : lastColumn;
        var r0 = firstRow < 0 ? 0 : firstRow;
        var r1 = lastRow >= Height ? Height - 1 : lastRow;

        if (c0 > c1 || r0 > r1)
            return false;

        var firstWord = c0 >> 6;
        var lastWord = c1 >> 6;

        // The bits of the span's first and last word that are inside it. When the
        // span lies in one word — the common case, because three columns straddle
        // a 64-cell boundary about one position in twenty — the two are ANDed.
        var lowMask = ulong.MaxValue << (c0 & 63);
        var highMask = ulong.MaxValue >> (63 - (c1 & 63));

        var words = _words;

        if (firstWord == lastWord)
        {
            var mask = lowMask & highMask;

            for (var row = r0; row <= r1; row++)
            {
                if ((words[(row * _stride) + firstWord] & mask) != 0)
                    return true;
            }

            return false;
        }

        for (var row = r0; row <= r1; row++)
        {
            var at = row * _stride;

            if ((words[at + firstWord] & lowMask) != 0)
                return true;

            for (var word = firstWord + 1; word < lastWord; word++)
            {
                if (words[at + word] != 0)
                    return true;
            }

            if ((words[at + lastWord] & highMask) != 0)
                return true;
        }

        return false;
    }

    /// <summary>
    /// For every cell, the Chebyshev distance in cells to the nearest solid
    /// cell — 0 where the cell is itself solid, <see cref="MaxClearance"/> where
    /// nothing solid is within that many cells. Row-major, <c>Width</c> stride.
    /// </summary>
    /// <remarks>
    /// <b>An acceleration structure for marching a ray, and nothing else reads
    /// it.</b> <c>ObservationWriter</c>'s geometry fan samples the mask every
    /// four pixels out to 320, which is up to 80 probes a ray and 24 rays an
    /// observation — <b>88% of the cost of writing one</b>, and an observation
    /// is written for every agent on every decision.
    /// <para>
    /// ⚠️ <b>It makes the march skip, never guess.</b> A clearance of <c>c</c>
    /// says every cell within Chebyshev distance <c>c - 1</c> is open, so every
    /// world pixel within <c>6c - 7</c> of the probe is open too — one cell of
    /// slack for the probe's own position inside its cell. A marcher that
    /// advances by no more than that lands on open ground by construction, so
    /// the first solid probe it finds is the first solid probe there is. The
    /// answer is <b>identical</b> to the naive march, not an approximation of
    /// it; <c>ObservationRayFanTests</c> asserts that against a transcription of
    /// the naive loop.
    /// </para>
    /// <para>
    /// ⚠️ <b>Built WITH the mask, and it was lazy first.</b> "A game server never
    /// marches a ray and should not pay <c>Width * Height</c> bytes for a field
    /// it will not read" is true and is the wrong trade. Lazy meant the
    /// <em>first</em> <c>ObservationWriter.Write</c> on a map allocated, which
    /// broke that type's zero-allocation gate — and broke it
    /// <b>order-dependently</b>, because the <see cref="Walkmask"/> is shared
    /// through <c>ScenarioResources</c>, so whether the field already existed
    /// depended on which test had run first. It passed twice before failing.
    /// </para>
    /// <para>
    /// Eager costs one pass and <c>Width * Height</c> bytes per map — 160 KB and
    /// about 0.3 ms for <c>ctf_truefort</c>, once, on a load that is itself
    /// cached. <b>An unconditional property is worth more than that.</b>
    /// </para>
    /// </remarks>
    public ReadOnlySpan<byte> Clearance => _clearance;

    /// <summary>
    /// The value <see cref="Clearance"/> saturates at. ⚠️ <b>A cap, so a reader
    /// must treat it as "at least this", never "exactly this"</b> — which is all
    /// a marcher needs, since it only ever skips forward by what the value
    /// guarantees.
    /// </summary>
    /// <remarks>
    /// 48 cells is 288 world pixels, which covers the 320-pixel ray range in a
    /// single skip from anywhere genuinely open. Raising it would buy nothing
    /// and would stop the field fitting in a byte.
    /// </remarks>
    public const byte MaxClearance = 48;

    private byte[] BuildClearance()
    {
        var width = Width;
        var height = Height;
        var field = new byte[width * height];

        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            field[(y * width) + x] = IsSolid(x, y) ? (byte)0 : MaxClearance;

        // The two-pass chamfer that computes a Chebyshev transform exactly: a
        // forward sweep over the four already-visited neighbours, then a
        // backward sweep over the other four. ⚠️ Off-mask counts as OPEN, like
        // IsSolid — so the edges are not seeded with zeros, and a ray leaving
        // the map is not stopped by a wall that is not there.
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var at = (y * width) + x;
            var best = field[at];

            if (best == 0)
                continue;

            if (x > 0)
                best = Math.Min(best, (byte)(field[at - 1] + 1));

            if (y > 0)
            {
                best = Math.Min(best, (byte)(field[at - width] + 1));

                if (x > 0)
                    best = Math.Min(best, (byte)(field[at - width - 1] + 1));

                if (x + 1 < width)
                    best = Math.Min(best, (byte)(field[at - width + 1] + 1));
            }

            field[at] = best;
        }

        for (var y = height - 1; y >= 0; y--)
        for (var x = width - 1; x >= 0; x--)
        {
            var at = (y * width) + x;
            var best = field[at];

            if (best == 0)
                continue;

            if (x + 1 < width)
                best = Math.Min(best, (byte)(field[at + 1] + 1));

            if (y + 1 < height)
            {
                best = Math.Min(best, (byte)(field[at + width] + 1));

                if (x + 1 < width)
                    best = Math.Min(best, (byte)(field[at + width + 1] + 1));

                if (x > 0)
                    best = Math.Min(best, (byte)(field[at + width - 1] + 1));
            }

            field[at] = best;
        }

        return field;
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
