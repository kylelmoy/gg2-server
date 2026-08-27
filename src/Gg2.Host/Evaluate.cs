using System.Globalization;
using Gg2.Bots;
using Gg2.Maps;

namespace Gg2.Host;

/// <summary>
/// <c>gg2-server eval</c>: plays bot matches across maps and reports what
/// happened, with nobody watching.
/// </summary>
/// <remarks>
/// <para>
/// This is phase 8's exit criterion made runnable — "a bot change can be
/// evaluated across every map without a human watching, faster than real time".
/// It is a thin front end over <see cref="MatchRunner"/>; the substance, and
/// the determinism guarantee, are there.
/// </para>
/// <para>
/// ⚠️ <b>A map with no nav cache is reported, not skipped silently.</b> Bots
/// with no graph do not walk anywhere, so on any full-size map the two teams
/// never meet and every match comes back <c>0-0, unfinished</c> — which reads
/// exactly like a bot change that broke capturing. The caches are gitignored in
/// the reference and are not shipped, so this is the common case on a fresh
/// checkout rather than an exotic one.
/// </para>
/// </remarks>
internal static class Evaluate
{
    public static int Run(string[] args)
    {
        var mapDirectory = Flag(args, "--maps")
            ?? (Environment.GetEnvironmentVariable("GG2_REFERENCE") is { Length: > 0 } reference
                ? Path.Combine(reference, "Source", "gg2", "Included Files")
                : @"D:\code\Gang-Garrison-2\Source\gg2\Included Files");

        var navDirectory = Flag(args, "--botnav");

        var matches = Int(args, "--matches", 4);
        var threads = Int(args, "--threads", Environment.ProcessorCount);

        var options = HeadlessMatchOptions.Default with
        {
            Bots = Int(args, "--bots", HeadlessMatchOptions.Default.Bots),
            Difficulty = Int(args, "--bot-difficulty", HeadlessMatchOptions.Default.Difficulty),
            MaxTicks = Int(args, "--max-ticks", HeadlessMatchOptions.Default.MaxTicks),
            Seed = (uint)Int(args, "--seed", 1),
        };

        string[] maps = Flag(args, "--map") is { Length: > 0 } one
            ? [one]
            : Directory
                .EnumerateFiles(mapDirectory, "*.png")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(name => name is { Length: > 0 })
                .Select(name => name!)
                .Order(StringComparer.Ordinal)
                .ToArray();

        if (maps.Length == 0)
        {
            Console.Error.WriteLine($"no maps in {mapDirectory}");
            return 1;
        }

        Console.WriteLine(
            $"{maps.Length} map(s) x {matches} match(es), {options.Bots} bots, "
            + $"seed {options.Seed}, {threads} thread(s)");
        Console.WriteLine();
        Console.WriteLine($"{"map",-26} {"red",4} {"blue",4} {"drew",4} {"unfin",5} {"mean ticks",10}  nav");

        var clock = System.Diagnostics.Stopwatch.StartNew();
        long totalTicks = 0;

        foreach (var name in maps)
        {
            LevelMap map;
            try
            {
                var png = File.ReadAllBytes(Path.Combine(mapDirectory, name + ".png"));
                map = LevelMap.FromPng(name, png).ForArea(options.Area);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException)
            {
                Console.WriteLine($"{name,-26} {"-",4} {"-",4} {"-",4} {"-",5} {"-",10}  unreadable: {e.Message}");
                continue;
            }

            NavGraph? nav = null;
            if (navDirectory is { Length: > 0 }
                && NavCache.TryLoad(navDirectory, name, options.Area, out var loaded))
            {
                nav = loaded;
            }

            var results = MatchRunner.Run(options with { Map = name }, matches, map, nav, threads);
            var summary = MatchRunner.Summarise(results);
            totalTicks += summary.TotalTicks;

            Console.WriteLine(
                $"{name,-26} {summary.RedWins,4} {summary.BlueWins,4} {summary.Draws,4} "
                + $"{summary.Exhausted,5} {summary.MeanTicks,10:0}  {(nav is null ? "MISSING" : "ok")}");
        }

        clock.Stop();

        var seconds = Math.Max(clock.Elapsed.TotalSeconds, 0.001);
        Console.WriteLine();
        Console.WriteLine(
            $"{totalTicks} ticks of match in {seconds:0.0}s = "
            + $"{totalTicks / seconds / 30:0}x real time on {threads} thread(s)");

        return 0;
    }

    private static string? Flag(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static int Int(string[] args, string name, int fallback) =>
        Flag(args, name) is { } text
            ? int.Parse(text, CultureInfo.InvariantCulture)
            : fallback;

    public static void PrintUsage() => Console.WriteLine(
        """
        gg2-server eval - play bot matches across maps and report the results

          --map <name>      one map (default: every map in --maps)
          --matches <n>     matches per map (default 4)
          --threads <n>     matches at once (default: processor count)
          --bots <n>        bots per match (default 12)
          --bot-difficulty <1-5>
          --max-ticks <n>   ceiling per match (default 36000, twenty minutes)
          --seed <n>        batch seed (default 1)
          --maps <path>     where the built-in map PNGs live
          --botnav <path>   where the bot nav caches live

        Results do not depend on --threads: match i is seeded from its index, so
        it is the same match whether it ran first, last or concurrently. That is
        checked by HeadlessMatchTests rather than asserted here.

        Without --botnav the bots do not walk anywhere, so on a full-size map the
        teams never meet and every match comes back 0-0 and unfinished. The nav
        column says which maps had a cache.
        """);
}
