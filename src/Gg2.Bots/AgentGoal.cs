namespace Gg2.Bots;

/// <summary>
/// The macro goal an agent is being asked to pursue, which the observation's
/// goal block reports.
/// </summary>
/// <remarks>
/// <b><c>gg2-rl</c> phase 4's hybrid boundary</b>, asked for 2026-08-30: the
/// scripted layer emits a goal — reach a point, engage a target, hold position —
/// and the learned policy always drives keys and aim, so there is no handoff for
/// the policy to game. For that, the goal has to vary during training, which
/// means it has to be settable per slot.
/// <para>
/// ⚠️ <b><c>default</c> is exactly what the observation carried before this type
/// existed</b>, and that is the whole reason the ask was cheap. Kind 0, no
/// target, so <c>WriteGoal</c> takes the nearest-enemy path it always took. Every
/// existing checkpoint sees byte-identical observations, so
/// <see cref="ObservationWriter.LayoutVersion"/> does not move and nothing is
/// invalidated. <c>gg2-rl</c> asked for an uglier API over a silent one and this
/// is the ugly half: an opt-in struct with a meaningful zero rather than a
/// required argument.
/// </para>
/// <para>
/// ⚠️ <b>The goal block was reserved on day one for this.</b>
/// <c>ObservationWriter.WriteGoal</c>'s comment — "a macro goal the policy has
/// never seen cannot be introduced later without a layout bump and a retrain, and
/// a constant column costs four floats" — is what makes today a struct rather
/// than a retrain of everything. Worth knowing next time a column looks like
/// waste.
/// </para>
/// </remarks>
public readonly record struct AgentGoal
{
    /// <summary>
    /// Which goal, as an index into the observation's
    /// <see cref="ObservationWriter.GoalKinds"/> one-hot. <b>0 is "engage"</b>.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Range-checked where a goal is SET, not where it is written.</b>
    /// <c>OneHot</c> silently writes no column at all for an out-of-range index,
    /// which reaches the policy as "no goal" rather than as an error — so
    /// <see cref="Validate"/> is called by every setter, and the observation
    /// writer trusts what it is handed.
    /// </remarks>
    public int Kind { get; init; }

    /// <summary>
    /// Whether <see cref="TargetX"/> and <see cref="TargetY"/> mean anything.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A flag rather than a sentinel coordinate.</b> (0, 0) is a real point
    /// on a map — the top-left corner — and NaN would make the "is it set"
    /// question depend on floating-point comparison rules at every reader. The
    /// same argument <c>DuelSpec</c> made for keeping <c>RedTier</c> and
    /// <c>RedRolloutOpponent</c> apart: two small values with no sentinel between
    /// them do not share a field.
    /// </remarks>
    public bool HasTarget { get; init; }

    /// <summary>The goal point's world x, when <see cref="HasTarget"/>.</summary>
    public double TargetX { get; init; }

    /// <summary>The goal point's world y, when <see cref="HasTarget"/>.</summary>
    public double TargetY { get; init; }

    /// <summary>
    /// The default: engage the nearest enemy, which is what a duel's goal block
    /// has always said.
    /// </summary>
    public static AgentGoal EngageNearest => default;

    /// <summary>A goal to reach a point, of the given kind.</summary>
    public static AgentGoal At(int kind, double x, double y) =>
        new() { Kind = kind, HasTarget = true, TargetX = x, TargetY = y };

    /// <summary>
    /// Throws unless this goal can be written into the layout.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A refusal, not a clamp.</b> Clamping an out-of-range kind to 0 would
    /// train the policy on "engage" while the trainer believed it had asked for
    /// something else — the failure that still trains, which is the one this
    /// project keeps paying for.
    /// </remarks>
    public void Validate()
    {
        if ((uint)Kind >= (uint)ObservationWriter.GoalKinds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Kind), Kind, $"a goal kind is 0..{ObservationWriter.GoalKinds - 1}");
        }

        if (HasTarget && (double.IsNaN(TargetX) || double.IsNaN(TargetY)))
            throw new ArgumentException("a goal target cannot be NaN", nameof(TargetX));
    }
}
