using Gg2.Protocol;

namespace Gg2.Simulation;

/// <summary>
/// What <c>place_free</c> and <c>place_meeting</c> actually test for a
/// <b>rotated</b> projectile: its precise mask, not the bounding box GM8
/// reports for it.
/// </summary>
/// <remarks>
/// <b>The two primitives disagree about the same pair of instances, and the
/// split is measured</b> (<c>tools/sample-rotated-mask.js</c>, nine sprites by
/// eighteen angles):
/// <list type="bullet">
/// <item><c>distance_to_object</c> — every splash radius and its falloff — is
/// computed from the <b>bounding boxes</b>, rotated or not, with the
/// character's <c>image_xscale</c> inflation included. <see cref="Aabb.Distance"/>
/// and <see cref="Aabb.ForRotatedSprite"/> are right and stay right.</item>
/// <item><c>place_free</c> and <c>place_meeting</c> test the <b>mask</b>, which
/// for a rotated sprite is the rectangle <em>turned</em> rather than its
/// axis-aligned bound. Unrotated the two coincide, which is why six phases of
/// this port never noticed.</item>
/// </list>
/// <para>
/// <b>Rotated, the box is not a small over-estimate.</b> A <c>Shot</c> at 45°
/// has 25 opaque pixels inside a 169-pixel box, and the Scattergun spreads every
/// pellet by <c>random(15) - 7.5</c>, so diagonal pellets are the normal case.
/// Measured against the fixture, treating the box as solid — what this port did
/// until now — answers <b>91.5%</b> of 310,194 sampled pixels correctly.
/// </para>
/// <para>
/// <b>There is no pixel art in any of this, and that is read from the source
/// rather than inferred.</b> All nine projectile sprites declare
/// <c>RECTANGLE</c> as their mask shape in <c>Sprites/</c>, with MANUAL, AUTO or
/// FULL bounds — so the mask is exactly the sprite's bbox rectangle and the test
/// below is an analytic rotated-rectangle test, not a rasterisation.
/// <c>floor(u)</c> in <c>[L,R]</c> is precisely <c>L &lt;= u &lt; R+1</c>, which
/// is the region a pixel rectangle covers.
/// </para>
/// <para>
/// <b>It is 99.9736% against the fixture — 82 wrong of 310,194 — and every one of
/// the 82 is a FLOOR TIE</b>: a transformed coordinate within <c>1e-9</c> of an
/// integer, where the x87 gap in docs/SIMULATION.md's "four motion variables"
/// decides the floor the other way. <b>Nothing structural remains.</b> That is
/// the same class <see cref="Aabb.ForRotatedSprite"/> documents, which is
/// explicit that it is not fixable in <c>double</c> and must not be fudged. The
/// box alone — what this port did until now — is right on 91.5%.
/// </para>
/// <para>
/// <b>Every sample was taken at an INTEGER position</b>, so which coordinate
/// the pixel grid hangs off is not measured. It is anchored on
/// <see cref="CollisionWorld.RoundPosition"/> here because that is what
/// <see cref="Aabb.ForRotatedSprite"/> does, and a mask that did not share the
/// box's anchor could report a pixel outside the box the engine also reports.
/// A probe at fractional positions would settle it.
/// </para>
/// </remarks>
public static class ProjectileMask
{
    /// <summary>
    /// The rotated source rectangle and the trig for one projectile, computed
    /// once so a scan does not redo it per pixel.
    /// </summary>
    private readonly struct Transform
    {
        public Transform(in ProjectileState projectile)
        {
            var sprite = ProjectileSprites.For(projectile.Kind);

            // Scaled like ForRotatedSprite's corners, so a mirrored sprite's
            // source rectangle is the mirrored one.
            //
            // Only the Stab is ever mirrored, and no sample covers it: the
            // probe left image_xscale at 1. Ordered rather than assumed
            // positive so a negative scale cannot silently produce an empty
            // rectangle that answers "never touching anything".
            var left = (sprite.Bbox.Left - sprite.OriginX) * projectile.XScale;
            var right = (sprite.Bbox.Right - sprite.OriginX) * projectile.XScale;

            Left = Math.Min(left, right);
            Right = Math.Max(left, right);
            Top = sprite.Bbox.Top - sprite.OriginY;
            Bottom = sprite.Bbox.Bottom - sprite.OriginY;

            var radians = Gml.DegToRad(projectile.ImageAngle);
            Cos = Math.Cos(radians);
            Sin = Math.Sin(radians);

            // The pixel grid hangs off the same rounded position the box does.
            AnchorX = CollisionWorld.RoundPosition(projectile.X);
            AnchorY = CollisionWorld.RoundPosition(projectile.Y);

            // The reported box is part of the answer, not just a broad phase.
            // GM8 never reports a mask pixel outside the box it also reports
            // (RotatedMaskSampleTests.ARotatedBoxOverStatesItsMask asserts that
            // over every sample), and the two are rounded by *different* rules —
            // the box takes each rotated corner half-to-even, this takes a floor
            // of a rotated point. Where they disagree the box wins, because it is
            // the one that was measured directly. Leaving it out costs a factor
            // of five: 431 wrong sampled pixels instead of 82.
            Box = projectile.Box;
        }

        public Aabb Box { get; }

        public double Left { get; }

        public double Right { get; }

        public double Top { get; }

        public double Bottom { get; }

        public double Cos { get; }

        public double Sin { get; }

        public int AnchorX { get; }

        public int AnchorY { get; }

        /// <summary>
        /// Whether the mask is opaque at a world pixel.
        /// </summary>
        /// <remarks>
        /// The world offset is rotated back into sprite space and floored, then
        /// tested against the sprite's own bbox rectangle — which is the mask,
        /// because every projectile sprite's mask fills its bbox
        /// (<c>RotatedMaskSampleTests.EveryUnrotatedMaskExactlyFillsItsBoundingBox</c>).
        /// <para>
        /// Game Maker's y axis points down and its angles do not, so the
        /// forward rotation carries a negated sine in the y row
        /// (<see cref="Aabb.ForRotatedSprite"/>); inverting it puts the negated
        /// sine in the <em>x</em> row here. Getting that backwards rotates by
        /// +angle instead of -angle and is not subtle — it answers 34% rather
        /// than 99.9%.
        /// </para>
        /// </remarks>
        public bool Covers(int px, int py)
        {
            if (px < Box.Left || px > Box.Right || py < Box.Top || py > Box.Bottom)
                return false;

            double dx = px - AnchorX;
            double dy = py - AnchorY;

            var u = Math.Floor((dx * Cos) - (dy * Sin));
            var v = Math.Floor((dx * Sin) + (dy * Cos));

            return u >= Left && u <= Right && v >= Top && v <= Bottom;
        }
    }

    /// <summary>
    /// Whether the projectile's precise mask covers the world pixel.
    /// </summary>
    public static bool Covers(in ProjectileState projectile, int px, int py) =>
        new Transform(projectile).Covers(px, py);

    /// <summary>
    /// <c>place_meeting</c> between two projectiles, where <b>both</b> masks are
    /// rotated rectangles and a pixel has to be inside each.
    /// </summary>
    /// <remarks>
    /// The last overlap in the port that was still comparing two bounding boxes.
    /// Only Quote's bubbles ever meet another projectile
    /// (<see cref="Projectiles"/>'s <c>HitBubble</c> and
    /// <c>BubbleHitProjectiles</c>), and a pellet arriving at one is rotated to
    /// its heading — so the box answered yes for pairs that never touched.
    /// <para>
    /// <b>Both boxes are the broad phase, and their intersection bounds the
    /// scan.</b> Neither mask reaches outside its own box, so a pixel outside
    /// either box cannot be in both masks — which is what makes walking only the
    /// overlap correct rather than merely cheaper.
    /// </para>
    /// </remarks>
    public static bool Overlaps(
        in ProjectileState first,
        in Aabb firstBox,
        in ProjectileState second,
        in Aabb secondBox)
    {
        var left = Math.Max(firstBox.Left, secondBox.Left);
        var right = Math.Min(firstBox.Right, secondBox.Right);
        var top = Math.Max(firstBox.Top, secondBox.Top);
        var bottom = Math.Min(firstBox.Bottom, secondBox.Bottom);

        if (left > right || top > bottom)
            return false;

        // Both unrotated: each mask fills its box, so the boxes overlapping is
        // the whole answer and the scan would only confirm it.
        var firstFlat = Gml.Equal(first.ImageAngle % 360, 0);
        var secondFlat = Gml.Equal(second.ImageAngle % 360, 0);

        if (firstFlat && secondFlat)
            return true;

        var a = new Transform(first);
        var b = new Transform(second);

        for (var y = top; y <= bottom; y++)
        {
            for (var x = left; x <= right; x++)
            {
                if ((firstFlat || a.Covers(x, y)) && (secondFlat || b.Covers(x, y)))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// <c>place_free</c> against the MAP: whether the projectile's mask clears
    /// every solid, rather than whether its bounding box does.
    /// </summary>
    /// <remarks>
    /// <b>The map is a precise mask and the projectile is a rectangle</b>, so
    /// GM8 tests where the two actually overlap — the same asymmetry
    /// <see cref="Overlaps(in ProjectileState, in Aabb)"/> handles for a
    /// character. Using the box instead explodes a diagonal rocket a few pixels
    /// before it reaches the wall.
    /// <para>
    /// <b>The box is a sound broad phase in the cheap direction</b>, and this
    /// leans on it: the mask never reaches outside the box
    /// (<c>RotatedMaskSampleTests.ARotatedBoxOverStatesItsMask</c>), so a free
    /// box means a free mask and the scan never runs. It only runs when the box
    /// says "blocked", which is the rare tick.
    /// </para>
    /// <para>
    /// The narrow phase asks the world about one pixel at a time rather than
    /// reaching into the walkmask, because a single-pixel box is exactly the
    /// question being asked and <see cref="CollisionWorld"/> already answers it
    /// for both the pixel mask and the rectangle list. A projectile's box is a
    /// few dozen pixels at most.
    /// </para>
    /// </remarks>
    public static bool PlaceFree(
        CollisionWorld world,
        in ProjectileState projectile,
        in CollisionFilter filter)
    {
        ArgumentNullException.ThrowIfNull(world);

        var box = projectile.Box;

        if (world.PlaceFree(in box, in filter))
            return true;

        // Unrotated the mask fills the box, so the box's answer is the mask's.
        if (Gml.Equal(projectile.ImageAngle % 360, 0))
            return false;

        var transform = new Transform(projectile);

        for (var y = box.Top; y <= box.Bottom; y++)
        {
            for (var x = box.Left; x <= box.Right; x++)
            {
                if (!transform.Covers(x, y))
                    continue;

                var pixel = new Aabb(x, y, x, y);

                if (!world.PlaceFree(in pixel, in filter))
                    return false;
            }
        }

        return true;
    }

    /// <inheritdoc cref="PlaceFree"/>
    /// <summary>
    /// <c>place_free</c> against the map's <em>obstacles only</em> — what
    /// <c>wallSetSolid</c> leaves standing.
    /// </summary>
    public static bool PlaceFreeInMask(CollisionWorld world, in ProjectileState projectile)
    {
        ArgumentNullException.ThrowIfNull(world);

        var box = projectile.Box;

        if (world.PlaceFreeInMask(in box))
            return true;

        if (Gml.Equal(projectile.ImageAngle % 360, 0))
            return false;

        var transform = new Transform(projectile);

        for (var y = box.Top; y <= box.Bottom; y++)
        {
            for (var x = box.Left; x <= box.Right; x++)
            {
                if (!transform.Covers(x, y))
                    continue;

                var pixel = new Aabb(x, y, x, y);

                if (!world.PlaceFreeInMask(in pixel))
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// <c>place_meeting</c> against an axis-aligned box — a character's or a
    /// sentry's, both of which are rectangles.
    /// </summary>
    /// <remarks>
    /// The bounding box is the broad phase and the mask is the narrow one, in
    /// that order: GM8 intersects the two boxes first and only walks pixels
    /// inside the overlap, so a projectile whose box misses cannot touch
    /// anything however its mask is shaped. That makes the common answer — no
    /// overlap at all — as cheap as it was before this file existed.
    /// </remarks>
    public static bool Overlaps(in ProjectileState projectile, in Aabb other) =>
        Overlaps(in projectile, projectile.Box, in other);

    /// <inheritdoc cref="Overlaps(in ProjectileState, in Aabb)"/>
    /// <param name="projectileBox">
    /// The projectile's own box, when the caller already has it. Recomputing it
    /// is a rotation of four corners, and <see cref="Projectiles.Collide"/>
    /// hoists it out of a loop over every slot on purpose.
    /// </param>
    public static bool Overlaps(in ProjectileState projectile, in Aabb projectileBox, in Aabb other)
    {
        var box = projectileBox;

        var left = Math.Max(box.Left, other.Left);
        var right = Math.Min(box.Right, other.Right);
        var top = Math.Max(box.Top, other.Top);
        var bottom = Math.Min(box.Bottom, other.Bottom);

        if (left > right || top > bottom)
            return false;

        // An unrotated sprite's mask fills its box, so the boxes overlapping is
        // already the answer and the scan would only confirm it. Skipped rather
        // than run because this is the case every Shot, Needle and Flame that
        // happens to be flying flat takes, and because it keeps the pre-rotation
        // behaviour exactly where it was.
        if (Gml.Equal(projectile.ImageAngle % 360, 0))
            return true;

        var transform = new Transform(projectile);

        for (var y = top; y <= bottom; y++)
        {
            for (var x = left; x <= right; x++)
            {
                if (transform.Covers(x, y))
                    return true;
            }
        }

        return false;
    }
}
