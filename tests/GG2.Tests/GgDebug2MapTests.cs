using GG2.Bots;
using GG2.Maps;
using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Tests;

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
/// <b>A failure after editing the generator is the re-record path</b>, the
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
    /// <b>The four moving platforms are load-bearing for the wire</b>: this is
    /// the only built-in map that places any, so it is the only place the
    /// platform blocks' framing is exercised by a real map. The count is safe on
    /// the wire with or without the platform <em>motion</em> —
    /// <c>MatchState.BuildEntities</c> has fed <c>StateUpdateWriter</c>'s
    /// five-byte blocks since phase 6, which this file's first version got
    /// wrong (it asserted zero "because the writer never writes them"; the
    /// writer always did — what was missing until the moving-platforms work was
    /// the positions <em>advancing</em>).
    /// </remarks>
    [Fact]
    public void TheShapeIsPlainCtf()
    {
        var shape = GameModes.ShapeOf(Load());

        Assert.Equal(GameMode.CaptureTheFlag, shape.GameMode);
        Assert.Equal(5, shape.MovingPlatformCount);
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
            [EntityTypes.MovingPlatform] = 5,
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
    /// The four moving platforms carry exactly the routes the generator's
    /// header promises — the properties are this map's contract with the
    /// moving-platform simulation.
    /// </summary>
    /// <remarks>
    /// Asserted on the raw entity properties rather than through the
    /// simulation's own parser, so the pin holds whether or not the platform
    /// motion has merged: two mirrored valley ferries on a horizontal loop, a
    /// vertical chamber hoist with asymmetric speeds, and one triggered
    /// platform that stays parked for ever because the carry (and so any
    /// rider) does not exist. None of the four can ever carry a character —
    /// a MovingPlatform's collision mask comes from an embedded sprite named
    /// by a <c>resource</c> property, and this map embeds none.
    /// </remarks>
    [Fact]
    public void TheMovingPlatformsCarryTheIntendedRoutes()
    {
        var platforms = Load().OfType(EntityTypes.MovingPlatform).ToArray();

        Assert.Equal(5, platforms.Length);

        var expected = new (double X, double Y, string Top, string Left, string Up, string Down, string Trigger)[]
        {
            (300, 380, "0", "120", "2", "2", "0"),   // red valley ferry
            (1140, 380, "0", "-120", "2", "2", "0"), // blue valley ferry, mirrored leg
            (564, 168, "90", "0", "1", "3", "0"),    // chamber hoist, asymmetric speeds
            (300, 120, "60", "0", "3", "3", "1"),    // chamber parked: triggered, no rider possible
            (282, 150, "0", "60", "1", "1", "0"),    // chamber raft: the one with a sprite
        };

        for (var i = 0; i < expected.Length; i++)
        {
            var (x, y, top, left, up, down, trigger) = expected[i];
            var platform = platforms[i];

            Assert.Equal(x, platform.X);
            Assert.Equal(y, platform.Y);
            Assert.Equal(top, platform.GetString("top"));
            Assert.Equal(left, platform.GetString("left"));
            Assert.Equal(up, platform.GetString("upspeed"));
            Assert.Equal(down, platform.GetString("downspeed"));
            Assert.Equal(trigger, platform.GetString("trigger"));

            // Only the raft names a resource: the other four must never grow a
            // collision shape by accident, because everything the map promises
            // about them assumes they cannot touch a character.
            Assert.Equal(i == 4 ? "raftsprite" : null, platform.GetString("resource"));
        }
    }

    /// <summary>
    /// The raft's embedded sprite survives the round trip: the meta block holds
    /// the key the entity names, and its bytes are the PNG the generator wrote.
    /// </summary>
    /// <remarks>
    /// <b>The binding is by name and nothing on this side validates it</b> —
    /// a platform naming a missing resource just gets no sprite and collides
    /// with nothing, exactly like the reference (<c>stringToResource</c>
    /// returns -1). So the linkage is asserted here, where it can fail loudly.
    /// The <em>shape</em> is the moving-platforms work's to gate
    /// (<c>SpriteMask.Decode</c>); the design facts it will want are pinned in
    /// the generator: 24x12, non-rectangular, <b>178 solid pixels under the
    /// bottom-left removeback rule and 110 under a wrong top-left reading</b>,
    /// which is what makes the sprite a discriminating probe target.
    /// </remarks>
    [Fact]
    public void TheRaftSpriteIsEmbeddedAndWellFormed()
    {
        var map = Load();

        var resource = Assert.IsType<string>(map.Metadata?.GetString("raftsprite"));
        var bytes = System.Text.Encoding.Latin1.GetBytes(resource);

        // The PNG signature, byte for byte — also what stringToResource sniffs
        // (characters 2-4 spelling "PNG").
        Assert.True(bytes.Length > 60, $"raft sprite is only {bytes.Length} bytes");
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, bytes[..8]);

        // IHDR: 24x12, bit depth 8, indexed — the shape PngSprite.Decode accepts.
        Assert.Equal(24u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16)));
        Assert.Equal(12u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20)));
        Assert.Equal(8, bytes[24]);
        Assert.Equal(3, bytes[25]);
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
    /// Regenerate with <c>gg2-server navgen</c> after any generator change — the graph is derived from the map bytes and
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
    /// <b>This is the bug the map's first cut shipped with</b>: the team
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
