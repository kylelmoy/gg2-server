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

    /// <summary>One tick's world, on the heap so the window can be an array.</summary>
    private sealed class Snapshot
    {
        public WorldState State;
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

        for (var i = 0; i < _snapshots.Length; i++)
            _snapshots[i] = new Snapshot();

        _rows = _options.MaxRollbackTicks * 2;
        _inputs = new PlayerInput[_rows * WorldState.MaxSlots];
        _known = new bool[_rows * WorldState.MaxSlots];
        _rowTick = new int[_rows];
        _latest = new PlayerInput[WorldState.MaxSlots];
        _latestTick = new int[WorldState.MaxSlots];

        Array.Fill(_rowTick, -1);
        Array.Fill(_latestTick, -1);
    }

    /// <summary>The tick this node is about to simulate.</summary>
    public int Tick { get; private set; }

    /// <summary>The world as of the start of <see cref="Tick"/>.</summary>
    public ref readonly WorldState State => ref _state;

    /// <summary>A 64-bit summary, for asking a peer whether it agrees.</summary>
    public ulong Digest => StateDigest.Of(in _state);

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
    /// Simulates one tick, rewinding first if something arrived that contradicts
    /// a guess.
    /// </summary>
    public void Advance()
    {
        if (_rewindTo >= 0)
            Rewind();

        Step();
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
        _snapshots[Tick % _snapshots.Length].State = _state;

        BuildInputs(Tick);
        MatchSimulation.Tick(_world, ref _state, _scratch, _spawnPoints);

        Tick++;
        _head = Math.Max(_head, Tick);
        _ticks++;
    }

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
    private void BuildInputs(int tick)
    {
        var row = Row(tick);
        var origin = row * WorldState.MaxSlots;

        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
        {
            var index = origin + slot;

            if (!_known[index])
                _inputs[index] = _options.Predict ? _latest[slot] : default;

            _scratch[slot] = _inputs[index];
        }
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
