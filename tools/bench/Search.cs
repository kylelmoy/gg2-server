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
/// <c>candidates x (fork + horizon x tick)</c> — the per-candidate term does NOT
/// scale with horizon, so pruning frees more than a naive
/// candidates-times-horizon account suggests. A horizon sweep separates the two:
/// the slope is candidates x tick, the intercept is candidates x everything else.
/// <para>
/// ⛔ <b>The column this prints as "fork" is the INTERCEPT, not the state copy,
/// and the two differ by an order of magnitude.</b> It read 27.1 us a candidate at
/// population 12 while the <c>WorldState</c> assignment itself measures
/// <b>2.2 us</b> — the rest is the zoom check, the decoder, the aim-error scan and
/// the per-candidate bookkeeping. This file said "the fork is a 145 KB WorldState
/// copy per candidate" and a reader would have gone looking for 25 us of memcpy
/// that is not there. <b>Shrinking <c>WorldState</c> buys about 3% of a
/// candidate, not 25%.</b> Measured 2026-09-02; see <c>docs/ARCHITECTURE.md</c>,
/// "Where a tick actually goes".
/// </para>
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

        var pop = int.TryParse(Environment.GetEnvironmentVariable("BENCH_POP"), out var pv) && pv > 2 ? pv : 2;
        Console.WriteLine($"map={map}  population={pop}  RolloutSearch decision cost by horizon");

        foreach (var horizon in horizons)
        {
            var world = ScenarioWorld.Load(map, 1, maps, nav);
            var cls = Environment.GetEnvironmentVariable("BENCH_CLASS") == "sniper" ? PlayerClass.Sniper : PlayerClass.Soldier;
            var red = world.Place(cls, Team.Red, 600, 850);
            var blue = world.Place(PlayerClass.Soldier, Team.Blue, 760, 850);

            // ⚠️ BENCH_POP: how many characters are ALIVE in the world, total.
            // A rollout re-simulates every one of them for every tick of every
            // candidate, so a decision measured in a 2-character duel is not the
            // decision a 12-bot match pays for. This is the term a duel number
            // silently omits, and the reason a per-decision cost does not
            // transfer between them. Default 2 keeps the old readings comparable.
            var population = int.TryParse(Environment.GetEnvironmentVariable("BENCH_POP"), out var p) && p > 2 ? p : 2;

            for (var extra = 2; extra < population; extra++)
            {
                world.Place(
                    PlayerClass.Soldier,
                    extra % 2 == 0 ? Team.Red : Team.Blue,
                    500 + (extra * 37),
                    850);
            }

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
        // ⛔ "intercept", not "fork". It was labelled fork and it is not the state
        // copy — that measures 2.2 us where this reads 27. See this class's remarks.
        Console.WriteLine(
            $"  per candidate: intercept {intercept / candidates:0.000} us "
            + $"(NOT the state copy — see SearchBench remarks), tick {slope / candidates:0.000} us");
        Console.WriteLine();

        // What horizon does k candidates buy at the cost of 60 at horizon 20?
        var budget = intercept + (slope * 20);

        Console.WriteLine($"  budget of {candidates:0} candidates at horizon 20 = {budget:0.0} us");

        foreach (var k in new[] { 8, 12, 16, 20, 24, 32, 43 })
        {
            var forkK = intercept / candidates * k;
            var tickK = slope / candidates * k;
            var affordable = (budget - forkK) / tickK;

            // ⚠️ MEASURED, not estimated: BENCH_POLICYCOST=1 on 2026-09-01, Release,
            // reads inference 3.67 us and observation extraction 2.92 us — 6.58 us
            // per policy decision. This line held 4.7 us, which was an estimate and
            // was 1.4x LOW, and it drives the (affordable)/(OVER) verdicts below.
            //
            // ⚠️ The cadence is what makes a continuation affordable, so it is a
            // parameter rather than a constant. At cadence 1 the continuation costs
            // 6.58 us EVERY tick and k=20 is over budget; at the search's own
            // DecisionTicks of 4 it is amortised to 1.65 us and k=20 fits twice over.
            // Re-measure before quoting: this is a number, and it is older than the
            // next change to ObservationWriter or PolicyNetwork.
            const double policyDecisionUs = 6.58;
            var cadence = double.TryParse(
                Environment.GetEnvironmentVariable("BENCH_CADENCE"), out var c) && c >= 1 ? c : 4;

            var withCont = (intercept / candidates)
                + (20 * ((slope / candidates) + (policyDecisionUs / cadence)));

            Console.WriteLine(
                $"    keep {k,2}: horizon {affordable,5:0} at equal cost, or a CONTINUATION at horizon 20 "
                + $"for {k * withCont / 1000.0:0.00} ms {(k * withCont <= budget ? "(affordable)" : "(OVER)")}");
        }
    }
}
