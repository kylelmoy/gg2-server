using GG2.Maps;
using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Tests;

/// <summary>
/// The spawn point lookup a <c>PLAYER_SPAWN</c> resolves against.
/// </summary>
/// <remarks>
/// This exists to make the parity harness's anchor exact. A trace anchored on a
/// broadcast position starts inside a quantisation bin — a fifth of a pixel of
/// position, <c>1/8.5</c> px/tick of velocity — and docs/PLAN.md's phase-4
/// history is the story of a one-wire-unit residual that turned out to be
/// exactly that. Resolving the spawn point instead gives a position that passed
/// through no wire at all, which is what makes a closed-loop gate of zero
/// mismatches reachable.
/// </remarks>
public sealed class SpawnPointTests
{
    /// <summary>
    /// The property the exact anchor rests on: no spawn point has a fractional
    /// coordinate.
    /// </summary>
    /// <remarks>
    /// A fractional one would round-trip through the wire's fifths cleanly only
    /// by luck, and the anchor would be back to being a guess inside a bin. That
    /// is cheap to check and expensive to discover later, so it is checked over
    /// every shipped map rather than asserted in a comment.
    /// </remarks>
    [Fact]
    public void EverySpawnPointInEveryShippedMapIsOnIntegerCoordinates()
    {
        var seen = 0;

        foreach (var fixture in MapFixtures.All)
        {
            var map = LevelMap.FromPng(fixture.Name, ReferenceTree.ReadMap(fixture.Name));

            foreach (var entity in map.Entities)
            {
                if (!IsSpawnPoint(entity.Type))
                    continue;

                seen++;
                Assert.True(
                    entity.X == Math.Floor(entity.X) && entity.Y == Math.Floor(entity.Y),
                    $"{fixture.Name}: {entity.Type} at ({entity.X}, {entity.Y}) is not on an integer");
            }
        }

        Assert.True(seen > 200, $"only {seen} spawn points seen; the fixture looks wrong");
    }

    /// <summary>
    /// A team with no spawn point at all does not get a character at (0,0), and
    /// the spawn is tried again a second later rather than being dropped.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Upstream this places the character in the TOP-LEFT CORNER of the
    /// map.</b> <c>PlayerSpawn.gml</c> reads
    /// <c>ds_list_find_value(&lt;empty list&gt;, 0)</c>, which GM8 answers with 0
    /// for both axes, and its own "this map does not contain valid spawn points"
    /// guard cannot fire because <c>selectSpawnGroup</c> clamps the group to 0.
    /// The port has never done that — <see cref="Timers"/>' spawn stage returns
    /// on an empty list — but returning plainly spent the alarm, so the player was
    /// stranded for the rest of the map with no character, no timer and nothing
    /// in the log. This pins the re-arm, which is the other half.
    /// <para>
    /// Unreachable on all 22 shipped maps, every one of which places spawn points
    /// for both teams — so the map here is a real one with one team's markers
    /// removed, and the red player beside the blue one is the control that says
    /// the map is otherwise fine.
    /// docs/TRAPS-SIMULATION.md, "Upstream bug fixes carried into the port".
    /// </para>
    /// </remarks>
    [Fact]
    public void ATeamWithNoSpawnPointIsRetriedRatherThanPlacedAtTheOrigin()
    {
        var full = LevelMap.FromPng("ctf_truefort", ReferenceTree.ReadMap("ctf_truefort")).ForArea(1);

        // Every entity except blue's spawn markers. WithEntities is the harness
        // seam LevelMap documents; nothing in the server uses it.
        var withoutBlueSpawns = full.WithEntities(
            [.. full.Entities.Where(e => !IsSpawnPoint(e.Type) || !e.Type.Contains("blue", StringComparison.OrdinalIgnoreCase))]);

        var world = new CollisionWorld(withoutBlueSpawns);
        var points = new SpawnPoints(withoutBlueSpawns);

        Assert.Equal(0, points.Count(Team.Blue, 0));
        Assert.True(points.Count(Team.Red, 0) > 0, "red's markers went too, so the control is gone");

        var state = default(WorldState);
        state.GameMode = GameModes.ShapeOf(withoutBlueSpawns).GameMode;

        state.AddPlayer(1);
        state.Players[1].Team = Team.Blue;
        state.Players[1].Class = PlayerClass.Scout;
        state.Players[1].RespawnTimer = 1;

        state.AddPlayer(2);
        state.Players[2].Team = Team.Red;
        state.Players[2].Class = PlayerClass.Scout;
        state.Players[2].RespawnTimer = 1;

        var events = default(TickEvents);
        MatchSimulation.Tick(world, ref state, [], ref events, points);

        // The control: red spawned, so the tick really did reach the spawn stage.
        Assert.True(state.IsAlive(2), "the red control did not spawn, so this test checks nothing");

        Assert.False(state.IsAlive(1), "blue was given a character with nowhere to put it");
        Assert.Equal(30, state.Players[1].RespawnTimer);
    }

    /// <summary>The eight entity types that are spawn points, and no others.</summary>
    private static bool IsSpawnPoint(string type) => type switch
    {
        EntityTypes.RedSpawn or EntityTypes.RedSpawn1 or EntityTypes.RedSpawn2
            or EntityTypes.RedSpawn3 or EntityTypes.RedSpawn4 => true,
        EntityTypes.BlueSpawn or EntityTypes.BlueSpawn1 or EntityTypes.BlueSpawn2
            or EntityTypes.BlueSpawn3 or EntityTypes.BlueSpawn4 => true,
        _ => false,
    };

    /// <summary>
    /// The lookup reproduces the reference's own list, in the reference's own
    /// order.
    /// </summary>
    /// <remarks>
    /// <c>offloadSpawnPoints.gml</c> appends in <c>with (SpawnPointRed)</c>
    /// order, which is instance order, which is the order the map builder
    /// created them in — so it is the order the entity block lists them. Getting
    /// this wrong would put a spawn at another spawn's coordinates, which on
    /// <c>ctf_truefort</c> is a 24-pixel error that looks exactly like a physics
    /// bug.
    /// </remarks>
    [Fact]
    public void TruefortResolvesEachSpawnPointToTheEntityInThatPosition()
    {
        var map = LevelMap.FromPng("ctf_truefort", ReferenceTree.ReadMap("ctf_truefort"));
        var spawns = new SpawnPoints(map);

        AssertMatchesEntityOrder(map, spawns, Team.Red, EntityTypes.RedSpawn);
        AssertMatchesEntityOrder(map, spawns, Team.Blue, EntityTypes.BlueSpawn);

        // The reference's own numbers as well, so a refactor of the entity
        // reader cannot quietly agree with itself.
        Assert.True(spawns.TryGet(Team.Red, 0, 0, out var x, out var y));
        Assert.Equal(1608, x);
        Assert.Equal(456, y);

        Assert.True(spawns.TryGet(Team.Blue, 0, 0, out x, out y));
        Assert.Equal(3726, x);
        Assert.Equal(456, y);

        Assert.Equal(5, spawns.Count(Team.Red, 0));
        Assert.Equal(5, spawns.Count(Team.Blue, 0));
    }

    private static void AssertMatchesEntityOrder(
        LevelMap map,
        SpawnPoints spawns,
        Team team,
        string type)
    {
        var expected = map.Entities.Where(e => e.Type == type).ToArray();

        Assert.Equal(expected.Length, spawns.Count(team, 0));

        for (var i = 0; i < expected.Length; i++)
        {
            Assert.True(spawns.TryGet(team, 0, i, out var x, out var y));
            Assert.Equal(expected[i].X, x);
            Assert.Equal(expected[i].Y, y);
        }
    }

    /// <summary>
    /// Each entity type lands in the group its object's Create event sets.
    /// </summary>
    /// <remarks>
    /// No shipped map uses a forward spawn — every one of the 227 spawn points
    /// in the built-in maps is a plain <c>redspawn</c> or <c>bluespawn</c> in
    /// group 0 — so this synthetic map is the only place the grouping is
    /// exercised. Custom maps do use them, and <c>selectSpawnGroup.gml</c> picks
    /// between the groups by gamemode state.
    /// </remarks>
    [Fact]
    public void NumberedSpawnEntitiesLandInTheirOwnGroup()
    {
        var spawns = new SpawnPoints(MapWith(
            (EntityTypes.RedSpawn, 10, 11),
            (EntityTypes.RedSpawn1, 20, 21),
            (EntityTypes.RedSpawn2, 30, 31),
            (EntityTypes.RedSpawn3, 40, 41),
            (EntityTypes.RedSpawn4, 50, 51),
            (EntityTypes.BlueSpawn2, 60, 61)));

        for (var group = 0; group < SpawnPoints.GroupCount; group++)
        {
            Assert.Equal(1, spawns.Count(Team.Red, group));
            Assert.True(spawns.TryGet(Team.Red, group, 0, out var x, out var y));
            Assert.Equal(10 * (group + 1), x);
            Assert.Equal((10 * (group + 1)) + 1, y);
        }

        // Teams are separate lists: a blue spawn in group 2 does not lengthen
        // red's group 2.
        Assert.Equal(1, spawns.Count(Team.Blue, 2));
        Assert.Equal(0, spawns.Count(Team.Blue, 0));
        Assert.True(spawns.TryGet(Team.Blue, 2, 0, out var blueX, out _));
        Assert.Equal(60, blueX);
    }

    /// <summary>
    /// <c>spawnroom</c> is not a spawn point, however much its name suggests it.
    /// </summary>
    /// <remarks>
    /// It is the room volume that decides where a player may change class, and
    /// <c>offloadSpawnPoints.gml</c> never reads it. Counting it would shift
    /// every later index by one, which is the quiet version of the ordering bug
    /// above.
    /// </remarks>
    [Fact]
    public void SpawnRoomsAreNotSpawnPoints()
    {
        var spawns = new SpawnPoints(MapWith(
            (EntityTypes.SpawnRoom, 1, 1),
            (EntityTypes.RedSpawn, 2, 2)));

        Assert.Equal(1, spawns.Count(Team.Red, 0));
        Assert.True(spawns.TryGet(Team.Red, 0, 0, out var x, out _));
        Assert.Equal(2, x);
    }

    /// <summary>
    /// An id or group outside the lists is refused rather than throwing or
    /// wrapping.
    /// </summary>
    /// <remarks>
    /// Both values arrive from the wire, so a malformed or truncated
    /// <c>PLAYER_SPAWN</c> must not take the replayer down. It falls back to
    /// anchoring on the next observed position instead.
    /// </remarks>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(0, -1)]
    [InlineData(-1, 0)]
    [InlineData(SpawnPoints.GroupCount, 0)]
    [InlineData(1, 0)]
    public void OutOfRangeLookupsReturnFalse(int group, int index)
    {
        var spawns = new SpawnPoints(MapWith((EntityTypes.RedSpawn, 7, 8)));

        Assert.False(spawns.TryGet(Team.Red, group, index, out var x, out var y));
        Assert.Equal(0, x);
        Assert.Equal(0, y);
    }

    /// <summary>Builds a map with a one-cell walkmask and the given entities.</summary>
    private static LevelMap MapWith(params (string Type, double X, double Y)[] entities)
    {
        var block = string.Join(',', entities.Select(e => $"{{type:{e.Type},x:{e.X},y:{e.Y}}}"));

        // A 1x1 open mask: one cell, six zero bits, chr(0 + 32) = ' '.
        var levelData = string.Join('\n',
            "{WALKMASK}", "1", "1", " ", "{END WALKMASK}",
            "{ENTITIES}", "[" + block + "]", "{END ENTITIES}");

        return LevelMap.FromLevelData("synthetic", levelData);
    }
}
