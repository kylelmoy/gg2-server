# gg2-server

A headless Gang Garrison 2 server in C#, wire-compatible with the stock Game
Maker 8 client.

**Status: phases 0–6 of [`docs/PLAN.md`](docs/PLAN.md) are done. All eight game
modes run end to end, the server plays match after match on its own, and every
field of a state update carries a real value. Phase 7, bots, is next and is the
reason the project exists.**

A stock client connects, picks a team and class, spawns, fights, dies,
respawns, captures the objective, and sees the round end and the map change.
All ten classes are armed — rockets, mines, healing beams, ubercharge,
afterburn, the airblast, the sentry gun, the sniper's scope, Quote's bubble
screen and the spy's cloak and backstab.

| Project | What |
|---|---|
| `Gg2.Protocol` | The constant table, the wire types, the little-endian buffer reader/writer, wire quantisation, command framing, `PlayerInput`. |
| `Gg2.Maps` | Map PNG level-data extraction, the walkmask decoder, GGON, the entity list. |
| `Gg2.Server` | Sockets, the join handshake, the roster, the 30 Hz broadcast, and the tick that drives the simulation. The only I/O. |
| `Gg2.Host` | `gg2-server`, the console entry point. |
| `Gg2.Parity` | `gg2-parity`: a stream recorder, a byte diff, and a trace replayer with drift reporting. |
| `Gg2.Simulation` | `WorldState`, collision, the movement solver and the character update. Pure — no sockets, no clock, no files. |

**Phase 0** — all 22 built-in maps decode **cell-for-cell** against
`gg2-agent/tools/walkmask.js`, the decoder that has itself been checked against
the running game.

**Phase 1** — a stock GM8 client joins this server and reaches the team-select
screen, indistinguishably from one joining the reference. Recording both
servers' broadcast streams with `gg2-parity` and diffing them gives 569 bytes
each, **byte-identical**. (For a long time they differed by four bytes of CTF
round timer — a value the port had no simulation to produce. Phase 6's round
closed that.)

**Phase 2** — the parity harness. Recorder, stream framer, trace format,
replayer and drift report all exist. A 30-second recording of the reference
server with two bots playing `ctf_truefort` is checked in as a fixture; replaying
it against the empty simulation names the tick, slot, field and signed delta of
every divergence.

**Phase 3** — collision. `PlaceFree` agrees with the running game exactly across
26,496 samples on three maps: 23,040 `collision_point` probes and 3,456
fractional-position sweeps of real character boxes. Gates and player walls are
in, behind a `CollisionFilter`, with every bounding box checked against the
game. `good_move_contact_solid`, `move_outside_solid` and `characterHitObstacle`
are transcribed.

**Phase 4** — the character update, and it is exact on both kinds of evidence.
Against the running engine, 48 trajectories driven inside the game itself and
read back to the last bit are reproduced **bit for bit**. Against a recording,
the 898-tick two-bot `ctf_truefort` trace replays **closed-loop — anchored once
at tick 0 and never corrected — with zero error on both axes**, now for all 898
ticks including its two deaths. There are no nudges or tolerances in that
statement.

**Phase 5** — combat. Every class's weapon, projectiles, blast damage, fire,
the airblast, death and its attribution, and the Engineer's sentry gun. The
sharpest recording — six bots fighting on `ctf_truefort` — replays closed-loop
with **exact damage for 1,204 ticks** (forty seconds) and exact positions for
357. Both of the points where the exact window ends have been traced to a cause
and neither is a bug: one is a position landing 0.0003px from a rounding
boundary, the other a wall stopping a character 0.0087px early.
`docs/PARITY.md` has both.

**Phase 6** — game modes. All eight: Capture the Flag (carrying, dropping,
returning, capturing, the score), King of the Hill, Dual King of the Hill, both
control-point arrangements, Invasion, Arena, Generator and Team Deathmatch —
plus the round itself (clock, win conditions, the victory screen, the map
rotation), respawn, class and team changes, the scoreboard, the domination
table, autobalance and the class limits.

Implementing the modes that reach them turned up **four latent wire bugs**, each
a message whose length depends on its own content and each invisible to every
recording: the sentry's `INPUTSTATE` block, `ARENA_ENDROUND`, the generator HUD
block and `BALANCE`. Any one of them would have desynchronised a real client the
first time the game reached that state.

⚠️ No recording covers any of the game-mode work — every fixture in the
project is one CTF map — so it is gated by the reference and by purpose-built
tests, and a recording on a control-point map is the next thing worth
capturing.

Getting there turned up three things GM8 does that no amount of careful
transcription would have predicted: it parses some decimal literals one ULP
above the IEEE nearest (and they are the physics constants), its comparisons are
fuzzy to `1e-13`, and subtracting `hspeed` and adding it back is not a no-op.
The last wire unit of drift, though, was none of those — it was **where a
replayed character starts**. A `PLAYER_SPAWN` carries a spawn point id rather
than a position, so resolving it against the map the way the client does starts
the character on an exact integer instead of inside a fifth-of-a-pixel bin.

The server runs that simulation now: pick a team and you get a character at a
real spawn point, advanced every tick and serialised to every client. The tick
and the wire conversion have exactly one implementation each, shared with the
parity replayer — two copies would let a recording pass every gate while the
server shipped different bytes.

And a stock GM8 client confirms it: pointed at this server it joins, renders the
whole CTF HUD, spawns at a real spawn point and walks around with no correction.
Sampling the client's own position every frame against a wall shows a 0.094px
wobble at the update cadence — which is the wire's `x*5` quantisation, not a
disagreement. The server's recorded state replayed offline reproduces the
client's trajectory bit for bit.

```powershell
$env:PATH = "$env:LOCALAPPDATA\Microsoft\dotnet;$env:PATH"   # .NET 9, user-local
dotnet test Gg2.sln
dotnet run --project src/Gg2.Host -- --map ctf_truefort
```

## Why

1. **Performance headroom for much heavier bot logic.** The existing bot layer is
   ~7,100 lines of GML interpreted by Game Maker 8, running for every bot every
   tick. That ceiling is the binding constraint on how good the bots can get.
   Compiled C# on the same work should leave one to two orders of magnitude of
   budget. ⚠️ **That is a design intention and not yet a measurement** — no
   benchmark has been run, and doing so is phase 7's exit criterion.
2. **A faster loop for writing and testing that logic.** A pure, I/O-free
   simulation can run thousands of ticks with no game, no rendering and no real
   time elapsing — so a bot change is evaluated in seconds across every map,
   instead of by watching a live server.

Wire compatibility is a hard requirement: stock clients must be able to connect
and play without modification.

## The rule this project runs on

**Compatible on the wire and in observable simulation. Free everywhere else.**

The stock client independently re-simulates the physics between position
updates, so the ported simulation must produce trajectories it agrees with. That
is a genuine constraint and `docs/SIMULATION.md` is careful about exactly how
far it reaches.

Everything else — objects, events, `with()` loops, alarms, GM8 data structures,
the automatic-movement dance, server-side RNG — is an artefact of a 2008 IDE and
should be discarded in favour of whatever is fastest and most testable in C#.

## Read these in order

| Doc | What it is |
|---|---|
| [`docs/PROTOCOL.md`](docs/PROTOCOL.md) | The wire protocol, extracted from the reference. Framing, handshake, roster indexing, the state-update layouts. |
| [`docs/SIMULATION.md`](docs/SIMULATION.md) | The determinism contract: what must match, what is free, the distilled physics, the collision model, float rules, GM8 traps. |
| [`docs/PARITY.md`](docs/PARITY.md) | How the port is proven to match. Build this before the physics. |
| [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) | Proposed C# structure, shaped for performance and testability. |
| [`docs/PLAN.md`](docs/PLAN.md) | Phased implementation plan with exit criteria. |
| [`docs/SOURCEMAP.md`](docs/SOURCEMAP.md) | Where everything lives in the reference tree. |
| [`docs/BOTAI.md`](docs/BOTAI.md) | Constraints the port must honour to keep search-based and learned bots possible. Structure only; no ML in this repo. |

## The five repositories

| Repo | What |
|---|---|
| `gg2-server` (this one) | The C# server. Nothing else. |
| `Gang-Garrison-2` | The reference implementation and the client. **Read-only from here.** |
| `gg2-agent` | Tooling for the GM8 game: build scripts, the agent bridge, the MCP server, nav analysis. The parity harness leans on it. |
| `gg2-nav-gen` | Offline bot nav-graph generator. Already outside the game; the port reads its cache. |
| `gg2-rl` | Bot-policy trainer. Documentation only and blocked on this repo; see its `docs/CONTRACT.md` for what it needs from here. |

Never copy reference GML into this repo. Cite it by path — `SOURCEMAP.md` is the
index — and write C# that does the same thing.

## Reference version

GG2 v2.9.2, `VERSION = 29200`, `PROTOCOL_UUID =
b31c2209-4256-9a19-d0ef-c71c5373bd75`. Every fact in `docs/` was read out of that
tree on 2026-08-24. If the fork moves, the protocol docs need re-checking against
it.
