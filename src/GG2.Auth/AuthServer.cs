using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace GG2.Auth;

internal sealed record AuthOptions
{
    public IPAddress Bind { get; init; } = IPAddress.IPv6Any;

    /// <summary><c>AUTH_SERVER_PORT</c> in the reference's <c>Constants.xml</c>.</summary>
    public int Port { get; init; } = 45022;

    /// <summary>
    /// Public addresses to also try when a request comes from a loopback or
    /// private address. See <see cref="AuthServer.CandidateAddresses"/>.
    /// </summary>
    public IReadOnlyList<string> PublicAddresses { get; init; } = [];

    /// <summary>Accept ipCheck = 0, the reference's host self-auth.</summary>
    public bool AllowWithoutIp { get; init; } = true;

    public int MaxConnections { get; init; } = 256;
    public int MaxPerAddress { get; init; } = 16;
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// The auth server's socket side: read one request, answer, close.
/// </summary>
/// <remarks>
/// <b>The answer is a bare reward string and the close is the terminator.</b>
/// The reference's <c>RewardAuthChecker</c> Step event reads to end of stream
/// and treats any non-empty body as success, so a failure is an empty body:
/// writing "FAIL" would hand the player a reward named FAIL. A user with no
/// rewards enabled is answered the same way, which costs nothing because the
/// client could not tell an empty set from a failure anyway.
/// </remarks>
internal sealed class AuthServer : IDisposable
{
    private readonly AuthOptions _options;
    private readonly UserStore _users;
    private readonly ThrottledLog _log;
    private readonly Socket _listener;
    private readonly ConcurrentDictionary<IPAddress, int> _perAddress = new();
    private int _connections;

    public AuthServer(AuthOptions options, UserStore users, Action<string> log)
    {
        _options = options;
        _users = users;
        _log = new ThrottledLog(log);

        _listener = new Socket(options.Bind.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        if (options.Bind.AddressFamily == AddressFamily.InterNetworkV6)
            _listener.DualMode = true;
        _listener.Bind(new IPEndPoint(options.Bind, options.Port));
        _listener.Listen(256);

        new Thread(AcceptLoop) { IsBackground = true, Name = "auth-accept" }.Start();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndPoint!).Port;

    public void Dispose() => _listener.Dispose();

    /// <summary>
    /// Blocking sockets on dedicated threads, one per connection, capped at
    /// <see cref="AuthOptions.MaxConnections"/>.
    /// </summary>
    /// <remarks>
    /// <b>Not async on the thread pool</b>, for the reason
    /// <c>GG2.Server.RewardAuthClient</c> gives: a pool saturated by unrelated
    /// work delays a continuation past the game server's 20-second deadline, and
    /// the player silently gets no rewards. A connection here is one player
    /// joining one game server, so the traffic is a few per minute and a thread
    /// each costs nothing that matters.
    /// </remarks>
    private void AcceptLoop()
    {
        while (true)
        {
            Socket socket;
            try
            {
                socket = _listener.Accept();
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }

            var remote = Normalise(((IPEndPoint)socket.RemoteEndPoint!).Address);

            if (Interlocked.Increment(ref _connections) > _options.MaxConnections)
            {
                Interlocked.Decrement(ref _connections);
                socket.Dispose();
                continue;
            }

            if (_perAddress.AddOrUpdate(remote, 1, static (_, n) => n + 1) > _options.MaxPerAddress)
            {
                Release(remote);
                socket.Dispose();
                continue;
            }

            new Thread(() => Serve(socket, remote)) { IsBackground = true, Name = "auth-connection" }.Start();
        }
    }

    private void Release(IPAddress remote)
    {
        Interlocked.Decrement(ref _connections);
        if (_perAddress.AddOrUpdate(remote, 0, static (_, n) => n - 1) <= 0)
            _perAddress.TryRemove(new KeyValuePair<IPAddress, int>(remote, 0));
    }

    private void Serve(Socket socket, IPAddress remote)
    {
        var buffer = new byte[AuthRequest.MaxBytes];
        var clock = System.Diagnostics.Stopwatch.StartNew();

        // The deadline is for the whole exchange, so each wait gets what is left
        // of it; a fresh timeout per receive would let a peer sending a byte at a
        // time hold the connection for ever.
        int Remaining()
        {
            var left = (int)(_options.ReadTimeout - clock.Elapsed).TotalMilliseconds;
            return left > 0 ? left : throw new SocketException((int)SocketError.TimedOut);
        }

        try
        {
            var filled = 0;
            AuthRequest request;
            while (true)
            {
                var status = AuthRequest.TryParse(buffer.AsSpan(0, filled), out request);
                if (status == RequestStatus.Complete)
                    break;
                if (status == RequestStatus.Invalid || filled == buffer.Length)
                {
                    _log.Write($"{remote}: malformed request");
                    return;
                }

                socket.ReceiveTimeout = Remaining();
                var read = socket.Receive(buffer, filled, buffer.Length - filled, SocketFlags.None);
                if (read == 0)
                    return;
                filled += read;
            }

            var (user, outcome) = Check(request, remote);
            _log.Write($"{remote}: id {Printable(request.RewardId)} {outcome}");

            if (user is { Rewards.Length: > 0 })
            {
                socket.SendTimeout = Remaining();
                socket.Send(Encoding.Latin1.GetBytes(user.Rewards));
            }

            socket.Shutdown(SocketShutdown.Send);
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            // Timed out, reset or shut down: the connection just ends, which the
            // game server reads as a failure.
        }
        finally
        {
            socket.Dispose();
            Release(remote);
        }
    }

    /// <summary>
    /// Whether the client knew the key, and the user if so.
    /// </summary>
    internal (User? User, string Outcome) Check(in AuthRequest request, IPAddress remote)
    {
        var user = _users.Find(request.RewardId);
        if (user is null)
            return (null, "FAIL: no such user");

        if (!request.IpCheck)
        {
            if (!_options.AllowWithoutIp)
                return (null, "FAIL: ipCheck 0 refused by --require-ip");

            return Matches(request.Response, AuthRequest.Expected(user.Key, request.Challenge, null))
                ? (user, "ok without ip")
                : (null, "FAIL: wrong key");
        }

        foreach (var ip in CandidateAddresses(remote))
            if (Matches(request.Response, AuthRequest.Expected(user.Key, request.Challenge, ip)))
                return (user, $"ok as {ip}");

        return (null, "FAIL: wrong key, or the client saw the game server at another address");
    }

    /// <summary>
    /// The addresses the client might have seen the game server at.
    /// </summary>
    /// <remarks>
    /// <b>Always the address the request came from</b>, which is the whole of
    /// the reference's check: the game server cannot choose it, so a response
    /// captured by one game server cannot be replayed through another.
    /// <para>
    /// <b>Plus the configured public addresses, but only for a loopback or
    /// private peer.</b> A game server on the same machine or LAN as this one
    /// arrives as 127.0.0.1 or 192.168.x.y while its players connected to the
    /// public address, so the IP check alone would fail every one of them. The
    /// cost is that any host on that private network could relay for the public
    /// address, which is a network the operator already controls.
    /// </para>
    /// </remarks>
    internal IEnumerable<string> CandidateAddresses(IPAddress remote)
    {
        yield return remote.ToString();

        if (IsLocal(remote))
            foreach (var ip in _options.PublicAddresses)
                yield return ip;
    }

    internal static bool IsLocal(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
            return true;

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal || address.IsIPv6SiteLocal;

        var b = address.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254)
            || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
    }

    internal static IPAddress Normalise(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static bool Matches(byte[] response, byte[] expected) =>
        CryptographicOperations.FixedTimeEquals(response, expected);

    /// <summary>An id is arbitrary bytes from the network; make it safe to print.</summary>
    internal static string Printable(string text)
    {
        var builder = new StringBuilder(Math.Min(text.Length, 64) + 2);
        builder.Append('"');
        foreach (var c in text.Length > 64 ? text[..64] : text)
            builder.Append(c is >= ' ' and <= '~' and not '"' and not '\\' ? c : '?');
        if (text.Length > 64)
            builder.Append('…');
        builder.Append('"');
        return builder.ToString();
    }
}

/// <summary>
/// At most <see cref="Burst"/> lines per <see cref="Window"/>, then one line
/// saying how many were dropped.
/// </summary>
/// <remarks>
/// Every line this server writes is caused by whoever connects, and anyone can
/// connect, so an unthrottled log is a disk anyone can fill.
/// </remarks>
internal sealed class ThrottledLog(Action<string> sink)
{
    public const int Burst = 30;
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(10);

    private readonly Lock _lock = new();
    // Far enough in the past that the first line opens a window, and not so far
    // that the subtraction overflows.
    private long _windowStart = long.MinValue / 2;
    private int _written;
    private int _dropped;

    public void Write(string line)
    {
        lock (_lock)
        {
            var now = Environment.TickCount64;
            if (now - _windowStart >= (long)Window.TotalMilliseconds)
            {
                if (_dropped > 0)
                    sink($"({_dropped} more line(s) dropped by the log throttle)");
                _windowStart = now;
                _written = 0;
                _dropped = 0;
            }

            if (_written < Burst)
            {
                _written++;
                sink(line);
            }
            else
            {
                _dropped++;
            }
        }
    }
}
