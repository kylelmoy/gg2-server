using Gg2.Protocol;

namespace Gg2.Simulation;

/// <summary>
/// What <c>place_free</c> and <c>place_meeting</c> actually test for a
/// <b>rotated</b> projectile: its precise mask, not the bounding box GM8
/// reports for it.
/// </summary>
/// <remarks>
/// <b>The two primitives disagree about the same pair of instances, and the
/// split is measured</b> (2026-08-27, <c>tools/sample-rotated-mask.js</c>,
/// nine sprites by eighteen angles):
/// <list type="bullet">
/// <item><c>distance_to_object</c> — every splash radius and its falloff — is
/// computed from the <b>bounding boxes</b>, rotated or not, with the
/// character's <c>image_xscale</c> inflation included. <see cref="Aabb.Distance"/>
/// and <see cref="Aabb.ForRotatedSprite"/> are right and stay right.</item>
/// <item><c>place_free</c> and <c>place_meeting</c> go to the <b>precise
/// masks</b>. Unrotated the two coincide — every one of the nine projectile
/// sprites' masks fills its own bbox exactly — which is why six phases of this
/// port never noticed.</item>
/// </list>
/// <para>
/// ⚠️ <b>Rotated, the box is not a small over-estimate.</b> A <c>Shot</c> at 45°
/// has 25 opaque pixels inside a 169-pixel box, and the Scattergun spreads every
/// pellet by <c>random(15) - 7.5</c>, so diagonal pellets are the normal case.
/// Measured against the fixture, treating the box as solid — what this port did
/// until now — answers <b>91.5%</b> of 310,194 sampled pixels correctly.
/// </para>
/// <para>
/// <b>The model here is 99.861% on the same 310,194</b>, which is a 61-fold
/// reduction in wrong pixels rather than an exact answer, and that is stated
/// plainly because this project's bar for observable simulation is exactness.
/// <see cref="Gg2.Simulation"/> has no better option available: closing the last
/// 431 needs GM8's own rasteriser, and the search that got here is recorded in
/// docs/TRAPS-SIMULATION.md so the next attempt does not repeat it.
/// </para>
/// <para>
/// <b>What the residual is.</b> 260 of the 431 are <b>floor ties</b> — a
/// transformed coordinate within <c>1e-9</c> of an integer, where the x87 gap in
/// docs/SIMULATION.md's "four motion variables" decides the floor the other way.
/// That is the same class already documented on <see cref="Aabb.ForRotatedSprite"/>
/// and it is not fixable in <c>double</c>. The other 171 are structural, sit on
/// the far edges of the source rectangle, and no offset of the sampling grid
/// removes them: a fine search over the whole unit square of offsets puts the
/// optimum at exactly zero, which is the plain <c>floor</c> written below.
/// </para>
/// <para>
/// ⚠️ <b>Every sample was taken at an INTEGER position</b>, so which coordinate
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
            // ⚠️ Only the Stab is ever mirrored, and no sample covers it: the
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
        }

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
