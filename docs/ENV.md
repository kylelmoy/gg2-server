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
| `MakeBatch` | N, map, per-side classes, spawn placement (explicit positions or sampled), base seed, tick cap, action repeat k | ack + initial observations |
| `Step` | N action tuples (one per world: 4 head indices + aim-delta bin) | N × (obs block, reward components, done, termination reason) |
| `Reset` | world index, optional new seed | that world's fresh observation |
| `Snapshot` | world index | the world's state bytes |
| `Restore` | world index, state bytes | ack |
| `Close` | — | ack |

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
| Decision steps/sec, batch 1 | — | — | at exit |
| Decision steps/sec, batch 64 | — | — | at exit |
