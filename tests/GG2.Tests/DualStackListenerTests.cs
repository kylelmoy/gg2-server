using System.Net;
using GG2.Server;

namespace GG2.Tests;

/// <summary>
/// The game port listens on both address families, as the stock GM8 server
/// does, and an IPv4 peer still reads as plain IPv4 everywhere.
/// </summary>
/// <remarks>
/// The mapping is the part with teeth: on a dual-mode socket an IPv4 peer
/// arrives as <c>::ffff:127.0.0.1</c>, and <see cref="IpRange"/> never maps
/// across families, so without <c>ClientConnection</c>'s normalisation every
/// IPv4 ban entry would silently stop matching. These assert on the server's
/// own reads, not on the socket, so a regression in either half goes red.
/// </remarks>
public sealed class DualStackListenerTests
{
    private static ServerHarness Harness() => new(options =>
    {
        options.Map = "ctf_truefort";
        options.MapRotation = ["ctf_truefort"];
        options.ShuffleRotation = ShuffleMode.None;
    });

    private static string AddressOfOnlyClient(ServerHarness harness)
    {
        var addresses = new List<string>();
        for (var slot = 1; slot < harness.Server.RosterCount; slot++)
            if (harness.Server.IsConnected(slot))
                addresses.Add(harness.Server.AddressOf(slot));

        return Assert.Single(addresses);
    }

    /// <summary>A client connecting over <c>::1</c> joins — the IPv4-only bind refused it.</summary>
    [Fact]
    public void AnIPv6ClientJoins()
    {
        using var harness = Harness();

        harness.JoinClient("six", IPAddress.IPv6Loopback);

        Assert.Equal("::1", AddressOfOnlyClient(harness));
    }

    /// <summary>An IPv4 client's address is <c>127.0.0.1</c>, not <c>::ffff:127.0.0.1</c>.</summary>
    [Fact]
    public void AnIPv4ClientReadsAsPlainIPv4()
    {
        using var harness = Harness();

        harness.JoinClient("four", IPAddress.Loopback);

        Assert.Equal("127.0.0.1", AddressOfOnlyClient(harness));
    }

    /// <summary>An IPv4 ban entry still refuses an IPv4 peer on the dual-stack socket.</summary>
    [Fact]
    public void AnIPv4BanStillMatches()
    {
        using var harness = Harness();
        Assert.True(IpRange.TryParse("127.0.0.0/8", out var range));
        harness.Server.BanAddress(range, null, "test");

        var client = harness.Connect(IPAddress.Loopback);
        client.SendHello();
        harness.Tick(5);

        Assert.Empty(client.Received);
        Assert.Contains(harness.Log.Warnings, line => line.Contains("refused 127.0.0.1: banned", StringComparison.Ordinal));
    }

    /// <summary>
    /// The control for the ban above: the same IPv4 entry does not catch an IPv6
    /// peer, so the refusal is the entry matching and not the server refusing
    /// everybody.
    /// </summary>
    [Fact]
    public void AnIPv4BanDoesNotCatchIPv6()
    {
        using var harness = Harness();
        Assert.True(IpRange.TryParse("127.0.0.0/8", out var range));
        harness.Server.BanAddress(range, null, "test");

        harness.JoinClient("six", IPAddress.IPv6Loopback);

        Assert.Equal("::1", AddressOfOnlyClient(harness));
    }
}
