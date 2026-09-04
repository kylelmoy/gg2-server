using System.Globalization;
using Gg2.Protocol;

namespace Gg2.Server;

/// <summary>
/// What the server was doing on a tick that ran late, for the warning line.
/// </summary>
/// <param name="Clients">Connected network clients.</param>
/// <param name="Bots">Bots on the roster.</param>
/// <param name="Characters">Live characters — the cost driver for the search controller.</param>
/// <param name="Map">The map being served.</param>
/// <param name="WorkMilliseconds">
/// How long this tick's own work took — <c>Tick()</c>, measured directly.
/// <b>THE FIRST NUMBER TO READ, and the reason is that everything else on this
/// line is pacing.</b> A tick is late when the loop reaches the next deadline
/// after it has passed, which happens either because the tick was slow or
/// because the process did not get scheduled. Those need opposite responses and
/// look identical without this.
/// <para>
/// A twelve-bot server can show <b>0.104 ms of CPU per tick</b> — 0.3% of one
/// core — while still showing p99 100 ms and a 262 ms worst case, when a
/// neighbouring process is competing for the same box. Work near zero with
/// lateness in the tens or hundreds of milliseconds is that signature, and no
/// amount of optimising this server moves it.
/// </para>
/// </param>
/// <param name="ArbiterSpentMicroseconds">
/// Controller microseconds the arbiter granted on this tick, or null when there
/// is no arbiter. <b>The single most useful number here when it exists</b>: a
/// late tick on a <c>--bot-mode tiered</c> server is usually the search tier, and
/// this is the difference between "the bots did it" and "something else did".
/// </param>
public readonly record struct LateTickContext(
    int Clients,
    int Bots,
    int Characters,
    string Map,
    double WorkMilliseconds,
    int? ArbiterSpentMicroseconds);

/// <summary>
/// Warns when a tick overruns its interval, without turning a slow server into a
/// log flood.
/// </summary>
/// <remarks>
/// <para>
/// <b>This measures PACING, never simulation.</b> A tick computes the same
/// thing whenever it runs — <c>MatchSimulation.Tick</c> takes no clock — so a
/// late tick cannot change what happened, only when the clients heard about it.
/// It is a delivery problem, and the symptom is the stock client dead-reckoning
/// somewhere the next <c>QUICK_UPDATE</c> visibly corrects.
/// </para>
/// <para>
/// <b>Rate-limited, and the shape of the limit is the design.</b> A server
/// that is 2 ms late on every tick would print thirty lines a second, which is
/// not a warning but a denial of service on the operator's terminal. It is also
/// not enough to simply drop the extras: "one late tick" and "nine hundred late
/// ticks" are completely different situations and must not print the same line.
/// So the first late tick in a window prints immediately — you see it when it
/// happens — and the rest are counted and summarised when the window closes.
/// </para>
/// <para>
/// <b>No clock, on purpose.</b> Windows are counted in ticks and lateness is
/// handed in already measured, so this is testable without sleeping and without
/// a flaky wall-clock assertion. The same reason <c>MatchSimulation</c> takes no
/// clock, for a much less important component.
/// </para>
/// </remarks>
public sealed class LateTickMonitor
{
    /// <summary>
    /// How far past its interval a tick must run before it counts as late:
    /// <b>20%</b>.
    /// </summary>
    /// <remarks>
    /// <b>A fraction of the interval, not a fixed millisecond count</b>,
    /// because <c>--speed</c> changes the interval. At the default 30 Hz this is
    /// 33.33 + 6.67 = <b>40 ms</b>, which is exactly the threshold
    /// <see cref="TickPacing"/> reports its "over 40ms" count at, and it is
    /// chosen for the same measured reason: one and a quarter ticks is where the
    /// stock client has dead-reckoned far enough that the correction is visible.
    /// <para>
    /// A fixed 40 ms would mean a <c>--speed 4</c> server (8.3 ms interval)
    /// never warned however far behind it fell, which is the configuration most
    /// likely to be asked for more than the machine can give.
    /// </para>
    /// </remarks>
    public const double LateFraction = 0.2;

    /// <summary>
    /// How long the coalescing window is, in ticks — <b>30 seconds</b> of play.
    /// </summary>
    /// <remarks>
    /// The same cadence <c>--tick-stats</c> reports at, so the two interleave
    /// legibly rather than at unrelated periods.
    /// </remarks>
    public const int WindowTicks = GameConstants.TickRate * 30;

    private readonly int _windowTicks;

    private bool _open;
    private long _windowEndsAt;
    private long _suppressed;
    private double _worstOverrunMs;
    private long _worstTick;

    public LateTickMonitor(int windowTicks = WindowTicks)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windowTicks);

        _windowTicks = windowTicks;
    }

    /// <summary>Late ticks seen since construction, including suppressed ones.</summary>
    public long LateTicks { get; private set; }

    /// <summary>
    /// Whether <paramref name="overrunMs"/> counts as late for an interval of
    /// <paramref name="intervalMs"/>.
    /// </summary>
    /// <remarks>
    /// <b>An unpaced server has no interval and can never be late.</b>
    /// <c>--speed max</c> never waits, so every tick "overruns" by construction
    /// and a monitor that did not say so here would warn continuously on exactly
    /// the configuration that wants no output. <c>GameServer.Run</c> also returns
    /// to the top before reaching this, so it is guarded twice — deliberately,
    /// because the loop's ordering is load-bearing for other reasons and this
    /// must not silently depend on it.
    /// </remarks>
    public static bool IsLate(double overrunMs, double intervalMs) =>
        intervalMs > 0 && overrunMs > intervalMs * LateFraction;

    /// <summary>
    /// Records a late tick. Returns the line to log, or null when this one is
    /// being coalesced into the open window.
    /// </summary>
    public string? Record(long tick, double overrunMs, double intervalMs, in LateTickContext context)
    {
        LateTicks++;

        if (_open)
        {
            _suppressed++;

            if (overrunMs > _worstOverrunMs)
            {
                _worstOverrunMs = overrunMs;
                _worstTick = tick;
            }

            return null;
        }

        _open = true;
        _windowEndsAt = tick + _windowTicks;
        _suppressed = 0;
        _worstOverrunMs = overrunMs;
        _worstTick = tick;

        return $"WARNING late tick {tick}: took {intervalMs + overrunMs:0.0}ms against a "
            + $"{intervalMs:0.0}ms budget ({overrunMs:0.0}ms over) — {Describe(context, intervalMs)}";
    }

    /// <summary>
    /// Called every tick. Returns a summary when a window closes on more than
    /// the one tick already reported, otherwise null.
    /// </summary>
    public string? Poll(long tick)
    {
        if (!_open || tick < _windowEndsAt)
            return null;

        _open = false;

        if (_suppressed == 0)
            return null;

        // Reports the WORST, not the last. A window's worst tick is the one
        // worth chasing, and the last is an accident of where the window fell.
        return $"WARNING {_suppressed} more late tick(s) in the last {_windowTicks} ticks; "
            + $"worst was tick {_worstTick} at {_worstOverrunMs:0.0}ms over";
    }

    /// <summary>
    /// Any pending summary, for shutdown — so a server that stops mid-window
    /// still reports what it saw.
    /// </summary>
    public string? Flush()
    {
        if (!_open)
            return null;

        _open = false;

        if (_suppressed == 0)
            return null;

        return $"WARNING {_suppressed} more late tick(s) before shutdown; "
            + $"worst was tick {_worstTick} at {_worstOverrunMs:0.0}ms over";
    }

    private static string Describe(in LateTickContext context, double intervalMs)
    {
        var arbiter = context.ArbiterSpentMicroseconds is { } spent
            ? $", controllers {spent.ToString(CultureInfo.InvariantCulture)}us"
            : string.Empty;

        // The verdict, not decoration. A late tick whose own work was a
        // fraction of a millisecond was not this server's fault, and saying so on
        // the line is the difference between an operator profiling the bot layer
        // for an afternoon and an operator looking at what else is running.
        var work = context.WorkMilliseconds.ToString("0.00", CultureInfo.InvariantCulture);

        // The same threshold lateness itself uses, so the two answers cannot
        // disagree: work that would have blown the budget on its own is the
        // tick's fault, and work far under it is not.
        var verdict = IsLate(context.WorkMilliseconds - intervalMs, intervalMs)
            ? "the tick itself"
            : "NOT this tick — the process was not scheduled";

        return $"{context.Map}, {context.Clients} client(s), {context.Bots} bot(s), "
            + $"{context.Characters} character(s){arbiter}; work {work}ms, so: {verdict}";
    }
}
