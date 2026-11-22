using GG2.Bots;
using GG2.Protocol;
using GG2.Server;
using GG2.Simulation;
using Xunit;

namespace GG2.Tests;

/// <summary>
/// <c>[Bots] AutoDifficulty</c>: each human's rating follows their kills of and
/// deaths to bots, and every bot plays the rating of the human it is shooting
/// at. <c>docs/BOTS.md</c>, "Auto difficulty".
/// </summary>
public sealed class AutoDifficultyTests
{
    // MaxBots is the count: the fill target counts humans as well, so it is set
    // high enough never to be the one that binds.
    private static ServerHarness Auto(bool on = true, int bots = 2, string map = "ctf_truefort", int? seed = null) =>
        new(o =>
        {
            o.Map = map;
            o.RandomSeed = seed;
            o.NavCacheDirectory = ReferenceTree.NavCacheDirectory;
            o.AutoDifficulty = on;
            o.Bots = BotPopulationOptions.Default with
            {
                Enabled = true,
                FillToPlayers = 24,
                MaxBots = bots,
                MinHumans = 0,
                Difficulty = 3,
            };
        });

    private static int Join(ServerHarness harness, string name, Team team)
    {
        var client = harness.JoinClient(name);
        client.SendChangeTeam(team);
        client.SendChangeClass(PlayerClass.Soldier);

        var slot = -1;
        harness.TickUntil(
            () =>
            {
                for (var s = 1; s < harness.Server.RosterCount; s++)
                {
                    if (harness.Server.NameOf(s) == name && harness.Server.TeamOf(s) == team)
                    {
                        slot = s;
                        return true;
                    }
                }

                return false;
            },
            $"{name} to join {team}",
            maxTicks: 300);

        return slot;
    }

    private static BotPopulationService Population(ServerHarness harness) =>
        Assert.IsType<BotPopulationService>(harness.Server.Population);

    private static DuelLadder.Rating RatingOf(ServerHarness harness, int slot) =>
        Population(harness).AutoRatings.For(harness.Server.EntryOf(slot).RatingKey!, null, 1, 0);

    private static void Kill(ServerHarness harness, int victim, int killer)
    {
        var events = new TickEvents();
        events.Died[victim] = true;
        events.Deaths[victim] = new PlayerDeathEvent(victim, (byte)killer, CharacterState.Nobody, DamageSource.None);
        Population(harness).ObserveDeaths(in events, harness.Server.RosterCount);
    }

    /// <summary>
    /// A new human starts on the server's Difficulty, and a bot plays exactly the
    /// tier against them: turning auto difficulty on changes nothing until a
    /// result comes in.
    /// </summary>
    [Fact]
    public void ANewHumanIsFoughtAtTheServersDifficulty()
    {
        using var harness = Auto();
        var human = Join(harness, "human", Team.Red);
        harness.Tick();

        Assert.Equal(5, RatingOf(harness, human).Value);
        Assert.True(Population(harness).Opponents.TryGet(human, out var skill));
        Assert.Equal(BotSkill.ForTier(3), skill);
    }

    /// <summary>
    /// A bot dying to a human is a win, a human dying to a bot a loss, each
    /// with the duel ladder's step and margin; everything else counts for nobody.
    /// </summary>
    [Fact]
    public void OnlyKillsBetweenAHumanAndABotMoveTheRating()
    {
        using var harness = Auto();
        var red = Join(harness, "red", Team.Red);
        var blue = Join(harness, "blue", Team.Blue);
        harness.TickUntil(() => harness.Server.BotSlots.Count == 2, "two bots", maxTicks: 60);
        var bot = harness.Server.BotSlots[0];
        harness.Tick();

        var rating = RatingOf(harness, red);
        Assert.Equal(5, rating.Value);

        // Human against human, a suicide and a death with no killer: nothing.
        Kill(harness, blue, red);
        Kill(harness, red, red);
        Kill(harness, red, CharacterState.Nobody);
        Assert.Equal(5, rating.Value);
        Assert.Equal(0, rating.Duels);

        // A win at full health: a whole step, the first one.
        var margin = 0.5 + (0.5 * harness.Server.HealthFractionOf(red));
        Kill(harness, bot, red);
        Assert.Equal(5 + margin, rating.Value, 9);

        // A loss: the step halves on the reversal, and a loss at 60% moves 1.5
        // times a win.
        var before = rating.Value;
        var botMargin = 0.5 + (0.5 * harness.Server.HealthFractionOf(bot));
        Kill(harness, red, bot);
        Assert.Equal(before - (0.5 * botMargin * 1.5), rating.Value, 9);
        Assert.Equal(2, rating.Duels);

        // The blue human was never in one.
        Assert.Equal(0, RatingOf(harness, blue).Duels);
    }

    /// <summary>
    /// Two humans rated apart get their own rows, and one bot plays each of
    /// them at theirs, switching as its target does.
    /// </summary>
    [Fact]
    public void ABotPlaysTheRatingOfWhoeverItIsShootingAt()
    {
        using var harness = Auto();
        var weak = Join(harness, "weak", Team.Red);
        var strong = Join(harness, "strong", Team.Blue);
        harness.TickUntil(() => harness.Server.BotSlots.Count == 2, "two bots", maxTicks: 60);
        harness.Tick();

        RatingOf(harness, weak).Value = 1;
        RatingOf(harness, strong).Value = 8;
        harness.Tick();

        var bot = Assert.IsType<ScriptedBot>(ScriptedBot.Of(harness.Server.SourceOf(harness.Server.BotSlots[0])));

        bot.Combat.Target = new BotTarget(BotTargetKind.Character, weak, 0, 0, true);
        Assert.Equal(DuelLadder.SkillOf(1), bot.Skill);

        bot.Combat.Target = new BotTarget(BotTargetKind.Sentry, strong, 0, 0, true);
        Assert.Equal(DuelLadder.SkillOf(8), bot.Skill);

        // Another bot, the generator and nothing: the bot's own tier.
        bot.Combat.Target = new BotTarget(BotTargetKind.Character, harness.Server.BotSlots[1], 0, 0, true);
        Assert.Equal(BotSkill.ForTier(3), bot.Skill);
        bot.Combat.Target = new BotTarget(BotTargetKind.Generator, 0, 0, 0, true);
        Assert.Equal(BotSkill.ForTier(3), bot.Skill);
        bot.Combat.Target = BotTarget.None;
        Assert.Equal(BotSkill.ForTier(3), bot.Skill);

        Assert.Equal(1, Population(harness).AutoRatingOf(weak));
        Assert.Equal(0, Population(harness).AutoRatingOf(harness.Server.BotSlots[0]));
    }

    /// <summary>Off, nobody has a row, so every bot plays its own tier, and no result is kept.</summary>
    [Fact]
    public void OffEveryBotPlaysTheTier()
    {
        using var harness = Auto(on: false);
        var human = Join(harness, "human", Team.Red);
        harness.TickUntil(() => harness.Server.BotSlots.Count == 2, "two bots", maxTicks: 60);
        harness.Tick();

        Assert.False(Population(harness).Opponents.TryGet(human, out _));
        Kill(harness, harness.Server.BotSlots[0], human);
        Assert.Equal(0, Population(harness).AutoRatings.Count);
        Assert.Equal(0, Population(harness).AutoRatingOf(human));
    }

    /// <summary>
    /// The whole path on a live server: a human who stands still is killed by the
    /// bots, and the rating falls from the death the simulation reported, with
    /// no test-side event.
    /// </summary>
    [Fact]
    public void ALiveDeathToABotLowersTheRating()
    {
        // Team Deathmatch, so the bots come to the human rather than the intel.
        //
        // ⚠️ SEEDED. Unseeded, this failed about one run in eight, two ways, and
        // both were the test rather than the scoring: on some world seeds the
        // bots never kill a standing soldier inside 9000 ticks, and on others it
        // ends up in a pit undamaged, which the reference credits to nobody
        // (Character.events/Collision with PitFall.xml: PITFALL only when there
        // is no lastDamageDealer) and so is rightly not a duel. Measured over ~160
        // runs. With the seed fixed the run is identical every time; 1002 is a
        // bot's rocket kill at tick 323.
        using var harness = Auto(bots: 4, map: "tdm_mantic", seed: 1002);
        var human = Join(harness, "human", Team.Red);
        var server = harness.Server;
        harness.TickUntil(() => server.IsAlive(human), "the human to spawn", maxTicks: 300);

        harness.TickUntil(() => server.DeathsOf(human) > 0, "a bot to kill the standing human", maxTicks: 9000);

        var rating = RatingOf(harness, human);
        Assert.True(rating.Duels >= 1, "the death was not scored");
        Assert.True(rating.Value < 5, $"rating {rating.Value} did not fall");
    }
}
