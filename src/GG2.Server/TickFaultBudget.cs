namespace GG2.Server;

/// <summary>
/// How many ticks may fail before the server gives up and stops.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a budget rather than "catch and carry on".</b> A tick that throws
/// leaves the simulation part-way through a stage — half a stage's mutations
/// applied to <c>WorldState</c>, a broadcast half written — and the next tick
/// starts from there. Sometimes that recovers: a transient failure in an
/// optional subsystem, a map that would not load, a plugin that threw once. Just
/// as often it does not, and then swallowing the exception converts a crash into
/// something much worse — a server that is up, listed in the lobby, accepting
/// players and simulating nothing, thirty times a second, for ever. Nobody is
/// paged for that; the players just leave.
/// </para>
/// <para>
/// <b>So the rule is: absorb a fault, refuse to absorb a pattern.</b> Faults
/// inside a rolling window are counted, and past the allowance the loop stops
/// and the process exits non-zero — which is the signal a supervisor
/// understands. <c>Restart=on-failure</c> in the unit file (docs/DEPLOY.md) then
/// does the one thing that reliably clears a corrupted in-memory state, and does
/// it with a fresh log line saying why.
/// </para>
/// <para>
/// ⚠️ <b>An isolated fault is still an error, not a shrug.</b> Staying under the
/// allowance means the server carries on; it does not mean nothing happened.
/// Every one is logged with its stack trace, and the count is on the panel, so
/// "it recovered" is a thing an operator can see rather than a thing that
/// happened quietly.
/// </para>
/// </remarks>
public sealed class TickFaultBudget
{
    /// <summary>
    /// Faults tolerated inside <see cref="Window"/> before the server stops.
    /// </summary>
    /// <remarks>
    /// Small on purpose. A healthy server's count is zero for its whole life, so
    /// this is not a number that trades reliability against noise — three faults
    /// in a minute is already a server that is not working, and the useful
    /// question is only how quickly it says so.
    /// </remarks>
    public const int Allowance = 3;

    /// <summary>The rolling window the allowance is counted over.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private long _windowStartMs;
    private int _inWindow;
    private bool _started;

    /// <summary>Faults since the server started. For the log and the panel.</summary>
    public int Total { get; private set; }

    /// <summary>
    /// Records a fault and says whether the server should carry on.
    /// </summary>
    /// <param name="nowMs">
    /// A monotonic millisecond clock. <c>Environment.TickCount64</c>, for the
    /// reason <see cref="LogThrottle"/> gives: a wall clock that steps backwards
    /// over an NTP correction would widen the window silently.
    /// </param>
    /// <returns><c>true</c> to keep ticking; <c>false</c> to stop.</returns>
    public bool RecordAndContinue(long nowMs)
    {
        Total++;

        if (!_started || nowMs - _windowStartMs > (long)Window.TotalMilliseconds)
        {
            _started = true;
            _windowStartMs = nowMs;
            _inWindow = 0;
        }

        _inWindow++;
        return _inWindow <= Allowance;
    }
}
