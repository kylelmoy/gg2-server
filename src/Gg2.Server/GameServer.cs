using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Gg2.Maps;
using Gg2.Protocol;

namespace Gg2.Server;

/// <summary>Where the server reports what it did. Nothing here is on the wire.</summary>
public interface IServerLog
{
    void Info(string message);
}

/// <summary>
/// The headless server: accept, handshake, roster, and the 30 Hz broadcast.
/// </summary>
/// <remarks>
/// Single-threaded and non-blocking. The tick thread owns everything and never
/// waits on a socket, which is the property docs/ARCHITECTURE.md actually needs;
/// moving the socket work onto its own threads is a change behind
/// <see cref="ClientConnection"/> and nothing above it has to know.
/// <para>
/// Two things the reference does that a headless port does not: the loopback TCP
/// connection the host uses to talk to itself (the roster slot stays, the socket
/// goes) and lobby registration. Neither is visible to a stock client.
/// </para>
/// </remarks>
public sealed class GameServer : IDisposable
{
    private readonly ServerOptions _options;
    private readonly IServerLog _log;

    private readonly List<JoiningConnection> _joining = [];
    private readonly List<ClientPlayer> _clients = [];
    private readonly NetWriter _broadcast = new(8192);

    private TcpListener? _listener;
    private Roster _roster = null!;
    private MatchState _match = null!;
    private StateUpdateWriter _stateWriter = null!;

    /// <summary>
    /// Ticks since start. <c>frame</c> in <c>GameServerBeginStep.gml</c>, and it
    /// is incremented <em>before</em> the cadence is tested — so the first tick
    /// the server ever sends is frame 1, an <c>INPUTSTATE</c>.
    /// </summary>
    private long _frame;

    public GameServer(ServerOptions options, IServerLog log)
    {
        options.Validate();
        _options = options;
        _log = log;
    }

    /// <summary>Ticks elapsed. Test hook; not part of the protocol.</summary>
    public long Frame => _frame;

    public int PlayerCount => _roster.Count;

    /// <summary>The port actually bound, which differs from the request when it was 0.</summary>
    public int BoundPort { get; private set; }

    /// <summary>Binds the listener and loads the map. Does not tick.</summary>
    public void Start()
    {
        var map = LoadMap(_options.Map);

        _roster = new Roster(_options.ServerPlayerName);
        _match = new MatchState(map, _roster, _options);
        _stateWriter = new StateUpdateWriter(_match);

        _listener = new TcpListener(IPAddress.Any, _options.Port);
        _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
        _listener.Start();
        BoundPort = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _log.Info(
            $"listening on {BoundPort}, map {map.Name} ({_match.GameMode}, " +
            $"{map.Walkmask.Width}x{map.Walkmask.Height} cells), {_options.PlayerLimit} slots");
    }

    /// <summary>
    /// Runs until <paramref name="cancellation"/> is signalled, pacing to 30 Hz.
    /// </summary>
    public void Run(CancellationToken cancellation)
    {
        var clock = Stopwatch.StartNew();
        var tickLength = TimeSpan.FromSeconds(1.0 / GameConstants.TickRate);
        var nextTick = TimeSpan.Zero;

        while (!cancellation.IsCancellationRequested)
        {
            Tick();

            nextTick += tickLength;
            var wait = nextTick - clock.Elapsed;

            if (wait > TimeSpan.Zero)
            {
                cancellation.WaitHandle.WaitOne(wait);
            }
            else if (wait < -tickLength * 10)
            {
                // Far enough behind that catching up tick-by-tick would only dig
                // deeper. Give up the debt rather than spiral.
                _log.Info($"dropped {-wait.TotalMilliseconds:0}ms of tick backlog");
                nextTick = clock.Elapsed;
            }
        }
    }

    /// <summary>
    /// One server tick. Exposed so tests can drive the server without a clock —
    /// the same reason the simulation will be a pure function.
    /// </summary>
    public void Tick()
    {
        var now = DateTime.UtcNow;

        _frame++;

        ServiceClients();
        WriteStateUpdate();
        FlushBroadcast();

        AcceptConnections(now);
        ServiceJoiningConnections(now);

        foreach (var client in _clients)
            client.Connection.Flush();
    }

    // --- the roster and its clients -----------------------------------------

    private sealed class ClientPlayer
    {
        public required ClientConnection Connection { get; init; }

        public required RosterEntry Entry { get; init; }

        public ReceiveBuffer Received { get; } = new();

        public CommandStream Commands { get; } = new();
    }

    private void ServiceClients()
    {
        // Reverse order so a removal does not skip the next entry, and so the
        // roster indices of everything still to be serviced stay put.
        for (var i = _clients.Count - 1; i >= 0; i--)
        {
            var client = _clients[i];

            client.Connection.Receive(client.Received);

            if (client.Connection.IsClosed)
            {
                RemoveClient(i, client.Connection.CloseReason ?? "connection lost");
                continue;
            }

            ProcessCommands(client);

            if (client.Connection.IsClosed)
                RemoveClient(i, client.Connection.CloseReason ?? "disconnected");
        }
    }

    private void ProcessCommands(ClientPlayer client)
    {
        // At most ten completed commands per player per tick; the rest stay
        // buffered. Flood protection, from processClientCommands.gml.
        var remaining = ClientCommands.PerTickLimit;

        while (remaining > 0 && client.Commands.TryRead(client.Received, out var command))
        {
            remaining--;
            Handle(client, command);

            if (client.Connection.IsClosed)
                return;
        }
    }

    private void Handle(ClientPlayer client, in ClientCommand command)
    {
        var entry = client.Entry;
        var reader = new NetReader(command.Payload);

        switch (command.Type)
        {
            case MessageType.PlayerLeave:
                client.Connection.Close("client sent PLAYER_LEAVE");
                break;

            case MessageType.InputState:
                entry.Input = PlayerInput.Read(ref reader);
                break;

            case MessageType.PlayerChangeTeam:
            {
                var requested = (Team)reader.ReadUByte();

                // Anything that is not RED, BLUE or SPECTATOR means "any", and
                // with no simulation to balance against, "any" is spectator.
                entry.Team = requested is Team.Red or Team.Blue or Team.Spectator
                    ? requested
                    : Team.Spectator;

                ServerMessages.PlayerChangeTeam(_broadcast, IdOf(entry), entry.Team);
                break;
            }

            case MessageType.PlayerChangeClass:
            {
                var requested = (PlayerClass)reader.ReadUByte();
                if (!Enum.IsDefined(requested))
                    break; // getCharacterObject() == -1: the reference ignores it.

                entry.Class = requested;
                ServerMessages.PlayerChangeClass(_broadcast, IdOf(entry), entry.Class);
                break;
            }

            case MessageType.ChatBubble:
                ServerMessages.ChatBubble(_broadcast, IdOf(entry), reader.ReadUByte());
                break;

            case MessageType.ClientSettings:
                entry.QueueJump = reader.ReadUByte() != 0;
                break;

            case MessageType.PlayerChangeName:
            {
                var name = Gg2Text.Decode(command.Payload);
                if (name.Length > GameConstants.MaxPlayerNameLength)
                    name = name[..GameConstants.MaxPlayerNameLength];

                entry.Name = name;
                ServerMessages.PlayerChangeName(_broadcast, IdOf(entry), name);
                break;
            }

            // Everything below is either simulation the port does not have yet
            // or a subsystem a headless server deliberately does not implement.
            // The reference's own answer to REWARD_REQUEST from a server with no
            // auth is silence, and silence is a valid answer to all of these.
            case MessageType.BuildSentry:
            case MessageType.DestroySentry:
            case MessageType.DropIntel:
            case MessageType.Omnomnomnom:
            case MessageType.ToggleZoom:
            case MessageType.RewardRequest:
            case MessageType.RewardChallengeResponse:
            case MessageType.PluginPacket:
                break;

            default:
                // Unreachable: CommandStream only yields commands in the table.
                break;
        }
    }

    private byte IdOf(RosterEntry entry) => checked((byte)_roster.IndexOf(entry));

    private void RemoveClient(int clientIndex, string reason)
    {
        var client = _clients[clientIndex];
        var rosterIndex = _roster.IndexOf(client.Entry);

        _clients.RemoveAt(clientIndex);

        if (rosterIndex > 0)
        {
            _roster.RemoveAt(rosterIndex);

            // Both sides compact the list, so every index above this one shifts
            // down by one. The client does it in its PLAYER_LEAVE case.
            ServerMessages.PlayerLeave(_broadcast, (byte)rosterIndex);
        }

        _log.Info($"{client.Entry.Name} left ({reason}); {_roster.Count - 1} of {_options.PlayerLimit} slots used");
        client.Connection.Dispose();
    }

    // --- broadcast -----------------------------------------------------------

    private void WriteStateUpdate()
    {
        // Cadence is part of the contract: the client dead-reckons between the
        // position-bearing updates. Every tick but the seventh is an INPUTSTATE.
        var tier = _frame % 7 == 0 ? MessageType.QuickUpdate : MessageType.InputState;
        _stateWriter.Write(_broadcast, tier);
    }

    private void FlushBroadcast()
    {
        if (_broadcast.IsEmpty)
            return;

        // One identical byte stream to every client: one serialisation, N sends.
        // Keep it that way — it is why the send path is nearly free.
        var bytes = _broadcast.WrittenSpan;
        foreach (var client in _clients)
            client.Connection.Send(bytes);

        _broadcast.Reset();
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

                _log.Info($"refused {connection.RemoteAddress}: too many connections from that address");
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
        var context = new JoinContext
        {
            ServerName = _options.ServerName,
            CurrentMap = _options.Map,
            Password = _options.Password,
            PlayerLimit = _options.PlayerLimit,
            OccupiedSlots = OccupiedSlots(),
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
    private void CompleteJoin(JoiningConnection joining)
    {
        var connection = joining.Connection;
        var writer = new NetWriter(2048);

        // 1. The whole join burst, on this socket alone. The joiner's id is the
        //    roster size *before* it is added, and everything here has to arrive
        //    before the FULL_UPDATE that follows it — that update's player count
        //    is checked against the client's own roster.
        var playerId = checked((byte)_roster.Count);
        ServerMessages.JoinUpdate(writer, playerId, _match.CurrentMapArea);

        // The CHANGE_MAP is what makes the client load the map.
        ServerMessages.ChangeMap(writer, _options.Map, string.Empty);

        for (var i = 0; i < _roster.Count; i++)
        {
            var existing = _roster[i];
            ServerMessages.PlayerJoin(writer, existing.Name);
            ServerMessages.PlayerChangeClass(writer, (byte)i, existing.Class);
            ServerMessages.PlayerChangeTeam(writer, (byte)i, existing.Team);
        }

        _stateWriter.Write(writer, MessageType.FullUpdate);

        connection.Send(writer.WrittenSpan);

        // 2. Now it exists.
        var entry = new RosterEntry { Name = joining.Name, Connection = connection };
        _roster.Add(entry);
        _clients.Add(new ClientPlayer { Connection = connection, Entry = entry });

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
        _log.Info($"{entry.Name} joined as player {playerId}; {_roster.Count - 1} of {_options.PlayerLimit} slots used");
    }

    // --- map loading ---------------------------------------------------------

    private LevelMap LoadMap(string name)
    {
        if (name.Contains('/', StringComparison.Ordinal)
            || name.Contains('\\', StringComparison.Ordinal)
            || name.Contains("..", StringComparison.Ordinal))
        {
            // The client rejects a map name with a path separator in it outright
            // (ClientBeginStep.gml:47). Refuse to serve one for the same reason.
            throw new ArgumentException($"illegal map name '{name}'", nameof(name));
        }

        var path = Path.Combine(_options.MapDirectory, name + ".png");
        if (!File.Exists(path))
            throw new FileNotFoundException($"no built-in map at {path}", path);

        return LevelMap.FromPng(name, File.ReadAllBytes(path));
    }

    public void Dispose()
    {
        foreach (var client in _clients)
            client.Connection.Dispose();
        _clients.Clear();

        foreach (var joining in _joining)
            joining.Connection.Dispose();
        _joining.Clear();

        _listener?.Stop();
        _listener?.Dispose();
        _listener = null;
    }
}
