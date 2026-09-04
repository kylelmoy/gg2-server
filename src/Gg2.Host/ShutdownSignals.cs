using System.Runtime.InteropServices;
using Gg2.Server;

namespace Gg2.Host;

/// <summary>
/// Turns a process signal into the same graceful stop that Ctrl+C already gets.
/// </summary>
/// <remarks>
/// <b>This exists for deployment, not for interactive use.</b>
/// <c>Console.CancelKeyPress</c> covers Ctrl+C — SIGINT on Linux, a console
/// control event on Windows — and that is the only way anybody stops a server
/// they are watching. It is <em>not</em> how a service is stopped: systemd's
/// default <c>KillSignal</c> is SIGTERM and <c>docker stop</c> sends SIGTERM
/// too, and neither reaches <c>CancelKeyPress</c>.
/// <para>
/// <b>What an ungraceful stop actually costs</b>, since "the process exits
/// either way" is the reason this is easy to skip. <c>GameServer.Dispose</c> is
/// only reached on the graceful path, and it does three things nothing else
/// does: it takes the lobby listing down (otherwise the server keeps being
/// advertised until the lobby's own timeout, and players connect to nothing),
/// it closes an in-flight <c>--record</c> segment (a <c>.part</c> file only
/// becomes a readable <c>.trace</c> when it is closed, so the recording is lost
/// rather than truncated), and it shuts the client sockets down.
/// </para>
/// <para>
/// <b>Cross-platform on purpose.</b> .NET maps <see cref="PosixSignal.SIGTERM"/>
/// onto a console control event on Windows, so this registers on both and needs
/// no <c>OperatingSystem.IsWindows()</c> guard — unlike
/// <c>TimerResolution</c>, which is a real P/Invoke and does.
/// </para>
/// </remarks>
internal static class ShutdownSignals
{
    /// <summary>
    /// Registers <paramref name="signal"/> to cancel <paramref name="stopping"/>.
    /// Dispose the result to unregister.
    /// </summary>
    public static IDisposable Handle(
        PosixSignal signal, CancellationTokenSource stopping, IServerLog log)
    {
        ArgumentNullException.ThrowIfNull(stopping);
        ArgumentNullException.ThrowIfNull(log);

        return PosixSignalRegistration.Create(signal, context => Stop(context, stopping, log));
    }

    /// <summary>
    /// The handler body, separated from the registration so a test can drive it
    /// without raising a real signal at the test host.
    /// </summary>
    /// <remarks>
    /// <b><see cref="PosixSignalContext.Cancel"/> must be set.</b> It
    /// suppresses the runtime's <em>default</em> action for the signal, which
    /// for SIGTERM is to terminate the process — so without it the cancellation
    /// below races the process going away, and the graceful path it exists to
    /// reach is the half that loses. Setting it makes the signal a request that
    /// <c>Run</c> returns from its own loop.
    /// </remarks>
    internal static void Stop(
        PosixSignalContext context, CancellationTokenSource stopping, IServerLog log)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Cancel = true;
        log.Info($"stopping ({context.Signal})");
        stopping.Cancel();
    }
}
