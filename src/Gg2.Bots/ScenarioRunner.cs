using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Bots;

/// <summary>What one scenario run measured.</summary>
/// <param name="Ticks">How long the run took — the arrival tick, or the whole limit.</param>
/// <param name="Arrived">
/// Travel mode only: did the subject reach the goal inside its budget. ⚠️ A leg
/// that arrives outside the budget is a failure, not a slow pass — "gets there
/// in the end" is exactly what a bot re-planning in a loop looks like.
/// </param>
/// <param name="Moved">Pixels between where the subject was placed and where it ended.</param>
/// <param name="Damage">
/// Hp removed from the enemy team over the run. A character that died counts
/// for everything it had, so a kill is not a smaller number than a near-miss.
/// </param>
/// <param name="Survived">Was the subject still alive at the end.</param>
/// <param name="GoalDistance">How far from the goal it finished. Travel mode only.</param>
public readonly record struct ScenarioResult(
    int Ticks,
    bool Arrived,
    double Moved,
    double Damage,
    bool Survived,
    double GoalDistance)
{
    /// <summary>Whether this run satisfies a scenario's expectations.</summary>
    /// <remarks>
    /// ⚠️ <see cref="ScenarioExpectations.Acquired"/> is deliberately not checked
    /// here. It is bot-internal state — did the policy ever pick a target — and
    /// no <see cref="IInputSource"/> in this port reports it yet. Silently
    /// passing an unchecked expectation is worse than not having one, so this
    /// reports what it could not judge rather than pretending.
    /// </remarks>
    public bool Satisfies(in ScenarioExpectations expect, out string? why)
    {
        why = null;
        var e = expect;

        if (e.MinDamage is { } min && Damage < min)
            why = $"dealt {Damage} damage, wanted at least {min}";
        else if (e.MaxDamage is { } max && Damage > max)
            why = $"dealt {Damage} damage, wanted at most {max}";
        else if (e.MaxTicks is { } ticks && Ticks > ticks)
            why = $"took {Ticks} ticks, wanted at most {ticks}";
        else if (e.MaxMoved is { } moved && Moved > moved)
            why = $"moved {Moved:0.#}px, wanted at most {moved}";

        return why is null;
    }
}

/// <summary>
/// Runs one <see cref="Scenario"/> against an <see cref="IInputSource"/> and
/// measures it.
/// </summary>
/// <remarks>
/// <c>gg2-agent/tools/botscenario.js</c>'s job, without a running game. The
/// runner is the part that must not be re-invented per scenario: place, step,
/// measure, stop — so that adding a scenario is an edit to
/// <see cref="Scenarios"/> and nothing else.
/// <para>
/// ⚠️ <b>There is no bot behaviour behind this yet</b> (phase 7 item 4), so the
/// suite's own scenarios cannot pass. That is deliberate ordering, not an
/// oversight: the runner is what makes item 4 measurable from its first commit
/// rather than felt, and every one of these legs was written because a bot did
/// something wrong that nobody had noticed.
/// </para>
/// </remarks>
public sealed class ScenarioRunner
{
    /// <summary>
    /// How close to the goal counts as arrived.
    /// </summary>
    /// <remarks>
    /// A node is up to a screen wide, so "reached the goal node" and "reached
    /// the goal point" are different questions; this asks the second, at a
    /// tolerance of one body width. ⚠️ Not measured against the reference
    /// runner, which reports arrival from its own follower's state rather than
    /// from a distance — so this is this port's definition and a scenario's
    /// quoted tick counts are only comparable to within it.
    /// </remarks>
    public const double ArrivalRadius = 24;

    /// <summary>
    /// Places everything, steps to the limit or to arrival, and reports.
    /// </summary>
    /// <param name="scenario">What to run.</param>
    /// <param name="world">
    /// A world already loaded on <see cref="Scenario.Map"/> — the caller owns it
    /// so a suite can load each map once, which in the reference is most of a
    /// scenario's cost.
    /// </param>
    /// <param name="subject">
    /// What drives the character under test. Null means it presses nothing,
    /// which is what a scenario measures against before there is a bot.
    /// </param>
    /// <param name="enemy">
    /// What drives a non-dummy enemy. ⚠️ A dummy is driven by nothing at all —
    /// in the reference it is an acquisition delay longer than the scenario and
    /// a locked goal, which are ordinary knobs; here "no source" is the same
    /// thing and needs no test-only code either.
    /// </param>
    public static ScenarioResult Run(
        Scenario scenario,
        ScenarioWorld world,
        IInputSource? subject,
        IInputSource? enemy = null)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(world);

        if (!string.Equals(world.Map.Name, scenario.Map, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"scenario '{scenario.Name}' wants {scenario.Map}, world is {world.Map.Name}",
                nameof(world));
        }

        // ⚠️ Snapped, and the snapped position is what is used. A raw
        // coordinate usually does not resolve to a node on its own — see
        // ScenarioWorld.Snap. With no nav graph the snap is the identity, which
        // is right for a scenario about physics rather than routing.
        var start = world.Snap(scenario.From.X, scenario.From.Y);
        var subjectSlot = world.Place(scenario.Class, scenario.Team, start.GoalX, start.GoalY);
        world.Drive(subjectSlot, subject);

        var enemyTeam = scenario.Team == Team.Red ? Team.Blue : Team.Red;
        var enemySlots = new int[scenario.Enemies.Count];
        var enemyStartHp = new double[scenario.Enemies.Count];

        for (var i = 0; i < scenario.Enemies.Count; i++)
        {
            var placement = world.Snap(scenario.Enemies[i].At.X, scenario.Enemies[i].At.Y);
            var slot = world.Place(scenario.Enemies[i].Class, enemyTeam, placement.GoalX, placement.GoalY);

            enemySlots[i] = slot;
            enemyStartHp[i] = world.State.Characters[slot].Hp;

            if (!scenario.Enemies[i].Dummy)
                world.Drive(slot, enemy);
        }

        var startX = world.State.Characters[subjectSlot].X;
        var startY = world.State.Characters[subjectSlot].Y;

        var goal = scenario.Mode == ScenarioMode.Travel
            ? world.Snap(scenario.To!.Value.X, scenario.To.Value.Y)
            : default;

        var limit = scenario.TickLimit;
        var arrived = false;
        var ticks = 0;

        for (; ticks < limit; ticks++)
        {
            world.Step();

            if (scenario.Mode != ScenarioMode.Travel || !world.State.Alive[subjectSlot])
                continue;

            if (Distance(world.State.Characters[subjectSlot].X, world.State.Characters[subjectSlot].Y,
                    goal.GoalX, goal.GoalY) <= ArrivalRadius)
            {
                arrived = true;
                ticks++;
                break;
            }
        }

        var alive = world.State.Alive[subjectSlot];
        var endX = alive ? world.State.Characters[subjectSlot].X : startX;
        var endY = alive ? world.State.Characters[subjectSlot].Y : startY;

        double damage = 0;
        for (var i = 0; i < enemySlots.Length; i++)
        {
            // A dead enemy lost everything it had, so a kill is not a smaller
            // number than a near-miss.
            //
            // ⚠️ The Alive guard is defensive rather than load-bearing today:
            // WorldState.Clear writes `Characters[slot] = default`, so a dead
            // slot reads 0 hp anyway. It is written out because that is an
            // implementation detail of Clear and this arithmetic should not
            // depend on it — checked by mutation, and the mutation does not
            // fail, which is exactly why it is worth saying so here.
            var now = world.State.Alive[enemySlots[i]] ? world.State.Characters[enemySlots[i]].Hp : 0;
            damage += Math.Max(0, enemyStartHp[i] - now);
        }

        return new ScenarioResult(
            ticks,
            arrived,
            Distance(startX, startY, endX, endY),
            damage,
            alive,
            scenario.Mode == ScenarioMode.Travel ? Distance(endX, endY, goal.GoalX, goal.GoalY) : 0);
    }

    private static double Distance(double x0, double y0, double x1, double y1)
    {
        var dx = x1 - x0;
        var dy = y1 - y0;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }
}
