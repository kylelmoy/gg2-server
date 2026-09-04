using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Bots;

/// <summary>How much the aim solver is allowed to predict.</summary>
/// <remarks>
/// These are a <em>difficulty</em> knob, not an optimisation. A bot that
/// always solves the full ballistic intercept is a bot that never misses a
/// moving target, which is neither fun nor human.
/// </remarks>
public enum BotLeadMode
{
    /// <summary>Aim where the target is, and let gravity be the only correction.</summary>
    None = BotConstants.LeadNone,

    /// <summary>One pass: guess the flight time from the current distance, then aim there.</summary>
    Linear = BotConstants.LeadLinear,

    /// <summary>The full iterated intercept, with the target's fall predicted and clamped to its floor.</summary>
    Full = BotConstants.LeadFull,
}

/// <summary>
/// Where to point — <c>botAimSolve.gml</c>, <c>botAimLead.gml</c>,
/// <c>botWeaponBallistics.gml</c> and <c>botFallPredict.gml</c>.
/// </summary>
/// <remarks>
/// <b>An exact ballistic intercept solver, against an opponent model of
/// "constant velocity plus gravity".</b> docs/BOTAI.md opens by saying that is
/// the wrong model against a human who reacts to the shot being fired — it leads
/// the mean of a distribution nothing in the codebase represents — and that
/// GG2's slow projectiles make opponent <em>prediction</em>, not marksmanship,
/// the skill the bots are missing. It is ported anyway, as the baseline the
/// document asks for: something known-good to measure an improvement against.
/// </remarks>
public static class BotAim
{
    /// <summary>
    /// The projectile speed, gravity and drift of the weapon a class carries —
    /// <c>botWeaponBallistics.gml</c>.
    /// </summary>
    /// <remarks>
    /// <b>These are the bot's model of the weapon, not the weapon.</b> They are
    /// the numbers the aim solver flies, and they are deliberately simpler than
    /// the simulation's: a scattergun blast is six pellets with a spread and a
    /// per-pellet lifetime, and this calls it one projectile at 13 px/tick
    /// falling at 0.15. Do not "fix" one to match
    /// <c>Gg2.Simulation.Projectiles</c> — a bot that aims with the real numbers
    /// is a different bot, and every one of these was tuned against play.
    /// <para>
    /// <b>A speed of zero means "do not lead at all"</b>, which is how the
    /// hitscan and melee weapons — the Rifle, the knife, the Medigun — fall out
    /// of the model without a special case. <see cref="Solve"/> returns a
    /// straight line for them.
    /// </para>
    /// <para>
    /// <b>Drift is the shooter's own horizontal speed</b>, and only for the
    /// three weapons whose shots inherit it. It is <em>subtracted</em> from the
    /// target's velocity in the solver, because what matters is the relative
    /// motion of the muzzle and the target.
    /// </para>
    /// </remarks>
    public static (double Speed, double Gravity, double Drift) Ballistics(in CharacterState character)
    {
        var (speed, gravity) = character.Class switch
        {
            PlayerClass.Scout or PlayerClass.Engineer => (13.0, 0.15),
            PlayerClass.Heavy => (12.5, 0.15),
            PlayerClass.Spy => (21.0, 0.15),
            PlayerClass.Soldier => (12.0, 0.0),
            PlayerClass.Demoman => (12.0, 0.2),
            PlayerClass.Pyro => (8.25, 0.15),
            PlayerClass.Quote => (10.0, 0.0),

            // The sniper's Rifle is a hitscan and the medic's Medigun is a stored
            // target: neither has a projectile to lead.
            _ => (0.0, 0.0),
        };

        // Only the three whose shots carry the shooter's momentum: the
        // Scattergun, the Shotgun and the Minigun.
        var drift = character.Class is PlayerClass.Scout or PlayerClass.Engineer or PlayerClass.Heavy
            ? character.HSpeed
            : 0.0;

        return (speed, gravity, drift);
    }

    /// <summary>
    /// Where to point to hit a target at <paramref name="targetX"/> moving at
    /// <paramref name="targetVx"/> — <c>botAimSolve.gml</c>.
    /// </summary>
    /// <param name="floorY">
    /// The stand height of the surface under the target, so its predicted fall
    /// can be clamped there. Without it the solver aims at where a falling
    /// target <em>would</em> be if the floor were not there, which on a target
    /// standing still on the ground is straight down through it. Pass
    /// <see cref="NoFloor"/> when the graph does not know.
    /// </param>
    /// <returns>A direction in degrees, GM8's convention: 0 is right, 90 is up.</returns>
    public static double Solve(
        in CharacterState character,
        double targetX, double targetY,
        double targetVx, double targetVy,
        BotLeadMode lead,
        double floorY)
    {
        var (speed, gravity, drift) = Ballistics(in character);

        // A hitscan or a melee weapon: point at it.
        if (speed <= 0)
            return Gml.PointDirection(character.X, character.Y, targetX, targetY);

        if (lead == BotLeadMode.None)
        {
            // Still through the solver, not a straight line: gravity is
            // corrected for even when the target's motion is not. A rocket at
            // 400px drops several body heights.
            return Lead(character.X, character.Y, targetX, targetY, 0, 0, speed, gravity, drift, targetY);
        }

        if (lead == BotLeadMode.Linear)
        {
            // One pass: guess the flight time from the current distance and aim
            // at where the target will be then, with no iteration and no fall
            // prediction beyond the straight extrapolation.
            var t = Math.Clamp(
                Gml.PointDistance(character.X, character.Y, targetX, targetY) / speed,
                1,
                BotConstants.AimMaxTicks);

            return Lead(
                character.X, character.Y,
                targetX + (targetVx * t), targetY + (targetVy * t),
                0, 0, speed, gravity, drift,
                targetY + (targetVy * t));
        }

        return Lead(
            character.X, character.Y, targetX, targetY, targetVx, targetVy,
            speed, gravity, drift, floorY);
    }

    /// <summary>
    /// No floor is known under the target, so its predicted fall is not clamped.
    /// </summary>
    /// <remarks>
    /// The reference's own sentinel — "no map is this tall".
    /// </remarks>
    public const double NoFloor = 100_000;

    /// <summary>
    /// The floor under a point, for <see cref="Solve"/>'s clamp.
    /// </summary>
    /// <remarks>
    /// The nav graph's answer, which is the only one available without walking
    /// the mask: snap downward and take that node's stand height. Returns
    /// <see cref="NoFloor"/> when nothing resolves, which disables the clamp —
    /// the right answer over a pit, and the reason the sentinel exists rather
    /// than a nullable.
    /// </remarks>
    public static double FloorUnder(NavGraph? graph, double worldX, double worldY)
    {
        if (graph is null)
            return NoFloor;

        var snap = graph.SnapObjective(worldX, worldY);
        return snap.Node < 0 ? NoFloor : graph.StandY(snap.Node);
    }

    /// <summary>
    /// The iterated intercept — <c>botAimLead.gml</c>.
    /// </summary>
    /// <remarks>
    /// Fixed-point: guess the flight time from the straight-line distance, work
    /// out where the target will be then, re-measure, repeat.
    /// <c>BOT_AIM_ITERATIONS</c> passes, and then one more evaluation outside the
    /// loop — that trailing repeat is not redundant, because the loop's last
    /// action is to update <c>t</c> and the aim point has to be recomputed from
    /// it.
    /// <para>
    /// <b>The projectile's own drop is <c>grav × t × (t + 1) / 2</c>, not
    /// <c>grav × t² / 2</c>.</b> A projectile falls in discrete ticks, so after
    /// <c>t</c> ticks it has fallen the triangular number rather than the
    /// integral — and it is <em>subtracted</em> from the aim point, because
    /// aiming higher is how you compensate for a shot that drops.
    /// </para>
    /// <para>
    /// <b>The flight time is clamped to at least one tick</b>, which matters
    /// at point blank: a distance of zero gives a time of zero, and the whole
    /// prediction collapses to "aim at your own feet".
    /// </para>
    /// </remarks>
    public static double Lead(
        double sourceX, double sourceY,
        double targetX, double targetY,
        double targetVx, double targetVy,
        double speed, double gravity, double drift,
        double floorY)
    {
        var t = Gml.PointDistance(sourceX, sourceY, targetX, targetY) / speed;

        double aimX = targetX;
        double aimY = targetY;

        for (var i = 0; i <= BotConstants.AimIterations; i++)
        {
            t = Math.Clamp(t, 1, BotConstants.AimMaxTicks);

            aimX = targetX + ((targetVx - drift) * t);
            aimY = FallPredict(targetY, targetVy, t, floorY) - (gravity * t * (t + 1) / 2);

            t = Gml.PointDistance(sourceX, sourceY, aimX, aimY) / speed;
        }

        return Gml.PointDirection(sourceX, sourceY, aimX, aimY);
    }

    /// <summary>
    /// Where a falling target will be in <paramref name="t"/> ticks —
    /// <c>botFallPredict.gml</c>.
    /// </summary>
    /// <remarks>
    /// <b>Two phases, because GG2 has a terminal velocity.</b> A target
    /// accelerates at <c>NAV_JUMP_GRAVITY</c> until it reaches
    /// <c>NAV_JUMP_TERM_VY</c> and then falls at a constant rate, so a single
    /// quadratic over-predicts a long fall badly — and the aim solver's whole job
    /// is long falls.
    /// <para>
    /// And the result is clamped at the floor. Without that a target standing
    /// still on the ground is predicted to keep falling through it, and the bot
    /// aims below its feet.
    /// </para>
    /// </remarks>
    public static double FallPredict(double y, double vy, double t, double floorY)
    {
        var clampTicks = vy < NavConstants.JumpTermVy
            ? (NavConstants.JumpTermVy - vy) / NavConstants.JumpGravity
            : 0;

        var predicted = t <= clampTicks
            ? y + (vy * t) + (NavConstants.JumpGravity * t * t / 2)
            : y + (vy * clampTicks) + (NavConstants.JumpGravity * clampTicks * clampTicks / 2)
              + (NavConstants.JumpTermVy * (t - clampTicks));

        return Math.Min(predicted, floorY);
    }
}
