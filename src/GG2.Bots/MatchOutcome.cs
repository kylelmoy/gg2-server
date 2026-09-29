
namespace GG2.Bots;

/// <summary>Why a match stopped.</summary>
public enum MatchOutcome
{
    /// <summary>Still running.</summary>
    Running,

    /// <summary>A team won, or the round was drawn — see <see cref="MatchResult.Winner"/>.</summary>
    Decided,

    /// <summary>It hit <see cref="HeadlessMatchOptions.MaxTicks"/> undecided.</summary>
    Exhausted,
}
