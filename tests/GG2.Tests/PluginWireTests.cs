using GG2.Protocol;
using GG2.Server;
using GG2.Server.Plugins;

namespace GG2.Tests;

/// <summary>
/// A server with a plugin loaded, over a real socket.
/// </summary>
/// <remarks>
/// <see cref="ChatV2PluginTests"/> covers what the plugin decides;
/// <see cref="PluginPacketTests"/> covers a server with none. This is the seam
/// between them: the greeting that makes a client load the plugin, the container
/// framing in both directions, and which ids are kicked once some are valid.
/// <para>
/// <b>The framing is the part with no symptom.</b> A plugin packet's
/// <c>ushort</c> covers the id byte as well as the data, and a client reads
/// exactly that many bytes before parsing anything else. One off and the client
/// keeps going, reading the next message from the wrong offset, and reports it
/// several messages later as "The Server sent unexpected data".
/// </para>
/// </remarks>
public sealed class PluginWireTests
{
    private const string ChatPlugin = "chat_v2_pr";
    private const string ChatMd5 = "46d0937c9206dcf27e036100d1ea8685";

    private const string ChatV6Plugin = "chat";
    private const string ChatV6Md5 = "bad8081d64f0b808a2bb3a6d88978fb5";

    private static ServerHarness ChatServer(bool required = false) =>
        new(options =>
        {
            options.ServerPluginList = ChatPlugin;
            options.ServerPluginsRequired = required;

            // chat_v2_pr is a second chat; the server's own must be off to list it.
            options.Chat = options.Chat with { Enabled = false };
        });

    /// <summary>
    /// The greeting names the plugin and the exact zip, so the client downloads
    /// the same file the server's implementation was written against.
    /// </summary>
    [Fact]
    public void TheGreetingAdvertisesThePluginAndItsHash()
    {
        using var harness = ChatServer(required: true);
        var client = harness.Connect();

        client.SendHello();
        harness.TickUntil(() => client.Received.Count > 0, "the greeting");

        Assert.Equal(MessageType.Hello, client.TakeMessageType());
        client.TakeString1();   // server name
        client.TakeString1();   // map name
        client.TakeString1();   // map md5

        Assert.Equal(1, client.TakeByte());
        Assert.Equal($"{ChatPlugin}@{ChatMd5}", client.TakeString2());
    }

    /// <summary>
    /// Required is one byte of advertisement and nothing else — the server
    /// does not verify that a client loaded anything, and neither does the
    /// reference. A test that assumed enforcement would be asserting a promise
    /// the protocol cannot keep.
    /// </summary>
    [Fact]
    public void RequiredIsAdvertisedButNotEnforced()
    {
        using var harness = ChatServer(required: true);

        // This client never loads the plugin and never says hello to it.
        var client = harness.JoinClient("refusenik");
        harness.Tick(3);

        Assert.False(client.IsClosed);
        Assert.Equal(2, harness.Server.PlayerCount);
    }

    /// <summary>
    /// A hello for packet id 0 reaches <see cref="ChatV2Plugin"/> and comes back
    /// as a plugin packet the client half can read.
    /// </summary>
    [Fact]
    public void AHelloForAKnownPacketIdIsAnsweredRatherThanKicked()
    {
        using var harness = ChatServer();
        var client = harness.JoinClient("alice");
        Drain(client);

        SendPluginPacket(client, packetId: 0, [0]);
        harness.Tick(3);

        Assert.False(client.IsClosed);
        AssertReceivedPluginPacket(client, 0, "Oalice has joined chat!");
    }

    /// <summary>
    /// Two clients, a global message, and the framing that carries it — the
    /// whole round trip a player sees.
    /// </summary>
    [Fact]
    public void AChatMessageReachesTheOtherClient()
    {
        using var harness = ChatServer();
        var alice = harness.JoinClient("alice");
        var bob = harness.JoinClient("bob");

        SendPluginPacket(alice, 0, [0]);
        SendPluginPacket(bob, 0, [0]);
        harness.Tick(3);
        Drain(alice);
        Drain(bob);

        // event3's client format: kind 1 (global), a length, the bytes.
        SendPluginPacket(alice, 0, [1, 5, .. GG2Text.Encode("hello")]);
        harness.Tick(3);

        // Spectators until they pick a team, so the colour is W.
        AssertReceivedPluginPacket(bob, 0, "Walice: #Whello");
        AssertReceivedPluginPacket(alice, 0, "Walice: #Whello");
    }

    /// <summary>
    /// With one plugin loaded, id 0 is valid and every other id is not. The
    /// kick is unchanged from a server with no plugins; only the boundary moved.
    /// </summary>
    [Fact]
    public void AnIdAboveTheAdvertisedListStillKicks()
    {
        using var harness = ChatServer();
        var client = harness.JoinClient("alice");
        Drain(client);

        SendPluginPacket(client, packetId: 1, [0]);
        harness.TickUntil(() => client.IsClosed, "the kick and disconnect");

        var received = client.Received;
        Assert.Equal((byte)MessageType.Kick, received[received.Count - 2]);
        Assert.Equal((byte)KickReason.BadPluginPacket, received[received.Count - 1]);
    }

    /// <summary>
    /// A plugin with no C# implementation is advertised anyway, and its packets
    /// are discarded rather than kicked.
    /// </summary>
    /// <remarks>
    /// <b>A divergence, deliberately.</b> The reference would have downloaded
    /// and run the plugin's GML on the server too. Kicking instead would punish a
    /// client for loading exactly what this server asked it to load, which is
    /// worse; the startup log names the gap, and <see cref="PluginListTests"/>
    /// gates that it does.
    /// </remarks>
    [Fact]
    public void APluginWithNoImplementationIsAdvertisedAndItsPacketsAreDiscarded()
    {
        var hash = new string('d', 32);
        using var harness = new ServerHarness(options =>
            options.ServerPluginList = $"some_other_plugin@{hash}");

        var client = harness.JoinClient("alice");

        // The server's own chat is appended, so the listed plugin keeps id 0.
        Assert.Equal($"some_other_plugin@{hash},{ChatV6Plugin}@{ChatV6Md5}", harness.Server.AdvertisedPlugins);

        Drain(client);
        SendPluginPacket(client, packetId: 0, [1, 2, 3]);
        harness.Tick(3);

        Assert.False(client.IsClosed);
    }

    /// <summary>
    /// <c>chat</c> named in the list keeps its place there, and is not advertised
    /// twice: the position is its packet id on both sides.
    /// </summary>
    [Fact]
    public void ChatNamedInTheListKeepsItsPlaceAndIsNotAdvertisedTwice()
    {
        var hash = new string('d', 32);
        using var harness = new ServerHarness(options =>
            options.ServerPluginList = $"{ChatV6Plugin},some_other_plugin@{hash}");

        harness.JoinClient("alice");

        Assert.Equal($"{ChatV6Plugin}@{ChatV6Md5},some_other_plugin@{hash}", harness.Server.AdvertisedPlugins);
    }

    /// <summary>With the server's chat off, nothing is advertised for it.</summary>
    [Fact]
    public void ChatOffAdvertisesNothing()
    {
        using var harness = new ServerHarness(options => options.Chat = options.Chat with { Enabled = false });

        harness.JoinClient("alice");

        Assert.Equal(string.Empty, harness.Server.AdvertisedPlugins);
        Assert.Null(harness.Server.ChatPluginName);
    }

    /// <summary>
    /// <c>chat</c> listed with chat off would advertise a plugin nothing answers,
    /// and <c>chat_v2_pr</c> with chat on would give a stock client two chats on
    /// one key. Both are refused at startup, not discovered by a player.
    /// </summary>
    [Theory]
    [InlineData("chat", false)]
    [InlineData("chat_v2_pr", true)]
    public void AConflictingChatListIsRefused(string plugins, bool chatOn)
    {
        var options = new ServerOptions
        {
            MapDirectory = "maps",
            ServerPluginList = plugins,
            Chat = new ChatSettings { Enabled = chatOn },
        };

        Assert.Throws<ArgumentException>(options.Validate);
    }

    /// <summary>
    /// The de-duplication the plugin does to names is visible to every client,
    /// plugin or not, because it goes out as an ordinary
    /// <c>PLAYER_CHANGENAME</c>.
    /// </summary>
    [Fact]
    public void ASecondPlayerWithTheSameNameIsRenamedOnTheWire()
    {
        using var harness = ChatServer();
        harness.JoinClient("alice");

        var second = harness.JoinClient("alice");
        harness.Tick(3);

        Assert.Equal("alice", harness.Server.NameOf(1));
        Assert.Equal("alice2", harness.Server.NameOf(2));

        // The rename is an ORDINARY PLAYER_CHANGENAME, not a plugin packet.
        // That is the one thing chat_v2 does that a client without the plugin
        // still sees, and it is why the plugin is not purely additive.
        Assert.True(
            FindSubsequence(
                second.Received,
                [(byte)MessageType.PlayerChangeName, 2, 6, .. GG2Text.Encode("alice2")]),
            "the de-duplicated name was never broadcast");
    }

    // --- the other chat plugin -----------------------------------------------

    /// <summary>
    /// <c>chat</c> is a separate plugin with a separate hash, and the greeting
    /// has to name the exact zip its server half was transcribed from.
    /// </summary>
    [Fact]
    public void TheOtherChatPluginAdvertisesItsOwnHash()
    {
        using var harness = new ServerHarness(options => options.ServerPluginList = ChatV6Plugin);

        harness.JoinClient("alice");

        Assert.Equal($"{ChatV6Plugin}@{ChatV6Md5}", harness.Server.AdvertisedPlugins);
    }

    /// <summary>
    /// <b>A different packet shape from <c>chat_v2_pr</c>'s, over the same
    /// container.</b> Every server-to-client line here starts with a sender byte,
    /// and a line the server composed uses 254. Get that byte wrong and the
    /// client reads the length from the wrong offset and reports it, several
    /// messages later, as "The Server sent unexpected data".
    /// </summary>
    [Fact]
    public void TheOtherChatPluginsAnnouncementCarriesItsSenderByte()
    {
        using var harness = new ServerHarness(options => options.ServerPluginList = ChatV6Plugin);
        var alice = harness.JoinClient("alice");
        SendPluginPacket(alice, packetId: 0, [0]);
        harness.Tick(3);
        Drain(alice);

        harness.JoinClient("bob");
        harness.Tick(3);

        Assert.False(alice.IsClosed);

        var text = GG2Text.Encode("Obob has joined the game.");
        AssertReceivedPluginData(alice, 0, [254, (byte)text.Length, .. text]);
    }

    /// <summary>
    /// One room over a real socket: a v6 client, a native client speaking
    /// behind the <see cref="GG2.Server.ServerChat.Envelope"/> byte on <c>chat</c>'s packet
    /// id, and a client that joined without ever saying hello.
    /// </summary>
    /// <remarks>
    /// The silent client is gg2-client's no-chat join. A GM8 client without the
    /// plugin raises a fatal error on any plugin packet, so the stream it receives
    /// must hold none of what the other two were sent.
    /// </remarks>
    [Fact]
    public void AV6AndANativeClientShareOneRoomAndASilentClientHearsNothing()
    {
        using var harness = new ServerHarness(options => options.ServerPluginList = ChatV6Plugin);
        var alice = harness.JoinClient("alice");
        var bob = harness.JoinClient("bob");
        var carol = harness.JoinClient("carol");

        SendPluginPacket(alice, 0, [0]);
        SendPluginPacket(bob, 0, [GG2.Server.ServerChat.Envelope, 0, GG2.Server.ServerChat.ProtocolVersion, 0]);
        harness.Tick(3);

        // WELCOME: version 1, gravetalk and votes on, a burst of 3, 2 seconds, no motd.
        AssertReceivedPluginData(bob, 0, [GG2.Server.ServerChat.Envelope, 0, 1, 3, 3, 2, 0]);

        SendPluginPacket(alice, 0, [1, 5, .. GG2Text.Encode("hello")]);
        SendPluginPacket(bob, 0, [GG2.Server.ServerChat.Envelope, 1, 0, 2, .. GG2Text.Encode("hi")]);
        harness.Tick(3);

        // alice (slot 1) to bob as a native LINE: envelope, kind, sender, then
        // flags and team, which depend on the round; the name and text are fixed.
        var aliceLine = GG2Text.Encode("\u0005alice\u0005hello");
        Assert.True(FindSubsequence(bob.Received, [GG2.Server.ServerChat.Envelope, 1, 1]));
        Assert.True(FindSubsequence(bob.Received, aliceLine));

        // bob (slot 2) to alice as a v6 line: roster index, audience 0, raw text.
        AssertReceivedPluginData(alice, 0, [2, 0, 2, .. GG2Text.Encode("hi")]);

        Assert.False(FindSubsequence(carol.Received, GG2Text.Encode("hello")));
        Assert.False(FindSubsequence(carol.Received, GG2Text.Encode("\u0002hi")));
        Assert.False(alice.IsClosed || bob.IsClosed || carol.IsClosed);
    }

    /// <summary>
    /// A player's own line carries their roster index, the audience and the
    /// <b>raw</b> text — the client adds the name and the colour.
    /// </summary>
    [Fact]
    public void TheOtherChatPluginRelaysTheRawTextAndTheSpeakersSlot()
    {
        using var harness = new ServerHarness(options => options.ServerPluginList = ChatV6Plugin);
        var alice = harness.JoinClient("alice");
        var bob = harness.JoinClient("bob");

        SendPluginPacket(alice, 0, [0]);
        SendPluginPacket(bob, 0, [0]);
        harness.Tick(3);
        Drain(alice);
        Drain(bob);

        SendPluginPacket(alice, 0, [1, 5, .. GG2Text.Encode("hello")]);
        harness.Tick(3);

        // Slot 1 is alice; 0 is the everyone audience.
        var text = GG2Text.Encode("hello");
        AssertReceivedPluginData(bob, 0, [1, 0, (byte)text.Length, .. text]);
        AssertReceivedPluginData(alice, 0, [1, 0, (byte)text.Length, .. text]);
    }

    // --- helpers -------------------------------------------------------------

    /// <summary>
    /// The client-to-server container: <c>PLUGIN_PACKET</c>, a two-byte length
    /// covering the id and the data, the id, the data.
    /// </summary>
    private static void SendPluginPacket(LoopbackClient client, byte packetId, ReadOnlySpan<byte> data)
    {
        var packet = new byte[4 + data.Length];
        packet[0] = (byte)MessageType.PluginPacket;

        var length = data.Length + 1;
        packet[1] = (byte)(length & 0xFF);
        packet[2] = (byte)(length >> 8);
        packet[3] = packetId;
        data.CopyTo(packet.AsSpan(4));

        client.Send(packet);
    }

    /// <summary>
    /// Asserts the client's stream contains the server-to-client container for
    /// one chat line, byte for byte.
    /// </summary>
    /// <remarks>
    /// Built here rather than parsed, so the assertion is on the exact bytes a
    /// GM8 client reads: the message id, the <c>ushort</c> that covers
    /// <em>id + data</em>, the packet id, then the plugin's own length-prefixed
    /// string. Searching for the subsequence rather than reading from the head
    /// keeps it independent of the join burst still arriving.
    /// </remarks>
    private static void AssertReceivedPluginPacket(LoopbackClient client, byte packetId, string message)
    {
        var text = GG2Text.Encode(message);
        var data = new byte[1 + text.Length];
        data[0] = (byte)text.Length;
        text.CopyTo(data, 1);

        AssertReceivedPluginData(client, packetId, data);
    }

    /// <summary>The same, for a plugin body that is not just a length-prefixed string.</summary>
    private static void AssertReceivedPluginData(LoopbackClient client, byte packetId, ReadOnlySpan<byte> data)
    {
        var length = data.Length + 1;
        var expected = new byte[4 + data.Length];
        expected[0] = (byte)MessageType.PluginPacket;
        expected[1] = (byte)(length & 0xFF);
        expected[2] = (byte)(length >> 8);
        expected[3] = packetId;
        data.CopyTo(expected.AsSpan(4));

        Assert.True(
            FindSubsequence(client.Received, expected),
            "the stream does not contain the expected plugin packet");
    }

    private static bool FindSubsequence(IReadOnlyList<byte> haystack, ReadOnlySpan<byte> needle)
    {
        for (var start = 0; start + needle.Length <= haystack.Count; start++)
        {
            var match = true;
            for (var i = 0; i < needle.Length; i++)
            {
                if (haystack[start + i] != needle[i])
                {
                    match = false;
                    break;
                }
            }

            if (match)
                return true;
        }

        return false;
    }

    private static void Drain(LoopbackClient client)
    {
        if (client.Received.Count > 0)
            client.Take(client.Received.Count);
    }
}
