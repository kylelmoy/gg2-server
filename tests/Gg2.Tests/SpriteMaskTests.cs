using Gg2.Maps;

namespace Gg2.Tests;

/// <summary>
/// The embedded-sprite decoders, against three real resources lifted out of the
/// map archive.
/// </summary>
/// <remarks>
/// ⛔ <b>These are REGRESSION values, not verified truth.</b> Nothing here has
/// been checked against the running game: the counts are what this port's
/// decoders produce today, pinned so a change to them is visible. The thing they
/// cannot tell you is whether GM8 agrees — in particular whether
/// <c>removeback</c> really samples the <b>bottom-left</b> pixel, which is taken
/// from Game Maker's documentation and not from a measurement. The plan to settle
/// that is in <c>docs/TRAPS-SIMULATION.md</c>, "Verifying the precise mask".
/// <para>
/// The fixtures are the three smallest platform sprites in the archive — 82, 161
/// and 331 bytes — chosen so a real PNG and a real GIF are both covered without
/// checking in anything large. <c>sprite-ji.png</c> is an RGBA PNG,
/// <c>sprite-eleplat.png</c> is an opaque one that only <c>removeback</c> carves,
/// and <c>sprite-bubble1.gif</c> is one of <c>maw_deathmatch</c>'s 139 bubbles.
/// </para>
/// </remarks>
public sealed class SpriteMaskTests
{
    private static byte[] Fixture(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    /// <summary>An RGBA PNG: transparency comes from the alpha channel.</summary>
    [Fact]
    public void AnRgbaPngDecodes()
    {
        var mask = SpriteMask.Decode(Fixture("sprite-ji.png"));

        Assert.NotNull(mask);
        Assert.Equal(46, mask.Width);
        Assert.Equal(5, mask.Height);
        Assert.Equal(184, mask.SolidCount);
    }

    /// <summary>
    /// An opaque PNG, where every transparent pixel comes from
    /// <c>removeback</c>.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>This is the fixture that would move if the background rule is
    /// wrong.</b> The file carries no alpha and no <c>tRNS</c>, so with
    /// <c>removeback</c> off it would be a full 10x2 rectangle; half of it
    /// disappears because the bottom-left pixel's colour is removed. If GM8
    /// samples a different corner, this count changes and the platform's shape
    /// changes with it.
    /// </remarks>
    [Fact]
    public void AnOpaquePngIsCarvedOnlyByRemoveback()
    {
        var carved = SpriteMask.Decode(Fixture("sprite-eleplat.png"));
        var whole = SpriteMask.Decode(Fixture("sprite-eleplat.png"), removeBackground: false);

        Assert.NotNull(carved);
        Assert.NotNull(whole);

        Assert.Equal(10, carved.Width);
        Assert.Equal(2, carved.Height);

        // Nothing but removeback is removing anything here.
        Assert.Equal(whole.Width * whole.Height, whole.SolidCount);
        Assert.Equal(10, carved.SolidCount);
    }

    /// <summary>A GIF: transparency comes from the Graphic Control Extension.</summary>
    /// <remarks>
    /// One of <c>maw_deathmatch</c>'s bubbles. 576 of 1,600 pixels solid — 36% —
    /// which is the number that makes a bounding box around it obviously wrong.
    /// </remarks>
    [Fact]
    public void AGifDecodes()
    {
        var mask = SpriteMask.Decode(Fixture("sprite-bubble1.gif"));

        Assert.NotNull(mask);
        Assert.Equal(40, mask.Width);
        Assert.Equal(40, mask.Height);
        Assert.Equal(576, mask.SolidCount);
    }

    /// <summary>
    /// ⚠️ A bubble is round, and the mask has to be too.
    /// </summary>
    /// <remarks>
    /// The whole argument against using a bounding box rests on this being a disc
    /// rather than a square, so it is worth asserting rather than eyeballing: the
    /// four corners are clear, the centre is solid.
    /// </remarks>
    [Fact]
    public void TheBubbleIsRoundRatherThanRectangular()
    {
        var mask = SpriteMask.Decode(Fixture("sprite-bubble1.gif"))!;

        Assert.True(mask.IsSolid(20, 20), "the centre should be solid");

        Assert.False(mask.IsSolid(0, 0), "top-left corner");
        Assert.False(mask.IsSolid(39, 0), "top-right corner");
        Assert.False(mask.IsSolid(0, 39), "bottom-left corner");
        Assert.False(mask.IsSolid(39, 39), "bottom-right corner");
    }

    /// <summary>
    /// A resource that is not an image gives no mask, and therefore no collision.
    /// </summary>
    /// <remarks>
    /// Exactly what GM8 does: <c>stringToResource</c> returns -1, the platform
    /// keeps <c>sprite_index = -1</c>, and nothing meets it. <c>animap</c> in
    /// <c>ctf_2dfort-ani</c> is a real instance — nine bytes of truncated GIF.
    /// </remarks>
    [Fact]
    public void AnUnrecognisedResourceGivesNoMask()
    {
        Assert.Null(SpriteMask.Decode("GIF89"u8));
        Assert.Null(SpriteMask.Decode("not an image at all"u8));
        Assert.Null(SpriteMask.Decode([]));
    }

    /// <summary>Outside the sprite is never solid, whatever the coordinate.</summary>
    [Fact]
    public void OutsideTheSpriteIsNeverSolid()
    {
        var mask = SpriteMask.Decode(Fixture("sprite-ji.png"))!;

        Assert.False(mask.IsSolid(-1, 0));
        Assert.False(mask.IsSolid(0, -1));
        Assert.False(mask.IsSolid(46, 0));
        Assert.False(mask.IsSolid(0, 5));
        Assert.False(mask.IsSolid(int.MaxValue, int.MaxValue));
    }
}
