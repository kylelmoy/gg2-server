using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Gg2.Mesh;
using Gg2.Protocol;

namespace Gg2.Server;

/// <summary>
/// The wire between two mesh nodes: their players' inputs, and a digest of what
/// each of them thinks the world looks like.
/// </summary>
/// <remarks>
/// <para>
/// <b>Serviced on the tick thread and never blocking</b>, the same shape as
/// <see cref="ControlPort"/>: poll what has arrived, hand it to the node, write
/// what this node owes, return. No threads and no locks means no concurrent
/// access to a <c>WorldState</c>, which is the rule that keeps the simulation a
/// pure function.
/// </para>
/// <para>
/// ⚠️ <b>What crosses this link is INPUTS, never state.</b> Twelve players at
/// 30 Hz is about 1 KB/s, against megabytes for the world — and more
/// importantly, a node that accepted a peer's state would have to trust it. Two
/// nodes fed the same inputs reach the same world on their own, which is what
/// the whole design rests on.
/// </para>
/// <para>
/// ⚠️ <b>A digest frame is a check, not a correction.</b> It says the two nodes
/// have diverged; it cannot say which one is right, and nothing here tries to
/// repair one. A divergence means a bug — or two different operating systems,
/// `docs/SIMULATION.md` — and the honest response is to say so loudly.
/// </para>
/// </remarks>
public sealed class MeshLink : IDisposable
{
    private const byte InputsFrame = 1;
    private const byte DigestFrame = 2;
    private const byte HelloFrame = 3;

    /// <summary>
    /// A roster change, scheduled for a tick.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>A command must cross the link or it is not a mesh change at all.</b>
    /// A join applied on one node and never told to the other leaves two nodes
    /// running different matches, and unlike a mispredicted key that does not
    /// converge. The name rides with it because a peer has to tell its OWN
    /// clients who joined, and a slot number is not a person.
    /// </remarks>
    private const byte CommandFrame = 4;

    /// <summary>A player name is twenty arbitrary bytes on the GG2 wire.</summary>
    private const int MaxName = 20;

    /// <summary>
    /// Inputs and digests only, so a frame is small; the cap is a sanity bound
    /// on a corrupt length rather than a protocol limit.
    /// </summary>
    private const int MaxFrame = 4096;

    /// <summary>
    /// A whole match, sent once when a peer attaches.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>The only frame a node cannot start without, and the only big one.</b>
    /// Without it a peer joining mid-match runs a different roster from the one
    /// it is exchanging inputs about.
    /// </remarks>
    private const byte StateFrame = 5;

    /// <summary>
    /// The bound on a snapshot, which is a world plus one line per player.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>This is the one frame whose length a PEER chooses</b>, so the bound
    /// is what stops a corrupt or hostile length making this node hold an
    /// arbitrary amount — the same rule as every other length on a socket
    /// (docs/HARDENING.md). A world is about 157 KB and the members add a few
    /// hundred bytes, so a megabyte is room to grow and nowhere near room to
    /// hurt.
    /// </remarks>
    private const int MaxStateFrame = 1 << 20;

    /// <summary>
    /// A chat bubble, forwarded so the other region can see it.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Forwarded, never scheduled, and the difference matters.</b> A
    /// bubble never reaches <c>WorldState</c> — it is rewritten and relayed —
    /// so two nodes showing it at slightly different moments is not a
    /// divergence and there is nothing to agree about. What it needs is to
    /// arrive at all, which without this it does not: a player in one region
    /// could not see a bubble from the other.
    /// </remarks>
    private const byte ChatFrame = 6;

    /// <summary>
    /// A player's name changed, forwarded so both regions call them the same
    /// thing.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Forwarded like a bubble, not scheduled like a join.</b> A name
    /// never reaches <c>WorldState</c>, so nothing diverges if the two nodes
    /// apply it a tick apart — but a node that never hears it shows a stale
    /// name for ever. Measured on a live two-host run: a bot re-classed by
    /// <c>checkClasslimits</c> is renamed to match, and the peer went on calling
    /// it by the name it had at the moment it joined.
    /// </remarks>
    private const byte NameFrame = 7;

    private readonly TcpListener? _listener;
    private readonly List<byte> _pending = [];
    private readonly ArrayBufferWriter<byte> _out = new();

    /// <summary>
    /// Frames written but not yet sent, one entry per tick, when
    /// <see cref="_delayTicks"/> is set.
    /// </summary>
    private readonly Queue<byte[]> _delayed = new();

    private TcpClient? _tcp;
    private NetworkStream? _stream;

    /// <summary>
    /// How many ticks to hold an outgoing frame for, from
    /// <c>GG2_MESH_DELAY_TICKS</c>. Zero, and absent, mean send at once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⛔ <b>A DIAGNOSTIC, and deliberately not a server setting.</b> Nobody
    /// operating a mesh wants their link made slower. This exists so a soak with
    /// both nodes on one machine can represent the link the design is FOR:
    /// loopback with Nagle off delivers a frame inside the same tick, and the
    /// mesh exists to span an ocean — <see cref="MeshOptions.MaxRollbackTicks"/>
    /// puts one way across the Atlantic at two or three ticks.
    /// </para>
    /// <para>
    /// ⚠️ <b>Ticks rather than milliseconds</b>, so a test can assert it without
    /// sleeping and so it is stated in the unit the rest of this project's
    /// latency figures use. One tick is 33.3ms.
    /// </para>
    /// <para>
    /// Off, nothing is queued and nothing is copied, so the send path is the one
    /// it has always been.
    /// </para>
    /// </remarks>
    private readonly int _delayTicks;

    private MeshLink(TcpListener? listener, TcpClient? tcp, int delayTicks)
    {
        _listener = listener;
        _delayTicks = Math.Max(0, delayTicks);
        Attach(tcp);
    }

    /// <summary>
    /// The delay a soak asks for through <c>GG2_MESH_DELAY_TICKS</c>, or zero.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Read here and passed in, never read inside the link.</b> A type that
    /// consults the environment in its own constructor cannot be tested without
    /// setting a process-wide variable, which leaks into whatever else is
    /// running. Anything unparseable or below one reads as off rather than as an
    /// error — a mistyped diagnostic must not stop a server starting.
    /// </remarks>
    public static int DelayTicksFromEnvironment() =>
        ParseDelayTicks(Environment.GetEnvironmentVariable("GG2_MESH_DELAY_TICKS"));

    /// <summary>
    /// What a <c>GG2_MESH_DELAY_TICKS</c> value means, split out so it can be
    /// tested without setting a variable this whole process would see.
    /// </summary>
    public static int ParseDelayTicks(string? value) =>
        int.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var ticks)
        && ticks > 0
            ? ticks
            : 0;

    /// <summary>Waits for a peer to connect, without blocking a tick.</summary>
    public static MeshLink Listen(IPAddress address, int port, int delayTicks = 0)
    {
        var listener = new TcpListener(address, port);
        listener.Start();

        return new MeshLink(listener, null, delayTicks);
    }

    /// <summary>Connects to a peer that is already listening.</summary>
    public static MeshLink Connect(string host, int port, int delayTicks = 0) =>
        Connect(host, port, TimeSpan.Zero, null, delayTicks);

    /// <summary>
    /// Connects to a peer, waiting up to <paramref name="patience"/> for one to
    /// start listening.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⛔ <b>The ONE place a mesh blocks a thread, and it is before the server
    /// is running.</b> Called from <c>MeshSession.Create</c>, which
    /// <c>GameServer.Start</c> runs before the first tick — so nothing is being
    /// starved while this waits, and nothing is connected to be desynchronised
    /// by it. Retrying from inside the tick loop instead would mean a server
    /// that ticks a match it has no peer for, which is the standalone behaviour
    /// this mode exists to not be.
    /// </para>
    /// <para>
    /// ⚠️ <b>Only a refusal is retried.</b> A refused connection means nothing
    /// is listening THERE YET, which time fixes. An unknown host, a bad address
    /// family or a denied permission are operator errors that time does not
    /// fix, and retrying them for thirty seconds buries the message that says
    /// so.
    /// </para>
    /// </remarks>
    public static MeshLink Connect(
        string host, int port, TimeSpan patience, Action<string>? waiting, int delayTicks = 0)
    {
        // Monotonic, because this is a duration and the wall clock can step.
        var until = Stopwatch.GetTimestamp() + (long)(patience.TotalSeconds * Stopwatch.Frequency);
        var said = false;

        while (true)
        {
            var tcp = new TcpClient();

            try
            {
                tcp.Connect(host, port);

                return new MeshLink(null, tcp, delayTicks);
            }
            catch (SocketException e) when (
                e.SocketErrorCode is SocketError.ConnectionRefused or SocketError.TimedOut
                && Stopwatch.GetTimestamp() < until)
            {
                tcp.Dispose();

                // ⚠️ Once, not once a second. This is a startup path with a
                // human watching, and a line a second for thirty seconds buries
                // whatever the peer says when it does come up.
                if (!said)
                {
                    waiting?.Invoke($"waiting for a mesh peer at {host}:{port}");
                    said = true;
                }

                Thread.Sleep(RetryDelayMs);
            }
            catch
            {
                tcp.Dispose();
                throw;
            }
        }
    }

    /// <summary>How long to wait between connection attempts, in milliseconds.</summary>
    private const int RetryDelayMs = 500;

    /// <summary>
    /// The tick origin the peer announced, as Unix milliseconds, or null until
    /// it has.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Two nodes must agree what tick it is, and a local counter each is
    /// not agreement.</b> Measured before this existed: a node started eight
    /// seconds after its peer refused every one of the 4,032 inputs it received,
    /// because they described ticks 240 ticks beyond anything it could hold. One
    /// end declares an origin, the other adopts it, and both then derive the
    /// tick from the wall clock — so they stay aligned to whatever NTP gives
    /// them rather than to their own drift.
    /// </remarks>
    public long? PeerOrigin { get; private set; }

    /// <summary>The port a listening link ended up on, which matters when asked for 0.</summary>
    public int Port => _listener?.LocalEndpoint is IPEndPoint endpoint ? endpoint.Port : 0;

    public bool Connected => _tcp is { Connected: true };

    /// <summary>Inputs received, frames sent, and disagreements seen.</summary>
    public MeshLinkStats Stats { get; private set; }

    /// <summary>
    /// Queues this node's own players' inputs for one tick. One frame per tick,
    /// however many players.
    /// </summary>
    public void SendInputs(int tick, ReadOnlySpan<int> slots, ReadOnlySpan<PlayerInput> inputs)
    {
        if (slots.Length == 0 || slots.Length != inputs.Length)
            return;

        var body = _out.GetSpan(9 + (slots.Length * 5));
        var at = 0;

        body[at++] = InputsFrame;
        WriteInt(body[at..], tick);
        at += 4;
        body[at++] = (byte)slots.Length;

        for (var i = 0; i < slots.Length; i++)
        {
            body[at++] = (byte)slots[i];
            body[at++] = (byte)inputs[i].Keys;
            body[at++] = (byte)(inputs[i].NetAimDirection & 0xFF);
            body[at++] = (byte)(inputs[i].NetAimDirection >> 8);
            body[at++] = inputs[i].AimDistance;
        }

        _out.Advance(at);
    }

    /// <summary>
    /// Queues a roster change for a tick, with the name when there is one.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The tick has to be far enough ahead that the peer hears it before
    /// reaching it</b>, which is the caller's job — a command that arrives for a
    /// tick already simulated is refused by the node at the other end, and
    /// counted, rather than applied on one machine only.
    /// </remarks>
    public void SendCommand(int tick, in MeshCommand command, ReadOnlySpan<char> name = default)
    {
        var length = Math.Min(name.Length, MaxName);
        var body = _out.GetSpan(11 + length);

        body[0] = CommandFrame;
        WriteInt(body[1..], tick);
        body[5] = (byte)command.Kind;
        body[6] = command.Slot;
        body[7] = command.A;
        body[8] = command.B;

        // ⚠️ Which node issued it, and it is not decoration: a join's ROSTER
        // PLACE is decided by this, so a frame that dropped it would have two
        // nodes handing the same slot to different people.
        body[9] = command.Origin;

        // ⛔ The length goes BEFORE the name. An earlier version put it after and
        // scanned for it, which mis-frames on any name whose own bytes happen to
        // equal their index — a corruption that never shows on loopback and
        // eats the stream's alignment on a real link.
        body[10] = (byte)length;

        // ⚠️ Written as bytes, not re-encoded: a GG2 name is twenty ARBITRARY
        // bytes and may be anything at all. LogText.Safe is what a log line
        // needs; a wire does not get to be fussy about it.
        for (var i = 0; i < length; i++)
            body[11 + i] = (byte)name[i];

        _out.Advance(11 + length);
    }

    /// <summary>
    /// Announces the wall-clock instant this node calls tick zero.
    /// </summary>
    public void SendHello(long originUnixMs)
    {
        var body = _out.GetSpan(9);

        body[0] = HelloFrame;

        for (var i = 0; i < 8; i++)
            body[1 + i] = (byte)(originUnixMs >> (i * 8));

        _out.Advance(9);
    }

    /// <summary>
    /// Queues a chat bubble for the peer to show its own clients.
    /// </summary>
    public void SendChat(int slot, byte image)
    {
        var body = _out.GetSpan(3);

        body[0] = ChatFrame;
        body[1] = (byte)slot;
        body[2] = image;

        _out.Advance(3);
    }

    /// <summary>
    /// Queues a name change for the peer to show its own clients.
    /// </summary>
    public void SendName(int slot, ReadOnlySpan<char> name)
    {
        var length = Math.Min(name.Length, MaxName);
        var body = _out.GetSpan(3 + length);

        body[0] = NameFrame;
        body[1] = (byte)slot;
        body[2] = (byte)length;

        for (var i = 0; i < length; i++)
            body[3 + i] = (byte)name[i];

        _out.Advance(3 + length);
    }

    /// <summary>
    /// Queues a whole match for a peer that has just attached.
    /// </summary>
    public void SendState(ReadOnlySpan<byte> snapshot)
    {
        if (snapshot.Length > MaxStateFrame)
            throw new ArgumentException("snapshot is too large to send", nameof(snapshot));

        var body = _out.GetSpan(5 + snapshot.Length);

        body[0] = StateFrame;
        WriteInt(body[1..], snapshot.Length);
        snapshot.CopyTo(body[5..]);

        _out.Advance(5 + snapshot.Length);
    }

    /// <summary>
    /// Queues what this node believes the world looked like at a tick, so the
    /// peer can notice that it disagrees.
    /// </summary>
    public void SendDigest(int tick, ulong digest)
    {
        var body = _out.GetSpan(13);

        body[0] = DigestFrame;
        WriteInt(body[1..], tick);

        for (var i = 0; i < 8; i++)
            body[5 + i] = (byte)(digest >> (i * 8));

        _out.Advance(13);
    }

    /// <summary>
    /// Accepts a peer if one is waiting, hands everything that has arrived to
    /// the node, and writes whatever has been queued. Never blocks.
    /// </summary>
    /// <param name="node">The node to submit received inputs to.</param>
    /// <param name="onDigest">
    /// Called with (tick, peer digest) for each digest frame. Null ignores them.
    /// </param>
    public void Service(
        MeshNode node,
        Action<int, ulong>? onDigest = null,
        Action<int, MeshCommand, string>? onCommand = null,
        Func<byte[], MeshNode?>? onState = null,
        Action<int, byte>? onChat = null,
        Action<int, string>? onName = null)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (_listener is not null && _tcp is null && _listener.Pending())
            Attach(_listener.AcceptTcpClient());

        if (_tcp is null || _stream is null)
        {
            // ⚠️ Whatever was queued for a peer that is not there is dropped
            // rather than kept. A node waiting to be joined would otherwise
            // accumulate every tick's inputs for as long as it waited and then
            // send the whole history at once — and those ticks are long past
            // the peer's rollback window, so every one of them would be refused
            // as too late. A peer that joins late starts from now.
            _out.Clear();
            _delayed.Clear();
            return;
        }

        Receive(node, onDigest, onCommand, onState, onChat, onName);
        Flush();
    }

    private void Receive(
        MeshNode node,
        Action<int, ulong>? onDigest,
        Action<int, MeshCommand, string>? onCommand,
        Func<byte[], MeshNode?>? onState,
        Action<int, byte>? onChat,
        Action<int, string>? onName)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(4096);

        try
        {
            while (_tcp!.Connected && _tcp.Available > 0)
            {
                var read = _stream!.Read(buffer, 0, Math.Min(buffer.Length, _tcp.Available));

                if (read <= 0)
                    break;

                for (var i = 0; i < read; i++)
                    _pending.Add(buffer[i]);
            }
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
        {
            Drop();
            return;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        Parse(node, onDigest, onCommand, onState, onChat, onName);
    }

    /// <summary>
    /// Pulls whole frames out of what has arrived, leaving a partial one for
    /// next tick.
    /// </summary>
    /// <remarks>
    /// <b>A frame can be split across reads and two frames can share one.</b>
    /// TCP is a stream, so the only safe rule is to consume exactly what a
    /// complete frame occupies and leave the rest — a reader that assumed one
    /// read was one frame would work on loopback and desynchronise over a real
    /// link, which is the worst place to find out.
    /// </remarks>
    private void Parse(
        MeshNode node,
        Action<int, ulong>? onDigest,
        Action<int, MeshCommand, string>? onCommand,
        Func<byte[], MeshNode?>? onState,
        Action<int, byte>? onChat,
        Action<int, string>? onName)
    {
        var at = 0;

        while (at < _pending.Count)
        {
            var kind = _pending[at];

            if (kind == InputsFrame)
            {
                if (_pending.Count - at < 6)
                    break;

                var count = _pending[at + 5];
                var length = 6 + (count * 5);

                if (length > MaxFrame)
                {
                    Drop();
                    return;
                }

                if (_pending.Count - at < length)
                    break;

                var tick = ReadInt(at + 1);

                for (var i = 0; i < count; i++)
                {
                    var origin = at + 6 + (i * 5);
                    var slot = _pending[origin];
                    var keys = (PlayerKeys)_pending[origin + 1];
                    var aim = (ushort)(_pending[origin + 2] | (_pending[origin + 3] << 8));

                    node.Submit(tick, slot, new PlayerInput(keys, aim, _pending[origin + 4]));
                    Stats = Stats with { InputsReceived = Stats.InputsReceived + 1 };
                }

                at += length;
                continue;
            }

            if (kind == DigestFrame)
            {
                if (_pending.Count - at < 13)
                    break;

                var tick = ReadInt(at + 1);
                ulong digest = 0;

                for (var i = 0; i < 8; i++)
                    digest |= (ulong)_pending[at + 5 + i] << (i * 8);

                onDigest?.Invoke(tick, digest);
                at += 13;
                continue;
            }

            if (kind == CommandFrame)
            {
                if (_pending.Count - at < 11)
                    break;

                var nameLength = _pending[at + 10];

                if (nameLength > MaxName)
                {
                    Drop();
                    return;
                }

                if (_pending.Count - at < 11 + nameLength)
                    break;

                var commandTick = ReadInt(at + 1);
                var command = new MeshCommand(
                    (MeshCommandKind)_pending[at + 5],
                    _pending[at + 6],
                    _pending[at + 7],
                    _pending[at + 8],
                    _pending[at + 9]);

                var chars = new char[nameLength];

                for (var i = 0; i < nameLength; i++)
                    chars[i] = (char)_pending[at + 11 + i];

                onCommand?.Invoke(commandTick, command, new string(chars));
                Stats = Stats with { CommandsReceived = Stats.CommandsReceived + 1 };

                at += 11 + nameLength;
                continue;
            }

            if (kind == NameFrame)
            {
                if (_pending.Count - at < 3)
                    break;

                var nameLength = _pending[at + 2];

                if (nameLength > MaxName)
                {
                    Drop();
                    return;
                }

                if (_pending.Count - at < 3 + nameLength)
                    break;

                var chars = new char[nameLength];

                for (var i = 0; i < nameLength; i++)
                    chars[i] = (char)_pending[at + 3 + i];

                onName?.Invoke(_pending[at + 1], new string(chars));
                at += 3 + nameLength;
                continue;
            }

            if (kind == ChatFrame)
            {
                if (_pending.Count - at < 3)
                    break;

                onChat?.Invoke(_pending[at + 1], _pending[at + 2]);
                at += 3;
                continue;
            }

            if (kind == StateFrame)
            {
                if (_pending.Count - at < 5)
                    break;

                var length = ReadInt(at + 1);

                // ⛔ Checked BEFORE it is used for anything. A negative or absurd
                // length from a corrupt or hostile peer would otherwise size an
                // allocation here.
                if (length < 0 || length > MaxStateFrame)
                {
                    Drop();
                    return;
                }

                if (_pending.Count - at < 5 + length)
                    break;

                var snapshot = new byte[length];

                for (var i = 0; i < length; i++)
                    snapshot[i] = _pending[at + 5 + i];

                // ⛔ THE NODE IS REPLACED HERE, mid-batch, and that is the
                // point. A snapshot is followed on the wire by the inputs for
                // the tick it was taken at, in the same read — handed to the
                // node being discarded, those inputs are GONE, and the adopting
                // node guesses a tick nobody will ever correct it about. A lost
                // input is a permanent divergence, not a late one.
                if (onState?.Invoke(snapshot) is { } adopted)
                    node = adopted;

                at += 5 + length;
                continue;
            }

            if (kind == HelloFrame)
            {
                if (_pending.Count - at < 9)
                    break;

                long origin = 0;

                for (var i = 0; i < 8; i++)
                    origin |= (long)_pending[at + 1 + i] << (i * 8);

                PeerOrigin = origin;
                at += 9;
                continue;
            }

            // Not a frame this link knows. There is no resynchronising from
            // here — the stream's alignment is lost — so the link goes down
            // rather than submitting whatever the next bytes happen to spell.
            Drop();
            return;
        }

        _pending.RemoveRange(0, at);
    }

    private void Flush()
    {
        if (_delayTicks > 0)
        {
            FlushDelayed();
            return;
        }

        if (_out.WrittenCount == 0)
            return;

        Send(_out.WrittenSpan);
        _out.Clear();
    }

    /// <summary>
    /// Sends the frame written <see cref="_delayTicks"/> ticks ago, and queues
    /// this tick's.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>An empty tick is queued too.</b> The queue's length is how many
    /// ticks are in flight, so skipping the ticks with nothing to say would make
    /// a quiet moment shorten the delay for whatever came next.
    /// </remarks>
    private void FlushDelayed()
    {
        _delayed.Enqueue(_out.WrittenCount == 0 ? [] : _out.WrittenSpan.ToArray());
        _out.Clear();

        if (_delayed.Count <= _delayTicks)
            return;

        var due = _delayed.Dequeue();

        if (due.Length > 0)
            Send(due);
    }

    private void Send(ReadOnlySpan<byte> frame)
    {
        try
        {
            _stream!.Write(frame);
            Stats = Stats with { FramesSent = Stats.FramesSent + 1 };
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
        {
            Drop();
        }
    }

    /// <summary>Records that the peer disagreed about a tick.</summary>
    public void NoteDisagreement() =>
        Stats = Stats with { Disagreements = Stats.Disagreements + 1 };

    private void Attach(TcpClient? tcp)
    {
        if (tcp is null)
            return;

        // Nagle off: an input frame is small and the next one is a tick away,
        // so there is never a second write to coalesce with and holding it can
        // only add latency to the thing the whole design exists to reduce.
        tcp.NoDelay = true;

        _tcp = tcp;
        _stream = tcp.GetStream();
    }

    private void Drop()
    {
        _stream?.Dispose();
        _tcp?.Dispose();
        _stream = null;
        _tcp = null;
        _pending.Clear();
        _out.Clear();

        // With a delay set, whatever was still in flight belongs to a
        // connection that no longer exists. A peer reconnecting must not be
        // handed the last few ticks of the previous one.
        _delayed.Clear();
    }

    private int ReadInt(int at) =>
        _pending[at] | (_pending[at + 1] << 8) | (_pending[at + 2] << 16) | (_pending[at + 3] << 24);

    private static void WriteInt(Span<byte> into, int value)
    {
        into[0] = (byte)value;
        into[1] = (byte)(value >> 8);
        into[2] = (byte)(value >> 16);
        into[3] = (byte)(value >> 24);
    }

    public void Dispose()
    {
        Drop();
        _listener?.Stop();
    }
}

/// <summary>What a link has carried.</summary>
public readonly record struct MeshLinkStats(
    int InputsReceived, int FramesSent, int Disagreements, int CommandsReceived)
{
    public override string ToString() =>
        $"{InputsReceived} input(s) in, {CommandsReceived} command(s) in, "
        + $"{FramesSent} frame(s) out, {Disagreements} disagreement(s)";
}
