# Running one match in more than one place

A GG2 client applies nothing locally. `Character.events/User Event 13.xml:18-19`
sets its own `keyState` and `aimDirection` only from what the server sends back,
so every key press costs a full round trip before anything moves, and no
server-side change can shorten that for a player who is far from the server.

What can be changed is *where* the server is. This is the design for putting one
node in each region, and the state of what is built.

## The shape

Each node runs the whole simulation and serves its own players as an ordinary
GG2 server. Nodes exchange **inputs only** — 4 bytes per player per tick, about
1 KB/s for eight players.

Each tick a node:

1. takes its own players' inputs, which are a few milliseconds old and exact;
2. guesses the inputs still in flight from the other region;
3. simulates, and serves the result to its clients;
4. when the real remote inputs land two or three ticks later, compares them
   against the guess, and where they disagree **rewinds to that tick and
   re-simulates everything since**.

Two nodes therefore reach the same world as soon as they know the same inputs.
There is no authority, nothing to hand off, and neither region waits a round
trip for the other's verdict — only one way for the other's keys.

`MeshNode` is that loop. It is a pure function of (state, inputs, arrival
order), with no I/O, so two nodes can be run in one process against a scripted
link and asserted to converge, which is what `MeshNodeTests` does.

## Why it is possible here

| Requirement | Why it holds |
|---|---|
| A tick is a pure function of (state, inputs) | `MatchSimulation.Tick`, `CLAUDE.md`'s first non-negotiable |
| A world can be copied and restored exactly | `WorldState` is a value type holding no reference to anything |
| A rewind is affordable | a tick costs ~0.151ms against a 33.3ms budget |
| Two machines agree bit for bit | measured across the fleet — `docs/SIMULATION.md`, "The same build on two machines" |
| The guess is usually right | 82-87% at two to three ticks — `tools/inputpredict` |
| Being wrong is cheap | 0.45 px mean at two ticks, the size of correction a client already absorbs |

⛔ **The determinism requirement is per operating system.** Windows and Linux
disagree in the last bit of `cos`, so a mesh whose nodes are not one OS family
would diverge continuously. The fleet is Debian and Ubuntu and is uniform.

## What is built

`src/Gg2.Mesh/MeshNode.cs`, gated by `tests/Gg2.Tests/MeshNodeTests.cs`:

- `Submit(tick, slot, input)` — the only way a node learns anything. Local
  players' inputs arrive before their tick; a peer's arrive after it has been
  simulated on a guess, and schedule a rewind when they contradict it.
- `Advance()` — rewind if something contradicted a guess, then simulate a tick.
- `Settle()` — apply what has arrived **without** advancing the clock. Two nodes
  are only comparable after this; a further tick would have no inputs at all, so
  each would guess at it alone.
- `Stats` — ticks, rollbacks, re-simulated ticks, deepest rewind, and inputs
  that arrived outside the window.

The guess is "they are still holding what they were holding", which is measured
rather than assumed and which no cheaper rule beats.

⚠️ **Correctness comes from the rewind, not from the guess.** A node told to
guess nothing at all still converges — it just re-simulates far more to get
there. `MeshNodeTests.AWorseGuessCostsMoreRewindingAndStillConverges` pins that
separation, which is what makes a better predictor a performance question and
never a correctness one.

⚠️ **An input outside the window is counted, not applied.** Older than
`MaxRollbackTicks` and the state to rewind to is gone; further ahead and there
is nowhere to put it without overwriting history a rewind needs. Either way the
honest answer is `MeshStats.TooLate` and a resync from a peer, not a quiet
divergence.

## What is NOT built

⛔ **Nothing here reaches a socket or a client yet.** Three pieces stand between
this and a playable match:

1. **Holding irreversible events.** A death, a capture or an intel pickup cannot
   be taken back on a stock client — `PLAYER_DEATH` starts a respawn. Those have
   to be held until every input for their tick is known, roughly 75ms, while
   movement goes out provisionally. `MeshNode` produces the corrected world;
   deciding what of it is safe to send is a layer that does not exist.
2. **The input link.** Sockets between nodes, a shared tick timeline, and clock
   offset estimation.
3. **Correction smoothing.** A node can reproduce exactly what each of its
   clients believes, because the client's dead reckoning is the same
   deterministic simulation — so it can spread a correction over several ticks
   instead of teleporting. Unmeasured and unimplemented; whether a 16 px
   correction reads as a snap needs a person watching a stock client.

A fourth thing is decided but unwritten: **non-input events** — joins, leaves,
chat, map changes — need a consistent order across nodes, which means either
scheduling them at an agreed future tick or giving one node the job of
sequencing them. They are rare enough to afford a round trip.
