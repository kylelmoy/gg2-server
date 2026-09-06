using System.Diagnostics;
using Gg2.Bots;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Bench;

/// <summary>
/// Phase 4's evaluation: the hybrid bot on red against the scripted bot on blue,
/// over whole CTF matches.
/// </summary>
/// <remarks>
/// <b>Every match reports duration, deaths and the policy's share of control
/// beside its winner.</b> "N matches completed" is satisfiable by a broken
/// feature — this project's own <c>eval</c> called instant Arena rounds clean
/// wins — and "the policy was wired in" is not "the policy drove".
/// </remarks>
public static class PolicyEval
{
    public static void Run(string maps, string nav, string npz, int matchesPerMap)
    {
        // BOTH ARMS. A stuck-bot number from the policy arm alone cannot say
        // whether the policy caused it; the scripted bot has always been able to
        // get stuck and nobody was measuring. The control is the same matches
        // with Policy = null.
        if (Environment.GetEnvironmentVariable("BENCH_CONFIRM") == "1")
        {
            Measure(maps, nav, npz, matchesPerMap, true, HandoverRule.MacroTask, 150);
            Console.WriteLine();
            Measure(maps, nav, npz, matchesPerMap, false, HandoverRule.Distance, 300);
            return;
        }

        Measure(maps, nav, npz, matchesPerMap, true, HandoverRule.MacroTask, 300);
        Console.WriteLine();
        Measure(maps, nav, npz, matchesPerMap, true, HandoverRule.MacroTask, 220);
        Console.WriteLine();
        Measure(maps, nav, npz, matchesPerMap, true, HandoverRule.MacroTask, 150);
        Console.WriteLine();
        Measure(maps, nav, npz, matchesPerMap, true, HandoverRule.MacroTask, 80);
        Console.WriteLine();
        Measure(maps, nav, npz, matchesPerMap, false, HandoverRule.Distance, 300);
    }

    private static void Measure(
        string maps, string nav, string npz, int matchesPerMap, bool usePolicy, HandoverRule rule, double range)
    {
        var policy = usePolicy ? PolicyNetwork.Load(npz) : null;
        string[] mapNames =
        [
            "ctf_truefort", "ctf_2dfort", "ctf_orange", "ctf_conflict",
            "ctf_classicwell", "ctf_eiger",
        ];

        // ⛔ The checkpoint's own set when there is one. `usePolicy` false is the
        // scripted arm, which has no checkpoint to ask and takes the default so the
        // two arms are populated alike — the moment a policy is loaded, the classes
        // it never trained are not a harder test of it, they are a test of the
        // fallback to scripted.
        var trained = policy is null
            ? PolicyNetwork.DefaultTrainedClasses.ToArray()
            : policy.TrainedClasses.ToArray();
        var clock = Stopwatch.StartNew();

        var redWins = 0;
        var blueWins = 0;
        var draws = 0;
        long totalTicks = 0;
        long totalDeaths = 0;
        long policyTicks = 0;
        long scriptedTicks = 0;
        var deadMatches = 0;
        var worstTravel = double.PositiveInfinity;
        var worstTravelMap = "";
        var stuck = 0;
        var bots = 0;
        long redDeaths = 0;
        long blueDeaths = 0;
        long redCaps = 0;
        long blueCaps = 0;
        long intelPickups = 0;

        foreach (var map in mapNames)
        {
            for (var i = 0; i < matchesPerMap; i++)
            {
                var seed = (uint)(1000 + (map.Length * 97) + (i * 31));

                var options = HeadlessMatchOptions.Default with
                {
                    Map = map,
                    Bots = 8,
                    Seed = seed,
                    CapLimit = 1,
                    TimeLimitMinutes = 5,
                    KillLimit = 40,
                    MaxTicks = 30 * 60 * 5,
                    AllowedClasses = trained,
                    Brain = policy is null
                        ? null
                        : new BotBrainSpec
                        {
                            Tiers = [new PolicyMicroSpec(policy)],
                            Rule = rule,
                            Range = range,
                        },
                    HybridTeam = Team.Red,
                };

                var match = HeadlessMatch.Load(options, maps, nav);
                var deaths = 0;
                var start = new (double X, double Y)[WorldState.MaxSlots];
                var travel = new double[WorldState.MaxSlots];
                var seen = new bool[WorldState.MaxSlots];

                while (match.Outcome == MatchOutcome.Running)
                {
                    match.Step();

                    for (var slot = 1; slot < WorldState.MaxSlots; slot++)
                    {
                        if (match.Events.Died[slot])
                        {
                            deaths++;

                            // Deaths BY TEAM, which exists in every match
                            // whether or not it decides. 22 of 24 matches hit the
                            // time cap undecided, so a win rate over them is a
                            // statistic about the cap rather than about the bots.
                            if (match.State.Players[slot].Team == Team.Red)
                                redDeaths++;
                            else
                                blueDeaths++;
                        }

                        if (!match.State.IsAlive(slot))
                            continue;

                        ref readonly var c = ref match.State.Characters[slot];

                        if (!seen[slot])
                        {
                            seen[slot] = true;
                            start[slot] = (c.X, c.Y);
                        }

                        var dx = c.X - start[slot].X;
                        var dy = c.Y - start[slot].Y;

                        travel[slot] = Math.Max(travel[slot], Math.Sqrt((dx * dx) + (dy * dy)));
                    }
                }

                for (var slot = 1; slot < WorldState.MaxSlots; slot++)
                {
                    if (match.HybridBotFor(slot) is { } bot)
                    {
                        policyTicks += bot.MicroTicks;
                        scriptedTicks += bot.ScriptedTicks;
                    }

                    if (!seen[slot])
                        continue;

                    var team = match.State.Players[slot].Team;

                    bots++;

                    if (travel[slot] < 100)
                        stuck++;

                    if (travel[slot] < worstTravel)
                    {
                        worstTravel = travel[slot];
                        worstTravelMap =
                            $"{map} seed {seed} slot {slot} {team} {match.State.Players[slot].Class}"
                            + $" {(match.HybridBotFor(slot) is null ? "scripted" : "hybrid")}";
                    }
                }

                redCaps += match.State.RedCaps;
                blueCaps += match.State.BlueCaps;

                totalTicks += match.Tick;
                totalDeaths += deaths;

                if (deaths == 0)
                    deadMatches++;

                var result = match.Result;

                if (result.Outcome != MatchOutcome.Decided)
                    draws++;
                else if (result.Winner == Team.Red)
                    redWins++;
                else if (result.Winner == Team.Blue)
                    blueWins++;
                else
                    draws++;
            }
        }

        var played = redWins + blueWins + draws;

        Console.WriteLine($"{(usePolicy ? $"POLICY (red) vs SCRIPTED (blue), handover={rule} range={range:0}" : "CONTROL: scripted both sides")}, {played} matches");
        Console.WriteLine($"  wall clock            {clock.Elapsed.TotalSeconds:0.0}s");
        Console.WriteLine($"  red wins              {redWins}  ({redWins / (double)played:0.0%})");
        Console.WriteLine($"  blue wins             {blueWins}  ({blueWins / (double)played:0.0%})");
        Console.WriteLine($"  undecided             {draws}");
        Console.WriteLine($"  mean duration         {totalTicks / (double)played / 30.0:0.0}s of game time");
        Console.WriteLine($"  mean deaths a match   {totalDeaths / (double)played:0.0}");
        Console.WriteLine(
            $"  deaths red/blue       {redDeaths} / {blueDeaths}"
            + $"   red death share {redDeaths / (double)Math.Max(1, redDeaths + blueDeaths):0.0%}"
            + "   <- below 50% means red survived better");
        Console.WriteLine($"  matches with NO death {deadMatches}   <- must be 0");
        Console.WriteLine(
            $"  CAPTURES red/blue     {redCaps} / {blueCaps}"
            + $"   ({redCaps / (double)played:0.00} / {blueCaps / (double)played:0.00} a match)");
        Console.WriteLine($"  policy share of control {policyTicks / (double)Math.Max(1, policyTicks + scriptedTicks):0.0%}");
        Console.WriteLine($"  bots that moved <100px {stuck} of {bots}");
        Console.WriteLine($"  least any bot moved   {worstTravel:0} px  ({worstTravelMap})");
    }
}
