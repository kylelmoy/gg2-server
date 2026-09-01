namespace Gg2.Server;

/// <summary>
/// Where the built-in map PNGs are, without needing a reference checkout.
/// </summary>
/// <remarks>
/// ⚠️ <b>The maps are checked in at <c>maps/</c> as of 2026-09-01, and this is
/// what made the server standalone.</b> Until then every entry point defaulted to
/// <c>D:\code\Gang-Garrison-2\Source\gg2\Included Files</c> — a hard-coded
/// absolute path to one machine's checkout of a fork — and
/// <see cref="ServerOptions.MapDirectory"/> threw if it was not set. A clone of
/// this repo could not serve a single round.
/// <para>
/// ⚠️ <b>They are 23 PNGs and 458 KB</b> — the reference's 22, plus the
/// port-authored <c>gg_debug2</c> (docs/DEBUG-MAPS.md), which no census of the
/// shipped maps should count. The old comment said "there is nowhere
/// else to get them: they ship inside the Game Maker project", which was true
/// about their origin and wrong about the consequence — nothing stopped them
/// being copied, and the cost of not copying them was a hard dependency on
/// another repository for the product's core function.
/// </para>
/// <para>
/// ⚠️ <b>The reference's 22 are kept byte-identical to it, and <c>maps.json</c>
/// is what says so.</b> <c>LevelEntityTests.MatchesTheEntityOracle</c> reads every map
/// through this resolver and compares each entity against the checked-in oracle,
/// so a map that drifted from the reference's fails the suite. Copying them here
/// did not weaken that gate; it pointed it at the copy.
/// </para>
/// <para>
/// ⚠️ <b><c>Gg2.Parity</c> has a parallel copy of this probe order</b> and does
/// not reference <c>Gg2.Server</c>. Change one, change the other. It is not
/// shared through <c>Gg2.Maps</c> on purpose: <c>Gg2.Simulation</c> references
/// that assembly, and CLAUDE.md's no-I/O rule is enforced by keeping file access
/// out of everything the simulation can reach.
/// </para>
/// </remarks>
public static class MapDirectory
{
    /// <summary>The map every probe uses to recognise a real map directory.</summary>
    /// <remarks>
    /// ⚠️ <b>Probing for the directory alone is not enough.</b> An empty
    /// <c>maps/</c> left behind by a partial checkout would satisfy
    /// <c>Directory.Exists</c> and then fail at the first join, which is a much
    /// worse error than not finding it at all.
    /// </remarks>
    private const string Sentinel = "ctf_truefort.png";

    /// <summary>
    /// The first of the candidates below that actually holds maps, or empty.
    /// </summary>
    /// <remarks>
    /// In order: <c>GG2_MAPS</c>, the directory beside the executable, a
    /// <c>maps/</c> found by walking up from it (which is how a
    /// <c>dotnet run</c> out of <c>bin/</c> finds the repo root), and finally
    /// <c>GG2_REFERENCE</c>'s <c>Included Files</c> for anyone deliberately
    /// serving a reference checkout.
    /// </remarks>
    public static string Resolve()
    {
        if (Environment.GetEnvironmentVariable("GG2_MAPS") is { Length: > 0 } explicitPath
            && Holds(explicitPath))
        {
            return explicitPath;
        }

        var beside = Path.Combine(AppContext.BaseDirectory, "maps");
        if (Holds(beside))
            return beside;

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "maps");
            if (Holds(candidate))
                return candidate;
        }

        if (Environment.GetEnvironmentVariable("GG2_REFERENCE") is { Length: > 0 } reference)
        {
            var included = Path.Combine(reference, "Source", "gg2", "Included Files");
            if (Holds(included))
                return included;
        }

        return string.Empty;
    }


    /// <summary>
    /// Where the bot nav graphs are: <c>GG2_NAVCACHE</c>, then <c>nav/</c> beside
    /// the executable or above it. Empty when there are none.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Deliberately NOT copied into build output, unlike the maps.</b> They
    /// are 34 MB, and copying them would put that through every build twice over
    /// for Debug and Release — the reason <c>ReferenceTree.NavCacheDirectory</c>
    /// gives for walking to the repository root instead, and it applies here for
    /// the same reason. A <em>published</em> server that wants bots to navigate
    /// carries a nav directory of its own and names it with <c>--botnav</c>.
    /// <para>
    /// ⚠️ <b>Empty is not an error.</b> <c>NavCache.TryLoad</c> degrades to bots
    /// that fight whatever reaches them and walk nowhere, which is what a server
    /// with an ungenerated map in its rotation should do. Returning empty here
    /// preserves that; it must not start throwing.
    /// </para>
    /// <para>
    /// <c>GG2_NAVCACHE</c> is the variable <c>gg2-nav-gen</c> writes to and
    /// <c>ReferenceTree.NavCacheDirectory</c> reads, so one setting moves the
    /// whole seam.
    /// </para>
    /// </remarks>
    public static string ResolveNav()
    {
        if (Environment.GetEnvironmentVariable("GG2_NAVCACHE") is { Length: > 0 } fromEnv
            && Directory.Exists(fromEnv))
        {
            return fromEnv;
        }

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "nav");
            if (Directory.Exists(candidate) && Directory.EnumerateFiles(candidate, "*.txt").Any())
                return candidate;
        }

        return string.Empty;
    }

    private static bool Holds(string directory) =>
        File.Exists(Path.Combine(directory, Sentinel));
}
