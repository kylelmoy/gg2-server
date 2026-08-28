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

        // ⚠️ 1 and 2, not 0 and 1. Slot 0 is the server's own reserved player
        // and always exists — `ScenarioWorld` starts its counter at 1 for the
        // same reason a headless server does. A test that expected 0 here (the
        // first draft of this one did) is asserting the one thing CLAUDE.md
        // says never to assume.
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
    /// ⚠️ <b>The action repeat is counted in TICKS and the cap in DECISIONS.</b>
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

        // ⚠️ Stepping a finished episode is a mistake worth refusing loudly: a
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
    /// ⚠️ <b>Damage is accumulated over the repeat's ticks, not read from the
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
    /// ⚠️ <b>Restoring is not the claim; CONTINUING is.</b>
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
    /// ⚠️ <b>The decoder's aim is part of the snapshot.</b>
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
}
