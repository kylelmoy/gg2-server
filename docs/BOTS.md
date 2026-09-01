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
