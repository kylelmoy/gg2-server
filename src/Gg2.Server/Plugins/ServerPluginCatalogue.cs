namespace Gg2.Server.Plugins;

/// <summary>
/// The plugins whose server half this port implements.
/// </summary>
/// <remarks>
/// <b>Each entry pins the zip it was written against, and that pin is load
/// bearing.</b> A plugin's two halves are one script: the client runs the GML
/// the site serves, and this port runs C# transcribed from one version of it. If
/// the site re-releases the plugin, the client half moves and the C# half does
/// not — and the two only disagree on whichever packets changed, which is a
/// failure that looks like "chat sometimes doesn't work". <see cref="PluginHashes"/>
/// advertises the pin by default and shouts when something else overrides it.
/// <para>
/// <b>Re-check the pin before changing a plugin implementation.</b> If you
/// port a newer version of a plugin's GML, move the hash in the same commit and
/// say which zip you read. A pin that no longer names the file the code was
/// written from is worse than none, because it claims a guarantee it is not
/// giving.
/// </para>
/// </remarks>
public static class ServerPluginCatalogue
{
    /// <param name="ChatConsole">
    /// Whether this plugin's server half runs the slash-command console — which
    /// is what makes <c>[Server] ChatJoinMessage</c> and <c>[Bots]
    /// SuspendMinutes</c> reach anybody. Recorded here rather than asked of a
    /// live plugin because a settings page has a plugin LIST and no server: the
    /// fleet editor configures machines that are not running.
    /// </param>
    private sealed record Entry(string Md5, bool ChatConsole, Func<IServerPlugin> Create);

    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.Ordinal)
    {
        // chat — "Lorgan3's chat plugin v6", from
        // http://www.ganggarrison.com/plugins/chat@bad8081d64f0b808a2bb3a6d88978fb5.zip
        // (md5 of the zip confirmed to equal the hash in its own filename).
        // Re-check http://www.ganggarrison.com/plugins/chat.md5 before changing
        // this implementation; a difference means the client half has moved and
        // nobody has read the new GML.
        ["chat"] = new("bad8081d64f0b808a2bb3a6d88978fb5", true, () => new ChatPlugin()),

        // chat_v2_pr — "chat_v2 by Lorgan, various fixes by PrOF", plugin.gml
        // dated 2017-04-10, from
        // http://www.ganggarrison.com/plugins/chat_v2_pr@46d0937c9206dcf27e036100d1ea8685.zip
        // (md5 of the zip confirmed to equal the hash in its own filename, and
        // to match http://www.ganggarrison.com/plugins/chat_v2_pr.md5).
        // A DIFFERENT plugin from "chat" above, not an older one: separate
        // hashes, separate packet formats, and no vote dialog.
        ["chat_v2_pr"] = new("46d0937c9206dcf27e036100d1ea8685", true, () => new ChatV2Plugin()),

        // chat2026 — not on ganggarrison.com yet. The pin is the md5 of
        // dist/chat2026.zip built by `node tools/build.js` at gg2-chat2026 commit
        // b46bbea (D:\Code\gg2-chat2026), whose PROTOCOL.md version 1 is what
        // Chat2026Plugin speaks. Until it is published, clients load it from
        // ServerPluginsDebug/chat2026.zip, and so must this server
        // (PluginHashes.Resolve's second choice), or they will disagree on the hash.
        ["chat2026"] = new("f5c27638f1eedebe1faa72c187726ed9", true, () => new Chat2026Plugin()),
    };

    /// <summary>Every plugin name this port can serve, ascending.</summary>
    public static IReadOnlyList<string> Names { get; } = Entries.Keys.Order(StringComparer.Ordinal).ToArray();

    /// <summary>
    /// A fresh instance of <paramref name="name"/>'s server half, or null if
    /// this port has none.
    /// </summary>
    public static IServerPlugin? Create(string name) =>
        Entries.TryGetValue(name, out var entry) ? entry.Create() : null;

    /// <summary>
    /// Whether <paramref name="pluginList"/> — a raw <c>ServerPluginList</c> —
    /// names any plugin whose server half runs the slash-command console.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>What makes a "this setting does nothing here" warning possible.</b>
    /// <c>[Server] ChatJoinMessage</c> and <c>[Bots] SuspendMinutes</c> are
    /// delivered by a chat plugin and by nothing else, and
    /// <c>ServerPluginList</c> is empty by default — so the obvious way to
    /// configure either is to set it on a server that can never show it to
    /// anybody, with nothing saying so.
    /// <para>
    /// A malformed list answers "no chat console" rather than throwing: every
    /// caller is a diagnostic, and the list itself is refused with a proper
    /// message by <see cref="PluginNames.ParseList"/> on the path that matters.
    /// </para>
    /// </remarks>
    public static bool ListHasChatConsole(string pluginList)
    {
        IReadOnlyList<PluginSpec> specs;

        try
        {
            specs = PluginNames.ParseList(pluginList);
        }
        catch (ArgumentException)
        {
            return false;
        }

        foreach (var spec in specs)
            if (Entries.TryGetValue(spec.Name, out var entry) && entry.ChatConsole)
                return true;

        return false;
    }

    /// <summary>
    /// The zip hash <paramref name="name"/>'s implementation was written
    /// against, or null if this port has no implementation of it.
    /// </summary>
    public static string? PinnedMd5(string name) =>
        Entries.TryGetValue(name, out var entry) ? entry.Md5 : null;
}
