using System.Buffers.Binary;
using System.IO.Compression;
using Gg2.Maps;

namespace Gg2.Tests;

/// <summary>
/// Which PNG text chunks the level-data reader accepts.
/// </summary>
/// <remarks>
/// <b>Every one of the 22 shipped maps is <c>zTXt</c>, so the whole test
/// suite passed while this port read only <c>zTXt</c> — and rejected 438 real
/// custom maps that the game opens fine.</b> That is the shape of gap a fixture
/// corpus cannot show you: the corpus was produced by the one writer whose
/// output already worked.
/// <para>
/// The game reads through libpng's <c>png_get_text</c>
/// (<c>Extensions/GG2DLL/GG2DLL/GG2DLL.cpp:357-368</c>), which returns
/// <c>tEXt</c>, <c>zTXt</c> and <c>iTXt</c> alike and has already decompressed
/// them; the DLL then matches on the keyword alone. It <em>writes</em> <c>zTXt</c>
/// (:288), which is why maps saved by GG2 itself always worked and maps from
/// other editors did not.
/// </para>
/// <para>
/// These build the PNGs by hand rather than shipping fixtures: the reader walks
/// chunks and does not verify CRCs, so a minimal signature + IHDR + text + IEND
/// is enough to exercise exactly the branch under test.
/// </para>
/// </remarks>
public sealed class LevelDataChunkTests
{
    private const string Body = "{WALKMASK}\n1x1\n0\n{END WALKMASK}";

    [Fact]
    public void ATextChunkIsRead() =>
        Assert.Equal(Body, LevelData.Extract(Png(TextChunk())));

    [Fact]
    public void ACompressedTextChunkIsRead() =>
        Assert.Equal(Body, LevelData.Extract(Png(CompressedTextChunk())));

    [Fact]
    public void AnUncompressedInternationalTextChunkIsRead() =>
        Assert.Equal(Body, LevelData.Extract(Png(InternationalChunk(compressed: false))));

    [Fact]
    public void ACompressedInternationalTextChunkIsRead() =>
        Assert.Equal(Body, LevelData.Extract(Png(InternationalChunk(compressed: true))));

    /// <summary>A text chunk with somebody else's keyword is not the level data.</summary>
    /// <remarks>
    /// Map PNGs routinely carry a "Software" or "Comment" chunk from whatever
    /// editor wrote them, so the keyword test is what stops the reader returning
    /// an editor's name as a level.
    /// </remarks>
    [Fact]
    public void AnUnrelatedKeywordIsIgnored()
    {
        var png = Png(Chunk("tEXt", Bytes("Software\0Some Map Editor 1.2")), TextChunk());

        Assert.Equal(Body, LevelData.Extract(png));
    }

    [Fact]
    public void APngWithNoLevelDataStillThrows()
    {
        var png = Png(Chunk("tEXt", Bytes("Comment\0just a picture")));

        Assert.Throws<MapFormatException>(() => LevelData.Extract(png));
    }

    // --- builders ------------------------------------------------------------

    private static byte[] TextChunk() => Chunk("tEXt", Bytes(LevelData.Keyword + "\0" + Body));

    private static byte[] CompressedTextChunk()
    {
        // keyword, NUL, one compression-method byte, then the zlib stream.
        var head = Bytes(LevelData.Keyword + "\0\0");
        return Chunk("zTXt", [.. head, .. Deflate(Bytes(Body))]);
    }

    private static byte[] InternationalChunk(bool compressed)
    {
        // keyword, NUL, compression flag, compression method, language NUL,
        // translated-keyword NUL, then the text.
        var head = Bytes(LevelData.Keyword + "\0");
        var fields = new byte[] { (byte)(compressed ? 1 : 0), 0, 0, 0 };
        var text = compressed ? Deflate(Bytes(Body)) : Bytes(Body);

        return Chunk("iTXt", [.. head, .. fields, .. text]);
    }

    private static byte[] Png(params byte[][] chunks)
    {
        var png = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        // A 1x1 8-bit greyscale IHDR, which is enough for ReadImageSize too.
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0, 4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4, 4), 1);
        ihdr[8] = 8;

        png.AddRange(Chunk("IHDR", ihdr));

        foreach (var chunk in chunks)
            png.AddRange(chunk);

        png.AddRange(Chunk("IEND", []));
        return [.. png];
    }

    private static byte[] Chunk(string type, byte[] data)
    {
        var chunk = new byte[12 + data.Length];

        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(0, 4), (uint)data.Length);
        Bytes(type).CopyTo(chunk.AsSpan(4, 4));
        data.CopyTo(chunk.AsSpan(8));

        // The CRC is left zero: the reader walks chunks by length and never
        // checks it, exactly as libpng does not reject on it here either.
        return chunk;
    }

    /// <summary>Latin-1, which is how the reader decodes a GM8 byte string.</summary>
    private static byte[] Bytes(string text)
    {
        var bytes = new byte[text.Length];
        for (var i = 0; i < text.Length; i++)
            bytes[i] = (byte)text[i];

        return bytes;
    }

    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionMode.Compress, leaveOpen: true))
            zlib.Write(data);

        return output.ToArray();
    }
}
