using GG2.Maps;
using GG2.Protocol;
using GG2.Server;
using GG2.Simulation;
using Xunit;

namespace GG2.Tests;

/// <summary>
/// <c>--duel</c>: one blue bot per red human, of the human's class, pinned to
/// the human's cell, killed (uncredited) after the kill cam when the human dies,
/// the human healed and moved when the bot dies, the bot removed when the human
/// goes. <c>docs/DUEL.md</c>.
/// </summary>
/// <remarks>
/// Through a real <see cref="GameServer"/> and a loopback client, on the shipped
/// <c>duel_01</c>. What it cannot show is how any of this looks to a stock client.
/// </remarks>
public sealed class DuelServerTests
{
    private static ServerHarness Duel(byte respawnSeconds = 1) =>
        new(o =>
        {
            o.Map = "duel_01";
            o.MapDirectory = Path.Combine(ReferenceTree.MapsDirectory, "duel");
            o.NavCacheDirectory = Path.Combine(ReferenceTree.RepositoryRoot, "nav", "duel");
            o.Duel = true;
            o.RespawnTimeSeconds = respawnSeconds;
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

    /// <summary>Joins, picks a team and a class, and waits until human and bot are both alive.</summary>
    private static LoopbackClient JoinAndPlay(ServerHarness harness, Team team, PlayerClass playerClass)
    {
        var client = harness.JoinClient("duellist");
        client.SendChangeTeam(team);
        client.SendChangeClass(playerClass);

        harness.TickUntil(
            () => HumanSlot(harness.Server) is var h and > 0 && harness.Server.IsAlive(h)
                && harness.Server.BotSlots.Count == 1 && harness.Server.IsAlive(BotSlot(harness.Server)),
            "the human and its bot to be alive",
            maxTicks: 300);

        return client;
    }

    private static SpawnPoints Spawns(ServerHarness harness)
    {
        var path = Path.Combine(harness.Options.MapDirectory, harness.Options.Map + ".png");
        return new SpawnPoints(LevelMap.FromPng(harness.Options.Map, File.ReadAllBytes(path)));
    }

    [Fact]
    public void AHumanOnRedGetsOneBlueBotOfTheirClassInTheirCell()
    {
        using var harness = Duel();
        JoinAndPlay(harness, Team.Red, PlayerClass.Soldier);

        var server = harness.Server;
        var human = HumanSlot(server);
        var bot = BotSlot(server);
        ref var world = ref server.WorldForTests;

        Assert.Equal(Team.Red, server.TeamOf(human));
        Assert.Equal(Team.Blue, server.TeamOf(bot));
        Assert.Equal(PlayerClass.Soldier, server.ClassOf(bot));

        var cell = world.Players[human].SpawnPin;
        Assert.NotNull(cell);
        Assert.Equal(cell, world.Players[bot].SpawnPin);

        // Spawned AT the cell's two points: neither has had a tick of input yet
        // that could move it sideways from where it appeared.
        var spawns = Spawns(harness);
        Assert.True(spawns.TryGet(Team.Red, 0, cell.Value, out var redX, out _));
        Assert.True(spawns.TryGet(Team.Blue, 0, cell.Value, out var blueX, out _));
        Assert.Equal(redX, world.Characters[human].X);
        Assert.Equal(blueX, world.Characters[bot].X);
    }

    [Fact]
    public void AHumanWhoPicksBlueIsMovedToRed()
    {
        using var harness = Duel();
        JoinAndPlay(harness, Team.Blue, PlayerClass.Scout);

        Assert.Equal(Team.Red, harness.Server.TeamOf(HumanSlot(harness.Server)));
        Assert.Equal(Team.Blue, harness.Server.TeamOf(BotSlot(harness.Server)));
    }

    /// <summary>
    /// The human dies (here by picking a class, a death by their own hand), so
    /// the bot dies too with nobody credited — even though the human had hurt it
    /// last — and both are re-pinned to a different cell and come back in it, the
    /// bot as the human's new class.
    /// </summary>
    [Fact]
    public void TheHumansDeathMovesThePairAndKillsTheBotUncredited()
    {
        using var harness = Duel();
        var client = JoinAndPlay(harness, Team.Red, PlayerClass.Soldier);

        var server = harness.Server;
        var human = HumanSlot(server);
        var bot = BotSlot(server);
        var before = server.WorldForTests.Players[human].SpawnPin;

        // The human hurt the bot last: without the credit being cleared, the
        // bot's death would be FINISHED_OFF for the human and a point for red.
        server.WorldForTests.Characters[bot].LastDamageDealer = (byte)human;

        // Waited on by the death COUNT: both have just spawned and are still under
        // Team Deathmatch's spawn protection, and a protected death respawns in
        // the very next alarm stage (Destroy.xml:37), so "dead" may never be seen.
        client.SendChangeClass(PlayerClass.Heavy);
        harness.TickUntil(() => server.DeathsOf(bot) > 0, "the bot to die with its human", maxTicks: 5);

        Assert.Equal(0, server.RedCaps);
        Assert.Equal(0, server.BlueCaps);

        var after = server.WorldForTests.Players[human].SpawnPin;
        Assert.NotNull(after);
        Assert.NotEqual(before, after);
        Assert.Equal(after, server.WorldForTests.Players[bot].SpawnPin);
        Assert.Equal(PlayerClass.Heavy, server.ClassOf(bot));

        harness.TickUntil(() => server.IsAlive(human) && server.IsAlive(bot), "both to respawn", maxTicks: 120);

        var spawns = Spawns(harness);
        Assert.True(spawns.TryGet(Team.Red, 0, after.Value, out var redX, out _));
        Assert.Equal(redX, server.WorldForTests.Characters[human].X);
    }

    /// <summary>
    /// With two humans, each bot's only opponent is its own human — the table
    /// every bot view carries (<see cref="WorldView.MayOppose"/>).
    /// </summary>
    [Fact]
    public void EachBotMayOpposeOnlyItsOwnHuman()
    {
        using var harness = Duel();
        var first = harness.JoinClient("first");
        first.SendChangeTeam(Team.Red);
        first.SendChangeClass(PlayerClass.Soldier);
        var second = harness.JoinClient("second");
        second.SendChangeTeam(Team.Red);
        second.SendChangeClass(PlayerClass.Scout);

        var server = harness.Server;
        harness.TickUntil(() => server.BotSlots.Count == 2, "two bots", maxTicks: 300);
        harness.Tick(2);

        var view = new WorldView(in server.WorldForTests, new CollisionWorld(
            LevelMap.FromPng(harness.Options.Map, File.ReadAllBytes(Path.Combine(harness.Options.MapDirectory, harness.Options.Map + ".png")))),
            server.OpponentsForTests);

        var humans = Enumerable.Range(1, server.RosterCount - 1).Where(s => !server.EntryOf(s).IsBot).ToArray();
        Assert.Equal(2, humans.Length);

        foreach (var bot in server.BotSlots)
        {
            var mine = humans.Single(h => server.ClassOf(h) == server.ClassOf(bot));
            var theirs = humans.Single(h => h != mine);

            Assert.True(view.MayOppose(bot, mine));
            Assert.False(view.MayOppose(bot, theirs));
        }
    }

    [Fact]
    public void TheBotGoesWhenItsHumanGoes()
    {
        using var harness = Duel();
        var client = JoinAndPlay(harness, Team.Red, PlayerClass.Pyro);

        client.Dispose();
        harness.TickUntil(() => harness.Server.BotSlots.Count == 0, "the bot to be removed", maxTicks: 300);
    }

    /// <summary>
    /// An unprotected human death holds the bot alive for the stock client's kill
    /// cam (<c>DeathCam</c> frames the killer 60 frames after the death), then
    /// kills it uncredited and spawns it on the same tick as its human.
    /// </summary>
    [Fact]
    public void TheBotOutlivesItsHumanForTheKillCamThenSpawnsWithThem()
    {
        using var harness = Duel(respawnSeconds: 5);
        var client = JoinAndPlay(harness, Team.Red, PlayerClass.Soldier);

        var server = harness.Server;
        var human = HumanSlot(server);
        var bot = BotSlot(server);

        // Past spawn protection, or the death respawns next tick and the bot dies
        // at once (the case above).
        harness.TickUntil(
            () => Gml.Equal(server.WorldForTests.Characters[human].DeathmatchInvulnerable, 0),
            "spawn protection to end",
            maxTicks: 300);

        server.WorldForTests.Characters[bot].LastDamageDealer = (byte)human;
        client.SendChangeClass(PlayerClass.Heavy);
        harness.TickUntil(() => server.DeathsOf(human) > 0, "the human to die", maxTicks: 5);

        var held = 0;
        while (server.IsAlive(bot) && held < 200)
        {
            harness.Tick();
            held++;
        }

        // Past the 60-frame snapshot with a margin, and no later than the hold.
        Assert.InRange(held, 70, BotPopulationService.KillCamTicks + 1);
        Assert.Equal(0, server.RedCaps);
        Assert.Equal(PlayerClass.Heavy, server.ClassOf(bot));
        Assert.Equal(server.WorldForTests.Players[human].SpawnPin, server.WorldForTests.Players[bot].SpawnPin);

        harness.TickUntil(() => server.IsAlive(human) || server.IsAlive(bot), "a respawn", maxTicks: 200);
        Assert.True(server.IsAlive(human));
        Assert.True(server.IsAlive(bot));
    }

    /// <summary>
    /// The bot's death is the human's win: after a beat they are healed to full
    /// and moved, at rest, to a different cell's red spawn, and the bot appears
    /// there with them rather than a respawn time later.
    /// </summary>
    [Fact]
    public void TheBotsDeathHealsAndMovesTheHumanWithTheBot()
    {
        using var harness = Duel(respawnSeconds: 5);
        JoinAndPlay(harness, Team.Red, PlayerClass.Soldier);

        var server = harness.Server;
        var human = HumanSlot(server);
        var bot = BotSlot(server);
        var cell = server.WorldForTests.Players[human].SpawnPin;
        server.WorldForTests.Characters[human].Hp = 12;

        server.RequestBotClass(bot, PlayerClass.Soldier);
        harness.TickUntil(() => server.WorldForTests.Players[human].SpawnPin != cell, "the new cell to be picked", maxTicks: 3);

        // The beat: still here, still hurt.
        var moved = server.WorldForTests.Players[human].SpawnPin!.Value;
        Assert.True(Spawns(harness).TryGet(Team.Red, 0, moved, out var redX, out _));
        harness.Tick(BotPopulationService.WinMoveTicks - 5);
        Assert.NotEqual(redX, server.WorldForTests.Characters[human].X);
        Assert.Equal(12, server.WorldForTests.Characters[human].Hp);
        Assert.False(server.IsAlive(bot));

        harness.TickUntil(() => server.WorldForTests.Characters[human].X == redX, "the human to be moved", maxTicks: 10);

        Assert.True(server.IsAlive(human));
        Assert.Equal(ClassStats.For(PlayerClass.Soldier).MaxHp, server.WorldForTests.Characters[human].Hp);
        Assert.Equal(moved, server.WorldForTests.Players[bot].SpawnPin);

        // Within a tick of the move, not after the five-second respawn.
        if (!server.IsAlive(bot))
            harness.Tick();
        Assert.True(server.IsAlive(bot));
        Assert.True(Spawns(harness).TryGet(Team.Blue, 0, moved, out var blueX, out _));
        Assert.Equal(blueX, server.WorldForTests.Characters[bot].X);
    }

    /// <summary>
    /// A duel bot moves off its spawn at once: no regroup hold, which waits for a
    /// team-mate a duel never has, and no wait for the next objective period.
    /// </summary>
    [Theory]
    [InlineData(PlayerClass.Soldier)]
    [InlineData(PlayerClass.Scout)]
    [InlineData(PlayerClass.Heavy)]
    public void ADuelBotMovesOffItsSpawnAtOnce(PlayerClass playerClass)
    {
        using var harness = Duel();
        var client = harness.JoinClient("duellist");
        client.SendChangeTeam(Team.Red);
        client.SendChangeClass(playerClass);

        var server = harness.Server;
        harness.TickUntil(() => server.BotSlots.Count == 1 && server.IsAlive(BotSlot(server)), "the bot to spawn", maxTicks: 300);

        var bot = BotSlot(server);
        var still = 0;
        while ((server.WorldForTests.Characters[bot].KeyState & (PlayerKeys.Left | PlayerKeys.Right)) == 0 && still < 60)
        {
            harness.Tick();
            still++;
        }

        // Measured 3 ticks for each; the hold alone used to be up to 90.
        Assert.InRange(still, 0, 10);
    }

    /// <summary>A bot that dies while its human is dead is no win, and moves nobody.</summary>
    [Fact]
    public void ABotDeathWhileItsHumanIsDeadMovesNobody()
    {
        using var harness = Duel(respawnSeconds: 5);
        var client = JoinAndPlay(harness, Team.Red, PlayerClass.Soldier);

        var server = harness.Server;
        var human = HumanSlot(server);
        var bot = BotSlot(server);
        harness.TickUntil(
            () => Gml.Equal(server.WorldForTests.Characters[human].DeathmatchInvulnerable, 0),
            "spawn protection to end",
            maxTicks: 300);

        client.SendChangeClass(PlayerClass.Soldier);
        harness.TickUntil(() => server.DeathsOf(human) > 0, "the human to die", maxTicks: 5);
        var cell = server.WorldForTests.Players[human].SpawnPin;

        server.RequestBotClass(bot, PlayerClass.Soldier);
        harness.Tick(5);

        Assert.Equal(cell, server.WorldForTests.Players[human].SpawnPin);
        Assert.Equal(cell, server.WorldForTests.Players[bot].SpawnPin);

        harness.TickUntil(() => server.IsAlive(human), "the human to respawn", maxTicks: 200);
        Assert.True(server.IsAlive(bot));
    }
}
