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
/// Talks to the auth servers. The only part of the reward flow that touches a
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
/// off.</b> Turning it on makes this server open outbound TCP connections to
/// third parties carrying its players' user ids. That is a decision for whoever
/// runs the server, not a default a binary should assume on their behalf.
/// </para>
/// <para>
/// <b>Every server in the list is asked at once; the list order picks the
/// answer</b> (<see cref="RewardAuthTargets"/>). A check resolves to the first
/// server's reward string that is non-empty, as soon as every server before it
/// has come back empty or failed — so a success at the top of the list returns
/// without waiting for the rest, and a dead server costs its own timeout once.
/// </para>
/// <para>
/// <b>Every failure is swallowed and logged.</b> An unreachable auth server,
/// a timeout, a refused connection — none of it is a reason to disturb a match,
/// and the consequence is exactly that somebody's hat does not appear.
/// </para>
/// <para>
/// <b>Serial per server, like the reference.</b> <c>RewardAuthChecker</c> holds
/// one socket and takes the next queue item only when it is done. One worker per
/// server here does the same, so a slow auth server cannot be made to take a
/// connection per player, and cannot hold up a faster one.
/// </para>
/// </remarks>
public sealed class RewardAuthClient : IDisposable
{
    /// <summary>
    /// How long one server's answer to one check may take before it counts as a
    /// failure.
    /// </summary>
    /// <remarks>
    /// <b>No equivalent in the reference, and it is needed here.</b>
    /// <c>RewardAuthChecker</c> is driven by a Step event that also drives the
    /// game, so a hung connection there stalls in a place somebody notices. A
    /// background thread has no such witness: without a deadline one unanswered
    /// connection would park a worker for ever and silently swallow every later
    /// request. Servers are asked in parallel, so this is also the longest a
    /// whole check can take once it reaches the front of every worker's queue.
    /// <para>
    /// ⚠️ <b>Public because a test that waits on this component has to wait
    /// LONGER than it does, and a magic number in the test cannot know that.</b>
    /// </para>
    /// </remarks>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly IServerLog _log;
    private readonly Worker[] _workers;
    private readonly ConcurrentQueue<RewardAuthResult> _completed = new();
    private readonly CancellationTokenSource _shutdown = new();

    /// <param name="log">Where failures go.</param>
    /// <param name="servers">Host and port of each auth server, highest priority first.</param>
    public RewardAuthClient(IServerLog log, IReadOnlyList<(string Host, int Port)> servers)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(servers);

        _log = log;
        _workers = new Worker[servers.Count];

        for (var i = 0; i < servers.Count; i++)
            _workers[i] = new Worker(this, i, servers[i].Host, servers[i].Port);
    }

    /// <summary>
    /// Queues one check with every server.
    /// </summary>
    /// <remarks>
    /// <b>A full queue drops that server's part rather than blocking</b>, which
    /// is <c>rewardAuthStart.gml</c>'s own behaviour and is also the only safe
    /// choice here: this runs on the tick thread, and a blocking add would stall
    /// the whole match behind a slow third party. A dropped part counts as that
    /// server failing, so the others can still answer.
    /// </remarks>
    public void Enqueue(RosterEntry player, byte[] request)
    {
        var check = new Check(this, player, _workers.Length);

        if (_workers.Length == 0)
        {
            check.Resolve(null);
            return;
        }

        foreach (var worker in _workers)
        {
            if (!worker.TryAdd(check, request))
            {
                _log.Warn($"reward auth for {LogText.Safe(player.Name)} via {worker.Name} dropped: {RewardAuth.MaxQueued} already queued");
                check.Report(worker.Index, null);
            }
        }
    }

    /// <summary>
    /// Hands back everything that finished since the last call.
    /// </summary>
    /// <remarks>
    /// <b>Drained on the tick thread and applied there.</b> The results carry
    /// roster entries and produce broadcasts; touching either from a worker
    /// would be a data race against the whole server.
    /// </remarks>
    public bool TryTakeResult(out RewardAuthResult result) => _completed.TryDequeue(out result);

    public void Dispose()
    {
        _shutdown.Cancel();

        foreach (var worker in _workers)
            worker.Stop();

        _shutdown.Dispose();
    }

    /// <summary>
    /// One player's check across every server: a slot per server, resolved by
    /// list order.
    /// </summary>
    private sealed class Check(RewardAuthClient owner, RosterEntry player, int servers)
    {
        private readonly Lock _gate = new();
        private readonly bool[] _done = new bool[servers];
        private readonly string?[] _answers = new string?[servers];
        private bool _resolved;

        public RosterEntry Player => player;

        /// <summary>Records server <paramref name="index"/>'s answer, and resolves if it can.</summary>
        public void Report(int index, string? rewards)
        {
            lock (_gate)
            {
                if (_resolved)
                    return;

                _done[index] = true;
                _answers[index] = rewards;

                // The first server in list order that has not failed decides:
                // its answer if it has one, otherwise keep waiting for it.
                for (var i = 0; i < _done.Length; i++)
                {
                    if (!_done[i])
                        return;

                    if (_answers[i] is not null)
                    {
                        ResolveLocked(_answers[i]);
                        return;
                    }
                }

                ResolveLocked(null);
            }
        }

        public void Resolve(string? rewards)
        {
            lock (_gate)
                ResolveLocked(rewards);
        }

        /// <summary>Whether the answer is already known, so a worker can skip its part.</summary>
        public bool IsResolved
        {
            get
            {
                lock (_gate)
                    return _resolved;
            }
        }

        private void ResolveLocked(string? rewards)
        {
            if (_resolved)
                return;

            _resolved = true;
            owner._completed.Enqueue(new RewardAuthResult(player, rewards));
        }
    }

    /// <summary>One auth server: its own queue and its own thread.</summary>
    private sealed class Worker
    {
        private readonly RewardAuthClient _owner;
        private readonly string _host;
        private readonly int _port;
        private readonly BlockingCollection<(Check Check, byte[] Request)> _queue =
            new(new ConcurrentQueue<(Check, byte[])>(), RewardAuth.MaxQueued);
        private readonly Thread _thread;

        public Worker(RewardAuthClient owner, int index, string host, int port)
        {
            _owner = owner;
            Index = index;
            _host = host;
            _port = port;
            Name = host.Contains(':') ? $"[{host}]:{port}" : $"{host}:{port}";

            // ⛔ A REAL THREAD, and synchronous I/O on it. An async pump started
            // with TaskCreationOptions.LongRunning gets a dedicated thread only
            // up to its first await; every continuation after that runs on the
            // thread pool. Measured under a full-suite run: a saturated pool
            // grows by about a thread a second, so a check waited 47 seconds,
            // past its own 20-second deadline, and the reward never came back.
            // A thread's latency does not depend on unrelated CPU load.
            _thread = new Thread(Pump)
            {
                IsBackground = true,
                Name = "reward-auth",
            };

            _thread.Start();
        }

        public int Index { get; }

        public string Name { get; }

        public bool TryAdd(Check check, byte[] request) => _queue.TryAdd((check, request));

        public void Stop()
        {
            _queue.CompleteAdding();

            // Best effort on the way out. The token is cancelled, so this is
            // bounded by whatever syscall the worker is parked in rather than by
            // Timeout, and the thread is a background one, so a worker still
            // parked in a read cannot hold the process open past it.
            _thread.Join(TimeSpan.FromSeconds(1));

            // ⚠️ The queue is NOT disposed. A worker still inside Ask when the
            // join gives up comes back to GetConsumingEnumerable afterwards, and
            // a disposed collection throws there, on a background thread, which
            // ends the process. The GC has it once the thread is gone.
        }

        private void Pump()
        {
            try
            {
                foreach (var (check, request) in _queue.GetConsumingEnumerable(_owner._shutdown.Token))
                {
                    // A server higher in the list already answered: nothing this
                    // one says can change the result, so do not send it the id.
                    if (check.IsResolved)
                        continue;

                    check.Report(Index, Ask(request, check.Player.Name));
                }
            }
            catch (OperationCanceledException)
            {
                // Shutdown.
            }
        }

        /// <summary>One request to this server, start to finish.</summary>
        /// <remarks>
        /// ⚠️ <b>Synchronous on purpose.</b> Socket timeouts bound this without
        /// any scheduler underneath it. <see cref="Timeout"/> is the budget for
        /// the WHOLE exchange, so each step gets what is left of it rather than a
        /// fresh copy; three fresh copies would make the real ceiling sixty
        /// seconds.
        /// </remarks>
        private string? Ask(byte[] request, string who)
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
                // terminator there is. Reading a fixed number of bytes instead
                // would either truncate a long reward list or hang for ever on a
                // short one.
                using var body = new MemoryStream();
                stream.CopyTo(body);

                return RewardAuth.ReadAuthResponse(body.GetBuffer().AsSpan(0, (int)body.Length));
            }
            catch (Exception e) when (e is SocketException or IOException or ObjectDisposedException)
            {
                _owner._log.Warn($"reward auth for {LogText.Safe(who)} via {Name} failed: {e.Message}");
                return null;
            }
        }

        /// <summary>Connects, bounded by <paramref name="timeoutMs"/>.</summary>
        /// <remarks>
        /// ⛔ <b>A non-blocking connect and a Poll, because a blocking one cannot
        /// be bounded and every async one needs a scheduler.</b>
        /// <c>Socket.Connect</c> runs to the OS's own limit — tens of seconds to
        /// a routed host that never answers — and that is the case a deadline
        /// exists for.
        /// <para>
        /// ⚠️ <c>SelectWrite</c> alone is not the answer: a REFUSED connection
        /// also makes the socket selectable, so the error state has to be read
        /// afterwards or a refusal reads as a success and the write fails
        /// confusingly later.
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

            // Poll takes MICROSECONDS.
            if (!socket.Poll(timeoutMs * 1000, SelectMode.SelectWrite))
                throw new SocketException((int)SocketError.TimedOut);

            if (socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Error) is int code and not 0)
                throw new SocketException(code);
        }
    }
}
