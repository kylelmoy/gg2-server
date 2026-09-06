using System.Diagnostics;
using System.Globalization;
using Gg2.Bots;
using Gg2.Protocol;
using Gg2.Simulation;

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
        var types = Enum.GetValues<NavEdgeType>();
        var perType = new Dictionary<NavEdgeType, EdgeFlightTally>();
        var perMap = new List<(string Key, EdgeFlightTally Tally)>();
        var measured = new List<(string Map, int Area, EdgeFlight Flight)>();
        var only = Focus(focus);

        Console.WriteLine(
            $"BENCH_FLIGHT budget={budget} distances={(distances ? "ON (the shipping arm)" : "off")} "
            + $"threads={(threads <= 0 ? Environment.ProcessorCount : threads)}"
            + (only.Count == 0 ? string.Empty : $" maps={string.Join(',', only)}"));
        Console.WriteLine();

        var sw = Stopwatch.StartNew();

        var flights = EdgeFlights.SweepAll(
            maps, nav, types, Team.Red, budget, distances, threads,
            include: only.Count == 0 ? null : (map, _) => only.Contains(map),
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
            var follower = new PathFollower(graph) { Distances = set };
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
                CanRocketJump = RocketModel.Ready(in self, in world.Weapon(slot)),
            };

            var keys = follower.Step(state, in self, in world.Weapon(slot), tick, _query);

            var aim = 0.0;
            PathFollower.ApplyLaunch(state, ref keys, ref aim);

            return PlayerInput.FromAim(keys, aim, 0);
        }
    }
}
