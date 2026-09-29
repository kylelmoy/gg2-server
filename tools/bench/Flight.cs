using System.Diagnostics;
using System.Globalization;
using GG2.Bots;
using GG2.Protocol;
using GG2.Simulation;

namespace Bench;

/// <summary>
/// Can a bot actually follow what the generator emitted — per edge, and per
/// whole route.
/// </summary>
/// <remarks>
/// <b>Two questions, and they are not the same one at two scales.</b>
/// <list type="bullet">
/// <item>
/// <b>The per-edge sweep</b> (<c>BENCH_FLIGHT</c>) asks whether each edge is
/// traversable as the single hop it claims to be. It is complete over the corpus
/// and <b>cannot cover it</b>: only ~32% of edges are posable as a one-hop route
/// at all, because A* prefers going round.
/// </item>
/// <item>
/// <b>The route sample</b> (<c>BENCH_FLIGHT_ROUTES</c>) asks whether a bot sent
/// from somewhere to somewhere arrives. It covers edges in the context they are
/// actually used in — including every edge the per-edge sweep skips — and is a
/// sample rather than a census.
/// </item>
/// </list>
/// <para>
/// ⛔ <b>Neither is a match.</b> A flight is one bot in an empty world: no
/// enemies, no projectiles, no sentry, no crowd for occupancy to push apart. Every
/// one of those absences arrives as a legal-looking zero — docs/BOTS.md, "A duel
/// does not contain what a match contains".
/// </para>
/// </remarks>
public static class FlightBench
{
    /// <summary>
    /// Where the map PNGs are, for a sweep of THIS repository's nav corpus.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Not <c>EnvOptions.DefaultMaps</c>, and the difference is not
    /// cosmetic.</b> That points at the reference's <c>Included Files</c>, which
    /// does not carry the port-authored <c>gg_debug2</c> — and <c>nav/</c> ships a
    /// graph for it, so a corpus sweep against the reference's maps dies on a
    /// missing PNG. <c>GG2_MAPS</c> overrides; otherwise this walks up for the
    /// repository's own <c>maps/</c> and falls back to the reference.
    /// </remarks>
    public static string ResolveMaps(string fallback)
    {
        if (Environment.GetEnvironmentVariable("GG2_MAPS") is { Length: > 0 } explicitDirectory)
            return explicitDirectory;

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "maps");

            if (File.Exists(Path.Combine(candidate, "ctf_truefort.png")))
                return candidate;
        }

        return fallback;
    }

    /// <summary>The per-edge census.</summary>
    public static void Run(string maps, string nav, int budget, bool distances, int threads, string? csv, string? focus)
    {
        // ⛔ A different ARM, not a faster run: it changes what the bot can do on
        // the tick it reaches the takeoff column. See EdgeFlights.Fly's remark.
        var warm = Environment.GetEnvironmentVariable("BENCH_FLIGHT_WARM") == "1";

        var types = Enum.GetValues<NavEdgeType>();
        var perType = new Dictionary<NavEdgeType, EdgeFlightTally>();
        var perMap = new List<(string Key, EdgeFlightTally Tally)>();
        var measured = new List<(string Map, int Area, EdgeFlight Flight)>();
        var only = Focus(focus);

        Console.WriteLine(
            $"BENCH_FLIGHT budget={budget} distances={(distances ? "ON (the shipping arm)" : "off")} "
            + (warm ? "weapon=WARM (a different arm) " : string.Empty)
            + $"threads={(threads <= 0 ? Environment.ProcessorCount : threads)}"
            + (only.Count == 0 ? string.Empty : $" maps={string.Join(',', only)}"));
        Console.WriteLine();

        var sw = Stopwatch.StartNew();

        var flights = EdgeFlights.SweepAll(
            maps, nav, types, Team.Red, budget, distances, threads,
            include: only.Count == 0 ? null : (map, _) => only.Contains(map),
            warmWeapon: warm,
            onGraph: (map, area, graph) =>
            {
                // ⚠️ Ticks rather than milliseconds. onGraph runs after the
                // parallel sweep has finished, so there is no per-map wall time to
                // report — and a simulated-tick count is the machine-independent
                // cost anyway.
                perMap.Add(($"{map}_a{area}", EdgeFlightTally.Of(graph)));

                foreach (var flight in graph)
                {
                    perType[flight.Type] = perType.GetValueOrDefault(flight.Type).Add(in flight);

                    // ⛔ Every MEASURED flight, not only the failures. A stranding
                    // is only interpretable against the arrivals on the same map
                    // and the same edge kind — "these arcs demand more takeoff
                    // speed than the node affords" is not a statement one can make
                    // from the failures alone. Filtering to Stranded is one `awk`
                    // away; recovering the control is a re-run.
                    if (flight.Measured)
                        measured.Add((map, area, flight));
                }
            });

        sw.Stop();

        foreach (var (key, tally) in perMap)
        {
            Console.WriteLine(
                $"  {key,-22} cand={tally.Candidates,6} measured={tally.Measured,6} "
                + $"arrived={tally.Arrived,6} flown={tally.Flown,5} stranded={tally.Stranded,5} "
                + $"ticks={tally.Ticks,8}");
        }

        var whole = EdgeFlightTally.Of(flights);

        Console.WriteLine();
        Console.WriteLine($"TOTAL {whole}");
        Console.WriteLine($"      {sw.Elapsed.TotalSeconds:0.0}s wall");
        Console.WriteLine();

        foreach (var (type, tally) in perType.OrderBy(t => t.Key))
        {
            var rate = tally.Measured == 0 ? 0 : 100.0 * tally.Stranded / tally.Measured;

            Console.WriteLine(
                $"  {type,-12} cand={tally.Candidates,6} measured={tally.Measured,6} "
                + $"arrived={tally.Arrived,6} flown={tally.Flown,5} stranded={tally.Stranded,5} ({rate,4:0.0}% of measured)");
        }

        // ⛔ The unit of observation, not just the total. The totals lose which
        // edges failed, and every question this bench gets run for ("which arcs,
        // on which map") needs the rows — docs/BOTS.md, "Record the unit of
        // observation". The map is attached here rather than re-derived from the
        // flight list afterwards, because a second walk of the graphs is a second
        // implementation of the sweep's own ordering.
        if (csv is not null)
        {
            using var writer = new StreamWriter(csv);
            writer.WriteLine("map,area,edge,type,from,to,outcome,team,swapped,ticks,manoeuvres,blacklists,missx");

            foreach (var (map, area, flight) in measured)
            {
                writer.WriteLine(string.Join(',', [
                    map,
                    area.ToString(CultureInfo.InvariantCulture),
                    flight.Edge.ToString(CultureInfo.InvariantCulture),
                    flight.Type.ToString(),
                    flight.From.ToString(CultureInfo.InvariantCulture),
                    flight.To.ToString(CultureInfo.InvariantCulture),
                    flight.Outcome.ToString(),
                    flight.Team.ToString(),
                    flight.TeamSwapped ? "1" : "0",
                    flight.Ticks.ToString(CultureInfo.InvariantCulture),
                    flight.Manoeuvres.ToString(CultureInfo.InvariantCulture),
                    flight.Blacklists.ToString(CultureInfo.InvariantCulture),
                    flight.MissX.ToString("0.##", CultureInfo.InvariantCulture),
                ]));
            }

            Console.WriteLine();
            Console.WriteLine(
                $"{measured.Count} measured rows written to {csv} "
                + $"({measured.Count(m => m.Flight.Outcome == EdgeFlightOutcome.Stranded)} of them stranded)");
        }
    }

    /// <summary>
    /// The route sample: random node pairs, flown end to end.
    /// </summary>
    /// <remarks>
    /// <b>Why a sample and not a census.</b> All-pairs over the shipped corpus is
    /// 6.5 million ordered pairs — about five core-hours at the measured
    /// ~2.8 ms a route, which is a nightly run rather than a bench. The sample
    /// answers the same question with a stated n.
    /// <para>
    /// ⚠️ <b><c>noroute</c> is not a failure.</b> A nav graph describes the
    /// terrain, so it carries islands no route reaches — 100 of 500 pairs on
    /// <c>ctf_truefort</c>. docs/TRAPS-BOTS.md, "Island nodes".
    /// </para>
    /// </remarks>
    public static void Routes(string maps, string nav, int samples, int budget, bool distances, int seed, string? focus)
    {
        var only = Focus(focus);

        Console.WriteLine(
            $"BENCH_FLIGHT_ROUTES n={samples}/map budget={budget} "
            + $"distances={(distances ? "ON (the shipping arm)" : "off")} seed={seed}");
        Console.WriteLine();

        var totalPosed = 0;
        var totalArrived = 0;
        var totalNoRoute = 0;
        var sw = Stopwatch.StartNew();

        foreach (var (map, area) in NavCache.List(nav))
        {
            if (only.Count > 0 && !only.Contains(map))
                continue;

            var probe = ScenarioWorld.Load(map, area, maps, nav);
            var graph = probe.Nav;

            if (graph is null)
                continue;

            var set = distances ? NavDistanceSet.LoadOrDerive(nav, map, area, graph, out _) : null;

            // Its own stream, seeded per map: a sweep of one map has to be
            // reproducible without running the ones before it. ⚠️ Hashed by hand
            // because string.GetHashCode is randomised per PROCESS — seeding with
            // it makes a "seeded" bench produce a different sample every run.
            var rng = new Random(Seed(seed, map, area));
            var mapWatch = Stopwatch.StartNew();
            int posed = 0, arrived = 0, stranded = 0, noroute = 0, hops = 0;
            long ticks = 0;
            var covered = new HashSet<(int, int)>();

            for (var i = 0; i < samples; i++)
            {
                var from = rng.Next(graph.Nodes.Length);
                var to = rng.Next(graph.Nodes.Length);

                if (from == to)
                    continue;

                var flight = FlyRoute(map, area, maps, nav, graph, set, from, to, budget, covered, out var pathHops, out var routeTicks);

                ticks += routeTicks;

                switch (flight)
                {
                    case EdgeFlightOutcome.NoRoute: noroute++; break;
                    case EdgeFlightOutcome.Arrived: posed++; arrived++; hops += pathHops; break;
                    default: posed++; stranded++; hops += pathHops; break;
                }
            }

            mapWatch.Stop();
            totalPosed += posed;
            totalArrived += arrived;
            totalNoRoute += noroute;

            var share = posed == 0 ? 0 : 100.0 * arrived / posed;

            Console.WriteLine(
                $"  {map}_a{area,-2} posed={posed,5} arrived={arrived,5} ({share,5:0.0}%) stranded={stranded,4} "
                + $"noroute={noroute,4} hops~{(posed == 0 ? 0 : (double)hops / posed),5:0.0} "
                + $"edges-walked={covered.Count,6}/{graph.EdgeCount,-6} "
                + $"{mapWatch.Elapsed.TotalSeconds,6:0.0}s ({(posed == 0 ? 0 : mapWatch.Elapsed.TotalMilliseconds / posed),5:0.0}ms/route, {ticks} ticks)");
        }

        sw.Stop();

        var overall = totalPosed == 0 ? 0 : 100.0 * totalArrived / totalPosed;

        Console.WriteLine();
        Console.WriteLine(
            $"TOTAL posed={totalPosed} arrived={totalArrived} ({overall:0.0}%) "
            + $"noroute={totalNoRoute} in {sw.Elapsed.TotalSeconds:0.0}s");
    }

    private static EdgeFlightOutcome FlyRoute(
        string map,
        int area,
        string maps,
        string nav,
        NavGraph graph,
        NavDistanceSet? distances,
        int from,
        int to,
        int budget,
        HashSet<(int, int)> covered,
        out int hops,
        out int ticks)
    {
        hops = 0;
        ticks = 0;

        var follower = new PathFollower(graph) { Distances = distances };
        var query = EdgeFlights.QueryFor(NavEdgeType.RocketJump, Team.Red);
        var probe = new BotPathState();

        double X(int node) => NavGraph.ColumnWorldX((graph.Nodes[node].X0 + graph.Nodes[node].X1) / 2);

        if (!follower.SetGoal(probe, X(to), graph.StandY(to)))
            return EdgeFlightOutcome.GoalUnresolved;

        if (!follower.Plan(probe, X(from), graph.StandY(from), tick: 0, in query))
            return EdgeFlightOutcome.NoRoute;

        hops = probe.Path.Count - 1;

        for (var h = 0; h + 1 < probe.Path.Count; h++)
            covered.Add((probe.Path[h], probe.Path[h + 1]));

        var world = ScenarioWorld.Load(map, area, maps, nav);
        var state = new BotPathState();

        // ⛔ The terrain, or this probe flies a DIFFERENT follower from the one
        // that ships. `PathFollower.Collision` answers one question — has the
        // rocket's muzzle anything under it — and a null reference silently
        // restores the pre-tilt behaviour, so a probe that omits it reports the
        // old follower's reach under the new follower's name. `ScriptedBot` hands
        // it over on every poll; a probe that drives the follower directly is the
        // only other place that has to.
        follower.Collision = world.Collision;
        follower.SetGoal(state, X(to), graph.StandY(to));

        var slot = world.Place(EdgeFlights.ClassFor(NavEdgeType.RocketJump), Team.Red, X(from), graph.StandY(from));
        world.Drive(slot, new RoutePilot(follower, state, query));

        for (; ticks < budget && !state.Arrived; ticks++)
            world.Step();

        return state.Arrived ? EdgeFlightOutcome.Arrived : EdgeFlightOutcome.Stranded;
    }

    /// <summary>A per-map seed that does not move between processes.</summary>
    /// <remarks>
    /// FNV-1a over the key rather than <c>string.GetHashCode</c>, which .NET
    /// randomises per process — a bench whose seed moves every run is a bench
    /// whose result cannot be reproduced or bisected.
    /// </remarks>
    private static int Seed(int seed, string map, int area)
    {
        var hash = 2166136261u;

        foreach (var c in $"{map}_a{area}")
        {
            hash ^= c;
            hash *= 16777619u;
        }

        return (int)(hash ^ (uint)seed) & 0x7FFFFFFF;
    }

    /// <summary>
    /// Is a stranded arc flyable at all — by ANY input sequence, not just the
    /// follower's?
    /// </summary>
    /// <remarks>
    /// <b>The attribution question.</b> A stranding says the follower did not fly
    /// the edge; it does not say the edge can be flown. This searches input
    /// sequences directly — start on the takeoff column at the speed the edge
    /// itself claims (<c>NAV_EDGE_BUCKET</c>), jump, and sweep the remaining
    /// controls.
    /// <para>
    /// ⛔ <b>A hit is proof and a miss is NOT.</b> The search is a finite grid, and
    /// measured against arcs the follower demonstrably flew it finds only about
    /// <b>40%</b> of them. So it can prove an arc real and can never prove one
    /// impossible, and <b>a "not flyable" count from this mode means nothing on its
    /// own.</b>
    /// </para>
    /// <para>
    /// ⛔ <b>Which is why it always runs the control.</b> The same search over a
    /// sample of arcs the follower demonstrably FLEW is what turns the treatment
    /// number into a
    /// statement — the two rates are printed in one block, with a two-proportion z
    /// between them, because a rate quoted without its control reads as a finding
    /// and is not one.
    /// </para>
    /// </remarks>
    public static void Oracle(
        string maps, string nav, int budget, bool distances, int threads, int control, int seed, string? csv)
    {
        Console.WriteLine(
            $"BENCH_FLIGHT_ORACLE budget={budget} distances={(distances ? "ON" : "off")} control={control}");
        Console.WriteLine();

        var located = new List<(string Map, int Area, EdgeFlight Flight)>();

        EdgeFlights.SweepAll(
            maps, nav, [NavEdgeType.Jump, NavEdgeType.DoubleJump, NavEdgeType.RocketJump],
            Team.Red, budget, distances, threads,
            onGraph: (map, area, graph) =>
            {
                foreach (var flight in graph)
                    located.Add((map, area, flight));
            });

        var stranded = located.Where(f => f.Flight.Outcome == EdgeFlightOutcome.Stranded).ToList();
        // ⛔ FLOWN, not merely arrived. 2,099 of the corpus's arrivals never
        // steered the edge they were credited to (EdgeFlight.RanEdge), so an
        // "arrived" control asks this search to fly arcs the follower never flew
        // either — which reads back as the search being weak.
        var arrived = located.Where(f => f.Flight.Flown).ToList();
        var rng = new Random(seed);
        var sample = control <= 0 || control >= arrived.Count
            ? arrived
            : [.. arrived.OrderBy(_ => rng.Next()).Take(control)];

        var strandedHits = Probe(maps, nav, stranded);
        var controlHits = Probe(maps, nav, sample);
        var verdicts = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>();
        var strandedAssisted = Probe(maps, nav, stranded, distances, assisted: true, verdicts);
        var controlAssisted = Probe(maps, nav, sample, distances, assisted: true);

        Console.WriteLine("  arm         n     provably flyable by the search");
        Console.WriteLine(
            $"  stranded {stranded.Count,6}     {strandedHits,6} ({Rate(strandedHits, stranded.Count),5:0.0}%)  "
            + "— a LOWER BOUND on how many are the follower's fault");
        Console.WriteLine(
            $"  flown    {sample.Count,6}     {controlHits,6} ({Rate(controlHits, sample.Count),5:0.0}%)  "
            + "— the search's RECALL, on arcs the follower demonstrably flew");
        Console.WriteLine();
        Console.WriteLine(
            $"  two-proportion z = {Z(strandedHits, stranded.Count, controlHits, sample.Count):0.00} "
            + "(negative: the search finds stranded arcs harder than ones the follower flew)");
        Console.WriteLine();
        Console.WriteLine(
            "  NOTE: the 'not flyable' remainder is NOT an arc-fault count. At this recall the search");
        Console.WriteLine(
            "     misses most real arcs, so its negatives carry no information about the generator.");

        // The second instrument, and the one whose negatives can mean something:
        // the REAL follower, handed the takeoff column and a takeoff speed. It
        // splits a stranding into "the approach was the problem" and "the arc is".
        Console.WriteLine();
        Console.WriteLine("  the follower, started ON the takeoff column at a swept speed:");
        Console.WriteLine(
            $"  stranded {stranded.Count,6}     {strandedAssisted,6} ({Rate(strandedAssisted, stranded.Count),5:0.0}%)  "
            + "— arc is fine, the APPROACH failed");
        Console.WriteLine(
            $"  flown    {sample.Count,6}     {controlAssisted,6} ({Rate(controlAssisted, sample.Count),5:0.0}%)  "
            + "— the recall of THIS instrument");

        // ⛔ The unit of observation again. "A third of strandings are the
        // approach" is not actionable until somebody can open the 109 and look at
        // one, and the counts cannot say which they are.
        if (csv is not null)
        {
            using var writer = new StreamWriter(csv);
            writer.WriteLine("map,area,edge,type,from,to,team,approach");

            foreach (var (map, area, flight) in stranded)
            {
                var key = $"{map},{area},{flight.Edge}";
                var hit = verdicts.TryGetValue(key, out var value) && value;

                writer.WriteLine(
                    $"{map},{area},{flight.Edge},{flight.Type},{flight.From},{flight.To},{flight.Team},"
                    + $"{(hit ? 1 : 0)}");
            }

            Console.WriteLine();
            Console.WriteLine($"{stranded.Count} stranded arcs written to {csv} (approach=1 means the arc is fine)");
        }
    }

    private static int Probe(
        string maps, string nav, IReadOnlyList<(string Map, int Area, EdgeFlight Flight)> flights,
        bool distances = false, bool assisted = false,
        System.Collections.Concurrent.ConcurrentDictionary<string, bool>? verdicts = null)
    {
        var hits = 0;

        Parallel.ForEach(flights, () => 0, (row, _, local) =>
        {
            var world = ScenarioWorld.Load(row.Map, row.Area, maps, nav);
            var graph = world.Nav!;

            var hit = assisted
                ? Assisted(row.Map, row.Area, maps, nav, graph, row.Flight, distances)
                : Flyable(row.Map, row.Area, maps, nav, graph, row.Flight);

            verdicts?.TryAdd($"{row.Map},{row.Area},{row.Flight.Edge}", hit);

            return hit ? local + 1 : local;
        },
        local => Interlocked.Add(ref hits, local));

        return hits;
    }

    /// <summary>
    /// The real follower, started on the takeoff column at a swept speed.
    /// </summary>
    /// <remarks>
    /// <b>What it separates.</b> A flight starts at the from-node's midpoint and
    /// has to get itself to the takeoff column in a fit state to leave. If the
    /// same follower makes the arc when it is simply PUT there, the arc is real and
    /// the approach is what failed — a different repair from a bad arc, and the
    /// open-loop grid above cannot tell them apart because it is not the follower.
    /// <para>
    /// ⛔ <b>It requires the bot to have actually steered the edge</b>
    /// (<see cref="EdgeFlight.RanEdge"/>'s reason): planning from the takeoff
    /// column can route around just as planning from the node can, and an arrival
    /// by some other way is not this arc.
    /// </para>
    /// </remarks>
    private static bool Assisted(
        string map, int area, string maps, string nav, NavGraph graph, EdgeFlight flight, bool distances)
    {
        var e = graph.Edges[flight.Edge];
        var takeoff = e.Takeoff >= 0
            ? NavGraph.ColumnWorldX(e.Takeoff)
            : NavGraph.ColumnWorldX((graph.Nodes[e.From].X0 + graph.Nodes[e.From].X1) / 2);
        var toMid = NavGraph.ColumnWorldX((graph.Nodes[e.To].X0 + graph.Nodes[e.To].X1) / 2);
        var direction = toMid >= takeoff ? 1 : -1;
        var startY = EdgeFlights.StandingY(graph, e.From);
        var query = EdgeFlights.QueryFor(e.Type, flight.Team);
        var set = distances ? NavDistanceSet.LoadOrDerive(nav, map, area, graph, out _) : null;

        foreach (var speed in (ReadOnlySpan<double>)[0, 2.0, 3.0, 4.0, 4.5, 5.1])
        {
            var world = ScenarioWorld.Load(map, area, maps, nav);

            // ⛔ `Collision` or this instrument measures the follower as it was
            // before the lip tilt shipped — see FlyRoute above for why a null
            // reference is silent rather than loud.
            var follower = new PathFollower(graph) { Distances = set, Collision = world.Collision };
            var state = new BotPathState();

            if (!follower.SetGoal(state, toMid, graph.StandY(e.To)))
                continue;

            var slot = world.Place(EdgeFlights.ClassFor(e.Type), flight.Team, takeoff, startY);
            world.State.Characters[slot].HSpeed = speed * direction;
            world.Drive(slot, new RoutePilot(follower, state, query));

            var ran = 0;

            for (var t = 0; t < 400 && !state.Arrived; t++)
            {
                world.Step();

                if (state.EdgeFrom == e.From && state.EdgeTo == e.To)
                    ran++;
            }

            if (state.Arrived && ran > 0)
                return true;
        }

        return false;
    }

    /// <summary>Does any candidate in the grid land the arc?</summary>
    private static bool Flyable(string map, int area, string maps, string nav, NavGraph graph, EdgeFlight flight)
    {
        var e = graph.Edges[flight.Edge];
        var takeoff = e.Takeoff >= 0
            ? NavGraph.ColumnWorldX(e.Takeoff)
            : NavGraph.ColumnWorldX((graph.Nodes[e.From].X0 + graph.Nodes[e.From].X1) / 2);
        var toMid = NavGraph.ColumnWorldX((graph.Nodes[e.To].X0 + graph.Nodes[e.To].X1) / 2);
        var direction = toMid >= takeoff ? 1 : -1;
        var move = direction > 0 ? PlayerKeys.Right : PlayerKeys.Left;
        var startY = EdgeFlights.StandingY(graph, e.From);
        var bucket = Math.Abs(e.Bucket);

        double[] speeds = [bucket, bucket * 0.75, bucket * 0.5, 0, 2.5, 3.5, 4.3, 5.1];

        int[] delays = e.Type switch
        {
            NavEdgeType.DoubleJump => [.. Enumerable.Range(1, 26)],
            NavEdgeType.RocketJump => [0, 1, 2, 3],
            _ => [0],
        };

        // ⚠️ Tick 0 is kept and it is NOT a wasted press. EdgeFlights.StandingY
        // starts a body one pixel above the floor, so it is airborne on its first
        // tick — but the physics QUEUES a jump pressed in the air
        // (CharacterState.QueueJump) and fires it on landing, which is a different
        // and sometimes better takeoff than any of the grounded ones. Dropping it
        // on the theory that it did nothing cost 16 of 118 control hits; the list
        // is a superset for a reason.
        int[] jumpAt = [0, 1, 2, 3, 5, 8];

        foreach (var speed in speeds.Distinct())
        {
            foreach (var delay in delays)
            {
                foreach (var start in jumpAt)
                foreach (var hold in (ReadOnlySpan<bool>)[true, false])
                {
                    var world = ScenarioWorld.Load(map, area, maps, nav);
                    var slot = world.Place(EdgeFlights.ClassFor(e.Type), flight.Team, takeoff, startY);

                    world.State.Characters[slot].HSpeed = speed * direction;
                    world.Drive(slot, new Launcher(e.Type, move, start, delay, hold));

                    for (var t = 0; t < 140; t++)
                    {
                        world.Step();

                        var view = new WorldView(in world.State, world.Collision);

                        if (!view.IsAlive(slot))
                            break;

                        ref readonly var self = ref view.Character(slot);

                        if (t > 2 && self.OnGround && graph.NodeFromWorld(self.X, self.Y) == e.To)
                            return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// What a rocket-jump takeoff actually DELIVERS, against what the arc was
    /// costed to need.
    /// </summary>
    /// <remarks>
    /// <b>The question the attribution could not reach.</b> <see cref="Oracle"/>
    /// splits a stranding into "the approach failed" and "the arc did", and the
    /// second half has never been opened: "this follower cannot fly it from a
    /// perfect start" says nothing about <em>which</em> of the three things an arc
    /// needs — a blast, height, reach — was missing.
    /// <para>
    /// So this puts the follower on the takeoff column exactly as
    /// <see cref="Assisted"/> does and watches the BODY rather than the verdict:
    /// did <c>moveStatus</c> ever reach 1 (the blast went off at all), what was the
    /// most negative <c>vspeed</c> it reached, how far did it rise against
    /// <c>StandY(from) - StandY(to)</c>, and how far across against the takeoff
    /// column to the to-node.
    /// </para>
    /// <para>
    /// ⛔ <b>The control arm is arcs the follower demonstrably FLEW</b>, for the
    /// reason the oracle records: each of these numbers has a perfectly ordinary
    /// distribution on its own and only says something beside the same number on
    /// arcs that work.
    /// </para>
    /// <para>
    /// ⚠️ <b>Best-over-the-sweep, per arc.</b> Six takeoff speeds are tried and the
    /// row keeps the deepest launch and the highest rise, because the question is
    /// what the manoeuvre can deliver here rather than what one arbitrary speed
    /// did. A per-attempt average would be a measurement of the speed list.
    /// </para>
    /// </remarks>
    public static void Launches(
        string maps, string nav, int budget, bool distances, int threads, int control, int seed, string? csv)
    {
        Console.WriteLine(
            $"BENCH_FLIGHT_LAUNCH budget={budget} distances={(distances ? "ON" : "off")} control={control}");
        Console.WriteLine();

        var located = new List<(string Map, int Area, EdgeFlight Flight)>();

        EdgeFlights.SweepAll(
            maps, nav, [NavEdgeType.RocketJump], Team.Red, budget, distances, threads,
            onGraph: (map, area, graph) =>
            {
                foreach (var flight in graph)
                    located.Add((map, area, flight));
            });

        var stranded = located.Where(f => f.Flight.Outcome == EdgeFlightOutcome.Stranded).ToList();
        var flown = located.Where(f => f.Flight.Flown).ToList();
        var rng = new Random(seed);
        var sample = control <= 0 || control >= flown.Count
            ? flown
            : [.. flown.OrderBy(_ => rng.Next()).Take(control)];

        var rows = new System.Collections.Concurrent.ConcurrentBag<(string Arm, string Map, LaunchReading R)>();

        void Measure(string arm, IReadOnlyList<(string Map, int Area, EdgeFlight Flight)> arcs) =>
            Parallel.ForEach(arcs, row =>
            {
                var world = ScenarioWorld.Load(row.Map, row.Area, maps, nav);

                rows.Add((arm, row.Map, Deliver(row.Map, row.Area, maps, nav, world.Nav!, row.Flight, distances)));
            });

        Measure("stranded", stranded);
        Measure("flown", sample);

        Console.WriteLine(
            "  arm          n    no blast    median launch    short of HEIGHT    short of REACH    "
            + "median rise/need    OVER the to-node    median closest    ARRIVED   diverted");
        Console.WriteLine(
            "  ---------------------------------------------------------------------------------------------"
            + "-------------------------------------------------");

        foreach (var arm in (ReadOnlySpan<string>)["stranded", "flown"])
        {
            var arc = rows.Where(r => r.Arm == arm).Select(r => r.R).ToList();

            if (arc.Count == 0)
                continue;

            var blasted = arc.Where(r => r.Launched).ToList();
            var dud = arc.Count - blasted.Count;
            var shortHeight = blasted.Count(r => r.RiseAchieved + 1e-9 < r.RiseNeeded);
            var shortReach = blasted.Count(r => r.ReachAchieved + 1e-9 < r.ReachNeeded);

            Console.WriteLine(
                $"  {arm,-9} {arc.Count,4}   {dud,4} ({Rate(dud, arc.Count),4:0.0}%)   "
                + $"{Median(blasted.Select(r => r.LaunchVSpeed)),13:0.00}    "
                + $"{shortHeight,6} ({Rate(shortHeight, blasted.Count),4:0.0}%)    "
                + $"{shortReach,6} ({Rate(shortReach, blasted.Count),4:0.0}%)    "
                + $"{Median(blasted.Select(r => r.RiseAchieved)),6:0.0} / {Median(blasted.Select(r => r.RiseNeeded)),-6:0.0}    "
                + $"{blasted.Count(r => r.Reached),6} ({Rate(blasted.Count(r => r.Reached), blasted.Count),4:0.0}%)    "
                + $"{Median(blasted.Select(r => r.MinDist)),9:0.0}px    "
                + $"{blasted.Count(r => r.Arrived),6} ({Rate(blasted.Count(r => r.Arrived), blasted.Count),4:0.0}%)    "
                + $"{blasted.Count(r => r.Arrived && !r.Flew),6}");
        }

        if (csv is not null)
        {
            using var writer = new StreamWriter(csv);
            writer.WriteLine(
                "arm,map,area,edge,from,to,launched,vlaunch,rise,riseneed,reach,reachneed,reached,mindist,"
                + "arrived,flew,cground,chspeed,cstale,chops,bandticks,bandmin,"
                + "steerticks,takeoffs,duds,offroute,blacklists,unroutable,replans,stuck,"
                + "airabandon,landselse");

            foreach (var (arm, map, r) in rows.OrderBy(r => r.Arm).ThenBy(r => r.Map).ThenBy(r => r.R.Edge))
            {
                writer.WriteLine(
                    string.Join(
                        ',',
                        arm, map, r.Area, r.Edge, r.From, r.To, r.Launched ? 1 : 0,
                        r.LaunchVSpeed.ToString("0.###", CultureInfo.InvariantCulture),
                        r.RiseAchieved.ToString("0.#", CultureInfo.InvariantCulture),
                        r.RiseNeeded.ToString("0.#", CultureInfo.InvariantCulture),
                        r.ReachAchieved.ToString("0.#", CultureInfo.InvariantCulture),
                        r.ReachNeeded.ToString("0.#", CultureInfo.InvariantCulture),
                        r.Reached ? "1" : "0",
                        r.MinDist.ToString("0.#", CultureInfo.InvariantCulture),
                        r.Arrived ? "1" : "0",
                        r.Flew ? "1" : "0",
                        r.ClosestOnGround ? "1" : "0",
                        r.ClosestHSpeed.ToString("0.##", CultureInfo.InvariantCulture),
                        r.ClosestStale ? "1" : "0",
                        r.ClosestHopsLeft.ToString(CultureInfo.InvariantCulture),
                        r.BandTicks.ToString(CultureInfo.InvariantCulture),
                        r.BandMinHSpeed.ToString("0.##", CultureInfo.InvariantCulture),
                        r.SteerTicks, r.Takeoffs, r.Duds, r.OffRoute,
                        r.Blacklists, r.Unroutable, r.Replans, r.Stuck,
                        r.AirAbandons, r.AirLandsElsewhere));
            }

            Console.WriteLine();
            Console.WriteLine($"{rows.Count} rows written to {csv}");
        }
    }

    /// <summary>One arc's best takeoff over the speed sweep.</summary>
    /// <param name="Reached">
    /// The body was inside the to-node's own footprint at some tick of the flight.
    /// <b>Not an arrival</b>: passing through the destination and carrying on past
    /// it is exactly the failure this column exists to make visible, and an
    /// arrival count cannot distinguish it from never getting there.
    /// </param>
    /// <param name="MinDist">Closest the body ever came to the to-node's standing spot.</param>
    /// <param name="Arrived">The follower's own verdict fired.</param>
    /// <param name="ClosestOnGround">
    /// The four <c>Closest*</c> readings are the follower's own state at the tick
    /// the body came NEAREST the goal — the clauses of the arrival test, read
    /// where the arrival should have fired. ⛔ <b>Not an average over the
    /// flight</b>: a clause that holds for 390 of 400 ticks and fails at the one
    /// tick that mattered is exactly the failure, and an average hides it.
    /// </param>
    /// <param name="BandTicks">
    /// Ticks the body spent ON THE GROUND inside the arrival tolerance around the
    /// goal column, and <paramref name="BandMinHSpeed"/> the slowest it ever got
    /// while there. ⛔ <b>This is the arrival test's own question</b>: it fires on
    /// <c>onGround &amp;&amp; |hspeed| &lt; 1</c> inside that band, so a flight
    /// with band ticks and a minimum that never falls under 1 is a bot that stood
    /// where it was sent and was refused for still moving — and one with zero band
    /// ticks never got there on the ground at all. The
    /// <c>Closest*</c> readings above cannot separate those two: the tick nearest
    /// the goal is usually not the tick the arrival would fire on.
    /// </param>
    /// <param name="SteerTicks">
    /// Ticks spent steering this edge, and <paramref name="Takeoffs"/> the rocket
    /// launches asked for — both SUMMED over the six attempts, as are every
    /// counter after them. ⚠️ <b>The unit is "per arc over six attempts"</b>, which
    /// is only comparable because both arms get the same six. It is not a rate and
    /// not a per-attempt average.
    /// <para>
    /// ⛔ <b>What they are for.</b> An arc the follower ARRIVES at without ever
    /// flying is either one it never planned as an arc — <c>SteerTicks</c> 0, A*
    /// preferring to walk from the takeoff column — or one it committed to and
    /// abandoned, which is <c>OffRoute</c> and <c>Blacklists</c>. Those are
    /// different repairs and the arrival cannot tell them apart.
    /// </para>
    /// </param>
    /// <param name="AirAbandons">
    /// Times the follower stopped naming this edge WHILE THE BODY WAS STILL
    /// AIRBORNE on it, and <paramref name="AirLandsElsewhere"/> times it stayed on
    /// the edge all the way down and landed on something that was not the
    /// to-node. ⛔ <b>These are the two ways a flight that leaves the ground
    /// correctly still fails</b>, and they want opposite repairs: the first is the
    /// follower changing its mind mid-arc, the second is the arc itself missing.
    /// An <c>OffRouteFires</c> count cannot separate them — it fires after the
    /// landing in both.
    /// </param>
    /// <param name="Flew">
    /// Some ONE attempt both arrived and steered this edge — the oracle's own
    /// bar. ⛔ <paramref name="Arrived"/> without this is the <c>Diverted</c>
    /// shape: the body got to the destination by walking some other way, which is
    /// not this arc being flown.
    /// </param>
    private readonly record struct LaunchReading(
        int Edge,
        int From,
        int To,
        bool Launched,
        double LaunchVSpeed,
        double RiseAchieved,
        double RiseNeeded,
        double ReachAchieved,
        double ReachNeeded,
        bool Reached,
        double MinDist,
        bool Arrived,
        bool Flew,
        bool ClosestOnGround,
        double ClosestHSpeed,
        bool ClosestStale,
        int ClosestHopsLeft,
        int Area,
        int BandTicks,
        double BandMinHSpeed,
        int SteerTicks,
        int Takeoffs,
        int Duds,
        int OffRoute,
        int Blacklists,
        int Unroutable,
        int Replans,
        int Stuck,
        int AirAbandons,
        int AirLandsElsewhere);

    /// <summary>
    /// Flies one arc from the takeoff column at each swept speed and keeps the
    /// best body the manoeuvre produced.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The rise is measured body-to-body, not goal-to-goal.</b>
    /// <see cref="EdgeFlights.StandingY"/> starts a body one pixel above the floor
    /// and <see cref="NavGraph.StandY"/> is a goal rather than a body position, so
    /// a "needed rise" taken between the two goals and an "achieved rise" taken
    /// between two bodies would differ by a pixel that has nothing to do with the
    /// arc.
    /// </remarks>
    private static LaunchReading Deliver(
        string map, int area, string maps, string nav, NavGraph graph, EdgeFlight flight, bool distances)
    {
        var e = graph.Edges[flight.Edge];
        var takeoff = e.Takeoff >= 0
            ? NavGraph.ColumnWorldX(e.Takeoff)
            : NavGraph.ColumnWorldX((graph.Nodes[e.From].X0 + graph.Nodes[e.From].X1) / 2);
        var toMid = NavGraph.ColumnWorldX((graph.Nodes[e.To].X0 + graph.Nodes[e.To].X1) / 2);
        var direction = toMid >= takeoff ? 1 : -1;
        var startY = EdgeFlights.StandingY(graph, e.From);
        var query = EdgeFlights.QueryFor(e.Type, flight.Team);
        var set = distances ? NavDistanceSet.LoadOrDerive(nav, map, area, graph, out _) : null;

        var riseNeeded = startY - EdgeFlights.StandingY(graph, e.To);
        var reachNeeded = Math.Abs(toMid - takeoff);

        var toY = EdgeFlights.StandingY(graph, e.To);
        var launched = false;
        var vLaunch = 0.0;
        var rise = 0.0;
        var reach = 0.0;
        var reached = false;
        var minDist = double.MaxValue;
        var arrived = false;
        var flew = false;
        var closestGround = false;
        var closestHSpeed = 0.0;
        var closestStale = false;
        var closestHops = -1;
        var bandTicks = 0;
        var bandMin = double.MaxValue;
        var steerTicks = 0;
        var takeoffs = 0;
        var duds = 0;
        var offRoute = 0;
        var blacklists = 0;
        var unroutable = 0;
        var replans = 0;
        var stuck = 0;
        var airAbandons = 0;
        var landsElsewhere = 0;

        foreach (var speed in (ReadOnlySpan<double>)[0, 2.0, 3.0, 4.0, 4.5, 5.1])
        {
            var world = ScenarioWorld.Load(map, area, maps, nav);
            var follower = new PathFollower(graph) { Distances = set, Collision = world.Collision };
            var state = new BotPathState();

            if (!follower.SetGoal(state, toMid, graph.StandY(e.To)))
                continue;

            var slot = world.Place(EdgeFlights.ClassFor(e.Type), flight.Team, takeoff, startY);
            world.State.Characters[slot].HSpeed = speed * direction;
            world.Drive(slot, new RoutePilot(follower, state, query));

            var attemptV = 0.0;
            var attemptRise = 0.0;
            var attemptReach = 0.0;
            var attemptDist = double.MaxValue;
            var attemptReached = false;
            var attemptSteered = 0;
            var attemptGround = false;
            var attemptHSpeed = 0.0;
            var attemptStale = false;
            var attemptHops = -1;
            var attemptBand = 0;
            var onThisEdgeAirborne = false;
            var attemptBandMin = double.MaxValue;
            var blast = false;

            for (var t = 0; t < 400 && !state.Arrived; t++)
            {
                world.Step();

                if (state.EdgeFrom == e.From && state.EdgeTo == e.To)
                    attemptSteered++;

                if (!world.State.IsAlive(slot))
                    break;

                ref readonly var self = ref world.State.Characters[slot];

                // ⚠️ moveStatus 1 is the blast's own signature — Rocket/User Event
                // 5 sets it on the owner — so it is the one reading that separates
                // a rocket jump from the ordinary jump the same press produces when
                // the rocket does not go off.
                if (self.MoveStatus == 1)
                {
                    blast = true;
                    attemptV = Math.Min(attemptV, self.VSpeed);
                }

                if (!blast)
                    continue;

                attemptRise = Math.Max(attemptRise, startY - self.Y);
                attemptReach = Math.Max(attemptReach, Math.Abs(self.X - takeoff));
                var here = Math.Sqrt(((self.X - toMid) * (self.X - toMid)) + ((self.Y - toY) * (self.Y - toY)));

                if (here < attemptDist)
                {
                    attemptDist = here;
                    attemptGround = self.OnGround;
                    attemptHSpeed = Math.Abs(self.HSpeed);
                    attemptStale = state.PathStale;
                    attemptHops = state.HasGoal ? state.Path.Count - 1 - state.PathAt : -1;
                }

                // ⚠️ Asked every tick, airborne included. The question is whether
                // the arc PUT the body over its destination, which a landing test
                // would answer only for the flights that already worked.
                if (graph.NodeFromWorld(self.X, self.Y) == e.To)
                    attemptReached = true;

                // The three states of one takeoff, read in the order they can
                // happen: leaving the ground on this edge arms the watch; the edge
                // changing under the body while it is still up is an in-air
                // abandonment; touching down disarms it and adjudicates where.
                var onEdge = state.EdgeFrom == e.From && state.EdgeTo == e.To;

                if (!self.OnGround && onEdge)
                    onThisEdgeAirborne = true;
                else if (!self.OnGround && onThisEdgeAirborne)
                {
                    airAbandons++;
                    onThisEdgeAirborne = false;
                }
                else if (self.OnGround && onThisEdgeAirborne)
                {
                    if (graph.NodeFromWorld(self.X, self.Y) != e.To)
                        landsElsewhere++;

                    onThisEdgeAirborne = false;
                }


                // ⚠️ The node test as well as the column. `reachNeeded` is under
                // 8px on some arcs — a rocket jump straight up onto a ledge — so a
                // body still standing on the FROM node is already inside the goal
                // column's tolerance, and a band measured on x alone counted the
                // takeoff as an arrival opportunity.
                if (self.OnGround
                    && Math.Abs(self.X - toMid) <= BotConstants.ArriveTol
                    && graph.NodeFromWorld(self.X, self.Y) == e.To)
                {
                    attemptBand++;
                    attemptBandMin = Math.Min(attemptBandMin, Math.Abs(self.HSpeed));
                }
            }

            if (!blast)
                continue;

            launched = true;
            vLaunch = Math.Min(vLaunch, attemptV);
            rise = Math.Max(rise, attemptRise);
            reach = Math.Max(reach, attemptReach);
            reached |= attemptReached;

            if (attemptDist < minDist)
            {
                minDist = attemptDist;
                closestGround = attemptGround;
                closestHSpeed = attemptHSpeed;
                closestStale = attemptStale;
                closestHops = attemptHops;
            }

            // Best over the sweep, like every other column: the question is
            // whether the manoeuvre can ever put the body still on the goal, not
            // what one arbitrary takeoff speed managed.
            bandTicks = Math.Max(bandTicks, attemptBand);
            bandMin = Math.Min(bandMin, attemptBandMin);

            steerTicks += attemptSteered;
            takeoffs += state.RocketJumps;
            duds += state.RocketDuds;
            offRoute += state.OffRouteFires;
            blacklists += state.BlacklistFires;
            unroutable += state.EdgeUnroutableFires;
            replans += state.Replans;
            stuck += state.StuckFires;
            // ⛔ Per ATTEMPT, not per arc. Arriving at one takeoff speed and
            // steering the edge at a different one is not the same event, and
            // summing the two across the sweep credited 44 arcs where the oracle
            // — which asks both of one attempt — credits 28.
            arrived |= state.Arrived;
            flew |= state.Arrived && attemptSteered > 0;
        }

        return new LaunchReading(
            flight.Edge, e.From, e.To, launched, vLaunch, rise, riseNeeded, reach, reachNeeded,
            reached, minDist == double.MaxValue ? -1 : minDist, arrived, flew,
            closestGround, closestHSpeed, closestStale, closestHops, area,
            bandTicks, bandMin == double.MaxValue ? -1 : bandMin,
            steerTicks, takeoffs, duds, offRoute, blacklists, unroutable, replans, stuck,
            airAbandons, landsElsewhere);
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();

        return sorted.Length == 0
            ? 0
            : sorted.Length % 2 == 1
                ? sorted[sorted.Length / 2]
                : (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2;
    }

    /// <summary>One arc, one tick per line, from the takeoff column.</summary>
    /// <remarks>
    /// <b>The instrument of last resort, and it exists because the aggregates ran
    /// out.</b> The launch readings say the failures and the successes are
    /// indistinguishable in blast, height and reach, and the counters say the bot
    /// barely steers the edge at all — neither of which names a mechanism. This
    /// prints what the follower and the body were doing on every tick so one arc
    /// can be read rather than summarised.
    /// <para>
    /// ⚠️ <b>Same setup as <see cref="Assisted"/> and <see cref="Deliver"/></b> —
    /// the takeoff column, the same six speeds, the same query — so what it shows
    /// is what those two measured, not a fresh arrangement that might differ.
    /// </para>
    /// </remarks>
    public static void Trace(string maps, string nav, bool distances, string spec, int ticks)
    {
        var parts = spec.Split(':');

        if (parts.Length != 3)
        {
            Console.WriteLine("BENCH_FLIGHT_TRACE wants map:area:edge, e.g. ctf_eiger:1:1960");
            return;
        }

        var map = parts[0];
        var area = int.Parse(parts[1]);
        var edgeIndex = int.Parse(parts[2]);

        var world0 = ScenarioWorld.Load(map, area, maps, nav);
        var graph = world0.Nav!;
        var e = graph.Edges[edgeIndex];
        var takeoff = e.Takeoff >= 0
            ? NavGraph.ColumnWorldX(e.Takeoff)
            : NavGraph.ColumnWorldX((graph.Nodes[e.From].X0 + graph.Nodes[e.From].X1) / 2);
        var toMid = NavGraph.ColumnWorldX((graph.Nodes[e.To].X0 + graph.Nodes[e.To].X1) / 2);
        var direction = toMid >= takeoff ? 1 : -1;
        var startY = EdgeFlights.StandingY(graph, e.From);
        var toY = EdgeFlights.StandingY(graph, e.To);
        var query = EdgeFlights.QueryFor(e.Type, Team.Red);
        var set = distances ? NavDistanceSet.LoadOrDerive(nav, map, area, graph, out _) : null;

        Console.WriteLine(
            $"{map} a{area} e{edgeIndex} {e.Type} {e.From}->{e.To} "
            + $"takeoff=x{takeoff:0.#} y{startY:0.#}  goal=x{toMid:0.#} y{toY:0.#}  "
            + $"bucket={e.Bucket:0.##} ticks={e.Ticks} rejump={e.Rejump}");
        Console.WriteLine();

        foreach (var speed in (ReadOnlySpan<double>)[0, 2.0, 3.0, 4.0, 4.5, 5.1])
        {
            var world = ScenarioWorld.Load(map, area, maps, nav);
            var follower = new PathFollower(graph) { Distances = set, Collision = world.Collision };
            var state = new BotPathState();

            if (!follower.SetGoal(state, toMid, graph.StandY(e.To)))
            {
                Console.WriteLine($"speed {speed}: SetGoal refused");
                continue;
            }

            var slot = world.Place(EdgeFlights.ClassFor(e.Type), Team.Red, takeoff, startY);
            world.State.Characters[slot].HSpeed = speed * direction;

            if (Environment.GetEnvironmentVariable("BENCH_FLIGHT_WARM") == "1")
            {
                world.State.Weapons[slot].ReadyToShoot = true;
                world.State.Weapons[slot].RefireTimer = 0;
            }

            world.Drive(slot, new RoutePilot(follower, state, query));

            Console.WriteLine($"--- takeoff speed {speed:0.0} ---");
            Console.WriteLine("    t      x       y   hspd   vspd  gnd ms  air  edge      node  hop  off bl  keys");

            var lastLine = string.Empty;
            var repeats = 0;

            for (var t = 0; t < ticks && !state.Arrived; t++)
            {
                var beforeOff = state.OffRouteFires;
                var beforeBl = state.BlacklistFires;

                world.Step();

                if (!world.State.IsAlive(slot))
                {
                    Console.WriteLine($"  {t,4}  DIED");
                    break;
                }

                ref readonly var self = ref world.State.Characters[slot];
                var node = graph.NodeFromWorld(self.X, self.Y);
                var hop = state.HasGoal ? state.Path.Count - 1 - state.PathAt : -1;

                var line =
                    $"{self.X,7:0.#} {self.Y,7:0.#} {self.HSpeed,6:0.##} {self.VSpeed,6:0.##} "
                    + $"{(self.OnGround ? "  Y" : "  .")} {self.MoveStatus,2} {state.AirTicks,4}  "
                    + $"{state.EdgeFrom,4}->{state.EdgeTo,-4} {node,5} {hop,4} "
                    + $"{(state.OffRouteFires > beforeOff ? " OFF" : "    ")}"
                    + $"{(state.BlacklistFires > beforeBl ? " BL" : "   ")}";

                // Consecutive identical body-and-plan lines are collapsed: a bot
                // standing at a gate for eighty ticks is one fact, and eighty
                // copies of it push the tick that matters off the screen.
                if (line == lastLine)
                {
                    repeats++;
                    continue;
                }

                if (repeats > 0)
                    Console.WriteLine($"         ... {repeats} more like the line above");

                repeats = 0;
                lastLine = line;
                Console.WriteLine($"  {t,4} {line}");
            }

            if (repeats > 0)
                Console.WriteLine($"         ... {repeats} more like the line above");

            Console.WriteLine(
                $"  => arrived={state.Arrived} takeoffs={state.RocketJumps} duds={state.RocketDuds} "
                + $"offroute={state.OffRouteFires} bl={state.BlacklistFires} replans={state.Replans} "
                + $"stuck={state.StuckFires} gate(cold={state.GateColdWeapon} fast={state.GateTooFast} "
                + $"back={state.GateBackwards} short={state.GateShort})");
            Console.WriteLine();
        }
    }

    private static double Rate(int hits, int n) => n == 0 ? 0 : 100.0 * hits / n;

    private static double Z(int a, int na, int b, int nb)
    {
        if (na == 0 || nb == 0)
            return 0;

        var pa = (double)a / na;
        var pb = (double)b / nb;
        var pooled = (double)(a + b) / (na + nb);
        var se = Math.Sqrt(pooled * (1 - pooled) * ((1.0 / na) + (1.0 / nb)));

        return se == 0 ? 0 : (pa - pb) / se;
    }

    /// <summary>Jump immediately, then the arc kind's own extra press.</summary>
    private sealed class Launcher(NavEdgeType type, PlayerKeys move, int jumpAt, int delay, bool hold) : IInputSource
    {
        private int _tick = -1;
        private bool _airborne;

        public PlayerInput Poll(in WorldView world, int slot, int tick)
        {
            if (!world.IsAlive(slot))
                return PlayerInput.None;

            _tick++;

            ref readonly var self = ref world.Character(slot);

            if (_tick > jumpAt && !self.OnGround)
                _airborne = true;

            var keys = hold || !_airborne ? move : PlayerKeys.None;

            if (_tick == jumpAt)
                keys |= PlayerKeys.Jump;

            if (type == NavEdgeType.DoubleJump && _tick == jumpAt + delay)
                keys |= PlayerKeys.Jump;

            var aim = 0.0;

            if (type == NavEdgeType.RocketJump && _tick == jumpAt + delay)
            {
                keys |= PlayerKeys.Attack;
                aim = PathFollower.RocketAimDegrees;
            }

            return PlayerInput.FromAim(keys, aim, 0);
        }
    }

    private static HashSet<string> Focus(string? focus) =>
        string.IsNullOrWhiteSpace(focus)
            ? []
            : [.. focus.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>
    /// The follower's keys plus the launch hand-off — the same pair
    /// <see cref="EdgeFlights"/> drives a single hop with.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Not a <see cref="ScriptedBot"/>.</b> A route is about the route: a
    /// full bot would pick targets and hold position, and a stranding would then
    /// be ambiguous between the two halves. What it does copy is the live query
    /// rebuild, which is where a soldier's rocket jumps go as its hp drops.
    /// </remarks>
    private sealed class RoutePilot(PathFollower follower, BotPathState state, NavQuery query) : IInputSource
    {
        private NavQuery _query = query;

        public PlayerInput Poll(in WorldView world, int slot, int tick)
        {
            if (!world.IsAlive(slot))
                return PlayerInput.None;

            ref readonly var self = ref world.Character(slot);

            _query = _query with
            {
                CanDoubleJump = BotClassProfile.AirJump(self.Class),
                CanRocketJump = RocketModel.Capable(in self, in world.Weapon(slot)),
            };

            var keys = follower.Step(state, in self, in world.Weapon(slot), tick, _query);

            var aim = 0.0;
            PathFollower.ApplyLaunch(state, ref keys, ref aim);

            return PlayerInput.FromAim(keys, aim, 0);
        }
    }
}
