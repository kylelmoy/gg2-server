using Gg2.Env;

namespace Gg2.Tests;

/// <summary>
/// The random-head agent, over the real socket.
/// </summary>
/// <remarks>
/// <c>docs/ENV.md</c>'s v0 exit criterion 1, at a size the suite can afford.
/// <b>The thousand-duel run is a CLI command, not a test</b> —
/// <c>gg2-env smoke --worlds 64 --duels 1000</c> — because a multi-second test
/// in a four-second suite is a test people start skipping. What is here is the
/// same code path at a size that runs in well under a second, so a break is
/// caught by the suite and the full run stays a deliberate act.
/// </remarks>
public sealed class EnvSmokeTests
{
    /// <summary>
    /// A random policy completes duels without crashing or hanging.
    /// </summary>
    /// <remarks>
    /// <b>Random is not a weak test here.</b> It reaches states a scripted
    /// policy never does — firing into its own feet, walking off every ledge —
    /// which is the coverage a smoke run wants. What it cannot check is that the
    /// numbers are <em>right</em>; that is what the other Env gates are for.
    /// </remarks>
    [Fact]
    public void ARandomHeadAgentCompletesDuelsOverTheSocket()
    {
        var result = SmokeRun.Run(ReferenceTree.IncludedFiles, worlds: 4, duels: 8, seed: 20260828);

        Assert.Equal(8, result.Duels);
        Assert.True(result.DecisionSteps > 0);
        Assert.True(result.StepsPerSecond > 0);
    }

    /// <summary>
    /// <b>A world that stopped terminating is REPORTED, not exhibited.</b>
    /// </summary>
    /// <remarks>
    /// The smoke loop has a hard ceiling on its step count as well as on its
    /// duel count, because the one failure a smoke run must not have is hanging:
    /// a hung run in CI is indistinguishable from an infrastructure problem, and
    /// somebody restarts it instead of reading it.
    /// <para>
    /// Asked for more duels than the ceiling can deliver, so the guard fires.
    /// </para>
    /// </remarks>
    [Fact]
    public void AnImpossibleDuelCountIsReportedRatherThanHung()
    {
        // The ceiling is passed explicitly. The default is duels * 1000,
        // which a real run never reaches — correct for the default and useless
        // for a test, so the guard would otherwise be a safety net nobody had
        // ever watched trip. The first draft of this test tried to reach the
        // default by asking for two duels and simply passed them.
        var thrown = Assert.Throws<InvalidOperationException>(
            () => SmokeRun.Run(
                ReferenceTree.IncludedFiles, worlds: 1, duels: 5, seed: 1, threads: 1, maxSteps: 10));

        Assert.Contains("not terminating", thrown.Message);
    }
}
