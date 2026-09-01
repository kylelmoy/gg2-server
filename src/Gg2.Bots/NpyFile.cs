using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Gg2.Bots;

/// <summary>
/// The narrowest reader of NumPy's <c>.npy</c> and <c>.npz</c> that will load a
/// <c>gg2-rl</c> checkpoint, and nothing wider.
/// </summary>
/// <remarks>
/// <b>Phase 4 needs a trained policy in this process</b>, and the artefact is
/// whatever <c>numpy.savez</c> writes. Reading that format directly is the
/// alternative to inventing one here — <c>docs/ENV.md</c>'s standing argument
/// against a weights format in this repo is that it would be **a second source of
/// truth about what a checkpoint is**, and reading theirs keeps there being one.
/// <para>
/// ⚠️ <b>Deliberately narrow, and it REFUSES rather than guesses.</b> It handles
/// little-endian <c>f4</c>/<c>f8</c> arrays and one pickled shape: a 1-D object
/// array of strings, which is how the metadata rides along. Anything else throws.
/// A loader that coped with more would be a loader nobody had tested on more.
/// </para>
/// <para>
/// ⚠️ <b>Why a misparse is safe.</b> Every field it recovers is checked against a
/// constant this build already knows — the simulation stamp, the action encoding
/// version, the observation layout version, the head shape, the observation
/// length. A parse that went wrong produces values that fail those checks and the
/// checkpoint is refused; producing exactly the seven right strings by accident is
/// not a failure mode. See <see cref="PolicyNetwork.Load"/>.
/// </para>
/// </remarks>
public static class NpyFile
{
    /// <summary>
    /// <c>.npy</c>'s six-byte signature: <c>0x93</c> then <c>NUMPY</c>.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Written as bytes, not as a string literal, because 0x93 is not
    /// ASCII.</b> <c>"NUMPY"u8</c> is the obvious spelling and it is wrong:
    /// the escape is the CHARACTER U+0093, which UTF-8 encodes as two bytes, so
    /// the literal is seven bytes long and matches nothing. It cost a debugging
    /// round trip — the file was read correctly, the archive was read correctly,
    /// and the header check refused it.
    /// </remarks>
    private static ReadOnlySpan<byte> Magic => [0x93, (byte)'N', (byte)'U', (byte)'M', (byte)'P', (byte)'Y'];

    /// <summary>One array out of an <c>.npy</c> stream: its shape and its values.</summary>
    public readonly record struct Array2(int Rows, int Columns, float[] Values)
    {
        public float this[int row, int column] => Values[(row * Columns) + column];
    }

    /// <summary>
    /// Every entry of an <c>.npz</c>, by name without the <c>.npy</c> suffix.
    /// </summary>
    public static Dictionary<string, byte[]> ReadArchive(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        foreach (var entry in zip.Entries)
        {
            using var open = entry.Open();
            using var buffer = new MemoryStream();

            open.CopyTo(buffer);

            var name = entry.Name.EndsWith(".npy", StringComparison.Ordinal)
                ? entry.Name[..^4]
                : entry.Name;

            entries[name] = buffer.ToArray();
        }

        return entries;
    }

    /// <summary>
    /// Reads a numeric array, flattened row-major, with its shape.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Fortran order is REFUSED, not transposed.</b> Transposing silently
    /// would be the same class of bug as a wrong head offset: a plausible matrix
    /// of the wrong thing. Nothing <c>numpy.savez</c> writes from a C-ordered
    /// array is Fortran-ordered, so this only fires on a file that was not
    /// produced the way the loader expects.
    /// </remarks>
    public static Array2 ReadArray(byte[] npy)
    {
        ArgumentNullException.ThrowIfNull(npy);

        var (header, at) = ReadHeader(npy);
        var descr = Field(header, "descr");
        var shape = Shape(header);

        if (Field(header, "fortran_order") != "False")
            throw new InvalidDataException("a Fortran-ordered array is refused rather than transposed");

        var rows = shape.Length switch
        {
            1 => 1,
            2 => shape[0],
            _ => throw new InvalidDataException($"only 1-D and 2-D arrays are read, got {shape.Length}-D"),
        };

        var columns = shape.Length == 1 ? shape[0] : shape[1];
        var count = rows * columns;
        var values = new float[count];

        switch (descr)
        {
            case "<f4":
                for (var i = 0; i < count; i++)
                    values[i] = BitConverter.ToSingle(npy, at + (i * 4));

                break;

            case "<f8":
                for (var i = 0; i < count; i++)
                    values[i] = (float)BitConverter.ToDouble(npy, at + (i * 8));

                break;

            default:
                throw new InvalidDataException($"dtype '{descr}' is not one this reader handles");
        }

        return new Array2(rows, columns, values);
    }

    /// <summary>
    /// Reads a 1-D object array of strings — the shape metadata rides in.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Not a pickle interpreter.</b> It finds the element list that numpy
    /// emits for an object array — <c>NEWFALSE, EMPTY_LIST, MEMOIZE, MARK</c> —
    /// and then reads exactly the <c>SHORT_BINUNICODE</c> entries that follow,
    /// stopping at <c>APPENDS</c>. Any other opcode in that run, or a count that
    /// disagrees with the header's shape, is a refusal.
    /// <para>
    /// ⚠️ <b>The count cross-check is the load-bearing part.</b> The header says
    /// how many elements there should be, independently of the pickle, so a scan
    /// that locked on to the wrong place has to produce the right NUMBER of
    /// strings as well as the right ones.
    /// </para>
    /// </remarks>
    public static string[] ReadStrings(byte[] npy)
    {
        ArgumentNullException.ThrowIfNull(npy);

        var (header, _) = ReadHeader(npy);

        if (Field(header, "descr") is not ("|O" or "|O8"))
            throw new InvalidDataException("ReadStrings wants an object array");

        var shape = Shape(header);

        if (shape.Length != 1)
            throw new InvalidDataException($"the metadata is 1-D, got {shape.Length}-D");

        // NEWFALSE, EMPTY_LIST, MEMOIZE, MARK — the run numpy writes before an
        // object array's elements.
        var start = IndexOf(npy, [0x89, 0x5d, 0x94, 0x28]);

        if (start < 0)
            throw new InvalidDataException("no element list found in the metadata pickle");

        var at = start + 4;
        var found = new List<string>();

        while (at < npy.Length && npy[at] == 0x8c)
        {
            var length = npy[at + 1];
            found.Add(Encoding.UTF8.GetString(npy, at + 2, length));
            at += 2 + length;

            // MEMOIZE follows every element numpy writes here.
            if (at < npy.Length && npy[at] == 0x94)
                at++;
        }

        if (at >= npy.Length || npy[at] != 0x65)
            throw new InvalidDataException("the metadata's element list did not end at APPENDS");

        if (found.Count != shape[0])
        {
            throw new InvalidDataException(
                $"the metadata header says {shape[0]} elements and the pickle held {found.Count}");
        }

        return [.. found];
    }

    private static (string Header, int Data) ReadHeader(byte[] npy)
    {
        if (npy.Length < 10 || !npy.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException("not a .npy file");

        var major = npy[6];

        int headerLength;
        int at;

        if (major == 1)
        {
            headerLength = BitConverter.ToUInt16(npy, 8);
            at = 10;
        }
        else
        {
            headerLength = (int)BitConverter.ToUInt32(npy, 8);
            at = 12;
        }

        return (Encoding.ASCII.GetString(npy, at, headerLength), at + headerLength);
    }

    private static string Field(string header, string key)
    {
        var marker = $"'{key}':";
        var at = header.IndexOf(marker, StringComparison.Ordinal);

        if (at < 0)
            throw new InvalidDataException($"the .npy header has no '{key}'");

        var rest = header[(at + marker.Length)..].TrimStart();

        if (rest.StartsWith('\''))
        {
            var end = rest.IndexOf('\'', 1);
            return rest[1..end];
        }

        var stop = rest.IndexOfAny([',', '}']);

        return rest[..stop].Trim();
    }

    private static int[] Shape(string header)
    {
        var at = header.IndexOf("'shape':", StringComparison.Ordinal);
        var open = header.IndexOf('(', at);
        var close = header.IndexOf(')', open);
        var inside = header[(open + 1)..close].Trim();

        if (inside.Length == 0)
            return [];

        var parts = inside.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var shape = new int[parts.Length];

        for (var i = 0; i < parts.Length; i++)
            shape[i] = int.Parse(parts[i], CultureInfo.InvariantCulture);

        return shape;
    }

    private static int IndexOf(byte[] haystack, ReadOnlySpan<byte> needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
                return i;
        }

        return -1;
    }
}
