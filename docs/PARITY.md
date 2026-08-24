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

**Still to do:** framing. The recorder currently keeps raw bytes with arrival
times, which is enough to diff two servers but not to replay a trace. Framing
the server-to-client stream needs the roster size and the gamemode, because both
change the width of a state update — that is the next piece, and it is what
turns a recording into a fixture.

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

Replay the trace and diff. Two modes, and you want both:

**Closed-loop (what the server must do).** Feed inputs, never correct. Divergence
accumulates. This is the honest measure of "is the physics the same", and the
target is *exact equality of the quantised wire fields* for the whole trace. It
is achievable: both sides are doing double-precision arithmetic in the same
order, so drift means a real difference in the maths, not floating-point noise.

**Open-loop segments (what the client actually experiences).** Reset the port's
state from each `QUICK_UPDATE` and simulate forward 6 ticks, then compare against
the next `QUICK_UPDATE`. This measures exactly the thing a player sees: the
prediction error at the moment of correction. Report it as a distribution, not a
pass/fail — max and 99th-percentile `|Δx|`, `|Δy|` in wire units (1/5 px).

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

- **Server-side RNG**: spawn team selection, chat bubbles. `SIMULATION.md`
  establishes the port is free here. Replay should take roster/team/spawn events
  from the trace rather than deciding them.
- **Spawn positions and respawn timing**, for the same reason. Resynchronise the
  character's state on every `PLAYER_SPAWN`.
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
