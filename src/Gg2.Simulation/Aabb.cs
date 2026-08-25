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
