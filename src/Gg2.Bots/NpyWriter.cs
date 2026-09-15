using System.Globalization;
using System.Text;

namespace Gg2.Bots;

/// <summary>
/// Writes arrays in NumPy's <c>.npy</c> format.
/// </summary>
/// <remarks>
/// <b>The format is the seam, not a convenience.</b> A dataset produced here is
/// read by <c>gg2-rl</c>, whose standing rule is that it holds no game
/// knowledge — so anything it has to parse is a second place the two repos can
/// disagree about what a column means. <c>np.load</c> parses this one, and the
/// shape and dtype travel inside the file.
/// <para>
/// Version 1.0 and C order only. That is what <see cref="NpyFile"/> reads back,
/// which is what makes a round-trip test possible without a Python process.
/// </para>
/// </remarks>
public static class NpyWriter
{
    /// <summary>Writes a 2-D <c>float32</c> array, C order.</summary>
    public static void WriteFloat2(Stream stream, ReadOnlySpan<float> values, int rows, int columns)
    {
        Check(values.Length, rows, columns);
        WriteHeader(stream, "<f4", rows, columns, twoDimensional: true);

        Span<byte> scratch = stackalloc byte[4];

        foreach (var value in values)
        {
            BitConverter.TryWriteBytes(scratch, value);
            stream.Write(scratch);
        }
    }

    /// <summary>Writes a 2-D <c>uint8</c> array, C order.</summary>
    public static void WriteByte2(Stream stream, ReadOnlySpan<byte> values, int rows, int columns)
    {
        Check(values.Length, rows, columns);
        WriteHeader(stream, "|u1", rows, columns, twoDimensional: true);
        stream.Write(values);
    }

    /// <summary>Writes a 1-D <c>float32</c> array.</summary>
    public static void WriteFloat1(Stream stream, ReadOnlySpan<float> values)
    {
        WriteHeader(stream, "<f4", values.Length, 0, twoDimensional: false);

        Span<byte> scratch = stackalloc byte[4];

        foreach (var value in values)
        {
            BitConverter.TryWriteBytes(scratch, value);
            stream.Write(scratch);
        }
    }

    /// <summary>Writes a 1-D <c>int32</c> array.</summary>
    public static void WriteInt1(Stream stream, ReadOnlySpan<int> values)
    {
        WriteHeader(stream, "<i4", values.Length, 0, twoDimensional: false);

        Span<byte> scratch = stackalloc byte[4];

        foreach (var value in values)
        {
            BitConverter.TryWriteBytes(scratch, value);
            stream.Write(scratch);
        }
    }

    /// <summary>Writes a 1-D <c>uint8</c> array.</summary>
    public static void WriteByte1(Stream stream, ReadOnlySpan<byte> values)
    {
        WriteHeader(stream, "|u1", values.Length, 0, twoDimensional: false);
        stream.Write(values);
    }

    private static void Check(int length, int rows, int columns)
    {
        // Checked rather than trusted because the failure is silent: a shape
        // that disagrees with the data produces a file numpy loads happily and
        // reads as garbage from the first mismatched row onwards.
        if ((long)rows * columns != length)
        {
            throw new ArgumentException(
                $"a {rows}x{columns} array holds {(long)rows * columns} values, not {length}");
        }
    }

    private static void WriteHeader(
        Stream stream, string dtype, int rows, int columns, bool twoDimensional)
    {
        var shape = twoDimensional
            ? string.Create(CultureInfo.InvariantCulture, $"({rows}, {columns})")
            : string.Create(CultureInfo.InvariantCulture, $"({rows},)");

        var dictionary =
            $"{{'descr': '{dtype}', 'fortran_order': False, 'shape': {shape}, }}";

        // The format requires the header to be a multiple of 64 bytes including
        // the ten-byte preamble, padded with spaces and ended with a newline.
        // numpy reads an unpadded header too, but memory-maps only an aligned
        // one — and a dataset large enough to be worth mmapping is the case this
        // exists for.
        var prefix = NpyFile.Magic.Length + 2 + 2;
        var padding = 64 - ((prefix + dictionary.Length + 1) % 64);
        var header = dictionary + new string(' ', padding == 64 ? 0 : padding) + "\n";
        var bytes = Encoding.ASCII.GetBytes(header);

        stream.Write(NpyFile.Magic);
        stream.WriteByte(1);
        stream.WriteByte(0);

        Span<byte> length = stackalloc byte[2];
        BitConverter.TryWriteBytes(length, (ushort)bytes.Length);
        stream.Write(length);

        stream.Write(bytes);
    }
}
