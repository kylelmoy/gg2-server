namespace GG2.Bots;

/// <summary>
/// One 64-bit number for a whole batch of matches, so two runs of the same
/// batch can be compared by eye and across machines.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="MatchResult.Digest"/> already covers where everybody stood at the
/// end of one match, bit for bit. What this adds is a way to carry a whole
/// <c>eval</c> — every map, every match — as a single token that can be read
/// out of two terminals side by side.
/// </para>
/// <para>
/// <b>Everything that identifies the batch goes in, not just the digests.</b> A
/// comparison is only worth anything if two runs that played different matches
/// cannot collide: the map name, the order the maps ran in and each match's
/// scoreboard are all mixed, so a run that skipped a map or played them in
/// another order reads as different rather than equal.
/// </para>
/// <para>
/// FNV-1a with the same constants and the same byte order as
/// <c>HeadlessMatch.Digest</c>, for the same reason it gives: stability across
/// runs and machines matters here, cryptographic strength does not.
/// </para>
/// </remarks>
public static class BatchDigest
{
    private const ulong Offset = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    /// <summary>The empty digest, which every fold starts from.</summary>
    public static ulong Seed => Offset;

    /// <summary>Mixes one 64-bit value in, low byte first.</summary>
    public static ulong Mix(ulong hash, ulong value)
    {
        for (var i = 0; i < 8; i++)
        {
            hash ^= (byte)(value >> (i * 8));
            hash *= Prime;
        }

        return hash;
    }

    /// <summary>Mixes text in — a map name, so two maps cannot swap silently.</summary>
    public static ulong Mix(ulong hash, ReadOnlySpan<char> text)
    {
        foreach (var c in text)
            hash = Mix(hash, c);

        return hash;
    }

    /// <summary>Mixes one match's whole result in.</summary>
    public static ulong Mix(ulong hash, in MatchResult result)
    {
        hash = Mix(hash, (ulong)(long)result.Outcome);
        hash = Mix(hash, (ulong)(long)result.Winner);
        hash = Mix(hash, (ulong)(long)result.RedCaps);
        hash = Mix(hash, (ulong)(long)result.BlueCaps);
        hash = Mix(hash, (ulong)(long)result.Ticks);
        return Mix(hash, result.Digest);
    }

    /// <summary>
    /// Digests a batch of matches played on one map, in the order they were
    /// indexed.
    /// </summary>
    public static ulong Of(ReadOnlySpan<char> map, ReadOnlySpan<MatchResult> results)
    {
        var hash = Mix(Seed, map);

        foreach (ref readonly var result in results)
            hash = Mix(hash, in result);

        return hash;
    }
}
