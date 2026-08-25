using Gg2.Protocol;

namespace Gg2.Simulation;

/// <summary>
/// A character's simulation state as the wire carries it.
/// </summary>
/// <remarks>
/// <b>One copy of this conversion, on purpose.</b> The live server and the
/// parity replayer both quantise characters for the wire, and if they did it
/// separately then a recording could pass every parity gate while the server
/// shipped different bytes — which would make docs/PARITY.md prove the wrong
/// program. The replayer's whole claim is that it exercises what the server
/// runs.
/// </remarks>
public static class CharacterWire
{
    /// <summary>
    /// Quantises a character into the block
    /// <c>Character.events/User Event 12.xml</c> writes.
    /// </summary>
    /// <remarks>
    /// Position is a plain <c>x*5</c> with no GML <c>round()</c> in front of it,
    /// so the extension's own half-away-from-zero rule is the entire
    /// quantisation — unlike <c>Intelligence</c>, which writes
    /// <c>round(x*5)</c>. CLAUDE.md's rounding table has the five cases and why
    /// they differ; this is the one that matters most often.
    /// </remarks>
    /// <param name="state">The character.</param>
    /// <param name="weapon">
    /// Its weapon. The ammo byte and — for three of the ten classes — the
    /// class-specific byte come out of it, so a character cannot be serialised
    /// without one. The reference cannot have a character without one either.
    /// </param>
    public static CharacterBlock ToBlock(in CharacterState state, in WeaponState weapon) => new()
    {
        Keys = state.KeyState,
        NetAimDirection = state.NetAimDirection,
        AimDistance = state.AimDistance,
        HasPosition = true,

        X = WireQuantise.ToUShort(state.X * 5),
        Y = WireQuantise.ToUShort(state.Y * 5),
        HSpeed = WireQuantise.ToByte(state.HSpeed * 8.5),
        VSpeed = WireQuantise.ToByte(state.VSpeed * 8.5),
        Hp = WireQuantise.ToUByte(Math.Ceiling(state.Hp)),
        Ammo = WireQuantise.ToUByte(weapon.AmmoCount),

        // Bit 0 is the cloak; bits 1..3 are moveStatus.
        Flags = (byte)((state.Cloak ? 0x01 : 0x00) | ((state.MoveStatus & 0x07) << 1)),

        HasFullDetail = true,

        // animationOffset picks which of the character's sprite sets the client
        // draws (CHARACTER_ANIMATION_NORMAL / _INTEL). It is a drawing
        // selector, and no phase owns it yet.
        AnimationOffset = 0,

        ClassSpecific = ClassSpecificByte(state, weapon),
        Alarm1 = WireQuantise.ToShort(state.IntelGrabTimer),
        Intel = state.CarryingIntel ? (byte)1 : (byte)0,
        IntelRecharge = WireQuantise.ToShort(state.IntelRecharge),
    };

    /// <summary>
    /// The one byte four classes use for something and six write as zero.
    /// </summary>
    /// <remarks>
    /// <c>Character.events/User Event 12.xml</c>'s <c>switch(player.class)</c>.
    /// Note that three of the four read it off the <em>weapon</em> and one off
    /// the character, and that only the medic's is scaled — the sniper's charge
    /// and the engineer's nuts and bolts go out unscaled because both already
    /// fit in a byte.
    /// </remarks>
    private static byte ClassSpecificByte(in CharacterState state, in WeaponState weapon) => state.Class switch
    {
        PlayerClass.Spy => WireQuantise.ToUByte(state.CloakAlpha * 255),
        PlayerClass.Medic => WireQuantise.ToUByte(weapon.UberCharge * 255 / WeaponState.MaxUberCharge),
        PlayerClass.Engineer => WireQuantise.ToUByte(state.NutsNBolts),
        PlayerClass.Sniper => WireQuantise.ToUByte(weapon.Charge),
        _ => 0,
    };

    /// <summary>
    /// Quantises a weapon into the block that closes a <c>FULL_UPDATE</c>
    /// character.
    /// </summary>
    /// <remarks>
    /// <c>alarm[0]</c> rests at -1 when nothing is pending and the reference
    /// writes it through <c>write_ubyte</c>, which clamps — so an idle weapon
    /// sends 0 and a client reading it back gets "no refire pending", which is
    /// what it is.
    /// </remarks>
    public static WeaponBlock ToWeaponBlock(in WeaponState weapon, PlayerClass playerClass) => new()
    {
        Class = playerClass,
        ReadyToShoot = weapon.ReadyToShoot ? (byte)1 : (byte)0,
        RefireAlarm = WireQuantise.ToUByte(weapon.RefireTimer),
        HealTarget = weapon.HealTarget,
        MinesLobbed = WireQuantise.ToUByte(weapon.MinesLobbed),
    };
}
