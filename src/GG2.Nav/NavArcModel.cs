using GG2.Protocol;

namespace GG2.Nav;

/// <summary>The arc a takeoff search proved, as the fan reads it back.</summary>
/// <remarks>
/// ⚠️ <b><see cref="Vx"/> and <see cref="Ticks"/> go into the cache
/// UNROUNDED.</b> They are what the follower steers by, so rounding them is not a
/// tidy-up — a slow climb wants 0.54 px/tick and rounding calls that 1, an 85%
/// error. <c>NavEdge.Bucket</c> in the reader carries the same warning from the
/// other side.
/// </remarks>
internal readonly record struct NavArc(int LandCol, double Ticks, double Vx, double CapH, int Rejump)
{
    public static NavArc None => new(-1, -1, 0, 0, -1);
}

/// <summary>
/// The character's veto on an arc the geometry has already accepted.
/// </summary>
/// <remarks>
/// Given the takeoff column, the speed the arc asks for, its airtime and the
/// <em>span</em> of the landing run, can a bot leaving that column actually cover
/// the ground? Passed in rather than computed by the model because the answer
/// depends on the walkable floor behind the takeoff, which is a fact about the
/// node list rather than about the arc — see <see cref="NavCharacter"/>.
/// <para>
/// ⚠️ <b>The whole SPAN goes in, not the near column on the search side.</b> The
/// sweep direction is not always the direction the jump travels: a landing run
/// can straddle the takeoff column, which is what a near-vertical hop onto the
/// ledge overhead is. Naming the far side of such a run as the thing to reach
/// asks an 18 px hop to cover 102 px, and <c>koth_gallery</c>'s pit climb lost
/// all ten of its arcs that way.
/// </para>
/// </remarks>
internal delegate bool NavCanFly(int takeoff, double vx, double ticks, int landX0, int landX1);

/// <summary>
/// Everything that samples a jump arc: the height curve, the flight time, the
/// ceiling, the landing column, the takeoff search and the clearance walk.
/// </summary>
/// <remarks>
/// <b>The one rule behind all of it: a jump ends where the arc comes back
/// down.</b> Not where it first arrives over the target (true on the first tick
/// of a drop), and not where it first arrives level with the target (true
/// seventeen ticks before a steep climb lands).
/// <para>
/// ⚠️ <b><c>gg2-nav-gen</c> kept three copies of this — one per manoeuvre — and
/// this port has one.</b> That is a real departure and it was made on the
/// strength of an oracle the JavaScript did not have: <c>NavGeneratorTests</c>
/// regenerates all 25 checked-in graphs and compares them <em>byte for byte</em>
/// against <c>nav/</c>. The reason for the duplication over there was that a
/// change to the single-jump path could not be checked against anything; here it
/// can, so the reason has expired. ⛔ <b>If that test is ever deleted or relaxed,
/// this consolidation loses its justification</b> — the three manoeuvres differ
/// in ways that look cosmetic and are not, and the differences are marked below
/// with the model that owns them.
/// </para>
/// </remarks>
internal abstract class NavArcModel
{
    private const int Cell = NavConstants.CellSize;

    /// <summary>The clearance grid, dilated by the character box.</summary>
    protected readonly byte[] Free;

    protected readonly int Width;
    protected readonly int LastCol;
    protected readonly int LastRow;

    /// <summary>How high this manoeuvre can rise with nothing above it.</summary>
    protected readonly double Apex;

    /// <summary>How far up <see cref="CapStraight"/> scans, in rows.</summary>
    private readonly int _maxRows;

    /// <summary>The longest flight this manoeuvre's edges may declare.</summary>
    private readonly int _maxTicks;

    // The straight-up ceiling scan's answer depends on nothing but (col, row),
    // and the candidate fan asks for the same column hundreds of times, so it is
    // memoised. -1 is "not yet computed"; every real answer is >= 0.
    private readonly double[] _straightCap;

    private NavArc _arc = NavArc.None;

    protected NavArcModel(byte[] free, int width, int height, double apex, int maxRows, int maxTicks)
    {
        ArgumentNullException.ThrowIfNull(free);

        Free = free;
        Width = width;
        LastCol = width - NavConstants.BoxW;
        LastRow = height - NavConstants.BoxH;
        Apex = apex;
        _maxRows = maxRows;
        _maxTicks = maxTicks;

        _straightCap = new double[width * (LastRow + 2)];
        Array.Fill(_straightCap, -1);
    }

    /// <summary>The arc the last successful <see cref="Takeoff"/> proved.</summary>
    /// <remarks>
    /// Reused between calls the way the reference published it in globals: read it
    /// straight after a call that returned a column, and not otherwise.
    /// </remarks>
    public NavArc Arc => _arc;

    /// <summary>
    /// ⚠️ <b>Whether the early-landing sweep runs on every descending sample, or
    /// only past a named apex.</b>
    /// </summary>
    /// <remarks>
    /// The single jump has exactly one apex, so it sweeps only after it. The
    /// compound arc of a double jump has <em>two</em> descents — the tail of the
    /// first jump, if the press comes late, and the real one — so it sweeps
    /// whenever the arc is going down. Strictly more restrictive, which is the
    /// safe direction: it can refuse an edge, never invent one. The rocket and
    /// sticky models inherit the stricter rule.
    /// </remarks>
    protected virtual bool SweepWholeDescent => true;

    /// <summary>How far above the takeoff row the arc is after <paramref name="t"/> ticks, in px.</summary>
    public abstract double Height(double t, double cap);

    /// <summary>
    /// How long the arc lasts before it comes back down to <paramref name="toRow"/>,
    /// or -1 if it cannot get there at all.
    /// </summary>
    /// <remarks>
    /// The flight time is a function of the <em>rise</em>. Horizontal distance
    /// does not shorten it; it only has to be coverable within it.
    /// </remarks>
    public abstract double FlightTime(int fromRow, int toRow, double cap);

    /// <summary>
    /// How many points along an arc get sampled: one per tick, never fewer than
    /// <see cref="NavConstants.JumpSamples"/>.
    /// </summary>
    /// <remarks>
    /// A tick moves the character at most 4.53 px sideways and 8.3 px down, both
    /// under two cells, so nothing thin gets tunnelled through.
    /// <para>
    /// ⚠️ <b>The snap is where Game Maker's 80-bit floating point shows through,
    /// and it is a correction rather than a fudge.</b> Delphi evaluates a whole
    /// expression in x87 extended registers before storing a double, so a flight
    /// time whose exact value is an integer comes out of the game as exactly that
    /// integer; the same formula in IEEE doubles lands an ulp over, and
    /// <c>ceil</c> then asks for one MORE sample than the game took. Different
    /// sample points are different clearance reads: <c>cp_dirtbowl</c>'s n158 →
    /// n132 is a real 21.0-tick arc that the game flies at 21 samples and a
    /// 22-sample walk finds a ceiling in. Everything here is derived from small
    /// decimal rationals, so a value within 1e-9 of an integer <em>is</em> that
    /// integer and x87 simply got closer to it.
    /// </para>
    /// </remarks>
    public int SamplesFor(double ticks)
    {
        var nearest = Gml.Round(ticks);
        var t = Math.Abs(ticks - nearest) < 1e-9 ? nearest : ticks;
        return Math.Max(NavConstants.JumpSamples, (int)Math.Ceiling(t));
    }

    /// <summary>
    /// <see cref="FlightTime"/>, refused when the arc is too long sideways or
    /// hangs in the air past this manoeuvre's cap.
    /// </summary>
    public double Flight(int fromRow, int toRow, int dCells, double cap)
    {
        var t = FlightTime(fromRow, toRow, cap);

        if (t < 0)
            return -1;

        // ⚠️ NAV_JUMP_VX, Heavy's ceiling, bounds every model's arc — including
        // the Scout's and the Soldier's. That constant does two jobs: it caps
        // what an arc may ASK for here, and it is the per-sample catch-up budget
        // in the clearance walk that decides how much ground a tick spent against
        // a wall can be made up. Raising it for the faster manoeuvres would change
        // the second of those, which is the half that was learned from broken
        // maps. The class's own speed is what the canFly veto checks against.
        if (dCells * (double)Cell > NavConstants.JumpVx * t)
            return -1;

        return t > _maxTicks ? -1 : t;
    }

    /// <summary>
    /// <c>navJumpCeiling</c>: how high a character standing at
    /// (<paramref name="col"/>, <paramref name="row"/>) and jumping
    /// <paramref name="dir"/> can rise before the map stops it.
    /// </summary>
    /// <remarks>
    /// Refusing a jump because the character would bump its head is wrong and
    /// expensively so: GG2's jump is a fixed impulse that rises 57 px whether the
    /// room has 57 px of headroom or not, and hitting a ceiling just brings it
    /// down early, which is a perfectly ordinary jump.
    /// <para>
    /// Pass one reads straight up from the takeoff column. Pass two is the same
    /// scan over the corridor the arc could actually be in, one anchor column of
    /// drift a row toward the target, and a row counts as open when <em>any</em>
    /// column the arc could be in by then is open.
    /// </para>
    /// <para>
    /// ⚠️ <b>Pass two is only consulted when the straight-up scan does not
    /// already give the jump the climb it asked for.</b> Widening it
    /// unconditionally looks harmless — it can only report MORE headroom — but the
    /// cap decides which arcs get costed at all and the per-side quota then keeps a
    /// fixed number of them, so more candidates means a DIFFERENT set kept, not a
    /// superset.
    /// </para>
    /// </remarks>
    public double Ceiling(int col, int row, int dir, int colLimit, double needRise)
    {
        var straight = CapStraight(col, row);

        if (straight >= needRise)
            return straight;

        var cap = Apex;

        for (var r = 1; r <= _maxRows; r++)
        {
            var scanRow = row - r;

            // Above the top of the walkmask is open sky, not a ceiling.
            if (scanRow < 0)
                break;

            int lo, hi;

            if (dir < 0)
            {
                lo = Math.Max(col - r, colLimit);
                hi = col;
            }
            else
            {
                lo = col;
                hi = Math.Min(col + r, colLimit);
            }

            lo = Math.Max(0, lo);
            hi = Math.Min(Width - 1, hi);

            var open = false;
            var here = scanRow * Width;
            var above = (scanRow - 1) * Width;

            for (var x = lo; x <= hi && !open; x++)
            {
                if (Free[here + x] != 1)
                    continue;

                if (scanRow - 1 < 0 || Free[above + x] == 1)
                    open = true;
            }

            if (!open)
            {
                cap = (r - 1) * (double)Cell;
                break;
            }
        }

        return cap;
    }

    private double CapStraight(int col, int row)
    {
        var key = (row * Width) + col;
        var cached = _straightCap[key];

        if (cached >= 0)
            return cached;

        var cap = Apex;

        for (var r = 1; r <= _maxRows; r++)
        {
            var scanRow = row - r;

            if (scanRow < 0)
                break;

            // ⚠️ Airborne is a row taller than standing. The clearance grid is
            // dilated by NAV_BOX_H = 6 cells, exactly the 36 px body GG2 rests
            // row-aligned on a surface, and a body in flight is at an arbitrary y
            // and touches seven.
            var open = Free[(scanRow * Width) + col] == 1;

            if (open && scanRow - 1 >= 0)
                open = Free[((scanRow - 1) * Width) + col] == 1;

            if (!open)
            {
                cap = (r - 1) * (double)Cell;
                break;
            }
        }

        _straightCap[key] = cap;
        return cap;
    }

    /// <summary>
    /// <c>navJumpLanding</c>: which anchor column on the target a jump aims at
    /// when it aims <paramref name="lead"/> columns into the surface, or -1.
    /// </summary>
    /// <remarks>
    /// The obvious answer — the nearest column — aims at the <em>worst</em> column
    /// on the surface: a node's span runs from <see cref="NavConstants.BoxW"/> − 1
    /// columns before the solid it stands on, because an anchor counts as
    /// standable when any one of the four footprint cells is supported. So the
    /// nearest anchor is where the body hangs off the edge by three cells of four,
    /// and landing one pixel short is a fall.
    /// </remarks>
    private int Landing(int fromRow, int toRow, int takeoff, int bx0, int bx1, int dir, int lead, double cap)
    {
        int col;

        if (dir < 0)
        {
            col = Math.Min(bx1, takeoff - 1) - lead;
            if (col < bx0)
                return -1;
        }
        else
        {
            col = Math.Max(bx0, takeoff + 1) + lead;
            if (col > bx1)
                return -1;
        }

        return Flight(fromRow, toRow, Math.Abs(col - takeoff), cap) < 0 ? -1 : col;
    }

    /// <summary>
    /// <c>navJumpTakeoff</c>: the cheapest takeoff column on the source surface
    /// from which a jump onto the target is actually flyable, or -1.
    /// </summary>
    /// <remarks>
    /// What makes a takeoff unusable is almost never the landing — it is what is
    /// <em>above</em> the takeoff, and nothing about aiming elsewhere can fix
    /// that, which is what makes this a search over takeoffs rather than over
    /// landings.
    /// <para>
    /// The search starts at the column nearest the target and steps away from it,
    /// not at the source run's own end. Those agree whenever the target lies beyond
    /// that end, and stop agreeing when the spans overlap — a platform under a
    /// ledge that is part of the same wide run can have its nearest approach
    /// columns nowhere near the run's ends at all.
    /// </para>
    /// <para>
    /// Leads are tried from the largest down because landing well inside a surface
    /// is worth having, and it has to be able to lose: a bigger lead is a longer
    /// arc over the same fixed airtime, so it is a faster one, and a faster arc is
    /// already moving sideways while it is still level with the thing it is
    /// climbing onto.
    /// </para>
    /// </remarks>
    public int Takeoff(
        int[] nodeGrid, int fromRow, int ax0, int ax1, int toRow, int bx0, int bx1,
        int dir, int toNode, NavCanFly? canFly)
    {
        ArgumentNullException.ThrowIfNull(nodeGrid);

        var basis = dir < 0
            ? Math.Max(ax0, Math.Min(ax1, bx1 + 1))
            : Math.Min(ax1, Math.Max(ax0, bx0 - 1));

        var tries = dir < 0
            ? Math.Min(NavConstants.JumpTakeoffTries, ax1 - basis + 1)
            : Math.Min(NavConstants.JumpTakeoffTries, basis - ax0 + 1);

        var needRise = (fromRow - toRow) * (double)Cell;

        for (var off = 0; off < tries; off++)
        {
            var from = dir < 0 ? basis + off : basis - off;

            // The corridor the ceiling scan may consider is bounded by the far
            // edge of the surface being aimed at: an arc that drifts past its own
            // landing is not a climb onto it.
            var cap = Ceiling(from, fromRow, dir, dir < 0 ? bx0 : bx1, needRise);

            if (TryLeads(nodeGrid, from, fromRow, toRow, bx0, bx1, dir, toNode, cap, canFly))
                return from;
        }

        _arc = NavArc.None;
        return -1;
    }

    /// <summary>
    /// One takeoff column's worth of the search, over every lead — and, for the
    /// double jump, every re-jump tick.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The only extension point in the search, and the double jump is the
    /// only user.</b> Its arc has a second impulse whose tick is part of what is
    /// being searched for, so it wraps this with an inner loop; every other model
    /// takes it as it stands.
    /// </remarks>
    protected virtual bool TryLeads(
        int[] nodeGrid, int from, int fromRow, int toRow, int bx0, int bx1,
        int dir, int toNode, double cap, NavCanFly? canFly)
    {
        for (var lead = NavConstants.JumpLandLead; lead >= 0; lead--)
        {
            if (TryLead(nodeGrid, from, fromRow, toRow, bx0, bx1, dir, toNode, cap, lead, canFly))
                return true;
        }

        return false;
    }

    /// <summary>One (takeoff, lead) pair: prove the arc, or move on.</summary>
    protected bool TryLead(
        int[] nodeGrid, int from, int fromRow, int toRow, int bx0, int bx1,
        int dir, int toNode, double cap, int lead, NavCanFly? canFly)
    {
        var landCol = Landing(fromRow, toRow, from, bx0, bx1, dir, lead, cap);

        if (landCol < 0)
            return false;

        var ticks = Flight(fromRow, toRow, Math.Abs(landCol - from), cap);

        if (ticks < 0)
            return false;

        var vx = Math.Abs(landCol - from) * (double)Cell / ticks;

        // Cheaper than the clearance sweep and it rejects a whole class the sweep
        // cannot see: an arc whose geometry is perfect and whose speed the
        // character has no way to reach. Tried first for that reason.
        if (canFly is not null && !canFly(from, vx, ticks, bx0, bx1))
            return false;

        if (!Flyable(nodeGrid, from, fromRow, landCol, dir, ticks, vx, cap, toNode))
            return false;

        _arc = new NavArc(landCol, ticks, vx, cap, Rejump);
        return true;
    }

    /// <summary>
    /// The re-jump tick to record on a proved arc; -1 for a single impulse.
    /// </summary>
    /// <remarks>
    /// The double jump's is whichever tick its own search is currently trying,
    /// which is also what its <see cref="Height"/> reads — so an arc's recorded
    /// re-jump and the curve it was proven on cannot disagree.
    /// </remarks>
    protected virtual int Rejump => -1;

    /// <summary>
    /// The clearance walk: step the proven arc column by column and see whether
    /// the body actually gets to the column the edge would name.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Being held against a wall is allowed but NOT free.</b> GG2 does not
    /// move a character into terrain, it stops it against it and keeps the
    /// vertical motion, which is the whole of climbing a flush wall face — so
    /// treating the first cell of lateral overlap as fatal refuses an entire class
    /// of climb. But a tick spent against a wall is a tick of travel that is
    /// simply gone, and it can only be made up out of whatever surplus the planned
    /// arc left. A slow climbing arc has plenty; a near-flat arc across a gap has
    /// none. Hence the per-sample budget: move toward the plan by at most
    /// <see cref="NavConstants.JumpVx"/> × dt, never past the plan itself.
    /// </remarks>
    private bool Flyable(
        int[] nodeGrid, int from, int fromRow, int landCol, int dir,
        double ticks, double vx, double cap, int toNode)
    {
        var samples = SamplesFor(ticks);

        // Once the rise ends the character is coming down, and the first surface
        // it comes down on is where the jump ends whatever the graph intended.
        // Under a ceiling that moment arrives early, so this is not a constant.
        var apexTick = SweepWholeDescent ? 0 : RiseEndsAt(cap);

        var cx = from;
        var cxf = (double)from;
        var prevRow = fromRow;
        var tPrev = 0.0;

        for (var k = 1; k <= samples; k++)
        {
            var t = ticks * k / samples;
            var sy = (int)Gml.Round(fromRow - (Height(t, cap) / Cell));

            var want = from + (dir * vx * t / Cell);
            var stepCols = NavConstants.JumpVx * (t - tPrev) / Cell;
            var aimCol = dir > 0
                ? (int)Gml.Round(Math.Min(want, cxf + stepCols))
                : (int)Gml.Round(Math.Max(want, cxf - stepCols));

            // The two clearance rows this sample reads, hoisted: the sweep below
            // can walk a dozen columns and each step would otherwise recompute
            // them.
            var rowAt = sy >= 0 ? sy * Width : -1;
            var rowAbove = sy >= 1 ? (sy - 1) * Width : -1;

            // Stepping one column at a time is what makes this a sweep rather
            // than a guess: the character cannot teleport past a pillar between
            // two samples.
            var heldBack = false;

            while (cx != aimCol)
            {
                var next = cx + dir;

                if (next < 0 || next > LastCol)
                {
                    heldBack = true;
                    break;
                }

                if (rowAt >= 0)
                {
                    if (Free[rowAt + next] != 1)
                    {
                        heldBack = true;
                        break;
                    }

                    if (rowAbove >= 0 && Free[rowAbove + next] != 1)
                    {
                        heldBack = true;
                        break;
                    }
                }

                cx = next;
            }

            if (heldBack)
                cxf = cx;
            else if (dir > 0)
                cxf = Math.Min(want, cxf + stepCols);
            else
                cxf = Math.Max(want, cxf - stepCols);

            tPrev = t;

            if (cx < 0 || cx > LastCol || sy > LastRow)
                return false;

            if (rowAt >= 0)
            {
                if (Free[rowAt + cx] != 1)
                    return false;

                if (rowAbove >= 0 && Free[rowAbove + cx] != 1)
                    return false;

                // ⚠️ The descending check is a SWEEP, not a sample. The node grid
                // marks one row out of the mask and a descending arc covers most
                // of a row per tick, so sampling walks straight past the surface
                // the jump really ends on.
                //
                // Clearance says the body fits; it does not say the character is
                // still in the air. A cell that is some node's anchor has ground
                // directly under it, so on the way down that is where this jump
                // ends — and if it is not the surface being aimed at, the edge is
                // a fiction.
                if (sy > prevRow && (SweepWholeDescent || t > apexTick))
                {
                    for (var sweep = Math.Max(0, prevRow + 1); sweep <= sy; sweep++)
                    {
                        var onto = nodeGrid[(sweep * Width) + cx];

                        if (onto >= 0 && onto != toNode)
                            return false;
                    }
                }
            }

            prevRow = sy;
        }

        // A wall that held the character back the whole way is not a jump onto
        // anything — it is a hop that ends against the wall.
        return cx == landCol;
    }

    /// <summary>
    /// The tick the rise ends, for a model that sweeps only after its one apex.
    /// </summary>
    protected virtual double RiseEndsAt(double cap) => 0;
}
