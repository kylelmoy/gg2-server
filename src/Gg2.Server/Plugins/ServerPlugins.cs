namespace Gg2.Server.Plugins;

/// <summary>
/// One advertised plugin: the name and hash on the wire, and the C# server half
/// behind it if this port has one.
/// </summary>
/// <param name="Name">As it appears in the greeting.</param>
/// <param name="Md5">The exact zip every client will download and run.</param>
/// <param name="Implementation">
/// The server half, or null when the plugin is advertised for its client half
/// alone.
/// </param>
public sealed record LoadedPlugin(string Name, string Md5, IServerPlugin? Implementation);

/// <summary>
/// The server's loaded plugin set: what the greeting advertises, and where a
/// <c>PLUGIN_PACKET</c> goes.
/// </summary>
/// <remarks>
/// The reference's <c>global.pluginPacketBuffers</c>, minus the queues. GML has
/// no way to call into a running plugin, so <c>loadserverplugins</c> gives each
/// one a queue and every plugin polls its own every step
/// (<c>PluginPacketGetBuffer</c> / <c>PluginPacketPop</c>). C# can just call the
/// plugin, so it does — the queue was scaffolding for the language, not part of
/// the protocol.
/// <para>
/// ⚠️ <b>What IS part of the protocol is the id, and the id is the index.</b>
/// <c>loadserverplugins.gml:184-206</c> keys the queues by loop counter and
/// passes the same counter to the plugin, and the client builds its list from
/// the same comma-separated string in the same order. So position is the whole
/// binding; nothing on the wire names a plugin.
/// </para>
/// </remarks>
public sealed class ServerPlugins
{
    private readonly LoadedPlugin[] _plugins;

    private ServerPlugins(LoadedPlugin[] plugins, string advertised)
    {
        _plugins = plugins;
        Advertised = advertised;
    }

    /// <summary>A set with nothing in it: an empty advertisement, and every packet id invalid.</summary>
    public static ServerPlugins None { get; } = new([], string.Empty);

    /// <summary>The greeting's <c>pluginList</c> field.</summary>
    public string Advertised { get; }

    public int Count => _plugins.Length;

    public IReadOnlyList<LoadedPlugin> Plugins => _plugins;

    public bool IsEmpty => _plugins.Length == 0;

    /// <summary>
    /// Resolves hashes, instantiates the server halves and binds each one to its
    /// packet id.
    /// </summary>
    /// <remarks>
    /// ⚠️ Startup only, and it can block on the network — see
    /// <see cref="GanggarrisonPluginHashes"/>. Failing here fails the server's
    /// start, which is the reference's behaviour too
    /// (<c>GameServerCreate.gml:131-150</c> calls <c>game_end()</c>): a server
    /// that came up advertising a list it could not serve would strand every
    /// client that tried to join.
    /// </remarks>
    public static ServerPlugins Load(
        IReadOnlyList<PluginSpec> specs,
        IPluginHost host,
        IPluginHashSource hashSource,
        IServerLog log,
        string? debugDirectory = null)
    {
        if (specs.Count == 0)
            return None;

        // ⚠️ The reference's ceiling, and it is a real one: the packet id goes on
        // the wire as a ubyte (PluginPacketSend.gml:42), so id 256 would alias
        // id 0 and cross-wire two plugins.
        if (specs.Count > 256)
            throw new InvalidOperationException(
                $"{specs.Count} plugins: a plugin packet id is one byte, so 256 is the ceiling");

        var loaded = PluginHashes.Resolve(specs, hashSource, log, debugDirectory).ToArray();
        var plugins = new ServerPlugins(loaded, PluginNames.Advertise(loaded));

        for (var i = 0; i < loaded.Length; i++)
            loaded[i].Implementation?.Attach(new PluginChannel(host, i));

        return plugins;
    }

    /// <summary>
    /// Hands a received packet to the plugin that owns
    /// <paramref name="packetId"/>.
    /// </summary>
    /// <returns>
    /// False when no plugin owns the id — <c>_PluginPacketPush</c>'s false, which
    /// the reference answers with <c>KICK_BAD_PLUGIN_PACKET</c>.
    /// </returns>
    /// <remarks>
    /// ⚠️ A known id whose plugin has no C# implementation returns <b>true</b>
    /// and discards the packet. That is deliberate and it is a divergence: the
    /// reference would have run the plugin's GML. The kick is reserved for an id
    /// the client made up, because kicking for a plugin the server itself asked
    /// the client to load would be indefensible from the client's side.
    /// <see cref="PluginHashes.Resolve"/> logs the gap at startup.
    /// </remarks>
    public bool Dispatch(int packetId, int slot, ReadOnlySpan<byte> data)
    {
        if (packetId < 0 || packetId >= _plugins.Length)
            return false;

        _plugins[packetId].Implementation?.Receive(slot, data);
        return true;
    }

    public void PlayerAdded(int slot)
    {
        foreach (var plugin in _plugins)
            plugin.Implementation?.PlayerAdded(slot);
    }

    public void PlayerRemoving(int slot)
    {
        foreach (var plugin in _plugins)
            plugin.Implementation?.PlayerRemoving(slot);
    }

    public void Tick()
    {
        foreach (var plugin in _plugins)
            plugin.Implementation?.Tick();
    }
}
