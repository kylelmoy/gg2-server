namespace GG2.Bots;

/// <summary>
/// Which of the shipped bot brains a server runs, named once so a flag and a
/// result table can mean the same thing.
/// </summary>
/// <remarks>
/// <b>The numbers are part of the contract</b>: <c>--bot-mode</c> accepts them as
/// well as the names, so a script can say <c>--bot-mode 2</c>. <b>Do not renumber
/// them</b> — 1 and 3 were the rollout-search and tiered arms, and reusing either
/// number would make an old script silently select something else. Both are
/// refused by name and by number with a message naming the replacement.
/// </remarks>
public enum BotMode
{
    /// <summary>
    /// The scripted layer alone, and the control every comparison is made against.
    /// </summary>
    /// <remarks>
    /// <b>A complete bot, not a degraded one.</b> It walks, fights and plays
    /// the objective; the policy mode only replaces its keys and aim inside the
    /// engagement window.
    /// </remarks>
    Scripted = 0,

    /// <summary>
    /// The scripted layer plus trained policy checkpoints. Needs <c>--policy</c>.
    /// </summary>
    Policy = 2,
}

/// <summary>What each <see cref="BotMode"/> needs.</summary>
public static class BotModes
{
    /// <summary>
    /// Whether this mode drives bots with a trained checkpoint, and therefore
    /// needs one loaded.
    /// </summary>
    /// <remarks>
    /// <b>Used in both directions by the host</b>: a mode that needs a policy
    /// and was given none is refused, and a policy given to a mode that cannot
    /// use it is refused too. The second is the one worth having — a checkpoint
    /// loaded, reported and never consulted is exactly the shape of claim this
    /// repo keeps catching.
    /// </remarks>
    public static bool UsesPolicy(this BotMode mode) => mode is BotMode.Policy;

    /// <summary>
    /// The refusal for a mode name or number that no longer exists, or null when
    /// <paramref name="value"/> is not one of them.
    /// </summary>
    /// <remarks>
    /// Shared by <c>--bot-mode</c> and <c>[Bots] Mode</c> so a config file left
    /// over from a server that ran search or tiered says what to change rather
    /// than listing the valid names alone.
    /// </remarks>
    public static string? Removed(string value) => value.Trim().ToLowerInvariant() switch
    {
        "search" or "hybrid-search" or "1" or "tiered" or "3" =>
            $"bot mode '{value.Trim()}' has been removed: the rollout search and the tiered "
            + "mode are gone and the policy nets replace them. Use policy (2), or scripted (0).",
        _ => null,
    };
}
