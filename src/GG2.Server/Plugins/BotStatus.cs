namespace GG2.Server.Plugins;

/// <summary>
/// What <c>/bots</c> is allowed to do and what the bot population is doing
/// right now.
/// </summary>
/// <param name="CommandAllowed">
/// <c>[Bots] SuspendMinutes</c> is above zero, so the command exists on this
/// server at all.
/// </param>
/// <param name="SuspendMinutes">How long a fresh suspension lasts.</param>
/// <param name="Managed">
/// The population manager is running. False means bots are not this server's
/// doing and suspending them would report success while changing nothing.
/// </param>
/// <param name="Count">Bots on the roster at this moment.</param>
/// <param name="SecondsLeft">
/// What is left of a running suspension, rounded up. Zero when none is running.
/// </param>
/// <remarks>
/// <b>One snapshot rather than five accessors</b>, because every caller asks all
/// of it at once and answering a player needs the combination: "no bots are here"
/// and "bots are suspended" and "this server does not run bots" are three
/// different replies to the same word, and a command that could only see one
/// field at a time would give the wrong one.
/// </remarks>
public readonly record struct BotStatus(
    bool CommandAllowed,
    int SuspendMinutes,
    bool Managed,
    int Count,
    int SecondsLeft)
{
    /// <summary>Whether a suspension is running.</summary>
    public bool Suspended => SecondsLeft > 0;
}
