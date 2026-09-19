using System.Globalization;
using Gg2.Bots;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Server;

/// <summary>How a setting is typed, so an editor can render it.</summary>
/// <remarks>
/// ⚠️ <b>The stored form of every setting is a STRING — the ini's own form —
/// whatever this says.</b> The kind is presentation and validation, not
/// representation: one representation is what makes
/// <see cref="ServerConfigFile.Write"/> the exact inverse of
/// <see cref="ServerConfigFile.Apply"/>, and a second one is where a
/// round-trip starts losing values.
/// </remarks>
public enum SettingKind
{
    /// <summary>Free text.</summary>
    Text,

    /// <summary>Text that must never be echoed back to a reader. See <see cref="ServerSetting.Secret"/>.</summary>
    Secret,

    /// <summary>A whole number.</summary>
    Integer,

    /// <summary>A number that may have a fraction, or the word <c>max</c>.</summary>
    Decimal,

    /// <summary><c>1</c> or <c>0</c>. ⚠️ Any non-zero reads as true, as every <c>if(global.flag)</c> does.</summary>
    Boolean,

    /// <summary>One of <see cref="ServerSetting.Choices"/>.</summary>
    Choice,

    /// <summary>A comma-separated list.</summary>
    List,

    /// <summary>A filesystem path.</summary>
    Path,
}

/// <summary>
/// One configurable server setting: where it lives in the ini, what type it is,
/// and how to move it between text and a <see cref="ServerOptions"/>.
/// </summary>
public sealed record ServerSetting
{
    /// <summary>The ini section, without brackets.</summary>
    public required string Section { get; init; }

    /// <summary>The ini key.</summary>
    public required string Key { get; init; }

    /// <summary>
    /// The <see cref="ServerOptions"/> property this setting writes into.
    /// </summary>
    /// <remarks>
    /// <b>The whole point of the field.</b> <c>ConfigFileCoverageTests</c>
    /// reflects over <see cref="ServerOptions"/> and fails on a property no row
    /// names, which is what stops an option being added, wired to real
    /// behaviour, and settable only from C#. Several rows may name one property
    /// — <see cref="ServerOptions.Limits"/> has fourteen.
    /// </remarks>
    public required string Property { get; init; }

    public required SettingKind Kind { get; init; }

    /// <summary>This setting's current value, in the ini's own text form.</summary>
    public required Func<ServerOptions, string> Get { get; init; }

    /// <summary>Applies a value in the ini's own text form.</summary>
    /// <remarks>
    /// ⚠️ <b>Clamps here, not in the caller.</b> The reference's own
    /// <c>game_init.gml</c> clamps as it reads, and a clamp applied by one caller
    /// and not another is how <c>CapLimit=0</c> becomes an endless round on one
    /// path and a one-capture round on the other.
    /// </remarks>
    public required Action<ServerOptions, string> Set { get; init; }

    /// <summary>
    /// A legal value that is NOT the default, for the gate that asserts this row
    /// moves the property it names.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Spelled out per row rather than derived from the kind.</b> A derived
    /// probe has to guess at every clamp, every enum's spelling and every list's
    /// separator, and a probe that lands on the default makes the assertion pass
    /// while testing nothing — the exact failure the gate exists to catch.
    /// </remarks>
    public required string Probe { get; init; }

    /// <summary>One line for an operator reading the settings page.</summary>
    public string Help { get; init; } = string.Empty;

    /// <summary>The legal values, for <see cref="SettingKind.Choice"/>.</summary>
    public IReadOnlyList<string> Choices { get; init; } = [];

    /// <summary>
    /// Whether the value must never leave the server.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>A secret is reported as "set" or "not set" and never as its
    /// value</b>, even to a signed-in admin over TLS. Two of the five are the
    /// admin panel's own password and the certificate's, so an endpoint that
    /// echoed them would turn one stolen session cookie into the credential
    /// itself — and the panel is the one surface here meant to be reachable from
    /// the network. <c>WebAdminTests.TheConfigEndpointNeverEchoesASecret</c>.
    /// </remarks>
    public bool Secret { get; init; }

    /// <summary>The stable identifier a wire protocol names this setting by.</summary>
    public string Id => Section + "." + Key;
}

/// <summary>
/// Every server setting, in one table.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ <b>ONE table, four readers.</b> <see cref="ServerConfigFile.Apply"/> reads
/// it, <see cref="ServerConfigFile.Write"/> writes it, the admin panel's settings
/// page renders it, and <c>ConfigFileCoverageTests</c> gates it against
/// <see cref="ServerOptions"/>'s own properties. Four hand-written lists is four
/// chances for one of them to be missing a row, and the failure of a missing row
/// is silent in every direction: a setting that loads and cannot be saved, or one
/// the page shows and the file ignores.
/// </para>
/// <para>
/// ⚠️ <b>The key names under <c>[Settings]</c>, <c>[Server]</c>,
/// <c>[Classlimits]</c> and <c>[Maps]</c> are the REFERENCE's</b>, spelling and
/// all — <c>Total bandwidth limit for map downloads in bytes per second</c> is a
/// real key in a real <c>gg2.ini</c>. They are transcribed rather than tidied so
/// that a config copied off a hosted GG2 server works here unedited. Everything
/// in the other sections is this port's own, because stock GG2 has no equivalent.
/// </para>
/// <para>
/// ⚠️ <b>Order matters in exactly two places</b>, and both are noted where they
/// occur: <c>[Web] Enabled</c> must come after <c>[Web] Port</c>, and
/// <c>[Runtime] ControlPortEnabled</c> after <c>[Runtime] ControlPort</c>, because
/// setting the port also turns the thing on and the explicit switch has to win.
/// </para>
/// </remarks>
public static class ServerSettings
{
    /// <summary>
    /// The ten <c>[Classlimits]</c> keys, in the ini's own spelling.
    /// </summary>
    /// <remarks>
    /// <b>The order here is the INI's, not <see cref="PlayerClass"/>'s.</b>
    /// <c>readClasslimitsFromIni.gml</c> writes each into
    /// <c>global.classlimits[CLASS_*]</c> by name, so the pairing is what matters
    /// and the sequence does not — which is exactly why this is a table of pairs
    /// rather than an array anybody could index by accident.
    /// </remarks>
    internal static readonly (string Key, PlayerClass Class)[] ClassLimitKeys =
    [
        ("Scout", PlayerClass.Scout),
        ("Pyro", PlayerClass.Pyro),
        ("Soldier", PlayerClass.Soldier),
        ("Heavy", PlayerClass.Heavy),
        ("Demoman", PlayerClass.Demoman),
        ("Medic", PlayerClass.Medic),
        ("Engineer", PlayerClass.Engineer),
        ("Spy", PlayerClass.Spy),
        ("Sniper", PlayerClass.Sniper),
        ("Quote", PlayerClass.Quote),
    ];

    private static readonly ServerSetting[] Table = Build();

    /// <summary>Every setting, in the order a settings page should show them.</summary>
    public static IReadOnlyList<ServerSetting> All => Table;

    private static readonly Dictionary<string, ServerSetting> ById =
        Table.ToDictionary(setting => setting.Id, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// One setting by its <see cref="ServerSetting.Id"/>, or null.
    /// </summary>
    /// <remarks>
    /// Case-insensitive, because the ini's own lookups are and an operator who
    /// typed <c>server.servername</c> means the same thing.
    /// </remarks>
    public static ServerSetting? Find(string id) =>
        ById.TryGetValue(id, out var setting) ? setting : null;

    private static ServerSetting[] Build()
    {
        var settings = new List<ServerSetting>();

        // --- [Settings] -------------------------------------------------------
        //
        // The reference's hosting port lives here rather than under [Server],
        // which reads as a mistake and is load-bearing: an ini written by the
        // stock client puts it here and a server that looked under [Server]
        // would silently listen on the wrong port.
        settings.Add(IntRow(
            "Settings", "HostingPort", nameof(ServerOptions.Port),
            o => o.Port, (o, v) => o.Port = v, "8191",
            "The game port clients connect to. 0 asks the OS for a free one."));

        settings.Add(IntRow(
            "Settings", "PlayerLimit", nameof(ServerOptions.PlayerLimit),
            o => o.PlayerLimit, (o, v) => o.PlayerLimit = v, "24",
            "Seats for network clients, 1..48. Slot 0 and bots do not count against it."));

        settings.Add(IntRow(
            "Settings", "MultiClientLimit", nameof(ServerOptions.MultiClientLimit),
            o => o.MultiClientLimit, (o, v) => o.MultiClientLimit = v, "1",
            "Connections allowed from one address. Loopback is exempt."));

        settings.Add(TextRow(
            "Settings", "PlayerName", nameof(ServerOptions.ServerPlayerName),
            o => o.ServerPlayerName, (o, v) => o.ServerPlayerName = v, "Referee",
            "The name of reserved roster slot 0, a permanent spectator."));

        // ⚠️ The default is NOT the reference's. See ServerOptions.AnnounceToLobby:
        // a headless binary must not publish its address because a config file it
        // inherited said 1. Writing `UseLobby=1` explicitly is a decision and is
        // honoured; leaving it out is not.
        settings.Add(BoolRow(
            "Settings", "UseLobby", nameof(ServerOptions.AnnounceToLobby),
            o => o.AnnounceToLobby, (o, v) => o.AnnounceToLobby = v,
            "Announce this server to ganggarrison.com so it appears in the client's "
            + "server list. The only outbound connection this project makes."));

        // No reference key to read: GG2 has no server-side rewards switch, because
        // its server answers every request. It is this port's own, for the same
        // reason UseLobby's default is not the reference's — relaying player
        // identities to a third party is a decision somebody has to make.
        settings.Add(BoolRow(
            "Settings", "Rewards", nameof(ServerOptions.Rewards),
            o => o.Rewards, (o, v) => o.Rewards = v,
            "Answer REWARD_REQUEST, relaying each player's reward id to the auth server."));

        // --- [Server] ---------------------------------------------------------
        settings.Add(TextRow(
            "Server", "ServerName", nameof(ServerOptions.ServerName),
            o => o.ServerName, (o, v) => o.ServerName = v, "a test server",
            "Shown in the client's server browser and on join."));

        settings.Add(TextRow(
            "Server", "WelcomeMessage", nameof(ServerOptions.WelcomeMessage),
            o => o.WelcomeMessage, (o, v) => o.WelcomeMessage = v, "hello",
            "Sent to each joiner. Empty means none."));

        // ⚠️ A DIFFERENT channel from WelcomeMessage above, not a second spelling
        // of it: that one is a MESSAGE_STRING every client understands, this one
        // is a line of chat only a client running a chat plugin ever sees. Chat
        // is where a player types, which is why an instruction about a chat
        // command belongs here rather than there.
        settings.Add(TextRow(
            "Server", "ChatJoinMessage", nameof(ServerOptions.ChatJoinMessage),
            o => o.ChatJoinMessage, (o, v) => o.ChatJoinMessage = v, "type /help",
            "A chat line sent to each player as they join chat. Empty means none. "
            + "Needs a chat plugin in ServerPluginList; a client without one never "
            + "sees it."));

        settings.Add(SecretRow(
            "Server", "Password", nameof(ServerOptions.Password),
            o => o.Password, (o, v) => o.Password = v, "hunter2",
            "The game password. Empty means none, and no PASSWORD_REQUEST."));

        settings.Add(TextRow(
            "Server", "Map", nameof(ServerOptions.Map),
            o => o.Map, (o, v) => o.Map = v, "koth_valley",
            "The map to open on. Empty means the first entry of the rotation."));

        // ⚠️ THE PORT'S OWN KEY, and it is checked BEFORE the reference's two
        // rotation sources — see ServerConfigFile.Apply. It has to win, because it
        // is the one the writer emits: a saved rotation that lost to a [Maps]
        // section left behind in the same file is a panel edit that silently does
        // nothing.
        settings.Add(ListRow(
            "Server", "Rotation", nameof(ServerOptions.MapRotation),
            o => o.MapRotation, (o, maps) => o.MapRotation = maps, "koth_valley,ctf_2dfort",
            "The map rotation, in order. A map may appear more than once."));

        settings.Add(ChoiceRow(
            "Server", "ShuffleRotation", nameof(ServerOptions.ShuffleRotation),
            ["none", "arena-last", "any"],
            o => o.ShuffleRotation switch
            {
                ShuffleMode.None => "none",
                ShuffleMode.AvoidArenaFirst => "arena-last",
                _ => "any",
            },
            (o, v) => o.ShuffleRotation = Shuffle(v),
            "any",
            "How the rotation is ordered at startup. arena-last shuffles but does "
            + "not open on an arena map."));

        settings.Add(ListRow(
            "Server", "VotableMaps", nameof(ServerOptions.VotableMaps),
            o => o.VotableMaps, (o, maps) => o.VotableMaps = maps,
            "ctf_truefort,koth_valley",
            "Maps a plugin map vote may offer. Empty means the rotation, else every "
            + "map in the maps directory."));

        settings.Add(TextRow(
            "Server", "ServerPluginList", nameof(ServerOptions.ServerPluginList),
            o => o.ServerPluginList, (o, v) => o.ServerPluginList = v, "chat_v2_pr",
            "Server-sent plugins clients are asked to load, comma-separated."));

        settings.Add(BoolRow(
            "Server", "ServerPluginsRequired", nameof(ServerOptions.ServerPluginsRequired),
            o => o.ServerPluginsRequired, (o, v) => o.ServerPluginsRequired = v,
            "Clients must load the plugin list to join."));

        settings.Add(BoolRow(
            "Server", "AutoBalance", nameof(ServerOptions.Autobalance),
            o => o.Autobalance, (o, v) => o.Autobalance = v,
            "Move a player across when one team is two larger."));

        // The same max/min clamps game_init.gml applies, and they are not
        // decoration: `CapLimit=0` in the reference is a ONE-capture round, not an
        // endless one, because the floor is applied before anything sees it.
        settings.Add(IntRow(
            "Server", "CapLimit", nameof(ServerOptions.CapLimit),
            o => o.CapLimit, (o, v) => o.CapLimit = (byte)Math.Clamp(v, 1, 255), "3",
            "Captures that win a round, 1..255. 0 is clamped to 1, not to unlimited."));

        settings.Add(IntRow(
            "Server", "Time Limit", nameof(ServerOptions.TimeLimitMinutes),
            o => o.TimeLimitMinutes, (o, v) => o.TimeLimitMinutes = (byte)Math.Clamp(v, 1, 255),
            "20", "Round length in minutes, 1..255. 0 is clamped to 1."));

        settings.Add(IntRow(
            "Server", "Deathmatch Kill Limit", nameof(ServerOptions.KillLimit),
            o => o.KillLimit, (o, v) => o.KillLimit = (ushort)Math.Clamp(v, 1, 65535), "50",
            "Kills that win a team-deathmatch round, 1..65535."));

        // Floored at 0, not 1 — zero really is "no spawn protection" here, which is
        // the one limit in this section with no escape to worry about.
        settings.Add(IntRow(
            "Server", "Team Deathmatch Invulnerability Seconds",
            nameof(ServerOptions.TdmInvulnerabilitySeconds),
            o => o.TdmInvulnerabilitySeconds,
            (o, v) => o.TdmInvulnerabilitySeconds = Math.Clamp(v, 0, 3600), "0",
            "Spawn protection in team deathmatch. 0 really is none."));

        // NOT clamped at 1, deliberately: game_init.gml reads Respawn Time raw and
        // GameServerCreate turns a 0 into a single tick. GameServer does the same,
        // so the escape stays where the reference put it.
        settings.Add(IntRow(
            "Server", "Respawn Time", nameof(ServerOptions.RespawnTimeSeconds),
            o => o.RespawnTimeSeconds, (o, v) => o.RespawnTimeSeconds = (byte)Math.Clamp(v, 0, 255),
            "8", "Seconds a dead player waits. 0 becomes a single tick."));

        settings.Add(IntRow(
            "Server", "Total bandwidth limit for map downloads in bytes per second",
            nameof(ServerOptions.MapDownloadBytesPerSecond),
            o => o.MapDownloadBytesPerSecond, (o, v) => o.MapDownloadBytesPerSecond = v, "12345",
            "TOTAL across every custom-map download in flight, not per client."));

        settings.Add(PathRow(
            "Server", "BanFile", nameof(ServerOptions.BanFile),
            o => o.BanFile, (o, v) => o.BanFile = v, "bans.txt",
            "Where operator bans are kept. Empty means they do not survive a restart."));

        // --- [Analytics] ------------------------------------------------------
        //
        // Its own section rather than two more keys in [Server], because both
        // are off by default and both are about keeping a record of people:
        // an operator reading a config file should find that decision in one
        // place. docs/ANALYTICS.md.
        settings.Add(PathRow(
            "Analytics", "File", nameof(ServerOptions.AnalyticsFile),
            o => o.AnalyticsFile, (o, v) => o.AnalyticsFile = v, "analytics.tsv",
            "Count who plays here and keep it across restarts. Empty counts nothing. "
            + "Addresses are stored as salted hashes, never as addresses."));

        settings.Add(PathRow(
            "Analytics", "GeoIpDatabase", nameof(ServerOptions.GeoIpFile),
            o => o.GeoIpFile, (o, v) => o.GeoIpFile = v, "GeoLite2-City.mmdb",
            "A MaxMind .mmdb used to put players on the analytics map. None ships with "
            + "this server. A country-level database resolves no US states."));

        settings.Add(PathRow(
            "Server", "MapDirectory", nameof(ServerOptions.MapDirectory),
            o => o.MapDirectory, (o, v) => o.MapDirectory = v, "some/other/maps",
            "Where the map PNGs are read from."));

        settings.Add(PathRow(
            "Server", "RecordingDirectory", nameof(ServerOptions.RecordingDirectory),
            o => o.RecordingDirectory, (o, v) => o.RecordingDirectory = v, "some/traces",
            "Write replayable .trace recordings of this server's broadcast stream here. "
            + "Empty records nothing — recording a game people are playing is a decision."));

        settings.Add(TextRow(
            "Server", "AuthServerHost", nameof(ServerOptions.AuthServerHost),
            o => o.AuthServerHost, (o, v) => o.AuthServerHost = v, "auth.example",
            "The reward auth service. Only contacted when Rewards is on."));

        settings.Add(IntRow(
            "Server", "AuthServerPort", nameof(ServerOptions.AuthServerPort),
            o => o.AuthServerPort, (o, v) => o.AuthServerPort = v, "45099",
            "That service's port."));

        // --- [Classlimits] ----------------------------------------------------
        //
        // ⚠️ 0 BANS a class and 255 unlimits it, which is the reference's encoding
        // and is the wrong way round from every intuition. readClasslimitFromIni.gml:
        // min(255, max(0, ...)) with 255 as the fallback, so a negative in the file
        // becomes 0 — which bans the class rather than unlimiting it.
        foreach (var (key, playerClass) in ClassLimitKeys)
        {
            var index = (int)playerClass;

            settings.Add(IntRow(
                "Classlimits", key, nameof(ServerOptions.ClassLimits),
                o => o.ClassLimits[index],
                (o, v) => o.ClassLimit(index, (byte)Math.Clamp(v, 0, 255)),
                "1",
                $"How many {key}s ONE TEAM may hold. 255 is unlimited; 0 BANS the class."));
        }

        // --- [Log] ------------------------------------------------------------
        //
        // ⚠️ Not needed under systemd or Docker, which capture stdout and stderr,
        // rotate on their own, and let an operator filter by stream. This is for a
        // deployment with neither.
        settings.Add(PathRow(
            "Log", "File", nameof(ServerOptions.LogFile),
            o => o.LogFile, (o, v) => o.LogFile = v, "logs/server.log",
            "TEE the log into this file. The console sink stays wired — the console "
            + "is the record an operator has when the thing that broke is the disk."));

        settings.Add(IntRow(
            "Log", "MaxBytes", nameof(ServerOptions.LogMaxBytes),
            o => (int)Math.Min(o.LogMaxBytes, int.MaxValue), (o, v) => o.LogMaxBytes = v,
            "1048576", "Rotate the log file at this size. At least 1024."));

        settings.Add(IntRow(
            "Log", "Keep", nameof(ServerOptions.LogKeep),
            o => o.LogKeep, (o, v) => o.LogKeep = v, "9",
            "Rotated log files to keep, 0..1000."));

        // --- [Limits] ---------------------------------------------------------
        //
        // ⚠️ THESE ARE THE BOUNDS THAT MAKE THE SERVER SAFE TO EXPOSE TO THE
        // INTERNET, and the defaults are the supported configuration. Every field
        // is orders of magnitude above what a stock client uses — ConnectionLimits
        // states the ratio for each — so raising one is not a way to be more
        // generous to real players, it is a way to give an attacker more room.
        // docs/HARDENING.md before changing any of them.
        settings.Add(IntRow(
            "Limits", "MaxReceiveBufferBytes", nameof(ServerOptions.Limits),
            o => o.Limits.MaxReceiveBufferBytes,
            (o, v) => o.Limits = o.Limits with { MaxReceiveBufferBytes = v }, "524288",
            "Unread bytes one client may have buffered before it is dropped. A stock "
            + "client's steady state is a few bytes."));

        settings.Add(IntRow(
            "Limits", "MaxReceiveBytesPerTick", nameof(ServerOptions.Limits),
            o => o.Limits.MaxReceiveBytesPerTick,
            (o, v) => o.Limits = o.Limits with { MaxReceiveBytesPerTick = v }, "32768",
            "Bytes read from one socket in one tick, however many are waiting. This "
            + "one is about the CLOCK, not about memory."));

        settings.Add(IntRow(
            "Limits", "MaxSendBacklogBytes", nameof(ServerOptions.Limits),
            o => o.Limits.MaxSendBacklogBytes,
            (o, v) => o.Limits = o.Limits with { MaxSendBacklogBytes = v }, "2097152",
            "Queued bytes one client may leave unread before it is dropped. The bound "
            + "on the cheapest attack there is: connect, join, stop reading."));

        settings.Add(IntRow(
            "Limits", "MaxInvalidCommandBytes", nameof(ServerOptions.Limits),
            o => o.Limits.MaxInvalidCommandBytes,
            (o, v) => o.Limits = o.Limits with { MaxInvalidCommandBytes = v }, "8192",
            "Garbage one connection may send before it is dropped."));

        settings.Add(IntRow(
            "Limits", "MaxAcceptsPerTick", nameof(ServerOptions.Limits),
            o => o.Limits.MaxAcceptsPerTick,
            (o, v) => o.Limits = o.Limits with { MaxAcceptsPerTick = v }, "16",
            "New connections taken off the listener in one tick."));

        settings.Add(IntRow(
            "Limits", "MaxJoiningConnections", nameof(ServerOptions.Limits),
            o => o.Limits.MaxJoiningConnections,
            (o, v) => o.Limits = o.Limits with { MaxJoiningConnections = v }, "64",
            "Half-finished handshakes held at once."));

        // ⚠️ SEPARATE from the reference's 30-second idle timeout, which a PING
        // resets — a connection that pings holds a seat for ever, which is why
        // there is a second clock that no activity resets. docs/HARDENING.md.
        settings.Add(IntRow(
            "Limits", "MaxHandshakeSeconds", nameof(ServerOptions.Limits),
            o => (int)o.Limits.MaxHandshakeDuration.TotalSeconds,
            (o, v) => o.Limits = o.Limits with { MaxHandshakeDuration = TimeSpan.FromSeconds(v) },
            "120",
            "Longest a connection may take to finish joining, however active it is. "
            + "NOT the 30s idle timeout — a PING resets that one and cannot reset this."));

        // ⚠️ NOT MultiClientLimit. That bounds how many connections one address
        // holds at once; this bounds how fast it opens them.
        settings.Add(IntRow(
            "Limits", "ConnectionBurstPerAddress", nameof(ServerOptions.Limits),
            o => o.Limits.ConnectionBurstPerAddress,
            (o, v) => o.Limits = o.Limits with { ConnectionBurstPerAddress = v }, "3",
            "Connections one address may open inside the window below. 0 turns the "
            + "rate limit off."));

        settings.Add(IntRow(
            "Limits", "ConnectionBurstWindowSeconds", nameof(ServerOptions.Limits),
            o => (int)o.Limits.ConnectionBurstWindow.TotalSeconds,
            (o, v) => o.Limits = o.Limits with { ConnectionBurstWindow = TimeSpan.FromSeconds(v) },
            "30", "The window that burst is counted over."));

        settings.Add(IntRow(
            "Limits", "ChatBubbleIntervalMs", nameof(ServerOptions.Limits),
            o => o.Limits.ChatBubbleIntervalMs,
            (o, v) => o.Limits = o.Limits with { ChatBubbleIntervalMs = v }, "500",
            "Smallest gap between one player's chat bubbles."));

        // ⚠️ Off is a REGRESSION, not a tuning choice: without keepalives a client
        // whose machine loses power holds a roster slot until TCP's own retransmit
        // gives up, which is around fifteen minutes on Linux. A SPECTATOR SENDS
        // NOTHING, so a receive-idle timeout is not the alternative.
        settings.Add(BoolRow(
            "Limits", "TcpKeepAlive", nameof(ServerOptions.Limits),
            o => o.Limits.TcpKeepAlive,
            (o, v) => o.Limits = o.Limits with { TcpKeepAlive = v },
            "Probe for peers that vanished. Off, a client whose machine dies holds a "
            + "roster slot for ~15 minutes. A spectator sends nothing, so this is the "
            + "mechanism rather than a heartbeat."));

        settings.Add(IntRow(
            "Limits", "KeepAliveIdleSeconds", nameof(ServerOptions.Limits),
            o => o.Limits.KeepAliveIdleSeconds,
            (o, v) => o.Limits = o.Limits with { KeepAliveIdleSeconds = v }, "60",
            "Quiet seconds before the first keepalive probe."));

        settings.Add(IntRow(
            "Limits", "KeepAliveIntervalSeconds", nameof(ServerOptions.Limits),
            o => o.Limits.KeepAliveIntervalSeconds,
            (o, v) => o.Limits = o.Limits with { KeepAliveIntervalSeconds = v }, "20",
            "Seconds between probes."));

        settings.Add(IntRow(
            "Limits", "KeepAliveProbeCount", nameof(ServerOptions.Limits),
            o => o.Limits.KeepAliveProbeCount,
            (o, v) => o.Limits = o.Limits with { KeepAliveProbeCount = v }, "5",
            "Unanswered probes before the peer is declared gone."));

        // --- [Nav] ------------------------------------------------------------
        settings.Add(PathRow(
            "Nav", "Directory", nameof(ServerOptions.NavCacheDirectory),
            o => o.NavCacheDirectory, (o, v) => o.NavCacheDirectory = v, "some/other/nav",
            "Where the bot nav graphs are read from. Empty is legal: bots then fight "
            + "but do not walk."));

        settings.Add(BoolRow(
            "Nav", "Generate", nameof(ServerOptions.NavGenerate),
            o => o.NavGenerate, (o, v) => o.NavGenerate = v,
            "Build a missing graph at the map change, which is what makes a custom "
            + "map navigable. Held in memory unless Save is on."));

        settings.Add(BoolRow(
            "Nav", "Save", nameof(ServerOptions.NavGenerateWrite),
            o => o.NavGenerateWrite, (o, v) => o.NavGenerateWrite = v,
            "Write a generated graph into the nav directory. Off by default because "
            + "it writes files the operator did not ask for."));

        // ⛔ OFF IS A DIFFERENT ARM, NOT A SLOWER ONE. NavConstants.SearchWeight makes
        // this a weighted A*: the heuristic decides which route is returned and not
        // merely how quickly, so routes MOVE and a run with this off is not
        // comparable to one with it on. docs/TRAPS-BOTS.md, "Shipping the tables".
        settings.Add(BoolRow(
            "Nav", "Distances", nameof(ServerOptions.NavDistances),
            o => o.NavDistances, (o, v) => o.NavDistances = v,
            "Give A* exact node-to-node distances instead of a straight-line estimate. "
            + "OFF IS A DIFFERENT ARM, not a slower one — the search is weighted, so "
            + "this picks the route as well as the speed."));

        // --- [Bots] -----------------------------------------------------------
        settings.Add(BoolRow(
            "Bots", "Enabled", "Bots",
            o => o.Bots.Enabled, (o, v) => o.Bots = o.Bots with { Enabled = v },
            "Manage a bot population at all."));

        // ⚠️ A FILL target including humans, not a bot count: a server filling to 8
        // with 3 humans on it runs 5 bots and drops one every time somebody joins.
        settings.Add(IntRow(
            "Bots", "FillToPlayers", "Bots",
            o => o.Bots.FillToPlayers, (o, v) => o.Bots = o.Bots with { FillToPlayers = v }, "12",
            "How many players the server wants IN TOTAL, humans included."));

        settings.Add(IntRow(
            "Bots", "MaxBots", "Bots",
            o => o.Bots.MaxBots, (o, v) => o.Bots = o.Bots with { MaxBots = v }, "12",
            "A hard ceiling on top of the fill target."));

        // ⚠️ BELOW THIS THE TARGET IS ZERO rather than the fill, which is why a bot
        // count set on an empty server can look like a button that does nothing.
        settings.Add(IntRow(
            "Bots", "MinHumans", "Bots",
            o => o.Bots.MinHumans, (o, v) => o.Bots = o.Bots with { MinHumans = v }, "2",
            "Humans that must be connected before ANY bot is spawned. Below it the "
            + "target is ZERO, not the fill — an empty server full of bots looks full "
            + "in the lobby list and is not."));

        settings.Add(IntRow(
            "Bots", "Difficulty", "Bots",
            o => o.Bots.Difficulty, (o, v) => o.Bots = o.Bots with { Difficulty = v }, "3",
            "1 to 5. 5 is 'as good as the layer gets'; the lower tiers are a "
            + "deliberate handicap ladder."));

        // ⚠️ ZERO MEANS THE COMMAND DOES NOT EXIST, and that is one key doing two
        // jobs on purpose: a separate boolean would let a server be configured
        // with /bots enabled and a window of zero minutes, which reads as working
        // and does nothing.
        settings.Add(IntRow(
            "Bots", "SuspendMinutes", nameof(ServerOptions.BotSuspendMinutes),
            o => o.BotSuspendMinutes, (o, v) => o.BotSuspendMinutes = v, "15",
            "How long the /bots off chat command takes the bots away for, 0..1440. "
            + "0 means the command does not exist on this server. Needs a chat plugin in "
            + "ServerPluginList - it is the only way a player can type /bots."));

        settings.Add(TextRow(
            "Bots", "NamePrefix", "Bots",
            o => o.Bots.NamePrefix ?? string.Empty,
            (o, v) => o.Bots = o.Bots with { NamePrefix = v }, "[AI] ",
            "What a bot's name starts with."));

        // ⛔ WHICH CONTROLLER DRIVES A CLASS — a different question from which
        // classes bots play, which is [BotClasslimits]. Default is every
        // checkpoint's own set, so this only ever has to be written to override one.
        settings.Add(ListRow(
            "Bots", "ClassPolicy", nameof(ServerOptions.BotClassPolicies),
            o => o.BotClassPolicies, (o, v) => o.BotClassPolicies = v,
            "Sniper=scripted,Pyro=tier2_pyro_rest3_s0",
            "Class=name entries deciding who drives each class, where name is a "
                + $"checkpoint's file name without its extension or '{BotBrain.Scripted}'. "
                + "Anything not named here keeps the default: whichever loaded checkpoint's "
                + "own class set claims it, scripted if none does."));

        // ⛔ A BEHAVIOUR switch, not a performance one. Off, a team's routes stop
        // being pushed apart and match dispersion falls 0.785 -> 0.634 with nothing
        // replacing it. docs/TRAPS-BOTS.md.
        settings.Add(BoolRow(
            "Bots", "Occupancy", nameof(ServerOptions.BotOccupancy),
            o => o.BotOccupancy, (o, v) => o.BotOccupancy = v,
            "Price an edge a team-mate is already walking above its honest cost, so a "
            + "squad fans out. A BEHAVIOUR switch: off, match dispersion falls "
            + "0.785 to 0.634 and nothing replaces it."));

        settings.Add(BoolRow(
            "Bots", "EngageBots", nameof(ServerOptions.BotEngagesBotsToo),
            o => o.BotEngagesBotsToo, (o, v) => o.BotEngagesBotsToo = v,
            "Run the expensive micro-controller in bot-vs-bot fights too. It does not "
            + "make the bots better; it makes those fights cost more."));

        // A DEBUGGING aid and the one Bots row that changes nothing a bot
        // decides. Every bubble is a broadcast to every connected client, which
        // is why it is off by default rather than merely quiet.
        settings.Add(BoolRow(
            "Bots", "HandoverBubbles", nameof(ServerOptions.BotHandoverBubbles),
            o => o.BotHandoverBubbles, (o, v) => o.BotHandoverBubbles = v,
            "Bots put up a chat bubble whenever their brain changes hands: z1 a policy "
            + "took the wheel, z3 the search did, z2 the scripted layer took it back. "
            + "For debugging; every bubble is a broadcast."));

        settings.Add(BoolRow(
            "Bots", "AdaptiveBudget", nameof(ServerOptions.AdaptiveBotBudget),
            o => o.AdaptiveBotBudget, (o, v) => o.AdaptiveBotBudget = v,
            "Let the server walk the controller budget down when ticks crowd their "
            + "deadline. The arbiter setting stays an upper bound."));

        settings.Add(ChoiceRow(
            "Bots", "Mode", nameof(ServerOptions.BotMode),
            ["scripted", "search", "policy", "tiered"],
            o => o.BotMode.ToString().ToLowerInvariant(),
            (o, v) => o.BotMode = Mode(v),
            "search",
            "Which of the four shipped brains runs. search is the better bot and the "
            + "far more expensive one."));

        // ⚠️ A LIST, not a path, since 2026-09-07 — the shipped bundle is two
        // checkpoints driving disjoint class sets, and one file cannot express two
        // sets. ⛔ Which means a checkpoint whose path contains a COMMA cannot be
        // named here; the flag can still name it, and nothing on this side can
        // round-trip it. Said out loud because a config file silently truncating a
        // path is exactly the failure this table exists to prevent.
        settings.Add(ListRow(
            "Bots", "Policy", nameof(ServerOptions.PolicyPaths),
            o => o.PolicyPaths, (o, v) => o.PolicyPaths = v,
            "checkpoints/tier0_combat4_s2.npz,checkpoints/tier2_pyro_rest3_s0.npz",
            "The gg2-rl checkpoints policy and tiered modes load, one tier each. A "
            + "checkpoint is DATED by the simulation it was trained against, and the "
            + "classes it drives are read off the file."));

        settings.Add(BoolRow(
            "Bots", "PolicyIgnoreStamp", nameof(ServerOptions.PolicyIgnoreStamp),
            o => o.PolicyIgnoreStamp, (o, v) => o.PolicyIgnoreStamp = v,
            "BYPASS the SIM_UUID guard, which exists to stop a policy running against "
            + "a simulation it was not trained on. The server prints both stamps."));

        settings.Add(new ServerSetting
        {
            Section = "Bots",
            Key = "Arbiter",
            Property = nameof(ServerOptions.BotArbiterMicroseconds),
            Kind = SettingKind.Integer,
            Get = o => o.BotArbiterMicroseconds?.ToString(CultureInfo.InvariantCulture)
                ?? string.Empty,
            Set = (o, v) => o.BotArbiterMicroseconds =
                v.Trim().Length == 0 ? null : Number("Bots.Arbiter", v),
            Probe = "10000",
            Help = "MICROSECONDS of bot-controller work per tick, against a 33,300us tick. "
                + "Empty lets the mode pick its default.",
        });

        settings.Add(IntRow(
            "Bots", "SearchThreads", nameof(ServerOptions.SearchThreads),
            o => o.SearchThreads, (o, v) => o.SearchThreads = v, OtherNumberProbe,
            "Threads the rollout search uses. Threads change NO decision, so this is a "
            + "pure cost knob."));

        // --- [BotClasslimits] -------------------------------------------------
        //
        // WHICH CLASSES BOTS PLAY, as a per-team count of BOTS. The same encoding
        // as [Classlimits] on purpose — 0 bars, 255 unlimits — so the two sections
        // cannot be read the wrong way round from each other. Quote has no key:
        // the draw is irandom(8) and never reaches it.
        //
        // ⚠️ Spy defaults to 0, which is BotPopulation.PickClass's own roster
        // written as a limit; a server configuring nothing draws what it always did.
        foreach (var (key, playerClass) in ClassLimitKeys)
        {
            if (playerClass == PlayerClass.Quote)
                continue;

            var index = (int)playerClass;

            settings.Add(IntRow(
                "BotClasslimits", key, "Bots",
                o => o.Bots.EffectiveClassLimits[index],
                (o, v) => o.Bots = BotClassLimit(o.Bots, index, (byte)Math.Clamp(v, 0, 255)),
                "1",
                $"How many {key} BOTS one team may hold. Humans do not count and are not "
                + "limited by this. 255 is unlimited; 0 keeps bots off the class."));
        }

        // --- [Web] ------------------------------------------------------------
        //
        // No reference keys to transcribe: stock GG2 has no admin panel, so this
        // whole section is this port's own. It is here rather than
        // command-line-only because the one thing an operator should not have to put
        // in a process listing is the admin password.
        //
        // ⚠️ Port being PRESENT enables the panel, not Port being non-zero: 0 means
        // "ask the OS for a free port" everywhere else in this project and must keep
        // meaning that here.
        settings.Add(new ServerSetting
        {
            Section = "Web",
            Key = "Port",
            Property = nameof(ServerOptions.Web),
            Kind = SettingKind.Integer,
            Get = o => o.Web.Port.ToString(CultureInfo.InvariantCulture),
            Set = (o, v) => o.Web = o.Web with { Enabled = true, Port = Number("Web.Port", v) },
            Probe = "8291",
            Help = "The admin panel's port. Naming it at all turns the panel ON; 0 asks "
                + "the OS for a free one.",
        });

        // ⚠️ AFTER Port, and the order is load-bearing: Port's setter turns the panel
        // on, so an explicit Enabled=0 has to be applied second to win.
        settings.Add(BoolRow(
            "Web", "Enabled", nameof(ServerOptions.Web),
            o => o.Web.Enabled, (o, v) => o.Web = o.Web with { Enabled = v },
            "Host the admin panel. Written explicitly so turning it OFF is expressible; "
            + "a Port key on its own turns it on.",
            probe: "1"));

        // ⛔ THE ONE DEFAULT IN THIS PROJECT WHOSE BEING WRONG WOULD PUT AN ADMIN
        // CONSOLE ON THE INTERNET. Loopback unless somebody says otherwise.
        settings.Add(TextRow(
            "Web", "Bind", nameof(ServerOptions.Web),
            o => o.Web.Bind, (o, v) => o.Web = o.Web with { Bind = v }, "0.0.0.0",
            "Where the panel listens. 127.0.0.1 is local only; 0.0.0.0 is what makes "
            + "it remote, and remote without a certificate sends the admin password in "
            + "the clear."));

        settings.Add(SecretRow(
            "Web", "Password", nameof(ServerOptions.Web),
            o => o.Web.Password, (o, v) => o.Web = o.Web with { Password = v }, "a long one",
            "The admin password. EMPTY DOES NOT MEAN NO PASSWORD — it means one is "
            + "generated and printed to the console at startup. A generated one lasts as "
            + "long as the process: a restart from this panel carries it, so pressing "
            + "restart does not lock you out, and a new process generates a new one."));

        settings.Add(PathRow(
            "Web", "Certificate", nameof(ServerOptions.Web),
            o => o.Web.CertificatePath, (o, v) => o.Web = o.Web with { CertificatePath = v },
            "cert.pfx",
            "A PKCS#12 certificate to serve HTTPS with. Empty is plain HTTP."));

        settings.Add(SecretRow(
            "Web", "CertificatePassword", nameof(ServerOptions.Web),
            o => o.Web.CertificatePassword,
            (o, v) => o.Web = o.Web with { CertificatePassword = v }, "pw",
            "That certificate's password, if it has one."));

        // --- [Runtime] --------------------------------------------------------
        //
        // This port's own section: pacing, telemetry and the loopback control port,
        // none of which stock GG2 has.
        //
        // ⛔ Speed and the control port MUST STAY OFF/1.0 wherever a client is
        // connected — clients pace themselves at 30 Hz and re-simulate between
        // updates, so an unpaced server desynchronises every one of them at once.
        settings.Add(new ServerSetting
        {
            Section = "Runtime",
            Key = "Speed",
            Property = nameof(ServerOptions.SpeedMultiplier),
            Kind = SettingKind.Decimal,
            Get = o => ControlCommands.Number(o.SpeedMultiplier).Trim('"'),
            Set = (o, v) => o.SpeedMultiplier = Speed(v),
            Probe = "max",
            Help = "Tick pacing. 1 is real time; 'max' is unpaced. MUST STAY AT 1 on a "
                + "server anybody is connected to.",
        });

        settings.Add(BoolRow(
            "Runtime", "IdleWhenEmpty", nameof(ServerOptions.IdleWhenEmpty),
            o => o.IdleWhenEmpty, (o, v) => o.IdleWhenEmpty = v,
            "Drop to 2 Hz when nobody is connected and no bots are running. The round "
            + "clock slows with it, which is why an idle server's round is not "
            + "wall-clock Time Limit."));

        settings.Add(BoolRow(
            "Runtime", "TickStats", nameof(ServerOptions.TickStats),
            o => o.TickStats, (o, v) => o.TickStats = v,
            "Log a histogram of tick intervals every 30 seconds. Pacing only, so it is "
            + "safe with players on."));

        settings.Add(new ServerSetting
        {
            Section = "Runtime",
            Key = "ControlPort",
            Property = nameof(ServerOptions.ControlPort),
            Kind = SettingKind.Integer,
            Get = o => o.ControlPort.ToString(CultureInfo.InvariantCulture),
            Set = (o, v) =>
            {
                o.ControlPort = Number("Runtime.ControlPort", v);
                o.ControlPortEnabled = true;
            },
            Probe = "9001",
            Help = "The loopback-only JSON control port. Naming it at all turns it ON; "
                + "0 asks the OS for a free one. It executes commands against a live game.",
        });

        settings.Add(new ServerSetting
        {
            Section = "Mesh",
            Key = "ListenPort",
            Property = nameof(ServerOptions.MeshListenPort),
            Kind = SettingKind.Integer,
            Get = o => o.MeshListenPort.ToString(CultureInfo.InvariantCulture),
            Set = (o, v) =>
            {
                o.MeshListenPort = Number("Mesh.ListenPort", v);
                o.MeshEnabled = true;
            },
            Probe = "27600",
            Help = "Wait for a mesh peer on this port. Naming it at all turns the mesh ON. "
                + "⛔ A server with no mesh setting is an ordinary standalone server.",
        });

        settings.Add(new ServerSetting
        {
            Section = "Mesh",
            Key = "Connect",
            Property = nameof(ServerOptions.MeshConnect),
            Kind = SettingKind.Text,
            Get = o => o.MeshConnect,
            Set = (o, v) =>
            {
                o.MeshConnect = v.Trim();
                o.MeshEnabled = o.MeshEnabled || o.MeshConnect.Length > 0;
            },
            Probe = "gg2-us-west:27600",
            Help = "Join a mesh peer at host:port instead of waiting for one. Naming it "
                + "turns the mesh ON.",
        });

        settings.Add(new ServerSetting
        {
            Section = "Mesh",
            Key = "ConnectPatienceSeconds",
            Property = nameof(ServerOptions.MeshConnectPatienceSeconds),
            Kind = SettingKind.Integer,
            Get = o => o.MeshConnectPatienceSeconds.ToString(CultureInfo.InvariantCulture),
            Set = (o, v) => o.MeshConnectPatienceSeconds =
                Number("Mesh.ConnectPatienceSeconds", v),
            Probe = "60",
            Help = "How long a joiner keeps trying to reach its peer before giving up. "
                + "⚠️ Does NOT turn the mesh on, unlike the two keys above — it only "
                + "says how patient Connect is. 0 fails on the first refusal.",
        });

        // ⚠️ AFTER the two above, for the reason [Runtime] ControlPortEnabled comes
        // after ControlPort: naming a port turns the mesh on, so the explicit
        // switch has to be able to turn it back off.
        settings.Add(BoolRow(
            "Mesh", "Enabled", nameof(ServerOptions.MeshEnabled),
            o => o.MeshEnabled, (o, v) => o.MeshEnabled = v,
            "Run as one node of a match happening in more than one place. A ListenPort "
            + "or Connect key on its own turns it on; this is how it is turned back off.",
            probe: "1"));

        // ⚠️ AFTER ControlPort, for the reason [Web] Enabled comes after [Web] Port:
        // the port's setter turns it on, so the explicit switch has to be second.
        // ⛔ A separate key rather than "a non-zero port means on", because zero is a
        // port people want — it is how every test asks for an ephemeral one.
        settings.Add(BoolRow(
            "Runtime", "ControlPortEnabled", nameof(ServerOptions.ControlPortEnabled),
            o => o.ControlPortEnabled, (o, v) => o.ControlPortEnabled = v,
            "Open the control port at all. A ControlPort key on its own turns it on; "
            + "this is how it is turned back off.",
            probe: "1"));

        return [.. settings];
    }

    // --- row constructors ----------------------------------------------------
    //
    // Shorthands, not abstraction: each one only fills in Kind and wraps the
    // property accessor in the ini's text form. A row that needs anything else
    // spells itself out above rather than growing a parameter here.

    private static ServerSetting TextRow(
        string section, string key, string property,
        Func<ServerOptions, string> get, Action<ServerOptions, string> set,
        string probe, string help) =>
        new()
        {
            Section = section, Key = key, Property = property, Kind = SettingKind.Text,
            Get = get, Set = set, Probe = probe, Help = help,
        };

    private static ServerSetting PathRow(
        string section, string key, string property,
        Func<ServerOptions, string> get, Action<ServerOptions, string> set,
        string probe, string help) =>
        new()
        {
            Section = section, Key = key, Property = property, Kind = SettingKind.Path,
            Get = get, Set = set, Probe = probe, Help = help,
        };

    private static ServerSetting SecretRow(
        string section, string key, string property,
        Func<ServerOptions, string> get, Action<ServerOptions, string> set,
        string probe, string help) =>
        new()
        {
            Section = section, Key = key, Property = property, Kind = SettingKind.Secret,
            Get = get, Set = set, Probe = probe, Help = help, Secret = true,
        };

    private static ServerSetting IntRow(
        string section, string key, string property,
        Func<ServerOptions, int> get, Action<ServerOptions, int> set,
        string probe, string help) =>
        new()
        {
            Section = section, Key = key, Property = property, Kind = SettingKind.Integer,
            Get = o => get(o).ToString(CultureInfo.InvariantCulture),
            Set = (o, v) => set(o, Number(section + "." + key, v)),
            Probe = probe, Help = help,
        };

    private static ServerSetting BoolRow(
        string section, string key, string property,
        Func<ServerOptions, bool> get, Action<ServerOptions, bool> set, string help,
        string probe = FlipProbe) =>
        new()
        {
            Section = section, Key = key, Property = property, Kind = SettingKind.Boolean,
            Get = o => get(o) ? "1" : "0",

            // ⚠️ ANY NON-ZERO IS TRUE, as every `if(global.someFlag)` in the
            // reference is. A file saying `AutoBalance=2` turns it on.
            Set = (o, v) => set(o, Number(section + "." + key, v) != 0),

            // ⚠️ The probe is "the opposite of whatever the default is", which
            // cannot be a literal: TcpKeepAlive defaults on and TickStats defaults
            // off, so one fixed string would silently be a no-op for half of these.
            Probe = probe,
            Help = help,
        };

    private static ServerSetting ChoiceRow(
        string section, string key, string property, string[] choices,
        Func<ServerOptions, string> get, Action<ServerOptions, string> set,
        string probe, string help) =>
        new()
        {
            Section = section, Key = key, Property = property, Kind = SettingKind.Choice,
            Get = get, Set = set, Probe = probe, Help = help, Choices = choices,
        };

    private static ServerSetting ListRow(
        string section, string key, string property,
        Func<ServerOptions, IReadOnlyList<string>> get,
        Action<ServerOptions, IReadOnlyList<string>> set,
        string probe, string help) =>
        new()
        {
            Section = section, Key = key, Property = property, Kind = SettingKind.List,
            Get = o => string.Join(',', get(o)),
            Set = (o, v) => set(o, Split(v)),
            Probe = probe, Help = help,
        };

    /// <summary>
    /// The probe marker a boolean row carries instead of a literal.
    /// </summary>
    /// <remarks>
    /// See the comment on <see cref="BoolRow"/>: a fixed <c>"1"</c> would be a
    /// no-op for every setting that already defaults on, and the gate would pass
    /// while testing nothing.
    /// </remarks>
    public const string FlipProbe = "<flip>";

    /// <summary>
    /// The probe marker a row carries when its DEFAULT depends on the machine.
    /// </summary>
    /// <remarks>
    /// The same failure <see cref="FlipProbe"/> exists for, one type along. A
    /// literal probe is only a probe while it differs from the default, and
    /// <c>SearchThreads</c> defaults to <c>ProcessorCount / 3</c> clamped to
    /// [1, 8] — which is exactly 2 on a 6- to 8-core box. A literal <c>"2"</c>
    /// therefore turns "this setting round-trips" into "this setting is
    /// unreachable" on the machines most people have, and the gate says the
    /// setting is broken rather than that the probe is.
    /// <para>
    /// Resolves to 1, or to 2 when the current value is already 1, so it is
    /// always a different value and always inside the range any thread-count
    /// setting accepts.
    /// </para>
    /// </remarks>
    public const string OtherNumberProbe = "<other>";

    /// <summary>
    /// The probe value to use against <paramref name="options"/>, resolving
    /// <see cref="FlipProbe"/> against the current value.
    /// </summary>
    public static string ProbeFor(this ServerSetting setting, ServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(setting);

        if (setting.Probe == FlipProbe)
            return setting.Get(options) == "0" ? "1" : "0";

        // A machine-derived default cannot be probed with a literal — see
        // OtherNumberProbe.
        if (setting.Probe == OtherNumberProbe)
            return setting.Get(options) == "1" ? "2" : "1";

        return setting.Probe;
    }

    // --- value parsing -------------------------------------------------------

    /// <summary>
    /// A number, as <c>ini_read_real</c> reads one.
    /// </summary>
    /// <remarks>
    /// <b>Parsed as a double and truncated</b>, because a file carrying
    /// <c>PlayerLimit=10.0</c> — which is what GM8 writes for some values — must
    /// not be a startup failure.
    /// <para>
    /// ⛔ <b>Refused rather than ignored.</b> A typo in <c>PlayerLimit</c> that
    /// silently left the default in place is a server quietly running a
    /// configuration nobody wrote.
    /// </para>
    /// </remarks>
    internal static int Number(string id, string value)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            || !double.IsFinite(parsed))
        {
            throw new FormatException($"{id}: expected a number, got '{value}'");
        }

        return (int)Math.Clamp(parsed, int.MinValue, int.MaxValue);
    }

    /// <summary>A comma-separated list, blanks dropped and entries trimmed.</summary>
    private static string[] Split(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// The reference's three branches: 0 is off, 1 avoids opening on arena, and
    /// <b>anything else</b> is a plain shuffle. The names are this port's.
    /// </summary>
    private static ShuffleMode Shuffle(string value) => value.Trim().ToLowerInvariant() switch
    {
        "none" or "off" or "0" => ShuffleMode.None,
        "arena-last" or "1" => ShuffleMode.AvoidArenaFirst,
        "any" or "on" or "2" => ShuffleMode.Any,
        _ => throw new FormatException(
            $"Server.ShuffleRotation: '{value}' is not a shuffle mode. "
            + "none, arena-last (shuffle but do not open on an arena map), or any."),
    };

    /// <summary>
    /// ⚠️ <b>The numbers are part of the contract</b> — a sweep over 0..3 is how a
    /// bench harness asks for each arm in turn, so <see cref="BotMode"/>'s values
    /// must not be renumbered.
    /// </summary>
    private static BotMode Mode(string value) => value.Trim().ToLowerInvariant() switch
    {
        "scripted" or "0" => BotMode.Scripted,
        "search" or "hybrid-search" or "1" => BotMode.Search,
        "policy" or "hybrid-policy" or "2" => BotMode.Policy,
        "tiered" or "3" => BotMode.Tiered,
        _ => throw new FormatException(
            $"Bots.Mode: '{value}' is not a bot mode. scripted (0), search (1), "
            + "policy (2) or tiered (3)."),
    };

    /// <summary>
    /// Writes one <c>[BotClasslimits]</c> value, copying the array first.
    /// </summary>
    /// <remarks>
    /// Copied for the reason <c>ClassLimit</c> below gives: an options object may
    /// share its array with another, and the coverage gate's before-and-after
    /// comparison needs two arrays to compare.
    /// </remarks>
    public static BotPopulationOptions BotClassLimit(BotPopulationOptions bots, int index, byte limit)
    {
        var limits = bots.EffectiveClassLimits.ToArray();
        limits[index] = limit;
        return bots with { ClassLimits = limits };
    }

    /// <summary>A number, or the word <c>max</c> — the same grammar <c>--speed</c> takes.</summary>
    private static double Speed(string value) =>
        value.Trim().Equals("max", StringComparison.OrdinalIgnoreCase)
            ? double.PositiveInfinity
            : double.TryParse(value, CultureInfo.InvariantCulture, out var speed)
                ? speed
                : throw new FormatException(
                    $"Runtime.Speed: needs a number or 'max', not '{value}'");

    // --- accessors the rows above need and ServerOptions does not expose ------

    /// <summary>
    /// Writes one class limit, copying the array first.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Copied, never mutated in place.</b> <see cref="ServerOptions.ClassLimits"/>
    /// may be <see cref="ServerOptions.NoClassLimits"/>'s array or one shared with
    /// another options object, and a settings page that writes one class's limit
    /// must not reach into somebody else's roster. It also has to be a copy for
    /// the gate that asserts a row moves the property it names: mutating in place
    /// leaves the "before" and "after" arrays the same object.
    /// </remarks>
    private static void ClassLimit(this ServerOptions options, int index, byte limit)
    {
        var limits = options.ClassLimits.Length == TeamBalance.ClassCount
            ? (byte[])options.ClassLimits.Clone()
            : ServerOptions.NoClassLimits();

        limits[index] = limit;
        options.ClassLimits = limits;
    }
}
