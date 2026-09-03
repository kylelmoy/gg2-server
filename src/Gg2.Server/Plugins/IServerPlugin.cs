using Gg2.Protocol;

namespace Gg2.Server.Plugins;

/// <summary>
/// What the server half of a server-sent plugin can see and do.
/// </summary>
/// <remarks>
/// The GML surface this stands in for is small and worth naming, because
/// anything outside it is a capability the reference's plugins did not have:
/// <c>global.players</c> (a name, a team and a socket per entry),
/// <c>socket_remote_ip</c>, <c>current_time</c>, <c>global.sendBuffer</c> and
/// <c>Scripts/Plugins/API</c>'s two send functions.
/// <para>
/// ⛔ <b>Nothing here reaches the simulation.</b> A plugin may say things on the
/// wire and may rename a player; it may not move a character, spawn a
/// projectile or draw from <c>WorldState.Random</c>. That stream is observable
/// simulation — every client re-derives a weapon's spread from its position — so
/// a plugin drawing from it would make the game depend on what the plugins were
/// saying. Same rule, and the same reason, as the one CLAUDE.md states for bots.
/// </para>
/// </remarks>
public interface IPluginHost
{
    /// <summary>Roster size, slot 0 and every bot included.</summary>
    int PlayerCount { get; }

    /// <summary><c>global.players[slot].name</c>.</summary>
    string NameOf(int slot);

    /// <summary><c>global.players[slot].team</c>.</summary>
    Team TeamOf(int slot);

    /// <summary>
    /// <c>socket_remote_ip(player.socket)</c>, or the empty string for a bot
    /// and for the server's own slot 0.
    /// </summary>
    /// <remarks>
    /// ⚠️ Not a stable identity — it is what the reference's plugins had, and
    /// what chat_v2's mute list keys on, so it is what this exposes. Two players
    /// behind one NAT share it.
    /// </remarks>
    string RemoteAddressOf(int slot);

    /// <summary>Whether this slot is a network client, rather than a bot or slot 0.</summary>
    bool IsClient(int slot);

    /// <summary>
    /// GML's <c>current_time</c>, in milliseconds.
    /// </summary>
    /// <remarks>
    /// ⚠️ Sampled once per tick and constant within it. GM8 re-reads the clock
    /// on every call, but nothing a plugin does inside one tick takes long
    /// enough to see the difference — and a per-tick value is the one a test can
    /// drive.
    /// </remarks>
    long CurrentTimeMilliseconds { get; }

    /// <summary>
    /// Renames a player and broadcasts <c>PLAYER_CHANGENAME</c>, exactly as a
    /// plugin writing to <c>global.sendBuffer</c> would.
    /// </summary>
    void Rename(int slot, string name);

    /// <summary>
    /// <c>PluginPacketSend</c>: appends to the broadcast buffer, so every client
    /// gets it in this tick's flush.
    /// </summary>
    void SendPluginPacketToAll(int packetId, ReadOnlySpan<byte> data);

    /// <summary>
    /// <c>PluginPacketSendTo</c>: queues on one client's socket. A slot with no
    /// socket — a bot, or slot 0 — is silently skipped, which is the reference's
    /// own short-circuit for <c>global.myself</c>.
    /// </summary>
    void SendPluginPacketTo(int slot, int packetId, ReadOnlySpan<byte> data);

    /// <summary>The server log, for anything a plugin would have drawn on screen.</summary>
    void Log(string message);
}

/// <summary>
/// One plugin's bound view of the host: the host plus this plugin's packet id.
/// </summary>
/// <remarks>
/// The reference hands a plugin its id once, as <c>argument1</c>, and every
/// plugin stashes it in a global (<c>global.chatPacketID</c>). This is that,
/// with the id no longer something a plugin can get wrong.
/// <para>
/// ⚠️ <b>The id is the plugin's INDEX in the advertised list.</b> Both sides
/// derive it from position and nothing carries it —
/// <c>loadserverplugins.gml:194-206</c> uses the loop counter for the queue key
/// and for <c>env.packetID</c> alike. Reordering <c>ServerPluginList</c>
/// renumbers every plugin on both sides at once, which is why it is safe;
/// changing the order on only one side is a silent cross-wiring.
/// </para>
/// </remarks>
public sealed class PluginChannel(IPluginHost host, int packetId)
{
    public IPluginHost Host { get; } = host;

    /// <summary>This plugin's <c>packetID</c>: its position in the advertised list.</summary>
    public int PacketId { get; } = packetId;

    public void Broadcast(ReadOnlySpan<byte> data) => Host.SendPluginPacketToAll(PacketId, data);

    public void SendTo(int slot, ReadOnlySpan<byte> data) => Host.SendPluginPacketTo(slot, PacketId, data);
}

/// <summary>
/// The server half of a server-sent plugin, written in C#.
/// </summary>
/// <remarks>
/// ⛔ <b>This does not run GML and never will.</b> A GG2 plugin ships as a zip of
/// GML that the client downloads from ganggarrison.com and executes inside Game
/// Maker 8 (<c>loadserverplugins.gml</c>). The client still does exactly that.
/// What this port replaces is the <em>host's</em> copy of the same script — the
/// branches guarded by <c>global.isHost</c> — with C# that speaks the same
/// packets. So a stock client with the plugin loaded cannot tell the difference,
/// and the port carries no GML.
/// <para>
/// ⚠️ <b>An implementation is therefore tied to one version of the plugin.</b>
/// <c>ServerPluginCatalogue</c> pins the zip hash each implementation was
/// written against, and the loader shouts when the hash actually advertised is a
/// different one. Without that pin, an upstream re-release changes what the
/// clients run and nothing on this side moves.
/// </para>
/// </remarks>
public interface IServerPlugin
{
    /// <summary>The name in <c>ServerPluginList</c>, e.g. <c>chat_v2_pr</c>.</summary>
    string Name { get; }

    /// <summary>
    /// Called once, before any other member, with this plugin's packet id.
    /// </summary>
    void Attach(PluginChannel channel);

    /// <summary>
    /// A roster entry appeared at <paramref name="slot"/>, which is always the
    /// end of the roster.
    /// </summary>
    /// <remarks>
    /// The reference's <c>Player</c> Create event. Slot 0 gets one too, at
    /// startup, so a plugin's per-player table lines up with the roster from the
    /// first tick.
    /// </remarks>
    void PlayerAdded(int slot);

    /// <summary>
    /// The entry at <paramref name="slot"/> is about to be removed, and every
    /// index above it is about to shift down by one.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Called BEFORE the removal</b>, so <see cref="IPluginHost"/> still
    /// answers for the departing player. Anything a plugin stores per player has
    /// to compact here — see <see cref="PluginPlayerTable{T}"/>, which exists so
    /// that is one line rather than a bug that lands on the wrong player some
    /// seconds after an unrelated disconnect.
    /// </remarks>
    void PlayerRemoving(int slot);

    /// <summary>Once a tick, after client commands have been processed.</summary>
    void Tick();

    /// <summary>
    /// A <c>PLUGIN_PACKET</c> for this plugin arrived from
    /// <paramref name="slot"/>.
    /// </summary>
    /// <param name="data">
    /// The packet body: everything after the container's <c>packetID</c> byte.
    /// Valid for this call only.
    /// </param>
    void Receive(int slot, ReadOnlySpan<byte> data);
}

/// <summary>
/// Default no-op bodies, so a plugin implements only the events it uses.
/// </summary>
public abstract class ServerPlugin : IServerPlugin
{
    private PluginChannel? _channel;

    public abstract string Name { get; }

    /// <summary>This plugin's bound host view. Throws until <see cref="Attach"/>.</summary>
    protected PluginChannel Channel =>
        _channel ?? throw new InvalidOperationException($"plugin {Name} used before Attach");

    protected IPluginHost Host => Channel.Host;

    public virtual void Attach(PluginChannel channel) => _channel = channel;

    public virtual void PlayerAdded(int slot) { }

    public virtual void PlayerRemoving(int slot) { }

    public virtual void Tick() { }

    public virtual void Receive(int slot, ReadOnlySpan<byte> data) { }
}
