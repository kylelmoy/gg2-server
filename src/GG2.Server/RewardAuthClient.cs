using System.Collections.Concurrent;
using System.Net.Sockets;
using GG2.Protocol;

namespace GG2.Server;

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
    /// <remarks>
    /// ⚠️ <b>Public because a test that waits on this component has to wait
    /// LONGER than it does, and a magic number in the test cannot know that.</b>
    /// <c>RewardAuthTests</c> hardcoded fifteen seconds against this twenty and
    /// so reported "the auth never came back" for a relay that was still
    /// perfectly well inside its own deadline — a failure that accuses the
    /// server of losing the response when nothing had gone wrong yet.
    /// </remarks>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly IServerLog _log;
    private readonly string _host;
    private readonly int _port;

    private readonly BlockingCollection<(RosterEntry Player, byte[] Request)> _queue =
        new(new ConcurrentQueue<(RosterEntry, byte[])>(), RewardAuth.MaxQueued);

    private readonly ConcurrentQueue<RewardAuthResult> _completed = new();

    private readonly CancellationTokenSource _shutdown = new();
    private readonly Thread _pump;

    public RewardAuthClient(IServerLog log, string host, int port)
    {
        ArgumentNullException.ThrowIfNull(log);

        _log = log;
        _host = host;
        _port = port;

        // ⛔ A REAL THREAD, and synchronous I/O on it, because the obvious
        // spelling of this does not do what it says.
        //
        // This was `Task.Factory.StartNew(PumpAsync, …, TaskCreationOptions.
        // LongRunning, …).Unwrap()`, with a comment claiming the pump got a
        // dedicated thread and so could not be starved by a busy pool. ⚠️ It did
        // not. `LongRunning` gives a dedicated thread to the SYNCHRONOUS PREFIX
        // of an async method — everything up to the first `await`. After that the
        // continuations are scheduled on TaskScheduler.Default, which is the
        // thread pool, and the awaits here are the whole body: connect, write,
        // read. So the part that was supposed to be immune was the part that
        // never ran there.
        //
        // Measured: under a full-suite run the end-to-end test waited 47 seconds
        // — past this class's own 20-second deadline — and the reward never came
        // back at all. The pool grows by roughly a thread a second once it is
        // saturated, so a burst of blocked pool work stalls a continuation for
        // far longer than the deadline it was supposed to be bounded by, and the
        // check then times out and returns null for ever.
        //
        // A thread has no such failure mode: nothing about this pump's latency
        // depends on unrelated CPU load any more, which is the property the old
        // comment claimed and this one can keep.
        _pump = new Thread(Pump)
        {
            IsBackground = true,
            Name = "reward-auth",
        };

        _pump.Start();
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
            _log.Warn($"reward auth for {LogText.Safe(player.Name)} dropped: {RewardAuth.MaxQueued} already queued");
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

    private void Pump()
    {
        try
        {
            foreach (var (player, request) in _queue.GetConsumingEnumerable(_shutdown.Token))
                _completed.Enqueue(new RewardAuthResult(player, Check(request, player.Name)));
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }

    /// <summary>One check, start to finish, on the pump thread.</summary>
    /// <remarks>
    /// ⚠️ <b>Synchronous on purpose.</b> Socket timeouts bound this without any
    /// scheduler underneath it — see the constructor for what the async spelling
    /// cost. <see cref="Timeout"/> is the budget for the WHOLE exchange, so each
    /// step gets what is left of it rather than a fresh copy; three fresh copies
    /// would make the real ceiling sixty seconds and this class's one public
    /// promise a third of the truth.
    /// </remarks>
    private string? Check(byte[] request, string who)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();

        int Remaining()
        {
            var left = (int)(Timeout - clock.Elapsed).TotalMilliseconds;

            return left > 0 ? left : throw new SocketException((int)SocketError.TimedOut);
        }

        try
        {
            using var client = new TcpClient();

            Connect(client.Client, Remaining());

            client.SendTimeout = Remaining();
            client.ReceiveTimeout = Remaining();

            var stream = client.GetStream();
            stream.Write(request);

            // The auth server answers by writing a body and CLOSING — there is
            // no length and no status code, so end-of-stream is the only
            // terminator there is. Reading a fixed number of bytes instead would
            // either truncate a long reward list or hang for ever on a short one.
            using var body = new MemoryStream();
            stream.CopyTo(body);

            return RewardAuth.ReadAuthResponse(body.GetBuffer().AsSpan(0, (int)body.Length));
        }
        catch (Exception e) when (e is SocketException or IOException or ObjectDisposedException)
        {
            // Info rather than warn: a server on a private network with no route
            // to the auth service would otherwise warn once per joining player.
            _log.Warn($"reward auth for {LogText.Safe(who)} failed: {e.Message}");
            return null;
        }
    }

    /// <summary>Connects, bounded by <paramref name="timeoutMs"/>.</summary>
    /// <remarks>
    /// ⛔ <b>A non-blocking connect and a Poll, because a blocking one cannot be
    /// bounded and every async one needs a scheduler.</b> <c>Socket.Connect</c>
    /// runs to the OS's own limit — tens of seconds to a routed host that never
    /// answers — and that is the case a deadline exists for. <c>ConnectAsync</c>
    /// with a token would bound it and put this back on the thread pool, which is
    /// the bug the constructor describes.
    /// <para>
    /// ⚠️ <c>SelectWrite</c> alone is not the answer: a REFUSED connection also
    /// makes the socket selectable, so the error state has to be read afterwards
    /// or a refusal reads as a success and the write fails confusingly later.
    /// </para>
    /// </remarks>
    private void Connect(Socket socket, int timeoutMs)
    {
        socket.Blocking = false;

        try
        {
            socket.Connect(_host, _port);
        }
        catch (SocketException e) when (e.SocketErrorCode is SocketError.WouldBlock or SocketError.InProgress)
        {
            // Expected: the connect is now in flight and Poll is what waits.
        }
        finally
        {
            socket.Blocking = true;
        }

        if (socket.Connected)
            return;

        // Poll takes MICROSECONDS, and its int overflows at about 35 minutes —
        // far above any Timeout this class would accept, but the units are the
        // kind of thing that gets copied wrong.
        if (!socket.Poll(timeoutMs * 1000, SelectMode.SelectWrite))
            throw new SocketException((int)SocketError.TimedOut);

        // GetSocketOption's object overload is documented to return the int for
        // SocketOptionName.Error, but its signature is `object?` — a null here
        // would mean the platform answered nothing, which is not a failure.
        if (socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Error) is int code and not 0)
            throw new SocketException(code);
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _queue.CompleteAdding();

        // Best effort on the way out. The pump is cancelled, so this is bounded
        // by whatever syscall it is parked in rather than by Timeout — and the
        // thread is a background one, so a pump still parked in a read cannot
        // hold the process open past it.
        _pump.Join(TimeSpan.FromSeconds(1));

        _queue.Dispose();
        _shutdown.Dispose();
    }
}
