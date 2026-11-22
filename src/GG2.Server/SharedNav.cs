using GG2.Bots;

namespace GG2.Server;

/// <summary>
/// Nav graphs and next-hop tables loaded once and shared by every server that
/// holds this object: the rooms of one room host (<c>docs/ROOMS.md</c>).
/// </summary>
/// <remarks>
/// <b>Safe to share because neither type changes after it is built</b>:
/// <see cref="NavGraph"/> and <see cref="NavDistances"/> write their arrays in
/// their constructors only. Everything per-match about routing (followers,
/// blacklists, occupancy) lives in the bot layer, keyed by node index, and is
/// never stored in the graph.
/// <para>
/// <b>What it saves</b>: one map's next-hop tables are about 10.8 MB
/// (<c>ctf_truefort</c>), which is most of what a hosted room costs when every
/// room loads its own.
/// </para>
/// <para>
/// <b>Never evicted.</b> Entries are bounded by the maps a host serves, which for
/// rooms is the shipped set. A host serving arbitrary custom maps would need
/// eviction first.
/// </para>
/// <para>
/// Only what came off disk is shared. A graph the server GENERATED for a map
/// with none stays per-server, since nothing promises two generations agree
/// on a file that is not there.
/// </para>
/// </remarks>
public sealed class SharedNav
{
    private readonly object _lock = new();
    private readonly Dictionary<(string Directory, string Map, int Area, string Fingerprint), NavGraph?> _graphs = [];
    private readonly Dictionary<NavGraph, NavDistanceSet> _distances = new(ReferenceEqualityComparer.Instance);

    /// <summary>Graphs held, for a test or a status page.</summary>
    public int GraphCount
    {
        get
        {
            lock (_lock)
                return _graphs.Count;
        }
    }

    /// <summary><see cref="NavCache.TryLoad"/>, once per graph per host.</summary>
    /// <remarks>A miss is cached too, so a map with no graph is not re-read every round.</remarks>
    public bool TryLoad(string directory, string map, int area, out NavGraph? graph, string fingerprint)
    {
        var key = (directory, map, area, fingerprint);

        lock (_lock)
        {
            if (!_graphs.TryGetValue(key, out graph))
            {
                NavCache.TryLoad(directory, map, area, out graph, fingerprint);
                _graphs[key] = graph;
            }

            return graph is not null;
        }
    }

    /// <summary>
    /// <see cref="NavDistanceSet.LoadOrDerive"/>, once per graph per host.
    /// </summary>
    /// <remarks>
    /// Keyed by the graph OBJECT, so it shares only for graphs that came from
    /// <see cref="TryLoad"/>. A generated graph is a new object each time and
    /// gets its own set.
    /// </remarks>
    public NavDistanceSet LoadOrDerive(string? directory, string map, int area, NavGraph graph, out string reason)
    {
        lock (_lock)
        {
            if (_distances.TryGetValue(graph, out var set))
            {
                reason = "shared";
                return set;
            }

            set = NavDistanceSet.LoadOrDerive(directory, map, area, graph, out reason);
            _distances[graph] = set;
            return set;
        }
    }
}
