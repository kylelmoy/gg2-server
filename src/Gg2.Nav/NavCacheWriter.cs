using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using Gg2.Protocol;

namespace Gg2.Nav;

/// <summary>
/// The on-disk nav graph, byte for byte what <c>navCacheSave</c> wrote.
/// </summary>
/// <remarks>
/// The point of an offline generator is that the reader consumes what it produces
/// without knowing the difference, so this is not "a format like the game's" — it
/// is the format, reproduced exactly, including the line endings and the hex case.
/// <code>
/// line 1  "navgraph &lt;NAV_CACHE_VERSION&gt;"
/// line 2  "&lt;maskW&gt; &lt;maskH&gt; &lt;nodeCount&gt; &lt;edgeCount&gt;"
/// line 3  ds_grid_write(nodes)  hex
/// line 4  ds_grid_write(edges)  hex
/// </code>
/// Every line is terminated CRLF, because <c>file_text_writeln</c> does.
/// <para>
/// ⚠️ <b><c>ds_grid_write</c> is NOT the obvious layout, and re-deriving it by
/// hand costs an afternoon.</b> A 12-byte header (uint32 601, uint32 width, uint32
/// height) is followed by COLUMN-MAJOR cells of SIXTEEN bytes each, with the
/// double sitting FOUR bytes into the cell — not 8-byte doubles packed end to end,
/// which is what it looks like. Cell (x, y) is at
/// <c>12 + 16·(x·height + y) + 4</c>. The four bytes before each double are its
/// type tag (0 = real) and the four after are padding; both are zero for every
/// value a nav graph holds. <c>NavCache</c> reads it back through the same three
/// constants.
/// </para>
/// <para>
/// ⚠️ <b>The hex is UPPERCASE.</b> A lowercase file still loads —
/// <c>ds_grid_read</c> is not case sensitive — but it is not what the game wrote,
/// and this format is a transcription of GM8's <c>ds_grid_write</c> rather than a
/// format of our own.
/// </para>
/// </remarks>
public static class NavCacheWriter
{
    private const int GridMagic = 601;
    private const int GridHeader = 12;
    private const int GridCell = 16;
    private const int GridValueOffset = 4;

    /// <summary>The plain container, which the GML game reads and this repo does not ship.</summary>
    public const string Extension = ".txt";

    /// <summary>The compressed container this repository ships.</summary>
    public const string CompressedExtension = ".txt.gz";

    /// <summary>
    /// Serialises a built graph into the four lines of a cache file.
    /// </summary>
    internal static string Serialise(NavGraphData graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var nodes = graph.Nodes;

        // ⚠️ A grid must have at least one row: an empty graph writes one padding
        // row of zeroes and declares a node count of zero, which is what the
        // reference's ds_grid_create(w, max(1, n)) produces.
        var nodeRows = Math.Max(nodes.Count, 1);

        var text = new StringBuilder();

        text.Append("navgraph ")
            .Append(NavConstants.CacheVersion.ToString(CultureInfo.InvariantCulture))
            .Append("\r\n");

        text.Append(graph.MaskWidth.ToString(CultureInfo.InvariantCulture)).Append(' ')
            .Append(graph.MaskHeight.ToString(CultureInfo.InvariantCulture)).Append(' ')
            .Append(nodes.Count.ToString(CultureInfo.InvariantCulture)).Append(' ')
            .Append(graph.EdgeCount.ToString(CultureInfo.InvariantCulture))
            .Append("\r\n");

        text.Append(WriteGrid(NavConstants.NodeFields, nodeRows, (field, row) =>
        {
            if (row >= nodes.Count)
                return 0; // the padding row of an empty graph

            return field switch
            {
                NavConstants.NodeY => nodes.Y[row],
                NavConstants.NodeX0 => nodes.X0[row],
                NavConstants.NodeX1 => nodes.X1[row],
                NavConstants.NodeFlags => nodes.Flags[row],
                NavConstants.NodeDoor => nodes.Doors[row],
                NavConstants.NodeGate => nodes.Gates[row],
                _ => 0,
            };
        })).Append("\r\n");

        var edges = graph.Edges;

        text.Append(WriteGrid(NavConstants.EdgeFields, graph.EdgeRows,
            (field, row) => edges[(row * NavConstants.EdgeFields) + field])).Append("\r\n");

        return text.ToString();
    }

    /// <summary>
    /// <c>ds_grid_write</c>: a column-major blob of 16-byte cells, as uppercase
    /// hex.
    /// </summary>
    private static string WriteGrid(int width, int height, Func<int, int, double> at)
    {
        var bytes = new byte[GridHeader + (width * height * GridCell)];

        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), GridMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), (uint)width);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), (uint)height);

        var offset = GridHeader + GridValueOffset;

        for (var x = 0; x < width; x++)
        {
            for (var y = 0; y < height; y++)
            {
                BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(offset, sizeof(double)), at(x, y));
                offset += GridCell;
            }
        }

        return Convert.ToHexString(bytes);
    }

    /// <summary>
    /// Writes a graph's text into <paramref name="directory"/> and removes the
    /// other container for the same key.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Removing the loser is the point, and it closes a documented
    /// footgun.</b> <c>NavCache.FindFile</c> prefers a plain <c>.txt</c> over a
    /// <c>.txt.gz</c> — what somebody just generated beats what shipped — so
    /// leaving both behind means one file is being ignored and nothing says which.
    /// The previous workflow wrote <c>.txt</c> and needed a second step
    /// (<c>tools/compress-nav.sh</c>) to put it back into the shipped form; this
    /// does the whole cycle in one call.
    /// <para>
    /// ⚠️ <b>An unchanged graph is not rewritten.</b> The comparison is on the
    /// decompressed TEXT, not on the container's bytes, because two gzip
    /// implementations compress the same input differently — so a byte comparison
    /// would rewrite all 25 shipped files the first time this generator ran and
    /// call it a change. Skipping keeps <c>git status</c> honest about which graphs
    /// a source edit actually moved, which is the question a regeneration is asked
    /// to answer.
    /// </para>
    /// </remarks>
    /// <returns>The file written, or the existing file when nothing changed.</returns>
    public static NavWriteResult Write(string directory, string key, string text, bool compress)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(text);

        Directory.CreateDirectory(directory);

        var stem = Path.Combine(directory, key);
        var wanted = stem + (compress ? CompressedExtension : Extension);
        var other = stem + (compress ? Extension : CompressedExtension);

        if (File.Exists(wanted) && Unchanged(wanted, text) && !File.Exists(other))
            return new NavWriteResult(wanted, Changed: false);

        if (compress)
            File.WriteAllBytes(wanted, Gzip(text));
        else
            File.WriteAllText(wanted, text, Encoding.Latin1);

        if (File.Exists(other))
            File.Delete(other);

        return new NavWriteResult(wanted, Changed: true);
    }

    private static bool Unchanged(string path, string text)
    {
        try
        {
            return string.Equals(ReadText(path), text, StringComparison.Ordinal);
        }
        catch (InvalidDataException)
        {
            // A .txt.gz that is not gzip. Rewriting it is the right answer.
            return false;
        }
    }

    /// <summary>Reads either container back as the text that was written.</summary>
    public static string ReadText(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (!path.EndsWith(CompressedExtension, StringComparison.OrdinalIgnoreCase))
            return File.ReadAllText(path, Encoding.Latin1);

        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.Latin1);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// The text, gzipped, with every header field that is not the data pinned.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Determinism is the requirement, not compression ratio.</b> These files
    /// are checked in, so the same graph has to produce the same bytes every time
    /// or every regeneration rewrites all 25 whether or not one moved — which is
    /// what <c>gzip -n</c> buys on the command line. The modification time is
    /// already zero and the OS byte is pinned to 0xFF ("unknown"), so the same
    /// graph gives the same file on any machine.
    /// </remarks>
    private static byte[] Gzip(string text)
    {
        using var output = new MemoryStream();

        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var bytes = Encoding.Latin1.GetBytes(text);
            gzip.Write(bytes, 0, bytes.Length);
        }

        var buffer = output.ToArray();

        // Bytes 4..7 are MTIME and byte 9 is the source OS. Both are metadata
        // about the run rather than about the graph.
        buffer.AsSpan(4, 4).Clear();
        buffer[9] = 0xFF;
        return buffer;
    }
}

/// <summary>What <see cref="NavCacheWriter.Write"/> did.</summary>
/// <param name="Path">The file that now holds the graph.</param>
/// <param name="Changed">
/// False when the graph on disk was already this one, so nothing was written.
/// </param>
public readonly record struct NavWriteResult(string Path, bool Changed);
