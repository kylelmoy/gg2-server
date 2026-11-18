# Proposal: server support for client-side prediction

**Status: proposal. Nothing here is implemented, and nothing should be until
Kyle approves it.** Asked for by gg2-client (its `docs/DECISIONS.md`, D12 item
2): predict the local character only, rewind to the replica's state on each
server tick, and replay the inputs the server has not applied yet. Remote
players stay as they are. This document is the server half: what the wire
carries, what the simulation offers, and what to predict.

## The answers

| Question | Answer |
|---|---|
| 1. Input acknowledgement | Yes. One new client byte asks for it and two new server messages answer it, sent only to the client that asked. **The client adds no sequence numbers**: TCP keeps INPUTSTATEs in order, so the server counts them and reports the count it applied, immediately before each state update. |
| How the client tells it is talking to gg2-server | The reply to that byte. GM8, and gg2-server builds older than this, ignore it and never reply. |
| 2. Simulation API | **Nothing new for v1.** `WorldState` is a value type built for forked rollouts: copy the replica and run `MatchSimulation.Tick` on the copy, with the local input substituted and everyone else's held. The server adds gates that make "a forked tick has no hidden state" a tested claim. A one-character step is the fallback if measurement says a full tick costs too much on a phone. |
| 3. Per-tick own state | Not in v1. The rewind target is the replica's own simulated character, which already tracks the server between QUICK_UPDATEs (D12 measured a median correction of 0.13 px). A per-tick quantised position would add error. A full-precision version is described below with the measurement that would justify it. |
| 4. Lag compensation | **No**, and not as an opt-in either. The reasons are below. |
| 5. What to predict | Your own movement, keys and own-weapon effects on yourself. Never damage, death, scoring, objectives or anything another player decides. The table is below. |

## 1. Input acknowledgement

### What "applied" means on this server

`GameServer.Tick` runs in this order every tick, and the order is what makes an
acknowledgement exact:

1. Read every client's commands, up to `ClientCommands.PerTickLimit` (10) per
   player. Each INPUTSTATE is assigned to `NetworkInputSource.Latest`, which
   also accumulates its key edges.
2. `GatherInputs`, then `ApplyInput` per living slot. The tick's input is the
   **last** INPUTSTATE read, plus the press and release edges of **every**
   INPUTSTATE read this tick (`NetworkInputSource.TakeEdges`).
3. `WriteStateUpdate`: INPUTSTATE (or QUICK_UPDATE every 7th tick) carrying
   **this tick's keys** and, on a QUICK_UPDATE, **last tick's position**.
4. `AdvanceSimulation`: the tick's step.
5. `FlushBroadcast`: per-player bytes already queued, then the broadcast.

So when the client's replica applies update T and steps, its local character
is the server's character after tick T, and that state includes exactly the
INPUTSTATEs the server read up to step 2 of tick T. That count is the
acknowledgement.

### Implicit numbering

The client numbers its INPUTSTATEs 1, 2, 3, ... on this connection, in the
order it writes them. The server counts the INPUTSTATEs it has read on the same
connection. TCP delivers them in order and loses none, so both ends assign the
same number to the same input and **nothing on the client-to-server wire
changes**. Input #N is the N-th INPUTSTATE written after the connection reached
the in-game command loop. Commands that are not INPUTSTATE (class change,
sandvich, zoom, build sentry, drop intel and the rest) are not counted and are
not predicted.

### The wire

Three message ids no stock build uses. `MessageType` in this repo takes ids up
to 60 plus 255. These sit well clear of that.

| Id | Direction | Name | Payload |
|---|---|---|---|
| `0xC0` (192) | client to server | `PREDICT_HELLO` | none |
| `0xC0` (192) | server to client | `PREDICT_WELCOME` | `ubyte version` (1), `uint inputsRead` |
| `0xC1` (193) | server to client | `PREDICT_ACK` | `uint inputsApplied` |

- **`PREDICT_HELLO`** is a bare byte, with no payload. That is what makes it safe
  against GM8. `processClientCommands` silently ignores any byte outside its
  length table and reads the next byte as a fresh command (`docs/PROTOCOL.md`,
  "Client to server commands"). A payload would be parsed as commands on a GM8
  server, so there is none. A later version is a different byte, not a field.
- **`PREDICT_WELCOME`** goes to that client only, once. `inputsRead` is the
  server's count at the moment it read the hello. The hello travels in the same
  ordered stream, so the count must equal the number of INPUTSTATEs the client
  wrote before the hello byte. **The client should check that.** A mismatch is a
  bug on one side, and the client should not predict on that connection.
- **`PREDICT_ACK`** is sent once per tick to every client that has been welcomed,
  queued as the last per-player bytes before `FlushBroadcast`.
  `inputsApplied` is the number of this client's INPUTSTATEs consumed into
  ticks so far, merged ones included ("The input queue" below). **It applies to the
  next INPUTSTATE or QUICK_UPDATE in the stream.** A CAPS_UPDATE or plugin
  packet may come between them, but no other state update can. On a tick where
  the server does not advance (only `--control-port` freeze does that), the
  count repeats.
- `uint` counts never need a wrap rule: 2^32 inputs at 30 Hz is four years.

Cost: 5 bytes a tick per opted-in client, 150 B/s, next to a broadcast of tens
of bytes per player per tick.

### Detecting gg2-server, and the fallback

The client sends `PREDICT_HELLO` once, after its first state update arrives.
If `PREDICT_WELCOME` arrives, the server is gg2-server with this feature, and
the client predicts. If nothing arrives within two seconds (the same wait the
chat hello uses), the server is GM8 or an older gg2-server, and the client
runs as it does today. Whether to fall back to RTT estimation there is the
client's call. Nothing here depends on it.

**gg2-client must recognise `0xC0` and `0xC1` before it sends the hello.** D12
item 7 fails the session on an unknown leading byte, so an old client must
never send the hello. The server sends neither message unasked, so an old
client never receives one.

### Why not the other two options

- **(a) Inside the `chat` packet id.** It works only where chat is advertised.
  `[Chat] Enabled=0` exists and turns that off. It costs PLUGIN_PACKET framing
  (4 bytes) on every ack. It also overloads gg2-chat2026's `0x80` envelope,
  whose contract is "exactly one native chat message". Prediction metadata does
  not belong to a chat protocol, and a client that skipped the plugin download
  could not opt in.
- **(b) At the greeting.** The handshake has no spare field in either direction.
  Client to server, a GM8 server **drops the connection** on any unknown byte
  in `STATE_EXPECT_COMMAND` (`docs/PROTOCOL.md`, "Handshake"), so probing there
  costs a reconnect against every GM8 server. Server to client, the greeting is
  `serverName`, the map, and a plugin list that a stock client downloads from,
  and none of those can carry a marker a stock client would ignore.

### Why this is allowed under "the rule"

Prediction runs only in the client. The server's only change is the input
queue below, which applies to a welcomed client only and is argued there. A
stock client receives a byte-identical
stream, because the new messages go only to a connection that sent the hello.
A stock client never sends the hello. A stock server ignores the hello. That
is the "cannot see the difference at all" arm of `CLAUDE.md`'s test for a new
feature.

### What the server change is

For approval, not yet written:

- `ClientCommands`: `0xC0` with length 0. `MessageType`: `PredictHello`,
  `PredictWelcome`, `PredictAck`.
- `ClientPlayer`: `uint InputsRead`, incremented in the `InputState` case of
  `Handle`; `uint InputsApplied`; `bool Predicting`.
- `NetworkInputSource` gains a queued mode, switched on by the welcome. An
  INPUTSTATE is enqueued rather than assigned. `GatherInputs` consumes one
  input, applies the depth and drain merges, and advances `InputsApplied`.
  Without the welcome, nothing about the source changes.
- `[Server] PredictionMaxQueuedInputs` (2) and `PredictionQueueDrainTicks`
  (30), in `ServerSettings.All`.
- `Handle(PredictHello)`: if prediction is enabled and the client is not yet
  `Predicting`, send the welcome and set the flag. A repeated hello does
  nothing, so it cannot make the server send more than one welcome. No log
  line per hello (`docs/HARDENING.md`: a client-driven log line is throttled
  or absent).
- `Tick`: before `FlushBroadcast`, one `PREDICT_ACK` per `Predicting`
  client.
- `[Server] ClientPrediction`, default 1, in `ServerSettings.All`, so an
  operator can turn it off. Off, the hello is ignored as it would be on GM8.
- The recorder is unaffected: it tees the broadcast only.

Gates:

- A socket client that never says hello receives the same bytes, tick for
  tick, as it does today, with prediction on. This is the stock-client claim.
- After N INPUTSTATEs and a hello, the welcome says N.
- Every state update a welcomed client receives is preceded by exactly one ack.
  When two INPUTSTATEs arrive in one tick, the ack moves by 1 on that tick and
  1 on the next, and the broadcast keys follow the two inputs in turn. When none
  arrive, the ack does not move.
- The queue cap holds: a burst of five inputs leaves the depth at the cap, with
  the ack counting the merged ones. A press and release merged into one tick
  still reaches `PressedKeys`. A standing depth of one drains after
  `QueueDrainTicks`.
- The ack equals the input count that actually reached `ApplyInput`. This test
  should go red if the capture moves below `WriteStateUpdate`.
- An unwelcomed connection's coalescing is unchanged.
  `NetworkInputSource.TakeEdges`'s existing gates stay green untouched.

### The input queue: one input per tick for a welcomed client

**In v1, because it decides what the ack means.** Adding it later would change
the meaning of the same field.

Without a queue, the server coalesces. If two INPUTSTATEs arrive in one tick,
it applies the last one's keys plus both inputs' edges. If none arrive, it holds
the last keys. The client's replay gives every input a tick of its own, so
every burst costs a tick of misprediction that reconciliation then corrects.
On mobile, where TCP batching makes bursts common, that shows as jitter.

So a welcomed client's inputs go through a small queue on its connection:

- **One input is consumed per advancing tick.** Its keys and aim become the
  tick's input, and its edges are taken against the previously consumed input,
  exactly as `ApplyInput` takes them. `inputsApplied` counts **consumed**
  inputs, and the replay is exact whenever the queue is neither empty nor
  merging.
- **Empty queue: hold.** The tick keeps the last consumed input, as today. A
  late input is a misprediction the ack cannot prevent, only report.
- **Bounded depth.** Inputs waiting after the consume are capped at
  `MaxQueuedInputs` (proposed: 2, about 67 ms). Past that, the two oldest merge
  into one tick, and both are counted. The merge keeps the later one's keys and
  ORs both inputs' edges, so a tap is never lost. Without the cap, one stall
  would add permanent latency.
- **Standing latency drains.** If at least one input has still been waiting
  after the consume for `QueueDrainTicks` consecutive ticks (proposed: 30, one
  second), the two oldest merge once. A gap followed by a pair of inputs
  otherwise leaves one tick of queueing in place for the rest of the
  connection.
- **What it costs.** A burst waits up to a tick or two instead of landing at
  once. That is latency only for the opted-in player, and prediction hides it
  from them. The two knobs are a jitter buffer's knobs. The proposed values are
  starting points, to be tuned against gg2-client's measured correction, not
  settled numbers.

**Why this is allowed under the rule.** The queue changes which tick an
opted-in player's input lands on. The broadcast carries the keys the server
actually applied, every tick, to every client. So every stock client
re-simulates the same thing the server did, exactly as it does for a player
whose packets simply arrived later. Nothing is simulated that the wire does not
carry.

**Interactions:**

- `AimCheck` and aim analysis judge each INPUTSTATE as it is read, before the
  queue, so they see the same stream as today.
- `ClientCommands.PerTickLimit` still bounds how many commands are read in a
  tick. The queue bound is separate and smaller.
- A dead player's inputs are consumed or held as today. gg2-client sends
  INPUTSTATE only while alive, so its queue empties on death.
- The merge is what makes a misbehaving client harmless. A client sending
  faster than 30 Hz is merged down to one tick per tick. It cannot buy extra
  ticks of movement, and it cannot grow the server's memory.

**Send exactly one INPUTSTATE per predicted tick**, or the mapping from input
number to tick breaks.

## 2. The simulation side

### Recommendation: replay a fork of the whole world

The procedure, per server tick received:

```
replica: apply update T, step                       (as today)
ack     := PREDICT_ACK that preceded update T
fork    := replica                                  (struct copy)
for each local input k in (ack, latest]:
    inputs[s] := held input of slot s, for every s  (from CharacterState)
    inputs[local] := input k
    MatchSimulation.Tick(world, ref fork, inputs, ref scratchEvents, spawnPoints: null)
predicted local character := fork.Characters[local] (movement fields only)
```

This needs no new server API, because the design already supports it. `WorldState`
is a flat value type with no references (`docs/SIMULATION.md`, "The state
itself"). `Tick` is a pure function of `(CollisionWorld, WorldState, inputs)`,
and the RNG lives in the state, so a fork forks the stream with it.
`docs/BOTAI.md` item 1 exists so that rollouts like this work. It is also better
than a one-character step on the mechanics that matter most for feel:

- **Your own rocket and mine jumps are predicted.** The fork spawns your rocket
  from your predicted position, and its explosion knocks you back in the fork.
  A one-character step cannot do that: the knockback would arrive an RTT late,
  as it does today, and then snap.
- **Moving platforms, move boxes and gates are predicted**, because they are in
  the state and advance with it.
- **Projectiles already in flight hit you in the fork when they will hit you on
  the server.** Only projectiles that remote players fire inside the RTT window
  are missed, and that is the irreducible part.

Holding remote inputs is what the server itself does when a player's packet is
late (`NetworkInputSource`, "holding the last input is the point"). A held
input reapplied through `ApplyInput` creates no new edges, so it does not
re-trigger a jump or a shot.

### Rules for reading the fork

- **Read only the local character's movement state from it**: position,
  velocity, `moveStatus`, facing, taunt and cloak. Never read `Alive`, hp, a
  death, a kill, a capture, an intel pickup or any `TickEvents` output. Those
  come from the replica, which a server update corrects.
- **Use a separate `TickEvents` buffer** for the fork. Its forced-fire-seed
  input exists for the parity replayer, and a leftover one would steer a
  predicted shot.
- **Pass `spawnPoints: null`**, as the replica should, so the fork never
  respawns anybody.
- **Never draw a remote character, projectile or sentry from the fork.** They
  are extrapolated a full RTT on held keys, and remote players were scoped
  out deliberately.
- **Spread does not match.** Seeded weapons draw from `WorldState.Random`, and
  the fork's stream is not the server's once remote players fire. A
  predicted shotgun, minigun or flamethrower spread is a guess. The real one
  arrives in WEAPON_FIRE.
- If the local character is dead in the replica, do not predict.

### Determinism

On one operating-system family, a forked tick is bit-exact with the server's.
Across families it is not: Windows, Linux and Apple's math libraries differ by
1 ULP on `sin`, `cos`, `tan`, `atan2`, `exp`, `pow` and `log` for a few percent
of inputs (`docs/SIMULATION.md`, "The same build on two machines", measured
Windows against Linux; Apple is unmeasured and should be assumed to differ).
**For prediction that does not matter.** The fork restarts from the replica on
every server tick and runs at most RTT/33 ms ticks, so a 1 ULP difference cannot
compound past a few ticks. The replica's own drift is the existing D12 figure.

### Cost, and what is not measured

- The copy: `WorldState` is 156.6 KB, 55% of it the projectile pool
  (`WorldStateTests.ACloneStaysWithinItsBudget`). One copy per server tick.
- A tick: a twelve-bot server's whole tick, including bot decisions and
  serialisation, is about 0.104 ms of process CPU on desktop x86 in Release
  (`docs/BOTS.md`). The simulation alone is less. Ten replays come to at most
  about 1 ms on a desktop.
- **Unmeasured: either number on a phone.** That measurement decides whether
  the fallback below is needed. Measure it in gg2-client as
  (copy + N x Tick) at N = 10 on the slowest target device, with a full
  twelve-player server's state.

### What the server adds for this

- **A gate that a fork has no hidden state.** Two copies of one state, ticked
  with the same inputs, stay bit-identical for a whole recorded match. Ticking
  one copy leaves the other, and the `CollisionWorld`, unchanged. Today this is
  true by construction: the only `CollisionWorld` writer is `SetCaptureZones`,
  at load. But nothing goes red if a future change adds a static or a
  `CollisionWorld` write inside `Tick`. That is the claim this proposal rests on,
  so it gets a test.
- **`PlayerInput.HeldBy(in CharacterState)`**, the input a slot's current
  `KeyState`, `NetAimDirection` and `AimDistance` describe, so the client does
  not rebuild it field by field.

### The fallback: a one-character step

Use this only if the phone measurement says the full tick is too slow. It would be a
`MatchSimulation` entry point that runs one slot through its per-character
phases (`ApplyInput`, the mirrored flags, `BeginStepInput`, `CloakStep`,
`BeginStepAccelerate`, `BeginStepUberEffects`, `Step`, `HeavyStep`, `EndStep`)
against an otherwise frozen world. It costs a refactor of `MatchSimulation.Tick`
so the full tick and the one-slot step share the per-slot code. Without that
sharing they drift apart silently. `MatchSimulation.cs` is serialised across
lanes (`docs/PARALLEL.md`). It also loses the three advantages above. The
character physics itself would allow it: `CharacterPhysics`'s phases take only the
`CollisionWorld` and the character (and its weapon at `EndStep`), so a
character's movement reads no other character and no sentry.

## 3. Update cadence

No per-tick state for v1. The rewind target is the replica's local character,
which is the server's own simulation run on the server's own keys. D12
measured its correction at each QUICK_UPDATE at a median of 0.13 px and a p99
of 1.6-2 px. A per-tick position in the wire's encoding (`x*5`, velocity
`*8.5`) would be worse than that: it would inject up to 0.1 px of quantisation
on every tick, where the replica takes it once in seven.

**If the client later measures its own character's correction at QUICK_UPDATE
above the quantisation floor** (more than about 0.1 px of position, regularly,
on one map), the remedy is version 2 of the ack: the local character's `x`,
`y`, `hspeed` and `vspeed` as full doubles (32 bytes), on QUICK_UPDATE ticks or
every tick, to the welcomed client only. Every byte goes only to that one
client, so it costs a stock player nothing. It is not proposed now because
nothing measured asks for it.

## 4. Lag compensation

No, and not as a per-server option either.

- **It changes what stock players experience**, which a client feature may not
  do. A GM8 player would be hit after reaching cover, by a shot the server
  rewound time to allow.
- **Most of GG2's weapons are not hitscan.** Rockets, mines, flames, needles,
  bubbles and shotgun pellets are projectiles that every client simulates from
  WEAPON_FIRE. Rewinding the target's position at the moment of firing does not
  apply to a projectile that travels for a dozen ticks.
- **The server keeps no history** and receives no client timestamp. Both would
  be new state for a feature whose output, a different hit, every client would
  see.

Prediction without lag compensation means your own shots leave from where
the server has you, behind your predicted character by about an RTT of
movement. The client chooses how to draw that: from the replica (correct, offset
from your sprite) or predicted from the fork and replaced when WEAPON_FIRE
arrives (aligned, but with a guessed spread).

## 5. What to predict

"Fork" means the replayed copy. "Replica" means the state as the server last
said it.

| Mechanic | Predict? | Why |
|---|---|---|
| Walking, jumping, scout double jump, dropping through platforms | Yes, from the fork | Pure function of your keys and the map |
| Taunt (`KEY_TAUNT`) | Yes | A key bit, and it freezes the character (`BeginStepInput`), which is exactly the latency players feel. Humiliation blocks it through a flag the fork reads from the state |
| Heavy slowed while firing, spy cloak | Yes | `HeavyStep` and `CloakStep` read your own keys |
| Rocket jump, mine jump (right-click detonation) | Yes, from the fork | Your own projectile from your own predicted position. The server's will differ by however far the fork's start differs |
| Moving platforms, move boxes | Yes, from the fork | In the state, and deterministic |
| Setup gates, intel gates, team gates | Yes, from the fork's flags | Mirrored onto the character each tick from state. A gate opening or closing inside the RTT window snaps |
| Airblast, explosions or sentries pushing you | From the replica (the fork shows only what is already in flight) | Another player's decision. It arrives as a snap at the rewind |
| Damage, hp, death, kill boxes and pits, respawn | **Never** | The fork can kill you in ways the server will not. Take all of it from the replica |
| Intel pickup, drop, capture; control-point capture | **Never** | Scoring and objective state. The fork may run them, but do not show them |
| Ubers, healing, resupply cabinets | From the replica | Another player's decision or a server-side timer. Uber state affects no movement |
| Sniper zoom, sandvich, class or team change, build or destroy sentry, drop intel | From the replica | They are commands, not INPUTSTATE bits. They are not counted in the ack and apply when the server applies them, as today |
| Round end, humiliation, Arena round state | From the replica | Global state. The fork reads it from the copy and is right until it changes |

## What would show this has gone stale

- A stock client is sent a byte of the new messages. The no-hello gate is what
  catches that.
- `GameServer.Tick` reorders input gathering, `WriteStateUpdate` and the flush.
  Each of the ack's meanings above depends on that order.
- `MatchSimulation.Tick` gains a dependency outside its arguments. The fork gate
  catches that.
- A future GM8 or upstream release assigns `0xC0` or `0xC1`, or makes an
  unknown in-game byte fatal.

## For Kyle to decide

- Approve the wire as specified (`0xC0` hello and welcome, `0xC1` ack, implicit
  numbering), or name changes.
- Default on (`[Server] ClientPrediction=1`) or off. On is proposed: a stock
  client cannot tell, and the fleet would serve gg2-client without a config
  push.
- Lag compensation stays out. Confirm.
