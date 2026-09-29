using GG2.Bots;
using GG2.Server;
using GG2.Simulation;
using Xunit.Abstractions;

namespace GG2.Tests;

/// <summary>
/// How often a bot shuttles at a takeoff instead of jumping.
/// </summary>
/// <remarks>
/// A run-up is a bot backing away from a jump to build speed for it, and one is
/// ordinary. What is not ordinary is starting a <em>second</em> run-up for the
/// same edge: that means the first one delivered the bot back to the gate at a
/// speed the gate refused again, which it will keep doing. A repeat is the
/// shuttle a player sees as a bot dithering on a step.
/// <para>
/// <b>Counted per bot, per edge, and only consecutively</b> — a bot that comes
/// back to the same edge later in the round, having been somewhere else in
/// between, is not shuttling. The streak resets whenever the edge changes.
/// </para>
/// <para>
/// A measurement rather than a gate, like <c>UnderFireCensusTests</c>. Numbers in
/// <c>docs/TRAPS-BOTS.md</c>, "a gate that fires before the run is finished".
/// </para>
/// </remarks>
public sealed class RunupCensus(ITestOutputHelper output)
{
    [Fact(Skip = "Measurement, not a gate. Numbers in docs/TRAPS-BOTS.md.")]
    public void Census()
    {
        var maps = MapDirectory.Resolve();
        var nav = MapDirectory.ResolveNav();

        var names = Directory
            .EnumerateFiles(maps, "*.png")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n is { Length: > 0 })
            .Select(n => n!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        long totalTicks = 0, totalRunup = 0, totalEpisodes = 0, totalRepeats = 0, totalWorst = 0;

        foreach (var name in names)
        {
            long runupTicks = 0, episodes = 0, repeats = 0;
            var worst = 0;

            foreach (var seed in new uint[] { 1, 2 })
            {
                var options = HeadlessMatchOptions.Default with
                {
                    Map = name, Seed = seed, MaxTicks = 9000,
                };

                var match = HeadlessMatch.Load(options, maps, nav);

                // Per slot: the edge the last run-up was for, and how many times
                // in a row a run-up has been started for that same edge.
                var lastEdge = new (int From, int To)[WorldState.MaxSlots];
                var streak = new int[WorldState.MaxSlots];
                var inRunup = new bool[WorldState.MaxSlots];

                while (match.Outcome == MatchOutcome.Running)
                {
                    match.Step();
                    totalTicks++;

                    for (var slot = 0; slot < WorldState.MaxSlots; slot++)
                    {
                        if (match.SourceOf(slot) is not ScriptedBot bot)
                            continue;

                        var live = bot.Path.RunupAt >= 0;

                        if (live)
                            runupTicks++;

                        if (live && !inRunup[slot])
                        {
                            episodes++;

                            var edge = (bot.Path.EdgeFrom, bot.Path.EdgeTo);

                            if (edge == lastEdge[slot])
                            {
                                streak[slot]++;
                                repeats++;
                                worst = Math.Max(worst, streak[slot] + 1);
                            }
                            else
                            {
                                lastEdge[slot] = edge;
                                streak[slot] = 0;
                            }
                        }

                        inRunup[slot] = live;
                    }
                }
            }

            totalRunup += runupTicks;
            totalEpisodes += episodes;
            totalRepeats += repeats;
            totalWorst = Math.Max(totalWorst, worst);

            output.WriteLine(
                $"{name,-22} runup ticks {runupTicks,7} episodes {episodes,6} "
                + $"repeats {repeats,6} worst streak {worst,3}");
        }

        output.WriteLine("");
        output.WriteLine(
            $"TOTAL bot-ticks {totalTicks} runup {totalRunup} episodes {totalEpisodes} "
            + $"repeats {totalRepeats} ({100.0 * totalRepeats / Math.Max(1, totalEpisodes):0.0}% of episodes) "
            + $"worst streak {totalWorst}");
    }
}
