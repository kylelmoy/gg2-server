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
| Macro goal | direction sin/cos, distance, goal kind one-hot — constant "engage" in a duel, present from day one per the hybrid-boundary rule |

Extraction allocates nothing and never enumerates the whole world
(`docs/BOTAI.md` item 6 — `NearestEnemies` / `ProjectilesWithin` are the
queries). Every count above is v0-tunable; a change is a layout bump.

## Reward components (per world, per step)

`damageDealt` (f32), `damageTaken` (f32), `kills` (u8), `deaths` (u8),
`termination` (u8: running / death / tick cap). Accumulated over the action
repeat's k ticks. The jitter penalty stays trainer-side — it is a function of
the actions, which the trainer already has.

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

**Serialising the bot layer**, which is what `Snapshot` with a bot-driven slot
would need — refused explicitly instead. No shared memory until the throughput number above justifies it. No ML runtime,
no inference path, no weights format (that is `gg2-rl` phase 2's arrival). No
rollout-search bot (`Gg2.Bots`, `gg2-rl` phase 1). No full-match episodes. No
reaction-delay model (phase 5 — only its observation field exists).

## Measurements

| Measurement | Value | Build | When |
|---|---|---|---|
| Decision steps/sec, batch 1 | **8,753** | Release | 2026-08-28 |
| Decision steps/sec, batch 64 | **34,073** | Release | 2026-08-28 |
| Decision steps/sec, batch 64, 2 threads | 36,173 | Release | 2026-08-28 |
| Decision steps/sec, batch 64, 8 threads | 37,592 | Release | 2026-08-28 |
| `ObservationWriter.Write`, one call | **~5.0 µs** | Release | 2026-08-28 |
| Sixteen of them — `gg2-rl` phase 2's budget unit | **79 µs, 7.9% of 1 ms** | Release | 2026-08-28 |

### Observation extraction, measured because somebody was about to design around it

⚠️ **Asked for by `gg2-rl`**, which had measured the *other* half of phase 2's
criterion — a 3-layer MLP, 209 inputs to 25 logits, at 13.6 µs for sixteen — and
noticed that the half it could not measure was mine. Its criterion is **1 ms for
16 bots including observation extraction**, and extraction runs per bot per
decision.

**The answer: extraction dominates inference by about seven to one**, and the
budget is still comfortable. 79 µs of extraction plus ~14 µs of inference is
about **9% of 1 ms**, so phase 2 has an order of magnitude in hand.

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
part. The simulation is not the bottleneck; the boundary is. Every step writes
`worlds × 2 × 209` floats and serialises them one `BinaryPrimitives` call at a
time in `EnvServer.Observations`, single-threaded, which is ~107 KB per step at
batch 64. **If throughput ever needs to go up, that loop is the lever and more
threads are not** — and shared memory would attack exactly this.

⚠️ **Both figures name their build, and the CLI prints it with the number.**
CLAUDE.md's standing rule: this project's headroom figure sat stale in four
documents because a re-measure forgot to say which configuration it was, and
Debug reads about half of Release.

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
