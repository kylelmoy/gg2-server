using GG2.Bots;
using GG2.Host;
using GG2.Protocol;
using GG2.Server;
using GG2.Simulation;

namespace GG2.Tests;

/// <summary>
/// gg2-sim's <c>.gg2p</c> policies as a bot tier: the file, the controller in a
/// match, and the server wiring.
/// </summary>
/// <remarks>
/// ⚠️ <b>None of these can show the observation or the actions match gg2-sim's</b>
/// — that needs gg2-sim's own <c>ref/PolicyDriver.cs</c>, which this repo does not
/// carry. <c>docs/BOTS.md</c>, "gg2-sim policies", has how that was checked and
/// what to re-run. These use synthetic weights, so they hold the mechanism and
/// not any trained behaviour.
/// </remarks>
public sealed class SimPolicyTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "gg2-simpolicy-" + Guid.NewGuid().ToString("N"));

    public SimPolicyTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void LoadsTeamAndDuelNets()
    {
        var team = SimPolicyNetwork.Load(Write("team.gg2p", SimPolicyObservation.TeamSize, SimPolicyController.TeamAimActions));
        var duel = SimPolicyNetwork.Load(Write("duel.gg2p", SimPolicyObservation.DuelSize, SimPolicyController.DuelAimActions));

        Assert.Equal(253, SimPolicyObservation.TeamSize);
        Assert.Equal(169, SimPolicyObservation.DuelSize);
        Assert.True(team.IsTeam);
        Assert.False(duel.IsTeam);
        Assert.Equal([3, 2, 2, 99], team.Heads.ToArray());
        Assert.Equal([3, 2, 2, 49], duel.Heads.ToArray());
    }

    /// <summary>
    /// A team-width net with a duel's aim head is refused at load, not decoded
    /// against the wrong bins on a live server.
    /// </summary>
    [Fact]
    public void RefusesAnAimHeadThatDoesNotFitTheObservation()
    {
        var path = Write("bad.gg2p", SimPolicyObservation.TeamSize, SimPolicyController.DuelAimActions);

        Assert.Throws<InvalidDataException>(() => SimPolicyNetwork.Load(path));
    }

    [Fact]
    public void RefusesAFileThatIsNotAGg2p()
    {
        var path = Path.Combine(_directory, "junk.gg2p");
        File.WriteAllBytes(path, new byte[64]);

        Assert.Throws<InvalidDataException>(() => SimPolicyNetwork.Load(path));
    }

    /// <summary>
    /// The policy drives inside a real match, and the match is reproducible.
    /// </summary>
    /// <remarks>
    /// A tier that never got the wheel would also be reproducible, which is why
    /// the driven count is asserted. Reproducibility is what a controller drawing
    /// from <c>WorldState.Random</c>, or holding state across bots, would break.
    /// </remarks>
    [Fact]
    public void DrivesRedInAHeadlessMatchReproducibly()
    {
        var team = SimPolicyNetwork.Load(Write("team.gg2p", SimPolicyObservation.TeamSize, SimPolicyController.TeamAimActions));
        var duel = SimPolicyNetwork.Load(Write("duel.gg2p", SimPolicyObservation.DuelSize, SimPolicyController.DuelAimActions));
        var brain = BotBrainSpec.Of(
            new SimPolicyMicroSpec(team, [PlayerClass.Soldier], "team"),
            new SimPolicyMicroSpec(duel, [PlayerClass.Scout], "duel")) with { Rule = HandoverRule.Distance };

        var (first, driven) = Play(brain);
        var (second, _) = Play(brain);

        Assert.Equal(first, second);
        Assert.True(driven[0] > 0, "the team net never drove");
        Assert.True(driven[1] > 0, "the duel net never drove");
    }

    [Fact]
    public void AGg2pTierDrivesOnlyTheClassesAssignedToIt()
    {
        var path = Write("kill.gg2p", SimPolicyObservation.TeamSize, SimPolicyController.TeamAimActions);
        var options = CommandLine.Parse(
        [
            "--bot-mode", "policy",
            "--policy", path,
            "--bot-class-policy", "Soldier=kill",
            "--bot-handover-rule", "distance",
        ]);

        var brain = Assert.IsType<BotBrainSpec>(options.Bots.Brain);
        var tier = Assert.IsType<SimPolicyMicroSpec>(Assert.Single(brain.Tiers));

        Assert.Equal([PlayerClass.Soldier], tier.Drives);
        Assert.Equal("gg2-sim:kill", brain.Name);
        Assert.Equal(HandoverRule.Distance, brain.Rule);
    }

    /// <summary>
    /// ⚠️ A <c>.gg2p</c> nobody assigned a class loads and drives nothing, and the
    /// server says so — the file has no class set to fall back to.
    /// </summary>
    [Fact]
    public void AnUnassignedGg2pDrivesNothingAndIsWarnedAbout()
    {
        var options = new ServerOptions
        {
            BotMode = BotMode.Policy,
            PolicyPaths = [Write("kill.gg2p", SimPolicyObservation.TeamSize, SimPolicyController.TeamAimActions)],
        };
        var lines = new List<string>();

        BotBrain.Build(options, lines.Add);

        var tier = Assert.IsType<SimPolicyMicroSpec>(Assert.Single(options.Bots.Brain!.Tiers));
        Assert.Empty(tier.Drives);
        Assert.Contains(lines, line => line.StartsWith("WARNING: kill drives no classes", StringComparison.Ordinal));
    }

    [Fact]
    public void TheHandoverRuleDefaultsToMacroTask()
    {
        var options = CommandLine.Parse(["--bot-mode", "policy", "--policy", PolicyFixture.Checkpoint]);

        Assert.Equal(HandoverRule.MacroTask, options.Bots.Brain!.Rule);
    }

    private static (MatchResult Result, long[] Driven) Play(BotBrainSpec brain)
    {
        var options = HeadlessMatchOptions.Default with
        {
            Map = "ctf_truefort",
            Bots = 8,
            Seed = 7123,
            MaxTicks = 1800,
            AllowedClasses = [PlayerClass.Soldier, PlayerClass.Scout],
            Brain = brain,
            HybridTeam = Team.Red,
        };
        var match = HeadlessMatch.Load(options, MapDirectory.Resolve(), MapDirectory.ResolveNav());
        var driven = new long[2];

        while (match.Outcome == MatchOutcome.Running)
        {
            match.Step();

            for (var slot = 1; slot < WorldState.MaxSlots; slot++)
            {
                if (match.HybridBotFor(slot)?.Driving is SimPolicyController controller)
                    driven[controller.Network.IsTeam ? 0 : 1]++;
            }
        }

        return (match.Result, driven);
    }

    // A net of the given widths with seeded random weights: 16 hidden units, the
    // four heads gg2-sim exports.
    private string Write(string name, int inputs, int aim)
    {
        var path = Path.Combine(_directory, name);
        var random = new Random(inputs + aim);
        int[] heads = [3, 2, 2, aim];
        (int Out, int In)[] layers = [(16, inputs), (heads.Sum(), 16)];

        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(SimPolicyNetwork.Magic);
        writer.Write(SimPolicyNetwork.Version);
        writer.Write(layers.Length);
        writer.Write(heads.Length);

        foreach (var head in heads)
            writer.Write(head);

        foreach (var (outputs, ins) in layers)
        {
            writer.Write(outputs);
            writer.Write(ins);

            for (var i = 0; i < (outputs * ins) + outputs; i++)
                writer.Write((float)((random.NextDouble() * 2) - 1));
        }

        return path;
    }
}
