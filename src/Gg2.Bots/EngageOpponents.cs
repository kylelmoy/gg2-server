
namespace Gg2.Bots;

/// <summary>What decides when a micro-controller takes the wheel.</summary>
/// <summary>
/// Which opponents are worth handing the wheel to a micro-controller for.
/// </summary>
/// <remarks>
/// ⛔ <b>The default here is <see cref="Anyone"/> and the SERVER's default is
/// <see cref="HumansOnly"/>, and that difference is deliberate.</b> A headless
/// bot-vs-bot match has no humans in it at all, so a library default of
/// <c>HumansOnly</c> would make every arm measurement silently measure the
/// all-scripted control — <c>HeadlessMatch</c>, the scenarios and
/// <c>tools/bench</c> would all report the controller doing nothing, and the
/// number would look like a result. The live server has the opposite need, so it
/// opts in; see <c>ServerOptions.BotEngagesBotsToo</c>.
/// </remarks>
public enum EngageOpponents
{
    /// <summary>
    /// Any enemy in range engages the controller. Every recorded arm measurement
    /// was taken this way, because it is the only setting under which a
    /// bot-vs-bot match exercises the controller at all.
    /// </summary>
    Anyone = 0,

    /// <summary>
    /// Only a human enemy engages the controller; a bot-vs-bot encounter stays on
    /// the scripted layer.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>What this buys is cost, not skill.</b> The expensive controllers
    /// exist to be good against people; spending a 4,800 µs search decision on
    /// two bots shooting at each other in an empty corner of the map is the bulk
    /// of what a populated server pays for. ⛔ <b>It does not make the bots
    /// better</b>, and a measurement taken under it against bots is measuring the
    /// scripted layer whatever the mode says.
    /// </remarks>
    HumansOnly = 1,
}
