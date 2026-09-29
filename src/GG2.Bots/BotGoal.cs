
namespace GG2.Bots;

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
/// <param name="Offset">
/// How far <see cref="X"/> was moved sideways off the objective's own position —
/// the bot's spread plus, for a defender, <c>BOT_DEFEND_RADIUS</c>. Zero for a spot,
/// a chase and anything that has to be touched. ⚠️ Carried here rather than
/// re-supplied by the caller because <see cref="BotObjective.TryResolve"/> resolves
/// <c>X - Offset</c> first and lets the offset in only on the same level: a caller
/// that knew the spread but not the defend radius handed it a base 220px off the
/// flag, which on <c>ctf_2dfort</c> falls off the intel's platform to the floor by
/// the spawn room.
/// </param>
public readonly record struct BotGoal(
    bool Found, double X, double Y, bool IsSpot, bool MustTouch, double Offset = 0)
{
    public static BotGoal None => default;
}
