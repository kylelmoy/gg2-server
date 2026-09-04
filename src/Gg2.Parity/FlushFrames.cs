using Gg2.Protocol;

namespace Gg2.Parity;

/// <summary>
/// One read off the socket: where it starts in the recorded stream, and when it
/// arrived.
/// </summary>
public readonly record struct FrameMark(int Offset, TimeSpan At);

/// <summary>
/// The <c>.frames</c> sidecar — the read boundaries a <c>.trace</c> deliberately
/// does not carry.
/// </summary>
/// <remarks>
/// <b>A sidecar rather than a change to the trace format, and that is not
/// squeamishness.</b> docs/PARITY.md gives three reasons the trace is the raw
/// stream, and the third is that the parity recorder and a live server writing
/// training data are the same path. Widening the format would break every
/// checked-in recording and split that path. A sidecar leaves <c>.trace</c>
/// byte-for-byte what it is and is simply absent for recordings made before this
/// existed.
/// <para>
/// <b>What it is FOR.</b> The flush boundary is the only carrier of
/// docs/PARITY.md's instrument 2, and the byte stream does not carry it: the
/// send buffer is FIFO, so two different flush orderings produce an identical
/// stream. Timing is the only signal left.
/// </para>
/// </remarks>
public sealed class FlushFrames
{
    private const int Magic = 0x47474632; // "GGF2"

    public required IReadOnlyList<FrameMark> Marks { get; init; }

    /// <summary>The conventional sidecar path for a trace.</summary>
    public static string PathFor(string tracePath) => tracePath + ".frames";

    public byte[] Encode()
    {
        var writer = new NetWriter(16 + (Marks.Count * 8));

        writer.WriteInt(Magic);
        writer.WriteInt(Marks.Count);

        foreach (var mark in Marks)
        {
            writer.WriteInt(mark.Offset);
            writer.WriteInt((int)mark.At.TotalMilliseconds);
        }

        return writer.WrittenSpan.ToArray();
    }

    public static FlushFrames Decode(ReadOnlySpan<byte> bytes)
    {
        var reader = new NetReader(bytes);

        if (reader.ReadInt() != Magic)
            throw new ProtocolException("not a .frames sidecar");

        var count = reader.ReadInt();
        var marks = new FrameMark[count];

        for (var i = 0; i < count; i++)
            marks[i] = new FrameMark(reader.ReadInt(), TimeSpan.FromMilliseconds(reader.ReadInt()));

        return new FlushFrames { Marks = marks };
    }

    /// <summary>
    /// Byte ranges, one per flush, inferred by merging reads that arrived
    /// together.
    /// </summary>
    /// <param name="gap">
    /// Reads separated by less than this belong to the same flush. The server
    /// flushes once a tick at 30 Hz, so the real gap is ~33ms and loopback
    /// latency is microseconds; anything from a few ms to ~20 works.
    /// </param>
    /// <param name="ambiguous">
    /// How many boundaries fell within a factor of two of <paramref name="gap"/>
    /// — i.e. how much of the grouping is a judgement call rather than obvious.
    /// </param>
    /// <remarks>
    /// <b>This is a HEURISTIC and the only one in the instrument.</b> TCP may
    /// coalesce two flushes into one read, and the reference is not known to set
    /// <c>TCP_NODELAY</c>. <b>Do not trust it uncalibrated</b>: sample
    /// <c>buffer_size(global.sendBuffer)</c> once a frame from the agent bridge
    /// and check the grouping against it on a short run first — that measures the
    /// leftover prefix directly and cannot be wrong. docs/PARITY.md says so at
    /// more length.
    /// <para>
    /// <b><paramref name="ambiguous"/> is not decoration.</b> A grouping that
    /// silently merged half its flushes would produce a clean-looking fingerprint
    /// that means nothing, and this is the only number that would say so.
    /// </para>
    /// </remarks>
    public IReadOnlyList<Range> Group(TimeSpan gap, out int ambiguous)
    {
        ambiguous = 0;

        if (Marks.Count == 0)
            return [];

        var groups = new List<Range>();
        var start = Marks[0].Offset;

        for (var i = 1; i < Marks.Count; i++)
        {
            var delta = Marks[i].At - Marks[i - 1].At;

            if (delta >= gap)
            {
                groups.Add(new Range(start, Marks[i].Offset));
                start = Marks[i].Offset;
            }

            if (delta >= gap / 2 && delta < gap * 2)
                ambiguous++;
        }

        return groups;
    }
}

/// <summary>
/// Where each message type sits relative to the state update in the flush that
/// carried it — docs/PARITY.md's <b>instrument 2</b>.
/// </summary>
/// <remarks>
/// A flush carries <c>[leftovers from the previous tick's End Steps][this tick's
/// pre-update messages][the update][this tick's post-update messages]</c>.
/// <b>Leading</b> counts the ones before the update, <b>trailing</b> the ones
/// after.
/// <para>
/// <b>Leading conflates two different things and cannot separate them.</b> A
/// message appended in an End Step (a <c>PLAYER_DEATH</c>) and one appended in
/// <c>GameServerBeginStep</c> above <c>serializeState</c> (a <c>PLAYER_LEAVE</c>)
/// both lead. So this grades the table for types that sit wholly on one side and
/// says nothing about the three that do not — <c>PLAYER_DEATH</c>,
/// <c>RETURN_INTEL</c>, <c>DESTROY_SENTRY</c>. Those need the port's own stage
/// flags; see <c>WireStageTests</c>.
/// </para>
/// </remarks>
public sealed class FlushFingerprint
{
    public required IReadOnlyDictionary<MessageType, int> Leading { get; init; }

    public required IReadOnlyDictionary<MessageType, int> Trailing { get; init; }

    /// <summary>Flushes that carried no state update at all.</summary>
    /// <remarks>
    /// The join path produces these legitimately — <c>serviceJoiningPlayer</c>
    /// runs after the flush, so a joining client's burst is its own group. A
    /// large count on a session with no joins means the grouping is wrong.
    /// </remarks>
    public required int GroupsWithoutAnUpdate { get; init; }

    public int Total(MessageType type) =>
        (Leading.TryGetValue(type, out var lead) ? lead : 0)
        + (Trailing.TryGetValue(type, out var trail) ? trail : 0);

    /// <summary>Types that never appeared, which is a coverage result and not a pass.</summary>
    /// <remarks>
    /// <b>Report this, always.</b> An absent row is indistinguishable from an
    /// agreeing row, and a CTF bot session never produces generator destruction,
    /// arena transitions, sentry destruction, map change or balance. This repo
    /// has shipped a vacuous gate twice; naming what was never seen is the cheap
    /// half of not doing it a third time.
    /// </remarks>
    public IReadOnlyList<MessageType> Unseen(IEnumerable<MessageType> expected) =>
        [.. expected.Where(t => Total(t) == 0)];

    /// <summary>Builds the fingerprint from flush groups of parsed messages.</summary>
    public static FlushFingerprint Of(IEnumerable<IReadOnlyList<ServerMessage>> groups)
    {
        var leading = new Dictionary<MessageType, int>();
        var trailing = new Dictionary<MessageType, int>();
        var withoutUpdate = 0;

        foreach (var group in groups)
        {
            var seenUpdate = false;

            foreach (var message in group)
            {
                if (IsTieredUpdate(message.Type))
                {
                    seenUpdate = true;
                    continue;
                }

                // A CAPS_UPDATE is written immediately above the tiered update
                // in the same event, so it is neither leading nor trailing in the
                // sense this measures. Counting it would put a permanent entry in
                // the leading column that says nothing about any boundary.
                if (message.Type == MessageType.CapsUpdate)
                    continue;

                var into = seenUpdate ? trailing : leading;
                into[message.Type] = into.TryGetValue(message.Type, out var n) ? n + 1 : 1;
            }

            if (!seenUpdate)
                withoutUpdate++;
        }

        return new FlushFingerprint
        {
            Leading = leading,
            Trailing = trailing,
            GroupsWithoutAnUpdate = withoutUpdate,
        };
    }

    private static bool IsTieredUpdate(MessageType type) =>
        type is MessageType.InputState or MessageType.QuickUpdate or MessageType.FullUpdate;

    public override string ToString()
    {
        var types = Leading.Keys.Concat(Trailing.Keys).Distinct().OrderBy(t => (int)t);

        return string.Join(
            ", ",
            types.Select(t =>
                $"{t} {(Leading.TryGetValue(t, out var l) ? l : 0)}L/"
                + $"{(Trailing.TryGetValue(t, out var r) ? r : 0)}T"));
    }
}
