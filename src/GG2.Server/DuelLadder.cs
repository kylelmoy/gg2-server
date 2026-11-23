using GG2.Bots;
using GG2.Protocol;

namespace GG2.Server;

/// <summary>
/// A difficulty staircase on <see cref="BotLevel"/>'s scale: a bot plays at a
/// rating that rises when its human wins and falls when they lose. The duel
/// server keeps one per human and class (<c>docs/DUEL.md</c>, "The difficulty
/// ladder"); auto difficulty one per human (<c>docs/BOTS.md</c>, "Auto difficulty").
/// </summary>
/// <remarks>
/// <para>
/// <b>Both run on the one level scale every bot plays</b>, so a level means the
/// same bot everywhere and a server tier is a level (<see cref="BotLevel.OfTier"/>).
/// The duel's ladder runs the whole scale, 0-10; auto difficulty's stops at
/// tier 1's level, since a bot that never fires or barely does is a fix for
/// cumbersome controls in a 1v1, not a team-mate (<see cref="ForAuto"/>).
/// </para>
/// <para>
/// <b>The rating is continuous and the bot plays it exactly</b>; the name shows
/// it rounded. A weighted up-down staircase: a win moves the rating up by
/// <c>step × margin</c> and a loss down by <c>step × margin × p / (1 − p)</c>, so
/// it stands still where the human wins a fraction <c>p</c> of their duels. The
/// margin is <c>0.5 + 0.5 × the winner's health left</c>, so a clean win counts
/// for twice a scrape. The step starts at a whole level, halves at each change of
/// direction down to a quarter, and goes back up to a half after three losses
/// in a row or a run of wins as unlikely (six at 60%), so a new player settles in a few duels and a player who warms
/// up or tilts moves again.
/// </para>
/// <para>
/// <b>Remembered by address, for an hour, in memory only.</b> A stock client
/// reconnects at every change to a map it does not have, and every duel map is
/// one, so a rating kept on the connection would reset each map.
/// </para>
/// </remarks>
public sealed class DuelLadder
{
    /// <summary>The lowest level: Dummy. <see cref="BotLevel.Min"/>.</summary>
    public const int MinLevel = BotLevel.Min;

    /// <summary>The highest level: Haxton, tier 5. <see cref="BotLevel.Max"/>.</summary>
    public const int MaxLevel = BotLevel.Max;

    /// <summary>The step a new rating starts with, in levels.</summary>
    public const double FirstStep = 1.0;

    /// <summary>The smallest step, in levels.</summary>
    public const double MinStep = 0.25;

    /// <summary>The step a run of <see cref="RunLength"/> same-way results restores.</summary>
    public const double RunStep = 0.5;

    /// <summary>
    /// Losses in a row that restore <see cref="RunStep"/>. Wins need a run as
    /// unlikely at the target rate (<see cref="WinRunLength"/>): with one length
    /// for both, runs of wins are the commoner at any target over a half, the
    /// restored step lands on the way up more often than down, and the rating
    /// settles above the target (measured: 55% for a 60% target).
    /// </summary>
    public const int RunLength = 3;

    /// <summary>How long a rating outlives its last duel: an hour of ticks.</summary>
    public const long ForgetTicks = 60L * 60 * GameConstants.TickRate;

    // A null class is one rating for every class: auto difficulty's.
    private readonly Dictionary<(string Player, PlayerClass? Class), Rating> _ratings = [];

    /// <summary>A ladder whose ratings run from <paramref name="minLevel"/> to <paramref name="maxLevel"/>.</summary>
    public DuelLadder(int minLevel = MinLevel, int maxLevel = MaxLevel)
    {
        Bottom = minLevel;
        Top = maxLevel;
    }

    /// <summary>A ladder for auto difficulty: tier 1's level to the top.</summary>
    public static DuelLadder ForAuto() => new(BotLevel.LowestTier, MaxLevel);

    /// <summary>The lowest level this ladder's ratings reach.</summary>
    public int Bottom { get; }

    /// <summary>The highest level this ladder's ratings reach.</summary>
    public int Top { get; }

    /// <summary>One human's standing with one class.</summary>
    public sealed class Rating
    {
        /// <summary>Continuous, in levels, <see cref="Bottom"/> to <see cref="Top"/>.</summary>
        public double Value { get; internal set; }

        /// <summary>The lowest level this rating reaches: its ladder's <see cref="DuelLadder.Bottom"/>.</summary>
        public int Bottom { get; internal init; } = MinLevel;

        /// <summary>The highest level this rating reaches: its ladder's <see cref="DuelLadder.Top"/>.</summary>
        public int Top { get; internal init; } = MaxLevel;

        /// <summary>The next result's step, in levels.</summary>
        public double Step { get; internal set; } = FirstStep;

        /// <summary>+1 after a win, -1 after a loss, 0 before either.</summary>
        public int LastDirection { get; internal set; }

        /// <summary>Results in a row the same way as <see cref="LastDirection"/>.</summary>
        public int Run { get; internal set; }

        /// <summary>Duels counted.</summary>
        public int Duels { get; internal set; }

        internal long LastSeen { get; set; }
    }

    /// <summary>Ratings held, live or not yet forgotten.</summary>
    public int Count => _ratings.Count;

    /// <summary>
    /// The rating for a human and class, made at <paramref name="startLevel"/>
    /// if there is none, and marked seen at <paramref name="tick"/>. A null class
    /// is one rating across every class, which is what auto difficulty keeps.
    /// </summary>
    public Rating For(string player, PlayerClass? playerClass, int startLevel, long tick)
    {
        if (!_ratings.TryGetValue((player, playerClass), out var rating))
        {
            rating = new Rating { Value = Math.Clamp(startLevel, Bottom, Top), Bottom = Bottom, Top = Top };
            _ratings[(player, playerClass)] = rating;
        }

        rating.LastSeen = tick;
        return rating;
    }

    /// <summary>The rating for a human and class if there is one, without marking it seen.</summary>
    public bool TryGet(string player, PlayerClass? playerClass, out Rating rating) =>
        _ratings.TryGetValue((player, playerClass), out rating!);

    /// <summary>Forgets every rating not seen for <see cref="ForgetTicks"/>.</summary>
    public void Forget(long tick)
    {
        List<(string, PlayerClass?)>? stale = null;
        foreach (var (key, rating) in _ratings)
        {
            if (tick - rating.LastSeen > ForgetTicks)
                (stale ??= []).Add(key);
        }

        if (stale is null)
            return;

        foreach (var key in stale)
            _ratings.Remove(key);
    }

    /// <summary>
    /// Moves <paramref name="rating"/> for one duel.
    /// </summary>
    /// <param name="rating">The human's rating for the class they fought as.</param>
    /// <param name="won">Whether the human won.</param>
    /// <param name="winnerHealth">The winner's health left, as a fraction of their maximum.</param>
    /// <param name="targetWinRate">The share of duels the rating settles the human at winning, 0.5-0.9.</param>
    public static void Record(Rating rating, bool won, double winnerHealth, double targetWinRate)
    {
        var p = Math.Clamp(targetWinRate, 0.5, 0.9);
        var margin = 0.5 + (0.5 * Math.Clamp(winnerHealth, 0, 1));
        var direction = won ? 1 : -1;

        if (rating.LastDirection == direction)
        {
            rating.Run++;
            if (rating.Run >= (won ? WinRunLength(p) : RunLength) && rating.Step < RunStep)
                rating.Step = RunStep;
        }
        else
        {
            if (rating.LastDirection != 0)
                rating.Step = Math.Max(MinStep, rating.Step / 2);

            rating.Run = 1;
        }

        var move = rating.Step * margin * (won ? 1 : p / (1 - p));
        rating.Value = Math.Clamp(rating.Value + (direction * move), rating.Bottom, rating.Top);
        rating.LastDirection = direction;
        rating.Duels++;
    }

    /// <summary>
    /// The wins in a row as likely at <paramref name="targetWinRate"/> as
    /// <see cref="RunLength"/> losses: 3 at a half, 6 at 60%, 11 at 70%.
    /// </summary>
    public static int WinRunLength(double targetWinRate)
    {
        var p = Math.Clamp(targetWinRate, 0.5, 0.9);
        return (int)Math.Ceiling((RunLength * Math.Log(1 - p) / Math.Log(p)) - 1e-9);
    }

    /// <summary>The level a duel rating shows as: <see cref="BotLevel.Round"/>.</summary>
    public static int LevelOf(double rating) => BotLevel.Round(rating);

    /// <summary>
    /// A duel bot's name: its level and label, "Lv 5 (Normal)". No class: with it
    /// "Engineer Lv 10 (Haxton)" is 23 bytes of the 20 a name may have, and the
    /// class shows on the sprite. The longest, "Lv 10 (Haxton)", is 14.
    /// </summary>
    public static string NameOf(double rating)
    {
        var level = LevelOf(rating);
        return $"Lv {level} ({BotLevel.Label(level)})";
    }
}
