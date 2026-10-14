using GG2.Bots;
using GG2.Maps;
using GG2.Protocol;
using GG2.Simulation;
using Xunit.Abstractions;

namespace GG2.Tests;

/// <summary>
/// A heavy climbing ctf_truefort's staircase to the intel, and the shuttle it
/// used to do on every landing.
/// </summary>
/// <remarks>
/// <b>Reported from play</b>: a heavy rocks back and forth on each landing before
/// eventually making the jump to the next flight. It is a run-up that cannot
/// deliver, retried until a re-plan routes round it — <c>docs/TRAPS-BOTS.md</c>,
/// "a gate that fires before the run is finished".
/// <para>
/// ⚠️⚠️ <b>Every measurement here is a SWEEP, and one run is not a measurement of
/// this leg.</b> A single (stagger, start x) pair put the fixed arm 240 ticks
/// behind the baseline and was read as a regression; the baseline's own mean over
/// the sweep is 2,064 against that pair's lucky 1,851, and the fixed arm is in
/// fact faster at every order statistic. A follower is chaotic in its start
/// state — arriving at a landing a few ticks earlier changes which re-plan phase
/// catches it — so a difference that does not survive the sweep is not a
/// difference.
/// </para>
/// <para>
/// <b>The class is the variable and it is not decoration.</b> A scout clears edge
/// 597-&gt;557 from a standstill and a soldier clears it at 2.5 px/tick, so
/// neither reaches the branch this gates. The soldier is carried as the control:
/// it must NOT move.
/// </para>
/// </remarks>
public sealed class BotStaircaseTests(ITestOutputHelper output)
{
    private const string Map = "ctf_truefort";

    /// <summary>One walk from the red spawn to the blue intel.</summary>
    /// <param name="stagger">
    /// Shifts the re-plan phase, which is what decides whether a bot is caught
    /// mid-approach. Sweeping it is most of what makes this a distribution.
    /// </param>
    private static int Walk(PlayerClass playerClass, int stagger, double startX, out int worstStreak)
    {
        var level = LevelMap.FromPng(Map, ReferenceTree.ReadMap(Map));
        var world = new CollisionWorld(level);
        var state = default(WorldState);

        state.GameMode = GameModes.Of(level);
        state.TimeLimitMinutes = 15;
        state.RespawnTime = 0;
        state.TdmInvulnerabilityTicks = 0;

        for (var i = 0; i < TeamBalance.ClassCount; i++)
            state.ClassLimits[i] = 255;

        Intel.Place(ref state, level);
        Rounds.Start(ref state);
        state.AddPlayer(0);

        var spawns = new SpawnPoints(level);
        var graph = NavCache.Load(ReferenceTree.NavCacheDirectory, Map, 1);

        const int Slot = 1;
        state.AddPlayer(Slot);
        state.Spawn(spawns, Slot, playerClass, Team.Red, spawnpointId: 0, group: 0);
        state.Characters[Slot].X = startX;
        state.Characters[Slot].Y = 864;

        var bot = new ScriptedBot(graph, new NavQuery(Team.Red), BotSkill.ForTier(5), stagger)
        {
            Objective = new BotObjective(graph, spawns),
            Role = BotRole.Attack,
            SpreadX = BotRoles.SpreadX(1),
            AliveLast = true,
        };

        var events = default(TickEvents);
        Span<PlayerInput> inputs = stackalloc PlayerInput[WorldState.MaxSlots];

        var worst = 0;
        var run = 0;
        var inRunup = false;
        var lastEdge = (-1, -1);

        for (var tick = 0; tick < 6000; tick++)
        {
            inputs.Clear();
            inputs[Slot] = bot.Poll(new WorldView(in state, world), Slot, tick);

            // A run-up STARTING is the event; how long it lasts is not the
            // pathology. Consecutive starts for the same edge are the shuttle.
            var live = bot.Path.RunupAt >= 0;

            if (live && !inRunup)
            {
                var edge = (bot.Path.EdgeFrom, bot.Path.EdgeTo);
                run = edge == lastEdge ? run + 1 : 1;
                lastEdge = edge;
                worst = Math.Max(worst, run);
            }

            inRunup = live;

            MatchSimulation.Tick(world, ref state, inputs[..(Slot + 1)], ref events, spawns);

            if (state.IsAlive(Slot) && state.Characters[Slot].CarryingIntel)
            {
                worstStreak = worst;
                return tick;
            }
        }

        worstStreak = worst;
        return -1;
    }

    private sealed record Sweep(int Arrived, int NeverArrived, double MeanTicks, int Shuttled);

    private Sweep Run(PlayerClass playerClass)
    {
        ReferenceTree.RequireNavCache();

        var times = new List<int>();
        var shuttled = 0;
        var never = 0;

        for (var stagger = 0; stagger < 8; stagger++)
        {
            foreach (var startX in new[] { 352.0, 384, 416, 448 })
            {
                var ticks = Walk(playerClass, stagger, startX, out var streak);

                // ⚠️ A run that never arrives is counted but its TICKS are not,
                // and the count is asserted separately. Folding 6,000 into the
                // mean would let a change that strands bots read as a slow one.
                if (ticks < 0)
                    never++;
                else
                    times.Add(ticks);

                if (streak > 2)
                    shuttled++;
            }
        }

        var sweep = new Sweep(
            times.Count, never, times.Count == 0 ? 0 : times.Average(), shuttled);

        output.WriteLine(
            $"{playerClass}: arrived {sweep.Arrived}, never {sweep.NeverArrived}, "
            + $"mean {sweep.MeanTicks:0} ticks, shuttled (>2 run-ups in a row) {sweep.Shuttled}");

        return sweep;
    }

    /// <summary>
    /// A heavy does not shuttle on a landing, over the whole sweep.
    /// </summary>
    /// <remarks>
    /// On the unfixed code 23 of 24 arriving runs take more than two run-ups in a
    /// row for one edge, the worst taking eleven; after, none do. That is the
    /// reported behaviour and it is what this gates.
    /// <para>
    /// The arrival count is the control: a follower that stopped walking would
    /// satisfy the shuttle assertion by never starting a run-up. Eight of the
    /// thirty-two starts never reach the intel on BOTH arms — a property of those
    /// start positions rather than of the change — so the number is pinned rather
    /// than required to be zero.
    /// </para>
    /// </remarks>
    [Fact]
    public void AHeavyDoesNotShuttleOnTheStaircaseToTheIntel()
    {
        var sweep = Run(PlayerClass.Heavy);

        Assert.Equal(0, sweep.Shuttled);
        Assert.Equal(24, sweep.Arrived);

        // Not pinned exactly: it is a distribution, and pinning a mean would make
        // every unrelated bot change a re-record here. What must not happen is a
        // drift back to the shuttling arm's cost, which was 2,064.
        Assert.True(
            sweep.MeanTicks < 1950,
            $"the heavy averaged {sweep.MeanTicks:0} ticks against the shuttling arm's 2,064");
    }

    /// <summary>
    /// The control: a soldier, which clears the same arcs and must not move.
    /// </summary>
    /// <remarks>
    /// It never reaches the branch this change touches. If this starts moving, the
    /// short branch is firing for a class that was never short — which is the way
    /// this fix would go wrong.
    /// </remarks>
    [Fact]
    public void ASoldierIsUnaffectedByTheStaircaseFix()
    {
        var sweep = Run(PlayerClass.Soldier);

        Assert.Equal(0, sweep.Shuttled);
        Assert.Equal(24, sweep.Arrived);
        // 1590-1625 until held flight (N1, 2026-10-11) took the stairs' plain
        // jumps at speed: about 1,436 since, and 1,386 since N4's two-phase
        // plans. Still no shuttle and every start arrives. About 1,332 since
        // N12 flew the falls between the flights held.
        Assert.InRange(sweep.MeanTicks, 1310, 1355);
    }
}
