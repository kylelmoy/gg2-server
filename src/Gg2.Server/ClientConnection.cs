using System.Net;
using System.Net.Sockets;
using Gg2.Protocol;

namespace Gg2.Server;

/// <summary>
/// One client socket, with non-blocking receive and a queued send.
/// </summary>
/// <remarks>
/// Non-blocking throughout, so the tick thread never waits on a socket. That is
/// the same shape the reference has (GM8's <c>tcp_receive</c> is "are n bytes
/// buffered yet"), and it keeps phase 1 free of concurrency entirely. When the
/// send path needs to move onto its own threads, <see cref="Send"/> and
/// <see cref="Flush"/> are the seam — nothing above this type touches a socket.
/// </remarks>
public sealed class ClientConnection : IDisposable
{
    private readonly Socket _socket;
    private readonly byte[] _receiveScratch = new byte[8192];
    private readonly ConnectionLimits _limits;

    // Bytes written but not yet accepted by the kernel. Almost always empty: a
    // client that cannot keep up with a 30 Hz broadcast has bigger problems —
    // but "almost always" is not "always", and a client that simply stops
    // reading grows this on purpose. _limits.MaxSendBacklogBytes is the ceiling.
    private byte[] _pending = new byte[4096];
    private int _pendingStart;
    private int _pendingEnd;

    public ClientConnection(Socket socket, ConnectionLimits? limits = null)
    {
        _socket = socket;
        _limits = limits ?? ConnectionLimits.Default;
        _socket.Blocking = false;
        _socket.NoDelay = true;
        RemoteAddress = (socket.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None;

        ConfigureKeepAlive();
    }

    public IPAddress RemoteAddress { get; }

    /// <summary>The limits this connection is held to.</summary>
    public ConnectionLimits Limits => _limits;

    /// <summary>
    /// The connection is finished and should be dropped after whatever is
    /// already queued has had its chance to go out.
    /// </summary>
    /// <remarks>
    /// Deliberately <b>not</b> the same as <see cref="IsFaulted"/>. Several of
    /// the reference's refusals — <c>INCOMPATIBLE_PROTOCOL</c>,
    /// <c>PASSWORD_WRONG</c>, <c>SERVER_FULL</c>, <c>KICK</c> — write a byte and
    /// then end the connection, so a <see cref="Close"/> that also stopped
    /// sending would swallow the very message it was closing for and leave the
    /// client staring at an unexplained disconnect.
    /// </remarks>
    public bool IsClosed { get; private set; }

    /// <summary>The socket itself is unusable: the peer went, or an error.</summary>
    public bool IsFaulted { get; private set; }

    /// <summary>Why the connection ended, for the log. Never sent to the client.</summary>
    public string? CloseReason { get; private set; }

    public int PendingSendBytes => _pendingEnd - _pendingStart;

    /// <summary>
    /// Bytes this connection has actually put on the wire.
    /// </summary>
    /// <remarks>
    /// <b>What the socket accepted, not what was queued.</b> A client whose
    /// receive window is full has bytes in <see cref="PendingSendBytes"/> that
    /// have not gone anywhere, and counting those would report bandwidth a
    /// congested link is not carrying — which is exactly the client the operator
    /// is looking at the graph about.
    /// </remarks>
    public long BytesSent { get; private set; }

    /// <summary>
    /// Moves what the kernel has into <paramref name="destination"/>, up to this
    /// tick's allowance. Returns the byte count.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>Two ceilings, and they answer different questions.</b>
    /// <see cref="ConnectionLimits.MaxReceiveBytesPerTick"/> bounds how much of
    /// the tick one socket can spend — this runs on the thread every other
    /// player's physics runs on, and a drain-until-empty loop lets whoever has
    /// the fastest link decide how long the tick takes.
    /// <see cref="ConnectionLimits.MaxReceiveBufferBytes"/> bounds how much the
    /// server will hold for a client that sends faster than the
    /// ten-commands-a-tick budget drains, which is unbounded growth for as long
    /// as it keeps sending.
    /// </para>
    /// <para>
    /// <b>Overflowing the buffer is a disconnect, not a discard.</b> Dropping
    /// bytes out of the middle of a command stream would leave the framer
    /// reading a payload as a command byte for ever after — a silently
    /// desynchronised client is worse than a disconnected one, and it would look
    /// exactly like a port bug.
    /// </para>
    /// </remarks>
    public int Receive(ReceiveBuffer destination)
    {
        ArgumentNullException.ThrowIfNull(destination);

        var total = 0;

        while (!IsFaulted && !IsClosed && total < _limits.MaxReceiveBytesPerTick)
        {
            var room = _limits.MaxReceiveBufferBytes - destination.Count;

            if (room <= 0)
            {
                Fault($"receive buffer full at {_limits.MaxReceiveBufferBytes} bytes");
                break;
            }

            // Never ask for more than there is room to keep, so a read cannot
            // overshoot the buffer's ceiling and have to be thrown away.
            var want = Math.Min(
                _receiveScratch.Length,
                Math.Min(room, _limits.MaxReceiveBytesPerTick - total));

            int read;
            try
            {
                read = _socket.Receive(_receiveScratch, 0, want, SocketFlags.None);
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.WouldBlock)
            {
                break;
            }
            catch (SocketException e)
            {
                Fault(e.SocketErrorCode.ToString());
                break;
            }
            catch (ObjectDisposedException)
            {
                Fault("socket disposed");
                break;
            }

            if (read == 0)
            {
                // An orderly shutdown from the peer.
                Fault("peer closed the connection");
                break;
            }

            destination.Append(_receiveScratch.AsSpan(0, read));
            total += read;
        }

        return total;
    }

    /// <summary>Queues bytes for this client. Nothing reaches the socket until <see cref="Flush"/>.</summary>
    /// <remarks>
    /// ⚠️ <b>A queue that has outgrown
    /// <see cref="ConnectionLimits.MaxSendBacklogBytes"/> faults the
    /// connection.</b> That backlog has exactly one cause — the client is not
    /// reading — and it is the cheapest denial of service against this server
    /// there is: connect, join, call <c>recv</c> never, and every tick's
    /// broadcast accumulates here until the process is killed. No malformed byte
    /// is involved and the client is legal as far as TCP is concerned, which is
    /// why nothing else on the path notices it.
    /// <para>
    /// The bytes that overflowed are not queued, deliberately: this connection is
    /// over, so writing them would buy one more allocation for a socket about to
    /// be disposed.
    /// </para>
    /// </remarks>
    public void Send(ReadOnlySpan<byte> bytes)
    {
        if (IsFaulted || bytes.IsEmpty)
            return;

        if (PendingSendBytes + bytes.Length > _limits.MaxSendBacklogBytes)
        {
            Fault($"send backlog over {_limits.MaxSendBacklogBytes} bytes; the client is not reading");
            return;
        }

        EnsureRoom(bytes.Length);
        bytes.CopyTo(_pending.AsSpan(_pendingEnd));
        _pendingEnd += bytes.Length;
    }

    /// <summary>Pushes as much of the queue as the kernel will take.</summary>
    public void Flush()
    {
        while (!IsFaulted && PendingSendBytes > 0)
        {
            int sent;
            try
            {
                sent = _socket.Send(_pending, _pendingStart, PendingSendBytes, SocketFlags.None);
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.WouldBlock)
            {
                return;
            }
            catch (SocketException e)
            {
                Fault(e.SocketErrorCode.ToString());
                return;
            }
            catch (ObjectDisposedException)
            {
                Fault("socket disposed");
                return;
            }

            BytesSent += sent;
            _pendingStart += sent;
            if (_pendingStart == _pendingEnd)
                _pendingStart = _pendingEnd = 0;
        }
    }

    /// <summary>
    /// Marks the connection finished. The socket is not torn down until
    /// <see cref="Dispose"/>, so anything already queued still has a chance to
    /// go out.
    /// </summary>
    public void Close(string reason)
    {
        if (IsClosed)
            return;

        IsClosed = true;
        CloseReason = reason;
    }

    /// <summary>The socket died. Nothing more can be read from or written to it.</summary>
    private void Fault(string reason)
    {
        IsFaulted = true;
        Close(reason);
    }

    public void Dispose()
    {
        IsFaulted = true;
        IsClosed = true;
        try
        {
            _socket.Shutdown(SocketShutdown.Both);
        }
        catch (SocketException)
        {
            // Already gone. Nothing to do and nothing worth reporting.
        }
        catch (ObjectDisposedException)
        {
            // Same.
        }

        _socket.Dispose();
    }

    /// <summary>
    /// Asks TCP to notice a peer that went away without saying so.
    /// </summary>
    /// <remarks>
    /// <b>Best effort, and silence on failure is correct.</b> The three interval
    /// options are not available on every platform this can run on, and a server
    /// that refused a client because it could not tune a keepalive timer would
    /// be trading a real outage for a hypothetical one.
    /// <see cref="SocketOptionName.KeepAlive"/> itself is universal; the tuning
    /// narrows detection from the system default — two hours on most stacks — to
    /// about a minute.
    /// </remarks>
    private void ConfigureKeepAlive()
    {
        if (!_limits.TcpKeepAlive)
            return;

        try
        {
            _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            return;
        }

        Tune(SocketOptionName.TcpKeepAliveTime, _limits.KeepAliveIdleSeconds);
        Tune(SocketOptionName.TcpKeepAliveInterval, _limits.KeepAliveIntervalSeconds);
        Tune(SocketOptionName.TcpKeepAliveRetryCount, _limits.KeepAliveProbeCount);

        void Tune(SocketOptionName option, int value)
        {
            try
            {
                _socket.SetSocketOption(SocketOptionLevel.Tcp, option, value);
            }
            catch (Exception e)
                when (e is SocketException or ObjectDisposedException or PlatformNotSupportedException)
            {
                // The platform keeps its own default. Still better than nothing:
                // KeepAlive itself is on.
            }
        }
    }

    private void EnsureRoom(int count)
    {
        if (_pendingEnd + count <= _pending.Length)
            return;

        if (_pendingStart > 0)
        {
            _pending.AsSpan(_pendingStart, PendingSendBytes).CopyTo(_pending);
            _pendingEnd -= _pendingStart;
            _pendingStart = 0;
        }

        if (_pendingEnd + count <= _pending.Length)
            return;

        Array.Resize(ref _pending, Math.Max(_pendingEnd + count, _pending.Length * 2));
    }
}
