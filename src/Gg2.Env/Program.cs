using Gg2.Env;

// gg2-env: the batch-stepping surface's entry point. docs/ENV.md.
//
// ⚠️ Loopback only, one client. It executes batch commands against live
// simulation state, which is the same reason --control-port is off by default
// on the game server.
//
// ⚠️ The parsing and the wiring are in EnvOptions, not here. Top-level
// statements cannot be called by a test, and a dropped --nav in this file cost
// gg2-rl its phase 1 exit criterion while every test upstream passed — see
// EnvOptions' own remarks.
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

        --nav points at gg2-nav-gen's cache directory. Without it a served world
        has no nav graph, and an EnvDriver.Scripted side is refused rather than
        quietly downgraded to a bot that cannot walk.
        """);

    return 0;
}

if (!EnvOptions.TryParse(args, out var options, out var error))
{
    Console.Error.WriteLine(error);
    return 2;
}

if (!Directory.Exists(options.Maps))
{
    Console.Error.WriteLine($"no maps at {options.Maps}; pass --maps or set GG2_REFERENCE");
    return 1;
}

// ⚠️ Warned about rather than refused. An env with no graphs is a legitimate
// configuration — Idle and Rollout need none — so this is not an error; it is
// the one fact that decides whether Scripted can be asked for, said out loud at
// startup instead of arriving as a per-batch refusal the caller cannot explain.
if (options.Nav is { } nav && !Directory.Exists(nav))
    Console.Error.WriteLine($"warning: no nav caches at {nav}; an EnvDriver.Scripted side will be refused");
else if (options.Nav is null && options.Command == "serve")
    Console.Error.WriteLine("warning: no --nav given; an EnvDriver.Scripted side will be refused");

if (options.Command == "smoke")
{
    // docs/ENV.md's v0 exit criteria 1 and 3 in one run: a random-head agent
    // completing N duels over the real socket, and the throughput it managed.
    var result = SmokeRun.Run(options.Maps, options.Worlds, options.Duels, seed: 20260828, options.Threads);

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

var server = options.CreateServer();

// ⚠️ Printed from the BOUND callback, never from the requested `port`. Printing
// the request means `--port 0` announces "127.0.0.1:0", which is not an address
// anything can connect to — and this tool's own --help promises it prints the
// one the OS granted. A launcher that scrapes this line got 0 and failed to
// connect, which is how it was found.
server.Serve(options.Port, bound: actual =>
{
    Console.WriteLine($"gg2-env serving on 127.0.0.1:{actual}");
    Console.Out.Flush();
});

Console.WriteLine("client closed");
return 0;
