
namespace Gg2.Parity;

internal static partial class Program
{
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
        var shape = Gg2.Maps.GameModes.ShapeOf(map.ForArea(1));
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

            // ⚠️ Follow a rotation onto another gamemode's map, or the HUD block
            // changes length underneath this loop. Only FULL_UPDATE and
            // CAPS_UPDATE read that block, so the damage is invisible until one
            // of them arrives on the new map. See docs/PARITY.md.
            if (message.Type == Gg2.Protocol.MessageType.ChangeMap && message.Payload.Length >= 1)
            {
                var length = message.Payload[0];
                if (message.Payload.Length >= 1 + length)
                {
                    var next = System.Text.Encoding.ASCII.GetString(message.Payload, 1, length);
                    parser.SetShape(ShapeForMap(Flag(args, "--maps"))(next));
                }
            }

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
}
