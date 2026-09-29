using GG2.Protocol;

namespace GG2.Tests;

/// <summary>
/// How a GML real becomes an integer on the wire.
/// </summary>
/// <remarks>
/// Transcribed from Faucet Networking's <c>writeIntValue</c>
/// (<c>faucet/ReadWritable.hpp</c>):
/// <code>
/// IntType converted = clipped_cast&lt;IntType&gt;(round(value));
/// </code>
/// Two rules that a from-scratch port gets wrong by default, because the
/// obvious C# spelling of each is the other answer: <c>Math.Round</c> is
/// banker's, and an integer cast wraps.
/// </remarks>
public sealed class WireQuantiseTests
{
    /// <summary>
    /// C++ <c>std::round</c> is half <b>away from zero</b>. .NET's
    /// <c>Math.Round</c> defaults to half-to-even, which disagrees on exactly
    /// the values a quantised position lands on most often — <c>x * 5</c> is
    /// half-integral whenever x is a tenth.
    /// </summary>
    [Theory]
    [InlineData(0.5, 1)]
    [InlineData(1.5, 2)]
    [InlineData(2.5, 3)]   // banker's would say 2
    [InlineData(3.5, 4)]
    [InlineData(4.5, 5)]   // banker's would say 4
    [InlineData(0.4, 0)]
    [InlineData(0.6, 1)]
    public void RoundsHalfAwayFromZero(double value, int expected) =>
        Assert.Equal(expected, WireQuantise.ToUShort(value));

    [Theory]
    [InlineData(-0.5, -1)]
    [InlineData(-1.5, -2)]
    [InlineData(-2.5, -3)] // banker's would say -2
    [InlineData(-0.4, 0)]
    public void RoundsNegativeHalvesAwayFromZeroToo(double value, int expected) =>
        Assert.Equal(expected, WireQuantise.ToByte(value));

    /// <summary>
    /// <c>clipped_cast</c> saturates. A C# cast would wrap, turning a
    /// far-off-map coordinate into a plausible on-map one — which is exactly the
    /// bug that would be hardest to spot.
    /// </summary>
    [Fact]
    public void ClampsInsteadOfWrapping()
    {
        Assert.Equal(65535, WireQuantise.ToUShort(70000));
        Assert.Equal(0, WireQuantise.ToUShort(-1));
        Assert.Equal(255, WireQuantise.ToUByte(300));
        Assert.Equal(0, WireQuantise.ToUByte(-5));
        Assert.Equal(127, WireQuantise.ToByte(200));
        Assert.Equal(-128, WireQuantise.ToByte(-200));
        Assert.Equal(32767, WireQuantise.ToShort(40000));
        Assert.Equal(-32768, WireQuantise.ToShort(-40000));
    }

    /// <summary>
    /// The velocity field is asymmetric, and it is not a bug in this port.
    /// <c>Character</c> clamps speed to ±15 and writes <c>hspeed * 8.5</c> as a
    /// signed byte: +15 gives 127.5, which rounds to 128 and clamps to 127,
    /// while -15 gives -127.5, which rounds to -128 and fits exactly.
    /// </summary>
    [Fact]
    public void MaximumVelocityQuantisesAsymmetrically()
    {
        Assert.Equal(127, WireQuantise.ToByte(15 * 8.5));
        Assert.Equal(-128, WireQuantise.ToByte(-15 * 8.5));
    }

    /// <summary>
    /// The extension's rounding is <em>not</em> GML's. The reference applies
    /// GML's <c>round()</c> to some fields and not others, so both spellings
    /// have to exist and the call site has to pick.
    /// </summary>
    [Fact]
    public void GmlRoundIsHalfToEvenAndTheExtensionsIsNot()
    {
        Assert.Equal(0, Gml.Round(0.5));
        Assert.Equal(2, Gml.Round(1.5));
        Assert.Equal(2, Gml.Round(2.5));
        Assert.Equal(-2, Gml.Round(-1.5));

        Assert.Equal(1, WireQuantise.ToUShort(0.5));
        Assert.Equal(3, WireQuantise.ToUShort(2.5));
    }

    /// <summary>
    /// Applying GML's round first, as <c>Intelligence</c> does, leaves the
    /// extension nothing to do — so the composite is half-to-even. That is a
    /// different answer from <c>Character</c>'s plain <c>x * 5</c> on the same
    /// coordinate.
    /// </summary>
    [Fact]
    public void IntelAndCharacterQuantisePositionsDifferently()
    {
        // x = 100.1 -> x * 5 = 500.5, the half-integral case.
        const double x = 100.1;

        var character = WireQuantise.ToUShort(x * 5);
        var intel = WireQuantise.ToUShort(Gml.Round(x * 5));

        Assert.Equal(501, character); // away from zero
        Assert.Equal(500, intel);     // to even
        Assert.NotEqual(character, intel);
    }

    [Fact]
    public void WholeNumbersAreUnchanged()
    {
        Assert.Equal(12345, WireQuantise.ToUShort(12345));
        Assert.Equal(-42, WireQuantise.ToByte(-42));
        Assert.Equal(4_000_000_000, WireQuantise.ToUInt(4_000_000_000));
    }

    [Fact]
    public void WriterHelpersApplyTheSameConversion()
    {
        var writer = new NetWriter();
        writer.WriteUShortFromReal(2.5);
        writer.WriteByteFromReal(-2.5);
        writer.WriteUByteFromReal(300);

        var reader = new NetReader(writer.WrittenSpan);
        Assert.Equal(3, reader.ReadUShort());
        Assert.Equal(-3, reader.ReadByte());
        Assert.Equal(255, reader.ReadUByte());
    }

    /// <summary>
    /// The client writes <c>point_direction(...) * 65536 / 360</c> unrounded, so
    /// a bot choosing the same angle must land on the same byte pair.
    /// </summary>
    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(90.0, 16384)]
    [InlineData(180.0, 32768)]
    [InlineData(270.0, 49152)]
    [InlineData(45.0, 8192)]
    public void AimQuantisationMatchesTheClientsArithmetic(double degrees, ushort expected)
    {
        Assert.Equal(expected, PlayerInput.FromAim(PlayerKeys.None, degrees, 0).NetAimDirection);
        Assert.Equal(expected, WireQuantise.ToUShort(degrees * 65536.0 / 360.0));
    }

    /// <summary>
    /// An angle that does not land on a whole step rounds away from zero, and
    /// the field never wraps: 360 degrees would be 65536, which clamps.
    /// </summary>
    [Fact]
    public void AimNeverWrapsThroughTheTopOfTheField()
    {
        // Normalised to 0 before quantising, which is what point_direction
        // already guarantees.
        Assert.Equal(0, PlayerInput.FromAim(PlayerKeys.None, 360.0, 0).NetAimDirection);

        // But the raw arithmetic on an un-normalised 360 would clamp, not wrap.
        Assert.Equal(65535, WireQuantise.ToUShort(360.0 * 65536.0 / 360.0));
    }
}
