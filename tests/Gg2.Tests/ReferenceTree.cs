namespace Gg2.Tests;

/// <summary>
/// Locates the reference checkout — the Game Maker 8 implementation that is also
/// the client, and therefore the specification.
/// </summary>
/// <remarks>
/// The map tests read the maps out of it rather than copying them here. That is
/// deliberate: a copied map is a map that can silently go stale, and phase 0's
/// exit criterion is about the maps the stock client actually ships.
/// <para>
/// Set <c>GG2_REFERENCE</c> to override the path. The tree is <b>read-only</b>
/// from this repo (CLAUDE.md).
/// </para>
/// <para>
/// ⚠️ <b>The default is stock upstream GG2, not the bot fork.</b> It used to be
/// <c>D:\code\Gang-Garrison-2</c>, a fork carrying 42 commits of its own bot and
/// nav work — 69 extra scripts and 198 extra constants. Tests grew to assert
/// against those, so "the specification" quietly meant one person's branch. This
/// port's bot layer has since diverged from that branch anyway, which leaves no
/// reason to check anything against it. Stock GG2 is the thing a stock client
/// runs, and that is the only thing this port owes compatibility to.
/// </para>
/// </remarks>
public static class ReferenceTree
{
    public const string EnvironmentVariable = "GG2_REFERENCE";

    public const string NavCacheEnvironmentVariable = "GG2_NAVCACHE";

    private const string DefaultPath = @"D:\code\Gang-Garrison-2-upstream";

    /// <summary>Root of the checkout, whether or not it exists.</summary>
    public static string Root =>
        Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } fromEnv
            ? fromEnv
            : DefaultPath;

    /// <summary>Where the built-in map PNGs live.</summary>
    public static string IncludedFiles => Path.Combine(Root, "Source", "gg2", "Included Files");

    /// <summary>
    /// Where the bot nav caches live: <c>GG2_NAVCACHE</c>, or <c>nav/</c> at the
    /// root of this repo.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Deliberately NOT under <see cref="Root"/>.</b> These used to be read
    /// from the reference's <c>Source/build/botnav</c> — the GM8 build directory
    /// of whichever checkout <c>GG2_REFERENCE</c> pointed at — which quietly made
    /// 190-odd bot tests depend on a fork that had run the in-game generator.
    /// A nav graph is <em>this project's</em> artefact and the reference knows
    /// nothing about it, so it does not belong in a tree CLAUDE.md calls
    /// read-only.
    /// <para>
    /// They are gitignored (34 MB of hex for 24 map areas) and rebuilt by
    /// <c>gg2-nav-gen</c>, so a fresh clone has none. A test that needs them must
    /// say so with <see cref="RequireNavCache"/> rather than failing as if
    /// something were broken.
    /// </para>
    /// </remarks>
    public static string NavCacheDirectory =>
        Environment.GetEnvironmentVariable(NavCacheEnvironmentVariable) is { Length: > 0 } fromEnv
            ? fromEnv
            : Path.Combine(RepositoryRoot, "nav");

    public static bool IsAvailable => Directory.Exists(IncludedFiles);

    public static bool HasNavCache =>
        Directory.Exists(NavCacheDirectory) && Directory.EnumerateFiles(NavCacheDirectory, "*.txt").Any();

    public static void RequireNavCache() =>
        Assert.True(
            HasNavCache,
            $"""
             No bot nav caches at {NavCacheDirectory}.

             These are written by gg2-nav-gen and gitignored here, so a fresh clone
             has none. Run the generator over the built-in maps, then either copy
             its output to that directory or point {NavCacheEnvironmentVariable} at it.
             """);

    /// <summary>
    /// This repo's root, found by walking up from the test binary until
    /// <c>Gg2.sln</c> turns up.
    /// </summary>
    /// <remarks>
    /// The alternative — copying the caches into the build output the way
    /// <c>Fixtures/</c> is copied — would put 34 MB through every build, twice
    /// over for Debug and Release.
    /// </remarks>
    private static string RepositoryRoot
    {
        get
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
                 directory is not null;
                 directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Gg2.sln")))
                    return directory.FullName;
            }

            // Only reachable if the tests are run from outside the tree; the
            // env var is then the way in, and RequireNavCache says so.
            return AppContext.BaseDirectory;
        }
    }

    public static string MapPath(string map) => Path.Combine(IncludedFiles, map + ".png");

    /// <summary>
    /// The bytes of a built-in map's PNG, with a failure message that says what
    /// to do rather than just that a file is missing.
    /// </summary>
    public static byte[] ReadMap(string map)
    {
        RequireAvailable();

        var path = MapPath(map);
        Assert.True(File.Exists(path), $"the reference tree has no map at {path}");
        return File.ReadAllBytes(path);
    }

    public static void RequireAvailable() =>
        Assert.True(
            IsAvailable,
            $"""
             No reference checkout at {IncludedFiles}.

             These tests read the built-in maps out of the Gang Garrison 2 tree, which
             is the specification this port is checked against. Point {EnvironmentVariable}
             at a checkout to run them.
             """);
}
