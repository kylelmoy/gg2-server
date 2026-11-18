using System.Text.Json;
using GG2.Protocol;
using GG2.Server;

namespace GG2.Tests;

/// <summary>
/// The panel's chat box: <see cref="GameServer.Say"/> and the <c>say</c> verb.
/// </summary>
/// <remarks>
/// The plugin paths are in <c>ChatBridgeTests</c> and <c>ChatV2PluginTests</c>;
/// this is the server with no chat plugin, which is the default, and where the
/// line has to go out as a <c>MESSAGE_STRING</c> or reach nobody at all.
/// </remarks>
public sealed class ServerSayTests
{
    [Fact]
    public void WithoutAChatPluginTheLineIsANoticeEveryClientReceives()
    {
        using var harness = new ServerHarness(options => options.Chat = options.Chat with { Enabled = false });
        var client = harness.JoinClient("alice");
        var before = client.Received.Count;

        var reply = JsonDocument.Parse(
            ControlCommands.Dispatch(harness.Server, """{"cmd":"say","text":"  round two in a minute  "}"""))
            .RootElement;

        Assert.True(reply.GetProperty("ok").GetBoolean());
        Assert.Equal("notice", reply.GetProperty("via").GetString());

        var expected = new List<byte> { (byte)MessageType.MessageString };
        var text = GG2Text.Encode($"{harness.Options.ServerPlayerName}: round two in a minute");
        expected.Add((byte)text.Length);
        expected.AddRange(text);

        harness.TickUntil(
            () => IndexOf(client.Received, before, expected) >= 0,
            "the MESSAGE_STRING");
    }

    [Theory]
    [InlineData("""{"cmd":"say","text":"   "}""")]
    [InlineData("""{"cmd":"say"}""")]
    public void NothingToSayIsRefused(string command)
    {
        using var harness = new ServerHarness();

        var reply = JsonDocument.Parse(ControlCommands.Dispatch(harness.Server, command)).RootElement;

        Assert.False(reply.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void ALineLongerThanTheWireAllowsIsRefusedRatherThanCut()
    {
        using var harness = new ServerHarness();
        var command = JsonSerializer.Serialize(new { cmd = "say", text = new string('a', GameServer.MaxSayLength + 1) });

        var reply = JsonDocument.Parse(ControlCommands.Dispatch(harness.Server, command)).RootElement;

        Assert.False(reply.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void TheHistoryKeepsTheNewestLinesAndCountsOnFromWhereTheCallerWas()
    {
        var history = new ChatHistory();

        for (var i = 0; i < ChatHistory.Capacity + 5; i++)
            history.Add("alice", Team.Red, $"line {i}", teamOnly: false);

        var all = history.Since(-1);
        Assert.Equal(ChatHistory.Capacity, all.Count);
        Assert.Equal("line 5", all[0].Text);

        var tail = history.Since(ChatHistory.Capacity + 2);
        Assert.Equal(["line 203", "line 204"], tail.Select(entry => entry.Text));
    }

    private static int IndexOf(IReadOnlyList<byte> haystack, int from, List<byte> needle)
    {
        for (var i = from; i + needle.Count <= haystack.Count; i++)
        {
            var match = true;

            for (var j = 0; j < needle.Count && match; j++)
                match = haystack[i + j] == needle[j];

            if (match)
                return i;
        }

        return -1;
    }
}
