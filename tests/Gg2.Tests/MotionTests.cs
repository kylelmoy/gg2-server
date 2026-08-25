using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// GM8's four motion variables and the way writing one rewrites the others.
/// </summary>
/// <remarks>
/// <b>What is pinned here is the model, not the numbers.</b> The arithmetic is
/// transcribed from OpenGMK's <c>instance.rs</c> and has not yet been measured
/// against the running game — no phase-4 code path writes <c>direction</c> or
/// <c>speed</c>, so no existing fixture can see it. These tests exist so that
/// the three behaviours that are easy to "simplify" away cannot be, and so that
/// a future oracle has something concrete to disagree with:
/// <list type="number">
/// <item>all four are stored, so a direction survives a zero speed;</item>
/// <item>writing a component the value it already holds is a no-op;</item>
/// <item>a recomputed component snaps to a nearby integer.</item>
/// </list>
/// </remarks>
public sealed class MotionTests
{
    [Fact]
    public void ComponentsFollowSpeedAndDirection()
    {
        var motion = default(Motion);
        motion.Speed = 10;
        motion.Direction = 0;

        Assert.Equal(10, motion.HSpeed);
        Assert.Equal(0, motion.VSpeed);
    }

    /// <summary>
    /// Straight up is negative <c>vspeed</c>: GM8's y axis points down and its
    /// direction is measured anticlockwise from east.
    /// </summary>
    [Fact]
    public void NinetyDegreesIsUpwards()
    {
        var motion = default(Motion);
        motion.SetPolar(10, 90);

        Assert.Equal(-10, motion.VSpeed);
    }

    /// <summary>
    /// The snap. <c>cos(90 degrees)</c> is 6.1e-17 rather than zero, so
    /// <c>hspeed</c> would carry a residue into every later comparison — and
    /// <see cref="Gg2.Protocol.Gml"/>'s own 1e-13 fuzz is far too tight to
    /// absorb what a large speed turns that into.
    /// </summary>
    [Fact]
    public void RecomputedComponentsSnapToIntegers()
    {
        var motion = default(Motion);
        motion.SetPolar(10, 90);

        Assert.Equal(0, motion.HSpeed);
        Assert.NotEqual(0, Math.Cos(90 * (Math.PI / 180.0)));
    }

    /// <summary>Anything further than 1e-4 from an integer is left exactly alone.</summary>
    [Fact]
    public void ComponentsAwayFromIntegersAreNotSnapped()
    {
        var motion = default(Motion);
        motion.SetPolar(10, 30);

        Assert.Equal(10 * Math.Cos(30 * (Math.PI / 180.0)), motion.HSpeed);
    }

    /// <summary>
    /// The whole reason all four are stored. A projectile stopped by a reflect,
    /// or a character brought to rest, still knows which way it was pointing.
    /// </summary>
    [Fact]
    public void DirectionSurvivesAZeroSpeed()
    {
        var motion = default(Motion);
        motion.SetPolar(10, 135);
        motion.Speed = 0;

        Assert.Equal(0, motion.HSpeed);
        Assert.Equal(0, motion.VSpeed);
        Assert.Equal(135, motion.Direction);
    }

    /// <summary>
    /// <c>set_hspeed</c> returns early when the value is unchanged, so an
    /// idempotent write does not collapse a direction the components can no
    /// longer express.
    /// </summary>
    [Fact]
    public void WritingAComponentItAlreadyHoldsChangesNothing()
    {
        var motion = default(Motion);
        motion.SetPolar(0, 200);

        motion.HSpeed = 0;
        motion.VSpeed = 0;

        Assert.Equal(200, motion.Direction);
    }

    /// <summary>And a real change does recompute it.</summary>
    [Fact]
    public void WritingADifferentComponentRecomputesDirection()
    {
        var motion = default(Motion);
        motion.SetPolar(0, 200);

        motion.HSpeed = 5;

        Assert.Equal(0, motion.Direction);
        Assert.Equal(5, motion.Speed);
    }

    [Fact]
    public void DirectionIsWrappedIntoZeroToThreeSixty()
    {
        var motion = default(Motion);

        motion.Direction = 370;
        Assert.Equal(10, motion.Direction);

        motion.Direction = -90;
        Assert.Equal(270, motion.Direction);
    }

    /// <summary>
    /// <c>motion_add</c> adds to the components, not to the polar pair — so
    /// pushing a stationary thing east and then north leaves it going
    /// north-east rather than north.
    /// </summary>
    [Fact]
    public void MotionAddAccumulatesComponents()
    {
        var motion = default(Motion);
        motion.MotionAdd(0, 3);
        motion.MotionAdd(90, 4);

        Assert.Equal(3, motion.HSpeed, 12);
        Assert.Equal(-4, motion.VSpeed, 12);
        Assert.Equal(5, motion.Speed, 12);
    }

    /// <summary>
    /// <b><c>motion_add</c> does not snap, and the polar setters do.</b>
    /// OpenGMK's <c>motion_add</c> goes through <c>set_hvspeed</c>, which
    /// recomputes <em>direction</em> — the snap lives on the other path, in
    /// <c>update_hvspeed</c>. So a knockback straight up leaves a 2.4e-16
    /// residue in <c>hspeed</c> that an identical-looking
    /// <c>direction = 90; speed = 4;</c> would have cleaned away.
    /// <para>
    /// It is small enough for <see cref="Gg2.Protocol.Gml"/>'s 1e-13 comparison
    /// fuzz to absorb, which is exactly why it is worth pinning: it will look
    /// like noise to whoever finds it next, and "cleaning it up" would change a
    /// rocket-jump trajectory.
    /// </para>
    /// </summary>
    [Fact]
    public void MotionAddLeavesTheResidueThePolarSettersWouldHaveSnappedAway()
    {
        var pushed = default(Motion);
        pushed.MotionAdd(90, 4);

        var assigned = default(Motion);
        assigned.SetPolar(4, 90);

        Assert.NotEqual(0, pushed.HSpeed);
        Assert.Equal(0, assigned.HSpeed);
        Assert.True(Math.Abs(pushed.HSpeed) < Gg2.Protocol.Gml.Epsilon);
    }

    /// <summary>
    /// <c>speed</c> is <c>sqrt(h^2+v^2)</c> after a component write. Never
    /// <c>Math.Hypot</c>, which rescales and loses the last bit — measured at
    /// 636 of 722 samples when <c>distance_to_object</c> was settled.
    /// </summary>
    [Fact]
    public void SpeedIsTheUnscaledRoot()
    {
        var motion = default(Motion);
        motion.SetComponents(1e-3, 2e-3);

        Assert.Equal(Math.Sqrt((1e-3 * 1e-3) + (2e-3 * 2e-3)), motion.Speed);
    }
}
