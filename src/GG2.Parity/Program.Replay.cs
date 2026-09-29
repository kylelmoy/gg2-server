using System.Globalization;

namespace GG2.Parity;

internal static partial class Program
{
    private static int Replay(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("replay needs a trace file");
            return 2;
        }

        var trace = GG2.Protocol.Trace.Decode(File.ReadAllBytes(args[0]));
        Console.WriteLine($"{args[0]}: {trace.Header}");

        if (trace.Header.ReferenceVersion != GG2.Protocol.GameConstants.Version)
        {
            Console.Error.WriteLine(
                $"trace was recorded against reference version {trace.Header.ReferenceVersion}, " +
                $"this build targets {GG2.Protocol.GameConstants.Version}");
            return 1;
        }

        var map = LoadMap(trace.Header.Map, Flag(args, "--maps"));
        if (map is null)
            return 1;

        // ⚠️ NARROWED, and the stream cannot be read without it. A tall map is
        // several rounds stacked vertically and only one area's entities exist:
        // cp_dirtbowl writes six control points and plays two. The HUD block
        // carries no count prefix, so a reader framing by the wrong number
        // desynchronises on the first update — measured, on
        // reference-cp_dirtbowl-closedcp.trace, which stops at byte 458 of
        // 138,406 un-narrowed and frames to the last byte narrowed.
        //
        // Area 1 because a recording is of a fresh round; a recording whose
        // area advances mid-stream would change the count underneath the reader
        // and is not handled. See docs/PARITY.md.
        var shape = GG2.Maps.GameModes.ShapeOf(map.ForArea(1));

        // Re-shaped on CHANGE_MAP: a recording can rotate onto a map of another
        // gamemode, which changes the HUD block's length underneath the reader.
        var ticks = GG2.Protocol.TraceParser.Parse(
            trace, shape, out var start, out _, ShapeForMap(Flag(args, "--maps")));

        Console.WriteLine(
            $"{ticks.Count} ticks, {shape.GameMode}" +
            (start is null ? ", no FULL_UPDATE anchor" : $", anchored on a {start.PlayerCount}-player FULL_UPDATE"));

        // --empty replays against the do-nothing stand-in, which is how the
        // harness was shown to report a failure before there was any physics to
        // disagree with.
        var useEmpty = args.Contains("--empty");
        var mapDirectory = Flag(args, "--maps");

        ITraceSimulation Build() => useEmpty
            ? new EmptySimulation()
            : new PortedSimulation(name => LoadMap(name, mapDirectory)
                ?? throw new IOException($"cannot load {name}"));

        // Phase 4 owns trajectories; ammo and the flag byte belong to phases 5
        // and 6, and holding a simulation with no weapons in it to an ammo
        // count reports a wall of failures that say nothing.
        var compare = args.Contains("--all-fields") ? Fields.All : Fields.Trajectory | Fields.Hp;

        var failed = false;

        foreach (var mode in new[] { ReplayMode.ClosedLoop, ReplayMode.OpenLoop })
        {
            // --no-stop keeps a closed-loop replay going past its first bad
            // tick. Normally that is noise — one divergence produces a mismatch
            // on every tick after it — but it is the way to tell a real maths
            // error from anchor quantisation: a maths error diverges, and
            // quantisation stays bounded.
            // --dump-slot <n> [--dump-from a] [--dump-to b] prints the PORT's own
            // per-tick state for one slot. A trace has positions only every
            // seventh tick, so when a divergence appears at one comparison and
            // the previous one was exact, the error was made in the six ticks
            // between and the trace cannot say which. This shows what the port
            // did across that gap.
            var dumpSlot = Flag(args, "--dump-slot");

            var report = new Replayer(Build())
                {
                    Compare = compare,
                    DumpSlot = dumpSlot is null
                        ? null
                        : int.Parse(dumpSlot, CultureInfo.InvariantCulture),
                    DumpFrom = int.Parse(
                        Flag(args, "--dump-from") ?? "0", CultureInfo.InvariantCulture),
                    DumpTo = int.Parse(
                        Flag(args, "--dump-to") ?? int.MaxValue.ToString(CultureInfo.InvariantCulture),
                        CultureInfo.InvariantCulture),
                }
                .Replay(args[0], trace.Header.Map, ticks, mode, stopAtFirstBadTick: !args.Contains("--no-stop"));

            Console.WriteLine();
            Console.Write(report.Render());
            failed |= !report.Passed;
        }

        return failed ? 1 : 0;
    }

    /// <summary>
    /// Resolves a map name to its wire shape, for following a
    /// <c>CHANGE_MAP</c> onto a map of another gamemode.
    /// </summary>
    /// <remarks>
    /// ⚠️ Narrowed to area 1, like every other shape here — a tall map plays one
    /// area's entities and the control-point block has no count prefix. A map
    /// that cannot be loaded falls back to an empty shape rather than throwing,
    /// because a framing report is more useful than a stack trace.
    /// </remarks>
    private static Func<string, GG2.Protocol.WorldShape> ShapeForMap(string? mapDirectory) =>
        name =>
        {
            var level = LoadMap(name, mapDirectory);
            return level is null
                ? default
                : GG2.Maps.GameModes.ShapeOf(level.ForArea(1));
        };

    private static GG2.Maps.LevelMap? LoadMap(string map, string? mapDirectory)
    {
        var directory = mapDirectory ?? DefaultMapDirectory();
        var png = Path.Combine(directory, map + ".png");

        if (File.Exists(png))
            return GG2.Maps.LevelMap.FromPng(map, File.ReadAllBytes(png));

        Console.Error.WriteLine($"no map at {png}; pass --maps or set GG2_REFERENCE");
        return null;
    }

    /// <summary>
    /// Frames what was recorded and prints a message census.
    /// </summary>
    /// <remarks>
    /// Worth doing at record time rather than later: a trace that cannot be
    /// framed is not a trace, and finding that out while the server is still
    /// running is much cheaper than finding it out in CI.
    /// </remarks>
    private static void Summarise(byte[] trace, string map, string? mapDirectory)
    {
        var directory = mapDirectory ?? DefaultMapDirectory();

        var png = Path.Combine(directory, map + ".png");
        if (!File.Exists(png))
        {
            Console.WriteLine($"(no framing: {png} not found; pass --maps)");
            return;
        }

        var shape = GG2.Maps.GameModes.ShapeOf(
            GG2.Maps.LevelMap.FromPng(map, File.ReadAllBytes(png)).ForArea(1));
        var stream = new GG2.Protocol.ServerStream();
        stream.SetShape(shape);

        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var reader = new GG2.Protocol.NetReader(trace);

        try
        {
            while (reader.Remaining > 0)
            {
                var message = stream.Read(ref reader);
                var name = message.Type.ToString();
                counts[name] = counts.GetValueOrDefault(name) + 1;
            }
        }
        catch (GG2.Protocol.ProtocolException e)
        {
            Console.WriteLine($"framing stopped at byte {reader.Position} of {trace.Length}: {e.Message}");
            return;
        }

        Console.WriteLine($"{shape.GameMode}, {shape.ControlPointCount} control point(s), " +
                          $"{shape.MovingPlatformCount} platform(s); framed cleanly to the last byte");

        foreach (var (name, count) in counts)
            Console.WriteLine($"  {count,5}  {name}");
    }
}
