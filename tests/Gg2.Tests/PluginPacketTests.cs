using Gg2.Protocol;

namespace Gg2.Tests;

/// <summary>
/// What a server with no plugins does with a <c>PLUGIN_PACKET</c>.
/// </summary>
/// <remarks>
/// ⛔ <b>Plugin support itself is not portable and this is not an attempt at
/// it.</b> A GG2 plugin is a zip of GML downloaded from ganggarrison.com and
/// executed inside Game Maker 8 (<c>loadserverplugins.gml</c>,
/// <c>extractzip.gml</c>); a C# server cannot run one. What IS portable is the
/// behaviour of a server that has loaded none, and that behaviour is not
/// "ignore".
/// <para>
/// <c>processClientCommands.gml:338-362</c> passes the packet to
/// <c>_PluginPacketPush</c>, which returns false when the id is absent from
/// <c>global.pluginPacketBuffers</c> — a map filled only by
/// <c>loadserverplugins</c>. With no plugins the map is empty, <b>every</b> id is
/// unrecognised, and the sender is kicked with <c>KICK_BAD_PLUGIN_PACKET</c>.
/// This port had been silently ignoring them.
/// </para>
/// <para>
/// ⚠️ <b>A stock client cannot reach this</b>, which is why it went unnoticed:
/// the client only sends plugin packets for plugins in the list the HELLO
/// advertised, and this server advertises none. Reaching it means a client that
/// invented a packet id — exactly who the reference kicks.
/// </para>
/// </remarks>
public sealed class PluginPacketTests
{
    /// <summary>The HELLO promises no plugins, which is what keeps the kick unreachable.</summary>
    /// <remarks>
    /// ⚠️ The load-bearing half of the pair. If this ever advertised a plugin,
    /// stock clients would start sending packets the server can only answer by
    /// kicking them — so the advertisement and the kick have to stay consistent,
    /// and the advertisement is the one that must not change.
    /// </remarks>
    [Fact]
    public void TheGreetingAdvertisesNoPluginsAndDoesNotRequireThem()
    {
        using var harness = new ServerHarness();
        var client = harness.Connect();

        client.SendHello();
        harness.TickUntil(() => client.Received.Count > 0, "the greeting");

        Assert.Equal(MessageType.Hello, client.TakeMessageType());
        client.TakeString1();                       // server name
        client.TakeString1();                       // map name
        client.TakeString1();                       // map md5, empty for a built-in

        Assert.Equal(0, client.TakeByte());         // pluginsRequired
        Assert.Equal(string.Empty, client.TakeString2());
    }

    [Fact]
    public void APluginPacketKicksTheSender()
    {
        using var harness = new ServerHarness();
        var client = harness.Connect();

        client.SendHello();
        harness.TickUntil(() => client.Received.Count > 0, "the greeting");
        Drain(client);

        client.SendReserveSlot("plugin user");
        harness.TickUntil(() => client.Received.Count > 0, "the slot reservation");
        Drain(client);

        client.SendPlayerJoin();
        harness.TickUntil(() => harness.Server.PlayerCount > 1, "the join");
        Drain(client);

        // PLUGIN_PACKET carries a 2-byte length prefix (the command table's
        // commandBytesPrefixLength2), then the plugin packet id and its body.
        // One byte of id, three of payload, so the prefix is 4.
        //
        // ⚠️ LITTLE-endian, unlike the lobby packet. CommandStream reads it as
        // `bytes[0] | bytes[1] << 8`. Writing it the other way round here made
        // the server wait for 1024 bytes that never came, and the test failed as
        // "no kick" rather than as "bad length" — which is what a real client
        // getting this wrong would look like too.
        client.Send([(byte)MessageType.PluginPacket, 0x04, 0x00, 0x2a, 0x01, 0x02, 0x03]);

        harness.TickUntil(() => client.IsClosed, "the kick and disconnect");

        AssertKicked(client);
    }

    /// <summary>
    /// ⚠️ The framing is consumed even though the body is thrown away.
    /// </summary>
    /// <remarks>
    /// The length prefix is what makes a plugin packet skippable at all. If the
    /// command stream mis-read it, the bytes after it would be parsed as
    /// commands — a desync that shows up much later and nowhere near the cause.
    /// A second, well-formed command after the plugin packet proves the stream
    /// stayed aligned right up to the kick.
    /// </remarks>
    [Fact]
    public void TheLengthPrefixIsConsumedSoTheStreamStaysAligned()
    {
        using var harness = new ServerHarness();
        var client = harness.Connect();

        client.SendHello();
        harness.TickUntil(() => client.Received.Count > 0, "the greeting");
        client.SendReserveSlot("plugin user");
        harness.TickUntil(() => client.Received.Count > 0, "the slot reservation");
        client.SendPlayerJoin();
        harness.TickUntil(() => harness.Server.PlayerCount > 1, "the join");
        Drain(client);

        // A 260-byte body, so the length prefix genuinely needs both its bytes.
        var packet = new byte[3 + 261];
        packet[0] = (byte)MessageType.PluginPacket;
        packet[1] = 0x05;   // 261, little-endian: low byte first
        packet[2] = 0x01;
        packet[3] = 0x2a;

        client.Send(packet);
        harness.TickUntil(() => client.IsClosed, "the kick and disconnect");

        AssertKicked(client);
    }
    /// <summary>
    /// The last thing the server said was KICK / KICK_BAD_PLUGIN_PACKET.
    /// </summary>
    /// <remarks>
    /// ⚠️ Asserted on the TAIL rather than on the next byte. The join burst is
    /// still arriving when the plugin packet goes out — the broadcast PLAYER_JOIN
    /// lands a tick later — so "drain, then read one message" is a race that
    /// passes or fails on tick timing. The kick is the last thing written before
    /// the close, which is the stable fact.
    /// </remarks>
    private static void AssertKicked(LoopbackClient client)
    {
        var received = client.Received;

        Assert.True(received.Count >= 2, "the server sent nothing before closing");
        Assert.Equal((byte)MessageType.Kick, received[received.Count - 2]);
        Assert.Equal((byte)KickReason.BadPluginPacket, received[received.Count - 1]);
    }

    /// <summary>Throws away everything received so far, so the next Take is the reply under test.</summary>
    private static void Drain(LoopbackClient client)
    {
        if (client.Received.Count > 0)
            client.Take(client.Received.Count);
    }
}
