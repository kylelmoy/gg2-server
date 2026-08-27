using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Bots;

/// <summary>
/// <c>botArcClear</c>: has the shot this bot is about to fire a clear path to what
/// it is aimed at — sampled along the <b>parabola</b>, not the straight line.
/// </summary>
/// <remarks>
/// ⚠️ <b>Nothing else in the bot layer ever checks a projectile's arc.</b> Target
/// selection, the keep-target test and the spot search are all a single straight
/// chest-to-chest sightline, while the aim solver deliberately aims <em>above</em>
/// the target to compensate for drop — and the amount is large:
/// <code>
///   Minegun (demoman)              spd 12,      grav 0.2    ~178px of sag at 500px
///   Scattergun/Shotgun/Minigun     spd 12.5-13, grav 0.15   ~120px at 500, ~64px at 375
///   Rocket                         spd 12,      grav 0      none
/// </code>
/// So a demoman with a clear straight line to a target 500px away is aiming a whole
/// storey above it. Indoors that is the reported "sometimes shooting at the
/// ceiling", stated as a mechanism rather than as a symptom — and it is one test
/// that serves the demoman, the soldier's wasted low-ceiling rocket, and every
/// arcing weapon in the game.
/// <para>
/// ⚠️ <b>Withholding rather than re-solving is deliberate.</b> A flatter solution
/// generally means closing the distance, which is a positioning decision, and
/// <see cref="BotGoalSpot"/> owns those.
/// </para>
/// </remarks>
public static class BotArc
{
    /// <summary>
    /// Whether a shot at <paramref name="aimDegrees"/> reaches
    /// (<paramref name="targetX"/>, <paramref name="targetY"/>) unobstructed.
    /// </summary>
    /// <param name="speed">
    /// ⚠️ Must be positive. A hitscan weapon has no arc to sample and its caller
    /// should not be here; zero answers <c>true</c> rather than dividing.
    /// </param>
    public static bool Clear(
        CollisionWorld collision, in CharacterState self,
        double aimDegrees, double speed, double gravity,
        double targetX, double targetY)
    {
        ArgumentNullException.ThrowIfNull(collision);

        if (speed <= 0)
            return true;

        var sx = self.X;
        var sy = self.Y;

        // ⚠️ Gml.LengthDir rather than cos/sin: GM8's y axis points down and these
        // already account for it, and the engine snaps their results where a bare
        // trig call does not (docs/TRAPS-SIMULATION.md, "lengthdir_x and lengthdir_y snap too").
        var vx = Gml.LengthDirX(speed, aimDegrees);
        var vy = Gml.LengthDirY(speed, aimDegrees);

        // ⚠️ When the shot is very nearly vertical the horizontal offset says
        // nothing about the flight time, so fall back to the straight-line estimate.
        // Clamped the same way the lead solver clamps its own t, so an unreachable
        // aim point cannot make this sample a thousand ticks of parabola.
        var flight = Math.Abs(vx) > 0.01
            ? (targetX - sx) / vx
            : Gml.PointDistance(sx, sy, targetX, targetY) / speed;

        // ⚠️ The LOWER bound is reachable and gated — a target on the shooter's own
        // position gives t ≈ 0 and collapses every sample onto the muzzle. The UPPER
        // bound is a guard against an unreachable aim point walking a thousand ticks
        // of parabola, and is deliberately NOT gated: it is a cost bound like
        // BotSuppress's hop budget, and every real-map fixture that reaches it is
        // blocked with or without it. Checked by mutation.
        flight = Math.Clamp(flight, 1, BotConstants.AimMaxTicks);

        var bx = sx;
        var by = sy;
        var filter = CollisionFilter.ForBullet();

        for (var i = 1; i <= BotConstants.ArcSamples; i++)
        {
            double ax;
            double ay;

            if (i == BotConstants.ArcSamples)
            {
                // ⚠️ The last sample is the TARGET itself, not the parabola's own
                // endpoint. The flight time is recovered from the horizontal offset,
                // which carries a tick or so of error, and letting that error put the
                // final sample a few pixels INTO the floor the target is standing on
                // would refuse every shot at a grounded enemy. Ending on the aim
                // point is also exactly what the straight sight test does, so a flat
                // weapon gets the same answer it always did.
                ax = targetX;
                ay = targetY;
            }
            else
            {
                var t = flight * i / BotConstants.ArcSamples;

                // ⚠️ `t * (t + 1) / 2` is EXACT rather than a continuous
                // approximation. Gravity is added to vspeed BEFORE the move is
                // applied, so after t ticks the accumulated sag is the triangular
                // number grav*(1 + 2 + ... + t) — the same correction BotAim.Solve
                // makes, and the same one that gets "simplified" by somebody reading
                // it as calculus.
                ax = sx + (vx * t);
                ay = sy + (vy * t) + (gravity * t * (t + 1) / 2);
            }

            if (collision.CollisionLineBlocksBullets(bx, by, ax, ay, filter))
                return false;

            bx = ax;
            by = ay;
        }

        return true;
    }
}
