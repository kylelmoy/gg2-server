# What the port must preserve for search-based and learned bots

A set of constraints on the C# server, written while it is still specification.
Each one is cheap now and expensive-to-impossible to retrofit once `WorldState`
and the bot seam have shipped.

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
  does for a socket).
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

## 2. Snapshot and restore, not just replay-from-start

**Requirement.** `WorldState` can be serialised to bytes and restored to a
bit-identical state that continues to tick identically.

**Why.** Search needs restore-to-here. Training needs to resume an episode from
the middle of a logged human game rather than replaying a match from tick zero to
reach an interesting position. Parity debugging wants it too - starting the
replay one second before the drift instead of ten minutes before it.

If item 1 is done properly this is nearly free: a copyable flat state is a
serialisable one.

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

## Reference

Facts about the GM8 implementation cited above were read from the
`Gang-Garrison-2` tree on 2026-08-24: `Source/gg2/Scripts/Bots/botAimLead.gml`,
`Source/gg2/Scripts/Bots/botInputUpdate.gml`, and
`Source/gg2/Scripts/GameServer/processClientCommands.gml`.
