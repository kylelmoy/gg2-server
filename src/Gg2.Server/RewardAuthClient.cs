using System.Collections.Concurrent;
using System.Net.Sockets;
using Gg2.Protocol;

namespace Gg2.Server;

/// <summary>
/// One completed auth check, waiting to be applied on the tick thread.
/// </summary>
/// <param name="Player">
/// The roster entry that asked. <b>The entry object, never its index.</b> A
/// slot index is a roster index and every <c>PLAYER_LEAVE</c> compacts them, so
/// an index captured before an auth round trip names a different player by the
/// time it comes back — and the wrong player would be handed somebody else's
/// rewards seconds after an unrelated disconnect. The reference has the same
/// hazard and solves it the same way, with <c>instance_exists(currentPlayer)</c>
/// on an object reference.
/// </param>
/// <param name="Rewards">The reward string, or null if the check failed.</param>
public readonly record struct RewardAuthResult(RosterEntry Player, string? Rewards);

/// <summary>
/// Talks to the auth server. The only part of the reward flow that touches a
/// socket.
/// </summary>
/// <remarks>
/// <c>Objects/Networking/RewardAuthChecker</c>. The packet layout lives in
/// <see cref="RewardAuth"/>, which has no socket in it and is where the
/// byte-level tests are; this type is the part that cannot be tested without the
/// network, so it is kept as thin as it can be — the same split
/// <see cref="LobbyAnnouncer"/> uses, for the same reason.
/// <para>
/// <b>Constructed only when <c>--rewards</c> was passed, and that defaults
/// off.</b> Turning it on makes this server open outbound TCP connections to a
/// third party carrying its players' user ids. That is a decision for whoever
/// runs the server, not a default a binary should assume on their behalf, and it
/// is why <c>docs/PROTOCOL.md</c> listed the whole subsystem as skippable for six
/// phases.
/// </para>
/// <para>
/// <b>Every failure is swallowed and logged.</b> An unreachable auth server,
/// a timeout, a refused connection — none of it is a reason to disturb a match,
/// and the consequence is exactly that somebody's hat does not appear.
/// </para>
/// <para>
/// <b>Serial, like the reference.</b> <c>RewardAuthChecker</c> holds one socket
/// and takes the next queue item only when it is done. One pump task here does
/// the same, which also means a slow auth server cannot open a connection per
/// player.
/// </para>
/// </remarks>
public sealed class RewardAuthClient : IDisposable
{
    /// <summary>
    /// How long one check may take before it is abandoned.
    /// </summary>
    /// <remarks>
    /// <b>No equivalent in the reference, and it is needed here.</b>
    /// <c>RewardAuthChecker</c> is driven by a Step event that also drives the
    /// game, so a hung connection there stalls in a place somebody notices. A
    /// background task has no such witness: without a deadline one unanswered
    /// connection would park the serial pump for ever and silently swallow every
    /// later request.
    /// </remarks>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly IServerLog _log;
    private readonly string _host;
    private readonly int _port;

    private readonly BlockingCollection<(RosterEntry Player, byte[] Request)> _queue =
        new(new ConcurrentQueue<(RosterEntry, byte[])>(), RewardAuth.MaxQueued);

    private readonly ConcurrentQueue<RewardAuthResult> _completed = new();

    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _pump;

    public RewardAuthClient(IServerLog log, string host, int port)
    {
        ArgumentNullException.ThrowIfNull(log);

        _log = log;
        _host = host;
        _port = port;

        // LongRunning, so this gets a dedicated thread rather than a pool
        // slot. The pump spends its life parked on a blocking queue take and
        // then on network I/O, which is exactly what starves on a busy pool —
        // observed as an intermittent failure of the end-to-end test under the
        // full suite, where hundreds of other tests were saturating it. A relay
        // whose latency depends on unrelated CPU load is a real defect, not
        // only a flaky test.
        _pump = Task.Factory.StartNew(
            PumpAsync,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();
    }

    /// <summary>
    /// Queues one check.
    /// </summary>
    /// <remarks>
    /// <b>A full queue drops the request rather than blocking</b>, which is
    /// <c>rewardAuthStart.gml</c>'s own behaviour and is also the only safe
    /// choice here: this runs on the tick thread, and a blocking add would stall
    /// the whole match behind a slow third party.
    /// </remarks>
    public void Enqueue(RosterEntry player, byte[] request)
    {
        if (!_queue.TryAdd((player, request)))
            _log.Info($"reward auth for {player.Name} dropped: {RewardAuth.MaxQueued} already queued");
    }

    /// <summary>
    /// Hands back everything that finished since the last call.
    /// </summary>
    /// <remarks>
    /// <b>Drained on the tick thread and applied there.</b> The results carry
    /// roster entries and produce broadcasts; touching either from the pump task
    /// would be a data race against the whole server.
    /// </remarks>
    public bool TryTakeResult(out RewardAuthResult result) => _completed.TryDequeue(out result);

    private async Task PumpAsync()
    {
        try
        {
            foreach (var (player, request) in _queue.GetConsumingEnumerable(_shutdown.Token))
            {
                var rewards = await CheckAsync(request, player.Name).ConfigureAwait(false);
                _completed.Enqueue(new RewardAuthResult(player, rewards));
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }

    private async Task<string?> CheckAsync(byte[] request, string who)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        deadline.CancelAfter(Timeout);

        try
        {
            using var socket = new TcpClient();
            await socket.ConnectAsync(_host, _port, deadline.Token).ConfigureAwait(false);

            var stream = socket.GetStream();
            await stream.WriteAsync(request, deadline.Token).ConfigureAwait(false);

            // The auth server answers by writing a body and CLOSING — there is
            // no length and no status code, so end-of-stream is the only
            // terminator there is. Reading a fixed number of bytes instead would
            // either truncate a long reward list or hang for ever on a short one.
            using var body = new MemoryStream();
            await stream.CopyToAsync(body, deadline.Token).ConfigureAwait(false);

            return RewardAuth.ReadAuthResponse(body.GetBuffer().AsSpan(0, (int)body.Length));
        }
        catch (Exception e) when (e is SocketException or IOException or OperationCanceledException)
        {
            // Info rather than warn: a server on a private network with no route
            // to the auth service would otherwise warn once per joining player.
            _log.Info($"reward auth for {who} failed: {e.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _queue.CompleteAdding();

        // Best effort on the way out. The pump is cancelled, so this is bounded
        // by whatever syscall it is parked in rather than by Timeout.
        try
        {
            _pump.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // A cancelled pump faulting on the way down is not worth reporting.
        }

        _queue.Dispose();
        _shutdown.Dispose();
    }
}
