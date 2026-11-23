using GG2.Bots;
using GG2.Protocol;
using GG2.Server;
using GG2.Server.Plugins;
using Xunit;

namespace GG2.Tests;

/// <summary>
/// <c>/difficulty off|auto|0-10</c> on a duel server, typed by a stock v6 chat
/// client over a real socket. <c>docs/DUEL.md</c>, "Choosing a level".
/// </summary>
public sealed class DuelDifficultyTests
{
    private static ServerHarness Duel(bool ladder = true) =>
        new(o =>
        {
            o.Map = "duel_01";
            o.MapDirectory = Path.Combine(ReferenceTree.MapsDirectory, "duel");
            o.NavCacheDirectory = Path.Combine(ReferenceTree.RepositoryRoot, "nav", "duel");
            o.Duel = true;
            o.DuelLadder = ladder;
            o.RespawnTimeSeconds = 1;
        });

    private static int HumanSlot(GameServer server)
    {
        for (var slot = 1; slot < server.RosterCount; slot++)
        {
            if (!server.EntryOf(slot).IsBot)
                return slot;
        }

        return -1;
    }

    private static int BotSlot(GameServer server) => Assert.Single(server.BotSlots);

    private static BotSkill SkillOfBot(GameServer server) =>
        ScriptedBot.Of(server.SourceOf(BotSlot(server)))!.Skill;

    private static void WaitForBoth(ServerHarness harness) =>
        harness.TickUntil(
            () => HumanSlot(harness.Server) is var h and > 0 && harness.Server.IsAlive(h)
                && harness.Server.BotSlots.Count == 1 && harness.Server.IsAlive(BotSlot(harness.Server)),
            "the human and its bot to be alive",
            maxTicks: 400);

    /// <summary>Joins red as a soldier, says the v6 chat hello, and waits for the pair.</summary>
    private static LoopbackClient JoinAndPlay(ServerHarness harness, string name = "duellist")
    {
        var client = harness.JoinClient(name);
        SendChat(client, [0]);
        client.SendChangeTeam(Team.Red);
        client.SendChangeClass(PlayerClass.Soldier);
        WaitForBoth(harness);
        DuelServerTests.Engage(harness, client);
        return client;
    }

    /// <summary>A v6 line, as the stock chat plugin sends one: kind 1, length, text, on packet id 0.</summary>
    private static void Say(ServerHarness harness, LoopbackClient client, string text)
    {
        var bytes = GG2Text.Encode(text);
        SendChat(client, [1, (byte)bytes.Length, .. bytes]);
        harness.Tick(3);
    }

    private static void SendChat(LoopbackClient client, ReadOnlySpan<byte> data)
    {
        var packet = new byte[4 + data.Length];
        packet[0] = (byte)MessageType.PluginPacket;
        packet[1] = (byte)((data.Length + 1) & 0xFF);
        packet[2] = (byte)((data.Length + 1) >> 8);
        packet[3] = 0;
        data.CopyTo(packet.AsSpan(4));
        client.Send(packet);
    }

    private static bool Heard(LoopbackClient client, string text)
    {
        var needle = GG2Text.Encode(text);
        var haystack = client.Received;
        for (var start = 0; start + needle.Length <= haystack.Count; start++)
        {
            var match = true;
            for (var i = 0; i < needle.Length && match; i++)
                match = haystack[start + i] == needle[i];

            if (match)
                return true;
        }

        return false;
    }

    private static DuelLadder.Rating Rating(GameServer server)
    {
        var human = server.EntryOf(HumanSlot(server));
        Assert.True(server.Population!.Ladder.TryGet(human.RatingKey!, PlayerClass.Soldier, out var rating));
        return rating;
    }

    [Fact]
    public void WithNoArgumentItReportsTheLaddersLevel()
    {
        using var harness = Duel();
        var client = JoinAndPlay(harness);

        Say(harness, client, "/difficulty");

        Assert.True(Heard(client, "Your bot is on auto, at Lv 5 (Normal) for this class."));
    }

    /// <summary>
    /// A fixed level renames and re-skills the live bot at once, and a win against
    /// it moves no rating; auto then picks the rating up where it was.
    /// </summary>
    [Fact]
    public void AFixedLevelAppliesAtOnceAndMovesNoRating()
    {
        using var harness = Duel();
        var client = JoinAndPlay(harness);
        var server = harness.Server;

        Say(harness, client, "/difficulty 1");
        Assert.True(Heard(client, "Your bot is now fixed at Lv 1 (Novice) for every class."));
        Assert.Equal("Lv 1 (Novice)", server.NameOf(BotSlot(server)));
        Assert.Equal(BotLevel.Skill(1), SkillOfBot(server));

        var bot = BotSlot(server);
        server.WorldForTests.Characters[bot].Hp = 0;
        harness.TickUntil(() => !server.IsAlive(bot), "the bot to die", maxTicks: 3);
        WaitForBoth(harness);

        Assert.Equal(0, Rating(server).Duels);
        Assert.Equal("Lv 1 (Novice)", server.NameOf(BotSlot(server)));

        Say(harness, client, "/difficulty auto");
        Assert.True(Heard(client, "Your bot is now on auto, at Lv 5 (Normal) for this class."));
        Assert.Equal("Lv 5 (Normal)", server.NameOf(BotSlot(server)));
        Assert.Equal(BotLevel.Skill(5), SkillOfBot(server));
    }

    /// <summary>Off takes the bot away and keeps the human on red in their cell; auto brings one back.</summary>
    [Fact]
    public void OffRemovesTheBotAndAutoBringsItBack()
    {
        using var harness = Duel();
        var client = JoinAndPlay(harness);
        var server = harness.Server;
        var human = HumanSlot(server);
        var cell = server.WorldForTests.Players[human].SpawnPin;

        Say(harness, client, "/difficulty off");

        Assert.True(Heard(client, "Your bot is now off: you have your cell to yourself."));
        Assert.Empty(server.BotSlots);
        human = HumanSlot(server);
        Assert.Equal(Team.Red, server.TeamOf(human));
        Assert.Equal(cell, server.WorldForTests.Players[human].SpawnPin);

        // Dying alone is no loss and brings no bot.
        server.WorldForTests.Characters[human].Hp = 0;
        harness.Tick(60);
        Assert.Empty(server.BotSlots);

        Say(harness, client, "/difficulty auto");
        WaitForBoth(harness);
        Assert.Equal("Lv 5 (Normal)", server.NameOf(BotSlot(server)));
    }

    /// <summary>
    /// Kept by address and name like a rating, so the reconnect a stock client
    /// makes at every duel map change does not undo it.
    /// </summary>
    [Fact]
    public void TheSettingOutlivesAReconnect()
    {
        using var harness = Duel();
        var client = JoinAndPlay(harness);
        var server = harness.Server;

        Say(harness, client, "/difficulty 8");
        Assert.Equal("Lv 8 (Hard)", server.NameOf(BotSlot(server)));

        client.Dispose();
        harness.TickUntil(() => HumanSlot(server) < 0 && server.BotSlots.Count == 0, "the pair to leave", maxTicks: 200);

        JoinAndPlay(harness);
        Assert.Equal("Lv 8 (Hard)", server.NameOf(BotSlot(server)));
        Assert.Equal(BotLevel.Skill(8), SkillOfBot(server));
    }

    /// <summary>With the ladder off a fixed level still applies, and auto is the server's Difficulty again.</summary>
    [Fact]
    public void AFixedLevelAppliesWithTheLadderOff()
    {
        using var harness = Duel(ladder: false);
        var client = JoinAndPlay(harness);
        var server = harness.Server;

        Say(harness, client, "/difficulty 2");
        Assert.Equal("Lv 2 (Novice)", server.NameOf(BotSlot(server)));
        Assert.Equal(BotLevel.Skill(2), SkillOfBot(server));

        Say(harness, client, "/difficulty auto");
        Assert.True(Heard(client, "Your bot is now on auto, at the server's difficulty."));
        Assert.Equal("[BOT] Soldier", server.NameOf(BotSlot(server)));
        Assert.Equal(BotSkill.ForTier(harness.Options.Bots.Difficulty), SkillOfBot(server));
    }

    [Theory]
    [InlineData("11")]
    [InlineData("-1")]
    [InlineData("hard")]
    [InlineData("5.5")]
    public void AnythingElseIsAnsweredWithTheHelpLine(string argument)
    {
        using var harness = Duel();
        var client = JoinAndPlay(harness);

        Say(harness, client, "/difficulty " + argument);

        Assert.True(Heard(client, "/difficulty [off|auto|0-10]: your duel bot's level."));
        Assert.Equal("Lv 5 (Normal)", harness.Server.NameOf(BotSlot(harness.Server)));
    }

    /// <summary>Not a duel server: refused, and not listed by /help.</summary>
    [Fact]
    public void OffADuelServerItIsRefusedAndUnlisted()
    {
        using var harness = new ServerHarness();
        var client = harness.JoinClient("player");
        SendChat(client, [0]);
        harness.Tick(3);

        Say(harness, client, "/difficulty 3");
        Say(harness, client, "/help");

        Assert.True(Heard(client, "/difficulty is for duel servers."));
        Assert.False(Heard(client, " /difficulty"));
    }

    /// <summary>On a duel server /help lists it.</summary>
    [Fact]
    public void ADuelServersHelpListsIt()
    {
        using var harness = Duel();
        var client = JoinAndPlay(harness);

        Say(harness, client, "/help");

        Assert.True(Heard(client, " /difficulty"));
    }
}
