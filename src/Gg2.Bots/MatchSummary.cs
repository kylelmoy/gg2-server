namespace Gg2.Bots;

/// <summary>What a batch of matches came to.</summary>
public readonly record struct MatchSummary(
    int Matches,
    int RedWins,
    int BlueWins,
    int Draws,
    int Exhausted,
    long TotalTicks)
{
    /// <summary>Ticks of match played per match, averaged.</summary>
    public double MeanTicks => Matches == 0 ? 0 : (double)TotalTicks / Matches;

    public override string ToString() =>
        $"{Matches} matches: red {RedWins}, blue {BlueWins}, drawn {Draws}, "
        + $"unfinished {Exhausted}, mean {MeanTicks:0} ticks";
}
