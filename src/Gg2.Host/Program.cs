using System.Globalization;
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

    private static ServerOptions Parse(string[] args)
    {
        var options = new ServerOptions
        {
            // The built-in maps live in the reference checkout. There is nowhere
            // else to get them: they ship inside the Game Maker project.
            MapDirectory = Environment.GetEnvironmentVariable("GG2_REFERENCE") is { Length: > 0 } reference
                ? Path.Combine(reference, "Source", "gg2", "Included Files")
                : @"D:\code\Gang-Garrison-2\Source\gg2\Included Files",
        };

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

                case "--bot-difficulty":
                    options.Bots = options.Bots with { Difficulty = RequireInt(name, value) };
                    i++;
                    break;
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
          --maps <path>     where the built-in map PNGs live
          --bots <n>        fill the server to n players with bots (0 = off)
          --bot-difficulty <1-5>
                            how good they are (default 3)
          --botnav <path>   where the bot nav caches live

        The maps ship inside the reference Game Maker project, so --maps (or
        GG2_REFERENCE) has to point at a Gang-Garrison-2 checkout.

        --bots is a FILL target including humans, not a bot count: a server
        filling to 8 with 3 people on it runs 5 bots. Without --botnav the bots
        fight whatever comes to them and do not walk anywhere; the caches are
        generated by the reference and are not shipped.
        """);
}
