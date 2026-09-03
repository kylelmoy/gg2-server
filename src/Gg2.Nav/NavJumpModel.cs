using Gg2.Protocol;

namespace Gg2.Nav;

/// <summary>GG2's ordinary jump, as the generator models it.</summary>
/// <remarks>
/// Below the ceiling the height curve is <c>p(t) = v0·t − g·t²/2</c>, and that is
/// exact rather than approximate: <c>Character</c>'s Step event adds half the
/// tick's gravity, moves, then adds the other half, and midpoint integration of a
/// constant acceleration lands on the continuous curve at every integer tick.
/// <para>
/// Above it the character has hit its head. GG2 zeroes <c>vspeed</c> on a ceiling
/// and lets gravity take over, so the arc resumes as a fall from rest — and past
/// <see cref="NavConstants.JumpTermVy"/> it stops accelerating, which over a long
/// drop is the difference between 98 ticks and the unclamped parabola's 55.
/// </para>
/// </remarks>
internal sealed class NavJumpModel : NavArcModel
{
    private const int Cell = NavConstants.CellSize;

    private static readonly double ApexHeight =
        NavConstants.JumpV0 * NavConstants.JumpV0 / (2 * NavConstants.JumpGravity);

    /// <summary>Ticks from rest to terminal velocity, and how far that falls.</summary>
    private static readonly double TermTicks = NavConstants.JumpTermVy / NavConstants.JumpGravity;

    private static readonly double TermHeight =
        NavConstants.JumpGravity * TermTicks * TermTicks / 2;

    // The longest arc between two rows, by row difference. Depends on nothing
    // else, so one map's worth is a couple of hundred values. NaN is "not yet".
    private readonly double[] _reach;

    // The one-entry memo Rise() reads. NaN so the first call always misses.
    private double _capIn = double.NaN;
    private double _capOut;
    private double _capRiseEnds;

    public NavJumpModel(byte[] free, int width, int height)
        : base(free, width, height, ApexHeight, (int)Math.Ceiling(ApexHeight / Cell), NavConstants.JumpMaxTicks)
    {
        var riseRows = (int)Math.Ceiling(ApexHeight / Cell) + 2;
        _reach = new double[NavConstants.MaxFall + riseRows + 2];
        Array.Fill(_reach, double.NaN);
    }

    /// <summary>How high an unobstructed jump rises, in px.</summary>
    public static double ApexPx => ApexHeight;

    /// <summary>
    /// ⚠️ The single jump has one apex, so it sweeps for an early landing only
    /// after it. See <see cref="NavArcModel.SweepWholeDescent"/>.
    /// </summary>
    protected override bool SweepWholeDescent => false;

    public override double Height(double t, double cap)
    {
        var (clamped, riseEnds) = Rise(cap);

        if (t <= riseEnds)
            return (NavConstants.JumpV0 * t) - (NavConstants.JumpGravity * t * t / 2);

        return clamped - Drop(t - riseEnds);
    }

    public override double FlightTime(int fromRow, int toRow, double cap)
    {
        var (clamped, riseEnds) = Rise(cap);
        var riseWorld = (fromRow - toRow) * (double)Cell;

        // Higher than the character can get.
        if (riseWorld > clamped)
            return -1;

        return riseEnds + FallTime(clamped - riseWorld);
    }

    protected override double RiseEndsAt(double cap) => Rise(cap).RiseEnds;

    /// <summary>
    /// How far, in world px, the longest arc between two rows could carry — or -1
    /// when there is no arc between them at any speed.
    /// </summary>
    /// <remarks>
    /// Both cases let the candidate fan reject a surface, or a whole row of them,
    /// without building an arc for it: <see cref="NavArcModel.Takeoff"/> would try
    /// every takeoff and every lead and reject each one on the same arithmetic,
    /// and this is that arithmetic once.
    /// <para>
    /// The longest arc is the one under NO ceiling, because both halves of the
    /// flight time grow with the cap — the rise is longer, and the descent starts
    /// higher. So an unobstructed apex bounds every cap a real takeoff could have.
    /// </para>
    /// <para>
    /// ⚠️ <b>A flight time over <see cref="NavConstants.JumpMaxTicks"/> is NOT a
    /// rejection here, even though <see cref="NavArcModel.Flight"/> rejects on
    /// it.</b> A low ceiling makes an arc SHORTER, so a long descent the uncapped
    /// arc takes 70 ticks to make can be a 40-tick arc from a takeoff with
    /// something over its head, and that arc is real. It does still bound the
    /// distance, hence the clamp rather than a rejection. The only outright cutoff
    /// is "the rise is taller than the apex", because no ceiling can raise one.
    /// </para>
    /// </remarks>
    /// <param name="rowDelta">
    /// Source row minus target row, so a target <em>below</em> the source is
    /// negative and reaches −<see cref="NavConstants.MaxFall"/>, while one above
    /// cannot exceed the apex.
    /// </param>
    public double ReachWorld(int rowDelta)
    {
        var at = rowDelta + NavConstants.MaxFall;

        if (at < 0 || at >= _reach.Length)
            return -1;

        if (!double.IsNaN(_reach[at]))
            return _reach[at];

        var t = FlightTime(rowDelta, 0, ApexHeight);
        var value = t < 0 ? -1 : NavConstants.JumpVx * Math.Min(t, NavConstants.JumpMaxTicks);
        _reach[at] = value;
        return value;
    }

    /// <summary>
    /// The cap the arc actually rises to, and the tick the rise ends.
    /// </summary>
    /// <remarks>
    /// Both <see cref="Height"/> and <see cref="FlightTime"/> start here, and both
    /// are called many times in a row with the <em>same</em> cap — once per sample
    /// along one arc. Caching the last one turns that square root from per-sample
    /// into per-arc, which is most of what an arc walk costs. The cached values are
    /// bit-identical to recomputing them, so this is speed only.
    /// </remarks>
    private (double Cap, double RiseEnds) Rise(double cap)
    {
        if (cap.Equals(_capIn))
            return (_capOut, _capRiseEnds);

        var clamped = Math.Clamp(cap, 0, ApexHeight);

        var riseEnds = (NavConstants.JumpV0
            - Math.Sqrt(Math.Max(0, (NavConstants.JumpV0 * NavConstants.JumpV0)
                - (2 * NavConstants.JumpGravity * clamped)))) / NavConstants.JumpGravity;

        _capIn = cap;
        _capOut = clamped;
        _capRiseEnds = riseEnds;
        return (clamped, riseEnds);
    }

    private static double Drop(double d) =>
        d <= TermTicks
            ? NavConstants.JumpGravity * d * d / 2
            : TermHeight + (NavConstants.JumpTermVy * (d - TermTicks));

    private static double FallTime(double fall) =>
        fall <= TermHeight
            ? Math.Sqrt(2 * fall / NavConstants.JumpGravity)
            : TermTicks + ((fall - TermHeight) / NavConstants.JumpTermVy);
}
