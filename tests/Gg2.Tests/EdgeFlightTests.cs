using Gg2.Bots;
using Gg2.Protocol;

namespace Gg2.Tests;

/// <summary>
/// <see cref="EdgeFlights"/> against the shipped nav caches — real arcs, real
/// map, real simulation.
/// </summary>
/// <remarks>
/// <b>What this answers that nothing else does.</b>
/// <see cref="PathFollowerTests"/> asserts the arms' decisions on hand-built
/// graphs and <see cref="PathFollowerFixtureTests"/> walks one long leg made
/// entirely of the kinds that need no weapon. Neither ever leaves the ground
/// under a rocket or a mine on an arc a generator actually emitted, and the one
/// time that was done by hand it produced a number — "zero sticky jumps across
/// 48 real edges on four maps" — that was true, useless, and about a bug three
/// layers away (docs/TRAPS-BOTS.md, "the arm was never asked").
/// <para>
/// ⚠️ <b>These pin counts against <c>Source/build/botnav</c>, which is
/// <c>gg2-nav-gen</c>'s output and not checked in.</b> Same exposure as
/// <see cref="PathFollowerFixtureTests"/>'s 137-node route, and the same
/// discipline: a failure here is the <b>re-record</b> — check the change is one
/// you meant, check it went the way you meant, update the row, say so in the
/// commit.
/// </para>
/// </remarks>
public sealed class EdgeFlightTests(Xunit.Abstractions.ITestOutputHelper output)
{
    /// <summary>
    /// The map the pinned sweeps run on.
    /// </summary>
    /// <remarks>
    /// <c>gg_debug</c> rather than a real map because it is small enough that a
    /// whole edge kind is a sweep of tens rather than hundreds — 67 rocket jumps
    /// and 23 air jumps, in about 120ms and 50ms. The same call on
    /// <c>ctf_truefort</c>'s 878 rocket jumps is eight seconds, which is a
    /// measurement rather than a gate.
    /// </remarks>
    private const string Map = "gg_debug";

    private static Func<ScenarioWorld> World(string map = Map)
    {
        ReferenceTree.RequireAvailable();
        ReferenceTree.RequireNavCache();

        return () => ScenarioWorld.Load(map, 1, ReferenceTree.IncludedFiles, ReferenceTree.NavCacheDirectory);
    }

    private static (int Measured, int Arrived, int Flown, int Blacklists) Tally(IReadOnlyList<EdgeFlight> flights)
    {
        var measured = 0;
        var arrived = 0;
        var flown = 0;
        var blacklists = 0;

        foreach (var flight in flights)
        {
            if (!flight.Measured)
                continue;

            measured++;
            blacklists += flight.Blacklists;

            if (flight.Outcome == EdgeFlightOutcome.Arrived)
                arrived++;

            if (flight.Flown)
                flown++;
        }

        return (measured, arrived, flown, blacklists);
    }

    /// <summary>
    /// A node's own midpoint resolves to that node, on every arc edge of every
    /// shipped graph — and so does the takeoff column.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>This corrects a claim this repo made about itself.</b>
    /// docs/TRAPS-BOTS.md said the first flight harness measured the wrong edges
    /// because "on <c>ctf_truefort</c> and <c>cp_egypt</c> the takeoff column does
    /// not resolve to the from-node". Measured here over all 18,396 rocket-jump
    /// and air-jump edges in <c>Source/build/botnav</c>: <b>the takeoff column
    /// resolves to the from-node every single time</b>, and so does the midpoint.
    /// Whatever that harness started from, it was neither of these.
    /// <para>
    /// The midpoint is what <see cref="EdgeFlights"/> uses anyway, and the reason
    /// is stronger than a measurement: <see cref="NavGraph.NodeFromWorld"/> ranks
    /// <c>dy * 4 + dx</c>, a node's own stand y gives it <c>dy = 0</c> exactly,
    /// its own midpoint column gives <c>dx = 0</c>, and no second node can hold
    /// that column on that row — so rank 0 is unique and the answer cannot be
    /// anything else. The takeoff column happens to sit inside the node's span on
    /// every shipped arc, which is why it agrees; nothing guarantees a generator
    /// keeps it there.
    /// </para>
    /// <para>
    /// So this is the gate rather than <see cref="EdgeFlightOutcome.StartUnresolved"/>,
    /// which reads zero on every cache and is the harness refusing to guess.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryArcEdgeResolvesFromItsOwnNodeAndFromItsTakeoffColumn()
    {
        ReferenceTree.RequireNavCache();

        var edges = 0;
        var midResolves = 0;
        var takeoffResolves = 0;

        foreach (var (map, area) in NavCache.List(ReferenceTree.NavCacheDirectory))
        {
            var graph = NavCache.Load(ReferenceTree.NavCacheDirectory, map, area);

            for (var i = 0; i < graph.EdgeCount; i++)
            {
                ref readonly var edge = ref graph.Edges[i];

                if (edge.Type is not (NavEdgeType.RocketJump or NavEdgeType.DoubleJump))
                    continue;

                edges++;

                var y = graph.StandY(edge.From);
                var node = graph.Nodes[edge.From];
                var mid = NavGraph.ColumnWorldX((node.X0 + node.X1) / 2);

                if (graph.NodeFromWorld(mid, y) == edge.From)
                    midResolves++;

                if (graph.NodeFromWorld(NavGraph.ColumnWorldX(edge.Takeoff), y) == edge.From)
                    takeoffResolves++;
            }
        }

        output.WriteLine($"{edges} arc edges; midpoint resolves {midResolves}, takeoff column {takeoffResolves}");

        Assert.Equal(18_396, edges);
        Assert.Equal(edges, midResolves);
        Assert.Equal(edges, takeoffResolves);
    }

    /// <summary>
    /// A soldier flies real rocket-jump arcs off the shipped cache.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b><see cref="EdgeFlight.Flown"/> is the load-bearing number and
    /// arrivals are not it.</b> Twenty of <c>gg_debug</c>'s 67 rocket jumps can be
    /// posed as a route, all twenty arrive, and only twelve of those arrivals
    /// involved a rocket — the other eight are node pairs close enough that the
    /// bot simply got there. On <c>ctf_truefort</c> the same split is 328 arrivals
    /// against 214 flights. A gate on arrivals alone would stay green with the arm
    /// deleted.
    /// <para>
    /// ⚠️ Which is exactly what makes this the control the synthetic tests could
    /// not be. <b>Mutation-checked</b>: rip the ATTACK half out of
    /// <see cref="PathFollower.ApplyLaunch"/> and this goes <b>20 arrivals / 12
    /// flights → 16 / 8</b>. Note which half moves. It does <em>not</em> go to
    /// zero, because <see cref="BotPathState.RocketJumps"/> counts what the
    /// takeoff gate <em>asked</em> for rather than what the weapon fired — so the
    /// arrival count is the honest signal here and the manoeuvre count on its own
    /// would have stayed green for a third of the arcs.
    /// </para>
    /// </remarks>
    [Fact]
    public void RealRocketJumpArcsAreFlown()
    {
        var flights = EdgeFlights.Sweep(World(), NavEdgeType.RocketJump);
        var (measured, arrived, flown, blacklists) = Tally(flights);

        output.WriteLine($"{Map} rocket jumps: {EdgeFlights.Summarise(flights)}");

        Assert.Equal(67, flights.Count);
        Assert.Equal(20, measured);
        Assert.Equal(20, arrived);
        Assert.Equal(12, flown);

        // ⚠️ TWO, and pinning the real number rather than a hoped-for zero is the
        // point. Both are one arc each: 28->30 fires once, goes off route, and the
        // retry arrives; 30->12 fires twice and takes 166 ticks against the 74-97
        // every other arc on this map needs. That is the off-route machinery doing
        // exactly what it is for — one retry, then a ban — so it is a cost rather
        // than a fault, but it is a cost worth watching. PathFollowerFixtureTests
        // pins zero over a 137-node leg and this pins two over twenty single-hop
        // arcs; a bot that starts banning edges still arrives, just slowly, inside
        // any budget, with the route quality gone and nothing red.
        Assert.Equal(2, blacklists);
    }

    /// <summary>A scout flies real air-jump arcs off the shipped cache.</summary>
    /// <remarks>
    /// The kind with no weapon in its gate, which is why nine of ten arrivals are
    /// flights here against twelve of twenty for the rocket: nothing can be cold.
    /// </remarks>
    [Fact]
    public void RealDoubleJumpArcsAreFlown()
    {
        var flights = EdgeFlights.Sweep(World(), NavEdgeType.DoubleJump);
        var (measured, arrived, flown, blacklists) = Tally(flights);

        output.WriteLine($"{Map} air jumps: {EdgeFlights.Summarise(flights)}");

        Assert.Equal(23, flights.Count);
        Assert.Equal(10, measured);
        Assert.Equal(10, arrived);
        Assert.Equal(9, flown);
        Assert.Equal(0, blacklists);
    }

    /// <summary>
    /// An arc A* routes around is skipped, and the skip is what makes the number
    /// mean anything.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The premise this test asserts is its whole point.</b> A* costs a jump
    /// the same as a walk, so on <c>gg_debug</c> it goes round 47 of the 67
    /// rocket-jump arcs — and every one of those, driven anyway, is a bot walking
    /// somewhere while a counter says "rocket jump attempted". That is how
    /// <c>koth_harvest</c> reported four of four arcs "arriving" without the bot
    /// ever leaving the ground.
    /// <para>
    /// So: take a skipped arc, show the route really is longer than the one hop,
    /// and show that flying it anyway arrives <em>without</em> the manoeuvre. If
    /// the skip were dropped, that arrival would be counted as a success for an
    /// arm that did nothing.
    /// </para>
    /// </remarks>
    [Fact]
    public void AnArcAStarRoutesAroundIsSkippedRatherThanCreditedToTheArm()
    {
        var world = World();
        var flights = EdgeFlights.Sweep(world, NavEdgeType.RocketJump);

        var skipped = flights.Where(f => f.Outcome == EdgeFlightOutcome.RoutedAround).ToList();
        Assert.Equal(47, skipped.Count);

        // None of them is counted, in either direction: not as an arrival and not
        // as a refusal.
        foreach (var flight in skipped)
        {
            Assert.False(flight.Measured);
            Assert.False(flight.Flown);
        }

        // The premise, on one of them: the search really does prefer a longer way
        // round, so there is no route over which this arc could be steered.
        var graph = world().Nav!;
        var edge = graph.Edges[skipped[0].Edge];

        var follower = new PathFollower(graph);
        var state = new BotPathState();
        var query = EdgeFlights.QueryFor(edge.Type, Team.Red);

        var midFrom = NavGraph.ColumnWorldX((graph.Nodes[edge.From].X0 + graph.Nodes[edge.From].X1) / 2);
        var midTo = NavGraph.ColumnWorldX((graph.Nodes[edge.To].X0 + graph.Nodes[edge.To].X1) / 2);

        Assert.True(follower.SetGoal(state, midTo, graph.StandY(edge.To)));
        Assert.True(follower.Plan(state, midFrom, graph.StandY(edge.From), tick: 0, in query));

        output.WriteLine($"e{skipped[0].Edge} {edge.From}->{edge.To} planned as {string.Join("->", state.Path)}");

        Assert.True(
            state.Path.Count > 2,
            $"edge {skipped[0].Edge} was reported RoutedAround but the plan is the single hop");

        // And the arm never fires on the way, which is what the skip is protecting
        // the number from: this arrival belongs to the walk, not to the rocket.
        var driven = EdgeFlights.Fly(world(), skipped[0].Edge);
        Assert.Equal(EdgeFlightOutcome.RoutedAround, driven.Outcome);
    }

    /// <summary>
    /// The two premises that never fire on today's caches, recorded so that
    /// "never seen" is a measurement rather than an assumption.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A branch nothing reaches is a claim nothing checks</b>, and this repo
    /// has been bitten twice in a week by exactly that shape. Both of these are
    /// the harness refusing to guess rather than dead code:
    /// <list type="bullet">
    /// <item>
    /// <see cref="EdgeFlightOutcome.StartUnresolved"/> cannot fire for a midpoint
    /// start — see <see cref="EveryArcEdgeResolvesFromItsOwnNodeAndFromItsTakeoffColumn"/>
    /// — and exists so that changing the start position cannot silently measure a
    /// different edge.
    /// </item>
    /// <item>
    /// <see cref="EdgeFlightOutcome.Shadowed"/> reads <b>zero across all 22,528
    /// arc edges</b> of the shipped and sticky caches, on both teams: no node pair
    /// carries a strictly cheaper routable edge of another kind. ⚠️ That is not the
    /// same as saying parallel edges are harmless — 11,615 pairs carry more than
    /// one kind, and picking among them by <em>index order</em> rather than by cost
    /// was a live bug for eight phases. Cost order simply happens to agree with the
    /// arc today.
    /// </item>
    /// </list>
    /// </remarks>
    [Fact]
    public void NoShippedArcIsShadowedByACheaperParallelEdge()
    {
        ReferenceTree.RequireNavCache();

        var arcs = 0;
        var shadowed = 0;

        foreach (var (map, area) in NavCache.List(ReferenceTree.NavCacheDirectory))
        {
            var graph = NavCache.Load(ReferenceTree.NavCacheDirectory, map, area);
            var follower = new PathFollower(graph);

            for (var i = 0; i < graph.EdgeCount; i++)
            {
                ref readonly var edge = ref graph.Edges[i];

                if (edge.Type is not (NavEdgeType.RocketJump or NavEdgeType.DoubleJump))
                    continue;

                foreach (var team in (ReadOnlySpan<Team>)[Team.Red, Team.Blue])
                {
                    var query = EdgeFlights.QueryFor(edge.Type, team);

                    // Gated out for this team: the search refuses it outright, so
                    // there is nothing for a cheaper edge to shadow.
                    if (!NavGraph.EdgeRoutable(in edge, in query))
                        continue;

                    arcs++;

                    // The follower's own answer, not a second implementation of
                    // the tie-break — that is what TryResolveHop is for.
                    var state = new BotPathState { PlanQuery = query };

                    if (!follower.TryResolveHop(state, edge.From, edge.To, out var steered) || steered != edge)
                        shadowed++;
                }
            }
        }

        output.WriteLine($"{arcs} routable arcs across both teams; {shadowed} shadowed");

        Assert.Equal(36_585, arcs);
        Assert.Equal(0, shadowed);
    }
}
