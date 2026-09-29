using GG2.Protocol;

namespace GG2.Tests;

/// <summary>
/// The four-byte action every player produces every tick.
/// </summary>
public sealed class PlayerInputTests
{
    [Fact]
    public void RoundTripsThroughTheWire()
    {
        var input = new PlayerInput(PlayerKeys.Right | PlayerKeys.Jump, 0xBEEF, 200);

        var writer = new NetWriter();
        input.Write(writer);

        Assert.Equal(4, writer.Count);

        var reader = new NetReader(writer.WrittenSpan);
        Assert.Equal(input, PlayerInput.Read(ref reader));
        Assert.True(reader.IsEmpty);
    }

    /// <summary>
    /// The server decodes aim as <c>netAimDirection * 360 / 65536</c>
    /// (<c>processClientCommands.gml:274</c>). Exactly that, in doubles.
    /// </summary>
    [Theory]
    [InlineData(0, 0.0)]
    [InlineData(16384, 90.0)]
    [InlineData(32768, 180.0)]
    [InlineData(49152, 270.0)]
    [InlineData(65535, 359.994506835938)]
    public void AimDirectionDecodesLikeTheReference(ushort net, double degrees)
    {
        var input = new PlayerInput(PlayerKeys.None, net, 0);
        Assert.Equal(degrees, input.AimDirectionDegrees, 9);
    }

    [Fact]
    public void AimDistanceIsHalvedOnTheWire()
    {
        // aimDistance = read_ubyte(socket) * 2 — processClientCommands.gml:276.
        Assert.Equal(510, new PlayerInput(PlayerKeys.None, 0, 255).AimDistancePixels);
        Assert.Equal(84, new PlayerInput(PlayerKeys.None, 0, 42).AimDistancePixels);
    }

    [Fact]
    public void FromAimSaturatesDistanceRatherThanWrapping()
    {
        // min(255, distance / 2) — a wrap would turn a long shot into a
        // point-blank one.
        var far = PlayerInput.FromAim(PlayerKeys.Attack, 0, 100_000);
        Assert.Equal(255, far.AimDistance);
    }

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(90.0, 16384)]
    [InlineData(360.0, 0)]
    [InlineData(-90.0, 49152)]
    [InlineData(450.0, 16384)]
    public void FromAimNormalisesDirectionIntoZeroToThreeSixty(double degrees, ushort expected) =>
        Assert.Equal(expected, PlayerInput.FromAim(PlayerKeys.None, degrees, 0).NetAimDirection);

    [Fact]
    public void KeyAccessorsReadTheRightBits()
    {
        var input = new PlayerInput(PlayerKeys.Left | PlayerKeys.Attack, 0, 0);

        Assert.True(input.Left);
        Assert.True(input.Attack);
        Assert.False(input.Right);
        Assert.False(input.Jump);
        Assert.False(input.Down);
        Assert.False(input.Special);
        Assert.False(input.Taunt);
    }

    [Fact]
    public void DefaultIsNoInput()
    {
        Assert.Equal(PlayerKeys.None, PlayerInput.None.Keys);
        Assert.Equal(0, PlayerInput.None.NetAimDirection);
        Assert.Equal(0, PlayerInput.None.AimDistance);
    }
}
