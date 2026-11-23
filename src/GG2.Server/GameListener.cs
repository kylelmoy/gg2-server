using System.Net;
using System.Net.Sockets;

namespace GG2.Server;

/// <summary>
/// The game port's listening socket: both address families, as the stock GM8
/// server listens (<c>0.0.0.0</c> and <c>[::]</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Dual-stack, because an IPv4-only bind costs a client two seconds.</b> On
/// Windows <c>localhost</c> resolves to <c>::1</c> first, and a client that
/// tries addresses in order waits out the refused IPv6 attempt (2054 ms,
/// measured against this server) before falling back to
/// <c>127.0.0.1</c>. A GM8 server does not cause that, so this server should not.
/// </para>
/// <para>
/// <b>An IPv4 client arrives as <c>::ffff:a.b.c.d</c> on a dual-mode socket</b>,
/// and <see cref="ClientConnection"/> maps it back down at capture. Everything
/// keyed on an address — bans, the per-address client limit, the connect-rate
/// limiter, logs, analytics, plugins — therefore sees IPv4 exactly as it did
/// with an IPv4-only bind. <see cref="IpRange"/> deliberately does not map, so
/// without that normalisation every IPv4 ban entry would stop matching.
/// </para>
/// <para>
/// Falls back to IPv4 <c>Any</c> when the host cannot bind IPv6 at all (no
/// IPv6 stack, or IPv6 disabled in a container), so the change never costs a
/// server its IPv4 listener.
/// </para>
/// </remarks>
internal static class GameListener
{
    public static TcpListener Start(int port, IServerLog log)
    {
        if (Socket.OSSupportsIPv6)
        {
            var dual = new TcpListener(IPAddress.IPv6Any, port);

            try
            {
                dual.Server.DualMode = true;
                dual.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
                dual.Start();
                return dual;
            }
            catch (SocketException e) when (e.SocketErrorCode is SocketError.AddressFamilyNotSupported
                                                or SocketError.AddressNotAvailable
                                                or SocketError.ProtocolNotSupported
                                                or SocketError.OperationNotSupported
                                                or SocketError.InvalidArgument)
            {
                dual.Stop();
                log.Warn($"IPv6 listen failed ({e.SocketErrorCode}); listening on IPv4 only");
            }
        }

        var v4 = new TcpListener(IPAddress.Any, port);
        v4.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
        v4.Start();
        return v4;
    }
}
