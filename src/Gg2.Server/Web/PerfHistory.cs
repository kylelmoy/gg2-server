using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Gg2.Server;

/// <summary>
/// One second of the server's health, as the panel graphs it.
/// </summary>
/// <remarks>
/// ⚠️ <b>Every rate is per-second and already divided</b>, so the browser plots
/// what it is given and cannot get the denominator wrong. The one thing this
/// project has repeatedly been bitten by is a number whose denominator was not
/// the one it looked like (CLAUDE.md, "the author is the one reader guaranteed
/// to reconstruct the mistake"), and a graph is the worst place to find out.
/// </remarks>
/// <param name="Sequence">Monotonic, so the panel can ask for "everything after".</param>
/// <param name="TickMeanMs">Mean cost of <c>GameServer.Tick</c> over this second.</param>
/// <param name="TickPeakMs">The worst single tick in this second — the one that hitches.</param>
/// <param name="TickBudgetMs">The pacing interval to compare both against.</param>
/// <param name="Ticks">Ticks actually run. ⚠️ 30 at full rate, 2 when idling.</param>
/// <param name="CpuPercent">
/// Process CPU as a percentage of <b>one core</b>, over a trailing
/// <see cref="PerfHistory.CpuWindowMs"/> window.
/// <para>
/// ⚠️ <b>Not of the machine.</b> The tick loop is a single thread, so "60% of one
/// core" is the number that says how close it is to being the bottleneck;
/// dividing by the core count would hide that behind a comfortable-looking 7%.
/// It can exceed 100 — GC and the panel's own threads are in it.
/// </para>
/// <para>
/// ⛔ <b>A trailing window, not this second, and that is a measurement fact
/// rather than a smoothing preference.</b> Windows accounts process CPU at the
/// 15.625 ms clock interrupt, so a one-second delta can only ever be a multiple
/// of <b>1.56%</b> — measured: this server, which really uses about 0.4% of a
/// core with ten bots, reported an alternating 0.00 / 1.56 / 0.00 sawtooth that
/// read as spiking. Over five seconds the same quantum is 0.31%, which is below
/// the resolution anybody reads off a graph.
/// </para>
/// </param>
/// <param name="WorkingSetMb">Physical memory the process is holding.</param>
/// <param name="ManagedMb">The managed heap inside that.</param>
/// <param name="LateTicks">Ticks in this second that overran their interval.</param>
/// <param name="Collections">Gen-0/1/2 collections in this second, summed.</param>
/// <param name="BytesOut">Bytes put on the wire in this second, across every client.</param>
/// <param name="Humans">Connected clients.</param>
/// <param name="Bots">Bots on the roster.</param>
/// <param name="Characters">Live characters — the simulation's actual load.</param>
public readonly record struct PerfSample(
    long Sequence,
    DateTime Utc,
    double TickMeanMs,
    double TickPeakMs,
    double TickBudgetMs,
    int Ticks,
    double CpuPercent,
    double WorkingSetMb,
    double ManagedMb,
    int LateTicks,
    int Collections,
    long BytesOut,
    int Humans,
    int Bots,
    int Characters);

/// <summary>
/// A few minutes of <see cref="PerfSample"/>, sampled once a second on the tick
/// thread.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>Collected whenever the panel is enabled, not only while somebody is
/// watching</b> — unlike the state snapshot, which is the opposite. The whole
/// value of a graph is that it already covers the minute before you opened it:
/// an operator goes to the panel <em>because</em> something happened, and a
/// history that started when they arrived would answer a question nobody has.
/// </para>
/// <para>
/// ⚠️ <b>What that costs: one <see cref="Environment.TickCount64"/> comparison
/// per tick, and a handful of syscalls per second.</b> The comparison is the only
/// thing on the per-tick path. <see cref="Process.TotalProcessorTime"/> and
/// <see cref="Environment.WorkingSet"/> are the syscalls; at 1 Hz against a
/// 33.3 ms budget they are not measurable, and they are the reason this samples
/// at 1 Hz rather than with the 5 Hz snapshot.
/// </para>
/// <para>
/// ⚠️ <b>Sequence numbers, not indices</b> — the same rule as
/// <see cref="LogRing"/>. A browser asking by index would silently skip samples
/// whenever the ring wrapped between two polls.
/// </para>
/// </remarks>
public sealed class PerfHistory
{
    /// <summary>
    /// Samples kept: <b>300</b>, so five minutes at 1 Hz.
    /// </summary>
    /// <remarks>
    /// Long enough to cover a map rotation and the hitch that made somebody open
    /// the panel; short enough that the whole history is a ~30 KB JSON reply the
    /// page can draw in one pass.
    /// </remarks>
    public const int Capacity = 300;

    /// <summary>How often a sample is taken.</summary>
    public const int IntervalMs = 1000;

    /// <summary>
    /// The window each CPU figure is measured over: <b>5 seconds</b>.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Longer than the sample interval on purpose.</b> See
    /// <see cref="PerfSample.CpuPercent"/> — Windows accounts process CPU at the
    /// 15.625 ms clock interrupt, so a one-second delta quantises to 1.56% and a
    /// nearly-idle server draws a sawtooth. Five seconds takes that to 0.31%.
    /// The series is therefore a trailing average and lags a step change by up to
    /// five seconds, which the chart's own caption says.
    /// </remarks>
    public const int CpuWindowMs = 5000;

    private readonly Lock _gate = new();
    private readonly PerfSample[] _samples = new PerfSample[Capacity];
    private readonly Process _process = Process.GetCurrentProcess();

    private readonly Queue<(long AtMs, TimeSpan Cpu)> _cpuWindow = new();

    private long _next;
    private long _sampledAtMs;
    private long _bytesAt;
    private long _lateAt;
    private int _collectionsAt;

    /// <summary>The sequence number the next sample will take.</summary>
    public long Sequence
    {
        get
        {
            lock (_gate)
                return _next;
        }
    }

    /// <summary>
    /// Takes a sample if a second has gone by. Call once per tick.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>On the tick thread only.</b> It reads the roster and calls
    /// <see cref="GameServer.TakeTickWork"/>, which resets an accumulator — a
    /// second caller would silently halve both windows.
    /// </remarks>
    public void Sample(GameServer server)
    {
        var now = Environment.TickCount64;

        if (_sampledAtMs == 0)
        {
            // The first call establishes the baselines and takes no sample: every
            // figure here is a delta, and a delta against "process start" would
            // make the first point a spike that never happened again.
            Rebase(server, now);
            return;
        }

        var elapsedMs = now - _sampledAtMs;

        if (elapsedMs < IntervalMs)
            return;

        _process.Refresh();

        var cpu = _process.TotalProcessorTime;
        var bytes = server.BytesSent;
        var late = server.LateTicks;
        var collections = GC.CollectionCount(0) + GC.CollectionCount(1) + GC.CollectionCount(2);

        server.TakeTickWork(out var meanMs, out var peakMs, out var ticks);

        var seconds = elapsedMs / 1000.0;

        // ⚠️ Bots counted in the SAME pass, not from GameServer.BotSlots — that
        // property builds a List<int> on every access, and this runs inside
        // GameServer.Tick. Once a second is not the tick loop's hot path, but an
        // allocation in there is the kind of thing that gets copied to somewhere
        // it does matter. One walk, no garbage.
        var bots = 0;
        var characters = 0;

        for (var slot = 1; slot < server.RosterCount; slot++)
        {
            if (server.IsBot(slot))
                bots++;

            if (server.IsAlive(slot))
                characters++;
        }

        var sample = new PerfSample(
            Sequence: _next,
            Utc: DateTime.UtcNow,
            TickMeanMs: meanMs,
            TickPeakMs: peakMs,
            TickBudgetMs: server.TickInterval.TotalMilliseconds,
            Ticks: ticks,

            // ⚠️ Divided by WALL time, and by one core, over a window that is
            // deliberately longer than this sample. See PerfSample.CpuPercent.
            CpuPercent: CpuOver(now, cpu),
            WorkingSetMb: Environment.WorkingSet / (1024.0 * 1024.0),
            ManagedMb: GC.GetTotalMemory(forceFullCollection: false) / (1024.0 * 1024.0),
            LateTicks: (int)(late - _lateAt),
            Collections: collections - _collectionsAt,
            BytesOut: (long)((bytes - _bytesAt) / seconds),
            Humans: server.HumanCount,
            Bots: bots,
            Characters: characters);

        lock (_gate)
        {
            _samples[(int)(_next % Capacity)] = sample;
            _next++;
        }

        _sampledAtMs = now;
        _bytesAt = bytes;
        _lateAt = late;
        _collectionsAt = collections;
    }

    /// <summary>
    /// CPU as a percentage of one core, against the oldest reading still inside
    /// <see cref="CpuWindowMs"/>.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The oldest reading still in the window, not the one exactly
    /// <see cref="CpuWindowMs"/> back.</b> For the first few seconds of a
    /// server's life there is no such reading, and waiting for one would leave
    /// the chart empty at exactly the moment somebody is watching a start-up.
    /// The window fills; the figure is correct throughout, over a shorter span at
    /// first.
    /// </remarks>
    private double CpuOver(long now, TimeSpan cpu)
    {
        _cpuWindow.Enqueue((now, cpu));

        while (_cpuWindow.Count > 1 && now - _cpuWindow.Peek().AtMs > CpuWindowMs)
            _cpuWindow.Dequeue();

        var (thenMs, thenCpu) = _cpuWindow.Peek();
        var spanMs = now - thenMs;

        return spanMs <= 0 ? 0 : (cpu - thenCpu).TotalMilliseconds / spanMs * 100;
    }

    private void Rebase(GameServer server, long now)
    {
        _process.Refresh();

        _sampledAtMs = now;
        _bytesAt = server.BytesSent;

        _cpuWindow.Clear();
        _cpuWindow.Enqueue((now, _process.TotalProcessorTime));
        _lateAt = server.LateTicks;
        _collectionsAt = GC.CollectionCount(0) + GC.CollectionCount(1) + GC.CollectionCount(2);

        server.TakeTickWork(out _, out _, out _);
    }

    /// <summary>Every sample after <paramref name="after"/>, oldest first.</summary>
    /// <param name="after">The caller's last sequence number, or -1 for all of it.</param>
    public IReadOnlyList<PerfSample> Since(long after)
    {
        lock (_gate)
        {
            var oldest = Math.Max(0, _next - Capacity);
            var from = after < 0 ? oldest : Math.Max(oldest, after + 1);
            var samples = new List<PerfSample>((int)Math.Min(Capacity, _next - from));

            for (var sequence = from; sequence < _next; sequence++)
                samples.Add(_samples[(int)(sequence % Capacity)]);

            return samples;
        }
    }

    /// <summary>Samples after <paramref name="after"/>, as a JSON array.</summary>
    /// <remarks>
    /// ⚠️ Arrays of numbers rather than objects per point — 300 samples of
    /// fifteen named fields is 60 KB of repeated key names for 5 KB of data, on a
    /// reply the panel fetches on every connect. The order is fixed and the
    /// panel names it once; <see cref="Fields"/> is that contract.
    /// </remarks>
    public string ToJson(long after)
    {
        var samples = Since(after);
        var json = new StringBuilder(samples.Count * 96 + 256);

        json.Append("{\"fields\":[");

        for (var i = 0; i < Fields.Length; i++)
            json.Append(i == 0 ? "\"" : ",\"").Append(Fields[i]).Append('"');

        json.Append("],\"samples\":[");

        for (var i = 0; i < samples.Count; i++)
        {
            var sample = samples[i];

            if (i > 0)
                json.Append(',');

            json.Append('[').Append(sample.Sequence);
            json.Append(',').Append(sample.Utc.ToUnixTimeMillisecondsInvariant());
            json.Append(',').Append(Round(sample.TickMeanMs, 3));
            json.Append(',').Append(Round(sample.TickPeakMs, 3));
            json.Append(',').Append(Round(sample.TickBudgetMs, 3));
            json.Append(',').Append(sample.Ticks);
            json.Append(',').Append(Round(sample.CpuPercent, 2));
            json.Append(',').Append(Round(sample.WorkingSetMb, 2));
            json.Append(',').Append(Round(sample.ManagedMb, 2));
            json.Append(',').Append(sample.LateTicks);
            json.Append(',').Append(sample.Collections);
            json.Append(',').Append(sample.BytesOut);
            json.Append(',').Append(sample.Humans);
            json.Append(',').Append(sample.Bots);
            json.Append(',').Append(sample.Characters);
            json.Append(']');
        }

        json.Append("]}");
        return json.ToString();
    }

    /// <summary>The order the numbers come in. The panel reads this, not a guess.</summary>
    public static readonly string[] Fields =
    [
        "seq", "at", "tickMean", "tickPeak", "tickBudget", "ticks",
        "cpu", "rss", "heap", "late", "gc", "bytes",
        "humans", "bots", "characters",
    ];

    private static string Round(double value, int digits) =>
        Math.Round(value, digits).ToString("R", CultureInfo.InvariantCulture);
}

internal static class PerfTimeExtensions
{
    /// <summary>Unix milliseconds, which is what a browser's <c>Date</c> takes.</summary>
    public static long ToUnixTimeMillisecondsInvariant(this DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
}
