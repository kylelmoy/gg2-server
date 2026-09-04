using System;
using System.IO;
using Gg2.Bots;
using Xunit;
using Xunit.Abstractions;

namespace Gg2.Tests;

/// <summary>
/// The next-hop file and the cost half it does not store.
/// </summary>
/// <remarks>
/// ⚠️ <b>The oracle here is <see cref="NavDistances.Derive(NavGraph)"/>, and the
/// comparison is bit-for-bit on purpose.</b> The stored costs are
/// <c>MathF.BitDecrement</c>-ed so the table stays an admissible lower bound; a
/// rebuild that rounded to nearest would be one ULP high, would still pass any
/// tolerance-based check, and would let the search prune the optimal route. A
/// tolerance comparison passed this file's first draft, which had exactly that
/// bug.
/// </remarks>
public sealed class NavDistanceFileTests(ITestOutputHelper output)
{
    private static NavGraph Graph(string map = "ctf_truefort", int area = 1) =>
        NavCache.Load(ReferenceTree.NavCacheDirectory, map, area);

    [Fact]
    public void ARebuiltTableIsBitIdenticalToADerivedOne()
    {
        var graph = Graph();
        var derived = NavDistances.Derive(graph, true, true, false);

        var hops = new ushort[graph.NodeCount * graph.NodeCount];

        for (var goal = 0; goal < graph.NodeCount; goal++)
            for (var from = 0; from < graph.NodeCount; from++)
            {
                var hop = derived.NextHop(from, goal);
                hops[(goal * graph.NodeCount) + from] =
                    hop == NavDistances.None ? ushort.MaxValue : (ushort)hop;
            }

        var rebuilt = NavDistances.FromNextHops(graph, true, true, false, hops);

        var checkedPairs = 0;

        for (var goal = 0; goal < graph.NodeCount; goal++)
            for (var from = 0; from < graph.NodeCount; from++)
            {
                var want = derived.Between(from, goal);
                var got = rebuilt.Between(from, goal);

                // BitConverter, not ==: this is the assertion that would have
                // let a one-ULP rounding difference through if it compared with
                // a tolerance, and NaN would compare unequal to itself.
                Assert.Equal(BitConverter.SingleToInt32Bits(want), BitConverter.SingleToInt32Bits(got));
                Assert.Equal(derived.NextHop(from, goal), rebuilt.NextHop(from, goal));
                checkedPairs++;
            }

        output.WriteLine($"{checkedPairs:N0} pairs, bit-identical");
    }

    [Fact]
    public void AFileRoundTripsToTheSameSet()
    {
        var graph = Graph();
        var derived = NavDistanceSet.Derive(graph);
        var path = Path.Combine(Path.GetTempPath(), $"gg2-dist-{Guid.NewGuid():N}.dist.gz");

        try
        {
            NavDistanceFile.Write(path, graph, derived);

            var read = NavDistanceFile.TryRead(path, graph, out var reason);

            Assert.NotNull(read);
            Assert.Equal("loaded", reason);
            Assert.Equal(derived.TableCount, read!.TableCount);
            Assert.Equal(derived.PresentArcs, read.PresentArcs);
            Assert.Equal(derived.Bytes, read.Bytes);

            // Every one of the eight profiles, not just the distinct ones: the
            // expansion back over the raw keys is what a file cannot store and
            // FromTables has to redo.
            foreach (var (dj, rj, sj) in Profiles())
            {
                var query = new NavQuery
                {
                    CanDoubleJump = dj,
                    CanRocketJump = rj,
                    CanStickyJump = sj,
                };

                var a = derived.For(in query);
                var b = read.For(in query);

                for (var goal = 0; goal < graph.NodeCount; goal += 17)
                    for (var from = 0; from < graph.NodeCount; from += 13)
                    {
                        Assert.Equal(
                            BitConverter.SingleToInt32Bits(a.Between(from, goal)),
                            BitConverter.SingleToInt32Bits(b.Between(from, goal)));
                        Assert.Equal(a.NextHop(from, goal), b.NextHop(from, goal));
                    }
            }

            output.WriteLine($"{new FileInfo(path).Length / 1024.0:0.0} KB on disk for {derived.TableCount} tables");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AFileBuiltAgainstADifferentGraphIsRefusedRatherThanUsed()
    {
        var mine = Graph();
        var other = Graph("ctf_2dfort");
        var path = Path.Combine(Path.GetTempPath(), $"gg2-dist-{Guid.NewGuid():N}.dist.gz");

        try
        {
            NavDistanceFile.Write(path, other, NavDistanceSet.Derive(other));

            // Refused on the node count, which differs here...
            Assert.Null(NavDistanceFile.TryRead(path, mine, out var reason));
            output.WriteLine(reason);

            // ...and the hash catches the case the node count cannot: a graph
            // with the same shape and different edges. This is the check that
            // stops a bot pathing through a wall after a map is edited.
            Assert.NotEqual(
                Convert.ToHexString(NavDistanceFile.HashOf(mine)),
                Convert.ToHexString(NavDistanceFile.HashOf(other)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AMissingFileDerivesInsteadOfFailing()
    {
        var graph = Graph();
        var empty = Path.Combine(Path.GetTempPath(), $"gg2-nodist-{Guid.NewGuid():N}");
        Directory.CreateDirectory(empty);

        try
        {
            var set = NavDistanceSet.LoadOrDerive(empty, "ctf_truefort", 1, graph, out var reason);

            Assert.NotNull(set);
            Assert.StartsWith("derived", reason);
            Assert.Equal(NavDistanceSet.Derive(graph).TableCount, set.TableCount);
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }

    [Fact]
    public void TheShippedGraphsAllCarryTablesThatLoad()
    {
        var directory = ReferenceTree.NavCacheDirectory;
        var loaded = 0;
        var derivedInstead = 0;

        foreach (var (map, area) in NavCache.List(directory))
        {
            var graph = NavCache.Load(directory, map, area);
            var set = NavDistanceSet.LoadOrDerive(directory, map, area, graph, out var reason);

            Assert.NotNull(set);

            if (reason == "loaded")
                loaded++;
            else
            {
                derivedInstead++;
                output.WriteLine($"{map}#{area}: {reason}");
            }
        }

        output.WriteLine($"{loaded} loaded, {derivedInstead} derived");

        // ⚠️ The point of the whole feature. If this goes red after a nav
        // change, the tables are stale: re-run `gg2-server navgen --distances`.
        Assert.Equal(0, derivedInstead);
    }

    private static (bool, bool, bool)[] Profiles() =>
    [
        (false, false, false), (true, false, false), (false, true, false), (true, true, false),
        (false, false, true), (true, false, true), (false, true, true), (true, true, true),
    ];
}
