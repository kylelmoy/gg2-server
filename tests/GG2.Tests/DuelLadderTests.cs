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
    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 2)]
    [InlineData(5, 3)]
    [InlineData(7, 4)]
    [InlineData(8, 5)]
    public void TheOddLevelsAndTheTopArePlayedAtTheServersTiers(int level, int tier) =>
        Assert.Equal(BotSkill.ForTier(tier), DuelLadder.SkillOf(level));

    /// <summary>
    /// Each knob the policy is handicapped by gets no worse from one level to the
    /// next, and the interpolated levels are strictly between their neighbours'.
    /// </summary>
    [Fact]
    public void EachLevelIsAtLeastAsStrongAsTheOneBelow()
    {
        for (var level = DuelLadder.MinLevel + 1; level <= DuelLadder.MaxLevel; level++)
        {
            var below = DuelLadder.SkillOf(level - 1);
            var here = DuelLadder.SkillOf(level);

            Assert.True(here.PolicyAimErrorDeg <= below.PolicyAimErrorDeg, $"aim error rose at level {level}");
            Assert.True(here.FireDelayTicks <= below.FireDelayTicks, $"fire delay rose at level {level}");
            Assert.True(here.HoldFireChance <= below.HoldFireChance, $"hold-fire rose at level {level}");
        }

        foreach (var level in new[] { 2, 4, 6 })
        {
            Assert.True(DuelLadder.SkillOf(level).PolicyAimErrorDeg < DuelLadder.SkillOf(level - 1).PolicyAimErrorDeg);
            Assert.True(DuelLadder.SkillOf(level).PolicyAimErrorDeg > DuelLadder.SkillOf(level + 1).PolicyAimErrorDeg);
        }
    }

    /// <summary>
    /// Between duel levels 7 and 8 the bot fades from playing like a person to
    /// not, with each part of that moving the right way and the ends exact.
    /// </summary>
    [Fact]
    public void TheTopDuelLevelIsASlope()
    {
        Assert.Equal(BotSkill.ForTier(4), DuelLadder.SkillOf(7));
        Assert.Equal(BotSkill.ForTier(5), DuelLadder.SkillOf(8));

        var previous = DuelLadder.SkillOf(7);
        for (var r = 7.25; r < 8; r += 0.25)
        {
            var here = DuelLadder.SkillOf(r);
            Assert.True(here.HumanAim, $"rating {r} stopped aiming like a person before 8");
            Assert.True(here.HumanAimStrength < previous.HumanAimStrength, $"human aim did not fall at {r}");
            Assert.True(here.PolicyReflex < previous.PolicyReflex, $"the rocket lag did not fall at {r}");
            Assert.True(here.ReflexTicks <= previous.ReflexTicks, $"the reflex rose at {r}");
            Assert.True(here.ReversalCooldownTicks <= previous.ReversalCooldownTicks, $"the turn-around rose at {r}");
            previous = here;
        }

        Assert.Equal(1.5, DuelLadder.SkillOf(7.5).PolicyReflex, 9);
    }

    /// <summary>
    /// The duel server's levels: the top is exactly tier 5, the bottom plays fully
    /// like a person, and no knob gets weaker going up, a quarter level at a time.
    /// </summary>
    [Fact]
    public void TheDuelLevelsRiseEvenlyToTierFive()
    {
        Assert.Equal(BotSkill.ForTier(5), DuelLadder.DuelSkillOf(8));
        Assert.Equal(BotSkill.For(0.15).Human(true), DuelLadder.DuelSkillOf(1));

        var previous = DuelLadder.DuelSkillOf(1);
        for (var r = 1.25; r <= 8; r += 0.25)
        {
            var here = DuelLadder.DuelSkillOf(r);
            var human = here.HumanAim ? here.HumanAimStrength : 0;
            var humanBefore = previous.HumanAim ? previous.HumanAimStrength : 0;

            Assert.True(human < humanBefore || (human == 0 && humanBefore == 0), $"human aim did not fall at {r}");
            Assert.True(here.PolicyAimErrorDeg <= previous.PolicyAimErrorDeg, $"aim error rose at {r}");
            Assert.True(here.FireDelayTicks <= previous.FireDelayTicks, $"fire delay rose at {r}");
            Assert.True(here.PolicyReflex <= previous.PolicyReflex, $"the rocket reaction slowed at {r}");
            Assert.True(here.ReflexMissChance <= previous.ReflexMissChance, $"the rocket miss rose at {r}");
            previous = here;
        }
    }

    [Fact]
    public void EveryNameFitsTheWire()
    {
        Assert.Equal("Soldier 3 (Normal)", DuelLadder.NameOf(PlayerClass.Soldier, 3));
        Assert.Equal("Scout 1 (Easy)", DuelLadder.NameOf(PlayerClass.Scout, 1.4));
        Assert.Equal("Heavy 8 (Max)", DuelLadder.NameOf(PlayerClass.Heavy, 7.5));

        foreach (var playerClass in Enum.GetValues<PlayerClass>())
        {
            for (var level = DuelLadder.MinLevel; level <= DuelLadder.MaxLevel; level++)
            {
                var name = DuelLadder.NameOf(playerClass, level);
                Assert.True(
                    Encoding.UTF8.GetByteCount(name) <= GameConstants.MaxPlayerNameLength,
                    $"{name} is longer than a name may be");
            }
        }
    }

    /// <summary>A player who wins every duel cleanly climbs one level a duel, to the top.</summary>
    [Fact]
    public void CleanWinsClimbALevelEach()
    {
        var ladder = new DuelLadder();
        var rating = ladder.For("p", PlayerClass.Soldier, 3, 0);

        for (var duel = 1; duel <= 5; duel++)
        {
            DuelLadder.Record(rating, won: true, winnerHealth: 1, targetWinRate: 0.6);
            Assert.Equal(3 + duel, rating.Value, 9);
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

    private static ServerHarness Duel(bool ladder = true) =>
        new(o =>
        {
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
    public void ANewDuellistsBotStartsAtLevelThreeNamedForIt()
    {
        using var harness = Duel();
        JoinAndPlay(harness, PlayerClass.Soldier);

        Assert.Equal("Soldier 3 (Normal)", harness.Server.NameOf(BotSlot(harness.Server)));
        Assert.Equal(DuelLadder.DuelSkillOf(3), SkillOfBot(harness.Server));
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
        harness.TickUntil(() => server.NameOf(bot) == "Soldier 4 (Normal)", "the bot to be renamed", maxTicks: 3);

        Assert.Equal(DuelLadder.DuelSkillOf(4), SkillOfBot(server));
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
        var expected = DuelLadder.DuelSkillOf(1.5).PolicyAimErrorDeg;
        harness.TickUntil(
            () => Math.Abs(SkillOfBot(server).PolicyAimErrorDeg - expected) < 1e-9,
            "the loss to be counted",
            maxTicks: 3);

        Assert.True(server.DeathsOf(human) > 0);
        Assert.Equal("Soldier 3 (Normal)", server.NameOf(bot));

        harness.TickUntil(() => !server.IsAlive(bot), "the bot to be killed", maxTicks: BotPopulationService.KillCamTicks + 2);
        Assert.Equal("Soldier 2 (Easy)", server.NameOf(bot));
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
        harness.TickUntil(() => server.NameOf(bot) == "Soldier 4 (Normal)", "the win", maxTicks: 3);
        WaitForBoth(harness);
        WaitOutProtection(harness);

        client.SendChangeClass(PlayerClass.Heavy);
        harness.TickUntil(() => server.ClassOf(BotSlot(server)) == PlayerClass.Heavy, "the bot to follow", maxTicks: 200);
        WaitForBoth(harness);
        Assert.Equal("Heavy 3 (Normal)", server.NameOf(BotSlot(server)));
        Assert.Equal(DuelLadder.DuelSkillOf(3), SkillOfBot(server));

        WaitOutProtection(harness);
        client.SendChangeClass(PlayerClass.Soldier);
        harness.TickUntil(() => server.ClassOf(BotSlot(server)) == PlayerClass.Soldier, "the bot to follow", maxTicks: 200);
        WaitForBoth(harness);
        Assert.Equal("Soldier 4 (Normal)", server.NameOf(BotSlot(server)));
        Assert.Equal(DuelLadder.DuelSkillOf(4), SkillOfBot(server));
    }
}
