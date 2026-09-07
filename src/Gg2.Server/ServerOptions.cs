using Gg2.Bots;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Server;

/// <summary>
/// How the server is configured. Defaults match the reference's <c>gg2.ini</c>
/// fallbacks in <c>Scripts/Game/game_init.gml:66-106</c>.
/// </summary>
public sealed class ServerOptions
{
    /// <summary>Shown in the client's server browser and on join.</summary>
    public string ServerName { get; set; } = "My Server";

    /// <summary>
    /// The name of the roster slot-0 entry — the reference's local player, which
    /// a headless server keeps as a permanent spectator.
    /// </summary>
    public string ServerPlayerName { get; set; } = "Server";

    /// <summary>Listen port. Zero means "any free port", which tests use.</summary>
    public int Port { get; set; } = GameConstants.DefaultPort;

    /// <summary>Empty means no password, and no <c>PASSWORD_REQUEST</c>.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Sent as a <c>MESSAGE_STRING</c> to each joiner. Empty means none.</summary>
    public string WelcomeMessage { get; set; } = string.Empty;

    /// <summary>
    /// Seats for network clients. The reference clamps this to 48; slot 0 and
    /// bots do not count against it (<c>getNumberOfOccupiedSlots</c>).
    /// </summary>
    public int PlayerLimit { get; set; } = 10;

    /// <summary>
    /// <c>ServerPluginList</c>: the server-sent plugins clients are asked to
    /// load, comma-separated. Empty means none, and the greeting then advertises
    /// an empty list.
    /// </summary>
    /// <remarks>
    /// Names are <c>[a-z0-9_]+</c> and the reference refuses a list over 254
    /// characters (<c>game_init.gml:99</c>); both are enforced when the list is
    /// parsed. An entry may be written <c>name@md5</c> to pin an exact zip —
    /// a port extension, not the reference's syntax, and
    /// <c>PluginHashes.Resolve</c> says when each source is used.
    /// <para>
    /// <b>A plugin this port has no C# implementation for is still
    /// advertised</b>, so its client half loads and its packets are discarded.
    /// See <c>ServerPluginCatalogue</c> for what is implemented; the loader logs
    /// a warning naming any plugin that is not.
    /// </para>
    /// </remarks>
    public string ServerPluginList { get; set; } = string.Empty;

    /// <summary>
    /// <c>ServerPluginsRequired</c>: whether a client must load the plugins to
    /// play here.
    /// </summary>
    /// <remarks>
    /// One byte of the greeting, and the client's whole use of it is which
    /// dialog it shows (<c>ClientBeginStep.gml:62-121</c>): required offers
    /// "Download and join" or "Disconnect", optional offers "Download" or
    /// "Skip". <b>The server does not enforce it</b> — a client that skipped
    /// an optional plugin is indistinguishable from one that has no plugin
    /// support at all, and the reference does not check either. Requiring a
    /// plugin whose server half sends unprompted packets is therefore the only
    /// way to be sure everyone can read them.
    /// </remarks>
    public bool ServerPluginsRequired { get; set; }

    /// <summary>
    /// The maps a plugin's map vote may offer. Empty falls back to the map
    /// rotation, and then to every map in <see cref="MapDirectory"/>.
    /// </summary>
    /// <remarks>
    /// The reference's <c>votemap</c> plugin reads a <c>votableMaps.txt</c> and
    /// falls back to <c>global.map_rotation</c>. <b>The third fallback is
    /// this port's</b>: with no rotation configured — a single-map server, which
    /// is the default — the reference's votemap can only answer "No maps were
    /// found!", so the feature is off exactly where it would be most used.
    /// </remarks>
    public IReadOnlyList<string> VotableMaps { get; set; } = [];


    /// <summary>
    /// Connections allowed from one address. Not enforced for loopback, which is
    /// how the reference lets a host run clients beside its own server.
    /// </summary>
    public int MultiClientLimit { get; set; } = 3;

    /// <summary>
    /// What one socket may make the server hold and do.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>These are the bounds that make the server safe to expose to the
    /// internet, and the defaults are the supported configuration.</b> Every
    /// field is orders of magnitude above what a stock client uses — see
    /// <see cref="ConnectionLimits"/>, which states the ratio for each — so
    /// raising one is not a way to be more generous to real players, it is a way
    /// to give an attacker more room. Lower them on a machine with less memory
    /// than a bounded worst case needs; do not raise them because a client hit
    /// one, until you have established which client and why.
    /// </remarks>
    public ConnectionLimits Limits { get; set; } = ConnectionLimits.Default;

    /// <summary>
    /// A file to tee the log into, or empty for none.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Not needed under systemd or Docker</b>, which capture stdout and
    /// stderr, rotate on their own, and let an operator filter by stream —
    /// which is what the log levels are for. This is for a deployment with
    /// neither, where the alternative is no log at all once the terminal
    /// scrolls. It <em>tees</em>: the console sink stays wired, because the
    /// console is the record an operator has when the thing that has broken is
    /// the disk. <see cref="FileLog"/>.
    /// </remarks>
    public string LogFile { get; set; } = string.Empty;

    /// <summary>
    /// Where the ban list is kept, or empty to hold bans in memory only.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Empty means bans do not survive a restart</b>, and a 24/7 server
    /// restarts — for an update, for a crash, for a reboot. The server says so
    /// the first time a ban is added without one, rather than letting an
    /// operator find out by having the same person come back.
    /// <para>
    /// ⛔ <b>An address is not a person.</b> Before using this at all, read
    /// <see cref="BanList"/>: the protocol has no durable identity, so a ban
    /// keys on the address and is wrong in both directions — it catches
    /// everybody behind a shared one and nobody whose address changes.
    /// </para>
    /// </remarks>
    public string BanFile { get; set; } = string.Empty;

    /// <summary>Size at which <see cref="LogFile"/> is rotated.</summary>
    public long LogMaxBytes { get; set; } = FileLog.DefaultMaxBytes;

    /// <summary>
    /// Rotated log files kept beside the live one. Zero rotates by deleting.
    /// </summary>
    public int LogKeep { get; set; } = FileLog.DefaultKeep;

    /// <summary>
    /// Which built-in map to serve first. <b>Empty means "the first entry of
    /// <see cref="MapRotation"/> after the shuffle"</b>, and falls back to
    /// <c>ctf_truefort</c> when the rotation is empty too.
    /// </summary>
    /// <remarks>
    /// The empty case is <c>global.launchMap</c>: <c>game_init.gml:377-380</c>
    /// makes <c>-map X</c> a rotation of exactly one, and
    /// <c>GameServerCreate.gml:110-113</c> opens on <c>nextMapInRotation()</c>
    /// only when no map was named. A pinned map here does NOT collapse the
    /// rotation, which is the one place this diverges on purpose — a headless
    /// server wants "start here, then rotate", and <c>MapRotationTests</c> gates
    /// it.
    /// </remarks>
    public string Map { get; set; } = DefaultMap;

    /// <summary>
    /// The map a server with no preference at all opens on — the reference's own
    /// first entry in <c>[Maps]</c>.
    /// </summary>
    public const string DefaultMap = "ctf_truefort";

    /// <summary>
    /// The map rotation, in order. Empty means "just <see cref="Map"/>", which
    /// is a rotation of one and replays the same map for ever.
    /// </summary>
    /// <remarks>
    /// <c>global.map_rotation</c>, walked by <c>nextMapInRotation.gml</c>: the
    /// next *loadable* entry after the current index, wrapping. The reference
    /// errors out when no entry is loadable; here the current map is the
    /// fallback, because a headless server refusing to start a new round is
    /// worse than one that repeats a map.
    /// </remarks>
    public IReadOnlyList<string> MapRotation { get; set; } = [];

    public byte CapLimit { get; set; } = 5;

    public byte TimeLimitMinutes { get; set; } = 15;

    public byte RespawnTimeSeconds { get; set; } = 5;

    public ushort KillLimit { get; set; } = 30;

    public int TdmInvulnerabilitySeconds { get; set; } = 5;

    /// <summary>
    /// The <c>[Classlimits]</c> section: how many of each class one team may
    /// hold, indexed by <see cref="PlayerClass"/>. 255 means no limit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ten independent values, as <c>readClasslimitsFromIni.gml</c> reads them.
    /// <see cref="ClassLimitsBy"/> builds one from names.
    /// </para>
    /// <para>
    /// <b>Zero means the class is FULL, not unlimited.</b> A defaulted
    /// <c>new byte[10]</c> bans every class, and the simulation's answer to that
    /// is to hand out the requested class anyway — see <c>Classlimits.Pick</c>'s
    /// last line. Use <see cref="NoClassLimits"/> rather than an array literal.
    /// </para>
    /// </remarks>
    public byte[] ClassLimits { get; set; } = NoClassLimits();

    /// <summary>Ten 255s: the reference's own default, meaning "no limit".</summary>
    public static byte[] NoClassLimits()
    {
        var limits = new byte[TeamBalance.ClassCount];
        Array.Fill(limits, (byte)255);
        return limits;
    }

    /// <summary>
    /// <see cref="ClassLimits"/> with every limit that cannot bind relaxed to
    /// 255 — the array both the enforcement and the wire must see.
    /// </summary>
    /// <remarks>
    /// <c>GameServerCreate.gml:42-46</c>, <c>if (global.classlimits[a] &gt;=
    /// global.playerLimit) global.classlimits[a] = 255;</c>. It mutates
    /// <c>global.classlimits</c> in place, before the first
    /// <c>serializeState</c>, so the reference's enforcement and the
    /// <c>FULL_UPDATE</c> it broadcasts are the same clamped numbers.
    /// <para>
    /// <b>Both callers must use this, not <see cref="ClassLimits"/>.</b>
    /// Clamping in one place only would put a limit on the client's HUD that the
    /// server does not apply, or apply one the client does not show — and
    /// neither shows up as an error anywhere.
    /// </para>
    /// </remarks>
    public byte[] EffectiveClassLimits()
    {
        var limits = (byte[])ClassLimits.Clone();

        for (var i = 0; i < limits.Length; i++)
        {
            if (limits[i] >= PlayerLimit)
                limits[i] = 255;
        }

        return limits;
    }

    /// <summary>
    /// Ten limits with the named classes overridden, the rest unlimited.
    /// </summary>
    public static byte[] ClassLimitsBy(params (PlayerClass Class, byte Limit)[] overrides)
    {
        var limits = NoClassLimits();

        foreach (var (playerClass, limit) in overrides)
            limits[(int)playerClass] = limit;

        return limits;
    }

    /// <summary>
    /// How <see cref="MapRotation"/> is ordered at startup.
    /// </summary>
    /// <remarks>
    /// <c>global.shuffleRotation</c>, applied once by <see cref="GameServer.Start"/>
    /// — <c>GameServerCreate.gml:92-108</c>, which is where the reference does it
    /// too. It draws from <see cref="System.Random.Shared"/> and <b>never</b>
    /// from <c>WorldState.Random</c>: that stream is observable simulation and
    /// every client re-derives weapon spreads from its position.
    /// </remarks>
    public ShuffleMode ShuffleRotation { get; set; } = ShuffleMode.None;

    /// <summary>
    /// <c>global.autobalance</c>: move a player across when one team is two
    /// larger. On by default, as the reference's ini is.
    /// </summary>
    public bool Autobalance { get; set; } = true;

    /// <summary>
    /// Announce this server to the public lobby at
    /// <c>ganggarrison.com:29944</c>, so it appears in the client's server list.
    /// </summary>
    /// <remarks>
    /// <b>OFF by default, and the default is the point.</b> The reference's
    /// ini has <c>UseLobby</c> on — because a person ticked a box in a hosting
    /// menu to get there. A headless binary has no such moment, so
    /// <c>docs/PROTOCOL.md</c> asks for a flag that defaults off: running
    /// <c>gg2-server</c> must not publish an address to a third party.
    /// <para>
    /// It is the only outbound connection this project makes. Turning it on
    /// sends a UDP datagram every 30 seconds, plus one on each map change, each
    /// join, and each leave that un-fills a full server, plus an unregister at
    /// shutdown.
    /// </para>
    /// </remarks>
    public bool AnnounceToLobby { get; set; }

    /// <summary>
    /// Whether to answer <c>REWARD_REQUEST</c> and relay reward auth.
    /// </summary>
    /// <remarks>
    /// <b>OFF by default, and this one is a deliberate departure from the
    /// reference rather than a transcription of it.</b>
    /// <c>processClientCommands.gml:320-325</c> answers every request
    /// unconditionally; there is no server-side switch in GG2 at all. The switch
    /// exists here because the handshake commits the server to opening outbound
    /// TCP connections to a third party <em>carrying its players' user ids</em>,
    /// and a headless binary has no hosting menu in which somebody agreed to
    /// that.
    /// <para>
    /// The cost of leaving it off is exactly that nobody's badges, hats or capes
    /// render. Nothing else: rewards are not observable simulation, and the
    /// client neither retries nor blocks when a request goes unanswered.
    /// </para>
    /// </remarks>
    public bool Rewards { get; set; }

    /// <summary>The auth server's host. <c>AUTH_SERVER_HOST</c>.</summary>
    /// <remarks>
    /// Overridable so an operator can point this at their own auth service
    /// rather than at ganggarrison.com — which is also the only way the flow can
    /// be exercised end to end, since the real service must not be contacted
    /// from this repo's tests.
    /// </remarks>
    public string AuthServerHost { get; set; } = GameConstants.AuthServerHost;

    /// <summary>The auth server's port. <c>AUTH_SERVER_PORT</c>.</summary>
    public int AuthServerPort { get; set; } = GameConstants.AuthServerPort;

    /// <summary>
    /// Total bandwidth across all in-flight custom-map downloads, bytes/second.
    /// </summary>
    /// <remarks>
    /// <c>game_init.gml:94</c>, "Total bandwidth limit for map downloads in bytes
    /// per second", default 50000. <b>Total, not per client</b> — a second
    /// downloader halves the first one's rate rather than doubling the server's
    /// outgoing traffic.
    /// </remarks>
    public int MapDownloadBytesPerSecond { get; set; } = MapDownload.DefaultBytesPerSecond;

    /// <summary>
    /// Where the built-in map PNGs live — the reference checkout's
    /// <c>Source/gg2/Included Files</c>.
    /// </summary>
    public string MapDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Where the bot nav caches live — this repo's checked-in <c>nav/</c>.
    /// </summary>
    /// <remarks>
    /// Empty, and a missing cache, are both fine and mean different things:
    /// empty is "this server has no graph directory", a miss is "this map has no
    /// graph in it". Neither is an error, because a server with an ungenerated map
    /// in its rotation should keep serving it — <c>navCacheLoad.gml</c>'s own
    /// contract.
    /// <para>
    /// ✅ <b>And neither now means bots that do not navigate</b>, because
    /// <see cref="NavGenerate"/> builds the missing graph. What this directory
    /// decides is whether a graph is <em>read</em> rather than built, and whether
    /// a built one has anywhere to be saved.
    /// </para>
    /// </remarks>
    public string NavCacheDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Build a nav graph on the fly for a map that has none, instead of serving
    /// it with bots that do not navigate.
    /// </summary>
    /// <remarks>
    /// ✅ <b>On by default, and it is what makes a CUSTOM map navigable.</b> The
    /// 25 checked-in graphs cover the shipped maps; anything else — a map an
    /// operator added, a stage of a multi-area map nobody generated, a map edited
    /// since its graph was built — had no graph and therefore no bot movement, and
    /// that reads exactly like a broken bot layer rather than like missing data.
    /// Everything a graph needs is in the map's own PNG, so there was never a
    /// reason for it to be missing data.
    /// <para>
    /// ⚠️ <b>It runs on the tick thread at the map change, and it is not free</b> —
    /// a large map is a few hundred milliseconds. A map change already stalls (the
    /// clients are loading, and nothing is being simulated), so the cost lands
    /// where there is nothing to disturb; <c>BotPopulationService.MapChanged</c>
    /// logs how long it took, so an operator who cares can see it rather than
    /// guess. Turn it off with <c>--no-navgen</c> and the old behaviour — bots that
    /// fight whatever reaches them and walk nowhere — comes back.
    /// </para>
    /// <para>
    /// ⚠️ <b>A generated graph is held in memory and not written anywhere</b>
    /// unless <see cref="NavGenerateWrite"/> says so. A server writing files into
    /// its own install directory as a side effect of a map rotation is a surprise;
    /// paying a few hundred milliseconds once per map change is not.
    /// </para>
    /// </remarks>
    public bool NavGenerate { get; set; } = true;

    /// <summary>
    /// Save a graph built by <see cref="NavGenerate"/> into
    /// <see cref="NavCacheDirectory"/>, so the next map change reads it instead of
    /// rebuilding it.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Off by default because it writes to disk and the operator did not ask
    /// it to.</b> Worth turning on for a server whose rotation is mostly custom
    /// maps, where the alternative is rebuilding the same graphs every rotation.
    /// The manifest is written with it — a graph with no manifest line is a graph
    /// nothing checks against its map afterwards.
    /// </remarks>
    public bool NavGenerateWrite { get; set; }

    /// <summary>
    /// Give A\* exact node-to-node distances in place of its straight-line
    /// estimate — <c>--no-nav-distances</c> turns it off.
    /// </summary>
    /// <remarks>
    /// <b>A\* is the most expensive thing this server does</b> — about 30% of a
    /// twelve-bot tick, at 320 searches per 1000 ticks — and an exact heuristic
    /// measured <b>1.36x</b> on it, which is roughly 8% of the tick. That is the
    /// whole of the upside.
    /// <para>
    /// ⚠️ <b>ON by default since 2026-09-04, and it DECIDES EVERY ROUTE.</b>
    /// <see cref="NavConstants.SearchWeight"/> makes this a <em>weighted</em>
    /// A\*: the heuristic is inflated on purpose, so it decides which route is
    /// returned and not merely how quickly. With the straight-line estimate the
    /// inflated heuristic still sits at about 0.914 of the true remaining cost;
    /// with an exact one it sits at the weight itself. Routes therefore move,
    /// and a run with this on is <b>not comparable</b> to a baseline with it off
    /// — it is a property of the arm and has to be recorded beside the numbers.
    /// <c>docs/TRAPS-BOTS.md</c>, "Shipping the precomputed distances".
    /// </para>
    /// <para>
    /// ⚠️ <b>The tables are READ from <c>&lt;key&gt;.dist.gz</c> beside the
    /// graph, not derived</b> — <see cref="NavDistanceFile"/>, written by
    /// <c>gg2-server navgen</c>. A map with no file (a custom one, whose graph
    /// was itself built at the map change) still works: the set is derived
    /// instead, at one reverse Dijkstra per node per distinct ability profile,
    /// which is 3-251 ms on the shipped graphs. The elapsed time, the size and
    /// which of the two happened are logged.
    /// </para>
    /// </remarks>
    public bool NavDistances { get; set; } = true;

    /// <summary>
    /// Whether bots price an edge their team-mates are already walking above its
    /// honest cost, so a squad fans out instead of filing down one corridor —
    /// <c>--no-bot-occupancy</c> turns it off.
    /// </summary>
    /// <remarks>
    /// <b>ON by default, because it is the largest single source of route
    /// variety the bot layer has</b> and turning it off is a visible behaviour
    /// change rather than a tuning knob: measured over 21 maps, match dispersion
    /// falls from <b>0.785 to 0.634</b> without it, and nothing replaces it —
    /// static lane fields buy 0.019 of spread per point of route inflation
    /// against occupancy's 0.066, and widening <c>BotRoles.SpreadX</c> saturates
    /// almost immediately. <c>docs/TRAPS-BOTS.md</c>, "Route variety, scored".
    /// <para>
    /// ⚠️ <b>It is also the one thing that makes the live server's routing differ
    /// from <c>eval</c>'s and from every training rollout</b>, which have never
    /// had it — <c>ScriptedBot.Occupancy</c> was set in exactly one place before
    /// this flag existed. Turning it off makes the three agree, which is worth
    /// something for training transfer and costs the dispersion above.
    /// </para>
    /// <para>
    /// ⚠️ <b>It is not a performance knob</b>, whatever the search numbers
    /// suggest. A\* is ~26-30% of a twelve-bot tick and that whole tick is
    /// 0.151 ms against a 33.3 ms budget, so what this changes about search cost
    /// is a fraction of a percent of the frame. Choose it on how the bots should
    /// play, not on the clock.
    /// </para>
    /// </remarks>
    public bool BotOccupancy { get; set; } = true;

    /// <summary>
    /// How fast <see cref="GameServer.Run"/> paces the tick loop, relative to
    /// real time. 1.0 is the reference's 30 Hz; 4.0 runs four minutes of match
    /// per minute; <see cref="double.PositiveInfinity"/> is <b>unpaced</b> — the
    /// loop never sleeps and runs as fast as the machine allows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Phase 8's cheapest item and, per <c>docs/PLAN.md</c>, its biggest
    /// iteration win: nothing about the simulation is rate-dependent, because
    /// <c>MatchSimulation.Tick</c> takes no clock and every duration in the game
    /// is counted in ticks rather than seconds. So pacing is presentation, and
    /// removing it changes throughput and nothing else.
    /// </para>
    /// <para>
    /// <b>Infinity rather than 0 for "as fast as possible", deliberately.</b>
    /// This codebase already has three fields where 0 means "no limit"
    /// (<c>RespawnTime</c>, <c>TimeLimitMinutes</c>, <c>CapLimit</c>) and every
    /// one of them is a documented trap. Here the arithmetic says it instead:
    /// the interval is <c>tickLength / SpeedMultiplier</c>, and that is exactly
    /// zero at infinity. A 0 would read as "stopped", which is a mode this
    /// does not have — freezing is the control port's, not the pacer's.
    /// </para>
    /// <para>
    /// <b>This does not make the server faster; it makes it not wait.</b> A
    /// live server with clients on it must stay at 1.0 — clients pace
    /// themselves at 30 Hz and re-simulate between updates, so an unpaced server
    /// desynchronises every one of them immediately. It is for a headless
    /// server nobody is connected to.
    /// </para>
    /// </remarks>
    public double SpeedMultiplier { get; set; } = 1.0;

    /// <summary>
    /// Where to write replayable <c>.trace</c> recordings of this server's own
    /// broadcast stream. Empty — the default — records nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>docs/BOTAI.md</c> item 3, <b>the only item on this project with a
    /// deadline</b>: "data not collected during the port's first public games
    /// does not exist afterwards". The broadcast stream already carries every
    /// player's keys and aim every tick, so a recording of a game with people in
    /// it is a labelled dataset of human action arriving for free — and it is
    /// the only source of human-<em>like</em> behaviour rather than
    /// optimal-looking behaviour.
    /// </para>
    /// <para>
    /// <b>Off by default and it should stay that way.</b> Recording a game
    /// people are playing is a thing to decide rather than a thing to inherit
    /// from a default, and the data is only worth having if somebody meant to
    /// collect it. See <see cref="MatchRecorder"/> for the on-disk bound.
    /// </para>
    /// </remarks>
    public string RecordingDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Forces the simulation's random stream, instead of seeding it from the
    /// clock. Null — the default — is the live behaviour.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>For a TEST or a rollout that has to be reproducible, and for nothing
    /// else.</b> <c>GameServer.Start</c> otherwise seeds <c>WorldState.Random</c>
    /// from an unseeded <c>Random</c>, which is the reference's <c>randomize()</c>
    /// and is right for a live server: without it every server on a machine would
    /// scatter its first blast identically.
    /// <para>
    /// ⚠️ <b>What it is for.</b> A test that runs a bot match and then asserts on a
    /// TAIL statistic of it — <c>InputLagSweepTests.ThisServersOwnRecordingIsInPhase</c>
    /// takes a 99th-percentile trajectory error — is measuring a fresh random
    /// sample on every run, so it fails intermittently and reproduces on nobody's
    /// machine. Seeding it makes the gate answer the same question every time:
    /// if it fails it fails always, which is the only kind of failure anybody
    /// investigates.
    /// </para>
    /// <para>
    /// ⛔ <b>It does not make the seed a free choice.</b> A seed picked because it
    /// passes is a gate that proves nothing; the seed here has to be one whose
    /// curve is healthy for the reason the test states, and the vacuity half of
    /// that test is what keeps it honest.
    /// </para>
    /// </remarks>
    public int? RandomSeed { get; set; }

    /// <summary>
    /// The loopback control port, when <see cref="ControlPortEnabled"/> is set.
    /// Zero asks the OS for a free port, which is what tests want.
    /// </summary>
    public int ControlPort { get; set; }

    /// <summary>
    /// Whether to open the control port at all. <b>Off by default.</b>
    /// </summary>
    /// <remarks>
    /// <b>A separate flag rather than "a non-zero port means on", because
    /// zero is a port people want</b> — it is how every test asks for an
    /// ephemeral one (<c>ServerHarness.cs</c> does exactly that for the game
    /// port). Overloading zero as "off" would make the one configuration a test
    /// needs the one it cannot express.
    /// </remarks>
    public bool ControlPortEnabled { get; set; }

    /// <summary>
    /// The <c>[Web]</c> section: the remote admin panel. <b>Off by default.</b>
    /// </summary>
    /// <remarks>
    /// <see cref="WebAdminOptions.Disabled"/>, never <c>new()</c> — a zeroed
    /// one has a null bind address and throws at startup rather than at
    /// construction. Same trap as <see cref="Bots"/>.
    /// </remarks>
    public WebAdminOptions Web { get; set; } = WebAdminOptions.Disabled;

    /// <summary>
    /// Logs a histogram of tick intervals every 30 seconds and at shutdown.
    /// </summary>
    /// <remarks>
    /// <b>Pacing only — it cannot touch the simulation</b>, so unlike the
    /// other switches this one is safe on a server with people on it, which is
    /// the only place the interesting measurement exists. Off by default
    /// because a healthy server's report is a line of noise every 30 seconds.
    /// </remarks>
    public bool TickStats { get; set; }

    /// <summary>
    /// Whether bots run their expensive micro-controller against each other, or
    /// only against human players.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>False by default, which is NOT the library default.</b>
    /// <c>BotBrainSpec.Engage</c> defaults to <c>Anyone</c> so that a headless
    /// bot-vs-bot match still exercises the controller — with no humans in it,
    /// <c>HumansOnly</c> would make every arm measurement silently measure the
    /// all-scripted control. A live server wants the opposite: the search and
    /// policy controllers exist to be good against people, and a 4,800 µs search
    /// decision spent on two bots shooting at each other in an empty corner is
    /// most of what a populated server pays for.
    /// </para>
    /// <para>
    /// <b>Turning this on does not make the bots better.</b> It makes
    /// bot-vs-bot fights cheaper and slightly worse. Any measurement of a
    /// controller taken with this true and no humans present is measuring the
    /// scripted layer, whatever <c>--bot-mode</c> said.
    /// </para>
    /// </remarks>
    public bool BotEngagesBotsToo { get; set; }

    /// <summary>
    /// Drop to a slow tick when nobody is connected and no bots are running.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An empty server has no characters, no clients to broadcast to and nobody
    /// to notice the difference, so 30 wakeups a second buys nothing. On
    /// <see cref="IdleTickRate"/> it costs a fifteenth of that. The moment a
    /// connection arrives — or a bot is added over the control port — it returns
    /// to full rate, and <c>GameServer.Run</c>'s existing speed-change rebase
    /// handles the transition in both directions.
    /// </para>
    /// <para>
    /// <b>The round clock slows with it.</b> Every duration in the game is
    /// counted in ticks, so an idle server's round advances at a fifteenth of
    /// real time. That is a feature rather than a cost — a server left up
    /// overnight should not greet its first player with twelve seconds left —
    /// but it does mean an empty server's wall-clock round length is not
    /// <c>--time-limit</c>.
    /// </para>
    /// <para>
    /// <b>An explicit <c>--speed</c> wins.</b> Idling is skipped entirely when
    /// the server is unpaced, because <c>--speed max</c> is a direct instruction
    /// about the tick rate and this must not quietly override it.
    /// </para>
    /// <para>
    /// <b>Run() only — <c>GameServer.Tick</c> is untouched.</b> Every test and
    /// the control port drive ticks themselves, and a Tick that no-opped on an
    /// empty server would break all of them for no saving: the cost this removes
    /// is the wakeup, and a caller driving ticks by hand has already paid it.
    /// </para>
    /// </remarks>
    public bool IdleWhenEmpty { get; set; } = true;

    /// <summary>Ticks per second while idle. A fifteenth of the live rate.</summary>
    /// <remarks>
    /// <b>Two rather than one, so a joiner is noticed within half a second.</b>
    /// The listener is only polled on a tick, so this is also the worst-case
    /// delay before a connecting client's handshake starts — after which the
    /// server is no longer idle and runs at full rate for the rest of it.
    /// </remarks>
    public const int IdleTickRate = 2;

    /// <summary>
    /// Let the server discover how much bot-controller work this machine can
    /// afford, instead of trusting a constant measured on another one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The reason it exists.</b> <c>SearchController</c>'s reference cost was
    /// measured on one machine, on one map, in Release. A rented VPS makes every
    /// decision cost more and no compiled-in constant can know that. The arbiter
    /// never uses the absolute scale — only <c>cost &lt;= budget - spent</c> — so
    /// the answer is a budget that calibrates itself rather than a better guess.
    /// <c>MicroBudgetController</c> does the walking.
    /// </para>
    /// <para>
    /// <b><c>--bot-arbiter</c> stays an upper bound.</b> This only ever holds
    /// the budget at or below the configured number.
    /// </para>
    /// <para>
    /// <b>On for the live server, and structurally absent everywhere a
    /// measurement is taken.</b> It lives in <see cref="GameServer.Run"/>, which
    /// <c>HeadlessMatch</c>, the env and <c>eval</c> never call — so a benchmark
    /// cannot pick it up by accident, and the thread-invariance pins keep meaning
    /// what they say. Bots get quietly worse under load, which is the point and
    /// is also why every move it makes is logged.
    /// </para>
    /// </remarks>
    public bool AdaptiveBotBudget { get; set; } = true;

    /// <summary>The <c>[Bots]</c> section. Off by default, as in the reference.</summary>
    /// <remarks>
    /// <see cref="BotPopulationOptions.Default"/>, never <c>new()</c> — see the
    /// warning there. A zeroed one has a fill target of 0, a difficulty of 0 and a
    /// null name prefix, and every one of those is silent.
    /// </remarks>
    public BotPopulationOptions Bots { get; set; } = BotPopulationOptions.Default;

    /// <summary>
    /// The <c>--config</c> file these options were read from, or empty when none
    /// was given.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Remembered rather than consumed, and that is what makes the panel's
    /// settings page possible</b>: a change an operator makes in the browser is
    /// written back here and re-read by the next start, so it survives the
    /// restart that applies it. With no path there is nowhere to write, and
    /// <c>ControlCommands</c>'s <c>config-set</c> refuses by name rather than
    /// silently keeping the change in memory until the process ends.
    /// </para>
    /// <para>
    /// ⛔ <b>Not itself readable from a config file.</b> A file naming another
    /// file is a redirect with a cycle in it and no rule about which wins;
    /// <c>ConfigFileCoverageTests.NotInTheConfigFile</c> records the decision.
    /// </para>
    /// </remarks>
    public string ConfigFile { get; set; } = string.Empty;

    /// <summary>
    /// Which of the four shipped bot brains this server runs — <c>--bot-mode</c>.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>An INPUT to <see cref="BotPopulationOptions.Brain"/>, not a copy of
    /// it.</b> <see cref="BotBrain.Build"/> is the one place these five inputs
    /// become a <see cref="BotBrainSpec"/>, and it is called once at the end of
    /// parsing — so setting this afterwards changes nothing, which is why nothing
    /// outside the two parsers writes it.
    /// <para>
    /// It is an option rather than a local in <c>CommandLine</c> because the
    /// brain cannot be written back to a config file: a <see cref="BotBrainSpec"/>
    /// holds a loaded <c>PolicyNetwork</c>, megabytes of weights with no textual
    /// form. The inputs have one, so the inputs are what round-trips.
    /// </para>
    /// </remarks>
    public BotMode BotMode { get; set; } = BotMode.Scripted;

    /// <summary>
    /// The <c>gg2-rl</c> checkpoint a policy-driven mode loads — <c>--policy</c>.
    /// Empty is the all-scripted arm.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A policy checkpoint is DATED by the simulation it was trained
    /// against</b> — <c>docs/BOTS.md</c>, "a policy is DATED by the physics".
    /// </remarks>
    public string PolicyPath { get; set; } = string.Empty;

    /// <summary>
    /// ⛔ Load a checkpoint whose <c>SIM_UUID</c> does not match this build —
    /// <c>--policy-ignore-stamp</c>.
    /// </summary>
    public bool PolicyIgnoreStamp { get; set; }

    /// <summary>
    /// Microseconds of bot-controller work per tick — <c>--bot-arbiter</c>. Null
    /// lets <see cref="BotModes.NeedsArbiter"/> supply the default.
    /// </summary>
    public int? BotArbiterMicroseconds { get; set; }

    /// <summary>
    /// Threads the search controller's rollouts use — <c>--search-threads</c>.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Threads change NO decision</b> — <c>RolloutThreadingTests</c> gates
    /// that at 2, 4 and 8 — so this is a pure cost knob.
    /// </remarks>
    public int SearchThreads { get; set; } = BotBrain.DefaultSearchThreads;

    /// <summary>
    /// The <see cref="ServerSetting.Id"/>s the command line set to something the
    /// config file does not say, so an editor can warn that saving them will not
    /// take.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>DERIVED, not configured</b> — which is why both coverage gates name
    /// it as deliberately absent from their tables rather than growing a row for
    /// it. <c>CommandLine.Parse</c> computes it by diffing the options it built
    /// against the ones the config file alone would have produced.
    /// </para>
    /// <para>
    /// <b>The failure it prevents is a settings page that appears to work.</b> A
    /// flag always beats the file — deliberately, and
    /// <c>CliCoverageTests.AFlagBeatsTheConfigFileInEitherOrder</c> pins it — so a
    /// server started with <c>--players 24</c> will still have 24 seats after an
    /// operator edits <c>PlayerLimit</c> in the panel, saves, and restarts. The
    /// value is written, the file is correct, and nothing happens. Naming the
    /// setting is the difference between that and a mystery.
    /// </para>
    /// <para>
    /// ⛔ <b>Setting IDs only, never values.</b> This is read by the admin panel;
    /// <c>--web-password</c> is on the command line and
    /// <c>SystemNeverLeaksTheCommandLineOrTheEnvironment</c> is the rule it must
    /// not break. <c>Web.Password</c> as a name is not a secret.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> CommandLineOverrides { get; set; } = [];

    /// <summary>
    /// A fresh options object carrying this codebase's defaults, including the
    /// two that are resolved from disk rather than written down.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Use this, never <c>new ServerOptions()</c>, wherever a config file is
    /// about to be applied over the result.</b> A bare one has an empty
    /// <see cref="MapDirectory"/>, which <see cref="Validate"/> refuses — so the
    /// panel's own "read the file, apply the change, check it, save it" path
    /// would fail on a server that is running perfectly. The checked-in
    /// <c>maps/</c> and <c>nav/</c> are what these two resolve to in a clone.
    /// </remarks>
    public static ServerOptions CreateDefault() => new()
    {
        MapDirectory = Server.MapDirectory.Resolve(),
        NavCacheDirectory = Server.MapDirectory.ResolveNav(),
    };

    public void Validate()
    {
        // First, because it is the one that can be reachable from the network:
        // a configuration error here is the one worth failing fast on.
        Web.Validate();

        if (Port is < 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(Port), Port, "port must be 0..65535");

        // Parsed here and thrown away: the point is that an unusable list fails
        // the server's start, not a client's join. GameServerCreate.gml:131-142
        // reaches game_end() for the same reason — a server advertising a list
        // it cannot serve strands every client that believes it.
        Plugins.PluginNames.ParseList(ServerPluginList);

        // The reference clamps at 48 (game_init.gml:152). Beyond 255 the roster
        // index stops fitting in the ubyte every message uses to name a player.
        Limits.Validate();

        if (LogMaxBytes < 1024)
        {
            throw new ArgumentOutOfRangeException(
                nameof(LogMaxBytes), LogMaxBytes, "must be at least 1024 bytes; a file that rotates every line is not a log");
        }

        if (LogKeep is < 0 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(LogKeep), LogKeep, "must be 0..1000");

        if (PlayerLimit is < 1 or > 48)
            throw new ArgumentOutOfRangeException(nameof(PlayerLimit), PlayerLimit, "player limit must be 1..48");

        // Length, not content: a short array would index out of bounds deep in
        // GameServer.ApplyOptions, and a long one would silently drop the tail.
        // Zeroes are legal here and mean "this class is full" — see ClassLimits.
        if (ClassLimits.Length != TeamBalance.ClassCount)
        {
            throw new ArgumentException(
                $"{nameof(ClassLimits)} must have exactly {TeamBalance.ClassCount} entries, "
                + $"one per class, and has {ClassLimits.Length}. "
                + $"Use {nameof(ServerOptions)}.{nameof(NoClassLimits)}() for the default.",
                nameof(ClassLimits));
        }

        if (MapDirectory.Length == 0)
        {
            // Reachable only if the checked-in maps/ is missing AND nothing
            // was passed, which means a partial checkout rather than a mistake
            // the caller made. Say where it looked. See MapDirectory.Resolve.
            throw new InvalidOperationException(
                $"{nameof(MapDirectory)} is empty: no maps/ beside the executable or above it, "
                + "no GG2_MAPS, no GG2_REFERENCE. The built-in maps are checked in at maps/ "
                + "in this repository; pass --maps to serve a different set.");
        }

        // Positive infinity is the one non-finite value that means something
        // here (unpaced). NaN and anything <= 0 are refused rather than
        // silently producing a negative or NaN wait, which WaitOne throws on
        // deep inside the loop rather than at startup.
        if (double.IsNaN(SpeedMultiplier) || SpeedMultiplier <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(SpeedMultiplier),
                SpeedMultiplier,
                "speed must be greater than zero, or infinity for unpaced");
        }
    }
}
