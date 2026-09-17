using System.Globalization;
using Gg2.Bots;
using Gg2.Maps;
using Gg2.Nav;

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
/// <b>A map with no nav graph is reported, not skipped silently.</b> Bots with
/// no graph do not walk anywhere, so on any full-size map the two teams never
/// meet, and a run without one produces 0-0 draws at the time limit rather than
/// real captures — a finished match that nothing happened on. So <c>eval</c>
/// builds a missing graph rather than measuring without one, the same as the
/// server, and the nav column says which maps were <c>built</c> rather than
/// read. <c>--no-navgen</c> restores the old behaviour.
/// </para>
/// </remarks>
internal static class Evaluate
{
    public static int Run(string[] args)
    {
        // The same resolution the server itself uses. Note the resolved maps/
        // includes the port-authored gg_debug2, so a no-flag eval covers 23
        // maps, not 22.
        var mapDirectory = Flag(args, "--maps") ?? Gg2.Server.MapDirectory.Resolve();

        if (mapDirectory.Length == 0)
        {
            Console.Error.WriteLine("no map directory: pass --maps, or run from a checkout with maps/");
            return 1;
        }

        // Resolved rather than null so a plain `eval` measures bots that can
        // navigate — the doc comment above names the navless footgun this
        // removes. The per-map nav column still says which maps had a cache.
        var navDirectory = Flag(args, "--botnav")
            ?? (Gg2.Server.MapDirectory.ResolveNav() is { Length: > 0 } resolvedNav ? resolvedNav : null);

        // ⚠️ On by default, matching the server. A map with no graph measured as
        // though it had one is the failure this whole column exists to make
        // visible, and building the graph removes the failure rather than the
        // symptom.
        var noNavGen = args.Contains("--no-navgen");

        var matches = Int(args, "--matches", 4);
        var threads = Int(args, "--threads", Environment.ProcessorCount);

        // Off by default because it is not what eval is for: a digest says
        // whether two runs were the same run, never whether the bots played
        // well. It is a determinism instrument, and reading a behavioural
        // conclusion out of it is the one misuse worth naming.
        var digests = args.Contains("--digest");

        var options = HeadlessMatchOptions.Default with
        {
            Bots = Int(args, "--bots", HeadlessMatchOptions.Default.Bots),
            Difficulty = Int(args, "--bot-difficulty", HeadlessMatchOptions.Default.Difficulty),
            MaxTicks = Int(args, "--max-ticks", HeadlessMatchOptions.Default.MaxTicks),
            Seed = (uint)Int(args, "--seed", 1),

            // ⛔ Changes every route, so it is a property of the ARM and not a
            // free speed-up — a table taken with it is not comparable to one
            // taken without. It is offered here because this is the path where
            // it pays: eval leaves Occupancy null, so A* is the largest single
            // cost in the tick.
            NavDistances = !args.Contains("--no-nav-distances"),
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
        Console.WriteLine(
            $"{"map",-26} {"red",4} {"blue",4} {"drew",4} {"unfin",5} {"mean ticks",10}  "
            + (digests ? $"{"nav",-6}  digest" : "nav"));

        var clock = System.Diagnostics.Stopwatch.StartNew();
        long totalTicks = 0;

        // Folded across maps in the order they ran, so the final number covers
        // the whole batch rather than the last map of it.
        var batch = BatchDigest.Seed;

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
            var navSource = "MISSING";

            if (navDirectory is { Length: > 0 }
                && NavCache.TryLoad(navDirectory, name, options.Area, out var loaded))
            {
                nav = loaded;
                navSource = "ok";
            }
            else if (!noNavGen)
            {
                // ⚠️ The same build the server does at a map change, for the same
                // reason and with more force: a bot with no graph does not walk
                // anywhere, so on a full-size map the teams never meet and every
                // match comes back 0-0 and unfinished — which reads exactly like a
                // bot change that broke capturing. `eval` is where a bot change is
                // MEASURED, so "the map had no graph" has to be the one thing it
                // cannot quietly be measuring. CLAUDE.md: a match that ends is not
                // a match that happened.
                //
                // The column still says which graphs were built rather than read,
                // because a built one is only as good as the generator and that is
                // worth seeing in the results rather than inferring.
                try
                {
                    nav = NavCache.Parse(
                        NavGenerator.Build(map, options.Area).Text, name, options.Area);
                    navSource = "built";
                }
                catch (Exception e) when (e is ArgumentException or FormatException
                    or InvalidOperationException or IndexOutOfRangeException)
                {
                    navSource = "unbuildable";
                    Console.Error.WriteLine($"{name}: could not build a nav graph: {e.Message}");
                }
            }

            var results = MatchRunner.Run(
                options with { Map = name }, matches, map, nav, threads, navDirectory);
            var summary = MatchRunner.Summarise(results);
            totalTicks += summary.TotalTicks;

            var mapDigest = BatchDigest.Of(name, results);
            batch = BatchDigest.Mix(batch, mapDigest);

            Console.WriteLine(
                $"{name,-26} {summary.RedWins,4} {summary.BlueWins,4} {summary.Draws,4} "
                + $"{summary.Exhausted,5} {summary.MeanTicks,10:0}  "
                + (digests ? $"{navSource,-6}  {mapDigest:x16}" : navSource));
        }

        clock.Stop();

        var seconds = Math.Max(clock.Elapsed.TotalSeconds, 0.001);
        Console.WriteLine();
        Console.WriteLine(
            $"{totalTicks} ticks of match in {seconds:0.0}s = "
            + $"{totalTicks / seconds / 30:0}x real time on {threads} thread(s)");

        if (digests)
        {
            // ⚠️ The batch line is the one to compare, not the per-map column:
            // a single map agreeing says nothing about the other twenty-two,
            // and this covers the maps that ran AND the order they ran in.
            // Two runs of the same flags that disagree here did not play the
            // same matches, whatever their scoreboards say.
            Console.WriteLine($"batch digest {batch:x16}");
        }

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
                            how good they are (default 5)
          --max-ticks <n>   ceiling per match (default 36000, twenty minutes)
          --seed <n>        batch seed (default 1)
          --digest          print a 64-bit digest per map and one for the whole
                            batch: every character's position, velocity and
                            health at the end of every match, bit for bit. Two
                            runs of the same flags agree or something is not
                            deterministic. It says nothing about how well the
                            bots played.
          --maps <path>     where the built-in map PNGs live
          --botnav <path>   where the bot nav caches live
          --no-navgen       do not build a graph for a map that has none
          --no-nav-distances
                            turn OFF the exact precomputed A* heuristic, which
                            is on by default. Slower AND different routes.
                            ⛔ CHANGES EVERY ROUTE - the search is weighted, so
                            this is an arm, not a speed-up. Not comparable to a
                            table taken without it.

        Results do not depend on --threads: match i is seeded from its index, so
        it is the same match whether it ran first, last or concurrently. That is
        checked by HeadlessMatchTests rather than asserted here.

        The nav column says where each map's graph came from: `ok` read from
        --botnav, `built` generated on the spot because there was none, `MISSING`
        with --no-navgen and no cache. ⚠️ MISSING means the bots do not walk
        anywhere, so on a full-size map the teams never meet and every match comes
        back 0-0 and unfinished — which reads exactly like a bot change that broke
        capturing.
        """);
}
