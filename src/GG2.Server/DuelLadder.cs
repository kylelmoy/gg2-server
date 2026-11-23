using GG2.Bots;
using GG2.Protocol;

namespace GG2.Server;

/// <summary>
/// The duel server's difficulty ladder: each human's bot plays at a rating that
/// rises when the human wins and falls when they lose, one rating per human and
/// class. <c>docs/DUEL.md</c>, "The difficulty ladder".
/// </summary>
/// <remarks>
/// <para>
/// <b>Eleven levels on the duel server (0-10), eight for auto difficulty.</b> The
/// duel's are <see cref="DuelSkillOf"/>'s own table, with only level 10 a tier (5)
/// and levels 0-2 below anything a tier plays (<see cref="BotSkill.Novice"/>), for
/// players on cumbersome controls; level 0 never fires. For auto difficulty, <see cref="SkillOf"/>:
/// tiers 1-4 sit on levels 1, 3, 5 and 7 and the levels between interpolate their
/// skill; tier 5 is level 8.
/// Tier 5 differs from tier 4 only in playing like a person, and
/// <see cref="BotSkill.Human(double)"/> fades that out between levels 7 and 8, so
/// that step is a slope too rather than a cliff at 7.5. The tiers were tuned to
/// be evenly spaced in play (<see cref="BotSkill.TierSkills"/>), which is why
/// they sit on every other level rather than at even skill intervals.
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
    /// <summary>The lowest level auto difficulty has.</summary>
    public const int MinLevel = 1;

    /// <summary>The duel server's lowest level: Dummy, a bot that never fires (<see cref="BotSkill.NeverFires"/>).</summary>
    public const int DuelMinLevel = 0;

    /// <summary>The highest level auto difficulty has: tier 5, the nets as trained.</summary>
    public const int MaxLevel = 8;

    /// <summary>
    /// The duel server's highest level, also tier 5: its ladder has two Novice
    /// levels below auto difficulty's level 1, so every level there is two higher.
    /// </summary>
    public const int DuelMaxLevel = 10;

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

    // Levels 1, 3, 5, 7 are tiers 1-4 (BotSkill.TierSkills); 8 is tier 5.
    private static ReadOnlySpan<double> AnchorSkills => [0.20, 0.37, 0.61, 0.95];

    // A null class is one rating for every class: auto difficulty's.
    private readonly Dictionary<(string Player, PlayerClass? Class), Rating> _ratings = [];

    /// <summary>A ladder whose ratings run from <paramref name="minLevel"/> to <paramref name="maxLevel"/>.</summary>
    public DuelLadder(int minLevel = MinLevel, int maxLevel = MaxLevel)
    {
        Bottom = minLevel;
        Top = maxLevel;
    }

    /// <summary>A ladder for the duel server: <see cref="DuelMinLevel"/> to <see cref="DuelMaxLevel"/>.</summary>
    public static DuelLadder ForDuel() => new(DuelMinLevel, DuelMaxLevel);

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

    /// <summary>
    /// The level a server difficulty tier sits on: tiers 1-4 on levels 1, 3, 5
    /// and 7, tier 5 on level 8.
    /// </summary>
    public static int LevelOfTier(int tier)
    {
        var tiers = BotSkill.TierSkills.Length;
        var clamped = Math.Clamp(tier, 1, tiers);
        return clamped == tiers ? MaxLevel : (2 * clamped) - 1;
    }

    /// <summary>The level a duel rating shows as.</summary>
    public static int LevelOf(double rating) =>
        Math.Clamp((int)Math.Floor(rating + 0.5), DuelMinLevel, DuelMaxLevel);

    /// <summary>The skill a rating plays at: the tiers' skills, interpolated between their levels.</summary>
    public static BotSkill SkillOf(double rating)
    {
        var r = Math.Clamp(rating, MinLevel, MaxLevel);

        double skill;
        if (r >= 7)
        {
            skill = AnchorSkills[3];
        }
        else
        {
            // Anchors on levels 1, 3, 5, 7.
            var segment = Math.Min(2, (int)((r - 1) / 2));
            var t = (r - 1 - (2 * segment)) / 2;
            skill = AnchorSkills[segment] + ((AnchorSkills[segment + 1] - AnchorSkills[segment]) * t);
        }

        // Tier 5 is tier 4's skill without playing like a person, so the top
        // level fades that out: fully human at 7, not at all at 8.
        return BotSkill.For(skill).Human(MaxLevel - r);
    }

    /// <summary>The duel server's skill at levels 0-10 (index 0 is level 0).</summary>
    private static ReadOnlySpan<double> DuelSkills => [0.15, 0.15, 0.15, 0.15, 0.27, 0.40, 0.55, 0.70, 0.85, 0.95, 0.95];

    /// <summary>How much like a person each duel level plays (<see cref="BotSkill.Human(double)"/>), 1 fully to 0 not at all.</summary>
    private static ReadOnlySpan<double> DuelHuman => [1, 1, 1, 1, 0.85, 0.68, 0.50, 0.33, 0.20, 0.08, 0];

    /// <summary>
    /// How far below the skill floor each duel level plays (<see cref="BotSkill.Novice"/>):
    /// the Dummy level and the two Novice levels. A rating under 0.5 never fires.
    /// </summary>
    private static ReadOnlySpan<double> DuelNovice => [3, 2, 1, 0, 0, 0, 0, 0, 0, 0, 0];

    /// <summary>The skill a duel bot plays at for its human's rating.</summary>
    /// <remarks>
    /// <b>Not <see cref="SkillOf"/>, which auto difficulty plays on team servers.</b>
    /// Human aim costs about one tier in a match and several in a duel: with the
    /// tiers' mapping, levels 1-7 scored 0.36-0.61 against level 4 and level 8 0.94.
    /// So here the human aim fades over the whole ladder, and the skill under it
    /// was refitted (<c>BENCH_DUEL_LADDER=160 BENCH_DUEL_OPPONENT=4</c>) for even
    /// steps in log-odds: 0.46-0.71 a level on the tuning seeds and on
    /// <c>BENCH_SEED_BASE=500000</c>, against 0.22-0.88 before.
    /// <c>docs/TRAPS-BOTS.md</c>, "The duel ladder". Those were levels 1-8 and are
    /// 3-10 now: level 0 (never fires) and levels 1 and 2 are under them, the floor's skill
    /// pushed further down (<see cref="BotSkill.Novice"/>). Level 10 is exactly tier
    /// 5; no level below is a tier. What would show it stale: a retrained duel net,
    /// a change to <see cref="HumanizeOptions.Tracking9"/> or to <see cref="BotSkill.For"/>.
    /// </remarks>
    public static BotSkill DuelSkillOf(double rating)
    {
        var r = Math.Clamp(rating, DuelMinLevel, DuelMaxLevel) - DuelMinLevel;
        var i = Math.Min((int)r, DuelMaxLevel - DuelMinLevel - 1);
        var t = r - i;

        var skill = DuelSkills[i] + ((DuelSkills[i + 1] - DuelSkills[i]) * t);
        var human = DuelHuman[i] + ((DuelHuman[i + 1] - DuelHuman[i]) * t);
        var novice = DuelNovice[i] + ((DuelNovice[i + 1] - DuelNovice[i]) * t);

        return BotSkill.For(skill).Human(human).Novice(novice);
    }

    /// <summary>The word a duel level is shown with.</summary>
    public static string LabelOf(int level) => level switch
    {
        <= 0 => "Dummy",
        <= 2 => "Novice",
        <= 4 => "Easy",
        <= 6 => "Normal",
        <= 8 => "Hard",
        9 => "Expert",
        _ => "Haxton",
    };

    /// <summary>
    /// A duel bot's name: its level and label, "Lv 5 (Normal)". No class: with it
    /// "Engineer Lv 10 (Haxton)" is 23 bytes of the 20 a name may have, and the
    /// class shows on the sprite. The longest, "Lv 10 (Haxton)", is 14.
    /// </summary>
    public static string NameOf(double rating)
    {
        var level = LevelOf(rating);
        return $"Lv {level} ({LabelOf(level)})";
    }
}
