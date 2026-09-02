using System.Diagnostics;
using Gg2.Bots;
using Gg2.Simulation;
using Gg2.Protocol;

namespace Bench;

/// <summary>
/// What threading the candidate loop buys the arm-3 tick tail, measured.
/// </summary>
/// <remarks>
/// ⛔ <b>This mode exists because arm 3 failed the tick gate on 21 of 21 maps</b>
/// — worst tick 45.50-69.14 ms against a 33.3 ms budget at 8 bots — and the worst
/// ticks held only <b>2-3 coinciding decisions</b>, so a concurrency cap has
/// nothing to bound. Cost per decision is the whole problem, and threading the
/// candidate loop is the one lever that attacks it without changing what the bot
/// decides. The question it answers is arithmetic: <b>threading needs to close
/// ~1.4x on the best map and ~2x on the worst.</b>
/// <para>
/// ⚠️ <b>ARM 3 ONLY, and the same seeds at every thread count.</b> Threads change
/// no decision (<c>RolloutThreadingTests</c> gates that), so every run plays the
/// identical match and the tick columns are paired by construction. Anything that
/// differs between rows is cost.
/// </para>
/// <para>
/// ⛔ <b>Matches run SERIALLY here and that is not a default to change.</b> The
/// tick figure is wall clock. Running matches in parallel would oversubscribe the
/// machine against the very thread pool being measured, and the speedup would
/// come back near 1.0x for a reason that has nothing to do with the search.
/// </para>
/// <para>
/// ⚠️ <b>The MAX is the gate and the mean is the diagnosis.</b> A tick budget is
/// a tail property — <c>docs/BOTS.md</c> records this repo getting that wrong
/// once, quoting a 3-map max as evidence of a pass when a max is monotone in
/// coverage. Both columns are printed because a speedup that shows in the mean
/// and not in the max means the tail is coincidence rather than cost, which
/// points at a different lever.
/// </para>
/// <para>
/// ⚠️ <b><c>Parallel.For</c>'s own overhead is INSIDE these numbers.</b> A factor
/// quoted with the scheduler excluded is not the factor the tick budget sees.
/// </para>
/// </remarks>
public static class ThreadsBench
{
    private static int Bots =>
        int.TryParse(Environment.GetEnvironmentVariable("BENCH_BOTS"), out var b) && b > 0 ? b : 8;

    private static int[] Ladder
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable("BENCH_THREAD_LADDER");

            if (string.IsNullOrWhiteSpace(raw))
                return [1, 2, 4, 8];

            return [.. raw.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse)];
        }
    }

    private readonly record struct Row(double MaxMs, double MeanMs, long Ticks, long Deaths);

    public static void Run(string maps, string nav, int perMap)
    {
        // ⚠️ The SAME map list ArmsEval uses, so a row here is comparable to the
        // arm-3 row there. A different list would make the factor a comparison
        // between two populations rather than a before and after.
        var list = Environment.GetEnvironmentVariable("BENCH_FOCUS") is { Length: > 0 } focus
            ? focus.Split(',', StringSplitOptions.RemoveEmptyEntries)
            : PerMapEval.Maps;

        var cap = int.Parse(Environment.GetEnvironmentVariable("BENCH_CAP") ?? "5");
        var ladder = Ladder;

        Console.WriteLine(
            $"arm 3 only, {Bots} bots, {perMap} matches a map, cap {cap} min, "
            + $"threads {string.Join('/', ladder)}");
        Console.WriteLine($"cores available: {Environment.ProcessorCount}");
        Console.WriteLine();

        var table = new Dictionary<string, Row[]>(StringComparer.Ordinal);
        var order = new Dictionary<string, double>(StringComparer.Ordinal);
        var clock = Stopwatch.StartNew();

        // ⛔ WARM THE JIT BEFORE TIMING ANYTHING, and this is not a nicety.
        // The first version of this mode reported 11.81x on FOUR threads and 6.12x
        // on the mean -- both arithmetically impossible -- because the serial row
        // ran first and paid JIT for the entire simulation, the bot layer and the
        // collision world. Every later row was warm. A speedup that exceeds the
        // thread count is the cheap tell; a speedup of 1.9x on 2 threads from the
        // same defect would have looked exactly right and been reported.
        Play(list[0], maps, nav, 1, 1, ladder[^1]);
        Play(list[0], maps, nav, 1, 1, 1);

        foreach (var map in list)
        {
            var row = new Row[ladder.Length];

            // ⚠️ THE LADDER IS RUN IN BOTH DIRECTIONS. Warming fixes the JIT, but
            // it does not prove there is no other order effect -- a thermal ramp, a
            // pool that grows and stays grown, a cache that the first row fills for
            // the second. Ascending and descending disagree if one exists, and the
            // gate takes the WORSE of the two rather than the luckier.
            for (var t = 0; t < ladder.Length; t++)
                row[t] = Play(map, maps, nav, perMap, cap, ladder[t]);

            for (var t = ladder.Length - 1; t >= 0; t--)
            {
                var ascending = row[t];
                var descending = Play(map, maps, nav, perMap, cap, ladder[t]);

                // How far the two passes disagreed on the mean, worst over the
                // whole ladder. Printed beside the factor: a large number means
                // the factor is measuring the order, not the threading.
                var spread = ascending.MeanMs == 0
                    ? 0
                    : Math.Abs(descending.MeanMs - ascending.MeanMs) / ascending.MeanMs;

                order[map] = Math.Max(order.GetValueOrDefault(map), spread);

                row[t] = new Row(
                    Math.Max(ascending.MaxMs, descending.MaxMs),
                    Math.Max(ascending.MeanMs, descending.MeanMs),
                    descending.Ticks,
                    descending.Deaths);
            }

            // ⛔ THE DECISIONS MUST BE THE SAME MATCH at every thread count, and
            // the death count is the cheapest witness of it that this harness
            // already has. RolloutThreadingTests gates the decision itself; this
            // catches the case where the bench wired a thread count somewhere it
            // changed the run - a speedup measured over a DIFFERENT match is the
            // exact shape of wrong number this repo keeps producing.
            for (var t = 1; t < ladder.Length; t++)
            {
                if (row[t].Deaths == row[0].Deaths && row[t].Ticks == row[0].Ticks)
                    continue;

                Console.WriteLine(
                    $"  ⛔ {map}: threads {ladder[t]} played a DIFFERENT match than threads "
                    + $"{ladder[0]} ({row[t].Deaths} deaths / {row[t].Ticks} ticks against "
                    + $"{row[0].Deaths} / {row[0].Ticks}). The speedup below is meaningless.");
            }

            table[map] = row;
            Console.WriteLine($"  {map,-18} done  {clock.Elapsed.TotalSeconds,6:0}s");
        }

        Console.WriteLine();
        Report(table, ladder, order);
    }

    private static Row Play(string map, string maps, string nav, int count, int cap, int threads)
    {
        var maxMs = 0.0;
        long ticks = 0, deaths = 0;
        var total = 0.0;

        for (var i = 0; i < count; i++)
        {
            // ⚠️ The SAME seed expression ArmsEval.Play uses, so a row here is
            // comparable to the arm-3 row there rather than merely similar.
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
                SearchMicro = new RolloutOptions(Threads: threads),
                PolicyTeam = Team.Red,
            };

            var match = HeadlessMatch.Load(options, maps, nav);
            var tick = new Stopwatch();

            while (match.Outcome == MatchOutcome.Running)
            {
                tick.Restart();
                match.Step();
                tick.Stop();

                var ms = tick.Elapsed.TotalMilliseconds;
                maxMs = Math.Max(maxMs, ms);
                total += ms;
                ticks++;

                for (var slot = 1; slot < WorldState.MaxSlots; slot++)
                {
                    if (match.Events.Died[slot])
                        deaths++;
                }
            }
        }

        return new Row(maxMs, ticks == 0 ? 0 : total / ticks, ticks, deaths);
    }

    private static void Report(
        Dictionary<string, Row[]> table, int[] ladder, Dictionary<string, double> order)
    {
        Console.Write($"{"map",-18}");

        foreach (var t in ladder)
            Console.Write($" {"max@" + t,8} {"mean@" + t,8}");

        Console.WriteLine($" {"max x",7} {"mean x",7} {"order",6} {"gate",6}");

        var needed = 0.0;
        var bestFactor = double.PositiveInfinity;
        var worstFactor = double.NegativeInfinity;
        var passes = 0;

        foreach (var (map, row) in table.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            Console.Write($"{map,-18}");

            foreach (var r in row)
                Console.Write($" {r.MaxMs,8:0.00} {r.MeanMs,8:0.000}");

            var top = row[^1];
            var maxFactor = top.MaxMs == 0 ? 0 : row[0].MaxMs / top.MaxMs;
            var meanFactor = top.MeanMs == 0 ? 0 : row[0].MeanMs / top.MeanMs;

            bestFactor = Math.Min(bestFactor, maxFactor);
            worstFactor = Math.Max(worstFactor, maxFactor);

            // ⚠️ What threading WOULD have to close on this map, from the serial
            // reading — printed whether or not it did, because the gap between
            // "needed" and "got" is the answer and a pass/fail alone hides it.
            needed = Math.Max(needed, row[0].MaxMs / 33.3);

            var pass = top.MaxMs <= 33.3;

            if (pass)
                passes++;

            Console.WriteLine(
                $" {maxFactor,7:0.00} {meanFactor,7:0.00} {order.GetValueOrDefault(map),5:0.0%} "
                + $"{(pass ? "pass" : "FAIL"),6}");
        }

        Console.WriteLine();
        Console.WriteLine(
            $"speedup on the MAX at {ladder[^1]} threads: {bestFactor:0.00}x to {worstFactor:0.00}x "
            + $"across {table.Count} maps");
        Console.WriteLine(
            $"the worst map needed {needed:0.00}x to reach the 33.3 ms budget");
        Console.WriteLine();

        // ⛔ The verdict and its refutation in one block - tools/bench trap 1.
        if (passes == table.Count)
        {
            Console.WriteLine(
                $"TICK GATE PASSES on all {table.Count} maps at {ladder[^1]} threads.");
            Console.WriteLine(
                "⚠️ A max is MONOTONE IN COVERAGE: this is a pass over the maps measured and");
            Console.WriteLine(
                "   nothing more. Widening the map list or the match count can only make the");
            Console.WriteLine(
                "   worst tick worse, never better. Re-read this line before shipping on it.");
            return;
        }

        Console.WriteLine(
            $"TICK GATE FAILS on {table.Count - passes} of {table.Count} maps at "
            + $"{ladder[^1]} threads - threading alone does not make arm 3 shippable.");
    }
}
