using System.Diagnostics;
using Gg2.Bots;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Bench;

/// <summary>
/// Arms 1, 2 and 3: all-scripted, policy hybrid, search hybrid.
/// </summary>
/// <remarks>
/// `gg2-rl`'s arm-3 experiment. ⚠️ <b>A NEW mode rather than a third arm inside
/// <see cref="PerMapEval"/></b>, deliberately: that mode produced phase 4's
/// numbers and its two arms have to stay reproducible. Adding a third there would
/// have put a new controller inside the instrument the old result is defined by.
/// <para>
/// ⛔ <b>Two criteria, and the second is a GATE rather than a diagnostic.</b> Death
/// share is the phase-4 criterion, unchanged. <b>Max tick time is co-primary</b>:
/// the server is single-threaded at 30 Hz, one search decision is ~23% of a
/// 33.3 ms tick at twelve characters, and handovers are correlated because several
/// bots have an enemy in range during the same firefight. <b>An arm that blows the
/// tick budget is unshippable whatever its death share</b>, and the death-share
/// criterion cannot see it — a server that missed nine ticks still finishes the
/// match and still reports a share.
/// </para>
/// <para>
/// ⚠️ <b>Maximum, not mean.</b> `tools/bench` trap 6: if the units share a
/// resource with a deadline, the mean is not the constraint. A mean tick time
/// would be reassuring and useless.
/// </para>
/// </remarks>
public static class ArmsEval
{
    private readonly record struct Row(
        string Map,
        int Matches,
        long RedDeaths,
        long BlueDeaths,
        long MicroTicks,
        long TotalTicks,
        double MaxTickMs,
        int Capped);

    public static void Run(string maps, string nav, string npz, int perMap)
    {
        var policy = PolicyNetwork.Load(npz);
        var cap = int.Parse(Environment.GetEnvironmentVariable("BENCH_CAP") ?? "5");
        var focus = Environment.GetEnvironmentVariable("BENCH_FOCUS");
        var list = focus is { Length: > 0 } ? focus.Split(',') : PerMapEval.Maps;

        // ⚠️ The search's own tuning, stated here rather than defaulted, because a
        // reader has to know which search this arm ran. k is the candidate set,
        // which is per class and not set here; horizon 20 is the measured default.
        var search = new RolloutOptions(HorizonTicks: 20);

        Console.WriteLine(
            $"{list.Length} maps, {perMap} matches an arm, THREE arms, {cap}-minute cap, {Bots} bots");
        Console.WriteLine("arm 1 all-scripted   arm 2 policy hybrid   arm 3 search hybrid");
        Console.WriteLine();

        var one = new List<Row>();
        var two = new List<Row>();
        var three = new List<Row>();
        var clock = Stopwatch.StartNew();

        foreach (var map in list)
        {
            one.Add(Play(map, maps, nav, null, null, perMap, cap));
            two.Add(Play(map, maps, nav, policy, null, perMap, cap));
            three.Add(Play(map, maps, nav, null, search, perMap, cap));

            Console.WriteLine($"  {map,-18} done  {clock.Elapsed.TotalSeconds,6:0}s");
        }

        Console.WriteLine();
        Report(one, two, three);
    }

    /// <summary>
    /// ⛔ The whole table in one block, verdict and refutation inseparable.
    /// </summary>
    /// <remarks>
    /// `tools/bench` trap 1 and its family: a conclusion and the thing that would
    /// overturn it must not be separable by scrolling. The tick column sits beside
    /// the death share for that reason — an arm can win the left half and be
    /// unshippable on the right, and the two must be read together or not at all.
    /// </remarks>
    private static void Report(List<Row> one, List<Row> two, List<Row> three)
    {
        Console.WriteLine(
            $"{"map",-18} {"a1 death",9} {"a2 death",9} {"a3 death",9} "
            + $"{"a2 ctl",7} {"a3 ctl",7} {"a1 tick",8} {"a2 tick",8} {"a3 tick",8} {"capped",7}");

        int betterThanOne = 0, betterThanTwo = 0, worseThanOne = 0, worseThanTwo = 0;
        double worstTick = 0;

        for (var i = 0; i < one.Count; i++)
        {
            var (a, b, c) = (one[i], two[i], three[i]);

            var da = Share(a.RedDeaths, a.RedDeaths + a.BlueDeaths);
            var db = Share(b.RedDeaths, b.RedDeaths + b.BlueDeaths);
            var dc = Share(c.RedDeaths, c.RedDeaths + c.BlueDeaths);

            if (dc < da) betterThanOne++; else if (dc > da) worseThanOne++;
            if (dc < db) betterThanTwo++; else if (dc > db) worseThanTwo++;

            worstTick = Math.Max(worstTick, c.MaxTickMs);

            Console.WriteLine(
                $"{a.Map,-18} {da,9:0.0%} {db,9:0.0%} {dc,9:0.0%} "
                + $"{Share(b.MicroTicks, b.TotalTicks),7:0.0%} {Share(c.MicroTicks, c.TotalTicks),7:0.0%} "
                + $"{a.MaxTickMs,8:0.00} {b.MaxTickMs,8:0.00} {c.MaxTickMs,8:0.00} "
                + $"{a.Capped + b.Capped + c.Capped,7}");
        }

        Console.WriteLine();
        Console.WriteLine(
            $"death share, arm 3 vs arm 1: better on {betterThanOne} maps, worse on {worseThanOne}");
        Console.WriteLine(
            $"death share, arm 3 vs arm 2: better on {betterThanTwo} maps, worse on {worseThanTwo}");
        Console.WriteLine();

        // ⛔ The gate. Stated as a verdict rather than a number, in the same block.
        Console.WriteLine(
            worstTick <= 33.3
                ? $"TICK GATE PASSED: arm 3's worst tick was {worstTick:0.00} ms against a 33.3 ms budget"
                : $"TICK GATE FAILED: arm 3's worst tick was {worstTick:0.00} ms against a 33.3 ms budget "
                  + "- unshippable regardless of the death share above");
    }

    /// <summary>Bots in the match. 8 keeps the death share comparable to phase 4.</summary>
    /// <remarks>
    /// ⚠️ <b>The tick gate wants TWELVE and the death-share criterion wants EIGHT</b>,
    /// and they are different questions. Eight is what phase 4 measured, so changing
    /// it silently would make arm 3 incomparable to the result it is being judged
    /// against. Twelve is the shipping configuration and the one whose tail matters.
    /// Run both and say which each number came from.
    /// </remarks>
    private static int Bots =>
        int.TryParse(Environment.GetEnvironmentVariable("BENCH_BOTS"), out var b) && b > 0 ? b : 8;

    private static double Share(long part, long whole) => whole == 0 ? 0 : part / (double)whole;

    private static Row Play(
        string map, string maps, string nav,
        PolicyNetwork? policy, RolloutOptions? search, int count, int cap)
    {
        long redDeaths = 0, blueDeaths = 0, microTicks = 0, totalTicks = 0;
        var capped = 0;
        var maxTickMs = 0.0;

        for (var i = 0; i < count; i++)
        {
            var options = HeadlessMatchOptions.Default with
            {
                Map = map,
                Bots = Bots,
                Seed = (uint)(7000 + (map.Length * 131) + (i * 37)),
                CapLimit = 1,
                TimeLimitMinutes = cap,
                KillLimit = 40,
                MaxTicks = 30 * 60 * cap,
                AllowedClasses = [.. PolicyBot.TrainedClasses],
                Policy = policy,
                SearchMicro = search,
                PolicyTeam = Team.Red,
            };

            var match = HeadlessMatch.Load(options, maps, nav);
            var tick = new Stopwatch();

            while (match.Outcome == MatchOutcome.Running)
            {
                // ⚠️ The WHOLE tick, not the micro-controller's share of it. What a
                // 30 Hz server has to fit in 33.3 ms is every bot's decision plus the
                // simulation, and the tail this gate exists for is several bots
                // engaging at once - which only a whole-tick measurement can see.
                tick.Restart();
                match.Step();
                tick.Stop();

                maxTickMs = Math.Max(maxTickMs, tick.Elapsed.TotalMilliseconds);
                totalTicks++;

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

            if (match.Outcome == MatchOutcome.Exhausted)
                capped++;

            for (var slot = 1; slot < WorldState.MaxSlots; slot++)
            {
                microTicks += match.PolicyBotFor(slot)?.PolicyTicks ?? 0;
                microTicks += match.SearchBotFor(slot)?.SearchTicks ?? 0;
            }
        }

        return new Row(map, count, redDeaths, blueDeaths, microTicks, totalTicks, maxTickMs, capped);
    }
}
