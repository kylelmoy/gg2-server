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
/// <b>Landed 2026-09-07, to receive the <c>gg2-rl</c> lane's two-tier ship
/// bundle.</b> Until then a server could load exactly one checkpoint, and a
/// checkpoint's class set is a property of the FILE — so covering five classes
/// with two families of weights was not expressible at all. The seam it needed
/// already existed: <c>HybridBot</c> has taken an ordered tier list since phase 9
/// began, and <c>EligibleTiers</c> has always asked each tier which classes it
/// drives. What was missing was a way to say so from outside the process.
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
            "--bot-mode", "policy",
            "--policy", PolicyFixture.Checkpoint,
            "--policy", PolicyFixture.EightFieldCheckpoint,
        ]);

        var brain = Assert.IsType<BotBrainSpec>(options.Bots.Brain);

        Assert.Equal(2, brain.Tiers.Count);

        // The LABEL is what tells them apart in a result table, and it is derived
        // from the file name. Two unlabelled policy tiers print "policy+policy",
        // which is the exact defect BotBrainSpec.Name exists to prevent.
        Assert.Equal(
            "policy:" + Path.GetFileNameWithoutExtension(PolicyFixture.Checkpoint)
            + "+policy:" + Path.GetFileNameWithoutExtension(PolicyFixture.EightFieldCheckpoint),
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
            "--bot-mode", "policy",
            "--policy", PolicyFixture.Checkpoint,
            "--policy", PolicyFixture.EightFieldCheckpoint,
        ]);

        Assert.Equal(
            [PolicyFixture.Checkpoint, PolicyFixture.EightFieldCheckpoint],
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
             Policy={PolicyFixture.Checkpoint},{PolicyFixture.EightFieldCheckpoint}
             """);

        var options = CommandLine.Parse(["--config", config]);
        var brain = Assert.IsType<BotBrainSpec>(options.Bots.Brain);

        Assert.Equal(2, brain.Tiers.Count);

        Assert.Equal(
            [PolicyFixture.Checkpoint, PolicyFixture.EightFieldCheckpoint],
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
             Policy={PolicyFixture.Checkpoint},{PolicyFixture.EightFieldCheckpoint}
             """);

        foreach (string[] args in new[]
                 {
                     new[] { "--config", config, "--policy", PolicyFixture.SniperTrainedCheckpoint },
                     ["--policy", PolicyFixture.SniperTrainedCheckpoint, "--config", config],
                 })
        {
            var options = CommandLine.Parse(args);

            Assert.Equal([PolicyFixture.SniperTrainedCheckpoint], options.PolicyPaths);
            Assert.Single(Assert.IsType<BotBrainSpec>(options.Bots.Brain).Tiers);
        }
    }

    /// <summary>
    /// Each checkpoint gets its own stamp line, naming the classes it will drive.
    /// </summary>
    /// <remarks>
    /// <b>One line per tier, not one per server.</b> Two checkpoints can disagree
    /// about their stamp — one bypassed and one not — and a single summary line
    /// could not say which. The class set is on the line because since 2026-09-07
    /// a shipped file may name a should-drive set narrower than what it trained on,
    /// and what the server will DRIVE is the only half either repository can check.
    /// </remarks>
    [Fact]
    public void EveryCheckpointReportsItsOwnStampAndClassSet()
    {
        var lines = new List<string>();

        var options = new ServerOptions
        {
            MapDirectory = "maps",
            BotMode = BotMode.Policy,
            PolicyPaths = [PolicyFixture.Checkpoint, PolicyFixture.EightFieldCheckpoint],
        };

        BotBrain.Build(options, lines.Add);

        var stamps = lines.Where(line => line.StartsWith("policy ", StringComparison.Ordinal)).ToArray();

        Assert.Equal(2, stamps.Length);

        Assert.Contains(Path.GetFileName(PolicyFixture.Checkpoint), stamps[0]);
        Assert.Contains("Scout,Soldier,Demoman,Heavy", stamps[0]);

        Assert.Contains(Path.GetFileName(PolicyFixture.EightFieldCheckpoint), stamps[1]);
        Assert.Contains("Engineer,Spy,Pyro", stamps[1]);
    }

    /// <summary>
    /// ⛔ Overlapping class sets WARN, naming the class and both files.
    /// </summary>
    /// <remarks>
    /// <b>Reported, not refused.</b> An overlap is a legitimate fallback ladder —
    /// the arbiter drops to the later tier under pressure — but it is also the only
    /// thing that makes registration order change what the server plays, and
    /// nothing downstream ever mentions it again. The shipped bundle's own
    /// load-bearing claim is that its two sets are disjoint so order cannot matter
    /// (<see cref="ShippedCheckpointTests"/>); an operator who breaks that by
    /// adding a third checkpoint has to be told.
    /// </remarks>
    [Fact]
    public void OverlappingClassSetsAreWarnedAboutRatherThanRefused()
    {
        var lines = new List<string>();

        var options = new ServerOptions
        {
            MapDirectory = "maps",
            BotMode = BotMode.Policy,

            // ppo_final records no class set at all, so it falls back to the
            // default four; DefaultSetCheckpoint's set IS the default four. Two
            // files, same set, complete overlap.
            PolicyPaths = [PolicyFixture.Checkpoint, PolicyFixture.DefaultSetCheckpoint],
        };

        BotBrain.Build(options, lines.Add);

        var warnings = lines.Where(line => line.StartsWith("WARNING:", StringComparison.Ordinal)).ToArray();

        // One per overlapping class, each naming both files, so a partial overlap
        // says exactly which classes are affected rather than that one exists.
        Assert.Equal(4, warnings.Length);

        foreach (var warning in warnings)
        {
            Assert.Contains(Path.GetFileNameWithoutExtension(PolicyFixture.Checkpoint), warning);
            Assert.Contains(
                Path.GetFileNameWithoutExtension(PolicyFixture.DefaultSetCheckpoint), warning);
        }

        // And it built anyway — both tiers are there.
        Assert.Equal(2, Assert.IsType<BotBrainSpec>(options.Bots.Brain).Tiers.Count);
    }

    /// <summary>
    /// Disjoint sets produce no warning, which is what makes the one above real.
    /// </summary>
    /// <remarks>
    /// <b>The control.</b> Without it the warning could fire on every pair and the
    /// test above would still be green — and an operator who saw it every time
    /// would learn to ignore it.
    /// </remarks>
    [Fact]
    public void DisjointClassSetsProduceNoWarning()
    {
        var lines = new List<string>();

        var options = new ServerOptions
        {
            MapDirectory = "maps",
            BotMode = BotMode.Policy,
            PolicyPaths = [PolicyFixture.Checkpoint, PolicyFixture.EightFieldCheckpoint],
        };

        BotBrain.Build(options, lines.Add);

        Assert.DoesNotContain(lines, line => line.StartsWith("WARNING:", StringComparison.Ordinal));
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
