using Gg2.Env;

// gg2-env: the batch-stepping surface's entry point. docs/ENV.md.
//
// ⚠️ Loopback only, one client. It executes batch commands against live
// simulation state, which is the same reason --control-port is off by default
// on the game server.
if (args.Length == 0 || args[0] is "--help" or "-h")
{
    Console.WriteLine(
        """
        gg2-env - the batch-stepping surface for a learned policy

          serve [--port <n>] [--maps <path>] [--nav <path>]
          smoke [--worlds <n>] [--duels <n>] [--threads <n>] [--maps <path>]

        Binds 127.0.0.1 only and serves one client. A port of 0 asks the OS for
        a free one and prints which it got.

        The maps ship inside the reference Game Maker project, so --maps (or
        GG2_REFERENCE) has to point at a Gang-Garrison-2 checkout.
        """);

    return 0;
}

if (args[0] is not ("serve" or "smoke"))
{
    Console.Error.WriteLine($"unknown command '{args[0]}'; try --help");
    return 2;
}

var port = 0;
var worlds = 1;
var duels = 1000;
var threads = 1;
var maps = Environment.GetEnvironmentVariable("GG2_REFERENCE") is { } root
    ? Path.Combine(root, "Source", "gg2", "Included Files")
    : Path.Combine("D:", "code", "Gang-Garrison-2", "Source", "gg2", "Included Files");

string? nav = null;

for (var i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--port":
            port = int.Parse(args[++i]);
            break;
        case "--maps":
            maps = args[++i];
            break;
        case "--nav":
            nav = args[++i];
            break;
        case "--worlds":
            worlds = int.Parse(args[++i]);
            break;
        case "--duels":
            duels = int.Parse(args[++i]);
            break;
        case "--threads":
            threads = int.Parse(args[++i]);
            break;
        default:
            Console.Error.WriteLine($"unknown option '{args[i]}'");
            return 2;
    }
}

if (!Directory.Exists(maps))
{
    Console.Error.WriteLine($"no maps at {maps}; pass --maps or set GG2_REFERENCE");
    return 1;
}

if (args[0] == "smoke")
{
    // docs/ENV.md's v0 exit criteria 1 and 3 in one run: a random-head agent
    // completing N duels over the real socket, and the throughput it managed.
    var result = SmokeRun.Run(maps, worlds, duels, seed: 20260828, threads);

    Console.WriteLine(result);

    // ⚠️ The build is printed WITH the number, never left to the reader.
    // CLAUDE.md's standing rule: this project's headroom figure sat stale in
    // four documents because a re-measure forgot to name its configuration, and
    // Debug reads about half of Release. A throughput number without a build is
    // not a measurement.
#if DEBUG
    Console.WriteLine("build: DEBUG - roughly half of Release; do not quote this as the number");
#else
    Console.WriteLine("build: RELEASE");
#endif

    return 0;
}

var server = new EnvServer(maps);

// ⚠️ Printed from the BOUND callback, never from the requested `port`. Printing
// the request means `--port 0` announces "127.0.0.1:0", which is not an address
// anything can connect to — and this tool's own --help promises it prints the
// one the OS granted. A launcher that scrapes this line got 0 and failed to
// connect, which is how it was found.
server.Serve(port, bound: actual =>
{
    Console.WriteLine($"gg2-env serving on 127.0.0.1:{actual}");
    Console.Out.Flush();
});

Console.WriteLine("client closed");
return 0;
