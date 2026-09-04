using Gg2.Protocol;

namespace Gg2.Simulation;

/// <summary>Where a character is, for the movement primitives.</summary>
public struct MoverState
{
    public double X;

    public double Y;

    public double HSpeed;

    public double VSpeed;

    /// <summary>
    /// The reference's <c>moveStatus</c>: 0 normal, 1 self rocket jump, 2 enemy
    /// rocket jump, 3 airblast, 4 team rocket jump. Landing on a floor resets it.
    /// </summary>
    public int MoveStatus;
}

/// <summary>
/// The movement primitives, transcribed from <c>Scripts/Physics/</c>.
/// </summary>
/// <remarks>
/// These are the parts of the physics that are already a specification rather
/// than something to reverse-engineer: <c>good_move_contact_solid.gml</c> is
/// itself a pure-GML reimplementation of a buggy GM8 built-in, so it is read and
/// copied rather than inferred. <c>move_outside_solid</c> is a real built-in and
/// <em>was</em> measured — see <see cref="MoveOutsideSolid"/>.
/// <para>
/// At a fixed 30 Hz <c>global.delta_factor</c> is 1, so every
/// <c>* global.delta_factor</c> and <c>/ global.delta_factor</c> in the
/// reference is gone (docs/SIMULATION.md, "Fix the tick rate at 30 Hz").
/// </para>
/// </remarks>
public static class Movement
{
    /// <summary>Subpixel precision of the crawl: eighths of a pixel.</summary>
    private const int MaxI = 8;

    /// <summary>What <see cref="GoodMoveContactSolid"/> ran into, if anything.</summary>
    public readonly record struct Contact(bool Happened, double X, double Y);

    /// <summary>
    /// Slides along <paramref name="directionDegrees"/> until something solid is
    /// in the way or <paramref name="maxDistance"/> is used up. Returns how far
    /// it actually moved.
    /// </summary>
    /// <remarks>
    /// Transcribed from <c>Scripts/Physics/good_move_contact_solid.gml</c>,
    /// which exists because GM8's own <c>move_contact_solid</c> is buggy. It
    /// crawls forward a pixel at a time and, on hitting something, backs off in
    /// eighths.
    /// <para>
    /// <b>The <c>i / MAX_I</c> factor is applied twice</b>, once building the
    /// step and again using it, so a back-off step is <c>(i/8)²</c> of a pixel
    /// rather than <c>i/8</c>. That reads like a bug and is reproduced
    /// deliberately: it is the arithmetic the stock client performs, and
    /// "fixing" it would put every wall-slide a fraction out.
    /// </para>
    /// <para>
    /// Angles are GM8's: degrees, counter-clockwise, y pointing down — hence
    /// the negated sine.
    /// </para>
    /// </remarks>
    public static double GoodMoveContactSolid(
        CollisionWorld world,
        in CharacterBox box,
        in CollisionFilter filter,
        ref MoverState mover,
        double directionDegrees,
        double maxDistance,
        out Contact contact)
        => GoodMoveContactSolid(
            world, box, filter, ref mover, directionDegrees, maxDistance, out contact, default);

    /// <summary>
    /// <c>good_move_contact_solid</c> with one moving platform temporarily solid.
    /// </summary>
    /// <remarks>
    /// The reference wraps exactly one call in <c>other.solid = true</c> /
    /// <c>other.solid = false</c> — the branch where a platform drives INTO a
    /// character and has to back them out along its own motion. Nothing else in
    /// the game makes a platform solid, so the collider is a parameter rather
    /// than world state.
    /// </remarks>
    public static double GoodMoveContactSolid(
        CollisionWorld world,
        in CharacterBox box,
        in CollisionFilter filter,
        ref MoverState mover,
        double directionDegrees,
        double maxDistance,
        out Contact contact,
        in PreciseCollision.PlatformCollider platform)
    {
        contact = default;

        // GML's comparisons are fuzzy — see Gml.Equal. A maxDistance inside the
        // epsilon counts as zero here, which is how a crawl with nothing left to
        // do returns without moving.
        if (Gml.LessOrEqual(maxDistance, 0))
            return 0;

        var radians = directionDegrees * Math.PI / 180.0;
        var hvec = Math.Cos(radians) * maxDistance;
        var vvec = -Math.Sin(radians) * maxDistance;

        // Scales the vector so its dominant axis is one pixel per full step.
        var sfac = Math.Max(Math.Abs(hvec), Math.Abs(vvec));
        if (Gml.Equal(sfac, 0))
            return 0;

        var totalMoved = 0.0;
        var i = MaxI;

        while (Gml.Less(totalMoved, maxDistance) && i > 0)
        {
            var remaining = Math.Min(1, maxDistance - totalMoved);

            var moveX = hvec / sfac * i / MaxI * remaining;
            var moveY = vvec / sfac * i / MaxI * remaining;

            // ...and i / MaxI again. See the remarks.
            var newX = mover.X + moveX * i / MaxI;
            var newY = mover.Y + moveY * i / MaxI;

            if (world.PlaceFree(box, newX, newY, filter, platform))
            {
                // The loop's fixed point; this is the one line here that is NOT
                // in `Scripts/Physics/good_move_contact_solid.gml`.
                //
                // A step this small changes nothing: `totalMoved` gains zero,
                // `i` does not fall, and every later iteration recomputes these
                // same numbers, so the loop never ends. It happens because two
                // tolerances disagree — GML's fuzzy comparison calls the loop
                // unfinished while `maxDistance - totalMoved` is above 1e-13
                // (Gml.Epsilon), but a coordinate in the thousands has a ULP of
                // about 4.5e-13, so a remaining step under half of that rounds
                // straight back to where it started: at
                // (2698.437089230609, 3933.463891267056) with 2.14e-13 left to
                // travel, both axes came back unchanged.
                //
                // Nothing observable is given up by stopping. In this state the
                // reference does not produce a different answer, it produces no
                // further frames at all — GM8 spins in the same `while` — and
                // in every state where a step is representable this test is
                // false and the arithmetic below is untouched. With it, the
                // loop is provably finite: every pass either drops `i` — eight
                // times at most — or moves the position by at least one of its
                // ULPs, which is orders of magnitude more than a ULP of
                // `totalMoved` and so strictly shortens what is left.
                if (newX == mover.X && newY == mover.Y)
                    break;

                totalMoved += Math.Sqrt(
                    (newX - mover.X) * (newX - mover.X) + (newY - mover.Y) * (newY - mover.Y));

                mover.X = newX;
                mover.Y = newY;

                if (i < MaxI)
                    break; // finished crawling back out of whatever we hit
            }
            else
            {
                contact = new Contact(true, newX, newY);
                i -= 1;
            }
        }

        return totalMoved;
    }

    /// <summary>
    /// GM8's <c>move_outside_solid</c>: step along <paramref name="directionDegrees"/>
    /// until the position is free, giving up after <paramref name="maxDistance"/>.
    /// </summary>
    /// <remarks>
    /// <b>Measured, not assumed.</b> It steps a <em>whole pixel</em> at a time
    /// and <em>keeps the fractional part</em> of the starting position: from
    /// x = 860.25, against a wall clear at 870, it lands on 870.25 — not 870,
    /// and not 869.75. From 860.5 it lands on 869.5, because that is already
    /// free (869.5 rounds to the even 870). Checked against the running game
    /// from six starting offsets.
    /// <para>
    /// <b>When it fails it still moves</b> — the whole allowance, and stays
    /// stuck. That is not a detail: <c>characterHitObstacle</c> takes the
    /// <em>shortest</em> of four escapes, so a failure has to report a
    /// <em>large</em> distance or it would win the comparison and the character
    /// would never get out. Measured — from inside a wall needing seven pixels
    /// to clear, an allowance of three lands three pixels along, still stuck.
    /// </para>
    /// <para>
    /// The allowance is rounded to whole pixels, half to even, like everything
    /// else GM8 rounds: an allowance of 3.5 moves four pixels and one of 2.5
    /// moves two. That matters here because the reference's allowances are half
    /// a box — 16.5 pixels for a scout.
    /// </para>
    /// </remarks>
    public static void MoveOutsideSolid(
        CollisionWorld world,
        in CharacterBox box,
        in CollisionFilter filter,
        ref MoverState mover,
        double directionDegrees,
        double maxDistance)
    {
        if (world.PlaceFree(box, mover.X, mover.Y, filter))
            return;

        var radians = directionDegrees * Math.PI / 180.0;
        var stepX = Math.Cos(radians);
        var stepY = -Math.Sin(radians);

        var startX = mover.X;
        var startY = mover.Y;
        var steps = CollisionWorld.RoundPosition(maxDistance);

        for (var step = 1; step <= steps; step++)
        {
            var x = startX + stepX * step;
            var y = startY + stepY * step;

            if (!world.PlaceFree(box, x, y, filter))
                continue;

            mover.X = x;
            mover.Y = y;
            return;
        }

        // Nowhere free within the allowance: go the whole way anyway.
        mover.X = startX + stepX * steps;
        mover.Y = startY + stepY * steps;
    }
}
