using Gg2.Protocol;

namespace Gg2.Bots;

/// <summary>The <c>[Bots]</c> section of the server's config.</summary>
/// <param name="Enabled">Whether to manage a bot population at all.</param>
/// <param name="FillToPlayers">
/// How many players the server wants in total, humans included. ⚠️ It is a
/// <em>fill</em> target, not a bot count: a server that fills to 8 with 3 humans
/// on it runs 5 bots and drops one every time somebody joins.
/// </param>
/// <param name="MaxBots">A hard ceiling on top of the fill target.</param>
/// <param name="MinHumans">
/// ⚠️ <b>Below this, the target is ZERO rather than the fill.</b> An empty server
/// running eight bots against each other is a server nobody can find a game on —
/// it looks full in the lobby list and it is not.
/// </param>
/// <param name="Difficulty">
/// 1 to 5, straight into <see cref="BotSkill.ForTier"/>. ⚠️ It is one point on a
/// continuous skill scale in even steps, so tiers 1, 4 and 5 land exactly on the
/// anchors the published tuning tables are quoted at.
/// </param>
/// <param name="NamePrefix">What a bot's name starts with.</param>
/// <param name="SearchMicro">
/// Non-null gives every bot a <see cref="RolloutSearch"/> micro-controller inside
/// its engagement window, with the scripted layer still owning movement, routing
/// and objectives — <c>docs/BOTS.md</c>’s arm 3. Null is the all-scripted arm 1.
/// </param>
/// <remarks>
/// ⚠️ <b>Arm 3 is the better BOT and the more expensive one.</b> It beat the
/// policy hybrid on death share pooled over ~34,000 deaths (44.55% against 46.17%,
/// z = −4.26) and over 840 matches an arm (−3.59 points, t = −5.60). It also costs
/// far more per decision, which is what <c>RolloutOptions.Threads</c> is for.
/// <para>
/// ⛔ <b>Measured at 8 bots, not 12.</b> A rollout re-simulates every live
/// character for every tick of every candidate, so its cost rises with the
/// POPULATION as well as with the bot count — <c>tools/bench/README.md</c> trap 5.
/// Nothing here has been measured at a full server with humans in it.
/// </para>
/// </remarks>
public readonly record struct BotPopulationOptions(
    bool Enabled = false,
    int FillToPlayers = 8,
    int MaxBots = 8,
    int MinHumans = 1,
    int Difficulty = 3,
    string NamePrefix = "[BOT] ",
    RolloutOptions? SearchMicro = null)
{
    /// <summary>
    /// The reference's own <c>[Bots]</c> defaults.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Use this, never <c>new BotPopulationOptions()</c> or
    /// <c>default</c>.</b> A record struct's primary-constructor defaults do NOT
    /// apply to its parameterless form — that one zero-initialises — so a defaulted
    /// value has a fill target of 0, a difficulty of 0 and a <b>null</b> name
    /// prefix. Measured: a server started with <c>--bots 12</c> named its bots
    /// "1".."12" instead of "[BOT] 1".."[BOT] 12", because the flag handler built
    /// its options with <c>with</c> from a zeroed one.
    /// <para>
    /// The same shape as <c>CharacterState.Fresh</c> and <c>PlayerState.Fresh</c>,
    /// and for the same reason: in this codebase a zeroed value type is routinely
    /// not a valid one, and the fix is always a named factory rather than a rule
    /// nobody remembers.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// ⚠️ The values are spelled out rather than delegating to <c>new()</c> —
    /// <c>new BotPopulationOptions()</c> resolves to the struct's implicit
    /// parameterless constructor, which is the zeroing one this factory exists to
    /// avoid. Writing <c>new()</c> here would make the factory a no-op that reads
    /// exactly like a fix.
    /// </remarks>
    public static BotPopulationOptions Default => new(
        Enabled: false,
        FillToPlayers: 8,
        MaxBots: 8,
        MinHumans: 1,
        Difficulty: 3,
        NamePrefix: "[BOT] ",
        SearchMicro: null);
}

/// <summary>
/// <c>botPopulationUpdate</c>, <c>botPickTeam</c> and <c>botPickClass</c>: how
/// many bots there should be, on which team, as what.
/// </summary>
/// <remarks>
/// The decisions only — who does the adding and removing is the server's, because
/// only it has a roster and a socket to broadcast on. Keeping the policy here
/// keeps it testable without one and keeps <c>Gg2.Bots</c> pure.
/// <para>
/// ⚠️ <b>None of this may draw from <c>WorldState.Random</c>.</b> That stream is
/// observable simulation — every seeded weapon puts its draw on the wire and every
/// client re-derives the spread from it — so a picker drawing from it would make
/// the stream's position depend on how many bots a server happened to add.
/// Callers pass their own; <c>ScriptedBot.Random</c> is the same rule one layer
/// down.
/// </para>
/// </remarks>
public static class BotPopulation
{
    /// <summary>
    /// How many bots the server should be running.
    /// </summary>
    /// <param name="humans">
    /// ⚠️ <c>getNumberOfOccupiedSlots</c>, which excludes bots <em>and</em> the
    /// server's own reserved slot 0. Counting either would make the target chase
    /// its own tail: every bot added would reduce the deficit it was added for.
    /// </param>
    public static int Target(int humans, in BotPopulationOptions options)
    {
        if (!options.Enabled)
            return 0;

        // ⚠️ Zero rather than the fill, and it is the first test. An empty server
        // running eight bots against each other looks full in the lobby list and
        // is not.
        if (humans < options.MinHumans)
            return 0;

        return Math.Min(options.MaxBots, Math.Max(0, options.FillToPlayers - humans));
    }

    /// <summary>
    /// <c>botPickTeam</c>: whichever team has fewer players, ties broken by a
    /// coin.
    /// </summary>
    /// <remarks>
    /// Bots have no team-select menu to answer for themselves. ⚠️ The count is
    /// over <em>everybody</em>, humans included — a bot filling a server is
    /// balancing the game rather than balancing the bots.
    /// </remarks>
    public static Team PickTeam(int red, int blue, ref GmlRandom random)
    {
        if (red < blue)
            return Team.Red;

        if (blue < red)
            return Team.Blue;

        // `choose(TEAM_RED, TEAM_BLUE)` — GmlRandom.ChooseIndex is that call.
        return random.ChooseIndex(2) == 0 ? Team.Red : Team.Blue;
    }

    /// <summary>
    /// <c>botPickClass</c>: a class for a newly added bot.
    /// </summary>
    /// <remarks>
    /// Every class from scout to pyro with equal weight. ⚠️ <b>Quote is excluded
    /// because it is not part of the normal roster</b> — the reference's draw is
    /// <c>irandom(8)</c>, which is nine classes and stops short of it.
    /// <para>
    /// ⚠️ <b>The spy is out too, and not because the spy's own code is broken.</b>
    /// The problem is that almost everything a spy does is invisible to the rest of
    /// the bot model: it spends most of its life cloaked, so target selection skips
    /// it and team-mates route around a fight it is standing in; its whole value is
    /// a backstab, which is one branch behind a range test; and a bot doing all of
    /// that badly is indistinguishable from a bot that is broken, which makes every
    /// other behaviour harder to judge from a playtest. Banning it is cheaper than
    /// tuning it and costs the roster one of nine classes.
    /// </para>
    /// <para>
    /// Rejection sampling rather than a shuffled list, because the excluded set is
    /// one class out of nine: the loop expects 1.125 draws, and the alternative is
    /// a table that has to be kept in step with the class constants by hand.
    /// </para>
    /// </remarks>
    public static PlayerClass PickClass(ref GmlRandom random)
    {
        while (true)
        {
            // `irandom(8)` — inclusive of 8, so nine classes, scout through pyro.
            var picked = (PlayerClass)random.NextInclusive(8);

            if (picked != PlayerClass.Spy)
                return picked;
        }
    }

    /// <summary>
    /// A class for a new bot, restricted to <paramref name="allowed"/>.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Phase 4 needs this and nothing else provided it.</b> `gg2-rl`'s
    /// policy was trained on four of the nine classes, and the other five are
    /// untrained input rather than a class it plays badly — so the honest first
    /// full-match experiment restricts both teams to the trained four.
    /// <b><c>ClassLimits</c> is the wrong lever</b>: nothing in the bot path reads
    /// it, and a default headless match is **50% untrained classes** because
    /// <see cref="PickClass(ref GmlRandom)"/> draws uniformly from the eight it
    /// allows, of which four are trained.
    /// <para>
    /// ⚠️ <b>The unrestricted overload is untouched, deliberately.</b> Its draw
    /// sequence is the reference's <c>irandom(8)</c> and every existing fixture
    /// depends on it; a shared implementation that took an "all classes" set would
    /// consume the stream differently and move every recorded match.
    /// </para>
    /// <para>
    /// ⚠️ <b>Rejection sampling, so an impossible set would spin.</b> The set is
    /// checked first: at least one class in scout..pyro, or it is refused. A bot
    /// picker that hung would look exactly like a hung match.
    /// </para>
    /// </remarks>
    public static PlayerClass PickClass(ref GmlRandom random, ReadOnlySpan<PlayerClass> allowed)
    {
        var possible = false;

        foreach (var playerClass in allowed)
        {
            if (playerClass <= PlayerClass.Pyro)
            {
                possible = true;
                break;
            }
        }

        if (!possible)
        {
            throw new ArgumentException(
                "no allowed class is one a bot can be picked as; scout..pyro is the range", nameof(allowed));
        }

        while (true)
        {
            var picked = (PlayerClass)random.NextInclusive(8);

            foreach (var playerClass in allowed)
            {
                if (playerClass == picked)
                    return picked;
            }
        }
    }
}
