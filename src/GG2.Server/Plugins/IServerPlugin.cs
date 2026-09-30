using GG2.Protocol;

namespace GG2.Server.Plugins;

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
/// <b>Nothing here reaches the simulation DIRECTLY.</b> A plugin can end a
/// round, move a player across teams and kick somebody — the reference's own
/// vote plugins do all three — but every one of those goes through the same
/// <see cref="GameServer"/> method a client command or the control port uses.
/// A plugin may not touch <c>WorldState</c>, and in particular may not draw from
/// <c>WorldState.Random</c>: that stream is observable simulation, every client
/// re-derives a weapon's spread from its position, so a plugin drawing from it
/// would make the game depend on what the plugins were saying. Use
/// <see cref="Random"/> here instead. Same rule, and the same reason, as the one
/// CLAUDE.md states for bots — a plugin must not be able to produce a state a
/// client could not.
/// </para>
/// <para>
/// <b>Anything added here is a capability the reference's plugins had</b>,
/// routed through an existing server operation. Adding one that is not is a
/// decision, not a convenience.
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
    /// Not a stable identity — it is what the reference's plugins had, and
    /// what chat_v2's mute list keys on, so it is what this exposes. Two players
    /// behind one NAT share it.
    /// </remarks>
    string RemoteAddressOf(int slot);

    /// <summary>Whether this slot is a network client, rather than a bot or slot 0.</summary>
    bool IsClient(int slot);

    /// <summary>
    /// GML's <c>current_time</c>: milliseconds since the process started.
    /// </summary>
    /// <remarks>
    /// <b>MONOTONIC, and that is not a preference.</b> Everything a plugin
    /// measures with this is an elapsed duration — a spam window, a vote clock, a
    /// rename throttle — and every one of them is written as
    /// <c>now - then &gt; limit</c>. Back it with a wall clock and a DST shift or
    /// an NTP correction backwards stalls all of them until real time catches up,
    /// silently, on a machine nobody is looking at. There are THREE clocks in
    /// this server for three different needs — a local calendar date for
    /// <c>calculateAprilFools</c>, wall-clock instants for connection timeouts,
    /// and this. <b>Do not collapse them into one field.</b>
    /// <para>
    /// Sampled once per tick and constant within it. GM8 re-reads the clock on
    /// every call, but nothing a plugin does inside one tick takes long enough to
    /// see the difference — and a per-tick value is the one a test can drive.
    /// </para>
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

    // --- what the vote commands need ----------------------------------------

    /// <summary>
    /// An id for the player at <paramref name="slot"/> that does not move when
    /// the roster compacts.
    /// </summary>
    /// <remarks>
    /// <b>The only safe way to remember a player across time.</b> A vote runs
    /// for thirty seconds; a slot index means something different the moment
    /// anybody disconnects, so a vote holding one would kick whoever inherited
    /// the number. The reference's plugin API has exactly this as
    /// <c>Player.gid</c>, which is some evidence it is the right shape.
    /// <b>Never put it on the wire</b> — clients name players by position.
    /// </remarks>
    long StableIdOf(int slot);

    /// <summary>The slot holding <paramref name="stableId"/>, or -1 if they left.</summary>
    int SlotOfStableId(long stableId);

    /// <summary>Disconnects a player with a <c>KICK</c> they can read a reason for.</summary>
    bool Kick(int slot, string reason);

    /// <summary>
    /// Moves a player onto <paramref name="team"/>, skipping the balance
    /// refusal a client's own request goes through — and skipping nothing else.
    /// </summary>
    void SetTeam(int slot, Team team);

    /// <summary>Ends the round with nobody winning, and rotates.</summary>
    bool EndRound();

    /// <summary>
    /// Makes the next round's map <paramref name="map"/>. False if there is no
    /// such map.
    /// </summary>
    bool SetNextMap(string map);

    /// <summary>The maps a map vote may offer, in configuration order.</summary>
    IReadOnlyList<string> VotableMaps { get; }

    /// <summary>
    /// <c>global.map_rotation</c>: the maps this server plays, in order.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="VotableMaps"/>, which falls back past the
    /// rotation to everything on disk. <c>/maplist</c> reports the rotation
    /// because that is what the next round will actually come from.
    /// </remarks>
    IReadOnlyList<string> MapRotation { get; }

    /// <summary>
    /// <c>GameServer.currentMapIndex</c>: where in <see cref="MapRotation"/> the
    /// current round sits, or 0 when there is no rotation.
    /// </summary>
    int CurrentMapIndex { get; }

    /// <summary>
    /// <c>Player.stats[POINTS]</c>, floored — what the reference's player picker
    /// puts in brackets after each name.
    /// </summary>
    int PointsOf(int slot);

    // --- this port's own, and each one is a decision -------------------------
    //
    // ⚠️ Nothing below stands in for a GML surface. The reference's plugins
    // could not reach any of it, because stock GG2 has no bots and no
    // server-configured chat line. They are here rather than inside a plugin
    // because both are SERVER state — one server, one bot population, one
    // configured line — and a plugin holding either would give two loaded chat
    // plugins two different answers.
    // docs/PLUGINS.md, "the bot suspension and the join line".

    /// <summary>
    /// <c>[Server] ChatJoinMessage</c>: a line to send a player as they join
    /// chat, or empty for none.
    /// </summary>
    /// <remarks>
    /// A plain read of configuration. Sending it is the plugin's, because only
    /// the plugin knows when a client has announced itself and what a line of
    /// chat looks like on its own wire.
    /// </remarks>
    string ChatJoinMessage { get; }

    /// <summary>What <c>/bots</c> may do, and what the population is doing.</summary>
    BotStatus Bots { get; }

    /// <summary>
    /// Holds the bot population at zero for <see cref="BotStatus.SuspendMinutes"/>.
    /// </summary>
    /// <returns>
    /// False, and nothing changed, when the server is not configured for it —
    /// which is the default.
    /// </returns>
    /// <remarks>
    /// ⛔ <b>It does not reach the simulation and it is not a client
    /// capability.</b> What it moves is the population manager's target, which is
    /// the server's own scheduling decision about how many sockets-that-are-not-
    /// sockets to hold open. Removing a bot is the same path a disconnection
    /// takes, so nothing here can produce a world a client could not.
    /// </remarks>
    bool SuspendBots();

    /// <summary>Ends a running suspension early.</summary>
    /// <returns>False when none was running.</returns>
    bool ResumeBots();

    /// <summary>
    /// A line of player chat was delivered: record it for the admin panel's chat
    /// window.
    /// </summary>
    /// <remarks>
    /// A write to the operator's side only — nothing goes on the wire, and a
    /// server without the panel drops it. Called for what a player said (and
    /// what slot 0 said through <see cref="IServerChat"/>), not for a command or
    /// the plugin's own announcements, which are in the server log.
    /// </remarks>
    void ChatHeard(int slot, string message, bool teamOnly);

    /// <summary>
    /// A random source for a plugin's own shuffles and tie-breaks.
    /// </summary>
    /// <remarks>
    /// <b>NOT <c>WorldState.Random</c>.</b> That one is observable simulation
    /// and a plugin drawing from it would move every client's re-derived weapon
    /// spread. This is the server's own, and nothing on the wire depends on it.
    /// </remarks>
    Random Random { get; }
}

/// <summary>
/// A chat plugin that can speak as the server's own slot-0 player — the admin
/// panel's chat box.
/// </summary>
/// <remarks>
/// Each plugin puts the line on its own wire the way it would put a player's,
/// so a stock client with the plugin draws it as chat from the roster's slot 0.
/// The reference's host could do the same from its own chat window; a headless
/// server has no window, so the panel is where the text comes from.
/// </remarks>
public interface IServerChat
{
    /// <summary>Sends <paramref name="message"/> to everybody in chat, as slot 0.</summary>
    void SayAsServer(string message);
}

/// <summary>
/// One plugin's bound view of the host: the host plus this plugin's packet id.
/// </summary>
/// <remarks>
/// The reference hands a plugin its id once, as <c>argument1</c>, and every
/// plugin stashes it in a global (<c>global.chatPacketID</c>). This is that,
/// with the id no longer something a plugin can get wrong.
/// <para>
/// <b>The id is the plugin's INDEX in the advertised list.</b> Both sides
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
/// <b>This does not run GML and never will.</b> A GG2 plugin ships as a zip of
/// GML that the client downloads from ganggarrison.com and executes inside Game
/// Maker 8 (<c>loadserverplugins.gml</c>). The client still does exactly that.
/// What this port replaces is the <em>host's</em> copy of the same script — the
/// branches guarded by <c>global.isHost</c> — with C# that speaks the same
/// packets. So a stock client with the plugin loaded cannot tell the difference,
/// and the port carries no GML.
/// <para>
/// <b>An implementation is therefore tied to one version of the plugin.</b>
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
    /// <b>Called BEFORE the removal</b>, so <see cref="IPluginHost"/> still
    /// answers for the departing player. Anything a plugin stores per player has
    /// to compact here — see <see cref="PluginPlayerTable{T}"/>, which exists so
    /// that is one line rather than a bug that lands on the wrong player some
    /// seconds after an unrelated disconnect.
    /// </remarks>
    void PlayerRemoving(int slot);

    /// <summary>Once a tick, after client commands have been processed.</summary>
    void Tick();

    /// <summary>
    /// A new round has started on <paramref name="map"/> — a rotation, a vote,
    /// or the server opening its first map.
    /// </summary>
    /// <remarks>
    /// Fired for the FIRST map too, before any client can connect, so a
    /// plugin need not special-case startup. Every roster entry is a spectator
    /// again by the time this runs, on a first-area change.
    /// </remarks>
    void MapChanged(string map);

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

    public virtual void MapChanged(string map) { }

    public virtual void Receive(int slot, ReadOnlySpan<byte> data) { }
}
