using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Tests;

/// <summary>
/// The alarm phase, and GM8's exact countdown rule.
/// </summary>
/// <remarks>
/// The rule is one line in OpenGMK's <c>run_alarms</c> — decrement, fire only on
/// landing exactly on zero — and every plausible paraphrase of it is off by one
/// tick or fires twice. A weapon's refire is on the wire, so an off-by-one here
/// is a byte the client disagrees with.
/// </remarks>
public sealed class TimerTests
{
    [Fact]
    public void ACounterFiresExactlyNTicksAfterBeingSetToN()
    {
        var timer = 3;

        Assert.False(Timers.Advance(ref timer));
        Assert.False(Timers.Advance(ref timer));
        Assert.True(Timers.Advance(ref timer));
    }

    /// <summary>
    /// <c>Character.events/Create.xml</c> writes <c>alarm[1] = 0</c>, and that
    /// is how GM8 spells "off". A rule that fired at zero would make every
    /// character's intel cooldown expire on its first tick.
    /// </summary>
    [Fact]
    public void ACounterSetToZeroNeverFires()
    {
        var timer = 0;

        Assert.False(Timers.Advance(ref timer));
        Assert.Equal(-1, timer);
        Assert.False(Timers.Advance(ref timer));
    }

    [Fact]
    public void ACounterFiresOnceAndThenRests()
    {
        var timer = 1;

        Assert.True(Timers.Advance(ref timer));
        Assert.False(Timers.Advance(ref timer));
        Assert.False(Timers.Advance(ref timer));
    }

    /// <summary>
    /// A fired counter settles in <b>two</b> stages, not one: 0 for the rest
    /// of the frame its event ran on, then -1 from the next frame's alarm stage.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured against the running game: a counter must not write -1 the
    /// instant it expires.
    /// </para>
    /// <para>
    /// It is worth a test of its own because it is worth <em>a whole tick of
    /// behaviour</em> at exactly one site:
    /// <c>Character.events/Begin Step.xml:67</c> gates the afterburn intensity
    /// decay on <c>alarm[0] == -1</c> exactly, and Begin Step runs before the
    /// alarm stage — so the eager write started every burn's decay a tick
    /// early. <see cref="AFiredCounterDoesNotFireAgainOnItsWayToInactive"/> is
    /// the other half: resting at 0 must not mean firing twice.
    /// </para>
    /// </remarks>
    [Fact]
    public void AFiredCounterRestsAtZeroForOneStageAndThenGoesInactive()
    {
        var timer = 1;

        Assert.True(Timers.Advance(ref timer));
        Assert.Equal(Timers.JustFired, timer);

        Assert.False(Timers.Advance(ref timer));
        Assert.Equal(Timers.Inactive, timer);
    }

    /// <summary>
    /// The safety property the two-stage settle needs: a counter resting at 0
    /// decrements to -1 <em>without</em> firing.
    /// </summary>
    /// <remarks>
    /// The fire test is <em>exactly</em> zero and -1 is not it, which is the
    /// same rule that makes an alarm set to 0 never fire at all. Without this,
    /// leaving a fired counter at 0 would double every respawn, every reload
    /// and every uber.
    /// </remarks>
    [Fact]
    public void AFiredCounterDoesNotFireAgainOnItsWayToInactive()
    {
        var timer = Timers.JustFired;

        Assert.False(Timers.Advance(ref timer));
        Assert.Equal(Timers.Inactive, timer);
    }

    [Fact]
    public void AnInactiveCounterStaysWhereItIs()
    {
        var timer = WeaponState.Inactive;

        Assert.False(Timers.Advance(ref timer));
        Assert.Equal(WeaponState.Inactive, timer);
    }

    /// <summary>
    /// <c>Weapon.events/Create.xml</c> starts every weapon unable to fire with
    /// its refire counter already running, so a character that has just spawned
    /// becomes able to shoot exactly <c>refireTime</c> ticks later.
    /// </summary>
    [Theory]
    [InlineData(PlayerClass.Scout, 20)]
    [InlineData(PlayerClass.Soldier, 30)]
    [InlineData(PlayerClass.Heavy, 2)]
    [InlineData(PlayerClass.Pyro, 1)]
    public void AFreshWeaponBecomesReadyAfterItsRefireTime(PlayerClass playerClass, int refireTime)
    {
        var state = default(WorldState);
        state.Alive[0] = true;
        state.Characters[0].Class = playerClass;
        state.Weapons[0] = WeaponState.Spawn(playerClass);

        Assert.False(state.Weapons[0].ReadyToShoot);
        Assert.Equal(refireTime, state.Weapons[0].RefireTimer);

        for (var i = 0; i < refireTime - 1; i++)
        {
            Timers.Advance(ref state);
            Assert.False(state.Weapons[0].ReadyToShoot);
        }

        Timers.Advance(ref state);
        Assert.True(state.Weapons[0].ReadyToShoot);
    }

    /// <summary>
    /// <c>Character.events/Alarm 3.xml</c> clears the assist as well as the
    /// kill: once the finish-off window has closed there is nobody left to
    /// credit, and Alarm 4 may never have run at all.
    /// </summary>
    [Fact]
    public void TheFinishOffExpiryClearsBothDealers()
    {
        var state = default(WorldState);
        state.Alive[0] = true;
        state.Characters[0].LastDamageDealer = 4;
        state.Characters[0].LastDamageSource = DamageSource.Rocketlauncher;
        state.Characters[0].SecondToLastDamageDealer = 5;
        state.Characters[0].FinishOffTimer = 1;

        Timers.Advance(ref state);

        Assert.Equal(CharacterState.Nobody, state.Characters[0].LastDamageDealer);
        Assert.Equal(DamageSource.None, state.Characters[0].LastDamageSource);
        Assert.Equal(CharacterState.Nobody, state.Characters[0].SecondToLastDamageDealer);
    }

    [Fact]
    public void TheUberExpiryEndsInvulnerability()
    {
        var state = default(WorldState);
        state.Alive[0] = true;
        state.Characters[0].Ubered = true;
        state.Characters[0].UberTimer = 2;

        Timers.Advance(ref state);
        Assert.True(state.Characters[0].Ubered);

        Timers.Advance(ref state);
        Assert.False(state.Characters[0].Ubered);
    }

    /// <summary>
    /// <c>Character.events/Alarm 0.xml</c>: the decay rate is derived from
    /// whatever intensity the fire had reached when the delay ran out, so a
    /// hotter fire also cools faster and both reach zero together.
    /// </summary>
    [Fact]
    public void TheAfterburnDecayRateIsDerivedWhenTheDelayExpires()
    {
        var state = default(WorldState);
        state.Alive[0] = true;
        state.Characters[0].BurnIntensity = 9;
        state.Characters[0].AfterburnDecayTimer = 1;

        Timers.Advance(ref state);

        Assert.Equal(9.0 / CharacterState.BurnDecayDuration, state.Characters[0].IntensityDecay);
    }

    /// <summary>A slot with no character has no counters to advance.</summary>
    [Fact]
    public void ADeadSlotIsSkipped()
    {
        var state = default(WorldState);
        state.Characters[0].UberTimer = 1;
        state.Characters[0].Ubered = true;

        Timers.Advance(ref state);

        Assert.True(state.Characters[0].Ubered);
        Assert.Equal(1, state.Characters[0].UberTimer);
    }

    /// <summary>
    /// An alarm assigned a fractional number of ticks becomes a whole one.
    /// </summary>
    /// <remarks>
    /// <b>Not a tidying-up: a counter that never lands on zero never fires.</b>
    /// <see cref="Timers.Advance(ref int)"/> runs the event only on an exact
    /// zero, so a lifetime of 30.45 would step past it to -0.55 and the thing
    /// holding it would live forever. The scattergun is the one place in GG2
    /// that assigns a real — <c>35</c> times a movement-compensation factor —
    /// and it is on the live path for every shot a moving scout takes.
    /// <para>
    /// Measured against the running game: assigning an alarm and reading it
    /// straight back in the same frame gives 0.5 → 0, 1.5 → 2, 2.5 → 2, 3.5 → 4,
    /// 30.5 → 30, 31.5 → 32, 17.5 → 18, 26.25 → 26, 26.75 → 27 and 30.4 → 30 —
    /// half to even, matching the codebase's default for GML-side rounding
    /// (docs/TRAPS-SIMULATION.md's alarm table). The rows below marked
    /// "measured" are that reading.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(35.0, 35)]
    [InlineData(30.45, 30)]
    [InlineData(17.5, 18)]
    [InlineData(18.5, 18)]
    [InlineData(0.4, 0)]
    // measured
    [InlineData(0.5, 0)]
    [InlineData(1.5, 2)]
    [InlineData(2.5, 2)]
    [InlineData(3.5, 4)]
    [InlineData(30.5, 30)]
    [InlineData(31.5, 32)]
    [InlineData(26.25, 26)]
    [InlineData(26.75, 27)]
    [InlineData(30.4, 30)]
    public void AFractionalAlarmBecomesAWholeNumberOfTicks(double assigned, int stored)
    {
        Assert.Equal(stored, Timers.SetAlarm(assigned));
    }

    /// <summary>
    /// A projectile whose lifetime expires is removed from the pool, and its
    /// neighbours are not skipped.
    /// </summary>
    /// <remarks>
    /// Freeing one compacts the creation-order array, so a loop that advanced
    /// its index unconditionally would step over whatever slid down into the
    /// gap — and half a blast would outlive the other half by a tick. Three
    /// pellets expiring together is the smallest case that catches it.
    /// </remarks>
    [Fact]
    public void ExpiringProjectilesAreAllRemovedInOnePass()
    {
        var state = default(WorldState);

        for (var i = 0; i < 3; i++)
        {
            state.SpawnProjectile(new ProjectileState
            {
                Kind = ProjectileKind.Shot,
                OwnerSlot = ProjectileState.NoOwner,
                Lifetime = 1,
            });
        }

        // One that outlives them, so an over-eager sweep is caught too.
        var survivor = state.SpawnProjectile(new ProjectileState
        {
            Kind = ProjectileKind.Shot,
            OwnerSlot = ProjectileState.NoOwner,
            Lifetime = 5,
        });

        Timers.Advance(ref state);

        Assert.Equal(1, state.LiveProjectileCount);
        Assert.True(state.IsLive(survivor));
    }
}
