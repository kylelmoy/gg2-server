using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace GG2.Server;

/// <summary>
/// The remote admin panel — <c>gg2-server --web &lt;port&gt;</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists.</b> <see cref="ControlPort"/> answers the same questions,
/// but only to something already on the machine and only in JSON. An operator
/// running a public server wants to see who is on it, why a tick is slow and
/// what map is next from wherever they are, and wants to change the map, the bot
/// count or the speed without an SSH session. That is a web page.
/// </para>
/// <para>
/// <b>THE THREADING RULE, and it is the whole design.</b> The simulation is
/// single-threaded and <c>WorldState</c> has no locks, so nothing on this class's
/// socket threads may touch <see cref="GameServer"/>. Instead:
/// <list type="bullet">
/// <item>a request that <em>changes</em> anything is queued and executed by
/// <see cref="Pump"/>, on the tick thread, between two ticks — the same
/// guarantee the control port gets by being serviced inline;</item>
/// <item>a request that only <em>reads</em> is answered from
/// <see cref="_snapshot"/>, an immutable JSON string the tick thread
/// republishes.</item>
/// </list>
/// The control port's own approach — service the sockets inline — is
/// <em>wrong</em> here and that is the reason for the difference. A control-port
/// client is on loopback and is a tool; a panel client is across a network and
/// may be slow, hostile, or a phone that went into a tunnel mid-response.
/// Writing to it from <see cref="GameServer.Tick"/> would let it stall the
/// simulation for every player on the server by simply not reading.
/// </para>
/// <para>
/// <b>It adds no verb of its own.</b> Everything it can do goes through
/// <see cref="ControlCommands"/>, which the control port also uses, so the two
/// surfaces cannot drift apart. What is here is transport, authentication and
/// the page.
/// </para>
/// <para>
/// <b>Reachable from the network, which nothing else in this project is.</b>
/// <see cref="WebAdminOptions"/> has the argument; the short version is that the
/// password is mandatory, generated when unset, checked in constant time, and
/// rate-limited, and that a non-loopback bind without TLS warns about the
/// password crossing the network in the clear.
/// </para>
/// </remarks>
public sealed class WebAdminServer : IDisposable
{
    /// <summary>
    /// Ticks between snapshots — 6, so 5 Hz on a 30 Hz server.
    /// </summary>
    /// <remarks>
    /// <b>Fast enough to watch a fight, slow enough not to matter.</b> A
    /// snapshot walks the roster and builds a string; at 30 Hz that would be a
    /// real per-tick cost for a page nobody can read that fast. It is also built
    /// <em>only while somebody is watching</em> — see <see cref="_watchers"/>.
    /// On an idle server the tick rate itself drops to 2 Hz
    /// (<c>--no-idle-when-empty</c>), so the panel updates twice a second and
    /// says "idle" rather than looking stuck.
    /// </remarks>
    public const int SnapshotIntervalTicks = 6;

    /// <summary>How long a queued command waits for the tick thread before failing.</summary>
    /// <remarks>
    /// Generous next to a 33 ms tick, and short enough that a panel against a
    /// server that has stopped ticking says so instead of hanging.
    /// </remarks>
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Sockets served at once. Beyond this, a connection is closed immediately.</summary>
    private const int MaxConnections = 64;

    /// <summary>Commands that may be waiting for the tick thread at once.</summary>
    private const int MaxQueuedCommands = 256;

    /// <summary>How long a kept-alive connection may sit idle before it is dropped.</summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);

    private readonly GameServer _server;
    private readonly IServerLog _log;
    private readonly LogRing _logRing;
    private readonly WebAdminOptions _options;
    private readonly AdminSessions _sessions;
    private readonly PerfHistory _perf = new();
    private readonly ConcurrentQueue<Pending> _queue = new();
    private readonly CancellationTokenSource _stopping = new();

    private TcpListener? _listener;
    private X509Certificate2? _certificate;
    private int _connections;
    private int _watchers;
    private int _queued;
    private long _snapshotVersion;
    private long _sweptAtFrame;
    /// <remarks>
    /// <b><see cref="_snapshotVersion"/> is 0 until the tick thread has
    /// published a real one, and the event stream will not send this.</b>
    /// Otherwise the panel's first act on connecting would be to render a
    /// state object with no fields in it and throw — an error a browser shows
    /// nowhere the operator will look.
    /// </remarks>
    private string _snapshot = "{\"ok\":false,\"error\":\"no snapshot yet\"}";

    private sealed class Pending
    {
        public required string Body { get; init; }

        public TaskCompletionSource<string> Result { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public WebAdminServer(GameServer server, IServerLog log, LogRing logRing, WebAdminOptions options)
    {
        _server = server;
        _log = log;
        _logRing = logRing;
        _options = options;

        Password = options.Password.Length != 0
            ? options.Password
            : AdminSessions.GeneratePassword();

        Generated = options.Password.Length == 0;
        _allowed = options.AllowedRanges();

        if (options.FleetMember)
        {
            _memberToken = WebIdentity.LoadOrCreateToken(
                WebIdentity.Directory(server.Options), out _memberTokenCreated);
        }

        _sessions = new AdminSessions(Password, memberToken: _memberToken);

        if (options.FleetDirectory.Length != 0)
        {
            var directory = Path.IsPathRooted(options.FleetDirectory)
                ? options.FleetDirectory
                : Path.Combine(WebIdentity.Directory(server.Options), options.FleetDirectory);

            foreach (var line in FleetHub.MigrateLegacyFiles(directory))
                _log.Info($"fleet hub: {line}");

            _hub = new FleetHub(directory);
        }
    }

    private readonly FleetHub? _hub;

    /// <summary>The fleet this panel manages, or null when it is not a hub.</summary>
    internal FleetHub? Hub => _hub;

    private readonly IReadOnlyList<IpRange> _allowed;
    private readonly string? _memberToken;
    private readonly bool _memberTokenCreated;

    /// <summary>The certificate's SHA-256 as hex, or null over plain HTTP.</summary>
    public string? Fingerprint { get; private set; }

    /// <summary>
    /// What a fleet hub is given to manage this server — the token and the
    /// certificate's fingerprint — or null when this server is not a member.
    /// </summary>
    public string? PairingCode =>
        _memberToken is not null && Fingerprint is not null
            ? WebIdentity.PairingCode(_memberToken, Fingerprint)
            : null;

    /// <summary>
    /// Whether a connection from <paramref name="address"/> may be served at all.
    /// </summary>
    /// <remarks>
    /// Loopback always may: <see cref="WebAdminOptions.AllowFrom"/> has why.
    /// An IPv4 address that arrives mapped into IPv6 is compared as IPv4, or a
    /// dual-stack bind would refuse every address on the list.
    /// </remarks>
    internal static bool Admits(IReadOnlyList<IpRange> allowed, IPAddress address)
    {
        if (allowed.Count == 0)
            return true;

        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (IPAddress.IsLoopback(address))
            return true;

        foreach (var range in allowed)
        {
            if (range.Contains(address))
                return true;
        }

        return false;
    }

    /// <summary>The password the panel will accept.</summary>
    /// <remarks>
    /// Held in the clear here because the operator can be shown it, which is
    /// the point of <see cref="Generated"/>. <see cref="AdminSessions"/> keeps
    /// only a salted hash for the comparison.
    /// </remarks>
    public string Password { get; }

    /// <summary>Whether <see cref="Password"/> was generated rather than configured.</summary>
    public bool Generated { get; }

    /// <summary>The port actually bound. Zero asks the OS for a free one.</summary>
    public int BoundPort { get; private set; }

    /// <summary>Whether the panel is served over TLS.</summary>
    public bool Secure => _certificate is not null;

    /// <inheritdoc cref="GameServer.WebSnapshotCount"/>
    internal long SnapshotCount => Interlocked.Read(ref _snapshotVersion);

    public void Start()
    {
        if (_options.AutoCertificateRequested)
        {
            var directory = WebIdentity.Directory(_server.Options);
            _certificate = WebIdentity.LoadOrCreateCertificate(directory, out var created);

            if (created)
                _log.Info($"web admin: made a self-signed certificate at {Path.Combine(directory, WebIdentity.CertificateFile)}");
        }
        else if (_options.CertificatePath.Length != 0)
        {
            _certificate = X509CertificateLoader.LoadPkcs12FromFile(
                _options.CertificatePath,
                _options.CertificatePassword.Length == 0 ? null : _options.CertificatePassword);
        }

        if (_certificate is not null)
            Fingerprint = WebIdentity.Fingerprint(_certificate);

        _listener = new TcpListener(IPAddress.Parse(_options.Bind), _options.Port);
        _listener.Start();
        BoundPort = ((IPEndPoint)_listener.LocalEndpoint).Port;

        var scheme = Secure ? "https" : "http";
        var shown = _options.Bind == "0.0.0.0" ? "<this machine>" : _options.Bind;

        _log.Info($"web admin on {scheme}://{shown}:{BoundPort}/");

        // Every start, so it can be compared against what a hub or a browser's
        // certificate warning shows without anything else to go on.
        if (Fingerprint is not null)
            _log.Info($"web admin certificate SHA-256: {Fingerprint}");

        if (_allowed.Count > 0)
            _log.Info($"web admin accepts connections only from loopback and {_options.AllowFrom}");

        // ⚠️ Printed every start, like a generated password, because it is the
        // only way an operator gets it. It is a credential: anybody who can
        // connect (AllowFrom) and has it can do anything the panel can.
        if (PairingCode is { } pairing)
        {
            _log.Info(
                $"fleet member {(_memberTokenCreated ? "token generated" : "token loaded")}; "
                + $"pairing code for the hub: {pairing}");
        }

        if (Generated)
        {
            // The only place this is ever printed. An operator who misses it
            // restarts with --web-password; there is deliberately no endpoint
            // that hands it back.
            //
            // ⚠️ "this process" rather than "changes on restart", and the
            // distinction is load-bearing: a restart asked for from the PANEL
            // carries this password into the new server — Program.Main, and
            // GameServer.GeneratedWebPassword — precisely so it does not lock out
            // the operator who pressed the button. A new process generates a new
            // one, and only that.
            _log.Info($"web admin password (generated, lasts as long as this process): {Password}");
        }

        // Printed every start, not once, and phrased as what an attacker gets
        // rather than as "consider using TLS". The failure it describes — the
        // admin password read off the wire — is silent and total.
        if (_options.IsRemote && !Secure)
        {
            _log.Info(
                "WARNING: the web admin is reachable from the network over plain HTTP, so "
                + "the admin password and every session cookie cross it in the clear. "
                + "Pass --web-tls <cert.pfx>, or put it behind a reverse proxy that "
                + "terminates TLS, or bind it to 127.0.0.1 and reach it over an SSH tunnel.");
        }

        _ = Task.Run(() => AcceptLoopAsync(_stopping.Token));
    }

    /// <summary>
    /// The tick thread's half: run what the panel asked for, publish what it
    /// reads.
    /// </summary>
    /// <remarks>
    /// <b>Called from inside <see cref="GameServer.Tick"/> and nowhere
    /// else.</b> Everything it does touches the game, and nothing else in this
    /// class may.
    /// </remarks>
    public void Pump(long frame)
    {
        while (_queue.TryDequeue(out var pending))
        {
            Interlocked.Decrement(ref _queued);

            // TrySetResult, not SetResult: the waiting request may already
            // have timed out and gone, and a command that lands after that is not
            // an error — it ran.
            pending.Result.TrySetResult(ControlCommands.Dispatch(_server, pending.Body));
        }

        // Sampled whether or not anybody is watching, which is the OPPOSITE of
        // the snapshot below and is deliberate: an operator opens the panel
        // because something already happened, and a graph that started when they
        // arrived answers a question nobody has. The per-tick cost is the one
        // TickCount64 comparison inside Sample. See PerfHistory.
        _perf.Sample(_server);

        // Only while a browser is actually watching. A server with the panel
        // enabled and nobody on it pays nothing per tick beyond this comparison,
        // which is the property that makes leaving --web on reasonable.
        if (Volatile.Read(ref _watchers) > 0 && frame % SnapshotIntervalTicks == 0)
        {
            Volatile.Write(ref _snapshot, ControlCommands.State(_server));
            Interlocked.Increment(ref _snapshotVersion);
        }

        // Once a minute, off the tick thread's critical path in every sense: it
        // walks two small dictionaries.
        if (frame - _sweptAtFrame >= 30 * 60)
        {
            _sweptAtFrame = frame;
            _sessions.Sweep();
        }
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;

            try
            {
                client = await _listener!.AcceptTcpClientAsync(token);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }

            // Before the connection counts against MaxConnections, before TLS and
            // before a byte is read: an address off the list gets nothing from
            // this process but a closed socket. Not logged — a scan would fill
            // the log, and the rule for a client-driven log line is a throttle.
            if (!Admits(_allowed, (client.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None))
            {
                client.Dispose();
                continue;
            }

            if (Interlocked.Increment(ref _connections) > MaxConnections)
            {
                Interlocked.Decrement(ref _connections);
                client.Dispose();
                continue;
            }

            _ = Task.Run(() => ServeAsync(client, token), CancellationToken.None);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken token)
    {
        try
        {
            client.NoDelay = true;

            await using var network = client.GetStream();
            Stream stream = network;
            SslStream? tls = null;

            if (_certificate is not null)
            {
                tls = new SslStream(network, leaveInnerStreamOpen: true);
                await tls.AuthenticateAsServerAsync(
                    _certificate,
                    clientCertificateRequired: false,
                    checkCertificateRevocation: false);
                stream = tls;
            }

            try
            {
                // Bounded. A keep-alive connection that is answered for ever
                // is a connection slot held for ever, and there are only
                // MaxConnections of them.
                for (var served = 0; served < 200 && !token.IsCancellationRequested; served++)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
                    idle.CancelAfter(IdleTimeout);

                    var request = await HttpRequest.ReadAsync(stream, idle.Token);

                    if (request is null)
                        return;

                    var address = (client.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None;

                    if (!await HandleAsync(request, stream, address, token) || !request.KeepAlive)
                        return;
                }
            }
            finally
            {
                if (tls is not null)
                    await tls.DisposeAsync();
            }
        }
        catch (Exception e) when (e is IOException
                                      or SocketException
                                      or ObjectDisposedException
                                      or OperationCanceledException
                                      or AuthenticationException)
        {
            // A browser closing a tab, a TLS handshake from something that is not
            // a browser, a phone leaving a tunnel. None of these is the server's
            // problem and logging them would make the log useless.
        }
        catch (Exception e)
        {
            _log.Error("web admin: unhandled exception serving a request", e);
        }
        finally
        {
            Interlocked.Decrement(ref _connections);
            client.Dispose();
        }
    }

    /// <summary>Routes one request. Returns false when the connection is finished.</summary>
    private async Task<bool> HandleAsync(
        HttpRequest request,
        Stream stream,
        IPAddress address,
        CancellationToken token)
    {
        // A hub authenticates with a bearer token on every request instead of
        // a cookie. An Authorization header that is PRESENT decides the request
        // on its own: a wrong one is refused even if a valid cookie came with it,
        // so a hub with a stale token finds out rather than riding on a session.
        var member = false;

        if (request.Header("Authorization") is { } authorization)
        {
            const string bearer = "Bearer ";

            var remaining = _sessions.LockoutRemaining(address);

            if (remaining > TimeSpan.Zero)
            {
                return await JsonAsync(
                    stream,
                    429,
                    Error($"too many attempts — try again in {(int)remaining.TotalSeconds + 1}s"),
                    token);
            }

            if (!authorization.StartsWith(bearer, StringComparison.OrdinalIgnoreCase)
                || !_sessions.AuthenticateMember(address, authorization[bearer.Length..].Trim()))
            {
                await Task.Delay(AdminSessions.FailureDelay, token);
                _log.Warn($"web admin: refused a fleet token from {address}");
                return await JsonAsync(stream, 401, Error("bad fleet token"), token);
            }

            member = true;
        }

        var session = member ? null : _sessions.Validate(request.Cookie(CookieName));

        switch (request.Method, request.Path)
        {
            case ("GET", "/"):
                return await AssetAsync(stream, "index.html", "text/html; charset=utf-8", token);

            case ("GET", "/app.css"):
                return await AssetAsync(stream, "app.css", "text/css; charset=utf-8", token);

            case ("GET", "/app.js"):
                return await AssetAsync(stream, "app.js", "text/javascript; charset=utf-8", token);

            case ("GET", "/strings.js"):
                return await AssetAsync(stream, "strings.js", "text/javascript; charset=utf-8", token);

            case ("GET", "/common.js"):
                return await AssetAsync(stream, "common.js", "text/javascript; charset=utf-8", token);

            case ("GET", "/analytics.js"):
                return await AssetAsync(stream, "analytics.js", "text/javascript; charset=utf-8", token);

            case ("GET", "/settings.js"):
                return await AssetAsync(stream, "settings.js", "text/javascript; charset=utf-8", token);

            case ("GET", "/favicon.svg"):
                return await AssetAsync(stream, "favicon.svg", "image/svg+xml", token);

            // The fleet settings page: config edit's own page, which notices
            // where it is served from and talks to the hub's routes instead.
            // Static, like the dashboard; its API is behind the session.
            case ("GET", "/fleet/settings"):
                return await AssetAsync(stream, "editor.html", "text/html; charset=utf-8", token);

            case ("GET", "/editor.js"):
                return await AssetAsync(stream, "editor.js", "text/javascript; charset=utf-8", token);

            case ("GET", "/editor.css"):
                return await AssetAsync(stream, "editor.css", "text/css; charset=utf-8", token);

            // Served without a session, like the stylesheet that names them.
            case ("GET", var path) when WebAssets.Fonts.TryGetValue(path, out var font):
                return await AssetAsync(stream, font, "font/woff2", token);

            // ⚠️ Served WITHOUT a session, like the page and the stylesheet, and
            // for the same reason: it is a static asset of the application, the
            // same eighty kilobytes of public-domain coastline for every server
            // in the world. It says nothing about this one. The analytics it is
            // drawn with are behind the session, below.
            case ("GET", "/world.json"):
                return await AssetAsync(stream, "world.json", "application/json", token);

            // The one endpoint that answers before sign-in, and it answers
            // with as little as it can: whether this browser is signed in, and
            // whether the connection is encrypted. The server's name is not a
            // secret — it is on the wire to every game client and in the public
            // lobby — but nothing else is offered.
            case ("GET", "/api/session"):
                return await JsonAsync(stream, 200, Json(
                    $"\"authenticated\":{(session is not null || member ? "true" : "false")}",
                    $"\"secure\":{(Secure ? "true" : "false")}",

                    // The CSRF token is re-issued to a browser that already
                    // holds a valid cookie, so a page reload does not mean
                    // signing in again. Safe here and only here: no CORS headers
                    // are sent, so only a page on this origin can read the reply,
                    // and the SameSite=Strict cookie that authorised the request
                    // is not attached to a cross-site one at all.
                    session is null ? "\"csrf\":null" : $"\"csrf\":\"{session.Csrf}\"",
                    $"\"name\":\"{ControlCommands.Escape(_server.Options.ServerName)}\""), token);

            case ("POST", "/api/login"):
                return await LoginAsync(request, stream, address, token);

            case ("POST", "/api/logout"):
                _sessions.SignOut(request.Cookie(CookieName));
                return await JsonAsync(stream, 200, "{\"ok\":true}", token, ClearCookie);
        }

        if (session is null && !member)
            return await JsonAsync(stream, 401, Error("sign in first"), token);

        // Every state-changing request must echo the token issued at sign-in.
        // The SameSite=Strict cookie already stops a cross-site post from
        // carrying credentials; this stops anything that gets past it, because a
        // page on another origin cannot read the token out of the login reply.
        // A hub's bearer token is exempt: a browser never attaches one by itself,
        // which is the only thing CSRF defends against. AdminSessions.AuthenticateMember.
        if (session is not null
            && request.Method != "GET"
            && !string.Equals(request.Header("X-CSRF-Token"), session.Csrf, StringComparison.Ordinal))
        {
            return await JsonAsync(stream, 403, Error("bad or missing CSRF token"), token);
        }

        // The hub's routes. A BROWSER's, never a token's: a member's token
        // authenticates a hub to that member, and letting it drive this panel's
        // own fleet would chain one member's credential into all of them.
        if (_hub is not null && session is not null)
        {
            string body;

            if (request.Path.StartsWith("/h/", StringComparison.Ordinal))
                return await ProxyAsync(request, stream, token);

            switch (request.Method, request.Path)
            {
                case ("GET", "/api/fleet"):
                    return await JsonAsync(stream, 200, await _hub.OverviewJsonAsync(token), token);

                case ("POST", "/api/fleet/add"):
                    return await FleetAddAsync(request, stream, token);

                case ("POST", "/api/fleet/remove"):
                    return await FleetRemoveAsync(request, stream, token);

                // The fleet settings page's three calls: FleetConfigEditor over
                // the hub's fleet directory, which is local files and no game —
                // so answered here, under the editor's own lock.
                case ("GET", "/api/fleet/config/model"):
                    lock (_hub.ConfigLock)
                        body = _hub.Config.Model();
                    return await JsonAsync(stream, 200, body, token);

                case ("GET", "/api/fleet/config/preview"):
                    lock (_hub.ConfigLock)
                        body = _hub.Config.Preview(request.Q("host") ?? string.Empty);
                    return await JsonAsync(stream, 200, body, token);

                case ("POST", "/api/fleet/config/save"):
                    lock (_hub.ConfigLock)
                        body = _hub.Config.Save(request.Body);
                    return await JsonAsync(stream, 200, body, token);

                // What a push would change on every member, as each member's own
                // dry run — POST, because it asks every member to do work.
                case ("POST", "/api/fleet/push/check"):
                    return await JsonAsync(stream, 200, await _hub.CheckJsonAsync(token), token);

                case ("POST", "/api/fleet/push"):
                    return await FleetPushAsync(request, stream, token);

                case ("POST", "/api/fleet/adopt"):
                    return await FleetAdoptAsync(request, stream, token);

                case ("POST", "/api/fleet/broadcast"):
                    body = await _hub.BroadcastAsync(request.Body, token);

                    if (body.StartsWith("{\"ok\":true", StringComparison.Ordinal))
                        _log.Info($"fleet: sent a command to every member: {LogText.Safe(request.Body)}");

                    return await JsonAsync(stream, 200, body, token);
            }
        }

        switch (request.Method, request.Path)
        {
            // Not a hub: says so, so the page can hide the fleet controls rather
            // than show a 404 as an error.
            case ("GET", "/api/fleet"):
                return await JsonAsync(stream, 200, "{\"ok\":false,\"hub\":false}", token);

            case ("GET", "/api/state"):
                return await JsonAsync(stream, 200, await ExecuteAsync("{\"cmd\":\"state\"}"), token);

            case ("GET", "/api/events"):
                await EventsAsync(stream, token);
                return false;

            case ("GET", "/api/maps"):
                return await JsonAsync(stream, 200, MapList(), token);

            case ("GET", "/api/map.png"):
                return await MapImageAsync(request, stream, token);

            case ("GET", "/api/log"):
                return await JsonAsync(stream, 200, LogSince(request.Q("after")), token);

            // Answered on THIS thread, not through the queue: it touches the
            // OS and the runtime, never the game. It is the one route that does.
            case ("GET", "/api/system"):
                return await JsonAsync(stream, 200, SystemInfo.ToJson(_server.Options), token);

            case ("GET", "/api/perf"):
                return await JsonAsync(
                    stream,
                    200,
                    _perf.ToJson(long.TryParse(request.Q("after"), out var after) ? after : -1),
                    token);

            // ⚠️ Through the QUEUE like every other mutating request, even though
            // "config" only reads: it reads GameServer.Options, and reading that
            // from a socket thread while the tick thread is between stages is the
            // race this whole class is shaped to avoid. It is not on a timer —
            // the settings page fetches it when it is opened.
            case ("GET", "/api/config"):
                return await JsonAsync(stream, 200, await ExecuteAsync("{\"cmd\":\"config\"}"), token);

            // ⚠️ Through the QUEUE, like "config" and for the same reason: it
            // walks the analytics tables, which the tick thread writes on every
            // join and leave. It is fetched when the Analytics view is opened
            // and on a slow refresh, never on the state cadence — a walk over
            // twenty thousand player records is not a per-tick cost.
            case ("GET", "/api/analytics"):
                return await JsonAsync(stream, 200, await ExecuteAsync("{\"cmd\":\"analytics\"}"), token);

            case ("POST", "/api/command"):
                if (member && HostLocalRefusal(request.Body) is { } refusal)
                    return await JsonAsync(stream, 403, Error(refusal), token);

                return await JsonAsync(stream, 200, await ExecuteAsync(request.Body), token);

            default:
                return await JsonAsync(stream, 404, Error($"no {request.Method} {request.Path}"), token);
        }
    }

    /// <summary>
    /// The member routes a hub passes through, and only these.
    /// </summary>
    /// <remarks>
    /// An allowlist, so a route added to the panel later is not reachable through
    /// the hub until somebody decides it should be — and <c>login</c> and
    /// <c>logout</c> are deliberately absent: the hub authenticates to a member
    /// with its token, and a browser has no business with a member's sessions.
    /// </remarks>
    private static readonly HashSet<(string Method, string Path)> ProxiedRoutes =
    [
        ("GET", "/api/session"),
        ("GET", "/api/state"),
        ("GET", "/api/events"),
        ("GET", "/api/maps"),
        ("GET", "/api/map.png"),
        ("GET", "/api/log"),
        ("GET", "/api/system"),
        ("GET", "/api/perf"),
        ("GET", "/api/config"),
        ("GET", "/api/analytics"),
        ("POST", "/api/command"),
    ];

    /// <summary>The largest member reply the hub will relay, other than an event stream.</summary>
    private const int MaxProxiedBytes = 16 * 1024 * 1024;

    /// <summary>
    /// <c>/h/&lt;member&gt;/api/…</c>: the same request, sent to that member with
    /// the hub's token, and its answer relayed back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is what lets the whole dashboard work for any member with no
    /// second copy of it.</b> The page prefixes its API calls with the member's
    /// path and everything else is the member's own <see cref="ControlCommands"/>;
    /// the hub adds no verb.
    /// </para>
    /// <para>
    /// The browser's session and CSRF token were checked by the caller, against
    /// THIS panel. The member sees only the hub's bearer token.
    /// </para>
    /// </remarks>
    private async Task<bool> ProxyAsync(HttpRequest request, Stream stream, CancellationToken token)
    {
        var rest = request.Path["/h/".Length..];
        var slash = rest.IndexOf('/');
        var name = slash < 0 ? rest : rest[..slash];
        var path = slash < 0 ? "/" : rest[slash..];

        if (!ProxiedRoutes.Contains((request.Method, path)))
            return await JsonAsync(stream, 404, Error($"no {request.Method} {path} through the hub"), token);

        var query = string.Join('&', request.Query.Select(pair =>
            Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));

        var target = query.Length == 0 ? path : path + "?" + query;
        var events = path == "/api/events";

        HttpResponseMessage reply;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);

        if (!events)
            timeout.CancelAfter(FleetHub.RequestTimeout);

        try
        {
            reply = await _hub!.SendAsync(
                name,
                request.Method == "POST" ? HttpMethod.Post : HttpMethod.Get,
                target,
                request.Method == "POST" ? request.Body : null,
                timeout.Token);
        }
        catch (KeyNotFoundException)
        {
            return await JsonAsync(stream, 404, Error($"no fleet member '{name}'"), token);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException && !token.IsCancellationRequested)
        {
            return await JsonAsync(
                stream, 502, Error($"{name} did not answer: {(e is OperationCanceledException ? "timed out" : e.Message)}"), token);
        }

        using (reply)
        {
            // Never relayed as a 401: the page reads a 401 as "THIS panel's
            // session expired" and shows the sign-in card, which would send the
            // operator round in circles over a member that has been re-keyed.
            if (reply.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.TooManyRequests)
            {
                return await JsonAsync(
                    stream, 502, Error($"{name} refused the hub's token ({(int)reply.StatusCode}) — re-pair it"), token);
            }

            var contentType = reply.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";

            if (events && reply.IsSuccessStatusCode)
            {
                await HttpResponse.StartEventStreamAsync(stream, token);

                await using var upstream = await reply.Content.ReadAsStreamAsync(token);
                var buffer = new byte[16 * 1024];

                // Flushed after every read: an event held in a buffer here is an
                // event the dashboard shows late, and the member already paces
                // them. Ends when either side goes away.
                int read;

                while ((read = await upstream.ReadAsync(buffer, token)) > 0)
                {
                    await stream.WriteAsync(buffer.AsMemory(0, read), token);
                    await stream.FlushAsync(token);
                }

                return false;
            }

            if (reply.Content.Headers.ContentLength > MaxProxiedBytes)
                return await JsonAsync(stream, 502, Error($"{name}'s reply is too large to relay"), token);

            byte[] body;

            try
            {
                body = await reply.Content.ReadAsByteArrayAsync(timeout.Token);
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException && !token.IsCancellationRequested)
            {
                return await JsonAsync(stream, 502, Error($"{name} stopped answering"), token);
            }

            await HttpResponse.WriteAsync(stream, (int)reply.StatusCode, contentType, body, token, NoStore);
            return true;
        }
    }

    /// <summary>
    /// Why a hub's token may not send this command, or null if it may.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>A hub token never changes a host-local setting</b> — a path, or
    /// anything in <c>[Web]</c>. <c>config-install</c> keeps them itself; this is
    /// the same rule for <c>config-set</c>, so the one credential the hub holds
    /// for every member cannot choose where a member writes or cut the hub off
    /// from it. The member's own password still can, from its own panel.
    /// </remarks>
    internal static string? HostLocalRefusal(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("cmd", out var cmd)
                || cmd.GetString() != "config-set"
                || !root.TryGetProperty("settings", out var settings)
                || settings.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var change in settings.EnumerateObject())
            {
                if (ServerSettings.Find(change.Name) is { HostLocal: true } setting)
                {
                    return $"{setting.Id} is host-local, and a fleet hub may not change it; "
                        + "set it from this server's own panel";
                }
            }
        }
        catch (JsonException)
        {
            // Dispatch answers malformed JSON itself.
        }

        return null;
    }

    private async Task<bool> FleetAddAsync(HttpRequest request, Stream stream, CancellationToken token)
    {
        string name, url, pairing;

        try
        {
            var root = JsonDocument.Parse(request.Body).RootElement;
            name = root.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
            url = root.TryGetProperty("url", out var u) ? u.GetString() ?? string.Empty : string.Empty;
            pairing = root.TryGetProperty("pairing", out var p) ? p.GetString() ?? string.Empty : string.Empty;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return await JsonAsync(stream, 400, Error("not JSON"), token);
        }

        var error = await _hub!.AddAsync(name.Trim(), url, pairing, token);

        if (error is not null)
            return await JsonAsync(stream, 200, Error(error), token);

        _log.Info($"fleet: paired with {name.Trim()} at {url.Trim()}");
        return await JsonAsync(stream, 200, "{\"ok\":true}", token);
    }

    private async Task<bool> FleetPushAsync(HttpRequest request, Stream stream, CancellationToken token)
    {
        string name, expected;
        bool force, restart;

        try
        {
            var root = JsonDocument.Parse(request.Body).RootElement;
            name = root.GetProperty("name").GetString() ?? string.Empty;
            expected = root.TryGetProperty("expected", out var x) ? x.GetString() ?? string.Empty : string.Empty;
            force = root.TryGetProperty("force", out var f) && f.ValueKind == JsonValueKind.True;
            restart = root.TryGetProperty("restart", out var r) && r.ValueKind == JsonValueKind.True;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return await JsonAsync(stream, 400, Error("expected {name, expected, force?, restart?}"), token);
        }

        var reply = await _hub!.PushAsync(name, expected, force, restart, token);

        if (reply.Contains("\"ok\":true", StringComparison.Ordinal))
            _log.Info($"fleet: pushed the fleet settings to {name}{(force ? " (forced)" : string.Empty)}{(restart ? ", restarting it" : string.Empty)}");

        return await JsonAsync(stream, 200, reply, token);
    }

    /// <summary><c>{name, ids?, into?: "host"|"common"}</c>; no <c>ids</c> lists the differences.</summary>
    private async Task<bool> FleetAdoptAsync(HttpRequest request, Stream stream, CancellationToken token)
    {
        string name;
        List<string>? ids = null;
        bool intoCommon;

        try
        {
            var root = JsonDocument.Parse(request.Body).RootElement;
            name = root.GetProperty("name").GetString() ?? string.Empty;
            intoCommon = root.TryGetProperty("into", out var into) && into.GetString() == "common";

            if (root.TryGetProperty("ids", out var list) && list.ValueKind == JsonValueKind.Array)
                ids = list.EnumerateArray().Select(id => id.GetString() ?? string.Empty).ToList();
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return await JsonAsync(stream, 400, Error("expected {name, ids?, into?}"), token);
        }

        var reply = await _hub!.AdoptAsync(name, ids, intoCommon, token);

        if (ids is not null && reply.Contains("\"ok\":true", StringComparison.Ordinal))
            _log.Info($"fleet: adopted {ids.Count} setting(s) from {name} into {(intoCommon ? "the shared settings" : "its overrides")}");

        return await JsonAsync(stream, 200, reply, token);
    }

    private async Task<bool> FleetRemoveAsync(HttpRequest request, Stream stream, CancellationToken token)
    {
        string name;

        try
        {
            var root = JsonDocument.Parse(request.Body).RootElement;
            name = root.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return await JsonAsync(stream, 400, Error("not JSON"), token);
        }

        if (!_hub!.Remove(name))
            return await JsonAsync(stream, 200, Error($"no fleet member '{name}'"), token);

        _log.Info($"fleet: removed {name}");
        return await JsonAsync(stream, 200, "{\"ok\":true}", token);
    }

    private async Task<bool> LoginAsync(
        HttpRequest request,
        Stream stream,
        IPAddress address,
        CancellationToken token)
    {
        var remaining = _sessions.LockoutRemaining(address);

        if (remaining > TimeSpan.Zero)
        {
            return await JsonAsync(
                stream,
                429,
                Error($"too many attempts — try again in {(int)remaining.TotalSeconds + 1}s"),
                token);
        }

        string password;

        try
        {
            password = JsonDocument.Parse(request.Body).RootElement
                .TryGetProperty("password", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()!
                : string.Empty;
        }
        catch (JsonException)
        {
            return await JsonAsync(stream, 400, Error("not JSON"), token);
        }

        var session = _sessions.SignIn(address, password);

        if (session is null)
        {
            // Paid on every failure, before the answer goes out. See
            // AdminSessions.FailureDelay.
            await Task.Delay(AdminSessions.FailureDelay, token);
            _log.Warn($"web admin: failed sign-in from {address}");
            return await JsonAsync(stream, 401, Error("wrong password"), token);
        }

        _log.Info($"web admin: signed in from {address}");

        // HttpOnly so script cannot read it, SameSite=Strict so another site
        // cannot make the browser send it, and Secure only under TLS — a Secure
        // cookie on plain HTTP is simply dropped, which would make the panel
        // impossible to sign in to rather than more secure.
        var cookie =
            $"Set-Cookie: {CookieName}={session.Token}; HttpOnly; SameSite=Strict; Path=/"
            + (Secure ? "; Secure" : string.Empty) + "\r\n";

        return await JsonAsync(
            stream,
            200,
            Json("\"ok\":true", $"\"csrf\":\"{session.Csrf}\""),
            token,
            cookie);
    }

    /// <summary>
    /// The live stream the dashboard runs on: state snapshots and log lines.
    /// </summary>
    /// <remarks>
    /// <b>Server-sent events rather than WebSockets</b>, because everything
    /// here flows one way and SSE needs no handshake, no framing and no ping
    /// protocol — about forty lines against several hundred, over a transport
    /// that reconnects itself.
    /// </remarks>
    private async Task EventsAsync(Stream stream, CancellationToken token)
    {
        Interlocked.Increment(ref _watchers);

        try
        {
            await HttpResponse.StartEventStreamAsync(stream, token);

            var seenVersion = -1L;
            var seenLog = -1L;
            var seenPerf = -1L;
            var seenChat = -1L;
            var idle = 0;

            while (!token.IsCancellationRequested)
            {
                var version = Interlocked.Read(ref _snapshotVersion);

                // `> 0` as well as "changed": zero means the tick thread has
                // not published yet, and the placeholder is not a state object.
                if (version != seenVersion && version > 0)
                {
                    seenVersion = version;
                    await SendEventAsync(stream, "state", Volatile.Read(ref _snapshot), token);
                    idle = 0;
                }

                // `> 0 &&`, and the sequence is read ONCE. Without the first
                // guard a ring with nothing in it compares unequal to the -1 this
                // starts at on every pass, resets `idle` every 100 ms, and the
                // keep-alive below never fires.
                var logSequence = _logRing.Sequence;

                if (logSequence > 0 && logSequence - 1 != seenLog)
                {
                    var lines = _logRing.Since(seenLog, out _);
                    seenLog = logSequence - 1;

                    if (lines.Count > 0)
                    {
                        await SendEventAsync(stream, "log", LogJson(lines), token);
                        idle = 0;
                    }
                }

                // Chat, with its backlog on the first pass like the log's.
                if (_server.ChatHistory is { } chat)
                {
                    var chatSequence = chat.Sequence;

                    if (chatSequence > 0 && chatSequence - 1 != seenChat)
                    {
                        var lines = chat.Since(seenChat);
                        seenChat = chatSequence - 1;

                        if (lines.Count > 0)
                        {
                            await SendEventAsync(stream, "chat", ChatJson(lines), token);
                            idle = 0;
                        }
                    }
                }

                // The backlog goes out on the FIRST pass too — seenPerf starts
                // at -1 — so a browser that has just connected is handed the last
                // five minutes in one event and draws a full graph immediately
                // rather than an empty one that fills in over five minutes.
                var perfSequence = _perf.Sequence;

                if (perfSequence > 0 && perfSequence - 1 != seenPerf)
                {
                    // A sample can land between these two lines, in which case
                    // it is sent again on the next pass. The panel keys on `seq`
                    // and ignores one it already has — a duplicate is cheaper
                    // than the lock this would otherwise need on the tick
                    // thread's own writer.
                    var samples = _perf.ToJson(seenPerf);
                    seenPerf = perfSequence - 1;

                    await SendEventAsync(stream, "perf", samples, token);
                    idle = 0;
                }

                // A comment line every ~15s. Without it a proxy or a NAT with
                // an idle timeout drops a quiet stream and the panel's only
                // symptom is that it silently stops updating.
                if (++idle >= 150)
                {
                    idle = 0;
                    await stream.WriteAsync(": keep-alive\n\n"u8.ToArray(), token);
                    await stream.FlushAsync(token);
                }

                await Task.Delay(100, token);
            }
        }
        catch (Exception e) when (e is IOException
                                      or SocketException
                                      or ObjectDisposedException
                                      or OperationCanceledException)
        {
            // The tab was closed. Normal.
        }
        finally
        {
            Interlocked.Decrement(ref _watchers);
        }
    }

    private static async Task SendEventAsync(
        Stream stream,
        string name,
        string json,
        CancellationToken token)
    {
        // The payload is one line of JSON with no newlines in it — every
        // string in it went through JsonEncodedText — so it needs no folding into
        // several `data:` lines. A raw newline here would silently truncate the
        // event, which is why every producer of these strings escapes.
        var bytes = Encoding.UTF8.GetBytes($"event: {name}\ndata: {json}\n\n");

        await stream.WriteAsync(bytes, token);
        await stream.FlushAsync(token);
    }

    /// <summary>
    /// Queues a command for the tick thread and waits for its answer.
    /// </summary>
    /// <remarks>
    /// <b>The seam.</b> This is the only way anything on a socket thread
    /// reaches the game, and it is why nothing in this class needs a lock.
    /// </remarks>
    private async Task<string> ExecuteAsync(string body)
    {
        if (Interlocked.Increment(ref _queued) > MaxQueuedCommands)
        {
            Interlocked.Decrement(ref _queued);
            return Error("the server is not draining commands — is it still ticking?");
        }

        var pending = new Pending { Body = body };
        _queue.Enqueue(pending);

        var completed = await Task.WhenAny(pending.Result.Task, Task.Delay(CommandTimeout));

        if (completed != pending.Result.Task)
        {
            return Error(
                $"timed out after {CommandTimeout.TotalSeconds:0}s waiting for the tick thread. "
                + "The server may be stopped, or wedged.");
        }

        return await pending.Result.Task;
    }

    private async Task<bool> AssetAsync(
        Stream stream,
        string name,
        string contentType,
        CancellationToken token)
    {
        var bytes = WebAssets.Read(name);

        // no-store rather than a long cache: the page is a few kilobytes off
        // localhost or a LAN, and an operator who upgraded the server and got the
        // old panel out of a browser cache would have no way to tell.
        await HttpResponse.WriteAsync(stream, 200, contentType, bytes, token, NoStore);
        return true;
    }

    private async Task<bool> MapImageAsync(HttpRequest request, Stream stream, CancellationToken token)
    {
        var name = request.Q("name") ?? string.Empty;

        // An allowlist of characters, not a check for "..". This path is
        // joined to a directory and handed to the filesystem; a map name in this
        // game is [a-z0-9_] and anything else is somebody trying.
        if (name.Length is 0 or > 64 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            return await JsonAsync(stream, 400, Error("not a map name"), token);

        var path = Path.Combine(_server.Options.MapDirectory, name + ".png");

        if (!File.Exists(path))
            return await JsonAsync(stream, 404, Error($"no map '{name}'"), token);

        // Read on this thread, not the tick thread. The map directory is
        // read-only to the running server and the file is a few tens of KB.
        var bytes = await File.ReadAllBytesAsync(path, token);

        // Immutable for the life of the process: a map PNG on disk does not
        // change under a running server, and the panel redraws it on every map
        // change.
        await HttpResponse.WriteAsync(
            stream, 200, "image/png", bytes, token, "Cache-Control: private, max-age=3600\r\n");

        return true;
    }

    /// <summary>The maps this server could be asked to change to.</summary>
    /// <remarks>
    /// Read off disk rather than from a compiled-in list, so a server started
    /// with <c>--maps</c> pointing somewhere else offers what it can actually
    /// load. The rotation is listed separately so the panel can mark it.
    /// </remarks>
    private string MapList()
    {
        var json = new StringBuilder(1024);

        json.Append("{\"ok\":true,\"maps\":[");

        try
        {
            var first = true;

            foreach (var file in Directory.EnumerateFiles(_server.Options.MapDirectory, "*.png")
                         .Select(Path.GetFileNameWithoutExtension)
                         .Where(name => name is not null)
                         .OrderBy(name => name, StringComparer.Ordinal))
            {
                json.Append(first ? "\"" : ",\"").Append(ControlCommands.Escape(file!)).Append('"');
                first = false;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // An empty list and a working panel beats a 500 and a blank page.
        }

        json.Append("],\"rotation\":[");

        for (var i = 0; i < _server.Options.MapRotation.Count; i++)
        {
            json.Append(i == 0 ? "\"" : ",\"")
                .Append(ControlCommands.Escape(_server.Options.MapRotation[i])).Append('"');
        }

        json.Append("]}");
        return json.ToString();
    }

    private string LogSince(string? after)
    {
        var from = long.TryParse(after, out var parsed) ? parsed : -1;
        var lines = _logRing.Since(from, out var dropped);

        return "{\"ok\":true,\"dropped\":" + dropped + ",\"lines\":" + LogJson(lines) + "}";
    }

    private static string LogJson(IReadOnlyList<LogRing.Entry> lines)
    {
        var json = new StringBuilder(lines.Count * 96 + 2);

        json.Append('[');

        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0)
                json.Append(',');

            json.Append("{\"seq\":").Append(lines[i].Sequence);
            json.Append(",\"at\":\"").Append(lines[i].Utc.ToString("O")).Append('"');
            json.Append(",\"text\":\"").Append(ControlCommands.Escape(lines[i].Text)).Append("\"}");
        }

        json.Append(']');
        return json.ToString();
    }

    private static string ChatJson(IReadOnlyList<ChatHistory.Entry> lines)
    {
        var json = new StringBuilder(lines.Count * 96 + 2);

        json.Append('[');

        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0)
                json.Append(',');

            json.Append("{\"seq\":").Append(lines[i].Sequence);
            json.Append(",\"at\":\"").Append(lines[i].Utc.ToString("O")).Append('"');
            json.Append(",\"name\":\"").Append(ControlCommands.Escape(lines[i].Name)).Append('"');
            json.Append(",\"team\":\"").Append(lines[i].Team).Append('"');
            json.Append(",\"teamOnly\":").Append(lines[i].TeamOnly ? "true" : "false");
            json.Append(",\"text\":\"").Append(ControlCommands.Escape(lines[i].Text)).Append("\"}");
        }

        json.Append(']');
        return json.ToString();
    }

    private const string CookieName = "gg2_admin";

    private const string NoStore = "Cache-Control: no-store\r\n";

    private const string ClearCookie =
        NoStore + $"Set-Cookie: {CookieName}=; HttpOnly; SameSite=Strict; Path=/; Max-Age=0\r\n";

    private static string Json(params string[] fields) => "{" + string.Join(',', fields) + "}";

    private static string Error(string message) => ControlCommands.Error(message);

    private static async Task<bool> JsonAsync(
        Stream stream,
        int status,
        string body,
        CancellationToken token,
        string extra = NoStore)
    {
        await HttpResponse.TextAsync(stream, status, "application/json", body, token, extra);
        return true;
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _listener?.Stop();
        _listener?.Dispose();
        _listener = null;
        _certificate?.Dispose();
        _certificate = null;
        _hub?.Dispose();
        _stopping.Dispose();
    }
}
