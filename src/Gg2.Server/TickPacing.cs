namespace Gg2.Server;

/// <summary>
/// A histogram of how long each tick actually took, wall-clock, so that "the
/// server hitches" can be answered with a distribution instead of a feeling.
/// </summary>
/// <remarks>
/// ⚠️ <b>The mean and the median both hide this, and that is the whole reason
/// the class exists.</b> Before <see cref="TimerResolution"/> the idle server's
/// median tick was <b>31.04ms</b> — <em>below</em> the 33.33ms target — while
/// 15% of its ticks landed near 47ms, because the pacing loop makes up the time
/// after every overshoot. Averaged, a badly hitching server and a perfect one
/// are the same number. Only the tail says anything.
/// <para>
/// So the report leads with the tail: <c>p99</c>, <c>max</c>, and the count of
/// ticks over 40ms — one and a quarter ticks, past which the stock client has
/// dead-reckoned somewhere the next <c>QUICK_UPDATE</c> will visibly correct.
/// </para>
/// <para>
/// ⚠️ <b>This measures pacing, never simulation.</b> A tick computes the same
/// thing whenever it runs, so nothing here can affect determinism; a slow tick
/// is a delivery problem. It is also not the tick's <em>cost</em> — the interval
/// between tick starts is mostly the deliberate wait — so a healthy server is
/// one whose intervals are boring, not one whose intervals are small.
/// </para>
/// </remarks>
public sealed class TickPacing
{
    /// <summary>1ms buckets, and everything at or over 100ms in the last.</summary>
    private const int Buckets = 101;

    private readonly int[] _histogram = new int[Buckets];
    private readonly double _targetMs;

    private long _count;
    private double _minMs = double.PositiveInfinity;
    private double _maxMs;
    private double _totalMs;

    public TickPacing(double targetMs) => _targetMs = targetMs;

    public long Count => _count;

    /// <summary>Records one interval between tick starts.</summary>
    public void Record(double elapsedMs)
    {
        _count++;
        _totalMs += elapsedMs;

        if (elapsedMs < _minMs)
            _minMs = elapsedMs;

        if (elapsedMs > _maxMs)
            _maxMs = elapsedMs;

        var bucket = (int)elapsedMs;

        _histogram[bucket < 0 ? 0 : bucket >= Buckets ? Buckets - 1 : bucket]++;
    }

    /// <summary>
    /// The value at <paramref name="fraction"/> of the distribution, to the
    /// nearest millisecond below it.
    /// </summary>
    /// <remarks>
    /// Bucket resolution is 1ms, which is coarse for a median and entirely
    /// adequate for the question being asked — whether anything is out past 40.
    /// </remarks>
    private int Percentile(double fraction)
    {
        var target = (long)(_count * fraction);
        long seen = 0;

        for (var i = 0; i < Buckets; i++)
        {
            seen += _histogram[i];

            if (seen >= target)
                return i;
        }

        return Buckets - 1;
    }

    /// <summary>Ticks that took longer than <paramref name="ms"/>.</summary>
    public long Over(double ms)
    {
        long over = 0;

        for (var i = (int)Math.Ceiling(ms); i < Buckets; i++)
            over += _histogram[i];

        return over;
    }

    /// <summary>
    /// One line, tail first. Empty when nothing has been recorded, so a caller
    /// need not special-case a server that has not ticked.
    /// </summary>
    public string Report()
    {
        if (_count == 0)
            return "tick pacing: nothing recorded";

        // The late count is the number a player would recognise; the buckets
        // behind it are there because a bimodal distribution and a merely noisy
        // one have the same percentiles and very different causes.
        return $"tick pacing over {_count} ticks (target {_targetMs:0.0}ms): "
            + $"p99 {Percentile(0.99)}ms max {_maxMs:0.00}ms, "
            + $"{Over(40)} over 40ms, {Over(50)} over 50ms | "
            + $"min {_minMs:0.00} p50 {Percentile(0.50)} mean {_totalMs / _count:0.00} | "
            + Shape();
    }

    /// <summary>The occupied buckets, so a bimodal distribution shows itself.</summary>
    private string Shape()
    {
        var shape = new System.Text.StringBuilder();

        for (var i = 0; i < Buckets; i++)
        {
            if (_histogram[i] == 0)
                continue;

            if (shape.Length > 0)
                shape.Append(' ');

            shape.Append(i == Buckets - 1 ? $"100+:{_histogram[i]}" : $"{i}:{_histogram[i]}");
        }

        return shape.ToString();
    }
}
