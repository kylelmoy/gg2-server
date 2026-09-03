# gg2-server

A headless [Gang Garrison 2](https://www.ganggarrison.com/) server in C#,
wire-compatible with the stock Game Maker 8 client.

Stock clients connect to it and play a normal game — pick a team and a class,
spawn, fight, capture the intel, see the round end and the map change — with no
modification and no patched client. Underneath, the simulation is a pure,
I/O-free C# port of GG2's physics, weapons and gamemodes, which makes two things
possible that the original server cannot do: **bots with room to think**, and
**matches that run thousands of times faster than real time**.

```powershell
dotnet run --project src/Gg2.Host -- --map ctf_truefort --bots 12
```

That is a full 12-bot game. The bots walk the map, fight over the objective and
cost **0.5% of the 30 Hz tick budget** (Release; see
[caveats](docs/STATUS.md#the-headroom-number-and-the-two-things-always-to-say-about-it)).

---

## Features

- **Wire-compatible** with GG2 v2.9.2 (`PROTOCOL_UUID
  b31c2209-4256-9a19-d0ef-c71c5373bd75`). Framing, handshake, roster indexing and
  state-update layouts all match the reference byte for byte.
- **All ten classes, fully armed** — rockets, mines, healing beams, ubercharge,
  afterburn, the airblast, the sentry gun, the sniper's scope, the bubble screen,
  the spy's cloak and backstab, the sandvich, taunting.
- **All eight gamemodes** — CTF, King of the Hill, Dual KotH, both control-point
  arrangements, Invasion, Arena, Generator and Team Deathmatch — plus the round
  clock, win conditions, the victory screen and the map rotation.
- **Physics proven against the running game**, not by eye: 64 engine-driven
  trajectories reproduced bit for bit, and an 898-tick recording of the reference
  server replayed closed-loop with zero positional error.
- **Bots** — nav graphs, A\*, a path follower with jump and rocket-jump branches,
  targeting, a combat controller, an objective layer and a population manager.
- **A headless match runner** — 44 matches across all 22 maps in 3.3 seconds,
  3,548x real time, so a bot change is evaluated in seconds instead of by
  watching a live server.
- **Batteries included** — the 22 shipped maps and 25 nav graphs are checked in.
  A fresh clone serves a round and runs bots with no arguments and no GG2
  checkout.

## Requirements

- **.NET 10 SDK** — that is all, to build and run the server.
- A **Gang Garrison 2 client** if you want to connect to it (any stock build).
- A **GG2 source checkout** only if you want to run the test suite; see
  [Development](#development).

## Quick start

```powershell
git clone https://github.com/kylelmoy/gg2-server.git
cd gg2-server

# Serve ctf_truefort on the default port 8190.
dotnet run --project src/Gg2.Host

# ...with twelve players' worth of bots.
dotnet run --project src/Gg2.Host -- --map koth_valley --bots 12

# Play bot matches across every map as fast as the CPU allows.
dotnet run -c Release --project src/Gg2.Host -- eval --matches 4
```

Then point a GG2 client at `127.0.0.1:8190` (Join Game → Direct Connect).

## Usage

`gg2-server --help` prints the full list with the caveats attached. The options
you are most likely to want:

| Option | Default | What |
|---|---|---|
| `--port <n>` | `8190` | Listen port. |
| `--map <name>` | `ctf_truefort` | Built-in map to serve; the rotation takes over from there. |
| `--rotation <list\|path>` | | Map rotation: comma-separated names, or a rotation file. Without `--map`, the server opens on its first entry. |
| `--shuffle <mode>` | `none` | `arena-last` shuffles but will not open on an arena map; `any` takes whatever comes out. |
| `--config <path>` | | Read a reference-format `gg2.ini`. Applied **before** every flag, so a flag always wins. Never written back. |
| `--name <text>` | | Server name shown to clients. |
| `--welcome <text>` | | Message sent to each joiner. |
| `--players <n>` | `10` | Seats for clients, 1–48. |
| `--password <text>` | | Require a password to join. |
| `--time-limit <n>` | `15` | Round clock in minutes. |
| `--cap-limit <n>` | `5` | Captures to win. |
| `--kill-limit <n>` | `30` | Kills to win a deathmatch round. |
| `--tdm-invuln <n>` | `5` | Seconds of team-deathmatch spawn protection. `0` is none. |
| `--class-limit <n\|list>` | unlimited | One limit for every class, or per class as `medic=1,spy=0`. **Per team**, not per server; `255` is unlimited and `0` bans. |
| `--no-autobalance` | on | Stop moving a player across when one team is two larger. |
| `--bots <n>` | `0` | **Fill target including humans**, not a bot count — a server filling to 8 with 3 people on it runs 5 bots. |
| `--bot-difficulty <1-5>` | `5` | How good they are. The lower tiers are a handicap ladder — aim error, an acquire delay, a hold-fire roll — so pass a lower one for beatable bots. |
| `--lobby` | off | List the server publicly at ganggarrison.com so it appears in the client's browser. |
| `--tick-stats` | off | Log a histogram of tick intervals every 30s. Safe with clients connected. |

Maps and nav graphs are found automatically; `--maps`, `--botnav`, `GG2_MAPS`
and `GG2_NAVCACHE` override them.

⚠️ **With no rotation the server replays `--map` for ever**, which is not what a
stock dedicated server does — that plays the twenty-map `[Maps]` rotation.
`--config gg2.ini` reproduces it, `--rotation` states one explicitly.

### Advanced bot controllers

`--bot-mode` picks which of the four shipped brains the bots run. Each of the
three non-scripted ones replaces only the bot's keys and aim, and only inside its
engagement window — the scripted layer still owns movement, routing and
objectives.

| `--bot-mode` | | What |
|---|---|---|
| `scripted` | `0` | The scripted layer alone. A complete bot, and the control every comparison is made against. |
| `search` | `1` | + a rollout search. Better on death share, and much more expensive. ⚠️ Never yet measured with humans in the server. |
| `policy` | `2` | + a trained checkpoint. Needs `--policy <path>`. |
| `tiered` | `3` | Both, most expensive first, with an arbiter choosing between them when several bots engage on the same tick. Needs `--policy`; defaults `--bot-arbiter` to 10000. |

`--bot-arbiter <microseconds>` is a per-tick budget shared by every bot. ⚠️ Its
default is derived from measured per-decision costs but has never itself been
tuned — a stated guess, and the constant records what would show it wrong.
[`docs/BOTS.md`](docs/BOTS.md) is the front door for all of it.

⚠️ The only checkpoint that exists was trained against a simulation this build no
longer is, so it lives here as a **test fixture** rather than as something to
run; the loader prints both simulation stamps and refuses the mismatch unless
told otherwise. See [`docs/ENV.md`](docs/ENV.md).

### Offline tooling

Four switches turn the server into a laboratory. **All are off by default, and
the first two must stay off wherever a client is connected** — clients pace
themselves at 30 Hz and re-simulate between updates, so an unpaced server
desynchronises every one of them at once.

| Switch | What |
|---|---|
| `--speed <n\|max>` | Run at n times real time, or unpaced. Pacing only: every duration in the game is counted in ticks, so the simulation is identical at any speed. |
| `--control-port <n>` | A line-of-JSON control surface on 127.0.0.1 only — read state, freeze, step N ticks, change map, teleport, set the bot count. It executes commands against a live game. |
| `--record <dir>` | Write replayable `.trace` recordings of the broadcast stream. Contains every player's keys and aim on every tick, so it is training data as well as a parity fixture. |
| `eval` | A subcommand, not a switch: plays bot matches across maps and reports the results. `gg2-server eval --help`. |

## Other executables

| Command | What |
|---|---|
| `dotnet run --project src/Gg2.Parity` | `gg2-parity` — record a server's broadcast stream, `census` it, `diff` two recordings byte for byte, or `replay` a trace against the port with a drift report. This is how the port is proven correct; see [`docs/PARITY.md`](docs/PARITY.md). |
| `dotnet run --project src/Gg2.Env` | `gg2-env` — the batch-stepping surface a learned policy trains against. Loopback only, one client. See [`docs/ENV.md`](docs/ENV.md). |
| `dotnet run --project tools/bench` | The measurement instrument: per-map criterion, engagement-range sweep, policy cost. **Not in `Gg2.sln`** — build it explicitly, and read [`tools/bench/README.md`](tools/bench/README.md) before reading a number out of it. |

## Repository layout

| Project | What |
|---|---|
| `src/Gg2.Protocol` | The constant table, wire types, the little-endian buffer reader/writer, wire quantisation, command framing, `PlayerInput`. |
| `src/Gg2.Maps` | Map PNG level-data extraction, the walkmask decoder, GGON, the entity list. |
| `src/Gg2.Simulation` | `WorldState`, collision, the movement solver, the character update, weapons and gamemodes. **Pure — no sockets, no clock, no files**, enforced by project references. |
| `src/Gg2.Bots` | Nav, A\*, the path follower, targeting, combat, objectives, population. A bot is an `IInputSource` like a socket; the simulation never special-cases one. |
| `src/Gg2.Server` | Sockets, the join handshake, the roster, the 30 Hz broadcast, the tick that drives the simulation. The only I/O. |
| `src/Gg2.Host` | `gg2-server`, the console entry point, and the `eval` subcommand. |
| `src/Gg2.Parity` | `gg2-parity`: a stream recorder, a byte diff, and a trace replayer with drift reporting. |
| `src/Gg2.Env` | `gg2-env`: the batch-stepping environment seam for policy training. |

| Directory | What |
|---|---|
| `maps/` | The 22 shipped maps plus `gg_debug2`, a port-authored debug range. A byte-identical copy of the reference's, gated by `MapCopyTests`. |
| `nav/` | The 25 bot nav graphs (34 MB on disk, ~1.2 MB packed). 188 tests need them. |
| `tests/Gg2.Tests` | xunit. Parity fixtures live here and run in CI. |
| `tools/` | Engine samplers, fixture generators and the benchmark harness. |
| `docs/` | Everything below. |

## Development

```powershell
dotnet test Gg2.sln
```

The test suite needs a **stock GG2 source checkout** for `Constants.xml` and the
scripts — only the maps and nav graphs are decoupled. It defaults to
`D:\code\Gang-Garrison-2-upstream`; override with `GG2_REFERENCE`.

Two rules that are not negotiable, whatever you are changing:

1. **Compatible on the wire and in observable simulation. Free everywhere else.**
   The stock client re-simulates the physics itself between position updates, so
   trajectories, byte layouts, update cadence and roster indexing must match.
   Nothing else does — objects, events, `with()` loops, alarms, GM8 data
   structures and per-frame recomputation are Game Maker showing through and
   should be replaced with whatever is fastest and clearest in C#. **Do not
   transcribe GML structure; transcribe GML arithmetic.**
2. **Never copy GML into this repo, and never modify the reference trees.** Cite
   the reference by path ([`docs/SOURCEMAP.md`](docs/SOURCEMAP.md) is the index)
   and write C#.

`CLAUDE.md` is the operating guide for anyone — human or agent — writing code
here, and it is worth reading first.

## Documentation

| Doc | What it is |
|---|---|
| [`docs/STATUS.md`](docs/STATUS.md) | **What is built and what proves it**, phase by phase, with the measurements and their caveats. |
| [`docs/PROTOCOL.md`](docs/PROTOCOL.md) | The wire protocol: framing, handshake, roster indexing, state-update layouts. |
| [`docs/SIMULATION.md`](docs/SIMULATION.md) | The determinism contract — what must match, what is free, the distilled physics, the float rules, the GM8 traps. **The document that decides whether the project works.** |
| [`docs/PARITY.md`](docs/PARITY.md) | How the port is proven to match the reference. |
| [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) | The C# structure, shaped for performance and testability, and where a tick actually goes. |
| [`docs/PLAN.md`](docs/PLAN.md) | The phased implementation plan and its exit criteria. |
| [`docs/BOTS.md`](docs/BOTS.md) | **Start here for bots**: the rules, what exists, and how to measure a bot change. |
| [`docs/BOTAI.md`](docs/BOTAI.md) | Structural constraints that are cheap now and impossible to retrofit. |
| [`docs/ENV.md`](docs/ENV.md) | The `Gg2.Env` seam, the policy checkpoint format and what the phase-9 numbers do and do not license. |
| [`docs/SOURCEMAP.md`](docs/SOURCEMAP.md) | Where everything lives in the reference tree. |
| [`docs/DEBUG-MAPS.md`](docs/DEBUG-MAPS.md) | `gg_debug2`, the port-authored debug range. |
| [`docs/PARALLEL.md`](docs/PARALLEL.md) | Lane ownership, if more than one session is working at once. |
| `docs/TRAPS-*.md` | The expensive part: facts measured once against the running game, one section per port stage, for [simulation](docs/TRAPS-SIMULATION.md), [bots](docs/TRAPS-BOTS.md) and [the live game](docs/TRAPS-LIVEGAME.md). **Grep these before writing.** |

## Related repositories

| Repo | What |
|---|---|
| `Gang-Garrison-2-upstream` | **The reference implementation.** Stock GG2 v2.9.2, cloned from [`Gang-Garrison-2/Gang-Garrison-2`](https://github.com/Gang-Garrison-2/Gang-Garrison-2). What `GG2_REFERENCE` defaults to. Read-only from here. |
| `Gang-Garrison-2` | A bot fork of the above, and the checkout the live game is built from. **Not the specification** — this port's bots have diverged from its. Read-only from here. |
| `gg2-agent` | Tooling for the GM8 game: build scripts, the agent bridge, the MCP server, nav analysis. |
| `gg2-nav-gen` | Offline bot nav-graph generator; its output is what `nav/` holds. |
| `gg2-rl` | Bot-policy trainer, on the other side of the `gg2-env` seam. |

## Reference version

GG2 v2.9.2, `VERSION = 29200`, `PROTOCOL_UUID =
b31c2209-4256-9a19-d0ef-c71c5373bd75`. Every fact in `docs/` was read out of that
tree on 2026-08-24; if the reference moves, the protocol docs need re-checking
against it.
