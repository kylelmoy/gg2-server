# Proving the port matches

Physics parity is the risk that sinks this project, and it is not the kind of
bug playtesting finds — a 0.3 px/tick divergence looks like "the netcode feels a
bit rubbery", which GG2 already does. **Build the harness before the physics.**

## The key realisation

The reference server's own broadcast stream is a complete golden trace.

Every tick it sends `INPUTSTATE`, which carries **every player's `keyState` and
aim**. Every seventh tick it sends `QUICK_UPDATE`, which carries **every
player's position, velocity and hp**. So one recording of a real match contains
both the inputs the simulation consumed and the outputs it produced, already
quantised to exactly the precision the client can observe.

That gives a test with no instrumentation, no bridge, no GML changes and no
game running:

```
record:   connect a passive recorder to the reference GM8 server, dump every byte
replay:   feed the recorded keyStates into the ported server tick by tick
compare:  the port's QUICK_UPDATE fields against the recording's, per character
```

If they agree, the stock client cannot tell the two servers apart. That is the
whole specification, and it is checkable in CI.

## Tier 0 — the recorder

**Built** — `Gg2.Parity`, the `gg2-parity` console app. `record` joins a server
as a passive spectator and writes its broadcast stream to a file; `diff`
compares two recordings and reports the first differing byte with a window
either side.

It earned its place before the physics did. Pointed at the reference GM8 server
and at this port in turn, on `ctf_truefort` with one spectator, the two
recordings came back **569 bytes each and byte-identical except the CTFHUD round
timer** — which turned "the client does not like my stream" from a guess into a
four-byte value difference in about ten minutes. That comparison is worth
re-running whenever the serialisation changes; it is much cheaper than a client.

A ~200-line console app. Speak the handshake from `PROTOCOL.md`, `RESERVE_SLOT`,
`PLAYER_JOIN`, then never send an `INPUTSTATE` of your own; just parse the
inbound stream into a list of framed messages and write it to disk with a tick
index.

**Framing is built too** — `Gg2.Protocol.ServerStream` and
`StateUpdateReader`. `record` now frames what it captured and prints a message
census before it exits, because a trace that cannot be framed is not a trace and
finding that out while the server is still up is much cheaper than finding it
out in CI.

The stream is **not self-delimiting**, and this is the part worth knowing before
writing a reader: a state update's width comes from three places at once.

1. **The roster**, which the reader has to track exactly as the client does —
   including the compaction on `PLAYER_LEAVE`.
2. **The map**, via `WorldShape`: the gamemode picks the HUD block, and both the
   control-point count and the `MovingPlatform` count are map facts that appear
   on the wire with no count prefix.
3. **Bytes inside the update.** A player's `subobjects` byte decides whether a
   character and a sentry follow; a sentry's hp byte carries "built" in bit 7
   and an unbuilt one has an extra `vspeed` byte on a `FULL_UPDATE`; a demoman's
   `Minegun` writes `lobbed` and then that many seven-byte mine blocks.

Point 3 has a consequence that is easy to miss: **the reader must know each
player's class**, because the weapon block that closes a character's
`FULL_UPDATE` is class-dependent.

⚠️ **This document used to say that block was zero bytes for eight classes out
of ten, and it is not. It is never zero.** The reasoning was that only `Medigun`
and `Minegun` have a `User Event 12` in `Objects/Weapons/` — true, and
irrelevant, because **GM8 runs a parent's event when the child does not define
one**. Every weapon has `<parent>Weapon</parent>`, and
`Objects/InGameElements/Weapon.events/User Event 12.xml` writes `readyToShoot`
and `alarm[0]`. The client agrees from the other side: `Weapon`'s
`User Event 13` is a two-byte `receiveCompleteMessage`.

Measured on the running engine on 2026-08-25 — swap `global.serializeBuffer` for
a fresh one, call `with(currentWeapon) event_user(12)`, and read `buffer_size`
either side:

| class | weapon | bytes |
|---|---|---|
| Medic | `Medigun` | **1** — heal target only |
| Demoman | `Minegun` | **3 + 7n** — the parent's two, `lobbed`, then the mines |
| every other class | | **2** |

`Medigun` and `Minegun` differ by one thing that is invisible in the GML text:
`Minegun`'s `User Event 12` opens with an `action_inherited` in its XML and
`Medigun`'s does not. Reading the code alone cannot tell you that; reading the
`.xml` — or measuring — can.

**The reason this survived four phases is worth more than the fix.** Every
recording in the repo was made by `gg2-parity record`, which joins immediately,
and a `FULL_UPDATE` is sent *on join* — so every fixture was captured before any
bot had spawned, and not one of them contained a character inside a full update.
The path was not undertested; it was unreachable by the fixtures that existed. A
recording made after the bots spawned fails to frame on its very first
`FULL_UPDATE`.

`tests/Gg2.Tests/Fixtures/reference-ctf_truefort-combat.trace` closes the hole:
six bots already spawned, classes forced so a medic, a demoman and four
ordinary weapons all appear in one update, plus eight `WEAPON_FIRE`s and a
`PLAYER_LEAVE` roster compaction. `gg2-parity census <trace>` prints what a
recording contains and where framing stopped, which is how this was found.

**When adding a fixture, ask what state it cannot reach.** That is the question
that would have caught this one.

A recording of the real GM8 server on `ctf_truefort` is checked in as
`tests/Gg2.Tests/Fixtures/reference-ctf_truefort-spectator.bin`, and the test
that frames it to the last byte is the one that would notice any of the above
being wrong.

### The trace format

**Built** — `Gg2.Protocol.Trace` and `TraceParser`.

**The trace is the raw stream**, with a header in front naming the map, the
reference version and where it came from. Not a parsed structure. Three reasons,
and the third is the one that matters:

1. It is lossless, so a change to the parser never invalidates a recording.
2. It is bounded and cheap: a 30 Hz broadcast to a spectator watching two bots
   is about 440 bytes a second.
3. It means the parity recorder and a live server writing training data are the
   **same path** (`docs/BOTAI.md`, item 3). A second logging format invented
   later is exactly how the two drift apart.

`TraceParser` turns it into ticks. A tick is delimited by its state update,
which is always the *last* thing the server writes for that tick —
`GameServerBeginStep.gml` queues command responses first and appends the update
afterwards, and the whole buffer goes out in End Step. `FULL_UPDATE` and
`CAPS_UPDATE` are not tick boundaries: the first is sent on the join path
outside the cadence, the second goes out *in addition* to that tick's update.

### What a wedged client looks like

Worth knowing before debugging one. `receiveCompleteMessage` is a **blocking
spin**:

```gml
do {
    if(socket_has_error(argument0)) return 2;
} until(tcp_receive(argument0, argument1));
```

There is no `io_handle()` in it. A client waiting for bytes that will not come
therefore pegs one core, stops running Draw events — so the window goes black,
not frozen-looking — and stops servicing the agent bridge, so `gg2_evalx` hangs
with no error. All three symptoms have the same cause, and none of them says
*where* the stream went wrong. That is what the recorder is for.

Note also that an unknown message byte does **not** wedge the client: it hits
`default: promptRestartOrQuit("The Server sent unexpected data.")` at the bottom
of `ClientBeginStep.gml` and puts up a dialog. A silent black screen means a
*short* message, not an unrecognised one.

A wedge is also what a client looks like **after** it has disconnected itself and
had its modal force-closed — which is what a spectator does at exactly five
minutes, of its own accord (`docs/PLAN.md`, phase 1). Check the launcher log for
a dismissed dialog before concluding the stream is at fault; `gg2-agent`'s
launcher saves a screenshot of every dialog it clicks, and that picture is
usually the whole answer.

Two things to get right:

- **Start the trace at a `FULL_UPDATE`.** It is the only message that carries
  complete initial state; a trace that begins mid-match has no defined starting
  point. The `JOIN_UPDATE`/`FULL_UPDATE` pair sent on join is the natural
  anchor.
- **Record the roster events too** (`PLAYER_JOIN`, `PLAYER_LEAVE`,
  `PLAYER_CHANGECLASS`, `PLAYER_CHANGETEAM`, `PLAYER_SPAWN`, `PLAYER_DEATH`).
  Replay needs them to keep indices and class stats aligned.

Build traces from a range of situations, and keep them in the repo as test
fixtures once they are small enough: flat ground running, a jump arc, a
double-jump, a wall-run into a step-up, a fall onto a corner, a rocket jump, a
drop-through platform, a gate transition.

**The cheapest source of a populated trace is the reference's own bots.** Set
`[Bots] Enabled=1` in the build directory's `gg2.ini`, run the dedicated server,
and record — the bots walk, jump, shoot and die on their own, which is thirty
seconds of real GM8 physics for no input driving at all.
`tests/Gg2.Tests/Fixtures/reference-ctf_truefort-bots.trace` is one such
recording: 13 KB, 898 ticks, two bots on `ctf_truefort`. Put the `gg2.ini` back
afterwards.

That covers walking and jumping but not the deliberate cases, which need
scripted input rather than a bot's own judgement.

## Tier 1 — primitives, against the live engine

Before any trajectory can match, `placeFree` must match. This is where the two
genuinely uncertain things in `SIMULATION.md` get settled.

`gg2-agent` can answer these directly from the running game: `gg2_eval` a
`place_free(x, y)` at a few thousand sampled points on a map and compare against
the C# implementation reading the same walkmask. A disagreement anywhere is a
boundary-rule bug, and it is far cheaper to find here than three layers up.

Sample deliberately at the nasty places, not uniformly: exactly on `x % 6 == 0`,
at negative coordinates, at the map edges, and at the character rectangle's own
corners.

## Tier 2 — trace replay, the main event

**Built** — `Gg2.Parity.Replayer` and `DriftReport`, driven by
`gg2-parity replay <trace>`.

The simulation it talks to is `ITraceSimulation`, which is deliberately tiny and
deliberately lives in the harness rather than in the simulation: phase 4 writes
the physics against `Tick(WorldState, ReadOnlySpan<PlayerInput>)` and an adapter
bridges the two, so the shape of `WorldState` is not prejudged by an interface
written before it exists. Everything crosses in **wire units**, because the
comparison is about the integers a client can observe.

`EmptySimulation` is the stand-in until then. It accepts characters and never
moves them, so replaying the bot fixture against it reports the movement it
failed to reproduce:

```
reference-ctf_truefort-bots.trace  [ClosedLoop]
  37 ticks, 2 comparison(s), 2 resync(s) on spawn
  8 mismatch(es), first at tick 36:
    tick 36 slot 2 x: expected 18395, got 18510 (+115)
    tick 36 slot 2 hspeed: expected -43, got 0 (+43)
    ...
  |dx| max 115, p99 115   |dy| max 90, p99 90   (wire units; 5 = one world pixel)
```

Replay the trace and diff. Two modes, and you want both:

**Closed-loop (what the server must do).** Feed inputs, never correct. Divergence
accumulates. This is the honest measure of "is the physics the same", and the
target is *exact equality of the quantised wire fields* for the whole trace. It
is achievable: both sides are doing double-precision arithmetic in the same
order, so drift means a real difference in the maths, not floating-point noise.

**Open-loop segments (what the client actually experiences).** Reset the port's
state from each `QUICK_UPDATE`, simulate forward to the next one, then compare.
This measures exactly the thing a player sees: the prediction error at the moment
of correction. Report it as a distribution, not a pass/fail — max and
99th-percentile `|Δx|`, `|Δy|` in wire units (1/5 px).

That gap is **7 ticks, not 6**. `QUICK_UPDATE` goes out when `frame mod 7 == 0`,
so consecutive ones are seven ticks apart and six `INPUTSTATE`s sit between
them — the ratio in a real recording is 6 : 1, and the tick indices in a drift
report step 36, 43, 50. An earlier draft of this document said six; a replayer
that believes it lands one tick short of the thing it is comparing against, and
reports a drift that is really just a missing tick of gravity.

Sensible gates for CI:

| Metric | Gate |
|---|---|
| Closed-loop wire-field mismatch, per fixture | 0 |
| Open-loop 6-tick max error | 0 wire units |
| Any `hp` / `ammo` / flags mismatch | 0 |

Start with the gates loose enough to be green on a walking-only trace, then
tighten to zero as each behaviour lands. A gate that has never been green tells
you nothing.

### Where replay legitimately diverges

Do not chase these — exclude them and say so in the report:

- **Server-side RNG**: team selection, chat bubbles, respawn timing, and *which*
  spawn point a character gets. `SIMULATION.md` establishes the port is free
  here. Replay should take roster/team/spawn events from the trace rather than
  deciding them.
- ⚠️ **A spawn *position* is not on that list, and this document used to say it
  was.** The advice here was "resynchronise the character's state on every
  `PLAYER_SPAWN`", and following it is what made phase 4 look like it had a
  one-wire-unit arithmetic bug for as long as it did. A resync anchors on a
  broadcast position, which is quantised to a fifth of a pixel and only sent
  every seventh tick (`GameServerBeginStep.gml:96`) — so it starts the character
  somewhere inside a bin, several ticks after it actually spawned.

  `PLAYER_SPAWN` carries a **spawn point id and a group**, not coordinates
  (`sendEventSpawn.gml`), and the client resolves the pair against the map
  itself (`doEventSpawn.gml`). Do the same: `Gg2.Maps.SpawnPoints` reproduces
  `offloadSpawnPoints.gml`'s lists, and the result is an integer coordinate with
  both speeds zero — an anchor that passed through no wire at all. Only the
  choice is RNG, and the choice is on the wire.
- **Anything after a divergence.** Report the first mismatching tick and stop;
  100 downstream mismatches are one bug.

## Tier 3 — a real client, watched

The end-to-end check the other tiers cannot give: launch a stock GM8 client
against the ported server and play. `gg2-agent` already automates every part of
this — `tools/launcher.js` starts a client, `gg2_session` manages a
server-plus-clients set, `gg2_screenshot` and `gg2_area_shot` see what is
happening, and `gg2_evalx` reads the *client's* predicted position so it can be
compared against what the port believes.

That last one is the sharpest tool in the box: `syncWrongness` already exists as
a variable on `Character`, and reading the client-side character's `x`/`y` while
the ported server runs turns "feels rubbery" into a number.

## Tier 4 — behaviour scenarios

`gg2-agent/tools/bot-scenarios.js` is an existing suite of "can a bot still walk
from A to B on map M" cases, with tick counts. Once the port runs bots, the same
scenarios should pass with comparable tick counts. This is the regression net for
the bot work that motivated the whole project, and it is worth porting the
runner early rather than reinventing it.

## Order of work

The tiers are also the order. Tier 1 gates tier 2; a trace replay against a wrong
`placeFree` produces a wall of mismatches that says nothing about the movement
code. Get `placeFree` bit-exact first, then a walking-only trace, then jumps,
then everything else.
