namespace GG2.Server.Plugins;

/// <summary>What a duel human has asked of their bot with <c>/difficulty</c>.</summary>
public enum DuelDifficultyMode
{
    /// <summary>The ladder's rating for their class, or the server's Difficulty with the ladder off.</summary>
    Auto,

    /// <summary>One level for every class, and no result moves a rating.</summary>
    Fixed,

    /// <summary>No bot: the human has their cell to themselves.</summary>
    Off,
}

/// <summary>
/// A duel human's <c>/difficulty</c> setting. <c>docs/DUEL.md</c>, "Choosing a
/// level".
/// </summary>
/// <param name="Mode">Auto, a fixed level, or no bot.</param>
/// <param name="Level">
/// For <see cref="DuelDifficultyMode.Fixed"/>, the level, 0-10. Reported by the
/// host for <see cref="DuelDifficultyMode.Auto"/> as the level the bot plays at
/// now, or -1 when the ladder is off; ignored when set.
/// </param>
public readonly record struct DuelDifficulty(DuelDifficultyMode Mode, int Level = -1)
{
    /// <summary>The default: the ladder.</summary>
    public static DuelDifficulty Auto => new(DuelDifficultyMode.Auto);
}
