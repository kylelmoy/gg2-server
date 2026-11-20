using System.Collections.Concurrent;
using System.Globalization;
using GG2.Bots;
using GG2.Protocol;
using GG2.Simulation;

namespace Bench;

/// <summary>
/// The shipped policy nets against the rollout search, on opposite teams of one
/// match — <c>BENCH_VERSUS</c>.
/// </summary>
/// <remarks>
/// <b>Head to head, not each against the scripted arm.</b> <c>BENCH_ARMS</c>
/// scores every arm against all-scripted opponents, so it can rank two controllers
/// only by comparing their margins over a third. Here the two meet: red and blue
/// get different brains (<see cref="HeadlessMatchOptions.RedBrain"/>), sides
/// alternate per match so a map's own asymmetry cancels, and both play at tier 5
/// under one handover rule and range, so the controller is the one thing different.
/// <para>
/// <b>Class coverage is held equal.</b> The policy arm is the nine shipped nets, one
/// class each; the search arm drives every class (<c>RestrictToTrainedClasses</c>
/// off), as <c>--bot-mode search</c> does on a server. Both teams draw from
/// <c>BENCH_VERSUS_CLASSES</c> (default all nine).
/// </para>
/// <para>
/// <b>Scored as the search team's share of the match's deaths</b>, so higher is
/// better for the policy and 0.5 is level; one observation per match. A
/// controller only drives inside the engagement window, so the wheel share per
/// side is printed beside it: a gap there is a gap in how often each was asked,
/// not only in how well it played.
/// </para>
/// <para>
/// ⚠️ <b>No arbiter and no thread budget</b>, as <c>BENCH_ARMS</c>' arm 3: the
/// search decides whenever it is due, which a live server's
/// <c>DecisionQuotaArbiter</c> would not always allow. So this is the search at
/// full strength, and a live search bot can be weaker than it reads here.
/// </para>
/// </remarks>
internal static class Versus
{
    private sealed record Observation(
        string Map, int Index, Team Policy, double Score, int Ticks,
        double PolicyWheel, double SearchWheel, string Outcome, long PolicyDeaths, long SearchDeaths);

    public static void Run(string maps, string nav, string checkpoints, int perMap)
    {
        var cap = Int("BENCH_CAP", 5);
        var bots = Int("BENCH_BOTS", 12);
        var rule = Enum.Parse<HandoverRule>(Environment.GetEnvironmentVariable("BENCH_VERSUS_RULE") ?? nameof(HandoverRule.Distance));
        var classes = Environment.GetEnvironmentVariable("BENCH_VERSUS_CLASSES") is { Length: > 0 } listed
            ? [.. listed.Split(',').Select(c => Enum.Parse<PlayerClass>(c.Trim(), ignoreCase: true))]
            : Enum.GetValues<PlayerClass>().Where(c => c != PlayerClass.Quote).ToArray();
        var list = Environment.GetEnvironmentVariable("BENCH_FOCUS") is { Length: > 0 } focus
            ? focus.Split(',')
            : PerMapEval.Maps;

        // One brain per thread: a SimPolicyNetwork owns scratch buffers.
        var policy = new ThreadLocal<BotBrainSpec>(() => BotBrainSpec.Of(Nets.Shipped(checkpoints)) with { Rule = rule });
        var search = BotBrainSpec.Of(new SearchMicroSpec(new RolloutOptions(Threads: 1), RestrictToTrainedClasses: false)) with { Rule = rule };

        var work = from map in list from i in Enumerable.Range(0, perMap) select (map, i);
        var results = new ConcurrentBag<Observation>();

        Console.WriteLine(
            $"{list.Length} maps x {perMap} matches, {bots} bots, {cap}-minute cap, rule {rule}, "
            + $"classes {string.Join(",", classes)}, both tier 5");

        Parallel.ForEach(work, item =>
        {
            var (map, i) = item;
            var policyTeam = i % 2 == 0 ? Team.Red : Team.Blue;

            var options = HeadlessMatchOptions.Default with
            {
                Map = map,
                Bots = bots,
                Seed = (uint)(9300 + (map.Length * 131) + (i * 37)),
                CapLimit = 1,
                TimeLimitMinutes = cap,
                KillLimit = 40,
                MaxTicks = 30 * 60 * cap,
                AllowedClasses = classes,
                RedBrain = policyTeam == Team.Red ? policy.Value : search,
                BlueBrain = policyTeam == Team.Blue ? policy.Value : search,
            };

            var match = HeadlessMatch.Load(options, maps, nav);
            long policyDeaths = 0, searchDeaths = 0;

            while (match.Outcome == MatchOutcome.Running)
            {
                match.Step();

                for (var slot = 1; slot < match.SlotCount; slot++)
                {
                    if (!match.Events.Died[slot])
                        continue;

                    if (match.State.Players[slot].Team == policyTeam)
                        policyDeaths++;
                    else
                        searchDeaths++;
                }
            }

            long policyMicro = 0, policyPolls = 0, searchMicro = 0, searchPolls = 0;

            for (var slot = 1; slot < match.SlotCount; slot++)
            {
                if (match.HybridBotFor(slot) is not { } hybrid)
                    continue;

                if (match.State.Players[slot].Team == policyTeam)
                {
                    policyMicro += hybrid.MicroTicks;
                    policyPolls += hybrid.ScriptedPolls;
                }
                else
                {
                    searchMicro += hybrid.MicroTicks;
                    searchPolls += hybrid.ScriptedPolls;
                }
            }

            var total = policyDeaths + searchDeaths;
            var result = match.Result;
            var outcome = result.Outcome != MatchOutcome.Decided ? "capped"
                : result.Winner == policyTeam ? "policy"
                : result.Winner is Team.Red or Team.Blue ? "search"
                : "draw";

            results.Add(new Observation(
                map, i, policyTeam, total == 0 ? 0.5 : (double)searchDeaths / total, match.Tick,
                policyPolls == 0 ? 0 : (double)policyMicro / policyPolls,
                searchPolls == 0 ? 0 : (double)searchMicro / searchPolls,
                outcome, policyDeaths, searchDeaths));
        });

        if (Environment.GetEnvironmentVariable("BENCH_VERSUS_CSV") is { Length: > 0 } path)
        {
            using var csv = new StreamWriter(path);
            csv.WriteLine("map,index,policy_team,score,ticks,policy_wheel,search_wheel,outcome,policy_deaths,search_deaths");

            foreach (var o in results.OrderBy(o => o.Map).ThenBy(o => o.Index))
            {
                csv.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{o.Map},{o.Index},{o.Policy},{o.Score:0.####},{o.Ticks},{o.PolicyWheel:0.####},{o.SearchWheel:0.####},{o.Outcome},{o.PolicyDeaths},{o.SearchDeaths}"));
            }
        }

        Console.WriteLine();
        Console.WriteLine("score = the search team's share of deaths (above 0.5 favours the policy); wheel = share of a side's polls its controller drove");
        Console.WriteLine($"{"map",-20} {"n",4} {"score",7} {"+/-95%",6}   {"wheel p/s",11}   outcomes");

        foreach (var group in results.GroupBy(o => o.Map).OrderBy(g => g.Key))
            Row(group.Key, [.. group]);

        Console.WriteLine();
        Row("ALL", [.. results]);

        var deathsPolicy = results.Sum(o => o.PolicyDeaths);
        var deathsSearch = results.Sum(o => o.SearchDeaths);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"pooled deaths: policy {deathsPolicy}, search {deathsSearch}, search share {(double)deathsSearch / Math.Max(1, deathsPolicy + deathsSearch):0.000}"));
    }

    private sealed record Duel(
        string Map, int Index, string Class, Team Policy, double Score, int Ticks,
        double PolicyWheel, double SearchWheel, string Outcome);

    /// <summary>
    /// 1v1 duels, policy against search, with the controller holding the wheel the
    /// whole time — <c>BENCH_VERSUS_DUEL</c>.
    /// </summary>
    /// <remarks>
    /// <b>Always driving</b> is the duel server's own setting: the <c>Distance</c>
    /// rule with a 100,000px range, so while the enemy lives the controller has the
    /// wheel and the scripted layer only routes. The wheel shares are printed so
    /// that is checked, not assumed.
    /// <para>
    /// <b>The policy is the duel nets</b> (<c>checkpoints/duel</c>, sampled, as the
    /// duel server runs them; <c>BENCH_VERSUS_NETS=team</c> takes the nine team
    /// nets, greedy), on the duel server's map pool by default (<c>maps/duel</c>,
    /// <c>duel_01</c>-<c>duel_08</c>; <c>BENCH_FOCUS</c> names shipped maps
    /// instead). Both bots get the map's next-hop tables, because a terrain net's
    /// observation reads its route from them.
    /// </para>
    /// <para>
    /// The placement and the score are <c>BENCH_TIERDUEL</c>'s: one class a duel,
    /// cycling, 80-200px apart with a clear line, to the first death or
    /// <c>BENCH_DUEL_TICKS</c> (900); a kill scores 1, a trade 1/2, a timeout the
    /// share of damage dealt. Scored for the policy, so 0.5 is level.
    /// </para>
    /// </remarks>
    public static void RunDuels(string maps, string nav, string checkpoints, int perMap)
    {
        var cap = Int("BENCH_DUEL_TICKS", 900);
        var teamNets = Environment.GetEnvironmentVariable("BENCH_VERSUS_NETS") == "team";
        var focus = Environment.GetEnvironmentVariable("BENCH_FOCUS");
        var duelMaps = focus is not { Length: > 0 };
        var mapDirectory = duelMaps ? Path.Combine(maps, "duel") : maps;
        var navDirectory = duelMaps ? Path.Combine(nav, "duel") : nav;
        string[] list = duelMaps ? [.. Enumerable.Range(1, 8).Select(n => $"duel_{n:00}")] : focus!.Split(',');

        PlayerClass[] classes = Environment.GetEnvironmentVariable("BENCH_VERSUS_CLASSES") is { Length: > 0 } listed
            ? [.. listed.Split(',').Select(c => Enum.Parse<PlayerClass>(c.Trim(), ignoreCase: true))]
            : [.. Enum.GetValues<PlayerClass>().Where(c => c != PlayerClass.Quote)];

        static BotBrainSpec Always(MicroControllerSpec[] tiers) => BotBrainSpec.Of(tiers) with
        {
            Rule = HandoverRule.Distance,
            Range = 100_000,
            ReleaseRange = 100_000,
        };

        var policy = new ThreadLocal<BotBrainSpec>(() =>
            Always(teamNets ? Nets.Shipped(checkpoints) : Nets.Duel(Path.Combine(checkpoints, "duel"))));
        var special = Environment.GetEnvironmentVariable("BENCH_VERSUS_SPECIAL") == "1";
        var search = Always([new SearchMicroSpec(new RolloutOptions(ConsiderSpecial: special, Threads: 1), RestrictToTrainedClasses: false)]);
        var tables = new ConcurrentDictionary<string, Lazy<NavDistanceSet?>>();

        var work = from map in list from i in Enumerable.Range(0, perMap) select (map, i);
        var results = new ConcurrentBag<Duel>();

        Console.WriteLine(
            $"{list.Length} maps x {perMap} duels, {cap}-tick cap, {(teamNets ? "team nets, greedy" : "duel nets, sampled")}, "
            + $"classes {string.Join(",", classes)}, both tier 5, the controller always driving, search special {(special ? "on" : "off (the server's)")}");

        Parallel.ForEach(work, item =>
        {
            var (map, i) = item;
            var playerClass = classes[i % classes.Length];
            var policyTeam = (i / classes.Length) % 2 == 0 ? Team.Red : Team.Blue;
            var (world, red, blue) = Tiers.PlaceInSight(
                mapDirectory, navDirectory, map, (uint)(9500 + (map.Length * 131) + (i * 37)), playerClass);
            var graph = world.Nav;
            var distances = tables.GetOrAdd(map, m => new Lazy<NavDistanceSet?>(() =>
                graph is null ? null : NavDistanceSet.LoadOrDerive(navDirectory, m, 1, graph, out _))).Value;

            var policySlot = policyTeam == Team.Red ? red : blue;
            var searchSlot = policyTeam == Team.Red ? blue : red;
            var searchTeam = policyTeam == Team.Red ? Team.Blue : Team.Red;
            var policyBot = policy.Value!.Wrap(new ScriptedBot(world.Nav, new NavQuery(policyTeam), BotSkill.ForTier(5), policySlot) { Distances = distances });
            var searchBot = search.Wrap(new ScriptedBot(world.Nav, new NavQuery(searchTeam), BotSkill.ForTier(5), searchSlot) { Distances = distances });

            world.Drive(policySlot, policyBot);
            world.Drive(searchSlot, searchBot);

            bool policyDied = false, searchDied = false;
            var ticks = 0;
            var policyStart = world.State.Characters[policySlot].Hp;
            var searchStart = world.State.Characters[searchSlot].Hp;

            while (ticks < cap && !policyDied && !searchDied)
            {
                world.Step();
                ticks++;
                policyDied = world.Events.Died[policySlot];
                searchDied = world.Events.Died[searchSlot];
            }

            var policyLost = policyDied ? 1.0 : 1 - (world.State.Characters[policySlot].Hp / policyStart);
            var searchLost = searchDied ? 1.0 : 1 - (world.State.Characters[searchSlot].Hp / searchStart);
            var score = policyDied != searchDied ? (searchDied ? 1.0 : 0.0)
                : policyDied || policyLost + searchLost <= 0 ? 0.5
                : searchLost / (policyLost + searchLost);
            var outcome = policyDied == searchDied ? (policyDied ? "trade" : "timeout") : searchDied ? "policy" : "search";

            results.Add(new Duel(map, i, playerClass.ToString(), policyTeam, score, ticks, Share(policyBot), Share(searchBot), outcome));
        });

        if (Environment.GetEnvironmentVariable("BENCH_VERSUS_CSV") is { Length: > 0 } path)
        {
            using var csv = new StreamWriter(path);
            csv.WriteLine("map,index,class,policy_team,score,ticks,policy_wheel,search_wheel,outcome");

            foreach (var o in results.OrderBy(o => o.Map).ThenBy(o => o.Index))
            {
                csv.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{o.Map},{o.Index},{o.Class},{o.Policy},{o.Score:0.####},{o.Ticks},{o.PolicyWheel:0.####},{o.SearchWheel:0.####},{o.Outcome}"));
            }
        }

        Console.WriteLine();
        Console.WriteLine("score: kill 1, trade 1/2, timeout the share of damage dealt; for the policy, so above 0.5 favours it");
        Console.WriteLine($"{"class/map",-20} {"n",4} {"score",7} {"+/-95%",6}   {"wheel p/s",11}   outcomes");

        foreach (var group in results.GroupBy(o => o.Class).OrderBy(g => g.Key))
            DuelRow(group.Key, [.. group]);

        Console.WriteLine();

        foreach (var group in results.GroupBy(o => o.Map).OrderBy(g => g.Key))
            DuelRow(group.Key, [.. group]);

        Console.WriteLine();
        DuelRow("ALL", [.. results]);
    }

    private static double Share(HybridBot bot) => bot.ScriptedPolls == 0 ? 0 : (double)bot.MicroTicks / bot.ScriptedPolls;

    private static void DuelRow(string label, List<Duel> rows)
    {
        var scores = rows.Select(o => o.Score).ToList();
        var mean = scores.Average();
        var variance = scores.Sum(v => (v - mean) * (v - mean)) / Math.Max(1, scores.Count - 1);
        var half = 1.96 * Math.Sqrt(variance / scores.Count);
        var outcomes = string.Join(" ", rows.GroupBy(o => o.Outcome).OrderBy(g => g.Key).Select(g => $"{g.Key}={g.Count()}"));

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{label,-20} {rows.Count,4} {mean,7:0.000} {half,6:0.000}   {rows.Average(o => o.PolicyWheel),5:0.00}/{rows.Average(o => o.SearchWheel),5:0.00}   {outcomes}"));
    }

    private static void Row(string label, List<Observation> rows)
    {
        var scores = rows.Select(o => o.Score).ToList();
        var mean = scores.Average();
        var variance = scores.Sum(v => (v - mean) * (v - mean)) / Math.Max(1, scores.Count - 1);
        var half = 1.96 * Math.Sqrt(variance / scores.Count);
        var outcomes = string.Join(" ", rows.GroupBy(o => o.Outcome).OrderBy(g => g.Key).Select(g => $"{g.Key}={g.Count()}"));

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{label,-20} {rows.Count,4} {mean,7:0.000} {half,6:0.000}   {rows.Average(o => o.PolicyWheel),5:0.00}/{rows.Average(o => o.SearchWheel),5:0.00}   {outcomes}"));
    }

    private static int Int(string name, int fallback) =>
        int.Parse(Environment.GetEnvironmentVariable(name) ?? fallback.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
}
