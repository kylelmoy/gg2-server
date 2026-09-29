using System.Linq;
using GG2.Maps;
using GG2.Protocol;

namespace GG2.Tests;

/// <summary>
/// When a King of the Hill point unlocks, measured from the recordings — and
/// why a replay gets it wrong.
/// </summary>
/// <remarks>
/// <c>KothHUD.events/Create.xml</c> sets <c>cpUnlock = 900</c>; its Step fires
/// <c>doEventUnlockCP</c> on the tick it reads <c>cpUnlock == 0</c> and
/// decrements <em>afterwards</em>, so the unlock lands <b>901 ticks after the
/// HUD was created</b> — which is when the server restarts the room, not when
/// it announced the change.
/// <para>
/// <b>That distinction is the whole finding.</b> A recorder sees exactly one
/// <c>CHANGE_MAP</c>, at t0, and a replay calls <c>LoadMap</c> — and therefore
/// <c>Rounds.Start</c>, and therefore starts its unlock timer — right there. But
/// the reference's own round starts <em>later</em>: the two recordings unlock at
/// <b>t1236</b> and <b>t1160</b>, which are not the same offset from t0 and
/// neither of which is 901. So <c>CHANGE_MAP</c> is demonstrably not the round
/// start, and every KotH timer in a replay is early by the difference.
/// </para>
/// </remarks>
public sealed class KothUnlockTimingTests
{
    /// <summary><c>cpUnlock = 900</c>, fired on the tick it reads zero.</summary>
    /// <remarks>
    /// The read-then-decrement that makes every "n ticks later" number in this
    /// mode one more than the obvious reading.
    /// </remarks>
    private const int UnlockTicks = 901;

    /// <summary>
    /// How long after the round restarts the first characters appear.
    /// </summary>
    /// <remarks>
    /// The respawn time — a reload destroys every character and the players
    /// come back on the ordinary 150-tick timer, which
    /// <c>RespawnTimingTests</c> measures independently.
    /// </remarks>
    private const int SpawnWaveDelay = 150;

    private static LevelMap MapLoader(string name) =>
        LevelMap.FromPng(name, ReferenceTree.ReadMap(name));

    /// <summary>
    /// <b>The model: unlock = (spawn wave − 150) + 901</b>, on both recordings,
    /// exactly.
    /// </summary>
    /// <remarks>
    /// Two independent recordings with different spawn waves, and the same
    /// arithmetic lands on the recorded <c>UNLOCKCP</c> tick in both — which is
    /// what makes this a model rather than a coincidence:
    /// <list type="bullet">
    /// <item><c>uncontested</c>: wave t485 → round start t335 → unlock
    /// <b>t1236</b>.</item>
    /// <item><c>closedkoth</c>: wave t404 → round start t254 → unlock
    /// <b>t1155</b>, recorded at t1160.</item>
    /// </list>
    /// The five-tick slack on the second is the spawn wave being staggered —
    /// its slots spawn at t404 and t409, so the wave's own start is known only
    /// to within that.
    /// </remarks>
    [Theory]
    [InlineData("reference-koth_valley-uncontested.trace", 1236)]
    [InlineData("reference-koth_valley-closedkoth.trace", 1160)]
    public void TheUnlockLandsOneSpawnWaveAndNineHundredTicksIn(string fixture, int recordedUnlock)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture);
        var trace = Trace.Decode(File.ReadAllBytes(path));
        var map = MapLoader(trace.Header.Map);
        var ticks = TraceParser.Parse(trace, GameModes.ShapeOf(map), out _, out _);

        var unlock = ticks
            .Where(t => t.Events.Any(m => m.Type == MessageType.UnlockCp))
            .Select(t => t.Index)
            .ToArray();

        Assert.Single(unlock);
        Assert.Equal(recordedUnlock, unlock[0]);

        var wave = ticks
            .Where(t => t.Events.Any(m => m.Type == MessageType.PlayerSpawn))
            .Select(t => t.Index)
            .First();

        var predicted = wave - SpawnWaveDelay + UnlockTicks;

        // Within the stagger of the wave itself.
        Assert.True(
            Math.Abs(predicted - unlock[0]) <= 6,
            $"{fixture}: predicted unlock t{predicted} from wave t{wave}, recorded t{unlock[0]}");
    }

    /// <summary>
    /// <b>And <c>CHANGE_MAP</c> is not the round start</b>, which is what a
    /// replay assumes.
    /// </summary>
    /// <remarks>
    /// The direct evidence: both recordings carry exactly one <c>CHANGE_MAP</c>,
    /// at t0, and their unlocks are 1236 and 1160 ticks later. If
    /// <c>CHANGE_MAP</c> were the round start both would be 901.
    /// <para>
    /// <b>So this is a harness limitation rather than a simulation bug.</b> The
    /// port counts 901 from its own round start correctly; the replay simply
    /// starts that round several hundred ticks too early, because the only
    /// thing it can see is the announcement. Fixing it means anchoring the
    /// round the way a spawn is anchored — the wire does carry <c>cpUnlock</c>
    /// in every <c>CAPS_UPDATE</c>, so the information is there.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("reference-koth_valley-uncontested.trace")]
    [InlineData("reference-koth_valley-closedkoth.trace")]
    public void TheChangeMapTickIsNotTheRoundStart(string fixture)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture);
        var trace = Trace.Decode(File.ReadAllBytes(path));
        var map = MapLoader(trace.Header.Map);
        var ticks = TraceParser.Parse(trace, GameModes.ShapeOf(map), out _, out _);

        var changeMaps = ticks
            .Where(t => t.Events.Any(m => m.Type == MessageType.ChangeMap))
            .Select(t => t.Index)
            .ToArray();

        Assert.Equal([0], changeMaps);

        var unlock = ticks
            .First(t => t.Events.Any(m => m.Type == MessageType.UnlockCp))
            .Index;

        Assert.True(
            unlock > UnlockTicks + 100,
            $"{fixture}: unlock at t{unlock} is close enough to {UnlockTicks} after the CHANGE_MAP "
            + "that a replay starting its round there would be right — which would mean this "
            + "finding is wrong and the harness can stop anchoring the round");
    }
}
