using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// The two oracles the airblast was blocked on, measured 2026-08-26 against the
/// running engine: <c>AirBlastMaskS</c>'s reported box at both
/// <c>image_xscale</c> signs, and <c>collision_circle</c> with a fractional
/// centre. Every row below is a raw probe output
/// (<c>tools/probes/probe-airblast.js</c>, <c>probe-airblast2.js</c>) — none is
/// derived from the model it gates.
/// </summary>
/// <remarks>
/// The flipped rows are the ones with teeth: the two-pixel <c>bbox_left</c>
/// inflation is applied to the mirrored sprite edge <em>before</em> the
/// rotation. Applying it to the rotated box instead (what the turret assumed
/// until this measurement) reproduces the angle-0 rows and none of the rotated
/// ones — <c>tf45</c>'s bottom would come out 15 instead of 16 and
/// <c>tf90</c> would be wrong on two edges.
/// </remarks>
public sealed class AirBlastBoxTests
{
    /// <summary><c>AirBlastMaskS</c>: bbox 9,1,97,28 about origin (1, 14).</summary>
    private static readonly Aabb AirBlast = new(9, 1, 97, 28);

    /// <summary><c>SentryTurretS</c>: bbox 3,4,33,15 about origin (15, 12).</summary>
    private static readonly Aabb Turret = new(3, 4, 33, 15);

    /// <summary><c>SentryMaskS</c>, expressed so the origin-relative box is -8,-7,7,12.</summary>
    private static readonly Aabb SentryMask = new(0, 0, 15, 19);

    // probe-airblast.js, xscale +1. Offsets are bbox_* minus the instance
    // position, at (1000, 1000).
    [Theory]
    [InlineData(0.0, 8, -13, 96, 14)]
    [InlineData(45.0, -4, -77, 78, 4)]
    [InlineData(90.0, -13, -96, 14, -8)]
    [InlineData(135.0, -77, -78, 4, 4)]
    [InlineData(180.0, -96, -14, -8, 13)]
    [InlineData(225.0, -78, -4, 4, 77)]
    [InlineData(270.0, -14, 8, 13, 96)]
    [InlineData(315.0, -4, -4, 77, 78)]
    [InlineData(30.0, 0, -59, 90, 8)]
    [InlineData(33.75, -1, -64, 88, 7)]
    public void TheAirBlastBoxMatchesTheEngineFacingRight(
        double angle, int left, int top, int right, int bottom)
    {
        var box = Aabb.ForRotatedSpriteReported(AirBlast, 1, 14, 1000, 1000, angle);
        Assert.Equal(new Aabb(1000 + left, 1000 + top, 1000 + right, 1000 + bottom), box);
    }

    // probe-airblast.js, xscale -1 — a left-facing pyro's poof, whose
    // image_angle is aim + 180.
    [Theory]
    [InlineData(0.0, -98, -13, -8, 14)]
    [InlineData(45.0, -78, -4, 4, 79)]
    [InlineData(90.0, -13, 8, 14, 98)]
    [InlineData(135.0, -4, -4, 79, 78)]
    [InlineData(180.0, 8, -14, 98, 13)]
    [InlineData(225.0, -4, -79, 78, 4)]
    [InlineData(270.0, -14, -98, 13, -8)]
    [InlineData(315.0, -79, -78, 4, 4)]
    [InlineData(213.75, -1, -66, 89, 6)]
    public void TheAirBlastBoxMatchesTheEngineFacingLeft(
        double angle, int left, int top, int right, int bottom)
    {
        var box = Aabb.ForRotatedSpriteReported(AirBlast, 1, 14, 1000, 1000, angle, xScale: -1);
        Assert.Equal(new Aabb(1000 + left, 1000 + top, 1000 + right, 1000 + bottom), box);
    }

    // probe-airblast.js's bonus rows plus probe-turret.js's angle-0 flip: the
    // turret, which settles the open "flipped bbox under rotation" question.
    [Theory]
    [InlineData(0.0, -20, -8, 12, 3)]
    [InlineData(45.0, -20, -14, 11, 16)]
    [InlineData(90.0, -8, -12, 3, 20)]
    [InlineData(315.0, -16, -20, 14, 11)]
    public void TheFlippedTurretBoxMatchesTheEngine(
        double angle, int left, int top, int right, int bottom)
    {
        var box = Aabb.ForRotatedSpriteReported(Turret, 15, 12, 1000, 1000, angle, xScale: -1);
        Assert.Equal(new Aabb(1000 + left, 1000 + top, 1000 + right, 1000 + bottom), box);
    }

    /// <summary>probe-airblast2.js: a second sprite's flip, read directly.</summary>
    [Fact]
    public void TheFlippedSentryMaskMatchesTheEngine()
    {
        var box = Aabb.ForRotatedSpriteReported(SentryMask, 8, 7, 1000, 1000, 0, xScale: -1);
        Assert.Equal(new Aabb(991, 993, 1008, 1012), box);
    }

    /// <summary>An unflipped sprite is untouched by the reported form.</summary>
    [Fact]
    public void TheReportedBoxIsThePlainOneWhenNotFlipped()
    {
        Assert.Equal(
            Aabb.ForRotatedSprite(Turret, 15, 12, 1000, 1000, 45),
            Aabb.ForRotatedSpriteReported(Turret, 15, 12, 1000, 1000, 45));
    }

    // collision_circle against the SentryMaskS probe box, abs 992,993,1007,1012,
    // r = 10 throughout. The integer rows are probe-circle.js; the fractional
    // rows are probe-airblast.js and they are the ones that pin the floored
    // centre: 1017.25 through 1017.75 all hit (floor), where the true distance
    // is over 10 (real centre would miss all three) and rounding would miss
    // from 1017.5. The diagonal at +7.125 is 10.076 away as a real and still
    // hits.
    [Theory]
    [InlineData(1017.0, 1000.0, true)]
    [InlineData(1018.0, 1000.0, false)]
    [InlineData(982.0, 1000.0, true)]
    [InlineData(981.0, 1000.0, false)]
    [InlineData(1000.0, 1022.0, true)]
    [InlineData(1000.0, 1023.0, false)]
    [InlineData(1014.0, 1019.0, true)]
    [InlineData(1015.0, 1020.0, false)]
    [InlineData(1016.75, 1000.0, true)]
    [InlineData(1017.25, 1000.0, true)]
    [InlineData(1017.5, 1000.0, true)]
    [InlineData(1017.75, 1000.0, true)]
    [InlineData(1000.0, 1021.75, true)]
    [InlineData(1000.0, 1022.25, true)]
    [InlineData(1000.0, 1022.5, true)]
    [InlineData(1014.0625, 1019.0625, true)]
    [InlineData(1014.125, 1019.125, true)]
    public void CollisionCircleFloorsItsCentre(double cx, double cy, bool hits)
    {
        var box = new Aabb(992, 993, 1007, 1012);
        Assert.Equal(hits, Aabb.CircleHits(cx, cy, 10, box));
    }
}
