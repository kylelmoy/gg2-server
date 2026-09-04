using System.Globalization;
using Gg2.Maps;
using Gg2.Protocol;

namespace Gg2.Nav;

/// <summary>A built graph, before it becomes a file.</summary>
internal sealed class NavGraphData
{
    public required string Map { get; init; }

    public required int Area { get; init; }

    public required int MaskWidth { get; init; }

    public required int MaskHeight { get; init; }

    public required NavNodeSet Nodes { get; init; }

    /// <summary>The sorted edge payload, <c>NAV_EDGE_FIELDS</c> doubles a row.</summary>
    public required double[] Edges { get; init; }

    /// <summary>How tall the grid is. ⚠️ Not the same as <see cref="EdgeCount"/>.</summary>
    public required int EdgeRows { get; init; }

    /// <summary>How many rows of <see cref="Edges"/> mean anything.</summary>
    public required int EdgeCount { get; init; }
}

/// <summary>
/// What one build produced, for a caller that wants to report on it.
/// </summary>
/// <param name="Key">The cache key — <c>&lt;map&gt;_a&lt;area&gt;</c>.</param>
/// <param name="Text">The cache file's contents.</param>
/// <param name="Nodes">How many surfaces the map has.</param>
/// <param name="Edges">How many traversals between them.</param>
/// <param name="EdgesByType">
/// The edge count per <c>NAV_EDGE_*</c> type, indexed by the type code.
/// </param>
/// <param name="Elapsed">How long the build took.</param>
public readonly record struct NavBuildResult(
    string Key,
    string Text,
    int Nodes,
    int Edges,
    int[] EdgesByType,
    TimeSpan Elapsed)
{
    /// <summary>The counts as "walk 812, jump 190, …", commonest first.</summary>
    public string Describe()
    {
        var names = new[] { "walk", "fall", "jump", "drop", "movebox", "doublejump", "rocketjump", "stickyjump" };

        return string.Join(", ", EdgesByType
            .Select((count, type) => (Name: type < names.Length ? names[type] : $"type{type}", Count: count))
            .Where(entry => entry.Count > 0)
            .OrderByDescending(entry => entry.Count)
            .Select(entry => string.Create(CultureInfo.InvariantCulture, $"{entry.Name} {entry.Count}")));
    }
}

/// <summary>
/// Builds a bot nav graph for one map area, with no game and no reference
/// checkout.
/// </summary>
/// <remarks>
/// Everything a graph needs is in the map's own PNG — the walkmask and the entity
/// list are deflated into a zTXt chunk, and that is the same input the reference's
/// <c>CustomMapInit</c> reads. So the server can build one for a map nobody has
/// ever generated a graph for, which is what makes custom maps navigable.
/// <para>
/// ⚠️ <b>The product is the cache file's TEXT, not a <c>NavGraph</c>.</b> The
/// caller turns it into one with <c>NavCache.Parse</c> — the same call it makes
/// for a file off disk — so an on-the-fly graph and a generated file are the same
/// graph by construction rather than by two code paths agreeing. It also keeps
/// this project free of a reference to the reader.
/// </para>
/// </remarks>
public static class NavGenerator
{
    /// <summary>
    /// Builds the graph for <paramref name="map"/>'s <paramref name="area"/>.
    /// </summary>
    /// <param name="map">
    /// ⚠️ <b>The map ALREADY NARROWED to its area</b> — <c>LevelMap.ForArea(n)</c>
    /// — which is <c>NavManifest.HashOf</c>'s contract too, deliberately: the graph
    /// and the fingerprint recorded for it have to be built from the same
    /// entities, and one call taking a sliced map while the other took a whole one
    /// is how they would quietly stop being.
    /// <para>
    /// ⛔ <b>Narrowing is NOT idempotent, which is why this cannot just do it
    /// itself for safety.</b> <c>ForArea</c> keeps a marker's own <c>y</c> on both
    /// sides of it, so slicing <c>cp_dirtbowl</c> to area 3 and then slicing that
    /// result to area 3 again finds only one marker left, reads the area as out of
    /// range, falls back to area 1, and keeps the handful of entities sitting
    /// exactly on the boundary. The graph builds, the counts look plausible, and it
    /// describes nothing that is there.
    /// </para>
    /// </param>
    /// <param name="area">
    /// Which stage <paramref name="map"/> was narrowed to. Used for the cache key;
    /// it does not narrow anything.
    /// </param>
    /// <param name="sticky">
    /// ⚠️ <b>Emit <c>NAV_EDGE_STICKYJUMP</c> (type 7) edges as well.</b> Off by
    /// default, and not as a tuning default: the GML game's search cannot refuse
    /// them by class, so a cache carrying them must never reach it. This port's
    /// <c>NavGraph.EdgeRoutable</c> can and does — see
    /// <see cref="NavGraphBuilder.StickyJumpEdges"/>.
    /// </param>
    public static NavBuildResult Build(LevelMap map, int area, bool sticky = false)
    {
        ArgumentNullException.ThrowIfNull(map);

        var started = System.Diagnostics.Stopwatch.GetTimestamp();

        var width = map.Walkmask.Width;
        var height = map.Walkmask.Height;

        var instances = NavInstances.Instantiate(map.Entities);
        var grids = NavGrids.Build(map.Walkmask, instances);
        var free = NavSurfaces.Clearance(grids.Solid, width, height);

        var nodes = NavSurfaces.ExtractNodes(grids, free, width, height);
        var nodeGrid = NavSurfaces.NodeGrid(nodes, width, height);
        var rowStart = NavSurfaces.RowIndex(nodes, height);

        var edges = new NavEdgeBuffer();

        NavGraphBuilder.WalkEdges(edges, nodes, rowStart, height);
        NavGraphBuilder.FallEdges(edges, nodes, free, nodeGrid, grids.Gate, width, height);
        NavGraphBuilder.DropEdges(edges, nodes, free, nodeGrid, grids.Gate, width, height);

        NavGraphBuilder.JumpEdges(
            edges, nodes, new NavJumpModel(free, width, height), nodeGrid, grids.Gate, rowStart, width, height);

        // ⚠️ The adjacency of everything the stages above emitted, snapshotted
        // BEFORE any of the three later fans runs — and deliberately not refreshed
        // between them. Two reasons, and the second is the one that would bite. A
        // fan that could see its own output would let the first edge it keeps
        // suppress the second. And a Scout-only edge must not be allowed to tell a
        // Soldier's fan "that pair is already joined", since a Soldier cannot fly
        // one.
        var adjacency = new List<int>?[nodes.Count];

        for (var e = 0; e < edges.Count; e++)
        {
            var from = edges.From(e);
            (adjacency[from] ??= []).Add(edges.To(e));
        }

        NavGraphBuilder.DoubleJumpEdges(
            edges, nodes, new NavDoubleJumpModel(free, width, height), nodeGrid, grids.Gate,
            rowStart, adjacency, width, height);

        NavGraphBuilder.RocketJumpEdges(
            edges, nodes, NavRocketJumpModel.ForRocket(free, width, height), nodeGrid, grids.Gate,
            rowStart, adjacency, width, height);

        if (sticky)
        {
            NavGraphBuilder.StickyJumpEdges(
                edges, nodes, NavRocketJumpModel.ForSticky(free, width, height), nodeGrid, grids.Gate,
                rowStart, adjacency, width, height);
        }

        NavGraphBuilder.MoveBoxEdges(edges, nodes, instances, free, nodeGrid, grids.Gate, width, height);

        var (sorted, rows, count) = edges.SortByFrom(nodes.Count);

        // ⚠️ Counted AFTER the sort and over `count`, not over the accumulator.
        // The sort drops an edge naming a node outside the graph, so counting
        // first makes the breakdown sum to more than the total printed beside it
        // — a reporting number that disagrees with the number it breaks down is
        // worse than no breakdown.
        var byType = new int[NavConstants.EdgeStickyjump + 1];

        for (var e = 0; e < count; e++)
        {
            var type = (int)sorted[(e * NavConstants.EdgeFields) + NavConstants.EdgeType];

            if ((uint)type < (uint)byType.Length)
                byType[type]++;
        }

        var text = NavCacheWriter.Serialise(new NavGraphData
        {
            Map = map.Name,
            Area = area,
            MaskWidth = width,
            MaskHeight = height,
            Nodes = nodes,
            Edges = sorted,
            EdgeRows = rows,
            EdgeCount = count,
        });

        return new NavBuildResult(
            Key(map.Name, area),
            text,
            nodes.Count,
            count,
            byType,
            System.Diagnostics.Stopwatch.GetElapsedTime(started));
    }

    /// <summary>
    /// How many stages a map has — one more than its <c>NextAreaO</c> markers.
    /// </summary>
    /// <remarks>
    /// A tall map is several rounds stacked vertically, and <c>basicRoomSetup</c>
    /// destroys everything outside the active stage's y-band, so each stage is
    /// genuinely a different graph. <c>cp_dirtbowl</c> is the only shipped map with
    /// more than one.
    /// <para>
    /// ⚠️ <b>The WHOLE map, unlike <see cref="Build"/></b> — a narrowed one has
    /// lost the markers that fell outside its own band and would under-count.
    /// </para>
    /// </remarks>
    public static int AreaCount(LevelMap map)
    {
        ArgumentNullException.ThrowIfNull(map);

        var markers = 0;

        foreach (var entity in map.Entities)
        {
            if (string.Equals(entity.Type, EntityTypes.NextArea, StringComparison.Ordinal))
                markers++;
        }

        return markers + 1;
    }

    /// <summary>
    /// <c>navCacheKey</c>: the filename stem for a map and area.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Kept identical to <c>NavCache.Key</c> on the reading side</b>, and
    /// restated here rather than shared because this project does not reference the
    /// reader. <c>NavGeneratorTests</c> asserts the two agree on every shipped map,
    /// which is the check that matters: a generator writing under a key the loader
    /// does not look for produces a directory full of files and a server with no
    /// bot navigation.
    /// </remarks>
    public static string Key(string map, int area)
    {
        ArgumentNullException.ThrowIfNull(map);

        var safe = string.Create(map.Length, map, static (span, name) =>
        {
            for (var i = 0; i < name.Length; i++)
            {
                var c = name[i];
                span[i] = c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-'
                    ? c
                    : '_';
            }
        });

        return $"{safe}_a{area.ToString(CultureInfo.InvariantCulture)}";
    }
}
