namespace Gg2.Env;

/// <summary>
/// N duels stepped together.
/// </summary>
/// <remarks>
/// <b><c>docs/ENV.md</c> step 4.</b> Throughput comes from stepping many worlds
/// per round trip, not from making one world faster — a decision step is
/// microseconds of simulation and the cost that matters is the boundary.
/// <para>
/// ⚠️ <b>Each world owns everything it touches</b> — its own
/// <see cref="DuelEnv"/>, its own <c>CollisionWorld</c>, its own RNG stream and
/// its own decoder. That is what makes the parallelism safe without a lock, and
/// it is the same shape <c>MatchRunner</c> uses.
/// </para>
/// <para>
/// ⚠️ <b>Results must not depend on the batch size or the thread count</b>, and
/// that is gated by comparing whole arrays rather than a summary. A batch that
/// produced the right multiset in the wrong order would pass a statistic and
/// fail the thing a trainer actually relies on.
/// </para>
/// </remarks>
public sealed class EnvBatch
{
    private readonly DuelEnv[] _worlds;
    private readonly int[] _episodes;
    private readonly DuelSpec _spec;
    private readonly uint _seed;

    /// <summary>
    /// ⚠️ <b>Not <c>Environment.ProcessorCount</c> by default.</b> A default
    /// that varied with the machine would make "the same however many threads"
    /// a claim nobody was testing on their own box. One thread unless asked.
    /// </summary>
    private readonly int _threads;

    public EnvBatch(
        int count,
        DuelSpec spec,
        uint seed,
        string mapDirectory,
        string? navDirectory = null,
        int threads = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(threads, 1);

        _spec = spec;
        _seed = seed;
        _threads = threads;
        _worlds = new DuelEnv[count];
        _episodes = new int[count];

        for (var i = 0; i < count; i++)
            _worlds[i] = new DuelEnv(mapDirectory, navDirectory);

        ResetAll();
    }

    public int Count => _worlds.Length;

    public DuelEnv World(int index) => _worlds[index];

    /// <summary>
    /// World <paramref name="index"/>'s seed, derived from the base and the
    /// index.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The index is MIXED IN, not added.</b> <c>seed + index</c> makes
    /// batch <c>{seed 1, count 100}</c> and batch <c>{seed 2, count 100}</c>
    /// overlap in 99 of their 100 worlds, so two "independent" runs are almost
    /// the same experiment. <c>MatchRunner</c> already learned this; the odd
    /// multiplier keeps them apart while staying a pure function of the pair.
    /// <para>
    /// Being a pure function of (base, index) is what makes batch size unable to
    /// move any world's stream — world 3 is world 3 whether the batch holds 4 or
    /// 64.
    /// </para>
    /// </remarks>
    public uint SeedFor(int index) => unchecked(_seed + ((uint)index * 2654435761u));

    /// <summary>
    /// The seed for episode <paramref name="episode"/> of world
    /// <paramref name="index"/>.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A pure function of (base seed, world, episode), and that is what
    /// makes it correct.</b> The first draft derived the next episode's seed
    /// from the finished episode's DECISION COUNT — so two consecutive episodes
    /// that happened to last the same number of decisions got the same seed and
    /// the world replayed one trajectory for ever, while looking busy. A batch
    /// training on a single repeated episode is the kind of bug that produces a
    /// falling loss and a useless policy.
    /// </remarks>
    public uint SeedForEpisode(int index, int episode) =>
        unchecked(SeedFor(index) + ((uint)episode * 40503u));

    /// <summary>Resets every world to its first episode.</summary>
    public void ResetAll()
    {
        for (var i = 0; i < _worlds.Length; i++)
        {
            _episodes[i] = 0;
            _worlds[i].Reset(_spec, SeedForEpisode(i, 0));
        }
    }

    /// <summary>
    /// Steps every world once, and auto-resets any that finished.
    /// </summary>
    /// <param name="red">One action per world, for the red agent.</param>
    /// <param name="blue">One action per world, for the blue agent.</param>
    /// <param name="redResults">Filled one per world.</param>
    /// <param name="blueResults">Filled one per world.</param>
    /// <remarks>
    /// ⚠️ <b>A world whose episode ended is reset inside this call</b>, and the
    /// results still carry the terminal components — <c>docs/ENV.md</c>'s
    /// auto-reset. Round-trip economy: without it a trainer would have to notice
    /// `done`, send a `Reset`, and wait, once per episode per world.
    /// <para>
    /// ⚠️ <b>The reset happens AFTER the results are recorded.</b> Resetting
    /// first would hand back the fresh episode's zeros as though the terminal
    /// step earned nothing, which is the transition a policy most needs.
    /// </para>
    /// <para>
    /// ⚠️ <b>Its seed advances</b>, so the next episode in a world is not a
    /// replay of the last. A batch whose worlds replayed one episode for ever
    /// would train on a single trajectory and look like it was learning.
    /// </para>
    /// </remarks>
    public void Step(
        ReadOnlySpan<EnvAction> red,
        ReadOnlySpan<EnvAction> blue,
        Span<StepResult> redResults,
        Span<StepResult> blueResults)
    {
        if (red.Length < _worlds.Length || blue.Length < _worlds.Length)
            throw new ArgumentException($"one action per world is {_worlds.Length}", nameof(red));

        if (redResults.Length < _worlds.Length || blueResults.Length < _worlds.Length)
            throw new ArgumentException($"one result per world is {_worlds.Length}", nameof(redResults));

        if (_threads == 1)
        {
            for (var i = 0; i < _worlds.Length; i++)
                StepOne(i, red[i], blue[i], out redResults[i], out blueResults[i]);

            return;
        }

        // ⚠️ Copied out of the spans because a lambda cannot capture one, and
        // written back by index rather than appended — the same rule
        // MatchRunner records: an appending runner gives the right multiset in
        // the wrong order.
        var redIn = red[.._worlds.Length].ToArray();
        var blueIn = blue[.._worlds.Length].ToArray();
        var redOut = new StepResult[_worlds.Length];
        var blueOut = new StepResult[_worlds.Length];

        Parallel.For(
            0,
            _worlds.Length,
            new ParallelOptions { MaxDegreeOfParallelism = _threads },
            i => StepOne(i, redIn[i], blueIn[i], out redOut[i], out blueOut[i]));

        redOut.CopyTo(redResults);
        blueOut.CopyTo(blueResults);
    }

    private void StepOne(int index, EnvAction red, EnvAction blue, out StepResult r, out StepResult b)
    {
        var world = _worlds[index];

        (r, b) = world.Step(red, blue);

        if (r.Done)
        {
            // ⚠️ Seeded from THIS world's own episode counter, never from a
            // shared one: a shared counter would make the next episode depend on
            // how many OTHER worlds had finished, which is exactly the
            // batch-size dependence the gates forbid.
            _episodes[index]++;
            world.Reset(_spec, SeedForEpisode(index, _episodes[index]));
        }
    }
}
