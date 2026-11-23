using GG2.Protocol;

namespace GG2.Nav;

/// <summary>
/// One class's horizontal motion constants, as <c>Character.Create</c> derives
/// them.
/// </summary>
internal readonly record struct NavCharacterProfile(string Name, double RunPower, double Accel, double MaxSpeed);

/// <summary>
/// The character the graph is written for.
/// </summary>
/// <remarks>
/// <see cref="NavJumpModel"/> proves an arc: a parabola with a <em>constant</em>
/// horizontal velocity, applied from the takeoff column at tick 0, with every
/// clearance cell along it checked. That is a statement about geometry and it is
/// true. It is not a statement about a character.
/// <para>
/// A GG2 character has no constant velocity — <c>Character/Begin Step</c> does
/// <c>hspeed = (hspeed + runPower * controlFactor) / baseFriction</c>, so it
/// approaches <c>basemaxspeed</c> geometrically and, standing still, starts at
/// zero. The follower's in-air tracker presses toward where the plan says the
/// bot should be by now, which repays the deficit out of the surplus between the
/// arc's speed and the class's own ceiling. <b>An arc that asks for most of that
/// ceiling has no surplus</b>, and the deficit built up over the acceleration
/// ramp is never repaid: the bot lands short, blacklists the edge, and the search
/// — which was never told — hands back the same cheapest route a few seconds
/// later.
/// </para>
/// <para>
/// ⚠️ <b>Two things decide whether the speed is there, and one of them is the
/// map.</b> The takeoff gate refuses to jump while the bot is going faster than
/// the arc asks for (<c>needVx + BOT_JUMP_VTOL</c>), so it cannot leave hot; and
/// the run-up is however much continuous walkable floor lies <em>behind</em> the
/// takeoff column, capped at <c>BOT_RUNUP_CELLS</c>. The second is the one that
/// bites. A one-column perch — the kind <c>ExtractNodes</c> emits wherever a
/// character can stand three-quarters off a ledge — offers no run-up at all in
/// the direction it overhangs, so every arc off it starts from a standstill.
/// <c>ctf_oldfort</c>'s n41 is one such perch and the graph used to offer it a
/// 102 px arc; a standing Heavy covers 75 px of it.
/// </para>
/// <para>
/// ⚠️ <b>Which class: Heavy.</b> <c>basemaxspeed</c> runs from Heavy's 4.53
/// px/tick to Scout's 7.93 over one shared graph, so an arc is only honest if the
/// slowest class asked to fly it can. That is also what
/// <see cref="NavConstants.JumpVx"/> already is, though the reference used it to
/// budget wall contact and never to reject an arc.
/// </para>
/// <para>
/// ⚠️ <b>This is a RESTRICTIVE filter and that is what makes it safe.</b> The
/// jump fan keeps a fixed number of candidates per side, so a <em>permissive</em>
/// change upstream of it means a different set kept, not a superset. A smaller
/// pool cannot displace anything — it only frees slots that were being spent on
/// arcs no bot could fly. Measured over all 24 shipped graphs when it landed in
/// the original nav generator: 293 jump edges removed, 174 kept in the freed slots, no
/// node lost, no team lost reachability on any map, and no route to any objective
/// longer by more than 2 cells.
/// </para>
/// </remarks>
internal static class NavCharacter
{
    // Character.Create: baseControl 0.85, baseFriction 1.15, and
    // basemaxspeed = abs(baseRunPower * baseControl / (baseFriction - 1)).
    private const double BaseControl = 0.85;
    private const double BaseFriction = 1.15;

    /// <summary>Below this an <em>uncontrolled</em> character is snapped to rest.</summary>
    private const double RestSpeed = 0.195;

    // Begin Step's switch sets controlFactor and frictionFactor from moveStatus,
    // and moveStatus 1 — "rocketing/mining myself" — is a different row from the
    // default:
    //
    //     case 1:   controlFactor = 0.65;   frictionFactor = 1;
    //     default:  controlFactor = 0.85;   frictionFactor = 1.15;
    //
    // so steering authority drops to 76% of normal AND friction stops existing.
    // `hspeed /= 1` is the whole of it: horizontal speed does not bleed at all
    // for the entire airborne period.
    private const double RocketControl = NavConstants.RjControl;
    private const double RocketFriction = NavConstants.RjFriction;

    /// <summary>
    /// How long a run-up or a flight is ever integrated for, in ticks.
    /// </summary>
    /// <remarks>
    /// A stop rather than a limit: <see cref="RunupSpeed"/> converges on
    /// <c>MaxSpeed</c> geometrically and 240 ticks is far past the point where
    /// another one changes the answer. It exists so a caller cannot hang on a
    /// distance nothing reaches.
    /// </remarks>
    private const int IntegrationCap = 240;

    // Objects/Characters/<Class>.events/Create.xml, baseRunPower. Heavy is the
    // one the graph is written for; the rest are here so the same model can
    // answer "which classes can fly this" without a second copy of the tick law.
    private static readonly (string Name, double RunPower)[] RunPowers =
    [
        ("heavy", 0.8), ("soldier", 0.9), ("sniper", 0.9), ("demoman", 1), ("engineer", 1),
        ("quote", 1.07), ("spy", 1.08), ("medic", 1.09), ("pyro", 1.1), ("scout", 1.4),
    ];

    /// <summary>The ordinary, on-the-ground profile of a class.</summary>
    public static NavCharacterProfile Profile(string name)
    {
        foreach (var (candidate, runPower) in RunPowers)
        {
            if (string.Equals(candidate, name, StringComparison.Ordinal))
                return new NavCharacterProfile(name, runPower, runPower * BaseControl, Math.Abs(runPower * BaseControl / (BaseFriction - 1)));
        }

        throw new ArgumentException($"unknown class {name}", nameof(name));
    }

    /// <summary>
    /// The same profile, re-derived for the rocket-jump row of Begin Step's
    /// switch.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b><see cref="NavCharacterProfile.MaxSpeed"/> is NOT re-derived from
    /// the rocket control factor.</b> <c>Character.Create</c> computes
    /// <c>basemaxspeed</c> once from <c>baseControl</c>/<c>baseFriction</c> and
    /// stores it; Begin Step's cap test reads that stored value and never
    /// recomputes it from the live <c>controlFactor</c>. So a rocket-jumping
    /// character accelerates more slowly toward exactly the same ceiling it
    /// always had.
    /// </remarks>
    public static NavCharacterProfile RocketProfile(string name)
    {
        var ground = Profile(name);
        return ground with { Accel = ground.RunPower * RocketControl };
    }

    /// <summary>
    /// One tick of <c>Character/Begin Step</c>'s horizontal motion, in the
    /// game's order.
    /// </summary>
    /// <remarks>
    /// The run key adds first and only while under the cap, friction divides
    /// unconditionally afterwards, and the rest snap applies only when nothing is
    /// held.
    /// </remarks>
    private static double Tick(double hspeed, int press, in NavCharacterProfile p)
    {
        if (press > 0 && hspeed <= p.MaxSpeed)
            hspeed += p.Accel;
        else if (press < 0 && hspeed >= -p.MaxSpeed)
            hspeed -= p.Accel;

        hspeed /= BaseFriction;

        if (Math.Abs(hspeed) < RestSpeed && press == 0)
            hspeed = 0;

        return hspeed;
    }

    /// <summary>
    /// <see cref="Tick"/> under moveStatus 1 — no friction, weaker steering.
    /// </summary>
    /// <remarks>
    /// The rest snap stays: Begin Step applies it after the friction divide
    /// regardless of moveStatus, gated on nothing being held.
    /// </remarks>
    private static double TickRocket(double hspeed, int press, in NavCharacterProfile p)
    {
        if (press > 0 && hspeed <= p.MaxSpeed)
            hspeed += p.Accel;
        else if (press < 0 && hspeed >= -p.MaxSpeed)
            hspeed -= p.Accel;

        hspeed /= RocketFriction;

        if (Math.Abs(hspeed) < RestSpeed && press == 0)
            hspeed = 0;

        return hspeed;
    }

    /// <summary>
    /// The speed a character carries into the takeoff column after
    /// <paramref name="distance"/> px of run-up from rest.
    /// </summary>
    /// <remarks>
    /// What matters is what it is carrying when it arrives, not how long it took
    /// to get there.
    /// </remarks>
    public static double RunupSpeed(double distance, in NavCharacterProfile p)
    {
        var v = 0.0;
        var x = 0.0;

        for (var i = 0; i < IntegrationCap && x < distance; i++)
        {
            v = Tick(v, 1, p);
            x += v;
        }

        return v;
    }

    /// <summary>
    /// The follower's in-flight tracker, replayed: how far along the arc the
    /// character actually gets, in px.
    /// </summary>
    /// <remarks>
    /// The bot presses toward where the plan says it should be by now and brakes
    /// when it is ahead, which is a pure 1-D problem — the arc's height was proven
    /// by the flight model and the tracker never touches it. One-pixel dead band,
    /// and the target is <c>min(vx * ticks, vx * airTicks)</c> with
    /// <c>airTicks</c> at 1 on the first airborne tick.
    /// </remarks>
    public static double FlyDistance(double needVx, double ticks, double v0, in NavCharacterProfile p)
    {
        var total = needVx * ticks;
        var v = v0;
        var x = 0.0;
        var n = Math.Max(1, (int)Math.Round(ticks, MidpointRounding.AwayFromZero));

        for (var air = 1; air <= n; air++)
        {
            var want = Math.Min(total, needVx * air);
            var press = 0;

            if (x < want - 1)
                press = 1;
            else if (x > want + 1)
                press = -1;

            v = Tick(v, press, p);
            x += v;
        }

        return x;
    }

    /// <summary>
    /// <see cref="FlyDistance"/> for an arc flown under moveStatus 1.
    /// </summary>
    /// <remarks>
    /// Same tracker, same dead band, different tick law — and it has to stay that
    /// way round: the tracker is the follower's and does not know what kind of arc
    /// it is on, while the motion is Begin Step's and does.
    /// <para>
    /// ⚠️ <b>The press is gated on SPEED as well as position, and the bare
    /// bang-bang law this replaces does not converge without friction under
    /// it.</b> Ordinary flight is damped by <c>baseFriction</c>'s ~13%-a-tick
    /// bleed; <c>frictionFactor</c> 1 removes that term, so every press
    /// permanently changes velocity and the controller limit-cycles. Measured live
    /// on <c>ctf_truefort</c> n551 → n327: hspeed ran 1.80, 2.08, 2.33, 2.54,
    /// 2.54, 1.67, 0.92, 0.27, −0.29, −0.78 on an arc wanting a steady 2.6,
    /// averaged ~0.6, and fell short sideways on an arc whose <em>height</em> was
    /// fine. With no friction the right law is "reach the arc's speed, then stop
    /// pressing": the arc was proven as a constant velocity from tick 0, and an
    /// unfrictioned character holds whatever it reaches.
    /// </para>
    /// <para>
    /// ⚠️ <b>The takeoff tick is deliberately flown under the ROCKET law</b> even
    /// though Begin Step's gravity on that one tick was still 0.6, because
    /// <c>moveStatus</c> is only set later in the same tick by the explosion. The
    /// discrepancy is real but it is vertical, not horizontal: the switch that
    /// sets <c>controlFactor</c> runs at the top of the <em>next</em> tick's Begin
    /// Step, by which point <c>moveStatus</c> is 1, and this loop's first
    /// iteration is that next tick. The vertical half is accounted for in
    /// <see cref="NavRocketJumpModel"/>.
    /// </para>
    /// </remarks>
    public static double FlyDistanceRocket(double needVx, double ticks, double v0, in NavCharacterProfile p)
    {
        var total = needVx * ticks;
        var v = v0;
        var x = 0.0;
        var n = Math.Max(1, (int)Math.Round(ticks, MidpointRounding.AwayFromZero));

        for (var air = 1; air <= n; air++)
        {
            var want = Math.Min(total, needVx * air);
            var press = 0;

            if (x < want - 1)
            {
                if (v < needVx)
                    press = 1;
            }
            else if (x > want + 1)
            {
                if (v > needVx)
                    press = -1;
            }

            v = TickRocket(v, press, p);
            x += v;
        }

        return x;
    }
}
