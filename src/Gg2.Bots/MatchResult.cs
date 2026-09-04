using Gg2.Protocol;

namespace Gg2.Bots;

/// <summary>What a finished match produced.</summary>
/// <remarks>
/// ⚠️ <b><see cref="Winner"/> is only meaningful when <see cref="Outcome"/> is
/// <see cref="MatchOutcome.Decided"/></b>, because <c>Team.Red</c> is 0 — a
/// defaulted result would say red won. The same reason <c>WorldState</c> keeps
/// <c>RoundDecided</c> as a separate flag from <c>Winner</c>. A draw is
/// <c>Team.Spectator</c>, which is how the wire spells it.
/// </remarks>
public readonly record struct MatchResult(
    MatchOutcome Outcome,
    Team Winner,
    int RedCaps,
    int BlueCaps,
    int Ticks,
    ulong Digest)
{
    /// <summary>
    /// ⚠️ <b><see cref="Digest"/> is what makes two results comparable at all.</b>
    /// </summary>
    /// <remarks>
    /// The scoreboard is a very coarse observable: over a few hundred ticks
    /// <em>every</em> match on a large map is
    /// <c>Exhausted, 0-0, MaxTicks</c>, whatever its seed — measured, and it is
    /// what made the first version of these tests both pass and fail for the
    /// wrong reasons. Worse, it is coarse in the direction that matters: two
    /// runs could put every character somewhere different and still tie 0-0, so
    /// a determinism check on the scoreboard alone would miss exactly the
    /// divergence it exists to catch. The digest covers where everybody is.
    /// </remarks>
    public bool SameOutcomeAs(MatchResult other) =>
        Outcome == other.Outcome
        && Winner == other.Winner
        && RedCaps == other.RedCaps
        && BlueCaps == other.BlueCaps;
}
