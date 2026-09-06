using System.Net;
using System.Net.Sockets;
using Gg2.Protocol;
using Gg2.Server;

namespace Gg2.Tests;

/// <summary>
/// The handshake, driven by a client that keeps every rule and never finishes.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>The reference's 30-second timeout is an IDLE timeout, and that is the
/// hole.</b> <c>serviceJoiningPlayer.gml:1</c> drops a connection when
/// <c>current_time - lastContact &gt; 30000</c>, and every byte received
/// refreshes <c>lastContact</c>. <c>PING</c> is a legal command in the
/// <c>ExpectCommand</c> state and costs the client one byte. So a connection
/// that pings occasionally is never idle, never times out, and — once it has
/// sent <c>RESERVE_SLOT</c> — holds a seat, because <c>OccupiesSlot</c> is
/// counted against the player limit by <c>OccupiedSlots</c>.
/// </para>
/// <para>
/// <see cref="JoiningConnection"/> takes the clock as a parameter, so these
/// drive it directly rather than through <c>ServerHarness</c>: the deadline is
/// measured in minutes and the server reads <c>DateTime.UtcNow</c> inside its
/// own tick. Same code path, no sleeping.
/// </para>
/// </remarks>
public sealed class HandshakeAbuseTests
{
    private sealed class Pair : IDisposable
    {
        private readonly Socket _listener;

        public Pair(ConnectionLimits limits, DateTime now)
        {
            _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            _listener.Listen(1);

            Client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            Client.Connect((IPEndPoint)_listener.LocalEndPoint!);

            Server = new ClientConnection(_listener.Accept(), limits);
            Joining = new JoiningConnection(Server, now, limits);
        }

        public Socket Client { get; }

        public ClientConnection Server { get; }

        public JoiningConnection Joining { get; }

        /// <summary>Sends bytes and services the handshake until they are consumed.</summary>
        /// <remarks>
        /// The handshake advances at most one step per <c>Service</c> call — the
        /// reference services one <c>JoiningPlayer</c> per End Step and the port
        /// reproduces that — so a test that called it once per message would be
        /// asserting on a half-finished state machine.
        /// </remarks>
        public JoinOutcome Pump(in JoinContext context, DateTime now, ReadOnlySpan<byte> bytes = default)
        {
            if (!bytes.IsEmpty)
                Client.Send(bytes);

            var outcome = JoinOutcome.Pending;

            for (var i = 0; i < 12; i++)
            {
                outcome = Joining.Service(context, now);

                if (outcome != JoinOutcome.Pending)
                    return outcome;
            }

            return outcome;
        }

        public void Dispose()
        {
            Server.Dispose();
            Client.Dispose();
            _listener.Dispose();
        }
    }

    private static JoinContext Context() => new()
    {
        ServerName = "test",
        CurrentMap = "ctf_truefort",
        Password = string.Empty,
        PlayerLimit = 10,
        OccupiedSlots = 0,
    };

    private static byte[] Hello()
    {
        var hello = new byte[17];
        hello[0] = (byte)MessageType.Hello;
        GameConstants.ProtocolUuidBytes.CopyTo(hello.AsSpan(1));
        return hello;
    }

    private static byte[] ReserveSlot(string name)
    {
        var bytes = new byte[2 + name.Length];
        bytes[0] = (byte)MessageType.ReserveSlot;
        bytes[1] = (byte)name.Length;
        Gg2Text.EncodeTo(name, bytes.AsSpan(2));
        return bytes;
    }

    /// <summary>
    /// A connection that reserves a seat and then pings for ever is eventually
    /// dropped.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Note what the loop does: it advances the clock by twenty seconds a
    /// step and pings each time.</b> That is inside the reference's
    /// thirty-second idle window, on purpose — the point is that the idle
    /// timeout never fires. Only the deadline, which no amount of activity
    /// resets, ends this.
    /// </remarks>
    [Fact]
    public void AConnectionThatPingsForEverStopsHoldingASeat()
    {
        var limits = ConnectionLimits.Default with { MaxHandshakeDuration = TimeSpan.FromMinutes(2) };
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var context = Context();

        using var pair = new Pair(limits, start);

        Assert.Equal(JoinOutcome.Pending, pair.Pump(context, start, Hello()));
        Assert.Equal(JoinOutcome.Pending, pair.Pump(context, start, ReserveSlot("squatter")));

        // It is holding a seat now — which is exactly what makes the squat worth
        // doing, and what OccupiedSlots counts against the player limit.
        Assert.True(pair.Joining.OccupiesSlot);

        var now = start;
        var outcome = JoinOutcome.Pending;

        for (var i = 0; i < 30 && outcome == JoinOutcome.Pending; i++)
        {
            now = now.AddSeconds(20);
            outcome = pair.Pump(context, now, [(byte)MessageType.Ping]);
        }

        Assert.Equal(JoinOutcome.Finished, outcome);
        Assert.Equal("handshake took too long", pair.Joining.Connection.CloseReason);

        // And it gave up when the deadline said so, not before: a deadline that
        // fired early would be a timeout for slow real clients.
        Assert.True(now - start >= limits.MaxHandshakeDuration);
    }

    /// <summary>
    /// ⚠️ <b>The control case.</b> The test above asserts a connection died,
    /// which is the shape that also passes if the deadline kills everything. A
    /// handshake that finishes promptly must be unaffected — and it is the same
    /// clock, the same limits and the same code path.
    /// </summary>
    [Fact]
    public void AnOrdinaryHandshakeIsUnaffectedByTheDeadline()
    {
        var limits = ConnectionLimits.Default with { MaxHandshakeDuration = TimeSpan.FromMinutes(2) };
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var context = Context();

        using var pair = new Pair(limits, start);

        Assert.Equal(JoinOutcome.Pending, pair.Pump(context, start, Hello()));
        Assert.Equal(JoinOutcome.Pending, pair.Pump(context, start, ReserveSlot("player")));

        // A second later, as a real client would.
        var joined = pair.Pump(context, start.AddSeconds(1), [(byte)MessageType.PlayerJoin]);

        Assert.Equal(JoinOutcome.Joined, joined);
        Assert.Equal("player", pair.Joining.Name);
        Assert.False(pair.Joining.Connection.IsClosed);
    }

    /// <summary>
    /// The reference's idle timeout still works, and is still the one that
    /// catches a client that simply stops.
    /// </summary>
    /// <remarks>
    /// The deadline is deliberately much longer, so this is what ends the common
    /// case — somebody whose connection dropped mid-handshake — in thirty
    /// seconds rather than ten minutes. Both clocks are needed; neither replaces
    /// the other.
    /// </remarks>
    [Fact]
    public void TheReferencesIdleTimeoutStillEndsAConnectionThatGoesQuiet()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var context = Context();

        using var pair = new Pair(ConnectionLimits.Default, start);

        Assert.Equal(JoinOutcome.Pending, pair.Pump(context, start, Hello()));

        var outcome = pair.Pump(context, start + JoiningConnection.Timeout + TimeSpan.FromSeconds(1));

        Assert.Equal(JoinOutcome.Finished, outcome);
        Assert.Equal("handshake timed out", pair.Joining.Connection.CloseReason);
    }
}

/// <summary>
/// The token bucket over client-driven log lines.
/// </summary>
/// <remarks>
/// ⚠️ <b>Writing a log line happens on the tick thread and is synchronous.</b>
/// A line per refused connection during a connection flood makes the defence
/// the outage.
/// </remarks>
public sealed class LogThrottleTests
{
    /// <summary>
    /// The normal case passes untouched: a burst is what a server emptying and
    /// refilling looks like, and reporting it as "1 line and 5 suppressed" would
    /// be hiding ordinary activity.
    /// </summary>
    [Fact]
    public void ABurstWithinTheAllowanceGoesThroughUnannotated()
    {
        var throttle = new LogThrottle(burst: 5, per: TimeSpan.FromSeconds(10));

        for (var i = 0; i < 5; i++)
        {
            Assert.True(throttle.ShouldLog(1000, out var suppressed), $"line {i} should pass");
            Assert.Equal(0, suppressed);
        }
    }

    /// <summary>Past the allowance, and at the same instant, the rest are held.</summary>
    [Fact]
    public void PastTheAllowanceTheRestAreHeldAndCounted()
    {
        var throttle = new LogThrottle(burst: 2, per: TimeSpan.FromSeconds(10));

        Assert.True(throttle.ShouldLog(0, out _));
        Assert.True(throttle.ShouldLog(0, out _));

        for (var i = 0; i < 100; i++)
            Assert.False(throttle.ShouldLog(0, out _));

        Assert.Equal(100, throttle.Suppressed);
    }

    /// <summary>
    /// ⚠️ <b>What was held back is reported, never silently dropped.</b> A log
    /// that quietly omits the evidence during the incident being investigated
    /// makes a flood look like a handful of connections — which is worse than a
    /// noisy log, because it is wrong rather than merely long.
    /// </summary>
    [Fact]
    public void TheSuppressedCountSurvivesToTheNextLineThatGetsThrough()
    {
        var throttle = new LogThrottle(burst: 1, per: TimeSpan.FromSeconds(10));

        Assert.True(throttle.ShouldLog(0, out _));

        for (var i = 0; i < 42; i++)
            Assert.False(throttle.ShouldLog(0, out _));

        // Ten seconds later the bucket is full again.
        Assert.True(throttle.ShouldLog(10_000, out var suppressed));
        Assert.Equal(42, suppressed);

        // And the count is reset, so the next line does not re-report it.
        Assert.True(throttle.ShouldLog(20_000, out var again));
        Assert.Equal(0, again);
    }

    /// <summary>
    /// A clock that steps backwards cannot be used to drain the bucket.
    /// </summary>
    /// <remarks>
    /// The server passes <c>Environment.TickCount64</c>, which is monotonic, so
    /// this is defence in depth rather than a case that arises — but a throttle
    /// that can be disabled by a clock is not a throttle.
    /// </remarks>
    [Fact]
    public void AClockThatGoesBackwardsDoesNotBreakIt()
    {
        var throttle = new LogThrottle(burst: 1, per: TimeSpan.FromSeconds(10));

        Assert.True(throttle.ShouldLog(50_000, out _));
        Assert.False(throttle.ShouldLog(40_000, out _));
        Assert.False(throttle.ShouldLog(0, out _));
    }
}
