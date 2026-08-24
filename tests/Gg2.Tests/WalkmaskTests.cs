using System.Security.Cryptography;
using Gg2.Maps;

namespace Gg2.Tests;

/// <summary>
/// Phase 0's exit criterion: decode every built-in map and match
/// <c>gg2-agent/tools/walkmask.js</c> cell-for-cell on all of them
/// (docs/PLAN.md).
/// </summary>
/// <remarks>
/// This is a real test with a real oracle. <c>walkmask.js</c> is the decoder
/// <c>gg2-nav-gen</c> builds nav graphs with, and a window of its output was
/// checked against <c>collision_point</c> in the live game — so agreement here
/// is agreement with the engine, not just with another guess at the format.
/// </remarks>
public sealed class WalkmaskTests
{
    public static TheoryData<MapFixture> Maps => MapFixtures.AsTheoryData();

    [Theory]
    [MemberData(nameof(Maps))]
    public void DecodesEveryBuiltInMapCellForCell(MapFixture expected)
    {
        var map = LevelMap.FromPng(expected.Name, ReferenceTree.ReadMap(expected.Name));
        var mask = map.Walkmask;

        Assert.Equal(expected.Width, mask.Width);
        Assert.Equal(expected.Height, mask.Height);
        Assert.Equal(expected.SolidCells, mask.SolidCount());

        var cells = mask.ToCellBytes();
        Assert.Equal(expected.Width * expected.Height, cells.Length);
        Assert.Equal(expected.CellsSha256, Convert.ToHexStringLower(SHA256.HashData(cells)));
    }

    /// <summary>
    /// One mask cell is six world pixels square — the scale on the collision
    /// instance (<c>CollisionMapO.events/Create.xml</c>), and also
    /// <c>NAV_CELL_SIZE</c>.
    /// </summary>
    /// <remarks>
    /// This is about the <b>walkmask's</b> extent. A map's bounds are a
    /// different number: <c>map_width()</c> measures the background art, and on
    /// <c>gg_debug</c> the two disagree. See
    /// <see cref="CollisionSampleTests.MapBoundsComeFromTheArtAndCollisionFromTheMask"/>.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Maps))]
    public void WalkmaskExtentIsSixTimesTheMask(MapFixture expected)
    {
        var map = LevelMap.FromPng(expected.Name, ReferenceTree.ReadMap(expected.Name));

        Assert.Equal(expected.Width * 6, map.Walkmask.WorldWidth);
        Assert.Equal(expected.Height * 6, map.Walkmask.WorldHeight);
    }

    /// <summary>
    /// The map's own bounds come from the art, and only <c>gg_debug</c> makes
    /// that visible.
    /// </summary>
    [Theory]
    [MemberData(nameof(Maps))]
    public void MapBoundsMatchTheWalkmaskOnEveryMapButGgDebug(MapFixture expected)
    {
        var map = LevelMap.FromPng(expected.Name, ReferenceTree.ReadMap(expected.Name));

        if (expected.Name == "gg_debug")
        {
            Assert.Equal(720, map.Width);
            Assert.Equal(384, map.Height);
            Assert.NotEqual(map.Walkmask.WorldWidth, map.Width);
            return;
        }

        Assert.Equal(map.Walkmask.WorldWidth, map.Width);
        Assert.Equal(map.Walkmask.WorldHeight, map.Height);
    }

    [Fact]
    public void SolidLookupIsOpenOutsideTheMask()
    {
        // '<' is 60, so six bits of 011100: cells (0,0)=open, (1,0)=solid,
        // (0,1)=solid, (1,1)=solid, and the remaining two bits go unused.
        var mask = Decode("""
                          {WALKMASK}
                          2
                          2
                          <
                          {END WALKMASK}
                          """);

        Assert.False(mask.IsSolid(0, 0));
        Assert.True(mask.IsSolid(1, 0));
        Assert.True(mask.IsSolid(0, 1));
        Assert.True(mask.IsSolid(1, 1));

        Assert.False(mask.IsSolid(-1, 0));
        Assert.False(mask.IsSolid(0, -1));
        Assert.False(mask.IsSolid(2, 0));
        Assert.False(mask.IsSolid(0, 2));
    }

    /// <summary>
    /// The bitstream is continuous across rows: with a width that is not a
    /// multiple of six, a row's last cell and the next row's first cell share a
    /// character. Getting this wrong produces a mask that is right on the first
    /// row and skewed afterwards — exactly the failure that is invisible by eye.
    /// </summary>
    [Fact]
    public void RowsAreNotPaddedToACharacterBoundary()
    {
        // 3x3 = 9 cells: 100 110 011, then three zero bits of padding.
        // Characters: 100110 -> 38 -> chr(70) = 'F'
        //             011000 -> 24 -> chr(56) = '8'
        var mask = Decode("""
                          {WALKMASK}
                          3
                          3
                          F8
                          {END WALKMASK}
                          """);

        Assert.Equal(new[] { true, false, false }, Row(mask, 0));
        Assert.Equal(new[] { true, true, false }, Row(mask, 1));
        Assert.Equal(new[] { false, true, true }, Row(mask, 2));
    }

    [Fact]
    public void ShortBitstreamIsRejected()
    {
        var error = Assert.Throws<MapFormatException>(() => Decode("""
                                                                  {WALKMASK}
                                                                  8
                                                                  8
                                                                  AA
                                                                  {END WALKMASK}
                                                                  """));

        Assert.Contains("short", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NonPrintableBitstreamCharacterIsRejected()
    {
        // Every legitimate data byte is chr(value + 32) for a 6-bit value, so
        // 32..95. Anything else means the block is not what it claims to be.
        var block = """
                    {WALKMASK}
                    2
                    2
                    @
                    {END WALKMASK}
                    """.Replace('@', (char)1);

        var error = Assert.Throws<MapFormatException>(() => Decode(block));

        Assert.Contains("6-bit", error.Message, StringComparison.Ordinal);
    }

    private static Walkmask Decode(string block) => Walkmask.Decode(block.ReplaceLineEndings("\n"));

    private static bool[] Row(Walkmask mask, int y)
    {
        var row = new bool[mask.Width];
        for (var x = 0; x < mask.Width; x++)
            row[x] = mask.IsSolid(x, y);
        return row;
    }
}
