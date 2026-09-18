using System.Buffers;
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

    private readonly TcpListener? _listener;
    private readonly List<byte> _pending = [];
    private readonly ArrayBufferWriter<byte> _out = new();

    private TcpClient? _tcp;
    private NetworkStream? _stream;

    private MeshLink(TcpListener? listener, TcpClient? tcp)
    {
        _listener = listener;
        Attach(tcp);
    }

    /// <summary>Waits for a peer to connect, without blocking a tick.</summary>
    public static MeshLink Listen(IPAddress address, int port)
    {
        var listener = new TcpListener(address, port);
        listener.Start();

        return new MeshLink(listener, null);
    }

    /// <summary>Connects to a peer that is already listening.</summary>
    public static MeshLink Connect(string host, int port)
    {
        var tcp = new TcpClient();
        tcp.Connect(host, port);

        return new MeshLink(null, tcp);
    }

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
        Action<int, MeshCommand, string>? onCommand = null)
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
            return;
        }

        Receive(node, onDigest, onCommand);
        Flush();
    }

    private void Receive(
        MeshNode node, Action<int, ulong>? onDigest, Action<int, MeshCommand, string>? onCommand)
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

        Parse(node, onDigest, onCommand);
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
        MeshNode node, Action<int, ulong>? onDigest, Action<int, MeshCommand, string>? onCommand)
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
        if (_out.WrittenCount == 0)
            return;

        try
        {
            _stream!.Write(_out.WrittenSpan);
            Stats = Stats with { FramesSent = Stats.FramesSent + 1 };
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
        {
            Drop();
        }
        finally
        {
            _out.Clear();
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
