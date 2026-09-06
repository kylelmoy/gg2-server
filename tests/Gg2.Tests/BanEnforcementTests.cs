using System.Net;
using Gg2.Server;

namespace Gg2.Tests;

/// <summary>
/// A ban against a running server: the kick, the refusal, and the lapse.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>Everything here runs over loopback, which is exempt from the RATE
/// limiter and deliberately not from bans.</b> That asymmetry is the thing
/// worth checking: the exemption exists because a host runs a client beside its
/// own server, and it must not become a way for a ban an operator explicitly
/// typed to do nothing.
/// </para>
/// <para>
/// ⛔ An address is not a person — <c>BanList</c> says so at length, and
/// <c>docs/HARDENING.md</c> records that this is a moderation tool rather than
/// a security control. These tests are about it doing what an operator expects.
/// </para>
/// </remarks>
public sealed class BanEnforcementTests
{
    private static ServerHarness Harness() => new(options =>
    {
        options.Map = "ctf_truefort";
        options.MapRotation = ["ctf_truefort"];
        options.ShuffleRotation = ShuffleMode.None;
    });

    private static IpRange Loopback()
    {
        Assert.True(IpRange.TryParse("127.0.0.1", out var range));
        return range;
    }

    /// <summary>
    /// ⚠️ <b>A ban disconnects the person it was aimed at.</b> One that only
    /// refused future connections would leave them sitting on the server until
    /// they chose to leave, which is precisely not what an operator pressing it
    /// means — and it is the reason this goes through the server rather than
    /// straight into the list.
    /// </summary>
    [Fact]
    public void BanningDisconnectsWhoeverIsAlreadyOnFromThatAddress()
    {
        using var harness = Harness();
        var client = harness.JoinClient("nuisance");

        var before = harness.Server.RosterCount;
        var kicked = harness.Server.BanAddress(Loopback(), null, "griefing");

        Assert.Equal(1, kicked);

        harness.Tick(3);

        Assert.Equal(before - 1, harness.Server.RosterCount);
        Assert.Contains(harness.Log.Warnings, line => line.Contains("banned 127.0.0.1", StringComparison.Ordinal));
    }

    /// <summary>And they cannot come back.</summary>
    [Fact]
    public void ABannedAddressIsRefusedAtAccept()
    {
        using var harness = Harness();
        harness.Server.BanAddress(Loopback(), null, "griefing");

        var client = harness.Connect();
        client.SendHello();
        harness.Tick(5);

        // Dropped with no message: a ban is the one refusal the peer is not owed
        // an explanation for, and naming which address is banned is free
        // reconnaissance.
        Assert.Empty(client.Received);
        Assert.Contains(harness.Log.Warnings, line => line.Contains("banned", StringComparison.Ordinal));
    }

    /// <summary>
    /// ⚠️ <b>The control case.</b> Both tests above assert a connection was
    /// refused, which is the shape that also passes if the server refuses
    /// everybody. After the ban is lifted the same address must join normally —
    /// same harness, same socket path.
    /// </summary>
    [Fact]
    public void OnceLiftedTheSameAddressJoinsNormally()
    {
        using var harness = Harness();

        harness.Server.BanAddress(Loopback(), null, "griefing");
        Assert.True(harness.Server.UnbanAddress(Loopback()));

        var client = harness.JoinClient("forgiven");

        Assert.False(client.IsClosed);
        Assert.Contains(harness.Log.Lines, line => line.Contains("forgiven joined", StringComparison.Ordinal));
    }

    /// <summary>
    /// A timed ban lapses without anybody doing anything.
    /// </summary>
    /// <remarks>
    /// Driven through <c>BanList</c> directly because the sweep is on the
    /// server's thirty-second cadence and reads the wall clock, which a test
    /// cannot move. What the server owns — the accept-time check — is covered
    /// above; what lapses is the list, and that is what this drives.
    /// </remarks>
    [Fact]
    public void ATimedBanStopsApplyingOnItsOwn()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var list = new BanList(string.Empty, new RecordingLog());

        list.Add(Loopback(), TimeSpan.FromMinutes(5), "cooling off", now);

        Assert.NotNull(list.Find(IPAddress.Loopback, now.AddMinutes(4)));
        Assert.Null(list.Find(IPAddress.Loopback, now.AddMinutes(6)));
    }

    /// <summary>
    /// An operator banning with no ban file is told the ban will not survive.
    /// </summary>
    /// <remarks>
    /// ⚠️ The alternative is finding out by having the same person come back
    /// after the next restart, which on a 24/7 server is soon and is silent.
    /// </remarks>
    [Fact]
    public void BanningWithoutAFileWarnsThatItWillNotSurviveARestart()
    {
        using var harness = Harness();

        harness.Server.BanAddress(Loopback(), null, "griefing");

        Assert.Contains(
            harness.Log.Warnings,
            line => line.Contains("lost on restart", StringComparison.Ordinal));
    }
}
