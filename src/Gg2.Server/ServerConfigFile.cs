using System.Globalization;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Server;

/// <summary>
/// Reads a reference-format <c>gg2.ini</c> into a <see cref="ServerOptions"/>,
/// plus the rotation file its <c>MapRotation</c> key points at.
/// </summary>
/// <remarks>
/// <para>
/// <c>Scripts/Game/game_init.gml:66-106</c> and
/// <c>Scripts/Game/load_map_rotation.gml</c>. Only the <b>server-side</b> keys
/// are read: everything under <c>[Settings]</c> that is a client preference
/// (crosshairs, gib level, HUD positions, music) has no meaning here, and
/// everything under <c>[Haxxy]</c> and the plugin keys belongs to features
/// <c>docs/PROTOCOL.md</c> lists as deliberately absent.
/// </para>
/// <para>
/// ⚠️ <b>Read-only, unlike the reference.</b> <c>game_init</c> writes every key
/// back after reading it, which is how a GM8 player's ini grows its own
/// defaults. A server that rewrites the operator's config file the first time it
/// starts is a surprise, and one that does it on a file under version control is
/// a diff nobody asked for. Unknown keys and unknown sections are ignored rather
/// than rejected, so a real <c>gg2.ini</c> copied off a hosted server works
/// unedited.
/// </para>
/// <para>
/// ⚠️ <b>Anything absent leaves the option alone.</b> The caller passes an
/// options object already carrying this codebase's defaults, so a key the file
/// does not mention keeps that default rather than picking up the reference's —
/// the two agree everywhere except <see cref="ServerOptions.AnnounceToLobby"/>,
/// where the difference is deliberate and documented there.
/// </para>
/// </remarks>
public static class ServerConfigFile
{
    /// <summary>The ten <c>[Classlimits]</c> keys, in the ini's own spelling.</summary>
    /// <remarks>
    /// ⚠️ <b>The order here is the INI's, not <see cref="PlayerClass"/>'s.</b>
    /// <c>readClasslimitsFromIni.gml</c> writes each into
    /// <c>global.classlimits[CLASS_*]</c> by name, so the pairing is what
    /// matters and the sequence does not — which is exactly why this is a table
    /// of pairs rather than an array anybody could index by accident.
    /// </remarks>
    private static readonly (string Key, PlayerClass Class)[] ClassLimitKeys =
    [
        ("Scout", PlayerClass.Scout),
        ("Pyro", PlayerClass.Pyro),
        ("Soldier", PlayerClass.Soldier),
        ("Heavy", PlayerClass.Heavy),
        ("Demoman", PlayerClass.Demoman),
        ("Medic", PlayerClass.Medic),
        ("Engineer", PlayerClass.Engineer),
        ("Spy", PlayerClass.Spy),
        ("Sniper", PlayerClass.Sniper),
        ("Quote", PlayerClass.Quote),
    ];

    /// <summary>
    /// The twenty built-in maps <c>game_init.gml:213</c> seeds <c>[Maps]</c>
    /// with, in its own order — which is also their default priority, 1-based.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Twenty, and the port ships twenty-two.</b> <c>ctf_oldfort</c> and
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
    /// A key that must be a number is not one. ⚠️ <b>Refused rather than ignored:</b>
    /// a typo in <c>PlayerLimit</c> that silently left the default in place is a
    /// server quietly running a configuration nobody wrote.
    /// </exception>
    public static void Apply(ServerOptions options, string path)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!File.Exists(path))
            throw new FileNotFoundException($"no config file at {path}", path);

        var ini = Parse(File.ReadAllLines(path));

        // --- [Settings] ------------------------------------------------------
        //
        // The reference's hosting port lives here rather than under [Server],
        // which reads as a mistake and is load-bearing: an ini written by the
        // stock client puts it here and a server that looked under [Server]
        // would silently listen on the wrong port.
        options.Port = Int(ini, "Settings", "HostingPort", options.Port);
        options.PlayerLimit = Int(ini, "Settings", "PlayerLimit", options.PlayerLimit);
        options.MultiClientLimit = Int(ini, "Settings", "MultiClientLimit", options.MultiClientLimit);
        options.ServerPlayerName = Text(ini, "Settings", "PlayerName", options.ServerPlayerName);

        // ⚠️ UseLobby is read but the default is NOT the reference's. See
        // ServerOptions.AnnounceToLobby: a headless binary must not publish its
        // address because a config file it inherited said 1. Writing
        // `UseLobby=1` explicitly is a decision and is honoured; leaving it out
        // is not, and leaves this off.
        options.AnnounceToLobby = Bool(ini, "Settings", "UseLobby", options.AnnounceToLobby);

        // --- [Server] --------------------------------------------------------
        options.ServerName = Text(ini, "Server", "ServerName", options.ServerName);
        options.WelcomeMessage = Text(ini, "Server", "WelcomeMessage", options.WelcomeMessage);
        options.Password = Text(ini, "Server", "Password", options.Password);

        // ⚠️ These two used to be deliberately ignored, and the note saying so
        // was right at the time: reading ServerPluginList without implementing
        // plugins would advertise a list the server could not serve. It can now
        // — see ServerPlugins — so the ignore came out with the implementation
        // and not before.
        options.ServerPluginList = Text(ini, "Server", "ServerPluginList", options.ServerPluginList);
        options.ServerPluginsRequired = Bool(
            ini, "Server", "ServerPluginsRequired", options.ServerPluginsRequired);
        options.Autobalance = Bool(ini, "Server", "AutoBalance", options.Autobalance);
        options.MapDownloadBytesPerSecond = Int(
            ini,
            "Server",
            "Total bandwidth limit for map downloads in bytes per second",
            options.MapDownloadBytesPerSecond);

        // ⚠️ The same max/min clamps game_init.gml applies, and they are not
        // decoration: `CapLimit=0` in the reference is a ONE-capture round, not
        // an endless one, because the floor is applied before anything sees it.
        options.CapLimit = (byte)Clamp(Int(ini, "Server", "CapLimit", options.CapLimit), 1, 255);
        options.TimeLimitMinutes =
            (byte)Clamp(Int(ini, "Server", "Time Limit", options.TimeLimitMinutes), 1, 255);
        options.KillLimit = (ushort)Clamp(
            Int(ini, "Server", "Deathmatch Kill Limit", options.KillLimit), 1, 65535);

        // ⚠️ Floored at 0, not 1 — zero really is "no spawn protection" here,
        // which is the one limit in this section with no escape to worry about.
        options.TdmInvulnerabilitySeconds = Clamp(
            Int(ini, "Server", "Team Deathmatch Invulnerability Seconds",
                options.TdmInvulnerabilitySeconds),
            0,
            3600);

        // ⚠️ NOT clamped, deliberately: game_init.gml reads Respawn Time raw and
        // GameServerCreate turns a 0 into a single tick. GameServer does the
        // same, so the escape stays where the reference put it.
        options.RespawnTimeSeconds =
            (byte)Clamp(Int(ini, "Server", "Respawn Time", options.RespawnTimeSeconds), 0, 255);

        options.ShuffleRotation = ShuffleFrom(
            Int(ini, "Server", "ShuffleRotation", (int)options.ShuffleRotation));

        // --- [Classlimits] ---------------------------------------------------
        var limits = options.ClassLimits.Length == TeamBalance.ClassCount
            ? (byte[])options.ClassLimits.Clone()
            : ServerOptions.NoClassLimits();

        foreach (var (key, playerClass) in ClassLimitKeys)
        {
            // readClasslimitFromIni.gml: min(255, max(0, ...)) with 255 as the
            // fallback. ⚠️ A negative in the file becomes 0, which BANS the
            // class rather than unlimiting it.
            limits[(int)playerClass] =
                (byte)Clamp(Int(ini, "Classlimits", key, limits[(int)playerClass]), 0, 255);
        }

        options.ClassLimits = limits;

        // --- the rotation ----------------------------------------------------
        //
        // load_map_rotation.gml: a rotation FILE wins outright, and only when it
        // is absent does the [Maps] priority list apply. ⚠️ The reference falls
        // back to [Maps] when the named file does not EXIST, silently. That is
        // transcribed rather than turned into an error: an operator who
        // mistypes the path gets the default rotation on the stock server too,
        // and diverging here would make the same ini behave differently on the
        // two implementations.
        var rotationFile = Text(ini, "Server", "MapRotation", string.Empty);
        var rotation = rotationFile.Length != 0 && File.Exists(RotationPath(path, rotationFile))
            ? ReadRotationFile(RotationPath(path, rotationFile))
            : PriorityRotation(ini);

        if (rotation.Count != 0)
            options.MapRotation = rotation;
    }

    /// <summary>
    /// A rotation file's path, resolved against the config file's own directory
    /// when it is relative.
    /// </summary>
    /// <remarks>
    /// ⚠️ The reference resolves it against the <em>working directory</em>,
    /// because the game's ini is always beside the executable. A server told
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
    /// checkout's root. ⚠️ <b>A map may appear more than once</b> and the order
    /// is meaningful, so this is a list and not a set — that is how a rotation
    /// plays some maps more often than others.
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
    /// ⚠️ <b>Priority order, not file order.</b> The reference builds a
    /// <c>ds_priority</c> and drains it with <c>delete_min</c>, so the rotation
    /// runs from the lowest number up and the ini's line order is irrelevant.
    /// A map with no key at all gets its 1-based position in the built-in list
    /// as its priority, which is what makes an empty <c>[Maps]</c> section
    /// produce the stock twenty-map rotation rather than nothing.
    /// <para>
    /// ⚠️ <b>Ties are broken by the built-in order</b>, because
    /// <see cref="List{T}.Sort"/> is unstable and <c>ds_priority_delete_min</c>
    /// has its own unspecified answer. Two maps sharing a priority is a
    /// configuration this port makes deterministic and the reference does not;
    /// nothing observable rides on which comes first.
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

        // A map named in [Maps] that is not one of the built-in twenty — a
        // custom map in the server's own Maps directory. The reference never
        // reads those (its loop walks the built-in list only), so this is an
        // addition rather than a transcription, and it is the difference between
        // a config file being able to express a custom-map rotation and not.
        if (ini.TryGetValue("Maps", out var maps))
        {
            var order = DefaultRotation.Length;

            foreach (var (map, value) in maps)
            {
                if (Array.IndexOf(DefaultRotation, map) >= 0)
                    continue;

                var priority = ParseInt("Maps", map, value);

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
    /// ⚠️ <b>Section and key lookups are case-insensitive; values are not.</b>
    /// GM8's <c>ini_read_*</c> is case-insensitive on both, and an operator who
    /// wrote <c>[server]</c> or <c>playerlimit</c> means the same thing. A
    /// password or a server name, though, is data and is taken verbatim.
    /// <para>
    /// ⚠️ <b>A repeated key keeps the LAST one</b>, which is what GM8's own
    /// writer produces after it rewrites a file, and a repeated section merges
    /// into the first rather than replacing it.
    /// </para>
    /// </remarks>
    private static Dictionary<string, Dictionary<string, string>> Parse(string[] lines)
    {
        var ini = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var section = string.Empty;

        foreach (var raw in lines)
        {
            var line = raw.Trim();

            // ⚠️ Both comment markers. GM8 writes none itself, but a hand-edited
            // gg2.ini in the wild carries `;` from the Windows convention and
            // `#` from the rotation file's.
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
        Dictionary<string, Dictionary<string, string>> ini,
        string section,
        string key,
        string fallback) =>
        ini.TryGetValue(section, out var entries) && entries.TryGetValue(key, out var value)
            ? value
            : fallback;

    private static int Int(
        Dictionary<string, Dictionary<string, string>> ini, string section, string key, int fallback)
    {
        if (!ini.TryGetValue(section, out var entries) || !entries.TryGetValue(key, out var value))
            return fallback;

        return ParseInt(section, key, value);
    }

    private static int ParseInt(string section, string key, string value)
    {
        // ⚠️ Parsed as a double and truncated, because ini_read_real is a real
        // read: a file carrying `PlayerLimit=10.0` — which is what GM8 writes
        // for some values — must not be a startup failure.
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            || !double.IsFinite(parsed))
        {
            throw new FormatException(
                $"[{section}] {key}: expected a number, got '{value}'");
        }

        return (int)Math.Clamp(parsed, int.MinValue, int.MaxValue);
    }

    /// <summary>
    /// ⚠️ <b>Any non-zero is true</b>, as every <c>if(global.someFlag)</c> in the
    /// reference is. A file saying <c>AutoBalance=2</c> turns it on.
    /// </summary>
    private static bool Bool(
        Dictionary<string, Dictionary<string, string>> ini, string section, string key, bool fallback) =>
        Int(ini, section, key, fallback ? 1 : 0) != 0;

    /// <summary>
    /// The reference's two branches: 0 is off, 1 avoids opening on arena, and
    /// <b>anything else</b> is a plain shuffle.
    /// </summary>
    private static ShuffleMode ShuffleFrom(int value) => value switch
    {
        0 => ShuffleMode.None,
        1 => ShuffleMode.AvoidArenaFirst,
        _ => ShuffleMode.Any,
    };

    private static int Clamp(int value, int low, int high) => Math.Clamp(value, low, high);
}
