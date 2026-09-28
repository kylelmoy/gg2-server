using Gg2.Bots;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// <b>A match that ends is not a match that happened.</b> The gate this repo
/// did not have.
/// </summary>
/// <remarks>
/// <b>Where this came from.</b> An Arena round can begin with nobody alive and
/// end on the next tick (see <see cref="ArenaRoundLoopTests"/>), and <c>eval</c>
/// reports such a match as a clean win — five instant rounds reach a cap limit
/// exactly like five real ones. Nothing else asks whether anything had happened
/// inside them.
/// <para>
/// So this asserts the cheapest possible thing about a match, on one map per
/// gamemode: <b>the bots move.</b> Movement rather than kills, because a kill
/// depends on two bots finding each other inside the budget and a stationary
/// roster is unambiguous.
/// </para>
/// </remarks>
public sealed class MatchActuallyHappensTests
{
    private const int Ticks = 600;

    private static HeadlessMatch Match(string map) => HeadlessMatch.Load(
        new HeadlessMatchOptions(
            Map: map,
            Area: 1,
            Bots: 8,
            Difficulty: 3,
            CapLimit: 5,
            TimeLimitMinutes: 5,
            KillLimit: 20,
            RespawnTimeSeconds: 5,
            MaxTicks: Ticks,
            Seed: 11),
        ReferenceTree.MapsDirectory,
        ReferenceTree.NavCacheDirectory);

    /// <summary>
    /// Movement, deaths, and how much the teams stack up.
    /// </summary>
    /// <remarks>
    /// <c>Died</c> is read for EVERY slot, not only live ones: a slot that just
    /// died is no longer <c>Alive</c>, so counting inside the live branch counts
    /// exactly zero deaths for ever.
    /// </remarks>
    private static (double[] Moved, int Deaths, double Clumped) RunFully(string map)
    {
        var match = Match(map);
        var start = new double[WorldState.MaxSlots];
        var moved = new double[WorldState.MaxSlots];
        var deaths = 0;
        var clumpSum = 0.0;
        var clumpN = 0;

        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
        {
            if (match.State.Alive[slot])
                start[slot] = match.State.Characters[slot].X;
        }

        for (var tick = 0; tick < Ticks; tick++)
        {
            match.Step();

            for (var slot = 0; slot < WorldState.MaxSlots; slot++)
            {
                if (match.Events.Died[slot])
                    deaths++;

                if (!match.State.Alive[slot])
                    continue;

                if (start[slot] == 0)
                    start[slot] = match.State.Characters[slot].X;

                moved[slot] = Math.Max(moved[slot], Math.Abs(match.State.Characters[slot].X - start[slot]));
            }

            // Sampled rather than per tick: it is O(slots²) and a bot cannot move
            // far in fifty ticks.
            if (tick % 50 != 0)
                continue;

            for (var a = 0; a < WorldState.MaxSlots; a++)
            {
                if (!match.State.Alive[a])
                    continue;

                for (var b = a + 1; b < WorldState.MaxSlots; b++)
                {
                    if (!match.State.Alive[b]
                        || match.State.Characters[a].Team != match.State.Characters[b].Team)
                    {
                        continue;
                    }

                    clumpSum += Math.Abs(match.State.Characters[a].X - match.State.Characters[b].X) < 64 ? 1 : 0;
                    clumpN++;
                }
            }
        }

        return (moved, deaths, clumpN > 0 ? clumpSum / clumpN : 0);
    }

    /// <summary>How far each live bot got from where it started, in x.</summary>
    private static double[] Run(string map)
    {
        var match = Match(map);
        var start = new double[WorldState.MaxSlots];
        var moved = new double[WorldState.MaxSlots];

        match.Step();

        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
            if (match.State.IsAlive(slot))
                start[slot] = match.State.Characters[slot].X;

        for (var tick = 0; tick < Ticks && match.Outcome == MatchOutcome.Running; tick++)
        {
            match.Step();

            for (var slot = 0; slot < WorldState.MaxSlots; slot++)
                if (match.State.IsAlive(slot))
                    moved[slot] = Math.Max(moved[slot], Math.Abs(match.State.Characters[slot].X - start[slot]));
        }

        return moved;
    }

    /// <summary>
    /// On a map with an objective, every bot goes somewhere.
    /// </summary>
    /// <remarks>
    /// One map per gamemode that has one. The bound is deliberately weak — a
    /// hundred pixels in twenty seconds — because what this catches is a roster
    /// that never moves at all, and anything tighter would be pinning bot
    /// quality rather than gating a match.
    /// </remarks>
    [Theory]
    [InlineData("ctf_truefort")]   // Capture the Flag
    [InlineData("ctf_avanti")]     // Invasion
    [InlineData("koth_valley")]    // King of the Hill
    [InlineData("dkoth_atalia")]   // Dual King of the Hill
    [InlineData("cp_egypt")]       // Control Points
    [InlineData("gen_destroy")]    // Generator
    [InlineData("arena_lumberyard")] // Arena — the one this file exists for
    public void EveryBotGoesSomewhere(string map)
    {
        var moved = Run(map);
        var live = moved.Count(m => m > 0);

        Assert.True(live >= 4, $"{map}: only {live} bots moved at all");
        Assert.True(moved.Count(m => m > 100) >= 4, $"{map}: only {moved.Count(m => m > 100)} bots went anywhere");
    }

    /// <summary>
    /// <b>Team Deathmatch has learned to play itself.</b>
    /// </summary>
    /// <remarks>
    /// <c>BotObjective.Decide</c> chooses between the intel, a generator and the
    /// control points; Team Deathmatch has none of the three, so it falls
    /// through to a fourth branch, <c>BotObjective.Deathmatch</c>: hunt the
    /// nearest enemy, and — the part that makes it work rather than merely
    /// move — stand at a <em>spot</em> anchored on that enemy at the class's own
    /// band, never on the enemy itself. Walking onto a target parks a soldier
    /// inside its own splash-safe band, where <c>BotClassKeys</c> correctly
    /// refuses to fire and deliberately does not move it away, because "where this
    /// bot stands is the objective layer's decision rather than this one's".
    /// <para>
    /// <b>Deaths, not only movement.</b> A kill depends on two bots finding each
    /// other, which is the right bar for "did anything happen" and the wrong one
    /// here, because hunting is precisely the claim being tested. Eight bots
    /// patrolling past each other would satisfy a movement test and would not be
    /// a deathmatch.
    /// </para>
    /// </remarks>
    /// <summary>
    /// The hill modes' own scoreboard actually moves.
    /// </summary>
    /// <remarks>
    /// <b>A liveness test on the wrong liveness misses dual KotH.</b>
    /// <see cref="EveryBotGoesSomewhere"/> checks that the bots MOVE — and in
    /// dkoth they moved, briskly, to their own team's hill, which scores nothing.
    /// Every bot went somewhere and the mode could not be won: zero decided
    /// matches on both dkoth maps at 5, 10, 20 and 40 minutes, with the hold
    /// clocks never advancing by a single tick.
    /// <para>
    /// <b>The general rule</b>: a gamemode needs a liveness assertion on
    /// <b>something only that mode moves</b>. Movement is shared by every mode,
    /// so it cannot distinguish a mode that is working from one that is being
    /// played backwards. Here that something is the hold clock — the mode's own
    /// scoreboard.
    /// </para>
    /// <para>
    /// <b>The single-hill maps are the control</b>, and they are not decoration:
    /// they pass throughout, so an assertion that failed on all four would be
    /// measuring the harness rather than the bug.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("dkoth_atalia")]
    [InlineData("dkoth_sixties")]
    [InlineData("koth_valley")]
    [InlineData("koth_harvest")]
    public void TheHillModesScoreboardMoves(string map)
    {
        // Its OWN options, with the kill limit out of the way. The shared
        // Match() helper caps at 20 kills, and with that in place a broken dkoth
        // match still ENDS — on kills — which let the first version of this test
        // pass through an `|| Outcome != Running` escape clause without the clock
        // ever moving. The mutation check caught it; nothing else would have.
        var match = HeadlessMatch.Load(
            HeadlessMatchOptions.Default with
            {
                Map = map,
                Bots = 8,
                Seed = 4242,
                CapLimit = 1,
                TimeLimitMinutes = 10,
                KillLimit = 4000,
                MaxTicks = 30 * 60 * 3,
            },
            ReferenceTree.MapsDirectory,
            ReferenceTree.NavCacheDirectory);

        var started = Math.Min(match.State.RedTimer, match.State.BlueTimer);
        var ownerChanges = 0;
        var lastRed = match.State.ControlPointCount > 0 ? match.State.ControlPoints[0].Team : Team.Spectator;

        // Three minutes: past the 900-tick pregame with time for a hill to fall
        // and a clock to run. Not the full 180-second hold — this asserts the
        // scoreboard MOVES, not that somebody wins.
        while (match.Outcome == MatchOutcome.Running)
        {
            match.Step();

            if (match.State.ControlPointCount > 0 && match.State.ControlPoints[0].Team != lastRed)
            {
                ownerChanges++;
                lastRed = match.State.ControlPoints[0].Team;
            }
        }

        var now = Math.Min(match.State.RedTimer, match.State.BlueTimer);

        // No escape clause. A hold clock that did not move is the failure,
        // whatever else the match did.
        Assert.True(
            now < started,
            $"{map}: neither hold clock advanced in three minutes "
            + $"({started} -> {now}), with {ownerChanges} hill owner changes");
    }

    /// <summary>
    /// On a symmetrical Control Points map the frontline moves past the opening
    /// fight.
    /// </summary>
    /// <remarks>
    /// <b>Reported from play on cp_egypt: bots take the middle point and then
    /// never advance.</b> The whole ten-minute match produced exactly one capture
    /// on every seed tried, and nothing in the suite noticed — the match ends, on
    /// the clock, with a winner, and every scoreboard assertion is satisfied by a
    /// team that took one point in the first minute and stood on it.
    /// <para>
    /// <b>So the assertion is which points changed hands, not how many captures
    /// there were.</b> A count is satisfied by the middle point flipping back and
    /// forth all match, which is the same stalemate one layer up. A point OTHER
    /// than the one that starts neutral changing owner means a team pushed into
    /// ground the other side held, which is the thing the mode is about.
    /// </para>
    /// <para>
    /// <b>cp_egypt and not cp_dirtbowl.</b> Dirtbowl is attack/defend with two
    /// points, where a red point is locked unconditionally and blue never takes
    /// anything back — one capture is a legitimate whole match there, and the same
    /// assertion would be pinning the defending team's skill instead.
    /// </para>
    /// <para>
    /// <b>A count over eight seeds rather than three fixed ones.</b> Whether the
    /// team pushing the point beyond the middle finishes the capture is close-run
    /// on a given seed — on the seeds that fail it, that team still stands inside
    /// the next zone for well over a thousand bot-ticks — so any small fixed set
    /// pins a coin flip and goes red on a change that improved the aggregate.
    /// Measured at 12 bots over a full ten-minute match: <b>0 of 8</b> seeds
    /// advanced on the code that had the bug, and 7 of 8 advance now.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheControlPointsFrontlineMovesPastTheOpeningFight()
    {
        uint[] seeds = [11, 4242, 90210, 7, 31337, 555, 2024, 88];
        var advancedOn = new List<uint>();
        var stalledOn = new List<string>();

        foreach (var seed in seeds)
            if (FrontlineAdvanced(seed, out var detail))
                advancedOn.Add(seed);
            else
                stalledOn.Add(detail);

        // Six of eight, not eight of eight. Whether a particular seed's losing team
        // completes a capture on the point beyond the middle is close-run — the
        // attacking team stands in that zone for over a thousand bot-ticks on seeds
        // that never take it — so a fixed handful of seeds pins a coin flip. The
        // threshold is the alarm: on the code that had the bug NONE of these eight
        // advanced, and both sides of the goal-oscillation fix score seven.
        Assert.True(
            advancedOn.Count >= 6,
            $"only {advancedOn.Count} of {seeds.Length} seeds moved the frontline past the "
            + $"opening fight — the teams took the middle and stopped. "
            + string.Join(" | ", stalledOn));
    }

    private static bool FrontlineAdvanced(uint seed, out string detail)
    {
        var match = HeadlessMatch.Load(
            HeadlessMatchOptions.Default with
            {
                Map = "cp_egypt",
                Bots = 12,
                Seed = seed,
                TimeLimitMinutes = 10,
                KillLimit = 4000,
                MaxTicks = 30 * 60 * 10,
            },
            ReferenceTree.MapsDirectory,
            ReferenceTree.NavCacheDirectory);

        var count = match.State.ControlPointCount;
        Assert.Equal(5, count);

        var owners = new Team[count];
        var changed = new bool[count];
        var neutralAtStart = -1;

        for (var i = 0; i < count; i++)
        {
            ref readonly var point = ref match.State.ControlPoints[i];
            owners[i] = point.Owned ? point.Team : Team.Spectator;

            if (!point.Owned)
                neutralAtStart = i;
        }

        Assert.True(
            neutralAtStart >= 0,
            "no point started neutral, so there was no opening fight to move past");

        while (match.Outcome == MatchOutcome.Running)
        {
            match.Step();

            for (var i = 0; i < count; i++)
            {
                ref readonly var point = ref match.State.ControlPoints[i];
                var now = point.Owned ? point.Team : Team.Spectator;

                if (now == owners[i])
                    continue;

                owners[i] = now;
                changed[i] = true;
            }
        }

        var advanced = false;

        for (var i = 0; i < count; i++)
            if (changed[i] && i != neutralAtStart)
                advanced = true;

        detail = advanced
            ? string.Empty
            : $"seed {seed}: only the point that started neutral ({neutralAtStart}) ever changed hands "
                + $"in {match.Result.Ticks} ticks, final owners {string.Join("/", owners)}";

        return advanced;
    }

    [Fact]
    public void TeamDeathmatchBotsHuntEachOtherAndFight()
    {
        var (moved, deaths, _) = RunFully("tdm_mantic");

        Assert.Equal(8, moved.Count(m => m > 0));
        Assert.True(
            moved.Count(m => m > 100) >= 6,
            $"only {moved.Count(m => m > 100)} of the eight went anywhere");

        // A threshold rather than an exact pin, matching this file: it answers
        // "did a deathmatch happen", not "what did it cost". The three measured
        // seeds give 4, 7 and 4 at this file's 600 ticks, against 0 before.
        Assert.True(deaths >= 3, $"tdm_mantic ran {Ticks} ticks and produced only {deaths} deaths");
    }

    /// <summary>
    /// <b>And they do NOT form a conga line</b>, which was the stated objection
    /// to giving Team Deathmatch a goal at all.
    /// </summary>
    /// <remarks>
    /// <c>docs/TRAPS-BOTS.md</c> recorded "walk at the nearest enemy is the obvious
    /// answer and is how you get eight bots in a conga line" as the reason not to
    /// do it. <b>Measured, and it is the other way round.</b> Each bot picks its
    /// <em>own</em> nearest enemy and then a spot that prefers to walk less and
    /// rewards height, so they distribute rather than stack.
    /// <para>
    /// <b>Fraction of live same-team pairs within 64px of each other, sampled
    /// every 50 ticks, three seeds</b>:
    /// </para>
    /// <para>
    /// <c>gg_debug</c> — the one-room map where the bug was invisible and where
    /// piling up is easiest — went from <b>1.00 / 0.76 / 0.98</b> to
    /// <b>0.73 / 0.54 / 0.63</b>, with deaths across the three seeds <b>42 → 50</b>.
    /// The bots spread out AND killed each other more, which is the opposite of
    /// the trade the objection predicted.
    /// </para>
    /// <para>
    /// <c>gg_debug</c> and not <c>tdm_mantic</c>, deliberately: on a large map
    /// the before-figure measures eight bots standing on their spawns, which is
    /// not a comparison. The one-room map is where a conga line would actually
    /// show, and it is the map the original bug hid behind.
    /// </para>
    /// </remarks>
    [Fact]
    public void TeamDeathmatchBotsSpreadOutRatherThanStacking()
    {
        var (_, _, clumped) = RunFully("gg_debug");

        // 0.85 rather than something tighter: seed 11 reads 0.73 now and 1.00
        // before, so this separates them with margin at both ends. A tighter bound
        // would be measuring this seed's particular geometry.
        Assert.True(
            clumped < 0.85,
            $"{clumped:0.00} of live same-team pairs stood within 64px of each other; before the "
            + "deathmatch goal this seed read 1.00, so this is the conga line the goal was "
            + "supposed to avoid");
    }

    /// <summary>
    /// The control: the same gamemode on a one-room map looks fine, which is
    /// exactly why nothing caught the line above.
    /// </summary>
    [Fact]
    public void TheSameGamemodeOnASmallMapHidesIt()
    {
        var moved = Run("gg_debug");

        Assert.True(moved.Count(m => m > 0) >= 4, "even gg_debug's bots stood still");
    }

    /// <summary>
    /// No bot stands on one pixel for half a minute.
    /// </summary>
    /// <remarks>
    /// <b><see cref="EveryBotGoesSomewhere"/> cannot see this and never could.</b>
    /// It measures the first 600 ticks and asks whether four of eight bots went
    /// anywhere; a bot becomes a statue <em>after</em> it arrives, and four movers
    /// out of eight is a pass with half the roster motionless. Reported from play
    /// as bots standing still holding the trigger.
    /// <para>
    /// <b>The cause was structural, not a tuning miss.</b> Arrival frees the path
    /// and clears the goal, so an arrived bot presses no movement key — and the
    /// objective layer then re-decides the same point, which resolves to the node
    /// the bot is standing on. Nothing re-tasked it for the rest of the round.
    /// Defenders showed it because their objective never moves, but any bot whose
    /// goal is a place to stand parks the same way. <c>ScriptedBot.ParkedTicks</c>
    /// and <c>BotObjective.Decide</c>'s <c>reposting</c> are the fix.
    /// </para>
    /// <para>
    /// <b>Measured across all 22 shipped maps, two seeds, twelve bots, 4,000
    /// ticks each, counting only ticks the round was still running: 98 of 528
    /// bot-matches froze for 900+ consecutive ticks, and 7 do now.</b> The worst
    /// run on these maps went 3,945 ticks to 565.
    /// </para>
    /// <para>
    /// The maps here are the modes the fixes actually repaired — CTF, KotH, Dual
    /// KotH, Arena, and Invasion with its 1,800-tick setup timer, which was the
    /// case that needed a re-post to be allowed on a goal that has to be touched.
    /// <b>The four maps still carrying a statue are deliberately not listed</b>:
    /// <c>cp_egypt</c> (3), <c>koth_harvest</c> (2), <c>dkoth_atalia</c> (1) and
    /// <c>tdm_mantic</c> (1), where the bot cannot route anywhere at all from
    /// where it is standing. That is a nav-reachability fault rather than an
    /// objective one and is written up in <c>docs/TRAPS-BOTS.md</c>. Listing them
    /// would mean weakening the bound until it proved nothing.
    /// </para>
    /// <para>
    /// 900 ticks is thirty seconds. A bot holding a firing position between
    /// re-posts reads 313-565 here, so this separates the two with margin at both
    /// ends rather than pinning bot quality.
    /// </para>
    /// <para>
    /// ⛔ <b>It stops at the decision, and that is not tidiness.</b>
    /// <see cref="HeadlessMatch.Step"/> returns immediately once
    /// <c>RoundDecided</c>, so the world stops advancing and <em>every</em> bot
    /// reads as motionless for the rest of the budget — a finished match scored as
    /// a roster of statues. Measured: counting those ticks reported 50 frozen
    /// bot-matches across the shipped maps where 31 were real, all 18 of
    /// <c>gg_debug</c>'s being a match that decided at tick 1,355 and then sat
    /// still for 2,645 more. The three maps here do not decide inside 4,000 ticks
    /// today, so this guard changes no result — which is exactly why it would have
    /// been silent until a seed or a tuning change made one of them finish early,
    /// and then it would have failed naming a bot that was doing nothing wrong.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("ctf_truefort")]
    [InlineData("ctf_orange")]
    [InlineData("koth_valley")]
    [InlineData("ctf_avanti")]        // Invasion, and its setup timer
    [InlineData("dkoth_sixties")]     // Dual King of the Hill
    [InlineData("arena_lumberyard")]  // Arena, whose points lock for 1,800 ticks
    public void NoBotStandsStillForHalfAMinute(string map)
    {
        const int Length = 4000;
        const int Ceiling = 900;

        for (uint seed = 1; seed <= 2; seed++)
        {
            var match = HeadlessMatch.Load(
                HeadlessMatchOptions.Default with { Map = map, Bots = 12, Seed = seed },
                ReferenceTree.MapsDirectory,
                ReferenceTree.NavCacheDirectory);

            var lastX = new double[WorldState.MaxSlots];
            var lastY = new double[WorldState.MaxSlots];
            var alive = new int[WorldState.MaxSlots];
            var run = new int[WorldState.MaxSlots];
            var longest = new int[WorldState.MaxSlots];

            for (var tick = 0; tick < Length; tick++)
            {
                // The round is over; nothing below this is a bot standing still.
                if (match.Outcome != MatchOutcome.Running)
                    break;

                match.Step();

                for (var slot = 1; slot < WorldState.MaxSlots; slot++)
                {
                    // A dead bot is not a statue, and its slot is reused, so the
                    // run has to break rather than carry across the respawn.
                    if (!match.State.IsAlive(slot))
                    {
                        run[slot] = 0;
                        continue;
                    }

                    ref readonly var c = ref match.State.Characters[slot];
                    alive[slot]++;

                    if (alive[slot] > 1
                        && Math.Abs(c.X - lastX[slot]) < 0.01
                        && Math.Abs(c.Y - lastY[slot]) < 0.01)
                    {
                        run[slot]++;
                        longest[slot] = Math.Max(longest[slot], run[slot]);
                    }
                    else
                    {
                        run[slot] = 0;
                    }

                    lastX[slot] = c.X;
                    lastY[slot] = c.Y;
                }
            }

            for (var slot = 1; slot < WorldState.MaxSlots; slot++)
            {
                if (alive[slot] == 0)
                    continue;

                Assert.True(
                    longest[slot] < Ceiling,
                    $"{map} seed {seed}: {match.State.Players[slot].Class} in slot {slot} stood "
                    + $"motionless for {longest[slot]} consecutive ticks of {alive[slot]} it was "
                    + $"alive for, against a ceiling of {Ceiling}");
            }
        }
    }

    /// <summary>
    /// A bot whose route cannot be walked is given somewhere else to go.
    /// </summary>
    /// <remarks>
    /// <b>The sibling above cannot see this case, and that is the point.</b> It
    /// asks for EXACT immobility — under 0.01px between two ticks — and the statue
    /// this gate exists for is a bot whose follower alternates LEFT and RIGHT on a
    /// two-tick limit cycle, moving 0.66px each way against a wall it cannot get
    /// past. So the metric here is a 16px circle held over time rather than a
    /// frozen coordinate, which catches both shapes.
    /// <para>
    /// <b>What it is gating.</b> Every recovery in the bot layer used to be keyed
    /// on the PLAN failing, and a route that exists and cannot be walked fails
    /// nothing: A* returns a route, the follower presses a direction, the position
    /// changes, and <c>StuckTicks</c>, <c>ParkedTicks</c> and <c>_repostSkip</c>
    /// each read that as progress. The re-post that would have rescued the bot was
    /// then revoked by the very next objective tick, 23 ticks after it was issued —
    /// long before the bot could walk the 90px it was sent. See
    /// <c>ScriptedBot._repostUntil</c> and <c>_repostFromX</c>.
    /// </para>
    /// <para>
    /// <b>The two theory rows are the measured reproductions</b>, and the ceiling
    /// separates them from ordinary play at both ends. Over these 9,000 ticks:
    /// <c>ctf_2dfort</c> seed 11 went 8,390 to 346 and <c>ctf_avanti</c> seed 11
    /// went 8,640 to 763, while <c>ctf_orange</c> seed 2024 (567 to 470) and
    /// <c>ctf_truefort</c> seed 1 (322 to 590) barely moved — the last two are
    /// carried as controls so a ceiling that merely said "no bot ever holds
    /// still" would fail them too.
    /// </para>
    /// <para>
    /// ⚠️ <b>A bot legitimately holding a firing position reads in the hundreds
    /// here</b>, so 2,700 ticks — a minute and a half in one 16px circle — is not
    /// a bot-quality pin. It is far above honest play and far below the defect,
    /// which held whole ten-minute matches.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("ctf_2dfort", 11u)]   // a Heavy against the left wall at (79, 786)
    [InlineData("ctf_avanti", 11u)]   // a Heavy holding a 45-node route at leg 0
    [InlineData("ctf_orange", 2024u)] // control: ordinary play
    [InlineData("ctf_truefort", 1u)]  // control: ordinary play
    public void ABotWhoseRouteCannotBeWalkedIsGivenSomewhereElseToGo(string map, uint seed)
    {
        const int Ticks = 9000;
        const int Ceiling = 2700;
        const double Radius = 16;

        var match = HeadlessMatch.Load(
            HeadlessMatchOptions.Default with
            {
                Map = map,
                Bots = 12,
                Seed = seed,
                TimeLimitMinutes = 10,
                KillLimit = 4000,
                CapLimit = 5,
                MaxTicks = Ticks,
            },
            ReferenceTree.MapsDirectory,
            ReferenceTree.NavCacheDirectory);

        var anchorX = new double[WorldState.MaxSlots];
        var anchorY = new double[WorldState.MaxSlots];
        var run = new int[WorldState.MaxSlots];
        var seen = new bool[WorldState.MaxSlots];
        var worst = 0;
        var where = "";

        for (var tick = 0; tick < Ticks; tick++)
        {
            // Same reason as the sibling above: once the round is decided the
            // world stops advancing and every bot reads as motionless.
            if (match.Outcome != MatchOutcome.Running)
                break;

            match.Step();

            for (var slot = 1; slot < WorldState.MaxSlots; slot++)
            {
                // A dead bot is not a statue, and its slot is reused, so the run
                // breaks rather than carrying across the respawn.
                if (!match.State.IsAlive(slot) || match.SourceOf(slot) is not ScriptedBot)
                {
                    seen[slot] = false;
                    run[slot] = 0;
                    continue;
                }

                ref readonly var c = ref match.State.Characters[slot];

                if (seen[slot] && Gml.PointDistance(c.X, c.Y, anchorX[slot], anchorY[slot]) <= Radius)
                {
                    run[slot]++;

                    if (run[slot] > worst)
                    {
                        worst = run[slot];
                        where = $"{match.State.Players[slot].Class} in slot {slot} at "
                            + $"({c.X:F0}, {c.Y:F0})";
                    }

                    continue;
                }

                // The anchor MOVES with the bot rather than being sampled on a
                // fixed clock: a bot drifting 15px every window would otherwise
                // reset the run for ever without going anywhere.
                anchorX[slot] = c.X;
                anchorY[slot] = c.Y;
                run[slot] = 0;
                seen[slot] = true;
            }
        }

        Assert.True(
            worst < Ceiling,
            $"{map} seed {seed}: {where} stayed inside one {Radius}px circle for {worst} "
            + $"consecutive ticks, against a ceiling of {Ceiling}");
    }
}
