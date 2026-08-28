using Gg2.Bots;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// ⚠️ <b>A match that ends is not a match that happened.</b> The gate this repo
/// did not have, written after a whole gamemode ran degenerate for two phases
/// with every test green.
/// </summary>
/// <remarks>
/// <b>Where this came from.</b> Every Arena round in this port began with nobody
/// alive and ended on the next tick (see <see cref="ArenaRoundLoopTests"/>), and
/// <c>eval</c> reported those matches as clean wins the whole time — because
/// five instant rounds reach a cap limit exactly like five real ones. Nothing
/// asked whether anything had happened inside them.
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
        ReferenceTree.IncludedFiles,
        ReferenceTree.NavCacheDirectory);

    /// <summary>
    /// Movement, deaths, and how much the teams stack up.
    /// </summary>
    /// <remarks>
    /// ⚠️ <c>Died</c> is read for EVERY slot, not only live ones: a slot that just
    /// died is no longer <c>Alive</c>, so counting inside the live branch counts
    /// exactly zero deaths for ever. That was the first draft of this and it made
    /// a working deathmatch look like a broken one.
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
    /// ✅ <b>Team Deathmatch has learned to play itself.</b>
    /// </summary>
    /// <remarks>
    /// <b>This replaces a pin that asserted the opposite</b>, whose own remark
    /// said to: "if this test fails, Team Deathmatch has learned to play itself:
    /// delete the pin and say so". <c>BotObjective.Decide</c> chose between the
    /// intel, a generator and the control points, Team Deathmatch has none of the
    /// three, so it returned <see cref="BotGoal.None"/> — and on
    /// <c>tdm_mantic</c>, which is large and vertical, <b>all eight bots moved
    /// exactly zero pixels for 3,000 ticks</b> while every test stayed green.
    /// <para>
    /// The cascade now has a fourth branch, <c>BotObjective.Deathmatch</c>: hunt
    /// the nearest enemy, and — the part that makes it work rather than merely
    /// move — stand at a <em>spot</em> anchored on that enemy at the class's own
    /// band, never on the enemy itself. Walking onto a target parks a soldier
    /// inside its own splash-safe band, where <c>BotClassKeys</c> correctly
    /// refuses to fire and deliberately does not move it away, because "where this
    /// bot stands is the objective layer's decision rather than this one's".
    /// </para>
    /// <para>
    /// <b>Measured on <c>tdm_mantic</c>, 8 bots, this file's 600 ticks, three
    /// seeds</b>: bots that moved <b>0 → 8</b> (and past 100px, <b>0 → 8</b>),
    /// furthest <b>0 → 856 / 1,156 / 766px</b>, deaths <b>0 → 4 / 7 / 4</b>. Over
    /// 1,500 ticks the same three seeds give 13 / 16 / 13 deaths.
    /// </para>
    /// <para>
    /// ⚠️ <b>Deaths, not only movement.</b> The pin this replaces asked for
    /// movement alone, on the argument that a kill depends on two bots finding
    /// each other — which was the right bar for "did anything happen" and is the
    /// wrong one here, because hunting is precisely the claim. Eight bots
    /// patrolling past each other for 1,500 ticks would satisfy a movement test
    /// and would not be a deathmatch.
    /// </para>
    /// </remarks>
    [Fact]
    public void TeamDeathmatchBotsHuntEachOtherAndFight()
    {
        var (moved, deaths, _) = RunFully("tdm_mantic");

        Assert.Equal(8, moved.Count(m => m > 0));
        Assert.True(
            moved.Count(m => m > 100) >= 6,
            $"only {moved.Count(m => m > 100)} of the eight went anywhere");

        // ⚠️ A threshold rather than an exact pin, matching this file: it answers
        // "did a deathmatch happen", not "what did it cost". The three measured
        // seeds give 4, 7 and 4 at this file's 600 ticks, against 0 before.
        Assert.True(deaths >= 3, $"tdm_mantic ran {Ticks} ticks and produced only {deaths} deaths");
    }

    /// <summary>
    /// ⚠️ <b>And they do NOT form a conga line</b>, which was the stated objection
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
    /// ⚠️ <c>gg_debug</c> and not <c>tdm_mantic</c>, deliberately: on a large map
    /// the before-figure measures eight bots standing on their spawns, which is
    /// not a comparison. The one-room map is where a conga line would actually
    /// show, and it is the map the original bug hid behind.
    /// </para>
    /// </remarks>
    [Fact]
    public void TeamDeathmatchBotsSpreadOutRatherThanStacking()
    {
        var (_, _, clumped) = RunFully("gg_debug");

        // ⚠️ 0.85 rather than something tighter: seed 11 reads 0.73 now and 1.00
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
}
