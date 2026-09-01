using Gg2.Simulation;

namespace Gg2.Bots;

/// <summary>
/// The hybrid handover gate: when a micro-controller takes the wheel from the
/// scripted layer.
/// </summary>
/// <remarks>
/// ⚠️ <b>Extracted from <see cref="PolicyBot"/> on 2026-09-01 so a second
/// micro-controller could use it, and extracted rather than copied on purpose.</b>
/// CLAUDE.md's rule for the command path is the same rule here: a second copy
/// lets one bot engage where the other would not, and the difference between two
/// arms then includes the gate as well as the controller — which is the one thing
/// an experiment comparing controllers must not measure.
/// <para>
/// ⚠️ <b>A pure move.</b> Every line below came out of <c>PolicyBot</c> unchanged,
/// because <c>PolicyBot</c> is phase 4's shipped arm and the experiment this was
/// extracted for uses it as the comparability anchor. If its behaviour moved by a
/// hair, that anchor is gone and nobody would see it. <c>ScenarioSuiteTests</c>,
/// <c>PolicyMatchTests</c> and the pinned baselines are what say it did not.
/// </para>
/// </remarks>
internal static class Handover
{
    /// <summary>
    /// Distance to the nearest live enemy, or infinity when there is none.
    /// </summary>
    /// <remarks>
    /// ⚠️ Infinity rather than a large number, so "nobody to fight" fails the
    /// range test rather than passing it on a map wider than the constant somebody
    /// picked.
    /// </remarks>
    internal static double DistanceToNearestEnemy(in WorldView world, int slot)
    {
        Span<int> found = stackalloc int[1];

        if (world.NearestEnemies(slot, found) == 0)
            return double.PositiveInfinity;

        ref readonly var self = ref world.Character(slot);
        ref readonly var enemy = ref world.Character(found[0]);

        // ⚠️ Raw Math.Sqrt, NOT Gml.PointDistance, because this is a verbatim move
        // and the two are not obviously the same function. The first draft of this
        // file "tidied" it to the Gml helper while claiming to be a pure move -
        // which is how a comparability anchor moves by a hair with nobody seeing.
        var dx = enemy.X - self.X;
        var dy = enemy.Y - self.Y;

        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    /// <summary>
    /// Whether the micro-controller should hold the wheel this decision.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b><see cref="HandoverRule.MacroTask"/> is a CONJUNCTION, and the range
    /// half is not optional.</b> Gating on the scripted engage hold ALONE was
    /// tried and is wrong: that hold fires out to <c>BotTargeting.ClassRange</c>,
    /// which is 800px for a soldier and 900 for a sniper — three times this range.
    /// It took the policy's share of control from 27% to 71% and handed it the
    /// wheel at distances its observation cannot describe. Captures fell to 3 a
    /// hundred matches.
    /// <para>
    /// `gg2-rl` predicted that failure exactly: "your control-share number is the
    /// mechanism dial. A goal-gated handover should cut it sharply. If it does
    /// not move, the gate is not doing what we think." It did not move down; it
    /// more than doubled.
    /// </para>
    /// </remarks>
    internal static bool ShouldEngage(
        in WorldView world, int slot, double range, HandoverRule rule, bool wantsToEngage)
    {
        var inRange = DistanceToNearestEnemy(in world, slot) <= range;

        return rule switch
        {
            HandoverRule.MacroTask => inRange && wantsToEngage,
            _ => inRange,
        };
    }
}
