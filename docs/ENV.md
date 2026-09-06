# `Gg2.Env` — the batch-stepping surface

The design record for `docs/BOTAI.md` item 10. The consumer is `gg2-rl`
(`D:\code\gg2-rl`): its `docs/CONTRACT.md` names what must cross the seam, and
its `docs/DESIGN.md` is where the observation and action layouts below come
from.

## Scope

- **The v0 observation layout is fixed and version-stamped**, folded into
  `SIM_UUID`.
- **The env accepts multi-discrete heads; the head→wire conversion, including
  the aim-delta turn-rate cap, is server-side.** The cap is an honesty
  property that must not be re-implementable differently per trainer, and the
  delta needs the current aim, which the server holds.
- **Rewards go out as components, not a scalar** — damage dealt, damage
  taken, kills, deaths, termination reason — a fixed contiguous struct per
  world per step. The trainer folds them into a scalar, so reward iteration
  never needs a C# rebuild.
- **v0 is duels only**, wrapping `ScenarioWorld`. Full matches through the
  same protocol are not built; `gg2-rl` asked for duels first.

## Damage accumulators in `TickEvents`

`SlotBuffer<double> DamageDealt` and `DamageTaken`, cleared by `ClearOutputs`,
written where character damage is applied.

- Attribution is **credited at application time**, following
  `LastDamageDealer` semantics — `DamageApi.Deal` does not know the dealer, so
  it is accumulated at the call sites.
- **Blocked damage accumulates nothing** — `Deal` returning false (Team
  Deathmatch invulnerability) means no amount landed.
- **Self-damage counts as taken, never as dealt.** A rocket jump must not
  score positive reward.
- Sentry and generator damage do not route through `Deal` and are **out of
  v0** — duels have no objective.

## Architecture

`src/Gg2.Env`, a console project in the `Gg2.Parity` mould (`gg2-env`), with
its types usable as a library by tests:

- **`DuelEnv`** — one world: `Reset(spec)`, `Step(actions)` with action
  repeat, termination on death or tick cap, snapshot/restore via a raw-bytes
  round-trip. Wraps `ScenarioWorld`.
- **`EnvBatch`** — N `DuelEnv`s stepped in one call, parallel the way
  `MatchRunner` is: each world owns its own `CollisionWorld` and RNG, results
  identical regardless of batch size or thread count, gated by whole-array
  comparison rather than statistics.
- **`ActionDecoder`** — heads → `PlayerInput`, per slot, stateful only in the
  tracked aim angle.
- **`ObservationWriter`** — the v0 layout, filled into a caller-supplied
  `Span<float>`, no allocation, built on `WorldView`'s nearest-K queries.
- **`gg2-env serve --port <n>`** — the socket host. Loopback only, one
  client, like the control port.

## Protocol (current)

Length-prefixed binary frames on a local TCP socket: `u32` little-endian
length, one message-kind byte, payload. Request/response. `EnvProtocol.Version`
is 8; a client whose version does not match is refused at `Hello`, never
tolerated. `MakeBatchFixedBytes` (128) moves together with `Version` in the
same commit, gated by
`EnvProtocolTests.TheVersionAndTheFrameLayoutMoveTogether` — the two must
never be able to disagree.

| Message | Carries | Returns |
|---|---|---|
| `Hello` | client's protocol version | protocol version, `SIM_UUID`, obs layout version, action encoding version, obs length, head shape |
| `MakeBatch` | N, map, per-side classes, spawn placement (explicit positions, or **NaN** for sampled), base seed, tick cap, action repeat k, per-side driver and skill tier, per-side rollout opponent, separation band (min, max), per-side commitment ticks and continuation policy handle | ack + initial observations |
| `LoadPolicy` (13) | a checkpoint path | handle (1-based; 0 means "no continuation") + the `SIM_UUID` it was checked against |
| `Step` | N action tuples (one per world: 4 head indices + aim-delta bin) | N × (obs block, reward components, done, termination reason, the action that side actually took + whether it is reproducible) |
| `StepRestricted` (11) | `Step`'s payload plus, per world, a restriction to a subset of a rollout side's candidate indices for that decision only | same as `Step` |
| `StepScored` / `StepScoredRestricted` (12) | opt-in variant of the above | adds, per shadow-searched side, index/score pairs over the evaluated candidate subset and the action the search actually chose |
| `Reset` | world index, optional new seed | that world's fresh observation |
| `Snapshot` | world index | the world's state bytes — **refused while a bot drives either slot** |
| `Restore` | world index, state bytes | ack |
| `SetGoal` (10) | world index (or `-1` for all), side (0 red, 1 blue), goal kind, has-target flag, target x, y | the ordinary observation block, so a caller sees the goal reflected without stepping |
| `Candidates` | — (asked once per batch, since a duel's classes are fixed by its spec) | red's candidate set then blue's, each a count and up to six `i32` per action; a non-rollout side reports an explicit zero |
| `Close` | — | ack |

The reward-components block carries the action a driven side actually took
plus an exactness flag rather than requiring the trainer to reconstruct it —
see "Per-slot drivers" below for why that action can be a lossy approximation
of what the bot really did.

### Sampled spawn placement and the separation band

A coordinate of **`NaN`** (`DuelSpec.Sampled`) means "draw a spawn point off
the map" — `state.Random.Next(count)`, the same stream and call a real
respawn uses, so a sampled duel starts where an ordinary spawn would have put
somebody. Half a sampled pair (one NaN, one explicit) is refused rather than
silently placing a character at NaN. The RNG stream is seeded before
placement, and facing is read off the placed positions rather than off the
spec (both agents face each other; a sampled spec holds two NaNs, so this
matters).

**Most maps don't put spawn-sampled duelists in engagement range of each
other** — several put each team behind its own gate, so a follower correctly
finds no route and neither side moves. `DuelSpec.MaxSeparation` (with
`MinSeparation`) fixes this: when set, placement draws from the nav graph's
gate-filtered walkable nodes instead of spawn points, paired inside
`[MinSeparation, MaxSeparation]` (straight-line distance, not path length —
what a weapon and a rollout horizon reach). Both a band's placements must be
sampled, and a minimum without a maximum does not switch banding on — all
three are refused rather than downgraded, on the same argument as the
half-NaN rule.

**Auto-reset on termination**: a world whose episode ends is reset within the
same `Step` call (seeded from its own stream) and returns the fresh episode's
first observation alongside the terminal reward components; explicit `Reset`
remains for forcing a new scenario. Auto-reset happens *after* results are
recorded, so the terminal step still carries its own components rather than
the fresh episode's zeros.

### The seeding guarantee

1. **A world's seed is a pure function of the base seed and its index.**
   World 7 of a batch of 64 is bit-identical to world 7 of a batch of 8.
2. **The index is mixed in, not added** — `seed + index * 2654435761`. So two
   batches one seed apart share no world at all; plain addition would make
   `{seed 1, count 100}` and `{seed 2, count 100}` share 99 of 100 worlds.
3. **An episode's seed also mixes its episode number**, drawn from the
   world's own counter (never a shared one), so a world that auto-resets does
   not replay its own first episode and batch size cannot move it.

### Per-slot drivers

| Driver | What it is |
|---|---|
| `Client` (0) | the action arrives over the socket — the default, and what a past checkpoint is: the trainer holds the weights and drives that slot itself |
| `Idle` (1) | presses nothing and goes on looking where it was — the comparison floor |
| `Scripted` (2) | `ScriptedBot` at a tier 0-5. Refused without a nav graph, not downgraded |
| `Rollout` (3) | `RolloutSearch`. ~2.9 ms a decision against a scripted bot's microseconds — an evaluation baseline, not a collection opponent |

Each bot drives its slot through its own `IInputSource.Poll` on its own
cadence, so the opponent is really that bot rather than an imitation squeezed
through the action space. The reported action is therefore the *nearest*
action, not necessarily a reproducible one: the four discrete heads are exact
(recovered from the key mask bits the decoder writes), but the aim head can
saturate — a scripted bot's turn has no cap and can exceed the ±30° per
decision the 15 aim bins cover. Measured (scripted vs scripted, three seeds):
**86-89% of actions are exact**, and `ActionExact` reports it per step.

`Snapshot` is refused while a bot drives a slot: `WorldState` holds no
reference to anything, but a bot is state outside it — `ScriptedBot` carries
a route, a target, four clocks and its own RNG stream, `RolloutSearch` a
decision countdown and a decoder. Restoring the world alone would resume with
an opponent mid-plan for a world that no longer exists.

**A duel runs the deathmatch branch of `BotObjective`**, so a scripted bot in
a duel walks and fights the same way one does in `tdm_mantic` rather than
standing still (a duel has no gamemode of its own — `docs/BOTAI.md` item 11).
`RolloutOpponent`'s zero value is `HoldLastInput`, not `Idle` — declaration
order is load-bearing for any enum riding in a `default`-able struct, since
"unset" and "explicitly first-declared" are the same zero.

### Protocol v8 — the policy continuation

`LoadPolicy` (message kind 13, not 10 — 10 is `SetGoal`) lets the rollout
search keep **acting** past its commitment window under a trained policy
instead of going neutral: `MakeBatch` carries per-side commit-tick counts (0
= the whole horizon) and a continuation policy handle (0 = neutral). The
handle is resolved to a `PolicyNetwork` inside `EnvServer` and does not reach
`DuelSpec`, which carries the resolved policy instead — one source of truth
per fact. An unknown handle is refused at the protocol edge rather than
silently resolving to the neutral continuation.

A continuation runs **inside the search's fork**, k times per candidate per
decision, in worlds the socket never sees — the first thing in the protocol
that requires a policy in-process rather than driving a slot from outside.
`RolloutOptions.WithDefaults` refuses a commitment out of phase with the
decision cadence (`CommitTicks % DecisionTicks == 0`) at the point the value
is set, except when the commitment covers the whole horizon, in which case no
continuation runs and there is nothing to be out of phase.

Cost is cadence-dependent: continuing every tick costs more than the search
it replaces (3.48ms vs 2.45ms at k=20/horizon 20, population 2), while
continuing on the search's own decision cadence (`DecisionTicks`, default 4 —
also the cadence the shipped policy was trained at) is 1.67x cheaper. A
population term dominates both: a rollout re-simulates every live character
per candidate-tick, so the same decision costs roughly 3x more at population
12 than at population 2 — quoted duel numbers should not be read into a
match without re-measuring at that population.

Measured result (one checkpoint, k=30, commit 4, horizon 20, against an
unpruned search): the continuation removes most of the blindness a short
commitment introduces but returns almost none of the win share the short
commitment cost, and in a head-to-head against the unpruned search it loses
by nineteen points at a *lower* per-decision cost. Blindness and search
strength are close to independent on this evidence. This does not establish
that a policy continuation cannot work in general — one policy, one
configuration, untested elsewhere — only that this one did not help.

**`SIM_UUID` does not move for any of this.** It is deliberately blind to the
bot layer, and a continuation is a bot decision.

### The shadow search slot and expert iteration

A per-side "shadow" flag (`DuelSpec.RedShadowSearch` / `BlueShadowSearch`)
constructs a `RolloutSearch` for a slot without installing it as the input
source: the client drives the slot, the search runs alongside and reports
what it would have done (polled after the action repeat, before observations
are written), for DAgger-style aggregation. Reported through the opt-in
`StepScored` block, extended with the search's argmax action sent explicitly
(not left to be derived from the distribution's index or ordering — the
distribution over candidates is a set, not a ranking, and nothing on either
side of the seam may assume otherwise). A shadow always searches at
`DecisionTicks: 1`, since the default cadence-4 would repeat and relabel the
same answer three times out of four. Refused on a non-`Client` side rather
than doubling a search whose answer the first already reports.

`gg2-rl` ran an expert-iteration experiment on this seam: distilling on
states the trained policy itself visited, aggregated with search-driven
control data. It improved imitation-metric agreement on every held-out set
and **cost 7.2 points of win rate** — imitation accuracy and play strength
came apart. `gg2-rl` stopped pursuing behaviour cloning of this search on
that result and moved to self-play from the distilled policy instead. The
checkpoint this was measured against predates the current build and is now a
test fixture (below); none of these numbers describe anything currently
running.

### `EnvMessage.Candidates` and the restriction messages

`Candidates` answers, once per batch, each side's candidate-index set (empty
meaning "search everything"); `BuildCandidates` is a pure function of
`(options, class)`, so an index is stable for the batch — except that only a
sniper carries the zoom dimension, so the set size is 60 for every other
class and 120 for a sniper, and an implementation must validate an index
against the *current* class's count and refuse an out-of-range one rather
than clamp it. (A sniper's candidate count was briefly wrong on decision 0
only, because the validator read an instance field that had not yet been
rebuilt for the class; fixed by making `DuelEnv.TrySearchShape` the one place
that decides a slot's candidate-set shape, gated by
`EnvProtocolTests.ASniperIsRestrictableOnStepZero` and
`RolloutRestrictionTests.EveryCountSourceAgrees`.)

`StepRestricted` / `StepScoredRestricted` are separate message kinds rather
than a tail on `Step`, because `EnvServer.Step` already tolerates and ignores
trailing bytes — a client sending restrictions to a server that does not
understand them would be silently unrestricted rather than refused. No
protocol version bump was needed for either message, on the same precedent as
`Candidates`: no existing message's body or reply changed.

Design invariants, each mutation-gated: a restriction produces a decision
that is a pure function of the restricted *set* (not of client array order —
the tie-break is lexicographic and most non-blind decisions have their top
two candidates tied); a stale restriction mask is dropped on a class change
(an index means a different action in the 60-list and the 120-list);
`LastDecisionTicksSimulated` counts what was actually searched; a restriction
is consumed by exactly one decision; all worlds' restrictions are validated
before any are applied, so one bad index does not leave earlier worlds
pruned with later ones refused.

The pruned candidate set sent to a search must not be purely the policy's
top-k — a search that only evaluates what a policy already favours, feeding
a policy trained on what the search evaluated, is self-confirming and cannot
correct where it is wrong. The convention is top-(k−m) plus m sampled from
the remainder, so the exploration sample also doubles as a monitor: if the
sampled-from-the-remainder candidates keep outscoring the top-k ones, the
prior is bad and pruning is hurting, computable every round at no cost.

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
- **The mask is written absolutely every tick**, never OR-ed.
- Aim: the decoder tracks an angle per slot, applies the capped delta, and
  quantises through `PlayerInput.FromAim` — the same 65,536 steps a client
  uses.
- `aimDistance` is a fixed constant in v0; a change to it or to the turn-rate
  cap's value is an encoding-version bump.

## Observation layout v0 (stamped)

Egocentric, normalised, fixed-size `float32`. The authoritative field order
is `ObservationWriter`; this is the shape:

| Block | Contents |
|---|---|
| Self | class one-hot (9), hp fraction, velocity, grounded, aim sin/cos, ammo fraction, reload fraction, **ready-to-shoot flag, refire fraction**, special-resource fraction |
| Enemies, nearest 2 | presence, relative position and velocity, class one-hot, hp fraction, aim sin/cos, time-since-seen (fixed 0 in v0 — the reaction-delay ring buffer is `gg2-rl` phase 5; the field exists now so the layout does not churn then) |
| Projectiles, nearest 4 | presence, relative position and velocity, kind one-hot (9 — the *spawnable* kinds, `Shot` through `Stab`; `None` has no column), mine/enemy flag — genuinely nearest via `ProjectilesWithin`, which picks rather than truncates, with equal distances breaking to creation order |
| Geometry | 24-ray fan of normalised distances (marched, not asked of `CollisionLine` — a policy needs *how far*, not just whether something is in the way) plus an 8×8 downsampled walkmask patch centred on self |
| Macro goal | direction sin/cos, distance, goal kind one-hot — settable per slot (`AgentGoal`, `EnvMessage.SetGoal`); default is "engage the nearest enemy" |

Extraction allocates nothing and never enumerates the whole world
(`docs/BOTAI.md` item 6 — `NearestEnemies` / `ProjectilesWithin` are the
queries). Every count above is v0-tunable; a change is a layout bump. Only
nine class columns exist (the Quote has none, and is refused rather than
silently observed as an empty slot).

**Only four of nine classes (scout, soldier, sniper, heavy) have ever
appeared in training data.** The other five class columns (demoman, medic,
engineer, spy, pyro — interleaved at indices 3, 4, 5, 7, 8, not a contiguous
block) are constant zero in every training run to date, so their input
weights are whatever they started as; setting one does not inform whatever
policy consumes it, it injects noise. `BotPopulation.PickClass` draws
uniformly from all eight non-spy classes and does not consult `ClassLimits`,
so a default headless match's roster is about half untrained classes by
default — restricting a match to trained classes needs a class-set parameter
this repo does not currently expose.

## Reward components (per world, per step)

`damageDealt` (f32), `damageTaken` (f32), `kills` (u8), `deaths` (u8),
`termination` (u8: running / death / tick cap). Accumulated over the action
repeat's k ticks. The jitter penalty stays trainer-side — it is a function of
the actions, which the trainer already has.

## `SIM_UUID` does not cover the bot layer

The stamp covers physics constants, the class tables, the action encoding,
the observation layout and a hand-bumped `SimulationBehaviourVersion`. It
does **not** cover `Gg2.Bots` — a bot is not observable simulation, and this
project's bots are its own (`CLAUDE.md`). The consequence: a change confined
to bot decision-making (`RolloutSearch`, `ScriptedBot`/`BotObjective`) moves
no stamp and invalidates no checkpoint, but can change the opponent every
historical win-rate number was measured against, invisibly to the
fingerprint. **The rule: when the bot layer moves, re-score rather than
trust the fingerprint.** A digest that cannot go red for the change you made
is not evidence about that change.

`SimulationBehaviourVersion` — the third hand-bumped number on the stamp's
surface (on the surface as `behaviour=`) — exists
alongside `SIM_UUID` for the physics side of the same problem: most of the
stamp is derived automatically from constants and layouts, but a behavioural
change that is a changed *line* rather than a changed *constant* (a
trajectory rule, a stage's position in `MatchSimulation`) has nothing else to
move it. Bump it for any knowingly-observable behaviour change; do not bump
it for a refactor, rename, comment or performance change; treat a change as
exempt from bumping only when it is *demonstrated* unobservable, not merely
argued to be.

⚠️ **`gg2-rl` reads several of this repo's source files as text**, not just
its wire values, so a pure rename or relocation inside them is a cross-repo
event even though no value changes: `EnvProtocol.Version` /
`ComponentBytes` / `ActionBytes` / `MakeBatchFixedBytes`; the order of
`EnvServer`'s reward-block `Write*LittleEndian` calls; `DuelEnv.EnvDriver`
and `RolloutSearch.RolloutOpponent`'s enum names and values; `ActionDecoder
.HeadShape`'s element count; `Team.PlayerClass`'s enum names and values. A
relocation, rename or reformatting of any of these is worth flagging to
`gg2-rl` when it happens, since their gate reads text and cannot tell a
relocation from a redefinition.

## Cross-repo protocol bump discipline

- `EnvProtocol.MakeBatchFixedBytes` and `Version` live beside each other and
  are asserted together, so the body cannot grow without the version moving
  with it (`EnvProtocolTests.TheVersionAndTheFrameLayoutMoveTogether`).
- A frame-length change (adding fields mid-body, changing a reply's byte
  count) requires a version bump; a byte-identical tail append does not, as
  long as no existing reply's layout moves — `Candidates`, `SetGoal` and the
  v8 restriction messages all shipped without one on that basis.
- A version bump refuses an old client loudly at `Hello` rather than
  tolerating it; a length-check failure inside a later message is a symptom
  of a missed bump, not a substitute for one.
- ⛔ **A checkpoint's metadata is a MINIMUM, not an exact count, and that is
  load-bearing.** `PolicyNetwork.Load` refuses fewer than seven fields and reads
  the seven by index, so an eighth is ignored. That tolerance is what lets
  `gg2-rl` ship a new field before this side reads it — it writes the trained
  class set as `meta[7]` from 2026-09-06 and has migrated its existing
  checkpoints to carry it. **Tightening the guard to `!= 7` would refuse every
  checkpoint it now produces**, with a message reading as a corrupt file rather
  than a version skew.
  ✅ Gated by `PolicyNetworkTests.AnEighthMetadataFieldIsIgnoredRatherThanRefused`
  against `rest3_s1_iter12000.npz`, **real `Policy.save` output** — the metadata
  is a pickled object array, so a synthetic eight-field checkpoint would exercise
  a pickle this repository wrote rather than one the trainer writes and would pass
  whether or not the two agree.
  ⛔ **That test asserts "not a metadata refusal", NOT "it loads", and the
  difference is what stops it rotting.** `Load` checks five version surfaces after
  the metadata parse — stamp, action encoding, observation layout, observation
  length, head shape — so asserting a load couples a metadata test to all five,
  and the next `SIM_UUID` bump would turn it red talking about stamps under a name
  about the eighth field. The two exception types split on exactly that line:
  `InvalidDataException` is archive and metadata structure,
  `InvalidOperationException` is version skew. ⚠️ `ignoreSimulationStamp` is not a
  substitute — it bypasses the stamp and leaves the other four. Both directions
  were checked: `!= 7` fails the test, and a deliberately staled fixture still
  passes it.
  ✅ **Field 7 is now READ** — see below. The shape check above remains the gate
  for the tolerance itself.

## The trained class set belongs to the CHECKPOINT (2026-09-06)

`PolicyNetwork.TrainedClasses` is read from `meta[7]`, comma-separated
`PlayerClass` names, and `PolicyController.Drives` asks the network it holds.
`PolicyNetwork.DefaultTrainedClasses` is the fallback for a file that does not
say — every checkpoint before 2026-09-06, and any whose field is empty, which is
what the trainer writes for "no set recorded".

⛔ **What the compile-time static cost, measured by the training lane before this
landed.** Three shipping checkpoint families train three different sets. Under
the static, a `control`-family file **drives its untrained demoman** (mirror
36.08% against a 50% null) and **refuses its trained sniper** (89.08%) — with no
stamp moving, no handshake field disagreeing and no test failing. **The same
failure class as `SIM_UUID` not covering the bot layer**: the information that
would catch it was real, known, and on no surface the loader read.

⚠️ **A differing set is a ROUTING fact, not a safety one, and it is deliberately
NOT on the stamp surface.** A policy trained on other classes is not invalid, it
is differently applicable; refusing it would be wrong. Nothing refuses over the
class set, and nothing should.

Gated by `TrainedClassesTests`, on three real `Policy.save` checkpoints:

| fixture | set | what it is for |
|---|---|---|
| `rest3_s1_iter12000.npz` | `ENGINEER,SPY,PYRO` (5, 7, 8) | non-prefix, non-contiguous, different length — catches "the first N", a fixed count, a contiguous range |
| `control_s0_iter12000.npz` | `SCOUT,SOLDIER,SNIPER,HEAVY` | ⛔ **the regression gate** — differs from the default by exactly two members, in both directions |
| `combat4_s2_iter12000.npz` | `SCOUT,SOLDIER,DEMOMAN,HEAVY` | the no-op control: equals the default, so if it moves the change is wrong |
| `ppo_final.npz` (7 fields) | *absent* | the fallback path every pre-2026-09-06 checkpoint takes |

✅ **Checked the gate fires**: reverting `Drives` to the default set fails the
regression gate and the two-controllers test, and leaves the no-op control and
the fallback test passing — which is what a control should do.

### ⛔ Every arm of a comparison takes the CHECKPOINT's set

`SearchController.TrainedClasses` and `SearchMicroSpec.TrainedClasses` exist
because the search holds no checkpoint: it restricts to the policy's classes only
so both arms engage on the same ones. Defaulting that to
`DefaultTrainedClasses` is an **assumption about the other arm**, and for a
checkpoint outside the default family it is wrong — the training lane confirmed
that for their `rest3` family the two sets are **disjoint**, so a three-arm bench
would compare arms sharing no class at all.

✅ Fixed at the five bench modes that load a checkpoint: `BENCH_ARMS`,
`BENCH_CHURN`, `BENCH_EVAL`, `BENCH_PERMAP` and the range sweep now take
`AllowedClasses` — and arm 3's search restriction — from `policy.TrainedClasses`.
⚠️ **`BENCH_THREADS` keeps the default and is the only one where that is right**:
it loads no checkpoint, measuring the search's throughput against thread count, so
it just needs a stable roster.

⛔ **`--bot-classes trained` is the one place that CANNOT ask the file**, and it
says so at the flag: options are parsed before `--policy` is loaded and argument
order is not guaranteed. With a checkpoint outside the default family that
shorthand gives a roster the policy does not drive — the all-scripted arm wearing
the hybrid one's name. Pass the classes explicitly for such a checkpoint.

## The checkpoint is a FIXTURE, not a policy

⚠️ **This section described the exact opposite until 2026-09-03, and the
inversion is the point.** For most of phase 9 the shipped checkpoint was the one
trained artefact that had ever existed, stamped for a *superseded* simulation,
refused by `PolicyNetwork.Load`, and reconciled with a green suite by two
non-interchangeable accessors on `PolicyFixture`. The layout v2 bump retired that
checkpoint for good, and `gg2-rl` minted a replacement **against this build**.

The shipped file (`tests/Gg2.Tests/Fixtures/policy/ppo_final.npz`, 19,483
parameters, action repeat 4, **layout v2, encoding v2, 211 floats**) now carries
**this build's** `SIM_UUID` and takes `Load`'s ordinary accept path with **no
override of any kind**. ⚠️ **Read the current stamp from `BENCH_CONST=1` rather
than from any number written down here** — restating one would just be another
stale number.

**Its weights are an untrained He-normal draw.** That is deliberate and it is
adequate: `TheForwardPassAgreesWithTheTrainer` compares C# against an independent
numpy oracle, and the arithmetic is the arithmetic — random weights exercise it
at least as well as trained ones, since smooth trained weights can mask an
indexing bug.

⛔ **Nothing here may read a behavioural result off it.** An untrained network
emits near-uniform noise, so any test asserting a pinned action, a pinned duel
outcome or a quality threshold against this fixture would be measuring the draw.
The behavioural tests that *do* load it (`PolicyMatchTests`, `ContinuationTests`)
survive only because `HybridBot`'s scripted layer owns movement and routing — the
policy holds keys and aim inside a bounded engagement window, so noise cannot pin
a team. **That is a property of the bot layer, not a licence to assert quality.**

`PolicyFixture` is now one accessor rather than two. `Load()` passes **no**
`ignoreSimulationStamp`, so every caller goes through the same refusal
`EnvServer.LoadPolicy` performs, and the suite exercises the guard instead of
routinely bypassing it. The minted-copy machinery, the hard-coded `TrainedStamp`
and the test asserting the shipped file was *refused* were **deleted rather than
updated**: their premise had gone, not merely changed. Refusal coverage lives in
`PolicyNetworkTests.AForeignStampIsRefused`, which corrupts one character of a
valid checkpoint's stamp instead of relying on the shipped file happening to be
stale.

⚠️ **A stale fixture is loud on its own now.** The hard-coded stamp existed so
that swapping the file re-opened the question; `Load` refusing it in
`PolicyNetwork` itself, naming both stamps, is a better signal and cannot drift
out of date.

## Phase 4 re-measured: the hybrid result (historical, one checkpoint)

`PolicyNetwork` runs the checkpoint above in-process; `HybridBot` hands it
the keys and aim inside an engagement window while the scripted layer keeps
movement, routing and objectives outside it. `HandoverRule.MacroTask` at
`EngagementRange = 150` is the shipped default — the scripted layer's own
engage-range gate (`BotTargeting.ClassRange`, 800-900px) is not narrow enough
on its own; the handover is a conjunction of intent and range. Widening past
about 400px is not supported: the observation saturates there (goal distance
clamps at `min(d/400, 1)`, rays reach 320px, the walkmask patch covers
128px), so beyond it the policy is being given the wheel at a range its
input cannot describe.

Corrected measurement (21 shipped maps, 400 matches an arm, `HEAD 679afb8`;
an earlier 5-minute-cap measurement under-counted decisive outcomes on
several maps and is superseded by this one): **death share is better under
the policy on 21 of 21 maps**, aggregate 46.3% against 50.2% for the
all-scripted control, sign test p = 4.8e-7 one-sided. Death share is the
statistic used because it has no filtered subset — drawn, capped and
decisive matches all contribute their deaths, unlike a win-share statistic
whose denominator (`decisive`, not `decided` — `decided` includes draws) can
be a small fraction of the arm on some maps (`cp_egypt`: 0 of 400 decisive in
either arm). At a 20-minute cap (removing most of the original censoring)
the result is unchanged: 46.4% against 50.2%, 21 of 21, same sign test.

Widening the engagement window raises the combat gain while the objective
collapses faster: at 300px the win share drops to 32.6% against a 45.7%
control on `ctf_truefort`; at 450px on `ctf_classicwell` the policy wins 4
rounds to 133.

**Scope**: this licenses "the shipped artefact beats the all-scripted
control" and not "a policy trained this way beats scripted" — `gg2-rl`
measured training variance across seeds at 6.2 points, larger than the 3.9
point aggregate effect here, and only one policy was ever measured. The
policy has only ever played 1v1 duels 0-300px apart, four classes, no
respawns, no objective; the reward has no objective term by design (the
macro layer owns objectives; the policy never pursues them). A head-level
split (policy aims and fires, scripted always moves) was tried and measured
worse than all-scripted on both axes — the policy's own movement is where
most of the combat gain comes from, and splitting the heads leaves the two
halves fighting each other.

Two implementation bugs were found and fixed while measuring this: the macro
objective layer was polled only on ticks the policy did not control, so it
stopped re-targeting or re-pathing for the ticks it lost, and now polls every
tick regardless of which layer's keys are used; and the original engagement
gate used the scripted engage-hold radius alone (2-3x the intended range),
handing the policy control far more often than intended.

## Goal conditioning

`AgentGoal` — a kind (index into `GoalKinds`) and an optional target point —
sits in the observation's macro-goal block, set per roster slot via
`DuelEnv.SetGoal` / `GoalFor` or the wire's `SetGoal` message. `default` is
exactly the pre-goal-conditioning behaviour (kind 0, "engage nearest enemy"),
so an unchanged caller writes an unchanged observation and neither
`ObservationWriter.LayoutVersion` nor `SIM_UUID` moved when this landed
(verified by observation digest over three maps, byte-identical to the
pre-goal build).

A goal survives an auto-reset — it is a property of the slot, not of the
episode, so a trainer that sets one once does not silently start training on
"engage" labels after the first auto-reset. Because the goal is *part of the
observation*, a stale goal on an episode's first decision is still a
correctly-labelled decision (the observation says what it was conditioned
on); the only effect is a distributional wrinkle a trainer can address by
sampling the next goal on seeing `world_done`. An out-of-range goal kind is
refused rather than clamped to 0, since silently training toward "engage"
while the caller believed it asked for something else is worse than an
error; an all-worlds `SetGoal` call validates every world before applying any
of them.

**Not built**: anything in this repo that decides a goal on its own. The
scripted-layer goal emitter and full-CTF match play through this seam
(`docs/BOTAI.md` item 8, policy as `IInputSource`) are not started.

## Performance

### The reset reload

`ScenarioResources` caches the two deeply-immutable pieces of loading a
scenario — the `LevelMap` and the `NavGraph` — process-wide, and rebuilds only
the `CollisionWorld` per world. Before this, `DuelEnv.Reset` re-parsed a
map's full nav cache (2 MB of text for `ctf_truefort`, ~5.1ms) on every
episode, which was five to six times the cost of the episode itself and the
dominant cost of a training round trip (~80% of it, not the per-float
serialisation loop that was originally suspected at ~2%). `ScenarioWorld.Load`
reads through the cache by default.

The socket layer batches a reply's header and body into one write
(`WriteFrame`, into a pooled buffer) and sets `NoDelay` on the accepted
socket. Both address the same failure mode from different ends: a two-write
reply is the shape Nagle's algorithm holds back waiting for an ACK, and
`NoDelay` prevents the same holding pattern for whichever side's small
writes remain. Buffering the socket's *read* side (wrapping both ends in a
`BufferedStream`) was tried and measured worse — the extra copy costs more
than the syscall it saves, because the frame body has usually already
arrived by the time it is read.

Env compute is a small fraction of a training iteration's wall time in
practice (measured against `gg2-rl`: the env's own CPU share is single-digit
percent of a live iteration, with the trainer's own interpreter contention —
not this server — dominating occupancy). The practical target for
performance work on this seam is therefore per-round-trip **latency**, not
raw throughput: `gg2-env`'s own throughput cleared `gg2-rl` phase 0's ~20k
steps/sec floor early and has not been the constraint since.

## Gates

- Batch-size independence: identical trajectories at batch 1 and batch 64
  with the same seeds, compared whole, digest included.
- Thread-count independence, same comparison.
- Snapshot → restore → continue equals the uninterrupted control, compared at
  the end.
- `ActionDecoder`: jump edge behaviour, absolute mask writes, aim cap and
  quantisation.
- `ObservationWriter`: zero allocation, bounded values, layout length matches
  the advertised one.
- Damage accumulators: attribution, the self-damage rule, invulnerable-victim
  accumulates nothing.
- Protocol round-trip against a real socket, including the `Hello` refuse and
  a frame split across two writes.
- Per-slot drivers: a bot slot returns real reward components and the action
  it took; two scripted opponents fight to a death, not to a tick cap; a
  scripted driver without a nav graph is refused; `Snapshot` is refused with
  a bot in play; a scripted opponent hunts down and kills a stationary agent;
  a bot-driven batch is identical at batch 1 and 32 and on 1, 2 and 4
  threads — each gated with its premise asserted (that a bot really is
  behind the slot), since a client slot fed a no-op action would pass most
  of these with the driver effectively ignored.
- Restriction messages: alignment between a scored distribution's argmax and
  the reward block's reported action; a stale mask dropped on a class
  change; a restriction consumed by exactly one decision; validate-then-apply
  across a whole batch.

## Exit (v0) — met

1. An in-repo random-head agent completed 1,024 duels over the real socket
   protocol: no crash, no hang, every episode terminated within its tick
   cap.
2. Every gate above is green.
3. Throughput measured and recorded: decision steps/sec at batch 1 and batch
   64, Release build. `gg2-rl` phase 0 gates on ~20k steps/sec; this repo's
   number has cleared that since the first measurement.

## Measurements

| Measurement | Value | Build |
|---|---|---|
| Decision steps/sec, batch 1 | ~9,060 | Release |
| Decision steps/sec, batch 64 | ~40,700 | Release |
| A round trip on a real map with nav, batch 16 (`gg2-rl`'s training shape) | ~0.20 ms | Release |
| Bytes allocated per round trip, batch 16 | ~23.6 KB | Release |
| `ScenarioWorld.Load`, `ctf_truefort` with nav, cached | ~0.01 ms | Release |
| `ObservationWriter.Write`, one call, averaged over a real duel | ~3.1 µs | Release |
| `RolloutSearch` decision, 60 candidates, horizon 20, population 2 | ~2.45 ms (soldier) | Release |
| `RolloutSearch` decision, 120 candidates (sniper), population 2 | ~4.56 ms | Release |
| `RolloutSearch` decision, horizon 20, population 12 | ~7.6 ms | Release |
| Socket round trip, batch 1 | ~50.7 µs mean | Release |
| Socket round trip, batch 16 | ~265.7 µs mean | Release |
| Round trips stalling past 10 ms in 20,000, batch 1 | 0 | Release |
| `gg2-env` worker bring-up, 16 worlds | ~92 ms (spawn + handshake + `MakeBatch`) | Release |

⚠️ **A `RolloutSearch` figure always has a population term.** A duel is a
match with population fixed at its minimum (2); do not quote a duel-measured
search cost into a full match without re-measuring at that population — cost
scales roughly linearly with live character count, since every candidate-tick
re-simulates every live character.

⚠️ **A `RolloutSearch` figure also has a cadence term** when a policy
continuation is involved — see "Protocol v8" above; the same absolute
milliseconds mean different things at different `DecisionTicks` values.

## Out of scope

- **Serialising the bot layer** (what `Snapshot` with a bot-driven slot would
  need) — still refused, and deliberately: a `Snapshot` that had to
  serialise a bot's own RNG stream, path, target and objective state would
  make every one of those a wire format, and the bot layer is the part of
  this repo explicitly allowed to change freely (`docs/TRAPS-BOTS.md`).
- **A reaction-delay model** — only its observation field exists
  (time-since-seen is a fixed 0 in v0).
- Weights over the wire arrive per-batch in `MakeBatch`/`LoadPolicy`, never
  as a file format read from this repo — a weights file here would be a
  second source of truth about what a checkpoint is.
- No callback over the socket for a rollout's per-tick decisions — the round
  trip cost (tens of microseconds each) is far larger than the budget for a
  single search decision (low milliseconds).
- No allocation in the rollout loop — scratch buffers per search instance.
- A continuation or shadow search is never folded into `SIM_UUID` — a search
  is a bot, and this project's bots are its own.
