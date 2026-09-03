using Gg2.Protocol;

namespace Gg2.Nav;

/// <summary>
/// <c>navEdgesBegin</c>/<c>navEdgeAdd</c>: one growable buffer shared by every
/// edge pass, because edges arrive from up to eight of them.
/// </summary>
internal sealed class NavEdgeBuffer
{
    private double[] _data = new double[NavConstants.EdgeFields * 4096];

    public int Count { get; private set; }

    public ReadOnlySpan<double> Data => _data.AsSpan(0, Count * NavConstants.EdgeFields);

    /// <summary>The from-node of edge <paramref name="index"/>, for the adjacency snapshot.</summary>
    public int From(int index) => (int)_data[(index * NavConstants.EdgeFields) + NavConstants.EdgeFrom];

    /// <summary>The to-node of edge <paramref name="index"/>.</summary>
    public int To(int index) => (int)_data[(index * NavConstants.EdgeFields) + NavConstants.EdgeTo];

    /// <summary>
    /// Appends one edge.
    /// </summary>
    /// <param name="rejump">
    /// The tick a double-jump arc presses JUMP again, and -1 on every other edge
    /// kind — which is the value every follower's second-impulse branch is inert
    /// on. It defaults so that a pass which does not have one writes that -1
    /// rather than a 0 the follower would act on.
    /// </param>
    public void Add(
        int from, int to, int type, double bucket, double ticks, double cost,
        int gate, int takeoff, int rejump = -1)
    {
        if ((Count + 1) * NavConstants.EdgeFields > _data.Length)
            Array.Resize(ref _data, _data.Length * 2);

        var at = Count * NavConstants.EdgeFields;

        _data[at + NavConstants.EdgeFrom] = from;
        _data[at + NavConstants.EdgeTo] = to;
        _data[at + NavConstants.EdgeType] = type;
        _data[at + NavConstants.EdgeBucket] = bucket;
        _data[at + NavConstants.EdgeTicks] = ticks;
        _data[at + NavConstants.EdgeCost] = cost;
        _data[at + NavConstants.EdgeGate] = gate;
        _data[at + NavConstants.EdgeTakeoff] = takeoff;
        _data[at + NavConstants.EdgeRejump] = rejump;

        Count++;
    }

    /// <summary>
    /// <c>navEdgesSortByFrom</c>: a counting sort grouping edges by from-node.
    /// </summary>
    /// <remarks>
    /// A counting sort because the reference has no <c>ds_grid_sort</c> at all and
    /// the key is already a small dense integer. Grouping is what lets an adjacency
    /// index describe a node's neighbours as a contiguous range, which on
    /// <c>cp_dirtbowl</c> is the difference between A* reading 1,298 edges per
    /// expansion and reading two — <c>NavGraph</c>'s edge index depends on it.
    /// <para>
    /// Edges naming a node outside the graph are dropped rather than carried: they
    /// could only come from a corrupt build and would index off the end in a
    /// search.
    /// </para>
    /// </remarks>
    /// <returns>
    /// The sorted payload and how many rows of it mean anything. ⚠️ <b>The grid is
    /// as tall as the accumulator was and the count can be smaller</b> — the
    /// reference sizes and counts separately, the cache header carries the count,
    /// and <c>ds_grid_write</c> carries the height.
    /// </returns>
    public (double[] Data, int Rows, int Count) SortByFrom(int nodeCount)
    {
        const int Fields = NavConstants.EdgeFields;

        var rows = Math.Max(Count, 1);
        var sorted = new double[rows * Fields];

        if (Count <= 0 || nodeCount <= 0)
            return (sorted, rows, 0);

        var counts = new int[nodeCount + 1];

        for (var i = 0; i < Count; i++)
        {
            var from = From(i);

            if (from >= 0 && from < nodeCount)
                counts[from]++;
        }

        var cursor = new int[nodeCount + 1];
        var written = 0;

        for (var i = 0; i < nodeCount; i++)
        {
            cursor[i] = written;
            written += counts[i];
        }

        for (var i = 0; i < Count; i++)
        {
            var from = From(i);

            if (from < 0 || from >= nodeCount)
                continue;

            var at = i * Fields;
            _data.AsSpan(at, Fields).CopyTo(sorted.AsSpan(cursor[from]++ * Fields, Fields));
        }

        return (sorted, rows, written);
    }
}
