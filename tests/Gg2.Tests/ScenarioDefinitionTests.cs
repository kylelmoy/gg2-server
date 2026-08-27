using Gg2.Bots;
using Gg2.Protocol;

namespace Gg2.Tests;

/// <summary>
/// The fourteen ported scenario definitions, checked against the maps and nav
/// graphs they name.
/// </summary>
/// <remarks>
/// ⚠️ <b>The scenarios themselves cannot pass yet and these tests do not pretend
/// otherwise.</b> Every one runs a bot and there is no bot behaviour in this
/// port (phase 7 item 4). What <em>is</em> checkable now is the data: that every
/// coordinate resolves to a real nav node on its real map, which is precisely
/// the failure the reference's runner reports as VOID — "a raw objective
/// coordinate usually does NOT resolve to a node on its own", and a scenario
/// whose start point is in the void looks exactly like a bot that will not move.
/// <para>
/// That check is worth having on its own account: it is a second, independent
/// exercise of the nav reader against real geometry, using coordinates that were
/// chosen by asking the reference's graph. If this port's reader disagreed with
/// that graph about where the nodes are, these would be the tests that noticed.
/// </para>
/// </remarks>
public sealed class ScenarioDefinitionTests
{
    private static NavGraph Graph(string map, int area)
    {
        ReferenceTree.RequireNavCache();
        return NavCache.Load(ReferenceTree.NavCacheDirectory, map, area);
    }

    /// <summary>All fourteen survived the port.</summary>
    [Fact]
    public void TheSuiteIsTheFourteenScenariosItWasPortedFrom()
    {
        Assert.Equal(14, Scenarios.All.Count);
        Assert.Equal(14, Scenarios.All.Select(s => s.Name).Distinct().Count());

        // Every one says what a failure would mean. A scenario without that is a
        // red test nobody can act on, which is the state this port would drift
        // into if `about` were optional.
        foreach (var scenario in Scenarios.All)
            Assert.True(scenario.About.Length > 80, $"{scenario.Name} has no usable `about`");
    }

    /// <summary>
    /// A scenario is a travel or a hold, never both and never neither.
    /// </summary>
    /// <remarks>
    /// ⚠️ The reference makes the choice required rather than defaulted because
    /// "a combat scenario that quietly inherited a movement goal would measure
    /// the wrong thing and still pass". Here the constructor enforces it, so the
    /// mistake is unrepresentable rather than merely discouraged.
    /// </remarks>
    [Fact]
    public void AScenarioIsATravelOrAHoldAndNeverBoth()
    {
        foreach (var scenario in Scenarios.All)
        {
            if (scenario.Mode == ScenarioMode.Travel)
            {
                Assert.NotNull(scenario.To);
                Assert.Equal(0, scenario.Hold);
                Assert.True(scenario.Budget > 0, $"{scenario.Name} travels with no budget");
            }
            else
            {
                Assert.Null(scenario.To);
                Assert.True(scenario.Hold > 0, $"{scenario.Name} holds for no ticks");
            }

            Assert.Equal(scenario.TickLimit, Math.Max(scenario.Budget, scenario.Hold));
        }

        // The constructor refuses both, and neither.
        Assert.Throws<ArgumentException>(() => new Scenario(
            "both", "koth_valley", new string('x', 100), PlayerClass.Soldier, Team.Red,
            new ScenarioPoint(0, 0), to: new ScenarioPoint(1, 1), hold: 10));

        Assert.Throws<ArgumentException>(() => new Scenario(
            "neither", "koth_valley", new string('x', 100), PlayerClass.Soldier, Team.Red,
            new ScenarioPoint(0, 0)));
    }

    /// <summary>
    /// Every scenario coordinate snaps to a real node on its own map.
    /// </summary>
    /// <remarks>
    /// The VOID check, and the sharpest thing available about the ported data.
    /// ⚠️ A start point with nothing under it inside <c>NAV_MAX_FALL</c> gives a
    /// bot no node to plan from, so it never moves — which reads as a broken bot
    /// and is a broken scenario. The reference's own header records
    /// <c>[1200, 900]</c> on koth_valley as exactly that: it "looked like the
    /// valley floor and has no node under it at all".
    /// <para>
    /// It also exercises this port's nav reader against coordinates chosen by
    /// asking the <em>reference's</em> graph — so a disagreement about where the
    /// nodes are fails here.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryScenarioCoordinateResolvesToANode()
    {
        foreach (var scenario in Scenarios.All)
        {
            var graph = Graph(scenario.Map, scenario.Area);

            var from = graph.SnapObjective(scenario.From.X, scenario.From.Y);
            Assert.True(
                from.Node >= 0,
                $"{scenario.Name}: `from` ({scenario.From.X}, {scenario.From.Y}) is in the void on "
                + $"{scenario.Map} — nothing under it within NAV_MAX_FALL");

            if (scenario.To is { } to)
            {
                var goal = graph.SnapObjective(to.X, to.Y);
                Assert.True(
                    goal.Node >= 0,
                    $"{scenario.Name}: `to` ({to.X}, {to.Y}) is in the void on {scenario.Map}");
            }

            foreach (var enemy in scenario.Enemies)
            {
                var at = graph.SnapObjective(enemy.At.X, enemy.At.Y);
                Assert.True(
                    at.Node >= 0,
                    $"{scenario.Name}: enemy at ({enemy.At.X}, {enemy.At.Y}) is in the void");
            }
        }
    }

    /// <summary>
    /// A travel scenario's goal is somewhere else, and the two ends are
    /// connected for the team that is walking.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Reachability is a per-team, per-carrier query, not a property of the
    /// graph</b> — your own team gate is open unless you are carrying intel out
    /// of it, so a route that exists for red may not exist for blue. Every leg
    /// here is an empty-handed walk, which is the case each scenario was written
    /// for.
    /// <para>
    /// This is the check that would have caught <c>truefort-blue-spawn-exit-west</c>'s
    /// pit — the one whose only exit is behind a blue team gate — as a
    /// <em>graph</em> problem rather than as a bot standing still for 1,484
    /// stuck fires.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryTravelScenariosGoalIsReachableByItsTeam()
    {
        var checkedAny = false;

        foreach (var scenario in Scenarios.All)
        {
            if (scenario.To is not { } to)
                continue;

            var graph = Graph(scenario.Map, scenario.Area);
            var from = graph.SnapObjective(scenario.From.X, scenario.From.Y).Node;
            var goal = graph.SnapObjective(to.X, to.Y).Node;

            Assert.NotEqual(from, goal); // otherwise the leg is already over

            Assert.True(
                Reaches(graph, from, goal, scenario.Team),
                $"{scenario.Name}: node {goal} is unreachable from {from} for {scenario.Team} on "
                + $"{scenario.Map} — the scenario asks for a route the graph does not have");

            checkedAny = true;
        }

        Assert.True(checkedAny, "no travel scenario was checked");
    }

    /// <summary>
    /// A breadth-first walk of the graph under one traveller's gate rules —
    /// <c>navgraph.js</c>'s <c>reach</c>.
    /// </summary>
    private static bool Reaches(NavGraph graph, int start, int target, Team team)
    {
        if (start < 0 || target < 0)
            return false;

        var seen = new bool[graph.NodeCount];
        var queue = new Queue<int>();

        seen[start] = true;
        queue.Enqueue(start);

        while (queue.Count > 0)
        {
            var at = queue.Dequeue();
            if (at == target)
                return true;

            foreach (var edge in graph.EdgesFrom(at))
            {
                if (seen[edge.To])
                    continue;

                // Empty-handed, and past setup: every ported leg is an ordinary
                // walk. A carrier's return leg is a different query and no
                // scenario asks it yet.
                if (!graph.EdgePassable(edge, team, hasIntel: false, setupGatesClosed: false))
                    continue;

                seen[edge.To] = true;
                queue.Enqueue(edge.To);
            }
        }

        return false;
    }
}
