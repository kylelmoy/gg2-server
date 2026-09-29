using GG2.Maps;

namespace GG2.Simulation;

/// <summary>
/// A character's rectangle against a moving platform's per-pixel mask.
/// </summary>
/// <remarks>
/// The one place in this project where a collision is not rectangle-vs-rectangle
/// or rectangle-vs-walkmask. A <c>MovingPlatform</c>'s sprite is an arbitrary
/// image the map author embedded and GM8 collides it per pixel
/// (<c>docs/SIMULATION.md</c>, "Runtime <c>sprite_add</c> sprites are precise").
/// <para>
/// <b>A character's own mask stays a RECTANGLE.</b> Every class's box is
/// <see cref="CharacterBox"/> and the reference's masks are rectangles, so this
/// is rect-vs-precise, not precise-vs-precise. Getting that backwards would make
/// characters collide by their sprite art, which is not what the game does.
/// </para>
/// <para>
/// <b>The sprite origin is its TOP-LEFT.</b> <c>stringToResource.gml</c> calls
/// <c>sprite_add(name, 1, 1, 0, 0, 0)</c>, whose last two arguments are
/// <c>xorig</c> and <c>yorig</c> — both zero. So a platform at <c>(x, y)</c>
/// covers <c>[x, x + width*xscale)</c> and <c>[y, y + height*yscale)</c>, and does
/// NOT extend left or above its position. A centred origin would put every
/// platform half a sprite from where the client draws it.
/// </para>
/// </remarks>
public static class PreciseCollision
{
    /// <summary>
    /// Whether a character at <paramref name="characterX"/>,
    /// <paramref name="characterY"/> overlaps the sprite placed at
    /// <paramref name="spriteX"/>, <paramref name="spriteY"/>.
    /// </summary>
    /// <remarks>
    /// Both positions are rounded the way <c>place_free</c> rounds — half to
    /// even, <see cref="CollisionWorld.RoundPosition"/> — before anything is
    /// compared, because a character is almost never on an integer coordinate and
    /// the reference rounds before it tests.
    /// <para>
    /// The character's box is INCLUSIVE at both ends, matching
    /// <see cref="CollisionWorld.PlaceFreeInMask"/>: a scout spans
    /// <c>[x-6, x+6]</c>, thirteen pixels, not twelve.
    /// </para>
    /// </remarks>
    public static bool Meets(
        SpriteMask mask,
        double spriteX,
        double spriteY,
        int xScale,
        int yScale,
        in CharacterBox box,
        double characterX,
        double characterY)
    {
        ArgumentNullException.ThrowIfNull(mask);

        if (xScale <= 0 || yScale <= 0)
            return false;

        var originX = CollisionWorld.RoundPosition(characterX);
        var originY = CollisionWorld.RoundPosition(characterY);

        var left = originX + box.Left;
        var right = originX + box.Right;
        var top = originY + box.Top;
        var bottom = originY + box.Bottom;

        var spriteLeft = CollisionWorld.RoundPosition(spriteX);
        var spriteTop = CollisionWorld.RoundPosition(spriteY);

        // Clip to where the two can possibly meet, so a 973x198 sprite does not
        // cost a scan of itself for a character standing nowhere near it.
        var firstX = Math.Max(left, spriteLeft);
        var lastX = Math.Min(right, spriteLeft + (mask.Width * xScale) - 1);
        var firstY = Math.Max(top, spriteTop);
        var lastY = Math.Min(bottom, spriteTop + (mask.Height * yScale) - 1);

        if (firstX > lastX || firstY > lastY)
            return false;

        for (var y = firstY; y <= lastY; y++)
        {
            // Integer division is floor here only because the clip above
            // guarantees a non-negative offset. Do not remove the clip.
            var sourceY = (y - spriteTop) / yScale;

            for (var x = firstX; x <= lastX; x++)
            {
                if (mask.IsSolid((x - spriteLeft) / xScale, sourceY))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// One moving platform, made solid for the length of a single query.
    /// </summary>
    /// <remarks>
    /// <b>The reference does exactly this and it is the oddest three lines in
    /// the End Step:</b> <c>other.solid = true; good_move_contact_solid(...);
    /// other.solid = false;</c>. A platform is <b>not</b> solid at any other
    /// moment — it holds people up through the dropdown path, not through
    /// <c>place_free</c> — so this is not a collider the world can own. It is
    /// handed to one call and forgotten.
    /// <para>
    /// A default instance has a null mask and is solid to nothing, which is what
    /// every caller outside the carry passes.
    /// </para>
    /// </remarks>
    public readonly record struct PlatformCollider
    {
        public SpriteMask? Mask { get; init; }

        public double X { get; init; }

        public double Y { get; init; }

        public int XScale { get; init; }

        public int YScale { get; init; }

        /// <summary>Whether a character's box at this position meets the platform.</summary>
        public bool Meets(in CharacterBox box, double characterX, double characterY) =>
            Mask is not null && PreciseCollision.Meets(Mask, X, Y, XScale, YScale, box, characterX, characterY);
    }

    /// <summary>
    /// A scale property as GM8 would use it, clamped to something a mask can be
    /// indexed by.
    /// </summary>
    /// <remarks>
    /// <b>Truncated to an integer, which is an assumption.</b> Every platform
    /// in the archive uses a whole-number <c>scale</c> (1, 2, 3 or 6), so nothing
    /// exercises a fractional one — but GM8's <c>image_xscale</c> is a real and
    /// would happily take 1.5. A fractional scale would need the mask sampled at
    /// sub-pixel positions rather than by integer division, so this rounds rather
    /// than pretending to support it. On the verification list.
    /// </remarks>
    public static int ScaleOf(double scale) => scale < 1 ? 1 : (int)scale;
}
