using System.Net;
using System.Net.Sockets;
using Gg2.Protocol;
using Gg2.Server;

namespace Gg2.Tests;

/// <summary>
/// The Haxxy reward handshake: challenge, relay, broadcast.
/// </summary>
/// <remarks>
/// <c>Documentation/Authentication.txt</c>, <c>Scripts/Rewards/</c> and
/// <c>processClientCommands.gml:320-336</c>.
/// <para>
/// <b>The auth server is a third party and is never contacted from here.</b>
/// The end-to-end test stands up a loopback listener and points
/// <c>--auth-server</c> at it, which is also the only way this flow can be
/// exercised at all — and the reason <c>AuthServerHost</c> is an option rather
/// than a constant.
/// </para>
/// </remarks>
public sealed class RewardAuthTests
{
    /// <summary>
    /// The request the game server sends the auth server, byte for byte.
    /// </summary>
    /// <remarks>
    /// <c>rewardAuthStart.gml</c>. <b>The UUID is big-endian</b> —
    /// <c>parseUuid</c> emits the hex in reading order — so a
    /// <see cref="Guid"/>-derived byte array would be wrong in its first three
    /// fields and the auth server would reject every request. That is the kind of
    /// failure that looks like an outage.
    /// </remarks>
    [Fact]
    public void TheAuthRequestIsUuidLengthResponseChallengeFlagAndId()
    {
        var response = new byte[16];
        var challenge = new byte[16];

        for (var i = 0; i < 16; i++)
        {
            response[i] = (byte)(0xA0 + i);
            challenge[i] = (byte)(0xB0 + i);
        }

        var packet = RewardAuth.BuildAuthRequest(response, challenge, ipCheck: true, "user42");

        Assert.Equal(
            new byte[] { 0x20, 0x5e, 0x2d, 0x84, 0x48, 0x33, 0x89, 0xd4, 0x15, 0xd9, 0xc0, 0x24, 0x96, 0x67, 0xdf, 0x1c },
            packet[..16]);

        // The ushort length covers everything after itself: 16 + 16 + 1 + 6.
        Assert.Equal(39, BitConverter.ToUInt16(packet, 16));

        Assert.Equal(response, packet[18..34]);
        Assert.Equal(challenge, packet[34..50]);
        Assert.Equal(1, packet[50]);
        Assert.Equal("user42", Gg2Text.Decode(packet.AsSpan(51)));
        Assert.Equal(57, packet.Length);
    }

    /// <summary>
    /// An empty body is a failure, not an empty reward set.
    /// </summary>
    /// <remarks>
    /// <c>RewardAuthChecker</c>'s <c>buffer_size(responseBuffer)&gt;0</c>. The two
    /// are indistinguishable on the wire — there is no status code — so the
    /// distinction has to live here, and getting it wrong would broadcast a
    /// <c>REWARD_UPDATE</c> clearing somebody's badges every time the auth server
    /// refused them.
    /// </remarks>
    [Fact]
    public void AnEmptyAuthResponseIsAFailure()
    {
        Assert.Null(RewardAuth.ReadAuthResponse([]));
        Assert.Equal("Badge_Dev", RewardAuth.ReadAuthResponse(Gg2Text.Encode("Badge_Dev")));
    }

    /// <summary>
    /// Challenges are 16 bytes and are not repeated.
    /// </summary>
    /// <remarks>
    /// A predictable or shared challenge is the one thing that would weaken
    /// this handshake: two clients handed the same nonce could replay each
    /// other's answer. The reference draws from GM8's free-running generator;
    /// this draws from a cryptographic one, and <b>neither may draw from
    /// <c>WorldState.Random</c></b>, which is observable simulation.
    /// </remarks>
    [Fact]
    public void ChallengesAreSixteenFreshBytes()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < 64; i++)
        {
            var challenge = RewardAuth.NewChallenge();

            Assert.Equal(RewardAuth.ChallengeBytes, challenge.Length);
            Assert.True(seen.Add(Convert.ToHexString(challenge)), "a challenge repeated");
        }
    }

    /// <summary>
    /// With rewards off, a request is answered with silence.
    /// </summary>
    /// <remarks>
    /// <b>The default, and a deliberate departure from the reference</b>,
    /// which answers unconditionally. Turning it on commits the server to
    /// relaying its players' user ids to a third party, so it is opt-in. The
    /// client neither retries nor blocks — it sends the request once on join and
    /// carries on — so the cost is exactly that no cosmetics appear.
    /// </remarks>
    [Fact]
    public void RewardsOffAnswersNothing()
    {
        using var harness = new ServerHarness();

        var client = harness.JoinClient("player");
        harness.Tick(2);
        client.Take(client.Received.Count);

        SendRewardRequest(client, "user42");
        harness.Tick(4);

        var seen = client.Take(client.Received.Count);

        Assert.DoesNotContain((byte)MessageType.RewardChallengeCode, seen);
        Assert.False(client.IsClosed, "an unanswered request must not drop the client");
    }

    /// <summary>
    /// The whole flow, against a stand-in auth server.
    /// </summary>
    /// <remarks>
    /// Request → challenge → response → auth → <c>REWARD_UPDATE</c> to
    /// everybody, plus the string sticking to the roster so a later joiner's
    /// <c>FULL_UPDATE</c> carries it.
    /// </remarks>
    [Fact]
    public void TheHandshakeReachesEveryClientAndSticks()
    {
        using var auth = new FakeAuthServer("Badge_Dev:GoldSc");

        using var harness = new ServerHarness(options =>
        {
            options.Rewards = true;
            options.AuthServerHost = "127.0.0.1";
            options.AuthServerPort = auth.Port;
        });

        var player = harness.JoinClient("player");
        var watcher = harness.JoinClient("watcher");

        harness.Tick(2);
        player.Take(player.Received.Count);
        watcher.Take(watcher.Received.Count);

        var slot = harness.Server.RosterCount - 2;
        var entry = harness.Server.EntryOf(slot);
        Assert.Equal("player", entry.Name);

        SendRewardRequest(player, "user42");
        harness.TickUntil(() => entry.RewardChallenge is not null, "the challenge to be minted");

        // Read off the server rather than scraped out of the stream. A
        // challenge is 16 random bytes, so there is nothing in it to recognise —
        // a scan for the message byte could land on a coincidental 51 in
        // somebody's position data and the test would be quietly measuring
        // noise. Taking the server's copy makes the search below an exact
        // sequence match instead of a one-byte guess.
        var challenge = entry.RewardChallenge!;
        Assert.Equal(RewardAuth.ChallengeBytes, challenge.Length);

        var stream = player.Take(player.Received.Count);

        Assert.True(
            IndexOfSequence(stream, [(byte)MessageType.RewardChallengeCode, .. challenge]) >= 0,
            "the challenge never reached the client");

        // The client's answer. A real one is hmac_md5(key, challenge + serverIp);
        // this server cannot tell the difference and is not supposed to be able
        // to, which is the point of the design and why any 16 bytes will do here.
        var answer = new byte[RewardAuth.ChallengeBytes];
        Array.Fill(answer, (byte)0x5A);

        player.Send([(byte)MessageType.RewardChallengeResponse, .. answer]);

        // Waits on the SERVER's state rather than on a byte in the stream, so
        // the wait cannot be satisfied by noise and then assert against it.
        WaitFor(harness, () => entry.Rewards.Length > 0, "the auth to come back");

        // What the auth server actually received.
        var request = auth.WaitForRequest();
        Assert.Equal(challenge, request[34..50]);
        Assert.Equal(answer, request[18..34]);
        Assert.Equal(1, request[50]);
        Assert.Equal("user42", Gg2Text.Decode(request.AsSpan(51)));

        // What the other client saw. The whole message is matched as a sequence
        // — type, slot, length, string — so a stray 47 in a position update
        // cannot stand in for it.
        harness.Tick(2);

        var expected = new NetWriter();
        ServerMessages.RewardUpdate(expected, (byte)slot, "Badge_Dev:GoldSc");

        Assert.True(
            IndexOfSequence(watcher.Take(watcher.Received.Count), expected.WrittenSpan.ToArray()) >= 0,
            "the other client was never told");

        // And it is remembered, so a client joining later is told too.
        Assert.Equal("Badge_Dev:GoldSc", entry.Rewards);
    }

    /// <summary>
    /// A response to a challenge that was never issued is ignored, and a second
    /// response never reaches the auth server.
    /// </summary>
    /// <remarks>
    /// <c>rewardAuthStart.gml</c>'s <c>rewardAuthAlreadyStarted</c> and
    /// <c>processClientCommands.gml</c>'s <c>variable_local_exists</c> pair.
    /// <b>Together they are what bounds the outbound traffic a client can
    /// cause</b> — without them one socket can make this server open a connection
    /// to a third party per packet it feels like sending.
    /// </remarks>
    [Fact]
    public void OnlyOneAuthPerPlayerAndNoneWithoutAChallenge()
    {
        using var auth = new FakeAuthServer("Badge_Dev");

        using var harness = new ServerHarness(options =>
        {
            options.Rewards = true;
            options.AuthServerHost = "127.0.0.1";
            options.AuthServerPort = auth.Port;
        });

        var player = harness.JoinClient("player");
        harness.Tick(2);

        var answer = new byte[RewardAuth.ChallengeBytes];

        // No REWARD_REQUEST first, so there is no challenge to answer.
        player.Send([(byte)MessageType.RewardChallengeResponse, .. answer]);
        harness.Tick(10);

        Assert.Equal(0, auth.Connections);

        // Now do it properly, twice.
        SendRewardRequest(player, "user42");
        harness.TickUntil(
            () => harness.Server.EntryOf(harness.Server.RosterCount - 1).RewardChallenge is not null,
            "the challenge");

        player.Send([(byte)MessageType.RewardChallengeResponse, .. answer]);
        player.Send([(byte)MessageType.RewardChallengeResponse, .. answer]);
        WaitFor(harness, () => auth.Connections > 0, "the auth request");

        // Then keep ticking and waiting, because the claim is that a SECOND
        // connection never happens — and "it has not happened yet" is only
        // evidence if the second one had time to.
        for (var i = 0; i < 30; i++)
        {
            harness.Tick(1);
            Thread.Sleep(10);
        }

        Assert.Equal(1, auth.Connections);
    }

    /// <summary>
    /// Ticks the server while waiting in wall-clock time for something the auth
    /// relay does off the tick thread.
    /// </summary>
    /// <remarks>
    /// <b>A tick count is the wrong clock for this.</b> <c>TickUntil</c> can run
    /// 600 ticks in a couple of milliseconds, which on a loaded machine is less
    /// time than the thread pool takes to schedule the pump task at all. The auth
    /// relay is deliberately asynchronous, so a test of it has to wait the way
    /// the outside world does.
    /// </remarks>
    private static void WaitFor(ServerHarness harness, Func<bool> condition, string what)
    {
        for (var i = 0; i < 1500; i++)
        {
            harness.Tick(1);

            if (condition())
                return;

            Thread.Sleep(10);
        }

        Assert.Fail($"gave up after fifteen seconds waiting for {what}");
    }

    private static void SendRewardRequest(LoopbackClient client, string id) =>
        client.Send([(byte)MessageType.RewardRequest, (byte)id.Length, .. Gg2Text.Encode(id)]);

    /// <summary>
    /// Finds a whole message in a broadcast stream.
    /// </summary>
    /// <remarks>
    /// <b>A sequence match rather than a scan for the type byte.</b> The
    /// reward messages share this stream with an <c>INPUTSTATE</c> every tick,
    /// whose position bytes take every value — so a one-byte search finds a false
    /// positive sooner or later and the test becomes a coin flip nobody
    /// investigates. Decoding the stream properly would test the reader instead
    /// of the feature, so the middle course is to match every byte of the message
    /// that is expected.
    /// </remarks>
    private static int IndexOfSequence(byte[] stream, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= stream.Length; i++)
            if (stream.AsSpan(i, needle.Length).SequenceEqual(needle))
                return i;

        return -1;
    }
}

/// <summary>
/// A stand-in for <c>ganggarrison.com:45022</c>.
/// </summary>
/// <remarks>
/// Speaks the one exchange the real service does: read a request, write a reward
/// string, close. <b>Closing is the protocol</b>, not politeness — the
/// response has no length and no terminator, so end-of-stream is the only thing
/// that says where it ends.
/// </remarks>
public sealed class FakeAuthServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly string _rewards;
    private readonly List<byte[]> _requests = [];
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _shutdown = new();

    public FakeAuthServer(string rewards)
    {
        _rewards = rewards;

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();

        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _ = Task.Run(AcceptAsync);
    }

    public int Port { get; }

    public int Connections
    {
        get
        {
            lock (_gate)
                return _requests.Count;
        }
    }

    /// <summary>The first request received, waiting briefly for it to arrive.</summary>
    public byte[] WaitForRequest()
    {
        for (var i = 0; i < 200; i++)
        {
            lock (_gate)
            {
                if (_requests.Count > 0)
                    return _requests[0];
            }

            Thread.Sleep(10);
        }

        throw new TimeoutException("the auth server was never contacted");
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_shutdown.Token).ConfigureAwait(false);
                var stream = client.GetStream();

                // The header is 18 bytes: the UUID and the length. Everything
                // after it is the length's business.
                var header = new byte[18];
                await stream.ReadExactlyAsync(header, _shutdown.Token).ConfigureAwait(false);

                var body = new byte[BitConverter.ToUInt16(header, 16)];
                await stream.ReadExactlyAsync(body, _shutdown.Token).ConfigureAwait(false);

                lock (_gate)
                    _requests.Add([.. header, .. body]);

                await stream.WriteAsync(Gg2Text.Encode(_rewards), _shutdown.Token).ConfigureAwait(false);
                client.Client.Shutdown(SocketShutdown.Send);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or IOException or ObjectDisposedException)
        {
            // Shutdown, or a client that went away.
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _listener.Dispose();
        _shutdown.Dispose();
    }
}
