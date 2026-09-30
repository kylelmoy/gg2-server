using System.Text.Json;
using GG2.Server;

namespace GG2.Tests;

/// <summary>
/// The admin panel's live settings — <c>GameServer.Live</c> — through the verbs
/// the panel sends.
/// </summary>
/// <remarks>
/// What each asserts is the EFFECT on the running server, read back through
/// <c>state</c>, because a verb that answers <c>ok</c> and changes nothing is the
/// failure a panel cannot show.
/// </remarks>
public sealed class LiveSettingsTests
{
    private static JsonElement Run(ServerHarness harness, string command) =>
        JsonDocument.Parse(ControlCommands.Dispatch(harness.Server, command)).RootElement;

    private static JsonElement State(ServerHarness harness) => Run(harness, """{"cmd":"state"}""");

    private static void AssertOk(JsonElement reply) =>
        Assert.True(reply.GetProperty("ok").GetBoolean(), reply.ToString());

    [Fact]
    public void ThePasswordIsReportedAsSetButNeverEchoed()
    {
        using var harness = new ServerHarness();

        AssertOk(Run(harness, """{"cmd":"password","value":"hunter2"}"""));

        var state = State(harness);
        Assert.True(state.GetProperty("server").GetProperty("passworded").GetBoolean());
        Assert.DoesNotContain("hunter2", state.ToString());
        Assert.Equal("hunter2", harness.Options.Password);

        AssertOk(Run(harness, """{"cmd":"password","value":""}"""));
        Assert.False(State(harness).GetProperty("server").GetProperty("passworded").GetBoolean());
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(48, true)]
    [InlineData(49, false)]
    public void ThePlayerLimitIsBounded(int value, bool accepted)
    {
        using var harness = new ServerHarness();

        var reply = Run(harness, $$"""{"cmd":"player-limit","value":{{value}}}""");

        Assert.Equal(accepted, reply.GetProperty("ok").GetBoolean());

        if (accepted)
            Assert.Equal(value, State(harness).GetProperty("server").GetProperty("slots").GetInt32());
    }

    [Fact]
    public void AutobalanceReachesTheWorldNotOnlyTheOptions()
    {
        using var harness = new ServerHarness();

        AssertOk(Run(harness, """{"cmd":"autobalance","enabled":false}"""));

        Assert.False(harness.Options.Autobalance);
        Assert.False(State(harness).GetProperty("server").GetProperty("autobalance").GetBoolean());

        AssertOk(Run(harness, """{"cmd":"autobalance","enabled":true}"""));
        Assert.True(State(harness).GetProperty("server").GetProperty("autobalance").GetBoolean());
    }

    [Fact]
    public void TheRotationFollowsTheCurrentMapAndRefusesAMissingOne()
    {
        using var harness = new ServerHarness();

        AssertOk(Run(harness, """{"cmd":"rotation","maps":["koth_valley","ctf_truefort","cp_dirtbowl"]}"""));

        var server = State(harness).GetProperty("server");
        Assert.Equal(3, server.GetProperty("rotation").GetArrayLength());
        Assert.Equal(1, server.GetProperty("rotationIndex").GetInt32());

        // Refused whole: the rotation is unchanged.
        Assert.False(Run(harness, """{"cmd":"rotation","maps":["ctf_truefort","no_such_map"]}""")
            .GetProperty("ok").GetBoolean());
        Assert.Equal(3, State(harness).GetProperty("server").GetProperty("rotation").GetArrayLength());
    }

    [Fact]
    public void EndingTheRoundGoesToTheNextMapInTheEditedRotation()
    {
        using var harness = new ServerHarness();

        AssertOk(Run(harness, """{"cmd":"rotation","maps":["ctf_truefort","koth_valley"]}"""));
        AssertOk(Run(harness, """{"cmd":"end-round"}"""));
        harness.Tick();

        Assert.Equal("koth_valley", harness.Server.NextMap);
    }

    [Fact]
    public void RestartingTheRoundStaysOnTheMap()
    {
        using var harness = new ServerHarness();

        AssertOk(Run(harness, """{"cmd":"rotation","maps":["ctf_truefort","koth_valley"]}"""));
        AssertOk(Run(harness, """{"cmd":"restart-round"}"""));
        harness.Tick();

        Assert.Equal("ctf_truefort", harness.Server.NextMap);

        // And a second request while the change is running is refused.
        Assert.False(Run(harness, """{"cmd":"restart-round"}""").GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void AnOperatorMovesAPlayerAcrossTeams()
    {
        using var harness = new ServerHarness();
        harness.JoinClient("alice");

        AssertOk(Run(harness, """{"cmd":"team","slot":1,"team":"blue"}"""));
        Assert.Equal("Blue", State(harness).GetProperty("players")[1].GetProperty("team").GetString());

        AssertOk(Run(harness, """{"cmd":"team","slot":1,"team":"red"}"""));
        Assert.Equal("Red", State(harness).GetProperty("players")[1].GetProperty("team").GetString());

        // Slot 0 is the server's own spectator and never moves.
        Assert.False(Run(harness, """{"cmd":"team","slot":0,"team":"red"}""").GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void TheBotLimitCapsThePopulation()
    {
        using var harness = new ServerHarness(options =>
            options.Bots = options.Bots with { Enabled = true, FillToPlayers = 6, MaxBots = 6, MinHumans = 0 });

        harness.TickUntil(() => harness.Server.BotSlots.Count == 6, "six bots");

        AssertOk(Run(harness, """{"cmd":"bot-limit","max":2}"""));
        harness.TickUntil(() => harness.Server.BotSlots.Count == 2, "two bots");

        Assert.Equal(2, State(harness).GetProperty("server").GetProperty("botLimit").GetInt32());
    }

    [Fact]
    public void SwappingTheBrainReplacesEveryBotAndAPolicyNeedsACheckpoint()
    {
        using var harness = new ServerHarness(options =>
            options.Bots = options.Bots with { Enabled = true, FillToPlayers = 3, MaxBots = 3, MinHumans = 0 });

        harness.TickUntil(() => harness.Server.BotSlots.Count == 3, "three bots");

        AssertOk(Run(harness, """{"cmd":"bot-brain","mode":"search"}"""));
        Assert.Empty(harness.Server.BotSlots);
        Assert.Equal("search", State(harness).GetProperty("bots").GetProperty("mode").GetString());

        harness.TickUntil(() => harness.Server.BotSlots.Count == 3, "the bots back");

        // No checkpoint was given at startup, so a policy brain is refused and
        // nothing changes.
        Assert.False(Run(harness, """{"cmd":"bot-brain","mode":"policy"}""").GetProperty("ok").GetBoolean());
        Assert.Equal("search", State(harness).GetProperty("bots").GetProperty("mode").GetString());
        Assert.Equal(3, harness.Server.BotSlots.Count);
    }

    /// <summary>
    /// Offline drops everybody, refuses a new connection outright rather than
    /// leaving it in the backlog, and holds the bots at zero — and coming back
    /// listens on the SAME port, which a server started on port 0 would not do
    /// by accident.
    /// </summary>
    [Fact]
    public void OfflineDropsEveryoneRefusesConnectionsAndComesBackOnTheSamePort()
    {
        using var harness = new ServerHarness(options =>
            options.Bots = options.Bots with { Enabled = true, FillToPlayers = 4, MaxBots = 4, MinHumans = 0 });

        var alice = harness.JoinClient("alice");
        harness.TickUntil(() => harness.Server.BotSlots.Count > 0, "the bots");
        var port = harness.Port;

        AssertOk(Run(harness, """{"cmd":"offline","enabled":true}"""));
        harness.TickUntil(() => alice.IsClosed && harness.Server.BotSlots.Count == 0, "everyone gone");

        Assert.True(State(harness).GetProperty("server").GetProperty("offline").GetBoolean());
        Assert.ThrowsAny<System.Net.Sockets.SocketException>(() => new LoopbackClient(port));

        AssertOk(Run(harness, """{"cmd":"offline","enabled":false}"""));

        Assert.Equal(port, harness.Port);
        harness.JoinClient("bob");
        harness.TickUntil(() => harness.Server.BotSlots.Count > 0, "the bots back");
        Assert.False(State(harness).GetProperty("server").GetProperty("offline").GetBoolean());
    }

    [Fact]
    public void AJoinIsCountedEveryTime()
    {
        using var harness = new ServerHarness();

        harness.JoinClient("alice");
        harness.JoinClient("alice");

        Assert.Equal(2, State(harness).GetProperty("server").GetProperty("joins").GetInt64());
    }
}
