using System.Globalization;

namespace GG2.Host;

/// <summary>
/// <c>gg2-server cellgen</c>: writes duel maps — many small arenas cut from the
/// shipped maps — with no game running. <c>docs/DUEL.md</c>.
/// </summary>
internal static class CellGen
{
    public static int Run(string[] args)
    {
        var sourceDirectory = Flag(args, "--maps") ?? GG2.Server.MapDirectory.Resolve();
        if (sourceDirectory.Length == 0)
        {
            Console.Error.WriteLine("no map directory: pass --maps, or run from a checkout with maps/");
            return 1;
        }

        var outDirectory = Flag(args, "--out") ?? DefaultOutput();
        var check = args.Contains("--check");

        List<(string Name, CellMapOptions Options)> maps;
        if (Flag(args, "--name") is { } name)
        {
            var size = (Flag(args, "--cell") ?? "150x100").Split('x');
            maps =
            [
                (name, new CellMapOptions(
                    Seed: Int(args, "--seed", 1),
                    Cells: Int(args, "--cells", 24),
                    CellWidth: int.Parse(size[0], CultureInfo.InvariantCulture),
                    CellHeight: int.Parse(size[1], CultureInfo.InvariantCulture),
                    Gap: Int(args, "--gap", 70))),
            ];
        }
        else
        {
            maps = [.. CellMapGenerator.Pool];
        }

        var sources = CellMapGenerator.LoadSources(sourceDirectory);
        var differ = 0;

        foreach (var (mapName, options) in maps)
        {
            var map = CellMapGenerator.Generate(sources, options);
            var png = map.ToPng();
            var path = Path.Combine(outDirectory, mapName + ".png");

            var current = File.Exists(path) && SameMap(File.ReadAllBytes(path), map);
            if (check)
            {
                if (!current)
                    differ++;

                Console.WriteLine($"{mapName}: {(current ? "current" : "DIFFERS")}");
                continue;
            }

            if (!current)
            {
                Directory.CreateDirectory(outDirectory);
                File.WriteAllBytes(path, png);
            }

            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{mapName}: {map.Cells.Count} cells, {map.Width}x{map.Height} mask ({map.Width * 6}x{map.Height * 6} px), "
                + $"{png.Length / 1024.0:0.0} KB{(current ? ", unchanged" : "")}"));

            if (args.Contains("--verbose"))
            {
                foreach (var cell in map.Cells)
                {
                    Console.WriteLine(string.Create(
                        CultureInfo.InvariantCulture,
                        $"  {cell.Source} @{cell.SourceX},{cell.SourceY}  open {cell.OpenCells}  red {cell.Red}  blue {cell.Blue}"));
                }
            }
        }

        return differ == 0 ? 0 : 1;
    }

    /// <summary>
    /// Whether a file on disk is this map — by its level data and art, not its
    /// bytes, so a different zlib build producing a different deflate stream
    /// does not read as a different map.
    /// </summary>
    public static bool SameMap(byte[] png, CellMap map)
    {
        try
        {
            var art = MapPng.DecodeRgb(png);
            return GG2.Maps.LevelData.Extract(png) == map.LevelData
                && art.Width == map.Width
                && art.Height == map.Height
                && art.Rgb.AsSpan().SequenceEqual(map.Rgb);
        }
        catch (Exception e) when (e is InvalidDataException or GG2.Maps.MapFormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// <c>duel/</c> under the nearest <c>maps/</c> at or above the working
    /// directory — the checkout's, the way navgen finds <c>nav/</c>. NOT under
    /// the source directory: <c>MapDirectory.Resolve</c> prefers the copy beside
    /// the binary, and a pool written into <c>bin/</c> is one nobody checks in.
    /// </summary>
    private static string DefaultOutput()
    {
        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "maps");

            if (Directory.Exists(candidate))
                return Path.Combine(candidate, "duel");
        }

        return Path.Combine(Directory.GetCurrentDirectory(), "maps", "duel");
    }

    private static string? Flag(string[] args, string name)
    {
        var at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    private static int Int(string[] args, string name, int fallback) =>
        Flag(args, name) is { } text ? int.Parse(text, CultureInfo.InvariantCulture) : fallback;

    public static void PrintUsage() => Console.WriteLine(
        """
        gg2-server cellgen - write duel maps: many small arenas cut from the shipped maps

        With no --name, writes the checked-in pool (CellMapGenerator.Pool).

          --name <map>       write one map with the options below instead
          --seed <n>         drives every choice; same options, same map   (1)
          --cells <n>        duel cells; cell i holds red and blue spawn i (24)
          --cell <w>x<h>     the window cut per cell, in 6 px mask cells   (150x100)
          --gap <n>          solid between cells, in mask cells; must beat
                             the 65 px blast radius, which ignores walls   (70)
          --maps <path>      where the shipped source maps live  [GG2_MAPS, or maps/]
          --out <dir>        where the maps go   [duel/ in the nearest maps/ at or above here]
          --check            compare against what is on disk and write nothing;
                             exit 1 if any map differs or is missing
          --verbose          list each cell's source window and spawns

        Then build their nav graphs:
          gg2-server navgen --maps maps/duel --out nav/duel
        """);
}
