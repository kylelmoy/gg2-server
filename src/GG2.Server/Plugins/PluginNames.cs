namespace GG2.Server.Plugins;

/// <summary>
/// A plugin name and, optionally, the exact zip the server pins it to.
/// </summary>
/// <param name="Name">The plugin's name, as it appears in <c>ServerPluginList</c>.</param>
/// <param name="Md5">
/// The pinned zip hash, or null to resolve one the way
/// <c>Scripts/Plugins/getpluginhashes.gml</c> does.
/// </param>
/// <remarks>
/// <b><c>name@hash</c> is a port EXTENSION, not the reference's syntax.</b>
/// The reference's <c>ServerPluginList</c> holds bare names and
/// <c>checkpluginname</c> rejects an <c>@</c>; the hash is fetched from
/// ganggarrison.com at startup. Pinning is here because this port implements a
/// plugin's server half in C# against <em>one specific version</em> of its GML —
/// see <see cref="ServerPluginCatalogue"/> — and because a test that reached the
/// network would not be a test.
/// </remarks>
public readonly record struct PluginSpec(string Name, string? Md5)
{
    public override string ToString() => Md5 is null ? Name : $"{Name}@{Md5}";
}

/// <summary>
/// Parsing and validation of the <c>ServerPluginList</c> setting.
/// </summary>
public static class PluginNames
{
    /// <summary>
    /// <c>checkpluginname.gml</c>'s alphabet, character for character.
    /// </summary>
    /// <remarks>
    /// No uppercase. The reference's own 404 message says so out loud
    /// ("plugin names are always lowercase"), and the check runs on the server
    /// as well as the client — a capitalised name never reaches the network.
    /// </remarks>
    private const string ValidCharacters = "0123456789abcdefghijklmnopqrstuvwxyz_";

    /// <summary>An md5 as the site serves it: 32 lowercase hex digits.</summary>
    private const int Md5HexLength = 32;

    /// <summary>
    /// The longest <c>ServerPluginList</c> the reference will start with
    /// (<c>game_init.gml:99</c>).
    /// </summary>
    /// <remarks>
    /// Measured on the <b>configured</b> list, before hashes are attached —
    /// the hashed list has its own, much larger limit. Both are transcribed
    /// because a server that starts here and refuses to start on the reference
    /// is a configuration nobody can move.
    /// </remarks>
    public const int MaxConfiguredLength = 254;

    /// <summary>
    /// The longest hashed list that fits the greeting's <c>ushort</c> length
    /// (<c>GameServerCreate.gml:137</c>).
    /// </summary>
    public const int MaxAdvertisedLength = 65535;

    /// <summary><c>checkpluginname.gml</c>: <c>/[a-z0-9_]+/</c>.</summary>
    public static bool IsValid(string name)
    {
        if (name.Length < 1)
            return false;

        foreach (var c in name)
            if (!ValidCharacters.Contains(c, StringComparison.Ordinal))
                return false;

        return true;
    }

    /// <summary>
    /// Splits a comma-separated <c>ServerPluginList</c> into specs, applying
    /// <c>getpluginhashes.gml</c>'s validation.
    /// </summary>
    /// <remarks>
    /// The reference trims each entry (<c>getpluginhashes.gml:13-16</c>), checks
    /// every name and rejects duplicates. All three are reproduced; the errors
    /// it raises with <c>show_message</c> are exceptions here, because a
    /// headless server has nowhere to put a dialog and starting anyway would
    /// advertise a list it cannot serve.
    /// </remarks>
    public static IReadOnlyList<PluginSpec> ParseList(string configured)
    {
        if (configured.Length > MaxConfiguredLength)
            throw new ArgumentException(
                $"server plugin list is {configured.Length} characters; "
                + $"the reference refuses anything over {MaxConfiguredLength}",
                nameof(configured));

        var specs = new List<PluginSpec>();

        // An empty or all-whitespace setting is "no plugins", not "one plugin
        // called nothing". game_init.gml's own guard is string_length(...) != 0
        // around the whole block, so the split never sees the empty string.
        if (configured.Trim().Length == 0)
            return specs;

        foreach (var raw in configured.Split(','))
        {
            var entry = raw.Trim();
            var at = entry.IndexOf('@', StringComparison.Ordinal);

            string name;
            string? md5 = null;

            if (at >= 0)
            {
                name = entry[..at];
                md5 = entry[(at + 1)..].ToLowerInvariant();

                if (md5.Length != Md5HexLength || !IsHex(md5))
                    throw new ArgumentException(
                        $"pinned hash for plugin '{name}' is not 32 hex digits: '{md5}'",
                        nameof(configured));
            }
            else
            {
                name = entry;
            }

            if (!IsValid(name))
                throw new ArgumentException(
                    $"invalid plugin name '{name}' (plugin names are [a-z0-9_]+)",
                    nameof(configured));

            foreach (var existing in specs)
                if (existing.Name == name)
                    throw new ArgumentException(
                        $"duplicate plugin '{name}' in the server plugin list",
                        nameof(configured));

            specs.Add(new PluginSpec(name, md5));
        }

        return specs;
    }

    /// <summary>
    /// The greeting's <c>pluginList</c>: <c>name@md5</c> joined by commas.
    /// </summary>
    /// <remarks>
    /// Byte-for-byte what <c>getpluginhashes.gml:79-83</c> builds, because the
    /// client splits it on <c>,</c> and then on <c>@</c> and derives each
    /// plugin's packet id from its <b>position</b>. A stray space would become
    /// part of a name and the download would 404.
    /// </remarks>
    public static string Advertise(IReadOnlyList<LoadedPlugin> plugins)
    {
        var advertised = string.Join(',', plugins.Select(p => $"{p.Name}@{p.Md5}"));

        if (advertised.Length > MaxAdvertisedLength)
            throw new InvalidOperationException(
                "the hashed plugin list does not fit the greeting's 2-byte length prefix; "
                + "you are requiring too many server-sent plugins");

        return advertised;
    }

    private static bool IsHex(string value)
    {
        foreach (var c in value)
            if (!char.IsAsciiHexDigitLower(c))
                return false;

        return true;
    }
}
