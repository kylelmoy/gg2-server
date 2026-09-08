using Gg2.Bots;
using Gg2.Env;
using Gg2.Protocol;

namespace Gg2.Tests;

/// <summary>
/// <see cref="DuelEnv"/> — one duel, reset, stepped, snapshotted.
/// </summary>
/// <remarks>
/// <c>docs/ENV.md</c> step 2. The env wraps <c>ScenarioWorld</c>, which is a
/// world with no gamemode in it — no round clock, no capture, no autobalance, no
/// respawn wave — because every one of those would move the reward without the
/// agent having done anything.
/// </remarks>
public sealed class DuelEnvTests
{
    private const string Map = "gg_debug";

    private static DuelEnv Env() => new(ReferenceTree.IncludedFiles);

    /// <summary>
    /// Two soldiers a short way apart, facing each other, with a generous cap.
    /// </summary>
    private static DuelSpec Spec(int repeat = 1, int cap = 200) => new()
    {
        Map = Map,
        RedClass = PlayerClass.Soldier,
        BlueClass = PlayerClass.Soldier,
        RedX = 600,
        RedY = 850,
        BlueX = 700,
        BlueY = 850,
        DecisionCap = cap,
        ActionRepeat = repeat,
    };

    private static EnvAction Idle => EnvAction.Idle;

    [Fact]
    public void AResetPlacesBothAgentsAndRedIsAlwaysFirst()
    {
        var env = Env();
        env.Reset(Spec(), seed: 1);

        // 1 and 2, not 0 and 1. Slot 0 is the server's own reserved player
        // and always exists — `ScenarioWorld` starts its counter at 1 for the
        // same reason a headless server does.
        Assert.Equal(1, env.RedSlot);
        Assert.Equal(2, env.BlueSlot);
        Assert.True(env.RedSlot < env.BlueSlot, "red must be placed first");
        Assert.Equal(Termination.Running, env.Termination);
        Assert.Equal(0, env.Decisions);

        Assert.True(env.World.State.Alive[env.RedSlot]);
        Assert.True(env.World.State.Alive[env.BlueSlot]);
        Assert.Equal(Team.Red, env.World.State.Characters[env.RedSlot].Team);
        Assert.Equal(Team.Blue, env.World.State.Characters[env.BlueSlot].Team);
    }

    /// <summary>
    /// <b>The action repeat is counted in TICKS and the cap in DECISIONS.</b>
    /// </summary>
    /// <remarks>
    /// A cap in ticks would silently change episode length when the repeat
    /// changed, so a trainer comparing two repeat settings would be comparing
    /// two different episode budgets without knowing.
    /// </remarks>
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void OneDecisionAdvancesTheWorldByTheActionRepeat(int repeat)
    {
        var env = Env();
        env.Reset(Spec(repeat), seed: 1);

        env.Step(Idle, Idle);

        Assert.Equal(1, env.Decisions);
        Assert.Equal(repeat, env.Tick);

        env.Step(Idle, Idle);

        Assert.Equal(2, env.Decisions);
        Assert.Equal(repeat * 2, env.Tick);
    }

    /// <summary>The episode ends on the cap, and the cap counts decisions.</summary>
    [Fact]
    public void TheEpisodeEndsOnTheDecisionCap()
    {
        var env = Env();
        env.Reset(Spec(repeat: 2, cap: 3), seed: 1);

        Assert.False(env.Step(Idle, Idle).Red.Done);
        Assert.False(env.Step(Idle, Idle).Red.Done);

        var last = env.Step(Idle, Idle);

        Assert.True(last.Red.Done);
        Assert.True(last.Blue.Done);
        Assert.Equal(Termination.TickCap, last.Red.Termination);
        Assert.Equal(6, env.Tick);

        // Stepping a finished episode is a mistake worth refusing loudly: a
        // trainer that ignored `done` would otherwise keep collecting
        // transitions from a world where one agent is a corpse.
        Assert.Throws<InvalidOperationException>(() => env.Step(Idle, Idle));
    }

    /// <summary>
    /// Idle agents standing on a floor neither hurt each other nor drift.
    /// </summary>
    /// <remarks>
    /// The control for every reward test below: if damage appeared here, the
    /// components would be measuring the scenario rather than the agents.
    /// </remarks>
    [Fact]
    public void DoingNothingEarnsNothing()
    {
        var env = Env();
        env.Reset(Spec(), seed: 1);

        for (var i = 0; i < 30; i++)
        {
            var (red, blue) = env.Step(Idle, Idle);

            Assert.Equal(0, red.DamageDealt);
            Assert.Equal(0, red.DamageTaken);
            Assert.Equal(0, blue.DamageDealt);
            Assert.Equal(0, blue.DamageTaken);
            Assert.Equal(0, red.Kills + red.Deaths + blue.Kills + blue.Deaths);
        }
    }

    /// <summary>
    /// <b>Damage is accumulated over the repeat's ticks, not read from the
    /// last one.</b>
    /// </summary>
    /// <remarks>
    /// <c>TickEvents</c> is cleared every tick, so reading it after the loop
    /// reports the final tick's damage and silently drops the rest — at a repeat
    /// of 4 that is three quarters of the signal, lost in a way that still
    /// trains. This fires a rocket and checks the damage survives a repeat long
    /// enough to contain both the shot and the hit.
    /// </remarks>
    [Fact]
    public void DamageIsSummedAcrossTheRepeatRatherThanSampledAtTheEnd()
    {
        var env = Env();
        env.Reset(Spec(repeat: 12), seed: 1);

        // Red aims at blue, who is directly to the right, and fires.
        env.Decoder.SetAim(env.RedSlot, 0);

        var fire = new EnvAction
        {
            Fire = 1,
            AimDelta = ActionDecoder.AimCentreBin,
        };

        var dealt = 0.0;
        var taken = 0.0;

        for (var i = 0; i < 6 && env.Termination == Termination.Running; i++)
        {
            var (red, blue) = env.Step(fire, Idle);
            dealt += red.DamageDealt;
            taken += blue.DamageTaken;
        }

        Assert.True(dealt > 0, "red never hurt blue, so this test measures nothing");
        Assert.Equal(dealt, taken, 9);
    }

    /// <summary>
    /// A death ends the episode, and the credit lands on the right agent.
    /// </summary>
    [Fact]
    public void ADeathEndsTheEpisodeAndCreditsTheKiller()
    {
        var env = Env();
        env.Reset(Spec(repeat: 12), seed: 1);
        env.Decoder.SetAim(env.RedSlot, 0);

        // Softened so one duel's worth of rockets is enough.
        env.World.State.Characters[env.BlueSlot].Hp = 20;

        var fire = new EnvAction { Fire = 1, AimDelta = ActionDecoder.AimCentreBin };

        StepResult red = default, blue = default;

        for (var i = 0; i < 20 && env.Termination == Termination.Running; i++)
            (red, blue) = env.Step(fire, Idle);

        Assert.Equal(Termination.Death, env.Termination);
        Assert.Equal(1, red.Kills);
        Assert.Equal(1, blue.Deaths);
        Assert.Equal(0, red.Deaths);
        Assert.Equal(0, blue.Kills);
    }

    /// <summary>
    /// <b>Restoring is not the claim; CONTINUING is.</b>
    /// </summary>
    /// <remarks>
    /// <c>docs/BOTAI.md</c> item 2's rule, and the reason it is written that way:
    /// the failure worth catching is a state that compares equal immediately
    /// after a restore and then ticks differently, because something that is not
    /// in the bytes — an RNG stream, a decoder's aim — was left behind. So the
    /// restored copy is stepped against a control that was never serialised, and
    /// compared at the END.
    /// </remarks>
    [Fact]
    public void ARestoredEpisodeContinuesIdenticallyToOneThatWasNeverSaved()
    {
        var control = Env();
        var restored = Env();

        control.Reset(Spec(repeat: 2), seed: 7);
        restored.Reset(Spec(repeat: 2), seed: 7);

        var script = new[]
        {
            new EnvAction { Horizontal = 2, AimDelta = 10 },
            new EnvAction { Jump = 1, Fire = 1, AimDelta = 3 },
            new EnvAction { Horizontal = 1, Fire = 1, AimDelta = ActionDecoder.AimCentreBin },
            new EnvAction { Down = 1, AimDelta = 14 },
        };

        // Both run the same opening.
        for (var i = 0; i < 4; i++)
        {
            control.Step(script[i], Idle);
            restored.Step(script[i], Idle);
        }

        var snapshot = restored.Snapshot();

        // The control carries on; the copy is thrown away and rebuilt.
        for (var i = 0; i < 4; i++)
            control.Step(script[i], Idle);

        restored.Restore(snapshot);

        for (var i = 0; i < 4; i++)
            restored.Step(script[i], Idle);

        // Compared at the end, on the bytes, not on a field or two.
        Assert.Equal(control.Snapshot(), restored.Snapshot());
        Assert.Equal(control.Decisions, restored.Decisions);
        Assert.Equal(
            control.World.State.Characters[control.RedSlot].X,
            restored.World.State.Characters[restored.RedSlot].X);
    }

    /// <summary>
    /// <b>The decoder's aim is part of the snapshot.</b>
    /// </summary>
    /// <remarks>
    /// The world's bytes are the world's, and the aim lives outside them. A
    /// snapshot that restored one and not the other would resume the episode
    /// pointing somewhere else — a silent divergence rather than a crash, and
    /// the same class of bug as a bot's RNG not travelling with its state.
    /// </remarks>
    [Fact]
    public void TheSnapshotCarriesTheAimAndTheDecisionCount()
    {
        var env = Env();
        env.Reset(Spec(), seed: 3);

        for (var i = 0; i < 5; i++)
            env.Step(new EnvAction { AimDelta = 12 }, Idle);

        var aim = env.Decoder.Aim(env.RedSlot);
        var decisions = env.Decisions;
        var snapshot = env.Snapshot();

        // Move it somewhere else entirely.
        for (var i = 0; i < 5; i++)
            env.Step(new EnvAction { AimDelta = 0 }, Idle);

        Assert.NotEqual(aim, env.Decoder.Aim(env.RedSlot));

        env.Restore(snapshot);

        Assert.Equal(aim, env.Decoder.Aim(env.RedSlot), 12);
        Assert.Equal(decisions, env.Decisions);
    }

    /// <summary>A snapshot from a different world is refused rather than half-read.</summary>
    [Fact]
    public void AWrongSizedSnapshotIsRefused()
    {
        var env = Env();
        env.Reset(Spec(), seed: 1);

        Assert.Throws<ArgumentException>(() => env.Restore(new byte[16]));
    }

    /// <summary>Two worlds with the same seed and script run identically.</summary>
    /// <remarks>
    /// The determinism this whole surface rests on, asserted on the bytes. If it
    /// fails, nothing downstream — batch independence, snapshot, the trainer's
    /// reproducibility — means anything.
    /// </remarks>
    [Fact]
    public void TheSameSeedAndScriptGiveTheSameBytes()
    {
        var a = Env();
        var b = Env();

        a.Reset(Spec(repeat: 3), seed: 99);
        b.Reset(Spec(repeat: 3), seed: 99);

        var action = new EnvAction { Horizontal = 2, Fire = 1, AimDelta = 9 };

        for (var i = 0; i < 10; i++)
        {
            a.Step(action, Idle);
            b.Step(action, Idle);
        }

        Assert.Equal(a.Snapshot(), b.Snapshot());
    }

    [Fact]
    public void AnInvalidSpecIsRefusedAtReset()
    {
        var env = Env();

        Assert.Throws<ArgumentOutOfRangeException>(() => env.Reset(Spec(repeat: 0), 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => env.Reset(Spec(cap: 0), 1));
    }
    // --- per-slot drivers -----------------------------------------------------

    private static DuelEnv NavEnv()
    {
        ReferenceTree.RequireNavCache();
        return new DuelEnv(ReferenceTree.IncludedFiles, ReferenceTree.NavCacheDirectory);
    }

    /// <summary>Both sides sampled onto the map's own spawn points.</summary>
    private static DuelSpec Sampled(string map, EnvDriver red, EnvDriver blue, int cap = 250) => new()
    {
        Map = map,
        RedClass = PlayerClass.Soldier,
        BlueClass = PlayerClass.Soldier,
        RedX = DuelSpec.Sampled,
        RedY = DuelSpec.Sampled,
        BlueX = DuelSpec.Sampled,
        BlueY = DuelSpec.Sampled,
        DecisionCap = cap,
        ActionRepeat = 4,
        RedDriver = red,
        BlueDriver = blue,
        RedTier = 5,
        BlueTier = 5,
    };

    /// <summary>
    /// A client side gets back exactly the action it sent.
    /// </summary>
    /// <remarks>
    /// <b>Echoed rather than left to the trainer to remember.</b> A trainer
    /// logging transitions would otherwise correlate the reply with its own send
    /// buffer — which works right up until a side is bot-driven and the two stop
    /// agreeing, which is exactly when the action matters most.
    /// </remarks>
    [Fact]
    public void AClientSideGetsBackTheActionItSent()
    {
        var env = Env();
        env.Reset(Spec(), seed: 1);

        var sent = new EnvAction { Horizontal = 2, Jump = 1, Down = 0, Fire = 1, AimDelta = 11 };
        var (red, blue) = env.Step(sent, Idle);

        Assert.Equal(sent, red.Action);
        Assert.True(red.ActionExact, "a client's own action is always reproducible");

        Assert.Equal(Idle, blue.Action);
        Assert.True(blue.ActionExact);
    }

    /// <summary>
    /// An idle driver presses nothing, and the slot still earns its rewards.
    /// </summary>
    /// <remarks>
    /// <b>The first of the two properties <c>gg2-rl</c> asked for</b>: a
    /// bot-driven slot has to keep returning its own observation and reward
    /// components, or an opponent cannot be scored and self-play cannot be
    /// evaluated. The floor case is the one worth gating, because a driver that
    /// short-circuited the whole slot would still look right on damage.
    /// </remarks>
    [Fact]
    public void AnIdleDriverPressesNothingAndStillEarnsItsRewards()
    {
        var env = NavEnv();
        env.Reset(Spec() with { BlueDriver = EnvDriver.Idle }, seed: 1);

        // The premise, asserted rather than assumed: a driver really is behind
        // that slot. Without this the whole test passes on an env that ignored the
        // driver field entirely, because a CLIENT slot sent EnvAction.Idle behaves
        // identically — checked by mutation, and it did.
        Assert.NotNull(env.BotFor(env.BlueSlot));
        Assert.Null(env.BotFor(env.RedSlot));

        var damage = 0.0;

        for (var i = 0; i < 60; i++)
        {
            // Red shoots at its neighbour; blue does nothing at all.
            var (_, blue) = env.Step(new EnvAction { Fire = 1, AimDelta = ActionDecoder.AimCentreBin }, Idle);

            Assert.Equal(EnvAction.Idle, blue.Action);
            Assert.True(blue.ActionExact);

            damage += blue.DamageTaken;
        }

        // NOT asserted: that it stayed where it was. It does not — measured at
        // 20px — because rocket splash moves a character that pressed nothing, and
        // that is the simulation being right. "Presses nothing" is the claim, and
        // the per-step action assertion above is what states it.
        //
        // The load-bearing half: the idle side's reward components are real,
        // not zeroed because nothing drove it from outside.
        Assert.True(damage > 0, "an idle opponent was shot at for 60 decisions and recorded no damage taken");
    }

    /// <summary>
    /// Two scripted opponents fight, and what they did comes back.
    /// </summary>
    /// <remarks>
    /// <b>The whole point of the item.</b> Until per-slot drivers existed a duel
    /// between two things that both live in this repo could not be asked for at
    /// all, which is what blocked <c>gg2-rl</c>'s phase 1 exit criterion and its
    /// phase 3 opponent pool.
    /// <para>
    /// <b>Deaths, not merely damage.</b> "N episodes completed" is satisfiable
    /// by a broken feature — <c>MatchActuallyHappensTests</c> exists because five
    /// instant Arena rounds looked exactly like five real ones — and a duel that
    /// ends on its tick cap with two bots standing in their own spawns is the same
    /// shape. Requiring somebody to die is what makes this a match rather than a
    /// timer.
    /// </para>
    /// <para>
    /// <b><c>gg_debug</c> and sampled spawns on purpose.</b> On a CTF or KotH
    /// map both spawns sit behind their own team gates, so neither bot can route
    /// to the other and nobody ever fires — correct simulation, useless duel. The
    /// maps that work are recorded in <c>docs/ENV.md</c>.
    /// </para>
    /// </remarks>
    [Fact]
    public void TwoScriptedOpponentsFightAndTheirActionsComeBack()
    {
        var env = NavEnv();
        env.Reset(Sampled("gg_debug", EnvDriver.Scripted, EnvDriver.Scripted), seed: 1);

        var fired = 0;
        var moved = 0;
        var damage = 0.0;

        while (env.Termination == Termination.Running)
        {
            var (red, blue) = env.Step(Idle, Idle);

            damage += red.DamageDealt + blue.DamageDealt;

            foreach (var r in new[] { red, blue })
            {
                if (r.Action.Fire != 0)
                    fired++;

                if (r.Action.Horizontal != 0 || r.Action.Jump != 0)
                    moved++;
            }
        }

        // Somebody DIED. Not "the episode ended".
        Assert.Equal(Termination.Death, env.Termination);
        Assert.True(damage > 0, "two scripted bots fought to a death and dealt no damage");

        // The second property gg2-rl asked for: what the bot chose comes back.
        // Zero here is the silent failure — a driver that ran the bot correctly
        // and reported EnvAction.Idle for every step would pass every other
        // assertion in this test.
        Assert.True(fired > 0, "neither scripted bot's action ever carried a shot it demonstrably took");
        Assert.True(moved > 0, "neither scripted bot's action ever carried a direction it demonstrably walked");
    }

    /// <summary>
    /// A scripted driver is refused without a nav graph rather than downgraded.
    /// </summary>
    /// <remarks>
    /// <c>ScriptedBot</c> takes a null graph happily and then has no follower at
    /// all: it fights from where it spawned and never walks. An experiment
    /// reporting "against the scripted bot" while playing a stationary one is
    /// precisely the class of wrong <c>CLAUDE.md</c> names — a claim about
    /// capability that nothing inside the repo can contradict.
    /// </remarks>
    [Fact]
    public void AScriptedDriverWithoutANavGraphIsRefused()
    {
        // No nav directory: the env loads maps but no graphs.
        var env = new DuelEnv(ReferenceTree.IncludedFiles);

        var refused = Assert.Throws<InvalidOperationException>(
            () => env.Reset(Spec() with { BlueDriver = EnvDriver.Scripted }, seed: 1));

        Assert.Contains("nav graph", refused.Message, StringComparison.Ordinal);

        // The control: the same env is perfectly happy with the drivers that need
        // no graph, so this is about the graph and not about the env.
        env.Reset(Spec() with { BlueDriver = EnvDriver.Idle }, seed: 1);
        env.Reset(Spec() with { BlueDriver = EnvDriver.Rollout }, seed: 1);
    }

    /// <summary>
    /// A snapshot is refused while a bot drives a slot.
    /// </summary>
    /// <remarks>
    /// <b>The honest edge of "the bytes ARE the state".</b> That claim is true of
    /// <see cref="Gg2.Simulation.WorldState"/>, which holds no reference to
    /// anything — but a bot is state outside it: <c>ScriptedBot</c> carries a
    /// route, a target, four clocks and its own RNG stream. Restoring the world
    /// alone would resume with an opponent mid-way through a plan for a world that
    /// no longer exists, and it would do it silently.
    /// <para>
    /// <b>The control matters more than the refusal.</b> Both slots
    /// client-driven still snapshots, so this is a statement about bots rather
    /// than a snapshot path that quietly stopped working.
    /// </para>
    /// </remarks>
    [Fact]
    public void ASnapshotIsRefusedWhileABotDrivesASlot()
    {
        var env = NavEnv();

        env.Reset(Spec() with { BlueDriver = EnvDriver.Scripted }, seed: 1);
        env.Step(Idle, Idle);

        var refused = Assert.Throws<InvalidOperationException>(() => env.Snapshot());
        Assert.Contains("bot-driven", refused.Message, StringComparison.Ordinal);

        // The control.
        env.Reset(Spec(), seed: 1);
        env.Step(Idle, Idle);
        Assert.NotEmpty(env.Snapshot());
    }

    /// <summary>
    /// Hunting a target that cannot be reached must not search every decision.
    /// </summary>
    /// <remarks>
    /// <c>PathFollower.SetGoal</c> clears both <c>ReplanAt</c> and
    /// <c>PlanFailUntil</c>, so calling it unconditionally on every decision
    /// defeats the unreachable-goal throttle completely: a full A* per bot per
    /// decision, for ever. <c>BotObjective</c>'s deathmatch branch sets the goal
    /// instead, throttled on the RESOLVED node by <c>BOT_GOAL_RETARGET_DIST</c> —
    /// but <c>PathFollowerFixtureTests</c>' throttle gate does not see this
    /// caller, so this test gates it here too: <b>a throttle is only as good as
    /// every path that reaches it</b>.
    /// <para>
    /// <c>ctf_2dfort</c> because it is genuinely unreachable and not merely far:
    /// both sampled spawns sit behind their own team gates, so A* correctly
    /// returns no route to the opponent at all.
    /// </para>
    /// <para>
    /// ⚠️ <b>The premise is counted over the episode, not read off the end of
    /// it.</b> A bot that has stood still for <c>BotConstants.RepostTicks</c>
    /// re-posts to reachable ground nearby, so it finishes the episode holding a
    /// perfectly good route to somewhere that is not the opponent — and an
    /// end-state <c>HasPath</c> check therefore reported "the opponent turned out
    /// to be reachable" when nothing of the sort had happened. Counting the
    /// decisions that had no route is also strictly the stronger premise: an
    /// end-state snapshot can be a momentary gap.
    /// </para>
    /// </remarks>
    [Fact]
    public void HuntingAnUnreachableOpponentDoesNotSearchEveryDecision()
    {
        var env = NavEnv();
        env.Reset(Sampled("ctf_2dfort", EnvDriver.Scripted, EnvDriver.Idle, cap: 200), seed: 1);

        var routeless = 0;
        var decisions = 0;

        while (env.Termination == Termination.Running)
        {
            env.Step(Idle, Idle);
            decisions++;

            if (env.BotFor(env.RedSlot) is ScriptedBot b && !b.Path.HasPath)
                routeless++;
        }

        var bot = (ScriptedBot)env.BotFor(env.RedSlot)!;

        // The premise: it really is unreachable, so the searches really did fail.
        // A quarter of the episode is well clear of the 67 of 200 measured and
        // well clear of the nothing a reachable opponent would produce.
        Assert.True(
            routeless > decisions / 4,
            $"only {routeless} of {decisions} decisions had no route; "
            + "the opponent turned out to be reachable and this map no longer tests the throttle");

        Assert.True(bot.Path.HasGoal, "the deathmatch goal never set anything at all, so nothing here was tested");

        // The bound is the throttle's own, not a round number: BOT_REPLAN_TICKS
        // apart over the episode's ticks, plus slack for the stagger and the first
        // plan. Unthrottled is one per decision — 200 — so this bites hard.
        var ticks = 200 * 4;
        var ceiling = (ticks / BotConstants.ReplanTicks) + 4;

        Assert.True(
            bot.Path.Replans <= ceiling,
            $"an unreachable opponent was searched for {bot.Path.Replans} times in 200 decisions, "
            + $"against a throttled ceiling of {ceiling}");
    }

    /// <summary>
    /// A scripted opponent hunts down and kills an agent that never moves.
    /// </summary>
    /// <remarks>
    /// Chasing the enemy's exact position walks a soldier to point blank —
    /// inside its own splash-safe band, where <c>BotClassKeys</c> correctly
    /// refuses to fire — and leaves it there with zero shots and zero damage.
    /// The objective layer instead stands at a <em>spot</em> anchored on the
    /// enemy at the class's own band, a real walkable node it can shoot from.
    /// <para>
    /// <b>An idle opponent, not a scripted one, and that is the point.</b> Two
    /// scripted bots find each other because both are walking; a stationary
    /// target separates "the opponent moves" from "the opponent hunts", and is
    /// also what an untrained policy looks like on its first episodes.
    /// </para>
    /// <para>
    /// <c>arena_montane</c> rather than <c>gg_debug</c>: gg_debug's two spawn
    /// points are 59px apart, already inside a soldier's band, so a bot that
    /// starts there does not need to leave. That gap is recorded in
    /// <c>docs/TRAPS-BOTS.md</c> and is not what this test is about.
    /// </para>
    /// </remarks>
    [Fact]
    public void AScriptedOpponentHuntsDownAnAgentThatNeverMoves()
    {
        var deaths = 0;

        for (uint seed = 1; seed <= 8; seed++)
        {
            var env = NavEnv();
            env.Reset(Sampled("arena_montane", EnvDriver.Idle, EnvDriver.Scripted), seed);

            while (env.Termination == Termination.Running)
                env.Step(Idle, Idle);

            if (env.Termination == Termination.Death)
                deaths++;
        }

        // An exact pin on eight stochastic-looking episodes would be a
        // re-record every time a spawn moves; the bound just needs to be well
        // above zero.
        Assert.True(
            deaths >= 5,
            $"a scripted opponent killed a stationary agent in only {deaths} of 8 duels; before the "
            + "deathmatch goal this was 0 of 8");
    }

    /// <summary>A driver or tier outside the table is refused, not clamped.</summary>
    /// <remarks>
    /// Silently giving an out-of-range tier the top one would make an
    /// experiment report a matchup that never happened, which is worse than a
    /// crash because it is publishable.
    /// </remarks>
    [Fact]
    public void AnUnknownDriverOrAnImpossibleTierIsRefused()
    {
        var env = NavEnv();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => env.Reset(Spec() with { BlueDriver = (EnvDriver)99 }, seed: 1));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => env.Reset(Spec() with { BlueDriver = EnvDriver.Scripted, BlueTier = 6 }, seed: 1));

        // The control: a tier is only checked for the driver that uses one, so
        // a leftover tier on a client side is not an error.
        env.Reset(Spec() with { RedTier = 6 }, seed: 1);
    }

    /// <summary>
    /// A bot-driven duel is a pure function of its seed.
    /// </summary>
    /// <remarks>
    /// <b><c>ScriptedBot.Random</c> is the bot's own stream and is NOT
    /// <c>WorldState.Random</c></b> — deliberately, because the match's stream is
    /// observable simulation and a bot drawing from it would make its position
    /// depend on how many bots were thinking. So it has to be seeded from
    /// somewhere, and "somewhere" has to be this world's own seed or every world
    /// in a batch shares one opponent's rolls.
    /// </remarks>
    [Fact]
    public void ABotDrivenDuelIsAPureFunctionOfItsSeed()
    {
        static string Play(uint seed)
        {
            var env = NavEnv();
            env.Reset(Sampled("gg_debug", EnvDriver.Scripted, EnvDriver.Scripted, cap: 80), seed);

            var log = new System.Text.StringBuilder();

            while (env.Termination == Termination.Running)
            {
                var (red, blue) = env.Step(Idle, Idle);
                log.Append($"{red.Action.Horizontal}{red.Action.Fire}{red.Action.AimDelta}|");
                log.Append($"{blue.Action.Horizontal}{blue.Action.Fire}{blue.Action.AimDelta};");
            }

            return log.ToString();
        }

        Assert.Equal(Play(11), Play(11));
        Assert.NotEqual(Play(11), Play(12));
    }
}
