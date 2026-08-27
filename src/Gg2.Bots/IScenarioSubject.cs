namespace Gg2.Bots;

/// <summary>
/// What a <see cref="ScenarioRunner"/> needs from the thing it is measuring,
/// beyond the keys an <see cref="Gg2.Simulation.IInputSource"/> already gives it.
/// </summary>
/// <remarks>
/// Two things, and both of them are the difference between a scenario that
/// measures something and one that quietly measures nothing:
/// <list type="number">
/// <item>
/// ⚠️ <b>A travel scenario has to be able to hand over its destination.</b> The
/// runner is what knows the goal — it is the thing that snapped it — so a
/// subject that cannot be told where to go stands still and the scenario
/// reports "did not arrive" about a bot that was never asked to. That is the
/// worst kind of red: it looks like the movement layer and it is the harness.
/// </item>
/// <item>
/// ⚠️ <b>Whether a target was ever acquired is bot-internal and nothing on the
/// wire carries it.</b> <c>ScenarioExpectations.Acquired</c> was carried
/// unevaluated from the day the suite was ported precisely because of that, and
/// silently passing an unchecked expectation is worse than not having one.
/// </item>
/// </list>
/// <para>
/// It is deliberately separate from <c>IInputSource</c>. A socket is an input
/// source and cannot be told where to walk; only something with a policy inside
/// it can. Folding these onto that interface would put two members on every
/// network player that can never mean anything.
/// </para>
/// <para>
/// ⚠️ <b>NOT implementing it is a legitimate answer, and the runner must not
/// treat it as an error.</b> That was tried and it broke the synthetic travel
/// scenario that exists to exercise the runner without a bot: a source holding
/// one key over flat ground needs no destination and cannot be misled by the
/// absence of one. <c>ScenarioResult.Steered</c> reports the fact instead, and
/// <c>ScenarioSuiteTests</c> asserts it about its own subjects — which is where
/// the silent-standstill mistake is actually reachable.
/// </para>
/// </remarks>
public interface IScenarioSubject
{
    /// <summary>
    /// Go here. False means the destination did not resolve to a route.
    /// </summary>
    /// <remarks>
    /// ⚠️ The runner treats false as a <em>failed run</em> rather than as an
    /// exception, because "there is no route from here to there for this team"
    /// is a real answer about the map and one of these scenarios exists to
    /// produce it.
    /// </remarks>
    bool SetGoal(double worldX, double worldY);

    /// <summary>
    /// Is a target held <em>right now</em>.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Sampled every tick and latched by the runner</b>, never read once at
    /// the end. A target clears when it dies, so by the last tick a bot that
    /// fought and won looks exactly like one that never saw anything —
    /// <c>ScenarioExpectations.Acquired</c> says so, and this is the property
    /// that makes it true.
    /// </remarks>
    bool HasTarget { get; }
}
