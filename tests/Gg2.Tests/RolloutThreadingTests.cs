using Gg2.Bots;
using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// <see cref="RolloutOptions.Threads"/> is a COST knob and changes no decision.
/// </summary>
/// <remarks>
/// ⚠️ <b>This is the gate the whole threading change rests on</b>, and it compares
/// the chosen ACTION and not only the score: the candidate loop's tie-break is
/// lexicographic, 85.2% of non-blind decisions have their top two tied, and a
/// parallel loop that reduced as it went would break exactly those and no others.
/// A score-only comparison would pass while every tied decision flipped.
/// <para>
/// ⚠️ <b>Identity, not similarity.</b> There is no tolerance here on purpose —
/// each candidate is simulated from the same baseline with the same inputs, so
/// the two paths are the same arithmetic in a different order of execution, never
/// a different order of arithmetic.
/// </para>
/// <para>
/// ⚠️ <b>The run deliberately starts inside the spawn cooldown</b>, where no
/// candidate can fire and every score is zero. Those are the decisions the aim
/// tie-break alone settles, so they are the ones a broken reduction would flip
/// first — skipping past them would test the easy half.
/// </para>
/// </remarks>
public sealed class RolloutThreadingTests
{
    private const string Map = "gg_debug";

    private const double RedX = 600;

    private const double BlueX = 700;

    private const double StandY = 850;

    private sealed record Decision(EnvAction Action, double Best, double[] Scores);

    /// <summary>
    /// A duel with a searcher on each side, recording every decision red took.
    /// </summary>
    /// <remarks>
    /// Blue is serial in both runs, so anything that differs is red's doing —
    /// but blue reacts to red, so a flipped decision propagates into the world
    /// rather than staying inside one comparison.
    /// </remarks>
    private static List<Decision> Run(int threads, int ticks = 150)
    {
        var world = ScenarioWorld.Load(Map, 1, ReferenceTree.IncludedFiles, null);
        var red = new RolloutSearch(new RolloutOptions(Threads: threads));
        var blue = new RolloutSearch();

        world.Drive(world.Place(PlayerClass.Soldier, Team.Red, RedX, StandY), red);
        world.Drive(world.Place(PlayerClass.Soldier, Team.Blue, BlueX, StandY), blue);

        var decisions = new List<Decision>();

        for (var tick = 0; tick < ticks; tick++)
        {
            world.Step();

            // A tick that reused the previous decision cost nothing and has
            // nothing new to record.
            if (red.LastDecisionTicksSimulated > 0)
                decisions.Add(new Decision(red.LastAction, red.LastBestScore, red.LastScores.ToArray()));
        }

        return decisions;
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void AThreadedSearchDecidesExactlyWhatASerialOneDecides(int threads)
    {
        var serial = Run(1);
        var parallel = Run(threads);

        Assert.Equal(serial.Count, parallel.Count);

        for (var i = 0; i < serial.Count; i++)
        {
            Assert.Equal(serial[i].Action, parallel[i].Action);
            Assert.Equal(serial[i].Best, parallel[i].Best);
            Assert.Equal(serial[i].Scores, parallel[i].Scores);
        }
    }

    /// <summary>
    /// ⚠️ The gate above is worth nothing if the run it compares never searched
    /// anything interesting — a fixture where every decision is the same action
    /// would agree trivially, whatever the reduction did.
    /// </summary>
    [Fact]
    public void TheEquivalenceRunReallySearches()
    {
        var serial = Run(1);

        Assert.True(serial.Count > 8, $"only {serial.Count} decisions");
        Assert.True(serial.Select(d => d.Action).Distinct().Count() > 1, "every decision was identical");

        // And it is not all cooldown: at least one decision found damage to do.
        Assert.Contains(serial, d => d.Best != 0);

        // And at least one was a genuine tie the aim error had to settle, which
        // is the case the reduction order actually decides.
        Assert.Contains(serial, d => d.Scores.Count(s => s == d.Best) > 1);
    }

    /// <summary>
    /// ⛔ A threaded search with a policy continuation is refused, because
    /// <see cref="PolicyNetwork"/> forwards through shared scratch.
    /// </summary>
    [Fact]
    public void ThreadsAndAContinuationPolicyAreRefusedTogether()
    {
        var policy = PolicyFixture.Load();

        var error = Assert.Throws<ArgumentException>(
            () => new RolloutSearch(new RolloutOptions(Threads: 4, ContinuationPolicy: policy)));

        Assert.Contains("re-entrant", error.Message);
    }

    [Fact]
    public void ZeroThreadsMeansSerialAndNegativeIsRefused()
    {
        Assert.Equal(1, new RolloutOptions(Threads: 0).WithDefaults().Threads);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new RolloutOptions(Threads: -1).WithDefaults());
    }
}
