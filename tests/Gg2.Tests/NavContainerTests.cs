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
/// <b>Written because the rest of the suite cannot see either.</b> Every
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
    /// With both present the PLAIN one wins — a regenerated graph takes
    /// effect.
    /// </summary>
    /// <remarks>
    /// "Compressed wins, because it is what shipped" is wrong: <c>gg2-nav-gen</c>
    /// writes <c>&lt;key&gt;.txt</c> into <c>../gg2-server/nav</c> by default, which
    /// is the directory holding the shipped <c>.txt.gz</c>. So preferring the
    /// compressed file would mean a regeneration is silently ignored — and nothing
    /// would catch it, because <c>NavManifestTests</c> hashes the MAPS, which a
    /// regeneration does not touch.
    /// <para>
    /// The graphs here are deliberately different sizes so the assertion can
    /// tell which file was actually read, rather than only that something
    /// loaded.
    /// </para>
    /// </remarks>
    [Fact]
    public void PlainWinsWhenBothArePresent()
    {
        ReferenceTree.RequireNavCache();

        // The shipped container holds ctf_truefort (687 nodes); the "freshly
        // generated" plain file holds gg_debug (52).
        WriteCompressed("ctf_truefort_a1.txt.gz", RealGraphText());
        File.WriteAllText(
            Path.Combine(_directory, "ctf_truefort_a1.txt"),
            NavCache.ReadFile(NavCache.FindFile(ReferenceTree.NavCacheDirectory, "gg_debug", 1)));

        Assert.True(NavCache.TryLoad(_directory, "ctf_truefort", 1, out var graph));
        Assert.Equal(52, graph!.Nodes.Length);

        Assert.True(NavCache.HasBothContainers(_directory, "ctf_truefort", 1));

        // And with only the shipped one there, it is used and nothing is
        // reported as ambiguous — otherwise the warning would fire on every
        // normal server.
        File.Delete(Path.Combine(_directory, "ctf_truefort_a1.txt"));

        Assert.False(NavCache.HasBothContainers(_directory, "ctf_truefort", 1));
        Assert.True(NavCache.TryLoad(_directory, "ctf_truefort", 1, out var shipped));
        Assert.Equal(687, shipped!.Nodes.Length);
    }

    /// <summary>
    /// The regeneration cycle end to end: generate, compress, still the same
    /// graph.
    /// </summary>
    /// <remarks>
    /// <b>This is the workflow, and every step of it is a place to lose
    /// data.</b> <c>gg2-nav-gen</c> writes <c>.txt</c>;
    /// <c>tools/compress-nav.sh</c> gzips and deletes the original; the loader
    /// has to read the result as the same graph. Asserting the round trip here
    /// means the script's contract is pinned by something other than the
    /// script's own comments — it is not run by the suite, but what it is
    /// required to produce is.
    /// </remarks>
    [Fact]
    public void TheRegenerationCycleRoundTrips()
    {
        ReferenceTree.RequireNavCache();

        var generated = Path.Combine(_directory, "ctf_truefort_a1.txt");
        File.WriteAllText(generated, RealGraphText());

        Assert.True(NavCache.TryLoad(_directory, "ctf_truefort", 1, out var fromPlain));

        // What compress-nav.sh does: gzip -9 -n, verify, delete the original.
        WriteCompressed("ctf_truefort_a1.txt.gz", File.ReadAllText(generated));
        File.Delete(generated);

        Assert.True(NavCache.TryLoad(_directory, "ctf_truefort", 1, out var fromCompressed));

        Assert.Equal(fromPlain!.Nodes.Length, fromCompressed!.Nodes.Length);
        Assert.Equal(fromPlain.Edges.Length, fromCompressed.Edges.Length);

        // Node-for-node, not just the counts. A container change that lost
        // the fractional NAV_EDGE_BUCKET would keep every count identical and
        // cost five of fourteen scenarios — NavCache.Parse's remarks on the cast
        // that already did exactly that once.
        for (var i = 0; i < fromPlain.Edges.Length; i++)
            Assert.Equal(fromPlain.Edges[i].Bucket, fromCompressed.Edges[i].Bucket);
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
    /// The load-bearing one: a graph whose manifest line disagrees is refused.
    /// </summary>
    /// <remarks>
    /// <b>Both arms, and the negative arm is the real assertion.</b> Checking
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

        // And with no hash offered it still loads: the check is something a
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
    /// An empty <c>nav/</c> must not satisfy <c>ResolveNav</c>'s probe.
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
    /// <b>A guard against the repository silently reverting.</b> Everything
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
            $"""
             Uncompressed nav graphs in {directory}: {string.Join(", ", plain.Select(Path.GetFileName))}

             This is what a gg2-nav-gen run leaves behind — it writes .txt into this
             directory by default. The graphs are live (a .txt beats a .txt.gz), so the
             server is using them; they are just 32x larger than the shipped form. Finish
             the cycle:
                 tools/compress-nav.sh
                 GG2_UPDATE_NAV_MANIFEST=1 dotnet test Gg2.sln --filter NavManifestTests
             docs/DEPLOY.md, "the nav graphs".
             """);

        Assert.NotEmpty(Directory.EnumerateFiles(directory, "*.txt.gz"));
    }

    /// <summary>
    /// <c>MapDirectory.ResolveNav</c> finds a directory holding only <c>.gz</c>.
    /// </summary>
    /// <remarks>
    /// <b>The probe must not glob only <c>*.txt</c></b> — gzipping the graphs
    /// would then make it resolve nothing, and every deployment would run bots
    /// that never walk.
    /// </remarks>
    [Fact]
    public void ResolveNavFindsACompressedDirectory()
    {
        ReferenceTree.RequireNavCache();

        WriteCompressed("ctf_truefort_a1.txt.gz", RealGraphText());

        // ⛔ The overload, NOT the process variable. This used to set
        // GG2_NAVCACHE around the assertion and restore it after — and
        // GG2_NAVCACHE is process-wide while xunit runs collections in parallel,
        // so for the width of that try block every other test resolved nav to this
        // temp directory. Observed as SpotBanTests failing with "no nav graph for
        // koth_valley area 1 in …\Temp\gg2-nav-<guid>", which is a directory it
        // has never heard of and could not be debugged from.
        // ⚠️ What this test is FOR is the globbing — that a directory holding only
        // .gz resolves — and that is entirely in the overload. The one line that
        // reads the variable is not covered and does not need to be.
        Assert.Equal(_directory, MapDirectory.ResolveNav(_directory));
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
