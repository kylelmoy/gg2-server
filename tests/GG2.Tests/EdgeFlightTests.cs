using GG2.Bots;
using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Tests;

/// <summary>
/// <see cref="EdgeFlights"/> against the shipped nav caches — real arcs, real
/// map, real simulation.
/// </summary>
/// <remarks>
/// <b>What this answers that nothing else does.</b>
/// <see cref="PathFollowerTests"/> asserts the arms' decisions on hand-built
/// graphs and <see cref="PathFollowerFixtureTests"/> walks one long leg made
/// entirely of the kinds that need no weapon. Neither ever leaves the ground
/// under a rocket or a mine on an arc a generator actually emitted — a hand-swept
/// count of that can look green while measuring a bug three layers away
/// (docs/TRAPS-BOTS.md, "the arm was never asked").
/// <para>
/// <b>These pin counts against the nav caches in <c>nav/</c>, which are
/// <c>gg2-nav-gen</c>'s output and not checked in.</b> Same exposure as
/// <see cref="PathFollowerFixtureTests"/>'s 137-node route, and the same
/// discipline: a failure here is the <b>re-record</b> — check the change is one
/// you meant, check it went the way you meant, update the row, say so in the
/// commit.
/// </para>
/// </remarks>
public sealed class EdgeFlightTests(Xunit.Abstractions.ITestOutputHelper output)
{
    /// <summary>
    /// The map the pinned sweeps run on.
    /// </summary>
    /// <remarks>
    /// <c>gg_debug</c> rather than a real map because it is small enough that a
    /// whole edge kind is a sweep of tens rather than hundreds — 67 rocket jumps
    /// and 23 air jumps, in about 120ms and 50ms.
    /// <para>
    /// ⚠️ <b>This used to say a sweep of <c>ctf_truefort</c>'s 878 rocket jumps
    /// was "eight seconds, which is a measurement rather than a gate". Measured
    /// 2026-09-05 it is 102 ms</b>, and the whole 25-graph corpus is 5.0 s serial
    /// in Release — <see cref="ScenarioResources"/> caches what used to be
    /// re-decoded per flight. The small map is kept for the per-kind pins because
    /// their <em>numbers</em> are small enough to read; affordability stopped being
    /// the reason, and
    /// <see cref="EveryEdgeOfEveryShippedGraphIsFlownOnTheShippingArm"/> now flies
    /// the corpus.
    /// </para>
    /// </remarks>
    private const string Map = "gg_debug";

    /// <summary>
    /// How many cores the corpus sweeps may take.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Not all of them, and this is a suite-wide fact rather than a
    /// preference.</b> xunit runs test classes in parallel, so a sweep that takes
    /// every core starves whatever is running beside it — with
    /// <c>ProcessorCount</c> threads,
    /// <c>RewardAuthTests.TheHandshakeReachesEveryClientAndSticks</c> went from 3 s
    /// alone to 31 s in the suite and timed out, which reads as an unrelated
    /// network flake and is not one. Two cores of headroom costs this file about a
    /// second and buys every socket-timing test in the suite its scheduler.
    /// </remarks>
    private static int SweepThreads => Math.Max(1, Environment.ProcessorCount - 2);

    private static Func<ScenarioWorld> World(string map = Map)
    {
        ReferenceTree.RequireNavCache();

        return () => ScenarioWorld.Load(map, 1, ReferenceTree.MapsDirectory, ReferenceTree.NavCacheDirectory);
    }

    private static (int Measured, int Arrived, int Flown, int Blacklists) Tally(IReadOnlyList<EdgeFlight> flights)
    {
        var measured = 0;
        var arrived = 0;
        var flown = 0;
        var blacklists = 0;

        foreach (var flight in flights)
        {
            if (!flight.Measured)
                continue;

            measured++;
            blacklists += flight.Blacklists;

            if (flight.Outcome == EdgeFlightOutcome.Arrived)
                arrived++;

            if (flight.Flown)
                flown++;
        }

        return (measured, arrived, flown, blacklists);
    }

    /// <summary>
    /// Every node of every shipped graph can actually hold a character.
    /// </summary>
    /// <remarks>
    /// <b>A nav node is a claim about where a body can be, and until this ran
    /// nothing checked it.</b> Two thirds of them do not admit one at
    /// <see cref="NavGraph.StandY"/>: the box's bottom offset is +23 and
    /// <c>StandY</c> is <c>FloorY - 23</c>, both inclusive, so the body's last
    /// row is the floor's first solid row. <b>Every one of those is free exactly
    /// one pixel up</b> — that is what <see cref="EdgeFlights.StandingY"/> is,
    /// and this is the census it rests on.
    /// <para>
    /// ⛔ <b>Both halves are the gate.</b> The second assertion is what would
    /// notice a generator emitting a node in a crawlspace or inside terrain — a
    /// node no body can occupy, which the graph has no other way of reporting.
    /// The first is what would notice the +23 relationship changing under
    /// <c>StandingY</c>: if nodes stopped overlapping at <c>StandY</c>, the
    /// one-pixel lift would be papering over something that had moved.
    /// </para>
    /// <para>
    /// ⚠️ <b>The soldier's box is used for all of them, and it is not the widest
    /// class.</b> Widths run 6, 7 and 9 and tops run -6 to -12
    /// (<see cref="CharacterBox"/>), so this clears the class the sweep flies
    /// with rather than every class. A per-class census is a bigger claim than
    /// this file needs and would be a different number.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryShippedNodeCanHoldACharacter()
    {
        ReferenceTree.RequireNavCache();

        var box = CharacterBox.For(PlayerClass.Soldier);
        var nodes = 0;
        var overlapAtStandY = 0;
        var blockedAtStandingY = new List<string>();

        foreach (var (map, area) in NavCache.List(ReferenceTree.NavCacheDirectory))
        {
            var world = ScenarioWorld.Load(map, area, ReferenceTree.MapsDirectory, ReferenceTree.NavCacheDirectory);
            var graph = world.Nav!;

            for (var n = 0; n < graph.Nodes.Length; n++)
            {
                var node = graph.Nodes[n];
                var mid = NavGraph.ColumnWorldX((node.X0 + node.X1) / 2);

                nodes++;

                if (!world.Collision.PlaceFreeInMask(box, mid, graph.StandY(n)))
                    overlapAtStandY++;

                if (!world.Collision.PlaceFreeInMask(box, mid, EdgeFlights.StandingY(graph, n)))
                    blockedAtStandingY.Add($"{map}_a{area} node {n}");
            }
        }

        output.WriteLine(
            $"{nodes} nodes; {overlapAtStandY} overlap the floor at StandY, "
            + $"{blockedAtStandingY.Count} still blocked one pixel up");

        Assert.Equal(11_327, nodes);
        Assert.Equal(7_541, overlapAtStandY);
        Assert.Empty(blockedAtStandingY);
    }

    /// <summary>
    /// <b>Every edge of every shipped graph, flown — on the arm a server runs.</b>
    /// </summary>
    /// <remarks>
    /// <b>What this is for.</b> The rest of this file flies one edge kind on one
    /// tiny map. This flies all 116,316 edges of all 25 shipped graphs, which is
    /// the only check that would notice a generator emitting arcs the follower
    /// cannot traverse — a nav graph is only worth what a bot can actually walk.
    /// <para>
    /// ⛔ <b>It runs with the next-hop tables ON, and it is the FIRST gate in this
    /// repository that does.</b> docs/TRAPS-BOTS.md, "The gates measure a
    /// DIFFERENT ARM", records that <see cref="ScenarioWorld"/> sets no
    /// <c>Distances</c>, so the three pinned bot measurements and all 25 route
    /// digests exercise the weighted straight-line heuristic while a live server
    /// and <c>eval</c> exercise the exact one. Those are left alone deliberately —
    /// re-recording them would be a decision about what those alarms mean. This
    /// gate is new, so it gets to start on the right arm, and
    /// <see cref="TheTwoRoutingArmsDisagreeAboutWhichEdgesAreEvenTestable"/> pins
    /// what the difference costs.
    /// </para>
    /// <para>
    /// ⚠️ <b><c>Measured</c> is 28% of <c>Candidates</c>, and that is the ceiling
    /// rather than a defect.</b> A* costs a jump the same as a walk, so 72,806 of
    /// these edges are ones it prefers to route around; those cannot be posed as a
    /// single-hop question at all. <b>A sweep of every edge is not a check of
    /// every edge</b>, and pretending otherwise is what
    /// <see cref="EdgeFlightOutcome.RoutedAround"/> exists to prevent. The
    /// complement — an edge in the context A* actually uses it in — is what
    /// <c>BENCH_FLIGHT_ROUTES</c> samples.
    /// </para>
    /// <para>
    /// ⚠️ <b><c>Stranded</c> is relative to the 400-tick budget and must never be
    /// quoted without it.</b> At 1,600 ticks the no-tables arm's 1,022 strandings
    /// fall to 733, so about a quarter of them are the budget rather than the
    /// edge.
    /// </para>
    /// <para>
    /// ✅ <b>Re-recorded again 2026-09-06, and this one is a FIX rather than a
    /// harness correction.</b> A rocket aimed straight down spawns at the body's
    /// centre, and at a takeoff column the body overhangs the lip of its run, so
    /// the blast fell past the ledge and the arc became a 57px ordinary jump.
    /// Tilting the shot back along the run when — and only when — the terrain says
    /// there is nothing under the muzzle
    /// (<see cref="PathFollower.LipAimDegrees"/>): <b>stranded 703 → 622, rocket-jump
    /// stranded 369 → 291, flown 1,686 → 1,735</b>, blacklists and ticks both down.
    /// At a 4,000-tick budget the hard failures go 429 → 355 and the rocket jump's
    /// share of them 195 → 125. ⚠️ The three pinned end-to-end bot measurements did
    /// NOT move, which is what makes it a targeted fix rather than a change of
    /// behaviour everywhere.
    /// <para>
    /// ✅ <b>Re-recorded twice before that, 2026-09-06.</b> Flying an edge as a team that
    /// can stand on it (<see cref="EdgeFlightOutcome.StartBlocked"/>) took stranded
    /// 847 → 703 and no-route 618 → 466. Then <see cref="EdgeFlight.RanEdge"/>
    /// landed: <b>flown 2,403 → 1,686 with arrived and stranded UNMOVED</b>, because
    /// 2,099 arrivals never steered the edge they were credited to — the follower
    /// re-plans mid-flight and can walk to the same node another way. Rocket jumps
    /// carry almost all of it (flown 1,784 → 1,067); <c>gg_debug</c>'s twelve are
    /// untouched, so the small-map pins in this file did not move.
    /// <para>
    /// ✅ <b>Re-recorded once before that, 2026-09-05, and here is the diff.</b> The harness
    /// was starting the bot at <see cref="NavGraph.StandY"/>, which puts one row
    /// of its box inside the floor on 66.6% of nodes; it now starts at
    /// <see cref="EdgeFlights.StandingY"/>, one pixel up. <b>Stranded 970 → 847,
    /// arrived 31,078 → 31,201, flown 2,355 → 2,400</b>, and
    /// <c>Measured</c>/<c>RoutedAround</c>/<c>Shadowed</c>/<c>NoRoute</c> did not
    /// move at all — the premises are asked of the graph and the plan, not of the
    /// body, so the before and after are comparable. The 123 edges that changed
    /// side were flights that never moved a pixel:
    /// <see cref="EveryShippedNodeCanHoldACharacter"/>.
    /// </para>
    /// <para>
    /// ⛔ <b>A failure here is the RE-RECORD, not a broken test</b> — the same
    /// discipline as the three pinned end-to-end measurements. Check the change is
    /// one you meant, check it went the way you meant, update the row, say so in
    /// the commit. <b>And the reverse:</b> a nav-graph or follower change that
    /// leaves all twelve numbers untouched has not done what it looked like it
    /// did.
    /// </para>
    /// </para>
    /// </remarks>
    /// <para>
    /// ✅ <b>Re-recorded 2026-10-11 for held flight</b> (<see cref="PathFollower.HeldFlight"/>,
    /// <c>gg2-rl</c> <c>docs/PLAN.md</c>, navigation, N1): arrived 31,520 → 31,561,
    /// stranded 535 → 494, ticks 1,474,882 → 1,411,258, blacklists 7,963 → 7,569,
    /// flown 3,626 → 3,644; the premises did not move. Per edge, 107 flights that
    /// stranded now arrive and 66 that arrived now strand (<c>HeldFlightCensus</c>).
    /// The no-tables arm moved the same way: stranded 578 → 459.
    /// Then N2 (launch at speed): stranded 494 → 489, ticks 1,411,258 → 1,419,589;
    /// no-tables 459 → 467. Then N3 (a scout's second jump, planned and rescued):
    /// stranded 489 → 470, ticks 1,419,589 → 1,402,544, flown 3,644 → 3,600 (a
    /// double-jump arc that lands on one held jump is no longer double jumped);
    /// no-tables 467 → 444. Then N4 (two-phase plans, and a landing must come to
    /// rest in the window where the route turns back or a standing take-off
    /// follows): stranded 470 → 445, arrived 31,585 → 31,610, ticks 1,402,544 →
    /// 1,367,620; no-tables 444 → 409.
    /// </para>
    [Fact]
    public void EveryEdgeOfEveryShippedGraphIsFlownOnTheShippingArm()
    {
        ReferenceTree.RequireNavCache();

        var tally = EdgeFlightTally.Of(EdgeFlights.SweepAll(
            ReferenceTree.MapsDirectory,
            ReferenceTree.NavCacheDirectory,
            distances: true,
            threads: SweepThreads));

        output.WriteLine($"shipping arm (tables ON): {tally}");

        Assert.Equal(
            new EdgeFlightTally(
                Candidates: 116_316,
                Measured: 32_055,
                Arrived: 31_610,
                Flown: 3_624,
                Stranded: 445,
                RoutedAround: 72_926,
                Shadowed: 10_869,
                NoRoute: 466,
                StartUnresolved: 0,
                GoalUnresolved: 0,
                Ticks: 1_367_620,
                Blacklists: 7_333,
                StartBlocked: 0,
                TeamSwapped: 178,
                Diverted: 33),
            tally);
    }

    /// <summary>
    /// Every plain and double-jump edge, flown by a scout.
    /// </summary>
    /// <remarks>
    /// The shipping arm flies plain jumps as a soldier, so a scout's own plans
    /// over them — a second jump drawn on a plain jump, a rescue in the air
    /// (<c>gg2-rl</c> <c>docs/PLAN.md</c>, N3) — are measured here and nowhere
    /// else. Recorded 2026-10-11 at N3 (164 stranded, 491,677 ticks); with the
    /// second-jump plans off (<see cref="PathFollower.HeldDoubles"/>) that sweep
    /// stranded 216 and took 509,023 ticks, and per edge 63 flights were won and
    /// 11 lost (<c>HeldFlightCensus.ScoutSecondJumps</c>). N4: 164 → 162, ticks
    /// 491,677 → 480,304.
    /// </remarks>
    [Fact]
    public void EveryJumpIsFlownByAScoutToo()
    {
        ReferenceTree.RequireNavCache();

        var tally = EdgeFlightTally.Of(EdgeFlights.SweepAll(
            ReferenceTree.MapsDirectory,
            ReferenceTree.NavCacheDirectory,
            [NavEdgeType.Jump, NavEdgeType.DoubleJump],
            distances: true,
            threads: SweepThreads,
            flyAs: PlayerClass.Scout));

        output.WriteLine($"scout arm: {tally}");

        Assert.Equal(
            new EdgeFlightTally(
                Candidates: 69_691,
                Measured: 7_543,
                Arrived: 7_381,
                Flown: 607,
                Stranded: 162,
                RoutedAround: 60_308,
                Shadowed: 1_540,
                NoRoute: 300,
                StartUnresolved: 0,
                GoalUnresolved: 0,
                Ticks: 480_304,
                Blacklists: 500,
                StartBlocked: 0,
                TeamSwapped: 40,
                Diverted: 0),
            tally);
    }

    /// <summary>
    /// The two routing arms do not measure the same corpus, and the gap is a
    /// third of the arcs.
    /// </summary>
    /// <remarks>
    /// <b>The exact heuristic is not a faster search, it is a different one</b>
    /// (<c>CLAUDE.md</c>, <c>--no-nav-distances</c>: "a different arm, not a
    /// slower one"), and this is that stated in edges. With the tables A* goes
    /// round <b>5,741 more</b> single hops, so the corpus a per-edge sweep can even
    /// pose shrinks from 37,789 to 32,048 — and measurable rocket jumps fall from
    /// 5,659 to 3,537, a 37% loss of coverage over the one edge kind whose arm
    /// nothing else exercises.
    /// <para>
    /// ⛔ <b>So the two tallies are not two runs of one measurement</b>, and a
    /// stranding count from one cannot be compared with the other's. Both are
    /// pinned because the DIFFERENCE is what would move if the tables, the edge
    /// costs or the heuristic changed — and neither number alone would say which.
    /// </para>
    /// <para>
    /// ⚠️ <b><c>Shadowed</c> and <c>NoRoute</c> are identical across the arms
    /// (10,844 and 618), and that is the control</b>: neither depends on the
    /// heuristic — one is the follower's cost tie-break and the other is
    /// connectivity. A change that moved those two along with the rest did not do
    /// it through the routing arm.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheTwoRoutingArmsDisagreeAboutWhichEdgesAreEvenTestable()
    {
        ReferenceTree.RequireNavCache();

        var plain = EdgeFlightTally.Of(EdgeFlights.SweepAll(
            ReferenceTree.MapsDirectory,
            ReferenceTree.NavCacheDirectory,
            distances: false,
            threads: SweepThreads));

        output.WriteLine($"no tables (what every other gate measures): {plain}");

        Assert.Equal(
            new EdgeFlightTally(
                Candidates: 116_316,
                Measured: 37_787,
                Arrived: 37_378,
                Flown: 5_545,
                Stranded: 409,
                RoutedAround: 67_194,
                Shadowed: 10_869,
                NoRoute: 466,
                StartUnresolved: 0,
                GoalUnresolved: 0,
                Ticks: 1_787_821,
                Blacklists: 9_439,
                StartBlocked: 0,
                TeamSwapped: 206,
                Diverted: 16),
            plain);
    }

    /// <summary>The sweep is the same however many threads run it.</summary>
    /// <remarks>
    /// <b>Why this is load-bearing rather than a formality.</b> The corpus sweep
    /// is only affordable as a gate because it is parallel — 55 s serial in Debug
    /// against a few seconds across cores — and <b>the moment a measurement's
    /// answer depends on its thread count, every number pinned above becomes a
    /// property of the machine that recorded it.</b> This repository gates that
    /// same property for <c>eval</c> and for the rollout search
    /// (<c>RolloutThreadingTests</c>), for this reason.
    /// <para>
    /// Asserted flight-for-flight rather than on the tally: two orderings that
    /// differ can produce identical counts, and the ordering is what a caller
    /// reading a flight back against a graph's edge list depends on.
    /// </para>
    /// <para>
    /// ⚠️ <b>One map, because the property is structural</b> — a flight owns its
    /// world, its follower and its state, and draws from no shared stream. A wider
    /// sweep would buy confidence in the same argument at 25x the cost.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheSweepIsTheSameHoweverManyThreads()
    {
        ReferenceTree.RequireNavCache();

        static bool OnlyDebugMap(string map, int area) => map == Map;

        var serial = EdgeFlights.SweepAll(
            ReferenceTree.MapsDirectory, ReferenceTree.NavCacheDirectory,
            distances: true, threads: 1, include: OnlyDebugMap);

        var parallel = EdgeFlights.SweepAll(
            ReferenceTree.MapsDirectory, ReferenceTree.NavCacheDirectory,
            distances: true, threads: 8, include: OnlyDebugMap);

        // Not vacuous: a filter matching nothing would make the comparison below
        // pass over two empty lists.
        Assert.Equal(495, serial.Count);
        Assert.Equal(serial, parallel);
    }

    /// <summary>
    /// A node's own midpoint resolves to that node, on every arc edge of every
    /// shipped graph — and so does the takeoff column.
    /// </summary>
    /// <remarks>
    /// Measured over every rocket-jump and air-jump edge in <c>nav/</c>: the
    /// takeoff column resolves to the from-node every single time, and so does
    /// the midpoint.
    /// <para>
    /// The midpoint is what <see cref="EdgeFlights"/> uses anyway, and the reason
    /// is stronger than a measurement: <see cref="NavGraph.NodeFromWorld"/> ranks
    /// <c>dy * 4 + dx</c>, a node's own stand y gives it <c>dy = 0</c> exactly,
    /// its own midpoint column gives <c>dx = 0</c>, and no second node can hold
    /// that column on that row — so rank 0 is unique and the answer cannot be
    /// anything else. The takeoff column happens to sit inside the node's span on
    /// every shipped arc, which is why it agrees; nothing guarantees a generator
    /// keeps it there.
    /// </para>
    /// <para>
    /// So this is the gate rather than <see cref="EdgeFlightOutcome.StartUnresolved"/>,
    /// which reads zero on every cache and is the harness refusing to guess.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryArcEdgeResolvesFromItsOwnNodeAndFromItsTakeoffColumn()
    {
        ReferenceTree.RequireNavCache();

        var edges = 0;
        var midResolves = 0;
        var takeoffResolves = 0;

        foreach (var (map, area) in NavCache.List(ReferenceTree.NavCacheDirectory))
        {
            var graph = NavCache.Load(ReferenceTree.NavCacheDirectory, map, area);

            for (var i = 0; i < graph.EdgeCount; i++)
            {
                ref readonly var edge = ref graph.Edges[i];

                if (edge.Type is not (NavEdgeType.RocketJump or NavEdgeType.DoubleJump))
                    continue;

                edges++;

                var y = graph.StandY(edge.From);
                var node = graph.Nodes[edge.From];
                var mid = NavGraph.ColumnWorldX((node.X0 + node.X1) / 2);

                if (graph.NodeFromWorld(mid, y) == edge.From)
                    midResolves++;

                if (graph.NodeFromWorld(NavGraph.ColumnWorldX(edge.Takeoff), y) == edge.From)
                    takeoffResolves++;
            }
        }

        output.WriteLine($"{edges} arc edges; midpoint resolves {midResolves}, takeoff column {takeoffResolves}");

        Assert.Equal(18_509, edges);
        Assert.Equal(edges, midResolves);
        Assert.Equal(edges, takeoffResolves);
    }

    /// <summary>
    /// A soldier flies real rocket-jump arcs off the shipped cache.
    /// </summary>
    /// <remarks>
    /// <b><see cref="EdgeFlight.Flown"/> is the load-bearing number and
    /// arrivals are not it.</b> Twenty of <c>gg_debug</c>'s 67 rocket jumps can be
    /// posed as a route, all twenty arrive, and only twelve of those arrivals
    /// involved a rocket — the other eight are node pairs close enough that the
    /// bot simply got there. On <c>ctf_truefort</c> the same split is 377 arrivals
    /// against 219 flights. A gate on arrivals alone would stay green with the arm
    /// deleted.
    /// <para>
    /// ⚠️ <b>Those two were 328 and 214 when written and nothing went red when
    /// they stopped being</b> — no test pinned a real map, so the number drifted
    /// in the docs while the code moved under it. That is the whole argument for
    /// <see cref="EveryEdgeOfEveryShippedGraphIsFlownOnTheShippingArm"/>: a
    /// measurement nobody pins is a measurement that quietly goes stale.
    /// </para>
    /// <para>
    /// Which is exactly what makes this the control the synthetic tests could
    /// not be. <b>Mutation-checked</b>: rip the ATTACK half out of
    /// <see cref="PathFollower.ApplyLaunch"/> and this goes <b>20 arrivals / 12
    /// flights → 16 / 8</b>. Note which half moves. It does <em>not</em> go to
    /// zero, because <see cref="BotPathState.RocketJumps"/> counts what the
    /// takeoff gate <em>asked</em> for rather than what the weapon fired — so the
    /// arrival count is the honest signal here and the manoeuvre count on its own
    /// would have stayed green for a third of the arcs.
    /// </para>
    /// </remarks>
    [Fact]
    public void RealRocketJumpArcsAreFlown()
    {
        var flights = EdgeFlights.Sweep(World(), NavEdgeType.RocketJump);
        var (measured, arrived, flown, blacklists) = Tally(flights);

        output.WriteLine($"{Map} rocket jumps: {EdgeFlights.Summarise(flights)}");

        Assert.Equal(67, flights.Count);
        Assert.Equal(21, measured);
        Assert.Equal(21, arrived);
        Assert.Equal(20, flown);

        // FIFTEEN, and pinning the real number rather than a hoped-for zero is the
        // point. ⚠️ It was TWO until RocketModel.Capable stopped a cold launcher
        // unplanning the route (2026-09-06), and the rise is the honest price of
        // the fix rather than a regression hiding inside it: the bot used to route
        // AROUND eight of these arcs while its launcher cooled — twelve of twenty
        // arrivals were flights — and now attempts all twenty-one, so the
        // off-route machinery's one-retry-then-ban runs on arcs that were
        // previously never tried.
        //
        // ⛔ **It is the SWEEP's spawn, not the bot's play.** EdgeFlights places a
        // freshly spawned character on the from-node and asks for the arc at once,
        // and a fresh weapon is ~45 ticks from firing (Weapon.events/Create.xml);
        // a bot that has been alive in a match is not. The control says so
        // exactly: with BENCH_FLIGHT_WARM the corpus blacklists are 4,667 against
        // the cold arm's 7,980 and the pre-fix 4,967 — LOWER than before the fix
        // once the spawn artefact is taken out. docs/TRAPS-BOTS.md, "A cold
        // launcher unplanned the route".
        //
        // ⛔ **And this sweep cannot price a blacklist, so read it as a diagnostic
        // rather than as a cost.** A single-hop flight ends before a ban has any
        // downstream consequence, and the experiment says so: raising
        // BotConstants.TakeoffPatience 60 -> 120 -> 400 moves the corpus blacklists
        // 7,980 -> 6,683 -> 5,594 and leaves stranded, flown, arrived, diverted and
        // ticks BIT-IDENTICAL. What does price it is BENCH_FLIGHT_ROUTES, where a
        // ban changes the rest of the route: 2,042,402 -> 2,001,641 ticks across
        // 3,866 routes for the Capable fix, and 3,722 -> 3,729 arrivals.
        // PathFollowerFixtureTests pins zero over a 137-node leg, which is the
        // other end of the same question.
        Assert.Equal(15, blacklists);
    }

    /// <summary>A scout flies real air-jump arcs off the shipped cache.</summary>
    /// <remarks>
    /// The kind with no weapon in its gate — nothing can be cold, so this arm was
    /// the one the launcher's refire never touched. ⚠️ It used to read "nine of ten
    /// here against twelve of twenty for the rocket"; since RocketModel.Capable the
    /// rocket is twenty of twenty-one and the contrast is gone, which is the point
    /// of the fix rather than a loss.
    /// </remarks>
    [Fact]
    public void RealDoubleJumpArcsAreFlown()
    {
        var flights = EdgeFlights.Sweep(World(), NavEdgeType.DoubleJump);
        var (measured, arrived, flown, blacklists) = Tally(flights);

        output.WriteLine($"{Map} air jumps: {EdgeFlights.Summarise(flights)}");

        Assert.Equal(23, flights.Count);
        Assert.Equal(11, measured);
        Assert.Equal(11, arrived);

        // FIVE, from ten, 2026-10-11 (gg2-rl N3, N4): a held flight takes the
        // second jump only where its plan needs or draws one, and six of these
        // arcs land on one held jump. Every one still arrives.
        Assert.Equal(5, flown);

        // ONE, re-recorded 2026-09-05 from zero when the start moved a pixel out
        // of the floor (EdgeFlights.StandingY). Every other number on this map is
        // unchanged, on both arc kinds — a pixel of start height is enough to cost
        // one arc a retry, which is worth knowing about the margins these arcs are
        // flown on rather than smoothing over.
        Assert.Equal(1, blacklists);
    }

    /// <summary>
    /// An arc A* routes around is skipped, and the skip is what makes the number
    /// mean anything.
    /// </summary>
    /// <remarks>
    /// <b>The premise this test asserts is its whole point.</b> A* costs a jump
    /// the same as a walk, so on <c>gg_debug</c> it goes round 47 of the 67
    /// rocket-jump arcs — and every one of those, driven anyway, is a bot walking
    /// somewhere while a counter says "rocket jump attempted". That is how
    /// <c>koth_harvest</c> reported four of four arcs "arriving" without the bot
    /// ever leaving the ground.
    /// <para>
    /// So: take a skipped arc, show the route really is longer than the one hop,
    /// and show that flying it anyway arrives <em>without</em> the manoeuvre. If
    /// the skip were dropped, that arrival would be counted as a success for an
    /// arm that did nothing.
    /// </para>
    /// </remarks>
    [Fact]
    public void AnArcAStarRoutesAroundIsSkippedRatherThanCreditedToTheArm()
    {
        var world = World();
        var flights = EdgeFlights.Sweep(world, NavEdgeType.RocketJump);

        var skipped = flights.Where(f => f.Outcome == EdgeFlightOutcome.RoutedAround).ToList();
        Assert.Equal(46, skipped.Count);

        // None of them is counted, in either direction: not as an arrival and not
        // as a refusal.
        foreach (var flight in skipped)
        {
            Assert.False(flight.Measured);
            Assert.False(flight.Flown);
        }

        // The premise, on one of them: the search really does prefer a longer way
        // round, so there is no route over which this arc could be steered.
        var graph = world().Nav!;
        var edge = graph.Edges[skipped[0].Edge];

        var follower = new PathFollower(graph);
        var state = new BotPathState();
        var query = EdgeFlights.QueryFor(edge.Type, Team.Red);

        var midFrom = NavGraph.ColumnWorldX((graph.Nodes[edge.From].X0 + graph.Nodes[edge.From].X1) / 2);
        var midTo = NavGraph.ColumnWorldX((graph.Nodes[edge.To].X0 + graph.Nodes[edge.To].X1) / 2);

        Assert.True(follower.SetGoal(state, midTo, graph.StandY(edge.To)));
        Assert.True(follower.Plan(state, midFrom, graph.StandY(edge.From), tick: 0, in query));

        output.WriteLine($"e{skipped[0].Edge} {edge.From}->{edge.To} planned as {string.Join("->", state.Path)}");

        Assert.True(
            state.Path.Count > 2,
            $"edge {skipped[0].Edge} was reported RoutedAround but the plan is the single hop");

        // And the arm never fires on the way, which is what the skip is protecting
        // the number from: this arrival belongs to the walk, not to the rocket.
        var driven = EdgeFlights.Fly(world(), skipped[0].Edge);
        Assert.Equal(EdgeFlightOutcome.RoutedAround, driven.Outcome);
    }

    /// <summary>
    /// The two premises that never fire on today's caches, recorded so that
    /// "never seen" is a measurement rather than an assumption.
    /// </summary>
    /// <remarks>
    /// <b>A branch nothing reaches is a claim nothing checks</b>, and this repo
    /// has been bitten twice in a week by exactly that shape. Both of these are
    /// the harness refusing to guess rather than dead code:
    /// <list type="bullet">
    /// <item>
    /// <see cref="EdgeFlightOutcome.StartUnresolved"/> cannot fire for a midpoint
    /// start — see <see cref="EveryArcEdgeResolvesFromItsOwnNodeAndFromItsTakeoffColumn"/>
    /// — and exists so that changing the start position cannot silently measure a
    /// different edge.
    /// </item>
    /// <item>
    /// <see cref="EdgeFlightOutcome.Shadowed"/> reads <b>zero across all 22,528
    /// arc edges</b> of the shipped and sticky caches, on both teams: no node pair
    /// carries a strictly cheaper routable edge of another kind. That is not the
    /// same as saying parallel edges are harmless — 11,615 pairs carry more than
    /// one kind, and picking among them by <em>index order</em> rather than by cost
    /// was a live bug for eight phases. Cost order simply happens to agree with the
    /// arc today.
    /// </item>
    /// </list>
    /// </remarks>
    [Fact]
    public void NoShippedArcIsShadowedByACheaperParallelEdge()
    {
        ReferenceTree.RequireNavCache();

        var arcs = 0;
        var shadowed = 0;

        foreach (var (map, area) in NavCache.List(ReferenceTree.NavCacheDirectory))
        {
            var graph = NavCache.Load(ReferenceTree.NavCacheDirectory, map, area);
            var follower = new PathFollower(graph);

            for (var i = 0; i < graph.EdgeCount; i++)
            {
                ref readonly var edge = ref graph.Edges[i];

                if (edge.Type is not (NavEdgeType.RocketJump or NavEdgeType.DoubleJump))
                    continue;

                foreach (var team in (ReadOnlySpan<Team>)[Team.Red, Team.Blue])
                {
                    var query = EdgeFlights.QueryFor(edge.Type, team);

                    // Gated out for this team: the search refuses it outright, so
                    // there is nothing for a cheaper edge to shadow.
                    if (!NavGraph.EdgeRoutable(in edge, in query))
                        continue;

                    arcs++;

                    // The follower's own answer, not a second implementation of
                    // the tie-break — that is what TryResolveHop is for.
                    var state = new BotPathState { PlanQuery = query };

                    if (!follower.TryResolveHop(state, edge.From, edge.To, out var steered) || steered != edge)
                        shadowed++;
                }
            }
        }

        output.WriteLine($"{arcs} routable arcs across both teams; {shadowed} shadowed");

        Assert.Equal(36_797, arcs);
        Assert.Equal(0, shadowed);
    }
}
