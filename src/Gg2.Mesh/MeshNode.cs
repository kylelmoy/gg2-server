using System.Linq;
using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Mesh;

/// <summary>
/// One node of a match that is running in more than one place: it simulates the
/// whole world, guesses at the inputs still in flight, and rewinds when a guess
/// turns out to have been wrong.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every node simulates everybody.</b> There is no authority here and
/// nothing to hand off: a node's own players' inputs are exact the moment they
/// are pressed, a peer's arrive a link away, and two nodes reach the same world
/// as soon as both know the same inputs. That is what makes the design
/// symmetric — neither region waits a round trip for the other's verdict, only
/// one way for the other's keys.
/// </para>
/// <para>
/// ⛔ <b>It rests on <c>MatchSimulation.Tick</c> being a pure function of
/// (state, inputs), and on <c>WorldState</c> holding no reference to
/// anything.</b> A rewind is a struct copy and a re-run; if either stopped being
/// true, a rollback would restore a world sharing mutable parts with the one it
/// replaced and the corruption would be silent. <c>CLAUDE.md</c>'s first
/// non-negotiable is what this is built on.
/// </para>
/// <para>
/// ⚠️ <b>What a node does NOT do yet:</b> hold irreversible events. A death, a
/// capture or an intel pickup cannot be taken back on a stock client, so a
/// deployment has to delay those until every input for their tick is known while
/// movement goes out provisionally. This type produces the corrected world;
/// deciding what of it is safe to send is the next layer's job and is not
/// written. `docs/MESH.md`.
/// </para>
/// </remarks>
public sealed class MeshNode
{
    private readonly CollisionWorld _world;
    private readonly SpawnPoints? _spawnPoints;
    private readonly MeshOptions _options;

    /// <summary>
    /// The state at the START of each tick, indexed by tick modulo the window.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Boxes, not a <c>WorldState[]</c>, and the CLR is what decides
    /// that.</b> A <c>WorldState</c> is far past the size limit the runtime puts
    /// on an array's element type — <c>Array of type 'WorldState' cannot be
    /// created because base value type is too large</c> — so the window is an
    /// array of holders, each allocated once in the constructor. Nothing here
    /// allocates per tick, which is the rule that matters.
    /// </remarks>
    private readonly Snapshot[] _snapshots;

    /// <summary>What each tick produced, indexed the same way as the snapshots.</summary>
    private readonly Outcome[] _outcomes;

    /// <summary>One tick's world, on the heap so the window can be an array.</summary>
    private sealed class Snapshot
    {
        public WorldState State;
    }

    /// <summary>
    /// What each tick of the window reported, and whether that tick was run on
    /// facts alone.
    /// </summary>
    /// <remarks>
    /// <b>This is what makes holding an irreversible event possible.</b> A tick
    /// simulated with a guess in it may be re-simulated into a different tick,
    /// so the death it reported may never have happened; a tick every one of
    /// whose inputs was known cannot change, because a rewind is only ever
    /// scheduled by an input contradicting a GUESS. Releasing the second kind
    /// and holding the first is the whole rule.
    /// </remarks>
    private sealed class Outcome
    {
        public TickEvents Events;
    }

    /// <summary>
    /// What every slot did on each tick, and whether that is known or a guess.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Twice the snapshot window, because a row can be written from both
    /// directions.</b> A peer's input can arrive for a tick this node has not
    /// reached, while the tick a whole window behind it is still needed to
    /// rewind to. Sized to the rollback window alone, those two would be the
    /// same row and an early arrival would quietly destroy the history a
    /// rollback needs.
    /// </remarks>
    private readonly PlayerInput[] _inputs;
    private readonly bool[] _known;

    /// <summary>Which tick each ledger row currently holds, so a wrap clears rather than lies.</summary>
    private readonly int[] _rowTick;

    /// <summary>
    /// Roster changes waiting for their tick, one list per ledger row.
    /// </summary>
    /// <remarks>
    /// <b>Kept by tick rather than applied on arrival, which is what makes a
    /// rewind safe.</b> A tick that is re-simulated runs its commands again
    /// because they are part of what that tick IS, in the same order both nodes
    /// compute from the data. Applied on arrival instead, a join inside a
    /// rewound window would be undone by the rewind and never come back.
    /// </remarks>
    private readonly List<MeshCommand>[] _scheduled;
    private readonly int[] _scheduledTick;

    /// <summary>
    /// Which slot each join was given, by the issuer and their own counter.
    /// </summary>
    /// <remarks>
    /// <b>The issuer cannot know its player's slot until the join is applied</b>,
    /// because the slot falls out of the order every node computes rather than
    /// being asked for. This is where it finds out. Keyed by (origin, sequence)
    /// so a rewind that re-applies the join writes the same answer again.
    /// </remarks>
    private readonly Dictionary<int, int> _assigned = [];

    /// <summary>
    /// Which node issued the join that owns each slot, or 255 for nobody.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Ownership follows the ISSUER, not the slot number.</b> It used to be
    /// read off the slot's parity, which was right only while slots were handed
    /// out by parity — once a join takes the lowest free place instead, a node's
    /// own player can land on any number at all. A node that guessed ownership
    /// from parity would predict inputs for a player it is itself driving, and
    /// never send that player's keys to the peer.
    /// </remarks>
    private readonly byte[] _slotOrigin = BuildOrigins();

    /// <summary>
    /// Which slots a tick's removals actually took, keyed by tick.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⛔ <b>A removal cannot be resolved after the fact.</b> The command names a
    /// join, applying it deletes that join's entry from
    /// <see cref="_assigned"/>, and the slot it named has been renumbered away —
    /// so a server draining the schedule a tick later has no way to work out
    /// whose roster place to take. It has to be told, and this is the telling.
    /// </para>
    /// <para>
    /// Kept in the same ring the schedule uses, and rewritten whenever the tick
    /// is applied again, so a rewind through a departure leaves the answer the
    /// re-application produced rather than the first one.
    /// </para>
    /// </remarks>
    private readonly List<byte>[] _removed;
    private readonly int[] _removedTick;

    private static byte[] BuildOrigins()
    {
        var origins = new byte[WorldState.MaxSlots];
        Array.Fill(origins, (byte)255);

        return origins;
    }

    /// <summary>The newest input heard for each slot, which is what a guess repeats.</summary>
    private readonly PlayerInput[] _latest;
    private readonly int[] _latestTick;

    /// <summary>Scratch for one tick's inputs, so a tick allocates nothing.</summary>
    private readonly PlayerInput[] _scratch = new PlayerInput[WorldState.MaxSlots];

    private readonly int _rows;

    private WorldState _state;
    private int _rewindTo = -1;

    /// <summary>The furthest tick ever simulated, which is what says a Step is new ground.</summary>
    private int _head;

    private int _ticks;
    private int _rollbacks;
    private int _resimulated;
    private int _deepest;
    private int _tooLate;

    public MeshNode(
        CollisionWorld world, in WorldState start, MeshOptions? options = null,
        SpawnPoints? spawnPoints = null, int startTick = 0)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentOutOfRangeException.ThrowIfNegative(startTick);

        _options = options ?? new MeshOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxRollbackTicks, 1);

        _world = world;
        _spawnPoints = spawnPoints;
        _state = start;
        Tick = startTick;
        _head = startTick;

        _snapshots = new Snapshot[_options.MaxRollbackTicks];
        _outcomes = new Outcome[_options.MaxRollbackTicks];

        for (var i = 0; i < _snapshots.Length; i++)
        {
            _snapshots[i] = new Snapshot();
            _outcomes[i] = new Outcome();
        }

        Confirmed = startTick;

        _rows = _options.MaxRollbackTicks * 2;
        _inputs = new PlayerInput[_rows * WorldState.MaxSlots];
        _known = new bool[_rows * WorldState.MaxSlots];
        _rowTick = new int[_rows];
        _scheduled = new List<MeshCommand>[_rows];
        _scheduledTick = new int[_rows];
        _removed = new List<byte>[_rows];
        _removedTick = new int[_rows];

        for (var i = 0; i < _rows; i++)
        {
            _scheduled[i] = [];
            _removed[i] = [];
        }

        _latest = new PlayerInput[WorldState.MaxSlots];
        _latestTick = new int[WorldState.MaxSlots];

        Array.Fill(_rowTick, -1);
        Array.Fill(_scheduledTick, -1);
        Array.Fill(_removedTick, -1);
        Array.Fill(_latestTick, -1);
    }

    /// <summary>The tick this node is about to simulate.</summary>
    public int Tick { get; private set; }

    /// <summary>The world as of the start of <see cref="Tick"/>.</summary>
    public ref readonly WorldState State => ref _state;

    /// <summary>A 64-bit summary, for asking a peer whether it agrees.</summary>
    public ulong Digest => StateDigest.Of(in _state);

    /// <summary>
    /// A digest of the world as it stood at the start of a past tick, or null if
    /// that tick is no longer in the window.
    /// </summary>
    /// <remarks>
    /// <b>This is what a peer's digest has to be compared against.</b> Comparing
    /// against the LIVE state instead compares two provisional worlds — each
    /// node is still guessing about the other's most recent ticks — and two
    /// nodes in perfect agreement about the match will differ there for as long
    /// as the link is longer than nothing. Ask about a confirmed tick and the
    /// answer means something.
    /// </remarks>
    public ulong? DigestAt(int tick)
    {
        if (tick < 0 || tick >= Tick || tick <= Tick - _snapshots.Length)
            return null;

        return StateDigest.Of(in _snapshots[tick % _snapshots.Length].State);
    }

    /// <summary>
    /// The first tick whose outcome is not yet final. Everything before it ran
    /// on facts alone and can no longer change.
    /// </summary>
    public int Confirmed { get; private set; }

    /// <summary>
    /// The tick a caller has released events up to, which never passes
    /// <see cref="Confirmed"/>.
    /// </summary>
    private int _released;

    /// <summary>
    /// Takes the next confirmed tick's events, in order, or reports that there
    /// are none left to take.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⛔ <b>This is the only safe source for anything a client cannot be told
    /// twice.</b> A death starts a respawn, a capture moves the score, an intel
    /// pickup attaches the briefcase — none of them can be taken back on a stock
    /// client. A tick simulated with a guess in it may be re-simulated into a
    /// tick where that death never happened, so its events are provisional;
    /// these are not.
    /// </para>
    /// <para>
    /// ⚠️ <b>Positions are the opposite case and must NOT wait for this.</b> The
    /// client is built to be corrected several times a second and holding
    /// movement until it was certain would hand back the whole latency saving
    /// the design exists for. Send movement provisionally; hold what cannot be
    /// unsaid. `docs/MESH.md`.
    /// </para>
    /// </remarks>
    public bool TryTakeConfirmed(out int tick, out TickEvents events)
    {
        if (_released >= Confirmed)
        {
            tick = -1;
            events = default;
            return false;
        }

        // Older than the window: the outcome has been overwritten by a later
        // tick, so a caller that stopped draining has lost events rather than
        // delayed them. Skipping to what is still held is the honest recovery,
        // and TooLate is where it is visible.
        if (_released <= Tick - _snapshots.Length)
        {
            _tooLate++;
            _released = Tick - _snapshots.Length + 1;
        }

        tick = _released;
        events = _outcomes[_released % _snapshots.Length].Events;
        _released++;

        return true;
    }

    /// <summary>
    /// What the most recent tick reported, whether or not it is final.
    /// </summary>
    /// <remarks>
    /// <b>For movement and for a node's own diagnostics, never for an
    /// irreversible event</b> — see <see cref="TryTakeConfirmed"/>. It is
    /// exposed so a caller can send a provisional world without also being
    /// handed a provisional death by accident.
    /// </remarks>
    public ref readonly TickEvents LastEvents =>
        ref _outcomes[(Tick - 1 + _snapshots.Length) % _snapshots.Length].Events;

    public MeshStats Stats => new(_ticks, _rollbacks, _resimulated, _deepest, _tooLate);

    /// <summary>
    /// Records what a player really pressed on a tick, whoever they belong to.
    /// </summary>
    /// <remarks>
    /// <b>The only way a node learns anything.</b> A local player's input
    /// arrives before its tick runs and is simply used; a peer's arrives after
    /// the tick has been simulated on a guess, and if the guess was wrong this
    /// marks the tick to rewind to. Submitting the same input twice is harmless,
    /// which matters because a link that retransmits is cheaper than one that
    /// tracks what it has already sent.
    /// </remarks>
    public void Submit(int tick, int slot, in PlayerInput input)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tick);
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(slot, WorldState.MaxSlots);

        // Outside the window in either direction. Older means the state it would
        // rewind to is gone; newer means there is nowhere to put it without
        // overwriting history a rewind still needs. Counted rather than
        // swallowed — a node seeing these has a link worse than its
        // configuration says, and the answer is a resync from a peer, not a
        // quiet divergence.
        if (tick < Tick - _options.MaxRollbackTicks || tick >= Tick + _options.MaxRollbackTicks)
        {
            _tooLate++;
            return;
        }

        var row = Row(tick);
        var index = (row * WorldState.MaxSlots) + slot;

        var wasKnown = _known[index];
        var used = _inputs[index];

        _inputs[index] = input;
        _known[index] = true;

        Remember(tick, slot, in input);

        // Not simulated yet, so there is nothing to undo.
        if (tick >= Tick)
            return;

        // A guess that turns out to have been right costs nothing, which is the
        // common case and the reason prediction is worth doing at all.
        if (wasKnown || used.Equals(input))
            return;

        if (!_options.Rollback)
            return;

        _rewindTo = _rewindTo < 0 ? tick : Math.Min(_rewindTo, tick);
    }

    /// <summary>
    /// Schedules a roster change for a tick that has not been simulated yet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⛔ <b>The tick must be in the FUTURE, far enough ahead that the peer
    /// hears about it first.</b> A command scheduled for a tick this node has
    /// already run would have to rewind to apply — and the peer, which never saw
    /// it, would not. Two nodes disagreeing about whether a player exists is not
    /// a divergence that converges; it is two different matches.
    /// </para>
    /// <para>
    /// A command for a past tick is refused and counted in
    /// <see cref="MeshStats.TooLate"/>, which is the honest answer: the schedule
    /// is what both nodes agree on, and one that arrived too late to be agreed
    /// on cannot be quietly applied by one of them.
    /// </para>
    /// </remarks>
    public bool Schedule(int tick, in MeshCommand command)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tick);

        if (tick < Tick || tick >= Tick + _options.MaxRollbackTicks)
        {
            _tooLate++;
            return false;
        }

        var list = ScheduleFor(tick);

        // Sorted on insertion, so the list IS the order both nodes apply in and
        // no node has to remember when it heard anything.
        var at = list.BinarySearch(command);

        list.Insert(at < 0 ? ~at : at, command);

        return true;
    }

    /// <summary>What is scheduled for a tick, in the order it will be applied.</summary>
    public IReadOnlyList<MeshCommand> ScheduledFor(int tick) =>
        tick < 0 || tick >= Tick + _options.MaxRollbackTicks || tick <= Tick - _snapshots.Length
            ? []
            : ScheduleFor(tick);

    private List<MeshCommand> ScheduleFor(int tick)
    {
        var row = tick % _rows;

        if (_scheduledTick[row] != tick)
        {
            _scheduled[row].Clear();
            _scheduledTick[row] = tick;
        }

        return _scheduled[row];
    }

    /// <summary>
    /// Simulates one tick, rewinding first if something arrived that contradicts
    /// a guess.
    /// </summary>
    public void Advance()
    {
        if (_rewindTo >= 0)
            Rewind();

        Step();
        Confirm();
    }

    /// <summary>
    /// Applies anything that arrived since the last tick, without advancing the
    /// clock.
    /// </summary>
    /// <remarks>
    /// <b>Reconciling and advancing are different acts.</b> A node that has just
    /// been told what a peer really pressed wants its world corrected before it
    /// serialises an update for its own clients — that is a rewind and a replay
    /// up to where it already was, and stepping a further tick would mean
    /// guessing again about a tick nobody has any input for. A comparison of two
    /// nodes is only meaningful after this, for the same reason: their last tick
    /// is otherwise a guess each made alone.
    /// </remarks>
    public void Settle()
    {
        if (_rewindTo >= 0)
            Rewind();

        Confirm();
    }

    /// <summary>Simulates <paramref name="ticks"/> ticks.</summary>
    public void Advance(int ticks)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ticks);

        for (var i = 0; i < ticks; i++)
            Advance();
    }

    private void Step()
    {
        var slot = Tick % _snapshots.Length;

        _snapshots[slot].State = _state;

        // ⚠️ AFTER the snapshot and BEFORE the tick. The snapshot is the world a
        // rewind returns to, so it has to be the world as it stood before this
        // tick's commands — otherwise replaying the tick would apply them twice,
        // and a join would arrive as two players.
        Apply(ScheduleFor(Tick));

        var certain = BuildInputs(Tick);

        // The events are captured rather than discarded, because a caller that
        // has to hold a death until its tick is certain needs the death.
        var events = default(TickEvents);
        MatchSimulation.Tick(_world, ref _state, _scratch, ref events, _spawnPoints);

        _outcomes[slot].Events = events;
        _ = certain;

        Tick++;
        _head = Math.Max(_head, Tick);
        _ticks++;

        Confirm();
    }

    /// <summary>
    /// Walks the confirmed mark forward over every tick that ran on facts alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It stops at the first uncertain tick and never steps over one.</b> A
    /// later tick being certain says nothing about an earlier one: a rewind to
    /// the earlier tick re-runs everything after it, so releasing out of order
    /// would publish an event that a rewind then unmade.
    /// </para>
    /// <para>
    /// ⚠️ <b>Certainty is recomputed from the ledger, not recorded when the tick
    /// ran.</b> The common case is a guess that turns out to have been RIGHT:
    /// the input arrives, contradicts nothing, and the tick is never
    /// re-simulated — so a flag written while the tick ran would say "guessed"
    /// for ever and the node would confirm nothing at all. Measured: it
    /// confirmed exactly nothing across a 600-tick match before this was a
    /// recomputation.
    /// </para>
    /// </remarks>
    private void Confirm()
    {
        while (Confirmed < Tick && Confirmed > Tick - _snapshots.Length && IsCertain(Confirmed))
            Confirmed++;
    }

    /// <summary>
    /// Whether every player who was alive at the start of a tick has had their
    /// real input heard for it.
    /// </summary>
    /// <remarks>
    /// The aliveness comes from that tick's own snapshot rather than from the
    /// live state, because who was playing is itself a thing that changes: a
    /// slot that has since died would otherwise stop a long-finished tick from
    /// ever being confirmed.
    /// </remarks>
    private bool IsCertain(int tick)
    {
        var row = Row(tick);
        var origin = row * WorldState.MaxSlots;
        ref readonly var world = ref _snapshots[tick % _snapshots.Length].State;

        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
        {
            if (world.IsAlive(slot) && !_known[origin + slot])
                return false;
        }

        return true;
    }

    /// <summary>
    /// Applies one tick's roster changes, in the order every node computes.
    /// </summary>
    private void Apply(List<MeshCommand> commands)
    {
        // ⚠️ Cleared rather than appended to, because a rewind runs this tick
        // again: the answer that stands has to be the last application's, not
        // the first's plus every replay since.
        var removed = RemovedFor(Tick);
        removed.Clear();

        foreach (var command in commands)
        {
            var slot = command.Slot;

            if (slot >= WorldState.MaxSlots)
                continue;

            switch (command.Kind)
            {
                case MeshCommandKind.AddPlayer:
                {
                    // ⛔ The lowest free slot, not the one asked for: a GG2
                    // roster is dense and positional, so a join takes the next
                    // place in it and both nodes compute the same place from the
                    // same order. Slot 0 is the server's own reserved player and
                    // is never handed out.
                    var free = 1;

                    while (free < WorldState.MaxSlots && _state.HasPlayer[free])
                        free++;

                    if (free >= WorldState.MaxSlots)
                        break;

                    _state.AddPlayer(free);
                    _assigned[Key(command.Origin, command.Sequence)] = free;
                    _slotOrigin[free] = command.Origin;
                    break;
                }

                case MeshCommandKind.SetTeam:
                    _state.Players[slot].Team = (Team)command.A;
                    break;

                case MeshCommandKind.SetClass:
                    _state.Players[slot].Class = (PlayerClass)command.A;
                    break;

                // ⛔ Nothing in MatchSimulation may read this — it is for bot
                // code only, and BotIsNotVisibleToTheSimulationTests is the
                // gate. It is meshed because a server copies its world from
                // here every tick, so a flag written outside the node lasts
                // exactly one tick.
                case MeshCommandKind.SetBot:
                    _state.Players[slot].IsBot = command.A != 0;
                    break;

                case MeshCommandKind.Spawn when _spawnPoints is not null:
                    _state.Spawn(
                        _spawnPoints, slot, _state.Players[slot].Class, _state.Players[slot].Team,
                        command.A, command.B);
                    break;

                // ⛔ The real command, not a field write: a team change drops
                // the intel, kills the character and arms a respawn. Both nodes
                // run all of it, from the same state, at the same tick — so a
                // node that only set the field would be carrying an intel the
                // other had dropped.
                case MeshCommandKind.ChangeTeam:
                    PlayerCommands.ChangeTeam(ref _state, slot, (Team)command.A);
                    break;

                case MeshCommandKind.ChangeClass:
                    PlayerCommands.ChangeClass(ref _state, slot, (PlayerClass)command.A, _world);
                    break;

                case MeshCommandKind.RemovePlayer:
                {
                    // ⛔ The command names the JOIN, not the place. Resolving it
                    // here is what makes two removals in flight at once land on
                    // the people they were meant for: the first renumbers this
                    // table, so the second resolves against a roster that has
                    // already changed rather than against the one its sender
                    // was looking at. Gone already means a second removal of the
                    // same player, which is a no-op rather than somebody else's
                    // departure.
                    if (!_assigned.TryGetValue(Key(command.Origin, command.Sequence), out var at)
                        || !_state.HasPlayer[at])
                    {
                        break;
                    }

                    slot = (byte)at;

                    _state.RemoveSlot(slot);
                    removed.Add(slot);

                    // ⚠️ A REMOVAL RENUMBERS EVERY SLOT ABOVE IT — CLAUDE.md's
                    // rule, and anything that names a player has to be renumbered
                    // with it. These two tables name players, so they are part of
                    // "anything": left alone, the slot a join was assigned and
                    // the node that owns it would both point one place too high
                    // the moment somebody leaves.
                    for (var above = slot; above < WorldState.MaxSlots - 1; above++)
                        _slotOrigin[above] = _slotOrigin[above + 1];

                    _slotOrigin[WorldState.MaxSlots - 1] = 255;

                    foreach (var key in _assigned.Keys.ToArray())
                    {
                        if (_assigned[key] == slot)
                            _assigned.Remove(key);
                        else if (_assigned[key] > slot)
                            _assigned[key]--;
                    }

                    break;
                }

                default:
                    break;
            }
        }
    }

    /// <summary>
    /// The slot a join was given, once it has been applied.
    /// </summary>
    /// <remarks>
    /// <b>Both nodes get the same answer</b>, because both applied the same
    /// commands in the same order. The issuer uses it to bind the slot to a
    /// socket; its peer uses it to know whose name goes with which roster place.
    /// </remarks>
    public bool TryGetAssignedSlot(int origin, int sequence, out int slot) =>
        _assigned.TryGetValue(Key(origin, sequence), out slot);

    /// <summary>
    /// The join that created a slot, which is how a departure names it.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>The reverse of <see cref="TryGetAssignedSlot"/>, and the only safe
    /// way to say "this player leaves".</b> A slot number means what it meant
    /// when it was read, and a removal anywhere below it changes that; a join
    /// key does not move. A linear scan because the table holds one entry per
    /// player in the match.
    /// </remarks>
    public bool TryGetJoinKey(int slot, out int origin, out int sequence)
    {
        foreach (var (key, at) in _assigned)
        {
            if (at != slot)
                continue;

            origin = key >> 16;
            sequence = key & 0xFFFF;

            return true;
        }

        origin = 255;
        sequence = 0;

        return false;
    }

    /// <summary>
    /// The slots a tick's removals took, in the order they were applied.
    /// </summary>
    /// <remarks>
    /// <b>Only a past tick can be asked</b>, because the answer is what applying
    /// it produced. A server drains this to take the same roster places its node
    /// took from the world, which it cannot work out for itself: the command
    /// names a join, and applying it deleted that join's entry.
    /// </remarks>
    public IReadOnlyList<byte> RemovedAt(int tick) =>
        tick < 0 || tick >= Tick || tick <= Tick - _rows ? [] : RemovedFor(tick);

    private List<byte> RemovedFor(int tick)
    {
        var row = tick % _rows;

        if (_removedTick[row] != tick)
        {
            _removed[row].Clear();
            _removedTick[row] = tick;
        }

        return _removed[row];
    }

    /// <summary>
    /// Which node issued the join that holds a slot, or 255 if nobody does.
    /// </summary>
    /// <remarks>
    /// <b>This is what "my player" means in a mesh</b>, and it is not the slot's
    /// parity: a join takes the lowest free place, so either node's player can
    /// land on any number.
    /// </remarks>
    public int OriginOf(int slot) =>
        (uint)slot < WorldState.MaxSlots ? _slotOrigin[slot] : 255;

    private static int Key(int origin, int sequence) => (origin << 16) | (sequence & 0xFFFF);

    /// <summary>
    /// Puts the world back to the start of the contradicted tick and replays
    /// every tick since, now that one of them has a real input where it had a
    /// guess.
    /// </summary>
    private void Rewind()
    {
        var from = _rewindTo;
        _rewindTo = -1;

        var depth = Tick - from;

        if (depth <= 0)
            return;

        if (depth > _snapshots.Length)
        {
            // Unreachable through Submit, which refuses anything older than the
            // window. Counted rather than thrown: a node that cannot rewind
            // should say so and keep serving, and an exception on the tick
            // thread would take the match down for a fault the link caused.
            _tooLate++;
            return;
        }

        _state = _snapshots[from % _snapshots.Length].State;
        Tick = from;

        _rollbacks++;
        _resimulated += depth;
        _deepest = Math.Max(_deepest, depth);

        for (var i = 0; i < depth; i++)
            Step();
    }

    /// <summary>
    /// Fills <see cref="_scratch"/> with one tick's inputs: what is known, and a
    /// guess for everything else.
    /// </summary>
    /// <returns>
    /// Whether every live slot's input for this tick was known rather than
    /// guessed, which is what makes the tick's outcome final.
    /// </returns>
    /// <remarks>
    /// <b>The guess is "they are still holding what they were holding", which is
    /// measured rather than assumed</b> — at the two or three ticks one way
    /// across the Atlantic costs, it is right for 82-87% of a person's ticks and
    /// no cheaper rule beats it. `tools/inputpredict` has the numbers and the
    /// variants that lose.
    /// <para>
    /// ⚠️ A guess is written back into the ledger but NOT marked known, because
    /// a rewind has to be able to tell what it ran with from what it was told.
    /// </para>
    /// </remarks>
    private bool BuildInputs(int tick)
    {
        var row = Row(tick);
        var origin = row * WorldState.MaxSlots;
        var certain = true;

        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
        {
            var index = origin + slot;

            if (!_known[index])
            {
                _inputs[index] = _options.Predict ? _latest[slot] : default;

                // ⚠️ Only a LIVE slot's missing input makes a tick uncertain. An
                // empty slot has nobody to hear from, and counting it would
                // leave every tick of every match provisional for ever.
                if (_state.IsAlive(slot))
                    certain = false;
            }

            _scratch[slot] = _inputs[index];
        }

        return certain;
    }

    /// <summary>
    /// The ledger row for a tick, cleared first if it still holds an older one.
    /// </summary>
    /// <remarks>
    /// <b>Lazy rather than swept on every tick.</b> A row is only ever wrong
    /// when the ring has wrapped past it, so clearing it at the moment it is
    /// first touched for its new tick costs one comparison in the common case
    /// and keeps the tick allocation-free.
    /// </remarks>
    private int Row(int tick)
    {
        var row = tick % _rows;

        if (_rowTick[row] == tick)
            return row;

        Array.Clear(_known, row * WorldState.MaxSlots, WorldState.MaxSlots);
        _rowTick[row] = tick;

        return row;
    }

    /// <summary>
    /// Keeps the newest input heard for a slot, which is what a guess repeats.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Newest by TICK, not by arrival.</b> Two inputs for one slot can
    /// arrive out of order on a link that retransmits, and repeating the older
    /// of them would make the node guess with stale keys for as long as that
    /// slot stayed quiet.
    /// </remarks>
    private void Remember(int tick, int slot, in PlayerInput input)
    {
        if (tick < _latestTick[slot])
            return;

        _latest[slot] = input;
        _latestTick[slot] = tick;
    }
}
