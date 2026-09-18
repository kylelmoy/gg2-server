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

    /// <summary>Bots waiting for the same thing, with what to do when it lands.</summary>
    private readonly List<MeshBot> _meshBots = [];

    /// <summary>A bot that has asked the mesh for a slot and not yet been given one.</summary>
    private readonly record struct MeshBot(
        int Sequence,
        string Name,
        Team Team,
        PlayerClass Class,
        IInputSource Source,
        Action<int>? OnAdded);

    /// <summary>This node's own count of joins, which names them to the peer.</summary>
    private int _meshSequence;

    /// <summary>
    /// What <see cref="AddBot"/> returns in a mesh: no slot yet, ask again
    /// through <c>onAdded</c>.
    /// </summary>
    public const int MeshPending = -1;

    /// <summary>The first tick whose landed commands have not been told to clients.</summary>
    private int _meshDrained = -1;

    /// <summary>
    /// Players whose departure has been scheduled and has not landed yet.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Keyed by <see cref="RosterEntry.StableId"/>, never by slot</b>,
    /// because a slot is a position and positions move under exactly the thing
    /// this is tracking. It stops a second <c>RemovePlayer</c> being scheduled
    /// for somebody already on their way out — harmless in the node, which
    /// resolves the second to nothing, but it would say so twice on the wire.
    /// </remarks>
    private readonly HashSet<long> _meshLeaving = [];

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
    /// Asks the mesh for a roster place for a bot, and parks it until one
    /// arrives.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A bot joins the way a person does</b>, which is the point: the
    /// simulation never special-cases one (CLAUDE.md), and a mesh is one more
    /// place that has to hold. The slot comes from an <c>AddPlayer</c> both
    /// nodes apply at an agreed tick, and the bot's keys reach the peer from
    /// then on because <c>MeshSession.Owns</c> reads the node's record of who
    /// issued the join.
    /// </para>
    /// <para>
    /// ⚠️ <b>No <c>checkClasslimits</c>, and that is not an oversight.</b>
    /// <c>Classlimits.Pick</c> DRAWS FROM <c>WorldState.Random</c> — the
    /// observable stream every client re-derives weapon spreads from — so a
    /// node that ran it where its peer did not would put the two streams in
    /// different places and break the mesh outright. Running it inside the node
    /// instead is possible and is not done here: a meshed bot takes the class it
    /// asked for. The same difference from standalone as a meshed team change
    /// not rebalancing, and for the same kind of reason.
    /// </para>
    /// </remarks>
    private int MeshAddBot(
        string name, Team team, PlayerClass playerClass, IInputSource source, Action<int>? onAdded)
    {
        var sequence = ++_meshSequence;

        _mesh!.Schedule(MeshCommand.AddPlayer(_mesh.Origin, sequence), name);
        _meshBots.Add(new MeshBot(sequence, name, team, playerClass, source, onAdded));

        _log.Info($"bot {LogText.Safe(name)} asked the mesh for a slot ({team}, {playerClass})");

        return MeshPending;
    }

    /// <summary>
    /// Finishes a parked bot once the mesh has given it a place.
    /// </summary>
    /// <remarks>
    /// The roster half happens here and the world half is scheduled, because
    /// only the node may write the world. ⚠️ <b><c>ChangeTeam</c> and
    /// <c>ChangeClass</c> rather than <c>SetTeam</c> and <c>SetClass</c></b>:
    /// the real player commands arm the respawn that puts a character in the
    /// world, which is what a standalone <c>AddBot</c> does with
    /// <c>RespawnTimer = 1</c>. Setting the fields would leave a bot standing
    /// nowhere for ever.
    /// </remarks>
    private void CompleteMeshBot(int slot, MeshBot bot)
    {
        // Team and class go on the entry now, not when the commands land, so a
        // caller's onAdded can read them back through TeamOf and ClassOf the way
        // it can standalone. The mesh grants what was asked for — there is no
        // classlimits pick to wait for — so these are already the right answers.
        var entry = new RosterEntry
        {
            Name = bot.Name,
            IsBot = true,
            Source = bot.Source,
            Team = bot.Team,
            Class = bot.Class,
        };

        _roster.Add(entry);
        _plugins.PlayerAdded(slot);
        ServerMessages.PlayerJoin(_broadcast, entry.Name);

        _mesh!.Schedule(MeshCommand.SetBot(slot), entry.Name);
        _mesh.Schedule(MeshCommand.ChangeTeam(slot, bot.Team), entry.Name);
        _mesh.Schedule(MeshCommand.ChangeClass(slot, bot.Class), entry.Name);

        _log.Info(
            $"bot {LogText.Safe(bot.Name)} added as player {slot} "
            + $"({bot.Team}, {bot.Class}) by the mesh");

        bot.OnAdded?.Invoke(slot);
    }

    /// <summary>
    /// Schedules a departure, naming the player by the join that created their
    /// place.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⛔ <b>Named by the join, never by the slot.</b> A removal renumbers every
    /// slot above it, so a command carrying a slot number is only right until
    /// somebody below it leaves — and with two nodes there is always somebody
    /// else's departure that might land first. Both nodes would then agree on
    /// the wrong player, which no digest can catch because they still agree.
    /// </para>
    /// <para>
    /// ⚠️ <b>The roster place is NOT taken here.</b> It goes when the command
    /// lands, in <see cref="DrainMeshCommands"/>, so this node and its node
    /// remove the same person at the same tick. Until then the player is still
    /// in the match — pressing nothing, if their socket has gone.
    /// </para>
    /// </remarks>
    private void MeshRemove(int slot, string who)
    {
        if (!_meshLeaving.Add(_roster[slot].StableId))
            return;

        if (!_mesh!.Node.TryGetJoinKey(slot, out var origin, out var sequence))
        {
            // Nothing in the node's table claims this slot, so there is no join
            // to name. Slot 0 is the reserved player and never leaves; anything
            // else here is a roster and a node that have come apart, which is
            // worth a line rather than a silent no-op.
            _log.Warn($"{who} cannot leave the mesh: no join owns slot {slot}");
            return;
        }

        _mesh.Schedule(MeshCommand.RemovePlayer(origin, sequence), _roster[slot].Name);
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

        // Drained BEFORE the walk so a place vacated this tick can be filled by
        // this tick's join rather than the next one's. ⚠️ Not a correctness
        // rule: drained afterwards the walk simply finds nothing to add — the
        // world is already a player shorter than the roster — and the newcomer
        // appears one tick later instead. The order is worth keeping and is not
        // worth a claim that it prevents corruption.
        DrainMeshCommands();

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

            if (TakeMeshBot(slot) is { } bot)
            {
                CompleteMeshBot(slot, bot);
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

        // ⛔ NO TRIM FROM THE END. A departure is drained by the slot the node
        // reports it took, because trimming the tail would take the LAST roster
        // entry instead of the one that left: the counts would agree while every
        // entry above the hole named the wrong person, which is the shape of
        // corruption a state update cannot show. If the two lengths still
        // disagree here, something removed a player without a command and that
        // is worth saying rather than papering over.
        if (_roster.Count > 1 && !_world.HasPlayer[_roster.Count - 1])
        {
            _log.Warn(
                $"mesh: the roster holds {_roster.Count} players and the world does not — "
                + "a player left the world without a RemovePlayer. docs/MESH.md");
        }
    }

    /// <summary>
    /// Tells this node's clients about the roster changes that have just landed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Both halves of a command happen at the same tick, on both nodes.</b>
    /// The world half is the node's — it runs the real
    /// <c>PlayerCommands</c> so a team change drops the intel and kills the
    /// character everywhere. The half here is presentation: the roster copy a
    /// joiner's <c>FULL_UPDATE</c> is built from, and the message that tells
    /// clients. Splitting them is what lets a change that came from the PEER
    /// reach this node's clients at all.
    /// </para>
    /// <para>
    /// ⚠️ <b>Drained by tick, once, in order.</b> A rewind re-applies a tick's
    /// commands — that is the point of a schedule — so anything that broadcast
    /// per application would send a team change again every time the tick was
    /// replayed. The high-water mark is what makes it once.
    /// </para>
    /// </remarks>
    private void DrainMeshCommands()
    {
        if (_meshDrained < 0)
            _meshDrained = _mesh!.Node.Tick;

        for (; _meshDrained < _mesh!.Node.Tick; _meshDrained++)
        {
            foreach (var command in _mesh.Node.ScheduledFor(_meshDrained))
            {
                var slot = command.Slot;

                if (slot >= _roster.Count)
                    continue;

                switch (command.Kind)
                {
                    case MeshCommandKind.ChangeTeam:
                        _roster[slot].Team = (Team)command.A;
                        ServerMessages.PlayerChangeTeam(_broadcast, (byte)slot, (Team)command.A);
                        break;

                    case MeshCommandKind.ChangeClass:
                        _roster[slot].Class = (PlayerClass)command.A;
                        ServerMessages.PlayerChangeClass(
                            _broadcast, (byte)slot, (PlayerClass)command.A);
                        break;

                    default:
                        break;
                }
            }

            // ⛔ The removals come from the NODE, not from the commands, and
            // that is the whole reason RemovedAt exists: a RemovePlayer names a
            // join, applying it deleted that join's entry, and the slot it
            // resolved to has been renumbered away. Nothing here could work it
            // out afterwards — it has to be told which places were taken.
            //
            // ⚠️ In the node's own order, which is descending nowhere and
            // arbitrary everywhere: each removal renumbers the ones after it on
            // both sides at once, so replaying them in the same order against
            // the roster is what keeps the two in step.
            foreach (var slot in _mesh.Node.RemovedAt(_meshDrained))
            {
                if (slot == 0 || slot >= _roster.Count)
                    continue;

                var leaving = _roster[slot];

                // ⛔ BEFORE the removal, so a plugin's per-player table compacts
                // with the roster rather than after it — see RemoveBot.
                _plugins.PlayerRemoving(slot);

                _roster.RemoveAt(slot);
                _meshLeaving.Remove(leaving.StableId);

                ServerMessages.PlayerLeave(_broadcast, slot);

                _log.Info($"{LogText.Safe(leaving.Name)} left player {slot} by the mesh");

                // ⚠️ No TeamBalance.Check, where a standalone departure runs one
                // (GameServerBeginStep.gml:70). It writes the world, and only
                // the node may do that; balancing here would be a change the
                // peer never made. The same difference from standalone as a
                // meshed team change not rebalancing. docs/MESH.md.
            }
        }
    }

    /// <summary>
    /// The parked bot whose place this is, if it is one of ours.
    /// </summary>
    private MeshBot? TakeMeshBot(int slot)
    {
        for (var i = 0; i < _meshBots.Count; i++)
        {
            if (!_mesh!.Node.TryGetAssignedSlot(_mesh.Origin, _meshBots[i].Sequence, out var at)
                || at != slot)
            {
                continue;
            }

            var bot = _meshBots[i];
            _meshBots.RemoveAt(i);

            return bot;
        }

        return null;
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
