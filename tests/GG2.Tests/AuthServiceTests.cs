using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using GG2.Auth;
using GG2.Protocol;
using GG2.Server;

namespace GG2.Tests;

/// <summary>
/// gg2-auth, the reward auth server. docs/AUTH.md.
/// </summary>
/// <remarks>
/// <b>Both ends here are this repo's</b>: the requests come from
/// <see cref="RewardAuth.BuildAuthRequest"/> and the client's HMAC is computed
/// by the test, so nothing in this file shows that a stock GM8 client computes
/// the same HMAC. <see cref="TheFrameworkHmacIsTheGmlHmac"/> transcribes the
/// reference's arithmetic to narrow that, and docs/AUTH.md records the live check.
/// </remarks>
public sealed class AuthServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gg2-auth-tests-" + Guid.NewGuid().ToString("N"));

    public AuthServiceTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Db => Path.Combine(_dir, "users.tsv");

    private static readonly byte[] Key = Convert.FromHexString("8a1f33c0d4e5f60718293a4b5c6d7e8f");

    private User SeedUser(string rewards = "Badge_Dev:GoldSc")
    {
        var user = new User("7", Key, rewards, "test");
        UserFile.Save(Db, [user]);
        return user;
    }

    /// <summary>What a stock client sends back: <c>ClientBeginStep.gml:553-561</c>.</summary>
    private static byte[] ClientAnswer(byte[] key, byte[] challenge, string serverIp) =>
        HMACMD5.HashData(key, challenge.Concat(Encoding.Latin1.GetBytes(serverIp)).ToArray());

    [Fact]
    public void ParsesWhatTheGameServerWrites()
    {
        var response = RandomNumberGenerator.GetBytes(16);
        var challenge = RandomNumberGenerator.GetBytes(16);
        var bytes = RewardAuth.BuildAuthRequest(response, challenge, ipCheck: true, "user42");

        Assert.Equal(RequestStatus.Complete, AuthRequest.TryParse(bytes, out var request));
        Assert.Equal(response, request.Response);
        Assert.Equal(challenge, request.Challenge);
        Assert.True(request.IpCheck);
        Assert.Equal("user42", request.RewardId);

        // Every proper prefix is waiting for more, never a verdict.
        for (var n = 0; n < bytes.Length; n++)
            Assert.Equal(RequestStatus.Incomplete, AuthRequest.TryParse(bytes.AsSpan(0, n), out _));

        // Garbage is refused at its first wrong byte, not at the timeout.
        Assert.Equal(RequestStatus.Invalid, AuthRequest.TryParse("GET / HTTP/1.1\r\n"u8, out _));

        var badLength = bytes.ToArray();
        badLength[16] = 0xFF;
        badLength[17] = 0xFF;
        Assert.Equal(RequestStatus.Invalid, AuthRequest.TryParse(badLength, out _));

        var badFlag = bytes.ToArray();
        badFlag[18 + 32] = 2;
        Assert.Equal(RequestStatus.Invalid, AuthRequest.TryParse(badFlag, out _));
    }

    /// <summary>
    /// <c>hmac_md5_bin.gml</c>'s arithmetic, written out, against HMACMD5.
    /// </summary>
    /// <remarks>
    /// Pad the key to 64 bytes with zeros, XOR with 0x36 and 0x5C, and nest two
    /// MD5s. If the reference did anything non-standard this is where it would
    /// show, and the server would reject every stock client.
    /// </remarks>
    [Theory]
    [InlineData(1)]
    [InlineData(16)]
    [InlineData(64)]
    public void TheFrameworkHmacIsTheGmlHmac(int keyLength)
    {
        var key = RandomNumberGenerator.GetBytes(keyLength);
        var challenge = RandomNumberGenerator.GetBytes(16);
        challenge[3] = 0;

        var padded = new byte[64];
        key.CopyTo(padded, 0);
        var inner = padded.Select(b => (byte)(b ^ 0x36)).ToArray();
        var outer = padded.Select(b => (byte)(b ^ 0x5C)).ToArray();
        var message = challenge.Concat(Encoding.Latin1.GetBytes("203.0.113.9")).ToArray();
        var gml = MD5.HashData(outer.Concat(MD5.HashData(inner.Concat(message).ToArray())).ToArray());

        Assert.Equal(gml, AuthRequest.Expected(key, challenge, "203.0.113.9"));
    }

    [Fact]
    public void TheUserFileRoundTripsAndRefusesBadRows()
    {
        var users = new List<User>
        {
            new("1", UserFile.NewKey(), "Badge_Dev", "first"),
            new("2", UserFile.NewKey(), string.Empty, string.Empty),
        };
        UserFile.Save(Db, users);

        var loaded = UserFile.Load(Db);
        Assert.Equal(2, loaded.Count);
        Assert.Equal(users[0].Key, loaded[0].Key);
        Assert.Equal("Badge_Dev", loaded[0].Rewards);
        Assert.Equal(string.Empty, loaded[1].Rewards);
        Assert.Equal("3", UserFile.NextId(loaded));

        File.AppendAllText(Db, "1\tabcd\n");
        Assert.Contains("appears twice", Assert.Throws<FormatException>(() => UserFile.Load(Db)).Message);

        File.WriteAllText(Db, "1\tnot-hex\n");
        Assert.Throws<FormatException>(() => UserFile.Load(Db));

        File.WriteAllText(Db, "1\tabcd\tBad:Name!\n");
        Assert.Throws<FormatException>(() => UserFile.Load(Db));

        Assert.Empty(UserFile.Load(Path.Combine(_dir, "missing.tsv")));
    }

    [Fact]
    public void IssuedKeysAreSixteenBytesWithNoNul()
    {
        for (var i = 0; i < 2000; i++)
        {
            var key = UserFile.NewKey();
            Assert.Equal(UserFile.KeyBytes, key.Length);
            Assert.DoesNotContain((byte)0, key);
        }
    }

    [Fact]
    public void TheStorePicksUpAnEditWithoutARestart()
    {
        var user = SeedUser("Badge_Dev");
        var store = new UserStore(Db, _ => { });
        Assert.Equal("Badge_Dev", store.Find("7")!.Rewards);

        UserFile.Save(Db, [user with { Rewards = "GoldSc" }]);
        // Some filesystems stamp to the second; the length differs here, which
        // is the other half of the change check.
        Assert.Equal("GoldSc", store.Find("7")!.Rewards);

        // A broken edit keeps the last good table.
        File.WriteAllText(Db, "garbage line with no tabs\n");
        Assert.Equal("GoldSc", store.Find("7")!.Rewards);
    }

    [Fact]
    public void ARightAnswerGetsTheRewardsAndEveryWrongOneGetsNothing()
    {
        SeedUser();
        using var server = Start(new AuthOptions());
        var challenge = RandomNumberGenerator.GetBytes(16);

        Assert.Equal("Badge_Dev:GoldSc",
            Ask(server, ClientAnswer(Key, challenge, "127.0.0.1"), challenge, ipCheck: true, "7"));

        // The wrong key, the wrong id, and an answer signed for another server.
        Assert.Equal(string.Empty,
            Ask(server, ClientAnswer(RandomNumberGenerator.GetBytes(16), challenge, "127.0.0.1"), challenge, true, "7"));
        Assert.Equal(string.Empty,
            Ask(server, ClientAnswer(Key, challenge, "127.0.0.1"), challenge, true, "8"));
        Assert.Equal(string.Empty,
            Ask(server, ClientAnswer(Key, challenge, "198.51.100.4"), challenge, true, "7"));
    }

    /// <summary>
    /// A user with nothing enabled is answered with an empty body, which the
    /// reference reads as a failure. That is the only thing an empty set can be
    /// on this wire.
    /// </summary>
    [Fact]
    public void NoRewardsIsAnEmptyBody()
    {
        SeedUser(rewards: string.Empty);
        using var server = Start(new AuthOptions());
        var challenge = RandomNumberGenerator.GetBytes(16);

        Assert.Equal(string.Empty, Ask(server, ClientAnswer(Key, challenge, "127.0.0.1"), challenge, true, "7"));
    }

    /// <summary>
    /// A game server on this machine connects from 127.0.0.1, but its players
    /// signed for its public address.
    /// </summary>
    [Fact]
    public void APublicAddressCoversALocalPeerAndOnlyALocalPeer()
    {
        SeedUser();
        var challenge = RandomNumberGenerator.GetBytes(16);
        var answer = ClientAnswer(Key, challenge, "203.0.113.5");

        using (var plain = Start(new AuthOptions()))
            Assert.Equal(string.Empty, Ask(plain, answer, challenge, true, "7"));

        var options = new AuthOptions { PublicAddresses = ["203.0.113.5"] };
        using (var mapped = Start(options))
            Assert.Equal("Badge_Dev:GoldSc", Ask(mapped, answer, challenge, true, "7"));

        // A peer on the internet is held to its own address alone, or any game
        // server anywhere could replay an answer meant for this one.
        var server = new AuthServer(options with { Port = 0, Bind = IPAddress.Loopback }, new UserStore(Db, _ => { }), _ => { });
        using (server)
        {
            Assert.Equal(["8.8.8.8"], server.CandidateAddresses(IPAddress.Parse("8.8.8.8")));
            Assert.Equal(["192.168.1.20", "203.0.113.5"], server.CandidateAddresses(IPAddress.Parse("192.168.1.20")));
        }

        Assert.True(AuthServer.IsLocal(IPAddress.Parse("10.1.2.3")));
        Assert.True(AuthServer.IsLocal(IPAddress.Parse("172.31.0.1")));
        Assert.True(AuthServer.IsLocal(IPAddress.Parse("100.100.1.1")));
        Assert.False(AuthServer.IsLocal(IPAddress.Parse("172.32.0.1")));
        Assert.False(AuthServer.IsLocal(IPAddress.Parse("203.0.113.5")));
    }

    /// <summary>The reference's host self-auth: <c>GameServerCreate.gml:79-83</c>.</summary>
    [Fact]
    public void IpCheckZeroIsAcceptedUnlessRefused()
    {
        SeedUser();
        var challenge = RandomNumberGenerator.GetBytes(16);
        var answer = HMACMD5.HashData(Key, challenge);

        using (var open = Start(new AuthOptions()))
            Assert.Equal("Badge_Dev:GoldSc", Ask(open, answer, challenge, ipCheck: false, "7"));

        using (var strict = Start(new AuthOptions { AllowWithoutIp = false }))
            Assert.Equal(string.Empty, Ask(strict, answer, challenge, ipCheck: false, "7"));
    }

    [Fact]
    public void GarbageIsClosedWithoutAnAnswer()
    {
        SeedUser();
        using var server = Start(new AuthOptions());

        using var client = new TcpClient();
        client.Connect(IPAddress.Loopback, server.Port);
        client.ReceiveTimeout = 5000;
        var stream = client.GetStream();
        stream.Write("GET / HTTP/1.1\r\n\r\n"u8);

        using var body = new MemoryStream();
        stream.CopyTo(body);
        Assert.Equal(0, body.Length);
    }

    /// <summary>
    /// The whole flow: a client on gg2-server, the relay, gg2-auth, and the
    /// REWARD_UPDATE on the roster.
    /// </summary>
    [Fact]
    public void AGameServerRelaysToItEndToEnd()
    {
        SeedUser();
        using var auth = Start(new AuthOptions());

        using var harness = new ServerHarness(options =>
        {
            options.Rewards = true;
            options.AuthServerHost = "127.0.0.1";
            options.AuthServerPort = auth.Port;
        });

        var player = harness.JoinClient("player");
        harness.Tick(2);
        var entry = harness.Server.EntryOf(harness.Server.RosterCount - 1);
        Assert.Equal("player", entry.Name);

        RewardAuthTests.SendRewardRequest(player, "7");
        harness.TickUntil(() => entry.RewardChallenge is not null, "the challenge to be minted");

        // The client signs for the address it reached the game server at, and
        // the game server reaches gg2-auth from the same loopback address.
        player.Send([(byte)MessageType.RewardChallengeResponse, .. ClientAnswer(Key, entry.RewardChallenge!, "127.0.0.1")]);

        RewardAuthTests.WaitFor(harness, () => entry.Rewards.Length > 0, "gg2-auth to answer");
        Assert.Equal("Badge_Dev:GoldSc", entry.Rewards);
    }

    private AuthServer Start(AuthOptions options) =>
        new(options with { Bind = IPAddress.Loopback, Port = 0 }, new UserStore(Db, _ => { }), _ => { });

    /// <summary>One exchange, as RewardAuthClient makes it: write, read to EOF.</summary>
    private static string Ask(AuthServer server, byte[] answer, byte[] challenge, bool ipCheck, string id)
    {
        using var client = new TcpClient();
        client.Connect(IPAddress.Loopback, server.Port);
        client.ReceiveTimeout = 5000;
        var stream = client.GetStream();
        stream.Write(RewardAuth.BuildAuthRequest(answer, challenge, ipCheck, id));

        using var body = new MemoryStream();
        stream.CopyTo(body);
        return Encoding.Latin1.GetString(body.ToArray());
    }
}
