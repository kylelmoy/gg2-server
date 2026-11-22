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

    /// <summary>
    /// The tier re-skills the bots already running, in place: nobody is removed,
    /// and a bot added afterwards gets it too.
    /// </summary>
    [Fact]
    public void TheBotDifficultyReskillsRunningBotsWithoutReplacingThem()
    {
        using var harness = new ServerHarness(options =>
            options.Bots = options.Bots with { Enabled = true, FillToPlayers = 3, MaxBots = 3, MinHumans = 0, Difficulty = 5 });

        harness.TickUntil(() => harness.Server.BotSlots.Count == 3, "three bots");
        var before = harness.Server.BotSlots.Select(s => GG2.Bots.ScriptedBot.Of(harness.Server.SourceOf(s))).ToArray();

        AssertOk(Run(harness, """{"cmd":"bot-difficulty","tier":1}"""));

        var after = harness.Server.BotSlots.Select(s => GG2.Bots.ScriptedBot.Of(harness.Server.SourceOf(s))).ToArray();
        Assert.Equal(before, after);
        Assert.All(after, bot => Assert.Equal(GG2.Bots.BotSkill.ForTier(1), bot!.Skill));
        Assert.NotEqual(GG2.Bots.BotSkill.ForTier(5), GG2.Bots.BotSkill.ForTier(1));
        Assert.Equal(1, State(harness).GetProperty("bots").GetProperty("difficulty").GetInt32());

        AssertOk(Run(harness, """{"cmd":"bot-limit","max":4}"""));
        AssertOk(Run(harness, """{"cmd":"bots","fill":4}"""));
        harness.TickUntil(() => harness.Server.BotSlots.Count == 4, "a fourth bot");
        Assert.All(harness.Server.BotSlots, s =>
            Assert.Equal(GG2.Bots.BotSkill.ForTier(1), GG2.Bots.ScriptedBot.Of(harness.Server.SourceOf(s))!.Skill));

        Assert.False(Run(harness, """{"cmd":"bot-difficulty","tier":0}""").GetProperty("ok").GetBoolean());
        Assert.False(Run(harness, """{"cmd":"bot-difficulty","tier":6}""").GetProperty("ok").GetBoolean());
        Assert.Equal(1, State(harness).GetProperty("bots").GetProperty("difficulty").GetInt32());
    }

    [Fact]
    public void SwappingTheBrainReplacesEveryBot()
    {
        using var harness = new ServerHarness(options =>
            options.Bots = options.Bots with { Enabled = true, FillToPlayers = 3, MaxBots = 3, MinHumans = 0 });

        harness.TickUntil(() => harness.Server.BotSlots.Count == 3, "three bots");

        AssertOk(Run(harness, """{"cmd":"bot-brain","mode":"policy"}"""));
        Assert.Empty(harness.Server.BotSlots);
        Assert.Equal("policy", State(harness).GetProperty("bots").GetProperty("mode").GetString());

        harness.TickUntil(() => harness.Server.BotSlots.Count == 3, "the bots back");
    }

    /// <summary>
    /// A brain that fails to build is refused and changes nothing: the mode, the
    /// options and the bots are all as they were.
    /// </summary>
    /// <remarks>
    /// Driven by a configured checkpoint deleted after startup — the one way left
    /// to make a policy build fail once the shipped bundle is the fallback.
    /// </remarks>
    [Fact]
    public void ABrainThatFailsToBuildIsRefusedAndChangesNothing()
    {
        var checkpoint = Path.Combine(Path.GetTempPath(), $"gg2-gone-{Guid.NewGuid():N}.gg2p");
        File.Copy(PolicyFixture.Checkpoint, checkpoint);

        var parsed = GG2.Host.CommandLine.Parse(["--bot-mode", "policy", "--policy", checkpoint]);

        using var harness = new ServerHarness(options =>
        {
            options.BotMode = parsed.BotMode;
            options.PolicyPaths = parsed.PolicyPaths;
            options.Bots = parsed.Bots with { Enabled = true, FillToPlayers = 3, MaxBots = 3, MinHumans = 0 };
        });

        File.Delete(checkpoint);
        harness.TickUntil(() => harness.Server.BotSlots.Count == 3, "three bots");

        AssertOk(Run(harness, """{"cmd":"bot-brain","mode":"scripted"}"""));
        harness.TickUntil(() => harness.Server.BotSlots.Count == 3, "the bots back");

        Assert.False(Run(harness, """{"cmd":"bot-brain","mode":"policy"}""").GetProperty("ok").GetBoolean());
        Assert.Equal("scripted", State(harness).GetProperty("bots").GetProperty("mode").GetString());
        Assert.Equal([checkpoint], harness.Server.Options.PolicyPaths);
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

    /// <summary>
    /// A server started with no checkpoint can still be switched to a policy brain
    /// live, and what it loads is the shipped bundle.
    /// </summary>
    /// <remarks>
    /// The verb takes a mode and never a path, so the bundle is the only thing it
    /// could load. The options keep saying what was CONFIGURED — nothing — which
    /// is why <c>PolicyPaths</c> stays empty after the switch.
    /// </remarks>
    [Fact]
    public void APolicyBrainCanBeChosenLiveWithoutACheckpointAtStartup()
    {
        using var harness = new ServerHarness();

        Assert.Empty(harness.Server.Options.PolicyPaths);
        Assert.True(State(harness).GetProperty("server").GetProperty("policy").GetBoolean());

        AssertOk(Run(harness, """{"cmd":"bot-brain","mode":"policy"}"""));

        var bots = State(harness).GetProperty("bots");

        Assert.Equal("policy", bots.GetProperty("mode").GetString());
        Assert.Equal(
            MapDirectory.ShippedCheckpoints.Select(f => "gg2-sim:" + Path.GetFileNameWithoutExtension(f)).ToArray(),
            bots.GetProperty("tiers").EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToArray());
        Assert.Empty(harness.Server.Options.PolicyPaths);

        // And back: scripted still builds with no checkpoint given.
        AssertOk(Run(harness, """{"cmd":"bot-brain","mode":"scripted"}"""));
        Assert.Equal("scripted", State(harness).GetProperty("bots").GetProperty("brain").GetString());
    }

    /// <summary>The bundle resolves all or nothing.</summary>
    /// <remarks>
    /// Half a bundle would drive a different class set from the one documented,
    /// so a directory missing any file is no bundle and the live switch to a
    /// policy brain is refused instead.
    /// </remarks>
    [Fact]
    public void TheShippedBundleResolvesWholeOrNotAtAll()
    {
        Assert.Equal(MapDirectory.ShippedCheckpoints.Count, MapDirectory.ResolveCheckpoints().Count);

        var root = Directory.CreateTempSubdirectory("gg2-checkpoints-");

        try
        {
            var start = Directory.CreateDirectory(Path.Combine(root.FullName, "bin")).FullName;
            Assert.Empty(MapDirectory.ResolveCheckpoints(start));

            var bundle = Directory.CreateDirectory(Path.Combine(root.FullName, "checkpoints")).FullName;
            File.WriteAllBytes(Path.Combine(bundle, MapDirectory.ShippedCheckpoints[0]), []);
            Assert.Empty(MapDirectory.ResolveCheckpoints(start));

            // Every file but the last is still no bundle.
            for (var i = 1; i < MapDirectory.ShippedCheckpoints.Count - 1; i++)
                File.WriteAllBytes(Path.Combine(bundle, MapDirectory.ShippedCheckpoints[i]), []);

            Assert.Empty(MapDirectory.ResolveCheckpoints(start));

            File.WriteAllBytes(Path.Combine(bundle, MapDirectory.ShippedCheckpoints[^1]), []);
            Assert.Equal(
                MapDirectory.ShippedCheckpoints.Select(f => Path.Combine(bundle, f)).ToArray(),
                MapDirectory.ResolveCheckpoints(start));
        }
        finally
        {
            root.Delete(recursive: true);
        }
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
