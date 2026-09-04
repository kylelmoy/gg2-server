using Gg2.Bots;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Bench;

/// <summary>
/// Does dual King of the Hill decide at ANY limit?
/// </summary>
/// <remarks>
/// If these two shipped modes never produce a decided match, that is a
/// gg2-server finding independent of any policy — the same shape as an arena
/// round ending a tick after starting and reporting a clean win. All-scripted
/// on both sides, so nothing here is about the policy.
/// </remarks>
public static class DkothProbe
{
    public static void Run(string maps, string nav)
    {
        foreach (var map in new[] { "dkoth_atalia", "dkoth_sixties", "koth_harvest" })
        {
            foreach (var minutes in new[] { 5, 10, 20, 40 })
            {
                var decided = 0;
                var bestHold = 0;

                for (var i = 0; i < 20; i++)
                {
                    var options = HeadlessMatchOptions.Default with
                    {
                        Map = map,
                        Bots = 8,
                        Seed = (uint)(500 + i),
                        CapLimit = 1,
                        TimeLimitMinutes = minutes,
                        KillLimit = 400,
                        MaxTicks = 30 * 60 * minutes,
                    };

                    var match = HeadlessMatch.Load(options, maps, nav);

                    while (match.Outcome == MatchOutcome.Running)
                        match.Step();

                    if (match.Result.Outcome == MatchOutcome.Decided)
                        decided++;

                    // How far either team got toward the hold: the timers count
                    // DOWN from KothHoldTicks.
                    var held = Rounds.KothHoldTicks
                        - Math.Min(match.State.RedTimer, match.State.BlueTimer);

                    bestHold = Math.Max(bestHold, held);
                }

                Console.WriteLine(
                    $"  {map,-16} limit {minutes,2}min: decided {decided,2}/20"
                    + $"   best hold reached {bestHold / 30.0,6:0.0}s of the {Rounds.KothHoldTicks / 30}s needed");
            }

            Console.WriteLine();
        }
    }
}
