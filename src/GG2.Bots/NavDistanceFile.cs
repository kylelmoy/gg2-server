using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;

namespace GG2.Bots;

/// <summary>
/// The on-disk next-hop tables for one nav graph — <c>&lt;map&gt;_a&lt;area&gt;.dist.gz</c>
/// beside the graph they belong to.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only the next-hop half is stored; the cost half is rebuilt on load</b> by
/// <see cref="NavDistances.FromNextHops"/>. Measured over the 25 shipped graphs:
/// hops gzip to 2.47 MB and costs to 35.07 MB, and rebuilding is 5.7x cheaper
/// than deriving from scratch. Storing the costs would trade 14x the bytes for
/// a fraction of the load time.
/// </para>
/// <para>
/// ⚠️ <b>This is a binary format, and <c>CONTRIBUTING.md</c>'s rule against one does
/// not cover it.</b> That rule is about re-encoding the GRAPH, where a binary
/// layout was measured to save ~0.1% because deflate already removes what it
/// would. This is an N² table of hop indices that no text encoding survives:
/// <c>ctf_truefort</c>'s four tables are 1.9M hops — 253 KB packed and gzipped,
/// several megabytes as digits. The graph's text format is untouched and still
/// the thing a human diffs.
/// </para>
/// <para>
/// ⚠️ <b>A file is bound to one build of one graph and says so three ways</b> —
/// the node count, the arc types present, and a hash of the graph's own nodes
/// and edges. A graph rebuilt from a changed map hashes differently and
/// <see cref="TryRead"/> then refuses the file rather than pathing a bot through
/// a wall. This is the failure <c>nav/manifest.tsv</c> guards for the graph
/// itself, one level down.
/// </para>
/// <para>
/// The format, little-endian throughout: magic <c>GG2NDIST</c>, a format
/// version, the node count, the profile mask the tables were built for, the
/// graph hash (16 bytes), a table count, then per table its profile key and
/// <c>NodeCount²</c> <see cref="ushort"/> hops in <c>[goal * n + from]</c> order
/// — the layout <see cref="NavDistances"/> already indexes, so a read is a copy.
/// The whole thing is gzipped.
/// </para>
/// </remarks>
public static class NavDistanceFile
{
    /// <summary>The extension, appended to the graph's own key.</summary>
    public const string Extension = ".dist.gz";

    /// <summary>
    /// Bumped when the layout changes. An older file is refused, not guessed at.
    /// </summary>
    public const int Version = 1;

    private static ReadOnlySpan<byte> Magic => "GG2NDIST"u8;

    /// <summary>The path a graph's tables live at.</summary>
    public static string PathFor(string directory, string map, int area) =>
        Path.Combine(directory, NavCache.Key(map, area) + Extension);

    /// <summary>
    /// Identifies the graph a table file was built against.
    /// </summary>
    /// <remarks>
    /// Over the nodes and edges rather than the file's bytes: two graphs that
    /// path identically must share a hash even if their text differs in
    /// whitespace, and two that path differently must not, whatever the text
    /// looks like.
    /// </remarks>
    public static byte[] HashOf(NavGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var buffer = new byte[16];
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        BinaryPrimitives.WriteInt32LittleEndian(buffer, graph.NodeCount);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4), graph.EdgeCount);
        sha.AppendData(buffer.AsSpan(0, 8));

        foreach (ref readonly var node in graph.Nodes)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buffer, node.Row);
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4), node.X0);
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(8), node.X1);
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(12), node.Flags);
            sha.AppendData(buffer);
        }

        foreach (ref readonly var edge in graph.Edges)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buffer, edge.From);
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4), edge.To);
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(8), (int)edge.Type);
            sha.AppendData(buffer.AsSpan(0, 12));
            sha.AppendData(BitConverter.GetBytes(edge.Cost));
        }

        return sha.GetHashAndReset()[..16];
    }

    /// <summary>
    /// Writes the tables for <paramref name="graph"/>, gzipped, replacing any
    /// file already there.
    /// </summary>
    public static void Write(string path, NavGraph graph, NavDistanceSet set)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(set);

        var directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        using var file = File.Create(path);
        using var gz = new GZipStream(file, CompressionLevel.Optimal);
        using var w = new BinaryWriter(gz);

        w.Write(Magic);
        w.Write(Version);
        w.Write(graph.NodeCount);
        w.Write(set.PresentArcs);
        w.Write(HashOf(graph));

        var keys = set.DistinctKeys;
        w.Write(keys.Count);

        var bytes = new byte[graph.NodeCount * sizeof(ushort)];

        foreach (var key in keys)
        {
            w.Write(key);

            var hops = set.TableFor(key).NextHops;

            // A row at a time: a table can be 1.4 MB and this keeps one
            // graph-row buffer rather than a second copy of the whole thing.
            for (var goal = 0; goal < graph.NodeCount; goal++)
            {
                var row = hops.Slice(goal * graph.NodeCount, graph.NodeCount);

                for (var i = 0; i < row.Length; i++)
                    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * sizeof(ushort)), row[i]);

                w.Write(bytes);
            }
        }
    }

    /// <summary>
    /// Whether a file exists, is this format version, and was built against this
    /// exact graph — reading only the header.
    /// </summary>
    /// <remarks>
    /// The generator asks this to decide whether to rebuild, and <c>--check</c>
    /// asks it to decide whether to complain. It deliberately does NOT rebuild
    /// the cost half, so it costs a gunzip of a few dozen bytes rather than the
    /// whole table — the difference between a <c>--check</c> that runs in a
    /// second and one that runs in half a minute.
    /// </remarks>
    public static bool IsCurrent(string path, NavGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        if (!File.Exists(path))
            return false;

        try
        {
            using var file = File.OpenRead(path);
            using var gz = new GZipStream(file, CompressionMode.Decompress);
            using var r = new BinaryReader(gz);

            return r.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic)
                && r.ReadInt32() == Version
                && r.ReadInt32() == graph.NodeCount
                && r.ReadInt32() >= 0
                && r.ReadBytes(16).AsSpan().SequenceEqual(HashOf(graph));
        }
        catch (Exception e) when (e is IOException or InvalidDataException or EndOfStreamException)
        {
            // A truncated or non-gzip file is simply not current; the generator
            // overwrites it and the loader derives instead.
            return false;
        }
    }

    /// <summary>
    /// Reads the tables for <paramref name="graph"/>, or returns <c>null</c> when
    /// there is no usable file — missing, a version this build does not know, or
    /// built against a different graph.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Every refusal is silent by design and the caller derives instead</b>,
    /// because a stale artefact must never be the difference between a server
    /// that runs and one that does not. <paramref name="reason"/> carries what
    /// happened so the caller can log it. A file that gets past the header and
    /// is then malformed throws, because that is corruption rather than
    /// staleness.
    /// </remarks>
    public static NavDistanceSet? TryRead(string path, NavGraph graph, out string reason)
    {
        ArgumentNullException.ThrowIfNull(graph);

        if (!File.Exists(path))
        {
            reason = "no file";
            return null;
        }

        using var file = File.OpenRead(path);
        using var gz = new GZipStream(file, CompressionMode.Decompress);
        using var r = new BinaryReader(gz);

        var magic = r.ReadBytes(Magic.Length);

        if (!magic.AsSpan().SequenceEqual(Magic))
        {
            reason = "not a nav distance file";
            return null;
        }

        var version = r.ReadInt32();

        if (version != Version)
        {
            reason = $"format v{version}, this build writes v{Version}";
            return null;
        }

        var nodes = r.ReadInt32();

        if (nodes != graph.NodeCount)
        {
            reason = $"built for {nodes} nodes, the graph has {graph.NodeCount}";
            return null;
        }

        var present = r.ReadInt32();
        var hash = r.ReadBytes(16);

        if (!hash.AsSpan().SequenceEqual(HashOf(graph)))
        {
            reason = "built against a different graph";
            return null;
        }

        var count = r.ReadInt32();

        if (count is < 1 or > 8)
            throw new InvalidDataException($"{path}: {count} tables is not a possible count");

        var tables = new Dictionary<int, NavDistances>(count);
        var hops = new ushort[(long)nodes * nodes];
        var bytes = new byte[nodes * sizeof(ushort)];

        for (var t = 0; t < count; t++)
        {
            var key = r.ReadInt32();

            for (var goal = 0; goal < nodes; goal++)
            {
                r.ReadExactly(bytes);

                for (var i = 0; i < nodes; i++)
                    hops[(goal * nodes) + i] =
                        BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i * sizeof(ushort)));
            }

            // Throws on a hop naming a missing node, a missing arc or a cycle —
            // all corruption rather than staleness, since the header has already
            // established this is our graph.
            tables[key] = NavDistances.FromNextHops(
                graph,
                (key & NavDistanceSet.DoubleJumpBit) != 0,
                (key & NavDistanceSet.RocketJumpBit) != 0,
                (key & NavDistanceSet.StickyJumpBit) != 0,
                hops);
        }

        reason = "loaded";
        return NavDistanceSet.FromTables(tables, present);
    }
}
