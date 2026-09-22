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

    /// <summary>
    /// Bots whose roster place a mesh has not agreed yet.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>The population manager has to count these as already added.</b> A
    /// meshed join takes about ten ticks to land, and the manager fills by
    /// comparing the roster against its target every tick — so without this it
    /// asks for the whole shortfall again on every one of those ticks.
    /// Measured on a live two-host run: a server told to fill to four added
    /// FORTY-EIGHT bots and then spent the next minute removing them again.
    /// Zero unless this server is meshed.
    /// </remarks>
    public int BotsPending => _meshBots.Count;

    /// <summary>
    /// Whether this node is the one that fills the match with bots.
    /// </summary>
    /// <remarks>
    /// True for a standalone server and for a mesh LISTENER; false for a joiner,
    /// which shares the listener's roster and would otherwise fill it a second
    /// time. See <c>BotPopulationService.Service</c> for what that cost.
    /// </remarks>
    public bool MeshFillsBots => _mesh is null || _mesh.Listening;

    /// <summary>
    /// Adds one bot to this node, bypassing the population manager.
    /// </summary>
    /// <remarks>
    /// For a mesh joiner, which does not fill the match — see
    /// <see cref="MeshFillsBots"/>. Works on a standalone server too, where it
    /// is simply one more bot than the target asked for and the manager will
    /// remove it again.
    /// </remarks>
    public bool AddMeshBot() => _population?.AddOne() ?? false;

    /// <summary>
    /// Players whose departure has been scheduled and has not landed yet.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>The mirror of <see cref="BotsPending"/>, and it bites the same
    /// way.</b> A meshed removal takes about ten ticks to leave the roster, and
    /// the population manager compares the roster against its target every tick
    /// — so without this it asks for the same removal again on every one of
    /// those ticks. Measured on a live two-host run: a listener that needed to
    /// drop four bots dropped ALL SIX, ended up driving nobody, and its peer had
    /// nothing left to correct it about.
    /// </remarks>
    public int BotsLeaving => _meshLeaving.Count;

    /// <summary>
    /// Whether this player's departure has been scheduled and not landed yet.
    /// </summary>
    /// <remarks>
    /// Always false on a standalone server, where a removal is immediate and
    /// there is no window in which to ask twice.
    /// </remarks>
    public bool IsLeaving(int slot) =>
        _mesh is not null
        && (uint)slot < _roster.Count
        && _meshLeaving.Contains(_roster[slot].StableId);

    /// <summary>
    /// How many players the world holds, which in a mesh is the node's answer
    /// rather than this server's roster.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>It LEADS the roster by a tick, and that is not a fault.</b> The node
    /// applies a removal when it advances, at the bottom of a tick;
    /// <see cref="DrainMeshCommands"/> replays it against the roster at the top
    /// of the next one. So a world one shorter than the roster is the ordinary
    /// state for exactly one tick after every departure. A gap that persists is
    /// the fault <see cref="MirrorMeshRoster"/>'s warning reports.
    /// </remarks>
    public int WorldPlayerCount
    {
        get
        {
            var inWorld = 0;

            for (var slot = 0; slot < WorldState.MaxSlots; slot++)
            {
                if (_world.HasPlayer[slot])
                    inWorld++;
            }

            return inWorld;
        }
    }

    /// <summary>This node's own count of joins, which names them to the peer.</summary>
    private int _meshSequence;

    /// <summary>
    /// What <see cref="AddBot"/> returns in a mesh: no slot yet, ask again
    /// through <c>onAdded</c>.
    /// </summary>
    public const int MeshPending = -1;

    /// <summary>The rollback window, past which a blocked-slot answer is meaningless.</summary>
    private const int MeshOptionsWindow = 32;

    /// <summary>Last tick's confirmation lag, so the moment it jumps is reported once.</summary>
    private int _meshLagWas;

    /// <summary>What Confirmed read at the previous report, so a STUCK one is visible.</summary>
    private int _meshConfirmedWas = -1;

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
    /// The join that gave each player their place, by
    /// <see cref="RosterEntry.StableId"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⛔ <b>Recorded when the player is admitted, and never re-derived.</b> A
    /// departure names the join that created the place rather than the slot it
    /// currently occupies (<see cref="MeshRemove"/>), and the node's own table
    /// is the wrong place to ask for that later: the node applies a removal at
    /// the bottom of a tick and <see cref="DrainMeshCommands"/> replays it
    /// against the roster at the top of the next one. For that one tick every
    /// slot above the hole means a different player to each of them — and
    /// <c>ServiceClients</c> and <c>ServiceBots</c> both run above the drain, so
    /// a disconnect is decided in exactly that window.
    /// </para>
    /// <para>
    /// ⚠️ <b>The failure it prevents is silent.</b> Both nodes are told to
    /// remove the join the stale slot resolved to, so both remove the same wrong
    /// person and their worlds go on agreeing — no digest can see it.
    /// <c>MeshJoinTests.ADepartureDecidedWhileTheNodeIsAheadTakesTheRightPlayer</c>
    /// is the gate, and it asserts the neighbour STAYED as well as that the
    /// named player left, because the count is right either way.
    /// </para>
    /// <para>
    /// Keyed by <c>StableId</c> for the same reason <see cref="_meshLeaving"/>
    /// is: a slot is a position, and positions move under exactly the thing this
    /// is tracking. The key survives a map change, because a rebuild carries the
    /// assignments and moves nobody.
    /// </para>
    /// </remarks>
    private readonly Dictionary<long, (int Origin, int Sequence)> _meshJoinKey = [];

    /// <summary>
    /// Confirmed ticks' events, waiting their turn on the wire.
    /// </summary>
    /// <remarks>
    /// One tick confirms for every tick simulated once inputs are flowing, so
    /// this holds nothing in the steady state. It fills when confirmation
    /// stalls — a peer gone quiet — and drains a tick at a time afterwards,
    /// which keeps one tick's events in one tick's packet rather than emptying
    /// a backlog into a single flush.
    /// </remarks>
    private readonly Queue<TickEvents> _meshEvents = new();

    /// <summary>
    /// Confirmed event-ticks handed to the broadcast.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Diagnostic, and NOT a gate.</b> It counts the drain, which a server
    /// that drains and then broadcasts the provisional events anyway would also
    /// do — measured: that mutation passes against this counter and fails
    /// against <c>MeshJoinTests.ASilentPeerHoldsEveryEventAndStopsNothingElse</c>,
    /// which counts what a client actually received.
    /// </remarks>
    public long MeshEventsReleased { get; private set; }

    /// <summary>How many are waiting, which is 0 or 1 unless confirmation stalled.</summary>
    public int MeshEventsHeld => _meshEvents.Count;

    /// <summary>Slots this node sent inputs for on the last tick. Zero is a fault.</summary>
    public int MeshOwnedSlots => _mesh?.Owned ?? 0;

    /// <summary>
    /// The tick this node's mesh is on, or -1 when there is no mesh.
    /// </summary>
    public int MeshTick => _mesh?.Node.Tick ?? -1;

    /// <summary>
    /// This node's world at a PAST tick as one number, or null if that tick is
    /// outside the window.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>At a tick, never live.</b> Two nodes are only comparable at the same
    /// tick — one that is a few ticks behind has a different world for entirely
    /// correct reasons, and comparing the two live states reports that as a
    /// divergence.
    /// </remarks>
    public ulong? MeshDigestAt(int tick) => _mesh?.Node.DigestAt(tick);

    /// <summary>
    /// The next confirmed tick's events, or none at all while nothing is
    /// certain.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⛔ <b>Nothing is the right answer when nothing is confirmed.</b> A node
    /// whose peer has gone quiet cannot know whether the deaths it is
    /// simulating will survive the inputs still in flight, so it says nothing
    /// about them and goes on sending positions. The client sees players moving
    /// and no deaths, which is recoverable; the alternative is a death it can
    /// never take back.
    /// </para>
    /// <para>
    /// ⚠️ <b>A tick is confirmed when every live player's real input for it has
    /// been heard</b>, which cannot be rewound because a rewind is only ever
    /// scheduled by an input contradicting a GUESS. With no peer connected every
    /// player is local, so every tick confirms at once and this is a queue of
    /// one.
    /// </para>
    /// </remarks>
    private TickEvents TakeMeshEvents()
    {
        while (_mesh!.Node.TryTakeConfirmed(out _, out var events))
            _meshEvents.Enqueue(events);

        if (_meshEvents.Count == 0)
            return default;

        MeshEventsReleased++;

        return _meshEvents.Dequeue();
    }

    /// <summary>
    /// Says, once every ten seconds, whether the two nodes still agree.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⛔ <b>Without this a running mesh cannot be observed at all.</b> The
    /// digests are exchanged and compared every second and the result went
    /// nowhere: no log line, no control verb, nothing on the panel. The one
    /// claim the whole design rests on — that two nodes reach the same world —
    /// was true or false in a counter nobody could read, which is exactly the
    /// shape of claim CLAUDE.md warns about.
    /// </para>
    /// <para>
    /// ⚠️ <b>A disagreement is reported at WARNING and never recovered from.</b>
    /// Nothing here repairs a divergence and nothing should: it means a bug, or
    /// two different operating systems (docs/SIMULATION.md), and the honest
    /// response is to say so loudly rather than to paper over it.
    /// </para>
    /// </remarks>
    /// <summary>
    /// <summary>
    /// Names each slot holding confirmation up, and WHO OWNS IT.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Ownership is the question a blocked slot raises.</b> A slot nobody
    /// owns gets inputs from nowhere: this node guesses it because it is the
    /// peer's, the peer guesses it because it is ours, and it stays alive and
    /// unheard-from for ever — so its tick never confirms and event release
    /// stops for the rest of the match. <c>MeshNode.OriginOf</c> returns 255 for
    /// nobody, which is the answer that would explain everything.
    /// </remarks>
    private string DescribeBlocked(ulong mask)
    {
        if (mask == 0)
            return "nothing";

        var parts = new List<string>();

        for (var slot = 0; slot < 64 && slot < WorldState.MaxSlots; slot++)
        {
            if ((mask & (1UL << slot)) == 0)
                continue;

            var origin = _mesh!.Node.OriginOf(slot);
            var name = slot < _roster.Count ? LogText.Safe(_roster[slot].Name) : "<past roster>";

            parts.Add(
                $"slot {slot} (origin {(origin == 255 ? "NOBODY" : origin.ToString())}, "
                + $"{(_mesh.Owns(slot) ? "ours" : "theirs")}, \"{name}\")");
        }

        return string.Join(" + ", parts);
    }

    private void ReportMesh()
    {
        if (_mesh is null)
            return;

        // ⛔ EVERY TICK, not every report. Measured: a wedged run goes from under
        // four ticks behind to past the whole 32-tick window inside one
        // 300-tick reporting gap — confirmation does not drift, it JUMPS. A
        // check that runs only with the report cannot see the moment, and the
        // blocking slot is unnameable a window later because the ring has
        // recycled by then. Said ONCE per episode, so a wedge costs one line
        // rather than one every tick for the rest of the match.
        //
        // ⛔ THE THRESHOLD IS HALF THE WINDOW, and that is not a number chosen
        // for taste. A healthy frontier sits about one round trip back — nine
        // ticks on a three-tick link — so the four this used to be is BELOW the
        // ordinary lag of any real link: it fires in the first seconds of a
        // match and, being once per episode, can then never fire again, wedge or
        // no wedge. Measured on a batch where two runs wedged and neither
        // produced a line. Half the window clears any sane link's lag and is
        // still inside the ring, which is the last moment BlockedAt can name a
        // slot at all.
        var lag = _mesh.Node.Tick - _mesh.Node.Confirmed;
        var alarm = MeshOptionsWindow / 2;

        if (lag >= alarm && _meshLagWas < alarm)
        {
            _log.Warn(
                $"mesh: confirmation fell behind at tick {_mesh.Node.Tick} — "
                + $"confirmed {_mesh.Node.Confirmed}, {lag} behind, "
                + $"waiting on {DescribeBlocked(_mesh.Node.BlockedAt(_mesh.Node.Confirmed))}, "
                + $"owned {_mesh.Owned}, roster {_roster.Count}, "
                + $"world {WorldPlayerCount}");
        }

        _meshLagWas = lag;

        // Ten seconds. Quiet enough to leave on, often enough that an operator
        // watching a match sees the two nodes agreeing rather than inferring it.
        if (_frame % 300 != 0)
            return;

        var stats = _mesh.Node.Stats;

        var line =
            $"mesh: tick {_mesh.Node.Tick}, "
            + $"{(_mesh.Connected ? "linked" : "NO PEER")}, "
            + $"{(_mesh.Synchronised ? "in step" : "WAITING FOR THE MATCH")}, "
            + $"agreed {_mesh.Agreements}/{_mesh.Agreements + _mesh.Disagreements} "
            + $"({_mesh.Unconfirmed} early), "
            + $"rollbacks {stats.Rollbacks} ({stats.ResimulatedTicks} ticks re-simulated, "
            + $"deepest {stats.DeepestRollback}), "
            + $"late {stats.TooLate}, "
            + $"events {MeshEventsReleased} released / {MeshEventsHeld} held, "
            + $"owned {_mesh.Owned}, "
            // ⛔ The confirmation frontier belongs on this line. Everything else
            // here can look healthy while it is stuck — and when it is stuck,
            // nothing irreversible reaches a client again. `events released`
            // freezing is the same fault seen one step later; this is the cause
            // rather than the symptom, and the gap against `tick` is the size of
            // the problem.
            + $"confirmed {_mesh.Node.Confirmed} (tick-{_mesh.Node.Tick - _mesh.Node.Confirmed}), "
            + $"link [{_mesh.LinkStats}]";

        // ⛔ A WEDGED CONFIRMATION IS INVISIBLE WITHOUT THIS. Once Confirmed
        // stops advancing nothing irreversible is ever published again, and
        // every other number on this line stays healthy — the node ticks, the
        // link is up, ownership is fine. Measured: a run froze at tick 289 and
        // ran on for 5,700 more looking perfectly well. The slot is named
        // because the only fix is to find why that player stopped being heard.
        //
        // ⚠️ Only when it is STUCK. A blocked frontier is the ordinary state of
        // the newest tick, so reporting it unconditionally would cry wolf on
        // every healthy server.
        var behind = _mesh.Node.Tick - _mesh.Node.Confirmed;

        if (_mesh.Node.Confirmed == _meshConfirmedWas && behind > 1)
        {
            // ⛔ THE MASK IS ONLY MEANINGFUL INSIDE THE WINDOW. BlockedAt returns
            // 0 for a tick outside the ring, which is indistinguishable from
            // "nothing is blocking" — and by the time a wedge is noticed,
            // Confirmed is thousands of ticks behind and every answer is 0.
            // Measured: three wedged runs all reported `slots 0` while stuck
            // 2,653 to 4,088 ticks back, which says nothing at all.
            var mask = behind < MeshOptionsWindow
                ? $"waiting on slots {_mesh.Node.BlockedAt(_mesh.Node.Confirmed):x}"
                : "PAST THE WINDOW — the blocking slot can no longer be named";

            line += $", ⛔ CONFIRMATION STUCK at {_mesh.Node.Confirmed} "
                + $"({behind} ticks behind), {mask}";
        }
        else if (behind >= alarm && behind < MeshOptionsWindow)
        {
            // ⚠️ STILL FALLING, and still nameable. A wedge is permanent once
            // Confirmed is a whole window back — Confirm()'s own guard then
            // refuses to advance — so the cause has to be caught before that.
            // ⚠️ Half the window rather than four ticks, for the reason the
            // per-tick check above gives: on a real link four is the ordinary
            // lag, and this printed on 378 healthy report lines in one batch.
            line += $", ⚠️ confirmation lagging {behind}, "
                + $"waiting on slots {_mesh.Node.BlockedAt(_mesh.Node.Confirmed):x}";
        }

        _meshConfirmedWas = _mesh.Node.Confirmed;

        if (_mesh.RefusedFromPeer > 0)
        {
            line += $", REFUSED {_mesh.RefusedFromPeer} (worst skew {_mesh.WorstRefusalSkew} ticks)";
        }

        if (_mesh.Disagreements > 0 || _mesh.RefusedFromPeer > 0)
            _log.Warn($"{line} — THE TWO NODES DISAGREE");
        else
            _log.Info(line);
    }

    /// <summary>
    /// Asks the mesh for a roster place for a joiner, and parks them until it
    /// arrives.
    /// </summary>
    private void MeshAdmit(JoiningConnection joining)
    {
        var sequence = ++_meshSequence;

        // ⛔ Refused rather than parked behind a command nobody will hear — see
        // MeshAddBot. A client that arrives in the seconds before two nodes are
        // in step is turned away and can try again, which is recoverable; a
        // client parked for a slot that is never agreed is not.
        if (_mesh!.Schedule(MeshCommand.AddPlayer(_mesh.Origin, sequence), joining.Name)
            == MeshSession.Refusal)
        {
            joining.Connection.Close("the mesh is not ready yet");
            return;
        }

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

        // ⛔ NOT PARKED IF THE MESH WOULD NOT CARRY IT. A command sent before the
        // two nodes are in step is refused at the far end, and a bot parked
        // behind one waits for a slot that is never coming — which also inflates
        // BotsPending for ever and stops the population manager ever filling
        // again. Dropping it instead leaves the manager free to ask once more
        // next tick, which it does until the mesh is ready.
        if (_mesh!.Schedule(MeshCommand.AddPlayer(_mesh.Origin, sequence), name)
            == MeshSession.Refusal)
        {
            return MeshPending;
        }

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
        var id = _roster[slot].StableId;

        if (!_meshLeaving.Add(id))
            return;

        // ⛔ FROM THIS SERVER'S OWN TABLE, never by asking the node about the
        // slot. The node renumbers a tick ahead of the roster, and both callers
        // of this method run inside that tick — so a slot resolved here against
        // the node names whoever shifted down into the place, and BOTH nodes
        // then remove that person while their digests agree. See _meshJoinKey.
        if (!_meshJoinKey.TryGetValue(id, out var join))
        {
            // No join was ever recorded for this player, so there is nothing to
            // name. Slot 0 is the reserved player and never leaves; anything
            // else here is a roster and a node that have come apart, which is
            // worth a line rather than a silent no-op.
            //
            // ⚠️ The id comes back out, exactly as a refusal below puts it back.
            // BotsLeaving is what tells the population manager it is already
            // shedding, and one id left behind here stops it ever removing
            // another.
            _meshLeaving.Remove(id);
            _log.Warn($"{who} cannot leave the mesh: no join was recorded for player {slot}");
            return;
        }

        var origin = join.Origin;
        var sequence = join.Sequence;

        // ⛔ FORGOTTEN AGAIN IF THE MESH WILL NOT CARRY IT, exactly as a parked
        // bot is. A departure whose command was refused never lands, so the id
        // would sit here for ever — and BotsLeaving is what tells the population
        // manager it is already shedding, so one leaked id stops it ever
        // removing another. Measured: a server stuck two bots above its target
        // with no way down.
        if (_mesh!.Schedule(MeshCommand.RemovePlayer(origin, sequence), _roster[slot].Name)
            == MeshSession.Refusal)
        {
            _meshLeaving.Remove(id);
        }
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
        DrainMeshChat();
        DrainMeshNames();

        while (_roster.Count < WorldState.MaxSlots && _world.HasPlayer[_roster.Count])
        {
            var slot = _roster.Count;

            if (TakeMeshJoiner(slot) is { } joining)
            {
                // A client of ours, whose place has now been agreed with the
                // peer. Everything from here is the ordinary join sequence.
                CompleteJoin(joining);
            }
            else if (TakeMeshBot(slot) is { } bot)
            {
                CompleteMeshBot(slot, bot);
            }
            else
            {
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

            // ⛔ HERE, and nowhere later. This is the one moment the roster and
            // the node are known to describe the same people: the drain above
            // has just replayed every removal the node has applied, and this
            // walk is putting the new arrival at the slot the node gave it. From
            // the next removal onwards the node leads the roster and the same
            // question gets a different answer — see _meshJoinKey.
            //
            // ⚠️ The peer's players are recorded too, though only our own can be
            // removed from here. It costs a dictionary entry and it means a
            // missing key is always a fault rather than sometimes the norm.
            if (slot < _roster.Count
                && _mesh.Node.TryGetJoinKey(slot, out var origin, out var sequence))
            {
                _meshJoinKey[_roster[slot].StableId] = (origin, sequence);
            }
        }

        // ⛔ THE WORLD IS AUTHORITATIVE FOR TEAM AND CLASS, so the roster is
        // brought into line with it every tick rather than only when a command
        // lands. A peer's player reaches this roster through a snapshot or a
        // join, neither of which carries what team they are on — and the team is
        // what every client is told. Measured on a live two-host run: one node
        // showed four players on Red and Blue and the other showed the same four
        // as spectators, while the two worlds agreed perfectly.
        //
        // ⚠️ Cheap and self-healing: it is a handful of slots, and it cannot
        // drift because it is copied from the thing that decides.
        for (var slot = 1; slot < _roster.Count && slot < WorldState.MaxSlots; slot++)
        {
            _roster[slot].Team = _world.Players[slot].Team;
            _roster[slot].Class = _world.Players[slot].Class;
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
            // ⚠️ The world's count goes in the line. "The roster is longer"
            // alone cannot be acted on: it does not say whether the world lost
            // one player or all of them, and those have different causes.
            var inWorld = WorldPlayerCount;

            _log.Warn(
                $"mesh: the roster holds {_roster.Count} players and the world holds "
                + $"{inWorld} — a player left the world without a RemovePlayer. docs/MESH.md");
        }
    }

    /// <summary>
    /// Shows this node's clients the bubbles the peer's players made.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Not scheduled and not held.</b> A bubble never reaches
    /// <c>WorldState</c>, so there is nothing for two nodes to agree about and
    /// nothing a rewind could unmake — it is shown when it arrives.
    /// <para>
    /// ⛔ <b>The slot is checked against THIS roster before it goes out.</b> It
    /// was resolved against the peer's, which is the same roster only for as
    /// long as both nodes are in step; a stale one would put somebody else's
    /// bubble over a player's head, and a slot past the end would name a player
    /// no client has.
    /// </para>
    /// </remarks>
    private void DrainMeshChat()
    {
        while (_mesh!.ChatFromPeer.TryDequeue(out var bubble))
        {
            if (bubble.Slot > 0 && bubble.Slot < _roster.Count)
                ServerMessages.ChatBubble(_broadcast, (byte)bubble.Slot, bubble.Image);
        }
    }

    /// <summary>
    /// Renames the peer's players here, so both regions call them the same
    /// thing.
    /// </summary>
    private void DrainMeshNames()
    {
        while (_mesh!.NamesFromPeer.TryDequeue(out var renamed))
        {
            if (renamed.Slot <= 0 || renamed.Slot >= _roster.Count)
                continue;

            _roster[renamed.Slot].Name = renamed.Name;
            ServerMessages.PlayerChangeName(_broadcast, (byte)renamed.Slot, renamed.Name);
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

                    // ⛔ The node ignores this one — it cannot load a map, and
                    // the collision world and spawn points are read-only inputs
                    // it is given. So the SERVERS do it, both of them, at the
                    // tick the command landed at, from the same reset world.
                    case MeshCommandKind.ChangeMap
                        when _mesh.MapChangeAt(_meshDrained) is { Length: > 0 } map:
                    {
                        _nextMap = map;
                        _nextArea = Math.Max(1, (int)command.A);
                        _mapChangeCountdown = -1;

                        _log.Info($"mesh: changing to {LogText.Safe(map)} area {_nextArea}");
                        // ⛔ Rebuilt at the tick AFTER the one the change landed
                        // at, which both nodes compute the same way.
                        PerformMapChange(_meshDrained + 1);

                        // ⛔ Nothing after this in the tick's commands can mean
                        // anything: the roster has been reset and the node
                        // replaced. Both nodes stop here for the same reason.
                        return;
                    }

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

                // The join that made this place is spent, and the id is never
                // reissued — so this is the only thing that keeps the table the
                // size of the match rather than the size of everyone who has
                // ever played on it.
                _meshJoinKey.Remove(leaving.StableId);

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
