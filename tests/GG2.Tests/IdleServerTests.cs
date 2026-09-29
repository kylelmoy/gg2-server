using GG2.Bots;
using GG2.Host;
using GG2.Protocol;
using GG2.Server;
using GG2.Simulation;

namespace GG2.Tests;

/// <summary>
/// An empty server does as little as possible, and bots wait for people.
/// </summary>
/// <remarks>
/// <b>Nothing here sleeps or measures wall-clock time.</b> Idling is a
/// property of <c>GameServer.PacingInterval</c>, which is a pure function of the
/// roster and the options, so it is asserted directly. A test that started
/// <c>Run</c> and timed the ticks would be measuring the build agent, and would
/// take half a second per assertion to do it.
/// </remarks>
public sealed class IdleServerTests
{
    private const string Map = "ctf_truefort";

    // ------------------------------------------------------------------
    // Idling
    // ------------------------------------------------------------------

    /// <summary>A fresh server with nobody on it is idle.</summary>
    [Fact]
    public void AnEmptyServerIsIdle()
    {
        using var harness = new ServerHarness(o => o.Map = Map);

        Assert.True(harness.Server.IsIdle);
        Assert.True(harness.Server.Options.IdleWhenEmpty);
    }

    /// <summary>
    /// A connected client ends idling, and that is the assertion that matters.
    /// </summary>
    /// <remarks>
    /// Idling that did not stop would be a server that never served anybody. The
    /// symptom would be a client joining and then moving at 2 Hz, which reads as
    /// a catastrophic network problem rather than as a server setting.
    /// </remarks>
    [Fact]
    public void AClientEndsIdling()
    {
        using var harness = new ServerHarness(o => o.Map = Map);

        Assert.True(harness.Server.IsIdle);

        var client = harness.Connect();
        client.SendHello();
        harness.Tick(2);

        Assert.False(harness.Server.IsIdle);
    }

    /// <summary>
    /// A bot counts as somebody, so a bot-only server is NOT idle.
    /// </summary>
    /// <remarks>
    /// A server running bots against each other with nobody watching is still
    /// simulating, and <c>--record</c> or the control port may well be the
    /// reason. Idling it would silently change what those recorded — a recording
    /// taken at 2 Hz is not a recording of a game.
    /// </remarks>
    [Fact]
    public void ABotOnlyServerIsNotIdle()
    {
        using var harness = new ServerHarness(o =>
        {
            o.Map = Map;
            o.NavCacheDirectory = ReferenceTree.NavCacheDirectory;
            o.Bots = BotPopulationOptions.Default with
            {
                Enabled = true,
                FillToPlayers = 2,
                MaxBots = 2,
                MinHumans = 0,
            };
        });

        harness.Tick(2);

        Assert.NotEmpty(harness.Server.BotSlots);
        Assert.False(harness.Server.IsIdle);
    }

    /// <summary>
    /// The pacing actually changes — idle is slower, live is the tick rate.
    /// </summary>
    /// <remarks>
    /// <b>The control is the second half.</b> Asserting only that an empty
    /// server paces at 500 ms would pass if the server paced at 500 ms always,
    /// which would be a server running fifteen times too slowly for everyone.
    /// </remarks>
    [Fact]
    public void IdlePacingIsSlowerAndReturnsToFullRateWhenSomebodyArrives()
    {
        using var harness = new ServerHarness(o => o.Map = Map);

        var idle = harness.Server.PacingIntervalForTest();
        Assert.Equal(TimeSpan.FromSeconds(1.0 / ServerOptions.IdleTickRate), idle);

        var client = harness.Connect();
        client.SendHello();
        harness.Tick(2);

        var live = harness.Server.PacingIntervalForTest();
        Assert.Equal(harness.Server.TickInterval, live);
        Assert.True(live < idle, $"live {live} was not faster than idle {idle}");
    }

    /// <summary>Turning it off keeps the full rate on an empty server.</summary>
    [Fact]
    public void IdlingCanBeTurnedOff()
    {
        using var harness = new ServerHarness(o =>
        {
            o.Map = Map;
            o.IdleWhenEmpty = false;
        });

        Assert.True(harness.Server.IsIdle);
        Assert.Equal(harness.Server.TickInterval, harness.Server.PacingIntervalForTest());
    }

    /// <summary>
    /// An explicit <c>--speed</c> wins over idling, in both directions.
    /// </summary>
    /// <remarks>
    /// <c>--speed max</c> is a direct instruction about the tick rate, and
    /// quietly overriding it with a 2 Hz idle would make the flag look broken on
    /// exactly the configuration that asked for it loudest. A <c>--speed</c>
    /// slower than the idle rate must not be sped UP either, which is the half a
    /// one-sided implementation gets wrong.
    /// </remarks>
    [Fact]
    public void AnExplicitSpeedBeatsIdling()
    {
        using var unpaced = new ServerHarness(o =>
        {
            o.Map = Map;
            o.SpeedMultiplier = double.PositiveInfinity;
        });

        Assert.True(unpaced.Server.IsIdle);
        Assert.Equal(TimeSpan.Zero, unpaced.Server.PacingIntervalForTest());

        // Slower than 2Hz already: idling must not accelerate it.
        using var crawling = new ServerHarness(o =>
        {
            o.Map = Map;
            o.SpeedMultiplier = 1.0 / 60.0;
        });

        Assert.True(crawling.Server.IsIdle);
        Assert.Equal(crawling.Server.TickInterval, crawling.Server.PacingIntervalForTest());
    }

    /// <summary>
    /// <c>Tick</c> itself is untouched, so every hand-driven caller is too.
    /// </summary>
    /// <remarks>
    /// Idling lives in <c>Run</c> alone. A <c>Tick</c> that no-opped on an empty
    /// server would break every test in this suite and the control port's
    /// <c>step</c> with it, for no saving at all: the cost idling removes is the
    /// wakeup, and a caller driving ticks by hand has already paid it.
    /// </remarks>
    [Fact]
    public void TickStillAdvancesAnIdleServer()
    {
        using var harness = new ServerHarness(o => o.Map = Map);

        Assert.True(harness.Server.IsIdle);

        var before = harness.Server.Frame;
        harness.Tick(5);

        Assert.Equal(before + 5, harness.Server.Frame);
    }

    // ------------------------------------------------------------------
    // Bots waiting for people
    // ------------------------------------------------------------------

    /// <summary>
    /// <c>--bots-min-humans 1</c> keeps the server bot-free until somebody joins.
    /// </summary>
    /// <remarks>
    /// The mechanism already existed — <c>MinHumans</c> makes the target ZERO
    /// below the floor rather than clamping the fill — and <c>--bots</c> was
    /// forcing it to 0. This is the way to ask for it back.
    /// </remarks>
    [Fact]
    public void BotsMinHumansKeepsAnEmptyServerBotFree()
    {
        using var harness = new ServerHarness(o =>
        {
            o.Map = Map;
            o.NavCacheDirectory = ReferenceTree.NavCacheDirectory;
            o.Bots = BotPopulationOptions.Default with
            {
                Enabled = true,
                FillToPlayers = 4,
                MaxBots = 4,
                MinHumans = 1,
            };
        });

        harness.Tick(5);
        Assert.Empty(harness.Server.BotSlots);

        // The control: the SAME server with the floor at 0 does fill, so the
        // emptiness above is the floor and not a broken bot population.
        using var filled = new ServerHarness(o =>
        {
            o.Map = Map;
            o.NavCacheDirectory = ReferenceTree.NavCacheDirectory;
            o.Bots = BotPopulationOptions.Default with
            {
                Enabled = true,
                FillToPlayers = 4,
                MaxBots = 4,
                MinHumans = 0,
            };
        });

        filled.Tick(5);
        Assert.NotEmpty(filled.Server.BotSlots);
    }

    /// <summary>
    /// <c>--bots-min-humans</c> beats <c>--bots</c> in either order.
    /// </summary>
    /// <remarks>
    /// <c>--bots</c> rewrites <c>MinHumans</c> to 0, so a naive implementation
    /// that applied this flag where it is parsed would make the two orderings two
    /// different servers — and the one that silently loses the flag is the one
    /// somebody will write. It is collected and applied after the loop, the same
    /// fix <c>--search-threads</c> needed.
    /// </remarks>
    [Fact]
    public void BotsMinHumansDoesNotDependOnFlagOrder()
    {
        var before = CommandLine.Parse(["--bots-min-humans", "1", "--bots", "8"]).Bots;
        var after = CommandLine.Parse(["--bots", "8", "--bots-min-humans", "1"]).Bots;

        Assert.Equal(1, before.MinHumans);
        Assert.Equal(1, after.MinHumans);

        // And --bots on its own still means "fill an empty server", which is the
        // default this flag exists to override.
        Assert.Equal(0, CommandLine.Parse(["--bots", "8"]).Bots.MinHumans);
    }

    [Fact]
    public void TheIdleFlagIsReachableAndDefaultsOn()
    {
        Assert.True(CommandLine.Parse([]).IdleWhenEmpty);
        Assert.False(CommandLine.Parse(["--no-idle-when-empty"]).IdleWhenEmpty);
    }
}
