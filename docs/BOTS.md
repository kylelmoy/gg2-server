# Working on the bots

**The front door for bot development.** `docs/TRAPS-BOTS.md` is 3,200 lines
indexed by phase-7 item number — useful once you know what you're touching, not
before you start.

⚠️ **This file navigates and states rules. It does not restate measurements.**
Every number lives in the file that measured it, cited here by section name so a
grep finds it. If you want a fact, follow the pointer — a number copied twice is
a number that goes stale in one place.

---

## The one rule that makes this area different

⛔ **The reference's bot layer is treated as INCOMPLETE, not as a specification.**
This reverses the project's central rule for `src/Gg2.Bots/` only. A bot's
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
| the scripted bot — nav, A\*, path following, targeting, combat, objectives, population | `src/Gg2.Bots/` | `ScenarioSuiteTests`, `BotObjectiveTests`, `PathFollowerFixtureTests` |
| the learned policy — loads a `gg2-rl` `.npz`, 211→64→64→27, argmax per head | `PolicyNetwork.cs` | `PolicyNetworkTests` — logit-for-logit against their numpy |
| the hybrid — a controller takes keys and aim inside an engagement window, scripted layer keeps movement, routing, objectives, class | `HybridBot.cs` | `PolicyMatchTests`, `SearchBotTests`, `HybridBotSeamTests` |
| **the controller seam** — how a new bot implementation drops in | `IMicroController.cs`, `BotBrainSpec.cs` | `HybridBotSeamTests` |
| the per-tick controller budget across bots | `IMicroArbiter.cs` | `HybridBotSeamTests` |
| the policy as a controller | `PolicyController.cs` | `PolicyMatchTests` |
| the rollout search, and as a controller | `RolloutSearch.cs`, `SearchController.cs` | `RolloutRestrictionTests`, `SearchBotTests` |
| the next-hop route table (CPD-Search) and the exact A\* heuristic — **ON by default**, `--no-nav-distances` | `NavDistances.cs`, `NavDistanceSet.cs`, `NavSearch.cs` | `NavDistancesTests`, `NavDistanceSetTests`, `NavSearchTests` |
| the tables **on disk** — `nav/<key>.dist.gz`, next-hop only, cost rebuilt on load | `NavDistanceFile.cs`, `NavDistances.FromNextHops` | `NavDistanceFileTests` |
| route dispersion: occupancy (on) and static lanes (measured, not adopted), `--no-bot-occupancy` | `BotLanes.cs`, `BotRoles.cs`, `ScriptedBot.cs` | `BotLanesTests`, `BotOccupancyFlagTests`, `RouteDiversityTests` |
| the training environment seam | `src/Gg2.Env/` | `EnvProtocolTests` |
| **is the nav graph followable** — every edge of every shipped graph flown by a real bot, and whole routes sampled | `EdgeFlights.SweepAll`, `BENCH_FLIGHT` | `EdgeFlightTests` — the corpus tally pinned on **both** routing arms, plus the node census |
| **can a body be where a node says** — 66.6% of nodes overlap the floor at `StandY`; all are free one pixel up | `EdgeFlights.StandingY` | `EdgeFlightTests.EveryShippedNodeCanHoldACharacter` |
| the measurement instrument | `tools/bench/` | not in `Gg2.sln`; build explicitly |

⚠️ **There is nothing to be "ported" against.** Stock GG2 ships no bots; this
layer is this port's own, measured against itself.
`BotLayerCoverageTests`, which used to assert a fork's script-name list, is
deleted; see `CLAUDE.md`, "Except the bot layer".

**So the only judge of a bot change is measurement.** That is what "Measuring a
bot change" below is for.

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

Numbers, tables and scope: `docs/ENV.md`, "Phase 4 re-measured".

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

**The policy's untrained block.** `ObservationWriter.Enemies` is 2, and every
training observation produced so far comes from a duel (1 enemy), so the
second-enemy block — indices `[38, 57)` of the then-209-wide observation, ⚠️
**measured against layout v1 and NOT re-derived for v2**, where the vector is 211
wide and those indices no longer name that block — is
constant zero in training; its first-layer weights are the random
initialisation (`max|delta| = 0.0` from iteration 100 to final). Feeding a real
second-enemy encoding changes 45.2% of the policy's decisions. This does not
invalidate phase 4's own result (also measured in an 8-bot match, so internally
consistent with itself) but means any arm-2/arm-3 comparison pits a controller
with a provably untrained input against a search, which re-derives from the
world state and has no equivalent blind spot.

**Maintenance: a policy is dated by the physics; a search is not.**
`PolicyNetwork` carries weights fit to one simulation version;
`SimulationStamp.Require` refuses a checkpoint whose stamp does not match the
current build. `RolloutSearch` carries no weights and re-derives from the
simulation every decision, so it absorbs a physics change for free. **Current
state: NO trained checkpoint exists for this build.** The layout v2 bump on
2026-09-03 retired the last one, and what ships in
`tests/Gg2.Tests/Fixtures/policy/` is an **untrained** fixture minted against
this build — it loads with no override, and its weights are a He-normal draw
kept for `PolicyNetwork`'s forward-pass arithmetic and nothing else
(`tests/Gg2.Tests/PolicyFixture.cs`; `docs/ENV.md`, "The checkpoint is a
FIXTURE, not a policy"). ⛔ **A checkpoint loading cleanly is therefore not
evidence that it plays**, which is the opposite of the reassurance a green
stamp usually gives. Nothing in the suite depends on `gg2-rl` producing
anything, and every phase-4 behavioural number — including the 21-of-21
death-share result — describes a superseded simulation. Re-measuring it needs
a checkpoint trained against the current head; none exists.

### ⚠️ A duel does not contain what a match contains

A reduced setting does not *contain* what a real one does — a duel has no
second enemy, no projectile crossfire, no sentry, no bystander — and **every
absence arrives as a legal-looking zero, not an error.** The policy's untrained
block above and the 3-6x cost gap above are both instances of this. Before
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
`Gg2.Env` references `Gg2.Protocol`, `Gg2.Maps`, `Gg2.Simulation` and
`Gg2.Bots` — **not `Gg2.Server`** — and nothing in `src/Gg2.Env/` mentions
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

`tools/bench/README.md` has the modes. `docs/ENV.md`, "Phase 4 re-measured",
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
in `docs/ENV.md`, "So a pure refactor". The worst: `EnvServer.cs` reads the
**order** of the reward block's serialisation calls, so **a formatter run over
that method goes red with no semantic change**.

⚠️ **Flagging a move is a courtesy on top of that gate, never a substitute.**

`SIM_UUID` covers the constants, the action encoding, the observation layout
and the simulation behaviour version — **not `Gg2.Bots`**. So a bot change does
not invalidate a checkpoint, and a change to what an observation *means* does.

---

## Where to look

| you are touching | read |
|---|---|
| anything in `src/Gg2.Bots/` | `docs/TRAPS-BOTS.md` — one section per phase-7 item, by name |
| adding or swapping a bot implementation | "Swapping the bot logic out" above — `IMicroController` and nothing else |
| the nav format, A\*, the path follower | same, items 2, 4a, 4b-i, 4b-ii |
| a route table, the A\* heuristic, route dispersion | same — "Shipping the tables", "CPD-Search", "Two ways to spend a next-hop table", "Route variety, scored". ⚠️ **Both ship ON now.** ⛔ The three pinned measurements and the 25 digests do NOT exercise the tables — `ScenarioWorld` sets no `Distances` — so those say nothing about the arm a server runs; see "The gates measure a DIFFERENT ARM". ✅ **`EdgeFlightTests`' corpus sweep is the one gate that does**, and pins both arms so the difference is visible |
| a rocket jump that leaves the ground and does nothing | same — "The rocket that does not go off". ✅ **Fixed**: the blast tilts back along the run when the terrain says nothing is under the muzzle (`PathFollower.LipAimDegrees`) |
| whether a bot can actually follow the graph | same — "The corpus sweep" and "The route sample". ⛔ **28% of edges is the ceiling for a per-edge sweep**: A\* routes around the rest, and driving them anyway credits a walk to an arm that did nothing |
| targeting, aim, the combat controller | same, items 4c-i, 4c-ii |
| the objective layer | same, item 4g, plus "Dual King of the Hill is unwinnable by bots" |
| the env, the observation, the action space | `docs/ENV.md` |
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
