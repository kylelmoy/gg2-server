using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// The character and weapon blocks, now that there is state behind them.
/// </summary>
/// <remarks>
/// Three of these fields were placeholder zeros for four phases, and one of them
/// — the medic's heal target — was a zero that named roster slot 0 rather than
/// nobody. What is checked here is that each now comes from somewhere real, and
/// that the two sentinels are 255 and not 0.
/// </remarks>
public sealed class CharacterWireTests
{
    private static (CharacterState Character, WeaponState Weapon) Fresh(PlayerClass playerClass)
    {
        var character = new CharacterState
        {
            Class = playerClass,
            Hp = ClassStats.For(playerClass).MaxHp,
            CloakAlpha = 1,
            NutsNBolts = 100,
            Healer = CharacterState.Nobody,
        };

        return (character, WeaponState.Spawn(playerClass));
    }

    /// <summary>
    /// The reference writes <c>currentWeapon.ammoCount</c>, so a scout who has
    /// just spawned reports six rounds — not the zero this wrote before there
    /// was a weapon to ask.
    /// </summary>
    [Theory]
    [InlineData(PlayerClass.Scout, 6)]
    [InlineData(PlayerClass.Soldier, 4)]
    [InlineData(PlayerClass.Heavy, 200)]
    [InlineData(PlayerClass.Sniper, 0)]
    public void AmmoComesFromTheWeapon(PlayerClass playerClass, int ammo)
    {
        var (character, weapon) = Fresh(playerClass);
        var block = CharacterWire.ToBlock(character, weapon);

        Assert.Equal(ammo, block.Ammo);
    }

    /// <summary>
    /// The ammo byte saturates rather than wrapping: a minigun's 200 fits, but
    /// the quantiser is what stops a future weapon's larger magazine from
    /// reporting a small number.
    /// </summary>
    [Fact]
    public void AmmoSaturatesRatherThanWrapping()
    {
        var (character, weapon) = Fresh(PlayerClass.Heavy);
        weapon.AmmoCount = 300;

        Assert.Equal(255, CharacterWire.ToBlock(character, weapon).Ammo);
    }

    /// <summary>
    /// <c>Character.events/User Event 12.xml</c>'s <c>switch(player.class)</c>:
    /// four classes use the byte and six write zero, and only the medic's is
    /// scaled.
    /// </summary>
    [Fact]
    public void TheClassSpecificByteIsScaledOnlyForTheMedic()
    {
        var (spy, spyGun) = Fresh(PlayerClass.Spy);
        spy.CloakAlpha = 0.5;
        Assert.Equal(128, CharacterWire.ToBlock(spy, spyGun).ClassSpecific);

        var (medic, medigun) = Fresh(PlayerClass.Medic);
        medigun.UberCharge = WeaponState.MaxUberCharge;
        Assert.Equal(255, CharacterWire.ToBlock(medic, medigun).ClassSpecific);

        var (engineer, shotgun) = Fresh(PlayerClass.Engineer);
        engineer.NutsNBolts = 100;
        Assert.Equal(100, CharacterWire.ToBlock(engineer, shotgun).ClassSpecific);

        var (sniper, rifle) = Fresh(PlayerClass.Sniper);
        rifle.Charge = 47;
        Assert.Equal(47, CharacterWire.ToBlock(sniper, rifle).ClassSpecific);

        var (scout, scattergun) = Fresh(PlayerClass.Scout);
        Assert.Equal(0, CharacterWire.ToBlock(scout, scattergun).ClassSpecific);
    }

    /// <summary>Bit 0 of the flags byte is the cloak; bits 1..3 are moveStatus.</summary>
    [Fact]
    public void CloakAndMoveStatusShareTheFlagsByte()
    {
        var (character, weapon) = Fresh(PlayerClass.Spy);
        character.Cloak = true;
        character.MoveStatus = 2;

        var block = CharacterWire.ToBlock(character, weapon);

        Assert.True(block.Cloaked);
        Assert.Equal(2, block.MoveStatus);
        Assert.Equal(0x05, block.Flags);
    }

    /// <summary>
    /// <c>Weapon.events/Create.xml</c>: a weapon spawns unable to fire, with its
    /// refire counter already running. The pair this used to send — ready, with
    /// nothing pending — was a guess made before there was any weapon state.
    /// </summary>
    [Fact]
    public void AFreshWeaponReportsItselfNotReady()
    {
        var (_, weapon) = Fresh(PlayerClass.Scout);
        var block = CharacterWire.ToWeaponBlock(weapon, PlayerClass.Scout);

        Assert.Equal(0, block.ReadyToShoot);
        Assert.Equal(20, block.RefireAlarm);
    }

    /// <summary>
    /// The reference writes <c>alarm[0]</c> through <c>write_ubyte</c>, which
    /// clamps — so a resting counter of -1 goes out as 0 rather than as 255.
    /// </summary>
    [Fact]
    public void AnIdleRefireCounterGoesOutAsZero()
    {
        var (_, weapon) = Fresh(PlayerClass.Scout);
        weapon.ReadyToShoot = true;
        weapon.RefireTimer = WeaponState.Inactive;

        var block = CharacterWire.ToWeaponBlock(weapon, PlayerClass.Scout);

        Assert.Equal(1, block.ReadyToShoot);
        Assert.Equal(0, block.RefireAlarm);
    }

    /// <summary>
    /// 255, not 0. Zero would tell every client the medic is beaming roster slot
    /// 0, which in a headless server is the server's own reserved player.
    /// </summary>
    [Fact]
    public void AMedicWithNoPatientReportsTwoFiveFive()
    {
        var (_, medigun) = Fresh(PlayerClass.Medic);

        Assert.Equal(255, CharacterWire.ToWeaponBlock(medigun, PlayerClass.Medic).HealTarget);
    }
}
