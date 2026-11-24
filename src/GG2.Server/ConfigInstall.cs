using System.Text;
using System.Text.Json;

namespace GG2.Server;

/// <summary>
/// <c>config-install</c>: a whole configuration file, sent by a fleet hub,
/// checked and written the way <c>scripts/deploy.sh --config</c> writes one.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it writes is exactly what <see cref="PushedConfigWatcher"/> reads</b>:
/// the file, its previous contents as <c>.prev</c>, and the new file's SHA-256 as
/// the <c>.pushed</c> marker. So a push through the panel is applied the way a
/// push over ssh is — at the first moment nobody is connected — and
/// <c>deploy.sh --status</c> reports it the same way.
/// </para>
/// <para>
/// ⛔ <b>Host-local settings are the member's, whatever the incoming text
/// says.</b> Every <see cref="ServerSetting.HostLocal"/> key is removed from the
/// incoming file and this server's current value for it put back. The hub's
/// page never offers one; this is the half that makes it true for anybody who
/// holds a token and calls the verb directly.
/// </para>
/// <para>
/// ⛔ <b>Compare-and-swap.</b> The caller names the hash of the file it previewed
/// (<c>expected</c>); a file that changed since — a panel save, a hand edit, a
/// second hub — is refused rather than overwritten. And a file that was never
/// pushed, or was edited since its last push, is refused without <c>force</c>,
/// the same two refusals <c>deploy.sh --config</c> makes.
/// </para>
/// <para>
/// ⚠️ <b>Validated before anything is written</b>, with the same
/// <see cref="ServerOptions.Validate"/> the watcher and the restart use, so a
/// push cannot install a file this server would not start on.
/// </para>
/// </remarks>
public static class ConfigInstall
{
    /// <summary>The largest file accepted. A real one is a few kilobytes.</summary>
    public const int MaxBytes = 48 * 1024;

    public static string Run(GameServer server, JsonElement request)
    {
        var path = server.Options.ConfigFile;

        if (path.Length == 0)
        {
            return ControlCommands.Error(
                "this server was started without --config, so there is no file to install into. "
                + "Restart it with --config <path>.");
        }

        if (!request.TryGetProperty("content", out var contentElement) || contentElement.ValueKind != JsonValueKind.String)
            return ControlCommands.Error("\"content\" must be the file's text");

        var content = contentElement.GetString()!;

        if (Encoding.UTF8.GetByteCount(content) > MaxBytes)
            return ControlCommands.Error($"a configuration over {MaxBytes / 1024} KB is not one");

        var dryRun = request.TryGetProperty("dryRun", out var d) && d.ValueKind == JsonValueKind.True;
        var force = request.TryGetProperty("force", out var f) && f.ValueKind == JsonValueKind.True;
        var expected = request.TryGetProperty("expected", out var e) && e.ValueKind == JsonValueKind.String
            ? e.GetString()!
            : null;

        var exists = File.Exists(path);
        var currentText = exists ? File.ReadAllText(path) : string.Empty;
        var currentHash = exists ? FleetConfig.Hash(File.ReadAllBytes(path)) : string.Empty;

        // Throws with the setting's name, which Dispatch turns into the reply.
        var text = KeepHostLocal(content, currentText);
        var newHash = FleetConfig.Hash(FleetConfig.Encode(text));

        var candidate = ServerOptions.CreateDefault();
        ServerConfigFile.ApplyText(candidate, text, path);
        candidate.Validate();

        var marker = ReadMarker(path);
        var managed = marker.Length != 0;
        var edited = managed && marker != currentHash;
        var unchanged = newHash == currentHash;

        if (dryRun)
        {
            var json = new StringBuilder(4096);
            json.Append("\"current\":\"").Append(currentHash).Append('"');
            json.Append(",\"next\":\"").Append(newHash).Append('"');
            json.Append(",\"unchanged\":").Append(unchanged ? "true" : "false");
            json.Append(",\"managed\":").Append(managed ? "true" : "false");
            json.Append(",\"editedSincePush\":").Append(edited ? "true" : "false");
            json.Append(",\"diff\":");
            AppendDiff(json, Mask(currentText), Mask(text));
            return ControlCommands.Ok(json.ToString());
        }

        if (expected is null || expected != currentHash)
        {
            return ControlCommands.Error(
                "the configuration on this server changed since it was checked; check again before pushing");
        }

        if (!force && !managed && exists)
        {
            return ControlCommands.Error(
                "this server's configuration was never pushed, so a push would replace a hand-written file; "
                + "look at the diff and force it");
        }

        if (!force && edited)
        {
            return ControlCommands.Error(
                "this server's configuration was edited here since the last push, and a push would undo that; "
                + "adopt the change or force it");
        }

        if (!unchanged)
        {
            if (exists)
                File.Copy(path, path + ".prev", overwrite: true);

            var temporary = path + ".incoming";
            File.WriteAllBytes(temporary, FleetConfig.Encode(text));
            File.Move(temporary, path, overwrite: true);
        }

        File.WriteAllText(path + FleetConfig.PushedSuffix, newHash + "\n");

        return ControlCommands.Ok(
            $"\"installed\":{(!unchanged ? "true" : "false")},\"next\":\"{newHash}\","
            + $"\"restartPending\":{(newHash != (server.PushedConfig?.StartedFrom ?? string.Empty) ? "true" : "false")}");
    }

    /// <summary>
    /// <paramref name="incoming"/> with every host-local key replaced by
    /// <paramref name="current"/>'s, or removed where the current file has none.
    /// </summary>
    /// <exception cref="FormatException">
    /// Either file is not JSON. ⚠️ Including the CURRENT one: its host-local
    /// keys cannot be kept from a file that cannot be read, and installing over
    /// it would silently replace them.
    /// </exception>
    internal static string KeepHostLocal(string incoming, string current)
    {
        var merged = ConfigDocument.Parse(incoming);

        ConfigDocument existing;

        try
        {
            existing = ConfigDocument.Parse(current);
        }
        catch (FormatException e)
        {
            throw new FormatException($"this server's own configuration file cannot be read, so its host-local settings cannot be kept: {e.Message}", e);
        }

        foreach (var setting in ServerSettings.All)
        {
            if (!setting.HostLocal)
                continue;

            merged.Remove(setting.Section, setting.Key);

            if (existing.TryGet(setting.Section, setting.Key, out var value))
                merged.Set(setting, value);
        }

        return merged.ToText();
    }

    private static string[] Mask(string text)
    {
        var lines = FleetConfigEditor.MaskSecrets(text).Split('\n');
        return lines.Length > 0 && lines[^1].Length == 0 ? lines[..^1] : lines;
    }

    private static string ReadMarker(string path)
    {
        var marker = path + FleetConfig.PushedSuffix;
        return File.Exists(marker) ? File.ReadAllText(marker).Trim() : string.Empty;
    }

    /// <summary>
    /// The changed lines, each with up to two lines of context, as
    /// <c>[{"op":" |+|-","text":…}]</c>, with <c>{"op":"…"}</c> where lines were skipped.
    /// </summary>
    /// <remarks>
    /// A longest-common-subsequence diff, quadratic in the line count — a config
    /// file is a few hundred lines at most, and <see cref="MaxBytes"/> caps it.
    /// </remarks>
    internal static void AppendDiff(StringBuilder json, string[] before, string[] after)
    {
        var n = before.Length;
        var m = after.Length;
        var lcs = new int[n + 1, m + 1];

        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                lcs[i, j] = before[i] == after[j]
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        var ops = new List<(char Op, string Text)>();
        int a = 0, b = 0;

        while (a < n || b < m)
        {
            if (a < n && b < m && before[a] == after[b])
            {
                ops.Add((' ', before[a++]));
                b++;
            }
            else if (b < m && (a == n || lcs[a, b + 1] >= lcs[a + 1, b]))
            {
                ops.Add(('+', after[b++]));
            }
            else
            {
                ops.Add(('-', before[a++]));
            }
        }

        const int context = 2;
        var keep = new bool[ops.Count];

        for (var i = 0; i < ops.Count; i++)
        {
            if (ops[i].Op == ' ')
                continue;

            for (var k = Math.Max(0, i - context); k <= Math.Min(ops.Count - 1, i + context); k++)
                keep[k] = true;
        }

        json.Append('[');
        var first = true;
        var skipped = false;

        for (var i = 0; i < ops.Count; i++)
        {
            if (!keep[i])
            {
                skipped = true;
                continue;
            }

            if (skipped && !first)
                json.Append(",{\"op\":\"…\"}");

            skipped = false;

            if (!first)
                json.Append(',');

            first = false;
            json.Append("{\"op\":\"").Append(ops[i].Op).Append("\",\"text\":\"")
                .Append(ControlCommands.Escape(ops[i].Text)).Append("\"}");
        }

        json.Append(']');
    }
}
