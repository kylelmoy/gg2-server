using System.Diagnostics;
using Gg2.Bots;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Bench;

/// <summary>
/// Phase 4's exit criterion, per map, on every shipped map.
/// </summary>
/// <remarks>
/// ⚠️ <b>Three things this reports that an aggregate cannot.</b> `gg2-rl` named
/// all three: the criterion says MATCH WIN RATE and the earlier runs quoted
/// capture share; "no map produces a stuck bot" is a per-map claim that an
/// aggregate ratio hides one bad map inside; and "every shipped map" is 22, not
/// the six that were screened.
/// </remarks>
public static class PerMapEval
{
    private static readonly string[] Maps =
    [
        "arena_lumberyard", "arena_montane", "cp_dirtbowl", "cp_egypt",
        "ctf_2dfort", "ctf_avanti", "ctf_classicwell", "ctf_conflict", "ctf_eiger",
        "ctf_oldfort", "ctf_orange", "ctf_truefort", "ctf_waterway",
        "dkoth_atalia", "dkoth_sixties", "gen_destroy",
        "koth_corinth", "koth_gallery", "koth_harvest", "koth_valley", "tdm_mantic",
    ];

    private sealed record Row(
        string Map, int Matches, int Decided, int RedWins, int BlueWins,
        long RedDeaths, long BlueDeaths, long RedScore, long BlueScore,
        int Bots, int Stuck, double MinTravel, int Capped);

    public static void Run(string maps, string nav, string npz, int perMap)
    {
        var policy = PolicyNetwork.Load(npz);

        // ⛔ THE CENSORING. Every phase 4 number was taken at the 5-minute cap,
        // and on gen_destroy the CONTROL arm decides 50% of matches there and
        // 97% at twenty minutes — so a third of both arms was cut off and the
        // win share was computed over the surviving subset. A win share over a
        // filtered subset is exactly the statistic censoring distorts, and if
        // the policy's matches run longer its subset is more selected than the
        // control's. `gg2-rl` raised it; their own Scoreboard already carries
        // draw and dead shares beside every win rate for the same reason.
        var cap = int.Parse(Environment.GetEnvironmentVariable("BENCH_CAP") ?? "5");
        var focus = Environment.GetEnvironmentVariable("BENCH_FOCUS");
        var list = focus is { Length: > 0 } ? focus.Split(',') : Maps;

        Console.WriteLine($"{list.Length} maps, {perMap} matches each, two arms, {cap}-minute cap");
        Console.WriteLine();

        var withPolicy = new List<Row>();
        var control = new List<Row>();
        var clock = Stopwatch.StartNew();

        foreach (var map in list)
        {
            withPolicy.Add(Play(map, maps, nav, policy, perMap, cap));
            control.Add(Play(map, maps, nav, null, perMap, cap));
        }

        // ⚠️ ABSOLUTE COUNTS, not only percentages, and a z on each comparison.
        // A per-map table invites per-map conclusions, and a bare "19.2% vs
        // 23.7%" reads as a result when at n=250 it is 1.2 sigma. A reader
        // cannot recompute that from the percentage — the counts are what make
        // the claim checkable, which is the whole point of printing it.
        Console.WriteLine(
            $"{"map",-18} {"win",9} {"share",7} {"z",6}  {"deaths",13} {"share",7} {"z",6} | "
            + $"{"ctl win",9} {"share",7}  {"ctl deaths",13} {"share",7} | {"decided",9} {"capped",11} | stuck");

        for (var i = 0; i < withPolicy.Count; i++)
        {
            var p = withPolicy[i];
            var c = control[i];

            Console.WriteLine(
                $"{p.Map,-18} {p.RedWins,4}-{p.BlueWins,-4} {Pct(p.RedWins, p.RedWins + p.BlueWins),7} "
                + $"{Z(p.RedWins, p.RedWins + p.BlueWins, c.RedWins, c.RedWins + c.BlueWins),6}  "
                + $"{p.RedDeaths,6}-{p.BlueDeaths,-6} {Pct(p.RedDeaths, p.RedDeaths + p.BlueDeaths),7} "
                + $"{Z(p.RedDeaths, p.RedDeaths + p.BlueDeaths, c.RedDeaths, c.RedDeaths + c.BlueDeaths),6} | "
                + $"{c.RedWins,4}-{c.BlueWins,-4} {Pct(c.RedWins, c.RedWins + c.BlueWins),7}  "
                + $"{c.RedDeaths,6}-{c.BlueDeaths,-6} {Pct(c.RedDeaths, c.RedDeaths + c.BlueDeaths),7} | "
                + $"{p.Decided,4}/{c.Decided,-4} {p.Capped / (double)p.Matches,5:0%}/{c.Capped / (double)c.Matches,-5:0%} | "
                + $"{p.Stuck}/{p.Bots} vs {c.Stuck}/{c.Bots}");
        }

        Console.WriteLine();
        Console.WriteLine(
            "z is a two-proportion z of the POLICY share against the CONTROL share on the same map.");
        Console.WriteLine(
            "|z| < 1.96 is not a result at this n — read those rows as 'no difference measured', not as a small one.");
        Console.WriteLine(
            "score columns dropped: they were identical to the win columns on every CTF map and empty elsewhere.");
        Console.WriteLine(
            "capped = share of matches that hit the time cap. A WIN SHARE IS COMPUTED OVER DECIDED MATCHES ONLY,");
        Console.WriteLine(
            "so a row with a high capped share is a win share over a filtered subset - and if the two arms are");
        Console.WriteLine(
            "filtered by different amounts, their win shares are not over comparable populations.");

        Console.WriteLine();
        Summarise("POLICY", withPolicy);
        Summarise("CONTROL", control);
        Console.WriteLine($"wall clock {clock.Elapsed.TotalSeconds:0}s");
    }

    private static string Pct(long a, long total) => total == 0 ? "-" : $"{a / (double)total:0.0%}";

    /// <summary>
    /// Two-proportion z of one share against another.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Printed beside every per-map comparison because the percentages
    /// alone are not readable.</b> Over 21 maps with two arms each, several
    /// gaps of 4-5 points appear that are pure sampling noise at n≈250 —
    /// and a reader scanning the column has no way to tell them from the
    /// 30-point ones that are real.
    /// </remarks>
    private static string Z(long a1, long n1, long a2, long n2)
    {
        if (n1 == 0 || n2 == 0)
            return "-";

        var pooled = (a1 + a2) / (double)(n1 + n2);
        var se = Math.Sqrt(pooled * (1 - pooled) * ((1.0 / n1) + (1.0 / n2)));

        if (se == 0)
            return "-";

        return $"{((a1 / (double)n1) - (a2 / (double)n2)) / se:+0.0;-0.0;0.0}";
    }

    private static void Summarise(string label, List<Row> rows)
    {
        long decided = 0, matches = 0, redWins = 0, blueWins = 0;
        long rd = 0, bd = 0, rs = 0, bs = 0, stuck = 0, bots = 0;
        var worstScore = ("", 2.0);
        var worstStuck = ("", 0.0);

        foreach (var r in rows)
        {
            decided += r.Decided;
            matches += r.Matches;
            redWins += r.RedWins;
            blueWins += r.BlueWins;
            rd += r.RedDeaths;
            bd += r.BlueDeaths;
            rs += r.RedScore;
            bs += r.BlueScore;
            stuck += r.Stuck;
            bots += r.Bots;

            if (r.RedScore + r.BlueScore > 0)
            {
                var share = r.RedScore / (double)(r.RedScore + r.BlueScore);

                if (share < worstScore.Item2)
                    worstScore = (r.Map, share);
            }

            var stuckRate = r.Bots == 0 ? 0 : r.Stuck / (double)r.Bots;

            if (stuckRate > worstStuck.Item2)
                worstStuck = (r.Map, stuckRate);
        }

        Console.WriteLine(
            $"{label,-8} decided {decided}/{matches} ({decided / (double)matches:0.0%})"
            + $"   red win share {Pct(redWins, redWins + blueWins)}"
            + $"   death share {Pct(rd, rd + bd)}"
            + $"   score share {Pct(rs, rs + bs)}");
        // ⚠️ A map with no score at all must not win "worst by score share".
        // dkoth and koth have no capture counter, so the first version printed
        // "WORST MAP by score share:  200.0%" — an empty name and a sentinel —
        // which is a bug that looks like a formatting glitch and reads as data.
        var worst = worstScore.Item1.Length == 0
            ? "none (no map on this arm has a score counter)"
            : $"{worstScore.Item1} {worstScore.Item2:0.0%}";

        Console.WriteLine(
            $"{"",8} WORST MAP by score share: {worst}"
            + $"   WORST by stuck: {worstStuck.Item1} {worstStuck.Item2:0.00%}"
            + $"   stuck {stuck}/{bots}");
        Console.WriteLine(
            $"{"",8} totals: wins {redWins}-{blueWins}   deaths {rd}-{bd}   score {rs}-{bs}");
    }

    private static Row Play(string map, string maps, string nav, PolicyNetwork? policy, int count, int cap)
    {
        var decided = 0;
        var capped = 0;
        var redWins = 0;
        var blueWins = 0;
        long redDeaths = 0, blueDeaths = 0;
        var bots = 0;
        var stuck = 0;
        var minTravel = double.PositiveInfinity;
        long redScore = 0, blueScore = 0;

        for (var i = 0; i < count; i++)
        {
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
            };

            var match = HeadlessMatch.Load(options, maps, nav);
            var start = new (double X, double Y)[WorldState.MaxSlots];
            var travel = new double[WorldState.MaxSlots];
            var seen = new bool[WorldState.MaxSlots];

            while (match.Outcome == MatchOutcome.Running)
            {
                match.Step();

                for (var slot = 1; slot < WorldState.MaxSlots; slot++)
                {
                    if (match.Events.Died[slot])
                    {
                        if (match.State.Players[slot].Team == Team.Red)
                            redDeaths++;
                        else
                            blueDeaths++;
                    }

                    if (!match.State.IsAlive(slot))
                        continue;

                    ref readonly var c = ref match.State.Characters[slot];

                    if (!seen[slot])
                    {
                        seen[slot] = true;
                        start[slot] = (c.X, c.Y);
                    }

                    var dx = c.X - start[slot].X;
                    var dy = c.Y - start[slot].Y;

                    travel[slot] = Math.Max(travel[slot], Math.Sqrt((dx * dx) + (dy * dy)));
                }
            }

            for (var slot = 1; slot < WorldState.MaxSlots; slot++)
            {
                if (!seen[slot])
                    continue;

                bots++;

                if (travel[slot] < 100)
                    stuck++;

                minTravel = Math.Min(minTravel, travel[slot]);
            }

            redScore += match.State.RedCaps;
            blueScore += match.State.BlueCaps;

            if (match.Tick >= 30 * 60 * cap)
                capped++;

            var result = match.Result;

            if (result.Outcome != MatchOutcome.Decided)
                continue;

            decided++;

            if (result.Winner == Team.Red)
                redWins++;
            else if (result.Winner == Team.Blue)
                blueWins++;
        }

        return new Row(map, count, decided, redWins, blueWins, redDeaths, blueDeaths,
            redScore, blueScore, bots, stuck, minTravel, capped);
    }
}
