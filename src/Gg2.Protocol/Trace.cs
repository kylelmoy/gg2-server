using System.Globalization;

namespace Gg2.Protocol;

/// <summary>
/// What a trace is a recording of.
/// </summary>
/// <remarks>
/// Small and fixed on purpose. Everything here has to be known before a single
/// byte of stream can be framed — the map decides a state update's shape — so
/// the header is the part that cannot be reconstructed from the recording.
/// </remarks>
public sealed record TraceHeader
{
    /// <summary>Bumped whenever the on-disk layout changes.</summary>
    /// <remarks>
    /// <b>Version 2 added <see cref="Trace.Roster"/>.</b> A v1 trace is still
    /// read — <see cref="Trace.Decode"/> accepts both — because the checked-in
    /// fixture corpus is written in v1 and re-recording it against the running
    /// game to gain a field none of those traces has any use for would be a
    /// week of live measurement for nothing.
    /// </remarks>
    public const int CurrentFormatVersion = 2;

    /// <summary>The oldest layout <see cref="Trace.Decode"/> still reads.</summary>
    public const int OldestReadableFormatVersion = 1;

    /// <summary>
    /// The longest string a header field can hold.
    /// </summary>
    /// <remarks>
    /// <b>Every header string is written with a one-byte length prefix</b>
    /// (<see cref="Trace.Encode"/>), so 255 is a hard limit rather than a
    /// convention — and it is <b>not</b> raised, because the prefix is the
    /// checked-in trace format and every fixture in the corpus is written in it.
    /// <para>
    /// Named so a caller can refuse an over-long value <em>before</em> doing the
    /// work: <c>Encode</c> runs after a recording has finished, and a throw there
    /// discards however many minutes of capture produced it.
    /// </para>
    /// </remarks>
    public const int MaxStringLength = 255;

    public required int FormatVersion { get; init; }

    /// <summary>
    /// The reference's <c>VERSION</c> the recording was made against.
    /// </summary>
    /// <remarks>
    /// A trace recorded against a different build of the game is not
    /// comparable, and a wrong answer here is invisible — the same reasoning
    /// behind <c>SIM_UUID</c> in docs/BOTAI.md, one layer out.
    /// </remarks>
    public required int ReferenceVersion { get; init; }

    public required string Map { get; init; }

    /// <summary>Empty for a built-in map.</summary>
    public required string MapMd5 { get; init; }

    /// <summary>Free text: which server produced it, and under what conditions.</summary>
    public required string Source { get; init; }

    public required DateTime RecordedUtc { get; init; }

    public override string ToString() =>
        $"{Map} from {Source}, v{ReferenceVersion}, {RecordedUtc.ToString("u", CultureInfo.InvariantCulture)}";
}

/// <summary>
/// Who was behind a roster slot: a person at a keyboard, or something this
/// server was driving.
/// </summary>
/// <remarks>
/// <b>The wire cannot say.</b> A bot joins as an ordinary player and
/// <c>PLAYER_JOIN</c> carries a name and nothing else (docs/PROTOCOL.md), which
/// is deliberate — <c>docs/BOTAI.md</c> item 4 makes a bot's action exactly a
/// client's action, so a recording of the broadcast holds a bot's keys and a
/// person's keys in the same field with nothing to tell them apart.
/// <para>
/// That is fatal to the one thing a recording of a public game is <em>for</em>.
/// <c>docs/BOTAI.md</c> item 3 wants <c>p(action | state)</c> over real
/// opponents; a dataset built from an unlabelled trace of a server that fills
/// with bots is mostly this project's own scripted layer, fed back in as if it
/// were human play. Nothing downstream can detect that.
/// </para>
/// </remarks>
public enum TraceParticipant : byte
{
    /// <summary>
    /// No label. <b>Expected only on a v1 trace</b>, which predates the labels
    /// entirely — a v2 recording labels every occupied slot, so this reaching a
    /// consumer means the trace is older than the question being asked of it.
    /// </summary>
    Unknown = 0,

    /// <summary>A person on a socket.</summary>
    Human = 1,

    /// <summary>An <c>IInputSource</c> this server was driving.</summary>
    Bot = 2,
}

/// <summary>
/// One slot's occupant, from one tick onwards.
/// </summary>
/// <remarks>
/// <b>Tick-indexed, not a flat table, because a slot is reused.</b> A roster
/// index is positional and compacts on <c>PLAYER_LEAVE</c>
/// (<c>WorldState.RemoveSlot</c>), so across a half-hour recording slot 3 is
/// several different people. A table keyed by slot alone would attribute all of
/// them to whoever was there last.
/// </remarks>
public sealed record TraceRosterNote
{
    /// <summary>
    /// The <see cref="TraceTick.Index"/> this label takes effect at, counted
    /// the way <see cref="TraceParser"/> counts.
    /// </summary>
    /// <remarks>
    /// <b>The number of state-update-delimited ticks recorded before it</b>, so
    /// it lines up with the parser by construction: the recorder counts the same
    /// broadcast flushes the parser turns into ticks, and the opening
    /// <c>FULL_UPDATE</c> is a tick to neither.
    /// </remarks>
    public required int Tick { get; init; }

    public required byte Slot { get; init; }

    public required TraceParticipant Participant { get; init; }

    /// <summary>
    /// What was driving a <see cref="TraceParticipant.Bot"/> — the bot mode the
    /// server was running. Empty for a person.
    /// </summary>
    /// <remarks>
    /// <b>Separate from the human/bot split because the two answer different
    /// questions.</b> "Was this a person" decides whether a row belongs in a
    /// human dataset at all; "which brain" decides what a bot row is evidence
    /// about, and a policy-driven bot's keys are not interchangeable with a
    /// scripted one's.
    /// </remarks>
    public required string Controller { get; init; }

    /// <summary>
    /// The player's name, as <c>PLAYER_JOIN</c> announced it.
    /// </summary>
    /// <remarks>
    /// <b>Redundant with the stream, and kept for exactly that reason.</b> The
    /// labels are the one part of a trace not derived from the bytes beside
    /// them, so a misalignment between the two has nothing to catch it — and a
    /// label attached to the wrong slot is silent and ruins the dataset. A
    /// consumer compares this against the name the stream gave that slot and
    /// refuses the trace when they disagree.
    /// </remarks>
    public required string Name { get; init; }
}

/// <summary>
/// A recorded broadcast stream, with the header needed to read it back.
/// </summary>
/// <remarks>
/// <b>The trace is the raw stream.</b> Not a parsed one — the bytes the server
/// sent, kept verbatim, with a header in front. Three reasons, and the third is
/// the one that matters:
/// <list type="number">
/// <item>It is lossless, so a format change never invalidates a recording.</item>
/// <item>It is bounded and cheap to write: a 30 Hz broadcast to a spectator is
/// a few hundred bytes a second.</item>
/// <item>It means the parity recorder and a live server writing training data
/// are the <em>same</em> path (docs/BOTAI.md, item 3). A second logging format
/// invented later is how the two drift apart.</item>
/// </list>
/// <para>
/// Parsing needs a <see cref="WorldShape"/>, which comes from the map named in
/// the header.
/// </para>
/// </remarks>
public sealed class Trace
{
    public required TraceHeader Header { get; init; }

    /// <summary>The recorded stream, exactly as it arrived.</summary>
    public required byte[] Stream { get; init; }

    /// <summary>
    /// Who occupied each roster slot, and from when. Always empty on a v1
    /// trace.
    /// </summary>
    /// <remarks>
    /// <b>In the container rather than in a sidecar file</b>, and that is the
    /// point of putting it here. A <c>.labels</c> beside a <c>.trace</c> is one
    /// copy away from being separated from it, and a trace that has lost its
    /// labels does not look broken — it looks like a trace, and everything
    /// downstream reads every bot in it as a person.
    /// <para>
    /// Ordered by <see cref="TraceRosterNote.Tick"/>, then by slot.
    /// </para>
    /// </remarks>
    public IReadOnlyList<TraceRosterNote> Roster { get; init; } = [];

    /// <summary>
    /// A ceiling on the labels one trace carries, so a corrupt length cannot
    /// make <see cref="Decode"/> allocate without bound.
    /// </summary>
    /// <remarks>
    /// Generous rather than tight: a segment rolls at 32 MB and a join costs a
    /// few hundred bytes of broadcast, so the cap is unreachable by legitimate
    /// churn. It bounds a hostile file; it is not a policy about how much roster
    /// movement is worth recording.
    /// </remarks>
    public const int MaxRosterNotes = 65535;

    private static ReadOnlySpan<byte> Magic => "GG2TRACE"u8;

    /// <summary>
    /// Whether these bytes carry the trace header, rather than being a raw
    /// recording.
    /// </summary>
    /// <remarks>
    /// The recorder writes both forms — a <c>.trace</c> with the header that
    /// makes it replayable, anything else raw for a byte diff — so a tool handed
    /// a file has to ask. Cheaper and clearer than catching the exception
    /// <see cref="Decode"/> throws.
    /// </remarks>
    public static bool LooksLikeTrace(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= Magic.Length && bytes[..Magic.Length].SequenceEqual(Magic);

    /// <summary>Serialises to the on-disk form. The caller does the writing.</summary>
    public byte[] Encode()
    {
        var writer = new NetWriter(Stream.Length + 256);

        writer.WriteBytes(Magic);
        writer.WriteInt(Header.FormatVersion);
        writer.WriteInt(Header.ReferenceVersion);
        writer.WriteString1(Header.Map);
        writer.WriteString1(Header.MapMd5);
        writer.WriteString1(Header.Source);

        // Unix seconds in a uint: good to 2106, and it keeps the trace header
        // to the wire types NetWriter already has rather than adding a 64-bit
        // one the GG2 protocol does not use.
        writer.WriteUInt((uint)new DateTimeOffset(Header.RecordedUtc).ToUnixTimeSeconds());
        writer.WriteInt(Stream.Length);
        writer.WriteBytes(Stream);

        // AFTER the stream, so the labels are a tail a v1 reader never reaches.
        // A header that still says 1 writes none, which is what keeps a v1
        // Trace encoding to the exact bytes it did before v2 existed — the
        // fixture corpus round-trips unchanged rather than being re-recorded.
        if (Header.FormatVersion >= 2)
        {
            if (Roster.Count > MaxRosterNotes)
            {
                throw new ProtocolException(
                    $"a trace carries at most {MaxRosterNotes} roster labels, not {Roster.Count}");
            }

            writer.WriteInt(Roster.Count);

            foreach (var note in Roster)
            {
                writer.WriteInt(note.Tick);
                writer.WriteUByte(note.Slot);
                writer.WriteUByte((byte)note.Participant);
                writer.WriteString1(note.Controller);
                writer.WriteString1(note.Name);
            }
        }

        return writer.WrittenSpan.ToArray();
    }

    public static Trace Decode(ReadOnlySpan<byte> bytes)
    {
        var reader = new NetReader(bytes);

        if (!reader.ReadBytes(Magic.Length).SequenceEqual(Magic))
            throw new ProtocolException("not a gg2 trace: magic mismatch");

        var formatVersion = reader.ReadInt();
        if (formatVersion is < TraceHeader.OldestReadableFormatVersion
                          or > TraceHeader.CurrentFormatVersion)
        {
            throw new ProtocolException(
                $"trace format version {formatVersion}, expected " +
                $"{TraceHeader.OldestReadableFormatVersion}-{TraceHeader.CurrentFormatVersion}");
        }

        var header = new TraceHeader
        {
            FormatVersion = formatVersion,
            ReferenceVersion = reader.ReadInt(),
            Map = reader.ReadString1(),
            MapMd5 = reader.ReadString1(),
            Source = reader.ReadString1(),
            RecordedUtc = DateTimeOffset.FromUnixTimeSeconds(reader.ReadUInt()).UtcDateTime,
        };

        var length = reader.ReadInt();
        if (length < 0 || length > reader.Remaining)
            throw new ProtocolException($"trace claims {length} stream bytes, has {reader.Remaining}");

        var stream = reader.ReadBytes(length).ToArray();

        // ⚠️ A v2 trace whose label tail is TRUNCATED is refused, rather than
        // read as far as it goes — the opposite of what TraceParser does to the
        // stream, and deliberately. A half-read stream is still a recording of
        // everything before the cut. A half-read label table is a roster with
        // some of its bots silently unlabelled, which is the exact failure
        // TraceParticipant exists to prevent and is invisible downstream.
        var roster = formatVersion >= 2 ? ReadRoster(ref reader) : [];

        return new Trace { Header = header, Stream = stream, Roster = roster };
    }

    private static TraceRosterNote[] ReadRoster(ref NetReader reader)
    {
        var count = reader.ReadInt();

        if (count < 0 || count > MaxRosterNotes)
            throw new ProtocolException($"trace claims {count} roster labels");

        var roster = new TraceRosterNote[count];

        for (var i = 0; i < count; i++)
        {
            roster[i] = new TraceRosterNote
            {
                Tick = reader.ReadInt(),
                Slot = reader.ReadUByte(),
                Participant = ParticipantOf(reader.ReadUByte()),
                Controller = reader.ReadString1(),
                Name = reader.ReadString1(),
            };
        }

        return roster;
    }

    /// <summary>Maps a recorded participant byte onto the enum.</summary>
    /// <remarks>
    /// <b>An unrecognised value becomes <see cref="TraceParticipant.Unknown"/>
    /// rather than throwing.</b> A label written by a later build, naming a kind
    /// this one has never heard of, still says "this slot was labelled";
    /// refusing the whole recording over it would discard a trace for carrying
    /// more information than the reader wanted. Unknown is a value every
    /// consumer already handles, because a v1 trace is nothing but.
    /// </remarks>
    private static TraceParticipant ParticipantOf(byte value) =>
        Enum.IsDefined(typeof(TraceParticipant), value)
            ? (TraceParticipant)value
            : TraceParticipant.Unknown;
}

/// <summary>One roster slot's contribution to one tick.</summary>
public readonly record struct TraceSlot
{
    /// <summary>False for a player with no character — spectating, or dead.</summary>
    public required bool HasCharacter { get; init; }

    /// <summary>
    /// The action this player took on this tick. Present on every tier, which
    /// is the whole reason a broadcast stream is a usable trace.
    /// </summary>
    public required PlayerInput Input { get; init; }

    /// <summary>
    /// What the server said the character's position and velocity were. Only on
    /// a position-bearing tick.
    /// </summary>
    public CharacterBlock? Observed { get; init; }
}

/// <summary>One server tick, as the broadcast stream recorded it.</summary>
/// <remarks>
/// A tick is delimited by its state update, which is always the <em>last</em>
/// thing the server writes for that tick: <c>GameServerBeginStep.gml</c> queues
/// command responses first and appends the update afterwards, and the whole
/// buffer goes out in End Step. So everything since the previous update belongs
/// to this one.
/// </remarks>
public sealed class TraceTick
{
    /// <summary>Index from the start of the trace, not the server's own frame counter.</summary>
    public required int Index { get; init; }

    /// <summary><c>INPUTSTATE</c> or <c>QUICK_UPDATE</c>.</summary>
    public required MessageType Tier { get; init; }

    /// <summary>Whether this tick carries positions.</summary>
    public bool HasPositions => Tier is MessageType.QuickUpdate or MessageType.FullUpdate;

    public required TraceSlot[] Slots { get; init; }

    /// <summary>
    /// The control points, as the update described them. Empty on an
    /// <c>INPUTSTATE</c>.
    /// </summary>
    /// <remarks>
    /// <b>Kept because nothing else in a recording says anything about a
    /// control point.</b> The block rides on the same tiers as positions, and
    /// carries <b>no count prefix</b> — a reader rebuilds the points from the
    /// map (docs/PROTOCOL.md, "The KotH HUD block").
    /// </remarks>
    public ControlPointBlock[] ControlPoints { get; init; } = [];

    /// <summary>
    /// <c>cpUnlock</c>, when this tick carried a King of the Hill HUD block.
    /// </summary>
    /// <remarks>
    /// <b>The one thing a replay cannot derive about a King of the Hill
    /// round.</b> <c>KothHUD.events/Create.xml</c> starts it at 900 and the
    /// unlock fires 901 ticks later — but the HUD is created when the server
    /// <em>restarts the room</em>, and a recording's only landmark is the
    /// <c>CHANGE_MAP</c> that announced the change several hundred ticks
    /// earlier. Measured on two recordings: both carry one <c>CHANGE_MAP</c> at
    /// t0 and unlock at t1236 and t1160, so the announcement is demonstrably not
    /// the round start. See <c>KothUnlockTimingTests</c>.
    /// <para>
    /// So it is anchored from the wire, exactly as a <c>PLAYER_SPAWN</c>'s
    /// position is: the server already made the choice and is telling everyone
    /// what it was.
    /// </para>
    /// </remarks>
    public int? ControlPointUnlock { get; init; }

    /// <summary>
    /// Team Deathmatch's spawn invulnerability, in ticks, as the server
    /// configured it — or null on a tick whose update did not carry it.
    /// </summary>
    /// <remarks>
    /// <b>It rides on a <c>FULL_UPDATE</c> and on nothing else</b>, and a
    /// recording holds exactly one of those, at the moment the recorder joined.
    /// So this is non-null on one tick of a whole trace.
    /// <para>
    /// <b>A replay cannot derive it.</b> It is server configuration
    /// (<c>ServerOptions.TdmInvulnerabilitySeconds</c>, five by default), so a
    /// port replaying a recording made against a differently-configured server
    /// has no way to know except by being told — and the server tells everyone,
    /// which is what makes anchoring it from the wire legitimate rather than a
    /// fudge. The same argument as a <c>PLAYER_SPAWN</c>'s position and the
    /// King of the Hill unlock.
    /// </para>
    /// </remarks>
    public int? TdmInvulnerability { get; init; }

    /// <summary>
    /// Everything else the server sent before this tick's update — roster
    /// changes, spawns, deaths, weapon fire.
    /// </summary>
    /// <remarks>
    /// A replay has to apply these rather than derive them: docs/PARITY.md is
    /// explicit that team selection, spawn positions and respawn timing are
    /// server-side RNG the port is free about, so they are taken from the trace
    /// instead of being decided again.
    /// </remarks>
    public required ServerMessage[] Events { get; init; }
}
