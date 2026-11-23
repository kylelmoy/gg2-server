using System.Net;
using System.Net.Sockets;
using GG2.Protocol;
using GG2.Server.Wire;

namespace GG2.Server.Rooms;

/// <summary>
/// A room host's one game listener. Reads each connection's HELLO and password,
/// and hands it to the room that password names (<c>docs/ROOMS.md</c>).
/// </summary>
/// <remarks>
/// <b>Stock-client compatible because of where the password sits.</b> The
/// handshake is HELLO, then <c>PASSWORD_REQUEST</c>, then the password, and only
/// then the greeting that names a server and a map (<c>docs/PROTOCOL.md</c>). So
/// nothing a room says has to be said before the front door knows which room it
/// is. Everything here up to the hand-over is <see cref="JoiningConnection"/>'s
/// first two states, answered the same way: <c>INCOMPATIBLE_PROTOCOL</c> on a
/// wrong UUID and <c>PASSWORD_WRONG</c> on a password that names no room, which
/// ends the connection exactly as a stock server's wrong password does.
/// <para>
/// <b>It owns the limits that are about the host, not a room</b>: accepts per
/// tick, the accept rate per address, how many handshakes may be in flight, and
/// the handshake's idle and total timeouts. A room applies its own seats and
/// <see cref="ServerOptions.MultiClientLimit"/> in <see cref="GameServer.Admit"/>.
/// </para>
/// <para>
/// Single-threaded: <see cref="Service"/> runs on the host's tick thread between
/// room ticks, so a hand-over never races a room's own tick.
/// </para>
/// </remarks>
public sealed class RoomFrontDoor : IDisposable
{
    private enum Stage
    {
        Hello,
        NativeVersion,
        PasswordLength,
        Password,
    }

    private sealed class Pending(ClientConnection connection, DateTime now)
    {
        public ClientConnection Connection { get; } = connection;
        public ReceiveBuffer Received { get; } = new(64);
        public Stage Stage { get; set; } = Stage.Hello;
        public int Expected { get; set; } = ProtocolHello.Length;
        public DateTime StartedAt { get; } = now;
        public DateTime LastContact { get; set; } = now;
    }

    private readonly ConnectionLimits _limits;
    private readonly IServerLog _log;
    private readonly Func<string, GameServer?> _findRoom;
    private readonly ConnectionRateLimiter? _rate;
    private readonly LogThrottle _refusalLog = new(burst: 5, per: TimeSpan.FromSeconds(10));
    private readonly List<Pending> _pending = [];
    private TcpListener? _listener;

    /// <param name="port">The game port; 0 asks the OS for one.</param>
    /// <param name="findRoom">The room a password names, or null for none.</param>
    public RoomFrontDoor(int port, ConnectionLimits limits, IServerLog log, Func<string, GameServer?> findRoom)
    {
        _limits = limits;
        _log = log;
        _findRoom = findRoom;
        _rate = limits.ConnectionBurstPerAddress > 0
            ? new ConnectionRateLimiter(limits.ConnectionBurstPerAddress, limits.ConnectionBurstWindow)
            : null;

        _listener = GameListener.Start(port, log);
        BoundPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public int BoundPort { get; }

    /// <summary>Connections that have not yet named a room.</summary>
    public int PendingCount => _pending.Count;

    /// <summary>Accepts what is waiting and advances every pending handshake.</summary>
    public void Service(DateTime now, long nowMs)
    {
        Accept(now, nowMs);

        for (var i = _pending.Count - 1; i >= 0; i--)
        {
            if (Advance(_pending[i], now))
                _pending.RemoveAt(i);
        }
    }

    private void Accept(DateTime now, long nowMs)
    {
        if (_listener is null)
            return;

        var accepted = 0;

        // Bounded for the reason GameServer.AcceptConnections gives: what does
        // not fit stays in the kernel's backlog, where a flood is cheapest.
        while (accepted < _limits.MaxAcceptsPerTick && _listener.Pending())
        {
            accepted++;

            var connection = new ClientConnection(_listener.AcceptSocket(), _limits);

            // Loopback exempt, as in GameServer: it is how a test drives several
            // clients at once, and a flood from loopback is already inside.
            if (_rate is not null
                && !IPAddress.IsLoopback(connection.RemoteAddress)
                && !_rate.Allow(connection.RemoteAddress, nowMs))
            {
                connection.Dispose();
                LogRefusal(nowMs, connection, "connecting too fast");
                continue;
            }

            if (_pending.Count >= _limits.MaxJoiningConnections)
            {
                var full = new NetWriter(2);
                ServerMessages.ServerFull(full);
                connection.Send(full.WrittenSpan);
                connection.Flush();
                connection.Dispose();
                LogRefusal(nowMs, connection, "the front door's queue is full");
                continue;
            }

            _pending.Add(new Pending(connection, now));
        }
    }

    /// <returns>Whether this connection is finished here: handed over or dropped.</returns>
    private bool Advance(Pending pending, DateTime now)
    {
        var connection = pending.Connection;

        if (connection.IsClosed)
            return Drop(pending);

        connection.Receive(pending.Received);

        if (connection.IsClosed)
            return Drop(pending);

        // The reference's idle timeout, which a stock client's password dialog
        // already has to live inside on a stock server.
        if (now - pending.LastContact > JoiningConnection.Timeout)
        {
            connection.Close("handshake timed out");
            return Drop(pending);
        }

        if (now - pending.StartedAt > _limits.MaxHandshakeDuration)
        {
            connection.Close("handshake took too long");
            return Drop(pending);
        }

        while (pending.Received.TryTake(pending.Expected, out var payload))
        {
            pending.LastContact = now;

            switch (pending.Stage)
            {
                case Stage.Hello:
                    // The same classification JoiningConnection makes, so a room
                    // host and a plain server tell the protocols apart one way.
                    switch (ProtocolHello.Classify(payload))
                    {
                        case HelloKind.Legacy:
                            AskForPassword(pending);
                            break;

                        case HelloKind.Native:
                            pending.Stage = Stage.NativeVersion;
                            pending.Expected = 2;
                            break;

                        default:
                            return Incompatible(pending, "protocol uuid mismatch");
                    }

                    break;

                case Stage.NativeVersion:
                {
                    var version = NativeProtocol.ReadVersion(payload);
                    if (!NativeProtocol.Supports(version))
                        return Incompatible(pending, $"native protocol version {version} not served");

                    connection.Speak(WireProtocol.Native, version);
                    AskForPassword(pending);
                    break;
                }

                case Stage.PasswordLength:
                    pending.Stage = Stage.Password;
                    pending.Expected = payload[0];
                    break;

                case Stage.Password:
                    return HandOver(pending, GG2Text.Decode(payload));
            }
        }

        return false;
    }

    /// <summary>A protocol this host speaks: ask which room, in that protocol.</summary>
    private static void AskForPassword(Pending pending)
    {
        var reply = new NetWriter(2);
        ServerEncoder.For(pending.Connection.Protocol).PasswordRequest(reply);
        pending.Connection.Send(reply.WrittenSpan);
        pending.Connection.Flush();
        pending.Stage = Stage.PasswordLength;
        pending.Expected = 1;
    }

    /// <summary>Neither protocol, or a native version this build does not serve.</summary>
    private static bool Incompatible(Pending pending, string why)
    {
        var reply = new NetWriter(2);
        ServerMessages.IncompatibleProtocol(reply);
        pending.Connection.Send(reply.WrittenSpan);
        pending.Connection.Flush();
        pending.Connection.Close(why);
        return Drop(pending);
    }

    private bool HandOver(Pending pending, string password)
    {
        var connection = pending.Connection;

        if (_findRoom(password) is not { } room)
        {
            var wrong = new NetWriter(2);
            ServerEncoder.For(connection.Protocol).PasswordWrong(wrong);
            connection.Send(wrong.WrittenSpan);
            connection.Flush();
            connection.Close("wrong password");
            connection.Dispose();
            return true;
        }

        // The room owns the connection from here, admitted or refused.
        room.Admit(connection, pending.Received.Buffered);
        return true;
    }

    private static bool Drop(Pending pending)
    {
        pending.Connection.Dispose();
        return true;
    }

    private void LogRefusal(long nowMs, ClientConnection connection, string why)
    {
        if (_refusalLog.ShouldLog(nowMs, out var suppressed))
        {
            _log.Warn($"refused {connection.RemoteAddress}: {why}"
                      + (suppressed == 0 ? string.Empty : $" (+{suppressed} similar lines not logged)"));
        }
    }

    public void Dispose()
    {
        foreach (var pending in _pending)
            pending.Connection.Dispose();

        _pending.Clear();
        _listener?.Stop();
        _listener?.Dispose();
        _listener = null;
    }
}
