using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using GG2.Host;
using GG2.Protocol;
using GG2.Server;

namespace GG2.Tests;

/// <summary>
/// <see cref="ServerOptions.AuthServerUrls"/>: parsing, the command line and
/// config file, and <see cref="RewardAuthClient"/> asking several auth servers
/// at once and taking the first listed one's answer.
/// </summary>
public sealed class RewardAuthTargetsTests
{
    [Theory]
    [InlineData("tcp://auth.example:45099", "auth.example", 45099)]
    [InlineData("tcp://auth.example", "auth.example", 45022)]
    [InlineData("auth.example:45099", "auth.example", 45099)]
    [InlineData("auth.example", "auth.example", 45022)]
    [InlineData("127.0.0.1:1234", "127.0.0.1", 1234)]
    [InlineData("[::1]:1234", "::1", 1234)]
    [InlineData("tcp://[::1]", "::1", 45022)]
    public void AnEntryNamesAHostAndPort(string entry, string host, int port)
    {
        Assert.True(RewardAuthTargets.TryParse(entry, out var parsedHost, out var parsedPort));
        Assert.Equal(host, parsedHost);
        Assert.Equal(port, parsedPort);
    }

    [Theory]
    [InlineData("http://auth.example")]
    [InlineData("tcp://auth.example/path")]
    [InlineData("tcp://")]
    [InlineData("auth.example:notaport")]
    public void ANonAuthServerIsRefused(string entry)
    {
        var thrown = Assert.Throws<FormatException>(() => RewardAuthTargets.Parse("Settings.AuthServerUrls", entry));
        Assert.Contains("Settings.AuthServerUrls", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDefaultIsTheOriginalThenGg2ServerNet()
    {
        Assert.Equal(["tcp://ganggarrison.com:45022", "tcp://gg2server.net:45022"], new ServerOptions().AuthServerUrls);

        // Naming the list is not agreeing to relay: --rewards stays the switch.
        Assert.False(new ServerOptions().Rewards);
    }

    [Fact]
    public void AuthServerReplacesThenAppendsAndDoesNotImplyRewards()
    {
        var options = CommandLine.Parse(["--auth-server", "a.example,tcp://b.example:1", "--auth-server", "c.example"]);

        Assert.Equal(["a.example", "tcp://b.example:1", "c.example"], options.AuthServerUrls);
        Assert.False(options.Rewards);
    }

    [Fact]
    public void TheConfigFileReadsAList()
    {
        var options = new ServerOptions();
        ServerConfigFile.ApplyText(options, """{"Settings": {"Rewards": true, "AuthServerUrls": "tcp://a.example:1, b.example"}}""", "test.json");

        Assert.True(options.Rewards);
        Assert.Equal(["tcp://a.example:1", "b.example"], options.AuthServerUrls);
    }

    /// <summary>
    /// The first server in the list decides, even when a later one answers
    /// first: list order is priority, not arrival order.
    /// </summary>
    [Fact]
    public void TheFirstListedAnswerWinsEvenWhenItIsSlower()
    {
        using var slow = new StubAuthServer("Badge_Dev", TimeSpan.FromMilliseconds(600));
        using var fast = new StubAuthServer("GoldSc", TimeSpan.Zero);

        Assert.Equal("Badge_Dev", Check([slow, fast]).Rewards);

        // Both were asked: in parallel, not one after the other.
        Assert.Equal(1, fast.Requests);
    }

    /// <summary>
    /// An empty reply means only "not my user", so the next server's answer is
    /// used.
    /// </summary>
    [Fact]
    public void AnEmptyReplyFallsThroughToTheNextServer()
    {
        using var empty = new StubAuthServer(string.Empty, TimeSpan.Zero);
        using var ours = new StubAuthServer("GoldSc", TimeSpan.Zero);

        Assert.Equal("GoldSc", Check([empty, ours]).Rewards);
    }

    [Fact]
    public void ARefusedServerFallsThroughToTheNextServer()
    {
        var refused = new TcpListener(IPAddress.Loopback, 0);
        refused.Start();
        var deadPort = ((IPEndPoint)refused.LocalEndpoint).Port;
        refused.Stop();

        using var ours = new StubAuthServer("GoldSc", TimeSpan.Zero);
        var log = new ConcurrentLog();

        var result = Check([("127.0.0.1", deadPort), ("127.0.0.1", ours.Port)], log);

        Assert.Equal("GoldSc", result.Rewards);
        Assert.Contains(log.Lines, line => line.Contains($"via 127.0.0.1:{deadPort} failed", StringComparison.Ordinal));
    }

    [Fact]
    public void NobodyKnowingThePlayerIsAFailure()
    {
        using var a = new StubAuthServer(string.Empty, TimeSpan.Zero);
        using var b = new StubAuthServer(string.Empty, TimeSpan.Zero);

        Assert.Null(Check([a, b]).Rewards);
    }

    /// <summary>
    /// A success at the top of the list does not wait for the rest, so a slow
    /// or dead server lower down costs nothing.
    /// </summary>
    [Fact]
    public void ATopSuccessDoesNotWaitForSlowerServers()
    {
        using var top = new StubAuthServer("Badge_Dev", TimeSpan.Zero);
        using var stuck = new StubAuthServer("GoldSc", TimeSpan.FromSeconds(8));

        var clock = Stopwatch.StartNew();
        Assert.Equal("Badge_Dev", Check([top, stuck]).Rewards);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed.TotalSeconds:0.0}s");
    }

    private static RewardAuthResult Check(StubAuthServer[] servers) =>
        Check([.. servers.Select(s => ("127.0.0.1", s.Port))], new ConcurrentLog());

    private static RewardAuthResult Check((string Host, int Port)[] servers, ConcurrentLog log)
    {
        using var client = new RewardAuthClient(log, servers);
        var player = new RosterEntry { Name = "player" };

        client.Enqueue(player, RewardAuth.BuildAuthRequest(new byte[16], new byte[16], ipCheck: true, "7"));

        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < RewardAuthClient.Timeout + TimeSpan.FromSeconds(10))
        {
            if (client.TryTakeResult(out var result))
            {
                Assert.Same(player, result.Player);
                return result;
            }

            Thread.Sleep(5);
        }

        Assert.Fail("the check never resolved");
        return default;
    }

    private sealed class ConcurrentLog : IServerLog
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public IReadOnlyCollection<string> Lines => _lines;

        public void Info(string message) => _lines.Enqueue(message);
    }
}

/// <summary>
/// An auth server that reads one request, waits, writes a fixed reply and
/// closes. On a real thread, for the reason <c>FakeAuthServer</c> gives.
/// </summary>
internal sealed class StubAuthServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly string _reply;
    private readonly TimeSpan _delay;
    private int _requests;

    public StubAuthServer(string reply, TimeSpan delay)
    {
        _reply = reply;
        _delay = delay;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        new Thread(Accept) { IsBackground = true, Name = "stub-auth" }.Start();
    }

    public int Port { get; }

    public int Requests => Volatile.Read(ref _requests);

    public void Dispose() => _listener.Stop();

    private void Accept()
    {
        try
        {
            while (true)
            {
                var client = _listener.AcceptTcpClient();
                new Thread(() => Serve(client)) { IsBackground = true }.Start();
            }
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException or InvalidOperationException)
        {
            // Disposed.
        }
    }

    private void Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var header = new byte[18];
                stream.ReadExactly(header);
                stream.ReadExactly(new byte[BitConverter.ToUInt16(header, 16)]);
                Interlocked.Increment(ref _requests);

                Thread.Sleep(_delay);
                stream.Write(GG2Text.Encode(_reply));
                client.Client.Shutdown(SocketShutdown.Send);
            }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
            {
                // The client went away, which is what a resolved check does to a slow server.
            }
        }
    }
}
