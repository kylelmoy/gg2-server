using GG2.Bots;
using GG2.Protocol;
using GG2.Server;
using Xunit.Abstractions;

namespace GG2.Tests;

/// <summary>
/// The per-profile table set behind <c>--nav-distances</c>: that it hands each
/// asker a table that is still a lower bound, that it is the TIGHT one, and that
/// it does not build eight of them.
/// </summary>
/// <remarks>
/// <b>The tightness assertion is the one that earns its place.</b>
/// <see cref="NavDistancesTests"/> already proves a single table never
/// overestimates, and a set that returned the permissive table for every asker
/// would pass that unchanged — while measuring <b>1.06x</b> against the
/// straight-line heuristic where the class-matched table measures <b>1.36x</b>.
/// A set that quietly went permissive is therefore a correct, green, worthless
/// feature, which is exactly the failure mode <c>CONTRIBUTING.md</c> warns about, and
/// nothing else in the suite could see it.
/// </remarks>
public sealed class NavDistanceSetTests(ITestOutputHelper output)
{
    private static NavGraph Graph(string map = "ctf_truefort")
    {
        ReferenceTree.RequireNavCache();
        return NavCache.Load(ReferenceTree.NavCacheDirectory, map, 1);
    }

    /// <summary>
    /// Every profile gets the table built for exactly its own arcs — not the
    /// permissive one.
    /// </summary>
    /// <remarks>
    /// Compared entry by entry against a table derived directly for that
    /// profile, on the whole N² grid, so "close enough" cannot pass.
    /// ⚠️ <b>The rocket-jump profile is the one that matters</b>: those arcs are
    /// about 14% of the shipped edges and they are shortcuts, so a walker handed
    /// the rocket-jumper's table gets a bound that is loose exactly where it was
    /// meant to bite.
    /// </remarks>
    [Fact]
    public void EachProfileGetsItsOwnTable()
    {
        var graph = Graph();
        var set = NavDistanceSet.Derive(graph);

        foreach (var (dj, rj, sj) in (ReadOnlySpan<(bool, bool, bool)>)
            [(false, false, false), (true, false, false), (false, true, false), (true, true, false)])
        {
            var expected = NavDistances.Derive(graph, dj, rj, sj);
            var actual = set.For(new NavQuery(
                Team.Red, CanDoubleJump: dj, CanRocketJump: rj, CanStickyJump: sj));

            for (var from = 0; from < graph.NodeCount; from++)
            {
                for (var goal = 0; goal < graph.NodeCount; goal++)
                {
                    Assert.Equal(expected.Between(from, goal), actual.Between(from, goal));
                }
            }
        }
    }

    /// <summary>
    /// A walker's table really is TIGHTER than the permissive one, on a graph
    /// that has jump arcs in it.
    /// </summary>
    /// <remarks>
    /// <b>The assertion that makes the previous one mean something.</b> Equality
    /// against a derived table proves the set wired the right builder up; this
    /// proves the right builder produces a different answer, so a regression that
    /// collapsed the set back to one permissive table would fail here rather than
    /// pass everything.
    /// </remarks>
    [Fact]
    public void TheWalkersTableIsStrictlyTighterWhereTheGraphJumps()
    {
        var graph = Graph();

        if (!HasJumpArc(graph))
            return;

        var set = NavDistanceSet.Derive(graph);

        var walker = set.For(new NavQuery(Team.Red));
        var jumper = set.For(new NavQuery(Team.Red, CanRocketJump: true));

        var tighter = 0;

        for (var from = 0; from < graph.NodeCount; from++)
        {
            for (var goal = 0; goal < graph.NodeCount; goal++)
            {
                var w = walker.Between(from, goal);
                var j = jumper.Between(from, goal);

                // A shortcut can only ever make the trip cheaper, so the
                // jumper's number is at or below the walker's everywhere.
                Assert.True(j <= w, $"{from}->{goal}: jumper {j} above walker {w}");

                if (w > j)
                    tighter++;
            }
        }

        output.WriteLine($"{tighter} of {graph.NodeCount * graph.NodeCount} pairs are tighter for a walker");
        Assert.True(tighter > 0, "the walker's table is identical to the jumper's on a graph with jump arcs");
    }

    /// <summary>
    /// A profile the graph cannot tell apart shares a table rather than paying
    /// for its own.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>This is the cost control, and it is why the map-change bill is
    /// tens of milliseconds rather than eight times that.</b> A graph with no
    /// sticky arcs gives the same distances whether or not the asker may take
    /// one, so eight profile keys collapse to the two or three the graph can
    /// actually distinguish.
    /// </remarks>
    [Fact]
    public void ProfilesTheGraphCannotDistinguishShareOneTable()
    {
        var graph = Graph();
        var set = NavDistanceSet.Derive(graph);

        Assert.InRange(set.TableCount, 1, 8);

        // Whatever the graph contains, a sticky-jump asker on a graph with no
        // sticky arcs must be handed the very same object as one without.
        if (!HasArc(graph, NavEdgeType.StickyJump))
        {
            Assert.Same(
                set.For(new NavQuery(Team.Red)),
                set.For(new NavQuery(Team.Red, CanStickyJump: true)));
        }

        output.WriteLine($"{set.TableCount} table(s), {set.Bytes / 1024.0 / 1024.0:0.00} MB");
    }

    /// <summary>
    /// Across every shipped graph, the table an asker is handed never
    /// overestimates that asker's real remaining cost.
    /// </summary>
    /// <remarks>
    /// <b>The same claim <see cref="NavDistancesTests.TheTableNeverOverestimates"/>
    /// makes, re-made through the SELECTOR.</b> That test builds the table it
    /// checks; this one asks the set for it, so a selector that handed out a
    /// table tighter than the asker deserves — the one direction that produces
    /// wrong routes rather than slow ones — fails here.
    /// </remarks>
    [Fact]
    public void TheSelectedTableNeverOverestimates()
    {
        ReferenceTree.RequireNavCache();

        var checkedPairs = 0;

        foreach (var entry in NavCache.List(ReferenceTree.NavCacheDirectory))
        {
            var graph = NavCache.Load(ReferenceTree.NavCacheDirectory, entry.Map, entry.Area);
            var set = NavDistanceSet.Derive(graph);
            var search = new NavSearch(graph);
            var path = new List<int>();

            foreach (var query in Queries())
            {
                // ⚠️ A COARSER stride than the 7/11 the rest of this layer
                // sweeps with, and that is a deliberate cost decision: this gate
                // builds a whole set per graph, and at 7/11 over sixteen query
                // classes it cost 37 s — enough CPU to make the wall-clock
                // pacing suites flake, which is the trap this branch already
                // paid for once. docs/TRAPS-BOTS.md, "Route variety, scored".
                for (var start = 0; start < graph.NodeCount; start += 13)
                {
                    for (var goal = 0; goal < graph.NodeCount; goal += 17)
                    {
                        if (start == goal || !search.Find(start, goal, in query, path))
                            continue;

                        var priced = PriceOf(graph, path, in query);

                        if (double.IsPositiveInfinity(priced))
                            continue;

                        var bound = set.For(in query).Between(start, goal);

                        Assert.True(
                            bound <= priced,
                            $"{entry.Map}/{entry.Area} {start}->{goal} {query}: "
                            + $"table {bound} above the route's own {priced}");

                        checkedPairs++;
                    }
                }
            }
        }

        output.WriteLine($"{checkedPairs} pairs checked across every shipped graph and query class");
        Assert.True(checkedPairs > 1000, $"only {checkedPairs} pairs checked; the sweep found nothing");
    }

    /// <summary>
    /// With <c>--nav-distances</c> on, EVERY bot on a live server has the tables
    /// — including the ones that joined after the map change.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>The bug this exists for is a MIXED configuration, not a missing
    /// one.</b> The set is handed out in two places — <c>MapChanged</c> reaches
    /// the bots that survived a rotation, the creation site reaches the ones that
    /// fill in afterwards — and a server missing the second would run some bots
    /// on the exact heuristic and some on the straight-line one. Every count,
    /// every log line and every other gate would still look right, and the
    /// routes would simply be inconsistent. It is also invisible to the headless
    /// path, which builds its whole roster at once.
    /// <para>
    /// ⚠️ And the OFF case is asserted beside it, because "every bot has one" is
    /// trivially satisfiable by handing one out unconditionally — which would
    /// move every route on a server that never asked for it.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EveryBotOnTheServerGetsTheTablesWhenAsked(bool on)
    {
        ReferenceTree.RequireNavCache();

        var options = new ServerOptions
        {
            Port = 0,
            MapDirectory = MapDirectory.Resolve(),
            NavCacheDirectory = MapDirectory.ResolveNav(),
            Map = "ctf_truefort",
            NavDistances = on,
            Bots = BotPopulationOptions.Default with
            {
                Enabled = true,
                FillToPlayers = 6,
                MaxBots = 6,
                MinHumans = 0,
            },
        };

        using var server = new GameServer(options, new RecordingLog());
        server.Start();

        for (var i = 0; i < 90; i++)
            server.Tick();

        var slots = server.BotSlots;
        Assert.NotEmpty(slots);

        foreach (var slot in slots)
        {
            var bot = ScriptedBot.Of(server.SourceOf(slot));
            Assert.NotNull(bot);

            if (on)
                Assert.NotNull(bot!.Distances);
            else
                Assert.Null(bot!.Distances);
        }
    }

    /// <summary>The query classes the bot layer actually produces.</summary>
    /// <remarks>
    /// ⚠️ <c>CanStickyJump</c> is never set by <c>ScriptedBot</c>'s per-tick
    /// rebuild — <c>navFindPath</c> has no such parameter and the shipped caches
    /// carry no type-7 edges — but it is swept anyway, because the set has to be
    /// right for a query somebody constructs by hand.
    /// </remarks>
    private static IEnumerable<NavQuery> Queries()
    {
        // ⚠️ ONE team and gates open, deliberately. The set splits on the three
        // class arcs and on nothing else — a team and a gate only ever REMOVE
        // edges from an asker, so they cannot make a bound that held start
        // failing — and sweeping them would quadruple a gate that already costs
        // more than any other in this file. The profiles are what must be swept.
        yield return new NavQuery(Team.Red);
        yield return new NavQuery(Team.Red, CanDoubleJump: true);
        yield return new NavQuery(Team.Red, CanRocketJump: true);
        yield return new NavQuery(Team.Red, CanStickyJump: true);
    }

    /// <summary>What the route the search returned actually costs.</summary>
    private static double PriceOf(NavGraph graph, List<int> path, in NavQuery query)
    {
        var total = 0.0;

        for (var i = 0; i + 1 < path.Count; i++)
        {
            var best = double.PositiveInfinity;
            var edges = graph.EdgesFrom(path[i]);

            for (var e = 0; e < edges.Length; e++)
            {
                ref readonly var edge = ref edges[e];

                if (edge.To == path[i + 1] && NavGraph.EdgeRoutable(in edge, in query) && edge.Cost < best)
                    best = edge.Cost;
            }

            if (double.IsPositiveInfinity(best))
                return double.PositiveInfinity;

            total += best;
        }

        return total;
    }

    private static bool HasJumpArc(NavGraph graph) =>
        HasArc(graph, NavEdgeType.DoubleJump)
        || HasArc(graph, NavEdgeType.RocketJump)
        || HasArc(graph, NavEdgeType.StickyJump);

    private static bool HasArc(NavGraph graph, NavEdgeType type)
    {
        for (var e = 0; e < graph.EdgeCount; e++)
        {
            if (graph.Edges[e].Type == type)
                return true;
        }

        return false;
    }
}
