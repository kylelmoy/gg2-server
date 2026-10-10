using System.Collections.Concurrent;
using System.Globalization;
using GG2.Bots;
using GG2.Protocol;
using GG2.Simulation;

namespace Bench;

/// <summary>
/// How much weaker each difficulty tier is, against a tier-5 opponent running the
/// same brain — <c>BENCH_TIERDUEL</c> and <c>BENCH_TIERS</c>.
/// </summary>
/// <remarks>
/// <b>Two instruments for two questions.</b> The duel puts nearly every tick in a
/// fight, so it is the one with the power to tune the controller's handicap
/// (<see cref="HybridBot.HandicapSkill"/>). The match is the one that contains what
/// a player meets — a second enemy, an objective, the scripted layer's share of the
/// game — and so is the one that says whether the tiers still rank in play.
/// <para>
/// <b>Arms.</b> <c>full:t</c> plays the subject's whole bot at tier t;
/// <c>hc:t</c> plays its scripted layer at tier 5 and only the controller's
/// handicap at t. <c>full:5</c> is the mirror and the CONTROL: it has to read
/// 0.5, or the harness has a side or slot bias and nothing else in the table
/// means anything.
/// </para>
/// <para>
/// <b>Paired.</b> Observation i is the same seed, map, class and side in every
/// arm, so an arm's difference from <c>full:5</c> is taken per observation and its
/// interval is far tighter than the gap between two unpaired means. The subject's
/// side alternates with i.
/// </para>
/// <para>
/// <b>The statistic has no subset.</b> The duel scores every duel (a draw is
/// half); the match scores the subject team's share of the match's deaths, with
/// a zero-death match scored 0.5, and each match is one observation —
/// <c>docs/BOTS.md</c>, "The statistic".
/// </para>
/// </remarks>
public static class Tiers
{
    /// <summary>The classes the two shipped checkpoints drive. A mirror cycles them.</summary>
    private static readonly PlayerClass[] Classes =
        [PlayerClass.Scout, PlayerClass.Soldier, PlayerClass.Demoman, PlayerClass.Heavy, PlayerClass.Pyro];

    private static readonly (string Name, int Tier, bool HandicapOnly)[] ArmList =
    [
        ("full:5", 5, false), ("full:4", 4, false), ("full:3", 3, false), ("full:2", 2, false), ("full:1", 1, false),
        ("hc:4", 4, true), ("hc:3", 3, true), ("hc:2", 2, true), ("hc:1", 1, true),

        // Tier 5 wrapped in HumanizedBot: what a human-like bot costs, against
        // the same full:5 mirror every other arm is read against.
        ("hum:5", 5, false),

        // Tier 5 with each policy turn spread over its action repeat
        // (PolicyController.SpreadTurn): gg2-rl phase 5, C0.
        ("spread:5", 5, false),

        // Tier 5 wrapped in HumanizedBot's v5 preset, lazy between shots
        // (HumanizeOptions.Lazy): gg2-rl phase 5, v5.
        ("lazy:5", 5, false),

        // Tier 5 wrapped in HumanizedBot's D2 preset, the aim held while there is
        // no target (HumanizeOptions.Hold): gg2-rl phase 5, D2.
        ("hold:5", 5, false),
    ];

    private static bool Humanized(string arm) =>
        arm.StartsWith("hum:", StringComparison.Ordinal) || Lazy(arm) || arm.StartsWith("hold:", StringComparison.Ordinal);

    private static bool Lazy(string arm) => arm.StartsWith("lazy:", StringComparison.Ordinal);

    private static HumanizeOptions HumanizeFor(string arm) =>
        Lazy(arm) ? HumanizeOptions.Lazy
        : arm.StartsWith("hold:", StringComparison.Ordinal) ? HumanizeOptions.Hold
        : HumanizeOptions.Default;

    private static bool Spread(string arm) => arm.StartsWith("spread:", StringComparison.Ordinal);

    private sealed record Observation(
        string Map, string Arm, int Index, string Class, Team Subject, double Score, int Ticks,
        long SubjectMicroTicks, string Detail);

    /// <summary>
    /// The brain the live server builds from the two shipped checkpoints, one copy
    /// per thread: a <see cref="PolicyNetwork"/> owns scratch buffers.
    /// </summary>
    private static ThreadLocal<BotBrainSpec> Brains(string checkpoints, HandoverRule rule = HandoverRule.MacroTask) => new(() => BotBrainSpec.Of(
        new PolicyMicroSpec(PolicyNetwork.Load(Path.Combine(checkpoints, "tier0_combat4_s2.npz"))),
        new PolicyMicroSpec(PolicyNetwork.Load(Path.Combine(checkpoints, "tier2_pyro_rest3_s0.npz")))) with
    {
        Rule = rule,
    });

    private static string[] MapList() =>
        Environment.GetEnvironmentVariable("BENCH_FOCUS") is { Length: > 0 } focus
            ? focus.Split(',')
            : PerMapEval.Maps;

    private static (string Name, int Tier, bool HandicapOnly)[] ArmsWanted() =>
        Environment.GetEnvironmentVariable("BENCH_TIER_ARMS") is { Length: > 0 } wanted
            ? [.. ArmList.Where(a => wanted.Split(',').Contains(a.Name))]
            : ArmList;

    /// <summary>
    /// Duels: two characters of one class placed 80-200px apart, the subject's
    /// whole bot against a tier-5 one, to the first death or <c>BENCH_DUEL_TICKS</c>.
    /// </summary>
    public static void RunDuels(string maps, string nav, string checkpoints, int perMap)
    {
        var cap = int.Parse(Environment.GetEnvironmentVariable("BENCH_DUEL_TICKS") ?? "900", CultureInfo.InvariantCulture);
        var rule = Enum.Parse<HandoverRule>(Environment.GetEnvironmentVariable("BENCH_DUEL_RULE") ?? nameof(HandoverRule.MacroTask));
        var brains = Brains(checkpoints, rule);
        var trace = int.Parse(Environment.GetEnvironmentVariable("BENCH_DUEL_TRACE") ?? "-1", CultureInfo.InvariantCulture);
        var arms = ArmsWanted();
        var list = MapList();
        var work = from map in list from i in Enumerable.Range(0, perMap) from arm in arms select (map, i, arm);
        var results = new ConcurrentBag<Observation>();

        Console.WriteLine($"{list.Length} maps x {perMap} duels x {arms.Length} arms, {cap}-tick cap, band 80-200px in sight, {rule}");

        Parallel.ForEach(work, item =>
        {
            var (map, i, arm) = item;
            var playerClass = Classes[i % Classes.Length];
            var subject = i % 2 == 0 ? Team.Red : Team.Blue;
            var (world, red, blue) = PlaceInSight(maps, nav, map, Seed(map, i), playerClass);

            var subjectSlot = subject == Team.Red ? red : blue;
            var otherSlot = subject == Team.Red ? blue : red;
            var subjectBot = Bot(world, Spread(arm.Name) ? brains.Value.WithSpreadTurn() : brains.Value, subject, subjectSlot, arm);
            var otherBot = Bot(world, brains.Value, subject == Team.Red ? Team.Blue : Team.Red, otherSlot, ("ref", 5, false));

            world.Drive(
                subjectSlot,
                Humanized(arm.Name) ? new HumanizedBot(subjectBot, HumanizeFor(arm.Name), (uint)subjectSlot) : subjectBot);
            world.Drive(otherSlot, otherBot);

            bool subjectDied = false, otherDied = false;
            var ticks = 0;
            var subjectStart = world.State.Characters[subjectSlot].Hp;
            var otherStart = world.State.Characters[otherSlot].Hp;

            while (ticks < cap && !subjectDied && !otherDied)
            {
                world.Step();
                ticks++;
                subjectDied = world.Events.Died[subjectSlot];
                otherDied = world.Events.Died[otherSlot];

                if (trace == i && ticks % 30 == 0)
                {
                    ref readonly var a = ref world.State.Characters[subjectSlot];
                    ref readonly var b = ref world.State.Characters[otherSlot];
                    Console.WriteLine(
                        $"{arm.Name} t={ticks} subj=({a.X:0},{a.Y:0}) hp {a.Hp:0} drove={subjectBot.MicroDrove} "
                        + $"other=({b.X:0},{b.Y:0}) hp {b.Hp:0} drove={otherBot.MicroDrove} "
                        + $"d={Gml.PointDistance(a.X, a.Y, b.X, b.Y):0} "
                        + $"target={subjectBot.ScriptedBot.Combat.Target.Exists}/{subjectBot.ScriptedBot.Combat.Target.Visible} "
                        + $"engage={subjectBot.ScriptedBot.WantsToEngage} keys={subjectBot.ScriptedBot.Combat.AttackKeys} aim={subjectBot.ScriptedBot.Combat.AimDir:0} "
                        + $"spawnroom={world.Collision.PointInSpawnRoom(a.X, a.Y)}/{world.Collision.PointInSpawnRoom(b.X, b.Y)} "
                        + $"blocked={world.Collision.CollisionLineBlocksBullets(a.X, a.Y, b.X, b.Y, world.State.BulletFilter)}");
                }
            }

            // A kill is 1 or 0 and a trade is 1/2. With neither dead by the cap the
            // duel is scored by the share of the damage dealt — fractions of each
            // side's own health, so a heavy and a scout are scored alike — because
            // the scripted layer with no objective and no target stands still, and
            // most timeouts are a real exchange that then stopped. Scoring those 1/2
            // threw the exchange away. No damage at all is 1/2.
            var subjectLost = subjectDied ? 1.0 : 1 - (world.State.Characters[subjectSlot].Hp / subjectStart);
            var otherLost = otherDied ? 1.0 : 1 - (world.State.Characters[otherSlot].Hp / otherStart);
            var score = subjectDied != otherDied ? (otherDied ? 1.0 : 0.0)
                : subjectDied || subjectLost + otherLost <= 0 ? 0.5
                : otherLost / (subjectLost + otherLost);
            var detail = subjectDied == otherDied ? (subjectDied ? "trade" : "timeout") : otherDied ? "win" : "loss";

            results.Add(new Observation(map, arm.Name, i, playerClass.ToString(), subject, score, ticks, subjectBot.MicroTicks, detail));
        });

        Report(results, "duel", "score (kill 1, else share of damage dealt)");
    }

    /// <summary>
    /// A pair inside the band with a clear line for bullets between them.
    /// </summary>
    /// <remarks>
    /// <b>The band alone is not a duel.</b> <see cref="ScenarioWorld.PlaceInBand"/>
    /// measures a straight line through walls, and on <c>ctf_truefort</c> two thirds
    /// of its pairs sat on different floors and timed out without a shot landing:
    /// two bots stood still for 90 seconds 200px apart through a floor, and another
    /// pair circled 55px apart, one directly above the other. Each timeout is a draw scored
    /// 1/2, so they diluted every arm towards the control. A rejected draw rebuilds
    /// the world from the next seed, so a duel is still a pure function of its own
    /// seed.
    /// </remarks>
    private static (ScenarioWorld World, int Red, int Blue) PlaceInSight(
        string maps, string nav, string map, uint seed, PlayerClass playerClass)
    {
        for (var attempt = 0u; attempt < 64; attempt++)
        {
            var world = ScenarioWorld.Load(map, 1, maps, nav);
            world.State.Random = new GmlRandom(seed + (attempt * 7919));
            var (red, blue) = world.PlaceInBand(playerClass, playerClass, 80, 200);

            ref readonly var a = ref world.State.Characters[red];
            ref readonly var b = ref world.State.Characters[blue];

            if (Clear(world, a.X, a.Y, b.X, b.Y, 0) && Clear(world, a.X, a.Y, b.X, b.Y, -8) && Clear(world, a.X, a.Y, b.X, b.Y, 8))
                return (world, red, blue);
        }

        throw new InvalidOperationException($"no pair in sight on {map} after 64 draws");
    }

    private static bool Clear(ScenarioWorld world, double ax, double ay, double bx, double by, double dy) =>
        !world.Collision.CollisionLineBlocksBullets(ax, ay + dy, bx, by + dy, world.State.BulletFilter);

    private static HybridBot Bot(ScenarioWorld world, BotBrainSpec brain, Team team, int slot, (string, int Tier, bool HandicapOnly) arm)
    {
        var scripted = new ScriptedBot(world.Nav, new NavQuery(team), BotSkill.ForTier(arm.HandicapOnly ? 5 : arm.Tier), slot);
        var hybrid = brain.Wrap(scripted);

        if (arm.HandicapOnly)
            hybrid.HandicapSkill = BotSkill.ForTier(arm.Tier);

        return hybrid;
    }

    /// <summary>
    /// Matches: <c>BENCH_BOTS</c> bots (default 12), the policy brain on both
    /// teams, the subject team at the arm's tier and the other at tier 5.
    /// </summary>
    public static void RunMatches(string maps, string nav, string checkpoints, int perMap)
    {
        var cap = int.Parse(Environment.GetEnvironmentVariable("BENCH_CAP") ?? "5", CultureInfo.InvariantCulture);
        var bots = int.Parse(Environment.GetEnvironmentVariable("BENCH_BOTS") ?? "12", CultureInfo.InvariantCulture);
        var brains = Brains(checkpoints);
        var arms = ArmsWanted();
        var list = MapList();
        var work = from map in list from i in Enumerable.Range(0, perMap) from arm in arms select (map, i, arm);
        var results = new ConcurrentBag<Observation>();

        Console.WriteLine($"{list.Length} maps x {perMap} matches x {arms.Length} arms, {bots} bots, {cap}-minute cap");

        Parallel.ForEach(work, item =>
        {
            var (map, i, arm) = item;
            var subject = i % 2 == 0 ? Team.Red : Team.Blue;

            var options = HeadlessMatchOptions.Default with
            {
                Map = map,
                Bots = bots,
                Seed = Seed(map, i),
                CapLimit = 1,
                TimeLimitMinutes = cap,
                KillLimit = 40,
                MaxTicks = 30 * 60 * cap,
                AllowedClasses = Classes,
                Brain = brains.Value,
                RedDifficulty = subject == Team.Red ? arm.Tier : 5,
                BlueDifficulty = subject == Team.Blue ? arm.Tier : 5,
                HandicapOnly = arm.HandicapOnly,
                HumanizeTeam = Humanized(arm.Name) ? subject : null,
                HumanizeWith = HumanizeFor(arm.Name),
                SpreadTurnTeam = Spread(arm.Name) ? subject : null,
            };

            var match = HeadlessMatch.Load(options, maps, nav);
            long subjectDeaths = 0, otherDeaths = 0;

            while (match.Outcome == MatchOutcome.Running)
            {
                match.Step();

                for (var slot = 1; slot < WorldState.MaxSlots; slot++)
                {
                    if (!match.Events.Died[slot])
                        continue;

                    if (match.State.Players[slot].Team == subject)
                        subjectDeaths++;
                    else
                        otherDeaths++;
                }
            }

            long micro = 0;

            for (var slot = 1; slot < WorldState.MaxSlots; slot++)
            {
                if (match.State.Players[slot].Team == subject)
                    micro += match.HybridBotFor(slot)?.MicroTicks ?? 0;
            }

            var total = subjectDeaths + otherDeaths;

            // The OTHER team's share of deaths, so that higher is better for the
            // subject in both modes.
            var score = total == 0 ? 0.5 : (double)otherDeaths / total;
            var result = match.Result;
            var detail = result.Outcome != MatchOutcome.Decided ? "capped"
                : result.Winner == subject ? "win"
                : result.Winner is Team.Red or Team.Blue ? "loss"
                : "draw";

            results.Add(new Observation(
                map, arm.Name, i, "mixed", subject, score, match.Tick, micro,
                $"{detail};{subjectDeaths};{otherDeaths}"));
        });

        Report(results, "match", "opponent's death share");
    }

    /// <summary>
    /// Observation i's seed. <c>BENCH_SEED_BASE</c> moves the whole set, so a
    /// tuning can be checked on matches it was not tuned on.
    /// </summary>
    private static uint Seed(string map, int i) =>
        (uint)(SeedBase + (map.Length * 131) + (i * 37));

    private static readonly int SeedBase =
        int.Parse(Environment.GetEnvironmentVariable("BENCH_SEED_BASE") ?? "9100", CultureInfo.InvariantCulture);

    private static void Report(ConcurrentBag<Observation> results, string unit, string statistic)
    {
        if (Environment.GetEnvironmentVariable("BENCH_TIERS_CSV") is { Length: > 0 } path)
        {
            using var csv = new StreamWriter(path);
            csv.WriteLine("map,arm,index,class,subject,score,ticks,subject_micro_ticks,detail");

            foreach (var o in results.OrderBy(o => o.Map).ThenBy(o => o.Arm).ThenBy(o => o.Index))
                csv.WriteLine($"{o.Map},{o.Arm},{o.Index},{o.Class},{o.Subject},{o.Score:0.####},{o.Ticks},{o.SubjectMicroTicks},{o.Detail}");
        }

        var control = results.Where(o => o.Arm == "full:5").ToDictionary(o => (o.Map, o.Index), o => o.Score);

        Console.WriteLine();
        Console.WriteLine($"{statistic}, one row per arm; n = {unit}{(unit == "match" ? "es" : "s")}; vs full:5 is paired per {unit}");
        Console.WriteLine($"{"arm",-8} {"n",6} {"score",7} {"+/-95%",6}   {"vs full:5",9} {"+/-95%",6}   {"micro/obs",9}   outcomes");

        foreach (var arm in ArmsWanted())
        {
            var rows = results.Where(o => o.Arm == arm.Name).ToList();

            if (rows.Count == 0)
                continue;

            var (mean, half) = MeanAndHalfWidth(rows.Select(o => o.Score).ToList());

            var diffs = rows
                .Where(o => control.ContainsKey((o.Map, o.Index)))
                .Select(o => o.Score - control[(o.Map, o.Index)])
                .ToList();

            var paired = diffs.Count > 1 && arm.Name != "full:5"
                ? MeanAndHalfWidth(diffs)
                : (double.NaN, double.NaN);

            var outcomes = string.Join(" ", rows
                .GroupBy(o => o.Detail.Split(';')[0])
                .OrderBy(g => g.Key)
                .Select(g => $"{g.Key}={g.Count()}"));

            Console.WriteLine(
                $"{arm.Name,-8} {rows.Count,6} {mean,7:0.000} {half,6:0.000}   "
                + $"{paired.Item1,9:+0.000;-0.000} {paired.Item2,6:0.000}   "
                + $"{rows.Average(o => o.SubjectMicroTicks),9:0.0}   {outcomes}");
        }
    }

    private static (double Mean, double HalfWidth) MeanAndHalfWidth(List<double> values)
    {
        var mean = values.Average();
        var variance = values.Sum(v => (v - mean) * (v - mean)) / Math.Max(1, values.Count - 1);

        return (mean, 1.96 * Math.Sqrt(variance / values.Count));
    }
}
