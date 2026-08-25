using System.Globalization;
using Gg2.Parity;

namespace Gg2.Parity;

/// <summary>
/// <c>gg2-parity</c>: record a server's broadcast stream, and diff two
/// recordings.
/// </summary>
/// <remarks>
/// docs/PARITY.md, tier 0. The intended use is to point this at the reference
/// GM8 server and at this port in turn and compare — which turns "the client
/// does not like my stream" from a guess into a byte offset.
/// </remarks>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            Usage();
            return args.Length == 0 ? 2 : 0;
        }

        try
        {
            return args[0] switch
            {
                "record" => Record(args[1..]),
                "diff" => Diff(args[1..]),
                "replay" => Replay(args[1..]),
                "census" => Census(args[1..]),
                _ => Unknown(args[0]),
            };
        }
        catch (Exception e) when (e is IOException or TimeoutException or InvalidOperationException)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"unknown command '{command}'");
        Usage();
        return 2;
    }

    private static int Record(string[] args)
    {
        var host = Flag(args, "--host") ?? "127.0.0.1";
        var port = int.Parse(Flag(args, "--port") ?? "8190", CultureInfo.InvariantCulture);
        var seconds = double.Parse(Flag(args, "--seconds") ?? "5", CultureInfo.InvariantCulture);
        var name = Flag(args, "--name") ?? "recorder";
        var output = Flag(args, "--out") ?? "trace.bin";

        using var recorder = new Recorder(host, port);
        recorder.Join(name, TimeSpan.FromSeconds(10));

        Console.WriteLine($"joined \"{recorder.ServerName}\" on {recorder.Map} " +
                          $"(md5 {(recorder.MapMd5.Length == 0 ? "<built-in>" : recorder.MapMd5)})");

        recorder.RecordFor(TimeSpan.FromSeconds(seconds));

        var bytes = recorder.AllBytes();

        // A .trace gets the header that makes it replayable; anything else is
        // written raw, which is what a byte diff against another server wants.
        if (output.EndsWith(".trace", StringComparison.OrdinalIgnoreCase))
        {
            var trace = new Gg2.Protocol.Trace
            {
                Header = new Gg2.Protocol.TraceHeader
                {
                    FormatVersion = Gg2.Protocol.TraceHeader.CurrentFormatVersion,
                    ReferenceVersion = Gg2.Protocol.GameConstants.Version,
                    Map = recorder.Map,
                    MapMd5 = recorder.MapMd5,
                    Source = Flag(args, "--source") ?? recorder.ServerName,
                    RecordedUtc = DateTime.UtcNow,
                },
                Stream = bytes,
            };

            File.WriteAllBytes(output, trace.Encode());
        }
        else
        {
            File.WriteAllBytes(output, bytes);
        }

        Console.WriteLine($"{bytes.Length} bytes in {recorder.Chunks.Count} reads over {seconds}s -> {output}");

        Summarise(bytes, recorder.Map, Flag(args, "--maps"));
        return 0;
    }

    private static int Replay(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("replay needs a trace file");
            return 2;
        }

        var trace = Gg2.Protocol.Trace.Decode(File.ReadAllBytes(args[0]));
        Console.WriteLine($"{args[0]}: {trace.Header}");

        if (trace.Header.ReferenceVersion != Gg2.Protocol.GameConstants.Version)
        {
            Console.Error.WriteLine(
                $"trace was recorded against reference version {trace.Header.ReferenceVersion}, " +
                $"this build targets {Gg2.Protocol.GameConstants.Version}");
            return 1;
        }

        var map = LoadMap(trace.Header.Map, Flag(args, "--maps"));
        if (map is null)
            return 1;

        var shape = Gg2.Maps.GameModes.ShapeOf(map);
        var ticks = Gg2.Protocol.TraceParser.Parse(trace, shape, out var start);

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
            var report = new Replayer(Build()) { Compare = compare }
                .Replay(args[0], trace.Header.Map, ticks, mode, stopAtFirstBadTick: !args.Contains("--no-stop"));

            Console.WriteLine();
            Console.Write(report.Render());
            failed |= !report.Passed;
        }

        return failed ? 1 : 0;
    }

    private static Gg2.Maps.LevelMap? LoadMap(string map, string? mapDirectory)
    {
        var directory = mapDirectory ?? DefaultMapDirectory();
        var png = Path.Combine(directory, map + ".png");

        if (File.Exists(png))
            return Gg2.Maps.LevelMap.FromPng(map, File.ReadAllBytes(png));

        Console.Error.WriteLine($"no map at {png}; pass --maps or set GG2_REFERENCE");
        return null;
    }

    private static string DefaultMapDirectory() =>
        Environment.GetEnvironmentVariable("GG2_REFERENCE") is { Length: > 0 } reference
            ? Path.Combine(reference, "Source", "gg2", "Included Files")
            : @"D:\code\Gang-Garrison-2\Source\gg2\Included Files";

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

        var shape = Gg2.Maps.GameModes.ShapeOf(Gg2.Maps.LevelMap.FromPng(map, File.ReadAllBytes(png)));
        var stream = new Gg2.Protocol.ServerStream();
        stream.SetShape(shape);

        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var reader = new Gg2.Protocol.NetReader(trace);

        try
        {
            while (reader.Remaining > 0)
            {
                var message = stream.Read(ref reader);
                var name = message.Type.ToString();
                counts[name] = counts.GetValueOrDefault(name) + 1;
            }
        }
        catch (Gg2.Protocol.ProtocolException e)
        {
            Console.WriteLine($"framing stopped at byte {reader.Position} of {trace.Length}: {e.Message}");
            return;
        }

        Console.WriteLine($"{shape.GameMode}, {shape.ControlPointCount} control point(s), " +
                          $"{shape.MovingPlatformCount} platform(s); framed cleanly to the last byte");

        foreach (var (name, count) in counts)
            Console.WriteLine($"  {count,5}  {name}");
    }

    /// <summary>
    /// Frames a recording and says what is in it, message by message.
    /// </summary>
    /// <remarks>
    /// <c>record</c> already prints a census of what it just captured, but it
    /// prints one line and gives up the moment framing throws — which is exactly
    /// when the detail is wanted. This is the same walk with the answers kept:
    /// where framing stopped, what had been read by then, and what the first
    /// <c>FULL_UPDATE</c> actually contained.
    /// <para>
    /// That last part earns its place. A <c>FULL_UPDATE</c>'s per-player block
    /// ends with a class-dependent weapon block, and a reader that has the width
    /// of that block wrong produces a stream that either explodes far downstream
    /// or — worse — happens to keep parsing. Being able to print the block and
    /// count its bytes is the difference between finding that in minutes and
    /// finding it in a day.
    /// </para>
    /// </remarks>
    private static int Census(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("census needs a trace file");
            return 2;
        }

        var bytes = File.ReadAllBytes(args[0]);
        string mapName;
        byte[] stream;

        if (Gg2.Protocol.Trace.LooksLikeTrace(bytes))
        {
            var trace = Gg2.Protocol.Trace.Decode(bytes);
            Console.WriteLine($"{args[0]}: {trace.Header}");
            mapName = trace.Header.Map;
            stream = trace.Stream;
        }
        else
        {
            mapName = Flag(args, "--map")
                ?? throw new InvalidOperationException("a raw recording needs --map");
            Console.WriteLine($"{args[0]}: {bytes.Length} raw bytes, map {mapName}");
            stream = bytes;
        }

        var map = LoadMap(mapName, Flag(args, "--maps"));
        if (map is null)
            return 1;

        var shape = Gg2.Maps.GameModes.ShapeOf(map);
        var parser = new Gg2.Protocol.ServerStream();
        parser.SetShape(shape);

        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var reader = new Gg2.Protocol.NetReader(stream);
        var messages = 0;
        Gg2.Protocol.ServerMessage? firstFull = null;
        string? stopped = null;

        while (reader.Remaining > 0)
        {
            var before = reader.Position;
            Gg2.Protocol.ServerMessage message;

            try
            {
                message = parser.Read(ref reader);
            }
            catch (Gg2.Protocol.ProtocolException e)
            {
                // Name the message that failed, not just where it started. A
                // state update that mis-parses runs off the end of the buffer,
                // so the offset in the exception is the end of the file and says
                // nothing; the byte at `before` says which message it was.
                var kind = (Gg2.Protocol.MessageType)stream[before];

                stopped = $"stopped after {messages} message(s) on a {kind} " +
                          $"beginning at byte {before} of {stream.Length}: {e.Message}";
                break;
            }

            messages++;
            counts[message.Type.ToString()] = counts.GetValueOrDefault(message.Type.ToString()) + 1;

            if (message.Type == Gg2.Protocol.MessageType.FullUpdate)
                firstFull ??= message;
        }

        Console.WriteLine(
            $"{shape.GameMode}, {shape.ControlPointCount} control point(s), " +
            $"{shape.MovingPlatformCount} platform(s)");

        foreach (var (name, count) in counts)
            Console.WriteLine($"  {count,5}  {name}");

        Console.WriteLine($"roster: {string.Join(", ", parser.Names.Select((n, i) => $"{i}:{n}"))}");

        if (firstFull?.State is { } full)
        {
            Console.WriteLine($"first FULL_UPDATE: {full.Players.Length} player(s), {firstFull.Payload.Length} bytes");

            for (var slot = 0; slot < full.Players.Length; slot++)
            {
                var player = full.Players[slot];
                var character = player.Character is { } c
                    ? $"character x={c.X} y={c.Y} hp={c.Hp} ammo={c.Ammo}"
                    : "no character";

                Console.WriteLine($"  slot {slot}: subobjects={player.Subobjects} {character}");
            }
        }
        else
        {
            Console.WriteLine("first FULL_UPDATE: none in this recording");
        }

        if (stopped is not null)
        {
            Console.WriteLine(stopped);

            // One trailing byte is a message the capture cut in half, not a
            // framing bug: the recorder stops on a wall clock, not a boundary.
            return stream.Length - reader.Position <= 1 ? 0 : 1;
        }

        Console.WriteLine($"framed cleanly to the last byte: {messages} message(s)");
        return 0;
    }

    private static int Diff(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("diff needs two files");
            return 2;
        }

        var left = File.ReadAllBytes(args[0]);
        var right = File.ReadAllBytes(args[1]);

        Console.WriteLine($"{args[0]}: {left.Length} bytes");
        Console.WriteLine($"{args[1]}: {right.Length} bytes");

        var shared = Math.Min(left.Length, right.Length);
        var at = 0;
        while (at < shared && left[at] == right[at])
            at++;

        if (at == shared && left.Length == right.Length)
        {
            Console.WriteLine("identical");
            return 0;
        }

        Console.WriteLine($"first difference at byte {at}");
        Console.WriteLine();
        Console.WriteLine(Window(left, at, args[0]));
        Console.WriteLine(Window(right, at, args[1]));
        return 1;
    }

    /// <summary>32 bytes either side of the divergence, with it marked.</summary>
    private static string Window(byte[] bytes, int at, string label)
    {
        var from = Math.Max(0, at - 32);
        var to = Math.Min(bytes.Length, at + 32);

        var hex = new System.Text.StringBuilder();
        hex.Append(label).Append('\n').Append("  ");

        for (var i = from; i < to; i++)
        {
            hex.Append(i == at ? '[' : ' ');
            hex.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
            if (i == at)
                hex.Append(']');
        }

        return hex.ToString();
    }

    private static string? Flag(string[] args, string name)
    {
        var at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    private static void Usage() => Console.WriteLine(
        """
        gg2-parity - record, compare and replay server broadcast streams

          record --host <h> --port <n> --seconds <s> --name <n> --out <file>
                 [--source <text>] [--maps <path>]
          census <trace> [--map <name>] [--maps <path>]
          diff   <a> <b>
          replay <trace> [--maps <path>]

        The recorder joins as a passive spectator and never sends an INPUTSTATE
        of its own, so what it writes down is the server's own broadcast: every
        player's keys and aim each tick, and every player's position, velocity
        and hp each seventh tick. One recording therefore holds both the inputs
        a simulation consumed and the outputs it produced.

        An --out ending in .trace gets the header that makes it replayable.
        Anything else is written raw, which is what a byte diff wants.

        census frames a recording and says what is in it: a message count, the
        roster, the first FULL_UPDATE player by player, and - when framing
        stops - which message it stopped on. A raw recording needs --map.

        Point record at the reference GM8 server and at this port in turn, then
        diff the two. See docs/PARITY.md.
        """);
}
