using GG2.Bots;
using GG2.Host;
using GG2.Protocol;
using GG2.Server;
using GG2.Simulation;

namespace GG2.Tests;

/// <summary>
/// <c>[BotClasslimits]</c>: how many bots of each class one team may hold.
/// </summary>
/// <remarks>
/// <b>The default is the thing to protect.</b> A server that configures nothing
/// must draw exactly what the unrestricted picker draws, stream position included,
/// or every live server's roster moves for a setting nobody set.
/// </remarks>
public sealed class BotClassLimitTests
{
    private static ReadOnlySpan<int> NoBots => new int[TeamBalance.ClassCount];

    /// <summary>
    /// With no limit binding, the limited picker is the unrestricted one draw for draw.
    /// </summary>
    [Fact]
    public void TheDefaultLimitsDrawExactlyWhatTheUnrestrictedPickerDraws()
    {
        var plain = new GmlRandom(4242);
        var limited = new GmlRandom(4242);

        for (var i = 0; i < 2000; i++)
        {
            Assert.Equal(
                BotPopulation.PickClass(ref plain),
                BotPopulation.PickClass(ref limited, BotPopulation.DefaultClassLimits, NoBots));
        }

        // Same position afterwards, not merely the same answers.
        Assert.Equal(plain.NextInclusive(1000), limited.NextInclusive(1000));
    }

    /// <summary>A class at its limit is never drawn, and a full team draws nothing.</summary>
    [Fact]
    public void AClassAtItsLimitIsNeverDrawnAndAFullTeamDrawsNothing()
    {
        var random = new GmlRandom(9);
        Span<byte> limits = stackalloc byte[TeamBalance.ClassCount];
        Span<int> bots = stackalloc int[TeamBalance.ClassCount];

        limits[(int)PlayerClass.Medic] = 2;
        limits[(int)PlayerClass.Spy] = 1;
        bots[(int)PlayerClass.Medic] = 1;

        var seen = new HashSet<PlayerClass?>();

        for (var i = 0; i < 500; i++)
            seen.Add(BotPopulation.PickClass(ref random, limits, bots));

        Assert.Equal([PlayerClass.Medic, PlayerClass.Spy], seen.Order());

        // The control: once both are at their limits there is nothing to draw, and
        // the answer is null rather than a spin.
        bots[(int)PlayerClass.Medic] = 2;
        bots[(int)PlayerClass.Spy] = 1;

        Assert.Null(BotPopulation.PickClass(ref random, limits, bots));
    }

    /// <summary>
    /// A live server holds each team to its limits, counting bots per team.
    /// </summary>
    /// <remarks>
    /// Six bots against one soldier and one heavy a team: four fit, and the fill
    /// stops there rather than exceeding a limit. Read from the server's roster,
    /// not from the population service.
    /// </remarks>
    [Fact]
    public void ALiveServerHoldsEachTeamToItsLimits()
    {
        var limits = new byte[TeamBalance.ClassCount];
        limits[(int)PlayerClass.Soldier] = 1;
        limits[(int)PlayerClass.Heavy] = 1;

        using var harness = new ServerHarness(o =>
        {
            o.Map = "ctf_truefort";
            o.NavCacheDirectory = ReferenceTree.NavCacheDirectory;
            o.Bots = BotPopulationOptions.Default with
            {
                Enabled = true, FillToPlayers = 6, MaxBots = 6, MinHumans = 0, ClassLimits = limits,
            };
        });

        harness.Tick(30);

        var server = harness.Server;
        var bots = server.BotSlots.Select(slot => (Team: server.TeamOf(slot), Class: server.ClassOf(slot))).ToArray();

        Assert.Equal(4, bots.Length);

        foreach (var team in new[] { Team.Red, Team.Blue })
        {
            Assert.Equal(
                [PlayerClass.Soldier, PlayerClass.Heavy],
                bots.Where(b => b.Team == team).Select(b => b.Class).Order());
        }
    }

    /// <summary>
    /// A bot that dies comes back as a random class within its limits, named for it.
    /// </summary>
    /// <remarks>
    /// Eight bots, four a team, over three classes allowed two each: every team
    /// always has room to move, so the draw is exercised rather than pinned. Read
    /// off the server's roster every tick, so a class that went over a limit for
    /// one tick is caught. ⚠️ The count of changes is what stops this passing on a
    /// server that never re-classes anybody.
    /// </remarks>
    [Fact]
    public void ADeadBotComesBackAsARandomClassWithinItsLimitsAndNamedForIt()
    {
        var limits = new byte[TeamBalance.ClassCount];
        limits[(int)PlayerClass.Scout] = 2;
        limits[(int)PlayerClass.Soldier] = 2;
        limits[(int)PlayerClass.Pyro] = 2;

        using var harness = new ServerHarness(o =>
        {
            o.Map = "gg_debug";
            o.NavCacheDirectory = ReferenceTree.NavCacheDirectory;
            o.Bots = BotPopulationOptions.Default with
            {
                Enabled = true, FillToPlayers = 8, MaxBots = 8, MinHumans = 0, ClassLimits = limits,
            };
        });

        var server = harness.Server;
        var last = new Dictionary<long, PlayerClass>();
        var alive = new Dictionary<long, bool>();
        var changes = 0;

        for (var tick = 0; tick < 12_000; tick++)
        {
            harness.Tick();

            foreach (var team in new[] { Team.Red, Team.Blue })
            {
                foreach (var playerClass in Enum.GetValues<PlayerClass>())
                {
                    var count = server.BotSlots.Count(s => server.TeamOf(s) == team && server.ClassOf(s) == playerClass);
                    Assert.True(count <= limits[(int)playerClass], $"{count} {playerClass} bots on {team} at tick {tick}");
                }
            }

            foreach (var slot in server.BotSlots)
            {
                var entry = server.EntryOf(slot);

                if (last.TryGetValue(entry.StableId, out var before) && before != entry.Class)
                {
                    changes++;

                    // Changed while dead, never by killing a live character.
                    Assert.False(alive[entry.StableId], $"{entry.Name} changed class while alive");
                }

                Assert.Equal(BotPopulationOptions.Default.NamePrefix + entry.Class, entry.Name);

                last[entry.StableId] = entry.Class;
                alive[entry.StableId] = server.IsAlive(slot);
            }
        }

        Assert.True(changes >= 6, $"only {changes} class changes in 12,000 ticks");
    }

    /// <summary>A pair on the command line applies over the defaults, so the spy stays barred.</summary>
    [Fact]
    public void APairAppliesOverTheDefaults()
    {
        var options = CommandLine.Parse(["--bot-class-limit", "medic=1"]);
        var limits = options.Bots.EffectiveClassLimits;

        Assert.Equal(1, limits[(int)PlayerClass.Medic]);
        Assert.Equal(0, limits[(int)PlayerClass.Spy]);
        Assert.Equal(255, limits[(int)PlayerClass.Scout]);

        var every = CommandLine.Parse(["--bot-class-limit", "3"]).Bots.EffectiveClassLimits;

        Assert.Equal(3, every[(int)PlayerClass.Spy]);
        Assert.Equal(0, every[(int)PlayerClass.Quote]);
    }

    /// <summary>Quote is refused: no bot is ever drawn as one.</summary>
    [Fact]
    public void QuoteIsRefused()
    {
        var thrown = Assert.Throws<ArgumentException>(
            () => CommandLine.Parse(["--bot-class-limit", "quote=1"]));

        Assert.Contains("not a class a bot can be", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>A config file reaches every class, and Quote has no key.</summary>
    [Fact]
    public void AConfigFileReachesTheLimits()
    {
        var config = CliCoverageTests.WriteConfig(
            """
            [BotClasslimits]
            Spy=2
            Medic=0
            """);

        var limits = CommandLine.Parse(["--config", config]).Bots.EffectiveClassLimits;

        Assert.Equal(2, limits[(int)PlayerClass.Spy]);
        Assert.Equal(0, limits[(int)PlayerClass.Medic]);
        Assert.Equal(255, limits[(int)PlayerClass.Sniper]);

        Assert.Equal(9, ServerSettings.All.Count(setting => setting.Section == "BotClasslimits"));
        Assert.DoesNotContain(ServerSettings.All, setting => setting.Id == "BotClasslimits.Quote");
    }

    /// <summary>Limits that cannot seat the fill are warned about; unlimited ones are not.</summary>
    [Fact]
    public void LimitsThatCannotSeatTheFillAreWarnedAbout()
    {
        var limits = new byte[TeamBalance.ClassCount];
        limits[(int)PlayerClass.Scout] = 2;

        var options = new ServerOptions
        {
            MapDirectory = "maps",
            Bots = BotPopulationOptions.Default with
            {
                Enabled = true, FillToPlayers = 8, MaxBots = 8, ClassLimits = limits,
            },
        };

        var warning = Assert.Single(ConfigWarnings.For(options));
        Assert.Contains("seat at most 2 bots a team, 4 in all", warning, StringComparison.Ordinal);

        options.Bots = options.Bots with { ClassLimits = null };
        Assert.Empty(ConfigWarnings.For(options));
    }
}
