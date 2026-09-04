namespace Gg2.Bots;

/// <summary>
/// A micro-controller that owns a slot's keys but leaves the macro layer to a
/// <see cref="ScriptedBot"/> underneath it.
/// </summary>
/// <remarks>
/// <b>This exists because `is ScriptedBot` is the server's way of asking "is
/// this slot a bot", and wrapping one silently answers no.</b> The live server
/// asks that question in four places — retargeting, removal (which frees the
/// bot's <c>BotOccupancy</c> reservations), the map-change hand-off, and the
/// control port's roster — and every one of them would skip a wrapped bot without
/// failing anything. The failure would be a bot that kept its route across a map
/// change, holding edges on a graph that no longer exists.
/// <para>
/// <b>Ask <see cref="ScriptedBot.Of"/>, never <c>is ScriptedBot</c>, anywhere a
/// slot's macro layer is wanted.</b> A future wrapper implements this interface
/// and every one of those sites keeps working; a future wrapper that does not is
/// invisible to them again.
/// </para>
/// </remarks>
public interface IWrapsScriptedBot
{
    /// <summary>The macro layer underneath.</summary>
    ScriptedBot Scripted { get; }
}
