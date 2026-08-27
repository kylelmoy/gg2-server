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
    public const int CurrentFormatVersion = 1;

    /// <summary>
    /// The longest string a header field can hold.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Every header string is written with a one-byte length prefix</b>
    /// (<see cref="Trace.Encode"/>), so 255 is a hard limit rather than a
    /// convention — and it is <b>not</b> raised, because the prefix is the
    /// checked-in trace format and every fixture in the corpus is written in it.
    /// <para>
    /// Named so a caller can refuse an over-long value <em>before</em> doing the
    /// work: <c>Encode</c> runs after a recording has finished, and a throw there
    /// discards however many minutes of capture produced it. Measured on
    /// 2026-08-27 by losing a 175-second recording to a 284-character
    /// <c>--source</c>.
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

        return writer.WrittenSpan.ToArray();
    }

    public static Trace Decode(ReadOnlySpan<byte> bytes)
    {
        var reader = new NetReader(bytes);

        if (!reader.ReadBytes(Magic.Length).SequenceEqual(Magic))
            throw new ProtocolException("not a gg2 trace: magic mismatch");

        var formatVersion = reader.ReadInt();
        if (formatVersion != TraceHeader.CurrentFormatVersion)
            throw new ProtocolException(
                $"trace format version {formatVersion}, expected {TraceHeader.CurrentFormatVersion}");

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

        return new Trace { Header = header, Stream = reader.ReadBytes(length).ToArray() };
    }
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
    /// ⚠️ <b>Kept because nothing else in a recording says anything about a
    /// control point.</b> The block rides on the same tiers as positions, and
    /// carries <b>no count prefix</b> — a reader rebuilds the points from the
    /// map (docs/PROTOCOL.md, "The KotH HUD block"). Until 2026-08-27
    /// <c>TraceParser</c> parsed it and threw it away, so a port that never
    /// captured a hill replayed a King of the Hill recording exactly as well as
    /// a correct one.
    /// </remarks>
    public ControlPointBlock[] ControlPoints { get; init; } = [];

    /// <summary>
    /// <c>cpUnlock</c>, when this tick carried a King of the Hill HUD block.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The one thing a replay cannot derive about a King of the Hill
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
    /// ⚠️ <b>It rides on a <c>FULL_UPDATE</c> and on nothing else</b>, and a
    /// recording holds exactly one of those, at the moment the recorder joined.
    /// So this is non-null on one tick of a whole trace.
    /// <para>
    /// ⚠️ <b>A replay cannot derive it.</b> It is server configuration
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
