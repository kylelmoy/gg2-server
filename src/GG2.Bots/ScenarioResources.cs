using System.Collections.Concurrent;
using GG2.Maps;

namespace GG2.Bots;

/// <summary>
/// The per-map read-only inputs a scenario world needs, decoded once and shared
/// by every world that asks for the same map.
/// </summary>
/// <remarks>
/// <b>This exists because <see cref="ScenarioWorld.Load"/> is on the episode
/// reset path.</b> A duel batch resets a world every time an episode ends, and
/// every reset was re-reading the map PNG, re-decoding the walkmask and
/// <b>re-parsing the nav cache</b> — 5.1 ms on <c>ctf_truefort</c>, of which
/// 4.8 ms is the 2 MB nav text. An episode of 120 decisions is about 0.9 ms of
/// simulation, so the reload cost several times more than the episode it
/// started. See <c>gg2-rl/docs/ENV.md</c>, "the reset reload".
/// <para>
/// <b>What is shared is exactly what is deeply immutable, and nothing
/// else.</b> <see cref="LevelMap"/> is <c>init</c>-only throughout — its
/// <see cref="Walkmask"/> is readonly words, its entities are <c>init</c>-only
/// and its <c>GgonMap</c> is written only by the parser, whose <c>Add</c> is
/// <c>internal</c>. <see cref="NavGraph"/> is readonly arrays and pure queries.
/// Neither is written after construction, so concurrent readers are safe with no
/// lock.
/// </para>
/// <para>
/// <b>The <c>CollisionWorld</c> is deliberately not cached</b>, even though a
/// scenario never writes to one. <c>CollisionWorld.SetCaptureZones</c> makes it
/// mutable in principle, and <c>MatchRunner</c> and <c>HeadlessMatch</c> already
/// record "not shared, because SetCaptureZones writes to it". Building one is
/// 0.009 ms against the 5.1 ms this class removes, so sharing it would buy 0.2%
/// in exchange for a rule that has to hold everywhere for ever.
/// </para>
/// <para>
/// <b>Never invalidated.</b> A map file that changes on disk mid-process is
/// not served again, which is the right trade for a trainer and a test run and
/// would be the wrong one for a long-lived game server — which does not use this
/// path. <see cref="ScenarioWorld.Load"/>'s uncached overload is still there for
/// a caller that needs a re-read.
/// </para>
/// </remarks>
public sealed class ScenarioResources
{
    /// <summary>
    /// The process-wide cache, which <see cref="ScenarioWorld.Load"/> uses by
    /// default.
    /// </summary>
    /// <remarks>
    /// <b>Process-wide rather than per-batch</b> so that the test suite and a
    /// multi-batch tool get the win too. It is safe to be static only because
    /// what it holds cannot be written; see the type's own remarks. Bounded by
    /// the 22 shipped maps times their areas.
    /// </remarks>
    public static ScenarioResources Shared { get; } = new();

    private readonly ConcurrentDictionary<(string Map, int Area, string MapDirectory, string Nav), Entry> _entries =
        new();

    /// <summary>How many distinct (map, area, directory) triples are held. For a test.</summary>
    public int Count => _entries.Count;

    /// <summary>
    /// The decoded map and nav graph for this map and area, loading them on the
    /// first ask.
    /// </summary>
    /// <remarks>
    /// <b>The nav directory is part of the key.</b> Two callers asking for the
    /// same map with different nav directories want different graphs, and a
    /// caller passing null wants none — <c>gg2-nav-gen</c> edits the reference
    /// tree, so "the nav directory" is not a constant of the process.
    /// </remarks>
    public (LevelMap Map, NavGraph? Nav) Get(string map, int area, string mapDirectory, string? navDirectory)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(mapDirectory);

        var entry = _entries.GetOrAdd(
            (map, area, mapDirectory, navDirectory ?? string.Empty),
            static key => new Entry(
                Read(key.Map, key.Area, key.MapDirectory),
                key.Nav.Length == 0 ? null : ReadNav(key.Map, key.Area, key.Nav)));

        return (entry.Map, entry.Nav);
    }

    internal static LevelMap Read(string map, int area, string mapDirectory) =>
        LevelMap.FromPng(map, File.ReadAllBytes(Path.Combine(mapDirectory, map + ".png"))).ForArea(area);

    internal static NavGraph? ReadNav(string map, int area, string navDirectory) =>
        NavCache.TryLoad(navDirectory, map, area, out var graph) ? graph : null;

    private sealed record Entry(LevelMap Map, NavGraph? Nav);
}
