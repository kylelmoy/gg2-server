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
/// ⚠️ <b>The numbers quoted in each <c>about</c> are the reference's, against the
/// GM8 bots, and they are a target rather than a contract.</b> This port is
/// expected to differ; where it misses one the question is whether the leg is
/// possible at all, not whether the tick count matches.
/// </para>
/// </remarks>
public sealed class ScenarioSuiteTests(ITestOutputHelper output)
{
    /// <summary>
    /// The legs this port does not complete, and what is known about each.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Asserted as an exact set, in both directions.</b> A leg that starts
    /// failing is a regression; a leg that starts passing means somebody fixed
    /// something and this list is now a lie, which is the state a "known
    /// failures" list decays into if it is only ever checked one way.
    /// <para>
    /// ⚠️ <b>Five of the six look like one defect</b>, diagnosed on
    /// <c>truefort-blue-spawn-exit-west</c> because it is the shortest — a 150px
    /// leg the reference walks in 58 ticks. The route is <em>right</em> (the
    /// direct 33.67 arc, node 178 to 177, takeoff column 588 at world x 3540);
    /// the takeoff gate refuses it every time. The bot's run-up is a <b>round
    /// trip</b>: it walks six cells right to the mark, arrives there at
    /// +3.13 px/tick heading <em>away</em> from the jump, spends seven ticks
    /// reversing, and is back at the takeoff column at <b>0.35 px/tick</b> — so
    /// every attempt is made from a standstill and the cycle repeats identically
    /// every <b>22 ticks</b> until the anti-thrash blacklist fires around t140 and
    /// sends it down a hole it cannot climb out of.
    /// </para>
    /// <para>
    /// ⚠️ <c>BOT_TAKEOFF_PATIENCE</c> cannot rescue it, and this is the first time
    /// that has been observed mattering. <c>TakeoffTicks</c> peaked at <b>4</b>
    /// over the whole run: every re-plan zeroes it and at this stagger the
    /// re-plan interval is 46, which is the ceiling CLAUDE.md records under item
    /// 4b-ii. It was found there by a test failing and reasoned about; here it is
    /// costing a real leg.
    /// </para>
    /// </remarks>
    private static readonly Dictionary<string, string> Unmet = new(StringComparer.Ordinal)
    {
        ["valley-shaft-floor-to-point"] =
            "arrives, but at 478 ticks against a 400 cap and the reference's 161. The only "
            + "one of the six that completes at all, which makes it the cheapest A/B signal "
            + "here — a takeoff fix should move this number first.",

        ["truefort-spawn-to-intel"] =
            "737px short after the full 6,000. The reference does it in 2,200-2,500. It is "
            + "the only leg covering two stacked jumps in a row, so it is the run-up defect "
            + "compounded.",

        ["truefort-blue-spawn-exit-west"] =
            "834px short on a 150px leg the reference walks in 58 ticks. The diagnosed case "
            + "— see the remarks above. ⚠️ Fix this one and the other four are worth "
            + "re-running before anything else is attempted.",

        ["avanti-ramp-pocket-to-intel"] =
            "958px short. The reference does it in 352-438. Its start node stands one anchor "
            + "column from the 54px block that is the way out, so the takeoff has no room at "
            + "all — the same gate in its harshest form.",

        ["gallery-pit-climb"] =
            "497px short. ⚠️ Do NOT use this as an A/B signal even though it is deterministic "
            + "here: Scenario.Known records that it measured 2/5 and then 1/6 in the "
            + "reference on one unchanged build, and was believed once at the price of a "
            + "wrong revert. It is only good for 'the takeoff machinery is broken outright'.",

        ["oldfort-perch-reroutes"] =
            "2,253px short with 479 off-route events, by far the most of any leg. ⚠️ This one "
            + "may not be the same defect: the scenario exists to make a bot DECLINE a lying "
            + "arc, and 479 misses is the signature of taking one. Worth its own look rather "
            + "than assuming the run-up explains it.",
    };

    private static ScenarioWorld Load(Scenario s) =>
        ScenarioWorld.Load(s.Map, s.Area, ReferenceTree.IncludedFiles, ReferenceTree.NavCacheDirectory);

    /// <summary>
    /// The bot a scenario is measured with.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Top tier, and a fixed stagger.</b> A scenario asks whether a
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
                + $"stuck={bot.Path.StuckFires,-3} blacklist={bot.Path.BlacklistFires,-3} {why}");

            if (!ok)
                failed.Add(s.Name);
        }

        var expected = new SortedSet<string>(Unmet.Keys, StringComparer.Ordinal);

        // ⚠️ Both directions, and the message says which way it went. A newly
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
    /// ⚠️ <b>This is what makes the recorded failure list mean anything at all.</b>
    /// A suite that answered differently run to run could not tell a regression
    /// from a coin landing the other way, and <c>gallery-pit-climb</c>'s
    /// <see cref="Scenario.Known"/> is the reference's own account of what that
    /// costs: it measured 2/5 and then 1/6 on one unchanged build, and was
    /// believed once at the price of a wrong revert.
    /// <para>
    /// It holds because everything that rolls is seeded: <c>WorldState.Random</c>
    /// starts at zero in a fresh world, and <c>ScriptedBot.Random</c> is the bot's
    /// own stream seeded from its stagger. ⚠️ A bot that drew from the match's
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
    /// ⚠️ <b>The control the whole suite rests on.</b> Every travel assertion is
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
    /// ⚠️ <b>The silent failure this suite is most exposed to.</b> If
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

        // ⚠️ The control: a subject with no goal seam is accepted, reports
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
    /// ⚠️ <b>It places no enemy characters at all</b>, so every point of its damage
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
    /// ⚠️ <b>A target clears when it dies</b>, so at the last tick a bot that
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
