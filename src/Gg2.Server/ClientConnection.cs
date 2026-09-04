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

    // Bytes written but not yet accepted by the kernel. Almost always empty: a
    // client that cannot keep up with a 30 Hz broadcast has bigger problems.
    private byte[] _pending = new byte[4096];
    private int _pendingStart;
    private int _pendingEnd;

    public ClientConnection(Socket socket)
    {
        _socket = socket;
        _socket.Blocking = false;
        _socket.NoDelay = true;
        RemoteAddress = (socket.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None;
    }

    public IPAddress RemoteAddress { get; }

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
    /// Drains whatever the kernel has, appending it to
    /// <paramref name="destination"/>. Returns the byte count.
    /// </summary>
    public int Receive(ReceiveBuffer destination)
    {
        var total = 0;

        while (!IsFaulted && !IsClosed)
        {
            int read;
            try
            {
                read = _socket.Receive(_receiveScratch, SocketFlags.None);
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
    public void Send(ReadOnlySpan<byte> bytes)
    {
        if (IsFaulted || bytes.IsEmpty)
            return;

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
