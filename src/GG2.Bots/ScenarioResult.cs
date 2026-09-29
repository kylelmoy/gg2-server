
namespace GG2.Bots;

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
