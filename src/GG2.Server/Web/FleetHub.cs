using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;

namespace GG2.Server;

/// <summary>One server a hub manages: where it is and how it is trusted.</summary>
/// <param name="Name">The hub's name for it; also its overlay's file name, so a restricted alphabet.</param>
/// <param name="Url">Its panel's base URL, always <c>https://</c>.</param>
/// <param name="Token">Its fleet member token.</param>
/// <param name="Fingerprint">Its certificate's SHA-256, lower-case hex — the pin.</param>
public sealed record FleetMember(string Name, Uri Url, string Token, string Fingerprint);

/// <summary>
/// The hub side of a fleet: the members a panel with <c>[Web] FleetDirectory</c>
/// manages, and a client for each that trusts exactly one certificate.
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything here runs on the panel's socket threads and none of it touches
/// the game.</b> Calling a member is network I/O that can take seconds, which is
/// the one thing <see cref="WebAdminServer"/>'s threading rule forbids on the tick
/// thread — so, like <c>/api/system</c>, the fleet routes are answered where the
/// request arrived and never queued.
/// </para>
/// <para>
/// ⛔ <b>A member's certificate is accepted by its SHA-256 and nothing else</b>:
/// not the system trust store, not a name check. A member's certificate is
/// self-signed, so the chain would never validate, and a CA-issued one that did
/// would still not be the certificate the operator paired with. A mismatch fails
/// the connection, which the fleet view reports as unreachable — the same thing
/// ssh does on a changed host key, for the same reason.
/// </para>
/// <para>
/// ⚠️ <b><c>members.ini</c> holds every member's token</b> and is written
/// owner-only. It is the hub's to write; an operator may edit it by hand, and it
/// is re-read on every change the panel makes, never cached across one.
/// </para>
/// </remarks>
public sealed class FleetHub : IDisposable
{
    /// <summary>The member list's file name inside the fleet directory.</summary>
    public const string MembersFile = "members.ini";

    /// <summary>How long one member gets to answer an ordinary request.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly string _directory;
    private readonly Lock _lock = new();
    private Dictionary<string, (FleetMember Member, HttpClient Client)> _members = new(StringComparer.Ordinal);

    public FleetHub(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
        Load();

        Config = new FleetConfigEditor(
            directory,
            () => Members.Select(member => member.Name).ToArray(),
            "this hub's paired members");
    }

    /// <summary>
    /// The shared settings and each member's overrides — <c>common.ini</c> and
    /// <c>hosts/&lt;member&gt;.ini</c> in the fleet directory, the layout
    /// <c>deploy/config</c> uses.
    /// </summary>
    /// <remarks>
    /// Not thread-safe; the caller holds <see cref="ConfigLock"/> around every
    /// call, because two browser tabs saving at once would each validate against
    /// files the other is replacing.
    /// </remarks>
    public FleetConfigEditor Config { get; }

    /// <summary>Held around every <see cref="Config"/> call.</summary>
    public Lock ConfigLock { get; } = new();

    /// <summary>
    /// A member's <c>gg2.ini</c> as the fleet settings say it should be — before
    /// the member puts its own host-local values back.
    /// </summary>
    /// <remarks>Throws <see cref="FormatException"/> naming the member when it would not start.</remarks>
    public string Render(string name)
    {
        lock (ConfigLock)
        {
            var commonPath = FleetConfig.CommonPath(_directory);

            if (!File.Exists(commonPath))
                throw new FormatException("there are no fleet settings yet; save some on the Fleet settings page first");

            var overlayPath = FleetConfig.OverlayPath(_directory, name);

            return FleetConfig.Render(
                File.ReadAllLines(commonPath),
                File.Exists(overlayPath) ? File.ReadAllLines(overlayPath) : null,
                name,
                "the fleet hub");
        }
    }

    /// <summary>One control command to a member; its JSON reply, or an error reply.</summary>
    public async Task<string> CommandAsync(string name, string body, CancellationToken token)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(RequestTimeout);

            using var reply = await SendAsync(name, HttpMethod.Post, "/api/command", body, timeout.Token);
            var text = await reply.Content.ReadAsStringAsync(timeout.Token);

            return reply.IsSuccessStatusCode || text.StartsWith('{')
                ? text
                : ControlCommands.Error($"{name} answered {(int)reply.StatusCode}");
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or KeyNotFoundException)
        {
            return ControlCommands.Error($"{name}: {Describe(e)}");
        }
    }

    /// <summary>
    /// What a push would do to every member: each one's <c>config-install</c>
    /// dry run, or why there is none.
    /// </summary>
    public async Task<string> CheckJsonAsync(CancellationToken token)
    {
        var members = Members;
        var replies = await Task.WhenAll(members.Select(async member =>
        {
            string content;

            try
            {
                content = Render(member.Name);
            }
            catch (Exception e) when (e is FormatException or IOException)
            {
                return ControlCommands.Error(e.Message);
            }

            return await CommandAsync(
                member.Name,
                JsonSerializer.Serialize(new { cmd = "config-install", content, dryRun = true }),
                token);
        }));

        var json = new StringBuilder(8192);
        json.Append("{\"ok\":true,\"members\":[");

        for (var i = 0; i < members.Count; i++)
        {
            if (i > 0)
                json.Append(',');

            json.Append("{\"name\":\"").Append(ControlCommands.Escape(members[i].Name)).Append("\",\"check\":")
                .Append(replies[i]).Append('}');
        }

        json.Append("]}");
        return json.ToString();
    }

    /// <summary>
    /// Adopt: where a member's own file differs from what the fleet settings
    /// would give it, and — with <paramref name="ids"/> — writes those values
    /// into its overrides or into the shared settings.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Read from the member's <c>config</c> verb</b>, which reports the FILE's
    /// values, so a setting pinned by a flag on the member is compared by what its
    /// file says. Host-local settings are skipped, being the member's anyway.
    /// </para>
    /// <para>
    /// ⚠️ <b>Secrets cannot be adopted</b>: <c>config</c> never sends a secret's
    /// value, only whether one is set, and a hub that could read them back would
    /// make every member's password one stolen hub session away. They are listed
    /// as "cannot compare" when the member has one set, and set on the settings
    /// page by hand.
    /// </para>
    /// <para>
    /// With no shared settings yet, the comparison is against the defaults — so
    /// "adopt into common" on one server is how a fleet starts from it.
    /// </para>
    /// </remarks>
    public async Task<string> AdoptAsync(string name, IReadOnlyList<string>? ids, bool intoCommon, CancellationToken token)
    {
        var reply = await CommandAsync(name, "{\"cmd\":\"config\"}", token);
        var memberValues = new Dictionary<string, string>(StringComparer.Ordinal);
        var memberSecrets = new List<string>();

        try
        {
            using var document = JsonDocument.Parse(reply);
            var root = document.RootElement;

            if (!root.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
                return reply;

            if (!root.GetProperty("writable").GetBoolean())
                return ControlCommands.Error($"{name} was started without --config, so it has no file to adopt from");

            foreach (var setting in root.GetProperty("settings").EnumerateArray())
            {
                var id = setting.GetProperty("id").GetString()!;

                if (setting.TryGetProperty("value", out var value))
                    memberValues[id] = value.GetString() ?? string.Empty;
                else if (setting.TryGetProperty("set", out var set) && set.GetBoolean())
                    memberSecrets.Add(id);
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return ControlCommands.Error($"{name}'s config reply could not be read: {e.Message}");
        }

        ServerOptions fleet;

        try
        {
            fleet = File.Exists(FleetConfig.CommonPath(_directory))
                ? FleetConfig.Load(Render(name), _directory)
                : ServerOptions.CreateDefault();
        }
        catch (FormatException e)
        {
            return ControlCommands.Error(e.Message);
        }

        var differences = new List<(ServerSetting Setting, string Fleet, string Member)>();

        foreach (var setting in ServerSettings.All)
        {
            if (setting.HostLocal || setting.Secret || !memberValues.TryGetValue(setting.Id, out var memberValue))
                continue;

            var fleetValue = setting.Get(fleet);

            if (fleetValue != memberValue)
                differences.Add((setting, fleetValue, memberValue));
        }

        if (ids is not null)
        {
            var chosen = differences.Where(d => ids.Contains(d.Setting.Id, StringComparer.Ordinal)).ToList();

            if (chosen.Count == 0)
                return ControlCommands.Error("none of those settings differ any more; check again");

            var values = chosen.ToDictionary(d => d.Setting.Id, d => d.Member, StringComparer.Ordinal);
            var body = intoCommon
                ? JsonSerializer.Serialize(new { common = values })
                : JsonSerializer.Serialize(new { hosts = new Dictionary<string, object> { [name] = new { set = values } } });

            lock (ConfigLock)
                return Config.Save(body);
        }

        var json = new StringBuilder(4096);
        json.Append("{\"ok\":true,\"name\":\"").Append(ControlCommands.Escape(name)).Append("\",\"differences\":[");

        for (var i = 0; i < differences.Count; i++)
        {
            if (i > 0)
                json.Append(',');

            json.Append("{\"id\":\"").Append(ControlCommands.Escape(differences[i].Setting.Id))
                .Append("\",\"fleet\":\"").Append(ControlCommands.Escape(differences[i].Fleet))
                .Append("\",\"member\":\"").Append(ControlCommands.Escape(differences[i].Member)).Append("\"}");
        }

        json.Append("],\"secrets\":[");
        json.Append(string.Join(',', memberSecrets
            .Where(id => ServerSettings.Find(id) is { HostLocal: false })
            .Select(id => "\"" + ControlCommands.Escape(id) + "\"")));
        json.Append("]}");
        return json.ToString();
    }

    /// <summary>
    /// Pushes the fleet settings to one member: renders again, installs against
    /// the hash the operator previewed, and optionally restarts it at once.
    /// </summary>
    public async Task<string> PushAsync(string name, string expected, bool force, bool restart, CancellationToken token)
    {
        string content;

        try
        {
            content = Render(name);
        }
        catch (Exception e) when (e is FormatException or IOException)
        {
            return ControlCommands.Error(e.Message);
        }

        var installed = await CommandAsync(
            name,
            JsonSerializer.Serialize(new { cmd = "config-install", content, expected, force }),
            token);

        if (!restart || !installed.Contains("\"ok\":true", StringComparison.Ordinal))
            return installed;

        var restarted = await CommandAsync(name, "{\"cmd\":\"restart\"}", token);

        return restarted.Contains("\"ok\":true", StringComparison.Ordinal)
            ? installed[..^1] + ",\"restarted\":true}"
            : ControlCommands.Error($"installed, but the restart was refused: {restarted}");
    }

    /// <summary>The fleet directory: <c>members.ini</c>, and later the shared config and overlays.</summary>
    public string DirectoryPath => _directory;

    /// <summary>Every member, in name order.</summary>
    public IReadOnlyList<FleetMember> Members
    {
        get
        {
            lock (_lock)
                return _members.Values.Select(entry => entry.Member).OrderBy(m => m.Name, StringComparer.Ordinal).ToArray();
        }
    }

    /// <summary>Whether <paramref name="name"/> can name a member.</summary>
    /// <remarks>
    /// The characters <c>FleetConfig.OverlayFileName</c> leaves alone, capped at
    /// 32, so a member's name is also its overlay's file name with no escaping and
    /// a URL path segment with no encoding.
    /// </remarks>
    public static bool IsValidName(string? name) =>
        name is { Length: > 0 and <= 32 }
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '@')
        && name is not ("." or "..");

    /// <summary>
    /// Sends one request to a member, with its token, over its pinned connection.
    /// </summary>
    /// <remarks>
    /// Returns once the HEADERS have arrived, so an event stream can be copied as
    /// it comes. The caller disposes the response.
    /// </remarks>
    public async Task<HttpResponseMessage> SendAsync(
        string name,
        HttpMethod method,
        string pathAndQuery,
        string? body,
        CancellationToken token)
    {
        HttpClient client;
        FleetMember member;

        lock (_lock)
        {
            if (!_members.TryGetValue(name, out var entry))
                throw new KeyNotFoundException($"no fleet member '{name}'");

            (member, client) = entry;
        }

        using var request = new HttpRequestMessage(method, new Uri(member.Url, pathAndQuery));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", member.Token);

        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
    }

    /// <summary>
    /// Adds or replaces a member, after proving the pairing works.
    /// </summary>
    /// <remarks>
    /// <b>Verified before it is written</b>: the member is asked for
    /// <c>/api/session</c> with the token over the pinned connection, and only an
    /// answer that says "authenticated" is kept. A typo in the URL or the code is
    /// then a message on the form rather than a member that is permanently red.
    /// Returns the error, or null on success.
    /// </remarks>
    public async Task<string?> AddAsync(string name, string url, string pairingCode, CancellationToken token)
    {
        if (!IsValidName(name))
            return "a member's name is 1-32 characters of letters, digits, '.', '_', '-' and '@'";

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return "the URL must be https://host:port/ — a member serves its panel over TLS";

        if (!WebIdentity.TryParsePairingCode(pairingCode, out var memberToken, out var fingerprint))
            return "that is not a pairing code; the member prints one at startup when [Web] FleetMember=1";

        uri = new Uri(uri.GetLeftPart(UriPartial.Authority) + "/");
        var member = new FleetMember(name, uri, memberToken, fingerprint);

        using (var client = CreateClient(member))
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(RequestTimeout);

                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(uri, "/api/session"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", memberToken);

                using var reply = await client.SendAsync(request, timeout.Token);

                if (!reply.IsSuccessStatusCode)
                    return $"{uri} refused the token ({(int)reply.StatusCode}); is the pairing code current?";

                using var session = JsonDocument.Parse(await reply.Content.ReadAsStringAsync(timeout.Token));

                if (!session.RootElement.TryGetProperty("authenticated", out var authenticated)
                    || !authenticated.GetBoolean())
                {
                    return $"{uri} did not accept the token";
                }
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException)
            {
                return $"could not reach {uri}: {Describe(e)}";
            }
        }

        lock (_lock)
        {
            var members = Members.Where(m => m.Name != name).Append(member).ToList();
            Save(members);
            Load();
        }

        return null;
    }

    /// <summary>Removes a member. False if there was none by that name.</summary>
    public bool Remove(string name)
    {
        lock (_lock)
        {
            if (!_members.ContainsKey(name))
                return false;

            Save(Members.Where(m => m.Name != name).ToList());
            Load();
            return true;
        }
    }

    /// <summary>
    /// Every member's <c>state</c>, or why it could not be had — the fleet
    /// overview's one call.
    /// </summary>
    /// <remarks>
    /// Asked of all members at once with a short timeout each, so one member that
    /// is down costs the page three seconds rather than three seconds per member.
    /// </remarks>
    public async Task<string> OverviewJsonAsync(CancellationToken token)
    {
        var members = Members;
        var replies = await Task.WhenAll(members.Select(async member =>
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));

                using var reply = await SendAsync(member.Name, HttpMethod.Get, "/api/state", null, timeout.Token);
                var text = await reply.Content.ReadAsStringAsync(timeout.Token);

                return reply.IsSuccessStatusCode
                    ? (State: text, Error: (string?)null)
                    : (State: (string?)null, Error: $"answered {(int)reply.StatusCode}");
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException or KeyNotFoundException)
            {
                return (State: (string?)null, Error: Describe(e));
            }
        }));

        var json = new StringBuilder(4096);
        json.Append("{\"ok\":true,\"hub\":true,\"members\":[");

        for (var i = 0; i < members.Count; i++)
        {
            if (i > 0)
                json.Append(',');

            json.Append("{\"name\":\"").Append(ControlCommands.Escape(members[i].Name)).Append('"');
            json.Append(",\"url\":\"").Append(ControlCommands.Escape(members[i].Url.ToString())).Append('"');
            json.Append(",\"fingerprint\":\"").Append(members[i].Fingerprint).Append('"');

            // The member's own state object, verbatim: it is JSON that member's
            // ControlCommands.State wrote, and re-encoding it here would be a
            // second schema to keep in step with the first.
            if (replies[i].State is { } state)
                json.Append(",\"state\":").Append(state);
            else
                json.Append(",\"error\":\"").Append(ControlCommands.Escape(replies[i].Error ?? "unknown")).Append('"');

            json.Append('}');
        }

        json.Append("]}");
        return json.ToString();
    }

    private static string Describe(Exception e) => e switch
    {
        OperationCanceledException => "timed out",
        HttpRequestException { InnerException: AuthenticationException } =>
            "its certificate is not the one paired with — re-pair if it was replaced on purpose",
        HttpRequestException { InnerException: { } inner } => inner.Message,
        _ => e.Message,
    };

    private void Load()
    {
        var path = Path.Combine(_directory, MembersFile);
        var loaded = new Dictionary<string, (FleetMember, HttpClient)>(StringComparer.Ordinal);

        // A member whose entry did not change keeps its client, so adding one
        // member does not cut the event stream somebody is watching on another.
        foreach (var member in File.Exists(path) ? Parse(File.ReadAllLines(path)) : [])
        {
            loaded[member.Name] = _members.TryGetValue(member.Name, out var existing) && existing.Member == member
                ? existing
                : (member, CreateClient(member));
        }

        var old = _members;
        _members = loaded;

        foreach (var (name, (_, client)) in old)
        {
            if (!loaded.TryGetValue(name, out var kept) || !ReferenceEquals(kept.Item2, client))
                client.Dispose();
        }
    }

    /// <summary>Reads <c>members.ini</c>: one section per member, <c>Url</c> and <c>Pairing</c> in each.</summary>
    /// <remarks>
    /// A section that is incomplete or malformed is SKIPPED rather than fatal: the
    /// file is hand-editable, and one bad member should not take the hub's view
    /// of the other members down with it.
    /// </remarks>
    internal static List<FleetMember> Parse(IEnumerable<string> lines)
    {
        var members = new List<FleetMember>();
        string? name = null;
        string? url = null;
        string? pairing = null;

        void Flush()
        {
            if (IsValidName(name)
                && Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && uri.Scheme == Uri.UriSchemeHttps
                && WebIdentity.TryParsePairingCode(pairing, out var memberToken, out var fingerprint))
            {
                members.Add(new FleetMember(name!, uri, memberToken, fingerprint));
            }

            name = url = pairing = null;
        }

        foreach (var raw in lines)
        {
            var line = raw.Trim();

            if (line.Length == 0 || line[0] is ';' or '#')
                continue;

            if (line[0] == '[' && line[^1] == ']')
            {
                Flush();
                name = line[1..^1].Trim();
                continue;
            }

            var equals = line.IndexOf('=');

            if (equals < 0)
                continue;

            var key = line[..equals].Trim();
            var value = line[(equals + 1)..].Trim();

            if (key.Equals("Url", StringComparison.OrdinalIgnoreCase))
                url = value;
            else if (key.Equals("Pairing", StringComparison.OrdinalIgnoreCase))
                pairing = value;
        }

        Flush();
        return members;
    }

    private void Save(IReadOnlyList<FleetMember> members)
    {
        var text = new StringBuilder();

        text.Append("; The servers this hub manages. Written by the admin panel; each Pairing\n");
        text.Append("; is that member's token and certificate fingerprint, so keep this private.\n");

        foreach (var member in members.OrderBy(m => m.Name, StringComparer.Ordinal))
        {
            text.Append('\n').Append('[').Append(member.Name).Append("]\n");
            text.Append("Url=").Append(member.Url).Append('\n');
            text.Append("Pairing=").Append(WebIdentity.PairingCode(member.Token, member.Fingerprint)).Append('\n');
        }

        WebIdentity.WriteOwnerOnly(Path.Combine(_directory, MembersFile), Encoding.UTF8.GetBytes(text.ToString()), overwrite: true);
    }

    private static HttpClient CreateClient(FleetMember member)
    {
        var handler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                    certificate is not null
                    && WebIdentity.Fingerprint(certificate) == member.Fingerprint,
            },
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
        };

        // Infinite at the client, because an event stream is a request that
        // never ends; every ordinary request brings its own deadline instead.
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var (_, client) in _members.Values)
                client.Dispose();

            _members = new(StringComparer.Ordinal);
        }
    }
}
