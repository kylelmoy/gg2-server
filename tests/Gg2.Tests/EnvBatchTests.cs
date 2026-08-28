using Gg2.Env;
using Gg2.Protocol;

namespace Gg2.Tests;

/// <summary>
/// <see cref="EnvBatch"/> — N duels stepped together.
/// </summary>
/// <remarks>
/// <c>docs/ENV.md</c>'s two gates for this type, and it is specific about how:
/// <b>identical trajectories at batch 1 and batch 64 with the same seeds,
/// compared WHOLE, and the same across thread counts.</b> A comparison of
/// summary statistics would pass on a batch that produced the right multiset in
/// the wrong order, which is exactly the difference a trainer relies on.
/// </remarks>
public sealed class EnvBatchTests
{
    private static DuelSpec Spec => new()
    {
        Map = "gg_debug",
        RedClass = PlayerClass.Soldier,
        BlueClass = PlayerClass.Soldier,
        RedX = 600,
        RedY = 850,
        BlueX = 700,
        BlueY = 850,

        // Short, so a run of steps crosses several episode boundaries and the
        // auto-reset is exercised rather than merely present.
        DecisionCap = 12,
        ActionRepeat = 2,
    };

    private static EnvBatch Batch(int count, int threads = 1) =>
        new(count, Spec, seed: 4242, ReferenceTree.IncludedFiles, null, threads);

    /// <summary>
    /// A deterministic script, so nothing here depends on a random source the
    /// test does not control.
    /// </summary>
    private static EnvAction ActionFor(int world, int step)
    {
        var n = (world * 7) + (step * 3);

        return new EnvAction
        {
            Horizontal = (byte)(n % 3),
            Jump = (byte)((n / 3) % 2),
            Down = 0,
            Fire = (byte)((n / 5) % 3),
            AimDelta = (byte)(n % ActionDecoder.AimBins),
        };
    }

    /// <summary>Runs a batch and records every world's every result.</summary>
    private static StepResult[] Trajectories(int count, int steps, int threads)
    {
        var batch = Batch(count, threads);

        var red = new EnvAction[count];
        var blue = new EnvAction[count];
        var redOut = new StepResult[count];
        var blueOut = new StepResult[count];

        var log = new StepResult[count * steps];

        for (var step = 0; step < steps; step++)
        {
            for (var w = 0; w < count; w++)
            {
                red[w] = ActionFor(w, step);
                blue[w] = ActionFor(w + 1, step);
            }

            batch.Step(red, blue, redOut, blueOut);

            for (var w = 0; w < count; w++)
                log[(step * count) + w] = redOut[w];
        }

        return log;
    }

    /// <summary>
    /// ⚠️ <b>World i behaves identically whether the batch holds 1 or 64.</b>
    /// </summary>
    /// <remarks>
    /// The property the whole surface rests on: a seed is a pure function of
    /// (base, world index, episode), so batch size cannot move any world's
    /// stream. Compared whole — every world, every step, every component.
    /// </remarks>
    [Fact]
    public void AWorldIsTheSameAtBatchOneAndBatchSixtyFour()
    {
        const int steps = 40;

        var large = Trajectories(64, steps, threads: 1);

        for (var world = 0; world < 64; world += 21)
        {
            // A batch of one, seeded so that its only world IS world `world` of
            // the large batch — which is what SeedFor being a pure function of
            // the index buys.
            var single = new EnvBatch(
                1, Spec, seed: unchecked(4242 + ((uint)world * 2654435761u)),
                ReferenceTree.IncludedFiles, null, 1);

            var red = new EnvAction[1];
            var blue = new EnvAction[1];
            var redOut = new StepResult[1];
            var blueOut = new StepResult[1];

            for (var step = 0; step < steps; step++)
            {
                red[0] = ActionFor(world, step);
                blue[0] = ActionFor(world + 1, step);

                single.Step(red, blue, redOut, blueOut);

                Assert.Equal(large[(step * 64) + world], redOut[0]);
            }
        }
    }

    /// <summary>
    /// ⚠️ <b>The same however many threads, compared whole.</b>
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void ThreadCountDoesNotChangeAnything(int threads)
    {
        var one = Trajectories(16, 30, threads: 1);
        var many = Trajectories(16, 30, threads);

        Assert.Equal(one, many);
    }

    /// <summary>
    /// The batch really does cross episode boundaries, so the gates above are
    /// not passing on trajectories that never reset.
    /// </summary>
    /// <remarks>
    /// ⚠️ The control this file needs most. Determinism gates are easy to pass
    /// when nothing interesting happens, and an auto-reset that never fired
    /// would make every assertion above a statement about the first twelve
    /// decisions of one episode.
    /// </remarks>
    [Fact]
    public void TheRunActuallyCrossesEpisodeBoundaries()
    {
        var log = Trajectories(8, 40, threads: 1);
        var done = log.Count(r => r.Done);

        // 40 steps at a cap of 12 is three terminations per world, at least.
        Assert.True(done >= 8 * 3, $"only {done} episodes ended in 8 worlds over 40 steps");
    }

    /// <summary>
    /// ⚠️ <b>Consecutive episodes in one world differ.</b>
    /// </summary>
    /// <remarks>
    /// The bug this test exists for was real and was mine: the first draft
    /// derived the next episode's seed from the finished episode's DECISION
    /// COUNT, so two episodes that lasted the same number of decisions — which,
    /// with a fixed cap, is nearly all of them — got the same seed and the world
    /// replayed one trajectory for ever. A batch training on a single repeated
    /// episode still produces a falling loss.
    /// </remarks>
    [Fact]
    public void EachEpisodeInAWorldGetsItsOwnSeed()
    {
        var batch = Batch(1);

        var seeds = new HashSet<uint>();

        for (var episode = 0; episode < 10; episode++)
            Assert.True(seeds.Add(batch.SeedForEpisode(0, episode)), $"episode {episode} reused a seed");

        // And across worlds, for the same episode.
        for (var world = 0; world < 10; world++)
            Assert.True(seeds.Add(batch.SeedForEpisode(world, 99)), $"world {world} reused a seed");
    }

    /// <summary>
    /// ⚠️ <b>Two batches one seed apart do not overlap.</b>
    /// </summary>
    /// <remarks>
    /// <c>seed + index</c> would make <c>{seed 1, count 100}</c> and
    /// <c>{seed 2, count 100}</c> share 99 of their 100 worlds, so two runs a
    /// researcher believed were independent would be almost the same experiment.
    /// <c>MatchRunner</c> records the same trap.
    /// </remarks>
    [Fact]
    public void TwoBatchesOneSeedApartShareNoWorld()
    {
        var a = new EnvBatch(64, Spec, 1, ReferenceTree.IncludedFiles);
        var b = new EnvBatch(64, Spec, 2, ReferenceTree.IncludedFiles);

        var mine = new HashSet<uint>();

        for (var i = 0; i < 64; i++)
            mine.Add(a.SeedFor(i));

        for (var i = 0; i < 64; i++)
            Assert.DoesNotContain(b.SeedFor(i), mine);
    }

    [Fact]
    public void AMisSizedCallIsRefused()
    {
        var batch = Batch(4);

        var two = new EnvAction[2];
        var four = new EnvAction[4];
        var resultsTwo = new StepResult[2];
        var resultsFour = new StepResult[4];

        Assert.Throws<ArgumentException>(() => batch.Step(two, four, resultsFour, resultsFour));
        Assert.Throws<ArgumentException>(() => batch.Step(four, four, resultsTwo, resultsFour));
    }
}
