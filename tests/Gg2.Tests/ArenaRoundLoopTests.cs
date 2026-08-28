using Gg2.Bots;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// ⚠️ <b>A live Arena round has people alive in it.</b> The gate that did not
/// exist, and the bug it would have caught ran for two phases.
/// </summary>
/// <remarks>
/// <b>What was wrong.</b> <c>Player.events/Alarm 5.xml</c> is
/// <c>if (spawningAllowed()) PlayerSpawn();</c> — the guard is on the
/// <b>alarm</b>. This port had moved it inside <c>PlayerSpawn</c>, which reads
/// like the same thing and is not: Arena's <c>respawnEveryone()</c> calls
/// <c>PlayerSpawn()</c> <em>directly</em>, precisely so that it can spawn a
/// whole round's worth of characters at a moment
/// <c>spawningAllowed()</c> refuses — <c>doEventArenaStartRound</c> sets the HUD
/// to <c>ARENA_STATE_ROUND_PROPER</c> before it calls the sweep.
/// <para>
/// So every Arena round in this port began with <b>nobody alive</b>, an empty
/// team is a win, and every round ended on the tick after it started: a
/// 601-tick cycle of scoreboard, setup, and one tick of "round". A whole
/// gamemode, silently, since phase 6.
/// </para>
/// <para>
/// ⚠️ <b>Nothing caught it, and the reason is worth more than the bug.</b> The
/// arena fixtures gate the <em>recording's</em> framing and the port's
/// trajectories, both of which were fine. <c>eval</c> ran arena matches to a
/// decision and reported them as wins, because five instant rounds reach a cap
/// limit exactly like five real ones. **A match that ends is not a match that
/// happened**, and no test asked how long a round lasted.
/// </para>
/// <para>
/// ⚠️ It was found only because <c>ArenaReplayLoopTests</c>' artefact — real,
/// and still real — was blamed for all of it. The replayed loop is degenerate
/// for its own reason (a replay may only spawn characters the recording
/// announced), and that masked a live bug sitting underneath. <b>Two causes with
/// one symptom, and the first one found stopped the search.</b>
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
    /// fight between bots and its length is not pinned — but <b>anything under a
    /// hundred ticks is the bug</b>, which used to read as one.
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
    /// ⚠️ <b>The direct assertion, and the one that names the cause:</b> on the
    /// tick a round starts, every player has a character.
    /// </summary>
    /// <remarks>
    /// This is what <c>respawnEveryone()</c> is for, and it is the line the
    /// misplaced guard broke. Asserted on the tick rather than a few ticks
    /// later, because <c>Timers.SpawnNow</c> exists precisely so that the
    /// spawn is not one tick late.
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
    /// ⚠️ <b>Without this the fix would be "delete the guard", which is wrong in
    /// the other direction</b> — Arena's whole shape is that a death is final
    /// until the next round. The recording agrees: three deaths inside the round
    /// at t1342 and zero spawns between its start and its end.
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
