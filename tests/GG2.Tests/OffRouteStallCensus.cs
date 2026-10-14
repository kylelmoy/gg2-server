using GG2.Bots;
using GG2.Protocol;
using GG2.Server;
using GG2.Simulation;

namespace GG2.Tests;

/// <summary>
/// How often bots stand off their route, re-planning and pressing nothing, and
/// why the search from where they stand fails.
/// </summary>
/// <remarks>
/// A measurement, not a gate; skipped in the suite. <c>gg2-rl</c>
/// <c>docs/PLAN.md</c>, N10: 94 stalls of 2 seconds or more in these 24 matches
/// before, none after.
/// </remarks>
public sealed class OffRouteStallCensus(Xunit.Abstractions.ITestOutputHelper output)
{
    private static readonly string[] Maps =
    [
        "ctf_2dfort", "ctf_conflict", "ctf_classicwell", "ctf_truefort", "ctf_waterway", "koth_corinth",
        "koth_harvest", "koth_valley", "ctf_oldfort", "cp_dirtbowl", "cp_egypt", "ctf_eiger",
    ];

    [Fact(Skip = "a census: run by hand")]
    public void Census()
    {
        var maps = MapDirectory.Resolve();
        var navDir = MapDirectory.ResolveNav();
        var causes = new SortedDictionary<string, int>(StringComparer.Ordinal);
        long botTicks = 0, stalled = 0;
        int episodes = 0, longEpisodes = 0;

        foreach (var seed in new uint[] { 7, 11 })
        {
            foreach (var map in Maps)
            {
                var options = HeadlessMatchOptions.Default with { Map = map, Seed = seed, MaxTicks = 9000, Difficulty = 3 };
                var match = HeadlessMatch.Load(options, maps, navDir);
                var graph = match.Nav!;
                var search = new NavSearch(graph);
                var route = new List<int>();
                var lastOff = new int[WorldState.MaxSlots];
                var run = new int[WorldState.MaxSlots];

                while (match.Outcome == MatchOutcome.Running)
                {
                    match.Step();
                    ref readonly var world = ref match.State;

                    for (var slot = 0; slot < WorldState.MaxSlots; slot++)
                    {
                        if (!world.Alive[slot] || ScriptedBot.Of(match.SourceOf(slot)) is not { } bot)
                        {
                            run[slot] = 0;
                            continue;
                        }

                        botTicks++;
                        var off = bot.Path.OffRouteFires;

                        if (off == lastOff[slot])
                        {
                            run[slot] = 0;
                            continue;
                        }

                        lastOff[slot] = off;
                        run[slot]++;

                        if (run[slot] >= 10)
                            stalled++;

                        if (run[slot] == 60)
                            longEpisodes++;

                        if (run[slot] != 10)
                            continue;

                        episodes++;
                        ref readonly var c = ref world.Characters[slot];
                        var here = graph.NodeFromWorld(c.X, c.Y);
                        var goal = graph.NodeFromWorld(bot.Path.GoalX, bot.Path.GoalY);
                        var banned = here >= 0 && goal >= 0 && search.Find(here, goal, bot.Query, route, bot.Path.Blacklist);
                        var open = here >= 0 && goal >= 0 && search.Find(here, goal, bot.Query, route);
                        var key = $"route with bans {banned}, without {open}, target {bot.Combat.Target.Exists}";
                        causes[key] = causes.GetValueOrDefault(key) + 1;
                    }
                }
            }
        }

        output.WriteLine($"bot ticks {botTicks}: {episodes} stalls of 10+ ticks, {longEpisodes} of 60+, {stalled} ticks stalled");
        foreach (var (key, count) in causes)
            output.WriteLine($"{count,5} {key}");
    }
}
