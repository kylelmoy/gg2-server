using System.Globalization;

namespace Gg2.Parity;

internal static partial class Program
{
    private static int Record(string[] args)
    {
        var host = Flag(args, "--host") ?? "127.0.0.1";
        var port = int.Parse(Flag(args, "--port") ?? "8190", CultureInfo.InvariantCulture);
        var seconds = double.Parse(Flag(args, "--seconds") ?? "5", CultureInfo.InvariantCulture);
        var name = Flag(args, "--name") ?? "recorder";
        var output = Flag(args, "--out") ?? "trace.bin";
        var source = Flag(args, "--source");

        // ⚠️ Checked HERE rather than at the end, because the end is after the
        // recording. A trace header writes its strings with a one-byte length
        // prefix (Trace.Encode), so an over-long --source throws from
        // WriteString1 once RecordFor has already returned — and the capture,
        // however many minutes it took, is gone with it. Measured on 2026-08-27
        // by losing a 175-second recording to a 284-character description.
        //
        // The limit is not raised: the prefix is the checked-in trace format and
        // 27 fixtures are written in it.
        if (source is { Length: > Gg2.Protocol.TraceHeader.MaxStringLength })
        {
            Console.Error.WriteLine(
                $"--source is {source.Length} characters; a trace header allows "
                + $"{Gg2.Protocol.TraceHeader.MaxStringLength}. Shorten it before recording.");

            return 2;
        }

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
                    Source = source ?? recorder.ServerName,
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

        // ⛔ The read boundaries, which the trace format deliberately does not
        // carry and which are the ONLY carrier of the flush boundary — the byte
        // stream is identical whichever side of the flush a message was appended
        // on. docs/PARITY.md, "Which flush a message rides". Written beside the
        // trace rather than into it so every existing recording stays valid.
        var frames = new FlushFrames { Marks = [.. Offsets(recorder.Chunks)] };
        var framesPath = FlushFrames.PathFor(output);

        File.WriteAllBytes(framesPath, frames.Encode());

        Console.WriteLine($"{bytes.Length} bytes in {recorder.Chunks.Count} reads over {seconds}s -> {output}");
        Console.WriteLine($"{frames.Marks.Count} read boundaries -> {framesPath}");

        Summarise(bytes, recorder.Map, Flag(args, "--maps"));
        return 0;
    }

    /// <summary>Running byte offset of each recorded read.</summary>
    private static IEnumerable<FrameMark> Offsets(IReadOnlyList<Recorder.Chunk> chunks)
    {
        var offset = 0;

        foreach (var chunk in chunks)
        {
            yield return new FrameMark(offset, chunk.At);
            offset += chunk.Bytes.Length;
        }
    }
}
