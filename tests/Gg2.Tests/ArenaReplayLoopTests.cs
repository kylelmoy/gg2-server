using System.Linq;
using Gg2.Maps;
using Gg2.Parity;
using Gg2.Protocol;

namespace Gg2.Tests;

/// <summary>
/// ⚠️ <b>OPEN, and pinned so that fixing it fails loudly: the port's Arena round
/// loop cannot be replayed.</b>
/// </summary>
/// <remarks>
/// A replayed Arena recording runs two round loops at once — the reference's,
/// written down in the stream as <c>ARENA_*</c> messages, and the port's own,
/// derived from who is alive. They are nowhere near each other, and until
/// 2026-08-28 nothing said so, because the port's arena state had no observable
/// effect beyond an ammo refill nobody was comparing.
/// <para>
/// <b>The cause is structural, not arithmetic.</b> <c>doEventArenaRestart</c>
/// ends in <c>respawnEveryone()</c>, and a replay is only allowed to spawn
/// characters the recording announced — so the tick after the port restarts a
/// round, one team has nobody alive, and an empty team is a win. The loop then
/// runs on the round-end and round-setup timers alone: 300 + 300 + 1.
/// </para>
/// <para>
/// ⚠️ <b>Injecting the recording's four messages is the obvious fix and is not
/// obviously right.</b> Everything else a replay cannot derive — a spawn, a
/// build, a zoom — is injected because the reference already made the choice
/// and put it on the wire, and the arena messages qualify. What has no
/// precedent is that the port would go on making its <em>own</em> decision
/// beside the injected one, and there is no way to tell the simulation not to:
/// <c>MatchSimulation</c> takes no mode and <c>docs/SIMULATION.md</c>'s rule is
/// that it never special-cases its caller. Whoever picks this up should decide
/// that question first.
/// </para>
/// <para>
/// ⚠️ <b>This file's first version blamed the whole symptom on replay, and a
/// LIVE bug was hiding behind it</b> — <c>spawningAllowed()</c> moved from the
/// respawn alarm into <c>PlayerSpawn</c>, so every arena round began with
/// nobody alive. That is fixed and gated by <see cref="ArenaRoundLoopTests"/>.
/// <b>Fixing it moved nothing here</b>, which is the evidence that these are two
/// causes with one symptom rather than one cause seen twice.
/// </para>
/// <para>
/// The two <see cref="ClientPredictionGateTests"/> arena rows are the price:
/// they read 19/21 and 28/9 rather than 2/2 and 9/2, and every one of those
/// units is this.
/// </para>
/// </remarks>
public sealed class ArenaReplayLoopTests
{
    private const string Fixture = "reference-arena_lumberyard-closedarena.trace";

    private static LevelMap MapLoader(string name) =>
        LevelMap.FromPng(name, ReferenceTree.ReadMap(name)).ForArea(1);

    private static (IReadOnlyList<TraceTick> Ticks, PortedSimulation Sim) Replay()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", Fixture);
        Assert.True(File.Exists(path), $"missing {path}");

        var trace = Trace.Decode(File.ReadAllBytes(path));
        var ticks = TraceParser.Parse(trace, GameModes.ShapeOf(MapLoader(trace.Header.Map)), out _, out _);

        var sim = new PortedSimulation(MapLoader);
        new Replayer(sim) { Compare = Fields.Trajectory }
            .Replay(Fixture, trace.Header.Map, ticks, ReplayMode.OpenLoop, stopAtFirstBadTick: false);

        return (ticks, sim);
    }

    /// <summary>
    /// The recording plays real rounds: 561 and 591 ticks, won by both teams.
    /// </summary>
    /// <remarks>
    /// Asserted first so the comparison below cannot pass by the recording being
    /// degenerate too.
    /// </remarks>
    [Fact]
    public void TheRecordingPlaysRoundsOfHundredsOfTicks()
    {
        var (ticks, _) = Replay();

        var starts = ticks
            .Where(t => t.Events.Any(m => m.Type == MessageType.ArenaStartRound))
            .Select(t => t.Index)
            .ToArray();

        var ends = ticks
            .Where(t => t.Events.Any(m => m.Type == MessageType.ArenaEndRound))
            .Select(t => t.Index)
            .ToArray();

        Assert.Equal(3, ends.Length);

        // Each end follows the start before it by hundreds of ticks.
        foreach (var end in ends)
        {
            var start = starts.Where(s => s < end).Max();
            Assert.True(end - start > 400, $"round at t{start} lasted only {end - start} ticks");
        }

        // And both sides win one, so "always Red" below is not the recording's.
        var winners = ticks
            .SelectMany(t => t.Events)
            .Where(m => m.Type == MessageType.ArenaEndRound && m.Payload.Length >= 1)
            .Select(m => m.Payload[0])
            .Distinct()
            .ToArray();

        Assert.Equal(2, winners.Length);
    }

    /// <summary>
    /// ⚠️ <b>The port ends every round one tick after starting it, and gives
    /// every one of them to Red.</b>
    /// </summary>
    /// <remarks>
    /// If this test fails, the replay has learned to run an Arena round and that
    /// is good news: delete the pin, re-measure the two
    /// <see cref="ClientPredictionGateTests"/> arena rows, and say so.
    /// </remarks>
    [Fact]
    public void ThePortEndsEveryReplayedRoundOnTheTickAfterItStarts()
    {
        var (_, sim) = Replay();

        var starts = sim.ArenaTransitions
            .Where(t => t.Kind == MessageType.ArenaStartRound)
            .Select(t => t.Tick)
            .ToArray();

        var ends = sim.ArenaTransitions
            .Where(t => t.Kind == MessageType.ArenaEndRound)
            .ToArray();

        Assert.True(starts.Length >= 5, $"only {starts.Length} round starts");
        Assert.Equal(starts.Length, ends.Length);

        for (var i = 0; i < ends.Length; i++)
        {
            Assert.Equal(starts[i] + 1, ends[i].Tick);
            Assert.Equal(Team.Red, ends[i].Winner);
        }

        // The 601-tick cycle: 300 of scoreboard, 300 of setup, one of "round".
        for (var i = 1; i < starts.Length; i++)
            Assert.Equal(601, starts[i] - starts[i - 1]);
    }
}
