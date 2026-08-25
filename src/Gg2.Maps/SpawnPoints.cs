using Gg2.Protocol;

namespace Gg2.Maps;

/// <summary>
/// A map's spawn points, indexed the way <c>PLAYER_SPAWN</c> indexes them.
/// </summary>
/// <remarks>
/// <c>sendEventSpawn.gml</c> puts a <b>spawn point id and a group</b> on the
/// wire rather than a position, and every client resolves the pair to
/// coordinates itself in <c>doEventSpawn.gml</c>. This is that resolution.
/// <para>
/// <b>Why the parity harness wants it.</b> Positions are broadcast only every
/// seventh tick (<c>GameServerBeginStep.gml:96</c>), so a replay that waits for
/// the next <c>QUICK_UPDATE</c> to place a freshly spawned character anchors on
/// a position the character has already fallen away from, quantised to a fifth
/// of a pixel. Resolving the spawn point instead gives an anchor that is exact:
/// every one of the 296 spawn entities in the built-in maps sits on integer
/// coordinates, and <c>instance_create</c> leaves both speeds at zero. That is
/// the difference between a closed-loop gate that can reach zero mismatches and
/// one that cannot.
/// </para>
/// </remarks>
public sealed class SpawnPoints
{
    /// <summary>Spawn groups per team, as <c>offloadSpawnPoints.gml</c> allocates them.</summary>
    public const int GroupCount = 5;

    private readonly List<(double X, double Y)>[] _red;
    private readonly List<(double X, double Y)>[] _blue;

    public SpawnPoints(LevelMap map)
    {
        ArgumentNullException.ThrowIfNull(map);

        _red = NewGroups();
        _blue = NewGroups();

        // offloadSpawnPoints.gml walks `with (SpawnPointRed)`, and SpawnPointRed1
        // through SpawnPointRed4 all declare SpawnPointRed as their parent — so
        // one `with` sweeps all five groups, because GM8's `with` includes an
        // object's descendants. It visits them in instance order, which is the
        // order the map builder created them in, which is the order the entity
        // block lists them in. Hence a single ordered pass here rather than five.
        foreach (var entity in map.Entities)
        {
            if (GroupOf(entity.Type) is not { } group)
                continue;

            var groups = IsRed(entity.Type) ? _red : _blue;
            groups[group].Add((entity.X, entity.Y));
        }
    }

    private static List<(double X, double Y)>[] NewGroups()
    {
        var groups = new List<(double X, double Y)>[GroupCount];
        for (var i = 0; i < groups.Length; i++)
            groups[i] = [];

        return groups;
    }

    /// <summary>
    /// The group a spawn entity type belongs to, or null if it is not one.
    /// </summary>
    /// <remarks>
    /// The group is a property of the <em>object</em>, not of the map data: each
    /// of the five objects per team sets it in its own Create event
    /// (<c>SpawnPointRed1.events/Create.xml</c> is <c>group = 1</c>), and
    /// <c>builder_init.gml:262-266</c> is what binds each entity name to the
    /// object. <c>spawnroom</c> is deliberately not here — it is the room
    /// volume, not a spawn point, and <c>offloadSpawnPoints</c> never reads it.
    /// </remarks>
    private static int? GroupOf(string type) => type switch
    {
        EntityTypes.RedSpawn or EntityTypes.BlueSpawn => 0,
        EntityTypes.RedSpawn1 or EntityTypes.BlueSpawn1 => 1,
        EntityTypes.RedSpawn2 or EntityTypes.BlueSpawn2 => 2,
        EntityTypes.RedSpawn3 or EntityTypes.BlueSpawn3 => 3,
        EntityTypes.RedSpawn4 or EntityTypes.BlueSpawn4 => 4,
        _ => null,
    };

    private static bool IsRed(string type) => type.StartsWith("red", StringComparison.Ordinal);

    /// <summary>How many spawn points a team has in a group.</summary>
    public int Count(Team team, int group) =>
        group >= 0 && group < GroupCount ? Groups(team)[group].Count : 0;

    /// <summary>
    /// Resolves a <c>PLAYER_SPAWN</c>'s point id and group to a position.
    /// </summary>
    /// <remarks>
    /// The reference picks the id with <c>floor(random(numSpawnPoints))</c>
    /// (<c>PlayerSpawn.gml</c>) — server-side RNG this port is free about
    /// (docs/PARITY.md) — and then sends it, so a replay does not have to
    /// reproduce the choice, only the lookup.
    /// </remarks>
    public bool TryGet(Team team, int group, int index, out double x, out double y)
    {
        x = 0;
        y = 0;

        if (group < 0 || group >= GroupCount)
            return false;

        var points = Groups(team)[group];
        if (index < 0 || index >= points.Count)
            return false;

        (x, y) = points[index];
        return true;
    }

    /// <summary>
    /// Which team's lists to read. Anything that is not Red is Blue, exactly as
    /// <c>doEventSpawn.gml</c>'s <c>if(spawner.team == TEAM_RED)</c> decides it.
    /// </summary>
    private List<(double X, double Y)>[] Groups(Team team) => team == Team.Red ? _red : _blue;
}
