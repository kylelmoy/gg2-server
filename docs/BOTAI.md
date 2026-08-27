# What the port must preserve for search-based and learned bots

A set of constraints on the C# server, written while it was still specification.
Each one is cheap early and expensive-to-impossible to retrofit once `WorldState`
and the bot seam have shipped.

⚠️ **Audited against the shipped code on 2026-08-27, and one of them was false.**
`BotAiConstraintsTests` now asserts the five that are structural facts rather than
design intent; each verdict is recorded against its item below. Items 3, 8 and 10 remain
unbuilt and say so; item 9 was built on the audit's own finding.

None of this asks for a neural network to be built. It asks that the port not
foreclose one.

## Why this document exists

The GM8 bot layer's weakness is not aim. `Scripts/Bots/botAimLead.gml` is an
exact ballistic intercept solver - against an opponent model of "constant
velocity plus gravity". Against a human who reacts to the shot being fired, that
leads the mean of a distribution nothing in the codebase represents. GG2's slow
projectiles make opponent *prediction*, not marksmanship, the skill the bots are
missing.

Three things fix that, in increasing order of cost:

1. Aim at a reachable *set* rather than a predicted point, and model intent from
   the nav graph rather than from velocity. Pure logic; needs nothing from the
   port beyond what it already promises.
2. **Short-horizon rollout search.** Simulate 15-30 ticks of real physics across
   a small discrete action set, score, act, repeat. This is what makes dodging
   good, not just aiming - and dodging is half of what "competitive" means here.
3. **Learned policies**, whether cloned from human play or trained by self-play.

Items 2 and 3 are the ones with requirements on this repo. They are listed below.

## Already satisfied - do not redesign these

`docs/ARCHITECTURE.md` gets most of the way there, and these are called out so
nobody solves them twice:

- The pure `Tick(WorldState, ReadOnlySpan<PlayerInput>)` with no I/O.
- `IInputSource` as the seam, with network clients and bots on the same
  interface - which is already true in the reference (`botInputUpdate.gml`
  writes `keyState`/`aimDirection` exactly as `processClientCommands.gml:267`
  does for a socket). **Built** (2026-08-27): `Gg2.Simulation.IInputSource`,
  `Gg2.Server.NetworkInputSource`, `GameServer.GatherInputs`. See
  docs/ARCHITECTURE.md, "Bots", for what it settled.
- Determinism, independent `WorldState`s across cores, headless
  faster-than-real-time, per-slot think-rate.
- `Gg2.Parity`'s trace recorder and replayer.

What follows is the delta.

## 1. `WorldState` must be cheap to copy

**Requirement.** Cloning a full match state is a `memcpy`, not a graph walk, and
`Tick` allocates zero bytes on the steady-state path.

**Why.** Rollout search forks the world, plays a candidate action sequence
forward, reads a score, and discards it - thousands of times per decision. If a
clone costs an allocation-heavy deep copy, or if each simulated tick produces
garbage, search is dead on cost alone and no amount of C# speed rescues it.

**What that implies for data layout.** Fixed-capacity pools rather than growable
collections; struct-of-arrays over arrays-of-references; entity handles (index
plus generation) rather than object references; no `WorldState` field pointing at
anything outside `WorldState` - no map object, no config object, no logger, no
event sink that owns memory. Map data and other read-only inputs are passed *to*
`Tick` alongside the state, never held by it.

**Retrofit cost.** Total. This is the one item on the list that cannot be added
later without rewriting the simulation.

✅ **Held, and asserted.** `WorldState` contains no reference anywhere in it —
recursively, so a reference inside a struct inside an `[InlineArray]` slot buffer
fails it too, which is exactly where one would be added without anybody noticing.
⚠️ **The failure mode is not a broken test elsewhere**: putting a `List<>` or a
logger in there breaks nothing that runs. The server still serves, every parity
window holds, the whole suite stays green — and rollout search is dead months
later for a reason nobody can find.
`BotAiConstraintsTests.WorldStateHoldsNoReferenceAnywhereInIt`.

❌ **The zero-allocation half was FALSE until the audit.** A steady-state tick
allocated **32 bytes per live character, every tick** — about 384 B/tick on a
twelve-bot server. The cause was `foreach` over an `IReadOnlyList<T>`: enumerating
an *interface* allocates a boxed enumerator where enumerating the concrete array
does not. `Cabinets.CollisionStep` walked its list once per live character;
`KillBoxes`, `MoveBoxes` and `ControlPoints`' capture zones had the same pattern
and were invisible only because no fixture map places any of those, and an
**empty** array's enumerator is a cached singleton that measures zero. All four
are indexed loops now.
⚠️ **The rule worth carrying: inside `Gg2.Simulation`, a collection the tick walks
must arrive as an array or a span, or be indexed — and a `foreach` that measures
zero today may only be walking an empty list.**
`BotAiConstraintsTests.ASteadyStateTickAllocatesNothing`.

## 2. Snapshot and restore, not just replay-from-start

**Requirement.** `WorldState` can be serialised to bytes and restored to a
bit-identical state that continues to tick identically.

**Why.** Search needs restore-to-here. Training needs to resume an episode from
the middle of a logged human game rather than replaying a match from tick zero to
reach an interesting position. Parity debugging wants it too - starting the
replay one second before the drift instead of ten minutes before it.

If item 1 is done properly this is nearly free: a copyable flat state is a
serialisable one.

✅ **Free, and demonstrated rather than assumed.** A whole match is **144,840
bytes** and round-trips through raw bytes bit-identically with no production code
at all — the bytes *are* the state.
⚠️ **Restoring is not the claim; continuing is.** The gate steps the restored copy
fifty ticks against a control that was never serialised and compares at the
**end**, because the failure worth catching is a state that compares equal and
then diverges: a cursor, a counter, an RNG stream left outside the struct.
⚠️ And the snapshot is taken on a **condition**, not a tick count — a rocket's
whole life is shorter than its refire, so most instants after firing have an empty
pool, and a snapshot with nothing in the air never exercises the handles or the
creation order.
`BotAiConstraintsTests.AStateRoundTripsThroughBytesAndGoesOnTickingIdentically`.

## 3. The parity trace format is the training data format

**Requirement.** Whatever `Gg2.Parity` records for trace replay - per tick, per
slot: `keyState`, `netAimDirection`, `aimDistance`, plus periodic full state
snapshots - is what a live server can be configured to record from **human**
players, to disk, on a real game.

**Why.** The server already receives every human's complete action every tick:
one byte of keys, two of aim, one of distance (`processClientCommands.gml:267`).
That is a labelled dataset arriving for free, and it is the only source of
*human-like* behaviour rather than optimal-looking behaviour. More usefully, it
yields `p(action | state)` for real players - precisely the opponent
distribution that fixes the aim problem this document opens with.

The point is that one recorder serves both purposes. Do not design a second
logging path later; make the parity recorder switchable on in production and
give it a bounded on-disk format.

**Deadline.** This is the only item here with one. Data not collected during the
port's first public games does not exist afterwards.

❌ **Not built.** The parity recorder records a *server's own broadcast stream*;
nothing records human input to disk on a live server, and no switch exists to turn
it on. Still the item with the deadline.

## 4. Keep the action space exactly the wire action

**Requirement.** A bot's output is the same `PlayerInput` a socket produces:
8-bit `keyState`, 16-bit quantised aim, 8-bit distance. Bots do not get a
privileged float aim, extra keys, or direct state writes.

**Why.** One action space for humans, scripted bots, search and learned policies
means a logged human action is a valid bot action and vice versa - no conversion
layer, no train/deploy mismatch, and behaviour cloning targets the exact
quantisation the game runs on. It is also the honesty property: a bot cannot aim
more precisely than a player can.

The reference already works this way. Do not "improve" it.

✅ **Asserted structurally**: every field of `PlayerInput` is a byte, a ushort or
an enum, so there is nowhere for a privileged float aim to live, and `FromAim`
quantises the way *in* onto the same 65,536 steps — about 182 per degree.
`BotAiConstraintsTests.TheActionSpaceIsExactlyTheWireAction`.

**Satisfied.** `IInputSource.Poll` returns a `PlayerInput` and nothing else, and
the actions a key cannot express go through a separate `PlayerCommand` channel
capped at the socket's own `ClientCommands.PerTickLimit` — so a bot's whole
budget is a client's whole budget. ⚠️ The commands are *not* an escape hatch:
each one lands in the same `GameServer.Execute*` handler the socket switch
calls, and its preconditions are re-checked there
(`InputSourceTests.ACommandWithFailedPreconditionsIsSilent`).

## 5. The bot seam must allow batched decisions

**Requirement.** Alongside the per-slot poll, there is a shape that decides for
many bots at once:

```csharp
interface IInputSource {
    PlayerInput Poll(in WorldView world, int slot, int tick);
}

// and, for sources that want it:
interface IBatchInputSource {
    void PollMany(in WorldView world, ReadOnlySpan<int> slots,
                  Span<PlayerInput> outputs, int tick);
}
```

**Why.** A per-slot `Poll` forces per-bot policy evaluation. A small MLP is
microseconds, but eight separate single-row matrix multiplies waste most of the
throughput available from one batched call - and the same is true for a
rollout-search source that wants to amortise its forked worlds across every bot
deciding on the same tick.

Only the interface shape matters now; nothing has to implement the batched form.
Adding it after every call site assumes per-slot polling is a wide change.

**Satisfied, including the call site.** `IBatchInputSource : IInputSource`, and
`GameServer.GatherInputs` groups slots by source *identity* and calls `PollMany`
once with all of them — because the wide change this item warns about is the
call site, not the declaration. Nothing implements it for real yet;
`InputSourceTests.OnePolicyDrivingSeveralBotsIsAskedOnce` is the gate, with the
per-slot fallback as its control.

## 6. `WorldView` must yield a feature vector cheaply

**Requirement.** The read-only projection can produce a fixed-size numeric
observation for a given slot without allocating and without walking the whole
world - self state, N nearest enemies, N nearest projectiles, local nav-graph
context, objective state.

**Why.** This is a policy's entire input, and it is computed for every bot every
decision tick during training, where the sample budget is the binding cost. If
extracting it means a LINQ query over an entity list, feature extraction becomes
more expensive than the simulation it observes.

Not asking for the feature set to be designed now. Asking that `WorldView`
support spatial queries - nearest-K by team, projectiles within a radius - rather
than only whole-collection enumeration.

**The shape is there.** `WorldView.NearestEnemies` / `NearestAllies` fill a
caller-supplied `Span<int>` by insertion into its sorted prefix — a nearest-3
query does three comparisons a candidate, not a sort of the roster — and
`ProjectilesWithin` fills a `Span<ProjectileHandle>`. Neither allocates.
⚠️ **Every tie is broken by roster index**, deliberately: without a defined
tie-break the answer depends on whether the insertion used `<` or `<=`, which is
exactly the kind of thing that makes a rollout search irreproducible for reasons
nobody can find. `ProjectilesWithin` answers in *creation* order, which is the
order the rest of the project already makes observable — a pool slot is recycled
and says nothing about when anything was fired. The feature vector itself is
still undesigned, which is what this item permits.

✅ **Asserted**: `typeof(WorldView).IsByRefLike`, so the read-only guarantee is
the compiler's rather than a convention
(`BotAiConstraintsTests.WorldViewIsARefStruct`).

⚠️ **`WorldView` is a `ref struct`**, carrying a `ref readonly WorldState`
rather than a copy. Constructing one is free, and the language refuses to let a
source stash it in a field or keep it past the call — the read-only guarantee
made structural. A by-value projection would have been a kilobyte copy per bot
per decision, which is item 1 in another costume.

## 7. Damage and score events must be emitted, not inferred

**Requirement.** The out-going event list `ARCHITECTURE.md` already describes
carries damage with attribution - who hit whom, for how much, with what - plus
kills, deaths, captures and objective transitions.

**Why.** These are the reward signal. Reconstructing "bot 3 dealt 40 damage" by
diffing two `WorldState` snapshots is both slow and ambiguous about attribution,
and it forces training code to duplicate game rules it should never know. The
events are needed for the scoreboard regardless; the requirement is only that
they carry the attributor and the amount rather than being presentation strings.

## 8. A match runner with no sockets in it

**Requirement.** An entry point that runs N independent matches in one process
with no networking, no client, no rendering, no sleeping, and a fixed seed - and
produces identical results regardless of how many threads it used.

**Why.** This is the self-play and evaluation harness. It is also how a bot
change is measured: run the new bot against the old across every map, N times,
report win rate. That measurement is the thing the current GM8 setup cannot do at
all, and it is worth more day-to-day than any learned policy.

`ARCHITECTURE.md`'s control surface is the interactive version of this; this is
the batch version, and the two should share their hosting code.

❌ **Not built.** It is phase 8's, in `docs/PLAN.md`, and the tooling lane's per
`docs/PARALLEL.md`.

## 9. `SIM_UUID`, and refusing a policy that does not match it

**Requirement.** The simulation carries a version stamp. Every policy file
records the `SIM_UUID` it was trained against and the `gg2-server` commit SHA,
and the server **refuses** to load one that does not match its own. Refuse, do
not warn.

**Why.** A policy trained against physics that has since changed does not error
and does not warn. It plays slightly worse forever and the cause is invisible.
GG2 already has the idiom one layer up: the reference refuses a client whose
`PROTOCOL_UUID` does not match rather than letting it connect and desync.

**Derived from**, at minimum: the physics constants and the `BOT_*` block of
`Constants.xml`; the items `SIMULATION.md` marks as contract rather than
incidental; the observation layout version; the action encoding version. Bumped
whenever any of those changes — which will be often and early, and every bump
invalidates every checkpoint. That is correct.

**Where it lands.** Naturally with phase 4 in `PLAN.md`, since there is nothing
to stamp before the physics exists. Do not defer it past that: a stamp added
after the first checkpoints are trained cannot tell you which ones were valid.

✅ **Built** (2026-08-27): `Gg2.Simulation.SimulationStamp`, and
`SimulationStampTests` is the gate. `Uuid` is `99b7ab3141e31fb4` over 213 entries
and `Require` **throws** on a mismatch, naming both stamps so an operator can tell
which side is stale.

⚠️ **The surface is chosen, not swept.** In: the physics constants (which live on
`ClassStats`, not only `Gml`), the `BOT_*` and `NAV_*` blocks, the per-class table
and the blast tables, and the two hand-bumped version numbers. **Out:
`GameConstants` — configuration and protocol.** A port number moving must not
invalidate every checkpoint, because **a stamp that churns gets ignored**.

⚠️ **Round-trip formatting, so a one-ULP change moves it** — `Gml.PointSix` and
its four siblings *are* gravity and the friction factors, and fifteen digits
cannot tell them from the plain literals. And **FNV-1a rather than
`string.GetHashCode()`**, which is randomised per process and would refuse every
policy after a restart.

⚠️ **The digest is pinned in the test, and a change there is the bump rather than
a broken test.** The question to answer before editing that line is which
checkpoints it just invalidated.

It arrives at the end of phase 7 rather than with phase 4 as this item asks.
Nothing has been trained, so nothing is invalid; the cost paid is only that the
stamp had to be derived from a larger surface than it would have been.

## 10. A `Gg2.Env` project

**Requirement.** A batch-stepping surface: N worlds advanced by one decision
step per call, observations and rewards returned as one contiguous block; reset
per world independently; snapshot and restore (item 2); seeded and reproducible
regardless of batch size or thread count; damage-with-attribution, kills, deaths
and termination reason out (item 7).

**Why here rather than in the trainer.** It needs `WorldState` internals and it
has to be fast in the tick loop. Extracting features across a process boundary
means shipping raw world state per bot per tick, which is the one thing the
batching exists to avoid. `Gg2.Parity` is the precedent for a non-serving
project living in this repo.

Start with a local socket and length-prefixed binary frames. Shared memory is an
optimisation to make once there is a measurement that justifies it.

❌ **Not built.** Items 1, 2 and 6 — its three hard preconditions — all hold, so
it is unblocked whenever it is wanted.

## 11. Scenario construction without a gamemode

**Requirement.** Spawn two characters of given classes at given positions on a
given map, with no gamemode running, and step them.

**Why.** Duel training and rollout search both want a fight, not a match. Item 8
asks for a socket-free runner of *full matches*; this is the smaller case, and
it is the one the sample budget actually spends its time in.

**Satisfied.** `Gg2.Bots.ScenarioWorld` — load a map for an area, `Place` any
number of characters of given classes and teams at exact positions, `Drive` each
with an `IInputSource`, `Step`. A library type rather than a test fixture,
because a self-play evaluator wants exactly this. ⚠️ **What makes it
gamemode-less is the zeroed limits and placing no objective, not the `GameMode`
field** — measured, by setting the mode three ways and watching nothing change.
CLAUDE.md, "the scenario harness", has the list of lines that do the work and
the two (`TdmInvulnerabilityTicks`, `RespawnTime`) that are only observable at
one instant each.

## Not asking for

To keep the scope honest:

- No ML runtime, tensor library, or ONNX dependency in this repo yet. When a
  policy exists it loads as weights behind an `IInputSource`.
- No training loop, replay buffer, or environment API. Those live outside the
  server.
- No end-to-end learned bot. The realistic target is a **hybrid**: the scripted
  macro layer keeps objective selection, class choice and routing - roughly 2,000
  working lines in `botObjectiveUpdate.gml` and `botPathKeys.gml` - and only the
  ~1-second duel controller is searched or learned. Small observation space,
  dense reward, and it is exactly the part scripting is bad at.
- Nothing here changes the wire protocol or the simulation's observable
  behaviour. Every constraint above is on structure, not on what the game does.

## Where items 9-11 came from

They are `gg2-rl/docs/CONTRACT.md`'s "What gg2-server does not yet promise".
That repo is the trainer; this one owns the simulation, the env surface, the
observation layout and the version stamp. The split is deliberate and the
asymmetry is the point: anything needing `WorldState` internals, or needing to
be fast in the tick loop, belongs here, and `gg2-rl` should be deletable without
this repo losing a feature.

## Reference

Facts about the GM8 implementation cited above were read from the
`Gang-Garrison-2` tree on 2026-08-24: `Source/gg2/Scripts/Bots/botAimLead.gml`,
`Source/gg2/Scripts/Bots/botInputUpdate.gml`, and
`Source/gg2/Scripts/GameServer/processClientCommands.gml`.
