using Gg2.Bots;
using Gg2.Maps;
using Gg2.Server;

namespace Gg2.Tests;

/// <summary>
/// ⛔ A map with no checked-in nav graph — a custom map — gets one built at the
/// map change, and bots navigate it.
/// </summary>
/// <remarks>
/// ⚠️ <b>This is the claim somebody else builds against, so it is the one that
/// has to be checked rather than reasoned about.</b> The 25 graphs in
/// <c>nav/</c> cover the shipped maps; before this the answer for anything else
/// was bots that fight whatever reaches them and walk nowhere — and CLAUDE.md's
/// own record of this project is that a degradation nothing goes red for is a
/// degradation nobody notices. Everything a graph needs is in the map's own PNG,
/// so it was never missing data that had to be missing.
/// <para>
/// The probe map is <c>gg_debug</c> copied under another name, deliberately. Its
/// real graph is checked in, so this can assert not merely "a graph appeared" but
/// that it is <b>the same graph</b>, node for node — and it builds in about ten
/// milliseconds, which keeps the suite honest about cost.
/// </para>
/// </remarks>
public sealed class NavGenerateOnMapChangeTests : IDisposable
{
    /// <summary>The shipped graph this probe map's generated one must equal.</summary>
    private const string Original = "gg_debug";

    private const string Probe = "custom_probe";

    private readonly string _maps =
        Path.Combine(Path.GetTempPath(), "gg2-navgen-maps-" + Guid.NewGuid().ToString("N"));

    private readonly string _nav =
        Path.Combine(Path.GetTempPath(), "gg2-navgen-nav-" + Guid.NewGuid().ToString("N"));

    public NavGenerateOnMapChangeTests()
    {
        Directory.CreateDirectory(_maps);
        Directory.CreateDirectory(_nav);
    }

    public void Dispose()
    {
        foreach (var directory in new[] { _maps, _nav })
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // A temp directory that outlives the run is not a test failure.
            }
        }
    }

    /// <summary>
    /// A map the nav directory has never heard of gets the graph it should have
    /// had.
    /// </summary>
    [Fact]
    public void BuildsAGraphForAMapThatHasNone()
    {
        ReferenceTree.RequireNavCache();
        StageProbeMap();

        using var harness = Harness();

        var built = harness.Server.Population?.Nav;
        Assert.NotNull(built);

        // ⚠️ Not just "a graph exists". The probe is a copy of gg_debug, whose
        // real graph is checked in, so the generated one has to BE it — an
        // on-the-fly graph that is merely plausible is the failure this whole
        // feature would otherwise hide.
        var shipped = NavCache.Load(ReferenceTree.NavCacheDirectory, Original, 1);

        Assert.Equal(shipped.NodeCount, built!.NodeCount);
        Assert.Equal(shipped.EdgeCount, built.EdgeCount);

        for (var i = 0; i < shipped.EdgeCount; i++)
            Assert.Equal(shipped.Edges[i], built.Edges[i]);
    }

    /// <summary>
    /// ⚠️ And nothing was written: a server does not put files in an operator's
    /// directory because it rotated onto a map.
    /// </summary>
    [Fact]
    public void HoldsTheGraphInMemoryUnlessAskedToSaveIt()
    {
        ReferenceTree.RequireNavCache();
        StageProbeMap();

        using var harness = Harness();

        Assert.NotNull(harness.Server.Population?.Nav);
        Assert.Empty(Directory.GetFiles(_nav));
    }

    /// <summary>
    /// With <c>--navgen-save</c> the graph and its manifest line land on disk, and
    /// the next server reads them.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The manifest line is half the assertion.</b> A graph saved without one
    /// loads, and is then never checked against the map it came from again — which
    /// is the silent failure the manifest exists to convert into a loud one. Saving
    /// the graph and forgetting the line would pass a test that only looked for the
    /// <c>.txt.gz</c>.
    /// </remarks>
    [Fact]
    public void SavesTheGraphAndItsManifestLineWhenAsked()
    {
        ReferenceTree.RequireNavCache();
        StageProbeMap();

        using (var harness = Harness(options => options.NavGenerateWrite = true))
            Assert.NotNull(harness.Server.Population?.Nav);

        var key = NavCache.Key(Probe, 1);

        Assert.True(File.Exists(Path.Combine(_nav, key + NavCache.CompressedExtension)));

        var expected = NavManifest.HashOf(
            LevelMap.FromPng(Probe, File.ReadAllBytes(Path.Combine(_maps, Probe + ".png"))).ForArea(1));

        Assert.Equal(expected, NavManifest.Load(_nav).HashFor(key));

        // And a second server loads it rather than rebuilding: the saved file has
        // to be one this server's own reader accepts, manifest check included.
        using var second = Harness(options => options.NavGenerate = false);

        Assert.NotNull(second.Server.Population?.Nav);
    }

    /// <summary>
    /// ⛔ <c>--no-navgen</c> really does turn it off.
    /// </summary>
    /// <remarks>
    /// The negative arm, and it is the one that would pass against a flag that was
    /// parsed and then ignored — which is what the first draft of any such option
    /// does.
    /// </remarks>
    [Fact]
    public void DoesNothingWhenGenerationIsOff()
    {
        ReferenceTree.RequireNavCache();
        StageProbeMap();

        using var harness = Harness(options => options.NavGenerate = false);

        Assert.Null(harness.Server.Population?.Nav);
        Assert.Empty(Directory.GetFiles(_nav));
    }

    /// <summary>
    /// ⛔ A graph refused by the manifest is REPLACED on disk, with saving off.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The asymmetry is deliberate and it is the interesting half of this
    /// feature.</b> A map that never had a graph is not a fault to correct, so its
    /// generated graph stays in memory. A graph whose manifest line disagrees is a
    /// file describing terrain that is not there — leaving it means every restart
    /// refuses it and rebuilds, and any other tool reading the directory still gets
    /// the wrong graph.
    /// </remarks>
    [Fact]
    public void OverwritesAGraphThatNoLongerFitsItsMap()
    {
        ReferenceTree.RequireNavCache();
        StageProbeMap();

        // A real graph, filed under the probe's key, built from a different map.
        var wrong = NavCache.ReadFile(NavCache.FindFile(ReferenceTree.NavCacheDirectory, "ctf_truefort", 1));
        var key = NavCache.Key(Probe, 1);
        File.WriteAllText(Path.Combine(_nav, key + NavCache.Extension), wrong);

        // And a manifest line saying it belongs to the probe map, which it does
        // not — this is what makes the load a refusal rather than a silent
        // wrong answer.
        NavManifest.Write(
            Path.Combine(_nav, NavManifest.FileName),
            [(key, NavManifest.HashOf(
                LevelMap.FromPng("ctf_truefort", ReferenceTree.ReadMap("ctf_truefort")).ForArea(1)))]);

        using var harness = Harness();

        var built = harness.Server.Population?.Nav;
        Assert.NotNull(built);
        Assert.Equal(NavCache.Load(ReferenceTree.NavCacheDirectory, Original, 1).NodeCount, built!.NodeCount);

        // The stale plain file is gone and the replacement is the shipped
        // container — NavCacheWriter deletes the loser, because whichever way
        // FindFile broke the tie one of the two would be silently ignored.
        Assert.False(File.Exists(Path.Combine(_nav, key + NavCache.Extension)));
        Assert.True(File.Exists(Path.Combine(_nav, key + NavCache.CompressedExtension)));

        using var second = Harness(options => options.NavGenerate = false);

        Assert.NotNull(second.Server.Population?.Nav);
    }

    private ServerHarness Harness(Action<ServerOptions>? configure = null) =>
        new(options =>
        {
            options.MapDirectory = _maps;
            options.NavCacheDirectory = _nav;
            options.Map = Probe;
            configure?.Invoke(options);
        });

    /// <summary>
    /// <c>gg_debug</c> under a name nothing has a graph for.
    /// </summary>
    private void StageProbeMap() =>
        File.WriteAllBytes(Path.Combine(_maps, Probe + ".png"), ReferenceTree.ReadMap(Original));
}
