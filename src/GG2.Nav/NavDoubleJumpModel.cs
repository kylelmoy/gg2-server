using GG2.Protocol;

namespace GG2.Nav;

/// <summary>GG2's SECOND jump — the arcs only a Scout can fly.</summary>
/// <remarks>
/// <b>The second jump is not an extra impulse on top of the first — it is a
/// RESET.</b> <c>Character/Begin Step</c> does <c>vspeed = -jumpStrength</c>
/// outright, discarding whatever vertical speed the character had, so the compound
/// arc is two ordinary jumps welded at the re-jump tick: the standard curve until
/// then, and from then a fresh launch from whatever height the first one had
/// reached. <c>jumpStrength</c> is <c>baseJumpStrength</c> for every class — the
/// Scout overrides <c>capStrength</c>, <c>canDoublejump</c>, <c>baseRunPower</c>
/// and <c>numFlames</c>, and not this — so
/// <see cref="NavConstants.JumpV0"/> is the impulse both times.
/// <para>
/// The gate on it is <c>vspeed &gt; -jumpStrength</c>, satisfied at any tick ≥ 1:
/// at the re-jump the character is rising strictly slower than it launched. Tick 0
/// is excluded anyway, since <c>onground</c> is still true on the takeoff tick and
/// the first branch of that <c>if</c> consumes the press.
/// </para>
/// <para>
/// Peak height is therefore up to twice the single-jump apex — 114.8 px against
/// 57.4 — when the second press lands exactly on the first arc's apex. That
/// doubling is the whole reason these edges are worth generating.
/// </para>
/// <para>
/// ⚠️ <b>One ceiling, both phases.</b> The cap is a height above the TAKEOFF row,
/// so a single number governs both impulses: phase 2 may rise to what is left of
/// it above where phase 1 finished, and hits the same ceiling the first jump would
/// have. A cap that is too HIGH is a fiction and one that is too LOW is only a
/// pessimism, so where the two phases disagree this takes the lower.
/// </para>
/// <para>
/// ⚠️ <b>The class is Scout, but the GEOMETRY still uses Heavy's
/// <see cref="NavConstants.JumpVx"/></b> — see <see cref="NavArcModel.Flight"/>
/// for why that constant is not free to move.
/// </para>
/// </remarks>
internal sealed class NavDoubleJumpModel : NavArcModel
{
    private const int Cell = NavConstants.CellSize;

    private static readonly double SingleApex =
        NavConstants.JumpV0 * NavConstants.JumpV0 / (2 * NavConstants.JumpGravity);

    private static readonly double TermTicks = NavConstants.JumpTermVy / NavConstants.JumpGravity;

    private static readonly double TermHeight = NavConstants.JumpGravity * TermTicks * TermTicks / 2;

    /// <summary>
    /// The re-jump ticks worth trying, cheapest question first.
    /// </summary>
    /// <remarks>
    /// Pressing exactly on the first apex buys the most HEIGHT, and height is what
    /// these edges are for, so that is tried first and a climb only it can make is
    /// found without paying for the rest. The later presses buy AIRTIME instead — a
    /// second jump taken while already falling launches from lower but extends the
    /// flight — which is what carries a long gap. Five values rather than a sweep,
    /// because the fan already costs takeoffs × leads per surface and this
    /// multiplies it.
    /// </remarks>
    private static readonly int[] RejumpTicks = BuildRejumpTicks();

    /// <summary>Which of <see cref="RejumpTicks"/> the search is currently trying.</summary>
    /// <remarks>
    /// Read by <see cref="Height"/> and published as
    /// <see cref="NavArc.Rejump"/>, so the curve an arc was proven on and the tick
    /// the edge records are the same number by construction. Left at the winning
    /// value after a successful takeoff, which is what lets the gate sampler
    /// re-walk the arc afterwards.
    /// </remarks>
    private int _rejump = -1;

    public NavDoubleJumpModel(byte[] free, int width, int height)
        : base(free, width, height, 2 * SingleApex,
            (int)Math.Ceiling(2 * SingleApex / Cell) + 1, NavConstants.DjMaxTicks)
    {
    }

    /// <summary>How high a compound arc rises with nothing above it, in px.</summary>
    public double Apex2Px => Apex;

    /// <summary>
    /// What ONE jump could already have done, in px.
    /// </summary>
    /// <remarks>
    /// A rise past this is the only thing in the whole candidate pool that a
    /// second impulse is strictly needed for. Derived from the compound apex rather
    /// than re-deriving <c>v0²/2g</c>, so the two cannot drift apart.
    /// </remarks>
    public double SingleApexPx => Apex / 2;

    protected override int Rejump => _rejump;

    public override double Height(double t, double cap)
    {
        var cap1 = Math.Clamp(cap, 0, SingleApex);

        if (t <= _rejump)
            return RiseAt(t, cap1);

        var hj = RiseAt(_rejump, cap1);
        var cap2 = Math.Clamp(cap - hj, 0, SingleApex);

        return hj + RiseAt(t - _rejump, cap2);
    }

    /// <summary>
    /// How long the compound arc lasts before it comes back down to the target
    /// row, or -1 if it cannot get there at all.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The rise is measured against the peak of the WHOLE arc, not of either
    /// impulse.</b> A target above the first apex but below the second is precisely
    /// the case these edges exist for, and testing phase 1 alone would reject every
    /// one of them.
    /// </remarks>
    public override double FlightTime(int fromRow, int toRow, double cap)
    {
        var cap1 = Math.Clamp(cap, 0, SingleApex);
        var hj = RiseAt(_rejump, cap1);
        var cap2 = Math.Clamp(cap - hj, 0, SingleApex);

        var riseEnds2 = (NavConstants.JumpV0
            - Math.Sqrt(Math.Max(0, (NavConstants.JumpV0 * NavConstants.JumpV0)
                - (2 * NavConstants.JumpGravity * cap2)))) / NavConstants.JumpGravity;

        var peak = hj + cap2;
        var riseWorld = (fromRow - toRow) * (double)Cell;

        if (riseWorld > peak)
            return -1;

        var fall = peak - riseWorld;
        var d = fall <= TermHeight
            ? Math.Sqrt(2 * fall / NavConstants.JumpGravity)
            : TermTicks + ((fall - TermHeight) / NavConstants.JumpTermVy);

        return _rejump + riseEnds2 + d;
    }

    /// <summary>
    /// The lead loop, with the re-jump tick innermost.
    /// </summary>
    /// <remarks>
    /// Innermost so the cheapest question — "does the highest arc from this takeoff
    /// reach?" — is asked before the expensive ones, and so a found arc stops the
    /// whole search.
    /// </remarks>
    protected override bool TryLeads(
        int[] nodeGrid, int from, int fromRow, int toRow, int bx0, int bx1,
        int dir, int toNode, double cap, NavCanFly? canFly)
    {
        for (var lead = NavConstants.JumpLandLead; lead >= 0; lead--)
        {
            foreach (var tj in RejumpTicks)
            {
                _rejump = tj;

                if (TryLead(nodeGrid, from, fromRow, toRow, bx0, bx1, dir, toNode, cap, lead, canFly))
                    return true;
            }
        }

        return false;
    }

    /// <summary>Where one ordinary jump is at <paramref name="t"/>, under a cap it may not exceed.</summary>
    private static double RiseAt(double t, double cap)
    {
        var riseEnds = (NavConstants.JumpV0
            - Math.Sqrt(Math.Max(0, (NavConstants.JumpV0 * NavConstants.JumpV0)
                - (2 * NavConstants.JumpGravity * cap)))) / NavConstants.JumpGravity;

        if (t <= riseEnds)
            return (NavConstants.JumpV0 * t) - (NavConstants.JumpGravity * t * t / 2);

        var d = t - riseEnds;
        var drop = d <= TermTicks
            ? NavConstants.JumpGravity * d * d / 2
            : TermHeight + (NavConstants.JumpTermVy * (d - TermTicks));

        return cap - drop;
    }

    private static int[] BuildRejumpTicks()
    {
        var apexTick = Math.Max(1, (int)Math.Round(NavConstants.JumpV0 / NavConstants.JumpGravity, MidpointRounding.AwayFromZero));
        double[] spread =
        [
            apexTick,
            1,
            Math.Round(apexTick / 2.0, MidpointRounding.AwayFromZero),
            Math.Round(apexTick * 1.5, MidpointRounding.AwayFromZero),
            apexTick * 2,
        ];

        var ticks = new List<int>();

        foreach (var candidate in spread)
        {
            var value = Math.Max(1, (int)Math.Round(candidate, MidpointRounding.AwayFromZero));

            if (ticks.Count < NavConstants.DjTickTries && !ticks.Contains(value))
                ticks.Add(value);
        }

        return [.. ticks];
    }
}
