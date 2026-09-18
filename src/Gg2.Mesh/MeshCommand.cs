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
/// the two machines. <see cref="MeshCommandKind"/>'s VALUES are the order
/// commands are applied in — the members are declared to match, but it is the
/// numbers <see cref="MeshCommand.CompareTo"/> compares — and ties break on
/// origin, slot and payload: a total order every node computes for itself.
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
    /// Mark the slot as a bot, so bot code at BOTH ends can tell a person from
    /// a bot.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>After <see cref="AddPlayer"/>, never with it.</b>
    /// <c>WorldState.AddPlayer</c> writes <c>PlayerState.Fresh</c> over the
    /// slot, which would erase a flag set alongside it.
    /// <para>
    /// It is meshed rather than written locally because a node copies its world
    /// FROM its node every tick: a flag set outside the node survives until the
    /// next copy and no longer. Meshing it also stops the peer's bots reading
    /// this node's bots as people —
    /// <c>Handover.ShouldEngage</c> under <c>EngageOpponents.HumansOnly</c>.
    /// </para>
    /// </remarks>
    SetBot = 3,

    /// <summary>Put a character in the world for them.</summary>
    Spawn = 4,

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

    /// <summary>
    /// Take the slot away and compact the roster.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Last, and the NUMBER is what makes it last.</b> A removal renumbers
    /// every slot above it, so anything else in the same tick has to have been
    /// applied while the slot numbers still meant what the sender meant. The
    /// order is <see cref="MeshCommand.CompareTo"/>'s comparison of these
    /// values, not the order they are declared in — a value below
    /// <see cref="ChangeTeam"/> would put a removal FIRST and hand the team
    /// change to whoever shifted down into the slot.
    /// </remarks>
    RemovePlayer = 7,

    /// <summary>
    /// Replace the map, and with it the whole world.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>The node cannot apply this one and deliberately ignores it.</b> A
    /// map change swaps the collision world and the spawn points, which are
    /// read-only inputs a node is GIVEN — and loading a map is file I/O, which
    /// the simulation may not do. So the servers watch for it landing and
    /// rebuild their nodes around the new map together, at the same tick, from
    /// the same reset world.
    /// <para>
    /// ⚠️ <b>The map NAME rides in the command frame's name field</b>, the same
    /// one a join uses for a player's name. Twenty bytes, and the longest map
    /// this server ships is sixteen.
    /// </para>
    /// </remarks>
    ChangeMap = 8,
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

    /// <summary>Marks <paramref name="slot"/> as a bot on every node.</summary>
    public static MeshCommand SetBot(int slot) =>
        new(MeshCommandKind.SetBot, (byte)slot, 1, 0);

    /// <summary>Spawns <paramref name="slot"/> at a spawnpoint and group.</summary>
    public static MeshCommand Spawn(int slot, int spawnpointId = 0, int group = 0) =>
        new(MeshCommandKind.Spawn, (byte)slot, (byte)spawnpointId, (byte)group);

    /// <summary>
    /// A departure, named by the join that created the place rather than by the
    /// place.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⛔ <b>A slot cannot name a departure, and this is the trap that cost the
    /// most to see.</b> A removal renumbers every slot above it, so two removals
    /// in flight at once — one from each node, which is the ordinary case —
    /// means the second was numbered against a roster the first has already
    /// changed. Both nodes then agree on the WRONG player, so nothing diverges
    /// and no digest goes red: it is simply somebody else who leaves.
    /// </para>
    /// <para>
    /// Ordering removals by descending slot fixes that within one tick and not
    /// across ticks, which is why the naming is the fix rather than the sort.
    /// The join key is stable under renumbering — <c>MeshNode</c> renumbers its
    /// table with the roster — so it is resolved to a slot at APPLY time, on
    /// both nodes, from the same state.
    /// </para>
    /// <para>
    /// A second removal of the same player resolves to nothing and is a no-op,
    /// which is what makes a departure safe to say twice.
    /// </para>
    /// </remarks>
    /// <summary>A map change, whose name travels beside it as the frame's name.</summary>
    public static MeshCommand ChangeMap(int area) =>
        new(MeshCommandKind.ChangeMap, 0, (byte)area, 0);

    public static MeshCommand RemovePlayer(int origin, int sequence) =>
        new(
            MeshCommandKind.RemovePlayer,
            0,
            (byte)(sequence & 0xFF),
            (byte)((sequence >> 8) & 0xFF),
            (byte)origin);

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
