using System.Buffers.Binary;
using System.IO.Compression;
using Gg2.Protocol;

namespace Gg2.Maps;

public sealed class MapFormatException(string message) : Exception(message);

/// <summary>
/// A GG2 map PNG carries its level data — entities and the collision walkmask —
/// deflated into a <c>zTXt</c> chunk keyed "Gang Garrison 2 Level Data".
/// </summary>
/// <remarks>
/// This is how the game itself loads a map
/// (<c>Scripts/Maps/CustomMaps/CustomMapProcessLevelData.gml</c>), which means
/// the mask is available with no game running, at exactly the resolution a
/// collision cell is. The art in the PNG is decoration; the mask is the truth,
/// and the two routinely disagree.
/// <para>
/// Ported from <c>gg2-agent/tools/walkmask.js</c>, which is the existing
/// decoder and has been verified cell-for-cell against the running game.
/// </para>
/// </remarks>
public static class LevelData
{
    public const string Keyword = "Gang Garrison 2 Level Data";

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// Pulls the level-data text out of a map PNG's <c>zTXt</c> chunk.
    /// </summary>
    /// <remarks>
    /// The text is a GM8 byte string, so it is decoded Latin-1: the entity block
    /// can carry embedded sprite resources as arbitrary bytes.
    /// </remarks>
    public static string Extract(ReadOnlySpan<byte> png)
    {
        if (png.Length < PngSignature.Length || !png[..PngSignature.Length].SequenceEqual(PngSignature))
            throw new MapFormatException("not a PNG: signature mismatch");

        var offset = PngSignature.Length;

        // PNG chunk lengths and CRCs are big-endian. This is the one place in
        // the project where that is true — the GG2 wire protocol is not PNG.
        while (offset + 8 <= png.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(offset, 4));
            if (length > int.MaxValue || offset + 12L + length > png.Length)
                throw new MapFormatException($"chunk at offset {offset} claims {length} bytes and runs off the end");

            var type = png.Slice(offset + 4, 4);
            var data = png.Slice(offset + 8, (int)length);

            if (type.SequenceEqual("zTXt"u8))
            {
                var nul = data.IndexOf((byte)0);
                if (nul > 0 && Gg2Text.Decode(data[..nul]) == Keyword)
                {
                    // After the keyword's NUL comes one compression-method byte
                    // (0 = zlib deflate), then the zlib stream itself.
                    var compressed = data[(nul + 2)..];
                    return Inflate(compressed);
                }
            }

            if (type.SequenceEqual("IEND"u8))
                break;

            offset += 12 + (int)length;
        }

        throw new MapFormatException($"PNG carries no \"{Keyword}\" chunk");
    }

    private static string Inflate(ReadOnlySpan<byte> compressed)
    {
        using var input = new MemoryStream(compressed.ToArray(), writable: false);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return Gg2Text.Decode(output.GetBuffer().AsSpan(0, (int)output.Length));
    }

    /// <summary>
    /// The body of a <c>{TAG}\n ... \n{END TAG}</c> block, or null if absent.
    /// </summary>
    /// <remarks>
    /// The reference locates blocks by <c>string_pos</c> on the open and close
    /// tags and slices between them
    /// (<c>CustomMapProcessLevelData.gml</c>), so a nested or repeated tag is
    /// not a case that arises — first open tag to first close tag is the whole
    /// rule.
    /// </remarks>
    public static string? TryGetBlock(string levelData, string tag)
    {
        var open = "{" + tag + "}\n";
        var close = "\n{END " + tag + "}";

        var start = levelData.IndexOf(open, StringComparison.Ordinal);
        if (start < 0)
            return null;

        start += open.Length;

        var end = levelData.IndexOf(close, start, StringComparison.Ordinal);
        return end < 0 ? null : levelData[start..end];
    }

    public static string GetBlock(string levelData, string tag) =>
        TryGetBlock(levelData, tag)
        ?? throw new MapFormatException($"level data has no {{{tag}}} block");
}
