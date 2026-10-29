using System.Globalization;
using System.Text;
using GG2.Bots;
using GG2.Maps;
using GG2.Nav;

namespace GG2.Host;

/// <summary>What one cell map is made of. Sizes are in mask cells (6 world px).</summary>
/// <param name="Seed">Drives every random choice, so a map is a function of its options.</param>
/// <param name="Cells">How many duel cells. Cell <c>i</c> holds red spawn <c>i</c> and blue spawn <c>i</c>.</param>
/// <param name="CellWidth">The window cut from a source map, in mask cells.</param>
/// <param name="CellHeight">The window cut from a source map, in mask cells.</param>
/// <param name="Gap">
/// Solid between cells and round the edge. It has to beat the 65 px blast radius
/// (<c>Explosions.BlastRadiusFor</c>), which ignores walls, by a margin; past that
/// it decides how much of the next cell is on screen, because the client draws
/// every character whatever is in between.
/// </param>
internal readonly record struct CellMapOptions(int Seed, int Cells = 24, int CellWidth = 100, int CellHeight = 67, int Gap = 70);

/// <summary>One shipped map, decoded once, that cells are cut from.</summary>
internal sealed class CellSource
{
    private CellSource(string name, int width, int height, bool[] solid, int[] rgb, IReadOnlyList<LevelEntity> entities)
    {
        Name = name;
        Width = width;
        Height = height;
        Solid = solid;
        Rgb = rgb;
        Entities = entities;
    }

    public string Name { get; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>The walkmask, row-major.</summary>
    public bool[] Solid { get; }

    /// <summary>The art, one pixel per mask cell, row-major.</summary>
    public int[] Rgb { get; }

    public IReadOnlyList<LevelEntity> Entities { get; }

    /// <remarks>
    /// Art and mask are the same size on every shipped map but <c>gg_debug</c>,
    /// which is not a source; the overlap is used regardless.
    /// </remarks>
    public static CellSource Load(string name, byte[] png)
    {
        var map = LevelMap.FromPng(name, png);
        var art = MapPng.DecodeRgb(png);

        var width = Math.Min(map.Walkmask.Width, art.Width);
        var height = Math.Min(map.Walkmask.Height, art.Height);
        var solid = new bool[width * height];
        var rgb = new int[width * height];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                solid[(y * width) + x] = map.Walkmask.IsSolid(x, y);
                rgb[(y * width) + x] = art.Rgb[(y * art.Width) + x];
            }
        }

        return new CellSource(name, width, height, solid, rgb, map.Entities);
    }
}

/// <summary>A cell as placed in the finished map.</summary>
/// <param name="Source">The shipped map it was cut from.</param>
/// <param name="SourceX">The window's left edge in the source, in mask cells.</param>
/// <param name="SourceY">The window's top edge in the source, in mask cells.</param>
/// <param name="MaskX">The window's left edge in the cell map, in mask cells.</param>
/// <param name="MaskY">The window's top edge in the cell map, in mask cells.</param>
/// <param name="Red">The red spawn, in cell-map world pixels.</param>
/// <param name="Blue">The blue spawn, in cell-map world pixels.</param>
/// <param name="OpenCells">How much of the window is playable.</param>
internal readonly record struct PlacedCell(
    string Source,
    int SourceX,
    int SourceY,
    int MaskX,
    int MaskY,
    (int X, int Y) Red,
    (int X, int Y) Blue,
    int OpenCells);

internal sealed record CellMap(int Width, int Height, string LevelData, int[] Rgb, IReadOnlyList<PlacedCell> Cells)
{
    public byte[] ToPng() => MapPng.Encode(Width, Height, Rgb, LevelData);
}

/// <summary>
/// Builds a duel map: many small arenas, each a flood-filled cut of a shipped
/// map, packed apart with solid between them. <c>docs/DUEL.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>A cell is the open space a floor reaches, not the window.</b> The window
/// only bounds a 4-connected flood fill from one floor cell, and everything the
/// fill does not reach is made solid, so a cell's shape is the source map's own
/// geometry and is never square unless the source is.
/// </para>
/// <para>
/// <b>Every cell is proven playable by the nav generator, not by a heuristic.</b>
/// The cell is built as a map of its own; over edges every class can take (walk,
/// fall, jump, drop-through) its two spawns must reach each other, everywhere the
/// first can reach must lead back to it, and that has to be at least
/// <see cref="MinSurfaces"/> surfaces.
/// The cell is validated inside a solid border so that its graph is the one it
/// has in the finished map, shifted; <c>CellMapTests</c> re-checks the same
/// properties on the shipped graphs.
/// </para>
/// <para>
/// <b>Entities:</b> spawns are the generator's. Dropdown platforms and the
/// invisible player and bullet walls are kept when they lie wholly inside the
/// window, because the art draws them or the geometry depends on them; everything
/// else (gates, objectives, spawn rooms, cabinets, doors, move boxes) is dropped,
/// and a window touching a <c>pitfall</c> is refused rather than cut, because its
/// art promises a death the map would no longer deliver.
/// </para>
/// </remarks>
internal static class CellMapGenerator
{
    /// <summary>The colour of everything solid that is not a source map's own art.</summary>
    public const int WallColour = 0x28282F;

    /// <summary>
    /// A spawn's height above the floor it drops onto. The commonest offset among
    /// the shipped maps' 227 spawn points (they range 26-66).
    /// </summary>
    public const int SpawnHeight = 42;

    private const int Cell = GG2.Protocol.GameConstants.CellSize;

    /// <summary>Art kept round a cell's open space, so its walls are the source's.</summary>
    private const int Rim = 2;

    private const int AttemptsPerCell = 5000;

    /// <summary>
    /// The fewest nav surfaces a cell's spawns may reach. Below it a cell is a
    /// box with a floor — measured: the first pool had cells of three and four.
    /// </summary>
    private const int MinSurfaces = 8;

    /// <summary>
    /// The maps cells are cut from: every built-in map FILE but <c>gg_debug</c>,
    /// whose art is four-bit and which is a test room rather than a place. By
    /// file, not by name — <c>InternalMaps</c> has aliases, and three names for
    /// <c>ctf_2dfort.png</c> would draw it three times as often.
    /// </summary>
    public static IReadOnlyList<string> SourceNames { get; } =
        InternalMaps.Names
            .Where(name => InternalMaps.FileFor(name) == name + ".png" && name != "gg_debug")
            .Order(StringComparer.Ordinal)
            .ToArray();

    /// <summary>The checked-in pool under <c>maps/duel/</c>, name to options.</summary>
    public static IReadOnlyList<(string Name, CellMapOptions Options)> Pool { get; } =
        Enumerable.Range(1, 8).Select(i => ($"duel_{i:D2}", new CellMapOptions(Seed: i))).ToArray();

    public static IReadOnlyList<CellSource> LoadSources(string mapDirectory) =>
        SourceNames
            .Select(name => CellSource.Load(name, File.ReadAllBytes(Path.Combine(mapDirectory, InternalMaps.FileFor(name)!))))
            .ToArray();

    public static CellMap Generate(IReadOnlyList<CellSource> sources, CellMapOptions options)
    {
        if (options.Cells < 1 || options.CellWidth < 20 || options.CellHeight < 20 || options.Gap < 12)
            throw new ArgumentException($"cell map options out of range: {options}");

        var random = new Random(options.Seed);
        var (cw, ch, gap) = (options.CellWidth, options.CellHeight, options.Gap);

        // Wider than tall, like every shipped map: about 2:1 overall.
        var columns = Math.Max(1, (int)Math.Round(Math.Sqrt(options.Cells * 2.0 * (ch + gap) / (cw + gap))));
        var rows = (options.Cells + columns - 1) / columns;
        var width = gap + (columns * (cw + gap));
        var height = gap + (rows * (ch + gap));

        var solid = new bool[width * height];
        Array.Fill(solid, true);
        var rgb = new int[width * height];
        Array.Fill(rgb, WallColour);

        var spawns = new StringBuilder();
        var extras = new List<string>();
        var placed = new List<PlacedCell>();
        var used = new HashSet<(string, int, int)>();

        for (var i = 0; i < options.Cells; i++)
        {
            var cut = CutCell(random, sources, cw, ch, used)
                ?? throw new InvalidOperationException(
                    $"no playable {cw}x{ch} cell found in {AttemptsPerCell} tries (cell {i}, seed {options.Seed})");

            var maskX = gap + ((i % columns) * (cw + gap));
            var maskY = gap + ((i / columns) * (ch + gap));
            var source = cut.Source;
            var rim = Dilate(cut.Open, cw, ch, Rim);

            for (var y = 0; y < ch; y++)
            {
                for (var x = 0; x < cw; x++)
                {
                    var at = ((maskY + y) * width) + maskX + x;
                    solid[at] = !cut.Open[(y * cw) + x];
                    if (rim[(y * cw) + x])
                        rgb[at] = source.Rgb[((cut.SourceY + y) * source.Width) + cut.SourceX + x];
                }
            }

            var dx = maskX * Cell;
            var dy = maskY * Cell;
            var red = (cut.Red.X + dx, cut.Red.Y + dy);
            var blue = (cut.Blue.X + dx, cut.Blue.Y + dy);

            spawns.Append(CultureInfo.InvariantCulture, $",{{type:redspawn,x:{red.Item1},y:{red.Item2}}}");
            spawns.Append(CultureInfo.InvariantCulture, $",{{type:bluespawn,x:{blue.Item1},y:{blue.Item2}}}");

            foreach (var entity in cut.Kept)
                extras.Add(Ggon(entity, entity.X + dx, entity.Y + dy));

            placed.Add(new PlacedCell(source.Name, cut.SourceX, cut.SourceY, maskX, maskY, red, blue, cut.OpenCells));
        }

        var background = WallColour.ToString("x6", CultureInfo.InvariantCulture);
        var entityBlock = $"[{{background:{background},type:meta}}{spawns}{string.Concat(extras.Select(e => "," + e))}]";

        return new CellMap(width, height, LevelText(entityBlock, solid, width, height), rgb, placed);
    }

    private sealed record CellCut(
        CellSource Source,
        int SourceX,
        int SourceY,
        bool[] Open,
        int OpenCells,
        (int X, int Y) Red,
        (int X, int Y) Blue,
        IReadOnlyList<LevelEntity> Kept);

    private static CellCut? CutCell(Random random, IReadOnlyList<CellSource> sources, int cw, int ch, HashSet<(string, int, int)> used)
    {
        for (var attempt = 0; attempt < AttemptsPerCell; attempt++)
        {
            var source = sources[random.Next(sources.Count)];
            if (source.Width < cw + 2 || source.Height < ch + 2)
                continue;

            var x0 = random.Next(1, source.Width - cw - 1);
            var y0 = random.Next(1, source.Height - ch - 1);

            // Two windows from the same place would be the same duel twice.
            if (!used.Add((source.Name, x0 / 10, y0 / 10)))
                continue;

            if (TouchesHazard(source, x0, y0, cw, ch))
                continue;

            var floors = new List<int>();
            for (var y = 0; y < ch - 1; y++)
            {
                for (var x = 0; x < cw; x++)
                {
                    if (!SolidAt(source, x0 + x, y0 + y) && SolidAt(source, x0 + x, y0 + y + 1))
                        floors.Add((y * cw) + x);
                }
            }

            if (floors.Count == 0)
                continue;

            var open = Fill(source, x0, y0, cw, ch, floors[random.Next(floors.Count)]);
            var openCells = open.Count(o => o);

            // Too little is a corridor; too much is open sky with nothing to fight round.
            if (openCells < cw * ch * 3 / 10 || openCells > cw * ch * 8 / 10)
                continue;

            var left = new List<(int X, int Row)>();
            var right = new List<(int X, int Row)>();
            for (var row = 10; row < ch; row++)
            {
                for (var x = 4; x < cw - 4; x++)
                {
                    if (!CanStand(open, cw, x, row))
                        continue;

                    if (x < cw * 2 / 5)
                        left.Add((x, row));
                    else if (x >= cw * 3 / 5)
                        right.Add((x, row));
                }
            }

            if (left.Count == 0 || right.Count == 0)
                continue;

            var kept = KeptEntities(source, x0, y0, cw, ch);

            for (var pair = 0; pair < 8; pair++)
            {
                var a = left[random.Next(left.Count)];
                var b = right[random.Next(right.Count)];

                // Spawn x on the 6 px grid like the shipped maps', dropping onto the floor row.
                var first = (a.X * Cell, (a.Row * Cell) - SpawnHeight);
                var second = (b.X * Cell, (b.Row * Cell) - SpawnHeight);

                if (!Connected(open, cw, ch, kept, first, a.Row, second, b.Row))
                    continue;

                var redFirst = random.Next(2) == 0;
                return new CellCut(
                    source, x0, y0, open, openCells,
                    redFirst ? first : second,
                    redFirst ? second : first,
                    kept);
            }
        }

        return null;
    }

    private static bool SolidAt(CellSource source, int x, int y) => source.Solid[(y * source.Width) + x];

    private static bool TouchesHazard(CellSource source, int x0, int y0, int cw, int ch)
    {
        // A pitfall's collider is sprite64, 42 px square at scale 1 (NavInstances.Box).
        var left = (x0 - 1) * Cell;
        var top = (y0 - 1) * Cell;
        var right = (x0 + cw + 1) * Cell;
        var bottom = (y0 + ch + 1) * Cell;

        foreach (var entity in source.Entities)
        {
            if (entity.Type is not (EntityTypes.PitFall or EntityTypes.KillBox or EntityTypes.FragBox))
                continue;

            if (entity.X < right && entity.X + (42 * entity.XScale) > left
                && entity.Y < bottom && entity.Y + (42 * entity.YScale) > top)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The open cells a 4-connected flood from <paramref name="seed"/> reaches inside the window.</summary>
    private static bool[] Fill(CellSource source, int x0, int y0, int cw, int ch, int seed)
    {
        var open = new bool[cw * ch];
        var queue = new Queue<int>();
        open[seed] = true;
        queue.Enqueue(seed);

        while (queue.TryDequeue(out var at))
        {
            var (y, x) = Math.DivRem(at, cw);
            Visit(x + 1, y);
            Visit(x - 1, y);
            Visit(x, y + 1);
            Visit(x, y - 1);
        }

        return open;

        void Visit(int x, int y)
        {
            if (x < 0 || y < 0 || x >= cw || y >= ch || open[(y * cw) + x] || SolidAt(source, x0 + x, y0 + y))
                return;

            open[(y * cw) + x] = true;
            queue.Enqueue((y * cw) + x);
        }
    }

    /// <summary>
    /// Whether a character can arrive standing at column <paramref name="x"/> on
    /// floor row <paramref name="row"/>: open for nine rows above (the drop from
    /// <see cref="SpawnHeight"/> plus a body) six columns wide, and floor under
    /// the two columns at its centre.
    /// </summary>
    private static bool CanStand(bool[] open, int cw, int x, int row)
    {
        if (open[(row * cw) + x] || open[(row * cw) + x - 1])
            return false;

        for (var y = row - 9; y < row; y++)
        {
            for (var c = x - 3; c <= x + 2; c++)
            {
                if (!open[(y * cw) + c])
                    return false;
            }
        }

        return true;
    }

    private static IReadOnlyList<LevelEntity> KeptEntities(CellSource source, int x0, int y0, int cw, int ch)
    {
        var kept = new List<LevelEntity>();

        foreach (var entity in source.Entities)
        {
            // NavInstances' bar sizes: sprite45 is 6x60 (vertical), sprite44 60x6.
            (int W, int H)? size = entity.Type switch
            {
                EntityTypes.DropdownPlatform or EntityTypes.PlayerWallHorizontal or EntityTypes.BulletWallHorizontal => (60, 6),
                EntityTypes.PlayerWall or EntityTypes.BulletWall => (6, 60),
                _ => null,
            };

            if (size is not { } s)
                continue;

            var left = x0 * Cell;
            var top = y0 * Cell;
            if (entity.X < left || entity.Y < top
                || entity.X + (s.W * entity.XScale) > (x0 + cw) * Cell
                || entity.Y + (s.H * entity.YScale) > (y0 + ch) * Cell)
            {
                continue;
            }

            kept.Add(new LevelEntity
            {
                Type = entity.Type,
                X = entity.X - left,
                Y = entity.Y - top,
                XScale = entity.XScale,
                YScale = entity.YScale,
                Properties = new Dictionary<string, GgonValue>(),
            });
        }

        return kept;
    }

    /// <summary>
    /// Whether the two stand points reach each other both ways, on the nav graph
    /// of the cell built as a map of its own.
    /// </summary>
    private static bool Connected(
        bool[] open, int cw, int ch, IReadOnlyList<LevelEntity> kept,
        (int X, int Y) a, int aRow, (int X, int Y) b, int bRow)
    {
        // ⚠️ Inside a solid border, as the cell sits in the packed map. Off the
        // edge of a mask is OPEN (Walkmask.IsSolid), so a bare window lets arcs
        // leave through its edge and come back — routes, and ways out of pits,
        // that the walled-in cell does not have. Measured: without the border 13
        // of 192 cells passed here and had a one-way pocket in the shipped graph.
        const int Border = 4;
        var w = cw + (2 * Border);
        var h = ch + (2 * Border);
        var solid = new bool[w * h];
        Array.Fill(solid, true);
        for (var y = 0; y < ch; y++)
        {
            for (var x = 0; x < cw; x++)
                solid[((y + Border) * w) + x + Border] = !open[(y * cw) + x];
        }

        var shift = Border * Cell;
        var entities = $"[{{type:redspawn,x:{a.X + shift},y:{a.Y + shift}}},{{type:bluespawn,x:{b.X + shift},y:{b.Y + shift}}}"
            + string.Concat(kept.Select(e => "," + Ggon(e, e.X + shift, e.Y + shift))) + "]";

        var map = LevelMap.FromLevelData("cell", LevelText(entities, solid, w, h));
        var graph = NavCache.Parse(NavGenerator.Build(map, 1).Text, "cell", 1);

        var from = graph.NodeFromWorld(a.X + shift, ((aRow + Border) * Cell) - NavGraph.FeetOffset);
        var to = graph.NodeFromWorld(b.X + shift, ((bRow + Border) * Cell) - NavGraph.FeetOffset);

        if (from < 0 || to < 0)
            return false;

        // Everywhere the first spawn can get to, it must also be able to get back
        // from: a pit with no way out strands whoever falls in for the rest of
        // their life. With the second spawn inside that set, this is also "the
        // two reach each other both ways".
        var forward = Flood(graph, from, reverse: false);
        if (!forward[to] || forward.Count(f => f) < MinSurfaces)
            return false;

        var back = Flood(graph, from, reverse: true);
        for (var node = 0; node < forward.Length; node++)
        {
            if (forward[node] && !back[node])
                return false;
        }

        return true;
    }

    private static bool Universal(NavEdgeType type) =>
        type is NavEdgeType.Walk or NavEdgeType.Fall or NavEdgeType.Jump or NavEdgeType.DropThrough;

    /// <summary>
    /// The nodes reachable from <paramref name="start"/> over <see cref="Universal"/>
    /// edges, or with <paramref name="reverse"/> the nodes that can reach it.
    /// </summary>
    private static bool[] Flood(NavGraph graph, int start, bool reverse)
    {
        List<int>[]? into = null;
        if (reverse)
        {
            into = new List<int>[graph.NodeCount];
            for (var node = 0; node < into.Length; node++)
                into[node] = [];

            for (var node = 0; node < graph.NodeCount; node++)
            {
                foreach (var edge in graph.EdgesFrom(node))
                {
                    if (Universal(edge.Type))
                        into[edge.To].Add(node);
                }
            }
        }

        var seen = new bool[graph.NodeCount];
        var queue = new Queue<int>();
        seen[start] = true;
        queue.Enqueue(start);

        while (queue.TryDequeue(out var node))
        {
            if (into is not null)
            {
                foreach (var next in into[node])
                    Visit(next);
            }
            else
            {
                foreach (var edge in graph.EdgesFrom(node))
                {
                    if (Universal(edge.Type))
                        Visit(edge.To);
                }
            }
        }

        return seen;

        void Visit(int node)
        {
            if (!seen[node])
            {
                seen[node] = true;
                queue.Enqueue(node);
            }
        }
    }

    private static bool[] Dilate(bool[] cells, int w, int h, int times)
    {
        var current = cells;
        for (var t = 0; t < times; t++)
        {
            var next = (bool[])current.Clone();
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    if (current[(y * w) + x])
                        continue;

                    next[(y * w) + x] =
                        (x > 0 && current[(y * w) + x - 1]) || (x < w - 1 && current[(y * w) + x + 1])
                        || (y > 0 && current[((y - 1) * w) + x]) || (y < h - 1 && current[((y + 1) * w) + x]);
                }
            }

            current = next;
        }

        return current;
    }

    private static string Ggon(LevelEntity entity, double x, double y)
    {
        var text = new StringBuilder();
        text.Append("{type:").Append(entity.Type);
        text.Append(",x:").Append(Number(x)).Append(",y:").Append(Number(y));
        if (entity.XScale != 1)
            text.Append(",xscale:").Append(Number(entity.XScale));
        if (entity.YScale != 1)
            text.Append(",yscale:").Append(Number(entity.YScale));
        return text.Append('}').ToString();
    }

    private static string Number(double value) => value.ToString("0.########", CultureInfo.InvariantCulture);

    /// <summary>
    /// The level data: GGON entities, then <c>compressWalkmask.gml</c>'s encoding —
    /// six cells per character, <c>chr(bits + 32)</c>, row-major from the top.
    /// </summary>
    private static string LevelText(string entityBlock, bool[] solid, int width, int height)
    {
        var mask = new StringBuilder((solid.Length + 5) / 6);
        var accumulator = 0;
        var bits = 0;

        foreach (var cell in solid)
        {
            accumulator = (accumulator << 1) | (cell ? 1 : 0);
            if (++bits == 6)
            {
                mask.Append((char)(accumulator + 32));
                accumulator = 0;
                bits = 0;
            }
        }

        if (bits > 0)
            mask.Append((char)((accumulator << (6 - bits)) + 32));

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{{ENTITIES}}\n{entityBlock}\n{{END ENTITIES}}\n{{WALKMASK}}\n{width}\n{height}\n{mask}\n{{END WALKMASK}}\n");
    }
}
