using System.Buffers.Binary;

namespace Gg2.Maps;

/// <summary>
/// Just enough GIF to get a collision mask out of a map's embedded sprite.
/// </summary>
/// <remarks>
/// Same scope rule as <see cref="PngSprite"/>: one question, the shapes the
/// archive actually contains, a clear throw for anything else. GIF matters
/// because five of the eight platform maps use it — including
/// <c>maw_deathmatch</c>, whose 139 platforms are 40x40 GIF bubbles.
/// <para>
/// ✅ <b>EVERY frame, unioned — measured against the running game 2026-09-01,
/// not reasoned.</b> <c>stringToResource.gml</c> asks for one subimage
/// (<c>sprite_add(name, 1, 1, 0, 0, 0)</c>) and GM8 expands an animated GIF into
/// several anyway: <c>sprite_get_number</c> answers <b>4</b> for
/// <c>maw_deathmatch</c>'s bubble. The engine's collision mask is the
/// <b>union of all of them</b> — GM8 sprites default to one shared mask rather
/// than separate per-subimage masks — so the shape does NOT change as the
/// platform animates, and <c>animationspeed</c> stays decoration as far as
/// collision is concerned.
/// </para>
/// <para>
/// ⚠️ <b><c>removeback</c> is applied PER SUBIMAGE, each sampling its OWN
/// bottom-left pixel.</b> This is the part no amount of reading would have got
/// right. The bubble's later frames are 3x5 and 3x7 patches whose own corners are
/// a different colour from the first frame's, so a single global background
/// colour leaves three pixels solid that the engine clears. First-frame-only was
/// out by ten pixels, a global background by three, and this model by
/// <b>zero</b>. <c>probe-platform-mask.js</c> and
/// <c>tests/Gg2.Tests/Fixtures/sprite-mask-samples.json</c>.
/// </para>
/// </remarks>
internal static class GifSprite
{
    public static SpriteMask? Decode(ReadOnlySpan<byte> file, bool removeBackground)
    {
        // Header (6) + logical screen descriptor (7).
        if (file.Length < 13)
            return null;

        var packed = file[10];
        var globalTableSize = (packed & 0x80) != 0 ? 2 << (packed & 0x07) : 0;

        // The logical screen, which is the size GM8 reports for the sprite and
        // therefore the size of the mask however small the later frames are.
        var width = BinaryPrimitives.ReadUInt16LittleEndian(file.Slice(6, 2));
        var height = BinaryPrimitives.ReadUInt16LittleEndian(file.Slice(8, 2));

        if (width <= 0 || height <= 0)
            return null;

        var offset = 13;
        var globalTable = ReadColourTable(file, ref offset, globalTableSize);

        int? transparentIndex = null;
        var solid = new bool[width * height];
        var frames = 0;

        while (offset < file.Length)
        {
            var block = file[offset];

            if (block == 0x3B) // trailer
                break;

            if (block == 0x21) // extension
            {
                if (offset + 2 >= file.Length)
                    break;

                var label = file[offset + 1];
                offset += 2;

                // A Graphic Control Extension names the transparent index for the
                // image that follows it, which is the whole reason we walk these.
                if (label == 0xF9 && offset < file.Length && file[offset] >= 4)
                {
                    var flags = file[offset + 1];
                    transparentIndex = (flags & 0x01) != 0 ? file[offset + 4] : null;
                }

                SkipSubBlocks(file, ref offset);
                continue;
            }

            if (block != 0x2C) // not an image descriptor: unknown, give up
                break;

            if (!AddFrame(file, ref offset, globalTable, transparentIndex, removeBackground, solid, width, height))
                break;

            frames++;
            transparentIndex = null;
        }

        return frames == 0 ? null : new SpriteMask(width, height, solid);
    }

    /// <summary>
    /// Unions one subimage into the mask and advances past its data.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The background colour is THIS frame's own bottom-left pixel</b>, not
    /// the first frame's and not the logical screen's. Measured: using a single
    /// global background leaves three pixels of <c>maw_deathmatch</c>'s bubble
    /// solid that the engine clears, because its 3x5 and 3x7 patch frames have a
    /// different corner colour from the 40x40 first frame.
    /// </remarks>
    private static bool AddFrame(
        ReadOnlySpan<byte> file, ref int offset, byte[]? globalTable,
        int? transparentIndex, bool removeBackground, bool[] solid, int width, int height)
    {
        if (offset + 10 > file.Length)
            return false;

        var frameX = BinaryPrimitives.ReadUInt16LittleEndian(file.Slice(offset + 1, 2));
        var frameY = BinaryPrimitives.ReadUInt16LittleEndian(file.Slice(offset + 3, 2));
        var frameWidth = BinaryPrimitives.ReadUInt16LittleEndian(file.Slice(offset + 5, 2));
        var frameHeight = BinaryPrimitives.ReadUInt16LittleEndian(file.Slice(offset + 7, 2));

        var packed = file[offset + 9];
        var localTableSize = (packed & 0x80) != 0 ? 2 << (packed & 0x07) : 0;
        var interlaced = (packed & 0x40) != 0;

        offset += 10;
        var table = ReadColourTable(file, ref offset, localTableSize) ?? globalTable;

        if (offset >= file.Length || frameWidth <= 0 || frameHeight <= 0)
            return false;

        var minimumCodeSize = file[offset++];

        // ⚠️ Decode from a COPY of the offset, then walk the sub-block chain
        // properly to find the next frame. Lzw returns the moment it sees the end
        // code, which leaves its cursor in the middle of a sub-block — following
        // that cursor lands on a length byte read as a block type, the walk stops,
        // and only the first frame is ever seen. That is a ten-pixel error on the
        // bubble and it looks exactly like "GIF animation is not supported".
        var dataStart = offset;
        var cursor = offset;
        var indices = Lzw(file, ref cursor, minimumCodeSize, frameWidth * frameHeight);

        offset = dataStart;
        SkipSubBlocks(file, ref offset);

        if (indices is null)
            return false;

        if (interlaced)
            indices = Deinterlace(indices, frameWidth, frameHeight);

        var background = ColourOf(indices, (frameHeight - 1) * frameWidth, table);

        for (var y = 0; y < frameHeight; y++)
        for (var x = 0; x < frameWidth; x++)
        {
            var source = (y * frameWidth) + x;
            if (source >= indices.Length)
                break;

            if (transparentIndex is { } transparent && indices[source] == transparent)
                continue;

            if (removeBackground && ColourOf(indices, source, table) == background)
                continue;

            var targetX = frameX + x;
            var targetY = frameY + y;

            if (targetX < width && targetY < height)
                solid[(targetY * width) + targetX] = true;
        }

        return true;
    }

    private static int ColourOf(byte[] indices, int pixel, byte[]? table)
    {
        if (table is null)
            return indices[pixel];

        var entry = indices[pixel] * 3;
        if (entry + 2 >= table.Length)
            return -1;

        return (table[entry] << 16) | (table[entry + 1] << 8) | table[entry + 2];
    }

    private static byte[]? ReadColourTable(ReadOnlySpan<byte> file, ref int offset, int entries)
    {
        if (entries == 0)
            return null;

        var bytes = entries * 3;
        if (offset + bytes > file.Length)
            return null;

        var table = file.Slice(offset, bytes).ToArray();
        offset += bytes;
        return table;
    }

    private static void SkipSubBlocks(ReadOnlySpan<byte> file, ref int offset)
    {
        while (offset < file.Length)
        {
            var size = file[offset];
            offset += 1 + size;

            if (size == 0)
                return;
        }
    }

    /// <summary>
    /// GIF's variable-width LZW, which is the only real work in the format.
    /// </summary>
    /// <remarks>
    /// Codes grow from <c>minimumCodeSize + 1</c> bits, resetting on the clear
    /// code. The dictionary is capped at 4096 entries and simply stops growing
    /// rather than erroring, which is what real encoders rely on.
    /// </remarks>
    private static byte[]? Lzw(ReadOnlySpan<byte> file, ref int offset, int minimumCodeSize, int pixels)
    {
        if (minimumCodeSize is < 2 or > 11)
            return null;

        var clearCode = 1 << minimumCodeSize;
        var endCode = clearCode + 1;

        var prefix = new int[4096];
        var suffix = new byte[4096];
        var first = new byte[4096];

        for (var i = 0; i < clearCode; i++)
        {
            prefix[i] = -1;
            suffix[i] = (byte)i;
            first[i] = (byte)i;
        }

        var codeSize = minimumCodeSize + 1;
        var next = endCode + 1;
        var previous = -1;

        var output = new byte[pixels];
        var written = 0;

        var bits = 0;
        var bitCount = 0;
        var stack = new byte[4096];

        while (offset < file.Length)
        {
            var blockSize = file[offset++];
            if (blockSize == 0)
                break;

            if (offset + blockSize > file.Length)
                return null;

            for (var b = 0; b < blockSize; b++)
            {
                bits |= file[offset + b] << bitCount;
                bitCount += 8;

                while (bitCount >= codeSize)
                {
                    var code = bits & ((1 << codeSize) - 1);
                    bits >>= codeSize;
                    bitCount -= codeSize;

                    if (code == endCode)
                        return Finish(output, written, pixels);

                    if (code == clearCode)
                    {
                        codeSize = minimumCodeSize + 1;
                        next = endCode + 1;
                        previous = -1;
                        continue;
                    }

                    var current = code;
                    var depth = 0;

                    // An unseen code means "the previous string plus its own
                    // first byte", the standard LZW self-referential case.
                    if (code >= next)
                    {
                        if (previous < 0)
                            return null;

                        stack[depth++] = first[previous];
                        current = previous;
                    }

                    while (current >= clearCode && depth < stack.Length)
                    {
                        stack[depth++] = suffix[current];
                        current = prefix[current];
                    }

                    if (current < 0)
                        return null;

                    stack[depth++] = first[current];

                    for (var s = depth - 1; s >= 0 && written < pixels; s--)
                        output[written++] = stack[s];

                    if (previous >= 0 && next < 4096)
                    {
                        prefix[next] = previous;
                        suffix[next] = first[current];
                        first[next] = first[previous];
                        next++;

                        if (next >= (1 << codeSize) && codeSize < 12)
                            codeSize++;
                    }

                    previous = code < next ? code : previous;

                    if (written >= pixels)
                        return Finish(output, written, pixels);
                }
            }

            offset += blockSize;
        }

        return Finish(output, written, pixels);
    }

    private static byte[]? Finish(byte[] output, int written, int pixels) =>
        written == 0 ? null : output;

    /// <summary>GIF's four-pass interlace, undone.</summary>
    private static byte[] Deinterlace(byte[] indices, int width, int height)
    {
        var result = new byte[indices.Length];
        var source = 0;

        ReadOnlySpan<int> starts = [0, 4, 2, 1];
        ReadOnlySpan<int> steps = [8, 8, 4, 2];

        for (var pass = 0; pass < 4; pass++)
        for (var row = starts[pass]; row < height; row += steps[pass])
        {
            if ((source + 1) * width > indices.Length)
                return result;

            Array.Copy(indices, source * width, result, row * width, width);
            source++;
        }

        return result;
    }
}
