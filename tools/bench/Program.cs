using System.Buffers.Binary;
using System.Diagnostics;
using Gg2.Bots;
using Gg2.Env;
using Gg2.Simulation;
using Gg2.Protocol;

var maps = EnvOptions.DefaultMaps;

int worlds = args.Length > 0 ? int.Parse(args[0]) : 64;
int rounds = args.Length > 1 ? int.Parse(args[1]) : 400;
int threads = args.Length > 2 ? int.Parse(args[2]) : 1;

var spec = new DuelSpec
{
    Map = Environment.GetEnvironmentVariable("BENCH_MAP") ?? "gg_debug",
    RedClass = PlayerClass.Soldier,
    BlueClass = PlayerClass.Soldier,
    RedX = 600, RedY = 850, BlueX = 700, BlueY = 850,
    DecisionCap = 120,
    ActionRepeat = 2,
};

if (Environment.GetEnvironmentVariable("BENCH_SEARCH") == "1")
{
    var snd = Environment.GetEnvironmentVariable("GG2_NAV");
    if (snd is not null && !Directory.Exists(snd)) snd = null;
    Bench.SearchBench.Run(maps, snd, Environment.GetEnvironmentVariable("BENCH_MAP") ?? "gg_debug");
    return;
}

if (Environment.GetEnvironmentVariable("BENCH_LAT") == "1")
{
    var lnd = Environment.GetEnvironmentVariable("GG2_NAV");
    if (lnd is not null && !Directory.Exists(lnd)) lnd = null;
    Bench.LatencyBench.Run(maps, lnd, Environment.GetEnvironmentVariable("BENCH_MAP") ?? "gg_debug", int.Parse(args.Length > 0 ? args[0] : "16"), int.Parse(args.Length > 1 ? args[1] : "20000"));
    return;
}

if (Environment.GetEnvironmentVariable("BENCH_OBS") is { } obsOut)
{
    var ond = Environment.GetEnvironmentVariable("GG2_NAV");
    if (ond is not null && !Directory.Exists(ond)) ond = null;
    Bench.ObsDump.Run(maps, ond, Environment.GetEnvironmentVariable("BENCH_MAP") ?? "gg_debug", obsOut, 64);
    return;
}

if (Environment.GetEnvironmentVariable("BENCH_CMP") == "1")
{
    var d = Environment.GetEnvironmentVariable("SCRATCH")!;
    Bench.PolicyCompare.Run(d + "/obs.bin", d + "/expected.txt", Environment.GetEnvironmentVariable("NPZ")!);
    return;
}

if (Environment.GetEnvironmentVariable("BENCH_EVAL") is { } perMap)
{
    Bench.PolicyEval.Run(maps, Environment.GetEnvironmentVariable("GG2_NAV")!, Environment.GetEnvironmentVariable("NPZ")!, int.Parse(perMap));
    return;
}

if (Environment.GetEnvironmentVariable("BENCH_POLICYCOST") is not null)
{
    Bench.PolicyCost.Run(
        Environment.GetEnvironmentVariable("NPZ")!,
        Environment.GetEnvironmentVariable("OBS")!,
        maps,
        Environment.GetEnvironmentVariable("GG2_NAV")!);
    return;
}

if (Environment.GetEnvironmentVariable("BENCH_SWEEP") is { } sweepArg)
{
    Bench.RangeSweep.Run(maps, Environment.GetEnvironmentVariable("GG2_NAV")!, Environment.GetEnvironmentVariable("NPZ")!, int.Parse(sweepArg));
    return;
}

if (Environment.GetEnvironmentVariable("BENCH_ARMS") is { } armsN)
{
    Bench.ArmsEval.Run(
        maps,
        Environment.GetEnvironmentVariable("GG2_NAV")!,
        Environment.GetEnvironmentVariable("NPZ")!,
        int.Parse(armsN));
    return;
}

if (Environment.GetEnvironmentVariable("BENCH_PERMAP") is { } pm)
{
    Bench.PerMapEval.Run(maps, Environment.GetEnvironmentVariable("GG2_NAV")!, Environment.GetEnvironmentVariable("NPZ")!, int.Parse(pm));
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
    Console.WriteLine($"ObservationWriter.Length = {ObservationWriter.Length}");
    Console.WriteLine($"ActionDecoder.AimBins   = {ActionDecoder.AimBins}");
    Console.WriteLine($"SimulationStamp.Uuid    = {Gg2.Simulation.SimulationStamp.Uuid}");
    Console.WriteLine($"ActionEncodingVersion   = {Gg2.Simulation.SimulationStamp.ActionEncodingVersion}");
    Console.WriteLine($"ObservationLayoutVersion= {Gg2.Simulation.SimulationStamp.ObservationLayoutVersion}");
    return;
}

if (Environment.GetEnvironmentVariable("BENCH_COLD") == "1")
{
    var ncd = Environment.GetEnvironmentVariable("GG2_NAV");
    if (ncd is not null && !Directory.Exists(ncd)) ncd = null;
    Bench.ColdBench.Run(maps, ncd, Environment.GetEnvironmentVariable("BENCH_MAP") ?? "gg_debug", int.Parse(args.Length > 0 ? args[0] : "16"));
    return;
}

if (Environment.GetEnvironmentVariable("BENCH_ALLOC") == "1")
{
    Bench.AllocProbe.Run(maps);
    return;
}

if (Environment.GetEnvironmentVariable("BENCH_DIGEST") == "1")
{
    var nd = Environment.GetEnvironmentVariable("GG2_NAV");
    if (nd is not null && !Directory.Exists(nd)) nd = null;
    Bench.DigestBench.Run(maps, nd, Environment.GetEnvironmentVariable("BENCH_MAP") ?? "gg_debug", int.Parse(args.Length > 0 ? args[0] : "16"), int.Parse(args.Length > 1 ? args[1] : "400"));
    return;
}

if (Environment.GetEnvironmentVariable("BENCH_DUEL") == "1")
{
    Bench.DuelStepBench.Run(maps, Environment.GetEnvironmentVariable("GG2_NAV"), Environment.GetEnvironmentVariable("BENCH_MAP") ?? "gg_debug", int.Parse(args.Length > 0 ? args[0] : "16"), int.Parse(args.Length > 1 ? args[1] : "400"));
    return;
}

if (Environment.GetEnvironmentVariable("BENCH_RAY") == "1")
{
    Bench.RayBench.Run(maps, Environment.GetEnvironmentVariable("GG2_NAV"), Environment.GetEnvironmentVariable("BENCH_MAP") ?? "gg_debug");
    return;
}

if (Environment.GetEnvironmentVariable("BENCH_TICK") == "1")
{
    Bench.TickBench.Run(maps, Environment.GetEnvironmentVariable("GG2_NAV"), Environment.GetEnvironmentVariable("BENCH_MAP") ?? "gg_debug");
    return;
}

if (Environment.GetEnvironmentVariable("BENCH_GEOM") == "1")
{
    Bench.GeomBench.Run(maps, Environment.GetEnvironmentVariable("GG2_NAV"), Environment.GetEnvironmentVariable("BENCH_MAP") ?? "gg_debug");
    return;
}

if (Environment.GetEnvironmentVariable("BENCH_RESET") == "1")
{
    Bench.ResetBench.Run(maps, Environment.GetEnvironmentVariable("GG2_NAV"));
    return;
}

var navDir = Environment.GetEnvironmentVariable("GG2_NAV");
if (navDir is not null && !Directory.Exists(navDir)) navDir = null;
var swBuild = Stopwatch.StartNew();
var batch = new EnvBatch(worlds, spec, 20260828, maps, navDir, threads);
swBuild.Stop();
Console.WriteLine($"map={spec.Map} nav={(navDir is null ? "(none)" : "yes")} ctor={swBuild.Elapsed.TotalMilliseconds:0.0} ms");

var red = new EnvAction[worlds];
var blue = new EnvAction[worlds];
var redOut = new StepResult[worlds];
var blueOut = new StepResult[worlds];
var rng = new Random(1);

void FillActions()
{
    for (var i = 0; i < worlds; i++)
    {
        red[i] = Rand();
        blue[i] = Rand();
    }
    EnvAction Rand() => new()
    {
        Horizontal = (byte)rng.Next(3),
        Jump = (byte)rng.Next(2),
        Down = (byte)rng.Next(2),
        Fire = (byte)rng.Next(3),
        AimDelta = (byte)rng.Next(ActionDecoder.AimBins),
        Zoom = (byte)rng.Next(2),
    };
}

var stride = ObservationWriter.Length;
var obs = new float[worlds * 2 * stride];
var reply = new byte[1 + 4 + (obs.Length * 4) + (worlds * 2 * EnvProtocol.ComponentBytes)];

// ⚠️ Warm to TIER-1, not just "a few iterations". .NET promotes at 30 calls but
// the rejit is asynchronous: a 200-round warmup at one world measured 23 us a
// tick where the same code at 64 worlds measured 2.8, because the small run
// never left tier-0. Warm by world-steps, and give the background JIT a moment.
var warmRounds = Math.Max(200, 60_000 / worlds);
for (var w = 0; w < warmRounds; w++)
{
    FillActions();
    batch.Step(red, blue, redOut, blueOut);
    for (var i = 0; i < worlds; i++)
    {
        var wd = batch.World(i);
        var v = new WorldView(in wd.World.State, wd.World.Collision);
        ObservationWriter.Write(in v, wd.RedSlot, obs.AsSpan(0, stride));
    }
}
Thread.Sleep(200);

double tStep = 0, tObs = 0, tSer = 0;
long allocated = 0;
var gc0 = GC.CollectionCount(0);
var resets = 0;
var sw = new Stopwatch();

for (var r = 0; r < rounds; r++)
{
    FillActions();

    var a0 = GC.GetAllocatedBytesForCurrentThread();
    sw.Restart();
    batch.Step(red, blue, redOut, blueOut);
    sw.Stop();
    allocated += GC.GetAllocatedBytesForCurrentThread() - a0;
    tStep += sw.Elapsed.TotalMilliseconds;
    for (var i = 0; i < worlds; i++) if (redOut[i].Done) resets++;

    sw.Restart();
    for (var i = 0; i < worlds; i++)
    {
        var world = batch.World(i);
        var view = new WorldView(in world.World.State, world.World.Collision);
        ObservationWriter.Write(in view, world.RedSlot, obs.AsSpan(((i * 2) + 0) * stride, stride));
        ObservationWriter.Write(in view, world.BlueSlot, obs.AsSpan(((i * 2) + 1) * stride, stride));
    }
    sw.Stop();
    tObs += sw.Elapsed.TotalMilliseconds;

    sw.Restart();
    var at = 5;
    foreach (var value in obs)
    {
        BinaryPrimitives.WriteSingleLittleEndian(reply.AsSpan(at), value);
        at += 4;
    }
    sw.Stop();
    tSer += sw.Elapsed.TotalMilliseconds;
}

var total = tStep + tObs + tSer;
Console.WriteLine($"worlds={worlds} rounds={rounds} threads={threads}");
Console.WriteLine($"  batch.Step   {tStep / rounds:0.000} ms/round  ({tStep / total * 100:0.0}%)");
Console.WriteLine($"  observations {tObs / rounds:0.000} ms/round  ({tObs / total * 100:0.0}%)");
Console.WriteLine($"  serialise    {tSer / rounds:0.000} ms/round  ({tSer / total * 100:0.0}%)");
Console.WriteLine($"  alloc/round  {allocated / (double)rounds:0} bytes in batch.Step; gen0 collections {GC.CollectionCount(0) - gc0}");
Console.WriteLine($"  resets       {resets} over {rounds} rounds ({(double)resets/rounds:0.00}/round)");
Console.WriteLine($"  total        {total / rounds:0.000} ms/round -> {worlds * rounds / (total / 1000):0} decision steps/sec (no socket)");
#if DEBUG
Console.WriteLine("build: DEBUG");
#else
Console.WriteLine("build: RELEASE");
#endif
