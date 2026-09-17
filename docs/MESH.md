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

## What it costs

Measured on a whole match — twelve bots, projectiles, deaths, an objective —
played across two nodes with a three-tick link, `MeshMatchTests`:

| | |
|---|---|
| Live ticks | 600 |
| Ticks actually simulated | 2,127 |
| Rollbacks | 509 |
| Re-simulated ticks | 1,527 |
| Deepest rollback | 3 |
| Inputs outside the window | 0 |

The cost scales with the link, because every rewind replays the whole delay:
1.7x at 3 ticks, 6.7x at 8, 12x at 15 and 24x at 30. A transatlantic link is the
cheap end of that.

**0.72 extra ticks of work per live tick, so about 1.7x a plain server.** A
twelve-bot tick costs 0.151ms in Release against a 33.3ms budget, so a node's
tick is about 0.26ms — under 1% of the budget. The deepest rewind is exactly the
link's delay, which is what it should be: nothing ever needs to go back further
than the oldest input still in flight.

⚠️ **A human-populated match would rewind LESS than this.** Bots change their
keys about twice as often as people do — measured, `tools/inputpredict` — so a
bot match is the pessimistic case for a scheme whose whole cost is driven by how
often a guess is contradicted.

## Holding what cannot be unsaid

A death starts a respawn, a capture moves the score, an intel pickup attaches
the briefcase. None of them can be taken back on a stock client, so a node may
only send one once the tick that produced it can no longer be re-simulated into
a tick where it did not happen.

A tick is **confirmed** when every player alive at its start has had their real
input heard for it. A confirmed tick cannot be rewound, because a rewind is only
ever scheduled by an input contradicting a *guess*. `MeshNode.TryTakeConfirmed`
hands those out in order and nothing else; `LastEvents` is the provisional view,
for movement and diagnostics.

⚠️ **Certainty is recomputed from the ledger rather than recorded when the tick
ran.** The common case is a guess that turns out to have been right: the input
arrives, contradicts nothing, and the tick is never re-simulated — so a flag
written while the tick ran says "guessed" for ever. Measured: a node confirmed
exactly nothing across a 600-tick match before this was a recomputation.

⚠️ **Positions must NOT wait for confirmation.** The client is built to be
corrected several times a second, and holding movement until it was certain
would hand back the whole latency saving the design exists for. Send movement
provisionally; hold what cannot be unsaid.

### How wrong the provisional stream actually is

Deaths in the provisional stream against the ones that really happened, 600
ticks of a twelve-bot match, by link length:

| link | provisional | real | phantom | missed |
|---|---|---|---|---|
| 3 ticks | 5 | 5 | 0 | 0 |
| 8 ticks | 2 | 1 | 2 | 1 |
| 15 ticks | 3 | 4 | 2 | 3 |
| 30 ticks | 3 | 6 | 0 | 3 |

**It is wrong in both directions**: it publishes deaths a rewind then unmakes,
and it misses deaths that only exist once the correction lands. A client reading
it would see a player die who is alive, and another go on living who is dead.

⚠️ **At the three ticks a transatlantic link costs, nothing went wrong in this
match** — five deaths, all real. That is a small sample and not a licence to
skip the holding; it says the failure is rare at that range and ordinary further
out. The rows are different matches, because bots seeing different worlds make
different decisions.

## It has been run across the real internet

`gg2-server mesh` plays one match across two nodes and a real link. Both ends
fill the match with bots, own half the roster, send their own players' inputs
and receive the other half; each sends a digest of a confirmed tick once a
second and says so loudly if the peer disagrees.

**barbara (Ubuntu, home) to us-west (Debian, VPS), 58ms RTT, 900 ticks, twelve
bots:**

| | barbara | us-west |
|---|---|---|
| Final digest | `873a7b9014e7bfb8` | `873a7b9014e7bfb8` |
| Confirmed ticks | 899 | 900 |
| Ticks simulated | 1,559 | 1,516 |
| Rollbacks | 659 | 601 |
| Deepest rewind | 1 | 2 |
| Inputs outside the window | 0 | 0 |
| Digest checks | 29 agreed, 0 diverged | 29 agreed, 0 diverged |

**The two machines ended in the same world**, having exchanged nothing but
inputs. The re-simulation cost, 1.73x and 1.68x, is what the lab measurement
predicted. The deepest rewind is one or two ticks rather than the link's
1.7-tick round trip, because what a node waits for is one WAY.

**And the check catches a real divergence.** The same run between a Windows node
and a Linux one — the pair `docs/SIMULATION.md` measures as disagreeing in the
last bit of `cos` — reported `DIVERGED at tick 468` after 18 checked ticks had
agreed. The safety mechanism is not a stub that always prints zero; it has been
seen to fire, on the one case whose answer was known in advance.

⚠️ **Two nodes must agree what tick it is**, and a local counter each is not
agreement. The listener names a wall-clock origin when the peer ARRIVES — not
when it started waiting — and both derive the tick from the clock after that.
Measured while getting this wrong: a node whose origin was in the past sprinted
through a backlog while its peer sent it the present, and refused all 4,032
inputs it received.

## What is NOT built

⛔ **Nothing here reaches a socket or a client yet.** Two pieces stand between
this and a playable match:

1. **The input link.** Sockets between nodes, a shared tick timeline, and clock
   offset estimation.
2. **Correction smoothing.** A node can reproduce exactly what each of its
   clients believes, because the client's dead reckoning is the same
   deterministic simulation — so it can spread a correction over several ticks
   instead of teleporting. Unmeasured and unimplemented; whether a 16 px
   correction reads as a snap needs a person watching a stock client.

A fourth thing is decided but unwritten: **non-input events** — joins, leaves,
chat, map changes — need a consistent order across nodes, which means either
scheduling them at an agreed future tick or giving one node the job of
sequencing them. They are rare enough to afford a round trip.
