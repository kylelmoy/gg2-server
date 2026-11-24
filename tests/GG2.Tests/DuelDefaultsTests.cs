using GG2.Bots;
using GG2.Host;
using GG2.Protocol;
using GG2.Server;

namespace GG2.Tests;

/// <summary>
/// <c>--duel</c> alone runs the ten duel nets under the settings they were
/// trained and scored with, and anything set explicitly still wins.
/// </summary>
/// <remarks>
/// <b>Both directions are gated</b>, because a default that also overrode an
/// explicit setting would pass the first half and a default that never applied
/// would pass the second. <c>docs/DUEL.md</c>, "The duel nets".
/// </remarks>
public sealed class DuelDefaultsTests
{
    private static SimPolicyMicroSpec[] Tiers(ServerOptions options) =>
        Assert.IsType<BotBrainSpec>(options.Bots.Brain).Tiers.Select(Assert.IsType<SimPolicyMicroSpec>).ToArray();

    [Fact]
    public void DuelAloneRunsTheDuelNetsUnderTheirSettings()
    {
        var options = CommandLine.Parse(["--duel"]);
        var tiers = Tiers(options);

        Assert.Equal(
            MapDirectory.ShippedDuelCheckpoints.Select(Path.GetFileNameWithoutExtension),
            tiers.Select(tier => tier.Label));

        // Ten classes, Quote included: a duel bot takes its human's class.
        Assert.Equal(10, tiers.SelectMany(tier => tier.Drives).Distinct().Count());
        Assert.Contains(tiers, tier => tier.Drives.Contains(PlayerClass.Quote));

        Assert.All(tiers, tier => Assert.True(tier.Sampled));
        Assert.Equal(HandoverRule.Distance, options.Bots.Brain!.Rule);
        Assert.Equal(DuelDefaults.HandoverRange, options.Bots.Brain.Range);
        Assert.Equal(0, DuelDefaults.TdmInvulnerabilitySeconds(options));
    }

    [Fact]
    public void ExplicitFlagsBeatTheDuelDefaults()
    {
        var options = CommandLine.Parse(
        [
            "--duel", "--bot-handover-rule", "macrotask", "--bot-handover-range", "200", "--tdm-invuln", "3",
        ]);

        Assert.Equal(HandoverRule.MacroTask, options.Bots.Brain!.Rule);
        Assert.Equal(200, options.Bots.Brain.Range);
        Assert.Equal(3, DuelDefaults.TdmInvulnerabilitySeconds(options));
    }

    /// <summary>
    /// A file key counts as explicit too, including one that names the global
    /// default — the case a comparison against the default could not see.
    /// </summary>
    [Fact]
    public void ExplicitFileKeysBeatTheDuelDefaults()
    {
        var config = CliCoverageTests.WriteConfig(
            """
            {
              "Server": {"Team Deathmatch Invulnerability Seconds": 5},
              "Bots": {"Duel": true, "PolicySampled": false, "HandoverRule": "macrotask"}
            }
            """);

        var options = CommandLine.Parse(["--config", config]);

        Assert.All(Tiers(options), tier => Assert.False(tier.Sampled));
        Assert.Equal(HandoverRule.MacroTask, options.Bots.Brain!.Rule);
        Assert.Equal(5, DuelDefaults.TdmInvulnerabilitySeconds(options));
    }

    [Fact]
    public void ANamedCheckpointReplacesTheDuelBundle()
    {
        var tier = Assert.Single(Tiers(CommandLine.Parse(["--duel", "--policy", PolicyFixture.Checkpoint])));

        Assert.Equal(PolicyFixture.Label, tier.Label);
    }

    [Fact]
    public void AServerThatIsNotADuelServerIsUnchanged()
    {
        var options = CommandLine.Parse([]);

        Assert.Equal(HandoverRule.MacroTask, options.Bots.Brain!.Rule);
        Assert.Equal(HybridBot.EngagementRange, options.Bots.Brain.Range);
        Assert.All(Tiers(options), tier => Assert.False(tier.Sampled));
        Assert.Equal(options.TdmInvulnerabilitySeconds, DuelDefaults.TdmInvulnerabilitySeconds(options));
    }

    /// <summary>
    /// <c>--duel</c> alone serves the shipped duel maps and nav graphs, opening
    /// on <c>duel_01</c>.
    /// </summary>
    [Fact]
    public void DuelAloneServesTheShippedDuelMaps()
    {
        var options = CommandLine.Parse(["--duel"]);
        DuelDefaults.ApplyPaths(options);

        Assert.Equal(Path.Combine(MapDirectory.Resolve(), "duel"), options.MapDirectory);
        Assert.Equal(Path.Combine(MapDirectory.ResolveNav(), "duel"), options.NavCacheDirectory);
        Assert.Equal(DuelDefaults.Map, options.Map);
        Assert.True(File.Exists(Path.Combine(options.MapDirectory, options.Map + ".png")));
    }

    /// <summary>A map, a rotation or a directory given explicitly is kept.</summary>
    [Theory]
    [InlineData("--map", "duel_02")]
    [InlineData("--rotation", "duel_03,duel_04")]
    public void AnExplicitMapChoiceIsKept(string flag, string value)
    {
        var options = CommandLine.Parse(["--duel", flag, value]);
        DuelDefaults.ApplyPaths(options);

        Assert.NotEqual(DuelDefaults.Map, options.Map);
        Assert.Equal(Path.Combine(MapDirectory.Resolve(), "duel"), options.MapDirectory);
    }

    [Fact]
    public void ExplicitDirectoriesAreKept()
    {
        var maps = MapDirectory.Resolve();
        var nav = MapDirectory.ResolveNav();
        var options = CommandLine.Parse(["--duel", "--maps", maps, "--botnav", nav, "--map", "ctf_truefort"]);
        DuelDefaults.ApplyPaths(options);

        Assert.Equal(maps, options.MapDirectory);
        Assert.Equal(nav, options.NavCacheDirectory);
        Assert.Equal("ctf_truefort", options.Map);
    }

    [Fact]
    public void AServerThatIsNotADuelServerKeepsItsMaps()
    {
        var options = CommandLine.Parse([]);
        DuelDefaults.ApplyPaths(options);

        Assert.Equal(MapDirectory.Resolve(), options.MapDirectory);
        Assert.Equal(ServerOptions.DefaultMap, options.Map);
    }

    /// <summary>The world a duel server builds carries no spawn protection.</summary>
    [Fact]
    public void TheDuelWorldHasNoSpawnProtection()
    {
        using var duel = new ServerHarness(o => o.Duel = true);
        using var plain = new ServerHarness();

        Assert.Equal(0, duel.Server.WorldForTests.TdmInvulnerabilityTicks);
        Assert.NotEqual(0, plain.Server.WorldForTests.TdmInvulnerabilityTicks);
    }
}
