using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using GG2.Host;
using GG2.Lobby;
using GG2.Server;

namespace GG2.Tests;

/// <summary>
/// <see cref="ServerOptions.LobbyUrls"/>: parsing, the command line and config
/// file, and a server announcing to several lobbies at once on loopback.
/// </summary>
public sealed class LobbyTargetsTests
{
    [Fact]
    public void AUdpUrlWithNoPortGetsTheLobbyPort()
    {
        Assert.True(LobbyTargets.TryParse("udp://lobby.example", out var uri));
        Assert.Equal(29944, uri.Port);
        Assert.Equal("lobby.example", uri.DnsSafeHost);
    }

    [Theory]
    [InlineData("ftp://lobby.example")]
    [InlineData("udp://lobby.example:29944/servers")]
    [InlineData("lobby.example")]
    [InlineData("http://")]
    public void ANonLobbyUrlIsRefused(string url)
    {
        Assert.False(LobbyTargets.TryParse(url, out _));
        var thrown = Assert.Throws<FormatException>(() => LobbyTargets.Parse("Settings.LobbyUrls", url));
        Assert.Contains("Settings.LobbyUrls", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AListIsCommaSeparated() =>
        Assert.Equal(
            ["udp://a.example", "https://b.example"],
            LobbyTargets.Parse("x", " udp://a.example , https://b.example ,"));

    [Fact]
    public void LobbyAloneAnnouncesToTheDefaultList()
    {
        var options = CommandLine.Parse(["--lobby"]);

        Assert.True(options.AnnounceToLobby);
        Assert.Equal(LobbyTargets.Default, options.LobbyUrls);
    }

    /// <summary>
    /// The first <c>--lobby-url</c> replaces the default, later ones append,
    /// and naming a lobby turns announcing on.
    /// </summary>
    [Fact]
    public void LobbyUrlReplacesThenAppendsAndImpliesLobby()
    {
        var options = CommandLine.Parse(["--lobby-url", "http://a.example,udp://b.example", "--lobby-url", "https://c.example"]);

        Assert.True(options.AnnounceToLobby);
        Assert.Equal(["http://a.example", "udp://b.example", "https://c.example"], options.LobbyUrls);
    }

    [Fact]
    public void ABadLobbyUrlIsAUsageError() =>
        Assert.Throws<FormatException>(() => CommandLine.Parse(["--lobby-url", "ftp://nope.example"]));

    [Fact]
    public void TheConfigFileReadsAList()
    {
        var options = new ServerOptions();
        ServerConfigFile.ApplyText(options, """{"Settings": {"UseLobby": true, "LobbyUrls": "udp://a.example:1, http://b.example:2"}}""", "test.json");

        Assert.True(options.AnnounceToLobby);
        Assert.Equal(["udp://a.example:1", "http://b.example:2"], options.LobbyUrls);
    }

    /// <summary>
    /// One server, three lobbies, all on loopback: both UDP lobbies get a
    /// registration under DIFFERENT ids, and the gg2-lobby lists it.
    /// </summary>
    /// <remarks>
    /// Loopback only, so this sends nothing off the machine, which is the line
    /// <see cref="LobbyOptInTests"/> draws.
    /// </remarks>
    [Fact]
    public async Task OneServerAnnouncesToEveryLobbyInTheList()
    {
        using var udpA = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var udpB = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var http = new LobbyServer(new LobbyOptions { Bind = IPAddress.Loopback, Port = 0, Probe = false }, static _ => { });

        var portA = ((IPEndPoint)udpA.Client.LocalEndPoint!).Port;
        var portB = ((IPEndPoint)udpB.Client.LocalEndPoint!).Port;

        using var harness = new ServerHarness(options =>
        {
            options.ServerName = "three lobbies";
            options.AnnounceToLobby = true;
            options.LobbyUrls = [$"udp://127.0.0.1:{portA}", $"udp://127.0.0.1:{portB}", $"http://127.0.0.1:{http.Port}"];
        });

        // Checked first: the startup line is written on this thread, before the
        // HTTP announcer's worker can log anything.
        Assert.True(harness.Log.Contains($"announcing to udp://127.0.0.1:{portA}, udp://127.0.0.1:{portB}, http://127.0.0.1:{http.Port}"));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var packetA = (await udpA.ReceiveAsync(timeout.Token)).Buffer;
        var packetB = (await udpB.ReceiveAsync(timeout.Token)).Buffer;

        Assert.Equal(packetA.Length, packetB.Length);
        Assert.NotEqual(packetA, packetB);

        using var client = new HttpClient();
        string list;
        do
        {
            await Task.Delay(20, timeout.Token);
            list = await client.GetStringAsync($"http://127.0.0.1:{http.Port}/servers", timeout.Token);
        }
        while (!list.Contains("three lobbies", StringComparison.Ordinal));

    }
}
