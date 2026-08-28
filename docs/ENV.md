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
| `MakeBatch` | N, map, per-side classes, spawn placement (explicit positions, or **NaN** for sampled — see below), base seed, tick cap, action repeat k | ack + initial observations |
| `Step` | N action tuples (one per world: 4 head indices + aim-delta bin) | N × (obs block, reward components, done, termination reason) |
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

**Auto-reset on termination**: a world whose episode ends is reset within the
same `Step` call (seeded from its own stream) and returns the fresh episode's
first observation alongside the terminal reward components. Round-trip economy;
explicit `Reset` remains for forcing a new scenario.

World `i`'s seed derives from the base seed and `i`, so batch size cannot move
any world's stream.

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

## Exit (v0)

1. An in-repo random-head agent completes 1,000 duels over the real socket
   protocol: no crash, no hang, every episode terminates within its tick cap.
2. Every gate above is green.
3. Throughput measured and recorded **here** — decision steps per second at
   batch 1 and batch 64, naming the build configuration. `gg2-rl` phase 0
   stops the whole programme below ~20k steps/sec, so this number is the one
   that decides whether shared memory ever gets built.

## Out of scope, so nobody re-opens it

No shared memory until the throughput number above justifies it. No ML runtime,
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
