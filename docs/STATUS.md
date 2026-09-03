# What is built, and what proves it

The phase-by-phase record: what each phase of [`PLAN.md`](PLAN.md) landed, and
the evidence that says so. `PLAN.md` is the plan and the exit criteria,
[`PARITY.md`](PARITY.md) is the method, and this file is the result.

⚠️ **This is a record, not a log.** It carries the claims a reader might act on —
"the trace replays with zero error", "the bots cost half a percent of a tick" —
and each should say what would show it has gone stale. It was moved out of
`README.md` on 2026-09-02, where it had grown into the whole document.

**Phases 0 to 8 are done and every exit criterion is met. Phase 9 — a learned
policy — is in progress.**

| Phase | What landed | State |
|---|---|---|
| 0–4 | protocol, maps, a joinable server, the parity harness, collision, character physics | exact against the running game |
| 5 | every weapon, projectile, the sentry, the airblast | nine oracles measured; an 898-tick trace replays closed-loop with zero error |
| 6 | all eight gamemodes, round after round, every `FULL_UPDATE` field real | closed — a stock client has played a round start to finish |
| 7 | bots: the input seam, nav, A\*, the follower, combat, the objective layer, the population manager | closed, with a number |
| 8 | unpaced ticking, the human-play recorder, the headless match runner, the control port | closed — 44 matches across all 22 maps in 3.3s, 3,548x real time |
| 9 | a learned policy: `NpyFile`, `PolicyNetwork`, `HybridBot`, the `Gg2.Env` seam, `tools/bench/` | in progress — see below |

✅ **Every class is implemented.** All ten are armed: rockets, mines, healing
beams, ubercharge, afterburn, the airblast, the sentry gun, the sniper's scope,
Quote's bubble screen, the spy's cloak and backstab, the sandvich, and taunting.

---

## Phase 0 — foundations

All 22 built-in maps decode **cell-for-cell** against `gg2-agent/tools/walkmask.js`,
the decoder that has itself been checked against the running game.

## Phase 1 — a server a client can join

A stock GM8 client joins this server and reaches the team-select screen,
indistinguishably from one joining the reference. Recording both servers'
broadcast streams with `gg2-parity` and diffing them gives 569 bytes each,
**byte-identical**. (For a long time they differed by four bytes of CTF round
timer — a value the port had no simulation to produce. Phase 6's round closed
that.)

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

✅ **Every collision box in the project is measured against the running game** as
of 2026-08-27. ⚠️ Three of the briefcase's four edges were invisible to every
checked-in trace: **a green fixture is not confirmation of a box.**

## Phase 4 — character physics

Exact on both kinds of evidence. Against the running engine, 64 trajectories
driven inside the game itself and read back to the last bit are reproduced **bit
for bit**. Against a recording, the 898-tick two-bot `ctf_truefort` trace replays
**closed-loop — anchored once at tick 0 and never corrected — with zero error on
both axes**, for all 898 ticks including its two deaths. There are no nudges or
tolerances in that statement.

Getting there turned up three things GM8 does that no amount of careful
transcription would have predicted: it parses some decimal literals one ULP above
the IEEE nearest (and they are the physics constants), its comparisons are fuzzy
to `1e-13`, and subtracting `hspeed` and adding it back is not a no-op. The last
wire unit of drift was none of those — it was **where a replayed character
starts**. A `PLAYER_SPAWN` carries a spawn point id rather than a position, so
resolving it against the map the way the client does starts the character on an
exact integer instead of inside a fifth-of-a-pixel bin.

A stock GM8 client confirms it: pointed at this server it joins, renders the whole
CTF HUD, spawns at a real spawn point and walks around with no correction.
Sampling the client's own position every frame against a wall shows a 0.094px
wobble at the update cadence — which is the wire's `x*5` quantisation, not a
disagreement. The server's recorded state replayed offline reproduces the client's
trajectory bit for bit.

## Phase 5 — combat

Every class's weapon, projectiles, blast damage, fire, the airblast, death and its
attribution, and the Engineer's sentry gun. The sharpest recording — six bots
fighting on `ctf_truefort` — replays closed-loop with **exact damage for 1,204
ticks** (forty seconds) and exact positions for 357. Both of the points where the
exact window ends have been traced to a cause and neither is a bug: one is a
position landing 0.0003px from a rounding boundary, the other a wall stopping a
character 0.0087px early. [`PARITY.md`](PARITY.md) has both.

## Phase 6 — game modes and rotation

All eight: Capture the Flag (carrying, dropping, returning, capturing, the score),
King of the Hill, Dual King of the Hill, both control-point arrangements,
Invasion, Arena, Generator and Team Deathmatch — plus the round itself (clock, win
conditions, the victory screen, the map rotation), respawn, class and team
changes, the scoreboard, the domination table, autobalance and the class limits.
Every gamemode has a recorded fixture.

Implementing the modes that reach them turned up **four latent wire bugs**, each a
message whose length depends on its own content and each invisible to every
recording: the sentry's `INPUTSTATE` block, `ARENA_ENDROUND`, the generator HUD
block and `BALANCE`. Any one of them would have desynchronised a real client the
first time the game reached that state.

The server runs the simulation: pick a team and you get a character at a real
spawn point, advanced every tick and serialised to every client. The tick and the
wire conversion have exactly one implementation each, shared with the parity
replayer — two copies would let a recording pass every gate while the server
shipped different bytes.

## Phase 7 — bots

The nav format and A\*, the path follower with its jump and rocket-jump branches,
targeting and aim, the combat controller, the objective layer and the population
manager. This was seeded by porting all 7,202 lines of a GG2 *fork's* bot layer;
stock GG2 has none, and the two have since diverged, so the bots here are judged
by measurement rather than by correspondence ([`BOTS.md`](BOTS.md)).

⚠️ **The reference's bot layer is treated as INCOMPLETE rather than as a
specification**, unlike every other phase. A bot's decisions are this project's
own; the wire and the observable simulation are not.

### The headroom number, and the two things always to say about it

✅ **Twelve bots that walk, fight and play the objective cost 0.151ms of a 33.3ms
tick — 0.5% of the budget**, measured 2026-08-27 over 600 steady-state ticks of a
real server with the whole simulation, the state serialiser and the broadcast in
the loop. That is phase 7's exit criterion and the project's justification.

⚠️ **Always say which build.** That is a **Release** figure. The same test in
Debug reads **0.307ms, 0.9%** — twice the cost, and a re-measure that forgets to
name its build looks exactly like a factor-of-two regression. A stale 0.20ms sat
in four documents without one.

⚠️ **What is measured is this port's cost, not the ratio to GM8.** Nobody has
benchmarked the reference's bot layer here, so "one to two orders of magnitude
better" is a design intention. What the number establishes is that a bot can be
made roughly two orders of magnitude heavier before the tick is at risk.

## Phase 8 — the fast loop

Unpaced ticking, a human-play recorder, a headless match runner and a loopback
control port. **44 matches across all 22 maps in 3.3 seconds, 3,548x real time.**

⚠️ All four switches are off by default, and `--speed` and `--control-port` must
stay off wherever a client is connected — clients pace themselves at 30 Hz and
re-simulate between updates, so an unpaced server desynchronises every one at
once.

⚠️ **A match that ends is not a match that happened.** `eval` reported broken
Arena matches as wins for weeks, because five instant rounds reach a cap limit
like five real ones. `MatchActuallyHappensTests` makes that mechanical; any
"N completed" criterion without it is satisfiable by a broken feature.

## Phase 9 — a learned policy (in progress)

`PolicyNetwork` runs a checkpoint trained by the sibling `gg2-rl` project in this
process, gated logit-for-logit against that project's own numpy on 64 real states;
`HybridBot` hands it the keys and aim inside a 150px engagement window while the
scripted layer keeps movement, routing and objectives.

⛔ **The result nobody predicted: the policy is most valuable used sparingly.** At
150px it holds about **7% of ticks** and beats the all-scripted bot — better death
share on **21 of 21 maps** over 400 matches a map, sign test p = 4.8e-7. Widen the
window and the combat gain keeps rising while the objective collapses faster: at
450px on one map the policy wins **4 rounds to 133**.

⚠️ **That claim is about one checkpoint, not about the method.** It licenses "the
shipped artefact beats the scripted control" and not "a policy trained this way
beats scripted" — training variance across seeds is larger than the effect.
[`ENV.md`](ENV.md), "Phase 4 re-measured", has both tables, the scope statement
and the two layers of censoring that made every earlier version of these numbers
wrong.

⛔ **All of that describes `SIM_UUID 7ff5940d97aa7bed`, and this build is
`5dac8a5e7c37ce3a`** — the 2026-09-02 carry invalidated the only checkpoint that
exists. **Treat every phase-4 behavioural number above as history**; re-measuring
one needs a checkpoint trained against this head. The suite does not wait for
that: the checkpoint is a test **fixture** now (real weights, a numpy oracle for
the forward pass), and nothing here depends on `gg2-rl` doing anything.
[`ENV.md`](ENV.md), "The checkpoint is a FIXTURE now"; [`BOTS.md`](BOTS.md), "a
policy is DATED by the physics".

---

## Why the project exists

1. **Performance headroom for much heavier bot logic.** The bot layer this port
   was seeded from is 7,202 lines of GML interpreted by Game Maker 8, running for
   every bot every tick. That ceiling is the binding constraint on how good the
   bots can get. The headroom number above says the budget is there.
2. **A faster loop for writing and testing that logic.** A pure, I/O-free
   simulation can run thousands of ticks with no game, no rendering and no real
   time elapsing — so a bot change is evaluated in seconds across every map,
   instead of by watching a live server.

Wire compatibility is a hard requirement throughout: stock clients must be able to
connect and play without modification.

## What is not gated

⚠️ **The parity fixtures are all recordings of the reference's own bots**, and no
recording covers the gamemode work end to end. It is gated by the reference and by
purpose-built tests instead; a recording on a control-point map is still worth
capturing.

⚠️ **If a number is quoted as a measurement anywhere in this repo, ask what would
go red if it doubled.** On this project's evidence the answer was usually
"nothing" — and two of the three pinned end-to-end measurements were hiding an
error when they were finally gated.
