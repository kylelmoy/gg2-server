using GG2.Bots;

namespace GG2.Tests;

/// <summary>
/// N13 flight by flight: the shipping sweep with the re-plan timer free to fire
/// in the air and held for the ground, paired by edge.
/// </summary>
/// <remarks>
/// A measurement, not a gate; skipped in the suite. ⚠️ The sweep places a bot a few pixels above some
/// nodes, so a held timer can plan from the node it lands on rather than the
/// one it was placed over: those flights are counted apart, as a change of
/// what the sweep measures and not of how the bot flies.
/// </remarks>
public sealed class GroundedReplanCensus(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact(Skip = "a census: run by hand")]
    public void Census()
    {
        ReferenceTree.RequireNavCache();

        List<EdgeFlight> Sweep(bool grounded, bool goal = false) => EdgeFlights.SweepAll(
            ReferenceTree.MapsDirectory, ReferenceTree.NavCacheDirectory, distances: true,
            threads: Math.Max(1, Environment.ProcessorCount - 2), groundedReplans: grounded, goalAirHolds: goal);

        var before = Sweep(false);
        var after = Environment.GetEnvironmentVariable("N13B") == "1" ? Sweep(false, goal: true) : Sweep(true);
        output.WriteLine("before " + EdgeFlights.Summarise(before));
        output.WriteLine("after  " + EdgeFlights.Summarise(after));

        var pairs = new SortedDictionary<string, int>(StringComparer.Ordinal);

        for (var i = 0; i < before.Count; i++)
        {
            if (!before[i].Measured && !after[i].Measured)
                continue;

            string Of(EdgeFlight f) => f.Outcome == EdgeFlightOutcome.Arrived ? (f.Diverted ? "diverted" : "arrived") : f.Outcome.ToString();
            var key = $"{Of(before[i]),-10} -> {Of(after[i]),-10} {(before[i].Type is NavEdgeType.RocketJump or NavEdgeType.StickyJump ? "weapon" : "plain")}";
            pairs[key] = pairs.GetValueOrDefault(key) + 1;
        }

        foreach (var (key, count) in pairs)
            output.WriteLine($"{key}: {count}");
    }
}
