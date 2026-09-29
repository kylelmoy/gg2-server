using System.Diagnostics;
using System.Net.Sockets;
using GG2.Protocol;

namespace LoadProbe;

/// <summary>
/// One spectator client, measuring when the server's broadcasts actually
/// arrive.
/// </summary>
/// <remarks>
/// <b>Why this is not <c>GG2.Parity.Recorder</c>, which already joins a remote
/// server and timestamps its chunks.</b> That type polls with
/// <c>Thread.Sleep(1)</c>, and a Windows sleep rounds up to the system timer
/// resolution — 1 to 15 ms depending on what else has raised it. The signal
/// here is a 33.3 ms broadcast interval, so a sleep-driven reader would inject
/// jitter the same size as the thing being measured and there would be no way
/// to tell the two apart afterwards. This blocks in <c>Receive</c> instead and
/// stamps the clock the instant it returns, which costs a thread per client and
/// is accurate to the stopwatch.
/// <para>
/// <b>It stays a spectator on purpose.</b> A spectator has no character, so it
/// adds no simulation cost and cannot change the match it is measuring — but it
/// is still a roster entry that every broadcast is addressed to, which is the
/// per-client fan-out cost this is here to load the server with.
/// </para>
/// <para>
/// <b>Nothing is framed.</b> The stream interleaves chat, joins, kills and
/// plugin packets with the update tiers, so framing it means writing a real
/// client's reader. The question here is when bytes arrived, not what they
/// said, and a gap in arrivals is exactly what a player sees as a freeze. The
/// server sets <c>NoDelay</c> on every client socket
/// (<c>ClientConnection.cs:36</c>), so one tick's broadcast is one write and
/// normally one read.
/// </para>
/// </remarks>
public sealed class ProbeClient : IDisposable
{
    private readonly Socket _socket;
    private readonly byte[] _scratch = new byte[1 << 16];
    private readonly object _sendLock = new();

    /// <summary>Stopwatch ticks at which a read returned, one per read.</summary>
    private readonly List<long> _arrivals;

    private readonly List<int> _sizes;

    public ProbeClient(string host, int port, int expectedReads)
    {
        _arrivals = new List<long>(expectedReads);
        _sizes = new List<int>(expectedReads);

        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
            ReceiveTimeout = 20000,
        };

        _socket.Connect(host, port);
    }

    public string ServerName { get; private set; } = string.Empty;

    public string Map { get; private set; } = string.Empty;

    public IReadOnlyList<long> Arrivals => _arrivals;

    public IReadOnlyList<int> Sizes => _sizes;

    public long TotalBytes { get; private set; }

    public string? Fault { get; private set; }

    /// <summary>
    /// Speaks the handshake and joins as a spectator. The sequence is
    /// docs/PROTOCOL.md, "Handshake"; it is the same one
    /// <c>GG2.Parity.Recorder.Join</c> walks.
    /// </summary>
    public void Join(string name)
    {
        Send(w =>
        {
            w.WriteMessageType(MessageType.Hello);
            w.WriteBytes(GameConstants.ProtocolUuidBytes);
        });

        var type = (MessageType)ReadByte();

        if (type == MessageType.IncompatibleProtocol)
            throw new InvalidOperationException("server rejected PROTOCOL_UUID");
        if (type == MessageType.PasswordRequest)
            throw new InvalidOperationException("server wants a password; the probe has none");
        if (type != MessageType.Hello)
            throw new InvalidOperationException($"expected HELLO, got {type}");

        ServerName = ReadString1();
        Map = ReadString1();
        ReadString1();                                  // map MD5
        ReadByte();                                     // pluginsRequired

        // pluginList is a string2, and it is NOT empty on these hosts — they
        // serve the `chat` plugin. Read it and drop it: ServerPluginsRequired is
        // a dialog on the stock client and is enforced by neither side
        // (docs/PROTOCOL.md), so a client that simply never sends a
        // PLUGIN_PACKET is served normally.
        var pluginListLength = (ushort)(ReadByte() | (ReadByte() << 8));
        for (var i = 0; i < pluginListLength; i++)
            ReadByte();

        Send(w =>
        {
            w.WriteMessageType(MessageType.ReserveSlot);
            w.WriteString1(name);
        });

        var reply = (MessageType)ReadByte();

        if (reply == MessageType.ServerFull)
            throw new InvalidOperationException("server is full");
        if (reply != MessageType.ReserveSlot)
            throw new InvalidOperationException($"expected RESERVE_SLOT, got {reply}");

        Send(w => w.WriteMessageType(MessageType.PlayerJoin));
    }

    /// <summary>
    /// Reads until <paramref name="stop"/>, stamping the clock as each read
    /// returns. Everything read is discarded after its size is noted.
    /// </summary>
    public void Record(CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                int read;

                try
                {
                    read = _socket.Receive(_scratch, SocketFlags.None);
                }
                catch (SocketException e) when (e.SocketErrorCode == SocketError.TimedOut)
                {
                    // 20 s with nothing at all. Recorded as a fault rather than
                    // thrown: a silent server is a RESULT here, and losing the
                    // arrivals collected before it would hide how long it had
                    // been healthy.
                    Fault = "receive timed out (20s with no data)";
                    return;
                }

                var at = Stopwatch.GetTimestamp();

                if (read == 0)
                {
                    Fault = "server closed the connection";
                    return;
                }

                _arrivals.Add(at);
                _sizes.Add(read);
                TotalBytes += read;
            }
        }
        catch (Exception e) when (e is SocketException or IOException or ObjectDisposedException)
        {
            // ⚠️ Only a fault if it was NOT the run ending. The driver wakes
            // these threads by disposing the socket out from under a blocking
            // Receive, so every client reports "connection aborted" at teardown.
            // Counting those would put a fault on every clean run and leave
            // nothing to distinguish a run where the server really did drop
            // clients — which is the one result this field exists to carry.
            if (!stop.IsCancellationRequested)
                Fault = e.Message;
        }
    }

    /// <summary>
    /// Sends one <c>INPUTSTATE</c>, as a playing client does every tick.
    /// </summary>
    /// <remarks>
    /// <b>A spectator may send this and it is safe.</b>
    /// <c>GameServer.Clients.cs:406</c> stores it straight into the connection's
    /// <c>NetworkInputSource.Latest</c> without requiring a character, so a
    /// probe with no character loads the RECEIVE path — a recv and a parse per
    /// client per tick, and the per-tick command accounting — without adding a
    /// character to the simulation or taking a bot's place in the population.
    /// That keeps the two costs separable, which is the whole point of sweeping
    /// spectators.
    /// <para>
    /// The keys change every call so the bytes are not identical each tick.
    /// Nothing downstream depends on them: with no character there is nothing
    /// for the input to drive.
    /// </para>
    /// </remarks>
    public void SendInput(int tick)
    {
        var keys = (PlayerKeys)(1 << (tick % 4));
        var input = new PlayerInput(keys, (ushort)(tick * 977 % 65536), (byte)(tick % 128));

        Send(w =>
        {
            w.WriteMessageType(MessageType.InputState);
            input.Write(w);
        });
    }

    private void Send(Action<NetWriter> write)
    {
        var writer = new NetWriter(64);
        write(writer);

        var bytes = writer.WrittenSpan;
        var sent = 0;

        // ⚠️ Under the lock, because the input sender runs on its own thread
        // while the reader is blocked in Receive. Concurrent send and receive on
        // one socket is fine; two concurrent SENDS would interleave their bytes
        // and desynchronise the server's command reader.
        lock (_sendLock)
        {
            while (sent < bytes.Length)
                sent += _socket.Send(bytes[sent..], SocketFlags.None);
        }
    }

    private byte ReadByte()
    {
        var one = new byte[1];
        var got = _socket.Receive(one, SocketFlags.None);

        if (got == 0)
            throw new IOException("server closed the connection during the handshake");

        return one[0];
    }

    private string ReadString1()
    {
        var length = ReadByte();
        var bytes = new byte[length];

        for (var i = 0; i < length; i++)
            bytes[i] = ReadByte();

        return GG2Text.Decode(bytes);
    }

    public void Dispose() => _socket.Dispose();
}
