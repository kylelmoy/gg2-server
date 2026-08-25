using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Simulation;

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

    // The simulation and the read-only geometry it runs against. WorldState is
    // a value type and is held by value on purpose: docs/BOTAI.md item 1 wants
    // cloning a match to be a copy of bytes, and a field is where that starts.
    private CollisionWorld _collision = null!;
    private SpawnPoints _spawnPoints = null!;
    private WorldState _world;

    // The tick's outbound event channel, reused rather than recreated: it is a
    // few kilobytes of inline buffers and allocating one per tick is exactly
    // what the no-allocation rule rules out. The server never uses its forced
    // seeds — only the parity replayer does — so it stays a pure output here.
    private TickEvents _events;

    // Which spawn point a player gets. Server-side RNG the port is explicitly
    // free about (docs/PARITY.md) — the choice is broadcast in PLAYER_SPAWN, so
    // clients and a replay follow it rather than reproducing it.
    private readonly Random _spawnRandom = new();

    // Reused every tick; see AdvanceSimulation.
    private readonly PlayerInput[] _inputs = new PlayerInput[WorldState.MaxSlots];

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

        _collision = new CollisionWorld(map);
        _spawnPoints = new SpawnPoints(map);
        _world = default;

        // The reference's randomize(), and the only place the simulation's
        // random stream is ever written from outside it. A default WorldState
        // starts at seed 0, which is deterministic and right for a test or a
        // bot rollout and wrong for a live match — every server would then
        // scatter its first blast identically. Seeding it here keeps the clock
        // out of Gg2.Simulation, where it does not belong.
        _world.Random = new GmlRandom((uint)_spawnRandom.Next());

        _stateWriter = new StateUpdateWriter(_match, TryGetCharacter);

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
    /// the same reason the simulation is a pure function.
    /// </summary>
    public void Tick()
    {
        var now = DateTime.UtcNow;

        _frame++;

        ServiceClients();

        // Order matters and is the reference's. GameServerBeginStep.gml
        // serialises the update *before* any Character's Step runs, so one
        // update carries this tick's input alongside last tick's position — the
        // pairing docs/PARITY.md's replayer calls UpdatesPrecedeTheirTick, and
        // getting it the other way round makes the port look systematically
        // slow to every client.
        WriteStateUpdate();
        FlushBroadcast();

        AdvanceSimulation();

        AcceptConnections(now);
        ServiceJoiningConnections(now);

        foreach (var client in _clients)
            client.Connection.Flush();
    }

    // --- the simulation ------------------------------------------------------

    /// <summary>Runs one tick of physics for every character in the world.</summary>
    /// <remarks>
    /// Inputs are gathered per roster slot into a scratch buffer that lives for
    /// the life of the server: <c>Tick</c> allocating on the steady-state path
    /// is what docs/BOTAI.md item 1 rules out, and a per-tick array here would
    /// be exactly that.
    /// </remarks>
    private void AdvanceSimulation()
    {
        var count = Math.Min(_roster.Count, WorldState.MaxSlots);

        for (var i = 0; i < count; i++)
            _inputs[i] = _roster[i].Input;

        MatchSimulation.Tick(_collision, ref _world, _inputs.AsSpan(0, count), ref _events);

        BroadcastWeaponFire(count);
    }

    /// <summary>
    /// Tells every client about the shots this tick took, and with what seed.
    /// </summary>
    /// <remarks>
    /// <b>After the tick, not before, and that is the reference's order.</b> The
    /// <c>GameServer</c> instance is older than every <c>Character</c>, so GM8
    /// runs its Begin Step first: the state update goes into the send buffer,
    /// and then each character's Begin Step appends any <c>WEAPON_FIRE</c> it
    /// produced. A client therefore reads the update for tick <c>n</c> and only
    /// then learns that a shot was fired during it. Moving this above
    /// <see cref="WriteStateUpdate"/> would change the byte order of the stream
    /// and shift every client's pellets by a tick.
    /// <para>
    /// It is a broadcast rather than per-client because the seed is the same for
    /// everyone — that is the entire point of sending a seed instead of six
    /// pellet vectors.
    /// </para>
    /// <para>
    /// Note where these bytes actually leave: <see cref="FlushBroadcast"/> has
    /// already run for this tick, so a fire message sits in the buffer until the
    /// <em>next</em> tick's flush and goes out ahead of that tick's update. The
    /// stream a client reads is therefore update <c>n</c>, fire <c>n</c>, update
    /// <c>n+1</c> — the reference's own byte order, and the one
    /// <c>Gg2.Protocol.TraceParser</c> assumes when it files a fire message
    /// under the following tick.
    /// </para>
    /// </remarks>
    private void BroadcastWeaponFire(int count)
    {
        for (var slot = 0; slot < count; slot++)
        {
            if (!_events.Fired[slot])
                continue;

            var fire = _events.Fires[slot];
            ServerMessages.WeaponFire(
                _broadcast, (byte)slot, fire.X, fire.Y, fire.HSpeed, fire.VSpeed, fire.Seed);
        }
    }

    /// <summary>The wire view of a slot's character, for the state writer.</summary>
    private bool TryGetCharacter(int slot, out CharacterBlock character, out WeaponBlock weapon)
    {
        if (!_world.IsAlive(slot))
        {
            character = default;
            weapon = default;
            return false;
        }

        character = CharacterWire.ToBlock(_world.Characters[slot], _world.Weapons[slot]);
        weapon = CharacterWire.ToWeaponBlock(_world.Weapons[slot], _world.Characters[slot].Class);
        return true;
    }

    /// <summary>
    /// Spawns a player who is on a team and has no character, announcing it
    /// first.
    /// </summary>
    /// <remarks>
    /// The reference does this by setting <c>alarm[5] = 1</c> on a class or team
    /// change and letting <c>Player.events/Alarm 5.xml</c> call
    /// <c>PlayerSpawn()</c> between Begin Step and Step
    /// (<c>processClientCommands.gml:107</c>). A headless port has no alarms, so
    /// it spawns at the same point in the tick directly. Respawn *timing* after
    /// a death is phase 6's, along with the respawn timer this ignores.
    /// <para>
    /// <c>PLAYER_SPAWN</c> carries the point id and group rather than
    /// coordinates (<c>sendEventSpawn.gml</c>), so the message must go out
    /// before the character moves — every client resolves the pair against its
    /// own copy of the map and would otherwise place it a tick behind.
    /// </para>
    /// </remarks>
    private void TrySpawn(RosterEntry entry)
    {
        var slot = _roster.IndexOf(entry);

        if (slot < 0 || slot >= WorldState.MaxSlots || _world.IsAlive(slot))
            return;

        if (entry.Team is not (Team.Red or Team.Blue))
            return;

        // selectSpawnGroup.gml picks a forward spawn group by gamemode state.
        // Group 0 is the answer for every mode without control points, and no
        // shipped map defines any other group at all.
        const int Group = 0;

        var available = _spawnPoints.Count(entry.Team, Group);
        if (available == 0)
            return;

        // floor(random(numSpawnPoints)) — PlayerSpawn.gml.
        var point = _spawnRandom.Next(available);

        if (!_world.Spawn(_spawnPoints, slot, entry.Class, entry.Team, point, Group))
            return;

        ServerMessages.PlayerSpawn(_broadcast, (byte)slot, (byte)point, Group);
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

                // The client destroys the character on PLAYER_CHANGETEAM
                // (ClientBeginStep.gml), so the server must too or the two
                // disagree about who exists. Changing team also decides which
                // gates are solid, so keeping the old body would be wrong even
                // if the client kept it.
                _world.Clear(IdOf(entry));
                TrySpawn(entry);
                break;
            }

            case MessageType.PlayerChangeClass:
            {
                var requested = (PlayerClass)reader.ReadUByte();
                if (!Enum.IsDefined(requested))
                    break; // getCharacterObject() == -1: the reference ignores it.

                entry.Class = requested;
                ServerMessages.PlayerChangeClass(_broadcast, IdOf(entry), entry.Class);

                // A live character keeps playing as the class it spawned as —
                // the reference only queues a respawn when the player has no
                // character (processClientCommands.gml:107). Changing class
                // mid-life is a death in the reference, and death is phase 5.
                TrySpawn(entry);
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
            // down by one. The client does it in its PLAYER_LEAVE case, and the
            // world has to shift with it — a slot index IS a roster index, so a
            // character left in place would belong to the wrong player from here
            // on.
            _world.RemoveSlot(rosterIndex);

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
