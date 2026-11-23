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
            "[Server]\nTeam Deathmatch Invulnerability Seconds=5\n"
            + "[Bots]\nDuel=1\nPolicySampled=0\nHandoverRule=macrotask\n");

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
