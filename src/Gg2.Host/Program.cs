using System.Globalization;
using Gg2.Bots;
using Gg2.Protocol;
using Gg2.Server;
using Gg2.Simulation;

namespace Gg2.Host;

internal static class Program
{
    private sealed class ConsoleLog : IServerLog
    {
        public void Info(string message) =>
            Console.WriteLine($"{DateTime.Now:HH:mm:ss} {message}");
    }

    private static int Main(string[] args)
    {
        // `gg2-server eval ...` plays bot matches instead of serving one —
        // phase 8's exit criterion. A subcommand rather than a flag, because it
        // shares almost none of the server's options and every one it did share
        // would have to mean the same thing in both.
        if (args.Length > 0 && args[0] == "eval")
        {
            var rest = args[1..];

            if (rest.Contains("--help") || rest.Contains("-h"))
            {
                Evaluate.PrintUsage();
                return 0;
            }

            try
            {
                return Evaluate.Run(rest);
            }
            catch (Exception e) when (e is ArgumentException or FormatException or IOException)
            {
                Console.Error.WriteLine(e.Message);
                Console.Error.WriteLine();
                Evaluate.PrintUsage();
                return 2;
            }
        }

        if (args.Contains("--help") || args.Contains("-h"))
        {
            PrintUsage();
            return 0;
        }

        ServerOptions options;
        try
        {
            options = Parse(args);
        }
        catch (Exception e) when (e is ArgumentException or FormatException)
        {
            Console.Error.WriteLine(e.Message);
            Console.Error.WriteLine();
            PrintUsage();
            return 2;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            // ⚠️ No usage text for these two. A missing config file or a missing
            // maps/ directory is not a mistake in the command line, and burying
            // the one sentence that says which file under sixty lines of flags
            // is how a clear error becomes an unread one.
            Console.Error.WriteLine(e.Message);
            return 2;
        }

        var log = new ConsoleLog();

        // ⚠️ INSIDE the try. ServerOptions.Validate runs in the GameServer
        // constructor, so `--players 100` used to leave an unhandled
        // ArgumentOutOfRangeException and a stack trace where the usage text
        // belongs — the parse succeeded and nothing above this caught what came
        // after it. Parse now validates too; this is the second net, for a
        // caller that builds options some other way.
        using var server = new GameServer(options, log);

        try
        {
            server.Start();
        }
        catch (Exception e) when (
            e is IOException or FileNotFoundException or ArgumentException or InvalidOperationException)
        {
            // ⚠️ InvalidOperationException is here for the plugin loader, whose
            // failures are all operator errors — a name the site does not have, a
            // hash it will not serve, a list too long for the greeting. Without
            // it, mistyping --plugins printed a stack trace where the message
            // belongs, and the message is the useful half (it names the URL and
            // says plugin names are lowercase).
            Console.Error.WriteLine($"unable to start: {e.Message}");
            return 1;
        }

        using var stopping = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            log.Info("stopping");
            stopping.Cancel();
        };

        server.Run(stopping.Token);
        return 0;
    }

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
                    + "or 'trained' for the four the policy was trained on.");
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
        var options = new ServerOptions
        {
            // ⚠️ The built-in maps are CHECKED IN at maps/ since 2026-09-01, so a
            // clone serves a round with no reference checkout and no arguments.
            // This used to hard-code one machine's path to a fork. MapDirectory.
            MapDirectory = MapDirectory.Resolve(),

            // ⚠️ The nav graphs are checked in at nav/ since 2026-09-01, so bots
            // navigate out of the box in a clone. Empty stays legal: a server
            // with no graphs runs bots that fight but do not walk.
            NavCacheDirectory = MapDirectory.ResolveNav(),
        };

        // ⚠️ Every bot-controller flag is COLLECTED here and reconciled once,
        // after the switch. A flag whose effect depends on argument order is the
        // kind of thing nobody notices until a measurement is wrong, and
        // --search-threads before --search-bots was exactly that.
        var searchThreads = Math.Clamp(Environment.ProcessorCount / 3, 1, 8);

        string? policyPath = null;
        var ignoreStamp = false;
        BotMode? botMode = null;
        int? arbiterUnits = null;

        // ⛔ --config is applied BEFORE the loop, in its own pass, so a flag
        // always beats the file no matter which side of it the flag appears on.
        // The alternative — handling it in the switch like everything else —
        // makes `--map koth_valley --config gg2.ini` and `--config gg2.ini --map
        // koth_valley` two different servers, and the one that silently loses
        // the flag is the one somebody will write.
        var configPath = ValueOf(args, "--config");

        if (configPath is not null)
            ServerConfigFile.Apply(options, configPath);

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

        for (var i = 0; i < args.Length; i++)
        {
            var name = args[i];
            var value = i + 1 < args.Length ? args[i + 1] : null;

            switch (name)
            {
                case "--port":
                    options.Port = RequireInt(name, value);
                    i++;
                    break;
                case "--name":
                    options.ServerName = Require(name, value);
                    i++;
                    break;
                case "--server-player":
                    options.ServerPlayerName = Require(name, value);
                    i++;
                    break;
                case "--map":
                    options.Map = Require(name, value);
                    i++;
                    break;
                case "--password":
                    options.Password = Require(name, value);
                    i++;
                    break;
                case "--players":
                    options.PlayerLimit = RequireInt(name, value);
                    i++;
                    break;
                case "--welcome":
                    options.WelcomeMessage = Require(name, value);
                    i++;
                    break;
                case "--plugins":
                    options.ServerPluginList = Require(name, value);
                    i++;
                    break;
                case "--plugins-required":
                    options.ServerPluginsRequired = true;
                    break;
                case "--votable-maps":
                    // The same "comma-separated names, or a path to a file of
                    // them" shape --rotation takes, because it is the same kind
                    // of list and the reference's own votableMaps.txt is a file.
                    options.VotableMaps = ParseRotation(name, Require(name, value));
                    i++;
                    break;
                case "--maps":
                    options.MapDirectory = Require(name, value);
                    i++;
                    break;

                // Consumed by the pre-pass above; here only so it is not an
                // "unknown option". ⚠️ Do not read it a second time — the pass
                // that applies it has already run and doing it again would let
                // the file overwrite flags given before it.
                case "--config":
                    i++;
                    break;

                // A comma-separated list, or the path of a reference-format
                // rotation file (`sampleMapRotation.txt`). ⚠️ A rotation does
                // NOT collapse to one map the way the reference's `-map` does;
                // --map still pins the opening map and the rotation runs after
                // it. ServerOptions.Map says why.
                case "--rotation":
                    options.MapRotation = ParseRotation(name, Require(name, value));
                    i++;
                    break;

                case "--shuffle":
                    options.ShuffleRotation = ParseShuffle(name, Require(name, value));
                    i++;
                    break;

                // ⚠️ Floored at 1 like --cap-limit and --time-limit, so
                // --kill-limit 0 is a one-kill round rather than an endless one.
                // The "0 means no limit" escape belongs to WorldState.
                case "--kill-limit":
                    options.KillLimit = (ushort)RequireInt(name, value);
                    i++;
                    break;

                // ⚠️ NOT floored at 1 — 0 really is "no spawn protection" here,
                // which is the reference's own max(0, ...) and the one limit in
                // this group with nothing to escape.
                case "--tdm-invuln":
                    options.TdmInvulnerabilitySeconds = RequireInt(name, value);
                    i++;
                    break;

                // Either one number for every class, or `scout=1,medic=2`.
                // ⚠️ A limit is PER TEAM, not per server: --class-limit medic=1
                // is one medic each, two on the map.
                case "--class-limit":
                    options.ClassLimits = ParseClassLimits(name, Require(name, value));
                    i++;
                    break;

                case "--autobalance":
                    options.Autobalance = true;
                    break;

                case "--no-autobalance":
                    options.Autobalance = false;
                    break;

                case "--multi-client-limit":
                    options.MultiClientLimit = RequireInt(name, value);
                    i++;
                    break;

                // ⚠️ TOTAL across every download in flight, not per client — a
                // second downloader halves the first one's rate.
                case "--map-download-limit":
                    options.MapDownloadBytesPerSecond = RequireInt(name, value);
                    i++;
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
                    options.TimeLimitMinutes = (byte)RequireInt(name, value);
                    i++;
                    break;
                case "--cap-limit":
                    options.CapLimit = (byte)RequireInt(name, value);
                    i++;
                    break;
                case "--respawn-time":
                    options.RespawnTimeSeconds = (byte)RequireInt(name, value);
                    i++;
                    break;

                // ⚠️ The nav caches are gitignored in the reference and are not
                // shipped, so a fresh checkout has none — and a server with an
                // ungenerated map in its rotation should keep serving it. A miss is
                // a quiet "these bots do not navigate", never an error.
                case "--botnav":
                    options.NavCacheDirectory = Require(name, value);
                    i++;
                    break;

                // The [Bots] section. `--bots N` is the fill target, which is what
                // the reference calls FillToPlayers — ⚠️ a total including humans,
                // not a bot count, so a server filling to 8 drops a bot every time
                // somebody joins.
                case "--bots":
                {
                    var fill = RequireInt(name, value);
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

                    i++;
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
                    botMode = ParseBotMode(name, Require(name, value));
                    i++;
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

                // How many HUMANS must be connected before any bot is spawned.
                // ⚠️ Below it the target is ZERO rather than the fill, so 1 is
                // "only ever run bots when somebody is actually playing" -- which
                // is what an empty public server wants, since a server full of
                // bots looks full in the lobby list and is not.
                case "--bots-min-humans":
                    botsMinHumans = RequireInt(name, value);
                    i++;
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
                    searchThreads = RequireInt(name, value);
                    i++;
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
                    arbiterUnits = RequireInt(name, value);
                    i++;
                    break;

                // docs/BOTS.md's arm 2: gg2-rl's trained checkpoint driving keys and
                // aim inside the same engagement window --search-bots uses.
                case "--policy":
                    policyPath = Require(name, value);
                    i++;
                    break;

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
                    ignoreStamp = true;
                    break;

                // ⚠️ SEPARATE from --search-bots and --policy on purpose. Both
                // shipped controllers engage only on the four classes the policy was
                // trained for, so a server drawing all eight runs a MIX of the hybrid
                // arm and the all-scripted one, and a playtester cannot tell which bot
                // they just fought. "trained" is the shorthand for making every bot
                // the configuration that was actually measured -- at the cost of a
                // roster missing five classes. Say which you ran.
                case "--bot-classes":
                {
                    var text = Require(name, value);

                    options.Bots = options.Bots with
                    {
                        AllowedClasses = string.Equals(text, "trained", StringComparison.OrdinalIgnoreCase)
                            ? [.. PolicyController.TrainedClasses]
                            : ParseClasses(name, text),
                    };

                    i++;
                    break;
                }

                case "--bot-difficulty":
                    options.Bots = options.Bots with { Difficulty = RequireInt(name, value) };
                    i++;
                    break;

                // docs/BOTAI.md item 3, the only item on this project with a
                // deadline: the broadcast stream already carries every player's
                // keys and aim every tick, so recording a game with people in it
                // is a labelled dataset of human action arriving for free. Off
                // unless asked for — recording people is a decision, not a
                // default.
                case "--record":
                    options.RecordingDirectory = Require(name, value);
                    i++;
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

                case "--control-port":
                    options.ControlPort = RequireInt(name, value);
                    options.ControlPortEnabled = true;
                    i++;
                    break;

                // ⚠️ Pacing only. Nothing about the simulation is rate-dependent
                // — every duration in the game is counted in ticks — so this
                // changes throughput and nothing else. It must stay at 1 on any
                // server a client is connected to: clients pace themselves at
                // 30 Hz and re-simulate between updates, so an unpaced server
                // desynchronises every one of them at once.
                case "--speed":
                {
                    var text = Require(name, value);

                    options.SpeedMultiplier = text.Equals("max", StringComparison.OrdinalIgnoreCase)
                        ? double.PositiveInfinity
                        : double.TryParse(text, CultureInfo.InvariantCulture, out var speed)
                            ? speed
                            : throw new ArgumentException($"{name} needs a number or 'max', not '{text}'");

                    i++;
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

        // ⚠️ AFTER the loop, so it beats --bots's own MinHumans = 0 wherever the
        // two appear relative to each other.
        if (botsMinHumans is { } minHumans)
            options.Bots = options.Bots with { MinHumans = minHumans };

        // ⛔ ONE PLACE BUILDS THE BRAIN, at the end, from flags collected above.
        // The flag handlers used to write BotPopulationOptions.SearchMicro
        // directly, which made --search-threads order-dependent: given BEFORE
        // --search-bots it was picked up, given after it had to re-write the
        // option it had just found. Collecting first and building once removes
        // the ordering entirely.
        //
        // ⚠️ THE ONE INFERENCE, and it exists to keep `--policy <path>` working on
        // its own the way docs/BOTS.md documents it: a --policy with no --bot-mode
        // means policy mode. It is order-independent because both were collected
        // before this line, and it applies ONLY when no mode was given -- an
        // explicit --bot-mode always wins, and a --policy it cannot use is refused
        // below rather than quietly ignored.
        var mode = botMode ?? (policyPath is not null ? BotMode.Policy : BotMode.Scripted);

        if (mode.UsesPolicy() && policyPath is null)
        {
            throw new ArgumentException(
                $"--bot-mode {mode.ToString().ToLowerInvariant()} drives bots with a trained "
                + "checkpoint and none was given; add --policy <path>, or use "
                + "--bot-mode scripted or search.");
        }

        if (!mode.UsesPolicy() && policyPath is not null)
        {
            // ⛔ Refused rather than ignored, the same rule the multi-tier case
            // has always applied: a checkpoint loaded, printed and never consulted
            // is a server saying it is doing something it is not.
            throw new ArgumentException(
                $"--policy was given but --bot-mode {mode.ToString().ToLowerInvariant()} never "
                + "consults a checkpoint; use --bot-mode policy or tiered, or drop --policy.");
        }

        // ⚠️ Tiered DEFAULTS its budget rather than refusing without one. Refusing
        // made the mode unreachable without a number nobody has tuned, which is a
        // worse default than a stated guess -- see the constant, which says what
        // would show it is wrong.
        // ⚠️ EVERY mode with a controller gets a budget, not just tiered. The Q2
        // reasoning that "one tier has nothing to choose between, so an arbiter is
        // pure cost" was wrong: an arbiter's second job is to GATE, and with one
        // tier it still returns -1 and drops the bot to the scripted layer once
        // the budget is spent. Without one, `--bot-mode search` let all twelve
        // bots search on the same tick -- 12 x ~4,800 us against a 33,300 us tick,
        // which is over budget before the simulation runs at all.
        if (mode.NeedsArbiter())
            arbiterUnits ??= DecisionQuotaArbiter.DefaultTieredBudgetMicroseconds;

        var tiers = new List<MicroControllerSpec>();

        if (mode.UsesSearch())
            tiers.Add(new SearchMicroSpec(new RolloutOptions(Threads: searchThreads)));

        if (mode.UsesPolicy())
        {
            var policy = PolicyNetwork.Load(policyPath!, ignoreStamp);

            // ⛔ ALWAYS printed, not only when overridden. A run whose stamps agree
            // says so; a run that bypassed the guard says THAT, loudly, on the same
            // line. A bypassed guard that leaves no trace in the output is worse
            // than no guard, because the next reader cannot tell it happened.
            Console.WriteLine(
                $"policy {Path.GetFileName(policyPath!)}: trained against {policy.SimulationUuid}, "
                + $"this build is {SimulationStamp.Uuid}"

                // ⚠️ Reported, never compared. NavConstants left the gating
                // surface on 2026-09-03 so a nav edit stops stranding
                // checkpoints — but removing it from the GATE must not remove it
                // from the RECORD, or "the bots play differently and the stamp
                // matches" becomes unattributable. No checkpoint carries one to
                // compare against yet; SimulationStamp.RoutingUuid says what to
                // do when one does.
                + $", routing {SimulationStamp.RoutingUuid}"
                + (policy.SimulationUuid == SimulationStamp.Uuid
                    ? " (match)"
                    : "  <-- STAMP MISMATCH, BYPASSED by --policy-ignore-stamp. Everything this "
                      + "policy decides was learned on a different simulation."));

            // ⚠️ AFTER the search, so tier 0 is the more expensive controller and
            // an arbiter under pressure drops to the policy before it drops to the
            // scripted layer. Ordering is the arbiter's whole input — HybridBot.
            tiers.Add(new PolicyMicroSpec(policy));
        }

        // ⛔ Unreachable from the flags now -- NeedsArbiter defaults the budget for
        // the only multi-tier mode -- and kept as the invariant it always was. If a
        // fifth mode arrives with two tiers and no default, this is what stops it
        // shipping a controller that is configured, reported and never consulted.
        if (tiers.Count > 1 && arbiterUnits is null)
        {
            throw new ArgumentException(
                $"{tiers.Count} bot controllers were configured but no --bot-arbiter <units>; "
                + "without one the first tier always drives and the rest are dead weight");
        }

        if (tiers.Count > 0)
        {
            options.Bots = options.Bots with
            {
                Brain = new BotBrainSpec
                {
                    Tiers = tiers,

                    // ⛔ The server's default is the OPPOSITE of BotBrainSpec's,
                    // deliberately -- see ServerOptions.BotEngagesBotsToo.
                    Engage = options.BotEngagesBotsToo
                        ? EngageOpponents.Anyone
                        : EngageOpponents.HumansOnly,

                    // ⛔ ONE arbiter for the whole server, shared by every bot. A
                    // per-bot one cannot see the coinciding decisions that make the
                    // tick tail, which is the only reason it exists.
                    Arbiter = arbiterUnits is { } units ? new DecisionQuotaArbiter(units) : null,
                },
            };
        }

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

    private static string Require(string name, string? value) =>
        value ?? throw new ArgumentException($"{name} needs a value");

    private static int RequireInt(string name, string? value) =>
        int.TryParse(Require(name, value), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new FormatException($"{name} needs a number, got '{value}'");

    private static void PrintUsage() => Console.WriteLine(
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
          --plugins <list>  server-sent plugins to ask clients to load,
                            comma-separated (e.g. chat_v2_pr)
          --plugins-required
                            clients must load them to join
          --votable-maps <list|path>
                            maps a plugin map vote may offer (default: the
                            rotation, else every map in --maps)
          --multi-client-limit <n>
                            connections allowed from one address (default 3);
                            loopback is exempt
          --maps <path>     override the built-in map PNGs (default: maps/)
          --map-download-limit <n>
                            TOTAL bytes/second across all custom-map downloads
                            in flight, not per client (default 50000)
          --config <path>   read a reference-format gg2.ini: its [Settings],
                            [Server], [Maps] and [Classlimits] sections, plus the
                            rotation file its MapRotation key names. Applied
                            BEFORE every flag, so a flag always wins wherever it
                            appears. The file is never written back.
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
          --bot-engages-bots
                            let bots run their expensive micro-controller against
                            each other too. Without this a bot only hands the
                            wheel to --bot-mode's controller for a HUMAN enemy and
                            a bot-vs-bot fight stays scripted, which is a COST
                            saving and not a skill one. Every recorded arm
                            measurement was taken with this behaviour.
          --bot-classes <list|trained>
                            restrict the bot class draw (default: the reference's
                            own draw of eight). 'trained' is Scout, Soldier,
                            Sniper and Heavy - the only classes the search and
                            policy controllers engage on, so pass it to make EVERY
                            bot the configuration the bench measured.
          --botnav <path>   override the bot nav graphs (default: nav/)
          --speed <n|max>   run at n times real time, or 'max' for unpaced
                            (default 1)
          --record <path>   write replayable .trace recordings of this server's
                            broadcast stream to a directory (default: off)
          --control-port <n>
                            open the loopback control port (default: off;
                            0 asks the OS for a free port)
          --tick-stats      log a histogram of tick intervals every 30s and at
                            shutdown; safe with clients connected
          --lobby           list this server publicly at ganggarrison.com, so it
                            appears in the client's server browser (default: off).
                            Sends this server's address, name, map and player
                            count there every 30 seconds until it shuts down.

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
}
