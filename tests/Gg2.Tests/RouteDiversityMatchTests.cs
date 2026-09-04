using Gg2.Bots;
using Gg2.Protocol;
using Xunit.Abstractions;

namespace Gg2.Tests;

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
    [Fact]
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
    [Fact]
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
    [Fact]
    public void LanesAgainstOccupancyInMatches()
    {
        ReferenceTree.RequireNavCache();

        var maps = NavCache.List(ReferenceTree.NavCacheDirectory)
            .Where(g => g.Area == 1 && !g.Map.StartsWith("gg_debug", StringComparison.Ordinal))
            .Select(g => g.Map)
            .ToArray();

        output.WriteLine($"{"map",-22} {"none",8} {"occupancy",10} {"lanes",8} {"lanes-occ",10}");

        var noneAll = 0.0;
        var occAll = 0.0;
        var laneAll = 0.0;

        foreach (var map in maps)
        {
            double none = 0, occ = 0, lane = 0;

            for (var seed = 1u; seed <= Seeds; seed++)
            {
                none += PlayArm(map, seed, Arm.None).Spread;
                occ += PlayArm(map, seed, Arm.Occupancy).Spread;
                lane += PlayArm(map, seed, Arm.Lanes).Spread;
            }

            none /= Seeds;
            occ /= Seeds;
            lane /= Seeds;

            noneAll += none;
            occAll += occ;
            laneAll += lane;

            output.WriteLine(
                $"{map,-22} {none,8:F3} {occ,10:F3} {lane,8:F3} {lane - occ,10:+0.000;-0.000}");
        }

        output.WriteLine(string.Empty);
        output.WriteLine(
            $"MEAN over {maps.Length} maps: none {noneAll / maps.Length:F3}, "
            + $"occupancy {occAll / maps.Length:F3}, lanes {laneAll / maps.Length:F3}");
    }

    /// <summary>Which route-variety mechanism a match runs.</summary>
    private enum Arm
    {
        None,
        Occupancy,
        Lanes,
    }

    /// <summary>One match under one mechanism, scored.</summary>
    private static (double Spread, int Samples) PlayArm(string map, uint seed, Arm arm)
    {
        var options = HeadlessMatchOptions.Default with
        {
            Map = map,
            Bots = Bots,
            MaxTicks = Ticks,
            Seed = seed,
        };

        var match = HeadlessMatch.Load(options, ReferenceTree.MapsDirectory, ReferenceTree.NavCacheDirectory);

        var shared = arm == Arm.Occupancy ? new BotOccupancy() : null;
        var lanes = arm == Arm.Lanes && match.Nav is { } nav ? BotLanes.Derive(nav, LaneCount) : null;

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
            bot.Lane = BotRoles.Lane(place, LaneCount);
        }

        return Sample(match);
    }

    /// <summary>Steps a prepared match, scoring each team's routes as it goes.</summary>
    private static (double Spread, int Samples) Sample(HeadlessMatch match)
    {
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
