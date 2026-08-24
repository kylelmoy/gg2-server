# gg2-server

A headless Gang Garrison 2 server in C#, wire-compatible with the stock Game
Maker 8 client.

**Status: phases 0–3 of [`docs/PLAN.md`](docs/PLAN.md) are done; phase 4 is close.**

| Project | What |
|---|---|
| `Gg2.Protocol` | The constant table, the wire types, the little-endian buffer reader/writer, wire quantisation, command framing, `PlayerInput`. |
| `Gg2.Maps` | Map PNG level-data extraction, the walkmask decoder, GGON, the entity list. |
| `Gg2.Server` | Sockets, the join handshake, the roster, the 30 Hz broadcast. The only I/O. |
| `Gg2.Host` | `gg2-server`, the console entry point. |
| `Gg2.Parity` | `gg2-parity`: a stream recorder, a byte diff, and a trace replayer with drift reporting. |
| `Gg2.Simulation` | Collision, the movement solver and the character update. Pure — no sockets, no clock, no files. |

**Phase 0** — all 22 built-in maps decode **cell-for-cell** against
`gg2-agent/tools/walkmask.js`, the decoder that has itself been checked against
the running game.

**Phase 1** — a stock GM8 client joins this server and reaches the team-select
screen, indistinguishably from one joining the reference. Recording both
servers' broadcast streams with `gg2-parity` and diffing them gives 569 bytes
each, **byte-identical apart from four bytes of CTF round timer** — a value this
port has no simulation to produce, not a layout difference.

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

**Phase 4** — the character update, close but not signed off. Replaying a
recording of the reference with two bots, closed-loop and never corrected, the
port tracks it for **218 ticks with horizontal error exactly zero**, then differs
by one wire unit of `y` — a fifth of a world pixel. That last unit is a real
difference and `docs/PLAN.md` records what has been ruled out.

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
   budget.
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
