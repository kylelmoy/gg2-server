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
    /// and then reads the entries that follow, stopping at <c>APPENDS</c>. Any
    /// other opcode in that run, or a count that disagrees with the header's
    /// shape, is a refusal.
    /// <para>
    /// ⚠️ <b>The count cross-check is the load-bearing part.</b> The header says
    /// how many elements there should be, independently of the pickle, so a scan
    /// that locked on to the wrong place has to produce the right NUMBER of
    /// strings as well as the right ones.
    /// </para>
    /// <para>
    /// ⛔ <b>A REPEATED STRING IS A <c>BINGET</c>, NOT A SECOND
    /// <c>SHORT_BINUNICODE</c>, AND THAT WAS A BUG HERE UNTIL 2026-09-01.</b>
    /// Pickle memoizes, so the second time an object is written it emits a
    /// back-reference. This loop read only <c>SHORT_BINUNICODE</c>, stopped dead
    /// on the <c>BINGET</c> and threw "did not end at APPENDS". Found by
    /// <c>gg2-rl</c>, whose metadata is <c>['2', 'uuid', '2', ...]</c> — an
    /// action-encoding version and a layout version that are both small integers,
    /// which is the ordinary case rather than a freak one.
    /// </para>
    /// <para>
    /// ⚠️ <b>It fired for them and not here because pickle's memo is keyed by
    /// object IDENTITY, not by value.</b> This repo's fixture holds two distinct
    /// <c>'2'</c> objects and pickles them as two strings; theirs holds one object
    /// twice and pickles the second as a back-reference. So whether a checkpoint
    /// loads depended on whether the trainer happened to reuse a variable —
    /// invisible from the file, and flipped by a one-line refactor upstream.
    /// <b>The phase-4 21-map table loaded by luck.</b>
    /// </para>
    /// <para>
    /// ⚠️ <b>Resolving a back-reference needs the memo's absolute numbering</b>,
    /// and the element list is not slot 0 — in a real checkpoint it is 18, behind
    /// the <c>_reconstruct</c> preamble. <see cref="CountMemoSlots"/> walks that
    /// preamble to count them, and <b>refuses on an opcode it does not know</b>
    /// rather than guessing a length and silently miscounting.
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

        // The EMPTY_LIST at start+1 is memoized at start+2, so the first element's
        // own MEMOIZE takes the slot after that one.
        var memo = CountMemoSlots(npy, start + 2) + 1;

        var at = start + 4;
        var found = new List<string>();
        var byIndex = new Dictionary<int, string>();

        while (at < npy.Length)
        {
            string value;

            if (npy[at] == 0x8c)
            {
                // SHORT_BINUNICODE: a fresh string, which numpy then memoizes.
                var length = npy[at + 1];
                value = Encoding.UTF8.GetString(npy, at + 2, length);
                at += 2 + length;

                if (at < npy.Length && npy[at] == 0x94)
                {
                    byIndex[memo] = value;
                    memo++;
                    at++;
                }
            }
            else if (npy[at] == 0x68 || npy[at] == 0x6a)
            {
                // BINGET / LONG_BINGET: a repeat of a string already written.
                // WARNING: a back-reference is NOT re-memoized, so the counter must
                // not advance here or every later slot is off by one.
                var wide = npy[at] == 0x6a;
                var slot = wide ? BitConverter.ToInt32(npy, at + 1) : npy[at + 1];

                if (!byIndex.TryGetValue(slot, out var referenced))
                {
                    throw new InvalidDataException(
                        $"the metadata references memo slot {slot}, which is not a string this "
                        + "element list wrote — either the scan locked on to the wrong list, or "
                        + "the pickle refers back into the reconstruct preamble");
                }

                value = referenced;
                at += wide ? 5 : 2;
            }
            else
            {
                break;
            }

            found.Add(value);
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

    /// <summary>
    /// How many memo slots the pickle has assigned before <paramref name="limit"/>.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Counting <c>0x94</c> bytes would be wrong</b> — that value occurs
    /// inside a FRAME length, inside a string's UTF-8 and inside any binary
    /// payload. This walks opcodes so it counts only real <c>MEMOIZE</c>s, and it
    /// <b>throws on an opcode it does not know</b> rather than assuming a length:
    /// a miscount resolves a back-reference to the WRONG string, which is a wrong
    /// policy loaded rather than an error.
    /// <para>
    /// The table covers what <c>numpy._core.multiarray._reconstruct</c> emits for
    /// an object array, plus the put/get family an older protocol would use. It is
    /// deliberately not the whole of pickle.
    /// </para>
    /// </remarks>
    private static int CountMemoSlots(byte[] npy, int limit)
    {
        var (_, at) = ReadHeader(npy);
        var slots = 0;

        while (at < limit)
        {
            var op = npy[at];

            switch (op)
            {
                case 0x94: slots++; at += 1; break;                             // MEMOIZE
                case 0x71: slots++; at += 2; break;                             // BINPUT
                case 0x72: slots++; at += 5; break;                             // LONG_BINPUT

                case 0x80: at += 2; break;                                      // PROTO
                case 0x95: at += 9; break;                                      // FRAME
                case 0x8c: at += 2 + npy[at + 1]; break;                        // SHORT_BINUNICODE
                case 0x43: at += 2 + npy[at + 1]; break;                        // SHORT_BINBYTES
                case 0x58: at += 5 + BitConverter.ToInt32(npy, at + 1); break;  // BINUNICODE
                case 0x42: at += 5 + BitConverter.ToInt32(npy, at + 1); break;  // BINBYTES
                case 0x4b: at += 2; break;                                      // BININT1
                case 0x4d: at += 3; break;                                      // BININT2
                case 0x4a: at += 5; break;                                      // BININT
                case 0x68: at += 2; break;                                      // BINGET
                case 0x6a: at += 5; break;                                      // LONG_BINGET

                // Zero-operand opcodes.
                case 0x28: // MARK
                case 0x29: // EMPTY_TUPLE
                case 0x2e: // STOP
                case 0x4e: // NONE
                case 0x52: // REDUCE
                case 0x5d: // EMPTY_LIST
                case 0x61: // APPEND
                case 0x62: // BUILD
                case 0x65: // APPENDS
                case 0x73: // SETITEM
                case 0x74: // TUPLE
                case 0x75: // SETITEMS
                case 0x7d: // EMPTY_DICT
                case 0x85: // TUPLE1
                case 0x86: // TUPLE2
                case 0x87: // TUPLE3
                case 0x88: // NEWTRUE
                case 0x89: // NEWFALSE
                case 0x93: // STACK_GLOBAL
                    at += 1;
                    break;

                default:
                    throw new InvalidDataException(
                        $"unknown pickle opcode 0x{op:x2} at {at} while counting memo slots; "
                        + "this reader knows only what numpy writes for an object array");
            }
        }

        return slots;
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
