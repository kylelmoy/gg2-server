# gg2-server

A headless Gang Garrison 2 server in C#, wire-compatible with the stock Game
Maker 8 client.

**Status: documentation only.** No code has been written yet. Everything here is
the specification and plan for an implementation that has not started.

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

## The four repositories

| Repo | What |
|---|---|
| `gg2-server` (this one) | The C# server. Nothing else. |
| `Gang-Garrison-2` | The reference implementation and the client. **Read-only from here.** |
| `gg2-agent` | Tooling for the GM8 game: build scripts, the agent bridge, the MCP server, nav analysis. The parity harness leans on it. |
| `gg2-nav-gen` | Offline bot nav-graph generator. Already outside the game; the port reads its cache. |

Never copy reference GML into this repo. Cite it by path — `SOURCEMAP.md` is the
index — and write C# that does the same thing.

## Reference version

GG2 v2.9.2, `VERSION = 29200`, `PROTOCOL_UUID =
b31c2209-4256-9a19-d0ef-c71c5373bd75`. Every fact in `docs/` was read out of that
tree on 2026-08-24. If the fork moves, the protocol docs need re-checking against
it.
