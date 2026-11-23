using System.Diagnostics;
using GG2.Bots;
using GG2.Server;
using GG2.Server.Rooms;

namespace Bench;

/// <summary>
/// <c>BENCH_ROOMS=&lt;n&gt;</c>: what <c>n</c> rooms cost one room host's tick
/// thread (<c>docs/ROOMS.md</c>, "Measured").
/// </summary>
/// <remarks>
/// <para>
/// A real <see cref="RoomHost"/> on loopback, rooms built by
/// <see cref="RoomTemplate.Apply"/> exactly as the API builds them, and the host
/// ticked back to back (unpaced) so the number is work, not pacing.
/// <c>BENCH_ROOMS_MODE</c> is <c>scripted</c> or <c>policy</c> (the shipped
/// bundle under the fleet's <c>HandoverRule=distance</c>); <c>BENCH_ROOMS_BOTS</c>
/// is the bots choice (default <c>normal</c>); <c>BENCH_ROOMS_TEAM</c> the team
/// size (default <c>medium</c>, ten bots); <c>BENCH_ROOMS_ENGAGE</c>
/// <c>anyone</c> (default) or <c>humans</c>.
/// <b>A live policy room engages humans only</b>
/// (<see cref="ServerOptions.BotEngagesBotsToo"/> off), so with nobody connected
/// <c>humans</c> never hands a net the wheel and measures scripted bots; the
/// default <c>anyone</c> lets every bot fight every bot with its net, which is
/// the upper bound a room full of people approaches. The "held the wheel" line
/// says which one ran. <c>BENCH_ROOMS_MAP</c> one map, else
/// each room opens on its own shuffled rotation map, as a rotation room does.
/// </para>
/// <para>
/// <b>No clients are connected</b> (<c>MinHumans</c> 0 fills the rooms), so serialisation and sends are not in it,
/// and the empty-room exit is switched off so the rooms outlive the run.
/// Warm-up (the map loads and the first <c>BENCH_ROOMS_WARM</c> ticks, default
/// 300) is excluded; <c>BENCH_ROOMS_TICKS</c> (default 1800, one minute of
/// game time) are timed.
/// </para>
/// </remarks>
public static class RoomsBench
{
    public static void Run(int rooms)
    {
        var mode = Environment.GetEnvironmentVariable("BENCH_ROOMS_MODE") ?? "policy";
        var bots = Environment.GetEnvironmentVariable("BENCH_ROOMS_BOTS") ?? "normal";
        var team = Environment.GetEnvironmentVariable("BENCH_ROOMS_TEAM") ?? "medium";
        var map = Environment.GetEnvironmentVariable("BENCH_ROOMS_MAP");
        var engage = Environment.GetEnvironmentVariable("BENCH_ROOMS_ENGAGE") ?? "anyone";
        var warm = int.Parse(Environment.GetEnvironmentVariable("BENCH_ROOMS_WARM") ?? "300");
        var ticks = int.Parse(Environment.GetEnvironmentVariable("BENCH_ROOMS_TICKS") ?? "1800");

        // A hybrid CPU (the 13700K's P and E cores) moves this number by 2x
        // between runs; BENCH_AFFINITY (a hex mask) holds the process to chosen cores.
        if (Environment.GetEnvironmentVariable("BENCH_AFFINITY") is { Length: > 0 } mask)
            Process.GetCurrentProcess().ProcessorAffinity = (nint)Convert.ToInt64(mask, 16);

        var log = new QuietLog();
        using var host = new RoomHost(new RoomHostOptions { Port = 0, MaxRooms = Math.Max(rooms, 1) }, log);
        host.Start();

        var heapBefore = GC.GetTotalMemory(forceFullCollection: true);
        var build = Stopwatch.StartNew();

        for (var i = 0; i < rooms; i++)
        {
            var options = ServerOptions.CreateDefault();
            options.BotMode = mode switch
            {
                "policy" => BotMode.Policy,
                "scripted" => BotMode.Scripted,
                _ => throw new ArgumentException($"BENCH_ROOMS_MODE '{mode}': scripted or policy"),
            };

            if (options.BotMode == BotMode.Policy)
            {
                options.BotHandoverRule = HandoverRule.Distance;
                options.BotEngagesBotsToo = engage == "anyone";
            }

            RoomTemplate.Apply(options, new RoomChoices(Map: map ?? "rotation", Bots: bots, TeamSize: team));
            options.ExitWhenEmptySeconds = 0;

            // A room fills only once somebody is in it (BotPopulation.Target);
            // with nobody connected, MinHumans 0 stands in for the creator, so a
            // room runs its whole team of bots rather than one human's worth fewer.
            options.Bots = options.Bots with { MinHumans = 0 };

            if (host.CreateRoom(options) is null)
                throw new InvalidOperationException("host refused a room");
        }

        var buildMs = build.Elapsed.TotalMilliseconds;

        for (var i = 0; i < warm; i++)
            host.Tick();

        var heapAfter = GC.GetTotalMemory(forceFullCollection: true);
        var botCount = 0;

        foreach (var room in host.Rooms)
            botCount += room.Server.PlayerCount - room.Server.HumanCount - 1; // less the server's own slot 0

        var (microTicksBefore, decisionsBefore) = PolicyWork(host);
        var samples = new double[ticks];
        var gcBefore = GC.CollectionCount(0);
        var allocBefore = GC.GetTotalAllocatedBytes(precise: true);
        var cpu = Process.GetCurrentProcess().TotalProcessorTime;
        var wall = Stopwatch.StartNew();

        for (var i = 0; i < ticks; i++)
        {
            var start = Stopwatch.GetTimestamp();
            host.Tick();
            samples[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }

        var wallMs = wall.Elapsed.TotalMilliseconds;
        var (microTicksAfter, decisionsAfter) = PolicyWork(host);
        var cpuMs = (Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalMilliseconds;
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocBefore;
        var gen0 = GC.CollectionCount(0) - gcBefore;

        Array.Sort(samples);
        double P(double q) => samples[Math.Min(samples.Length - 1, (int)(q * samples.Length))];
        var mean = samples.Average();

        var maps = host.Rooms.Select(r => r.Server.CurrentMap).Distinct().Count();

        Console.WriteLine($"rooms={rooms} mode={mode}{(mode == "policy" ? "/" + engage : "")} bots={bots} team={team} map={map ?? "rotation"} ({maps} distinct)  live bots={botCount} live rooms={host.Rooms.Count}");
        Console.WriteLine($"  build {buildMs:0} ms; heap {heapBefore / 1048576.0:0.0} -> {heapAfter / 1048576.0:0.0} MB "
                          + $"({(rooms > 0 ? (heapAfter - heapBefore) / 1048576.0 / rooms : 0):0.00} MB/room)");
        Console.WriteLine($"  host tick ms over {ticks}: mean {mean:0.000}  p50 {P(0.5):0.000}  p99 {P(0.99):0.000}  max {samples[^1]:0.000}"
                          + $"  per room {(rooms > 0 ? mean / rooms : 0):0.000}  per bot {(botCount > 0 ? mean / botCount * 1000 : 0):0.0} us");
        Console.WriteLine($"  policy held the wheel {(botCount > 0 ? (microTicksAfter - microTicksBefore) / (double)(botCount * ticks) : 0):P1} of bot-ticks; "
                          + $"{(decisionsAfter - decisionsBefore) / (double)ticks:0.00} policy decisions a host tick");
        Console.WriteLine($"  cpu/wall {cpuMs / wallMs:0.00}; allocated {allocated / (double)ticks / 1024:0.0} KB/tick; gen0 GCs {gen0}");
    }

    /// <summary>Ticks a policy drove and decisions it took, over every bot in every room.</summary>
    private static (long Ticks, long Decisions) PolicyWork(RoomHost host)
    {
        long ticks = 0, decisions = 0;

        foreach (var room in host.Rooms)
        {
            for (var slot = 1; slot < room.Server.RosterCount; slot++)
            {
                if (HybridBot.Of(room.Server.SourceOf(slot)) is { } hybrid)
                {
                    ticks += hybrid.MicroTicks;
                    decisions += hybrid.MicroDecisions;
                }
            }
        }

        return (ticks, decisions);
    }

    private sealed class QuietLog : IServerLog
    {
        public void Info(string message) { }

        public void Warn(string message) => Console.Error.WriteLine("warn: " + message);

        public void Error(string message, Exception? error = null) => Console.Error.WriteLine("error: " + message + " " + error);
    }
}
