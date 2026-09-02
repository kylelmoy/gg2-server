using System.Globalization;
using Gg2.Bots;
using Gg2.Protocol;
using Gg2.Server;

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

        var log = new ConsoleLog();
        using var server = new GameServer(options, log);

        try
        {
            server.Start();
        }
        catch (Exception e) when (e is IOException or FileNotFoundException or ArgumentException)
        {
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

    private static ServerOptions Parse(string[] args)
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

        // ⚠️ Held OUTSIDE the switch because --search-threads and --search-bots
        // can arrive in either order, and a flag whose effect depends on argument
        // order is the kind of thing nobody notices until a measurement is wrong.
        // Whichever comes second reconciles the two.
        var searchThreads = Math.Clamp(Environment.ProcessorCount / 3, 1, 8);

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
                case "--maps":
                    options.MapDirectory = Require(name, value);
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

                // docs/BOTS.md's arm 3: a RolloutSearch micro-controller inside
                // each bot's engagement window, the scripted layer still owning
                // movement, routing and objectives.
                //
                // ⚠️ IT IS THE BETTER BOT AND THE EXPENSIVE ONE. Better on death
                // share than both the all-scripted control and the policy hybrid,
                // pooled over ~34,000 deaths and over 840 matches an arm. Its cost
                // is why --search-threads exists.
                //
                // ⛔ NOT MEASURED WITH HUMANS IN THE SERVER. A rollout re-simulates
                // every live character for every tick of every candidate, so cost
                // rises with the population, and every number quoted for it comes
                // from 8-bot headless matches. Watch --tick-stats while playing.
                case "--search-bots":
                    options.Bots = options.Bots with
                    {
                        SearchMicro = new RolloutOptions(Threads: searchThreads),
                    };

                    break;

                // ⚠️ Threads change NO decision -- RolloutThreadingTests gates that
                // at 2, 4 and 8 -- so this is a pure cost knob. It is also the one
                // place this server is not single-threaded, and the pool it uses is
                // the process's: a burst inside a tick takes cores from nothing else
                // here, but it is not free on a shared machine.
                case "--search-threads":
                    searchThreads = RequireInt(name, value);

                    if (options.Bots.SearchMicro is not null)
                    {
                        options.Bots = options.Bots with
                        {
                            SearchMicro = new RolloutOptions(Threads: searchThreads),
                        };
                    }

                    i++;
                    break;

                // ⚠️ SEPARATE from --search-bots on purpose. SearchBot engages only
                // on the four classes the policy was trained for, so a server drawing
                // all eight runs a MIX of arm 3 and arm 1 and a playtester cannot tell
                // which bot they just fought. "trained" is the shorthand for making
                // every bot the configuration that was actually measured -- at the cost
                // of a roster missing five classes. Say which you ran.
                case "--bot-classes":
                {
                    var text = Require(name, value);

                    options.Bots = options.Bots with
                    {
                        AllowedClasses = string.Equals(text, "trained", StringComparison.OrdinalIgnoreCase)
                            ? [.. PolicyBot.TrainedClasses]
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
          --maps <path>     override the built-in map PNGs (default: maps/)
          --time-limit <n>  round clock in minutes, 1..255 (default 15)
          --cap-limit <n>   captures to win, 1..255 (default 5)
          --respawn-time <n>
                            seconds a death lasts (default 5)
          --bots <n>        fill the server to n players with bots (0 = off)
          --bot-difficulty <1-5>
                            how good they are (default 3)
          --search-bots     give every bot a rollout-search micro-controller inside
                            its engagement window (default: off). Better on death
                            share than the scripted bot and much more expensive;
                            never yet measured with humans in the server.
          --search-threads <n>
                            threads the search splits its candidates across
                            (default: cores/3, capped at 8). Changes cost only,
                            never a decision.
          --bot-classes <list|trained>
                            restrict the bot class draw (default: the reference's
                            own draw of eight). 'trained' is Scout, Soldier,
                            Sniper and Heavy - the only classes --search-bots
                            engages on, so pass it to make EVERY bot the
                            configuration the bench measured.
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

        --time-limit and --cap-limit are floored at 1, exactly as the
        reference's own ini is (game_init.gml:96, 101), so a zero is a
        one-minute round rather than an endless one. There is no "no limit" —
        for a long session pass a large number.

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
