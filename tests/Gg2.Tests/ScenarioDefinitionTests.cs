using Gg2.Bots;
using Gg2.Protocol;

namespace Gg2.Tests;

/// <summary>
/// The fourteen ported scenario definitions, checked against the maps and nav
/// graphs they name.
/// </summary>
/// <remarks>
/// <b>What these gate is the DATA, not the behaviour.</b> Running the scenarios
/// is <c>ScenarioSuiteTests</c>'s job; what is checked here is that every
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
    /// The choice is required rather than defaulted because
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
    /// A start point with nothing under it inside <c>NAV_MAX_FALL</c> gives a
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
    /// A travel scenario's two ends are different nodes.
    /// </summary>
    /// <remarks>
    /// The rest of this claim — that the two ends are actually <em>connected</em>
    /// for the team walking — moved to
    /// <c>NavSearchFixtureTests.EveryTravelScenarioHasARouteEvenForTheLeastCapableClass</c>
    /// once there was a real search to ask. It is a better question than the
    /// breadth-first walk that stood here: a BFS answers "is there any way at
    /// all" and ignores cost, so it cannot see a leg whose only route runs over
    /// an arc the asker cannot fly. Two copies of a reachability check, one of
    /// them weaker, is worse than one.
    /// </remarks>
    [Fact]
    public void ATravelScenarioGoesSomewhere()
    {
        var checkedAny = false;

        foreach (var scenario in Scenarios.All)
        {
            if (scenario.To is not { } to)
                continue;

            var graph = Graph(scenario.Map, scenario.Area);
            var from = graph.SnapObjective(scenario.From.X, scenario.From.Y).Node;
            var goal = graph.SnapObjective(to.X, to.Y).Node;

            Assert.NotEqual(from, goal);
            checkedAny = true;
        }

        Assert.True(checkedAny, "no travel scenario was checked");
    }
}
