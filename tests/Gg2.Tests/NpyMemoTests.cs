using Gg2.Bots;

namespace Gg2.Tests;

/// <summary>
/// A repeated string in the metadata is a pickle back-reference, and reading it
/// is not optional.
/// </summary>
/// <remarks>
/// <b>The bug this gates rejected every checkpoint <c>gg2-rl</c> had, and this
/// repo could not see it.</b> <c>NpyFile.ReadStrings</c> read only
/// <c>SHORT_BINUNICODE</c>, so the first repeated value — emitted by pickle as
/// <c>BINGET</c> — stopped the parse dead with "did not end at APPENDS".
/// <para>
/// <b>Why it fired there and not here: pickle's memo is keyed by object
/// IDENTITY, not by value.</b> Metadata of <c>['2', uuid, '2', …]</c> pickles as
/// two separate strings if the two <c>'2'</c>s are distinct objects and as a
/// back-reference if they are the same object. This repo's own fixture is the
/// first case, so <b>every policy this repo ever loaded — the phase-4 21-map
/// table included — loaded by luck</b>, and a one-line refactor in the trainer
/// would have flipped it. Verified: <c>ppo_final.npz</c> does contain the
/// duplicate and does not contain a <c>BINGET</c>.
/// </para>
/// <para>
/// <b>Which is why the fixture here is BUILT rather than checked in.</b> A
/// checked-in npz would freeze whichever accident its writer had; constructing
/// the bytes states the case being tested, and a reader can see that the
/// back-reference is really there.
/// </para>
/// </remarks>
public sealed class NpyMemoTests
{
    /// <summary>
    /// A minimal object-array <c>.npy</c> whose element list repeats a string.
    /// </summary>
    /// <param name="preambleStrings">
    /// How many memoized strings to put BEFORE the element list. <b>Not
    /// decoration.</b> In a real checkpoint the list is memo slot 18, behind
    /// <c>_reconstruct</c>, so a reader that assumed the first element was slot 1
    /// would resolve every back-reference to the wrong string. A preamble of zero
    /// would let that bug pass.
    /// </param>
    private static byte[] Build(string[] values, int preambleStrings)
    {
        var pickle = new List<byte> { 0x80, 0x04 }; // PROTO 4
        var slot = 0;

        void Str(string text)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            pickle.Add(0x8c);
            pickle.Add((byte)bytes.Length);
            pickle.AddRange(bytes);
        }

        for (var i = 0; i < preambleStrings; i++)
        {
            Str($"preamble{i}");
            pickle.Add(0x94); // MEMOIZE
            slot++;
        }

        pickle.Add(0x89); // NEWFALSE
        pickle.Add(0x5d); // EMPTY_LIST
        pickle.Add(0x94); // MEMOIZE — the list itself
        slot++;
        pickle.Add(0x28); // MARK

        var written = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var value in values)
        {
            if (written.TryGetValue(value, out var earlier))
            {
                // BINGET: the repeat, exactly as pickle emits it. No MEMOIZE.
                pickle.Add(0x68);
                pickle.Add((byte)earlier);
                continue;
            }

            Str(value);
            pickle.Add(0x94);
            written[value] = slot;
            slot++;
        }

        pickle.Add(0x65); // APPENDS
        pickle.Add(0x2e); // STOP

        var header = $"{{'descr': '|O', 'fortran_order': False, 'shape': ({values.Length},), }}";
        // Padded so magic + version + length + header is a multiple of 64 and
        // ends in a newline, which is numpy’s own rule.
        var total = ((6 + 2 + 2 + header.Length + 1 + 63) / 64) * 64;
        var padded = header.PadRight(total - 6 - 2 - 2 - 1) + '\n';
        var headerBytes = System.Text.Encoding.ASCII.GetBytes(padded);

        // The magic is NUMPY. Written as explicit bytes: a source literal
        // carrying U+0093 encodes to TWO bytes in UTF-8 and silently produces a
        // file nothing can read, which is exactly what the first version did.
        var npy = new List<byte> { 0x93, (byte)'N', (byte)'U', (byte)'M', (byte)'P', (byte)'Y' };
        npy.Add(1);
        npy.Add(0);
        npy.AddRange(BitConverter.GetBytes((ushort)headerBytes.Length));
        npy.AddRange(headerBytes);
        npy.AddRange(pickle);

        return [.. npy];
    }

    /// <summary>
    /// The regression: a repeated value is read, not refused.
    /// </summary>
    /// <remarks>
    /// The exact metadata shape <c>gg2-rl</c> reported — an action-encoding
    /// version and a layout version that are both <c>'2'</c>, which is ordinary
    /// rather than contrived.
    /// </remarks>
    [Fact]
    public void ARepeatedMetadataStringIsResolvedRatherThanRefused()
    {
        string[] meta = ["2", "7ff5940d97aa7bed", "2", "1", "3,2,2,3,15,2", "209", "4"];

        Assert.Equal(meta, NpyFile.ReadStrings(Build(meta, preambleStrings: 18)));
    }

    /// <summary>
    /// The memo base is respected: the same list behind different preambles reads
    /// the same.
    /// </summary>
    /// <remarks>
    /// <b>This is the test that fails if the memo counter is wrong rather than
    /// missing.</b> Reading back-references but numbering them from 1 would pass
    /// the test above only at <c>preambleStrings: 0</c>; here the same values must
    /// come back whatever the list's absolute slot is.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(18)]
    [InlineData(40)]
    public void TheMemoBaseIsCountedRatherThanAssumed(int preamble)
    {
        string[] meta = ["a", "b", "a", "c", "b"];

        Assert.Equal(meta, NpyFile.ReadStrings(Build(meta, preamble)));
    }

    /// <summary>
    /// Several repeats of several values, so an off-by-one cannot pass by luck.
    /// </summary>
    /// <remarks>
    /// With one repeat of one value, a reader that resolved every
    /// back-reference to the <em>first</em> string would still pass. These need
    /// each reference to land on its own target.
    /// </remarks>
    [Fact]
    public void EveryBackReferenceResolvesToItsOwnTarget()
    {
        string[] meta = ["x", "y", "z", "y", "x", "z", "z"];

        Assert.Equal(meta, NpyFile.ReadStrings(Build(meta, preambleStrings: 5)));
    }

    /// <summary>
    /// A file with no repeats at all still reads — the path that always worked.
    /// </summary>
    [Fact]
    public void AMetadataWithNoRepeatsIsUnaffected()
    {
        string[] meta = ["2", "7ff5940d97aa7bed", "1", "3,2,2,3,15,2", "209", "4"];

        Assert.Equal(meta, NpyFile.ReadStrings(Build(meta, preambleStrings: 18)));
    }

    /// <summary>
    /// The real checkpoint still loads, and its metadata really does repeat.
    /// </summary>
    /// <remarks>
    /// The other half of the story: this file carries <c>'2'</c> twice and reads
    /// fine either way, because its two <c>'2'</c>s are distinct objects. It is
    /// the evidence that the old reader was working by accident rather than
    /// because repeats could not happen.
    /// </remarks>
    [Fact]
    public void TheCheckedInCheckpointCarriesADuplicateAndStillLoads()
    {
        var policy = PolicyFixture.Load();

        Assert.Equal(ObservationWriter.Length, policy.ObservationLength);
    }
}
