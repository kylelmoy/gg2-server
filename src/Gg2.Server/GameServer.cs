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

    // botPopulationUpdate's half that needs a roster and a socket. Created in
    // Start, because it needs a `this` and the constructor does not have one to
    // hand out safely.
    private BotPopulationService? _population;

    // One bot's commands for one tick, reused across bots and across ticks: an
    // inline buffer, so this field is the whole storage and ServiceBots
    // allocates nothing.
    private PlayerCommandBuffer _commands;

    /// <summary>
    /// Ticks since start. <c>frame</c> in <c>GameServerBeginStep.gml</c>, and it
    /// is incremented <em>before</em> the cadence is tested — so the first tick
    /// the server ever sends is frame 1, an <c>INPUTSTATE</c>.
    /// </summary>
    private long _frame;

    /// <summary>
    /// <c>GameServer.syncTimer</c>: something changed the scoreboard, so the
    /// next tick owes every client a <c>CAPS_UPDATE</c> on top of its ordinary
    /// one.
    /// </summary>
    /// <remarks>
    /// Set from the collision stage in the reference too
    /// (<c>Character.events/Collision with IntelligenceBaseRed.xml:20</c>),
    /// which is after that tick's update was serialised — so the extra message
    /// really does go out on the following tick, and nothing here needs to
    /// arrange that.
    /// </remarks>
    private bool _capsSyncPending;

    /// <summary>
    /// <c>GameServer.impendingMapChange</c>: ticks until the map actually
    /// changes, or -1 when none is pending.
    /// </summary>
    /// <remarks>
    /// Server bookkeeping rather than simulation — nothing in
    /// <c>Gg2.Simulation</c> reads it. What the <em>world</em> knows is
    /// <c>WorldState.MapChanging</c>, the flag the physics consults.
    /// </remarks>
    private int _mapChangeCountdown = -1;

    /// <summary>The map now loaded, and the one the rotation moves on from.</summary>
    private string _currentMap = string.Empty;

    private string _nextMap = string.Empty;

    private int _nextArea = 1;

    /// <summary><c>GameServer.currentMapIndex</c> into the rotation.</summary>
    private int _rotationIndex;

    public GameServer(ServerOptions options, IServerLog log)
    {
        options.Validate();
        _options = options;
        _log = log;
    }

    /// <summary>Ticks elapsed. Test hook; not part of the protocol.</summary>
    public long Frame => _frame;

    public int PlayerCount => _roster.Count;

    /// <summary>The configuration this server was started with.</summary>
    public ServerOptions Options => _options;

    /// <summary>The roster's size, bots and slot 0 included.</summary>
    public int RosterCount => _roster.Count;

    /// <summary>
    /// The map that is loaded right now.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Not <see cref="ServerOptions.Map"/>.</b> Those are the same string
    /// until the first rotation and different for ever after, and reading the
    /// option instead is the bug <c>MapRotationTests</c> was written to catch: every
    /// player joining after round one was told the wrong map and then simulated a
    /// different world, silently.
    /// </remarks>
    public string CurrentMap => _currentMap;

    /// <summary>
    /// <c>getNumberOfOccupiedSlots</c>: humans only.
    /// </summary>
    /// <remarks>
    /// ⚠️ Neither bots nor the server's own reserved slot 0 count. Including
    /// either would make the fill target chase its own tail — every bot added
    /// would reduce the deficit it was added for.
    /// </remarks>
    public int HumanCount => OccupiedSlots();

    /// <summary>Every bot's roster slot, ascending.</summary>
    /// <remarks>
    /// Rebuilt per call rather than cached: a departure compacts the roster, so a
    /// cached list is stale the moment anybody leaves and the failure is a bot
    /// removed from somebody else's slot.
    /// </remarks>
    public IReadOnlyList<int> BotSlots
    {
        get
        {
            var slots = new List<int>();

            for (var i = 1; i < _roster.Count; i++)
            {
                if (_roster[i].IsBot)
                    slots.Add(i);
            }

            return slots;
        }
    }

    public Team TeamOf(int slot) => _roster[slot].Team;

    public PlayerClass ClassOf(int slot) => _roster[slot].Class;

    public IInputSource SourceOf(int slot) => _roster[slot].Source;

    public bool IsAlive(int slot) => _world.IsAlive(slot);

    /// <summary>
    /// How many players are on each team — <c>botPickTeam</c>'s count.
    /// </summary>
    /// <remarks>
    /// ⚠️ Everybody, humans included: a bot filling a server is balancing the
    /// game rather than balancing the bots. ⚠️ And slot 0 is skipped, because the
    /// server's own reserved player is a spectator that never plays.
    /// </remarks>
    public void CountTeams(out int red, out int blue)
    {
        red = 0;
        blue = 0;

        for (var i = 1; i < _roster.Count; i++)
        {
            if (_roster[i].Team == Team.Red)
                red++;
            else if (_roster[i].Team == Team.Blue)
                blue++;
        }
    }

    /// <summary>The port actually bound, which differs from the request when it was 0.</summary>
    public int BoundPort { get; private set; }

    /// <summary>Binds the listener and loads the map. Does not tick.</summary>
    public void Start()
    {
        _roster = new Roster(_options.ServerPlayerName);
        _world = default;

        // The reference's randomize(), and the only place the simulation's
        // random stream is ever written from outside it. A default WorldState
        // starts at seed 0, which is deterministic and right for a test or a
        // bot rollout and wrong for a live match — every server would then
        // scatter its first blast identically. Seeding it here keeps the clock
        // out of Gg2.Simulation, where it does not belong.
        _world.Random = new GmlRandom((uint)_spawnRandom.Next());

        // game_init.gml: Server_Respawntime is the ini's seconds times the tick
        // rate, floored at one tick — the reference special-cases zero seconds
        // to one tick, so a live server never has it off. (0 would disable
        // death-armed respawn entirely; that mode belongs to the parity
        // replayer, not here.)
        _world.RespawnTime = Math.Max(1, _options.RespawnTimeSeconds * GameConstants.TickRate);

        // Slot 0, the server's own reserved player: a spectator that always
        // exists, now in the world as well as the roster.
        _world.AddPlayer(0);

        // ⚠️ Before OpenMap, because OpenMap is what points it at a map — and
        // after AddPlayer(0), because its first Retarget walks the roster.
        //
        // Seeded from the port, so two servers on one machine roll differently and
        // one server rolls the same way every run. ⚠️ NOT WorldState.Random: that
        // stream is observable simulation and a bot joining must not move it.
        _population = new BotPopulationService(this, _log, (uint)_options.Port);

        var map = OpenMap(_options.Map, area: 1);

        _stateWriter = new StateUpdateWriter(
            _match, TryGetCharacter, TryGetSentry, Objective, Statistics, Dominating);

        _listener = new TcpListener(IPAddress.Any, _options.Port);
        _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
        _listener.Start();
        BoundPort = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _log.Info(
            $"listening on {BoundPort}, map {map.Name} ({_match.GameMode}, " +
            $"{map.Walkmask.Width}x{map.Walkmask.Height} cells), {_options.PlayerLimit} slots");
    }

    /// <summary>
    /// Loads a map and rebuilds everything that depends on it: the geometry, the
    /// spawn points, the HUD placeholders, and the round.
    /// </summary>
    /// <remarks>
    /// Shared by <see cref="Start"/> and the map change, so a server on its
    /// tenth round is in the same shape as one that has just started — which is
    /// the property a long-running headless server needs and the one thing a
    /// single-shot <c>Start</c> could never be tested for.
    /// <para>
    /// The world's <em>players</em> and its random stream survive; everything
    /// else about the old map does not. <see cref="Rounds.ResetForNewMap"/> is
    /// what clears the rest, and the caller runs it before calling this.
    /// </para>
    /// </remarks>
    /// <param name="name">The map to load.</param>
    /// <param name="area">
    /// <c>global.currentMapArea</c>, 1-based. A multi-area map (Invasion) keeps
    /// its teams and advances the area instead of rotating.
    /// </param>
    private LevelMap OpenMap(string name, int area)
    {
        // ⚠️ Narrowed to the area first, and everything below sees only that.
        // A tall map can be several rounds stacked vertically and the reference
        // destroys the entities outside the one being played — see
        // LevelMap.ForArea. cp_dirtbowl is the only shipped map it affects, and
        // it is also the only attack/defend map, so the control-point work
        // depends on this.
        var map = LoadMap(name).ForArea(area);

        _collision = new CollisionWorld(map);
        _spawnPoints = new SpawnPoints(map);
        _match = new MatchState(map, _roster, _options)
        {
            CurrentMapArea = (byte)area,
        };

        // Both briefcases on their bases. The map cannot live in the state, so
        // the base positions are copied in here — the same shape as seeding the
        // RNG in Start.
        Intel.Place(ref _world, map);

        // Both generators at full health, for the same reason and in the same
        // way: the map cannot live in the state, so their positions are copied
        // in here.
        Generators.Place(ref _world, map);

        // The round's own configuration, with the ini's own clamps
        // (game_init.gml:96, 101 — both max(1, min(255, ...))). A zero means
        // "no limit" in WorldState and is unreachable in the reference, so the
        // floor is applied here rather than left to chance.
        //
        // ⚠️ GameMode first: ControlPoints.Place reads it to decide which point
        // objects the map places and in what order.
        _world.GameMode = _match.GameMode;
        _world.CapLimit = Math.Max(1, (int)_options.CapLimit);
        _world.TimeLimitMinutes = Math.Max(1, (int)_options.TimeLimitMinutes);

        // game_init.gml:103, the same max(1, ...) floor the other two limits get
        // — 0 means "no limit" in WorldState and is unreachable in the
        // reference.
        _world.KillLimit = Math.Max(1, (int)_options.KillLimit);

        // GameServerCreate.gml:88: `seconds * 30 * 0.2`, a fifth of the ticks it
        // buys, because the countdown is spent at 0.2 a tick. ⚠️ Its own floor
        // is max(0, ...) and not max(1, ...) — zero really does mean "no spawn
        // protection" here, so there is nothing to escape.
        _world.TdmInvulnerabilityTicks = (int)(_options.TdmInvulnerabilitySeconds * 30 * 0.2);

        // game_init.gml:105 and the [Classlimits] section. ⚠️ A zeroed
        // ClassLimits would mean every class is full, so this is not optional
        // even on a server that enforces nothing — see WorldState.ClassLimits.
        _world.Autobalance = _options.Autobalance;

        for (var i = 0; i < TeamBalance.ClassCount; i++)
            _world.ClassLimits[i] = _options.ClassLimit;

        // ControlPointHUD.events/Create.xml decides which of the two
        // control-point arrangements a map is by looking for a setup gate.
        _world.AttackDefend = GameModes.HasSetupGate(map);

        // The control points, and the capture zones that name them. The zones
        // are read-only geometry and go to the CollisionWorld; the points are
        // state and go to the world.
        ControlPoints.Place(ref _world, map, out var zones);
        _collision.SetCaptureZones(zones);

        // Everything a gamemode's HUD sets in its Create event: the round clock,
        // and for KotH and DKotH the two hold timers, the pregame lock and (in
        // DKotH) each hill's starting owner. After Place, because it writes to
        // the points.
        Rounds.Start(ref _world);

        _currentMap = name;

        // ⚠️ Every bot's follower, route and blacklist are indices into the graph
        // that was loaded a moment ago, so a rotation has to hand them the new one
        // — see ScriptedBot.SetMap. The reference re-teams every bot across a map
        // change (botReteamAll) and frees the nav state on the way
        // (botNavRelease); this is both, in the one place a map is opened.
        _population?.MapChanged(map, area, _options.NavCacheDirectory);

        return map;
    }

    /// <summary>
    /// <c>GameServerBeginStep.gml:102-131</c>: the round is over, so tell
    /// everybody, then change the map ten seconds later.
    /// </summary>
    /// <remarks>
    /// <b>Run at the top of the tick, after that tick's state update has already
    /// been written and before the simulation runs.</b> That is the reference's
    /// order exactly: the check is in the server's Begin Step, below
    /// <c>serializeState</c>, and the round was decided in the <em>previous</em>
    /// tick's Step (<see cref="Rounds.Step"/>). So a win on tick N is announced
    /// at the start of N+1.
    /// </remarks>
    private void ServeRoundEnd()
    {
        if (_mapChangeCountdown > 0)
            _mapChangeCountdown--;

        if (_world.RoundDecided && !_world.MapChanging)
        {
            // `if(global.winners == TEAM_RED and global.currentMapArea <
            // global.totalMapAreas)` — red pushing through an Invasion map's
            // areas replays the same map with the next area; anything else
            // rotates and goes back to area 1.
            if (_world.Winner == Team.Red && _match.CurrentMapArea < _match.TotalMapAreas)
            {
                _nextArea = _match.CurrentMapArea + 1;
                _nextMap = _currentMap;
            }
            else
            {
                _nextArea = 1;
                _nextMap = NextMapInRotation();
            }

            Rounds.BeginMapChange(ref _world);
            _mapChangeCountdown = Rounds.MapChangeDelay;

            ServerMessages.MapEnd(_broadcast, _nextMap, _world.Winner, (byte)_nextArea);
            _log.Info($"round over: {_world.Winner} wins; {_nextMap} area {_nextArea} in ten seconds");
        }

        if (_mapChangeCountdown != 0)
            return;

        // `if(impendingMapChange == 0)`: the change itself. The reset clears
        // mapchanging, the room reloads, and every player is given
        // `alarm[5] = 1` so the whole server spawns in this same tick's alarm
        // stage.
        _mapChangeCountdown = -1;

        Rounds.ResetForNewMap(ref _world, firstArea: _nextArea == 1);

        // The roster's own copy of the team, which the wire and the joining
        // path read. `with(Player) ... team = TEAM_SPECTATOR` in the reference
        // is one assignment because there is one Player; here the world and the
        // roster both hold it, so both are set.
        if (_nextArea == 1)
        {
            foreach (var entry in _roster.Entries)
                entry.Team = Team.Spectator;
        }

        var map = OpenMap(_nextMap, _nextArea);

        // ServerChangeMap.gml. The MD5 is empty for a built-in map, which is
        // every map this server serves.
        ServerMessages.ChangeMap(_broadcast, map.Name, string.Empty);
        _log.Info($"now playing {map.Name} ({_match.GameMode}), area {_nextArea}");
    }

    /// <summary>
    /// <c>nextMapInRotation.gml</c>: the next loadable entry after the current
    /// one, wrapping.
    /// </summary>
    /// <remarks>
    /// The reference calls <c>show_error</c> and exits when nothing in the
    /// rotation is loadable. A headless server stays up instead and repeats the
    /// map it is on — a server that stops serving is the worse failure, and the
    /// log says which map it picked either way.
    /// </remarks>
    private string NextMapInRotation()
    {
        var rotation = _options.MapRotation;
        if (rotation.Count == 0)
            return _currentMap;

        for (var i = 1; i <= rotation.Count; i++)
        {
            var index = (_rotationIndex + i) % rotation.Count;
            var candidate = rotation[index];

            if (!MapExists(candidate))
                continue;

            _rotationIndex = index;
            return candidate;
        }

        _log.Info($"no loadable map in the rotation; staying on {_currentMap}");
        return _currentMap;
    }

    private bool MapExists(string name)
    {
        // An illegal name — a path separator, say — would make LoadMap throw, so
        // it counts as absent rather than reaching the wire in a MAP_END.
        if (name.Length == 0
            || name.Contains('/', StringComparison.Ordinal)
            || name.Contains('\\', StringComparison.Ordinal)
            || name.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        return File.Exists(Path.Combine(_options.MapDirectory, name + ".png"));
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

        // ⚠️ Before ServiceBots, so a bot added this tick is polled this tick
        // rather than pressing nothing for one frame. GameServerBeginStep.gml
        // calls botPopulationUpdate ahead of the input pass for the same reason.
        _population?.Service();

        ServiceBots();

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
        // Before the tick, because it reads the round the *previous* tick
        // decided — the reference checks it in the server's Begin Step, which is
        // the first thing in a frame. See ServeRoundEnd.
        ServeRoundEnd();

        var count = Math.Min(_roster.Count, WorldState.MaxSlots);

        GatherInputs(count);

        MatchSimulation.Tick(_collision, ref _world, _inputs.AsSpan(0, count), ref _events, _spawnPoints);

        // Fires before deaths, because a weapon fires in a character's Begin
        // Step and a character dies in its End Step — so within one tick the
        // reference appends the WEAPON_FIRE messages first. A sentry's landing
        // is also a Begin Step (the sentry's, after every character's) and its
        // destruction an End Step, so each pair straddles the deaths the same
        // way. A spawn is the alarm stage, between the Begin Steps and the End
        // Steps, so it sits between the landings and the deaths.
        // First of all of them: a generator's Begin Step runs before every
        // character's, so a destruction is the earliest thing a tick can
        // report — and the explosion's victims die in their own End Steps, so
        // this message must precede the PLAYER_DEATHs it caused.
        BroadcastGeneratorDestructions();

        BroadcastWeaponFire(count);
        BroadcastUbers(count);
        BroadcastControlPointUnlock();
        BroadcastArena();
        BroadcastIntelDrops(count);
        BroadcastIntelReturns();
        BroadcastSentryLandings(count);
        BroadcastSpawns(count);
        BroadcastIntelGrabsAndCaptures(count);
        BroadcastPlayerDeaths(count);
        BroadcastSentryDestructions(count);

        // Last: the only in-tick BALANCE comes from a death's own
        // ServerBalanceTeams, which runs after doEventPlayerDeath.
        BroadcastTeamBalance();
    }

    /// <summary>
    /// Asks every slot's <see cref="IInputSource"/> what it is pressing.
    /// </summary>
    /// <remarks>
    /// One path for sockets and bots alike — docs/ARCHITECTURE.md's seam. The
    /// world handed to a source is the state as the previous tick left it,
    /// which is the same thing an <c>INPUTSTATE</c> that arrived before
    /// <see cref="ServiceClients"/> was decided against.
    /// <para>
    /// <b>The batched form is used where a source offers it</b>
    /// (docs/BOTAI.md item 5). Slots are grouped by source <em>identity</em>,
    /// because that is what a batching source is: one policy driving several
    /// bots, which wants one call with all of their slots rather than one call
    /// each. The grouping is here rather than deferred precisely because item 5
    /// says adding it afterwards is a wide change — a call site that assumed
    /// per-slot polling has to be rewritten, and this is that call site.
    /// </para>
    /// <para>
    /// Everything below is stack or long-lived buffers: this runs every tick.
    /// </para>
    /// </remarks>
    private void GatherInputs(int count)
    {
        Span<bool> polled = stackalloc bool[WorldState.MaxSlots];
        Span<int> group = stackalloc int[WorldState.MaxSlots];

        // Hoisted, not per group: a stackalloc inside the loop would grow the
        // frame once per batching source and never give it back.
        Span<PlayerInput> outputs = stackalloc PlayerInput[WorldState.MaxSlots];

        for (var i = 0; i < count; i++)
        {
            if (polled[i])
                continue;

            var source = _roster[i].Source;

            if (source is not IBatchInputSource batch)
            {
                _inputs[i] = source.Poll(new WorldView(in _world, _collision), i, (int)_frame);
                polled[i] = true;
                continue;
            }

            // Every remaining slot driven by this same instance, in ascending
            // order — the roster order every other per-slot loop in the project
            // walks.
            var size = 0;
            for (var j = i; j < count; j++)
            {
                if (!polled[j] && ReferenceEquals(_roster[j].Source, source))
                {
                    group[size++] = j;
                    polled[j] = true;
                }
            }

            batch.PollMany(
                new WorldView(in _world, _collision), group[..size], outputs[..size], (int)_frame);

            for (var k = 0; k < size; k++)
                _inputs[group[k]] = outputs[k];
        }
    }

    /// <summary>
    /// <c>UNLOCKCP</c>: the pregame lock has come off.
    /// </summary>
    /// <remarks>
    /// From the HUD's own Step, so it sits with the Begin Step messages rather
    /// than with the collision ones. A capture also owes a <c>CAPS_UPDATE</c>,
    /// which the point's own Step asks for the same way a cap does — see
    /// <see cref="WriteStateUpdate"/>.
    /// </remarks>
    /// <summary>
    /// The Arena round transitions — <c>ARENA_RESTART</c>,
    /// <c>ARENA_STARTROUND</c>, <c>ARENA_ENDROUND</c> and
    /// <c>ARENA_WAITFORPLAYERS</c>.
    /// </summary>
    /// <remarks>
    /// From the HUD's own Step, so they sit with the other Begin/Step-stage
    /// messages. At most one can fire in a tick: each transition leaves the
    /// state machine in a phase whose own exit condition cannot already be met.
    /// A round ending also owes a scoreboard update, because the round scores
    /// it carries are in the <c>FULL_UPDATE</c> tail rather than in the
    /// message a client can miss.
    /// </remarks>
    private void BroadcastArena()
    {
        if (_events.ArenaRestarted)
            ServerMessages.ArenaRestart(_broadcast);

        if (_events.ArenaRoundStarted)
            ServerMessages.ArenaStartRound(_broadcast);

        if (_events.ArenaWaitingForPlayers)
            ServerMessages.ArenaWaitForPlayers(_broadcast);

        if (!_events.ArenaRoundEnded)
            return;

        // ⚠️ Both lists are chosen before either is written, because the two
        // counts lead the message — see ServerMessages.ArenaEndRound. And
        // SelectMvps writes back a rounded healing total, which is the
        // reference's own assignment, so it takes the world by reference.
        Span<ArenaMvp> red = stackalloc ArenaMvp[Scoring.MaxMvps];
        Span<ArenaMvp> blue = stackalloc ArenaMvp[Scoring.MaxMvps];

        var redCount = Scoring.SelectMvps(ref _world, Team.Red, red);
        var blueCount = Scoring.SelectMvps(ref _world, Team.Blue, blue);

        ServerMessages.ArenaEndRound(
            _broadcast,
            _events.ArenaRoundWinner,
            WireQuantise.ToUByte(_world.ArenaRedWins),
            WireQuantise.ToUByte(_world.ArenaBlueWins),
            red[..redCount],
            blue[..blueCount]);

        _capsSyncPending = true;
    }

    private void BroadcastControlPointUnlock()
    {
        // syncTimer, wherever the simulation asked for one: a captured point, a
        // capture meter crossing a threshold, or the five-second warning before
        // the pregame lock lifts.
        if (_world.ScoreboardSyncRequested)
        {
            _capsSyncPending = true;
            _world.ScoreboardSyncRequested = false;
        }

        if (!_world.UnlockedControlPointsThisTick)
            return;

        ServerMessages.UnlockControlPoints(_broadcast);
        _world.UnlockedControlPointsThisTick = false;
    }

    /// <summary>
    /// <c>DROP_INTEL</c> for a carrier who was ubered into putting it down.
    /// </summary>
    /// <remarks>
    /// A Begin Step, like a <c>WEAPON_FIRE</c>, and above the uber messages'
    /// own line in the same event — so it goes out in this part of the stream.
    /// A drop caused by a death is never broadcast; see
    /// <see cref="TickEvents.DroppedIntel"/>.
    /// </remarks>
    private void BroadcastIntelDrops(int count)
    {
        for (var slot = 0; slot < count; slot++)
            if (_events.DroppedIntel[slot])
                ServerMessages.DropIntel(_broadcast, (byte)slot);
    }

    /// <summary>
    /// <c>RETURN_INTEL</c>: a dropped briefcase's own alarm fired.
    /// </summary>
    /// <remarks>
    /// The alarm stage, so between the Begin Steps and the collision events —
    /// beside the spawns rather than beside the grabs.
    /// </remarks>
    private void BroadcastIntelReturns()
    {
        if (_events.ReturnedRedIntel)
            ServerMessages.ReturnIntel(_broadcast, Team.Red);

        if (_events.ReturnedBlueIntel)
            ServerMessages.ReturnIntel(_broadcast, Team.Blue);
    }

    /// <summary>
    /// <c>BALANCE</c>, both shapes: the notice, and each player actually moved.
    /// </summary>
    /// <remarks>
    /// Drained rather than swept with the rest of the tick's events, because two
    /// of the three call sites are <em>outside</em> the tick — a departure and a
    /// team change are both handled where the command arrives. The third, a
    /// death, is inside it and is drained by
    /// <see cref="BroadcastTickEvents"/>'s call to this.
    /// <para>
    /// ⚠️ Each flag is cleared as it is sent, so calling this twice in a tick
    /// cannot double a message.
    /// </para>
    /// </remarks>
    private void BroadcastTeamBalance()
    {
        if (_events.BalanceNoticed)
        {
            ServerMessages.BalanceNotice(_broadcast);
            _events.BalanceNoticed = false;
        }

        for (var slot = 0; slot < _roster.Count && slot < WorldState.MaxSlots; slot++)
        {
            if (!_events.Balanced[slot])
                continue;

            _roster[slot].Team = _world.Players[slot].Team;
            _roster[slot].Class = _events.BalancedClass[slot];

            ServerMessages.BalanceSwitch(_broadcast, (byte)slot, _events.BalancedClass[slot]);
            _events.Balanced[slot] = false;
        }
    }

    /// <summary>
    /// <c>GENERATOR_DESTROY</c>: a generator has run out of hit points.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The message names the team that lost one</b>, not the team that
    /// won — see <see cref="ServerMessages.GeneratorDestroy"/>. Both can go out
    /// on one tick, and the reference sends both.
    /// <para>
    /// ⚠️ <b>It also shortens every later HUD block by four bytes</b>, because
    /// the destroyed generator stops being serialised. A client stays in step
    /// because it destroys its own instance on this message; anything else
    /// reading the stream has to track it too
    /// (<see cref="WorldShape.GeneratorCount"/>).
    /// </para>
    /// </remarks>
    private void BroadcastGeneratorDestructions()
    {
        if (_events.RedGeneratorDestroyed)
            ServerMessages.GeneratorDestroy(_broadcast, Team.Red);

        if (_events.BlueGeneratorDestroyed)
            ServerMessages.GeneratorDestroy(_broadcast, Team.Blue);
    }

    /// <summary>
    /// <c>GRAB_INTEL</c> and <c>SCORE_INTEL</c>, from the collision stage.
    /// </summary>
    /// <remarks>
    /// After the spawns and before the deaths, because collision events sit
    /// between the alarm stage and End Step. A capture also forces an extra
    /// <c>CAPS_UPDATE</c> out on the next tick, which is the reference's
    /// <c>GameServer.syncTimer = 1</c> — see <see cref="WriteStateUpdate"/>.
    /// </remarks>
    private void BroadcastIntelGrabsAndCaptures(int count)
    {
        for (var slot = 0; slot < count; slot++)
        {
            if (_events.GrabbedIntel[slot])
                ServerMessages.GrabIntel(_broadcast, (byte)slot);

            if (!_events.ScoredIntel[slot])
                continue;

            ServerMessages.ScoreIntel(_broadcast, (byte)slot);
            _capsSyncPending = true;
        }
    }

    /// <summary>One cell of the domination matrix. See <see cref="DominationLookup"/>.</summary>
    private byte Dominating(int killer, int victim) =>
        Dominations.Get(_world, killer, victim);

    /// <summary>One roster entry's scoreboard. See <see cref="StatsLookup"/>.</summary>
    /// <remarks>
    /// A slot index is a roster index, so this is a straight read — no lookup
    /// table, and a slot with no player answers with zeroes rather than
    /// throwing, which is what a roster entry mid-join needs.
    /// </remarks>
    private PlayerStatsBlock Statistics(int slot) => new(
        Scoring.Get(_world, slot, StatIndex.Kills),
        Scoring.Get(_world, slot, StatIndex.Deaths),
        Scoring.Get(_world, slot, StatIndex.Caps),
        Scoring.Get(_world, slot, StatIndex.Assists),
        Scoring.Get(_world, slot, StatIndex.Destruction),
        Scoring.Get(_world, slot, StatIndex.Stabs),
        Scoring.Get(_world, slot, StatIndex.Healing),
        Scoring.Get(_world, slot, StatIndex.Defenses),
        Scoring.Get(_world, slot, StatIndex.Invulns),
        Scoring.Get(_world, slot, StatIndex.Bonus),
        Scoring.Get(_world, slot, StatIndex.Points));

    /// <summary>The objective as the state writer wants it. See <see cref="ObjectiveLookup"/>.</summary>
    private ObjectiveBlock Objective()
    {
        // Reused across ticks: the block is written every update and a fresh
        // array per tick is exactly the steady-state allocation docs/BOTAI.md
        // rules out. Its length is fixed for as long as a map is loaded.
        if (_pointBlocks.Length != _world.ControlPointCount)
            _pointBlocks = new ControlPointBlock[_world.ControlPointCount];

        for (var i = 0; i < _pointBlocks.Length; i++)
            _pointBlocks[i] = ControlPoints.ToBlock(_world.ControlPoints[i]);

        return new ObjectiveBlock(
            Intel.ToBlock(_world.RedIntel),
            Intel.ToBlock(_world.BlueIntel),
            _world.RedCaps,
            _world.BlueCaps,
            _world.TimeLimitMinutes,
            _world.RoundTimer,
            _pointBlocks,
            _world.ControlPointUnlock,
            _world.RedTimer,
            _world.BlueTimer,
            _world.SetupTimer,
            (byte)_world.ArenaState,
            _world.ArenaRedWins,
            _world.ArenaBlueWins,
            (sbyte)_world.ArenaWinner,
            _world.ArenaEndCount,
            _world.ArenaRoundStart,
            Generators.ToBlock(_world.RedGenerator),
            Generators.ToBlock(_world.BlueGenerator),
            _world.KillLimit,
            _world.TdmInvulnerabilityTicks);
    }

    private ControlPointBlock[] _pointBlocks = [];

    /// <summary>
    /// Tells every client who respawned this tick and at which spawn point —
    /// <c>PLAYER_SPAWN</c>, from <c>sendEventSpawn.gml</c>.
    /// </summary>
    /// <remarks>
    /// The message carries the point id and group rather than coordinates;
    /// every client resolves the pair against its own copy of the map, which is
    /// the property the parity anchors lean on.
    /// </remarks>
    private void BroadcastSpawns(int count)
    {
        for (var slot = 0; slot < count; slot++)
        {
            if (!_events.Spawned[slot])
                continue;

            var spawn = _events.Spawns[slot];
            ServerMessages.PlayerSpawn(_broadcast, (byte)slot, spawn.SpawnPointId, spawn.Group);
        }
    }

    /// <summary>
    /// Tells every client where a sentry that just finished falling came to
    /// rest — <c>SENTRY_POSITION</c>, from <c>Sentry.events/Begin Step.xml</c>.
    /// </summary>
    /// <remarks>
    /// The position is read back off the world: a landed sentry does not move
    /// again between the event and this broadcast, and the message quantises
    /// with <c>round(x*5)</c> exactly as the sentry's own update block does.
    /// </remarks>
    private void BroadcastSentryLandings(int count)
    {
        for (var slot = 0; slot < count; slot++)
        {
            if (!_events.SentryLanded[slot] || !_world.HasSentry[slot])
                continue;

            ref readonly var sentry = ref _world.Sentries[slot];
            ServerMessages.SentryPosition(
                _broadcast,
                (byte)slot,
                WireQuantise.ToUShort(Gml.Round(sentry.X * 5)),
                WireQuantise.ToUShort(Gml.Round(sentry.Y * 5)));
        }
    }

    /// <summary>
    /// Tells every client about the sentries the tick destroyed —
    /// <c>DESTROY_SENTRY</c>, with the killer and their healer.
    /// </summary>
    private void BroadcastSentryDestructions(int count)
    {
        for (var slot = 0; slot < count; slot++)
        {
            if (!_events.SentryDestroyed[slot])
                continue;

            var destruction = _events.SentryDestructions[slot];
            ServerMessages.DestroySentry(
                _broadcast,
                (byte)slot,
                destruction.Killer,
                destruction.Assistant,
                destruction.Source);
        }
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

    /// <summary>
    /// Tells every client who died this tick, and who gets the credit.
    /// </summary>
    /// <remarks>
    /// The same placement argument as <see cref="BroadcastWeaponFire"/>, one
    /// event stage later: <c>Character.events/End Step.xml:42</c> calls
    /// <c>sendEventPlayerDeath</c>, so these bytes follow the tick's update and
    /// its fires and are read by a client under the next tick's flush.
    /// <para>
    /// <b>The character is already gone from the world by the time this runs</b>
    /// — <see cref="Deaths.Kill"/> cleared the slot during the tick — so
    /// everything the message needs is in the event and none of it can be read
    /// back out of <see cref="_world"/>. That is the whole reason
    /// <see cref="TickEvents"/> carries the resolved killer and assistant rather
    /// than just a flag.
    /// </para>
    /// <para>
    /// <b>No respawn is scheduled here.</b> The reference arms
    /// <c>player.alarm[5]</c> in <c>Character.events/Destroy.xml</c> and a
    /// dead player stays dead until it fires; this server has no player-side
    /// timers yet, so a killed character simply stays absent. docs/PLAN.md
    /// phase 6.
    /// </para>
    /// </remarks>
    private void BroadcastPlayerDeaths(int count)
    {
        for (var slot = 0; slot < count; slot++)
        {
            if (!_events.Died[slot])
                continue;

            var death = _events.Deaths[slot];
            ServerMessages.PlayerDeath(
                _broadcast, (byte)slot, death.Killer, death.Assistant, death.Source);
        }
    }

    /// <summary>
    /// Tells every client which medics filled a charge this tick and which
    /// spent one.
    /// </summary>
    /// <remarks>
    /// Both come out of the medic's Begin Step — <c>UBER_CHARGED</c> from
    /// <c>Medigun.events/User Event 1.xml</c> and <c>UBER</c> from
    /// <c>User Event 2.xml</c> — so they belong in the same place in the stream
    /// as a <c>WEAPON_FIRE</c>, between the tick's state update and its deaths.
    /// <para>
    /// Per slot rather than two sweeps, because the reference emits them
    /// per instance: one medic's charge message precedes the next medic's, and
    /// a single medic's <c>UBER_CHARGED</c> precedes its own <c>UBER</c> because
    /// the attack key is handled a few lines above the right-click. The two can
    /// only coincide on the tick a medic tops up and pops in the same frame,
    /// which needs the beam and the right-click held together.
    /// </para>
    /// <para>
    /// <b>Body is one byte, the player.</b> <c>sendEventUberReady.gml</c> and
    /// <c>sendEventUber.gml</c> are two lines each and neither says anything
    /// else — the charge itself rides the medic's class-specific byte in the
    /// state update.
    /// </para>
    /// </remarks>
    private void BroadcastUbers(int count)
    {
        for (var slot = 0; slot < count; slot++)
        {
            if (_events.UberCharged[slot])
                ServerMessages.UberCharged(_broadcast, (byte)slot);

            if (_events.Ubered[slot])
                ServerMessages.Uber(_broadcast, (byte)slot);
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
        weapon = CharacterWire.ToWeaponBlock(_world, slot);
        return true;
    }

    /// <summary>The wire view of a slot's sentry, for the state writer.</summary>
    private bool TryGetSentry(int slot, out SentryBlock sentry)
    {
        if (slot >= WorldState.MaxSlots || !_world.HasSentry[slot])
        {
            sentry = default;
            return false;
        }

        sentry = CharacterWire.ToSentryBlock(_world.Sentries[slot]);
        return true;
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

    /// <summary>
    /// The bots' half of the roster service loop: the commands a key cannot
    /// express, asked for once per bot per tick.
    /// </summary>
    /// <remarks>
    /// <c>GameServerBeginStep.gml:79-85</c> puts <c>botInputUpdate(player)</c>
    /// in the same loop and the same position as
    /// <c>processClientCommands(player, i)</c> — a bot is serviced exactly where
    /// a socket is. The <em>keys</em> half of that is
    /// <see cref="GatherInputs"/>, which runs for every slot including this one;
    /// what is left here is <see cref="ICommandSource"/>.
    /// <para>
    /// A separate ascending pass rather than one interleaved loop, because
    /// <see cref="ServiceClients"/> walks its clients <em>backwards</em> so a
    /// disconnect cannot skip the next one — and a departure compacts the
    /// roster, so running after it is what keeps every slot index below stable.
    /// The cost is that a bot's broadcasts follow the humans' within a tick
    /// where the reference interleaves them by roster index. Nothing observes
    /// it: commands from different players produce independent messages, and no
    /// recording in this project contains a bot at all.
    /// </para>
    /// </remarks>
    private void ServiceBots()
    {
        for (var slot = 0; slot < _roster.Count; slot++)
        {
            var entry = _roster[slot];

            if (entry.Source is not ICommandSource source)
                continue;

            _commands.Clear();
            source.PollCommands(new WorldView(in _world, _collision), slot, (int)_frame, ref _commands);

            for (var i = 0; i < _commands.Count; i++)
            {
                Execute(entry, _commands[i]);

                // ⚠️ A command can move the roster under this loop: a class or
                // team change can kill, and a kill can autobalance somebody
                // across. It cannot *remove* an entry — only a disconnect does
                // that, and a bot has no socket — so the index stays this
                // player's. Re-reading the entry is not enough on its own,
                // which is why every Execute* below takes the entry and asks
                // IdOf for the slot rather than trusting one captured here.
            }
        }
    }

    /// <summary>
    /// Adds a bot to the roster and joins it to the game the way a client is
    /// joined — <c>Scripts/Bots/botAdd.gml</c>.
    /// </summary>
    /// <remarks>
    /// The reference's order exactly: roster insertion first, so the id is the
    /// index it lands at; <c>PLAYER_JOIN</c> broadcast <em>before</em> the team
    /// and class are known; <c>checkClasslimits</c>; then <c>CHANGETEAM</c> and
    /// <c>CHANGECLASS</c> — ⚠️ <b>team first</b>, which is the opposite of the
    /// order <see cref="CompleteJoin"/> replays an existing roster in, and is
    /// what the reference does. Finally <c>alarm[5] = 1</c> for anything but a
    /// spectator: a spawn in this same tick's alarm stage.
    /// <para>
    /// ⚠️ <b>There is no <c>FULL_UPDATE</c> burst and no
    /// <see cref="ClientPlayer"/>.</b> Those exist to catch a joining socket up;
    /// a bot has no socket, and the state it needs is the one it is already
    /// standing in.
    /// </para>
    /// <para>
    /// Bots do not count against <see cref="ServerOptions.PlayerLimit"/> —
    /// <c>getNumberOfOccupiedSlots</c> subtracts them — but they do occupy
    /// roster slots, and <see cref="WorldState.MaxSlots"/> is the hard ceiling
    /// on those.
    /// </para>
    /// </remarks>
    /// <param name="source">
    /// What this bot presses. May also implement <see cref="ICommandSource"/>
    /// and <see cref="IBatchInputSource"/>.
    /// </param>
    /// <returns>The roster slot the bot landed in.</returns>
    public int AddBot(string name, Team team, PlayerClass playerClass, IInputSource source)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(source);

        if (_roster.Count >= WorldState.MaxSlots)
            throw new InvalidOperationException($"the roster is full at {WorldState.MaxSlots} slots");

        var entry = new RosterEntry { Name = name, IsBot = true, Source = source };
        var slot = _roster.Add(entry);
        _world.AddPlayer(slot);

        ServerMessages.PlayerJoin(_broadcast, entry.Name);

        // botAdd.gml:26 — the class it counts with is the one checkClasslimits
        // handed back, not the one asked for.
        var allowed = Classlimits.Pick(ref _world, slot, team, playerClass);

        entry.Team = team;
        entry.Class = allowed;
        _world.Players[slot].Team = team;
        _world.Players[slot].Class = allowed;

        ServerMessages.PlayerChangeTeam(_broadcast, (byte)slot, team);
        ServerMessages.PlayerChangeClass(_broadcast, (byte)slot, allowed);

        if (team != Team.Spectator)
            _world.Players[slot].RespawnTimer = 1;

        _log.Info($"bot {name} added as player {slot} ({team}, {allowed})");
        return slot;
    }

    /// <summary>
    /// Removes a bot, mirroring the disconnect path —
    /// <c>Scripts/Bots/botRemove.gml</c>.
    /// </summary>
    /// <remarks>
    /// The same <see cref="WorldState.RemoveSlot"/> a departure runs, and for
    /// the same reason: a slot index <em>is</em> a roster index, so everything
    /// above the hole shifts down on both sides of the wire at once. Everything
    /// the bot owned goes with it — its character, its sentry and every
    /// projectile of its still in the air (docs/SIMULATION.md, "what a
    /// departure destroys").
    /// </remarks>
    public void RemoveBot(int slot)
    {
        if (slot <= 0 || slot >= _roster.Count || !_roster[slot].IsBot)
            throw new ArgumentOutOfRangeException(nameof(slot), slot, "not a bot slot");

        var name = _roster[slot].Name;

        _roster.RemoveAt(slot);
        _world.RemoveSlot(slot);

        ServerMessages.PlayerLeave(_broadcast, (byte)slot);

        // GameServerBeginStep.gml:70's ServerBalanceTeams() after a departure,
        // which botRemove reaches through removePlayer for the same reason.
        TeamBalance.Check(ref _world, ref _events);
        BroadcastTeamBalance();

        _log.Info($"bot {name} removed from player {slot}");
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
                // The one command that is not a PlayerCommand: it *is* the
                // per-tick input, and it reaches the simulation through the
                // seam like everything else. A client always has a
                // NetworkInputSource behind it, so the cast never fails.
                ((NetworkInputSource)entry.Source).Latest = PlayerInput.Read(ref reader);
                break;

            case MessageType.PlayerChangeTeam:
                ExecuteChangeTeam(entry, (Team)reader.ReadUByte());
                break;

            case MessageType.PlayerChangeClass:
            {
                var requested = (PlayerClass)reader.ReadUByte();
                if (!Enum.IsDefined(requested))
                    break; // getCharacterObject() == -1: the reference ignores it.

                ExecuteChangeClass(entry, requested);
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

            case MessageType.BuildSentry:
                ExecuteBuildSentry(IdOf(entry));
                break;

            case MessageType.DestroySentry:
                ExecuteDestroySentry(IdOf(entry));
                break;

            case MessageType.DropIntel:
                ExecuteDropIntel(IdOf(entry));
                break;

            // Everything below is either simulation the port does not have yet
            // or a subsystem a headless server deliberately does not implement.
            // The reference's own answer to REWARD_REQUEST from a server with no
            // auth is silence, and silence is a valid answer to all of these.
            case MessageType.ToggleZoom:
                ExecuteToggleZoom(IdOf(entry));
                break;

            case MessageType.Omnomnomnom:
            case MessageType.RewardRequest:
            case MessageType.RewardChallengeResponse:
            case MessageType.PluginPacket:
                break;

            default:
                // Unreachable: CommandStream only yields commands in the table.
                break;
        }
    }

    // --- the commands that are not keys ---------------------------------------
    //
    // Every one of these is reached from two places: a socket packet, and a bot
    // that asked for it through ICommandSource. ⚠️ They must stay ONE copy.
    // Scripts/Bots/botServerActions.gml is explicit about why — "a bot performs
    // them by calling the same server-side scripts the network path calls …
    // each of which re-checks its own preconditions" — and the reference
    // extracted serverBuildSentry.gml and serverToggleZoom.gml out of
    // processClientCommands.gml for exactly that reason. A second copy here
    // would let a bot build a sentry a client could not.

    /// <summary>
    /// Runs one <see cref="PlayerCommand"/> for a slot, whoever asked for it.
    /// </summary>
    /// <remarks>
    /// A command whose preconditions fail is silence, never an error: that is
    /// the reference's guard style throughout <c>processClientCommands.gml</c>,
    /// and <c>botServerActions.gml</c> leans on it — "a wrong guess here is
    /// ignored rather than harmful".
    /// </remarks>
    private void Execute(RosterEntry entry, in PlayerCommand command)
    {
        switch (command.Kind)
        {
            case PlayerCommandKind.BuildSentry:
                ExecuteBuildSentry(IdOf(entry));
                break;

            case PlayerCommandKind.DestroySentry:
                ExecuteDestroySentry(IdOf(entry));
                break;

            case PlayerCommandKind.ToggleZoom:
                ExecuteToggleZoom(IdOf(entry));
                break;

            case PlayerCommandKind.DropIntel:
                ExecuteDropIntel(IdOf(entry));
                break;

            case PlayerCommandKind.ChangeClass:
                // The socket path's Enum.IsDefined guard, for the same reason:
                // getCharacterObject() == -1 is ignored by the reference.
                if (Enum.IsDefined(command.AsClass))
                    ExecuteChangeClass(entry, command.AsClass);
                break;

            case PlayerCommandKind.ChangeTeam:
                ExecuteChangeTeam(entry, command.AsTeam);
                break;

            case PlayerCommandKind.None:
            default:
                break;
        }
    }

    private void ExecuteChangeTeam(RosterEntry entry, Team requested)
    {
        var slot = IdOf(entry);

        // The balance block: "any" resolves to the smaller team, a
        // request for the bigger team — or the one they are already on
        // — is refused with silence.
        var resolved = PlayerCommands.ResolveRequestedTeam(ref _world, slot, requested);

        if (resolved is not Team team)
            return;

        // getCharacterObject(player.class) != -1 or TEAM_SPECTATOR:
        // every class the port accepts has a character object, so the
        // clause is always true here.

        // A change with a live character is a death, announced before
        // the change itself — the reference's own stream order. The
        // simulation half also arms the respawn and remembers the team;
        // the sentry notices on its own next Begin Step, exactly one
        // tick later like the reference's guard.
        var death = PlayerCommands.ChangeTeamWithBalance(
            ref _world, slot, team, ref _events, out var reclassed);

        if (death is PlayerDeathEvent died)
            ServerMessages.PlayerDeath(
                _broadcast, (byte)slot, died.Killer, died.Assistant, died.Source);

        // ⚠️ checkClasslimits runs *before* the team message and is
        // announced separately, and only when it actually moved the
        // class — processClientCommands.gml:181-186.
        if (reclassed is PlayerClass forced)
        {
            entry.Class = forced;
            ServerMessages.PlayerChangeClass(_broadcast, (byte)slot, forced);
        }

        entry.Team = team;
        ServerMessages.PlayerChangeTeam(_broadcast, (byte)slot, team);

        // And ServerBalanceTeams(), which ChangeTeamWithBalance has
        // already run into _events — the messages it produced go out
        // after this one, which is the reference's order.
        BroadcastTeamBalance();
    }

    private void ExecuteChangeClass(RosterEntry entry, PlayerClass requested)
    {
        var slot = IdOf(entry);

        // A live character dies for the swap — BID_FAREWELL by their
        // own hand, or FINISHED_OFF for whoever had hurt them — and the
        // death precedes the class message, the reference's order. A
        // characterless player is queued to spawn in this same tick's
        // alarm stage.
        if (PlayerCommands.ChangeClass(ref _world, slot, requested, _collision) is PlayerDeathEvent death)
            ServerMessages.PlayerDeath(
                _broadcast, (byte)slot, death.Killer, death.Assistant, death.Source);

        entry.Class = requested;
        ServerMessages.PlayerChangeClass(_broadcast, (byte)slot, requested);
    }

    private void ExecuteBuildSentry(int slot)
    {
        // serverBuildSentry.gml: check, broadcast, build. A failed
        // precondition is silence, exactly as the reference's guard
        if (!Sentries.CanBuild(_world, slot, _collision))
            return;

        ref readonly var builder = ref _world.Characters[slot];

        ServerMessages.BuildSentry(
            _broadcast,
            (byte)slot,
            WireQuantise.ToUShort(Gml.Round(builder.X * 5)),
            WireQuantise.ToUShort(Gml.Round(builder.Y * 5)),
            (sbyte)builder.Facing);

        // The build itself uses the *exact* position; only the
        // broadcast is quantised, like a character's own block. The
        // team comes off the world's own player state.
        Sentries.Build(ref _world, slot, builder.X, builder.Y, builder.Facing);
    }

    private void ExecuteDestroySentry(int slot)
    {
        // processClientCommands.gml: a bare instance_destroy, and no
        // broadcast at all — clients notice through the subobject bit
        // of the next update. The Destroy event still runs, so the
        // owner is shoved if airborne and stuck mines drop.
        if (slot < WorldState.MaxSlots && _world.HasSentry[slot])
            Sentries.Destroy(_collision, ref _world, slot);
    }

    private void ExecuteDropIntel(int slot)
    {
        // processClientCommands.gml:221 — the player asked to put it
        // down. Unlike the death drop this one *is* broadcast, and the
        // reference sends it before doing it (sendEventDropIntel then
        // doEventDropIntel); the order is unobservable because the
        // message carries nothing but the player.
        //
        // ⚠️ It is a command and not a keypress, so it never reaches
        // MatchSimulation.Tick — the same shape as BUILD_SENTRY, and
        // the reason Intel.Drop is public.
        if (Intel.Drop(ref _world, slot))
            ServerMessages.DropIntel(_broadcast, (byte)slot);
    }

    private void ExecuteToggleZoom(int slot)
    {
        // serverToggleZoom.gml: the preconditions, the broadcast, then
        // the apply. ⚠️ Zoom is its own client command rather than a key
        // in the input byte, which is the whole reason the reference
        // extracted it into a script a bot can call.
        if (PlayerCommands.ToggleZoom(ref _world, slot))
            ServerMessages.ToggleZoom(_broadcast, (byte)slot);
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

            // GameServerBeginStep.gml:70: ServerBalanceTeams() immediately after
            // the PLAYER_LEAVE, because a departure is the commonest way for the
            // teams to come apart — and, since the leaver is gone, the commonest
            // way for them to come back together.
            TeamBalance.Check(ref _world, ref _events);
            BroadcastTeamBalance();
        }

        _log.Info($"{client.Entry.Name} left ({reason}); {_roster.Count - 1} of {_options.PlayerLimit} slots used");
        client.Connection.Dispose();
    }

    // --- broadcast -----------------------------------------------------------

    private void WriteStateUpdate()
    {
        // Cadence is part of the contract: the client dead-reckons between the
        // position-bearing updates. Every tick but the seventh is an INPUTSTATE.
        // GameServerBeginStep.gml:88, and it comes *before* the tiered update in
        // the same event: an extra scoreboard message when something scored, and
        // a two-minute heartbeat so a client that missed one is corrected
        // eventually. (`global.setupTimer == 180`, its third trigger, waits on
        // the setup timer being simulated.)
        // Generator.events/Begin Step.xml:22: a live generator asks for a
        // scoreboard update every minute, on top of the two-minute heartbeat
        // below. Here rather than in Gg2.Simulation because it is keyed on
        // GameServer.frame, which is the server's own counter and not part of
        // the world — the same reason the map-change countdown lives here.
        //
        // The reference's guard is `else if`, under `if hp <= 0`: a generator
        // that is about to be destroyed does not ask.
        if (_frame % 1800 == 0
            && ((_world.RedGenerator.Exists && !Gml.LessOrEqual(_world.RedGenerator.Hp, 0))
                || (_world.BlueGenerator.Exists && !Gml.LessOrEqual(_world.BlueGenerator.Hp, 0))))
        {
            _capsSyncPending = true;
        }

        if (_capsSyncPending || _frame % 3600 == 0)
        {
            _stateWriter.Write(_broadcast, MessageType.CapsUpdate);
            _capsSyncPending = false;
        }

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
            // The map now loaded, not the one configured — after a rotation
            // they differ, and a HELLO naming the wrong one makes the client
            // download or refuse a map the server is not serving.
            CurrentMap = _currentMap,
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
        // ⚠️ _currentMap, not _options.Map. They are the same until the first
        // rotation and different for ever afterwards, and this is the message
        // that makes a joining client load the map — so a stale name here means
        // every player who joins after the first round plays a different map
        // from everybody else.
        ServerMessages.ChangeMap(writer, _currentMap, string.Empty);

        for (var i = 0; i < _roster.Count; i++)
        {
            var existing = _roster[i];
            ServerMessages.PlayerJoin(writer, existing.Name);
            ServerMessages.PlayerChangeClass(writer, (byte)i, existing.Class);
            ServerMessages.PlayerChangeTeam(writer, (byte)i, existing.Team);
        }

        _stateWriter.Write(writer, MessageType.FullUpdate);

        connection.Send(writer.WrittenSpan);

        // 2. Now it exists — in the roster and, since phase 6, in the world:
        //    a fresh spectator whose respawn alarm is at rest.
        var entry = new RosterEntry { Name = joining.Name, Connection = connection };
        _roster.Add(entry);
        _world.AddPlayer(_roster.Count - 1);
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
