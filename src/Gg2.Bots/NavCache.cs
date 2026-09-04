using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using Gg2.Protocol;

namespace Gg2.Bots;

/// <summary>
/// Reads a nav graph off disk — <c>botnav/&lt;map&gt;_a&lt;area&gt;.txt</c>,
/// written by <c>Gg2.Nav</c> and read by <c>navCacheLoad.gml</c>.
/// </summary>
/// <remarks>
/// ✅ <b>Generating one is <c>Gg2.Nav</c>'s job as of 2026-09-03</b>, and it
/// writes exactly this format and this version — <c>NavGeneratorTests</c>
/// rebuilds all 25 checked-in graphs and compares them character for character.
/// ⚠️ The generator does not reference this assembly: it produces the file's
/// TEXT and the caller hands that to <see cref="Parse"/>, so an on-the-fly graph
/// and a file off disk are the same graph by construction. docs/NAVGEN.md.
/// <para>
/// The I/O lives here rather than on <see cref="NavGraph"/> so that a loaded
/// graph is a pure in-memory object with no path and no stream in it — the same
/// split <c>Gg2.Simulation</c> keeps between a <c>CollisionWorld</c> and the
/// tick that reads it.
/// </para>
/// <para>
/// <b>A miss is not an error.</b> <c>navCacheLoad.gml</c> returns false —
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

    /// <summary>The plain container, which the GML game reads.</summary>
    public const string Extension = ".txt";

    /// <summary>
    /// The same bytes deflated, which is what this repository checks in.
    /// </summary>
    /// <remarks>
    /// <b>The compression is the container, not the format.</b> Decompressing
    /// one of these gives back exactly the file the generator wrote, byte
    /// for byte, and <see cref="Parse"/> never learns which it came from — so a
    /// generator that knows nothing about gzip stays a working generator and
    /// <c>NAV_CACHE_VERSION</c> does not move.
    /// <para>
    /// It is roughly a 32x saving and costs about a millisecond of load time per
    /// map change — a hand-rolled binary format was rejected because the
    /// <em>compressed</em> size barely improves on it: everything it would remove
    /// is redundancy deflate already removes for free. docs/DEPLOY.md, "the nav
    /// graphs".
    /// </para>
    /// <para>
    /// This assumes <b>one graph per map change</b>, which is what
    /// <c>BotPopulationService.MapChanged</c> does. Re-measure before reusing it
    /// if nav ever loads per-round, per-area-transition, or eagerly for a whole
    /// rotation: a load cost that is free once a round is not free times 25 at
    /// startup.
    /// </para>
    /// </remarks>
    public const string CompressedExtension = ".txt.gz";

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
    /// <b>The map <em>name</em> is the identity, not its MD5.</b>
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
    /// The file holding a map and area's graph, preferring a freshly generated
    /// plain one, or empty when neither is there.
    /// </summary>
    /// <remarks>
    /// <b>PLAIN WINS.</b> A generator writes <c>&lt;key&gt;.txt</c> into the very
    /// directory this repository ships <c>.txt.gz</c> in, so the only way a
    /// <c>.txt</c> appears beside a <c>.gz</c> is that somebody just ran one —
    /// <c>NavManifestTests</c> hashes the MAPS, not the graph files, so a
    /// regeneration alone would not otherwise be noticed. <c>gg2-server navgen</c>
    /// writes the shipped container directly and deletes the loser, but the rule
    /// still matters for an external generator or a hand-written fixture. The
    /// rule: <b>the file somebody just wrote wins over
    /// the file that shipped</b>. The shipped one is the default; a plain file
    /// next to it is a deliberate act.
    /// <para>
    /// <b>Neither answer is silent.</b> <see cref="HasBothContainers"/> says
    /// when a graph exists twice, and <c>BotPopulationService</c> logs it on the
    /// map change — because whichever way the tie breaks, one of the two files
    /// is being ignored and the operator should know which.
    /// </para>
    /// </remarks>
    public static string FindFile(string directory, string map, int area)
    {
        ArgumentNullException.ThrowIfNull(directory);

        var stem = Path.Combine(directory, Key(map, area));
        var plain = stem + Extension;

        if (File.Exists(plain))
            return plain;

        var compressed = stem + CompressedExtension;
        return File.Exists(compressed) ? compressed : string.Empty;
    }

    /// <summary>
    /// Whether both containers hold this graph, so one of them is being ignored.
    /// </summary>
    public static bool HasBothContainers(string directory, string map, int area)
    {
        ArgumentNullException.ThrowIfNull(directory);

        var stem = Path.Combine(directory, Key(map, area));
        return File.Exists(stem + Extension) && File.Exists(stem + CompressedExtension);
    }

    /// <summary>
    /// Reads a cache file, transparently decompressing a <c>.txt.gz</c>.
    /// </summary>
    public static string ReadFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (!path.EndsWith(CompressedExtension, StringComparison.OrdinalIgnoreCase))
            return File.ReadAllText(path);

        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Loads the graph for a map and area out of <paramref name="directory"/>,
    /// or returns false the way <c>navCacheLoad.gml</c> does.
    /// </summary>
    /// <param name="walkmaskHash">
    /// The hash of the walkmask the graph is about to be used against, from
    /// <see cref="NavManifest.HashOf(Gg2.Maps.Walkmask)"/>. When supplied, and
    /// the directory carries a manifest naming this graph, a disagreement makes
    /// the load fail rather than hand back a graph describing geometry that is
    /// not there. Null skips the check, which is what a fixture and a tool want.
    /// </param>
    public static bool TryLoad(
        string directory,
        string map,
        int area,
        out NavGraph? graph,
        string? walkmaskHash = null)
    {
        ArgumentNullException.ThrowIfNull(directory);

        graph = null;
        var path = FindFile(directory, map, area);

        if (path.Length == 0)
            return false;

        if (walkmaskHash is not null
            && NavManifest.Load(directory).Check(Key(map, area), walkmaskHash) == NavMatch.Mismatch)
        {
            return false;
        }

        try
        {
            graph = Parse(ReadFile(path), map, area);
            return true;
        }
        catch (FormatException)
        {
            // A version mismatch or a shape the header did not promise. Quiet,
            // per the reference — and narrow: an IOException is a real problem
            // and is not swallowed.
            return false;
        }
        catch (InvalidDataException)
        {
            // A .txt.gz that is not gzip. Same class of "this file is not what
            // its name promises" as a bad header, so it gets the same quiet
            // answer; GZipStream throws this rather than FormatException.
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

        var path = FindFile(directory, map, area);

        if (path.Length == 0)
        {
            throw new FileNotFoundException(
                $"no nav graph for {map} area {area} in {directory} (looked for "
                + $"{Key(map, area)}{CompressedExtension} and {Key(map, area)}{Extension})");
        }

        return Parse(ReadFile(path), map, area);
    }

    /// <summary>Every map/area pair with a cache file in <paramref name="directory"/>.</summary>
    public static IReadOnlyList<(string Map, int Area)> List(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);

        if (!Directory.Exists(directory))
            return [];

        // A set, not a list: a directory holding both containers for one map
        // names that map once, the way it holds one graph.
        var found = new SortedSet<(string, int)>();

        foreach (var file in Directory.EnumerateFiles(directory))
        {
            // Matched by suffix rather than with a "*.txt" glob. Windows
            // matches a search pattern against 8.3 short names as well as real
            // ones, so a three-character extension can pick up longer ones;
            // doing it here also keeps both containers in one place rather than
            // in two globs that can drift apart.
            var name = Path.GetFileName(file);
            string stem;

            if (name.EndsWith(CompressedExtension, StringComparison.OrdinalIgnoreCase))
                stem = name[..^CompressedExtension.Length];
            else if (name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
                stem = name[..^Extension.Length];
            else
                continue;

            var split = stem.LastIndexOf("_a", StringComparison.Ordinal);

            if (split <= 0
                || !int.TryParse(stem.AsSpan(split + 2), NumberStyles.None, CultureInfo.InvariantCulture, out var area))
            {
                continue;
            }

            found.Add((stem[..split], area));
        }

        return [.. found];
    }

    /// <summary>
    /// Parses the four lines of a cache file.
    /// </summary>
    /// <remarks>
    /// <c>navCacheLoad.gml</c>'s order, and its checks: the version line, the
    /// dimensions line, then the two grids. <b>The header and the payload
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

        // The edge line may be absent: navCacheLoad guards its second read on
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

                // NOT cast. NAV_EDGE_BUCKET is the arc's horizontal velocity in
                // px/tick and every jump edge in every shipped cache carries a
                // fractional one — see NavEdge. Casting to (int) truncates about
                // a third of them to zero, which breaks in-flight tracking.
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
