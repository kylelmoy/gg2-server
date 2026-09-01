# `Gg2.Env` — the batch-stepping surface

The design record for `docs/BOTAI.md` item 10. The consumer is `gg2-rl`
(`D:\code\gg2-rl`): its `docs/CONTRACT.md` names what must cross the seam, its
`docs/PLAN.md` phase 0 is the first thing that will call this, and its
`docs/DESIGN.md` is where the observation and action sketches below come from.

## The scope call — decided 2026-08-27, by the user

Phase 9's handoff required these four to be decided rather than inferred:

1. **The v0 observation layout is built now**, to `gg2-rl/docs/DESIGN.md`'s
   sketch, version-stamped and folded into `SIM_UUID`. The "deliberately
   undesigned" stance in `docs/PLAN.md` phase 9 held until a trainer existed to
   want something; that document now exists and says what it wants. Churn is
   expected and priced in via the stamp.
2. **The env accepts multi-discrete heads, and the head→wire conversion is
   server-side** — including the aim-delta turn-rate cap. `CONTRACT.md` puts the
   action encoding version inside `SIM_UUID`, which implies server ownership;
   the cap is an honesty property that must not be re-implementable differently
   per trainer; and the delta needs the current aim, which the server holds.
3. **Rewards go out as components, not a scalar.** Damage dealt, damage taken,
   kills, deaths, termination reason — a fixed contiguous struct per world per
   step. The trainer folds them into a scalar, so reward iteration never needs
   a C# rebuild. This still satisfies `CONTRACT.md`'s "one contiguous block".
4. **v0 is duels only**, wrapping `ScenarioWorld`. Full matches through the same
   protocol wait for a consumer (`gg2-rl` phase 4).

## What already existed, and the one gap

Items 1, 2, 6, 8, 9 and 11 of `docs/BOTAI.md` supply the step function, the
snapshot, the spatial queries, the deterministic batch precedent
(`MatchRunner`), the stamp and the gamemode-less duel. The gap is **item 7's
first half**: `TickEvents` carries kills, deaths, spawns and captures with
attribution, but **no per-tick damage amounts**. The trainer's reward is dense
damage-dealt-minus-taken and its design forbids reconstructing it by state
diffing — so the accumulators below are a prerequisite, not a nicety.

## Prerequisite: damage accumulators in `TickEvents`

- `SlotBuffer<double> DamageDealt` and `SlotBuffer<double> DamageTaken`,
  cleared by `ClearOutputs`, written where character damage is applied.
- `DamageApi.Deal` is the chokepoint for character damage but does not know the
  dealer — attribution lives at the call sites, next to the `LastDamageDealer`
  writes. Accumulate there (or thread the dealer through `Deal`); either way
  the rule is: **credited at application time, following `LastDamageDealer`
  semantics**.
- **Blocked damage accumulates nothing.** `Deal` returning false (Team
  Deathmatch invulnerability) means no amount landed.
- **Self-damage counts as taken, never as dealt.** A rocket jump must not score
  positive reward.
- Sentry and generator damage do not route through `Deal` and are **out of v0**
  — duels have no objective. Noted here so the omission is a decision.
- This is additive bookkeeping: no simulation behaviour changes, no parity
  impact, but it touches `Gg2.Simulation`, so the standing chore applies
  (re-measure the parity bounds after the change).

## Architecture

`src/Gg2.Env`, a console project in the `Gg2.Parity` mould (`gg2-env`), with
its types usable as a library by tests:

- **`DuelEnv`** — one world: `Reset(spec)`, `Step(actions)` with action repeat,
  termination on death or tick cap, snapshot/restore via the raw-bytes
  round-trip item 2 proved. Wraps `ScenarioWorld`.
- **`EnvBatch`** — N `DuelEnv`s stepped in one call, parallel the way
  `MatchRunner` is: each world owns its own `CollisionWorld` and RNG, results
  identical regardless of batch size or thread count, and gated the same way —
  whole-array comparison, not statistics.
- **`ActionDecoder`** — heads → `PlayerInput`, per slot, stateful only in the
  tracked aim angle.
- **`ObservationWriter`** — the v0 layout, filled into a caller-supplied
  `Span<float>`, no allocation, built on `WorldView`'s nearest-K queries.
- **`gg2-env serve --port <n>`** — the socket host. Loopback only, one client,
  like the control port.

## Protocol v0

Length-prefixed binary frames on a local TCP socket: `u32` little-endian
length, one message-kind byte, payload. Request/response.

| Message | Carries | Returns |
|---|---|---|
| `Hello` | client's protocol version | protocol version, `SIM_UUID`, obs layout version, action encoding version, obs length, head shape. **Mismatch refuses** — the `PROTOCOL_UUID` idiom, not a warning |
| `MakeBatch` | N, map, per-side classes, spawn placement (explicit positions, or **NaN** for sampled — see below), base seed, tick cap, action repeat k, **per-side driver and skill tier**, **per-side rollout opponent**, **separation band (min, max)** | ack + initial observations |
| `Step` | N action tuples (one per world: 4 head indices + aim-delta bin) | N × (obs block, reward components, done, termination reason, **the action that side actually took + whether it is reproducible**) |
| `Reset` | world index, optional new seed | that world's fresh observation |
| `Snapshot` | world index | the world's state bytes |
| `Restore` | world index, state bytes | ack |
| `Close` | — | ack |

### ⚠️ Sampled spawn placement, and the year this row was wrong

**This row said "explicit positions or sampled" from the first draft and only
the first half existed** until 2026-08-28. Nothing was wrong with the code; the
document described a surface that did not exist, and a reader who trusted it
built a trainer around it. **A documented capability that was never built is the
same trap as a gap recorded only in a code comment** — `CLAUDE.md` names the
second and this was the first.

**What it cost**, measured by `gg2-rl` before it was built: a duel on any map
required the caller to know that map's coordinates, which is exactly the game
knowledge the trainer is not supposed to hold. Driving `gg_debug`'s (600,850) at
four other maps, 8 worlds, 199 steps:

| map | what happened |
|---|---|
| `gg_debug`, `arena_lumberyard`, `koth_harvest` | every episode to the cap, no deaths |
| `ctf_2dfort` | 5 deaths, intermittent |
| `tdm_mantic` | **32 deaths across 16 episodes — exactly 2.0 each** |

Both characters, every episode, together. They are in **open air** at that
coordinate: y goes 850 → 2,267 and they die at **t152**. Environmental, not
combat — and since the reward is damage taken, a trainer sampling maps would have
learned to avoid the map rather than to fight.

**How it works now.** A coordinate of **`NaN`** (`DuelSpec.Sampled`) means "draw
a spawn point off the map". The draw is `state.Random.Next(count)` — the same
stream and the same call `Timers.PlayerSpawn` uses for a real respawn, so a
sampled duel starts where an ordinary spawn would have put somebody and nothing
about the distribution is this project's invention.

- ⚠️ **A sentinel rather than a new field, deliberately**: it rides in the four
  doubles `MakeBatch` already carries, so the frame layout is unchanged and
  **`EnvProtocol.Version` does not move**. A client built against v1 goes on
  working; one that wants sampling sends NaN. No map coordinate can ever be NaN,
  so there is no legal value it collides with.
- ⚠️ **Half a sampled pair is refused**, loudly. One NaN of a pair is meaningless
  and is the shape a marshalling bug produces; letting it through would place a
  character at NaN and make every comparison about it quietly false.
- ⚠️ **The stream is now seeded BEFORE placement.** It was seeded after, which
  was invisible while every placement was explicit and would have taken a
  sampled draw from whatever the previous episode left behind.
- ⚠️ **The facing is read off the placed positions, not off the spec.** A sampled
  spec holds two NaNs and `NaN <= NaN` is false either way, so both agents would
  have faced 180 and one would have started with its back turned.

`SampledSpawnTests` gates all of it, including the lethal explicit coordinate as
a pinned control.

⚠️ **The rest of the table above was then checked by hand**, on the argument that
a table with one false row is the highest-prior place to look for a second. Every
other row holds: all seven messages are dispatched in `EnvServer`, the auto-reset
really does happen inside `Step` (`EnvBatch`), and the per-world seed derivation
is not only real but stronger than the row claims — the index is *mixed in*
rather than added, so two worlds cannot share a stream by arithmetic accident.
**One false row, six sound ones**, recorded so the next reader does not re-check
them.

**Auto-reset on termination**: a world whose episode ends is reset within the
same `Step` call (seeded from its own stream) and returns the fresh episode's
first observation alongside the terminal reward components. Round-trip economy;
explicit `Reset` remains for forcing a new scenario.

### The seeding guarantee, stated as a promise rather than a description

⚠️ **This used to read "world `i`'s seed derives from the base seed and `i`", and
that undersold a property a trainer needs to rely on.** A description that
undersells is the same defect class as one that oversells — nobody can depend on
the real behaviour, because the document never promised it. Raised by `gg2-rl`,
whose phase 0 criterion 2 leans on this.

**The promise, and it is gated in both halves:**

1. **A world's seed is a pure function of the base seed and its index**, so batch
   size cannot move any world's stream. World 7 of a batch of 64 is bit-identical
   to world 7 of a batch of 8. `EnvBatchTests` compares a whole batch against a
   batch of one seeded to be that world.
2. ⚠️ **The index is MIXED IN, not added** — `seed + index * 2654435761`. So two
   batches one seed apart share **no world at all**. Plain `seed + index` would
   make `{seed 1, count 100}` and `{seed 2, count 100}` share 99 of their 100
   worlds, and two runs a researcher believed were independent would be almost
   the same experiment. `EnvBatchTests.TwoBatchesOneSeedApartShareNoWorld`.
3. **An episode's seed also mixes its episode number**, so a world that
   auto-resets does not replay its own first episode.

### Per-slot drivers, and protocol v2

**Built 2026-08-28, requested by `gg2-rl`.** Until this existed `MakeBatch` could
only drive both slots from the client, so a duel between two things that both
live in this repo — `RolloutSearch` against `ScriptedBot` — could not be asked
for over the socket at all. It blocked phase 1's exit criterion (500 duels per
matchup), phase 2's collection against fixed opponents, and phase 3's opponent
pool ("past checkpoints, the scripted bot, and a noise bot").

| Driver | What it is |
|---|---|
| `Client` (0) | the action arrives over the socket. **The default, and what a past checkpoint is** — the trainer has the weights and drives that slot itself |
| `Idle` (1) | presses nothing, and goes on looking where it was. The comparison floor |
| `Scripted` (2) | `ScriptedBot` at a tier 0-5. ⚠️ **Refused without a nav graph**, not downgraded |
| `Rollout` (3) | `RolloutSearch`. ⚠️ **~2.94 ms a decision** against a scripted bot's microseconds — an evaluation baseline, not a collection opponent |

**Protocol v2**: `MakeBatch` gains four `i32` fields (red driver, blue driver,
red tier, blue tier) and every reward block grows from 20 to **44** bytes,
carrying the action that side actually took plus an exactness flag. ⚠️ **A v1
client is refused at `Hello` rather than tolerated** — the reward block changed
length, so a v1 reader would slice a v2 reply into plausible nonsense.

⚠️ **The whole components block stays 4-byte typed** (eleven fields, the five
action heads as `i32` each). The heads would fit in a byte, but `CONTRACT.md`
maps this block straight into a tensor and six bytes an agent against a 209-float
observation is not a trade worth making.

#### ⚠️ The bot is polled AS IT IS, so the reported action can be lossy

Each bot drives its slot through its own `IInputSource.Poll`, on its own
cadence. The opponent is then really that bot rather than an imitation squeezed
through the action space — which matters most for `ScriptedBot`, whose aim has
**no turn-rate cap** and which would be a materially weaker shot if forced
through one.

The consequence is that the action reported back is the *nearest* action, not
necessarily a reproducible one:

- the four discrete heads are **exact** — they are recovered from the same key
  mask bits the decoder writes;
- the **aim head can saturate**. The bot's turn over a decision is quantised to
  the nearest of 15 bins over ±30°, and a scripted bot snapping onto a fresh
  target turns much further than that.

**Measured**, scripted vs scripted on `gg_debug`, three seeds: **86-89% of
actions are exact**. `ActionExact` reports it per step, so nothing has to assume.

⚠️ **This matters for imitation and not for logging.** Cloning a scripted bot
from these actions learns a throttled version of it; recording what an opponent
did is exact enough.

#### ⚠️ `Snapshot` is refused while a bot drives a slot

The honest edge of "the bytes ARE the state". That claim is true of `WorldState`,
which holds no reference to anything — but a bot is state *outside* it:
`ScriptedBot` carries a route, a target, four clocks and its own RNG stream, and
`RolloutSearch` carries a decision countdown and a decoder. Restoring the world
alone would resume with an opponent mid-way through a plan for a world that no
longer exists, and it would do it silently. **Lifting this means serialising the
bot layer**, which is real work and is not wanted yet.

#### ✅ A duel is a deathmatch, and the objective layer plays it

`ScriptedBot` gets where to walk from `BotObjective`, and a duel runs on
`ScenarioWorld` — `docs/BOTAI.md` item 11, a world with no gamemode in it. **With
no objective there is nothing to tell a bot where to be**, which is the same open
issue as "Team Deathmatch bots have no goal" in `docs/TRAPS-BOTS.md`.

Measured before it was handled, sampled spawns on `ctf_2dfort`, 300 decisions:
neither bot walked a pixel, **nobody fired a shot**, and every episode ended on
the tick cap.

✅ **Fixed in the bot layer rather than here.** `BotObjective` grew a deathmatch
branch, so a duel now falls to the same code path `tdm_mantic` uses and
`DuelEnv` simply gives its scripted bots a real `BotObjective`.

⚠️ **`DuelEnv` had a hand-rolled `GoalLocked` chase first, and it is gone.** The
objective layer is better at it in three separate ways:

- it stands at a **spot** at the class's own band instead of walking onto the
  target — the chase parked a soldier 59px from an idle opponent, inside a band of
  110, where it correctly refuses to fire, and it never moved again: **250
  decisions, zero shots, zero damage**;
- the spot search returns a **real walkable node**, where offsetting the enemy's
  position by the band lands off whatever surface the target is on and A\*
  refuses it;
- its re-issue is throttled on the **resolved node** by `BOT_GOAL_RETARGET_DIST`.
  ⚠️ That last one nearly shipped as a bug: `PathFollower.SetGoal` clears both
  `ReplanAt` and `PlanFailUntil`, so the env's own version defeated the
  unreachable-goal throttle completely — **55 searches in 60 decisions** against a
  goal that could never route.

**Against a STATIONARY opponent**, 8 seeds, sampled spawns, 250-decision cap:
`arena_montane` went **0 → 8 of 8** duels ending in a death and `tdm_mantic`
**0 → 5 of 8**. That is the case an untrained policy looks like on its first
episodes, and it did not work at all before.

⬅️ **Still open: `gg_debug`'s two spawn points are 59px apart**, already inside a
soldier's band, and a bot that starts there moves 41px in 250 decisions with a
live route it never walks. `docs/TRAPS-BOTS.md`, "Team Deathmatch", has the
repro and what has already been eliminated.

#### ⚠️ Which maps two sampled duellists can actually reach each other on

Scripted against scripted, sampled spawns, 8 seeds × 250 decisions each:

| map | episodes | ended in a death | engaged |
|---|---|---|---|
| `gg_debug` | 8 | **8** | yes |
| `tdm_mantic` | 8 | **7** | yes |
| `arena_montane` | 8 | **7** | yes |
| `arena_lumberyard` | 8 | 5 | yes |
| `koth_harvest` | 8 | 0 | **no** |
| `ctf_2dfort` | 8 | 0 | **no** |
| `koth_valley` | 8 | 0 | **no** |
| `dkoth_atalia` | 8 | 0 | **no** |

⚠️ **Re-measured across all eight after the deathmatch goal landed: the four
zeros are unchanged and `arena_lumberyard` moved 4 → 5.** The zeros are the point
— they are gate geometry, not a bot failure, and no amount of better hunting will
move them. Against a **stationary** opponent the same eight maps read differently —
`arena_montane` 8 of 8 and `tdm_mantic` 5 of 8 — because that case depends on the
opponent hunting rather than on both sides wandering into each other.

⚠️ **SUPERSEDED for duels, 2026-08-28: `DuelSpec.MaxSeparation` fixes all four.**
Gate-filtered nav-node sampling places the pair on nodes each team may actually
stand on, and the three of these maps re-measured deal 1,009-1,615 damage where
spawn sampling dealt **zero**. The table stays because it is still the truth
about *spawn-point* placement, which is what a real respawn does. See "Sampled
placements that are actually in engagement range" above.

⚠️ **The four that never engage are not slow, they are unroutable.** Both sampled
spawns sit behind their own team gates, so a blue bot asked to walk to a red one
standing in the red spawn is correctly told there is no route
(`NavGraph.GatePassable`) — the follower's path length is **0** for the whole
episode and neither character leaves its spawn x. Use explicit positions outside
spawn on those maps, or an arena/tdm map.

⚠️ **This is a different experiment from the sampled-placement table above** and
the two do not contradict each other: that one measured random-head agents, this
one measures scripted bots.

### Protocol v3: the separation band, the rollout opponent, and a dropped `--nav`

**2026-08-28, all three requested by `gg2-rl`, which was blocked on every one of
them.** `MakeBatch` gains four fields at the **end** of its body — a
`RolloutOpponent` per side as `i32`, then a minimum and a maximum separation as
`f64`. ⚠️ **`ComponentBytes` does NOT move**, so unlike the v2 bump a client only
has to lengthen what it sends; every reply it already slices is byte-identical.

⚠️ **Two bumps in a day is a cost paid by somebody else**, so v3 carries both
open asks at once. The bump is not optional for a tail-append: a short frame
would read the new fields out of whatever followed it, and any non-zero maximum
separation moves placement off the spawn points entirely.

#### ⚠️⚠️ v3 shipped in `dbe8b15` with `Version` still at 2

The four fields went into the body, into `EnvServer.MakeBatch`, into both clients
and into this document. **The constant was never written.** The edit existed, was
reviewed, and was not run; the commit message, this file and the message to
`gg2-rl` then all asserted it had landed.

⚠️ **It inverted the handshake.** A client speaking the real layout sent
`Hello(3)` and was **refused**; a client speaking the stale one handshook
**successfully** and failed its first `MakeBatch` on the frame-length check —
with an error naming v2 as the thing it should be speaking, which it already was.
**The version exists so a mismatch is named at `Hello`.** A length check is a
backstop for a mis-framed client, not a substitute for a version.

⚠️ **Nothing here could have caught it, and that is the shape worth remembering.**
Every test in `EnvProtocolTests` builds its frames from the same constants the
server reads, so client and server agreed with each other about a layout the
version misdescribed — self-consistent, and false. Only a caller with an
independent notion of the version could see it, and one did: `gg2-rl` sent
`Hello(3)` and got refused. Same failure class as the `--nav` drop below, as
`docs/TRAPS-SIMULATION.md`'s arena claim, and as the sampled-placement row — **a
claim the repo makes about itself, that only somebody acting on it can falsify.**

The gate is `EnvProtocolTests.TheVersionAndTheFrameLayoutMoveTogether`, which
asserts `Version` and `MakeBatchFixedBytes` **together**, and
`EnvProtocol.MakeBatchFixedBytes` now lives beside `Version` so the body cannot
grow without somebody editing a line with the version on it.

⚠️ **The process lesson is smaller and blunter: a scripted edit that was written
is not a scripted edit that was run.** Verify the file, never the script's exit
code — three other edits in the same commit were grepped afterwards and this one
was not.

#### ⛔ `serve` parsed `--nav` and threw it away

`Program.cs` read `--nav` into a local and then built `new EnvServer(maps)`
without it. **Every served world therefore had a null nav graph and
`EnvDriver.Scripted` was refused in every batch** — correctly, and for a reason
the caller could do nothing about. `gg2-rl` could not run phase 1's exit
criterion in any form.

⚠️ **It is this document's own named failure class**: ENV.md documented a driver
the served env could not produce, every test upstream passed, and only a caller
who *acted* on the description could find out. The same shape as the sampled-
placement row below and as `docs/TRAPS-SIMULATION.md`'s arena claim.

⚠️ **And it survived because nothing could call it.** Top-level statements are
not callable from a test, `EnvServer`'s nav directory is an optional parameter so
dropping it compiles, and every existing test constructs `EnvServer` itself —
`EnvProtocolTests` included, which passes no nav directory and so could never
have noticed. The fix is therefore structural rather than a fresh pair of eyes:
the parse and the wiring both moved into **`EnvOptions`**, whose `CreateServer()`
is the one line that was wrong, and `EnvOptionsTests` drives it over a real
socket with a `Scripted` side. Re-drop the argument and that test reproduces the
exact refusal.

#### ✅ Sampled placements that are actually in engagement range

**The problem: no map satisfied phase 1's exit criterion.** Measured by
`gg2-rl`, `RolloutSearch` against a stationary `Idle`, 90 decisions:

| map | placement | result |
|---|---|---|
| `gg_debug` | explicit | 4 distinct actions, 16-0-0 |
| `arena_lumberyard` | sampled | **2** distinct actions, 0 damage, tick cap |
| `ctf_2dfort` | sampled | **2** distinct actions, 0 damage, tick cap |

That two-action signature is the horizon trap with distance as the cause instead
of the refire timer: no candidate can produce damage inside a 20-tick horizon at
spawn separation, every candidate scores exactly zero, the tie goes to the first,
and the bot stands still. `RolloutSearch` has no objective layer and no follower,
so nothing in it walks toward anybody — **it is not a search bug, it is out of
range.**

And the other half: **`gg_debug` has one spawn point per team**, so sampling
draws the same duel every time. Sixteen of them printed
`100.0% [70.7%, 100.0%] p=3.1e-05` off a sample size of one. The duels genuinely
happened; they were one duel counted sixteen times. ⚠️ **A map that gave
engagement gave no variety and a map that gave variety gave no engagement.**

`DuelSpec.MaxSeparation` is the answer, and it is a switch as well as a bound:
**with it set, placements are drawn from the nav graph's walkable nodes** rather
than from spawn points — gate-filtered per team, paired inside
`[MinSeparation, MaxSeparation]`, red drawn first out of the world's own
`GmlRandom` so a placement stays a pure function of the seed.

⚠️ **Gate filtering is the half that fixes the four zeros in the table below.**
A node whose gate a team may not pass is a node that team may not stand on, and
placing there is exactly what left `koth_harvest`, `ctf_2dfort`, `koth_valley`
and `dkoth_atalia` at 0 of 8 — the pair started behind their own team gates and
could not route to one another.

**Measured, two scripted tier-5 bots, 8 duels × 90 decisions, total damage:**

| map | banded 150-350px | spawn-sampled |
|---|---|---|
| `ctf_2dfort` | **1,615** | **0** |
| `arena_lumberyard` | **1,113** | **0** |
| `koth_harvest` | **1,009** | **0** |

and on `gg_debug` over 24 seeds: **24 distinct placements banded, 1 spawn-
sampled.** Both halves, on the maps that had neither.

⚠️ **Refused rather than downgraded, in three ways**, all on the argument the
half-NaN rule already makes — answering a request with the exact behaviour it
asked to replace is worse than answering it with an error. A band needs a nav
graph; a band needs **both** placements sampled; a minimum without a maximum does
not switch banding on and says so.

⚠️ **Straight-line distance, not path length.** What "in range" means here is
what a weapon and a rollout horizon reach, which is a line. A path metric would
call two agents either side of a thin wall 0px apart.

#### ⚠️ The rollout opponent is selectable — and the MAP decides whether it matters

`DuelEnv.MakeBot` answered `EnvDriver.Rollout` with `new RolloutSearch()`, so no
batch could choose the opponent model. `DuelSpec.RedRolloutOpponent` /
`BlueRolloutOpponent` now carry it. ⚠️ **Only the opponent model is exposed, not
the whole `RolloutOptions`** — the horizon and the aim ladder are the search's own
tuning, and moving them onto the wire would make "the search baseline" a different
thing per batch.

⚠️⚠️ **AND `new RolloutSearch()` was searching against the OPPOSITE of its
documented default.** `RolloutOpponent` declared `Idle` first, so `Idle` was zero,
so `default(RolloutOptions).Opponent` was `Idle` — and `new RolloutSearch()` takes
`default`. Every caller that took the documented default got the floor instead,
this repo's own "11-0-1 against tier 5" figure included. It is precisely the trap
`RolloutOptions.WithDefaults` exists for, and it caught two of the three fields;
an enum cannot be fixed there at all, because "unset" and "explicitly Idle" are
the same zero. **The fix is the declaration order, so the order is load-bearing:
the default member of an enum that rides in a `default`-able struct must be the
zero one.**

⚠️ **Nothing pinned moved when that was fixed**, and that is the more interesting
finding. Measured over 36 scenarios — four classes × three maps × three seeds —
the two opponent models chose **identical action sequences in 26 of them**:

| map | scenarios | diverged |
|---|---|---|
| `ctf_2dfort` | 12 | **9** (up to 18 of 21 decisions) |
| `arena_lumberyard` | 12 | 1 |
| `gg_debug` | 12 | **0** |

`gg_debug` diverged zero times at every separation tried, against both a scripted
and a rollout opponent, even with the opponent pressing keys on 34 of 37
decisions. The opponent's damage is very nearly a constant across candidates, so
it cancels in the argmax unless the geometry gives a candidate somewhere to be
that the opponent's fire does not reach — and a flat one-room map does not.
⚠️ **So an experiment that reports "the opponent model changes nothing" from
`gg_debug` has measured the map.** Run that comparison on `ctf_2dfort`.

### ⚠️ `SIM_UUID` does NOT cover the bot layer, and that bit a consumer

**2026-08-29, found by `gg2-rl`.** The stamp covers the simulation and the `NAV_*`
block — "a policy trained against different physics is not loadable". **It does
not cover `RolloutSearch`**, which lives in `Gg2.Bots`.

⚠️ So the aim tie-break (`2d810c3`) **changed what the labels in a distillation
corpus ARE**, while leaving every field of that lane's dataset identity untouched:
`SIM_UUID`, encoding version, layout version, head shape and cadence were all
unmoved. **A corpus from the old expert and one from the new would have pooled
silently and trained a policy on two different teachers.**

⚠️ **It is not obviously right to fold the bot layer into `SIM_UUID`.** The stamp's
job is "these weights cannot be loaded against this simulation", and a trained
policy does not care what generated its labels once it exists — so folding the
expert in would invalidate checkpoints that are perfectly valid. The scope is
correct; what was missing was anyone saying so out loud.

✅ **`gg2-rl`'s fix is to key its dataset identity on this repo's COMMIT**, which
over-refuses — a docs commit invalidates a corpus — and cannot drift. ⚠️ **That is
the better mechanism on this session's own evidence**: a hand-maintained version
constant is exactly what shipped as `Version = 2` while the wire carried v3, and
what advertised five action heads while sending six. **A number a human has to
remember to bump is the weaker half of every pairing tried today.**

⬅️ **Open, and deliberately not built**: the server could report its own build
revision in the handshake, so a consumer keys on something it is *told* rather
than something it tracks externally. That is real value and it is also more build
plumbing (`SourceRevisionId`) than the problem currently justifies, given the
consumer already has a working answer.

### ✅ BUILT 2026-08-30: the third hand-bumped number, and what it does not fix

**Asked for by `gg2-rl` after `3bba987`**, the `cp_dirtbowl` hang fix, which
changed control flow in `Movement.cs` and moved **no** field of that lane's
dataset identity. The section above is about the bot layer falling outside the
stamp; this is the simulation's own behaviour falling outside it. **Every other
entry on the surface is a constant**, so the stamp could express a changed
gravity and could not express a changed line.

`SimulationStamp.SimulationBehaviourVersion`, on the surface as `behaviour=`.
`SIM_UUID` `c526b96e34184138` → **`7ff5940d97aa7bed`**.

- **Bump it** when you knowingly change what the simulation does: a trajectory, a
  damage number, a projectile path, a stage's position in `MatchSimulation`.
- **Do not** bump it for a refactor, a rename, a comment or a performance change.
- ⚠️ **Exempt a change only when it is DEMONSTRATED unobservable.** `3bba987` is
  exempt because two independent 90-iteration training runs reproduced their
  per-iteration numbers to the digit across 1.66M agent-steps — not because the
  argument for it was good. This project's own rule: reasoning is not checking.

⛔ **The first bump that was not free.** `gg2-rl` holds checkpoints, two
600-iteration runs among them, and every one of them is refused until re-stamped.
That cost was named in the ask and accepted before the work rather than
discovered after it.

⚠️ **And it is a hand-bumped number, which the section above calls "the weaker
half of every pairing tried today". That judgement still stands and was not
quietly dropped.** It is here anyway because the three options are not
interchangeable:

| mechanism | refuses? | fails how |
|---|---|---|
| a swept digest over the code | yes | churns on a comment, a refactor, a compiler upgrade — and a stamp that churns gets ignored |
| the consumer keying on this repo's commit | **no** — it labels a corpus, it does not refuse a load | over-refuses, cannot drift, and is the strongest of the three where it applies |
| `SimulationBehaviourVersion` | yes, through `Require` | somebody forgets |

**So it is the forgettable option inside the refusing mechanism, and the reliable
option is outside it.** ⬅️ The thing that would dominate both is still open and
still not built: the server reporting its own build revision in the handshake, so
a consumer keys on something it is *told*. Until then, ⚠️ **the commit SHA beside
every number remains the real guard, and this number is a second line rather than
a replacement.**

### ✅ BUILT 2026-08-29: the shadow search slot

**Asked for by `gg2-rl` 2026-08-29, deferred by the user pending a cheaper
experiment.** Recorded here so the decision has a design attached rather than
being re-derived.

**The problem it solves, measured.** Phase 2's first cloned policy wins **12.5%
[10.5%, 14.8%]** where the search wins **76.9%**, against a target of 69.2% —
89,454 rows, held-out exact match 0.422. Three explanations were tested and
eliminated:

| ruled out | evidence |
|---|---|
| capacity | wider is **worse** held-out: 0.422 at hidden 64, 0.401 at 256, 0.378 at 512 |
| an uninformative observation | 1-NN over 71,942 rows scores **0.300** against the network's 0.422, so it generalises past a memorised lookup — `ObservationWriter` is not the bottleneck |
| failure to learn the basics | the policy beats `EnvDriver.Idle` **82.4%** as a soldier and **100%** as a scout, and scores 5.0% / 5.2% against a scripted tier 5 |

⚠️ **It can aim and fire, and collapses only against something that reacts.** That
is **❌ NOT compounding error — see the result below.** The story was: one wrong
action reaches a state the search never visited,
the next prediction is worse there, and it snowballs. ✅ `EnvDriver.Idle` earned
its keep exactly as its own remark predicts — it is what separates "has not
learned to shoot" from "cannot recover".

⚠️ **One detail worth keeping**: the cloned soldier loses **6 of 60** duels to an
opponent that never fires — self-inflicted splash. The search respects its own
blast radius through `Rollout`'s damage-taken-at-equal-weight term and the clone
learned that imperfectly. **Splash safety is one of the harder things to distil**,
and it is a distinct failure from being outfought.

#### The design, and the ordering question is already answered

A slot the **client** drives while `RolloutSearch` still runs and reports what it
*would* have done — DAgger's aggregation step, and `gg2-rl/docs/DESIGN.md`'s own
step 3. The policy plays, the search grades, and the next training round sees the
states the policy actually visits.

- `DuelSpec` gains a per-side shadow flag. The search is **constructed but not
  installed as the input source**.
- ✅ **Polled after the action repeat, before `Observations` is written** — which
  labels *exactly* the observation the reply carries. `gg2-rl` flagged this as
  possibly awkward; it is not. `EnvBatch.Step` runs the whole repeat and then
  `EnvServer.Observations` reads the post-step world, so "the search's opinion of
  the state as the policy left it" is the **natural** ordering rather than a
  contortion.
- Reported in the existing **opt-in `StepScored`** block, extended per agent with
  the argmax action — ⚠️ **sent explicitly rather than left to be derived from the
  distribution's index**, because recovering it otherwise needs a model of
  `RolloutSearch`'s candidate enumeration inside the trainer, which is the thing
  the whole seam exists to avoid.
- Protocol v7.

⚠️ **Cost: a labelling batch costs what a `Rollout` batch costs** (~2.9 ms a
decision, Release), because the search runs on a slot that is not using it. Opt-in
only; never a default.

#### ⚠️ The deferral's premise was half wrong, and is now repaired

The shadow slot was deferred pending `gg2-rl`'s soft-targets run — **and it could
not run one.** `StepScored` sent scores aligned with a candidate list whose
**actions the env never sent**, so turning an index into an action meant
reconstructing `RolloutSearch`'s enumeration inside the trainer. What was
buildable was **margin weighting** (best minus runner-up as a per-row weight),
which attacks part of the problem and is strictly weaker than matching a
distribution over actions.

⚠️ **This repo made exactly that argument one commit earlier** — the shadow
slot's argmax "sent explicitly rather than left to be derived from the
distribution's index, because recovering it otherwise needs a model of
`RolloutSearch`'s candidate enumeration inside the trainer" — **and did not notice
it applied with equal force to the whole distribution.** `gg2-rl` did.

✅ **`EnvMessage.Candidates` closes it**: red's set then blue's, each a count and
six i32 an action. Asked **once per batch**, not per step — a duel's classes are
fixed by its spec — so every existing reply stays byte-identical and no version
moves. A non-rollout side reports an explicit zero.

⚠️ **The gate asserts ALIGNMENT, not presence**: the action at the argmax of the
distribution must equal the action the reward block says was taken. A table that
came back in a different order would train a policy towards the wrong actions and
nothing about it would look wrong. Mutation-checked by reversing it.

**The case for it, from the first scoring decision `gg2-rl` inspected**: 60
candidates, best **15.2 at index 6**, runner-up **15.2 at index 7**, margin
**0.0**. Two candidates the search considered exactly equal, one recorded as the
label, and a policy marked wrong for choosing the other. **A one-hot is a lie on
every such row.**

#### ⛔ THE ANSWER: expert iteration made the policy WORSE, by 7.2 points

**2026-08-29.** 29,443 labelled states — **ten times** the agreed "few thousand" —
aggregated with a 69,998-row control from the same expert build:

| corpus | optimal-set | win |
|---|---|---|
| control alone (search-driven states) | 0.616 | **19.4%** |
| control + policy states | 0.560 | **12.2%** |
| policy states alone | 0.483 | — |

**Not "less than +5". The wrong direction.** ✅ **The stopping rule agreed before
the build therefore applies**: behaviour cloning of this search does not reach
75.8%, and self-play initialised from the distilled policy is the path rather
than a refinement.

⚠️ **The feature was checked first and is not the problem.** Every shadow label
lies inside the tied-best candidate set, verified across the socket against the
distribution and the candidate table; the pairing is the one specified here; and
the blind fraction on shadow data is 75.0% at scale against 78% search-driven, so
a driving policy does give the search more to score.

##### ⚠️ A BETTER IMITATOR THAT PLAYS WORSE — and a correction to this section

⚠️ **This section first said "the policy is not off-distribution", on the strength
of a distance figure that turned out to be raw Euclidean over the unnormalised
209-float observation.** That measure weights every column equally and a policy
does not. **It was wrong, and so was the correction built on it.**

**The operational measure** — label a state by its nearest *control* state and see
how often that is right:

| | exact |
|---|---|
| control → control | 0.204 |
| dagger → control | **0.074** (36% retained) |

**The policy-visited states ARE meaningfully uncovered.** ✅ And covering them
helps, exactly as the off-distribution story predicts:

| trained on | control held-out | dagger held-out |
|---|---|---|
| control only | 0.616 | 0.422 |
| dagger only | 0.539 | 0.483 |
| **both** | **0.624** | **0.503** |

The aggregate is **strictly the best imitator on both held-out sets** — better on
control states than the control-only policy, better on dagger states than the
dagger-only one.

⛔⛔ **AND IT WINS 12.2% WHERE THE CONTROL WINS 19.4%, intervals not overlapping.
A strictly better imitator plays strictly worse.**

##### ⛔ THE REAL FINDING: the optimised quantity has come apart from the judged one

Soft targets improved agreement **and** win rate. Expert iteration improved
agreement on **every** held-out set and cost **7.2 points** of win rate. **Two
changes that both improved the optimised quantity moved the objective in opposite
directions**, so **imitation metrics cannot steer this phase at all.**

That is a stronger reason for the same conclusion: cloning does not reach 75.8%,
and it is not merely that the levers are small — it is that the thing distillation
optimises is not the thing phase 2 is judged on.

###### ⚠️ OPEN, 2026-08-30: every win rate above is ONE training run a side

`gg2-rl` raised this and it is right in structure, so it is recorded next to the
numbers rather than in a handoff. **A duel-count Wilson interval measures
evaluation noise. The object being compared here is a training PROCEDURE, and each
one was sampled once.** "Intervals not overlapping" was the wrong bar: it was
never the training seed's interval.

**What was measured, and where** — its phase 3, PPO, and reported here at its
figure: two runs differing **only** in the seed landed **3.9 points** apart on
trained maps and **7.2** across three, against a 1,600-duel Wilson interval of
about **±3**. Two of that lane's A/Bs decided on disjoint intervals failed to
replicate, and one — `entropy_coef` — **reversed sign** at a second seed.

⚠️ **The magnitude does not transfer and is not claimed to.** Those are PPO runs;
`19.4%` and `12.2%` come from *supervised* distillation on a fixed corpus, whose
seed variance is plausibly smaller and **has never been measured**. What transfers
exactly is the structure: nobody varied the seed on either side, so the spread is
unknown rather than small.

**What survives untouched, and what does not:**

- ✅ **"Cloning does not reach 75.8%" stands.** The gap is ~56 points against a
  seed spread that would have to be an order of magnitude past anything measured
  to matter. The stopping rule was agreed on that margin, not on this one.
- ⚠️ **"Expert iteration is −7.2" and the REAL FINDING above are single-seed
  claims**, and the REAL FINDING needs the *sign* of two deltas of about 4-7
  points each to hold. It is the better-supported reading of the evidence, not a
  settled result, and it should not harden into one by being repeated.
- ⬅️ **The cheap check that would settle it**: re-run each distillation at a
  second seed and see whether the ordering survives. Supervised training on a
  corpus that already exists — hours, not days, and no new machinery.

⚠️ **This is not a retraction and nothing above was deleted.** The distinction
worth keeping is between a conclusion that survives the noise by fifty points and
one that lives inside it.

⚠️ **What still stands from the earlier text**: leaving the expert's state space is
**not** why the policy loses, since fixing coverage made it a better imitator and a
worse player. The `EnvDriver.Idle` result also stands — 82.4% / 100% against a
statue, ~5% against a reactive opponent.

⚠️ **No mechanism is claimed.** There is a plausible one, recorded as
**unmeasured** so it cannot later be mistaken for a finding: the policy takes a
**per-head argmax**, and a joint action assembled from six independently-argmaxed
marginals need not be the action the expert would have played.
✅ **Narrowed by a fact about this repo**: `RolloutSearch.BuildCandidates` is a
**full cross product** over horizontal × jump × fire × aim × zoom with `Down`
pinned to 0 — so a per-head argmax **is always a valid candidate**, and the
hypothesis is not "an action the expert could not play". What a factored policy
cannot represent is the **coupling**: the expert's choice among tied candidates
uses the aim tie-break, which ties the aim head to the others through a simulated
outcome. Independent heads cannot express that. **Testable, untested.**

##### The four levers, all against the same 56-point gap

| lever | effect |
|---|---|
| more data (2.8× rows) | +3 exact-match |
| soft targets | +4 to +5 win |
| a better expert (+7.3 to it) | +6 win |
| **expert iteration** | **−7.2 win** |

##### ⚠️ The one untested variant, named rather than implied

This was **β = 0** expert iteration: pure policy rollouts from a 19.4% policy. The
classical formulation mixes expert and policy actions early, precisely because a
weak policy visits unhelpful states. ⚠️ **Do not run it — and for the OPPOSITE reason to the one first recorded here.**
The first argument was "a schedule fixes coverage and coverage is not the
problem". That was wrong twice over: coverage **was** a problem, and fixing it
**did** work on the imitation metric — it simply did not improve play. **A
schedule buys more of the thing that did not help.**

#### ✅ Built as a decisive experiment, with the stopping rule agreed first

**Protocol v7.** `DuelSpec.RedShadowSearch` / `BlueShadowSearch`; the search is
constructed but **never installed as an input source**, polled after the action
repeat and before observations, reported through the opt-in `StepScored` block —
which now also carries **the action the search chose**, six i32 after the
distribution. ⚠️ Not derivable from the scores: a median of four candidates share
the best and the search picks among them on aim error.

⚠️ **`DecisionTicks: 1` for a shadow**, so it re-searches on every poll. The
default cadence of 4 would repeat the previous answer for three of every four rows
and label them as fresh — the duplicate-row problem from the other direction.

⚠️ **Refused on a non-Client side** rather than doubled: it would run a second
search whose answer the first already reports, at double the cost, silently.

⚠️ **THE STOPPING RULE, agreed before building**: a few thousand labelled states,
one retrain, one evaluation. **If it does not beat the +5 that better labels
bought, the honest conclusion is that behaviour cloning of this search does not
reach the target and phase 3's self-play is the path.** Three levers have each
given single digits against a 56-point gap — more data (+3), better labels (+4 to
+5), a better expert (+6, and the target rose as fast). State coverage is the last
untested one, by elimination rather than by theory.

#### ⬅️ The deferral, and why it ended

`gg2-rl` is training on the `StepScored` distribution instead of a one-hot on the
argmax — **entirely its own work, next on its list regardless**, and now actually
buildable. That attacks the
same 0.422 exact-match number from the other end: a near-miss stops being scored
as a total miss. **Measuring that first says how much the shadow slot actually
buys**, and it costs nothing but a run that was happening anyway.

⚠️ This is the same discipline that killed `RolloutOptions.CommitTicks` and the
horizon-45 claim on the same day: **measure before building**, especially when the
thing to be built is a protocol version.

#### ⚠️ The starting facing was in the decoder only, and is now on the character

`Reset` arranges for both agents to start facing one another, because facing is
observable — it is what the two-pixel box inflation splash is measured against.
Until 2026-08-28 that facing was written into the `ActionDecoder` and **not onto
the characters**, so:

- **the first observation of every episode reported the spawn's facing** (due
  east) rather than the arranged one, and aim sin/cos is in the layout;
- a client-driven slot hid it, because `Decode` writes an aim every tick and the
  character caught up on tick one;
- a bot-driven slot did not, because a bot supplies its own aim.

Found by a driver test whose idle opponent came back having apparently made a
180° turn nobody asked for. ⚠️ **`PlayerInput.None` is not "no aim" — it is "aim
at zero degrees"**, which is why `Idle` reads the character's own bearing back
rather than using it.

## Action encoding v0 (stamped)

Per `gg2-rl/docs/DESIGN.md`:

| Head | Values |
|---|---|
| Horizontal | left, right, none |
| Jump | press, none |
| Down | hold, none |
| Fire | none, attack, special |
| Aim delta | 15 signed bins over the clipped turn rate |

- **Jump "press" emits the bit for exactly one tick** — `KEY_JUMP` is
  edge-triggered in the reference's Begin Step, so a held bit does nothing
  after the first tick, and under action repeat the decoder sets it on the
  repeat's first tick only.
- **The mask is written absolutely every tick**, never OR-ed — the same trap
  CLAUDE.md records for `gg2_watch` driving.
- Aim: the decoder tracks an angle per slot, applies the capped delta, and
  quantises through `PlayerInput.FromAim` — the bot aims on the same 65,536
  steps a client does (`docs/BOTAI.md` item 4).
- `aimDistance` is a fixed constant in v0; the heads do not carry it. A change
  is an encoding bump.
- The turn-rate cap's value is a v0 constant, calibrated properly in `gg2-rl`
  phase 5; changing it bumps the encoding version.

## Observation layout v0 (stamped)

Egocentric, normalised, fixed-size `float32`. The authoritative field order is
`ObservationWriter`; this is the shape:

| Block | Contents |
|---|---|
| Self | class one-hot (9), hp fraction, velocity, grounded, aim sin/cos, ammo fraction, reload fraction, special-resource fraction |
| Enemies, nearest 2 | presence, relative position and velocity, class one-hot, hp fraction, aim sin/cos, time-since-seen (fixed 0 in v0 — the reaction-delay ring buffer is `gg2-rl` phase 5; the field exists now so the layout does not churn then) |
| Projectiles, nearest 4 | presence, relative position and velocity, kind one-hot, mine/enemy flag — creation-order tie-break via `ProjectilesWithin` |
| Geometry | 24-ray fan of normalised distances plus an 8×8 downsampled walkmask patch centred on self |
| Macro goal | direction sin/cos, distance, goal kind one-hot — **settable per slot since 2026-08-30** (`AgentGoal`, `EnvMessage.SetGoal`); the default is the "engage the nearest enemy" this always carried |

Extraction allocates nothing and never enumerates the whole world
(`docs/BOTAI.md` item 6 — `NearestEnemies` / `ProjectilesWithin` are the
queries). Every count above is v0-tunable; a change is a layout bump.

## Reward components (per world, per step)

`damageDealt` (f32), `damageTaken` (f32), `kills` (u8), `deaths` (u8),
`termination` (u8: running / death / tick cap). Accumulated over the action
repeat's k ticks. The jitter penalty stays trainer-side — it is a function of
the actions, which the trainer already has.

## ⚠️ `gg2-rl` reads this repo's SOURCE as data, so moving a constant crosses the seam

**Learned 2026-09-01 when `6f75f5f` turned its drift gate red — correctly, on a
change that was an improvement.**

That commit moved `HeadShape` out of a literal in `EnvProtocol.cs` into
`ActionDecoder.HeadShape`, so a policy in `Gg2.Bots` could reach the one
definition instead of a second copy. **No value changed**, the runtime handshake
still reports `(3,2,2,3,15,2)`, and the literal no longer sits apart from the
thing it describes. `gg2-rl`'s gate reads these files as **text** — deliberately,
so that it cannot agree with this repo by construction — and a relocation is
indistinguishable from a redefinition to a reader that only has the text.

⚠️ **So a pure refactor of anything `gg2-rl` parses is a cross-repo event.** Moving,
renaming or re-spelling one is free here and red there. **Say so when it happens**
rather than letting them find it — the gate fires loudly, which is the point of
it, but the ten minutes belong to whoever caused it.

⛔ **The full list, as of 2026-09-01 — and a flagging promise built on a partial
one silently does not cover the rest.** I had named two; there are eight patterns
across six files:

| file | what is parsed |
|---|---|
| `Gg2.Env/EnvProtocol.cs` | `Version`, `ComponentBytes`, `ActionBytes`, `MakeBatchFixedBytes` (tolerates a sum) |
| `Gg2.Env/EnvServer.cs` | ⚠️ the **ORDER** of the reward block's `Write*LittleEndian` calls |
| `Gg2.Env/DuelEnv.cs` | `enum EnvDriver` — names **and** values |
| `Gg2.Bots/RolloutSearch.cs` | `enum RolloutOpponent` — names, **positionally** |
| `Gg2.Bots/ActionDecoder.cs` | `HeadShape => [ … ]` — counted, not evaluated |
| `Gg2.Protocol/Team.cs` | `enum PlayerClass` — names **and** values |

⚠️ **Three are more fragile than a constant:**

- **`EnvServer.cs` is the worst.** It reads the *order* of the serialisation calls
  to check component offsets against the sequence actually written. **Reordering
  those lines is a real change and the gate is right to fire; reformatting them is
  not, and the gate cannot tell the difference.** A formatter run over that method
  will go red.
- **`EnvDriver` and `RolloutOpponent` are indentation-anchored** (`^\s{4}`). A
  brace-style or indentation change fires them with no semantic change at all.
- ✅ **`RolloutOpponent` was positional and is now explicit.** Its declaration
  order *was* the wire value, so reordering its two members would have been a
  silent wire change that compiles and runs — the same class as the
  `Idle`-declared-first bug this seam already hit once. `HoldLastInput = 0` and
  `Idle = 1` are written down now, so there is nothing left to catch.

⚠️ **The list is `gg2-rl`'s to keep current, not mine** — a gate added and not
announced is one my flagging cannot cover. They send a diff when it changes.

⚠️ **And flagging is a courtesy on top of a mechanism, never a substitute.** Their
words, and they are right: *"if the only thing standing between us and a silent
wire change is you remembering to mention it, the gate has stopped being the
safety net and become a formality."* Every red stays theirs to investigate —
"that will just be their refactor again" is exactly how a real one gets waved
through.

✅ **And the cost is the gate working.** A checker that followed a refactor
automatically would follow a real change automatically too. `gg2-rl` recorded the
matching hazard on its side: **if the pattern ever stops matching, do not relax
it — go and find where the thing moved to.** A gate that stops finding what it
checks passes by looking at nothing, which is the vacuous-assertion shape from the
other direction — failing open, but only until somebody loosens the regex to get
green.

## ⛔ `SIM_UUID` is blind to the bot layer, and that is the layer the opponents live in

**Established 2026-09-01, by `gg2-rl` checking something it could have assumed.**

The stamp covers physics constants, the class tables, the action encoding, the
observation layout and a hand-bumped behaviour version. **It does not cover
`Gg2.Bots`.** That is correct — a bot is not observable simulation, and this
project's bots are its own — but it has a consequence nobody had written down:

⚠️ **Every opponent in every win rate `gg2-rl` has ever reported is a scripted bot
running `BotObjective`.** So a change confined to bot decision-making moves no
stamp, invalidates no checkpoint, and **changes the thing every historical number
was measured against**. The fingerprint cannot see it: placement and stepped
motion come back identical because they *are* identical.

When `BotObjective` changed for dual KotH, `gg2-rl` re-scored its checkpoint
rather than trusting the fingerprint, and got **49.3% / 48.4% to the decimal** —
its recorded numbers. The fix was mode-gated and duels carry no gamemode, so
neutrality was expected; **the point is that it was established rather than
assumed, and the fingerprint would have said "identical" either way.**

✅ **The rule both repos now hold**: when the bot layer moves, re-score rather
than fingerprint. A digest that cannot go red for the change you made is not
evidence about that change.

## The stamp

Obs layout version and action encoding version fold into `SimulationStamp`, as
`docs/BOTAI.md` item 9 always said they would. ⚠️ **That changes `Uuid`, and
the pinned digest in `SimulationStampTests` is updated as the bump, not fixed
as a broken test.** No checkpoints exist yet, so this bump is free — the reason
to do it now rather than after `gg2-rl` phase 2.

## Gates

- Batch-size independence: identical trajectories at batch 1 and batch 64 with
  the same seeds, compared whole, digest included.
- Thread-count independence, same comparison.
- Snapshot → restore → continue equals the uninterrupted control, compared at
  the end (the item-2 rule: restoring is not the claim; continuing is).
- `ActionDecoder`: jump edge behaviour, absolute mask writes, aim cap and
  quantisation.
- `ObservationWriter`: zero allocation, bounded values, layout length matches
  the advertised one.
- Damage accumulators: attribution, the self-damage rule, invulnerable-victim
  accumulates nothing.
- Protocol round-trip against a real socket, including the `Hello` refuse.
- **Per-slot drivers** (2026-08-28): a bot slot returns real reward components
  and the action it took; two scripted opponents fight **to a death**, not to a
  tick cap; a scripted driver without a nav graph is refused; `Snapshot` is
  refused with a bot in play; hunting an unreachable opponent does not defeat the
  re-plan throttle; a scripted opponent hunts down and kills a stationary agent; a
  bot-driven batch is identical at batch 1 and 32 and on 1, 2 and 4 threads.
  ⚠️ **Each of those has its premise asserted** — that a bot really is behind the
  slot — because a client slot fed `EnvAction.Idle` behaves identically and made
  four of them pass with the driver ignored entirely, which is what the mutation
  check found.

## Exit (v0)

1. An in-repo random-head agent completes 1,000 duels over the real socket
   protocol: no crash, no hang, every episode terminates within its tick cap.
2. Every gate above is green.
3. Throughput measured and recorded **here** — decision steps per second at
   batch 1 and batch 64, naming the build configuration. `gg2-rl` phase 0
   stops the whole programme below ~20k steps/sec, so this number is the one
   that decides whether shared memory ever gets built.

## Out of scope, so nobody re-opens it

⛔ **Four of the five items below are HISTORICAL as of 2026-09-01, and the
heading is the trap.** "Out of scope, so nobody re-opens it" reads as a standing
refusal; four of these were phase boundaries that have since been crossed, on the
user's explicit call each time. **A list like this decays into a lie the moment
the programme advances past it, and nothing in the repo goes red when it does** —
the failure mode `CLAUDE.md` names. Struck through rather than deleted, because
the reasons they were out of scope are still the reasons to be careful:

| item | state |
|---|---|
| **Serialising the bot layer** — what `Snapshot` with a bot-driven slot would need | ✅ **still refused**, and this is the one real entry |
| ~~No ML runtime, no inference path, no weights format~~ | ⛔ **built** — `NpyFile`, `PolicyNetwork`, `PolicyBot`, gated logit-for-logit against `gg2-rl`'s numpy |
| ~~No rollout-search bot~~ | ⛔ **built** — `RolloutSearch`, with candidate pruning |
| ~~No full-match episodes~~ | ⛔ **built** — `MatchRunner`, 21 maps × 400 matches × two arms |
| ~~No reaction-delay model~~ | **still true** — only its observation field exists |

⚠️ **The refusal that stands is the first one, and it stands for a reason that has
not changed**: a `Snapshot` that had to serialise a bot's own `GmlRandom`, its
path, its target and its objective state would make every one of those a wire
format, and the bot layer is the part of this repo that is explicitly allowed to
change freely. `docs/TRAPS-BOTS.md` has the argument.

### ⛔ Shared memory: the refusal stands, and the CRITERION was wrong

This document said "no shared memory until **the throughput number** above
justifies it". **Throughput was never going to justify it and never could.**
34,073 steps/sec cleared the floor on day one and the transport has not been the
bound for a moment since; measuring it harder would only ever have re-confirmed
that.

⚠️ **The criterion that replaces it**, from the 2026-08-31 profile:
`gg2-rl`'s collect loop is **69% GIL-bound** — 2.86x overlap against a 9.09x
ceiling — and its PPO update is 36% of an iteration and entirely serial. Shared
memory would be feeding a bottleneck that is not the transport. **The honest
justification is a profile taken AFTER that work showing the loop transport-bound**,
and `gg2-rl` has said it will bring one if that is how it lands.

⚠️ **And if it ever is justified, the lever is round TRIPS, not their cost.**
Latency per trip is already 50.7 µs at batch 1; the win would come from doing
more per exchange, and shared memory is one way of many.

⚠️ **RE-CHECK THIS GATE BEFORE QUOTING IT.** It rests on `gg2-rl` being GIL-bound
in `collect` and serial in its PPO update, measured 2026-08-31. **If that work has
landed, this criterion is stale and the answer may have changed** — so the thing
to do is ask for a current profile, not repeat this paragraph.

⚠️ **That last line is the device, and it is worth more than the criterion it
guards.** `gg2-rl` supplied it: the entry in its own `CLAUDE.md` that said "the
boundary is the cost, not the maths" — which is why numpy went unexamined for
three phases — was caught today **only because whoever last touched it had written
"re-measure before repeating this sentence" into the claim**. It was the third
entry-point sentence in that repo to outlive its measurement, and the only one of
the three that carried its own falsification condition was the only one caught
before somebody acted on it. **The others were code-right, prose-stale, invisible
to every test, and findable only by a reader who acted on them.**

⚠️ **So: a claim that would be expensive to act on wrongly should say what would
show it has gone stale.** It is not a test and cannot be one — this is exactly the
class `CLAUDE.md` calls "the failure mode no test can catch" — but it survives the
person who wrote it, which a good intention does not.

## ⛔ Where the training time actually goes — measured from outside, 2026-08-31

**Read this before optimising anything in this project for `gg2-rl`'s sake.**

Two `phase3.py` runs were observed live for 62 seconds, from outside both repos,
by sampling process CPU counters:

| | CPU |
|---|---|
| 16 `gg2-env` workers | **0.67 cores total — 4.2% of one core each** |
| 2 Python trainers | 1.41 cores |
| **the whole machine** | **2.08 of 24 logical cores — 8.7% busy** |

⛔ **The machine is 91% idle while training, and the env is 4% of it.** Per run:
Python is busy about 70% of the wall clock, the env workers about 4%, and the
rest is neither — round trips, blocking, scheduling.

⚠️ **So making this simulation COMPUTE faster cannot move `gg2-rl`'s iteration
time by more than a few percent.** The 3.67x of 2026-08-30 is real and the
numbers behind it are honest; what it bought the *trainer* is bounded by a duty
cycle of 4%. An env that computed infinitely fast would save that 4% and nothing
else.

⛔ **But "the env is 4% of an iteration" is the wrong sentence and this document
said it.** 4% is a **CPU** share. **A worker blocked on a round trip burns 0% CPU
and sits on the trainer's critical path for the whole time it is blocked**, so
the env's *occupancy* of that path is larger — possibly much larger — and this
measurement cannot see it. `gg2-rl` expects its own wall-clock figure to come out
far above 4% and that will **not** be a disagreement; it is the reconciliation:

| | |
|---|---|
| this 4% | the env is not **computing** much |
| `gg2-rl`'s share | the env is what its workers are **waiting for** |

⚠️ **Which is exactly why latency is the right target and throughput was not.**
You cannot make waiting faster by making the computation faster when the
computation is not the wait. Both halves of that follow from the same 96%-idle
number, and the first round of work read only the half that flattered a
throughput benchmark.

⚠️ **This is the check that the previous round of work did not do**, and it is a
cheaper check than any of the optimising was: a single 60-second CPU sample,
requiring no instrumentation in either repo and no cooperation from the trainer.
**Measure the consumer's duty cycle before optimising the producer.** A
throughput benchmark on an idle box answers "how fast can this go", which is a
different question from "what is anybody waiting for".

⚠️ **The corollary for latency.** A worker that is idle 96% of the time is
waiting, so what it costs the trainer is *per-round-trip latency*, not
throughput — and those are different numbers here. That is why the boundary was
re-measured as latency (below) rather than trusted from the steps/sec table.

⚠️ **And one thing worth knowing about how the pool runs**: the workers are
restarted periodically — 32 distinct PIDs over 30 seconds with 16 alive. That is
`gg2-rl`'s `rotate_every=10`, **deliberate rather than a leak**: it tears the pool
down every ten iterations and rebuilds it, which is what buys matchup coverage.
Bring-up is **~92 ms** per worker (33 ms spawn to serving, 15 ms connect and
handshake, 43 ms `MakeBatch` at 16 worlds), and in a cold process the load path is
JIT-dominated: `LevelMap.FromPng` reads **11.4 ms** cold against 0.28 ms warm,
`NavCache.TryLoad` **18.2 ms** against 4.8 ms. Amortised over ten iterations it is
about 0.4%, so it does not matter — but `ReadyToRun` is the lever if that interval
is ever shortened, rather than anything algorithmic.

### ✅ Reconciled with `gg2-rl`'s own profile — and it is the GIL

**Their measurement, idle machine, `gg2-server` 3bd0c5a, 34 iterations, 10
discarded, medians, both arms** — a sequential control as well as the concurrent
run, because a single arm cannot be interpreted:

| | sequential | concurrent |
|---|---|---|
| iteration | 982.1 ms | 771.6 ms |
| collect | 683.7 ms | 494.4 ms |
| PPO update | 298.4 ms | 277.2 ms — **36%, single-threaded** |
| `step()` wall, summed | 414.4 ms | **1412.4 ms** |
| `step()` wall, max | 414.4 ms | 107.3 ms |

Concurrency buys **1.27x**. Overlap reaches **2.86x against a ceiling of 9.09x**,
so **69% of the available parallelism is lost to the GIL**.

⛔ **The number that reconciles the two measurements is `step()` wall going
414 → 1412 ms — 3.4x — for the same work.** The env did not get 3.4x slower.
`step()` wall is socket **plus this server's compute plus the calling thread
reacquiring the GIL**, and with fifteen threads the third term dominates.

So both figures were right and they are the same system:

| | |
|---|---|
| the 4% CPU above | the env is not **computing** much |
| `gg2-rl`'s occupancy | what its workers are **waiting for** |

⚠️ **And a large part of that occupancy is not this repo at all** — it is their
own threads queueing for the interpreter *after the reply has already arrived*,
charged to the call that is waiting. **That makes the case for stopping stronger
than the 4% did**: an infinitely fast env saves less than the occupancy figure
suggests, because some of the occupancy is on the far side of the socket.

⚠️ **Their ceiling was wrong first, and the control is what caught it.** The first
version used the thread count (15x). The real ceiling is threads × the fraction of
a *solo* worker's time that is env — 15 × 61% = 9.09 — because a worker alone is
only 61% env-bound.

⚠️ **Recorded at the height it actually happened, because `gg2-rl` corrected this
entry downward against its own credit.** They did **not** run the sequential arm
expecting to falsify anything. They ran it as an **instrument check**, predicted
1.0 because that is what "fully serialised" ought to mean, and got **0.60**. The
whole of the discipline was *not shrugging at a control that disagreed*. **The
reproducible lesson is "believe the control", not "have good instincts"** — and
the first version of this paragraph made it the second, which would have made it
a story about judgement instead of a thing anyone can do.

⚠️ **What `9f0d344` bought a training iteration is UNMEASURED, and deliberately
so.** Its effect is a stall tail; their benchmark resolves 0.4% on a slope; and
the mechanism those stalls would contend with is a GIL queue worth 69% of their
parallelism. Measuring it would be reporting noise. **Not zero — unmeasured**, and
the stall elimination is real on its own terms (12 per 20,000 → 0). They also
retracted an earlier offer that their benchmark "should see a latency change if
one is there"; at these sizes they no longer believe it can.

## The boundary is latency, not throughput (2026-08-31)

Per-round-trip latency over a real socket, `ctf_truefort`, 20,000 timed round
trips after a 3,000 warm-up:

| batch | before | after | |
|---|---|---|---|
| 1 | 79.3 µs mean, 58.3 p50 | **50.7 µs mean, 44.2 p50** | **1.56x** |
| 16 | 289.2 µs mean, 281.1 p50 | **265.7 µs mean, 258.8 p50** | 1.09x |
| 64 | — | 961.3 µs mean | — |
| **round trips stalling past 10 ms, batch 1** | **12 in 20,000, worst 25.3 ms** | **0** | |

Two causes, both fixed:

- **`WriteFrame` wrote the 4-byte header and the body separately.** A small
  segment ahead of the body is the exact shape Nagle holds back waiting for an
  ACK. It is one write now, assembled in a **pooled** buffer — a reply is 107 KB
  at batch 64 and this is the per-step path, so allocating would trade a syscall
  for the large-object heap. `EnvProtocolTests.AFrameIsOneWrite`.
- **`NoDelay` was never set.** This is request/response on loopback with the
  client blocked until the reply lands, so there is never a second small write to
  coalesce with and Nagle can only add delay. Set on the accepted socket.

⚠️ **The two are independent and both were measured on their own** — `NoDelay`
alone took the batch-1 mean 79.3 → 69.8, coalescing alone 79.3 → 54.4, together
50.7. Neither replaces the other: coalescing removes the small segment, `NoDelay`
covers the frames that are still small.

### ⚠️ Corrected by `gg2-rl`: the fault was ONE-SIDED, and the A/B above hid that

**`gg2-rl`'s client has had `TCP_NODELAY` since its first phase 0 commit**
(`src/gg2rl/env/protocol.py`, immediately after `create_connection`), **and its
`send_frame` was already one write** — `sendall(header + body)`, coalesced by
construction rather than by intent. So:

- **Nagle needs a small unacknowledged segment followed by another small write
  from the *same sender*.** With `NODELAY` on their side and not on this one,
  their requests always went immediately and **only this server's replies could
  ever be held**. The two-write `WriteFrame` was the entire mechanism, and there
  was never a symmetric version of this bug to find.
- ⚠️ **The four cells measured above toggled `NoDelay` on BOTH ends at once**,
  because the benchmark owns both sockets. **So the "NoDelay alone" row describes
  a configuration no real deployment was ever in** — the real client already had
  it. The row that corresponds to what `gg2-rl` actually experienced is
  *two-write server, client `NODELAY` on*, which is not one of the four. The
  stalls are still real and still theirs: Nagle is a property of the sender, and
  the sender holding segments was this server.
- ⚠️ **Keep the server-side `NoDelay` anyway.** It is defence for any client that
  is not `gg2-rl`, and a server should not depend on its clients configuring
  their sockets correctly. But **the credit for the measured win belongs to the
  coalescing**, not to it.

⚠️ **A knock-on neither of us costed: coalescing also fixes the client's READ
path.** `read_frame` reads 4 header bytes and then the body. Two writes here
meant that second read could wait for a second segment to arrive; one write means
the body is already in the client's buffer when the header read returns, so the
second `recv` returns immediately. That is a syscall's worth of latency per round
trip on top of the Nagle fix, in the direction being measured.

⚠️ **Buffering the READ side was tried and is WORSE.** Wrapping both ends in a
`BufferedStream` (1 MB) to serve the body read from memory: batch 1 **50.8 →
63.3 µs**, batch 16 **249.9 → 269.7 µs**. The extra copy through the buffer costs
more than the `recv` it saves, because the body has already arrived by then. Do
not try this again.

⚠️ **`smoke` cannot see this and reads 40k steps/sec before and after.** A tight
request/response loop keeps the ACK clock running and rarely stalls; the trainer
stepping N workers in lockstep pays the worst of them on every step. **A stall
that happens 0.06% of the time happens on most steps once there are enough
workers.** This is why the throughput table above did not move and the change is
still worth having.

## ⚠️ A setting that outlived the configuration it was chosen for

**`gg2-rl`'s finding, 2026-08-31, and it is a distinct failure from a stale
claim.** Its default duel count was chosen when duels were long and rare — the
(150,350) band — and the in-range band it uses now resolves far faster, so the
same wall-clock budget buys many more duels. Nobody re-derived the default after
changing the task. The result: **four numbers in one day that moved substantially
when the sample was widened**, one from −4.5 to −13.7 points at four times the
duels.

⚠️ **The dangerous half is the CONFIRMATIONS.** Nobody widens a sample that
already agreed with them, so a thin sample that matched a prior is never re-run
and never corrected. `gg2-rl` audited its own results on that basis and found one
genuinely exposed (an engagement result, ~500 duels, never widened) and one saved
by accident (the +22 prior lift, replicated at n=11,215 by the exploration
monitor it built for a different purpose).

### The same shape, twice, in THIS repo

Found by applying the lesson here rather than agreeing with it:

- ⚠️ **`RolloutOptions.HorizonTicks = 20` is a design-time range, not a
  measurement.** `docs/BOTAI.md` item 12 said 15-30 and 20 was picked from it. The
  only sweep ever run went **upward**, 20 to 90, and found the win rate falling
  monotonically in every class. **Nobody had run it below 20**, and 15 is inside
  item 12's own range — so the entire measured curve pointed into an untested
  region. ⛔ Not lowered on that: extrapolating off the end of a measured range is
  exactly how the blind fraction came to be minimised.

  ✅ **`gg2-rl` ran the downward half within the hour, and the refusal was right
  for the right reason: the curve is NOT monotone below the default.** 288 duels
  a cell — horizon 8 wins **80.4%** [73.6, 85.8], 15 wins **93.1%** [88.1, 96.1],
  20 wins 90.8%. The upward sweep's monotone fall was **one side of a peak**, and
  "shorter is simply better" would have been wrong at 8, where the interval is
  disjoint from 15's. **That is the durable result.**

  ⛔ **"15 beats 20" did NOT survive widening.** At 864 a cell: 91.6% against
  90.5%, +1.1, overlapping. The default's cell was stable (90.8 → 90.5) and the
  15 cell was the noisy one (93.1 → 91.6) — the apparent 2.3 was one arm being
  lucky. ✅ **So the default stays at 20 for a MEASURED reason now**: it is inside
  the peak region, and the region below it has been looked at. **This entry is
  resolved** — the setting is no longer one that outlived its justification.

  ⚠️ **It is the fifth number in a day to shrink materially on widening, and the
  first widened BEFORE being written down as a finding.** It cost a re-run instead
  of a retraction in two repos, which is the entire value of the rule.
- ⚠️ **The `CommitTicks` negative result rests partly on 24 duels a cell.** Its
  blind fractions are ratios over many decisions and are not in doubt — 0.523 →
  0.965 is not a sampling artefact. Its **win rates are thin**: 0.375 against
  0.625 at n=24 barely separate. "It slides back along the same trade" is solid on
  the blind axis and provisional on the win axis, and that distinction was not
  drawn when the result was written down.

⚠️ **This is deliberately NOT in `CLAUDE.md`.** That file is already 17% over its
own budget and I put it there twice yesterday; a third "my addition is the
exception" is the failure it warns about. It lives with the settings it is about.

## ⚠️ The reset reload — five milliseconds to start a one-millisecond episode

**Found 2026-08-30, and it had been there since the env was built.**

`DuelEnv.Reset` called `ScenarioWorld.Load`, which read the map PNG off disk,
decoded it, decoded the walkmask, built a `CollisionWorld` — and **re-parsed the
nav cache**, which for `ctf_truefort` is 2 MB of text. **5.121 ms.** An episode
of 120 decisions is about **0.9 ms** of simulation. So starting an episode cost
between five and six times as much as running it, and `EnvBatch`'s constructor
paid it once per world on top.

The split, on `ctf_truefort`: nav parse **4.80 ms**, map decode **0.281 ms**,
`new CollisionWorld` **0.009 ms**.

`ScenarioResources` fixes it by caching the two things that are **deeply
immutable** — the `LevelMap` and the `NavGraph` — process-wide, and rebuilding
the `CollisionWorld` per world for 0.009 ms rather than reasoning about
`SetCaptureZones`. `ScenarioWorld.Load` reads through it by default, so the test
suite gets it too.

⚠️ **Why nobody saw it.** Three separate reasons, and each is reusable:

- **It is not on the step path.** Every profile anyone had reasoned about was of
  a *step*, and a reset is not one. The cost only appears when you ask how often
  an episode ends — 0.12 times per round trip at batch 16, which multiplied by
  5.1 ms is 0.61 ms against a 0.14 ms of everything else.
- **The throughput number was healthy.** 34,073 steps/sec cleared the 20k floor
  the programme was gated on, so the number that would have prompted the question
  had already answered a different one and been filed.
- **`--nav` is passed unconditionally**, by `gg2-rl` and by most tests, even for
  a duel with no scripted side. A graph nothing was going to use was parsed for
  every episode of every world. ⚠️ **Still true, and still worth fixing on the
  caller's side** — it is now 10 µs instead of 5 ms, so it is no longer urgent
  and no longer interesting, which is exactly how this sort of thing survives.

⚠️ **Allocation went with it**: 2.98 MB per round trip at batch 16 became
**23.6 KB**, because a reset was building a fresh 145 KB `WorldState`, a
`CollisionWorld` and a nav graph each time. `CLAUDE.md` forbids allocating in the
tick loop; nothing forbade it in the reset, and nothing was watching.

⚠️ **The remaining allocation is the reset too** — a `DuelEnv.Reset` still
builds a whole new `ScenarioWorld` where it could clear the one it has. That is
~1.5 KB a round trip and about 1% of the time, so it is written down here rather
than done.

## Measurements

| Measurement | Value | Build | When |
|---|---|---|---|
| Decision steps/sec, batch 1 | 8,753 → **9,060** | Release | 2026-08-28 → 08-30 |
| Decision steps/sec, batch 64 | 34,073 → **40,700** | Release | 2026-08-28 → 08-30 |
| Decision steps/sec, batch 64, 2 threads | 36,173 | Release | 2026-08-28 |
| Decision steps/sec, batch 64, 8 threads | 37,592 | Release | 2026-08-28 |
| **A round trip on a real map with nav, batch 16** — `gg2-rl`'s training shape | **0.749 → 0.204 ms, 3.67x** | Release | 2026-08-30 |
| — of which the map and nav reload | **0.61 → 0.00 ms** (see "the reset reload") | Release | 2026-08-30 |
| — of which the observation writer | 0.138 → **0.100 ms** (the ray fan skips open ground) | Release | 2026-08-30 |
| — of which serialisation | 0.004 ms, **2%** ⚠️ not the lever, see below | Release | 2026-08-30 |
| Bytes allocated per round trip, batch 16 | **2.98 MB → 23.6 KB**, gen0 collections 250 → 9 per 1,500 rounds | Release | 2026-08-30 |
| `ScenarioWorld.Load`, `ctf_truefort` with nav | **5.121 → ~0.01 ms** | Release | 2026-08-30 |
| `ObservationWriter.Write`, ray fan, averaged over 512 map positions | **5.02 → 2.73 µs** | Release | 2026-08-30 |
| `RolloutSearch` decision, 60 candidates, horizon 20 | **3.22 ms** (fork 11.1 µs/candidate, tick 2.13 µs) | Release | 2026-08-31 |
| `RolloutSearch` decision, 120 candidates (sniper) | **5.83 ms** | Release | 2026-08-31 |
| **Socket round trip, batch 1** | 79.3 → **50.7 µs mean** | Release | 2026-08-31 |
| Socket round trip, batch 16 | 289.2 → **265.7 µs mean** | Release | 2026-08-31 |
| Round trips stalling past 10 ms, batch 1 | **12 in 20,000 → 0** | Release | 2026-08-31 |
| ⛔ **`gg2-env` CPU share of a live training iteration** | **~4%** — 4.2% of one core each, machine 91% idle. ⚠️ **CPU, not occupancy**: a worker blocked on a round trip burns 0% and still holds the critical path | Release | 2026-08-31 |
| `gg2-env` worker bring-up, 16 worlds | ~92 ms (33 spawn + 15 handshake + 43 `MakeBatch`) | Release | 2026-08-31 |
| `ObservationWriter.Write`, one call | ~5.0 µs (2026-08-28, one position) → **3.13 µs** averaged over a real duel | Release | 2026-08-28 → 08-30 |
| Sixteen of them — `gg2-rl` phase 2's budget unit | 79 µs → **50 µs, 5.0% of 1 ms** | Release | 2026-08-28 → 08-30 |

⚠️ **The two `steps/sec` rows are `gg_debug` with no nav, which is this
measurement's WORST case for the 2026-08-30 work** — there is no 2 MB nav cache
to stop re-parsing and the map is 636 bytes. The 3.67x row is the configuration
`gg2-rl` actually trains in. Quoting the 1.28x as "the speedup" understates it by
almost threefold; quoting the 3.67x as "the throughput" overstates what a smoke
run will print. **They are different questions and both are here.**

⚠️ **Nothing in that work moved a number.** Observations and reward components
were digested over 4,012,800 floats across `gg_debug`, `ctf_truefort` and
`cp_dirtbowl` (multi-area) on the before and after trees:
`4FFEC587…`, `F6763969…`, `2DDB39FB…` — **identical on both**. So
`SIM_UUID` did **not** move and no checkpoint was invalidated. That check exists
because "a refactor cannot change behaviour" is a claim, and this repo has been
wrong about that kind of claim before.
### Observation extraction, measured because somebody was about to design around it

⚠️ **Asked for by `gg2-rl`**, which had measured the *other* half of phase 2's
criterion — a 3-layer MLP, 209 inputs to 25 logits, at 13.6 µs for sixteen — and
noticed that the half it could not measure was mine. Its criterion is **1 ms for
16 bots including observation extraction**, and extraction runs per bot per
decision.

**The answer: extraction dominates inference by about seven to one**, and the
budget is still comfortable. 79 µs of extraction plus ~14 µs of inference is
about **9% of 1 ms**, so phase 2 has an order of magnitude in hand.

⚠️ **Updated 2026-08-30: extraction is now ~50 µs for sixteen, so about 6%** —
the ray fan skips open ground. The ratio to inference is four to one rather than
seven to one, and extraction still dominates.

⚠️ **And the 5.0 µs figure hid a dependence it should have named.** It was one
call at one position, and a ray fan costs what the geometry around it costs:
**3.2 µs in a corridor and 8.3 µs in an open room**, on the pre-2026-08-30 code.
The row above is now averaged over a real duel instead, which is the number a
budget should be built on. The flatness *in roster size* below is real and
unaffected — that is a different variable, and it is the one that was actually
being claimed.

⚠️ **It is flat in roster size**, which is the non-obvious part and the reason
the number is safe to design around: 5.7, 5.1 and 5.0 µs at populations of 2, 8
and 16.

⚠️ **And the flatness is STRUCTURAL, not luck** — worth stating precisely,
because "no neighbour-scan cost was observed" and "there cannot be one" are
different claims and only the second lets phase 4 be projected from phase 2.
`WorldView.Nearest` loops `for (other = 0; other < WorldState.MaxSlots; other++)`
and skips the empty slots inside the loop, so **the sweep is 64 iterations at a
population of two and 64 at a population of sixteen**. A fuller roster adds only
the insertion work for the extra live characters, which is dwarfed by that sweep
and by the 209-float write. The observation is already paying its worst-case scan
cost in a duel.

⚠️ **Do not read anything into the downward direction.** 5.7 → 5.0 is noise; if
anything the insertion work should make it rise slightly. What the data shows is
the *absence of growth*, and the loop bound above is why there can be none.

⚠️ **Measured in Release**, like everything else in this table, and by calling
`ObservationWriter.Write` in a loop over a settled `ScenarioWorld` — so it is the
extraction alone, with no socket and no serialisation. The boundary cost is the
row above it, and the two should not be added: a policy running **in process**
pays this and not that.

✅ **34,073 clears the ~20k `gg2-rl` phase 0 stops the programme below**, so
shared memory is not needed yet — which was the decision this number existed to
make.

⚠️ **Threads barely help: 34k → 38k across 1 to 8.** That is the informative
part, and **the conclusion originally drawn from it was wrong.**

### ⛔ Superseded 2026-08-30 — the serialisation loop was never the lever

**What this document said**, from the day the throughput was first measured until
2026-08-30: *"the simulation is not the bottleneck; the boundary is … every step
serialises `worlds × 2 × 209` floats one `BinaryPrimitives` call at a time … if
throughput ever needs to go up, that loop is the lever and more threads are
not."* `gg2-rl` repeated it in its own `docs/PLAN.md`.

**Measured, at last:** that loop is **2%** of a batch-16 round trip. The three
things that actually cost anything were

| | share of the round trip, before | |
|---|---|---|
| re-reading and re-parsing the map and nav cache **on every episode reset** | **~80%** | nobody had looked |
| the observation writer, 88% of it the geometry ray fan | ~19% | |
| serialising the floats | **~1%** | the one named as the lever |

⚠️ **The reasoning that produced the wrong answer is worth keeping, because it
was not careless.** "Threads move it 1.1x, so the parallel part is not the
bottleneck" is *sound*, and it correctly located the cost outside
`MatchSimulation.Tick`. What it then did was pick the most visible serial thing —
a loop with a per-float call in it, right there in `EnvServer` — and stop.
**`ScenarioWorld.Load` is serial too, and it is not in `EnvServer`, and it does
file I/O**; it was invisible precisely because it is on the reset path rather
than the step path and nobody had asked how often a reset happens.

⚠️ **The general form: an elimination argument tells you which half, not which
line.** "Not the parallel part" left two candidates and only one of them was on
screen. The fix is the same as everywhere else in this repo — **measure the
half you concluded into, do not nominate a line inside it.** One `Stopwatch`
around three regions settled it in ten minutes.

⚠️ **And a benchmark that warms up for 50 iterations measures tier-0 code.** The
first attempt at this breakdown reported a duel tick at **23 µs** with one world
and **2.9 µs** with 64 — the same code, the same content. .NET promotes a method
after 30 calls but recompiles it in the background, so a short run never leaves
the unoptimised build, and a *small* configuration finishes before the rejit
lands. Warm by total work, not by iteration count; the numbers above use ≥60,000
world-steps of warmup and reproduce to ±1%.

The serialisation is a single `MemoryMarshal.AsBytes` copy now anyway — it was
two minutes of work and it is worth the 2% — with the per-float loop kept for a
big-endian host.

⚠️ **Both figures name their build, and the CLI prints it with the number.**
CLAUDE.md's standing rule: this project's headroom figure sat stale in four
documents because a re-measure forgot to say which configuration it was, and
Debug reads about half of Release.

## The policy as the rollout continuation — costed, not built (2026-08-31)

**`gg2-rl`'s `docs/DESIGN.md` step 3**, and the only part of that method that
compounds. Asked about, not asked for. Recorded here because the **cost model is
this repo's to supply** and the estimate that came with the ask was out by ~4x.

### The measured cost model for a search decision

Release, `gg_debug`, 60 candidates, `DecisionTicks: 1`, horizon swept 20→320 and
fitted:

| | |
|---|---|
| per candidate, fixed | **~10-12 µs** — the `_fork = _baseline` copy, a 145 KB `WorldState` |
| per candidate-tick | **~2.1 µs** |
| a decision at horizon 20 | **~2.79 ms** |

⚠️ **The fork does not scale with horizon**, which is why pruning frees more than
a candidates-times-horizon account suggests: it removes whole forks, not just
ticks.

### What a policy continuation would cost, including the term the ask omitted

A continuation means the policy drives the rollout after the commit window — so
**an observation and a forward pass per tick per candidate**.

| per candidate-tick | |
|---|---|
| simulation | 2.1 µs |
| ⛔ **`ObservationWriter.Write`** | **~3.1 µs** |
| forward pass (`gg2-rl`'s figure) | ~1.6 µs |
| **total** | **~6.8 µs — 3.2x the bare tick** |

⛔ **Extraction is the largest of the three and was not in the estimate.** The ask
costed 1,200 forward passes at ~1.9 ms and concluded "+50%". Adding the
observations those passes need:

| at horizon 20 | cost | against today's 2.79 ms |
|---|---|---|
| 60 candidates, continuation | **8.8 ms** | **+215%**, not +50% |
| 8 candidates, continuation | **1.17 ms** | **cheaper than the search we run now** |

⚠️ **The conclusion survives the correction and gets stronger.** Pruning does not
merely make the continuation affordable — **pruned-with-continuation costs less
than unpruned-without**. But the reason is mostly this repo's observation writer,
not the network, so the number to watch when anyone re-estimates this is the
extraction row.

### ⚠️ The affordability frontier is PER CLASS, and a fixed k over-prunes the sniper

Measured 2026-08-31, Release, `gg_debug`, horizon swept and fitted per class:

| | candidates | fork | tick | a decision at horizon 20 | continuation affordable to |
|---|---|---|---|---|---|
| soldier (and everyone else) | 60 | 11.1 µs | 2.13 µs | **3.22 ms** | **k ≈ 21** |
| **sniper** | **120** | 11.2 µs | 1.87 µs | **5.83 ms** | **k ≈ 40** |

The per-candidate costs are the same; the sniper simply has twice the set, so its
unpruned decision costs twice as much and it can afford twice the k.

⛔ **Which means a k chosen for a soldier is twice as aggressive on a sniper.**
k=8 is 13.3% of a soldier's candidates and **6.7% of a sniper's**. `gg2-rl`'s
retention table — the policy's top 8 holds the search's best action 78.2% of the
time, top 12 holds 86.6% — was measured on **60**-candidate sets. Applying the
same absolute k to a sniper halves the fraction retained, so retention will be
worse and pruning will cost more.

✅ **Confirmed 2026-08-31**: `gg2-rl` re-ran four classes keeping **27% of each
class's set** instead of a fixed 16, and the pruning cost went **−8.1 → −6.7
points**. Correcting the fraction recovered **1.4 points**, in the predicted
direction — the fixed-k sniper cell really was measuring arithmetic.

⚠️ **And the sniper is the class most likely to be read wrong because of it.** It
has the worst blind fraction (92.8% at the default horizon) and twice the
branching factor, so "pruning hurts snipers more" is exactly the conclusion an
absolute k would manufacture — when the cause would be **k/N halved**, not
anything about snipers. **Scale k with the candidate set, or state that you did
not.**

### The shape, if it is ever built

⚠️ **This was written when the "out of scope" list still refused an ML runtime**,
and the notice was that reopening it had to be deliberate rather than quiet. ✅ It
WAS reopened, deliberately, on 2026-08-30 — see the struck-through table under
"Out of scope". **The paragraphs below were written before that and are kept as
written**, because their design constraints did not depend on the refusal.

- ⚠️ **Weights over the wire, in the batch — NOT a file format.** A weights file
  in this repo is a second source of truth about what a checkpoint is, and a
  version coupling with a trainer that already couples through `SIM_UUID`.
  Arriving in `MakeBatch` and living for the batch's life has neither.
- ⚠️ **Not a callback over the socket.** 1,200 round trips a decision at 50 µs
  each is 60 ms; the whole decision is 2.8 ms.
- **No allocation in the rollout loop** — scratch buffers per search instance.
- **Not in `SIM_UUID`.** A search is a bot, and this project's bots are its own
  (`CLAUDE.md`). The observation layout it consumes is already stamped, which is
  the part that must not drift.

### ✅ The cheap piece — BUILT 2026-08-31

`EnvMessage.StepRestricted = 11` and `StepScoredRestricted = 12`, plus
`RolloutSearch.RestrictNextDecision` and `DuelEnv.RestrictSearch`.
**No protocol version bump** — no existing message's body or reply moved, on
`Candidates`'s precedent. Observations digest identical on all three maps;
`SIM_UUID` unmoved.

⚠️ **A new KIND rather than a tail on `Step`, and not because a bump was
unwelcome.** `EnvServer.Step` checks `body.Length < needed`, so it already
accepts and ignores trailing bytes — a new client sending restrictions to an old
server would be **silently unrestricted** and would measure "pruning changes
nothing". A kind the old server does not know is refused loudly instead.

### ⛔ And it shipped with a bug: the sniper, on step 0 only

**Found by `gg2-rl` within an hour**, and every test above passed over it.

Only a sniper carries the zoom dimension, so its candidate set is **120**. But
`RolloutSearch`'s constructor builds with `considerZoom: false`, and the set is
only rebuilt from the slot's class inside `Search` — so the instance
`CandidateCount` reads **60 for a sniper until its first decision has searched**.
The restriction validator asked the instance. Index 76 — valid, and inside what
`Candidates` had just reported — was refused, **on step 0 and only on step 0**,
because by step 1 the set has been rebuilt and the bug evaporates.

⚠️ **Three sources disagreed about one number**: `Candidates` said 120, the score
block said 120, the validator said 60. `DuelEnv.CandidatesFor` was already right
and already documented as *"derived from the spec rather than read off the live
search, so it is answerable before the first Step"* — **the bug was adding a
fourth way to ask instead of using it.**

The fix is that `DuelEnv.TrySearchShape` is the only place that decides what
shape a slot's candidate set has, and `RolloutSearch.CandidateCountFor(options,
class)` is the only arithmetic. Gated by
`EnvProtocolTests.ASniperIsRestrictableOnStepZero` (the exact repro, and it must
be step 0) and `RolloutRestrictionTests.EveryCountSourceAgrees` (all nine
classes, three option shapes, comparing the arithmetic against the built list so
neither can drift).

⚠️ **The generalisable part.** Refusing rather than clamping is what turned this
into a loud failure in seconds instead of a sniper quietly searching the wrong
half of its options — that decision paid for itself immediately. But **a bound
that is wrong in the safe direction still breaks the caller**: the refusal killed
the step rather than dropping the mask. *Validate against the thing that decides,
not against a thing that usually agrees with it.*

**What is gated, and each of these was mutation-tested:**

| invariant | why it would rot silently |
|---|---|
| the decision is a pure function of the SET | the tie-break is lexicographic and 85.2% of non-blind decisions have their top two tied, so iteration order settles many of them. An implementation that walked the client's array would be irreproducible and every other test would pass |
| a stale mask is dropped on a class change | index *i* names a different action in the 60-list and the 120-list; a mask that merely *fits* would search six wrong actions and report plausible numbers |
| `LastDecisionTicksSimulated` counts what was searched | the point of a restriction is that it costs less; counting the candidate list reports a budget nobody spent |
| the restriction is consumed by one decision | a sticky one would prune every later decision invisibly |
| validate everything, then apply | a bad index in a late world must not leave earlier worlds pruned |
| the restriction is applied before the step | applying after would restrict the decision *after* the one the client meant |

⛔ **The validate-then-apply gate did not catch its mutation at first**, and the
reason is worth keeping: a restriction changes **no observation**, so a test that
checked "nothing stepped" passed just as well against a server that restricted
world 0 and then refused world 1. It needed a later `StepScored` asserting world
0's score count was still the full candidate set. **A leak with no observable
signature needs a test that goes looking for the signature it does have.**

The design, for reference — indices into what `Candidates` returns, empty
meaning "search everything". `BuildCandidates` is a pure function of
`(options, class)`, so an index is stable for a batch; ⚠️ **the length changes
60 ↔ 120 when the class does** (only a sniper carries the zoom dimension), so an
implementation must validate against the current count and **refuse**, never
clamp — an index silently meaning a different action is the worst failure
available here.

**That interaction is now answered** — `LastScores` is aligned 1:1 with the full
candidate list and `StepScored`'s block is that array, so a pruned search either
breaks the alignment or needs a sentinel. `gg2-rl`'s three answers, 2026-08-31:

1. ⚠️ **Send `(index, score)` pairs, never a full-length block with a sentinel.**
   The argument is this repo's own note on `Candidates` — without the actions, a
   distribution is "a ranking over an unnamed list" — and a sentinel recreates
   exactly that one level down. **Zero is already taken by the blind case** and
   NaN propagates silently through the first reduction that touches it; a number
   sitting in a numeric array will eventually be summed, averaged or softmaxed by
   somebody. An index list also describes its own length, which is what the
   60 ↔ 120 class change needs.
2. ⚠️ **Unevaluated candidates get NO target, not a zero one.** The
   cross-entropy runs over the evaluated set, renormalised. Training them toward
   zero probability teaches the policy those actions are *bad* when the truth is
   that **nobody looked** — invisible in a loss curve, and it surfaces two rounds
   later as a policy that has narrowed for no reason.

### ⛔ 3. The pruned set must NOT be purely the policy's top-k

**A search that only evaluates what the policy already likes, feeding a policy
trained on what the search evaluated, is self-confirming.** It cannot be
corrected where it is wrong, because where it is wrong is exactly where nothing
gets looked at.

⚠️ **This is not hypothetical and the measurement says so.** The policy's top 8 of
60 contains the search's best action **78%** of the time — so **22% of the time
the best action is outside the pruned set**, the search would never find it, and
the distillation target would confirm the mistake. Iterated, that compounds into a
policy that is confidently narrow with every round of evidence generated by its
own blind spot.

So the set is **top-(k−m) plus m sampled from the remainder** — AlphaZero's
Dirichlet-noise-at-the-root problem, different mechanism, identical requirement.

⚠️ **What that means for this seam, and it is the part the interface must not get
wrong:** the index list arriving here is **not "the policy's top k"**. It is a
SET. **Nothing on this side may assume it is ordered, or that it is a prefix of a
ranking, or that index 0 is the policy's favourite.** A future optimisation that
"knows" the list is sorted would silently break the exploration that keeps the
loop honest.

✅ **And it buys a free monitor on the whole loop**: the exploration candidates
are a held-out sample of the region the policy pruned, so **if the
sampled-from-the-remainder candidates keep out-scoring the top-k ones, the prior
is bad and pruning is hurting.** Computable every round at no cost, and it fails
loudly instead of plateauing quietly.

## ✅ Phase 4 result: the hybrid beats the bot it replaces, on both axes (2026-09-01)

> ⛔⛔ **SUPERSEDED IN ITS STATISTICS, NOT IN ITS CONCLUSION.** Every number below
> was measured at a **5-minute match cap** and is CENSORED — on some maps half
> the arm never expressed an outcome — and every win share below is over
> `decided`, which **includes draws** and is not the win share's denominator.
> **Read "Phase 4 re-measured" further down before quoting anything here.** The
> conclusion survived the correction and two of its results got stronger; the
> figures did not.


**The exit criterion is met, and the value that got it there is not the one
anybody started with.** Six CTF maps, 8 bots, four trained classes both sides,
policy on red, all-scripted on blue.

### The criterion in its own words: win rate, per map, on every shipped map

⚠️ **`gg2-rl` asked for three things the aggregate above cannot say**, and it was
right about all three. **21 shipped maps** (`gg_debug` excluded as a test map),
40 matches a map an arm, the game's own decision rule:

| | decided | red win share | death share | stuck |
|---|---|---|---|---|
| policy (macro task, 150px) | 566/840 — **67.4%** | **62.5%** | **46.2%** | 10/6,720 |
| all scripted | 560/840 — 66.7% | 55.3% | 49.9% | 16/6,720 |

✅ **The cap does not dominate at `CapLimit = 1`** — two thirds of matches decide,
so the win rate is quotable rather than a statistic about the clock. That was the
condition for reporting it at all.

⚠️ **The aggregate win share is confounded and should not be quoted alone.**
Several maps are structurally one-sided — `cp_dirtbowl`, `ctf_avanti`,
`ctf_classicwell`, `ctf_orange` read 100% red in **both** arms — so 62.5% against
55.3% mixes a real effect with map asymmetry that is identical in both arms.

⛔ **Three maps read WORSE for the policy at 40 matches. All three evaporated at
400.**

| map | policy / control at n=40 | at n=400 |
|---|---|---|
| `ctf_2dfort` | 42.1% / 55.0% — worse by 12.9 | **59.4% / 47.4% — better by 12.0** |
| `arena_montane` | 47.4% / 55.6% — worse by 8.2 | 57.2% / 56.6% — no difference |
| `koth_corinth` | 15.4% / 23.1% — worse by 7.7 | 19.2% / 23.7% — worse by 4.5, ~1.5σ |

**`ctf_2dfort` reversed sign.** 40 matches a map is ~38 decided, so a 12-point
difference is five wins — noise. ⚠️ **The per-map screen at n=40 cannot resolve a
per-map regression**, which is what it was for; the three most suspicious were
re-run and none is resolvable. `koth_corinth` is the one that persists in
direction, on a map that is 19-24% red in **both** arms, and its death share still
improves (46.2% against 50.1%).

⚠️ **Stuck bots, per map rather than aggregate**: the policy arm's worst map is
`ctf_orange` at **1.88%**, against the control's worst of `ctf_classicwell` at
**2.19%**. No map is worse under the policy than the control's own worst, and the
three re-run maps had **0 of 9,600** in both arms.

### ⛔ So: met, with the gaps stated

**Met**, on 21 shipped maps, by the game's own decision rule, with a control arm,
and with no resolvable per-map regression. **The gaps, which belong in the same
sentence:**

- ⚠️ **`dkoth_atalia` and `dkoth_sixties` produce ZERO decided matches in either
  arm.** Dual KotH does not resolve at these limits, so the win-rate criterion is
  **silent** on two of the 21. Death share and stuck bots are fine there; the
  win rate is untested, not passed.
- ⚠️ **Per-map differences at 40 matches a map are mostly not resolvable.** "No
  map regresses" is supported for the three that looked worst and re-run at 400,
  and is an *absence of evidence* elsewhere.
- ⚠️ The aggregate win share is confounded by structurally one-sided maps; the
  death share (46.2% against 49.9%, over every map) is the cleaner aggregate.

### The handover sweep — 420 matches a point

| rule / range | red caps | blue caps | red's share | red death share | policy share of control |
|---|---|---|---|---|---|
| **head split** (no trigger) | 49 | **126** | 28.0% | **55.0%** | 85.1% |
| distance, 300px | 36 | 48 | 42.9% | 43.0% | 27.8% |
| macro task, 300px | 47 | 53 | 47.0% | 43.8% | 23.4% |
| macro task, 220px | 77 | 52 | 59.7% | 44.8% | 14.5% |
| **macro task, 150px** | **97** | 45 | **68.3%** | 46.0% | **7.0%** |
| macro task, 80px | 92 | 45 | 67.2% | 47.6% | 2.3% |
| all scripted (control) | 76 | 72 | 51.4% | 50.5% | — |

### Confirmed at 1,200 matches an arm

| | red caps | blue caps | red's share | red death share | stuck bots |
|---|---|---|---|---|---|
| **macro task, 150px** | **262** | 134 | **66.2%** | **46.8%** | 84 / 9,600 |
| all scripted | 212 | 198 | 51.7% | 51.5% | 98 / 9,600 |

~400 capture events and ~59,000 deaths an arm. **Better at fighting AND better at
the objective than the bot it replaces**, and no stuck-bot regression.

⛔ **THE POLICY IS MOST VALUABLE USED SPARINGLY.** At 150px it holds **7%** of
ticks and wins on both axes; at 300px it holds 23% and loses the objective. More
policy is not better — the combat gain does rise with range (46.8% → 43.8% death
share from 150 to 300) but the objective collapses faster than the combat
improves.

⛔ **`gg2-rl`'s ORIGINAL objection was right and its retraction was wrong.** It had
argued its user out of a head-level split (policy aims and fires, scripted always
moves) on a duel measurement, then withdrew that on the grounds the measurement
compared against random movement rather than competent nav. Measured against
competent nav: the head split is **worse than all-scripted on both axes** — 55.0%
death share against a 50.5% control, and 49 captures against 126. Its own
falsification condition was "the death share slides back toward 50.5%"; it went
**past** it. **The policy's movement is not a bonus, it is where the combat gain
comes from**, and splitting the heads leaves the two halves fighting each other.

⚠️ **The default moved: `HandoverRule.MacroTask` at `EngagementRange = 150`.**
`DuelEngagementRange = 300` is kept as a named constant, because the 300 was
right for duels — every tick a combat tick, nothing else the bot could be doing —
and too wide for a game with an objective in it. This is the curve `gg2-rl` asked
for before the default moved.

### ⚠️ Two bugs found by measuring, both mine, both in the wiring

**1. The macro layer stopped thinking whenever the policy drove.** `PolicyBot`
polled `ScriptedBot` only on the ticks it handed to it — so for 27% of ticks
there was no objective planning at all: no re-target, no re-path, no spot ban, and
stale path state the moment control returned. It now polls **every tick** and its
keys are overridden rather than skipped. ⚠️ **This voids the original "20 captures
to 7" in both directions**: a macro layer that is not running would suppress
captures with no policy involved.

**2. Gating on the scripted engage hold ALONE handed the policy the wheel blind.**
That hold fires out to `BotTargeting.ClassRange` — **800px for a soldier, 900 for
a sniper** — three times the engagement range and twice the ~400px where the
observation saturates. Control share went 27% → **71%** and captures to 3.
`MacroTask` is a **conjunction** of intent and range for that reason. ✅ `gg2-rl`
predicted the check that caught it: *"if the gate does not cut the share sharply,
the gate is not doing what we think"*. It did not cut it; it more than doubled.

### ⚠️ And an n=108 reading that reversed

At 108 matches an arm the distance rule read red **13** captures against blue
**6**, and the control read **20 against 12** — both apparently a red advantage
that does not exist. At 420 the control is **76 against 72**, even. **A capture is
a rare event, under 0.2 a match**, so a hundred matches is a few dozen events and
a difference of ten is nothing. The failure was not that the sample was small; it
was that **nobody converted matches into events before choosing the sample size.**

## ⛔⛔ Phase 4 re-measured: every earlier number was CENSORED (2026-09-01)

⚠️ **Read this before quoting any phase 4 figure written above.** The result
stands; the statistic it was quoted on does not, and the correction took a day.

### What was wrong

Every phase 4 measurement used a **5-minute match cap**, and on several maps that
cap was truncating the distribution the statistics were computed from. On
`gen_destroy` the **CONTROL** arm decides **50.3% of matches at five minutes and
97.0% at twenty**, with a mean length of 382.6s against a 300s ceiling.

⚠️ **The mean length was the giveaway and two sessions read past it.** "342s
against a 300s cap" is not a mean, it is a boundary. A "7.8% rise in mean length"
that both sessions agreed was too small to explain a 40%→20% decidedness fall was
not small — it was **truncated**. Uncensored the same quantity is
**342s → 790s at 450px**, a factor of 2.3.

⛔ **And there are TWO layers, not one.** Underneath the cap:
**`MatchOutcome.Decided` INCLUDES A DRAW.** `HeadlessMatch.cs:84` says so in as
many words — "a team won, **or the round was drawn**" — and `Rounds.cs` calls
`Decide(ref state, Team.Spectator)` in five places. So `decided` is **not** the
win share's denominator; wins-plus-losses is, and on some maps it is a fraction
of the decided count:

| map | decided | **decisive** | the quoted win share was over |
|---|---|---|---|
| `ctf_classicwell` (control) | 337/400 | **25** | 6% of the arm, not 84% |
| `ctf_orange` | 384/400 | **11** | eleven matches |
| `ctf_conflict` | 187/400 | **18** | eighteen matches |
| `cp_egypt` | 396/400 | **0** | ⛔ the win share is literally 0/0 |

⚠️ **`decided` is a GOOD name, and that is what made it dangerous.** A bad name
would have prompted the lookup. This was not a hidden fact — it was documented on
the line above the field — so the defence that works for the cap layer (add a
column) does not work here. **The only defence that scales is to read the
definition of a field before quoting it in an argument, and it is expensive
enough that nobody will do it every time.** Which is the real argument for the
rule below.

### ⛔ The rule: prefer the statistic with no subset

Not "be careful with filtered statistics" — a label travels and a filter does
not. **A statistic computed over a subset must display the subset's size in the
same row**, never in a caption, because that is the only place a copy-paste
cannot separate them. `PerMapEval` and `RangeSweep` print `capped` and the
decisive count beside every win share now.

✅ **`cp_egypt` is the argument for the rule rather than an example of it.** Its
win share is undefined — 0 decisive matches out of 400 — and **it still casts a
death-share vote and sits inside the 21-of-21 below.** The statistic with no
subset works on a map where the filtered one does not exist.

### ✅ The corrected criterion — 21 maps, 400 an arm, Release, HEAD `679afb8`

⛔ **DEATH SHARE IS BETTER UNDER THE POLICY ON 21 OF 21 MAPS.**
**Sign test p = 4.8e-7 one-sided (9.5e-7 two-sided).** Aggregate **46.3% against
50.2%**.

The death share is the right statistic because **no match is excluded** — drawn,
capped and decisive all contribute their deaths, so neither censoring layer
touches it.

⚠️ **It is not defect-free: the aggregate is a ratio of summed counts and is
therefore LENGTH-WEIGHTED**, and the policy's matches are the long ones. The
unweighted alternative (a mean of per-match ratios) removes the weighting and
**needs a denominator per match**, so a zero-death match has no ratio and would
have to be dropped — reintroducing a filter. ✅ **Measured rather than argued,
12 arms and 2,400 matches: ZERO zero-death matches, and the two estimators agree
to 0.58 points worst case (0.06–0.31 typical) against effects of 4 to 12
points.** The weighting does not bite. **Re-check if the cap ever shortens** —
zero-death is cap-dependent in principle.

⚠️ **The sign test removes the CROSS-map weighting and not the within-map one.**
One vote per map stops a 20-minute `ctf_classicwell` match counting twenty times
a short one *across* maps; each map's own figure is still pooled.

### ⛔ WHICH CLAIM THIS IS — n=1 POLICY

**The criterion is about the shipped artefact**, one checkpoint, and the sign
test licenses exactly that, strongly. ⛔ **It does NOT license "a policy trained
this way beats scripted."** `gg2-rl` measures training variance at a **6.2-point
four-seed spread — larger than the 3.9-point aggregate effect here** — and one
policy says nothing about it. **The 21 maps buy precision; seeds buy
generalisation, and only the first was purchased.** That repo has been wrong
about exactly this twice (the jitter term and `entropy_coef`, both decided on one
seed with disjoint intervals, both reversed on the second).

### The win share, as a per-map diagnostic only

⚠️ **Not 21 verdicts** — 21 significance tests at n=400 is a multiple-comparison
problem wearing the costume of a finer answer. The table finds broken rows; the
aggregate plus the sign test is the claim.

**Ranked on `decisive`, NOT on `decided`** — three rows are not over comparable
populations:

```
gen_destroy      -44 decisive  (-11.0%)  z -3.1   67.5% vs 50.2%   NOT comparable
ctf_truefort     -37 decisive  ( -9.2%)  z -2.6   47.3% vs 39.3%   NOT comparable
ctf_eiger        -11 decisive  ( -2.8%)  z -0.8   90.9% vs 77.3%
```

⛔ **The two rankings disagree, and the worked example is worth more than the
principle.** `ctf_classicwell` ranks **2nd on `decided` (−31) and 16th on
`decisive` (−1)**: the cap layer moved 31 matches on that map and the draw layer
absorbed all but one. Ranking on `decided` sends you to a row that is fine and
**clears `ctf_truefort`, which is not**.

### ✅ What survived the correction, and what it strengthened

- ⛔ **The objective collapse at a wide handover is REAL, not censoring.**
  `ctf_truefort` at 300px is **32.6% win share against a 45.7% control with 69%
  of matches finishing** under a 20-minute cap. `ctf_classicwell` at 450px is
  **4-133, a 2.9% win share, at 31% capped**. On some maps it is total.
- ✅ **`gen_destroy` — flagged as the likely artefact — is where the policy most
  clearly WINS.** At 150px under a 20-minute cap: **124-62 against 93-101,
  z = +3.70**, and the five-minute table could not have said so. Going looking for
  an inflated number found a suppressed one.
- ✅ **Pre-registered stratification: the advantage is REAL AND UNIFORM.**
  Splitting the cap-20 matches at the old 300s boundary, the policy's advantage is
  **+17.6 points among matches the 5-minute runs could already see (z = +2.40)
  and +20.1 points among those only the longer clock revealed (z = +2.83)**. The
  control's own stratum gap is −3.3 points ("long matches are closer matches",
  real, about three points, both arms). Difference-in-differences **+2.5 points**.
  The cap hid a random third; it did not manufacture the advantage.
  ⚠️ **At n=20 the same quantity read 54.5-vs-77.8 and pointed at the wrong
  answer.** Only the control's strata make it readable.
- ⚠️ **`koth_harvest` LOOKS like it runs the other way and does not survive a
  test.** At 150px the advantage is **+15.8 points inside 300s (z = +2.79) and
  +1.9 beyond (z = +0.22)** — which reads as a strong stratum difference. But the
  quantity that claim needs is the **difference in differences**, and that is
  **+14.0 points at z = +1.40**: not significant. `gen_destroy`'s is −2.6 points
  at z = −0.26. **Neither map shows a significant stratum difference**, and the
  two are not distinguishable from each other.
  ⛔ **The first version of this entry said "`koth_harvest` runs the other way"**
  on the strength of two point estimates with no test between them — which is the
  exact failure this page is about, committed while writing the page about it.
  The two per-stratum z-scores are each significant and say nothing about whether
  the strata DIFFER; comparing two significant numbers is not a test of their
  difference. What is true is narrower: **`gen_destroy`'s uniformity is one map's
  answer and nothing here establishes it as the method's.**

### ⚠️ The cap-20 instrument is not neutral either

A longer clock gives a stand-off policy four times as long to convert a
stalemate, and `KillLimit` and `CapLimit` are unchanged — so it does not only
reveal outcomes, it can change **which limit binds**. ✅ **Measured and small**:
on `tdm_mantic` the terminating condition goes from `killlimit=189 timecap=11`
(control) to `killlimit=184 timecap=16` at 150px. Five matches in 200. On
`gen_destroy` and `koth_harvest` every decided match ends on the objective in
every arm, so the risk does not fire there at all.

### ✅ `policy 0px` reproduces the scripted control EXACTLY

A hybrid whose engagement gate never opens is **behaviourally identical** to the
plain scripted bot — same decided count, same death counts to the unit, same mean
length — at n=10, n=200 and n=400. ⚠️ **This was load-bearing for every phase 4
comparison and had never been checked**, and it **could not have been checked by
reasoning**: "a gate that never opens obviously changes nothing" is the kind of
obvious that ships bugs. It rules out the wrapper, the extra `ScriptedBot.Poll`
and the class restriction in one row.

⛔ **Both findings on this page came out of a CONTROL ROW THAT WAS NOT THE
EXPERIMENT.** `policy 0px` was added only to validate that assumption; the
censoring fell out of the same row. **Do not drop a control arm as redundant.**

### Two maps whose behaviour is a property of the map

- ⛔ **`cp_egypt` never produces a winner.** 400 matches an arm, 396 and 400
  decided, **0 decisive**. Any future measurement reading a win rate there reads
  0/0.
- ⛔ **`ctf_classicwell` cannot finish in twenty minutes.** **82% of CONTROL
  matches hit a 20-minute cap** and still count as `decided`, because a capped
  round there is a draw. It will corrupt any measurement that reads `decided`.

### ⚠️ The retracted number, kept because the retraction is the lesson

`gg2-rl` reported censoring rising monotonically with the policy's share of
control across four arms, **p = 0.042**, and withdrew it. The four arms are
**cumulative in construction** — 450px acts everywhere 300px acts and more — so
monotonicity is close to structural, and the test's null is one nobody holds.
⚠️ **Range and control share are not two variables; they are one knob read two
ways**, so "confounded with range" understates it: anything monotone in one is
monotone in all.

✅ **The observation survives and needs no test**: 3%→27% censoring against
0%→35% control share. **What went is the false precision, not the pattern.**

⛔ **THE RULE THAT ACTUALLY WORKED: write the test and its possible outcomes down
before the data exists.** Of everything measured on 2026-09-01, the stratification
is the **only pre-registered test**, and it is the only answer either session
would defend unqualified. Two of the day's written-down predictions were
falsified, **which is the mechanism working**. Everything else was a description
with a p-value attached — a real pattern, correctly computed, decorated with a
number implying a test was at risk when none was. ⚠️ **A p-value on a post-hoc
quantity and one on a pre-registered test PRINT IDENTICALLY.**

### ⚠️ And the trap that was flagged an hour before it was walked into

`Rounds.cs:565` compares the kill limit against `RedCaps`/`BlueCaps`, so **the
same pair means captures in CTF and kills in TDM**. This was written up and sent
to `gg2-rl` as a warning — and then the bench's own terminating-condition
classifier keyed off "which limit is configured", which is not enough, because
both always are. With `CapLimit = 1` every TDM match with a single kill was
labelled `caplimit`, and `tdm_mantic` printed `caplimit=200` in all five arms —
which reads as a clean finding. **The mode has to gate it.** ⚠️ **Knowing a field
is overloaded does not stop you reading it.**

## Phase 4 handover from `gg2-rl` (2026-08-31) — the artefact, and the hole in it

**Received, nothing built yet.** The duel work is done and a policy exists:
`checkpoints/vlong_s0/ppo_final.npz`, 19,355 parameters, action repeat 4, layout
v1, encoding v2, `SIM_UUID 7ff5940d97aa7bed` — **this head, no migration**.

⚠️ **Quote 54.1% / 53.8%, not the artefact's own 58.7% / 57.7%.** The second pair
carries selection over two seeds; the first is the four-seed mean at the standard
1200 iterations. The checkpoint was chosen by a rule fixed before looking — the
final checkpoint of the longest run — and `gg2-rl` explicitly declined its own
iteration 1600, which scores *better* on held-out maps, because picking it would
turn the evaluation into a training signal. **The best checkpoint is the right
thing to ship and the wrong thing to quote.** Phase 2's clone is 26.6% / 24.9%.

The architecture, all measured there: **scripted layer owns movement outside
engagement range, the policy owns in-range combat, handover at 300px
straight-line**, costing ~1.7 points. ⛔ **Do not widen past ~400px** — the
observation saturates (goal distance clamps at `min(d/400, 1)`, rays reach 320px,
the walkmask patch covers 128px), so beyond that the policy is being given the
wheel at a range its input cannot describe. ⚠️ **Do not hand movement over
entirely** either: the policy's own movement is worth ~19.5 points over walking
straight at the enemy. Goal kind stays **0**; a target point with kind 0 is the
usable half.

### ⛔ Five of the nine classes are untrained input, and this repo makes it worse

Training used **scout, soldier, sniper, heavy**. The other five class columns
were constant zero for every decision of every run, self block *and* enemy block,
so their first-layer weights are still the random He-normal draw — byte-identical
between iteration 400 and 2400. **Setting one does not inform the policy, it
injects noise.** Same failure as the goal kinds, found the same way.

⚠️ **The untrained columns are 3, 4, 5, 7 and 8 — interleaved, not a tail.**
`PlayerClass` is scout 0, soldier 1, sniper 2, demoman 3, medic 4, engineer 5,
**heavy 6**, spy 7, pyro 8. Anything that assumes "the first four" or a
contiguous trained prefix is wrong.

⛔ **And a default headless match is 50% untrained classes.**
`BotPopulation.PickClass` draws uniformly from scout..pyro and rejects only the
spy — eight classes, of which four are trained. **It does not consult
`ClassLimits` at all**, and `HeadlessMatch` calls it directly. So:

- `gg2-rl`'s proposed first experiment — restrict both teams to the four trained
  classes — **is not expressible today.** It needs a class-set parameter on the
  population picker; `ClassLimits` is the wrong lever because nothing in the bot
  path reads it.
- Even the four-class bot **cannot recognise** a nine-class opponent, and half of
  every default match's roster would be a class it has never seen. It still gets
  position, velocity, health and aim, which are trained columns, so it may cope —
  **but it has never once been measured against an enemy it cannot classify.**

### The exit criterion has a half that is not about winning

⚠️ **"200 matches completed" is not a criterion** — `eval` reported arena matches
as clean wins for two phases because five *instant* rounds reach a cap exactly as
five real ones do (`MatchActuallyHappensTests` exists for this). Every match must
assert **duration and deaths** beside the win; a match with no deaths is a failed
match, not a fast one. And the second half is not optional: **no map may produce
a stuck bot or a pathing regression.** A duel-strong bot that cannot leave spawn
is worse than what ships today.

⚠️ **The caveat on all of it**, in `gg2-rl`'s words: this policy has only ever
played 1v1 duels, 0-300px apart, four classes, no respawns, no objective. A CTF
match is none of those. The observation has two enemy slots and duels only ever
filled one.

**Out of scope by decision**: the game objective. The reward stays damage dealt
minus taken with no objective term — their user's call, on human-likeness
grounds. The macro layer owns objectives; the policy never pursues them.

⚠️ **Building this reopened `docs/ENV.md`'s "no ML runtime, no inference path, no
weights format"** — flagged before starting so the decision was taken rather than
drifted into. ✅ The user took it (*"Full phase 4"*), and the list is struck
through under "Out of scope".

## Goal conditioning — `gg2-rl` phase 4's hybrid boundary (2026-08-30)

**Asked for by `gg2-rl`**, and cheap for one reason: the goal block was reserved
on day one. `WriteGoal`'s original comment — *"a macro goal the policy has never
seen cannot be introduced later without a layout bump and a retrain, and a
constant column costs four floats"* — is what turned phase 4's ask into a struct
instead of a retrain of 83 checkpoints. **It is the clearest case in either repo
of an early decision paying for itself**, and it is recorded here because the
next four-float column that looks like waste will look exactly the same.

The shape, which both repos landed on independently:

- `AgentGoal` — a kind (index into `GoalKinds`) and an optional target point.
- `DuelEnv.SetGoal(slot, goal)` / `GoalFor(slot)`, per roster slot.
- `EnvMessage.SetGoal` on the wire: world (or `-1` for all), side (0 red, 1
  blue), kind, hasTarget, x, y. The reply is the ordinary observation block, so a
  caller sees the goal reflected without having to step.
- **`default` is exactly the old behaviour**, so an unchanged caller writes an
  unchanged observation.

⚠️ **`ObservationWriter.LayoutVersion` did NOT move and `SIM_UUID` did not
either.** Verified the same way the performance work was: observations digested
over 4,012,800 floats on three maps, `4FFEC587…` / `F6763969…` / `2DDB39FB…`,
identical to the pre-goal build. `gg2-rl`'s words: *"if the default path ever
stops being byte-identical, that is a layout bump and a retrain, and I would much
rather have an uglier API than a silent one."* The ugly half is the optional
parameter with a meaningful zero. **If `ObservationGoalTests`' first test goes
red, that is a retrain, not a test to update.**

⚠️ **No protocol version bump either, on `EnvMessage.Candidates`'s precedent**:
no existing message's body or reply moves by a byte, so a client that never sends
`SetGoal` is unaffected. `gg2-rl` had a pool sweep running when this landed and a
bump would have refused its handshake over a feature it was not using.
`EnvProtocolTests.AGoalUnawareClientIsUnaffected` states that rather than leaving
it implied by an absence.

### ⚠️ The two decisions inside it that could have gone the other way

**A goal survives the auto-reset.** `EnvBatch.Step` resets a finished world
*inside* the call, so a trainer has no moment at which to re-set a goal — it
learns the episode ended by reading the reply, after the next episode has already
begun. Clearing goals on reset would mean a trainer that set one once silently
trained on "engage" labels from the second episode onward, which is this
project's recurring failure: **the bug that still trains**. So a goal is a
property of the slot, not of the episode. `ObservationGoalTests.AGoalSurvivesAReset`.

⚠️ **`gg2-rl` supplied the stronger form of that argument and it is the one to
remember**, because the surface reading — "the first observation after a reset can
carry a stale goal" — *looks* exactly like this project's auto-reset traps and is
not one. **The goal is IN the observation.** So if an episode's first decision
carries the previous episode's goal, the policy was conditioned on precisely what
the observation said it was conditioned on, and the reward is whatever actually
happened. Observation and action stay consistent; nothing is mislabelled. That is
what makes the persistence safe, rather than merely convenient.

⚠️ **What it does leave is a distributional wrinkle, and it is the trainer's to
fix.** Training will want goals to vary per episode, and the first decision of
each will be conditioned on the previous goal — one decision in a hundred-odd.
`gg2-rl`'s own remedy if it ever matters is to sample the next goal on seeing
`world_done`, **not** a protocol change here.

**An out-of-range kind is refused, not clamped.** `OneHot` writes *no column* for
one, which reaches the policy as "no goal" rather than as an error — and clamping
to 0 would train on "engage" while the trainer believed it had asked for
something else. The all-worlds form validates **once, before the loop**, so a bad
call cannot set half a batch and then refuse.

### What is NOT built

The goal is **set from outside** — nothing in this repo decides one. Phase 4's
scripted-layer goal emitter (reach node N, hold position) is `gg2-rl`'s, and the
full-CTF match-play exit criterion (`docs/BOTAI.md` item 8, policy as
`IInputSource`) is not started; `gg2-rl` asked for duels first and said it would
bring a policy worth wiring in.

## Build log

### ✅ Step 2 — `DuelEnv` and `ActionDecoder` (2026-08-28)

`src/Gg2.Env`, referencing `Gg2.Bots` for `ScenarioWorld` and **not**
`Gg2.Server`: an env is not a game server, and the seam's whole claim is that a
policy can be stepped without a socket.

**What landed**, gated by `ActionDecoderTests` (8) and `DuelEnvTests` (12):

- `EnvAction` — the five heads as indices, so "left and right at once" is not
  expressible. The reference cancels that case to "not controlling" anyway
  (`Begin Step.xml:249`), and letting a policy spend probability mass on it
  teaches it nothing.
- `ActionDecoder` — jump on the first tick of a decision only; the mask built
  fresh every tick; the turn applied once per **decision**; the angle through
  `PlayerInput.FromAim` so it lands on the wire's 65,536 steps.
- `DuelEnv` — reset, step with action repeat, termination on death or the
  decision cap, and a raw-bytes snapshot that carries the decoder's aim.

⚠️ **The cap counts DECISIONS, the repeat counts TICKS.** A cap in ticks would
silently change episode length when the repeat changed, so a trainer comparing
two repeat settings would be comparing two different episode budgets.

⚠️ **The turn is applied once per decision, not once per tick.** Repeating it
across a repeat of k makes the effective turn-rate cap k times larger and
quietly undoes the honesty property the cap exists for — the policy could spin
four times faster purely because the trainer chose a longer repeat.

⚠️ **Damage is summed across the repeat's ticks.** `TickEvents` is cleared every
tick, so reading it after the loop reports the last tick's damage and drops the
rest — at a repeat of 4 that is three quarters of the signal, lost in a way that
*still trains*.

⚠️ **Red is slot 1 and blue is slot 2, not 0 and 1.** Slot 0 is the server's own
reserved player and always exists; `ScenarioWorld` starts its counter at 1 for
the same reason a headless server does. The first draft of the test asserted 0
and was asserting the one thing CLAUDE.md says never to assume.

⚠️ **The seed is `uint`, not `ulong`.** GML's generator has a 32-bit seed;
accepting 64 would throw away half of whatever a trainer sent without saying so.

**Not done in this step**, and each is its own step: the observation writer, the
stamp bump, `EnvBatch`, and the socket host.

### ✅ Step 3 — `ObservationWriter` v0 and the stamp bump (2026-08-28)

`Gg2.Env.ObservationWriter` is the authoritative field order; the table earlier
in this document describes the shape. **`ObservationWriterTests` (9)** holds the
three gates this document asked for — zero allocation, bounded values, advertised
length — plus two that would otherwise be silent.

✅ **The stamp is bumped**: `ObservationLayoutVersion` 0 → 1, and the pinned
digest went `99b7ab3141e31fb4` → `08dc7b09303b3843`. **It invalidated no
checkpoints, because none exist** — which is exactly why this was scheduled
before `gg2-rl` phase 2. The bump is free precisely once.

⚠️ **The version is MIRRORED, and the compiler cannot hold the two copies
equal.** `Gg2.Simulation` must not depend on `Gg2.Env` — the simulation is the
thing an env observes, not the reverse — so
`ObservationWriterTests.TheStampMirrorsTheWritersLayoutVersion` is the only thing
standing between a layout change and a stamp that did not move with it. That is
the exact failure the stamp exists to prevent, so it is worth knowing it rests on
one test.

⚠️ **Nine class columns, not ten: the Quote has none, and is REFUSED.** Encoding
it would set no column at all, which is indistinguishable from an empty slot —
so a Quote throws rather than being observed as nothing.

⚠️ **A scale is part of the layout.** `PositionScale`, `VelocityScale`,
`RayRange`, `PatchCell` and the projectile radius all decide what the numbers
mean; changing one is a layout bump exactly like changing a count.

⚠️ **The rays are MARCHED, not asked of `CollisionLine`.** That primitive
answers "is anything in the way", and a policy needs *how far* — a different
question, and not one the simulation ever has to answer.

**Two v0 shortcuts, both deliberate and both carrying their column now** so the
layout does not churn when they arrive: `time-since-seen` is a fixed 0 (the
reaction-delay ring buffer is `gg2-rl` phase 5), and the macro goal is a constant
"engage" (the hybrid-boundary rule). A constant column costs one float; a bump
costs a retrain.

### ✅ Step 4 — `EnvBatch` (2026-08-28)

N duels stepped together, each world owning its own `DuelEnv`, `CollisionWorld`,
RNG stream and decoder — the same shape `MatchRunner` uses, and what makes the
parallelism safe without a lock. **`EnvBatchTests` (7).**

✅ **Both gates are compared WHOLE, as this document asked**: world *i* is
identical at batch 1 and batch 64, and identical at 1, 2 and 4 threads — every
world, every step, every reward component. A comparison of summary statistics
would pass a batch that produced the right multiset in the wrong order.

⚠️ **The seed is a pure function of (base, world, EPISODE), and the episode part
was a real bug.** The first draft derived the next episode's seed from the
finished episode's **decision count** — so two episodes lasting the same number
of decisions, which with a fixed cap is nearly all of them, got the same seed and
the world **replayed one trajectory for ever**. A batch training on a single
repeated episode still produces a falling loss and a useless policy. Caught by
`EachEpisodeInAWorldGetsItsOwnSeed`.

⚠️ **The auto-reset happens AFTER the results are recorded**, so the terminal
step still carries its components. Resetting first would hand back the fresh
episode's zeros as though the death earned nothing, which is the one transition a
policy most needs.

⚠️ **A world's next episode is seeded from its OWN counter, never a shared one.**
A shared counter would make the next episode depend on how many *other* worlds
had finished — precisely the batch-size dependence the gates forbid, and it would
have passed the batch-size gate because that gate compares aligned steps.

⚠️ **`TheRunActuallyCrossesEpisodeBoundaries` is the control the file needs
most.** Determinism gates are easy to pass when nothing happens; without it every
assertion above would be a statement about one episode that never ended.

⚠️ **Threads default to 1, not `Environment.ProcessorCount`.** A default that
varied with the machine would make "the same however many threads" a claim nobody
was testing on their own box.

### ✅ Step 5 — the socket host, and v0's exit (2026-08-28)

`gg2-env serve --port <n>` and `gg2-env smoke`. **`EnvProtocolTests` (7)** drives
the real message loop over a **real socket**, and **`EnvSmokeTests` (2)** runs
the random-head agent.

✅ **All three v0 exit criteria are met**: a random-head agent completed
**1,024 duels** over the socket with no crash and no hang; every gate above is
green; and the throughput is measured, named by build, in the table above.

⚠️ **A real socket, not a `MemoryStream`, and that is the point of the gate.**
Framing bugs hide behind a stream that always returns everything asked for and
reappear the first time a frame is split across two reads —
`AFrameSplitAcrossTwoWritesIsStillOneMessage` sends a header and a body in two
writes with a pause between them.

⚠️ **A refusal is a message, never a dropped connection.** A trainer that saw
the socket close would report "the env died" and the reason would be in nobody's
log. The gate checks the connection is still usable afterwards, which is the
half that makes it a refusal rather than a crash that happened to be caught.

⚠️ **The frame length is capped before the buffer is allocated.** A length prefix
read off a socket is attacker-controlled input even on loopback: a mis-framed
client sends four bytes that mean nothing, and `new byte[thatLength]` turns a
mis-frame into an out-of-memory instead of an error message.

⚠️ **A `ReadOnlySpan<byte>` cannot be captured by a local function**, so the
message readers use an explicit cursor. The compiler says so, which is the good
outcome — the tidier design copies each frame to an array first and allocates on
every message.

**Two bugs the gates caught in this step**, both mine: `Reset` called
`DuelEnv.Reset(default, …)`, which compiles and resets a world to an *empty*
spec with no map and no classes; and the smoke run's step ceiling was
unreachable by construction, so the guard against a hang could never have been
observed to work. The ceiling is now a parameter for exactly that reason — **a
safety net nobody has watched trip is indistinguishable from one that does not
work**.
