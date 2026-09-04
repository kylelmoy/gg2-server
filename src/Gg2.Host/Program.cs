using System.Runtime.InteropServices;
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

        // `gg2-server navgen ...` builds the bot nav graphs instead of serving.
        // A subcommand for the same reason `eval` is one: it shares almost none
        // of the server's options, and the ones it looks like it shares (--maps,
        // --botnav/--out) would have to mean the same thing in both.
        if (args.Length > 0 && args[0] == "navgen")
        {
            var rest = args[1..];

            if (rest.Contains("--help") || rest.Contains("-h"))
            {
                NavGen.PrintUsage();
                return 0;
            }

            try
            {
                return NavGen.Run(rest);
            }
            catch (Exception e) when (e is ArgumentException or FormatException or IOException)
            {
                Console.Error.WriteLine(e.Message);
                Console.Error.WriteLine();
                NavGen.PrintUsage();
                return 2;
            }
        }

        if (args.Contains("--help") || args.Contains("-h"))
        {
            CommandLine.PrintUsage();
            return 0;
        }

        ServerOptions options;
        try
        {
            options = CommandLine.Parse(args);
        }
        catch (Exception e) when (e is ArgumentException or FormatException)
        {
            Console.Error.WriteLine(e.Message);
            Console.Error.WriteLine();
            CommandLine.PrintUsage();
            return 2;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            // No usage text for these two. A missing config file or a missing
            // maps/ directory is not a mistake in the command line, and burying
            // the one sentence that says which file under sixty lines of flags
            // is how a clear error becomes an unread one.
            Console.Error.WriteLine(e.Message);
            return 2;
        }

        var log = new ConsoleLog();

        // INSIDE the try: ServerOptions.Validate runs in the GameServer
        // constructor, so an invalid option would otherwise reach an unhandled
        // exception past the usage text. Parse also validates; this is the
        // second net, for a caller that builds options some other way.
        using var server = new GameServer(options, log);

        try
        {
            server.Start();
        }
        catch (Exception e) when (e is IOException
                                      or FileNotFoundException
                                      or ArgumentException
                                      or InvalidOperationException
                                      or System.Net.Sockets.SocketException)
        {
            // InvalidOperationException is here for the plugin loader, whose
            // failures are all operator errors — a name the site does not have, a
            // hash it will not serve, a list too long for the greeting. Its message
            // is the useful half: it names the URL and says plugin names are
            // lowercase.

            // SocketException is NOT an IOException, so it needs its own catch —
            // there are two listeners that can collide (the game port and --web),
            // and a stack trace is the least useful way to be told about either.
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

        // SIGTERM is how a SERVICE is stopped — systemd's default KillSignal
        // and `docker stop` both send it, and CancelKeyPress does not see it.
        // Without this the runtime terminates the process where it stands: the
        // lobby listing is never taken down (it then advertises a dead server
        // until the lobby's own timeout), an in-flight --record segment stays a
        // .part file that never becomes a readable trace, and no client gets a
        // clean socket shutdown. Dispose does all three, and only the graceful
        // path reaches it. docs/DEPLOY.md.
        using var sigterm = ShutdownSignals.Handle(PosixSignal.SIGTERM, stopping, log);

        server.Run(stopping.Token);
        return 0;
    }
}
