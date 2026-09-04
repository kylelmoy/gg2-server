using Gg2.Simulation;

namespace Gg2.Bots;

/// <summary>
/// The hybrid handover gate: when a micro-controller takes the wheel from the
/// scripted layer.
/// </summary>
/// <remarks>
/// <b>Extracted from <c>PolicyBot</c> so a second micro-controller could use
/// it, and extracted rather than copied on purpose.</b> CLAUDE.md's rule for the
/// command path is the same rule here: a second copy lets one bot engage where
/// the other would not, and the difference between two arms then includes the
/// gate as well as the controller — which is the one thing an experiment
/// comparing controllers must not measure.
/// <para>
/// <b>A pure move.</b> Every line below came out of <c>PolicyBot</c> unchanged,
/// because that class was phase 4's shipped arm and the experiment this was
/// extracted for uses it as the comparability anchor. <c>ScenarioSuiteTests</c>,
/// <c>PolicyMatchTests</c> and the pinned baselines are what say it did not move.
/// </para>
/// <para>
/// <b><c>PolicyBot</c> and <c>SearchBot</c> were merged into
/// <see cref="HybridBot"/></b>, which is when this stopped being the only shared
/// thing between the arms and became one piece of a gate they both run. The
/// extraction's reason still stands and is now structural: there is one gate, so
/// two arms cannot differ in it.
/// </para>
/// </remarks>
internal static class Handover
{
    /// <summary>
    /// Distance to the nearest live enemy, or infinity when there is none.
    /// </summary>
    /// <remarks>
    /// Infinity rather than a large number, so "nobody to fight" fails the
    /// range test rather than passing it on a map wider than the constant somebody
    /// picked.
    /// </remarks>
    internal static double DistanceToNearestEnemy(
        in WorldView world, int slot, EngageOpponents engage = EngageOpponents.Anyone)
    {
        // Under HumansOnly the NEAREST enemy may be a bot while a human stands
        // just behind it, so this cannot ask for one result and filter it — that
        // would report "no human in range" whenever a bot was closer, which is
        // most of the time on a populated server. NearestEnemies returns the
        // roster in distance order, so the whole span is asked for and walked
        // until the first one that counts.
        var wanted = engage == EngageOpponents.Anyone ? 1 : WorldState.MaxSlots;
        Span<int> found = stackalloc int[wanted];

        var count = world.NearestEnemies(slot, found);

        var enemySlot = -1;

        for (var i = 0; i < count; i++)
        {
            if (engage == EngageOpponents.HumansOnly && world.Player(found[i]).IsBot)
                continue;

            enemySlot = found[i];
            break;
        }

        if (enemySlot < 0)
            return double.PositiveInfinity;

        ref readonly var self = ref world.Character(slot);
        ref readonly var enemy = ref world.Character(enemySlot);

        // Raw Math.Sqrt, NOT Gml.PointDistance — this is a verbatim move and the
        // two are not obviously the same function.
        var dx = enemy.X - self.X;
        var dy = enemy.Y - self.Y;

        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    /// <summary>
    /// Whether the micro-controller should hold the wheel this decision.
    /// </summary>
    /// <remarks>
    /// <b><see cref="HandoverRule.MacroTask"/> is a CONJUNCTION, and the range
    /// half is not optional.</b> Gating on the scripted engage hold ALONE is
    /// wrong: that hold fires out to <c>BotTargeting.ClassRange</c>, which is
    /// 800px for a soldier and 900 for a sniper — three times this range. It took
    /// the policy's share of control from 27% to 71% and handed it the wheel at
    /// distances its observation cannot describe. Captures fell to 3 a hundred
    /// matches.
    /// </remarks>
    /// <param name="healTarget">
    /// The slot this bot is beaming, or <see cref="BotAlly.None"/>. The raw
    /// slot rather than a bool, so the PREDICATE stays here — the caller supplies
    /// the fact and this file decides what it means, exactly as
    /// <paramref name="wantsToEngage"/> already works.
    /// </param>
    internal static bool ShouldEngage(
        in WorldView world,
        int slot,
        double range,
        HandoverRule rule,
        bool wantsToEngage,
        EngageOpponents engage = EngageOpponents.Anyone,
        int healTarget = BotAlly.None)
    {
        // A medic on the beam is NOT handed over, whatever the range says. The
        // policy owns needle combat and only when the medic is alone with an
        // enemy, with uber deliberately out of its scope.
        //
        // It is a THIRD term and belongs in neither of the two above. It is not
        // about who is nearby (that is `engage`) and not about whether to fight at
        // all (that is `wantsToEngage`) — it is whether this bot, in this state, is
        // the right one to hand over. A medic beaming a patient has a job this
        // tick, and a controller that has never been trained to heal would drive
        // the body away from it.
        //
        // Mirrors BotCombat's `subjectIsAlly` rather than re-deciding it: the
        // ally REPLACES the subject there, which is what makes BotClassKeys press
        // ATTACK (the beam) instead of SPECIAL (the needles). Two places that
        // should agree about one decision must not disagree.
        if (BotClassProfile.Heals(world.Character(slot).Class)
            && BotAlly.StillValid(in world, slot, healTarget))
        {
            return false;
        }

        // Only the RANGE half is filtered by `engage`. `wantsToEngage` is the
        // scripted layer's own engage hold and is deliberately left alone: it
        // decides whether the bot stops to fight at all, which must not change
        // with who it is fighting, or a HumansOnly server would have bots ignore
        // each other entirely rather than merely fight each other with the
        // cheaper brain.
        var inRange = DistanceToNearestEnemy(in world, slot, engage) <= range;

        return rule switch
        {
            HandoverRule.MacroTask => inRange && wantsToEngage,
            _ => inRange,
        };
    }
}
