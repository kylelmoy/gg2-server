using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using GG2.Bots;
using GG2.Server;
using GG2.Server.Rooms;

namespace GG2.Host;

/// <summary>
/// <c>gg2-server rooms</c>: a room host and its API (<c>docs/ROOMS.md</c>).
/// </summary>
/// <remarks>
/// <b>The base config is read again for every room</b>, so an edit to it
/// applies to the next room created and never to a live one. It is checked once
/// at startup, with the default choices, so a broken file stops the host rather
/// than every create.
/// </remarks>
internal static class RoomsCommand
{
    /// <summary>The exit code of a host that drained (<c>docs/ROOMS.md</c>, "Draining for a deploy").</summary>
    public const int DrainedExitCode = 4;

    public static void PrintUsage()
    {
        Console.WriteLine("""
            usage: gg2-server rooms [options]

            Runs many private rooms behind one game port, and the HTTP API that creates
            them (docs/ROOMS.md). Every room is the base config plus a player's choices.

              --config <path>       base gg2.json for every room (bots, rules, rotation)
              --port <n>            the game port every room shares (default 8300)
              --prefix <letter>     this host's code prefix (default X)
              --max-rooms <n>       rooms held at once (default 8)
              --api-port <n>        the HTTP API's port (default 8301)
              --api-bind <address>  the API's bind address (default 127.0.0.1)
              --min-free-mb <n>     refuse a room under this much MemAvailable (default 256, 0 = off)
              --live-per-address <n>  live rooms one address may hold (default 2, 0 = off)
              --creates-per-hour <n>  rooms one address may create an hour (default 6, 0 = off)
              --api-trust-proxy     read the client address from X-Forwarded-For on a
                                    loopback request (only behind a proxy that appends it)
              --drain-file <path>   written after startup, this file drains the host: no
                                    new rooms, and exit 4 once the last one ends, so a
                                    Restart=on-failure unit comes back on the new release
            """);
    }

    public static int Run(string[] args, IServerLog log)
    {
        string? config = null;
        var host = new RoomHostOptions();
        var api = new RoomApiOptions();

        for (var i = 0; i < args.Length; i++)
        {
            string Value() => i + 1 < args.Length
                ? args[++i]
                : throw new ArgumentException($"{args[i]} needs a value");

            int Int() => int.Parse(Value(), NumberStyles.Integer, CultureInfo.InvariantCulture);

            switch (args[i])
            {
                case "--config":
                    config = Value();
                    break;
                case "--port":
                    host = host with { Port = Int() };
                    break;
                case "--prefix":
                    var prefix = Value();
                    host = host with
                    {
                        Prefix = prefix.Length == 1
                            ? char.ToUpperInvariant(prefix[0])
                            : throw new ArgumentException($"--prefix '{prefix}': one letter"),
                    };
                    break;
                case "--max-rooms":
                    host = host with { MaxRooms = Int() };
                    break;
                case "--api-port":
                    api = api with { Port = Int() };
                    break;
                case "--api-bind":
                    api = api with { Bind = IPAddress.Parse(Value()) };
                    break;
                case "--min-free-mb":
                    api = api with { MinFreeMemoryMb = Int() };
                    break;
                case "--live-per-address":
                    api = api with { MaxLiveRoomsPerAddress = Int() };
                    break;
                case "--creates-per-hour":
                    api = api with { MaxCreatesPerAddressPerHour = Int() };
                    break;
                case "--drain-file":
                    host = host with { DrainFile = Value() };
                    break;
                case "--api-trust-proxy":
                    api = api with { TrustForwardedFor = true };
                    break;
                default:
                    throw new ArgumentException($"unknown option {args[i]}");
            }
        }

        host.Validate();

        if (config is not null && !File.Exists(config))
            throw new FileNotFoundException($"no config file at {config}", config);

        ServerOptions Template(RoomChoices choices)
        {
            var options = ServerOptions.CreateDefault();

            if (config is not null)
                ServerConfigFile.Apply(options, config);

            RoomTemplate.Apply(options, choices);
            return options;
        }

        // The startup check: the default room, validated, its nets loaded.
        var probe = Template(new RoomChoices());

        probe.Validate();
        BotBrain.CheckSettings(probe);

        log.Info(BuildInfo.Description);

        using var rooms = new RoomHost(host, log);

        foreach (var path in probe.PolicyPaths)
            rooms.SharedPolicies.Load(path);

        rooms.Start();

        using var server = new RoomApi(rooms, Template, api, log);
        server.Start();

        if (!IPAddress.IsLoopback(api.Bind) && (api.MaxLiveRoomsPerAddress <= 0 || api.MaxCreatesPerAddressPerHour <= 0))
            log.Warn("room API bound beyond loopback with a per-address limit off: one address can fill this host");

        using var stopping = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            log.Info("stopping");
            stopping.Cancel();
        };
        using var sigterm = ShutdownSignals.Handle(PosixSignal.SIGTERM, stopping, log);

        var serving = server.RunAsync(stopping.Token);
        rooms.Run(stopping.Token);

        server.Dispose();
        serving.Wait(TimeSpan.FromSeconds(2));

        // NON-ZERO on purpose: a drain is a deploy asking for a fresh process on
        // the new release, and Restart=on-failure only restarts a failure. An
        // operator's stop (SIGTERM, Ctrl+C) still exits 0 and stays stopped.
        return rooms.Drained ? DrainedExitCode : 0;
    }
}
