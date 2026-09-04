using Gg2.Protocol;

namespace Gg2.Nav;

/// <summary>
/// The nav graph build, exactly as the reference did it, with no game.
/// </summary>
/// <remarks>
/// This is <c>navBuildStart</c>/<c>navBuildStep</c>'s whole state machine
/// collapsed into one straight-line pass. The chunking in the game existed only
/// because a ~1.3 s blocking loop stops the server servicing sockets and drops
/// every connected client; nothing here has clients, so the stages run to
/// completion one after another and the result is identical.
/// <list type="bullet">
/// <item><b>solidity</b> — the walkmask, plus the instances that are not in it</item>
/// <item><b>clearance</b> — dilate by the character box: "can a body stand anchored here"</item>
/// <item><b>nodes</b> — run-length encode the standable anchors into floor surfaces</item>
/// <item><b>edges</b> — walk, fall, drop-through, jump, double jump, rocket jump, sticky jump, movebox</item>
/// <item><b>finish</b> — counting-sort the edges by from-node</item>
/// </list>
/// <para>
/// ⚠️ <b>Every decision here is load-bearing, and none of it is arbitrary.</b> The
/// takeoff search, the landing lead, the ceiling corridor, the jump pruning quota,
/// the free step-up — each of them cost a real map its connectivity when it was
/// got wrong, and each is documented in place below.
/// </para>
/// </remarks>
internal static class NavGraphBuilder
{
    private const int Cell = NavConstants.CellSize;

    /// <summary>
    /// <c>navWalkEdges</c>: runs one mask cell apart whose spans touch, plus runs
    /// on the same row that touch.
    /// </summary>
    /// <remarks>
    /// GG2 gives a free 6 px — exactly one cell — step-up and step-down in
    /// <c>characterHitObstacle</c>, so a row-to-row step needs no jump; a same-row
    /// touch needs no step at all — it is one continuous floor that node
    /// extraction only split because the support kind, a door or a gate changed.
    /// <para>
    /// Each edge carries the gate code of the node it ARRIVES at and nothing else.
    /// A walk crosses exactly one node boundary, so that is the only gate it can
    /// meet — and taking the destination's code is what lets a bot walk <em>out</em>
    /// of a gate it may not walk into.
    /// </para>
    /// <para>
    /// A <c>LeftDoor</c> blocks only leftward crossing and a <c>RightDoor</c> only
    /// rightward (<c>Character</c>'s Collision events check the sign of
    /// <c>hspeed</c>), so the edge in the disallowed direction is simply not added.
    /// </para>
    /// </remarks>
    public static void WalkEdges(NavEdgeBuffer edges, NavNodeSet nodes, int[] rowStart, int height)
    {
        var n = nodes.Count;

        if (n <= 0)
            return;

        for (var i = 0; i < n; i++)
        {
            var row = nodes.Y[i];
            var ax0 = nodes.X0[i];
            var ax1 = nodes.X1[i];

            if (row + 1 > height)
                continue;

            var j = rowStart[row + 1];

            if (j < 0)
                continue;

            while (j < n && nodes.Y[j] == row + 1)
            {
                var bx0 = nodes.X0[j];
                var bx1 = nodes.X1[j];

                // Touch or overlap: one cell of horizontal slack is the step itself.
                if (bx0 <= ax1 + 1 && ax0 <= bx1 + 1)
                {
                    var cost = Math.Max(1, Math.Abs(((ax0 + ax1) / 2.0) - ((bx0 + bx1) / 2.0)));
                    edges.Add(i, j, NavConstants.EdgeWalk, 0, 0, cost, nodes.Gates[j], -1);
                    edges.Add(j, i, NavConstants.EdgeWalk, 0, 0, cost, nodes.Gates[i], -1);
                }

                j++;
            }
        }

        // Same-row touches: only ever the immediate next node in sort order, since
        // two nodes on one row cannot overlap and nothing else sorts between them.
        for (var i = 0; i < n - 1; i++)
        {
            if (nodes.Y[i] != nodes.Y[i + 1])
                continue;

            if (nodes.X0[i + 1] > nodes.X1[i] + 1)
                continue;

            var doorA = nodes.Doors[i];
            var doorB = nodes.Doors[i + 1];

            if (doorA != NavConstants.DoorRight && doorB != NavConstants.DoorRight)
                edges.Add(i, i + 1, NavConstants.EdgeWalk, 0, 0, 1, nodes.Gates[i + 1], -1);

            if (doorA != NavConstants.DoorLeft && doorB != NavConstants.DoorLeft)
                edges.Add(i + 1, i, NavConstants.EdgeWalk, 0, 0, 1, nodes.Gates[i], -1);
        }
    }

    /// <summary>
    /// <c>navFallEdges</c>: walking off the end of a surface.
    /// </summary>
    /// <remarks>
    /// One-way by construction — getting back up needs a jump edge — and the swept
    /// column is scanned for gate cells so a drop past a spawn gate is offered only
    /// to whoever may pass it.
    /// <para>
    /// ⚠️ <b>The takeoff column is stored and it is not a decoration.</b> Which END
    /// of the run a fall leaves from is not recoverable from the two nodes alone
    /// where the landing reaches under both ends, and a follower guessing between
    /// them walks the wrong way about half the time.
    /// </para>
    /// </remarks>
    public static void FallEdges(
        NavEdgeBuffer edges, NavNodeSet nodes, byte[] free, int[] nodeGrid, byte[] gate, int width, int height)
    {
        var maxRow = height - NavConstants.BoxH;
        var lastCol = width - NavConstants.BoxW;

        for (var i = 0; i < nodes.Count; i++)
        {
            var row = nodes.Y[i];
            var sourceGate = nodes.Gates[i];

            for (var side = 0; side < 2; side++)
            {
                var edgeCol = side == 0 ? nodes.X0[i] - 1 : nodes.X1[i] + 1;

                if (edgeCol < 0 || edgeCol > lastCol)
                    continue;

                // The character has to be able to occupy the column it steps into,
                // at the height it is standing at, or it is walking into a wall.
                if (free[(row * width) + edgeCol] != 1)
                    continue;

                // And there has to be nothing to stand on there, or it is not a
                // ledge — it is a step across, which WalkEdges already links.
                if (nodeGrid[(row * width) + edgeCol] >= 0)
                    continue;

                var landed = -1;
                var dropped = 0;
                var arcGate = NavConstants.GateNone;
                var limit = Math.Min(maxRow, row + NavConstants.MaxFall);

                for (var y = row + 1; y <= limit; y++)
                {
                    var at = (y * width) + edgeCol;

                    if (free[at] != 1)
                        break;

                    if (arcGate == NavConstants.GateNone)
                    {
                        var cellGate = gate[at];

                        if (cellGate != NavConstants.GateNone && cellGate != sourceGate)
                            arcGate = cellGate;
                    }

                    dropped++;
                    landed = nodeGrid[at];

                    if (landed >= 0)
                        break;
                }

                if (landed >= 0 && landed != i)
                {
                    if (arcGate == NavConstants.GateNone)
                        arcGate = nodes.Gates[landed];

                    edges.Add(i, landed, NavConstants.EdgeFall, 0, dropped, Math.Max(1, dropped), arcGate, edgeCol);
                }
            }
        }
    }

    /// <summary>
    /// <c>navDropEdges</c>: holding DOWN through a platform in the middle of a run.
    /// </summary>
    /// <remarks>
    /// Only runs whose support is platform and nothing else. A run with even one
    /// cell of terrain under it cannot be dropped through.
    /// </remarks>
    public static void DropEdges(
        NavEdgeBuffer edges, NavNodeSet nodes, byte[] free, int[] nodeGrid, byte[] gate, int width, int height)
    {
        var maxRow = height - NavConstants.BoxH;

        for (var i = 0; i < nodes.Count; i++)
        {
            if (nodes.Flags[i] != 1)
                continue;

            var row = nodes.Y[i];
            var midX = (nodes.X0[i] + nodes.X1[i]) / 2;
            var sourceGate = nodes.Gates[i];

            var landed = -1;
            var dropped = 0;
            var arcGate = NavConstants.GateNone;
            var limit = Math.Min(maxRow, row + NavConstants.MaxFall);

            for (var y = row + 1; y <= limit; y++)
            {
                var at = (y * width) + midX;

                if (free[at] != 1)
                    break;

                if (arcGate == NavConstants.GateNone)
                {
                    var cellGate = gate[at];

                    if (cellGate != NavConstants.GateNone && cellGate != sourceGate)
                        arcGate = cellGate;
                }

                dropped++;
                landed = nodeGrid[at];

                if (landed >= 0)
                    break;
            }

            if (landed >= 0 && landed != i)
            {
                if (arcGate == NavConstants.GateNone)
                    arcGate = nodes.Gates[landed];

                edges.Add(i, landed, NavConstants.EdgeDropthrough, 0, dropped, Math.Max(1, dropped), arcGate, -1);
            }
        }
    }

    /// <summary>
    /// How much run-up a takeoff column actually has, in cells.
    /// </summary>
    /// <remarks>
    /// <c>botRunupCol</c>, as the generator has to see it. The follower walks
    /// BACKWARDS from the takeoff column — away from the jump — along whatever
    /// continuous floor it can walk, and jumps from the takeoff carrying whatever
    /// speed that bought. So the run-up is a property of the map, and a node that
    /// overhangs in the direction it is jumping has none at all.
    /// <para>
    /// "Continuous floor" is the walk edge's own rule and is deliberately expressed
    /// that way rather than as a mask scan: a walk edge is one surface by
    /// construction — a same-row touch, or a one-cell step
    /// <c>characterHitObstacle</c> takes for free while KEEPING <c>hspeed</c> — so
    /// a chain of them is exactly the ground a run-up can cover. Reading the mask
    /// instead would count a cell the character cannot actually run along.
    /// </para>
    /// <para>
    /// Walk edges are not indexed yet at this point in the build (the counting sort
    /// is the last stage), so neighbours are found the way <see cref="WalkEdges"/>
    /// itself finds them. Only a neighbour that both TOUCHES the frontier and
    /// extends past it counts, or the walk sits on the spot.
    /// </para>
    /// </remarks>
    public static int RunupCols(NavNodeSet nodes, int[] rowStart, int i, int takeoff, int dir)
    {
        var n = nodes.Count;
        var seen = new HashSet<int> { i };
        var node = i;
        var frontier = dir > 0 ? nodes.X0[i] : nodes.X1[i];

        for (var hop = 0; hop < BotConstants.RunupHops; hop++)
        {
            if (Math.Abs(takeoff - frontier) >= BotConstants.RunupCells)
                break;

            var ay = nodes.Y[node];
            var ax0 = nodes.X0[node];
            var ax1 = nodes.X1[node];
            var best = -1;

            for (var ry = ay - 1; ry <= ay + 1; ry++)
            {
                if (ry < 0 || ry >= rowStart.Length)
                    continue;

                var j = rowStart[ry];

                if (j < 0)
                    continue;

                for (; j < n && nodes.Y[j] == ry; j++)
                {
                    if (j == node || seen.Contains(j))
                        continue;

                    var bx0 = nodes.X0[j];
                    var bx1 = nodes.X1[j];

                    // The walk edge's own touch test.
                    if (bx0 > ax1 + 1 || ax0 > bx1 + 1)
                        continue;

                    // A same-row crossing can be refused by a door; a step to
                    // another row never is, which is WalkEdges' rule and not a
                    // simplification of it.
                    if (ry == ay)
                    {
                        var back = dir > 0 ? NavConstants.DoorLeft : NavConstants.DoorRight;

                        if (nodes.Doors[node] == back || nodes.Doors[j] == back)
                            continue;
                    }

                    // Touches the frontier and extends past it.
                    if (dir > 0)
                    {
                        if (bx1 < frontier - 1 || bx1 >= frontier)
                            continue;

                        if (best < 0 || bx0 < nodes.X0[best])
                            best = j;
                    }
                    else
                    {
                        if (bx0 > frontier + 1 || bx0 <= frontier)
                            continue;

                        if (best < 0 || bx1 > nodes.X1[best])
                            best = j;
                    }
                }
            }

            if (best < 0)
                break;

            seen.Add(best);
            node = best;
            frontier = dir > 0 ? nodes.X0[best] : nodes.X1[best];
        }

        return Math.Min(Math.Abs(takeoff - frontier), BotConstants.RunupCells);
    }

    /// <summary>
    /// <c>navJumpEdges</c>: what makes the graph connected in both directions —
    /// walk and fall alone let a bot descend and never climb back.
    /// </summary>
    /// <remarks>
    /// Three things are doing the real work and none is obvious.
    /// <list type="bullet">
    /// <item><b>The candidate fan is PRUNED</b> to the cheapest
    /// <see cref="NavConstants.JumpMaxPerSide"/> per side, because emitting every
    /// surface the envelope admits is what makes this technique notorious for
    /// redundant links — a far platform is almost always reachable by landing on a
    /// nearer one and going again.</item>
    /// <item><b>One extra slot is kept back for diversity</b>: whatever is cheapest
    /// among the leftovers whose row is not close to anything already kept.
    /// "Cheapest few" is blind to what a landing connects to — a node at the top of
    /// a long staircase has many cheap candidates one step further down that same
    /// staircase, all mutually redundant, and they fill the quota before a
    /// structurally load-bearing candidate is considered. On <c>ctf_truefort</c>
    /// the missing edge ranked 7th cheapest.</item>
    /// <item><b>And a second reserved slot for the cheapest landing ABOVE the
    /// source</b>, when the quota and the diversity slot between them kept none.
    /// The diversity rule measures rows apart, not up versus down, so a descending
    /// staircase satisfies it: on <c>dkoth_atalia</c> the ground beside each control
    /// point fans onto four steps down, spends its bonus on a fifth, and drops the
    /// ONE candidate that climbs — the control point itself, provable and 0.33 cost
    /// units too dear. Both teams' points were unreachable, in a mode where
    /// standing on the point IS the goal.</item>
    /// </list>
    /// <para>
    /// ⚠️ <b>Cost is charged in FLIGHT TICKS, not ground covered</b>, so an arc and
    /// a walk are priced in the same currency — otherwise a 20-tick hop down three
    /// steps looks cheaper than walking them, and bots hop down every staircase they
    /// meet. The consequence is that a climb is systematically the dearer candidate:
    /// it hangs in the air ~17 ticks to gain 54 px where a step down is over in 8.
    /// Cheapness therefore selects AGAINST the direction only a jump can serve,
    /// which is why the ascent slot is a slot and not a discount. Across all 24
    /// shipped graphs it is claimed by 6 node-sides out of 19,902, so it adds at
    /// most six edges and removes none.
    /// </para>
    /// </remarks>
    public static void JumpEdges(
        NavEdgeBuffer edges, NavNodeSet nodes, NavJumpModel model, int[] nodeGrid, byte[] gate,
        int[] rowStart, int width, int height)
    {
        var n = nodes.Count;
        var maxRowIndex = height - NavConstants.BoxH;

        // The class the graph is written for. One graph is handed to every class
        // and Heavy is the slowest of them, so Heavy is the one that decides
        // whether an arc is honest — see NavCharacter.
        var profile = NavCharacter.Profile("heavy");

        // Reused across every node and side; the fan is the hot loop of the build.
        var candidateNode = new int[n];
        var candidateCost = new double[n];
        var keptRows = new int[NavConstants.JumpMaxPerSide + 3];

        for (var i = 0; i < n; i++)
        {
            var ay = nodes.Y[i];
            var ax0 = nodes.X0[i];
            var ax1 = nodes.X1[i];
            var sourceGate = nodes.Gates[i];

            // The apex is about 9.6 cells, so nothing above that is ever
            // reachable; below, the fall cap bounds how far a jump can carry.
            var minRow = Math.Max(0, ay - 10);
            var maxRow = Math.Min(maxRowIndex, ay + NavConstants.MaxFall);

            for (var side = 0; side < 2; side++)
            {
                var dir = side == 0 ? -1 : 1;
                var candidates = 0;
                var hasAscent = false;

                var runup = new Dictionary<int, int>();

                bool CanFly(int from, double vx, double ticks, int bx0, int bx1)
                {
                    if (!runup.TryGetValue(from, out var cols))
                    {
                        cols = RunupCols(nodes, rowStart, i, from, dir);
                        runup[from] = cols;
                    }

                    // The ground to cover is the follower's own jump need, derived
                    // the same way: the first column of the landing run on the side
                    // the jump actually travels, and ZERO when the run already
                    // spans the takeoff column. That last case is not an edge case
                    // — it is every near-vertical hop onto a ledge overhead.
                    var need = bx0 > from ? bx0 - from : (bx1 < from ? from - bx1 : 0);

                    if (need == 0)
                        return true;

                    // ⚠️ NOT credited here: the six px of intra-cell lead the
                    // follower allows itself. A nav column is six world px wide and
                    // the follower may leave from anywhere in that band, so
                    // crediting it — and the extra cell of floor the bot
                    // accelerates over to earn it — hands back 209 of the arcs this
                    // refuses. It was built, measured, and taken out again: those
                    // arcs are flyable ONLY by a bot that has drifted to the far
                    // side of its takeoff cell first, and the takeoff patience timer
                    // fires a jump after 60 ticks whether it has or not. Scored
                    // against a bot that gets no lead, the relaxed graph put
                    // ctf_oldfort back to 18% of red's routes crossing an arc it
                    // cannot fly (this one: 0%), cp_dirtbowl to 7% and ctf_orange to
                    // 4%. The conservative line is that an arc is honest when the
                    // run-up the NODE offers is enough, and the lead is margin on
                    // top of it.
                    //
                    // The gate also refuses to jump while the bot is going faster
                    // than the arc asks for, so a long run-up cannot be spent above
                    // needVx + BOT_JUMP_VTOL.
                    var v0 = Math.Min(
                        NavCharacter.RunupSpeed(cols * (double)Cell, profile),
                        Math.Min(vx + BotConstants.JumpVtol, profile.MaxSpeed));

                    return NavCharacter.FlyDistance(vx, ticks, v0, profile) + BotConstants.JumpReachTol
                        >= need * (double)Cell;
                }

                for (var ry = minRow; ry <= maxRow; ry++)
                {
                    var j = rowStart[ry];

                    if (j < 0)
                        continue;

                    // The furthest any arc from this row to that one could carry. A
                    // row no arc can reach at all is skipped whole, and within a
                    // reachable row a surface further away than this cannot be
                    // reached from any takeoff. The takeoff search would reject each
                    // of those by trying every takeoff and every lead and failing
                    // the same arithmetic; this is that arithmetic, once per row.
                    var reach = model.ReachWorld(ay - ry);

                    if (reach < 0)
                        continue;

                    for (; j < n && nodes.Y[j] == ry; j++)
                    {
                        if (j == i)
                            continue;

                        var bx0 = nodes.X0[j];
                        var bx1 = nodes.X1[j];

                        // A step a walk edge already covers needs no jump. Skipping
                        // these is the main defence against the redundant-link
                        // explosion.
                        if (Math.Abs(ry - ay) <= 1 && bx0 <= ax1 + 1 && ax0 <= bx1 + 1)
                            continue;

                        var gap = bx0 > ax1 ? bx0 - ax1 : (ax0 > bx1 ? ax0 - bx1 : 0);

                        if (gap * (double)Cell > reach)
                            continue;

                        // Which column to leave from is a search, not a given.
                        var from = model.Takeoff(nodeGrid, ay, ax0, ax1, ry, bx0, bx1, dir, j, CanFly);

                        if (from < 0)
                            continue;

                        candidateNode[candidates] = j;
                        candidateCost[candidates] = Math.Max(
                            Math.Max(1, Math.Abs(model.Arc.LandCol - from)), model.Arc.Ticks)
                            + NavConstants.JumpPenalty;
                        candidates++;

                        if (ry < ay)
                            hasAscent = true;
                    }
                }

                if (candidates == 0)
                    continue;

                var kept = 0;
                var bonusUsed = false;

                // The ascent slot is only open on a side that has something to
                // climb to. Without this guard a side whose whole fan descends
                // would drain its entire candidate list looking for a climb that
                // does not exist, and keep nothing for it.
                var ascentUsed = !hasAscent;
                var keptRowCount = 0;
                var live = candidates;

                while (live > 0 && (kept < NavConstants.JumpMaxPerSide || !bonusUsed || !ascentUsed))
                {
                    var chosen = TakeCheapest(candidateNode, candidateCost, ref live);
                    var by = nodes.Y[chosen];
                    var bx0 = nodes.X0[chosen];
                    var bx1 = nodes.X1[chosen];

                    // Past the normal quota there are two reserved slots, and which
                    // one a candidate claims decides whether it is kept.
                    //
                    // The ascent slot is tested first and deliberately skips the
                    // diversity test. Being above the source is the only property
                    // being asked for, and "far from the rows already kept" exists
                    // to stop a fifth rung of the same staircase — it has nothing to
                    // protect on a side that has no upward jump at all. Testing it
                    // first also keeps the loop's termination simple: while
                    // ascentUsed is false, no ascending candidate is ever skipped.
                    var claim = 0;

                    if (kept >= NavConstants.JumpMaxPerSide)
                    {
                        if (by < ay && !ascentUsed)
                        {
                            claim = 1;
                        }
                        else if (!bonusUsed)
                        {
                            var far = true;

                            for (var k = 0; k < keptRowCount && far; k++)
                            {
                                if (Math.Abs(by - keptRows[k]) <= NavConstants.JumpDiversityRows)
                                    far = false;
                            }

                            if (!far)
                                continue;

                            claim = 2;
                        }
                        else
                        {
                            continue;
                        }
                    }

                    // Re-derive the winning takeoff rather than carrying it out of
                    // the candidate loop: this runs at most a handful of times per
                    // side against a loop that considered every surface in the
                    // envelope, and it keeps one definition of what a flyable arc is.
                    var from = model.Takeoff(nodeGrid, ay, ax0, ax1, by, bx0, bx1, dir, chosen, CanFly);

                    if (from < 0)
                        continue;

                    var arc = model.Arc;
                    var cost = Math.Max(Math.Max(1, Math.Abs(arc.LandCol - from)), arc.Ticks)
                        + NavConstants.JumpPenalty;

                    var arcGate = ArcGate(model, gate, nodes, from, ay, dir, arc, chosen, sourceGate, width, maxRowIndex);

                    edges.Add(i, chosen, NavConstants.EdgeJump, arc.Vx, arc.Ticks, cost, arcGate, from);

                    keptRows[keptRowCount++] = by;

                    // Any kept climb closes the ascent slot, whichever slot paid.
                    if (by < ay)
                        ascentUsed = true;

                    if (kept < NavConstants.JumpMaxPerSide)
                        kept++;
                    else if (claim == 2)
                        bonusUsed = true;
                }
            }
        }
    }

    /// <summary>
    /// <c>ds_priority_delete_min</c>, reproduced rather than approximated.
    /// </summary>
    /// <remarks>
    /// ⚠️ GM8 scans for the first STRICTLY smaller priority and then removes the
    /// winner by moving the LAST entry into its slot — a Delphi list's
    /// delete-by-swap. That is not a stable order and it is not the reverse of one
    /// either: for three equal-cost candidates added as 29, 30, 31 it hands back
    /// 29, 31, 30, and for the same three behind one cheaper entry it hands back
    /// 31, 30, 29.
    /// <para>
    /// Which order this is matters because it decides which candidates fill a fixed
    /// quota, so a "reasonable" tie-break is a different graph. Sorting the
    /// candidates instead — the obvious way to write this — disagreed with the game
    /// on 23 of 23 shipped maps during the original port.
    /// </para>
    /// </remarks>
    private static int TakeCheapest(int[] node, double[] cost, ref int live)
    {
        var best = 0;

        for (var k = 1; k < live; k++)
        {
            if (cost[k] < cost[best])
                best = k;
        }

        var chosen = node[best];
        live--;
        node[best] = node[live];
        cost[best] = cost[live];
        return chosen;
    }

    /// <summary>
    /// Re-walks a proved arc looking for the gate it passes through.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>This is the one place gates genuinely have to be arc-sampled rather
    /// than read off a node.</b> A spawn gate stands in a doorway on a flat floor,
    /// its own clearance is open, and both halves of that floor are separate nodes
    /// — so without this every gate in the game would have a pair of jump edges
    /// hopping straight over it.
    /// <para>
    /// Done here rather than in the candidate loop because it only matters for an
    /// arc that is actually going to be emitted.
    /// </para>
    /// </remarks>
    private static int ArcGate(
        NavArcModel model, byte[] gate, NavNodeSet nodes, int from, int ay, int dir,
        in NavArc arc, int toNode, int sourceGate, int width, int maxRowIndex)
    {
        var arcGate = NavConstants.GateNone;
        var samples = model.SamplesFor(arc.Ticks);

        for (var k = 1; k <= samples && arcGate == NavConstants.GateNone; k++)
        {
            var t = arc.Ticks * k / samples;
            var sx = (int)Gml.Round(from + (dir * arc.Vx * t / Cell));
            var sy = (int)Gml.Round(ay - (model.Height(t, arc.CapH) / Cell));

            if (sx < 0 || sx > width - NavConstants.BoxW || sy < 0 || sy > maxRowIndex)
                continue;

            var cellGate = gate[(sy * width) + sx];

            if (cellGate != NavConstants.GateNone && cellGate != sourceGate)
                arcGate = cellGate;
        }

        return arcGate == NavConstants.GateNone ? nodes.Gates[toNode] : arcGate;
    }

    /// <summary>
    /// Everything reachable from <paramref name="source"/> in at most
    /// <paramref name="hops"/> hops over the edges the earlier stages emitted.
    /// </summary>
    /// <remarks>
    /// A breadth-first walk done once per source node rather than once per
    /// candidate. <paramref name="seenAt"/> holds the stamp that last touched a
    /// node, so clearing it between sources is a counter bump rather than a fill.
    /// </remarks>
    private static void MarkReachable(
        List<int>?[] adjacency, int[] seenAt, int[] queue, int source, int hops, int stamp)
    {
        seenAt[source] = stamp;

        var head = 0;
        var tail = 0;
        queue[tail++] = source;

        for (var hop = 0; hop < hops && head < tail; hop++)
        {
            var endOfHop = tail;

            while (head < endOfHop)
            {
                var u = queue[head++];
                var outs = adjacency[u];

                if (outs is null)
                    continue;

                foreach (var v in outs)
                {
                    if (seenAt[v] == stamp)
                        continue;

                    seenAt[v] = stamp;
                    queue[tail++] = v;
                }
            }
        }
    }

    /// <summary>
    /// The Scout's second jump, as graph edges.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>This runs AFTER <see cref="JumpEdges"/> and cannot displace anything
    /// it kept. That is the single most important property here.</b> The per-side
    /// quota is fixed, so feeding double-jump candidates into the same fan would
    /// mean a DIFFERENT set of ordinary jumps kept, not a superset — on every map,
    /// for every class, to add a manoeuvre only one class can perform. Instead these
    /// get their own quota, their own candidate pool, and a skip on any pair some
    /// earlier stage already connected.
    /// <para>
    /// Three restrictions on what is even considered, each earning its place.
    /// <b>Already reachable</b>: any pair the earlier stages join within
    /// <see cref="NavConstants.DjSkipHops"/> hops is skipped outright. That started
    /// as a one-hop test and one hop is far too weak — measured on
    /// <c>ctf_2dfort</c>, of the first 240 edges it kept, ZERO connected a pair that
    /// was otherwise unreachable, so the fan was spending its quota on shortcuts
    /// rather than on access. <b>Upward only</b>: falls and drop-throughs already
    /// serve every descent, and serve it for every class; what no other edge kind
    /// can offer is height. <b>Dearer than a jump</b>:
    /// <see cref="NavConstants.DjPenalty"/> is 40 against
    /// <see cref="NavConstants.JumpPenalty"/> 6, so where a Scout has both a route
    /// over these and an ordinary one, A* takes the ordinary one.
    /// </para>
    /// <para>
    /// ⚠️ <b>40 is not a taste knob and 12 was wrong.</b> Swept live over 75 random
    /// <c>ctf_2dfort</c> routes: at 12, 72% of routes took an edge, 33 got cheaper
    /// and 21 got DEARER for a 1.6% net saving; at 40, 19% took one, 13 cheaper, 1
    /// dearer, 2.0% saving — the only one-sided row and also the best.
    /// <c>ctf_truefort</c> agrees independently. Routes getting dearer when edges
    /// are ADDED is the inadmissible heuristic, not the edges, and that is the real
    /// argument for a high number: keep these out of the range where a bad heuristic
    /// picks wrong.
    /// </para>
    /// <para>
    /// <b>The reserved climb slot.</b> The quota ranks on cost, and a tall climb
    /// hangs in the air far longer than a flat hop of the same length — so cheapness
    /// selects against the one thing a second jump is FOR, and with the quota at 2 a
    /// side that has both spends all of it on flat shortcuts. The slot is reserved
    /// for a rise past the single-jump apex, which is what only a SECOND jump can
    /// do, and a side whose whole fan sits under that apex never opens it.
    /// </para>
    /// </remarks>
    public static void DoubleJumpEdges(
        NavEdgeBuffer edges, NavNodeSet nodes, NavDoubleJumpModel model, int[] nodeGrid, byte[] gate,
        int[] rowStart, List<int>?[] adjacency, int width, int height)
    {
        var n = nodes.Count;
        var maxRowIndex = height - NavConstants.BoxH;

        // navFindPath refuses these edges to everything but the Scout, so the
        // slowest class that has to fly one is the only class that can.
        var profile = NavCharacter.Profile("scout");

        // How many rows above the source a compound arc could possibly reach. The
        // single-jump fan uses 10; two impulses need twice that.
        var riseRows = (int)Math.Ceiling(model.Apex2Px / Cell) + 1;
        var singleApex = model.SingleApexPx;

        var candidateNode = new int[n];
        var candidateCost = new double[n];

        var seenAt = new int[n];
        Array.Fill(seenAt, -1);
        var queue = new int[n];
        var stamp = 0;

        // A target whose run STRADDLES the takeoff column is reachable with dir −1
        // and dir +1 both — it is the near-vertical hop onto the ledge overhead —
        // so without this every such pair is emitted twice and A* relaxes an
        // identical edge for nothing.
        var emitted = new HashSet<long>();

        for (var i = 0; i < n; i++)
        {
            var ay = nodes.Y[i];
            var ax0 = nodes.X0[i];
            var ax1 = nodes.X1[i];
            var sourceGate = nodes.Gates[i];
            var minRow = Math.Max(0, ay - riseRows);

            stamp++;
            MarkReachable(adjacency, seenAt, queue, i, NavConstants.DjSkipHops, stamp);

            for (var side = 0; side < 2; side++)
            {
                var dir = side == 0 ? -1 : 1;
                var candidates = 0;
                var hasHighClimb = false;

                var runup = new Dictionary<int, int>();

                bool CanFly(int from, double vx, double ticks, int bx0, int bx1)
                {
                    if (!runup.TryGetValue(from, out var cols))
                    {
                        cols = RunupCols(nodes, rowStart, i, from, dir);
                        runup[from] = cols;
                    }

                    var need = bx0 > from ? bx0 - from : (bx1 < from ? from - bx1 : 0);

                    if (need == 0)
                        return true;

                    var v0 = Math.Min(
                        NavCharacter.RunupSpeed(cols * (double)Cell, profile),
                        Math.Min(vx + BotConstants.JumpVtol, profile.MaxSpeed));

                    return NavCharacter.FlyDistance(vx, ticks, v0, profile) + BotConstants.JumpReachTol
                        >= need * (double)Cell;
                }

                // Upward only, so the row scan stops at the source's own row.
                for (var ry = minRow; ry <= ay; ry++)
                {
                    var j = rowStart[ry];

                    if (j < 0)
                        continue;

                    for (; j < n && nodes.Y[j] == ry; j++)
                    {
                        if (j == i || seenAt[j] == stamp || emitted.Contains(((long)i * n) + j))
                            continue;

                        var bx0 = nodes.X0[j];
                        var bx1 = nodes.X1[j];

                        // The same walk-adjacency skip JumpEdges makes: a step a
                        // walk edge covers needs no jump, let alone two.
                        if (Math.Abs(ry - ay) <= 1 && bx0 <= ax1 + 1 && ax0 <= bx1 + 1)
                            continue;

                        var from = model.Takeoff(nodeGrid, ay, ax0, ax1, ry, bx0, bx1, dir, j, CanFly);

                        if (from < 0)
                            continue;

                        candidateNode[candidates] = j;
                        candidateCost[candidates] = Math.Max(
                            Math.Max(1, Math.Abs(model.Arc.LandCol - from)), model.Arc.Ticks)
                            + NavConstants.DjPenalty;
                        candidates++;

                        if ((ay - ry) * (double)Cell > singleApex)
                            hasHighClimb = true;
                    }
                }

                if (candidates == 0)
                    continue;

                var kept = 0;
                var live = candidates;

                // The reserved slot is only open on a side that has something to
                // spend it on — the same guard, and the same reason, as JumpEdges'.
                var climbUsed = !hasHighClimb;

                while (live > 0 && (kept < NavConstants.DjMaxPerSide || !climbUsed))
                {
                    var chosen = TakeCheapest(candidateNode, candidateCost, ref live);

                    if (emitted.Contains(((long)i * n) + chosen))
                        continue;

                    var by = nodes.Y[chosen];
                    var bx0 = nodes.X0[chosen];
                    var bx1 = nodes.X1[chosen];

                    // Past the quota, only an out-of-single-jump-reach climb may be
                    // kept, and only one. Everything else is a shortcut, and
                    // shortcuts are what the quota already spent itself on.
                    if (kept >= NavConstants.DjMaxPerSide)
                    {
                        if (climbUsed || (ay - by) * (double)Cell <= singleApex)
                            continue;
                    }

                    var from = model.Takeoff(nodeGrid, ay, ax0, ax1, by, bx0, bx1, dir, chosen, CanFly);

                    if (from < 0)
                        continue;

                    var arc = model.Arc;
                    var cost = Math.Max(Math.Max(1, Math.Abs(arc.LandCol - from)), arc.Ticks)
                        + NavConstants.DjPenalty;

                    var arcGate = ArcGate(model, gate, nodes, from, ay, dir, arc, chosen, sourceGate, width, maxRowIndex);

                    edges.Add(i, chosen, NavConstants.EdgeDoublejump, arc.Vx, arc.Ticks, cost, arcGate, from, arc.Rejump);
                    emitted.Add(((long)i * n) + chosen);

                    // Any kept high climb closes the reserved slot, whichever slot
                    // paid for it — so a side whose cheapest two candidates already
                    // include one keeps two edges, not three.
                    if ((ay - by) * (double)Cell > singleApex)
                        climbUsed = true;

                    if (kept < NavConstants.DjMaxPerSide)
                        kept++;
                }
            }
        }
    }

    /// <summary>
    /// A Soldier firing at its own feet, as graph edges.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>This fan is deliberately shaped differently from
    /// <see cref="DoubleJumpEdges"/>, and the difference is the POINT of the feature
    /// rather than an oversight. Do not harmonise the two.</b>
    /// <para>
    /// The double-jump fan exists to reach what is otherwise unreachable, so it
    /// skips any pair the graph already joins within three hops and prices itself
    /// out of ordinary routes. Rocket jumping is not for that, and measuring it as
    /// though it were gives the wrong answer: closing every team's spawn-reachable
    /// set under this envelope over all 24 shipped graphs opens 219 distinct places
    /// against the double jump's 171 — about one extra place per team-map — and
    /// changes objective reachability on exactly zero of them. Judged as an access
    /// feature it does not earn its keep.
    /// </para>
    /// <para>
    /// <b>It is a VARIETY feature.</b> What it buys is a Soldier that takes a
    /// different line to the same place as the rest of its team, gets there by a
    /// shorter one, and reads like a person playing rather than a unit executing.
    /// So <see cref="NavConstants.RjSkipHops"/> is <b>1</b> — skip only a duplicate
    /// direct edge, because a two- or three-hop shortcut <em>is</em> the feature —
    /// and <see cref="NavConstants.RjPenalty"/> is low enough that A* actually takes
    /// them. Swept over 116 real spawn→objective legs: at 10, a third of Soldier
    /// routes take one, 30 get cheaper and 9 dearer, for a 5.8% net saving.
    /// </para>
    /// <para>
    /// ⚠️ <b>Two profiles for one bot.</b> The run-up is walked under moveStatus 0
    /// and the arc is flown under moveStatus 1; using one law for both is the
    /// mistake <see cref="NavCharacter.FlyDistanceRocket"/> exists to prevent.
    /// </para>
    /// </remarks>
    public static void RocketJumpEdges(
        NavEdgeBuffer edges, NavNodeSet nodes, NavRocketJumpModel model, int[] nodeGrid, byte[] gate,
        int[] rowStart, List<int>?[] adjacency, int width, int height)
    {
        var n = nodes.Count;
        var maxRowIndex = height - NavConstants.BoxH;

        var ground = NavCharacter.Profile("soldier");
        var air = NavCharacter.RocketProfile("soldier");

        var riseRows = (int)Math.Ceiling(model.ApexPx / Cell) + 1;

        var candidateNode = new int[n];
        var candidateCost = new double[n];

        var seenAt = new int[n];
        Array.Fill(seenAt, -1);
        var queue = new int[n];
        var stamp = 0;

        var emitted = new HashSet<long>();

        for (var i = 0; i < n; i++)
        {
            var ay = nodes.Y[i];
            var ax0 = nodes.X0[i];
            var ax1 = nodes.X1[i];
            var sourceGate = nodes.Gates[i];
            var minRow = Math.Max(0, ay - riseRows);

            stamp++;
            MarkReachable(adjacency, seenAt, queue, i, NavConstants.RjSkipHops, stamp);

            for (var side = 0; side < 2; side++)
            {
                var dir = side == 0 ? -1 : 1;
                var candidates = 0;

                var runup = new Dictionary<int, int>();

                bool CanFly(int from, double vx, double ticks, int bx0, int bx1)
                {
                    if (!runup.TryGetValue(from, out var cols))
                    {
                        cols = RunupCols(nodes, rowStart, i, from, dir);
                        runup[from] = cols;
                    }

                    var need = bx0 > from ? bx0 - from : (bx1 < from ? from - bx1 : 0);

                    if (need == 0)
                        return true;

                    // Run-up on the ground under the ordinary law, flight under the
                    // rocket one. The gate cap is every other fan's: the follower
                    // will not take off faster than the arc asks for plus the
                    // tolerance.
                    var v0 = Math.Min(
                        NavCharacter.RunupSpeed(cols * (double)Cell, ground),
                        Math.Min(vx + BotConstants.JumpVtol, air.MaxSpeed));

                    return NavCharacter.FlyDistanceRocket(vx, ticks, v0, air) + BotConstants.JumpReachTol
                        >= need * (double)Cell;
                }

                // Climbing or level: falls and drop-throughs already serve every
                // descent and serve it for every class. Level pairs are kept because
                // a flat hop across a gap no ordinary jump spans, carried by ~57
                // ticks of airtime that friction never bleeds, is the most visible
                // shortcut this feature has.
                for (var ry = minRow; ry <= ay; ry++)
                {
                    var j = rowStart[ry];

                    if (j < 0)
                        continue;

                    for (; j < n && nodes.Y[j] == ry; j++)
                    {
                        if (j == i || seenAt[j] == stamp || emitted.Contains(((long)i * n) + j))
                            continue;

                        var bx0 = nodes.X0[j];
                        var bx1 = nodes.X1[j];

                        if (Math.Abs(ry - ay) <= 1 && bx0 <= ax1 + 1 && ax0 <= bx1 + 1)
                            continue;

                        var from = model.Takeoff(nodeGrid, ay, ax0, ax1, ry, bx0, bx1, dir, j, CanFly);

                        if (from < 0)
                            continue;

                        candidateNode[candidates] = j;
                        candidateCost[candidates] = Math.Max(
                            Math.Max(1, Math.Abs(model.Arc.LandCol - from)), model.Arc.Ticks)
                            + NavConstants.RjPenalty;
                        candidates++;
                    }
                }

                if (candidates == 0)
                    continue;

                var kept = 0;
                var live = candidates;

                while (live > 0 && kept < NavConstants.RjMaxPerSide)
                {
                    var chosen = TakeCheapest(candidateNode, candidateCost, ref live);

                    if (emitted.Contains(((long)i * n) + chosen))
                        continue;

                    var from = model.Takeoff(
                        nodeGrid, ay, ax0, ax1, nodes.Y[chosen], nodes.X0[chosen], nodes.X1[chosen],
                        dir, chosen, CanFly);

                    if (from < 0)
                        continue;

                    var arc = model.Arc;
                    var cost = Math.Max(Math.Max(1, Math.Abs(arc.LandCol - from)), arc.Ticks)
                        + NavConstants.RjPenalty;

                    var arcGate = ArcGate(model, gate, nodes, from, ay, dir, arc, chosen, sourceGate, width, maxRowIndex);

                    // No re-jump: these arcs have one impulse, and the buffer writes
                    // the -1 the follower's second-impulse branch is inert on.
                    edges.Add(i, chosen, NavConstants.EdgeRocketjump, arc.Vx, arc.Ticks, cost, arcGate, from);
                    emitted.Add(((long)i * n) + chosen);
                    kept++;
                }
            }
        }
    }

    /// <summary>
    /// A Demoman detonating its own mine on the tick it jumps — <b>off by
    /// default</b>.
    /// </summary>
    /// <remarks>
    /// ⚠️⚠️ <b>The GML game must never load a cache containing these.</b> Its
    /// <c>navFindPath</c> takes <c>canDouble</c> and <c>canRocket</c> and refuses
    /// types 5 and 6 per query; it has no <c>canSticky</c> and no refusal for type
    /// 7, so a type-7 edge in its cache routes EVERY class over it — a Medic walking
    /// to a takeoff cell to wait for a mine boost no Medic can produce, blacklisting
    /// it, and asking again forever. This port's <c>NavGraph.EdgeRoutable</c>
    /// refuses them by class, which is the only reason the stage may exist.
    /// <para>
    /// <b>The fan is shaped like <see cref="DoubleJumpEdges"/>, not like
    /// <see cref="RocketJumpEdges"/>, because the measurement said so.</b> Closing
    /// every team's spawn-reachable set under the sticky envelope opens new ground
    /// on 21 of 24 shipped graphs — 1,247 node-openings across 48 team-closures, of
    /// which this fan delivers 1,121 (90%) — against the double jump's 171 places
    /// and the rocket jump's 219. That is an ACCESS feature by an order of
    /// magnitude, so it takes the access shape.
    /// </para>
    /// <para>
    /// Two costs the other fans do not have.
    /// <see cref="NavConstants.SjSetupTicks"/> charges the mine's own journey — fire
    /// at the floor, let it settle — into every edge, so A* prices the standing-still
    /// time and never prefers a sticky jump to a walk that is genuinely faster.
    /// <see cref="NavConstants.SjDamage"/> is NOT charged here; it is the consumer's
    /// per-query business, exactly as the rocket jump's damage is.
    /// </para>
    /// <para>
    /// ⚠️ <b>The veto is a STANDING START, unlike the rocket fan's, and that
    /// difference was paid for before it was written down.</b> The setup wait stands
    /// the bot still on the mine, so the walked-in run-up the rocket fan credits does
    /// not exist here — and the first version credited it anyway, having copied the
    /// rocket fan's veto. 333 of 4,285 emitted edges failed a standing-start reach
    /// test. A human Demoman also does <em>running</em> sticky jumps; that is a
    /// different manoeuvre with a different launch, and relaxing this wants that
    /// measurement in hand, not before.
    /// </para>
    /// </remarks>
    public static void StickyJumpEdges(
        NavEdgeBuffer edges, NavNodeSet nodes, NavRocketJumpModel model, int[] nodeGrid, byte[] gate,
        int[] rowStart, List<int>?[] adjacency, int width, int height)
    {
        var n = nodes.Count;
        var maxRowIndex = height - NavConstants.BoxH;

        // No ground profile: the standing start makes the run-up law moot for this
        // fan, and the reach tolerance is the only ground-side number that survives.
        var air = NavCharacter.RocketProfile("demoman");

        var riseRows = (int)Math.Ceiling(model.ApexPx / Cell) + 1;

        // What one ordinary jump could already have done. For a Demoman there is no
        // second impulse and no rocket, so everything above it is mine-only ground.
        var singleApex = NavJumpModel.ApexPx;

        var candidateNode = new int[n];
        var candidateCost = new double[n];

        var seenAt = new int[n];
        Array.Fill(seenAt, -1);
        var queue = new int[n];
        var stamp = 0;

        var emitted = new HashSet<long>();

        for (var i = 0; i < n; i++)
        {
            var ay = nodes.Y[i];
            var ax0 = nodes.X0[i];
            var ax1 = nodes.X1[i];
            var sourceGate = nodes.Gates[i];
            var minRow = Math.Max(0, ay - riseRows);

            stamp++;
            MarkReachable(adjacency, seenAt, queue, i, NavConstants.SjSkipHops, stamp);

            for (var side = 0; side < 2; side++)
            {
                var dir = side == 0 ? -1 : 1;
                var candidates = 0;
                var hasHighClimb = false;

                // Standing start: v0 is 0, not the walked-in run-up.
                bool CanFly(int from, double vx, double ticks, int bx0, int bx1)
                {
                    var need = bx0 > from ? bx0 - from : (bx1 < from ? from - bx1 : 0);

                    if (need == 0)
                        return true;

                    return NavCharacter.FlyDistanceRocket(vx, ticks, 0, air) + BotConstants.JumpReachTol
                        >= need * (double)Cell;
                }

                for (var ry = minRow; ry <= ay; ry++)
                {
                    var j = rowStart[ry];

                    if (j < 0)
                        continue;

                    for (; j < n && nodes.Y[j] == ry; j++)
                    {
                        if (j == i || seenAt[j] == stamp || emitted.Contains(((long)i * n) + j))
                            continue;

                        var bx0 = nodes.X0[j];
                        var bx1 = nodes.X1[j];

                        if (Math.Abs(ry - ay) <= 1 && bx0 <= ax1 + 1 && ax0 <= bx1 + 1)
                            continue;

                        var from = model.Takeoff(nodeGrid, ay, ax0, ax1, ry, bx0, bx1, dir, j, CanFly);

                        if (from < 0)
                            continue;

                        candidateNode[candidates] = j;
                        candidateCost[candidates] = Math.Max(
                            Math.Max(1, Math.Abs(model.Arc.LandCol - from)), model.Arc.Ticks)
                            + NavConstants.SjSetupTicks + NavConstants.SjPenalty;
                        candidates++;

                        if ((ay - ry) * (double)Cell > singleApex)
                            hasHighClimb = true;
                    }
                }

                if (candidates == 0)
                    continue;

                var kept = 0;
                var live = candidates;
                var climbUsed = !hasHighClimb;

                while (live > 0 && (kept < NavConstants.SjMaxPerSide || !climbUsed))
                {
                    var chosen = TakeCheapest(candidateNode, candidateCost, ref live);

                    if (emitted.Contains(((long)i * n) + chosen))
                        continue;

                    var by = nodes.Y[chosen];

                    if (kept >= NavConstants.SjMaxPerSide)
                    {
                        if (climbUsed || (ay - by) * (double)Cell <= singleApex)
                            continue;
                    }

                    var from = model.Takeoff(
                        nodeGrid, ay, ax0, ax1, by, nodes.X0[chosen], nodes.X1[chosen], dir, chosen, CanFly);

                    if (from < 0)
                        continue;

                    var arc = model.Arc;
                    var cost = Math.Max(Math.Max(1, Math.Abs(arc.LandCol - from)), arc.Ticks)
                        + NavConstants.SjSetupTicks + NavConstants.SjPenalty;

                    var arcGate = ArcGate(model, gate, nodes, from, ay, dir, arc, chosen, sourceGate, width, maxRowIndex);

                    edges.Add(i, chosen, NavConstants.EdgeStickyjump, arc.Vx, arc.Ticks, cost, arcGate, from);
                    emitted.Add(((long)i * n) + chosen);

                    if ((ay - by) * (double)Cell > singleApex)
                        climbUsed = true;

                    if (kept < NavConstants.SjMaxPerSide)
                        kept++;
                }
            }
        }
    }

    /// <summary>
    /// <c>navMoveBoxEdges</c>: walking into a <c>MoveBox</c>.
    /// </summary>
    /// <remarks>
    /// The box accelerates the character while it is inside the bounding box,
    /// exactly as <c>Character</c>'s own collision events do it, and ordinary
    /// gravity carries it the rest of the way once it leaves. Simulated per tick
    /// rather than fitted to a closed form — there is no closed form, since the
    /// accelerated interval's length depends on the box's own size.
    /// <para>
    /// Cost is charged in ticks alone, with no jump-style penalty: these are among
    /// the map's free traversal edges, so a route through one should not be punished
    /// relative to walking the same ground.
    /// </para>
    /// </remarks>
    public static void MoveBoxEdges(
        NavEdgeBuffer edges, NavNodeSet nodes, List<NavInstance> instances, byte[] free,
        int[] nodeGrid, byte[] gate, int width, int height)
    {
        var maxRow = height - NavConstants.BoxH;

        foreach (var box in instances)
        {
            if (!box.HasBox || box.Stamp != NavStamp.MoveBox)
                continue;

            // Entry is the node directly under the box's horizontal centre — a bot
            // walking up to the box along the ground, which is how every shipped
            // use of MoveBox is placed.
            var entryX = Math.Clamp(
                (int)Gml.Round((box.Left + box.Right) / 2 / Cell), 0, width - NavConstants.BoxW);

            var entryNode = -1;
            var entryRow = Math.Max(0, (int)Math.Floor(box.Top / Cell));

            while (entryRow <= maxRow)
            {
                if (free[(entryRow * width) + entryX] != 1)
                    break;

                entryNode = nodeGrid[(entryRow * width) + entryX];

                if (entryNode >= 0)
                    break;

                entryRow++;
            }

            if (entryNode < 0)
                continue;

            var sourceGate = nodes.Gates[entryNode];
            var arcGate = NavConstants.GateNone;
            var vx = 0.0;
            var vy = 0.0;
            var wx = (entryX * (double)Cell) + (Cell / 2.0);
            var wy = entryRow * (double)Cell;
            var landed = -1;
            var blocked = false;
            var t = 1;

            for (; t <= NavConstants.JumpMaxTicks; t++)
            {
                vy += NavConstants.JumpGravity;

                // The push applies for every tick of overlap, mirroring the
                // collision event firing every step rather than once on entry.
                if (wx >= box.Left && wx <= box.Right && wy >= box.Top && wy <= box.Bottom)
                {
                    vx += box.PushX;
                    vy += box.PushY;
                }

                vx = Math.Clamp(vx, -15, 15);
                vy = Math.Clamp(vy, -15, 15);
                wx += vx;
                wy += vy;

                var cx = (int)Gml.Round(wx / Cell);
                var cy = (int)Gml.Round(wy / Cell);

                if (cx < 0 || cx > width - NavConstants.BoxW || cy > maxRow)
                {
                    blocked = true;
                    break;
                }

                // Above the top of the walkmask is open sky, not a ceiling.
                if (cy < 0)
                    continue;

                var at = (cy * width) + cx;

                if (free[at] != 1)
                {
                    blocked = true;
                    break;
                }

                if (arcGate == NavConstants.GateNone)
                {
                    var cellGate = gate[at];

                    if (cellGate != NavConstants.GateNone && cellGate != sourceGate)
                        arcGate = cellGate;
                }

                landed = nodeGrid[at];

                if (landed >= 0 && landed != entryNode && vy >= 0)
                    break;

                landed = -1;
            }

            if (blocked || landed < 0)
                continue;

            if (arcGate == NavConstants.GateNone)
                arcGate = nodes.Gates[landed];

            edges.Add(entryNode, landed, NavConstants.EdgeMovebox, 0, t, Math.Max(1, t), arcGate, -1);
        }
    }
}
