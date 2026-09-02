# Working on the bots

**The front door for bot development.** Written 2026-09-01 because there wasn't
one: `docs/TRAPS-BOTS.md` is 3,200 lines indexed by phase-7 item number, which
answers "what bit the session that built this" and not "what do I need before I
start".

⚠️ **This file navigates and states rules. It does not restate measurements.**
Every number lives in the file that measured it, cited here by section name so a
grep finds it. If you want a fact, follow the pointer — a number copied twice is
a number that goes stale in one place.

---

## The one rule that makes this area different

⛔ **The reference's bot layer is treated as INCOMPLETE, not as a specification.**
The user's call on 2026-08-27, reversing this project's central rule for
`src/Gg2.Bots/` only. A bot's decisions are this project's own: improve them
where the fix is straightforward and measured, write the hard ones down as known
issues rather than chasing them.

⚠️ **This does NOT reach the wire or observable simulation.** A bot may press
"drop intel"; `MatchSimulation` may not decide to drop one. Everything a bot's
input does after it leaves the bot is held to the reference exactly.

Departures taken so far, and the issues deliberately not chased, are in
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
| the learned policy — loads a `gg2-rl` `.npz`, 209→64→64→27, argmax per head | `PolicyNetwork.cs` | `PolicyNetworkTests` — logit-for-logit against their numpy |
| the hybrid — policy takes keys and aim inside an engagement window, scripted layer keeps movement, routing, objectives, class | `PolicyBot.cs` | `PolicyMatchTests` |
| the rollout-search bot | `RolloutSearch.cs` | `RolloutRestrictionTests` |
| the training environment seam | `src/Gg2.Env/` | `EnvProtocolTests` |
| the measurement instrument | `tools/bench/` | not in `Gg2.sln`; build explicitly |

⚠️ **There is nothing to be "ported" against any more.** This layer was seeded
from a bot fork of GG2, and phase 7 closed by porting all 7,202 lines of it —
but stock GG2 ships no bots, the fork has moved, and this port's bots have
diverged from both. `BotLayerCoverageTests`, which asserted that script-name
list, was deleted on 2026-09-01 along with the fork-only constant checks; see
CLAUDE.md, "Except the bot layer".

**So the only judge of a bot change is measurement.** That is what the rest of
this file is for, and it is why the section below is the one not to skip.

### The hybrid, and the result that decides how to use it

⛔ **The policy is most valuable used sparingly.** At a 150px engagement window
it holds about 7% of ticks and beats the all-scripted bot. Widen the window and
the combat gain keeps rising while the objective collapses faster.

⚠️ **Widening the window re-acquires constraints the narrow one sidesteps.** Goal
conditioning is the one to know about: the checkpoint's `kind1/2/3` input columns
were constant through training, so their first-layer weights are still the random
initialisation. `PolicyBot` never sets a goal kind — it gates *when* the policy
drives rather than conditioning *what it pursues* — and that is what makes the
untrained columns harmless. **A macro layer that conditions the policy needs a
retrain first**, and would not look broken without one.

Numbers, tables and scope: `docs/ENV.md`, "Phase 4 re-measured".

### ⛔ Arm 3, the search as micro-controller: unshippable on the tick, verdict-pending on deaths

**21 maps, 40 matches an arm, 8 bots, 5-minute cap, `BENCH_ARMS`, 2026-09-01.**
Arm 1 all-scripted, arm 2 policy hybrid, arm 3 search hybrid — same 150px gate,
same macro layer, one thing different.

#### ✅ SETTLED: the tick gate fails, and n cannot rescue it

⛔ **Arm 3 exceeds the 33.3 ms budget on 20 of 21 maps.** Worst 80.65 ms
(`ctf_eiger`); the single map under is `ctf_avanti` at **31.70 ms**, which is not
headroom. Arms 1 and 2 sit at 0.8–7.2 ms throughout.

⚠️ **This half does not depend on the sample size, and the reason is the shape of
the statistic rather than the size of the run.** A worst tick is a **maximum over
every tick of every match**, not a rate. **A small sample weakens a share; it
cannot weaken a maximum** — if anything 40 matches *understates* a worst case.
`gg2-rl` made that argument and it is why this half is reportable now.

#### ⚠️ NOT SETTLED: the death-share sign count is not yet interpretable

The raw counts are **arm 3 better than arm 1 on 20 of 21**, and **better than arm 2
on 17 of 21**. ⛔ **Do not read the second as a result.**

| |arm 3 − arm 2| | maps |
|---|---|
| under 0.5 points | **6** |
| under 1.0 point | **9** |

⚠️ **And the capped exposure belongs in the same breath as those counts, not in a
later section.** `cp_dirtbowl` capped **85 of 120** match-arms, `koth_gallery` 78,
`ctf_conflict` 72. **Death share counts deaths in capped matches too, so it is far
less exposed than a win share would be — but "less exposed" is not "unexposed"**,
and this repo has already paid for treating those as the same thing. `gg2-rl`’s
rule, and it is trap 1: if the floor ever licenses the sign test, **the capped
number goes in the row beside the share, not in a paragraph under it.**

**All four maps where arm 3 reads worse are worse by 0.0–0.4 points**, and nine of
the twenty-one carry effects under a point. A sign test counts DIRECTIONS, so a map
whose direction is noise is a whole observation lost rather than a slightly noisier
one. **Nine such maps is not a 17-of-21.**

✅ **THE CHECK HAS RUN, AND THE SIGN TEST IS NOT LICENSED.**
`BENCH_PERMAP` at n=40 against n=400, policy versus control, 2026-09-01:

⛔ **Directions held for every map with |effect| ≥ 4.2 points.** Two maps flipped
below it — `arena_lumberyard` +1.4p → −4.1p and `arena_montane` +3.5p → −0.4p —
so 19 of 21 agreed, **and 19-of-21 is not the number to quote.** The floor is.

| arm 2 vs arm 3, |effect| against the 4.2p floor | maps |
|---|---|
| at or above — licensed | **6** |
| below — unlicensed | **15** |

**Fifteen of twenty-one per-map directions are below the level at which direction
was shown stable.** So the 17-of-21 is withdrawn as a result, not merely qualified.

✅ **What survives is a licensed SIX-of-six, and it is worth pricing before it is
quoted beside phase 4’s number in this same file:**

| | signs | one-sided p |
|---|---|---|
| arm 3 over arm 2, licensed maps | 6 of 6 | **0.016** |
| phase 4, policy over control | 21 of 21 | **4.8e-07** |

**Four orders of magnitude apart — 32,768x. Modest, not decisive.** A licensed
six-of-six is worth more than a laundered seventeen and **is not the same object**
as the result it sits next to. `gg2-rl` checked the selection and it holds: the
threshold admits **both signs**, so it cannot bias direction, and **4.2p is
exogenous** — it came from the policy-versus-control run rather than from the arm
data it is applied to. A threshold tuned on the same data would have been the
laundering this whole sequence existed to avoid.

⛔ **And the six are the LARGE-EFFECT maps by construction**, so *"all six favour
arm 3"* is a statement about where the effect is big and **not about the typical
map.** The fifteen unlicensed directions are **unknown, not absent** — those are
different claims and the second is the one supported.

✅ **THE FALLBACK HAS RUN. The aggregate death share, pooled over every map and
match, with its counts:**

| arm | death share | deaths | vs arm 2 | vs arm 1 |
|---|---|---|---|---|
| 1 all-scripted | 49.82% | 17,432 / 34,990 | — | — |
| 2 policy hybrid | 46.17% | 16,167 / 35,015 | — | z = −9.66 |
| 3 **search hybrid** | **44.55%** | 14,954 / 33,567 | **z = −4.26** | z = −13.82 |

⛔ **The aggregate is decisive where the sign test was not.** Arm 3 beats arm 2 by
1.62 points at **z = −4.26** (p ≈ 1e-5 one-sided) on ~34,000 deaths an arm — against
the licensed six-map sign test’s p = 0.016. **The statistic that survived the
stability check is also the stronger one**, which is not what anybody expected when
it was chosen as a fallback.

⚠️ **Read the denominators, not only the shares.** Arm 3 records **1,448 fewer total
deaths** than arm 2 — 4.1% — so its matches are less lethal overall as well as
less lethal to its own side. That is a real difference between the arms and not a
censoring artefact, but a share alone would have hidden it.

#### ✅ Both units agree, and the gap between them is a fact about the BOT

**`gg2-rl` objected that the pooled z treats ~34,000 clustered deaths as
independent** — ~40 arrive together per match — and that it is therefore an upper
bound on the evidence. **And then that the match-level fix is biased the other way**,
weighting a 2-death match like an 80-death one. So the two **bracket** the answer:

| unit | arm 3 vs arm 2 | statistic |
|---|---|---|
| pooled over ~34,000 **deaths** | −1.62 points | z = **−4.26** |
| over 840 **matches** an arm | −3.59 points | t = **−5.60**, df 1678 |

✅ **Both significant, so the conclusion is robust to the choice of unit** — the
pre-registered "both" branch. ⚠️ **Zero-death matches: none, in any arm**, so the
filter that worried us does not exist here.

⛔ **But the supposedly CONSERVATIVE unit came back STRONGER, which the bias
analysis did not predict — and the reason is not clustering.** It is `gg2-rl`’s
other point: **the two estimate different quantities**, and here they differ a lot.
Bucketed by how lethal the match was:

| deaths in match | arm 2 share | arm 3 share | gap |
|---|---|---|---|
| 1–9 | 46.0% | 37.0% | **−9.1p** |
| 10–24 | 45.7% | 39.5% | −6.2p |
| 25–49 | 46.3% | 43.5% | −2.8p |
| 50–99 | 46.3% | 45.4% | **−0.8p** |

⛔ **The search hybrid’s advantage is concentrated in QUIET matches and very nearly
vanishes in bloodbaths** — monotone across four buckets, 118–370 matches an arm in
each. The pooled statistic is dominated by the high-death matches where the
advantage is −0.8p; the match-level mean weights the quiet ones equally, where it is
−9.1p. **Neither number is wrong and neither is "the" effect.**

⚠️ **This was invisible in both aggregates and is the most interesting thing the
experiment produced about the bot rather than about the method.** It also fits the
lethality shift: arm 3 records **4.1% fewer total deaths** and a median of 41 a
match against arm 2’s 45.

⛔ **THE BUCKETS ABOVE CONDITION ON A POST-TREATMENT VARIABLE, and `gg2-rl` caught
it before the threading decision.** Deaths-in-match is an *outcome* of the arm —
arm 3 causes 4.1% fewer — so the high-death bucket selects arm 3’s **failures**
against arm 2’s typical matches, and the low-death bucket selects arm 3’s
**successes**. **That manufactures a monotone shrinking advantage with no mechanism** —
exactly the shape observed.

✅ **RE-CUT on a pre-treatment axis, and the trend SURVIVES.** Maps bucketed by
**arm 1’s own death rate** — arm 1 never sees arms 2 or 3, so the bucket cannot be
their outcome:

| maps by arm-1 lethality | a1 deaths/match | arm 2 | arm 3 | gap | t |
|---|---|---|---|---|---|
| 7 quiet | 16.2 | 46.6% | 38.9% | **−7.7p** | −4.77 |
| 7 middling | 45.3 | 45.9% | 43.9% | −2.0p | −2.63 |
| 7 bloody | 63.4 | 45.9% | 44.9% | −1.0p | −1.58 |

⛔ **Three per-bucket t’s are NOT a test that the buckets differ**, and the claim is
about the difference. `gg2-rl` caught it — *"significant beside non-significant read
as a contrast"* is its own entry — and reconstructed the interaction from the t’s.
**Recomputed here from the per-match rows, which is the better source:**

    quiet minus bloody:  -6.68p   SE 1.75   z = -3.83   two-sided p = 1.3e-4
    (its reconstruction: -6.7p    SE 1.73   z = -3.86   p = 1.1e-4)

**So the buckets genuinely differ, and the trend is established rather than
eyeballed.**

| maps by arm-1 lethality | gap | 95% CI |
|---|---|---|
| 7 quiet | −7.72p | [−10.90, −4.54] |
| 7 middling | −2.01p | [−3.50, −0.52] |
| 7 bloody | −1.04p | **[−2.33, +0.25]** |

⚠️ **"Not distinguishable from zero on bloody maps" is NOT "zero on bloody maps."**
That interval spans a real advantage to a slight disadvantage. **The TREND is
established; the bloody bucket’s value is not**, and the two are different claims.

#### ⛔ RETRACTED: "it helps most where the tick budget is least pressured"

**Mine, and checking the link killed it.** I read the lethality trend as meaning
the search earns its keep exactly where it is cheap. **Quiet maps are not cheap:**

    quiet maps      arm-3 worst tick  70.11 ms
    middling maps                     72.12 ms
    bloody maps                       65.07 ms
    correlation, map lethality vs arm-3 worst tick:  r = +0.16

⚠️ **The tick cost is essentially independent of map lethality** — it is driven by
decisions coinciding, which happens everywhere. **The death-share advantage varies
by map; the tick cost does not.** Those are two independent facts and I had
conflated them into a mechanism.

✅ **Which leaves threading NEUTRAL rather than weakened**: if it fixed the tick
cost, the search would deliver its map-varying advantage everywhere, and there is
no "it only helps where it is already affordable" to argue against it.

#### ⛔ And on the re-run the tick gate fails on 21 of 21

The first run had `ctf_avanti` at **31.70 ms** — the single map under budget. On the
re-run the same map reads **47.36 ms**, and the lowest arm-3 tick anywhere is
`koth_gallery` at **45.50 ms**. **Every map fails.**

✅ **That is `gg2-rl`’s coverage rule confirmed by accident, on this repo’s own
data.** A max under budget was **no evidence of a pass** — and the one map that
appeared to pass did not survive being measured twice. Had anybody built on that
reading, the thing it licensed would have evaporated on the next run.

#### ⛔ RETRACTED: "shippable at 8 bots, unshippable at 12"

**That was mine and it was wrong.** A three-map, six-match run read **31.15 ms** at
eight bots and I reported it as *"passes with 6% to spare"*, against 92.38 ms at
twelve — framed as a fact about bot counts. `gg2-rl` carried it to its user as
*"shippable at 8, unshippable at 12"*.

⛔ **The 21-map table fails on 20 of 21 maps AT EIGHT BOTS.** The 8-bot pass was one
map’s luck generalised — the same configuration-read-as-general error this file
records twice already, made by the person who wrote those entries, hours later.

⚠️ **And it makes the verdict stronger rather than weaker.** *"Too expensive at 12,
fine at 8"* is a deployment question with a knob. **"Fails 20 of 21 at eight" is
not: there is no measured bot count at which this ships as built.**

#### ⚠️ And the experiment costs what it costs

**Arm 3 is roughly twenty times slower to evaluate than either other arm** — this
run took 1,916 seconds and arm 3 is nearly all of it. `gg2-rl` pre-registered 400
matches an arm without knowing that. ⛔ **"Run it at 400 because the pre-registration
says so" is not one of the honest outcomes** when the pre-registration was written
in ignorance of the cost; the outcomes are *40 is enough, shown* or *this criterion
is unaffordable at this size, said plainly*.

### ⛔ A duel does not CONTAIN what a match contains, and every absence is a zero

⛔ **One mechanism, and this project found it twice in one day as two unrelated
bugs.** A reduced setting does not *contain* things a real one does — a duel has no
second enemy, no projectile crossfire, no sentry, no bystander — and **every
absence arrives as a legal-looking ZERO rather than as an error.** Nobody has to
notice a wrong value; somebody has to notice an absent one.

| the duel lacks | the zero shows up in | it was read as |
|---|---|---|
| a second enemy | the observation column, so the gradient | "the policy is trained" |
| projectiles, sentries, bystanders | the per-tick simulation cost | "a decision costs 7.64 ms" |

⚠️ **The operative form is `gg2-rl`’s and it is stronger than "vary the
population":** the question is not *what did I forget to vary* but **what does the
reduced setting not CONTAIN.** Naming one missing term feels like having found the
gap, and this repo named population and then took a cost measurement that was still
3-6x low for four other reasons.

#### The weights half

**`gg2-rl` 2026-09-01, verified here rather than taken.**
`ObservationWriter.Enemies` is **2** and a duel has **one**, so the second enemy
block — indices **[38, 57)** of the 209 — is **constant zero in every training
observation this project has produced.** Its first-layer weights are therefore
the random initialisation, unchanged from iteration 100 to final
(`max|delta| = 0.0`, their measurement). Feeding it a real enemy encoding changes
**45.2%** of the policy’s decisions.

✅ **Checked on this side**: an observation taken in a two-character duel has that
block all zero; add a second enemy and features inside it light up. So a policy
driving a bot in an 8- or 12-bot match is reading a block it never saw during
training, through weights nothing ever trained.

⛔ **What this does NOT qualify.** It is not new, and it is not a defect in any
arm-2 measurement: **phase 4’s 21-of-21 was itself an 8-bot match result**, so that
number was obtained with this property present. Arm 2 and phase 4 are off
distribution in the same way and remain comparable to each other.

⚠️ **What it DOES qualify** is what "the policy" means in any match comparison:
**its controller is provably untrained on the condition the benchmark puts it in.**
A search has no equivalent — it re-derives from the world in front of it — so an
arm-2/arm-3 comparison is not two controllers meeting the same standard. **State
that beside the comparison rather than letting it be discovered afterwards.**

#### The clock half

⛔ **A search decision costs 7.64 ms in a twelve-character bench SCENARIO and
22-44 ms in a real match** — 3-6x, measured 2026-09-01 by decomposing arm 3’s worst
tick. Every candidate re-simulates every projectile in flight, every sentry and
every bystander for every tick of the horizon, and a scenario has none of them.

⚠️ **The 7.64 ms was itself a correction** — of a 2.94 ms duel figure, made by this
repo, about population. It was right about population and still 3-6x low, which is
the whole argument for the operative form above.

⚠️ **Falsification, since this is a claim about training data**: it dies the moment
anything trains on observations with two or more enemies present. Check
`ObservationWriter.Enemies` against the population the trainer actually runs before
repeating it.

### ⛔ A policy is DATED by the physics. A search is dated by nothing.

**`gg2-rl`'s observation, 2026-09-01, and it is a maintenance property rather
than a performance one — so no criterion in this project measures it.**

`PolicyNetwork` carries weights fitted to one version of the simulation, which is
what `SimulationStamp` exists to police: change the physics and a checkpoint is
**wrong**, silently, and `Require` refuses it. `RolloutSearch` carries no weights
at all — it **re-derives from the simulation in front of it, every decision**. So
the same physics change that invalidates a policy leaves a search **automatically
correct.**

    a shipped policy   every physics change costs a retrain, forever
    a shipped search   every physics change is absorbed for free

⚠️ **For a bot meant to live on a public server against a simulation still being
changed, maintenance may be the larger term** — and it is one the death-share
criterion cannot see, in either direction.

⚠️ **Written down BEFORE the bump that prompted it landed**, deliberately. The
platform lane had `SimulationBehaviourVersion` bumped in its working tree and the
observation was made while the outcome was still unknown; recorded afterwards it
would read as a rationalisation of whichever way that went. **Its falsification is
cheap and should be checked rather than assumed: if the physics stops changing,
this argument is worth nothing.**

⚠️ **And the cost of a bump lands unevenly on the arms, worth stating plainly
rather than under the argument above.** An all-scripted arm and a search-hybrid arm
carry no checkpoint and survive; **a policy-hybrid arm dies until somebody
retrains**, and that arm is the comparability anchor. Without it, a search-hybrid
comparison answers "does this beat all-scripted" rather than the question actually
asked. **Phase 4's 21-of-21 also becomes a statement about superseded physics**,
which is a real loss to this project independent of what any new arm shows.

✅ **It landed, and the prediction held to the letter (2026-09-02).** The carry
took `SIM_UUID` to `5dac8a5e7c37ce3a`; **25 tests went red, every one of them a
policy test**, and not one search or scripted test moved. The policy arm died
exactly as described and the cost was exactly the one written down here in
advance.

⛔ **The repair is NOT a retrain, and that is a decision rather than an
inability.** The user's call was that the trained policy may not be carried
forward, so this repo stopped waiting on `gg2-rl`: the checkpoint is now a
**fixture** — real weights and a numpy oracle for `PolicyNetwork`'s arithmetic —
reached through `tests/Gg2.Tests/PolicyFixture.cs`, and nothing in the suite
depends on that repo doing anything. `docs/ENV.md`, "The checkpoint is a FIXTURE
now, not a policy".

⚠️ **So the loss above is now realised, not hypothetical**: every phase-4
behavioural number in `docs/ENV.md` describes a simulation this build no longer
is. The 21-of-21 death-share result is history, not a current claim, and
**re-measuring it needs a checkpoint trained against this head** — which nobody
has and nobody is currently making.

---

## ⛔ Measuring a bot change

**Read this before running an evaluation, not after.** Every phase-4 number was
wrong once for the reasons below, and none of the errors was caught by review.

### The statistic

- ⛔ **Prefer the statistic with no subset.** A label travels and a filter does
  not. **Death share** counts deaths across every match — drawn, capped and
  decisive alike — and is the criterion for that reason. A **win share** is over
  decided matches only.
- ⛔ **`decided` is NOT the win share's denominator.** `MatchOutcome.Decided`
  **includes a draw** (`HeadlessMatch.cs:84`, and `Rounds.cs` decides with
  `Team.Spectator` in five places). Wins-plus-losses is the denominator, and on
  some maps it is a twentieth of the decided count.
- ⛔ **A statistic over a subset must display the subset's size IN THE SAME
  ROW** — never a caption, because a copy-paste separates them.
- ⚠️ **What invalidates a comparison is the censoring GAP between arms, not its
  level.** 3%/3% is comparable and 30%/30% is comparable; **3%/15% is broken and
  looks better than the second.** Rank on the differential.

### The design

- ⛔ **Keep the control arm even when it looks redundant.** Two of the largest
  findings of 2026-09-01 came out of a control row added only to validate an
  assumption nobody doubted.
- ⛔ **Write the test and its possible outcomes down BEFORE the data exists.**
  The only pre-registered test that day is the only result either session would
  defend unqualified. A p-value on a post-hoc quantity and one on a
  pre-registered test **print identically**.
- ⚠️ **Ask what result would falsify the check.** A test essentially nothing
  could fail is not evidence — four arms that are cumulative in construction
  cannot fail a monotonicity test.
- ⚠️ **Comparing two significant numbers is not a test of their difference.** A
  significant result beside a non-significant one *reads* as a contrast and is
  not one; the difference needs its own test.
- ⚠️ **A match cap censors.** Check what fraction of each arm reached it before
  reading anything computed over outcomes.
- ⚠️ **N maps is N measurements of ONE policy**, not N trials of a method.
  Training variance across seeds can exceed the effect. Maps buy precision;
  seeds buy generalisation.

### The mechanics

- ⚠️ **Warm past tier-1 before measuring.** Promotion is at 30 calls and the
  rejit is asynchronous, so a short warm-up measures tier-0 code — it made one
  duel tick read 23 µs at one world and 2.9 µs at sixty-four.
- ⚠️ **Name the build.** Release and Debug differ by about 2x, and a re-measure
  that forgets looks exactly like a factor-of-two regression.
- ⚠️ **Publish to a fresh directory.** A running `bench.exe` holds its own file,
  the build silently cannot overwrite it, and the next run measures the old
  binary.
- ⚠️ **Two maps are pathological.** `cp_egypt` never produces a winner — 0
  decisive out of 400 in both arms, at any cap — and `ctf_classicwell` cannot
  finish in twenty minutes, with 82% of *control* matches capping and still
  counting as `decided`.
- ⚠️ **A per-map comparability flag is a property of the measurement, not of the
  map.** The set of untrustworthy rows changes with the cap.

`tools/bench/README.md` has the modes. `docs/ENV.md`, "Phase 4 re-measured", has
the worked examples behind every bullet above.

### The three pinned end-to-end measurements

`ScenarioSuiteTests.Baseline`, `BotObjectiveTests`' intel run and
`PathFollowerFixtureTests`' longest steerable leg are pinned **in both
directions**. ⛔ **A failure there is the re-record, not a broken test**: check
the change is one you meant, check it went the way you meant, update the row, and
say so in the commit.

⚠️ **Two of those three were hiding an error when they were finally gated.** If a
number is quoted as a measurement anywhere in this repo, ask what would go red if
it doubled — on this project's evidence the answer is usually "nothing".

---

## The `gg2-rl` seam

The sibling training project reads this repo's source directly for its drift
gate. ⛔ **A pure refactor of anything it parses is a cross-repo event.** The
full list of eight patterns across six files, and which three are fragile, is in
`docs/ENV.md`, "So a pure refactor". The worst: `EnvServer.cs` reads the **order**
of the reward block's serialisation calls, so **a formatter run over that method
goes red with no semantic change**.

⚠️ **Flagging a move is a courtesy on top of that gate, never a substitute.**

`SIM_UUID` covers the constants, the action encoding, the observation layout and
the simulation behaviour version — **not `Gg2.Bots`**. So a bot change does not
invalidate a checkpoint, and a change to what an observation *means* does.

---

## Where to look

| you are touching | read |
|---|---|
| anything in `src/Gg2.Bots/` | `docs/TRAPS-BOTS.md` — one section per phase-7 item, by name |
| the nav format, A\*, the path follower | same, items 2, 4a, 4b-i, 4b-ii |
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

**A claim the repo makes about itself.** The code is self-consistent and the
description is false, so nothing inside the repo can contradict it — only a
reader who *acts* on the description finds out. It has happened repeatedly here,
in both directions, and a stale "not done" costs exactly what a stale "done"
does: the follower's jump branch was documented as unported two hundred lines
above its own implementation, and a session took the queue item before finding
the work already done.

⚠️ **The author is the one reader guaranteed to reconstruct the mistake.**
Re-reading recovers the meaning you intended, which is exactly the meaning that
was wrong. **Send a claim across a seam early; do not review it harder.** Cheap
contradicting actions for questions of meaning — grep the definition, keep the
control arm, run the one command that could disagree — and review for questions
of mechanism.
