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
    public static void PrintUsage()
    {
        Console.WriteLine("""
            usage: gg2-server rooms [options]

            Runs many private rooms behind one game port, and the HTTP API that creates
            them (docs/ROOMS.md). Every room is the base config plus a player's choices.

              --config <path>       base gg2.ini for every room (bots, rules, rotation)
              --port <n>            the game port every room shares (default 8300)
              --prefix <letter>     this host's code prefix (default X)
              --max-rooms <n>       rooms held at once (default 8)
              --api-port <n>        the HTTP API's port (default 8301)
              --api-bind <address>  the API's bind address (default 127.0.0.1)
              --min-free-mb <n>     refuse a room under this much MemAvailable (default 256, 0 = off)
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

        if (probe.BotMode is BotMode.Search or BotMode.Tiered)
            throw new ArgumentException($"[Bots] Mode={probe.BotMode.ToString().ToLowerInvariant()} is not offered in rooms; use scripted or policy");

        probe.Validate();
        BotBrain.CheckSettings(probe);

        log.Info(BuildInfo.Description);

        using var rooms = new RoomHost(host, log);

        foreach (var path in probe.PolicyPaths)
            rooms.SharedPolicies.Load(path);

        rooms.Start();

        using var server = new RoomApi(rooms, Template, api, log);
        server.Start();

        if (!IPAddress.IsLoopback(api.Bind))
            log.Warn("room API bound beyond loopback with no device check: anybody who can reach it can fill this host");

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
        return 0;
    }
}
