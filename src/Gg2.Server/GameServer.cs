using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Simulation;
using Gg2.Bots;

namespace Gg2.Server;

/// <summary>What <see cref="GameServer.KickPlayer"/> did.</summary>
/// <remarks>
/// Three outcomes rather than a bool, because "there is no such slot" and "that
/// slot is a bot" need different sentences in front of an operator: the second
/// one is a person who should be changing the bot count instead.
/// </remarks>
public enum KickOutcome
{
    /// <summary>The connection was sent a <c>KICK</c> and closed.</summary>
    Kicked,

    /// <summary>No roster entry at that index, or it was the reserved slot 0.</summary>
    NoSuchSlot,

    /// <summary>A bot, or a slot whose connection has already gone.</summary>
    NotAConnection,
}

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

    /// <summary>Null unless the server was configured to record — docs/BOTAI.md item 3.</summary>
    private MatchRecorder? _recorder;

    /// <summary>Null unless a control port was configured — off by default.</summary>
    private ControlPort? _control;

    /// <summary>Null unless <c>--web</c> was given — off by default.</summary>
    private WebAdminServer? _web;

    /// <summary>The panel's scrollback. Null unless <c>--web</c> was given.</summary>
    private readonly LogRing? _webLog;

    /// <summary>
    /// A smoothed cost of <see cref="Tick"/> itself, in milliseconds.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Measured in <see cref="Run"/> only, so it is zero everywhere a
    /// benchmark runs.</b> <c>HeadlessMatch</c>, the env and <c>eval</c> drive
    /// <see cref="Tick"/> directly and never see this; it exists so an operator
    /// watching the panel can see the tick approaching its deadline, which is
    /// the one thing that is invisible from inside the game.
    /// ⛔ <b>Not a benchmark number.</b> It is an EMA over whatever the server
    /// happened to be doing, including the map load on a rotation; quoting it as
    /// a per-tick cost would be quoting a different measurement from
    /// <c>tools/bench/</c>'s.
    /// </remarks>
    private double _tickWorkMs;

    /// <summary>When <see cref="Start"/> ran, for the panel's uptime.</summary>
    private DateTime _startedUtc = DateTime.UtcNow;

    /// <summary>
    /// The tick's own cost since the last time a sampler took it: the sum, the
    /// worst, and how many ticks that was over.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>An accumulator rather than a history, and the reader RESETS it.</b>
    /// The alternative — keeping a ring here — would put a policy about how far
    /// back somebody wants to look into <see cref="GameServer"/>, and would cost
    /// the same on a server nobody is watching. <see cref="TakeTickWork"/> is
    /// called once a second by the web panel's sampler and by nothing else.
    /// ⚠️ Written in <see cref="Run"/> only, so it is zero everywhere a benchmark
    /// runs — <c>HeadlessMatch</c>, the env and <c>eval</c> drive Tick directly.
    /// </remarks>
    private double _tickWorkSumMs;

    /// <inheritdoc cref="_tickWorkSumMs"/>
    private double _tickWorkPeakMs;

    /// <inheritdoc cref="_tickWorkSumMs"/>
    private int _tickWorkCount;

    /// <summary>Ticks that overran their pacing interval since startup.</summary>
    /// <remarks>
    /// ⚠️ <b>Pacing, never simulation</b>, the same as <see cref="LateTickMonitor"/>
    /// — a tick computes the same thing whenever it runs. A rising count is a
    /// delivery problem, and its symptom is a client visibly correcting.
    /// ⚠️ <see cref="Run"/> only, for the reason above.
    /// </remarks>
    private long _lateTicks;

    /// <inheritdoc cref="_lateTicks"/>
    private double _worstOverrunMs;

    /// <summary>Bytes sent by connections that have since gone.</summary>
    /// <remarks>
    /// ⚠️ Kept so the total only ever rises. Summing the LIVE connections alone
    /// would make the total drop whenever somebody disconnected, and a rate
    /// derived from it would go negative at exactly the moment an operator was
    /// looking to see why.
    /// </remarks>
    private long _bytesRetired;

    /// <summary>
    /// While true, <see cref="Tick"/> services the network and broadcasts but
    /// does not advance the simulation.
    /// </summary>
    private bool _frozen;

    /// <summary>Simulation steps still owed to a <c>step</c> while frozen.</summary>
    private int _stepsOwed;
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
    /// Flushes performed. <b>Exactly one per tick that sent anything</b>, which
    /// is the reference's shape: <c>GameServerEndStep.gml:7-10</c> writes the
    /// buffer to every socket and clears it once, in one event.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Exposed because a test cannot see this from outside and one needs
    /// to.</b> <c>FlushFingerprintTests</c> groups messages by TICK — it ticks
    /// once and drains — and that is only the same thing as grouping by flush
    /// while this invariant holds. A second flush inside one tick would put two
    /// groups' worth of messages into one drain and the fingerprint would read
    /// as if nothing had changed; found by mutation, and this counter is what
    /// closes it.
    /// </remarks>
    public long FlushCount => _flushes;

    private long _flushes;

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
    /// The setup-timer value that owes every client a <c>CAPS_UPDATE</c>:
    /// <c>global.setupTimer == 180</c>, <c>GameServerBeginStep.gml:55</c>.
    /// </summary>
    /// <remarks>
    /// Six seconds before the gates open, and the same tick the reference plays
    /// the first of its countdown sounds (<c>ControlPointHUD.events/Step.xml</c>
    /// :23-24 lists 180, 150, 120 and 90 together). Only the 180 reaches the
    /// wire; the rest are audio.
    /// </remarks>
    private const int SetupSyncTick = 180;

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

    /// <summary>
    /// The current map's MD5, or empty when the client has it built in.
    /// </summary>
    /// <remarks>
    /// ⚠️ Empty is not "unknown" -- it is the signal that the map cannot be
    /// downloaded, and the reference guards on exactly that
    /// (<c>advertisedMapMd5 != ""</c>).
    /// </remarks>
    private string _currentMapMd5 = string.Empty;

    /// <summary>The current map's bytes, kept only when it is downloadable.</summary>
    /// <remarks>
    /// ⚠️ Held in memory for the life of the map rather than re-read per client:
    /// the MD5 advertised must be of the bytes actually served, and re-reading
    /// opens a window where a file edited on disk mid-match is served under the
    /// old hash. Null for a built-in map, which is most of them.
    /// </remarks>
    private byte[]? _currentMapFile;

    private string _nextMap = string.Empty;

    private int _nextArea = 1;

    /// <summary><c>GameServer.currentMapIndex</c> into the rotation.</summary>
    private int _rotationIndex;

    /// <summary>
    /// <see cref="ServerOptions.MapRotation"/> after <see cref="ShuffleMode"/> has
    /// been applied — the list <see cref="NextMapInRotation"/> actually walks.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Shuffled ONCE, in <see cref="Start"/>, not per map change.</b>
    /// <c>GameServerCreate.gml:92-108</c> shuffles at server creation and
    /// <c>nextMapInRotation</c> then walks a fixed order for the life of the
    /// server, so a shuffle per round would change what "rotation" means: every
    /// map would become reachable twice in a row and none would be guaranteed to
    /// come up at all.
    /// </remarks>
    private IReadOnlyList<string> _rotation = [];

    /// <summary>
    /// The public-lobby announcer, or null when <c>--lobby</c> was not passed.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Null is the default and means "announce nothing".</b> Every call
    /// site below is null-conditional rather than wrapped in an option check, so
    /// a forgotten check cannot accidentally publish this server's address.
    /// </remarks>
    private LobbyAnnouncer? _lobby;

    /// <summary>
    /// The reward auth relay, or null when <c>--rewards</c> was not passed.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Null is the default and means "answer nothing".</b> Every call site
    /// is null-guarded rather than wrapped in an option check, so a forgotten
    /// check cannot accidentally send a player's user id to a third party.
    /// </remarks>
    private RewardAuthClient? _rewards;

    /// <summary>
    /// Whether today is April 1st, refreshed on the reference's cadence.
    /// </summary>
    /// <remarks>
    /// <c>global.aFirst</c>. <c>GameServerBeginStep.gml:5-8</c> recomputes it in
    /// the same 30-second block as the lobby re-announcement, which is what lets
    /// a server that has been up for a week notice the date changing under it.
    /// Read by <see cref="ChatBubbles.Rewrite"/> and nothing else.
    /// </remarks>
    private bool _aprilFools;

    /// <summary>
    /// <c>current_time</c>: milliseconds since this process started, held still
    /// for the tick.
    /// </summary>
    /// <remarks>
    /// Sampled once per <see cref="Tick"/> and shared, so two things that happen
    /// in one tick cannot disagree about when it was — which is also GM8's
    /// behaviour, where <c>current_time</c> is read many times inside one step.
    /// <para>
    /// ⚠️ <b>Monotonic, deliberately.</b> Everything that reads this is measuring
    /// an elapsed duration, and a wall clock is the wrong instrument for that: an
    /// NTP correction or a DST step going backwards would stall the name throttle
    /// for as long as the jump. <see cref="LocalClock"/> is the wall clock, is
    /// separate, and is only ever asked what day it is.
    /// </para>
    /// </remarks>
    private long _nowMs;

    /// <summary>
    /// The minimum gap between one player's accepted <c>PLAYER_CHANGENAME</c>
    /// commands.
    /// </summary>
    /// <remarks>
    /// <c>processClientCommands.gml:293-296</c>, <c>current_time -
    /// lastNamechange &lt; 1000</c>.
    /// </remarks>
    private const long NameChangeIntervalMs = 1000;

    public GameServer(ServerOptions options, IServerLog log)
    {
        options.Validate();
        _options = options;

        // ⚠️ The web panel shows the server's log, so the log has to be captured
        // as well as printed — and the wrap happens HERE rather than in Start()
        // so that the lines Start() itself writes (the listening line, the map,
        // the generated password) are in the buffer a browser sees. A panel whose
        // log begins after startup is missing the half an operator most wants.
        _webLog = options.Web.Enabled ? new LogRing() : null;
        _log = _webLog is null ? log : new TeeLog(log, _webLog);

        // Once up front as well as on the cadence, so the first thirty seconds
        // of a server started on April 1st are not the odd ones out.
        _aprilFools = ChatBubbles.IsAprilFools(LocalClock());
    }

    /// <summary>
    /// The host's <b>local</b> calendar clock. Test hook; not part of the
    /// protocol.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Separate from the UTC instant <see cref="Tick"/> takes, and
    /// deliberately.</b> That one measures durations — join timeouts, the
    /// name-change throttle — where UTC is right and a DST step would be a bug.
    /// This one answers "what is today's date here", which is a calendar
    /// question the reference asks of the host's local clock
    /// (<c>date_current_date()</c>). One <c>Func</c> serving both would have to
    /// pick a wrong answer for one of them.
    /// </remarks>
    public Func<DateTime> LocalClock { get; set; } = static () => DateTime.Now;

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

    /// <summary>The roster entry at <paramref name="slot"/>. Test hook.</summary>
    public RosterEntry EntryOf(int slot) => _roster[slot];

    /// <summary>
    /// Whether the simulation believes this slot has queued jumping on. Test
    /// hook.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Deliberately reads the WORLD rather than the roster.</b> The two are
    /// written together by <c>ExecuteClientSettings</c> and a test that read the
    /// roster copy would pass while the physics saw nothing — which is the exact
    /// shape of the gap this accessor exists to gate.
    /// </remarks>
    public bool SimulatedQueueJump(int slot) => _world.Players[slot].QueueJump;


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

        // GameServerCreate.gml:92-108, before the first serverGotoMap: the
        // rotation is ordered once and then walked in that order for ever.
        // ⚠️ Random.Shared, never _world.Random and never _spawnRandom — the
        // first is observable simulation, and the second is seeded from the port
        // so that a bot population rolls the same way on every run, which is the
        // opposite of what a shuffle wants.
        _rotation = MapRotationShuffle.Apply(
            _options.MapRotation, _options.ShuffleRotation, Random.Shared);

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

        var map = OpenMap(OpeningMap(), area: 1);

        // ⚠️ `() => _match`, not `_match`. OpenMap replaces the MatchState on
        // every rotation, and a writer that captured the object once wrote the
        // PREVIOUS map's gamemode for ever — see StateUpdateWriter.Match.
        _stateWriter = new StateUpdateWriter(
            () => _match, TryGetCharacter, TryGetSentry, Objective, Statistics, Dominating,
            MovingPlatformBlocks);

        _startedUtc = DateTime.UtcNow;

        _listener = new TcpListener(IPAddress.Any, _options.Port);
        _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
        _listener.Start();
        BoundPort = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _log.Info(
            $"listening on {BoundPort}, map {map.Name} ({_match.GameMode}, " +
            $"{map.Walkmask.Width}x{map.Walkmask.Height} cells), {_options.PlayerLimit} slots");

        // ⚠️ Off unless asked for: this publishes the server's address to a
        // third party. The first registration goes out here rather than waiting
        // for the 30-second cadence, so a short-lived server still gets listed.
        if (_options.AnnounceToLobby)
        {
            _lobby = new LobbyAnnouncer(_log);
            _log.Info($"announcing to lobby {GameConstants.LobbyServerHost}:{GameConstants.LobbyServerPort}");
            AnnounceToLobby();
        }

        // ⚠️ Off unless asked for, and for a sharper reason than the lobby's:
        // this makes the server relay its players' user ids to a third party.
        // The reference answers every REWARD_REQUEST unconditionally, so this
        // gate is a deliberate departure and not a transcription — see
        // ExecuteRewardRequest.
        if (_options.Rewards)
        {
            _rewards = new RewardAuthClient(_log, _options.AuthServerHost, _options.AuthServerPort);
            _log.Info($"reward auth via {_options.AuthServerHost}:{_options.AuthServerPort}");
        }

        // ⚠️ Loopback only and off unless configured — it executes commands
        // against a live game, which is the same reason the GM8 bridge is.
        if (_options.ControlPort >= 0 && _options.ControlPortEnabled)
        {
            _control = new ControlPort(this, _log);
            _control.Start(_options.ControlPort);
        }

        // ⚠️ The one surface that CAN be reachable from the network, and the
        // only reason it is allowed to be: managing a server you are not sitting
        // at is what it is for. Its own thread, a password that cannot be empty,
        // and every command marshalled back onto this one — WebAdminServer.
        if (_options.Web.Enabled)
        {
            _web = new WebAdminServer(this, _log, _webLog!, _options.Web);
            _web.Start();
        }

        // ⚠️ Last, and after OpenMap: the first segment's preamble is a
        // FULL_UPDATE of the world, so there has to be a world.
        if (_options.RecordingDirectory.Length != 0)
        {
            _recorder = new MatchRecorder(
                _options.RecordingDirectory, _options.ServerName, _log, JoinBurstBytes);

            _recorder.StartSegment(_currentMap);
            _log.Info($"recording to {_options.RecordingDirectory}");
        }
    }

    /// <summary>The join burst as a fresh array, for <see cref="MatchRecorder"/>.</summary>
    private byte[] JoinBurstBytes()
    {
        var writer = new NetWriter(2048);
        WriteJoinBurst(writer);
        return writer.WrittenSpan.ToArray();
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
        MovingPlatforms.Place(ref _world, map);

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

        _world.Autobalance = _options.Autobalance;

        // game_init.gml:105 and the [Classlimits] section, ten independent
        // values. ⚠️ A zeroed ClassLimits would mean every class is full, so
        // this is not optional even on a server that enforces nothing — see
        // WorldState.ClassLimits.
        //
        // ⚠️ EffectiveClassLimits, not ClassLimits: MatchState broadcasts the
        // same array and the two must not disagree. See the remarks there.
        var classLimits = _options.EffectiveClassLimits();

        for (var i = 0; i < TeamBalance.ClassCount; i++)
            _world.ClassLimits[i] = classLimits[i];

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
        // ⚠️ The md5 the map was loaded WITH. ServerChangeMap.gml carries the
        // same pair the greeting does, so a client already connected across a
        // rotation onto a custom map learns it has to download.
        ServerMessages.ChangeMap(_broadcast, map.Name, _currentMapMd5);
        _log.Info($"now playing {map.Name} ({_match.GameMode}), area {_nextArea}");

        // GameServerBeginStep.gml:142-143 — "message lobby to update map name".
        // The listing shows the map, so without this it advertises the previous
        // one for up to 30 seconds after a rotation.
        AnnounceToLobby();

        // ⚠️ A segment is one map, so the header's map name cannot go stale —
        // and the new segment's preamble is taken against the world OpenMap has
        // just built, before anything broadcasts on it.
        _recorder?.StartSegment(map.Name);
    }

    /// <summary>
    /// The map the server opens on: <see cref="ServerOptions.Map"/> when one was
    /// named, otherwise the shuffled rotation's first entry.
    /// </summary>
    /// <remarks>
    /// <c>GameServerCreate.gml:110-113</c> — <c>if(global.launchMap == "")
    /// serverGotoMap(nextMapInRotation()) else serverGotoMap(global.launchMap)</c>.
    /// ⚠️ <b>The rotation index stays at 0 either way</b>, so the map after the
    /// opening one is <c>rotation[1]</c>: opening on <c>rotation[0]</c> must not
    /// also consume it, or the first round would be the only one that map ever
    /// gets and a rotation of two would degenerate to a rotation of one.
    /// <para>
    /// ⚠️ <c>ctf_truefort</c> is the last resort rather than an error, because
    /// an empty <see cref="ServerOptions.Map"/> with an empty rotation is a
    /// caller that expressed no preference at all — and it is the reference's
    /// own first map.
    /// </para>
    /// </remarks>
    private string OpeningMap()
    {
        if (_options.Map.Length != 0)
            return _options.Map;

        return _rotation.Count != 0 ? _rotation[0] : ServerOptions.DefaultMap;
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
        var rotation = _rotation;
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
    /// Nobody is connected, nobody is joining, and no bots are running — so
    /// nothing that happens this tick can be observed by anyone.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Joining connections count.</b> A client part-way through the
    /// handshake has no roster entry yet, and a server that ignored them would
    /// service the rest of that handshake at the idle rate — several round trips
    /// at half a second each.
    /// <para>
    /// ⚠️ <b>Bots count too.</b> A server running bots against each other with
    /// nobody watching is still simulating, and <c>--record</c> or the control
    /// port may well be the reason. Idling it would silently change what those
    /// recorded.
    /// </para>
    /// </remarks>
    public bool IsIdle
    {
        get
        {
            if (_clients.Count != 0 || _joining.Count != 0)
                return false;

            for (var i = 0; i < _roster.Count; i++)
            {
                if (_roster[i].IsBot)
                    return false;
            }

            return true;
        }
    }

    /// <summary>
    /// The interval <see cref="Run"/> should pace to right now — the idle rate
    /// when there is nobody to serve, otherwise <see cref="TickInterval"/>.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>An unpaced server is never idled.</b> <c>--speed max</c> is a direct
    /// instruction about the tick rate; quietly overriding it with a 2 Hz idle
    /// would make the flag look broken on exactly the configuration that asked
    /// for it loudest.
    /// </remarks>
    /// <summary>
    /// <see cref="PacingInterval"/>, for a test. Not part of the server's own
    /// surface — idling is otherwise only observable by timing <see cref="Run"/>,
    /// and a test that did that would be measuring the machine.
    /// </summary>
    internal TimeSpan PacingIntervalForTest() => PacingInterval();

    private TimeSpan PacingInterval()
    {
        var live = TickInterval;

        if (!_options.IdleWhenEmpty || live <= TimeSpan.Zero || !IsIdle)
            return live;

        var idle = TimeSpan.FromSeconds(1.0 / ServerOptions.IdleTickRate);

        // ⚠️ Never FASTER than the live rate. A --speed below 1 already ticks
        // more slowly than the idle rate, and "idling" it up to 2 Hz would be a
        // speed-up nobody asked for.
        return idle > live ? idle : live;
    }

    /// <summary>
    /// The wall-clock length of one tick at the configured speed, or
    /// <see cref="TimeSpan.Zero"/> when the server is unpaced.
    /// </summary>
    /// <remarks>
    /// Exposed so a caller can tell a paced server from an unpaced one without
    /// re-deriving the arithmetic — and so the zero case has one definition
    /// rather than two.
    /// </remarks>
    public TimeSpan TickInterval =>
        double.IsPositiveInfinity(_options.SpeedMultiplier)
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(1.0 / (GameConstants.TickRate * _options.SpeedMultiplier));

    /// <summary>
    /// Runs until <paramref name="cancellation"/> is signalled, pacing to 30 Hz
    /// times <see cref="ServerOptions.SpeedMultiplier"/> — or as fast as the
    /// machine allows when that is infinite.
    /// </summary>
    public void Run(CancellationToken cancellation)
    {
        // ⚠️ Windows rounds a wait up to its timer quantum — about 15.6ms by
        // default — and the pacing below waits. Measured on an idle server
        // without this: 15% of ticks arrived about 14ms late, nearly half a
        // tick, in a distribution with nothing between 34ms and 45ms. With it,
        // none. The stock client re-simulates between position updates and
        // corrects when one lands, so irregular ticks reach a player as
        // rubberbanding. See TimerResolution for the numbers.
        using var resolution = TimerResolution.Raise();

        var clock = Stopwatch.StartNew();
        var nextTick = TimeSpan.Zero;
        var pacing = TickInterval;

        // ⚠️ Measured with clients on it, not idle. The 15% figure above came
        // from an empty server, which is the clean measurement but not the one
        // a player experienced; --tick-stats is here so the same distribution
        // can be read back out of a real game.
        var stats = _options.TickStats ? new TickPacing(TickInterval.TotalMilliseconds) : null;
        var lastStart = clock.Elapsed;

        // ⚠️ ALWAYS ON, unlike --tick-stats. A healthy server prints nothing, so
        // there is no cost to leaving it armed, and a tick that overruns its
        // budget is the one thing an operator cannot see from inside the game:
        // the symptom is a client rubberbanding, which looks like the player's
        // own network. LateTickMonitor rate-limits itself; see its remarks.
        var late = new LateTickMonitor();

        // ⛔ HERE and nowhere else. The budget may only move BETWEEN ticks, or
        // Choose stops being a pure function of (tick, poll order, costs, budget)
        // and a match stops replaying. Run is also the one entry point that
        // HeadlessMatch, the env and eval never call, which is what keeps a
        // benchmark from picking this up by accident.
        var arbiter = _options.AdaptiveBotBudget
            ? _options.Bots.Brain?.Arbiter as DecisionQuotaArbiter
            : null;

        var budget = arbiter is null
            ? null
            : new MicroBudgetController(
                ceiling: arbiter.UnitsPerTick,

                // ⚠️ The floor is one policy decision, not zero: a budget of zero
                // would also starve the cheap tier, and the whole point of tiering
                // is that the cheap one survives the expensive one being cut.
                floor: MicroBudgetController.PolicyFloorUnits,
                step: MicroBudgetController.DefaultStepUnits,
                windowTicks: LateTickMonitor.WindowTicks);

        while (!cancellation.IsCancellationRequested)
        {
            if (stats is not null)
            {
                var start = clock.Elapsed;

                // The first interval is measured from the loop's start rather
                // than skipped, so the count matches the tick count.
                stats.Record((start - lastStart).TotalMilliseconds);
                lastStart = start;

                // Every 30 seconds of play, so a hitch can be placed in time
                // against what the players were doing rather than only summed
                // at the end — and so a crash still leaves the evidence.
                if (stats.Count % (GameConstants.TickRate * 30) == 0)
                    _log.Info(stats.Report());
            }

            var tickStarted = clock.Elapsed;

            Tick();

            // ⚠️ Two Stopwatch reads a tick, unconditionally, for the panel's
            // "tick ms" tile. Against a 33,300us budget the pair costs tens of
            // nanoseconds, and the alternative — arming it only when --web is on
            // — makes the paced loop two different loops.
            // ⛔ NOT a benchmark number: it is an EMA over whatever the server was
            // doing, map loads included. tools/bench/ is where a per-tick cost
            // comes from.
            var tickWorkMs = (clock.Elapsed - tickStarted).TotalMilliseconds;

            _tickWorkMs = (_tickWorkMs * 15 + tickWorkMs) / 16;
            _tickWorkSumMs += tickWorkMs;
            _tickWorkPeakMs = Math.Max(_tickWorkPeakMs, tickWorkMs);
            _tickWorkCount++;

            // ⚠️ Re-read every tick rather than captured before the loop: the
            // control port can change the speed on a running server, and a
            // captured interval would make --speed a startup-only setting with
            // no sign that a later change did nothing.
            //
            // ⚠️ PacingInterval, not TickInterval — it drops to the idle rate
            // when nobody is connected. The rebase below already handles an
            // interval that changes underneath the loop, which is why idling
            // needed no other change to the pacing: entering and leaving idle is
            // the same event as a --speed change.
            var tickLength = PacingInterval();

            // ⚠️ Unpaced returns to the top BEFORE the pacing arithmetic, and
            // that is not tidiness. The backlog branch below tests
            // `wait < -tickLength * 10`, which at a zero tickLength reads
            // `wait < 0` — true on every tick after the first — so falling
            // through would log "dropped 0ms of tick backlog" forever, at
            // unpaced speed. There is no debt to shed when there is no clock to
            // be behind. Rebasing on the way past keeps a later switch back to a
            // paced speed from thinking it is hours late.
            if (tickLength <= TimeSpan.Zero)
            {
                nextTick = clock.Elapsed;
                continue;
            }

            // A speed change rebases too: debt accumulated in the old interval
            // means nothing in the new one, and carrying it would make a server
            // slowed from max sprint until it had "caught up".
            if (tickLength != pacing)
            {
                pacing = tickLength;
                nextTick = clock.Elapsed;
            }

            nextTick += tickLength;
            var wait = nextTick - clock.Elapsed;

            // ⚠️ Below the unpaced `continue` above, so `tickLength` is real
            // here. A negative wait is the tick's work having overrun its own
            // budget, which is the only definition of "late" that does not need
            // a second clock.
            // ⚠️ The count is incremented OUTSIDE the `&&` the warning used to
            // hang off. LateTickMonitor rate-limits itself — it returns null for
            // most late ticks so the log is not a flood — so counting only when
            // it produced a line would have under-reported by exactly the amount
            // the rate limiter suppressed, which on a struggling server is nearly
            // all of them.
            var overrunMs = -wait.TotalMilliseconds;

            if (LateTickMonitor.IsLate(overrunMs, tickLength.TotalMilliseconds))
            {
                _lateTicks++;
                _worstOverrunMs = Math.Max(_worstOverrunMs, overrunMs);

                if (late.Record(_frame, overrunMs, tickLength.TotalMilliseconds, LateContext())
                    is { } warning)
                {
                    _log.Info(warning);
                }
            }

            if (late.Poll(_frame) is { } summary)
                _log.Info(summary);

            // ⚠️ AFTER the late check and BEFORE the wait, so it sees this tick's
            // pressure and the next tick runs on the adjusted budget.
            if (budget is not null && arbiter is not null)
            {
                var used = tickLength - wait;

                if (budget.Observe(
                        _frame,
                        used.TotalMilliseconds,
                        tickLength.TotalMilliseconds,
                        arbiter.SpentThisTick) is { } moved)
                {
                    _log.Info(moved);
                }

                arbiter.UnitsPerTick = budget.Units;
            }

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

        if (late.Flush() is { } pending)
            _log.Info(pending);

        if (stats is not null)
            _log.Info(stats.Report());
    }

    /// <summary>
    /// What the server was doing on a tick that ran late.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Built only when a tick is actually late</b> — it walks the roster,
    /// and doing that every tick to feed a line that is almost never printed
    /// would be paying the diagnostic's cost on the healthy path.
    /// </remarks>
    private LateTickContext LateContext()
    {
        var bots = 0;
        var characters = 0;

        for (var i = 0; i < _roster.Count; i++)
        {
            if (_roster[i].IsBot)
                bots++;

            if (_world.IsAlive(i))
                characters++;
        }

        return new LateTickContext(
            Clients: _clients.Count,
            Bots: bots,
            Characters: characters,
            Map: _currentMap,

            // Only DecisionQuotaArbiter can say what it spent, and only a tiered
            // server has one. Null means "nothing to report", never "zero".
            ArbiterSpentMicroseconds:
                (_options.Bots.Brain?.Arbiter as DecisionQuotaArbiter)?.SpentThisTick);
    }

    /// <summary>
    /// One server tick. Exposed so tests can drive the server without a clock —
    /// the same reason the simulation is a pure function.
    /// </summary>
    public void Tick()
    {
        var now = DateTime.UtcNow;
        _nowMs = Environment.TickCount64;

        _frame++;

        // GameServerBeginStep.gml:5-8, `frame mod 900`: the 30-second block at
        // the very top of the server's own Begin Step. Two things live in it,
        // and the second is not optional the way the first is.
        if (_frame % LobbyRegistration.ReRegisterIntervalTicks == 0)
        {
            // ⚠️ Cheap to leave unguarded because _lobby is null unless --lobby
            // was passed, and the modulo runs either way.
            if (_lobby is not null)
                AnnounceToLobby();

            _aprilFools = ChatBubbles.IsAprilFools(LocalClock());
        }

        ServiceClients();

        // Immediately after the client commands, because that is where the
        // reference's equivalent sits: RewardAuthChecker is an object created by
        // processClientCommands and its Step runs in the same frame's object
        // pass. It only ever appends to the broadcast, so it must be above the
        // flush at the end of this method.
        ServiceRewardAuth();

        // ⚠️ Before ServiceBots, so a bot added this tick is polled this tick
        // rather than pressing nothing for one frame. GameServerBeginStep.gml
        // calls botPopulationUpdate ahead of the input pass for the same reason.
        _population?.Service();

        ServiceBots();

        // ⛔ THE INPUT HAS TO REACH THE WORLD BEFORE THE UPDATE IS SERIALISED,
        // and for six phases it did not. The broadcast reads
        // _world.Characters[slot].KeyState; KeyState is written only by
        // CharacterState.ApplyInput, which runs inside MatchSimulation.Tick —
        // i.e. in AdvanceSimulation, BELOW this. So every INPUTSTATE and
        // QUICK_UPDATE carried the PREVIOUS tick's keys.
        //
        // The reference writes keyState straight onto the character in
        // processClientCommands (GameServerBeginStep.gml:52) and serialises after
        // it, at line 66. This is that ordering.
        //
        // ⚠️ Clients dead-reckon each character from the last keys they heard
        // until the next position update, every 7th tick, so one-tick-stale keys
        // mispredict every character for most of every gap — forever, invisibly,
        // and looking exactly like lag while the server sits inside its budget.
        // A jump is the worst case: GG2 jumps on the PRESSED edge, so a late key
        // can miss the edge against the client's own previous keystate entirely
        // and the character then snaps upward on the next position update.
        // InputReachesTheWireTests is the gate, with a bot variant that has no
        // socket so the network cannot explain a failure.
        //
        // ⚠️ MatchSimulation.Tick applies the SAME inputs again below, and that
        // is deliberate rather than tolerated. The simulation must stay a pure
        // function of (state, inputs) — CLAUDE.md's first non-negotiable — so it
        // cannot depend on the server having pre-applied anything. The second
        // apply contributes nothing: ApplyInput ORs the edge into PressedKeys and
        // then sets LastKeyState = KeyState, so the second call ORs zero, and
        // PressedKeys is not cleared until CharacterPhysics has consumed it
        // (CharacterPhysics.cs:306, at the end of the Begin Step). The edge
        // survives, once.
        var advancing = AdvancesThisTick();
        var live = Math.Min(_roster.Count, WorldState.MaxSlots);

        if (advancing)
        {
            GatherInputs(live);

            for (var slot = 0; slot < live; slot++)
            {
                if (_world.Alive[slot])
                    _world.Characters[slot].ApplyInput(_inputs[slot]);
            }
        }

        // Order matters and is the reference's. GameServerBeginStep.gml
        // serialises the update *before* any Character's Step runs, so one
        // update carries this tick's input alongside last tick's position.
        WriteStateUpdate();

        // ⚠️ Freezing skips EXACTLY this call, and everything around it keeps
        // running. A freeze that skipped the whole tick would stop servicing
        // sockets, so a frozen server would silently drop every client it was
        // being frozen to inspect — and it would stop answering the control port
        // that froze it. Clients keep receiving updates of an unchanging world,
        // which is what "frozen" looks like from a client that re-simulates
        // between updates.
        if (advancing)
            AdvanceSimulation(live);

        // ⛔ THE FLUSH BELONGS AFTER THE SIMULATION, not under the update.
        // GameServerEndStep.gml:7-8 writes global.sendBuffer to every socket and
        // clears it in the server's END Step, after everything that frame has
        // been appended — so the reference's packet for tick N holds the state
        // update AND that tick's events together.
        //
        // Flushing directly under WriteStateUpdate split them across two sends:
        // the update for tick N went out mid-tick, and everything
        // AdvanceSimulation appends — WEAPON_FIRE, PLAYER_DEATH, ubers, intel
        // drops, generator destruction, arena — waited for the NEXT tick's
        // flush. Every event message was delivered a tick late, and ahead of the
        // update it should have followed.
        //
        // ⚠️ This changes only WHEN the bytes leave, never their order or their
        // content: they were already appended in the reference's order into the
        // same buffer. Which is exactly why a closed-loop replay cannot see it —
        // a trace records the stream, and the stream is identical. Found by
        // reading GameServerEndStep after a 3,599-tick replay came back with
        // ZERO physics divergence and the symptom was still there.
        //
        // ⚠️ A client simulates every projectile itself from WEAPON_FIRE and the
        // shooter's keys; there is no projectile message. A late WEAPON_FIRE
        // starts the client's rocket a tick behind the server's.
        //
        // ⛔ BEFORE AcceptConnections, deliberately. Flushing after it delivers
        // this tick's broadcast to a client that joined DURING this tick, ahead
        // of its own join burst — JoinAndBroadcastTests and FullUpdateLayoutTests
        // both catch that, 23 of them. The join sequence is a separate contract
        // and this change is not licensed to move it.
        FlushBroadcast();

        // ⛔ AFTER THE FLUSH, and measured — see BroadcastEndStepEvents. The
        // reference's flush is in the GameServer's End Step, which is the FIRST
        // End Step of the frame, so a death, a sentry's destruction and the
        // BALANCE a death triggers all miss it and ship on the next tick.
        // Appending them above the flush delivered every one a tick EARLY —
        // the mirror of the bug cdaa62a fixed, and left behind by it.
        if (advancing)
            BroadcastEndStepEvents(live);

        _control?.Service();

        // ⚠️ Not "service": the web admin's sockets are handled on its own
        // threads and this only drains what they queued, plus publishing the
        // snapshot they read. See WebAdminServer.Pump — a remote client that
        // stops reading must never be able to stall a tick, which is exactly
        // what servicing it here would allow.
        _web?.Pump(_frame);

        AcceptConnections(now);
        ServiceJoiningConnections(now);

        foreach (var client in _clients)
            client.Connection.Flush();
    }

    /// <summary>
    /// Whether this tick advances the simulation: always, unless frozen — and
    /// then only for as many steps as a <c>step</c> asked for.
    /// </summary>
    private bool AdvancesThisTick()
    {
        if (!_frozen)
            return true;

        if (_stepsOwed <= 0)
            return false;

        _stepsOwed--;
        return true;
    }

    // --- the control surface -------------------------------------------------
    //
    // docs/ARCHITECTURE.md's "A control surface, eventually", and phase 8's last
    // item. Everything here is an OPERATOR action — freezing the world, moving a
    // character, changing the map — and none of it is something a client can ask
    // for. ⚠️ Anything a client CAN ask for does not belong here: it goes
    // through Execute(RosterEntry, PlayerCommand), the one dispatch the socket
    // path and the bots already share, or the control port becomes a third way
    // to do what CLAUDE.md says must have exactly one.

    /// <summary>
    /// A live character's <em>exact</em> position and health.
    /// </summary>
    /// <remarks>
    /// ⚠️ The unquantised doubles, deliberately — <c>TryGetCharacter</c> hands
    /// back the wire blocks, which are rounded to what a client can observe. A
    /// control port exists to answer "where is it really", and a debugging tool
    /// that silently reported the wire's rounding would make every sub-pixel
    /// question unanswerable.
    /// </remarks>
    public bool TryGetExactPosition(int slot, out double x, out double y, out double hp)
    {
        x = 0;
        y = 0;
        hp = 0;

        if (slot < 0 || slot >= WorldState.MaxSlots || !_world.IsAlive(slot))
            return false;

        ref readonly var character = ref _world.Characters[slot];

        x = character.X;
        y = character.Y;
        hp = character.Hp;

        return true;
    }

    /// <summary>Ticks left on the round clock — the value the HUD blocks carry.</summary>
    public int RoundTimer => _world.RoundTimer;

    /// <summary>Whether the round has been decided and by whom.</summary>
    public bool RoundDecided => _world.RoundDecided;

    /// <inheritdoc cref="RoundDecided"/>
    public Team RoundWinner => _world.Winner;

    // --- what an operator surface reads -------------------------------------
    //
    // ControlCommands.State builds the control port's and the web panel's one
    // shared view out of these. They are reads, they run on the tick thread with
    // everything else, and none of them is on any hot path.

    /// <summary>Seconds since <see cref="Start"/>.</summary>
    public double UptimeSeconds => (DateTime.UtcNow - _startedUtc).TotalSeconds;

    /// <inheritdoc cref="_tickWorkMs"/>
    public double TickWorkMilliseconds => _tickWorkMs;

    /// <summary>Ticks that overran their pacing interval since startup.</summary>
    /// <inheritdoc cref="_lateTicks" path="/remarks"/>
    public long LateTicks => _lateTicks;

    /// <summary>The worst overrun seen, in milliseconds past the interval.</summary>
    public double WorstOverrunMilliseconds => _worstOverrunMs;

    /// <summary>Bytes this server has put on the wire, across every connection.</summary>
    public long BytesSent
    {
        get
        {
            var total = _bytesRetired;

            foreach (var client in _clients)
                total += client.Connection.BytesSent;

            return total;
        }
    }

    /// <summary>
    /// The mean and worst tick cost since the last call, and resets.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Reading it clears it</b>, so there is exactly one caller — the web
    /// panel's sampler, once a second, on this thread. A second caller would
    /// silently halve both of their windows.
    /// ⚠️ <paramref name="ticks"/> is zero on a server whose <see cref="Run"/>
    /// loop is not the thing driving it; the caller must not divide by it.
    /// </remarks>
    public void TakeTickWork(out double meanMs, out double peakMs, out int ticks)
    {
        ticks = _tickWorkCount;
        meanMs = ticks == 0 ? 0 : _tickWorkSumMs / ticks;
        peakMs = _tickWorkPeakMs;

        _tickWorkSumMs = 0;
        _tickWorkPeakMs = 0;
        _tickWorkCount = 0;
    }

    /// <summary>The gamemode the loaded map is being played as.</summary>
    public GameMode GameMode => _match.GameMode;

    /// <summary><c>map_width()</c> in world pixels. See <see cref="LevelMap.Width"/>.</summary>
    public int MapWidth => _match.Map.Width;

    /// <inheritdoc cref="MapWidth"/>
    public int MapHeight => _match.Map.Height;

    /// <summary>The 1-based area of a multi-area map being played.</summary>
    public byte CurrentMapArea => _match.CurrentMapArea;

    /// <inheritdoc cref="CurrentMapArea"/>
    public int TotalMapAreas => _match.TotalMapAreas;

    /// <summary>Ticks left on the setup gate, or 0 when there is none.</summary>
    public int SetupTimer => _world.SetupTimer;

    /// <summary>
    /// The map the server is on its way to, or empty when it is not changing.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A map change is a ten-second countdown, not an event</b>
    /// (<c>Rounds.MapChangeDelay</c>: <c>MAP_END</c> goes out, then the load).
    /// An operator surface that reported only "accepted" would look like nothing
    /// had happened for ten seconds.
    /// </remarks>
    public string NextMap => _world.MapChanging ? _nextMap : string.Empty;

    /// <summary>Ticks until the pending map change lands, or -1 when there is none.</summary>
    public int MapChangeCountdown => _mapChangeCountdown;

    /// <summary><c>global.redCaps</c>. ⚠️ Also the team's kill count in deathmatch.</summary>
    public int RedCaps => _world.RedCaps;

    /// <inheritdoc cref="RedCaps"/>
    public int BlueCaps => _world.BlueCaps;

    /// <summary>How many control points the loaded map has.</summary>
    public int ControlPointCount => _world.ControlPointCount;

    /// <summary>One control point's ownership and capture progress.</summary>
    /// <remarks>
    /// ⚠️ Fully qualified: <c>Gg2.Server.ControlPointState</c> also exists, on
    /// <see cref="MatchState"/>, and is a different type holding the map's copy
    /// rather than the simulation's.
    /// </remarks>
    public Gg2.Simulation.ControlPointState ControlPointAt(int index) => _world.ControlPoints[index];

    /// <summary>A team's briefcase — where it is, and whether it is on the ground.</summary>
    public IntelState IntelOf(Team team) => team == Team.Red ? _world.RedIntel : _world.BlueIntel;

    /// <summary>
    /// The slot carrying <paramref name="team"/>'s briefcase, or -1.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A team carries the OTHER team's intel</b>, so red's briefcase is
    /// held by a blue player. Getting this backwards puts the marker on the
    /// wrong side of the map, which looks like a physics bug and is not one.
    /// </remarks>
    public int IntelCarrier(Team team)
    {
        var thief = team == Team.Red ? Team.Blue : Team.Red;
        var live = Math.Min(_roster.Count, WorldState.MaxSlots);

        for (var slot = 0; slot < live; slot++)
        {
            if (_world.IsAlive(slot)
                && _roster[slot].Team == thief
                && _world.Characters[slot].CarryingIntel)
            {
                return slot;
            }
        }

        return -1;
    }

    /// <summary>Whether the character in <paramref name="slot"/> is holding a briefcase.</summary>
    public bool IsCarryingIntel(int slot) =>
        slot >= 0 && slot < WorldState.MaxSlots
        && _world.IsAlive(slot)
        && _world.Characters[slot].CarryingIntel;

    /// <summary>One roster entry's scoreboard, as the wire's stats block.</summary>
    public PlayerStatsBlock StatisticsOf(int slot) => Statistics(slot);

    /// <summary>A roster entry's name.</summary>
    public string NameOf(int slot) => _roster[slot].Name;

    /// <summary>
    /// Whether this slot is a bot.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The allocation-free way to ask.</b> <see cref="BotSlots"/> builds a
    /// <c>List&lt;int&gt;</c> on every access, which is fine for the population
    /// manager and is not fine for anything called from inside
    /// <see cref="Tick"/>.
    /// </remarks>
    public bool IsBot(int slot) => _roster[slot].IsBot;

    /// <summary>Whether this slot is a network client rather than a bot or slot 0.</summary>
    public bool IsConnected(int slot) => _roster[slot].Connection is { IsClosed: false };

    /// <summary>
    /// A connected player's address, or the empty string.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Only an operator sees this.</b> It never reaches the game's wire —
    /// the reference does not put addresses on it and neither does this port —
    /// and the web panel shows it behind the same password as everything else.
    /// </remarks>
    public string AddressOf(int slot) =>
        _roster[slot].Connection?.RemoteAddress.ToString() ?? string.Empty;

    /// <summary>
    /// Closes a player's connection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>An operator action with no client equivalent</b>, and the only one
    /// that ends a connection, so it does not go through <c>Execute</c>. The
    /// client is sent the reference's own <c>KICK</c> message with
    /// <see cref="KickReason.Operator"/> so it shows the player a kick rather
    /// than a dropped connection, and the flush happens BEFORE the close — the
    /// reference's plugin-packet
    /// kick has the same requirement, and without it the message is buffered,
    /// thrown away, and the player just sees a dropped connection.
    /// </para>
    /// <para>
    /// ⚠️ <b>A bot is refused rather than removed.</b> A bot has no connection to
    /// close, and the population manager would put it straight back on the next
    /// tick; the honest answer is that the bot count is the thing to change.
    /// </para>
    /// </remarks>
    public KickOutcome KickPlayer(int slot, string reason = "")
    {
        if (slot <= 0 || slot >= _roster.Count)
            return KickOutcome.NoSuchSlot;

        var connection = _roster[slot].Connection;

        if (connection is null || connection.IsClosed)
            return KickOutcome.NotAConnection;

        var writer = new NetWriter(2);
        ServerMessages.Kick(writer, KickReason.Operator);
        connection.Send(writer.WrittenSpan);
        connection.Flush();
        connection.Close(reason.Length == 0 ? "kicked by the operator" : reason);

        _log.Info(
            $"kicked {_roster[slot].Name} (slot {slot})"
            + (reason.Length == 0 ? string.Empty : $": {reason}"));

        return KickOutcome.Kicked;
    }

    /// <summary>
    /// The control port actually bound, or 0 when there is none.
    /// </summary>
    /// <remarks>
    /// ⚠️ Read back rather than assumed, the same as <see cref="BoundPort"/> —
    /// a test asks for port 0 and needs to learn which one it got. Hard-coding
    /// one makes the suite un-parallelisable and the failure intermittent.
    /// </remarks>
    public int ControlBoundPort => _control?.BoundPort ?? 0;

    /// <summary>The web admin's port, or 0 when there is none.</summary>
    /// <inheritdoc cref="ControlBoundPort" path="/remarks"/>
    public int WebBoundPort => _web?.BoundPort ?? 0;

    /// <summary>The password the web admin will accept, generated or configured.</summary>
    public string WebPassword => _web?.Password ?? string.Empty;

    /// <summary>
    /// How many state snapshots the panel has published.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A test hook, and it gates a claim rather than a behaviour</b>: the
    /// panel is meant to cost a server with nobody watching nothing per tick, and
    /// "only builds a snapshot when somebody is subscribed" is exactly the kind of
    /// sentence this repo has been wrong about before. WebAdminTests watches this
    /// number fail to move.
    /// </remarks>
    internal long WebSnapshotCount => _web?.SnapshotCount ?? 0;

    /// <summary>Whether the simulation is frozen.</summary>
    public bool IsFrozen => _frozen;

    /// <summary>Stops advancing the simulation. The network keeps running.</summary>
    public void Freeze()
    {
        _frozen = true;
        _stepsOwed = 0;
    }

    /// <summary>Resumes normal ticking.</summary>
    public void Resume()
    {
        _frozen = false;
        _stepsOwed = 0;
    }

    /// <summary>
    /// Advances the simulation <paramref name="ticks"/> times and stays frozen.
    /// </summary>
    /// <remarks>
    /// ⚠️ It <b>freezes first</b> rather than refusing on a running server. A
    /// step against a server that is still ticking would advance an unknowable
    /// number of times, which is the opposite of what stepping is for.
    /// </remarks>
    public void StepSimulation(int ticks)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ticks);

        _frozen = true;
        _stepsOwed = ticks;
    }

    /// <summary>
    /// Ends the round now and changes to <paramref name="map"/>.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Down the same path a finished round takes</b> —
    /// <c>Rounds.BeginMapChange</c>, the ten-second countdown and a
    /// <c>MAP_END</c> — rather than swapping the map underneath everybody. A
    /// control port that changed maps by a route no round takes would produce a
    /// sequence no client has ever been sent, and the bug would look like a
    /// client bug.
    /// </remarks>
    public bool RequestMapChange(string map, int area = 1)
    {
        if (!MapExists(map) || _world.MapChanging)
            return false;

        _nextMap = map;
        _nextArea = Math.Max(1, area);

        Rounds.BeginMapChange(ref _world);
        _mapChangeCountdown = Rounds.MapChangeDelay;

        ServerMessages.MapEnd(_broadcast, _nextMap, _world.Winner, (byte)_nextArea);
        _log.Info($"control: {_nextMap} area {_nextArea} in ten seconds");

        return true;
    }

    /// <summary>
    /// Puts a bot back on a team, the way a client asks to join one.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Goes through <c>ExecuteChangeTeam</c> — the same body the socket
    /// switch and <see cref="Execute"/> reach.</b> CLAUDE.md's rule: a bot is an
    /// <see cref="IInputSource"/> like a socket, and a second copy here would let
    /// a bot take a team a client could not. That matters concretely — the
    /// reference's balance guard refuses a request for the bigger team, and this
    /// has to be refused by it too.
    /// <para>
    /// ⚠️ <b>It exists because a MAP CHANGE makes every player a spectator.</b>
    /// <c>Rounds.ResetForNewMap</c> does that on the first area, matching the
    /// reference's <c>with(Player) team = TEAM_SPECTATOR</c>, and a human re-picks
    /// from the team-select screen. A bot has nobody to pick for it, so
    /// <see cref="BotPopulationService"/> does — see the bug in its
    /// <c>Reteam</c>.
    /// </para>
    /// </remarks>
    public void RequestBotTeam(int slot, Team team)
    {
        if (slot <= 0 || slot >= _roster.Count || !_roster[slot].IsBot)
            throw new ArgumentOutOfRangeException(nameof(slot), slot, "not a bot slot");

        ExecuteChangeTeam(_roster[slot], team);
    }

    /// <summary>Changes the bot fill target; the population manager converges on it.</summary>
    /// <remarks>
    /// ⚠️ A fill TARGET including humans, not a bot count — the reference's
    /// <c>FillToPlayers</c>. Setting it to 8 on a server with three people gives
    /// five bots.
    /// </remarks>
    public void SetBotFill(int fill)
    {
        _options.Bots = _options.Bots with
        {
            Enabled = fill > 0,
            FillToPlayers = fill,
            MaxBots = Math.Max(fill, _options.Bots.MaxBots),
        };
    }

    /// <summary>Sets the tick pacing. Infinity is unpaced.</summary>
    public void SetSpeed(double multiplier)
    {
        if (double.IsNaN(multiplier) || multiplier <= 0)
            throw new ArgumentOutOfRangeException(nameof(multiplier), multiplier, "speed must be > 0");

        _options.SpeedMultiplier = multiplier;
    }

    /// <summary>Moves a live character, leaving its velocity alone.</summary>
    /// <remarks>
    /// ⚠️ <b>Not something a client can do</b>, which is why it is here and not
    /// a <see cref="PlayerCommand"/>. It writes the position the same way a
    /// spawn does and does not check that the destination is free: putting a
    /// character inside a wall is a legitimate thing to want from a debugging
    /// tool, and the movement solver's stuck case is one of the things worth
    /// being able to reproduce on purpose.
    /// </remarks>
    public bool Teleport(int slot, double x, double y)
    {
        if (slot < 0 || slot >= _roster.Count || !_world.IsAlive(slot))
            return false;

        _world.Characters[slot].X = x;
        _world.Characters[slot].Y = y;
        _world.Weapons[slot].X = x;
        _world.Weapons[slot].Y = y;

        return true;
    }

    /// <summary>
    /// Runs a <see cref="PlayerCommand"/> for a slot on the operator's behalf.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>This is the seam that keeps the control port honest.</b> It hands
    /// the command to the same <c>Execute</c> the socket switch and the bots
    /// reach, so the port cannot do anything a client could not, and a command
    /// added later is reachable from all three without being written three
    /// times. Preconditions that fail are silence, as everywhere else.
    /// </remarks>
    public bool ExecuteFor(int slot, in PlayerCommand command)
    {
        if (slot < 0 || slot >= _roster.Count)
            return false;

        Execute(_roster[slot], command);
        return true;
    }

    // --- the simulation ------------------------------------------------------

    /// <summary>Runs one tick of physics for every character in the world.</summary>
    /// <remarks>
    /// Inputs are gathered per roster slot into a scratch buffer that lives for
    /// the life of the server: <c>Tick</c> allocating on the steady-state path
    /// is what docs/BOTAI.md item 1 rules out, and a per-tick array here would
    /// be exactly that.
    /// </remarks>
    /// <param name="count">
    /// Roster slots whose input was gathered and applied before the broadcast.
    /// ⚠️ Gathered THERE and not here, so every source is polled exactly once a
    /// tick — a bot polled twice would think twice and could answer differently.
    /// </param>
    private void AdvanceSimulation(int count)
    {
        // Before the tick, because it reads the round the *previous* tick
        // decided — the reference checks it in the server's Begin Step, which is
        // the first thing in a frame. See ServeRoundEnd.
        ServeRoundEnd();

        // ⚠️ Before the tick, and the position is measured rather than tidy —
        // see the method.
        RequestGeneratorScoreboardSync();

        MatchSimulation.Tick(_collision, ref _world, _inputs.AsSpan(0, count), ref _events, _spawnPoints);

        // ⚠️ And this one after it, equally deliberately. Same method-level
        // reasoning, opposite answer.
        RequestSetupScoreboardSync();

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

        // ⚠️ The BEGIN STEP destructions belong here, above the flush, beside
        // the landings that share their event. Only the hp<=0 ones are late.
        BroadcastSentryDestructions(count);

        BroadcastSpawns(count);
        BroadcastIntelGrabsAndCaptures(count);
    }

    /// <summary>
    /// The three messages the reference appends in an End Step — <b>after</b>
    /// its own flush, and so delivered on the following tick.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>MEASURED against the running game on 2026-09-02, not inferred.</b>
    /// <c>GameServerEndStep.gml:7-10</c> flushes and clears <c>global.sendBuffer</c>
    /// in the GameServer's End Step, and the GameServer is the OLDEST instance in
    /// the game (<c>start_server.gml:7</c>, <c>persistent</c>) — so under GM8's
    /// instance-id event order its End Step runs before every other object's, and
    /// anything a Character or a Sentry appends in <em>its</em> End Step misses
    /// that flush.
    /// <para>
    /// The probe: two bots killed on one frame, watching
    /// <c>buffer_size(global.sendBuffer)</c> once a frame from the agent bridge's
    /// Step — which is after every Begin Step and before every End Step. The frame
    /// after the deaths read <b>17 bytes against a 5-byte steady state</b>, and the
    /// 12 left over close exactly: 5 + 5 for the two <c>PLAYER_DEATH</c>s
    /// (<c>sendEventPlayerDeath.gml</c>) and 2 for the <c>BALANCE</c> notice
    /// (<c>ServerBalanceTeams.gml:29-30</c>) that a death's own
    /// <c>ServerBalanceTeams</c> writes. Those bytes could only have been appended
    /// during the PREVIOUS frame's End Steps and survived its flush.
    /// </para>
    /// <para>
    /// ⚠️ <b>This changes WHEN the bytes leave and never their order.</b> The
    /// buffer is FIFO, so these still follow the same messages they followed
    /// before and still precede the next tick's update — which is exactly why no
    /// trace can see it, and why <c>cdaa62a</c> got the boundary half right.
    /// </para>
    /// <para>
    /// ⚠️ Relative order among the three is left EXACTLY as it was, so the flush
    /// point is the only thing this changed. (The reference interleaves each
    /// character's <c>BALANCE</c> with its own death and puts every sentry's
    /// destruction after all of them; nothing observes the difference, and
    /// straightening it is not this change's business.)
    /// </para>
    /// </remarks>
    private void BroadcastEndStepEvents(int count)
    {
        BroadcastPlayerDeaths(count);

        // ⚠️ Immediately after the deaths, because that is where the reference
        // puts it: Character.events/End Step.xml sends the death and then calls
        // doEventPlayerDeath, which destroys the body, and Destroy.xml's return
        // is appended from inside that. A carrier killed by a pit returns the
        // flag AFTER its own PLAYER_DEATH, never before.
        if (_events.ReturnedRedIntelInEndStep)
            ServerMessages.ReturnIntel(_broadcast, Team.Red);

        if (_events.ReturnedBlueIntelInEndStep)
            ServerMessages.ReturnIntel(_broadcast, Team.Blue);

        BroadcastEndStepSentryDestructions(count);

        // The only in-tick BALANCE comes from a death's own ServerBalanceTeams,
        // which runs after doEventPlayerDeath.
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
        // ⚠️ The alarm-stage returns ONLY. A return that came out of a dying
        // carrier's Destroy is appended in the Character's End Step, after the
        // reference has already flushed — see TickEvents.ReturnedRedIntelInEndStep
        // and BroadcastEndStepEvents.
        if (_events.ReturnedRedIntel && !_events.ReturnedRedIntelInEndStep)
            ServerMessages.ReturnIntel(_broadcast, Team.Red);

        if (_events.ReturnedBlueIntel && !_events.ReturnedBlueIntelInEndStep)
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
    /// The sentries destroyed in a <b>Begin Step</b> — an owner who stopped
    /// being an engineer of that team, or two sentries too close together.
    /// </summary>
    /// <remarks>
    /// <c>Sentry.events/Begin Step.xml:1-3</c>, which makes its own tick's
    /// flush. Its <c>hp &lt;= 0</c> sibling does not; see
    /// <see cref="BroadcastEndStepSentryDestructions"/>.
    /// </remarks>
    private void BroadcastSentryDestructions(int count) =>
        BroadcastSentryDestructions(count, inEndStep: false);

    /// <summary>
    /// The sentries destroyed in their own <b>End Step</b> by running out of
    /// hit points, which ship on the following tick.
    /// </summary>
    private void BroadcastEndStepSentryDestructions(int count) =>
        BroadcastSentryDestructions(count, inEndStep: true);

    private void BroadcastSentryDestructions(int count, bool inEndStep)
    {
        for (var slot = 0; slot < count; slot++)
        {
            if (!_events.SentryDestroyed[slot]
                || _events.SentryDestroyedInEndStep[slot] != inEndStep)
            {
                continue;
            }

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

    /// <summary>
    /// Every moving platform's block, straight off the live world.
    /// </summary>
    /// <remarks>
    /// <c>MovingPlatform.events/User Event 10.xml</c>: <c>x_ * 5</c> and
    /// <c>y_ * 5</c> as ushorts, then the movement state. ⚠️ The underscored
    /// pair, so the quantisation is the extension's <c>write_ushort</c> of a real
    /// and nothing else — no GML <c>round()</c> on the way.
    /// </remarks>
    private int MovingPlatformBlocks(Span<MovingPlatformBlock> platforms)
    {
        var count = Math.Min(_world.MovingPlatformCount, platforms.Length);

        for (var i = 0; i < count; i++)
        {
            ref readonly var platform = ref _world.MovingPlatforms[i];

            platforms[i] = new MovingPlatformBlock(
                WireQuantise.ToUShort(platform.X * 5),
                WireQuantise.ToUShort(platform.Y * 5),
                platform.MovementState);
        }

        return count;
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

        // ⚠️ Mirrored into the world so BOT CODE can tell a person from a bot —
        // Handover.ShouldEngage under EngageOpponents.HumansOnly. ⛔ Nothing in
        // MatchSimulation may read it; BotIsNotVisibleToTheSimulationTests is the
        // gate. It goes here rather than being read off the roster so a bot's
        // decisions stay a pure function of WorldState and remain replayable.
        //
        // ⚠️ AFTER AddPlayer, which writes PlayerState.Fresh over the slot.
        _world.Players[slot].IsBot = true;

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
            {
                if (ChatBubbles.Rewrite(reader.ReadUByte(), _aprilFools, out var image))
                    ServerMessages.ChatBubble(_broadcast, IdOf(entry), image);

                break;
            }

            case MessageType.ClientSettings:
                ExecuteClientSettings(entry, reader.ReadUByte() != 0);
                break;

            case MessageType.PlayerChangeName:
                ExecuteChangeName(client, entry, command.Payload);
                break;

            case MessageType.BuildSentry:
                ExecuteBuildSentry(IdOf(entry));
                break;

            case MessageType.DestroySentry:
                ExecuteDestroySentry(IdOf(entry));
                break;

            case MessageType.DropIntel:
                ExecuteDropIntel(IdOf(entry));
                break;

            case MessageType.ToggleZoom:
                ExecuteToggleZoom(IdOf(entry));
                break;

            case MessageType.Omnomnomnom:
                ExecuteEatSandvich(IdOf(entry));
                break;

            // ⚠️ A plugin packet is KICKED, not ignored, and that IS the
            // reference's behaviour rather than a policy of ours.
            // processClientCommands.gml:338-362 hands the packet to
            // _PluginPacketPush, which returns false when the id is not in
            // global.pluginPacketBuffers — a map populated only by
            // loadserverplugins. A server with no plugins loaded has an empty
            // map, so EVERY id is unrecognised and every sender is kicked with
            // KICK_BAD_PLUGIN_PACKET. This server never loads plugins (it
            // cannot: they are GML), so that branch is the only reachable one.
            //
            // ⚠️ Unreachable from a stock client, deliberately. It only sends
            // plugin packets after loading the plugin list the HELLO advertised,
            // and JoiningConnection advertises none — so reaching this means a
            // client that made something up, which is exactly who the reference
            // kicks.
            case MessageType.PluginPacket:
            {
                var kick = new NetWriter(2);
                ServerMessages.Kick(kick, KickReason.BadPluginPacket);
                client.Connection.Send(kick.WrittenSpan);

                // ⚠️ Flush BEFORE closing, or the kick is buffered and thrown
                // away and the client just sees a dropped connection with no
                // reason. The reference writes to the socket and then destroys
                // it, which has the same requirement; the multi-client refusal
                // above is the other place in this file that gets it right.
                client.Connection.Flush();
                client.Connection.Close("unrecognised plugin packet id");

                _log.Info($"kicked {entry.Name}: plugin packet on a server with no plugins");
                break;
            }

            // ⚠️ The client is not told that rewards are off, because the
            // reference has no way to say so — there is no "rewards disabled"
            // message and no flag in the greeting. A client whose REWARD_REQUEST
            // goes unanswered simply never gets its hats, and it neither retries
            // nor blocks (ClientBeginStep.gml:163-170 sends it once, on join).
            case MessageType.RewardRequest:
                ExecuteRewardRequest(client, entry, command.Payload);
                break;

            case MessageType.RewardChallengeResponse:
                ExecuteRewardChallengeResponse(entry, command.Payload);
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

            case PlayerCommandKind.EatSandvich:
                ExecuteEatSandvich(IdOf(entry));
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

    /// <summary>
    /// <c>CLIENT_SETTINGS</c>: one client's queued-jumping preference.
    /// </summary>
    /// <remarks>
    /// <c>processClientCommands.gml:364-371</c>. Three writes, and none of them
    /// is redundant: the roster copy is what a joiner's <c>FULL_UPDATE</c>
    /// carries, the <see cref="WorldState"/> copy is what the physics reads, and
    /// the broadcast is what already-connected clients dead-reckon with. Missing
    /// any one of them puts somebody's jump on a different rule from everybody
    /// else's.
    /// </remarks>
    private void ExecuteClientSettings(RosterEntry entry, bool queueJump)
    {
        var slot = IdOf(entry);

        entry.QueueJump = queueJump;
        _world.Players[slot].QueueJump = queueJump;

        ServerMessages.ClientSettings(_broadcast, (byte)slot, queueJump);
    }

    /// <summary>
    /// <c>PLAYER_CHANGENAME</c>: rename, throttle, or kick.
    /// </summary>
    /// <remarks>
    /// <c>processClientCommands.gml:279-317</c>, and ⚠️ <b>an over-long name is
    /// a KICK here, not a truncation.</b> The join path truncates
    /// (<c>serviceJoiningPlayer.gml:176</c>) and this one does not, which looks
    /// like an inconsistency in the reference and is not ours to tidy: a client
    /// that renames past the limit is one that did not write the message its own
    /// UI can produce.
    /// <para>
    /// ⚠️ <b>The throttle is one change per second, and it is the flood
    /// control.</b> Every accepted rename is an unbounded broadcast to every
    /// connected client; without it one socket can make the server send as fast
    /// as it can read. The reference keys it on <c>current_time</c> per player.
    /// </para>
    /// </remarks>
    private void ExecuteChangeName(ClientPlayer client, RosterEntry entry, ReadOnlySpan<byte> payload)
    {
        // ⚠️ The reference tests the PAYLOAD length in bytes, before decoding —
        // `socket_receivebuffer_size(socket)`, which is the count the length
        // prefix declared. Testing the decoded string instead would let a name
        // of 30 bytes that decodes to 20 characters through.
        if (payload.Length > GameConstants.MaxPlayerNameLength)
        {
            var kick = new NetWriter(2);
            ServerMessages.Kick(kick, KickReason.Name);
            client.Connection.Send(kick.WrittenSpan);

            // Flush before closing, or the reason is buffered and discarded and
            // the client sees only a dropped connection — the same requirement
            // the plugin-packet and multi-client kicks have.
            client.Connection.Flush();
            client.Connection.Close("name longer than the protocol allows");

            _log.Info($"kicked {entry.Name}: PLAYER_CHANGENAME of {payload.Length} bytes");
            return;
        }

        // ⚠️ The throttled command is DROPPED, not deferred. The reference's
        // `break` leaves the socket's other commands to carry on, so a client
        // spamming renames simply has most of them ignored.
        if (entry.LastNameChangeMs is { } last && _nowMs - last < NameChangeIntervalMs)
            return;

        entry.LastNameChangeMs = _nowMs;
        entry.Name = Gg2Text.Decode(payload);

        ServerMessages.PlayerChangeName(_broadcast, IdOf(entry), entry.Name);
    }

    /// <summary>
    /// <c>REWARD_REQUEST</c>: mint a nonce and challenge the client.
    /// </summary>
    /// <remarks>
    /// <c>processClientCommands.gml:320-326</c>, which answers <b>every</b>
    /// request unconditionally — there is no server-side auth configuration in
    /// the reference at all, and the <c>RewardKey</c>/<c>RewardId</c> pair a host
    /// can set controls only its <em>own</em> self-auth. This port gates the
    /// reply on <c>--rewards</c> instead, because answering commits it to an
    /// outbound connection to a third party later in the handshake.
    /// <para>
    /// ⚠️ <b>A second request overwrites the first.</b> The reference stores the
    /// id and challenge in plain player variables and does the same. It costs
    /// nothing, because <see cref="RosterEntry.RewardAuthStarted"/> is what
    /// actually bounds the work a client can cause.
    /// </para>
    /// </remarks>
    private void ExecuteRewardRequest(ClientPlayer client, RosterEntry entry, ReadOnlySpan<byte> payload)
    {
        if (_rewards is null)
            return;

        entry.RewardId = Gg2Text.Decode(payload);
        entry.RewardChallenge = RewardAuth.NewChallenge();

        // ⚠️ To this socket, not to _broadcast. A challenge addressed to
        // everybody would leak it and hand every other client a message its
        // stream cannot parse.
        var writer = new NetWriter(1 + RewardAuth.ChallengeBytes);
        ServerMessages.RewardChallengeCode(writer, entry.RewardChallenge);
        client.Connection.Send(writer.WrittenSpan);
    }

    /// <summary>
    /// <c>REWARD_CHALLENGE_RESPONSE</c>: forward the client's answer to the auth
    /// server.
    /// </summary>
    /// <remarks>
    /// <c>processClientCommands.gml:328-336</c>. ⛔ <b>This server verifies
    /// nothing</b> — it does not have the client's key and is not meant to. It
    /// relays, and the auth server is the only party that can tell a real answer
    /// from a made-up one.
    /// <para>
    /// The reference's two guards are both here: <c>variable_local_exists</c> on
    /// the id and challenge, which drops a response to a challenge that was
    /// never issued, and <c>rewardAuthAlreadyStarted</c>, which bounds a client
    /// to one outbound connection for the life of its session.
    /// </para>
    /// </remarks>
    private void ExecuteRewardChallengeResponse(RosterEntry entry, ReadOnlySpan<byte> payload)
    {
        if (_rewards is null)
            return;

        if (entry.RewardId is null || entry.RewardChallenge is null)
            return;

        if (entry.RewardAuthStarted)
            return;

        entry.RewardAuthStarted = true;

        // ipCheck: true. The auth server folds the address this request arrives
        // from into the HMAC it recomputes, which is what stops this server
        // replaying the client's answer at a different one. Only the reference's
        // self-auth path passes false, and this port has no local player to
        // decorate — docs/PROTOCOL.md, "What a headless server can skip".
        _rewards.Enqueue(
            entry,
            RewardAuth.BuildAuthRequest(payload, entry.RewardChallenge, ipCheck: true, entry.RewardId));
    }

    /// <summary>
    /// Applies whatever auth checks finished since the last tick.
    /// </summary>
    /// <remarks>
    /// <c>rewardAuthSuccess.gml</c> → <c>sendEventUpdateRewards</c>. ⛔ <b>The
    /// roster index is resolved HERE, not when the check was queued.</b> Several
    /// seconds pass over an auth round trip, and any <c>PLAYER_LEAVE</c> in that
    /// window compacts every index above the hole — so the slot that asked is
    /// not necessarily the slot that answers to that number now.
    /// <see cref="Roster.IndexOf"/> on the entry object is the reference's
    /// <c>instance_exists(currentPlayer)</c>: -1 means they left, and the result
    /// is dropped.
    /// </remarks>
    private void ServiceRewardAuth()
    {
        if (_rewards is null)
            return;

        while (_rewards.TryTakeResult(out var result))
        {
            if (result.Rewards is null)
                continue;

            var slot = _roster.IndexOf(result.Player);
            if (slot < 0)
                continue;

            // Remembered as well as broadcast: the broadcast reaches whoever is
            // connected now, and this is what a later joiner's FULL_UPDATE
            // carries.
            result.Player.Rewards = result.Rewards;

            ServerMessages.RewardUpdate(_broadcast, (byte)slot, result.Rewards);
            _log.Info($"rewards for {result.Player.Name} authenticated");
        }
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

    private void ExecuteEatSandvich(int slot)
    {
        // serverEatSandvich.gml, and the same shape as the zoom beside it: the
        // preconditions live in the simulation, and a refusal is silence.
        //
        // ⚠️ The broadcast carries no timer and no hp. The client re-derives
        // the cooldown itself from the same OMNOMNOMNOM byte
        // (ClientBeginStep.gml:386-400), and the healing arrives through the
        // ordinary position updates a tick at a time — so a client that missed
        // this message sees a heavy healing for no reason, not a desynchronised
        // one.
        if (PlayerCommands.EatSandvich(ref _world, slot))
            ServerMessages.Omnomnomnom(_broadcast, (byte)slot);
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

        // ⚠️ Read BEFORE the removal. GameServerBeginStep.gml:46-49 re-announces
        // only when the server WAS full, so that a list showing "10/10" stops
        // showing it the moment a slot opens — the case where a stale entry
        // actually costs somebody a game. Sampling after the remove would make
        // the test `occupied == limit` never true and the branch dead.
        var wasFull = OccupiedSlots() == _options.PlayerLimit;

        // ⚠️ Before the removal, or this connection's traffic vanishes from the
        // running total. See _bytesRetired.
        _bytesRetired += client.Connection.BytesSent;

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

        if (wasFull)
            AnnounceToLobby();
    }

    // --- broadcast -----------------------------------------------------------

    /// <summary>
    /// <c>Generator.events/Begin Step.xml:24-25</c>: a live generator asks the
    /// scoreboard to resync every minute, on top of the two-minute heartbeat.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Below <see cref="WriteStateUpdate"/>, so the flag it sets is read
    /// on the NEXT tick.</b> That is the reference's timing, not a delay this
    /// port adds: a <c>Generator</c> is a room object and the GameServer is
    /// persistent and older, so the generator's Begin Step runs after
    /// <c>GameServerBeginStep.gml:55</c> has already tested <c>syncTimer</c>
    /// that frame. Keyed on <c>GameServer.frame</c> rather than living in
    /// <c>Gg2.Simulation</c> because that counter is the server's own and not
    /// part of the world — the same reason the map-change countdown is here.
    /// <para>
    /// ⚠️ <b>And ABOVE <see cref="MatchSimulation.Tick"/>, which is the half
    /// that is easy to get wrong.</b> The reference's guard is an
    /// <c>else if</c> under <c>if hp &lt;= 0</c> — a generator about to be
    /// destroyed does not ask — and it reads that <c>hp</c> in the generator's
    /// own Begin Step, before any character has acted. Reading it after the tick
    /// would swallow the request whenever a generator died later in the same
    /// tick. Reachable only on a multiple of 1800, which is precisely the kind
    /// of tick nothing would ever notice going wrong.
    /// </para>
    /// </remarks>
    private void RequestGeneratorScoreboardSync()
    {
        if (_frame % 1800 != 0)
            return;

        if ((_world.RedGenerator.Exists && !Gml.LessOrEqual(_world.RedGenerator.Hp, 0))
            || (_world.BlueGenerator.Exists && !Gml.LessOrEqual(_world.BlueGenerator.Hp, 0)))
        {
            _capsSyncPending = true;
        }
    }

    /// <summary>
    /// <c>GameServerBeginStep.gml:55</c>'s third trigger — the setup timer
    /// reaching 180, six seconds before the gates open.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>After <see cref="MatchSimulation.Tick"/>, unlike
    /// <see cref="RequestGeneratorScoreboardSync"/>.</b> The reference reads a
    /// value the HUD's Step decrements — <c>ControlPointHUD.events/Step.xml</c>
    /// :21-22 and <c>InvasionHUD.events/Step.xml</c>:18-19, both younger than
    /// the GameServer — so the frame that decrements it to 180 is not the frame
    /// that sees 180. <see cref="Rounds.StepSetupTimer"/> is this port's copy of
    /// those five lines, so the value after the tick is the one the reference's
    /// next Begin Step reads.
    /// <para>
    /// ⚠️ <b>This trigger did not exist until 2026-09-02</b>, and the comment
    /// standing where it belonged said it "waits on the setup timer being
    /// simulated" — which had stopped being true at some point nobody recorded:
    /// <c>WorldState.SetupTimer</c> is simulated and goes out on the wire.
    /// CLAUDE.md's "a claim the repo makes about itself", found by reading
    /// rather than by any test, because no test could.
    /// </para>
    /// </remarks>
    private void RequestSetupScoreboardSync()
    {
        if (_world.SetupTimer == SetupSyncTick)
            _capsSyncPending = true;
    }

    private void WriteStateUpdate()
    {
        // Cadence is part of the contract: the client dead-reckons between the
        // position-bearing updates. Every tick but the seventh is an INPUTSTATE.
        // GameServerBeginStep.gml:55-67 — an extra scoreboard message when
        // something asked for one, and a two-minute heartbeat so a client that
        // missed one is corrected eventually.
        //
        // ⛔ NOTHING MAY SET _capsSyncPending FROM IN HERE. `syncTimer` has no
        // writer inside GameServerBeginStep either: all nine of its setters are
        // objects YOUNGER than the GameServer, so every one of them runs after
        // line 55 has already tested it, and the extra message lands on the
        // FOLLOWING tick. A set-and-consume in this method is a CAPS_UPDATE a
        // tick early — which is what the generator's minute timer did from the
        // day it landed until 2026-09-02. The two triggers keyed on the
        // last two that were still asked for up here — the generator's minute
        // timer and the setup timer's six-second mark — are in
        // RequestGeneratorScoreboardSync and RequestSetupScoreboardSync now,
        // below the broadcast with every other request.
        // CapsSyncPhaseTests is the gate.
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

        _flushes++;

        // One identical byte stream to every client: one serialisation, N sends.
        // Keep it that way — it is why the send path is nearly free.
        var bytes = _broadcast.WrittenSpan;
        foreach (var client in _clients)
            client.Connection.Send(bytes);

        // ⚠️ The recording tees HERE and nowhere else, which is what makes it
        // exactly what the clients saw rather than a reconstruction of it.
        // docs/BOTAI.md item 3: one recorder, not a second logging path.
        _recorder?.Append(bytes);

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
        ServerMessages.ChangeMap(writer, _currentMap, string.Empty);

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
            $"{entry.Name} joined as player {_roster.Count - 1}; "
            + $"{_roster.Count - 1} of {_options.PlayerLimit} slots used");

        // serviceJoiningPlayer.gml:171 — tell the lobby the player count moved,
        // rather than letting the list show a stale one for up to 30 seconds.
        AnnounceToLobby();
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
            throw new FileNotFoundException($"no map at {path}", path);

        var bytes = File.ReadAllBytes(path);

        // serverGotoMap.gml:4-12 — an internal map advertises NO md5 and cannot
        // be downloaded; anything else advertises the file's hash so a client
        // that lacks it knows to ask. InternalMaps is the same switch the client
        // uses, aliases included.
        if (InternalMaps.IsInternal(name))
        {
            _currentMapMd5 = string.Empty;
            _currentMapFile = null;
        }
        else
        {
            _currentMapMd5 = MapDownload.Md5Of(bytes);
            _currentMapFile = bytes;
        }

        return LevelMap.FromPng(name, bytes);
    }

    public void Dispose()
    {
        // First: a segment only becomes a readable .trace when it is closed, so
        // a recording lost here is a recording that never existed.
        _recorder?.Dispose();
        _recorder = null;

        // GameServer.events/Destroy.xml:16-17. Best effort, and before the
        // sockets go: a listing nobody takes down lingers until the lobby's own
        // timeout, advertising a server that is not there.
        //
        // ⚠️ The reference does a BLOCKING DNS lookup here, on purpose, so the
        // datagram is out before GM8 tears the networking extension down. .NET's
        // UdpClient.Send resolves synchronously too, so the same guarantee holds
        // without the sleep loop — but it does mean Dispose can block briefly on
        // a slow resolver.
        _lobby?.Unregister();
        _lobby?.Dispose();
        _lobby = null;

        // Any auth check still in flight is abandoned. Nothing is lost that
        // matters: the result would have been a hat on a player who is about to
        // be disconnected anyway.
        _rewards?.Dispose();
        _rewards = null;

        _control?.Dispose();
        _control = null;

        _web?.Dispose();
        _web = null;

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
