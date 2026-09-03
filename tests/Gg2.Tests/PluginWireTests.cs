using Gg2.Protocol;
using Gg2.Server.Plugins;

namespace Gg2.Tests;

/// <summary>
/// A server with a plugin loaded, over a real socket.
/// </summary>
/// <remarks>
/// <see cref="ChatV2PluginTests"/> covers what the plugin decides;
/// <see cref="PluginPacketTests"/> covers a server with none. This is the seam
/// between them: the greeting that makes a client load the plugin, the container
/// framing in both directions, and which ids are kicked once some are valid.
/// <para>
/// ⚠️ <b>The framing is the part with no symptom.</b> A plugin packet's
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

    private static ServerHarness ChatServer(bool required = false) =>
        new(options =>
        {
            options.ServerPluginList = ChatPlugin;
            options.ServerPluginsRequired = required;
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
    /// ⚠️ Required is one byte of advertisement and nothing else — the server
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
        SendPluginPacket(alice, 0, [1, 5, .. Gg2Text.Encode("hello")]);
        harness.Tick(3);

        // Spectators until they pick a team, so the colour is W.
        AssertReceivedPluginPacket(bob, 0, "Walice: #Whello");
        AssertReceivedPluginPacket(alice, 0, "Walice: #Whello");
    }

    /// <summary>
    /// ⚠️ With one plugin loaded, id 0 is valid and every other id is not. The
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
    /// ⛔ <b>A divergence, deliberately.</b> The reference would have downloaded
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
        Assert.Equal($"some_other_plugin@{hash}", harness.Server.AdvertisedPlugins);

        Drain(client);
        SendPluginPacket(client, packetId: 0, [1, 2, 3]);
        harness.Tick(3);

        Assert.False(client.IsClosed);
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

        // ⚠️ The rename is an ORDINARY PLAYER_CHANGENAME, not a plugin packet.
        // That is the one thing chat_v2 does that a client without the plugin
        // still sees, and it is why the plugin is not purely additive.
        Assert.True(
            FindSubsequence(
                second.Received,
                [(byte)MessageType.PlayerChangeName, 2, 6, .. Gg2Text.Encode("alice2")]),
            "the de-duplicated name was never broadcast");
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
    /// ⚠️ Built here rather than parsed, so the assertion is on the exact bytes a
    /// GM8 client reads: the message id, the <c>ushort</c> that covers
    /// <em>id + data</em>, the packet id, then the plugin's own length-prefixed
    /// string. Searching for the subsequence rather than reading from the head
    /// keeps it independent of the join burst still arriving.
    /// </remarks>
    private static void AssertReceivedPluginPacket(LoopbackClient client, byte packetId, string message)
    {
        var text = Gg2Text.Encode(message);
        var data = new byte[1 + text.Length];
        data[0] = (byte)text.Length;
        text.CopyTo(data, 1);

        var length = data.Length + 1;
        var expected = new byte[4 + data.Length];
        expected[0] = (byte)MessageType.PluginPacket;
        expected[1] = (byte)(length & 0xFF);
        expected[2] = (byte)(length >> 8);
        expected[3] = packetId;
        data.CopyTo(expected, 4);

        Assert.True(
            FindSubsequence(client.Received, expected),
            $"the stream does not contain a plugin packet carrying \"{message}\"");
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
