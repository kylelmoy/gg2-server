using System.Net;

namespace GG2.Server;

/// <summary>
/// How often one address may open a connection.
/// </summary>
/// <remarks>
/// <para>
/// <b>Different from <c>ServerOptions.MultiClientLimit</c>, which is the thing
/// people confuse it with.</b> That bounds how many connections one address
/// holds <em>at once</em> — the reference's answer to a household behind one
/// NAT. This bounds how <em>fast</em> it opens them, which is the shape nothing
/// else catches: connect, get refused or dropped, reconnect, repeat. Each cycle
/// costs an accept, an allocation, a roster walk and a log line, and the
/// concurrent count never goes above one.
/// </para>
/// <para>
/// ⚠️ <b>It bounds the table as well as the rate, and the two interact.</b> A
/// per-address dictionary that grows on demand is the same unbounded-growth bug
/// this whole exercise is about, one layer up — an attacker with many addresses
/// would fill it. So the table has a ceiling, and past that it <b>fails
/// open</b>: a new address is allowed rather than refused. That is deliberate
/// and it is the honest trade. Failing closed would mean an attacker with
/// enough addresses could lock out every legitimate player by filling the
/// table, which turns a rate limiter into the denial of service. What a
/// distributed flood runs into instead is
/// <c>ConnectionLimits.MaxAcceptsPerTick</c> and
/// <c>MaxJoiningConnections</c>, which bound the damage without needing to know
/// who is doing it — and, past that, the firewall, which is where a distributed
/// attack has to be answered. <c>docs/HARDENING.md</c>.
/// </para>
/// <para>
/// <b>Sweeping is what keeps the table small in practice.</b> A bucket that has
/// refilled completely carries no information — it is indistinguishable from an
/// address never seen — so it is dropped. Only addresses currently being
/// limited stay, which on a healthy server is none.
/// </para>
/// <para>
/// Tick-thread only, like <see cref="BanList"/>. No lock.
/// </para>
/// </remarks>
public sealed class ConnectionRateLimiter
{
    /// <summary>
    /// Addresses tracked at once before new ones are waved through.
    /// </summary>
    /// <remarks>
    /// Each entry is an address and two numbers, so this is tens of kilobytes.
    /// It is sized to be far above the number of distinct addresses a real
    /// server sees in a sweep interval and far below anything that matters for
    /// memory.
    /// </remarks>
    public const int MaxTracked = 4096;

    private readonly Dictionary<IPAddress, Bucket> _buckets = [];
    private readonly int _burst;
    private readonly double _refillPerMs;

    private long _lastSweepMs;
    private bool _warnedFull;

    private struct Bucket
    {
        public double Tokens;
        public long LastMs;
    }

    /// <param name="burst">Connections allowed back to back from one address.</param>
    /// <param name="per">How long refilling the whole allowance takes.</param>
    public ConnectionRateLimiter(int burst, TimeSpan per)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(burst, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(per, TimeSpan.Zero);

        _burst = burst;
        _refillPerMs = burst / per.TotalMilliseconds;
    }

    /// <summary>Addresses currently held. For the panel and for tests.</summary>
    public int Tracked => _buckets.Count;

    /// <summary>
    /// Whether this address may open a connection now.
    /// </summary>
    /// <param name="address">The peer.</param>
    /// <param name="nowMs">
    /// A monotonic millisecond clock — <c>Environment.TickCount64</c>. Monotonic
    /// for the reason <see cref="LogThrottle"/> gives: a wall clock stepping
    /// backwards over an NTP correction would refill nothing for the length of
    /// the step, turning this into a hard block.
    /// </param>
    public bool Allow(IPAddress address, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(address);

        Sweep(nowMs);

        if (!_buckets.TryGetValue(address, out var bucket))
        {
            if (_buckets.Count >= MaxTracked)
                return true;

            bucket = new Bucket { Tokens = _burst, LastMs = nowMs };
        }

        // Clamped at zero so a clock that went backwards cannot drain a bucket.
        var elapsed = Math.Max(0, nowMs - bucket.LastMs);

        bucket.Tokens = Math.Min(_burst, bucket.Tokens + (elapsed * _refillPerMs));
        bucket.LastMs = nowMs;

        var allowed = bucket.Tokens >= 1;

        if (allowed)
            bucket.Tokens -= 1;

        _buckets[address] = bucket;
        return allowed;
    }

    /// <summary>Whether the table is full and new addresses are being waved through.</summary>
    /// <remarks>
    /// Exposed so an operator can be told, once. A limiter that has quietly
    /// stopped limiting is worse than one that was never on, because the log
    /// says it is on.
    /// </remarks>
    public bool IsSaturated => _buckets.Count >= MaxTracked;

    /// <summary>
    /// Whether to warn about saturation. True at most once per saturation
    /// episode.
    /// </summary>
    public bool ShouldWarnSaturated()
    {
        if (!IsSaturated || _warnedFull)
            return false;

        _warnedFull = true;
        return true;
    }

    /// <summary>
    /// Drops buckets that have refilled, which carry no information.
    /// </summary>
    /// <remarks>
    /// Once a second at most: it walks the table, and doing that on every accept
    /// would make a connection flood quadratic — the exact shape the accept cap
    /// exists to avoid.
    /// </remarks>
    private void Sweep(long nowMs)
    {
        if (nowMs - _lastSweepMs < 1000)
            return;

        _lastSweepMs = nowMs;

        // Collected first: removing from a Dictionary while enumerating it is
        // undefined, and the count here is bounded by MaxTracked.
        List<IPAddress>? stale = null;

        foreach (var (address, bucket) in _buckets)
        {
            var elapsed = Math.Max(0, nowMs - bucket.LastMs);

            if (bucket.Tokens + (elapsed * _refillPerMs) >= _burst)
                (stale ??= []).Add(address);
        }

        if (stale is null)
            return;

        foreach (var address in stale)
            _buckets.Remove(address);

        if (_buckets.Count < MaxTracked)
            _warnedFull = false;
    }
}
