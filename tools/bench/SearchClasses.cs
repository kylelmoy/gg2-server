using System.Diagnostics;
using Gg2.Bots;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Bench;

/// <summary>
/// Is the search hybrid still a better bot on classes outside the policy's
/// trained set?
/// </summary>
/// <remarks>
/// <para>
/// <b>The question <c>SearchController.RestrictToTrainedClasses</c> leaves
/// open.</b> Every search number in <c>docs/BOTS.md</c> comes from matches
/// populated with the four default trained classes, because the restriction
/// exists to hold class coverage still against a policy arm. The search holds no
/// checkpoint, so nothing about it needs that set; what is unknown is whether it
/// plays the other classes well, and some of them have a job that is not damage —
/// a medic's heal and uber, an engineer's sentry, a spy's cloak.
/// </para>
/// <para>
/// <b>Two arms, both populated from <c>BENCH_CLASSES</c></b> (default the five
/// outside <see cref="PolicyNetwork.DefaultTrainedClasses"/>): all-scripted, and
/// the search hybrid with the restriction OFF. Red is the hybrid team, blue is
/// scripted in both, and the seeds are shared, so the control is the same match
/// with the controller removed.
/// </para>
/// <para>
/// <b>Wins beside deaths, and deaths per class.</b> Death share alone would pass
/// a medic that stopped healing to shoot, since the question for such a class is
/// whether its team still wins; and a pooled share can hide one class losing
/// inside four that win. No arbiter, as in <c>BENCH_ARMS</c> arm 3, so the
/// search decides at its own cadence and the max tick is that arm's ceiling, not
/// the live server's.
/// </para>
/// </remarks>
public static class SearchClassesEval
{
    private sealed class Tally
    {
        public int Matches;
        public int Decided;
        public int RedWins;
        public int BlueWins;
        public int Capped;
        public long MicroTicks;
        public long RedTicks;
        public double MaxTickMs;
        public readonly long[] RedDeaths = new long[9];
        public readonly long[] BlueDeaths = new long[9];

        public long Red => RedDeaths.Sum();

        public long Blue => BlueDeaths.Sum();

        public void Add(Tally other)
        {
            Matches += other.Matches;
            Decided += other.Decided;
            RedWins += other.RedWins;
            BlueWins += other.BlueWins;
            Capped += other.Capped;
            MicroTicks += other.MicroTicks;
            RedTicks += other.RedTicks;
            MaxTickMs = Math.Max(MaxTickMs, other.MaxTickMs);

            for (var i = 0; i < 9; i++)
            {
                RedDeaths[i] += other.RedDeaths[i];
                BlueDeaths[i] += other.BlueDeaths[i];
            }
        }
    }

    private static StreamWriter? _rows;

    public static void Run(string maps, string nav, int perMap)
    {
        var cap = int.Parse(Environment.GetEnvironmentVariable("BENCH_CAP") ?? "5");
        var focus = Environment.GetEnvironmentVariable("BENCH_FOCUS");
        var list = focus is { Length: > 0 } ? focus.Split(',') : PerMapEval.Maps;
        var bots = int.TryParse(Environment.GetEnvironmentVariable("BENCH_BOTS"), out var b) && b > 0 ? b : 8;

        PlayerClass[] allowed = Environment.GetEnvironmentVariable("BENCH_CLASSES") is { Length: > 0 } named
            ? [.. named.Split(',').Select(n => Enum.Parse<PlayerClass>(n.Trim(), ignoreCase: true))]
            : [PlayerClass.Pyro, PlayerClass.Medic, PlayerClass.Engineer, PlayerClass.Spy, PlayerClass.Sniper];

        // The search's tuning, as BENCH_ARMS arm 3 runs it, with the restriction
        // off — the one thing this mode varies.
        var search = BotBrainSpec.Of(new SearchMicroSpec(
            new RolloutOptions(HorizonTicks: 20), RestrictToTrainedClasses: false));

        Console.WriteLine(
            $"{list.Length} maps, {perMap} matches an arm, TWO arms, {cap}-minute cap, {bots} bots");
        Console.WriteLine($"roster (both arms, both teams): {string.Join(",", allowed)}");
        Console.WriteLine("arm 1 all-scripted   arm 3u search hybrid, restriction OFF   red is the hybrid team");
        Console.WriteLine();

        if (Environment.GetEnvironmentVariable("BENCH_SEARCHCLASSES_CSV") is { Length: > 0 } csv)
        {
            _rows = new StreamWriter(csv, append: false);
            _rows.WriteLine(
                "map,arm,match,winner,red_deaths,blue_deaths,ticks,max_tick_ms,"
                + string.Join(",", allowed.Select(c => $"red_{c},blue_{c}")));
        }

        var control = new List<(string Map, Tally Tally)>();
        var hybrid = new List<(string Map, Tally Tally)>();
        var clock = Stopwatch.StartNew();

        foreach (var map in list)
        {
            control.Add((map, Play(map, maps, nav, null, perMap, cap, bots, allowed, "1")));
            hybrid.Add((map, Play(map, maps, nav, search, perMap, cap, bots, allowed, "3u")));
            Console.WriteLine($"  {map,-18} done  {clock.Elapsed.TotalSeconds,6:0}s");
        }

        _rows?.Flush();
        _rows?.Dispose();

        Console.WriteLine();
        Report(control, hybrid, allowed);
    }

    private static void Report(
        List<(string Map, Tally Tally)> control, List<(string Map, Tally Tally)> hybrid, PlayerClass[] allowed)
    {
        Console.WriteLine(
            $"{"map",-18} {"a1 wins",9} {"a3u wins",9} {"a1 death",9} {"a3u death",9} {"a3u dths",8} {"a3u ctl",8} {"a3u tick",9} {"capped",7}");

        var one = new Tally();
        var three = new Tally();

        for (var i = 0; i < control.Count; i++)
        {
            var (a, c) = (control[i].Tally, hybrid[i].Tally);
            one.Add(a);
            three.Add(c);

            Console.WriteLine(
                $"{control[i].Map,-18} {a.RedWins,4}-{a.BlueWins,-4} {c.RedWins,4}-{c.BlueWins,-4} "
                + $"{Share(a.Red, a.Red + a.Blue),9:0.0%} {Share(c.Red, c.Red + c.Blue),9:0.0%} {c.Red + c.Blue,8} "
                + $"{Share(c.MicroTicks, c.RedTicks),8:0.0%} {c.MaxTickMs,9:0.00} {a.Capped + c.Capped,7}");
        }

        Console.WriteLine();
        Console.WriteLine("aggregate, pooled over every map and match (red = hybrid team):");
        Console.WriteLine(
            $"  wins       arm 1 {one.RedWins}-{one.BlueWins} of {one.Decided} decided / {one.Matches}   "
            + $"red share {Share(one.RedWins, one.RedWins + one.BlueWins):0.0%}");
        Console.WriteLine(
            $"             arm 3u {three.RedWins}-{three.BlueWins} of {three.Decided} decided / {three.Matches}   "
            + $"red share {Share(three.RedWins, three.RedWins + three.BlueWins):0.0%}   "
            + $"z {Z(three.RedWins, three.RedWins + three.BlueWins, one.RedWins, one.RedWins + one.BlueWins)}");
        Console.WriteLine(
            $"  deaths     arm 1 {Share(one.Red, one.Red + one.Blue):0.0%} ({one.Red} of {one.Red + one.Blue})   "
            + $"arm 3u {Share(three.Red, three.Red + three.Blue):0.0%} ({three.Red} of {three.Red + three.Blue})   "
            + $"z {Z(three.Red, three.Red + three.Blue, one.Red, one.Red + one.Blue)}");
        Console.WriteLine(
            $"  search held {Share(three.MicroTicks, three.RedTicks):0.0%} of red bot-ticks; worst tick {three.MaxTickMs:0.00} ms (no arbiter)");

        Console.WriteLine();
        Console.WriteLine("death share by the VICTIM's class (red deaths of that class over both teams' deaths of it):");
        Console.WriteLine($"  {"class",-9} {"arm 1",8} {"n",7} {"arm 3u",8} {"n",7} {"z",6}");

        foreach (var playerClass in allowed)
        {
            var k = (int)playerClass;
            var n1 = one.RedDeaths[k] + one.BlueDeaths[k];
            var n3 = three.RedDeaths[k] + three.BlueDeaths[k];

            Console.WriteLine(
                $"  {playerClass,-9} {Share(one.RedDeaths[k], n1),8:0.0%} {n1,7} "
                + $"{Share(three.RedDeaths[k], n3),8:0.0%} {n3,7} {Z(three.RedDeaths[k], n3, one.RedDeaths[k], n1),6}");
        }

        Console.WriteLine();
        Console.WriteLine(
            "A pooled z treats deaths as independent and they arrive ~40 a match, so it overstates; "
            + "clustered.py on the CSV is the match-level test.");
    }

    private static Tally Play(
        string map, string maps, string nav, BotBrainSpec? brain, int count, int cap, int bots,
        PlayerClass[] allowed, string arm)
    {
        var tally = new Tally();

        for (var i = 0; i < count; i++)
        {
            var options = HeadlessMatchOptions.Default with
            {
                Map = map,
                Bots = bots,
                Seed = (uint)(7000 + (map.Length * 131) + (i * 37)),
                CapLimit = 1,
                TimeLimitMinutes = cap,
                KillLimit = 40,
                MaxTicks = 30 * 60 * cap,
                AllowedClasses = allowed,
                Brain = brain,
                HybridTeam = Team.Red,
            };

            var match = HeadlessMatch.Load(options, maps, nav);
            var tick = new Stopwatch();
            var matchMaxMs = 0.0;
            var redDeaths = new long[9];
            var blueDeaths = new long[9];
            long ticks = 0;

            while (match.Outcome == MatchOutcome.Running)
            {
                tick.Restart();
                match.Step();
                tick.Stop();

                matchMaxMs = Math.Max(matchMaxMs, tick.Elapsed.TotalMilliseconds);
                ticks++;

                for (var slot = 1; slot < WorldState.MaxSlots; slot++)
                {
                    if (match.State.IsAlive(slot) && match.State.Players[slot].Team == Team.Red)
                        tally.RedTicks++;

                    if (!match.Events.Died[slot])
                        continue;

                    // The PLAYER's class: a death has already cleared the character.
                    var victim = (int)match.State.Players[slot].Class;

                    if (victim >= 9)
                        continue;

                    if (match.State.Players[slot].Team == Team.Red)
                        redDeaths[victim]++;
                    else
                        blueDeaths[victim]++;
                }
            }

            for (var slot = 1; slot < WorldState.MaxSlots; slot++)
                tally.MicroTicks += match.HybridBotFor(slot)?.MicroTicks ?? 0;

            tally.Matches++;
            tally.MaxTickMs = Math.Max(tally.MaxTickMs, matchMaxMs);

            if (match.Tick >= 30 * 60 * cap)
                tally.Capped++;

            for (var k = 0; k < 9; k++)
            {
                tally.RedDeaths[k] += redDeaths[k];
                tally.BlueDeaths[k] += blueDeaths[k];
            }

            var result = match.Result;
            var winner = "none";

            if (result.Outcome == MatchOutcome.Decided)
            {
                tally.Decided++;

                if (result.Winner == Team.Red)
                {
                    tally.RedWins++;
                    winner = "red";
                }
                else if (result.Winner == Team.Blue)
                {
                    tally.BlueWins++;
                    winner = "blue";
                }
                else
                {
                    winner = "draw";
                }
            }

            _rows?.WriteLine(
                $"{map},{arm},{i},{winner},{redDeaths.Sum()},{blueDeaths.Sum()},{ticks},{matchMaxMs:0.###},"
                + string.Join(",", allowed.Select(c => $"{redDeaths[(int)c]},{blueDeaths[(int)c]}")));
        }

        return tally;
    }

    private static double Share(long part, long whole) => whole == 0 ? 0 : part / (double)whole;

    /// <summary>Two-proportion z, first against second, as <c>PerMapEval</c> prints it.</summary>
    private static string Z(long x1, long n1, long x2, long n2)
    {
        if (n1 == 0 || n2 == 0)
            return "-";

        var p1 = x1 / (double)n1;
        var p2 = x2 / (double)n2;
        var pooled = (x1 + x2) / (double)(n1 + n2);
        var se = Math.Sqrt(pooled * (1 - pooled) * ((1.0 / n1) + (1.0 / n2)));

        return se == 0 ? "-" : ((p1 - p2) / se).ToString("+0.00;-0.00");
    }
}
