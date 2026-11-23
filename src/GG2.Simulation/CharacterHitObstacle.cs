using GG2.Protocol;

namespace GG2.Simulation;

/// <summary>
/// The movement solver: what happens when the naive move would end up inside
/// something.
/// </summary>
/// <remarks>
/// Transcribed from <c>Scripts/Physics/characterHitObstacle.gml</c>, which the
/// reference runs from Step rather than from a collision event. At a fixed
/// 30 Hz the <c>* global.delta_factor</c> / <c>/ global.delta_factor</c> pair
/// that brackets it is the identity and is gone.
/// <para>
/// This is the piece docs/STATUS.md calls the heart of phase 4, and the one whose
/// step-up and corner cases decide whether a wall-run feels right. It is
/// deliberately a close transcription: the order of the three checks, and the
/// fact that the horizontal one runs <em>after</em> the vertical one has
/// already zeroed <c>vleft</c>, are load-bearing.
/// </para>
/// </remarks>
public static class CharacterHitObstacle
{
    /// <summary>How far a character will step up or down over an obstacle.</summary>
    /// <remarks>
    /// Six world pixels — one mask cell. That is not a coincidence: it is what
    /// makes a one-cell lip walkable and a two-cell one a wall.
    /// </remarks>
    public const int StepHeight = 6;

    /// <summary>
    /// The reference bails out after ten iterations and calls itself stuck.
    /// </summary>
    private const int MaxIterations = 10;

    /// <summary>
    /// Resolves one tick's movement, mutating <paramref name="mover"/>.
    /// </summary>
    public static void Resolve(
        CollisionWorld world,
        in CharacterBox box,
        in CollisionFilter filter,
        ref MoverState mover)
    {
        var oldX = mover.X;
        var oldY = mover.Y;

        var boxHeight = box.Bottom - box.Top;
        var boxWidth = box.Right - box.Left;

        if (!world.PlaceFree(box, mover.X, mover.Y, filter))
            SlideOutOfWall(world, box, filter, ref mover, oldX, oldY, boxWidth, boxHeight);

        var hLeft = mover.HSpeed;
        var vLeft = mover.VSpeed;

        var iterations = 0;
        var stuck = false;

        // "while we still have distance to travel". The 0.1 is the reference's:
        // below a tenth of a pixel there is nothing worth resolving.
        // Every comparison of two reals below goes through Gml, because GML's
        // are fuzzy to 1e-13 and C#'s are not. This loop is where that stopped
        // being academic: a crawl that uses up its whole allowance leaves hLeft
        // at about -2.7e-14 from ordinary cancellation, and a literal `!= 0`
        // then takes the wall branch the reference skips.
        while ((Gml.Greater(Math.Abs(hLeft), 0.1) || Gml.Greater(Math.Abs(vLeft), 0.1)) && !stuck)
        {
            iterations++;
            if (iterations > MaxIterations)
                stuck = true;

            var rectified = false;

            var prevX = mover.X;
            var prevY = mover.Y;

            var targetX = mover.X + hLeft;
            var targetY = mover.Y + vLeft;

            Movement.GoodMoveContactSolid(
                world, box, filter, ref mover,
                PointDirection(mover.X, mover.Y, targetX, targetY),
                PointDistance(mover.X, mover.Y, targetX, targetY),
                out _);

            hLeft -= mover.X - prevX;
            vLeft -= mover.Y - prevY;

            // A ceiling or a floor.
            if (Gml.NotEqual(vLeft, 0) && !world.PlaceFree(box, mover.X, mover.Y + Gml.Sign(vLeft), filter))
            {
                // Floors reset moveStatus; ceilings do not. Being knocked into a
                // ceiling should not end a rocket jump.
                if (Gml.Greater(vLeft, 0))
                    mover.MoveStatus = 0;

                vLeft = 0;
                mover.VSpeed = 0;
                rectified = true;
            }

            // A wall to the left or right. Note this runs after the vertical
            // check, so vLeft may already be zero.
            if (Gml.NotEqual(hLeft, 0) && !world.PlaceFree(box, mover.X + Gml.Sign(hLeft), mover.Y, filter))
            {
                if (world.PlaceFree(box, mover.X + Gml.Sign(hLeft), mover.Y - StepHeight, filter))
                {
                    // Walk up the step.
                    mover.Y -= StepHeight;
                    rectified = true;
                    mover.MoveStatus = 0;
                }
                else if (world.PlaceFree(box, mover.X + Gml.Sign(hLeft), mover.Y + StepHeight, filter)
                         && Gml.GreaterOrEqual(Math.Abs(mover.HSpeed), Math.Abs(mover.VSpeed)))
                {
                    // Sloping ceiling. Only taken when moving more sideways than
                    // vertically, or a fall would duck under every overhang.
                    mover.Y += StepHeight;
                    rectified = true;
                    mover.MoveStatus = 0;
                }
                else
                {
                    hLeft = 0;
                    mover.HSpeed = 0;
                    rectified = true;
                }
            }

            // Nothing explained the blockage and there is still real distance
            // left: the common case is falling sideways onto a corner, where
            // neither the floor test nor the wall test fires because the
            // obstruction is diagonal. Give up the vertical component.
            if (!rectified && (Gml.GreaterOrEqual(Math.Abs(hLeft), 1) || Gml.GreaterOrEqual(Math.Abs(vLeft), 1)))
            {
                mover.VSpeed = 0;
                vLeft = 0;
            }
        }
    }

    /// <summary>
    /// Already inside something: try all four ways out and take the shortest.
    /// </summary>
    /// <remarks>
    /// The allowance is half the box, so a character cannot teleport through a
    /// wall thicker than itself. If none of the four works the position is put
    /// back — <c>move_outside_solid</c> leaves the instance where it was when it
    /// fails, which reports as distance zero and would otherwise win the
    /// comparison.
    /// </remarks>
    private static void SlideOutOfWall(
        CollisionWorld world,
        in CharacterBox box,
        in CollisionFilter filter,
        ref MoverState mover,
        double oldX,
        double oldY,
        int boxWidth,
        int boxHeight)
    {
        var probe = mover;

        Movement.MoveOutsideSolid(world, box, filter, ref probe, 90, boxHeight / 2.0);
        var distanceUp = oldY - probe.Y;
        var upY = probe.Y;
        probe.Y = oldY;

        Movement.MoveOutsideSolid(world, box, filter, ref probe, 270, boxHeight / 2.0);
        var distanceDown = probe.Y - oldY;
        var downY = probe.Y;
        probe.Y = oldY;

        Movement.MoveOutsideSolid(world, box, filter, ref probe, 0, boxWidth / 2.0);
        var distanceRight = probe.X - oldX;
        var rightX = probe.X;
        probe.X = oldX;

        Movement.MoveOutsideSolid(world, box, filter, ref probe, 180, boxWidth / 2.0);
        var distanceLeft = oldX - probe.X;
        var leftX = probe.X;
        probe.X = oldX;

        if (Gml.Less(distanceUp, distanceDown) && Gml.Less(distanceUp, distanceRight) && Gml.Less(distanceUp, distanceLeft))
            mover.Y = upY;
        else if (Gml.Less(distanceDown, distanceRight) && Gml.Less(distanceDown, distanceLeft))
            mover.Y = downY;
        else if (Gml.Less(distanceRight, distanceLeft))
            mover.X = rightX;
        else
            mover.X = leftX;

        if (!world.PlaceFree(box, mover.X, mover.Y, filter))
        {
            mover.X = oldX;
            mover.Y = oldY;
        }
    }

    /// <summary>
    /// GM8's <c>point_direction</c>: degrees in <c>[0, 360)</c>, counter-clockwise,
    /// with y pointing down.
    /// </summary>
    /// <remarks>
    /// Note the argument order — <c>atan2(y1 - y2, x2 - x1)</c>, not the usual
    /// <c>atan2(dy, dx)</c>. Getting the sign wrong here mirrors the whole game
    /// (docs/SIMULATION.md, "Float rules").
    /// </remarks>
    public static double PointDirection(double x1, double y1, double x2, double y2)
    {
        var degrees = Math.Atan2(y1 - y2, x2 - x1) * 180.0 / Math.PI;
        return degrees < 0 ? degrees + 360.0 : degrees;
    }

    public static double PointDistance(double x1, double y1, double x2, double y2) =>
        Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));
}
