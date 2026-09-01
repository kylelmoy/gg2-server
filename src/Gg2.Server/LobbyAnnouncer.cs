using System.Net.Sockets;
using Gg2.Protocol;

namespace Gg2.Server;

/// <summary>
/// Sends the lobby datagrams. The only thing in this project that contacts a
/// third party.
/// </summary>
/// <remarks>
/// ⚠️ <b>Constructed only when <c>--lobby</c> was passed.</b> The default is off,
/// per <c>docs/PROTOCOL.md</c>: a private server should not announce itself to a
/// public list because somebody ran the binary.
/// <para>
/// ⚠️ <b>Every failure here is swallowed and logged, never thrown.</b> The lobby
/// is unreachable, DNS is down, the network is gone — none of that is a reason to
/// interrupt a match in progress, and the reference does not stop for it either.
/// A server that cannot be listed is still a server people can join by address.
/// </para>
/// <para>
/// The packet layout is <see cref="LobbyRegistration"/>, which has no socket in
/// it and is where the byte-level tests live. This type is the part that cannot
/// be tested without the network, so it is kept as thin as it can be.
/// </para>
/// </remarks>
public sealed class LobbyAnnouncer : IDisposable
{
    private readonly IServerLog _log;

    private readonly byte[] _serverId = LobbyRegistration.NewServerId();

    private UdpClient? _socket = new();

    public LobbyAnnouncer(IServerLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    /// <summary>
    /// This server's lobby identity, stable for the life of the process.
    /// </summary>
    /// <remarks>
    /// ⚠️ Stable is the whole contract: the lobby keys its entry on this, so a
    /// fresh id per registration would leave a stale listing behind every 30
    /// seconds rather than updating one.
    /// </remarks>
    public ReadOnlyMemory<byte> ServerId => _serverId;

    /// <summary>Announces, or re-announces, this server.</summary>
    public void Register(in LobbyServerInfo info) => Send(LobbyRegistration.BuildRegister(info), "register");

    /// <summary>Takes the listing down. Best effort, on the way out.</summary>
    public void Unregister() => Send(LobbyRegistration.BuildUnregister(_serverId), "unregister");

    private void Send(byte[] packet, string what)
    {
        if (_socket is null)
            return;

        try
        {
            _socket.Send(packet, packet.Length, GameConstants.LobbyServerHost, GameConstants.LobbyServerPort);
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException or InvalidOperationException)
        {
            // Logged at info rather than warn: an unreachable lobby is an
            // ordinary condition for a server on a private network, and a
            // warning every 30 seconds would train everyone to ignore the log.
            _log.Info($"lobby {what} failed: {e.Message}");
        }
    }

    public void Dispose()
    {
        _socket?.Dispose();
        _socket = null;
    }
}
