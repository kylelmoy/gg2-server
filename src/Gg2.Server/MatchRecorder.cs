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
/// <b>It is a ghost, not a client.</b> <c>gg2-parity record</c> joins as a
/// spectator, which costs a roster seat, shows up in the player list and — on a
/// public server — is a seat a person cannot have. This writes the identical
/// byte stream from inside <see cref="GameServer.FlushBroadcast"/> instead, so
/// a recording server is indistinguishable from one that is not.
/// </para>
/// <para>
/// <b>Every segment carries its own preamble, and that is what makes it
/// replayable.</b> A trace has to begin at a message that defines a complete
/// starting point, which is the join burst's <c>FULL_UPDATE</c>. Teeing only
/// the broadcast would produce a file that starts wherever the recording
/// happened to start — parseable, because a flush is always a message boundary,
/// but with no state to anchor against. So each segment opens with the same
/// burst <see cref="GameServer.CompleteJoin"/> sends a joiner.
/// </para>
/// <para>
/// <b>Bounded, because a public game runs for hours.</b> Segments roll at
/// <see cref="SegmentBytes"/> and recording stops once the directory holds
/// <see cref="MaxTotalBytes"/>; the cap is a disk-space bound, not a policy
/// about how much play is worth keeping. It bounds the directory rather than
/// the process, re-read at every roll, so collecting segments off a running
/// server gives it its budget back. The recorder also rolls on a map change, so
/// a segment is always one map — which keeps its header's map name honest.
/// </para>
/// </remarks>
public sealed class MatchRecorder : IDisposable
{
    private readonly string _directory;
    private readonly string _source;
    private readonly IServerLog _log;
    private readonly Func<byte[]> _preamble;
    private readonly Func<IReadOnlyList<RecordedSlot>> _roster;

    private FileStream? _segment;
    private string _map = string.Empty;
    private long _written;
    private int _index;
    private long _total;
    private bool _full;

    /// <summary>
    /// Ticks appended to the open segment, which is the index
    /// <see cref="TraceParser"/> will give the next one.
    /// </summary>
    /// <remarks>
    /// <b>Counts <see cref="Append"/> and not the preamble</b>, which is what
    /// makes it agree with the parser: a flush carries exactly one
    /// <c>INPUTSTATE</c> or <c>QUICK_UPDATE</c> (<c>WriteStateUpdate</c> appends
    /// every tick), and the preamble's <c>FULL_UPDATE</c> is a tick to neither
    /// side.
    /// </remarks>
    private int _ticks;

    /// <summary>The open segment's roster labels, in the order they were noted.</summary>
    private readonly List<TraceRosterNote> _notes = [];

    /// <summary>
    /// What <see cref="SyncRoster"/> last wrote down for each slot, so an
    /// unchanged roster produces no notes.
    /// </summary>
    /// <remarks>
    /// Cleared at every roll: a new segment re-labels every occupied slot from
    /// tick 0, because it has to stand alone.
    /// </remarks>
    private readonly Dictionary<byte, RecordedSlot> _known = [];

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
    /// <b>A total, not a segment count.</b> A segment also rolls on a map
    /// change, so a server rotating quickly through short rounds would spend a
    /// segment-count cap on rounds rather than on bytes. A byte total bounds the
    /// disk whatever the round length is.
    /// </remarks>
    public const long MaxTotalBytes = 1024L * 1024 * 1024;

    /// <param name="preamble">
    /// Produces the join burst that opens a segment — the same bytes a real
    /// joiner is sent, ending in a <c>FULL_UPDATE</c>. It is a callback rather
    /// than a value because it has to be re-taken at every roll, against the
    /// world as it is then.
    /// </param>
    /// <param name="roster">
    /// Produces who currently occupies each slot. <b>Re-taken at every roll for
    /// the same reason the preamble is</b>: a segment that opened mid-game has
    /// to carry the labels for players who joined before it started, or every
    /// segment but the first describes its bots as people.
    /// </param>
    public MatchRecorder(
        string directory,
        string source,
        IServerLog log,
        Func<byte[]> preamble,
        Func<IReadOnlyList<RecordedSlot>>? roster = null)
    {
        _directory = directory;
        _source = source;
        _log = log;
        _preamble = preamble;
        _roster = roster ?? (static () => []);

        Directory.CreateDirectory(directory);

        // ⚠️ The cap bounds the DIRECTORY, not this process's own writing. A
        // server restarts — a deploy, a config push, a crash loop — and a cap
        // that started again from zero each time would bound nothing at all on
        // the one kind of host this exists for: an unattended public server,
        // which is also the kind that restarts most.
        //
        // StartSegment re-takes this, so the bound keeps tracking the directory
        // for the life of the process rather than only at its start.
        _total = ExistingBytes(directory);

        if (_total > 0)
        {
            _log.Info(
                $"recording into {directory}, which already holds " +
                $"{_total / (1024 * 1024)}MB of the {MaxTotalBytes / (1024 * 1024)}MB cap");
        }
    }

    /// <summary>
    /// What the directory already holds, so a restart resumes against the cap
    /// rather than beneath it.
    /// </summary>
    /// <remarks>
    /// Counts the <c>.part</c> of an interrupted segment too: those bytes are on
    /// the disk this bound is about, whether or not they ever became a trace. A
    /// directory that cannot be read counts as empty and says so — refusing to
    /// record because a listing failed is the worse failure, the same call
    /// <see cref="Truncate"/> makes about an over-long source.
    /// </remarks>
    private long ExistingBytes(string directory)
    {
        try
        {
            var total = 0L;

            foreach (var file in Directory.EnumerateFiles(directory, "*.trace*"))
                total += new FileInfo(file).Length;

            return total;
        }
        catch (IOException e)
        {
            _log.Warn($"could not size {directory} against the recording cap: {e.Message}");
            return 0;
        }
        catch (UnauthorizedAccessException e)
        {
            _log.Warn($"could not size {directory} against the recording cap: {e.Message}");
            return 0;
        }
    }

    /// <summary>
    /// Takes the roster as it stands and records whatever changed since the
    /// last look.
    /// </summary>
    /// <remarks>
    /// <b>A diff of a snapshot, not a hook on each join — deliberately.</b> A
    /// player reaches the roster from three places (a socket join, a bot the
    /// population manager added, the server's own reserved slot 0) and leaves
    /// from more, and <c>WorldState.RemoveSlot</c> renumbers everyone above a
    /// departure. Hooking each site is the "add it in three places" rule
    /// <c>CLAUDE.md</c> records for client commands, and the failure mode here
    /// is worse than a missing feature: an unhooked site produces a recording
    /// whose labels are silently attached to the wrong slots.
    /// <para>
    /// Comparing up to <c>Roster.MaxSlots</c> entries once a tick costs
    /// nothing against a 33.3 ms budget, and only when recording is on.
    /// </para>
    /// <para>
    /// <b>Called before the tick's flush</b>, so a label lands on the tick whose
    /// state it describes rather than on the one after it.
    /// </para>
    /// </remarks>
    public void SyncRoster(IReadOnlyList<RecordedSlot> occupants)
    {
        if (_segment is null)
            return;

        for (var i = 0; i < occupants.Count; i++)
        {
            var occupant = occupants[i];

            if (_known.TryGetValue(occupant.Slot, out var was) && was == occupant)
                continue;

            _known[occupant.Slot] = occupant;
            Note(occupant);
        }

        // A slot nobody occupies any more is left in _known on purpose: the next
        // occupant of it differs from this one and gets its own note, and a slot
        // that empties needs none — the stream's own PLAYER_LEAVE already says
        // so, and a label for an absent player is a row no consumer can use.
    }

    private void Note(in RecordedSlot occupant)
    {
        // Dropped rather than grown without bound, like the broadcast beside it.
        // Unreachable by legitimate churn — see Trace.MaxRosterNotes — so a
        // recording that hits it is one worth saying something about.
        if (_notes.Count >= Trace.MaxRosterNotes)
        {
            if (_notes.Count == Trace.MaxRosterNotes)
                _log.Warn($"{Path.GetFileName(CurrentPath)} hit the roster-label cap; later joins are unlabelled");

            return;
        }

        _notes.Add(new TraceRosterNote
        {
            Tick = _ticks,
            Slot = occupant.Slot,
            Participant = occupant.Participant,
            Controller = Truncate(occupant.Controller),
            Name = Truncate(occupant.Name),
        });
    }

    /// <summary>Segments written so far, including the one open.</summary>
    public int SegmentCount => _index;

    /// <summary>
    /// What the recording directory holds, as of the open segment's start, plus
    /// what has been written into it since.
    /// </summary>
    /// <remarks>
    /// <b>Not a count of broadcast bytes.</b> It is re-anchored to the directory
    /// at every <see cref="StartSegment"/>, so it tracks the disk the cap is
    /// about — including the trace headers a segment gains on close, and
    /// excluding segments an operator has collected and removed.
    /// </remarks>
    public long TotalBytes => _total;

    /// <summary>True once <see cref="MaxTotalBytes"/> is reached and nothing more is kept.</summary>
    public bool IsFull => _full;

    /// <summary>The segment currently being written, or null before the first.</summary>
    public string? CurrentPath { get; private set; }

    /// <summary>
    /// Starts a segment for <paramref name="map"/>, closing any open one.
    /// </summary>
    /// <remarks>
    /// Called at startup and at every map change. It must run <em>after</em>
    /// the new map is loaded and before the tick that broadcasts on it, or the
    /// preamble describes the previous world.
    /// </remarks>
    public void StartSegment(string map)
    {
        Close();

        // ⚠️ Re-taken from the directory rather than carried forward, and that
        // is what makes the cap a bound on the DIRECTORY rather than on this
        // process's lifetime writing. Collecting segments off a live server is
        // the expected case on the hosts this exists for, not an unusual one; a
        // running total would mean a server that had once written a gigabyte
        // never recorded again, however empty the directory had since become.
        //
        // Once per segment: a directory listing per roll or map change, on a
        // thread where Close() has just rewritten up to SegmentBytes.
        _total = ExistingBytes(_directory);

        if (_total >= MaxTotalBytes)
        {
            // Logged on the transition only — StartSegment still runs at every
            // map change while full, because that is the moment a directory
            // that has since been collected gets to resume.
            if (!_full)
            {
                _full = true;
                _log.Info(
                    $"recording stopped: {_directory} holds {_total / (1024 * 1024)}MB, "
                    + "which is the cap; collecting segments off it resumes recording");
            }

            return;
        }

        if (_full)
        {
            _full = false;
            _log.Info(
                $"recording resumed: {_directory} is back under the cap at "
                + $"{_total / (1024 * 1024)}MB");
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
        _ticks = 0;
        _notes.Clear();
        _known.Clear();

        // The preamble goes in WITHOUT counting as a tick — it is a join burst
        // ending in a FULL_UPDATE, which TraceParser keeps as the anchor rather
        // than as tick 0. Counting it here would put every label in this segment
        // one tick ahead of the state it describes.
        AppendRaw(_preamble());

        // AFTER the preamble, so the labels describe the world the segment
        // actually opens on, and at tick 0, because that is where this segment's
        // history starts. Every occupied slot, not just the ones that change
        // later: the roll is the only place a mid-game segment can learn who was
        // already playing, and _known was just cleared so every one of them is a
        // change.
        SyncRoster(_roster());
    }

    /// <summary>Appends one broadcast flush.</summary>
    /// <remarks>
    /// Rolls <em>after</em> writing rather than before, so a flush is never
    /// split across two segments. A message that straddled a segment boundary
    /// would make one file end mid-message and the next begin mid-message, and
    /// the second is the one that cannot be recovered — a reader has no way to
    /// find the next boundary.
    /// </remarks>
    public void Append(ReadOnlySpan<byte> bytes)
    {
        // Counted BEFORE the write, and before any roll it triggers. A flush is
        // one tick, so the labels this tick produced — taken during the tick,
        // ahead of the flush that ends it — are the ones already sitting at
        // index _ticks; incrementing afterwards would leave the next tick's
        // labels pointing at this one.
        if (_segment is not null && !bytes.IsEmpty)
            _ticks++;

        AppendRaw(bytes);
    }

    /// <summary>
    /// Appends without counting a tick: the preamble, which carries no state
    /// update of its own.
    /// </summary>
    private void AppendRaw(ReadOnlySpan<byte> bytes)
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
            Roster = _notes.ToArray(),
        };

        var encoded = trace.Encode();

        File.WriteAllBytes(CurrentPath, encoded);
        File.Delete(part);

        // The segment counted against the cap as the raw bytes appended to its
        // .part; on disk it is now a trace with a header and its roster labels.
        // Correcting the difference keeps TotalBytes equal to what the directory
        // holds between here and the next StartSegment, which is the only window
        // where nothing re-reads it.
        _total += encoded.Length - _written;

        // The human count is in the line because it is the one number that says
        // whether the segment is worth keeping. A recording of twelve bots
        // playing each other is a large file and no training data at all, and an
        // operator reading "0 human" has been told that without opening it.
        var humans = 0;
        foreach (var note in _notes)
        {
            if (note.Participant == TraceParticipant.Human)
                humans++;
        }

        _log.Info(
            $"recorded {stream.Length} bytes, {_ticks} ticks, {_notes.Count} roster label(s) " +
            $"({humans} human) to {Path.GetFileName(CurrentPath)}");

        CurrentPath = null;
    }

    public void Dispose() => Close();

    /// <summary>
    /// A trace header writes its strings with a one-byte length, so 255 is a
    /// hard limit.
    /// </summary>
    /// <remarks>
    /// Truncated rather than refused, which is the opposite of what
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

/// <summary>
/// Who is behind one roster slot, as the server knows it.
/// </summary>
/// <remarks>
/// <b>The server's side of <see cref="TraceRosterNote"/>, without the tick</b>
/// — the recorder stamps that, because only it knows where the segment has got
/// to. Kept a separate type so <c>GameServer</c> never has to reason about
/// trace tick indices to report a join.
/// </remarks>
/// <param name="Slot">The roster index.</param>
/// <param name="Participant">Person or bot.</param>
/// <param name="Controller">
/// The bot mode driving it, or empty for a person. Free text rather than an
/// enum because it is written to a file a later build has to read: an unknown
/// mode should arrive as a string nobody recognises, not fail to parse.
/// </param>
/// <param name="Name">The name the wire announced, for cross-checking.</param>
public readonly record struct RecordedSlot(
    byte Slot,
    TraceParticipant Participant,
    string Controller,
    string Name);
