using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Gg2.Simulation;

namespace Gg2.Mesh;

/// <summary>One player's place in a match, as a node that was not there is told it.</summary>
/// <remarks>
/// The slot is carried as well as the join that owns it, because a node adopting
/// a match cannot recompute the assignment: the joins that produced it happened
/// before it was listening, and slots depend on every one of them in order.
/// </remarks>
public readonly record struct MeshMember(byte Origin, int Sequence, int Slot, string Name);

/// <summary>
/// Everything a node needs to start where another node already is.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ <b>Without this, two nodes have to start together.</b> A node that
/// connects mid-match begins from whatever world it was built with and hears
/// only what is scheduled from then on, so a server that already had players has
/// a roster its new peer does not share — and the two then disagree about which
/// slot every later join takes. Nothing detects that but the digest, which
/// reports a divergence without saying which of the two is wrong.
/// </para>
/// <para>
/// ⚠️ <b>The world crosses the link as RAW STRUCT BYTES</b>, which is sound here
/// for exactly the reason the mesh already depends on: both nodes run bit-identical
/// builds of the same assembly on the same OS family, because otherwise their
/// physics would diverge anyway (docs/SIMULATION.md, "The same build on two
/// machines"). It is not a portable format and must never be written to disk or
/// sent anywhere a different build might read it.
/// </para>
/// </remarks>
public sealed class MeshSnapshot
{
    /// <summary>The tick the world is the start of.</summary>
    public required int Tick { get; init; }

    public required WorldState State { get; init; }

    /// <summary>Who is playing, where, and under whose join.</summary>
    public required IReadOnlyList<MeshMember> Members { get; init; }

    /// <summary>A name is twenty bytes on the GG2 wire and no more here.</summary>
    public const int MaxName = 20;

    /// <summary>The size of the world, which is most of a snapshot.</summary>
    public static int StateBytes { get; } = Unsafe.SizeOf<WorldState>();

    public byte[] Encode()
    {
        var state = State;
        var world = MemoryMarshal.AsBytes(new ReadOnlySpan<WorldState>(in state));

        var size = 8 + world.Length;

        foreach (var member in Members)
            size += 5 + Math.Min(member.Name.Length, MaxName);

        var bytes = new byte[size];
        var at = 0;

        Write(bytes, ref at, Tick);
        Write(bytes, ref at, Members.Count);

        foreach (var member in Members)
        {
            var name = member.Name.Length > MaxName ? member.Name[..MaxName] : member.Name;

            bytes[at++] = member.Origin;
            bytes[at++] = (byte)(member.Sequence & 0xFF);
            bytes[at++] = (byte)((member.Sequence >> 8) & 0xFF);
            bytes[at++] = (byte)member.Slot;
            bytes[at++] = (byte)name.Length;

            for (var i = 0; i < name.Length; i++)
                bytes[at++] = (byte)name[i];
        }

        world.CopyTo(bytes.AsSpan(at));

        return bytes;
    }

    /// <summary>
    /// Reads one back, or null if the bytes are not a whole well-formed snapshot.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Null rather than a throw, and every length checked before it is
    /// used.</b> This is the one frame whose size a peer chooses, so a short read
    /// or a hostile length must end as a dropped link rather than an exception on
    /// the tick thread. docs/HARDENING.md.
    /// </remarks>
    public static MeshSnapshot? Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 8)
            return null;

        var at = 0;
        var tick = Read(bytes, ref at);
        var count = Read(bytes, ref at);

        if (tick < 0 || count < 0 || count > WorldState.MaxSlots)
            return null;

        var members = new MeshMember[count];

        for (var i = 0; i < count; i++)
        {
            if (bytes.Length - at < 5)
                return null;

            var origin = bytes[at];
            var sequence = bytes[at + 1] | (bytes[at + 2] << 8);
            var slot = bytes[at + 3];
            var nameLength = bytes[at + 4];

            at += 5;

            if (nameLength > MaxName || bytes.Length - at < nameLength)
                return null;

            if (slot >= WorldState.MaxSlots)
                return null;

            var chars = new char[nameLength];

            for (var c = 0; c < nameLength; c++)
                chars[c] = (char)bytes[at + c];

            at += nameLength;
            members[i] = new MeshMember(origin, sequence, slot, new string(chars));
        }

        if (bytes.Length - at != StateBytes)
            return null;

        return new MeshSnapshot
        {
            Tick = tick,
            State = MemoryMarshal.Read<WorldState>(bytes[at..]),
            Members = members,
        };
    }

    private static void Write(byte[] bytes, ref int at, int value)
    {
        bytes[at++] = (byte)value;
        bytes[at++] = (byte)(value >> 8);
        bytes[at++] = (byte)(value >> 16);
        bytes[at++] = (byte)(value >> 24);
    }

    private static int Read(ReadOnlySpan<byte> bytes, ref int at)
    {
        var value = bytes[at] | (bytes[at + 1] << 8) | (bytes[at + 2] << 16) | (bytes[at + 3] << 24);
        at += 4;

        return value;
    }
}
