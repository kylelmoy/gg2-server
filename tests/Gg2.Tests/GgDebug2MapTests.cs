using Gg2.Bots;
using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// <c>gg_debug2</c> — the port-authored CTF debug map — is what it says it is.
/// </summary>
/// <remarks>
/// The map is <c>tools/gen-gg-debug2.js</c>'s output and the reference does not
/// ship it (<see cref="MapCopyTests"/> asserts that direction), so nothing
/// outside this repo can gate it. These tests are that gate: each one pins a
/// claim the generator's header makes, so a regenerated map that silently
/// dropped a feature goes red here rather than in whatever was using the map to
/// test something else.
/// <para>
/// ⚠️ <b>A failure after editing the generator is the re-record path</b>, the
/// same rule as the pinned bot measurements: check the change is one you meant,
/// update the expectation, and say so in the commit.
/// </para>
/// </remarks>
public sealed class GgDebug2MapTests
{
    private static LevelMap Load() =>
        LevelMap.FromPng("gg_debug2", ReferenceTree.ReadMap("gg_debug2"));

    /// <summary>It parses, and its mode is Capture the Flag.</summary>
    [Fact]
    public void ItLoadsAndIsCaptureTheFlag()
    {
        var map = Load();

        Assert.Equal(GameMode.CaptureTheFlag, GameModes.Of(map));
        Assert.Equal(240 * 6, map.Width);
        Assert.Equal(80 * 6, map.Height);
        Assert.Equal(240, map.Walkmask.Width);
        Assert.Equal(80, map.Walkmask.Height);
    }

    /// <summary>
    /// The FULL_UPDATE shape has nothing in it the writer cannot serialise.
    /// </summary>
    /// <remarks>
    /// ⚠️ The load-bearing zero is <c>MovingPlatformCount</c>: the port declares
    /// <c>moving_platform</c> and simulates nothing for it, and a non-zero count
    /// here would make <c>WorldShape</c> promise five-byte blocks the writer
    /// never writes — a framing break, not a missing feature.
    /// </remarks>
    [Fact]
    public void TheShapeIsPlainCtf()
    {
        var shape = GameModes.ShapeOf(Load());

        Assert.Equal(GameMode.CaptureTheFlag, shape.GameMode);
        Assert.Equal(0, shape.MovingPlatformCount);
        Assert.Equal(0, shape.ControlPointCount);
        Assert.Equal(0, shape.GeneratorCount);
    }

    /// <summary>
    /// The entity census, both directions — exactly what the generator placed.
    /// </summary>
    [Fact]
    public void TheEntityCensusIsExactlyAsAuthored()
    {
        var map = Load();

        var expected = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [EntityTypes.SpawnRoom] = 4,
            [EntityTypes.RedSpawn] = 2,
            [EntityTypes.BlueSpawn] = 2,
            [EntityTypes.RedIntel] = 1,
            [EntityTypes.BlueIntel] = 1,
            [EntityTypes.MedCabinet] = 2,
            [EntityTypes.RedTeamGate] = 1,
            [EntityTypes.BlueTeamGate] = 1,
            [EntityTypes.RedTeamGate2] = 1,
            [EntityTypes.BlueTeamGate2] = 1,
            [EntityTypes.DropdownPlatform] = 3,
            [EntityTypes.MoveBoxUp] = 2,
            [EntityTypes.BulletWall] = 1,
            [EntityTypes.PlayerWall] = 1,
            [EntityTypes.FragBox] = 1,
            [EntityTypes.KillBox] = 1,
        };

        var actual = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entity in map.Entities)
            actual[entity.Type] = actual.GetValueOrDefault(entity.Type) + 1;

        Assert.Equal(
            expected.OrderBy(p => p.Key, StringComparer.Ordinal),
            actual.OrderBy(p => p.Key, StringComparer.Ordinal));

        foreach (var entity in map.Entities)
            Assert.True(EntityTypes.IsKnown(entity.Type), $"unknown entity type '{entity.Type}'");
    }

    /// <summary>
    /// The walkmask is exactly mirror-symmetric in x, which is what makes the
    /// map a fair CTF arena and is the invariant the generator asserts too.
    /// </summary>
    [Fact]
    public void TheMaskIsMirrorSymmetric()
    {
        var mask = Load().Walkmask;

        for (var y = 0; y < mask.Height; y++)
        for (var x = 0; x < mask.Width; x++)
        {
            Assert.True(
                mask.IsSolid(x, y) == mask.IsSolid(mask.Width - 1 - x, y),
                $"mask differs from its mirror at cell ({x}, {y})");
        }
    }

    /// <summary>
    /// One team's spawn can reach the other's intel, and the two physics
    /// chambers cannot be reached at all.
    /// </summary>
    /// <remarks>
    /// A flood fill over open cells: necessary for the play space to connect
    /// (a character cannot cross solid), and <em>exact</em> for the sealed
    /// chambers — no open path in means no way in short of a teleport, which is
    /// the chambers' whole design: their hazard rigs must never be reachable by
    /// a nav edge, so no bot can wander into the killbox.
    /// </remarks>
    [Fact]
    public void TheFieldConnectsAndTheChambersAreSealed()
    {
        var mask = Load().Walkmask;

        // Red spawn (78, 372) -> cell (13, 62).
        var reached = FloodFrom(mask, 13, 62);

        Assert.True(reached[Cell(mask, 204, 63)], "blue intel is not reachable from the red spawn");
        Assert.True(reached[Cell(mask, 226, 62)], "blue spawn area is not reachable from the red spawn");
        Assert.True(reached[Cell(mask, 119, 70)], "the under-tower tunnel is not reachable");
        Assert.True(reached[Cell(mask, 120, 37)], "the air above the tower top is not reachable");

        // Chamber interiors: red [46..94]x[8..30], blue mirrored. Every open
        // cell inside is unreachable, or the shell has a hole.
        for (var y = 8; y <= 30; y++)
        for (var x = 46; x <= 94; x++)
        {
            if (!mask.IsSolid(x, y))
                Assert.False(reached[y * mask.Width + x], $"red chamber leaks at cell ({x}, {y})");

            var mirrored = mask.Width - 1 - x;
            if (!mask.IsSolid(mirrored, y))
                Assert.False(reached[y * mask.Width + mirrored], $"blue chamber leaks at cell ({mirrored}, {y})");
        }
    }

    private static int Cell(Walkmask mask, int x, int y) => y * mask.Width + x;

    private static bool[] FloodFrom(Walkmask mask, int startX, int startY)
    {
        var reached = new bool[mask.Width * mask.Height];
        var queue = new Queue<(int X, int Y)>();

        Assert.False(mask.IsSolid(startX, startY), "flood start is inside a wall");
        reached[startY * mask.Width + startX] = true;
        queue.Enqueue((startX, startY));

        while (queue.TryDequeue(out var at))
        {
            Span<(int X, int Y)> next =
                [(at.X - 1, at.Y), (at.X + 1, at.Y), (at.X, at.Y - 1), (at.X, at.Y + 1)];

            foreach (var (x, y) in next)
            {
                if (x < 0 || y < 0 || x >= mask.Width || y >= mask.Height)
                    continue;

                if (mask.IsSolid(x, y) || reached[y * mask.Width + x])
                    continue;

                reached[y * mask.Width + x] = true;
                queue.Enqueue((x, y));
            }
        }

        return reached;
    }

    /// <summary>
    /// Every spawn point and both intel bases stand in open space with solid
    /// ground within a jump beneath them.
    /// </summary>
    [Fact]
    public void SpawnsAndIntelStandOnGround()
    {
        var map = Load();
        var mask = map.Walkmask;

        var anchors = map.Entities
            .Where(e => e.Type is EntityTypes.RedSpawn or EntityTypes.BlueSpawn
                or EntityTypes.RedIntel or EntityTypes.BlueIntel);

        foreach (var entity in anchors)
        {
            var cx = (int)entity.X / 6;
            var cy = (int)entity.Y / 6;

            Assert.False(mask.IsSolid(cx, cy), $"{entity.Type} at ({entity.X}, {entity.Y}) is inside terrain");

            var grounded = false;
            for (var below = cy + 1; below <= cy + 10 && !grounded; below++)
                grounded = mask.IsSolid(cx, below);

            Assert.True(grounded, $"{entity.Type} at ({entity.X}, {entity.Y}) has no ground within 60px below");
        }
    }

    /// <summary>
    /// The nav graph exists and carries every edge type the bot layer consumes,
    /// which is half of what the map is for.
    /// </summary>
    /// <remarks>
    /// Regenerate with
    /// <c>node &lt;gg2-nav-gen&gt;/bin/gg2navgen.js build maps/gg_debug2.png --md5 none --out nav</c>
    /// after any generator change — the graph is derived from the map bytes and
    /// goes stale with them.
    /// </remarks>
    [Fact]
    public void TheNavGraphCoversEveryEdgeType()
    {
        ReferenceTree.RequireNavCache();

        var graph = NavCache.Load(ReferenceTree.NavCacheDirectory, "gg_debug2", 1);

        Assert.True(graph.NodeCount > 40, $"only {graph.NodeCount} nav nodes");

        var kinds = new HashSet<NavEdgeType>();
        for (var node = 0; node < graph.NodeCount; node++)
        {
            foreach (var edge in graph.EdgesFrom(node))
                kinds.Add(edge.Type);
        }

        foreach (var wanted in new[]
                 {
                     NavEdgeType.Walk, NavEdgeType.Jump, NavEdgeType.Fall,
                     NavEdgeType.DropThrough, NavEdgeType.DoubleJump, NavEdgeType.RocketJump,
                 })
        {
            Assert.True(kinds.Contains(wanted), $"nav graph has no {wanted} edge");
        }
    }

    /// <summary>
    /// A plain walker — no double jump, no rocket jump — can path from its
    /// spawn to the enemy intel, and can NOT path into the enemy spawn zone.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>This is the bug the map's first cut shipped with</b>: the team
    /// gates sat on the outer doors, so each intel was behind a gate solid to
    /// the enemy and no route to it existed for anybody — while every geometry
    /// test stayed green. Pathability is a claim about the graph, so it is
    /// gated on the graph, not inferred from the mask.
    /// </remarks>
    [Fact]
    public void APlainWalkerReachesTheEnemyIntelButNotTheEnemySpawn()
    {
        ReferenceTree.RequireNavCache();

        var graph = NavCache.Load(ReferenceTree.NavCacheDirectory, "gg_debug2", 1);
        var search = new NavSearch(graph);
        var path = new List<int>();

        var redSpawn = graph.NodeFromWorld(78, 372);
        var blueSpawn = graph.NodeFromWorld(1362, 372);
        var redIntel = graph.NodeFromWorld(216, 380);
        var blueIntel = graph.NodeFromWorld(1224, 380);

        Assert.True(
            search.Find(redSpawn, blueIntel, new NavQuery(Team.Red), path),
            "no walker route from the red spawn to the blue intel");
        Assert.True(
            search.Find(blueSpawn, redIntel, new NavQuery(Team.Blue), path),
            "no walker route from the blue spawn to the red intel");

        Assert.False(
            search.Find(redSpawn, blueSpawn, new NavQuery(Team.Red), path),
            "a red walker pathed into the blue spawn zone through the blue team gate");
        Assert.False(
            search.Find(blueSpawn, redSpawn, new NavQuery(Team.Blue), path),
            "a blue walker pathed into the red spawn zone through the red team gate");
    }

    /// <summary>
    /// Bots actually play it: they leave their spawns and go somewhere, the
    /// same weak bar <see cref="MatchActuallyHappensTests"/> holds every
    /// gamemode's exemplar map to.
    /// </summary>
    [Fact]
    public void BotsGoSomewhereOnIt()
    {
        var match = HeadlessMatch.Load(
            new HeadlessMatchOptions(
                Map: "gg_debug2",
                Area: 1,
                Bots: 8,
                Difficulty: 3,
                CapLimit: 5,
                TimeLimitMinutes: 5,
                KillLimit: 20,
                RespawnTimeSeconds: 5,
                MaxTicks: 600,
                Seed: 11),
            ReferenceTree.MapsDirectory,
            ReferenceTree.NavCacheDirectory);

        var start = new double[WorldState.MaxSlots];
        var moved = new double[WorldState.MaxSlots];

        match.Step();
        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
            if (match.State.IsAlive(slot))
                start[slot] = match.State.Characters[slot].X;

        for (var tick = 0; tick < 600 && match.Outcome == MatchOutcome.Running; tick++)
        {
            match.Step();

            for (var slot = 0; slot < WorldState.MaxSlots; slot++)
                if (match.State.IsAlive(slot))
                    moved[slot] = Math.Max(moved[slot], Math.Abs(match.State.Characters[slot].X - start[slot]));
        }

        var live = moved.Count(m => m > 0);
        Assert.True(live >= 4, $"only {live} bots moved at all");
        Assert.True(moved.Count(m => m > 100) >= 4, $"only {moved.Count(m => m > 100)} bots went anywhere");
    }
}
