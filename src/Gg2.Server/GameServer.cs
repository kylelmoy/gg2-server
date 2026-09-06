using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Simulation;
using Gg2.Bots;
using Gg2.Server.Plugins;

namespace Gg2.Server;

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
public sealed partial class GameServer : IDisposable, IPluginHost
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
    /// <b>Measured in <see cref="Run"/> only, so it is zero everywhere a
    /// benchmark runs.</b> <c>HeadlessMatch</c>, the env and <c>eval</c> drive
    /// <see cref="Tick"/> directly and never see this; it exists so an operator
    /// watching the panel can see the tick approaching its deadline.
    /// <b>Not a benchmark number</b>: it is an EMA over whatever the server
    /// happened to be doing, including a rotation's map load, which is a
    /// different measurement from <c>tools/bench/</c>'s.
    /// </remarks>
    private double _tickWorkMs;

    /// <summary>When <see cref="Start"/> ran, for the panel's uptime.</summary>
    private DateTime _startedUtc = DateTime.UtcNow;

    /// <summary>
    /// The tick's own cost since the last time a sampler took it: the sum, the
    /// worst, and how many ticks that was over.
    /// </summary>
    /// <remarks>
    /// <b>An accumulator rather than a history, and the reader RESETS it.</b>
    /// A ring here would put a policy about how far back somebody wants to look
    /// into <see cref="GameServer"/>, and would cost the same on a server nobody
    /// is watching. <see cref="TakeTickWork"/> is called once a second by the
    /// web panel's sampler and by nothing else.
    /// Written in <see cref="Run"/> only, so it is zero everywhere a benchmark
    /// runs — <c>HeadlessMatch</c>, the env and <c>eval</c> drive Tick directly.
    /// </remarks>
    private double _tickWorkSumMs;

    /// <inheritdoc cref="_tickWorkSumMs"/>
    private double _tickWorkPeakMs;

    /// <inheritdoc cref="_tickWorkSumMs"/>
    private int _tickWorkCount;

    /// <summary>Ticks that overran their pacing interval since startup.</summary>
    /// <remarks>
    /// <b>Pacing, never simulation</b>, the same as <see cref="LateTickMonitor"/>
    /// — a tick computes the same thing whenever it runs. A rising count is a
    /// delivery problem, and its symptom is a client visibly correcting.
    /// <see cref="Run"/> only, for the reason above.
    /// </remarks>
    private long _lateTicks;

    /// <inheritdoc cref="_lateTicks"/>
    private double _worstOverrunMs;

    /// <summary>Bytes sent by connections that have since gone.</summary>
    /// <remarks>
    /// Kept so the total only ever rises. Summing the LIVE connections alone
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

    /// <summary>
    /// The server-sent plugins this server advertises and runs the host half of.
    /// <see cref="ServerPlugins.None"/> unless <c>--plugins</c> was passed.
    /// </summary>
    private ServerPlugins _plugins = ServerPlugins.None;

    /// <summary>
    /// Where a plugin's zip hash comes from when neither the operator nor
    /// <c>ServerPluginCatalogue</c> pinned one. Settable so a test never reaches
    /// ganggarrison.com; a null one is built on demand in <see cref="Start"/>
    /// and disposed there, so a server with no plugins opens no HTTP client.
    /// </summary>
    public IPluginHashSource? PluginHashSource { get; set; }

    /// <summary>
    /// GML's <c>current_time</c> for the plugins, sampled once at the top of
    /// each tick.
    /// </summary>
    private long _pluginClock = Environment.TickCount64;

    /// <summary>
    /// The plugins' own random source. Seeded from the port for the same reason
    /// <c>_spawnRandom</c> is: two servers on one machine roll differently and
    /// one server rolls the same way every run.
    /// </summary>
    private Random _pluginRandom = new(0);

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

    /// <summary>
    /// The bot population manager, once <see cref="Start"/> has made one.
    /// </summary>
    /// <remarks>
    /// Internal, for tests that need to see which nav graph a map change actually
    /// ended up with — "the server built one for this custom map" is otherwise
    /// only observable through a log line, and a test that asserts on log text is
    /// pinned to the wording rather than to the behaviour.
    /// </remarks>
    internal BotPopulationService? Population => _population;

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
    /// <b>Exposed because a test cannot see this from outside and one needs
    /// to.</b> <c>FlushFingerprintTests</c> groups messages by TICK — it ticks
    /// once and drains — which is only the same as grouping by flush while this
    /// invariant holds. A second flush inside one tick would merge two groups'
    /// worth of messages into one drain and the fingerprint would read as if
    /// nothing had changed; this counter gates that.
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
    /// Empty is not "unknown" -- it is the signal that the map cannot be
    /// downloaded, and the reference guards on exactly that
    /// (<c>advertisedMapMd5 != ""</c>).
    /// </remarks>
    private string _currentMapMd5 = string.Empty;

    /// <summary>The current map's bytes, kept only when it is downloadable.</summary>
    /// <remarks>
    /// Held in memory for the life of the map rather than re-read per client:
    /// the MD5 advertised must be of the bytes actually served, and re-reading
    /// opens a window where a file edited on disk mid-match is served under the
    /// old hash. Null for a built-in map, which is most of them.
    /// </remarks>
    private byte[]? _currentMapFile;

    private string _nextMap = string.Empty;

    /// <summary>
    /// A map that replaces the rotation's pick for the next round only, or null.
    /// </summary>
    /// <remarks>
    /// <b>One shot, and that is a deliberate difference from the reference's
    /// votemap plugin</b>, which assigns <c>global.map_rotation</c> a
    /// single-entry list and never puts the real one back — so one successful
    /// vote replaces the server's rotation for the rest of its life. A
    /// one-round override is what the players are actually voting for.
    /// </remarks>
    private string? _nextMapOverride;

    private int _nextArea = 1;

    /// <summary><c>GameServer.currentMapIndex</c> into the rotation.</summary>
    private int _rotationIndex;

    /// <summary>
    /// <see cref="ServerOptions.MapRotation"/> after <see cref="ShuffleMode"/> has
    /// been applied — the list <see cref="NextMapInRotation"/> actually walks.
    /// </summary>
    /// <remarks>
    /// <b>Shuffled ONCE, in <see cref="Start"/>, not per map change.</b>
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
    /// <b>Null is the default and means "announce nothing".</b> Every call
    /// site below is null-conditional rather than wrapped in an option check, so
    /// a forgotten check cannot accidentally publish this server's address.
    /// </remarks>
    private LobbyAnnouncer? _lobby;

    /// <summary>
    /// The reward auth relay, or null when <c>--rewards</c> was not passed.
    /// </summary>
    /// <remarks>
    /// <b>Null is the default and means "answer nothing".</b> Every call site
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
    /// <b>Monotonic, deliberately.</b> Everything that reads this is measuring
    /// an elapsed duration, and a wall clock is the wrong instrument for that: an
    /// NTP correction or a DST step going backwards would stall the name throttle
    /// for as long as the jump. <see cref="LocalClock"/> is the wall clock, is
    /// separate, and is only ever asked what day it is.
    /// </para>
    /// </remarks>
    private long _nowMs;

    /// <summary>
    /// The bounds one socket is held to, lifted out of the options once.
    /// </summary>
    /// <remarks>
    /// Read on every accept and handed to every connection, so it is a field
    /// rather than an <c>_options.Limits</c> walk. Immutable, which is why one
    /// instance is shared by every connection rather than copied per client.
    /// </remarks>
    private ConnectionLimits _limits = ConnectionLimits.Default;

    /// <summary>
    /// Rate limit on the "refused a connection" line.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A refusal is a CLIENT-driven event, so its log rate is chosen by
    /// whoever is connecting.</b> One line per refused connection is a flood
    /// that arrives on the tick thread, where writing it is synchronous — the
    /// defence would become the outage. <see cref="LogThrottle"/> keeps the
    /// count so the burst is still visible.
    /// </remarks>
    private readonly LogThrottle _refusalLog = new(burst: 5, per: TimeSpan.FromSeconds(10));

    /// <summary>
    /// Rate limit on the join and leave lines.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Connection churn is client-driven too, and a refusal is not the
    /// only way to make the server write a line per connection.</b> A client can
    /// join properly, get dropped for breaking a limit, reconnect and repeat;
    /// that path produces two lines a cycle and is bounded only by
    /// <see cref="ConnectionLimits.MaxAcceptsPerTick"/> — around a thousand
    /// lines a second.
    /// <para>
    /// The burst is sized so ordinary play never reaches it: thirty lines per
    /// ten seconds passes a whole server emptying and refilling, twice over.
    /// Separate from <see cref="_refusalLog"/> so a flood of one never hides the
    /// other — they are different attacks, and an operator seeing only the
    /// louder one would be missing half of what is happening.
    /// </para>
    /// </remarks>
    private readonly LogThrottle _churnLog = new(burst: 30, per: TimeSpan.FromSeconds(10));

    /// <summary>
    /// Addresses an operator has refused. Empty unless somebody put one in.
    /// </summary>
    /// <remarks>
    /// ⛔ Nothing adds to this automatically — see <see cref="BanList"/> for why
    /// an automatic ban on a shared address is how a whole school gets locked
    /// out of a server nobody was attacking. <see cref="_connectionRate"/> is
    /// what answers a machine, because it lapses on its own.
    /// </remarks>
    private BanList _bans = null!;

    /// <summary>How fast one address may open connections, or null when off.</summary>
    private ConnectionRateLimiter? _connectionRate;

    /// <summary>The ban list, for the control surfaces.</summary>
    public BanList Bans => _bans;

    /// <summary>
    /// How many ticks have failed, and whether that is still survivable.
    /// </summary>
    /// <remarks>
    /// Read by <c>Run</c> and reported by <see cref="TickFaults"/>. A healthy
    /// server's count is zero for its whole life.
    /// </remarks>
    private readonly TickFaultBudget _faults = new();

    /// <summary>
    /// Ticks that threw and were absorbed, since start.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Anything other than zero is a bug that has already happened.</b>
    /// The server carried on, which is the right behaviour for the players on it
    /// and is not the same as nothing being wrong — the stack trace is in the
    /// log at error level, and this is the number that says to go and look.
    /// </remarks>
    public int TickFaults => _faults.Total;

    /// <summary>
    /// Bots whose decision code threw this tick, to be removed after the pass.
    /// </summary>
    /// <remarks>
    /// A field rather than a local so the common case — which is every tick of
    /// every healthy server — allocates nothing. Cleared by
    /// <c>ServiceBots</c> after it has drained it.
    /// </remarks>
    private readonly List<RosterEntry> _faultedBots = [];

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

        // The web panel shows the server's log, so the log has to be captured
        // as well as printed — and the wrap happens HERE rather than in Start()
        // so that the lines Start() itself writes (the listening line, the map,
        // the generated password) are in the buffer a browser sees. A panel whose
        // log begins after startup is missing the half an operator most wants.
        _webLog = options.Web.Enabled ? new LogRing() : null;
        _log = _webLog is null ? log : new TeeLog(log, _webLog);

        // ⚠️ Here as well as in Start, because a caller can reach Bans before
        // Start has run — the control surfaces are constructed against a
        // GameServer, and a test drives Tick directly. Start replaces it with
        // one that has read the file.
        _bans = new BanList(options.BanFile, _log);

        // Once up front as well as on the cadence, so the first thirty seconds
        // of a server started on April 1st are not the odd ones out.
        _aprilFools = ChatBubbles.IsAprilFools(LocalClock());
    }

    /// <summary>
    /// The host's <b>local</b> calendar clock. Test hook; not part of the
    /// protocol.
    /// </summary>
    /// <remarks>
    /// <b>Separate from the UTC instant <see cref="Tick"/> takes, and
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
    /// The name at <paramref name="slot"/>, as clients know it.
    /// </summary>
    /// <remarks>
    /// Not necessarily the name that slot's client asked for: a plugin may
    /// have renamed it (<c>chat_v2_pr</c> de-duplicates), and a
    /// <c>PLAYER_CHANGENAME</c> went out when it did.
    /// </remarks>
    public string NameOf(int slot) => _roster[slot].Name;

    /// <summary>
    /// The map that is loaded right now.
    /// </summary>
    /// <remarks>
    /// <b>Not <see cref="ServerOptions.Map"/>.</b> Those are the same string
    /// until the first rotation and different for ever after; reading the
    /// option instead makes every player joining after round one be told the
    /// wrong map and simulate a different world, silently — <c>MapRotationTests</c>.
    /// </remarks>
    public string CurrentMap => _currentMap;

    /// <summary>
    /// <c>getNumberOfOccupiedSlots</c>: humans only.
    /// </summary>
    /// <remarks>
    /// Neither bots nor the server's own reserved slot 0 count. Including
    /// either would make the fill target chase its own tail — every bot added
    /// would reduce the deficit it was added for.
    /// </remarks>
    public int HumanCount => OccupiedSlots();

    /// <summary>Every bot's roster slot, ascending.</summary>
    /// <remarks>
    /// Rebuilt whenever the roster has changed since the last call: a departure
    /// compacts the roster, so a list cached across one is stale and the failure
    /// is a bot removed from somebody else's slot. <see cref="Roster.Version"/>
    /// is what says the roster moved. The population manager reads this every
    /// tick, which is why it is not simply rebuilt per call — that would be a
    /// steady-state allocation on an otherwise allocation-free tick.
    /// <para>
    /// A rebuild makes a <em>new</em> list rather than clearing the old one, so
    /// a caller enumerating the previous list while removing bots is not
    /// enumerating a collection being modified under it.
    /// </para>
    /// </remarks>
    public IReadOnlyList<int> BotSlots
    {
        get
        {
            if (ReferenceEquals(_botSlotsRoster, _roster) && _botSlotsVersion == _roster.Version)
                return _botSlots;

            var slots = new List<int>();

            for (var i = 1; i < _roster.Count; i++)
            {
                if (_roster[i].IsBot)
                    slots.Add(i);
            }

            _botSlots = slots;
            _botSlotsRoster = _roster;
            _botSlotsVersion = _roster.Version;

            return slots;
        }
    }

    private IReadOnlyList<int> _botSlots = [];
    private Roster? _botSlotsRoster;
    private int _botSlotsVersion;

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
    /// <b>Deliberately reads the WORLD rather than the roster.</b> The two are
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
    /// Everybody, humans included: a bot filling a server is balancing the
    /// game rather than balancing the bots. Slot 0 is skipped, because the
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
        // Random.Shared, never _world.Random and never _spawnRandom — the first
        // is observable simulation, and the second is seeded from the port so a
        // bot population rolls the same way on every run, the opposite of what a
        // shuffle wants.
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

        // AFTER the roster and its slot 0, and BEFORE anything can connect.
        // GameServerCreate.gml:126-152 loads plugins at exactly this point for
        // the same two reasons: the greeting has to be able to name them, and a
        // plugin's per-player table has to see slot 0 appear like any other.
        // It can block on the network — nothing is listening yet, so nothing is
        // being starved — do not move it onto the tick path.
        LoadPlugins();

        // Before OpenMap, because OpenMap is what points it at a map — and after
        // AddPlayer(0), because its first Retarget walks the roster.
        //
        // Seeded from the port, so two servers on one machine roll differently and
        // one server rolls the same way every run. NOT WorldState.Random: that
        // stream is observable simulation and a bot joining must not move it.
        _population = new BotPopulationService(this, _log, (uint)_options.Port);

        // Bots with no nav directory at all fight but do not navigate.
        // MapChanged says "no nav cache for <map>" when a directory is
        // configured and that map misses, but an EMPTY NavCacheDirectory skips
        // that branch entirely, so this warning is what makes an empty
        // GG2_NAVCACHE or a binary copied without nav/ legible. docs/DEPLOY.md.
        // With --navgen on (the default) the server builds the graph itself, so
        // this only fires when generation is off too — the one case left where
        // a missing directory still costs anything.
        if (_options.Bots.Enabled && _options.NavCacheDirectory.Length == 0 && !_options.NavGenerate)
        {
            _log.Info(
                "no nav directory and --no-navgen: bots will fight but not navigate. "
                + "Pass --botnav <path>, set GG2_NAVCACHE, or drop --no-navgen "
                + "(docs/DEPLOY.md)");
        }

        // Not _world.Random and not _spawnRandom. See IPluginHost.Random.
        _pluginRandom = new Random(_options.Port * 7919);

        var map = OpenMap(OpeningMap(), area: 1);

        // `() => _match`, not `_match`. OpenMap replaces the MatchState on
        // every rotation, and a writer that captured the object once wrote the
        // PREVIOUS map's gamemode for ever — see StateUpdateWriter.Match.
        _stateWriter = new StateUpdateWriter(
            () => _match, TryGetCharacter, TryGetSentry, Objective, Statistics, Dominating,
            MovingPlatformBlocks);

        _startedUtc = DateTime.UtcNow;

        // Before the listener: every connection accepted below is constructed
        // with these, and ServerOptions.Validate has already checked them.
        _limits = _options.Limits;

        _bans = new BanList(_options.BanFile, _log);
        _bans.Load();

        _connectionRate = _limits.ConnectionBurstPerAddress > 0
            ? new ConnectionRateLimiter(_limits.ConnectionBurstPerAddress, _limits.ConnectionBurstWindow)
            : null;

        _listener = new TcpListener(IPAddress.Any, _options.Port);
        _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
        _listener.Start();
        BoundPort = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _log.Info(
            $"listening on {BoundPort}, map {map.Name} ({_match.GameMode}, " +
            $"{map.Walkmask.Width}x{map.Walkmask.Height} cells), {_options.PlayerLimit} slots");

        // Off unless asked for: this publishes the server's address to a third
        // party. The first registration goes out here rather than waiting for
        // the 30-second cadence, so a short-lived server still gets listed.
        if (_options.AnnounceToLobby)
        {
            _lobby = new LobbyAnnouncer(_log);
            _log.Info($"announcing to lobby {GameConstants.LobbyServerHost}:{GameConstants.LobbyServerPort}");
            AnnounceToLobby();
        }

        // Off unless asked for, and for a sharper reason than the lobby's: this
        // makes the server relay its players' user ids to a third party.
        // The reference answers every REWARD_REQUEST unconditionally, so this
        // gate is a deliberate departure and not a transcription — see
        // ExecuteRewardRequest.
        if (_options.Rewards)
        {
            _rewards = new RewardAuthClient(_log, _options.AuthServerHost, _options.AuthServerPort);
            _log.Info($"reward auth via {_options.AuthServerHost}:{_options.AuthServerPort}");
        }

        // Loopback only and off unless configured — it executes commands
        // against a live game, which is the same reason the GM8 bridge is.
        if (_options.ControlPort >= 0 && _options.ControlPortEnabled)
        {
            _control = new ControlPort(this, _log);
            _control.Start(_options.ControlPort);
        }

        // The one surface that CAN be reachable from the network, and the only
        // reason it is allowed to be: managing a server you are not sitting at
        // is what it is for. Its own thread, a password that cannot be empty,
        // and every command marshalled back onto this one — WebAdminServer.
        if (_options.Web.Enabled)
        {
            _web = new WebAdminServer(this, _log, _webLog!, _options.Web);
            _web.Start();
        }

        // Last, and after OpenMap: the first segment's preamble is a
        // FULL_UPDATE of the world, so there has to be a world.
        if (_options.RecordingDirectory.Length != 0)
        {
            _recorder = new MatchRecorder(
                _options.RecordingDirectory, _options.ServerName, _log, JoinBurstBytes);

            _recorder.StartSegment(_currentMap);
            _log.Info($"recording to {_options.RecordingDirectory}");
        }
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
            // Cheap to leave unguarded because _lobby is null unless --lobby
            // was passed, and the modulo runs either way.
            if (_lobby is not null)
                AnnounceToLobby();

            _aprilFools = ChatBubbles.IsAprilFools(LocalClock());

            // On the same thirty-second cadence, because both are housekeeping
            // nothing is waiting on. A lapsed ban that lingers for half a minute
            // is not a bug; checking on every accept would be.
            if (_bans.Sweep(now) is var swept and > 0)
                _log.Info($"{swept} ban(s) expired");

            // ⚠️ Said once per episode, and it matters: a rate limiter whose
            // table is full has quietly stopped limiting, which is worse than
            // one that was never on because the configuration still says it is.
            // ConnectionRateLimiter explains why failing open is nonetheless the
            // right answer.
            if (_connectionRate?.ShouldWarnSaturated() == true)
            {
                _log.Warn(
                    $"the per-address connection limiter is tracking its maximum "
                    + $"{ConnectionRateLimiter.MaxTracked} addresses and is letting new ones through; "
                    + "this is a distributed connection flood and belongs in the firewall - docs/HARDENING.md");
            }
        }

        // GML's current_time, held still for the tick so two plugins asked in
        // the same tick agree.
        _pluginClock = Environment.TickCount64;

        ServiceClients();

        // Immediately after the client commands, because that is where the
        // reference's equivalent sits: RewardAuthChecker is an object created by
        // processClientCommands and its Step runs in the same frame's object
        // pass. It only ever appends to the broadcast, so it must be above the
        // flush at the end of this method.
        ServiceRewardAuth();

        // AFTER the tick's client commands, so a PLAYER_CHANGENAME that arrived
        // this tick is seen this tick. The reference runs plugin code in Player
        // Begin Step events, whose order against the server's own is a GM8
        // detail nothing observable turns on.
        _plugins.Tick();

        // Before ServiceBots, so a bot added this tick is polled this tick
        // rather than pressing nothing for one frame. GameServerBeginStep.gml
        // calls botPopulationUpdate ahead of the input pass for the same reason.
        _population?.Service();

        ServiceBots();

        // THE INPUT HAS TO REACH THE WORLD BEFORE THE UPDATE IS SERIALISED. The
        // broadcast reads _world.Characters[slot].KeyState; KeyState is written
        // only by CharacterState.ApplyInput, which runs inside
        // MatchSimulation.Tick — i.e. in AdvanceSimulation, BELOW this. Applying
        // input any later would make every INPUTSTATE and QUICK_UPDATE carry the
        // previous tick's keys.
        //
        // The reference writes keyState straight onto the character in
        // processClientCommands (GameServerBeginStep.gml:52) and serialises after
        // it, at line 66. This is that ordering.
        //
        // Clients dead-reckon each character from the last keys they heard until
        // the next position update, every 7th tick, so stale-by-one-tick keys
        // would mispredict every character for most of every gap, looking
        // exactly like lag while the server sits inside its budget. A jump is
        // the worst case: GG2 jumps on the PRESSED edge, so a late key can miss
        // the edge against the client's own previous keystate entirely and the
        // character then snaps upward on the next position update.
        // InputReachesTheWireTests is the gate, with a bot variant that has no
        // socket so the network cannot explain a failure.
        //
        // MatchSimulation.Tick applies the SAME inputs again below, and that is
        // deliberate rather than tolerated. The simulation must stay a pure
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

        // Freezing skips EXACTLY this call, and everything around it keeps
        // running. A freeze that skipped the whole tick would stop servicing
        // sockets, so a frozen server would silently drop every client it was
        // being frozen to inspect — and it would stop answering the control port
        // that froze it. Clients keep receiving updates of an unchanging world,
        // which is what "frozen" looks like from a client that re-simulates
        // between updates.
        if (advancing)
            AdvanceSimulation(live);

        // THE FLUSH BELONGS AFTER THE SIMULATION, not under the update.
        // GameServerEndStep.gml:7-8 writes global.sendBuffer to every socket and
        // clears it in the server's END Step, after everything that frame has
        // been appended — so the reference's packet for tick N holds the state
        // update AND that tick's events together. Flushing under
        // WriteStateUpdate instead would split them across two sends: the
        // update for tick N going out mid-tick, and everything AdvanceSimulation
        // appends — WEAPON_FIRE, PLAYER_DEATH, ubers, intel drops, generator
        // destruction, arena — shipping a tick late, and ahead of the update it
        // should follow.
        //
        // This changes only WHEN the bytes leave, never their order or content:
        // they are appended in the reference's order into the same buffer, which
        // is why a closed-loop replay cannot see it — the stream is identical.
        //
        // A client simulates every projectile itself from WEAPON_FIRE and the
        // shooter's keys; there is no projectile message. A late WEAPON_FIRE
        // starts the client's rocket a tick behind the server's.
        //
        // BEFORE AcceptConnections, deliberately. Flushing after it delivers
        // this tick's broadcast to a client that joined DURING this tick, ahead
        // of its own join burst — JoinAndBroadcastTests and FullUpdateLayoutTests
        // both catch that. The join sequence is a separate contract and this
        // change is not licensed to move it.
        FlushBroadcast();

        // AFTER THE FLUSH, and measured — see BroadcastEndStepEvents. The
        // reference's flush is in the GameServer's End Step, the FIRST End Step
        // of the frame, so a death, a sentry's destruction and the BALANCE a
        // death triggers all miss it and ship on the next tick. Appending them
        // above the flush would deliver every one a tick EARLY.
        if (advancing)
            BroadcastEndStepEvents(live);

        _control?.Service();

        // Not "service": the web admin's sockets are handled on its own threads
        // and this only drains what they queued, plus publishing the snapshot
        // they read. See WebAdminServer.Pump — a remote client that stops
        // reading must never be able to stall a tick, which is exactly what
        // servicing it here would allow.
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
        // The reference does a BLOCKING DNS lookup here, on purpose, so the
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
