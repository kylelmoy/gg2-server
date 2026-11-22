using System.Runtime.InteropServices;
using GG2.Bots;
using GG2.Protocol;
using GG2.Server;
using GG2.Simulation;

namespace GG2.Host;

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

        // `gg2-server cellgen ...` writes duel maps (docs/DUEL.md), for the same
        // reason navgen is a subcommand: it serves no game.
        if (args.Length > 0 && args[0] == "cellgen")
        {
            var rest = args[1..];

            if (rest.Contains("--help") || rest.Contains("-h"))
            {
                CellGen.PrintUsage();
                return 0;
            }

            try
            {
                return CellGen.Run(rest);
            }
            catch (Exception e) when (e is ArgumentException or FormatException or IOException or InvalidOperationException)
            {
                Console.Error.WriteLine(e.Message);
                Console.Error.WriteLine();
                CellGen.PrintUsage();
                return 2;
            }
        }

        // `gg2-server config ...` edits and renders the configuration that
        // scripts/deploy.sh --config pushes to several servers. It serves no game.
        if (args.Length > 0 && args[0] == "config")
        {
            var rest = args[1..];

            if (rest.Length == 0 || rest.Contains("--help") || rest.Contains("-h"))
            {
                ConfigTool.PrintUsage();
                return rest.Length == 0 ? 2 : 0;
            }

            try
            {
                return ConfigTool.Run(rest);
            }
            catch (Exception e) when (e is ArgumentException or FormatException or IOException)
            {
                Console.Error.WriteLine(e.Message);
                Console.Error.WriteLine();
                ConfigTool.PrintUsage();
                return 2;
            }
        }

        // ⚠️ BEFORE the parser and before --help, and it answers without
        // touching a socket, a map or a config file. This is the question a
        // deployment asks of a binary it has just copied — `gg2-server
        // --version` on the target names the commit AND proves the file
        // executes on that machine, so a missing .NET 10 runtime or an x86-64
        // publish on an arm64 box fails here rather than at the restart after
        // the symlink has already been flipped. scripts/deploy.sh runs it in
        // exactly that order.
        if (args.Contains("--version"))
        {
            Console.WriteLine(BuildInfo.Description);
            return 0;
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
        //
        // ⚠️ Registered ONCE, outside the restart loop below. Re-registering per
        // restart would leave one live handler per generation on a process that
        // is meant to run for weeks, and CancelKeyPress has no way to remove the
        // old ones.
        using var sigterm = ShutdownSignals.Handle(PosixSignal.SIGTERM, stopping, log);

        // ⚠️ The GENERATED admin password, carried across a restart.
        // WebAdminServer generates one when none is configured and prints it at
        // startup — to the console, which an operator who just pressed "restart"
        // in a browser on another machine cannot see. Without this, a restart
        // from the panel locks that operator out of the panel, which is the one
        // failure a restart button must not have. It is carried only within this
        // process, only while no password is configured, and it is logged when it
        // happens so nobody has to infer it.
        string? carriedWebPassword = null;

        // ⚠️ OUTSIDE the loop: this identifies the PROCESS, and a control-port
        // or panel restart rebuilds the server inside the same one. Logging it
        // per generation would put a second, identical line in the journal
        // saying a new build had started when none had — which is the one thing
        // an operator reads this line to rule out. New binaries need a new
        // process; docs/DEPLOY.md, "updating many machines at once".
        log.Info(BuildInfo.Description);

        while (true)
        {
            if (carriedWebPassword is not null
                && options.Web.Enabled
                && options.Web.Password.Length == 0)
            {
                options.Web = options.Web with { Password = carriedWebPassword };

                log.Info(
                    "web admin: keeping the generated password across this restart, so "
                    + "whoever asked for it is not locked out. Set [Web] Password or "
                    + "--web-password to choose one instead.");
            }

            // INSIDE the try: ServerOptions.Validate runs in the GameServer
            // constructor, so an invalid option would otherwise reach an unhandled
            // exception past the usage text. Parse also validates; this is the
            // second net, for a caller that builds options some other way.
            var server = new GameServer(options, log);

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
                server.Dispose();
                return 1;
            }

            carriedWebPassword = server.GeneratedWebPassword;

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
                // ⛔ NOT a restart, however much this loop looks like one. A tick
                // loop that failed repeatedly is exactly the state a fresh PROCESS
                // clears and a fresh GameServer may not, and looping here would
                // turn a supervisor's restart budget into an unsupervised spin.
                //
                // Caught rather than left to the runtime so the operator gets one
                // sentence instead of an unhandled-exception dump, and so Dispose
                // runs: without it the lobby keeps advertising a server that is not
                // there, and an in-flight --record segment stays a .part file.
                log.Error("stopped: the tick loop failed repeatedly", e.InnerException);
                server.Dispose();
                return 3;
            }

            // A room that stopped itself exits even if a restart was also asked
            // for: the restart would only bring it back for another interval.
            var restarting = server.RestartRequested && server.SelfExit == SelfExitReason.None;

            // ⛔ Disposed BEFORE the next Start, which is why this is not a
            // `using`: a `using` inside the loop releases at the closing brace,
            // after the next iteration has already tried to bind the same game
            // port and the same --web port. It is also what takes the lobby
            // listing down and closes an in-flight recording into a readable
            // trace, both of which a restart owes the operator as much as a
            // shutdown does.
            server.Dispose();

            if (!restarting)
                return 0;

            log.Info("restarting: re-reading the configuration and rebuilding the server");

            try
            {
                // ⚠️ The whole command line again, not just the config file: a flag
                // beats the file before a restart and must keep beating it after
                // one, or a restart would quietly become a different server.
                // ServerOptions.CommandLineOverrides is how the panel says which.
                options = CommandLine.Parse(args);
            }
            catch (Exception e) when (e is ArgumentException or FormatException or IOException)
            {
                // ⛔ The one place a bad configuration is FATAL rather than a usage
                // error: the server this process was running has already stopped,
                // so there is nothing to fall back to. Naming the key is all that
                // is left to do — and ControlCommands validates before it writes,
                // so reaching here means the file was changed some other way
                // between the save and the restart.
                Console.Error.WriteLine(
                    $"restart failed and the old server has already stopped: {e.Message}");

                return 2;
            }
        }
    }
}
