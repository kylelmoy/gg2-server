using System.Net;
using System.Net.Sockets;
using GG2.Bots;
using GG2.Protocol;

namespace GG2.Server;

/// <summary>
/// Settings an operator changes on a RUNNING server from the admin panel — and
/// never writes to the config file.
/// </summary>
/// <remarks>
/// <para>
/// <b>Each one is here because it is read at use time, not once at
/// <see cref="Start"/></b>: the password and the seat count when a client joins,
/// autobalance by the balance check, the rotation when a round ends, the bot
/// limits every tick by the population manager. A setting read once at startup
/// cannot be changed this way, and the settings page plus a restart remains the
/// route for it.
/// </para>
/// <para>
/// ⚠️ <b>Temporary by design.</b> A restart re-reads the flags and the file, so
/// everything set here is lost; the panel says so beside the controls.
/// </para>
/// </remarks>
public sealed partial class GameServer
{
    /// <summary>Longest game password a client can send: a <c>ubyte</c>-prefixed string.</summary>
    public const int MaxPasswordLength = 255;

    /// <summary>Bumped when the bot brain is replaced, so the pacing loop re-arms its budget.</summary>
    private int _brainVersion;

    /// <summary>Set by <see cref="SetOffline"/>.</summary>
    private bool _offline;

    /// <summary>Whether the game is offline: nobody connected, nothing accepted, no bots.</summary>
    public bool IsOffline => _offline;

    /// <summary>
    /// Takes the game offline, or brings it back — without stopping the process.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Offline:</b> every client is kicked, every half-finished join dropped,
    /// the listening socket closed so a new connection is REFUSED rather than left
    /// hanging in the backlog, the lobby listing withdrawn, and the bot population
    /// held at zero through the same target a <c>/bots off</c> suspension uses.
    /// The panel and the control port stay up, which is the point: an operator can
    /// change settings, edit the rotation and bring it back without a shell.
    /// </para>
    /// <para>
    /// <b>Online:</b> the listener is re-opened on the SAME port it had — a
    /// server started on port 0 would otherwise come back somewhere else — and the
    /// lobby is told again. The world keeps ticking throughout; with nobody on it
    /// that is the idle 2 Hz loop.
    /// </para>
    /// </remarks>
    public void SetOffline(bool offline)
    {
        if (offline == _offline)
            return;

        if (offline)
        {
            _offline = true;

            for (var slot = _roster.Count - 1; slot > 0; slot--)
                if (_roster[slot].Connection is { IsClosed: false })
                    KickPlayer(slot, "server taken offline");

            foreach (var joining in _joining)
                joining.Connection.Dispose();

            _joining.Clear();

            _listener?.Stop();
            _lobby?.Unregister();

            _log.Info("control: server offline; players disconnected, connections refused");
            return;
        }

        _listener = new TcpListener(IPAddress.Any, BoundPort);
        _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
        _listener.Start();

        _offline = false;
        AnnounceToLobby();
        _log.Info($"control: server online; listening on {BoundPort}");
    }

    /// <summary>Clients that completed a join since startup, repeats included.</summary>
    public long JoinsSinceStart { get; private set; }

    /// <summary>The rotation being walked, after any shuffle and any live edit.</summary>
    public IReadOnlyList<string> Rotation => _rotation;

    /// <summary>
    /// The rotation entry the current round is, or -1 when the map being played
    /// is not the rotation's — an opening <c>--map</c>, or a map change by hand.
    /// </summary>
    public int CurrentRotationIndex =>
        _rotationIndex < _rotation.Count && _rotation[_rotationIndex] == _currentMap ? _rotationIndex : -1;

    /// <summary>Whether joining needs a password.</summary>
    public bool Passworded => _options.Password.Length != 0;

    /// <summary>Sets the join password; empty removes it. Players already in stay.</summary>
    public void SetGamePassword(string password)
    {
        if (password.Length > MaxPasswordLength)
            throw new ArgumentException($"a password is at most {MaxPasswordLength} characters");

        _options.Password = password;
        _log.Info(password.Length == 0 ? "control: join password removed" : "control: join password changed");
    }

    /// <summary>
    /// Sets the seats for network clients. Lowering it below the players already
    /// connected turns nobody away; it only refuses the next joins.
    /// </summary>
    public void SetPlayerLimit(int limit)
    {
        if (limit is < 1 or > 48)
            throw new ArgumentException("player limit must be 1..48");

        _options.PlayerLimit = limit;
        _log.Info($"control: player limit {limit}");
    }

    /// <summary>
    /// Turns autobalance on or off for this round and the rounds after it.
    /// </summary>
    /// <remarks>
    /// The world's copy is what the balance check reads; the option is what the
    /// next map load copies into it (<c>GameServer.Maps</c>). Both, or the change
    /// would last one round.
    /// </remarks>
    public void SetAutobalance(bool enabled)
    {
        _options.Autobalance = enabled;
        _world.Autobalance = enabled;
        _log.Info($"control: autobalance {(enabled ? "on" : "off")}");
    }

    /// <summary>Ends the round with nobody winning and plays the same map again.</summary>
    /// <remarks>
    /// The ordinary round end with a one-round override — so a multi-area map
    /// starts again from its first area, which is what "restart" means to the
    /// players watching the scoreboard.
    /// </remarks>
    public bool RequestRoundRestart()
    {
        if (_world.MapChanging || _world.RoundDecided)
            return false;

        RequestNextMap(_currentMap);
        return RequestRoundEnd();
    }

    /// <summary>
    /// Replaces the rotation the next round is picked from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The index follows the current map when it is in the new list, so the round
    /// after this one is the entry after it. When it is not, the next round is the
    /// first entry. An empty rotation keeps the server on the map it is on, as a
    /// single-map server does.
    /// </para>
    /// <para>
    /// ⚠️ Refused whole if any entry is not a map on disk: a rotation that names a
    /// missing map is skipped over at every round end, silently.
    /// </para>
    /// </remarks>
    public void SetRotation(IReadOnlyList<string> maps)
    {
        foreach (var map in maps)
            if (!MapExists(map))
                throw new ArgumentException($"no map '{map}'");

        var rotation = maps.ToArray();
        var current = CurrentRotationIndex;
        var index = -1;

        // The same position when the current entry is still there, otherwise the
        // first occurrence of the current map.
        if (current >= 0 && current < rotation.Length && rotation[current] == _currentMap)
            index = current;
        else
            index = Array.IndexOf(rotation, _currentMap);

        _rotation = rotation;
        _options.MapRotation = rotation;
        _rotationIndex = index >= 0 ? index : Math.Max(0, rotation.Length - 1);

        _log.Info($"control: rotation {(rotation.Length == 0 ? "cleared" : string.Join(", ", rotation))}");
    }

    /// <summary>
    /// Moves a player, bot or client, onto a team — the balance refusal a
    /// client's own request meets is skipped, nothing else.
    /// </summary>
    public void SetPlayerTeam(int slot, Team team)
    {
        if (slot <= 0 || slot >= _roster.Count)
            throw new ArgumentException($"slot {slot} is not a player");

        if (team is not (Team.Red or Team.Blue or Team.Spectator))
            throw new ArgumentException($"no team '{team}'");

        ForceTeam(slot, team);
    }

    /// <summary>The most bots the population manager will run, whatever the fill.</summary>
    public void SetBotLimit(int max)
    {
        if (max is < 0 or > 48)
            throw new ArgumentException("bot limit must be 0..48");

        _options.Bots = _options.Bots with { MaxBots = max };
        _log.Info($"control: bot limit {max}");
    }

    /// <summary>
    /// Replaces the bot brain and every running bot with it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Built by <see cref="BotBrain.Build"/>, the one place a brain is built</b>,
    /// so a live swap cannot produce a brain a restart would not. A policy mode
    /// needs a checkpoint the server was started with; without one this is
    /// refused and nothing changes.
    /// </para>
    /// <para>
    /// ⚠️ A running bot holds its brain for life (<c>BotBrainSpec.Wrap</c> at
    /// add time), so the bots are removed and the population manager adds them
    /// back with the new one on the next tick. ⚠️ Loading a policy checkpoint
    /// happens on the tick thread and can make that one tick late.
    /// </para>
    /// </remarks>
    public void SetBotBrain(BotMode mode)
    {
        var saved = (_options.BotMode, _options.Bots, _options.PolicyPaths, _options.BotClassPolicies);

        _options.BotMode = mode;

        // A server started with a checkpoint keeps it for a switch back, but a
        // mode that never consults one refuses to be built alongside it.
        if (!mode.UsesPolicy())
        {
            _options.PolicyPaths = [];
            _options.BotClassPolicies = [];
        }

        try
        {
            BotBrain.Build(_options, line => _log.Info(line));
        }
        catch (Exception e) when (e is ArgumentException or IOException or InvalidDataException)
        {
            (_options.BotMode, _options.Bots, _options.PolicyPaths, _options.BotClassPolicies) = saved;
            throw new ArgumentException(e.Message, e);
        }

        _options.PolicyPaths = saved.PolicyPaths;
        _options.BotClassPolicies = saved.BotClassPolicies;
        _brainVersion++;

        // Highest slot first, so every removal leaves the lower indices alone.
        var bots = BotSlots.ToArray();

        for (var i = bots.Length - 1; i >= 0; i--)
            RemoveBot(bots[i]);

        _log.Info($"control: bot brain {mode.ToString().ToLowerInvariant()}; {bots.Length} bot(s) replaced");
    }
}
