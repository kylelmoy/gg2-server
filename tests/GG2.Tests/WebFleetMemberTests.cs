using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GG2.Server;

namespace GG2.Tests;

/// <summary>
/// What a fleet hub needs from a member's panel with no ssh in between: a
/// certificate it can pin, a token it can authenticate with, and an allowlist
/// that keeps everybody else away from the parser.
/// </summary>
/// <remarks>
/// <b>Over real TLS, pinned the way a hub pins it</b> — the handler accepts the
/// member's certificate by its SHA-256 and nothing else, so a test passing here
/// means the fingerprint the server logs is the one it actually serves.
/// </remarks>
public sealed class WebFleetMemberTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "gg2-fleet-member-" + Guid.NewGuid().ToString("N"));

    public WebFleetMemberTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private ServerHarness Member() => new(o =>
    {
        // The identity files live beside the config file, so pointing ConfigFile
        // into a fresh directory is what keeps each test's certificate its own.
        o.ConfigFile = Path.Combine(_directory, "gg2.ini");
        o.Web = WebAdminOptions.Disabled with
        {
            Enabled = true,
            Port = 0,
            Password = "correct-horse-battery-staple",
            CertificatePath = WebAdminOptions.AutoCertificate,
            FleetMember = true,
        };
    });

    [Fact]
    public void TheCertificateAndTokenAreMadeOnceAndKept()
    {
        string fingerprint;
        string pairing;

        using (var first = Member())
        {
            fingerprint = first.Server.WebFingerprint!;
            pairing = first.Server.WebPairingCode!;

            Assert.Equal(64, fingerprint.Length);
            Assert.True(first.Log.Contains(fingerprint), "the fingerprint was never printed");
            Assert.True(first.Log.Contains(pairing), "the pairing code was never printed");
        }

        Assert.True(File.Exists(Path.Combine(_directory, WebIdentity.CertificateFile)));
        Assert.True(File.Exists(Path.Combine(_directory, WebIdentity.TokenFile)));

        // The property the hub depends on: a restart is the same server.
        using var second = Member();

        Assert.Equal(fingerprint, second.Server.WebFingerprint);
        Assert.Equal(pairing, second.Server.WebPairingCode);
    }

    [Fact]
    public void APairingCodeRoundTrips()
    {
        var token = new string('a', 43);
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData([1, 2, 3]));

        Assert.True(WebIdentity.TryParsePairingCode(
            " " + WebIdentity.PairingCode(token, fingerprint) + "\n", out var parsedToken, out var parsedPrint));
        Assert.Equal(token, parsedToken);
        Assert.Equal(fingerprint, parsedPrint);

        Assert.False(WebIdentity.TryParsePairingCode("short." + fingerprint, out _, out _));
        Assert.False(WebIdentity.TryParsePairingCode(token + ".nothex", out _, out _));
        Assert.False(WebIdentity.TryParsePairingCode(token, out _, out _));
    }

    [Fact]
    public async Task AHubWithTheTokenIsServedAndOneWithoutIsRefused()
    {
        using var member = Member();
        using var ticking = new Ticker(member);

        Assert.True(WebIdentity.TryParsePairingCode(member.Server.WebPairingCode, out var token, out var pin));

        using var handler = new HttpClientHandler
        {
            // Exactly what a hub does: this certificate, by hash, and no other.
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                certificate is not null && WebIdentity.Fingerprint(certificate) == pin,
        };

        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri($"https://127.0.0.1:{member.Server.WebBoundPort}/"),
            Timeout = TimeSpan.FromSeconds(20),
        };

        // No credential at all.
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/state")).StatusCode);

        // A wrong token.
        using (var wrong = new HttpRequestMessage(HttpMethod.Get, "/api/state"))
        {
            wrong.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token + "x");
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(wrong)).StatusCode);
        }

        // The right one reads...
        using (var read = new HttpRequestMessage(HttpMethod.Get, "/api/state"))
        {
            read.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var reply = await http.SendAsync(read);

            Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
            using var state = JsonDocument.Parse(await reply.Content.ReadAsStringAsync());
            Assert.True(state.RootElement.GetProperty("ok").GetBoolean());
        }

        // ...and commands with no CSRF token, which a cookie session could not.
        using (var command = new HttpRequestMessage(HttpMethod.Post, "/api/command"))
        {
            command.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            command.Content = new StringContent("""{"cmd":"config"}""", Encoding.UTF8, "application/json");

            var reply = await http.SendAsync(command);
            var body = await reply.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
            Assert.Contains("\"ok\":true", body, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A server that is not a member refuses every bearer token, and the
    /// refusals count toward the address's lockout like wrong passwords.
    /// </summary>
    [Fact]
    public async Task ANonMemberRefusesATokenAndLocksOut()
    {
        using var server = new ServerHarness(o => o.Web = WebAdminOptions.Disabled with
        {
            Enabled = true,
            Port = 0,
            Password = "correct-horse-battery-staple",
        });

        using var ticking = new Ticker(server);
        using var http = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{server.Server.WebBoundPort}/"),
            Timeout = TimeSpan.FromSeconds(20),
        };

        var statuses = new List<HttpStatusCode>();

        for (var i = 0; i <= AdminSessions.FreeAttempts + 1; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/state");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "anything-at-all");
            statuses.Add((await http.SendAsync(request)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, statuses[0]);
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
    }

    [Fact]
    public void TheAllowlistAdmitsItsRangesAndLoopbackAndNothingElse()
    {
        var allowed = (WebAdminOptions.Disabled with { AllowFrom = "203.0.113.7, 198.51.100.0/24" })
            .AllowedRanges();

        Assert.True(WebAdminServer.Admits(allowed, IPAddress.Parse("203.0.113.7")));
        Assert.True(WebAdminServer.Admits(allowed, IPAddress.Parse("198.51.100.200")));
        Assert.True(WebAdminServer.Admits(allowed, IPAddress.Loopback));
        Assert.True(WebAdminServer.Admits(allowed, IPAddress.IPv6Loopback));
        Assert.True(WebAdminServer.Admits(allowed, IPAddress.Parse("::ffff:203.0.113.7")));

        Assert.False(WebAdminServer.Admits(allowed, IPAddress.Parse("203.0.113.8")));
        Assert.False(WebAdminServer.Admits(allowed, IPAddress.Parse("::ffff:192.0.2.1")));

        // The negative control's opposite: an empty list admits anybody.
        Assert.True(WebAdminServer.Admits([], IPAddress.Parse("192.0.2.1")));
    }

    [Theory]
    [InlineData("not-an-address")]
    [InlineData("1.2.3.4/33")]
    [InlineData("1.2.3.4, bogus")]
    public void AnAllowlistEntryThatIsNotAnAddressIsRefused(string allowFrom)
    {
        var options = WebAdminOptions.Disabled with { Enabled = true, AllowFrom = allowFrom };

        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Fact]
    public void AFleetMemberWithoutTlsIsRefused()
    {
        var options = WebAdminOptions.Disabled with { Enabled = true, FleetMember = true };

        var thrown = Assert.Throws<ArgumentException>(options.Validate);
        Assert.Contains("Certificate=auto", thrown.Message, StringComparison.Ordinal);

        // The control: the same with a certificate is fine.
        (options with { CertificatePath = WebAdminOptions.AutoCertificate }).Validate();
    }

    /// <summary>Ticks a harness on a background thread, as <c>WebAdminTests</c> does and for its reason.</summary>
    private sealed class Ticker : IDisposable
    {
        private readonly CancellationTokenSource _stopping = new();
        private readonly Thread _thread;

        public Ticker(ServerHarness harness)
        {
            _thread = new Thread(() =>
            {
                while (!_stopping.IsCancellationRequested)
                {
                    harness.Server.Tick();
                    Thread.Sleep(1);
                }
            }) { IsBackground = true };

            _thread.Start();
        }

        public void Dispose()
        {
            _stopping.Cancel();
            _thread.Join(TimeSpan.FromSeconds(5));
            _stopping.Dispose();
        }
    }
}
