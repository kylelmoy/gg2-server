using System.Globalization;
using System.Text;
using GG2.Protocol;

namespace GG2.Parity;

/// <summary>One field of one character disagreeing on one tick.</summary>
public readonly record struct Mismatch
{
    public required int Tick { get; init; }

    public required int Slot { get; init; }

    /// <summary>Which wire field: "x", "y", "hspeed", "vspeed", "hp", "ammo", "flags".</summary>
    public required string Field { get; init; }

    public required long Expected { get; init; }

    public required long Actual { get; init; }

    public long Delta => Actual - Expected;

    public override string ToString() =>
        $"tick {Tick} slot {Slot} {Field}: expected {Expected}, got {Actual} ({Delta:+#;-#;0})";
}

/// <summary>
/// What a replay found.
/// </summary>
/// <remarks>
/// Reported in <b>wire units</b> throughout — position in fifths of a world
/// pixel, velocity in 1/8.5 px/tick. That is the precision the client can
/// observe, and it is the only precision at which "the same" means anything.
/// </remarks>
public sealed class DriftReport
{
    public required string TraceName { get; init; }

    public required ReplayMode Mode { get; init; }

    /// <summary>Ticks the replay actually ran.</summary>
    public required int TicksReplayed { get; init; }

    /// <summary>Comparisons made — a character on a position-bearing tick.</summary>
    public required int Comparisons { get; init; }

    /// <summary>
    /// Mismatches, oldest first. Closed-loop stops at the first tick that has
    /// any: once a trajectory diverges everything after it is the same bug
    /// reported many times.
    /// </summary>
    public required IReadOnlyList<Mismatch> Mismatches { get; init; }

    /// <summary>Per-axis error magnitudes, for the open-loop distribution.</summary>
    public required IReadOnlyList<long> XErrors { get; init; }

    public required IReadOnlyList<long> YErrors { get; init; }

    /// <summary>
    /// Slots resynchronised from the trace rather than compared: a character
    /// the simulation did not have, which means it had just spawned.
    /// </summary>
    public required int Resyncs { get; init; }

    public bool Passed => Mismatches.Count == 0;

    public long MaxXError => Max(XErrors);

    public long MaxYError => Max(YErrors);

    public long PercentileXError(double percentile) => Percentile(XErrors, percentile);

    public long PercentileYError(double percentile) => Percentile(YErrors, percentile);

    private static long Max(IReadOnlyList<long> values)
    {
        long max = 0;
        foreach (var value in values)
            max = Math.Max(max, value);
        return max;
    }

    private static long Percentile(IReadOnlyList<long> values, double percentile)
    {
        if (values.Count == 0)
            return 0;

        var sorted = values.Order().ToArray();

        // Nearest-rank. With a handful of samples an interpolating percentile
        // invents a value no tick ever had, which is not what a drift figure
        // should be.
        var rank = (int)Math.Ceiling(percentile / 100.0 * sorted.Length) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
    }

    /// <summary>A report a person can act on.</summary>
    public string Render()
    {
        var sb = new StringBuilder();

        sb.Append(TraceName).Append("  [").Append(Mode).Append("]\n");
        sb.Append("  ").Append(TicksReplayed).Append(" ticks, ")
          .Append(Comparisons).Append(" comparison(s)");

        if (Resyncs > 0)
            sb.Append(", ").Append(Resyncs).Append(" resync(s) on spawn");

        sb.Append('\n');

        if (Comparisons == 0)
        {
            sb.Append("  nothing to compare: no slot had a character in both the trace and the simulation\n");
            return sb.ToString();
        }

        if (Passed)
        {
            sb.Append("  no mismatches\n");
        }
        else
        {
            sb.Append("  ").Append(Mismatches.Count).Append(" mismatch(es), first at tick ")
              .Append(Mismatches[0].Tick).Append(":\n");

            // Enough context to see the shape of the divergence without
            // printing a hundred lines of the same bug.
            foreach (var mismatch in Mismatches.Take(12))
                sb.Append("    ").Append(mismatch).Append('\n');

            if (Mismatches.Count > 12)
                sb.Append("    ... and ").Append(Mismatches.Count - 12).Append(" more\n");
        }

        if (XErrors.Count > 0)
        {
            sb.Append("  |dx| max ").Append(MaxXError)
              .Append(", p99 ").Append(PercentileXError(99))
              .Append("   |dy| max ").Append(MaxYError)
              .Append(", p99 ").Append(PercentileYError(99))
              .Append("   (wire units; 5 = one world pixel)\n");
        }

        return sb.ToString();
    }

    public override string ToString() => Render();

    /// <summary>A one-line summary, for a table of many traces.</summary>
    public string Summary() =>
        $"{(Passed ? "ok  " : "FAIL")} {TraceName,-40} {Mode,-10} " +
        $"{TicksReplayed,6} ticks  {Mismatches.Count,5} mismatch(es)" +
        (XErrors.Count > 0
            ? $"  max |dx| {MaxXError.ToString(CultureInfo.InvariantCulture)}"
            : string.Empty);
}

/// <summary>How a trace is replayed.</summary>
public enum ReplayMode
{
    /// <summary>
    /// Anchor once, then never correct. Divergence accumulates.
    /// </summary>
    /// <remarks>
    /// The honest measure of "is the physics the same", and the target is exact
    /// equality of the quantised wire fields for the whole trace. Both sides do
    /// double-precision arithmetic in the same order, so drift means a real
    /// difference in the maths rather than floating-point noise.
    /// </remarks>
    ClosedLoop,

    /// <summary>
    /// Reset from each position-bearing update and simulate forward to the next
    /// one.
    /// </summary>
    /// <remarks>
    /// Measures what a player actually sees: the prediction error at the moment
    /// of correction. Reported as a distribution rather than pass/fail.
    /// </remarks>
    OpenLoop,
}
