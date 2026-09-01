using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// A character's rectangle against a moving platform's per-pixel mask.
/// </summary>
/// <remarks>
/// ⚠️ <b>None of this is verified against the running game yet.</b> These pin
/// the geometry this port implements — origin, scaling, inclusive bounds,
/// rounding — so that a later probe has something exact to disagree with. The
/// plan for that probe is in <c>docs/TRAPS-SIMULATION.md</c>. Until it runs, a
/// green suite here means "self-consistent", not "matches GM8".
/// </remarks>
public sealed class PreciseCollisionTests
{
    /// <summary>A 4x4 mask with a single solid pixel at (1,1).</summary>
    private static SpriteMask OnePixel()
    {
        var solid = new bool[16];
        solid[(1 * 4) + 1] = true;
        return new SpriteMask(4, 4, solid);
    }

    /// <summary>A 1x1 character box, so a test is about one pixel meeting another.</summary>
    private static CharacterBox Point => new(0, 0, 0, 0);

    private static bool Meets(SpriteMask mask, double sx, double sy, double cx, double cy, int scale = 1) =>
        PreciseCollision.Meets(mask, sx, sy, scale, scale, Point, cx, cy);

    /// <summary>
    /// ⚠️ The origin is the sprite's TOP-LEFT, not its centre.
    /// </summary>
    /// <remarks>
    /// <c>sprite_add(name, 1, 1, 0, 0, 0)</c> — the last two arguments are
    /// <c>xorig</c>/<c>yorig</c> and both are zero. A centred origin would put
    /// every platform half a sprite away from where the client draws it, which is
    /// the kind of error that looks like "collision is slightly off" for ever.
    /// </remarks>
    [Fact]
    public void TheSpriteOriginIsItsTopLeft()
    {
        var mask = OnePixel();

        // The solid pixel is at sprite (1,1), so with the sprite at (100,200) it
        // is at room (101,201) and nowhere else.
        Assert.True(Meets(mask, 100, 200, 101, 201));

        Assert.False(Meets(mask, 100, 200, 100, 200));
        Assert.False(Meets(mask, 100, 200, 99, 199));
    }

    [Fact]
    public void TransparentPixelsDoNotCollide()
    {
        var mask = OnePixel();

        // Every other pixel of the 4x4 is clear.
        for (var y = 0; y < 4; y++)
        for (var x = 0; x < 4; x++)
        {
            if (x == 1 && y == 1)
                continue;

            Assert.False(Meets(mask, 0, 0, x, y), $"({x},{y}) should be transparent");
        }
    }

    /// <summary>A scaled sprite covers scale x scale room pixels per source pixel.</summary>
    /// <remarks>
    /// Real maps use 1, 2, 3 and 6. At scale 3 the single solid pixel at (1,1)
    /// covers room [3,5] x [3,5] — nine pixels — and the boundary either side of
    /// that is where an off-by-one lives.
    /// </remarks>
    [Fact]
    public void ScalingExpandsEachSourcePixel()
    {
        var mask = OnePixel();

        for (var y = 3; y <= 5; y++)
        for (var x = 3; x <= 5; x++)
            Assert.True(Meets(mask, 0, 0, x, y, scale: 3), $"({x},{y}) should be covered");

        Assert.False(Meets(mask, 0, 0, 2, 3, scale: 3));
        Assert.False(Meets(mask, 0, 0, 6, 3, scale: 3));
        Assert.False(Meets(mask, 0, 0, 3, 2, scale: 3));
        Assert.False(Meets(mask, 0, 0, 3, 6, scale: 3));
    }

    /// <summary>
    /// The character's box is inclusive at both ends, as
    /// <c>PlaceFreeInMask</c>'s is.
    /// </summary>
    /// <remarks>
    /// A scout spans <c>[x-6, x+6]</c> — thirteen pixels, not twelve. Treating
    /// the right edge as exclusive would let a character stand one pixel inside a
    /// platform.
    /// </remarks>
    [Fact]
    public void TheCharacterBoxIsInclusiveAtBothEnds()
    {
        var mask = OnePixel();
        var scout = CharacterBox.For(PlayerClass.Scout);

        // The solid pixel sits at room (1,1). A scout whose right edge lands
        // exactly on it must collide: x + 6 == 1, so x == -5.
        Assert.True(PreciseCollision.Meets(mask, 0, 0, 1, 1, scout, -5, 1 - scout.Bottom));

        // One further left and its right edge is at 0, which is clear.
        Assert.False(PreciseCollision.Meets(mask, 0, 0, 1, 1, scout, -6, 1 - scout.Bottom));
    }

    /// <summary>
    /// Positions are rounded half to even before the test, as <c>place_free</c>
    /// does.
    /// </summary>
    /// <remarks>
    /// ⚠️ Half to EVEN, so 0.5 rounds to 0 and 1.5 rounds to 2 — GML's
    /// <c>round</c>, not "away from zero". A character is almost never on an
    /// integer coordinate, so this rule decides nearly every real query.
    /// </remarks>
    [Fact]
    public void PositionsRoundHalfToEven()
    {
        var mask = OnePixel();

        // 1.5 -> 2, which misses the pixel at 1.
        Assert.False(Meets(mask, 0, 0, 1.5, 1));

        // 0.5 -> 0, which also misses.
        Assert.False(Meets(mask, 0, 0, 0.5, 1));

        // 1.4 -> 1 and 0.6 -> 1, which both hit.
        Assert.True(Meets(mask, 0, 0, 1.4, 1));
        Assert.True(Meets(mask, 0, 0, 0.6, 1));
    }

    /// <summary>A sprite the character is nowhere near costs nothing and answers false.</summary>
    [Fact]
    public void ADistantSpriteDoesNotCollide()
    {
        var solid = new bool[973 * 198];
        Array.Fill(solid, true);

        var mask = new SpriteMask(973, 198, solid);

        Assert.False(Meets(mask, 0, 0, 5000, 5000));
        Assert.True(Meets(mask, 0, 0, 500, 100));
    }

    [Fact]
    public void AZeroScaleCannotCollide() =>
        Assert.False(PreciseCollision.Meets(OnePixel(), 0, 0, 0, 0, Point, 1, 1));

    /// <summary>Real maps only ever use whole-number scales.</summary>
    /// <remarks>
    /// ⚠️ Pins the assumption rather than the behaviour: <c>image_xscale</c> is a
    /// real in GM8 and a fractional one would need sub-pixel sampling. Nothing in
    /// the archive uses one — see <see cref="PreciseCollision.ScaleOf"/>.
    /// </remarks>
    [Theory]
    [InlineData(1.0, 1)]
    [InlineData(2.0, 2)]
    [InlineData(6.0, 6)]
    [InlineData(0.0, 1)]
    [InlineData(0.5, 1)]
    public void ScaleOfClampsToAUsableInteger(double scale, int expected) =>
        Assert.Equal(expected, PreciseCollision.ScaleOf(scale));
}
