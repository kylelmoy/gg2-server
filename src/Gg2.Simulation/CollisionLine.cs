namespace Gg2.Simulation;

/// <summary>
/// GM8's <c>collision_line</c>: does a segment reach an instance?
/// </summary>
/// <remarks>
/// The Rifle's whole hitscan is eleven calls to this
/// (<c>Rifle.events/User Event 3.xml</c>), and
/// <c>Scripts/Physics/collision_line_bulletblocking.gml</c> asks it five times
/// before any rocket is allowed to fly. Both pass <c>prec = true</c>, so the
/// precise path below is the only one GG2 ever takes.
/// <para>
/// <b>This is not a segment-versus-rectangle intersection and writing one
/// gives wrong answers.</b> GM8 clips the segment to the instance's bounding
/// box horizontally, <em>rounds the clipped endpoints to integers</em>, walks
/// whole pixels along the longer axis and looks each step up in the sprite's
/// collision mask. Rounding before walking is what makes it inexact: a line can
/// register against a box it never geometrically enters, by up to half a pixel,
/// and it does so asymmetrically — measured against the running game, a line
/// that only grazes the corner of a one-pixel box hits from one diagonal and
/// misses from the other.
/// </para>
/// <para>
/// Measured across 13,702 samples on <c>ctf_truefort</c> —
/// <c>tests/Gg2.Tests/Fixtures/collision-line-samples.json</c>, regenerate with
/// <c>node tools/sample-collision-line.js</c> — covering seven probe boxes at
/// both coordinate parities, positive, fractional and <b>negative</b> scales,
/// an off-centre sprite origin, four classes of live character at both facings,
/// and the map's own precise 6× mask. Exact on all of them, with no
/// tolerance.
/// </para>
/// <para>
/// The structure follows GM8's, which the rest of this repo would normally
/// refuse to do (CLAUDE.md: "transcribe GML arithmetic, not GML structure").
/// It is transcribed here because the walk <em>is</em> the arithmetic: the
/// order of clip, round and step decides the answer, and there is no closed
/// form that reproduces it.
/// </para>
/// </remarks>
public static class CollisionLine
{
    /// <summary>The per-pixel lookup into an instance's collision mask.</summary>
    /// <remarks>
    /// A struct type parameter rather than an interface reference, so the walk
    /// stays allocation-free and the lookup inlines — this runs a few hundred
    /// times per shot.
    /// </remarks>
    public interface IMask
    {
        /// <summary>Whether the mask covers a pixel, in sprite coordinates.</summary>
        bool IsSolid(int x, int y);

        /// <summary>
        /// How many pixels the walk may advance from an <em>open</em> sample at
        /// (<paramref name="x"/>, <paramref name="y"/>) without being able to
        /// step over anything solid. Zero means "no idea, walk pixel by pixel".
        /// </summary>
        /// <remarks>
        /// ⛔ <b>It makes the walk SKIP, never guess, and the answer is identical
        /// rather than approximate.</b> <see cref="Walk{TMask}"/> advances at most
        /// one pixel on each axis per step — the major axis is chosen so the
        /// slope's magnitude is at most one — so <c>k</c> steps move the sample
        /// point by a Chebyshev distance of exactly <c>k</c>. A mask that reports
        /// <c>n</c> here is promising that every pixel within <c>n</c> of this one
        /// is open, so the samples the walk jumps over cannot be the first solid
        /// one.
        /// <para>
        /// ⚠️ <b>Zero is the honest answer wherever the promise cannot be made</b>
        /// — off the mask, or for a mask with no clearance field. Returning
        /// anything else is a bullet through a wall, and no test of the walk's
        /// ordinary cases would notice.
        /// </para>
        /// </remarks>
        int SafeSkipPixels(int x, int y);
    }

    /// <summary>
    /// The mask of a sprite whose bounding box is filled — every GG2 collider
    /// except the map.
    /// </summary>
    /// <remarks>
    /// Characters, gates, obstacles and bullet walls all use rectangle-shaped
    /// masks (<c>&lt;shape&gt;RECTANGLE&lt;/shape&gt;</c> in the sprite XML), so
    /// the bounds test in <see cref="Hits{TMask}"/> is the whole answer and this
    /// adds nothing to it.
    /// </remarks>
    public readonly struct FilledMask : IMask
    {
        public bool IsSolid(int x, int y) => true;

        /// <summary>
        /// Never reached: a filled mask is solid at every in-bounds pixel, so the
        /// walk returns on its first in-bounds sample and never asks.
        /// </summary>
        public int SafeSkipPixels(int x, int y) => 0;
    }

    /// <summary>
    /// Whether the segment from (<paramref name="x1"/>, <paramref name="y1"/>)
    /// to (<paramref name="x2"/>, <paramref name="y2"/>) collides with
    /// <paramref name="collider"/>.
    /// </summary>
    public static bool Hits<TMask>(in LineCollider collider, TMask mask, double x1, double y1, double x2, double y2)
        where TMask : struct, IMask
    {
        if (!MightMeet(collider.Bbox, x1, y1, x2, y2))
            return false;

        // The instance's span, on the half-open convention MightMeet explains.
        double left = collider.Bbox.Left;
        double top = collider.Bbox.Top;
        double right = collider.Bbox.Right + 1.0;
        double bottom = collider.Bbox.Bottom + 1.0;

        // Order the endpoints by x, then trim the segment to the instance's
        // horizontal span. Only horizontally: GM8 does not clip vertically, and
        // that is why a near-vertical line against a character still walks the
        // whole thing.
        if (x2 < x1)
        {
            (x1, x2) = (x2, x1);
            (y1, y2) = (y2, y1);
        }

        // Neither division can be by zero. A segment with x1 == x2 that needed
        // clipping would have failed the horizontal rejection above, and after
        // the first clip x1 is bbox_left while x2 is past bbox_right + 1.
        if (x1 < left)
        {
            y1 = ((y2 - y1) * (left - x1) / (x2 - x1)) + y1;
            x1 = left;
        }

        if (x2 > right)
        {
            y2 = ((y2 - y1) * (right - x2) / (x2 - x1)) + y2;
            x2 = right;
        }

        // Both endpoints of the clipped segment above the box, or both below.
        // Not a full vertical clip — a segment that straddles the span survives
        // here and is settled by the walk.
        if ((top > y1 && top > y2) || (y1 >= bottom && y2 >= bottom))
            return false;

        return Walk(collider, mask, x1, y1, x2, y2);
    }

    /// <summary>
    /// Whether a segment's bounding box overlaps an instance's at all — the broad
    /// phase, and the first thing <see cref="Hits{TMask}"/> asks.
    /// </summary>
    /// <remarks>
    /// The instance covers <c>[bbox_left, bbox_right + 1)</c> horizontally and
    /// <c>[bbox_top, bbox_bottom + 1)</c> vertically: GM8's <c>bbox_right</c> is
    /// the last covered pixel, and a pixel is a unit square. Inclusive at the low
    /// end, exclusive at the high end, and that asymmetry is load-bearing — see
    /// the measurements in docs/SIMULATION.md.
    /// <para>
    /// ⛔ <b>Public because a caller with many colliders wants it BEFORE building
    /// them, and it has to be the same body.</b> A copy of this interval with
    /// <c>bbox_right</c> in place of <c>bbox_right + 1</c> was mutated into
    /// <c>CollisionWorld</c> on 2026-09-02 and <b>all 2,643 tests stayed
    /// green</b> — a collision quietly refused at the edge of a gate is invisible
    /// to this suite. Shared, the same mutation reddens four, because this body
    /// is under <c>CollisionLineTests</c>' 9,014-segment oracle.
    /// </para>
    /// </remarks>
    public static bool MightMeet(Aabb bbox, double x1, double y1, double x2, double y2)
    {
        var right = bbox.Right + 1.0;
        var bottom = bbox.Bottom + 1.0;

        var segLeft = Math.Min(x1, x2);
        var segRight = Math.Max(x1, x2);
        var segTop = Math.Min(y1, y2);
        var segBottom = Math.Max(y1, y2);

        return right > segLeft && segRight >= bbox.Left && bottom > segTop && segBottom >= bbox.Top;
    }

    private static bool Walk<TMask>(in LineCollider collider, TMask mask, double x1, double y1, double x2, double y2)
        where TMask : struct, IMask
    {
        // Round everything, as GM8 does. Half to even — the same rule as GML's
        // round() and as place_free's position rounding, and *not* the wire's
        // half-away-from-zero (docs/TRAPS-SIMULATION.md, "Six roundings").
        var instX = CollisionWorld.RoundPosition(collider.X);
        var instY = CollisionWorld.RoundPosition(collider.Y);

        var ix1 = CollisionWorld.RoundPosition(x1);
        var iy1 = CollisionWorld.RoundPosition(y1);
        var ix2 = CollisionWorld.RoundPosition(x2);
        var iy2 = CollisionWorld.RoundPosition(y2);

        // Step along whichever axis the rounded segment is longer on, so the
        // walk never skips a pixel column or row. Ties go to x: the comparison
        // is strict.
        var vertical = Math.Abs(ix2 - ix1) < Math.Abs(iy2 - iy1);

        if (vertical && iy2 < iy1)
        {
            (ix1, ix2) = (ix2, ix1);
            (iy1, iy2) = (iy2, iy1);
        }

        var count = (vertical ? iy2 - iy1 : ix2 - ix1) + 1;

        // A single step means the two rounded endpoints landed on the same
        // pixel; the slope below would be 0/0.
        var slope = count == 1
            ? 0.0
            : vertical
                ? (double)(ix2 - ix1) / (iy2 - iy1)
                : (double)(iy2 - iy1) / (ix2 - ix1);

        for (var i = 0; i < count; i++)
        {
            double px, py;

            if (vertical)
            {
                px = ix1 + (i * slope);
                py = iy1 + i;
            }
            else
            {
                px = ix1 + i;
                py = iy1 + (i * slope);
            }

            // World pixel to mask pixel. The division by the image scale is
            // what makes a left-facing character (image_xscale = -1) accept a
            // different half-open interval from a right-facing one, so this
            // cannot be simplified into a comparison against the bounding box.
            var mx = collider.OriginX + (int)Math.Floor((px - instX) / collider.XScale);
            var my = collider.OriginY + (int)Math.Floor((py - instY) / collider.YScale);

            if (mx < collider.Mask.Left || my < collider.Mask.Top ||
                mx > collider.Mask.Right || my > collider.Mask.Bottom)
            {
                // Off the mask. No hit, and no promise to skip on — see
                // IMask.SafeSkipPixels.
                continue;
            }

            if (mask.IsSolid(mx, my))
                return true;

            // ⚠️ Open ground is skipped rather than walked, and the result is
            // IDENTICAL rather than approximate — IMask.SafeSkipPixels carries
            // the argument. This is the difference between a soldier's arc test
            // costing a few hundred mask probes and costing a few dozen: the
            // walk is one sample per pixel of the segment, and a rocket's flight
            // is hundreds of pixels of mostly empty air.
            var skip = mask.SafeSkipPixels(mx, my);

            if (skip > 1)
                i += skip - 1;
        }

        return false;
    }
}

/// <summary>
/// Everything <see cref="CollisionLine"/> needs about one instance.
/// </summary>
/// <param name="X">The instance's unrounded position; GM8 rounds it itself.</param>
/// <param name="XScale">
/// <c>image_xscale</c>. May be negative — that is a character facing left — and
/// may be fractional.
/// </param>
/// <param name="Bbox">
/// The bounding box GM8 reports for the instance, inclusive on all four sides.
/// </param>
/// <param name="OriginX">
/// The mask sprite's origin. It cancels out of the bounds test, because
/// <paramref name="Mask"/> is in the same coordinates — it is carried only so
/// the index handed to a real mask lookup is the sprite's own.
/// </param>
/// <param name="Mask">The mask sprite's bounding box, in sprite pixels.</param>
public readonly record struct LineCollider(
    double X,
    double Y,
    double XScale,
    double YScale,
    Aabb Bbox,
    int OriginX,
    int OriginY,
    Aabb Mask)
{
    /// <summary>
    /// The collider a character presents to a hitscan.
    /// </summary>
    /// <remarks>
    /// <b><paramref name="facing"/> changes two separate things here, and only
    /// one of them is the one people remember.</b> It widens
    /// <c>bbox_left</c> by two pixels, which
    /// <see cref="Aabb.ForCharacter"/> applies — and it is also the
    /// <c>image_xscale</c> the mask lookup <em>divides</em> by, which flips
    /// which end of the acceptance interval is open. A right-facing character
    /// is reached over <c>[left, right + 1)</c> and a left-facing one over
    /// <c>(left - 1, right]</c>.
    /// <para>
    /// The sprite origin is dropped rather than carried: the mask bounds are
    /// given relative to it, so it cancels out of the only comparison it takes
    /// part in. That is what lets this be built from
    /// <see cref="CharacterBox"/> — whose numbers are already
    /// <c>sprite_get_bbox_* - sprite_get_*offset</c>
    /// (<c>Character.events/Create.xml:5-8</c>) — instead of needing the sprite
    /// metadata the repo does not keep.
    /// </para>
    /// <para>
    /// <c>sprite_index</c> is set once in a class's Create event and never
    /// reassigned — <c>Character.events/Draw.xml</c> picks the animation frame
    /// into a local and draws that — so a character's bounding box does not
    /// change with what it is doing. Checked live across four classes.
    /// </para>
    /// </remarks>
    /// <summary>
    /// The collider a map's own solid rectangle presents to a hitscan: a gate,
    /// a player wall, a bullet wall.
    /// </summary>
    /// <remarks>
    /// Simpler than <see cref="ForCharacter"/> in the one way that matters —
    /// the sprite's origin is <c>(0, 0)</c> and its mask fills it, so the mask
    /// bounds are just the sprite's extent and the acceptance interval is
    /// always the ordinary <c>[left, right + 1)</c>. A map entity's scale is
    /// never negative, so the flipped interval a left-facing character gets
    /// cannot arise here.
    /// <para>
    /// The scale is <em>not</em> folded into the bounds. It has to stay as a
    /// divisor: at a fractional scale — 0.3 and 0.8 are both on shipped maps —
    /// several world pixels map to one mask pixel, and collapsing that into a
    /// box comparison rounds where GM8 floors.
    /// </para>
    /// <para>
    /// Gated by the same measured rows as the algorithm itself. The rectangle
    /// probes in <c>collision-line-samples.json</c> are literal
    /// <c>BulletWall</c>s wearing the 6x60 runtime sprite every map entity
    /// wears, so
    /// <c>CollisionLineSampleTests.SolidRectanglesPresentTheColliderTheEngineMeasured</c>
    /// rebuilds each probe through <see cref="SolidRect.FromEntity"/> and runs
    /// the engine's own answers back through this.
    /// </para>
    /// </remarks>
    public static LineCollider ForSolidRect(in SolidRect rect) =>
        new(
            rect.X,
            rect.Y,
            rect.XScale,
            rect.YScale,
            new Aabb(rect.Left, rect.Top, rect.Right, rect.Bottom),
            0,
            0,
            new Aabb(0, 0, rect.SpriteWidth - 1, rect.SpriteHeight - 1));

    public static LineCollider ForCharacter(in CharacterBox box, double x, double y, int facing = 1) =>
        new(
            x,
            y,
            facing < 0 ? -1.0 : 1.0,
            1.0,
            Aabb.ForCharacter(box, x, y, facing),
            0,
            0,
            new Aabb(box.Left, box.Top, box.Right, box.Bottom));
}
