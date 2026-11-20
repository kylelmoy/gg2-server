# Working on the bots

**The front door for bot development.** `docs/TRAPS-BOTS.md` is the long form,
indexed by phase-7 item number — useful once you know what you're touching, not
before you start.

⚠️ **This file navigates and states rules. It does not restate measurements.**
Every number lives in the file that measured it, cited here by section name so a
grep finds it. If you want a fact, follow the pointer — a number copied twice is
a number that goes stale in one place.

---

## The one rule that makes this area different

⛔ **The reference's bot layer is treated as INCOMPLETE, not as a specification.**
This reverses the project's central rule for `src/GG2.Bots/` only. A bot's
decisions are this project's own: improve them where the fix is straightforward
and measured, write the hard ones down as known issues rather than chasing them.

⚠️ **This does NOT reach the wire or observable simulation.** A bot may press
"drop intel"; `MatchSimulation` may not decide to drop one. Everything a bot's
input does after it leaves the bot is held to the reference exactly.

Departures taken so far, and issues deliberately not chased, are in
`docs/TRAPS-BOTS.md`, "Departures taken so far" and "Known issues, not chased".

## Three rules that outrank everything else in the layer

- ⛔ **A bot must NOT draw from `WorldState.Random`.** The match's stream is
  *observable simulation* — every seeded weapon puts its draw on the wire and
  every client re-derives the spread from it — so a bot drawing from it would
  make the stream's position depend on how many bots were thinking.
  `ScriptedBot.Random` is its own, and **has to be part of any snapshot**.
- ⛔ **The simulation never special-cases a bot.** A bot is an `IInputSource`
  like a socket. When adding a client command, add it in **three** places — the
  socket switch, `PlayerCommandKind`, and `GameServer.Execute`'s dispatch — with
  the body in a shared method both reach, or a bot can do something a client
  cannot.
- ⛔ **`WorldView` is a `ref struct` over a `ref readonly WorldState`.** Do not
  "simplify" it to a class or to a by-value struct: the first breaks the
  no-allocation rule, the second is a kilobyte copy per bot per decision, and
  both throw away the compiler's refusal to let bot code stash it.

`CLAUDE.md`, "`WorldState` — the five rules that bite before you know you are
near them", has the state rules that apply to everyone, bot work included.

## What exists

| | where | gated by |
|---|---|---|
| the scripted bot — nav, A\*, path following, targeting, combat, objectives, population | `src/GG2.Bots/` | `ScenarioSuiteTests`, `BotObjectiveTests`, `PathFollowerFixtureTests` |
| the learned policy — loads a gg2-sim `.gg2p`, the only checkpoint format the server loads | `SimPolicyNetwork.cs`, `SimPolicyObservation.cs` | `SimPolicyTests` |
| the hybrid — a controller takes keys and aim inside an engagement window, scripted layer keeps movement, routing, objectives, class | `HybridBot.cs` | `SearchBotTests`, `HybridBotSeamTests`, `ShippedCheckpointTests` |
| **the controller seam** — how a new bot implementation drops in | `IMicroController.cs`, `BotBrainSpec.cs` | `HybridBotSeamTests` |
| the per-tick controller budget across bots | `IMicroArbiter.cs` | `HybridBotSeamTests` |
| the policy as a controller | `SimPolicyController.cs` | `SimPolicyTests` |
| the rollout search, and as a controller | `RolloutSearch.cs`, `SearchController.cs` | `RolloutRestrictionTests`, `SearchBotTests` |
| the next-hop route table (CPD-Search) and the exact A\* heuristic — **ON by default**, `--no-nav-distances` | `NavDistances.cs`, `NavDistanceSet.cs`, `NavSearch.cs` | `NavDistancesTests`, `NavDistanceSetTests`, `NavSearchTests` |
| the tables **on disk** — `nav/<key>.dist.gz`, next-hop only, cost rebuilt on load | `NavDistanceFile.cs`, `NavDistances.FromNextHops` | `NavDistanceFileTests` |
| difficulty 1-4 aim like people, tier 5 untouched, `--no-bot-human-aim` to turn off (on) | `HumanizedBot.ByTier`, `BotSkill.HumanAim` | `HumanizedBotTests`, `BotCombatTests` |
| auto difficulty: each bot plays the rating of the human it is shooting at, `[Bots] AutoDifficulty` (off) | `BotPopulationService.Auto.cs`, `OpponentSkills.cs`, `ScriptedBot.Skill` | `AutoDifficultyTests` |
| human-like output inside a fight, `--bot-humanize` (off) | `HumanizedBot.cs` | `HumanizedBotTests` |
| route dispersion: occupancy (on) and static lanes (measured, not adopted), `--no-bot-occupancy` | `BotLanes.cs`, `BotRoles.cs`, `ScriptedBot.cs` | `BotLanesTests`, `BotOccupancyFlagTests`, `RouteDiversityTests` |
| the training environment seam | `gg2-rl/env/GG2.Env/`, in the `gg2-rl` repo | `EnvProtocolTests`, in `gg2-rl` |
| **is the nav graph followable** — every edge of every shipped graph flown by a real bot, and whole routes sampled | `EdgeFlights.SweepAll`, `BENCH_FLIGHT` | `EdgeFlightTests` — the corpus tally pinned on **both** routing arms, plus the node census |
| **can a body be where a node says** — 66.6% of nodes overlap the floor at `StandY`; all are free one pixel up | `EdgeFlights.StandingY` | `EdgeFlightTests.EveryShippedNodeCanHoldACharacter` |
| the measurement instrument | `tools/bench/` | not in `GG2.sln`; build explicitly |

⚠️ **There is nothing to be "ported" against.** Stock GG2 ships no bots; this
layer is this port's own, measured against itself.
`BotLayerCoverageTests`, which used to assert a fork's script-name list, is
deleted; see `CLAUDE.md`, "Except the bot layer".

**So the only judge of a bot change is measurement.** That is what "Measuring a
bot change" below is for.

### Auto difficulty

`[Bots] AutoDifficulty` (`--auto-difficulty`, off by default, not read on a duel
server) gives every human a rating and makes every bot play at the rating of the
human it is shooting at. **Per target, not per team**: a team-wide level would
make the bots a strong human faces strong for a new player on the other side
too, and with mostly-bot teams it reduces to per-human anyway.

| | |
|---|---|
| The rating | `DuelLadder`'s staircase and its eight levels (`docs/DUEL.md`, "The difficulty ladder"), one per human across every class, by address and name, forgotten an hour after it was last used |
| A result | A kill between a human and a bot, whoever `PlayerDeathEvent.Killer` credits: the bot dying is a win, the human dying a loss, with the margin the winner's health left. Human against human, a suicide and a death with no killer count for nobody |
| The target | `AutoDifficultyWinPercent`, 60%: the rating stands still where a human wins 60% of their fights with bots |
| The start | A new human starts on `Difficulty`'s level (`DuelLadder.LevelOfTier`: tiers 1-4 on levels 1, 3, 5, 7, tier 5 on 8), where a bot plays exactly the tier, so turning it on changes nothing until results come in |
| Against a bot, a generator, nothing | The bot's own `Difficulty` |
| Where it is read | `ScriptedBot.Skill` resolves the current target's row in `OpponentSkills`, which the population service rewrites every tick; every reader of a bot's skill goes through it (the combat step, which re-reads it once the target is settled so a fresh target's acquire hold is already its player's, the reflex dodge, `HumanizedBot`, and `HybridBot`'s handicap over a net). A bot is never renamed for it |
| Where it shows | The admin panel's player list, an `LV` tag on each human (`autoLevel` in `state`) |

Results are scored in `BotPopulationService.ObserveDeaths`, straight after the
simulation ticks. `AutoDifficultyTests.ALiveDeathToABotLowersTheRating` is the
end-to-end gate and goes red if that call is removed.

What it cannot change: under a policy net a level reaches only the handicap and
the aim, as on the duel server, so level 1 against a new player may still be
too hard; the target the handicap follows is the scripted layer's, which can
differ from the one the net is shooting at; and three bots on one weak human
are each easy but still three. What would show it wrong: humans' kill:death
ratios against bots, after ten minutes of play, sitting far from 1.5 (60%), or
spread widely between players.

### The hybrid, and the result that decides how to use it

⛔ **The policy is most valuable used sparingly.** At a 150px engagement window
it holds about 7% of ticks and beats the all-scripted bot. Widen the window and
the combat gain keeps rising while the objective collapses faster.

⚠️ **Widening the window re-acquires constraints the narrow one sidesteps.** Goal
conditioning is the one to know about: the checkpoint's `kind1/2/3` input columns
were constant through training, so their first-layer weights are the random
initialisation. `HybridBot` never sets a goal kind — it gates *when* the policy
drives rather than conditioning *what it pursues* — and that is what makes the
untrained columns harmless. **A macro layer that conditions the policy needs a
retrain first**, and would not look broken without one.

Numbers, tables and scope: `gg2-rl/gg2-rl/docs/ENV.md`, "Phase 4 re-measured".

### The search controller (arm 3): where it stands

Three arms are compared throughout this section: arm 1 all-scripted, arm 2
policy hybrid, arm 3 search hybrid — same 150px engagement gate, same macro
layer, one thing different.

**Tick cost, single-threaded.** Arm 3 exceeds the 33.3ms budget on the max tick
over every map tried, at both 8 bots (worst 80.65ms, `ctf_eiger`; `BENCH_ARMS`,
21 maps, 40 matches/arm, 2026-09-01) and 12 bots (19 of 21 maps on the max;
worst 80.74ms on the same map — a near-exact reproduction of the 8-bot number
that is a sanity check on the instrument, not evidence the two population sizes
cost the same; `BENCH_THREADS=6 BENCH_BOTS=12`, 6 matches/map, Release, 24
cores, 2026-09-02). A **max is a maximum over every tick of every match, not a
rate — it is monotone in coverage**: a pass at low n is never evidence a
larger run would also pass, but a fail is conclusive regardless of n.

**Tick cost, threaded.** At 8 threads and the shipping population (12 bots),
the max clears budget on all 21 maps (worst 18.26ms, `gen_destroy`, 55% of
budget) and p99.9 clears with margin (worst 6.37ms, `tdm_mantic`). At 8 bots,
p99.9 already clears budget **serially** (worst 29.37ms, `arena_montane`) — so
threading is not what decides affordability at 8 bots. At 12 bots it is: serial
p99.9 has no headroom left (worst 32.11ms, `dkoth_atalia`, 96% of budget).
Threading buys 4-5x on p99.9 at a cost of 8 cores — under 60% efficiency; say
what a factor costs in cores before recommending it.
⚠️ **A max is one draw from a heavy tail** — three identical runs at one config
read 72.37, 98.31 and 97.47ms serially (`Threads.cs`) — so a pass at 6
matches/map does not mean 40 would; widening coverage can only make the max
worse.

**Every arm-3 number below understates the search.** They were measured while
`SearchController.OnHandoverOut` kept a decision open across a release off a
decision boundary (a death, an intel pickup). The search's countdown runs only
while it drives, so the decision never ended and a search bot stayed scripted
after its first such release. On `ctf_truefort` that held the search side's
wheel share at 3% against the policy's 36%; with the release fixed it is 21%
(`BENCH_VERSUS`, 4 matches). `SearchBotTests.AReleaseMidDecisionLeavesTheSearchReady`
gates the fix. Re-measure before quoting the figures below as the search's.

**Head to head, the policy wins the fights and the search wins more matches.**
`BENCH_VERSUS=10` at `39cdd24`: 21 maps, 210 matches, 12 bots, both tier 5,
`Distance` rule, all nine classes, the nine shipped nets against the search
with no arbiter. The search team takes 0.519 ± 0.017 of a match's deaths (pooled
7,105 of 13,870, 0.512), so the policy is ahead in the fighting by about two
points, and four maps lean the other way (`tdm_mantic` 0.443 ± 0.037). Of the 85
decided matches the search won 52 and the policy 33. The wheel shares differ
(policy 0.27, search 0.17 of polls), so the two controllers were not asked
equally often; one candidate cause is that a medic net takes a beaming medic and
the search does not (`IBeamController`). Not measured: why the search wins more
objectives while dying more, and either controller under a live arbiter.

**In a 1v1 duel with the controller always driving, the duel nets beat the
search 0.712 ± 0.023** (`BENCH_VERSUS_DUEL=180`: 1,440 duels on `duel_01`-`duel_08`,
every class and side, both tier 5, wheel share 1.00 both sides; 990 policy kills,
364 search). By class it is far from uniform: the search wins Engineer (0.403)
and Spy (0.424), Scout is close (0.530), and it almost never wins Demoman (0 of
160), Sniper (4) or, with special off as the server runs it, Medic (0). Letting
it press special (`BENCH_VERSUS_SPECIAL=1`) moves Medic to 0.656 and Pyro 0.730
to 0.616, and the whole to 0.661 ± 0.023; Demoman and Sniper do not move. Not
measured: why. A sticky's damage lands only on a detonation and a sniper's
charge pays out late, and both may sit past the 20-tick horizon, but that is a
guess.

**Death share.** Pooled over ~34,000 deaths (`BENCH_ARMS`, 8 bots, 21 maps, 40
matches/arm, 2026-09-01): arm 1 49.82%, arm 2 46.17%, arm 3 44.55%. Arm 3 beats
arm 2 by 1.62 points (z = −4.26) and beats arm 1 (z = −13.82). At the match
level (840 matches/arm) the gap is larger: −3.59 points, t = −5.60, df 1678 —
both units agree the effect is real. The aggregate is decisive where a per-map
sign test is not: only 6 of 21 maps clear the 4.2-point effect-size floor at
which per-map direction was shown stable (an exogenous threshold, taken from a
separate policy-vs-control run); those 6 favour arm 3 6-of-6 (p = 0.016). The
other 15 maps' per-map directions are unknown, not absent.

⚠️ **The advantage varies by map lethality, and the tick cost does not track
it (r = +0.16 against map lethality).** Bucketing maps by **arm 1's own** death
rate (pre-treatment, so not an artefact of arm 3's own effect on match length)
shows the arm2→arm3 gap shrinking from quiet maps (−7.72p, 95% CI
[−10.90, −4.54]) through middling (−2.01p) to bloody maps (−1.04p, CI
[−2.33, +0.25] — not distinguishable from zero). This is a fact about where the
search's advantage lives, not about when it is affordable.

**Decision cost.** A search decision re-simulates every live character for
every candidate for every tick of the horizon; cost is affine in population,
`1490 + 181·N` µs (within 2%; `BENCH_SEARCH`, horizon 20, 60 candidates,
`gg_debug`, Release, 2026-09-03): 1831µs at 2 live characters, 5116µs at 20. A
duel bench or scenario undercounts real-match cost by 3-6x — 7.64ms in a
12-character bench scenario versus 22-44ms decomposed from a real match's
worst tick (2026-09-01) — because a scenario has no projectiles, sentries or
bystanders for the rollout to re-simulate.

**Maintenance: a policy is dated by the physics; a search is not.** A net
carries weights fit to one simulation version, and a `.gg2p` carries no stamp,
so nothing refuses a stale one: gg2-sim records the gg2-server commit it trained
at, and a physics change here is the cue to retrain. `RolloutSearch` carries no
weights and re-derives from the simulation every decision, so it absorbs a
physics change for free.

### The shipped bundle — `checkpoints/`

Nine gg2-sim nets ship in `checkpoints/`, one per class, which is what the fleet
runs (`checkpoints/README.md` has each file, its md5 and gg2-sim's note on it).
Each is a `HybridBot` tier. A tier **takes** the wheel within the handover range
and, once driving, **keeps** it out to 300px (`BotBrainSpec.ReleaseRange`, the
release band); the fleet runs `HandoverRule=distance`, which is what gg2-sim
measures its nets under.

```powershell
dotnet run --project src/GG2.Host -- --map ctf_truefort --bots 12 --bot-mode policy `
  --bot-handover-rule distance `
  --policy checkpoints/ppo-warm-soldier-s1.gg2p --policy checkpoints/ppo-finetune-scout-s1.gg2p `
  --bot-class-policy Soldier=ppo-warm-soldier-s1,Scout=ppo-finetune-scout-s1
```

`--policy` is repeatable and appends, one tier per checkpoint; `[Bots] Policy`
takes the same list comma-separated. A `.gg2p` names no classes, so every class
a net drives has to be assigned, and a net nobody assigned a class is warned
about at startup. The panel's live `bot-brain` switch, on a server started with
no checkpoint, loads all nine with the fleet's assignment
(`MapDirectory.ShippedClassPolicies`).

Two settings decide what plays, and they answer different questions:

| setting | question |
|---|---|
| `--bot-class-policy Class=name` / `[Bots] ClassPolicy` | who drives a class: a checkpoint's file name without its extension, or `scripted`. A class not named is driven by no net. |
| `--bot-class-limit class=n` / `[BotClasslimits]` | which classes bots play: how many bots of each class one team may hold, 0 keeping bots off a class. |

This repository gates presence and wiring, not quality. `ShippedCheckpointTests`
pins all nine md5s, loads each as a layout v2 team net, and plays a match in
which the soldier net takes the wheel. It says nothing about whether any net
plays well; that is gg2-sim's claim, judged against its other nets rather than
against the scripted bots.

### ⚠️ A duel does not contain what a match contains

A reduced setting does not *contain* what a real one does — a duel has no
second enemy, no projectile crossfire, no sentry, no bystander — and **every
absence arrives as a legal-looking zero, not an error.** The 3-6x cost gap
above is an instance of this. Before
trusting a duel- or scenario-scale measurement, ask what the reduced setting
does not contain, not only what obvious parameter you forgot to vary.

---

## Swapping the bot logic out — the seam

⛔ **Read this before writing another bot.** Adding a way to fight is one class
implementing `IMicroController`, and **nothing else** — no new option field, no
new branch in `HeadlessMatch` or `BotPopulationService`, no new wrapper.

| you want | you write |
|---|---|
| a new way to fight | `class MyThing : IMicroController` — six members |
| to run it in a match | `Brain = BotBrainSpec.Of(new MyThingSpec())` |
| to run it on a live server | a `BotMode` value, and the arm that builds its `MicroControllerSpec` in the host's `tiers` list |
| to compare it against another | two `BotBrainSpec`s, same gate — see "Measuring a bot change" below |

`HybridBot` owns the handover architecture — the gate, the intel carve-out,
"the scripted layer thinks every tick", the aim resync, the counters — and
knows nothing about how a controller decides. `IMicroController` is what it
hands to. `BotBrainSpec` is the recipe a caller configures: an ordered tier
list, the gate settings, and an optional `IMicroArbiter`. Every arm carries
every counter (`ScriptedPolls` included), and `HandoverRule.HeadSplit` is
gated by `HybridBotSeamTests` even though nothing in the suite, the bench or
the host currently selects it.

⚠️ **`BENCH_CHURN` output byte-identical across a `HybridBot` restructure, all
three arms, two maps, every input-derived statistic, is the only check that
can catch a behaviour change** — every pre-existing assertion about a hybrid is
an inequality (`PolicyTicks > 0`), so a green suite alone does not establish
that a restructure preserved behaviour. Prove the comparison could fail before
trusting it (e.g. delete the aim resync and confirm arm 2/3 rows move while arm
1 does not). `BENCH_ARMS` cannot substitute: it loads the checkpoint without
the stamp bypass, so arm 2 does not run on this build. `BENCH_CHURN` loads with
the bypass and is the only mode that exercises all three arms.

### The handover is HUMAN-ONLY on a live server, and NOT in a measurement

`EngageOpponents` decides whether a bot hands the wheel to its micro-controller
for another **bot**, or only for a **human**. The two defaults disagree on
purpose:

| | default | why |
|---|---|---|
| `BotBrainSpec.Engage` (library) | `Anyone` | A headless bot-vs-bot match has **no humans in it**. `HumansOnly` here would make `HeadlessMatch`, the scenarios and `tools/bench` silently measure the all-scripted control and report it as a controller result. |
| `gg2-server` (live) | `HumansOnly` | The expensive controllers exist to be good against **people**. A multi-millisecond search decision spent on two bots shooting at each other in an empty corner is most of what a populated server pays for. `--bot-engages-bots` restores `Anyone`. |

**Every arm number in this document was measured under `Anyone`**, the only
setting under which a bot-vs-bot match exercises a controller at all. A
controller measurement taken under `HumansOnly` with no humans present measures
the scripted layer regardless of `--bot-mode`, and both arms converge — a
result that looks like a finding rather than a mistake.
`HumanOnlyHandoverTests.TheLibraryDefaultIsAnyoneSoHeadlessMeasurementsStillEngage`
guards this. **What the human-only default buys is cost, not skill** — cheaper
bot-vs-bot fights that are very slightly worse.

### Watching a handover from inside the game

`--bot-handover-bubbles` (or `[Bots] HandoverBubbles`) makes every bot put up a
chat bubble when the wheel changes hands. The panel's Bots card answers "which
brain and how often" in aggregate; this answers "which bot, right now, and
where it was standing" — the question a screenshot has to answer.

| Bubble | Index | When |
|---|---|---|
| `z1` | 20 | a policy tier took the wheel (gg2-rl or gg2-sim) |
| `z2` | 21 | the scripted layer took it back |
| `z3` | 22 | the rollout search took the wheel |

The indices are the client's own `z` menu
(`Objects/Overlays/Interfaces/BubbleMenuZ.events/Key {1,2,3} pressed.xml`), so an
operator reads them as the bubbles they already know.

**Off by default because a bubble is a broadcast.** Eight bots handing over is
eight extra messages a tick to every connected client and a screen full of
speech balloons; it is for a server being debugged, not one people are playing
on.

**It cannot change what a bot does.** A bubble is a broadcast and a client-side
sprite — unlike a taunt it neither freezes the character nor touches its keys —
and it travels as a `PlayerCommand` through the same `ExecuteChatBubble` a socket
reaches, so the April Fools rewrite and the per-player throttle bound a bot's
exactly as they bound a person's. Two consequences worth knowing: on April 1st
every one of them becomes bubble 61 and the aid is useless for the day, and a
burst of handovers inside the throttle window shows as one bubble.

**A handover is decided in `Poll` and sent from `PollCommands`, which
`GameServer.Tick` calls first**, so each bubble is one tick behind the handover
that asked for it.

`HandoverBubbleTests` gates the mapping, the wrapper's transitions, the
off-by-default control, and — the claim nothing inside `GG2.Bots` could make —
that the bubble reaches the wire, by decoding the server's own broadcast.
Confirmed against a stock GM8 client: a pyro driven by the policy tier put up
bubble 20 and a search-driven bot put up 22 and 21, and all three draw as
distinct sprites.

### The controller budget calibrates itself to the machine

Two things decide whether a bot may spend an expensive decision: what the
decision costs, and what this tick can afford.

**Cost is affine in the live character count** (measured; see "Decision cost"
above) — a rollout re-simulates every live character per candidate per tick, so
`SearchController` takes the population. A policy forward pass is flat and
ignores the count.

**The budget walks itself down.** No compiled-in constant can know the
machine's headroom, so the arbiter never uses an absolute scale — only
`cost <= budget - spent` — and `MicroBudgetController` calibrates the budget by
AIMD instead: halve it when a tick uses more than 80% of its interval (at most
once per 900-tick window), creep it back up after a quiet window, never above
`--bot-arbiter`. The cost model only has to be right up to a constant factor;
the loop supplies the factor.

⛔ **A duel has no arbiter, so nothing on this path reaches `gg2-rl`.**
`GG2.Env` references `GG2.Protocol`, `GG2.Maps`, `GG2.Simulation` and
`GG2.Bots` — **not `GG2.Server`** — and nothing in `gg2-rl/env/GG2.Env/` mentions
`IMicroArbiter` or `BotBrainSpec`. `HybridBot.Choose` short-circuits at
`_arbiter is null`, `DecisionCostFor` is never called, and a duel cannot be
moved by the cost model or the budget however wrong either is.

⛔ **The budget may only change BETWEEN ticks.** It lives in `GameServer.Run`,
which `HeadlessMatch`, the env and `eval` never call — so `Choose` stays a pure
function of tick, poll order, costs and budget, and `RolloutThreadingTests` and
`EnvBatchTests.ABotDrivenBatchIsTheSameHoweverManyThreads` keep meaning what
they say. `--fixed-bot-budget` pins it.

⚠️ **It responds to TOTAL tick pressure, not to controller-attributable
pressure**, so it throttles bots for slowness that is not theirs — the safe
direction, since cutting optional work is right whenever the tick is at risk,
but surprising from outside; every cut logs what the controllers actually
spent. **Bots get quietly worse under load**, which is why this is structurally
absent from every measurement path rather than merely defaulted off.

### Choosing a controller by what the machine can afford

⛔ **The constraint is the TICK TAIL, not the duty cycle.** A search decision is
a large fraction of a 33.3ms tick on its own, and handovers are **correlated**
— several bots have an enemy in range during the same firefight — so the worst
tick is several coinciding decisions rather than one expensive bot. **A per-bot
setting cannot see that.** One `IMicroArbiter`, shared by every bot on a
server, can: `HybridBot` asks it which tier may spend this decision, and a
refused bot drops to a cheaper tier or to the scripted layer.

⛔ **DO NOT READ A CLOCK INSIDE AN ARBITER.** An answer that depends on
wall-clock time makes a match unreproducible: `HeadlessMatch` stops being a
function of its seed, `eval` stops being thread-count independent — a property
this repo *gates* rather than assumes — and a parity fixture recorded with bots
in it stops replaying. **The measured resource number belongs outside the
tick**: read `--tick-stats` between ticks and write
`DecisionQuotaArbiter.UnitsPerTick`, keeping the in-tick decision a pure
function of the budget it was given.

⚠️ **Spend is ordered by roster slot**, so a low slot takes the budget first.
That is deterministic and it is not fair; an arbiter wanting fairness must
rotate on something derived from the **tick**.

✅ **Measured costs, in microseconds** (`BENCH_POLICYCOST`, `BENCH_SEARCH`,
2026-09-02, Release):

| decision | cost |
|---|---|
| policy: observation + forward pass | **6.9 µs** (2.2 + 4.7) |
| search: 60 candidates × horizon 20, twelve live characters | **4,795 µs** |
| the tick itself | 33,300 µs |

`DecisionCost` is priced in these microsecond units — the measured ratio is
695:1, so `--bot-arbiter 10000` means "ten milliseconds of controller work a
tick" and caps concurrent search decisions at two. Do not price controllers in
invented relative units; `tools/bench/README.md` trap 7 covers this shape.

⛔ **The search's constant is configuration-dependent**: cost scales with
population, horizon and candidate count. Re-measure with `BENCH_SEARCH=1
BENCH_POP=<n>` before deriving a budget.

⚠️ **No budget has been TUNED.** The mechanism and the costs are measured; what
`UnitsPerTick` should be is not. Falsification: if a downgrading arbiter does
not move a `BENCH_THREADS`-style p99.9 column, it is not doing what this
section says it does.

## ⛔ Measuring a bot change

**Read this before running an evaluation, not after.**

⚠️ **Measuring a bot's COST is a different job from measuring its play, and it
has its own trap: the .NET sampling profiler is not usable here.** It has been
wrong repeatedly on this workload, by factors of five to forty, always blaming
a runtime memset. Use the sampler to pick a method, an in-process `Stopwatch`
around named stages to size it, and an interleaved A/B to confirm anything you
change — `docs/ARCHITECTURE.md`, "The .NET sampling profiler was wrong THREE
times here".

### The statistic

- ⛔ **Prefer the statistic with no subset.** A label travels and a filter does
  not. **Death share** counts deaths across every match — drawn, capped and
  decisive alike — and is the criterion for that reason. A **win share** is
  over decided matches only.
- ⛔ **`decided` is NOT the win share's denominator.** `MatchOutcome.Decided`
  **includes a draw** (`MatchOutcome.cs`, and `Rounds.cs` decides with
  `Team.Spectator` in five places). Wins-plus-losses is the denominator, and on
  some maps it is a twentieth of the decided count.
- ⛔ **A statistic over a subset must display the subset's size IN THE SAME
  ROW** — never a caption, because a copy-paste separates them.
- ⚠️ **What invalidates a comparison is the censoring GAP between arms, not its
  level.** 3%/3% is comparable and 30%/30% is comparable; **3%/15% is broken
  and looks better than the second.** Rank on the differential.

### The design

- ⛔ **Keep the control arm even when it looks redundant.** Some of the
  largest findings this project has made came out of a control row added only
  to validate an assumption nobody doubted.
- ⛔ **Write the test and its possible outcomes down BEFORE the data exists.**
  Only a pre-registered test is defensible unqualified. A p-value on a
  post-hoc quantity and one on a pre-registered test **print identically**.
- ⚠️ **Ask what result would falsify the check.** A test essentially nothing
  could fail is not evidence — four arms that are cumulative in construction
  cannot fail a monotonicity test.
- ⚠️ **Comparing two significant numbers is not a test of their difference.**
  A significant result beside a non-significant one *reads* as a contrast and
  is not one; the difference needs its own test.
- ⚠️ **A match cap censors.** Check what fraction of each arm reached it
  before reading anything computed over outcomes.
- ⚠️ **N maps is N measurements of ONE policy**, not N trials of a method.
  Training variance across seeds can exceed the effect. Maps buy precision;
  seeds buy generalisation.
- ⚠️ **A max is monotone in coverage; a bucket cut on a post-treatment
  variable manufactures trends with no mechanism.** Cut on something the
  control arm alone determines (e.g. a per-map baseline rate) if you want a
  causal bucket.

### The mechanics

- ⚠️ **Warm past tier-1 before measuring.** Promotion is at 30 calls and the
  rejit is asynchronous, so a short warm-up measures tier-0 code — it made one
  duel tick read 23 µs at one world and 2.9 µs at sixty-four.
- ⚠️ **Name the build.** Release and Debug differ by about 2x, and a
  re-measure that forgets looks exactly like a factor-of-two regression.
- ⚠️ **Publish to a fresh directory.** A running `bench.exe` holds its own
  file, the build silently cannot overwrite it, and the next run measures the
  old binary.
- ⚠️ **Two maps are pathological.** `cp_egypt` never produces a winner — 0
  decisive out of 400 in both arms, at any cap — and `ctf_classicwell` cannot
  finish in twenty minutes, with 82% of *control* matches capping and still
  counting as `decided`.
- ⚠️ **A per-map comparability flag is a property of the measurement, not of
  the map.** The set of untrustworthy rows changes with the cap.

- ⚠️ **A machine can be the difference.** `eval --digest` prints a 64-bit
  digest per map and one for the batch — every character's position, velocity
  and health at the end of every match. Two runs of the same flags agree or
  something is not deterministic. Same-OS machines agree; Windows and Linux do
  not, so a before-and-after taken on two different operating systems is two
  experiments. `docs/SIMULATION.md`, "The same build on two machines".

`tools/bench/README.md` has the modes. `gg2-rl/gg2-rl/docs/ENV.md`, "Phase 4 re-measured",
has the worked examples behind every bullet above.

### The three pinned end-to-end measurements

`ScenarioSuiteTests.Baseline`, `BotObjectiveTests`' intel run and
`PathFollowerFixtureTests`' longest steerable leg are pinned **in both
directions**. ⛔ **A failure there is the re-record, not a broken test**: check
the change is one you meant, check it went the way you meant, update the row,
and say so in the commit.

⚠️ **A pinned number quoted without its diff says nothing.** If a number is
quoted as a measurement anywhere in this repo, ask what would go red if it
doubled — on this project's evidence the answer is usually "nothing" until it
is checked.

---

## The `gg2-rl` seam

The sibling training project reads this repo's source directly for its drift
gate. ⛔ **A pure refactor of anything it parses is a cross-repo event.** The
full list of eight patterns across six files, and which three are fragile, is
in `gg2-rl/gg2-rl/docs/ENV.md`, "So a pure refactor". The worst: `EnvServer.cs` reads the
**order** of the reward block's serialisation calls, so **a formatter run over
that method goes red with no semantic change**.

⚠️ **Flagging a move is a courtesy on top of that gate, never a substitute.**
`ObservationWriter.cs`, `PolicyNetwork.cs` and the `.npz` loader were removed
with the gg2-rl nets, so any of its patterns that read those files no longer
have a source here.

`SIM_UUID` covers the constants, the action encoding, the observation layout
and the simulation behaviour version — **not `GG2.Bots`**. So a bot change does
not invalidate a checkpoint, and a change to what an observation *means* does.

### gg2-sim policies (`.gg2p`)

gg2-sim (a sibling checkout, a CUDA port of `GG2.Simulation` that trains bot
policies) exports `.gg2p` files, the only checkpoint format the server loads.
`--policy x.gg2p` loads one as a `SimPolicyMicroSpec` tier:

```powershell
gg2-server --bots 8 --bot-mode policy --bot-handover-rule distance --bot-engages-bots `
  --bot-brain-team red `
  --policy ppo-kill1-6b-s2.gg2p --policy ppo-mixscout-s1.gg2p `
  --bot-class-policy Soldier=ppo-kill1-6b-s2,Scout=ppo-mixscout-s1
```

`--bot-brain-team red` gives the brain to red's bots only and leaves blue's as
plain scripted bots, which is how gg2-sim's match numbers are taken.
`TeamGatedBot` wraps the hybrid and reads the team every tick, so a bot moved by
autobalance or the `team` verb changes brain with it; `HybridBot` itself is
unchanged. It relies on a team change killing the character, which releases the
wheel on the way across (the class remarks say what breaks if that stops).

| | gg2-sim `.gg2p` |
|---|---|
| Observation | `SimPolicyObservation`: team (253 floats), duel (169), zoom (255: team plus own scope and rifle charge), demo (286: team plus the eight nearest own mines and lobbed/8), pyro (256: team plus ReadyToBlast, ReadyToFlare and own burn/210), medic (259: team plus uber/2000, uber ready, ubering, beaming, patient hp share and the patient's index among the listed team-mates), engineer (258: team plus nuts'n'bolts/100 and its own sentry's present, hp/100, dx/500, dy/500) spy (257: team plus cloak, cloak alpha, ready to stab and stab freeze left) or Quote (262: team plus own live bubbles/25, a blade of its own in the air, magazine refilling, each listed enemy's live bubbles/25 and each listed enemy Quote's magazine/90, the enemy columns 0 for a remembered one). Every team width lists enemy mines and flares and either team's thrown blades beside rockets and shots; the duel width does not. **Base layout v2** is any team width plus 10 (263 to 296): the two nearest sentries after the layout's columns, and hidden enemies listed from the body's memory (below). **The terrain layout** is any team width, either version, plus 176 (the duel nets: 439 to 472), below |
| Actions | `SimPolicyController`: 24 absolute aim bins, then 25 relative to the k-th listed enemy (team) or to the nearest enemy (duel); a zoom net adds a fifth head, the scope level; a demo, pyro, medic or spy net's fifth head is the Special key (the detonate; the airblast, and with Attack the flare; the needles, and with Attack the uber when ready; the cloak toggle; Quote's blade throw), held for the decision like Attack; an engineer net's is the build command (`IBuildController`), sent on every tick of the decision. A medic net's relative groups 1 and 2 aim at the two listed team-mates, with the aim distance on them (capped at 255) so the beam picks that patient |
| Sniper scope, sentry build | a zoom net's scope and an engineer net's build, while it drives (`IScopeController`, `IBuildController`); otherwise the scripted layer's (which never builds) |
| Classes it drives | **only** what `ClassPolicy` assigns; none is warned about |
| Stamp | none; gg2-sim records the gg2-server commit it trained at |
| Decision | greedy by default; `[Bots] PolicySampled` draws each head from its softmax (below) |
| Decision cost | 72 µs team, 57 µs duel, 102-107 µs terrain |

**Base layout v2** (gg2-sim `env/env.h`, `SentryBlockObs` and `SpyMemoryTicks`)
is told apart by width alone (`SimPolicyNetwork.IsV2`); v1 nets load and run
unchanged. Every row ends in the two nearest sentries of either team (dx/500,
dy/500, hp/100, enemy, present; nearest first, ties to the lower owner slot).
An enemy `BotVisibility.CanSee` refuses, which is a cloaked spy not stabbing in
front of the body, is listed from the body's last sighting: that position, zero
velocity, that hp, present `1 - age/60`, until 60 ticks have passed. A relative
aim at such a listed enemy points at that remembered spot, and the fallback for
a dead listed enemy is the nearest one the body can see. Each controller keeps
one `SimPolicyMemory`. It learns at every observation and, through
`ISightingController`, on every third tick while the scripted layer drives:
`HybridBot.Poll` calls it on every tier at the top of the poll whenever no tier
holds the wheel, which is where gg2-sim's `ref/Matches.cs` calls it.

**Known issue: the memory is indexed by roster slot and a departure renumbers
the roster.** The controller clears its memory when its own body's slot changes,
but for up to 60 ticks after somebody else leaves, an entry can describe
whoever moved into a remembered slot. It is read only for an enemy the body
cannot see, so the cost is one stale remembered row. gg2-sim's environment has
no departures, so nothing there can show it.

**The terrain layout** (gg2-sim `env/env.h`, `TerrainObs`; the duel nets) is told
apart by width alone (`SimPolicyNetwork.IsTerrain`). Its position columns are
written 0, and after the sentry block every row ends in 15x11 blocks of 9x9 mask
cells (54 px) centred on the body's cell, each the larger of its walkmask share
and the largest share one rect solid to the body covers; then, per listed enemy,
1 when `CollisionLineBlocksBullets` from the body to it (its remembered position
when hidden) is clear; then eight route columns toward the first listed enemy:
hops 1 and 2 on the next-hop table the body's scripted layer routes with, between
the two bodies' `SnapObjective` nodes (dx/500, dy/500, present each), standing on
a node, and same node. The graph and tables come from the body's own
`ScriptedBot` through `IRouteController`, which `HybridBot.Poll` calls on every
tier at the top of every poll. **With no tables (`--no-nav-distances`, or a
custom map too large to derive them) the hops are written absent**, a row no
net trained on; gg2-sim always has them.

**Sampled decisions** (`[Bots] PolicySampled`, `--bot-policy-sampled`, off by
default) draw each head from the softmax of its logits, in double after a shift
by the head's greedy logit, one `NextDouble` per head in head order: gg2-sim's
`PolicyDriver(net, new Random(seed))`. Each controller owns its `System.Random`,
seeded from the first roster slot it decides for, never `WorldState.Random`.
Greedy on a static scene is a fixed point: the duel soldier net stops pressing
Attack at an idle target in about 6% of cells greedy and under 2% sampled, and
sampled wins more against every opponent gg2-sim scores for all nine classes
(gg2-sim `docs/HANDOFF-SERVER-DUEL.md`). Greedy remains the arm every earlier
cross-check below was taken with.

**The observation, the forward pass and the decode are gg2-sim's
`ref/PolicyDriver.cs` and `ref/Matches.cs` (`SimPolicyController`), expression
for expression**, except one line each in `SimPolicyObservation.Nearest` and
`SimPolicyController.NearestEnemy` that skip an enemy `WorldView.MayOppose`
refuses. That is a no-op unless the view carries an opponents table, which only
the duel server builds (`docs/DUEL.md`), so the cross-check below is unaffected; and are checked against them, not against anything in this
repo. On 2026-10-27, compiling those two files next to this build
(gg2-sim at `b62d6b3`, its pin `8535530`) over headless matches on five maps:
286,869 team observations bitwise equal, 1,244,765 greedy actions (team and duel
nets) equal, and 40 eight-bot matches (both rules, red hybrid) equal in result,
digest, driven ticks and every character's position on every tick. The zoom
layout was checked against gg2-sim `cdb70e0` on 2026-10-28: 548,432 team observations in both layouts with snipers present
bitwise equal, 109,887 sniper controller polls equal in toggle, input and
decision, and 20 matches with the existing nets still equal. The demo layout
and the enemy mines were checked against gg2-sim `31f42ef` on 2026-10-29, its
`ref/PolicyDriver.cs` compiled next to this build over 80 headless demoman and
soldier matches on five maps: 1,304,956 observations (base and demo team
widths, both duel widths) and 328,018 decoded team inputs with random actions
bitwise equal, with enemy mines in view in 86,541 of them and own mines in
17,615; leaving the enemy mines out made it fail at the first one. The pyro
layout and enemy flares were checked against gg2-sim `e2afef1`'s
`ref/PolicyDriver.cs` (pin `8535530`) on 2026-10-29 over 80 pyro,
demoman and soldier matches on five maps: 1,591,444 observations (base, demo,
pyro and both duel widths) and 638,012 decoded inputs bitwise equal, enemy
flares in view in 4,021; leaving the flares out made it fail at the first one.
The medic layout was checked against gg2-sim `4a8d9ca` the same way, over 80
medic, pyro, demoman and soldier matches: 2,038,526 observations and 1,022,805
decoded inputs bitwise equal, with a beam in 59,795, an uber running in 3,653 and
172,008 ally-aim decodes; fixing the ally aim distance at 128 failed it on the
first. Base layout v2 and the engineer and spy layouts were checked against
gg2-sim `acd9a7a`'s `ref/PolicyDriver.cs` and `ref/Matches.cs` on 2026-11-11,
with gg2-sim's nine round-two v2 nets (`ppo-warm-soldier-s1`,
`ppo-finetune-scout-s1`, `ppo-finetune2-{heavy,sniper,pyro}-s1`,
`ppo-freeze-{demoman,engineer,medic,spy}-s1`). Over 15 headless matches on five
maps with all nine classes, every live body observed through all nine layouts
every third tick with its own memory: 3,498,192 observations bitwise equal
(3,124,278 with a sentry in view, sentries planted where an engineer could
build because the scripted layer never does; 380,232 remembered rows),
3,498,192 greedy actions equal and 10,498,086 decoded random actions equal
(271,742 aimed at a hidden listed enemy). Then 20 hybrid matches (distance
rule, red driven by all nine nets) with the ref's controller in one world and
this one in another, stepped in lockstep: equal in every character's position,
hp, cloak, scope and sentry on every one of 106,714 ticks, with 181,904
policy-driven bot-ticks and 30 sentries built by the engineer net. Removing the
`HybridBot` sighting call made the lockstep run diverge (tick 4,725 on
`arena_montane`); holding a remembered row's present column at 1 failed the
observation check at tick 18. The terrain layout and sampled decisions were
checked against gg2-sim `4f7723c`'s `ref/PolicyDriver.cs` (pin `da4fafb`) on
2026-11-17 with its nine duel nets (`ppo-duel2-{soldier,scout,heavy,demoman,pyro,
medic,engineer}-s1`, `ppo-duel2-sniper-s1-1000M`, `ppo-duel3-spy-s1`, exported
from the `.pt` checkpoints), over six 2,400-tick hybrid matches the nets drove
sampled (`duel_01`, `duel_04`, `duel_07`, `ctf_truefort`, `cp_dirtbowl`,
`koth_harvest`): every live body observed through all nine layouts every tick
with its own memory, 719,550 observations bitwise equal (539,946 with a first
hop, 535,599 with two, 23,238 with a clear line of sight), and 239,850 sampled
decisions equal with the same seed on each side. The Quote layout was
checked the same way on 2026-11-17 (gg2-sim `cc9ae50`'s ref, its duel net
`ppo-duelq-quote-s1-1000M`), through the real duel server: a loopback Quote
human strafing, swinging, throwing at its bot on `duel_01`, `duel_03` and
`duel_06` for 6,000 ticks each, the bot driven by the net sampled. 17,997
observations bitwise equal and 5,997 sampled decisions equal, with own bubbles
in 17,032, an enemy's in 14,617, an enemy magazine in 16,557 and a blade in
flight in 3,367; the net killed the human 48 times and was never killed.
`HeadlessMatch` cannot make a Quote bot (its class draw stops at Pyro), which
is why that check runs on the duel server. **A medic on the beam is handed over only to a controller that heals
itself**: `Handover.ShouldEngage` keeps a beaming medic scripted, and
`HybridBot` drops that term when every eligible tier is an `IBeamController`
owning the beam, which a medic-layout net is (the user's call, 2026-10-29). Any
other medic controller keeps the gate
(`HybridBotSeamTests.ABeamingMedicIsHandedOnlyToAControllerThatOwnsTheBeam`).
**Nothing in
this suite can show that** — `SimPolicyTests` uses synthetic weights — so a
change to either side is re-checked by re-running that comparison, and gg2-sim's
`scripts/obs-check.sh` holds the observation to its GPU environment. Changing
`GG2.Simulation`, `ScriptedBot`, `HybridBot` or `Handover` breaks gg2-sim's
parity: tell that lane the SHA.

**The scope and the sentry build are the only commands a controller sends.**
While an `IScopeController` that owns the scope drives, `HybridBot.PollCommands`
drops the scripted layer's `ToggleZoom` and sends the controller's; an
`IBuildController` that owns the build does the same for `BuildSentry`. At a
decision boundary the controller decides in that call, because commands are
polled before inputs, and its `Poll` reuses the decision. Driving is settled in
`Poll`, so either follows a handover one tick late, as the bubbles do. Nothing
a controller sends destroys a sentry.

---

## Where to look

| you are touching | read |
|---|---|
| anything in `src/GG2.Bots/` | `docs/TRAPS-BOTS.md` — one section per phase-7 item, by name |
| adding or swapping a bot implementation | "Swapping the bot logic out" above — `IMicroController` and nothing else |
| the nav format, A\*, the path follower | same, items 2, 4a, 4b-i, 4b-ii |
| a route table, the A\* heuristic, route dispersion | same — "Shipping the tables", "CPD-Search", "Two ways to spend a next-hop table", "Route variety, scored". ⚠️ **Both ship ON now.** ⛔ The three pinned measurements and the 25 digests do NOT exercise the tables — `ScenarioWorld` sets no `Distances` — so those say nothing about the arm a server runs; see "The gates measure a DIFFERENT ARM". ✅ **`EdgeFlightTests`' corpus sweep is the one gate that does**, and pins both arms so the difference is visible |
| a rocket jump that leaves the ground and does nothing | same — "The rocket that does not go off". ✅ **Fixed**: the blast tilts back along the run when the terrain says nothing is under the muzzle (`PathFollower.LipAimDegrees`) |
| whether a bot can actually follow the graph | same — "The corpus sweep" and "The route sample". ⛔ **28% of edges is the ceiling for a per-edge sweep**: A\* routes around the rest, and driving them anyway credits a walk to an arm that did nothing |
| targeting, aim, the combat controller | same, items 4c-i, 4c-ii |
| what a bot is allowed to SEE — the cloak, the spy | same — "the cloak is not the whole of what a bot cannot see". `BotVisibility.CanSee` is the one predicate; a candidate loop that tests `Cloak` itself has half the rule |
| the objective layer | same, item 4g, plus "Dual King of the Hill is unwinnable by bots" |
| the medic — who it beams, the attack/defend split | same — "The medic: who it beams, and the one role it never takes". ⛔ A full-health patient still charges the uber (2.5 during setup), so "never instead of somebody hurt" is the rule and "never" is not |
| the env, the observation, the action space | `gg2-rl/gg2-rl/docs/ENV.md` |
| anything the client can observe | `docs/SIMULATION.md` — the contract |
| a weapon, a projectile, a gamemode | `docs/TRAPS-SIMULATION.md` — the section for it |
| the wire | `docs/PROTOCOL.md` |
| the structural constraints on the seam | `docs/BOTAI.md` |
| the running GM8 game | `docs/TRAPS-LIVEGAME.md` — **before**, not after |
| more than one session at once | `docs/PARALLEL.md` — in force |

⚠️ **Grep the traps files before writing.** They exist so a fact measured once
against the running game is never measured again, and a session that does not
open them will re-derive something at the cost of a day.

---

## ⚠️ The failure this area is most prone to

**A claim the repo makes about itself.** Code can be self-consistent while a
description of it is false, and nothing inside the repo contradicts that —
only a reader who *acts* on the description finds out. It has happened
repeatedly here, in both directions: a stale "not done" costs exactly what a
stale "done" does. (One instance: the path follower's jump branch was
documented as unported two hundred lines above its own implementation, and a
session took the queue item before finding the work already done.)

⚠️ **The author is the one reader guaranteed to reconstruct the mistake.**
Re-reading recovers the meaning you intended, which is exactly the meaning
that was wrong. **Send a claim across a seam early; do not review it harder.**
Cheap contradicting actions for questions of meaning — grep the definition,
keep the control arm, run the one command that could disagree — and review for
questions of mechanism.

## ⚠️ Late ticks are a PACING measurement, and pacing measures the machine

Measured 2026-09-03:

| what | number |
|---|---|
| server CPU per tick, 12 bots, Release, no client | **0.104 ms** (0.3% of one core) |
| the same run's pacing | p50 33 ms, mean 33.31, **p99 100 ms, max 262 ms** |
| what else was running | another lane's training: **70 python processes**, ~7.7 cores |

⛔ **A tick that did 0.1ms of work and was 94ms late is not a bot problem, and
no amount of optimising this server would move it.** The late-tick warning is
computed from a scheduled deadline (`nextTick += tickLength`), so it folds in
every millisecond the process spent not running — on a shared machine, the
dominant term by orders of magnitude.

✅ **The warning carries the verdict**: the tick's own work, and
`so: NOT this tick — the process was not scheduled` versus `so: the tick
itself`. `LateTickMonitorTests` gates both directions against the same
threshold lateness uses, so the two answers cannot disagree.

⚠️ **The histogram's shape says it too, before you read any code.** Contention
is bimodal — a correct median with a long tail, plus a cluster of near-zero
intervals where the loop catches up after a stall. Real overload moves the
*median*. In the run above: p50 33, mean 33.31, and 131 near-zero ticks in
1,800.

⚠️ **Before quoting any pacing number, check what else is on the box.**
`Get-Process python | Measure-Object CPU -Sum` twice, ten seconds apart, over
`(Get-CimInstance Win32_ComputerSystem).NumberOfLogicalProcessors`. An empty
server idles at 2 Hz, so a bots-only measurement needs `--no-idle-when-empty`
or it measures nothing at all.
