using Gg2.Bots;
using Gg2.Protocol;

namespace Gg2.Tests;

/// <summary>
/// The exact routes A\* returns on every shipped graph, pinned.
/// </summary>
/// <remarks>
/// ⛔ <b>The gate the nav layer did not have.</b> <c>NavSearchFixtureTests</c>
/// asserts <em>properties</em> — a route is a walkable chain, it starts and ends
/// where it was asked to, a gate takes routes away — and every one of them holds
/// for a route that is not the route this port returns today. So a change that
/// swapped two equal-cost routes, or reordered the frontier, would pass the whole
/// suite: the three pinned bot measurements cover three legs, and nothing covered
/// the other hundreds of thousands.
/// <para>
/// ⚠️ <b>Equal-cost routes are not a corner case here.</b> Half the shipped maps
/// are symmetric by construction, so two mirror-image routes of identical cost
/// are the normal situation and which one comes back is decided by the order the
/// frontier happens to pop. That order is an implementation detail of
/// <c>PriorityQueue</c> today; the moment somebody replaces it — for speed, which
/// is a live proposal — every bot on the map can take a different lane, and no
/// other test in this repo would notice.
/// </para>
/// <para>
/// ⚠️ <b>A failure here is a RE-RECORD, not a broken test</b>, and it is the same
/// rule the three pinned bot measurements carry: check the change is one you
/// meant, check it went the way you meant, update the row, and say so in the
/// commit. ⛔ <b>And the other direction is the alarm</b> — a digest that does
/// <em>not</em> move when the search was deliberately changed says the change
/// never reached what this measures.
/// </para>
/// </remarks>
public sealed class NavRouteIdentityTests
{
    /// <summary>How many start/goal pairs are folded in per graph and team.</summary>
    private const int Pairs = 250;

    /// <summary>
    /// The digest of every route on every shipped graph. ⚠️ RE-RECORDED 2026-09-02
    /// when the search took NavConstants.SearchWeight and stopped re-opening: 23 of
    /// the 25 digests moved and every found-count is unchanged, which is the
    /// property that mattered — no route that existed became unreachable.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A digest rather than a list, and that is a real trade.</b> The whole
    /// sweep is about 150,000 node indices; a checked-in list of them would be
    /// unreadable and unreviewable, and nobody would diff it. The cost is that a
    /// failure says "something moved" and not "which leg moved" — the second
    /// column exists for that: it is how many of the pairs found a route at all,
    /// so a wholesale breakage and a single swapped lane look different.
    /// </remarks>
    private static readonly Dictionary<string, (ulong Digest, int Found)> Expected = new()
    {
        ["arena_lumberyard_a1"] = (14788675366214503893UL, 984),
        ["arena_montane_a1"] = (11295441403353704709UL, 1000),
        ["cp_dirtbowl_a1"] = (14162545353670478030UL, 325),
        ["cp_dirtbowl_a2"] = (15506444063556699280UL, 309),
        ["cp_dirtbowl_a3"] = (4885258262418318904UL, 312),
        ["cp_egypt_a1"] = (5210553772887417302UL, 964),
        ["ctf_2dfort_a1"] = (2611945521325008427UL, 912),
        ["ctf_avanti_a1"] = (12893365823046489519UL, 854),
        ["ctf_classicwell_a1"] = (13597301971318768909UL, 890),
        ["ctf_conflict_a1"] = (4945426045024243719UL, 670),
        ["ctf_eiger_a1"] = (16551968544854263959UL, 970),
        ["ctf_oldfort_a1"] = (7240544624571533099UL, 534),
        ["ctf_orange_a1"] = (12549215511932400003UL, 880),
        ["ctf_truefort_a1"] = (10566035562846899415UL, 844),
        ["ctf_waterway_a1"] = (7712211561662090991UL, 924),
        ["dkoth_atalia_a1"] = (4804870971441709201UL, 782),
        ["dkoth_sixties_a1"] = (15780653206685659676UL, 826),
        ["gen_destroy_a1"] = (10330741463005333567UL, 990),
        ["gg_debug2_a1"] = (3234777014370026277UL, 1000),
        ["gg_debug_a1"] = (14260607603416349530UL, 525),
        ["koth_corinth_a1"] = (5519080838724246400UL, 948),
        ["koth_gallery_a1"] = (13737592279165596173UL, 960),
        ["koth_harvest_a1"] = (17422843924765240433UL, 577),
        ["koth_valley_a1"] = (12764766492475167033UL, 856),
        ["tdm_mantic_a1"] = (5864869011259159109UL, 632),
    };

    /// <summary>
    /// Every shipped graph returns exactly the routes it returned when this was
    /// recorded.
    /// </summary>
    [Fact]
    public void EveryShippedGraphReturnsTheRoutesItWasRecordedWith()
    {
        ReferenceTree.RequireNavCache();

        var actual = Sweep();

        // ⚠️ Recorded rather than asserted, when the table is empty: printing the
        // rows is how the table is filled, and an empty table must not pass.
        Assert.NotEmpty(Expected);

        foreach (var (key, value) in actual)
        {
            Assert.True(Expected.ContainsKey(key), $"{key} has no recorded row — add it");
            Assert.Equal(Expected[key], value);
        }

        Assert.Equal(Expected.Count, actual.Count);

        // ⛔ Agreement is worthless if the sweep found nothing to agree about.
        var found = actual.Values.Sum(v => v.Found);
        Assert.True(found > 5_000, $"only {found} routes were found across {actual.Count} graphs");
    }

    /// <summary>
    /// Folds every route on every shipped graph into one digest per graph.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The pairs are derived arithmetically, not drawn from
    /// <see cref="Random"/>.</b> A seeded <c>Random</c> would be reproducible in
    /// practice and is a dependency on a framework implementation detail for a
    /// value that gets checked in — the two coprime strides below are the same
    /// coverage with nothing behind them.
    /// </remarks>
    private static SortedDictionary<string, (ulong Digest, int Found)> Sweep()
    {
        var results = new SortedDictionary<string, (ulong, int)>();

        foreach (var (map, area) in NavCache.List(ReferenceTree.NavCacheDirectory))
        {
            var graph = NavCache.Load(ReferenceTree.NavCacheDirectory, map, area);
            var search = new NavSearch(graph);
            var path = new List<int>();

            var digest = 14695981039346656037UL;
            var found = 0;

            foreach (var team in new[] { Team.Red, Team.Blue })
            {
                // Both capability flags, because a class arc that stopped being
                // refused would change routes for exactly one kind of bot.
                foreach (var canRocketJump in new[] { false, true })
                {
                    var query = new NavQuery(team, CanRocketJump: canRocketJump);

                    for (var i = 1; i <= Pairs; i++)
                    {
                        var start = (int)((long)i * 7919 % graph.NodeCount);
                        var goal = (int)((long)i * 104729 % graph.NodeCount);

                        if (!search.Find(start, goal, query, path))
                        {
                            digest = Fold(digest, ulong.MaxValue);
                            continue;
                        }

                        found++;

                        foreach (var node in path)
                            digest = Fold(digest, (ulong)node);

                        digest = Fold(digest, 0xFFFF_FFFFUL);
                    }
                }
            }

            results[$"{map}_a{area}"] = (digest, found);
        }

        return results;
    }

    /// <summary>FNV-1a over 64-bit values. Any stable fold would do.</summary>
    private static ulong Fold(ulong hash, ulong value)
    {
        for (var i = 0; i < 8; i++)
        {
            hash ^= (value >> (i * 8)) & 0xFF;
            hash *= 1099511628211UL;
        }

        return hash;
    }

    /// <summary>
    /// Prints the table above. Not a test of anything; run it to re-record.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Skipped rather than deleted.</b> A recorder that lives in the
    /// session that wrote it has to be rebuilt by whoever re-records, and they
    /// will rebuild it slightly differently — which is how a re-recorded pin
    /// stops meaning what the old one meant.
    /// </remarks>
    [Fact(Skip = "Recorder. Remove the Skip, run, and paste the output into Expected.")]
    public void Record()
    {
        ReferenceTree.RequireNavCache();

        foreach (var (key, value) in Sweep())
            Console.WriteLine($"        [\"{key}\"] = ({value.Digest}UL, {value.Found}),");
    }
}
