using GG2.Protocol;
using GG2.Simulation;
using GG2.Server.Plugins;

namespace GG2.Server;

public sealed partial class GameServer
{
    // --- server-sent plugins -------------------------------------------------

    /// <summary>The greeting's <c>pluginList</c> field: <c>name@md5</c>, comma-separated.</summary>
    public string AdvertisedPlugins => _plugins.Advertised;

    /// <summary>
    /// Resolves the configured plugin list to hashes and instantiates the server
    /// halves this port has.
    /// </summary>
    /// <remarks>
    /// ⚠️ Throwing here fails <see cref="Start"/>, which is the reference's
    /// <c>game_end()</c> on the same failure. A server that came up anyway would
    /// advertise a list every client then failed to download, and the client's
    /// report of that is a dialog on the player's screen and nothing on the
    /// server's.
    /// </remarks>
    private void LoadPlugins()
    {
        var specs = PluginNames.ParseList(_options.ServerPluginList);

        // The server's own chat. A stock client gets chat only by downloading the
        // `chat` plugin, so with chat on the server advertises it whether or not
        // the list names it, appended so every listed plugin keeps its packet id.
        // ServerOptions.Validate refuses `chat` listed with chat off, and a second
        // chat plugin with it on.
        ServerChat? chat = null;
        if (_options.Chat.Enabled)
        {
            chat = new ServerChat();
            if (!specs.Any(spec => spec.Name == ServerChat.PluginName))
                specs = [.. specs, new PluginSpec(ServerChat.PluginName, null)];
        }

        if (specs.Count == 0)
        {
            _plugins = ServerPlugins.None;
            return;
        }

        var owned = PluginHashSource is null ? new GanggarrisonPluginHashes() : null;
        try
        {
            _plugins = ServerPlugins.Load(
                specs, this, PluginHashSource ?? owned!, _log,
                native: name => name == ServerChat.PluginName ? chat : null);
        }
        finally
        {
            owned?.Dispose();
        }

        _plugins.PlayerAdded(0);

        _log.Info(
            $"server-sent plugins ({(_options.ServerPluginsRequired ? "required" : "optional")}): "
            + _plugins.Advertised);
    }

    // --- IPluginHost ---------------------------------------------------------
    //
    // The whole surface a plugin gets. Everything here is something the
    // reference's plugins could reach through global.players, socket_remote_ip,
    // current_time and Scripts/Plugins/API; nothing here reaches the simulation.

    Team IPluginHost.TeamOf(int slot) => _roster[slot].Team;

    string IPluginHost.RemoteAddressOf(int slot) =>
        _roster[slot].Connection?.RemoteAddress.ToString() ?? string.Empty;

    bool IPluginHost.IsClient(int slot) => _roster[slot].Connection is not null;

    bool IPluginHost.IsAlive(int slot) => _world.IsAlive(slot);

    bool IPluginHost.RoundOver => _world.MapChanging || _world.RoundDecided;

    long IPluginHost.CurrentTimeMilliseconds => _pluginClock;

    void IPluginHost.Log(string message) => _log.Info(message);

    long IPluginHost.StableIdOf(int slot) => _roster[slot].StableId;

    int IPluginHost.SlotOfStableId(long stableId) => _roster.SlotOfStableId(stableId);

    bool IPluginHost.Kick(int slot, string reason) =>
        KickPlayer(slot, reason) == KickOutcome.Kicked;

    void IPluginHost.SetTeam(int slot, Team team) => ForceTeam(slot, team);

    bool IPluginHost.EndRound() => RequestRoundEnd();

    bool IPluginHost.SetNextMap(string map) => RequestNextMap(map);

    /// <summary>
    /// <c>--votable-maps</c>, then the rotation, then every map on disk.
    /// </summary>
    /// <remarks>
    /// ⚠️ The third fallback is this port's. The reference's votemap plugin
    /// stops at the rotation and answers "No maps were found!" without one —
    /// which is every single-map server, i.e. the default, i.e. exactly where a
    /// map vote is most wanted.
    /// </remarks>
    IReadOnlyList<string> IPluginHost.VotableMaps
    {
        get
        {
            if (_options.VotableMaps.Count > 0)
                return _options.VotableMaps;

            if (_rotation.Count > 0)
                return _rotation;

            return _installedMaps ??= InstalledMaps();
        }
    }

    /// <summary>
    /// <c>global.map_rotation</c>, as configured. Empty when the server is not
    /// rotating, which is the single-map default.
    /// </summary>
    IReadOnlyList<string> IPluginHost.MapRotation => _rotation;

    /// <summary><c>GameServer.currentMapIndex</c>.</summary>
    int IPluginHost.CurrentMapIndex => _rotationIndex;

    /// <summary>
    /// <c>Player.stats[POINTS]</c>. A read of the simulation, never a write —
    /// see <see cref="IPluginHost"/>'s rule about what a plugin may reach.
    /// </summary>
    int IPluginHost.PointsOf(int slot) =>
        (int)Math.Floor(Scoring.Get(_world, slot, StatIndex.Points));

    /// <summary>
    /// <c>[Server] ChatJoinMessage</c>, read straight off the options.
    /// </summary>
    string IPluginHost.ChatJoinMessage => _options.ChatJoinMessage;

    /// <summary><c>[Plugins] chat_*</c>, read straight off the options.</summary>
    ChatSettings IPluginHost.ChatSettings => _options.Chat;

    /// <summary>
    /// What <c>/bots</c> may do and what the population is doing.
    /// </summary>
    /// <remarks>
    /// <b><c>Managed</c> is <c>Bots.Enabled</c> and <c>Count</c> is the roster</b>,
    /// and the command needs both because they disagree in the case that matters:
    /// a bot added by hand on a server with the manager off is a bot no suspension
    /// will take away, and answering "done" there would be the command lying.
    /// </remarks>
    BotStatus IPluginHost.Bots => new(
        CommandAllowed: _options.BotSuspendMinutes > 0,
        SuspendMinutes: _options.BotSuspendMinutes,
        Managed: _options.Bots.Enabled,
        Count: BotSlots.Count,
        SecondsLeft: BotSuspensionSecondsLeft);

    /// <summary>
    /// ⛔ <b>Refused outright when <c>[Bots] SuspendMinutes</c> is zero</b>, which
    /// is the default. The gate is here rather than in the plugin so that the two
    /// chat plugins cannot answer differently, and so that an operator turning the
    /// command off turns it off everywhere at once.
    /// </summary>
    bool IPluginHost.SuspendBots() => SuspendBots(_options.BotSuspendMinutes);

    bool IPluginHost.ResumeBots() => ResumeBots();

    DuelDifficulty? IPluginHost.DuelDifficultyOf(int slot) =>
        _options.Duel && _population is { } population && slot > 0 && slot < RosterCount && !EntryOf(slot).IsBot
            ? population.DuelDifficultyOf(slot)
            : null;

    bool IPluginHost.SetDuelDifficulty(int slot, DuelDifficulty setting)
    {
        if (((IPluginHost)this).DuelDifficultyOf(slot) is null)
            return false;

        _population!.SetDuelDifficulty(slot, setting);
        return true;
    }

    /// <summary>
    /// ⛔ <b>NOT <see cref="WorldState.Random"/>.</b> That stream is observable
    /// simulation; a plugin drawing from it would move every client's re-derived
    /// weapon spread. This is <see cref="_spawnRandom"/>'s sibling: the server's
    /// own, seeded from the port so two servers on one machine differ.
    /// </remarks>
    Random IPluginHost.Random => _pluginRandom;

    private IReadOnlyList<string>? _installedMaps;

    private List<string> InstalledMaps()
    {
        var maps = new List<string>();

        foreach (var path in Directory.EnumerateFiles(_options.MapDirectory, "*.png"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (name.Length != 0)
                maps.Add(name);
        }

        maps.Sort(StringComparer.Ordinal);
        return maps;
    }

    /// <summary>
    /// A plugin renaming a player, which is the one game-visible thing the
    /// plugin API can do.
    /// </summary>
    /// <remarks>
    /// ⚠️ The same truncation the client command gets, for the same reason: a
    /// name over <c>MAX_PLAYERNAME_LENGTH</c> is what <c>KICK_NAME</c> exists
    /// for, and a plugin must not be able to produce one the server would kick a
    /// client over.
    /// </remarks>
    void IPluginHost.Rename(int slot, string name)
    {
        if (name.Length > GameConstants.MaxPlayerNameLength)
            name = name[..GameConstants.MaxPlayerNameLength];

        _roster[slot].Name = name;
        _broadcast.PlayerChangeName(checked((byte)slot), name);
    }

    /// <summary>
    /// <c>PluginPacketSend</c>: into the broadcast buffer, so it leaves with
    /// this tick's flush and in order with everything else appended this tick.
    /// </summary>
    void IPluginHost.SendPluginPacketToAll(int packetId, ReadOnlySpan<byte> data) =>
        _broadcast.PluginPacket(checked((byte)packetId), data);

    /// <summary>
    /// <c>PluginPacketSendTo</c>: onto one client's own queue.
    /// </summary>
    /// <remarks>
    /// ⚠️ Queued rather than written to the socket, unlike the reference's
    /// <c>socket_send</c>. The ordering a client sees is the same either way —
    /// this tick's broadcast is appended to the same queue afterwards — and
    /// queueing keeps every send on the tick's one flush.
    /// <para>
    /// A slot with no socket is skipped: a bot, or the server's own slot 0.
    /// The reference short-circuits <c>global.myself</c> into its local packet
    /// queue instead, which a headless server has no use for.
    /// </para>
    /// </remarks>
    void IPluginHost.SendPluginPacketTo(int slot, int packetId, ReadOnlySpan<byte> data)
    {
        var connection = _roster[slot].Connection;
        if (connection is null || connection.IsClosed)
            return;

        var writer = new NetWriter(data.Length + 4);
        Wire.ServerEncoder.For(connection.Protocol).PluginPacket(writer, checked((byte)packetId), data);
        connection.Send(writer.WrittenSpan);
    }

    // --- the operator's chat ---------------------------------------------------

    /// <summary>
    /// Longest line the panel may send. A <c>MESSAGE_STRING</c> is a
    /// <c>ubyte</c>-prefixed string and <c>chat_v2_pr</c> truncates at the same
    /// length, so one bound serves every path.
    /// </summary>
    public const int MaxSayLength = 255;

    /// <summary>What the panel's chat window shows, or null without the panel.</summary>
    public ChatHistory? ChatHistory => _chatHistory;

    /// <summary>The loaded chat plugin the operator's lines go through, or null.</summary>
    public string? ChatPluginName => _plugins.Chat?.Name;

    void IPluginHost.ChatHeard(int slot, string message, bool teamOnly) =>
        _chatHistory?.Add(_roster[slot].Name, _roster[slot].Team, message, teamOnly);

    /// <summary>
    /// Says <paramref name="text"/> as the server's own slot-0 spectator.
    /// </summary>
    /// <returns>
    /// <c>"chat"</c> when a chat plugin carried it, <c>"notice"</c> when it went
    /// out as a <c>MESSAGE_STRING</c> instead.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>Chat exists only through a plugin.</b> Stock GG2 has no chat, so with a
    /// chat plugin loaded the line is ordinary chat from slot 0, and without one
    /// it is a <c>MESSAGE_STRING</c> — the notice every stock client draws on
    /// screen (<c>ClientBeginStep.gml</c>, <c>case MESSAGE_STRING</c>), prefixed
    /// with slot 0's name so it still says who spoke. A notice replaces the one
    /// before it rather than scrolling.
    /// </para>
    /// <para>
    /// Both are messages a stock client already handles, so nothing here is
    /// invisible to a client that the server believes it said.
    /// </para>
    /// </remarks>
    public string Say(string text)
    {
        text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();

        if (text.Length == 0)
            throw new ArgumentException("nothing to say");

        if (text.Length > MaxSayLength)
            throw new ArgumentException($"a line is at most {MaxSayLength} characters");

        if (_plugins.Chat?.Implementation is IServerChat chat)
        {
            chat.SayAsServer(text);
            return "chat";
        }

        var name = _roster.ServerPlayer.Name;
        var notice = $"{name}: {text}";

        if (notice.Length > MaxSayLength)
            notice = notice[..MaxSayLength];

        _broadcast.MessageString(notice);
        _chatHistory?.Add(name, Team.Spectator, text, teamOnly: false);
        _log.Info($"[notice] {LogText.Safe(name)}: {LogText.Safe(text)}");
        return "notice";
    }
}
