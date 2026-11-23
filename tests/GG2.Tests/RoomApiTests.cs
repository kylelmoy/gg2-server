using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GG2.Protocol;
using GG2.Server;
using GG2.Server.Rooms;

namespace GG2.Tests;

/// <summary>
/// The dispatcher API over real HTTP, with the host ticking on its own thread
/// as <c>gg2-server rooms</c> runs it (<c>docs/ROOMS.md</c>, step 3).
/// </summary>
public sealed class RoomApiTests : IDisposable
{
    private readonly RecordingLog _log = new();
    private readonly RoomHost _host;
    private readonly RoomApi _api;
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _ticking;
    private readonly Task _serving;
    private readonly HttpClient _http;
    private long? _freeMb;

    public RoomApiTests()
    {
        _host = new RoomHost(new RoomHostOptions { Port = 0, Prefix = 'V', MaxRooms = 2 }, _log);
        _host.Start();

        _api = new RoomApi(_host, Template, new RoomApiOptions { Port = 0, MinFreeMemoryMb = 100, MaxLiveRoomsPerAddress = 1, TrustForwardedFor = true }, _log)
        {
            FreeMemoryMb = () => _freeMb,
        };
        _api.Start();

        _ticking = new Thread(() => _host.Run(_stop.Token)) { IsBackground = true };
        _ticking.Start();
        _serving = _api.RunAsync(_stop.Token);

        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_api.BoundPort}") };
    }

    /// <summary>A base config with a rotation and a rule, as a host's file would give.</summary>
    private static ServerOptions Template(RoomChoices choices)
    {
        var options = new ServerOptions
        {
            MapDirectory = ReferenceTree.MapsDirectory,
            NavCacheDirectory = ReferenceTree.NavCacheDirectory,
            MapRotation = ["koth_valley", "ctf_2dfort"],
            PlayerLimit = 30,
        };

        RoomTemplate.Apply(options, choices);
        return options;
    }

    /// <summary>A create from <paramref name="from"/>, as the proxy in front would say it.</summary>
    private Task<HttpResponseMessage> Create(string json, string contentType = "application/json", string from = "203.0.113.1")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/rooms")
        {
            Content = new StringContent(json, Encoding.UTF8, contentType),
        };
        request.Headers.Add("X-Forwarded-For", $"198.51.100.9, {from}");
        return _http.SendAsync(request);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>())!;

    [Fact]
    public async Task StatusCountsTheRooms()
    {
        var before = await Json(await _http.GetAsync("/status"));
        Assert.Equal("V", before.GetProperty("prefix").GetString());
        Assert.Equal(0, before.GetProperty("rooms").GetInt32());
        Assert.Equal(2, before.GetProperty("free").GetInt32());
        Assert.Equal(_host.BoundPort, before.GetProperty("gamePort").GetInt32());

        Assert.Equal(HttpStatusCode.Created, (await Create("{}")).StatusCode);

        var after = await Json(await _http.GetAsync("/status"));
        Assert.Equal(1, after.GetProperty("rooms").GetInt32());
        Assert.Equal(1, after.GetProperty("free").GetInt32());
    }

    /// <summary>The whole path: create over HTTP, then join with the code it gave.</summary>
    [Fact]
    public async Task TheCodeACreateReturnsJoinsThatRoom()
    {
        var response = await Create("""{"map":"ctf_waterway","bots":"hard","teamSize":"small"}""");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await Json(response);
        var code = body.GetProperty("code").GetString()!;
        Assert.Equal('V', code[0]);
        Assert.Equal("ctf_waterway", body.GetProperty("map").GetString());
        Assert.True(body.TryGetProperty("endsUtc", out _));

        using var client = new LoopbackClient(body.GetProperty("gamePort").GetInt32());
        client.SendHello();
        await Until(() => client.Received.Count > 0, client);
        Assert.Equal(MessageType.PasswordRequest, client.TakeMessageType());

        client.SendPassword(code);
        await Until(() => client.Received.Count > 0, client);
        Assert.Equal(MessageType.Hello, client.TakeMessageType());
        Assert.Equal("Private room", client.TakeString1());
        Assert.Equal("ctf_waterway", client.TakeString1());
    }

    /// <summary>
    /// A rotation room opens on the SHUFFLED rotation's first map. Twenty rooms
    /// on a two-map rotation all opening on one map is 2 in 10^6.
    /// </summary>
    [Fact]
    public void ARotationRoomOpensOnAShuffledRotationMap()
    {
        using var host = new RoomHost(new RoomHostOptions { Port = 0, MaxRooms = 20 }, _log);
        host.Start();

        var opened = new HashSet<string>();

        for (var i = 0; i < 20; i++)
            opened.Add(host.CreateRoom(Template(new RoomChoices()))!.Server.CurrentMap);

        Assert.Equal(new HashSet<string> { "koth_valley", "ctf_2dfort" }, opened);
    }

    [Fact]
    public async Task ARoomCanBeLookedUpByCode()
    {
        var code = (await Json(await Create("{}"))).GetProperty("code").GetString()!;

        var found = await _http.GetAsync($"/rooms/{code}");
        Assert.Equal(HttpStatusCode.OK, found.StatusCode);

        var body = await Json(found);
        Assert.Equal(code, body.GetProperty("code").GetString());
        Assert.Equal(0, body.GetProperty("players").GetInt32());
        Assert.Equal(RoomTemplate.PlayerLimit, body.GetProperty("playerLimit").GetInt32());

        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/rooms/V22222")).StatusCode);
    }

    [Theory]
    [InlineData("""{"map":"ctf_custom"}""")]
    [InlineData("""{"map":"gg_debug"}""")]
    [InlineData("""{"bots":"insane"}""")]
    [InlineData("""{"teamSize":"huge"}""")]
    [InlineData("""{"players":20}""")]
    [InlineData("""{"map":3}""")]
    [InlineData("[]")]
    [InlineData("not json")]
    public async Task AChoiceOffTheListIsRefused(string json)
    {
        var response = await Create(json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_host.Rooms);
    }

    /// <summary>What keeps a web page from creating rooms through a visitor's browser.</summary>
    [Fact]
    public async Task ACreateMustBeJson()
    {
        var response = await Create("{}", "text/plain");

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Empty(_host.Rooms);
    }

    [Fact]
    public async Task AFullHostSaysSo()
    {
        Assert.Equal(HttpStatusCode.Created, (await Create("{}", from: "203.0.113.1")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Create("{}", from: "203.0.113.2")).StatusCode);

        var third = await Create("{}", from: "203.0.113.3");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, third.StatusCode);
        Assert.Contains("full", (await Json(third)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task LowMemoryRefusesACreate()
    {
        _freeMb = 50;

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Create("{}")).StatusCode);
        Assert.Empty(_host.Rooms);

        _freeMb = 500;
        Assert.Equal(HttpStatusCode.Created, (await Create("{}")).StatusCode);
    }

    /// <summary>
    /// One address, one live room here; another address is unaffected. The
    /// address is the proxy's last <c>X-Forwarded-For</c> entry, not the first,
    /// which a client could have written.
    /// </summary>
    [Fact]
    public async Task AnAddressHoldsOnlyItsLiveRoomAllowance()
    {
        Assert.Equal(HttpStatusCode.Created, (await Create("{}", from: "203.0.113.7")).StatusCode);

        var second = await Create("{}", from: "203.0.113.7");
        Assert.Equal((HttpStatusCode)429, second.StatusCode);
        Assert.Contains("already have 1 room", (await Json(second)).GetProperty("error").GetString());
        Assert.True(second.Headers.RetryAfter?.Delta > TimeSpan.Zero);

        Assert.Equal(HttpStatusCode.Created, (await Create("{}", from: "203.0.113.8")).StatusCode);
        Assert.Equal(["203.0.113.7", "203.0.113.8"], _host.Rooms.Select(r => r.Creator));
    }

    private static async Task Until(Func<bool> condition, LoopbackClient client)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
            client.Poll();
        }

        Assert.True(condition(), "timed out");
    }

    public void Dispose()
    {
        _stop.Cancel();
        _api.Dispose();
        _serving.Wait(TimeSpan.FromSeconds(2));
        _ticking.Join(TimeSpan.FromSeconds(2));
        _http.Dispose();
        _host.Dispose();
    }
}

/// <summary>The template alone: each choice lands where <c>docs/ROOMS.md</c> says.</summary>
public sealed class RoomTemplateTests
{
    private static ServerOptions Apply(RoomChoices choices, Action<ServerOptions>? baseConfig = null)
    {
        var options = new ServerOptions { MapDirectory = ReferenceTree.MapsDirectory };
        baseConfig?.Invoke(options);
        RoomTemplate.Apply(options, choices);
        return options;
    }

    [Theory]
    [InlineData("off", false, 3, false)]
    [InlineData("easy", true, 1, false)]
    [InlineData("normal", true, 3, false)]
    [InlineData("hard", true, 5, false)]
    [InlineData("adaptive", true, 3, true)]
    public void BotsChoiceSetsEnabledDifficultyAndAdaptive(string bots, bool enabled, int difficulty, bool adaptive)
    {
        var options = Apply(new RoomChoices(Bots: bots));

        Assert.Equal(enabled, options.Bots.Enabled);
        Assert.Equal(difficulty, options.Bots.Difficulty);
        Assert.Equal(adaptive, options.AutoDifficulty);
    }

    [Theory]
    [InlineData("small", 6)]
    [InlineData("medium", 10)]
    [InlineData("large", 16)]
    public void TeamSizeSetsTheFill(string size, int fill)
    {
        var options = Apply(new RoomChoices(TeamSize: size));

        Assert.Equal(fill, options.Bots.FillToPlayers);
        Assert.Equal(fill, options.Bots.MaxBots);
    }

    [Fact]
    public void RotationIsTheBaseConfigsOrTheDefault()
    {
        var based = Apply(new RoomChoices(), o => o.MapRotation = ["koth_valley", "ctf_2dfort"]);
        Assert.Equal(["koth_valley", "ctf_2dfort"], based.MapRotation);
        Assert.Equal(ShuffleMode.AvoidArenaFirst, based.ShuffleRotation);
        Assert.Equal(string.Empty, based.Map);

        var bare = Apply(new RoomChoices());
        Assert.Equal(20, bare.MapRotation.Count);
    }

    [Fact]
    public void OneMapIsARotationOfOne()
    {
        var options = Apply(new RoomChoices(Map: "cp_egypt"), o => o.ShuffleRotation = ShuffleMode.Any);

        Assert.Equal("cp_egypt", options.Map);
        Assert.Equal(["cp_egypt"], options.MapRotation);
        Assert.Equal(ShuffleMode.None, options.ShuffleRotation);
    }

    /// <summary>The fixed values beat the base config.</summary>
    [Fact]
    public void TheFixedValuesOverrideTheBase()
    {
        var options = Apply(new RoomChoices(), o =>
        {
            o.PlayerLimit = 30;
            o.Chat = o.Chat with { Enabled = false };
            o.ExitWhenEmptySeconds = 0;
            o.MaxLifetimeMinutes = 0;
            o.ServerName = "the fleet's name";
        });

        Assert.Equal(RoomTemplate.PlayerLimit, options.PlayerLimit);
        Assert.True(options.Chat.Enabled);
        Assert.Equal(RoomTemplate.ExitWhenEmptySeconds, options.ExitWhenEmptySeconds);
        Assert.Equal(RoomTemplate.MaxLifetimeMinutes, options.MaxLifetimeMinutes);
        Assert.Equal("the fleet's name", options.ServerName);
    }

    [Fact]
    public void EveryOfferedMapIsBuiltIntoTheClient()
    {
        Assert.Equal(21, RoomTemplate.Maps.Count);

        foreach (var map in RoomTemplate.Maps)
            Assert.True(GG2.Maps.InternalMaps.IsInternal(map), map);
    }
}

/// <summary>The per-address create limits alone (<c>docs/ROOMS.md</c>, "Create limits").</summary>
public sealed class RoomCreateLimitTests
{
    private DateTime _now = new(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TheHourlyAllowanceRefillsAnHourAfterEachCreate()
    {
        var limits = new RoomCreateLimits(0, 3, () => _now);

        for (var i = 0; i < 3; i++)
        {
            Assert.Null(limits.Refusal("a", live: 0));
            limits.Created("a");
            _now = _now.AddMinutes(10);
        }

        var refused = limits.Refusal("a", live: 0);
        Assert.NotNull(refused);
        Assert.Equal(30 * 60, refused.Value.RetryAfterSeconds);
        Assert.Null(limits.Refusal("b", live: 0));

        _now = _now.AddMinutes(30);
        Assert.Null(limits.Refusal("a", live: 0));
    }

    [Fact]
    public void TheLiveCountIsTheCallersAndZeroTurnsALimitOff()
    {
        var limits = new RoomCreateLimits(2, 0, () => _now);

        Assert.Null(limits.Refusal("a", live: 1));
        Assert.NotNull(limits.Refusal("a", live: 2));

        for (var i = 0; i < 100; i++)
            limits.Created("a");

        Assert.Null(limits.Refusal("a", live: 0));
        Assert.Equal(0, limits.TrackedAddresses);
    }

    [Fact]
    public void ExpiredAddressesAreSweptOnceThereAreMany()
    {
        var limits = new RoomCreateLimits(0, 1, () => _now);

        for (var i = 0; i < 5000; i++)
            limits.Created($"10.0.{i / 256}.{i % 256}");

        _now = _now.AddHours(1);
        limits.Created("new");

        Assert.Equal(1, limits.TrackedAddresses);
    }

    [Theory]
    [InlineData("203.0.113.5", "203.0.113.5")]
    [InlineData("::ffff:203.0.113.5", "203.0.113.5")]
    [InlineData("2001:db8:1:2:aaaa:bbbb:cccc:dddd", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2::1", "2001:db8:1:2::/64")]
    public void AnIpv6AddressCountsAsItsSlash64(string address, string key) =>
        Assert.Equal(key, RoomCreateLimits.Key(IPAddress.Parse(address)));

    [Theory]
    [InlineData(true, "127.0.0.1", "198.51.100.9, 203.0.113.5", "203.0.113.5")]
    [InlineData(true, "::1", "203.0.113.5", "203.0.113.5")]
    [InlineData(true, "127.0.0.1", "not an address", "127.0.0.1")]
    [InlineData(true, "127.0.0.1", null, "127.0.0.1")]
    [InlineData(true, "192.0.2.4", "203.0.113.5", "192.0.2.4")]
    [InlineData(false, "127.0.0.1", "203.0.113.5", "127.0.0.1")]
    public void TheForwardedAddressIsReadOnlyFromATrustedLoopbackPeer(bool trust, string peer, string? header, string expected)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (header is not null)
            headers["X-Forwarded-For"] = header;

        var request = new HttpRequest
        {
            Method = "POST",
            Path = "/rooms",
            Query = [],
            Headers = headers,
            Body = "{}",
        };

        Assert.Equal(IPAddress.Parse(expected), RoomApi.ClientAddress(request, IPAddress.Parse(peer), trust));
    }
}
