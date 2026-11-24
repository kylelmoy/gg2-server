using GG2.Bots;
using GG2.Host;
using GG2.Protocol;
using GG2.Server;

namespace GG2.Tests;

/// <summary>
/// A server always runs the policy brain: the shipped bundle when nothing names
/// a checkpoint, the named checkpoints when something does, and the retired
/// <c>--bot-mode</c> flag and <c>[Bots] Mode</c> key change neither.
/// </summary>
/// <remarks>
/// <b>These assert the TIERS that get built, not that a flag parses.</b> A setting
/// that is accepted and then builds a different brain from the one documented is
/// exactly what a parse test cannot see, which is why every case reads
/// <c>Brain.Tiers</c>.
/// </remarks>
public sealed class BotModeTests
{
    private static BotBrainSpec? BrainOf(params string[] args) =>
        CommandLine.Parse(args).Bots.Brain;

    private static SimPolicyMicroSpec[] Tiers(BotBrainSpec? brain) =>
        Assert.IsType<BotBrainSpec>(brain).Tiers.Select(Assert.IsType<SimPolicyMicroSpec>).ToArray();

    /// <summary>
    /// With no settings at all, the nine shipped nets drive one class each.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Each class exactly once</b>, so a default that loaded the bundle and
    /// dropped its class assignment — nine nets that drive nothing, the failure a
    /// <c>.gg2p</c> invites because it names no classes — goes red here.
    /// </remarks>
    [Fact]
    public void TheDefaultRunsTheShippedBundle()
    {
        var tiers = Tiers(BrainOf());

        Assert.Equal(
            MapDirectory.ShippedCheckpoints.Select(Path.GetFileNameWithoutExtension),
            tiers.Select(tier => tier.Label));

        PlayerClass[] driven = [.. tiers.SelectMany(tier => tier.Drives).Order()];
        PlayerClass[] expected =
        [
            .. MapDirectory.ShippedClassPolicies
                .Select(entry => Enum.Parse<PlayerClass>(entry[..entry.IndexOf('=')]))
                .Order(),
        ];

        Assert.Equal(expected, driven);
        Assert.Equal(9, driven.Length);
    }

    /// <summary>
    /// The retired flag is accepted, so an existing script still starts, and
    /// changes nothing — scripted included.
    /// </summary>
    [Theory]
    [InlineData("scripted")]
    [InlineData("policy")]
    [InlineData("search")]
    [InlineData("0")]
    public void TheRetiredModeFlagIsIgnored(string mode)
    {
        Assert.Equal(
            Tiers(BrainOf()).Select(tier => tier.Name),
            Tiers(BrainOf("--bot-mode", mode)).Select(tier => tier.Name));
    }

    /// <summary>
    /// An old config file's <c>[Bots] Mode</c> is an unknown key, which a config
    /// file ignores — the fleet's files carry <c>Mode=policy</c>.
    /// </summary>
    [Fact]
    public void AnOldModeKeyInAConfigFileIsIgnored()
    {
        var config = CliCoverageTests.WriteConfig("""{"Bots": {"Mode": "scripted"}}""");

        Assert.Equal(9, Tiers(BrainOf("--config", config)).Length);
        Assert.DoesNotContain(ServerSettings.All, s => s.Section == "Bots" && s.Key == "Mode");
    }

    /// <summary>A named checkpoint replaces the bundle rather than joining it.</summary>
    [Fact]
    public void ANamedCheckpointReplacesTheBundle()
    {
        var tier = Assert.Single(Tiers(BrainOf("--policy", PolicyFixture.Checkpoint)));

        Assert.Equal(PolicyFixture.Label, tier.Label);
    }

    /// <summary>
    /// A class assignment with no checkpoint applies over the bundle, so a class
    /// can be handed back to the scripted layer without naming nine files.
    /// </summary>
    [Fact]
    public void AClassPolicyAloneAppliesToTheBundle()
    {
        var tiers = Tiers(BrainOf("--bot-class-policy", "Soldier=scripted"));

        Assert.Equal(9, tiers.Length);
        Assert.DoesNotContain(tiers, tier => tier.Drives.Contains(PlayerClass.Soldier));
        Assert.Contains(tiers, tier => tier.Drives.Contains(PlayerClass.Scout));
    }

    /// <summary>
    /// The scripted control arm the bench and the recorder compare against builds
    /// no brain at all — null is that arm, not a broken configuration.
    /// </summary>
    [Fact]
    public void TheScriptedControlArmBuildsNoBrain()
    {
        var options = ServerOptions.CreateDefault();
        options.BotMode = BotMode.Scripted;

        BotBrain.Build(options);

        Assert.Null(options.Bots.Brain);
    }

    /// <summary>
    /// A checkpoint handed to the scripted arm is refused, not loaded and ignored.
    /// </summary>
    [Fact]
    public void ACheckpointGivenToTheScriptedArmIsRefused()
    {
        var options = ServerOptions.CreateDefault();
        options.BotMode = BotMode.Scripted;
        options.PolicyPaths = [PolicyFixture.Checkpoint];

        var thrown = Assert.Throws<ArgumentException>(() => BotBrain.Build(options));

        Assert.Contains("never consults one", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>The flags that configured the search and the arbiter are gone, and say so by failing.</summary>
    [Theory]
    [InlineData("--search-bots")]
    [InlineData("--search-threads", "4")]
    [InlineData("--bot-arbiter", "10000")]
    [InlineData("--fixed-bot-budget")]
    public void ARemovedFlagIsUnknown(params string[] args)
    {
        var thrown = Assert.Throws<ArgumentException>(() => CommandLine.Parse(args));

        Assert.Contains(args[0], thrown.Message, StringComparison.Ordinal);
    }
}
