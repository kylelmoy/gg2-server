using System.Text;

namespace GG2.Server;

/// <summary>
/// Reads a <c>gg2.json</c> config file into a <see cref="ServerOptions"/> and
/// writes one back, plus the rotation file its <c>MapRotation</c> key points at.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ServerSettings"/> is the table of what is read;
/// <see cref="ConfigDocument"/> is the JSON grammar around it, and this file
/// is the rules between the two. The keys under <c>Settings</c>, <c>Server</c>,
/// <c>Classlimits</c> and <c>Maps</c> are the reference's <c>gg2.ini</c> keys
/// (<c>Scripts/Game/game_init.gml:66-106</c>,
/// <c>Scripts/Game/load_map_rotation.gml</c>); <see cref="IniConverter"/> turns
/// a reference-format ini into this format.
/// </para>
/// <para>
/// <b>Unknown keys and unknown sections are ignored rather than rejected</b>, so
/// a file written for a newer release still starts an older one, and so
/// <see cref="Write"/> can preserve them.
/// </para>
/// <para>
/// <b>Anything absent leaves the option alone.</b> The caller passes an options
/// object already carrying this codebase's defaults, so a key the file does not
/// mention keeps that default.
/// </para>
/// <para>
/// ⚠️ <b><see cref="Apply"/> never rewrites the file, and <see cref="Write"/>
/// only rewrites what it must</b>: it edits keys in place and leaves unknown keys
/// and the file's order where they were.
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
    /// behaviour. Naming either in <c>Maps</c> still works — an explicit key
    /// wins over the default list.
    /// </remarks>
    internal static readonly string[] DefaultRotation =
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
    /// Not JSON, a key that must be a number is not one, or a choice is not one
    /// of the choices. <b>Refused rather than ignored:</b> a typo in
    /// <c>PlayerLimit</c> that silently left the default in place is a server
    /// quietly running a configuration nobody wrote.
    /// </exception>
    public static void Apply(ServerOptions options, string path)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!File.Exists(path))
            throw new FileNotFoundException($"no config file at {path}", path);

        ApplyText(options, File.ReadAllText(path), path);
    }

    /// <summary>
    /// <see cref="Apply"/> over text already in memory. <paramref name="path"/>
    /// is still needed: a relative <c>Server.MapRotation</c> resolves against it.
    /// </summary>
    public static void ApplyText(ServerOptions options, string text, string path)
    {
        ArgumentNullException.ThrowIfNull(options);
        ApplyDocument(options, ConfigDocument.Parse(text), path);
    }

    /// <summary>The half of <see cref="Apply"/> that works from a parsed document.</summary>
    internal static void ApplyDocument(ServerOptions options, ConfigDocument config, string path)
    {
        // ⚠️ IN TABLE ORDER, not in the file's. Two rows depend on running after
        // another — Web.Enabled after Web.Port, Runtime.ControlPortEnabled after
        // Runtime.ControlPort — and the file's order is whatever the operator's
        // editor left behind. ServerSettings says so at both rows.
        foreach (var setting in ServerSettings.All)
        {
            if (config.TryGet(setting.Section, setting.Key, out var value))
            {
                setting.Set(options, value);
                options.ExplicitSettings.Add(setting.Property);
            }
        }

        // --- the rotation ----------------------------------------------------
        //
        // Three sources with a fixed precedence, and the order is the point:
        //
        //  1. Server.Rotation, a list. THIS PORT'S OWN key and the one Write
        //     emits, so it has to win — a rotation saved from the admin panel
        //     that lost to a Maps section left in the same file is a panel edit
        //     that silently does nothing. Applied by the table walk above, so
        //     all this branch does is decline to overwrite it.
        //
        //  2. Server.MapRotation, a rotation FILE. load_map_rotation.gml. The
        //     reference falls back to [Maps] when the named file does not EXIST,
        //     silently; that is transcribed rather than turned into an error.
        //
        //  3. Maps, the priority list.
        if (config.Text("Server", "Rotation").Length == 0)
        {
            var rotationFile = config.Text("Server", "MapRotation");
            var rotation = rotationFile.Length != 0 && File.Exists(RotationPath(path, rotationFile))
                ? ReadRotationFile(RotationPath(path, rotationFile))
                : PriorityRotation(config);

            if (rotation.Count != 0)
                options.MapRotation = rotation;
        }

        // ⚠️ GameServerCreate.gml:110-113: a server opens on nextMapInRotation()
        // only when no map was NAMED. Expressed by emptying Map, which is
        // global.launchMap == "" — see ServerOptions.Map.
        //
        // ⛔ Applied HERE and not only in CommandLine, because "what would this
        // file alone produce" is a question two other things ask: the admin
        // panel's settings page, and CommandLine's own override diff. Without it
        // both compared a file-loaded Map of ctf_truefort against a running Map
        // of "" and reported the opening map as changed on every server with a
        // rotation.
        if (!config.Has("Server", "Map") && options.MapRotation.Count != 0)
            options.Map = string.Empty;
    }

    /// <summary>
    /// Writes every setting in <paramref name="options"/> back to
    /// <paramref name="path"/>, preserving whatever else is in the file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An EDIT, not a regeneration.</b> Key order and keys this project does
    /// not know about are left where they were; a managed key that already
    /// exists has its value replaced in place, and one that does not is appended
    /// to its section (the section itself appended at the end if it is missing).
    /// </para>
    /// <para>
    /// ⚠️ <b>The two lower-precedence rotation sources are REMOVED when they
    /// disagree with the rotation being written.</b> <c>Server.MapRotation</c>
    /// and the <c>Maps</c> priority section both lose to <c>Server.Rotation</c>
    /// on the way back in, so leaving a stale one behind would be a config file
    /// that reads differently from the one an operator just saved. They are left
    /// alone when they already agree, so a file that uses a rotation file keeps
    /// using it.
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

        var text = Edit(options, File.Exists(path) ? File.ReadAllText(path) : string.Empty, path);

        // ⚠️ Same directory as the target, not the temp directory: File.Move
        // across a volume boundary is a copy, which is not atomic and defeats the
        // whole point. GetDirectoryName is null only for a bare filename, where
        // the current directory is the right answer.
        var directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        var temporary = Path.Combine(directory, Path.GetFileName(path) + ".tmp");

        File.WriteAllText(temporary, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>
    /// <see cref="Write"/>'s edit, over text in memory: what the file would say
    /// after saving <paramref name="options"/> into it.
    /// </summary>
    public static string Edit(ServerOptions options, string existing, string path)
    {
        var config = ConfigDocument.Parse(existing);

        // ⚠️ What the file ALREADY says, so this can write a minimal diff.
        //
        // ⛔ Writing every key unconditionally is what this replaced, and it was
        // wrong in a way only a real save showed: two of the settings —
        // MapDirectory and Nav Directory — default to paths RESOLVED FROM DISK,
        // so the first save stamped this machine's absolute install path into the
        // operator's config file and pinned it there. It also turned a
        // twelve-line hand-written file into eighty lines of defaults.
        //
        // A key is written when the file already names it (so an operator's own
        // key keeps its place and gets the new value) or when the value differs
        // from what the file alone would produce (so a real change is recorded).
        // Everything else stays unwritten and keeps meaning "the default".
        var baseline = ServerOptions.CreateDefault();
        ApplyDocument(baseline, config, path);

        var named = ServerSettings.All
            .Where(setting => config.Has(setting.Section, setting.Key))
            .ToHashSet();

        foreach (var setting in ServerSettings.All)
        {
            var value = setting.Get(options);

            if (named.Contains(setting) || value != setting.Get(baseline))
                config.Set(setting, value);
        }

        RemoveShadowedRotationSources(config, options, path);
        return config.ToText();
    }

    /// <summary>
    /// Drops <c>Server.MapRotation</c> and the <c>Maps</c> section when they
    /// would name a rotation other than the one just written.
    /// </summary>
    private static void RemoveShadowedRotationSources(ConfigDocument config, ServerOptions options, string path)
    {
        var rotationFile = config.Text("Server", "MapRotation");
        var written = string.Join(',', options.MapRotation);

        // ⚠️ Resolved against the config file's own directory, exactly as Apply
        // resolves it. Reading it as a working-directory path here would make the
        // two disagree about whether the file agrees, and the writer would delete
        // a MapRotation key that was in fact the source.
        var resolved = rotationFile.Length == 0 ? string.Empty : RotationPath(path, rotationFile);

        var fileAgrees = resolved.Length != 0
            && File.Exists(resolved)
            && string.Join(',', ReadRotationFile(resolved)) == written;

        // The Maps section is judged as it stood BEFORE MapRotation goes.
        var mapsAgree = string.Join(',', PriorityRotation(config)) == written;

        if (rotationFile.Length != 0 && !fileAgrees)
            config.Remove("Server", "MapRotation");

        // The Maps priority section only matters when there is no rotation file
        // in front of it. Left alone when the file is the source, because then it
        // was already inert and removing it is a diff nobody asked for.
        if (!fileAgrees && !mapsAgree)
            config.RemoveSection("Maps");
    }

    /// <summary>
    /// A rotation file's path, resolved against the config file's own directory
    /// when it is relative.
    /// </summary>
    /// <remarks>
    /// The reference resolves it against the <em>working directory</em>, because
    /// the game's ini is always beside the executable. A server told
    /// <c>--config /etc/gg2/server.json</c> is not in that position, and
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
    /// <c>game_init.gml:205-224</c>: the <c>Maps</c> section as a priority
    /// queue, with a value of 0 meaning "never play this map".
    /// </summary>
    /// <remarks>
    /// <b>Priority order, not file order.</b> The reference builds a
    /// <c>ds_priority</c> and drains it with <c>delete_min</c>, so the rotation
    /// runs from the lowest number up. A map with no key at all gets its 1-based
    /// position in the built-in list as its priority, which is what makes an
    /// empty <c>Maps</c> section produce the stock twenty-map rotation rather
    /// than nothing.
    /// <para>
    /// <b>Ties are broken by the built-in order</b>, because
    /// <see cref="List{T}.Sort(Comparison{T})"/> is unstable and
    /// <c>ds_priority_delete_min</c> has its own unspecified answer.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<string> PriorityRotation(ConfigDocument config)
    {
        var ranked = new List<(int Priority, int Order, string Map)>();

        for (var i = 0; i < DefaultRotation.Length; i++)
        {
            var map = DefaultRotation[i];
            var priority = config.TryGet("Maps", map, out var value)
                ? ServerSettings.Number($"Maps.{map}", value)
                : i + 1;

            if (priority != 0)
                ranked.Add((priority, i, map));
        }

        // A map named in Maps that is not one of the built-in twenty — a custom
        // map in the server's own Maps directory. The reference never reads those
        // (its loop walks the built-in list only), so this is an addition rather
        // than a transcription, and it is the difference between a config file
        // being able to express a custom-map rotation and not.
        var order = DefaultRotation.Length;

        foreach (var (map, value) in config.Entries("Maps"))
        {
            if (DefaultRotation.Contains(map, StringComparer.OrdinalIgnoreCase))
                continue;

            var priority = ServerSettings.Number($"Maps.{map}", value);

            if (priority != 0)
                ranked.Add((priority, order++, map));
        }

        ranked.Sort((a, b) =>
            a.Priority != b.Priority ? a.Priority.CompareTo(b.Priority) : a.Order.CompareTo(b.Order));

        return ranked.ConvertAll(entry => entry.Map);
    }
}
