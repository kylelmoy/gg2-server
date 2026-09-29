using GG2.Bots;
using GG2.Server;
using Xunit;
using Xunit.Abstractions;

namespace GG2.Tests;

/// <summary>
/// <c>--no-bot-occupancy</c>: that it reaches every bot, and that leaving it off
/// still gives every bot the shared store.
/// </summary>
/// <remarks>
/// <b>Both directions, because each one is trivially satisfiable alone.</b> "No
/// bot has a store when asked" passes on a server that never wired occupancy up
/// at all; "every bot has one by default" passes on a server that ignores the
/// flag. Only the pair says the switch is the thing deciding it.
/// <para>
/// ⚠️ <b>The store must be NULL when off, not empty.</b> <c>NavSearch</c> hoists
/// <c>occupancy is { Count: &gt; 0 }</c> once per search, so a null store costs
/// nothing per edge while an empty one still costs the key arithmetic and an
/// interface-dispatched lookup on every edge the frontier touches. An empty
/// store would also make the two configurations indistinguishable from outside,
/// which is how a flag that does nothing survives.
/// </para>
/// </remarks>
public sealed class BotOccupancyFlagTests(ITestOutputHelper output)
{
    private static ServerOptions Options(bool occupancy) => new()
    {
        // Port 0 so a real server on this machine cannot make the test red —
        // ServerHarness has always done this.
        Port = 0,
        MapDirectory = MapDirectory.Resolve(),
        NavCacheDirectory = MapDirectory.ResolveNav(),
        Map = "ctf_truefort",
        BotOccupancy = occupancy,
        Bots = BotPopulationOptions.Default with
        {
            Enabled = true,
            FillToPlayers = 6,
            MaxBots = 6,
            MinHumans = 0,
        },
    };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheFlagDecidesWhetherBotsShareACrowdMap(bool occupancy)
    {
        ReferenceTree.RequireNavCache();

        using var server = new GameServer(Options(occupancy), new RecordingLog());
        server.Start();

        for (var i = 0; i < 90; i++)
            server.Tick();

        var slots = server.BotSlots;
        Assert.NotEmpty(slots);

        foreach (var slot in slots)
        {
            var bot = ScriptedBot.Of(server.SourceOf(slot));
            Assert.NotNull(bot);

            if (occupancy)
                Assert.NotNull(bot!.Occupancy);
            else
                Assert.Null(bot!.Occupancy);
        }

        output.WriteLine($"{slots.Count} bots, occupancy={occupancy}");
    }

    /// <summary>
    /// With occupancy on, a running server really does accumulate crowding —
    /// the flag is not just a field nobody reads.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>The gate that stops this pair being about plumbing.</b> Asserting
    /// which object a property holds says nothing about whether the mechanism
    /// fires; a store that stayed empty for a whole round would satisfy the test
    /// above and disperse nothing. This waits for real counts to appear.
    /// </remarks>
    [Fact]
    public void WithOccupancyOnTheCrowdMapActuallyFills()
    {
        ReferenceTree.RequireNavCache();

        using var server = new GameServer(Options(true), new RecordingLog());
        server.Start();

        var seen = 0;

        for (var i = 0; i < 600 && seen == 0; i++)
        {
            server.Tick();

            foreach (var slot in server.BotSlots)
            {
                if (ScriptedBot.Of(server.SourceOf(slot))?.Occupancy is { } store)
                {
                    seen = Math.Max(
                        seen,
                        Math.Max(store.EdgeCount(Protocol.Team.Red), store.EdgeCount(Protocol.Team.Blue)));
                }
            }
        }

        output.WriteLine($"{seen} edges counted as crowded");
        Assert.True(seen > 0, "no edge was ever counted as crowded; the mechanism never fired");
    }
}
