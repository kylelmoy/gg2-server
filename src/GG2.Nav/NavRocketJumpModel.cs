using GG2.Protocol;

namespace GG2.Nav;

/// <summary>
/// A Soldier firing at its own feet — and, on the same law with the mine's
/// numbers, a Demoman detonating its own sticky.
/// </summary>
/// <remarks>
/// <b>The manoeuvre, tick by tick.</b> All three of these happen inside ONE tick,
/// in this order, and the order is what makes it a rocket jump at all rather than
/// an ordinary one:
/// <list type="number">
/// <item><c>Character/Begin Step</c>'s ATTACK branch, which comes BEFORE the jump
/// branch in the same event: <c>Rocketlauncher/User Event 3</c> creates the Rocket
/// 20 px along the aim direction from the gun.</item>
/// <item>The jump branch: <c>vspeed = -jumpStrength</c>, so −8.3.</item>
/// <item><c>GameServer/Step</c> → <c>move_all_bullets</c>: the rocket steps 13 px
/// down, meets the floor at the feet, and explodes.</item>
/// </list>
/// <para>
/// ⚠️⚠️ <b><see cref="NavConstants.RjImpulse"/> is 5.285 where Rocket's
/// <c>knockback</c> is 8, and that gap was MEASURED.</b> The first version of this
/// model derived 8, promised a 276 px apex, and produced edges the follower flew
/// 206 px of and then blacklisted. It is not distance falloff — measured
/// self-damage is the full 29.5–30, so the blast <em>is</em> at the feet. It is
/// <c>vectorfactor</c>, <c>sqrt(sin⁴ + cos⁴)</c>, which is 1 only when the impulse
/// is axis-aligned. <c>Rocketlauncher</c>'s <c>xoffset = -15</c> puts the muzzle
/// ~15 px to one side of the character, so a rocket aimed straight down detonates
/// down <em>and across</em>, the blast direction lands near 53° rather than 90°,
/// and ~26% of the impulse disappears into that term. <b>A "straight down" rocket
/// jump is already an angled one.</b> Launch velocity measured live is a
/// reproducible −14.40, for a 192 px apex. <b>Measure the launch; do not derive
/// it.</b>
/// </para>
/// <para>
/// ⚠️ <b>Gravity, and the one tick this model is knowingly wrong about.</b> Begin
/// Step: <c>if(rocketJumping) applied_gravity += 0.54; else += 0.6;</c> — a 10%
/// weaker gravity for the whole airborne period, which is why a naive model
/// reusing <see cref="NavConstants.JumpGravity"/> under-predicts the apex by ~28
/// px. On the TAKEOFF tick itself <c>moveStatus</c> is still 0 when Begin Step
/// reads it, so that one tick's gravity is genuinely 0.6. This model uses 0.54
/// throughout, which over-predicts the apex by 0.03 px — four thousandths of one
/// cell — and that is <em>recorded</em> rather than modelled, because a special
/// case on tick 0 is exactly the kind of "harmless" refinement this project has
/// three stories about.
/// </para>
/// <para>
/// ⚠️ <b>Horizontal is the part that is genuinely different.</b> moveStatus 1
/// gives <c>controlFactor = 0.65; frictionFactor = 1</c>, so horizontal speed does
/// not bleed AT ALL for the whole flight. The character veto therefore uses
/// <see cref="NavCharacter.FlyDistanceRocket"/>; using the ordinary law would
/// accept arcs the bot overshoots (it cannot coast speed off) and refuse arcs it
/// flies easily (it catches up without friction fighting it) — wrong in both
/// directions at once.
/// </para>
/// </remarks>
internal sealed class NavRocketJumpModel : NavArcModel
{
    private const int Cell = NavConstants.CellSize;

    private static readonly double TermTicks = NavConstants.JumpTermVy / NavConstants.RjGravity;

    private static readonly double TermHeight = NavConstants.RjGravity * TermTicks * TermTicks / 2;

    private readonly double _v0;

    private double _capIn = double.NaN;
    private double _capOut;
    private double _capRiseEnds;

    private NavRocketJumpModel(byte[] free, int width, int height, double v0, double apex, int maxTicks)
        : base(free, width, height, apex, (int)Math.Ceiling(apex / Cell) + 1, maxTicks) =>
        _v0 = v0;

    /// <summary>How high this launch rises with nothing above it, in px.</summary>
    public double ApexPx => Apex;

    /// <summary>
    /// The Soldier's: <c>(NAV_JUMP_V0 + NAV_RJ_IMPULSE) · NAV_RJ_SPEEDUP</c> =
    /// 14.40 px/tick, for a 192 px apex.
    /// </summary>
    /// <remarks>
    /// The <c>speedup</c> term is the explosion's own <c>speed *= 1.06</c> on the
    /// owner. Against an ordinary jump's 57.4 px that is three and a third jumps of
    /// height, and it is the number the bot actually achieves rather than the one
    /// the textbook impulse would give.
    /// </remarks>
    public static NavRocketJumpModel ForRocket(byte[] free, int width, int height) =>
        Make(free, width, height,
            (NavConstants.JumpV0 + NavConstants.RjImpulse) * NavConstants.RjSpeedup,
            NavConstants.RjMaxTicks);

    /// <summary>
    /// The Demoman's: <c>NAV_JUMP_V0 + NAV_SJ_IMPULSE</c> = 14.46 px/tick, for a
    /// 193.6 px apex.
    /// </summary>
    /// <remarks>
    /// <b>The same class rather than a fourth model, because the flight law is
    /// identical and only the numbers differ.</b> <c>Mine/User Event 2</c> sets
    /// <c>moveStatus = 1</c> on its owner exactly as <c>Rocket/User Event 5</c>
    /// does, and gravity, control and friction are properties of that moveStatus
    /// row rather than of the projectile — so the sticky envelope reads
    /// <c>NAV_RJ_GRAVITY</c> and friends deliberately. No <c>speedup</c> term: the
    /// mine's self-branch has no <c>speed *= 1.06</c>.
    /// <para>
    /// ⚠️ <b>The impulse is MEASURED, not derived, and the derivation was wrong —
    /// the rocket jump's lesson, paid a second time by an author who had read
    /// it.</b> The derived 10 (full knockback, no muzzle offset, vectorfactor ~1,
    /// no falloff — each step individually defensible) promised an 18.3 launch and
    /// a ~310 px apex; the manoeuvre flown in this port's engine-exact mine physics
    /// measures −14.460, identical to three decimals on four maps, pinned as
    /// <c>MineJumpTests</c>. 60% too generous.
    /// </para>
    /// <para>
    /// ⚠️⚠️ <b>THE GML GAME MUST NEVER LOAD A CACHE CONTAINING TYPE-7 EDGES.</b>
    /// Its <c>navFindPath</c> takes <c>canDouble</c> and <c>canRocket</c> and has
    /// <em>no</em> <c>canSticky</c> — a type-7 edge in its cache routes every class
    /// over it, forever. This port's <c>NavGraph.EdgeRoutable</c> refuses them by
    /// class, which is the whole reason the stage may exist here at all, and why
    /// it stays off by default.
    /// </para>
    /// </remarks>
    public static NavRocketJumpModel ForSticky(byte[] free, int width, int height) =>
        Make(free, width, height,
            NavConstants.JumpV0 + NavConstants.SjImpulse,
            NavConstants.SjMaxTicks);

    private static NavRocketJumpModel Make(byte[] free, int width, int height, double v0, int maxTicks) =>
        new(free, width, height, v0, v0 * v0 / (2 * NavConstants.RjGravity), maxTicks);

    public override double Height(double t, double cap)
    {
        var (clamped, riseEnds) = Rise(cap);

        if (t <= riseEnds)
            return (_v0 * t) - (NavConstants.RjGravity * t * t / 2);

        var d = t - riseEnds;
        var drop = d <= TermTicks
            ? NavConstants.RjGravity * d * d / 2
            : TermHeight + (NavConstants.JumpTermVy * (d - TermTicks));

        return clamped - drop;
    }

    public override double FlightTime(int fromRow, int toRow, double cap)
    {
        var (clamped, riseEnds) = Rise(cap);
        var riseWorld = (fromRow - toRow) * (double)Cell;

        if (riseWorld > clamped)
            return -1;

        var fall = clamped - riseWorld;
        var d = fall <= TermHeight
            ? Math.Sqrt(2 * fall / NavConstants.RjGravity)
            : TermTicks + ((fall - TermHeight) / NavConstants.JumpTermVy);

        return riseEnds + d;
    }

    private (double Cap, double RiseEnds) Rise(double cap)
    {
        if (cap.Equals(_capIn))
            return (_capOut, _capRiseEnds);

        var clamped = Math.Clamp(cap, 0, Apex);

        var riseEnds = (_v0 - Math.Sqrt(Math.Max(0, (_v0 * _v0) - (2 * NavConstants.RjGravity * clamped))))
            / NavConstants.RjGravity;

        _capIn = cap;
        _capOut = clamped;
        _capRiseEnds = riseEnds;
        return (clamped, riseEnds);
    }
}
