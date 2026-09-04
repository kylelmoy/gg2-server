using System.Buffers.Binary;
using System.IO.Compression;

namespace Gg2.Maps;

/// <summary>
/// Just enough PNG to get a collision mask out of a map's embedded sprite.
/// </summary>
/// <remarks>
/// <b>Not a general PNG decoder and must not become one.</b> It answers one
/// question — is this pixel opaque — for the shapes the map archive actually
/// contains, and throws with a clear message for everything else rather than
/// guessing. Written by hand because this project carries no image dependency and
/// the mask has to match GM8's <c>removeback</c> semantics exactly, which no
/// library will do for us.
/// <para>
/// Supported: bit depth 8, non-interlaced, colour types 0, 2, 3, 4 and 6, with
/// <c>tRNS</c> for palette and for the two opaque types. That covers all 31
/// sprites in the archive's platform maps.
/// </para>
/// </remarks>
internal static class PngSprite
{
    public static SpriteMask? Decode(ReadOnlySpan<byte> file, bool removeBackground)
    {
        if (file.Length < 33)
            return null;

        var width = (int)BinaryPrimitives.ReadUInt32BigEndian(file.Slice(16, 4));
        var height = (int)BinaryPrimitives.ReadUInt32BigEndian(file.Slice(20, 4));

        if (width <= 0 || height <= 0)
            return null;

        var bitDepth = file[24];
        var colourType = file[25];
        var interlace = file[28];

        if (bitDepth != 8)
            throw new MapFormatException($"embedded sprite: PNG bit depth {bitDepth} is not supported (only 8)");

        if (interlace != 0)
            throw new MapFormatException("embedded sprite: interlaced PNG is not supported");

        var samples = colourType switch
        {
            0 => 1,      // greyscale
            2 => 3,      // truecolour
            3 => 1,      // palette index
            4 => 2,      // greyscale + alpha
            6 => 4,      // truecolour + alpha
            _ => throw new MapFormatException($"embedded sprite: PNG colour type {colourType} is not supported"),
        };

        var idat = new MemoryStream();
        byte[]? palette = null;
        byte[]? transparency = null;

        var offset = 8;
        while (offset + 8 <= file.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(file.Slice(offset, 4));
            if (length > int.MaxValue || offset + 12L + length > file.Length)
                break;

            var type = file.Slice(offset + 4, 4);
            var data = file.Slice(offset + 8, (int)length);

            if (type.SequenceEqual("PLTE"u8))
                palette = data.ToArray();
            else if (type.SequenceEqual("tRNS"u8))
                transparency = data.ToArray();
            else if (type.SequenceEqual("IDAT"u8))
                idat.Write(data);
            else if (type.SequenceEqual("IEND"u8))
                break;

            offset += 12 + (int)length;
        }

        if (idat.Length == 0)
            return null;

        var raw = Inflate(idat.GetBuffer().AsSpan(0, (int)idat.Length), height * ((width * samples) + 1));
        var pixels = Unfilter(raw, width, height, samples);

        return BuildMask(pixels, width, height, samples, colourType, palette, transparency, removeBackground);
    }

    private static SpriteMask BuildMask(
        byte[] pixels, int width, int height, int samples, byte colourType,
        byte[]? palette, byte[]? transparency, bool removeBackground)
    {
        var solid = new bool[width * height];

        // removeback takes the colour of the bottom-left pixel and makes every
        // pixel of that colour transparent. Sampled before anything is decided,
        // so the corner pixel itself is also removed.
        var backgroundIndex = SpriteMask.BackgroundPixelIndex(width, height);
        var background = ColourAt(pixels, backgroundIndex, samples, colourType, palette);

        for (var i = 0; i < solid.Length; i++)
        {
            if (!IsOpaque(pixels, i, samples, colourType, transparency))
                continue;

            if (removeBackground && ColourAt(pixels, i, samples, colourType, palette) == background)
                continue;

            solid[i] = true;
        }

        return new SpriteMask(width, height, solid);
    }

    /// <summary>Whether the pixel's own alpha or <c>tRNS</c> entry leaves it visible.</summary>
    private static bool IsOpaque(byte[] pixels, int index, int samples, byte colourType, byte[]? transparency)
    {
        var at = index * samples;

        return colourType switch
        {
            // Greyscale and truecolour carry no alpha; tRNS names one colour.
            0 => transparency is not { Length: >= 2 } || pixels[at] != transparency[1],
            2 => transparency is not { Length: >= 6 }
                 || pixels[at] != transparency[1]
                 || pixels[at + 1] != transparency[3]
                 || pixels[at + 2] != transparency[5],

            // A palette index is transparent when tRNS gives it alpha 0. tRNS may
            // be shorter than the palette; entries past its end are opaque.
            3 => transparency is null
                 || pixels[at] >= transparency.Length
                 || transparency[pixels[at]] != 0,

            4 => pixels[at + 1] != 0,
            6 => pixels[at + 3] != 0,
            _ => true,
        };
    }

    /// <summary>The pixel's colour as a packed RGB, for the background comparison.</summary>
    private static int ColourAt(byte[] pixels, int index, int samples, byte colourType, byte[]? palette)
    {
        var at = index * samples;

        switch (colourType)
        {
            case 0:
            case 4:
                return (pixels[at] << 16) | (pixels[at] << 8) | pixels[at];

            case 3:
            {
                var entry = pixels[at] * 3;
                if (palette is null || entry + 2 >= palette.Length)
                    return -1;

                return (palette[entry] << 16) | (palette[entry + 1] << 8) | palette[entry + 2];
            }

            default:
                return (pixels[at] << 16) | (pixels[at + 1] << 8) | pixels[at + 2];
        }
    }

    /// <summary>
    /// Reverses the per-scanline filters, which is the only part of PNG decoding
    /// with any state in it.
    /// </summary>
    /// <remarks>
    /// Filter types 0-4 from the PNG spec, applied per byte with <c>a</c> the
    /// byte one pixel left, <c>b</c> the byte above, and <c>c</c> the byte above
    /// left. Bytes before the start of a row count as zero.
    /// </remarks>
    private static byte[] Unfilter(byte[] raw, int width, int height, int samples)
    {
        var stride = width * samples;
        var pixels = new byte[stride * height];

        for (var row = 0; row < height; row++)
        {
            var source = row * (stride + 1);
            if (source >= raw.Length)
                break;

            var filter = raw[source];
            var target = row * stride;

            for (var i = 0; i < stride; i++)
            {
                if (source + 1 + i >= raw.Length)
                    break;

                int value = raw[source + 1 + i];

                var a = i >= samples ? pixels[target + i - samples] : 0;
                var b = row > 0 ? pixels[target - stride + i] : 0;
                var c = row > 0 && i >= samples ? pixels[target - stride + i - samples] : 0;

                pixels[target + i] = filter switch
                {
                    0 => (byte)value,
                    1 => (byte)(value + a),
                    2 => (byte)(value + b),
                    3 => (byte)(value + ((a + b) / 2)),
                    4 => (byte)(value + Paeth(a, b, c)),
                    _ => throw new MapFormatException($"embedded sprite: unknown PNG filter {filter}"),
                };
            }
        }

        return pixels;
    }

    /// <summary>The PNG spec's Paeth predictor, verbatim.</summary>
    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);

        if (pa <= pb && pa <= pc)
            return a;

        return pb <= pc ? b : c;
    }

    private static byte[] Inflate(ReadOnlySpan<byte> compressed, int expected)
    {
        using var input = new MemoryStream(compressed.ToArray(), writable: false);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(expected);

        zlib.CopyTo(output);
        return output.ToArray();
    }
}
