using Gg2.Protocol;

namespace Gg2.Server;

/// <summary>
/// Writes the server's own broadcast stream to disk as replayable
/// <c>.trace</c> segments — <c>docs/BOTAI.md</c> item 3, the only item on this
/// project with a deadline.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is not a second logging path.</b> Item 3 asks that "whatever
/// <c>Gg2.Parity</c> records for trace replay … is what a live server can be
/// configured to record from <b>human</b> players", and is explicit: "do not
/// design a second logging path later; make the parity recorder switchable on
/// in production and give it a bounded on-disk format." The broadcast stream
/// already <em>is</em> that data — <c>INPUTSTATE</c> carries every player's
/// keys and aim and <c>QUICK_UPDATE</c> carries every player's position,
/// velocity and hp, both quantised to exactly the precision a client can
/// observe. So there is nothing to capture that the server does not already
/// send; what was missing was a way to write it down without a socket.
/// </para>
/// <para>
/// ⚠️ <b>It is a ghost, not a client.</b> <c>gg2-parity record</c> joins as a
/// spectator, which costs a roster seat, shows up in the player list and — on a
/// public server — is a seat a person cannot have. This writes the identical
/// byte stream from inside <see cref="GameServer.FlushBroadcast"/> instead, so
/// a recording server is indistinguishable from one that is not.
/// </para>
/// <para>
/// ⚠️ <b>Every segment carries its own preamble, and that is what makes it
/// replayable.</b> A trace has to begin at a message that defines a complete
/// starting point, which is the join burst's <c>FULL_UPDATE</c>. Teeing only
/// the broadcast would produce a file that starts wherever the recording
/// happened to start — parseable, because a flush is always a message boundary,
/// but with no state to anchor against. So each segment opens with the same
/// burst <see cref="GameServer.CompleteJoin"/> sends a joiner.
/// </para>
/// <para>
/// <b>Bounded, because a public game runs for hours.</b> Segments roll at
/// <see cref="SegmentBytes"/> and stop after <see cref="MaxSegments"/>; the cap
/// is a disk-space bound, not a policy about how much play is worth keeping.
/// The recorder also rolls on a map change, so a segment is always one map —
/// which keeps its header's map name honest.
/// </para>
/// </remarks>
public sealed class MatchRecorder : IDisposable
{
    private readonly string _directory;
    private readonly string _source;
    private readonly IServerLog _log;
    private readonly Func<byte[]> _preamble;

    private FileStream? _segment;
    private string _map = string.Empty;
    private long _written;
    private int _index;
    private long _total;
    private bool _full;

    /// <summary>
    /// Bytes of broadcast per segment before it rolls. Thirty-two megabytes is
    /// a few hours of a full server; the point of the number is that it is
    /// finite, not that it is exactly this.
    /// </summary>
    public const long SegmentBytes = 32L * 1024 * 1024;

    /// <summary>
    /// Total bytes written before the recorder stops. One gigabyte, which is
    /// the actual requirement: an unattended public server must not fill a disk.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A total, not a segment count, and the difference was found by
    /// running it.</b> The first version capped at 32 segments on the reasoning
    /// that 32 x <see cref="SegmentBytes"/> is a gigabyte — but a segment also
    /// rolls on a map change, so a server rotating quickly spends the cap on
    /// rounds rather than on bytes. Measured: at <c>--speed max</c> with bots,
    /// rounds ended about once a second and the recorder stopped after
    /// <b>26 seconds</b> having written 8 MB of its allowed 1,024. A byte total
    /// bounds the disk whatever the round length is.
    /// </remarks>
    public const long MaxTotalBytes = 1024L * 1024 * 1024;

    /// <param name="preamble">
    /// Produces the join burst that opens a segment — the same bytes a real
    /// joiner is sent, ending in a <c>FULL_UPDATE</c>. It is a callback rather
    /// than a value because it has to be re-taken at every roll, against the
    /// world as it is then.
    /// </param>
    public MatchRecorder(string directory, string source, IServerLog log, Func<byte[]> preamble)
    {
        _directory = directory;
        _source = source;
        _log = log;
        _preamble = preamble;

        Directory.CreateDirectory(directory);
    }

    /// <summary>Segments written so far, including the one open.</summary>
    public int SegmentCount => _index;

    /// <summary>Bytes of broadcast written across every segment.</summary>
    public long TotalBytes => _total;

    /// <summary>True once <see cref="MaxTotalBytes"/> is reached and nothing more is kept.</summary>
    public bool IsFull => _full;

    /// <summary>The segment currently being written, or null before the first.</summary>
    public string? CurrentPath { get; private set; }

    /// <summary>
    /// Starts a segment for <paramref name="map"/>, closing any open one.
    /// </summary>
    /// <remarks>
    /// Called at startup and at every map change. ⚠️ It must run <em>after</em>
    /// the new map is loaded and before the tick that broadcasts on it, or the
    /// preamble describes the previous world.
    /// </remarks>
    public void StartSegment(string map)
    {
        Close();

        if (_full)
            return;

        if (_total >= MaxTotalBytes)
        {
            _full = true;
            _log.Info(
                $"recording stopped: {_total / (1024 * 1024)}MB written to {_directory}, "
                + "which is the cap");
            return;
        }

        _map = map;
        _index++;

        var name = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{map}-{_index:D2}.trace";
        CurrentPath = Path.Combine(_directory, name);

        // The header is written on Close, when the length is known — a trace is
        // a header plus one stream, so the stream has to be complete first.
        // Until then the segment file holds raw broadcast and nothing else.
        _segment = new FileStream(CurrentPath + ".part", FileMode.Create, FileAccess.Write);
        _written = 0;

        Append(_preamble());
    }

    /// <summary>Appends one broadcast flush.</summary>
    /// <remarks>
    /// ⚠️ Rolls <em>after</em> writing rather than before, so a flush is never
    /// split across two segments. A message that straddled a segment boundary
    /// would make one file end mid-message and the next begin mid-message, and
    /// the second is the one that cannot be recovered — a reader has no way to
    /// find the next boundary.
    /// </remarks>
    public void Append(ReadOnlySpan<byte> bytes)
    {
        if (_segment is null || bytes.IsEmpty)
            return;

        _segment.Write(bytes);
        _written += bytes.Length;
        _total += bytes.Length;

        if (_written >= SegmentBytes || _total >= MaxTotalBytes)
            StartSegment(_map);
    }

    /// <summary>Finishes the open segment, giving it its header and its name.</summary>
    public void Close()
    {
        if (_segment is null || CurrentPath is null)
            return;

        var part = CurrentPath + ".part";
        _segment.Dispose();
        _segment = null;

        var stream = File.ReadAllBytes(part);

        var trace = new Trace
        {
            Header = new TraceHeader
            {
                FormatVersion = TraceHeader.CurrentFormatVersion,
                ReferenceVersion = GameConstants.Version,
                Map = _map,

                // Empty for a built-in map, which is every map this server
                // serves — the same value gg2-parity record writes.
                MapMd5 = string.Empty,
                Source = Truncate(_source),
                RecordedUtc = DateTime.UtcNow,
            },
            Stream = stream,
        };

        File.WriteAllBytes(CurrentPath, trace.Encode());
        File.Delete(part);

        _log.Info($"recorded {stream.Length} bytes to {Path.GetFileName(CurrentPath)}");
        CurrentPath = null;
    }

    public void Dispose() => Close();

    /// <summary>
    /// A trace header writes its strings with a one-byte length, so 255 is a
    /// hard limit.
    /// </summary>
    /// <remarks>
    /// ⚠️ Truncated rather than refused, which is the opposite of what
    /// <c>gg2-parity record</c> does with <c>--source</c> — and deliberately.
    /// There the operator is standing at a prompt and can shorten it; here the
    /// value is a server name that arrived from a config file, and refusing to
    /// record a public game over a long name is the worse failure by far.
    /// </remarks>
    private static string Truncate(string value) =>
        value.Length <= TraceHeader.MaxStringLength
            ? value
            : value[..TraceHeader.MaxStringLength];
}
