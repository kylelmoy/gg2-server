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

    [Fact]
    public void LoadsAZoomNetAndRefusesAHalfZoomLayout()
    {
        var zoom = SimPolicyNetwork.Load(WriteZoom("zoom.gg2p", scoped: true));

        Assert.True(zoom.IsZoom);
        Assert.True(zoom.IsTeam);
        Assert.Equal([3, 2, 2, 99, 2], zoom.Heads.ToArray());

        // A fifth head on a 253-wide net, and a 255-wide net with four heads.
        Assert.Throws<InvalidDataException>(() => SimPolicyNetwork.Load(
            Write("five.gg2p", SimPolicyObservation.TeamSize, SimPolicyController.TeamAimActions, zoomHead: true)));
        Assert.Throws<InvalidDataException>(() => SimPolicyNetwork.Load(
            Write("four.gg2p", SimPolicyObservation.ZoomSize, SimPolicyController.TeamAimActions)));
    }

    /// <summary>
    /// gg2-sim 31f42ef's demo layout: 286 inputs and a fifth head, Special. Both
    /// or neither, as for the zoom net.
    /// </summary>
    [Fact]
    public void LoadsADemoNetAndRefusesAHalfDemoLayout()
    {
        var demo = SimPolicyNetwork.Load(WriteDemo("demo.gg2p", detonate: true));

        Assert.True(demo.IsDemo);
        Assert.True(demo.IsTeam);
        Assert.False(demo.IsZoom);
        Assert.Equal(286, demo.InputSize);
        Assert.Equal([3, 2, 2, 99, 2], demo.Heads.ToArray());

        Assert.Throws<InvalidDataException>(() => SimPolicyNetwork.Load(
            Write("demo4.gg2p", SimPolicyObservation.DemoSize, SimPolicyController.TeamAimActions)));
    }

    /// <summary>
    /// The demo net's fifth head is the Special key, pressed with every tick of
    /// the decision when the head says so and never otherwise; a zoom net's fifth
    /// head never presses it. Its observation is bit-exact against gg2-sim's ref
    /// by a cross-check outside this suite (docs/BOTS.md); this pins the decode.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ADemoNetsFifthHeadIsTheSpecialKey(bool detonate)
    {
        var options = HeadlessMatchOptions.Default with
        {
            Map = "ctf_truefort",
            Bots = 4,
            Seed = 7123,
            MaxTicks = 600,
            AllowedClasses = [PlayerClass.Demoman],
        };
        var match = HeadlessMatch.Load(options, MapDirectory.Resolve(), MapDirectory.ResolveNav());
        var demo = new SimPolicyController(SimPolicyNetwork.Load(WriteDemo(detonate ? "det.gg2p" : "hold.gg2p", detonate)), [PlayerClass.Demoman]);
        var zoom = new SimPolicyController(SimPolicyNetwork.Load(WriteZoom("z.gg2p", scoped: true)), [PlayerClass.Demoman]);
        var polled = 0;
        Span<int> nearest = stackalloc int[1];

        while (match.Outcome == MatchOutcome.Running && polled < 60)
        {
            match.Step();
            var view = new WorldView(in match.State, match.Collision);

            if (!view.IsAlive(1) || view.NearestEnemies(1, nearest) == 0)
                continue;

            Assert.Equal(detonate, (demo.Poll(in view, 1, match.Tick).Keys & PlayerKeys.Special) != 0);
            Assert.Equal(PlayerKeys.None, zoom.Poll(in view, 1, match.Tick).Keys & PlayerKeys.Special);
            polled++;
        }

        Assert.Equal(60, polled);
    }

    /// <summary>
    /// gg2-sim's pyro layout: 256 inputs and a fifth head, Special. Both or
    /// neither, as for the zoom and demo nets.
    /// </summary>
    [Fact]
    public void LoadsAPyroNetAndRefusesAHalfPyroLayout()
    {
        var pyro = SimPolicyNetwork.Load(WritePyro("pyro.gg2p", blast: true));

        Assert.True(pyro.IsPyro);
        Assert.True(pyro.IsTeam);
        Assert.False(pyro.IsDemo);
        Assert.False(pyro.IsZoom);
        Assert.Equal(256, pyro.InputSize);
        Assert.Equal([3, 2, 2, 99, 2], pyro.Heads.ToArray());

        Assert.Throws<InvalidDataException>(() => SimPolicyNetwork.Load(
            Write("pyro4.gg2p", SimPolicyObservation.PyroSize, SimPolicyController.TeamAimActions)));
    }

    /// <summary>The pyro net's fifth head is the Special key, as the demo net's is.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void APyroNetsFifthHeadIsTheSpecialKey(bool blast)
    {
        var options = HeadlessMatchOptions.Default with
        {
            Map = "ctf_truefort",
            Bots = 4,
            Seed = 7123,
            MaxTicks = 600,
            AllowedClasses = [PlayerClass.Pyro],
        };
        var match = HeadlessMatch.Load(options, MapDirectory.Resolve(), MapDirectory.ResolveNav());
        var pyro = new SimPolicyController(SimPolicyNetwork.Load(WritePyro(blast ? "blast.gg2p" : "calm.gg2p", blast)), [PlayerClass.Pyro]);
        var polled = 0;
        Span<int> nearest = stackalloc int[1];

        while (match.Outcome == MatchOutcome.Running && polled < 60)
        {
            match.Step();
            var view = new WorldView(in match.State, match.Collision);

            if (!view.IsAlive(1) || view.NearestEnemies(1, nearest) == 0)
                continue;

            Assert.Equal(blast, (pyro.Poll(in view, 1, match.Tick).Keys & PlayerKeys.Special) != 0);
            polled++;
        }

        Assert.Equal(60, polled);
    }

    /// <summary>
    /// The team projectile list takes an enemy's flare and not one's own; the
    /// pyro layout's three columns are ReadyToBlast, ReadyToFlare and burn/210.
    /// </summary>
    [Fact]
    public void TheTeamLayoutListsEnemyFlaresAndThePyroLayoutItsOwnState()
    {
        var options = HeadlessMatchOptions.Default with
        {
            Map = "ctf_truefort",
            Bots = 2,
            Seed = 7123,
            MaxTicks = 600,
            AllowedClasses = [PlayerClass.Pyro],
        };
        var match = HeadlessMatch.Load(options, MapDirectory.Resolve(), MapDirectory.ResolveNav());

        while (match.Outcome == MatchOutcome.Running && !(match.State.Alive[1] && match.State.Alive[2]))
            match.Step();

        ref var state = ref match.State;
        ref var me = ref state.Characters[1];
        state.Characters[2].Team = me.Team == Team.Red ? Team.Blue : Team.Red;
        me.BurnDuration = 105;
        state.Weapons[1].ReadyToBlast = true;
        state.Weapons[1].ReadyToFlare = false;

        var own = new ProjectileState { Kind = ProjectileKind.Flare, Team = me.Team, OwnerSlot = 1, Reflector = ProjectileState.NoOwner, X = me.X + 10, Y = me.Y };
        var theirs = own with { Team = state.Characters[2].Team, OwnerSlot = 2, X = me.X + 20 };
        state.SpawnProjectile(own);
        state.SpawnProjectile(theirs);

        var o = new float[SimPolicyObservation.PyroSize];
        var listed = new int[SimPolicyObservation.ListedEnemies];
        var view = new WorldView(in state, match.Collision);
        SimPolicyObservation.Team(in view, 1, o, listed, pyro: true);

        // The first projectile row (cols 88-94): the enemy flare, 20 px right,
        // not ours, an enemy's, present; the second row is empty.
        Assert.Equal(20f / 500, o[88]);
        Assert.Equal(0f, o[92]);
        Assert.Equal(1f, o[93]);
        Assert.Equal(1f, o[94]);
        Assert.Equal(0f, o[95 + 6]);

        Assert.Equal([1f, 0f, 0.5f], o[253..256]);
    }

    /// <summary>
    /// A zoom net driving a sniper holds the scope at its head's level, whatever
    /// the scripted layer wants.
    /// </summary>
    /// <remarks>
    /// The nets are pinned: the scope head's bias outweighs anything the hidden
    /// layer can sum to, so it always says the same level. From the second
    /// consecutive driven tick the scope must equal it, because the first tick's
    /// commands were polled before the hybrid took the wheel (IScopeController's
    /// one-tick lag). Scoped exercises the controller's toggle. Unscoped exercises
    /// the HybridBot dropping the scripted layer's own toggle, which would
    /// otherwise scope a sniper the policy is driving. Tested by removing that
    /// filter: it fails.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AZoomNetHoldsTheScopeWhileItDrives(bool scoped)
    {
        var net = SimPolicyNetwork.Load(WriteZoom(scoped ? "on.gg2p" : "off.gg2p", scoped));
        var brain = BotBrainSpec.Of(new SimPolicyMicroSpec(net, [PlayerClass.Sniper], "zoom")) with { Rule = HandoverRule.Distance };
        var options = HeadlessMatchOptions.Default with
        {
            Map = "ctf_truefort",
            Bots = 8,
            Seed = 7123,
            MaxTicks = 3600,
            AllowedClasses = [PlayerClass.Sniper],
            Brain = brain,
            HybridTeam = Team.Red,
        };
        var match = HeadlessMatch.Load(options, MapDirectory.Resolve(), MapDirectory.ResolveNav());
        var drove = new bool[WorldState.MaxSlots];
        var held = 0;

        while (match.Outcome == MatchOutcome.Running)
        {
            match.Step();

            for (var slot = 1; slot < WorldState.MaxSlots; slot++)
            {
                var driving = match.HybridBotFor(slot)?.Driving is SimPolicyController;

                if (driving && drove[slot] && match.State.IsAlive(slot))
                {
                    Assert.Equal(scoped, match.State.Characters[slot].Zoomed);
                    held++;
                }

                drove[slot] = driving;
            }
        }

        Assert.True(held > 0, "the zoom net never drove two ticks running");
    }

    /// <summary>
    /// <c>--bot-brain-team red</c>: only red's bots ever drive, and a bot that
    /// changes team changes brain with it, in both directions.
    /// </summary>
    /// <remarks>
    /// "Blue never drove" alone passes on a brain nobody wired up, so red has to
    /// drive too, before and after the swap. The swap is what a gate built at
    /// creation would fail: every bot is moved across, so after it the drivers
    /// must be exactly the bots that started blue.
    /// </remarks>
    [Fact]
    public void ABrainGatedToRedDrivesOnlyRedAndFollowsATeamChange()
    {
        ReferenceTree.RequireNavCache();

        var options = new ServerOptions
        {
            Port = 0,
            MapDirectory = MapDirectory.Resolve(),
            NavCacheDirectory = MapDirectory.ResolveNav(),
            Map = "ctf_truefort",
            BotMode = BotMode.Policy,
            PolicyPaths = [Write("kill.gg2p", SimPolicyObservation.TeamSize, SimPolicyController.TeamAimActions)],
            BotClassPolicies = [.. Enum.GetValues<PlayerClass>().Where(c => c != PlayerClass.Quote).Select(c => c + "=kill")],
            BotHandoverRule = HandoverRule.Distance,
            BotEngagesBotsToo = true,
            BotBrainTeam = Team.Red,
            Bots = BotPopulationOptions.Default with { Enabled = true, FillToPlayers = 8, MaxBots = 8, MinHumans = 0 },
        };
        BotBrain.Build(options);

        using var server = new GameServer(options, new RecordingLog());
        server.Start();

        var before = Drivers(server, 1800);
        var startedBlue = server.BotSlots.Where(slot => server.TeamOf(slot) == Team.Blue).ToHashSet();

        Assert.All(server.BotSlots, slot => Assert.NotNull(TeamGatedBot.Of(server.SourceOf(slot))));
        Assert.NotEmpty(before);
        Assert.All(before, d => Assert.Equal(Team.Red, d.Team));

        foreach (var slot in server.BotSlots.ToArray())
            server.ForceTeam(slot, server.TeamOf(slot) == Team.Red ? Team.Blue : Team.Red);

        var after = Drivers(server, 2700);

        Assert.NotEmpty(after);
        Assert.All(after, d => Assert.Equal(Team.Red, d.Team));
        Assert.All(after, d => Assert.Contains(d.Slot, startedBlue));
    }

    // Every (slot, team) that had a controller driving it on some tick.
    private static HashSet<(int Slot, Team Team)> Drivers(GameServer server, int ticks)
    {
        var drivers = new HashSet<(int, Team)>();

        for (var i = 0; i < ticks; i++)
        {
            server.Tick();

            foreach (var slot in server.BotSlots)
            {
                if (HybridBot.Of(server.SourceOf(slot)) is { MicroDrove: true })
                    drivers.Add((slot, server.TeamOf(slot)));
            }
        }

        return drivers;
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
    private string Write(string name, int inputs, int aim, bool zoomHead = false, int? zoomLevel = null)
    {
        var path = Path.Combine(_directory, name);
        var random = new Random(inputs + aim);
        int[] heads = zoomHead ? [3, 2, 2, aim, 2] : [3, 2, 2, aim];
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
            {
                // The last layer's biases come after its weights; the zoom head's
                // are its last two outputs. 1000 outweighs 16 tanh units at |w| <= 1.
                var zoomBias = i - (outputs * ins) - (outputs - 2);
                var pinned = zoomLevel is not null && ins == 16 && zoomBias is 0 or 1;
                writer.Write(pinned ? (zoomBias == zoomLevel ? 1000f : 0f) : (float)((random.NextDouble() * 2) - 1));
            }
        }

        return path;
    }

    // A demo-layout net whose Special head always says press, or always release.
    private string WriteDemo(string name, bool detonate) =>
        Write(name, SimPolicyObservation.DemoSize, SimPolicyController.TeamAimActions, zoomHead: true, zoomLevel: detonate ? 1 : 0);

    // A pyro-layout net whose Special head always says press, or always release.
    private string WritePyro(string name, bool blast) =>
        Write(name, SimPolicyObservation.PyroSize, SimPolicyController.TeamAimActions, zoomHead: true, zoomLevel: blast ? 1 : 0);

    // A zoom-layout net whose scope head always says scoped, or always unscoped.
    private string WriteZoom(string name, bool scoped) =>
        Write(name, SimPolicyObservation.ZoomSize, SimPolicyController.TeamAimActions, zoomHead: true, zoomLevel: scoped ? 1 : 0);
}
