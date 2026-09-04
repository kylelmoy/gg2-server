using Gg2.Bots;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// <b>A live Arena round has people alive in it.</b>
/// </summary>
/// <remarks>
/// <c>Player.events/Alarm 5.xml</c> is <c>if (spawningAllowed()) PlayerSpawn();</c>
/// — the guard belongs on the <b>alarm</b>, not inside <c>PlayerSpawn</c>: Arena's
/// <c>respawnEveryone()</c> calls <c>PlayerSpawn()</c> <em>directly</em>, precisely
/// so it can spawn a whole round's worth of characters at a moment
/// <c>spawningAllowed()</c> refuses — <c>doEventArenaStartRound</c> sets the HUD
/// to <c>ARENA_STATE_ROUND_PROPER</c> before it calls the sweep. Put the guard on
/// <c>PlayerSpawn</c> instead and every Arena round begins with nobody alive: an
/// empty team wins and the round ends on the tick after it started.
/// <para>
/// The arena fixtures gate the recording's framing and the port's trajectories,
/// neither of which catches this. <c>eval</c> reports a match to a decision as a
/// win regardless of how long the round lasted, so an instant round reaches a cap
/// limit exactly like a real one — a match that ends is not a match that
/// happened, and these tests ask how long a round lasted rather than how it ended.
/// </para>
/// <para>
/// <c>ArenaReplayLoopTests</c> covers a separate degeneracy: a replay may only
/// spawn characters the recording announced, which is unrelated to this guard.
/// </para>
/// </remarks>
public sealed class ArenaRoundLoopTests
{
    private static HeadlessMatch Match(uint seed) => HeadlessMatch.Load(
        new HeadlessMatchOptions(
            Map: "arena_lumberyard",
            Area: 1,
            Bots: 8,
            Difficulty: 3,
            CapLimit: 5,
            TimeLimitMinutes: 15,
            KillLimit: 0,
            RespawnTimeSeconds: 5,
            MaxTicks: 4000,
            Seed: seed),
        ReferenceTree.IncludedFiles,
        ReferenceTree.NavCacheDirectory);

    /// <summary>
    /// The round that starts is fought, not conceded on the next tick.
    /// </summary>
    /// <remarks>
    /// The recording this is measured against plays rounds of 561 and 591 ticks
    /// (<c>reference-arena_lumberyard-closedarena.trace</c>, ends at t1342,
    /// t2533 and t3601). The bound here is deliberately loose — a round is a
    /// fight between bots and its length is not pinned — but anything under a
    /// hundred ticks is the bug.
    /// </remarks>
    [Fact]
    public void ARoundLastsHundredsOfTicksAndNotOne()
    {
        var match = Match(7);
        var start = -1;
        var length = -1;

        for (var tick = 0; tick < 4000 && length < 0; tick++)
        {
            match.Step();

            if (match.Events.ArenaRoundStarted)
                start = match.Tick;

            if (match.Events.ArenaRoundEnded && start >= 0)
                length = match.Tick - start;
        }

        Assert.True(start >= 0, "no arena round ever started");
        Assert.True(length > 100, $"the round lasted {length} ticks");
    }

    /// <summary>
    /// On the tick a round starts, every player has a character.
    /// </summary>
    /// <remarks>
    /// This is what <c>respawnEveryone()</c> is for. Asserted on the tick rather
    /// than a few ticks later, because <c>Timers.SpawnNow</c> exists precisely so
    /// the spawn is not one tick late.
    /// </remarks>
    [Fact]
    public void EveryPlayerIsAliveOnTheTickTheRoundStarts()
    {
        var match = Match(7);

        for (var tick = 0; tick < 4000; tick++)
        {
            match.Step();

            if (!match.Events.ArenaRoundStarted)
                continue;

            var players = 0;
            var alive = 0;

            for (var slot = 0; slot < WorldState.MaxSlots; slot++)
            {
                if (!match.State.HasPlayer[slot])
                    continue;

                if (match.State.Players[slot].Team is Team.Red or Team.Blue)
                {
                    players++;
                    if (match.State.IsAlive(slot))
                        alive++;
                }
            }

            Assert.True(players > 0, "the round started with nobody on a team");
            Assert.Equal(players, alive);
            return;
        }

        Assert.Fail("no arena round ever started");
    }

    /// <summary>
    /// The control for the two above: <c>spawningAllowed()</c> still refuses the
    /// <b>alarm</b>, so nobody comes back mid-round.
    /// </summary>
    /// <remarks>
    /// The fix is not "delete the guard": Arena's whole shape is that a death is
    /// final until the next round. The recording agrees: three deaths inside the
    /// round at t1342 and zero spawns between its start and its end.
    /// </remarks>
    [Fact]
    public void NobodyRespawnsInsideAnArenaRound()
    {
        var match = Match(7);
        var inRound = false;
        var deaths = 0;
        var spawns = 0;

        for (var tick = 0; tick < 4000; tick++)
        {
            match.Step();

            if (match.Events.ArenaRoundStarted)
            {
                inRound = true;
                continue;
            }

            if (match.Events.ArenaRoundEnded)
                break;

            if (!inRound)
                continue;

            for (var slot = 0; slot < WorldState.MaxSlots; slot++)
            {
                if (match.Events.Died[slot])
                    deaths++;

                if (match.Events.Spawned[slot])
                    spawns++;
            }
        }

        Assert.True(deaths > 0, "nobody died, so the claim below is vacuous");
        Assert.Equal(0, spawns);
    }
}
