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

    /// <summary>
    /// They asked to change team, with everything that follows from it.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Not the same as <see cref="SetTeam"/>, and the difference is the
    /// point.</b> Setting the field writes a team; asking to change team drops
    /// the intel, kills the character and arms a respawn — <c>PlayerCommands</c>
    /// does all of it. Both nodes have to do all of it, or one of them is
    /// carrying an intel the other dropped.
    /// </remarks>
    ChangeTeam = 5,

    /// <summary>They asked to change class, with everything that follows.</summary>
    ChangeClass = 6,

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
public readonly record struct MeshCommand(
    MeshCommandKind Kind, byte Slot, byte A, byte B, byte Origin = 0)
    : IComparable<MeshCommand>
{
    /// <summary>
    /// A join, whose SLOT is decided by applying it rather than by asking for
    /// one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⛔ <b>A GG2 roster is dense and positional</b> — the wire numbers players
    /// by their place in it, and every client tracks that. So two nodes cannot
    /// each pick a slot for somebody joining: they would pick the same one, and
    /// the second join would land on top of the first.
    /// </para>
    /// <para>
    /// Instead a join names WHO issued it and their own counter, and the slot
    /// falls out of the order every node already computes: the lowest free slot,
    /// assigned as the command is applied. Two joins landing on the same tick
    /// from opposite ends of the link therefore take consecutive slots, in the
    /// same order, on both machines, with nobody having coordinated anything.
    /// </para>
    /// <para>
    /// <paramref name="origin"/> is which node issued it — the listener is 0 —
    /// and <paramref name="sequence"/> is that node's own count, which is what
    /// lets the issuer find out afterwards which slot its player got.
    /// </para>
    /// </remarks>
    public static MeshCommand AddPlayer(int origin, int sequence) =>
        new(
            MeshCommandKind.AddPlayer,
            0,
            (byte)(sequence & 0xFF),
            (byte)((sequence >> 8) & 0xFF),
            (byte)origin);

    /// <summary>The issuer's own count, for finding the slot it was given.</summary>
    public int Sequence => A | (B << 8);

    public static MeshCommand SetTeam(int slot, Team team) =>
        new(MeshCommandKind.SetTeam, (byte)slot, (byte)team, 0);

    public static MeshCommand SetClass(int slot, PlayerClass playerClass) =>
        new(MeshCommandKind.SetClass, (byte)slot, (byte)playerClass, 0);

    /// <summary>A player asking to change team, as a client does.</summary>
    public static MeshCommand ChangeTeam(int slot, Team team) =>
        new(MeshCommandKind.ChangeTeam, (byte)slot, (byte)team, 0);

    /// <summary>A player asking to change class, as a client does.</summary>
    public static MeshCommand ChangeClass(int slot, PlayerClass playerClass) =>
        new(MeshCommandKind.ChangeClass, (byte)slot, (byte)playerClass, 0);

    /// <summary>Spawns <paramref name="slot"/> at a spawnpoint and group.</summary>
    public static MeshCommand Spawn(int slot, int spawnpointId = 0, int group = 0) =>
        new(MeshCommandKind.Spawn, (byte)slot, (byte)spawnpointId, (byte)group);

    public static MeshCommand RemovePlayer(int slot) =>
        new(MeshCommandKind.RemovePlayer, (byte)slot, 0, 0);

    /// <summary>
    /// The order two nodes both compute: kind, then who issued it, then slot,
    /// then payload.
    /// </summary>
    /// <remarks>
    /// <b>Origin comes before slot because a join has no slot yet.</b> Two joins
    /// on one tick are ordered by which node issued them and that node's own
    /// counter, which is the whole of what decides who gets the lower roster
    /// place — and it has to be decided the same way on both machines.
    /// </remarks>
    public int CompareTo(MeshCommand other)
    {
        var kind = Kind.CompareTo(other.Kind);

        if (kind != 0)
            return kind;

        var origin = Origin.CompareTo(other.Origin);

        if (origin != 0)
            return origin;

        var slot = Slot.CompareTo(other.Slot);

        if (slot != 0)
            return slot;

        var a = A.CompareTo(other.A);

        return a != 0 ? a : B.CompareTo(other.B);
    }
}
