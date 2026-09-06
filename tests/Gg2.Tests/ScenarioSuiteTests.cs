using Gg2.Bots;
using Gg2.Protocol;
using Gg2.Simulation;
using Xunit.Abstractions;

namespace Gg2.Tests;

/// <summary>
/// The fourteen behaviour scenarios, run for real against
/// <see cref="ScriptedBot"/>.
/// </summary>
/// <remarks>
/// <b>This is the measurement every later change to the bot layer gets compared
/// against</b>, which is why it exists before the objective layer rather than
/// after it. Each leg was written in the reference because a bot did something
/// wrong that nobody had noticed, and <see cref="Scenario.About"/> records what a
/// failure there would mean — so a red here is actionable in a way that a red in
/// a unit test about a follower branch is not.
/// <para>
/// <b>The numbers quoted in each <c>about</c> are the reference's, against the
/// GM8 bots, and they are a target rather than a contract.</b> This port is
/// expected to differ; where it misses one the question is whether the leg is
/// possible at all, not whether the tick count matches. As it happens it now
/// matches closely — <c>valley-shaft-floor-to-point</c> on 161 ticks against the
/// reference's quoted 161, <c>truefort-blue-spawn-exit-west</c> on 53 against 58
/// — which is corroboration rather than a requirement.
/// </para>
/// </remarks>
public sealed class ScenarioSuiteTests(ITestOutputHelper output)
{
    /// <summary>
    /// The legs this port does not complete. <b>It is empty, and keeping it
    /// empty is the point.</b>
    /// </summary>
    /// <remarks>
    /// All fourteen pass.
    /// <para>
    /// <b>Asserted as an exact set, in both directions.</b> A leg that starts
    /// failing is a regression; a leg that starts passing means an entry here is
    /// a lie and has to go, taking its diagnosis with it. A "known failures" list
    /// that is only ever checked one way decays into a list of things nobody
    /// looks at — this one is checked both ways and is currently a claim that
    /// there is nothing to look at.
    /// </para>
    /// </remarks>
    private static readonly Dictionary<string, string> Unmet = new(StringComparer.Ordinal);

    /// <summary>
    /// What each leg costs, and how many rocket jumps the suite flies.
    /// </summary>
    /// <remarks>
    /// <b>A leg's BUDGET is not a gate on its cost.</b> Every budget was set
    /// before the arcs were flown, so they are loose by design — a bot that lost
    /// its rocket jumps on a leg could regress substantially and stay comfortably
    /// inside a budget that still admits it, going entirely unnoticed. These
    /// numbers close that.
    /// <para>
    /// <b>Exact, because <see cref="ARunIsReproducible"/> says it can be.</b>
    /// Every draw is seeded, the graph comes off disk, and the physics is IEEE
    /// doubles — so a tick count that moves at all is a behaviour change and not
    /// noise. <b>A failure here is therefore not a broken test: it is the
    /// re-record.</b> Check first that the change is one you meant, then that it
    /// went the direction you meant, then update the row and say so in the
    /// commit — the same discipline the pinned <c>SIM_UUID</c> asks for.
    /// </para>
    /// <para>
    /// <b>Both directions.</b> A leg that got FASTER fails too, because a
    /// baseline nobody re-records is a baseline that stops describing the thing it
    /// names — the same reason <see cref="Unmet"/> is checked both ways.
    /// </para>
    /// </remarks>
    private static readonly Dictionary<string, int> Baseline = new(StringComparer.Ordinal)
    {
        ["valley-floor-to-point"] = 219,
        ["valley-spawn-to-point"] = 523,
        ["valley-point-to-spawn"] = 438,
        ["valley-shaft-crate-to-point"] = 95,
        ["valley-shaft-floor-to-point"] = 132,
        ["truefort-spawn-to-intel"] = 1568,
        ["truefort-blue-spawn-exit-west"] = 53,
        ["avanti-ramp-pocket-to-intel"] = 220,
        ["gallery-pit-climb"] = 216,
        ["soldier-shells-generator"] = 600,
        ["soldier-holds-and-fires"] = 400,
        ["sniper-fires-at-close-range"] = 400,
        ["atalia-ground-to-point"] = 62,
        ["oldfort-perch-reroutes"] = 1190,
    };

    /// <summary>
    /// How many rocket jumps the fourteen legs fly between them.
    /// </summary>
    /// <remarks>
    /// Pinned rather than <c>&gt; 0</c>: the search offering <em>one</em> arc
    /// where it used to offer six is a regression that a presence test cannot see,
    /// and the count is quoted as fact in `docs/TRAPS-BOTS.md`.
    /// </remarks>
    private const int BaselineRocketJumps = 10;

    private static ScenarioWorld Load(Scenario s) =>
        ScenarioWorld.Load(s.Map, s.Area, ReferenceTree.IncludedFiles, ReferenceTree.NavCacheDirectory);

    /// <summary>
    /// The bot a scenario is measured with.
    /// </summary>
    /// <remarks>
    /// <b>Top tier, and a fixed stagger.</b> A scenario asks whether a
    /// behaviour is possible; <see cref="BotSkill"/>'s lower tiers are deliberate
    /// handicaps — aim error, an acquire delay, a hold-fire roll — so measuring
    /// against one would be measuring the handicap. The stagger is fixed at 1
    /// rather than left at 0 because it is what a server gives the first
    /// non-reserved roster slot, and because a fixed one is what makes a run
    /// reproducible.
    /// </remarks>
    private static ScriptedBot Subject(Scenario s, ScenarioWorld world) =>
        new(world.Nav, new NavQuery(s.Team), BotSkill.ForTier(5), stagger: 1);

    private static ScenarioResult Run(Scenario s, out ScriptedBot bot)
    {
        var world = Load(s);
        bot = Subject(s, world);
        return ScenarioRunner.Run(s, world, bot);
    }

    /// <summary>Every leg runs, and exactly the recorded ones fail.</summary>
    [Fact]
    public void TheSuiteRunsAndExactlyTheRecordedLegsFail()
    {
        ReferenceTree.RequireNavCache();

        var failed = new SortedSet<string>(StringComparer.Ordinal);
        var costs = new Dictionary<string, int>(StringComparer.Ordinal);
        var rocketJumps = 0;

        foreach (var s in Scenarios.All)
        {
            var result = Run(s, out var bot);
            var ok = result.Satisfies(s, out var why);

            var how = s.Mode == ScenarioMode.Travel
                ? result.Arrived ? "arrived" : "short  "
                : "held   ";

            output.WriteLine(
                $"{(ok ? "ok  " : "FAIL")} {s.Name,-32} {how} "
                + $"t={result.Ticks,-5} short={result.GoalDistance,7:0.#} dmg={result.Damage,-5:0} "
                + $"acq={(result.Acquired ? "y" : "n")} moved={result.Moved,7:0.#} "
                + $"replan={bot.Path.Replans,-4} off={bot.Path.OffRouteFires,-4} "
                + $"stuck={bot.Path.StuckFires,-3} blacklist={bot.Path.BlacklistFires,-3} "
                + $"rj={bot.Path.RocketJumps,-3} {why}");

            rocketJumps += bot.Path.RocketJumps;
            costs[s.Name] = result.Ticks;

            if (!ok)
                failed.Add(s.Name);
        }

        // <b>Somebody rocket-jumped, and exactly as often as before.</b>
        // Nothing else here notices whether the search is offered those arcs at
        // all: with them switched off every leg still arrives, just more slowly,
        // and an arrival test cannot see the difference. Measured — the rocket
        // jump took avanti-ramp-pocket-to-intel from 311 ticks to 175 and
        // valley-floor-to-point from 251 to 178 — so a regression that quietly
        // stops offering them costs a third of the walking time on this suite and
        // nothing goes red without this line.
        Assert.Equal(BaselineRocketJumps, rocketJumps);

        // And what each leg COST, which the budgets do not gate — see Baseline.
        var drifted = costs
            .Where(c => Baseline[c.Key] != c.Value)
            .Select(c => $"{c.Key} {Baseline[c.Key]} -> {c.Value}")
            .ToArray();

        Assert.True(
            drifted.Length == 0,
            $"the suite's cost moved: {string.Join("; ", drifted)}. This is the re-record, not a "
            + "broken test — check the change is one you meant and went the way you meant, then "
            + "update Baseline and say so in the commit.");

        Assert.Equal(
            new SortedSet<string>(Baseline.Keys, StringComparer.Ordinal),
            new SortedSet<string>(costs.Keys, StringComparer.Ordinal));

        var expected = new SortedSet<string>(Unmet.Keys, StringComparer.Ordinal);

        // Both directions, and the message says which way it went. A newly
        // failing leg is a regression; a newly passing one means Unmet is now a
        // lie and the entry has to go, taking its diagnosis with it.
        var regressed = failed.Except(expected).ToArray();
        var passing = expected.Except(failed).ToArray();

        Assert.True(
            regressed.Length == 0,
            $"scenarios that used to pass now fail: {string.Join(", ", regressed)}");

        Assert.True(
            passing.Length == 0,
            $"scenarios recorded as unmet now PASS: {string.Join(", ", passing)}. Delete their "
            + "entries from Unmet — a known-failure list nobody prunes stops meaning anything.");
    }

    /// <summary>
    /// A run is reproducible to the last bit.
    /// </summary>
    /// <remarks>
    /// <b>This is what makes the recorded failure list mean anything at all.</b>
    /// A suite that answered differently run to run could not tell a regression
    /// from a coin landing the other way, and <c>gallery-pit-climb</c>'s
    /// <see cref="Scenario.Known"/> is the reference's own account of what that
    /// costs: it measured 2/5 and then 1/6 on one unchanged build, and was
    /// believed once at the price of a wrong revert.
    /// <para>
    /// It holds because everything that rolls is seeded: <c>WorldState.Random</c>
    /// starts at zero in a fresh world, and <c>ScriptedBot.Random</c> is the bot's
    /// own stream seeded from its stagger. A bot that drew from the match's
    /// stream would still be deterministic here — that mistake is invisible to
    /// this test, and <c>ScriptedBotTests</c> is what catches it.
    /// </para>
    /// </remarks>
    [Fact]
    public void ARunIsReproducible()
    {
        ReferenceTree.RequireNavCache();

        foreach (var s in Scenarios.All)
        {
            var first = Run(s, out _);
            var second = Run(s, out _);

            Assert.True(first == second, $"{s.Name} answered differently twice:\n  {first}\n  {second}");
        }
    }

    /// <summary>
    /// A travel leg measures the bot, not gravity.
    /// </summary>
    /// <remarks>
    /// <b>The control the whole suite rests on.</b> Every travel assertion is
    /// "did it get there", and a leg whose start and goal happened to be close
    /// enough — or whose subject simply fell into the goal — would pass with the
    /// bot deleted. So the same legs are run against a subject that presses
    /// nothing, and none of them may arrive.
    /// </remarks>
    [Fact]
    public void ATravelLegDoesNotArriveWithNobodyDrivingIt()
    {
        ReferenceTree.RequireNavCache();

        foreach (var s in Scenarios.All)
        {
            if (s.Mode != ScenarioMode.Travel)
                continue;

            var result = ScenarioRunner.Run(s, Load(s), subject: null);

            Assert.False(
                result.Arrived,
                $"{s.Name} arrived with nothing driving the character, so it asserts nothing "
                + $"about the bot: it finished {result.GoalDistance:0.#}px from the goal after "
                + $"{result.Ticks} ticks having moved {result.Moved:0.#}px.");
        }
    }

    /// <summary>
    /// Every travel leg actually hands its bot the destination.
    /// </summary>
    /// <remarks>
    /// <b>The silent failure this suite is most exposed to.</b> If
    /// <see cref="ScriptedBot"/> ever stopped implementing
    /// <see cref="IScenarioSubject"/> nothing would fail to compile: every travel
    /// leg would simply stand still and report "did not arrive", and the reader
    /// would go looking at the follower for a defect in the harness. So the fact
    /// is asserted directly rather than left to be inferred from eight legs going
    /// red at once.
    /// <para>
    /// The control below is the other half: a subject that takes no destination
    /// is <b>not</b> an error — that was tried and it broke the synthetic travel
    /// scenario in <c>ScenarioWorldTests</c>, which exists to exercise this runner
    /// without a bot. It reports <c>Steered</c> false and says so in its failure
    /// text.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryTravelLegHandsItsBotTheDestination()
    {
        ReferenceTree.RequireNavCache();

        foreach (var s in Scenarios.All)
        {
            if (s.Mode != ScenarioMode.Travel)
                continue;

            var result = Run(s, out var bot);

            Assert.True(result.Steered, $"{s.Name}: the bot was never given a destination");
            Assert.True(result.Planned, $"{s.Name}: the destination resolved to no route");
            Assert.True(bot.Path.HasGoal || result.Arrived, $"{s.Name}: the bot holds no goal");
        }

        // The control: a subject with no goal seam is accepted, reports
        // Steered false, and its failure text says so — because a source that
        // holds one key cannot be misled by not being told where to go.
        var travel = Scenarios.All.First(x => x.Mode == ScenarioMode.Travel);
        var mute = ScenarioRunner.Run(travel, Load(travel), new Mute());

        Assert.False(mute.Steered);
        Assert.False(mute.Satisfies(travel, out var why));
        Assert.Contains("takes no destination", why!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The generator scenario measures the generator.
    /// </summary>
    /// <remarks>
    /// <b>It places no enemy characters at all</b>, so every point of its damage
    /// figure is the generator's — which means that without <c>Generators.Place</c>
    /// in <see cref="ScenarioWorld"/> and the generator term in the runner's damage
    /// sum, this leg reports 0 for ever and its <c>MinDamage</c> of 50 is
    /// unreachable however well the bot shoots. The scenario exists to catch a
    /// regression whose symptom is silence: bots had never damaged a generator at
    /// all, and a round on <c>gen_destroy</c> simply never ended.
    /// </remarks>
    [Fact]
    public void TheGeneratorLegMeasuresTheGeneratorAndNothingElse()
    {
        ReferenceTree.RequireNavCache();

        var s = Scenarios.ByName("soldier-shells-generator")!;
        Assert.Empty(s.Enemies);

        var world = Load(s);

        Assert.True(
            Generators.Of(ref world.State, Team.Blue).Exists,
            "gen_destroy's blue generator was not placed in the scenario world");

        var startHp = Generators.Of(ref world.State, Team.Blue).Hp;
        var result = ScenarioRunner.Run(s, world, Subject(s, world));

        Assert.True(result.Acquired, "the bot never acquired the generator");
        Assert.True(
            result.Damage > 0,
            "the run measured no damage, and with no enemy characters that means the "
            + "generator was not counted");

        Assert.Equal(startHp - Generators.Of(ref world.State, Team.Blue).Hp, result.Damage);
    }

    /// <summary>
    /// Acquisition is latched across the run, not read at the end.
    /// </summary>
    /// <remarks>
    /// <b>A target clears when it dies</b>, so at the last tick a bot that
    /// fought and won looks exactly like one that never saw anything. This is the
    /// control for that: <c>soldier-holds-and-fires</c> kills its dummy outright —
    /// all 200 of a heavy's hit points — and the run must still report
    /// <c>Acquired</c> while the bot itself holds no target by the end.
    /// </remarks>
    [Fact]
    public void AcquisitionSurvivesKillingTheThingThatWasAcquired()
    {
        ReferenceTree.RequireNavCache();

        var s = Scenarios.ByName("soldier-holds-and-fires")!;
        var result = Run(s, out var bot);

        Assert.True(result.Damage >= 200, $"the dummy survived on {200 - result.Damage} hp");
        Assert.False(bot.HasTarget, "the dummy is dead and the bot still holds a target");
        Assert.True(result.Acquired, "the run reported no acquisition, so it is reading the last tick");
    }

    /// <summary>Presses nothing, for ever, and cannot be given a goal.</summary>
    private sealed class Mute : IInputSource
    {
        public PlayerInput Poll(in WorldView world, int slot, int tick) => PlayerInput.None;
    }
}
