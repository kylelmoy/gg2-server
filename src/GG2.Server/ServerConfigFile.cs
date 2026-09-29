using System.Text;

namespace GG2.Server;

/// <summary>
/// Reads a reference-format <c>gg2.ini</c> into a <see cref="ServerOptions"/> and
/// writes one back, plus the rotation file its <c>MapRotation</c> key points at.
/// </summary>
/// <remarks>
/// <para>
/// <c>Scripts/Game/game_init.gml:66-106</c> and
/// <c>Scripts/Game/load_map_rotation.gml</c> for the reference's own keys.
/// Everything under <c>[Settings]</c> that is a client preference (crosshairs,
/// gib level, HUD positions, music) has no meaning here, and everything under
/// <c>[Haxxy]</c> belongs to features <c>docs/PROTOCOL.md</c> lists as
/// deliberately absent. <see cref="ServerSettings"/> is the table of what IS
/// read; this file is only the ini grammar around it.
/// </para>
/// <para>
/// <b>Unknown keys and unknown sections are ignored rather than rejected</b>, so
/// a real <c>gg2.ini</c> copied off a hosted server works unedited — and so
/// <see cref="Write"/> can preserve them.
/// </para>
/// <para>
/// <b>Anything absent leaves the option alone.</b> The caller passes an options
/// object already carrying this codebase's defaults, so a key the file does not
/// mention keeps that default rather than picking up the reference's — the two
/// agree everywhere except <see cref="ServerOptions.AnnounceToLobby"/>, where the
/// difference is deliberate and documented there.
/// </para>
/// <para>
/// ⚠️ <b><see cref="Apply"/> no longer rewrites the file, and <see cref="Write"/>
/// only rewrites what it must.</b> <c>game_init</c> writes every key back after
/// reading it, which is how a GM8 player's ini grows its own defaults. A server
/// that rewrote the operator's config the first time it started would be a
/// surprise, and one that did it to a file under version control would be a diff
/// nobody asked for — so writing is something a caller asks for by name, and when
/// it happens it edits keys in place and leaves comments, layout and unknown keys
/// where they were.
/// </para>
/// </remarks>
public static class ServerConfigFile
{
    /// <summary>
    /// The twenty built-in maps <c>game_init.gml:213</c> seeds <c>[Maps]</c>
    /// with, in its own order — which is also their default priority, 1-based.
    /// </summary>
    /// <remarks>
    /// <b>Twenty, and the port ships twenty-two.</b> <c>ctf_oldfort</c> and
    /// <c>gg_debug</c> are playable and are NOT in the reference's default
    /// rotation, so they are absent here too: a server that started rotating
    /// through maps the stock host never picks would be this port inventing
    /// behaviour. Naming either in <c>[Maps]</c> still works — an explicit key
    /// wins over the default list.
    /// </remarks>
    private static readonly string[] DefaultRotation =
    [
        "ctf_truefort", "ctf_2dfort", "ctf_conflict", "ctf_classicwell", "ctf_waterway",
        "ctf_orange", "cp_dirtbowl", "cp_egypt", "arena_montane", "arena_lumberyard",
        "gen_destroy", "koth_valley", "koth_corinth", "koth_harvest", "dkoth_atalia",
        "dkoth_sixties", "tdm_mantic", "ctf_avanti", "koth_gallery", "ctf_eiger",
    ];

    /// <summary>
    /// Applies <paramref name="path"/> to <paramref name="options"/> in place.
    /// </summary>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="FormatException">
    /// A key that must be a number is not one, or a choice is not one of the
    /// choices. <b>Refused rather than ignored:</b> a typo in
    /// <c>PlayerLimit</c> that silently left the default in place is a server
    /// quietly running a configuration nobody wrote.
    /// </exception>
    public static void Apply(ServerOptions options, string path)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!File.Exists(path))
            throw new FileNotFoundException($"no config file at {path}", path);

        ApplyLines(options, File.ReadAllLines(path), path);
    }

    /// <summary>
    /// <see cref="Apply"/> over lines already in memory. <paramref name="path"/>
    /// is still needed: a relative <c>[Server] MapRotation</c> resolves against it.
    /// </summary>
    internal static void ApplyLines(ServerOptions options, string[] lines, string path)
    {
        ArgumentNullException.ThrowIfNull(options);
        ApplySettings(options, Parse(lines), path);
    }

    /// <summary>
    /// The half of <see cref="Apply"/> that works from a parsed ini, so
    /// <see cref="Write"/> can build the same baseline without re-reading.
    /// </summary>
    private static void ApplySettings(
        ServerOptions options, Dictionary<string, Dictionary<string, string>> ini, string path)
    {
        // ⚠️ IN TABLE ORDER, not in the file's. Two rows depend on running after
        // another — [Web] Enabled after [Web] Port, [Runtime] ControlPortEnabled
        // after [Runtime] ControlPort — and the file's line order is whatever the
        // operator's editor left behind. ServerSettings says so at both rows.
        foreach (var setting in ServerSettings.All)
        {
            if (ini.TryGetValue(setting.Section, out var entries)
                && entries.TryGetValue(setting.Key, out var value))
            {
                setting.Set(options, value);
            }
        }

        // --- the rotation ----------------------------------------------------
        //
        // Three sources with a fixed precedence, and the order is the point:
        //
        //  1. [Server] Rotation, a comma-separated list. THIS PORT'S OWN key and
        //     the one Write emits, so it has to win — a rotation saved from the
        //     admin panel that lost to a [Maps] section left in the same file is
        //     a panel edit that silently does nothing. Applied by the table walk
        //     above, so all this branch does is decline to overwrite it.
        //
        //  2. [Server] MapRotation, a rotation FILE. load_map_rotation.gml. The
        //     reference falls back to [Maps] when the named file does not EXIST,
        //     silently; that is transcribed rather than turned into an error,
        //     because an operator who mistypes the path gets the default rotation
        //     on the stock server too and diverging here would make the same ini
        //     behave differently on the two implementations.
        //
        //  3. [Maps], the priority list.
        if (Text(ini, "Server", "Rotation").Length == 0)
        {
            var rotationFile = Text(ini, "Server", "MapRotation");
            var rotation = rotationFile.Length != 0 && File.Exists(RotationPath(path, rotationFile))
                ? ReadRotationFile(RotationPath(path, rotationFile))
                : PriorityRotation(ini);

            if (rotation.Count != 0)
                options.MapRotation = rotation;
        }

        // ⚠️ <c>GameServerCreate.gml:110-113</c>: a server opens on
        // nextMapInRotation() only when no map was NAMED. Expressed by emptying
        // Map, which is global.launchMap == "" — see ServerOptions.Map.
        //
        // ⛔ Applied HERE and not only in CommandLine, because "what would this
        // file alone produce" is a question two other things ask: the admin
        // panel's settings page, to say which saved settings are still waiting
        // for a restart, and CommandLine's own override diff. Without it both
        // compared a file-loaded Map of ctf_truefort against a running Map of ""
        // and reported the opening map as changed on every server with a
        // rotation — a warning about a setting nobody had touched.
        if (!ini.TryGetValue("Server", out var server) || !server.ContainsKey("Map"))
        {
            if (options.MapRotation.Count != 0)
                options.Map = string.Empty;
        }
    }

    /// <summary>
    /// Writes every setting in <paramref name="options"/> back to
    /// <paramref name="path"/>, preserving whatever else is in the file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An EDIT, not a regeneration.</b> Comments, blank lines, key order and
    /// keys this project does not know about are all left exactly where they
    /// were; a managed key that already exists has its value replaced in place,
    /// and one that does not is appended to its section (the section itself
    /// appended at the end if it is missing). An operator's file comes back
    /// recognisable, which is the difference between a settings page they will
    /// use and one they will not.
    /// </para>
    /// <para>
    /// ⚠️ <b>The two lower-precedence rotation sources are REMOVED when they
    /// disagree with the rotation being written.</b> <c>[Server] MapRotation</c>
    /// and the <c>[Maps]</c> priority section both lose to
    /// <c>[Server] Rotation</c> on the way back in, so leaving a stale one behind
    /// would be a config file that reads differently from the one an operator
    /// just saved. They are left alone when they already agree, so a file that
    /// uses a rotation file keeps using it.
    /// </para>
    /// <para>
    /// ⚠️ <b>Written through a temporary file and moved into place</b>, so a
    /// process killed mid-write leaves the old config rather than half of a new
    /// one. The config file is the thing the admin password is in; truncating it
    /// is how a panel locks its operator out.
    /// </para>
    /// </remarks>
    public static void Write(ServerOptions options, string path)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(path);

        var lines = Edit(options, File.Exists(path) ? File.ReadAllLines(path) : [], path);

        // ⚠️ Same directory as the target, not the temp directory: File.Move
        // across a volume boundary is a copy, which is not atomic and defeats the
        // whole point. GetDirectoryName is null only for a bare filename, where
        // the current directory is the right answer.
        var directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        var temporary = Path.Combine(directory, Path.GetFileName(path) + ".tmp");

        File.WriteAllLines(temporary, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>
    /// <see cref="Write"/>'s edit, over lines in memory: what the file would say
    /// after saving <paramref name="options"/> into it.
    /// </summary>
    internal static List<string> Edit(ServerOptions options, string[] existing, string path)
    {
        var lines = new List<string>(existing);

        // ⚠️ What the file ALREADY says, so this can write a minimal diff.
        //
        // ⛔ Writing every key unconditionally is what this replaced, and it was
        // wrong in a way only a real save showed: two of the settings —
        // MapDirectory and Nav Directory — default to paths RESOLVED FROM DISK,
        // so the first save stamped this machine's absolute install path into the
        // operator's config file and pinned it there. It also turned a
        // twelve-line hand-written ini into eighty lines of defaults.
        //
        // A key is written when the file already names it (so an operator's own
        // line keeps its place and gets the new value) or when the value differs
        // from what the file alone would produce (so a real change is recorded).
        // Everything else stays unwritten and keeps meaning "the default".
        var baseline = ServerOptions.CreateDefault();
        var present = Parse([.. lines]);

        ApplySettings(baseline, present, path);

        foreach (var setting in ServerSettings.All)
        {
            var value = setting.Get(options);

            var named = present.TryGetValue(setting.Section, out var entries)
                && entries.ContainsKey(setting.Key);

            if (named || value != setting.Get(baseline))
                Put(lines, setting.Section, setting.Key, Quote(value));
        }

        RemoveShadowedRotationSources(lines, options, path);
        return lines;
    }

    /// <summary>
    /// Wraps a value in the double quotes <see cref="Parse"/> strips, when
    /// leaving them off would not read back.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The case that made this necessary is a TRAILING SPACE, and it is the
    /// default.</b> <c>BotPopulationOptions.NamePrefix</c> is <c>"[BOT] "</c>, the
    /// reader trims every value, and an unquoted round trip therefore renamed
    /// every bot on the server from <c>[BOT] 1</c> to <c>[BOT]1</c> — a config
    /// save that changed the game without touching a setting. Found by
    /// <c>ConfigFileCoverageTests.EverySettingSurvivesAWriteAndAReRead</c>, which
    /// is the whole argument for probing with non-default values.
    /// <para>
    /// The second case is a value that is ALREADY in quotes — a server named
    /// <c>"The Fort"</c> — which the reader would strip. Wrapping it in a second
    /// pair is what survives the strip. <c>ini_write_string</c> quotes
    /// unconditionally; this quotes only when it changes the answer, so an
    /// ordinary file stays free of quotes an operator did not type.
    /// </para>
    /// </remarks>
    internal static string Quote(string value) =>
        value != value.Trim() || (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            ? '"' + value + '"'
            : value;

    /// <summary>
    /// Replaces a key's value in place, or appends it to its section.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The LAST occurrence wins, matching <see cref="Parse"/>.</b> A
    /// repeated key is what GM8's own writer produces, and updating the first of
    /// two would write a value the reader then ignores — a save that appears to
    /// work and changes nothing.
    /// </remarks>
    internal static void Put(List<string> lines, string section, string key, string value)
    {
        var line = $"{key}={value}";
        var inSection = section.Length == 0;
        var lastMatch = -1;
        var sectionEnd = inSection ? 0 : -1;

        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();

            if (trimmed.Length != 0 && trimmed[0] == '[' && trimmed[^1] == ']')
            {
                inSection = string.Equals(
                    trimmed[1..^1].Trim(), section, StringComparison.OrdinalIgnoreCase);

                if (inSection)
                    sectionEnd = i + 1;

                continue;
            }

            if (!inSection)
                continue;

            // ⚠️ The section's end is tracked past every line, not only past a
            // matching key: appending after the last KEY would push a new entry
            // above a trailing comment that was written about the section.
            sectionEnd = i + 1;

            if (trimmed.Length == 0 || trimmed[0] == ';' || trimmed[0] == '#')
                continue;

            var separator = trimmed.IndexOf('=', StringComparison.Ordinal);

            if (separator >= 0
                && string.Equals(trimmed[..separator].Trim(), key, StringComparison.OrdinalIgnoreCase))
            {
                lastMatch = i;
            }
        }

        if (lastMatch >= 0)
        {
            lines[lastMatch] = line;
            return;
        }

        if (sectionEnd < 0)
        {
            // A missing section, appended with a blank line before it so the file
            // stays readable rather than becoming one run of keys.
            if (lines.Count != 0 && lines[^1].Trim().Length != 0)
                lines.Add(string.Empty);

            lines.Add($"[{section}]");
            lines.Add(line);
            return;
        }

        lines.Insert(sectionEnd, line);
    }

    /// <summary>
    /// Drops <c>[Server] MapRotation</c> and the <c>[Maps]</c> section when they
    /// would name a rotation other than the one just written.
    /// </summary>
    /// <remarks>
    /// See <see cref="Write"/>: both lose to <c>[Server] Rotation</c> on the way
    /// back in, so a stale one is not merely redundant, it is a line an operator
    /// will edit expecting it to do something.
    /// </remarks>
    private static void RemoveShadowedRotationSources(
        List<string> lines, ServerOptions options, string path)
    {
        var ini = Parse([.. lines]);
        var rotationFile = Text(ini, "Server", "MapRotation");
        var written = string.Join(',', options.MapRotation);

        // ⚠️ Resolved against the config file's own directory, exactly as Apply
        // resolves it. Reading it as a working-directory path here would make the
        // two disagree about whether the file agrees, and the writer would delete
        // a MapRotation line that was in fact the source.
        var resolved = rotationFile.Length == 0 ? string.Empty : RotationPath(path, rotationFile);

        var fileAgrees = resolved.Length != 0
            && File.Exists(resolved)
            && string.Join(',', ReadRotationFile(resolved)) == written;

        if (rotationFile.Length != 0 && !fileAgrees)
            Remove(lines, "Server", "MapRotation");

        // The [Maps] priority section only matters when there is no rotation file
        // in front of it. Left alone when the file is the source, because then it
        // was already inert and removing it is a diff nobody asked for.
        if (!fileAgrees && string.Join(',', PriorityRotation(ini)) != written)
            RemoveSection(lines, "Maps");
    }

    internal static void Remove(List<string> lines, string section, string key)
    {
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            var trimmed = lines[i].Trim();

            if (trimmed.Length != 0 && trimmed[0] == '[' && trimmed[^1] == ']')
                continue;

            var separator = trimmed.IndexOf('=', StringComparison.Ordinal);

            if (separator < 0
                || !string.Equals(trimmed[..separator].Trim(), key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (SectionOf(lines, i).Equals(section, StringComparison.OrdinalIgnoreCase))
                lines.RemoveAt(i);
        }
    }

    private static void RemoveSection(List<string> lines, string section)
    {
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            if (SectionOf(lines, i).Equals(section, StringComparison.OrdinalIgnoreCase))
                lines.RemoveAt(i);
        }
    }

    /// <summary>Which section line <paramref name="index"/> is in, header included.</summary>
    private static string SectionOf(List<string> lines, int index)
    {
        var section = string.Empty;

        for (var i = 0; i <= index; i++)
        {
            var trimmed = lines[i].Trim();

            if (trimmed.Length != 0 && trimmed[0] == '[' && trimmed[^1] == ']')
                section = trimmed[1..^1].Trim();
        }

        return section;
    }

    /// <summary>
    /// A rotation file's path, resolved against the config file's own directory
    /// when it is relative.
    /// </summary>
    /// <remarks>
    /// The reference resolves it against the <em>working directory</em>, because
    /// the game's ini is always beside the executable. A server told
    /// <c>--config /etc/gg2/server.ini</c> is not in that position, and
    /// "rotation.txt" in that file plainly means the one next to it. An absolute
    /// path is used as given, which is the case the two agree on.
    /// </remarks>
    private static string RotationPath(string configPath, string rotationFile)
    {
        if (Path.IsPathRooted(rotationFile))
            return rotationFile;

        var directory = Path.GetDirectoryName(Path.GetFullPath(configPath));
        return directory is null ? rotationFile : Path.Combine(directory, rotationFile);
    }

    /// <summary>
    /// <c>load_map_rotation.gml</c>'s file branch: one map per line, blanks and
    /// <c>#</c> comments skipped.
    /// </summary>
    /// <remarks>
    /// The format is documented by <c>sampleMapRotation.txt</c> at the reference
    /// checkout's root. <b>A map may appear more than once</b> and the order is
    /// meaningful, so this is a list and not a set — that is how a rotation plays
    /// some maps more often than others.
    /// </remarks>
    public static IReadOnlyList<string> ReadRotationFile(string path)
    {
        var maps = new List<string>();

        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.Trim();

            // `string_char_at(mapname, 0) != "#"`. GML's char index 0 clamps to
            // 1, so this is the first character.
            if (trimmed.Length == 0 || trimmed[0] == '#')
                continue;

            maps.Add(trimmed);
        }

        return maps;
    }

    /// <summary>
    /// <c>game_init.gml:205-224</c>: the <c>[Maps]</c> section as a priority
    /// queue, with a key of 0 meaning "never play this map".
    /// </summary>
    /// <remarks>
    /// <b>Priority order, not file order.</b> The reference builds a
    /// <c>ds_priority</c> and drains it with <c>delete_min</c>, so the rotation
    /// runs from the lowest number up and the ini's line order is irrelevant. A
    /// map with no key at all gets its 1-based position in the built-in list as
    /// its priority, which is what makes an empty <c>[Maps]</c> section produce
    /// the stock twenty-map rotation rather than nothing.
    /// <para>
    /// <b>Ties are broken by the built-in order</b>, because <see cref="List{T}.Sort"/>
    /// is unstable and <c>ds_priority_delete_min</c> has its own unspecified
    /// answer. Two maps sharing a priority is a configuration this port makes
    /// deterministic and the reference does not; nothing observable rides on
    /// which comes first.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<string> PriorityRotation(
        Dictionary<string, Dictionary<string, string>> ini)
    {
        var ranked = new List<(int Priority, int Order, string Map)>();

        for (var i = 0; i < DefaultRotation.Length; i++)
        {
            var map = DefaultRotation[i];
            var priority = Int(ini, "Maps", map, i + 1);

            if (priority != 0)
                ranked.Add((priority, i, map));
        }

        // A map named in [Maps] that is not one of the built-in twenty — a custom
        // map in the server's own Maps directory. The reference never reads those
        // (its loop walks the built-in list only), so this is an addition rather
        // than a transcription, and it is the difference between a config file
        // being able to express a custom-map rotation and not.
        if (ini.TryGetValue("Maps", out var maps))
        {
            var order = DefaultRotation.Length;

            foreach (var (map, value) in maps)
            {
                if (Array.IndexOf(DefaultRotation, map) >= 0)
                    continue;

                var priority = ServerSettings.Number($"[Maps] {map}", value);

                if (priority != 0)
                    ranked.Add((priority, order++, map));
            }
        }

        ranked.Sort((a, b) =>
            a.Priority != b.Priority ? a.Priority.CompareTo(b.Priority) : a.Order.CompareTo(b.Order));

        return ranked.ConvertAll(entry => entry.Map);
    }

    /// <summary>
    /// A GM8-compatible ini: <c>[Section]</c> headers and <c>key=value</c>, with
    /// values optionally in double quotes.
    /// </summary>
    /// <remarks>
    /// <b>Section and key lookups are case-insensitive; values are not.</b> GM8's
    /// <c>ini_read_*</c> is case-insensitive on both, and an operator who wrote
    /// <c>[server]</c> or <c>playerlimit</c> means the same thing. A password or a
    /// server name, though, is data and is taken verbatim.
    /// <para>
    /// <b>A repeated key keeps the LAST one</b>, which is what GM8's own writer
    /// produces after it rewrites a file, and a repeated section merges into the
    /// first rather than replacing it.
    /// </para>
    /// </remarks>
    internal static Dictionary<string, Dictionary<string, string>> Parse(string[] lines)
    {
        var ini = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var section = string.Empty;

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
                section = line[1..^1].Trim();
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

            if (!ini.TryGetValue(section, out var entries))
                ini[section] = entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            entries[key] = value;
        }

        return ini;
    }

    private static string Text(
        Dictionary<string, Dictionary<string, string>> ini, string section, string key) =>
        ini.TryGetValue(section, out var entries) && entries.TryGetValue(key, out var value)
            ? value
            : string.Empty;

    private static int Int(
        Dictionary<string, Dictionary<string, string>> ini, string section, string key, int fallback) =>
        ini.TryGetValue(section, out var entries) && entries.TryGetValue(key, out var value)
            ? ServerSettings.Number($"[{section}] {key}", value)
            : fallback;
}
