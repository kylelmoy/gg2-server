using GG2.Bots;
using GG2.Protocol;

namespace GG2.Tests;

/// <summary>
/// <see cref="EdgeSearch.Run"/>, the search stepped across ticks, finds what
/// <see cref="EdgeSearch.Fastest"/> finds, and <see cref="EdgeSearch.Trace"/>
/// follows its plan.
/// </summary>
public sealed class EdgeSearchTests
{
    [Fact]
    public void ASteppedSearchFindsTheSamePlanAsAWholeOne()
    {
        ReferenceTree.RequireNavCache();

        var world = ScenarioWorld.Load("gg_debug", 1, ReferenceTree.MapsDirectory, ReferenceTree.NavCacheDirectory);
        var graph = world.Nav!;
        var compared = 0;

        foreach (var edge in graph.Edges.ToArray().Where(e => e.Type == NavEdgeType.Jump).Take(5))
        {
            var slot = world.Place(PlayerClass.Soldier, Team.Red, Mid(graph, edge.From), EdgeFlights.StandingY(graph, edge.From));
            var start = world.State.Characters[slot];
            var whole = EdgeSearch.Fastest(world.Collision, graph, in start, edge.To, Mid(graph, edge.To), maxTicks: 120, beam: 100);

            var run = new EdgeSearch.Run(world.Collision, graph, in start, edge.To, Mid(graph, edge.To), maxTicks: 120, beam: 100);
            while (!run.Advance(7))
            {
            }

            Assert.Equal(whole.Found, run.Result.Found);
            Assert.Equal(whole.Ticks, run.Result.Ticks);
            Assert.Equal(whole.Plan, run.Result.Plan);
            Assert.Equal(whole.Expanded, run.Result.Expanded);

            if (whole.Found)
            {
                var track = EdgeSearch.Trace(world.Collision, in start, whole.Plan);
                Assert.Equal(whole.Plan.Length, track.Length);
                Assert.Equal(edge.To, graph.NodeFromWorld(track[^1].X, track[^1].Y));
                compared++;
            }
        }

        Assert.True(compared > 0, "no jump edge of gg_debug had a plan to compare");
    }

    private static double Mid(NavGraph graph, int node) =>
        NavGraph.ColumnWorldX((graph.Nodes[node].X0 + graph.Nodes[node].X1) / 2);
}
