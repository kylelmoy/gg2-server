using System.Globalization;
using GG2.Protocol;

namespace GG2.Maps;

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

    /// <summary>
    /// One bitset per precomputed box SHAPE: bit (c, r) is set when the
    /// <c>w x h</c> cell rectangle whose top-left corner is (c, r) contains
    /// anything solid. <see cref="EroShape"/> indexes it; null entries are shapes
    /// that were not worth a table.
    /// </summary>
    /// <remarks>
    /// <b>This is a morphological EROSION of the mask, precomputed once per
    /// map, and it exists because the shapes a character box produces are almost
    /// constant.</b> A box's cell rectangle is <c>w</c> or <c>w+1</c> columns by
    /// <c>h</c> or <c>h+1</c> rows — the pixel size is fixed per class and only the
    /// sub-cell phase of the position varies — so a handful of shapes answers
    /// nearly every query. <b>Measured over real bot matches: FOUR shapes (3x6,
    /// 4x6, 3x7, 4x7) cover 86.7%, 88.4% and 94.5% of all mask queries on three
    /// maps</b>, and off-map queries are 0.02%. Pyro's two shapes were added on
    /// top so that every class in the bot draw is covered.
    /// <para>
    /// <b>At CELL resolution, which is what makes it small.</b> One bit per
    /// possible top-left cell is the same footprint as the mask itself — about
    /// 20 KB a shape on <c>ctf_truefort</c>, 80 KB for the four. A per-PIXEL table
    /// would be 36x that and would not stay in cache, which is the version of this
    /// idea that does not work.
    /// </para>
    /// <para>
    /// <b>The lookup is exact, not conservative.</b> It answers the same
    /// question <see cref="AnySolidInBox"/> answers, so there is no fallback path
    /// for a "maybe" — a shape is either tabulated or it is not.
    /// </para>
    /// </remarks>
    private readonly ulong[][] _eroded;

    /// <summary>
    /// Whether a <paramref name="width"/> x <paramref name="height"/> cell
    /// rectangle has a precomputed table.
    /// </summary>
    /// <remarks>
    /// <b>Exposed for a TEST, and the test is the point.</b> Equivalence alone
    /// cannot tell a working table from one nothing reaches — both are green. This
    /// lets <c>WalkmaskBoxTests</c> assert that the shapes real character boxes
    /// produce are the shapes that are tabulated, so a change to
    /// <c>CharacterBox.For</c> that quietly moved every query onto the general path
    /// fails instead of merely getting slower.
    /// </remarks>
    public static bool IsTabulatedShape(int width, int height) => EroShape(width, height) >= 0;

    /// <summary>
    /// The table index for a <paramref name="width"/> x <paramref name="height"/>
    /// cell rectangle, or -1 where no table exists.
    /// </summary>
    /// <remarks>
    /// <b>3-4 columns by 5-7 rows, and that set is DERIVED from the shipped
    /// character boxes rather than guessed.</b> A box's cell rectangle is fixed by
    /// its pixel size and the sub-cell phase of its position, so each class
    /// produces at most two widths and two heights. Enumerated over all 36 phases:
    /// <b>13 px wide is ALWAYS 3 columns and 19 px is ALWAYS 4</b> — only 15 px
    /// varies. Scout, Soldier, Sniper, Demoman, Medic, Engineer, Heavy and Spy are
    /// 3-4 x 6-7; <b>Pyro is 3-4 x 5-6</b>, which is why the row range starts at 5.
    /// <para>
    /// <b>Quote alone falls outside (4-5 rows) and takes the general path</b> —
    /// correct, merely slower, and it is not in the bot class draw:
    /// <c>Program.ParseClasses</c> refuses anything above Pyro.
    /// </para>
    /// <para>
    /// <b>Enumerate the phases; do not derive them by hand.</b> A hand-derived
    /// claim that a 19 px box spans 4-5 columns is wrong — it spans exactly 4 —
    /// so <c>WalkmaskBoxTests</c> asserts the intersection mechanically instead.
    /// </para>
    /// <para>
    /// <b>Widening further is a memory decision — about 20 KB a shape — and
    /// wants a shape-mix measurement rather than a guess.</b> Four shapes measured
    /// at 87-95% of all mask queries over three real matches; Pyro's two were added
    /// on top so that every class in the draw is covered.
    /// </para>
    /// </remarks>
    private static int EroShape(int width, int height) =>
        width is 3 or 4 && height is >= 5 and <= 7 ? ((width - 3) * 3) + (height - 5) : -1;

    private Walkmask(int width, int height, ulong[] words, int stride)
    {
        Width = width;
        Height = height;
        _words = words;
        _stride = stride;
        _clearance = BuildClearance();
        _eroded = BuildEroded();
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
    /// <b>This is what the bitset was built for.</b> The storage is a
    /// word-strided bitset because <c>PlaceFree</c> is the hottest call in the
    /// server and a character rectangle spans only a handful of cells — the test
    /// wants to be a few masked word reads, not a byte loop.
    /// <para>
    /// <b>Measured on a soldier on <c>ctf_truefort</c>:</b> a box test visits
    /// <b>18 cells over 6 rows</b> and its three columns land in ONE word per
    /// row, so this reads 6 words where a per-cell loop runs 18 tests.
    /// <c>PlaceFreeInMask</c> 17.1 ns → 6.8 ns, and a 10px crawl — which calls it
    /// about thirteen times — 310 ns → 179 ns.
    /// </para>
    /// <para>
    /// <b>Exactly the same predicate, including off-mask.</b> Out-of-range is
    /// OPEN, as <see cref="IsSolid"/> has it, so the rectangle is clamped rather
    /// than rejected and a box hanging off the edge of the map reports only what
    /// the cells inside say. <c>WalkmaskBoxTests</c> asserts the equivalence
    /// against the naive loop over every shipped map, edges included, rather than
    /// leaving it to reading.
    /// </para>
    /// <para>
    /// <b>Bits above <see cref="Width"/> in a row's last word are excluded by
    /// the clamp</b>, so this does not depend on the decoder leaving them zero —
    /// a dependency that would be invisible until a map whose width is not a
    /// multiple of 64 had junk there.
    /// </para>
    /// </remarks>
    public bool AnySolidInBox(int firstColumn, int lastColumn, int firstRow, int lastRow)
    {
        // THE TABLE FIRST, because it answers most queries and answers them
        // exactly. A shape is tabulated only for rectangles wholly on the map, so
        // the unsigned bounds tests below are the whole precondition — where they
        // hold, clamping is a no-op and the tabulated rectangle IS the rectangle
        // asked about. Off-map queries measure 0.02% of the total.
        var shape = EroShape(lastColumn - firstColumn + 1, lastRow - firstRow + 1);

        if (shape >= 0
            && (uint)firstColumn < (uint)Width && (uint)lastColumn < (uint)Width
            && (uint)firstRow < (uint)Height && (uint)lastRow < (uint)Height)
        {
            return (_eroded[shape][(firstRow * _stride) + (firstColumn >> 6)]
                & (1UL << (firstColumn & 63))) != 0;
        }

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
    /// <b>An acceleration structure for marching a line, and nothing else reads
    /// it.</b> <c>CollisionLine.Walk</c> samples one pixel per pixel of a
    /// segment, and a soldier's arc test walks ten of them a decision.
    /// <para>
    /// <b>It makes the march skip, never guess.</b> A clearance of <c>c</c>
    /// says every cell within Chebyshev distance <c>c - 1</c> is open, so every
    /// world pixel within <c>6c - 7</c> of the probe is open too — one cell of
    /// slack for the probe's own position inside its cell. A marcher that
    /// advances by no more than that lands on open ground by construction, so
    /// the first solid probe it finds is the first solid probe there is. The
    /// answer is <b>identical</b> to the naive march, not an approximation of
    /// it; <c>CollisionLineSkipTests</c> asserts that against the walk with the
    /// skip switched off.
    /// </para>
    /// <para>
    /// <b>Built eagerly, with the mask.</b> Computing it lazily — on the first
    /// read — would allocate inside whichever call happens to read it first, and
    /// since the <see cref="Walkmask"/> is shared through <c>ScenarioResources</c>,
    /// that would make an allocation gate elsewhere pass or fail depending on
    /// test order. Eager costs one pass and <c>Width * Height</c> bytes per map —
    /// 160 KB and about 0.3 ms for <c>ctf_truefort</c>, once, on a load that is
    /// itself cached. <b>An unconditional property is worth more than that.</b>
    /// </para>
    /// </remarks>
    public ReadOnlySpan<byte> Clearance => _clearance;

    /// <summary>
    /// How many world pixels a marcher standing on a cell of this
    /// <see cref="Clearance"/> may advance without being able to step over
    /// anything solid.
    /// </summary>
    /// <remarks>
    /// <b>The one copy of the bound.</b> <c>CollisionLine.Walk</c> reads it, and
    /// a second marcher must read this one rather than its own copy: a bound that
    /// drifted would step over a wall, which is a wrong answer that looks like
    /// nothing.
    /// <para>
    /// The algebra: a clearance of <c>c</c> says every cell within Chebyshev
    /// distance <c>c - 1</c> is open. A pixel <c>d</c> away from a probe is at
    /// most <c>floor((d + 5) / 6)</c> cells away — the 5 is the probe's own
    /// position inside its 6-pixel cell — so <c>d</c> is safe while
    /// <c>floor((d + 5) / 6) &lt;= c - 1</c>, which holds up to <c>d = 6c - 6</c>.
    /// </para>
    /// <para>
    /// <b>One pixel is given back deliberately.</b> <c>6c - 6</c> is the tight
    /// value; the extra pixel leaves the bound with margin rather than sitting
    /// exactly on the boundary of an argument about rounding. It is not a slip to
    /// be tidied up.
    /// </para>
    /// </remarks>
    public static int SafeSkipPixels(int clearance) => (6 * clearance) - 7;

    /// <summary>
    /// The value <see cref="Clearance"/> saturates at. <b>A cap, so a reader
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

    /// <summary>
    /// Builds one bitset per <see cref="EroShape"/>: does the <c>w x h</c> cell
    /// rectangle at (c, r) hold anything solid?
    /// </summary>
    /// <remarks>
    /// A separable sliding OR — a horizontal pass over <c>w</c> columns, then a
    /// vertical pass over <c>h</c> rows of that — so the cost is linear in cells
    /// rather than in cells x w x h. Sub-millisecond per map.
    /// <para>
    /// <b>Rectangles that would run off the right or bottom edge are left
    /// CLEAR, and nothing reads them</b>: <see cref="AnySolidInBox"/> only consults
    /// a table when the whole rectangle is on the map. Leaving them clear rather
    /// than solid keeps a stray read failing open, which matches what off-mask
    /// means everywhere else in this class.
    /// </para>
    /// </remarks>
    private ulong[][] BuildEroded()
    {
        var tables = new ulong[6][];

        // How many solid cells the sliding window currently holds. A count rather
        // than a boolean, so the window advances in O(1) instead of recomputing
        // the OR over the whole window at every position.
        var rowOr = new bool[Width * Height];

        foreach (var width in (ReadOnlySpan<int>)[3, 4])
        {
            // Horizontal: does [c, c + width) hold anything solid on row r?
            for (var r = 0; r < Height; r++)
            {
                var solid = 0;

                for (var d = 0; d < width && d < Width; d++)
                {
                    if (IsSolid(d, r))
                        solid++;
                }

                for (var c = 0; c < Width; c++)
                {
                    rowOr[(r * Width) + c] = solid > 0;

                    if (IsSolid(c, r))
                        solid--;

                    if (IsSolid(c + width, r))
                        solid++;
                }
            }

            foreach (var height in (ReadOnlySpan<int>)[5, 6, 7])
            {
                var shape = EroShape(width, height);
                var table = new ulong[_words.Length];

                // Vertical: the same sliding window down each column, over the
                // horizontal pass rather than over the mask.
                for (var c = 0; c + width <= Width; c++)
                {
                    var solid = 0;

                    for (var d = 0; d < height && d < Height; d++)
                    {
                        if (rowOr[(d * Width) + c])
                            solid++;
                    }

                    for (var r = 0; r + height <= Height; r++)
                    {
                        if (solid > 0)
                            table[(r * _stride) + (c >> 6)] |= 1UL << (c & 63);

                        if (rowOr[(r * Width) + c])
                            solid--;

                        if (r + height < Height && rowOr[((r + height) * Width) + c])
                            solid++;
                    }
                }

                tables[shape] = table;
            }
        }

        return tables;
    }

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
        // backward sweep over the other four. Off-mask counts as OPEN, like
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
    /// the JavaScript walkmask decoder produces.
    /// </summary>
    /// <remarks>
    /// Exists so the parity fixture can compare this decoder against that one
    /// cell-for-cell (docs/STATUS.md, phase 0 exit criterion). Not a hot path.
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
