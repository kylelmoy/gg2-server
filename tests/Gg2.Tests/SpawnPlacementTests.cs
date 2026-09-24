using Gg2.Maps;
using Gg2.Parity;
using Gg2.Protocol;

namespace Gg2.Tests;

/// <summary>
/// Every queued spawn, in every fixture, lands on the marker the recording named.
/// </summary>
/// <remarks>
/// <b>A spawn on the wrong marker is nearly invisible, which is why it gets a
/// gate of its own.</b> <c>ctf_truefort</c>'s five spawn points are 24 world
/// pixels apart — 120 wire units — so a character that starts one along shows up
/// as a handful of comparisons before the next anchor swallows it. When it was
/// really happening on <c>reference-ctf_truefort-hotsupport.trace</c> it moved
/// <b>no percentile and no closed-loop window at all</b>; the only number that
/// noticed was one open-loop maximum. docs/PARITY.md, "an unseeded weapon moves
/// the spawn-point draw".
/// <para>
/// <b>What can make it happen.</b> The replayer builds a spawning character in
/// the tick's own alarm stage (<see cref="ITraceSimulation.QueueSpawn"/>), and
/// <c>PlayerSpawn.gml</c> picks the marker with
/// <c>floor(random(numSpawnPoints))</c> off the shared stream. The harness cannot
/// reach into the middle of a tick, so it has two ways to make that come out
/// right, and only one of them is robust:
/// </para>
/// <list type="number">
/// <item><c>PortedSimulation.SeedIndependentSpawnPoints</c> narrows the list so
/// every index resolves to the wanted point. The draw's result cannot matter.
/// Used whenever at most one slot per team is spawning.</item>
/// <item><c>TryForceSpawnPointDraws</c> arranges the stream beforehand. Sound
/// only while nothing draws from it before the alarm stage — and an
/// <b>unseeded</b> weapon firing in Begin Step does exactly that. Used for a
/// wave, which wants a different point per slot out of one shared list and so
/// cannot be narrowed.</item>
/// </list>
/// <para>
/// So this test is the guard on route 2. It passes today with 25 fixtures
/// including five mid-fight Arena round starts, and it is the thing that will
/// say so if a future recording puts a wave next to an unseeded weapon.
/// </para>
/// </remarks>
public sealed class SpawnPlacementTests
{
    public static TheoryData<string> Fixtures
    {
        get
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "Fixtures");
            var data = new TheoryData<string>();

            foreach (var path in Directory.EnumerateFiles(directory, "*.trace"))
                data.Add(Path.GetFileName(path));

            return data;
        }
    }

    private static LevelMap MapLoader(string name) =>
        LevelMap.FromPng(name, ReferenceTree.ReadMap(name)).ForArea(1);

    private static WorldShape ShapeOf(string map) => GameModes.ShapeOf(MapLoader(map));

    /// <summary>
    /// <b>Both modes, because they queue different spawns.</b> An open-loop
    /// replay re-anchors every slot from the wire on each update, so a character
    /// can be present when the closed-loop run had it absent and vice versa —
    /// and <c>QueueSpawn</c> refuses a slot that already has a character. The
    /// case that was really broken showed up in open loop only.
    /// </summary>
    [Theory]
    [MemberData(nameof(Fixtures))]
    public void EveryQueuedSpawnLandsOnItsRecordedMarker(string fixture)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture);
        var trace = Trace.Decode(File.ReadAllBytes(path));
        var ticks = TraceParser.Parse(trace, ShapeOf(trace.Header.Map), out _, out var stopped, ShapeOf);

        Assert.True(stopped is null, $"{fixture}: {stopped}");

        foreach (var mode in new[] { ReplayMode.ClosedLoop, ReplayMode.OpenLoop })
        {
            var simulation = new PortedSimulation(MapLoader);

            new Replayer(simulation) { Compare = Fields.Trajectory }
                .Replay(fixture, trace.Header.Map, ticks, mode, stopAtFirstBadTick: false);

            Assert.True(
                simulation.MisplacedSpawns == 0,
                $"{fixture} [{mode}]: {simulation.MisplacedSpawns} spawn(s) landed on the wrong "
                + "marker — the spawn-point draw is being moved by something drawing from the "
                + "stream before the alarm stage. See SeedIndependentSpawnPoints.");
        }
    }
}
