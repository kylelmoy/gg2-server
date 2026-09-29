using GG2.Bots;
using GG2.Protocol;
using Xunit.Abstractions;

namespace GG2.Tests;

/// <summary>
/// What occupancy contributes <em>in a match</em>, as opposed to what it is
/// capable of on one leg.
/// </summary>
/// <remarks>
/// <b><see cref="RouteDiversityTests"/> measures the wrong thing on purpose and
/// this file measures the right one.</b> There, four bots are made to ask for
/// the <em>identical</em> leg, which is the reference measurement's setup and
/// the worst case for single file. In a real match a team's bots do not share a
/// leg: <c>BotRoles.Assign</c> has already split them into attackers and
/// defenders, <c>BotRoles.SpreadX</c> has already given each a constant lateral
/// goal offset, and <c>BotGoalSpot</c> resolves a different standing spot per
/// bot. So some dispersion exists before occupancy is consulted at all, and the
/// number that decides whether occupancy can be replaced is its <b>marginal</b>
/// contribution on top of that — not its capability in isolation.
/// <para>
/// ⛔ <b>The control arm here is not hypothetical: it is what
/// <c>HeadlessMatch</c> has always done.</b> <c>ScriptedBot.Occupancy</c> is set
/// in exactly one place in the whole repo — <c>BotPopulationService</c>, the
/// live <c>--bots</c> server. Every <c>eval</c> number this project has ever
/// recorded, and every training rollout, was taken with occupancy OFF. That is
/// worth knowing on its own, and it means the no-occupancy arm is a
/// configuration that already ships rather than a thought experiment.
/// </para>
/// <para>
/// <b>Three seeds a map because one match is one sample of a stochastic
/// thing.</b> The arms diverge immediately — a different route is a different
/// match — so this is not a paired comparison and single-map differences should
/// not be read as effects. The column that matters is the mean across maps.
/// </para>
/// </remarks>
public sealed class RouteDiversityMatchTests(ITestOutputHelper output)
{
    /// <summary>
    /// Why every measurement in this file is skipped by default.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>These are measurements, not gates, and leaving them in the default
    /// run made the suite flake.</b> Each one plays dozens of 3,000-tick matches;
    /// together they added about eighty seconds of CPU-saturating simulation
    /// running in parallel with <c>ServerPacingTests</c>,
    /// <c>TickPacingTests</c> and <c>TimerResolutionTests</c>, which measure the
    /// wall clock. One full-suite run in four failed, and the failure was not
    /// reproduced in the next three — the signature of load, not of a bug.
    /// <para>
    /// That is this repo's own warning arriving from the other direction:
    /// <c>docs/BOTS.md</c>, "late ticks are a pacing measurement, and pacing
    /// measures the machine". A measurement that makes a timing gate flaky is
    /// worse than a slow one, because the next session cannot tell a real
    /// failure from this.
    /// </para>
    /// <para>
    /// The numbers live in <c>docs/TRAPS-BOTS.md</c>, "Route variety, scored",
    /// which is where this repo keeps a measured fact. Remove the Skip to
    /// re-take one, and run it alone.
    /// </para>
    /// </remarks>
    private const string Measurement =
        "Measurement, not a gate: ~80s of matches, and it makes the pacing tests flake. "
        + "Remove the Skip and run it alone. Numbers in docs/TRAPS-BOTS.md.";

    private const int Ticks = 3000;

    private const int SampleEvery = 60;

    private const int Bots = 12;

    private const int Seeds = 3;

    /// <summary>
    /// How many corridors a graph is cut into.
    /// </summary>
    /// <remarks>
    /// Four for six bots a team: enough that the split is visible, few enough
    /// that every lane still gets more than one bot on a full server. Not tuned —
    /// this is the first value tried, and tuning it belongs after the mechanism
    /// is shown to work at all.
    /// </remarks>
    private const int LaneCount = 4;

    /// <summary>
    /// Mean spread of a team's installed routes, sampled through one match.
    /// </summary>
    /// <remarks>
    /// <b>Only samples where at least three bots on a team hold a route are
    /// counted.</b> Two routes is a pair, and a pair's Jaccard distance is far
    /// noisier than a team's — and a sample taken while most of the team is dead
    /// would report the dispersion of whoever happened to be alive.
    /// </remarks>
    private static (double Spread, int Samples) PlayOne(string map, uint seed, bool crowded)
    {
        var options = HeadlessMatchOptions.Default with
        {
            Map = map,
            Bots = Bots,
            MaxTicks = Ticks,
            Seed = seed,
        };

        var match = HeadlessMatch.Load(options, ReferenceTree.MapsDirectory, ReferenceTree.NavCacheDirectory);

        // One instance serves both teams: BotOccupancy keys by team internally,
        // which is exactly how BotPopulationService holds it.
        var shared = crowded ? new BotOccupancy() : null;

        if (shared is not null)
        {
            for (var slot = 1; slot < match.SlotCount; slot++)
                if (ScriptedBot.Of(match.SourceOf(slot)) is { } bot)
                    bot.Occupancy = shared;
        }

        var total = 0.0;
        var samples = 0;

        for (var tick = 0; tick < Ticks; tick++)
        {
            match.Step();

            if (tick % SampleEvery != 0)
                continue;

            foreach (var team in (ReadOnlySpan<Team>)[Team.Red, Team.Blue])
            {
                var routes = new List<IReadOnlyList<int>>();

                for (var slot = 1; slot < match.SlotCount; slot++)
                {
                    if (match.State.Players[slot].Team != team)
                        continue;

                    if (ScriptedBot.Of(match.SourceOf(slot)) is not { } bot || !bot.Path.HasPath)
                        continue;

                    routes.Add(bot.Path.Path.ToArray());
                }

                if (routes.Count < 3)
                    continue;

                total += RouteDiversity.Spread(routes);
                samples++;
            }
        }

        return (samples == 0 ? 0 : total / samples, samples);
    }

    /// <summary>
    /// Occupancy's marginal contribution to route dispersion, per map.
    /// </summary>
    [Fact(Skip = Measurement)]
    public void HowMuchDispersionOccupancyAddsInAMatch()
    {
        ReferenceTree.RequireNavCache();

        var maps = NavCache.List(ReferenceTree.NavCacheDirectory)
            .Where(g => g.Area == 1 && !g.Map.StartsWith("gg_debug", StringComparison.Ordinal))
            .Select(g => g.Map)
            .ToArray();

        Assert.NotEmpty(maps);

        output.WriteLine($"{"map",-22} {"occ OFF",8} {"occ ON",8} {"delta",8} {"samples",8}");

        var offAll = 0.0;
        var onAll = 0.0;
        var counted = 0;

        foreach (var map in maps)
        {
            var off = 0.0;
            var on = 0.0;
            var samples = 0;

            for (var seed = 1u; seed <= Seeds; seed++)
            {
                var a = PlayOne(map, seed, crowded: false);
                var b = PlayOne(map, seed, crowded: true);

                off += a.Spread;
                on += b.Spread;
                samples += a.Samples + b.Samples;
            }

            off /= Seeds;
            on /= Seeds;

            offAll += off;
            onAll += on;
            counted++;

            output.WriteLine($"{map,-22} {off,8:F3} {on,8:F3} {on - off,8:+0.000;-0.000} {samples,8}");
        }

        output.WriteLine(string.Empty);
        output.WriteLine(
            $"MEAN over {counted} maps: occupancy OFF {offAll / counted:F3}, ON {onAll / counted:F3}, "
            + $"delta {(onAll - offAll) / counted:+0.000;-0.000}");
    }

    /// <summary>
    /// Dispersion against the goal spread, with occupancy OFF throughout.
    /// </summary>
    /// <remarks>
    /// <b>The cheap question, asked before any machinery is built.</b> The
    /// no-occupancy arm above already reaches most of what occupancy reaches,
    /// and all of that comes from the scripted layer choosing different
    /// <em>goals</em> — <c>BotRoles.Assign</c>, <c>BotRoles.SpreadX</c>,
    /// <c>BotGoalSpot</c>. If simply widening that spread recovers the rest,
    /// then "move route variety into the scripted layer" is a constant and a
    /// measurement rather than a lane-derivation project.
    /// <para>
    /// The multiplier scales each bot's <c>SpreadX</c> after construction rather
    /// than moving <c>BotConstants.SpreadMax</c>, so the sweep needs no rebuild
    /// and nothing in the shipped configuration moves while it runs.
    /// </para>
    /// <para>
    /// ⚠️ <b>Wider is not freely better and this cannot see the cost.</b>
    /// <c>docs/TRAPS-BOTS.md</c> records that the spread is skipped for a goal
    /// that must be TOUCHED, that a defender already sits 220px beside what it
    /// guards, and that a spread at <c>ctf_orange</c> node 1 drops 636px — most
    /// of the map. A spread that walks bots off the objective would score well
    /// here and play worse, which is what the walking-time suite and the two
    /// single-leg pins are for.
    /// </para>
    /// </remarks>
    [Fact(Skip = Measurement)]
    public void DispersionAgainstTheGoalSpreadWithNoOccupancy()
    {
        ReferenceTree.RequireNavCache();

        var maps = NavCache.List(ReferenceTree.NavCacheDirectory)
            .Where(g => g.Area == 1 && !g.Map.StartsWith("gg_debug", StringComparison.Ordinal))
            .Select(g => g.Map)
            .ToArray();

        double[] multipliers = [1.0, 1.5, 2.0, 3.0];

        output.WriteLine($"{"map",-22} {"x1.0",8} {"x1.5",8} {"x2.0",8} {"x3.0",8}");

        var totals = new double[multipliers.Length];

        foreach (var map in maps)
        {
            var row = new double[multipliers.Length];

            for (var m = 0; m < multipliers.Length; m++)
            {
                var sum = 0.0;

                for (var seed = 1u; seed <= Seeds; seed++)
                    sum += PlaySpread(map, seed, multipliers[m]).Spread;

                row[m] = sum / Seeds;
                totals[m] += row[m];
            }

            output.WriteLine(
                $"{map,-22} {row[0],8:F3} {row[1],8:F3} {row[2],8:F3} {row[3],8:F3}");
        }

        output.WriteLine(string.Empty);
        output.WriteLine(
            $"MEAN over {maps.Length} maps: "
            + string.Join("  ", multipliers.Select((x, i) => $"x{x:F1} {totals[i] / maps.Length:F3}")));
    }

    /// <summary>
    /// The three arms head to head, in matches: nothing, occupancy, lanes.
    /// </summary>
    /// <remarks>
    /// <b>This is the measurement the project turns on.</b> The leg-level
    /// head-to-head in <see cref="RouteDiversityTests"/> makes four bots ask for
    /// the identical leg, which never happens in play; here the bots pick their
    /// own goals through the objective layer, so what is scored is the dispersion
    /// a team actually ends up with.
    /// <para>
    /// ⛔ <b>The "none" arm is the incumbent for every number this project has
    /// ever published</b> — <c>ScriptedBot.Occupancy</c> is set only by
    /// <c>BotPopulationService</c>, so <c>eval</c> and every training rollout run
    /// there. Lanes have to beat <em>that</em> to be worth having at all, and
    /// match occupancy to replace it on the live server.
    /// </para>
    /// </remarks>
    [Fact(Skip = Measurement)]
    public void LanesAgainstOccupancyInMatches()
    {
        ReferenceTree.RequireNavCache();

        var maps = NavCache.List(ReferenceTree.NavCacheDirectory)
            .Where(g => g.Area == 1 && !g.Map.StartsWith("gg_debug", StringComparison.Ordinal))
            .Select(g => g.Map)
            .ToArray();

        output.WriteLine(
            $"{"map",-22} {"none",7} {"occ",7} {"lanes",7} | {"none infl",10} {"occ infl",9} {"lane infl",10}");

        double noneAll = 0, occAll = 0, laneAll = 0;
        double noneInf = 0, occInf = 0, laneInf = 0;

        foreach (var map in maps)
        {
            double none = 0, occ = 0, lane = 0, ni = 0, oi = 0, li = 0;

            for (var seed = 1u; seed <= Seeds; seed++)
            {
                var a = PlayArm(map, seed, Arm.None);
                var b = PlayArm(map, seed, Arm.Occupancy);
                var c = PlayArm(map, seed, Arm.Lanes);

                none += a.Spread;
                ni += a.Inflation;
                occ += b.Spread;
                oi += b.Inflation;
                lane += c.Spread;
                li += c.Inflation;
            }

            noneAll += none / Seeds;
            noneInf += ni / Seeds;
            occAll += occ / Seeds;
            occInf += oi / Seeds;
            laneAll += lane / Seeds;
            laneInf += li / Seeds;

            output.WriteLine(
                $"{map,-22} {none / Seeds,7:F3} {occ / Seeds,7:F3} {lane / Seeds,7:F3} | "
                + $"{ni / Seeds,10:P1} {oi / Seeds,9:P1} {li / Seeds,10:P1}");
        }

        var n = maps.Length;

        output.WriteLine(string.Empty);
        output.WriteLine(
            $"MEAN over {n} maps  spread: none {noneAll / n:F3}, occupancy {occAll / n:F3}, "
            + $"lanes {laneAll / n:F3}");
        output.WriteLine(
            $"                inflation: none {noneInf / n:P1}, occupancy {occInf / n:P1}, "
            + $"lanes {laneInf / n:P1}");
    }

    /// <summary>
    /// The lane penalty against what it buys and what it costs.
    /// </summary>
    /// <remarks>
    /// <b>Occupancy is far more EFFICIENT than lanes at the first penalty
    /// tried, and that is what this sweep is for.</b> Measured over 25 graphs:
    /// occupancy reaches 0.388 leg-level spread for 102.6% of four uncrowded
    /// routes' cost; lanes at penalty 1.75 reach 0.472 for <b>113.9%</b>. The
    /// gap is the price of being static — occupancy only diverts where crowding
    /// actually is and by as little as will do it, where a lane diverts every
    /// route through a corridor whether anyone is on it or not.
    /// <para>
    /// ⚠️ <b>14% is not a rounding error in this repo.</b>
    /// <c>NavConstants.SearchWeight</c> was chosen inside a ±2% band on
    /// <c>ScenarioSuiteTests</c>' summed walking time, and 1.5 was rejected for
    /// costing about 4%. A mechanism that inflates every route by 14% would be a
    /// large play regression that this file's dispersion score cannot see.
    /// </para>
    /// <para>
    /// So the question is not "do lanes disperse" — they do — but whether a
    /// gentler penalty keeps the MATCH-level dispersion, where most of the
    /// spread comes from the objective layer choosing different goals anyway,
    /// while giving the route cost back.
    /// </para>
    /// </remarks>
    [Fact(Skip = Measurement)]
    public void WhatTheLanePenaltyBuysAndWhatItCosts()
    {
        ReferenceTree.RequireNavCache();

        var maps = NavCache.List(ReferenceTree.NavCacheDirectory)
            .Where(g => g.Area == 1 && !g.Map.StartsWith("gg_debug", StringComparison.Ordinal))
            .Select(g => g.Map)
            .ToArray();

        double[] penalties = [1.20, 1.35, 1.75];
        int[] counts = [2, 3, 4];

        output.WriteLine($"{"lanes",6} {"penalty",8} {"match spread",13}");

        foreach (var lanes in counts)
        {
            foreach (var penalty in penalties)
            {
                var spread = 0.0;

                foreach (var map in maps)
                    for (var seed = 1u; seed <= Seeds; seed++)
                        spread += PlayArm(map, seed, Arm.Lanes, penalty, lanes).Spread;

                output.WriteLine(
                    $"{lanes,6} {penalty,8:F2} {spread / (maps.Length * Seeds),13:F3}");
            }
        }

        output.WriteLine(string.Empty);
        output.WriteLine("reference: match spread none 0.634, occupancy 0.785; route cost occupancy 102.6%");
    }

    /// <summary>
    /// What one map's lanes cost, against the same routes on lane 0.
    /// </summary>
    private static double LaneRouteCost(string map, double penalty)
    {
        var graph = NavCache.Load(ReferenceTree.NavCacheDirectory, map, 1);
        var lanes = BotLanes.Derive(graph, LaneCount, penalty);
        var search = new NavSearch(graph);
        var query = new NavQuery(Team.Red);
        var route = new List<int>();

        double baseCost = 0, laneCost = 0;
        var legs = 0;

        for (var from = 0; from < graph.NodeCount && legs < 200; from += 7)
        {
            for (var to = 0; to < graph.NodeCount && legs < 200; to += 11)
            {
                if (from == to || !search.Find(from, to, query, route, null, null, lanes.Costs(0).Span))
                    continue;

                legs++;
                var zero = Priced(graph, route);
                baseCost += zero * LaneCount;
                laneCost += zero;

                for (var lane = 1; lane < LaneCount; lane++)
                    if (search.Find(from, to, query, route, null, null, lanes.Costs(lane).Span))
                        laneCost += Priced(graph, route);
            }
        }

        return baseCost == 0 ? 1 : laneCost / baseCost;
    }

    /// <summary>A route priced with the graph's own edge costs.</summary>
    private static double Priced(NavGraph graph, IReadOnlyList<int> route)
    {
        var total = 0.0;

        for (var i = 1; i < route.Count; i++)
        {
            var best = double.PositiveInfinity;

            foreach (var edge in graph.EdgesFrom(route[i - 1]))
                if (edge.To == route[i] && edge.Cost < best)
                    best = edge.Cost;

            if (!double.IsPositiveInfinity(best))
                total += best;
        }

        return total;
    }

    /// <summary>Which route-variety mechanism a match runs.</summary>
    private enum Arm
    {
        None,
        Occupancy,
        Lanes,

        /// <summary>
        /// A\* given the exact precomputed heuristic — <c>--nav-distances</c>.
        /// <b>Not a route-variety mechanism at all</b>, and it lives in this
        /// enum because this file owns the only instrument that prices a route
        /// against the cheapest route between its own endpoints.
        /// </summary>
        Distances,
    }

    /// <summary>One match under one mechanism, scored.</summary>
    private static (double Spread, int Samples, double Inflation) PlayArm(
        string map, uint seed, Arm arm, double penalty = BotLanes.Penalty, int laneCount = LaneCount)
    {
        var options = HeadlessMatchOptions.Default with
        {
            Map = map,
            Bots = Bots,
            MaxTicks = Ticks,
            Seed = seed,
            NavDistances = arm == Arm.Distances,
        };

        var match = HeadlessMatch.Load(options, ReferenceTree.MapsDirectory, ReferenceTree.NavCacheDirectory);

        var shared = arm == Arm.Occupancy ? new BotOccupancy() : null;
        var lanes = arm == Arm.Lanes && match.Nav is { } nav
            ? BotLanes.Derive(nav, laneCount, penalty)
            : null;

        // Lane by place among the bot's OWN TEAM, never by slot: teams alternate
        // down the roster, so a slot-keyed lane would hand red the even lanes and
        // blue the odd ones and use half the corridors. BotRoles.Lane says so.
        var placed = new Dictionary<Team, int>();

        for (var slot = 1; slot < match.SlotCount; slot++)
        {
            if (ScriptedBot.Of(match.SourceOf(slot)) is not { } bot)
                continue;

            bot.Occupancy = shared;

            if (lanes is null)
                continue;

            var team = match.State.Players[slot].Team;
            placed.TryGetValue(team, out var place);
            placed[team] = place + 1;

            bot.Lanes = lanes;
            bot.Lane = BotRoles.Lane(place, laneCount);
        }

        return Sample(match);
    }

    /// <summary>
    /// What the precomputed heuristic does to route cost and to dispersion.
    /// </summary>
    /// <remarks>
    /// <b>The question <c>--nav-distances</c> could not be shipped without.</b>
    /// <c>NavConstants.SearchWeight</c> makes this a WEIGHTED A\*, so the
    /// heuristic decides which route comes back and not only how fast it comes
    /// back: with the straight-line estimate the inflated heuristic still sits at
    /// about 0.914 of the true remaining cost, and with an exact one it sits at
    /// the weight itself. Every route therefore moves, and whether they move for
    /// better or worse is not derivable from the speed-up.
    /// <para>
    /// ⚠️ <b>The "ideal" this prices against is itself produced by the same
    /// deliberately-suboptimal A\* on the straight-line heuristic</b>, so it is an
    /// upper bound and not the true optimum. An arm reading BELOW 100% is
    /// therefore meaningful rather than impossible: it is routing better than the
    /// incumbent search does.
    /// </para>
    /// </remarks>
    [Fact(Skip = Measurement)]
    public void WhatThePrecomputedHeuristicCostsInRouteCost()
    {
        ReferenceTree.RequireNavCache();

        var maps = NavCache.List(ReferenceTree.NavCacheDirectory)
            .Where(g => g.Area == 1 && !g.Map.StartsWith("gg_debug", StringComparison.Ordinal))
            .Select(g => g.Map)
            .ToArray();

        output.WriteLine($"{"map",-22} {"none",7} {"dist",7} | {"none infl",10} {"dist infl",10}");

        double noneAll = 0, distAll = 0, noneInf = 0, distInf = 0;

        foreach (var map in maps)
        {
            double none = 0, dist = 0, ni = 0, di = 0;

            for (var seed = 1u; seed <= Seeds; seed++)
            {
                var a = PlayArm(map, seed, Arm.None);
                var b = PlayArm(map, seed, Arm.Distances);

                none += a.Spread;
                ni += a.Inflation;
                dist += b.Spread;
                di += b.Inflation;
            }

            noneAll += none / Seeds;
            noneInf += ni / Seeds;
            distAll += dist / Seeds;
            distInf += di / Seeds;

            output.WriteLine(
                $"{map,-22} {none / Seeds,7:F3} {dist / Seeds,7:F3} | "
                + $"{ni / Seeds,10:P1} {di / Seeds,10:P1}");
        }

        var n = maps.Length;

        output.WriteLine(string.Empty);
        output.WriteLine($"MEAN over {n} maps  spread: none {noneAll / n:F3}, distances {distAll / n:F3}");
        output.WriteLine($"                inflation: none {noneInf / n:P1}, distances {distInf / n:P1}");
    }

    /// <summary>
    /// How often a PRECOMPUTED route would still have been the right answer.
    /// </summary>
    /// <remarks>
    /// <b>The question behind "why store distances and not routes".</b> A stored
    /// route is still optimal only if nothing has made one of its own hops dearer
    /// or illegal since it was computed. Two things can:
    /// <see cref="BotOccupancy"/> raises the cost of an edge a team-mate is
    /// walking, and a bot's anti-thrash blacklist removes one outright. Either
    /// means the stored answer may no longer be the cheapest, and a lookup that
    /// returns a stale route is a bot walking a worse way for the rest of its leg.
    /// <para>
    /// ⚠️ <b>A bot's OWN route is in the occupancy counts</b> —
    /// <c>BotOccupancy.Add</c> put it there — so "somebody else is on this hop"
    /// is a count of two or more, not of one. Counting one would call every route
    /// in the match contended and the answer would be a flat 0%.
    /// </para>
    /// <para>
    /// ⛔ <b>The no-occupancy arm is the control, and it reads 98.4% rather than
    /// 100% — which is the point.</b> This test first asserted an exact 100% and
    /// FAILED, correctly: occupancy is not the only thing that can invalidate a
    /// stored route. The blacklist is per bot and is live in every arm, so about
    /// <b>1.6% of routes are stale even with no crowding mechanism at all</b> —
    /// which means <c>eval</c> and the training rollouts would still need the
    /// guard, not just the live server.
    /// </para>
    /// </remarks>
    [Fact(Skip = Measurement)]
    public void HowOftenAStoredRouteWouldStillBeValid()
    {
        ReferenceTree.RequireNavCache();

        var maps = NavCache.List(ReferenceTree.NavCacheDirectory)
            .Where(g => g.Area == 1 && !g.Map.StartsWith("gg_debug", StringComparison.Ordinal))
            .Select(g => g.Map)
            .ToArray();

        output.WriteLine($"{"map",-22} {"no occupancy",13} {"with occupancy",15} {"routes",8}");

        long controlClean = 0, controlAll = 0, liveClean = 0, liveAll = 0;

        foreach (var map in maps)
        {
            long cc = 0, ca = 0, lc = 0, la = 0;

            for (var seed = 1u; seed <= Seeds; seed++)
            {
                var (c1, a1) = StoredRouteValidity(map, seed, occupancyOn: false);
                var (c2, a2) = StoredRouteValidity(map, seed, occupancyOn: true);

                cc += c1;
                ca += a1;
                lc += c2;
                la += a2;
            }

            controlClean += cc;
            controlAll += ca;
            liveClean += lc;
            liveAll += la;

            output.WriteLine(
                $"{map,-22} {(ca == 0 ? 0 : (double)cc / ca),13:P1} "
                + $"{(la == 0 ? 0 : (double)lc / la),15:P1} {la,8}");
        }

        output.WriteLine(string.Empty);
        output.WriteLine(
            $"OVER {maps.Length} maps  no occupancy {(double)controlClean / controlAll:P1} "
            + $"({controlAll} routes), with occupancy {(double)liveClean / liveAll:P1} "
            + $"({liveAll} routes)");

        // The control, asserted rather than eyeballed. NOT an exact 100%: the
        // blacklist runs in this arm too, and about 1.6% of routes carry a banned
        // hop. What must hold is that the control is near-clean while the live arm
        // is not — if both drifted together this would be measuring its own
        // bookkeeping rather than contention.
        Assert.True(
            (double)controlClean / controlAll > 0.90,
            $"control arm only {(double)controlClean / controlAll:P1} clean; "
            + "with no occupancy running almost every route should still be valid");

        Assert.True(
            (double)liveClean / liveAll < (double)controlClean / controlAll,
            "occupancy invalidated no more routes than the control did");
    }

    /// <summary>
    /// Plays one match and counts installed routes that nothing has invalidated.
    /// </summary>
    private static (long Clean, long All) StoredRouteValidity(string map, uint seed, bool occupancyOn)
    {
        var options = HeadlessMatchOptions.Default with
        {
            Map = map,
            Bots = Bots,
            MaxTicks = Ticks,
            Seed = seed,
        };

        var match = HeadlessMatch.Load(options, ReferenceTree.MapsDirectory, ReferenceTree.NavCacheDirectory);
        var shared = occupancyOn ? new BotOccupancy() : null;

        for (var slot = 1; slot < match.SlotCount; slot++)
        {
            if (ScriptedBot.Of(match.SourceOf(slot)) is { } b)
                b.Occupancy = shared;
        }

        long clean = 0;
        long all = 0;

        for (var tick = 0; tick < Ticks; tick++)
        {
            match.Step();

            if (tick % SampleEvery != 0)
                continue;

            for (var slot = 1; slot < match.SlotCount; slot++)
            {
                if (ScriptedBot.Of(match.SourceOf(slot)) is not { } bot || !bot.Path.HasPath)
                    continue;

                var route = bot.Path.Path;

                if (route.Count < 2)
                    continue;

                var team = match.State.Players[slot].Team;
                var counts = shared?.Counts(team);
                var contended = false;

                for (var i = 0; i + 1 < route.Count && !contended; i++)
                {
                    var key = NavGraph.EdgeKey(route[i], route[i + 1]);

                    // Two or more, because this bot's own route is in the counts.
                    if (counts is not null && counts.TryGetValue(key, out var crowd) && crowd >= 2)
                        contended = true;

                    if (bot.Path.Blacklist.Contains(key))
                        contended = true;
                }

                all++;

                if (!contended)
                    clean++;
            }
        }

        return (clean, all);
    }

    /// <summary>Steps a prepared match, scoring each team's routes as it goes.</summary>
    /// <remarks>
    /// <b>Inflation is measured here and not on the scenario suite, because the
    /// scenario suite cannot see occupancy at all.</b> Those legs run ONE bot, so
    /// an occupancy map has nothing in it and the arm scores exactly 100% — which
    /// would have let lanes be charged a walking-time cost that occupancy was
    /// never charged. Each installed route is priced against the cheapest route
    /// between its OWN endpoints on the untouched graph, so both mechanisms are
    /// quoted in the same units in the same matches.
    /// </remarks>
    private static (double Spread, int Samples, double Inflation) Sample(HeadlessMatch match)
    {
        var total = 0.0;
        var samples = 0;
        var inflation = 0.0;
        var priced = 0;

        var search = match.Nav is null ? null : new NavSearch(match.Nav);
        var ideal = new List<int>();

        for (var tick = 0; tick < Ticks; tick++)
        {
            match.Step();

            if (tick % SampleEvery != 0)
                continue;

            foreach (var team in (ReadOnlySpan<Team>)[Team.Red, Team.Blue])
            {
                var routes = new List<IReadOnlyList<int>>();

                // ⚠️ The query each route was PLANNED under, not a fresh one for
                // the team. `BotPathState.PlanQuery` is recorded with the route
                // for exactly this reason, and pricing a route against a
                // cheapest-route computed under a DIFFERENT query is not a
                // comparison at all: on the setup-gate maps a bot routes with the
                // gates shut and the ideal would be computed with them open, so
                // the detour the gates forced is charged to whatever mechanism
                // happens to be running.
                var queries = new List<NavQuery>();

                for (var slot = 1; slot < match.SlotCount; slot++)
                {
                    if (match.State.Players[slot].Team != team)
                        continue;

                    if (ScriptedBot.Of(match.SourceOf(slot)) is not { } bot || !bot.Path.HasPath)
                        continue;

                    routes.Add(bot.Path.Path.ToArray());
                    queries.Add(bot.Path.PlanQuery);
                }

                if (routes.Count < 3)
                    continue;

                total += RouteDiversity.Spread(routes);
                samples++;

                if (search is null || match.Nav is not { } nav)
                    continue;

                for (var i = 0; i < routes.Count; i++)
                {
                    var route = routes[i];

                    if (route.Count < 2)
                        continue;

                    // The honest cheapest route between the same two nodes for
                    // the same asker: no lane, no occupancy, no blacklist.
                    if (!search.Find(route[0], route[^1], queries[i], ideal))
                        continue;

                    var best = Priced(nav, ideal);
                    if (best <= 0)
                        continue;

                    inflation += Priced(nav, route) / best;
                    priced++;
                }
            }
        }

        return (
            samples == 0 ? 0 : total / samples,
            samples,
            priced == 0 ? 1 : inflation / priced);
    }

    /// <summary>One match with every bot's lateral goal offset scaled.</summary>
    private static (double Spread, int Samples) PlaySpread(string map, uint seed, double multiplier)
    {
        var options = HeadlessMatchOptions.Default with
        {
            Map = map,
            Bots = Bots,
            MaxTicks = Ticks,
            Seed = seed,
        };

        var match = HeadlessMatch.Load(options, ReferenceTree.MapsDirectory, ReferenceTree.NavCacheDirectory);

        for (var slot = 1; slot < match.SlotCount; slot++)
            if (ScriptedBot.Of(match.SourceOf(slot)) is { } bot)
                bot.SpreadX *= multiplier;

        var total = 0.0;
        var samples = 0;

        for (var tick = 0; tick < Ticks; tick++)
        {
            match.Step();

            if (tick % SampleEvery != 0)
                continue;

            foreach (var team in (ReadOnlySpan<Team>)[Team.Red, Team.Blue])
            {
                var routes = new List<IReadOnlyList<int>>();

                for (var slot = 1; slot < match.SlotCount; slot++)
                {
                    if (match.State.Players[slot].Team != team)
                        continue;

                    if (ScriptedBot.Of(match.SourceOf(slot)) is not { } bot || !bot.Path.HasPath)
                        continue;

                    routes.Add(bot.Path.Path.ToArray());
                }

                if (routes.Count < 3)
                    continue;

                total += RouteDiversity.Spread(routes);
                samples++;
            }
        }

        return (samples == 0 ? 0 : total / samples, samples);
    }
}
