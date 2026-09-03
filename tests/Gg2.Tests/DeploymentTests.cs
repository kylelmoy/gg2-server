using System.Runtime.InteropServices;
using Gg2.Bots;
using Gg2.Host;
using Gg2.Server;

namespace Gg2.Tests;

/// <summary>
/// The two things a server that was <em>deployed</em> gets wrong that one run
/// from a checkout never does.
/// </summary>
/// <remarks>
/// Both were found by asking what a Linux VPS would do differently, and neither
/// is Linux-specific once written down: a service is stopped with a signal
/// rather than with Ctrl+C, and a published directory does not contain the
/// repository's <c>nav/</c>. <c>docs/DEPLOY.md</c>.
/// </remarks>
public class DeploymentTests
{
    /// <summary>
    /// SIGTERM stops the server the same way Ctrl+C does, rather than killing it.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>The <c>Cancel</c> assertion is the load-bearing half.</b> Cancelling
    /// the token without it leaves the runtime's default action in place, so the
    /// process is terminated while <c>Run</c> is still unwinding and
    /// <c>GameServer.Dispose</c> — the lobby takedown, the <c>.part</c> recording
    /// segment, the socket shutdown — is a race the graceful path loses. A
    /// version of this that asserted only the cancellation passed while fixing
    /// nothing.
    /// </remarks>
    [Fact]
    public void SigtermRequestsAGracefulStop()
    {
        using var stopping = new CancellationTokenSource();
        var log = new RecordingLog();
        var context = new PosixSignalContext(PosixSignal.SIGTERM);

        ShutdownSignals.Stop(context, stopping, log);

        Assert.True(context.Cancel, "the runtime's default termination was not suppressed");
        Assert.True(stopping.IsCancellationRequested);
        Assert.True(log.Contains("stopping"));
    }

    /// <summary>
    /// The registration itself works on this platform — .NET maps SIGTERM onto a
    /// console control event on Windows, so no <c>OperatingSystem.IsWindows()</c>
    /// guard is wanted and none is written.
    /// </summary>
    [Fact]
    public void SigtermCanBeRegistered()
    {
        using var stopping = new CancellationTokenSource();

        using var registration =
            ShutdownSignals.Handle(PosixSignal.SIGTERM, stopping, new RecordingLog());

        Assert.NotNull(registration);
        Assert.False(stopping.IsCancellationRequested);
    }

    /// <summary>
    /// A server told to run bots with no nav directory at all says so at startup.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The silent case, and the one a deployment hits.</b>
    /// <c>BotPopulationService.MapChanged</c> already logs "no nav cache for
    /// &lt;map&gt;" when a directory is configured and one map misses — but an
    /// EMPTY <c>NavCacheDirectory</c> never enters that branch, so the whole
    /// feature failing was quieter than one map failing.
    /// <para>
    /// ⚠️ <b>When this was written it was the DEFAULT outcome of deploying</b>,
    /// because <c>nav/</c> was 34 MB and shipped only behind
    /// <c>-p:PublishNav=true</c>. Gzipping the graphs to 1.2 MB removed the flag
    /// and with it the default; the warning stays because the state is still
    /// reachable — an empty <c>GG2_NAVCACHE</c>, or a binary copied without its
    /// directory. docs/DEPLOY.md, "the nav graphs".
    /// </para>
    /// </remarks>
    [Fact]
    public void BotsWithNoNavDirectoryWarnAtStartup()
    {
        using var harness = new ServerHarness(options =>
        {
            options.Bots = BotPopulationOptions.Default with { Enabled = true, MinHumans = 0 };
            options.NavCacheDirectory = string.Empty;
        });

        Assert.True(
            harness.Log.Contains("bots will fight but not navigate"),
            "a server running bots with no nav directory started silently");
    }

    /// <summary>
    /// ⚠️ The control arm: a configured nav directory must NOT produce it.
    /// </summary>
    /// <remarks>
    /// Without this the warning could be unconditional and the test above would
    /// still be green — the shape CLAUDE.md keeps finding, where the code is
    /// self-consistent and only a reader who acts on it finds out. Here that
    /// reader is an operator who wired <c>--botnav</c> correctly and was told
    /// every startup that they had not.
    /// </remarks>
    [Fact]
    public void AConfiguredNavDirectoryDoesNotWarn()
    {
        ReferenceTree.RequireNavCache();

        using var harness = new ServerHarness(options =>
        {
            options.Bots = BotPopulationOptions.Default with { Enabled = true, MinHumans = 0 };
            options.NavCacheDirectory = ReferenceTree.NavCacheDirectory;
        });

        Assert.False(
            harness.Log.Contains("bots will fight but not navigate"),
            "a correctly configured server was told its nav directory was missing");
    }

    /// <summary>
    /// ⚠️ And the other control arm: a server with bots OFF says nothing either,
    /// however the nav directory is set.
    /// </summary>
    [Fact]
    public void AServerWithoutBotsDoesNotWarnAboutNav()
    {
        using var harness = new ServerHarness(options =>
        {
            options.Bots = BotPopulationOptions.Default;   // Enabled = false
            options.NavCacheDirectory = string.Empty;
        });

        Assert.False(harness.Log.Contains("bots will fight but not navigate"));
    }
}
