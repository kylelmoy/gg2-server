using GG2.Bots;
using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Tests;

/// <summary>
/// The learned policy in a full CTF match — phase 4's exit criterion.
/// </summary>
/// <remarks>
/// <b><c>gg2-rl</c> handed over a policy that has only ever played 1v1 duels,
/// 0-300px apart, four classes, no respawns, no objective.</b> A CTF match is
/// none of those things. Everything about the policy is measured; how it survives
/// contact with a real match is what these assert.
/// <para>
/// <b>"200 matches completed" is NOT the criterion.</b> <c>eval</c> reported
/// Arena matches as clean wins because five <em>instant</em> rounds reach a cap
/// exactly as five real ones do. Every match here asserts <b>duration and
/// deaths</b>, and a match with no deaths is a failed match rather than a fast
/// one.
/// </para>
/// <para>
/// <b>The second half is not optional either: no map may produce a stuck bot.</b>
/// A duel-strong bot that cannot leave spawn is worse than what ships today, so
/// movement is asserted per map and not only aggregate outcomes.
/// </para>
/// </remarks>
public sealed class PolicyMatchTests
{
    private static PolicyNetwork Policy() => PolicyFixture.Load();

    /// <summary>
    /// The classes THE FIXTURE was trained on. Everything else is untrained
    /// input, not a class it plays badly.
    /// </summary>
    /// <remarks>
    /// ⚠️ Read off the checkpoint rather than a constant since 2026-09-06: the set
    /// is a property of the file, and asking a constant here would test a
    /// different policy from the one the match is driven by.
    /// </remarks>
    private static PlayerClass[] Trained => [.. Policy().TrainedClasses];

    private static HeadlessMatchOptions Options(string map, uint seed, PlayerClass[]? classes, PolicyNetwork? policy) =>
        HeadlessMatchOptions.Default with
        {
            Map = map,
            Bots = 8,
            Seed = seed,
            CapLimit = 2,
            TimeLimitMinutes = 3,
            KillLimit = 20,
            MaxTicks = 30 * 60 * 4,
            AllowedClasses = classes,
            Brain = policy is null ? null : BotBrainSpec.Of(new PolicyMicroSpec(policy)),
            HybridTeam = Team.Red,
        };

    private sealed record Played(
        int Ticks, int Deaths, long PolicyTicks, long ScriptedTicks, int Bots, int Moved, MatchOutcome Outcome);

    private static Played Play(string map, uint seed, PlayerClass[]? classes, PolicyNetwork? policy)
    {
        var match = HeadlessMatch.Load(Options(map, seed, classes, policy), ReferenceTree.MapsDirectory, ReferenceTree.NavCacheDirectory);

        var deaths = 0;
        var start = new (double X, double Y)[WorldState.MaxSlots];
        var travel = new double[WorldState.MaxSlots];
        var seen = new bool[WorldState.MaxSlots];

        while (match.Outcome == MatchOutcome.Running)
        {
            match.Step();

            for (var slot = 1; slot < WorldState.MaxSlots; slot++)
            {
                if (match.Events.Died[slot])
                    deaths++;

                if (!match.State.IsAlive(slot))
                    continue;

                ref readonly var character = ref match.State.Characters[slot];

                if (!seen[slot])
                {
                    seen[slot] = true;
                    start[slot] = (character.X, character.Y);
                }

                var dx = character.X - start[slot].X;
                var dy = character.Y - start[slot].Y;

                travel[slot] = Math.Max(travel[slot], Math.Sqrt((dx * dx) + (dy * dy)));
            }
        }

        long policyTicks = 0;
        long scriptedTicks = 0;
        var bots = 0;
        var moved = 0;

        for (var slot = 1; slot < WorldState.MaxSlots; slot++)
        {
            if (match.HybridBotFor(slot) is { } bot)
            {
                policyTicks += bot.MicroTicks;
                scriptedTicks += bot.ScriptedTicks;
            }

            if (!seen[slot])
                continue;

            bots++;

            if (travel[slot] > 100)
                moved++;
        }

        return new Played(match.Tick, deaths, policyTicks, scriptedTicks, bots, moved, match.Outcome);
    }

    /// <summary>
    /// A policy-driven match actually happens: it lasts, people die, and the
    /// policy really drove.
    /// </summary>
    /// <remarks>
    /// <b>The premise assertions are the point.</b> Without
    /// <c>PolicyTicks &gt; 0</c> every other assertion here passes on a hybrid
    /// whose engagement gate never fired — a scripted bot with extra steps, which
    /// would win, lose and last exactly like one. <c>gg2-rl/docs/ENV.md</c> records four
    /// per-slot driver tests that passed with the driver ignored entirely.
    /// </remarks>
    [Theory]
    [InlineData("ctf_truefort", 11u)]
    [InlineData("ctf_2dfort", 12u)]
    [InlineData("ctf_orange", 13u)]
    public void APolicyMatchHappens(string map, uint seed)
    {
        var played = Play(map, seed, Trained, Policy());

        Assert.NotEqual(MatchOutcome.Running, played.Outcome);

        // A match that ends is not a match that happened.
        Assert.True(played.Ticks > 30 * 20, $"{map} lasted {played.Ticks} ticks");
        Assert.True(played.Deaths > 0, $"{map} had no deaths in {played.Ticks} ticks");

        // And the policy really drove some of it.
        Assert.True(played.PolicyTicks > 0, $"{map}: the policy never took control");
        Assert.True(played.ScriptedTicks > 0, $"{map}: the scripted layer never took control");
    }

    /// <summary>
    /// No map produces a stuck bot.
    /// </summary>
    /// <remarks>
    /// <b>The half of the exit criterion that is not about winning.</b> A
    /// duel-strong bot that cannot leave spawn is worse than what ships today, and
    /// a win rate cannot see it — a stuck bot on the losing team looks like a
    /// weaker bot.
    /// </remarks>
    [Theory]
    [InlineData("ctf_truefort", 21u)]
    [InlineData("ctf_2dfort", 22u)]
    [InlineData("ctf_orange", 23u)]
    [InlineData("ctf_conflict", 24u)]
    public void NoMapLeavesTheBotsStuck(string map, uint seed)
    {
        var played = Play(map, seed, Trained, Policy());

        // MOST bots, not the best one. "The furthest any bot got" is satisfied
        // by seven stuck bots and one that ran, and it would pass a map where
        // the policy pinned the whole team.
        //
        // And not ALL bots either, because that is not true today and asserting
        // it would be asserting a fix nobody has made: measured over 864 bots,
        // 11 moved under 100px with the policy and 7 without, and the worst case
        // in BOTH arms is a scripted bot. See gg2-rl/docs/ENV.md.
        Assert.True(
            played.Moved >= played.Bots * 0.8,
            $"{map}: only {played.Moved} of {played.Bots} bots moved more than 100px");
    }

    /// <summary>
    /// The restricted match really contains only trained classes.
    /// </summary>
    /// <remarks>
    /// <b>The premise of every measurement above.</b> A default headless match
    /// is <b>50% untrained classes</b> — <c>BotPopulation.PickClass</c> draws
    /// uniformly from the eight it allows, four of which are trained — and
    /// <c>ClassLimits</c> does not gate it, so without
    /// <c>AllowedClasses</c> the "four-class experiment" would quietly be a
    /// nine-class one.
    /// </remarks>
    [Fact]
    public void ARestrictedMatchContainsOnlyTrainedClasses()
    {
        var match = HeadlessMatch.Load(Options("ctf_truefort", 31u, Trained, Policy()), ReferenceTree.MapsDirectory, ReferenceTree.NavCacheDirectory);
        var counted = 0;

        for (var slot = 1; slot < WorldState.MaxSlots; slot++)
        {
            // An input source is what makes a slot a BOT. Team is the wrong
            // test — Team.Red is 0, so every empty PlayerState reads as red and
            // the first version of this counted all 63.
            if (match.SourceOf(slot) is null)
                continue;

            Assert.True(
                Policy().IsTrained(match.State.Players[slot].Class),
                $"slot {slot} is a {match.State.Players[slot].Class}, which the policy was not trained on");

            counted++;
        }

        Assert.Equal(8, counted);
    }

    /// <summary>
    /// And an unrestricted match really does contain untrained ones, so the
    /// restriction above is doing work.
    /// </summary>
    /// <remarks>
    /// <b>Without this the test above passes on a picker that happened to draw
    /// four trained classes</b>, and the restriction would be untested. This is
    /// the measurement behind "a default match is 50% untrained".
    /// </remarks>
    [Fact]
    public void AnUnrestrictedMatchContainsUntrainedClasses()
    {
        var untrained = 0;
        var total = 0;

        for (uint seed = 40; seed < 48; seed++)
        {
            var match = HeadlessMatch.Load(Options("ctf_truefort", seed, classes: null, policy: null), ReferenceTree.MapsDirectory, ReferenceTree.NavCacheDirectory);

            for (var slot = 1; slot < WorldState.MaxSlots; slot++)
            {
                if (match.SourceOf(slot) is null)
                    continue;

                total++;

                if (!Policy().IsTrained(match.State.Players[slot].Class))
                    untrained++;
            }
        }

        Assert.True(total > 0);
        Assert.True(
            untrained > 0,
            "an unrestricted match drew no untrained class in 64 bots; the restriction proves nothing");
    }

    /// <summary>
    /// An untrained class is driven by the scripted layer, never by the policy.
    /// </summary>
    /// <remarks>
    /// <b>Their class columns never received a gradient</b>, so their
    /// first-layer weights are still the random draw. Driving one is not playing
    /// badly, it is acting on noise.
    /// <para>
    /// ⚠️ <b>The premise is asserted, not assumed, because this test went stale
    /// once already.</b> It drove <c>Demoman</c> until 2026-09-03, when demoman
    /// joined the trained set and sniper left it — at which point the test was
    /// asserting that a <em>trained</em> class is never driven by the policy, and
    /// failed. It could not report that at the time because the fixture
    /// checkpoint was unloadable and this test was red for that unrelated reason.
    /// A hard-coded class list is a copy of the checkpoint's own set that nothing
    /// keeps in step, so the guard below fails loudly on the next change instead
    /// of quietly testing the wrong thing. ⚠️ Since 2026-09-06 that set comes off
    /// the fixture rather than a constant, so swapping the fixture for one trained
    /// differently trips the guard rather than silently changing what is asserted.
    /// </para>
    /// </remarks>
    [Fact]
    public void AnUntrainedClassIsNeverDrivenByThePolicy()
    {
        PlayerClass[] untrained = [PlayerClass.Sniper, PlayerClass.Medic];

        foreach (var playerClass in untrained)
        {
            Assert.False(
                Policy().IsTrained(playerClass),
                $"{playerClass} is in the FIXTURE's trained set now, so this test's premise is "
                + "gone — pick classes it actually did not train rather than deleting the assertion");
        }

        var match = HeadlessMatch.Load(
            Options("ctf_truefort", 51u, untrained, Policy()),
            ReferenceTree.MapsDirectory,
            ReferenceTree.NavCacheDirectory);

        while (match.Outcome == MatchOutcome.Running && match.Tick < 30 * 60)
            match.Step();

        var wrapped = 0;

        for (var slot = 1; slot < WorldState.MaxSlots; slot++)
        {
            if (match.HybridBotFor(slot) is not { } bot)
                continue;

            wrapped++;

            Assert.Equal(0, bot.MicroTicks);
            Assert.True(bot.ScriptedTicks > 0);
        }

        Assert.True(wrapped > 0, "no slot was wrapped, so nothing was tested");
    }
}
