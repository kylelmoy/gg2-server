using Gg2.Protocol;

namespace Gg2.Mesh;

/// <summary>
/// Something that happens to a match and is not a key press: somebody joining,
/// leaving, changing team or class, or being spawned.
/// </summary>
/// <remarks>
/// <para>
/// <b>These are the events a mesh cannot treat as inputs.</b> An input belongs
/// to a player and a tick, and a node that has not heard one guesses. Nobody can
/// guess a join: it either happened or it did not, and two nodes that disagree
/// about whether slot 4 exists are not running the same match at all.
/// </para>
/// <para>
/// So a command is <b>scheduled for a future tick</b> rather than applied when
/// it arrives. The originating node picks a tick far enough ahead that its peer
/// will have heard about it first, both apply it at exactly that tick, and a
/// rewind through that tick applies it again — because it lives in the schedule,
/// keyed by tick, not in whatever order the sockets delivered it.
/// </para>
/// <para>
/// ⚠️ <b>The ORDER within a tick has to be decided by the data, not by
/// arrival.</b> Two nodes can each schedule something for the same tick and hear
/// the other's afterwards, so an order that depended on arrival would differ on
/// the two machines. <see cref="MeshCommandKind"/> is declared in the order
/// commands are applied, and ties break on slot and payload — a total order
/// every node computes for itself.
/// </para>
/// </remarks>
public enum MeshCommandKind : byte
{
    /// <summary>Give a roster slot to somebody. Must precede anything else about them.</summary>
    AddPlayer = 0,

    /// <summary>Put them on a team.</summary>
    SetTeam = 1,

    /// <summary>Give them a class.</summary>
    SetClass = 2,

    /// <summary>Put a character in the world for them.</summary>
    Spawn = 3,

    /// <summary>
    /// Take the slot away and compact the roster.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Last, and that is not arbitrary.</b> A removal renumbers every slot
    /// above it, so anything else in the same tick has to have been applied
    /// while the slot numbers still meant what the sender meant.
    /// </remarks>
    RemovePlayer = 4,
}

/// <summary>One scheduled change to the roster.</summary>
/// <remarks>
/// A value type with no strings and no references, so it costs the same to hold
/// in a schedule as an input does, and so two nodes comparing schedules are
/// comparing numbers.
/// </remarks>
public readonly record struct MeshCommand(MeshCommandKind Kind, byte Slot, byte A, byte B)
    : IComparable<MeshCommand>
{
    public static MeshCommand AddPlayer(int slot) =>
        new(MeshCommandKind.AddPlayer, (byte)slot, 0, 0);

    public static MeshCommand SetTeam(int slot, Team team) =>
        new(MeshCommandKind.SetTeam, (byte)slot, (byte)team, 0);

    public static MeshCommand SetClass(int slot, PlayerClass playerClass) =>
        new(MeshCommandKind.SetClass, (byte)slot, (byte)playerClass, 0);

    /// <summary>Spawns <paramref name="slot"/> at a spawnpoint and group.</summary>
    public static MeshCommand Spawn(int slot, int spawnpointId = 0, int group = 0) =>
        new(MeshCommandKind.Spawn, (byte)slot, (byte)spawnpointId, (byte)group);

    public static MeshCommand RemovePlayer(int slot) =>
        new(MeshCommandKind.RemovePlayer, (byte)slot, 0, 0);

    /// <summary>
    /// The order two nodes both compute: kind first, then slot, then payload.
    /// </summary>
    public int CompareTo(MeshCommand other)
    {
        var kind = Kind.CompareTo(other.Kind);

        if (kind != 0)
            return kind;

        var slot = Slot.CompareTo(other.Slot);

        if (slot != 0)
            return slot;

        var a = A.CompareTo(other.A);

        return a != 0 ? a : B.CompareTo(other.B);
    }
}
