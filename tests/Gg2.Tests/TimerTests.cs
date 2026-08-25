using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

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
}
