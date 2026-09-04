using Gg2.Bots;
using Gg2.Maps;
using Gg2.Nav;
using Gg2.Protocol;

namespace Gg2.Tests;

/// <summary>
/// ⛔ <b>The oracle.</b> Every graph this repository ships, rebuilt from the map
/// PNG and compared against the checked-in file.
/// </summary>
/// <remarks>
/// ⚠️ <b>This is the whole justification for the generator being a port rather
/// than a reimplementation, and for it having one flight model where
/// <c>gg2-nav-gen</c> has three.</b> That project could not check a change to its
/// single-jump path against anything — the GML generator it was ported from had
/// been deleted — so it duplicated the arc machinery per manoeuvre to keep
/// unverifiable edits off the hot path of every ordinary jump. Here the 25 graphs
/// in <c>nav/</c> are exactly that missing oracle: they were produced by
/// <c>gg2-nav-gen</c>, they are what the bot layer's pinned end-to-end
/// measurements were taken against, and a C# generator that reproduces them
/// character for character is provably the same generator.
/// <para>
/// ⛔ <b>Do not relax this to counts, or to a tolerance.</b> The traps this port
/// had to avoid — GM8's banker's rounding, <c>ds_priority_delete_min</c>'s
/// delete-by-swap order, the x87 snap in <c>SamplesFor</c> — each produced a graph
/// that looked right, had a plausible node and edge count, and was different on 22
/// of 23 maps. Only a full comparison sees them. If this test is ever weakened,
/// <see cref="NavArcModel"/>'s consolidation has to be undone with it.
/// </para>
/// <para>
/// ⚠️ <b>The comparison is on the decompressed TEXT and not on the container's
/// bytes</b>, deliberately: the shipped files were gzipped by <c>gzip -9 -n</c>
/// and this port gzips with .NET, which packs the same input differently. The
/// format is the text; the gzip is a container. <c>NavContainerTests</c> owns the
/// container.
/// </para>
/// </remarks>
public sealed class NavGeneratorTests
{
    /// <summary>
    /// Every shipped graph, rebuilt and compared character for character.
    /// </summary>
    /// <remarks>
    /// One test over all 25 rather than a theory per map: the useful failure
    /// message is "these three maps moved", and a theory reports twenty-five
    /// separate ones with no way to see which stage the three have in common.
    /// </remarks>
    [Fact]
    public void RebuildsEveryShippedGraphExactly()
    {
        ReferenceTree.RequireNavCache();

        var differing = new List<string>();
        var compared = 0;

        foreach (var (map, area) in NavCache.List(ReferenceTree.NavCacheDirectory))
        {
            var expected = NavCache.ReadFile(
                NavCache.FindFile(ReferenceTree.NavCacheDirectory, map, area));

            var built = NavGenerator.Build(Load(map).ForArea(area), area).Text;

            compared++;

            if (!string.Equals(expected, built, StringComparison.Ordinal))
                differing.Add($"{NavCache.Key(map, area)}: {FirstDifference(expected, built)}");
        }

        Assert.True(compared >= 25, $"only {compared} graphs found to compare against");

        Assert.True(
            differing.Count == 0,
            $"{differing.Count} of {compared} regenerated graphs differ from nav/:\n  "
            + string.Join("\n  ", differing));
    }

    /// <summary>
    /// The generator writes under the key the loader looks for.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Two copies of <c>navCacheKey</c> exist</b> — the generator's, because
    /// <c>Gg2.Nav</c> does not reference the reader, and <c>NavCache.Key</c>. They
    /// disagreeing is not a crash; it is a directory full of files and a server
    /// with no bot navigation, which looks like a bot bug.
    /// </remarks>
    [Fact]
    public void TheGeneratorAndTheLoaderAgreeOnTheCacheKey()
    {
        foreach (var name in new[] { "ctf_truefort", "cp_dirtbowl", "gg_debug2", "a map with spaces", "wüt" })
        {
            for (var area = 1; area <= 3; area++)
                Assert.Equal(NavCache.Key(name, area), NavGenerator.Key(name, area));
        }
    }

    /// <summary>
    /// A rebuilt graph parses back into the same <see cref="NavGraph"/> the shipped
    /// file does.
    /// </summary>
    /// <remarks>
    /// The text comparison above is stronger, but it does not exercise the seam the
    /// server actually uses: generate text, hand it to <c>NavCache.Parse</c>, get a
    /// graph. This does, on the map every other nav fixture uses.
    /// </remarks>
    [Fact]
    public void AGeneratedGraphParsesIntoTheSameGraphTheFileDoes()
    {
        ReferenceTree.RequireNavCache();

        var shipped = NavCache.Load(ReferenceTree.NavCacheDirectory, "ctf_truefort", 1);
        var built = NavCache.Parse(NavGenerator.Build(Load("ctf_truefort").ForArea(1), 1).Text, "ctf_truefort", 1);

        Assert.Equal(shipped.MaskWidth, built.MaskWidth);
        Assert.Equal(shipped.MaskHeight, built.MaskHeight);
        Assert.Equal(shipped.NodeCount, built.NodeCount);
        Assert.Equal(shipped.EdgeCount, built.EdgeCount);

        for (var i = 0; i < shipped.NodeCount; i++)
            Assert.Equal(shipped.Nodes[i], built.Nodes[i]);

        // ⚠️ Whole records, so the fractional NAV_EDGE_BUCKET is in the comparison.
        // Truncating it cost this port five of its fourteen scenarios once, and
        // every count stayed identical while it did.
        for (var i = 0; i < shipped.EdgeCount; i++)
            Assert.Equal(shipped.Edges[i], built.Edges[i]);
    }

    /// <summary>
    /// ⚠️ The sticky-jump stage is <b>purely additive</b>: with type-7 edges
    /// dropped, a sticky build is the default build.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>This is the property that lets the stage exist at all.</b> Every jump
    /// fan keeps a fixed number of candidates per side, so a stage that could feed
    /// the earlier fans would change which ordinary jumps are kept — a different
    /// set, not a superset, on every map and for every class. The stage runs last,
    /// reads the pre-fan adjacency snapshot, and keeps its own quota precisely so
    /// this holds; nothing but a comparison notices when it stops.
    /// </remarks>
    [Fact]
    public void TheStickyStageAddsEdgesAndMovesNone()
    {
        var map = Load("ctf_truefort").ForArea(1);

        var plain = NavCache.Parse(NavGenerator.Build(map, 1).Text, "ctf_truefort", 1);
        var sticky = NavCache.Parse(NavGenerator.Build(map, 1, sticky: true).Text, "ctf_truefort", 1);

        var survivors = new List<NavEdge>();

        foreach (var edge in sticky.Edges)
        {
            if (edge.Type != NavEdgeType.StickyJump)
                survivors.Add(edge);
        }

        Assert.True(sticky.EdgeCount > plain.EdgeCount, "the sticky stage emitted nothing at all");
        Assert.Equal(plain.EdgeCount, survivors.Count);

        for (var i = 0; i < plain.EdgeCount; i++)
            Assert.Equal(plain.Edges[i], survivors[i]);
    }

    /// <summary>
    /// ⛔ And nothing emits a type-7 edge unless it is asked to.
    /// </summary>
    /// <remarks>
    /// The GML game's <c>navFindPath</c> has no <c>canSticky</c> and cannot refuse
    /// one by class, so a cache carrying type 7 routes every class over an arc only
    /// a Demoman can fly. This port refuses them per query, which is why the stage
    /// may exist; the default staying off is what keeps a file generated here safe
    /// to hand to the game.
    /// </remarks>
    [Fact]
    public void NoShippedGraphCarriesAStickyEdge()
    {
        ReferenceTree.RequireNavCache();

        foreach (var (map, area) in NavCache.List(ReferenceTree.NavCacheDirectory))
        {
            foreach (var edge in NavCache.Load(ReferenceTree.NavCacheDirectory, map, area).Edges)
            {
                Assert.True(
                    edge.Type != NavEdgeType.StickyJump,
                    $"{NavCache.Key(map, area)} ships a NAV_EDGE_STICKYJUMP edge");
            }
        }
    }

    /// <summary>
    /// The area count the generator derives is the one the map actually has.
    /// </summary>
    [Fact]
    public void CountsTheStagesOfAMultiAreaMap()
    {
        Assert.Equal(1, NavGenerator.AreaCount(Load("ctf_truefort")));
        Assert.Equal(3, NavGenerator.AreaCount(Load("cp_dirtbowl")));
    }

    private static LevelMap Load(string map) => LevelMap.FromPng(map, ReferenceTree.ReadMap(map));

    /// <summary>Where two graph texts first disagree, in terms a reader can chase.</summary>
    private static string FirstDifference(string expected, string built)
    {
        var expectedLines = expected.Split('\n');
        var builtLines = built.Split('\n');

        // Line 2 is "<maskW> <maskH> <nodes> <edges>", which is the difference
        // worth naming when there is one — a graph that gained or lost a surface.
        if (expectedLines.Length > 1 && builtLines.Length > 1
            && !string.Equals(expectedLines[1], builtLines[1], StringComparison.Ordinal))
        {
            return $"header \"{expectedLines[1].Trim()}\" vs \"{builtLines[1].Trim()}\"";
        }

        for (var line = 0; line < Math.Min(expectedLines.Length, builtLines.Length); line++)
        {
            if (string.Equals(expectedLines[line], builtLines[line], StringComparison.Ordinal))
                continue;

            var a = expectedLines[line];
            var b = builtLines[line];
            var at = 0;

            while (at < a.Length && at < b.Length && a[at] == b[at])
                at++;

            // Every grid line is hex over 16-byte cells, so the character offset
            // names a cell: (offset - header) / 32 counts cells down a column.
            var cell = Math.Max(0, (at - 24) / 32);
            var what = line == 2 ? "node" : "edge";

            return $"line {line + 1} differs at character {at} (about {what} row {cell % Math.Max(1, cell + 1)}), "
                + $"lengths {a.Length} vs {b.Length}";
        }

        return $"lengths {expected.Length} vs {built.Length}";
    }
}
