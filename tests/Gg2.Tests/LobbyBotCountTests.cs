using Gg2.Bots;
using Gg2.Server;

namespace Gg2.Tests;

/// <summary>
/// What the lobby packet's bot counter says, on a server that has bots and on
/// one that only would.
/// </summary>
/// <remarks>
/// The number, not the datagram: <see cref="LobbyOptInTests"/> is why nothing
/// here sends one, and <see cref="LobbyRegistrationTests"/> covers where the
/// value lands in the bytes.
/// </remarks>
public sealed class LobbyBotCountTests
{
    private const string Map = "ctf_truefort";

    private static ServerHarness Harness(Action<ServerOptions>? configure = null) =>
        new(o =>
        {
            o.Map = Map;
            o.NavCacheDirectory = ReferenceTree.NavCacheDirectory;
            configure?.Invoke(o);
        });

    /// <summary>
    /// Bots off is zero, and no prospective count leaks out of it.
    /// </summary>
    [Fact]
    public void AServerWithoutBotsAnnouncesNone()
    {
        using var harness = Harness();

        harness.Tick(5);

        Assert.Equal(0, harness.Server.AnnouncedBotCount);
    }

    /// <summary>
    /// An idle bot server announces the game a joiner would arrive to, not the
    /// empty one it is sitting in.
    /// </summary>
    /// <remarks>
    /// <c>MinHumans</c> holds the live count at zero until somebody joins, so
    /// the honest live number and the useful one differ exactly here. Filling to
    /// 8 means the first joiner finds seven bots, and the browser renders that
    /// as <c>0+7/16</c>.
    /// </remarks>
    [Fact]
    public void AnIdleBotServerAnnouncesWhatAJoinerWouldFind()
    {
        using var harness = Harness(o => o.Bots = BotPopulationOptions.Default with
        {
            Enabled = true,
            FillToPlayers = 8,
            MaxBots = 8,
            MinHumans = 1,
        });

        harness.Tick(5);

        Assert.Empty(harness.Server.BotSlots);
        Assert.Equal(0, harness.Server.HumanCount);
        Assert.Equal(7, harness.Server.AnnouncedBotCount);
    }

    /// <summary>
    /// The prospective count is what ONE more human unlocks, so a server that
    /// needs more than one still announces zero.
    /// </summary>
    /// <remarks>
    /// Advertising the fill here would promise a game the first joiner cannot
    /// have: at <c>MinHumans = 3</c> they arrive to an empty map and wait.
    /// </remarks>
    [Fact]
    public void AHigherHumanFloorAnnouncesNothingToTheFirstJoiner()
    {
        using var harness = Harness(o => o.Bots = BotPopulationOptions.Default with
        {
            Enabled = true,
            FillToPlayers = 8,
            MaxBots = 8,
            MinHumans = 3,
        });

        harness.Tick(5);

        Assert.Equal(0, harness.Server.AnnouncedBotCount);
    }

    /// <summary>
    /// Once bots are running, the announced count is the running one.
    /// </summary>
    /// <remarks>
    /// It stops being a prediction the instant it can be a fact — including
    /// when the two disagree, which they do here: the fill target would predict
    /// two and <c>MaxBots</c> is what actually caps the server at one.
    /// </remarks>
    [Fact]
    public void ARunningBotServerAnnouncesItsLiveCount()
    {
        using var harness = Harness(o => o.Bots = BotPopulationOptions.Default with
        {
            Enabled = true,
            FillToPlayers = 8,
            MaxBots = 1,
            MinHumans = 0,
            Difficulty = 3,
        });

        harness.Tick(5);

        Assert.Single(harness.Server.BotSlots);
        Assert.Equal(1, harness.Server.AnnouncedBotCount);
    }
}
