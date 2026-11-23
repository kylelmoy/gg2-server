using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace GG2.Lobby;

internal sealed record LobbyOptions
{
    public IPAddress Bind { get; init; } = IPAddress.IPv6Any;
    public int Port { get; init; } = 29945;
    public TimeSpan Ttl { get; init; } = TimeSpan.FromSeconds(70);
    public bool Probe { get; init; } = true;
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(3);
    public int MaxConcurrentProbes { get; init; } = 64;
    public int MaxServers { get; init; } = 4096;
    public int MaxPerAddress { get; init; } = 64;
    public int MaxConnections { get; init; } = 4096;
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Milliseconds on a monotonic clock. Replaced by tests that need to expire entries.</summary>
    public Func<long> Clock { get; init; } = static () => Environment.TickCount64;
}

/// <summary>
/// The lobby's HTTP front: a raw socket accept loop and the four routes.
/// </summary>
/// <remarks>
/// <b>No Kestrel, no HttpListener, on purpose.</b> The whole surface is four
/// routes with bodies of a few hundred bytes, and the hot one — <c>GET
/// /servers</c> — is answered with a single send of a byte array the
/// <see cref="Registry"/> built when the list last changed. A connection costs
/// one pooled 8 KB buffer and one async state machine; there is no per-request
/// allocation on the list path at all. docs/LOBBY.md has the routes.
/// </remarks>
internal sealed class LobbyServer : IDisposable
{
    private const int BufferSize = 8192;
    private const int MaxHeader = 4096;
    private const int MaxBody = 4096;

    private static readonly byte[] Index = Simple(200, "OK", "text/plain",
        "gg2-lobby\n\nGET /servers\nPUT /servers/<id>\nDELETE /servers/<id>\n");
    private static readonly byte[] NotFound = Simple(404, "Not Found", "application/json", "{\"error\":\"no such route\"}");
    private static readonly byte[] NotAllowed = Simple(405, "Method Not Allowed", "application/json", "{\"error\":\"method not allowed\"}");
    private static readonly byte[] BadRequest = Simple(400, "Bad Request", "application/json", "{\"error\":\"malformed request\"}");
    private static readonly byte[] BadId = Simple(400, "Bad Request", "application/json", "{\"error\":\"id is 32 hex digits\"}");
    private static readonly byte[] TooLarge = Simple(413, "Content Too Large", "application/json", "{\"error\":\"request too large\"}");
    private static readonly byte[] LengthRequired = Simple(411, "Length Required", "application/json", "{\"error\":\"send a Content-Length, not chunks\"}");
    private static readonly byte[] Unreachable = Simple(422, "Unprocessable Content", "application/json", "{\"error\":\"unreachable\"}");
    private static readonly byte[] Full = Simple(503, "Service Unavailable", "application/json", "{\"error\":\"lobby full\"}");
    private static readonly byte[] Busy = Simple(503, "Service Unavailable", "application/json", "{\"error\":\"busy, retry\"}");
    private static readonly byte[] Withdrawn = Simple(204, "No Content", null, null);

    private readonly LobbyOptions _options;
    private readonly Registry _registry;
    private readonly Socket _listener;
    private readonly SemaphoreSlim _probes;
    private readonly byte[] _listed;
    private int _connections;

    public LobbyServer(LobbyOptions options, Action<string> log)
    {
        _options = options;
        _registry = new Registry(options.Ttl, options.MaxServers, options.MaxPerAddress, log);
        _probes = new SemaphoreSlim(options.MaxConcurrentProbes);
        _listed = Simple(200, "OK", "application/json", $"{{\"ttl\":{_registry.TtlSeconds}}}");

        _listener = new Socket(options.Bind.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        if (options.Bind.AddressFamily == AddressFamily.InterNetworkV6)
            _listener.DualMode = true;
        _listener.NoDelay = true;
        _listener.Bind(new IPEndPoint(options.Bind, options.Port));
        _listener.Listen(1024);

        _ = AcceptLoopAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndPoint!).Port;

    internal Registry Registry => _registry;

    public void Dispose() => _listener.Dispose();

    private async Task AcceptLoopAsync()
    {
        while (true)
        {
            Socket socket;
            try
            {
                socket = await _listener.AcceptAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                // An aborted handshake surfaces here on some platforms; it is the
                // client's problem, not the listener's.
                continue;
            }

            if (Interlocked.Increment(ref _connections) > _options.MaxConnections)
            {
                Interlocked.Decrement(ref _connections);
                socket.Dispose();
                continue;
            }

            socket.NoDelay = true;
            _ = ServeAsync(socket);
        }
    }

    private async Task ServeAsync(Socket socket)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        using var timeout = new CancellationTokenSource();

        try
        {
            var remote = ((IPEndPoint)socket.RemoteEndPoint!).Address;
            if (remote.IsIPv4MappedToIPv6)
                remote = remote.MapToIPv4();

            var filled = 0;
            while (true)
            {
                var consumed = 0;
                while (true)
                {
                    var status = LobbyRequest.TryParse(buffer.AsSpan(consumed, filled - consumed), MaxHeader, out var request);

                    if (status == ParseStatus.Incomplete)
                        break;

                    if (status != ParseStatus.Complete)
                    {
                        await SendAsync(socket, status == ParseStatus.TooLarge ? TooLarge : BadRequest, timeout).ConfigureAwait(false);
                        return;
                    }

                    if (request.Chunked)
                    {
                        await SendAsync(socket, LengthRequired, timeout).ConfigureAwait(false);
                        return;
                    }

                    if (request.ContentLength > MaxBody)
                    {
                        await SendAsync(socket, TooLarge, timeout).ConfigureAwait(false);
                        return;
                    }

                    if (filled - consumed < request.TotalLength)
                        break;

                    var response = Route(buffer.AsSpan(consumed, request.TotalLength), request, remote, out var announce);
                    if (announce is { } pending)
                        response = await AnnounceAsync(pending).ConfigureAwait(false);

                    await SendAsync(socket, response, timeout).ConfigureAwait(false);

                    consumed += request.TotalLength;
                    if (!request.KeepAlive)
                        return;
                }

                if (consumed > 0)
                {
                    Buffer.BlockCopy(buffer, consumed, buffer, 0, filled - consumed);
                    filled -= consumed;
                }

                timeout.CancelAfter(_options.IdleTimeout);
                var read = await socket.ReceiveAsync(buffer.AsMemory(filled), SocketFlags.None, timeout.Token).ConfigureAwait(false);
                if (read == 0 || !timeout.TryReset())
                    return;
                filled += read;
            }
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // Idle, reset or shut down: the connection just ends.
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            socket.Dispose();
            Interlocked.Decrement(ref _connections);
        }
    }

    private readonly record struct PendingAnnounce(UInt128 Id, IPAddress Address, Endpoint Endpoint, byte[] Fragment, string Label);

    /// <summary>
    /// Everything that can be answered without waiting, answered; an announce
    /// that needs a reachability probe is handed back as <paramref name="announce"/>.
    /// </summary>
    /// <remarks>
    /// Synchronous so the request can be read straight out of the receive
    /// buffer as a span — the body is parsed and re-serialised here, before the
    /// buffer can move under it.
    /// </remarks>
    private byte[] Route(ReadOnlySpan<byte> raw, in LobbyRequest request, IPAddress remote, out PendingAnnounce? announce)
    {
        announce = null;

        var path = raw.Slice(request.PathStart, request.PathLength);
        var query = path.IndexOf((byte)'?');
        if (query >= 0)
            path = path[..query];

        if (path.SequenceEqual("/servers"u8))
        {
            if (request.Method != RequestMethod.Get)
                return NotAllowed;

            var snapshot = _registry.GetSnapshot(_options.Clock());

            if (request.IfNoneMatchLength > 0
                && raw.Slice(request.IfNoneMatchStart, request.IfNoneMatchLength).IndexOf(snapshot.ETag) >= 0)
                return snapshot.NotModified;

            return request.AcceptsGzip ? snapshot.Gzip : snapshot.Plain;
        }

        if (path.StartsWith("/servers/"u8))
        {
            if (!TryParseId(path[9..], out var id))
                return BadId;

            if (request.Method == RequestMethod.Delete)
            {
                _registry.Remove(id);
                return Withdrawn;
            }

            if (request.Method != RequestMethod.Put)
                return NotAllowed;

            var ip = remote.ToString();
            var body = raw[request.HeaderLength..];
            if (!Announcement.TryBuild(body, ip, out var port, out var fragment, out var name, out var error))
                return Simple(400, "Bad Request", "application/json", JsonError(error));

            var endpoint = new Endpoint(Endpoint.Key(remote), port);
            var label = string.Create(CultureInfo.InvariantCulture, $"{(remote.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{ip}]" : ip)}:{port} \"{name}\"");

            if (_options.Probe && _registry.NeedsProbe(id, endpoint, _options.Clock()))
            {
                announce = new PendingAnnounce(id, remote, endpoint, fragment, label);
                return [];
            }

            return Commit(id, endpoint, fragment, label);
        }

        if (path.SequenceEqual("/"u8))
            return request.Method == RequestMethod.Get ? Index : NotAllowed;

        return NotFound;
    }

    /// <summary>
    /// Connects back to the announced port before listing it, as Faucet-Lobby does.
    /// </summary>
    /// <remarks>
    /// The announce arrived over TCP, so its source address cannot be spoofed —
    /// what this catches is the other thing a list full of dead entries is made
    /// of: a server whose port is not forwarded. The probe goes to the address
    /// the request came from and nowhere else, so it cannot be aimed at a third
    /// party.
    /// </remarks>
    private async Task<byte[]> AnnounceAsync(PendingAnnounce pending)
    {
        if (!_probes.Wait(0))
            return Busy;

        try
        {
            using var probe = new Socket(pending.Address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            using var deadline = new CancellationTokenSource(_options.ProbeTimeout);
            try
            {
                await probe.ConnectAsync(pending.Address, pending.Endpoint.Port, deadline.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException)
            {
                return Unreachable;
            }
        }
        finally
        {
            _probes.Release();
        }

        return Commit(pending.Id, pending.Endpoint, pending.Fragment, pending.Label);
    }

    private byte[] Commit(UInt128 id, Endpoint endpoint, byte[] fragment, string label) =>
        _registry.Upsert(id, endpoint, fragment, label, _options.Clock()) == UpsertResult.Listed ? _listed : Full;

    private async ValueTask SendAsync(Socket socket, byte[] response, CancellationTokenSource timeout)
    {
        timeout.CancelAfter(_options.IdleTimeout);

        var sent = 0;
        while (sent < response.Length)
            sent += await socket.SendAsync(response.AsMemory(sent), SocketFlags.None, timeout.Token).ConfigureAwait(false);

        if (!timeout.TryReset())
            throw new OperationCanceledException();
    }

    internal static bool TryParseId(ReadOnlySpan<byte> hex, out UInt128 id)
    {
        id = 0;
        if (hex.Length != 32)
            return false;

        foreach (var c in hex)
        {
            var digit = c switch
            {
                >= (byte)'0' and <= (byte)'9' => c - '0',
                >= (byte)'a' and <= (byte)'f' => c - 'a' + 10,
                >= (byte)'A' and <= (byte)'F' => c - 'A' + 10,
                _ => -1,
            };
            if (digit < 0)
                return false;
            id = (id << 4) | (uint)digit;
        }

        return true;
    }

    private static string JsonError(string message)
    {
        var buffer = new ArrayBufferWriter<byte>(64);
        using (var writer = new System.Text.Json.Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("error"u8, message);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static byte[] Simple(int code, string reason, string? contentType, string? body)
    {
        var bodyBytes = body is null ? [] : Encoding.UTF8.GetBytes(body);
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {code} {reason}\r\n" +
            (contentType is null ? string.Empty : $"Content-Type: {contentType}\r\n") +
            (code == 204 ? string.Empty : $"Content-Length: {bodyBytes.Length}\r\n") +
            "Access-Control-Allow-Origin: *\r\n\r\n");

        var response = new byte[head.Length + bodyBytes.Length];
        head.CopyTo(response, 0);
        bodyBytes.CopyTo(response, head.Length);
        return response;
    }
}
