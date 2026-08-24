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
/// </remarks>
public static class ReferenceTree
{
    public const string EnvironmentVariable = "GG2_REFERENCE";

    private const string DefaultPath = @"D:\code\Gang-Garrison-2";

    /// <summary>Root of the checkout, whether or not it exists.</summary>
    public static string Root =>
        Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } fromEnv
            ? fromEnv
            : DefaultPath;

    /// <summary>Where the built-in map PNGs live.</summary>
    public static string IncludedFiles => Path.Combine(Root, "Source", "gg2", "Included Files");

    public static bool IsAvailable => Directory.Exists(IncludedFiles);

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
