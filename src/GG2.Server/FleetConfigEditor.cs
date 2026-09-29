using System.Text;
using System.Text.Json;

namespace GG2.Server;

/// <summary>
/// What <c>gg2-server config edit</c>'s page reads and writes: the shared
/// <c>common.ini</c> and each host's overlay, as <see cref="FleetConfig"/> lays
/// them out.
/// </summary>
/// <remarks>
/// <para>
/// <b>Transport-free</b>, so the rules below are tested without a socket;
/// <see cref="ConfigEditorServer"/> is only the HTTP around it.
/// </para>
/// <para>
/// ⛔ <b>A save is all or nothing.</b> Every file the save would change is built
/// in memory, every host's rendered <c>gg2.ini</c> is validated against the new
/// contents, and only then is anything written. A value that would stop one
/// host from starting leaves all of the files as they were.
/// </para>
/// <para>
/// ⛔ <b>A secret's value is never sent to the page</b>, only whether it is set,
/// and an empty secret field means "leave it alone" — the same two rules as the
/// admin panel's settings page, for the same reasons. The page is local, but it
/// is still a browser tab, and the rule costs nothing.
/// </para>
/// <para>
/// ⚠️ <b>A host's override is written as the text typed, not as the value the
/// server would normalise it to.</b> The overlay is the operator's file and stays
/// a short list of the keys they chose; <c>common.ini</c> goes through
/// <see cref="ServerConfigFile.Edit"/> like any panel save.
/// </para>
/// </remarks>
public sealed class FleetConfigEditor
{
    private readonly string _directory;
    private readonly string _hostsFile;

    public FleetConfigEditor(string directory, string hostsFile)
    {
        _directory = directory;
        _hostsFile = hostsFile;
    }

    /// <summary>The hosts the page offers, from the hosts file, re-read on every call.</summary>
    public IReadOnlyList<string> Hosts() =>
        File.Exists(_hostsFile) ? FleetConfig.ReadHosts(_hostsFile) : [];

    /// <summary>Everything the page draws: the settings, the shared values, each host's overrides.</summary>
    public string Model()
    {
        var hosts = Hosts();
        var commonPath = FleetConfig.CommonPath(_directory);
        var commonLines = File.Exists(commonPath) ? File.ReadAllLines(commonPath) : [];

        var common = ServerOptions.CreateDefault();
        string? commonError = null;

        try
        {
            ServerConfigFile.ApplyLines(common, commonLines, commonPath);
        }
        catch (FormatException e)
        {
            commonError = e.Message;
        }

        var effective = new Dictionary<string, ServerOptions>(StringComparer.Ordinal);
        var overlays = new Dictionary<string, Dictionary<string, Dictionary<string, string>>>(StringComparer.Ordinal);
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var host in hosts)
        {
            overlays[host] = FleetConfig.ReadOverlay(_directory, host);

            try
            {
                var overlayPath = FleetConfig.OverlayPath(_directory, host);
                var rendered = FleetConfig.Render(
                    commonLines, File.Exists(overlayPath) ? File.ReadAllLines(overlayPath) : null, host);
                effective[host] = FleetConfig.Load(rendered, _directory);
            }
            catch (FormatException e)
            {
                errors[host] = e.Message;
                effective[host] = common;
            }
        }

        // ⚠️ Two path defaults (MapDirectory, [Nav] Directory) are RESOLVED FROM
        // THIS MACHINE'S DISK, and each server resolves its own. Shown as the
        // shared value they would read as "every server uses this one path", so a
        // path still at its resolved default is reported empty and marked.
        var pristine = ServerOptions.CreateDefault();

        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteBoolean("ok", true);
            json.WriteString("directory", Path.GetFullPath(_directory));
            json.WriteString("hostsFile", Path.GetFullPath(_hostsFile));
            json.WriteBoolean("commonExists", File.Exists(commonPath));

            if (commonError is not null)
                json.WriteString("commonError", commonError);

            json.WriteStartArray("hosts");

            foreach (var host in hosts)
            {
                json.WriteStartObject();
                json.WriteString("dest", host);
                json.WriteString("file", FleetConfig.HostsDirectoryName + "/" + FleetConfig.OverlayFileName(host));
                json.WriteBoolean("exists", File.Exists(FleetConfig.OverlayPath(_directory, host)));
                json.WriteString("name", effective[host].ServerName);

                if (errors.TryGetValue(host, out var error))
                    json.WriteString("error", error);

                json.WriteEndObject();
            }

            json.WriteEndArray();

            json.WriteStartArray("warnings");
            foreach (var warning in Warnings(hosts, effective))
                json.WriteStringValue(warning);
            json.WriteEndArray();

            json.WriteStartArray("settings");

            foreach (var setting in ServerSettings.All)
            {
                json.WriteStartObject();
                json.WriteString("id", setting.Id);
                json.WriteString("section", setting.Section);
                json.WriteString("key", setting.Key);
                json.WriteString("kind", setting.Kind.ToString().ToLowerInvariant());
                json.WriteString("help", setting.Help);
                json.WriteBoolean("secret", setting.Secret);

                if (setting.Choices.Count != 0)
                {
                    json.WriteStartArray("choices");
                    foreach (var choice in setting.Choices)
                        json.WriteStringValue(choice);
                    json.WriteEndArray();
                }

                json.WritePropertyName("common");
                WriteValue(json, setting, common, pristine);

                json.WriteStartObject("hosts");

                foreach (var host in hosts)
                {
                    json.WritePropertyName(host);
                    json.WriteStartObject();
                    json.WriteBoolean("overridden", Names(overlays[host], setting));
                    json.WritePropertyName("effective");
                    WriteValue(json, setting, effective[host], pristine);
                    json.WriteEndObject();
                }

                json.WriteEndObject();
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Applies <c>{"common": {id: value}, "hosts": {dest: {"set": {id: value}, "clear": [id]}}}</c>.
    /// </summary>
    /// <returns>A JSON reply, <c>ok</c> false with <c>error</c> when nothing was written.</returns>
    public string Save(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return SaveCore(document.RootElement);
        }
        catch (JsonException)
        {
            return Error("not JSON");
        }
        catch (Exception e) when (e is FormatException or ArgumentException or InvalidOperationException)
        {
            return Error(e.Message);
        }
    }

    /// <summary>A host's rendered <c>gg2.ini</c>, with every secret's value masked.</summary>
    public string Preview(string destination)
    {
        if (!Hosts().Contains(destination, StringComparer.Ordinal))
            return Error($"no host \"{destination}\" in {_hostsFile}");

        try
        {
            var commonPath = FleetConfig.CommonPath(_directory);
            var overlayPath = FleetConfig.OverlayPath(_directory, destination);
            var rendered = FleetConfig.Render(
                File.Exists(commonPath) ? File.ReadAllLines(commonPath) : [],
                File.Exists(overlayPath) ? File.ReadAllLines(overlayPath) : null,
                destination);

            using var buffer = new MemoryStream();
            using (var json = new Utf8JsonWriter(buffer))
            {
                json.WriteStartObject();
                json.WriteBoolean("ok", true);
                json.WriteString("text", MaskSecrets(rendered));
                json.WriteString("sha256", FleetConfig.Hash(FleetConfig.Encode(rendered)));
                json.WriteEndObject();
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch (FormatException e)
        {
            return Error(e.Message);
        }
    }

    /// <summary>Replaces the value of every secret setting in an ini's text with asterisks.</summary>
    public static string MaskSecrets(string text)
    {
        var lines = text.Split('\n');
        var section = string.Empty;

        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();

            if (trimmed.Length > 1 && trimmed[0] == '[' && trimmed[^1] == ']')
            {
                section = trimmed[1..^1].Trim();
                continue;
            }

            if (trimmed.Length == 0 || trimmed[0] is ';' or '#')
                continue;

            var separator = trimmed.IndexOf('=', StringComparison.Ordinal);

            if (separator <= 0)
                continue;

            var key = trimmed[..separator].Trim();

            if (trimmed[(separator + 1)..].Trim().Length != 0 && IsSecret(section, key))
                lines[i] = key + "=********";
        }

        return string.Join('\n', lines);
    }

    private string SaveCore(JsonElement request)
    {
        var hosts = Hosts();
        var commonPath = FleetConfig.CommonPath(_directory);
        var commonLines = File.Exists(commonPath) ? File.ReadAllLines(commonPath) : [];
        var newCommon = commonLines;
        var commonChanged = false;

        if (request.TryGetProperty("common", out var commonChanges)
            && commonChanges.ValueKind == JsonValueKind.Object
            && commonChanges.EnumerateObject().Any())
        {
            // Built from the FILE, exactly as config-set builds its candidate.
            var candidate = ServerOptions.CreateDefault();
            ServerConfigFile.ApplyLines(candidate, commonLines, commonPath);

            foreach (var change in commonChanges.EnumerateObject())
            {
                var (setting, value) = Change(change);

                if (setting.Secret && value.Length == 0)
                    continue;

                setting.Set(candidate, value);
                commonChanged = true;
            }

            if (commonChanged)
                newCommon = [.. ServerConfigFile.Edit(candidate, commonLines, commonPath)];
        }

        var newOverlays = new Dictionary<string, string[]?>(StringComparer.Ordinal);
        var changedOverlays = new HashSet<string>(StringComparer.Ordinal);

        foreach (var host in hosts)
        {
            var path = FleetConfig.OverlayPath(_directory, host);
            newOverlays[host] = File.Exists(path) ? File.ReadAllLines(path) : null;
        }

        if (request.TryGetProperty("hosts", out var hostChanges) && hostChanges.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in hostChanges.EnumerateObject())
            {
                if (!newOverlays.TryGetValue(entry.Name, out var existing))
                    throw new ArgumentException($"no host \"{entry.Name}\" in {_hostsFile}");

                var lines = existing is null
                    ? new List<string>
                    {
                        $"; {entry.Name} only. Every key here wins over {FleetConfig.CommonFileName}.",
                        string.Empty,
                    }
                    : new List<string>(existing);

                var changed = false;

                if (entry.Value.TryGetProperty("set", out var set) && set.ValueKind == JsonValueKind.Object)
                {
                    foreach (var change in set.EnumerateObject())
                    {
                        var (setting, value) = Change(change);

                        if (setting.Secret && value.Length == 0)
                            continue;

                        // Parsed once for its error message: a typed "sixteen" in an
                        // integer field names the setting here rather than at render.
                        setting.Set(ServerOptions.CreateDefault(), value);

                        ServerConfigFile.Put(lines, setting.Section, setting.Key, ServerConfigFile.Quote(value));
                        changed = true;
                    }
                }

                if (entry.Value.TryGetProperty("clear", out var clear) && clear.ValueKind == JsonValueKind.Array)
                {
                    foreach (var id in clear.EnumerateArray())
                    {
                        var setting = ServerSettings.Find(id.GetString() ?? string.Empty)
                            ?? throw new ArgumentException($"unknown setting \"{id.GetString()}\"");

                        ServerConfigFile.Remove(lines, setting.Section, setting.Key);
                        changed = true;
                    }
                }

                if (changed)
                {
                    newOverlays[entry.Name] = [.. lines];
                    changedOverlays.Add(entry.Name);
                }
            }
        }

        // ⛔ Every host rendered and validated BEFORE anything is written.
        // FleetConfig.Render throws a FormatException naming the host.
        foreach (var host in hosts)
            FleetConfig.Render(newCommon, newOverlays[host], host);

        var written = new List<string>();

        if (commonChanged)
        {
            WriteAtomically(commonPath, newCommon);
            written.Add(FleetConfig.CommonFileName);
        }

        foreach (var host in changedOverlays)
        {
            WriteAtomically(FleetConfig.OverlayPath(_directory, host), newOverlays[host]!);
            written.Add(FleetConfig.HostsDirectoryName + "/" + FleetConfig.OverlayFileName(host));
        }

        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteBoolean("ok", true);
            json.WriteStartArray("written");
            foreach (var file in written)
                json.WriteStringValue(file);
            json.WriteEndArray();
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static (ServerSetting Setting, string Value) Change(JsonProperty change)
    {
        var setting = ServerSettings.Find(change.Name)
            ?? throw new ArgumentException($"unknown setting \"{change.Name}\"");

        if (change.Value.ValueKind != JsonValueKind.String)
            throw new ArgumentException($"\"{change.Name}\" must be a string; every value here is ini text");

        return (setting, change.Value.GetString()!);
    }

    private IEnumerable<string> Warnings(IReadOnlyList<string> hosts, Dictionary<string, ServerOptions> effective)
    {
        if (hosts.Count == 0)
            yield return $"no hosts in {_hostsFile}";

        var named = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var host in hosts)
        {
            if (!File.Exists(FleetConfig.OverlayPath(_directory, host)))
                yield return $"{host} has no overrides, so it gets {FleetConfig.CommonFileName} unchanged";

            var name = effective[host].ServerName;

            if (named.TryGetValue(name, out var other))
                yield return $"{host} and {other} are both called \"{name}\" in the server browser";
            else
                named[name] = host;

            // ⛔ Settings this host's configuration asks for and cannot deliver.
            // Said HERE as well as at the server's startup because this page is
            // where somebody sets them, and the startup line only arrives after a
            // push and a restart — by which time nobody is looking.
            foreach (var warning in ConfigWarnings.For(effective[host]))
                yield return $"{host}: {warning}";
        }
    }

    private static bool Names(Dictionary<string, Dictionary<string, string>> ini, ServerSetting setting) =>
        ini.TryGetValue(setting.Section, out var entries) && entries.ContainsKey(setting.Key);

    private static bool IsSecret(string section, string key)
    {
        foreach (var setting in ServerSettings.All)
        {
            if (setting.Secret
                && string.Equals(setting.Section, section, StringComparison.OrdinalIgnoreCase)
                && string.Equals(setting.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void WriteValue(Utf8JsonWriter json, ServerSetting setting, ServerOptions options, ServerOptions pristine)
    {
        json.WriteStartObject();
        var value = setting.Get(options);

        if (setting.Secret)
        {
            json.WriteBoolean("set", value.Length != 0);
        }
        else if (setting.Kind == SettingKind.Path && value.Length != 0 && value == setting.Get(pristine))
        {
            json.WriteString("value", string.Empty);
            json.WriteBoolean("resolved", true);
        }
        else
        {
            json.WriteString("value", value);
        }

        json.WriteEndObject();
    }

    private static void WriteAtomically(string path, string[] lines)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        Directory.CreateDirectory(directory);

        var temporary = Path.Combine(directory, Path.GetFileName(path) + ".tmp");
        File.WriteAllText(temporary, string.Join('\n', lines) + "\n", new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }

    private static string Error(string message)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteBoolean("ok", false);
            json.WriteString("error", message);
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
