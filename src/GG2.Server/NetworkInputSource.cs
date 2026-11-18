using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Server;

/// <summary>
/// The input source behind a socket: whatever <c>INPUTSTATE</c> arrived most
/// recently.
/// </summary>
/// <remarks>
/// This is what makes docs/ARCHITECTURE.md's claim — "a network client and a
/// bot are both an <see cref="IInputSource"/>" — literally true rather than
/// aspirational, and it is why the server has one input-gathering path instead
/// of a branch on <c>isBot</c>.
/// <para>
/// <b>It ignores the world and the tick, and holding the last input is the
/// point.</b> <c>processClientCommands.gml:267</c> writes <c>keyState</c> when
/// the packet arrives and nothing clears it, so a player whose packet was lost
/// or delayed keeps pressing what they last pressed — the client dead-reckons
/// on the same assumption. <see cref="MatchSimulation.Tick"/> says the same
/// thing from the other side: a slot past the end of the input span keeps the
/// input it had. Zeroing here would make every dropped packet a stutter.
/// </para>
/// <para>
/// Slot 0 — the reference's local player, which a headless server never
/// simulates — gets one of these with nothing ever written to it, so it presses
/// nothing for ever.
/// </para>
/// </remarks>
public sealed class NetworkInputSource : IInputSource
{
    private PlayerInput _latest;
    private PlayerKeys _pressed;
    private PlayerKeys _released;

    /// <summary>
    /// The most recent input this player sent.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Assigning it is how an <c>INPUTSTATE</c> is delivered, and the
    /// setter does more than store it</b> — it also accumulates the key edges
    /// this input creates against the one before it. Read
    /// <see cref="TakeEdges"/> before believing that only the last input of a
    /// tick matters.
    /// </remarks>
    public PlayerInput Latest
    {
        get => _latest;
        set
        {
            // `event_user(1)` per INPUTSTATE, exactly as
            // processClientCommands.gml:315 runs it inside the `with(player.object)`
            // — `pressedKeys |= keyState & ~lastKeyState; releasedKeys |=
            // ~keyState & lastKeyState; lastKeyState = keyState`. Here the
            // "last" is the previous input rather than a character field,
            // because this runs whether or not the player has a character.
            _pressed |= value.Keys & ~_latest.Keys;
            _released |= ~value.Keys & _latest.Keys;
            _latest = value;
        }
    }

    public PlayerInput Poll(in WorldView world, int slot, int tick) => _latest;

    // --- Queued mode: a client that asked for prediction ---------------------
    //
    // docs/PREDICTION.md, "The input queue". Without a welcome none of this
    // runs and the source coalesces exactly as above.

    /// <summary>
    /// Room for every input one tick can read (<c>ClientCommands.PerTickLimit</c>)
    /// on top of a full queue, with slack. A freeze reads inputs without
    /// consuming them, so <see cref="Enqueue"/> still merges when this fills;
    /// the memory a client can make the server hold is this, whatever it sends.
    /// </summary>
    internal const int QueueCapacity = 16;

    private readonly QueuedInput[] _queue = new QueuedInput[QueueCapacity];
    private int _head;
    private int _count;
    private int _standingTicks;

    /// <summary>The last input enqueued, which the next one's edges are taken against.</summary>
    private PlayerInput _lastQueued;

    /// <summary>
    /// Whether <c>INPUTSTATE</c>s go through the queue. Set once, by
    /// <see cref="StartQueueing"/>, and never cleared for the connection.
    /// </summary>
    public bool Queueing { get; private set; }

    /// <summary>Inputs consumed into ticks so far — what <c>PREDICT_ACK</c> carries.</summary>
    public uint InputsApplied { get; private set; }

    /// <summary>Inputs waiting for a tick of their own.</summary>
    public int QueuedCount => _count;

    /// <summary>
    /// Switches this connection to one input per tick.
    /// </summary>
    /// <param name="inputsRead">
    /// Every <c>INPUTSTATE</c> read so far. All of them have reached
    /// <see cref="Latest"/>, and this tick's gather applies them, so all of
    /// them count as applied from here.
    /// </param>
    public void StartQueueing(uint inputsRead)
    {
        if (Queueing)
            return;

        Queueing = true;
        InputsApplied = inputsRead;
        _lastQueued = _latest;
    }

    /// <summary>
    /// Delivers one <c>INPUTSTATE</c> to a queueing connection.
    /// </summary>
    /// <remarks>
    /// The edges are taken here, against the input before it, which is the
    /// input consumed before it — so they are exactly what
    /// <see cref="GG2.Simulation.CharacterState.ApplyInput"/> takes, one tick
    /// at a time.
    /// </remarks>
    public void Enqueue(PlayerInput input)
    {
        var entry = new QueuedInput(
            input,
            input.Keys & ~_lastQueued.Keys,
            ~input.Keys & _lastQueued.Keys,
            1);
        _lastQueued = input;

        if (_count == QueueCapacity)
            MergeOldest();

        _queue[(_head + _count) % QueueCapacity] = entry;
        _count++;
    }

    /// <summary>
    /// Takes this tick's input off the queue: one input, or more than one
    /// merged when the queue is too deep or has stood too long.
    /// </summary>
    /// <param name="maxQueued">Inputs that may wait after the consume (<c>PredictionMaxQueuedInputs</c>).</param>
    /// <param name="drainTicks">Ticks an input may stand waiting before two merge (<c>PredictionQueueDrainTicks</c>).</param>
    /// <remarks>
    /// An empty queue holds <see cref="Latest"/>, as an unqueued source does.
    /// The consumed input's edges are ORed into the accumulator
    /// <see cref="TakeEdges"/> drains, so the tick's caller is unchanged.
    /// Called only on a tick that advances, for every slot alive or not.
    /// </remarks>
    public void ConsumeTick(int maxQueued, int drainTicks)
    {
        if (_count == 0)
        {
            _standingTicks = 0;
            return;
        }

        // Deeper than the cap after this consume: fold the oldest into it. Both
        // are counted, so the ack moves past every input folded in.
        while (_count - 1 > Math.Max(0, maxQueued))
            MergeOldest();

        // A standing queue gives back one tick of latency at a time.
        if (_count >= 2 && _standingTicks >= Math.Max(1, drainTicks))
        {
            MergeOldest();
            _standingTicks = 0;
        }

        var taken = _queue[_head];
        _head = (_head + 1) % QueueCapacity;
        _count--;

        _latest = taken.Input;
        _pressed |= taken.Pressed;
        _released |= taken.Released;
        InputsApplied += taken.Count;

        _standingTicks = _count > 0 ? _standingTicks + 1 : 0;
    }

    /// <summary>
    /// Folds the oldest queued input into the one after it: the later keys and
    /// aim, both inputs' edges, both counted — so a tap is never lost.
    /// </summary>
    private void MergeOldest()
    {
        var first = _queue[_head];
        _head = (_head + 1) % QueueCapacity;
        _count--;

        ref var second = ref _queue[_head];
        second = new QueuedInput(
            second.Input,
            first.Pressed | second.Pressed,
            first.Released | second.Released,
            first.Count + second.Count);
    }

    private readonly record struct QueuedInput(
        PlayerInput Input, PlayerKeys Pressed, PlayerKeys Released, uint Count);

    /// <summary>
    /// The key edges every <c>INPUTSTATE</c> since the last call created,
    /// clearing them.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>This is the whole reason a socket's input source is not just a
    /// "last value wins" box, and skipping it silently drops input.</b> More than
    /// one <c>INPUTSTATE</c> reaches the server in one tick whenever a client's
    /// packets coalesce — a delayed one arriving beside the next, which is
    /// ordinary over the internet — and up to ten are read per player per tick
    /// (<c>ClientCommands.PerTickLimit</c>). A key pressed and released across
    /// two of them is not in the last one's <c>keyState</c> at all, so a server
    /// that only looked at <see cref="Latest"/> would never see the press: a
    /// spy's tapped stab, a tapped jump and a tapped airblast simply would not
    /// happen. The reference does see them, because its <c>keyState</c> write
    /// runs <c>event_user(1)</c> per input rather than per tick.
    /// <para>
    /// The pressed mask is a superset of what
    /// <see cref="GG2.Simulation.CharacterState.ApplyInput"/> derives from
    /// <see cref="Latest"/> alone, so the caller ORs both and nothing is counted
    /// twice.
    /// </para>
    /// <para>
    /// docs/TRAPS-SIMULATION.md, "Upstream bug fixes carried into the port".
    /// </para>
    /// </remarks>
    public (PlayerKeys Pressed, PlayerKeys Released) TakeEdges()
    {
        var edges = (_pressed, _released);
        _pressed = PlayerKeys.None;
        _released = PlayerKeys.None;
        return edges;
    }
}
