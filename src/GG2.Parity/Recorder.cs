using System.Diagnostics;
using System.Net.Sockets;
using GG2.Protocol;

namespace GG2.Parity;

/// <summary>
/// A passive client that joins a server and writes down every byte it is sent.
/// </summary>
/// <remarks>
/// docs/PARITY.md, tier 0. The key realisation is that the server's own
/// broadcast stream is a complete golden trace: <c>INPUTSTATE</c> carries every
/// player's keys and aim, <c>QUICK_UPDATE</c> carries every player's position,
/// velocity and hp, and both are already quantised to exactly the precision the
/// client can observe. One recording therefore contains both the inputs a
/// simulation consumed and the outputs it produced.
/// <para>
/// It never sends an <c>INPUTSTATE</c> of its own. It joins, sits in spectator
/// and listens — so pointing it at the reference GM8 server and at this port in
/// turn, and diffing the two recordings, is a direct answer to "do these two
/// servers say the same thing".
/// </para>
/// </remarks>
public sealed class Recorder : IDisposable
{
    private readonly Socket _socket;
    private readonly ReceiveBuffer _buffer = new(1 << 16);
    private readonly byte[] _scratch = new byte[1 << 16];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<Chunk> _chunks = [];

    /// <summary>Bytes as they arrived, with the moment they did.</summary>
    /// <remarks>
    /// Arrival grouping is a property of the network, not of the protocol, so it
    /// is recorded but never compared — two servers sending identical bytes may
    /// group them into different reads.
    /// </remarks>
    public readonly record struct Chunk(TimeSpan At, byte[] Bytes);

    public Recorder(string host, int port)
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        _socket.Connect(host, port);
        _socket.Blocking = false;
    }

    public IReadOnlyList<Chunk> Chunks => _chunks;

    /// <summary>Everything received so far, in order, with no framing applied.</summary>
    public byte[] AllBytes()
    {
        var total = _chunks.Sum(c => c.Bytes.Length);
        var all = new byte[total];
        var at = 0;

        foreach (var chunk in _chunks)
        {
            chunk.Bytes.CopyTo(all.AsSpan(at));
            at += chunk.Bytes.Length;
        }

        return all;
    }

    /// <summary>Server name, map and MD5 from the greeting.</summary>
    public string ServerName { get; private set; } = string.Empty;

    public string Map { get; private set; } = string.Empty;

    public string MapMd5 { get; private set; } = string.Empty;

    /// <summary>
    /// Speaks the handshake and joins. Blocks (by polling) until the join
    /// completes or <paramref name="timeout"/> elapses.
    /// </summary>
    /// <remarks>
    /// Recording starts at the <c>JOIN_UPDATE</c>, not at connect: the handshake
    /// is per-client and carries no state, and docs/PARITY.md wants a trace to
    /// begin at the one message that defines a complete starting point.
    /// </remarks>
    public void Join(string name, TimeSpan timeout)
    {
        Send(w =>
        {
            w.WriteMessageType(MessageType.Hello);
            w.WriteBytes(GameConstants.ProtocolUuidBytes);
        });

        // The greeting, which is the only per-client message worth parsing here.
        var greeting = WaitFor(1, timeout);
        var type = (MessageType)greeting[0];

        if (type == MessageType.IncompatibleProtocol)
            throw new InvalidOperationException("server rejected PROTOCOL_UUID");
        if (type == MessageType.PasswordRequest)
            throw new InvalidOperationException("server wants a password; the recorder has none");
        if (type != MessageType.Hello)
            throw new InvalidOperationException($"expected HELLO, got {type}");

        ServerName = ReadString1(timeout);
        Map = ReadString1(timeout);
        MapMd5 = ReadString1(timeout);
        WaitFor(1, timeout);                       // pluginsRequired
        var pluginListLength = ReadUShort(timeout);
        if (pluginListLength > 0)
            WaitFor(pluginListLength, timeout);

        Send(w =>
        {
            w.WriteMessageType(MessageType.ReserveSlot);
            w.WriteString1(name);
        });

        var reply = WaitFor(1, timeout);
        if ((MessageType)reply[0] == MessageType.ServerFull)
            throw new InvalidOperationException("server is full");
        if ((MessageType)reply[0] != MessageType.ReserveSlot)
            throw new InvalidOperationException($"expected RESERVE_SLOT, got {(MessageType)reply[0]}");

        Send(w => w.WriteMessageType(MessageType.PlayerJoin));

        // From here on nothing is parsed. Everything is kept.
        _clock.Restart();
        _chunks.Clear();
    }

    /// <summary>Collects for <paramref name="duration"/>, then returns.</summary>
    public void RecordFor(TimeSpan duration)
    {
        var until = _clock.Elapsed + duration;

        while (_clock.Elapsed < until)
        {
            if (!Drain())
                Thread.Sleep(1);
        }
    }

    /// <summary>
    /// Reads whatever is waiting into <see cref="Chunks"/>. Returns false when
    /// nothing was there.
    /// </summary>
    private bool Drain()
    {
        var got = false;

        while (true)
        {
            int read;
            try
            {
                read = _socket.Receive(_scratch, SocketFlags.None);
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.WouldBlock)
            {
                return got;
            }

            if (read == 0)
                throw new IOException("server closed the connection");

            _chunks.Add(new Chunk(_clock.Elapsed, _scratch.AsSpan(0, read).ToArray()));
            _buffer.Append(_scratch.AsSpan(0, read));
            got = true;
        }
    }

    // --- handshake plumbing --------------------------------------------------

    private void Send(Action<NetWriter> write)
    {
        var writer = new NetWriter(64);
        write(writer);

        var bytes = writer.WrittenSpan;
        var sent = 0;
        while (sent < bytes.Length)
            sent += _socket.Send(bytes[sent..], SocketFlags.None);
    }

    private byte[] WaitFor(int count, TimeSpan timeout)
    {
        var until = _clock.Elapsed + timeout;

        while (true)
        {
            if (_buffer.TryTake(count, out var bytes))
                return bytes.ToArray();

            if (_clock.Elapsed > until)
                throw new TimeoutException($"waited {timeout.TotalSeconds:0.#}s for {count} byte(s)");

            if (!Drain())
                Thread.Sleep(1);
        }
    }

    private string ReadString1(TimeSpan timeout) =>
        GG2Text.Decode(WaitFor(WaitFor(1, timeout)[0], timeout));

    private ushort ReadUShort(TimeSpan timeout)
    {
        var bytes = WaitFor(2, timeout);
        return (ushort)(bytes[0] | (bytes[1] << 8));
    }

    public void Dispose() => _socket.Dispose();
}
