using System.Collections.Concurrent;
using System.Globalization;
using GG2.Bots;
using GG2.Protocol;
using GG2.Server;
using GG2.Simulation;

namespace Bench;

/// <summary>
/// <c>BENCH_DUEL_LADDER</c>: how much weaker each duel level is, in the duel
/// server's own setting.
/// </summary>
/// <remarks>
/// <b>What the duel server builds</b>: <c>maps/duel</c>, the duel nets sampled and
/// always driving, every bot wrapped in <see cref="HumanizedBot.ByTier"/> as
/// <c>[Bots] HumanAim</c> does, its skill <see cref="BotLevel.Skill"/> of the
/// level. The subject plays each level in <c>BENCH_DUEL_LEVELS</c> (default 1-10;
/// fractions allowed) against an opponent at level 10, same class, on the same
/// placements, so every level is paired per duel against the mirror,
/// which must read 0.5. Every class a duel net drives, cycling per duel.
/// <para>
/// Scored as <c>BENCH_TIERDUEL</c> is (a kill 1, else the share of damage dealt)
/// to the first death or <c>BENCH_DUEL_TICKS</c> (900). <b>This is a bot against
/// a bot</b>: it says how evenly apart the levels are, not where a person
/// settles on them.
/// </para>
/// </remarks>
internal static class DuelLevels
{
    public static void Run(string maps, string nav, string checkpoints, int perMap)
    {
        var cap = int.Parse(Environment.GetEnvironmentVariable("BENCH_DUEL_TICKS") ?? "900", CultureInfo.InvariantCulture);
        double[] levels = Environment.GetEnvironmentVariable("BENCH_DUEL_LEVELS") is { Length: > 0 } listed
            ? [.. listed.Split(',').Select(t => double.Parse(t, CultureInfo.InvariantCulture))]
            : [10, 9, 8, 7, 6, 5, 4, 3, 2, 1];
        // BENCH_DUEL_OPPONENT sets the opponent's level (default 10); the subject's
        // levels are paired against the subject at that same level, the mirror.
        var opponent = double.Parse(Environment.GetEnvironmentVariable("BENCH_DUEL_OPPONENT") ?? "10", CultureInfo.InvariantCulture);
        if (!levels.Contains(opponent))
            levels = [opponent, .. levels];

        // BENCH_DUEL_HUMANIZE=0 leaves the wrap off, to see what it costs.
        var humanize = Environment.GetEnvironmentVariable("BENCH_DUEL_HUMANIZE") != "0";

        // BENCH_DUEL_PRESET swaps the wrap's preset (HumanizeOptions.ForPreset) and
        // BENCH_DUEL_JUMPKEEP its share of jump presses kept, to split its cost.
        var preset = Environment.GetEnvironmentVariable("BENCH_DUEL_PRESET") is { Length: > 0 } named
            ? HumanizeOptions.ForPreset(named) : HumanizeOptions.Tracking9;
        if (Environment.GetEnvironmentVariable("BENCH_DUEL_JUMPKEEP") is { Length: > 0 } keep)
            preset = preset with { JumpKeep = double.Parse(keep, CultureInfo.InvariantCulture) };
        // BENCH_SEED_BASE moves every placement, so a tuning can be checked on duels it never saw.
        var seedBase = int.Parse(Environment.GetEnvironmentVariable("BENCH_SEED_BASE") ?? "9900", CultureInfo.InvariantCulture);
        var mapDirectory = Path.Combine(maps, "duel");
        var navDirectory = Path.Combine(nav, "duel");
        string[] list = [.. Enumerable.Range(1, 8).Select(n => $"duel_{n:00}")];

        static BotBrainSpec Always(MicroControllerSpec[] specs) => BotBrainSpec.Of(specs) with
        {
            Rule = HandoverRule.Distance,
            Range = 100_000,
            ReleaseRange = 100_000,
        };

        var specs = Nets.Duel(Path.Combine(checkpoints, "duel"));
        var classes = Nets.Classes(specs);
        var policy = new ThreadLocal<BotBrainSpec>(() => Always(Nets.Duel(Path.Combine(checkpoints, "duel"))));
        var tables = new ConcurrentDictionary<string, Lazy<NavDistanceSet?>>();
        var work = from map in list from i in Enumerable.Range(0, perMap) from level in levels select (map, i, level);
        var scores = new ConcurrentDictionary<(string Map, int I, double Level), double>();

        Console.WriteLine(
            $"{list.Length} duel maps x {perMap} duels x levels {string.Join(",", levels.Select(l => l.ToString(CultureInfo.InvariantCulture)))}, "
            + $"classes {string.Join(",", classes)} cycling, opponent level {opponent.ToString(CultureInfo.InvariantCulture)}, {cap}-tick cap, duel nets always driving, human aim {(humanize ? "by skill" : "OFF")}");

        Parallel.ForEach(work, item =>
        {
            var (map, i, level) = item;
            var playerClass = classes[i % classes.Length];
            var subjectTeam = (i / classes.Length) % 2 == 0 ? Team.Red : Team.Blue;
            var (world, red, blue) = Tiers.PlaceInSight(
                mapDirectory, navDirectory, map, (uint)(seedBase + (map.Length * 131) + (i * 37)), playerClass);
            var graph = world.Nav;
            var distances = tables.GetOrAdd(map, m => new Lazy<NavDistanceSet?>(() =>
                graph is null ? null : NavDistanceSet.LoadOrDerive(navDirectory, m, 1, graph, out _))).Value;

            var subject = subjectTeam == Team.Red ? red : blue;
            var other = subjectTeam == Team.Red ? blue : red;
            var otherTeam = subjectTeam == Team.Red ? Team.Blue : Team.Red;

            IInputSource Bot(Team team, int slot, double rating)
            {
                IInputSource bot = policy.Value!.Wrap(
                    new ScriptedBot(world.Nav, new NavQuery(team), BotLevel.Skill(rating), slot) { Distances = distances });
                return humanize ? HumanizedBot.ByTier(bot, (uint)slot, preset) : bot;
            }

            world.Drive(subject, Bot(subjectTeam, subject, level));
            world.Drive(other, Bot(otherTeam, other, opponent));

            bool subjectDied = false, otherDied = false;
            var ticks = 0;
            var subjectStart = world.State.Characters[subject].Hp;
            var otherStart = world.State.Characters[other].Hp;

            while (ticks < cap && !subjectDied && !otherDied)
            {
                world.Step();
                ticks++;
                subjectDied = world.Events.Died[subject];
                otherDied = world.Events.Died[other];
            }

            var subjectLost = subjectDied ? 1.0 : 1 - (world.State.Characters[subject].Hp / subjectStart);
            var otherLost = otherDied ? 1.0 : 1 - (world.State.Characters[other].Hp / otherStart);
            var score = subjectDied != otherDied ? (otherDied ? 1.0 : 0.0)
                : subjectDied || subjectLost + otherLost <= 0 ? 0.5
                : otherLost / (subjectLost + otherLost);

            scores[(map, i, level)] = score;
        });

        Console.WriteLine();
        Console.WriteLine("subject's duel score (kill 1, else damage share), paired per duel against the mirror; ±95%; then the score by class");
        Console.WriteLine($"{"level",6} {"duels",6} {"score",7} {"vs mirror",9} {"±",6} {"step",7}   " + string.Join(" ", classes.Select(c => c.ToString()[..Math.Min(5, c.ToString().Length)].PadLeft(5))));

        double? previous = null;
        foreach (var level in levels.OrderByDescending(l => l))
        {
            var keys = (from map in list from i in Enumerable.Range(0, perMap) select (map, i)).ToList();
            var here = keys.Select(k => scores[(k.map, k.i, level)]).ToList();
            var diffs = keys.Select(k => scores[(k.map, k.i, level)] - scores[(k.map, k.i, opponent)]).ToList();
            var mean = diffs.Average();
            var sd = Math.Sqrt(diffs.Sum(d => (d - mean) * (d - mean)) / Math.Max(1, diffs.Count - 1));
            var step = previous is { } p ? (here.Average() - p).ToString("+0.000;-0.000", CultureInfo.InvariantCulture) : "";

            var byClass = classes.Select((c, ci) => keys.Where(k => k.i % classes.Length == ci).Select(k => scores[(k.map, k.i, level)]).DefaultIfEmpty(double.NaN).Average());

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{level,6:0.##} {here.Count,6} {here.Average(),7:0.000} {mean,9:+0.000;-0.000} {1.96 * sd / Math.Sqrt(diffs.Count),6:0.000} {step,7}   {string.Join(" ", byClass.Select(v => v.ToString("0.00", CultureInfo.InvariantCulture).PadLeft(5)))}"));
            previous = here.Average();
        }
    }
}
