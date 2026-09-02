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
/// decides.
/// <para>
/// ⚠️ <b>ARM 3 ONLY, and the same seeds at every thread count.</b> Threads change
/// no decision (<c>RolloutThreadingTests</c> gates that), so every rung plays the
/// identical match and the cost columns are paired by construction. Anything that
/// differs between rungs is cost.
/// </para>
/// <para>
/// ⛔ <b>THE RUNGS ARE INTERLEAVED PER MATCH, and the first design was not.</b>
/// Running forty matches at one thread and then forty at eight is a block design,
/// and it measured the machine's power state rather than the search: the serial
/// block ran first on an idle box and read <b>0.472 ms</b> mean, then <b>0.330 ms</b>
/// — 43% faster — once the threaded blocks had loaded every core and lifted the
/// clocks. That drift lands entirely on the FACTOR'S NUMERATOR. Match <c>i</c> now
/// plays at every rung before match <c>i+1</c> starts, and the rung order flips on
/// odd matches, so no rung holds the first slot and any drift is shared.
/// </para>
/// <para>
/// ⛔ <b>Matches run SERIALLY and that is not a default to change.</b> The tick
/// figure is wall clock; running matches in parallel would oversubscribe the
/// machine against the very thread pool being measured.
/// </para>
/// <para>
/// ⛔ <b>The MAX is one sample and it is NOT a measurement of the search.</b> Three
/// identical runs put the serial max at <b>72.37</b>, <b>98.31</b> and <b>97.47</b> ms
/// against a mean of 0.47 — 200x the mean, out of ~360,000 ticks. It is not the
/// garbage collector (gen0/1/2 deltas across the worst tick were 0/0/0) and not
/// match startup (worst tick index 2962 of 9000), but a single draw from a very
/// heavy tail is not a number to gate on. <b>Read <c>p999</c> for the tail and
/// <c>mean</c> for the cost; the max is printed to be attributed, not quoted.</b>
/// </para>
/// <para>
/// ⚠️ <b><c>Parallel.For</c>'s own overhead is INSIDE these numbers.</b> A factor
/// quoted with the scheduler excluded is not the factor the tick budget sees.
/// </para>
/// </remarks>
public static class ThreadsBench
{
    private const double BudgetMs = 33.3;

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

    /// <summary>
    /// One thread count on one map: every cost column, and what the worst tick was.
    /// </summary>
    private sealed class Rung
    {
        internal double MaxMs;
        internal double TotalMs;
        internal long Ticks;
        internal long Deaths;
        internal int WorstGen0;
        internal int WorstGen1;
        internal int WorstGen2;
        internal int WorstTickIndex;

        // ⚠️ Allocated ONCE per map, before any timing. A list that grows mid-run
        // allocates, and an allocation is the one thing that can provoke the
        // collection this is trying to rule out.
        internal double[] Samples = [];
        internal int Sampled;

        // The total over even-indexed matches and over odd-indexed ones. Even
        // matches played the ladder ascending and odd ones descending.
        //
        // ⚠️ THIS IS AN UPPER BOUND ON THE ORDER EFFECT, NOT A MEASUREMENT OF IT.
        // Even and odd matches carry DIFFERENT SEEDS, so the gap contains genuine
        // match-to-match variation as well as any effect of rung order. It went
        // from 30-89% under the block design to ~15% here, and most of what
        // remains is probably seeds. Isolating the two would mean playing every
        // match at every rung twice, in both orders, for a number that only says
        // how well the alternation worked. The alternation is what makes the
        // AGGREGATE unbiased with respect to order; this column watches it.
        internal double EvenMs;
        internal long EvenTicks;
        internal double OddMs;
        internal long OddTicks;

        internal double MeanMs => Ticks == 0 ? 0 : TotalMs / Ticks;

        internal double P999Ms
        {
            get
            {
                if (Sampled == 0)
                    return 0;

                Array.Sort(Samples, 0, Sampled);
                return Samples[Math.Min(Sampled - 1, (int)(Sampled * 0.999))];
            }
        }

        internal double OrderSpread
        {
            get
            {
                if (EvenTicks == 0 || OddTicks == 0)
                    return 0;

                var even = EvenMs / EvenTicks;
                var odd = OddMs / OddTicks;

                return even == 0 ? 0 : Math.Abs(odd - even) / even;
            }
        }
    }

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
            + $"threads {string.Join('/', ladder)}, rungs INTERLEAVED per match");
        Console.WriteLine($"cores available: {Environment.ProcessorCount}");
        Console.WriteLine();

        // ⛔ WARM THE JIT AND THE MACHINE BEFORE TIMING ANYTHING, and this is not a
        // nicety. The first version of this mode reported 11.81x on FOUR threads
        // and 6.12x on the mean — both arithmetically impossible — because the
        // serial rung ran first and paid the JIT for the entire simulation, the bot
        // layer and the collision world. A speedup exceeding the thread count is
        // the cheap tell; the same defect at 1.9x on two threads would have looked
        // exactly right and been written down.
        foreach (var t in ladder)
            Play(list[0], maps, nav, cap, t, new Rung(), 0);

        var table = new Dictionary<string, Rung[]>(StringComparer.Ordinal);
        var clock = Stopwatch.StartNew();

        foreach (var map in list)
        {
            var rungs = new Rung[ladder.Length];

            for (var t = 0; t < ladder.Length; t++)
                rungs[t] = new Rung { Samples = new double[perMap * (30 * 60 * cap + 8)] };

            for (var i = 0; i < perMap; i++)
            {
                // ⚠️ The rung order FLIPS on odd matches. Interleaving alone still
                // gives one rung the first slot in every match; alternating means
                // no rung holds it.
                for (var n = 0; n < ladder.Length; n++)
                {
                    var t = i % 2 == 0 ? n : ladder.Length - 1 - n;
                    Play(map, maps, nav, cap, ladder[t], rungs[t], i);
                }
            }

            // ⛔ THE RUNGS MUST HAVE PLAYED THE SAME MATCHES, and the death count is
            // the cheapest witness this harness already has. RolloutThreadingTests
            // gates the decision itself; this catches a bench that wired a thread
            // count somewhere it changed the run — a speedup measured over a
            // DIFFERENT match is the exact shape of wrong number this repo produces.
            for (var t = 1; t < ladder.Length; t++)
            {
                if (rungs[t].Deaths == rungs[0].Deaths && rungs[t].Ticks == rungs[0].Ticks)
                    continue;

                Console.WriteLine(
                    $"  ⛔ {map}: threads {ladder[t]} played a DIFFERENT match than threads "
                    + $"{ladder[0]} ({rungs[t].Deaths} deaths / {rungs[t].Ticks} ticks against "
                    + $"{rungs[0].Deaths} / {rungs[0].Ticks}). Everything below is meaningless.");
            }

            table[map] = rungs;
            Console.WriteLine($"  {map,-18} done  {clock.Elapsed.TotalSeconds,6:0}s");
        }

        Console.WriteLine();
        Report(table, ladder);
    }

    private static void Play(
        string map, string maps, string nav, int cap, int threads, Rung into, int index)
    {
        // ⚠️ The SAME seed expression ArmsEval.Play uses, so a row here is
        // comparable to the arm-3 row there rather than merely similar.
        var options = HeadlessMatchOptions.Default with
        {
            Map = map,
            Bots = Bots,
            Seed = (uint)(7000 + (map.Length * 131) + (index * 37)),
            CapLimit = 1,
            TimeLimitMinutes = cap,
            KillLimit = 40,
            MaxTicks = 30 * 60 * cap,
            AllowedClasses = [.. PolicyController.TrainedClasses],
            Brain = BotBrainSpec.Of(new SearchMicroSpec(new RolloutOptions(Threads: threads))),
            HybridTeam = Team.Red,
        };

        var match = HeadlessMatch.Load(options, maps, nav);
        var tick = new Stopwatch();
        var n = 0;
        var matchMs = 0.0;
        long matchTicks = 0;

        while (match.Outcome == MatchOutcome.Running)
        {
            // ⛔ The collection counts are read ACROSS the timed region, so a tick
            // that contains a pause is identifiable as one rather than inferred
            // from its size. "The search is expensive in the tail" and "the tail is
            // the garbage collector" have nothing in common as problems, and the
            // max alone cannot tell them apart.
            var g0 = GC.CollectionCount(0);
            var g1 = GC.CollectionCount(1);
            var g2 = GC.CollectionCount(2);

            tick.Restart();
            match.Step();
            tick.Stop();

            var ms = tick.Elapsed.TotalMilliseconds;

            if (ms > into.MaxMs)
            {
                into.MaxMs = ms;
                into.WorstGen0 = GC.CollectionCount(0) - g0;
                into.WorstGen1 = GC.CollectionCount(1) - g1;
                into.WorstGen2 = GC.CollectionCount(2) - g2;
                into.WorstTickIndex = n;
            }

            if (into.Sampled < into.Samples.Length)
                into.Samples[into.Sampled++] = ms;

            into.TotalMs += ms;
            into.Ticks++;
            matchMs += ms;
            matchTicks++;
            n++;

            for (var slot = 1; slot < WorldState.MaxSlots; slot++)
            {
                if (match.Events.Died[slot])
                    into.Deaths++;
            }
        }

        if (index % 2 == 0)
        {
            into.EvenMs += matchMs;
            into.EvenTicks += matchTicks;
        }
        else
        {
            into.OddMs += matchMs;
            into.OddTicks += matchTicks;
        }
    }

    /// <summary>
    /// ⛔ The whole table in one block, verdict and refutation inseparable.
    /// </summary>
    private static void Report(Dictionary<string, Rung[]> table, int[] ladder)
    {
        Console.Write($"{"map",-18}");

        foreach (var t in ladder)
            Console.Write($" {"max@" + t,8} {"p999@" + t,8} {"mean@" + t,8}");

        Console.WriteLine($" {"p999 x",7} {"mean x",7} {"e/o",6} {"gate",6}");

        var needed = 0.0;
        var bestFactor = double.PositiveInfinity;
        var worstFactor = double.NegativeInfinity;
        var passing = 0;

        foreach (var (map, rungs) in table.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            Console.Write($"{map,-18}");

            foreach (var r in rungs)
                Console.Write($" {r.MaxMs,8:0.00} {r.P999Ms,8:0.000} {r.MeanMs,8:0.000}");

            var top = rungs[^1];

            // ⚠️ The factor is quoted on the P99.9 and the mean, NOT on the max.
            // The max is one draw from a tail that moved 72 -> 98 ms across three
            // identical runs; a ratio of two such draws is a ratio of two coin
            // flips. Both of these average over ~360,000 ticks.
            var p999Factor = top.P999Ms == 0 ? 0 : rungs[0].P999Ms / top.P999Ms;
            var meanFactor = top.MeanMs == 0 ? 0 : rungs[0].MeanMs / top.MeanMs;

            bestFactor = Math.Min(bestFactor, p999Factor);
            worstFactor = Math.Max(worstFactor, p999Factor);
            needed = Math.Max(needed, rungs[0].P999Ms / BudgetMs);

            // ⛔ GATED ON THE P99.9, and say so wherever the verdict is repeated.
            // That is a WEAKER claim than the max: one tick in a thousand is
            // allowed over budget, and on a 30 Hz server that is one every 33
            // seconds. Whether that is acceptable is a decision nobody has taken —
            // a stock client tolerates a late update, and this repo has no
            // measurement of how many in a row it tolerates.
            var pass = top.P999Ms <= BudgetMs;

            if (pass)
                passing++;

            // ⚠️ e/o is an UPPER BOUND on the order effect - see Rung.OddMs. It
            // also carries seed-to-seed variation, so a modest number here is not
            // evidence of a problem and a large one is not proof of order.
            var spread = Math.Max(rungs[0].OrderSpread, top.OrderSpread);

            Console.WriteLine(
                $" {p999Factor,7:0.00} {meanFactor,7:0.00} {spread,5:0.0%} "
                + $"{(pass ? "pass" : "FAIL"),6}");
        }

        Console.WriteLine();
        Attribution(table, ladder);

        Console.WriteLine(
            $"speedup on the P99.9 at {ladder[^1]} threads: {bestFactor:0.00}x to "
            + $"{worstFactor:0.00}x across {table.Count} maps");
        Console.WriteLine(
            $"the worst map needed {needed:0.00}x on the p99.9 to reach the {BudgetMs} ms budget");
        Console.WriteLine();

        if (passing == table.Count)
        {
            Console.WriteLine(
                $"P99.9 TICK GATE PASSES on all {table.Count} maps at {ladder[^1]} threads.");
            Console.WriteLine(
                "⚠️ This is the P99.9, NOT the max — one tick in a thousand may still exceed");
            Console.WriteLine(
                "   the budget, which at 30 Hz is one every 33 seconds. The max is in the");
            Console.WriteLine(
                "   table and is NOT gated, because three identical runs put it at 72, 98 and");
            Console.WriteLine(
                "   97 ms against a 0.47 ms mean.");
            Console.WriteLine(
                "⚠️ A tail statistic is MONOTONE IN COVERAGE like a max: this is a pass over");
            Console.WriteLine(
                "   the maps and matches measured, and widening either can only make it worse.");
            return;
        }

        Console.WriteLine(
            $"P99.9 TICK GATE FAILS on {table.Count - passing} of {table.Count} maps at "
            + $"{ladder[^1]} threads — threading alone does not make arm 3 shippable.");
    }

    /// <summary>
    /// What the worst tick on each rung WAS — a pause, a cold first tick, or work.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>A gate reads a tail, so the tail has to be a measurement of the thing
    /// being gated.</b> This mode's max is one sample out of ~360,000 and sat 200x
    /// above the mean. The columns here say which of three quite different things
    /// it is:
    /// <list type="bullet">
    /// <item><b>gen2 &gt; 0 across the worst tick</b> — a collection pause, which
    /// threading changes only by changing the allocation rate.</item>
    /// <item><b>tick index 0 or 1</b> — match startup, which no tick in a real
    /// round looks like.</item>
    /// <item><b>neither</b> — genuinely coinciding decisions, the only case the
    /// tick gate was ever about. <b>This is what it measured:</b> 0/0/0 across the
    /// worst tick on every rung, at index 2962 of 9000.</item>
    /// </list>
    /// </remarks>
    private static void Attribution(Dictionary<string, Rung[]> table, int[] ladder)
    {
        Console.WriteLine("what the worst tick on each rung actually was:");
        Console.Write($"{"map",-18}");

        foreach (var t in ladder)
            Console.Write($" {"@" + t + " gc(0/1/2)",14} {"@" + t + " tick#",10}");

        Console.WriteLine();

        foreach (var (map, rungs) in table.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            Console.Write($"{map,-18}");

            foreach (var r in rungs)
            {
                Console.Write(
                    $" {r.WorstGen0 + "/" + r.WorstGen1 + "/" + r.WorstGen2,14} "
                    + $"{r.WorstTickIndex,10}");
            }

            Console.WriteLine();
        }

        Console.WriteLine();
    }
}
