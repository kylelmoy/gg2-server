using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace GG2.Host;

/// <summary>
/// Reads a map PNG's art as RGB and writes a new map PNG with level data in it,
/// for <see cref="CellMapGenerator"/>.
/// </summary>
/// <remarks>
/// <b>Separate from <c>GG2.Maps.PngSprite</c> on purpose.</b> That decoder answers
/// one question — is this pixel opaque, under GM8's <c>removeback</c> rule — and
/// its own remarks forbid growing it. This one wants colours and nothing else,
/// and only ever reads the shipped map art: bit depth 8, non-interlaced, colour
/// types 2, 3 and 6 (alpha dropped). Anything else throws with the reason rather
/// than guessing.
/// </remarks>
internal static class MapPng
{
    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>The art as 0xRRGGBB per pixel, row-major from the top.</summary>
    public static (int Width, int Height, int[] Rgb) DecodeRgb(ReadOnlySpan<byte> png)
    {
        if (png.Length < 33 || !png[..8].SequenceEqual(Signature))
            throw new InvalidDataException("not a PNG");

        var width = (int)BinaryPrimitives.ReadUInt32BigEndian(png.Slice(16, 4));
        var height = (int)BinaryPrimitives.ReadUInt32BigEndian(png.Slice(20, 4));
        var bitDepth = png[24];
        var colourType = png[25];
        var interlace = png[28];

        if (bitDepth != 8 || interlace != 0 || colourType is not (2 or 3 or 6))
        {
            throw new InvalidDataException(
                $"map art: bit depth {bitDepth}, colour type {colourType}, interlace {interlace} is not supported");
        }

        var palette = Array.Empty<int>();
        using var idat = new MemoryStream();

        for (var offset = 8; offset + 8 <= png.Length;)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.Slice(offset, 4));
            var type = png.Slice(offset + 4, 4);
            var data = png.Slice(offset + 8, length);

            if (type.SequenceEqual("PLTE"u8))
            {
                palette = new int[length / 3];
                for (var i = 0; i < palette.Length; i++)
                    palette[i] = (data[i * 3] << 16) | (data[(i * 3) + 1] << 8) | data[(i * 3) + 2];
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                idat.Write(data);
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                break;
            }

            offset += 12 + length;
        }

        var channels = colourType switch { 2 => 3, 6 => 4, _ => 1 };
        var stride = width * channels;
        var raw = Unfilter(Inflate(idat.ToArray()), stride, height, channels);

        var rgb = new int[width * height];
        for (var y = 0; y < height; y++)
        {
            var row = raw.AsSpan(y * stride, stride);
            for (var x = 0; x < width; x++)
            {
                rgb[(y * width) + x] = colourType == 3
                    ? (row[x] < palette.Length ? palette[row[x]] : 0)
                    : (row[x * channels] << 16) | (row[(x * channels) + 1] << 8) | row[(x * channels) + 2];
            }
        }

        return (width, height, rgb);
    }

    private static byte[] Inflate(byte[] compressed)
    {
        using var input = new MemoryStream(compressed, writable: false);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] Unfilter(byte[] filtered, int stride, int height, int bpp)
    {
        if (filtered.Length < (stride + 1) * height)
            throw new InvalidDataException("map art: image data is short");

        var raw = new byte[stride * height];

        for (var y = 0; y < height; y++)
        {
            var filter = filtered[y * (stride + 1)];
            var source = filtered.AsSpan((y * (stride + 1)) + 1, stride);
            var row = raw.AsSpan(y * stride, stride);
            var prior = y > 0 ? raw.AsSpan((y - 1) * stride, stride) : Span<byte>.Empty;

            for (var i = 0; i < stride; i++)
            {
                int a = i >= bpp ? row[i - bpp] : 0;
                int b = y > 0 ? prior[i] : 0;
                int c = i >= bpp && y > 0 ? prior[i - bpp] : 0;

                row[i] = (byte)(source[i] + filter switch
                {
                    0 => 0,
                    1 => a,
                    2 => b,
                    3 => (a + b) / 2,
                    4 => Paeth(a, b, c),
                    _ => throw new InvalidDataException($"map art: filter type {filter}"),
                });
            }
        }

        return raw;
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    /// <summary>
    /// A map PNG: the art as an indexed image when it has 256 colours or fewer
    /// (truecolour otherwise), and the level data in a <c>zTXt</c> chunk under
    /// <see cref="GG2.Maps.LevelData.Keyword"/> — the chunk the game itself writes.
    /// </summary>
    public static byte[] Encode(int width, int height, int[] rgb, string levelData)
    {
        // Indexed when it fits: cut from palette maps and filled with one wall
        // colour, most cell maps do, and it is about half the size.
        var palette = new Dictionary<int, byte>();
        foreach (var colour in rgb)
        {
            if (!palette.ContainsKey(colour))
            {
                if (palette.Count == 256)
                {
                    palette = null;
                    break;
                }

                palette[colour] = (byte)palette.Count;
            }
        }

        var channels = palette is null ? 3 : 1;
        var stride = width * channels;
        var raw = new byte[stride * height];

        for (var i = 0; i < rgb.Length; i++)
        {
            if (palette is not null)
            {
                raw[i] = palette[rgb[i]];
            }
            else
            {
                raw[i * 3] = (byte)(rgb[i] >> 16);
                raw[(i * 3) + 1] = (byte)(rgb[i] >> 8);
                raw[(i * 3) + 2] = (byte)rgb[i];
            }
        }

        using var file = new MemoryStream();
        file.Write(Signature);

        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), (uint)height);
        header[8] = 8;
        header[9] = (byte)(palette is null ? 2 : 3);
        WriteChunk(file, "IHDR", header);

        if (palette is not null)
        {
            var plte = new byte[palette.Count * 3];
            foreach (var (colour, index) in palette)
            {
                plte[index * 3] = (byte)(colour >> 16);
                plte[(index * 3) + 1] = (byte)(colour >> 8);
                plte[(index * 3) + 2] = (byte)colour;
            }

            WriteChunk(file, "PLTE", plte);
        }

        WriteChunk(file, "IDAT", Deflate(Filter(raw, stride, height, channels)));

        var text = new MemoryStream();
        text.Write(Encoding.Latin1.GetBytes(GG2.Maps.LevelData.Keyword));
        text.WriteByte(0);
        text.WriteByte(0); // compression method: zlib
        text.Write(Deflate(Encoding.Latin1.GetBytes(levelData)));
        WriteChunk(file, "zTXt", text.ToArray());

        WriteChunk(file, "IEND", []);
        return file.ToArray();
    }

    /// <summary>
    /// Each row gets the filter whose output has the smallest sum of absolute
    /// values — the usual heuristic, and the one libpng uses.
    /// </summary>
    private static byte[] Filter(byte[] raw, int stride, int height, int bpp)
    {
        var output = new byte[(stride + 1) * height];
        var candidate = new byte[stride];

        for (var y = 0; y < height; y++)
        {
            var row = raw.AsSpan(y * stride, stride);
            var prior = y > 0 ? raw.AsSpan((y - 1) * stride, stride) : Span<byte>.Empty;
            var bestScore = long.MaxValue;

            for (byte filter = 0; filter <= 4; filter++)
            {
                long score = 0;
                for (var i = 0; i < stride; i++)
                {
                    int a = i >= bpp ? row[i - bpp] : 0;
                    int b = y > 0 ? prior[i] : 0;
                    int c = i >= bpp && y > 0 ? prior[i - bpp] : 0;

                    candidate[i] = (byte)(row[i] - filter switch
                    {
                        0 => 0,
                        1 => a,
                        2 => b,
                        3 => (a + b) / 2,
                        _ => Paeth(a, b, c),
                    });
                    score += Math.Abs((int)(sbyte)candidate[i]);
                }

                if (score < bestScore)
                {
                    bestScore = score;
                    output[y * (stride + 1)] = filter;
                    candidate.CopyTo(output.AsSpan((y * (stride + 1)) + 1, stride));
                }
            }
        }

        return output;
    }

    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            zlib.Write(data);

        return output.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(word, (uint)data.Length);
        stream.Write(word);

        var typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);

        BinaryPrimitives.WriteUInt32BigEndian(word, ~Crc(Crc(uint.MaxValue, typeBytes), data));
        stream.Write(word);
    }

    // PNG's CRC-32 (ISO 3309), bitwise: the chunks here are small and written
    // once, so a table is not worth carrying.
    private static uint Crc(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }

        return crc;
    }
}
