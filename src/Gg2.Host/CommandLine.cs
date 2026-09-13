using System.Globalization;
using Gg2.Bots;
using Gg2.Protocol;
using Gg2.Server;
using Gg2.Simulation;

namespace Gg2.Host;

/// <summary>
/// The server's command line: every flag, what it sets, and the usage text
/// that documents it. <see cref="Program"/> only decides which parser runs.
/// </summary>
internal static class CommandLine
{
    /// <summary>
    /// The value after <paramref name="flag"/>, or null when it was not given.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A pre-pass helper, and it does not understand the grammar.</b> It
    /// finds the flag anywhere in the argument list, which is right for
    /// <c>--config</c> and for "was --map given" and would be wrong for anything
    /// whose value could itself look like a flag. Do not grow it into a parser;
    /// the switch below is the parser.
    /// </remarks>
    private static string? ValueOf(string[] args, string flag)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == flag)
                return i + 1 < args.Length ? args[i + 1] : string.Empty;
        }

        return null;
    }

    /// <summary>
    /// <c>--rotation</c>: a comma-separated list, or a rotation file's path.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>An existing file wins over the comma split</b>, and a map name
    /// cannot contain a path separator, so the two cannot collide. A path that
    /// does not exist falls through to the comma split and then fails as an
    /// unloadable map name, which names the thing that is wrong.
    /// </remarks>
    private static IReadOnlyList<string> ParseRotation(string flag, string text)
    {
        if (File.Exists(text))
            return ServerConfigFile.ReadRotationFile(text);

        var maps = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (maps.Length == 0)
            throw new ArgumentException($"{flag}: no maps in '{text}'");

        return maps;
    }

    /// <summary>
    /// <c>--bot-mode</c>: a name or the enum's number.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Numbers are part of the contract</b> — a sweep over 0..3 is how a
    /// bench harness asks for each arm in turn, so <see cref="BotMode"/>'s values
    /// must not be renumbered. Names are the readable form and both are accepted.
    /// </remarks>
    private static BotMode ParseBotMode(string flag, string text) => text.ToLowerInvariant() switch
    {
        "scripted" or "0" => BotMode.Scripted,
        "search" or "hybrid-search" or "1" => BotMode.Search,
        "policy" or "hybrid-policy" or "2" => BotMode.Policy,
        "tiered" or "3" => BotMode.Tiered,
        _ => throw new ArgumentException(
            $"{flag}: '{text}' is not a bot mode. scripted (0), search (1), policy (2) "
            + "or tiered (3) — see --help for what each one costs."),
    };

    /// <summary>
    /// <c>--shuffle</c>: the ini's 0/1/2, or the names this port gives them.
    /// </summary>
    private static ShuffleMode ParseShuffle(string flag, string text) => text.ToLowerInvariant() switch
    {
        "none" or "off" or "0" => ShuffleMode.None,
        "arena-last" or "1" => ShuffleMode.AvoidArenaFirst,
        "any" or "on" or "2" => ShuffleMode.Any,
        _ => throw new ArgumentException(
            $"{flag}: '{text}' is not a shuffle mode. "
            + "none, arena-last (shuffle but do not open on an arena map), or any."),
    };

    /// <summary>
    /// Splits a <c>host:port</c> argument, defaulting the port when it is
    /// omitted.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Rightmost colon</b>, so a bare IPv6 literal is not silently cut in
    /// half. A host with no colon keeps the reference's port.
    /// </remarks>
    private static (string Host, int Port) ParseHostPort(string flag, string text)
    {
        var separator = text.LastIndexOf(':');

        if (separator < 0)
            return (text, GameConstants.AuthServerPort);

        var host = text[..separator];
        var port = text[(separator + 1)..];

        if (!int.TryParse(port, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            || parsed is < 1 or > 65535)
        {
            throw new ArgumentException($"{flag}: '{port}' is not a port number");
        }

        return (host, parsed);
    }

    /// <summary>
    /// <c>--class-limit</c>: one number for every class, or <c>name=n</c> pairs.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>0 bans a class and 255 unlimits it</b>, which is the reference's
    /// encoding and is the wrong way round from every intuition. It is stated in
    /// the usage text for exactly that reason.
    /// </remarks>
    private static byte[] ParseClassLimits(string flag, string text)
    {
        if (byte.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var everyClass))
        {
            var uniform = new byte[10];
            Array.Fill(uniform, everyClass);
            return uniform;
        }

        var limits = ServerOptions.NoClassLimits();

        foreach (var pair in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);

            if (separator < 0)
            {
                throw new ArgumentException(
                    $"{flag}: '{pair}' is neither a number for every class nor a "
                    + "<class>=<limit> pair, such as 'medic=1,spy=0'.");
            }

            var className = pair[..separator].Trim();
            var limitText = pair[(separator + 1)..].Trim();

            if (!Enum.TryParse(className, ignoreCase: true, out PlayerClass playerClass)
                || !Enum.IsDefined(playerClass))
            {
                throw new ArgumentException(
                    $"{flag}: '{className}' is not a class. Scout, Soldier, Sniper, Demoman, "
                    + "Medic, Engineer, Heavy, Spy, Pyro or Quote.");
            }

            if (!byte.TryParse(limitText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit))
            {
                throw new FormatException(
                    $"{flag}: '{className}' needs a limit of 0..255, got '{limitText}'. "
                    + "0 bans the class; 255 is unlimited.");
            }

            limits[(int)playerClass] = limit;
        }

        return limits;
    }

    /// <summary>A comma-separated class list, or a clear error naming the bad one.</summary>
    private static PlayerClass[] ParseClasses(string flag, string text)
    {
        var names = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var picked = new PlayerClass[names.Length];

        for (var i = 0; i < names.Length; i++)
        {
            if (!Enum.TryParse(names[i], ignoreCase: true, out PlayerClass playerClass)
                || playerClass > PlayerClass.Pyro)
            {
                throw new ArgumentException(
                    $"{flag}: '{names[i]}' is not a class a bot can be. "
                    + "Scout, Pyro, Soldier, Heavy, Demoman, Medic, Engineer, Spy, Sniper, "
                    + "or 'driven' for whatever the loaded policies drive.");
            }

            picked[i] = playerClass;
        }

        return picked;
    }

    /// <summary>
    /// The command line, applied over the defaults and over <c>--config</c>.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Internal so <c>CliCoverageTests</c> can reach it.</b> That test is
    /// the gate this file did not have: every settable <see cref="ServerOptions"/>
    /// property must be reachable from a flag or named as deliberately absent.
    /// Seven of them were not, and nothing went red — the options existed, were
    /// wired to real behaviour, and could only be set from C#.
    /// </remarks>
    internal static ServerOptions Parse(string[] args)
    {
        // ⚠️ The built-in maps are CHECKED IN at maps/ since 2026-09-01, so a
        // clone serves a round with no reference checkout and no arguments; the
        // nav graphs likewise, so bots navigate out of the box. ServerOptions.
        // CreateDefault resolves both, and the panel's save path uses the same
        // factory so the two agree about what "the defaults" are.
        var options = ServerOptions.CreateDefault();

        // ⚠️ Every bot-controller flag is COLLECTED into ServerOptions here and
        // reconciled once, after the switch, by BotBrain.Build. A flag whose
        // effect depends on argument order is the kind of thing nobody notices
        // until a measurement is wrong, and --search-threads before
        // --search-bots was exactly that.

        // The last --web-* settings flag seen, so one given without --web can be
        // refused by name after the loop rather than silently doing nothing.
        string? webSetting = null;

        // ⚠️ Only --bot-mode's "was it given" survives as a local: the ONE
        // inference below — a --policy with no mode means policy mode — has to be
        // able to tell "not given" from "given as scripted", and
        // ServerOptions.BotMode cannot carry that. Everything else the brain
        // needs is now an option, because a config file has to be able to set it
        // and a settings page has to be able to write it back.
        BotMode? botMode = null;

        // Whether --policy appeared on the COMMAND LINE, which is not the same as
        // options.PolicyPaths being non-empty: a config file's [Bots] Policy is a
        // checkpoint named beside a mode in the same file, and inferring a mode
        // from it would silently overrule the Mode key sitting two lines above it.
        var policyOnCommandLine = false;

        // ⛔ --config is applied BEFORE the loop, in its own pass, so a flag
        // always beats the file no matter which side of it the flag appears on.
        // The alternative — handling it in the switch like everything else —
        // makes `--map koth_valley --config gg2.ini` and `--config gg2.ini --map
        // koth_valley` two different servers, and the one that silently loses
        // the flag is the one somebody will write.
        var configPath = ValueOf(args, "--config");

        if (configPath is not null)
        {
            ServerConfigFile.Apply(options, configPath);

            // Remembered, so the panel's settings page has somewhere to write
            // back to and a restart re-reads the same file. ServerOptions.ConfigFile.
            options.ConfigFile = configPath;
        }

        // ⚠️ Latched before the loop for the same reason OpeningMap needs it: a
        // rotation with no --map opens on the rotation, and "no --map" has to
        // survive a config file that set one.
        var mapPinnedOnCommandLine = ValueOf(args, "--map") is not null;

        // ⚠️ COLLECTED, not applied in the switch. `--bots` rewrites MinHumans to
        // 0, so applying this where it is parsed would make
        // `--bots-min-humans 1 --bots 8` and `--bots 8 --bots-min-humans 1` two
        // different servers -- the same order-dependence --search-threads once
        // had, and the reason the brain is built in one place at the end.
        int? botsMinHumans = null;

        var cursor = new ArgCursor(args);

        while (cursor.MoveNext())
        {
            var name = cursor.Current;

            switch (name)
            {
                case "--port":
                    options.Port = cursor.TakeInt();
                    break;
                case "--name":
                    options.ServerName = cursor.Take();
                    break;
                case "--server-player":
                    options.ServerPlayerName = cursor.Take();
                    break;
                case "--map":
                    options.Map = cursor.Take();
                    break;
                case "--password":
                    options.Password = cursor.Take();
                    break;
                case "--players":
                    options.PlayerLimit = cursor.TakeInt();
                    break;
                case "--welcome":
                    options.WelcomeMessage = cursor.Take();
                    break;

                // ⚠️ Not --welcome's twin. That is a MESSAGE_STRING every client
                // draws; this is a line of chat, which only a client running a
                // chat plugin sees at all. Chat is where the player types, so it
                // is where an instruction about a chat command has to land.
                case "--chat-join-message":
                    options.ChatJoinMessage = cursor.Take();
                    break;
                case "--plugins":
                    options.ServerPluginList = cursor.Take();
                    break;
                case "--plugins-required":
                    options.ServerPluginsRequired = true;
                    break;
                case "--votable-maps":
                    // The same "comma-separated names, or a path to a file of
                    // them" shape --rotation takes, because it is the same kind
                    // of list and the reference's own votableMaps.txt is a file.
                    options.VotableMaps = ParseRotation(name, cursor.Take());
                    break;
                case "--maps":
                    options.MapDirectory = cursor.Take();
                    break;

                // Consumed by the pre-pass above; here only so it is not an
                // "unknown option". ⚠️ Do not read it a second time — the pass
                // that applies it has already run and doing it again would let
                // the file overwrite flags given before it.
                case "--config":
                    cursor.Skip();
                    break;

                // A comma-separated list, or the path of a reference-format
                // rotation file (`sampleMapRotation.txt`). ⚠️ A rotation does
                // NOT collapse to one map the way the reference's `-map` does;
                // --map still pins the opening map and the rotation runs after
                // it. ServerOptions.Map says why.
                case "--rotation":
                    options.MapRotation = ParseRotation(name, cursor.Take());
                    break;

                case "--shuffle":
                    options.ShuffleRotation = ParseShuffle(name, cursor.Take());
                    break;

                // ⚠️ Floored at 1 like --cap-limit and --time-limit, so
                // --kill-limit 0 is a one-kill round rather than an endless one.
                // The "0 means no limit" escape belongs to WorldState.
                case "--kill-limit":
                    options.KillLimit = (ushort)cursor.TakeInt();
                    break;

                // ⚠️ NOT floored at 1 — 0 really is "no spawn protection" here,
                // which is the reference's own max(0, ...) and the one limit in
                // this group with nothing to escape.
                case "--tdm-invuln":
                    options.TdmInvulnerabilitySeconds = cursor.TakeInt();
                    break;

                // Either one number for every class, or `scout=1,medic=2`.
                // ⚠️ A limit is PER TEAM, not per server: --class-limit medic=1
                // is one medic each, two on the map.
                case "--class-limit":
                    options.ClassLimits = ParseClassLimits(name, cursor.Take());
                    break;

                case "--autobalance":
                    options.Autobalance = true;
                    break;

                case "--no-autobalance":
                    options.Autobalance = false;
                    break;

                case "--multi-client-limit":
                    options.MultiClientLimit = cursor.TakeInt();
                    break;

                // The bounds that make this safe to expose to the internet.
                // ⚠️ Every default is orders of magnitude above what a stock
                // client uses — ConnectionLimits states the ratio for each — so
                // raising one is not generosity towards real players, it is room
                // for an attacker. They are flags because the right value
                // depends on how much memory the machine has, which this cannot
                // know; the rest of ConnectionLimits has no flag on purpose, and
                // CliCoverageTests records why.
                case "--max-send-backlog":
                    options.Limits = options.Limits with { MaxSendBacklogBytes = cursor.TakeInt() };
                    break;

                case "--max-receive-buffer":
                    options.Limits = options.Limits with { MaxReceiveBufferBytes = cursor.TakeInt() };
                    break;

                case "--max-joining":
                    options.Limits = options.Limits with { MaxJoiningConnections = cursor.TakeInt() };
                    break;

                case "--handshake-deadline":
                    options.Limits = options.Limits with
                    {
                        MaxHandshakeDuration = TimeSpan.FromSeconds(cursor.TakeInt()),
                    };
                    break;

                // ⚠️ Off is a REGRESSION, not a tuning choice: without keepalives
                // a client whose machine loses power holds a roster slot until
                // TCP's own retransmit gives up, which is around fifteen minutes
                // on Linux. It exists because a network that filters keepalives,
                // or a platform where the socket option fails, is a thing that
                // happens — not because anybody should want it.
                case "--no-tcp-keepalive":
                    options.Limits = options.Limits with { TcpKeepAlive = false };
                    break;

                // ⚠️ Not needed under systemd or Docker — both already capture
                // stdout and stderr and already rotate. This is for a
                // deployment with neither. It tees rather than replaces.
                case "--log-file":
                    options.LogFile = cursor.Take();
                    break;

                // ⛔ An address is not a person — see BanList. This keeps an
                // operator's bans across a restart, which a 24/7 server does
                // often enough that losing them silently is the default worth
                // avoiding.
                case "--ban-file":
                    options.BanFile = cursor.Take();
                    break;

                // ⛔ Off unless this is passed, like --record: a durable record
                // of who played on a server is a decision, not a default. What
                // is kept is a salted hash of each address rather than the
                // address, and a country — a US state at most — rather than a
                // place. docs/ANALYTICS.md.
                case "--analytics":
                    options.AnalyticsFile = cursor.Take();
                    break;

                // ⚠️ Nothing ships a database and this reads whatever the
                // operator downloaded. It only matters beside --analytics, and
                // an unreadable file costs the geography rather than the start.
                case "--geoip":
                    options.GeoIpFile = cursor.Take();
                    break;

                // ⚠️ NOT --multi-client-limit. That bounds how many connections
                // one address holds at once; this bounds how fast it opens them.
                case "--connect-rate":
                    options.Limits = options.Limits with { ConnectionBurstPerAddress = cursor.TakeInt() };
                    break;

                case "--connect-rate-window":
                    options.Limits = options.Limits with
                    {
                        ConnectionBurstWindow = TimeSpan.FromSeconds(cursor.TakeInt()),
                    };
                    break;

                case "--log-max-bytes":
                    options.LogMaxBytes = cursor.TakeInt();
                    break;

                case "--log-keep":
                    options.LogKeep = cursor.TakeInt();
                    break;

                // ⚠️ TOTAL across every download in flight, not per client — a
                // second downloader halves the first one's rate.
                case "--map-download-limit":
                    options.MapDownloadBytesPerSecond = cursor.TakeInt();
                    break;

                // The round rules. Without these a round cannot be played to
                // completion in a session — the default clock is fifteen
                // minutes — which is what docs/PLAN.md's phase 6 exit criterion
                // asks for. ⚠️ Both limits are floored at 1 here, exactly as
                // game_init.gml floors the ini, so --time-limit 0 is a ONE-MINUTE
                // round and not an endless one. The "0 means no limit" escape
                // belongs to WorldState and nothing above it; see
                // docs/SIMULATION.md, "Settled: the round".
                case "--time-limit":
                    options.TimeLimitMinutes = (byte)cursor.TakeInt();
                    break;
                case "--cap-limit":
                    options.CapLimit = (byte)cursor.TakeInt();
                    break;
                case "--respawn-time":
                    options.RespawnTimeSeconds = (byte)cursor.TakeInt();
                    break;

                // ⚠️ The nav caches are gitignored in the reference and are not
                // shipped, so a fresh checkout has none — and a server with an
                // ungenerated map in its rotation should keep serving it. A miss is
                // a quiet "these bots do not navigate", never an error.
                case "--botnav":
                    options.NavCacheDirectory = cursor.Take();
                    break;

                // ⚠️ Generation is ON by default and this turns it off, rather
                // than the other way round. Everything a graph needs is in the
                // map's own PNG, so a map with no graph was never missing
                // anything that had to be missing — and the failure it caused,
                // bots that fight whatever reaches them and walk nowhere, reads
                // as a broken bot layer rather than as absent data. The opt-out
                // is here for a server that would rather have a predictable map
                // change than navigating bots on a custom map.
                case "--no-navgen":
                    options.NavGenerate = false;
                    break;

                // Worth having on a rotation that is mostly custom maps, where
                // the alternative is rebuilding the same graphs every time round.
                // Off by default because it writes files the operator did not ask
                // for. ⚠️ A graph REFUSED by the manifest is rewritten either way
                // — that file describes terrain that is not there.
                case "--navgen-save":
                    options.NavGenerateWrite = true;
                    break;

                // ⚠️ ON by default since 2026-09-04. The tables are read from
                // <key>.dist.gz beside the graph; a map without one derives them
                // at the map change instead. Turning them OFF reverts to the
                // weighted straight-line heuristic and moves every route back —
                // it is a different arm, not a slower one. ServerOptions.NavDistances.
                case "--no-nav-distances":
                    options.NavDistances = false;
                    break;

                // Accepted and does nothing: it was the opt-IN before the tables
                // shipped, and a script or a saved command line carrying it must
                // not fail. Removing it would break those for no gain.
                case "--nav-distances":
                    options.NavDistances = true;
                    break;

                // ⛔ A BEHAVIOUR switch, not a performance one. Off, a team's
                // routes stop being pushed apart and match dispersion falls
                // 0.785 -> 0.634 with nothing replacing it. What it buys is a
                // live server that routes the way `eval` and every training
                // rollout already do. ServerOptions.BotOccupancy.
                case "--no-bot-occupancy":
                    options.BotOccupancy = false;
                    break;

                // The [Bots] section. `--bots N` is the fill target, which is what
                // the reference calls FillToPlayers — ⚠️ a total including humans,
                // not a bot count, so a server filling to 8 drops a bot every time
                // somebody joins.
                case "--bots":
                {
                    var fill = cursor.TakeInt();
                    options.Bots = options.Bots with
                    {
                        Enabled = fill > 0,
                        FillToPlayers = fill,
                        MaxBots = fill,

                        // ⚠️ Zero, not the reference's 1. A server started with
                        // --bots is a server somebody wants bots on right now; the
                        // ini's floor of one human exists so a public server does
                        // not look full in the lobby list, and a command line is not
                        // that case.
                        MinHumans = 0,
                    };

                    break;
                }

                // Which of the four shipped brains this server runs -- see BotMode,
                // which carries the per-arm warnings. Replaced --search-bots, a
                // boolean: with one flag per controller the four combinations were
                // reachable only by knowing which pairs to give together.
                //
                // ⚠️ The mode ALONE decides; nothing below infers a controller from
                // another flag being present, except the one documented fallback
                // where --policy is given and no mode is (see the reconcile below).
                case "--bot-mode":
                    botMode = ParseBotMode(name, cursor.Take());
                    break;

                // ⛔ Restores the pre-2026-09-02 behaviour, and it is what every
                // recorded arm measurement was taken under. Without it a bot only
                // hands the wheel to its micro-controller for a HUMAN enemy, so a
                // bot-vs-bot fight on a live server stays on the scripted layer --
                // which is a cost saving, not a skill one. ⚠️ A measurement taken
                // WITHOUT this flag and without humans present is measuring the
                // scripted layer whatever --bot-mode said.
                case "--bot-engages-bots":
                    options.BotEngagesBotsToo = true;
                    break;

                // A debugging aid: watch a brain change hands from inside the
                // game rather than off the panel. Accepted under every bot mode,
                // including one with no controller to hand over to — it is
                // diagnostic, and refusing it would make flipping between modes
                // mean editing the command line twice.
                case "--bot-handover-bubbles":
                    options.BotHandoverBubbles = true;
                    break;

                // How many HUMANS must be connected before any bot is spawned.
                // ⚠️ Below it the target is ZERO rather than the fill, so 1 is
                // "only ever run bots when somebody is actually playing" -- which
                // is what an empty public server wants, since a server full of
                // bots looks full in the lobby list and is not.
                case "--bots-min-humans":
                    botsMinHumans = cursor.TakeInt();
                    break;

                // How long `/bots off` takes the bot population away for.
                // ⚠️ ZERO, the default, means the chat command does not exist —
                // one knob rather than a flag and a duration that can disagree.
                case "--bot-suspend-minutes":
                    options.BotSuspendMinutes = cursor.TakeInt();
                    break;

                // ⚠️ ON by default. An empty server has no characters, no clients
                // and nobody to notice, so 30 wakeups a second buys nothing; this
                // turns that off. The round clock slows with the tick rate, which
                // is deliberate -- see ServerOptions.IdleWhenEmpty.
                case "--no-idle-when-empty":
                    options.IdleWhenEmpty = false;
                    break;

                // ⛔ Pins the controller budget at --bot-arbiter instead of letting
                // the server walk it down when ticks crowd their deadline. The
                // budget's units come from a cost measured on ONE machine; this
                // flag is for when you would rather have the number you set than
                // the number this machine can afford.
                case "--fixed-bot-budget":
                    options.AdaptiveBotBudget = false;
                    break;

                // ⚠️ Threads change NO decision -- RolloutThreadingTests gates that
                // at 2, 4 and 8 -- so this is a pure cost knob. It is also the one
                // place this server is not single-threaded, and the pool it uses is
                // the process's: a burst inside a tick takes cores from nothing else
                // here, but it is not free on a shared machine.
                case "--search-threads":
                    options.SearchThreads = cursor.TakeInt();
                    break;

                // MICROSECONDS of controller work per tick. ⛔ REQUIRED when more
                // than one controller is configured, because without an arbiter the
                // first tier always drives and the second would be dead weight
                // nobody could see.
                //
                // ⚠️ The per-decision COSTS are measured (policy 7 us, search 4800
                // at twelve live characters); the BUDGET is not tuned. The tick is
                // 33,300 us, so 10000 caps concurrent search decisions at two.
                // Watch --tick-stats.
                //
                // ⛔ The arbiter reads the TICK, never a clock: a match with one in
                // it still replays from its seed. See IMicroArbiter.
                case "--bot-arbiter":
                    options.BotArbiterMicroseconds = cursor.TakeInt();
                    break;

                // docs/BOTS.md's arm 2: gg2-rl's trained checkpoint driving keys and
                // aim inside the same engagement window --search-bots uses.
                //
                // ⛔ REPEATABLE, and the second one APPENDS rather than replacing —
                // one checkpoint is one tier, and the shipped bundle is two of them
                // covering five classes between them. ⚠️ The first --policy on the
                // command line CLEARS whatever a config file named, so `--config
                // gg2.ini --policy x.npz` runs x.npz alone rather than x.npz appended
                // to the file's list: a flag beats the file, and a flag that ADDED to
                // it would be the one setting here that cannot override.
                case "--policy":
                {
                    var checkpoint = cursor.Take();

                    options.PolicyPaths = policyOnCommandLine
                        ? [.. options.PolicyPaths, checkpoint]
                        : [checkpoint];

                    policyOnCommandLine = true;
                    break;
                }

                // ⛔ BYPASSES THE SIMULATION STAMP, which exists to stop a policy
                // running against a simulation it was not trained on. It is honest
                // ONLY when the difference between the two simulations cannot reach
                // the maps being played, and that is a claim to CHECK rather than
                // assume: the 2026-09-02 case was the moving-platform carry, and
                // MovingPlatforms.Build reads the `moving_platform` entity, which
                // appears in ZERO shipped maps.
                //
                // ⚠️ The server prints both stamps and the reason when this is used,
                // because a bypassed guard that leaves no trace is worse than no
                // guard: the next reader cannot tell the run was overridden.
                case "--policy-ignore-stamp":
                    options.PolicyIgnoreStamp = true;
                    break;

                // ⚠️ WHICH CLASSES BOTS PLAY, and SEPARATE from which controller
                // drives one — that is --bot-class-policy below. A controller engages
                // only on the classes it drives, so a server drawing all nine runs a
                // MIX of the hybrid arm and the all-scripted one and a playtester
                // cannot tell which bot they just fought. Restricting the draw is what
                // makes every bot the configuration under test. Say which you ran.
                //
                // ⛔ "driven" is RESOLVED IN BotBrain.Build, not here, because here
                // the answer does not exist yet: flags are parsed before any --policy
                // is opened and argument order is not guaranteed. That deferral is the
                // fix for a real bug — the word used to be "trained" and used to mean
                // a compile-time set, which on the shipped bundle silently dropped
                // pyro and left its tier never driving. "trained" is still accepted.
                case "--bot-classes":
                {
                    var text = cursor.Take();

                    var followsPolicy =
                        string.Equals(text, ServerSettings.DrivenClasses, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(text, "trained", StringComparison.OrdinalIgnoreCase);

                    options.Bots = options.Bots with
                    {
                        AllowedClasses = followsPolicy ? null : ParseClasses(name, text),
                        ClassDrawFollowsPolicy = followsPolicy,
                    };

                    break;
                }

                // ⛔ WHICH CONTROLLER DRIVES A CLASS: Class=name, where name is a
                // checkpoint's file name without its extension, or "scripted".
                // Repeatable and APPENDS, so a class per flag reads the way an
                // operator thinks about it; a single flag may also carry a
                // comma-separated list, because the config key is one line.
                //
                // ⚠️ It does not need to name every class. Anything unmentioned keeps
                // the default — whichever loaded checkpoint's own set claims it — so
                // this only ever has to be written to OVERRIDE one, which is what
                // keeps "the policies drive their intended classes" the thing that
                // happens when nobody configures anything.
                case "--bot-class-policy":
                    options.BotClassPolicies =
                    [
                        .. options.BotClassPolicies,
                        .. cursor.Take().Split(
                            ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    ];

                    break;

                case "--bot-difficulty":
                    options.Bots = options.Bots with { Difficulty = cursor.TakeInt() };
                    break;

                // docs/BOTAI.md item 3, the only item on this project with a
                // deadline: the broadcast stream already carries every player's
                // keys and aim every tick, so recording a game with people in it
                // is a labelled dataset of human action arriving for free. Off
                // unless asked for — recording people is a decision, not a
                // default.
                case "--record":
                    options.RecordingDirectory = cursor.Take();
                    break;

                // ⚠️ Loopback only, and off unless this is passed — it executes
                // commands against a live game, which is the same reason the GM8
                // bridge is off by default. A port of 0 asks the OS for a free
                // one and the server logs which it got.
                // Pacing telemetry. Safe with clients connected — unlike the
                // two switches around it, it changes nothing the simulation can
                // see.
                case "--tick-stats":
                    options.TickStats = true;
                    break;

                // ⚠️ The only switch that reaches outside this machine. Opt-in,
                // never a default: it publishes this server's address, name,
                // map and player count to ganggarrison.com every 30 seconds.
                case "--lobby":
                    options.AnnounceToLobby = true;
                    break;

                // ⚠️ The other switch that reaches outside this machine, and the
                // one that sends something ABOUT A PLAYER rather than about the
                // server: it relays each client's reward id to the auth server.
                case "--rewards":
                    options.Rewards = true;
                    break;

                case "--auth-server":
                {
                    var (host, port) = ParseHostPort(name, cursor.Take());
                    options.AuthServerHost = host;
                    options.AuthServerPort = port;
                    break;
                }

                case "--control-port":
                    options.ControlPort = cursor.TakeInt();
                    options.ControlPortEnabled = true;
                    break;

                // ⚠️ The one surface that can be reachable from the network, so
                // every flag around it defaults to the safe answer: loopback,
                // and a password generated and printed rather than absent.
                // WebAdminOptions and WebAdminServer have the argument.
                case "--web":
                    options.Web = options.Web with
                    {
                        Enabled = true,
                        Port = cursor.TakeInt(),
                    };
                    break;

                // ⚠️ These four SETTLE the panel and do not turn it on. Only
                // --web (or a [Web] Port in the config) does that, and a settings
                // flag with no panel to configure is REFUSED below rather than
                // ignored — the same rule --policy without a mode that consults
                // it follows. Silently accepting `--web-bind 0.0.0.0` on a server
                // with no panel is a flag that does nothing, which is the exact
                // bug CliCoverageTests exists for.
                case "--web-bind":
                    webSetting = name;
                    options.Web = options.Web with { Bind = cursor.Take() };
                    break;

                case "--web-password":
                    webSetting = name;
                    options.Web = options.Web with { Password = cursor.Take() };
                    break;

                case "--web-tls":
                    webSetting = name;
                    options.Web = options.Web with { CertificatePath = cursor.Take() };
                    break;

                case "--web-tls-password":
                    webSetting = name;
                    options.Web = options.Web with { CertificatePassword = cursor.Take() };
                    break;

                // ⚠️ Pacing only. Nothing about the simulation is rate-dependent
                // — every duration in the game is counted in ticks — so this
                // changes throughput and nothing else. It must stay at 1 on any
                // server a client is connected to: clients pace themselves at
                // 30 Hz and re-simulate between updates, so an unpaced server
                // desynchronises every one of them at once.
                case "--speed":
                {
                    var text = cursor.Take();

                    options.SpeedMultiplier = text.Equals("max", StringComparison.OrdinalIgnoreCase)
                        ? double.PositiveInfinity
                        : double.TryParse(text, CultureInfo.InvariantCulture, out var speed)
                            ? speed
                            : throw new ArgumentException($"{name} needs a number or 'max', not '{text}'");

                    break;
                }
                default:
                    throw new ArgumentException($"unknown option '{name}'");
            }
        }

        // ⚠️ `--map` beats a rotation; without one the rotation picks the opening
        // map, which is GameServerCreate.gml:110-113. Expressed by emptying Map
        // rather than by a second option, because "which map do we open on" then
        // has ONE answer in one place — see ServerOptions.Map and OpeningMap.
        if (!mapPinnedOnCommandLine && options.MapRotation.Count != 0)
            options.Map = string.Empty;

        // ⛔ Refused rather than ignored. Also after the loop, so the order the
        // flags appear in does not matter and a [Web] Port in --config counts as
        // having turned the panel on.
        if (webSetting is not null && !options.Web.Enabled)
        {
            throw new ArgumentException(
                $"{webSetting} configures the web admin panel and none is being hosted. "
                + "Add --web <port> to host one, or drop the flag.");
        }

        // ⛔ The same rule one feature over: --geoip is only ever consulted by
        // the analytics store, so on its own it opens a file, holds a handle and
        // answers nobody. After the loop, so the flags' order does not matter and
        // an [Analytics] File in --config counts as having turned it on.
        if (options.GeoIpFile.Length != 0 && options.AnalyticsFile.Length == 0)
        {
            throw new ArgumentException(
                "--geoip supplies geography for the player analytics and none are being kept. "
                + "Add --analytics <path> to keep them, or drop the flag.");
        }

        // ⚠️ AFTER the loop, so it beats --bots's own MinHumans = 0 wherever the
        // two appear relative to each other.
        if (botsMinHumans is { } minHumans)
            options.Bots = options.Bots with { MinHumans = minHumans };

        // ⛔ ONE PLACE BUILDS THE BRAIN, at the end, from settings collected
        // above -- and that place is BotBrain.Build, shared with the config file.
        // The flag handlers used to write BotPopulationOptions.SearchMicro
        // directly, which made --search-threads order-dependent: given BEFORE
        // --search-bots it was picked up, given after it had to re-write the
        // option it had just found. Collecting first and building once removes
        // the ordering entirely.
        //
        // ⚠️ THE ONE INFERENCE, and it exists to keep `--policy <path>` working on
        // its own the way docs/BOTS.md documents it: a --policy with no --bot-mode
        // means policy mode. It is order-independent because both were collected
        // before this line, and it applies ONLY when no --bot-mode was given -- an
        // explicit one always wins, and a --policy it cannot use is refused by
        // BotBrain.Build rather than quietly ignored.
        //
        // ⚠️ It reads policyOnCommandLine, NOT options.PolicyPaths. A checkpoint
        // named by the config file sits beside that file's own Mode key, and
        // inferring a mode from it would overrule the line two above it.
        options.BotMode = botMode
            ?? (policyOnCommandLine ? BotMode.Policy : options.BotMode);

        BotBrain.Build(options, Console.WriteLine);

        // ⚠️ Which settings a save from the admin panel will NOT be able to change,
        // computed by diffing what was built against what the file alone would
        // have produced. A flag beats the file — deliberately — so without this
        // the panel would accept an edit to a flag-pinned setting, write it
        // correctly, restart, and change nothing.
        // ServerOptions.CommandLineOverrides has the argument.
        options.CommandLineOverrides = OverriddenSettings(options, configPath);

        // ⛔ HERE, not only in the GameServer constructor. Validate throws
        // ArgumentOutOfRangeException, and construction happens after this
        // method returns — so `--players 100` reached the user as an unhandled
        // exception and a stack trace instead of the message Validate wrote for
        // them. Inside Parse it lands in Main's catch, which prints the message
        // and the usage. The constructor still validates, for callers that do
        // not come through here.
        options.Validate();

        return options;
    }

    /// <summary>
    /// The settings whose final value differs from what
    /// <paramref name="configPath"/> alone would have given.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A diff rather than a record of which flags were seen</b>, because the
    /// two are not the same set and the diff is the one that matters: a flag
    /// given the value the file already had changes nothing and should not be
    /// warned about, and <c>--rotation</c> with no <c>--map</c> silently empties
    /// <see cref="ServerOptions.Map"/>, which no per-flag record would notice.
    /// </para>
    /// <para>
    /// ⚠️ <b>Compared through <see cref="ServerSettings"/>'s own text form</b>, so
    /// it asks exactly the question the settings page asks: would saving this
    /// value and restarting produce a different server. Anything not in that
    /// table cannot be edited from the page and so cannot be shadowed on it.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<string> OverriddenSettings(
        ServerOptions options, string? configPath)
    {
        var fromFile = ServerOptions.CreateDefault();

        if (configPath is not null)
        {
            try
            {
                ServerConfigFile.Apply(fromFile, configPath);
            }
            catch (Exception e) when (e is IOException or FormatException)
            {
                // Unreachable: the same file was applied without complaint at the
                // top of Parse. Answering "nothing is overridden" rather than
                // failing the start keeps a diagnostic from being the thing that
                // stops a server booting.
                return [];
            }
        }

        // ⚠️ The baseline needs no rotation fix-up of its own: it is "what this
        // server would be with ONLY --config", that server has no --map, and
        // ServerConfigFile.Apply already empties Map for a file that carries a
        // rotation and no Map key. A --rotation FLAG that empties it here is a
        // genuine override and is meant to be reported as one.
        var overridden = new List<string>();

        foreach (var setting in ServerSettings.All)
        {
            if (setting.Get(fromFile) != setting.Get(options))
                overridden.Add(setting.Id);
        }

        return overridden;
    }

    internal static void PrintUsage() => Console.WriteLine(
        """
        gg2-server - a headless Gang Garrison 2 server

          --port <n>        listen port (default 8190)
          --name <text>     server name shown to clients
          --server-player <text>
                            name of the reserved roster slot 0 (default Server)
          --map <name>      built-in map to serve (default ctf_truefort)
          --password <text> require a password
          --players <n>     seats for clients, 1..48 (default 10)
          --welcome <text>  message sent to each joiner
          --chat-join-message <text>
                            a CHAT line sent to each player as they join chat.
                            Needs a chat plugin; a client without one never sees
                            it. Not --welcome, which every client draws.
          --plugins <list>  server-sent plugins to ask clients to load,
                            comma-separated (e.g. chat, chat_v2_pr)
          --plugins-required
                            clients must load them to join
          --votable-maps <list|path>
                            maps a plugin map vote may offer (default: the
                            rotation, else every map in --maps)
          --multi-client-limit <n>
                            connections allowed from one address (default 3);
                            loopback is exempt
          --max-send-backlog <bytes>
                            queued bytes one client may leave unread before it is
                            dropped (default 1048576). This is the bound on what a
                            client that stops reading can make the server hold.
          --max-receive-buffer <bytes>
                            unread bytes one client may have buffered before it is
                            dropped (default 262144)
          --max-joining <n> half-finished handshakes held at once (default 128)
          --handshake-deadline <seconds>
                            longest a connection may take to finish joining,
                            however active it is (default 600). Separate from the
                            30s idle timeout, which activity resets and this does
                            not.
          --no-tcp-keepalive
                            do not probe for peers that vanished. A REGRESSION:
                            without it a client whose machine dies holds a roster
                            slot until TCP gives up, ~15 minutes on Linux.
          --ban-file <path> keep operator bans here, so they survive a restart.
                            Without it a ban is in memory only. An address is
                            NOT a person - see docs/HARDENING.md before relying
                            on bans at all.
          --connect-rate <n>
                            connections one address may open back to back before
                            it waits (default 10, 0 disables). NOT
                            --multi-client-limit, which is how many it holds AT
                            ONCE; this is how fast it opens them.
          --connect-rate-window <seconds>
                            how long refilling --connect-rate takes (default 60)
          --log-file <path> ALSO write the log to this file, rotating it by size.
                            Not needed under systemd or Docker, which capture
                            stdout/stderr and rotate already. The console sink
                            stays on either way.
          --log-max-bytes <n>
                            rotate --log-file past this size (default 16777216)
          --log-keep <n>    rotated files kept beside the live one (default 5);
                            0 rotates by deleting
          --maps <path>     override the built-in map PNGs (default: maps/)
          --map-download-limit <n>
                            TOTAL bytes/second across all custom-map downloads
                            in flight, not per client (default 50000)
          --config <path>   read a reference-format gg2.ini. EVERY setting this
                            server has can be set from it — the reference's
                            [Settings], [Server], [Maps] and [Classlimits], plus
                            [Limits], [Log], [Nav], [Bots], [Web] and [Runtime];
                            see docs/CONFIG.md. Applied BEFORE every flag, so a
                            flag always wins wherever it appears. Written back
                            only when the --web admin panel saves.
          --rotation <list|path>
                            the map rotation: comma-separated names, or the path
                            of a rotation file (one map per line, # comments).
                            Without --map the server opens on its first entry.
          --shuffle <mode>  none (default), arena-last (shuffle, but do not open
                            on an arena map), or any. Shuffled once at startup.
          --time-limit <n>  round clock in minutes, 1..255 (default 15)
          --cap-limit <n>   captures to win, 1..255 (default 5)
          --respawn-time <n>
                            seconds a death lasts (default 5)
          --kill-limit <n>  kills to win a deathmatch round, 1..65535
                            (default 30)
          --tdm-invuln <n>  seconds of spawn protection in team deathmatch
                            (default 5; 0 is none, and is allowed)
          --class-limit <n|list>
                            one limit for every class, or per class as
                            'medic=1,spy=0'. PER TEAM, not per server.
                            255 is unlimited and 0 bans the class; a limit at or
                            above --players is stored as unlimited, as the
                            reference stores it.
          --no-autobalance  do not move a player across when one team is two
                            larger (--autobalance turns it back on; on by
                            default)
          --bots <n>        fill the server to n players with bots (0 = off)
          --bot-difficulty <1-5>
                            how good they are (default 5, the top tier). The
                            lower tiers are a handicap ladder - aim error, an
                            acquire delay, a hold-fire roll - so pass a lower
                            one for beatable bots.
          --bot-mode <mode> which brain the bots run (default: scripted):
                              scripted (0)  the scripted layer alone. A complete
                                            bot, and the control every comparison
                                            is made against.
                              search   (1)  + a rollout search inside the
                                            engagement window. Better on death
                                            share and MUCH more expensive; never
                                            yet measured with humans in the
                                            server.
                              policy   (2)  + a trained checkpoint. Needs --policy.
                              tiered   (3)  both, most expensive first, with an
                                            arbiter choosing between them. Needs
                                            --policy; defaults --bot-arbiter to
                                            10000.
                            Replaces --search-bots. --policy on its own still
                            means policy mode.
          --search-threads <n>
                            threads the search splits its candidates across
                            (default: cores/3, capped at 8). Changes cost only,
                            never a decision.
          --policy <path>   the trained checkpoint (.npz) --bot-mode policy and
                            --bot-mode tiered run - docs/BOTS.md arm 2. Given on
                            its own it selects policy mode. Given to a mode that
                            cannot consult it, it is REFUSED rather than ignored.
          --bot-arbiter <us>
                            per-tick budget in MICROSECONDS of controller work,
                            shared by every bot, deciding which controller may
                            spend a decision when several bots engage on the same
                            tick. Cheaper tiers, then the scripted layer, once it
                            runs out. Defaults to 10000 under --bot-mode tiered,
                            which is the only mode with more than one tier.
                            A tick is 33300us; a search decision measures ~4800 at
                            12 live characters and a policy decision ~7.
          --policy-ignore-stamp
                            load a checkpoint whose simulation stamp does NOT
                            match this build. Only honest when the simulation
                            difference cannot reach the maps being played, which
                            is a claim to check. Both stamps are always printed.
          --bots-min-humans <n>
                            humans who must be connected before any bot spawns
                            (default 0 with --bots). Below it the target is ZERO
                            rather than the fill, so 1 means "only run bots when
                            somebody is actually playing".
          --bot-suspend-minutes <n>
                            how long the /bots off chat command takes the bots
                            away for, 0..1440. 0 (the default) means the command
                            does not exist on this server.
          --fixed-bot-budget
                            keep the bot controller budget pinned at
                            --bot-arbiter. By default the server walks it down
                            when ticks crowd their deadline and back up when they
                            do not, because the budget's units come from a cost
                            measured on one machine and yours is a different one.
                            Every change is logged.
          --no-idle-when-empty
                            keep ticking at full rate with nobody connected.
                            By default a server with no clients, no joiners and
                            no bots drops to 2Hz until someone arrives, which
                            also slows its round clock. An explicit --speed
                            always wins over idling.
          --bot-handover-bubbles
                            bots put up a chat bubble whenever their brain
                            changes hands: z1 a policy took the wheel, z3 the
                            search did, z2 the scripted layer took it back. A
                            DEBUGGING aid -- every bubble is a broadcast to every
                            client, and it changes nothing a bot decides.
          --bot-engages-bots
                            let bots run their expensive micro-controller against
                            each other too. Without this a bot only hands the
                            wheel to --bot-mode's controller for a HUMAN enemy and
                            a bot-vs-bot fight stays scripted, which is a COST
                            saving and not a skill one. Every recorded arm
                            measurement was taken with this behaviour.
          --bot-classes <list|driven>
                            restrict which classes bots PLAY (default: the
                            reference's own draw of eight). 'driven' is whatever
                            the loaded policies drive, resolved at startup - pass
                            it to make EVERY bot the configuration under test
                            rather than a mix of that and the all-scripted arm.
                            'trained' is accepted as the old spelling.
          --bot-class-policy <Class=name,...>
                            choose who drives a class: a checkpoint's file name
                            without its extension, or 'scripted'. Repeatable, and
                            it only has to name the ones you are OVERRIDING -
                            anything unmentioned is driven by whichever loaded
                            checkpoint's own class set claims it, and is scripted
                            if none does. e.g. --bot-class-policy Soldier=scripted
          --botnav <path>   override the bot nav graphs (default: nav/)
          --no-navgen       do not build a nav graph for a map that has none.
                            On by default, and it is what makes a CUSTOM map
                            navigable: everything a graph needs is in the map
                            PNG, so a missing one costs a few hundred ms at the
                            map change rather than a round of motionless bots.
          --navgen-save     save a graph built that way into --botnav, so the
                            next map change reads it (default: memory only)
          --no-bot-occupancy
                            stop bots pricing edges their team-mates are walking
                            above cost. ON by default and it is the biggest
                            source of route variety there is - without it a team
                            bunches up (dispersion 0.785 -> 0.634) and nothing
                            replaces it. What it buys is a live server that
                            routes exactly as eval and the training rollouts do.
          --no-nav-distances
                            do not give A* the exact precomputed distances. Bots
                            fall back to the weighted straight-line heuristic,
                            which is ~8% of a twelve-bot tick slower and takes
                            DIFFERENT routes. --nav-distances is accepted and is
                            now the default.

          --speed <n|max>   run at n times real time, or 'max' for unpaced
                            (default 1)
          --record <path>   write replayable .trace recordings of this server's
                            broadcast stream to a directory (default: off)
          --analytics <path>
                            count who plays here and keep it across restarts:
                            unique players all time and per day, playtime, peak
                            concurrency, and where they connect from (default:
                            off). Addresses are stored as salted hashes, never
                            as addresses. Shown in the panel's Analytics view.
          --geoip <path>    a MaxMind .mmdb to resolve addresses with, for the
                            analytics map (default: off). None ships with this
                            server; docs/ANALYTICS.md names two free ones. A
                            country-level database resolves no US states.
                            ⚠ Given without --analytics it is refused rather than
                            ignored: on its own it has nothing to resolve for.
          --control-port <n>
                            open the loopback control port (default: off;
                            0 asks the OS for a free port)
          --web <n>         host the web admin panel on port n (default: off;
                            0 asks the OS for a free port). Shows the roster, the
                            live map, the round and the log, and drives freeze,
                            step, speed, bot count, map change, teleport and kick
                            — the same verbs as --control-port, through a browser.
                            ⚠ Binds 127.0.0.1 unless --web-bind says otherwise, so
                            this alone is NOT remotely reachable.
          --web-bind <addr> the address the panel listens on (default 127.0.0.1).
                            Like every --web-* setting below, it CONFIGURES the
                            panel and does not turn one on; given without --web it
                            is refused rather than ignored.
                            0.0.0.0 accepts connections from anywhere, which is
                            what makes it a REMOTE admin panel and what makes
                            --web-tls matter.
          --web-password <text>
                            the panel's password. There is no way to turn
                            authentication off: without this the server generates
                            one and prints it at startup, so an unattended panel
                            is never open. Five wrong guesses from an address
                            start a lockout that doubles.
          --web-tls <path>  serve the panel over HTTPS with this PKCS#12
                            (.pfx) certificate. ⚠ Without it, on a non-loopback
                            bind, the admin password and every session cookie
                            cross the network in the clear — a reverse proxy or an
                            SSH tunnel is the other answer.
          --web-tls-password <text>
                            the certificate's password, if it has one
          --tick-stats      log a histogram of tick intervals every 30s and at
                            shutdown; safe with clients connected
          --lobby           list this server publicly at ganggarrison.com, so it
                            appears in the client's server browser (default: off).
                            Sends this server's address, name, map and player
                            count there every 30 seconds until it shuts down.
          --rewards         let players authenticate their Haxxy badges, hats and
                            capes so everyone sees them (default: off). Relays
                            each player's reward id to the auth server, which is
                            a third party; with it off nobody's cosmetics show
                            and nothing else changes.
          --auth-server <host[:port]>
                            where --rewards authenticates
                            (default: ganggarrison.com:45022)
          --version         print the build - version, commit, runtime and
                            architecture - and exit. Running it on a machine you
                            have just copied the binary to is what proves the
                            copy executes there; a missing .NET runtime fails
                            here rather than at the next restart.

        The built-in maps — 22 shipped plus the gg_debug2 debug range — are
        checked in at maps/ and are found automatically, so a clone serves a
        round with no arguments and no reference checkout.
        --maps, or GG2_MAPS, overrides that; GG2_REFERENCE is still honoured last
        for anyone deliberately serving a Gang-Garrison-2 checkout's own copies.

        --speed is pacing and nothing else: every duration in the game is
        counted in ticks, so the simulation is identical at any speed. Leave it
        at 1 whenever a client is connected — clients pace themselves at 30 Hz
        and re-simulate between updates, so an unpaced server desynchronises
        every one of them at once. It is for a headless server nobody is
        watching, which is what phase 8 is about.

        --time-limit, --cap-limit and --kill-limit are floored at 1, exactly as
        the reference's own ini is (game_init.gml:96, 101, 103), so a zero is a
        one-minute round rather than an endless one. There is no "no limit" —
        for a long session pass a large number. --tdm-invuln is the exception
        and really does accept 0, because the reference floors that one at 0.

        Without --rotation or a --config carrying one, the rotation is empty and
        the server replays --map for ever. That is NOT what the reference does:
        a stock dedicated server with no -map plays the twenty-map [Maps]
        rotation, which is what `--config gg2.ini` reproduces here.

        --bots is a FILL target including humans, not a bot count: a server
        filling to 8 with 3 people on it runs 5 bots. The nav graphs are checked
        in at nav/ and found automatically, so bots walk without --botnav. With
        no graphs at all they still fight whatever comes to them and go nowhere,
        which is deliberate rather than an error.

        --record writes what every client saw, which already contains every
        player's keys and aim on every tick — so a recording of a game with
        people in it is training data as well as a parity fixture, and it is the
        same format gg2-parity replays. It rolls a new file per map and caps
        itself at a gigabyte. See docs/BOTAI.md item 3.

        `gg2-server eval` plays bot matches across maps instead of serving one,
        faster than real time and with nobody watching - phase 8's exit
        criterion. See `gg2-server eval --help`.

        --control-port opens a line-of-JSON control surface on 127.0.0.1 only:
        read state, freeze, step N ticks, set the speed or the bot count, change
        the map, teleport, and run any command a client could. It is off unless
        asked for, because it executes commands against a live game.

        The world is simulated: characters, weapons, all eight gamemodes and the
        map rotation. See docs/PLAN.md.
        """);

    /// <summary>
    /// Walks the argument list so a flag that takes a value can consume it
    /// without every case in the switch carrying its own <c>i++</c>.
    /// </summary>
    private sealed class ArgCursor(string[] args)
    {
        private int _index = -1;

        /// <summary>The argument the switch is looking at — the flag.</summary>
        public string Current => args[_index];

        public bool MoveNext() => ++_index < args.Length;

        /// <summary>Consumes the flag's value, or refuses by the flag's name.</summary>
        public string Take()
        {
            if (_index + 1 >= args.Length)
                throw new ArgumentException($"{Current} needs a value");

            return args[++_index];
        }

        public int TakeInt()
        {
            var flag = Current;
            var text = Take();

            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : throw new FormatException($"{flag} needs a number, got '{text}'");
        }

        /// <summary>Steps over a value that was consumed by an earlier pass.</summary>
        public void Skip()
        {
            if (_index + 1 < args.Length)
                _index++;
        }
    }
}
