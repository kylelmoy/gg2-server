using GG2.Bots;
using GG2.Host;
using GG2.Maps;
using GG2.Nav;
using GG2.Protocol;
using GG2.Simulation;
using Xunit;

namespace GG2.Tests;

/// <summary>
/// The duel map pool under <c>maps/duel/</c> and <c>nav/duel/</c>, and the
/// properties that make one a set of separate duels. <c>docs/DUEL.md</c>.
/// </summary>
/// <remarks>
/// <b>None of this shows a stock client can load the maps or that a fight in one
/// cell stays out of the next on screen.</b> Every check here reads the server's
/// own geometry and simulation; the client side is a live measurement and
/// <c>docs/DUEL.md</c> says what has and has not been taken.
/// </remarks>
public sealed class CellMapTests
{
    private static readonly Lazy<IReadOnlyList<CellSource>> Sources =
        new(() => CellMapGenerator.LoadSources(ReferenceTree.MapsDirectory));

    private static string DuelMaps => Path.Combine(ReferenceTree.MapsDirectory, "duel");

    private static string DuelNav => Path.Combine(ReferenceTree.RepositoryRoot, "nav", "duel");

    public static TheoryData<string> PoolNames()
    {
        var data = new TheoryData<string>();
        foreach (var (name, _) in CellMapGenerator.Pool)
            data.Add(name);

        return data;
    }

    private static CellMap Regenerate(string name) =>
        CellMapGenerator.Generate(Sources.Value, CellMapGenerator.Pool.Single(p => p.Name == name).Options);

    /// <summary>
    /// The checked-in map is what its pool entry generates, by level data and
    /// art. A miss is a regeneration (<c>gg2-server cellgen</c>, then navgen),
    /// or the shipped maps moved — which <c>MapCopyTests</c> would also say.
    /// </summary>
    [Theory]
    [MemberData(nameof(PoolNames))]
    public void TheCheckedInMapIsWhatThePoolGenerates(string name)
    {
        var path = Path.Combine(DuelMaps, name + ".png");
        Assert.True(File.Exists(path), $"{path} is missing: run `gg2-server cellgen`");
        Assert.True(CellGen.SameMap(File.ReadAllBytes(path), Regenerate(name)), $"{name} differs from its generator output");
    }

    [Fact]
    public void ThePoolDirectoryHoldsThePoolAndNothingElse()
    {
        var onDisk = Directory.GetFiles(DuelMaps, "*.png").Select(Path.GetFileNameWithoutExtension).Order().ToArray();
        Assert.Equal(CellMapGenerator.Pool.Select(p => p.Name).Order(), onDisk);
    }

    /// <summary>
    /// A cell map is a TDM map, so it has to have nothing that makes it another
    /// mode — and none of the 22 built-in names, or a client would use its own copy.
    /// </summary>
    [Theory]
    [MemberData(nameof(PoolNames))]
    public void EveryPoolMapIsCustomTeamDeathmatch(string name)
    {
        var level = LevelMap.FromPng(name, File.ReadAllBytes(Path.Combine(DuelMaps, name + ".png")));

        Assert.Equal(GameMode.TeamDeathmatch, GameModes.Of(level));
        Assert.False(InternalMaps.IsInternal(name));
        Assert.Equal(1, NavGenerator.AreaCount(level));
    }

    /// <summary>
    /// Cell <c>i</c> is red spawn <c>i</c> and blue spawn <c>i</c>, both inside
    /// its window — the index a pinned spawn will be sent with.
    /// </summary>
    [Theory]
    [MemberData(nameof(PoolNames))]
    public void SpawnIndexIsCellIndex(string name)
    {
        var map = Regenerate(name);
        var level = LevelMap.FromLevelData(name, map.LevelData);
        var spawns = new SpawnPoints(level);
        var options = CellMapGenerator.Pool.Single(p => p.Name == name).Options;

        Assert.Equal(options.Cells, map.Cells.Count);
        Assert.Equal(options.Cells, spawns.Count(Team.Red, 0));
        Assert.Equal(options.Cells, spawns.Count(Team.Blue, 0));

        for (var i = 0; i < map.Cells.Count; i++)
        {
            foreach (var team in new[] { Team.Red, Team.Blue })
            {
                Assert.True(spawns.TryGet(team, 0, i, out var x, out var y));
                Assert.True(InWindow(map.Cells[i], options, x, y), $"{name} cell {i} {team} spawn ({x}, {y}) is outside its window");
            }
        }
    }

    /// <summary>
    /// Nothing outside the cells is open, and the solid between two cells is
    /// wider than any blast reaches — rocket and sentry 65 px, mine 40, and
    /// <c>Explosions</c> asks no wall whether it is in the way. The generator
    /// blast (400) cannot occur: a cell map has no generator.
    /// </summary>
    [Theory]
    [MemberData(nameof(PoolNames))]
    public void CellsAreSealedAndFartherApartThanABlast(string name)
    {
        var map = Regenerate(name);
        var options = CellMapGenerator.Pool.Single(p => p.Name == name).Options;
        var level = LevelMap.FromLevelData(name, map.LevelData);

        Assert.True(options.Gap * GameConstants.CellSize > 2 * Explosions.BlastRadiusFor(ProjectileKind.Rocket));

        var inside = new bool[map.Width * map.Height];
        foreach (var cell in map.Cells)
        {
            for (var y = 0; y < options.CellHeight; y++)
            {
                for (var x = 0; x < options.CellWidth; x++)
                    inside[((cell.MaskY + y) * map.Width) + cell.MaskX + x] = true;
            }
        }

        for (var y = 0; y < map.Height; y++)
        {
            for (var x = 0; x < map.Width; x++)
            {
                if (!inside[(y * map.Width) + x])
                    Assert.True(level.Walkmask.IsSolid(x, y), $"{name}: ({x}, {y}) is open outside every cell");
            }
        }
    }

    /// <summary>
    /// Every class, at every spawn, drops onto solid ground inside its own cell
    /// within two seconds and stays alive there — through the real simulation,
    /// not the generator's own stand test. All 48 at once: they share no cell
    /// with anybody but their partner and nobody presses anything.
    /// </summary>
    [Theory]
    [MemberData(nameof(PoolNames))]
    public void EveryClassLandsInsideItsCell(string name)
    {
        var map = Regenerate(name);
        var options = CellMapGenerator.Pool.Single(p => p.Name == name).Options;
        var level = LevelMap.FromLevelData(name, map.LevelData);
        var world = new CollisionWorld(level);
        var spawns = new SpawnPoints(level);

        for (var playerClass = PlayerClass.Scout; playerClass <= PlayerClass.Quote; playerClass++)
        {
            var state = NewState(level);
            var events = default(TickEvents);
            var inputs = new PlayerInput[WorldState.MaxSlots];

            for (var i = 0; i < map.Cells.Count; i++)
            {
                Spawn(ref state, spawns, 1 + (2 * i), playerClass, Team.Red, i);
                Spawn(ref state, spawns, 2 + (2 * i), playerClass, Team.Blue, i);
            }

            for (var tick = 0; tick < 60; tick++)
                MatchSimulation.Tick(world, ref state, inputs.AsSpan(0, 1 + (2 * map.Cells.Count)), ref events, spawns);

            for (var i = 0; i < map.Cells.Count; i++)
            {
                foreach (var slot in new[] { 1 + (2 * i), 2 + (2 * i) })
                {
                    ref readonly var character = ref state.Characters[slot];
                    var where = $"{name} cell {i} slot {slot} {playerClass} at ({character.X:0.#}, {character.Y:0.#})";

                    Assert.True(state.IsAlive(slot), $"{where}: dead");
                    Assert.True(character.OnGround, $"{where}: not on the ground after two seconds");
                    Assert.True(InWindow(map.Cells[i], options, character.X, character.Y), $"{where}: left its cell");
                }
            }
        }
    }

    /// <summary>
    /// On the SHIPPED graph, each cell's two spawns reach each other both ways
    /// over edges every class can take, every surface the red spawn reaches leads
    /// back, and there are at least eight — the generator proved it on the cell
    /// alone, and this proves the packed map did not change the answer.
    /// </summary>
    [Theory]
    [MemberData(nameof(PoolNames))]
    public void PartnersCanReachEachOtherOnTheShippedGraph(string name)
    {
        var map = Regenerate(name);
        var graph = NavCache.Load(DuelNav, name, 1);

        for (var i = 0; i < map.Cells.Count; i++)
        {
            var cell = map.Cells[i];
            var red = Landing(graph, cell.Red);
            var blue = Landing(graph, cell.Blue);

            Assert.True(red >= 0 && blue >= 0, $"{name} cell {i}: a spawn has no node under it");
            Assert.True(Reaches(graph, red, blue) && Reaches(graph, blue, red), $"{name} cell {i}: partners cannot reach each other");

            // And no pocket you can fall into and not leave, and room to move.
            var reachable = Enumerable.Range(0, graph.NodeCount).Where(n => Reaches(graph, red, n)).ToArray();
            Assert.True(reachable.Length >= 8, $"{name} cell {i}: only {reachable.Length} surfaces");
            foreach (var node in reachable)
                Assert.True(Reaches(graph, node, red), $"{name} cell {i}: node {node} is reachable and has no way back");
        }
    }

    /// <summary>
    /// The shipped graphs are what the nav generator builds from the shipped
    /// maps, character for character — <c>NavGeneratorTests</c>' rule, for the
    /// directory it does not read.
    /// </summary>
    [Theory]
    [MemberData(nameof(PoolNames))]
    public void TheShippedGraphIsCurrent(string name)
    {
        var level = LevelMap.FromPng(name, File.ReadAllBytes(Path.Combine(DuelMaps, name + ".png")));
        var built = NavGenerator.Build(level.ForArea(1), 1);

        Assert.Equal(built.Text, NavCache.ReadFile(NavCache.FindFile(DuelNav, name, 1)));
    }

    private static WorldState NewState(LevelMap level)
    {
        var state = default(WorldState);
        state.GameMode = GameModes.Of(level);
        state.TimeLimitMinutes = 15;
        state.RespawnTime = 0;
        state.TdmInvulnerabilityTicks = 0;

        for (var i = 0; i < TeamBalance.ClassCount; i++)
            state.ClassLimits[i] = 255;

        Rounds.Start(ref state);
        state.AddPlayer(0);
        return state;
    }

    private static void Spawn(ref WorldState state, SpawnPoints spawns, int slot, PlayerClass playerClass, Team team, int cell)
    {
        state.AddPlayer(slot);
        Assert.True(state.Spawn(spawns, slot, playerClass, team, spawnpointId: cell, group: 0));
    }

    private static bool InWindow(PlacedCell cell, CellMapOptions options, double x, double y) =>
        x >= cell.MaskX * GameConstants.CellSize && x < (cell.MaskX + options.CellWidth) * GameConstants.CellSize
        && y >= cell.MaskY * GameConstants.CellSize && y < (cell.MaskY + options.CellHeight) * GameConstants.CellSize;

    /// <summary>The node under a spawn: the floor it drops onto, read off the stand rows below it.</summary>
    private static int Landing(NavGraph graph, (int X, int Y) spawn) =>
        graph.NodeFromWorld(spawn.X, spawn.Y + CellMapGenerator.SpawnHeight - NavGraph.FeetOffset);

    private static bool Reaches(NavGraph graph, int from, int to)
    {
        var seen = new bool[graph.NodeCount];
        var queue = new Queue<int>([from]);
        seen[from] = true;

        while (queue.TryDequeue(out var node))
        {
            if (node == to)
                return true;

            foreach (var edge in graph.EdgesFrom(node))
            {
                if (edge.Type is NavEdgeType.Walk or NavEdgeType.Fall or NavEdgeType.Jump or NavEdgeType.DropThrough
                    && !seen[edge.To])
                {
                    seen[edge.To] = true;
                    queue.Enqueue(edge.To);
                }
            }
        }

        return false;
    }
}
