using Gg2.Protocol;

namespace Gg2.Simulation;

/// <summary>
/// An axis-aligned box in world pixels, <b>inclusive on all four sides</b>.
/// </summary>
/// <remarks>
/// The same convention as <see cref="CharacterBox"/> and <see cref="SolidRect"/>:
/// a box from 2994 to 3006 covers thirteen pixels, both ends included. GM8's
/// <c>bbox_right</c> and <c>bbox_bottom</c> are inclusive too, so a box built
/// from an engine reading needs no adjustment.
/// </remarks>
public readonly record struct Aabb(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left + 1;

    public int Height => Bottom - Top + 1;

    /// <summary>
    /// The box a character occupies, as GM8's collision primitives see it.
    /// </summary>
    /// <remarks>
    /// The position is rounded half to even before the box is built — the rule
    /// docs/SIMULATION.md settles under "place_free rounds the position".
    /// <para>
    /// <b><paramref name="facing"/> is not cosmetic here, and that is a trap.</b>
    /// A character facing left has <c>image_xscale = -1</c>, and GM8 then reports
    /// a bounding box two pixels wider on the left. <c>place_free</c> and
    /// <c>place_meeting</c> ignore that — they test the precise mask — but
    /// <c>distance_to_object</c> does not, so pass the facing when computing a
    /// splash distance and leave it out when testing an overlap. See
    /// <see cref="Distance"/>.
    /// </para>
    /// </remarks>
    public static Aabb ForCharacter(in CharacterBox box, double x, double y, int facing = 1)
    {
        var ix = CollisionWorld.RoundPosition(x);
        var iy = CollisionWorld.RoundPosition(y);

        // Every class's mask is symmetric about its origin, so mirroring it
        // would change nothing — the two pixels come from GM8's own off-by-one
        // in the flipped case, not from the mask.
        var left = facing < 0 ? box.Left - 2 : box.Left;

        return new Aabb(ix + left, iy + box.Top, ix + box.Right, iy + box.Bottom);
    }

    /// <summary>
    /// The box a rotated sprite occupies: GM8's <c>bbox_*</c> for an instance
    /// whose <c>image_angle</c> is not zero.
    /// </summary>
    /// <remarks>
    /// <b>Every projectile needs this and no character does.</b> A character's
    /// <c>image_angle</c> is always 0, so <see cref="ForCharacter"/> can offset a
    /// fixed box; but <c>Scripts/Object Creation/createShot.gml</c> sets
    /// <c>image_angle = dir</c> on whatever it creates, and GM8 then reports the
    /// axis-aligned bound of the <em>rotated</em> sprite. A scattergun pellet
    /// fired flat has an 18x2 box and the same pellet fired at 45 degrees has a
    /// 13x13 one — so the angle decides who it can hit.
    /// <para>
    /// The rule, measured over 1,008 boxes across all seven projectile sprites
    /// (<c>tests/Gg2.Tests/Fixtures/rotated-box-samples.json</c>, regenerate with
    /// <c>node tools/sample-rotated-box.js</c>): take the sprite's own bbox
    /// corners relative to the origin, scale them, rotate each about the origin,
    /// take the extremes, and round each half to <b>even</b> — the same rounding
    /// as <see cref="SolidRect.FromEntity"/>, and the fourth row of docs/TRAPS-SIMULATION.md's
    /// rounding table.
    /// </para>
    /// <para>
    /// ⚠️ <b>Exact except on a rounding tie.</b> Six of those 1,008 boxes differ
    /// by one pixel, and every one of them is an angle where a rotated corner
    /// lands on a precise half — 210 and 330 degrees, where <c>sin</c> is
    /// analytically -0.5. There the x87 gap in docs/SIMULATION.md's "four motion
    /// variables" decides the tie the other way. It is unreachable in play: the
    /// scattergun adds <c>random(15)-7.5</c> to every pellet's direction, so an
    /// exact 210.000 has probability zero. Do not add a fudge for it — a fudge
    /// would be wrong at every angle instead of at two.
    /// </para>
    /// </remarks>
    /// <param name="sprite">The sprite's own bbox, in sprite pixels.</param>
    /// <param name="xOrigin">The sprite's origin, in sprite pixels.</param>
    /// <param name="yOrigin">The sprite's origin, in sprite pixels.</param>
    /// <param name="x">The instance's position. Not rounded — the rotation is.</param>
    /// <param name="y">The instance's position.</param>
    /// <param name="angle">GM8's <c>image_angle</c>, in degrees, counter-clockwise.</param>
    public static Aabb ForRotatedSprite(
        in Aabb sprite,
        double xOrigin,
        double yOrigin,
        double x,
        double y,
        double angle,
        double xScale = 1,
        double yScale = 1)
    {
        var radians = Gml.DegToRad(angle);
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);

        // The sprite's bbox corners, relative to the origin and scaled. The
        // right and bottom edges are inclusive pixel indices in GM8 and stay
        // that way through the rotation — the engine rotates the reported box,
        // not a half-open one.
        var left = (sprite.Left - xOrigin) * xScale;
        var right = (sprite.Right - xOrigin) * xScale;
        var top = (sprite.Top - yOrigin) * yScale;
        var bottom = (sprite.Bottom - yOrigin) * yScale;

        // Game Maker's y axis points down and its angles do not, hence the
        // negated sine in the y row.
        var x0 = (left * cos) + (top * sin);
        var x1 = (right * cos) + (top * sin);
        var x2 = (left * cos) + (bottom * sin);
        var x3 = (right * cos) + (bottom * sin);

        var y0 = (-left * sin) + (top * cos);
        var y1 = (-right * sin) + (top * cos);
        var y2 = (-left * sin) + (bottom * cos);
        var y3 = (-right * sin) + (bottom * cos);

        var minX = Math.Min(Math.Min(x0, x1), Math.Min(x2, x3));
        var maxX = Math.Max(Math.Max(x0, x1), Math.Max(x2, x3));
        var minY = Math.Min(Math.Min(y0, y1), Math.Min(y2, y3));
        var maxY = Math.Max(Math.Max(y0, y1), Math.Max(y2, y3));

        // Half to even, and each edge rounded on its own: the box is not the
        // rounded position plus a rounded extent, which would be off by one
        // wherever the two roundings disagree.
        return new Aabb(
            (int)(CollisionWorld.RoundPosition(x) + Gml.Round(minX)),
            (int)(CollisionWorld.RoundPosition(y) + Gml.Round(minY)),
            (int)(CollisionWorld.RoundPosition(x) + Gml.Round(maxX)),
            (int)(CollisionWorld.RoundPosition(y) + Gml.Round(maxY)));
    }

    /// <summary>
    /// The box GM8 <em>reports</em> for a rotated sprite — <c>bbox_*</c> with
    /// the flipped-sprite inflation included.
    /// </summary>
    /// <remarks>
    /// Measured 2026-08-26 (<c>tools/probes/probe-airblast.js</c>, 19 boxes on
    /// <c>AirBlastMaskS</c> plus three flipped angles on <c>SentryTurretS</c>):
    /// at <c>image_xscale = -1</c> GM8's box is the mirrored sprite box with the
    /// mirrored-left edge extended by <b>two sprite pixels before the
    /// rotation</b> — the same two pixels a left-facing character gains on
    /// <c>bbox_left</c>, but applied to the sprite-space edge and then rotated,
    /// so at 45 degrees it moves <em>two</em> world edges by one pixel each.
    /// Applying it to the rotated box instead (what <c>TurretReportedBox</c>
    /// used to assume) reproduces none of the three measured flipped rotations.
    /// <para>
    /// This is what <c>distance_to_object</c> and <c>collision_circle</c>
    /// consume; <c>place_meeting</c> and <c>collision_line</c> test the mask and
    /// never see the two pixels, which is why <see cref="ForRotatedSprite"/>
    /// stays the plain mirror.
    /// </para>
    /// </remarks>
    public static Aabb ForRotatedSpriteReported(
        in Aabb sprite,
        double xOrigin,
        double yOrigin,
        double x,
        double y,
        double angle,
        double xScale = 1,
        double yScale = 1)
    {
        // Extending sprite.Right by 2 before the negative scale is exactly
        // "mirrored left edge minus two": (right + 2 - ox) * -1 = -(right - ox) - 2.
        var box = xScale < 0 ? sprite with { Right = sprite.Right + 2 } : sprite;
        return ForRotatedSprite(box, xOrigin, yOrigin, x, y, angle, xScale, yScale);
    }

    /// <summary>
    /// GM8 <c>collision_circle(cx, cy, r, ...)</c> against one reported box.
    /// </summary>
    /// <remarks>
    /// Measured twice (docs/SIMULATION.md, "collision_circle is a plain
    /// Euclidean test"): hit iff the Euclidean distance from the centre to the
    /// <em>inclusive</em> box is <c>&lt;= r</c> — and the centre is
    /// <b>floored per axis first</b> (probe-airblast.js: every fractional
    /// centre up to +0.99 past the last integer hit still hits, including a
    /// diagonal whose true distance is 10.076 against r = 10; rounding would
    /// have missed at +0.5). Floor and truncate-toward-zero are
    /// indistinguishable at positive coordinates, which is all the probe — and
    /// the game's maps — reach.
    /// <para>
    /// The box is the <em>reported</em> one: a rotated instance answers with
    /// the axis-aligned bound of its rotated sprite (the probe's corner walk
    /// hits an AABB corner ~8px from any sprite pixel), and a flipped instance
    /// answers with the two-pixel inflation included.
    /// </para>
    /// </remarks>
    public static bool CircleHits(double cx, double cy, double r, in Aabb box)
    {
        var fx = Math.Floor(cx);
        var fy = Math.Floor(cy);
        var dx = Math.Max(0, Math.Max(box.Left - fx, fx - box.Right));
        var dy = Math.Max(0, Math.Max(box.Top - fy, fy - box.Bottom));
        return (dx * dx) + (dy * dy) <= r * r;
    }

    /// <summary>
    /// GM8 <c>place_meeting</c> between two rectangles: do they share a pixel?
    /// </summary>
    /// <remarks>
    /// Measured against the running game across 722 offsets of a <c>Rocket</c>
    /// and a <c>Shot</c> around a character
    /// (<c>tests/Gg2.Tests/Fixtures/primitive-samples.json</c>). Touching counts:
    /// boxes that share exactly one pixel column meet.
    /// </remarks>
    public bool Overlaps(in Aabb other) =>
        Left <= other.Right && other.Left <= Right &&
        Top <= other.Bottom && other.Top <= Bottom;

    /// <summary>
    /// GM8 <c>distance_to_object</c>: the straight-line gap between two boxes,
    /// zero when they touch or overlap.
    /// </summary>
    /// <remarks>
    /// <code>
    /// gapx = max(0, a.left - b.right, b.left - a.right)
    /// gapy = max(0, a.top - b.bottom, b.top - a.bottom)
    /// distance = hypot(gapx, gapy)
    /// </code>
    /// Exact on all 722 sampled offsets, with no rounding rule and no epsilon.
    /// <para>
    /// <b>This is the function every splash radius in the game is built on</b> —
    /// <c>Rocket.events/User Event 5.xml</c> gates the hit on
    /// <c>distance_to_object(other) &lt; other.blastRadius</c> and then scales
    /// the damage by <c>1 - distance/blastRadius</c> — so an error here is a
    /// wrong damage number rather than a wrong pixel.
    /// </para>
    /// <para>
    /// It also takes the boxes it is <em>given</em>, and GM8 gives it the
    /// facing-inflated one. Measured directly: a character at x = 3000 with a
    /// rocket whose box ends at 2986 is 8 away facing right and <b>6</b> away
    /// facing left, same coordinates, same tick. Splash damage in GG2 genuinely
    /// depends on which way the victim is looking.
    /// </para>
    /// </remarks>
    public double Distance(in Aabb other)
    {
        var gapX = Math.Max(0, Math.Max(Left - other.Right, other.Left - Right));
        var gapY = Math.Max(0, Math.Max(Top - other.Bottom, other.Top - Bottom));

        // Both gaps are non-negative integers, so this is the plain Euclidean
        // hypotenuse — no GML fuzzy comparison is involved.
        //
        // Spelled out rather than double.Hypot, and that is not style. Hypot
        // rescales its arguments to avoid overflow, which changes the last bit:
        // checked against the fixture, sqrt(x*x + y*y) matches the engine on all
        // 722 samples and Hypot matches 636 of them. GML's own point_distance is
        // the naive form, so the naive form is the one to write.
        return Math.Sqrt(((double)gapX * gapX) + ((double)gapY * gapY));
    }
}
