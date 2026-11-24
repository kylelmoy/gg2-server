using GG2.Bots;
using GG2.Host;
using GG2.Protocol;
using GG2.Server;

namespace GG2.Tests;

/// <summary>
/// Who drives which class: <c>Bots.ClassPolicy</c>.
/// </summary>
/// <remarks>
/// <c>Bots.ClassPolicy</c> decides who DRIVES a class that is played; which
/// classes bots play is <c>[BotClasslimits]</c>, gated by <c>BotClassLimitTests</c>.
/// A <c>.gg2p</c> names no classes of its own, so a net drives exactly the classes
/// assigned to it and nothing else.
/// </remarks>
public sealed class BotClassPolicyTests
{
    private static ServerOptions Parse(params string[] args) =>
        CommandLine.Parse([.. args]);

    /// <summary>Which classes tier <paramref name="index"/> answers Drives for.</summary>
    private static PlayerClass[] DrivenBy(ServerOptions options, int index)
    {
        var brain = Assert.IsType<BotBrainSpec>(options.Bots.Brain);
        var controller = brain.Tiers[index].Create();

        return [.. Enum.GetValues<PlayerClass>()
            .Where(playerClass => playerClass <= PlayerClass.Pyro)
            .Where(controller.Drives)];
    }

    private static List<string> Report(ServerOptions options)
    {
        var lines = new List<string>();
        BotBrain.Build(options, lines.Add);
        return lines;
    }

    /// <summary>Each net drives exactly the classes assigned to it.</summary>
    /// <remarks>
    /// Both tiers and both directions: a test that asserted only the members would
    /// pass for a tier that drives everything.
    /// </remarks>
    [Fact]
    public void EachNetDrivesExactlyItsAssignedClasses()
    {
        var options = Parse(
            "--policy", PolicyFixture.Checkpoint,
            "--policy", PolicyFixture.Second,
            "--bot-class-policy", PolicyFixture.SoldierAssignment + ",Heavy=" + PolicyFixture.Label,
            "--bot-class-policy", PolicyFixture.ScoutAssignment);

        Assert.Equal([PlayerClass.Soldier, PlayerClass.Heavy], DrivenBy(options, 0));
        Assert.Equal([PlayerClass.Scout], DrivenBy(options, 1));
    }

    /// <summary>A class sent to <c>scripted</c> is driven by no tier.</summary>
    [Fact]
    public void AClassCanBeLeftToTheScriptedLayer()
    {
        var options = Parse(
            "--policy", PolicyFixture.Checkpoint,
            "--bot-class-policy", PolicyFixture.SoldierAssignment + ",Scout=scripted");

        Assert.Equal([PlayerClass.Soldier], DrivenBy(options, 0));
    }

    /// <summary>
    /// ⛔ A net left driving nothing is warned about, not silently loaded.
    /// </summary>
    /// <remarks>
    /// A controller configured, reported and never consulted is a server saying it
    /// is doing something it is not. Not refused, because turning one tier off to
    /// see what the rest do is a legitimate experiment.
    /// </remarks>
    [Fact]
    public void ATierLeftDrivingNothingSaysSo()
    {
        var options = new ServerOptions
        {
            MapDirectory = "maps",
            BotMode = BotMode.Policy,
            PolicyPaths = [PolicyFixture.Checkpoint, PolicyFixture.Second],
            BotClassPolicies = [PolicyFixture.SoldierAssignment],
        };

        var lines = Report(options);

        Assert.Contains(lines, line => line.StartsWith("WARNING:", StringComparison.Ordinal)
            && line.Contains(PolicyFixture.SecondLabel, StringComparison.Ordinal)
            && line.Contains("drives no classes", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.StartsWith("WARNING: " + PolicyFixture.Label + " ", StringComparison.Ordinal));

        Assert.Empty(DrivenBy(options, 1));
    }

    /// <summary>Typos, duplicates and impossible modes are refused with the reason.</summary>
    /// <remarks>
    /// ⛔ <b>A mis-typed label is the failure this setting invites</b>, and it is
    /// invisible: the class silently stays scripted, the server starts, and the bots
    /// look fine. A duplicate is refused rather than last-wins, because last-wins
    /// would make the order two config lines were written in decide which weights
    /// play a class.
    /// </remarks>
    [Theory]
    [InlineData("Sniper=no_such_checkpoint", "no checkpoint is loaded under that name")]
    [InlineData("Nobody=scripted", "is not a class a bot can be")]
    [InlineData("Scout=scripted,Scout=scripted", "is already assigned")]
    [InlineData("Scout", "expected Class=name")]
    public void AMisconfiguredClassPolicyIsRefusedWithTheReason(string entry, string expected)
    {
        var options = new ServerOptions
        {
            MapDirectory = "maps",
            BotMode = BotMode.Policy,
            PolicyPaths = [PolicyFixture.Checkpoint],
            BotClassPolicies = [.. entry.Split(',')],
        };

        var thrown = Assert.Throws<ArgumentException>(() => BotBrain.Build(options));

        Assert.Contains(expected, thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Class policies given to a mode that runs no policy are refused, not ignored.
    /// </summary>
    [Fact]
    public void ClassPoliciesWithoutAPolicyModeAreRefused()
    {
        var options = new ServerOptions
        {
            MapDirectory = "maps",
            BotMode = BotMode.Scripted,
            BotClassPolicies = ["Scout=scripted"],
        };

        Assert.Contains(
            "runs no policy",
            Assert.Throws<ArgumentException>(() => BotBrain.Build(options)).Message,
            StringComparison.Ordinal);
    }

    /// <summary>A config file reaches the setting.</summary>
    [Fact]
    public void AConfigFileReachesTheSetting()
    {
        var config = CliCoverageTests.WriteConfig(new
        {
            Bots = new { Policy = new[] { PolicyFixture.Checkpoint }, ClassPolicy = new[] { PolicyFixture.SoldierAssignment } },
        });

        var options = CommandLine.Parse(["--config", config]);

        Assert.Equal([PolicyFixture.SoldierAssignment], options.BotClassPolicies);
        Assert.Equal([PlayerClass.Soldier], DrivenBy(options, 0));
    }
}
