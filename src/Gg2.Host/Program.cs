using System.Runtime.InteropServices;
using Gg2.Bots;
using Gg2.Protocol;
using Gg2.Server;
using Gg2.Simulation;

namespace Gg2.Host;

internal static class Program
{
    /// <summary>
    /// The console sink: one line per event, timestamped, with warnings and
    /// errors on stderr.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A full date, not <c>HH:mm:ss</c>.</b> This process is expected to run
    /// for weeks, so a bare time of day makes every line in the journal
    /// ambiguous the moment it is read the next morning. ISO-8601 with the
    /// offset sorts lexicographically, survives a timezone change and is what
    /// every log tool already parses.
    /// </para>
    /// <para>
    /// <b>Warnings and errors go to stderr.</b> That is what makes them
    /// separable without a log parser: <c>2&gt;</c> in a shell, a
    /// <c>StandardError</c> redirect in a unit file, and <c>journalctl -p warning</c>
    /// under systemd all key off the stream rather than the text. Under a
    /// terminal the two are interleaved as usual, so nothing is lost
    /// interactively either.
    /// </para>
    /// </remarks>
    private sealed class ConsoleLog : IServerLog
    {
        public void Info(string message) => Console.WriteLine(Line("info ", message));

        public void Warn(string message) => Console.Error.WriteLine(Line("warn ", message));

        public void Error(string message, Exception? error = null)
        {
            Console.Error.WriteLine(Line("error", message));

            // The stack trace, on the stream the message went to, and only for
            // Error. It is the difference between a bug report that can be acted
            // on and one that says "it crashed".
            if (error is not null)
                Console.Error.WriteLine(error.ToString());
        }

        private static string Line(string level, string message) =>
            $"{DateTimeOffset.Now:yyyy-MM-ddTHH:mm:ss.fffzzz} {level} {message}";
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

        // ⚠️ The console sink is ALWAYS the base, and --log-file wraps it
        // rather than replacing it. A file log that silently became the only
        // log would fail exactly when it mattered — when the disk is what has
        // broken. FileLog says the rest.
        IServerLog log = new ConsoleLog();
        FileLog? fileLog = null;

        if (options.LogFile.Length != 0)
        {
            try
            {
                log = fileLog = new FileLog(log, options.LogFile, options.LogMaxBytes, options.LogKeep);
                log.Info($"logging to {Path.GetFullPath(options.LogFile)}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Refused at startup rather than absorbed. A path that cannot be
                // opened at all is a typo in the command line, and starting
                // anyway would give the operator a server that is running and a
                // log they will go looking for and never find. Failures AFTER
                // this point are absorbed instead — see FileLog — because by
                // then a working server is worth more than its log.
                Console.Error.WriteLine($"unable to open --log-file {options.LogFile}: {e.Message}");
                return 2;
            }
        }

        using var ownedFileLog = fileLog;

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

        try
        {
            server.Run(stopping.Token);
        }
        catch (TickFaultException e)
        {
            // ⚠️ NON-ZERO, and that is the whole point of catching it here. The
            // server has already logged every fault and its own decision to
            // stop; what this adds is the exit code, which is the only part a
            // supervisor reads. `Restart=on-failure` in the unit file
            // (docs/DEPLOY.md) brings back a clean process — which is the one
            // thing that reliably clears a corrupted in-memory state — and
            // leaves an ordinary shutdown alone, because that exits 0.
            //
            // Caught rather than left to the runtime so the operator gets one
            // sentence instead of an unhandled-exception dump, and so Dispose
            // runs: without it the lobby keeps advertising a server that is not
            // there, and an in-flight --record segment stays a .part file.
            log.Error("stopped: the tick loop failed repeatedly", e.InnerException);
            return 3;
        }

        return 0;
    }
}
