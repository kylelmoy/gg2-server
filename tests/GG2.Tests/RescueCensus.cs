using System.Collections.Concurrent;
using GG2.Bots;

namespace GG2.Tests;

/// <summary>
/// What the stranding rescue does on the shipping sweep: how many start, find a
/// plan, arrive, and why the rest give up.
/// </summary>
/// <remarks>
/// A measurement, not a gate; skipped in the suite. <c>gg2-rl</c>
/// <c>docs/PLAN.md</c>, NS3.
/// </remarks>
public sealed class RescueCensus(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact(Skip = "a census: run by hand")]
    public void Census()
    {
        ReferenceTree.RequireNavCache();

        var states = new ConcurrentDictionary<BotPathState, Trace>();
        var flights = EdgeFlights.SweepAll(
            ReferenceTree.MapsDirectory, ReferenceTree.NavCacheDirectory, distances: true,
            threads: Math.Max(1, Environment.ProcessorCount - 2),
            onTick: (slot, world, state) =>
            {
                var t = states.GetOrAdd(state, _ => new Trace(world.Map.Name));
                t.Ticks++;

                if (t.Ticks == 1)
                {
                    t.First = (state.EdgeFrom, state.EdgeTo);
                    foreach (var e in world.Nav!.EdgesFrom(state.EdgeFrom < 0 ? 0 : state.EdgeFrom))
                    {
                        if (e.To == state.EdgeTo)
                        {
                            t.Type = e.Type.ToString();
                            break;
                        }
                    }
                }
                ref readonly var c = ref world.State.Characters[slot];
                var node = world.Nav!.NodeFromWorld(c.X, c.Y);
                var now = (state.Rescues, state.RescuesFound, state.RescuesArrived, state.BlacklistFires);

                if (now != t.Last)
                {
                    t.Log.Add($"t{t.Ticks} rescues/found/arrived/bans {now} at ({c.X:0},{c.Y:0}) node {node} edge {state.EdgeFrom}->{state.EdgeTo} rescuing {state.RescueFrom}->{state.RescueTo}");
                    t.Last = now;
                }

                t.End = $"end ({c.X:0},{c.Y:0}) node {node} goal x {state.GoalX:0} edge {state.EdgeFrom}->{state.EdgeTo} stuck {state.StuckFires} off-route {state.OffRouteFires}";
            });

        output.WriteLine(EdgeFlights.Summarise(flights));

        var all = states.Keys.ToList();
        var stranded = states.Where(p => !p.Key.Arrived).ToList();
        foreach (var g in stranded.GroupBy(p => (p.Value.Type, Bans: p.Key.BlacklistFires > 0, Rescued: p.Key.Rescues > 0, Found: p.Key.RescuesFound > 0)).OrderBy(g => g.Key.ToString()))
            output.WriteLine($"stranded {g.Key}: {g.Count()}");
        foreach (var (state, t) in stranded.Where(p => p.Value.Type is "Jump" or "Walk" or "Fall" or "DoubleJump" && p.Key.Rescues == 0).Take(12))
        {
            output.WriteLine($"== {t.Map} {t.Type} {t.First} {t.End} bans {state.BlacklistFires} ticks {t.Ticks}");
            foreach (var line in t.Log)
                output.WriteLine("   " + line);
        }

        var after = states.Where(p => p.Key.RescuesArrived > 0 && !p.Key.Arrived).ToList();
        output.WriteLine($"rescue arrived but the flight stranded: {after.Count} of {states.Count(p => p.Key.RescuesArrived > 0)}");
        foreach (var (state, t) in after.Take(25))
        {
            output.WriteLine($"-- {t.Map} {t.End}");
            foreach (var line in t.Log)
                output.WriteLine("   " + line);
        }

        var rescued = all.Where(s => s.Rescues > 0).ToList();
        output.WriteLine($"flights with a rescue {rescued.Count}; rescues {all.Sum(s => s.Rescues)}");
        output.WriteLine($"found {all.Sum(s => s.RescuesFound)}, arrived {all.Sum(s => s.RescuesArrived)}, off track {all.Sum(s => s.RescuesAbandoned)}, "
            + $"never still {all.Sum(s => s.RescuesUnsettled)}, moved while searching {all.Sum(s => s.RescuesMoved)}");
        output.WriteLine($"not found {all.Sum(s => s.Rescues - s.RescuesFound - s.RescuesUnsettled - s.RescuesMoved - (s.Rescuing ? 1 : 0))}; still running at the end {all.Count(s => s.Rescuing)}");

        var ticks = all.Sum(s => (long)s.RescueSearchTicks);
        var steps = all.Sum(s => s.RescueStates);
        output.WriteLine($"search ticks {ticks:N0}, states {steps:N0}, {(ticks > 0 ? steps / (double)ticks : 0):N0} a tick");
    }

    private sealed class Trace(string map)
    {
        public string Map { get; } = map;
        public int Ticks { get; set; }
        public List<string> Log { get; } = [];
        public (int, int, int, int) Last { get; set; }
        public string End { get; set; } = "";
        public (int, int) First { get; set; }
        public string Type { get; set; } = "?";
    }
}
