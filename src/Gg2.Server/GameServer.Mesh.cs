using Gg2.Mesh;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Server;

public sealed partial class GameServer
{
    /// <summary>
    /// Clients admitted to the handshake but waiting for their roster place.
    /// </summary>
    /// <remarks>
    /// <b>In a mesh a join is not immediate.</b> The roster place comes from a
    /// command both nodes apply at an agreed tick, so a joiner waits roughly
    /// <see cref="MeshSession.CommandLead"/> ticks — a third of a second, against
    /// the seconds a GG2 join already takes — while the peer is told. Until then
    /// the connection is held here and nothing about it is on the wire.
    /// </remarks>
    private readonly List<(int Sequence, JoiningConnection Joining)> _meshJoining = [];

    /// <summary>This node's own count of joins, which names them to the peer.</summary>
    private int _meshSequence;

    /// <summary>
    /// Asks the mesh for a roster place for a joiner, and parks them until it
    /// arrives.
    /// </summary>
    private void MeshAdmit(JoiningConnection joining)
    {
        var sequence = ++_meshSequence;

        _mesh!.Schedule(MeshCommand.AddPlayer(_mesh.Origin, sequence), joining.Name);
        _meshJoining.Add((sequence, joining));
    }

    /// <summary>
    /// Brings the roster into line with the world, which in a mesh is the thing
    /// that decides who is playing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⛔ <b>The roster and the world must agree, slot for slot.</b> Everything a
    /// client sees names a player by position — <c>MatchState</c> hands the
    /// state writer the ROSTER, so the roster's length is the player count on
    /// the wire and the world supplies each block. A roster shorter than the
    /// world simply leaves the newest players unsent; a roster LONGER than the
    /// world would ask for a character that does not exist.
    /// </para>
    /// <para>
    /// ⚠️ <b>Called before the state update is written</b>, so that within one
    /// tick the roster, the world and the update all describe the same set of
    /// people. The world gains a player during the tick's simulation, so the
    /// roster catches up at the top of the next one.
    /// </para>
    /// <para>
    /// Slots only ever appear at the end, because a join takes the lowest free
    /// place in a dense roster. That is why this is a walk from
    /// <c>_roster.Count</c> upward rather than a scan.
    /// </para>
    /// </remarks>
    private void MirrorMeshRoster()
    {
        if (_mesh is null)
            return;

        while (_roster.Count < WorldState.MaxSlots && _world.HasPlayer[_roster.Count])
        {
            var slot = _roster.Count;

            if (TakeMeshJoiner(slot) is { } joining)
            {
                // A client of ours, whose place has now been agreed with the
                // peer. Everything from here is the ordinary join sequence.
                CompleteJoin(joining);
                continue;
            }

            // Somebody who joined at the other end of the link. They have no
            // socket here — the same shape a bot has — and this node's own
            // clients have to be told they exist.
            var entry = new RosterEntry
            {
                Name = _mesh.NameOf(slot) is { Length: > 0 } named ? named : "player",
            };

            _roster.Add(entry);
            _plugins.PlayerAdded(slot);
            ServerMessages.PlayerJoin(_broadcast, entry.Name);
        }

        // A departure at the far end shortens the world, and the roster follows.
        // ⚠️ RemoveSlot has already renumbered everything above it in the world,
        // so the roster is corrected from the end rather than by index.
        while (_roster.Count > 1 && !_world.HasPlayer[_roster.Count - 1])
            _roster.RemoveAt(_roster.Count - 1);
    }

    /// <summary>
    /// The parked joiner whose place this is, if it is one of ours.
    /// </summary>
    private JoiningConnection? TakeMeshJoiner(int slot)
    {
        for (var i = 0; i < _meshJoining.Count; i++)
        {
            if (!_mesh!.Node.TryGetAssignedSlot(_mesh.Origin, _meshJoining[i].Sequence, out var at)
                || at != slot)
            {
                continue;
            }

            var joining = _meshJoining[i].Joining;
            _meshJoining.RemoveAt(i);

            return joining;
        }

        return null;
    }
}
