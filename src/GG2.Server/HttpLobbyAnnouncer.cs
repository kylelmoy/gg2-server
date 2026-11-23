using System.Buffers;
using System.Net;
using System.Text.Json;
using GG2.Protocol;

namespace GG2.Server;

/// <summary>
/// Announces this server to a gg2-lobby: the HTTP/JSON list in
/// <c>src/GG2.Lobby</c>, documented in <c>docs/LOBBY.md</c>.
/// </summary>
/// <remarks>
/// <b>The tick thread never waits on the network here.</b>
/// <see cref="Register"/> only stores the latest state and wakes a background
/// worker; the worker sends whatever is newest when it gets to it. So a burst
/// of joins is a burst of overwrites of one field and at most one request in
/// flight, never a queue — the lobby sees the state as of the last send, which
/// is all it lists anyway.
/// <para>
/// <b>Runs beside <see cref="LobbyAnnouncer"/>, not instead of it.</b> The two
/// lobbies are separate services with separate ids: this one's id is the secret
/// that lets this server withdraw its own listing, and handing the same bytes
/// to the ganggarrison.com lobby would let that operator withdraw it here.
/// </para>
/// <para>
/// Failures are logged when they CHANGE, not on every attempt — a lobby that is
/// down would otherwise put a warning in the log every 30 seconds.
/// </para>
/// </remarks>
public sealed class HttpLobbyAnnouncer : IDisposable
{
    /// <summary>Requests closer together than this are coalesced into one.</summary>
    /// <remarks>
    /// Joins are announced as they happen (<c>serviceJoiningPlayer.gml:171</c>),
    /// and a server filling at map start would otherwise send one PUT per
    /// player. Two seconds is invisible in a server browser.
    /// </remarks>
    public static readonly TimeSpan MinSpacing = TimeSpan.FromSeconds(2);

    /// <summary>How long shutdown waits for the withdraw to go out.</summary>
    public static readonly TimeSpan WithdrawBudget = TimeSpan.FromSeconds(2);

    private enum Op
    {
        None,
        Put,
        Delete,
    }

    private readonly IServerLog _log;
    private readonly Uri _resource;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _gate = new();
    private readonly Task _worker;
    private readonly TimeSpan _spacing;

    private Op _pending;
    private LobbyServerInfo _info;
    private bool _exit;
    private string? _lastOutcome;

    /// <param name="baseUrl">The lobby's root, e.g. <c>https://lobby.example</c>.</param>
    public HttpLobbyAnnouncer(Uri baseUrl, IServerLog log)
        : this(baseUrl, log, MinSpacing)
    {
    }

    internal HttpLobbyAnnouncer(Uri baseUrl, IServerLog log, TimeSpan spacing)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(log);

        _log = log;
        _spacing = spacing;
        Id = Convert.ToHexStringLower(LobbyRegistration.NewServerId());

        var root = baseUrl.AbsoluteUri.EndsWith('/') ? baseUrl : new Uri(baseUrl.AbsoluteUri + "/");
        _resource = new Uri(root, "servers/" + Id);

        _http = new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(5),
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
        })
        {
            // The lobby probes this server's port before it answers a first
            // announce, for up to three seconds, so the budget is above that.
            Timeout = TimeSpan.FromSeconds(10),
        };

        _worker = Task.Run(RunAsync);
    }

    /// <summary>This server's id at the lobby: 32 hex digits, fresh per process.</summary>
    public string Id { get; }

    /// <summary>Where the announces go.</summary>
    public Uri Resource => _resource;

    /// <summary>Announces, or re-announces, this server. Returns at once.</summary>
    public void Register(in LobbyServerInfo info)
    {
        lock (_gate)
        {
            _info = info;
            _pending = Op.Put;
        }
        Wake();
    }

    /// <summary>Takes the listing down. Returns at once; a later <see cref="Register"/> lists it again.</summary>
    public void Unregister()
    {
        lock (_gate)
            _pending = Op.Delete;
        Wake();
    }

    /// <summary>
    /// Withdraws the listing and stops, waiting up to <see cref="WithdrawBudget"/>
    /// for the withdraw to be sent.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_exit)
                return;
            _exit = true;
            _pending = Op.Delete;
        }

        // Cancels the coalescing delay, not the request: the withdraw below
        // still goes out with its own budget.
        _stopping.Cancel();
        Wake();

        _worker.Wait(WithdrawBudget + TimeSpan.FromMilliseconds(500));
        _http.Dispose();
    }

    private void Wake()
    {
        lock (_gate)
            if (_wake.CurrentCount == 0)
                _wake.Release();
    }

    private async Task RunAsync()
    {
        while (true)
        {
            await _wake.WaitAsync().ConfigureAwait(false);

            Op op;
            LobbyServerInfo info;
            bool exit;
            lock (_gate)
            {
                op = _pending;
                info = _info;
                exit = _exit;
                _pending = Op.None;
            }

            if (op == Op.Put)
                await SendAsync(HttpMethod.Put, Body(info), CancellationToken.None).ConfigureAwait(false);
            else if (op == Op.Delete)
                await WithdrawAsync().ConfigureAwait(false);

            if (exit)
                return;

            try
            {
                await Task.Delay(_spacing, _stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Shutdown: loop round at once and send the withdraw.
            }
        }
    }

    private async Task WithdrawAsync()
    {
        using var budget = new CancellationTokenSource(WithdrawBudget);
        await SendAsync(HttpMethod.Delete, null, budget.Token).ConfigureAwait(false);
    }

    private async Task SendAsync(HttpMethod method, byte[]? body, CancellationToken token)
    {
        string outcome;
        var ok = false;
        try
        {
            using var request = new HttpRequestMessage(method, _resource);
            if (body is not null)
            {
                request.Content = new ByteArrayContent(body);
                request.Content.Headers.ContentType = new("application/json");
            }

            using var response = await _http.SendAsync(request, token).ConfigureAwait(false);
            ok = response.IsSuccessStatusCode;
            outcome = (int)response.StatusCode switch
            {
                _ when ok && method == HttpMethod.Delete => "withdrawn",
                _ when ok => "listed",
                422 => "the lobby cannot connect to this server's port - is it forwarded?",
                503 => "the lobby is full or busy",
                var code => $"HTTP {code}: {await response.Content.ReadAsStringAsync(token).ConfigureAwait(false)}",
            };
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException or ObjectDisposedException)
        {
            outcome = e is OperationCanceledException ? "timed out" : e.Message;
        }

        if (outcome == _lastOutcome)
            return;
        _lastOutcome = outcome;

        if (ok)
            _log.Info($"lobby {_resource.GetLeftPart(UriPartial.Authority)}: {outcome}");
        else
            _log.Warn($"lobby {_resource.GetLeftPart(UriPartial.Authority)} {method.Method.ToLowerInvariant()} failed: {outcome}");
    }

    /// <summary>The announce body. docs/LOBBY.md, "PUT /servers/&lt;id&gt;".</summary>
    internal static byte[] Body(in LobbyServerInfo info)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("port"u8, info.Port);
            writer.WriteString("name"u8, info.ServerName);
            writer.WriteString("map"u8, info.Map);
            writer.WriteNumber("slots"u8, info.PlayerLimit);
            writer.WriteNumber("players"u8, info.OccupiedSlots);
            writer.WriteNumber("bots"u8, info.Bots);
            writer.WriteBoolean("passworded"u8, info.Passworded);
            writer.WriteString("game"u8, LobbyRegistration.GameNameString);
            writer.WriteString("version"u8, LobbyRegistration.GameVersionString);
            writer.WriteString("url"u8, LobbyRegistration.GameUrlString);

            // Both wires this server speaks, by the UUID each one's HELLO
            // carries, so a browser can tell a native-capable server from a
            // stock one without connecting. The legacy registration cannot say
            // this; docs/PROTOCOL.md, "Not protocol-aware yet".
            writer.WriteStartArray("protocols"u8);
            writer.WriteStringValue(GameConstants.ProtocolUuid);
            writer.WriteStringValue(NativeProtocol.Uuid);
            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }
}
