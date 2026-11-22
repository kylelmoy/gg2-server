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
        var exit = new EmptyServerExit(60, nowMilliseconds: 1_000);

        Assert.False(exit.ShouldExit(60_999, occupied: false));
        Assert.True(exit.ShouldExit(61_000, occupied: false));
    }

    [Fact]
    public void TheIntervalCountsFromTheLastMomentSomebodyWasOn()
    {
        var exit = new EmptyServerExit(60, nowMilliseconds: 0);

        Assert.False(exit.ShouldExit(50_000, occupied: true));
        Assert.False(exit.ShouldExit(109_999, occupied: false));
        Assert.True(exit.ShouldExit(110_000, occupied: false));
    }

    [Fact]
    public void AnOccupiedServerNeverExits()
    {
        var exit = new EmptyServerExit(1, nowMilliseconds: 0);

        Assert.False(exit.ShouldExit(1_000_000, occupied: true));
    }

    [Fact]
    public void OffByDefault()
    {
        Assert.Equal(0, new ServerOptions().ExitWhenEmptySeconds);
    }

    [Fact]
    public void ANegativeIntervalIsRefused()
    {
        var options = new ServerOptions { ExitWhenEmptySeconds = -1 };

        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
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
        Assert.False(harness.Server.ExitedEmpty);
    }

    private static bool TickUntilExited(ServerHarness harness, TimeSpan deadline)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();

        while (clock.Elapsed < deadline)
        {
            harness.Tick();

            if (harness.Server.ExitedEmpty)
                return true;

            Thread.Sleep(20);
        }

        return false;
    }
}
