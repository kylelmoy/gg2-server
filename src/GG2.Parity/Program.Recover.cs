
namespace GG2.Parity;

internal static partial class Program
{
    /// <summary>
    /// Gives an orphaned <c>.part</c> its header, turning it into a readable
    /// trace.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A live server's newest recording exists only as a <c>.part</c>
    /// until the segment closes</b>, because a trace is a header in front of one
    /// stream and the header cannot be written until the stream's length is
    /// known (<c>MatchRecorder.Close</c>). A clean stop finalises it — the host
    /// cancels on Ctrl+C and disposes the recorder — but a <c>kill</c>, a crash
    /// or a power cut leaves the bytes on disk with no header and no tool able
    /// to read them.
    /// <para>
    /// <b>This is that tool, and it exists because of the deadline.</b>
    /// <c>docs/BOTAI.md</c> item 3 is the one item on this project that cannot
    /// be redone: "data not collected during the port's first public games does
    /// not exist afterwards". On a public server the segment in progress is the
    /// round in progress, which is exactly the one worth keeping — and the
    /// events most likely to end a server uncleanly are the events worth having
    /// recorded. Found by a two-human playtest on 2026-08-27, which is what a
    /// rehearsal is for.
    /// </para>
    /// <para>
    /// ⚠️ <b>The map cannot be recovered from the bytes</b>, only from the file
    /// name <c>MatchRecorder</c> chose (<c>&lt;utc&gt;-&lt;map&gt;-&lt;nn&gt;.trace.part</c>).
    /// A renamed file needs <c>--map</c>, and getting it wrong produces a trace
    /// that frames against the wrong world rather than one that fails loudly.
    /// </para>
    /// </remarks>
    private static int Recover(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("recover needs a .part file");
            return 2;
        }

        var part = args[0];

        if (!File.Exists(part))
        {
            Console.Error.WriteLine($"no such file: {part}");
            return 1;
        }

        var stream = File.ReadAllBytes(part);

        if (stream.Length == 0)
        {
            Console.Error.WriteLine($"{part} is empty; there is nothing to recover");
            return 1;
        }

        // "20260828-022743-koth_corinth-03.trace.part" -> "koth_corinth".
        var map = Flag(args, "--map") ?? MapFromName(Path.GetFileName(part));

        if (map is null)
        {
            Console.Error.WriteLine(
                $"cannot read a map name out of '{Path.GetFileName(part)}'; pass --map");
            return 2;
        }

        var output = part.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
            ? part[..^".part".Length]
            : part + ".trace";

        var trace = new GG2.Protocol.Trace
        {
            Header = new GG2.Protocol.TraceHeader
            {
                FormatVersion = GG2.Protocol.TraceHeader.CurrentFormatVersion,
                ReferenceVersion = GG2.Protocol.GameConstants.Version,
                Map = map,

                // Empty for a built-in map, which is what a recording server
                // serves — the same value MatchRecorder and `record` write.
                MapMd5 = string.Empty,
                Source = Flag(args, "--source") ?? "recovered from " + Path.GetFileName(part),
                RecordedUtc = DateTime.UtcNow,
            },
            Stream = stream,
        };

        File.WriteAllBytes(output, trace.Encode());

        Console.WriteLine($"recovered {stream.Length} bytes of {map} to {output}");
        Console.WriteLine("⚠️ run `census` on it: a .part can end mid-message, and the last");
        Console.WriteLine("   whole message before that is all a reader can trust.");

        return 0;
    }
}
