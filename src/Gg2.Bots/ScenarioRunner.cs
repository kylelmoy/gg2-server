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
/// Hp removed from the enemy team over the run — its characters and its
/// generator. A character that died counts for everything it had, so a kill is
/// not a smaller number than a near-miss.
/// </param>
/// <param name="Survived">Was the subject still alive at the end.</param>
/// <param name="GoalDistance">How far from the goal it finished. Travel mode only.</param>
/// <param name="Acquired">
/// Did the subject ever hold a target. ⚠️ <b>Latched across the run</b>, because a
/// target clears when it dies: read once at the end, a bot that fought and won is
/// indistinguishable from one that never saw anything.
/// </param>
/// <param name="Planned">
/// Travel mode: did the destination resolve to a route at all. ⚠️ False is a
/// distinct failure from "did not arrive" and must not be reported as one — the
/// bot never took a step, so nothing about the follower was exercised. Only
/// meaningful when <paramref name="Steered"/>.
/// </param>
/// <param name="Steered">
/// Travel mode: was the subject actually handed the destination, i.e. does it
/// implement <see cref="IScenarioSubject"/>.
/// </param>
public readonly record struct ScenarioResult(
    int Ticks,
    bool Arrived,
    double Moved,
    double Damage,
    bool Survived,
    double GoalDistance,
    bool Acquired = false,
    bool Planned = true,
    bool Steered = false)
{
    /// <summary>Whether this run satisfies what its scenario asked for.</summary>
    /// <remarks>
    /// ⚠️ <see cref="ScenarioExpectations.Acquired"/> IS checked now — it was
    /// carried unevaluated from the day the suite was ported, because no
    /// <see cref="IInputSource"/> reported it. <see cref="IScenarioSubject"/> is
    /// what closed that, and a subject that cannot answer reports
    /// <see cref="Acquired"/> false rather than passing quietly.
    /// <para>
    /// ⚠️ <b>The order matters.</b> A travel scenario that never planned is
    /// reported as such before anything else, because every later measurement is
    /// then a measurement of a bot standing still and describing it as "did not
    /// arrive" points at the follower for a failure in the map, the team or the
    /// coordinate.
    /// </para>
    /// </remarks>
    public bool Satisfies(in Scenario scenario, out string? why)
    {
        ArgumentNullException.ThrowIfNull(scenario);

        why = null;
        var e = scenario.Expect;

        if (Steered && !Planned)
            why = "no route to the goal — the bot never took a step";
        else if (scenario.Mode == ScenarioMode.Travel && !Arrived)
        {
            // ⚠️ Arrival is not an expectation and must not be one. Scenario.Budget
            // already says a leg that takes longer is a failure "even if it
            // eventually arrives", so a travel scenario with no `expect` at all is
            // still asserting something — and reading this off the expectations
            // instead let three legs that ended 737, 958 and 2,253px short report
            // themselves satisfied. Measured, on the first run of the suite.
            //
            // ⚠️ And the message says whether the subject was ever told where to
            // go, because the two failures point in opposite directions: an
            // unsteered subject says nothing at all about the follower.
            why = $"did not arrive: {GoalDistance:0.#}px short after {Ticks} ticks"
                + (Steered ? string.Empty : " (the subject takes no destination)");
        }
        else if (e.Acquired == true && !Acquired)
            why = "never acquired a target";
        else if (e.MinDamage is { } min && Damage < min)
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
/// <b>There is a bot behind this now</b> (<see cref="ScriptedBot"/>), and every
/// one of these legs was written because a bot did something wrong that nobody
/// had noticed. The runner existing before the behaviour is what made item 4
/// measurable from its first commit rather than felt.
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

        // ⚠️ The enemy GENERATOR counts as enemy hp, and without this line
        // `soldier-shells-generator` measures nothing at all: it places no
        // enemy characters, so the damage figure is over an empty list and a
        // MinDamage of 50 can never be met however well the bot shoots. The
        // scenario exists to catch a silent regression; unmeasurable is the same
        // failure wearing the harness's clothes.
        var generatorStartHp = Generators.Of(ref world.State, enemyTeam).Hp;

        var startX = world.State.Characters[subjectSlot].X;
        var startY = world.State.Characters[subjectSlot].Y;

        var goal = scenario.Mode == ScenarioMode.Travel
            ? world.Snap(scenario.To!.Value.X, scenario.To.Value.Y)
            : default;

        // ⚠️ The runner is what knows the goal — it snapped it — so it is what
        // hands it over. A subject that is never told where to go stands still
        // and the run reports "did not arrive" about a bot that was never asked,
        // which reads as a broken follower and is a broken harness.
        //
        // ⚠️ Not implementing IScenarioSubject is a legitimate answer and must
        // not be an error. That was tried, and it broke the synthetic travel
        // scenario that exists precisely to exercise this runner without a bot —
        // a source that holds one key needs no destination and cannot be misled
        // by the absence of one. So the fact is REPORTED (`Steered`) rather than
        // enforced, and the suite asserts it about its own subjects, where the
        // silent-standstill mistake is actually reachable.
        //
        // ⚠️ And a Hold scenario is told NOTHING, whatever it implements:
        // bot-scenarios.js's reasoning is that "a combat scenario that quietly
        // inherited a movement goal would measure the wrong thing and still pass".
        var probe = subject as IScenarioSubject;
        var steered = scenario.Mode == ScenarioMode.Travel && probe is not null;
        var planned = !steered || probe!.SetGoal(goal.GoalX, goal.GoalY);

        var limit = scenario.TickLimit;
        var arrived = false;
        var acquired = false;
        var ticks = 0;

        for (; ticks < limit; ticks++)
        {
            world.Step();

            // ⚠️ Sampled every tick, never read at the end: a target clears when
            // it dies. See IScenarioSubject.HasTarget.
            acquired |= probe?.HasTarget ?? false;

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

        damage += Math.Max(0, generatorStartHp - Generators.Of(ref world.State, enemyTeam).Hp);

        return new ScenarioResult(
            ticks,
            arrived,
            Distance(startX, startY, endX, endY),
            damage,
            alive,
            scenario.Mode == ScenarioMode.Travel ? Distance(endX, endY, goal.GoalX, goal.GoalY) : 0,
            acquired,
            planned,
            steered);
    }

    private static double Distance(double x0, double y0, double x1, double y1)
    {
        var dx = x1 - x0;
        var dy = y1 - y0;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }
}
