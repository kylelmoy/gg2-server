using System.Diagnostics;
using Gg2.Server;

namespace Gg2.Host;

/// <summary>
/// <c>gg2-server config ...</c>: one configuration for several servers.
/// <c>docs/CONFIG.md</c>, "Many servers, one configuration".
/// </summary>
/// <remarks>
/// A subcommand for the reason <c>navgen</c> is: it serves no
/// game and shares none of the server's options. It runs on the operator's
/// machine; <c>scripts/deploy.sh --config</c> calls <c>render</c>.
/// </remarks>
internal static class ConfigTool
{
    public static void PrintUsage()
    {
        Console.WriteLine(
            """
            usage:
              gg2-server config edit   [--dir <dir>] [--hosts <file>] [--port <n>] [--no-browser]
              gg2-server config render [--dir <dir>] [--hosts <file>] --out <dir> [host ...]

              edit     a local page for writing common.ini and each host's overrides.
                       Loopback only; the address it prints carries a one-run token.
              render   write each host's gg2.ini into --out, validated, and print
                       "<host> <sha256> <file>" per host. Hosts default to every
                       line of --hosts.

              --dir    the config directory (default deploy/config): common.ini and
                       hosts/<ssh destination>.ini
              --hosts  one ssh destination per line (default deploy/hosts)
              --port   the editor's port (default 0: any free one)
            """);
    }

    public static int Run(string[] args)
    {
        if (args.Length == 0)
            throw new ArgumentException("config: say edit or render");

        var directory = Path.Combine("deploy", "config");
        var hostsFile = Path.Combine("deploy", "hosts");
        var output = string.Empty;
        var port = 0;
        var browser = true;
        var named = new List<string>();

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--dir": directory = Value(args, ref i); break;
                case "--hosts": hostsFile = Value(args, ref i); break;
                case "--out": output = Value(args, ref i); break;
                case "--port": port = int.Parse(Value(args, ref i), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--no-browser": browser = false; break;
                default:
                    if (args[i].StartsWith('-'))
                        throw new ArgumentException($"config: unknown option {args[i]}");
                    named.Add(args[i]);
                    break;
            }
        }

        return args[0] switch
        {
            "render" => Render(directory, hostsFile, output, named),
            "edit" => Edit(directory, hostsFile, port, browser),
            _ => throw new ArgumentException($"config: unknown action {args[0]}"),
        };
    }

    private static int Render(string directory, string hostsFile, string output, List<string> named)
    {
        if (output.Length == 0)
            throw new ArgumentException("config render: --out is required");

        IReadOnlyList<string> hosts = named.Count != 0 ? named : FleetConfig.ReadHosts(hostsFile);

        if (hosts.Count == 0)
            throw new ArgumentException($"config render: no hosts named and none in {hostsFile}");

        // Every host rendered before any file is written, so a refusal for the
        // third host does not leave two fresh files beside a stale one.
        var rendered = new List<(string Host, byte[] Bytes)>();

        try
        {
            foreach (var host in hosts)
                rendered.Add((host, FleetConfig.Encode(FleetConfig.Render(directory, host))));

            foreach (var warning in FleetConfig.Warnings(directory, hosts))
                Console.Error.WriteLine($"warning: {warning}");
        }
        catch (FormatException e)
        {
            Console.Error.WriteLine($"refused: {e.Message}");
            return 1;
        }

        Directory.CreateDirectory(output);

        foreach (var (host, bytes) in rendered)
        {
            var path = Path.Combine(output, FleetConfig.OverlayFileName(host));
            File.WriteAllBytes(path, bytes);
            Console.WriteLine($"{host} {FleetConfig.Hash(bytes)} {path}");
        }

        return 0;
    }

    private static int Edit(string directory, string hostsFile, int port, bool browser)
    {
        if (!File.Exists(hostsFile))
            Console.Error.WriteLine($"warning: no hosts file at {Path.GetFullPath(hostsFile)}; the page will list no servers");

        Directory.CreateDirectory(directory);

        using var server = new ConfigEditorServer(new FleetConfigEditor(directory, hostsFile), port);
        server.Start();

        Console.WriteLine($"editing {Path.GetFullPath(directory)}");
        Console.WriteLine($"open {server.Url}");
        Console.WriteLine("Ctrl+C to stop.");

        if (browser)
        {
            try
            {
                Process.Start(new ProcessStartInfo(server.Url) { UseShellExecute = true });
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                Console.Error.WriteLine("could not open a browser; open the address above yourself");
            }
        }

        using var stopping = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stopping.Cancel();
        };

        server.RunAsync(stopping.Token).GetAwaiter().GetResult();
        return 0;
    }

    private static string Value(string[] args, ref int i)
    {
        if (i + 1 >= args.Length)
            throw new ArgumentException($"config: {args[i]} needs a value");

        return args[++i];
    }
}
