using GG2.Protocol;

namespace GG2.Bots;

/// <summary>
/// The one difficulty scale every bot plays on: a continuous level from 0 to 10.
/// The server's <c>[Bots] Difficulty</c> tiers 1-5 are five points on it
/// (<see cref="OfTier"/>), and the duel ladder and auto difficulty move a rating
/// along it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The table</b> is three columns, interpolated between whole levels: the
/// skill under <see cref="BotSkill.For"/>, how much like a person the bot plays
/// (<see cref="BotSkill.Human(double)"/>), and how far below the skill floor
/// (<see cref="BotSkill.Novice"/>).
/// </para>
/// <list type="table">
/// <item><term>0</term><description>Dummy: never fires (<see cref="BotSkill.NeverFires"/>), any level under 0.5.</description></item>
/// <item><term>1-2</term><description>Novice: the skill floor with the handicaps pushed past it.</description></item>
/// <item><term>3-9</term><description>The skill rises from 0.15 to 0.95 while the human aim fades out.</description></item>
/// <item><term>10</term><description>Haxton: the nets exactly as trained. Tier 5.</description></item>
/// </list>
/// <para>
/// <b>Fitted in a duel</b> (<c>BENCH_DUEL_LADDER</c>) for even steps in log-odds,
/// about 0.6 a level from 3 up and 0.75-0.92 below. In a match human aim costs
/// less than in a duel, so the same levels are not evenly apart in a match; the
/// tiers are placed on it by match measurement instead. <c>docs/TRAPS-BOTS.md</c>,
/// "The duel ladder" and "The tiers are levels". What would show it stale: a
/// retrained net, a change to <see cref="HumanizeOptions.Tracking9"/> or to
/// <see cref="BotSkill.For"/>.
/// </para>
/// </remarks>
public static class BotLevel
{
    /// <summary>The lowest level: Dummy.</summary>
    public const int Min = 0;

    /// <summary>The highest level: Haxton, tier 5.</summary>
    public const int Max = 10;

    private static ReadOnlySpan<double> Skills => [0.15, 0.15, 0.15, 0.15, 0.27, 0.40, 0.55, 0.70, 0.85, 0.95, 0.95];

    private static ReadOnlySpan<double> HumanShares => [1, 1, 1, 1, 0.85, 0.68, 0.50, 0.33, 0.20, 0.08, 0];

    private static ReadOnlySpan<double> Depths => [3, 2, 1, 0, 0, 0, 0, 0, 0, 0, 0];

    /// <summary>The level each server tier 1-5 plays (index 0 is tier 1).</summary>
    /// <remarks>
    /// <b>Placed by match measurement</b>, each tier on the whole level nearest the
    /// strength it had on its own table: <c>BENCH_TIERS=20 BENCH_HUMAN_AIM=1</c>,
    /// 420 paired matches an arm, opponent's death share against tier 5. The old
    /// tiers read -0.303 / -0.235 / -0.153 / -0.086; levels 3 / 5 / 6 / 7 read
    /// -0.312 / -0.206 / -0.139 / -0.093 (level 4 -0.272, 8 -0.046, 9 -0.015).
    /// In a match the top of the scale bunches, so tier 4 sits three levels below
    /// tier 5. <c>docs/TRAPS-BOTS.md</c>, "The tiers are levels".
    /// </remarks>
    private static ReadOnlySpan<int> TierLevels => [3, 5, 6, 7, 10];

    /// <summary>The skill a bot at <paramref name="level"/> plays, clamped to 0-10.</summary>
    public static BotSkill Skill(double level)
    {
        var r = Math.Clamp(level, Min, Max) - Min;
        var i = Math.Min((int)r, Max - Min - 1);
        var t = r - i;

        var skill = Skills[i] + ((Skills[i + 1] - Skills[i]) * t);
        var human = HumanShares[i] + ((HumanShares[i + 1] - HumanShares[i]) * t);
        var depth = Depths[i] + ((Depths[i + 1] - Depths[i]) * t);

        return BotSkill.For(skill).Human(human).Novice(depth);
    }

    /// <summary>The level a server tier 1-5 plays, clamped.</summary>
    public static int OfTier(int tier) =>
        TierLevels[Math.Clamp(tier, BotConstants.TierMin, BotConstants.TierMax) - BotConstants.TierMin];

    /// <summary>The level the lowest server tier plays: auto difficulty's floor.</summary>
    public static int LowestTier => OfTier(BotConstants.TierMin);

    /// <summary>The whole level a continuous one shows as.</summary>
    public static int Round(double level) =>
        Math.Clamp((int)Math.Floor(level + 0.5), Min, Max);

    /// <summary>The word a level is shown with.</summary>
    public static string Label(int level) => level switch
    {
        <= 0 => "Dummy",
        <= 2 => "Novice",
        <= 4 => "Easy",
        <= 6 => "Normal",
        <= 8 => "Hard",
        9 => "Expert",
        _ => "Haxton",
    };
}
