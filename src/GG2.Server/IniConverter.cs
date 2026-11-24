using System.Text.Json.Nodes;

namespace GG2.Server;

/// <summary>
/// <c>gg2-server config convert</c>: a reference-format <c>gg2.ini</c> as a
/// <c>gg2.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Syntactic, not semantic.</b> Every key the server reads is carried across
/// with the text the ini gave it, typed as its setting is; nothing absent is
/// filled in, so a twelve-line ini becomes a twelve-key file and every default
/// stays a default. Keys the server does not read — a GM8 client's crosshair,
/// HUD and music preferences, <c>[Haxxy]</c> — are left out and listed in
/// <see cref="Result.Dropped"/>.
/// </para>
/// <para>
/// The ini grammar is the one GM8's <c>ini_read_*</c> reads: <c>[Section]</c>
/// headers, <c>key=value</c>, values trimmed and optionally double-quoted, <c>;</c>
/// and <c>#</c> comments, names case-insensitive, and a repeated key keeping the
/// LAST value.
/// </para>
/// <para>
/// <b>Raw mode</b> keeps every section and key as text and reads none of them,
/// for an ini that is not a server config — a fleet hub's <c>members.ini</c>.
/// </para>
/// </remarks>
public static class IniConverter
{
    /// <summary>The converted file and what was left out of it.</summary>
    public sealed record Result(string Json, IReadOnlyList<string> Dropped);

    /// <summary>Converts an ini's text.</summary>
    /// <exception cref="FormatException">
    /// The converted file would not load — a number that is not one, a choice
    /// that is not one of the choices — with the setting named.
    /// </exception>
    public static Result Convert(string ini, bool raw = false, string path = "gg2.json")
    {
        ArgumentNullException.ThrowIfNull(ini);

        var parsed = Parse(ini.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'));
        var config = new ConfigDocument();
        var dropped = new List<string>();

        foreach (var (section, entries) in parsed)
        {
            foreach (var (key, value) in entries)
            {
                if (raw)
                {
                    config.Put(section, key, JsonValue.Create(value));
                    continue;
                }

                var setting = ServerSettings.All.FirstOrDefault(s =>
                    s.Section.Equals(section, StringComparison.OrdinalIgnoreCase)
                    && s.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

                if (setting is not null)
                {
                    // ⚠️ A boolean through ini_read_real's rule, any non-zero is
                    // true, so "AutoBalance=2" arrives as true and not as a string.
                    config.Set(setting, setting.Kind == SettingKind.Boolean
                        ? (ServerSettings.Number(setting.Id, value) != 0 ? "1" : "0")
                        : value);
                }
                else if (section.Equals("Maps", StringComparison.OrdinalIgnoreCase))
                {
                    config.Put("Maps", key, JsonValue.Create(ServerSettings.Number($"Maps.{key}", value)));
                }
                else
                {
                    dropped.Add($"[{section}] {key}");
                }
            }
        }

        var json = config.ToText();

        // ⛔ Loaded once before it is handed back: a converter whose output the
        // server refuses has only moved the error to the next restart.
        if (!raw)
        {
            var options = ServerOptions.CreateDefault();
            ServerConfigFile.ApplyText(options, json, path);
        }

        return new Result(json, dropped);
    }

    /// <summary>
    /// GM8's ini grammar: sections in file order, a repeated section merged into
    /// the first, a repeated key keeping the last value.
    /// </summary>
    internal static List<(string Section, List<(string Key, string Value)> Entries)> Parse(IEnumerable<string> lines)
    {
        var sections = new List<(string Section, List<(string Key, string Value)> Entries)>();
        List<(string Key, string Value)>? current = null;

        foreach (var raw in lines)
        {
            var line = raw.Trim();

            // Both comment markers. GM8 writes none itself, but a hand-edited
            // gg2.ini in the wild carries `;` from the Windows convention and `#`
            // from the rotation file's.
            if (line.Length == 0 || line[0] == ';' || line[0] == '#')
                continue;

            if (line[0] == '[' && line[^1] == ']')
            {
                var name = line[1..^1].Trim();
                var index = sections.FindIndex(s => s.Section.Equals(name, StringComparison.OrdinalIgnoreCase));

                if (index < 0)
                {
                    sections.Add((name, []));
                    index = sections.Count - 1;
                }

                current = sections[index].Entries;
                continue;
            }

            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator < 0)
                continue;

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();

            // ini_write_string quotes its values; ini_write_real does not.
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                value = value[1..^1];

            if (key.Length == 0)
                continue;

            if (current is null)
            {
                sections.Add((string.Empty, []));
                current = sections[^1].Entries;
            }

            var existing = current.FindIndex(e => e.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

            if (existing >= 0)
                current[existing] = (current[existing].Key, value);
            else
                current.Add((key, value));
        }

        return sections;
    }
}
