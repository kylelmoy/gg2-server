using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;

namespace GG2.Lobby;

internal static class Program
{
    private const string Usage = """
        gg2-lobby: an HTTP/JSON server list for gg2-server. docs/LOBBY.md.

          --port <n>          listen port (default 29945)
          --bind <address>    listen address (default [::], which is dual-stack)
          --ttl <seconds>     how long an announce lists a server (default 70)
          --no-probe          list servers without connecting back to their port
          --max-servers <n>   entries in total (default 4096)
          --max-per-ip <n>    entries from one address (default 64)
          --quiet             do not log listings and removals
        """;

    public static int Main(string[] args)
    {
        var options = new LobbyOptions();
        var quiet = false;

        try
        {
            for (var i = 0; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : throw new FormatException($"{args[i]} needs a value");
                int NextInt() => int.Parse(Next(), CultureInfo.InvariantCulture);

                switch (args[i])
                {
                    case "--port": options = options with { Port = NextInt() }; break;
                    case "--bind": options = options with { Bind = IPAddress.Parse(Next()) }; break;
                    case "--ttl": options = options with { Ttl = TimeSpan.FromSeconds(NextInt()) }; break;
                    case "--no-probe": options = options with { Probe = false }; break;
                    case "--max-servers": options = options with { MaxServers = NextInt() }; break;
                    case "--max-per-ip": options = options with { MaxPerAddress = NextInt() }; break;
                    case "--quiet": quiet = true; break;
                    case "-h" or "--help": Console.WriteLine(Usage); return 0;
                    default: throw new FormatException($"unknown argument {args[i]}");
                }
            }
        }
        catch (FormatException e)
        {
            Console.Error.WriteLine(e.Message);
            Console.Error.WriteLine(Usage);
            return 2;
        }

        Action<string> log = quiet
            ? static _ => { }
            : static line => Console.WriteLine($"{DateTime.UtcNow:HH:mm:ss} {line}");

        using var server = new LobbyServer(options, log);
        Console.WriteLine($"gg2-lobby listening on {options.Bind}:{server.Port}, ttl {options.Ttl.TotalSeconds:0}s, probe {(options.Probe ? "on" : "off")}");

        using var stop = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Set();
        };
        using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            stop.Set();
        });

        stop.Wait();
        return 0;
    }
}
