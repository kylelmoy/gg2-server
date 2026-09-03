using Gg2.Maps;
using Gg2.Protocol;

namespace Gg2.Nav;

/// <summary>
/// The five per-cell grids a nav build reasons over, row-major, <c>y * w + x</c>.
/// </summary>
/// <remarks>
/// The game uses <c>ds_grid</c>, which is <c>(x, y)</c> and column-major on disk.
/// The layout is an implementation detail either way; this one is what makes the
/// clearance passes and the arc sampler read consecutive memory.
/// </remarks>
internal readonly record struct NavGrids(byte[] Solid, byte[] Platform, byte[] Lethal, byte[] Door, byte[] Gate)
{
    /// <summary>
    /// Stage 1, <c>navBuildStart</c>'s solidity pass: the walkmask, plus the
    /// instances that are not in it.
    /// </summary>
    /// <remarks>
    /// The game scans the map's collision sprite a pixel at a time through a
    /// scale-1 <c>CollisionDummy</c>, which is exactly the walkmask the sprite
    /// was built from — so the scan is a copy, and instance stamping is the only
    /// real work.
    /// </remarks>
    public static NavGrids Build(Walkmask mask, List<NavInstance> instances)
    {
        ArgumentNullException.ThrowIfNull(mask);

        var width = mask.Width;
        var height = mask.Height;

        var grids = new NavGrids(
            mask.ToCellBytes(),
            new byte[width * height],
            new byte[width * height],
            new byte[width * height],
            new byte[width * height]);

        // NAV_DOOR_NONE and NAV_GATE_NONE are both zero, so a fresh array is
        // already the "nothing here" state. Asserted rather than assumed: a
        // renumbering in Constants.xml would otherwise leave every cell claiming
        // to be whatever code took the zero.
        if (NavConstants.DoorNone != 0 || NavConstants.GateNone != 0)
            throw new InvalidOperationException("NAV_DOOR_NONE and NAV_GATE_NONE are no longer zero");

        NavInstances.StampInto(grids, instances, width, height);
        return grids;
    }
}

/// <summary>
/// The standable surfaces of one map area — <c>navNodesExtract</c>'s output, in
/// the flat parallel arrays the cache writes.
/// </summary>
internal sealed class NavNodeSet
{
    public required int Count { get; init; }

    /// <summary>Anchor row.</summary>
    public required int[] Y { get; init; }

    /// <summary>First anchor column of the run.</summary>
    public required int[] X0 { get; init; }

    /// <summary>Last anchor column of the run.</summary>
    public required int[] X1 { get; init; }

    /// <summary>1 when the run's only support is drop-through platform.</summary>
    public required int[] Flags { get; init; }

    public required int[] Doors { get; init; }

    public required int[] Gates { get; init; }
}

/// <summary>
/// Stages 2 and 3 of the build: clearance dilation, then the standable runs.
/// </summary>
internal static class NavSurfaces
{
    /// <summary>
    /// <c>navClearanceRows</c> + <c>navClearanceCols</c>: where a character body
    /// fits, anchored at this cell.
    /// </summary>
    /// <remarks>
    /// A separable box dilation by the character box: a column is horizontally
    /// free where <see cref="NavConstants.BoxW"/> cells from it are all empty,
    /// and free where <see cref="NavConstants.BoxH"/> consecutive rows of that
    /// are.
    /// <para>
    /// ⚠️ <b><see cref="NavConstants.BoxH"/> is 6 because Heavy's manual
    /// rectangle mask is 19x36 px and six 6px rows is exactly 36.</b> It was 7 in
    /// the reference once, on the grounds that it "covered Heavy", and that
    /// over-covered him by a whole cell: measured across the 21 shipped maps it
    /// walled off 370 floor cells a player walks through without noticing.
    /// </para>
    /// </remarks>
    public static byte[] Clearance(byte[] solid, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(solid);

        var free = new byte[width * height];

        if (width < NavConstants.BoxW || height < NavConstants.BoxH)
            return free;

        const int Bw = NavConstants.BoxW;
        const int Bh = NavConstants.BoxH;

        var horizontal = new byte[width * height];
        var lastX = width - Bw;
        var lastY = height - Bh;

        for (var cy = 0; cy < height; cy++)
        {
            var row = cy * width;
            var count = 0;

            for (var cx = 0; cx < Bw; cx++)
                count += solid[row + cx];

            for (var cx = 0; cx <= lastX; cx++)
            {
                if (count == 0)
                    horizontal[row + cx] = 1;

                if (cx + Bw < width)
                    count += solid[row + cx + Bw] - solid[row + cx];
            }
        }

        // Column-major access over a row-major buffer, so the counts are carried
        // down each column with a stride rather than re-summing a window a cell.
        for (var cx = 0; cx <= lastX; cx++)
        {
            var count = 0;

            for (var cy = 0; cy < Bh; cy++)
                count += horizontal[(cy * width) + cx];

            for (var cy = 0; cy <= lastY; cy++)
            {
                if (count == Bh)
                    free[(cy * width) + cx] = 1;

                if (cy + Bh < height)
                    count += horizontal[((cy + Bh) * width) + cx] - horizontal[(cy * width) + cx];
            }
        }

        return free;
    }

    /// <summary>
    /// <c>navNodesExtract</c>: one node per maximal horizontal run of anchors a
    /// character can stand on, sorted by row then column.
    /// </summary>
    /// <remarks>
    /// An anchor is standable when the box fits and at least <em>one</em> cell of
    /// the row under the footprint supports it. "At least one" rather than "all"
    /// is deliberate: a character part-way off a ledge is still supported, and it
    /// is why a node's span runs up to <see cref="NavConstants.BoxW"/> − 1
    /// columns past the solid it stands on.
    /// <para>
    /// A run is cut wherever the <em>kind</em> of support changes (terrain versus
    /// drop-through platform, since only the platform half can be dropped
    /// through), wherever the door code changes, and wherever the gate code
    /// changes. It is cut short entirely where the support row is lethal — a node
    /// there is not somewhere a bot may stand, it is a place the graph must never
    /// offer.
    /// </para>
    /// <para>
    /// The sort order is not incidental: <see cref="NavGraphBuilder"/>'s walk
    /// pass and every jump fan index rows through it, and
    /// <c>NavGraph</c>'s own row index re-derives it on load.
    /// </para>
    /// </remarks>
    public static NavNodeSet ExtractNodes(in NavGrids grids, byte[] free, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(free);

        const int Bw = NavConstants.BoxW;
        const int Bh = NavConstants.BoxH;

        var y = new List<int>();
        var x0 = new List<int>();
        var x1 = new List<int>();
        var flags = new List<int>();
        var doors = new List<int>();
        var gates = new List<int>();

        if (width < Bw || height < Bh)
            return Pack(y, x0, x1, flags, doors, gates);

        var solid = grids.Solid;
        var platform = grids.Platform;
        var lethal = grids.Lethal;
        var door = grids.Door;
        var gate = grids.Gate;

        var lastX = width - Bw;
        var lastY = height - Bh;

        for (var cy = 0; cy <= lastY; cy++)
        {
            // The row of terrain the character's feet rest on.
            var belowY = cy + Bh;
            if (belowY >= height)
                continue;

            var below = belowY * width;
            var here = cy * width;

            // Running counts of what is under the footprint window at cx.
            var support = 0;
            var platformSupport = 0;
            var lethalCells = 0;

            for (var i = 0; i < Bw; i++)
            {
                support += solid[below + i];
                platformSupport += platform[below + i];
                lethalCells += lethal[below + i];
            }

            void Slide(int cx)
            {
                if (cx + Bw >= width)
                    return;

                support += solid[below + cx + Bw] - solid[below + cx];
                platformSupport += platform[below + cx + Bw] - platform[below + cx];
                lethalCells += lethal[below + cx + Bw] - lethal[below + cx];
            }

            var column = 0;

            while (column <= lastX)
            {
                var standable = lethalCells == 0 && (support > 0 || platformSupport > 0)
                    && free[here + column] == 1;

                if (!standable)
                {
                    Slide(column);
                    column++;
                    continue;
                }

                var runStart = column;
                var platformOnly = support == 0 && platformSupport > 0;
                var doorCode = door[here + column];
                var gateCode = gate[here + column];
                var sameDoor = true;
                var sameGate = true;

                while (column <= lastX && standable
                       && (support == 0 && platformSupport > 0) == platformOnly
                       && sameDoor && sameGate)
                {
                    Slide(column);
                    column++;
                    standable = column <= lastX && lethalCells == 0
                        && (support > 0 || platformSupport > 0)
                        && free[here + column] == 1;
                    sameDoor = column > lastX || door[here + column] == doorCode;
                    sameGate = column > lastX || gate[here + column] == gateCode;
                }

                y.Add(cy);
                x0.Add(runStart);
                x1.Add(column - 1);
                flags.Add(platformOnly ? 1 : 0);
                doors.Add(doorCode);
                gates.Add(gateCode);
            }
        }

        return Pack(y, x0, x1, flags, doors, gates);
    }

    private static NavNodeSet Pack(
        List<int> y, List<int> x0, List<int> x1, List<int> flags, List<int> doors, List<int> gates) =>
        new()
        {
            Count = y.Count,
            Y = [.. y],
            X0 = [.. x0],
            X1 = [.. x1],
            Flags = [.. flags],
            Doors = [.. doors],
            Gates = [.. gates],
        };

    /// <summary>
    /// <c>navNodeGrid</c>: which surface occupies this cell, or -1.
    /// </summary>
    /// <remarks>
    /// Filled one node at a time rather than one cell at a time, so the cost is
    /// the node count (hundreds) and not the mask (up to 560,000).
    /// </remarks>
    public static int[] NodeGrid(NavNodeSet nodes, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        var grid = new int[width * height];
        Array.Fill(grid, -1);

        for (var i = 0; i < nodes.Count; i++)
        {
            var start = (nodes.Y[i] * width) + nodes.X0[i];
            Array.Fill(grid, i, start, nodes.X1[i] - nodes.X0[i] + 1);
        }

        return grid;
    }

    /// <summary>
    /// <c>navRowIndex</c>: the first node on each mask row, or -1.
    /// </summary>
    /// <remarks>
    /// Nodes are emitted sorted by row then column, so one row's nodes are
    /// contiguous and a caller walks forward from here until the row changes.
    /// Filled backwards so the lowest index on each row survives — the same
    /// direction <c>NavGraph</c>'s loader uses, and the tie-breaks downstream
    /// are strict comparisons that resolve the two orders differently.
    /// </remarks>
    public static int[] RowIndex(NavNodeSet nodes, int height)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        var rowStart = new int[height + 2];
        Array.Fill(rowStart, -1);

        for (var i = nodes.Count - 1; i >= 0; i--)
            rowStart[nodes.Y[i]] = i;

        return rowStart;
    }
}
