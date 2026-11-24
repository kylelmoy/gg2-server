using GG2.Protocol;
using GG2.Server;
using GG2.Server.Rooms;
using GG2.Protocol.Rooms;

namespace GG2.Tests;

/// <summary>
/// The room host: many rooms behind one port, routed by password
/// (<c>docs/ROOMS.md</c>, "The room host").
/// </summary>
/// <remarks>
/// Every client here is a real TCP socket to the host's one port, so these
/// prove the routing a stock client meets: HELLO, <c>PASSWORD_REQUEST</c>, the
/// code as the password, then the named room's own greeting.
/// </remarks>
public sealed class RoomHostTests : IDisposable
{
    private readonly RecordingLog _log = new();
    private readonly RoomHost _host;
    private readonly List<LoopbackClient> _clients = [];

    public RoomHostTests()
    {
        _host = new RoomHost(new RoomHostOptions { Port = 0, Prefix = 'W', MaxRooms = 3 }, _log);
        _host.Start();
    }

    private static ServerOptions Room(string name) => new()
    {
        ServerName = name,
        Map = "ctf_truefort",
        MapDirectory = ReferenceTree.MapsDirectory,
    };

    private LoopbackClient Connect()
    {
        var client = new LoopbackClient(_host.BoundPort);
        _clients.Add(client);
        return client;
    }

    private void Tick(int count = 1)
    {
        for (var i = 0; i < count; i++)
        {
            _host.Tick();
            foreach (var client in _clients)
                client.Poll();
        }
    }

    private void TickUntil(Func<bool> condition, string what, int maxTicks = 200)
    {
        for (var i = 0; i < maxTicks; i++)
        {
            if (condition())
                return;
            Tick();
        }

        Assert.Fail($"gave up after {maxTicks} ticks waiting for {what}");
    }

    /// <summary>HELLO, and the front door's password request.</summary>
    private LoopbackClient Knock()
    {
        var client = Connect();
        client.SendHello();
        TickUntil(() => client.Received.Count > 0, "the password request");
        Assert.Equal(MessageType.PasswordRequest, client.TakeMessageType());
        return client;
    }

    /// <summary>Reads a greeting and returns the server name it names.</summary>
    private string Greeting(LoopbackClient client)
    {
        TickUntil(() => client.Received.Count > 0, "the greeting");
        Assert.Equal(MessageType.Hello, client.TakeMessageType());
        var name = client.TakeString1();
        Assert.Equal("ctf_truefort", client.TakeString1());
        client.TakeString1(); // map md5
        client.TakeByte();    // plugins required
        client.TakeString2(); // plugin list
        return name;
    }

    [Fact]
    public void EachCodeReachesItsOwnRoomOnOnePort()
    {
        var a = _host.CreateRoom(Room("room a"))!;
        var b = _host.CreateRoom(Room("room b"))!;

        Assert.NotEqual(a.Code, b.Code);
        Assert.Equal('W', a.Code[0]);
        Assert.Equal(1 + RoomCodes.SecretLength, a.Code.Length);

        var toB = Knock();
        toB.SendPassword(b.Secret);
        Assert.Equal($"Private room {b.Code}", Greeting(toB));

        var toA = Knock();
        toA.SendPassword(a.Secret);
        Assert.Equal($"Private room {a.Code}", Greeting(toA));
    }

    /// <summary>A typed code: the whole code, lower case, with spaces.</summary>
    [Fact]
    public void TheWholeCodeInAnyCaseWorksAsThePassword()
    {
        var room = _host.CreateRoom(Room("typed"))!;

        var client = Knock();
        client.SendPassword($" {room.Code.ToLowerInvariant()} ");
        Assert.Equal($"Private room {room.Code}", Greeting(client));
    }

    [Fact]
    public void APasswordThatNamesNoRoomIsAWrongPassword()
    {
        _host.CreateRoom(Room("real"));

        var client = Knock();
        client.SendPassword("ZZZZZ");
        TickUntil(() => client.Received.Count > 0, "the refusal");

        Assert.Equal(MessageType.PasswordWrong, client.TakeMessageType());
        TickUntil(() => client.IsClosed, "the connection to close");
    }

    [Fact]
    public void AWrongUuidIsRefusedAtTheDoor()
    {
        var client = Connect();
        client.SendHelloWithWrongUuid();
        TickUntil(() => client.Received.Count > 0, "the refusal");

        Assert.Equal(MessageType.IncompatibleProtocol, client.TakeMessageType());
    }

    /// <summary>
    /// A client that sends its next command in the same segment as its password
    /// must not lose it at the hand-over.
    /// </summary>
    [Fact]
    public void BytesAfterThePasswordReachTheRoom()
    {
        var room = _host.CreateRoom(Room("eager"))!;
        var client = Knock();

        var both = new NetWriter(64);
        both.WriteString1(room.Secret);
        both.WriteMessageType(MessageType.ReserveSlot);
        both.WriteString1("eager player");
        client.Send(both.WrittenSpan);

        Assert.Equal($"Private room {room.Code}", Greeting(client));
        TickUntil(() => client.Received.Count > 0, "the slot reservation");
        Assert.Equal(MessageType.ReserveSlot, client.TakeMessageType());
    }

    [Fact]
    public void AFullJoinCompletesThroughTheDoor()
    {
        var room = _host.CreateRoom(Room("joined"))!;
        var client = Knock();
        client.SendPassword(room.Secret);
        Greeting(client);

        client.SendReserveSlot("friend");
        TickUntil(() => client.Received.Count > 0, "the slot reservation");
        Assert.Equal(MessageType.ReserveSlot, client.TakeMessageType());

        client.SendPlayerJoin();
        TickUntil(() => room.Server.HumanCount == 1 && room.Server.RosterCount == 2, "the join");
    }

    [Fact]
    public void AFullHostCreatesNoRoom()
    {
        Assert.NotNull(_host.CreateRoom(Room("1")));
        Assert.NotNull(_host.CreateRoom(Room("2")));
        Assert.NotNull(_host.CreateRoom(Room("3")));
        Assert.Null(_host.CreateRoom(Room("4")));
    }

    [Fact]
    public void ARoomForcesItsPerProcessSurfacesOff()
    {
        var options = Room("quiet");
        options.AnnounceToLobby = true;
        options.LobbyUrls = ["https://lobby.example"];
        options.Password = "chosen by the caller";

        var room = _host.CreateRoom(options)!;

        Assert.False(options.AnnounceToLobby);
        Assert.Empty(options.LobbyUrls);
        Assert.Equal(room.Secret, options.Password);
        Assert.Equal($"Private room {room.Code}", options.ServerName);
        Assert.True(room.Server.IsHosted);
    }

    /// <summary>An empty room ends, and its code stops working.</summary>
    [Fact]
    public void AnEmptyRoomEndsAndFreesItsCode()
    {
        var options = Room("brief");
        options.ExitWhenEmptySeconds = 1;
        var room = _host.CreateRoom(options)!;

        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (_host.Rooms.Count != 0 && clock.Elapsed < TimeSpan.FromSeconds(10))
        {
            Tick();
            Thread.Sleep(20);
        }

        Assert.Empty(_host.Rooms);
        Assert.Null(_host.Find(room.Code));
        Assert.Contains(_log.Lines, line => line.Contains($"room {room.Code} ended: nobody was on it"));
    }

    /// <summary>
    /// A room whose ticks keep throwing ends alone, past its own budget, and the
    /// room beside it carries on (<c>docs/ROOMS.md</c>, "The loop").
    /// </summary>
    [Fact]
    public void ARoomWhoseTicksKeepFailingEndsAlone()
    {
        var broken = _host.CreateRoom(Room("broken"))!;
        var healthy = _host.CreateRoom(Room("healthy"))!;
        var thrown = 0;
        var healthyTicks = 0;

        _host.BeforeRoomTick = room =>
        {
            if (room == healthy)
                healthyTicks++;

            if (room == broken)
            {
                thrown++;
                throw new InvalidOperationException("injected");
            }
        };

        Tick(TickFaultBudget.Allowance);
        Assert.Contains(broken, _host.Rooms);

        Tick();

        Assert.Equal(TickFaultBudget.Allowance + 1, thrown);
        Assert.Equal([healthy], _host.Rooms);
        Assert.Null(_host.Find(broken.Code));
        Assert.Same(healthy, _host.Find(healthy.Code));
        Assert.Contains(_log.Lines, line => line.Contains($"room {broken.Code} ended: its ticks kept failing"));

        Assert.Equal(TickFaultBudget.Allowance + 1, healthyTicks);
        Assert.Equal(0, healthy.Server.TickFaults);
        Tick(3);
        Assert.Equal(TickFaultBudget.Allowance + 4, healthyTicks);
        Assert.Equal(TickFaultBudget.Allowance + 1, thrown);
    }

    /// <summary>
    /// A draining host makes no new room, lets a live one run to its own end,
    /// and is drained once that one has gone.
    /// </summary>
    [Fact]
    public void ADrainingHostWaitsForItsLiveRooms()
    {
        var options = Room("last");
        options.ExitWhenEmptySeconds = 1;
        var room = _host.CreateRoom(options)!;

        _host.Drain("a test");

        Assert.True(_host.Draining);
        Assert.False(_host.Drained);
        Assert.Null(_host.CreateRoom(Room("refused")));
        Assert.Same(room, _host.Find(room.Code));

        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!_host.Drained && clock.Elapsed < TimeSpan.FromSeconds(10))
        {
            Tick();
            Thread.Sleep(20);
        }

        Assert.True(_host.Drained);
        Assert.Contains(_log.Lines, line => line.Contains($"room {room.Code} ended: nobody was on it"));
    }

    /// <summary>
    /// The drain file drains a host it was written after, and not the host a
    /// deploy restarts onto, which starts after the marker.
    /// </summary>
    [Fact]
    public void TheDrainFileDrainsOnlyAHostItPostdates()
    {
        var directory = Directory.CreateTempSubdirectory("gg2-drain-");

        try
        {
            var marker = Path.Combine(directory.FullName, "rooms.drain");
            File.WriteAllText(marker, string.Empty);
            File.SetLastWriteTimeUtc(marker, DateTime.UtcNow.AddMinutes(-1));

            using var host = new RoomHost(new RoomHostOptions { Port = 0, DrainFile = marker }, _log);
            host.Start();

            host.Tick();
            Assert.False(host.Draining);

            File.SetLastWriteTimeUtc(marker, DateTime.UtcNow);

            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!host.Draining && clock.Elapsed < TimeSpan.FromSeconds(5))
            {
                host.Tick();
                Thread.Sleep(20);
            }

            Assert.True(host.Draining);
            Assert.True(host.Drained);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// <see cref="RoomHost.Run"/> returns on its own once drained, which is what
    /// lets <c>gg2-server rooms</c> exit for its unit to restart it.
    /// </summary>
    [Fact]
    public async Task RunReturnsOnceDrained()
    {
        _host.Drain("a test");

        await Task.Run(() => _host.Run(CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Rooms on one map share one graph and one set of next-hop tables, which is
    /// most of what a room would otherwise cost (<c>SharedNav</c>).
    /// </summary>
    [Fact]
    public void RoomsOnOneMapShareTheirNavData()
    {
        ServerOptions WithBots(string name)
        {
            var options = Room(name);
            options.NavCacheDirectory = ReferenceTree.NavCacheDirectory;
            options.Bots = GG2.Bots.BotPopulationOptions.Default with
            {
                Enabled = true, FillToPlayers = 2, MaxBots = 2, MinHumans = 0,
            };
            return options;
        }

        var a = _host.CreateRoom(WithBots("a"))!.Server.Population!;
        var b = _host.CreateRoom(WithBots("b"))!.Server.Population!;

        Assert.NotNull(a.Nav);
        Assert.Same(a.Nav, b.Nav);
        Assert.NotNull(a.Distances);
        Assert.Same(a.Distances, b.Distances);
        Assert.Equal(1, _host.SharedNav.GraphCount);
    }

    /// <summary>
    /// Policy rooms hold the same weights and their own brain.
    /// </summary>
    [Fact]
    public void PolicyRoomsShareTheirNetsButNotTheirBrain()
    {
        ServerOptions Policy(string name)
        {
            var options = Room(name);
            options.BotMode = GG2.Bots.BotMode.Policy;
            return options;
        }

        var a = _host.CreateRoom(Policy("a"))!.Server.Options.Bots.Brain!;
        var b = _host.CreateRoom(Policy("b"))!.Server.Options.Bots.Brain!;

        var netsA = a.Tiers.Cast<GG2.Bots.SimPolicyMicroSpec>().Select(t => t.Network).ToArray();
        var netsB = b.Tiers.Cast<GG2.Bots.SimPolicyMicroSpec>().Select(t => t.Network).ToArray();

        Assert.Equal(MapDirectory.ShippedCheckpoints.Count, netsA.Length);
        Assert.Equal(netsA.Length, _host.SharedPolicies.Count);
        for (var i = 0; i < netsA.Length; i++)
            Assert.Same(netsA[i], netsB[i]);

        Assert.NotSame(a, b);
    }

    [Fact]
    public void CodesAvoidLookAlikes()
    {
        foreach (var c in "0O1IL")
            Assert.DoesNotContain(c, RoomCodes.Alphabet);

        Assert.Equal(31, RoomCodes.Alphabet.Length);
        Assert.Null(RoomCodes.Normalise("ABCD", 'W'));
        Assert.Equal("ABCDE", RoomCodes.Normalise("wabcde", 'W'));
        Assert.Null(RoomCodes.Normalise("XABCDE", 'W'));
    }

    public void Dispose()
    {
        foreach (var client in _clients)
            client.Dispose();

        _host.Dispose();
    }
}
