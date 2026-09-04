using System.Text.RegularExpressions;

namespace Gg2.Tests;

/// <summary>
/// Every <c>BENCH_*</c> the bench reads is named in its README, and vice versa.
/// </summary>
/// <remarks>
/// A mode can exist and be documented nowhere — not in the current modes
/// table, not in the kept-for-reference list. Grepping <c>Program.cs</c>
/// against <c>README.md</c> is the only thing that catches it: reading the
/// file confirms only whatever it says.
/// <para>
/// <b>This is deliberately a TOKEN-SET check and not a table parser.</b>
/// Matching a markdown table against an if-chain fails on formatting rather
/// than on drift. Comparing two sets of identifiers is blind to layout, and
/// fires only when a mode is genuinely in one place and not the other.
/// </para>
/// <para>
/// <b>The half it does NOT catch, stated so nobody mistakes it for full
/// cover.</b> A mode can be listed in the current modes table <em>and</em> in
/// "kept for reference, not current" — a mode named twice satisfies a subset
/// check perfectly. <b>This gates "undocumented", never "contradictorily
/// documented".</b> The second still needs somebody to read the file.
/// </para>
/// </remarks>
public sealed class BenchModeCoverageTests
{
    private static string BenchDirectory
    {
        get
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
                 directory is not null;
                 directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "tools", "bench");

                if (File.Exists(Path.Combine(candidate, "README.md")))
                    return candidate;
            }

            return string.Empty;
        }
    }

    /// <summary>
    /// <c>BENCH_[A-Z_]+</c> wherever it appears — source or prose.
    /// </summary>
    /// <remarks>
    /// Matching the identifier rather than its context is what makes this
    /// formatting-independent. A mode named in a table cell, a prose sentence, a
    /// code fence or an <c>if</c> all count as named.
    /// </remarks>
    private static HashSet<string> Modes(string text) =>
        Regex.Matches(text, "BENCH_[A-Z_]+")
            .Select(m => m.Value)
            .ToHashSet(StringComparer.Ordinal);

    private static (HashSet<string> Code, HashSet<string> Docs) Read()
    {
        var bench = BenchDirectory;

        Assert.False(bench.Length == 0, "no tools/bench above the test assembly");

        var code = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in Directory.GetFiles(bench, "*.cs"))
            code.UnionWith(Modes(File.ReadAllText(file)));

        // Not empty by construction — a regex that silently matched nothing
        // would make both assertions below pass vacuously, which is the shape
        // this repo has been caught by twice today.
        Assert.NotEmpty(code);

        return (code, Modes(File.ReadAllText(Path.Combine(bench, "README.md"))));
    }

    /// <summary>
    /// A mode the bench reads but the README never names.
    /// </summary>
    /// <remarks>
    /// The direction that actually fired: an operator cannot run what they cannot
    /// find, and an undocumented mode is invisible rather than merely awkward.
    /// </remarks>
    [Fact]
    public void EveryModeTheBenchReadsIsDocumented()
    {
        var (code, docs) = Read();
        var undocumented = code.Except(docs).OrderBy(m => m, StringComparer.Ordinal).ToArray();

        Assert.True(
            undocumented.Length == 0,
            "tools/bench reads these and README.md names none of them: "
                + string.Join(", ", undocumented)
                + " — add them to the modes table, or to the kept-for-reference list if they "
                + "answered one question and are not maintained.");
    }

    /// <summary>
    /// A mode the README names that the bench no longer has.
    /// </summary>
    /// <remarks>
    /// The stale-capability direction, and the one this repo keeps paying for:
    /// documentation promising something that does not exist is worse than
    /// documentation missing something that does, because acting on it is the only
    /// way to find out.
    /// </remarks>
    [Fact]
    public void TheReadmeNamesNoModeTheBenchHasLost()
    {
        var (code, docs) = Read();
        var phantom = docs.Except(code).OrderBy(m => m, StringComparer.Ordinal).ToArray();

        Assert.True(
            phantom.Length == 0,
            "README.md names these and no file in tools/bench reads them: "
                + string.Join(", ", phantom)
                + " — either the mode was removed and its documentation was not, or the "
                + "name is misspelt in one of the two places.");
    }
}
