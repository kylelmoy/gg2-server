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

                // The round rules. Without these a round cannot be played to
                // completion in a session — the default clock is fifteen
                // minutes — which is what docs/PLAN.md's phase 6 exit criterion
                // asks for. The reference floors both limits at 1 in
                // game_init.gml, so 0 is unreachable there and means "no limit"
                // here; see docs/SIMULATION.md, "Settled: the round".
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
          --time-limit <n>  round clock in minutes, 1..255 (default 15)
          --cap-limit <n>   captures to win, 1..255 (default 5)
          --respawn-time <n>
                            seconds a death lasts (default 5)

        --time-limit and --cap-limit are floored at 1, exactly as the
        reference's own ini is (game_init.gml:96, 101), so a zero is a
        one-minute round rather than an endless one. There is no "no limit" —
        for a long session pass a large number.

        The maps ship inside the reference Game Maker project, so --maps (or
        GG2_REFERENCE) has to point at a Gang-Garrison-2 checkout.

        The world is simulated: characters, weapons, all eight gamemodes and the
        map rotation. See docs/PLAN.md.
        """);
}
