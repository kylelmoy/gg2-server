using GG2.Server;
using GG2.Protocol;
using GG2.Bots;
using GG2.Simulation;

namespace GG2.Tests;

/// <summary>
/// Which edges a held flight (N1) changes the outcome of, against the tracker
/// alone: the corpus swept twice, diffed edge by edge.
/// </summary>
/// <remarks>
/// A measurement, not a gate; skipped in the suite.
/// </remarks>
public sealed class HeldFlightCensus(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact(Skip = "a census: run by hand")]
    public void Census()
    {
        ReferenceTree.RequireNavCache();

        var threads = Math.Max(1, Environment.ProcessorCount - 2);
        var off = new Dictionary<(string, int), IReadOnlyList<EdgeFlight>>();
        var on = new Dictionary<(string, int), IReadOnlyList<EdgeFlight>>();

        EdgeFlights.SweepAll(ReferenceTree.MapsDirectory, ReferenceTree.NavCacheDirectory, distances: true, threads: threads,
            onGraph: (map, area, flights) => off[(map, area)] = flights.ToArray(), heldFlight: false);
        EdgeFlights.SweepAll(ReferenceTree.MapsDirectory, ReferenceTree.NavCacheDirectory, distances: true, threads: threads,
            onGraph: (map, area, flights) => on[(map, area)] = flights.ToArray(), heldFlight: true);

        var lost = new List<string>();
        var won = 0;
        var byType = new Dictionary<NavEdgeType, (int Lost, int Won)>();
        var ticksOff = 0L;
        var ticksOn = 0L;

        foreach (var (key, a) in off)
        {
            var b = on[key];

            for (var i = 0; i < a.Count; i++)
            {
                var x = a[i];
                var y = b[i];

                if (x.Measured && y.Measured)
                {
                    ticksOff += x.Ticks;
                    ticksOn += y.Ticks;
                }

                if (x.Outcome == EdgeFlightOutcome.Arrived && y.Outcome == EdgeFlightOutcome.Stranded)
                {
                    lost.Add($"{key.Item1}/{key.Item2} e{x.Edge} {x.Type} {x.From}->{x.To} ticks {x.Ticks} vs {y.Ticks} miss {y.MissX:0.#}");
                    var t = byType.GetValueOrDefault(x.Type);
                    byType[x.Type] = (t.Lost + 1, t.Won);
                }
                else if (x.Outcome == EdgeFlightOutcome.Stranded && y.Outcome == EdgeFlightOutcome.Arrived)
                {
                    won++;
                    var t = byType.GetValueOrDefault(x.Type);
                    byType[x.Type] = (t.Lost, t.Won + 1);
                }
            }
        }

        output.WriteLine($"lost {lost.Count}, won {won}; ticks on both-measured edges {ticksOff} -> {ticksOn}");
        foreach (var (type, (l, w)) in byType.OrderBy(kv => kv.Key))
            output.WriteLine($"  {type}: lost {l}, won {w}");
        foreach (var line in lost.Take(60))
            output.WriteLine(line);
    }

    /// <summary>The shipping arm with and without face hops (N5), diffed edge by edge.</summary>
    [Fact]
    public void FaceHops()
    {
        ReferenceTree.RequireNavCache();

        var threads = Math.Max(1, Environment.ProcessorCount - 2);
        var off = new Dictionary<(string, int), IReadOnlyList<EdgeFlight>>();
        var on = new Dictionary<(string, int), IReadOnlyList<EdgeFlight>>();

        EdgeFlights.SweepAll(ReferenceTree.MapsDirectory, ReferenceTree.NavCacheDirectory, distances: true, threads: threads,
            onGraph: (map, area, flights) => off[(map, area)] = flights.ToArray());
        EdgeFlights.SweepAll(ReferenceTree.MapsDirectory, ReferenceTree.NavCacheDirectory, distances: true, threads: threads,
            onGraph: (map, area, flights) => on[(map, area)] = flights.ToArray(), faceHops: true);

        var lost = new List<string>();
        var won = 0;
        var changed = 0;
        var extra = new List<int>();

        foreach (var (key, a) in off)
        {
            var b = on[key];

            for (var i = 0; i < a.Count; i++)
            {
                var x = a[i];
                var y = b[i];

                if (x.Ticks != y.Ticks)
                {
                    changed++;
                    if (x.Measured && y.Measured)
                        extra.Add(y.Ticks - x.Ticks);
                }

                if (x.Outcome == EdgeFlightOutcome.Arrived && y.Outcome == EdgeFlightOutcome.Stranded)
                    lost.Add($"{key.Item1}/{key.Item2} e{x.Edge} {x.Type} {x.From}->{x.To} ticks {x.Ticks} vs {y.Ticks} miss {y.MissX:0.#}");
                else if (x.Outcome == EdgeFlightOutcome.Stranded && y.Outcome == EdgeFlightOutcome.Arrived)
                    won++;
            }
        }

        extra.Sort();
        output.WriteLine($"changed {changed}, lost {lost.Count}, won {won}");
        if (extra.Count > 0)
            output.WriteLine($"extra ticks on changed edges: p10 {extra[extra.Count / 10]} median {extra[extra.Count / 2]} p90 {extra[extra.Count * 9 / 10]}");
        foreach (var line in lost.Take(40))
            output.WriteLine(line);
    }

    /// <summary>The shipping arm and the scout arm with and without two-phase plans (N4), diffed edge by edge.</summary>
    [Fact(Skip = "a census: run by hand")]
    public void SwitchPlans()
    {
        ReferenceTree.RequireNavCache();
        var threads = Math.Max(1, Environment.ProcessorCount - 2);

        foreach (var (label, types, cls) in new (string, NavEdgeType[]?, PlayerClass?)[]
        {
            ("shipping", null, null),
            ("scout", [NavEdgeType.Jump, NavEdgeType.DoubleJump], PlayerClass.Scout),
        })
        {
            var off = new Dictionary<(string, int), IReadOnlyList<EdgeFlight>>();
            var on = new Dictionary<(string, int), IReadOnlyList<EdgeFlight>>();

            EdgeFlights.SweepAll(ReferenceTree.MapsDirectory, ReferenceTree.NavCacheDirectory, types, distances: true, threads: threads,
                onGraph: (map, area, flights) => off[(map, area)] = flights.ToArray(), flyAs: cls, heldSwitchPlans: false);
            EdgeFlights.SweepAll(ReferenceTree.MapsDirectory, ReferenceTree.NavCacheDirectory, types, distances: true, threads: threads,
                onGraph: (map, area, flights) => on[(map, area)] = flights.ToArray(), flyAs: cls, heldSwitchPlans: true);

            var lost = new List<string>();
            var won = 0;
            var slower = 0L;

            foreach (var (key, a) in off)
            {
                var b = on[key];

                for (var i = 0; i < a.Count; i++)
                {
                    if (a[i].Measured && b[i].Measured)
                        slower += b[i].Ticks - a[i].Ticks;

                    if (a[i].Outcome == EdgeFlightOutcome.Arrived && b[i].Outcome == EdgeFlightOutcome.Stranded)
                        lost.Add($"{key.Item1}/{key.Item2} e{a[i].Edge} {a[i].Type} {a[i].From}->{a[i].To} ticks {a[i].Ticks} vs {b[i].Ticks} miss {b[i].MissX:0.#}");
                    else if (a[i].Outcome == EdgeFlightOutcome.Stranded && b[i].Outcome == EdgeFlightOutcome.Arrived)
                        won++;
                }
            }

            output.WriteLine($"{label}: lost {lost.Count}, won {won}, ticks on both-measured {slower:+0;-0}");
            foreach (var line in lost.OrderBy(l => l.Length).Take(40))
                output.WriteLine("  " + line);
        }
    }

    /// <summary>
    /// A scout's plain and double jumps, with and without second-jump planning
    /// (N3), diffed edge by edge. The shipping sweep flies plain jumps as a
    /// soldier, so it cannot see this.
    /// </summary>
    [Fact(Skip = "a census: run by hand")]
    public void ScoutSecondJumps()
    {
        ReferenceTree.RequireNavCache();

        var threads = Math.Max(1, Environment.ProcessorCount - 2);
        NavEdgeType[] types = [NavEdgeType.Jump, NavEdgeType.DoubleJump];
        var off = new Dictionary<(string, int), IReadOnlyList<EdgeFlight>>();
        var on = new Dictionary<(string, int), IReadOnlyList<EdgeFlight>>();

        EdgeFlights.SweepAll(ReferenceTree.MapsDirectory, ReferenceTree.NavCacheDirectory, types, distances: true, threads: threads,
            onGraph: (map, area, flights) => off[(map, area)] = flights.ToArray(), flyAs: PlayerClass.Scout, heldDoubles: false);
        EdgeFlights.SweepAll(ReferenceTree.MapsDirectory, ReferenceTree.NavCacheDirectory, types, distances: true, threads: threads,
            onGraph: (map, area, flights) => on[(map, area)] = flights.ToArray(), flyAs: PlayerClass.Scout, heldDoubles: true);

        output.WriteLine($"off: {EdgeFlightTally.Of(off.Values.SelectMany(f => f).ToList())}");
        output.WriteLine($"on:  {EdgeFlightTally.Of(on.Values.SelectMany(f => f).ToList())}");

        var lost = new List<string>();
        var won = 0;

        foreach (var (key, a) in off)
        {
            var b = on[key];

            for (var i = 0; i < a.Count; i++)
            {
                if (a[i].Outcome == EdgeFlightOutcome.Arrived && b[i].Outcome == EdgeFlightOutcome.Stranded)
                    lost.Add($"{key.Item1}/{key.Item2} e{a[i].Edge} {a[i].Type} {a[i].From}->{a[i].To} ticks {a[i].Ticks} vs {b[i].Ticks} miss {b[i].MissX:0.#}");
                else if (a[i].Outcome == EdgeFlightOutcome.Stranded && b[i].Outcome == EdgeFlightOutcome.Arrived)
                    won++;
            }
        }

        output.WriteLine($"lost {lost.Count}, won {won}");
        foreach (var line in lost.Take(60))
            output.WriteLine(line);
    }
}

/// <summary>
/// What bots' out-of-combat take-offs in a match are flown by, and for those
/// the tracker flies, whether any held plan from the first air tick lands on
/// the edge's destination (N3).
/// </summary>
public sealed class TakeoffCensus(Xunit.Abstractions.ITestOutputHelper output)
{
    private static readonly string[] Maps =
        ["ctf_2dfort", "ctf_conflict", "ctf_classicwell", "ctf_truefort", "ctf_waterway", "koth_corinth", "koth_harvest", "koth_valley"];

    [Fact(Skip = "a census: run by hand")]
    public void ScoutTakeoffsInAMatch() => Census([PlayerClass.Scout]);

    [Fact(Skip = "a census: run by hand")]
    public void EveryClassTakeoffsInAMatch() => Census(null);

    private void Census(PlayerClass[]? classes)
    {
        var byClass = new SortedDictionary<string, int[]>(StringComparer.Ordinal);
        var maps = MapDirectory.Resolve();
        var navDir = MapDirectory.ResolveNav();
        var counts = new SortedDictionary<string, List<double>>(StringComparer.Ordinal);
        var examples = new List<string>();

        void Add(string key, double speed)
        {
            if (!counts.TryGetValue(key, out var list))
                counts[key] = list = [];
            list.Add(speed);
        }

        foreach (var map in Maps)
        {
            var options = HeadlessMatchOptions.Default with
            {
                Map = map, Seed = 7, MaxTicks = 9000, Difficulty = 3, AllowedClasses = classes,
            };

            var match = HeadlessMatch.Load(options, maps, navDir);
            var graph = match.Nav!;
            var wasGround = new bool[WorldState.MaxSlots];

            while (match.Outcome == MatchOutcome.Running)
            {
                match.Step();
                ref readonly var world = ref match.State;

                for (var slot = 0; slot < WorldState.MaxSlots; slot++)
                {
                    if (!world.Alive[slot] || ScriptedBot.Of(match.SourceOf(slot)) is not { } bot)
                    {
                        wasGround[slot] = false;
                        continue;
                    }

                    ref readonly var c = ref world.Characters[slot];
                    var tookOff = wasGround[slot] && !c.OnGround && c.VSpeed < 0;
                    wasGround[slot] = c.OnGround;

                    if (!tookOff || NearestEnemy(in world, slot) <= 300)
                        continue;

                    var path = bot.Path;
                    var speed = Math.Abs(c.HSpeed);
                    var cls = c.Class.ToString();
                    if (!byClass.TryGetValue(cls, out var tally))
                        byClass[cls] = tally = new int[3];
                    tally[path.HeldDir != 0 ? 0 : 1]++;

                    if (path.HeldDir != 0)
                    {
                        Add(path.HeldRejump >= 0 ? "held, double" : "held, single", speed);
                        continue;
                    }

                    NavEdge? found = null;
                    if (path.EdgeFrom >= 0 && path.EdgeTo >= 0)
                    {
                        foreach (var e in graph.EdgesFrom(path.EdgeFrom))
                        {
                            if (e.To == path.EdgeTo && (found is null || e.Type is NavEdgeType.Jump or NavEdgeType.DoubleJump))
                                found = e;
                        }
                    }

                    if (found is not { } edge || edge.Type is not (NavEdgeType.Jump or NavEdgeType.DoubleJump))
                    {
                        Add($"other: {found?.Type.ToString() ?? "no edge"}", speed);
                        continue;
                    }

                    var dir = edge.To >= 0 && graph.Nodes[edge.To].X0 > edge.Takeoff ? 1 : graph.Nodes[edge.To].X1 < edge.Takeoff ? -1 : Math.Sign(c.HSpeed);
                    var plan = NavGraph.ColumnWorldX(edge.Takeoff) + (dir * edge.Bucket * edge.Ticks);
                    var best = double.PositiveInfinity;

                    foreach (var key in new[] { dir, 0, -dir })
                    {
                        foreach (var r in new[] { 0, 3, 6, 9 })
                        {
                            if (FlightPredictor.Land(match.Collision, in c, key, key, false, r, out var x, out var y, out _)
                                && graph.NodeFromWorld(x, y) == edge.To)
                            {
                                best = Math.Min(best, (x - plan) * dir);
                            }
                        }
                    }

                    if (double.IsPositiveInfinity(best))
                    {
                        var fix = "none";
                        foreach (var (early, late) in new[] { (0, dir), (-dir, dir), (dir, 0), (dir, -dir), (0, 0) })
                        {
                            for (var k = 1; k <= 24 && fix == "none"; k++)
                            {
                                foreach (var r in new[] { 0, 3, 6, 9, 12 })
                                {
                                    if (FlightPredictor.Land(match.Collision, in c, early, early, k, late, false, r, out var x2, out var y2, out _)
                                        && graph.NodeFromWorld(x2, y2) == edge.To)
                                    {
                                        fix = $"{(early == 0 ? "none" : early == dir ? "toward" : "away")} then {(late == 0 ? "none" : late == dir ? "toward" : "away")}";
                                        break;
                                    }
                                }
                            }

                            if (fix != "none")
                                break;
                        }

                        Add($"  nothing lands; a switch that does: {fix}", speed);
                        if (fix == "none" && examples.Count < 12)
                            examples.Add($"{map} e {edge.From}->{edge.To} {edge.Type} at ({c.X:0},{c.Y:0}) hs {c.HSpeed:0.0} takeoff col {edge.Takeoff} to [{graph.Nodes[edge.To].X0},{graph.Nodes[edge.To].X1}] row {graph.Nodes[edge.To].Row}");
                    }

                    var from = Math.Abs(c.X - NavGraph.ColumnWorldX(edge.Takeoff));
                    var where = from <= 36 ? "near take-off" : "far from take-off";
                    var land = double.IsPositiveInfinity(best) ? "nothing lands on next" : best <= 12 ? "lands within 12px" : "lands only past 12px";
                    Add($"tracker {edge.Type}, {where}, {land}", speed);
                }
            }
        }

        var total = counts.Values.Sum(l => l.Count);
        output.WriteLine($"{total} out-of-combat take-offs (sub-rows under \"nothing lands\" are counted twice)");
        foreach (var (key, list) in counts)
        {
            list.Sort();
            output.WriteLine($"{key,-70} {list.Count,6} {100.0 * list.Count / total,5:0.0}%  speed median {list[list.Count / 2]:0.00}");
        }

        foreach (var (cls, t) in byClass)
            output.WriteLine($"{cls,-10} held {t[0],6}  not held {t[1],6}  held share {100.0 * t[0] / Math.Max(1, t[0] + t[1]):0.0}%");

        foreach (var e in examples)
            output.WriteLine(e);
    }

    private static double NearestEnemy(in WorldState world, int self)
    {
        ref readonly var me = ref world.Characters[self];
        var best = double.PositiveInfinity;
        for (var j = 0; j < WorldState.MaxSlots; j++)
        {
            if (j == self || !world.Alive[j])
                continue;
            ref readonly var other = ref world.Characters[j];
            if (other.Team == me.Team)
                continue;
            best = Math.Min(best, Math.Sqrt(((other.X - me.X) * (other.X - me.X)) + ((other.Y - me.Y) * (other.Y - me.Y))));
        }

        return best;
    }
}
