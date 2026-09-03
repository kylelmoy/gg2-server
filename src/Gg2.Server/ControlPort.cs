using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Gg2.Server;

/// <summary>
/// A loopback-only line-of-JSON control port — <c>docs/ARCHITECTURE.md</c>'s
/// "A control surface, eventually", and phase 8's last item.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists.</b> <c>gg2-agent</c> drives the Game Maker game through an
/// injected bridge because there was no other way in. A C# server does not need
/// injection — it needs a socket and a switch statement — and the capabilities
/// worth having are the ones that tooling actually found useful over the last
/// eight phases: read live state, step N ticks, set the bot count, change the
/// map, teleport, freeze and resume, and run faster than real time.
/// </para>
/// <para>
/// ⚠️ <b>Loopback only, and off unless asked for</b>, for the same reason the
/// GM8 bridge is: it executes commands against a live game. It binds
/// <see cref="IPAddress.Loopback"/> specifically rather than <c>Any</c>, so
/// there is no configuration in which it is reachable from the network — that
/// is a property of the bind, not of a check somebody can forget.
/// </para>
/// <para>
/// ⚠️ <b>Serviced from inside <see cref="GameServer.Tick"/>, on the tick
/// thread.</b> No locks, no concurrent access to <c>WorldState</c>, and a
/// command's effect always lands between two ticks rather than in the middle of
/// one. A control port on its own thread would need the simulation to be
/// thread-safe, which is a far larger claim than anything this project makes —
/// and the failures would be the irreproducible kind.
/// </para>
/// <para>
/// ⚠️ <b>It cannot do anything a client cannot, except operator things.</b>
/// Freezing, stepping, teleporting and changing the map are operator actions no
/// client can ask for. Anything a client <em>can</em> ask for goes through
/// <c>command</c>, which submits a <see cref="PlayerCommand"/> to the same
/// <c>Execute</c> the socket switch and the bots reach — CLAUDE.md's "three
/// places" rule, honoured by not adding a fourth.
/// </para>
/// <para>
/// The protocol is one JSON object per line, answered by one JSON object per
/// line. That is enough for <c>nc</c>, for a test, and for a script, and it
/// needs no schema negotiation.
/// </para>
/// </remarks>
public sealed class ControlPort : IDisposable
{
    private readonly GameServer _server;
    private readonly IServerLog _log;
    private readonly List<Client> _clients = [];
    private TcpListener? _listener;

    private sealed class Client(TcpClient tcp)
    {
        public TcpClient Tcp { get; } = tcp;

        public StringBuilder Pending { get; } = new();
    }

    public ControlPort(GameServer server, IServerLog log)
    {
        _server = server;
        _log = log;
    }

    /// <summary>The port actually bound. Zero asks the OS for a free one.</summary>
    public int BoundPort { get; private set; }

    public void Start(int port)
    {
        // ⚠️ Loopback, never Any. See the remarks.
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();

        BoundPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _log.Info($"control port on 127.0.0.1:{BoundPort}");
    }

    /// <summary>
    /// Accepts, reads and answers whatever is pending. Never blocks.
    /// </summary>
    public void Service()
    {
        if (_listener is null)
            return;

        while (_listener.Pending())
        {
            var tcp = _listener.AcceptTcpClient();
            tcp.NoDelay = true;
            _clients.Add(new Client(tcp));
        }

        for (var i = _clients.Count - 1; i >= 0; i--)
        {
            if (!ServiceOne(_clients[i]))
            {
                _clients[i].Tcp.Dispose();
                _clients.RemoveAt(i);
            }
        }
    }

    private bool ServiceOne(Client client)
    {
        var stream = client.Tcp.GetStream();

        if (!client.Tcp.Connected)
            return false;

        var buffer = ArrayPool<byte>.Shared.Rent(4096);

        try
        {
            while (client.Tcp.Available > 0)
            {
                var read = stream.Read(buffer, 0, Math.Min(buffer.Length, client.Tcp.Available));
                if (read <= 0)
                    return false;

                client.Pending.Append(Encoding.UTF8.GetString(buffer, 0, read));
            }
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
        {
            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        // Whole lines only: a command split across two reads is answered when
        // the rest of it arrives, not refused.
        while (true)
        {
            var text = client.Pending.ToString();
            var newline = text.IndexOf('\n');

            if (newline < 0)
                break;

            var line = text[..newline].Trim();
            client.Pending.Remove(0, newline + 1);

            if (line.Length == 0)
                continue;

            if (!Reply(stream, Handle(line)))
                return false;
        }

        return true;
    }

    private static bool Reply(NetworkStream stream, string json)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(json + "\n");
            stream.Write(bytes, 0, bytes.Length);
            return true;
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Answers one line.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Transport only.</b> Every verb, every reply and every error message
    /// lives in <see cref="ControlCommands"/>, so that this port and the web
    /// admin panel cannot drift into two slightly different control surfaces.
    /// Do not add a <c>cmd</c> here.
    /// </remarks>
    private string Handle(string line) => ControlCommands.Dispatch(_server, line);

    public void Dispose()
    {
        foreach (var client in _clients)
            client.Tcp.Dispose();

        _clients.Clear();

        _listener?.Stop();
        _listener?.Dispose();
        _listener = null;
    }
}
