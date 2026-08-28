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
    /// ⚠️ <b>KNOWN ISSUE, pinned: on Team Deathmatch the bots do not move at
    /// all.</b>
    /// </summary>
    /// <remarks>
    /// <c>BotObjective.Decide</c> chooses between the intel, a generator and the
    /// control points — <b>Team Deathmatch has none of the three</b>, so it
    /// returns <see cref="BotGoal.None"/>, the follower has no route, and a bot
    /// stands where it spawned until an enemy walks into its combat range.
    /// <para>
    /// On <c>gg_debug</c> that is invisible: it is one room, everybody spawns on
    /// top of everybody, and combat steering does all the moving — which is why
    /// the TDM fixtures and <see cref="TeamDeathmatchTests"/> look healthy.
    /// <b>On <c>tdm_mantic</c>, which is large and vertical, all eight bots move
    /// exactly zero pixels in 1,500 ticks and the match is 3,000 ticks of
    /// nothing.</b> Measured 2026-08-28.
    /// </para>
    /// <para>
    /// ⚠️ <b>Pinned rather than fixed</b>, deliberately: giving Team Deathmatch a
    /// goal is a bot-layer design question — hunt the nearest enemy? patrol? hold
    /// a chokepoint? — and <c>CLAUDE.md</c> says the bot layer is this project's
    /// own, to improve where the fix is straightforward and to write down where
    /// it is not. This is not straightforward. <b>If this test fails, Team
    /// Deathmatch has learned to play itself: delete the pin and say so.</b>
    /// </para>
    /// <para>
    /// ⚠️ It also means <c>eval</c>'s <c>tdm_mantic</c> result has never measured
    /// anything, the same way its arena results did not.
    /// </para>
    /// </remarks>
    [Fact]
    public void TeamDeathmatchBotsStillHaveNoGoalAndDoNotMove()
    {
        var moved = Run("tdm_mantic");

        Assert.Equal(0, moved.Count(m => m > 0));
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
