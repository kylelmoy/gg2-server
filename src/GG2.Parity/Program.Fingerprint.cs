using System.Globalization;

namespace GG2.Parity;

internal static partial class Program
{
    /// <summary>
    /// docs/PARITY.md's instrument 2: which side of the state update each
    /// message type rode on, from a recording and its <c>.frames</c> sidecar.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The sidecar carries READ boundaries, not flush boundaries.</b> They
    /// are the same thing only while TCP does not coalesce two flushes into one
    /// read, which is why <c>--gap</c> exists and why the ambiguity count is
    /// printed rather than hidden. Compare the group count against the number of
    /// state updates: the server flushes once a tick, so they should be within a
    /// percent or so of each other, and a large shortfall means the grouping has
    /// merged flushes and the fingerprint below is not trustworthy.
    /// </remarks>
    private static int Fingerprint(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("fingerprint needs a trace file");
            return 2;
        }

        var tracePath = args[0];
        var framesPath = FlushFrames.PathFor(tracePath);

        if (!File.Exists(framesPath))
        {
            Console.Error.WriteLine(
                $"no {framesPath}. A fingerprint needs the read boundaries, and the byte "
                + "stream does not carry them — re-record; every trace made since the "
                + "sidecar landed has one.");

            return 2;
        }

        var trace = GG2.Protocol.Trace.Decode(File.ReadAllBytes(tracePath));
        var frames = FlushFrames.Decode(File.ReadAllBytes(framesPath));

        var gapMs = double.Parse(Flag(args, "--gap") ?? "10", CultureInfo.InvariantCulture);
        var groups = frames.Group(TimeSpan.FromMilliseconds(gapMs), out var ambiguous);

        var directory = Flag(args, "--maps") ?? DefaultMapDirectory();
        var png = Path.Combine(directory, trace.Header.Map + ".png");

        if (!File.Exists(png))
        {
            Console.Error.WriteLine($"{png} not found; pass --maps");
            return 2;
        }

        var shape = GG2.Maps.GameModes.ShapeOf(
            GG2.Maps.LevelMap.FromPng(trace.Header.Map, File.ReadAllBytes(png)).ForArea(1));

        var stream = new GG2.Protocol.ServerStream();
        stream.SetShape(shape);

        // ⚠️ One buffer across every group, and a message is attributed to the
        // group its LAST byte arrived in. A message split across two reads has
        // not arrived until it is complete, so that is the honest attribution —
        // and it is what a client does.
        var buffer = new GG2.Protocol.ReceiveBuffer();
        var parsed = new List<IReadOnlyList<GG2.Protocol.ServerMessage>>();

        foreach (var group in groups)
        {
            buffer.Append(trace.Stream.AsSpan()[group]);

            var messages = new List<GG2.Protocol.ServerMessage>();

            while (stream.TryRead(buffer, out var message))
                messages.Add(message);

            parsed.Add(messages);
        }

        var fingerprint = FlushFingerprint.Of(parsed);
        var updates = parsed.Sum(g => g.Count(m =>
            m.Type is GG2.Protocol.MessageType.InputState
                or GG2.Protocol.MessageType.QuickUpdate));

        Console.WriteLine($"{tracePath}: {trace.Header}");
        Console.WriteLine(
            $"{frames.Marks.Count} reads -> {groups.Count} groups at a {gapMs}ms gap "
            + $"({ambiguous} near the threshold); {updates} state updates");
        Console.WriteLine($"groups with no state update: {fingerprint.GroupsWithoutAnUpdate}");
        Console.WriteLine();
        Console.WriteLine("  message                  leading  trailing");

        foreach (var type in fingerprint.Leading.Keys.Concat(fingerprint.Trailing.Keys)
                     .Distinct().OrderBy(t => t.ToString(), StringComparer.Ordinal))
        {
            var lead = fingerprint.Leading.TryGetValue(type, out var l) ? l : 0;
            var trail = fingerprint.Trailing.TryGetValue(type, out var t) ? t : 0;

            Console.WriteLine($"  {type,-22} {lead,8}  {trail,8}");
        }

        return 0;
    }
}
