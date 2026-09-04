using System.Globalization;
using Gg2.Bots;
using Gg2.Maps;
using Gg2.Nav;

namespace Gg2.Host;

/// <summary>
/// <c>gg2-server navgen</c>: build bot nav graphs for maps, with no game running.
/// </summary>
/// <remarks>
/// The generator used to live in the game, on a server tick, and then in
/// <c>gg2-nav-gen</c>, a Node program beside a Game Maker checkout. It is here now
/// because <see cref="NavGenerator"/> is: everything a graph needs is in the map's
/// own PNG, so the same binary that serves a round can build the graphs it serves
/// it with, and a clone of this repository needs nothing else.
/// <para>
/// ⚠️ <b>This does the WHOLE cycle, which the previous workflow did not.</b>
/// <c>gg2-nav-gen</c> wrote <c>&lt;key&gt;.txt</c> into the directory this
/// repository ships <c>.txt.gz</c> in, and two further steps —
/// <c>tools/compress-nav.sh</c> and a manifest re-record — were needed before the
/// tree was consistent again. Skipping the second left the old pairing recorded
/// and the new graphs refused at run time, which looks exactly like a bot bug.
/// Here one command writes the shipped container, deletes the loser, and rewrites
/// the manifest.
/// </para>
/// <para>
/// ⚠️ <b>An unchanged graph is not rewritten</b>, so <c>git status</c> after a run
/// names the graphs a source edit actually moved. That is the question a
/// regeneration is asked to answer, and it is the one <c>--check</c> answers
/// without writing at all.
/// </para>
/// </remarks>
internal static class NavGen
{
    public static int Run(string[] args)
    {
        var mapDirectory = Flag(args, "--maps") ?? Gg2.Server.MapDirectory.Resolve();

        if (mapDirectory.Length == 0)
        {
            Console.Error.WriteLine("no map directory: pass --maps, or run from a checkout with maps/");
            return 1;
        }

        var outDirectory = Flag(args, "--out") ?? DefaultOutput();

        var check = args.Contains("--check");

        // ⚠️ ON by default, because the SERVER now expects the tables to be
        // there: NavDistanceSet.LoadOrDerive falls back to deriving, so a run
        // without them is slower at every map change rather than broken, and a
        // generator that quietly stopped writing them would look fine here and
        // cost 3-251 ms a map change in production. --no-distances is for
        // rebuilding graphs alone when the tables are known to be current.
        var distances = !args.Contains("--no-distances");
        var sticky = args.Contains("--sticky");
        var plain = args.Contains("--plain");
        var quiet = args.Contains("--quiet");

        // ⛔ A cache carrying NAV_EDGE_STICKYJUMP must never reach the GML game,
        // whose navFindPath has no canSticky and cannot refuse type 7 by class —
        // it would route every class over an arc only a Demoman can fly. This port
        // refuses them per query, so the flag is safe HERE and nowhere else, and
        // saying so at the point of writing is cheaper than a note in a document.
        if (sticky && !quiet)
        {
            Console.WriteLine(
                "--sticky: these graphs carry NAV_EDGE_STICKYJUMP edges. Only a search that "
                + "refuses them by class may load them — this server does; the Game Maker "
                + "client's own bot search does not.");
        }

        var only = Flag(args, "--map");
        var oneArea = Flag(args, "--area") is { } text
            ? int.Parse(text, CultureInfo.InvariantCulture)
            : (int?)null;

        var maps = only is { Length: > 0 }
            ? [only]
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

        // Every graph's fingerprint, so the manifest can be rewritten whole. A
        // partial run writes only the lines it built and would otherwise DROP the
        // rest — a manifest with a graph missing from it is a graph nobody checks,
        // which is the silent half of the failure this whole mechanism exists to
        // make loud. So a partial run leaves the existing lines alone and merges.
        var fingerprints = new List<(string Key, string Hash)>();
        var stale = new List<string>();
        var written = 0;
        var unchanged = 0;
        var failed = 0;
        var tablesWritten = 0;
        var tablesUnchanged = 0;
        var tableBytes = 0L;

        foreach (var name in maps)
        {
            LevelMap map;

            try
            {
                map = LoadMap(mapDirectory, name);
            }
            catch (Exception e) when (e is IOException or FormatException or InvalidDataException)
            {
                Console.Error.WriteLine($"{name}: {e.Message}");
                failed++;
                continue;
            }

            var areas = NavGenerator.AreaCount(map);

            if (oneArea is { } wanted && (wanted < 1 || wanted > areas))
            {
                Console.Error.WriteLine(
                    $"{name} has {areas} area{(areas == 1 ? "" : "s")}; --area {wanted} is out of range");
                failed++;
                continue;
            }

            for (var area = oneArea ?? 1; area <= (oneArea ?? areas); area++)
            {
                // ⚠️ Sliced ONCE, and the same slice feeds both. NavGenerator.Build
                // and NavManifest.HashOf take the area's map rather than the whole
                // one, so the graph and the fingerprint recorded beside it are
                // built from the same entities by construction.
                var stage = map.ForArea(area);
                var built = NavGenerator.Build(stage, area, sticky);
                fingerprints.Add((built.Key, NavManifest.HashOf(stage)));

                if (check)
                {
                    // ⚠️ Keyed on the BUILD's key, not on `name` — which may be a
                    // path to a PNG rather than a map name, and then names no cache
                    // file at all. NavCacheWriter.Write has the same key; a --check
                    // that looked somewhere else would report every custom map as
                    // MISSING and exit 1.
                    var existing = NavCache.FindFile(outDirectory, stage.Name, area);
                    var same = existing.Length > 0
                        && string.Equals(NavCacheWriter.ReadText(existing), built.Text, StringComparison.Ordinal);

                    if (!same)
                        stale.Add(built.Key);

                    var tableNote = string.Empty;

                    if (distances)
                    {
                        // Against the graph JUST BUILT, not the one on disk: a
                        // table that matches a stale graph is exactly the drift
                        // this check exists to catch.
                        var current = NavDistanceFile.IsCurrent(
                            NavDistanceFile.PathFor(outDirectory, stage.Name, area),
                            NavCache.Parse(built.Text, stage.Name, area));

                        if (!current)
                        {
                            stale.Add(built.Key + NavDistanceFile.Extension);
                            tableNote = ", tables STALE";
                        }
                    }

                    Report(quiet, built, (existing.Length == 0 ? "MISSING" : same ? "match" : "DIFFERS") + tableNote);
                    continue;
                }

                var result = NavCacheWriter.Write(outDirectory, built.Key, built.Text, compress: !plain);

                if (result.Changed)
                    written++;
                else
                    unchanged++;

                var note = result.Changed ? Path.GetFileName(result.Path) : "unchanged";

                if (distances)
                {
                    var graph = NavCache.Parse(built.Text, stage.Name, area);
                    var path = NavDistanceFile.PathFor(outDirectory, stage.Name, area);

                    // Skipped when the file already names this exact graph —
                    // deriving 100 tables across the shipped maps is ~2.2 s, and
                    // a no-op run should not pay it.
                    if (NavDistanceFile.IsCurrent(path, graph))
                    {
                        tablesUnchanged++;
                        note += ", tables unchanged";
                    }
                    else
                    {
                        var set = NavDistanceSet.Derive(graph);
                        NavDistanceFile.Write(path, graph, set);
                        tablesWritten++;
                        note += $", {set.TableCount} tables";
                    }

                    tableBytes += new FileInfo(path).Length;
                }

                Report(quiet, built, note);
            }
        }

        if (check)
        {
            Console.WriteLine(stale.Count == 0
                ? $"{fingerprints.Count} graphs match {outDirectory}"
                : $"{stale.Count} of {fingerprints.Count} graphs differ from {outDirectory}: "
                    + string.Join(", ", stale));

            return failed > 0 || stale.Count > 0 ? 1 : 0;
        }

        WriteManifest(outDirectory, fingerprints);

        Console.WriteLine(
            $"{written} written, {unchanged} unchanged -> {outDirectory}"
            + (distances
                ? $"; tables {tablesWritten} written, {tablesUnchanged} unchanged, "
                    + $"{tableBytes / 1048576.0:0.00} MB"
                : "; tables skipped (--no-distances)")
            + (failed > 0 ? $", {failed} failed" : string.Empty));

        return failed > 0 ? 1 : 0;
    }

    /// <summary>
    /// Where graphs go when <c>--out</c> is not given.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Deliberately NOT <c>MapDirectory.ResolveNav</c>, which is the
    /// SERVER's answer and is the wrong one here.</b> That probe walks up from the
    /// executable, and <c>nav/</c> is copied into the build output — so a
    /// <c>dotnet run … navgen</c> from the repository would write its graphs into
    /// <c>bin/Debug/net10.0/nav</c>, leave the checked-in ones untouched, and
    /// report success. The graphs would then be right in the one place nobody
    /// looks and stale everywhere else.
    /// <para>
    /// The working directory is the right anchor for a tool: run from the
    /// repository it finds the repository's <c>nav/</c>, and run from a deployed
    /// server's install directory it finds the one beside the binary, because that
    /// is where an operator stands. <c>GG2_NAVCACHE</c> still wins, since it is the
    /// one setting that moves both ends of the seam.
    /// </para>
    /// </remarks>
    private static string DefaultOutput()
    {
        if (Environment.GetEnvironmentVariable("GG2_NAVCACHE") is { Length: > 0 } fromEnv)
            return fromEnv;

        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "nav");

            if (Directory.Exists(candidate))
                return candidate;
        }

        return Path.Combine(Directory.GetCurrentDirectory(), "nav");
    }

    /// <summary>
    /// A map by name from the map directory, or by path for a custom map.
    /// </summary>
    /// <remarks>
    /// A custom map is the case this whole command exists for on a real server, so
    /// <c>--map</c> takes a path as readily as a name: the graphs a server needs
    /// for its own rotation are not in <c>maps/</c>.
    /// </remarks>
    private static LevelMap LoadMap(string mapDirectory, string name)
    {
        var path = File.Exists(name) ? name : Path.Combine(mapDirectory, name + ".png");
        return LevelMap.FromPng(Path.GetFileNameWithoutExtension(path), File.ReadAllBytes(path));
    }

    /// <summary>
    /// Rewrites the manifest, keeping the lines this run did not build.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Merged rather than replaced.</b> A run over one map must not drop the
    /// other twenty-four lines: a graph with no line is a graph nobody checks, and
    /// the whole point of the manifest is to notice a graph that no longer fits its
    /// map. A line this run rebuilt wins over the one already there.
    /// </remarks>
    private static void WriteManifest(string directory, List<(string Key, string Hash)> built)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        var existing = NavManifest.Load(directory);

        foreach (var (map, area) in NavCache.List(directory))
        {
            var key = NavCache.Key(map, area);

            if (existing.HashFor(key) is { } hash)
                merged[key] = hash;
        }

        foreach (var (key, hash) in built)
            merged[key] = hash;

        NavManifest.Write(
            Path.Combine(directory, NavManifest.FileName),
            merged.Select(entry => (entry.Key, entry.Value)));
    }

    private static void Report(bool quiet, in NavBuildResult built, string outcome)
    {
        if (quiet)
            return;

        Console.WriteLine(
            string.Create(CultureInfo.InvariantCulture,
                $"{built.Key,-24} {built.Nodes,5} nodes {built.Edges,6} edges "
                + $"{built.Elapsed.TotalMilliseconds,6:F0}ms  {outcome}")
            + $"  [{built.Describe()}]");
    }

    private static string? Flag(string[] args, string name)
    {
        var at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    public static void PrintUsage() => Console.WriteLine(
        """
        gg2-server navgen - build the bot nav graphs, with no game running

          --map <name|path>  one map, by name in --maps or by path to a PNG
                             (default: every map in --maps)
          --area <n>         one stage of a multi-stage map (default: every stage)
          --maps <path>      where the map PNGs live      [GG2_MAPS, or maps/]
          --out <dir>        where the graphs go          [GG2_NAVCACHE, or the
                             nearest nav/ at or above the working directory]
          --check            compare against what is on disk and write nothing;
                             exit 1 if any graph differs or is missing
          --plain            write <key>.txt instead of the shipped <key>.txt.gz
          --no-distances     do not write the <key>.dist.gz next-hop tables. The
                             server derives them at the map change instead, which
                             costs 3-251 ms a map and changes no routes.
          --sticky           also emit NAV_EDGE_STICKYJUMP (type 7) edges, a
                             Demoman detonating its own mine on the jump tick.
                             This server refuses them by class; the Game Maker
                             client's own bot search cannot, so a cache built with
                             this must never be given to it.
          --quiet            the summary line only

        Writing also rewrites nav/manifest.tsv, which records the map each graph
        was built against. That is not optional bookkeeping: a graph whose line
        disagrees is refused at load, and a graph with no line is not checked at
        all. Lines for graphs this run did not build are kept.

        A graph identical to the one already on disk is not rewritten, so
        `git status` after a run names the graphs a change actually moved.

        The server builds a missing graph itself on a map change, so this is for
        checking graphs in rather than for making a server work — see
        `gg2-server --help`, --navgen.
        """);
}
