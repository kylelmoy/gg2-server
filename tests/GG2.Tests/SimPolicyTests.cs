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
    /// gg2-sim 4a8d9ca's medic layout: 259 inputs and a fifth head, Special.
    /// Both or neither, as for the other five-head nets.
    /// </summary>
    [Fact]
    public void LoadsAMedicNetAndRefusesAHalfMedicLayout()
    {
        var medic = SimPolicyNetwork.Load(WriteMedic("medic.gg2p", needles: true));

        Assert.True(medic.IsMedic);
        Assert.True(medic.IsTeam);
        Assert.False(medic.IsPyro);
        Assert.Equal(259, medic.InputSize);
        Assert.Equal([3, 2, 2, 99, 2], medic.Heads.ToArray());

        Assert.Throws<InvalidDataException>(() => SimPolicyNetwork.Load(
            Write("medic4.gg2p", SimPolicyObservation.MedicSize, SimPolicyController.TeamAimActions)));
    }

    /// <summary>
    /// The medic layout's six columns: uber/2000, ready, ubering, beaming, the
    /// patient's hp share, and the patient's index among the listed team-mates.
    /// The ally aim's decode is bit-exact against gg2-sim's ref by the
    /// cross-check outside this suite (docs/BOTS.md).
    /// </summary>
    [Fact]
    public void TheMedicLayoutSeesItsUberAndItsPatient()
    {
        var options = HeadlessMatchOptions.Default with
        {
            Map = "ctf_truefort",
            Bots = 4,
            Seed = 7123,
            MaxTicks = 600,
            AllowedClasses = [PlayerClass.Medic],
        };
        var match = HeadlessMatch.Load(options, MapDirectory.Resolve(), MapDirectory.ResolveNav());
        var o = new float[SimPolicyObservation.MedicSize];
        var listed = new int[SimPolicyObservation.ListedEnemies];
        var allies = new int[SimPolicyObservation.ListedAllies];

        while (match.Outcome == MatchOutcome.Running)
        {
            match.Step();
            var view = new WorldView(in match.State, match.Collision);

            if (view.IsAlive(1))
            {
                SimPolicyObservation.Team(in view, 1, o, listed, medic: true, listedAllies: allies);

                if (allies[0] >= 0)
                    break;
            }
        }

        ref var state = ref match.State;
        var patient = allies[0];
        state.Weapons[1].HealTarget = (byte)patient;
        state.Weapons[1].UberCharge = 500;
        state.Weapons[1].UberReady = false;
        state.Weapons[1].Ubering = true;
        state.Characters[patient].Hp = ClassStats.For(state.Characters[patient].Class).MaxHp / 2;

        var beamed = new WorldView(in state, match.Collision);
        SimPolicyObservation.Team(in beamed, 1, o, listed, medic: true, listedAllies: allies);
        Assert.Equal([0.25f, 0f, 1f, 1f, 0.5f, 0f], o[253..259]);

        state.Weapons[1].HealTarget = WeaponState.NoTarget;
        var idle = new WorldView(in state, match.Collision);
        SimPolicyObservation.Team(in idle, 1, o, listed, medic: true, listedAllies: allies);
        Assert.Equal([0f, 0f, -1f], o[256..259]);
    }

    /// <summary>
    /// gg2-sim's base layout v2: every team layout ten wider, told apart by width
    /// alone, and each still the same layout. A v2 width with the wrong head count
    /// is refused as its v1 width is.
    /// </summary>
    [Theory]
    [InlineData(SimPolicyObservation.TeamSize, false)]
    [InlineData(SimPolicyObservation.ZoomSize, true)]
    [InlineData(SimPolicyObservation.DemoSize, true)]
    [InlineData(SimPolicyObservation.PyroSize, true)]
    [InlineData(SimPolicyObservation.MedicSize, true)]
    [InlineData(SimPolicyObservation.EngineerSize, true)]
    [InlineData(SimPolicyObservation.SpySize, true)]
    public void LoadsAV2NetOfEveryLayout(int v1Width, bool fiveHeads)
    {
        var v1 = SimPolicyNetwork.Load(Write("v1.gg2p", v1Width, SimPolicyController.TeamAimActions, zoomHead: fiveHeads));
        var v2 = SimPolicyNetwork.Load(
            Write("v2.gg2p", v1Width + SimPolicyObservation.SentryColumns, SimPolicyController.TeamAimActions, zoomHead: fiveHeads));

        Assert.False(v1.IsV2);
        Assert.True(v2.IsV2);
        Assert.True(v2.IsTeam);
        Assert.Equal(
            (v1.IsZoom, v1.IsDemo, v1.IsPyro, v1.IsMedic, v1.IsEngineer, v1.IsSpy),
            (v2.IsZoom, v2.IsDemo, v2.IsPyro, v2.IsMedic, v2.IsEngineer, v2.IsSpy));
        Assert.Throws<InvalidDataException>(() => SimPolicyNetwork.Load(Write(
            "half.gg2p", v1Width + SimPolicyObservation.SentryColumns, SimPolicyController.TeamAimActions, zoomHead: !fiveHeads)));
    }

    /// <summary>
    /// With no hidden enemy and no sentry a v2 row is its v1 row plus ten zeros,
    /// on every tick of a soldier match, which is what gg2-sim's env.h promises.
    /// </summary>
    [Fact]
    public void AV2RowIsTheV1RowPlusTenZerosWithNoSpyOrSentry()
    {
        var options = HeadlessMatchOptions.Default with
        {
            Map = "ctf_truefort",
            Bots = 8,
            Seed = 7123,
            MaxTicks = 900,
            AllowedClasses = [PlayerClass.Soldier],
        };
        var match = HeadlessMatch.Load(options, MapDirectory.Resolve(), MapDirectory.ResolveNav());
        var memory = new SimPolicyMemory();
        var v1 = new float[SimPolicyObservation.TeamSize];
        var v2 = new float[SimPolicyObservation.TeamSize + SimPolicyObservation.SentryColumns];
        int[] listed1 = new int[3], listed2 = new int[3];
        var compared = 0;

        while (match.Outcome == MatchOutcome.Running)
        {
            match.Step();
            var view = new WorldView(in match.State, match.Collision);

            if (!view.IsAlive(1))
                continue;

            SimPolicyObservation.Team(in view, 1, v1, listed1);
            SimPolicyObservation.Team(in view, 1, v2, listed2, memory: memory, tick: match.Tick);
            Assert.Equal(v1, v2[..SimPolicyObservation.TeamSize]);
            Assert.All(v2[SimPolicyObservation.TeamSize..], c => Assert.Equal(0f, c));
            Assert.Equal(listed1, listed2);

            if (listed1[0] >= 0)
                compared++;
        }

        Assert.True(compared > 100, $"an enemy was listed on only {compared} ticks, so this gates little");
    }

    /// <summary>
    /// The sentry block: the two nearest sentries of either team, nearest first,
    /// dx/500, dy/500, hp/100, enemy, present; a third further one is not listed.
    /// </summary>
    [Fact]
    public void TheSentryBlockListsTheTwoNearestSentries()
    {
        var match = SoldiersInView(out var me, out var enemy);
        ref var state = ref match.State;
        var mate = -1;

        for (var s = 1; s < WorldState.MaxSlots && mate < 0; s++)
        {
            if (s != me && state.Alive[s] && state.Characters[s].Team == state.Characters[me].Team)
                mate = s;
        }

        Assert.True(mate > 0, "no team-mate to own the second sentry");
        double x = state.Characters[me].X, y = state.Characters[me].Y;
        Sentries.Build(ref state, enemy, x + 50, y, 1);
        Sentries.Build(ref state, mate, x - 100, y + 25, 1);
        Sentries.Build(ref state, me, x + 400, y, 1);
        state.Sentries[mate].Hp = 50;

        var o = new float[SimPolicyObservation.TeamSize + SimPolicyObservation.SentryColumns];
        var view = new WorldView(in state, match.Collision);
        SimPolicyObservation.Team(in view, me, o, new int[3], memory: new SimPolicyMemory(), tick: match.Tick);

        var block = o[SimPolicyObservation.TeamSize..];
        Assert.Equal([0.1f, 0f, (float)(state.Sentries[enemy].Hp / SentryState.MaxHp), 1f, 1f], block[..5]);
        Assert.Equal([-0.2f, 0.05f, 0.5f, 0f, 1f], block[5..]);
    }

    /// <summary>
    /// A cloaked enemy is listed from the body's last sighting: there, still, at
    /// its hp then, present fading as 1 - age/60, and gone at 60 ticks. The
    /// relative aim at it points at the remembered spot.
    /// </summary>
    [Fact]
    public void ACloakedEnemyIsListedFromItsLastSighting()
    {
        var match = SoldiersInView(out var me, out var enemy);
        ref var state = ref match.State;
        var memory = new SimPolicyMemory();
        var o = new float[SimPolicyObservation.TeamSize + SimPolicyObservation.SentryColumns];
        var listed = new int[3];
        var tick = 300;

        var seen = new WorldView(in state, match.Collision);
        SimPolicyObservation.Team(in seen, me, o, listed, memory: memory, tick: tick);
        var row = 8 + (16 * Array.IndexOf(listed, enemy));
        Assert.True(row >= 8, "the enemy was not listed while visible");
        double sightedX = state.Characters[enemy].X, sightedY = state.Characters[enemy].Y, sightedHp = state.Characters[enemy].Hp;

        state.Characters[enemy].Cloak = true;
        state.Characters[enemy].X += 30;
        state.Characters[enemy].HSpeed = 5;
        state.Characters[enemy].Hp = sightedHp / 2;
        var hidden = new WorldView(in state, match.Collision);
        SimPolicyObservation.Team(in hidden, me, o, listed, memory: memory, tick: tick + 30);

        var at = Array.IndexOf(listed, enemy);
        Assert.True(at >= 0, "a cloaked enemy seen 30 ticks ago is listed");
        row = 8 + (16 * at);
        Assert.Equal((float)((sightedX - state.Characters[me].X) / 500), o[row]);
        Assert.Equal(0f, o[row + 2]);
        Assert.Equal((float)(sightedHp / ClassStats.For(state.Characters[enemy].Class).MaxHp), o[row + 4]);
        Assert.Equal(0.5f, o[row + 5]);

        // The relative aim's centre bin at that group points at the sighting, not the spy.
        int[] action = [0, 0, 0, SimPolicyController.AbsoluteAimBins + (at * SimPolicyController.RelativeAimBins) + 12, 0];
        var aim = SimPolicyController.TeamInput(in hidden, action, me, listed, special: false, listedAllies: null, memory);
        ref readonly var self = ref state.Characters[me];
        var expected = (ushort)((uint)(Gml.PointDirection(self.X, self.Y, sightedX, sightedY) * 65536.0 / 360.0) & 0xFFFF);
        Assert.Equal(expected, aim.NetAimDirection);

        SimPolicyObservation.Team(in hidden, me, o, listed, memory: memory, tick: tick + SimPolicyMemory.Ticks);
        Assert.DoesNotContain(enemy, listed);
    }

    /// <summary>
    /// gg2-sim's terrain layout (the duel nets) is told apart by width alone, on
    /// top of any team layout and either version.
    /// </summary>
    [Fact]
    public void LoadsTerrainNetsOnEveryTeamLayout()
    {
        Assert.Equal(176, SimPolicyObservation.TerrainColumns);

        var soldier = SimPolicyNetwork.Load(Write(
            "t-soldier.gg2p", SimPolicyObservation.TeamSize + SimPolicyObservation.SentryColumns + SimPolicyObservation.TerrainColumns,
            SimPolicyController.TeamAimActions));
        var v1 = SimPolicyNetwork.Load(Write(
            "t-v1.gg2p", SimPolicyObservation.TeamSize + SimPolicyObservation.TerrainColumns, SimPolicyController.TeamAimActions));
        var spy = SimPolicyNetwork.Load(Write(
            "t-spy.gg2p", SimPolicyObservation.SpySize + SimPolicyObservation.SentryColumns + SimPolicyObservation.TerrainColumns,
            SimPolicyController.TeamAimActions, zoomHead: true));
        var plain = SimPolicyNetwork.Load(Write(
            "plain.gg2p", SimPolicyObservation.TeamSize + SimPolicyObservation.SentryColumns, SimPolicyController.TeamAimActions));

        Assert.Equal(439, soldier.InputSize);
        Assert.True(soldier is { IsTerrain: true, IsV2: true, IsTeam: true });
        Assert.True(v1 is { IsTerrain: true, IsV2: false, IsTeam: true });
        Assert.True(spy is { IsTerrain: true, IsV2: true, IsSpy: true, FifthIsSpecial: true });
        Assert.False(plain.IsTerrain);
    }

    /// <summary>
    /// A terrain row zeroes its position, and its route columns are hops on the
    /// tables the body routes with: present with them, absent without.
    /// </summary>
    /// <remarks>
    /// The values themselves are checked against gg2-sim's ref outside the suite
    /// (<c>docs/BOTS.md</c>, "gg2-sim policies"); this holds the wiring.
    /// </remarks>
    [Fact]
    public void ATerrainRowRoutesOnTheBodysTables()
    {
        var match = SoldiersInView(out var me, out _);
        var view = new WorldView(in match.State, match.Collision);
        var graph = match.Nav!;
        var tables = match.HybridBotFor(me)?.ScriptedBot.Distances ?? NavDistanceSet.Derive(graph);
        var width = SimPolicyObservation.TeamSize + SimPolicyObservation.SentryColumns + SimPolicyObservation.TerrainColumns;
        var routed = new float[width];
        var unrouted = new float[width];
        var listed = new int[3];

        SimPolicyObservation.Team(in view, me, routed, listed, memory: new SimPolicyMemory(), terrain: true, graph: graph, tables: tables);
        SimPolicyObservation.Team(in view, me, unrouted, listed, memory: new SimPolicyMemory(), terrain: true, graph: graph);

        Assert.Equal(0f, routed[0]);
        Assert.Equal(0f, routed[1]);

        // Some block around a body standing in a map is solid.
        var blocks = routed.AsSpan(width - SimPolicyObservation.TerrainColumns, 165).ToArray();
        Assert.Contains(blocks, share => share > 0);

        var route = width - 8;
        Assert.True(routed[route + 2] == 1 || routed[route + 7] == 1, "neither a first hop nor the same node");
        Assert.Equal(0f, unrouted[route + 2]);
        Assert.Equal(routed[route + 6], unrouted[route + 6]);
    }

    /// <summary>
    /// Sampled, a decision is drawn from each head's softmax with the bot's own
    /// stream: it varies on a fixed observation where greedy cannot, and the same
    /// seed repeats it.
    /// </summary>
    [Fact]
    public void ASampledDecisionVariesAndRepeatsWithItsSeed()
    {
        var net = SimPolicyNetwork.Load(Write("s.gg2p", SimPolicyObservation.TeamSize, SimPolicyController.TeamAimActions));
        var observation = new float[net.InputSize];

        for (var i = 0; i < observation.Length; i++)
            observation[i] = (i % 7) / 7f;

        var a = new float[net.Widest];
        var b = new float[net.Widest];
        var greedy = new HashSet<int>();
        var first = new List<int>();
        var second = new List<int>();
        var one = new Random(11);
        var two = new Random(11);
        var action = new int[net.Heads.Length];

        for (var n = 0; n < 200; n++)
        {
            net.Decide(observation, a, b, action);
            greedy.Add(action[3]);
            net.Decide(observation, a, b, action, one);
            first.Add(action[3]);
            net.Decide(observation, a, b, action, two);
            second.Add(action[3]);
        }

        Assert.Single(greedy);
        Assert.True(first.Distinct().Count() > 1, "sampling never left one aim bin");
        Assert.Equal(first, second);
    }

    [Fact]
    public void PolicySampledReachesEveryGg2pTier()
    {
        var path = Write("kill.gg2p", SimPolicyObservation.TeamSize, SimPolicyController.TeamAimActions);
        string[] args = ["--bot-mode", "policy", "--policy", path, "--bot-class-policy", "Soldier=kill"];

        var greedy = Assert.IsType<SimPolicyMicroSpec>(Assert.Single(CommandLine.Parse(args).Bots.Brain!.Tiers));
        var sampled = Assert.IsType<SimPolicyMicroSpec>(Assert.Single(CommandLine.Parse([.. args, "--bot-policy-sampled"]).Bots.Brain!.Tiers));

        Assert.False(greedy.Sampled);
        Assert.True(sampled.Sampled);
        Assert.True(Assert.IsType<SimPolicyController>(sampled.Create()).Sampled);
    }

    /// <summary>
    /// A terrain net drives in a match, sampled, and the hybrid hands it the
    /// graph its scripted layer routes on (IRouteController). Without that
    /// hand-over its route columns would be absent on every decision.
    /// </summary>
    [Fact]
    public void AHybridBotHandsATerrainNetItsRoute()
    {
        var net = SimPolicyNetwork.Load(Write(
            "t.gg2p", SimPolicyObservation.TeamSize + SimPolicyObservation.SentryColumns + SimPolicyObservation.TerrainColumns,
            SimPolicyController.TeamAimActions));
        var brain = BotBrainSpec.Of(new SimPolicyMicroSpec(net, [PlayerClass.Soldier], "t", Sampled: true)) with
        {
            Rule = HandoverRule.Distance,
        };
        var options = HeadlessMatchOptions.Default with
        {
            Map = "ctf_truefort",
            Bots = 4,
            Seed = 7123,
            MaxTicks = 900,
            AllowedClasses = [PlayerClass.Soldier],
            Brain = brain,
            HybridTeam = Team.Red,
        };
        var match = HeadlessMatch.Load(options, MapDirectory.Resolve(), MapDirectory.ResolveNav());
        var driven = 0;
        var routed = 0;

        while (match.Outcome == MatchOutcome.Running)
        {
            match.Step();

            for (var slot = 1; slot < WorldState.MaxSlots; slot++)
            {
                if (match.HybridBotFor(slot) is not { } hybrid)
                    continue;

                var tier = Assert.IsType<SimPolicyController>(Assert.Single(hybrid.Tiers));

                if (ReferenceEquals(tier.RouteGraph, match.Nav))
                    routed++;

                if (hybrid.Driving == tier)
                    driven++;
            }
        }

        Assert.True(driven > 0, "the terrain net never drove");
        Assert.True(routed > 0, "no tier was handed the match's graph");
    }

    // A soldier match stepped until slot `me` has a live enemy within 300 px.
    private static HeadlessMatch SoldiersInView(out int me, out int enemy)
    {
        var options = HeadlessMatchOptions.Default with
        {
            Map = "ctf_truefort",
            Bots = 8,
            Seed = 7123,
            MaxTicks = 9000,
            AllowedClasses = [PlayerClass.Soldier],
        };
        var match = HeadlessMatch.Load(options, MapDirectory.Resolve(), MapDirectory.ResolveNav());
        Span<int> one = stackalloc int[1];

        while (match.Outcome == MatchOutcome.Running)
        {
            match.Step();
            var view = new WorldView(in match.State, match.Collision);

            for (var s = 1; s < match.SlotCount; s++)
            {
                if (!view.IsAlive(s))
                    continue;

                if (SimPolicyObservation.Nearest(in view, s, mates: false, one) == 0)
                    continue;

                ref readonly var a = ref view.Character(s);
                ref readonly var b = ref view.Character(one[0]);

                if (Gml.PointDistance(a.X, a.Y, b.X, b.Y) < 300)
                {
                    me = s;
                    enemy = one[0];
                    return match;
                }
            }
        }

        throw new InvalidOperationException("no two soldiers met");
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

    // A medic-layout net whose Special head always says press, or always release.
    private string WriteMedic(string name, bool needles) =>
        Write(name, SimPolicyObservation.MedicSize, SimPolicyController.TeamAimActions, zoomHead: true, zoomLevel: needles ? 1 : 0);

    // A zoom-layout net whose scope head always says scoped, or always unscoped.
    private string WriteZoom(string name, bool scoped) =>
        Write(name, SimPolicyObservation.ZoomSize, SimPolicyController.TeamAimActions, zoomHead: true, zoomLevel: scoped ? 1 : 0);
}
