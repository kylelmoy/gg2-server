using Gg2.Bots;
using Gg2.Maps;
using Gg2.Nav;
using Gg2.Protocol;
using Xunit;

namespace Gg2.Tests;

/// <summary>
/// <see cref="BatchDigest"/> exists to answer one question — "did these two
/// runs play the same matches?" — so what it has to be is sensitive, in every
/// direction a run can differ.
/// </summary>
/// <remarks>
/// A digest that collided on two different batches would read as agreement,
/// which is the failure that matters: it is quoted as evidence of determinism
/// across machines, and evidence that cannot come back negative is not
/// evidence. Each test below is one way a batch can differ.
/// </remarks>
public sealed class BatchDigestTests
{
    private static MatchResult Result(ulong digest, int redCaps = 3) =>
        new(MatchOutcome.Decided, Team.Red, redCaps, 1, 4321, digest);

    [Fact]
    public void TheSameBatchDigestsTheSame()
    {
        MatchResult[] one = [Result(11), Result(22)];
        MatchResult[] two = [Result(11), Result(22)];

        Assert.Equal(BatchDigest.Of("ctf_2fort", one), BatchDigest.Of("ctf_2fort", two));
    }

    /// <summary>
    /// The case the whole instrument is for: identical scoreboards, different
    /// worlds. <c>MatchResult.Digest</c> is where "where everybody stood" lives,
    /// so a fold that dropped it would compare equal exactly when a physics
    /// divergence had gone unnoticed.
    /// </summary>
    [Fact]
    public void ADifferentWorldWithTheSameScoreboardIsADifferentBatch()
    {
        MatchResult[] one = [Result(11)];
        MatchResult[] two = [Result(12)];

        Assert.NotEqual(BatchDigest.Of("ctf_2fort", one), BatchDigest.Of("ctf_2fort", two));
    }

    [Fact]
    public void ADifferentScoreboardIsADifferentBatch()
    {
        MatchResult[] one = [Result(11, redCaps: 3)];
        MatchResult[] two = [Result(11, redCaps: 4)];

        Assert.NotEqual(BatchDigest.Of("ctf_2fort", one), BatchDigest.Of("ctf_2fort", two));
    }

    [Fact]
    public void TheMapNameIsPartOfIt()
    {
        MatchResult[] results = [Result(11)];

        Assert.NotEqual(
            BatchDigest.Of("ctf_2fort", results), BatchDigest.Of("ctf_orange", results));
    }

    /// <summary>
    /// Order matters because a batch is indexed: match 2 is a different match
    /// from match 1, and two runs that played them in different slots played
    /// different matches.
    /// </summary>
    [Fact]
    public void TheOrderOfTheMatchesIsPartOfIt()
    {
        MatchResult[] one = [Result(11), Result(22)];
        MatchResult[] two = [Result(22), Result(11)];

        Assert.NotEqual(BatchDigest.Of("ctf_2fort", one), BatchDigest.Of("ctf_2fort", two));
    }

    /// <summary>
    /// A shorter batch is a different batch. Without this, a run that stopped
    /// early — the thing CLAUDE.md warns about, a run that ended rather than
    /// happened — could fold to the same number as a complete one.
    /// </summary>
    [Fact]
    public void AMissingMatchIsADifferentBatch()
    {
        MatchResult[] one = [Result(11), Result(22)];
        MatchResult[] two = [Result(11)];

        Assert.NotEqual(BatchDigest.Of("ctf_2fort", one), BatchDigest.Of("ctf_2fort", two));
    }

    /// <summary>
    /// The real thing, end to end: the same eval batch run twice must fold to
    /// the same number, or the instrument is measuring its own noise.
    /// </summary>
    [Fact]
    public void TwoRunsOfARealBatchAgree()
    {
        var options = HeadlessMatchOptions.Default with
        {
            Map = "ctf_truefort",
            Bots = 6,
            MaxTicks = 600,
            Seed = 7,
        };

        ReferenceTree.RequireNavCache();

        var map = LevelMap.FromPng(options.Map, ReferenceTree.ReadMap(options.Map))
            .ForArea(options.Area);

        var nav = NavCache.Load(ReferenceTree.NavCacheDirectory, options.Map, options.Area);

        var first = MatchRunner.Run(options, count: 2, map, nav, threads: 1);
        var second = MatchRunner.Run(options, count: 2, map, nav, threads: 1);

        Assert.Equal(
            BatchDigest.Of(options.Map, first), BatchDigest.Of(options.Map, second));
    }
}
