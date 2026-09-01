using Gg2.Bots;
using Gg2.Protocol;

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
    /// Connections allowed from one address. Not enforced for loopback, which is
    /// how the reference lets a host run clients beside its own server.
    /// </summary>
    public int MultiClientLimit { get; set; } = 3;

    /// <summary>Which built-in map to serve first.</summary>
    public string Map { get; set; } = "ctf_truefort";

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

    /// <summary>Per-class limit applied to all ten classes. 255 means no limit.</summary>
    public byte ClassLimit { get; set; } = 255;

    /// <summary>
    /// <c>global.autobalance</c>: move a player across when one team is two
    /// larger. On by default, as the reference's ini is.
    /// </summary>
    public bool Autobalance { get; set; } = true;

    /// <summary>
    /// Where the built-in map PNGs live — the reference checkout's
    /// <c>Source/gg2/Included Files</c>.
    /// </summary>
    public string MapDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Where the bot nav caches live — <c>gg2-nav-gen</c>'s output, kept in this
    /// repo's gitignored <c>nav/</c>.
    /// </summary>
    /// <remarks>
    /// ⚠️ Empty, and a missing cache, are both fine and mean different things:
    /// empty is "this server does not navigate", a miss is "this map has no cache
    /// generated". Neither is an error, because a server with an ungenerated map
    /// in its rotation should keep serving it — <c>navCacheLoad.gml</c>'s own
    /// contract. The directory is gitignored in the reference and is not shipped,
    /// so a fresh checkout has none.
    /// </remarks>
    public string NavCacheDirectory { get; set; } = string.Empty;

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
    /// ⚠️ <b>Infinity rather than 0 for "as fast as possible", deliberately.</b>
    /// This codebase already has three fields where 0 means "no limit"
    /// (<c>RespawnTime</c>, <c>TimeLimitMinutes</c>, <c>CapLimit</c>) and every
    /// one of them is a documented trap. Here the arithmetic says it instead:
    /// the interval is <c>tickLength / SpeedMultiplier</c>, and that is exactly
    /// zero at infinity. A 0 would read as "stopped", which is a mode this
    /// does not have — freezing is the control port's, not the pacer's.
    /// </para>
    /// <para>
    /// ⚠️ <b>This does not make the server faster; it makes it not wait.</b> A
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
    /// ⚠️ <b>Off by default and it should stay that way.</b> Recording a game
    /// people are playing is a thing to decide rather than a thing to inherit
    /// from a default, and the data is only worth having if somebody meant to
    /// collect it. See <see cref="MatchRecorder"/> for the on-disk bound.
    /// </para>
    /// </remarks>
    public string RecordingDirectory { get; set; } = string.Empty;

    /// <summary>
    /// The loopback control port, when <see cref="ControlPortEnabled"/> is set.
    /// Zero asks the OS for a free port, which is what tests want.
    /// </summary>
    public int ControlPort { get; set; }

    /// <summary>
    /// Whether to open the control port at all. <b>Off by default.</b>
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A separate flag rather than "a non-zero port means on", because
    /// zero is a port people want</b> — it is how every test asks for an
    /// ephemeral one (<c>ServerHarness.cs</c> does exactly that for the game
    /// port). Overloading zero as "off" would make the one configuration a test
    /// needs the one it cannot express.
    /// </remarks>
    public bool ControlPortEnabled { get; set; }

    /// <summary>
    /// Logs a histogram of tick intervals every 30 seconds and at shutdown.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Pacing only — it cannot touch the simulation</b>, so unlike the
    /// other switches this one is safe on a server with people on it, which is
    /// the only place the interesting measurement exists. Off by default
    /// because a healthy server's report is a line of noise every 30 seconds.
    /// </remarks>
    public bool TickStats { get; set; }

    /// <summary>The <c>[Bots]</c> section. Off by default, as in the reference.</summary>
    /// <remarks>
    /// ⚠️ <see cref="BotPopulationOptions.Default"/>, never <c>new()</c> — see the
    /// warning there. A zeroed one has a fill target of 0, a difficulty of 0 and a
    /// null name prefix, and every one of those is silent.
    /// </remarks>
    public BotPopulationOptions Bots { get; set; } = BotPopulationOptions.Default;

    public void Validate()
    {
        if (Port is < 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(Port), Port, "port must be 0..65535");

        // The reference clamps at 48 (game_init.gml:152). Beyond 255 the roster
        // index stops fitting in the ubyte every message uses to name a player.
        if (PlayerLimit is < 1 or > 48)
            throw new ArgumentOutOfRangeException(nameof(PlayerLimit), PlayerLimit, "player limit must be 1..48");

        if (MapDirectory.Length == 0)
            throw new InvalidOperationException($"{nameof(MapDirectory)} must point at the built-in map PNGs");

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
