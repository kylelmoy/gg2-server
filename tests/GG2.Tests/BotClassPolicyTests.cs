using GG2.Bots;
using GG2.Host;
using GG2.Protocol;
using GG2.Server;

namespace GG2.Tests;

/// <summary>
/// Who drives which class: the default and the per-class override.
/// </summary>
/// <remarks>
/// <c>Bots.ClassPolicy</c> decides who DRIVES a class that is played; which
/// classes bots play is <c>[BotClasslimits]</c>, gated by <c>BotClassLimitTests</c>.
/// <para>
/// ⛔ <b>The default is the thing to protect.</b> Every checkpoint drives the
/// classes its own file claims, and a server that configures nothing gets that —
/// so the tests below that assert an override work only because
/// <see cref="TheDefaultIsEveryCheckpointDrivingItsOwnClasses"/> establishes what
/// is being overridden.
/// </para>
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

    /// <summary>
    /// ⛔ Configure nothing and every checkpoint drives the classes it claims.
    /// </summary>
    /// <remarks>
    /// <b>This is the behaviour the whole feature is built around, so it is
    /// asserted first and separately.</b> A per-class setting whose default was
    /// anything else — no class driven until configured, say — would make a
    /// working server depend on a config file nobody writes, and the failure would
    /// be a complete, plausible all-scripted bot.
    /// </remarks>
    [Fact]
    public void TheDefaultIsEveryCheckpointDrivingItsOwnClasses()
    {
        var options = Parse(
            "--bot-mode", "policy",
            "--policy", PolicyFixture.Checkpoint,
            "--policy", PolicyFixture.EightFieldCheckpoint);

        Assert.Equal(
            [PlayerClass.Scout, PlayerClass.Soldier, PlayerClass.Demoman, PlayerClass.Heavy],
            DrivenBy(options, 0));

        Assert.Equal(
            [PlayerClass.Engineer, PlayerClass.Spy, PlayerClass.Pyro],
            DrivenBy(options, 1));
    }

    /// <summary>A class can be sent back to the scripted layer.</summary>
    /// <remarks>
    /// <b>The negative assignment, and the one with no other way to express it.</b>
    /// Before this, dropping a class the checkpoint claims meant re-minting the
    /// file. Soldier is the case that motivated it: it ships driving, on a mirror
    /// of 46.47 that is <em>below</em> its null — <c>checkpoints/README.md</c> —
    /// so an operator who does not like what a soldier does needs a way to turn
    /// exactly that off without giving up the other three.
    /// </remarks>
    [Fact]
    public void AClassCanBeSentBackToTheScriptedLayer()
    {
        var options = Parse(
            "--bot-mode", "policy",
            "--policy", PolicyFixture.Checkpoint,
            "--bot-class-policy", "Soldier=scripted");

        Assert.Equal(
            [PlayerClass.Scout, PlayerClass.Demoman, PlayerClass.Heavy],
            DrivenBy(options, 0));
    }

    /// <summary>A class can be moved from the tier that claims it to another.</summary>
    /// <remarks>
    /// <b>Both halves, and the second is the one that would be missed.</b> Moving
    /// Pyro to the first tier must ADD it there and REMOVE it from the tier whose
    /// file claims it — a test asserting only the addition would pass on an
    /// implementation that left both tiers driving pyro, which is the overlap the
    /// bundle is disjoint specifically to avoid.
    /// </remarks>
    [Fact]
    public void AClassMovesToTheTierItIsAssignedTo()
    {
        var options = Parse(
            "--bot-mode", "policy",
            "--policy", PolicyFixture.Checkpoint,
            "--policy", PolicyFixture.EightFieldCheckpoint,
            "--bot-class-policy", "Pyro=" + Path.GetFileNameWithoutExtension(PolicyFixture.Checkpoint));

        Assert.Contains(PlayerClass.Pyro, DrivenBy(options, 0));
        Assert.DoesNotContain(PlayerClass.Pyro, DrivenBy(options, 1));
    }

    /// <summary>
    /// ⛔ Driving a class the file does not list is legal and WARNED about.
    /// </summary>
    /// <remarks>
    /// <b>Legal because the warning cannot tell two cases apart.</b> A checkpoint's
    /// class set does two jobs — what the run trained and what its author intends
    /// it to drive — and the ship bundle narrows the second into the first, which
    /// destroys the record of what else was trained. So a class the file does not
    /// list is either one the run never saw, or one a narrowing dropped and an
    /// operator is legitimately putting back. ⛔ <b>Refusing would make the second
    /// impossible</b>, and it is the case the shipped `tier2_pyro` actually
    /// creates: that file trained engineer and spy and no longer says so.
    /// </remarks>
    [Fact]
    public void DrivingAClassTheFileDoesNotListWarnsRatherThanRefusing()
    {
        var options = new ServerOptions
        {
            MapDirectory = "maps",
            BotMode = BotMode.Policy,
            PolicyPaths = [PolicyFixture.Checkpoint],
            BotClassPolicies = ["Sniper=" + Path.GetFileNameWithoutExtension(PolicyFixture.Checkpoint)],
        };

        var lines = Report(options);

        Assert.Contains(lines, line => line.StartsWith("WARNING:", StringComparison.Ordinal)
            && line.Contains("Sniper", StringComparison.Ordinal)
            && line.Contains("does not list", StringComparison.Ordinal));

        // Legal: it built, and the class really is driven.
        Assert.Contains(PlayerClass.Sniper, DrivenBy(options, 0));
    }

    /// <summary>
    /// The control: an assignment the file DOES list produces no warning.
    /// </summary>
    /// <remarks>
    /// Without this the warning above could fire on every assignment and the test
    /// would still be green — and a warning an operator sees every time is one
    /// they stop reading.
    /// </remarks>
    [Fact]
    public void AnAssignmentTheFileAlreadyClaimsProducesNoWarning()
    {
        var options = new ServerOptions
        {
            MapDirectory = "maps",
            BotMode = BotMode.Policy,
            PolicyPaths = [PolicyFixture.Checkpoint],
            BotClassPolicies = ["Scout=" + Path.GetFileNameWithoutExtension(PolicyFixture.Checkpoint)],
        };

        Assert.DoesNotContain(
            Report(options), line => line.StartsWith("WARNING:", StringComparison.Ordinal));
    }

    /// <summary>
    /// ⛔ A tier left driving nothing is warned about, not silently loaded.
    /// </summary>
    /// <remarks>
    /// <b>The same defect the arbiter invariant refuses in its own case</b>: a
    /// controller configured, reported and never consulted is a server saying it is
    /// doing something it is not. Not refused, because turning one tier off to see
    /// what the rest do is a legitimate experiment — but megabytes of weights and a
    /// stamp check bought for no behaviour should not look like an ordinary
    /// startup.
    /// </remarks>
    [Fact]
    public void ATierLeftDrivingNothingSaysSo()
    {
        var options = new ServerOptions
        {
            MapDirectory = "maps",
            BotMode = BotMode.Policy,
            PolicyPaths = [PolicyFixture.Checkpoint, PolicyFixture.EightFieldCheckpoint],
            BotClassPolicies = ["Engineer=scripted", "Spy=scripted", "Pyro=scripted"],
        };

        var lines = Report(options);

        Assert.Contains(lines, line => line.StartsWith("WARNING:", StringComparison.Ordinal)
            && line.Contains(
                Path.GetFileNameWithoutExtension(PolicyFixture.EightFieldCheckpoint),
                StringComparison.Ordinal)
            && line.Contains("drives no classes", StringComparison.Ordinal));

        Assert.Empty(DrivenBy(options, 1));
    }

    /// <summary>Typos, duplicates and impossible modes are refused with the reason.</summary>
    /// <remarks>
    /// ⛔ <b>A mis-typed label is the failure this setting invites</b>, and it is
    /// invisible: the class silently keeps whatever default it had, the server
    /// starts, and the bots look fine. Refusing at startup with the loaded names in
    /// the message is the only point at which anybody would find out.
    /// <para>
    /// <b>A duplicate is refused rather than last-wins</b> for the same reason the
    /// bundle's disjointness matters: last-wins would make the ORDER two config
    /// lines were written in decide which weights play a class.
    /// </para>
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
    /// <remarks>
    /// The same rule as a checkpoint handed to <c>scripted</c>: a setting read,
    /// validated and then unable to apply is a server saying it is doing something
    /// it is not.
    /// </remarks>
    [Fact]
    public void ClassPoliciesWithoutAPolicyModeAreRefused()
    {
        var options = new ServerOptions
        {
            MapDirectory = "maps",
            BotMode = BotMode.Search,
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
        var config = CliCoverageTests.WriteConfig(
            $"""
             [Bots]
             Mode=policy
             Policy={PolicyFixture.Checkpoint}
             ClassPolicy=Soldier=scripted
             """);

        var options = CommandLine.Parse(["--config", config]);

        Assert.Equal(["Soldier=scripted"], options.BotClassPolicies);
        Assert.DoesNotContain(PlayerClass.Soldier, DrivenBy(options, 0));
    }

    /// <summary>
    /// An explicit assignment resolves an overlap instead of warning about it.
    /// </summary>
    /// <remarks>
    /// <b>The property that makes this setting the ANSWER to the overlap warning
    /// rather than another source of one.</b> Two checkpoints claiming the same
    /// class warn, because list order then decides which weights play it; naming
    /// the class explicitly takes it out of every tier but the one named, so there
    /// is nothing left for order to decide.
    /// </remarks>
    [Fact]
    public void AnExplicitAssignmentResolvesAnOverlapRatherThanWarningAboutIt()
    {
        var label = Path.GetFileNameWithoutExtension(PolicyFixture.DefaultSetCheckpoint);

        // Two files with identical class sets: complete overlap, four warnings.
        var overlapping = new ServerOptions
        {
            MapDirectory = "maps",
            BotMode = BotMode.Policy,
            PolicyPaths = [PolicyFixture.Checkpoint, PolicyFixture.DefaultSetCheckpoint],
        };

        Assert.Equal(
            4,
            Report(overlapping).Count(line => line.Contains("driven by both", StringComparison.Ordinal)));

        // Assign all four and there is nothing left to be ambiguous about.
        var resolved = new ServerOptions
        {
            MapDirectory = "maps",
            BotMode = BotMode.Policy,
            PolicyPaths = [PolicyFixture.Checkpoint, PolicyFixture.DefaultSetCheckpoint],
            BotClassPolicies =
                [$"Scout={label}", $"Soldier={label}", $"Demoman={label}", $"Heavy={label}"],
        };

        Assert.DoesNotContain(
            Report(resolved), line => line.Contains("driven by both", StringComparison.Ordinal));
    }
}
