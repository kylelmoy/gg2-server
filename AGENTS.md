# AGENTS.md

Orientation for coding agents and new contributors. The rules themselves are in
[`CONTRIBUTING.md`](CONTRIBUTING.md); read it before changing anything.

## What this is

A headless Gang Garrison 2 server in C#, wire-compatible with the stock Game
Maker 8 client, with bots. The client re-simulates physics on its own side, so
the server has to match the reference game exactly on the wire and in anything
a client can observe. Everything else is free to be ordinary, fast C#.

## Build and test

Requires the .NET 10 SDK.

```sh
dotnet test GG2.sln
dotnet run --project src/GG2.Host -- --map ctf_truefort --bots 8
```

The first test run clones the stock GG2 source (the reference) once, so it needs
git and network access. Set `GG2_REFERENCE` to use an existing checkout instead.
Judge a run by its exit code, not by piped output.

## Layout

| Path | What |
|---|---|
| `src/` | The server, one project per concern. `GG2.Simulation` is the game itself and does no I/O; `GG2.Host` is the executable. |
| `tests/` | The test suite, including parity fixtures recorded from the real game. |
| `docs/` | Design and reference documents, one file per subject. |
| `maps/`, `nav/`, `checkpoints/` | Shipped maps, bot navigation graphs and trained bot policy nets. |
| `tools/` | Measurement and maintenance tools, not part of `GG2.sln`. |

## Where to start reading

- `docs/ARCHITECTURE.md` for how the projects fit together.
- `docs/SIMULATION.md` and `docs/PARITY.md` before touching anything in the simulation.
- `docs/BOTS.md` before touching the bots.
- The `docs/TRAPS-*.md` files hold measured facts and known pitfalls, indexed by
  heading. Search the relevant section before re-deriving something.

## Ground rules

- Match the reference game's arithmetic, not its structure.
- Physics uses `double` throughout; keep I/O out of `GG2.Simulation`.
- Never copy code from the reference game into this repository; cite it by path.
- Add or update a test with every behaviour change, and fix the doc in the same
  change when the code and a doc disagree.
