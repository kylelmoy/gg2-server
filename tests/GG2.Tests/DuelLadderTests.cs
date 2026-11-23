using System.Text;
using GG2.Bots;
using GG2.Protocol;
using GG2.Server;
using GG2.Simulation;
using Xunit;

namespace GG2.Tests;

/// <summary>
/// The duel server's difficulty ladder (<see cref="DuelLadder"/>): the levels'
/// skills and names, the staircase that moves a rating, and the duel service
/// applying it. <c>docs/DUEL.md</c>, "The difficulty ladder".
/// </summary>
public sealed class DuelLadderTests
{
    /// <summary>
    /// Every server tier is a whole level on the one scale: a tier's bot is that
    /// level's bot, wherever it plays. Tier 5 is the top, the nets as trained.
    /// </summary>
    [Fact]
    public void EveryTierIsALevel()
    {
        for (var tier = BotConstants.TierMin; tier <= BotConstants.TierMax; tier++)
        {
            var level = BotLevel.OfTier(tier);
            Assert.Equal(BotSkill.ForTier(tier), BotLevel.Skill(level));

            if (tier > BotConstants.TierMin)
                Assert.True(level > BotLevel.OfTier(tier - 1), $"tier {tier} is not above tier {tier - 1}");
        }

        Assert.Equal(BotLevel.Max, BotLevel.OfTier(BotConstants.TierMax));
        Assert.False(BotSkill.ForTier(BotConstants.TierMax).HumanAim);
        Assert.True(BotSkill.ForTier(BotConstants.TierMax - 1).HumanAim);
    }

    /// <summary>
    /// The duel server's levels: the top is exactly tier 5, level 3 is the skill
    /// floor played fully like a person, and no knob gets weaker going up, a
    /// quarter level at a time.
    /// </summary>
    [Fact]
    public void TheDuelLevelsRiseEvenlyToTierFive()
    {
        Assert.Equal(BotSkill.ForTier(5), BotLevel.Skill(DuelLadder.MaxLevel));
        Assert.Equal(BotSkill.For(0.15).Human(true), BotLevel.Skill(3));

        var previous = BotLevel.Skill(0);
        for (var r = 0.25; r <= DuelLadder.MaxLevel; r += 0.25)
        {
            var here = BotLevel.Skill(r);
            var human = here.HumanAim ? here.HumanAimStrength : 0;
            var humanBefore = previous.HumanAim ? previous.HumanAimStrength : 0;

            // Levels 1-3 all play fully like a person; above 3 that fades.
            Assert.True(
                r <= 3 ? human == 1 : human < humanBefore || (human == 0 && humanBefore == 0),
                $"human aim did not fall at {r}");
            Assert.True(here.HoldFireChance <= previous.HoldFireChance, $"hold-fire rose at {r}");
            // The turn-around applies only where the bot plays like a person.
            Assert.True(!here.HumanAim || here.ReversalCooldownTicks <= previous.ReversalCooldownTicks, $"the turn-around rose at {r}");
            Assert.True(here.PolicyAimErrorDeg <= previous.PolicyAimErrorDeg, $"aim error rose at {r}");
            Assert.True(here.FireDelayTicks <= previous.FireDelayTicks, $"fire delay rose at {r}");
            Assert.True(here.PolicyReflex <= previous.PolicyReflex, $"the rocket reaction slowed at {r}");
            Assert.True(here.ReflexMissChance <= previous.ReflexMissChance, $"the rocket miss rose at {r}");
            previous = here;
        }
    }

    /// <summary>
    /// The two Novice levels are weaker than the skill floor in every knob that
    /// reaches a net that always drives, strictly, and level 2 is between.
    /// </summary>
    [Fact]
    public void TheNoviceLevelsAreWeakerThanTheFloor()
    {
        var floor = BotLevel.Skill(3);
        var two = BotLevel.Skill(2);
        var one = BotLevel.Skill(1);

        foreach (var (upper, lower) in new[] { (floor, two), (two, one) })
        {
            Assert.True(lower.HoldFireChance > upper.HoldFireChance);
            Assert.True(lower.PolicyAimErrorDeg > upper.PolicyAimErrorDeg);
            Assert.True(lower.FireDelayTicks > upper.FireDelayTicks);
            Assert.True(lower.ReversalCooldownTicks > upper.ReversalCooldownTicks);
            Assert.True(lower.PolicyReflex > upper.PolicyReflex);
        }

        Assert.Equal(floor, floor.Novice(0));
        Assert.Equal(BotLevel.Skill(0), BotLevel.Skill(-5));
    }

    /// <summary>
    /// Every rating that shows as level 0 never fires; level 1 and above always
    /// might, and the hold-fire climbs without a gap to certainty on the way down.
    /// </summary>
    [Fact]
    public void LevelZeroNeverFires()
    {
        foreach (var r in new[] { 0, 0.2, 0.49 })
        {
            Assert.True(BotLevel.Skill(r).NeverFires, $"rating {r} fires");
            Assert.Equal(0, DuelLadder.LevelOf(r));
        }

        foreach (var r in new[] { 0.5, 1, 2, 5, 10 })
            Assert.False(BotLevel.Skill(r).NeverFires, $"rating {r} never fires");

        Assert.Equal(0.85, BotLevel.Skill(0.5).HoldFireChance, 9);
        Assert.Equal(0.70, BotLevel.Skill(1).HoldFireChance, 9);
        Assert.False(BotSkill.ForTier(1).NeverFires);
    }

    /// <summary>The duel server's ladder runs 0-10; auto difficulty's from tier 1's level to 10.</summary>
    [Fact]
    public void TheDuelLadderRunsFromZeroToTen()
    {
        var duel = new DuelLadder().For("p", PlayerClass.Soldier, 9, 0);
        DuelLadder.Record(duel, won: true, winnerHealth: 1, targetWinRate: 0.6);
        DuelLadder.Record(duel, won: true, winnerHealth: 1, targetWinRate: 0.6);
        Assert.Equal(DuelLadder.MaxLevel, duel.Value);

        var low = new DuelLadder().For("q", PlayerClass.Soldier, 1, 0);
        DuelLadder.Record(low, won: false, winnerHealth: 1, targetWinRate: 0.6);
        DuelLadder.Record(low, won: false, winnerHealth: 1, targetWinRate: 0.6);
        Assert.Equal(DuelLadder.MinLevel, low.Value);

        var auto = DuelLadder.ForAuto().For("p", null, 10, 0);
        Assert.Equal(DuelLadder.MaxLevel, auto.Value);

        var floor = DuelLadder.ForAuto().For("p", null, 0, 0);
        Assert.Equal(BotLevel.LowestTier, floor.Value);
        DuelLadder.Record(floor, won: false, winnerHealth: 1, targetWinRate: 0.6);
        Assert.Equal(BotLevel.LowestTier, floor.Value);
    }

    [Fact]
    public void EveryNameFitsTheWire()
    {
        Assert.Equal("Lv 5 (Normal)", DuelLadder.NameOf(5));
        Assert.Equal("Lv 0 (Dummy)", DuelLadder.NameOf(0.4));
        Assert.Equal("Lv 1 (Novice)", DuelLadder.NameOf(1.4));
        Assert.Equal("Lv 3 (Easy)", DuelLadder.NameOf(3));
        Assert.Equal("Lv 9 (Expert)", DuelLadder.NameOf(9));
        Assert.Equal("Lv 10 (Haxton)", DuelLadder.NameOf(9.5));

        for (var level = DuelLadder.MinLevel; level <= DuelLadder.MaxLevel; level++)
        {
            var name = DuelLadder.NameOf(level);
            Assert.True(
                Encoding.UTF8.GetByteCount(name) <= GameConstants.MaxPlayerNameLength,
                $"{name} is longer than a name may be");
        }
    }

    /// <summary>A player who wins every duel cleanly climbs one level a duel, to the top.</summary>
    [Fact]
    public void CleanWinsClimbALevelEach()
    {
        var ladder = new DuelLadder();
        var rating = ladder.For("p", PlayerClass.Soldier, 5, 0);

        for (var duel = 1; duel <= 5; duel++)
        {
            DuelLadder.Record(rating, won: true, winnerHealth: 1, targetWinRate: 0.6);
            Assert.Equal(5 + duel, rating.Value, 9);
        }

        DuelLadder.Record(rating, won: true, winnerHealth: 1, targetWinRate: 0.6);
        Assert.Equal(DuelLadder.MaxLevel, rating.Value);
    }

    /// <summary>
    /// A loss weighs p / (1 - p) of a win, the step halves at a change of
    /// direction to no less than a quarter, and three losses in a row, or six
    /// wins at 60%, restore half a level.
    /// </summary>
    [Fact]
    public void TheStepHalvesAtEachReversalAndARunRestoresIt()
    {
        var rating = new DuelLadder().For("p", PlayerClass.Soldier, 5, 0);

        DuelLadder.Record(rating, won: true, winnerHealth: 0, targetWinRate: 0.6);
        Assert.Equal(5.5, rating.Value, 9);

        DuelLadder.Record(rating, won: false, winnerHealth: 1, targetWinRate: 0.6);
        Assert.Equal(0.5, rating.Step);
        Assert.Equal(5.5 - 0.75, rating.Value, 9);

        DuelLadder.Record(rating, won: true, winnerHealth: 1, targetWinRate: 0.6);
        DuelLadder.Record(rating, won: false, winnerHealth: 1, targetWinRate: 0.6);
        DuelLadder.Record(rating, won: true, winnerHealth: 1, targetWinRate: 0.6);
        Assert.Equal(DuelLadder.MinStep, rating.Step);

        Assert.Equal(6, DuelLadder.WinRunLength(0.6));
        for (var run = 2; run <= 5; run++)
        {
            DuelLadder.Record(rating, won: true, winnerHealth: 1, targetWinRate: 0.6);
            Assert.Equal(DuelLadder.MinStep, rating.Step);
        }

        DuelLadder.Record(rating, won: true, winnerHealth: 1, targetWinRate: 0.6);
        Assert.Equal(DuelLadder.RunStep, rating.Step);

        DuelLadder.Record(rating, won: false, winnerHealth: 1, targetWinRate: 0.6);
        DuelLadder.Record(rating, won: false, winnerHealth: 1, targetWinRate: 0.6);
        Assert.Equal(DuelLadder.MinStep, rating.Step);
        DuelLadder.Record(rating, won: false, winnerHealth: 1, targetWinRate: 0.6);
        Assert.Equal(DuelLadder.RunStep, rating.Step);
    }

    /// <summary>
    /// Against a simulated player whose chance of winning falls with the bot's
    /// level, the ladder settles them at the target win rate, from either side of
    /// where that is. The player model is a logistic curve one level wide; what
    /// it stands in for, a person, has not been measured.
    /// </summary>
    [Theory]
    [InlineData(0.6, 5.3, 3)]
    [InlineData(0.6, 2.5, 3)]
    [InlineData(0.6, 7.0, 3)]
    [InlineData(0.5, 4.5, 3)]
    [InlineData(0.7, 4.5, 8)]
    public void ItSettlesAPlayerAtTheTargetWinRate(double target, double playerLevel, int start)
    {
        var random = new Random(1);
        var rating = new DuelLadder().For("p", PlayerClass.Soldier, start, 0);
        var wins = 0;
        const int Warmup = 50, Counted = 4000;

        for (var duel = 0; duel < Warmup + Counted; duel++)
        {
            var chance = 1 / (1 + Math.Exp(rating.Value - playerLevel));
            var won = random.NextDouble() < chance;
            DuelLadder.Record(rating, won, winnerHealth: random.NextDouble(), target);

            if (duel >= Warmup && won)
                wins++;
        }

        // Where the player model is clamped by the ladder's ends the rate cannot
        // be reached; every case here keeps the settling point inside 1-8. The
        // restored step still leans a point or two above the target.
        Assert.InRange(wins / (double)Counted, target - 0.02, target + 0.03);
    }

    [Fact]
    public void ARatingIsForgottenAnHourAfterItsLastDuel()
    {
        var ladder = new DuelLadder();
        ladder.For("p", PlayerClass.Soldier, 3, 0).Value = 6;
        ladder.For("p", PlayerClass.Scout, 3, 100);

        ladder.Forget(DuelLadder.ForgetTicks);
        Assert.Equal(2, ladder.Count);
        Assert.Equal(6, ladder.For("p", PlayerClass.Soldier, 3, DuelLadder.ForgetTicks).Value);

        ladder.Forget((2 * DuelLadder.ForgetTicks) + 1);
        Assert.Equal(0, ladder.Count);
        Assert.Equal(3, ladder.For("p", PlayerClass.Soldier, 3, 0).Value);
    }

    private static ServerHarness Duel(bool ladder = true, int? startLevel = null) =>
        new(o =>
        {
            if (startLevel is { } start)
                o.DuelLadderStartLevel = start;

            o.Map = "duel_01";
            o.MapDirectory = Path.Combine(ReferenceTree.MapsDirectory, "duel");
            o.NavCacheDirectory = Path.Combine(ReferenceTree.RepositoryRoot, "nav", "duel");
            o.Duel = true;
            o.DuelLadder = ladder;
            o.RespawnTimeSeconds = 5;
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

    private static LoopbackClient JoinAndPlay(ServerHarness harness, PlayerClass playerClass)
    {
        var client = harness.JoinClient("duellist");
        client.SendChangeTeam(Team.Red);
        client.SendChangeClass(playerClass);
        WaitForBoth(harness);
        DuelServerTests.Engage(harness, client);
        return client;
    }

    private static void WaitForBoth(ServerHarness harness) =>
        harness.TickUntil(
            () => HumanSlot(harness.Server) is var h and > 0 && harness.Server.IsAlive(h)
                && harness.Server.BotSlots.Count == 1 && harness.Server.IsAlive(BotSlot(harness.Server)),
            "the human and its bot to be alive",
            maxTicks: 400);

    private static void WaitOutProtection(ServerHarness harness)
    {
        var human = HumanSlot(harness.Server);
        harness.TickUntil(
            () => Gml.Equal(harness.Server.WorldForTests.Characters[human].DeathmatchInvulnerable, 0),
            "spawn protection to end",
            maxTicks: 300);
    }

    [Fact]
    public void ANewDuellistsBotStartsAtLevelFiveNamedForIt()
    {
        using var harness = Duel();
        JoinAndPlay(harness, PlayerClass.Soldier);

        Assert.Equal("Lv 5 (Normal)", harness.Server.NameOf(BotSlot(harness.Server)));
        Assert.Equal(BotLevel.Skill(5), SkillOfBot(harness.Server));
    }

    [Fact]
    public void WithTheLadderOffTheBotKeepsItsClassName()
    {
        using var harness = Duel(ladder: false);
        JoinAndPlay(harness, PlayerClass.Soldier);

        Assert.Equal("[BOT] Soldier", harness.Server.NameOf(BotSlot(harness.Server)));
        Assert.Equal(BotSkill.ForTier(harness.Options.Bots.Difficulty), SkillOfBot(harness.Server));
    }

    /// <summary>A clean win is a whole level up, and the beaten bot is renamed at once.</summary>
    [Fact]
    public void AWinRaisesTheLevelAndRenamesTheBeatenBot()
    {
        using var harness = Duel();
        JoinAndPlay(harness, PlayerClass.Soldier);
        var server = harness.Server;
        var bot = BotSlot(server);

        server.WorldForTests.Characters[bot].Hp = 0;
        harness.TickUntil(() => server.NameOf(bot) == "Lv 6 (Normal)", "the bot to be renamed", maxTicks: 3);

        Assert.Equal(BotLevel.Skill(6), SkillOfBot(server));
    }

    /// <summary>
    /// A loss to a bot on full health is one and a half levels down at 60%; the
    /// bot keeps its name through the kill cam, which shows it, and takes the new
    /// one when it is killed.
    /// </summary>
    [Fact]
    public void ALossLowersTheLevelAndTheBotIsRenamedAfterTheKillCam()
    {
        using var harness = Duel();
        JoinAndPlay(harness, PlayerClass.Soldier);
        WaitOutProtection(harness);
        var server = harness.Server;
        var human = HumanSlot(server);
        var bot = BotSlot(server);

        server.WorldForTests.Characters[human].Hp = 0;
        // 1.5 to within rounding: 0.6 / 0.4 is not exactly 1.5 in a double.
        var expected = BotLevel.Skill(3.5).PolicyAimErrorDeg;
        harness.TickUntil(
            () => Math.Abs(SkillOfBot(server).PolicyAimErrorDeg - expected) < 1e-9,
            "the loss to be counted",
            maxTicks: 3);

        Assert.True(server.DeathsOf(human) > 0);
        Assert.Equal("Lv 5 (Normal)", server.NameOf(bot));

        harness.TickUntil(() => !server.IsAlive(bot), "the bot to be killed", maxTicks: BotPopulationService.KillCamTicks + 2);
        Assert.Equal("Lv 4 (Easy)", server.NameOf(bot));
    }

    /// <summary>
    /// A class change costs the human a life and is not a lost duel; the new
    /// class has its own rating, and the old one is still there on the way back.
    /// </summary>
    [Fact]
    public void AClassChangeIsNotALossAndEachClassHasItsOwnLevel()
    {
        using var harness = Duel();
        var client = JoinAndPlay(harness, PlayerClass.Soldier);
        var server = harness.Server;
        var bot = BotSlot(server);

        server.WorldForTests.Characters[bot].Hp = 0;
        harness.TickUntil(() => server.NameOf(bot) == "Lv 6 (Normal)", "the win", maxTicks: 3);
        WaitForBoth(harness);
        WaitOutProtection(harness);

        client.SendChangeClass(PlayerClass.Heavy);
        harness.TickUntil(() => server.ClassOf(BotSlot(server)) == PlayerClass.Heavy, "the bot to follow", maxTicks: 200);
        WaitForBoth(harness);
        Assert.Equal("Lv 5 (Normal)", server.NameOf(BotSlot(server)));
        Assert.Equal(BotLevel.Skill(5), SkillOfBot(server));

        WaitOutProtection(harness);
        client.SendChangeClass(PlayerClass.Soldier);
        harness.TickUntil(() => server.ClassOf(BotSlot(server)) == PlayerClass.Soldier, "the bot to follow", maxTicks: 200);
        WaitForBoth(harness);
        Assert.Equal("Lv 6 (Normal)", server.NameOf(BotSlot(server)));
        Assert.Equal(BotLevel.Skill(6), SkillOfBot(server));
    }

    /// <summary>
    /// A level-0 bot beside its human, past spawn protection, deals no damage; the
    /// same duel at level 10 does, so the first half can fail.
    /// </summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(10, true)]
    public void ALevelZeroBotDealsNoDamage(int level, bool hurts)
    {
        using var harness = Duel(startLevel: level);
        JoinAndPlay(harness, PlayerClass.Soldier);
        WaitOutProtection(harness);
        var server = harness.Server;
        var human = HumanSlot(server);
        var full = server.WorldForTests.Characters[human].Hp;

        var hurt = false;
        for (var tick = 0; tick < 600 && !hurt; tick++)
        {
            harness.Tick();
            hurt = !server.IsAlive(human) || server.WorldForTests.Characters[human].Hp < full;
        }

        Assert.Equal(hurts, hurt);
    }
}
