using Gg2.Protocol;

namespace Gg2.Bots;

/// <summary>
/// The fourteen behaviour scenarios, ported from
/// <c>gg2-agent/tools/bot-scenarios.js</c>.
/// </summary>
/// <remarks>
/// <b>Adding a scenario is an edit to this file and nothing else.</b> That is
/// the property worth keeping: in the reference the runner lives in Node
/// because every new GML script costs a full Game Maker build, and a harness
/// that wanted GML per scenario would cost a minute per test written.
/// <para>
/// ⚠️ <b>These are not yet executable assertions in this port, and saying so is
/// the point.</b> Every one of them runs a bot, and there is no bot behaviour
/// here yet — that is phase 7 item 4. What is ported now is the <em>data</em>
/// and the harness that will run it (<see cref="ScenarioWorld"/>,
/// <see cref="ScenarioRunner"/>), so that item 4 is measurable from its first
/// commit rather than felt. <c>ScenarioDefinitionTests</c> gates what can be
/// gated without behaviour: that every coordinate resolves to a real nav node
/// on its real map, which is the check the reference's runner reports as VOID.
/// </para>
/// <para>
/// The measurements quoted in each <c>about</c> are the reference
/// implementation's, against the GM8 bots. They are a target, not a contract:
/// this port's bots are expected to differ, and where one of these numbers is
/// missed the question is whether the leg is possible at all, not whether the
/// tick count matches.
/// </para>
/// </remarks>
public static class Scenarios
{
    /// <summary>Every scenario, in the reference's own order.</summary>
    public static IReadOnlyList<Scenario> All { get; } =
    [
        new Scenario(
            name: "valley-floor-to-point",
            map: "koth_valley",
            about:
                "The four-jump climb out of the valley floor onto the control point. This is "
                + "the chain the jump-edge work was built for — each link has to land for the "
                + "next to be attempted — so it is the most sensitive scenario here to any "
                + "change in jump edge geometry or in how the follower flies an arc. "
                + "⚠️ `from` is the lowest node within 900px of the point, found by asking the "
                + "graph rather than read off a screenshot: [1200, 900] looks like the valley "
                + "floor and has no node under it at all.",
            playerClass: PlayerClass.Soldier,
            team: Team.Red,
            from: new ScenarioPoint(1974, 859),
            to: new ScenarioPoint(2331, 546),
            budget: 900),

        new Scenario(
            name: "valley-spawn-to-point",
            map: "koth_valley",
            about:
                "The ordinary route a bot takes every round: red spawn to the control point. "
                + "Longer and easier than the valley climb, and it exercises the team gate on "
                + "the way out.",
            playerClass: PlayerClass.Soldier,
            team: Team.Red,
            from: new ScenarioPoint(784, 581),
            to: new ScenarioPoint(2331, 546),
            budget: 1200),

        new Scenario(
            name: "valley-point-to-spawn",
            map: "koth_valley",
            about:
                "The return leg. ⚠️ Worth having separately from the outbound one because "
                + "gates are evaluated per query against the asking team, so a route that "
                + "works one way is not evidence about the other.",
            playerClass: PlayerClass.Soldier,
            team: Team.Red,
            from: new ScenarioPoint(2331, 546),
            to: new ScenarioPoint(784, 581),
            budget: 1200),

        new Scenario(
            name: "valley-shaft-crate-to-point",
            map: "koth_valley",
            about:
                "The climb OUT of the control point shaft, starting on the crate at its "
                + "bottom. The shaft is a dogleg — the free channel jogs three columns left at "
                + "the ledge row — so the climb rises AND moves sideways, which a straight-up "
                + "ceiling scan does not model. This does not test arrival: the bot always got "
                + "out eventually by walking a ~45-node detour through the rest of the map. It "
                + "tests the COST of getting out — 532 and 673 ticks by the detour, 100 with "
                + "the rung present. ⚠️ `from` is ABOVE the crate, not on it: the snap searches "
                + "downward, so a point level with the surface you mean resolves to whatever "
                + "is under it instead.",
            playerClass: PlayerClass.Soldier,
            team: Team.Red,
            from: new ScenarioPoint(2250, 780),
            to: new ScenarioPoint(2331, 546),
            budget: 1200,
            expect: new ScenarioExpectations(MaxTicks: 300)),

        new Scenario(
            name: "valley-shaft-floor-to-point",
            map: "koth_valley",
            about:
                "The same shaft one rung lower, from the underground floor rather than from "
                + "the crate. Worth having separately because it exercises the floor→crate "
                + "rung as well, and because it is the position a bot actually falls into. "
                + "⚠️ Measured 489 and 1139 ticks by the detour — within a 1200 budget only "
                + "barely, which is why this always read as 'the bot took the long way' rather "
                + "than as a failure, and why arrival alone asserts nothing useful here. 161 "
                + "ticks with the rung present.",
            playerClass: PlayerClass.Soldier,
            team: Team.Red,
            from: new ScenarioPoint(2150, 815),
            to: new ScenarioPoint(2331, 546),
            budget: 1200,
            expect: new ScenarioExpectations(MaxTicks: 400)),

        new Scenario(
            name: "truefort-spawn-to-intel",
            map: "ctf_truefort",
            about:
                "Red spawn to the enemy intel on the largest shipped map — the long-route "
                + "case, and where a pruning regression in the jump edge generator would show "
                + "up first. It is the only scenario covering two stacked jumps in a row, and "
                + "both of the bugs that made it a known failure lived in exactly that: the "
                + "follower advancing its route index in mid-air, and ⚠️ navNodeFromWorld "
                + "flooring the feet row so a bot on a ramp believed it was a step behind "
                + "where it stood. Arrives in 2200-2500 ticks with no blacklists.",
            playerClass: PlayerClass.Soldier,
            team: Team.Red,
            from: new ScenarioPoint(384, 864),
            to: new ScenarioPoint(4962, 864),
            budget: 6000),

        new Scenario(
            name: "truefort-blue-spawn-exit-west",
            map: "ctf_truefort",
            about:
                "The first move of every blue life: out of the spawn shelf and west over the "
                + "hole. ⚠️ It exists because the search used to route this leg wrong for every "
                + "bot in the game and nothing noticed for a whole milestone. The direct jump "
                + "costs 33.67; the alternative is three hops across two SINGLE-COLUMN ledges "
                + "six pixels wide at 54.67 — and a bot that misses one lands in a pit whose "
                + "only exit is behind a BLUE TEAM GATE, which is closed to a carrier, so it "
                + "is stuck there until something kills it. Measured before the fix: 1,484 "
                + "stuck fires and 724 off-route events, never arriving. After: 58 ticks. Run "
                + "this first if per-bot route jitter ever comes back in any form.",
            playerClass: PlayerClass.Soldier,
            team: Team.Blue,
            from: new ScenarioPoint(3630, 456),
            to: new ScenarioPoint(3480, 456),
            budget: 600,
            expect: new ScenarioExpectations(MaxTicks: 300)),

        new Scenario(
            name: "avanti-ramp-pocket-to-intel",
            map: "ctf_avanti",
            about:
                "Found by scanning the graph rather than by watching a bot, and it was two "
                + "bugs on one leg. The start node stands one anchor column from a 54px block "
                + "whose top is the way out; the climb needs 54px of a 57.4px apex, so the "
                + "character cannot move sideways AT ALL before it is over — and the clearance "
                + "walk treated that first cell of lateral overlap as fatal instead of as a "
                + "wall to slide up. With no edge there the graph priced this run at 538 cells "
                + "against a straight line of 76, the worst ratio on any shipped map. Now 205 "
                + "cells and 352-438 ticks. ⚠️ The budget assumes the run starts past setup: "
                + "ctf_avanti has 63 setup-gated edges and a run started inside setup is "
                + "routed the long way round quite correctly, which looks like flake and is "
                + "not.",
            playerClass: PlayerClass.Soldier,
            team: Team.Red,
            from: new ScenarioPoint(1626, 817),
            to: new ScenarioPoint(1218, 607),
            budget: 600),

        new Scenario(
            name: "gallery-pit-climb",
            map: "koth_gallery",
            about:
                "The takeoff-speed case, and ⚠️ it reproduces as a HEAVY while arriving "
                + "instantly as a scout — which is the whole shape of the bug. The generator "
                + "proves an arc at a constant NAV_JUMP_VX (4.53), which is literally a "
                + "heavy's ceiling, so an arc near that speed leaves the in-flight tracker no "
                + "surplus to make back the acceleration ramp. This leg climbs out of a "
                + "V-shaped pit whose walls are one-column staircases, so the bot arrives at "
                + "each takeoff moving the wrong way with nowhere to run up. Before: heavy and "
                + "soldier never arrived in 1200 ticks while a scout arrived in 101. After: "
                + "271 and 135, scout 125.",
            playerClass: PlayerClass.Heavy,
            team: Team.Red,
            from: new ScenarioPoint(1102, 906),
            to: new ScenarioPoint(1228, 738),
            budget: 1200,
            expect: new ScenarioExpectations(MaxTicks: 800),
            known:
                "⚠️ This leg is a coin flip and its verdict must NOT be used as an A/B signal. "
                + "Run in isolation on one unchanged build it measured 2/5 and then 1/6, with "
                + "passes at 123-319 ticks and failures all landing 222-259px short — and the "
                + "failures repeat IDENTICAL triples, so they are not independent samples of a "
                + "random process either: something in the entry state decides it and then "
                + "plays out the same way every time. It was believed once and cost a wrong "
                + "revert. Use valley-shaft-floor-to-point or oldfort-perch-reroutes to "
                + "compare builds. What this is still good for is the thing it was written "
                + "for: if it fails on EVERY run, the takeoff machinery is broken."),

        new Scenario(
            name: "soldier-shells-generator",
            map: "gen_destroy",
            about:
                "A soldier with a clear line to the enemy generator should shell it from where "
                + "it stands. ⚠️ This pins the finding that bots had NEVER damaged a generator: "
                + "botFindTarget iterated with(Character) and nothing else, so a 2,100 hp "
                + "objective that is shot rather than stood on was invisible to every bot in "
                + "the game. A regression there is silent — the round simply never ends. "
                + "Measured 206 and 165 damage over 600 ticks; the floor is far below that "
                + "because this asserts 'it shoots the thing at all' rather than a "
                + "damage-per-second figure that moves with any weapon tuning. ⚠️ No `moved` "
                + "bound, and that is not an oversight: 23, 42 and then 232px on three runs — "
                + "a soldier shelling something 460px away stands in its own splash.",
            playerClass: PlayerClass.Soldier,
            team: Team.Red,
            from: new ScenarioPoint(2358, 235),
            hold: 600,
            expect: new ScenarioExpectations(Acquired: true, MinDamage: 50)),

        new Scenario(
            name: "soldier-holds-and-fires",
            map: "koth_valley",
            about:
                "The plainest combat case: an enemy 200px away, on the same floor, outside the "
                + "soldier's splash-safe band. It should acquire, fire, and not walk anywhere. "
                + "A failure here is the see/acquire/aim/fire chain itself rather than "
                + "anything about a particular map. ⚠️ No `moved` bound, for a reason worth "
                + "knowing before adding one: measured 57, 1, 1 and then 576px across four "
                + "runs. The soldier's own rockets knock the dummy toward it, the dummy drifts "
                + "inside the splash-safe band, and the bot then walks away from it — so a "
                + "scenario about firing turns into one about retreating.",
            playerClass: PlayerClass.Soldier,
            team: Team.Red,
            from: new ScenarioPoint(1880, 859),
            hold: 400,
            enemies: [new ScenarioEnemy(PlayerClass.Heavy, new ScenarioPoint(2080, 859))],
            expect: new ScenarioExpectations(Acquired: true, MinDamage: 1)),

        new Scenario(
            name: "sniper-fires-at-close-range",
            map: "koth_valley",
            about:
                "A sniper with an enemy well inside its zoom range must actually shoot it. "
                + "⚠️ This is the regression test for a bug this harness found: the trigger was "
                + "gated on rifle charge, charge only accumulates while zoomed, and the bot "
                + "only zooms past 400px — so from the minimum band out to ~250-400px it "
                + "acquired an enemy, tracked it all the way in, and never fired. Measured 0 "
                + "damage over four runs before the fix and 280-315 after, which is the "
                + "Rifle's unscoped 35 landing repeatedly. ⚠️ Both points sit near the MIDDLE "
                + "of their node rather than at its edges: placement 14px from the edge made "
                + "this flaky at 264px of drift, because an evasive hop that close to a lip "
                + "walks the bot off the platform.",
            playerClass: PlayerClass.Sniper,
            team: Team.Red,
            from: new ScenarioPoint(1930, 859),
            hold: 400,
            enemies: [new ScenarioEnemy(PlayerClass.Heavy, new ScenarioPoint(2030, 859))],
            expect: new ScenarioExpectations(Acquired: true, MinDamage: 1)),

        new Scenario(
            name: "atalia-ground-to-point",
            map: "dkoth_atalia",
            about:
                "The ascent case, and the map that proved it was needed. ⚠️ dkoth_atalia "
                + "shipped with BOTH control points unreachable by either team — on a mode "
                + "where standing on the point IS the goal — and it was not a geometry bug: "
                + "the 54px climb onto the point proved perfectly and a per-side quota then "
                + "threw the edge away. Cost is charged in flight ticks, so a climb hangs ~17 "
                + "ticks to gain 54px where a step down is over in 8; the ground beside the "
                + "point fans onto four steps DOWN and drops the one candidate that climbs as "
                + "the 6th cheapest at 23.20 against 22.87. One reserved slot for the cheapest "
                + "ascending landing fixes it, and across all 24 shipped graphs only 6 "
                + "node-sides of 19,902 claim that slot — two of them being these two points.",
            playerClass: PlayerClass.Heavy,
            team: Team.Red,
            from: new ScenarioPoint(1300, 1240),
            to: new ScenarioPoint(1338, 1176),
            budget: 600),

        new Scenario(
            name: "oldfort-perch-reroutes",
            map: "ctf_oldfort",
            about:
                "The bot must DECLINE a shortcut that was never real and take the long way. "
                + "A one-column perch on a stepped face overhangs left and steps up right, so "
                + "a bot standing on it has nowhere to back up to and every arc off it leaves "
                + "at a standstill — yet the graph offered an arc at 4.36 px/tick over 23.4 "
                + "ticks, which is 102px of plan against the 75px a standing heavy covers. The "
                + "bot flew it, landed short, blacklisted it, and got handed the identical "
                + "route again. ⚠️ 26% of the nodes on the red side had a cheapest route across "
                + "an arc like that. Against the pre-fix graph this leg never arrives; a pass "
                + "with anything blacklisted means a lying arc is back. ⚠️ `from` is the wide "
                + "ledge, not the perch: a bot placed on the perch slides off before it has "
                + "planned anything.",
            playerClass: PlayerClass.Heavy,
            team: Team.Red,
            from: new ScenarioPoint(6885, 174),
            to: new ScenarioPoint(7434, 660),
            budget: 1800),
    ];

    /// <summary>The distinct maps the suite needs, in first-seen order.</summary>
    /// <remarks>
    /// The reference's runner only changes map when the running game is on a
    /// different one, "because a map change plus a nav build is most of a
    /// scenario's cost". Here the equivalent saving is loading each map once and
    /// running every scenario on it.
    /// </remarks>
    public static IReadOnlyList<(string Map, int Area)> Maps { get; } =
        All.Select(s => (s.Map, s.Area)).Distinct().ToArray();

    /// <summary>One scenario by name, or null.</summary>
    public static Scenario? ByName(string name) =>
        All.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));
}
