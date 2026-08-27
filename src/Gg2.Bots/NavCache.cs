using System.Buffers.Binary;
using System.Globalization;
using Gg2.Protocol;

namespace Gg2.Bots;

/// <summary>
/// Reads a nav graph off disk — <c>botnav/&lt;map&gt;_a&lt;area&gt;.txt</c>,
/// written by <c>gg2-nav-gen</c> and read by <c>navCacheLoad.gml</c>.
/// </summary>
/// <remarks>
/// <b>Reading the cache is the whole job; generating one is not.</b> The
/// generator moved out of the game into <c>gg2-nav-gen</c>, which builds every
/// shipped map in under a second, and the files it writes are this format and
/// this version. Moving generation into this project is a natural consolidation
/// later; it is not this.
/// <para>
/// The I/O lives here rather than on <see cref="NavGraph"/> so that a loaded
/// graph is a pure in-memory object with no path and no stream in it — the same
/// split <c>Gg2.Simulation</c> keeps between a <c>CollisionWorld</c> and the
/// tick that reads it.
/// </para>
/// <para>
/// ⚠️ <b>A miss is not an error.</b> <c>navCacheLoad.gml</c> returns false —
/// having allocated nothing — when the file is absent, was written by a
/// different <c>NAV_CACHE_VERSION</c>, or does not deserialise to the shape its
/// header promised, and callers treat that as "no bots path on this map" rather
/// than as a failure: a server with an ungenerated map in its rotation should
/// keep serving that map to humans. <see cref="TryLoad"/> keeps that contract.
/// <see cref="Load"/> is the loud version, for a test or a tool that knows the
/// file should be there.
/// </para>
/// </remarks>
public static class NavCache
{
    /// <summary>Where the caches live, relative to the reference build directory.</summary>
    public const string DirectoryName = "botnav";

    // ds_grid_write's serialisation, and it is NOT the obvious one: a 12-byte
    // header (uint32 601, uint32 width, uint32 height) followed by COLUMN-MAJOR
    // cells of SIXTEEN bytes each, with the double sitting FOUR bytes into the
    // cell. Not 8-byte doubles packed end to end, which is what it looks like.
    // Cell (x, y) is at 12 + 16*(x*height + y) + 4.
    private const int GridHeader = 12;
    private const int GridCell = 16;
    private const int GridValueOffset = 4;

    /// <summary>
    /// The filename stem for a map and area — <c>navCacheKey.gml</c>.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The map <em>name</em> is the identity, not its MD5.</b>
    /// <c>serverGotoMap.gml</c> leaves <c>currentMapMD5</c> empty for every
    /// internal map, so keying on the MD5 alone would give all 22 shipped maps
    /// the same cache entry and serve the first graph built for every one of
    /// them — bots pathing <c>ctf_truefort</c> against <c>gg_debug</c>'s
    /// geometry. Anything outside <c>[A-Za-z0-9_-]</c> becomes an underscore.
    /// <para>
    /// The area is part of the key because each stage of a multi-area map is a
    /// genuinely different graph. The optional custom-map MD5 suffix is not
    /// modelled here: this port serves built-in maps, for which it is always
    /// empty.
    /// </para>
    /// </remarks>
    public static string Key(string map, int area)
    {
        ArgumentNullException.ThrowIfNull(map);

        var safe = string.Create(map.Length, map, static (span, name) =>
        {
            for (var i = 0; i < name.Length; i++)
            {
                var c = name[i];
                span[i] = c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-'
                    ? c
                    : '_';
            }
        });

        return $"{safe}_a{area.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// Loads the graph for a map and area out of <paramref name="directory"/>,
    /// or returns false the way <c>navCacheLoad.gml</c> does.
    /// </summary>
    public static bool TryLoad(string directory, string map, int area, out NavGraph? graph)
    {
        ArgumentNullException.ThrowIfNull(directory);

        graph = null;
        var path = Path.Combine(directory, Key(map, area) + ".txt");

        if (!File.Exists(path))
            return false;

        try
        {
            graph = Parse(File.ReadAllText(path), map, area);
            return true;
        }
        catch (FormatException)
        {
            // A version mismatch or a shape the header did not promise. Quiet,
            // per the reference — and narrow: an IOException is a real problem
            // and is not swallowed.
            return false;
        }
    }

    /// <summary>
    /// Loads a graph, throwing on anything <see cref="TryLoad"/> would answer
    /// false to.
    /// </summary>
    public static NavGraph Load(string directory, string map, int area)
    {
        ArgumentNullException.ThrowIfNull(directory);

        var path = Path.Combine(directory, Key(map, area) + ".txt");
        return Parse(File.ReadAllText(path), map, area);
    }

    /// <summary>Every map/area pair with a cache file in <paramref name="directory"/>.</summary>
    public static IReadOnlyList<(string Map, int Area)> List(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);

        if (!Directory.Exists(directory))
            return [];

        var found = new List<(string, int)>();

        foreach (var file in Directory.EnumerateFiles(directory, "*.txt"))
        {
            var stem = Path.GetFileNameWithoutExtension(file);
            var split = stem.LastIndexOf("_a", StringComparison.Ordinal);

            if (split <= 0
                || !int.TryParse(stem.AsSpan(split + 2), NumberStyles.None, CultureInfo.InvariantCulture, out var area))
            {
                continue;
            }

            found.Add((stem[..split], area));
        }

        found.Sort();
        return found;
    }

    /// <summary>
    /// Parses the four lines of a cache file.
    /// </summary>
    /// <remarks>
    /// <c>navCacheLoad.gml</c>'s order, and its checks: the version line, the
    /// dimensions line, then the two grids. ⚠️ <b>The header and the payload
    /// have to agree</b>, or the columns do not mean what the field constants
    /// say they mean — which is a graph that parses cleanly and describes
    /// geometry that does not exist.
    /// </remarks>
    public static NavGraph Parse(string text, string map, int area)
    {
        ArgumentNullException.ThrowIfNull(text);

        // The reference writes CRLF through GM8's file_text_writeln; splitting
        // on '\n' and trimming is what its own reader effectively does.
        var lines = text.Split('\n');

        if (lines.Length < 3)
            throw new FormatException("a nav cache is at least three lines");

        var header = lines[0].Trim();
        var expected = $"navgraph {NavConstants.CacheVersion.ToString(CultureInfo.InvariantCulture)}";

        if (header != expected)
        {
            // Refusing rather than adapting: the field layout is what matters
            // and a reader that silently accepts a graph built by a different
            // generator is how you end up trusting numbers about geometry that
            // no longer exists.
            throw new FormatException($"expected \"{expected}\", got \"{header}\"");
        }

        var dims = lines[1].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (dims.Length < 4)
            throw new FormatException("the dimensions line wants four numbers");

        var maskWidth = ParseInt(dims[0]);
        var maskHeight = ParseInt(dims[1]);
        var nodeCount = ParseInt(dims[2]);
        var edgeCount = ParseInt(dims[3]);

        var nodeGrid = ReadGrid(lines[2].Trim());

        // ⚠️ The edge line may be absent: navCacheLoad guards its second read on
        // file_text_eof, so a graph with no edges is a two-grid file with one
        // grid in it. Every shipped map has edges; a hand-built fixture need
        // not.
        var edgeGrid = lines.Length > 3 && lines[3].Trim().Length > 0
            ? ReadGrid(lines[3].Trim())
            : new Grid(0, 0, []);

        if (nodeGrid.Width != NavConstants.NodeFields || nodeGrid.Height < nodeCount)
        {
            throw new FormatException(
                $"node grid is {nodeGrid.Width}x{nodeGrid.Height}, wanted "
                + $"{NavConstants.NodeFields}x{nodeCount}");
        }

        if (edgeCount > 0 && (edgeGrid.Width != NavConstants.EdgeFields || edgeGrid.Height < edgeCount))
        {
            throw new FormatException(
                $"edge grid is {edgeGrid.Width}x{edgeGrid.Height}, wanted "
                + $"{NavConstants.EdgeFields}x{edgeCount}");
        }

        var nodes = new NavNode[nodeCount];
        for (var i = 0; i < nodeCount; i++)
        {
            nodes[i] = new NavNode(
                (int)nodeGrid.At(NavConstants.NodeY, i),
                (int)nodeGrid.At(NavConstants.NodeX0, i),
                (int)nodeGrid.At(NavConstants.NodeX1, i),
                (int)nodeGrid.At(NavConstants.NodeFlags, i),
                (NavDoor)(int)nodeGrid.At(NavConstants.NodeDoor, i),
                (NavGate)(int)nodeGrid.At(NavConstants.NodeGate, i));
        }

        var edges = new NavEdge[edgeCount];
        for (var e = 0; e < edgeCount; e++)
        {
            edges[e] = new NavEdge(
                (int)edgeGrid.At(NavConstants.EdgeFrom, e),
                (int)edgeGrid.At(NavConstants.EdgeTo, e),
                (NavEdgeType)(int)edgeGrid.At(NavConstants.EdgeType, e),

                // ⚠️ NOT cast. NAV_EDGE_BUCKET is the arc's horizontal velocity
                // in px/tick and every jump edge in every shipped cache carries a
                // fractional one — see NavEdge. An (int) here truncated a third of
                // them to zero and cost this port five of its fourteen scenarios.
                edgeGrid.At(NavConstants.EdgeBucket, e),
                edgeGrid.At(NavConstants.EdgeTicks, e),
                edgeGrid.At(NavConstants.EdgeCost, e),
                (NavGate)(int)edgeGrid.At(NavConstants.EdgeGate, e),
                (int)edgeGrid.At(NavConstants.EdgeTakeoff, e),
                (int)edgeGrid.At(NavConstants.EdgeRejump, e));
        }

        return new NavGraph(map, area, NavConstants.CacheVersion, maskWidth, maskHeight, nodes, edges);
    }

    private static int ParseInt(string token) =>
        int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new FormatException($"\"{token}\" is not a number");

    /// <summary>A deserialised <c>ds_grid_write</c> blob.</summary>
    private readonly struct Grid(int width, int height, byte[] bytes)
    {
        public int Width { get; } = width;

        public int Height { get; } = height;

        /// <summary>Cell (x, y). Column-major, sixteen-byte cells, value four bytes in.</summary>
        public double At(int x, int y) =>
            BinaryPrimitives.ReadDoubleLittleEndian(
                bytes.AsSpan(GridHeader + (GridCell * ((x * Height) + y)) + GridValueOffset, sizeof(double)));
    }

    private static Grid ReadGrid(string hex)
    {
        if (hex.Length < (GridHeader * 2) || (hex.Length & 1) != 0)
            throw new FormatException("grid hex is too short or has an odd length");

        byte[] bytes;
        try
        {
            bytes = Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            throw new FormatException("grid is not hex");
        }

        // The leading uint32 is ds_grid's own type tag, 601. Checked rather than
        // skipped: it is the cheapest possible signal that this line is not a
        // grid at all, which is otherwise a silently plausible pile of doubles.
        var tag = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4));
        if (tag != 601)
            throw new FormatException($"grid type tag is {tag}, not 601");

        var width = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4));
        var height = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4));

        var need = GridHeader + ((long)width * height * GridCell);
        if (bytes.Length < need)
            throw new FormatException($"grid truncated: {bytes.Length} bytes, expected {need} for {width}x{height}");

        return new Grid(width, height, bytes);
    }
}
