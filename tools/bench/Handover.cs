using System.Collections.Concurrent;
using System.Globalization;
using GG2.Bots;
using GG2.Protocol;
using GG2.Simulation;

namespace Bench;

/// <summary>
/// <c>BENCH_HANDOVER=&lt;n&gt;</c>: changes to WHEN the policy holds the wheel,
/// each paired against no release band.
/// </summary>
/// <remarks>
/// <para>
/// The shape of <see cref="RangeSweep"/> (Q22), for the shipped two-checkpoint
/// bundle rather than one checkpoint. Red is the hybrid team and blue is
/// all-scripted, both at tier 5. Twelve bots, the bundle's five classes,
/// <c>BENCH_FOCUS</c> maps (default every map), a <c>BENCH_CAP</c>-minute cap
/// (5), <c>n</c> matches a map an arm, with the same seeds in every arm.
/// </para>
/// <para>
/// Arms (<c>BENCH_HANDOVER_ARMS</c> picks a subset): <c>control</c> (red
/// all-scripted), <c>noband</c> (the gate before the release band shipped), <c>rel250</c> and <c>rel300</c> (the shipped default)
/// (<see cref="HybridBot.ReleaseRange"/>), and <c>goal</c>
/// (<see cref="HybridBot.EngageNearOwnGoal"/>). Each prints red's death share
/// (pooled), red's win share of decided matches and the control share. Against
/// <c>noband</c>, paired per match, it prints the death share difference (mean
/// of per-match differences, over matches where both had deaths) and the win
/// share difference, each with a 95% interval: a normal one for the first and a
/// paired bootstrap over matches for the second.
/// </para>
/// <para>
/// <b>Death share and win share are co-primaries and neither is read alone</b>:
/// Q14 and Q22 found death-share optima that lose the game. <c>gg2-rl</c>
/// <c>docs/PLAN.md</c> phase 5, B2, has the pre-registration.
/// </para>
/// </remarks>
public static class HandoverArms
{
    private static readonly PlayerClass[] Classes =
        [PlayerClass.Scout, PlayerClass.Soldier, PlayerClass.Demoman, PlayerClass.Heavy, PlayerClass.Pyro];

    private static readonly (string Name, double Release, bool NearGoal)[] ArmList =
    [
        ("control", double.NaN, false),
        ("noband", double.NaN, false),
        ("rel250", 250, false),
        ("rel300", 300, false),
        ("goal", double.NaN, true),
    ];

    private sealed record Observation(string Map, int Index, string Arm, long Red, long Blue, Team? Winner, long Micro, long Scripted);

    public static void Run(string maps, string nav, string checkpoints, int perMap)
    {
        var cap = int.Parse(Environment.GetEnvironmentVariable("BENCH_CAP") ?? "5", CultureInfo.InvariantCulture);
        var list = Environment.GetEnvironmentVariable("BENCH_FOCUS") is { Length: > 0 } focus ? focus.Split(',') : PerMapEval.Maps;
        var arms = Environment.GetEnvironmentVariable("BENCH_HANDOVER_ARMS") is { Length: > 0 } wanted
            ? [.. ArmList.Where(a => wanted.Split(',').Contains(a.Name))]
            : ArmList;

        // One set of networks per thread: a spec holds them and matches run in
        // parallel (BENCH_RELEASE found a shared one nondeterministic).
        var networks = new ThreadLocal<MicroControllerSpec[]>(() => Nets.Shipped(checkpoints));

        var results = new ConcurrentBag<Observation>();
        var work = from map in list from i in Enumerable.Range(0, perMap) from arm in arms select (map, i, arm);

        Console.WriteLine($"{list.Length} maps x {perMap} matches x {arms.Length} arms, 12 bots, red hybrid vs blue scripted, {cap}-minute cap");

        Parallel.ForEach(work, item =>
        {
            var (map, i, arm) = item;
            var shipped = networks.Value!;

            var options = HeadlessMatchOptions.Default with
            {
                Map = map,
                Bots = 12,
                Seed = (uint)(9300 + (map.Length * 131) + (i * 37)),
                CapLimit = 1,
                TimeLimitMinutes = cap,
                KillLimit = 40,
                MaxTicks = 30 * 60 * cap,
                AllowedClasses = Classes,
                Brain = arm.Name == "control"
                    ? null
                    : BotBrainSpec.Of(shipped) with
                    {
                        ReleaseRange = arm.Release,
                        EngageNearOwnGoal = arm.NearGoal,
                    },
                HybridTeam = Team.Red,
            };

            var match = HeadlessMatch.Load(options, maps, nav);
            long red = 0, blue = 0;

            while (match.Outcome == MatchOutcome.Running)
            {
                match.Step();

                for (var slot = 1; slot < WorldState.MaxSlots; slot++)
                {
                    if (!match.Events.Died[slot])
                        continue;

                    if (match.State.Players[slot].Team == Team.Red)
                        red++;
                    else
                        blue++;
                }
            }

            long micro = 0, scripted = 0;

            for (var slot = 1; slot < WorldState.MaxSlots; slot++)
            {
                if (match.HybridBotFor(slot) is not { } bot)
                    continue;

                micro += bot.MicroTicks;
                scripted += bot.ScriptedTicks;
            }

            var result = match.Result;
            Team? winner = result.Outcome == MatchOutcome.Decided ? result.Winner : null;
            results.Add(new Observation(map, i, arm.Name, red, blue, winner, micro, scripted));
        });

        var all = results.ToList();

        Console.WriteLine();
        Console.WriteLine($"{"arm",-9} {"n",5} {"death share",12} {"win share",10} {"decided",8} {"control",8}   {"vs noband: death",18} {"+/-95%",7} {"win",8} {"+/-95%",7}");

        var reference = all.Where(o => o.Arm == "noband").ToDictionary(o => (o.Map, o.Index));

        foreach (var arm in arms)
        {
            var rows = all.Where(o => o.Arm == arm.Name).ToList();
            var red = rows.Sum(o => o.Red);
            var deaths = rows.Sum(o => o.Red + o.Blue);
            var decided = rows.Count(o => o.Winner is not null);
            var redWins = rows.Count(o => o.Winner == Team.Red);
            var micro = rows.Sum(o => o.Micro);
            var ticks = micro + rows.Sum(o => o.Scripted);

            var line = $"{arm.Name,-9} {rows.Count,5} {Pct(red, deaths),12} {Pct(redWins, decided),10} {decided,8} {Pct(micro, ticks),8}";

            if (arm.Name is not ("noband" or "control") && reference.Count > 0)
            {
                var (dDeath, ciDeath) = DeathDifference(rows, reference);
                var (dWin, ciWin) = WinDifference(rows, reference);
                line += $"   {dDeath * 100,18:+0.00;-0.00} {ciDeath * 100,7:0.00} {dWin * 100,8:+0.00;-0.00} {ciWin * 100,7:0.00}";
            }

            Console.WriteLine(line);
        }

        Console.WriteLine("death and win columns are percentage points; death share is red's share of all deaths (lower is red doing well)");
    }

    private static string Pct(long part, long whole) =>
        whole == 0 ? "-" : (part * 100.0 / whole).ToString("0.00", CultureInfo.InvariantCulture) + "%";

    /// <summary>Mean of per-match red death share differences, and a 95% half-width.</summary>
    private static (double Mean, double Half) DeathDifference(List<Observation> rows, Dictionary<(string, int), Observation> reference)
    {
        var diffs = new List<double>();

        foreach (var o in rows)
        {
            if (!reference.TryGetValue((o.Map, o.Index), out var b) || o.Red + o.Blue == 0 || b.Red + b.Blue == 0)
                continue;

            diffs.Add((o.Red / (double)(o.Red + o.Blue)) - (b.Red / (double)(b.Red + b.Blue)));
        }

        if (diffs.Count < 2)
            return (double.NaN, double.NaN);

        var mean = diffs.Average();
        var sd = Math.Sqrt(diffs.Sum(d => (d - mean) * (d - mean)) / (diffs.Count - 1));
        return (mean, 1.96 * sd / Math.Sqrt(diffs.Count));
    }

    /// <summary>Difference in red's win share of decided matches, with a paired-bootstrap 95% half-width.</summary>
    private static (double Mean, double Half) WinDifference(List<Observation> rows, Dictionary<(string, int), Observation> reference)
    {
        var pairs = rows
            .Where(o => reference.ContainsKey((o.Map, o.Index)))
            .Select(o => (Arm: o.Winner, Base: reference[(o.Map, o.Index)].Winner))
            .ToArray();

        static double Share(IEnumerable<Team?> winners)
        {
            var decided = 0;
            var red = 0;

            foreach (var w in winners)
            {
                if (w is null)
                    continue;

                decided++;

                if (w == Team.Red)
                    red++;
            }

            return decided == 0 ? double.NaN : red / (double)decided;
        }

        var point = Share(pairs.Select(p => p.Arm)) - Share(pairs.Select(p => p.Base));
        var random = new Random(1);
        var samples = new double[1000];

        for (var s = 0; s < samples.Length; s++)
        {
            var pick = new (Team? Arm, Team? Base)[pairs.Length];

            for (var k = 0; k < pick.Length; k++)
                pick[k] = pairs[random.Next(pairs.Length)];

            samples[s] = Share(pick.Select(p => p.Arm)) - Share(pick.Select(p => p.Base));
        }

        Array.Sort(samples);
        return (point, (samples[974] - samples[25]) / 2);
    }
}
