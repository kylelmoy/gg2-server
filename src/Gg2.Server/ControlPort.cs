using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Gg2.Protocol;
using Gg2.Simulation;

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

    private string Handle(string line)
    {
        JsonElement request;

        try
        {
            request = JsonDocument.Parse(line).RootElement;
        }
        catch (JsonException e)
        {
            return Error($"not JSON: {e.Message}");
        }

        if (!request.TryGetProperty("cmd", out var cmd) || cmd.ValueKind != JsonValueKind.String)
            return Error("no \"cmd\"");

        try
        {
            return Dispatch(cmd.GetString()!, request);
        }
        catch (Exception e) when (e is ArgumentException or FormatException or InvalidOperationException)
        {
            return Error(e.Message);
        }
    }

    private string Dispatch(string cmd, JsonElement request) => cmd switch
    {
        "ping" => Ok($"\"frame\":{_server.Frame}"),

        "state" => State(),

        "freeze" => Do(() => _server.Freeze()),

        "resume" => Do(() => _server.Resume()),

        "step" => Do(() => _server.StepSimulation(Int(request, "ticks", 1))),

        "speed" => Do(() => _server.SetSpeed(Speed(request))),

        "bots" => Do(() => _server.SetBotFill(Int(request, "fill", 0))),

        "map" => _server.RequestMapChange(Text(request, "name"), Int(request, "area", 1))
            ? Ok()
            : Error("no such map, or a map change is already running"),

        "teleport" => _server.Teleport(
            Int(request, "slot", -1),
            Double(request, "x"),
            Double(request, "y"))
            ? Ok()
            : Error("no live character in that slot"),

        "command" => Command(request),

        _ => Error($"unknown cmd \"{cmd}\""),
    };

    private string Do(Action action)
    {
        action();
        return Ok();
    }

    /// <summary>
    /// Submits a <see cref="PlayerCommand"/> on a slot's behalf.
    /// </summary>
    /// <remarks>
    /// ⚠️ The whole point of this command: everything a client can do reaches
    /// the server through one dispatch, and the control port uses that dispatch
    /// rather than growing its own copy of each action.
    /// </remarks>
    private string Command(JsonElement request)
    {
        var kindText = Text(request, "kind");

        if (!Enum.TryParse<PlayerCommandKind>(kindText, ignoreCase: true, out var kind)
            || kind == PlayerCommandKind.None)
        {
            return Error($"unknown command kind \"{kindText}\"");
        }

        var command = new PlayerCommand
        {
            Kind = kind,
            Argument = (byte)Int(request, "argument", 0),
        };

        return _server.ExecuteFor(Int(request, "slot", -1), command)
            ? Ok()
            : Error("no such slot");
    }

    private string State()
    {
        var json = new StringBuilder();

        json.Append("{\"ok\":true");
        json.Append(",\"frame\":").Append(_server.Frame);
        json.Append(",\"map\":\"").Append(Escape(_server.CurrentMap)).Append('"');
        json.Append(",\"frozen\":").Append(_server.IsFrozen ? "true" : "false");
        json.Append(",\"speed\":").Append(Number(_server.Options.SpeedMultiplier));
        json.Append(",\"round\":{\"timer\":").Append(_server.RoundTimer);
        json.Append(",\"decided\":").Append(_server.RoundDecided ? "true" : "false");
        json.Append(",\"winner\":\"").Append(_server.RoundWinner).Append("\"}");
        json.Append(",\"players\":[");

        for (var slot = 0; slot < _server.RosterCount; slot++)
        {
            if (slot > 0)
                json.Append(',');

            json.Append("{\"slot\":").Append(slot);
            json.Append(",\"team\":\"").Append(_server.TeamOf(slot)).Append('"');
            json.Append(",\"class\":\"").Append(_server.ClassOf(slot)).Append('"');
            json.Append(",\"bot\":").Append(_server.SourceOf(slot) is Gg2.Bots.ScriptedBot ? "true" : "false");
            json.Append(",\"alive\":").Append(_server.IsAlive(slot) ? "true" : "false");

            if (_server.TryGetExactPosition(slot, out var x, out var y, out var hp))
            {
                json.Append(",\"x\":").Append(Number(x));
                json.Append(",\"y\":").Append(Number(y));
                json.Append(",\"hp\":").Append(Number(hp));
            }

            json.Append('}');
        }

        json.Append("]}");
        return json.ToString();
    }

    private static string Ok(string? extra = null) =>
        extra is null ? "{\"ok\":true}" : "{\"ok\":true," + extra + "}";

    private static string Error(string message) =>
        "{\"ok\":false,\"error\":\"" + Escape(message) + "\"}";

    /// <summary>
    /// Escapes a string for a JSON literal.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b><see cref="JsonEncodedText"/> rather than three <c>Replace</c>
    /// calls, and the difference is a reply that does not parse.</b> The
    /// hand-rolled version handled backslash, quote and newline — and
    /// <c>ArgumentOutOfRangeException.Message</c> is multi-line with a
    /// <em>carriage return</em> in it, which is a raw control character inside a
    /// JSON string and therefore invalid. So the port answered an invalid speed
    /// with an unparseable error: the one reply a caller most needs to read.
    /// Measured — it failed the test that sends <c>"value":0</c>.
    /// </remarks>
    private static string Escape(string value) => JsonEncodedText.Encode(value).ToString();

    /// <remarks>
    /// ⚠️ Invariant culture and "R", because a machine reads this. A decimal
    /// comma would make the reply un-parseable on exactly the machines whose
    /// locale uses one, and nowhere else — the worst kind of bug to be handed.
    /// Infinity is not JSON, so an unpaced server reports its speed as the
    /// string it is set with.
    /// </remarks>
    private static string Number(double value) =>
        double.IsPositiveInfinity(value)
            ? "\"max\""
            : value.ToString("R", CultureInfo.InvariantCulture);

    private static string Text(JsonElement request, string name) =>
        request.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new ArgumentException($"\"{name}\" must be a string");

    private static int Int(JsonElement request, string name, int fallback) =>
        request.TryGetProperty(name, out var value) && value.TryGetInt32(out var number)
            ? number
            : fallback;

    private static double Double(JsonElement request, string name) =>
        request.TryGetProperty(name, out var value) && value.TryGetDouble(out var number)
            ? number
            : throw new ArgumentException($"\"{name}\" must be a number");

    /// <summary>Accepts a number or the string <c>"max"</c>, as <c>--speed</c> does.</summary>
    private static double Speed(JsonElement request)
    {
        if (!request.TryGetProperty("value", out var value))
            throw new ArgumentException("\"value\" must be a number or \"max\"");

        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString()!.Equals("max", StringComparison.OrdinalIgnoreCase)
                ? double.PositiveInfinity
                : throw new ArgumentException("the only speed word is \"max\"");
        }

        return value.GetDouble();
    }

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
