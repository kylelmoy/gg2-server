using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Gg2.Server;

/// <summary>
/// <c>gg2-server config edit</c>: a page for writing the configuration
/// <c>scripts/deploy.sh --config</c> pushes. Runs on the operator's own machine,
/// never on a server.
/// </summary>
/// <remarks>
/// <para>
/// <b>Loopback only, with no option to change that.</b> What it edits is a
/// directory of files holding every server's admin password; the machine that
/// holds that directory is the only place the page belongs.
/// </para>
/// <para>
/// ⛔ <b>A per-run token, in the URL's fragment, is required on every API
/// call</b>, and the <c>Host</c> header must name loopback. Binding to
/// <c>127.0.0.1</c> keeps the network out, but not the other tabs in the
/// operator's own browser: without a token any web page could post to
/// <c>http://127.0.0.1:port/api/save</c>, and without the <c>Host</c> check a
/// DNS-rebinding page could also read the replies. The fragment is never sent in
/// a request, so the token does not reach a log or a <c>Referer</c>.
/// </para>
/// <para>
/// <b>No tick thread and no game</b>, so none of <see cref="WebAdminServer"/>'s
/// threading rule applies: connections are served concurrently and the API
/// calls take one lock, around the files.
/// <see cref="FleetConfigEditor"/> is the logic.
/// </para>
/// </remarks>
public sealed class ConfigEditorServer : IDisposable
{
    private readonly FleetConfigEditor _editor;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _files = new(1, 1);

    public ConfigEditorServer(FleetConfigEditor editor, int port)
    {
        _editor = editor;
        _listener = new TcpListener(IPAddress.Loopback, port);
        Token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
    }

    /// <summary>The secret every API request must carry as <c>X-Editor-Token</c>.</summary>
    public string Token { get; }

    public int BoundPort { get; private set; }

    /// <summary>The address to open, token included.</summary>
    public string Url => $"http://127.0.0.1:{BoundPort}/#{Token}";

    public void Start()
    {
        _listener.Start();
        BoundPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    /// <summary>Serves until <paramref name="token"/> is cancelled.</summary>
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

            // Concurrently: a browser opens several connections for one page load,
            // and serving them in turn would stall each behind the last one's idle
            // timeout. The files are guarded by _files instead.
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
                        // A tab closed mid-request.
                    }
                }
            }, CancellationToken.None);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken token)
    {
        await using var stream = client.GetStream();

        for (var served = 0; served < 100; served++)
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

            await HandleAsync(request, stream, token);

            if (!request.KeepAlive)
                return;
        }
    }

    private async Task HandleAsync(HttpRequest request, Stream stream, CancellationToken token)
    {
        if (!IsLoopbackHost(request.Header("Host")))
        {
            await JsonAsync(stream, 403, Error("wrong Host header"), token);
            return;
        }

        switch (request.Method, request.Path)
        {
            case ("GET", "/"):
                await AssetAsync(stream, "editor.html", "text/html; charset=utf-8", token);
                return;
            case ("GET", "/editor.js"):
                await AssetAsync(stream, "editor.js", "text/javascript; charset=utf-8", token);
                return;
            case ("GET", "/editor.css"):
                await AssetAsync(stream, "editor.css", "text/css; charset=utf-8", token);
                return;
            case ("GET", "/app.css"):
                await AssetAsync(stream, "app.css", "text/css; charset=utf-8", token);
                return;
            case ("GET", "/favicon.svg"):
                await AssetAsync(stream, "favicon.svg", "image/svg+xml", token);
                return;
        }

        // Constant time, like every other secret comparison in this project.
        var presented = request.Header("X-Editor-Token") ?? string.Empty;

        if (!CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(presented), System.Text.Encoding.UTF8.GetBytes(Token)))
        {
            await JsonAsync(stream, 401, Error("missing or wrong token - open the address the command printed"), token);
            return;
        }

        // One reader or writer of the config directory at a time. A save reads
        // every file, validates, then writes; a model read in the middle of that
        // would draw half of it.
        await _files.WaitAsync(token);

        try
        {
            await ApiAsync(request, stream, token);
        }
        finally
        {
            _files.Release();
        }
    }

    private async Task ApiAsync(HttpRequest request, Stream stream, CancellationToken token)
    {
        switch (request.Method, request.Path)
        {
            case ("GET", "/api/model"):
                await JsonAsync(stream, 200, _editor.Model(), token);
                return;
            case ("GET", "/api/preview"):
                await JsonAsync(stream, 200, _editor.Preview(request.Q("host") ?? string.Empty), token);
                return;
            case ("POST", "/api/save"):
                await JsonAsync(stream, 200, _editor.Save(request.Body), token);
                return;
            default:
                await JsonAsync(stream, 404, Error($"no {request.Method} {request.Path}"), token);
                return;
        }
    }

    private bool IsLoopbackHost(string? host) =>
        host is not null
        && (string.Equals(host, $"127.0.0.1:{BoundPort}", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, $"localhost:{BoundPort}", StringComparison.OrdinalIgnoreCase));

    private static Task AssetAsync(Stream stream, string name, string contentType, CancellationToken token) =>
        HttpResponse.WriteAsync(stream, 200, contentType, WebAssets.Read(name), token, NoStore);

    private static Task JsonAsync(Stream stream, int status, string body, CancellationToken token) =>
        HttpResponse.TextAsync(stream, status, "application/json", body, token, NoStore);

    private static string Error(string message) => ControlCommands.Error(message);

    private const string NoStore = "Cache-Control: no-store\r\n";

    public void Dispose()
    {
        _stopping.Cancel();
        _listener.Stop();
        _listener.Dispose();
    }
}
