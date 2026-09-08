using System.Reflection;
using Gg2.Server;

namespace Gg2.Tests;

/// <summary>
/// That the lobby re-announcement is paced by the wall clock and not only by
/// the tick count.
/// </summary>
/// <remarks>
/// <b>This is a real-time contract with a third party, and it is the one
/// cadence in the server a frame count cannot express.</b> The lobby drops an
/// entry it has not heard from within under a minute, while
/// <c>ServerOptions.IdleWhenEmpty</c> makes an empty server's 900 ticks seven
/// and a half minutes. A frame-only cadence therefore lists an empty server,
/// loses it, and cannot get it back: the player whose arrival would restore the
/// live rate cannot see the server to join it.
/// <para>
/// Nothing here sends a datagram; <see cref="LobbyOptInTests"/> says why no
/// test in this repo ever may.
/// </para>
/// </remarks>
public sealed class LobbyCadenceTests
{
    /// <summary>The reference's own cadence, on the reference's own clock.</summary>
    [Fact]
    public void TheFrameArmFiresEveryNineHundredTicks()
    {
        Assert.True(LobbyRegistration.IsReRegisterDue(900, 1_000, 1_000));
        Assert.False(LobbyRegistration.IsReRegisterDue(899, 1_000, 1_000));
    }

    /// <summary>
    /// Thirty real seconds is enough on its own, at any frame.
    /// </summary>
    [Fact]
    public void TheWallClockArmFiresWithoutNineHundredTicks()
    {
        const int ms = LobbyRegistration.ReRegisterIntervalMs;

        Assert.False(LobbyRegistration.IsReRegisterDue(1, 1_000 + ms - 1, 1_000));
        Assert.True(LobbyRegistration.IsReRegisterDue(1, 1_000 + ms, 1_000));
    }

    /// <summary>
    /// At the idle rate the wall clock is the only arm that fires in time.
    /// </summary>
    /// <remarks>
    /// Stated as arithmetic rather than as a claim, so that changing either
    /// constant fails here rather than silently restoring the outage: 900 ticks
    /// at <see cref="ServerOptions.IdleTickRate"/> is minutes, and the lobby
    /// measures in seconds.
    /// </remarks>
    [Fact]
    public void NineHundredIdleTicksIsFarLongerThanTheLobbyWaits()
    {
        var idleMs = 1000L * LobbyRegistration.ReRegisterIntervalTicks / ServerOptions.IdleTickRate;

        Assert.True(idleMs > 5 * LobbyRegistration.ReRegisterIntervalMs);
    }

    /// <summary>
    /// The running server actually consults that clock.
    /// </summary>
    /// <remarks>
    /// The wiring rather than the predicate, and the half that was missing: the
    /// block used to be reachable only on a multiple of 900. The last-run stamp
    /// is pushed back by a full interval instead of the test waiting one, which
    /// is why this reads a private field — the alternative is a thirty-second
    /// test.
    /// </remarks>
    [Fact]
    public void ATickFiresTheBlockOnceTheIntervalHasPassed()
    {
        using var harness = new ServerHarness();

        // Away from a multiple of 900, so only the wall clock can be what fires.
        harness.Tick(3);

        var before = harness.Server.HousekeepingRuns;

        Stamp(harness.Server, Environment.TickCount64 - LobbyRegistration.ReRegisterIntervalMs);
        harness.Tick(1);

        Assert.Equal(before + 1, harness.Server.HousekeepingRuns);

        // And having run, it is not due again on the next tick.
        harness.Tick(1);

        Assert.Equal(before + 1, harness.Server.HousekeepingRuns);
    }

    private static void Stamp(GameServer server, long ms) =>
        typeof(GameServer)
            .GetField("_lastHousekeepingMs", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(server, ms);
}
