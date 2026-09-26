using System.Net.Sockets;
using System.Diagnostics;
using System.Globalization;
using LoadProbe;

// loadprobe — N spectator clients against a real server, measuring when the
// broadcasts arrive. tools/loadprobe/README.md has the traps; read it before
// reading a number out of this.

var argv = Environment.GetCommandLineArgs();

string Arg(string name, string fallback)
{
    for (var i = 1; i < argv.Length - 1; i++)
        if (argv[i] == "--" + name)
            return argv[i + 1];

    return fallback;
}

var host = Arg("host", "");
var port = int.Parse(Arg("port", "8190"), CultureInfo.InvariantCulture);
var clients = int.Parse(Arg("clients", "1"), CultureInfo.InvariantCulture);
var seconds = double.Parse(Arg("seconds", "60"), CultureInfo.InvariantCulture);
var warmup = double.Parse(Arg("warmup", "5"), CultureInfo.InvariantCulture);
var staggerMs = int.Parse(Arg("stagger-ms", "250"), CultureInfo.InvariantCulture);
var label = Arg("label", clients + "c");
var outPath = Arg("out", "");
var gapsPath = Arg("gaps", "");
var namePrefix = Arg("name-prefix", "probe");

if (host.Length == 0)
{
    Console.Error.WriteLine("usage: loadprobe --host <h> [--port 8190] [--clients N] [--seconds S]");
    Console.Error.WriteLine("       [--warmup S] [--stagger-ms N] [--label L] [--out t.tsv] [--gaps g.tsv]");
    return 2;
}

// Enough room that the arrival list never grows mid-measurement: a List<T>
// resize is an allocation and a copy on the very thread being timed.
var expectedReads = (int)((seconds + warmup + 5) * 40) + 64;

Console.WriteLine($"==> {clients} client(s) -> {host}:{port}, {seconds:0}s after a {warmup:0}s warmup");

var probes = new List<ProbeClient>();
var threads = new List<Thread>();
var stop = new CancellationTokenSource();

// Staggered, because MaxAcceptsPerTick is 32 and a simultaneous burst would
// measure the accept path rather than the steady state. It also keeps the joins
// out of one tick, so the per-join bursts do not pile into one broadcast.
for (var i = 0; i < clients; i++)
{
    ProbeClient probe;

    try
    {
        probe = new ProbeClient(host, port, expectedReads);
        probe.Join($"{namePrefix}{i:00}");
    }
    catch (Exception e)
    {
        Console.Error.WriteLine($"  client {i:00} could not join: {e.Message}");

        // Stop rather than carry on with fewer: a run with fewer clients than
        // its label claims is a mislabelled measurement, which is worse than no
        // run at all.
        stop.Cancel();
        foreach (var p in probes) p.Dispose();
        foreach (var t in threads) t.Join(2000);
        return 1;
    }

    probes.Add(probe);

    var thread = new Thread(() => probe.Record(stop.Token)) { IsBackground = true };
    thread.Start();
    threads.Add(thread);

    if (i == 0)
        Console.WriteLine($"  joined \"{probe.ServerName}\" on {probe.Map}");

    if (staggerMs > 0 && i < clients - 1)
        Thread.Sleep(staggerMs);
}

// --input makes every client also SEND an INPUTSTATE a tick, which is what a
// playing client does. One thread for all of them, on a stopwatch deadline
// rather than a Sleep-per-client: a Windows Sleep(33) lands anywhere from 33 to
// 48 ms, so a thread per client would quietly send at ~25 Hz and the arm would
// be mislabelled.
var sendInput = Arg("input", "0") != "0";
var inputRate = double.Parse(Arg("input-hz", "30"), CultureInfo.InvariantCulture);
Thread? sender = null;
var sent = 0L;

if (sendInput)
{
    sender = new Thread(() =>
    {
        var period = Stopwatch.Frequency / inputRate;
        var next = Stopwatch.GetTimestamp() + period;
        var tick = 0;

        while (!stop.IsCancellationRequested)
        {
            var now = Stopwatch.GetTimestamp();

            if (now < next)
            {
                var msLeft = (next - now) * 1000.0 / Stopwatch.Frequency;
                if (msLeft > 2) Thread.Sleep((int)(msLeft - 1));
                continue;
            }

            next += period;
            tick++;

            foreach (var p in probes)
            {
                try
                {
                    p.SendInput(tick);
                    Interlocked.Increment(ref sent);
                }
                catch (Exception e) when (e is SocketException or ObjectDisposedException)
                {
                    return;
                }
            }
        }
    }) { IsBackground = true };

    sender.Start();
    Console.WriteLine($"  sending INPUTSTATE at {inputRate:0} Hz from every client");
}

var cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
var settleAt = Stopwatch.GetTimestamp();

Thread.Sleep(TimeSpan.FromSeconds(warmup));
var fromAt = Stopwatch.GetTimestamp();

Thread.Sleep(TimeSpan.FromSeconds(seconds));
var untilAt = Stopwatch.GetTimestamp();

stop.Cancel();

sender?.Join(1000);

// The threads are blocked in Receive, so cancelling the token does not wake
// them — disposing the socket is what does.
foreach (var p in probes) p.Dispose();
foreach (var t in threads) t.Join(2000);

var cpuUsed = Process.GetCurrentProcess().TotalProcessorTime - cpuBefore;
var wall = (untilAt - settleAt) / (double)Stopwatch.Frequency;

// --- statistics -------------------------------------------------------------

static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

static double Quantile(List<double> sorted, double q)
{
    if (sorted.Count == 0)
        return double.NaN;

    var at = (int)Math.Ceiling(q * sorted.Count) - 1;
    return sorted[Math.Clamp(at, 0, sorted.Count - 1)];
}

var allGaps = new List<double>();
var perClient = new List<(int Index, int Reads, long Bytes, List<double> Gaps, string? Fault)>();

for (var i = 0; i < probes.Count; i++)
{
    var probe = probes[i];
    var gaps = new List<double>();
    var reads = 0;
    long bytes = 0;

    // Only the window between the end of the warmup and the end of the run. The
    // join burst, and the staggered arrival of the later clients, both fall
    // before fromAt.
    for (var k = 1; k < probe.Arrivals.Count; k++)
    {
        var at = probe.Arrivals[k];

        if (at < fromAt || at > untilAt)
            continue;

        gaps.Add(Ms(at - probe.Arrivals[k - 1]));
        reads++;
        bytes += probe.Sizes[k];
    }

    gaps.Sort();
    allGaps.AddRange(gaps);
    perClient.Add((i, reads, bytes, gaps, probe.Fault));
}

allGaps.Sort();

var window = (untilAt - fromAt) / (double)Stopwatch.Frequency;
var faults = perClient.Count(c => c.Fault is not null);
var probeCpuPercent = cpuUsed.TotalSeconds / wall * 100;

Console.WriteLine();
Console.WriteLine($"  window   {window:0.0}s, {probes.Count} client(s), {faults} fault(s)");
Console.WriteLine($"  probe    {probeCpuPercent:0.0}% of one core on THIS box"
                  + $" ({cpuUsed.TotalSeconds:0.00}s over {wall:0.0}s)");

if (allGaps.Count == 0)
{
    Console.Error.WriteLine("  NO ARRIVALS IN THE WINDOW - nothing to report");

    foreach (var c in perClient.Where(c => c.Fault is not null))
        Console.Error.WriteLine($"    client {c.Index:00}: {c.Fault}");

    return 1;
}

// Reads per client per second against the 30 Hz the protocol promises. This is
// the check that says whether one read is one broadcast: docs/PROTOCOL.md's
// cadence is one broadcast a tick, so ~30 means the gap distribution below IS
// the broadcast distribution. Well under 30 means reads are coalescing several
// ticks and the gaps are inflated; well over means one broadcast is arriving
// split across reads and they are deflated. Either way the number below has to
// be read differently, so it is printed next to them rather than in a note.
var readsPerSecond = perClient.Sum(c => c.Reads) / (double)probes.Count / window;
var kibPerSecond = perClient.Sum(c => c.Bytes) / window / 1024.0;

Console.WriteLine($"  reads/s  {readsPerSecond:0.0} per client (30.0 = one read per broadcast)");
Console.WriteLine($"  bytes/s  {kibPerSecond:0.0} KiB/s summed over every client");

if (sendInput)
    Console.WriteLine($"  sent     {sent} INPUTSTATE over the run"
                      + $" ({sent / wall / Math.Max(probes.Count, 1):0.0}/client/s)");
Console.WriteLine();
Console.WriteLine($"  inter-arrival ms, pooled over {probes.Count} client(s), n={allGaps.Count}");
Console.WriteLine($"    p50 {Quantile(allGaps, 0.50):0.0}  p90 {Quantile(allGaps, 0.90):0.0}"
                  + $"  p99 {Quantile(allGaps, 0.99):0.0}  p99.9 {Quantile(allGaps, 0.999):0.0}"
                  + $"  max {allGaps[^1]:0.0}");

// The tail is the whole question. A player does not experience a mean, they
// experience the freeze: at 33.3 ms a broadcast, 100 ms is about three missed
// in a row, which is where dead reckoning visibly diverges.
foreach (var threshold in new[] { 50.0, 100.0, 200.0, 500.0 })
{
    var over = allGaps.Count(g => g > threshold);

    Console.WriteLine($"    over {threshold,3:0}ms  {over,6}  ({over * 100.0 / allGaps.Count:0.000}%)"
                      + $"  {over / window / probes.Count:0.00}/client/s");
}

foreach (var c in perClient.Where(c => c.Fault is not null))
    Console.WriteLine($"    client {c.Index:00} FAULT: {c.Fault}");

if (outPath.Length > 0)
{
    var header = !File.Exists(outPath);
    using var w = new StreamWriter(outPath, append: true);

    if (header)
        w.WriteLine("label\tclients\twindow_s\treads_per_s\tkib_per_s\tn\tp50\tp90\tp99\tp999\tmax"
                    + "\tover50\tover100\tover200\tover500\tfaults\tprobe_cpu_pct");

    w.WriteLine(string.Join("\t",
        label, probes.Count, $"{window:0.0}", $"{readsPerSecond:0.0}", $"{kibPerSecond:0.0}",
        allGaps.Count,
        $"{Quantile(allGaps, 0.50):0.0}", $"{Quantile(allGaps, 0.90):0.0}",
        $"{Quantile(allGaps, 0.99):0.0}", $"{Quantile(allGaps, 0.999):0.0}", $"{allGaps[^1]:0.0}",
        allGaps.Count(g => g > 50), allGaps.Count(g => g > 100),
        allGaps.Count(g => g > 200), allGaps.Count(g => g > 500),
        faults, $"{probeCpuPercent:0.0}"));

    Console.WriteLine($"  appended to {outPath}");
}

if (gapsPath.Length > 0)
{
    using var w = new StreamWriter(gapsPath, append: true);

    foreach (var c in perClient)
        foreach (var g in c.Gaps)
            w.WriteLine($"{label}\t{c.Index}\t{g.ToString("0.000", CultureInfo.InvariantCulture)}");

    Console.WriteLine($"  raw gaps appended to {gapsPath}");
}

return 0;
