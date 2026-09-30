using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GG2.Protocol;
using GG2.Server;

namespace GG2.Tests;

/// <summary>
/// The remote admin panel — <c>gg2-server --web</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the only thing in the project that answers to the network, so
/// the tests that matter most are the refusals.</b> Every other surface here is
/// either a game client on a port a player already trusts or a loopback tool. A
/// bug in this file's subject is somebody else's server.
/// </para>
/// <para>
/// <b>A real socket and a real HTTP client, not a call into the handler.</b>
/// The parser is hand-written (see <c>HttpExchange.cs</c>) and half of what is
/// being asserted — that a cookie is <c>HttpOnly</c>, that a 401 really is a 401,
/// that the CSRF header is required — is only true if the bytes on the wire say
/// so. Testing the method behind it would assert the design and not the server.
/// </para>
/// <para>
/// <b>The server is ticked by a background thread.</b> Unlike
/// <c>ControlPortTests</c>, an HTTP request cannot be interleaved with manual
/// ticks from the test thread: the panel's commands are queued and drained by
/// <c>Pump</c>, so a request would deadlock against a server nobody is ticking.
/// One thread ticks, the test thread does HTTP, and that is the same split the
/// running server has.
/// </para>
/// </remarks>
public sealed class WebAdminTests : IDisposable
{
    private const string Password = "correct-horse-battery-staple";

    private readonly ServerHarness _harness;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Thread _ticker;
    private readonly HttpClientHandler _handler;
    private readonly HttpClient _http;

    public WebAdminTests()
    {
        _harness = new ServerHarness(o => o.Web = WebAdminOptions.Disabled with
        {
            Enabled = true,

            // Port 0: an ephemeral port read back from the server, for the
            // reason ControlPortTests gives — a hard-coded one makes the suite
            // un-parallelisable and the failure intermittent.
            Port = 0,
            Password = Password,
        });

        // MinHumans defaults to 1, and below it the population manager's
        // target is ZERO rather than the fill — so a bot test on a server with
        // nobody connected would silently never get a bot. The panel says the
        // same thing to an operator rather than looking like a broken button.
        _harness.Options.Bots = _harness.Options.Bots with { MinHumans = 0 };

        _ticker = new Thread(Tick) { IsBackground = true, Name = "web-admin-test-tick" };
        _ticker.Start();

        _handler = new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true };
        _http = new HttpClient(_handler)
        {
            BaseAddress = new Uri($"http://127.0.0.1:{_harness.Server.WebBoundPort}/"),
            Timeout = TimeSpan.FromSeconds(20),
        };
    }

    private void Tick()
    {
        while (!_stopping.IsCancellationRequested)
        {
            _harness.Server.Tick();

            // Roughly a tick's worth, so a snapshot cadence measured in ticks
            // still takes about the time it would on a real server.
            Thread.Sleep(1);
        }
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _ticker.Join(TimeSpan.FromSeconds(5));
        _http.Dispose();
        _handler.Dispose();
        _harness.Dispose();
        _stopping.Dispose();
    }

    // --- the refusals --------------------------------------------------------

    /// <summary>
    /// Nothing behind the password answers without one.
    /// </summary>
    /// <remarks>
    /// A <see cref="Theory"/> over every route rather than one endpoint, because
    /// the gate is a single <c>if</c> in the router and a route added above it is
    /// exactly how one would come to be public. A new route must be added here.
    /// </remarks>
    [Theory]
    [InlineData("/api/state")]
    [InlineData("/api/events")]
    [InlineData("/api/maps")]
    [InlineData("/api/log")]
    [InlineData("/api/map.png?name=ctf_truefort")]
    [InlineData("/api/perf")]
    [InlineData("/api/system")]
    [InlineData("/api/config")]
    [InlineData("/api/analytics")]
    public async Task EveryApiRouteRefusesAnUnauthenticatedRequest(string path)
    {
        var reply = await _http.GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, reply.StatusCode);
    }

    [Fact]
    public async Task ACommandRefusesAnUnauthenticatedRequest()
    {
        var reply = await _http.PostAsync("/api/command", Json("""{"cmd":"freeze"}"""));

        Assert.Equal(HttpStatusCode.Unauthorized, reply.StatusCode);
        Assert.False(_harness.Server.IsFrozen);
    }

    [Fact]
    public async Task TheWrongPasswordIsRefused()
    {
        var reply = await SignInAsync("not the password");

        Assert.Equal(HttpStatusCode.Unauthorized, reply.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.GetAsync("/api/state")).StatusCode);
    }

    /// <summary>
    /// A valid session is not enough: a mutating request must echo the token
    /// issued at sign-in.
    /// </summary>
    /// <remarks>
    /// This is the whole cross-site defence behind the <c>SameSite=Strict</c>
    /// cookie, and it is invisible from the panel — which always sends the header
    /// — so nothing but this test would notice it being dropped.
    /// </remarks>
    [Fact]
    public async Task ACommandWithoutTheCsrfTokenIsRefusedEvenWithAValidSession()
    {
        await SignInAsync(Password);

        var reply = await _http.PostAsync("/api/command", Json("""{"cmd":"freeze"}"""));

        Assert.Equal(HttpStatusCode.Forbidden, reply.StatusCode);
        Assert.False(_harness.Server.IsFrozen);
    }

    /// <summary>
    /// The map route joins its argument to a directory, so it takes a map
    /// name and nothing else.
    /// </summary>
    [Theory]
    [InlineData("../../../../Windows/win")]
    [InlineData("..%2f..%2fsecret")]
    [InlineData("ctf_truefort.png")]
    [InlineData("C:\\Windows\\win")]
    [InlineData("")]
    public async Task TheMapImageRouteTakesAMapNameAndNothingElse(string name)
    {
        var csrf = await SignedInAsync();
        Assert.NotEqual(string.Empty, csrf);

        var reply = await _http.GetAsync($"/api/map.png?name={Uri.EscapeDataString(name)}");

        // 400 for "that is not a name", 404 for a name that is one and does not
        // exist. Neither is 200, which is the assertion.
        Assert.True(
            reply.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound,
            $"'{name}' was answered with {(int)reply.StatusCode}");
    }

    // --- signing in ----------------------------------------------------------

    [Fact]
    public async Task TheRightPasswordSignsInAndTheSessionCookieIsHttpOnly()
    {
        var reply = await SignInAsync(Password);

        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);

        var cookie = Assert.Single(reply.Headers.GetValues("Set-Cookie"));

        Assert.Contains("HttpOnly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SameSite=Strict", cookie, StringComparison.OrdinalIgnoreCase);

        // NOT Secure, and that is right on plain HTTP: a browser drops a
        // Secure cookie on an unencrypted connection, so setting it there would
        // make the panel impossible to sign in to rather than safer.
        Assert.DoesNotContain("Secure", cookie, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A reload must not mean signing in again, so a browser holding a valid
    /// cookie gets its CSRF token back.
    /// </summary>
    [Fact]
    public async Task SessionReportsTheCsrfTokenToAnAlreadySignedInBrowser()
    {
        var issued = await SignedInAsync();
        var session = await ReadAsync("/api/session");

        Assert.True(session.GetProperty("authenticated").GetBoolean());
        Assert.Equal(issued, session.GetProperty("csrf").GetString());
    }

    [Fact]
    public async Task SessionTellsAnUnauthenticatedBrowserNothingButTheServerName()
    {
        var session = await ReadAsync("/api/session");

        Assert.False(session.GetProperty("authenticated").GetBoolean());
        Assert.Equal(JsonValueKind.Null, session.GetProperty("csrf").ValueKind);
        Assert.Equal(_harness.Options.ServerName, session.GetProperty("name").GetString());
    }

    [Fact]
    public async Task SigningOutInvalidatesTheSession()
    {
        await SignedInAsync();
        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync("/api/state")).StatusCode);

        await _http.PostAsync("/api/logout", null);

        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.GetAsync("/api/state")).StatusCode);
    }

    // --- what it can do ------------------------------------------------------

    [Fact]
    public async Task StateDescribesTheRunningServer()
    {
        await SignedInAsync();

        var state = await ReadAsync("/api/state");

        Assert.True(state.GetProperty("ok").GetBoolean());
        Assert.Equal(_harness.Server.CurrentMap, state.GetProperty("map").GetString());
        Assert.Equal("CaptureTheFlag", state.GetProperty("match").GetProperty("mode").GetString());

        // The map's own dimensions, so the panel can put a player on it. Art
        // pixels times six; ctf_truefort is 891 cells wide.
        Assert.Equal(_harness.Server.MapWidth, state.GetProperty("match").GetProperty("width").GetInt32());

        // Slot 0 is the reserved server player and is always on the roster.
        var players = state.GetProperty("players");
        Assert.Equal(_harness.Server.RosterCount, players.GetArrayLength());
        Assert.Equal(0, players[0].GetProperty("slot").GetInt32());
    }

    /// <summary>
    /// A command posted on a socket thread lands on the tick thread.
    /// </summary>
    /// <remarks>
    /// The property this whole class is built around: nothing on an HTTP thread
    /// touches <c>WorldState</c>, so a command is queued and <c>Pump</c> runs it
    /// between two ticks. A reply that has arrived means it has already run.
    /// </remarks>
    [Fact]
    public async Task ACommandReachesTheServer()
    {
        var csrf = await SignedInAsync();

        Assert.True((await CommandAsync(csrf, """{"cmd":"freeze"}""")).GetProperty("ok").GetBoolean());
        Assert.True(_harness.Server.IsFrozen);

        Assert.True((await CommandAsync(csrf, """{"cmd":"resume"}""")).GetProperty("ok").GetBoolean());
        Assert.False(_harness.Server.IsFrozen);
    }

    /// <summary>
    /// The panel and the control port are one dispatch, not two.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every verb the control port answers is answered here, because both go
    /// through <see cref="ControlCommands"/>. <b>What this establishes is that
    /// each verb is <em>reachable</em>, not that it does the same thing</b> — it
    /// asserts the reply is not "unknown cmd". If someone gives the panel its own
    /// switch statement, this test keeps passing until the two lists differ,
    /// which is the point at which it starts failing.
    /// </para>
    /// <para>
    /// <b>Adding a verb to <c>ControlCommands</c> means adding a row here</b>,
    /// and the negative control below is what says the assertion can fail at all.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("""{"cmd":"ping"}""")]
    [InlineData("""{"cmd":"state"}""")]
    [InlineData("""{"cmd":"freeze"}""")]
    [InlineData("""{"cmd":"resume"}""")]
    [InlineData("""{"cmd":"step","ticks":1}""")]
    [InlineData("""{"cmd":"speed","value":1}""")]
    [InlineData("""{"cmd":"bots","fill":0}""")]
    [InlineData("""{"cmd":"map","name":"koth_valley"}""")]
    [InlineData("""{"cmd":"teleport","slot":1,"x":100,"y":100}""")]
    [InlineData("""{"cmd":"kick","slot":1}""")]
    [InlineData("""{"cmd":"command","kind":"ToggleZoom","slot":1}""")]
    [InlineData("""{"cmd":"say","text":"hello"}""")]
    [InlineData("""{"cmd":"ban","address":"198.51.100.7","minutes":1}""")]
    [InlineData("""{"cmd":"unban","address":"198.51.100.7"}""")]
    [InlineData("""{"cmd":"bans"}""")]
    [InlineData("""{"cmd":"analytics"}""")]
    [InlineData("""{"cmd":"config"}""")]

    // ⚠️ Reachability only, as the whole theory is. The harness starts without
    // --config, so this one is REFUSED — "no file to save to" — which is exactly
    // the assertion: refused is fine, "unknown cmd" is not. What the verb actually
    // does is gated by ServerRestartTests, which owns a config file.
    [InlineData("""{"cmd":"config-set","settings":{"Server.ServerName":"x"}}""")]

    // ⛔ NOT in this theory: "restart" tears the server down, and the harness is
    // running it. It is gated by RestartTests instead, which owns a server it is
    // allowed to stop.
    public async Task EveryControlPortVerbIsReachableFromThePanel(string body)
    {
        var csrf = await SignedInAsync();
        var reply = await CommandAsync(csrf, body);

        // Refusals are fine — there is no slot 1 on an empty server — but
        // "unknown cmd" means the verb did not arrive.
        if (!reply.GetProperty("ok").GetBoolean())
            Assert.DoesNotContain("unknown cmd", reply.GetProperty("error").GetString()!, StringComparison.Ordinal);
    }

    /// <summary>The negative control for the theory above.</summary>
    [Fact]
    public async Task AnUnknownVerbSaysSo()
    {
        var csrf = await SignedInAsync();
        var reply = await CommandAsync(csrf, """{"cmd":"selfdestruct"}""");

        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Contains("unknown cmd", reply.GetProperty("error").GetString()!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Kicking a bot is refused, and says why.
    /// </summary>
    /// <remarks>
    /// A bot has no connection to close and the population manager would put it
    /// straight back, so a kick that appeared to work would be the panel lying
    /// about what it did. The message names the thing to change instead.
    /// </remarks>
    [Fact]
    public async Task KickingABotIsRefusedAndNamesTheBotCountInstead()
    {
        var csrf = await SignedInAsync();

        await CommandAsync(csrf, """{"cmd":"bots","fill":2}""");
        await WaitFor(() => _harness.Server.BotSlots.Count > 0, "a bot to be added");

        var slot = _harness.Server.BotSlots[0];
        var reply = await CommandAsync(csrf, $$"""{"cmd":"kick","slot":{{slot}}}""");

        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Contains("bot", reply.GetProperty("error").GetString()!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(slot, _harness.Server.BotSlots);
    }

    /// <summary>
    /// An operator kick puts <see cref="KickReason.Operator"/> on the wire, and
    /// that is the byte a plugin vote-kick sends too.
    /// </summary>
    /// <remarks>
    /// <see cref="KickReason.Name"/> is not a neutral byte —
    /// <c>ClientBeginStep.gml:433</c> renders it as <b>"Name Exploit"</b>, so a
    /// vote-kicked player would be told they had been caught cheating their name.
    /// <see cref="KickReason.Operator"/> falls through that chain to
    /// <c>else kickReason = ""</c> and shows the bare "You have been kicked
    /// from the server", which is what both paths actually mean.
    /// <para>
    /// The plugin suite cannot catch this: it drives <c>FakePluginHost</c>,
    /// which records a kick instead of writing one. This is the only gate that
    /// reads the byte a client would receive.
    /// </para>
    /// </remarks>
    [Fact]
    public void AnOperatorKickSendsTheOperatorReason()
    {
        // Its OWN harness, not the fixture's. This class ticks on a
        // background thread, and a test that also drives TickUntil and reads
        // the receive buffer from the test thread is racing it.
        using var harness = new ServerHarness();

        var client = harness.JoinClient("victim");
        harness.Tick(2);
        client.Take(client.Received.Count);

        var slot = harness.Server.RosterCount - 1;
        Assert.Equal(KickOutcome.Kicked, harness.Server.KickPlayer(slot, "by the operator"));

        // KickPlayer flushes, but the loopback transport still has to be
        // pumped before the bytes are readable here — the same wait
        // NameAndBubbleTests does around its own kick.
        harness.TickUntil(() => client.IsClosed, "the kick");

        var seen = client.Take(client.Received.Count);
        var at = Array.IndexOf(seen, (byte)MessageType.Kick);

        Assert.True(at >= 0 && at + 1 < seen.Length, "the client was dropped with no reason byte");
        Assert.Equal((byte)KickReason.Operator, seen[at + 1]);
        Assert.NotEqual((byte)KickReason.Name, seen[at + 1]);
    }

    /// <summary>Slot 0 is the reserved server player and is not kickable.</summary>
    [Fact]
    public async Task KickingTheReservedSlotIsRefused()
    {
        var csrf = await SignedInAsync();
        var reply = await CommandAsync(csrf, """{"cmd":"kick","slot":0}""");

        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Equal(KickOutcome.NoSuchSlot, _harness.Server.KickPlayer(0));
    }

    /// <summary>
    /// The system section describes the box, on whichever OS it is running.
    /// </summary>
    /// <remarks>
    /// <b>Asserts the SHAPE, not the values.</b> The suite runs on Windows
    /// today and the server is meant for a Linux VPS, so pinning a CPU model or a
    /// memory figure would only pin this machine. What must hold on both is that
    /// every group is present, that the counts are sane, and that a platform
    /// lookup which cannot answer says <c>null</c> rather than throwing — which
    /// is the whole failure mode of a file that is not where the code expected.
    /// </remarks>
    [Fact]
    public async Task SystemDescribesTheHost()
    {
        await SignedInAsync();

        var info = await ReadAsync("/api/system");

        Assert.True(info.GetProperty("ok").GetBoolean());

        var os = info.GetProperty("os");
        Assert.NotEmpty(os.GetProperty("description").GetString()!);
        Assert.NotEmpty(os.GetProperty("architecture").GetString()!);
        Assert.True(os.GetProperty("uptimeSeconds").GetDouble() > 0);

        var cpu = info.GetProperty("cpu");
        Assert.Equal(Environment.ProcessorCount, cpu.GetProperty("logicalCores").GetInt32());

        // Nullable by design on a platform that will not say, so the assertion
        // is that the property EXISTS and is one of the two — not that it is set.
        Assert.Contains(
            cpu.GetProperty("model").ValueKind,
            new[] { JsonValueKind.String, JsonValueKind.Null });

        var memory = info.GetProperty("memory");
        Assert.True(memory.GetProperty("processBytes").GetInt64() > 0);
        Assert.True(memory.GetProperty("managedBytes").GetInt64() > 0);

        var runtime = info.GetProperty("runtime");
        Assert.Contains(".NET", runtime.GetProperty("framework").GetString()!, StringComparison.Ordinal);
        Assert.Contains(runtime.GetProperty("build").GetString(), new[] { "Debug", "Release" });
        Assert.Equal(3, runtime.GetProperty("collections").GetArrayLength());
        Assert.Equal(Environment.ProcessId, runtime.GetProperty("processId").GetInt32());

        // The one an operator on a box they did not set up asks first.
        Assert.Equal(_harness.Options.MapDirectory, info.GetProperty("paths").GetProperty("maps").GetString());
    }

    /// <summary>
    /// The command line and the environment are never in it.
    /// </summary>
    /// <remarks>
    /// <c>--web-password</c> is in the command line and may be in the
    /// environment, and this endpoint is read by a browser. The test looks for
    /// the actual password in the reply, so it fails on any future field that
    /// starts including either — which is a thing somebody would add meaning
    /// well.
    /// </remarks>
    [Fact]
    public async Task SystemNeverLeaksTheCommandLineOrTheEnvironment()
    {
        await SignedInAsync();

        var reply = await _http.GetAsync("/api/system");
        var body = await reply.Content.ReadAsStringAsync();

        Assert.DoesNotContain(Password, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("--web", body, StringComparison.OrdinalIgnoreCase);

        // A canary in the environment, so this covers the variables too.
        Assert.DoesNotContain("PATH=", body, StringComparison.Ordinal);
    }

    // --- the page ------------------------------------------------------------

    /// <summary>
    /// The panel's files are compiled in, and a build that dropped one would
    /// serve a working binary and a broken page.
    /// </summary>
    [Theory]
    [InlineData("/", "text/html", "<title>gg2-server</title>")]
    [InlineData("/app.css", "text/css", ":root")]
    [InlineData("/app.js", "text/javascript", "/api/events")]
    [InlineData("/favicon.svg", "image/svg+xml", "<svg")]

    // ⚠️ Deliberately public, like the three above it. It is the analytics
    // map's geometry — the same public-domain coastline for every server in the
    // world — and says nothing about this one. What is drawn ON it is behind
    // /api/analytics, which is in the refusal theory above.
    [InlineData("/world.json", "application/json", "\"views\"")]
    public async Task ThePageIsServedWithoutSigningIn(string path, string type, string contains)
    {
        var reply = await _http.GetAsync(path);
        var body = await reply.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
        Assert.Equal(type, reply.Content.Headers.ContentType!.MediaType);
        Assert.Contains(contains, body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The CSP has no <c>unsafe-inline</c>, which is why the page's CSS and
    /// JavaScript are separate routes.
    /// </summary>
    /// <remarks>
    /// The panel renders player-chosen names into the DOM. Losing this header is
    /// the difference between a name that renders as text and one that runs.
    /// </remarks>
    [Fact]
    public async Task EveryReplyCarriesTheSecurityHeaders()
    {
        var reply = await _http.GetAsync("/");
        var policy = Assert.Single(reply.Headers.GetValues("Content-Security-Policy"));

        Assert.Contains("default-src 'self'", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-inline", policy, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", policy, StringComparison.Ordinal);
        Assert.Equal("nosniff", Assert.Single(reply.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("DENY", Assert.Single(reply.Headers.GetValues("X-Frame-Options")));
    }

    // --- what it costs -------------------------------------------------------

    /// <summary>
    /// A server with the panel enabled and nobody watching builds no
    /// snapshots.
    /// </summary>
    /// <remarks>
    /// The claim that makes leaving <c>--web</c> on reasonable, so it is measured
    /// rather than asserted in a comment. The panel has been up and ticking for
    /// the whole of this test's life; the number is still zero.
    /// </remarks>
    [Fact]
    public async Task NobodyWatchingMeansNoSnapshots()
    {
        // Long enough for many multiples of the snapshot cadence to have gone by.
        await Task.Delay(300);
        Assert.Equal(0, _harness.Server.WebSnapshotCount);

        // And the negative control: a one-shot /api/state does not subscribe
        // either, because it is answered by running `state` on the tick thread
        // rather than out of the published snapshot.
        await SignedInAsync();
        await ReadAsync("/api/state");
        Assert.Equal(0, _harness.Server.WebSnapshotCount);
    }

    /// <summary>
    /// The performance history IS collected with nobody watching, which is the
    /// opposite of the snapshot and is the point.
    /// </summary>
    /// <remarks>
    /// An operator opens the panel <em>because</em> something already happened. A
    /// graph that started when they arrived answers a question nobody has — so
    /// this asymmetry is deliberate, and both halves of it are gated: the
    /// snapshot must not be built, and the history must be.
    /// </remarks>
    [Fact]
    public async Task ThePerformanceHistoryIsCollectedWithNobodyWatching()
    {
        await SignedInAsync();
        await WaitFor(() => _harness.Server.WebSnapshotCount == 0, "no snapshot to have been built");

        // Two sample intervals, so this cannot pass on a single lucky sample.
        await Task.Delay(PerfHistory.IntervalMs * 2 + 400);

        var perf = await ReadAsync("/api/perf");
        var samples = perf.GetProperty("samples");

        Assert.True(samples.GetArrayLength() > 0, "no samples were collected");

        // And nobody was watching for any of it.
        Assert.Equal(0, _harness.Server.WebSnapshotCount);
    }

    /// <summary>
    /// The field list and the rows cannot drift apart.
    /// </summary>
    /// <remarks>
    /// The samples go over the wire as arrays of bare numbers — 300 samples of
    /// fifteen named fields would be 60 KB of repeated key names for 5 KB of data
    /// — so the panel reads them positionally against the <c>fields</c> array. A
    /// column added to one and not the other silently shifts every series after
    /// it onto the wrong chart, which looks like a server problem and is not one.
    /// </remarks>
    [Fact]
    public async Task EveryPerformanceRowMatchesTheDeclaredFields()
    {
        await SignedInAsync();
        await Task.Delay(PerfHistory.IntervalMs + 400);

        var perf = await ReadAsync("/api/perf");
        var fields = perf.GetProperty("fields");

        Assert.Equal(PerfHistory.Fields.Length, fields.GetArrayLength());

        for (var i = 0; i < PerfHistory.Fields.Length; i++)
            Assert.Equal(PerfHistory.Fields[i], fields[i].GetString());

        var samples = perf.GetProperty("samples");
        Assert.True(samples.GetArrayLength() > 0, "no samples were collected");

        foreach (var sample in samples.EnumerateArray())
            Assert.Equal(PerfHistory.Fields.Length, sample.GetArrayLength());
    }

    /// <summary>The history reaches a browser over the same stream as the rest.</summary>
    /// <remarks>
    /// The backlog goes out on the stream's FIRST pass, so a panel that has
    /// just connected draws a full five minutes immediately instead of filling in
    /// over the next five.
    /// </remarks>
    [Fact]
    public async Task PerformanceSamplesArriveOnTheStream()
    {
        await SignedInAsync();
        await Task.Delay(PerfHistory.IntervalMs + 400);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/events");
        using var reply = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        await using var body = await reply.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(body, Encoding.UTF8);

        var perf = await ReadEventAsync(reader, "perf");

        Assert.Contains("\"tickMean\"", perf, StringComparison.Ordinal);
        Assert.Contains("\"samples\":[[", perf, StringComparison.Ordinal);
    }

    /// <summary>And the stream does subscribe, so the panel is not simply dead.</summary>
    [Fact]
    public async Task AnOpenEventStreamGetsSnapshots()
    {
        await SignedInAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/events");
        using var reply = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
        Assert.Equal("text/event-stream", reply.Content.Headers.ContentType!.MediaType);

        await using var body = await reply.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(body, Encoding.UTF8);

        // Not "the first event": the stream also carries the server's log, and
        // a server that has just started has a backlog of it waiting. Asserting
        // on event ONE made this a race against how much had been logged.
        var state = await ReadEventAsync(reader, "state");

        Assert.Contains("\"ok\":true", state, StringComparison.Ordinal);

        // The placeholder must never reach a browser: the panel renders the
        // first state event it is given, and one with no fields in it threw.
        Assert.DoesNotContain("no snapshot yet", state, StringComparison.Ordinal);
        Assert.True(_harness.Server.WebSnapshotCount > 0);
    }

    // --- the password --------------------------------------------------------

    /// <summary>
    /// There is no configuration in which the panel is unauthenticated.
    /// </summary>
    /// <remarks>
    /// "No password" means "one you did not choose", printed once at startup. The
    /// generated one is asserted to actually work, because a password nobody can
    /// use is the same outage as a broken panel — and to be long enough that
    /// <see cref="AdminSessions"/>'s lockout is not the only thing standing
    /// between a stranger and the server.
    /// </remarks>
    [Fact]
    public async Task AnUnconfiguredPasswordIsGeneratedRatherThanAbsent()
    {
        using var harness = new ServerHarness(o => o.Web = WebAdminOptions.Disabled with
        {
            Enabled = true,
            Port = 0,
            Password = string.Empty,
        });

        var generated = harness.Server.WebPassword;

        Assert.True(generated.Length >= 16, $"a generated password of {generated.Length} characters");

        // Read BEFORE the ticking thread starts. RecordingLog is a bare List
        // and Start() has already written the line, so this is the one moment it
        // can be read without racing the server's own logging.
        Assert.True(harness.Log.Contains(generated), "the generated password was never printed");

        using var stopping = new CancellationTokenSource();
        var ticker = new Thread(() =>
        {
            while (!stopping.IsCancellationRequested)
            {
                harness.Server.Tick();
                Thread.Sleep(1);
            }
        }) { IsBackground = true };

        ticker.Start();

        try
        {
            using var http = new HttpClient
            {
                BaseAddress = new Uri($"http://127.0.0.1:{harness.Server.WebBoundPort}/"),
                Timeout = TimeSpan.FromSeconds(20),
            };

            var wrong = await http.PostAsync("/api/login", Json("""{"password":""}"""));
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

            var right = await http.PostAsync(
                "/api/login",
                Json(JsonSerializer.Serialize(new { password = generated })));

            Assert.Equal(HttpStatusCode.OK, right.StatusCode);
        }
        finally
        {
            stopping.Cancel();
            ticker.Join(TimeSpan.FromSeconds(5));
        }
    }

    // --- helpers -------------------------------------------------------------

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private Task<HttpResponseMessage> SignInAsync(string password) =>
        _http.PostAsync("/api/login", Json(JsonSerializer.Serialize(new { password })));

    /// <summary>Signs in and returns the CSRF token.</summary>
    private async Task<string> SignedInAsync()
    {
        var reply = await SignInAsync(Password);
        reply.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await reply.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("csrf").GetString()!;
    }

    private async Task<JsonElement> ReadAsync(string path)
    {
        var reply = await _http.GetAsync(path);
        return JsonDocument.Parse(await reply.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private async Task<JsonElement> CommandAsync(string csrf, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/command") { Content = Json(body) };
        request.Headers.Add("X-CSRF-Token", csrf);

        var reply = await _http.SendAsync(request);
        return JsonDocument.Parse(await reply.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    /// <summary>Reads whole server-sent events until one of the named kind arrives.</summary>
    private static async Task<string> ReadEventAsync(StreamReader reader, string wanted)
    {
        var text = new StringBuilder();

        // Bounded, so a stream that never produces one fails the test rather than
        // hanging the suite. Heartbeat comment lines are skipped.
        for (var line = 0; line < 4000; line++)
        {
            var next = await reader.ReadLineAsync();

            if (next is null)
                break;

            if (next.Length == 0)
            {
                if (text.ToString().StartsWith($"event: {wanted}", StringComparison.Ordinal))
                    return text.ToString();

                text.Clear();
                continue;
            }

            if (next[0] == ':')
                continue;

            text.Append(next).Append('\n');
        }

        Assert.Fail($"no complete '{wanted}' event arrived on the stream");
        return string.Empty;
    }

    private static async Task WaitFor(Func<bool> condition, string description)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (condition())
                return;

            await Task.Delay(10);
        }

        Assert.Fail($"gave up waiting for {description}");
    }
}
