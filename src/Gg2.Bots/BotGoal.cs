
namespace Gg2.Bots;

/// <summary>What <see cref="BotObjective"/> decided, ready to be issued as a goal.</summary>
/// <param name="Found">Whether the mode cascade had anything to offer at all.</param>
/// <param name="X">Where to walk, in world coordinates.</param>
/// <param name="Y"><inheritdoc cref="X"/></param>
/// <param name="IsSpot">
/// Whether this came out of <see cref="BotGoalSpot"/> rather than being the
/// objective itself. ⚠️ The caller has to know, because a spot is chosen without
/// checking reachability and one that turns out to be in another component has to
/// be given up on rather than retried for ever.
/// </param>
/// <param name="MustTouch">
/// Whether this is a goal the bot has to physically occupy rather than cover — the
/// intel, its home base, or a contested point. ⚠️ Recorded because it is what
/// forbids both the per-class standoff and the lateral spread.
/// </param>
public readonly record struct BotGoal(bool Found, double X, double Y, bool IsSpot, bool MustTouch)
{
    public static BotGoal None => default;
}
