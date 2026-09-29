using System.Buffers.Binary;
using System.IO.Compression;
using GG2.Protocol;

namespace GG2.Maps;

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
/// Ported from the JavaScript walkmask decoder, which is the existing
/// decoder and has been verified cell-for-cell against the running game.
/// </para>
/// </remarks>
public static class LevelData
{
    public const string Keyword = "Gang Garrison 2 Level Data";

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// The PNG's own pixel dimensions — the map <b>art</b>, which is not always
    /// the same size as the walkmask inside it.
    /// </summary>
    /// <remarks>
    /// This is what <c>map_width()</c> and <c>map_height()</c> measure:
    /// <c>background_width[7] * background_xscale[7]</c>
    /// (<c>Scripts/Maps/CustomMaps/map_width.gml</c>), the background, not the
    /// collision sprite. Twenty-one of the twenty-two shipped maps have art and
    /// mask the same size, which is why the difference is easy to miss;
    /// <c>gg_debug</c> is 120x64 of art over a 118x69 mask.
    /// </remarks>
    public static (int Width, int Height) ReadImageSize(ReadOnlySpan<byte> png)
    {
        if (png.Length < 24 || !png[..PngSignature.Length].SequenceEqual(PngSignature))
            throw new MapFormatException("not a PNG: signature mismatch");

        // IHDR is required to be the first chunk, and its width and height are
        // the first eight bytes of its data.
        if (!png.Slice(12, 4).SequenceEqual("IHDR"u8))
            throw new MapFormatException("PNG does not open with IHDR");

        return (
            (int)BinaryPrimitives.ReadUInt32BigEndian(png.Slice(16, 4)),
            (int)BinaryPrimitives.ReadUInt32BigEndian(png.Slice(20, 4)));
    }

    /// <summary>
    /// Pulls the level-data text out of a map PNG's text chunk — <c>tEXt</c>,
    /// <c>zTXt</c> or <c>iTXt</c>, as libpng and therefore the game accept.
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

            // ALL THREE text chunk types, not just zTXt. The game reads its
            // level data through libpng's png_get_text
            // (Extensions/GG2DLL/GG2DLL/GG2DLL.cpp:357-368), which hands back
            // tEXt, zTXt and iTXt alike — already decompressed — and the DLL then
            // matches on the keyword alone, ignoring how it was stored.
            //
            // Reading only zTXt is stricter than the game: the game WRITES zTXt
            // (:288), so every map saved by GG2 itself works that way, but maps
            // written by other tools may use tEXt or iTXt and still open fine in
            // GG2 — so all three must be accepted here too.
            if (TryReadText(type, data, out var text))
                return text;

            if (type.SequenceEqual("IEND"u8))
                break;

            offset += 12 + (int)length;
        }

        throw new MapFormatException($"PNG carries no \"{Keyword}\" chunk");
    }

    /// <summary>
    /// The level-data text out of one PNG text chunk, if that is what it is.
    /// </summary>
    /// <remarks>
    /// The three layouts, from the PNG spec:
    /// <list type="bullet">
    /// <item><c>tEXt</c>: <c>keyword \0 text</c> — Latin-1, uncompressed.</item>
    /// <item><c>zTXt</c>: <c>keyword \0 method text</c> — one method byte, then
    /// a zlib stream.</item>
    /// <item><c>iTXt</c>: <c>keyword \0 flag method language \0 translated \0
    /// text</c> — two extra NUL-terminated fields before the text, and the text
    /// is compressed only when the flag is 1.</item>
    /// </list>
    /// <para>
    /// <b><c>iTXt</c> text is UTF-8 by the spec, and GG2 level data is raw
    /// bytes.</b> A map stored that way would have every byte above 0x7F
    /// re-encoded, and the entity block can carry embedded sprite resources as
    /// arbitrary bytes — so it would round-trip wrong. It is accepted here
    /// because libpng accepts it and the game therefore does; the decoding below
    /// is Latin-1 like the others rather than UTF-8. <b>If an iTXt map ever
    /// turns up broken, this is the line.</b>
    /// </para>
    /// </remarks>
    private static bool TryReadText(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data, out string text)
    {
        text = string.Empty;

        var compressed = type.SequenceEqual("zTXt"u8);
        var international = type.SequenceEqual("iTXt"u8);

        if (!compressed && !international && !type.SequenceEqual("tEXt"u8))
            return false;

        var nul = data.IndexOf((byte)0);
        if (nul <= 0 || GG2Text.Decode(data[..nul]) != Keyword)
            return false;

        var body = data[(nul + 1)..];

        if (international)
        {
            // flag, method, then language and translated-keyword, each
            // NUL-terminated and both usually empty.
            if (body.Length < 2)
                return false;

            var compressionFlag = body[0];
            body = body[2..];

            for (var field = 0; field < 2; field++)
            {
                var end = body.IndexOf((byte)0);
                if (end < 0)
                    return false;

                body = body[(end + 1)..];
            }

            text = compressionFlag == 1 ? Inflate(body) : GG2Text.Decode(body);
            return true;
        }

        // zTXt spends one byte on the compression method (0 = zlib); tEXt is the
        // text itself with nothing in between.
        text = compressed ? Inflate(body[1..]) : GG2Text.Decode(body);
        return true;
    }

    private static string Inflate(ReadOnlySpan<byte> compressed)
    {
        using var input = new MemoryStream(compressed.ToArray(), writable: false);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return GG2Text.Decode(output.GetBuffer().AsSpan(0, (int)output.Length));
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
