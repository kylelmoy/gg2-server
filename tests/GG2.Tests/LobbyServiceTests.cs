using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using GG2.Lobby;
using GG2.Protocol;
using GG2.Server;

namespace GG2.Tests;

/// <summary>
/// The gg2-lobby service (<c>src/GG2.Lobby</c>) and the server's
/// <see cref="HttpLobbyAnnouncer"/>, against each other on loopback.
/// </summary>
/// <remarks>
/// Unlike <see cref="LobbyOptInTests"/>, these are allowed a real socket: both
/// ends are this repo's own code on 127.0.0.1, so nothing leaves the machine.
/// </remarks>
public sealed class LobbyServiceTests
{
    private static readonly Action<string> Quiet = static _ => { };

    // --- Announcement -------------------------------------------------------

    [Fact]
    public void AnAnnounceIsReserialisedWithTheSourceAddressNotTheClaimedOne()
    {
        var body = """{"ip":"6.6.6.6","port":8190,"name":"Bacon <Town>","map":"ctf_2dfort","slots":16,"players":3,"bots":7,"passworded":true,"future":{"x":[1,2]},"protocols":["a","b"],"info":{"x-respawn":"5"}}"""u8;

        Assert.True(Announcement.TryBuild(body, "1.2.3.4", out var port, out var fragment, out var name, out var error), error);

        Assert.Equal(8190, port);
        Assert.Equal("Bacon <Town>", name);

        using var json = JsonDocument.Parse(fragment);
        var root = json.RootElement;
        Assert.Equal("1.2.3.4", root.GetProperty("ip").GetString());
        Assert.Equal("Bacon <Town>", root.GetProperty("name").GetString());
        Assert.Equal(7, root.GetProperty("bots").GetInt32());
        Assert.True(root.GetProperty("passworded").GetBoolean());
        Assert.Equal(["a", "b"], root.GetProperty("protocols").EnumerateArray().Select(p => p.GetString()));
        Assert.Equal("5", root.GetProperty("info").GetProperty("x-respawn").GetString());
        Assert.False(root.TryGetProperty("future", out _));

        // Re-serialised, so markup cannot ride through into a web page verbatim.
        Assert.DoesNotContain("<", Encoding.UTF8.GetString(fragment), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"name":"x"}""")]
    [InlineData("""{"port":8190}""")]
    [InlineData("""{"port":0,"name":"x"}""")]
    [InlineData("""{"port":70000,"name":"x"}""")]
    [InlineData("""{"port":"8190","name":"x"}""")]
    [InlineData("""{"port":8190,"name":""}""")]
    [InlineData("""{"port":8190,"name":"x","protocols":[1]}""")]
    [InlineData("""[1,2]""")]
    [InlineData("""{"port":8190,"name":"x" """)]
    [InlineData("""not json""")]
    public void AMalformedAnnounceIsRefused(string body) =>
        Assert.False(Announcement.TryBuild(Encoding.UTF8.GetBytes(body), "1.2.3.4", out _, out _, out _, out _));

    [Fact]
    public void AnOversizedNameIsRefused()
    {
        var body = Encoding.UTF8.GetBytes($$"""{"port":8190,"name":"{{new string('n', Announcement.MaxNameLength + 1)}}"}""");
        Assert.False(Announcement.TryBuild(body, "1.2.3.4", out _, out _, out _, out _));
    }

    // --- LobbyRequest --------------------------------------------------------

    [Fact]
    public void PipelinedRequestsParseOneAtATime()
    {
        var raw = "GET /servers HTTP/1.1\r\nHost: x\r\nAccept-Encoding: br, GZIP\r\n\r\nPUT /servers/ab HTTP/1.1\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}"u8;

        Assert.Equal(ParseStatus.Complete, LobbyRequest.TryParse(raw, 4096, out var first));
        Assert.Equal(RequestMethod.Get, first.Method);
        Assert.True(first.AcceptsGzip);
        Assert.True(first.KeepAlive);
        Assert.Equal("/servers", Encoding.ASCII.GetString(raw.Slice(first.PathStart, first.PathLength)));

        var rest = raw[first.TotalLength..];
        Assert.Equal(ParseStatus.Complete, LobbyRequest.TryParse(rest, 4096, out var second));
        Assert.Equal(RequestMethod.Put, second.Method);
        Assert.Equal(2, second.ContentLength);
        Assert.False(second.KeepAlive);
        Assert.Equal(rest.Length, second.TotalLength);
    }

    [Fact]
    public void AnUnfinishedHeadIsIncompleteAndAnEndlessOneIsTooLarge()
    {
        Assert.Equal(ParseStatus.Incomplete, LobbyRequest.TryParse("GET / HTTP/1.1\r\nHost:"u8, 4096, out _));
        Assert.Equal(ParseStatus.TooLarge, LobbyRequest.TryParse(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nX: " + new string('a', 5000)), 4096, out _));
        Assert.Equal(ParseStatus.Bad, LobbyRequest.TryParse("GET / HTTP/2\r\n\r\n"u8, 4096, out _));
        Assert.Equal(ParseStatus.Bad, LobbyRequest.TryParse("GET / HTTP/1.1\r\nContent-Length: -1\r\n\r\n"u8, 4096, out _));
    }

    // --- Registry -----------------------------------------------------------

    private static Endpoint At(string ip, ushort port) => new(Endpoint.Key(IPAddress.Parse(ip)), port);

    private static byte[] Fragment(string name) => Encoding.UTF8.GetBytes($$"""{"name":"{{name}}"}""");

    [Fact]
    public void AnUnchangedReannounceReusesTheCachedResponse()
    {
        var registry = new Registry(TimeSpan.FromSeconds(70), 100, 10, Quiet);
        registry.Upsert(1, At("1.2.3.4", 8190), Fragment("a"), "a", now: 0);
        var before = registry.GetSnapshot(0);

        registry.Upsert(1, At("1.2.3.4", 8190), Fragment("a"), "a", now: 30_000);
        Assert.Same(before, registry.GetSnapshot(30_000));

        registry.Upsert(1, At("1.2.3.4", 8190), Fragment("b"), "b", now: 31_000);
        var after = registry.GetSnapshot(31_000);
        Assert.NotSame(before, after);
        Assert.Contains("\"b\"", Encoding.UTF8.GetString(after.Plain), StringComparison.Ordinal);
    }

    [Fact]
    public void AnEntryExpiresAfterTheTtlAndARefreshKeepsIt()
    {
        var registry = new Registry(TimeSpan.FromSeconds(70), 100, 10, Quiet);
        registry.Upsert(1, At("1.2.3.4", 8190), Fragment("kept"), "kept", now: 0);
        registry.Upsert(2, At("1.2.3.4", 8191), Fragment("lost"), "lost", now: 0);

        registry.Upsert(1, At("1.2.3.4", 8190), Fragment("kept"), "kept", now: 60_000);

        var snapshot = registry.GetSnapshot(70_000);
        Assert.Equal(1, snapshot.Count);
        Assert.Contains("kept", Encoding.UTF8.GetString(snapshot.Plain), StringComparison.Ordinal);

        Assert.Equal(0, registry.GetSnapshot(130_000).Count);
    }

    [Fact]
    public void ANewIdOnAHeldEndpointReplacesTheOldEntry()
    {
        var registry = new Registry(TimeSpan.FromSeconds(70), 100, 10, Quiet);
        registry.Upsert(1, At("1.2.3.4", 8190), Fragment("old"), "old", now: 0);

        Assert.True(registry.NeedsProbe(2, At("1.2.3.4", 8190), 1));
        registry.Upsert(2, At("1.2.3.4", 8190), Fragment("new"), "new", now: 1);

        var body = Encoding.UTF8.GetString(registry.GetSnapshot(1).Plain);
        Assert.Contains("new", body, StringComparison.Ordinal);
        Assert.DoesNotContain("old", body, StringComparison.Ordinal);
        Assert.False(registry.Remove(1));
    }

    [Fact]
    public void OneAddressCannotFillTheLobby()
    {
        var registry = new Registry(TimeSpan.FromSeconds(70), 100, 2, Quiet);
        Assert.Equal(UpsertResult.Listed, registry.Upsert(1, At("1.2.3.4", 1), Fragment("a"), "a", 0));
        Assert.Equal(UpsertResult.Listed, registry.Upsert(2, At("::ffff:1.2.3.4", 2), Fragment("b"), "b", 0));
        Assert.Equal(UpsertResult.Full, registry.Upsert(3, At("1.2.3.4", 3), Fragment("c"), "c", 0));
        Assert.Equal(UpsertResult.Listed, registry.Upsert(4, At("5.6.7.8", 3), Fragment("d"), "d", 0));

        // Withdrawing one frees its address's slot.
        registry.Remove(1);
        Assert.Equal(UpsertResult.Listed, registry.Upsert(3, At("1.2.3.4", 3), Fragment("c"), "c", 0));
    }

    [Fact]
    public void AnEmptyListIsValidJson()
    {
        var registry = new Registry(TimeSpan.FromSeconds(70), 100, 10, Quiet);
        var plain = registry.GetSnapshot(0).Plain;
        var body = Encoding.UTF8.GetString(plain)[(Encoding.UTF8.GetString(plain).IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..];
        Assert.Equal("""{"servers":[]}""", body);
    }

    // --- End to end ---------------------------------------------------------

    private sealed class ConcurrentLog : IServerLog
    {
        public ConcurrentQueue<string> Lines { get; } = new();

        public void Info(string message) => Lines.Enqueue(message);

        public void Warn(string message) => Lines.Enqueue("[warn] " + message);

        public bool Has(string fragment) => Lines.Any(l => l.Contains(fragment, StringComparison.Ordinal));
    }

    private static LobbyServerInfo Info(int port, ushort players = 3) => new()
    {
        ServerId = ReadOnlyMemory<byte>.Empty,
        ServerName = "Loopback Fort",
        Map = "ctf_truefort",
        Port = checked((ushort)port),
        PlayerLimit = 16,
        OccupiedSlots = players,
        Bots = 7,
        Passworded = false,
    };

    private static async Task<JsonElement[]> ListAsync(HttpClient http, int lobbyPort)
    {
        var json = await http.GetStringAsync($"http://127.0.0.1:{lobbyPort}/servers");
        using var doc = JsonDocument.Parse(json);
        return [.. doc.RootElement.GetProperty("servers").EnumerateArray().Select(e => e.Clone())];
    }

    private static async Task<JsonElement[]> WaitForAsync(HttpClient http, int lobbyPort, Func<JsonElement[], bool> done)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var servers = await ListAsync(http, lobbyPort);
            if (done(servers) || clock.Elapsed > TimeSpan.FromSeconds(10))
                return servers;
            await Task.Delay(25);
        }
    }

    [Fact]
    public async Task AServerIsListedProbedUpdatedAndWithdrawn()
    {
        using var game = new TcpListener(IPAddress.Loopback, 0);
        game.Start();
        var gamePort = ((IPEndPoint)game.LocalEndpoint).Port;

        using var lobby = new LobbyServer(new LobbyOptions { Bind = IPAddress.Loopback, Port = 0 }, Quiet);
        using var http = new HttpClient();
        var log = new ConcurrentLog();

        var announcer = new HttpLobbyAnnouncer(new Uri($"http://127.0.0.1:{lobby.Port}"), log, TimeSpan.FromMilliseconds(20));
        try
        {
            announcer.Register(Info(gamePort));
            var servers = await WaitForAsync(http, lobby.Port, s => s.Length == 1);

            var entry = Assert.Single(servers);
            Assert.Equal("127.0.0.1", entry.GetProperty("ip").GetString());
            Assert.Equal(gamePort, entry.GetProperty("port").GetInt32());
            Assert.Equal("Loopback Fort", entry.GetProperty("name").GetString());
            Assert.Equal(3, entry.GetProperty("players").GetInt32());
            Assert.Equal(7, entry.GetProperty("bots").GetInt32());
            Assert.Equal(LobbyRegistration.GameNameString, entry.GetProperty("game").GetString());
            Assert.Contains(NativeProtocol.Uuid, entry.GetProperty("protocols").EnumerateArray().Select(p => p.GetString()));
            Assert.Contains(GameConstants.ProtocolUuid, entry.GetProperty("protocols").EnumerateArray().Select(p => p.GetString()));

            // The id is the server's secret and is never listed.
            Assert.DoesNotContain(announcer.Id, entry.GetRawText(), StringComparison.Ordinal);

            // The lobby connected to the game port before listing it. Nothing
            // accepts here, so the probe is still sitting in the backlog.
            Assert.True(game.Pending());

            announcer.Register(Info(gamePort, players: 9));
            servers = await WaitForAsync(http, lobby.Port, s => s.Length == 1 && s[0].GetProperty("players").GetInt32() == 9);
            Assert.Equal(9, Assert.Single(servers).GetProperty("players").GetInt32());
        }
        finally
        {
            announcer.Dispose();
        }

        Assert.Empty(await ListAsync(http, lobby.Port));
        Assert.True(log.Has("withdrawn"), string.Join("\n", log.Lines));
    }

    [Fact]
    public async Task AnUnreachablePortIsNotListedAndTheServerIsTold()
    {
        var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        var deadPort = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();

        using var lobby = new LobbyServer(new LobbyOptions { Bind = IPAddress.Loopback, Port = 0 }, Quiet);
        using var http = new HttpClient();
        var log = new ConcurrentLog();

        using (var announcer = new HttpLobbyAnnouncer(new Uri($"http://127.0.0.1:{lobby.Port}"), log, TimeSpan.FromMilliseconds(20)))
        {
            announcer.Register(Info(deadPort));

            var clock = Stopwatch.StartNew();
            while (!log.Has("cannot connect") && clock.Elapsed < TimeSpan.FromSeconds(10))
                await Task.Delay(25);
        }

        Assert.True(log.Has("cannot connect"), string.Join("\n", log.Lines));
        Assert.Empty(await ListAsync(http, lobby.Port));
    }

    [Fact]
    public async Task TheListIsServedGzippedAndRevalidatedByETag()
    {
        using var lobby = new LobbyServer(new LobbyOptions { Bind = IPAddress.Loopback, Port = 0, Probe = false }, Quiet);

        // Enough entries that gzip is worth sending.
        for (var i = 0; i < 20; i++)
            lobby.Registry.Upsert((UInt128)i + 1, At("10.0.0.1", (ushort)(1000 + i)), Fragment($"server number {i}"), "x", Environment.TickCount64);

        using var http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip });
        using var first = await http.GetAsync($"http://127.0.0.1:{lobby.Port}/servers");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Contains("server number 19", await first.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var raw = new HttpClient();
        using var request = new HttpRequestMessage(System.Net.Http.HttpMethod.Get, $"http://127.0.0.1:{lobby.Port}/servers");
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        using var zipped = await raw.SendAsync(request);
        Assert.Contains("gzip", zipped.Content.Headers.ContentEncoding);

        using var revalidate = new HttpRequestMessage(System.Net.Http.HttpMethod.Get, $"http://127.0.0.1:{lobby.Port}/servers");
        revalidate.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        using var notModified = await raw.SendAsync(revalidate);
        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
    }

    [Fact]
    public async Task PipelinedRequestsOnOneConnectionAreAllAnswered()
    {
        using var lobby = new LobbyServer(new LobbyOptions { Bind = IPAddress.Loopback, Port = 0 }, Quiet);
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(IPAddress.Loopback, lobby.Port);

        var three = "GET /servers HTTP/1.1\r\n\r\nGET /nope HTTP/1.1\r\n\r\nGET /servers HTTP/1.1\r\nConnection: close\r\n\r\n"u8.ToArray();
        await socket.SendAsync(three);

        var received = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = await socket.ReceiveAsync(buffer)) > 0)
            received.Write(buffer, 0, read);

        var text = Encoding.ASCII.GetString(received.ToArray());
        Assert.Equal(2, text.Split("HTTP/1.1 200 OK").Length - 1);
        Assert.Equal(1, text.Split("HTTP/1.1 404").Length - 1);
    }
}
