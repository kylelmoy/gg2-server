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
    public static CharacterBlock ToBlock(in CharacterState state) => new()
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

        // Weapons are phase 5, so there is no ammo to report and no cloak bit
        // to set. moveStatus is real and occupies bits 1..3.
        Ammo = 0,
        Flags = (byte)((state.MoveStatus & 0x07) << 1),
    };
}
