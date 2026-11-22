# What is built, and what proves it

The phase-by-phase record: what each phase of [`PLAN.md`](PLAN.md) landed, and
the evidence that says so. `PLAN.md` is the plan and the exit criteria,
[`PARITY.md`](PARITY.md) is the method, and this file is the result.

**This is a record, not a log.** It carries the claims a reader might act on —
"the trace replays with zero error", "the bots cost half a percent of a tick" —
and each should say what would show it has gone stale.

**The port is finished.** Phases 0 to 8 are done and every exit criterion is
met. Phase 9's environment seam moved to `gg2-rl`; what remains here is the
in-process policy runner, and its section below is what has been measured
about it. [`PLAN.md`](PLAN.md) is closed, and the work from here is bug fixes
and new features rather than phases.

| Phase | What landed | State |
|---|---|---|
| 0–4 | protocol, maps, a joinable server, the parity harness, collision, character physics | exact against the running game |
| 5 | every weapon, projectile, the sentry, the airblast | nine oracles measured; an 898-tick trace replays closed-loop with zero error |
| 6 | all eight gamemodes, round after round, every `FULL_UPDATE` field real | closed — a stock client has survived four gamemode changes in one session |
| 7 | bots: the input seam, nav, A\*, the follower, combat, the objective layer, the population manager | closed, with a number |
| 8 | unpaced ticking, the human-play recorder, the headless match runner, the control port | closed — 44 matches across all 22 maps in 3.3s, 3,548x real time |
| 9 | a learned policy: `SimPolicyNetwork`, `HybridBot`, `tools/bench/`; nine gg2-sim `.gg2p` nets ship in `checkpoints/` | the runner is built and gated — see below for what is and is not measured |

**Every class is implemented.** All ten are armed: rockets, mines, healing
beams, ubercharge, afterburn, the airblast, the sentry gun, the sniper's scope,
Quote's bubble screen, the spy's cloak and backstab, the sandvich, and taunting.

## The stock-upstream audit

Checked mechanically against stock upstream GG2: the 61 message ids, the
15-entry client-command table, the 55 entity types `builder_init.gml`
registers, the eight gamemodes, the weapons and projectiles, and each
server-side script group. **Falsification condition: re-run this audit against
upstream after any reference bump.**

| Gap | What it cost | Gate |
|---|---|---|
| Queued jumping was not simulated | a client with the setting on desynced on every landing — observable simulation | `QueueJumpTests`, `SimulationBehaviourVersion` 2 → 3 |
| `CLIENT_SETTINGS` was stored, never rebroadcast | other clients dead-reckoned that player on the wrong jump rule | `QueueJumpTests` |
| `PLAYER_CHANGENAME` truncated instead of kicking, and had no throttle | an unbounded broadcast amplifier on one socket | `NameAndBubbleTests` |
| The April Fools chat-bubble substitution was missing | wrong bubble on one day, a relayed bubble the reference drops on the other 364 | `NameAndBubbleTests` |
| Rewards were answered with silence | nobody's badges, hats or capes rendered | `RewardAuthTests`, behind `--rewards` |
| Every connected stock client died on a gamemode-changing rotation | `StateUpdateWriter` captured `MatchState` once at `Start` and kept writing the previous map's HUD block after a rotation | fixed; see `docs/PARITY.md` |
| Commands pipelined behind `PLAYER_JOIN` were dropped | a client with `Queued Jumping` on had the setting discarded at join, so the server dead-reckoned it on the wrong jump rule for the whole session; a reward request travels the same way | `JoinAndBroadcastTests.KeepsACommandThatArrivedWithThePlayerJoin`; measured live A/B, `docs/PROTOCOL.md` |

**A claim is only as strong as the thing that was actually done.** "A stock
client has played a round start to finish" was true of exactly one round; the
rotation between rounds was broken until this audit found it.

---

## Phase 0 — foundations

All 22 built-in maps decode **cell-for-cell** against the JavaScript walkmask decoder,
the decoder that has itself been checked against the running game.

## Phase 1 — a server a client can join

A stock GM8 client joins this server and reaches the team-select screen,
indistinguishably from one joining the reference. Recording both servers'
broadcast streams with `gg2-parity` and diffing them gives 569 bytes each,
**byte-identical**.

## Phase 2 — the parity harness

Recorder, stream framer, trace format, replayer and drift report all exist. A
30-second recording of the reference server with two bots playing `ctf_truefort`
is checked in as a fixture; replaying it against the empty simulation names the
tick, slot, field and signed delta of every divergence.

## Phase 3 — collision

`PlaceFree` agrees with the running game exactly across 26,496 samples on three
maps: 23,040 `collision_point` probes and 3,456 fractional-position sweeps of
real character boxes. Gates and player walls are in, behind a `CollisionFilter`,
with every bounding box checked against the game. `good_move_contact_solid`,
`move_outside_solid` and `characterHitObstacle` are transcribed.

**Every collision box in the project is measured against the running game.**
Three of the briefcase's four edges were invisible to every checked-in
trace: **a green fixture is not confirmation of a box.**

## Phase 4 — character physics

Exact on both kinds of evidence. Against the running engine, 64 trajectories
driven inside the game itself and read back to the last bit are reproduced **bit
for bit**. Against a recording, the 898-tick two-bot `ctf_truefort` trace replays
**closed-loop — anchored once at tick 0 and never corrected — with zero error on
both axes**, for all 898 ticks including its two deaths.

Three GM8 behaviours transcription alone would not have predicted: some decimal
literals parse one ULP above the IEEE nearest (and they are the physics
constants), comparisons are fuzzy to `1e-13`, and subtracting `hspeed` and
adding it back is not a no-op. The last wire unit of drift was none of those —
it was where a replayed character starts: a `PLAYER_SPAWN` carries a spawn
point id rather than a position, and resolving it against the map the way the
client does starts the character on an exact integer instead of inside a
fifth-of-a-pixel bin.

A stock GM8 client confirms it: pointed at this server it joins, renders the
whole CTF HUD, spawns at a real spawn point and walks around with no
correction. Sampling the client's own position every frame against a wall
shows a 0.094px wobble at the update cadence — the wire's `x*5` quantisation,
not a disagreement.

## Phase 5 — combat

Every class's weapon, projectiles, blast damage, fire, the airblast, death and
its attribution, and the Engineer's sentry gun. The sharpest recording — six
bots fighting on `ctf_truefort` — replays closed-loop with **exact damage for
1,204 ticks** (forty seconds) and exact positions for 357. Both points where
the exact position window ends have been traced to a rounding boundary rather
than a bug; [`PARITY.md`](PARITY.md) has both.

## Phase 6 — game modes and rotation

All eight: Capture the Flag (carrying, dropping, returning, capturing, the
score), King of the Hill, Dual King of the Hill, both control-point
arrangements, Invasion, Arena, Generator and Team Deathmatch — plus the round
itself (clock, win conditions, the victory screen, the map rotation), respawn,
class and team changes, the scoreboard, the domination table, autobalance and
the class limits. Every gamemode has a recorded fixture.

Four latent wire bugs were found by implementing the modes that reach them,
each a message whose length depends on its own content and each invisible to
every recording: the sentry's `INPUTSTATE` block, `ARENA_ENDROUND`, the
generator HUD block and `BALANCE`.

The server runs the simulation: pick a team and you get a character at a real
spawn point, advanced every tick and serialised to every client. The tick and
the wire conversion have exactly one implementation each, shared with the
parity replayer.

## Phase 7 — bots

The nav format and A\*, the path follower with its jump and rocket-jump
branches, targeting and aim, the combat controller, the objective layer and
the population manager. This was seeded by porting a GG2 *fork's* bot layer;
stock GG2 has none, and the two have since diverged, so the bots here are
judged by measurement rather than by correspondence ([`BOTS.md`](BOTS.md)).

**The reference's bot layer is treated as INCOMPLETE rather than as a
specification**, unlike every other phase. A bot's decisions are this
project's own; the wire and the observable simulation are not.

**Twelve bots that walk, fight and play the objective cost 0.151ms of a
33.3ms tick — 0.5% of the budget**, measured over 600 steady-state ticks of a
real server with the whole simulation, the state serialiser and the broadcast
in the loop. That is phase 7's exit criterion and the project's justification.

**Always say which build.** That is a **Release** figure; the same test in
Debug reads **0.307ms, 0.9%** — twice the cost.

**What is measured is this port's cost, not the ratio to GM8.** Nobody has
benchmarked the reference's bot layer here. What the number establishes is
that a bot can be made roughly two orders of magnitude heavier before the tick
is at risk.

## Phase 8 — the fast loop

Unpaced ticking, a human-play recorder, a headless match runner and a
loopback control port. **44 matches across all 22 maps in 3.3 seconds, 3,548x
real time.**

All four switches are off by default, and `--speed` and `--control-port`
must stay off wherever a client is connected — clients pace themselves at 30
Hz and re-simulate between updates, so an unpaced server desynchronises every
one at once.

**A match that ends is not a match that happened.** `eval` reported broken
Arena matches as wins for weeks, because five instant rounds reach a cap limit
like five real ones. `MatchActuallyHappensTests` makes that mechanical; any
"N completed" criterion without it is satisfiable by a broken feature.

## Phase 9 — a learned policy

`SimPolicyNetwork` runs a gg2-sim `.gg2p` net in this process, and `HybridBot`
hands it the keys and aim inside an engagement window while the scripted layer
keeps movement, routing and objectives. `.gg2p` is the only checkpoint format
the server loads. Nine nets ship in `checkpoints/`, one per class, and the
fleet runs them under `HandoverRule=distance`.

What this repository gates is presence and wiring: `ShippedCheckpointTests`
pins each file's md5, loads each as a layout v2 team net, and plays a match in
which the soldier net takes the wheel; `SimPolicyTests` covers the observation,
the action heads and each class's extra head. **How well the nets play is
gg2-sim's claim**, judged against its other nets rather than against the
scripted bots, and nothing here measures it. A `.gg2p` carries no simulation
stamp, so a physics change here does not refuse one; it is the cue to retrain.

---

## Why the project exists

1. **Performance headroom for much heavier bot logic.** The bot layer this
   port was seeded from is 7,202 lines of GML interpreted by Game Maker 8,
   running for every bot every tick. That ceiling is the binding constraint on
   how good the bots can get. The headroom number above says the budget is
   there.
2. **A faster loop for writing and testing that logic.** A pure, I/O-free
   simulation can run thousands of ticks with no game, no rendering and no
   real time elapsing — so a bot change is evaluated in seconds across every
   map, instead of by watching a live server.

Wire compatibility is a hard requirement throughout: stock clients must be
able to connect and play without modification.

## What is not gated

**The parity fixtures are all recordings of the reference's own bots**, and
no recording covers the gamemode work end to end. It is gated by the
reference and by purpose-built tests instead.

**If a number is quoted as a measurement anywhere in this repo, ask what
would go red if it doubled.** On this project's evidence the answer was
usually "nothing" — and two of the three pinned end-to-end measurements were
hiding an error when they were finally gated.
