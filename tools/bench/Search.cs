using System.Diagnostics;
using Gg2.Bots;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Bench;

/// <summary>
/// Splits a search decision's cost into the part that is per CANDIDATE and the
/// part that is per TICK, by sweeping the horizon.
/// </summary>
/// <remarks>
/// The question is `gg2-rl`'s: if the policy prunes 60 candidates to k, can the
/// freed budget be spent on depth? Cost is
/// <c>candidates x (fork + horizon x tick)</c> — the fork is a 145 KB
/// <c>WorldState</c> copy per candidate and does NOT scale with horizon, so
/// pruning frees more than a naive candidates-times-horizon account suggests.
/// A horizon sweep separates the two terms: the slope is candidates x tick, the
/// intercept is candidates x fork.
/// </remarks>
public static class SearchBench
{
    public static void Run(string maps, string? nav, string map)
    {
        // ⚠️ DecisionTicks 1 so every Poll really searches — otherwise this measures
        // the search amortised over the ticks it is held for, and the intercept is
        // whatever fraction of polls happened to land on a decision boundary.
        var horizons = new[] { 20, 40, 80, 160, 320 };
        var results = new List<(int Horizon, double Us)>();

        Console.WriteLine($"map={map}  RolloutSearch decision cost by horizon");

        foreach (var horizon in horizons)
        {
            var world = ScenarioWorld.Load(map, 1, maps, nav);
            var cls = Environment.GetEnvironmentVariable("BENCH_CLASS") == "sniper" ? PlayerClass.Sniper : PlayerClass.Soldier;
            var red = world.Place(cls, Team.Red, 600, 850);
            var blue = world.Place(PlayerClass.Soldier, Team.Blue, 760, 850);

            for (var i = 0; i < 20; i++)
                world.Step();

            var search = new RolloutSearch(new RolloutOptions(HorizonTicks: horizon, DecisionTicks: 1));
            var view = new WorldView(in world.State, world.Collision);

            // Warm past tier-1: a search decision is a big method and a short
            // warm-up measures the unoptimised build.
            for (var i = 0; i < 400; i++)
                search.Poll(in view, red, i);

            const int N = 300;
            var sw = Stopwatch.StartNew();

            for (var i = 0; i < N; i++)
                search.Poll(in view, red, 1000 + i);

            sw.Stop();

            // Poll only searches on the first tick of a decision; DecisionTicks
            // defaults above 1, so count the searches rather than the polls.
            var perDecision = sw.Elapsed.TotalMilliseconds * 1000 / N;

            results.Add((horizon, perDecision));

            Console.WriteLine(
                $"  horizon {horizon,4}  {perDecision,8:0.00} us/decision   "
                + $"candidates {search.CandidateCount}  ticks simulated {search.LastDecisionTicksSimulated}");
        }

        // Least squares over (horizon, cost): cost = a + b*horizon.
        double n = results.Count, sx = 0, sy = 0, sxx = 0, sxy = 0;

        foreach (var (h, us) in results)
        {
            sx += h;
            sy += us;
            sxx += (double)h * h;
            sxy += h * us;
        }

        var slope = ((n * sxy) - (sx * sy)) / ((n * sxx) - (sx * sx));
        var intercept = (sy - (slope * sx)) / n;
        var candidates = (double)RolloutSearch.CandidateCountFor(new RolloutOptions(), Environment.GetEnvironmentVariable("BENCH_CLASS") == "sniper" ? PlayerClass.Sniper : PlayerClass.Soldier);

        Console.WriteLine();
        Console.WriteLine($"  fit: cost = {intercept:0.00} us + {slope:0.000} us x horizon");
        Console.WriteLine($"  per candidate: fork {intercept / candidates:0.000} us, tick {slope / candidates:0.000} us");
        Console.WriteLine();

        // What horizon does k candidates buy at the cost of 60 at horizon 20?
        var budget = intercept + (slope * 20);

        Console.WriteLine($"  budget of {candidates:0} candidates at horizon 20 = {budget:0.0} us");

        foreach (var k in new[] { 8, 12, 16, 20, 24, 32, 43 })
        {
            var forkK = intercept / candidates * k;
            var tickK = slope / candidates * k;
            var affordable = (budget - forkK) / tickK;

            var withCont = (intercept / candidates) + (20 * ((slope / candidates) + 4.7));

            Console.WriteLine(
                $"    keep {k,2}: horizon {affordable,5:0} at equal cost, or a CONTINUATION at horizon 20 "
                + $"for {k * withCont / 1000.0:0.00} ms {(k * withCont <= budget ? "(affordable)" : "(OVER)")}");
        }
    }
}
