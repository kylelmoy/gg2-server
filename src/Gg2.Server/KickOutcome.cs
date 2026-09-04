namespace Gg2.Server;

/// <summary>What <see cref="GameServer.KickPlayer"/> did.</summary>
/// <remarks>
/// Three outcomes rather than a bool, because "there is no such slot" and "that
/// slot is a bot" need different sentences in front of an operator: the second
/// one is a person who should be changing the bot count instead.
/// </remarks>
public enum KickOutcome
{
    /// <summary>The connection was sent a <c>KICK</c> and closed.</summary>
    Kicked,

    /// <summary>No roster entry at that index, or it was the reserved slot 0.</summary>
    NoSuchSlot,

    /// <summary>A bot, or a slot whose connection has already gone.</summary>
    NotAConnection,
}
