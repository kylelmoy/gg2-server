using System.IO.Compression;
using Gg2.Bots;
using Gg2.Maps;
using Gg2.Server;

namespace Gg2.Tests;

/// <summary>
/// The two containers a nav graph can arrive in, and the manifest check that
/// refuses one built against a different map.
/// </summary>
/// <remarks>
/// ⚠️ <b>Written because the rest of the suite cannot see either.</b> Every
/// other nav test reads <c>nav/</c>, which now holds exactly one container and
/// a manifest that matches — so "both containers work", "the compressed one
/// wins" and "a mismatch is refused" were all true by construction and asserted
/// nowhere. They are the claims somebody else builds against: an operator
/// dropping a regenerated <c>.txt</c> beside a shipped <c>.txt.gz</c>, and a
/// server told to run graphs that no longer fit its maps.
/// </remarks>
public sealed class NavContainerTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "gg2-nav-" + Guid.NewGuid().ToString("N"));

    public NavContainerTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory that outlives the run is not a test failure.
        }
    }

    /// <summary>A plain <c>.txt</c> still loads — <c>gg2-nav-gen</c> writes those.</summary>
    [Fact]
    public void APlainFileLoads()
    {
        ReferenceTree.RequireNavCache();

        File.WriteAllText(Path.Combine(_directory, "ctf_truefort_a1.txt"), RealGraphText());

        Assert.True(NavCache.TryLoad(_directory, "ctf_truefort", 1, out var graph));
        Assert.Equal(687, graph!.Nodes.Length);
    }

    /// <summary>And so does the compressed one, which is what ships.</summary>
    [Fact]
    public void ACompressedFileLoads()
    {
        ReferenceTree.RequireNavCache();

        WriteCompressed("ctf_truefort_a1.txt.gz", RealGraphText());

        Assert.True(NavCache.TryLoad(_directory, "ctf_truefort", 1, out var graph));
        Assert.Equal(687, graph!.Nodes.Length);
    }

    /// <summary>
    /// ⚠️ With both present the compressed one wins, and it is not a coin toss.
    /// </summary>
    /// <remarks>
    /// The plain file here is a VALID graph for a different map, so a reader
    /// that picked it would load happily and be wrong — the failure this
    /// ordering exists to avoid, rather than a crash that would announce itself.
    /// </remarks>
    [Fact]
    public void CompressedWinsWhenBothArePresent()
    {
        ReferenceTree.RequireNavCache();

        WriteCompressed("ctf_truefort_a1.txt.gz", RealGraphText());
        File.WriteAllText(
            Path.Combine(_directory, "ctf_truefort_a1.txt"),
            NavCache.ReadFile(NavCache.FindFile(ReferenceTree.NavCacheDirectory, "gg_debug", 1)));

        Assert.True(NavCache.TryLoad(_directory, "ctf_truefort", 1, out var graph));

        // gg_debug's graph is 52 nodes; ctf_truefort's is 687.
        Assert.Equal(687, graph!.Nodes.Length);
    }

    /// <summary>One map with two containers is one entry, not two.</summary>
    [Fact]
    public void ListDoesNotDoubleCount()
    {
        ReferenceTree.RequireNavCache();

        var text = RealGraphText();
        WriteCompressed("ctf_truefort_a1.txt.gz", text);
        File.WriteAllText(Path.Combine(_directory, "ctf_truefort_a1.txt"), text);
        WriteCompressed("gg_debug_a1.txt.gz", text);

        Assert.Equal([("ctf_truefort", 1), ("gg_debug", 1)], NavCache.List(_directory));
    }

    /// <summary>
    /// ⛔ The load-bearing one: a graph whose manifest line disagrees is refused.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Both arms, and the negative arm is the real assertion.</b> Checking
    /// only that a matching hash loads would pass against a
    /// <c>TryLoad</c> that ignored the parameter entirely — which is exactly
    /// what the first draft of this feature would have shipped, since the file
    /// on disk is perfectly valid and parses either way.
    /// </remarks>
    [Fact]
    public void AGraphThatDisagreesWithItsManifestIsRefused()
    {
        ReferenceTree.RequireNavCache();

        WriteCompressed("ctf_truefort_a1.txt.gz", RealGraphText());

        var map = LevelMap.FromPng("ctf_truefort", ReferenceTree.ReadMap("ctf_truefort")).ForArea(1);
        var real = NavManifest.HashOf(map);

        NavManifest.Write(
            Path.Combine(_directory, NavManifest.FileName),
            [("ctf_truefort_a1", real)]);

        Assert.True(
            NavCache.TryLoad(_directory, "ctf_truefort", 1, out var loaded, real),
            "a graph matching its manifest was refused");
        Assert.NotNull(loaded);

        var somethingElse = NavManifest.HashOf(
            LevelMap.FromPng("gg_debug", ReferenceTree.ReadMap("gg_debug")).ForArea(1));

        Assert.False(
            NavCache.TryLoad(_directory, "ctf_truefort", 1, out var refused, somethingElse),
            "a graph built against different geometry was loaded anyway");
        Assert.Null(refused);

        // ⚠️ And with no hash offered it still loads: the check is something a
        // caller opts into, so a fixture or a tool is not forced to own a map.
        Assert.True(NavCache.TryLoad(_directory, "ctf_truefort", 1, out _));
    }

    /// <summary>
    /// A graph the manifest does not name loads, however the hash comes out.
    /// </summary>
    [Fact]
    public void AnUnnamedGraphIsStillLoaded()
    {
        ReferenceTree.RequireNavCache();

        WriteCompressed("ctf_truefort_a1.txt.gz", RealGraphText());
        NavManifest.Write(
            Path.Combine(_directory, NavManifest.FileName),
            [("some_other_map_a1", new string('0', 32))]);

        Assert.True(NavCache.TryLoad(_directory, "ctf_truefort", 1, out _, new string('a', 32)));
    }

    /// <summary>A <c>.txt.gz</c> that is not gzip is a quiet miss, not a throw.</summary>
    /// <remarks>
    /// <c>NavCache.TryLoad</c>'s contract is that a file it cannot make sense of
    /// answers false — <c>GZipStream</c> throws <c>InvalidDataException</c>
    /// rather than the <c>FormatException</c> the parser uses, which is a
    /// separate catch and therefore a separate way to get this wrong.
    /// </remarks>
    [Fact]
    public void ACorruptCompressedFileIsAQuietMiss()
    {
        File.WriteAllText(Path.Combine(_directory, "ctf_truefort_a1.txt.gz"), "not gzip at all");

        Assert.False(NavCache.TryLoad(_directory, "ctf_truefort", 1, out var graph));
        Assert.Null(graph);
    }

    /// <summary>
    /// ⚠️ An empty <c>nav/</c> must not satisfy <c>ResolveNav</c>'s probe.
    /// </summary>
    /// <remarks>
    /// Otherwise the directory resolves and then every map misses one at a time,
    /// which reads as twenty-five separate faults instead of one missing
    /// directory — the reason <c>MapDirectory.Sentinel</c> exists on the map
    /// side. Asserted through <c>NavCache.List</c>, which is the shared
    /// definition of "a nav file" that the probe now goes through.
    /// </remarks>
    [Fact]
    public void AnEmptyDirectoryHoldsNoGraphs()
    {
        Assert.Empty(NavCache.List(_directory));

        // A manifest on its own is not a graph either.
        NavManifest.Write(Path.Combine(_directory, NavManifest.FileName), []);
        Assert.Empty(NavCache.List(_directory));
    }

    /// <summary>The shipped directory really is the compressed container.</summary>
    /// <remarks>
    /// ⚠️ <b>A guard against the repository silently reverting.</b> Everything
    /// above works with either container, so nothing else in the suite would
    /// notice 35 MB of plain text coming back — and the publish size, which is
    /// the thing that actually mattered, is not otherwise gated.
    /// </remarks>
    [Fact]
    public void TheCheckedInGraphsAreCompressed()
    {
        ReferenceTree.RequireNavCache();

        var directory = ReferenceTree.NavCacheDirectory;
        var plain = Directory.EnumerateFiles(directory, "*.txt").ToArray();

        Assert.True(
            plain.Length == 0,
            $"uncompressed nav graphs are checked in: {string.Join(", ", plain.Select(Path.GetFileName))}. "
            + "They are 32x larger and gzip costs about a millisecond a map change (docs/DEPLOY.md).");

        Assert.NotEmpty(Directory.EnumerateFiles(directory, "*.txt.gz"));
    }

    /// <summary>
    /// <c>MapDirectory.ResolveNav</c> finds a directory holding only <c>.gz</c>.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>The probe used to glob <c>*.txt</c></b>, so gzipping the graphs
    /// would have made it resolve nothing and every deployment would have run
    /// bots that never walk — the exact failure this change was made to end,
    /// reintroduced by the change itself.
    /// </remarks>
    [Fact]
    public void ResolveNavFindsACompressedDirectory()
    {
        ReferenceTree.RequireNavCache();

        WriteCompressed("ctf_truefort_a1.txt.gz", RealGraphText());

        var previous = Environment.GetEnvironmentVariable("GG2_NAVCACHE");
        try
        {
            Environment.SetEnvironmentVariable("GG2_NAVCACHE", _directory);
            Assert.Equal(_directory, MapDirectory.ResolveNav());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GG2_NAVCACHE", previous);
        }
    }

    private static string RealGraphText() =>
        NavCache.ReadFile(NavCache.FindFile(ReferenceTree.NavCacheDirectory, "ctf_truefort", 1));

    private void WriteCompressed(string name, string text)
    {
        using var file = File.Create(Path.Combine(_directory, name));
        using var gzip = new GZipStream(file, CompressionLevel.SmallestSize);
        using var writer = new StreamWriter(gzip);
        writer.Write(text);
    }
}
