using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Bots;

/// <summary>
/// <c>botRocketReach</c>: how far along a ROCKET-JUMP arc a character actually
/// gets, and whether it has a rocket to make one with.
/// </summary>
/// <remarks>
/// <b><see cref="JumpModel.Reach"/> answers the same question for every other
/// edge kind and the two must not be merged: they differ in BOTH terms of the
/// motion law, not one.</b> <c>Character</c>'s Begin Step sets the control and
/// friction factors from <c>moveStatus</c>, and <c>moveStatus</c> 1 —
/// "rocketing/mining myself", which is exactly what a rocket jump is — is a
/// different row of that switch:
/// <code>
///   case 1:   controlFactor = 0.65;   frictionFactor = 1;
///   default:  controlFactor = 0.85;   frictionFactor = 1.15;
/// </code>
/// <para>
/// <b>A friction factor of 1 means <c>hspeed /= 1</c>: horizontal speed does
/// not bleed at all</b> for the whole airborne period (around 69 ticks on a
/// full-height rocket jump). A bot that leaves slow catches up faster, since
/// nothing takes speed back off it between presses — speed climbs linearly
/// rather than approaching a ceiling geometrically. A bot that leaves hot
/// cannot shed speed by coasting: an ordinary arc sheds about 13% a tick with
/// the key released, but here the only way down is to press against the
/// motion. A rocket jump carries its takeoff momentum the whole flight.
/// </para>
/// <para>
/// Using <see cref="JumpModel.Reach"/> for these arcs would accept ones the bot
/// sails past and refuse ones it flies easily — wrong in both directions at
/// once.
/// </para>
/// <para>
/// <b><c>gg2-nav-gen</c>'s <c>follow.js</c> runs the same law on the generator
/// side</b> and vetoes an arc before the edge is ever emitted. This is the
/// follower's own copy, asked at the takeoff gate. If one moves, move both, or
/// the gate answers a question about a flight the generator never proved.
/// </para>
/// </remarks>
public static class RocketModel
{
    /// <summary>
    /// How far along the arc this character gets, leaving the takeoff column
    /// carrying <paramref name="v0"/> along the jump.
    /// </summary>
    /// <remarks>
    /// <b>Not monotonic in <paramref name="v0"/></b>, for the same reason
    /// <see cref="JumpModel.Reach"/> is not: arriving ahead of a
    /// constant-velocity schedule makes the tracker brake. A caller must not
    /// bisect this for a threshold, and the takeoff gate must keep its
    /// <c>BOT_TAKEOFF_PATIENCE</c> escape hatch.
    /// </remarks>
    public static double Reach(in CharacterState character, double needVx, double flightTicks, double v0)
    {
        // The control factor is not read off the character, unlike JumpModel:
        // at the moment this is asked the bot is still on the ground with
        // moveStatus 0, so the live value is the ordinary 0.85 — the rocket
        // that sets moveStatus to 1 does not exist yet. NAV_RJ_CONTROL is the
        // value that will apply for every tick this loop models.
        var acceleration = character.RunPower * NavConstants.RjControl;

        // basemaxspeed is read live and is correct live: Character's Create
        // computes it once from baseControl/baseFriction and stores it, and Begin
        // Step's cap test reads that stored value rather than recomputing it from
        // the live controlFactor. A rocket-jumping character accelerates more
        // slowly toward exactly the same ceiling it always had.
        var maxSpeed = ClassStats.For(character.Class).BaseMaxSpeed;

        // NAV_RJ_FRICTION is 1 and the divide below is written out rather than
        // folded away, so that a change to the constant is a change to this
        // method's behaviour rather than a silent no-op.
        //
        // A local rather than a `const`: as a const the guard below is a
        // compile-time false and the compiler rejects the body as unreachable,
        // which would force deleting the very guard that makes changing the
        // constant safe. The reference guards for the same reason JumpModel does —
        // a caller that hands over something that is not a character should get a
        // bad answer, not a division.
        var friction = (double)NavConstants.RjFriction;

        if (friction <= 0)
            return 0;

        var total = needVx * flightTicks;
        var speed = v0;
        var travelled = 0.0;

        // GM8's `round` — half to even. A fractional tick count rounding the
        // other way is one whole tick of an arc.
        var ticks = Math.Max(1, (int)Gml.Round(flightTicks));

        for (var air = 1; air <= ticks; air++)
        {
            var want = Math.Min(total, needVx * air);

            // The speed gate, which is what makes this law converge with no
            // friction under it. Position decides whether to press; speed decides
            // whether pressing can help. Once the arc's own vx is reached there is
            // nothing to gain — an unfrictioned character holds it — and pressing
            // on from there is what made the real tracker limit-cycle.
            //
            // JumpModel.Reach has no such gate and must not grow one: friction
            // is what bounds it there.
            var press = 0;

            if (travelled < want - 1)
            {
                if (speed < needVx)
                    press = 1;
            }
            else if (travelled > want + 1)
            {
                if (speed > needVx)
                    press = -1;
            }

            if (press > 0 && speed <= maxSpeed)
                speed += acceleration;
            else if (press < 0 && speed >= -maxSpeed)
                speed -= acceleration;

            speed /= friction;

            // The "ice skating" snap, and it applies only with nothing held.
            if (Math.Abs(speed) < BotConstants.RestSpeed && press == 0)
                speed = 0;

            travelled += speed;
        }

        return travelled;
    }

    /// <summary>
    /// Has this character a rocket to jump with, right now.
    /// </summary>
    /// <remarks>
    /// What a rocket jump needs that no other edge does. The launcher has a
    /// 30-tick refire and four rounds, so "ready" is a real gate and not a
    /// formality — and <b>the health test is re-asked here because the search only
    /// ever answered it at plan time</b>: a route planned at 160hp is still
    /// installed at 35.
    /// <para>
    /// <b><c>hp &gt; NAV_RJ_DAMAGE</c> is strictly greater</b>, because the
    /// damage lands before the bot does — a soldier on exactly 30 arrives dead.
    /// </para>
    /// <para>
    /// In the reference this is a chain of nested ifs rather than one
    /// and-chain, twice over: GM8's <c>and</c> does not short-circuit, so a guard
    /// folded in beside the access it guards is not a guard — and
    /// <c>currentWeapon != -1</c> has to come first because -1 is GM8's constant
    /// for <c>self</c>, which makes <c>instance_exists(-1)</c> true and the read
    /// that follows a silent read of the caller. Neither hazard exists here; the
    /// structure is recorded so the next person to read the GML knows why it looks
    /// like that.
    /// </para>
    /// </remarks>
    public static bool Ready(in CharacterState character, in WeaponState weapon) =>
        character.Hp > NavConstants.RjDamage
        && WeaponStats.KindFor(character.Class) == WeaponKind.Rocketlauncher
        && weapon.ReadyToShoot
        && weapon.AmmoCount > 0;
}
