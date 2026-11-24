using GG2.Bots;
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
            ProtectSpawns(o);

            // These are about the pairing, at the server's Difficulty;
            // DuelLadderTests has the ladder.
            o.DuelLadder = false;
        });

    /// <summary>
    /// Team Deathmatch spawn protection at the global default, set explicitly so
    /// the duel server's own default of none (<see cref="DuelDefaults"/>) does not
    /// apply. These tests are about pairing under protection: a class change while
    /// protected is a death, and the hold-fire rule only exists while it lasts.
    /// </summary>
    private static void ProtectSpawns(ServerOptions o)
    {
        o.TdmInvulnerabilitySeconds = 5;
        o.ExplicitSettings.Add(nameof(ServerOptions.TdmInvulnerabilitySeconds));
    }

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

        Engage(harness, client);
        return client;
    }

    /// <summary>
    /// Presses Down for a tick, which is the human closing the class menu: until
    /// then their bot has no opponent. Down rather than a direction, so the
    /// human stays on their spawn, and it does not end spawn protection.
    /// </summary>
    internal static void Engage(ServerHarness harness, LoopbackClient client)
    {
        client.SendInputState(new PlayerInput(PlayerKeys.Down, 0, 0));
        harness.TickUntil(
            () => HumanSlot(harness.Server) is var h and > 0 && harness.Server.EntryOf(h).DuelEngaged,
            "the human's key to engage its bot",
            maxTicks: 30);
        client.SendInputState(new PlayerInput(PlayerKeys.None, 0, 0));
        harness.Tick();
    }

    /// <summary>
    /// A human who has pressed nothing since joining — the stock client's class
    /// menu sends a key byte of zero — is not fought: the bot neither fires nor
    /// walks, past the end of spawn protection, until the human's first key.
    /// </summary>
    [Fact]
    public void ADuelBotWaitsForItsHumansFirstKey()
    {
        using var harness = Duel();
        var client = harness.JoinClient("duellist");
        client.SendChangeTeam(Team.Red);
        client.SendChangeClass(PlayerClass.Soldier);

        var server = harness.Server;
        harness.TickUntil(
            () => HumanSlot(server) is var h and > 0 && server.IsAlive(h)
                && server.BotSlots.Count == 1 && server.IsAlive(BotSlot(server)),
            "the human and its bot to be alive",
            maxTicks: 300);

        var human = HumanSlot(server);
        var bot = BotSlot(server);
        var start = server.WorldForTests.Characters[bot].X;

        // Twice the 150 ticks of spawn protection, so the hold is not the
        // protection's.
        for (var t = 0; t < 300; t++)
        {
            harness.Tick();
            Assert.Equal(PlayerKeys.None, server.WorldForTests.Characters[bot].KeyState);
        }

        Assert.False(server.EntryOf(human).DuelEngaged);
        Assert.Equal(start, server.WorldForTests.Characters[bot].X);
        Assert.True(Gml.Equal(server.WorldForTests.Characters[human].DeathmatchInvulnerable, 0));

        Engage(harness, client);

        harness.TickUntil(
            () => (server.WorldForTests.Characters[bot].KeyState & (PlayerKeys.Left | PlayerKeys.Right | PlayerKeys.Attack)) != 0,
            "the bot to move or fire once its human has pressed a key",
            maxTicks: 30);
    }

    /// <summary>
    /// The wait re-arms in every new cell: after a death the bot stands still
    /// until a key arrives, so a player can type in chat before fighting.
    /// </summary>
    [Fact]
    public void AfterADeathTheBotWaitsForAKeyAgain()
    {
        using var harness = Duel();
        var client = JoinAndPlay(harness, Team.Red, PlayerClass.Soldier);
        var server = harness.Server;
        var human = HumanSlot(server);

        client.SendChangeClass(PlayerClass.Heavy);
        harness.TickUntil(() => server.DeathsOf(human) > 0, "the human to die", maxTicks: 5);
        Assert.False(server.EntryOf(human).DuelEngaged);

        harness.TickUntil(() => server.IsAlive(human) && server.IsAlive(BotSlot(server)), "both to respawn", maxTicks: 120);
        var bot = BotSlot(server);
        var start = server.WorldForTests.Characters[bot].X;
        for (var t = 0; t < 120; t++)
        {
            harness.Tick();
            Assert.Equal(PlayerKeys.None, server.WorldForTests.Characters[bot].KeyState);
        }

        Assert.Equal(start, server.WorldForTests.Characters[bot].X);
        Engage(harness, client);
    }

    /// <summary>
    /// A key held as the human died is not a key pressed since: a stock client
    /// sends nothing while dead, so the server still holds that input when the
    /// human respawns, and taking it for a fresh press would let a player who
    /// holds Attack through a death keep the bot frozen for good.
    /// </summary>
    [Fact]
    public void AKeyHeldThroughADeathDoesNotEngage()
    {
        using var harness = Duel();
        var client = JoinAndPlay(harness, Team.Red, PlayerClass.Soldier);
        var server = harness.Server;
        var human = HumanSlot(server);

        client.SendInputState(new PlayerInput(PlayerKeys.Down, 0, 0));
        harness.Tick(2);
        client.SendChangeClass(PlayerClass.Heavy);
        harness.TickUntil(() => server.DeathsOf(human) > 0, "the human to die", maxTicks: 5);
        harness.TickUntil(() => server.IsAlive(human), "the human to respawn", maxTicks: 120);
        harness.Tick(30);

        Assert.Equal(PlayerKeys.Down, server.KeysOf(human));
        Assert.False(server.EntryOf(human).DuelEngaged);

        // The same keys, sent again from the new life, do engage.
        client.SendInputState(new PlayerInput(PlayerKeys.Down, 0, 0));
        harness.TickUntil(() => server.EntryOf(human).DuelEngaged, "a fresh input to engage", maxTicks: 5);
    }

    /// <summary>After a win the human is moved to a new cell, and the bot there waits for a key too.</summary>
    [Fact]
    public void AfterAWinsMoveTheBotWaitsForAKeyAgain()
    {
        using var harness = Duel(respawnSeconds: 5);
        var client = JoinAndPlay(harness, Team.Red, PlayerClass.Soldier);
        var server = harness.Server;
        var human = HumanSlot(server);
        var bot = BotSlot(server);

        server.WorldForTests.Characters[bot].Hp = 0;
        harness.TickUntil(() => server.EntryOf(human).DuelMoveIn > 0, "the win", maxTicks: 5);
        harness.TickUntil(() => server.EntryOf(human).DuelMoveIn == 0, "the move", maxTicks: BotPopulationService.WinMoveTicks + 10);
        Assert.False(server.EntryOf(human).DuelEngaged);

        harness.Tick(60);
        Assert.False(server.EntryOf(human).DuelEngaged);
        Assert.Equal(PlayerKeys.None, server.WorldForTests.Characters[bot].KeyState);

        Engage(harness, client);
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

        // Before either human has pressed a key, neither bot may oppose anybody.
        foreach (var bot in server.BotSlots)
        {
            for (var other = 1; other < server.RosterCount; other++)
            {
                if (other != bot)
                    Assert.False(server.OpponentsForTests[bot] == other, $"bot {bot} may oppose {other} before any key");
            }
        }

        first.SendInputState(new PlayerInput(PlayerKeys.Down, 0, 0));
        second.SendInputState(new PlayerInput(PlayerKeys.Down, 0, 0));
        harness.TickUntil(
            () => Enumerable.Range(1, server.RosterCount - 1).All(s => server.EntryOf(s).IsBot || server.EntryOf(s).DuelEngaged),
            "both humans' keys to engage their bots",
            maxTicks: 30);
        harness.Tick();

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
    /// The bot's death is the human's win: after a beat they are respawned, with
    /// no death, at a different cell's red spawn (full hp and a full clip, told to
    /// the client by a <c>PLAYER_SPAWN</c>), and the bot appears there with them
    /// rather than a respawn time later.
    /// </summary>
    [Fact]
    public void TheBotsDeathHealsAndMovesTheHumanWithTheBot()
    {
        using var harness = Duel(respawnSeconds: 5);
        var client = JoinAndPlay(harness, Team.Red, PlayerClass.Soldier);

        var server = harness.Server;
        var human = HumanSlot(server);
        var bot = BotSlot(server);
        var cell = server.WorldForTests.Players[human].SpawnPin;
        server.WorldForTests.Characters[human].Hp = 12;
        server.WorldForTests.Weapons[human].AmmoCount = 1;
        var deaths = server.DeathsOf(human);

        server.RequestBotClass(bot, PlayerClass.Soldier);
        harness.TickUntil(() => server.WorldForTests.Players[human].SpawnPin != cell, "the new cell to be picked", maxTicks: 3);

        // The beat: still here, still hurt.
        var moved = server.WorldForTests.Players[human].SpawnPin!.Value;
        Assert.True(Spawns(harness).TryGet(Team.Red, 0, moved, out var redX, out _));
        harness.Tick(BotPopulationService.WinMoveTicks - 5);
        Assert.NotEqual(redX, server.WorldForTests.Characters[human].X);
        Assert.Equal(12, server.WorldForTests.Characters[human].Hp);
        Assert.False(server.IsAlive(bot));

        var heard = client.Received.Count;
        harness.TickUntil(() => server.WorldForTests.Characters[human].X == redX, "the human to be moved", maxTicks: 10);

        Assert.True(server.IsAlive(human));
        Assert.Equal(deaths, server.DeathsOf(human));
        Assert.Equal(ClassStats.For(PlayerClass.Soldier).MaxHp, server.WorldForTests.Characters[human].Hp);
        Assert.Equal(WeaponState.Spawn(PlayerClass.Soldier).AmmoCount, server.WorldForTests.Weapons[human].AmmoCount);

        Assert.Equal(moved, server.WorldForTests.Players[bot].SpawnPin);

        // Within a tick of the move, not after the five-second respawn.
        if (!server.IsAlive(bot))
            harness.Tick();
        Assert.True(server.IsAlive(bot));
        Assert.True(Spawns(harness).TryGet(Team.Blue, 0, moved, out var blueX, out _));
        Assert.Equal(blueX, server.WorldForTests.Characters[bot].X);

        // PLAYER_SPAWN, slot, point, group 0: the client replaces its character.
        harness.Tick();
        byte[] spawn = [(byte)MessageType.PlayerSpawn, (byte)human, (byte)moved, 0];
        Assert.True(
            client.Received.Skip(heard).ToArray().AsSpan().IndexOf(spawn) >= 0,
            "the client was not sent the human's spawn");
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
        Engage(harness, client);

        var bot = BotSlot(server);
        var still = 0;
        while ((server.WorldForTests.Characters[bot].KeyState & (PlayerKeys.Left | PlayerKeys.Right)) == 0 && still < 60)
        {
            harness.Tick();
            still++;
        }

        // From the human's first key, which is when a duel bot is given its
        // target: measured 18 for each, the default skill's 15-tick AcquireTicks
        // plus the engage. The regroup hold this guards against was up to 90.
        Assert.InRange(still, 0, 25);
    }

    /// <summary>
    /// A bot does not press a key that ends its own spawn protection while its
    /// human is still protected, and fires once that protection ends.
    /// <c>BotSpawnProtection</c>.
    /// </summary>
    /// <remarks>
    /// Under both brains the live duel server can run: the scripted layer, and
    /// the shipped nets under the distance handover (<c>duel.ini</c>), with the
    /// range widened so the nets hold the wheel while the human is protected —
    /// at the live 150 px they do not, and the policy rows would test the
    /// scripted layer again. The soldier net is the row that needs the
    /// nearest-enemy fallback (it fires four ticks in, before the scripted layer
    /// has a target), and it skips the second half: forced to drive against an
    /// idle human it never fires once protection ends, with or without the hold. Without the
    /// hold every class pressed Attack 15-20 ticks after spawning, against a human
    /// with 120+ ticks of protection left. The second half is what keeps the first
    /// from passing on a bot that never fires at all.
    /// </remarks>
    [Theory]
    [InlineData(PlayerClass.Soldier, false, true)]
    [InlineData(PlayerClass.Heavy, false, true)]
    [InlineData(PlayerClass.Soldier, true, false)]
    [InlineData(PlayerClass.Scout, true, true)]
    [InlineData(PlayerClass.Heavy, true, true)]
    public void ADuelBotHoldsFireWhileItsHumanIsSpawnProtected(
        PlayerClass playerClass, bool policy, bool firesAfter)
    {
        using var harness = new ServerHarness(o =>
        {
            if (policy)
            {
                var nets = Path.Combine(ReferenceTree.RepositoryRoot, "checkpoints");
                ServerConfigFile.ApplyText(o, System.Text.Json.JsonSerializer.Serialize(new
                {
                    // A range past the cell, so the nets drive the whole approach
                    // rather than only inside 150 px.
                    Bots = new
                    {
                        HandoverRule = "distance",
                        HandoverRange = 5000,
                        Policy = new[]
                        {
                            Path.Combine(nets, "ppo-warm-soldier-s1.gg2p"),
                            Path.Combine(nets, "ppo-finetune-scout-s1.gg2p"),
                            Path.Combine(nets, "ppo-finetune2-heavy-s1.gg2p"),
                        },
                        ClassPolicy = new[] { "Soldier=ppo-warm-soldier-s1", "Scout=ppo-finetune-scout-s1", "Heavy=ppo-finetune2-heavy-s1" },
                    },
                }), Path.Combine(nets, "test.json"));
                BotBrain.Build(o);
            }

            o.Map = "duel_01";
            o.MapDirectory = Path.Combine(ReferenceTree.MapsDirectory, "duel");
            o.NavCacheDirectory = Path.Combine(ReferenceTree.RepositoryRoot, "nav", "duel");
            o.Duel = true;
            o.DuelLadder = false;
            ProtectSpawns(o);
        });

        JoinAndPlay(harness, Team.Red, playerClass);

        var server = harness.Server;
        var human = HumanSlot(server);
        var bot = BotSlot(server);
        var protectedTicks = 0;
        var policyTicks = 0;

        // The loopback human presses nothing, so it keeps its protection the full
        // 150 ticks standing still.
        while (!Gml.Equal(server.WorldForTests.Characters[human].DeathmatchInvulnerable, 0))
        {
            Assert.Equal(PlayerKeys.None, server.WorldForTests.Characters[bot].KeyState & PlayerKeys.Attack);
            harness.Tick();

            if (HybridBot.Of(server.SourceOf(bot)) is { MicroDrove: true })
                policyTicks++;
            Assert.InRange(++protectedTicks, 0, 300);
        }

        Assert.InRange(protectedTicks, 100, 300);

        if (policy)
            Assert.InRange(policyTicks, 50, 300);

        if (!firesAfter)
            return;

        harness.TickUntil(
            () => (server.WorldForTests.Characters[bot].KeyState & PlayerKeys.Attack) != 0,
            "the bot to fire once its human is unprotected",
            maxTicks: 60);
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
