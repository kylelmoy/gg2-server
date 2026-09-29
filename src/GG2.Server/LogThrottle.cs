namespace GG2.Server;

/// <summary>
/// A token bucket over one kind of log line, counting what it held back.
/// </summary>
/// <remarks>
/// <para>
/// <b>The log is on the tick thread and writing to it is synchronous.</b>
/// <c>Console.WriteLine</c> takes a lock and, when the output is a pipe or a
/// file, can block; the panel's ring takes a lock of its own. That is fine for
/// events the server causes and wrong for events a <em>client</em> causes,
/// because those have no natural rate. One line per refused connection during a
/// connection flood turns the defence into the outage: the server spends its
/// tick budget writing about what it refused instead of simulating for the
/// players who got in.
/// </para>
/// <para>
/// <b>A bucket rather than a plain interval, because the normal case is
/// bursty.</b> Six players joining together is six lines an operator wants,
/// and a one-line-per-interval throttle would report it as one line plus "5
/// suppressed" — hiding ordinary activity to defend against a flood that is not
/// happening. A burst allowance passes the real case untouched and still puts a
/// ceiling on the pathological one.
/// </para>
/// <para>
/// ⚠️ <b>Suppressed lines are counted and reported, never silently dropped.</b>
/// A log that quietly omits the evidence during exactly the incident an operator
/// is investigating is worse than a noisy one — it makes a flood look like a
/// handful of connections. The count goes out with the next line that is
/// allowed through, so the shape of the burst survives.
/// </para>
/// <para>
/// Not thread-safe, and does not need to be: every user is on the tick thread.
/// The clock is a parameter rather than something this reads, so it stays
/// testable without sleeping — the same reason <c>ServerHarness</c> ticks
/// explicitly rather than waiting.
/// </para>
/// </remarks>
/// <param name="burst">Lines allowed before the bucket is empty. At least one.</param>
/// <param name="per">How long refilling the whole bucket takes.</param>
public sealed class LogThrottle(int burst, TimeSpan per)
{
    private readonly int _burst = burst >= 1
        ? burst
        : throw new ArgumentOutOfRangeException(nameof(burst), burst, "must be at least one");

    private readonly double _refillPerMs = burst / Math.Max(1.0, per.TotalMilliseconds);

    private double _tokens = burst;
    private long _lastMs;
    private bool _started;
    private int _suppressed;

    /// <summary>Lines held back since the last one that was allowed through.</summary>
    public int Suppressed => _suppressed;

    /// <summary>
    /// Whether to write this line now.
    /// </summary>
    /// <param name="nowMs">
    /// A monotonic millisecond clock — <c>Environment.TickCount64</c>, which the
    /// server already samples once per tick as <c>_nowMs</c>. Monotonic on
    /// purpose: a wall clock that steps backwards over a DST change or an NTP
    /// correction would mute this for the length of the step.
    /// </param>
    /// <param name="suppressed">
    /// How many were held back since the last allowed line. Zero unless this
    /// call returns true, and reset by it.
    /// </param>
    public bool ShouldLog(long nowMs, out int suppressed)
    {
        // The first call fixes the origin. Without it a server whose
        // TickCount64 already reads days would refill the bucket from zero on
        // its first line, which is harmless but makes the first burst untestable.
        if (!_started)
        {
            _started = true;
            _lastMs = nowMs;
        }

        // Clamped at zero: a caller passing a clock that went backwards must not
        // be able to drain the bucket by doing so.
        var elapsed = Math.Max(0, nowMs - _lastMs);
        _lastMs = nowMs;
        _tokens = Math.Min(_burst, _tokens + (elapsed * _refillPerMs));

        if (_tokens < 1)
        {
            _suppressed++;
            suppressed = 0;
            return false;
        }

        _tokens -= 1;
        suppressed = _suppressed;
        _suppressed = 0;
        return true;
    }
}
