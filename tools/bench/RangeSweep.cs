using System.Diagnostics;
using Gg2.Bots;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Bench;

/// <summary>
/// Does decidedness fall as the policy's share of control rises?
/// </summary>
/// <remarks>
/// ⛔ <b><c>gg2-rl</c>'s hypothesis, made separable.</b> The 21-map table showed
/// the policy arm deciding 54 fewer matches than the control across 19 maps —
/// 0.7 points in aggregate, which reads as nothing — and <c>gen_destroy</c>
/// losing <b>22%</b> of its decided matches on its own. Their framing: "matches
/// get more violent and less decisive", showing up per map for the first time.
/// <para>
/// ⚠️ <b>An A/B cannot separate that, and a sweep can.</b> Policy-vs-control is
/// one point against one point; if decidedness falls MONOTONICALLY as the
/// engagement range (and with it the policy's share of ticks) rises, the policy's
/// control is the cause. If it does not, something specific to that map is, and
/// <c>gen_destroy</c> was already the closest miss in `gg2-rl`'s own duel audit
/// — 6.2% dead duels against a 5% tolerance — so a map-specific cause has a
/// standing candidate.
/// </para>
/// <para>
/// ⚠️ <b>Range 0 is not the control arm.</b> It is the hybrid with a gate that
/// never opens, which costs a <c>ScriptedBot.Poll</c> and then discards nothing.
/// The control arm is <c>Policy = null</c> and is printed separately, because
/// "range 0 equals control" is an assumption worth being able to read off the
/// table rather than assert.
/// </para>
/// </remarks>
public static class RangeSweep
{
    public static void Run(string maps, string nav, string npz, int count)
    {
        var policy = PolicyNetwork.Load(npz);
        var mapList = (Environment.GetEnvironmentVariable("BENCH_FOCUS") ?? "gen_destroy").Split(',');
        var ranges = (Environment.GetEnvironmentVariable("BENCH_RANGES") ?? "0,80,150,220,300,450")
            .Split(',').Select(double.Parse).ToArray();

        // ⛔ THE CAP TEST. `gg2-rl`'s alternative explanation for "less decisive",
        // and it may dissolve the effect entirely: a capped match is an undecided
        // match, so if the policy simply makes fights LONGER, decidedness falls
        // with no separate mechanism at all. Raising the cap separates the two —
        // if decidedness recovers, length is the whole story; if it does not,
        // matches are genuinely not resolving and length is not the mechanism.
        //
        // ⚠️ Their own warning about the mean is why this exists rather than a
        // comparison of mean durations: mean length rose 7.8% while decidedness
        // fell from 40% to 20%, which a mean shift alone cannot produce. It works
        // only if the distribution is already clustered at the cap — which is a
        // claim about the distribution that the mean cannot support either way.
        var cap = int.Parse(Environment.GetEnvironmentVariable("BENCH_CAP") ?? "5");
        var clock = Stopwatch.StartNew();

        foreach (var map in mapList)
        {
            Console.WriteLine($"=== {map}, {count} matches an arm, {cap}-minute cap ===");
            Console.WriteLine(
                $"{"arm",-14} {"decided",9} {"rate",7} {"ctl%",7} {"deaths",13} {"share",7} "
                + $"{"wins",9} {"share",7} {"len(s)",7} {"capped",7}");

            Row(map, maps, nav, null, 0, count, cap, "CONTROL");

            foreach (var range in ranges)
                Row(map, maps, nav, policy, range, count, cap, $"policy {range:0}px");

            Console.WriteLine();
        }

        Console.WriteLine($"wall clock {clock.Elapsed.TotalSeconds:0}s");
    }


    private static (int SR, int SB, int LR, int LB, int SD, int LD, Dictionary<string, int> Ended) Strata;

    /// <summary>Which rule actually ended the match.</summary>
    /// <remarks>
    /// ⚠️ <b>Derived from the finished state, not from a field</b> — nothing in
    /// <c>MatchResult</c> records it, and adding one would put a bench question
    /// into the simulation. Order matters: a match can satisfy more than one, and
    /// the cap is checked last because a match at the cap that also reached a
    /// limit was ended by the limit.
    /// </remarks>
    private static string Why(HeadlessMatch match, MatchResult result, int cap)
    {
        if (result.Outcome != MatchOutcome.Decided)
            return "undecided";

        // ⛔ TDM's kill counter IS RedCaps/BlueCaps — `Rounds.cs:565` compares the
        // kill limit against exactly those two fields, so the same pair means
        // "captures" in CTF and "kills" in Team Deathmatch.
        //
        // ⚠️ THE MODE HAS TO GATE IT, and checking "which limit was configured"
        // is NOT enough — which is how the first version of this was wrong. Both
        // limits are always configured, so with CapLimit = 1 every TDM match that
        // saw a single kill was labelled `caplimit`, and `tdm_mantic` printed
        // caplimit=200 in all five arms. It reads like a clean finding ("the kill
        // limit always binds") and is an artefact of the aliasing.
        //
        // ⚠️ Worth recording that this trap was flagged to `gg2-rl` an hour
        // before it was walked into here. Knowing a field is overloaded does not
        // stop you reading it.
        var kills = match.State.GameMode == GameMode.TeamDeathmatch;

        if (!kills && match.Options.CapLimit > 0
            && (result.RedCaps >= match.Options.CapLimit || result.BlueCaps >= match.Options.CapLimit))
            return "caplimit";

        if (kills && match.State.KillLimit > 0
            && (result.RedCaps >= match.State.KillLimit || result.BlueCaps >= match.State.KillLimit))
            return "killlimit";

        if (match.Tick >= 30 * 60 * cap)
            return "timecap";

        // Whatever the mode's own rule is: a generator destroyed, a hill held,
        // an arena team wiped. Not a fallback — for five of the eight modes it
        // is the ONLY way a round ends.
        return "objective";
    }

    private static void PrintStrata(string label)
    {
        var (sr, sb, lr, lb, sd, ld, ended) = Strata;
        var mix = string.Join(" ", ended.OrderBy(e => e.Key).Select(e => $"{e.Key}={e.Value}"));

        Console.WriteLine(
            $"{"",14}   inside 300s: {sd,4} decided, red {Share(sr, sr + sb),6}"
            + $"   |  beyond 300s: {ld,4} decided, red {Share(lr, lr + lb),6}   |  {mix}");
    }

    private static string Share(int a, int total) => total == 0 ? "  -   " : $"{a / (double)total:0.0%}";

    private static void Row(
        string map, string maps, string nav, PolicyNetwork? policy, double range, int count, int cap, string label)
    {
        var decided = 0;
        var capped = 0;

        // ⛔ PER-MATCH, because the question `gg2-rl` asked cannot be answered
        // from totals: stratify the cap-20 matches by whether they finished
        // inside the OLD 300-second cap.
        //
        //   - a longer-clock ARTEFACT predicts the newly-decided stratum favours
        //     the policy far more than the already-decided one — the advantage
        //     lives in matches that only exist because the clock grew;
        //   - a genuinely better policy predicts the two strata look ALIKE.
        //
        // ⚠️ And WHY each match ended, which is the direct observation of the
        // mechanism rather than an inference about it. KillLimit and CapLimit are
        // unchanged when the clock grows, so a longer cap does not merely reveal
        // outcomes — it changes which limit binds. If the mix shifts from
        // CapLimit toward KillLimit in the policy arm, the instrument is
        // changing what the game rewards.
        // ⛔ BOTH ESTIMATORS OF THE DEATH SHARE, because they trade defects and
        // arguing about which defect bites is worse than measuring it.
        //
        //   pooled ratio  -- sum(red) / sum(red+blue) over every match.
        //                    NO FILTER (a drawn or capped match still contributes
        //                    its deaths) but LENGTH-WEIGHTED, and the policy's
        //                    matches are the long ones.
        //   mean of ratios -- the average of each match's own red share.
        //                    UNWEIGHTED but needs a denominator per match, so a
        //                    ZERO-DEATH match has no ratio and must be dropped,
        //                    which reintroduces exactly the kind of filter the
        //                    day was spent removing.
        //
        // Printing both plus the zero-death count makes the disagreement itself
        // the answer: agreement settles it by observation rather than by a
        // plausibility claim about whether death ratios vary with match length.
        //
        // ⚠️ BUT THIS MEASURES THE BIAS RATHER THAN REMOVING IT, and do not read
        // the two rows as meaning it is handled. `gg2-rl` hit the same family in
        // its blind share on 2026-09-01 -- a rate whose denominator is itself an
        // outcome of the arm -- and fixed it by TRUNCATING every duel at a common
        // decision index, which removes the bias by construction. Where a common
        // cut point exists that is strictly better, because instrumentation still
        // has to be reasoned about correctly at every point of use, and this
        // project's record is of correct reasoning failing exactly there.
        //
        // Not changed here because a match has no obvious common cut point the way
        // a fixed-length duel does -- maps end on different limits. ⚠️ If one is
        // ever found, prefer it to this.
        var perMatchShare = 0.0;
        var perMatchCount = 0;
        var zeroDeath = 0;
        var ended = new Dictionary<string, int>();
        int shortDecided = 0, shortRed = 0, shortBlue = 0;
        int longDecided = 0, longRed = 0, longBlue = 0;
        var redWins = 0;
        var blueWins = 0;
        long redDeaths = 0, blueDeaths = 0;
        long policyTicks = 0, scriptedTicks = 0, ticks = 0;

        for (var i = 0; i < count; i++)
        {
            // ⚠️ The SAME seeds as the 21-map table, so a row here is comparable
            // to that row rather than to a fresh sample of the same map.
            var seed = (uint)(7000 + (map.Length * 131) + (i * 37));

            var options = HeadlessMatchOptions.Default with
            {
                Map = map,
                Bots = 8,
                Seed = seed,
                CapLimit = 1,
                TimeLimitMinutes = cap,
                KillLimit = 40,
                MaxTicks = 30 * 60 * cap,
                AllowedClasses = [.. PolicyBot.TrainedClasses],
                Policy = policy,
                PolicyTeam = Team.Red,
                EngagementRange = policy is null ? HeadlessMatchOptions.Default.EngagementRange : range,
            };

            var match = HeadlessMatch.Load(options, maps, nav);
            var redBefore = redDeaths;
            var deathsBefore = redDeaths + blueDeaths;

            while (match.Outcome == MatchOutcome.Running)
            {
                match.Step();

                for (var slot = 1; slot < WorldState.MaxSlots; slot++)
                {
                    if (!match.Events.Died[slot])
                        continue;

                    if (match.State.Players[slot].Team == Team.Red)
                        redDeaths++;
                    else
                        blueDeaths++;
                }
            }

            for (var slot = 1; slot < WorldState.MaxSlots; slot++)
            {
                if (match.PolicyBotFor(slot) is not { } bot)
                    continue;

                policyTicks += bot.PolicyTicks;
                scriptedTicks += bot.ScriptedTicks;
            }

            ticks += match.Tick;

            // ⚠️ This match's OWN death ratio, for the unweighted estimator.
            // A zero-death match has no ratio and is counted separately rather
            // than silently skipped — the count IS the diagnostic, because it is
            // the size of the filter this estimator reintroduces.
            var matchDeaths = (redDeaths + blueDeaths) - deathsBefore;

            if (matchDeaths == 0)
            {
                zeroDeath++;
            }
            else
            {
                perMatchShare += (redDeaths - redBefore) / (double)matchDeaths;
                perMatchCount++;
            }


            // ⚠️ How many matches reached the cap, printed rather than inferred.
            // "undecided" and "hit the cap" are not the same thing — a match can
            // end undecided for other reasons — and the cap test's whole question
            // is which of the two is moving.
            if (match.Tick >= 30 * 60 * cap)
                capped++;

            var result = match.Result;
            var why = Why(match, result, cap);

            ended[why] = ended.GetValueOrDefault(why) + 1;

            if (result.Outcome != MatchOutcome.Decided)
                continue;

            decided++;

            // ⚠️ 9,000 ticks is 300 seconds — the OLD cap exactly, so this
            // stratum is precisely the set of matches the five-minute runs could
            // see, and the other is precisely what the longer clock added.
            var early = match.Tick <= 30 * 60 * 5;

            if (early)
                shortDecided++;
            else
                longDecided++;

            if (result.Winner == Team.Red)
            {
                redWins++;

                if (early)
                    shortRed++;
                else
                    longRed++;
            }
            else if (result.Winner == Team.Blue)
            {
                blueWins++;

                if (early)
                    shortBlue++;
                else
                    longBlue++;
            }
        }

        Strata = (shortRed, shortBlue, longRed, longBlue, shortDecided, longDecided, ended);

        var share = policyTicks + scriptedTicks == 0
            ? "-"
            : $"{policyTicks / (double)(policyTicks + scriptedTicks):0.0%}";

        Console.WriteLine(
            $"{label,-14} {decided,4}/{count,-4} {decided / (double)count,7:0.0%} {share,7} "
            + $"{redDeaths,6}-{blueDeaths,-6} {redDeaths / (double)Math.Max(1, redDeaths + blueDeaths),7:0.0%} "
            + $"{redWins,4}-{blueWins,-4} {redWins / (double)Math.Max(1, redWins + blueWins),7:0.0%} "
            + $"{ticks / (double)count / 30.0,7:0.0} {capped / (double)count,7:0.0%}");

        Console.WriteLine(
            $"{"",14}   death share pooled {redDeaths / (double)Math.Max(1, redDeaths + blueDeaths),6:0.0%}"
            + $"   unweighted {(perMatchCount == 0 ? 0 : perMatchShare / perMatchCount),6:0.0%}"
            + $" over {perMatchCount,4} matches"
            + $"   zero-death {zeroDeath,4}"
            + $"   {(perMatchCount == 0 ? "" : $"gap {Math.Abs(redDeaths / (double)Math.Max(1, redDeaths + blueDeaths) - (perMatchShare / perMatchCount)):0.00%}")}");

        if (cap > 5)
            PrintStrata(label);
    }
}
