using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;

namespace GG2.Server;

/// <summary>
/// What the server is running on: the OS, the CPU, the memory, the runtime.
/// </summary>
/// <remarks>
/// <para>
/// The static counterpart to <see cref="PerfHistory"/> — that one is how the
/// server is doing, this is what it is doing it on. An operator reading a tick
/// graph on a rented box needs both, and the questions it answers ("how many
/// cores did the VPS actually give me", "is this a Debug build", "is the memory
/// figure the host's or my container's") are exactly the ones that make a
/// performance number mean something different.
/// </para>
/// <para>
/// <b>THE COMMAND LINE AND THE ENVIRONMENT ARE NEVER REPORTED.</b>
/// <c>--web-password</c> is in one and may be in the other, and this endpoint
/// exists to be read by a browser. Nothing here reads
/// <see cref="Environment.GetCommandLineArgs"/> or
/// <see cref="Environment.GetEnvironmentVariables()"/>; the paths below are named
/// individually, on purpose.
/// </para>
/// <para>
/// <b>Runs on an HTTP thread, not the tick thread</b>, and may because it
/// touches no game state — only the OS, the runtime, and the option strings that
/// are set once at startup and never written again. It is the one route that
/// does not go through the command queue.
/// </para>
/// <para>
/// <b>Every platform lookup degrades to null rather than throwing.</b> A panel
/// that 500s because <c>/proc</c> looked different on somebody's distro is worse
/// than one that says "unknown" for the CPU model.
/// </para>
/// </remarks>
public static class SystemInfo
{
    /// <summary>
    /// The CPU's model and clock, or nulls where the platform will not say.
    /// </summary>
    /// <remarks>
    /// <b>The two platforms report different things and the panel says which.</b>
    /// Windows' registry value is the NOMINAL clock recorded at boot; Linux's
    /// <c>/proc/cpuinfo</c> is the CURRENT clock of core 0, which on any modern
    /// machine is whatever the governor is doing this instant and will not match
    /// the number on the box.
    /// </remarks>
    private static readonly (string? Model, int? Megahertz, bool Nominal) Cpu = ReadCpu();

    /// <remarks>
    /// <b>This is how <c>GG2.Server</c> was compiled, which is the one that
    /// matters</b>: the simulation and the bot layer live here. CLAUDE.md's
    /// standing rule is that a tick figure without its build is meaningless —
    /// the same twelve-bot tick is 0.151ms in Release and 0.307ms in Debug — so
    /// the panel that shows tick graphs says which build produced them.
    /// </remarks>
    private const string Build =
#if DEBUG
        "Debug";
#else
        "Release";
#endif

    public static string ToJson(ServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var json = new StringBuilder(1536);
        var process = Process.GetCurrentProcess();
        var memory = ReadMemory();

        json.Append("{\"ok\":true");

        json.Append(",\"os\":{\"description\":\"")
            .Append(ControlCommands.Escape(RuntimeInformation.OSDescription)).Append('"');
        json.Append(",\"architecture\":\"").Append(RuntimeInformation.OSArchitecture).Append('"');
        json.Append(",\"machine\":\"").Append(ControlCommands.Escape(Environment.MachineName)).Append('"');
        json.Append(",\"uptimeSeconds\":").Append(Number(OsUptimeSeconds()));
        json.Append('}');

        json.Append(",\"cpu\":{\"model\":").Append(Text(Cpu.Model));
        json.Append(",\"megahertz\":").Append(Cpu.Megahertz?.ToString(CultureInfo.InvariantCulture) ?? "null");

        // Said out loud rather than left for the reader to assume: Windows
        // reports the clock recorded at boot and Linux reports what core 0 is
        // doing right now.
        json.Append(",\"clockIsNominal\":").Append(Cpu.Nominal ? "true" : "false");

        // LOGICAL cores, and container-aware: on a VPS with a CPU quota this
        // is what the runtime will actually schedule on, which is the number
        // that explains the tick cost. It is not the host's core count.
        json.Append(",\"logicalCores\":").Append(Environment.ProcessorCount).Append('}');

        json.Append(",\"memory\":{\"totalBytes\":").Append(Null(memory.TotalBytes));
        json.Append(",\"availableBytes\":").Append(Null(memory.AvailableBytes));
        json.Append(",\"source\":").Append(Text(memory.Source));
        json.Append(",\"processBytes\":").Append(Environment.WorkingSet);
        json.Append(",\"managedBytes\":").Append(GC.GetTotalMemory(forceFullCollection: false)).Append('}');

        json.Append(",\"runtime\":{\"framework\":\"")
            .Append(ControlCommands.Escape(RuntimeInformation.FrameworkDescription)).Append('"');
        json.Append(",\"identifier\":\"")
            .Append(ControlCommands.Escape(RuntimeInformation.RuntimeIdentifier)).Append('"');
        json.Append(",\"build\":\"").Append(Build).Append('"');
        json.Append(",\"serverGc\":").Append(GCSettings.IsServerGC ? "true" : "false");
        json.Append(",\"concurrentGc\":").Append(ConcurrentGc() ? "true" : "false");
        json.Append(",\"collections\":[").Append(GC.CollectionCount(0)).Append(',')
            .Append(GC.CollectionCount(1)).Append(',').Append(GC.CollectionCount(2)).Append(']');
        json.Append(",\"threads\":").Append(Null(ThreadCount(process)));
        json.Append(",\"processId\":").Append(Environment.ProcessId);
        json.Append(",\"processUptimeSeconds\":").Append(Number(ProcessUptimeSeconds(process)));
        json.Append(",\"timerResolution\":").Append(Text(TimerResolutionNote())).Append('}');

        // Where it is loading things from. An operator on a box they did not set
        // up asks this more often than anything else here.
        json.Append(",\"paths\":{\"maps\":").Append(Text(options.MapDirectory));
        json.Append(",\"nav\":").Append(Text(NullIfEmpty(options.NavCacheDirectory)));
        json.Append(",\"recordings\":").Append(Text(NullIfEmpty(options.RecordingDirectory))).Append('}');

        AppendDisk(json, options.MapDirectory);

        json.Append('}');
        return json.ToString();
    }

    // --- the CPU -------------------------------------------------------------

    private static (string? Model, int? Megahertz, bool Nominal) ReadCpu()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return ReadCpuWindows();

            if (OperatingSystem.IsLinux())
                return ReadCpuLinux();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
        {
            // Fall through to the environment variable, or to nulls.
        }

        return (Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"), null, false);
    }

    /// <remarks>
    /// <c>Microsoft.Win32.Registry</c> needs no package on <c>net10.0</c>,
    /// but it is Windows-only, so the call sits behind
    /// <see cref="OperatingSystem.IsWindows"/>, which is what the platform
    /// analyser reads. Without that guard a <c>linux-x64</c> publish fails the
    /// build, which is how this was verified.
    /// </remarks>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static (string? Model, int? Megahertz, bool Nominal) ReadCpuWindows()
    {
        const string Key = @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0";

        var model = Microsoft.Win32.Registry.GetValue(Key, "ProcessorNameString", null) as string;
        var megahertz = Microsoft.Win32.Registry.GetValue(Key, "~MHz", null) as int?;

        // Nominal: the registry records the clock as it was at boot, not now.
        return (model?.Trim(), megahertz, true);
    }

    private static (string? Model, int? Megahertz, bool Nominal) ReadCpuLinux()
    {
        string? model = null;
        double? megahertz = null;

        foreach (var line in File.ReadLines("/proc/cpuinfo"))
        {
            if (model is null && line.StartsWith("model name", StringComparison.Ordinal))
                model = After(line);

            if (megahertz is null
                && line.StartsWith("cpu MHz", StringComparison.Ordinal)
                && double.TryParse(After(line), CultureInfo.InvariantCulture, out var value))
            {
                megahertz = value;
            }

            // Only core 0: the file repeats per core and they are the same chip.
            if (model is not null && megahertz is not null)
                break;
        }

        // NOT nominal — this is whatever the governor has core 0 at right now,
        // and on a shared VPS it moves between requests.
        return (model, megahertz is null ? null : (int)Math.Round(megahertz.Value), false);
    }

    private static string? After(string line)
    {
        var colon = line.IndexOf(':', StringComparison.Ordinal);
        return colon < 0 ? null : line[(colon + 1)..].Trim();
    }

    // --- the memory ----------------------------------------------------------

    /// <summary>
    /// Total and available physical memory, and where the numbers came from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>GC.GetGCMemoryInfo()</c> is NOT used for this.</b> Its fields
    /// describe <em>the last garbage collection</em>, not the present: on a
    /// freshly started server it reports <c>MemoryLoadBytes = 0</c> even with
    /// plenty of physical memory free. A panel showing "0 bytes in use" would
    /// be worse than showing nothing.
    /// </para>
    /// <para>
    /// <b>The cgroup is preferred over <c>/proc/meminfo</c> where one exists</b>,
    /// because that is the whole point on a VPS: a container sees the host's
    /// <c>/proc/meminfo</c> and is killed at its own limit. The panel says which
    /// of the two it read.
    /// </para>
    /// </remarks>
    private static (long? TotalBytes, long? AvailableBytes, string? Source) ReadMemory()
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                if (ReadCgroupMemory() is { } cgroup)
                    return cgroup;

                return ReadProcMeminfo();
            }

            if (OperatingSystem.IsWindows())
                return ReadMemoryWindows();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
        {
            // Reported as unknown rather than as a failed request.
        }

        return (null, null, null);
    }

    private static (long?, long?, string?)? ReadCgroupMemory()
    {
        // cgroup v2, which is what any current distro gives a container.
        const string Limit = "/sys/fs/cgroup/memory.max";
        const string Current = "/sys/fs/cgroup/memory.current";

        if (!File.Exists(Limit) || !File.Exists(Current))
            return null;

        var limit = File.ReadAllText(Limit).Trim();

        // "max" means no limit — the container can have the whole host, so the
        // host's own figures are the honest ones.
        if (limit == "max" || !long.TryParse(limit, out var total))
            return null;

        return long.TryParse(File.ReadAllText(Current).Trim(), out var used)
            ? (total, total - used, "cgroup v2")
            : (total, null, "cgroup v2");
    }

    private static (long?, long?, string?) ReadProcMeminfo()
    {
        long? total = null;
        long? available = null;

        foreach (var line in File.ReadLines("/proc/meminfo"))
        {
            // Every value in this file is in kB.
            if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
                total = Kilobytes(line);
            else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
                available = Kilobytes(line);

            if (total is not null && available is not null)
                break;
        }

        return (total, available, "/proc/meminfo");
    }

    private static long? Kilobytes(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && long.TryParse(parts[1], out var value) ? value * 1024 : null;
    }

    /// <remarks>
    /// <b>A P/Invoke, because the BCL has no cross-platform answer</b> — the
    /// same shape <see cref="TimerResolution"/> already uses for
    /// <c>winmm.dll</c>. Guarded, so a <c>linux-x64</c> publish still builds.
    /// </remarks>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static (long?, long?, string?) ReadMemoryWindows()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };

        return GlobalMemoryStatusEx(ref status)
            ? ((long?)status.TotalPhys, (long?)status.AvailPhys, "GlobalMemoryStatusEx")
            : (null, null, null);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    // --- the rest ------------------------------------------------------------

    /// <remarks>
    /// <b><see cref="Environment.TickCount64"/> is NOT the OS uptime on every
    /// platform</b> — it is a monotonic clock, which on Linux is
    /// <c>CLOCK_MONOTONIC</c> and does not count time suspended. Linux has the
    /// real answer in <c>/proc/uptime</c>, so that is read where it exists and
    /// the tick count is the fallback.
    /// </remarks>
    private static double OsUptimeSeconds()
    {
        try
        {
            if (OperatingSystem.IsLinux() && File.Exists("/proc/uptime"))
            {
                var first = File.ReadAllText("/proc/uptime").Split(' ')[0];

                if (double.TryParse(first, CultureInfo.InvariantCulture, out var seconds))
                    return Math.Round(seconds);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The tick count below is a fine approximation.
        }

        return Math.Round(Environment.TickCount64 / 1000.0);
    }

    private static double ProcessUptimeSeconds(Process process)
    {
        try
        {
            return Math.Round((DateTime.UtcNow - process.StartTime.ToUniversalTime()).TotalSeconds);
        }
        catch (Exception e) when (e is InvalidOperationException or PlatformNotSupportedException)
        {
            return 0;
        }
    }

    private static int? ThreadCount(Process process)
    {
        try
        {
            process.Refresh();
            return process.Threads.Count;
        }
        catch (Exception e) when (e is InvalidOperationException or PlatformNotSupportedException or IOException)
        {
            return null;
        }
    }

    private static bool ConcurrentGc() =>
        !AppContext.TryGetSwitch("System.GC.Concurrent", out var concurrent) || concurrent;

    /// <summary>
    /// What the tick loop's pacing is resting on.
    /// </summary>
    /// <remarks>
    /// Worth a line on the panel because it explains a whole class of late
    /// ticks. On Windows the loop raises the timer to 1 ms
    /// (<see cref="TimerResolution"/>) because a wait would otherwise round up to
    /// the 15.6 ms quantum; on Linux there is nothing to raise and nothing to
    /// warn about.
    /// </remarks>
    private static string TimerResolutionNote() => OperatingSystem.IsWindows()
        ? "raised to 1ms while running (winmm timeBeginPeriod)"
        : "not applicable on this platform";

    private static void AppendDisk(StringBuilder json, string path)
    {
        json.Append(",\"disk\":");

        try
        {
            // The LONGEST matching mount point, not the first. On Linux every
            // path matches "/", so picking the first drive would report the root
            // filesystem for a maps directory on its own mount.
            var full = Path.GetFullPath(path);
            DriveInfo? best = null;

            foreach (var drive in DriveInfo.GetDrives())
            {
                var root = drive.RootDirectory.FullName;

                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (best is null || root.Length > best.RootDirectory.FullName.Length)
                    best = drive;
            }

            if (best is null || !best.IsReady)
            {
                json.Append("null");
                return;
            }

            json.Append("{\"mount\":").Append(Text(best.RootDirectory.FullName));
            json.Append(",\"freeBytes\":").Append(best.AvailableFreeSpace);
            json.Append(",\"totalBytes\":").Append(best.TotalSize).Append('}');
        }
        catch (Exception e) when (e is IOException
                                      or UnauthorizedAccessException
                                      or ArgumentException
                                      or SecurityException)
        {
            json.Append("null");
        }
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    private static string Text(string? value) =>
        value is null ? "null" : "\"" + ControlCommands.Escape(value) + "\"";

    private static string Null(long? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? "null";

    private static string Null(int? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? "null";

    private static string Number(double value) =>
        value.ToString("R", CultureInfo.InvariantCulture);
}
