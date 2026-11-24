using GG2.Protocol;
using GG2.Server;
using GG2.Server.Rooms;
using GG2.Server.Wire;

namespace GG2.Tests;

/// <summary>
/// The two wire protocols: how a HELLO picks one, and that each connection is
/// served in the one it picked. docs/PROTOCOL.md, "Two protocols".
/// </summary>
/// <remarks>
/// ⚠️ <b>Two tests here pin "native v1 is the legacy wire, byte for byte"</b> —
/// <see cref="AtNativeVersion1TheCommandTablesAgree"/> and
/// <see cref="ALegacyAndANativeClientAreSentTheSameBytesAtNativeVersion1"/>.
/// They are meant to go red on the first native change: replace each with a
/// test of what that change does, rather than loosening it.
/// </remarks>
public sealed class WireProtocolTests
{
    // --- identity ---------------------------------------------------------------

    [Fact]
    public void TheNativeUuidBytesAreItsTextForm()
    {
        var fromText = Convert.FromHexString(NativeProtocol.Uuid.Replace("-", string.Empty, StringComparison.Ordinal));
        Assert.Equal(fromText, NativeProtocol.UuidBytes.ToArray());
    }

    [Fact]
    public void TheNativeUuidIsNotTheLegacyOne() =>
        Assert.False(NativeProtocol.UuidBytes.SequenceEqual(GameConstants.ProtocolUuidBytes));

    [Fact]
    public void TheNativeHelloIsTheLegacyHelloWithAnotherUuidAndAVersion()
    {
        var writer = new NetWriter(32);
        NativeProtocol.WriteHello(writer);

        Assert.Equal(NativeProtocol.HelloLength, writer.Count);
        Assert.Equal(LoopbackClient.NativeHelloBytes(), writer.WrittenSpan.ToArray());
        Assert.Equal(NativeProtocol.Version, NativeProtocol.ReadVersion(writer.WrittenSpan[17..]));
    }

    [Fact]
    public void AHelloIsClassifiedByItsUuid()
    {
        var legacy = new NetWriter(17);
        legacy.WriteMessageType(MessageType.Hello);
        legacy.WriteBytes(GameConstants.ProtocolUuidBytes);

        Assert.Equal(HelloKind.Legacy, ProtocolHello.Classify(legacy.WrittenSpan));
        Assert.Equal(HelloKind.Native, ProtocolHello.Classify(LoopbackClient.NativeHelloBytes().AsSpan(0, 17)));

        var wrongUuid = legacy.WrittenSpan.ToArray();
        wrongUuid[^1] ^= 0xFF;
        Assert.Equal(HelloKind.Incompatible, ProtocolHello.Classify(wrongUuid));

        var wrongByte = legacy.WrittenSpan.ToArray();
        wrongByte[0] = (byte)MessageType.Ping;
        Assert.Equal(HelloKind.Incompatible, ProtocolHello.Classify(wrongByte));
    }

    [Fact]
    public void OnlyTheVersionsInTheRangeAreServed()
    {
        Assert.True(NativeProtocol.Supports(NativeProtocol.Version));
        Assert.True(NativeProtocol.Supports(NativeProtocol.MinVersion));
        Assert.False(NativeProtocol.Supports(NativeProtocol.Version + 1));
        Assert.False(NativeProtocol.Supports(0));
    }

    [Fact]
    public void EachProtocolHasItsOwnTableAndEncoder()
    {
        Assert.Same(ClientCommandTable.Legacy, ClientCommandTable.For(WireProtocol.Legacy));
        Assert.Same(ClientCommandTable.Native, ClientCommandTable.For(WireProtocol.Native));
        Assert.NotSame(ClientCommandTable.Legacy, ClientCommandTable.Native);

        Assert.Equal(WireProtocol.Legacy, ServerEncoder.For(WireProtocol.Legacy).Protocol);
        Assert.Equal(WireProtocol.Native, ServerEncoder.For(WireProtocol.Native).Protocol);

        Assert.Same(ClientCommandTable.Legacy, new CommandStream().Table);
    }

    /// <summary>Replace with a test of the first native-only command when it lands.</summary>
    [Fact]
    public void AtNativeVersion1TheCommandTablesAgree()
    {
        Assert.Equal(1, NativeProtocol.Version);

        for (var i = 0; i < 256; i++)
        {
            var command = (byte)i;
            Assert.Equal(ClientCommandTable.Legacy.KindOf(command), ClientCommandTable.Native.KindOf(command));
            Assert.Equal(
                ClientCommandTable.Legacy.FixedLengthOf(command), ClientCommandTable.Native.FixedLengthOf(command));
        }
    }

    // --- the handshake ------------------------------------------------------

    [Fact]
    public void ANativeHelloIsAnsweredWithTheOrdinaryGreeting()
    {
        using var harness = new ServerHarness();
        var client = harness.Connect();

        client.SendNativeHello();
        harness.TickUntil(() => client.Received.Count > 0, "the greeting");

        Assert.Equal(MessageType.Hello, client.TakeMessageType());
        Assert.Equal("test server", client.TakeString1());
        Assert.Equal("ctf_truefort", client.TakeString1());
    }

    [Fact]
    public void ANativeHelloSplitAcrossWritesStillJoins()
    {
        using var harness = new ServerHarness();
        var client = harness.Connect();

        client.SendNativeHello(split: true);
        Join(harness, client, "split");

        Assert.Equal(1, harness.Server.NativeClientCount);
    }

    [Fact]
    public void AVersionThisBuildDoesNotServeIsRefusedLikeAWrongUuid()
    {
        using var harness = new ServerHarness();
        var client = harness.Connect();

        client.SendNativeHello(version: NativeProtocol.Version + 1);
        harness.TickUntil(() => client.Received.Count > 0, "the refusal");

        Assert.Equal(MessageType.IncompatibleProtocol, client.TakeMessageType());
        harness.TickUntil(() => client.IsClosed, "the connection to close");
        Assert.Equal(0, harness.Server.NativeClientCount);
    }

    [Fact]
    public void ANativeClientIsAskedForThePasswordTheUsualWay()
    {
        using var harness = new ServerHarness(o => o.Password = "hunter2");
        var client = harness.Connect();

        client.SendNativeHello();
        harness.TickUntil(() => client.Received.Count > 0, "the password request");
        Assert.Equal(MessageType.PasswordRequest, client.TakeMessageType());

        client.SendPassword("hunter2");
        harness.TickUntil(() => client.Received.Count > 0, "the greeting");
        Assert.Equal(MessageType.Hello, client.TakeMessageType());
    }

    [Fact]
    public void ALegacyHelloStillJoinsAsLegacy()
    {
        using var harness = new ServerHarness();
        var client = harness.Connect();

        client.SendHello();
        Join(harness, client, "stock");

        Assert.Equal(0, harness.Server.NativeClientCount);
        Assert.DoesNotContain(harness.Log.Lines, line => line.Contains("native"));
    }

    // --- the in-game streams --------------------------------------------------

    /// <summary>
    /// A native joiner on a server whose native stream was idle still hears
    /// its own <c>PLAYER_JOIN</c> in the same write as its burst — the
    /// <c>ServerBroadcast</c> rule that a mark on an idle stream is 0 and the
    /// stream starts at the listen.
    /// </summary>
    [Fact]
    public void ANativeJoinerHearsAboutItselfWithItsBurst()
    {
        using var harness = new ServerHarness();
        var client = harness.Connect();

        client.SendNativeHello();
        harness.TickUntil(() => client.Received.Count > 0, "the greeting");
        client.Take(client.Received.Count);

        client.SendReserveSlot("joiner");
        harness.TickUntil(() => client.Received.Count > 0, "the slot reservation");
        client.Take(client.Received.Count);

        client.SendPlayerJoin();
        harness.TickUntil(() => harness.Server.PlayerCount > 1, "the join");

        Assert.Equal(MessageType.JoinUpdate, (MessageType)client.Received[0]);

        byte[] ownJoin = [(byte)MessageType.PlayerJoin, 6, .. "joiner"u8];
        Assert.Equal(ownJoin, client.Received.Skip(client.Received.Count - ownJoin.Length).ToArray());
        Assert.Equal(1, harness.Server.NativeClientCount);
    }

    /// <summary>Replace when the native encoder first overrides a message.</summary>
    [Fact]
    public void ALegacyAndANativeClientAreSentTheSameBytesAtNativeVersion1()
    {
        Assert.Equal(1, NativeProtocol.Version);

        using var harness = new ServerHarness();

        var stock = harness.Connect();
        stock.SendHello();
        Join(harness, stock, "stock");

        var native = harness.Connect();
        native.SendNativeHello();
        Join(harness, native, "native");

        // Both drained at the same tick boundary, so from here every flush
        // goes to both and nothing before it does.
        harness.Tick();
        stock.Take(stock.Received.Count);
        native.Take(native.Received.Count);

        stock.SendChangeTeam(Team.Red);
        native.SendChangeTeam(Team.Blue);
        harness.Tick(40);

        // The last flush can still be in flight to one of them; give loopback a
        // moment rather than ticking, which would only send more.
        for (var i = 0; i < 100 && stock.Received.Count != native.Received.Count; i++)
        {
            Thread.Sleep(5);
            stock.Poll();
            native.Poll();
        }

        Assert.True(stock.Received.Count > 40, $"only {stock.Received.Count} bytes in 40 ticks");
        Assert.Equal(stock.Received.ToArray(), native.Received.ToArray());
    }

    [Fact]
    public void TheNativeStreamStopsWhenTheLastNativeClientLeaves()
    {
        using var harness = new ServerHarness();

        var native = harness.Connect();
        native.SendNativeHello();
        Join(harness, native, "native");
        Assert.Equal(1, harness.Server.NativeClientCount);

        native.SendCommand(MessageType.PlayerLeave);
        harness.TickUntil(() => harness.Server.PlayerCount == 1, "the departure");

        Assert.Equal(0, harness.Server.NativeClientCount);
    }

    // --- the room host's front door ---------------------------------------------

    [Fact]
    public void ARoomHostReadsTheNativeHelloAndTheRoomServesItNative()
    {
        var log = new RecordingLog();
        using var host = new RoomHost(new RoomHostOptions { Port = 0, Prefix = 'W', MaxRooms = 1 }, log);
        host.Start();

        var room = host.CreateRoom(new ServerOptions
        {
            ServerName = "room",
            Map = "ctf_truefort",
            MapDirectory = ReferenceTree.MapsDirectory,
        })!;

        using var client = new LoopbackClient(host.BoundPort);

        void Tick()
        {
            host.Tick();
            client.Poll();
        }

        void TickUntil(Func<bool> condition, string what)
        {
            for (var i = 0; i < 200 && !condition(); i++)
                Tick();

            Assert.True(condition(), what);
        }

        client.SendNativeHello(split: true);
        TickUntil(() => client.Received.Count > 0, "the password request");
        Assert.Equal(MessageType.PasswordRequest, client.TakeMessageType());

        client.SendPassword(room.Secret);
        TickUntil(() => client.Received.Count > 0, "the greeting");
        Assert.Equal(MessageType.Hello, client.TakeMessageType());
        Assert.Equal($"Private room {room!.Code}", client.TakeString1());
        client.Take(client.Received.Count);

        client.SendReserveSlot("guest");
        TickUntil(() => client.Received.Count > 0, "the slot reservation");
        client.SendPlayerJoin();
        TickUntil(() => room.Server.HumanCount == 1 && room.Server.RosterCount == 2, "the join");

        Assert.Equal(1, room.Server.NativeClientCount);
    }

    [Fact]
    public void ARoomHostRefusesANativeVersionItDoesNotServe()
    {
        var log = new RecordingLog();
        using var host = new RoomHost(new RoomHostOptions { Port = 0, Prefix = 'W', MaxRooms = 1 }, log);
        host.Start();

        using var client = new LoopbackClient(host.BoundPort);
        client.SendNativeHello(version: 0);

        for (var i = 0; i < 200 && client.Received.Count == 0; i++)
        {
            host.Tick();
            client.Poll();
        }

        Assert.Equal(MessageType.IncompatibleProtocol, client.TakeMessageType());
    }

    /// <summary>Greeting, slot, join, from just after the HELLO.</summary>
    private static void Join(ServerHarness harness, LoopbackClient client, string name)
    {
        var before = harness.Server.PlayerCount;

        harness.TickUntil(() => client.Received.Count > 0, "the greeting");
        Assert.Equal(MessageType.Hello, client.TakeMessageType());
        client.Take(client.Received.Count);

        client.SendReserveSlot(name);
        harness.TickUntil(() => client.Received.Count > 0, "the slot reservation");
        Assert.Equal(MessageType.ReserveSlot, client.TakeMessageType());

        client.SendPlayerJoin();
        harness.TickUntil(() => harness.Server.PlayerCount > before, "the join");
    }
}
