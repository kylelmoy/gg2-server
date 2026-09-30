using System.Net;
using System.Text;
using System.Text.Json;
using GG2.Server;

namespace GG2.Tests;

/// <summary>
/// A hub managing a member: pairing, the overview, and the pass-through the
/// whole dashboard runs on.
/// </summary>
/// <remarks>
/// <b>Two real servers on loopback</b>, the member over TLS with its own
/// self-signed certificate, and a browser-shaped client on the hub. Nothing is
/// mocked, because the claim is that a request goes browser → hub → member and
/// back with the right credential at each hop — which only the bytes can show.
/// </remarks>
public sealed class WebFleetHubTests : IDisposable
{
    private const string HubPassword = "hub-password-for-tests";

    private readonly string _memberDirectory = TempDirectory("member");
    private readonly string _hubDirectory = TempDirectory("hub");
    private readonly ServerHarness _member;
    private readonly ServerHarness _hub;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Thread _ticker;
    private readonly HttpClientHandler _handler;
    private readonly HttpClient _http;

    public WebFleetHubTests()
    {
        _member = new ServerHarness(o =>
        {
            o.ServerName = "The Member";
            o.ConfigFile = Path.Combine(_memberDirectory, "gg2.ini");
            o.Web = WebAdminOptions.Disabled with
            {
                Enabled = true,
                Port = 0,
                Password = "member-password-nobody-types",
                CertificatePath = WebAdminOptions.AutoCertificate,
                FleetMember = true,
            };
        });

        _hub = new ServerHarness(o =>
        {
            o.ServerName = "The Hub";
            o.ConfigFile = Path.Combine(_hubDirectory, "gg2.ini");
            o.Web = WebAdminOptions.Disabled with
            {
                Enabled = true,
                Port = 0,
                Password = HubPassword,
                FleetDirectory = "fleet",
            };
        });

        _ticker = new Thread(() =>
        {
            while (!_stopping.IsCancellationRequested)
            {
                _member.Server.Tick();
                _hub.Server.Tick();
                Thread.Sleep(1);
            }
        }) { IsBackground = true };

        _ticker.Start();

        _handler = new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true };
        _http = new HttpClient(_handler)
        {
            BaseAddress = new Uri($"http://127.0.0.1:{_hub.Server.WebBoundPort}/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _ticker.Join(TimeSpan.FromSeconds(5));
        _http.Dispose();
        _handler.Dispose();
        _hub.Dispose();
        _member.Dispose();
        _stopping.Dispose();

        foreach (var directory in new[] { _memberDirectory, _hubDirectory })
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private string MemberUrl => $"https://127.0.0.1:{_member.Server.WebBoundPort}/";

    [Fact]
    public async Task APairedMemberIsManagedThroughTheHub()
    {
        var csrf = await SignInAsync();

        var added = await PostAsync(csrf, "/api/fleet/add", new
        {
            name = "member",
            url = MemberUrl,
            pairing = _member.Server.WebPairingCode,
        });

        Assert.True(added.GetProperty("ok").GetBoolean(), added.ToString());

        // The members file holds the token, so it is the hub's own record of
        // the pairing — and survives the hub being rebuilt.
        var members = File.ReadAllText(Path.Combine(_hubDirectory, "fleet", FleetHub.MembersFile));
        Assert.Contains(_member.Server.WebPairingCode!, members, StringComparison.Ordinal);

        // The overview carries the member's own state.
        var overview = await GetJsonAsync("/api/fleet");
        var entry = overview.GetProperty("members")[0];
        Assert.Equal("member", entry.GetProperty("name").GetString());
        Assert.Equal("The Member", entry.GetProperty("state").GetProperty("server").GetProperty("name").GetString());

        // A read through the pass-through is the MEMBER's, not the hub's.
        var state = await GetJsonAsync("/h/member/api/state");
        Assert.Equal("The Member", state.GetProperty("server").GetProperty("name").GetString());

        // A command needs the HUB's CSRF token: the hub is the panel the browser
        // is signed in to, and the member only ever sees the hub's bearer token.
        using (var bare = new HttpRequestMessage(HttpMethod.Post, "/h/member/api/command"))
        {
            bare.Content = new StringContent("""{"cmd":"config"}""", Encoding.UTF8, "application/json");
            Assert.Equal(HttpStatusCode.Forbidden, (await _http.SendAsync(bare)).StatusCode);
        }

        var config = await PostAsync(csrf, "/h/member/api/command", new { cmd = "config" });
        Assert.True(config.GetProperty("ok").GetBoolean(), config.ToString());

        // The event stream is relayed as it comes.
        using var events = await _http.GetAsync("/h/member/api/events", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, events.StatusCode);

        using var reader = new StreamReader(await events.Content.ReadAsStreamAsync());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        string? line;

        do
        {
            line = await reader.ReadLineAsync(deadline.Token);
        }
        while (line is not null && line != "event: state");

        Assert.Equal("event: state", line);
        var data = await reader.ReadLineAsync(deadline.Token);
        Assert.Contains("The Member", data, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheFleetSettingsAreTheMembersAndNeverTheHostLocalOnes()
    {
        var csrf = await SignInAsync();

        await PostAsync(csrf, "/api/fleet/add", new { name = "member", url = MemberUrl, pairing = _member.Server.WebPairingCode });

        var page = await _http.GetAsync("/fleet/settings");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("/editor.js", await page.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var model = await GetJsonAsync("/api/fleet/config/model");
        Assert.Equal("member", model.GetProperty("hosts")[0].GetProperty("dest").GetString());

        var ids = model.GetProperty("settings").EnumerateArray().Select(s => s.GetProperty("id").GetString()!).ToList();
        Assert.Contains("Server.ServerName", ids);
        Assert.DoesNotContain(ids, id => id.StartsWith("Web.", StringComparison.Ordinal));
        Assert.DoesNotContain(model.GetProperty("settings").EnumerateArray(), s => s.GetProperty("kind").GetString() == "path");

        // The control: the full table does have paths, so the line above can fail.
        Assert.Contains(ServerSettings.All, s => s.Kind == SettingKind.Path);

        // A shared value and a member's override, written as the two files.
        var saved = await PostAsync(csrf, "/api/fleet/config/save", new
        {
            common = new Dictionary<string, string> { ["Settings.PlayerLimit"] = "20" },
            hosts = new Dictionary<string, object>
            {
                ["member"] = new { set = new Dictionary<string, string> { ["Server.ServerName"] = "Renamed" } },
            },
        });

        Assert.True(saved.GetProperty("ok").GetBoolean(), saved.ToString());
        Assert.Contains("PlayerLimit=20", File.ReadAllText(Path.Combine(_hubDirectory, "fleet", "common.ini")), StringComparison.Ordinal);
        Assert.Contains("Renamed", File.ReadAllText(Path.Combine(_hubDirectory, "fleet", "hosts", "member.ini")), StringComparison.Ordinal);

        // A host-local setting is refused outright, not quietly dropped.
        var refused = await PostAsync(csrf, "/api/fleet/config/save", new
        {
            common = new Dictionary<string, string> { ["Web.AllowFrom"] = "192.0.2.1" },
        });

        Assert.False(refused.GetProperty("ok").GetBoolean());
        Assert.Contains("host-local", refused.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The whole push: a dry run with its diff, the refusal a hand-written file
    /// gets, a forced install that keeps the member's host-local keys, the marker
    /// the watcher reads, and a stale preview refused.
    /// </summary>
    [Fact]
    public async Task APushInstallsTheFleetSettingsAndKeepsTheMembersOwn()
    {
        var memberFile = Path.Combine(_memberDirectory, "gg2.ini");

        // A hand-written file on the member, with two host-local keys the fleet
        // settings cannot name and one shared key they will change.
        File.WriteAllText(memberFile, "[Settings]\nPlayerLimit=12\n\n[Web]\nAllowFrom=192.0.2.9\n\n[Server]\nBanFile=my-bans.txt\n");

        var csrf = await SignInAsync();
        await PostAsync(csrf, "/api/fleet/add", new { name = "member", url = MemberUrl, pairing = _member.Server.WebPairingCode });

        await PostAsync(csrf, "/api/fleet/config/save", new
        {
            common = new Dictionary<string, string> { ["Settings.PlayerLimit"] = "20" },
            hosts = new Dictionary<string, object>
            {
                ["member"] = new { set = new Dictionary<string, string> { ["Server.ServerName"] = "Renamed" } },
            },
        });

        var check = (await PostAsync(csrf, "/api/fleet/push/check", new { })).GetProperty("members")[0].GetProperty("check");
        Assert.True(check.GetProperty("ok").GetBoolean(), check.ToString());
        Assert.False(check.GetProperty("managed").GetBoolean());
        Assert.False(check.GetProperty("unchanged").GetBoolean());
        Assert.Contains(check.GetProperty("diff").EnumerateArray(), d => d.GetProperty("op").GetString() == "-"
            && d.GetProperty("text").GetString() == "PlayerLimit=12");

        var current = check.GetProperty("current").GetString()!;

        // Never pushed and hand-written: refused without force, and untouched.
        var refused = await PostAsync(csrf, "/api/fleet/push", new { name = "member", expected = current });
        Assert.False(refused.GetProperty("ok").GetBoolean());
        Assert.Contains("PlayerLimit=12", File.ReadAllText(memberFile), StringComparison.Ordinal);

        var pushed = await PostAsync(csrf, "/api/fleet/push", new { name = "member", expected = current, force = true });
        Assert.True(pushed.GetProperty("ok").GetBoolean(), pushed.ToString());

        var installed = File.ReadAllText(memberFile);
        Assert.Contains("PlayerLimit=20", installed, StringComparison.Ordinal);
        Assert.Contains("Renamed", installed, StringComparison.Ordinal);
        Assert.Contains("AllowFrom=192.0.2.9", installed, StringComparison.Ordinal);
        Assert.Contains("BanFile=my-bans.txt", installed, StringComparison.Ordinal);
        Assert.DoesNotContain("PlayerLimit=12", installed, StringComparison.Ordinal);
        Assert.Contains("PlayerLimit=12", File.ReadAllText(memberFile + ".prev"), StringComparison.Ordinal);

        // The marker PushedConfigWatcher applies from: the new file's own hash.
        Assert.Equal(
            FleetConfig.Hash(File.ReadAllBytes(memberFile)),
            File.ReadAllText(memberFile + FleetConfig.PushedSuffix).Trim());

        // Now managed and in step.
        var again = (await PostAsync(csrf, "/api/fleet/push/check", new { })).GetProperty("members")[0].GetProperty("check");
        Assert.True(again.GetProperty("managed").GetBoolean());
        Assert.True(again.GetProperty("unchanged").GetBoolean());

        // A preview that has gone stale is refused, even forced.
        var stale = await PostAsync(csrf, "/api/fleet/push", new { name = "member", expected = current, force = true });
        Assert.False(stale.GetProperty("ok").GetBoolean());
        Assert.Contains("changed since", stale.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A fleet started from a server it already runs: adopt into the shared
    /// settings, which then match, with host-local settings never taken.
    /// </summary>
    [Fact]
    public async Task AdoptingIntoTheSharedSettingsStartsAFleetFromAServer()
    {
        File.WriteAllText(
            Path.Combine(_memberDirectory, "gg2.ini"),
            "[Settings]\nPlayerLimit=14\n\n[Server]\nWelcomeMessage=hello from the member\n\n[Web]\nAllowFrom=192.0.2.9\n");

        var csrf = await SignInAsync();
        await PostAsync(csrf, "/api/fleet/add", new { name = "member", url = MemberUrl, pairing = _member.Server.WebPairingCode });

        var listed = await PostAsync(csrf, "/api/fleet/adopt", new { name = "member" });
        Assert.True(listed.GetProperty("ok").GetBoolean(), listed.ToString());

        var ids = listed.GetProperty("differences").EnumerateArray().Select(d => d.GetProperty("id").GetString()!).ToList();
        Assert.Contains("Settings.PlayerLimit", ids);
        Assert.Contains("Server.WelcomeMessage", ids);
        Assert.DoesNotContain(ids, id => ServerSettings.Find(id) is { HostLocal: true });

        var adopted = await PostAsync(csrf, "/api/fleet/adopt", new { name = "member", ids, into = "common" });
        Assert.True(adopted.GetProperty("ok").GetBoolean(), adopted.ToString());

        var common = File.ReadAllText(Path.Combine(_hubDirectory, "fleet", "common.ini"));
        Assert.Contains("PlayerLimit=14", common, StringComparison.Ordinal);
        Assert.Contains("hello from the member", common, StringComparison.Ordinal);
        Assert.DoesNotContain("192.0.2.9", common, StringComparison.Ordinal);

        // Nothing left to adopt: the member and the fleet now agree.
        var after = await PostAsync(csrf, "/api/fleet/adopt", new { name = "member" });
        Assert.Empty(after.GetProperty("differences").EnumerateArray());
    }

    [Fact]
    public async Task ABroadcastReachesEveryMemberAndOnlyForListedVerbs()
    {
        var csrf = await SignInAsync();
        await PostAsync(csrf, "/api/fleet/add", new { name = "member", url = MemberUrl, pairing = _member.Server.WebPairingCode });

        var limited = await PostAsync(csrf, "/api/fleet/broadcast", new { cmd = "player-limit", value = 7 });
        Assert.True(limited.GetProperty("ok").GetBoolean(), limited.ToString());

        var result = limited.GetProperty("results")[0];
        Assert.Equal("member", result.GetProperty("name").GetString());
        Assert.True(result.GetProperty("reply").GetProperty("ok").GetBoolean(), result.ToString());

        // The member itself says so, not just the hub.
        var state = await GetJsonAsync("/h/member/api/state");
        Assert.Equal(7, state.GetProperty("server").GetProperty("slots").GetInt32());

        var refused = await PostAsync(csrf, "/api/fleet/broadcast", new { cmd = "restart" });
        Assert.False(refused.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void AHubTokenMayNotSetAHostLocalSetting()
    {
        Assert.NotNull(WebAdminServer.HostLocalRefusal("""{"cmd":"config-set","settings":{"Web.AllowFrom":"0.0.0.0/0"}}"""));
        Assert.NotNull(WebAdminServer.HostLocalRefusal("""{"cmd":"config-set","settings":{"Server.ServerName":"x","Web.FleetMember":"0"}}"""));

        // The controls: a shared setting, and a different verb, pass.
        Assert.Null(WebAdminServer.HostLocalRefusal("""{"cmd":"config-set","settings":{"Server.ServerName":"x"}}"""));
        Assert.Null(WebAdminServer.HostLocalRefusal("""{"cmd":"state"}"""));
    }

    [Fact]
    public async Task AWrongFingerprintIsNotPaired()
    {
        var csrf = await SignInAsync();

        Assert.True(WebIdentity.TryParsePairingCode(_member.Server.WebPairingCode, out var token, out _));

        // The right token, but a certificate pin that is not this member's:
        // exactly what a man in the middle would present.
        var reply = await PostAsync(csrf, "/api/fleet/add", new
        {
            name = "member",
            url = MemberUrl,
            pairing = WebIdentity.PairingCode(token, new string('0', 64)),
        });

        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.False(File.Exists(Path.Combine(_hubDirectory, "fleet", FleetHub.MembersFile)));
    }

    [Fact]
    public async Task AWrongTokenIsNotPaired()
    {
        var csrf = await SignInAsync();

        var reply = await PostAsync(csrf, "/api/fleet/add", new
        {
            name = "member",
            url = MemberUrl,
            pairing = WebIdentity.PairingCode(new string('x', 43), _member.Server.WebFingerprint!),
        });

        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Contains("refused", reply.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/h/member/api/login")]
    [InlineData("/h/member/api/logout")]
    [InlineData("/h/member/")]
    [InlineData("/h/nobody/api/state")]
    public async Task OnlyTheListedRoutesOfAKnownMemberPassThrough(string path)
    {
        var csrf = await SignInAsync();

        await PostAsync(csrf, "/api/fleet/add", new
        {
            name = "member",
            url = MemberUrl,
            pairing = _member.Server.WebPairingCode,
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("X-CSRF-Token", csrf);
        request.Content = new StringContent("""{"password":"x"}""", Encoding.UTF8, "application/json");

        Assert.Equal(HttpStatusCode.NotFound, (await _http.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task TheFleetRoutesRefuseAnUnauthenticatedBrowser()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.GetAsync("/api/fleet")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.GetAsync("/h/member/api/state")).StatusCode);
    }

    [Fact]
    public void AHubReachableFromTheNetworkWithoutTlsIsRefused()
    {
        var options = WebAdminOptions.Disabled with { Enabled = true, Bind = "0.0.0.0", FleetDirectory = "fleet" };

        Assert.Throws<ArgumentException>(options.Validate);

        (options with { Bind = "127.0.0.1" }).Validate();
        (options with { CertificatePath = WebAdminOptions.AutoCertificate }).Validate();
    }

    [Fact]
    public void TheMembersFileSkipsWhatItCannotRead()
    {
        var pairing = WebIdentity.PairingCode(new string('a', 43), new string('b', 64));

        var members = FleetHub.Parse(
        [
            "; comment",
            "[good]",
            "Url=https://203.0.113.7:8191/",
            "Pairing=" + pairing,
            "[plain-http]",
            "Url=http://203.0.113.8:8191/",
            "Pairing=" + pairing,
            "[no/slashes]",
            "Url=https://203.0.113.9:8191/",
            "Pairing=" + pairing,
            "[no-pairing]",
            "Url=https://203.0.113.10:8191/",
        ]);

        Assert.Equal("good", Assert.Single(members).Name);
    }

    private static string TempDirectory(string role)
    {
        var path = Path.Combine(Path.GetTempPath(), $"gg2-fleet-{role}-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private async Task<string> SignInAsync()
    {
        var reply = await _http.PostAsync(
            "/api/login",
            new StringContent(JsonSerializer.Serialize(new { password = HubPassword }), Encoding.UTF8, "application/json"));

        reply.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await reply.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("csrf").GetString()!;
    }

    private async Task<JsonElement> GetJsonAsync(string path)
    {
        var reply = await _http.GetAsync(path);
        var text = await reply.Content.ReadAsStringAsync();

        Assert.True(reply.IsSuccessStatusCode, $"{path}: {(int)reply.StatusCode} {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task<JsonElement> PostAsync(string csrf, string path, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("X-CSRF-Token", csrf);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        var reply = await _http.SendAsync(request);
        var text = await reply.Content.ReadAsStringAsync();

        return JsonDocument.Parse(text).RootElement.Clone();
    }
}
