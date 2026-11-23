using System.Buffers.Binary;
using System.Diagnostics;
using GG2.Bots;
using GG2.Simulation;
using GG2.Protocol;

// The reference's Included Files when GG2_REFERENCE is set, otherwise this
// repo's checked-in maps/ — byte-identical to them, which MapCopyTests gates.
var maps = Environment.GetEnvironmentVariable("GG2_REFERENCE") is { Length: > 0 } reference
    ? Path.Combine(reference, "Source", "gg2", "Included Files")
    : RepositoryMaps();

static string RepositoryMaps()
{
    for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
    {
        if (File.Exists(Path.Combine(directory.FullName, "GG2.sln")))
            return Path.Combine(directory.FullName, "maps");
    }

    throw new DirectoryNotFoundException("no GG2.sln above the bench binary; set GG2_REFERENCE");
}

// Both flight modes want a nav directory and are meaningless without one, so
// they resolve it the loud way rather than falling back to "no graph, no edges,
// zero of zero flown" — which is a pass.
static string FlightNav()
{
    var dir = Environment.GetEnvironmentVariable("GG2_NAV");

    if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        throw new InvalidOperationException("BENCH_FLIGHT needs GG2_NAV pointing at a nav directory");

    return dir;
}

static bool FlightDistances() => Environment.GetEnvironmentVariable("BENCH_FLIGHT_NODIST") != "1";

static string? FlightFocus() =>
    Environment.GetEnvironmentVariable("BENCH_FOCUS") ?? Environment.GetEnvironmentVariable("BENCH_MAP");

if (Environment.GetEnvironmentVariable("BENCH_FLIGHT_TRACE") is { } traceSpec)
{
    Bench.FlightBench.Trace(
        Bench.FlightBench.ResolveMaps(maps),
        FlightNav(),
        FlightDistances(),
        traceSpec,
        int.Parse(Environment.GetEnvironmentVariable("BENCH_FLIGHT_BUDGET") ?? "400"));
    return;
}

if (Environment.GetEnvironmentVariable("BENCH_FLIGHT_LAUNCH") is { } launchControl)
{
    Bench.FlightBench.Launches(
        Bench.FlightBench.ResolveMaps(maps),
        FlightNav(),
        int.Parse(Environment.GetEnvironmentVariable("BENCH_FLIGHT_BUDGET") ?? "4000"),
        FlightDistances(),
        int.Parse(Environment.GetEnvironmentVariable("BENCH_FLIGHT_THREADS") ?? "0"),
        int.Parse(launchControl),
        int.Parse(Environment.GetEnvironmentVariable("BENCH_FLIGHT_SEED") ?? "20260905"),
        Environment.GetEnvironmentVariable("BENCH_FLIGHT_CSV"));
    return;
}

if (Environment.GetEnvironmentVariable("BENCH_FLIGHT_ORACLE") is { } oracleControl)
{
    Bench.FlightBench.Oracle(
        Bench.FlightBench.ResolveMaps(maps),
        FlightNav(),
        int.Parse(Environment.GetEnvironmentVariable("BENCH_FLIGHT_BUDGET") ?? "4000"),
        FlightDistances(),
        int.Parse(Environment.GetEnvironmentVariable("BENCH_FLIGHT_THREADS") ?? "0"),
        int.Parse(oracleControl),
        int.Parse(Environment.GetEnvironmentVariable("BENCH_FLIGHT_SEED") ?? "20260905"),
        Environment.GetEnvironmentVariable("BENCH_FLIGHT_CSV"));
    return;
}

if (Environment.GetEnvironmentVariable("BENCH_FLIGHT_ROUTES") is { } routeN)
{
    Bench.FlightBench.Routes(
        Bench.FlightBench.ResolveMaps(maps),
        FlightNav(),
        int.Parse(routeN),
        int.Parse(Environment.GetEnvironmentVariable("BENCH_FLIGHT_BUDGET") ?? "3000"),
        FlightDistances(),
        int.Parse(Environment.GetEnvironmentVariable("BENCH_FLIGHT_SEED") ?? "20260905"),
        FlightFocus());
    return;
}

if (Environment.GetEnvironmentVariable("BENCH_FLIGHT") == "1")
{
    Bench.FlightBench.Run(
        Bench.FlightBench.ResolveMaps(maps),
        FlightNav(),
        int.Parse(Environment.GetEnvironmentVariable("BENCH_FLIGHT_BUDGET") ?? "400"),
        FlightDistances(),
        int.Parse(Environment.GetEnvironmentVariable("BENCH_FLIGHT_THREADS") ?? "0"),
        Environment.GetEnvironmentVariable("BENCH_FLIGHT_CSV"),
        FlightFocus());
    return;
}

if (Environment.GetEnvironmentVariable("BENCH_COMBAT") is { } combatTicks)
{
    var cnd = Environment.GetEnvironmentVariable("GG2_NAV");
    if (cnd is not null && !Directory.Exists(cnd)) cnd = null;
    Bench.CombatBench.Run(maps, cnd, Environment.GetEnvironmentVariable("BENCH_MAP") ?? "ctf_truefort", int.Parse(combatTicks));
    return;
}

if (Environment.GetEnvironmentVariable("BENCH_EVAL") is { } perMap)
{
    Bench.PolicyEval.Run(maps, Environment.GetEnvironmentVariable("GG2_NAV")!, Environment.GetEnvironmentVariable("POLICY")!, int.Parse(perMap));
    return;
}

if (Environment.GetEnvironmentVariable("BENCH_SWEEP") is { } sweepArg)
{
    Bench.RangeSweep.Run(maps, Environment.GetEnvironmentVariable("GG2_NAV")!, Environment.GetEnvironmentVariable("POLICY")!, int.Parse(sweepArg));
    return;
}

// The repo's own nav/ and checkpoints/ unless GG2_NAV and CHECKPOINTS say
// otherwise: both sit next to maps/ in a clone.
static string Beside(string maps, string name, string variable) =>
    Environment.GetEnvironmentVariable(variable) is { Length: > 0 } set
        ? set
        : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(maps))!, name);

if (Environment.GetEnvironmentVariable("BENCH_TIERDUEL") is { } tierDuels)
{
    Bench.Tiers.RunDuels(maps, Beside(maps, "nav", "GG2_NAV"), Beside(maps, "checkpoints", "CHECKPOINTS"), int.Parse(tierDuels));
    return;
}

if (Environment.GetEnvironmentVariable("BENCH_HANDOVER") is { } handoverMatches)
{
    Bench.HandoverArms.Run(maps, Beside(maps, "nav", "GG2_NAV"), Beside(maps, "checkpoints", "CHECKPOINTS"), int.Parse(handoverMatches));
    return;
}

if (Environment.GetEnvironmentVariable("BENCH_RELEASE") is { } releaseSeeds)
{
    Bench.ReleaseReasons.Run(maps, Beside(maps, "nav", "GG2_NAV"), Beside(maps, "checkpoints", "CHECKPOINTS"), int.Parse(releaseSeeds));
    return;
}

if (Environment.GetEnvironmentVariable("BENCH_TURNS") is { } turnMatches)
{
    Bench.Turns.Run(maps, Beside(maps, "nav", "GG2_NAV"), Beside(maps, "checkpoints", "CHECKPOINTS"), int.Parse(turnMatches));
    return;
}

if (Environment.GetEnvironmentVariable("BENCH_DUEL_LADDER") is { } ladderDuels)
{
    Bench.DuelLevels.Run(maps, Beside(maps, "nav", "GG2_NAV"), Beside(maps, "checkpoints", "CHECKPOINTS"), int.Parse(ladderDuels));
    return;
}

if (Environment.GetEnvironmentVariable("BENCH_DODGE") is { } dodgeDuels)
{
    Bench.Dodge.Run(maps, Beside(maps, "nav", "GG2_NAV"), Beside(maps, "checkpoints", "CHECKPOINTS"), int.Parse(dodgeDuels));
    return;
}

if (Environment.GetEnvironmentVariable("BENCH_TIERS") is { } tierMatches)
{
    Bench.Tiers.RunMatches(maps, Beside(maps, "nav", "GG2_NAV"), Beside(maps, "checkpoints", "CHECKPOINTS"), int.Parse(tierMatches));
    return;
}

if (Environment.GetEnvironmentVariable("BENCH_CHURN") is { } churnN)
{
    Bench.ChurnBench.Run(
        maps,
        Environment.GetEnvironmentVariable("GG2_NAV")!,
        Environment.GetEnvironmentVariable("POLICY")!,
        int.Parse(churnN));
    return;
}

if (Environment.GetEnvironmentVariable("BENCH_PERMAP") is { } pm)
{
    Bench.PerMapEval.Run(maps, Environment.GetEnvironmentVariable("GG2_NAV")!, Environment.GetEnvironmentVariable("POLICY")!, int.Parse(pm));
    return;
}

if (Environment.GetEnvironmentVariable("BENCH_DKOTH2") == "1")
{
    Bench.DkothWhere.Run(maps, Environment.GetEnvironmentVariable("GG2_NAV")!);
    return;
}

if (Environment.GetEnvironmentVariable("BENCH_DKOTH") == "1")
{
    Bench.DkothProbe.Run(maps, Environment.GetEnvironmentVariable("GG2_NAV")!);
    return;
}

if (Environment.GetEnvironmentVariable("BENCH_CONST") == "1")
{
    Console.WriteLine($"ActionDecoder.AimBins   = {ActionDecoder.AimBins}");
    Console.WriteLine($"SimulationStamp.Uuid    = {GG2.Simulation.SimulationStamp.Uuid}");

    // The un-gated half: whether the world the bots were tuned on has moved.
    Console.WriteLine(
        $"SimulationStamp.Routing = {GG2.Simulation.SimulationStamp.RoutingUuid} (reported, not gated)");
    Console.WriteLine($"ActionEncodingVersion   = {GG2.Simulation.SimulationStamp.ActionEncodingVersion}");
    Console.WriteLine($"ObservationLayoutVersion= {GG2.Simulation.SimulationStamp.ObservationLayoutVersion}");
    return;
}

if (Environment.GetEnvironmentVariable("BENCH_COLD") == "1")
{
    var ncd = Environment.GetEnvironmentVariable("GG2_NAV");
    if (ncd is not null && !Directory.Exists(ncd)) ncd = null;
    Bench.ColdBench.Run(maps, ncd, Environment.GetEnvironmentVariable("BENCH_MAP") ?? "gg_debug", int.Parse(args.Length > 0 ? args[0] : "16"));
    return;
}

if (Environment.GetEnvironmentVariable("BENCH_DUEL") == "1")
{
    Bench.DuelStepBench.Run(maps, Environment.GetEnvironmentVariable("GG2_NAV"), Environment.GetEnvironmentVariable("BENCH_MAP") ?? "gg_debug", int.Parse(args.Length > 0 ? args[0] : "16"), int.Parse(args.Length > 1 ? args[1] : "400"));
    return;
}

if (Environment.GetEnvironmentVariable("BENCH_ROOMS") is { } roomCount)
{
    Bench.RoomsBench.Run(int.Parse(roomCount));
    return;
}

if (Environment.GetEnvironmentVariable("BENCH_TICK") == "1")
{
    Bench.TickBench.Run(maps, Environment.GetEnvironmentVariable("GG2_NAV"), Environment.GetEnvironmentVariable("BENCH_MAP") ?? "gg_debug");
    return;
}

if (Environment.GetEnvironmentVariable("BENCH_RESET") == "1")
{
    Bench.ResetBench.Run(maps, Environment.GetEnvironmentVariable("GG2_NAV"));
    return;
}

Console.Error.WriteLine("no mode selected: set one of the BENCH_* variables in README.md.");
return;
