using GG2.Server;

namespace GG2.Tests;

/// <summary>
/// <c>--exit-when-empty</c>: a private room stops itself once nobody is on it
/// (<c>docs/ROOMS.md</c>).
/// </summary>
/// <remarks>
/// The rule is tested on <see cref="EmptyServerExit"/> with times passed in, so
/// nothing sleeps. The two server tests prove it is wired: one waits for a real
/// one-second interval, the other proves a client holds the server open across it.
/// </remarks>
public sealed class EmptyServerExitTests
{
    [Fact]
    public void ARoomNobodyJoinsExitsAfterTheInterval()
    {
        var exit = new EmptyServerExit(60, 0, nowMilliseconds: 1_000);

        Assert.Equal(SelfExitReason.None, exit.Check(60_999, occupied: false));
        Assert.Equal(SelfExitReason.Empty, exit.Check(61_000, occupied: false));
    }

    [Fact]
    public void TheIntervalCountsFromTheLastMomentSomebodyWasOn()
    {
        var exit = new EmptyServerExit(60, 0, nowMilliseconds: 0);

        Assert.Equal(SelfExitReason.None, exit.Check(50_000, occupied: true));
        Assert.Equal(SelfExitReason.None, exit.Check(109_999, occupied: false));
        Assert.Equal(SelfExitReason.Empty, exit.Check(110_000, occupied: false));
    }

    [Fact]
    public void AnOccupiedServerNeverExits()
    {
        var exit = new EmptyServerExit(1, 0, nowMilliseconds: 0);

        Assert.Equal(SelfExitReason.None, exit.Check(1_000_000, occupied: true));
    }

    /// <summary>The hard lifetime ends a room even with players on it.</summary>
    [Fact]
    public void TheLifetimeEndsAnOccupiedRoom()
    {
        var exit = new EmptyServerExit(120, 360, nowMilliseconds: 5_000);

        Assert.Equal(SelfExitReason.None, exit.Check(5_000 + 359 * 60_000L, occupied: true));
        Assert.Equal(SelfExitReason.Lifetime, exit.Check(5_000 + 360 * 60_000L, occupied: true));
    }

    /// <summary>A lifetime with no empty limit never exits for being empty.</summary>
    [Fact]
    public void ALifetimeAloneIgnoresEmptiness()
    {
        var exit = new EmptyServerExit(0, 10, nowMilliseconds: 0);

        Assert.Equal(SelfExitReason.None, exit.Check(9 * 60_000L, occupied: false));
        Assert.Equal(SelfExitReason.Lifetime, exit.Check(10 * 60_000L, occupied: false));
    }

    [Fact]
    public void OffByDefault()
    {
        Assert.Equal(0, new ServerOptions().ExitWhenEmptySeconds);
        Assert.Equal(0, new ServerOptions().MaxLifetimeMinutes);
    }

    [Fact]
    public void ANegativeIntervalIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(new ServerOptions { ExitWhenEmptySeconds = -1 }.Validate);
        Assert.Throws<ArgumentOutOfRangeException>(new ServerOptions { MaxLifetimeMinutes = -1 }.Validate);
    }

    [Fact]
    public void AnEmptyServerStopsItself()
    {
        using var harness = new ServerHarness(o =>
        {
            o.Map = "ctf_truefort";
            o.ExitWhenEmptySeconds = 1;
        });

        Assert.True(TickUntilExited(harness, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void AConnectedClientKeepsItRunning()
    {
        using var harness = new ServerHarness(o =>
        {
            o.Map = "ctf_truefort";
            o.ExitWhenEmptySeconds = 1;
        });

        var client = harness.Connect();
        client.SendHello();

        // Twice the interval, with the client on throughout.
        Assert.False(TickUntilExited(harness, TimeSpan.FromSeconds(2)));
        Assert.Equal(SelfExitReason.None, harness.Server.SelfExit);
    }

    private static bool TickUntilExited(ServerHarness harness, TimeSpan deadline)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();

        while (clock.Elapsed < deadline)
        {
            harness.Tick();

            if (harness.Server.SelfExit == SelfExitReason.Empty)
                return true;

            Thread.Sleep(20);
        }

        return false;
    }
}
