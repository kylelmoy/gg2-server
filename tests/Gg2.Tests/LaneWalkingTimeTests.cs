using Gg2.Bots;
using Gg2.Protocol;
using Xunit.Abstractions;

namespace Gg2.Tests;

/// <summary>
/// What a lane costs a bot in walking time — the half
/// <see cref="RouteDiversity"/> is blind to.
/// </summary>
/// <remarks>
/// <b>Summed walking time over the scenario suite's legs is the currency this
/// project tunes routing in.</b> <c>NavConstants.SearchWeight</c> was chosen on
/// exactly this quantity — 6,259 ticks at weight 1.0 against 6,142 at 1.3, a
/// ±2% band with no direction, and 1.5 rejected at 6,416 — so a lane's cost
/// belongs in the same units rather than in the route-cost proxy the dispersion
/// files use.
/// <para>
/// ⛔ <b>Arrival count first, ticks second.</b> A lane that makes a leg fail
/// outright is not a slower lane, it is a broken one, and a sum of ticks over
/// surviving legs would hide it — the shorter the survivor set, the better the
/// sum looks. The arrivals column is printed per lane for that reason and the
/// tick sum is taken only over legs every lane finished.
/// </para>
/// <para>
/// ⚠️ <b>These are single-bot legs, so lane 0 is the shipped behaviour
/// exactly.</b> That is the control: if lane 0's column ever differs from the
/// numbers <c>ScenarioSuiteTests.Baseline</c> pins, the lane plumbing has
/// changed routing for bots that were given no lane, which is the one thing it
/// promised not to do.
/// </para>
/// </remarks>
public sealed class LaneWalkingTimeTests(ITestOutputHelper output)
{
    private const int Lanes = 4;

    [Fact]
    public void WhatALaneCostsInWalkingTime()
    {
        ReferenceTree.RequireNavCache();

        double[] penalties = [1.20, 1.35, 1.75];

        foreach (var penalty in penalties)
        {
            var ticks = new long[Lanes];
            var arrived = new int[Lanes];
            var counted = 0;

            foreach (var scenario in Scenarios.All)
            {
                if (scenario.Mode != ScenarioMode.Travel)
                    continue;

                var legTicks = new int[Lanes];
                var legArrived = new bool[Lanes];

                for (var lane = 0; lane < Lanes; lane++)
                {
                    var world = ScenarioWorld.Load(
                        scenario.Map, scenario.Area, ReferenceTree.IncludedFiles, ReferenceTree.NavCacheDirectory);

                    var bot = new ScriptedBot(
                        world.Nav, new NavQuery(scenario.Team), BotSkill.ForTier(5), stagger: 1);

                    if (world.Nav is { } nav)
                    {
                        bot.Lanes = BotLanes.Derive(nav, Lanes, penalty);
                        bot.Lane = lane;
                    }

                    var result = ScenarioRunner.Run(scenario, world, bot);

                    legTicks[lane] = result.Ticks;
                    legArrived[lane] = result.Arrived;

                    if (result.Arrived)
                        arrived[lane]++;
                }

                // Only legs every lane finished contribute to the sum, so a
                // lane cannot look fast by failing.
                var all = true;
                foreach (var ok in legArrived)
                    all &= ok;

                if (!all)
                    continue;

                counted++;
                for (var lane = 0; lane < Lanes; lane++)
                    ticks[lane] += legTicks[lane];
            }

            var baseline = ticks[0] == 0 ? 1 : (double)ticks[0];

            output.WriteLine($"penalty {penalty:F2}, {counted} legs finished by every lane");

            for (var lane = 0; lane < Lanes; lane++)
            {
                output.WriteLine(
                    $"  lane {lane}: {ticks[lane],6} ticks  {ticks[lane] / baseline,7:P1}  "
                    + $"arrived {arrived[lane]}");
            }

            long total = 0;
            foreach (var t in ticks)
                total += t;

            output.WriteLine($"  mean over lanes: {total / (Lanes * baseline),7:P1} of lane 0");
            output.WriteLine(string.Empty);
        }
    }
}
