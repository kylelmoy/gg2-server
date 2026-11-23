using GG2.Bots;
using GG2.Protocol;
using GG2.Host;
using GG2.Server;

namespace GG2.Tests;

/// <summary>
/// Several checkpoints, one brain: <c>--policy</c> repeated and the
/// <c>[Bots] Policy</c> list.
/// </summary>
/// <remarks>
/// The fleet loads nine gg2-sim nets, one per class, as nine tiers of one brain.
/// <c>HybridBot</c> takes an ordered tier list and <c>EligibleTiers</c> asks each
/// tier which classes it drives; these gate the way to say so from outside the
/// process.
/// <para>
/// ⚠️ <b>These assert the WIRING, not the bundle.</b> Which checkpoints ship,
/// what they drive and what was measured is <see cref="ShippedCheckpointTests"/>
/// and <c>checkpoints/README.md</c>; these use whatever fixtures make the
/// mechanism visible.
/// </para>
/// </remarks>
public sealed class PolicyTierWiringTests
{
    /// <summary>
    /// Two <c>--policy</c> flags build two tiers, in the order given.
    /// </summary>
    /// <remarks>
    /// <b>Order is asserted even though the shipped bundle's sets are disjoint and
    /// its order therefore cannot matter.</b> It matters for anything that
    /// overlaps: <c>HybridBot.Choose</c> takes the most capable ELIGIBLE tier, so
    /// list position decides which weights play a shared class. A test that only
    /// counted two would pass with them reversed.
    /// </remarks>
    [Fact]
    public void TwoPolicyFlagsBuildTwoTiersInOrder()
    {
        var options = CommandLine.Parse(
        [
            "--policy", PolicyFixture.Checkpoint,
            "--policy", PolicyFixture.Second,
        ]);

        var brain = Assert.IsType<BotBrainSpec>(options.Bots.Brain);

        Assert.Equal(2, brain.Tiers.Count);

        // The LABEL is what tells them apart in a result table, and it is derived
        // from the file name. Two unlabelled policy tiers print "policy+policy",
        // which is the exact defect BotBrainSpec.Name exists to prevent.
        Assert.Equal(
            "gg2-sim:" + PolicyFixture.Label + "+gg2-sim:" + PolicyFixture.SecondLabel,
            brain.Name);
    }

    /// <summary>
    /// ⛔ The second <c>--policy</c> APPENDS; it does not replace.
    /// </summary>
    /// <remarks>
    /// <b>The failure this guards is silent and total.</b> A flag that overwrote
    /// would leave one tier standing, the server would start, the stamp line would
    /// print, and four of the bundle's five driven classes — or one, depending on
    /// order — would quietly fall through to the scripted layer. Nothing else
    /// would say a word.
    /// </remarks>
    [Fact]
    public void ASecondPolicyFlagAppendsRatherThanReplacing()
    {
        var options = CommandLine.Parse(
        [
            "--policy", PolicyFixture.Checkpoint,
            "--policy", PolicyFixture.Second,
        ]);

        Assert.Equal(
            [PolicyFixture.Checkpoint, PolicyFixture.Second],
            options.PolicyPaths);
    }

    /// <summary>A config file names both, comma-separated, and gets both tiers.</summary>
    /// <remarks>
    /// <b>The other half of "every setting is reachable from a file".</b>
    /// <see cref="ConfigFileCoverageTests"/> proves the key MOVES the property;
    /// this proves the property the key moves reaches the brain, which is a
    /// different claim and the one an operator acts on.
    /// </remarks>
    [Fact]
    public void AConfigFileCanNameSeveralCheckpoints()
    {
        var config = CliCoverageTests.WriteConfig(
            $"""
             [Bots]
             Mode=policy
             Policy={PolicyFixture.Checkpoint},{PolicyFixture.Second}
             """);

        var options = CommandLine.Parse(["--config", config]);
        var brain = Assert.IsType<BotBrainSpec>(options.Bots.Brain);

        Assert.Equal(2, brain.Tiers.Count);

        Assert.Equal(
            [PolicyFixture.Checkpoint, PolicyFixture.Second],
            options.PolicyPaths);
    }

    /// <summary>
    /// ⛔ A <c>--policy</c> flag REPLACES the file's whole list.
    /// </summary>
    /// <remarks>
    /// <b>A flag beats the file, and a repeatable flag makes that ambiguous.</b>
    /// The alternative reading — the flag appends to what the file named — would
    /// make <c>--policy</c> the one setting on this server that cannot override
    /// its file, and an operator debugging a bad tier could not turn it off from
    /// the command line. Both orders are checked because <c>--config</c> is applied
    /// in its own pass before the flag loop, and that is the mechanism this relies
    /// on.
    /// </remarks>
    [Fact]
    public void APolicyFlagReplacesTheFilesEntireList()
    {
        var config = CliCoverageTests.WriteConfig(
            $"""
             [Bots]
             Mode=policy
             Policy={PolicyFixture.Checkpoint},{PolicyFixture.Second}
             """);

        foreach (string[] args in new[]
                 {
                     new[] { "--config", config, "--policy", PolicyFixture.Second },
                     ["--policy", PolicyFixture.Second, "--config", config],
                 })
        {
            var options = CommandLine.Parse(args);

            Assert.Equal([PolicyFixture.Second], options.PolicyPaths);
            Assert.Single(Assert.IsType<BotBrainSpec>(options.Bots.Brain).Tiers);
        }
    }

    /// <summary>
    /// <c>[Bots] HandoverRange</c> reaches the brain, and leaving it out keeps the
    /// measured 150px every shipped arm ran at.
    /// </summary>
    [Fact]
    public void TheHandoverRangeReachesTheBrain()
    {
        var config = CliCoverageTests.WriteConfig(
            $"""
             [Bots]
             Mode=policy
             Policy={PolicyFixture.Checkpoint}
             HandoverRule=distance
             HandoverRange=100000
             """);

        var wide = CommandLine.Parse(["--config", config]);
        Assert.Equal(100000, wide.Bots.Brain!.Range);

        var plain = new ServerOptions
        {
            MapDirectory = "maps",
            BotMode = BotMode.Policy,
            PolicyPaths = [PolicyFixture.Checkpoint],
        };
        BotBrain.Build(plain, _ => { });
        Assert.Equal(HybridBot.EngagementRange, plain.Bots.Brain!.Range);
    }

    /// <summary>
    /// Each checkpoint gets its own line, naming the classes it will drive.
    /// </summary>
    /// <remarks>
    /// One line per tier, so a typo in <c>ClassPolicy</c> that silences one tier
    /// shows as that tier driving nothing.
    /// </remarks>
    [Fact]
    public void EveryCheckpointReportsTheClassesItDrives()
    {
        var lines = new List<string>();

        var options = new ServerOptions
        {
            MapDirectory = "maps",
            BotMode = BotMode.Policy,
            PolicyPaths = [PolicyFixture.Checkpoint, PolicyFixture.Second],
            BotClassPolicies = [PolicyFixture.SoldierAssignment, PolicyFixture.ScoutAssignment],
        };

        BotBrain.Build(options, lines.Add);

        var stamps = lines.Where(line => line.StartsWith("policy ", StringComparison.Ordinal)).ToArray();

        Assert.Equal(2, stamps.Length);
        Assert.Contains(Path.GetFileName(PolicyFixture.Checkpoint), stamps[0]);
        Assert.EndsWith("drives Soldier", stamps[0]);
        Assert.Contains(Path.GetFileName(PolicyFixture.Second), stamps[1]);
        Assert.EndsWith("drives Scout", stamps[1]);
        Assert.DoesNotContain(lines, line => line.StartsWith("WARNING:", StringComparison.Ordinal));
    }

    /// <summary>
    /// A net nobody assigned a class to is warned about: it would load and never
    /// drive.
    /// </summary>
    [Fact]
    public void ANetThatDrivesNothingIsWarnedAbout()
    {
        var lines = new List<string>();

        var options = new ServerOptions
        {
            MapDirectory = "maps",
            BotMode = BotMode.Policy,
            PolicyPaths = [PolicyFixture.Checkpoint],
        };

        BotBrain.Build(options, lines.Add);

        Assert.Contains(lines, line => line.StartsWith("WARNING: " + PolicyFixture.Label + " drives no classes", StringComparison.Ordinal));
    }

    /// <summary>
    /// Only gg2-sim's <c>.gg2p</c> is loadable, and anything else is refused by
    /// the settings check, before any file is opened.
    /// </summary>
    /// <remarks>
    /// In <c>CheckSettings</c> rather than at load, so the panel's save and the
    /// fleet renderer refuse it too instead of a restart ending in exit 2.
    /// </remarks>
    [Theory]
    [InlineData("checkpoints/policy.npz")]
    [InlineData("checkpoints/net.onnx")]
    public void ACheckpointThatIsNotAGg2pIsRefused(string path)
    {
        var options = new ServerOptions
        {
            MapDirectory = "maps",
            BotMode = BotMode.Policy,
            PolicyPaths = [path],
        };

        var error = Assert.Throws<ArgumentException>(() => BotBrain.CheckSettings(options));
        Assert.Contains("only gg2-sim .gg2p", error.Message);
    }

    /// <summary>
    /// A live server's brain carries the shipped release band through to every
    /// <see cref="HybridBot"/> it wraps.
    /// </summary>
    /// <remarks>
    /// The server builds its spec from <see cref="BotBrainSpec"/>'s defaults, so this
    /// is what makes the measured band the one players meet. Nothing else in the
    /// suite moved when the default changed, so without this a regression to "no
    /// band" would go green.
    /// </remarks>
    [Fact]
    public void TheServersBrainCarriesTheReleaseBand()
    {
        var options = new ServerOptions
        {
            MapDirectory = "maps",
            BotMode = BotMode.Policy,
            PolicyPaths = [PolicyFixture.Checkpoint],
        };

        BotBrain.Build(options, _ => { });

        var spec = options.Bots.Brain;
        Assert.NotNull(spec);
        Assert.Equal(BotBrainSpec.DefaultReleaseRange, spec.ReleaseRange);
        Assert.Equal(300, BotBrainSpec.DefaultReleaseRange);

        var hybrid = spec.Wrap(new ScriptedBot(null, new NavQuery(Team.Red), BotSkill.ForTier(5), stagger: 0));
        Assert.Equal(300, hybrid.ReleaseRange);
        Assert.Equal(HybridBot.EngagementRange, hybrid.Range);
    }
}
