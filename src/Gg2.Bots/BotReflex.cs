using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Bots;

/// <summary>
/// The two reactions to something already in the air: the pyro's airblast, and
/// jumping out of a rocket's way.
/// </summary>
/// <remarks>
/// <c>botInBlastBeam</c>, <c>botIncomingProjectile</c> and <c>botRocketDodge</c>.
/// They live together because the first is only ever called by the second, and
/// because both of the outer two answer the same shape of question — <b>model the
/// thing's actual future rather than measuring a radius</b>, which is what each of
/// them was originally got wrong by doing.
/// </remarks>
public static class BotReflex
{
    /// <summary>
    /// How many projectiles a single reflex pass will look at.
    /// </summary>
    /// <remarks>
    /// The reference walks every instance of each type. This is a fixed scratch
    /// buffer instead, sized well past what a match produces — a pool of 512 in
    /// total, and the search is already radius-limited. ⚠️ It is a ceiling on a
    /// <em>reaction</em>, so overflowing it declines a blast rather than
    /// mis-declining one, which is the safe direction.
    /// </remarks>
    private const int Scan = 32;

    /// <summary>
    /// Is this point inside the volume the character's airblast would actually
    /// reflect — <c>botInBlastBeam</c>.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>It is a BEAM, not a cone.</b> What gates a reflect is
    /// <c>collision_circle(projectile.x, projectile.y, 5, poof, false, true)</c>
    /// against an <c>AirBlastO</c> spawned 25px along the aim, whose mask is a
    /// narrow rectangle — so a cone test is the wrong shape at every distance.
    /// <para>
    /// ⚠️ <b>The three constants are measured off the running exe, not off the
    /// sprite file, which does not agree with it.</b> <c>AirBlastMaskS</c> is a
    /// 120×30 image but its mask bounds are AUTO, so GM8 takes them from the opaque
    /// pixels: left 8, right 96, top -13, bottom 14 about the poof's own origin. The
    /// poof spawns 25px out and <c>collision_circle</c> adds its 5px radius, giving
    /// near 28 (8 + 25 − 5), reach 126 (96 + 25 + 5) and half-width 18 (13 + 5).
    /// </para>
    /// <para>
    /// ⚠️ <b>There IS a near edge, and it is not a rounding detail</b>: a rocket
    /// already at the pyro's feet is UNDER the beam and cannot be blasted at all.
    /// </para>
    /// <para>
    /// ⚠️ <b>The perpendicular offset is compared unsigned</b>, so the beam is
    /// symmetric about the aim line. The real mask is very slightly not (−13 above,
    /// +14 below); taking the smaller as the half-width is the conservative reading,
    /// which is the one to want — a blast declined costs nothing and a blast spent
    /// costs 40 ticks of cooldown. Modelling the rotated beam as a straight
    /// rectangle is conservative on the diagonals for the same reason: at 45° GM8
    /// reports the axis-aligned bounds of the rotated rectangle, which is a superset
    /// of the true beam.
    /// </para>
    /// </remarks>
    public static bool InBlastBeam(
        double selfX, double selfY, double px, double py,
        double forwardX, double forwardY, double sideX, double sideY)
    {
        var dx = px - selfX;
        var dy = py - selfY;

        var forward = (dx * forwardX) + (dy * forwardY);

        if (forward < BotConstants.AirblastNear || forward > BotConstants.AirblastReach)
            return false;

        return Math.Abs((dx * sideX) + (dy * sideY)) <= BotConstants.AirblastHalfw;
    }

    /// <summary>
    /// Is a reflectable enemy projectile somewhere the airblast can reach, having
    /// already flown <paramref name="minTravel"/> px — <c>botIncomingProjectile</c>.
    /// </summary>
    /// <remarks>
    /// The reflectable set is Rocket, Flare and Mine
    /// (<c>Flamethrower/User Event 2</c>).
    /// <para>
    /// ⚠️ <b>This used to be a radius-and-arc question in the reference — within
    /// 150px and 75° of the aim — and both numbers were wrong the same way</b>: they
    /// were copied from the Flamethrower's own <c>blastDistance</c> and
    /// <c>blastAngle</c>, neither of which gates a reflect any more. The bot pressed
    /// SPECIAL for rockets it could not possibly touch, and <b>the press is not
    /// free</b>: the reflect test runs only on the press frame and the press sets
    /// <c>readyToBlast</c> false for 40 ticks, so a wasted blast on a rocket 150px
    /// out was still on cooldown when it arrived. Reported from play as a pyro
    /// unable to deflect rockets even from a distance — not a delay that was too
    /// long, a trigger volume about ten times wider than the weapon.
    /// </para>
    /// <para>
    /// ⚠️ <b>Both the projectile's current position and its position one tick on are
    /// tested, and either will do.</b> The press and the blast do not resolve in the
    /// same place in the frame — the input is written, then Begin Step runs the
    /// reflect, then the projectile pass moves it — and a rocket covers about 12px a
    /// tick, most of the beam's half-width. Rather than depend on that ordering,
    /// allow the frame either way round.
    /// </para>
    /// <para>
    /// ⚠️ <b><paramref name="minTravel"/> is a DISTANCE on purpose.</b> The airblast
    /// has no delay of any kind — this is re-evaluated every tick — so without it a
    /// rocket fired point-blank came back on the frame it was born, which was
    /// confirmed inhuman. A distance says "do not reflect a rocket the instant it
    /// leaves the barrel" directly rather than by proxy, and is frame-rate
    /// independent. It is 60 rather than the 120 it was first set to, and the beam
    /// is why: the window is 28 to 126px, so at 120 a shooter standing 130px away
    /// produced a rocket that was never eligible at any point on its flight.
    /// </para>
    /// </remarks>
    public static bool IncomingProjectile(in WorldView world, int slot, double minTravel)
    {
        if (!world.IsAlive(slot))
            return false;

        ref readonly var self = ref world.Character(slot);
        var team = world.Player(slot).Team;

        // The aim's unit vector and its perpendicular, once — the same for every
        // projectile on a given tick. ⚠️ Gml.LengthDir rather than cos/sin: GM8's y
        // axis points down and these already account for it, and the engine snaps
        // their results where a bare trig call does not.
        var forwardX = Gml.LengthDirX(1, self.AimDirection);
        var forwardY = Gml.LengthDirY(1, self.AimDirection);
        var sideX = Gml.LengthDirX(1, self.AimDirection + 90);
        var sideY = Gml.LengthDirY(1, self.AimDirection + 90);

        Span<ProjectileHandle> found = stackalloc ProjectileHandle[Scan];

        // Radius-limited only as a cheap pre-filter — the beam is what decides. The
        // reach is 126px and a tick of travel is added on top.
        var count = world.ProjectilesWithin(
            self.X, self.Y, BotConstants.AirblastReach + BotConstants.DodgeRange, found);

        for (var i = 0; i < count; i++)
        {
            if (!world.State.TryGetProjectile(found[i], out var projectile))
                continue;

            if (projectile.Kind is not (ProjectileKind.Rocket or ProjectileKind.Mine or ProjectileKind.Flare))
                continue;

            if (projectile.Team == team)
                continue;

            if (TravelledFrom(in world, in projectile) < minTravel)
                continue;

            if (InBlastBeam(self.X, self.Y, projectile.X, projectile.Y, forwardX, forwardY, sideX, sideY)
                || InBlastBeam(
                    self.X, self.Y,
                    projectile.X + projectile.Motion.HSpeed,
                    projectile.Y + projectile.Motion.VSpeed,
                    forwardX, forwardY, sideX, sideY))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Would jumping right now take this bot out of the path of a rocket that is
    /// otherwise going to hit it — <c>botRocketDodge</c>.
    /// </summary>
    /// <remarks>
    /// Grounded callers only: it models the jump from a standstill.
    /// <para>
    /// ⚠️ <b>The old test was "is an enemy projectile within 150px", and it was
    /// wrong in BOTH directions.</b> It jumped at rockets that were never going to
    /// connect, and — reported from play — it jumped <em>into</em> ones fired over
    /// the bot's head: a rocket passing above is exactly the case where rising 57px
    /// is what creates the collision, so aiming high was a guaranteed hit rather
    /// than a miss. <b>Anything that answers this has to model the arc</b>, because
    /// "would it hit" and "does jumping help" have opposite answers depending on
    /// where the shot is aimed, and no proximity test can tell them apart.
    /// </para>
    /// <para>
    /// The model is small because both bodies are. A rocket is a straight line at
    /// constant velocity — GG2 rockets have no gravity and no drag, and nothing ever
    /// touches their speeds after Create. The bot carries its horizontal speed
    /// forward and either stands still or flies the standing jump,
    /// <c>rise(t) = NAV_JUMP_V0·t − NAV_JUMP_GRAVITY·t²/2</c>, the same closed form
    /// the graph generator uses.
    /// </para>
    /// <para>
    /// ⚠️ <b>The verdict is "the standing bot is hit at some tick AND the jumping
    /// bot is hit at none".</b> That is the whole feature: never jump at a rocket
    /// that misses anyway, and never jump into one. A rocket at the feet clears
    /// easily, one at the chest needs about 13px of rise and so about two ticks of
    /// warning, and one above the head fails the first half and is left alone.
    /// </para>
    /// <para>
    /// The rise is SUBTRACTED rather than clamped, so in principle it goes negative
    /// once the arc has come back down past where it started — a late crossing met by
    /// a bot that is falling again, which has to count as a hit.
    /// <para>
    /// ⚠️ <b>That case is unreachable and the subtraction is faithful rather
    /// than load-bearing.</b> Measured: with <c>NAV_JUMP_V0</c> 8.3 and
    /// <c>NAV_JUMP_GRAVITY</c> 0.6 the rise crosses zero at t = 27.67, and
    /// <c>BOT_DODGE_TICKS</c> is <b>26</b> — one tick short. Over the whole walk the
    /// rise runs from 8.0 at t=1 to 13.0 at t=26 and never dips below 8. Clamping it
    /// at zero is a no-op, checked by mutation, so nothing here gates it; it is
    /// written the reference's way because widening the budget would make it matter
    /// and a clamp would then be silently wrong.
    /// </para>
    /// <para>
    /// ⚠️ <b>Splash is deliberately not modelled.</b> A rocket detonating on the
    /// floor under a jumping bot still catches it inside <c>blastRadius</c> 65, so
    /// counting splash would refuse nearly every dodge. What the jump reliably
    /// avoids is the direct hit — 25 damage plus the 30 from an explosion centred on
    /// the body, plus the knockback — and that is worth having.
    /// </para>
    /// </remarks>
    public static bool RocketDodge(in WorldView world, int slot)
    {
        if (!world.IsAlive(slot))
            return false;

        ref readonly var self = ref world.Character(slot);
        var team = world.Player(slot).Team;

        // The rocket's own size, folded into the body box once rather than into
        // every test.
        var halfWidth = BotConstants.BodyHalfw + BotConstants.RocketRadius;

        Span<ProjectileHandle> found = stackalloc ProjectileHandle[Scan];
        var count = world.ProjectilesWithin(self.X, self.Y, BotConstants.DodgeRange, found);

        for (var i = 0; i < count; i++)
        {
            if (!world.State.TryGetProjectile(found[i], out var rocket))
                continue;

            if (rocket.Kind != ProjectileKind.Rocket || rocket.Team == team)
                continue;

            var hitStanding = false;
            var hitJumping = false;

            for (var t = 1; t <= BotConstants.DodgeTicks; t++)
            {
                var rocketX = rocket.X + (rocket.Motion.HSpeed * t);
                var rocketY = rocket.Y + (rocket.Motion.VSpeed * t);
                var bodyX = self.X + (self.HSpeed * t);

                if (Math.Abs(rocketX - bodyX) > halfWidth)
                    continue;

                if (rocketY >= self.Y - BotConstants.BodyUp - BotConstants.RocketRadius
                    && rocketY <= self.Y + BotConstants.BodyDown + BotConstants.RocketRadius)
                {
                    hitStanding = true;
                }

                var rise = (NavConstants.JumpV0 * t) - (NavConstants.JumpGravity * t * t / 2);
                var jumpY = self.Y - rise;

                if (rocketY >= jumpY - BotConstants.BodyUp - BotConstants.RocketRadius
                    && rocketY <= jumpY + BotConstants.BodyDown + BotConstants.RocketRadius)
                {
                    hitJumping = true;
                }

                // Nothing later in the walk can change the verdict for this rocket.
                if (hitStanding && hitJumping)
                    break;
            }

            if (hitStanding && !hitJumping)
                return true;
        }

        return false;
    }

    /// <summary>
    /// How far this projectile has flown from whoever fired it.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A rocket already carries this number and the other two do not.</b>
    /// <c>move_all_bullets</c> measures a rocket from its owner's live position every
    /// step for the game's own 800px fade, and this port keeps the same pair of
    /// coordinates (<c>LastKnownX/Y</c>) — which freeze where a dead shooter fell, so
    /// the distance keeps growing and a dead shooter's rocket stays eligible.
    /// <para>
    /// A flare and a mine have no such field, so theirs is measured to the owner's
    /// character. ⚠️ <b>One whose owner has since died is treated as having travelled
    /// far enough</b> — it has, by then.
    /// </para>
    /// </remarks>
    private static double TravelledFrom(in WorldView world, in ProjectileState projectile)
    {
        if (projectile.Kind == ProjectileKind.Rocket)
            return Gml.PointDistance(projectile.X, projectile.Y, projectile.LastKnownX, projectile.LastKnownY);

        var owner = projectile.OwnerSlot;

        if (owner == ProjectileState.NoOwner || !world.IsAlive(owner))
            return double.MaxValue;

        ref readonly var shooter = ref world.Character(owner);

        return Gml.PointDistance(projectile.X, projectile.Y, shooter.X, shooter.Y);
    }
}
