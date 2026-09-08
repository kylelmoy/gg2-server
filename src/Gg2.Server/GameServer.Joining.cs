using System.Diagnostics;
using System.Net;
using Gg2.Bots;
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
        // to. Accepting several is not observable and avoids making a burst of
        // joins take a second.
        //
        // ⚠️ BOUNDED, unlike the loop this replaced. Each accept allocates a
        // connection and walks every existing one for the per-address check, so
        // draining the whole backlog is quadratic work on the thread that owes
        // every player already on the server a tick every 33 ms. What does not
        // fit stays in the kernel's own accept backlog, which is where a
        // connection flood is supposed to be absorbed and where it can be
        // dropped far more cheaply than here.
        var accepted = 0;

        while (accepted < _limits.MaxAcceptsPerTick && _listener.Pending())
        {
            accepted++;

            var socket = _listener.AcceptSocket();
            var connection = new ClientConnection(socket, _limits);

            // ⚠️ FIRST, and with no message written back. An operator's ban is
            // the one refusal where the peer is not owed an explanation, and
            // telling them which of several addresses is banned is free
            // reconnaissance. It is also the cheapest check, so a banned
            // flooder costs the least of any refused connection.
            if (_bans.Find(connection.RemoteAddress, now) is { } ban)
            {
                connection.Dispose();

                if (_refusalLog.ShouldLog(_nowMs, out var banSuppressed))
                {
                    _log.Warn(
                        $"refused {connection.RemoteAddress}: banned ({LogText.Safe(ban.Reason)})"
                        + Suffix(banSuppressed));
                }

                continue;
            }

            // ⚠️ Then the RATE, which is not MultiClientLimit. That one bounds
            // how many connections an address holds at once; this bounds how
            // fast it opens them — connect, get refused, reconnect, repeat, with
            // the concurrent count never above one and every cycle costing an
            // accept, an allocation and a roster walk.
            // ⚠️ Loopback is exempt, exactly as it is from MultiClientLimit and
            // for the reference's own reason: it is how a host runs a client
            // beside its own server, and how a test drives several at once.
            // Rate-limiting the operator's own machine protects nothing — a
            // flood from 127.0.0.1 is already inside the box.
            //
            // ⛔ A BAN is not exempt, deliberately. An operator who bans
            // loopback meant it; the exemption above is about a limit nobody
            // asked for, not about overriding one somebody did.
            if (_connectionRate is not null
                && !IPAddress.IsLoopback(connection.RemoteAddress)
                && !_connectionRate.Allow(connection.RemoteAddress, _nowMs))
            {
                // Closed without a message, like a ban: a client being told
                // "slow down" would reconnect to read it, which is the loop
                // being damped.
                connection.Dispose();

                if (_refusalLog.ShouldLog(_nowMs, out var rateSuppressed))
                {
                    _log.Warn(
                        $"refused {connection.RemoteAddress}: connecting too fast"
                        + Suffix(rateSuppressed));
                }

                continue;
            }

            // ⚠️ Before the per-address check, because this is the one an
            // attacker with many addresses does not trip. MultiClientLimit is
            // the reference's answer to a household behind one NAT — three
            // connections per address — and a hundred addresses is three hundred
            // sockets, each with a receive buffer and an 8 KB scratch array, all
            // held for as long as the handshake allows.
            if (_joining.Count >= _limits.MaxJoiningConnections)
            {
                // SERVER_FULL rather than a KICK: it is the message that already
                // means "there is no room for you", and the client's answer to
                // it is "The server is full." — which is what has happened, from
                // the point of view of somebody trying to get in.
                var full = new NetWriter(2);
                ServerMessages.ServerFull(full);
                Refuse(connection, full.WrittenSpan, "the joining queue is full");
                continue;
            }

            if (ExceedsMultiClientLimit(connection))
            {
                var kick = new NetWriter(2);
                ServerMessages.Kick(kick, KickReason.MultiClient);
                Refuse(connection, kick.WrittenSpan, "too many connections from that address");
                continue;
            }

            _joining.Add(new JoiningConnection(connection, now, _limits));
        }
    }

    /// <summary>
    /// Tells a connection why it is not being served, and drops it.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Flush BEFORE Dispose.</b> The reason byte is queued, not written, so
    /// disposing first throws away the very message being sent — and the client
    /// then shows an unexplained disconnect instead of "server full". The same
    /// requirement bites in <c>GameServer.Clients</c>'s plugin-packet kick.
    /// <para>
    /// Logged at warning, and rate-limited by <see cref="_refusalLog"/>: a
    /// refusal is the server declining work, which is worth an operator seeing
    /// — and a connection flood is precisely the case where writing one line per
    /// refusal turns the log into the denial of service. Console output is
    /// synchronous and happens on the tick thread.
    /// </para>
    /// </remarks>
    /// <summary>
    /// What a throttled log line says about the ones that did not get written.
    /// </summary>
    /// <remarks>
    /// Empty when nothing was held back, so an ordinary line stays an ordinary
    /// line — the note appears only when there is something to note, which is
    /// what makes it worth reading when it does.
    /// </remarks>
    private static string Suffix(int suppressed) =>
        suppressed == 0 ? string.Empty : $" (+{suppressed} similar lines not logged)";

    private void Refuse(ClientConnection connection, ReadOnlySpan<byte> message, string why)
    {
        connection.Send(message);
        connection.Flush();
        connection.Dispose();

        if (_refusalLog.ShouldLog(_nowMs, out var suppressed))
        {
            _log.Warn($"refused {connection.RemoteAddress}: {why}{Suffix(suppressed)}");
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

            JoinOutcome outcome;

            // Same containment as ServiceClients, and for the same reason: a
            // handshake is driven entirely by bytes the peer chose, and this one
            // runs BEFORE the peer is a player — so it is reachable by anything
            // that can open a socket, with no password and no protocol
            // handshake completed. A fault here must cost one connection.
            try
            {
                outcome = joining.Service(context, now);
            }
            catch (Exception e)
            {
                _log.Error($"dropping a handshake from {joining.Connection.RemoteAddress}: it faulted", e);
                outcome = JoinOutcome.Finished;
            }

            switch (outcome)
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
    /// are excluded from it and announced separately, in the packet's own bot
    /// field, which the browser renders as <c>players+bots/slots</c>. The
    /// reference sends a hard zero there because it has no bots to count; see
    /// <see cref="LobbyServerInfo.Bots"/> and <see cref="AnnouncedBotCount"/>.
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
            Bots = checked((ushort)AnnouncedBotCount),
            Passworded = _options.Password.Length > 0,
        });
    }

    /// <summary>
    /// The bot count to advertise: the bots that are running, or — when none
    /// are — the bots the next player to join would find.
    /// </summary>
    /// <remarks>
    /// <b>An idle server's real bot count is zero and advertising that hides the
    /// game.</b> <c>BotPopulationOptions.MinHumans</c> keeps a server with nobody
    /// on it from filling with bots, so a bot server sitting empty announces
    /// <c>0/16</c> and reads as dead — while the moment somebody joins it becomes
    /// a full game. The prospective count is
    /// <c>BotPopulation.Target(humans + 1, …)</c>, which is that game as the
    /// browser would show it: <c>0+7/16</c>.
    /// <para>
    /// <b>Asking for one more human rather than for <c>MinHumans</c> is what
    /// keeps it honest.</b> A server needing three humans before any bot spawns
    /// gets zero from this, because a single joiner really would arrive to an
    /// empty map. Bots switched off get zero for the same reason.
    /// </para>
    /// <para>
    /// Live bots win whenever there are any, so the number stops being a
    /// prediction the instant it can be a fact.
    /// </para>
    /// </remarks>
    public int AnnouncedBotCount
    {
        get
        {
            var live = BotSlots.Count;

            if (live > 0)
                return live;

            return BotPopulation.Target(OccupiedSlots() + 1, _options.Bots);
        }
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

        // ⚠️ TAKEN BEFORE THE PLAYER_JOIN BELOW, and that is the whole point of
        // the mark: everything standing in the broadcast buffer right now was
        // appended ABOVE this call in the same tick — BroadcastEndStepEvents,
        // and anything the control port or the web admin executed — and the
        // burst just written already carries the world all of it describes.
        // Everything appended from here on, starting with this player's own
        // PLAYER_JOIN, is news the joiner has to have.
        // ClientPlayer.SkipBroadcastBytes has the two failures this prevents.
        _clients.Add(new ClientPlayer
        {
            Connection = connection,
            Entry = entry,
            SkipBroadcastBytes = _broadcast.WrittenSpan.Length,
        });

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
        if (_churnLog.ShouldLog(_nowMs, out var suppressed))
        {
            _log.Info(
                $"{LogText.Safe(entry.Name)} joined as player {_roster.Count - 1}; "
                + $"{_roster.Count - 1} of {_options.PlayerLimit} slots used"
                + Suffix(suppressed));
        }

        // serviceJoiningPlayer.gml:171 — tell the lobby the player count moved,
        // rather than letting the list show a stale one for up to 30 seconds.
        AnnounceToLobby();
    }
}
