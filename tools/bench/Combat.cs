using System.Diagnostics;
using Gg2.Bots;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Bench;

/// <summary>
/// What a twelve-bot match costs a tick, and how many A* searches it runs to
/// get there — the two numbers every behavioural nav change trades against
/// each other.
/// </summary>
/// <remarks>
/// ⛔ <b>The first SECONDS of this process run 5-30x slow, and it is NOT the
/// JIT.</b> A settled match on this box is ~12 us a tick; the first ~15,000
/// ticks read 50-260, with every region scaling together and the A* call count
/// unchanged — a clock or scheduler ramp, reproducible across runs. The
/// README's trap 3 (tier-1 promotion, ~200,000 iterations) and trap 8 (the
/// first row of a ladder) do not cover it: this outlasts both by an order of
/// magnitude in wall clock, and a mode that warmed by a tick count would report
/// the ramp as its measurement. So the warm-up here is a <b>stability test on
/// wall time</b> — whole matches until three consecutive ones agree within 8% —
/// and <c>settled=</c> says whether it ever got there rather than assuming it.
/// ⚠️ <b>A number from a run printing <c>settled=False</c> is the ramp.</b>
/// <para>
/// ⚠️ <b>Report the MINIMUM across matches, not the mean.</b> Everything this
/// mode competes with on the machine moves the number one way, so the mean is a
/// measure of what else was running. The median is printed beside it so the two
/// can be compared; when they disagree by more than a few percent the box was
/// busy and the run should be repeated.
/// </para>
/// <para>
/// ⛔ <b><c>searches</c> is the quantity to change, and <c>goals</c> says which
/// half of it moved.</b> Measured 2026-09-02, twelve bots on
/// <c>ctf_truefort</c>: 321 searches a thousand ticks, of which <b>181 are a
/// goal reissue</b> (<see cref="BotPathState.GoalsSet"/> — <c>SetGoal</c> arms
/// an immediate re-plan), 97 the re-plan timer and 43 a lost route. A change
/// that moves the cadence and not the objective layer is working on the
/// smaller half.
/// </para>
/// </remarks>
public static class CombatBench
{
    private static double PlayOne(HeadlessMatch match, out int ticks, out long replans, out long goals)
    {
        match.Reset();

        var sw = Stopwatch.StartNew();
        match.Run();
        sw.Stop();

        ticks = match.Tick;
        replans = 0;
        goals = 0;

        for (var slot = 0; slot < WorldState.MaxSlots; slot++)
        {
            if (match.SourceOf(slot) is not ScriptedBot bot)
                continue;

            replans += bot.Path.Replans;
            goals += bot.Path.GoalsSet;
        }

        return sw.Elapsed.TotalMilliseconds * 1000 / ticks;
    }

    public static void Run(string maps, string? nav, string map, int matches)
    {
        var options = HeadlessMatchOptions.Default with { Map = map };
        var match = HeadlessMatch.Load(options, maps, nav);

        // ⚠️ Whole matches, not ticks: a match is the unit whose cost is stable,
        // and the ramp above is what makes a tick count the wrong warm-up.
        var recent = new double[3];
        var settled = false;

        for (var i = 0; i < 40 && !settled; i++)
        {
            recent[i % 3] = PlayOne(match, out _, out _, out _);

            if (i < 4)
                continue;

            var lo = Math.Min(recent[0], Math.Min(recent[1], recent[2]));
            var hi = Math.Max(recent[0], Math.Max(recent[1], recent[2]));
            settled = hi <= lo * 1.08;
        }

        var us = new List<double>();
        long totalReplans = 0;
        long totalGoals = 0;
        long totalTicks = 0;

        for (var m = 0; m < matches; m++)
        {
            us.Add(PlayOne(match, out var ticks, out var replans, out var goals));
            totalReplans += replans;
            totalGoals += goals;
            totalTicks += ticks;
        }

        us.Sort();

        Console.WriteLine(
            $"map={map} bots={options.Bots} matches={matches} settled={settled}"
            + $"  replan={BotConstants.ReplanTicks} retarget={BotConstants.GoalRetargetDist}");
        Console.WriteLine(
            $"  us/tick  min {us[0]:F2}  med {us[us.Count / 2]:F2}  max {us[^1]:F2}"
            + $"   searches {(double)totalReplans / totalTicks * 1000:F1}/1000t"
            + $"   goals {(double)totalGoals / totalTicks * 1000:F1}/1000t"
            + $"   ticks {totalTicks}");
    }
}
