namespace GG2.Bots;

/// <summary>
/// Runs many <see cref="HeadlessMatch"/>es and gives the same answers however
/// many threads it used — <c>docs/BOTAI.md</c> item 8.
/// </summary>
/// <remarks>
/// <para>
/// <b>"Identical results regardless of how many threads it used" is the clause
/// with teeth</b>, and it is a constraint on what a match may share rather than
/// on this file. Two things make it hold:
/// </para>
/// <list type="bullet">
/// <item>
/// <b>Every match owns everything mutable it touches.</b> A
/// <see cref="HeadlessMatch"/> builds its own <c>WorldState</c>, its own
/// <c>CollisionWorld</c>, its own <c>ScriptedBot</c>s and — through them — its
/// own <c>PathFollower</c> and its own <c>NavSearch</c>. <b><c>NavSearch</c> is
/// deliberately not thread-safe</b>: its scratch is reused across queries, so
/// two matches sharing one would interleave two frontiers into one
/// <c>gScore</c> and produce routes that are <em>wrong rather than slow</em>.
/// That is the failure this clause exists to forbid, and it would be invisible
/// — a slightly worse bot, in a subset of runs, for no discoverable reason.
/// </item>
/// <item>
/// <b>A match's seed is derived from its index, not from the order it ran in.</b>
/// So match <c>i</c> is the same match whether it ran first, last or
/// concurrently, and the results are written to a fixed slot rather than
/// appended.
/// </item>
/// </list>
/// <para>
/// <b>The one thing matches DO share is the loaded map</b>, and it is safe
/// only because it is immutable after load: <c>LevelMap</c> and its walkmask,
/// and the <c>NavGraph</c>, are read-only. The <c>CollisionWorld</c> is
/// <em>not</em> shared, because <c>SetCaptureZones</c> writes to it.
/// </para>
/// </remarks>
public static class MatchRunner
{
    /// <summary>
    /// Runs <paramref name="count"/> matches, seeded from
    /// <paramref name="options"/>'s seed and their own index.
    /// </summary>
    /// <param name="threads">
    /// How many at once. 1 is sequential. The answers must not depend on
    /// this — <see cref="Run"/> is what a test varies to prove it.
    /// </param>
    public static MatchResult[] Run(
        HeadlessMatchOptions options,
        int count,
        string mapDirectory,
        string? navDirectory,
        int threads = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        // Loaded ONCE for the batch. A LevelMap and a NavGraph are immutable
        // after load, so sharing them saves a PNG decode and a cache read per
        // match; the CollisionWorld is not shared, because SetCaptureZones
        // writes to it.
        var png = File.ReadAllBytes(Path.Combine(mapDirectory, options.Map + ".png"));
        var map = GG2.Maps.LevelMap.FromPng(options.Map, png).ForArea(options.Area);

        NavGraph? nav = null;
        if (navDirectory is { Length: > 0 }
            && NavCache.TryLoad(navDirectory, options.Map, options.Area, out var loaded))
        {
            nav = loaded;
        }

        return Run(options, count, map, nav, threads, navDirectory);
    }

    /// <summary>Runs a batch on a map that is already loaded.</summary>
    /// <param name="navDirectory">
    /// Where to look for the graph's <c>.dist.gz</c> tables. Null derives them
    /// instead, which is the same answer at a higher price.
    /// </param>
    public static MatchResult[] Run(
        HeadlessMatchOptions options,
        int count,
        GG2.Maps.LevelMap map,
        NavGraph? nav,
        int threads = 1,
        string? navDirectory = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(threads);

        var results = new MatchResult[count];

        // ⛔ Derived ONCE for the batch, not once a match. Every match here plays
        // the same graph, so a per-match derivation is the identical table
        // computed `count` times — a few hundred ms each against a match that
        // costs about 0.6 s, which would spend more than the heuristic saves and
        // make the exact heuristic look like a regression. It is read-only once
        // built, so the Parallel.For below shares it safely.
        //
        // ⚠️ LOADED from <key>.dist.gz when there is one — an `eval` over 22 maps
        // derived 100 tables and paid ~2.2 s of pure setup for it.
        var distances = options.NavDistances && nav is not null
            ? NavDistanceSet.LoadOrDerive(navDirectory, options.Map, options.Area, nav, out _)
            : null;

        if (threads == 1)
        {
            for (var i = 0; i < count; i++)
                results[i] = RunOne(options, i, map, nav, distances);

            return results;
        }

        // Written to results[i], never appended. An appending runner gives
        // the right multiset in the wrong order, which is exactly the kind of
        // difference that makes "the same however many threads" true of a
        // summary statistic and false of the data behind it.
        Parallel.For(
            0,
            count,
            new ParallelOptions { MaxDegreeOfParallelism = threads },
            i => results[i] = RunOne(options, i, map, nav, distances));

        return results;
    }

    /// <summary>
    /// Builds and plays match <paramref name="index"/> of a batch.
    /// </summary>
    /// <remarks>
    /// <b>The per-match seed mixes the index in rather than adding it.</b>
    /// <c>seed + index</c> makes batch <c>{seed: 1, count: 100}</c> and batch
    /// <c>{seed: 2, count: 100}</c> overlap in 99 of their 100 matches, so two
    /// "independent" batches are almost the same experiment. Multiplying by an
    /// odd constant keeps them apart while staying a pure function of the pair.
    /// </remarks>
    public static MatchResult RunOne(
        HeadlessMatchOptions options,
        int index,
        GG2.Maps.LevelMap map,
        NavGraph? nav,
        NavDistanceSet? distances = null)
    {
        var seeded = options with { Seed = unchecked(options.Seed + ((uint)index * 2654435761u)) };
        return HeadlessMatch.On(seeded, map, nav, distances).Run();
    }

    /// <summary>
    /// Win rate over a batch, which is what a bot change is measured by.
    /// </summary>
    /// <remarks>
    /// <b>Draws and exhausted matches are reported, not folded away.</b> A
    /// change that makes bots worse at capturing shows up first as more
    /// exhausted matches, and a summary that only counted red-versus-blue would
    /// hide it — the win rate would drift for a reason nobody could see.
    /// </remarks>
    public static MatchSummary Summarise(ReadOnlySpan<MatchResult> results)
    {
        var summary = default(MatchSummary);

        foreach (var result in results)
        {
            if (result.Outcome == MatchOutcome.Exhausted)
                summary = summary with { Exhausted = summary.Exhausted + 1 };
            else if (result.Winner == GG2.Protocol.Team.Red)
                summary = summary with { RedWins = summary.RedWins + 1 };
            else if (result.Winner == GG2.Protocol.Team.Blue)
                summary = summary with { BlueWins = summary.BlueWins + 1 };
            else
                summary = summary with { Draws = summary.Draws + 1 };

            summary = summary with { TotalTicks = summary.TotalTicks + result.Ticks };
        }

        return summary with { Matches = results.Length };
    }
}
