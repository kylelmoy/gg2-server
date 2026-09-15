# What the port must preserve for search-based and learned bots

A set of structural constraints on the C# server. Each one is cheap to hold now
and expensive-to-impossible to retrofit once `WorldState` and the bot seam have
shipped. `BotAiConstraintsTests` asserts the ones that are structural facts
rather than design intent; each item below says which.

Items 1-12 are all built. None of this asks for a neural network to be built.
It asks that the port not foreclose one.

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
  does for a socket): `Gg2.Simulation.IInputSource`,
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

**Held, and asserted.** `WorldState` contains no reference anywhere in it —
recursively, so a reference inside a struct inside an `[InlineArray]` slot buffer
fails it too, which is exactly where one would be added without anybody noticing.
`BotAiConstraintsTests.WorldStateHoldsNoReferenceAnywhereInIt`.

**The failure mode is not a broken test elsewhere.** Putting a `List<>` or a
logger in there breaks nothing that runs — the server still serves, every parity
window holds, the whole suite stays green — and rollout search is dead months
later for a reason nobody can find.

**Zero-allocation is easy to lose silently via `foreach` over an interface.**
Enumerating an `IReadOnlyList<T>` via `foreach` allocates a boxed enumerator
where enumerating the concrete array does not, and an **empty** collection's
enumerator is a cached singleton that measures zero — so a `foreach` that
measures zero today may only be walking an empty list on the fixture that
exercised it. Inside `Gg2.Simulation`, a collection the tick walks must arrive as
an array or a span, or be indexed. `BotAiConstraintsTests.ASteadyStateTickAllocatesNothing`.

## 2. Snapshot and restore, not just replay-from-start

**Requirement.** `WorldState` can be serialised to bytes and restored to a
bit-identical state that continues to tick identically.

**Why.** Search needs restore-to-here. Training needs to resume an episode from
the middle of a logged human game rather than replaying a match from tick zero to
reach an interesting position. Parity debugging wants it too - starting the
replay one second before the drift instead of ten minutes before it.

If item 1 is done properly this is nearly free: a copyable flat state is a
serialisable one.

**Free, and demonstrated rather than assumed.** A whole match is **144,840
bytes** and round-trips through raw bytes bit-identically with no production code
at all — the bytes *are* the state.

**Restoring is not the claim; continuing is.** The gate steps the restored copy
fifty ticks against a control that was never serialised and compares at the
**end**, because the failure worth catching is a state that compares equal and
then diverges: a cursor, a counter, an RNG stream left outside the struct. And the
snapshot is taken on a **condition**, not a tick count — a rocket's whole life is
shorter than its refire, so most instants after firing have an empty pool, and a
snapshot with nothing in the air never exercises the handles or the creation
order. `BotAiConstraintsTests.AStateRoundTripsThroughBytesAndGoesOnTickingIdentically`.

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

The point is that one recorder serves both purposes: do not design a second
logging path later; make the parity recorder switchable on in production and
give it a bounded on-disk format.

**Built:** `gg2-server --record <dir>`, off by default. There was nothing extra
to capture — the broadcast stream already carries every player's keys and aim
every tick, so the recorder tees `GameServer.FlushBroadcast`, which is also why
"do not design a second logging path" holds by construction. `gg2-parity census`
reads a six-bot game as 27,989 messages, framed cleanly to the last byte.

`Recorder` is a passive spectator client: it connects to any host and port,
sends no `INPUTSTATE`, and writes down every byte
(`src/Gg2.Parity/Recorder.cs:7-22`). So `gg2-parity record --host
<public-server> --port <n>` captures human play on somebody else's GM8 server —
the cheapest route to the *opponent* distribution this item wants, independent
of whether this port has its own public game.

**The bar is lower than "a playerbase".** What the item wants is a distribution
over players nobody scripted, not a large corpus. At 30 Hz one 8-player
half-hour round is ~430,000 labelled player-ticks, and the thing being fitted is
small: reaction-time and dodge-direction priors conditioned on a handful of
local features, over an action space of one byte of keys and a quantised aim. A
dozen recorded rounds clears it.

### The precondition has fired

The operator runs a three-host public fleet and strangers join it, so the
"zero active players world wide" that tabled this no longer holds. What the
deadline said still does: play not recorded while it happens does not exist
afterwards.

**It is still not critical.** Item 3 serves one thing — the opponent model in
the aim problem this document opens with — and the fallback is self-play,
through items 8, 10 and 11, all of which are built. What is lost without it is
not the ability to train; it is that every behaviour the trainer ever sees was
written by somebody in this repo, a quality cost rather than a blocker.

**One human is not the data it wants.** The item asks for `p(action | state)`
over real *opponents*: a solo session is repeatable whenever, a game with
strangers is not.

### A recording says who was a person

A bot joins as an ordinary player and `PLAYER_JOIN` carries a name and nothing
else, which is item 4 working as intended — so on a bot-filled server the
broadcast holds a person's keys and the scripted layer's keys in the same
fields with nothing to separate them. A dataset built from an unlabelled trace
of such a server is mostly this project's own bots, fed back in as
demonstration data, and every row of it is a valid row: nothing downstream can
detect the substitution.

Trace format v2 carries a `TraceParticipant` per slot per tick, written by the
recorder from `RosterEntry.IsBot` and re-taken at every segment roll so a
segment stands alone. Slot 0 is labelled a bot: `IsBot` defaults to false
meaning human — the safe direction for `Handover.ShouldEngage`, the wrong one
here — and an unlabelled reserved slot reads downstream as a person who never
pressed anything.

A v1 trace has no labels, and every consumer refuses one rather than assuming
its slots were people. The checked-in fixture corpus stays v1: those traces
came off the running game and have no bots in them.

### From a trace to training rows

`gg2-env dataset <trace|dir>... --out <dir>` replays a recording through
`Gg2.Parity`'s `Replayer` — the same replay the parity fixtures run, so there
is one path from a trace to a world — and writes `(observation, action)` as
`.npy`. Human rows only unless `--include-bots`, and every row carries its own
participant column either way.

| Choice | Why |
|---|---|
| Open-loop replay | A trace of this server's own play should reconstruct exactly, but a reconstruction that had drifted would emit observations of a world nobody saw. Open-loop re-anchors to the recorded positions every seventh tick. |
| `--action-repeat k` samples every k-th tick | A policy at repeat k turns once per k ticks against a fixed per-decision cap, so the aim delta is measured over the same span. Taking every k-th row of a per-tick dataset under-reports every turn by a factor of k. |
| Both the binned aim head and the raw signed degrees | A mouse is unbounded and the head is not, so a person's flick has no head that reproduces it. `saturated` counts the rows the action space could not express, and that rate is what phase 5's turn-rate cap is fitted to. |
| The manifest carries `SIM_UUID` and the observation layout version | An observation is the output of one physics and one encoder. Rows from another stamp describe a different game, and without it two corpora merge silently. |

`ActionEncoder` is the inverse of `ActionDecoder` and lives beside it: a pair
that disagrees produces a dataset labelled in an action space no policy is
trained in.

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

**Asserted structurally**: every field of `PlayerInput` is a byte, a ushort or
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
call site, not the declaration. `InputSourceTests.OnePolicyDrivingSeveralBotsIsAskedOnce`
is the gate, with the per-slot fallback as its control.

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

**Asserted**: `typeof(WorldView).IsByRefLike`, so the read-only guarantee is
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

**Whole.** `TickEvents` carries kills, deaths, spawns and captures with
attribution; the accumulators `SlotBuffer<double> DamageDealt` / `DamageTaken`
(`TickEvents.cs:374`) are credited at application time in `DamageApi.Deal`
(`DamageApi.cs:68`), cleared by `ClearOutputs`, gated by `DamageAccumulatorTests`.
Self-damage counts as taken and never as dealt; blocked damage accumulates
nothing.

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

**Built.** `HeadlessMatch` (`Reset`/`Step`/`State`) with `MatchRunner` as a loop
over it, and `gg2-server eval` on top. Its results do **not** depend on the
thread count — gated by comparing whole result arrays, digest included, rather
than win rates, because every match owns its own `CollisionWorld`,
`ScriptedBot`s and therefore its own `NavSearch`. 44 matches across all 22 maps
in 3.3s, 3,548x real time.

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

**Built.** `Gg2.Simulation.SimulationStamp`, gated by `SimulationStampTests`.
`Require` **throws** on a mismatch, naming both stamps so an operator can tell
which side is stale.

**The surface is chosen, not swept.** In: the physics constants (which live on
`ClassStats`, not only `Gml`), the `BOT_*` and `NAV_*` blocks, the per-class table
and the blast tables, and the **three** hand-bumped version numbers. **Out:
`GameConstants` — configuration and protocol.** A port number moving must not
invalidate every checkpoint, because **a stamp that churns gets ignored**.

**The third hand-bumped number is `SimulationBehaviourVersion`, and it exists
because everything else on the surface is a CONSTANT** — it is the escape hatch
for a change to control flow (e.g. in `Movement.cs`) that is real physics but
touches no constant, so nothing else on the stamp's surface would move for it.
**Bump it when you knowingly change what the simulation does**; do not bump it
for a refactor, and exempt a change only when it has been *demonstrated*
unobservable rather than argued to be. `docs/ENV.md`, "the third hand-bumped
number", has the rule and the exemption that was used.

**Round-trip formatting, so a one-ULP change moves it** — `Gml.PointSix` and
its four siblings *are* gravity and the friction factors, and fifteen digits
cannot tell them from the plain literals. And **FNV-1a rather than
`string.GetHashCode()`**, which is randomised per process and would refuse every
policy after a restart.

**The digest is pinned in the test, and a change there is the bump rather than
a broken test.** The question to answer before editing that line is which
checkpoints it just invalidated.

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

**Built and scoped.** Items 1, 2 and 6 — its three hard preconditions — all
hold. Scope: duels only; v0 observation layout built to `gg2-rl/docs/DESIGN.md`'s
sketch; action heads decoded server-side; rewards as components. Design is
**`docs/ENV.md`**. Item 7's damage half — per-tick damage amounts via
accumulators — was the one precondition gap found, and it is closed
(see item 7).

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
docs/TRAPS-BOTS.md, "the scenario harness", has the list of lines that do the
work and the two (`TdmInvulnerabilityTicks`, `RespawnTime`) that are only
observable at one instant each.

## 12. The rollout-search bot

**Requirement.** An `IInputSource` in `Gg2.Bots` that, on each decision, forks
the world, plays candidate short action sequences forward 15-30 ticks, scores
each from the emitted damage events, restores, and plays the first action of the
best sequence - replanning next decision rather than committing to the plan. Its
action space is the same `EnvAction` heads `Gg2.Env` decodes, so a decision it
logs is valid data for a policy and vice versa.

**Why it is here and not in `gg2-rl`.** **It is a bot in its own right.** It
is a better bot than this repo currently ships, it is the permanent evaluation
baseline every later policy is measured against, and it is useful with `gg2-rl`
deleted. It also wants `WorldState` internals and has to be fast in the tick
loop, which is this document's own test for which side of the seam a thing lives
on. Putting it in the trainer would make the trainer non-deletable.

**Why it is worth building even if no policy ever ships.** Two things fall out of
it for free. It is a **parity fuzzer**: a search systematically tries what no
human would, so it finds physics-port bugs by exploiting them, and each exploit
is a finding for `docs/PARITY.md` rather than something to suppress. And it gives
`gg2-rl` phase 1 an exit criterion that can be checked - beat the ported scripted
bot over 500 duels per class matchup at p < 0.01 - which is the cheapest possible
test of whether the whole learning programme is worth continuing.

**What it needs that already exists.** Item 2's snapshot/restore (the fork), item
5's `IBatchInputSource` (amortising forked worlds across every bot deciding on
the same tick), item 6's `WorldView` queries, item 7's damage accumulators (the
score), and item 11's `ScenarioWorld` (a fight, not a match). **Nothing new is
required from the simulation.** This item is assembly, not capability.

**Built.** `Gg2.Bots.RolloutSearch`. 2.94 ms a decision in Release and 11-0-1
against `ScriptedBot` over twelve duels; `docs/TRAPS-BOTS.md`, "Item 12", has the
rest.

**The one open design question**, and it is the one that decides whether the
search is worth imitating: **what the opponent is assumed to do during a
rollout.** `botAimLead.gml` assumes constant velocity plus gravity, which is the
assumption this whole programme exists because it is wrong. Self-play - rolling
the opponent out with the same search - is the answer that needs no data.

**Decided, as a named enum rather than a comment**: `RolloutOpponent.HoldLastInput`
is the default — every other character holds the input it is already holding,
read off `CharacterState`. `Idle` (presses nothing) is kept beside it as the
comparison floor. Self-play is deliberately out of v0 and its seam is a third
member of that enum.

**`HoldLastInput` is the same CLASS of assumption as `botAimLead`'s** — better,
because it is right about momentum over a 20-tick horizon, and still an opponent
who does not *react*, which is the thing this programme exists because of. So
`gg2-rl` phase 1 should report the win rate under both models rather than one
headline number: if they barely differ the search is not really using its
opponent model and the horizon or the scoring is the binding constraint, and if
they differ a lot that gap is the quantified argument for paying for nested
rollouts. That number is wanted before phase 2 distils anything, because a
policy inherits whatever the search assumed.

**`RolloutOpponent.Idle` is therefore not dead code.** It looks like unused
surface area and it is the control that makes the paragraph above measurable.
`docs/TRAPS-BOTS.md`, "Item 12", and `gg2-rl/docs/PLAN.md` phase 1.

**Not asked for:** a learned policy guiding the search. That is `gg2-rl` phase 2's
expert iteration, and it arrives as weights behind a seam that already exists.

## Not asking for

- **No ML runtime, tensor library, or ONNX dependency required in this repo.**
  What shipped stayed inside that rule: `PolicyNetwork` runs a `gg2-rl`
  checkpoint in-process, loaded by `NpyFile` from their own `.npz`, as weights
  behind an `IInputSource` (`HybridBot` driving a `PolicyController`) — a
  `Dense` loop and an argmax per head, no tensor library and no dependency.
- **No training loop, replay buffer, or environment API. Those live outside the
  server.** `src/Gg2.Env/` is a *seam* — a socket and an observation writer —
  not a trainer; the loop is `gg2-rl`'s.
- **No end-to-end learned bot. The realistic target is a hybrid**: the scripted
  macro layer keeps objective selection, class choice and routing - roughly 2,000
  working lines in `botObjectiveUpdate.gml` and `botPathKeys.gml` - and only the
  ~1-second duel controller is searched or learned. Small observation space,
  dense reward, and it is exactly the part scripting is bad at.
  This is what shipped, and the measurement went further than the prediction:
  the policy is most valuable holding about **7%** of ticks, and widening its
  window past ~150px *loses the objective* while still improving combat. **The
  narrow scope is not a cautious first step — it is the result.** `docs/BOTS.md`,
  and `docs/ENV.md`, "Phase 4 re-measured".
- Nothing here changes the wire protocol or the simulation's observable
  behaviour. Every constraint above is on structure, not on what the game does.

## Where items 9-12 came from

They are `gg2-rl/docs/CONTRACT.md`'s "What gg2-server does not yet promise".
That repo is the trainer; this one owns the simulation, the env surface, the
observation layout and the version stamp. The split is deliberate and the
asymmetry is the point: anything needing `WorldState` internals, or needing to
be fast in the tick loop, belongs here, and `gg2-rl` should be deletable without
this repo losing a feature.

## Reference

Facts about the GM8 implementation cited above were read from the **bot fork**
— `github.com/kylelmoy/Gang-Garrison-2` (branch `bot-route-variety`), not the local
stock tree, which has no `Scripts/Bots/`: `Source/gg2/Scripts/Bots/botAimLead.gml`,
`Source/gg2/Scripts/Bots/botInputUpdate.gml`, and
`Source/gg2/Scripts/GameServer/processClientCommands.gml`.
