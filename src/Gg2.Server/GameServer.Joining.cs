using System.Diagnostics;
using System.Net;
using Gg2.Protocol;

namespace Gg2.Server;

public sealed partial class GameServer
{
    /// <summary>The join burst as a fresh array, for <see cref="MatchRecorder"/>.</summary>
    private byte[] JoinBurstBytes()
    {
        var writer = new NetWriter(2048);
        WriteJoinBurst(writer);
        return writer.WrittenSpan.ToArray();
    }

    // --- joining -------------------------------------------------------------

    private void AcceptConnections(DateTime now)
    {
        Debug.Assert(_listener is not null, "Start() must run before Tick()");

        // The reference accepts one per tick because GML gave it no reason not
        // to. Accepting everything pending is not observable and avoids making a
        // burst of joins take a second.
        while (_listener.Pending())
        {
            var socket = _listener.AcceptSocket();
            var connection = new ClientConnection(socket);

            if (ExceedsMultiClientLimit(connection))
            {
                var writer = new NetWriter(2);
                ServerMessages.Kick(writer, KickReason.MultiClient);
                connection.Send(writer.WrittenSpan);
                connection.Flush();
                connection.Dispose();

                _log.Warn($"refused {connection.RemoteAddress}: too many connections from that address");
                continue;
            }

            _joining.Add(new JoiningConnection(connection, now));
        }
    }

    private bool ExceedsMultiClientLimit(ClientConnection candidate)
    {
        // Loopback is exempt: it is how the reference lets a host run clients
        // beside its own server, and how a test drives several at once.
        if (IPAddress.IsLoopback(candidate.RemoteAddress))
            return false;

        var count = 1;

        foreach (var client in _clients)
            if (client.Connection.RemoteAddress.Equals(candidate.RemoteAddress))
                count++;

        foreach (var joining in _joining)
            if (joining.Connection.RemoteAddress.Equals(candidate.RemoteAddress))
                count++;

        return count > _options.MultiClientLimit;
    }

    private void ServiceJoiningConnections(DateTime now)
    {
        // GameServerEndStep.gml:12-17 counts the downloads in flight BEFORE
        // servicing any of them, and divides the tick's allowance between them.
        // ⚠️ Counted first on purpose: doing it inside the loop would give the
        // first client a bigger share than the last for no reason anyone could
        // see from the outside.
        var downloading = 0;
        foreach (var joining in _joining)
            if (joining.State == JoinState.ClientDownloading)
                downloading++;

        var share = downloading == 0
            ? 0
            : _options.MapDownloadBytesPerSecond / (double)(GameConstants.TickRate * downloading);

        var context = new JoinContext
        {
            ServerName = _options.ServerName,
            // The map now loaded, not the one configured — after a rotation
            // they differ, and a HELLO naming the wrong one makes the client
            // download or refuse a map the server is not serving.
            CurrentMap = _currentMap,
            Password = _options.Password,
            PlayerLimit = _options.PlayerLimit,
            OccupiedSlots = OccupiedSlots(),
            MapMd5 = _currentMapMd5,
            MapFile = _currentMapFile,
            MapDownloadShare = share,
            MapDownloadBudget = _options.MapDownloadBytesPerSecond / (double)GameConstants.TickRate,
            PluginList = _plugins.Advertised,
            PluginsRequired = _options.ServerPluginsRequired,
        };

        for (var i = _joining.Count - 1; i >= 0; i--)
        {
            var joining = _joining[i];

            switch (joining.Service(context, now))
            {
                case JoinOutcome.Pending:
                    joining.Connection.Flush();
                    break;

                case JoinOutcome.Joined:
                    _joining.RemoveAt(i);
                    CompleteJoin(joining);
                    break;

                case JoinOutcome.Finished:
                    _joining.RemoveAt(i);
                    joining.Connection.Flush();
                    joining.Connection.Dispose();
                    break;
            }
        }
    }

    /// <summary>
    /// Re-announces this server to the lobby, if announcing is on at all.
    /// </summary>
    /// <remarks>
    /// <c>sendLobbyRegistration.gml</c> is called from four places in the
    /// reference and all four are wired here: the 30-second cadence
    /// (<c>GameServerBeginStep.gml:5-8</c>), a map change (:143), a completed
    /// join (<c>serviceJoiningPlayer.gml:171</c>), and a leave that un-fills a
    /// server that was full (<c>GameServerBeginStep.gml:46-49</c>).
    /// <para>
    /// ⚠️ <b>The count is <see cref="OccupiedSlots"/>, which is humans.</b> Bots
    /// are excluded from it and the packet's own bot field is a hard zero — so a
    /// server full of bots advertises as empty. That is the reference's
    /// behaviour, transcribed rather than improved; see
    /// <see cref="LobbyRegistration"/>.
    /// </para>
    /// </remarks>
    private void AnnounceToLobby()
    {
        if (_lobby is null)
            return;

        _lobby.Register(new LobbyServerInfo
        {
            ServerId = _lobby.ServerId,
            ServerName = _options.ServerName,
            Map = _match.Map.Name,
            Port = checked((ushort)BoundPort),
            PlayerLimit = checked((ushort)_options.PlayerLimit),
            OccupiedSlots = checked((ushort)OccupiedSlots()),
            Passworded = _options.Password.Length > 0,
        });
    }

    private int OccupiedSlots()
    {
        var count = _roster.OccupiedSlots;

        foreach (var joining in _joining)
            if (joining.OccupiesSlot)
                count++;

        return count;
    }

    /// <summary>
    /// Turns a finished handshake into a roster entry, in the order
    /// <c>serviceJoiningPlayer.gml:108-129</c> does it.
    /// </summary>
    /// <summary>
    /// The join burst: everything a joiner is sent before it starts following
    /// the broadcast, ending in the <c>FULL_UPDATE</c> that defines a complete
    /// starting point.
    /// </summary>
    /// <remarks>
    /// Factored out of <see cref="CompleteJoin"/> for <see cref="MatchRecorder"/>,
    /// which opens every segment with it so the segment is replayable on its own.
    /// ⚠️ There must be exactly one of these: a recording whose preamble drifted
    /// from what a real client is sent would replay differently from the game it
    /// recorded, and nothing would say so.
    /// </remarks>
    private void WriteJoinBurst(NetWriter writer)
    {
        // The joiner's id is the roster size *before* it is added, and
        // everything here has to arrive before the FULL_UPDATE that follows it —
        // that update's player count is checked against the client's own roster.
        var playerId = checked((byte)_roster.Count);
        ServerMessages.JoinUpdate(writer, playerId, _match.CurrentMapArea);

        // The CHANGE_MAP is what makes the client load the map.
        // ⚠️ _currentMap, not _options.Map. They are the same until the first
        // rotation and different for ever afterwards, and this is the message
        // that makes a joining client load the map — so a stale name here means
        // every player who joins after the first round plays a different map
        // from everybody else.
        //
        // ⚠️ _currentMapMd5, NOT string.Empty. `ServerJoinUpdate.gml:8` passes
        // `global.currentMapMD5` here, and an empty MD5 does not mean "no hash
        // handy" to a client — it means "this is an INTERNAL map"
        // (`ClientBeginStep.gml:476`). On a custom map the joiner then fails
        // `findInternalMapName`, shows "Server went to invalid internal map:
        // <name>#Exiting." and drops to the menu, so it can never join at all.
        // Measured live 2026-09-05 on gg_debug2: two stock clients, both killed
        // on arrival. The HELLO already carried the right hash, which is why the
        // handshake tests passed throughout.
        ServerMessages.ChangeMap(writer, _currentMap, _currentMapMd5);

        for (var i = 0; i < _roster.Count; i++)
        {
            var existing = _roster[i];
            ServerMessages.PlayerJoin(writer, existing.Name);
            ServerMessages.PlayerChangeClass(writer, (byte)i, existing.Class);
            ServerMessages.PlayerChangeTeam(writer, (byte)i, existing.Team);
        }

        _stateWriter.Write(writer, MessageType.FullUpdate);
    }

    private void CompleteJoin(JoiningConnection joining)
    {
        var connection = joining.Connection;
        var writer = new NetWriter(2048);

        // 1. The whole join burst, on this socket alone.
        WriteJoinBurst(writer);

        connection.Send(writer.WrittenSpan);

        // 2. Now it exists — in the roster and, since phase 6, in the world:
        //    a fresh spectator whose respawn alarm is at rest.
        var entry = new RosterEntry { Name = joining.Name, Connection = connection };
        _roster.Add(entry);
        _world.AddPlayer(_roster.Count - 1);
        _clients.Add(new ClientPlayer { Connection = connection, Entry = entry });
        _plugins.PlayerAdded(_roster.Count - 1);

        // 3. Broadcast to everyone *including* the joiner, which is how the
        //    joiner learns about itself — after the replay of everyone else.
        ServerMessages.PlayerJoin(_broadcast, entry.Name);

        // 4. The welcome message, to this socket only.
        if (_options.WelcomeMessage.Length != 0)
        {
            writer.Reset();
            ServerMessages.MessageString(writer, _options.WelcomeMessage);
            connection.Send(writer.WrittenSpan);
        }

        connection.Flush();
        // The joiner's id is its roster index, which is the count before it was
        // added — the same value WriteJoinBurst put in the JOIN_UPDATE.
        _log.Info(
            $"{LogText.Safe(entry.Name)} joined as player {_roster.Count - 1}; "
            + $"{_roster.Count - 1} of {_options.PlayerLimit} slots used");

        // serviceJoiningPlayer.gml:171 — tell the lobby the player count moved,
        // rather than letting the list show a stale one for up to 30 seconds.
        AnnounceToLobby();
    }
}
