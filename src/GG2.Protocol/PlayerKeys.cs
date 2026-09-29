namespace GG2.Protocol;

/// <summary>
/// The <c>keyState</c> byte carried by every <c>INPUTSTATE</c>, in both
/// directions.
/// </summary>
/// <remarks>
/// Bit 0x04 is unallocated in the reference and must be preserved as zero
/// rather than repurposed — the stock client masks against these exact bits.
/// </remarks>
[Flags]
public enum PlayerKeys : byte
{
    None = 0x00,
    Taunt = 0x01,
    Down = 0x02,
    Special = 0x08,
    Attack = 0x10,
    Right = 0x20,
    Left = 0x40,
    Jump = 0x80,
}
