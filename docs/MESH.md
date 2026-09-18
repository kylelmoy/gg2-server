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

## Smoothing a correction

A node can reproduce exactly what each of its clients believes, because the
client's dead reckoning is the same deterministic simulation. So a correction
does not have to be a teleport: `CorrectionSmoother` sends a position part of
the way from the belief toward the truth, closing a fixed fraction of the error
each update.

⛔ **This deliberately sends a position that is not the server's**, which is the
one place in this project that is on purpose. It is bounded and temporary, and
the justification is that the client is ALREADY somewhere wrong — the choice is
between arriving abruptly and arriving smoothly, not between wrong and right.

⚠️ **A correction larger than four character widths is sent whole.** A respawn
crosses the map, and the client simulates from whatever position it is given, so
gliding it there would walk a body through walls.

### What it buys and what it costs

Modelled over the real correction stream — every seventh tick, the cadence a
position update rides, on the windows where a key really was mispredicted:

| | unsmoothed | retain 0.5 | retain 0.75 |
|---|---|---|---|
| mean jump | 2.55 px | 2.54 | 2.51 |
| p99 jump | 16.30 | 11.89 | 8.12 |
| worst jump | 33.5 | 20.6 | 12.9 |
| jumps over 8 px | 7.8% | 5.7% | 1.2% |
| standing offset from truth | none | 2.54 mean | 7.54 mean |

**The mean jump does not move**, and it cannot: the same error has to be
absorbed either way. What smoothing does is trade a rare large jump for a small
permanent offset — at retain 0.75 the worst correction a player sees falls from
33.5 px to 12.9 px and the over-8px rate from 7.8% to 1.2%, and the price is a
client that sits about 7 px behind the truth all the time.

⚠️ **That is a model of a client, not a measurement of one.** It assumes the
client holds the position it was last sent and dead-reckons from there, which is
what the GG2 client does — but which of those two shapes a player prefers is not
a thing this table can say.

## Joins, leaves and class changes

A key press can be guessed at and corrected. A join cannot: either slot 4 exists
or it does not, and two nodes that disagree are not running the same match.

So a roster change is a `MeshCommand` **scheduled for a future tick** rather than
applied when it arrives. The node that decides picks a tick far enough ahead
that its peer hears first, both apply it at exactly that tick, and a rewind
through that tick applies it again — because it lives in a schedule keyed by
tick, not in whatever order the sockets delivered it.

⚠️ **The order within a tick comes from the data, not from arrival.** Two nodes
can each schedule something for the same tick and hear the other's afterwards.
`MeshCommandKind` is declared in application order — `AddPlayer`, `SetTeam`,
`SetClass`, `Spawn`, `RemovePlayer` — and ties break on slot and payload, so
every node computes the same order for itself. `RemovePlayer` is last because a
removal renumbers every slot above it.

⛔ **A command for a tick already simulated is refused**, and counted in
`MeshStats.TooLate`. Applying it would mean rewinding to a tick the peer never
rewound to, over a change the peer has never heard of.

### Crossing the link

A command is scheduled on the node that decided it **and** sent to the peer in
one call — `MeshSession.Schedule`. Both halves or neither: scheduling without
sending leaves the peer in a different match, sending without scheduling leaves
this node in one, and no ordering between them makes a half-done roster change
safe.

The lead is ten ticks, a third of a second. It has to exceed the link's worst
one-way delay plus jitter, because a command for a tick already simulated is
refused rather than applied on one machine only. The only thing it delays is how
soon a joining player appears, against the seconds a GG2 join already takes.

⚠️ **The name rides with the command**, because the peer tells its OWN clients
who joined and a slot number is not a person. It is written as bytes and not
re-encoded: a GG2 name is twenty arbitrary bytes, control characters included.

⛔ **The length byte goes BEFORE the name.** The first version put it after and
scanned for it, which mis-frames on any name whose bytes equal their own index —
invisible on loopback, and on a real link it eats the stream's alignment so
every later frame is read from the wrong offset. The gate sends exactly that
name and then asserts the NEXT frame still reads, because alignment damage only
shows downstream.

### A join does not name its slot

⛔ **A GG2 roster is dense and positional** — the wire numbers players by their
place in it, and every client tracks that. So two nodes cannot each pick a slot
for somebody joining: they would pick the same one, and the second join would
land on top of the first.

Instead a join names **who issued it and that node's own counter**, and the slot
falls out of the order both nodes already compute: the lowest free place,
assigned as the command is applied, never slot 0. Two joins landing on one tick
from opposite ends of the link take consecutive places, in the same order, on
both machines, with nobody having coordinated anything. The issuer finds out
which place its player got from `MeshNode.TryGetAssignedSlot`.

The gate has the two nodes hear the two joins in OPPOSITE orders, which is what
a real link produces — each hears its own first — and asserts they agree about
both slots *and* that the slots differ. The second half is the one that bites:
handing one place to both people satisfies every equality otherwise.

### Ownership follows the issuer, and a departure renumbers

⛔ **"My player" is read off who issued the join, never off the slot number.**
That was the same thing while slots were handed out by parity; once a join takes
the lowest free place, either node's player can land on any number. A node
guessing from parity would predict inputs for a player it is itself driving —
and never send that player's keys to the peer, which is the same player going
silent on the other side of the link.

⚠️ **A departure renumbers every slot above it**, and anything that NAMES a
player has to move with it — `CLAUDE.md`'s oldest trap, in a new place. Two
tables here name players: which slot a join was assigned, and which node owns
each slot. Left alone, both point one place too high the moment somebody leaves.
Names are keyed by issuer and counter for the same reason, and resolved to a
slot through the assignment.

### A gate that could not fail

The first version of the test for this asserted that a player still existed
after a rewind through their join tick. Mutated to apply commands on arrival —
the exact bug the schedule exists to prevent — **it passed**: both arms were
wrong the same way, so nothing distinguished them.

What distinguishes them is the case that actually happens: two nodes told the
same thing at different moments. Even then the digest alone is blind, because a
character that spawns with nobody pressing anything settles to the same resting
place within a few ticks. The gate bites only once it asserts *when* the player
appears, while that is still observable. Mutation score 2 of 6 before, 3 of 7
after.

## Turning it on

⛔ **The mesh is opt-in and a server given no mesh setting is an ordinary
standalone server.** The tick branches on one null field; with nothing
configured it runs `MatchSimulation.Tick` exactly as it always has, and no
rollback code is reachable. `MeshIsOptionalTests` asserts that from one side and
the other 3,500-odd tests assert it from the other, by exercising a standalone
server and never mentioning the mesh.

| Flag | Config key | Effect |
|---|---|---|
| `--mesh-listen <port>` | `[Mesh] ListenPort` | wait for a peer; this node is origin 0 |
| `--mesh-connect <host:port>` | `[Mesh] Connect` | join a peer; this node is origin 1 |
| `--no-mesh` | `[Mesh] Enabled` | off, whatever else was set |

Which end listened decides a node's ORIGIN — the only asymmetry two nodes have
for free. Anything else is a thing an operator can get wrong on one machine, and
two nodes that both think they own slot 4 would each be guessing at a player the
other is driving.

⚠️ **Ownership is not a slot parity.** A slot belongs to whichever node's
`AddPlayer` created it, which `MeshNode.OriginOf` records and `MeshSession.Owns`
reads. It cannot be a parity because a join takes the lowest free place and a
departure renumbers everything above it.

⚠️ **In a mesh, the NODE owns the world.** The server's own `_world` becomes a
per-tick copy of the node's, so the 140-odd places that read it go on reading
it. The corollary is the limit below: anything that changes the world between
ticks is overwritten by that copy.

### A client joining a meshed server

A roster place is not a meshed server's to give. The join path schedules
`AddPlayer` and parks the connection; nothing about that player reaches the wire
until both nodes have applied the command at the agreed tick — a third of a
second, against the seconds a GG2 join already takes. `MirrorMeshRoster` then
brings the roster into line with the world: a parked client of ours completes
the ordinary join sequence, and somebody who joined at the far end is added with
no socket, which is the shape a bot already has.

⛔ **The roster and the world must agree slot for slot.** A state update's
player count comes from the ROSTER and each block from the world, so a roster
longer than the world asks for a character that does not exist. The mirror runs
before the update is written, so within one tick the three agree.

⛔ **Two nodes must agree what tick it is**, and the session had no handshake
until a client tried to join: the server scheduled ten ticks ahead of its OWN
count, its peer was already past that, and every command was refused as too
late. The listener names its tick and the joiner adopts it by rebuilding its
node at that number.

⚠️ **The link is serviced FIRST in a tick, always** — that call is where a
listener ACCEPTS. Checking for a connection before servicing deadlocks: measured
as a server sitting for 200 ticks with a peer waiting on a socket it had never
accepted.

### Playing, not just joining

A team or class change mutates the world outside the tick, so in a mesh it is a
scheduled command like a join. Without that a client could reach the roster and
then have every team change silently reverted by the next copy from the node —
worse than being refused, because it looks like it works.

⛔ **A mesh command is the REAL player command, not a field write.** A team
change drops the intel, kills the character and arms a respawn, and both nodes
must do all of it from the same state at the same tick. A node that only set the
team field would be carrying an intel the other had dropped.

Each server does the presentation half for its own clients: its roster copy and
its own `PLAYER_CHANGETEAM`. That split is what lets a change that originated at
the PEER reach this node's clients at all.

⚠️ **Drained by tick, once, behind a high-water mark.** A rewind re-applies a
tick's commands — that is what a schedule is for — so anything broadcasting per
application would re-send the change on every replay.

⚠️ **A meshed team change does not rebalance.** It runs
`PlayerCommands.ChangeTeam`, not the balancing variant. Both nodes agree, which
is what the mesh needs, but it differs from a standalone server.

### Bots

A bot takes the same route a person does — the simulation never special-cases
one, and a mesh is one more place that has to hold. `AddBot` schedules
`AddPlayer` and parks the bot; when the slot lands, the roster half happens
locally and `SetBot`, `ChangeTeam` and `ChangeClass` are scheduled into the
node.

⛔ **`AddBot` returns `GameServer.MeshPending` in a mesh**, because there is no
slot to return: a caller that used the return value would be naming slot -1.
Post-add work goes in the `onAdded` callback, which runs inline standalone and
some ticks later meshed, so one piece of calling code serves both.

⚠️ **`ChangeTeam`, not `SetTeam`.** The real player command arms the respawn
that puts a character in the world, which is what a standalone `AddBot` does
with `RespawnTimer = 1`. Setting the field leaves a bot standing nowhere for
ever.

⚠️ **A meshed bot gets no `checkClasslimits`, and that is forced.**
`Classlimits.Pick` draws from `WorldState.Random` — the observable stream every
client re-derives weapon spreads from — so a node that ran it where its peer did
not would put the two streams in different places and break the mesh outright.
Running it inside the node instead would work and is not done; a meshed bot
takes the class it asked for.

⚠️ **`SetBot` is meshed rather than written locally** because the world is
copied from the node every tick, so a flag written outside the node lasts
exactly one tick. Meshing it also stops the peer's bots reading this node's bots
as people — `Handover.ShouldEngage` under `EngageOpponents.HumansOnly`.

### The order within a tick

`MeshCommandKind`'s VALUES are the apply order, not the order the members are
declared in: `CompareTo` compares the numbers. A removal renumbers every slot
above it, so it sorts last — a value below `ChangeTeam` puts the removal first
and hands the team change to whoever shifted down into the slot. Both nodes
agree either way, so no digest goes red and nothing diverges; it is simply the
wrong player. `MeshCommandTests.ARemovalIsAppliedAfterEverythingElseInItsTick`.

### A departure

A departure is a scheduled command like a join, and the roster place goes when
it lands rather than when the socket does. A disconnecting client's connection
is dropped at once — nothing can be done about that — and their player stands
there pressing nothing for the ten-odd ticks until both nodes remove them at the
same tick.

⛔ **A departure is named by the JOIN that created the place, never by the
place.** A removal renumbers every slot above it, so two removals in flight at
once — one from each node, which is the ordinary case — means the second was
numbered against a roster the first has already changed. **Nothing about that
shows up as a divergence**: both nodes compute the same wrong answer, agree
with each other, and the digests match. `MeshCommand.RemovePlayer(origin,
sequence)` resolves to a slot at apply time, on both nodes, from the same state.
Ordering removals by descending slot fixes the same-tick case and not the
cross-tick one, which is why the naming is the fix rather than the sort.

A consequence worth having: a removal said twice resolves to nothing the second
time, so a departure is safe to repeat.

⛔ **The node reports which slot each removal took** — `MeshNode.RemovedAt(tick)`,
kept in the same ring the schedule uses and rewritten whenever the tick is
applied again. A server cannot work it out afterwards: the command names a join,
applying it deleted that join's entry, and the slot has been renumbered away.

⚠️ **The roster is never trimmed from the end.** Taking the last entry when the
world shrinks leaves the counts agreeing while every entry above the hole names
the wrong person — corruption a state update cannot show, because it is the
right length. `MeshJoinTests.AMeshedBotLeavesAndTakesItsOwnPlaceWithIt` checks
the survivors by name for that reason.

⚠️ **A meshed departure does not rebalance the teams**, where a standalone one
runs `TeamBalance.Check` (`GameServerBeginStep.gml:70`). It writes the world,
and only the node may do that.

## What is NOT built

A client can join a meshed server, pick a team, play and leave; a meshed server
can fill itself with bots and remove them again. What is still missing:

1. **Irreversible events are broadcast provisionally.** The server reads
   `MeshNode.LastEvents`, not `TryTakeConfirmed`, so a death or a capture can be
   published and then unmade by a rewind. The holding layer exists and is
   gated — what it needs is for the broadcast to emit a past tick's events
   during the current tick's, which is a change to the broadcast path rather
   than to the mesh.
2. **Whether a correction is VISIBLE.** The magnitudes are measured and the
   smoothing is written; whether an 8 px nudge reads as a snap or as nothing is
   a question about a person watching a screen, and no measurement here answers
   it. `docs/TRAPS-LIVEGAME.md` and a playtest.
3. **Chat and map changes are not scheduled**, so they have no agreed order
   across nodes. They are rare enough to afford a round trip.

Three differences from a standalone server are deliberate rather than missing: a
meshed team change does not rebalance, a meshed departure does not rebalance
either, and a meshed bot gets no class limits. All three are above, with why —
and all three are the same reason, that only the node may write the world.
