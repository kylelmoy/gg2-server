using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace GG2.Server.Rooms;

/// <summary>What the room API is configured with.</summary>
public sealed record RoomApiOptions
{
    /// <summary>The HTTP port. 0 asks the OS for one.</summary>
    public int Port { get; init; } = 8301;

    /// <summary>
    /// The address to bind. <b>Loopback by default</b>: the public surface is the
    /// reverse proxy in front of it (<c>docs/ROOMS.md</c> step 6). With no device
    /// check (step 4), the per-address limits below are all that bound a create.
    /// </summary>
    public IPAddress Bind { get; init; } = IPAddress.Loopback;

    /// <summary>
    /// Refuse a create when the machine's <c>MemAvailable</c> is under this many
    /// MB, so rooms never squeeze the fleet server beside them. 0 turns it off;
    /// it is not checked where <c>/proc/meminfo</c> does not exist.
    /// </summary>
    public int MinFreeMemoryMb { get; init; } = 256;

    /// <summary>Live rooms one address may hold at once. 0 turns it off.</summary>
    public int MaxLiveRoomsPerAddress { get; init; } = 2;

    /// <summary>Rooms one address may create in an hour. 0 turns it off.</summary>
    public int MaxCreatesPerAddressPerHour { get; init; } = 6;

    /// <summary>
    /// Take the client's address from the last <c>X-Forwarded-For</c> entry,
    /// for a request whose peer is loopback: what the reverse proxy in front of
    /// this port (step 6) appends. Off, the header is ignored, and behind a
    /// proxy every create would share the proxy's one allowance. <b>Only for a
    /// proxy that appends the header</b>; one that passes a client's own value
    /// through would let a client name any address.
    /// </summary>
    public bool TrustForwardedFor { get; init; }
}

/// <summary>
/// The dispatcher API inside the room host (<c>docs/ROOMS.md</c>, step 3):
/// <c>GET /status</c>, <c>POST /rooms</c>, <c>GET /rooms/{code}</c>.
/// </summary>
/// <remarks>
/// <b>Served on its own threads, and every answer comes from the tick thread</b>
/// through <see cref="RoomHost.Invoke{T}"/>, so the host's state has one owner.
/// A reply waits for at most one host tick, 33 ms.
/// <para>
/// <b>HTTP is <see cref="HttpRequest"/>'s bounded subset</b>, with a 5-second
/// idle timeout per request. TLS is not here: the public surface is a reverse
/// proxy in front of this port (step 6).
/// </para>
/// <para>
/// <b>A create must say <c>Content-Type: application/json</c></b>. A browser
/// cannot send that cross-site without a CORS preflight, which this never
/// answers, so a web page cannot create rooms through a visitor's browser.
/// </para>
/// </remarks>
public sealed class RoomApi : IDisposable
{
    private readonly RoomHost _host;
    private readonly Func<RoomChoices, ServerOptions> _template;
    private readonly RoomApiOptions _options;
    private readonly IServerLog _log;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stopping = new();
    private RoomCreateLimits? _limits;

    /// <param name="template">
    /// Builds a room's options from a player's choices: a fresh copy of the base
    /// config with <see cref="RoomTemplate.Apply"/> over it. Called on the tick thread.
    /// </param>
    public RoomApi(RoomHost host, Func<RoomChoices, ServerOptions> template, RoomApiOptions options, IServerLog log)
    {
        _host = host;
        _template = template;
        _options = options;
        _log = log;
        _listener = new TcpListener(options.Bind, options.Port);
    }

    public int BoundPort { get; private set; }

    /// <summary>
    /// Overrides how free memory is read, for a test. Returns MB, or null where
    /// it cannot be read.
    /// </summary>
    public Func<long?> FreeMemoryMb { get; init; } = ReadMemAvailableMb;

    /// <summary>The clock the create limits read, for a test.</summary>
    public Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;

    /// <summary>Built on first use, so it reads the <see cref="UtcNow"/> an initializer set.</summary>
    private RoomCreateLimits Limits =>
        _limits ??= new RoomCreateLimits(_options.MaxLiveRoomsPerAddress, _options.MaxCreatesPerAddressPerHour, UtcNow);

    public void Start()
    {
        _listener.Start();
        BoundPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _log.Info($"room API listening on {_options.Bind}:{BoundPort}; per address: {_options.MaxLiveRoomsPerAddress} live, {_options.MaxCreatesPerAddressPerHour} an hour"
            + (_options.TrustForwardedFor ? "; X-Forwarded-For trusted from loopback" : string.Empty));
    }

    /// <summary>Serves until <paramref name="token"/> is cancelled or this is disposed.</summary>
    public async Task RunAsync(CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _stopping.Token);

        while (!linked.IsCancellationRequested)
        {
            TcpClient client;

            try
            {
                client = await _listener.AcceptTcpClientAsync(linked.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }

            _ = Task.Run(async () =>
            {
                using (client)
                {
                    try
                    {
                        await ServeAsync(client, linked.Token);
                    }
                    catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
                    {
                        // The client went away, or the host is stopping.
                    }
                }
            }, CancellationToken.None);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken token)
    {
        await using var stream = client.GetStream();
        var peer = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;

        for (var served = 0; served < 20; served++)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
            idle.CancelAfter(TimeSpan.FromSeconds(5));

            HttpRequest? request;

            try
            {
                request = await HttpRequest.ReadAsync(stream, idle.Token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                return;
            }

            if (request is null)
                return;

            var (status, body, extra) = await HandleAsync(request, peer, token);
            await JsonAsync(stream, status, body, token, extra);

            if (!request.KeepAlive)
                return;
        }
    }

    /// <summary>One request's status and JSON body.</summary>
    internal async Task<(int Status, string Body, string Headers)> HandleAsync(HttpRequest request, IPAddress peer, CancellationToken token)
    {
        const string roomsPrefix = "/rooms/";

        switch (request.Method, request.Path)
        {
            case ("GET", "/status"):
                return (200, await _host.Invoke(Status).WaitAsync(token), string.Empty);

            case ("POST", "/rooms"):
                return await CreateAsync(request, ClientAddress(request, peer, _options.TrustForwardedFor), token);

            case ("GET", var path) when path.StartsWith(roomsPrefix, StringComparison.Ordinal):
                var code = path[roomsPrefix.Length..];
                var found = await _host.Invoke(() => _host.Find(code) is { } room ? Describe(room) : null).WaitAsync(token);
                return found is null ? (404, Error("no such room"), string.Empty) : (200, found, string.Empty);

            default:
                return (404, Error($"no {request.Method} {request.Path}"), string.Empty);
        }
    }

    /// <summary>
    /// The address a request is from: the peer, or with
    /// <paramref name="trustForwardedFor"/> and a loopback peer, the last
    /// <c>X-Forwarded-For</c> entry. A header that does not parse is ignored.
    /// </summary>
    internal static IPAddress ClientAddress(HttpRequest request, IPAddress peer, bool trustForwardedFor)
    {
        if (peer.IsIPv4MappedToIPv6)
            peer = peer.MapToIPv4();

        if (!trustForwardedFor || !IPAddress.IsLoopback(peer) || request.Header("X-Forwarded-For") is not { } forwarded)
            return peer;

        var last = forwarded[(forwarded.LastIndexOf(',') + 1)..].Trim();
        return IPAddress.TryParse(last, out var address) ? address : peer;
    }

    private async Task<(int, string, string)> CreateAsync(HttpRequest request, IPAddress address, CancellationToken token)
    {
        var contentType = request.Header("Content-Type") ?? string.Empty;

        if (!contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            return (415, Error("Content-Type must be application/json"), string.Empty);

        RoomChoices choices;

        try
        {
            choices = RoomChoices.Parse(request.Body);
        }
        catch (ArgumentException e)
        {
            return (400, Error(e.Message), string.Empty);
        }

        if (_options.MinFreeMemoryMb > 0 && FreeMemoryMb() is { } free && free < _options.MinFreeMemoryMb)
        {
            _log.Warn($"room refused: {free} MB available, under the {_options.MinFreeMemoryMb} MB floor");
            return (503, Error("this server is busy; try another region"), string.Empty);
        }

        var key = RoomCreateLimits.Key(address);

        return await _host.Invoke<(int, string, string)>(() =>
        {
            if (_host.Rooms.Count >= _host.Options.MaxRooms)
                return (503, Error("this server is full; try another region"), string.Empty);

            var live = 0;

            foreach (var open in _host.Rooms)
            {
                if (open.Creator == key)
                    live++;
            }

            if (Limits.Refusal(key, live) is { } refusal)
            {
                _log.Info($"room refused for {key}: {refusal.Reason}");
                return (429, Error(refusal.Reason), $"Retry-After: {refusal.RetryAfterSeconds}\r\n");
            }

            ServerOptions options;

            try
            {
                options = _template(choices);
            }
            catch (ArgumentException e)
            {
                return (400, Error(e.Message), string.Empty);
            }

            var room = _host.CreateRoom(options, key);

            if (room is null)
                return (503, Error("this server is full; try another region"), string.Empty);

            Limits.Created(key);
            return (201, Describe(room), string.Empty);
        }).WaitAsync(token);
    }

    private string Status()
    {
        var json = new StringBuilder(128);
        json.Append("{\"prefix\":\"").Append(_host.Options.Prefix).Append('"');
        json.Append(",\"gamePort\":").Append(_host.BoundPort);
        json.Append(",\"rooms\":").Append(_host.Rooms.Count);
        json.Append(",\"maxRooms\":").Append(_host.Options.MaxRooms);
        json.Append(",\"free\":").Append(_host.Options.MaxRooms - _host.Rooms.Count);
        json.Append('}');
        return json.ToString();
    }

    private string Describe(HostedRoom room)
    {
        var ends = room.CreatedUtc.AddMinutes(room.Server.Options.MaxLifetimeMinutes);

        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("code", room.Code);
            json.WriteNumber("gamePort", _host.BoundPort);
            json.WriteString("map", room.Server.CurrentMap);
            json.WriteNumber("players", room.Server.HumanCount);
            json.WriteNumber("playerLimit", room.Server.Options.PlayerLimit);
            json.WriteString("createdUtc", room.CreatedUtc.ToString("O", CultureInfo.InvariantCulture));

            if (room.Server.Options.MaxLifetimeMinutes > 0)
                json.WriteString("endsUtc", ends.ToString("O", CultureInfo.InvariantCulture));

            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string Error(string message) => ControlCommands.Error(message);

    private static Task JsonAsync(Stream stream, int status, string body, CancellationToken token, string extra = "") =>
        HttpResponse.TextAsync(stream, status, "application/json", body, token, "Cache-Control: no-store\r\n" + extra);

    /// <summary><c>MemAvailable</c> from <c>/proc/meminfo</c>, in MB, or null off Linux.</summary>
    private static long? ReadMemAvailableMb()
    {
        const string path = "/proc/meminfo";

        if (!File.Exists(path))
            return null;

        foreach (var line in File.ReadLines(path))
        {
            if (!line.StartsWith("MemAvailable:", StringComparison.Ordinal))
                continue;

            var digits = line["MemAvailable:".Length..].Trim().Split(' ')[0];
            return long.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var kb) ? kb / 1024 : null;
        }

        return null;
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _listener.Stop();
        _listener.Dispose();
    }
}
